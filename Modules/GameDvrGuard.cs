using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace EndKnot.Modules;

// Windows の GameDVR ユーザーサービス (BcastDVRUserService_<接尾辞>) が STOP_PENDING のまま固まると、
// Game Bar のキャプチャ (Windows.Media.Capture.Internal.AppCaptureShell) は DCOM 起動で「サービスの
// 停止待ち」のタイムアウトを繰り返し、録画ボタンが灰色のまま二度と戻らない (2026-09-27 実機で
// 09-25 18:49 から丸 2 日詰まったまま、182 回のタイムアウトを確認)。ゲームを再起動しても直らない。
// 唯一の復旧は詰まったホスト svchost を終了させること — サービスは DCOM が需要時に自動で作り直す
// (終了から 2 秒で Running に戻るのを実測)。
//
// この番人は同じ手当てを自動化する。cfg で opt-in した時だけ動く (ゲームプロセスから svchost を
// 終了させる振る舞いは既定で有効にすべきでない)。判定は時間だけでなく SCM の dwCheckPoint も見る —
// 正常な遅い停止は CheckPoint が進むので巻き込まない。
public static class GameDvrGuard
{
    private const string ServicePrefix = "BcastDVRUserService_";

    private const int PollIntervalMs = 15000;
    private const int StuckSeconds = 90;
    private const int CooldownSeconds = 600;

    private const uint ScManagerConnect = 0x0001;
    private const uint ScManagerEnumerateService = 0x0004;
    private const uint ServiceWin32 = 0x00000030;
    private const uint ServiceStateAll = 0x00000003;
    private const int ScEnumProcessInfo = 0;
    private const uint ServiceStopPending = 0x00000003;
    private const int ErrorMoreData = 234;

    private const uint ProcessTerminate = 0x0001;

    private static Thread _watcher;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManagerW(string machineName, string databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumServicesStatusExW(IntPtr scm, int infoLevel, uint serviceType, uint serviceState, IntPtr services, uint bufSize, out uint bytesNeeded, out uint servicesReturned, ref uint resumeHandle, string groupName);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct EnumServiceStatusProcess
    {
        public IntPtr ServiceName;
        public IntPtr DisplayName;
        public ServiceStatusProcess Status;
    }

    private struct Snapshot
    {
        public string Name;
        public uint State;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
    }

    /// <summary>番人スレッドを開始し、結果を人間可読の1行で返す (呼び出し元がログする)。</summary>
    public static string Start(bool enabled)
    {
        if (!enabled) return "disabled (cfg GameDvrAutoRepair=false)";
        if (!OperatingSystem.IsWindows()) return "skipped (not Windows)";
        if (_watcher != null) return "already running";

        try
        {
            _watcher = new Thread(WatchLoop) { IsBackground = true, Name = "EndKnot.GameDvrGuard" };
            _watcher.Start();
            return $"watching {ServicePrefix}* (poll={PollIntervalMs / 1000}s, stuck>={StuckSeconds}s, cooldown={CooldownSeconds}s)";
        }
        catch (Exception e) { return $"error: {e.Message}"; }
    }

    // 背景スレッドからの記録専用。HealthLog 未初期化なら書かずに諦める (ConsoleGuard と同じ理由)。
    private static void SafeNote(string line)
    {
        if (!HealthLog.IsInitialized) return;

        try { HealthLog.Note(line); }
        catch
        {
            // 計器の失敗で番人を止めない。
        }
    }

    private static void WatchLoop()
    {
        DateTime? stuckSince = null;
        uint lastCheckPoint = 0;
        DateTime lastKill = DateTime.MinValue;
        bool notedMissing = false;

        while (true)
        {
            try
            {
                Thread.Sleep(PollIntervalMs);

                if (!TryFindService(out Snapshot svc))
                {
                    if (!notedMissing)
                    {
                        notedMissing = true;
                        SafeNote($"GAMEDVRGUARD service {ServicePrefix}* not found (Game Bar / GameDVR not installed?)");
                    }

                    stuckSince = null;
                    continue;
                }

                if (svc.State != ServiceStopPending)
                {
                    if (stuckSince != null) SafeNote($"GAMEDVRGUARD {svc.Name} left STOP_PENDING on its own (state={svc.State})");
                    stuckSince = null;
                    continue;
                }

                DateTime now = DateTime.UtcNow;

                if (stuckSince == null || svc.CheckPoint != lastCheckPoint)
                {
                    // 初観測、または CheckPoint が進んだ (= SCM が生きた停止処理と見なしている) → 計測やり直し。
                    if (stuckSince == null) SafeNote($"GAMEDVRGUARD {svc.Name} entered STOP_PENDING (pid={svc.ProcessId}, checkpoint={svc.CheckPoint})");
                    stuckSince = now;
                    lastCheckPoint = svc.CheckPoint;
                    continue;
                }

                if ((now - stuckSince.Value).TotalSeconds < StuckSeconds) continue;
                if ((now - lastKill).TotalSeconds < CooldownSeconds) continue;

                if (svc.ProcessId == 0)
                {
                    SafeNote($"GAMEDVRGUARD {svc.Name} stuck in STOP_PENDING but has no host pid; nothing to terminate");
                    stuckSince = now;
                    continue;
                }

                // クールダウンは実際に終了できた時だけ消費する (OpenProcess 失敗 = ホストが既に消えた等は次周期で再評価)。
                bool terminated = TerminateHost(svc.ProcessId, out string result);
                if (terminated) lastKill = now;
                SafeNote($"GAMEDVRGUARD {svc.Name} stuck in STOP_PENDING for {(int)(now - stuckSince.Value).TotalSeconds}s (checkpoint frozen at {svc.CheckPoint}, waitHint={svc.WaitHint}ms) -> terminate host pid={svc.ProcessId}: {result}");
                stuckSince = null;
            }
            catch
            {
                // 番人スレッドは何があっても落とさない。
            }
        }
    }

    // SCM を列挙して接尾辞付きのユーザーサービス名を解決する (接尾辞はユーザー/機械ごとに違う)。
    private static bool TryFindService(out Snapshot found)
    {
        found = default;

        IntPtr scm = OpenSCManagerW(null, null, ScManagerConnect | ScManagerEnumerateService);
        if (scm == IntPtr.Zero) return false;

        IntPtr buffer = IntPtr.Zero;

        try
        {
            uint resume = 0;
            EnumServicesStatusExW(scm, ScEnumProcessInfo, ServiceWin32, ServiceStateAll, IntPtr.Zero, 0, out uint needed, out _, ref resume, null);
            if (needed == 0 || Marshal.GetLastWin32Error() != ErrorMoreData) return false;

            buffer = Marshal.AllocHGlobal((int)needed);
            resume = 0;
            if (!EnumServicesStatusExW(scm, ScEnumProcessInfo, ServiceWin32, ServiceStateAll, buffer, needed, out _, out uint count, ref resume, null)) return false;

            int stride = Marshal.SizeOf<EnumServiceStatusProcess>();

            for (int i = 0; i < count; i++)
            {
                var entry = Marshal.PtrToStructure<EnumServiceStatusProcess>(buffer + (i * stride));
                string name = Marshal.PtrToStringUni(entry.ServiceName);
                if (name == null || !name.StartsWith(ServicePrefix, StringComparison.OrdinalIgnoreCase)) continue;

                found = new Snapshot
                {
                    Name = name,
                    State = entry.Status.CurrentState,
                    CheckPoint = entry.Status.CheckPoint,
                    WaitHint = entry.Status.WaitHint,
                    ProcessId = entry.Status.ProcessId
                };

                return true;
            }

            return false;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            CloseServiceHandle(scm);
        }
    }

    // ホストは同じユーザーのトークンで動くユーザーサービスなので、昇格無しで終了できる。
    private static bool TerminateHost(uint pid, out string result)
    {
        IntPtr h = OpenProcess(ProcessTerminate, false, pid);

        if (h == IntPtr.Zero)
        {
            result = $"OpenProcess failed (err={Marshal.GetLastWin32Error()})";
            return false;
        }

        try
        {
            if (TerminateProcess(h, 1))
            {
                result = "terminated";
                return true;
            }

            result = $"TerminateProcess failed (err={Marshal.GetLastWin32Error()})";
            return false;
        }
        finally { CloseHandle(h); }
    }
}
