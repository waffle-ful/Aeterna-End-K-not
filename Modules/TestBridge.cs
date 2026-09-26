using System;
using AmongUs.Data;
using AmongUs.GameOptions;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using EndKnot.Patches;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using InnerNet;
using TMPro;
using UnityEngine;

namespace EndKnot.Modules;

// 外部ツールからゲームを遠隔テストするための観測・操作ブリッジ(既定 OFF、config でのみ有効化)。
// <Desktop>/EndKnot_Logs/bridge-cmd.txt を 1/sec ポーリングしてチャットコマンドを実行し、
// bridge-out.log に結果を書き出す。スクショは Screens/ 配下へ保存する。
// 入出力はプレーンなテキストファイルだけなので、driver 側の実装言語や種類は問わない。
// 全処理はメインスレッド(FixedUpdateCaller の 1/sec ゲート + コルーチン)のみで完結させ、
// FileSystemWatcher 等の非同期監視は使わない。host-only 前提(Command.Action は LocalPlayer=host で実行)。
public static class TestBridge
{
    private const int MaxBatchLines = 20; // 1回のファイル読取で受け付けるディレクティブ数の上限
    private const long MaxOutFileBytes = 2 * 1024 * 1024; // bridge-out.log の .prev ローテート閾値

    private static bool _inited;
    private static string _dir;
    private static string _cmdPath;
    private static string _legacyCmdPath; // 旧名 (claude-cmd.txt)。既存の driver スクリプトを当面壊さないための受け口。
    private static string _outPath;
    private static string _statePath;
    private static string _screensDir;

    private static bool _captureInFlight;
    private static long _lastAutoShotTs;

    // ファイルから読み取った未実行ディレクティブのキュー。排出レート 1件/秒を守るため、
    // ファイル読取と削除は一括で行い、実行だけを Tick ごとに 1件ずつ進める。
    private static readonly Queue<string> PendingDirectives = new();
    private static readonly Queue<string> ObservationDirectives = new(); // wait 中に届いた観測専用行の追い越しレーン

    private static void EnsureInit()
    {
        if (_inited) return;
        _inited = true;

        try
        {
            // HealthLog と同じ配置式(EndKnot_Logs 直下)。Windows 限定機能だが式自体は揃えておく。
            string basePath = OperatingSystem.IsAndroid() ? Main.DataPath : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            _dir = Path.Combine(basePath, "EndKnot_Logs");
            Directory.CreateDirectory(_dir);

            _cmdPath = Path.Combine(_dir, "bridge-cmd.txt");
            _legacyCmdPath = Path.Combine(_dir, "claude-cmd.txt");
            _outPath = Path.Combine(_dir, "bridge-out.log");
            _statePath = Path.Combine(_dir, "bridge-state.json");
            _screensDir = Path.Combine(_dir, "Screens");
            Directory.CreateDirectory(_screensDir);
        }
        catch { _dir = null; }
    }

    // Tick は FixedUpdate 毎 (50Hz) に呼ばれる (2026-09-13: 旧 1/sec ゲートを撤廃 — 1 行/秒のキュー処理が
    // 連投テストと応答レイテンシの上限になっていた)。cmd ファイルの stat は 100ms 間隔に絞り、
    // 自動スクショ/ロビーコード/idle 警告は従来どおり 1s 間隔。実行はキューから 1 tick 1 本 (= 最大 50 行/秒)。
    private static long _lastCmdPollMs;
    private static long _lastSlowTickMs;
    private const int CmdPollIntervalMs = 100;

    public static void Tick()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (Main.EnableTestBridge is not { Value: true }) return;

        EnsureInit();
        if (_dir == null) return;

        long nowMs = Environment.TickCount64;
        bool pollFile = nowMs - _lastCmdPollMs >= CmdPollIntervalMs;
        if (pollFile) _lastCmdPollMs = nowMs;

        try { DrainCommandFile(pollFile); }
        catch (Exception e) { Utils.ThrowException(e); }

        if (nowMs - _lastSlowTickMs < 1000) return;
        _lastSlowTickMs = nowMs;

        try { HandleAutoScreenshot(); }
        catch (Exception e) { Utils.ThrowException(e); }

        try { PushLobbyCodeIfChanged(); }
        catch (Exception e) { Utils.ThrowException(e); }

        try { WarnLobbyIdleDeadline(); }
        catch (Exception e) { Utils.ThrowException(e); }
    }

    // Utils.SendLocally からの写し窓口。ホストローカル表示のチャット/通知を bridge-out.log にも記録する。
    public static void OnHostSystemMessage(string title, string text)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (Main.EnableTestBridge is not { Value: true }) return;

        EnsureInit();
        if (_dir == null) return;

        try
        {
            string safeText = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\\n");
            if (safeText.Length > 4000) safeText = safeText[..4000] + "...";

            string safeTitle = (title ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ');

            WriteOut($"SYS {safeTitle}: {safeText}");
        }
        catch { }
    }

    private static void DrainCommandFile(bool pollFile)
    {
        // wait 中: 後続ディレクティブはキューに滞留させたまま条件だけを評価する。
        // cmd ファイルは読み続ける(`wait cancel` の割り込みと観測系の追い越しを受けるため)。
        if (_activeWait != null)
        {
            if (pollFile && PendingDirectives.Count + ObservationDirectives.Count < MaxBatchLines) ReadCmdFileIntoQueue(duringWait: true);
            if (TryConsumeWaitCancel()) return;

            // wait 開始「後」に届いた観測専用ディレクティブ (state/screenshot/errors/grep) は副作用が無いので
            // 追い越して即実行する — 待ちの間スクショが更新されず「ブリッジが死んだ」と誤読される実害への対処。
            // wait より前から滞留していた行は追い越さない (スクリプトの「wait の後に撮る」意図を壊さないため、
            // 振り分けは読み込み時に duringWait で行う)。1 tick 1 本まで。
            if (ObservationDirectives.Count > 0) ExecuteDirective(ObservationDirectives.Dequeue());

            EvaluateActiveWait();
            return;
        }

        // wait 解除と同 tick に残った追い越し分を先に掃く (通常時は空)
        if (ObservationDirectives.Count > 0)
        {
            ExecuteDirective(ObservationDirectives.Dequeue());
            return;
        }

        if (PendingDirectives.Count == 0)
        {
            if (!pollFile) return;
            ReadCmdFileIntoQueue();
            if (PendingDirectives.Count == 0) return;
        }

        string directive = PendingDirectives.Dequeue();
        ExecuteDirective(directive);
    }

    private static bool IsObservationDirective(string d)
    {
        return d.Equals("state", StringComparison.OrdinalIgnoreCase)
               || d.Equals("screenshot", StringComparison.OrdinalIgnoreCase)
               || d.Equals("errors", StringComparison.OrdinalIgnoreCase) || d.StartsWith("errors ", StringComparison.OrdinalIgnoreCase)
               || d.Equals("grep", StringComparison.OrdinalIgnoreCase) || d.StartsWith("grep ", StringComparison.OrdinalIgnoreCase);
    }

    private static void ReadCmdFileIntoQueue(bool duringWait = false)
    {
        List<string> lines = TryReadAndClearCmdFile();
        if (lines == null) return;

        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            if (duringWait && IsObservationDirective(line)) ObservationDirectives.Enqueue(line);
            else PendingDirectives.Enqueue(line);

            if (PendingDirectives.Count + ObservationDirectives.Count >= MaxBatchLines) break;
        }
    }

    // 既読管理 = 削除方式。実行前にファイルを消す(flood-clear の教訓)。削除失敗→truncate、
    // それも失敗したら今回は何も実行しない(誤再実行ゼロを構造で保証)。
    private static List<string> TryReadAndClearCmdFile()
    {
        // 新名を優先し、無ければ旧名を読む。旧名で書く driver がまだ動いていても取りこぼさない。
        string path = File.Exists(_cmdPath) ? _cmdPath
            : _legacyCmdPath != null && File.Exists(_legacyCmdPath) ? _legacyCmdPath
            : null;

        if (path == null) return null;

        List<string> lines;

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            using var sr = new StreamReader(fs, Encoding.UTF8);

            lines = [];
            string line;
            while ((line = sr.ReadLine()) != null) lines.Add(line);
        }
        catch { return null; } // ロック中等 = 次回リトライ

        try { File.Delete(path); }
        catch
        {
            try { File.WriteAllText(path, string.Empty); }
            catch { return null; }
        }

        return lines;
    }

    private static void ExecuteDirective(string directive)
    {
        WriteOut($"> {directive}");

        if (directive.Equals("screenshot", StringComparison.OrdinalIgnoreCase))
        {
            if (!RequestScreenshot("manual")) WriteOut("ERR screenshot busy");
            return;
        }

        // 数秒で消える演出 (開票アニメ・キルフラッシュ等) を撮るための遅延シャッター。
        // 外から screenshot を撃つと往復 (3〜15 秒) が窓より長くて間に合わないので、
        // 「撮る時刻」をゲーム内に予約しておく。
        if (directive.StartsWith("delayshot ", StringComparison.OrdinalIgnoreCase))
        {
            try { ExecuteDelayShot(directive[10..].Trim()); }
            catch (Exception e) { Utils.ThrowException(e); WriteOut("ERR delayshot failed"); }
            return;
        }

        // 動きのある演出 (雨・稲妻など) をコマ送りで見るための連写。Screens/burst_<ts>/ へ保存。
        if (directive.StartsWith("burst ", StringComparison.OrdinalIgnoreCase))
        {
            try { ExecuteBurst(directive[6..].Trim()); }
            catch (Exception e) { Utils.ThrowException(e); WriteOut("ERR burst failed"); }
            return;
        }

        // シーン上の SpriteRenderer とカメラ設定を rdump.txt へ書き出す (背景の構造調査用)。
        if (directive.Equals("rdump", StringComparison.OrdinalIgnoreCase) || directive.StartsWith("rdump ", StringComparison.OrdinalIgnoreCase))
        {
            try { ExecuteRendererDump(directive.Length > 6 ? directive[6..].Trim() : ""); }
            catch (Exception e) { Utils.ThrowException(e); WriteOut("ERR rdump failed"); }
            return;
        }

        // Layer 1: 構造化スナップショット。Menu 画面でも動く(host 非依存)。
        if (directive.Equals("state", StringComparison.OrdinalIgnoreCase))
        {
            try { WriteState(); }
            catch (Exception e) { Utils.ThrowException(e); WriteOut("ERR state failed"); }
            return;
        }

        // Layer 3: PassiveButton クリック。selector はスナップショットの handle または `label:<text>`。
        if (directive.StartsWith("click ", StringComparison.OrdinalIgnoreCase))
        {
            try { ExecuteClick(directive[6..].Trim()); }
            catch (Exception e) { Utils.ThrowException(e); WriteOut("ERR click failed"); }
            return;
        }

        // Layer 3b: OS レベルのマウス注入。click(OnClick.Invoke 直呼び)では発火しない動的配線 UI
        // (会議の投票確認・Shapeshifter 対象選択等)向け。
        if (directive.StartsWith("press ", StringComparison.OrdinalIgnoreCase))
        {
            try { ExecutePress(directive[6..].Trim()); }
            catch (Exception e) { Utils.ThrowException(e); WriteOut("ERR press failed"); }
            return;
        }

        // Layer 3b-2: OS レベルのホイール注入。スクロールビュー (バニラ設定パネル等) の
        // 画面外にある行は click/press では触れないので、これでしか届かない。
        if (directive.StartsWith("scroll ", StringComparison.OrdinalIgnoreCase))
        {
            try { ExecuteScroll(directive[7..].Trim()); }
            catch (Exception e) { Utils.ThrowException(e); WriteOut("ERR scroll failed"); }
            return;
        }

        // Layer 3c: OS レベルのキーボード注入。テキスト欄 (設定検索・数値入力) に文字を打つ経路は
        // Unity の Input.inputString しか無いので、click/chat では代用できない。
        if (directive.StartsWith("type ", StringComparison.OrdinalIgnoreCase))
        {
            try { ExecuteType(directive[5..]); }
            catch (Exception e) { Utils.ThrowException(e); WriteOut("ERR type failed"); }
            return;
        }

        if (directive.StartsWith("key ", StringComparison.OrdinalIgnoreCase))
        {
            try { ExecuteKey(directive[4..].Trim()); }
            catch (Exception e) { Utils.ThrowException(e); WriteOut("ERR key failed"); }
            return;
        }

        // Layer A: mod オプション操作(OptionItem ツリー直アクセス。/changesetting は vanilla 設定専用)。
        if (directive.StartsWith("getopt ", StringComparison.OrdinalIgnoreCase))
        {
            try { ExecuteGetOpt(directive[7..].Trim()); }
            catch (Exception e) { Utils.ThrowException(e); WriteOut("ERR getopt failed"); }
            return;
        }

        if (directive.StartsWith("setopt ", StringComparison.OrdinalIgnoreCase))
        {
            try { ExecuteSetOpt(directive[7..].Trim()); }
            catch (Exception e) { Utils.ThrowException(e); WriteOut("ERR setopt failed"); }
            return;
        }

        // `setopt#<id> <value>`(空白無し)も受理する。ExecuteSetOpt 側の #<id> 分岐がそのまま処理できる。
        if (directive.StartsWith("setopt#", StringComparison.OrdinalIgnoreCase))
        {
            try { ExecuteSetOpt(directive[6..].Trim()); }
            catch (Exception e) { Utils.ThrowException(e); WriteOut("ERR setopt failed"); }
            return;
        }

        // Layer A: 役職の事前指定。翻訳名パース(/setrole)を経由せず CustomRoles enum 名で直接書く。
        if (directive.StartsWith("forcerole ", StringComparison.OrdinalIgnoreCase))
        {
            try { ExecuteForceRole(directive[10..].Trim()); }
            catch (Exception e) { Utils.ThrowException(e); WriteOut("ERR forcerole failed"); }
            return;
        }

        // ホストのロビーチャット種別を切り替える (quick = クイックチャット専用ロビー、free = 既定)。次に作るロビーから効く。
        if (directive.StartsWith("chatmode ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var mode = directive[9..].Trim();
                bool quick = mode.Equals("quick", StringComparison.OrdinalIgnoreCase);
                ChatControllerUpdatePatch.AllowQuickChatOnly = quick;
                DataManager.Settings.Multiplayer.ChatMode = quick ? QuickChatModes.QuickChatOnly : QuickChatModes.FreeChatOrQuickChat;
                DataManager.Settings.Save();
                WriteOut($"OK chatmode {(quick ? "quick" : "free")} (current={DataManager.Settings.Multiplayer.ChatMode})");
            }
            catch (Exception e) { Utils.ThrowException(e); WriteOut("ERR chatmode failed"); }
            return;
        }

        // Layer B: カウントダウン無しの即時ゲーム開始。
        if (directive.Equals("start", StringComparison.OrdinalIgnoreCase))
        {
            try { ExecuteStart(); }
            catch (Exception e) { Utils.ThrowException(e); WriteOut("ERR start failed"); }
            return;
        }

        // Layer B2: メインメニューからのロビー自動作成 (AutoRehost の起動時ホスト機構を借用 —
        // UI クリックチェーン非依存。成立は wait phase=Lobby で待ち、入場時に LOBBYCODE が push される)。
        if (directive.Equals("hostlobby", StringComparison.OrdinalIgnoreCase))
        {
            try { ExecuteHostLobby(); }
            catch (Exception e) { Utils.ThrowException(e); WriteOut("ERR hostlobby failed"); }
            return;
        }

        // Layer B2: ロビー/ゲームから抜けてメインメニューへ戻る (hostlobby / eosstall の前段)。
        if (directive.Equals("leavelobby", StringComparison.OrdinalIgnoreCase))
        {
            try { ExecuteLeaveLobby(); }
            catch (Exception e) { Utils.ThrowException(e); WriteOut("ERR leavelobby failed"); }
            return;
        }

        // Layer B2: 起動時 EOS ログインフロー停止の模擬 (AutoRehost の見張りとホスト抑止の実機検証口)。
        if (directive.Equals("eosstall", StringComparison.OrdinalIgnoreCase))
        {
            try { ExecuteEosStall(); }
            catch (Exception e) { Utils.ThrowException(e); WriteOut("ERR eosstall failed"); }
            return;
        }

        // Layer B2: ログイン失敗ダイアログの模擬表示 (メニューで AccountManager が寝ていても見えるかの検証口)。
        if (directive.Equals("signinfail", StringComparison.OrdinalIgnoreCase))
        {
            try { ExecuteSignInFail(); }
            catch (Exception e) { Utils.ThrowException(e); WriteOut("ERR signinfail failed"); }
            return;
        }

        // Layer A2: AutoStart (ConfigEntry — setopt の OptionItem ツリー外) のフリップ。
        if (directive.StartsWith("autostart ", StringComparison.OrdinalIgnoreCase))
        {
            try { ExecuteAutoStart(directive[10..].Trim()); }
            catch (Exception e) { Utils.ThrowException(e); WriteOut("ERR autostart failed"); }
            return;
        }

        // Layer C: ホストの TP と HUD アクションボタン押下。
        if (directive.StartsWith("tp ", StringComparison.OrdinalIgnoreCase))
        {
            try { ExecuteTp(directive[3..].Trim()); }
            catch (Exception e) { Utils.ThrowException(e); WriteOut("ERR tp failed"); }
            return;
        }

        // Layer C2: サボタージュの発動/修理 (2026-09-13)。ホストの `click SabotageButton` / `press` はサボ画面が開かず
        // (役職ゲートでタスクマップに化ける)、エミュ側も対象アイコンに届かなかったため、サボマップのボタンが送るのと
        // 同じ RpcUpdateSystem(Sabotage, type) をホスト自身で撃つ。ホスト経路なので ShipStatusPatch のゲート
        // (DisableSabotage / EKR AllowsSabotage / Fool) も同じく通る = 「ゲートで弾かれた」を active=false で観測できる。
        if (directive.StartsWith("sabotage ", StringComparison.OrdinalIgnoreCase))
        {
            try { ExecuteSabotage(directive[9..].Trim()); }
            catch (Exception e) { Utils.ThrowException(e); WriteOut("ERR sabotage failed"); }
            return;
        }

        if (directive.StartsWith("fixsabotage ", StringComparison.OrdinalIgnoreCase))
        {
            try { ExecuteFixSabotage(directive[12..].Trim()); }
            catch (Exception e) { Utils.ThrowException(e); WriteOut("ERR fixsabotage failed"); }
            return;
        }

        if (directive.StartsWith("switch ", StringComparison.OrdinalIgnoreCase))
        {
            try { ExecuteSwitch(directive[7..].Trim()); }
            catch (Exception e) { Utils.ThrowException(e); WriteOut("ERR switch failed"); }
            return;
        }

        // Layer C3: ホスト発言を本物の UI 経路 (ChatController.SendChat → Prefix パッチ) で送る (2026-09-13)。
        // `chat` は RpcSendChat 直呼びで送信側フック (WordKiller / EKR FireChat 等) が鳴らない。
        if (directive.StartsWith("chatui ", StringComparison.OrdinalIgnoreCase))
        {
            try { ExecuteChatUi(directive[7..]); }
            catch (Exception e) { Utils.ThrowException(e); WriteOut("ERR chatui failed"); }
            return;
        }

        if (directive.StartsWith("use ", StringComparison.OrdinalIgnoreCase))
        {
            try { ExecuteUse(directive[4..].Trim()); }
            catch (Exception e) { Utils.ThrowException(e); WriteOut("ERR use failed"); }
            return;
        }

        // Layer C: 任意プレイヤーのタスク完了。非モッド客はタスク画面を自動操作できないため、
        // ホストから RpcCompleteTask を撃って GameData.CompleteTask 経由の OnTaskComplete 系を鳴らす。
        if (directive.StartsWith("task ", StringComparison.OrdinalIgnoreCase))
        {
            try { ExecuteTask(directive[5..].Trim()); }
            catch (Exception e) { Utils.ThrowException(e); WriteOut("ERR task failed"); }
            return;
        }

        // Layer C: ベント出入り(RpcEnterVent/RpcExitVent 直呼び。使用可否判定は挟まない実機検証口)。
        if (directive.StartsWith("vent ", StringComparison.OrdinalIgnoreCase))
        {
            try { ExecuteVent(directive[5..].Trim()); }
            catch (Exception e) { Utils.ThrowException(e); WriteOut("ERR vent failed"); }
            return;
        }

        // Layer C2: 歩行移動。tp と違い通常の移動パケット(client-authoritative)を出すので、
        // 公式サーバーの anticheat が見るものと同じ「本物の挙動」でマルチプレイ in-task テストができる。
        if (directive.StartsWith("walk ", StringComparison.OrdinalIgnoreCase))
        {
            try { ExecuteWalk(directive[5..].Trim()); }
            catch (Exception e) { Utils.ThrowException(e); WriteOut("ERR walk failed"); }
            return;
        }

        // Layer C3: 会議投票。
        if (directive.StartsWith("vote ", StringComparison.OrdinalIgnoreCase))
        {
            try { ExecuteVote(directive[5..].Trim()); }
            catch (Exception e) { Utils.ThrowException(e); WriteOut("ERR vote failed"); }
            return;
        }

        // Layer C3b: バニラ Judge 木槌演出つき強制追放の実機検証口 (JudgeGavelPresenter 経由)。
        if (directive.StartsWith("overrule ", StringComparison.OrdinalIgnoreCase))
        {
            try { ExecuteOverrule(directive[9..].Trim()); }
            catch (Exception e) { Utils.ThrowException(e); WriteOut("ERR overrule failed"); }
            return;
        }

        // Layer C4: 実チャット送信(SYS のホストローカル表示でなく、他クライアントにも見える通常チャット)。
        if (directive.StartsWith("chat ", StringComparison.OrdinalIgnoreCase))
        {
            try { ExecuteChat(directive[5..].Trim()); }
            catch (Exception e) { Utils.ThrowException(e); WriteOut("ERR chat failed"); }
            return;
        }

        // Layer D: 直近のエラー/例外を out.log へ転写(in-proc リングバッファ)。
        if (directive.Equals("errors", StringComparison.OrdinalIgnoreCase) || directive.StartsWith("errors ", StringComparison.OrdinalIgnoreCase))
        {
            try { ExecuteErrors(directive.Length > 6 ? directive[7..].Trim() : ""); }
            catch (Exception e) { Utils.ThrowException(e); WriteOut("ERR errors failed"); }
            return;
        }

        // Layer D2: 全レベルのログリングをゲーム内 grep(発火マーカー確認を out.log 1チャンネルで完結させる)。
        if (directive.Equals("grep", StringComparison.OrdinalIgnoreCase) || directive.StartsWith("grep ", StringComparison.OrdinalIgnoreCase))
        {
            try { ExecuteGrep(directive.Length > 4 ? directive[5..].Trim() : ""); }
            catch (Exception e) { Utils.ThrowException(e); WriteOut("ERR grep failed"); }
            return;
        }

        // Layer D3: il2cpp (Boehm) 側の型別生存オブジェクト census を手動発火する。
        if (directive.Equals("bcensus", StringComparison.OrdinalIgnoreCase))
        {
            try { ExecuteBcensus(); }
            catch (Exception e) { Utils.ThrowException(e); WriteOut("ERR bcensus failed"); }
            return;
        }

        // Layer D4: CLR gen0 GC / CLR フル GC / il2cpp (Boehm) GC を片側ずつ強制発火する
        // ヒッチ帰属の因果分離用ディレクティブ。
        if (directive.StartsWith("gc ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                string mode = directive[3..].Trim().ToLowerInvariant();
                string r = GcPrepass.ProbeCollect(mode);
                WriteOut(r.StartsWith("ERR", StringComparison.Ordinal) ? r : "OK " + r);
            }
            catch (Exception e) { Utils.ThrowException(e); WriteOut("ERR gc failed"); }
            return;
        }

        // cpusets show|off|auto|cache:N — CPU Sets の実行時切替 (A/B 用・再起動不要)。
        if (directive.Equals("cpusets", StringComparison.OrdinalIgnoreCase) || directive.StartsWith("cpusets ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                string arg = directive.Length > 7 ? directive[8..].Trim() : "show";
                string res = arg.Equals("show", StringComparison.OrdinalIgnoreCase) ? CpuSetsOptimizer.Show() : CpuSetsOptimizer.Apply(arg);
                WriteOut(res.StartsWith("ERR") ? res : res.StartsWith("SKIP") ? "SKIP cpusets " + res[5..] : "OK cpusets " + res);
            }
            catch (Exception e) { Utils.ThrowException(e); WriteOut("ERR cpusets failed"); }
            return;
        }

        // Layer E: 待ち合わせ。条件成立 or timeout まで後続ディレクティブの実行を停める(1/sec 評価)。
        if (directive.Equals("wait", StringComparison.OrdinalIgnoreCase) || directive.StartsWith("wait ", StringComparison.OrdinalIgnoreCase))
        {
            try { ExecuteWait(directive.Length > 4 ? directive[5..].Trim() : ""); }
            catch (Exception e) { Utils.ThrowException(e); WriteOut("ERR wait failed"); }
            return;
        }

        // sleep N — 後続を N 秒停めるだけの純粋ペーシング(click チェーンの画面遷移待ちに必須。wait の糖衣)。
        if (directive.StartsWith("sleep ", StringComparison.OrdinalIgnoreCase))
        {
            try { ExecuteSleep(directive[6..].Trim()); }
            catch (Exception e) { Utils.ThrowException(e); WriteOut("ERR sleep failed"); }
            return;
        }

        if (directive.Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            WriteOut("HELP directives: state | screenshot | delayshot <ms> | burst <count> [everyNFrames] | rdump [filter] | click <h|label:x> | press <h|x y> | scroll <x> <y> <notches> | type <text> | key <enter|escape|tab|backspace> | getopt <pattern> | setopt <name|#id> <idx|on|off|~real> | forcerole <id|name|host|clear> [EnumName] | start | hostlobby | leavelobby | eosstall | autostart <on|off> | tp <x> <y> | tp <playerId> | tp <playerId|name> <x> <y> | switch <0-4> [playerId|name] | walk <x> <y> | walk <playerId> | walk stop | vote <playerId|skip> | vote <voterId> <playerId|skip> | overrule <targetId> [judgeId] | chat <text> | chatui <text> | sabotage <comms|reactor|o2|lights|lab|heli|mushroom|cd0> | fixsabotage <type> | use <kill|vent|pet|ability|report|sabotage> | vent enter <id> | vent exit | errors [n] | grep <pattern> [n] | bcensus | gc <clr|clr2|boehm|both> | sleep <sec> | wait <phase=X|players=N|marker:text|join|arrived> [timeoutSec] | wait cancel | /<chatcommand>");
            return;
        }

        if (!directive.StartsWith('/'))
        {
            WriteOut("ERR unknown directive");
            return;
        }

        try
        {
            if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost || !PlayerControl.LocalPlayer)
            {
                WriteOut("ERR not host");
                return;
            }

            PlayerControl pc = PlayerControl.LocalPlayer;
            Command matched = Command.AllCommands.FirstOrDefault(c => c.IsThisCommand(directive));

            if (matched == null)
            {
                WriteOut("ERR unknown command");
                return;
            }

            if (!matched.CanUseCommand(pc))
            {
                WriteOut($"BLOCKED {matched.Key}");
                return;
            }

            matched.Action(pc, directive, directive.Split(' '));
            WriteOut($"OK {matched.Key}");
        }
        catch (Exception e)
        {
            Utils.ThrowException(e);
            WriteOut("ERR exception");
        }
    }

    private static void HandleAutoScreenshot()
    {
        if (Main.TestBridgeAutoScreenshot is not { Value: true }) return;

        long now = Utils.TimeStamp;
        int interval = Math.Max(1, Main.TestBridgeScreenshotInterval?.Value ?? 20);

        if (now - _lastAutoShotTs < interval) return;

        if (RequestScreenshot("auto")) _lastAutoShotTs = now;
    }

    // delayshot <ms> — ms ミリ秒後にプロセス内スクショを 1 枚撮る。
    // 使い方: 窓を開く操作の「直前」にこれを予約してから操作を撃つ
    // (例: `delayshot 800` → `vote 1 skip` で、開票アニメの最中にシャッターが落ちる)。
    // ディレクティブは 1 tick に 1 行ずつ実行されるので、予約と操作の間隔は 20ms 程度しかずれない。
    private static void ExecuteDelayShot(string rest)
    {
        if (!int.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ms))
        {
            WriteOut("ERR delayshot usage: delayshot <ms>");
            return;
        }

        ms = Math.Clamp(ms, 0, 10000);

        LateTask.New(() =>
        {
            if (!RequestScreenshot($"delayshot+{ms}ms")) WriteOut("ERR delayshot busy");
        }, ms / 1000f, "TestBridge.DelayShot", log: false);

        WriteOut($"OK delayshot scheduled in {ms}ms");
    }

    // burst <枚数> [何フレームおき=1] — 連続フレームを JPEG で保存する。上限 120 枚。
    // 通常スクショの保持枚数 (PruneOldScreenshots) とは別フォルダなので消されない。
    private static void ExecuteBurst(string rest)
    {
        string[] parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int count))
        {
            WriteOut("ERR burst usage: burst <count> [everyNFrames]");
            return;
        }

        int every = 1;
        if (parts.Length > 1 && !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out every)) every = 1;

        count = Math.Clamp(count, 1, 120);
        every = Math.Clamp(every, 1, 60);

        if (_captureInFlight || Main.Instance == null)
        {
            WriteOut("ERR burst busy");
            return;
        }

        string folder = $"burst_{Utils.TimeStamp}";
        string dir = Path.Combine(_screensDir, folder);
        Directory.CreateDirectory(dir);

        _captureInFlight = true;

        try { Main.Instance.StartCoroutine(BurstCoroutine(dir, folder, count, every)); }
        catch (Exception e)
        {
            Utils.ThrowException(e);
            _captureInFlight = false;
            WriteOut("ERR burst start failed");
            return;
        }

        WriteOut($"OK burst started {count} frames every {every} -> Screens/{folder}/");
    }

    private static IEnumerator BurstCoroutine(string dir, string folder, int count, int every)
    {
        int saved = 0;
        float start = Time.realtimeSinceStartup;

        for (int i = 0; i < count; i++)
        {
            for (int k = 1; k < every; k++) yield return null;
            yield return new WaitForEndOfFrame();

            Texture2D tex = null;

            try
            {
                int w = Screen.width;
                int h = Screen.height;
                if (w <= 0 || h <= 0) break;

                tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();

                byte[] bytes = Il2CppBytesToManaged(tex.EncodeToJPG(70));

                if (bytes is { Length: > 0 })
                {
                    float t = Time.realtimeSinceStartup - start;
                    File.WriteAllBytes(Path.Combine(dir, $"{i:D3}_{(int)(t * 1000):D6}ms.jpg"), bytes);
                    saved++;
                }
            }
            catch (Exception e)
            {
                Utils.ThrowException(e);
                break;
            }
            finally
            {
                if (tex) Object.Destroy(tex);
            }
        }

        _captureInFlight = false;
        WriteOut($"burst done {saved}/{count} -> Screens/{folder}/");
    }

    // rdump [名前フィルタ] — カメラ設定と全 SpriteRenderer (非アクティブ含む) を rdump.txt に書く。
    private static void ExecuteRendererDump(string filter)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# rdump {DateTime.Now:yyyy-MM-dd HH:mm:ss} map={(ShipStatus.Instance ? ShipStatus.Instance.name : "none")} filter='{filter}'");

        foreach (Camera cam in Camera.allCameras)
        {
            if (!cam) continue;
            Vector3 p = cam.transform.position;
            sb.AppendLine($"CAM {GetPath(cam.transform)} depth={cam.depth} clear={cam.clearFlags} bg={ColorStr(cam.backgroundColor)} mask=0x{cam.cullingMask:X} ortho={cam.orthographic}/{cam.orthographicSize:0.##} near={cam.nearClipPlane:0.##} far={cam.farClipPlane:0.##} pos=({p.x:0.##},{p.y:0.##},{p.z:0.##})");
        }

        var shaders = new List<string>();
        foreach (Object o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<Shader>()))
        {
            Shader s = o != null ? o.TryCast<Shader>() : null;
            if (s) shaders.Add(s.name);
        }
        sb.AppendLine($"SHADERS {string.Join(" | ", shaders.Distinct().OrderBy(x => x))}");

        int n = 0;

        foreach (SpriteRenderer sr in Object.FindObjectsOfType<SpriteRenderer>(true))
        {
            if (!sr) continue;

            string path = GetPath(sr.transform);
            if (filter.Length > 0 && !path.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;

            Bounds b = sr.bounds;
            Vector3 wp = sr.transform.position;
            string sprite = sr.sprite ? sr.sprite.name : "-";
            string shader = sr.sharedMaterial && sr.sharedMaterial.shader ? sr.sharedMaterial.shader.name : "-";

            var comps = new List<string>();
            foreach (MonoBehaviour mb in sr.GetComponents<MonoBehaviour>())
                if (mb) comps.Add(mb.GetIl2CppType().Name);

            sb.AppendLine($"SR {path} act={sr.gameObject.activeInHierarchy} en={sr.enabled} layer={sr.gameObject.layer} sl={sr.sortingLayerID} so={sr.sortingOrder} z={wp.z:0.###} " +
                          $"ctr=({b.center.x:0.##},{b.center.y:0.##}) size=({b.size.x:0.##},{b.size.y:0.##}) col={ColorStr(sr.color)} sprite={sprite} shader={shader} draw={sr.drawMode} comps=[{string.Join(",", comps)}]");
            n++;
        }

        string outPath = Path.Combine(_dir, "rdump.txt");
        File.WriteAllText(outPath, sb.ToString(), Encoding.UTF8);
        WriteOut($"OK rdump {n} renderers -> rdump.txt");

        static string ColorStr(Color c) => $"{c.r:0.##}/{c.g:0.##}/{c.b:0.##}/{c.a:0.##}";

        static string GetPath(Transform t)
        {
            string s = t.name;
            for (Transform p = t.parent; p; p = p.parent) s = p.name + "/" + s;
            return s;
        }
    }

    private static bool RequestScreenshot(string reason)
    {
        if (_captureInFlight) return false;
        if (Main.Instance == null) return false;

        _captureInFlight = true;

        try { Main.Instance.StartCoroutine(CaptureCoroutine(reason)); }
        catch (Exception e)
        {
            Utils.ThrowException(e);
            _captureInFlight = false;
            return false;
        }

        return true;
    }

    private static IEnumerator CaptureCoroutine(string reason)
    {
        yield return new WaitForEndOfFrame();

        try { DoCapture(reason); }
        catch (Exception e) { Utils.ThrowException(e); }
        finally { _captureInFlight = false; }
    }

    private static void DoCapture(string reason)
    {
        Texture2D tex = null;

        try
        {
            int w = Screen.width;
            int h = Screen.height;

            if (w <= 0 || h <= 0)
            {
                WriteOut("ERR screenshot invalid screen size");
                return;
            }

            tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();

            byte[] bytes = null;
            string ext = "jpg";

            try { bytes = Il2CppBytesToManaged(tex.EncodeToJPG(75)); }
            catch (Exception e) { Utils.ThrowException(e); bytes = null; }

            if (bytes == null || bytes.Length == 0)
            {
                try
                {
                    bytes = Il2CppBytesToManaged(tex.EncodeToPNG());
                    ext = "png";
                }
                catch (Exception e) { Utils.ThrowException(e); bytes = null; }
            }

            if (bytes == null || bytes.Length == 0)
            {
                WriteOut("ERR screenshot encode failed");
                return;
            }

            SaveScreenshotBytes(bytes, ext, reason);
        }
        finally
        {
            if (tex) Object.Destroy(tex);
        }
    }

    private static void SaveScreenshotBytes(byte[] bytes, string ext, string reason)
    {
        try
        {
            long ts = Utils.TimeStamp;
            string state = HealthLog.GetState();
            if (string.IsNullOrEmpty(state)) state = "?";

            string fileName = $"{ts}_{state}.{ext}";
            string path = Path.Combine(_screensDir, fileName);

            File.WriteAllBytes(path, bytes);

            PruneOldScreenshots();

            WriteOut($"screenshot ({reason}) -> Screens/{fileName}");
        }
        catch (Exception e)
        {
            Utils.ThrowException(e);
            WriteOut("ERR screenshot save failed");
        }
    }

    private static void PruneOldScreenshots()
    {
        try
        {
            int keep = Math.Max(1, Main.TestBridgeScreenshotKeep?.Value ?? 30);

            List<FileInfo> files = [.. new DirectoryInfo(_screensDir).GetFiles().OrderByDescending(f => f.CreationTimeUtc)];

            for (int i = keep; i < files.Count; i++)
            {
                try { files[i].Delete(); }
                catch { }
            }
        }
        catch { }
    }

    // Il2CppStructArray<byte> -> managed byte[]。Utils.cs の LoadTextureFromResources(4908-4911) の
    // 対称形(Pointer + IntPtr.Size*4 を Span で見て CopyTo)。per-element indexer は使わない(遅い上に罠あり)。
    private static unsafe byte[] Il2CppBytesToManaged(Il2CppStructArray<byte> arr)
    {
        if (arr == null) return null;

        int len = arr.Length;
        if (len <= 0) return [];

        byte[] managed = new byte[len];
        new Span<byte>(IntPtr.Add(arr.Pointer, IntPtr.Size * 4).ToPointer(), len).CopyTo(managed);
        return managed;
    }

    // ── Layer 1: 構造化スナップショット ─────────────────────────────────

    // シーン上の PassiveButton を「列挙順に依存しない安定 handle」付きで返す。
    // (name, x, y) でソートしてから採番するので、snapshot と click で同じ handle になる。
    private sealed class BtnRec
    {
        public PassiveButton Pb;
        public string Name;
        public string Label;
        public bool Active;
        public float X;
        public float Y;
        public string Handle;
    }

    private static List<BtnRec> EnumerateButtons()
    {
        var list = new List<BtnRec>();

        Il2CppArrayBase<PassiveButton> all;
        try { all = Object.FindObjectsOfType<PassiveButton>(true); }
        catch { return list; }

        if (all == null) return list;

        foreach (PassiveButton pb in all)
        {
            if (!pb) continue;

            try
            {
                bool active = pb.gameObject.activeInHierarchy && pb.isActiveAndEnabled;

                string label = "";
                try
                {
                    var tmp = pb.GetComponentInChildren<TMP_Text>(true);
                    if (tmp != null) label = CleanLabel(tmp.text);
                }
                catch { }

                Vector3 wp = pb.transform.position;
                list.Add(new BtnRec { Pb = pb, Name = pb.name ?? "", Label = label, Active = active, X = wp.x, Y = wp.y });
            }
            catch { }
        }

        list.Sort((a, b) =>
        {
            int c = string.CompareOrdinal(a.Name, b.Name);
            if (c != 0) return c;
            c = a.X.CompareTo(b.X);
            return c != 0 ? c : a.Y.CompareTo(b.Y);
        });

        var counts = new Dictionary<string, int>();
        foreach (BtnRec r in list)
        {
            string basis = Sanitize(r.Name);
            if (basis.Length == 0) basis = "btn";

            if (counts.TryGetValue(basis, out int n))
            {
                counts[basis] = n + 1;
                r.Handle = $"{basis}~{n + 1}";
            }
            else
            {
                counts[basis] = 1;
                r.Handle = basis;
            }
        }

        return list;
    }

    private static void WriteState()
    {
        List<BtnRec> buttons = EnumerateButtons();

        var sb = new StringBuilder(8192);
        sb.Append('{');
        sb.Append("\"ts\":").Append(Utils.TimeStamp.ToString(CultureInfo.InvariantCulture)).Append(',');
        sb.Append("\"phase\":").Append(JStr(SafeState())).Append(',');
        sb.Append("\"gameMode\":").Append(JStr(SafeGameMode())).Append(',');
        sb.Append("\"errorsTotal\":").Append(TotalErrorsRecorded).Append(',');
        sb.Append("\"local\":"); AppendLocal(sb); sb.Append(',');
        sb.Append("\"code\":"); AppendGameCode(sb); sb.Append(','); // ルームコード(未接続は null)。スクショから読む往復を潰す
        sb.Append("\"players\":["); int np = AppendPlayers(sb); sb.Append("],");
        sb.Append("\"cnos\":["); int nc = AppendCnos(sb); sb.Append("],");
        sb.Append("\"vents\":["); int nv = AppendVents(sb); sb.Append("],");
        sb.Append("\"hud\":"); AppendHud(sb); sb.Append(',');
        sb.Append("\"walk\":"); AppendWalk(sb); sb.Append(',');
        sb.Append("\"lastDisconnect\":"); AppendLastDisconnect(sb); sb.Append(',');
        sb.Append("\"ui\":["); int nb = AppendButtons(sb, buttons); sb.Append(']');
        sb.Append('}');

        File.WriteAllText(_statePath, sb.ToString());
        WriteOut($"OK state ({np} players, {nc} cnos, {nv} vents, {nb} buttons) -> bridge-state.json");
    }

    private static void AppendLocal(StringBuilder sb)
    {
        PlayerControl lp = PlayerControl.LocalPlayer;
        if (!lp) { sb.Append("null"); return; }

        Vector2 p = SafePos(lp);
        float kt = 0f;
        try { kt = lp.killTimer; } catch { }

        sb.Append('{');
        sb.Append("\"id\":").Append(lp.PlayerId).Append(',');
        sb.Append("\"name\":").Append(JStr(SafeName(lp))).Append(',');
        sb.Append("\"role\":").Append(JStr(SafeRole(lp))).Append(',');
        sb.Append("\"alive\":").Append(SafeAlive(lp) ? "true" : "false").Append(',');
        sb.Append("\"pos\":[").Append(F(p.x)).Append(',').Append(F(p.y)).Append("],");
        sb.Append("\"killTimer\":").Append(F(kt));
        sb.Append('}');
    }

    private static int AppendPlayers(StringBuilder sb)
    {
        IReadOnlyList<PlayerControl> all;
        try { all = Main.AllPlayerControls; } catch { all = null; }
        if (all == null) return 0;

        int count = 0;
        foreach (PlayerControl pc in all)
        {
            if (!pc) continue;

            Vector2 p = SafePos(pc);
            if (count > 0) sb.Append(',');

            sb.Append('{');
            sb.Append("\"id\":").Append(pc.PlayerId).Append(',');
            sb.Append("\"name\":").Append(JStr(SafeName(pc))).Append(',');
            sb.Append("\"role\":").Append(JStr(SafeRole(pc))).Append(',');
            sb.Append("\"alive\":").Append(SafeAlive(pc) ? "true" : "false").Append(',');
            sb.Append("\"client\":").Append(SafeClientId(pc)).Append(','); // PLAYERJOINED の client id と突合してエミュ台↔playerId を特定する(id は join し直しで入れ替わる)
            sb.Append("\"color\":").Append(SafeColorId(pc)).Append(',');
            sb.Append("\"pos\":[").Append(F(p.x)).Append(',').Append(F(p.y)).Append(']');
            sb.Append('}');
            count++;
        }

        return count;
    }

    // CNO 観測: 「CNO が host 側で spawn したか」の機械判定用。
    // Sprite は private なので reflection で長さだけ覗く(state 呼び出し時のみ、常時コストなし)。
    private static readonly System.Reflection.FieldInfo CnoSpriteField =
        typeof(CustomNetObject).GetField("Sprite", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

    private static int AppendCnos(StringBuilder sb)
    {
        List<CustomNetObject> all;
        try { all = [.. CustomNetObject.AllObjects]; } catch { return 0; }

        const int cap = 100;
        int count = 0;

        foreach (CustomNetObject cno in all)
        {
            if (count >= cap) break;
            if (cno == null) continue;

            try
            {
                int spriteLen = -1;
                try { spriteLen = (CnoSpriteField?.GetValue(cno) as string)?.Length ?? -1; } catch { }

                bool hasPc = false;
                uint netId = 0;
                byte pcId = 0;

                try
                {
                    hasPc = cno.playerControl;
                    if (hasPc)
                    {
                        netId = cno.playerControl.NetId;
                        pcId = cno.playerControl.PlayerId;
                    }
                }
                catch { }

                if (count > 0) sb.Append(',');
                sb.Append('{');
                sb.Append("\"type\":").Append(JStr(cno.GetType().Name)).Append(',');
                sb.Append("\"pos\":[").Append(F(cno.Position.x)).Append(',').Append(F(cno.Position.y)).Append("],");
                sb.Append("\"alive\":").Append(hasPc ? "true" : "false").Append(',');
                sb.Append("\"netId\":").Append(netId).Append(',');
                sb.Append("\"playerId\":").Append(pcId).Append(',');
                sb.Append("\"spriteLen\":").Append(spriteLen);
                sb.Append('}');
                count++;
            }
            catch { }
        }

        return count;
    }

    // vent enter/exit ディレクティブが指定できる id の一覧。cnos と同じ「例外時は0件で JSON は常に整形式」方針。
    private static int AppendVents(StringBuilder sb)
    {
        Il2CppReferenceArray<Vent> all;
        try { all = ShipStatus.Instance?.AllVents; } catch { return 0; }
        if (all == null) return 0;

        int count = 0;
        foreach (Vent v in all)
        {
            // cnos と同じ per-element 防御: 値の取得を try で済ませてから append する
            // (append 途中で例外を出すと JSON が半端に切れる — 取得と書き出しを分離)。
            int id;
            float px, py;

            try
            {
                if (!v) continue;
                id = v.Id;
                Vector3 wp = v.transform.position;
                px = wp.x;
                py = wp.y;
            }
            catch { continue; }

            if (count > 0) sb.Append(',');

            sb.Append('{');
            sb.Append("\"id\":").Append(id).Append(',');
            sb.Append("\"pos\":[").Append(F(px)).Append(',').Append(F(py)).Append(']');
            sb.Append('}');
            count++;
        }

        return count;
    }

    private static void AppendWalk(StringBuilder sb)
    {
        if (_walkTarget is not { } t) { sb.Append("null"); return; }
        sb.Append("{\"target\":[").Append(F(t.x)).Append(',').Append(F(t.y)).Append("],\"elapsed\":").Append(F(_walkTotalTime)).Append('}');
    }

    private static void AppendLastDisconnect(StringBuilder sb)
    {
        if (_lastDisconnect == null) { sb.Append("null"); return; }
        sb.Append("{\"reason\":").Append(JStr(_lastDisconnect)).Append(",\"ts\":").Append(_lastDisconnectTs.ToString(CultureInfo.InvariantCulture)).Append('}');
    }

    private static void AppendHud(StringBuilder sb)
    {
        if (!HudManager.InstanceExists) { sb.Append("null"); return; }

        HudManager hud = HudManager.Instance;

        sb.Append('{');
        AppendHudButton(sb, "kill", hud.KillButton); sb.Append(',');
        AppendHudButton(sb, "vent", hud.ImpostorVentButton); sb.Append(',');
        AppendHudButton(sb, "pet", hud.PetButton); sb.Append(',');
        AppendHudButton(sb, "ability", hud.AbilityButton); sb.Append(',');
        AppendHudButton(sb, "report", hud.ReportButton); sb.Append(',');
        AppendHudButton(sb, "sabotage", hud.SabotageButton); sb.Append(',');
        // kill/pet/ability の false がイントロ明けの PreventKill 窓によるものかを添える (偽陰性の判定材料)
        sb.Append("\"preventKill\":").Append(IntroCutsceneDestroyPatch.PreventKill ? "true" : "false");
        sb.Append('}');
    }

    private static void AppendHudButton(StringBuilder sb, string key, ActionButton btn)
    {
        bool usable = false;
        try { usable = btn && btn.isActiveAndEnabled; } catch { }
        sb.Append('"').Append(key).Append("\":").Append(usable ? "true" : "false");
    }

    private static int AppendButtons(StringBuilder sb, List<BtnRec> buttons)
    {
        // Menu シーン等はボタン総数が cap を超える(2026-07-05 実測 250+)。ナイーブに先頭から
        // 出すと active なボタン(=click 対象)が JSON から切り落とされるため、active を先に出す。
        // handle は全数ソート済みリストで採番済みなので、出力順を変えても click との対応は不変。
        const int cap = 250;
        int count = 0;

        for (int pass = 0; pass < 2 && count < cap; pass++)
        {
            bool wantActive = pass == 0;

            for (int i = 0; i < buttons.Count && count < cap; i++)
            {
                BtnRec b = buttons[i];
                if (b.Active != wantActive) continue;

                if (count > 0) sb.Append(',');

                sb.Append('{');
                sb.Append("\"h\":").Append(JStr(b.Handle)).Append(',');
                sb.Append("\"name\":").Append(JStr(b.Name)).Append(',');
                sb.Append("\"label\":").Append(JStr(b.Label)).Append(',');
                sb.Append("\"active\":").Append(b.Active ? "true" : "false").Append(',');
                sb.Append("\"pos\":[").Append(F(b.X)).Append(',').Append(F(b.Y)).Append(']');
                sb.Append('}');
                count++;
            }
        }

        return count;
    }

    // ── Layer 3: PassiveButton クリック ────────────────────────────────

    private static void ExecuteClick(string selector)
    {
        if (string.IsNullOrEmpty(selector)) { WriteOut("ERR click needs a handle"); return; }

        List<BtnRec> buttons = EnumerateButtons();
        BtnRec target;

        if (selector.StartsWith("label:", StringComparison.OrdinalIgnoreCase))
        {
            string want = selector[6..].Trim();
            target = buttons.FirstOrDefault(b => b.Active && string.Equals(b.Label, want, StringComparison.OrdinalIgnoreCase));

            if (target == null)
            {
                // 完全一致なし → 一意な部分一致にフォールバック (TMP ラベルが翻訳キーのままのボタンがある。
                // 2件以上当たったら曖昧なので実行せず候補を返す)
                List<BtnRec> partial = buttons.Where(b => b.Active && b.Label != null && b.Label.Contains(want, StringComparison.OrdinalIgnoreCase)).ToList();

                if (partial.Count == 1)
                    target = partial[0];
                else if (partial.Count > 1)
                {
                    WriteOut($"ERR click ambiguous label \"{want}\": {string.Join(", ", partial.Take(8).Select(b => b.Handle))}");
                    return;
                }
            }
        }
        else
        {
            target = buttons.FirstOrDefault(b => string.Equals(b.Handle, selector, StringComparison.OrdinalIgnoreCase));
        }

        if (target == null) { WriteOut($"ERR click no match: {selector}"); return; }
        if (!target.Active) { WriteOut($"ERR click inactive: {selector}"); return; }
        if (!target.Pb) { WriteOut($"ERR click destroyed: {selector}"); return; }

        try
        {
            target.Pb.OnClick.Invoke();
            WriteOut($"OK click {target.Handle} ({target.Label})");
        }
        catch (Exception e)
        {
            Utils.ThrowException(e);
            WriteOut("ERR click invoke threw");
        }
    }

    // ── Layer 3b: OS レベルのマウス注入 ─────────────────────────────────
    // click(PassiveButton.OnClick.Invoke 直呼び)は AU が実行時に動的 AddListener する UI
    // (会議の投票確認チェック、PlayerVoteArea の Select 連鎖等)を発火できない。ここは本物の
    // OS マウスイベントを注入して Unity から見て人間の操作と区別が付かない形にする。

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Win32Point
    {
        public int X;
        public int Y;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Win32Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint MouseEventWheel = 0x0800;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Win32Point lpPoint);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Win32Rect lpRect);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int nWidth, int nHeight, bool bRepaint);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref Win32Point lpPoint);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, IntPtr dwExtraInfo);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out Win32Rect lpRect);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private static EnumWindowsProc _enumWindowsProc;
    private static uint _ownProcessId;
    private static readonly List<IntPtr> WindowScratch = [];

    private static bool CollectProcessWindow(IntPtr hWnd, IntPtr lParam)
    {
        try
        {
            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == _ownProcessId && IsWindowVisible(hWnd)) WindowScratch.Add(hWnd);
        }
        catch { }

        return true;
    }

    private static string ClassNameOf(IntPtr hWnd)
    {
        try
        {
            var sb = new StringBuilder(96);
            return GetClassName(hWnd, sb, sb.Capacity) == 0 ? string.Empty : sb.ToString();
        }
        catch { return string.Empty; }
    }

    // Process.MainWindowHandle は「タイトルを持つ最初のトップレベル窓」なので、レンダーウィンドウ以外を
    // 掴むことがある(その窓のクライアント矩形へ換算すると注入が全く別の場所に落ちる)。プロセス内の
    // 可視トップレベル窓を列挙し、Unity のバックバッファ(Screen.width/height)と寸法が一致する窓 >
    // Unity のウィンドウクラス > 面積最大、の順で本命を選ぶ。
    // 解決結果はキャッシュしない — 列挙が一度でも空振りした瞬間 (シーン遷移などで可視窓が無い間) の
    // フォールバック値を握ると、まさに直したい「別窓を掴んだ状態」がセッション中ずっと固定される。
    private static IntPtr ResolveGameWindow(out Win32Rect client, out string className)
    {
        client = default;
        className = string.Empty;

        if (_ownProcessId == 0)
        {
            try { _ownProcessId = (uint)System.Diagnostics.Process.GetCurrentProcess().Id; }
            catch { return IntPtr.Zero; }
        }

        WindowScratch.Clear();
        _enumWindowsProc ??= CollectProcessWindow;

        try { EnumWindows(_enumWindowsProc, IntPtr.Zero); }
        catch (Exception e) { Utils.ThrowException(e); }

        int screenW = Screen.width;
        int screenH = Screen.height;
        IntPtr best = IntPtr.Zero;
        long bestScore = long.MinValue;
        Win32Rect bestRect = default;

        foreach (IntPtr h in WindowScratch)
        {
            if (!GetClientRect(h, out Win32Rect r)) continue;

            int w = r.Right - r.Left;
            int ht = r.Bottom - r.Top;
            if (w <= 0 || ht <= 0) continue;

            string cls = ClassNameOf(h);

            long score = (long)w * ht;
            if (w == screenW && ht == screenH) score += 1_000_000_000L;
            if (cls.Contains("Unity", StringComparison.OrdinalIgnoreCase)) score += 100_000_000L;

            if (score <= bestScore) continue;

            bestScore = score;
            best = h;
            bestRect = r;
            className = cls;
        }

        if (best == IntPtr.Zero)
        {
            try { best = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle; }
            catch { return IntPtr.Zero; }

            if (best == IntPtr.Zero || !GetClientRect(best, out bestRect)) return IntPtr.Zero;

            className = ClassNameOf(best) + "|fallback";
        }

        client = bestRect;
        return best;
    }

    // press <handle> — state の ui[].h から world 座標を解決して押す。
    // press <x> <y> — screenshot 画像座標(top-down, クライアント原点)をそのままクライアント座標として押す。
    private static void ExecutePress(string rest)
    {
        if (!OperatingSystem.IsWindows()) { WriteOut("ERR press windows only"); return; }
        if (string.IsNullOrEmpty(rest)) { WriteOut("ERR press usage: press <handle> | press <x> <y>"); return; }

        string[] parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int clientX, clientY;

        if (parts.Length == 1)
        {
            List<BtnRec> buttons = EnumerateButtons();
            BtnRec target = buttons.FirstOrDefault(b => string.Equals(b.Handle, parts[0], StringComparison.OrdinalIgnoreCase));

            if (target == null) { WriteOut($"ERR press no match: {parts[0]}"); return; }
            if (!target.Active || !target.Pb) { WriteOut($"ERR press inactive/destroyed: {parts[0]}"); return; }

            // HUD/メニュー系 PassiveButton は UICamera (固定投影) が描画する。Camera.main はズーム/追従で
            // 投影が変わるゲームプレイカメラなので、ボタンのレイヤーを cullingMask に含むカメラを選ぶ
            // (同レイヤーを複数カメラが含む場合は UI カメラ優先)。Camera.main 固定だとズーム中に
            // 「別の場所を静かに押す」誤操作になる。
            Camera cam = null;
            int layerBit = 1 << target.Pb.gameObject.layer;

            foreach (Camera c in Camera.allCameras)
            {
                if (!c || !c.isActiveAndEnabled || (c.cullingMask & layerBit) == 0) continue;
                if (cam == null) cam = c;
                if (c.name.Contains("UI", StringComparison.OrdinalIgnoreCase)) { cam = c; break; }
            }

            if (!cam) cam = Camera.main;
            if (!cam) { WriteOut("ERR press no camera renders this button"); return; }

            Vector3 sp = cam.WorldToScreenPoint(target.Pb.transform.position);
            clientX = (int)sp.x;
            clientY = Screen.height - (int)sp.y; // Unity の screen 座標は左下原点 -> client(top-down) へ反転
        }
        else if (parts.Length == 2 &&
                 float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float fx) &&
                 float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float fy))
        {
            clientX = (int)fx;
            clientY = (int)fy;
        }
        else
        {
            WriteOut("ERR press usage: press <handle> | press <x> <y>");
            return;
        }

        IntPtr hWnd = ResolveGameWindow(out Win32Rect clientRect, out string winClass);
        if (hWnd == IntPtr.Zero) { WriteOut("ERR press no window handle"); return; }

        int clientW = clientRect.Right - clientRect.Left;
        int clientH = clientRect.Bottom - clientRect.Top;
        int screenW = Screen.width;
        int screenH = Screen.height;

        string geom = $"win=0x{hWnd.ToInt64():X} class={(winClass.Length > 0 ? winClass : "?")} client={clientW}x{clientH} screen={screenW}x{screenH}";

        // 範囲チェックは換算「前」に、座標の出所と同じバックバッファ基準で行う。換算後の値は定義上
        // クライアント矩形に収まるので、そちらで見ると当たり判定の外れを永久に検出できない。
        if (screenW > 0 && screenH > 0 && (clientX < 0 || clientY < 0 || clientX >= screenW || clientY >= screenH))
            WriteOut($"WARN press target outside the captured frame: [{clientX}, {clientY}] {geom}");

        // 座標の出所(screenshot / WorldToScreenPoint)はどちらも Unity のバックバッファ基準だが、注入は
        // ウィンドウのクライアント座標で行う。DPI スケーリングやレンダースケールで両者の寸法は一致しない
        // ことがあるため実測比で換算する(一致していれば係数 1 で素通り)。
        if (screenW > 0 && screenH > 0 && clientW > 0 && clientH > 0 && (clientW != screenW || clientH != screenH))
        {
            clientX = (int)Math.Round(clientX * (double)clientW / screenW);
            clientY = (int)Math.Round(clientY * (double)clientH / screenH);
            geom += " scaled";
        }

        try { SetForegroundWindow(hWnd); } catch { }

        var clientPoint = new Win32Point { X = clientX, Y = clientY };
        if (!ClientToScreen(hWnd, ref clientPoint)) { WriteOut("ERR press ClientToScreen failed"); return; }

        var moved = false;
        Win32Rect originalRect = default;

        try
        {
            SetCursorPos(clientPoint.X, clientPoint.Y);
            GetCursorPos(out Win32Point actual);

            if (actual.X != clientPoint.X || actual.Y != clientPoint.Y)
            {
                // 画面外ウィンドウでカーソルが仮想スクリーン境界にクリップされる既知の罠への構造対策。
                if (!GetWindowRect(hWnd, out originalRect)) { WriteOut("ERR press GetWindowRect failed"); return; }

                int width = originalRect.Right - originalRect.Left;
                int height = originalRect.Bottom - originalRect.Top;

                if (!MoveWindow(hWnd, 100, 100, width, height, true)) { WriteOut("ERR press MoveWindow failed"); return; }
                moved = true;

                // ウィンドウ移動で client→screen の対応が変わるため同じクライアント座標を再変換する。
                var retryPoint = new Win32Point { X = clientX, Y = clientY };
                if (!ClientToScreen(hWnd, ref retryPoint))
                {
                    WriteOut("ERR press ClientToScreen retry failed");
                    RestoreWindow(hWnd, originalRect);
                    return;
                }

                SetCursorPos(retryPoint.X, retryPoint.Y);
                GetCursorPos(out Win32Point actual2);

                if (actual2.X != retryPoint.X || actual2.Y != retryPoint.Y)
                {
                    WriteOut($"ERR press cursor mismatch after retry: wanted [{retryPoint.X}, {retryPoint.Y}] got [{actual2.X}, {actual2.Y}]");
                    RestoreWindow(hWnd, originalRect);
                    return;
                }
            }
        }
        catch (Exception e)
        {
            if (moved) RestoreWindow(hWnd, originalRect);
            Utils.ThrowException(e);
            WriteOut("ERR press injection setup failed");
            return;
        }

        try { mouse_event(MouseEventLeftDown, 0, 0, 0, IntPtr.Zero); }
        catch (Exception e)
        {
            if (moved) RestoreWindow(hWnd, originalRect);
            Utils.ThrowException(e);
            WriteOut("ERR press mouse down failed");
            return;
        }

        // down と up を同一 tick で打たない(Unity のフレームポーリング取りこぼし対策)。up は 0.1s 後。
        LateTask.New(() =>
        {
            try { mouse_event(MouseEventLeftUp, 0, 0, 0, IntPtr.Zero); }
            catch (Exception e) { Utils.ThrowException(e); }
            finally { if (moved) RestoreWindow(hWnd, originalRect); }

            WriteOut($"OK press [{clientX}, {clientY}] ({geom})");
        }, 0.1f, "TestBridge.PressUp", log: false);
    }

    // scroll <x> <y> <notches> — スクロールビューの画面外の行へ届くための唯一の経路。
    // カーソルをその座標へ置いてからホイールを 1 ノッチずつ注入する (Unity の ScrollRect は
    // 「カーソルがビューの上にある」ことを要求するので、座標指定は必須)。
    // press と違いウィンドウが画面外に出ている場合の MoveWindow 退避は行わない — 届かなければ ERR を返す。
    private static void ExecuteScroll(string rest)
    {
        if (!OperatingSystem.IsWindows()) { WriteOut("ERR scroll windows only"); return; }

        string[] parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length != 3
            || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float fx)
            || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float fy)
            || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int notches))
        {
            WriteOut("ERR scroll usage: scroll <x> <y> <notches> (notches: 下方向がマイナス)");
            return;
        }

        if (notches == 0) { WriteOut("ERR scroll: notches must not be 0"); return; }
        notches = Math.Clamp(notches, -20, 20);

        var clientX = (int)fx;
        var clientY = (int)fy;

        IntPtr hWnd = ResolveGameWindow(out Win32Rect clientRect, out string winClass);
        if (hWnd == IntPtr.Zero) { WriteOut("ERR scroll no window handle"); return; }

        int clientW = clientRect.Right - clientRect.Left;
        int clientH = clientRect.Bottom - clientRect.Top;
        int screenW = Screen.width;
        int screenH = Screen.height;

        string geom = $"win=0x{hWnd.ToInt64():X} class={(winClass.Length > 0 ? winClass : "?")} client={clientW}x{clientH} screen={screenW}x{screenH}";

        // 範囲チェックは換算「前」に、座標の出所 (スクリーンショット) と同じバックバッファ基準で行う。
        if (screenW > 0 && screenH > 0 && (clientX < 0 || clientY < 0 || clientX >= screenW || clientY >= screenH))
            WriteOut($"WARN scroll target outside the captured frame: [{clientX}, {clientY}] {geom}");

        if (screenW > 0 && screenH > 0 && clientW > 0 && clientH > 0 && (clientW != screenW || clientH != screenH))
        {
            clientX = (int)Math.Round(clientX * (double)clientW / screenW);
            clientY = (int)Math.Round(clientY * (double)clientH / screenH);
            geom += " scaled";
        }

        try { SetForegroundWindow(hWnd); } catch { }

        var clientPoint = new Win32Point { X = clientX, Y = clientY };
        if (!ClientToScreen(hWnd, ref clientPoint)) { WriteOut("ERR scroll ClientToScreen failed"); return; }

        try
        {
            SetCursorPos(clientPoint.X, clientPoint.Y);
            GetCursorPos(out Win32Point actual);

            if (actual.X != clientPoint.X || actual.Y != clientPoint.Y)
            {
                WriteOut($"ERR scroll cursor mismatch: wanted [{clientPoint.X}, {clientPoint.Y}] got [{actual.X}, {actual.Y}] (ウィンドウが画面外の可能性)");
                return;
            }
        }
        catch (Exception e)
        {
            Utils.ThrowException(e);
            WriteOut("ERR scroll injection setup failed");
            return;
        }

        // 1 ノッチずつ、フレームを跨いで送る。まとめて 1 発だと Unity 側が 1 回分しか拾わないことがある。
        int step = notches > 0 ? 1 : -1;
        int remaining = Math.Abs(notches);
        var delay = 0f;

        for (var i = 0; i < remaining; i++)
        {
            LateTask.New(() =>
            {
                try { mouse_event(MouseEventWheel, 0, 0, unchecked((uint)(step * 120)), IntPtr.Zero); }
                catch (Exception e) { Utils.ThrowException(e); }
            }, delay, "TestBridge.ScrollTick", log: false);

            delay += 0.06f;
        }

        LateTask.New(() => WriteOut($"OK scroll [{clientX}, {clientY}] notches={notches} ({geom})"), delay, "TestBridge.ScrollDone", log: false);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct KeybdInput
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    // INPUT は union を含む (x64 で 40 バイト)。KEYBDINPUT 以外は使わないので末尾に詰め物で幅を合わせる。
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Win32Input
    {
        public uint type;
        public KeybdInput ki;
        public long pad;
    }

    private const uint InputKeyboard = 1;
    private const uint KeyEventUnicode = 0x0004;
    private const uint KeyEventKeyUp = 0x0002;

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, Win32Input[] pInputs, int cbSize);

    private static bool FocusGameWindow()
    {
        IntPtr hWnd = ResolveGameWindow(out _, out _);
        if (hWnd == IntPtr.Zero) return false;
        try { SetForegroundWindow(hWnd); } catch { /* ignore */ }
        return true;
    }

    // type <text> — 文字を Unicode キーイベントとして注入する (フォーカス中のテキスト欄に入る)。
    private static void ExecuteType(string text)
    {
        if (!OperatingSystem.IsWindows()) { WriteOut("ERR type windows only"); return; }
        if (string.IsNullOrEmpty(text)) { WriteOut("ERR type usage: type <text>"); return; }
        if (!FocusGameWindow()) { WriteOut("ERR type no window handle"); return; }

        var inputs = new Win32Input[text.Length * 2];
        for (int i = 0; i < text.Length; i++)
        {
            inputs[i * 2] = new Win32Input { type = InputKeyboard, ki = new KeybdInput { wScan = text[i], dwFlags = KeyEventUnicode } };
            inputs[i * 2 + 1] = new Win32Input { type = InputKeyboard, ki = new KeybdInput { wScan = text[i], dwFlags = KeyEventUnicode | KeyEventKeyUp } };
        }

        int size = System.Runtime.InteropServices.Marshal.SizeOf<Win32Input>();
        uint sent = SendInput((uint)inputs.Length, inputs, size);
        WriteOut(sent == inputs.Length ? $"OK type {text.Length} chars" : $"ERR type sent {sent}/{inputs.Length} events (err={System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
    }

    // key <name> — 仮想キーを down → 0.1s 後に up で注入する (Unity のフレームポーリング取りこぼし対策)。
    private static void ExecuteKey(string name)
    {
        if (!OperatingSystem.IsWindows()) { WriteOut("ERR key windows only"); return; }

        ushort vk = name.ToLowerInvariant() switch
        {
            "enter" or "return" => 0x0D,
            "escape" or "esc" => 0x1B,
            "tab" => 0x09,
            "backspace" => 0x08,
            "delete" => 0x2E,
            "left" => 0x25,
            "right" => 0x27,
            _ => 0
        };

        if (vk == 0) { WriteOut("ERR key usage: key <enter|escape|tab|backspace|delete|left|right>"); return; }
        if (!FocusGameWindow()) { WriteOut("ERR key no window handle"); return; }

        int size = System.Runtime.InteropServices.Marshal.SizeOf<Win32Input>();
        var down = new[] { new Win32Input { type = InputKeyboard, ki = new KeybdInput { wVk = vk } } };
        if (SendInput(1, down, size) != 1) { WriteOut($"ERR key down failed (err={System.Runtime.InteropServices.Marshal.GetLastWin32Error()})"); return; }

        LateTask.New(() =>
        {
            var up = new[] { new Win32Input { type = InputKeyboard, ki = new KeybdInput { wVk = vk, dwFlags = KeyEventKeyUp } } };
            try { SendInput(1, up, size); }
            catch (Exception e) { Utils.ThrowException(e); }
            WriteOut($"OK key {name}");
        }, 0.1f, "TestBridge.KeyUp", log: false);
    }

    private static void RestoreWindow(IntPtr hWnd, Win32Rect rect)
    {
        try { MoveWindow(hWnd, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, true); }
        catch { }
    }

    // ── Layer A: mod オプション操作 ────────────────────────────────────

    // OptionItem の実 SetValue は「選択肢 index」を取る。ここでは index を主インターフェースにし、
    // 実値指定は `~30` / `~0.5` 形式(Rule.GetNearestIndex 変換)だけサポートする。
    private static int MaxIndexOf(OptionItem opt)
    {
        return opt switch
        {
            BooleanOptionItem => 1,
            StringOptionItem s => Math.Max(0, s.Selections.Count - 1),
            IntegerOptionItem i => (i.Rule.MaxValue - i.Rule.MinValue) / i.Rule.Step,
            FloatOptionItem f => (int)((f.Rule.MaxValue - f.Rule.MinValue) / f.Rule.Step),
            _ => int.MaxValue
        };
    }

    private static void ExecuteGetOpt(string pattern)
    {
        if (pattern.Length < 2) { WriteOut("ERR getopt pattern too short (min 2 chars)"); return; }

        List<OptionItem> matches = OptionItem.AllOptions
            .Where(o => o.Name != null && o.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            .ToList();

        const int cap = 120;
        var sb = new StringBuilder(8192);
        sb.Append('{');
        sb.Append("\"ts\":").Append(Utils.TimeStamp.ToString(CultureInfo.InvariantCulture)).Append(',');
        sb.Append("\"pattern\":").Append(JStr(pattern)).Append(',');
        sb.Append("\"total\":").Append(matches.Count).Append(',');
        sb.Append("\"options\":[");

        for (int i = 0; i < matches.Count && i < cap; i++)
        {
            OptionItem o = matches[i];
            if (i > 0) sb.Append(',');

            sb.Append('{');
            sb.Append("\"id\":").Append(o.Id).Append(',');
            sb.Append("\"name\":").Append(JStr(o.Name)).Append(',');
            sb.Append("\"type\":").Append(JStr(o.GetType().Name.Replace("OptionItem", ""))).Append(',');
            sb.Append("\"index\":").Append(o.CurrentValue).Append(',');

            string display;
            try { display = o.GetString(); } catch { display = "?"; }
            sb.Append("\"value\":").Append(JStr(CleanLabel(display))).Append(',');

            switch (o)
            {
                case StringOptionItem s:
                    sb.Append("\"selections\":[");
                    for (int k = 0; k < s.Selections.Count; k++)
                    {
                        if (k > 0) sb.Append(',');
                        sb.Append(JStr(CleanLabel(s.Selections[k])));
                    }
                    sb.Append("],");
                    break;
                case IntegerOptionItem n:
                    sb.Append("\"min\":").Append(n.Rule.MinValue).Append(",\"max\":").Append(n.Rule.MaxValue).Append(",\"step\":").Append(n.Rule.Step).Append(',');
                    break;
                case FloatOptionItem f:
                    sb.Append("\"min\":").Append(F(f.Rule.MinValue)).Append(",\"max\":").Append(F(f.Rule.MaxValue)).Append(",\"step\":").Append(F(f.Rule.Step)).Append(',');
                    break;
            }

            sb.Append("\"tab\":").Append(JStr(o.Tab.ToString())).Append(',');
            sb.Append("\"parent\":").Append(JStr(o.Parent?.Name ?? ""));
            sb.Append('}');
        }

        sb.Append("]}");

        string optsPath = Path.Combine(_dir, "bridge-opts.json");
        File.WriteAllText(optsPath, sb.ToString());
        WriteOut($"OK getopt {Math.Min(matches.Count, cap)}/{matches.Count} matches -> bridge-opts.json");
    }

    private static void ExecuteSetOpt(string rest)
    {
        int sp = rest.LastIndexOf(' ');
        if (sp <= 0) { WriteOut("ERR setopt usage: setopt <name|#id> <index|on|off|~realValue>"); return; }

        string name = rest[..sp].Trim();
        string valueArg = rest[(sp + 1)..].Trim();

        OptionItem opt;

        // #<id> 直指定 — getopt が bridge-opts.json に出す id と同じ体系(OptionItem.AllOptions を共有ソースにする)。
        // AbilityUseLimit のような同名オプションが実機に89個ある問題(名前一意解決が不可能)を id で迂回する。
        if (name.StartsWith('#'))
        {
            if (!int.TryParse(name[1..], out int wantId)) { WriteOut($"ERR setopt bad id: {name}"); return; }

            opt = OptionItem.AllOptions.FirstOrDefault(o => o.Id == wantId);
            if (opt == null) { WriteOut($"ERR setopt no option with id {wantId}"); return; }
        }
        else
        {
            // 完全一致優先、なければ一意な部分一致で解決。
            List<OptionItem> exact = OptionItem.AllOptions.Where(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();

            if (exact.Count == 0)
            {
                List<OptionItem> partial = OptionItem.AllOptions.Where(o => o.Name != null && o.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).ToList();

                switch (partial.Count)
                {
                    case 0:
                        WriteOut($"ERR setopt no option named: {name}");
                        return;
                    case 1:
                        exact = partial;
                        break;
                    default:
                        WriteOut($"ERR setopt ambiguous ({partial.Count}): {string.Join(", ", partial.Take(5).Select(o => o.Name))}{(partial.Count > 5 ? ", ..." : "")}");
                        return;
                }
            }

            opt = exact[0];
        }

        // PresetOptionItem は全インスタンスが Name=="Preset" で、SetValue が SwitchPreset(全オプション
        // Refresh + 全体同期)に化ける。単一オプション操作の意図と食い違うので明示ブロック。
        // TextOptionItem は見出し行で値を持たないためこれも弾く。
        if (opt is PresetOptionItem) { WriteOut("ERR setopt refuses Preset (would switch ALL options to another preset)"); return; }
        if (opt is TextOptionItem) { WriteOut($"ERR setopt {opt.Name} is a text header, not a value option"); return; }

        int index;

        if (valueArg.Equals("on", StringComparison.OrdinalIgnoreCase) || valueArg.Equals("true", StringComparison.OrdinalIgnoreCase))
            index = 1;
        else if (valueArg.Equals("off", StringComparison.OrdinalIgnoreCase) || valueArg.Equals("false", StringComparison.OrdinalIgnoreCase))
            index = 0;
        else if (valueArg.StartsWith('~'))
        {
            string real = valueArg[1..];

            switch (opt)
            {
                case IntegerOptionItem n when int.TryParse(real, NumberStyles.Integer, CultureInfo.InvariantCulture, out int iv):
                    index = n.Rule.GetNearestIndex(iv);
                    break;
                case FloatOptionItem f when float.TryParse(real, NumberStyles.Float, CultureInfo.InvariantCulture, out float fv):
                    index = f.Rule.GetNearestIndex(fv);
                    break;
                default:
                    WriteOut($"ERR setopt ~real only valid for Integer/Float options: {opt.Name} is {opt.GetType().Name}");
                    return;
            }
        }
        else if (!int.TryParse(valueArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out index))
        {
            WriteOut($"ERR setopt bad value: {valueArg}");
            return;
        }

        index = Math.Clamp(index, 0, MaxIndexOf(opt));

        int before = opt.CurrentValue;
        opt.SetValue(index); // save + modded クライアントへの sync 込み(OptionItem.SetValue 既定経路)

        string display;
        try { display = opt.GetString(); } catch { display = "?"; }

        WriteOut($"OK setopt {opt.Name}: {before} -> {index} ({CleanLabel(display)})");
    }

    private static void ExecuteForceRole(string rest)
    {
        // clear はローカル状態(Main.SetRoles/SetAddOns)の掃除だけなので host 不要 — メニューからでも通す
        // (実機テストで「テスト後の掃除がメニューに戻ってからだと弾かれる」ことが判明した対処)。
        if (rest.Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            int n = Main.SetRoles.Count + Main.SetAddOns.Count;
            Main.SetRoles.Clear();
            Main.SetAddOns.Clear();
            WriteOut($"OK forcerole cleared {n} presets");
            return;
        }

        if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost)
        {
            WriteOut("ERR not host");
            return;
        }

        string[] parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) { WriteOut("ERR forcerole usage: forcerole <playerId|name|host|clear> <CustomRolesEnumName>"); return; }

        byte targetId;

        if (parts[0].Equals("host", StringComparison.OrdinalIgnoreCase))
        {
            if (!PlayerControl.LocalPlayer) { WriteOut("ERR no local player"); return; }
            targetId = PlayerControl.LocalPlayer.PlayerId;
        }
        else if (!byte.TryParse(parts[0], out targetId))
        {
            // ロビー再生成のたびに playerId が入れ替わり state で番号を引き直す手間があるので、
            // 表示名 (タグ除去済み・前方一致・大小無視) でも指定できる。空白入りの名前は前方一致で拾う。
            PlayerControl byName = ResolvePlayerByName(parts[0], out string nameErr);
            if (byName == null) { WriteOut($"ERR forcerole {nameErr}"); return; }

            targetId = byName.PlayerId;
        }

        if (!Enum.TryParse(parts[1], true, out CustomRoles role))
        {
            WriteOut($"ERR forcerole unknown role enum: {parts[1]}");
            return;
        }

        if (role.IsAdditionRole())
        {
            if (!Main.SetAddOns.ContainsKey(targetId)) Main.SetAddOns[targetId] = [];

            if (Main.SetAddOns[targetId].Contains(role))
            {
                Main.SetAddOns[targetId].Remove(role);
                WriteOut($"OK forcerole addon removed: {role} from {targetId}");
            }
            else
            {
                Main.SetAddOns[targetId].Add(role);
                WriteOut($"OK forcerole addon added: {role} to {targetId}");
            }
        }
        else
        {
            Main.SetRoles[targetId] = role;
            WriteOut($"OK forcerole {targetId} = {role} (applies at next game start)");
        }
    }

    private static PlayerControl ResolvePlayerByName(string query, out string error)
    {
        error = null;
        List<PlayerControl> exact = [];
        List<PlayerControl> prefix = [];

        foreach (PlayerControl pc in Main.AllPlayerControls)
        {
            string name = SafeName(pc);
            if (name.Length == 0) continue;

            if (name.Equals(query, StringComparison.OrdinalIgnoreCase)) exact.Add(pc);
            else if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) prefix.Add(pc);
        }

        List<PlayerControl> hits = exact.Count > 0 ? exact : prefix;
        if (hits.Count == 1) return hits[0];

        error = hits.Count == 0
            ? $"no player named '{query}'"
            : $"ambiguous name '{query}': {string.Join(", ", hits.Select(p => $"{p.PlayerId}:{SafeName(p)}"))}";

        return null;
    }

    // ── Layer B: ゲームフロー ─────────────────────────────────────────

    private static void ExecuteStart()
    {
        if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) { WriteOut("ERR not host"); return; }
        if (!GameStates.IsLobby) { WriteOut("ERR start only works in lobby"); return; }
        if (!GameStartManager.InstanceExists) { WriteOut("ERR no GameStartManager"); return; }

        GameStartManager gsm = GameStartManager.Instance;

        if (gsm.startState == GameStartManager.StartingStates.Countdown)
        {
            gsm.countDownTimer = 0; // 既にカウントダウン中ならスキップだけ
            WriteOut("OK start (countdown skipped)");
            return;
        }

        gsm.BeginGame();
        gsm.countDownTimer = 0;
        WriteOut("OK start");
    }

    // ── Layer B2: ロビー自動作成 / Layer A2: AutoStart フリップ ─────────

    private static void ExecuteHostLobby()
    {
        if (AutoRehost.Pending) { WriteOut("OK hostlobby already in progress (wait phase=Lobby)"); return; }
        if (!GameStates.IsNotJoined) { WriteOut("ERR hostlobby already in a lobby/game (leave first)"); return; }
        if (UnityEngine.Object.FindObjectOfType<MainMenuManager>() == null) { WriteOut("ERR hostlobby not at MainMenu"); return; }

        AutoRehost.RequestStartupHost();
        WriteOut("OK hostlobby requested (region/map/settings restored from disk — follow with: wait phase=Lobby 90)");
    }

    private static void ExecuteLeaveLobby()
    {
        if (GameStates.IsNotJoined) { WriteOut("ERR leavelobby not in a lobby/game"); return; }

        AmongUsClient.Instance.ExitGame(DisconnectReasons.ExitGame);
        WriteOut("OK leavelobby requested (follow with: wait phase=Menu 30)");
    }

    private static void ExecuteEosStall()
    {
        EOSManager eos = EOSManager.Instance;
        if (eos == null) { WriteOut("ERR eosstall EOSManager missing"); return; }

        eos.loginFlowFinished = false;
        eos.tryingToLogin = true;
        AutoRehost.StartBootLoginWatch();
        WriteOut("OK eosstall simulated (loginFlowFinished=false tryingToLogin=true; boot login watch re-armed — retry fires after ~40s, hostlobby holds until the flow finishes)");
    }

    private static void ExecuteSignInFail()
    {
        if (!AccountManager.InstanceExists) { WriteOut("ERR signinfail AccountManager missing"); return; }

        AccountManager am = AccountManager.Instance;
        bool wasActive = am.gameObject.activeSelf;
        am.SignInFail(EOSManager.EOS_ERRORS.NoConnectionError, (Il2CppSystem.Action)(() => Logger.Info("signinfail dialog closed", "TestBridge")));
        bool visible = am.genericInfoDisplayBox != null && am.genericInfoDisplayBox.gameObject.activeInHierarchy;
        WriteOut($"OK signinfail shown (amWasActive={wasActive} amActive={am.gameObject.activeSelf} dialogVisible={visible})");
    }

    private static void ExecuteAutoStart(string rest)
    {
        bool? value = rest.ToLowerInvariant() switch
        {
            "on" or "true" or "1" => true,
            "off" or "false" or "0" => false,
            _ => null
        };

        if (value == null) { WriteOut("ERR autostart usage: autostart <on|off>"); return; }
        if (Main.AutoStart == null) { WriteOut("ERR autostart config not bound"); return; }

        Main.AutoStart.Value = value.Value;
        WriteOut($"OK autostart {(value.Value ? "on" : "off")}");
    }

    // ── Layer C: TP / HUD アクションボタン ─────────────────────────────

    private static void ExecuteTp(string rest)
    {
        PlayerControl lp = PlayerControl.LocalPlayer;
        if (!lp) { WriteOut("ERR no local player"); return; }
        if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) { WriteOut("ERR not host"); return; }

        string[] parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        Vector2 dest;
        PlayerControl mover = lp;

        if (parts.Length == 1 && byte.TryParse(parts[0], out byte pid))
        {
            PlayerControl target = Utils.GetPlayerById(pid);
            if (!target) { WriteOut($"ERR tp no player with id {pid}"); return; }
            dest = target.Pos(); // SnapTo は transform 空間 — GetTruePosition(足元)を渡すと Collider.offset ぶん沈む
        }
        else if (parts.Length == 2 &&
                 float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
                 float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y))
            dest = new(x, y);
        else if (parts.Length == 3 &&
                 float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float tx) &&
                 float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float ty))
        {
            // 非モッド客は歩かせる以外に位置を作れず、walk は壁や柱に張り付いて狙った座標に乗らない。
            // 位置が述語になる機能 (デバイス圏内判定など) の確認用に、ホストから客を 1 発で置けるようにする。
            if (byte.TryParse(parts[0], out byte moverId))
            {
                mover = Utils.GetPlayerById(moverId);
                if (!mover) { WriteOut($"ERR tp no player with id {moverId}"); return; }
            }
            else
            {
                mover = ResolvePlayerByName(parts[0], out string moverErr);
                if (!mover) { WriteOut($"ERR tp {moverErr}"); return; }
            }

            // PlayerId 200 以上は CNO のダミー。CNO の SnapTo は CustomNetObject 側が SendOption.None で
            // 束ねて送る決まりなので、ここから Reliable で撃つと約束を破り、共有の SnapTo 予算も食う。
            if (mover.PlayerId >= 200) { WriteOut($"ERR tp {mover.PlayerId} is a CNO dummy (use the CNO's own update path)"); return; }

            dest = new(tx, ty);
        }
        else
        {
            WriteOut("ERR tp usage: tp <x> <y> | tp <playerId> | tp <playerId|name> <x> <y>");
            return;
        }

        if (!mover.NetTransform) { WriteOut($"ERR tp {mover.GetRealName()} has no NetTransform"); return; }

        bool ok = Utils.TP(mover.NetTransform, dest, true);
        string who = mover.PlayerId == lp.PlayerId ? string.Empty : $" {mover.GetRealName()}";
        WriteOut(ok ? $"OK tp{who} -> [{F(dest.x)}, {F(dest.y)}]" : "ERR tp rejected (noCheckState 経路で false になるのは SnapTo cap 超過がほぼ唯一 — KICKRISK 抑制中)");
    }

    // task <playerId|name> [count|all] — 未完了の通常タスクを先頭から count 個 (既定 1) 完了させる。
    // 複数個は 0.4s 間隔で順送りし、全件送り終えた時点で `OK task done` を出す。
    private static void ExecuteTask(string rest)
    {
        if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) { WriteOut("ERR not host"); return; }
        if (!GameStates.IsInTask) { WriteOut("ERR task only works in task phase"); return; }

        string[] parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 2) { WriteOut("ERR task usage: task <playerId|name> [count|all]"); return; }

        PlayerControl target;

        if (byte.TryParse(parts[0], out byte pid))
        {
            target = Utils.GetPlayerById(pid);
            if (!target) { WriteOut($"ERR task no player with id {pid}"); return; }
        }
        else
        {
            target = ResolvePlayerByName(parts[0], out string error);
            if (!target) { WriteOut($"ERR task {error}"); return; }
        }

        if (target.Data == null || target.Data.Tasks == null) { WriteOut("ERR task target has no task data"); return; }

        List<uint> pending = [];
        var tasks = target.Data.Tasks;

        for (var i = 0; i < tasks.Count; i++)
        {
            NetworkedPlayerInfo.TaskInfo t = tasks[i];
            if (t != null && !t.Complete) pending.Add(t.Id);
        }

        if (pending.Count == 0) { WriteOut($"ERR task {target.PlayerId} has no incomplete tasks (total {tasks.Count})"); return; }

        // 客側の myTasks 先頭には Id=0 のヒント文タスクが挿入されており、Id=0 の完了 RPC はそちらに当たって
        // 客の画面では完了にならない。部分完了で客とホストの表示をずらさないよう Id=0 は最後に回す。
        if (pending.Remove(0u)) pending.Add(0u);

        int count = 1;

        if (parts.Length == 2)
        {
            if (parts[1].Equals("all", StringComparison.OrdinalIgnoreCase)) count = pending.Count;
            else if (!int.TryParse(parts[1], out count) || count < 1) { WriteOut($"ERR task bad count: {parts[1]}"); return; }
        }

        count = Math.Min(count, pending.Count);
        byte targetId = target.PlayerId;

        for (var i = 0; i < count; i++)
        {
            uint taskId = pending[i];
            bool last = i == count - 1;

            LateTask.New(() =>
            {
                PlayerControl pc = Utils.GetPlayerById(targetId);

                if (!pc || !GameStates.IsInTask)
                {
                    WriteOut($"ERR task {targetId} aborted before task {taskId}");
                    return;
                }

                pc.RpcCompleteTask(taskId);

                if (last)
                {
                    TaskState ts = pc.GetTaskState();
                    WriteOut($"OK task done {targetId} x{count} (progress {ts.CompletedTasksCount}/{ts.AllTasksCount})");
                }
            }, 0.4f * i, "TestBridge.Task", log: false);
        }

        WriteOut($"OK task queued {targetId} x{count} (incomplete {pending.Count})");
    }

    // use <button> [playerId|name] [targetId|name]
    // 引数が 1 つならホスト自身の HUD ボタンを押す (従来どおり)。
    // プレイヤーを添えると、その人が押した時にホスト側で走る処理をそのまま呼ぶ。
    // 非モッド客はバニラのボタンしか持たないので、こちらから押させる手段が他に無い。
    private static void ExecuteUse(string rest)
    {
        string[] parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) { WriteOut("ERR use usage: use <kill|vent|pet|ability|report|sabotage> [playerId|name|host] [targetId|name]"); return; }

        if (parts.Length == 1) { UseLocalButton(parts[0]); return; }

        if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) { WriteOut("ERR not host"); return; }

        PlayerControl actor = ResolvePlayerToken(parts[1], out string actorErr);
        if (!actor) { WriteOut($"ERR use {actorErr}"); return; }

        PlayerControl explicitTarget = null;

        if (parts.Length >= 3)
        {
            explicitTarget = ResolvePlayerToken(parts[2], out string targetErr);
            if (!explicitTarget) { WriteOut($"ERR use {targetErr}"); return; }
        }

        UseAsPlayer(parts[0].ToLowerInvariant(), actor, explicitTarget);
    }

    // playerId / 表示名 / host のどれでもプレイヤーを引けるようにする (tp・task と同じ語彙)。
    private static PlayerControl ResolvePlayerToken(string token, out string error)
    {
        error = null;

        if (token.Equals("host", StringComparison.OrdinalIgnoreCase))
        {
            if (!PlayerControl.LocalPlayer) { error = "no local player"; return null; }

            return PlayerControl.LocalPlayer;
        }

        if (byte.TryParse(token, out byte pid))
        {
            PlayerControl byId = Utils.GetPlayerById(pid);
            if (!byId) { error = $"no player with id {pid}"; return null; }

            return byId;
        }

        return ResolvePlayerByName(token, out error);
    }

    private static void UseLocalButton(string button)
    {
        if (!HudManager.InstanceExists) { WriteOut("ERR no HudManager"); return; }

        HudManager hud = HudManager.Instance;

        ActionButton target = button.ToLowerInvariant() switch
        {
            "kill" => hud.KillButton,
            "vent" => hud.ImpostorVentButton,
            "pet" => hud.PetButton,
            "ability" => hud.AbilityButton,
            "report" => hud.ReportButton,
            "sabotage" => hud.SabotageButton,
            _ => null
        };

        if (target == null) { WriteOut($"ERR use unknown button: {button} (kill|vent|pet|ability|report|sabotage)"); return; }
        if (!target.isActiveAndEnabled) { WriteOut($"ERR use {button}: button inactive"); return; }

        target.DoClick();

        // 再発防止: イントロ明け StartingKillCooldown 秒の PreventKill 窓内は、
        // kill (PlayerControlPatch.cs:700) / pet (PetActionsPatch.OnPetUse) / ability=vanish・shapeshift
        // (PlayerControlPatch.cs:988) の発動がモッド側で無音棄却される。OK だけ返すと「押したのに
        // 発火しない」を故障と誤診するので、窓内である事実を応答に併記する (解除待ちは
        // `wait marker:PreventKillReset`)。
        if (button.ToLowerInvariant() is "kill" or "pet" or "ability" && IntroCutsceneDestroyPatch.PreventKill)
        {
            WriteOut($"OK use {button} (WARN PreventKill active: ability/kill triggers are silently swallowed until \"PreventKillReset\" — wait marker:PreventKillReset first)");
            return;
        }

        WriteOut($"OK use {button}");
    }

    // 客 (バニラ) が押した時にホスト側で走る処理を、そのままホストから呼ぶ。
    // RPC を偽造するのではなく、受信後に通る関数を直に叩くので、役職の判定・クールタイム・
    // 通知はすべて本物と同じ経路を通る。
    private static void UseAsPlayer(string button, PlayerControl actor, PlayerControl explicitTarget)
    {
        string who = SafeName(actor);

        if (actor.PlayerId >= 200) { WriteOut($"ERR use {actor.PlayerId} is a CNO dummy (not a real client)"); return; }
        if (!actor.IsAlive()) { WriteOut($"ERR use {button}: {who} is dead"); return; }

        string preventKillNote = button is "kill" or "pet" or "ability" && IntroCutsceneDestroyPatch.PreventKill
            ? " (WARN PreventKill active: triggers are silently swallowed until \"PreventKillReset\" — wait marker:PreventKillReset first)"
            : string.Empty;

        switch (button)
        {
            case "pet":
            {
                if (!actor.MyPhysics) { WriteOut($"ERR use pet: {who} has no MyPhysics"); return; }
                if (!Options.UsePets.GetBool()) { WriteOut("ERR use pet: UsePets is OFF (pet abilities are disabled this game)"); return; }

                // 客のペット RPC を受けた時とまったく同じ入口。連打抑止 (1 秒) もここで効く。
                ExternalRpcPetPatch.Prefix(actor.MyPhysics, (byte)RpcCalls.Pet);
                WriteOut($"OK use pet {who}{preventKillNote}");
                return;
            }

            case "kill":
            {
                PlayerControl victim = explicitTarget ?? ExternalRpcPetPatch.SelectKillButtonTarget(actor);
                if (!victim) { WriteOut($"ERR use kill: no target in range of {who} (pass a target explicitly)"); return; }
                if (victim.PlayerId == actor.PlayerId) { WriteOut("ERR use kill: target is the killer"); return; }

                // 客のキルボタンは CmdCheckMurder → ホストの CheckMurder へ届く。その Prefix を直に呼ぶ。
                CheckMurderPatch.Prefix(actor, victim);
                WriteOut($"OK use kill {who} -> {SafeName(victim)}{preventKillNote}");
                return;
            }

            case "report":
            {
                if (explicitTarget != null)
                {
                    if (explicitTarget.Data == null) { WriteOut("ERR use report: target has no player data"); return; }

                    actor.ReportDeadBody(explicitTarget.Data);
                    WriteOut($"OK use report {who} -> body of {SafeName(explicitTarget)}");
                    return;
                }

                actor.ReportDeadBody(null);
                WriteOut($"OK use report {who} (emergency button)");
                return;
            }

            case "vent":
            {
                // 他人の netId での RpcEnterVent/RpcExitVent 自体は既存の前例がある
                // (Patches/ControlPatch.cs のデバッグホットキーが同じ形で全員を潜らせる)。
                // 死体への RpcEnterVent だけは IL2CPP ネイティブヒープを壊すので、
                // 入口の生存ガード (この関数の冒頭) が外せない前提になっている。
                if (!actor.MyPhysics) { WriteOut($"ERR use vent: {who} has no MyPhysics"); return; }
                if (ShipStatus.Instance == null) { WriteOut("ERR use vent: no ShipStatus"); return; }

                if (actor.inVent)
                {
                    Vent current = actor.GetClosestVent();
                    if (current == null) { WriteOut("ERR use vent: no vent resolved for exit"); return; }

                    actor.MyPhysics.RpcExitVent(current.Id);
                    WriteOut($"OK use vent {who} exit {current.Id}");
                    return;
                }

                Vent nearest = actor.GetClosestVent();
                if (nearest == null) { WriteOut($"ERR use vent: no vent near {who}"); return; }

                actor.MyPhysics.RpcEnterVent(nearest.Id);
                WriteOut($"OK use vent {who} enter {nearest.Id}");
                return;
            }

            case "ability":
            {
                UseAbilityAsPlayer(actor, explicitTarget, who, preventKillNote);
                return;
            }

            case "sabotage":
                WriteOut("ERR use sabotage: no per-player path (use the `sabotage` directive — サボは誰が撃っても同じ系統操作)");
                return;

            default:
                WriteOut($"ERR use unknown button: {button} (kill|vent|pet|ability|report)");
                return;
        }
    }

    // 「能力ボタン」は役職基底 (客の画面に出ているバニラのボタン) ごとに別の RPC になる。
    // 基底で分岐して、その基底の押下がホスト側で入る関数を呼ぶ。
    private static void UseAbilityAsPlayer(PlayerControl actor, PlayerControl explicitTarget, string who, string preventKillNote)
    {
        // desync 役職は「ホストから見た基底」と「本人の画面に出ている基底」が食い違う
        // (ホスト視点では Scientist 等に見えるのに、本人は Phantom のまま)。客の押下を代行するので、
        // 見るべきは本人側の基底 — 役職定義から引き直す。
        CustomRoles customRole = actor.GetCustomRole();
        RoleTypes basis = customRole.IsDesyncRole() ? customRole.GetDYRole() : actor.Data?.Role?.Role ?? RoleTypes.Crewmate;

        switch (basis)
        {
            case RoleTypes.Shapeshifter:
            {
                // 変身の相手が要る。省略時は近くの誰か、居なければ自分 (= 変身解除の押下と同じ形)。
                PlayerControl target = explicitTarget ?? ExternalRpcPetPatch.SelectKillButtonTarget(actor) ?? actor;
                CheckShapeshiftPatch.Prefix(actor, target, true);
                WriteOut($"OK use ability {who} (shapeshift -> {SafeName(target)}){preventKillNote}");
                return;
            }

            case RoleTypes.Phantom:
            {
                PhantomRolePatch.CheckTrigger(actor);
                WriteOut($"OK use ability {who} (phantom vanish){preventKillNote}");
                return;
            }

            case RoleTypes.GuardianAngel:
            {
                PlayerControl target = explicitTarget ?? ExternalRpcPetPatch.SelectKillButtonTarget(actor);
                if (!target) { WriteOut($"ERR use ability: {who} is a Guardian Angel and needs a target (pass one explicitly)"); return; }

                CheckProtectPatch.Prefix(actor, target);
                WriteOut($"OK use ability {who} (protect -> {SafeName(target)}){preventKillNote}");
                return;
            }

            case RoleTypes.Engineer:
            {
                if (!actor.MyPhysics) { WriteOut($"ERR use ability: {who} has no MyPhysics"); return; }

                Vent nearest = actor.GetClosestVent();
                if (nearest == null) { WriteOut($"ERR use ability: no vent near {who}"); return; }

                actor.MyPhysics.RpcEnterVent(nearest.Id);
                WriteOut($"OK use ability {who} (engineer vent {nearest.Id}){preventKillNote}");
                return;
            }

            default:
            {
                // 残りの基底 (クルー・追跡者・科学者・騒音探知機など) は固有の能力 RPC を持たない。
                // これらの役職の能力はペット押下に載っているので、そちらへ倒す。
                if (!actor.MyPhysics) { WriteOut($"ERR use ability: {who} has no MyPhysics"); return; }
                if (!Options.UsePets.GetBool()) { WriteOut($"ERR use ability: {who}'s basis is {basis} and its ability rides on the pet button, but UsePets is OFF"); return; }

                ExternalRpcPetPatch.Prefix(actor.MyPhysics, (byte)RpcCalls.Pet);
                WriteOut($"OK use ability {who} (basis {basis} -> pet){preventKillNote}");
                return;
            }
        }
    }

    // vent enter <id> / vent exit — MyPhysics.RpcEnterVent/RpcExitVent 直呼び(既存経路の前例:
    // Roles/Standard/Crewmate/Support/Aid.cs, Comebacker.cs)。exit の対象 id は GetClosestVent()
    // (repo 全体で「現在の vent」を引くのに使われている既存パターン、真の currentVent フィールドは無い)。
    private static void ExecuteVent(string rest)
    {
        PlayerControl lp = PlayerControl.LocalPlayer;
        if (!lp) { WriteOut("ERR no local player"); return; }
        if (ShipStatus.Instance == null) { WriteOut("ERR vent no ShipStatus"); return; }

        string[] parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 2 && parts[0].Equals("enter", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(parts[1], out int ventId)) { WriteOut($"ERR vent enter bad id: {parts[1]}"); return; }
            if (!ShipStatus.Instance.AllVents.Any(v => v.Id == ventId)) { WriteOut($"ERR vent enter no such vent id {ventId}"); return; }
            if (!lp.MyPhysics) { WriteOut("ERR vent no MyPhysics"); return; }
            // 死体への RpcEnterVent は IL2CPP ネイティブヒープを破壊する(Patches/ControlPatch.cs の
            // CRITICAL コメント / Roles/Standard/Ghost/DemonicVenter.cs 参照)。生存ガード必須。
            if (!lp.IsAlive()) { WriteOut("ERR vent enter local player is dead (RpcEnterVent on a corpse corrupts the IL2CPP heap)"); return; }
            // 二重 enter はローカルの vent ステートマシンを desync させ、後続 exit の GetClosestVent
            // 近似解決が「入っていない vent の id」を送る入口になる。
            if (lp.inVent) { WriteOut("ERR vent enter already in a vent (use `vent exit` first)"); return; }

            lp.MyPhysics.RpcEnterVent(ventId);
            WriteOut($"OK vent enter {ventId}");
            return;
        }

        if (parts.Length == 1 && parts[0].Equals("exit", StringComparison.OrdinalIgnoreCase))
        {
            if (!lp.inVent) { WriteOut("ERR vent exit not in vent"); return; }

            Vent current = lp.GetClosestVent();
            if (current == null) { WriteOut("ERR vent exit no vent resolved"); return; }

            lp.MyPhysics?.RpcExitVent(current.Id);
            WriteOut($"OK vent exit {current.Id}");
            return;
        }

        WriteOut("ERR vent usage: vent enter <id> | vent exit");
    }

    // ── Layer C2: 歩行移動 ─────────────────────────────────────────────
    // tp(ホスト権限ワープ)と違い、PlayerPhysics.FixedUpdate の Postfix から毎物理 tick
    // SetNormalizedVelocity で速度を上書きする = vanilla の歩行と同じ client-authoritative な
    // 移動パケットが出る。anticheat テスト(公式サーバーでキックされないか)はこちらを使う。
    // 経路探索はしない(壁に当たったら stuck 検知で自動停止して報告する)。

    private static Vector2? _walkTarget;
    private static float _walkBestDist;
    private static float _walkNoProgressTime;
    private static float _walkTotalTime;

    private const float WalkArriveDist = 0.3f;       // 到着判定
    private const float WalkNoProgressLimit = 5f;    // 距離が縮まらないまま経過したら stuck
    private const float WalkHardTimeLimit = 60f;     // 全体の打ち切り

    private static void ExecuteWalk(string rest)
    {
        PlayerControl lp = PlayerControl.LocalPlayer;
        if (!lp) { WriteOut("ERR no local player"); return; }

        if (rest.Equals("stop", StringComparison.OrdinalIgnoreCase))
        {
            bool was = _walkTarget.HasValue;
            StopWalk(true);
            WriteOut(was ? "OK walk stopped" : "OK walk (was not walking)");
            return;
        }

        string[] parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Vector2 dest;

        if (parts.Length == 1 && byte.TryParse(parts[0], out byte pid))
        {
            PlayerControl target = Utils.GetPlayerById(pid);
            if (!target) { WriteOut($"ERR walk no player with id {pid}"); return; }
            dest = target.GetTruePosition();
        }
        else if (parts.Length == 2 &&
                 float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
                 float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y))
            dest = new(x, y);
        else
        {
            WriteOut("ERR walk usage: walk <x> <y> | walk <playerId> | walk stop");
            return;
        }

        _walkTarget = dest;
        _walkBestDist = float.MaxValue;
        _walkNoProgressTime = 0f;
        _walkTotalTime = 0f;
        WriteOut($"OK walk -> [{F(dest.x)}, {F(dest.y)}] (dist {F(Vector2.Distance(lp.GetTruePosition(), dest))})");
    }

    private static void StopWalk(bool zeroVelocity)
    {
        _walkTarget = null;
        if (!zeroVelocity) return;

        try
        {
            PlayerControl lp = PlayerControl.LocalPlayer;
            if (lp && lp.MyPhysics) lp.MyPhysics.SetNormalizedVelocity(Vector2.zero);
        }
        catch { }
    }

    // TestBridgeWalkPatch(毎物理 tick)から呼ばれる。先頭の _walkTarget null チェックで
    // 非使用時のコストは実質ゼロ。到着/stuck/timeout の終端イベントだけ out.log に書く。
    internal static void OnPlayerPhysicsFixedUpdate(PlayerPhysics physics)
    {
        if (_walkTarget == null) return;
        if (Main.EnableTestBridge is not { Value: true }) { _walkTarget = null; return; }

        try
        {
            PlayerControl lp = PlayerControl.LocalPlayer;
            if (!lp || !physics || physics.myPlayer != lp) return; // 自分の物理更新のときだけ動かす

            if (MeetingHud.Instance || ExileController.Instance)
            {
                StopWalk(false);
                WriteOut("ERR walk canceled (meeting)");
                return;
            }

            Vector2 pos = lp.GetTruePosition();
            Vector2 dest = _walkTarget.Value;
            float dist = Vector2.Distance(pos, dest);

            if (dist <= WalkArriveDist)
            {
                StopWalk(true);
                WriteOut($"OK walk arrived [{F(pos.x)}, {F(pos.y)}]");
                return;
            }

            float dt = Time.fixedDeltaTime;
            _walkTotalTime += dt;

            if (dist < _walkBestDist - 0.05f)
            {
                _walkBestDist = dist;
                _walkNoProgressTime = 0f;
            }
            else
                _walkNoProgressTime += dt;

            if (_walkNoProgressTime > WalkNoProgressLimit)
            {
                StopWalk(true);
                WriteOut($"ERR walk stuck at [{F(pos.x)}, {F(pos.y)}] (remaining {F(dist)})");
                return;
            }

            if (_walkTotalTime > WalkHardTimeLimit)
            {
                StopWalk(true);
                WriteOut($"ERR walk timeout at [{F(pos.x)}, {F(pos.y)}] (remaining {F(dist)})");
                return;
            }

            if (!lp.CanMove || lp.inVent) return; // 移動不能中は待機(縮まらなければ stuck 判定が拾う)

            physics.SetNormalizedVelocity((dest - pos).normalized);
        }
        catch (Exception e)
        {
            _walkTarget = null;
            Utils.ThrowException(e);
        }
    }

    // ── Layer C3: 会議投票 ─────────────────────────────────────────────

    private static void ExecuteVote(string rest)
    {
        MeetingHud meeting = MeetingHud.Instance;
        if (!meeting) { WriteOut("ERR vote no meeting"); return; }

        PlayerControl lp = PlayerControl.LocalPlayer;
        if (!lp) { WriteOut("ERR no local player"); return; }

        // 引数 1 個 = ホスト自身の投票。2 個 = 代理投票 (voter を明示)。
        // 代理投票が要るのは、非モッド客 (エミュ) が自分では投票しないため「全員が投票した瞬間 = 開票」を
        // 作れず、開票の数秒間でしか見えない挙動 (匿名投票の見え方など) をタイマー満了に頼らず観測できないから。
        string[] parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        const string usage = "ERR vote usage: vote <playerId|skip> | vote <voterId> <playerId|skip>";

        if (parts.Length is < 1 or > 2) { WriteOut(usage); return; }

        PlayerControl voter = lp;

        if (parts.Length == 2)
        {
            if (!byte.TryParse(parts[0], out byte voterId)) { WriteOut("ERR vote bad voterId"); return; }

            voter = Utils.GetPlayerById(voterId);
            if (voter == null) { WriteOut($"ERR vote no such voter: {voterId}"); return; }
            if (!voter.IsAlive()) { WriteOut($"ERR vote voter {voterId} is dead"); return; }
        }

        string suspectArg = parts[^1];
        byte suspect;

        if (suspectArg.Equals("skip", StringComparison.OrdinalIgnoreCase))
            suspect = 253; // vanilla の skip vote id
        else if (!byte.TryParse(suspectArg, out suspect)) { WriteOut(usage); return; }

        try
        {
            if (meeting.DidVote(voter.PlayerId)) { WriteOut($"ERR vote already voted ({voter.PlayerId})"); return; }
        }
        catch { }

        // CastVoteChecked = EHR の投票判定 (OnVote 等) を通してからバニラ CastVote に流す共通入口。
        // CancelsVote 系役職/死亡ガードはサイレントに投票を握り潰すので、
        // 呼び出し後に DidVote で「実際に反映されたか」を検証してから OK/ERR を出し分ける。
        Patches.MeetingHudCastVotePatch.CastVoteChecked(meeting, voter.PlayerId, suspect);

        bool landed;
        try { landed = meeting.DidVote(voter.PlayerId); }
        catch { landed = true; } // 検証不能時は楽観扱い(SYS 写しで役職側の拒否メッセージは別途見える)

        string targetStr = suspect == 253 ? "skip" : suspect.ToString();
        string voterStr = voter.PlayerId == lp.PlayerId ? string.Empty : $" by {voter.PlayerId}";
        WriteOut(landed ? $"OK vote {targetStr}{voterStr}" : $"ERR vote {targetStr}{voterStr} silently canceled (role logic / dead)");
    }

    // ── Layer C3b: Judge 木槌演出つき強制追放 (実機検証口) ─────────────

    // overrule <targetId> [judgeId] — 会議中限定。judgeId 省略時はホスト自身がジャッジ役。
    // 成立すると target が「ジャッジに覆された」ネイティブ演出 (gavel) 付きで追放される。
    private static void ExecuteOverrule(string rest)
    {
        if (!AmongUsClient.Instance.AmHost) { WriteOut("ERR overrule host only"); return; }

        MeetingHud meeting = MeetingHud.Instance;
        if (!meeting) { WriteOut("ERR overrule no meeting"); return; }

        string[] parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 2 || !byte.TryParse(parts[0], out byte targetId)) { WriteOut("ERR overrule usage: overrule <targetId> [judgeId]"); return; }

        byte judgeId = PlayerControl.LocalPlayer.PlayerId;
        if (parts.Length == 2 && !byte.TryParse(parts[1], out judgeId)) { WriteOut("ERR overrule bad judgeId"); return; }

        PlayerControl target = Utils.GetPlayerById(targetId);
        PlayerControl judge = Utils.GetPlayerById(judgeId);
        if (!target || !target.IsAlive()) { WriteOut("ERR overrule target invalid/dead"); return; }
        if (!judge) { WriteOut("ERR overrule judge invalid"); return; }

        Patches.CheckForEndVotingPatch.ForceExile(target, judge, judgeGavel: true);
        WriteOut($"OK overrule target={targetId} judge={judgeId}");
    }

    // ── Layer C4: 実チャット ───────────────────────────────────────────

    private static bool TryParseSabotageType(string name, out SystemTypes type)
    {
        type = name.ToLowerInvariant() switch
        {
            "comms" or "communications" => SystemTypes.Comms,
            "reactor" => SystemTypes.Reactor,
            "o2" or "oxygen" or "lifesupp" => SystemTypes.LifeSupp,
            "lights" or "electrical" => SystemTypes.Electrical,
            "lab" or "laboratory" or "seismic" => SystemTypes.Laboratory,
            "heli" or "helisabotage" or "crash" => SystemTypes.HeliSabotage,
            "mushroom" or "mixup" => SystemTypes.MushroomMixupSabotage,
            _ => (SystemTypes)255
        };

        return (byte)type != 255;
    }

    private static string DescribeSabotageState(SystemTypes type)
    {
        try
        {
            // active は Utils.IsActive(type) を使う (Polus の Reactor=false 固定・Comms の具象クラス切替・
            // mushroom 専用読み等の map 依存分岐がここに集約されている。自前 TryCast だと偽陰性を返す)。
            var sabo = ShipStatus.Instance.Systems[SystemTypes.Sabotage].CastFast<SabotageSystemType>();
            bool active = Utils.IsActive(type);
            return $"active={(active ? "true" : "false")} anyActive={(sabo.AnyActive ? "true" : "false")} cd={sabo.Timer:0.0}";
        }
        catch (Exception e) { return $"state? ({e.GetType().Name})"; }
    }

    // sabotage <comms|reactor|o2|lights|lab|heli|mushroom> — サボマップのボタンと同じ RPC をホスト自身で撃つ。
    // sabotage cd0 — サボクールダウンを 0 にする (開始直後の初期 CD で弾かれる回を潰す)。
    private static void ExecuteSabotage(string rest)
    {
        if (!AmongUsClient.Instance || !AmongUsClient.Instance.AmHost) { WriteOut("ERR sabotage: not host"); return; }
        if (!ShipStatus.Instance || !GameStates.InGame || GameStates.IsMeeting) { WriteOut("ERR sabotage: not in task phase"); return; }

        if (rest.Equals("cd0", StringComparison.OrdinalIgnoreCase))
        {
            var sabo = ShipStatus.Instance.Systems[SystemTypes.Sabotage].CastFast<SabotageSystemType>();
            sabo.Timer = 0f;
            sabo.IsDirty = true;
            WriteOut("OK sabotage cd0");
            return;
        }

        if (!TryParseSabotageType(rest, out SystemTypes type)) { WriteOut($"ERR sabotage unknown type: {rest} (comms|reactor|o2|lights|lab|heli|mushroom|cd0)"); return; }
        if (!ShipStatus.Instance.Systems.ContainsKey(type)) { WriteOut($"ERR sabotage {type}: not on this map"); return; }

        string before = DescribeSabotageState(type);
        ShipStatus.Instance.RpcUpdateSystem(SystemTypes.Sabotage, (byte)type);
        string after = DescribeSabotageState(type);

        // 発動したかは IActivatable.IsActive で機械判定する (ゲート棄却・CD 中は OK を返さない)。
        WriteOut(after.StartsWith("active=true") ? $"OK sabotage {type} {after}" : $"ERR sabotage {type} not activated ({after}; before {before}) — cd>0 なら `sabotage cd0`、ゲート棄却なら役職/DisableSabotage を疑う");
    }

    // fixsabotage <type> — 修理側の amount は Adventurer.cs の実績値 (Reactor/Comms/Heli=16,17・Lab/O2=66,67・Lights=全スイッチ)。
    private static void ExecuteFixSabotage(string rest)
    {
        if (!AmongUsClient.Instance || !AmongUsClient.Instance.AmHost) { WriteOut("ERR fixsabotage: not host"); return; }
        if (!ShipStatus.Instance || !GameStates.InGame) { WriteOut("ERR fixsabotage: not in game"); return; }
        if (!TryParseSabotageType(rest, out SystemTypes type)) { WriteOut($"ERR fixsabotage unknown type: {rest}"); return; }
        if (!ShipStatus.Instance.Systems.ContainsKey(type)) { WriteOut($"ERR fixsabotage {type}: not on this map"); return; }

        ShipStatus ship = ShipStatus.Instance;

        switch (type)
        {
            case SystemTypes.Reactor:
            case SystemTypes.Comms:
            case SystemTypes.HeliSabotage:
                ship.RpcUpdateSystem(type, 16);
                ship.RpcUpdateSystem(type, 17);
                break;
            case SystemTypes.Laboratory:
            case SystemTypes.LifeSupp:
                ship.RpcUpdateSystem(type, 67);
                ship.RpcUpdateSystem(type, 66);
                break;
            case SystemTypes.Electrical:
                var sw = ship.Systems[SystemTypes.Electrical].CastFast<SwitchSystem>();
                sw.ActualSwitches = sw.ExpectedSwitches;
                sw.IsDirty = true;
                break;
            case SystemTypes.MushroomMixupSabotage:
                WriteOut("ERR fixsabotage mushroom: 時間経過でしか解けない"); return;
        }

        WriteOut($"OK fixsabotage {type} {DescribeSabotageState(type)}");
    }

    // 配電盤のノブ 1 つ分の操作。`use` に console 操作は無く、`fixsabotage lights` は ActualSwitches へ直接代入するので
    // SwitchSystem.UpdateSystem を通らない = 「そのプレイヤーには直させない」系のゲートが観測できない。
    // ここはノブが送るのと同じ UpdateSystem(Electrical, player, index) をホスト側で撃つ。
    // player を差し替えられるので、同じゲームの中で素のプレイヤーと属性持ちを並べて比べられる。
    private static void ExecuteSwitch(string rest)
    {
        if (!AmongUsClient.Instance || !AmongUsClient.Instance.AmHost) { WriteOut("ERR switch: not host"); return; }
        if (!ShipStatus.Instance || !GameStates.InGame || GameStates.IsMeeting) { WriteOut("ERR switch: not in task phase"); return; }
        if (!ShipStatus.Instance.Systems.ContainsKey(SystemTypes.Electrical)) { WriteOut("ERR switch: this map has no electrical panel"); return; }

        string[] parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length is < 1 or > 2 || !byte.TryParse(parts[0], out byte index) || index > 4)
        {
            WriteOut("ERR switch usage: switch <0-4> [playerId|name]");
            return;
        }

        PlayerControl player = PlayerControl.LocalPlayer;
        if (!player) { WriteOut("ERR switch: no local player"); return; }

        if (parts.Length == 2)
        {
            if (byte.TryParse(parts[1], out byte pid))
            {
                player = Utils.GetPlayerById(pid);
                if (!player) { WriteOut($"ERR switch no player with id {pid}"); return; }
            }
            else
            {
                player = ResolvePlayerByName(parts[1], out string nameErr);
                if (!player) { WriteOut($"ERR switch {nameErr}"); return; }
            }

            if (player.PlayerId >= 200) { WriteOut($"ERR switch {player.PlayerId} is a CNO dummy"); return; }
        }

        var sw = ShipStatus.Instance.Systems[SystemTypes.Electrical].CastFast<SwitchSystem>();
        byte before = sw.ActualSwitches;

        ShipStatus.Instance.UpdateSystem(SystemTypes.Electrical, player, index);

        byte after = sw.ActualSwitches;
        WriteOut($"OK switch {index} by {player.GetRealName()}: actual {Bits5(before)} -> {Bits5(after)} expected {Bits5(sw.ExpectedSwitches)} changed={(before != after ? "true" : "false")}");
    }

    // ノブ 5 個の上下を左が index 0 になる並びで見せる (SwitchSystem は 1 ビット = 1 ノブ)。
    private static string Bits5(byte value)
    {
        var chars = new char[5];
        for (var i = 0; i < 5; i++) chars[i] = (value & (1 << i)) != 0 ? '1' : '0';
        return new(chars);
    }

    // 文字境界を割らずに UTF-8 バイト数を budget 以下へ切り詰める (ChatControlPatch.SplitByUtf8Bytes と同じ 1/2/3B 見積り)。
    private static string ClampUtf8Bytes(string s, int budget)
    {
        int bytes = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            int cb = c < 0x80 ? 1 : c < 0x800 ? 2 : 3;
            if (bytes + cb > budget) return s[..i];
            bytes += cb;
        }

        return s;
    }

    // chatui <text> — 入力欄に文字を置いて ChatController.SendChat() を呼ぶ = 人間が Enter を押したのと同じ経路。
    // vanilla の連投抑止 (timeSinceLastMessage < 3s) はホストローカルの UI 側チェックなので、連投テストのために外す。
    private static void ExecuteChatUi(string text)
    {
        if (text.Length == 0) { WriteOut("ERR chatui empty"); return; }
        if (!HudManager.InstanceExists) { WriteOut("ERR no HudManager"); return; }

        ChatController chat = HudManager.Instance.Chat;
        if (!chat || !chat.freeChatField || !chat.freeChatField.textArea) { WriteOut("ERR chatui: chat field unavailable"); return; }

        // 公式鯖は chat を UTF-8 バイト (~1KB 帯) で判定し、単一 RPC が閾値超だと PacketSplit も切れない
        // (ChatControlPatch の ChatChunkByteBudget=700 と同じ保護)。テストツールなので分割せず安全長へ切る。
        text = ClampUtf8Bytes(text, 700);

        chat.freeChatField.textArea.SetText(text);
        chat.timeSinceLastMessage = 3f;
        chat.SendChat();
        WriteOut($"OK chatui ({text.Length} chars)");
    }

    private static void ExecuteChat(string text)
    {
        if (text.Length == 0) { WriteOut("ERR chat empty"); return; }

        PlayerControl lp = PlayerControl.LocalPlayer;
        if (!lp) { WriteOut("ERR no local player"); return; }

        // "/" 始まりはチャット送信でなくホストのチャットコマンドとして実行する
        // (RpcSendChat はコマンド解釈パッチ (ChatController.SendChat Prefix) を通らず生テキストが全員に流れる)。
        if (text.StartsWith('/'))
        {
            string[] args = text.Split(' ');
            Command cmd = Command.AllCommands.Find(c => c.IsThisCommand(text));
            if (cmd == null) { WriteOut($"ERR chatcmd unknown: {args[0]}"); return; }
            if (!cmd.CanUseCommand(lp, sendErrorMessage: true)) { WriteOut($"ERR chatcmd denied: {args[0]}"); return; }

            cmd.Action(lp, text, args);
            WriteOut($"OK chatcmd {args[0]}");
            return;
        }

        if (text.Length > 100) text = text[..100]; // vanilla チャット長制限側で切られる前に丸める

        bool ok = lp.RpcSendChat(text);
        WriteOut(ok ? "OK chat" : "ERR chat rejected");
    }

    // ── 接続イベント(キック検知)──────────────────────────────────────
    // HealthLogDisconnectPatch / OnPlayerJoinedPatch / OnPlayerLeftPatch から呼ばれる軽量フック。
    // 「ホストがキックされたか」「Android サブ端末が落ちたか」をポーリング無しの push 通知で判定する。

    // ロビーコードの push 通知 (`LOBBYCODE XXXXXX`)。ロビー再生成 (LobbyInactivity 切断や自動開始サイクル) で
    // コードが変わるたびに out.log へ流れるので、ld-sub.ps1 watch-rejoin がこれを tail してエミュ fleet を自動再 join できる。
    private static int _lastPushedGameId;

    private static void PushLobbyCodeIfChanged()
    {
        if (!GameStates.IsLobby) return;

        int id = AmongUsClient.Instance ? AmongUsClient.Instance.GameId : 0;
        if (id == 0 || id == _lastPushedGameId) return;

        string code = GameCode.IntToGameName(id);
        if (string.IsNullOrEmpty(code)) return;

        _lastPushedGameId = id;
        WriteOut($"LOBBYCODE {code.ToUpperInvariant()}");
    }

    // 公式鯖はゲームを開始しないまま放置したロビーを約600秒で LobbyInactivity 切断する (実測 596s/619s)。
    // 窓を踏む前に一度だけ WARN を push して、運転側が start か段取り繰り上げを判断できるようにする。
    // タイマーは「ロビーに入った時点」から数える (試合を挟んだらロビー復帰時点から数え直し — 実測挙動と一致)。
    private const long LobbyIdleWarnAtSec = 480;
    private static long _lobbyIdleSince;
    private static bool _lobbyIdleWarned;
    private static bool _wasLobby;

    private static void WarnLobbyIdleDeadline()
    {
        bool lobby = GameStates.IsLobby;
        long now = Utils.TimeStamp;

        if (lobby && !_wasLobby)
        {
            _lobbyIdleSince = now;
            _lobbyIdleWarned = false;
        }

        _wasLobby = lobby;

        if (!lobby || _lobbyIdleWarned || _lobbyIdleSince == 0) return;
        if (!AmongUsClient.Instance || !AmongUsClient.Instance.AmHost || !GameStates.IsOnlineGame) return;

        long age = now - _lobbyIdleSince;
        if (age < LobbyIdleWarnAtSec) return;

        _lobbyIdleWarned = true;
        WriteOut($"WARN lobby idle {age}s — server closes idle lobbies at ~600s (LobbyInactivity); start a game or expect a relobby soon");
    }

    private static string _lastDisconnect;
    private static long _lastDisconnectTs;

    public static void OnDisconnect(DisconnectReasons reason, string stringReason)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (Main.EnableTestBridge is not { Value: true }) return;

        EnsureInit();
        if (_dir == null) return;

        try
        {
            string str = (stringReason ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
            _lastDisconnect = str.Length > 0 ? $"{reason} ({str})" : reason.ToString();
            _lastDisconnectTs = Utils.TimeStamp;
            StopWalk(false); // 切断後に velocity を触らない
            WriteOut($"DISCONNECTED {_lastDisconnect}");
            AbortWaitOnDisconnect();
        }
        catch { }
    }

    // ホスト切断はロビーごと消える = このロビー/試合を前提にした wait は充足しえない。
    // 自動再ホストで新コードのロビーが立ち直っても客は join のやり直しになるため、
    // timeout まで寝かせず即 ERR で運転側へ返す。sleep / marker / Lobby・Menu 待ちは切断を跨いでも
    // 意味が変わらない (再ホスト成立の待ち受けに使われる) ので生かす。
    private static void AbortWaitOnDisconnect()
    {
        WaitState w = _activeWait;
        if (w == null) return;

        bool survivable = w.Kind is "sleep" or "marker" || (w.Kind == "phase" && IsOutOfGamePhase(w.Arg));
        if (survivable) return;

        _activeWait = null;
        WriteOut($"ERR wait aborted {w.Cond} (disconnected: {_lastDisconnect} after {Utils.TimeStamp - w.StartTs}s)");
    }

    public static void OnPlayerJoined(ClientData client)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (Main.EnableTestBridge is not { Value: true }) return;

        EnsureInit();
        if (_dir == null) return;

        try
        {
            _joinCount++; // `wait join` の充足判定用(push 通知と同じ発火点)
            string name = client?.PlayerName ?? "?";
            WriteOut($"PLAYERJOINED {name} (client {client?.Id ?? -1})");
        }
        catch { }
    }

    public static void OnPlayerLeft(ClientData data, DisconnectReasons reason)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (Main.EnableTestBridge is not { Value: true }) return;

        EnsureInit();
        if (_dir == null) return;

        try
        {
            var pid = 255;
            try { if (data != null && data.Character) pid = data.Character.PlayerId; }
            catch { }

            WriteOut($"PLAYERLEFT {(pid == 255 ? "?" : pid.ToString())} {data?.PlayerName ?? "?"} ({reason})");
        }
        catch { }
    }

    // ── Layer D: エラーリングバッファ ─────────────────────────────────

    private const int ErrorRingCap = 50;
    private static readonly Queue<string> ErrorRing = new(ErrorRingCap);
    private static int TotalErrorsRecorded;

    // Logger(Debugger.cs)の Error/Fatal 経路から呼ばれる。ファイル I/O 無し・超軽量必須。
    public static void RecordError(string tag, string text)
    {
        if (Main.EnableTestBridge is not { Value: true }) return;

        try
        {
            string t = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\\n");
            if (t.Length > 500) t = t[..500] + "...";

            lock (ErrorRing)
            {
                TotalErrorsRecorded++;
                if (ErrorRing.Count >= ErrorRingCap) ErrorRing.Dequeue();
                ErrorRing.Enqueue($"[{Utils.TimeStamp}] [{tag}] {t}");
            }
        }
        catch { }
    }

    private static void ExecuteErrors(string arg)
    {
        int n = 10;
        if (arg.Length > 0 && int.TryParse(arg, out int parsed)) n = Math.Clamp(parsed, 1, ErrorRingCap);

        string[] snapshot;
        int total;

        lock (ErrorRing)
        {
            snapshot = ErrorRing.ToArray();
            total = TotalErrorsRecorded;
        }

        WriteOut($"OK errors ({total} total since launch, showing last {Math.Min(n, snapshot.Length)})");
        for (int i = Math.Max(0, snapshot.Length - n); i < snapshot.Length; i++) WriteOut($"E {snapshot[i]}");
    }

    // ── Layer D2: 全レベルログリング + ゲーム内 grep ──────────────────
    // log.html を外部 grep する2チャンネル運用を潰すための in-proc 検索窓。
    // リングの中身は log.html と同じフィルタ(DisableList / Debug ゲート)通過後の行なので内容は一致する。

    private const int LogRingCap = 1000;
    private const int LogLineMaxChars = 300;
    private static readonly Queue<string> LogRing = new(LogRingCap);
    private static long TotalLogsRecorded; // リング先頭のシーケンス復元と marker 走査位置の管理用

    // Logger(Debugger.cs)の全レベル経路から呼ばれる。ファイル I/O 無し・超軽量必須。
    public static void RecordLog(BepInEx.Logging.LogLevel level, string tag, string text)
    {
        if (Main.EnableTestBridge is not { Value: true } || !OperatingSystem.IsWindows()) return;

        try
        {
            string t = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\\n");
            if (t.Length > LogLineMaxChars) t = t[..LogLineMaxChars] + "...";

            // log.html と突き合わせられるよう壁時計 HH:mm:ss(out.log の unix 秒とはあえて別系)
            string line = $"[{DateTime.Now:HH:mm:ss}][{level}][{tag}]{t}";

            lock (LogRing)
            {
                TotalLogsRecorded++;
                if (LogRing.Count >= LogRingCap) LogRing.Dequeue();
                LogRing.Enqueue(line);
            }
        }
        catch { }
    }

    // ── Layer D3: Boehm census 手動発火 ──────────────────
    private static void ExecuteBcensus()
    {
        MemCensus.RunNow("bridge");
        WriteOut("OK bcensus snapshot written to Health.log (CENSUS/CENSUSTOP/CENSUSREF/BCENSUS/BCENSUSTOP)");
    }

    private static void ExecuteGrep(string rest)
    {
        if (rest.Length == 0)
        {
            WriteOut("ERR grep usage: grep <pattern> [n]  (末尾が数字1トークンなら件数指定と解釈)");
            return;
        }

        var n = 20;
        int idx = rest.LastIndexOf(' ');

        if (idx > 0 && int.TryParse(rest[(idx + 1)..], out int parsed))
        {
            n = Math.Clamp(parsed, 1, 50);
            rest = rest[..idx].TrimEnd();
        }

        List<string> matches = [];
        int ringCount;

        lock (LogRing)
        {
            ringCount = LogRing.Count;
            foreach (string line in LogRing)
                if (line.Contains(rest, StringComparison.OrdinalIgnoreCase))
                    matches.Add(line);
        }

        WriteOut($"OK grep \"{rest}\" ({matches.Count} matches in last {ringCount} log lines, showing last {Math.Min(n, matches.Count)})");
        for (int i = Math.Max(0, matches.Count - n); i < matches.Count; i++) WriteOut($"L {matches[i]}");
    }

    // ── Layer E: wait(待ち合わせ)─────────────────────────────────────
    // 条件成立 or timeout まで後続ディレクティブの実行を停める。評価は Tick(1/sec)。
    // driver 側の sleep+tail ポーリングを「wait 1行 → OK/ERR 1行」に置き換えるのが目的。

    private sealed class WaitState
    {
        public string Cond;          // 表示用の正規化済み条件
        public string Kind;          // phase | players | marker | join | arrived | sleep
        public string Arg;           // phase 名 / marker 部分一致文字列
        public long StartTs;
        public int TimeoutSec;
        public long JoinCountAtStart;
        public long MarkerScanSeq;   // 走査済みシーケンス(毎 Tick の再走査を避ける)
        public bool SawInGame;       // phase 待ち中に一度でもゲーム内フェーズを観測したか(fail-fast 判定用)
    }

    private static WaitState _activeWait;
    private static long _joinCount;

    private static void ExecuteWait(string rest)
    {
        if (rest.Equals("cancel", StringComparison.OrdinalIgnoreCase))
        {
            // アクティブな wait 中の cancel は TryConsumeWaitCancel が先に拾う。ここに来たら待機なし。
            WriteOut("ERR wait none active");
            return;
        }

        if (rest.Length == 0)
        {
            WriteOut("ERR wait usage: wait <phase=X|players=N|marker:text|join|arrived> [timeoutSec=60]");
            return;
        }

        var timeout = 60;
        int idx = rest.LastIndexOf(' ');

        if (idx > 0 && int.TryParse(rest[(idx + 1)..], out int parsed))
        {
            timeout = Math.Clamp(parsed, 5, 300);
            rest = rest[..idx].TrimEnd();
        }

        var w = new WaitState
        {
            StartTs = Utils.TimeStamp,
            TimeoutSec = timeout,
            JoinCountAtStart = _joinCount
        };

        if (rest.StartsWith("phase=", StringComparison.OrdinalIgnoreCase))
        {
            w.Kind = "phase";
            w.Arg = rest[6..].Trim();
        }
        else if (rest.StartsWith("players=", StringComparison.OrdinalIgnoreCase))
        {
            // エミュ複数台の一括合流待ち: 総プレイヤー数が N 以上で充足(join イベント数でなく現在数 — 途中退出に強い)
            w.Kind = "players";
            w.Arg = rest[8..].Trim();

            if (!int.TryParse(w.Arg, out int want) || want < 1)
            {
                WriteOut("ERR wait usage: wait players=<N>  (ホスト含む総プレイヤー数が N 以上になるまで)");
                return;
            }
        }
        else if (rest.StartsWith("marker:", StringComparison.OrdinalIgnoreCase))
        {
            w.Kind = "marker";
            w.Arg = rest[7..].Trim();
            lock (LogRing) w.MarkerScanSeq = TotalLogsRecorded; // wait 開始以降の行だけを対象にする
        }
        else if (rest.Equals("join", StringComparison.OrdinalIgnoreCase))
            w.Kind = "join";
        else if (rest.Equals("arrived", StringComparison.OrdinalIgnoreCase))
            w.Kind = "arrived";
        else
        {
            WriteOut("ERR wait usage: wait <phase=X|players=N|marker:text|join|arrived> [timeoutSec=60]");
            return;
        }

        if (w.Kind is "phase" or "marker" && w.Arg.Length == 0)
        {
            WriteOut("ERR wait usage: wait <phase=X|players=N|marker:text|join|arrived> [timeoutSec=60]");
            return;
        }

        w.Cond = w.Kind switch
        {
            "phase" => $"phase={w.Arg}",
            "players" => $"players={w.Arg}",
            "marker" => $"marker:{w.Arg}",
            _ => w.Kind
        };

        _activeWait = w;
        EvaluateActiveWait(); // 既に条件成立なら 0s で即 OK
    }

    private static void ExecuteSleep(string rest)
    {
        if (!int.TryParse(rest, out int sec) || sec < 1)
        {
            WriteOut("ERR sleep usage: sleep <秒 1-120>");
            return;
        }

        sec = Math.Clamp(sec, 1, 120);

        // wait の機構を流用: 充足条件が「時間経過のみ」の WaitState。cancel も wait cancel で効く
        _activeWait = new WaitState
        {
            Cond = $"sleep {sec}s",
            Kind = "sleep",
            StartTs = Utils.TimeStamp,
            TimeoutSec = sec
        };
    }

    private static void EvaluateActiveWait()
    {
        WaitState w = _activeWait;
        if (w == null) return;

        long elapsed = Utils.TimeStamp - w.StartTs;

        bool ok;

        try
        {
            ok = w.Kind switch
            {
                "phase" => SafeState().Equals(w.Arg, StringComparison.OrdinalIgnoreCase),
                "sleep" => elapsed >= w.TimeoutSec, // 時間経過のみで充足(timeout 分岐より先に OK 側で拾う)
                "players" => SafePlayerCount() >= int.Parse(w.Arg),
                "join" => _joinCount > w.JoinCountAtStart,
                "arrived" => _walkTarget == null, // walk 終了(到着/stuck/timeout/cancel)で充足。終端の実際の結果行は直前の out.log にある
                "marker" => ScanForMarker(w),
                _ => true
            };
        }
        catch (Exception e)
        {
            // 評価中の例外で _activeWait が残ると timeout 判定にも到達できず恒久ハングするため、強制解除する
            _activeWait = null;
            WriteOut($"ERR wait exception {w.Cond}");
            Utils.ThrowException(e);
            return;
        }

        if (ok)
        {
            _activeWait = null;
            WriteOut($"OK wait {w.Cond} ({elapsed}s)");
            return;
        }

        // ゲーム内フェーズ待ちの fail-fast: 試合が終わってロビーへ戻ったら、この待ちは次の試合まで
        // 充足しえない — timeout まで寝かせず即 ERR で運転側へ返す (即勝利終了した試合の
        // phase=InTask 待ちが 90 秒まるごと死んだ実測への対処)。
        if (w.Kind == "phase" && IsPhaseWaitDead(w))
        {
            _activeWait = null;
            WriteOut($"ERR wait aborted {w.Cond} (game ended, back in lobby after {elapsed}s)");
            return;
        }

        if (elapsed >= w.TimeoutSec)
        {
            _activeWait = null;
            WriteOut($"ERR wait timeout {w.Cond} ({w.TimeoutSec}s)");
        }
    }

    // Lobby/Menu は「試合の外」— これら以外のフェーズを目標にした待ちだけが試合終了で死ぬ。
    private static bool IsOutOfGamePhase(string s)
    {
        return s.Equals("Lobby", StringComparison.OrdinalIgnoreCase) || s.Equals("Menu", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPhaseWaitDead(WaitState w)
    {
        if (IsOutOfGamePhase(w.Arg)) return false; // Lobby/Menu 待ちは試合終了がむしろ充足へ向かう

        string cur = SafeState();

        if (!IsOutOfGamePhase(cur) && cur != "?")
        {
            w.SawInGame = true; // 試合中を観測 — ここからロビーへ戻ったら「終わった」と断定できる
            return false;
        }

        return w.SawInGame && IsOutOfGamePhase(cur);
    }

    private static bool ScanForMarker(WaitState w)
    {
        lock (LogRing)
        {
            long firstSeq = TotalLogsRecorded - LogRing.Count;
            var skip = (int)Math.Max(0, w.MarkerScanSeq - firstSeq);
            var i = 0;

            foreach (string line in LogRing)
            {
                if (i++ < skip) continue;
                if (line.Contains(w.Arg, StringComparison.OrdinalIgnoreCase)) return true;
            }

            w.MarkerScanSeq = TotalLogsRecorded;
            return false;
        }
    }

    // ExecuteWait 側のディスパッチ正規化 (directive[5..].Trim()) と同じ判定に揃える。
    // 完全一致だけだと "wait  cancel" (二重スペース等) が割り込みで拾えず無音で滞留する。
    private static bool IsWaitCancel(string d)
    {
        return d.StartsWith("wait ", StringComparison.OrdinalIgnoreCase) && d[5..].Trim().Equals("cancel", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryConsumeWaitCancel()
    {
        WaitState w = _activeWait;
        if (w == null) return false;

        var found = false;
        foreach (string d in PendingDirectives)
            if (IsWaitCancel(d)) { found = true; break; }

        if (!found) return false;

        // 最初の "wait cancel" 1行だけ取り除き、他のディレクティブは順序を保って残す
        var removed = false;
        List<string> rest = [];

        while (PendingDirectives.Count > 0)
        {
            string d = PendingDirectives.Dequeue();
            if (!removed && IsWaitCancel(d)) { removed = true; continue; }
            rest.Add(d);
        }

        foreach (string d in rest) PendingDirectives.Enqueue(d);

        long elapsed = Utils.TimeStamp - w.StartTs;
        _activeWait = null;
        WriteOut("> wait cancel");
        WriteOut($"OK wait cancel ({w.Cond} aborted after {elapsed}s)");
        return true;
    }

    // ── safe accessors / JSON helpers ─────────────────────────────────

    private static string SafeState()
    {
        try { string s = HealthLog.GetState(); return string.IsNullOrEmpty(s) ? "?" : s; }
        catch { return "?"; }
    }

    private static string SafeGameMode()
    {
        try { return Options.CurrentGameMode.ToString(); }
        catch { return "?"; }
    }

    private static string SafeName(PlayerControl pc)
    {
        // 装飾ロビー名は TMP タグだらけで機械可読性ゼロ (state の JSON が数百バイト膨れる) — タグ除去+空白圧縮して返す
        try
        {
            string raw = (pc.Data?.PlayerName ?? "").RemoveHtmlTags();
            return System.Text.RegularExpressions.Regex.Replace(raw, @"\s+", " ").Trim();
        }
        catch { return ""; }
    }

    private static string SafeRole(PlayerControl pc)
    {
        try { return Utils.GetRoleName(pc.GetCustomRole(), false); }
        catch { return "?"; }
    }

    private static bool SafeAlive(PlayerControl pc)
    {
        try { return pc.Data is { IsDead: false }; }
        catch { return true; }
    }

    private static int SafeColorId(PlayerControl pc)
    {
        try { return pc.Data?.DefaultOutfit?.ColorId ?? -1; }
        catch { return -1; }
    }

    private static void AppendGameCode(StringBuilder sb)
    {
        try
        {
            int id = AmongUsClient.Instance ? AmongUsClient.Instance.GameId : 0;
            string code = id == 0 ? null : GameCode.IntToGameName(id);
            if (string.IsNullOrEmpty(code)) { sb.Append("null"); return; }

            sb.Append(JStr(code.ToUpperInvariant()));
        }
        catch { sb.Append("null"); }
    }

    private static int SafeClientId(PlayerControl pc)
    {
        try { return pc.OwnerId; }
        catch { return -1; }
    }

    private static int SafePlayerCount()
    {
        try
        {
            var n = 0;
            foreach (PlayerControl pc in Main.AllPlayerControls)
                if (pc)
                    n++;

            return n;
        }
        catch { return 0; }
    }

    private static Vector2 SafePos(PlayerControl pc)
    {
        try { return pc.GetTruePosition(); }
        catch { return Vector2.zero; }
    }

    private static string CleanLabel(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return s.Length > 80 ? s[..80] + "…" : s;
    }

    private static string Sanitize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";

        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (char.IsLetterOrDigit(c) || c is '_' or '-' or '.') sb.Append(c);
            else if (c == ' ') sb.Append('_');
        }

        return sb.ToString();
    }

    private static string F(float v)
    {
        if (float.IsNaN(v) || float.IsInfinity(v)) return "0";
        return v.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static string JStr(string s)
    {
        if (s == null) return "\"\"";

        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }

        sb.Append('"');
        return sb.ToString();
    }

    private static long _outBytesSinceRotateCheck = 64 * 1024; // 初回は必ずチェックさせる (閾値ちょうどから始める — MaxValue 初期化は加算で負数へ巻き戻り永久に走らない)

    private static void WriteOut(string line)
    {
        if (_outPath == null) return;

        try
        {
            string payload = $"[{Utils.TimeStamp}] {line}\n";

            // ローテート判定の File.Exists+FileInfo は毎回やらず、書込累積が閾値を超えた時だけ実サイズを見る
            // (50Hz 化で WriteOut が最大 ~100/秒 = InnerNetClient.FixedUpdate 上の同期 I/O になったため)。
            _outBytesSinceRotateCheck += System.Text.Encoding.UTF8.GetByteCount(payload);
            if (_outBytesSinceRotateCheck >= 64 * 1024)
            {
                _outBytesSinceRotateCheck = 0;
                if (File.Exists(_outPath) && new FileInfo(_outPath).Length > MaxOutFileBytes)
                {
                    string prev = Path.Combine(_dir, "bridge-out.prev.log");

                    try
                    {
                        if (File.Exists(prev)) File.Delete(prev);
                        File.Move(_outPath, prev);
                    }
                    catch { }
                }
            }

            File.AppendAllText(_outPath, payload);
        }
        catch { }
    }
}

// walk ディレクティブの駆動源。vanilla が入力から velocity を書いた「後」に上書きするため Postfix。
// FixedUpdate は private なので nameof でなく文字列指定。
[HarmonyPatch(typeof(PlayerPhysics), "FixedUpdate")]
internal static class TestBridgeWalkPatch
{
    // PlayerPhysics.FixedUpdate は 50Hz×プレイヤー数の最ホット経路で、素通し Postfix でも detour 分の
    // 呼び出し税を払い続ける。TestBridge 無効 (既定) ならパッチ自体を当てない。有効化は起動前の
    // config 設定が前提 (実行中トグルでは patch は増えない)。
    public static bool Prepare() => Main.EnableTestBridge is { Value: true };

    public static void Postfix(PlayerPhysics __instance)
    {
        TestBridge.OnPlayerPhysicsFixedUpdate(__instance);
    }
}
