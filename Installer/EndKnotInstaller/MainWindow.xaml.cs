using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace EndKnotInstaller;

public partial class MainWindow : Window
{
    private readonly List<GameInstall> installs = [];
    private ReleaseInfo? latestRelease;
    private bool busy;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await InitAsync();
    }

    private GameInstall? Selected =>
        InstallCombo.SelectedIndex >= 0 && InstallCombo.SelectedIndex < installs.Count
            ? installs[InstallCombo.SelectedIndex]
            : null;

    private void Log(string message)
    {
        // 展開処理は Task.Run 内から log してくるので UI スレッドへマーシャリング必須
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => Log(message));
            return;
        }

        LogBox.AppendText(message + Environment.NewLine);
        LogBox.ScrollToEnd();
    }

    private async Task InitAsync()
    {
        Rescan();
        try
        {
            latestRelease = await ReleaseClient.GetLatestReleaseAsync();
            Log($"最新リリース: {latestRelease.Tag}");
        }
        catch (Exception ex)
        {
            Log($"リリース情報の取得に失敗しました: {ex.Message}");
            Log($"ネットワークを確認するか、{ReleaseClient.ReleasesPageUrl} から手動でダウンロードしてください。");
        }
        RefreshStatus();
    }

    private void Rescan()
    {
        installs.Clear();
        installs.AddRange(GameDetector.Detect());
        InstallCombo.ItemsSource = installs.Select(i => i.ToString()).ToList();
        if (installs.Count > 0) InstallCombo.SelectedIndex = 0;
        Log(installs.Count > 0
            ? $"Among Us を {installs.Count} 件検出しました"
            : "Among Us を自動検出できませんでした。「手動選択…」でゲームフォルダを指定してください。");
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        var install = Selected;
        if (install == null)
        {
            StatusText.Text = "Among Us が選択されていません";
            InstallButton.IsEnabled = ToggleButton.IsEnabled = UninstallButton.IsEnabled = LaunchButton.IsEnabled = false;
            return;
        }

        string state;
        if (!install.IsModded)
        {
            state = "バニラ (End K not 未導入)";
        }
        else
        {
            var version = install.ModVersion ?? "?";
            state = install.IsDoorstopEnabled
                ? $"End K not v{version} 導入済み (有効)"
                : $"End K not v{version} 導入済み (無効化中 — バニラとして起動します)";
        }

        var latest = latestRelease != null ? $" / 最新: {latestRelease.Tag}" : "";
        StatusText.Text = state + latest;

        InstallButton.IsEnabled = !busy && latestRelease != null;
        ToggleButton.IsEnabled = !busy && install.IsModded;
        UninstallButton.IsEnabled = !busy && install.IsModded;
        LaunchButton.IsEnabled = !busy;
    }

    private void InstallCombo_SelectionChanged(object sender, RoutedEventArgs e) => RefreshStatus();

    private void ClickRescan(object sender, RoutedEventArgs e) => Rescan();

    private void ClickBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Among Us のインストールフォルダを選択してください" };
        if (dialog.ShowDialog() != true) return;

        var dir = dialog.FolderName;
        if (!File.Exists(Path.Combine(dir, "Among Us.exe")))
        {
            MessageBox.Show("選択したフォルダに Among Us.exe がありません。", "End K not インストーラー",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var platform = GameDetector.GuessPlatform(dir);
        var appName = platform == GamePlatform.Epic ? GameDetector.FindEpicAppNameFor(dir) : null;
        installs.Add(new GameInstall(dir, platform, appName));
        InstallCombo.ItemsSource = installs.Select(i => i.ToString()).ToList();
        InstallCombo.SelectedIndex = installs.Count - 1;
        RefreshStatus();
    }

    private bool EnsureGameClosed()
    {
        if (!ModInstaller.IsGameRunning()) return true;
        MessageBox.Show("Among Us が起動中です。ゲームを終了してからもう一度お試しください。",
            "End K not インストーラー", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    /// <summary>タグ ("v0.9.0-alpha") から比較用の数値バージョンを取り出す</summary>
    private static Version? ParseTagVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var num = tag.TrimStart('v', 'V').Split('-')[0];
        return Version.TryParse(num, out var parsed) ? Normalize(parsed) : null;
    }

    // FileVersion は "0.9.0.0" の4要素、タグは "0.9.0" の3要素で来るため、揃えないと同一版が「新しい」と誤判定される
    private static Version Normalize(Version v) => new(v.Major, v.Minor, v.Build < 0 ? 0 : v.Build);

    /// <summary>
    /// 既存のモッドが「これから入れる公開版より新しい / 開発ビルド」か。
    /// true なら上書きで開発中の変更が失われるので、確認とバックアップが要る (2026-07-31 に実際に発生)。
    /// </summary>
    private bool IsOverwritingNewer(GameInstall install)
    {
        if (!install.IsModded) return false;
        if (install.IsDevBuild) return true;

        var current = install.ModNumericVersion;
        var latest = ParseTagVersion(latestRelease?.Tag);
        return current != null && latest != null && Normalize(current) > latest;
    }

    private bool ConfirmOverwrite(GameInstall install)
    {
        var reason = install.IsDevBuild
            ? $"開発ビルド ({install.ModVersion}) が入っています"
            : $"導入済みのバージョン ({install.ModVersion}) が最新リリース ({latestRelease?.Tag}) より新しいです";

        var answer = MessageBox.Show(
            $"{reason}。\n\nこのまま進めると公開版で上書きされ、開発中の変更が失われます。\n" +
            "(上書きする前に BepInEx\\plugins\\EndKnot.dll.bak へバックアップします)\n\n続行しますか？",
            "End K not インストーラー", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        return answer == MessageBoxResult.Yes;
    }

    private async void ClickInstall(object sender, RoutedEventArgs e)
    {
        var install = Selected;
        if (install == null || latestRelease == null || busy || !EnsureGameClosed()) return;

        if (!latestRelease.AssetUrls.TryGetValue(install.Platform, out var zipUrl))
        {
            Log($"最新リリースに {install.Platform} 用 zip がありません。");
            return;
        }

        // 上書きで失われるものがあるなら、ここで同意を取ってバックアップを有効にする
        var needsBackup = IsOverwritingNewer(install);
        if (needsBackup && !ConfirmOverwrite(install))
        {
            Log("インストールを中止しました (既存のモッドはそのままです)。");
            return;
        }

        busy = true;
        RefreshStatus();
        try
        {
            var progress = new Progress<double>(v => Progress.Value = v);
            await ModInstaller.InstallAsync(install, zipUrl, progress, Log, CancellationToken.None, needsBackup);
            Log("「ゲーム起動」からそのまま遊べます (初回起動は1分ほどかかります)。");
        }
        catch (Exception ex)
        {
            Log($"インストール失敗: {ex.Message}");
        }
        finally
        {
            busy = false;
            Progress.Value = 0;
            RefreshStatus();
        }
    }

    private void ClickToggle(object sender, RoutedEventArgs e)
    {
        var install = Selected;
        if (install == null || busy || !EnsureGameClosed()) return;

        try { ModInstaller.ToggleDoorstop(install, Log); }
        catch (Exception ex) { Log($"切替失敗: {ex.Message}"); }
        RefreshStatus();
    }

    private void ClickUninstall(object sender, RoutedEventArgs e)
    {
        var install = Selected;
        if (install == null || busy || !EnsureGameClosed()) return;

        var answer = MessageBox.Show(
            "End K not (BepInEx 一式) を削除します。ゲーム本体や Steam/Epic のデータには影響しません。よろしいですか？",
            "End K not インストーラー", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        try { ModInstaller.Uninstall(install, Log); }
        catch (Exception ex) { Log($"アンインストール失敗: {ex.Message}"); }
        RefreshStatus();
    }

    private void ClickLaunch(object sender, RoutedEventArgs e)
    {
        var install = Selected;
        if (install == null || busy) return;

        try { ModInstaller.Launch(install, Log); }
        catch (Exception ex) { Log($"起動失敗: {ex.Message}"); }
    }
}
