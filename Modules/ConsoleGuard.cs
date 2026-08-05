using System;
using System.Runtime.InteropServices;

namespace EndKnot.Modules;

// BepInEx コンソール (legacy conhost) の QuickEdit モード対策。
// QuickEdit が有効だと、コンソールウィンドウ内を左クリックしただけで選択モードに入り、
// 選択が解除されるまでコンソールへの WriteFile が無期限ブロックする。
// BepInEx.Logging.ConsoleLogListener は同期書き込みのため、次にログを1行吐いた瞬間に
// メインスレッドごと停止し、心拍途絶→番犬 kill の「ハング」として観測される
// (2026-08-04 16:41 のハングダンプで WriteFile 停止スタックを直接確認 — BUG-20260721-02)。
public static class ConsoleGuard
{
    private const int StdInputHandle = -10;
    private const uint EnableQuickEditMode = 0x0040;
    private const uint EnableExtendedFlags = 0x0080;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

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
}
