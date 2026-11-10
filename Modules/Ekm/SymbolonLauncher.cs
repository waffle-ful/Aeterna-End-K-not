using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Win32;
using UnityEngine;

namespace EndKnot.Modules.Ekm;

// 役職メーカー (エディタ Symbolon) をボタン 1 つで開く入口。
// PC は配布物に同梱した Symbolon.exe を役職メーカー画面で起動し、使えない時 (Android・exe が無い・WebView2 が無い) は
// ブラウザ版を開く。どちらもホストの手元で開くだけで、ネットワーク送信は無い。
public static class SymbolonLauncher
{
    public const string WebUrl = "https://waffle-ful.github.io/Aeterna-End-K-not/editor/#role-maker";
    private const string ProcessName = "Symbolon";
    private const string ShortcutName = "Symbolon.lnk";

    // Evergreen WebView2 Runtime の製品 GUID (Microsoft の配布手引きにある検出用キー)。
    private const string WebView2ClientKey = @"Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

    // Main.DataPath は Windows では "." (相対)。ShellExecute は相対パスを解決できないので必ず絶対化する。
    private static string ExeDir => Path.GetFullPath(Path.Combine(Main.DataPath, "EndKnot_DATA", "Symbolon"));
    private static string ExePath => Path.Combine(ExeDir, "Symbolon.exe");
    private static string ShortcutMarkPath => Path.Combine(ExeDir, "shortcut.txt");

    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);

    /// <summary>true = 同梱アプリを開いた / false = ブラウザ版を開いた。</summary>
    public static bool OpenRoleMaker()
    {
        if (OperatingSystem.IsWindows() && File.Exists(ExePath) && HasWebView2())
        {
            try
            {
                Process[] running = Process.GetProcessesByName(ProcessName);
                if (running.Length > 0)
                {
                    // 既に開いている窓がゲームの裏に隠れていると「押しても開かない」に見えるので手前へ出す。
                    IntPtr h = running[0].MainWindowHandle;
                    if (h != IntPtr.Zero)
                    {
                        if (IsIconic(h)) ShowWindow(h, 9);
                        SetForegroundWindow(h);
                    }
                }
                else
                {
                    Process.Start(new ProcessStartInfo(ExePath, "--role-maker")
                    {
                        UseShellExecute = true,
                        WorkingDirectory = ExeDir
                    });
                }

                CreateStartMenuShortcutOnce();
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn($"Symbolon launch failed, falling back to the web version: {ex.Message}", "SymbolonLauncher");
            }
        }

        try { Application.OpenURL(WebUrl); }
        catch (Exception ex) { Logger.Warn($"Opening the web version failed: {ex.Message}", "SymbolonLauncher"); }
        return false;
    }

    // Symbolon.exe は WebView2 で画面を描く。Windows 11 と更新済みの Windows 10 には入っているが、
    // 無い PC で起動すると何も開かないので、その時はブラウザ版に回す。
    private static bool HasWebView2()
    {
        try
        {
            foreach (RegistryKey root in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                foreach (string prefix in new[] { @"SOFTWARE\WOW6432Node\", @"SOFTWARE\" })
                {
                    using RegistryKey key = root.OpenSubKey(prefix + WebView2ClientKey);
                    if (key?.GetValue("pv") is string pv && pv.Length > 0 && pv != "0.0.0.0") return true;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"WebView2 check failed: {ex.Message}", "SymbolonLauncher");
            return true;
        }

        return false;
    }

    // ゲームを起動しなくてもスタートメニューから開けるよう、初めて開いた時に 1 回だけショートカットを置く。
    // 置いた印が残っていれば、利用者がショートカットを消していても作り直さない。
    // Among Us の場所が変わった時 (印の中のパスが違う) だけ置き直す。
    private static void CreateStartMenuShortcutOnce()
    {
        string exe = ExePath;
        string dir = ExeDir;
        string mark = ShortcutMarkPath;
        try
        {
            if (File.Exists(mark) && File.ReadAllText(mark).Trim() == exe) return;
        }
        catch { return; }

        Task.Run(() =>
        {
            try
            {
                string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                if (string.IsNullOrEmpty(programs)) return;
                string lnk = Path.Combine(programs, ShortcutName);

                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return;
                object shell = Activator.CreateInstance(shellType);
                object sc = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [lnk]);
                Type scType = sc.GetType();
                scType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, [exe]);
                scType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, [dir]);
                scType.InvokeMember("Description", BindingFlags.SetProperty, null, sc, ["Symbolon"]);
                scType.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
                Marshal.FinalReleaseComObject(sc);
                Marshal.FinalReleaseComObject(shell);

                if (File.Exists(lnk)) File.WriteAllText(mark, exe);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Symbolon start-menu shortcut failed: {ex.Message}", "SymbolonLauncher");
            }
        });
    }
}
