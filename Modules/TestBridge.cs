using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
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

    public static void Tick()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (Main.EnableTestBridge is not { Value: true }) return;

        EnsureInit();
        if (_dir == null) return;

        try { DrainCommandFile(); }
        catch (Exception e) { Utils.ThrowException(e); }

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

    private static void DrainCommandFile()
    {
        // wait 中: 後続ディレクティブはキューに滞留させたまま条件だけを評価する。
        // cmd ファイルは読み続ける(`wait cancel` の割り込みと観測系の追い越しを受けるため)。
        if (_activeWait != null)
        {
            if (PendingDirectives.Count < MaxBatchLines) ReadCmdFileIntoQueue(duringWait: true);
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

            if (PendingDirectives.Count >= MaxBatchLines) break;
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

        if (directive.StartsWith("use ", StringComparison.OrdinalIgnoreCase))
        {
            try { ExecuteUse(directive[4..].Trim()); }
            catch (Exception e) { Utils.ThrowException(e); WriteOut("ERR use failed"); }
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
            WriteOut("HELP directives: state | screenshot | click <h|label:x> | press <h|x y> | type <text> | key <enter|escape|tab|backspace> | getopt <pattern> | setopt <name|#id> <idx|on|off|~real> | forcerole <id|name|host|clear> [EnumName] | start | hostlobby | autostart <on|off> | tp <x> <y> | tp <playerId> | walk <x> <y> | walk <playerId> | walk stop | vote <playerId|skip> | overrule <targetId> [judgeId] | chat <text> | use <kill|vent|pet|ability|report|sabotage> | vent enter <id> | vent exit | errors [n] | grep <pattern> [n] | bcensus | sleep <sec> | wait <phase=X|players=N|marker:text|join|arrived> [timeoutSec] | wait cancel | /<chatcommand>");
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
        AppendHudButton(sb, "sabotage", hud.SabotageButton);
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
        else
        {
            WriteOut("ERR tp usage: tp <x> <y> | tp <playerId>");
            return;
        }

        bool ok = Utils.TP(lp.NetTransform, dest, true);
        WriteOut(ok ? $"OK tp -> [{F(dest.x)}, {F(dest.y)}]" : "ERR tp rejected (noCheckState 経路で false になるのは SnapTo cap 超過がほぼ唯一 — KICKRISK 抑制中)");
    }

    private static void ExecuteUse(string button)
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

        byte suspect;

        if (rest.Equals("skip", StringComparison.OrdinalIgnoreCase))
            suspect = 253; // vanilla の skip vote id
        else if (!byte.TryParse(rest, out suspect))
        {
            WriteOut("ERR vote usage: vote <playerId|skip>");
            return;
        }

        try
        {
            if (meeting.DidVote(lp.PlayerId)) { WriteOut("ERR vote already voted"); return; }
        }
        catch { }

        // CastVoteChecked = EHR の投票判定 (OnVote 等) を通してからバニラ CastVote に流す共通入口。
        // CancelsVote 系役職/死亡ガードはサイレントに投票を握り潰すので、
        // 呼び出し後に DidVote で「実際に反映されたか」を検証してから OK/ERR を出し分ける。
        Patches.MeetingHudCastVotePatch.CastVoteChecked(meeting, lp.PlayerId, suspect);

        bool landed;
        try { landed = meeting.DidVote(lp.PlayerId); }
        catch { landed = true; } // 検証不能時は楽観扱い(SYS 写しで役職側の拒否メッセージは別途見える)

        string targetStr = suspect == 253 ? "skip" : suspect.ToString();
        WriteOut(landed ? $"OK vote {targetStr}" : $"ERR vote {targetStr} silently canceled (role logic / dead)");
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

    private static void WriteOut(string line)
    {
        if (_outPath == null) return;

        try
        {
            try
            {
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
            catch { }

            File.AppendAllText(_outPath, $"[{Utils.TimeStamp}] {line}\n");
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
