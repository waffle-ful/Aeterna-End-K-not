using System;
using System.IO;
using System.Linq;
using HarmonyLib;
using InnerNet;
using UnityEngine;

namespace EndKnot.Modules;

// 独立した観測レイヤー: heartbeat(状態 + メモリ) と切断/kick イベントを machine-readable な
// EndKnot-Health.log に書き出す。将来の外部ウォッチドッグ(クラッシュ→再起動 / テスト判定)が tail する土台。
// 既存の AutoRehost / EOSReLogin には一切触らない(読み取り専用の観測のみ)。
public static class HealthLog
{
    private const long HeartbeatIntervalSeconds = 5; // 専用ファイルへの heartbeat 間隔
    private const long NormalLogIntervalSeconds = 60; // 通常ログへの要約間隔(普段見る場所・低ノイズ)
    private const long FrameStallThresholdSeconds = 3; // Tick 間の実時間ギャップがこれを超えたらフレームストールとして記録
    private const long MaxFileBytes = 3 * 1024 * 1024; // .prev 退避に失敗した時のサイズ上限
    private const long MaxTimelineBytes = 8 * 1024 * 1024; // Timeline は 8MB で .prev ローテート

    private static bool Inited;

    // 背景スレッド (ConsoleGuard / AsyncConsoleLog) が EnsureInit() の**最初の呼び出し者**に
    // なるのを防ぐための門。EnsureInit は check-then-act で File.Move まで行うため、2スレッドが
    // 同時に入ると片方が IOException を受け、その catch が Utils.ThrowException → EndKnot.Logger
    // (ロック無しの共有 StringBuilder/Dictionary) に落ちて、直そうとしているハングを別の形で
    // 作ってしまう。背景スレッドはこれを見て、メインスレッドが初期化するまで黙って諦める。
    internal static bool IsInitialized => Inited;
    public static string FilePath { get; private set; } // ライブ本体(EndKnot_Logs 直下の固定ファイル)。DumpLog がセッションフォルダへ同梱する時に参照。
    private static string TimelinePath; // 横断セッション時系列ログ(EndKnot-Timeline.log)
    private static long StartTs;
    private static long LastBeatTs;
    private static long LastTickTs; // 直近 Tick の実時間(フレームストール検出用。heartbeat grid とは独立に毎フレーム更新)
    private static int _lastGc0Count, _lastGc2Count; // 直近 Tick 時点の GC 回数 (framestall の GC 帰属計器)

    // --- サブ秒ヒッチ計器 (HITCH): framestall (3s+) に届かない 50ms〜3s のメインスレッド停止を捕まえる ---
    private const long HitchThresholdMs = 50; // フル GC 実測 ~80ms (ManagedCensus gcPauseMs) を確実に拾う閾値
    private const long HitchWindowSeconds = 10; // レート制限窓
    private const int HitchMaxLinesPerWindow = 5; // 窓内の最大行数 (超過分は suppressed 集計)
    private static readonly System.Diagnostics.Stopwatch HitchClock = System.Diagnostics.Stopwatch.StartNew();
    private static long _lastTickMs; // 直近 Tick の ms 精度実時間 (LastTickTs は秒精度なのでヒッチ検出には使えない)
    private static long _lastBoehmUsed; // 直近 Tick 時点の il2cpp (Boehm) ヒープ使用量 (ヒッチの Boehm GC 帰属計器)
    private static long _hitchWindowStartTs;
    private static int _hitchLinesInWindow;
    private static int _hitchSuppressed;
    private static bool _lastFullScreen;
    private static int _lastScreenW, _lastScreenH; // 直近 Tick 時点の画面モード (reschg⇔framestall 相関計器)
    private static long LastNormalLogTs;
    private static string LastState = "?";
    private static System.Diagnostics.Process Proc;
    private static long _gameStartTime;

    // --- 直近送信リングバッファ (zero I/O) ---
    private struct HostActionEntry
    {
        public string Tag;
        public int Len;
        public string Opt;
        public long Ts;
    }

    private static readonly HostActionEntry[] SendRing = new HostActionEntry[16];
    private static int SendRingIndex; // 次の書き込み位置

    // --- 送信タグ別ヒストグラム用の広いリング (BUG-20260706-05 の弁別計器) ---
    // DCTX の直近16本は「キック時に何を送っていたか」しか答えられず、無傷の窓と比較できないため
    // キックの弁別に3回連続で失敗した (量の指標=Reliable burst / CNO burst はどちらも負の対照で棄却済み)。
    // そこで「直近 N 秒に送ったメッセージをタグ別に集計」した同一書式のサマリを、
    // (a) 異常切断時 (DCTAG) と (b) ゲーム中は定期的に (TAGWIN) の両方で吐き、初めて kick 窓 vs 無傷窓の
    // 差分を取れるようにする。ホストローカルのログのみ・送信は一切増えない。
    private const int TagWindowSeconds = 10; // 集計窓 (Utils.TimeStamp が秒精度なので実効 10〜11 秒)
    private const long TagWindowLogIntervalSeconds = 30; // 平常時サンプルの出力間隔
    private static readonly HostActionEntry[] TagRing = new HostActionEntry[1024]; // 約20送信/秒で50秒分
    private static int TagRingIndex;
    private static long _lastTagWindowLogTs;

    // --- phase3 判定層(早期警報)の状態。SESSION 開始(EnsureInit)でリセット ---
    private static long _sessionStartWsMB; // セッション先頭の wsMB(mem 増分の基準)
    private static long _lastNameSent; // 前回 HB 時点の FixedUpdatePatch.NameSent(HB デルタ算出用)
    private static long _lastNameSkip; // 前回 HB 時点の FixedUpdatePatch.NameSkip
    private static int _lastNetResent; // 前回 HB 時点の Hazel MessagesResent(wire 再送ストーム弁別計器 — BUG-20260716-06)
    private static bool _hadDisconnectThisSession; // セッション中に DC 記録があったか(stuck-menu 判定の前提条件)
    private static long _continuousMenuSinceTs; // 非ホスト Menu 状態が連続している開始 t(0=非連続)
    private static long _lastStuckMenuNoteTs;
    private static long _lastMemNoteTs;
    private static long _lastAbnormalDcTs; // 直近の異常切断 t(回復判定の猶予に使用)

    // --- Innersloth UserIDToken 死 + メニュー落ちゾンビの検出 (BUG-20260715-05) ---
    // TokenGrant 401 (外部 JWT 失効) で UserIDToken が失われても接続中のロビーは動き続け、次のロビー遷移で
    // DisconnectPopup も DC イベントも無しにメインメニューへ落ちる (GameState=Ended のまま数時間ゾンビ化)。
    // 検出は2段: ①トークン消失 (計器+フラグのみ、再起動しない) ②Ended スタック+メニュー実在 (=実際に壊れた確定) で初めて再起動。
    private const long TokenDeadSustainSeconds = 120; // トークン null がこの秒数続いたら死と判定 (瞬断除け)
    private const long EndedStuckSeconds = 300; // state=Ended がこの秒数続いたら異常 (正常時は outro の数秒〜数十秒)
    private static bool _sawUserIdToken; // 一度でも非空トークンを観測した (ブート時の未ログイン null と区別するラッチ)
    private static long _tokenNullSinceTs; // トークン null が連続している開始 t (0=非連続)
    private static bool _tokenDeadNoted; // idtokendead ANOM 発行済み (回復で解除)
    private static long _endedSinceTs; // state=Ended が連続している開始 t (0=非連続)
    private static bool _zombieHandled; // menufall エスカレーション発行済み (state が Ended を離れたら解除)
    private static long _lastEndedStuckNoteTs; // endedstuck ANOM のスロットル

    // --- 有人/無人の弁別計器 (BUG-20260721-02: 「ハングは有人操作中のみ」説の機械判定用) ---
    // GetLastInputInfo はこの Windows セッション全体の最終入力 tick を返す。HB に「最終入力からの
    // 経過秒」を載せることで、ハング直前の HB が有人 (数秒) か無人 (数分〜) かを事後に判定できる。
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint cbSize;
        public uint dwTime;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo plii);

    private static long GetInputIdleSeconds()
    {
        if (!OperatingSystem.IsWindows()) return -1;

        try
        {
            var lii = new LastInputInfo { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<LastInputInfo>() };
            if (!GetLastInputInfo(ref lii)) return -1;
            // Environment.TickCount と dwTime は同じ 32bit tick 系。unchecked 減算でラップも正しく差になる。
            return unchecked((uint)Environment.TickCount - lii.dwTime) / 1000;
        }
        catch { return -1; }
    }

    private static void EnsureInit()
    {
        if (Inited) return;
        Inited = true;

        try
        {
            // 実ログと同じ場所に置く: 非 Android は <Desktop>/EndKnot_Logs (Utils.DumpLog の basePath と一致)。
            // セッション毎サブフォルダでなく root に固定ファイル + .prev で置き、将来のウォッチドッグが安定 tail できるように。
            string basePath = OperatingSystem.IsAndroid() ? Main.DataPath : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string dir = Path.Combine(basePath, "EndKnot_Logs");
            Directory.CreateDirectory(dir);
            FilePath = Path.Combine(dir, "EndKnot-Health.log");
            TimelinePath = Path.Combine(dir, "EndKnot-Timeline.log");

            // 前セッションの末尾(クラッシュ直前の heartbeat)を .prev に退避してから新規セッションを開始。
            if (File.Exists(FilePath))
            {
                string prev = Path.Combine(dir, "EndKnot-Health.prev.log");

                try
                {
                    if (File.Exists(prev)) File.Delete(prev);
                    File.Move(FilePath, prev);
                }
                catch
                {
                    try { if (new FileInfo(FilePath).Length > MaxFileBytes) File.Delete(FilePath); }
                    catch { }
                }
            }

            // Timeline は append-only。セッションをまたいで保持し、8MB で .prev ローテートのみ。
            try
            {
                if (File.Exists(TimelinePath) && new FileInfo(TimelinePath).Length > MaxTimelineBytes)
                {
                    string tlPrev = Path.Combine(dir, "EndKnot-Timeline.prev.log");
                    try
                    {
                        if (File.Exists(tlPrev)) File.Delete(tlPrev);
                        File.Move(TimelinePath, tlPrev);
                    }
                    catch { }
                }
            }
            catch { }

            StartTs = Utils.TimeStamp;
            try { Proc = System.Diagnostics.Process.GetCurrentProcess(); }
            catch { }

            _sessionStartWsMB = 0;
            _lastTagWindowLogTs = 0;
            Array.Clear(TagRing, 0, TagRing.Length);
            TagRingIndex = 0;
            _hadDisconnectThisSession = false;
            _continuousMenuSinceTs = 0;
            _lastStuckMenuNoteTs = 0;
            _lastMemNoteTs = 0;
            _sawUserIdToken = false;
            _tokenNullSinceTs = 0;
            _tokenDeadNoted = false;
            _endedSinceTs = 0;
            _zombieHandled = false;
            _lastEndedStuckNoteTs = 0;

            int gcInc = 0;
            try { gcInc = GcUafProbe.IsIncrementalGc() ? 1 : 0; }
            catch { }

            string gcUafState = "none";
            try { gcUafState = GcUafSelfHeal.GetMarkerState(); }
            catch { }

            string sessionLine = $"SESSION start ver={Main.PluginVersion} t={StartTs} gcInc={gcInc} gcUafState={gcUafState}";
            Write(sessionLine);
            Timeline(sessionLine);
        }
        catch (Exception e) { Utils.ThrowException(e); }
    }

    public static void Tick()
    {
        EnsureInit();

        long now = Utils.TimeStamp;

        // 状態遷移(ロビー入った / ゲーム開始 / メニュー戻り)は heartbeat の grid を待たず即記録。
        string state = GetState();

        if (state != LastState)
        {
            Write($"STATE {LastState}->{state} t={now}");
            LastState = state;

            // ロビー復帰毎に 1 回、型別オブジェクト census を残す (per-game 破棄漏れの犯人型特定計器)
            if (state == "Lobby")
                try { MemCensus.ScheduleAfterLobbyEnter(); }
                catch { }
        }

        // フレームストール検出: Tick は毎フレーム呼ばれる。前回 Tick から実時間で大きく空いた =
        // メインスレッド(FixedUpdate)が停止していた証拠。停止直前の送信コンテキストを添えて記録し、
        // 「フォーカス中に起きる真ハングか / フォーカス喪失で消えるか」の切り分け材料にする。
        // 状態遷移(シーンロード)でも数秒空くため state を併記して区別できるようにする。
        // gc0d/gc2d = ストール窓内の GC 回数 (帰属計器): ≥1 なら GC/ヒープ churn 起因の疑い、
        // 0 なら GC 外 (同期 I/O / アセット操作 / native)。ログはストール解除フレームに一斉 flush
        // されるため「ANOM と同秒のログ行」は原因でなく症状 (2026-07-14 の教訓・逆因果に注意)。
        if (LastTickTs != 0 && now - LastTickTs >= FrameStallThresholdSeconds)
            NoteAnom($"ANOM live kind=framestall gapSec={now - LastTickTs} state={state} gc0d={GC.CollectionCount(0) - _lastGc0Count} gc2d={GC.CollectionCount(2) - _lastGc2Count}{GetLastSendSuffix(now)} t={now}");

        // サブ秒ヒッチ (かくつき) 検出: gc0d/gc2d ≥1 なら CoreCLR GC 起因の疑い、boehmDeltaKB が大きく負なら
        // il2cpp (Boehm) GC 起因の疑い、両方動いていなければ GC 外 (同期 I/O / アセット操作 / シーン遷移)。
        // フォーカス喪失 (Alt-Tab) やシーン遷移でも出るため state 併記 + レート制限で洪水を防ぐ。
        // framestall と同じく「HITCH と同秒のログ行」は原因でなく症状 (flush が解除フレームに寄る) — 逆因果に注意。
        long nowMs = HitchClock.ElapsedMilliseconds;
        long boehmNow = GcPrepass.BoehmUsedBytes();

        if (_lastTickMs != 0)
        {
            long gapMs = nowMs - _lastTickMs;

            if (gapMs >= HitchThresholdMs && gapMs < FrameStallThresholdSeconds * 1000)
            {
                if (now - _hitchWindowStartTs >= HitchWindowSeconds)
                {
                    if (_hitchSuppressed > 0) Write($"HITCH suppressed={_hitchSuppressed} t={now}");

                    _hitchWindowStartTs = now;
                    _hitchLinesInWindow = 0;
                    _hitchSuppressed = 0;
                }

                if (_hitchLinesInWindow < HitchMaxLinesPerWindow)
                {
                    _hitchLinesInWindow++;
                    long boehmDeltaKb = _lastBoehmUsed > 0 && boehmNow > 0 ? (boehmNow - _lastBoehmUsed) / 1024 : 0;
                    // ⚠️ Boehm GC 回数は Il2CppInterop ラッパー越しなので raw DllImport (BoehmUsedBytes) より高い。
                    // Tick は FixedUpdateCaller から毎フレーム走るため、ここ (ヒッチ検出時のみ) で取る。
                    // 差分ではなく累計を出し、連続する HITCH 行の差として読む — 毎フレーム標本を持たずに済む。
                    Write($"HITCH gapMs={gapMs} state={state} gc0d={GC.CollectionCount(0) - _lastGc0Count} gc2d={GC.CollectionCount(2) - _lastGc2Count} bgc={GcPrepass.BoehmCollectionCount()} boehmMB={(boehmNow > 0 ? boehmNow / 1048576 : -1)} boehmDeltaKB={boehmDeltaKb} t={now}");
                }
                else
                    _hitchSuppressed++;
            }
        }

        _lastTickMs = nowMs;
        _lastBoehmUsed = boehmNow;

        LastTickTs = now;
        _lastGc0Count = GC.CollectionCount(0);
        _lastGc2Count = GC.CollectionCount(2);

        // フルスクリーン切替/解像度変更の帰属計器 (BUG-20260729-17 系: ユーザー仮説「全画面切替→3-4秒スタッター」の
        // 1-bit 検証用)。切替ストール中は Tick 自体が止まるため、切替検知行は解除フレームで framestall ANOM と
        // 同時に flush される — reschg 行と framestall 行の t= 一致/近接が「切替起因」の判定条件。
        if (Screen.fullScreen != _lastFullScreen || Screen.width != _lastScreenW || Screen.height != _lastScreenH)
        {
            if (_lastScreenW != 0)
                NoteAnom($"ANOM live kind=reschg fs={_lastFullScreen}->{Screen.fullScreen} res={_lastScreenW}x{_lastScreenH}->{Screen.width}x{Screen.height} t={now}");

            _lastFullScreen = Screen.fullScreen;
            _lastScreenW = Screen.width;
            _lastScreenH = Screen.height;
        }

        // 早期警報テレメトリは HB の 5 秒 grid を待たず 1/sec で回す(SnapTo 枯渇・例外洪水はより早い検知が要る)。
        if (PerSecondUpdateScheduler.ShouldRunUpdate("earlywarning-tick"))
        {
            try { EarlyWarning.Tick(); }
            catch (Exception e) { Utils.ThrowException(e); }
        }

        if (now - LastBeatTs < HeartbeatIntervalSeconds) return;
        LastBeatTs = now;

        try
        {
            bool host = false;
            try { host = AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost; }
            catch { }

            string server = "?";
            try { server = GameStates.CurrentServerType.ToString(); }
            catch { }

            int players = 0;
            try { players = GameData.Instance != null ? GameData.Instance.PlayerCount : 0; }
            catch { }

            long gcMB = GC.GetTotalMemory(false) / (1024 * 1024);
            long wsMB = 0;
            int gen2 = 0;
            try { if (Proc != null) { Proc.Refresh(); wsMB = Proc.WorkingSet64 / (1024 * 1024); } }
            catch { }
            try { gen2 = GC.CollectionCount(2); }
            catch { }

            // 直近送信リングから最新エントリを取得して HB に添付
            string lastSendSuffix = GetLastSendSuffix(now);

            // RpcSetName の送信/スキップ回数を前回 HB からのデルタで載せる(dirty-check の効き目計器)。
            long nmSent = 0, nmSkip = 0;
            try
            {
                long s = FixedUpdatePatch.NameSent, k = FixedUpdatePatch.NameSkip;
                nmSent = s - _lastNameSent;
                nmSkip = k - _lastNameSkip;
                _lastNameSent = s;
                _lastNameSkip = k;
            }
            catch { }

            // EOS ログインフローの進行中フラグ (再ログインスタック監視の計器 — 1 が 180 秒続くと不発弾)
            // eosFlow=0 が online 中に続く場合は「再ログインでフローが再スタートしたまま未完了」の直接証拠
            // (BUG-20260711-03 の 1-bit 分離用。正常時は起動ログイン完了後ずっと 1)
            int eosTry = 0;
            int eosFlow = 0;
            try
            {
                eosTry = EOSManager.Instance != null && EOSManager.Instance.tryingToLogin ? 1 : 0;
                eosFlow = EOSManager.Instance != null && EOSManager.Instance.loginFlowFinished ? 1 : 0;
            }
            catch { }

            // Innersloth UserIDToken の生存 (BUG-20260715-05 計器)。TokenGrant 401 で失われると 0 に落ちたまま
            // 戻らず、次のロビー遷移で無音メニュー落ちする。field 直読みなので EOSReLoginPatch の cfg 状態と無関係に生きる。
            int idTok = 0;
            try { idTok = EOSManager.Instance != null && !string.IsNullOrEmpty(EOSManager.Instance.UserIDToken) ? 1 : 0; }
            catch { }

            // Hazel connection の wire 統計 (BUG-20260716-06 計器)。mod 送信層の計測では既知4機序が全て
            // シロだったため、送信層から見えない再送ストーム (回線ヒカップで Reliable 再送が実ワイヤレートを
            // 数倍化) を弁別する。rsndD=前回HBからの再送デルタ / unack=未ACKの Reliable 在庫 / pNoAck=ACK
            // 無しに連続した ping 数 (リンク死の直接signal)。
            int rsndD = 0, unack = 0, pNoAck = 0, ping = 0;
            if (TryGetNetStats(out int rsnd, out int relSent, out int ackd, out pNoAck, out ping))
            {
                rsndD = rsnd - _lastNetResent;
                if (rsndD < 0) rsndD = rsnd; // 接続張り替えでカウンタが 0 から再スタートした
                _lastNetResent = rsnd;
                unack = relSent - ackd;
            }

            string hb = $"t={now} up={now - StartTs} state={state} host={(host ? 1 : 0)} server={server} players={players} wsMB={wsMB} gcMB={gcMB} gc2={gen2} nmSent={nmSent} nmSkip={nmSkip} eosTry={eosTry} eosFlow={eosFlow} idTok={idTok} ping={ping} rsndD={rsndD} unack={unack} pNoAck={pNoAck} inIdle={GetInputIdleSeconds()}{lastSendSuffix}";
            Write($"HB {hb}");

            // マネージド保持リークの帰属計器 (BUG-20260706-01)。間隔判定は MaybeTick 側。
            try { ManagedCensus.MaybeTick(now, state); }
            catch { }

            // 平常時のタグ別送信サマリ。ゲーム中だけ出す (Menu/Lobby は比較対象にならないうえ無駄に嵩む)。
            // これが無いと DCTAG に「比較すべき無傷の窓」が存在せず、また相関止まりの結論になる。
            if ((state == "InTask" || state == "Meeting") && now - _lastTagWindowLogTs >= TagWindowLogIntervalSeconds)
            {
                _lastTagWindowLogTs = now;

                try
                {
                    // nest=[...] は PacketRateGate のリング由来 (tag/leaf tag 別のネスト総数)。BuildTagWindow が
                    // 拾えるのは CustomRpcSender 経由の per-name だけで、人数比例で膨らむ t26 の中身は見えないため。
                    string tagLine = $"TAGWIN state={state} players={players} {BuildTagWindow(now, TagWindowSeconds)} {PacketRateGate.SummarizeRecent(TagWindowSeconds)} t={now}";
                    Write(tagLine);
                    Timeline(tagLine);
                }
                catch (Exception e) { Utils.ThrowException(e); }
            }

            // 普段見る通常ログにもメモリ + 状態の要約を低頻度で(最適化余地の把握用)。
            if (now - LastNormalLogTs >= NormalLogIntervalSeconds)
            {
                LastNormalLogTs = now;
                Logger.Info(hb, "Health");
            }

            // phase3 判定: 非ホストで Menu 状態が長時間連続 + セッション中に DC 記録あり = 復帰失敗の疑い。
            if (state == "Menu" && !host)
            {
                if (_continuousMenuSinceTs == 0) _continuousMenuSinceTs = now;
                long menuDurSec = now - _continuousMenuSinceTs;

                if (menuDurSec >= 120 && _hadDisconnectThisSession && now - _lastStuckMenuNoteTs >= 300)
                {
                    _lastStuckMenuNoteTs = now;
                    NoteAnom($"ANOM live kind=stuckmenu durSec={menuDurSec} t={now}");
                }
            }
            else
            {
                _continuousMenuSinceTs = 0;

                // ロビー/ゲームへ復帰できた = 切断から回復済みとみなし、以後の Menu 滞在を正常系へ戻す。
                // DC 直後の 1 tick に古い state 読みが残っても誤リセットしないよう 15 秒の猶予を置く。
                if (state != "Menu" && _hadDisconnectThisSession && now - _lastAbnormalDcTs > 15)
                    _hadDisconnectThisSession = false;
            }

            // phase3 判定: メモリがセッション先頭比で大きく増えた、または絶対値が高い。
            if (wsMB > 0)
            {
                if (_sessionStartWsMB == 0) _sessionStartWsMB = wsMB;

                long deltaMB = wsMB - _sessionStartWsMB;
                if ((deltaMB > 800 || wsMB > 2200) && now - _lastMemNoteTs >= 300)
                {
                    _lastMemNoteTs = now;
                    NoteAnom($"ANOM live kind=mem ws={wsMB} base={_sessionStartWsMB} t={now}");
                }
            }

            // ── 段1: UserIDToken 死の検出 (BUG-20260715-05)。ここでは再起動しない — 接続中のロビーは
            // トークン無しでも動き続けるため、計器 (ANOM) と AutoRestart へのフラグ通知のみ。
            // 実際の再起動は段2 (実害=メニュー落ち) が確定してから (ユーザー方針: 再起動は最終手段)。
            if (idTok == 1)
            {
                _sawUserIdToken = true;
                _tokenNullSinceTs = 0;

                if (_tokenDeadNoted)
                {
                    _tokenDeadNoted = false;
                    AutoRestart.UserIdTokenDead = false;
                    NoteAnom($"ANOM live kind=eos stage=idtokenrecovered t={now}");
                }
            }
            else if (_sawUserIdToken && eosFlow == 1 && !_tokenDeadNoted)
            {
                if (_tokenNullSinceTs == 0) _tokenNullSinceTs = now;

                if (now - _tokenNullSinceTs >= TokenDeadSustainSeconds)
                {
                    _tokenDeadNoted = true;
                    AutoRestart.UserIdTokenDead = true;
                    NoteAnom($"ANOM live kind=eos stage=idtokendead nullSec={now - _tokenNullSinceTs} t={now}");
                }
            }

            // ── 段2: メニュー落ちゾンビの検出。state=Ended は正常なら outro の数秒〜数十秒で抜ける。
            // これが EndedStuckSeconds 以上続き、かつ MainMenuManager が実在する (=outro でなくメニューへ
            // 落ちている矛盾状態) なら、プロセス内では回復不能 (GameState=Ended 残留で AutoRehost の
            // WaitClean も永久に通らない) と確定 → AutoRestart へエスカレーション。
            // メニュー不在 (本物の outro に人間が座っているだけ等) なら ANOM 記録のみで再起動しない。
            if (state == "Ended")
            {
                if (_endedSinceTs == 0) _endedSinceTs = now;
                long endedDur = now - _endedSinceTs;

                if (endedDur >= EndedStuckSeconds && !_zombieHandled)
                {
                    bool atMenu = false;
                    try { atMenu = UnityEngine.Object.FindObjectOfType<MainMenuManager>() != null; }
                    catch { }

                    if (atMenu)
                    {
                        _zombieHandled = true;
                        NoteAnom($"ANOM live kind=zombie stage=menufall durSec={endedDur} idTok={idTok} t={now}");
                        AutoRestart.OnMainMenuZombie(endedDur);
                    }
                    else if (now - _lastEndedStuckNoteTs >= 300)
                    {
                        _lastEndedStuckNoteTs = now;
                        NoteAnom($"ANOM live kind=zombie stage=endedstuck durSec={endedDur} idTok={idTok} t={now}");
                    }
                }
            }
            else
            {
                _endedSinceTs = 0;
                _zombieHandled = false;
            }
        }
        catch (Exception e) { Utils.ThrowException(e); }
    }

    public static void RecordDisconnect(DisconnectReasons reason, string stringReason)
    {
        EnsureInit();

        try
        {
            bool wasHost = false;
            try { wasHost = AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost; }
            catch { }

            string server = "?";
            try { server = GameStates.CurrentServerType.ToString(); }
            catch { }

            string str = (stringReason ?? string.Empty).Replace("\r", " ").Replace("\n", " ");
            if (str.Length > 200) str = str[..200];

            // intentional = 「異常ではない」= DCTX ダンプ / stuck-menu 判定の対象外。ユーザーの意図的退出に加え、
            // 再ホスト/シーン切替中に正常に起きる接続の張り替え (NewConnection) や、フォーカス喪失、意図的離脱も
            // ここに含める (穴4: これらを異常扱いすると DCTX 大量ダンプ + _hadDisconnectThisSession 誤セットで
            // stuck-menu/rehost を誤発火しうる)。DuplicateConnectionDetected (別所ログイン) は本物の異常なので除外。
            bool intentional = reason is DisconnectReasons.ExitGame or DisconnectReasons.Destroy
                or DisconnectReasons.Banned or DisconnectReasons.IncorrectVersion
                or DisconnectReasons.NewConnection or DisconnectReasons.IntentionalLeaving
                or DisconnectReasons.FocusLost or DisconnectReasons.FocusLostBackground;

            // 認証/サーバー死は「その場では回復不能」なので、AutoRehost の 3×リトライを待たず即プロセス再起動へ (穴1+穴5)。
            try { AutoRestart.OnDisconnect(reason); }
            catch (Exception e) { Utils.ThrowException(e); }

            long now = Utils.TimeStamp;

            if (!intentional)
            {
                // stuck-menu 判定の前提は「異常切断の後の長時間 Menu 滞在」のみ。意図的な退出後のメニュー滞在は正常系。
                _hadDisconnectThisSession = true;
                _lastAbnormalDcTs = now;
            }

            if (!intentional)
            {
                // wire 統計の最終スナップショット (BUG-20260716-06)。再送ストーム説なら resent が直近 HB の
                // rsndD 帯から跳ね、pNoAck が積み上がっているはず。切断後は connection が既に死んでいる
                // ことがあるので取れたときだけ書く。
                if (TryGetNetStats(out int rsnd, out int relSent, out int ackd, out int pNoAck, out int ping))
                    Write($"DCNET resent={rsnd} relSent={relSent} ackd={ackd} unack={relSent - ackd} pNoAck={pNoAck} ping={ping}");

                // 異常切断: リングバッファを DCTX としてダンプ (crash前の最終送信コンテキスト)
                try
                {
                    for (int i = 0; i < SendRing.Length; i++)
                    {
                        int idx = (SendRingIndex + i) % SendRing.Length;
                        ref HostActionEntry e = ref SendRing[idx];
                        if (e.Tag == null) continue;
                        long ageSec = now - e.Ts;
                        Write($"DCTX send=\"{e.Tag}\" len={e.Len} opt={e.Opt} ageSec={ageSec}");
                    }
                }
                catch { }

                // タグ別ヒストグラム: 平常時の TAGWIN と同一書式なので、そのまま無傷窓と比較できる。
                try
                {
                    // TAGWIN と同一書式を保つ (nest=[...] 込み) — そのまま無傷窓と diff できるのが存在理由。
                    string tagLine = $"DCTAG {BuildTagWindow(now, TagWindowSeconds)} {PacketRateGate.SummarizeRecent(TagWindowSeconds)}";
                    Write(tagLine);
                    Timeline(tagLine);
                }
                catch { }

                // log.html のバッファも flush (crash前の詳細ログを保全)
                Logger.FlushNow();
            }

            string line = $"DC reason={reason} intentional={(intentional ? 1 : 0)} wasHost={(wasHost ? 1 : 0)} server={server} t={now} str=\"{str}\"";
            Write(line);
            Timeline(line);

            // kick は通常ログにも目立たせる(診断・将来のウォッチドッグ判定用)。
            if (reason == DisconnectReasons.Hacking)
                Logger.Warn($"HACKING kick detected: {line}", "Health");
            else
                Logger.Info($"disconnect: {line}", "Health");
        }
        catch (Exception e) { Utils.ThrowException(e); }
    }

    /// <summary>
    /// Hazel connection の wire 統計スナップショット (BUG-20260716-06 計器)。読み取り専用・送信ゼロ。
    /// resent=Reliable 再送の累計 / relSent=Reliable 送信累計 / ackd=ACK 済み累計 /
    /// pingsNoAck=ACK を受けずに連続した keepalive ping 数 / ping=AU 報告の RTT(ms)。
    /// connection 未確立・切断済みなどで取れなければ false。
    /// </summary>
    private static bool TryGetNetStats(out int resent, out int relSent, out int ackd, out int pingsNoAck, out int ping)
    {
        resent = relSent = ackd = pingsNoAck = ping = 0;

        try
        {
            AmongUsClient client = AmongUsClient.Instance;
            var conn = client != null ? client.connection : null;
            if (conn == null) return false;

            Hazel.ConnectionStatistics st = conn.Statistics;
            if (st == null) return false;

            resent = st.MessagesResent;
            relSent = st.reliableMessagesSent;
            ackd = st.reliablePacketsAcknowledged;
            ping = client.Ping;

            var udp = conn.TryCast<Hazel.Udp.UdpConnection>();
            if (udp != null) pingsNoAck = udp.pingsSinceAck;

            return true;
        }
        catch { return false; }
    }

    /// <summary>直近送信リングの最新エントリを " lastSend=... lastLen=... lastAgeSec=..." 形式で返す(なければ空文字列)。</summary>
    private static string GetLastSendSuffix(long now)
    {
        try
        {
            // SendRingIndex は「次に書く場所」なので、最新 = (SendRingIndex + 15) % 16
            HostActionEntry latest = SendRing[(SendRingIndex + 15) % 16];
            if (latest.Tag != null)
                return $" lastSend=\"{latest.Tag}\" lastLen={latest.Len} lastAgeSec={now - latest.Ts}";
        }
        catch { }

        return string.Empty;
    }

    /// <summary>ユーザー向けポップアップ/メッセージを捕捉して記録する (MessageCapture から)。
    /// 「ログに拾えないメッセージがある」= 検知の穴を塞ぐための観測窓口。Health + Timeline + log.html の三点に残す。</summary>
    public static void RecordMessage(string source, string text)
    {
        EnsureInit();

        try
        {
            string flat = (text ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            if (flat.Length > 300) flat = flat[..300];

            string line = $"MSG src={source} text=\"{flat}\"";
            Write(line);
            Timeline(line);
            Logger.Info($"[{source}] {flat}", "MessageCapture");
        }
        catch (Exception e) { Utils.ThrowException(e); }
    }

    /// <summary>
    /// 直近 windowSec 秒に送ったメッセージをタグ別に集計した1行サマリを作る。
    /// DCTAG (切断時) と TAGWIN (平常時) で同一書式を使い、kick 窓 vs 無傷窓を直接 diff できるようにする。
    /// 捕捉範囲は CustomRpcSender / Utils.RawRPC 経由のモッド送信のみ (バニラ自身の送信は含まない)。
    /// </summary>
    private static string BuildTagWindow(long now, int windowSec)
    {
        var counts = new System.Collections.Generic.Dictionary<string, (int N, int Bytes)>(32);
        int total = 0, totalBytes = 0, reliable = 0, maxLen = 0;
        long oldest = now;

        for (int i = 0; i < TagRing.Length; i++)
        {
            ref HostActionEntry e = ref TagRing[i];
            if (e.Tag == null || e.Ts > now || now - e.Ts > windowSec) continue;

            if (e.Ts < oldest) oldest = e.Ts;
            total++;
            totalBytes += e.Len;
            if (e.Len > maxLen) maxLen = e.Len;
            if (e.Opt == "Reliable") reliable++;

            counts.TryGetValue(e.Tag, out (int N, int Bytes) cur);
            counts[e.Tag] = (cur.N + 1, cur.Bytes + e.Len);
        }

        string top = string.Join(",", counts.OrderByDescending(x => x.Value.N).Take(8).Select(x => $"{x.Key}:{x.Value.N}/{x.Value.Bytes}"));
        // span < win はリングが1周したサイン = 集計は「窓全体の実数」ではなく下限値。
        // まさに追いたいバースト時ほど飽和しやすいので、飽和の有無を必ず添える。
        return $"win={windowSec}s span={now - oldest}s n={total} b={totalBytes} rel={reliable} maxLen={maxLen} tags={counts.Count} top=[{top}]";
    }

    /// <summary>ホストが送信した RPC/パケットをゼロ I/O でリングバッファに記録。</summary>
    public static void RecordHostAction(string tag, int len, string opt)
    {
        // len <= 3 は空パケットノイズとして無視 (CustomRpcSender の既存しきい値に合わせる)
        if (len <= 3) return;

        // 送信ホットパス(CustomRpcSender.SendMessage)から呼ばれるので、観測が送信を絶対に壊さないよう全体を包む。
        try
        {
            long ts = Utils.TimeStamp;

            ref HostActionEntry entry = ref SendRing[SendRingIndex];
            entry.Tag = tag;
            entry.Len = len;
            entry.Opt = opt;
            entry.Ts = ts;
            SendRingIndex = (SendRingIndex + 1) % SendRing.Length;

            ref HostActionEntry wide = ref TagRing[TagRingIndex];
            wide.Tag = tag;
            wide.Len = len;
            wide.Opt = opt;
            wide.Ts = ts;
            TagRingIndex = (TagRingIndex + 1) % TagRing.Length;
        }
        catch { }
    }

    /// <summary>横断セッション Timeline ログへの即時追記。sid= プレフィックスを自動付与。</summary>
    public static void Timeline(string line)
    {
        if (TimelinePath == null) return;
        try { File.AppendAllText(TimelinePath, $"sid={StartTs} {line}\n"); }
        catch { }
    }

    public static void RecordGameStart(CustomGameMode mode, int players, string rolesStr)
    {
        EnsureInit();

        try
        {
            _gameStartTime = Utils.TimeStamp;
            Logger.ResetExceptionTags();

            string line = $"GAMESTART gm={mode} players={players} roles=[{rolesStr}]";
            Write(line);
            Timeline(line);
        }
        catch (Exception e) { Utils.ThrowException(e); }
    }

    public static void RecordGameEnd(
        CustomGameMode mode,
        CustomWinner winnerTeam,
        System.Collections.Generic.HashSet<byte> winnerIds,
        System.Collections.Generic.HashSet<CustomRoles> winnerRoles,
        System.Collections.Generic.HashSet<AdditionalWinners> additionalWinnerTeams,
        int meetings,
        int players,
        bool allDead,
        bool isTimerEnd)
    {
        EnsureInit();

        try
        {
            long now = Utils.TimeStamp;
            long dur = _gameStartTime > 0 ? now - _gameStartTime : 0;

            // 異常フラグ計算
            bool flagShort = mode == CustomGameMode.Standard && dur < 30;
            bool flagNoWinner = winnerTeam is CustomWinner.None or CustomWinner.Draw or CustomWinner.Error or CustomWinner.Default;
            bool flagError = winnerTeam == CustomWinner.Error;
            bool flagAllDead = allDead && !isTimerEnd;
            bool flagUnattributed = !flagNoWinner && (winnerIds == null || winnerIds.Count == 0) && (winnerRoles == null || winnerRoles.Count == 0);

            string flags = string.Join(",", new[] {
                flagShort ? "short" : null,
                flagNoWinner ? "nowinner" : null,
                flagError ? "error" : null,
                flagAllDead ? "alldead" : null,
                flagUnattributed ? "unattributed" : null
            }.Where(f => f != null));

            string exTagsSummary = Logger.GetExceptionTagsSummary();
            string winIdsStr = winnerIds != null ? string.Join(",", winnerIds) : string.Empty;
            string winRolesStr = winnerRoles != null ? string.Join(",", winnerRoles) : string.Empty;
            string addStr = additionalWinnerTeams != null ? string.Join(",", additionalWinnerTeams) : string.Empty;

            string line = $"GAMEEND gm={mode} winner={winnerTeam} winIds=[{winIdsStr}] winRoles=[{winRolesStr}] add=[{addStr}] dur={dur} meetings={meetings} players={players} exTags=[{exTagsSummary}] flags=[{flags}]";
            Write(line);
            Timeline(line);

            bool anyBadFlag = flagShort || flagNoWinner || flagError || flagAllDead || flagUnattributed;
            if (anyBadFlag)
            {
                string anomLine = $"ANOM game winner={winnerTeam} dur={dur} flags=[{flags}]";
                Write(anomLine);
                Timeline(anomLine);
                Logger.Warn(anomLine, "Health");
            }
        }
        catch (Exception e) { Utils.ThrowException(e); }
    }

    // 観測レイヤーの外部利用者(UiAnomalyWatch 等)が EndKnot-Health.log に 1 行追記するための公開窓口。
    // クラッシュ/切断行のすぐ隣に異常行を並べてウォッチドッグが tail できるようにする。
    public static void Note(string line)
    {
        EnsureInit();
        Write(line);
    }

    // phase3 判定層(EarlyWarning 等)専用の窓口。Note() と違い Health + Timeline の両方に書く
    // (ウォッチドッグは Timeline を横断 tail するので、live 判定の異常は Timeline にも残す)。
    public static void NoteAnom(string line)
    {
        EnsureInit();
        Write(line);
        Timeline(line);
    }

    public static string GetState()
    {
        try
        {
            if (GameStates.InGame) return GameStates.IsMeeting ? "Meeting" : "InTask";
            if (GameStates.IsLobby) return "Lobby";
            if (GameStates.IsNotJoined) return "Menu";
            return AmongUsClient.Instance != null ? AmongUsClient.Instance.GameState.ToString() : "?";
        }
        catch { return "?"; }
    }

    private static void Write(string line)
    {
        if (FilePath == null) return;
        try { File.AppendAllText(FilePath, line + "\n"); }
        catch { }
    }
}

// 切断 / kick の理由を観測する自前パッチ(既存の AutoRehost / DisconnectInternalPatch とは独立・並走)。
[HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.DisconnectInternal))]
internal static class HealthLogDisconnectPatch
{
    // ReSharper disable once UnusedMember.Global
    public static void Prefix(DisconnectReasons reason, string stringReason)
    {
        HealthLog.RecordDisconnect(reason, stringReason);
        try { ClaudeBridge.OnDisconnect(reason, stringReason); } catch { } // ブリッジ OFF 時は即 return する軽量フック
    }
}
