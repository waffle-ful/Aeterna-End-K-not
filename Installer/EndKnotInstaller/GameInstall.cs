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

    /// <summary>表示用のバージョン文字列。ProductVersion = AssemblyInformationalVersion なので "0.9.0-dev" のようにサフィックス付きで取れる</summary>
    public string? ModVersion
    {
        get
        {
            if (!IsModded) return null;
            try
            {
                var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(PluginDllPath);
                return string.IsNullOrWhiteSpace(info.ProductVersion) ? info.FileVersion : info.ProductVersion;
            }
            catch { return null; }
        }
    }

    /// <summary>比較用の数値バージョン (FileVersion 由来)。開発ビルドは次の公開版の番号を名乗るので公開版より大きくなる</summary>
    public Version? ModNumericVersion
    {
        get
        {
            if (!IsModded) return null;
            try
            {
                var raw = System.Diagnostics.FileVersionInfo.GetVersionInfo(PluginDllPath).FileVersion;
                return Version.TryParse(raw, out var parsed) ? parsed : null;
            }
            catch { return null; }
        }
    }

    /// <summary>開発ビルド (release.ps1 が公開時に -alpha へ確定するまでの間の版)</summary>
    public bool IsDevBuild => ModVersion?.EndsWith("-dev", StringComparison.OrdinalIgnoreCase) == true;

    public override string ToString()
    {
        var platform = Platform == GamePlatform.Epic ? "Epic" : "Steam";
        return $"[{platform}] {Path}";
    }
}
