using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace EndKnotInstaller;

/// <summary>
/// 起動しない時の診断情報を zip 1 つにまとめる。mod が動かない状態でも集められるようインストーラー側に置く。
/// ゲームフォルダを消して入れ直すと手掛かりも消えるので、入れ直す前に保存してもらう。
/// </summary>
public static class DiagnosticsCollector
{
    // そのまま写すファイル (ゲームフォルダからの相対パス)
    private static readonly string[] GameFiles =
    [
        "BepInEx/LogOutput.log",
        "BepInEx/ErrorLog.log",
        "BepInEx/log.html",
        "doorstop_config.ini",
        ".doorstop_version",
        "BepInEx/EndKnot.package.txt",
        "BepInEx/config/BepInEx.cfg",
        "BepInEx/config/EndKnot.boot.config",
        "BepInEx/config/EndKnot.PackageSync.txt",
        "BepInEx/config/EndKnot.GcUafStatus.txt",
        "BepInEx/cache/EndKnot.package-cleanup.txt",
        "BepInEx/interop/assembly-hash.txt",
        "Among Us_Data/boot.config"
    ];

    // mod 本体の設定は API キー等を含みうるので、起動とメモリまわりの鍵だけを抜き出す
    private const string PluginConfig = "BepInEx/config/cf13a94a-1671-41f1-85a8-3e32770cf09e.cfg";

    private static readonly string[] PluginConfigKeys =
    [
        "ForceDisableIncrementalGc", "UnityAllocatorTuning", "SweepDestroyedReferences",
        "ReleaseFinishedCoroutines", "TrimForeignControllerMaps"
    ];

    // 目録と違って当然のもの (利用者が書き換える設定)
    private const string ConfigPrefix = "BepInEx/config/";

    // 配布物の入れ替え途中の残骸
    private static readonly string[] LeftoverPatterns = ["*.endknot-new", "*.endknot-old", "*.part", "*.tmp"];

    // 1 ファイルあたりの上限。超えた分は末尾だけ残す
    private const long MaxCopyBytes = 8L * 1024 * 1024;

    /// <summary>デスクトップに zip を書き出してそのパスを返す</summary>
    public static string Collect(GameInstall install, Action<string> log)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var zipPath = Path.Combine(desktop, $"EndKnot_Diagnostics_{DateTime.Now:yyyyMMdd_HHmmss}.zip");

        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);

        foreach (var rel in GameFiles)
            AddFile(zip, Path.Combine(install.Path, rel), "game/" + rel);

        // BepInEx が本体のログを開く前に止まった時は、ゲーム直下の preloader_*.log にしか記録が残らない
        // (その時の LogOutput.log は前回の起動のまま)。空のファイルも「空だった」手掛かりなので写す
        foreach (var f in SafeFiles(install.Path, "preloader_*.log").OrderByDescending(File.GetLastWriteTime).Take(3))
            AddFile(zip, f, "game/" + Path.GetFileName(f));

        // mod がデスクトップに書く稼働ログ
        var modLogs = Path.Combine(desktop, "EndKnot_Logs");
        foreach (var name in new[] { "EndKnot-Health.log", "EndKnot-Health.prev.log", "EndKnot-Timeline.log" })
            AddFile(zip, Path.Combine(modLogs, name), "EndKnot_Logs/" + name);

        // Unity 自身のログ (BepInEx より前で止まった時の手掛かり)
        var localLow = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "Innersloth", "Among Us");
        AddFile(zip, Path.Combine(localLow, "Player.log"), "unity/Player.log");
        AddFile(zip, Path.Combine(localLow, "Player-prev.log"), "unity/Player-prev.log");

        // Unity のクラッシュフォルダは最新 1 件のテキストだけ (ダンプは大きいので入れない)
        var crashRoot = Path.Combine(Path.GetTempPath(), "Innersloth", "Among Us", "Crashes");
        var latestCrash = SafeDirs(crashRoot).OrderByDescending(Directory.GetLastWriteTime).FirstOrDefault();
        if (latestCrash != null)
            foreach (var f in SafeFiles(latestCrash).Where(f => f.EndsWith(".log", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)))
                AddFile(zip, f, $"unity/crash-{Path.GetFileName(latestCrash)}/{Path.GetFileName(f)}");

        log("ゲームフォルダを調べています (配布物の照合に数秒かかります)…");
        AddText(zip, "report.txt", BuildReport(install, crashRoot));

        return zipPath;
    }

    private static string BuildReport(GameInstall install, string crashRoot)
    {
        var sb = new StringBuilder();
        var root = install.Path;

        sb.AppendLine("== 環境");
        sb.AppendLine($"作成日時: {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"インストーラー: {typeof(DiagnosticsCollector).Assembly.GetName().Version}");
        sb.AppendLine($"OS: {Environment.OSVersion} / 64bit OS: {Environment.Is64BitOperatingSystem}");
        sb.AppendLine($"物理メモリ: {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024} MB");
        Try(sb, () =>
        {
            var drive = new DriveInfo(Path.GetPathRoot(root)!);
            sb.AppendLine($"空き容量 ({drive.Name}): {drive.AvailableFreeSpace / 1024 / 1024} MB");
        });
        sb.AppendLine($"Among Us 起動中: {ModInstaller.IsGameRunning()}");

        sb.AppendLine();
        sb.AppendLine("== ゲーム");
        sb.AppendLine($"種類: {install.Platform}");
        sb.AppendLine($"場所: {root}");
        Try(sb, () => sb.AppendLine($"Among Us.exe: {FileVersionInfo.GetVersionInfo(install.ExePath).FileVersion} / {Machine(install.ExePath)}"));
        sb.AppendLine($"winhttp.dll: {Describe(install.WinhttpPath)} / {Machine(install.WinhttpPath)}");
        sb.AppendLine($"winhttp.dll.disabled: {Describe(install.WinhttpDisabledPath)}");
        sb.AppendLine($"GameAssembly.dll: {Describe(Path.Combine(root, "GameAssembly.dll"))}");
        sb.AppendLine($"EndKnot.dll: {Describe(install.PluginDllPath)} / 版 {install.ModVersion ?? "-"}");

        sb.AppendLine();
        sb.AppendLine("== 配布物の照合 (BepInEx/EndKnot.package.txt)");
        Try(sb, () => CheckManifest(sb, root, install.ModVersion));

        sb.AppendLine();
        sb.AppendLine("== 入れ替えの残骸");
        Try(sb, () =>
        {
            var found = new List<string>();
            foreach (var dir in new[] { root, Path.Combine(root, "BepInEx"), Path.Combine(root, "dotnet") })
            {
                if (!Directory.Exists(dir)) continue;
                var option = dir == root ? SearchOption.TopDirectoryOnly : SearchOption.AllDirectories;
                foreach (var pattern in LeftoverPatterns)
                    found.AddRange(Directory.GetFiles(dir, pattern, option));
            }

            if (found.Count == 0) sb.AppendLine("なし");
            foreach (var f in found.Distinct()) sb.AppendLine(Describe(f, root));
        });

        sb.AppendLine();
        sb.AppendLine("== mod の設定 (起動まわりの鍵だけ)");
        Try(sb, () =>
        {
            var cfg = Path.Combine(root, PluginConfig);
            if (!File.Exists(cfg))
            {
                sb.AppendLine("なし");
                return;
            }

            foreach (var line in ReadAllLinesShared(cfg))
            {
                var key = line.Split('=', 2)[0].Trim();
                if (PluginConfigKeys.Contains(key)) sb.AppendLine(line.Trim());
            }
        });

        sb.AppendLine();
        sb.AppendLine("== ファイル一覧");
        Try(sb, () => ListDir(sb, root, root));
        foreach (var sub in new[] { "BepInEx", "BepInEx/core", "BepInEx/plugins", "BepInEx/patchers", "BepInEx/config", "BepInEx/cache" })
            Try(sb, () => ListDir(sb, Path.Combine(root, sub), root));
        foreach (var sub in new[] { "BepInEx/interop", "BepInEx/unity-libs", "dotnet" })
            Try(sb, () => Summarize(sb, Path.Combine(root, sub), root));

        sb.AppendLine();
        sb.AppendLine("== Unity のクラッシュ記録 (新しい順に 5 件)");
        var crashes = SafeDirs(crashRoot).OrderByDescending(Directory.GetLastWriteTime).Take(5).ToList();
        if (crashes.Count == 0) sb.AppendLine("なし");
        foreach (var c in crashes) sb.AppendLine($"{Path.GetFileName(c)}  {Directory.GetLastWriteTime(c):yyyy-MM-dd HH:mm:ss}");

        return sb.ToString();
    }

    // 目録は 1 行目 "version=<版>"、以降 "<sha256> <bytes> <path>"
    private static void CheckManifest(StringBuilder sb, string root, string? modVersion)
    {
        var manifestPath = Path.Combine(root, "BepInEx", "EndKnot.package.txt");
        if (!File.Exists(manifestPath))
        {
            sb.AppendLine("目録なし (zip を手で入れた古い版か、DLL だけ差し替えた環境)");
            return;
        }

        string? version = null;
        int ok = 0, skipped = 0;
        var problems = new List<string>();

        foreach (var raw in ReadAllLinesShared(manifestPath))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("version=", StringComparison.Ordinal))
            {
                version = line["version=".Length..];
                continue;
            }

            var parts = line.Split(' ', 3);
            if (parts.Length != 3 || !long.TryParse(parts[1], out var size)) continue;

            var rel = parts[2].Replace('\\', '/').TrimStart('/');
            if (rel.StartsWith(ConfigPrefix, StringComparison.OrdinalIgnoreCase))
            {
                skipped++;
                continue;
            }

            var full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full))
            {
                problems.Add($"無い      {rel}");
                continue;
            }

            var actualSize = new FileInfo(full).Length;
            if (actualSize != size)
            {
                problems.Add($"大きさ違い {rel} (目録 {size} / 実物 {actualSize}, {File.GetLastWriteTime(full):yyyy-MM-dd HH:mm})");
                continue;
            }

            if (!Hash(full).Equals(parts[0], StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"中身違い   {rel} ({File.GetLastWriteTime(full):yyyy-MM-dd HH:mm})");
                continue;
            }

            ok++;
        }

        sb.AppendLine($"目録の版: {version ?? "-"} / EndKnot.dll の版: {modVersion ?? "-"}{(version != null && version == modVersion ? "" : "  ← 食い違い")}");
        sb.AppendLine($"一致 {ok} / 不一致 {problems.Count} / 設定のため照合せず {skipped}");
        foreach (var p in problems.Take(300)) sb.AppendLine(p);
        if (problems.Count > 300) sb.AppendLine($"…ほか {problems.Count - 300} 件");
    }

    private static void ListDir(StringBuilder sb, string dir, string root)
    {
        sb.AppendLine($"-- {Relative(dir, root)}");
        if (!Directory.Exists(dir))
        {
            sb.AppendLine("  (無い)");
            return;
        }

        foreach (var d in Directory.GetDirectories(dir).Order())
            sb.AppendLine($"  [{Path.GetFileName(d)}]  {Directory.GetLastWriteTime(d):yyyy-MM-dd HH:mm}");
        foreach (var f in Directory.GetFiles(dir).Order())
            sb.AppendLine("  " + Describe(f, dir));
    }

    // 中身の多いフォルダは件数・合計・日付の幅だけ
    private static void Summarize(StringBuilder sb, string dir, string root)
    {
        sb.AppendLine($"-- {Relative(dir, root)}");
        if (!Directory.Exists(dir))
        {
            sb.AppendLine("  (無い)");
            return;
        }

        var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Select(f => new FileInfo(f)).ToList();
        if (files.Count == 0)
        {
            sb.AppendLine("  (空)");
            return;
        }

        sb.AppendLine($"  {files.Count} 件 / {files.Sum(f => f.Length) / 1024 / 1024} MB / 最古 {files.Min(f => f.LastWriteTime):yyyy-MM-dd HH:mm} / 最新 {files.Max(f => f.LastWriteTime):yyyy-MM-dd HH:mm}");
    }

    private static string Describe(string path) => Describe(path, Path.GetDirectoryName(path)!);

    private static string Describe(string path, string relativeTo)
    {
        if (!File.Exists(path)) return "無い";
        var info = new FileInfo(path);
        return $"{Relative(path, relativeTo)}  {info.Length} bytes  {info.LastWriteTime:yyyy-MM-dd HH:mm:ss}";
    }

    // PE ヘッダの Machine。winhttp.dll とゲームのビット数が違うと mod が読まれずバニラで起動する
    private static string Machine(string path)
    {
        if (!File.Exists(path)) return "-";
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var br = new BinaryReader(fs);
            fs.Position = 0x3C;
            fs.Position = br.ReadInt32() + 4;
            return br.ReadUInt16() switch
            {
                0x8664 => "x64",
                0x014C => "x86",
                0xAA64 => "arm64",
                var m => $"0x{m:X4}"
            };
        }
        catch (Exception e) { return $"読めない ({e.GetType().Name})"; }
    }

    private static string Hash(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    private static string Relative(string path, string root)
    {
        var rel = Path.GetRelativePath(root, path);
        return rel == "." ? Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)) : rel;
    }

    // ゲームが書き込み中でも読めるように共有で開く
    private static IEnumerable<string> ReadAllLinesShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line) lines.Add(line);
        return lines;
    }

    private static void AddFile(ZipArchive zip, string source, string entryName)
    {
        try
        {
            if (!File.Exists(source)) return;
            using var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (src.Length > MaxCopyBytes) src.Position = src.Length - MaxCopyBytes;

            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            entry.LastWriteTime = File.GetLastWriteTime(source);
            using var dst = entry.Open();
            src.CopyTo(dst);
        }
        catch (Exception e)
        {
            AddText(zip, entryName + ".error.txt", $"{source}: {e.GetType().Name}: {e.Message}");
        }
    }

    private static void AddText(ZipArchive zip, string entryName, string text)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(true));
        writer.Write(text);
    }

    private static void Try(StringBuilder sb, Action action)
    {
        try { action(); }
        catch (Exception e) { sb.AppendLine($"(調べられませんでした: {e.GetType().Name}: {e.Message})"); }
    }

    private static IEnumerable<string> SafeDirs(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.GetDirectories(dir) : []; }
        catch { return []; }
    }

    private static IEnumerable<string> SafeFiles(string dir, string pattern = "*")
    {
        try { return Directory.Exists(dir) ? Directory.GetFiles(dir, pattern) : []; }
        catch { return []; }
    }
}
