using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace EndKnotInstaller;

public static partial class GameDetector
{
    public static List<GameInstall> Detect()
    {
        List<GameInstall> results = [];
        try { results.AddRange(DetectSteam()); }
        catch { /* 片方の検出失敗でもう片方を巻き込まない */ }
        try { results.AddRange(DetectEpic()); }
        catch { }

        // EGL は同一インストールを指す manifest を複数残す (実測14件・パス区切りも / \ 混在) — 正規化パスで dedup
        return results
            .GroupBy(i => Path.GetFullPath(i.Path).TrimEnd('\\'), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    // ---------------- Steam ----------------

    private static IEnumerable<GameInstall> DetectSteam()
    {
        var steamRoot =
            Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string ??
            Registry.GetValue(@"HKEY_CURRENT_USER\SOFTWARE\Valve\Steam", "SteamPath", null) as string;
        if (steamRoot == null || !Directory.Exists(steamRoot)) yield break;

        List<string> libraries = [steamRoot];
        var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
        {
            foreach (Match m in LibraryPathRegex().Matches(File.ReadAllText(vdf)))
            {
                libraries.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
            }
        }

        foreach (var lib in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var gameDir = Path.Combine(lib, "steamapps", "common", "Among Us");
            if (File.Exists(Path.Combine(gameDir, "Among Us.exe")))
            {
                yield return new GameInstall(gameDir, GamePlatform.Steam, null);
            }
        }
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"")]
    private static partial Regex LibraryPathRegex();

    // ---------------- Epic ----------------

    private static IEnumerable<GameInstall> DetectEpic()
    {
        var manifestDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(manifestDir)) yield break;

        foreach (var item in Directory.EnumerateFiles(manifestDir, "*.item"))
        {
            GameInstall? found = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(item));
                var root = doc.RootElement;
                var launchExe = root.TryGetProperty("LaunchExecutable", out var exe) ? exe.GetString() : null;
                var displayName = root.TryGetProperty("DisplayName", out var dn) ? dn.GetString() : null;
                if (launchExe != "Among Us.exe" && displayName != "Among Us") continue;

                var location = root.TryGetProperty("InstallLocation", out var loc) ? loc.GetString() : null;
                var appName = root.TryGetProperty("AppName", out var an) ? an.GetString() : null;
                if (location != null && File.Exists(Path.Combine(location, "Among Us.exe")))
                {
                    found = new GameInstall(location, GamePlatform.Epic, appName);
                }
            }
            catch { }
            if (found != null) yield return found;
        }
    }

    /// <summary>手動選択されたフォルダのプラットフォーム推定 (NoS 方式: .egstore / パス表記)</summary>
    public static GamePlatform GuessPlatform(string gameDir)
    {
        if (Directory.Exists(Path.Combine(gameDir, ".egstore"))) return GamePlatform.Epic;
        if (gameDir.Contains("Epic Games", StringComparison.OrdinalIgnoreCase)) return GamePlatform.Epic;
        return GamePlatform.Steam;
    }

    /// <summary>手動選択の Epic フォルダに対し、EGL マニフェストから AppName を逆引きする</summary>
    public static string? FindEpicAppNameFor(string gameDir)
    {
        try
        {
            foreach (var install in DetectEpic())
            {
                if (string.Equals(Path.GetFullPath(install.Path).TrimEnd('\\'),
                                  Path.GetFullPath(gameDir).TrimEnd('\\'),
                                  StringComparison.OrdinalIgnoreCase))
                {
                    return install.EpicAppName;
                }
            }
        }
        catch { }
        return null;
    }
}
