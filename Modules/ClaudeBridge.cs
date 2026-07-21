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

// Claude がゲームを遠隔テストするための観測・操作ブリッジ(既定 OFF、config でのみ有効化)。
// <Desktop>/EndKnot_Logs/claude-cmd.txt を 1/sec ポーリングしてチャットコマンドを実行し、
// claude-out.log に結果を書き出す。スクショは Screens/ 配下へ保存する。
// 全処理はメインスレッド(FixedUpdateCaller の 1/sec ゲート + コルーチン)のみで完結させ、
// FileSystemWatcher 等の非同期監視は使わない。host-only 前提(Command.Action は LocalPlayer=host で実行)。
public static class ClaudeBridge
{
    private const int MaxBatchLines = 20; // 1回のファイル読取で受け付けるディレクティブ数の上限
    private const long MaxOutFileBytes = 2 * 1024 * 1024; // claude-out.log の .prev ローテート閾値

    private static bool _inited;
    private static string _dir;
    private static string _cmdPath;
    private static string _outPath;
    private static string _statePath;
    private static string _screensDir;

    private static bool _captureInFlight;
    private static long _lastAutoShotTs;

    // ファイルから読み取った未実行ディレクティブのキュー。排出レート 1件/秒を守るため、
    // ファイル読取と削除は一括で行い、実行だけを Tick ごとに 1件ずつ進める。
    private static readonly Queue<string> PendingDirectives = new();

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

            _cmdPath = Path.Combine(_dir, "claude-cmd.txt");
            _outPath = Path.Combine(_dir, "claude-out.log");
            _statePath = Path.Combine(_dir, "claude-state.json");
            _screensDir = Path.Combine(_dir, "Screens");
            Directory.CreateDirectory(_screensDir);
        }
        catch { _dir = null; }
    }

    public static void Tick()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (Main.EnableClaudeBridge is not { Value: true }) return;

        EnsureInit();
        if (_dir == null) return;

        try { DrainCommandFile(); }
        catch (Exception e) { Utils.ThrowException(e); }

        try { HandleAutoScreenshot(); }
        catch (Exception e) { Utils.ThrowException(e); }
    }

    // Utils.SendLocally からの写し窓口。ホストローカル表示のチャット/通知を claude-out.log にも記録する。
    public static void OnHostSystemMessage(string title, string text)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (Main.EnableClaudeBridge is not { Value: true }) return;

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
        if (PendingDirectives.Count == 0)
        {
            List<string> lines = TryReadAndClearCmdFile();
            if (lines == null) return;

            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;

                PendingDirectives.Enqueue(line);
                if (PendingDirectives.Count >= MaxBatchLines) break;
            }
        }

        if (PendingDirectives.Count == 0) return;

        string directive = PendingDirectives.Dequeue();
        ExecuteDirective(directive);
    }

    // 既読管理 = 削除方式。実行前にファイルを消す(flood-clear の教訓)。削除失敗→truncate、
    // それも失敗したら今回は何も実行しない(誤再実行ゼロを構造で保証)。
    private static List<string> TryReadAndClearCmdFile()
    {
        if (!File.Exists(_cmdPath)) return null;

        List<string> lines;

        try
        {
            using var fs = new FileStream(_cmdPath, FileMode.Open, FileAccess.Read, FileShare.None);
            using var sr = new StreamReader(fs, Encoding.UTF8);

            lines = [];
            string line;
            while ((line = sr.ReadLine()) != null) lines.Add(line);
        }
        catch { return null; } // ロック中等 = 次回リトライ

        try { File.Delete(_cmdPath); }
        catch
        {
            try { File.WriteAllText(_cmdPath, string.Empty); }
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

        if (directive.Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            WriteOut("HELP directives: state | screenshot | click <h|label:x> | getopt <pattern> | setopt <name> <idx|on|off|~real> | forcerole <id|host|clear> [EnumName] | start | tp <x> <y> | tp <playerId> | walk <x> <y> | walk <playerId> | walk stop | vote <playerId|skip> | chat <text> | use <kill|vent|pet|ability|report|sabotage> | errors [n] | /<chatcommand>");
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
        if (Main.ClaudeBridgeAutoScreenshot is not { Value: true }) return;

        long now = Utils.TimeStamp;
        int interval = Math.Max(1, Main.ClaudeBridgeScreenshotInterval?.Value ?? 20);

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
            int keep = Math.Max(1, Main.ClaudeBridgeScreenshotKeep?.Value ?? 30);

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
        sb.Append("\"players\":["); int np = AppendPlayers(sb); sb.Append("],");
        sb.Append("\"cnos\":["); int nc = AppendCnos(sb); sb.Append("],");
        sb.Append("\"hud\":"); AppendHud(sb); sb.Append(',');
        sb.Append("\"walk\":"); AppendWalk(sb); sb.Append(',');
        sb.Append("\"lastDisconnect\":"); AppendLastDisconnect(sb); sb.Append(',');
        sb.Append("\"ui\":["); int nb = AppendButtons(sb, buttons); sb.Append(']');
        sb.Append('}');

        File.WriteAllText(_statePath, sb.ToString());
        WriteOut($"OK state ({np} players, {nc} cnos, {nb} buttons) -> claude-state.json");
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

        string optsPath = Path.Combine(_dir, "claude-opts.json");
        File.WriteAllText(optsPath, sb.ToString());
        WriteOut($"OK getopt {Math.Min(matches.Count, cap)}/{matches.Count} matches -> claude-opts.json");
    }

    private static void ExecuteSetOpt(string rest)
    {
        int sp = rest.LastIndexOf(' ');
        if (sp <= 0) { WriteOut("ERR setopt usage: setopt <name> <index|on|off|~realValue>"); return; }

        string name = rest[..sp].Trim();
        string valueArg = rest[(sp + 1)..].Trim();

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

        OptionItem opt = exact[0];

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
        if (parts.Length != 2) { WriteOut("ERR forcerole usage: forcerole <playerId|host|clear> <CustomRolesEnumName>"); return; }

        byte targetId;

        if (parts[0].Equals("host", StringComparison.OrdinalIgnoreCase))
        {
            if (!PlayerControl.LocalPlayer) { WriteOut("ERR no local player"); return; }
            targetId = PlayerControl.LocalPlayer.PlayerId;
        }
        else if (!byte.TryParse(parts[0], out targetId))
        {
            WriteOut($"ERR forcerole bad player id: {parts[0]}");
            return;
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
            dest = target.GetTruePosition();
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
        WriteOut(ok ? $"OK tp -> [{F(dest.x)}, {F(dest.y)}]" : "ERR tp rejected (state check)");
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
        WriteOut($"OK use {button}");
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

    // ClaudeBridgeWalkPatch(毎物理 tick)から呼ばれる。先頭の _walkTarget null チェックで
    // 非使用時のコストは実質ゼロ。到着/stuck/timeout の終端イベントだけ out.log に書く。
    internal static void OnPlayerPhysicsFixedUpdate(PlayerPhysics physics)
    {
        if (_walkTarget == null) return;
        if (Main.EnableClaudeBridge is not { Value: true }) { _walkTarget = null; return; }

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

        // CmdCastVote はホストなら CastVote 直行 = EHR の MeetingHudCastVotePatch(OnVote 等)を通る通常経路。
        // ただし CancelsVote 系役職/死亡ガードは Prefix 内でサイレントに投票を握り潰すので、
        // 呼び出し後に DidVote で「実際に反映されたか」を検証してから OK/ERR を出し分ける。
        meeting.CmdCastVote(lp.PlayerId, suspect);

        bool landed;
        try { landed = meeting.DidVote(lp.PlayerId); }
        catch { landed = true; } // 検証不能時は楽観扱い(SYS 写しで役職側の拒否メッセージは別途見える)

        string targetStr = suspect == 253 ? "skip" : suspect.ToString();
        WriteOut(landed ? $"OK vote {targetStr}" : $"ERR vote {targetStr} silently canceled (role logic / dead)");
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

    private static string _lastDisconnect;
    private static long _lastDisconnectTs;

    public static void OnDisconnect(DisconnectReasons reason, string stringReason)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (Main.EnableClaudeBridge is not { Value: true }) return;

        EnsureInit();
        if (_dir == null) return;

        try
        {
            string str = (stringReason ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
            _lastDisconnect = str.Length > 0 ? $"{reason} ({str})" : reason.ToString();
            _lastDisconnectTs = Utils.TimeStamp;
            StopWalk(false); // 切断後に velocity を触らない
            WriteOut($"DISCONNECTED {_lastDisconnect}");
        }
        catch { }
    }

    public static void OnPlayerJoined(ClientData client)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (Main.EnableClaudeBridge is not { Value: true }) return;

        EnsureInit();
        if (_dir == null) return;

        try
        {
            string name = client?.PlayerName ?? "?";
            WriteOut($"PLAYERJOINED {name} (client {client?.Id ?? -1})");
        }
        catch { }
    }

    public static void OnPlayerLeft(ClientData data, DisconnectReasons reason)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (Main.EnableClaudeBridge is not { Value: true }) return;

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
        if (Main.EnableClaudeBridge is not { Value: true }) return;

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
        try { return pc.Data?.PlayerName ?? ""; }
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
                    string prev = Path.Combine(_dir, "claude-out.prev.log");

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
internal static class ClaudeBridgeWalkPatch
{
    public static void Postfix(PlayerPhysics __instance)
    {
        ClaudeBridge.OnPlayerPhysicsFixedUpdate(__instance);
    }
}
