using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;

namespace EndKnot.Modules.Companion;

// AI実況相棒アプリ (tools/companion/companion.py) をゲーム内オプションから自動起動する橋渡し。
// 番犬 (WatchdogLauncher) と同じく本体スクリプトは DLL 埋め込み・起動時に実体化するので配布は DLL 1個で済む。
//
// 【番犬と逆の設計】番犬は AU が死んでも生き残る必要があるため schtasks でプロセスツリー外へ切り離したが、
// 相棒アプリは逆に「AU が終わったら一緒に黙ってほしい」ので普通の子プロセスとして起動する
// (Steam/Epic のジョブオブジェクトによる道連れ終了がむしろ後始末として機能する)。
// graceful 終了・オプション OFF では明示的にプロセスツリーごと kill する。
public static class CompanionLauncher
{
    private const string ScriptResource = "EndKnot.Resources.Companion.companion.py";
    private const string RequirementsResource = "EndKnot.Resources.Companion.requirements.txt";

    // Main.DataPath は Windows では "." (相対)。File API は CWD 解決で通るが、Process.Start (ShellExecute) は
    // 相対パスを解決できず「指定されたファイルが見つかりません」で失敗する (2026-07-14 実機ログで確認) — 必ず絶対化する。
    private static string BaseDir => Path.GetFullPath(Path.Combine(Main.DataPath, "EndKnot_DATA", "companion"));
    private static string EventsPath => Path.GetFullPath(Path.Combine(Main.DataPath, "EndKnot_DATA", "companion-events.jsonl"));
    private static string RunCmdPath => Path.Combine(BaseDir, "companion-run.cmd");

    // Python + pip + cmd 前提。Android ホストには存在しない。
    public static bool IsSupported => OperatingSystem.IsWindows();

    private static Process _proc;
    private static volatile bool _startInFlight;
    private static long _lastStartAttempt;

    // 相棒アプリが即死するケース (Python 未導入・キー無効等) で毎秒リトライのウィンドウ連打にならないよう、
    // 自動再起動は最短でもこの間隔を空ける。オプションの OFF→ON エッジは即時扱い。
    private const long RestartCooldownSeconds = 120;

    // APIキー未設定の案内はロビーチャットで1回だけ (毎秒 reconcile から呼ばれるためフラグ必須)。
    private static bool _noKeyNoticePending;
    private static bool _noKeyNoticeShown;

    private static bool IsProcAlive
    {
        get
        {
            try { return _proc != null && !_proc.HasExited; }
            catch { return false; }
        }
    }

    // ── オプション連動 ──
    // EnableAICommentary の ON/OFF を実プロセスに反映する。FixedUpdateCaller から 1 秒ごとに呼ばれる。
    private static bool _lastWant;
    private static bool _reconcileInit;

    public static void ReconcileWithOption()
    {
        if (!IsSupported) return;

        bool want;
        try { want = Main.EnableAICommentary?.Value ?? false; }
        catch { return; }

        if (!_reconcileInit)
        {
            _reconcileInit = true;
            _lastWant = want;
            if (want) Start(edge: true);
            TryShowNoKeyNotice();
            return;
        }

        if (want == _lastWant)
        {
            // ON のはずなのにプロセスが居ない (クラッシュ/手動で閉じた) → クールダウン付きで再武装。
            if (want && !IsProcAlive) Start(edge: false);
            TryShowNoKeyNotice();
            return;
        }

        _lastWant = want;
        if (want) Start(edge: true);
        else Stop();
    }

    private static void Start(bool edge)
    {
        if (_startInFlight || IsProcAlive) return;

        long now = Utils.TimeStamp;
        if (!edge && now - _lastStartAttempt < RestartCooldownSeconds) return;

        _lastStartAttempt = now;
        _startInFlight = true;
        var t = new Thread(StartWorker) { IsBackground = true, Name = "EndKnotCompanionStart" };
        t.Start();
    }

    private static void StartWorker()
    {
        try
        {
            string apiKey = FindApiKey();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                Logger.Warn("GEMINI_API_KEY is not set (checked process/user/machine env); companion app not launched", "CompanionLauncher");
                if (!_noKeyNoticeShown) _noKeyNoticePending = true;
                return;
            }

            if (!Materialize())
            {
                Logger.Warn("Companion script unavailable (not embedded in this build); start aborted", "CompanionLauncher");
                return;
            }

            // 子プロセスは親 (AU) の環境ブロックを継承する。キーをディスクに書かず、プロセス環境経由で渡す。
            // EVENTS_PATH も同経路にすることで cmd 生成時のパスクオート問題を避ける。
            Environment.SetEnvironmentVariable("GEMINI_API_KEY", apiKey);
            Environment.SetEnvironmentVariable("EK_COMPANION_EVENTS", EventsPath);
            Environment.SetEnvironmentVariable("EK_COMPANION_ARGS", Main.AICommentaryArgs?.Value ?? "");

            var psi = new ProcessStartInfo
            {
                FileName = RunCmdPath,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Minimized,
                WorkingDirectory = BaseDir
            };

            _proc = Process.Start(psi);
            Logger.Info("AI commentary companion app launched (minimized console)", "CompanionLauncher");
        }
        catch (Exception e) { Logger.Warn($"Companion launch failed: {e.Message}", "CompanionLauncher"); }
        finally { _startInFlight = false; }
    }

    // 相棒アプリをプロセスツリーごと止める (cmd → python の親子両方)。
    public static void Stop()
    {
        try
        {
            if (_proc == null) return;

            if (!_proc.HasExited)
            {
                _proc.Kill(true);
                Logger.Info("AI commentary companion app stopped", "CompanionLauncher");
            }

            _proc.Dispose();
            _proc = null;
        }
        catch (Exception e) { Logger.Warn($"Companion stop failed: {e.Message}", "CompanionLauncher"); }
    }

    // ゲームの正常終了時 (OnApplicationQuit) に呼ぶ。クラッシュ/強制終了時はジョブオブジェクトの
    // 道連れ終了に任せる (相棒は AU の子プロセスなので明示 kill が無くても残留しない)。
    public static void OnGameQuit()
    {
        Stop();
    }

    // GEMINI_API_KEY を Process → User → Machine の順で探す。User/Machine はレジストリを直接読むため、
    // 「キーを設定した後に Steam ごと再起動しないと反映されない」問題を回避できる。
    private static string FindApiKey()
    {
        try
        {
            string key = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
            if (!string.IsNullOrWhiteSpace(key)) return key;

            key = Environment.GetEnvironmentVariable("GEMINI_API_KEY", EnvironmentVariableTarget.User);
            if (!string.IsNullOrWhiteSpace(key)) return key;

            key = Environment.GetEnvironmentVariable("GEMINI_API_KEY", EnvironmentVariableTarget.Machine);
            if (!string.IsNullOrWhiteSpace(key)) return key;
        }
        catch (Exception e) { Logger.Warn($"FindApiKey failed: {e.Message}", "CompanionLauncher"); }

        return null;
    }

    // 埋め込みの companion.py / requirements.txt と起動用 cmd をディスクへ書き出す。
    // 毎回上書きするので DLL 更新で相棒アプリも最新になる。persona.txt 等のユーザーカスタムは触らない。
    private static bool Materialize()
    {
        Directory.CreateDirectory(BaseDir);

        if (!WriteResource(ScriptResource, Path.Combine(BaseDir, "companion.py"))) return false;
        if (!WriteResource(RequirementsResource, Path.Combine(BaseDir, "requirements.txt"))) return false;

        // 依存の自動インストール (初回のみ、deps-ok.flag で判定) → 本体起動。メッセージは cmd の
        // コードページ事故を避けるため ASCII のみ。ゲーム内向けの日本語案内は lang キー側で行う。
        const string cmd =
            "@echo off\r\n" +
            "title EndKnot AI Commentary\r\n" +
            "cd /d \"%~dp0\"\r\n" +
            // where python は Microsoft Store のエイリアス (実行すると Store が開くだけの偽物) にもヒットするため、
            // 実際に --version が通るかで判定する。
            "python --version >nul 2>nul\r\n" +
            "if errorlevel 1 (\r\n" +
            "  echo Python 3.10+ is required. Install it from https://www.python.org/ [check 'Add to PATH'], then re-enable the option.\r\n" +
            "  pause\r\n" +
            "  exit /b 1\r\n" +
            ")\r\n" +
            "if not exist deps-ok.flag (\r\n" +
            "  echo Installing dependencies [first run only]...\r\n" +
            "  python -m pip install -r requirements.txt\r\n" +
            "  if errorlevel 1 (\r\n" +
            "    echo Dependency install failed. See output above.\r\n" +
            "    pause\r\n" +
            "    exit /b 1\r\n" +
            "  )\r\n" +
            "  echo ok> deps-ok.flag\r\n" +
            ")\r\n" +
            "python companion.py --events \"%EK_COMPANION_EVENTS%\" %EK_COMPANION_ARGS%\r\n";

        File.WriteAllText(RunCmdPath, cmd);
        return true;
    }

    private static bool WriteResource(string resourceName, string path)
    {
        var asm = Assembly.GetExecutingAssembly();
        using Stream stream = asm.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            Logger.Warn($"Embedded companion resource not found: {resourceName}", "CompanionLauncher");
            return false;
        }

        using FileStream fs = File.Create(path);
        stream.CopyTo(fs);
        return true;
    }

    // APIキー未設定の案内。トグルはメニューでも切れるため、チャットが使えるロビー到達を待って1回だけ出す。
    private static void TryShowNoKeyNotice()
    {
        if (!_noKeyNoticePending || _noKeyNoticeShown) return;
        if (!GameStates.IsLobby || !AmongUsClient.Instance || !AmongUsClient.Instance.AmHost) return;
        if (!PlayerControl.LocalPlayer) return;

        _noKeyNoticePending = false;
        _noKeyNoticeShown = true;

        try { Utils.SendMessage(Translator.GetString("Companion.NoApiKeyNotice"), PlayerControl.LocalPlayer.PlayerId); }
        catch (Exception e) { Logger.Warn($"NoApiKeyNotice failed: {e.Message}", "CompanionLauncher"); }
    }
}
