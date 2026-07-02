using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security;
using System.Threading;

namespace EndKnot.Modules;

// 外部ウォッチドッグ(番犬)をゲーム内オプション/ボタンから起動・停止する橋渡し。
// 番犬本体スクリプトは DLL に埋め込まれており、起動時にデスクトップの EndKnot_Logs/ へ書き出す。
// これによりユーザーへの配布ファイルは DLL だけで済む(.ps1/.cmd を別途配る必要がない)。
//
// 【なぜ schtasks 経由か】
// Among Us は Steam/Epic 等のジョブオブジェクト(kill-on-close)配下で動くことがあり、単純な
// Process.Start の子プロセスは AU を強制終了すると道連れで死ぬ(実機確認済)。そこでタスク
// スケジューラのサービスに番犬を産ませ、AU のプロセスツリー外へ切り離す。こうすると AU が
// タスクキルされても番犬は生き残り、AU を立て直せる。番犬は AU の外の別プロセスなので
// アンチチートには一切触れない。
public static class WatchdogLauncher
{
    // Local\ で十分(AU と番犬は同一の対話セッションを共有する。Global\ は権限まわりの余計な穴を招くだけ)。
    private const string MutexName = "Local\\EndKnotWatchdog";
    private const string ResourceName = "EndKnot.Resources.EndKnotWatchdog.ps1";
    private const string TaskName = "EndKnotWatchdog";

    private static string BaseDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "EndKnot_Logs");

    private static string ScriptPath => Path.Combine(BaseDir, "EndKnotWatchdog.ps1");
    private static string TaskXmlPath => Path.Combine(BaseDir, "watchdog-task.xml");
    private static string StopFlagPath => Path.Combine(BaseDir, "watchdog-stop.flag");

    // 番犬は Windows の PowerShell + タスクスケジューラ前提。Android ホストには存在しない。
    public static bool IsSupported => OperatingSystem.IsWindows();

    // 起動処理中フラグ。1秒ごとの reconcile が Mutex 取得前に二重起動を仕掛けないためのガード。
    private static volatile bool _startInFlight;

    // 番犬が現在動いているか。名前付き Mutex で見るので、AU がクラッシュ→再起動して
    // プロセスハンドルを失った後の新セッションからでも「既に番犬が動いている」を正しく検出できる。
    public static bool IsRunning
    {
        get
        {
            try
            {
                if (Mutex.TryOpenExisting(MutexName, out var m))
                {
                    m.Dispose();
                    return true;
                }
            }
            catch { }

            return false;
        }
    }

    // ── オプション連動 ──
    // CrashWatchdog オプションの ON/OFF を実際の番犬に反映する。FixedUpdateCaller から 1 秒ごとに呼ばれる。
    // 「レベル」ではなく「エッジ(切り替わった瞬間)」で停止指示を出す。毎秒 stop-flag を書き続けると、
    // 手動 (.cmd) で番犬を立てているパワーユーザーの番犬まで殺してしまうため。
    private static bool _lastWant;
    private static bool _reconcileInit;

    public static void ReconcileWithOption()
    {
        if (!IsSupported) return;

        bool want;
        try { want = Options.CrashWatchdog?.GetBool() ?? false; }
        catch { return; }

        if (!_reconcileInit)
        {
            _reconcileInit = true;
            _lastWant = want;
            if (want && !IsRunning) Start();
            return;
        }

        if (want == _lastWant)
        {
            // 同状態の維持: ON のはずなのに番犬が居なければ(手動で閉じた/取りこぼし)再武装する。
            if (want && !IsRunning) Start();
            return;
        }

        _lastWant = want;
        if (want) Start();
        else Stop();
    }

    // 番犬を起動する。既に動いていれば何もしない。schtasks 呼び出しでゲームスレッドを固めないよう
    // 実処理はバックグラウンドスレッドで回す。
    public static void Start()
    {
        if (!IsSupported) return;
        if (_startInFlight || IsRunning) return;

        _startInFlight = true;
        var t = new Thread(StartWorker) { IsBackground = true, Name = "EndKnotWatchdogStart" };
        t.Start();
    }

    private static void StartWorker()
    {
        try
        {
            Directory.CreateDirectory(BaseDir);

            // 前回の停止指示が残っていると、起動直後の番犬が最初の巡回で自滅してしまうので消しておく。
            try { if (File.Exists(StopFlagPath)) File.Delete(StopFlagPath); }
            catch { }

            // スクリプトが用意できなければ(埋め込み不在=公開CIビルド等)、schtasks まで進まず中断する。
            if (!MaterializeScript() && !File.Exists(ScriptPath))
            {
                Logger.Warn("Watchdog script unavailable (not embedded in this build); start aborted", "WatchdogLauncher");
                return;
            }

            WriteTaskXml();

            // タスクスケジューラのサービスに番犬を産ませる(AU のジョブ外へ切り離す)。
            // 定義を作成 → 即実行。トリガー無し定義なので勝手に再発火しない。既に走っている場合は
            // Mutex ガードで二重起動しない。作成に失敗したら最後の砦として直接起動にフォールバック
            // (この場合 AU タスクキルには耐えられないが、何も起きないよりはマシ)。
            bool created = RunSchtasks($"/Create /F /TN {TaskName} /XML \"{TaskXmlPath}\"");
            if (created)
            {
                RunSchtasks($"/Run /TN {TaskName}");
                Logger.Info("Watchdog launched via Task Scheduler (detached)", "WatchdogLauncher");
            }
            else
            {
                Logger.Warn("schtasks create failed; falling back to direct launch (won't survive AU force-kill)", "WatchdogLauncher");
                LaunchDirect();
            }
        }
        catch (Exception e) { Utils.ThrowException(e); }
        finally { _startInFlight = false; }
    }

    // フォールバック: タスクスケジューラが使えない環境向けの直接起動(最小化可視ウィンドウ)。
    private static void LaunchDirect()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Minimized -File \"{ScriptPath}\"",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Minimized,
                WorkingDirectory = BaseDir
            };
            Process.Start(psi);
        }
        catch (Exception e) { Utils.ThrowException(e); }
    }

    // 番犬に停止を指示する。停止フラグを置くだけ。番犬は次の巡回(最大約20秒)でそれを読んで自滅する。
    // 併せて残ったタスク定義も掃除する(実行中インスタンスは削除しても生き残ることを実証済)。
    public static void Stop()
    {
        try
        {
            Directory.CreateDirectory(BaseDir);
            File.WriteAllText(StopFlagPath, DateTime.Now.ToString("o"));
            Logger.Info("Watchdog stop requested (flag written)", "WatchdogLauncher");

            if (IsSupported)
            {
                var t = new Thread(() => RunSchtasks($"/Delete /F /TN {TaskName}")) { IsBackground = true };
                t.Start();
            }
        }
        catch (Exception e) { Utils.ThrowException(e); }
    }

    // ゲームの正常終了時(OnApplicationQuit)に呼ぶ。ユーザーが自分で×を押して終わったのに
    // 番犬が「AU が落ちた」と誤認して蘇生させる無限ループを防ぐ。
    // クラッシュ/強制終了では OnApplicationQuit が呼ばれないので、この経路は「意図的な終了」だけを拾う。
    public static void OnGameQuit()
    {
        if (IsRunning) Stop();
    }

    // 埋め込みの番犬スクリプトをディスクへ書き出す。起動のたびに上書きするので、DLL 更新で番犬も最新になる。
    // 埋め込みが無い(公開CIビルド等)場合は false を返す。
    private static bool MaterializeScript()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(ResourceName);
        if (stream == null)
        {
            Logger.Warn($"Embedded watchdog script not found: {ResourceName}", "WatchdogLauncher");
            return false;
        }

        using var fs = File.Create(ScriptPath);
        stream.CopyTo(fs);
        return true;
    }

    // タスクスケジューラ用の XML を書き出す。Command と Arguments を別要素で持つので、パスに
    // スペースや日本語が含まれてもシェルのクオートに悩まされない(schtasks /TR の直書きはここで詰む)。
    private static void WriteTaskXml()
    {
        string escaped = SecurityElement.Escape(ScriptPath);
        string xml =
            "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
            "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
            "  <RegistrationInfo><Description>End K not Watchdog</Description></RegistrationInfo>\r\n" +
            "  <Principals><Principal id=\"Author\"><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>\r\n" +
            // Settings の子要素は XSD で順序が厳格。バッテリー系は AllowHardTerminate/ExecutionTimeLimit
            // より前に置くこと(順序違反だと厳格な Windows で schtasks が拒否 → フォールバック道連れ死になる)。
            "  <Settings>\r\n" +
            "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n" +
            "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n" +
            "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n" +
            "    <AllowHardTerminate>false</AllowHardTerminate>\r\n" +
            "    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\r\n" +
            "    <Enabled>true</Enabled><Hidden>false</Hidden>\r\n" +
            "  </Settings>\r\n" +
            "  <Actions Context=\"Author\"><Exec><Command>powershell.exe</Command>" +
            $"<Arguments>-NoProfile -ExecutionPolicy Bypass -WindowStyle Minimized -File \"{escaped}\"</Arguments></Exec></Actions>\r\n" +
            "</Task>";

        File.WriteAllText(TaskXmlPath, xml, System.Text.Encoding.Unicode);
    }

    // schtasks.exe を隠しウィンドウで実行し、終了コードで成否を返す。schtasks 自身は AU の子だが、
    // タスクを登録・実行してすぐ終わるだけで、番犬本体はサービスが産むので切り離しは保たれる。
    private static bool RunSchtasks(string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            if (!p.WaitForExit(8000))
            {
                try { p.Kill(); }
                catch { }

                return false;
            }

            return p.ExitCode == 0;
        }
        catch (Exception e)
        {
            Logger.Warn($"schtasks '{args}' failed: {e.Message}", "WatchdogLauncher");
            return false;
        }
    }
}
