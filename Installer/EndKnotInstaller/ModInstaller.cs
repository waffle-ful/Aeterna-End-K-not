using System.Diagnostics;
using System.IO;
using System.IO.Compression;

namespace EndKnotInstaller;

public static class ModInstaller
{
    /// <summary>アンインストール時に消す zip 由来のルート直下アイテム (リリース zip のレイアウトと同期)</summary>
    private static readonly string[] RootArtifacts =
    [
        "BepInEx", "dotnet", "winhttp.dll", "winhttp.dll.disabled",
        "doorstop_config.ini", ".doorstop_version", "changelog.txt", "README.txt"
    ];

    public static bool IsGameRunning() => Process.GetProcessesByName("Among Us").Length > 0;

    public static async Task InstallAsync(GameInstall install, string zipUrl, IProgress<double> progress, Action<string> log, CancellationToken ct)
    {
        var tmpZip = Path.Combine(Path.GetTempPath(), "EndKnot_payload.zip");
        log("最新リリースをダウンロード中…");
        await ReleaseClient.DownloadAsync(zipUrl, tmpZip, progress, ct);
        log($"ダウンロード完了 ({new FileInfo(tmpZip).Length / 1024 / 1024} MB)");

        await Task.Run(() =>
        {
            // 旧バージョンの interop は BepInEx.cfg やゲーム更新とズレて事故のもと — 消して初回起動時に再生成させる
            var interop = Path.Combine(install.Path, "BepInEx", "interop");
            if (Directory.Exists(interop))
            {
                log("既存の BepInEx/interop を削除 (初回起動時に再生成されます — 1分ほどかかります)");
                Directory.Delete(interop, recursive: true);
            }

            log("ゲームフォルダへ展開中…");
            ZipFile.ExtractToDirectory(tmpZip, install.Path, overwriteFiles: true);

            // 無効化状態で更新した場合は有効状態へ揃える (zip が winhttp.dll を復元するため)
            if (File.Exists(install.WinhttpDisabledPath))
            {
                File.Delete(install.WinhttpDisabledPath);
                log("モッドを有効状態に切り替えました");
            }

            File.Delete(tmpZip);
        }, ct);

        log("インストール完了！");
    }

    /// <summary>モッド有効⇔無効の切替 (BepInEx doorstop の winhttp.dll をリネームするだけ — ファイルは残る)</summary>
    public static bool ToggleDoorstop(GameInstall install, Action<string> log)
    {
        if (File.Exists(install.WinhttpPath))
        {
            File.Move(install.WinhttpPath, install.WinhttpDisabledPath, overwrite: true);
            log("モッドを無効化しました。次回起動から完全バニラで動作します。");
            return false;
        }

        if (File.Exists(install.WinhttpDisabledPath))
        {
            File.Move(install.WinhttpDisabledPath, install.WinhttpPath, overwrite: true);
            log("モッドを有効化しました。");
            return true;
        }

        log("winhttp.dll が見つかりません — 先にインストールしてください。");
        return false;
    }

    /// <summary>モッド一式を削除する。EHR_DATA 等のユーザーデータとゲーム本体には触らない。</summary>
    public static void Uninstall(GameInstall install, Action<string> log)
    {
        foreach (var name in RootArtifacts)
        {
            var path = Path.Combine(install.Path, name);
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
                log($"削除: {name}\\");
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
                log($"削除: {name}");
            }
        }

        log("アンインストール完了。ゲーム本体は無傷です。");
    }

    /// <summary>
    /// ゲーム起動。Epic は auth token がランチャー経由でしか渡らないため必ずランチャー URL を使う
    /// (exe 直接起動は「Unable to get Epic Account ID」で死ぬ — NoS インストーラーの穴と同じ機序)。
    /// </summary>
    public static void Launch(GameInstall install, Action<string> log)
    {
        if (install.Platform == GamePlatform.Epic)
        {
            var appName = install.EpicAppName ?? GameDetector.FindEpicAppNameFor(install.Path);
            if (appName == null)
            {
                log("Epic の AppName を特定できませんでした。Epic Games Launcher から起動してください。");
                return;
            }

            Process.Start(new ProcessStartInfo($"com.epicgames.launcher://apps/{appName}?action=launch") { UseShellExecute = true });
            log("Epic Games Launcher 経由で起動しています…");
        }
        else
        {
            Process.Start(new ProcessStartInfo("steam://rungameid/945360") { UseShellExecute = true });
            log("Steam 経由で起動しています…");
        }
    }
}
