#if ANDROID
using System;
using System.Runtime.InteropServices;
using HarmonyLib;

namespace EndKnot.Modules;

// ランチャーのネイティブ層へ「メインスレッドが動いている」ことを毎フレーム知らせる。
// 心拍が途絶えると、ランチャー側がこのスレッドのスタックをディスク上の記録へ残す。
internal static class NativeHeartbeat
{
    private static bool _unavailable;
    private static bool _pauseUnavailable;

    [DllImport("fusion", EntryPoint = "fusion_heartbeat", ExactSpelling = true)]
    private static extern void FusionHeartbeat();

    [DllImport("fusion", EntryPoint = "fusion_set_paused", ExactSpelling = true)]
    private static extern void FusionSetPaused(int paused);

    public static void Beat()
    {
        if (_unavailable) return;

        try { FusionHeartbeat(); }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException)
        {
            // このエクスポートを持たないランチャーでは、以後のフレームで呼び出しを繰り返さない。
            _unavailable = true;
        }
    }

    // 画面オフや別画面の割り込みでフレームが止まる (または戻る) ことを知らせる。止まっている間の無音は停止として記録されない。
    public static void SetPaused(bool paused)
    {
        if (_pauseUnavailable) return;

        try { FusionSetPaused(paused ? 1 : 0); }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException)
        {
            _pauseUnavailable = true;
        }
    }
}

[HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnApplicationPause))]
internal static class NativeHeartbeatPausePatch
{
    public static void Prefix([HarmonyArgument(0)] bool pause)
    {
        NativeHeartbeat.SetPaused(pause);
    }
}
#endif
