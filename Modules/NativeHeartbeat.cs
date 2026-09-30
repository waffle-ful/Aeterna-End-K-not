#if ANDROID
using System;
using System.Runtime.InteropServices;

namespace EndKnot.Modules;

// ランチャーのネイティブ層へ「メインスレッドが動いている」ことを毎フレーム知らせる。
// 心拍が途絶えると、ランチャー側がこのスレッドのスタックをディスク上の記録へ残す。
internal static class NativeHeartbeat
{
    private static bool _unavailable;

    [DllImport("fusion", EntryPoint = "fusion_heartbeat", ExactSpelling = true)]
    private static extern void FusionHeartbeat();

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
}
#endif
