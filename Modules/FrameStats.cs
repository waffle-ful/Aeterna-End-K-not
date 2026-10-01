using System;
using System.Diagnostics;

namespace EndKnot.Modules;

// 描画フレーム間隔の 5 秒窓統計。平均 fps は一瞬の引っ掛かりを均してしまうので、遅い側 1% のフレームだけで
// 割り戻した fps (1% low) と最大 1 フレームの長さを Health.log の心拍行と ALLOC 行へ載せる。
// 標本は固定長配列に貯めて窓の締めに並べ替えるだけで、確保も interop 呼び出しも無い。
public static class FrameStats
{
    private const int Capacity = 2048;
    private static readonly float[] Samples = new float[Capacity];
    private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;
    private static int count;
    private static long lastTs;

    // 直近に締めた窓の値。標本不足の窓は -1。
    public static int Low1Fps { get; private set; } = -1;
    public static int MaxFrameMs { get; private set; } = -1;

    // 描画フレームごとに 1 回 (メインスレッド)。
    public static void Frame()
    {
        long ts = Stopwatch.GetTimestamp();
        if (lastTs != 0 && count < Capacity) Samples[count++] = (float)((ts - lastTs) * MsPerTick);
        lastTs = ts;
    }

    // 窓の締め (AllocProbe.FrameEnd の 5 秒周期から)。
    public static void CloseWindow()
    {
        int n = count;
        count = 0;

        if (n < 30)
        {
            Low1Fps = -1;
            MaxFrameMs = -1;
            return;
        }

        Array.Sort(Samples, 0, n);
        int worst = Math.Max(1, n / 100);
        double sum = 0;
        for (int i = n - worst; i < n; i++) sum += Samples[i];
        double avgWorstMs = sum / worst;
        Low1Fps = avgWorstMs > 0 ? (int)(1000.0 / avgWorstMs) : -1;
        MaxFrameMs = (int)Samples[n - 1];
    }
}
