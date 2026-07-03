using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
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
