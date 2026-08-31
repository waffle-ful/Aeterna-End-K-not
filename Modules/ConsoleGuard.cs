using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace EndKnot.Modules;

// BepInEx コンソール (legacy conhost) の選択モード対策。
// conhost はテキストが選択されている間、コンソールへの WriteFile を無期限ブロックする。
// BepInEx.Logging.ConsoleLogListener は同期書き込みのため、選択中に次のログを1行吐いた瞬間に
// メインスレッドごと停止し、心拍途絶→番犬 kill の「ハング」として観測される
// (2026-08-04 16:41 のハングダンプで WriteFile 停止スタックを直接確認 — BUG-20260721-02)。
//
// 対策は2段構え:
//   1) DisableQuickEdit() — 左クリックだけで選択モードに入る QuickEdit を起動時に無効化する。
//      偶発的な事故クリックはこれで塞がる。
//   2) StartSelectionWatcher() — 常駐スレッドで以下2つを継続的に行う。
//      (a) QuickEdit の再無効化。ConsoleManager.CreateConsole() はゲーム開始時に呼ばれうるため
//          (Patches/ShipStatusPatch.cs:295 の AllowConsole 経路 / Main.cs:1203)、起動時に一度
//          落としただけでは新しいコンソールが既定の QuickEdit 有効で復活してしまう。
//      (b) 選択状態の監視と強制解除。QuickEdit を切っても Mark モード (右クリックメニュー /
//          Ctrl+M) や明示的な範囲選択は残るため、選択が一定時間続いたら ESC を送って解除する。
//      2026-08-07 02:07 のハングは (1) が効いている状態で発生しており (コンソール窓の操作が
//      引き金)、起動時1回では塞ぎ切れないことが実証された。
//      (b) は「ハングを防ぐ」のではなく「ハングしても GraceSeconds で自動復帰する」機構である。
public static class ConsoleGuard
{
    private const int StdInputHandle = -10;
    private const uint EnableQuickEditMode = 0x0040;
    private const uint EnableExtendedFlags = 0x0080;

    // CONSOLE_SELECTION_INFO.dwFlags
    private const uint ConsoleSelectionInProgress = 0x0001;
    private const uint ConsoleSelectionNotEmpty = 0x0002;

    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const int VkEscape = 0x1B;

    // 選択がこの秒数続いたら強制解除する。番犬の心拍途絶閾値 (90s) より十分短く、かつ
    // 人間が範囲選択→Enter でコピーする操作を邪魔しない程度の猶予として 10 秒。
    private const int GraceSeconds = 10;
    private const int PollIntervalMs = 1000;

    private static Thread _watcher;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleSelectionInfo(out ConsoleSelectionInfo lpConsoleSelectionInfo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SmallRect
    {
        public short Left;
        public short Top;
        public short Right;
        public short Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ConsoleSelectionInfo
    {
        public uint Flags;
        public Coord SelectionAnchor;
        public SmallRect Selection;
    }

    /// <summary>QuickEdit を無効化し、結果を人間可読の1行で返す (呼び出し元がログする)。</summary>
    public static string DisableQuickEdit()
    {
        if (!OperatingSystem.IsWindows()) return "skipped (not Windows)";

        try
        {
            IntPtr handle = GetStdHandle(StdInputHandle);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return "no console stdin handle";

            if (!GetConsoleMode(handle, out uint mode)) return $"GetConsoleMode failed (err={Marshal.GetLastWin32Error()}, console likely disabled)";

            if ((mode & EnableQuickEditMode) == 0) return $"QuickEdit already off (mode=0x{mode:X})";

            // ENABLE_EXTENDED_FLAGS を同時に立てないと QUICK_EDIT の変更が conhost に反映されない
            uint newMode = (mode | EnableExtendedFlags) & ~EnableQuickEditMode;
            if (!SetConsoleMode(handle, newMode)) return $"SetConsoleMode failed (err={Marshal.GetLastWin32Error()})";

            return $"QuickEdit disabled (mode 0x{mode:X} -> 0x{newMode:X})";
        }
        catch (Exception e) { return $"error: {e.Message}"; }
    }

    /// <summary>
    /// 選択モードを監視する常駐スレッドを開始し、結果を人間可読の1行で返す (呼び出し元がログする)。
    /// </summary>
    public static string StartSelectionWatcher()
    {
        if (!OperatingSystem.IsWindows()) return "watcher skipped (not Windows)";
        if (_watcher != null) return "watcher already running";

        try
        {
            // コンソールが今存在しなくても開始する — ConsoleManager.CreateConsole() で後から
            // 生えてくる経路があるため、ここで打ち切るとその新しいコンソールが無防備になる。
            _watcher = new Thread(WatchLoop) { IsBackground = true, Name = "EndKnot.ConsoleGuard" };
            _watcher.Start();
            return $"selection watcher started (grace={GraceSeconds}s)";
        }
        catch (Exception e) { return $"watcher error: {e.Message}"; }
    }

    // 背景スレッドからの記録専用。HealthLog がまだ初期化されていなければ**書かずに諦める**。
    // 理由: HealthLog.EnsureInit() は check-then-act で .prev への File.Move まで行うため、
    // 背景スレッドがその最初の呼び出し者になるとメインスレッドと競合して IOException を生み、
    // その catch が Utils.ThrowException → EndKnot.Logger (ロック無しの共有 StringBuilder /
    // Dictionary) へ落ちる。Dictionary の並行破壊は例外ではなく無限ループになるので、
    // ハングを直すはずのコードが別種のハングを作ることになる。起動直後の1行を捨てる方が安い。
    private static void SafeNote(string line)
    {
        if (!HealthLog.IsInitialized) return;

        try { HealthLog.Note(line); }
        catch
        {
            // 計器の失敗でウォッチャーを止めない。
        }
    }

    // QuickEdit が (作り直されたコンソールなどで) 復活していたら落とし直す。
    // 落とす必要が生じたときだけ記録するので、平常時はログを汚さない。
    private static void KeepQuickEditOff()
    {
        IntPtr handle = GetStdHandle(StdInputHandle);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return;
        if (!GetConsoleMode(handle, out uint mode)) return;
        if ((mode & EnableQuickEditMode) == 0) return;

        uint newMode = (mode | EnableExtendedFlags) & ~EnableQuickEditMode;
        if (SetConsoleMode(handle, newMode))
            SafeNote($"CONSOLEGUARD re-disabled QuickEdit on a recreated console (mode 0x{mode:X} -> 0x{newMode:X})");
    }

    // ⚠️ このループは絶対に BepInEx の Logger を呼んではならない。
    // 詰まっているのがまさに ConsoleLogListener の同期書き込みなので、ここでログを吐くと
    // 解除役のスレッド自身がブロックされ、機構ごと無力化する。
    // 記録は HealthLog.Note (File.AppendAllText・コンソール非経由) のみを使う。
    private static void WatchLoop()
    {
        int heldSeconds = 0;
        bool warnedStuck = false;

        while (true)
        {
            try
            {
                Thread.Sleep(PollIntervalMs);

                // (a) コンソールは ConsoleManager.CreateConsole() で作り直されうる。作り直された
                // コンソールは既定で QuickEdit 有効なので、毎周期見張って落とし続ける。
                KeepQuickEditOff();

                // (b) 選択状態の監視
                if (!GetConsoleSelectionInfo(out ConsoleSelectionInfo info))
                {
                    heldSeconds = 0;
                    continue;
                }

                bool selecting = (info.Flags & (ConsoleSelectionInProgress | ConsoleSelectionNotEmpty)) != 0;
                if (!selecting)
                {
                    heldSeconds = 0;
                    warnedStuck = false;
                    continue;
                }

                heldSeconds++;
                if (heldSeconds < GraceSeconds) continue;

                IntPtr hwnd = GetConsoleWindow();
                if (hwnd == IntPtr.Zero) continue;

                // ESC で選択モードを抜ける (QuickEdit のマウス選択・Mark モードの双方に効く)。
                PostMessage(hwnd, WmKeyDown, new IntPtr(VkEscape), IntPtr.Zero);
                PostMessage(hwnd, WmKeyUp, new IntPtr(VkEscape), IntPtr.Zero);

                SafeNote($"CONSOLEGUARD released console selection after {heldSeconds}s flags=0x{info.Flags:X}");
                heldSeconds = 0;

                // ESC を送っても解除されない場合 (別種のブロック) は1回だけ記録して以後は黙る。
                Thread.Sleep(PollIntervalMs);
                if (!warnedStuck && GetConsoleSelectionInfo(out ConsoleSelectionInfo after) && (after.Flags & (ConsoleSelectionInProgress | ConsoleSelectionNotEmpty)) != 0)
                {
                    warnedStuck = true;
                    SafeNote($"CONSOLEGUARD WARN escape did not clear selection flags=0x{after.Flags:X}");
                }
            }
            catch
            {
                // 監視スレッドは何があっても落とさない (落ちるとハング自動復帰の唯一の口が消える)。
            }
        }
    }
}
