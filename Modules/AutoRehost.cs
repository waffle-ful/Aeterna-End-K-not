using System;
using System.IO;
using System.Reflection;
using InnerNet;
using UnityEngine;

namespace EndKnot.Modules;

// 配信者向け「自動部屋立て直し」。
// 公式 kick (Hacking) / 通信エラー / タイムアウトでホストがオンライン部屋から切断されたら、
// 一定秒数 (既定 10 秒) 待ってから「同じリージョン・同じ設定」で新しいオンライン部屋を自動で立て直す。
// 立て直し後は EHR 既存の自律ループ (AutoPlayAgain / AutoStart) と LobbyShare の自動再投稿が引き継ぐ。
// 対象は公式・Modded 両方 (公式除外ガードは入れない。ループ保護は AutoRehostMaxAttempts のみ)。認証(EOS)には触れない。
//
// 【設計 — 2026-06-03 v3 / 大規模調査後の作り直し】
// 明示 4 フェーズのステートマシン: WaitClean → OpenDialog → WaitDialog → WaitJoin。
//   ・成功はポーリングではなく **OnGameJoined イベント** (OnGameJoinedPatch → NotifyJoinedNewLobby) で検知。
//     旧 GameId と比較して「別の新しい部屋に入った」ことを確認する。これで「古い部屋の残骸を成功と誤検知」も
//     「部屋は立ったのに成功扱いされずループ継続 → working な部屋を watchdog が破壊」も両方なくなる。
//   ・**アンチチートの真因は Confirm 連打ではなく「前セッションの teardown 中 / scene-connect 中にホスト作成すること」**
//     (実機ログ: 19 Confirm に対し kick は 2 回だけ、いずれも前部屋 OnLobbyDestroyed の 1〜2 秒後の最初の Confirm)。
//     よって AC 対策の本丸は **SETTLE GATE**: 完全にクリーンな MainMenu 状態が SettleSeconds 継続するまでホストしない。
//     1 回だけ Confirm するのは念のためのベルト (本丸ではない)。
//   ・作成は mm.OpenCreateGame() を reflection で呼ぶ (in-scene、scene-connect を避け MultiplayerMapIdFix も通らない)。
//     開かなければ同じ OpenCreateGame を再試行する。OpenGameModeMenu 系のフォールバックは使わない —
//     あれは create ダイアログではなく matchmaking へ遷移し、その残留状態が次試行の WaitClean を汚染して
//     全滅させる (実機ログで確認)。開けないまま WatchdogSeconds を超えたら StartAttempt がクリーンにリトライ。
//   ・リージョン/EHR 設定/ゲームモードは disconnect+ChangeScene を跨いで自動保持される (再適用不要)。MapId(Dleks=3)だけ
//     フォールバック経路で MultiplayerMapIdFix にリセットされうるので、念のため捕捉して再適用する。
public static class AutoRehost
{
    private enum Phase
    {
        WaitClean,  // クリーンな MainMenu を待ち、SettleSeconds 安定するまで待機 (AC 対策の本丸)
        OpenDialog, // 作成ダイアログを開く
        WaitDialog, // ダイアログが active になるのを待ち、1 回だけ Confirm
        WaitJoin    // 新部屋への join を待つ (成功は NotifyJoinedNewLobby イベントで来る)
    }

    private static bool _pending;
    private static int _attempts;
    private static int _seq;        // 世代トークン。古い Tick / stabilize を無効化する
    private static Phase _phase;
    private static float _deadline; // この試行のタイムアウト時刻
    private static float _cleanSince; // クリーン状態が始まった時刻 (0=未クリーン)
    private static float _openedAt; // ダイアログを開いた時刻 (DialogTimeout 用)
    private static float _nextWaitLogAt; // WaitClean 診断ログのスロットル
    private static int _oldGameId;  // 切断前の GameId (新部屋判定用)
    private static byte _oldMapId;  // 切断前の MapId (Dleks 保持用)
    // 「オンライン部屋のホストだった」ラッチ。OnGameJoined (AmHost が信頼できる綺麗な瞬間) で更新する。
    // 本物の kick では切断処理中に AmHost が先に false に倒れることがあるので、live AmHost ではなくこれを見る。
    private static bool _hostingOnlineLatch;

    // ===== tunable =====
    private const float PollInterval = 0.5f;
    private const float WatchdogSeconds = 45f;  // 新部屋に到達できなければリトライ
    private const float SettleSeconds = 3f;     // クリーン状態がこの秒数続いてからホスト (AC 対策の本丸)
    private const float DialogTimeout = 8f;     // ダイアログが開かなければ OpenCreateGame を再試行する猶予
    private const float StabilizeSeconds = 60f; // 新部屋がこの秒数もてば成功確定 → attempts リセット
    private const float SuccessPopupSeconds = 6f;
    // ===================

    private static int MaxAttempts => Mathf.Max(1, Options.AutoRehostMaxAttempts?.GetInt() ?? 3);

    // ── 切断検知 (ExitGamePatch.Prefix から)。Prefix 時点なら AmHost / NetworkMode / GameId がまだ正しい ──
    public static void OnDisconnect(DisconnectReasons reason)
    {
        if (!(Options.AutoRehostAfterKick?.GetBool() ?? false)) return;

        if (reason is DisconnectReasons.ExitGame or DisconnectReasons.Banned or DisconnectReasons.IncorrectVersion)
        {
            if (_pending) Cancel("intentional/unrecoverable disconnect");
            return;
        }

        // 認証/サーバー死は同プロセスでの立て直し不能。AutoRestart が直接プロセス再起動へ回すので、
        // ここで無駄な 3×リトライ (~145s) を始めない (穴1)。進行中の立て直しがあれば畳む。
        if (AutoRestart.IsFatalConnectionDeath(reason))
        {
            if (_pending) Cancel($"fatal connection death ({reason}) -> handing to AutoRestart");
            return;
        }

        // 本物の kick では AmHost が切断処理中に倒れることがあるので、live AmHost に加えて
        // OnGameJoined で記録したラッチも見る (どちらかが「オンラインホストだった」と言えば対象)。
        bool wasHostingOnline = _hostingOnlineLatch
                             || (AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost && GameStates.IsOnlineGame);
        if (!wasHostingOnline) return;

        if (_pending) return;

        _pending = true;
        OfficialServerNotice.SuppressWhileRehosting = true;

        _oldGameId = AmongUsClient.Instance.GameId;
        try { _oldMapId = GameOptionsManager.Instance.normalGameHostOptions.MapId; }
        catch { _oldMapId = 0; }

        float delay = Mathf.Max(1, Options.AutoRehostDelay?.GetInt() ?? 10);
        Logger.Info($"Host disconnected (reason: {reason}). Auto-rehost in {delay:N0}s (attempt {_attempts + 1}/{MaxAttempts}); oldGameId={_oldGameId} oldMapId={_oldMapId}", "AutoRehost");
        LateTask.New(StartAttempt, delay, "AutoRehost.StartAttempt", log: false);
    }

    // ── 起動時 (番犬による再起動後) の自動ホスト handoff ──
    // 番犬は AU を (再)起動する際に <Desktop>/EndKnot_Logs/autohost_request.flag を置く。
    // メインメニュー読込時にそれを見つけたら、設定ロード完了を待って前回設定 (region/map/EHR設定は
    // ディスクから自動復元される) で新しいオンライン部屋を立てる。マーカーは一度読んだら消す (再発火防止)。
    public static void OnMainMenuStart()
    {
        try
        {
            string marker = StartupHostMarkerPath();
            if (!File.Exists(marker)) return;

            DateTime writeTime;
            try { writeTime = File.GetLastWriteTime(marker); }
            catch { writeTime = DateTime.MinValue; }

            try { File.Delete(marker); } catch { } // 一度で消費 (再発火防止)

            if ((DateTime.Now - writeTime).TotalMinutes > 10)
            {
                Logger.Info("Auto-rehost: startup host marker is stale (>10min); ignoring", "AutoRehost");
                return;
            }

            Logger.Info("Auto-rehost: startup host marker found (watchdog handoff); will host once options are loaded", "AutoRehost");
            WaitForLoadThenHost(0);
        }
        catch (Exception e) { Utils.ThrowException(e); }
    }

    private static string StartupHostMarkerPath()
    {
        string baseP = OperatingSystem.IsAndroid() ? Main.DataPath : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        return Path.Combine(baseP, "EndKnot_Logs", "autohost_request.flag");
    }

    // AutoRestart (プロセス再起動エスカレーション) が終了前に置く。再起動後の OnMainMenuStart が
    // これを見つけて前回設定で自動ホストする。番犬も再起動時に置くが、依存しないよう二重で保険する。
    internal static void EnsureStartupHostMarker()
    {
        try
        {
            string marker = StartupHostMarkerPath();
            Directory.CreateDirectory(Path.GetDirectoryName(marker));
            File.WriteAllText(marker, DateTime.Now.ToString("o"));
        }
        catch (Exception e) { Logger.Warn($"EnsureStartupHostMarker failed: {e.Message}", "AutoRehost"); }
    }

    // 設定ロード完了 + クリーンなメニューになるまで待ってから起動時ホストを開始 (オプション未ロードレース回避)。
    private static void WaitForLoadThenHost(int tries)
    {
        if (tries > 90)
        {
            Logger.Warn("Auto-rehost: startup host aborted (not ready within ~90s)", "AutoRehost");
            return;
        }

        bool ready = Options.IsLoaded
                  && GameStates.IsNotJoined
                  && UnityEngine.Object.FindObjectOfType<MainMenuManager>() != null;

        if (!ready)
        {
            LateTask.New(() => WaitForLoadThenHost(tries + 1), 1f, "AutoRehost.StartupWait", log: false);
            return;
        }

        RequestStartupHost();
    }

    // 起動時ホストの開始 (切断起因でなく、番犬の再起動 handoff から)。前ゲームが無いので oldGameId=-1 に
    // して「実 GameId を持つ部屋に入れたら成功」とする。既に MainMenu に居るので ChangeScene しない。
    public static void RequestStartupHost()
    {
        if (_pending) return; // 既に立て直し/起動ホスト進行中

        _pending = true;
        OfficialServerNotice.SuppressWhileRehosting = true;
        _oldGameId = -1;
        try { _oldMapId = GameOptionsManager.Instance.normalGameHostOptions.MapId; }
        catch { _oldMapId = 0; }
        _attempts = 0;

        Logger.Info("Auto-rehost: startup auto-host starting (region/map/settings restored from disk)", "AutoRehost");
        BeginAttempt(false); // 既にメインメニューなので scene 切替不要
    }

    // ── 待機満了 → この試行を開始。リトライの起点でもある ──
    private static void StartAttempt()
    {
        BeginAttempt(true);
    }

    // 1 回の試行を開始。changeScene=true で MainMenu へ戻ってから (切断rehost/リトライ)、
    // false で現在のメインメニューのまま (起動時ホスト) 開始する。
    private static void BeginAttempt(bool changeScene)
    {
        if (!_pending) return;

        _attempts++;
        if (_attempts > MaxAttempts)
        {
            GiveUp();
            return;
        }

        _seq++;
        int mySeq = _seq;
        _phase = Phase.WaitClean;
        _cleanSince = 0f;
        _openedAt = 0f;
        _nextWaitLogAt = 0f;
        _deadline = Time.realtimeSinceStartup + WatchdogSeconds;

        Logger.Info($"Auto-rehost attempt {_attempts}/{MaxAttempts}: {(changeScene ? "leaving to MainMenu" : "hosting from current MainMenu")}", "AutoRehost");
        if (changeScene)
        {
            try { SceneChanger.ChangeScene("MainMenu"); }
            catch (Exception ex) { Logger.Warn($"ChangeScene(MainMenu) failed: {ex.Message}", "AutoRehost"); }
        }

        LateTask.New(() => Tick(mySeq), 1f, "AutoRehost.Tick", log: false);
    }

    private static void Tick(int mySeq)
    {
        if (!_pending || _seq != mySeq) return; // 完了 or 世代切替

        if (Time.realtimeSinceStartup > _deadline)
        {
            Logger.Warn($"Auto-rehost: attempt {_attempts} timed out (phase {_phase}); retrying", "AutoRehost");
            HealthLog.NoteAnom($"ANOM live kind=rehost stage=timeout attempt={_attempts} phase={_phase}");
            StartAttempt();
            return;
        }

        try { DriveOneStep(); }
        catch (Exception ex) { Logger.Warn($"Auto-rehost tick error: {ex.Message}", "AutoRehost"); }

        if (!_pending || _seq != mySeq) return; // 成功 / リトライで世代が変わった
        LateTask.New(() => Tick(mySeq), PollInterval, "AutoRehost.Tick", log: false);
    }

    private static void DriveOneStep()
    {
        float now = Time.realtimeSinceStartup;

        switch (_phase)
        {
            case Phase.WaitClean:
            {
                // 完全にクリーンな MainMenu か? (どれも active-only / singleton 判定で zombie を拾わない)
                bool notJoined = GameStates.IsNotJoined;
                bool lobbyNull = LobbyBehaviour.Instance == null;
                bool atMenu = UnityEngine.Object.FindObjectOfType<MainMenuManager>() != null;
                bool noMatchmaking = UnityEngine.Object.FindObjectOfType<MMOnlineManager>() == null;
                bool optsLoaded = Options.IsLoaded; // 起動時ホストで未ロード設定を参照しないためのゲート (切断rehost時は常にtrue)
                bool clean = notJoined && lobbyNull && atMenu && noMatchmaking && optsLoaded;

                if (!clean)
                {
                    _cleanSince = 0f;
                    // 自己診断: WaitClean で詰まると watchdog timeout と区別がつかないので、どの条件が未達か出す
                    // (特に MMOnlineManager が MainMenu に居座ると永久に clean にならない可能性 — 実機で要確認)
                    if (now >= _nextWaitLogAt)
                    {
                        _nextWaitLogAt = now + 2f;
                        Logger.Info($"Auto-rehost WaitClean: not clean yet (IsNotJoined={notJoined} LobbyNull={lobbyNull} MainMenu={atMenu} NoMatchmaking={noMatchmaking} OptsLoaded={optsLoaded})", "AutoRehost");
                    }
                    return;
                }

                if (_cleanSince == 0f)
                {
                    _cleanSince = now;
                    Logger.Info($"Auto-rehost: clean MainMenu reached; settling {SettleSeconds:N0}s before host", "AutoRehost");
                    return;
                }
                if (now - _cleanSince < SettleSeconds) return; // SETTLE GATE (AC 対策の本丸)

                Logger.Info("Auto-rehost: settled -> proceeding to create", "AutoRehost");
                ReapplyMapId();
                _phase = Phase.OpenDialog;
                return;
            }

            case Phase.OpenDialog:
            {
                MainMenuManager mm = UnityEngine.Object.FindObjectOfType<MainMenuManager>();
                if (mm == null) { _phase = Phase.WaitClean; _cleanSince = 0f; return; }

                // OpenCreateGame() は in-scene で create ダイアログを開く (matchmaking scene-connect を回避)。
                // ここで OpenGameModeMenu 系のフォールバックは使わない — あれは create ダイアログではなく
                // matchmaking 画面へ遷移させ、その残留状態 (IsNotJoined=False) が次の試行の WaitClean を
                // 永久に汚染して全滅させる (実機ログで確認済み)。開かなければ WaitDialog から再試行する。
                if (TryOpenCreateGame(mm))
                    Logger.Info("Auto-rehost: opening create dialog via OpenCreateGame()", "AutoRehost");
                else
                    Logger.Warn("Auto-rehost: OpenCreateGame unavailable on this build; will keep retrying (no unsafe fallback)", "AutoRehost");

                _openedAt = now;
                _phase = Phase.WaitDialog;
                return;
            }

            case Phase.WaitDialog:
            {
                CreateGameOptions cgo = UnityEngine.Object.FindObjectOfType<CreateGameOptions>(); // active-only
                if (cgo != null && cgo.isActiveAndEnabled)
                {
                    Logger.Info($"Auto-rehost: CreateGameOptions active -> Confirm() once (region={Utils.GetRegionName()} mapId={SafeMapId()})", "AutoRehost");
                    try { cgo.Confirm(); }
                    catch (Exception ex) { Logger.Warn($"CreateGameOptions.Confirm() failed: {ex.Message}", "AutoRehost"); }
                    _phase = Phase.WaitJoin; // 二度と Confirm しない。成功は OnGameJoined で来る
                    return;
                }

                // ダイアログが開かなかった場合は OpenCreateGame を再試行する (in-scene なので状態を汚さない)。
                // 全体は WatchdogSeconds で頭打ちされ、超えれば StartAttempt がクリーンにリトライする。
                if (now - _openedAt > DialogTimeout)
                {
                    Logger.Warn("Auto-rehost: create dialog never activated; re-opening via OpenCreateGame", "AutoRehost");
                    _phase = Phase.OpenDialog;
                }
                return;
            }

            case Phase.WaitJoin:
                // 何もしない。成功は NotifyJoinedNewLobby (OnGameJoined) で来る。来なければ watchdog がリトライ。
                return;
        }
    }

    // mm.OpenCreateGame() を reflection で呼ぶ (リポジトリ未参照のため直書きすると compile risk)。
    // 存在すれば in-scene でダイアログを開き、matchmaking scene-connect (kick と相関) と MultiplayerMapIdFix を回避できる。
    private static bool TryOpenCreateGame(MainMenuManager mm)
    {
        try
        {
            MethodInfo m = typeof(MainMenuManager).GetMethod("OpenCreateGame", BindingFlags.Public | BindingFlags.Instance);
            if (m == null) return false;
            m.Invoke(mm, null);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"OpenCreateGame reflection failed: {ex.Message}", "AutoRehost");
            return false;
        }
    }

    // 切断前の MapId を再適用 (リージョン/設定/ゲームモードは自動保持だが、Dleks=3 だけはフォールバック経路で
    // MultiplayerMapIdFix にリセットされうるので保険)。
    private static void ReapplyMapId()
    {
        try
        {
            var opts = GameOptionsManager.Instance.normalGameHostOptions;
            if (opts != null && opts.MapId != _oldMapId)
            {
                opts.MapId = _oldMapId;
                GameOptionsManager.Instance.SaveNormalHostOptions();
            }
        }
        catch (Exception ex) { Logger.Warn($"ReapplyMapId failed: {ex.Message}", "AutoRehost"); }
    }

    private static byte SafeMapId()
    {
        try { return GameOptionsManager.Instance.normalGameHostOptions.MapId; }
        catch { return 0; }
    }

    // ── 成功検知 (OnGameJoinedPatch.Postfix から)。OnGameJoined は本物の join で 1 回だけ発火し、
    //    破棄済みの旧部屋では再発火しないので誤検知しない。GameId が旧と違う = 別の新部屋に入った ──
    public static void NotifyJoinedNewLobby()
    {
        AmongUsClient c = AmongUsClient.Instance;
        if (c == null) return;

        // ホスト状態のラッチ更新 (AmHost が信頼できる綺麗な瞬間に記録 → 実 kick 時の判定に使う)。
        // オンラインのホストなら true、クライアント参加 / freeplay / local なら false。
        _hostingOnlineLatch = c.AmHost && GameStates.IsOnlineGame;

        // 立て直し進行中の成功検知
        if (!_pending) return;
        if (!(c.AmHost && GameStates.IsOnlineGame)) return;
        if (c.GameId == _oldGameId) return; // 新しい部屋ではない (stale ガード)
        Success();
    }

    private static void Success()
    {
        Logger.Info($"Auto-rehost: new lobby reached (attempt {_attempts}, gameId={AmongUsClient.Instance?.GameId}). Handing off to existing auto-start loop", "AutoRehost");
        _pending = false;
        _seq++; // 飛んでいる Tick を無効化
        OfficialServerNotice.SuppressWhileRehosting = false;
        ShowAutoDismissPopup(GetSafeString("AutoRehost.Success", "Lobby was automatically re-hosted."), SuccessPopupSeconds);

        // すぐ attempts を 0 にすると「成功 → 即再 kick」のループを検知できないので、一定時間もったらリセット
        int genAtSuccess = _seq;
        LateTask.New(() =>
        {
            if (_seq == genAtSuccess && !_pending) _attempts = 0;
        }, StabilizeSeconds, "AutoRehost.Stabilize", log: false);
    }

    private static void GiveUp()
    {
        Logger.Warn($"Auto-rehost: reached max attempts ({MaxAttempts}); stopping", "AutoRehost");
        HealthLog.NoteAnom($"ANOM live kind=rehost stage=giveup attempts={MaxAttempts}");
        ResetState();

        // その場立て直しが尽きた = 認証/回線が死んでいる可能性が高い。番犬が居ればプロセスごと再起動して
        // EOS ログイン+接続をやり直す (その場では戻れない「認証不能」からの唯一の回復パス)。
        // 番犬が居なければ EscalateFromRehostGiveUp が手動再起動を促す通知だけ出す。
        AutoRestart.EscalateFromRehostGiveUp();
    }

    private static void Cancel(string why)
    {
        Logger.Info($"Auto-rehost: cancelled ({why})", "AutoRehost");
        ResetState();
    }

    private static void ResetState()
    {
        _pending = false;
        _cleanSince = 0f;
        _openedAt = 0f;
        _attempts = 0;
        _seq++; // 飛んでいる Tick / stabilize を無効化
        OfficialServerNotice.SuppressWhileRehosting = false;
    }

    // 自動で消えるポップアップ (無人ホスト運用でモーダルが残り続けないように)。
    // ShowPopUp は HudManager.Instance.Dialogue (型は DialogueBox) を使う共有ポップアップなので、N 秒後に閉じる。
    private static void ShowAutoDismissPopup(string text, float seconds)
    {
        if (!HudManager.InstanceExists) return;
        try { HudManager.Instance.ShowPopUp(text); }
        catch { return; }

        LateTask.New(() =>
        {
            try
            {
                DialogueBox dlg = HudManager.Instance != null ? HudManager.Instance.Dialogue : null;
                if (dlg != null) dlg.gameObject.SetActive(false);
            }
            catch { }
        }, seconds, "AutoRehost.DismissPopup", log: false);
    }

    private static string GetSafeString(string key, string fallback)
    {
        try
        {
            string s = Translator.GetString(key);
            return string.IsNullOrEmpty(s) || s.StartsWith("*") ? fallback : s;
        }
        catch { return fallback; }
    }
}
