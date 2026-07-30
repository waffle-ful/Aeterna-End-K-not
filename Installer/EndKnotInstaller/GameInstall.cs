namespace EndKnotInstaller;

public enum GamePlatform
{
    Steam,
    Epic
}

public class GameInstall(string path, GamePlatform platform, string? epicAppName)
{
    // EGL manifest は "C:\Program Files\Epic Games/AmongUs" のような区切り混在パスを返すので正規化して保持
    public string Path { get; } = System.IO.Path.GetFullPath(path);
    public GamePlatform Platform { get; } = platform;

    /// <summary>Epic ランチャー URL 起動に使う AppName (EGL マニフェスト由来。手動選択時は null のことがある)</summary>
    public string? EpicAppName { get; } = epicAppName;

    public string ExePath => System.IO.Path.Combine(Path, "Among Us.exe");
    public string PluginDllPath => System.IO.Path.Combine(Path, "BepInEx", "plugins", "EndKnot.dll");
    public string WinhttpPath => System.IO.Path.Combine(Path, "winhttp.dll");
    public string WinhttpDisabledPath => System.IO.Path.Combine(Path, "winhttp.dll.disabled");

    public bool IsModded => System.IO.File.Exists(PluginDllPath);
    public bool IsDoorstopEnabled => System.IO.File.Exists(WinhttpPath);

    public string? ModVersion
    {
        get
        {
            if (!IsModded) return null;
            try { return System.Diagnostics.FileVersionInfo.GetVersionInfo(PluginDllPath).FileVersion; }
            catch { return null; }
        }
    }

    public override string ToString()
    {
        var platform = Platform == GamePlatform.Epic ? "Epic" : "Steam";
        return $"[{platform}] {Path}";
    }
}
