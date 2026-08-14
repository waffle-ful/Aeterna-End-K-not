using System.Diagnostics;
using UnityEngine;

namespace EndKnot;

// Frame-time attribution for the settings menu. The menu holds a steady 50–80 ms/frame while open
// (2026-08-15 Health HITCH stream, zero GC involvement) and nothing in a one-shot log can say which
// per-frame path is eating it — so the candidates stamp their time here and one aggregate line goes
// out every 5 seconds while the menu is open (per-event logging would reintroduce the log-I/O hitches
// the perf workstream removed).
public static class MenuPerfProbe
{
    private static long RowFuTicks;
    private static int RowFuCalls;
    private static long PreviewTicks;
    private static int PreviewCalls;
    private static long ReflowTicks;
    private static int ReflowCalls;
    private static int Frames;
    private static float NextDump;
    private static float LastPump;

    public static long Stamp() => Stopwatch.GetTimestamp();

    public static void RowFixedUpdate(long start)
    {
        RowFuTicks += Stopwatch.GetTimestamp() - start;
        RowFuCalls++;
    }

    public static void Preview(long start)
    {
        PreviewTicks += Stopwatch.GetTimestamp() - start;
        PreviewCalls++;
    }

    public static void Reflow(long start)
    {
        ReflowTicks += Stopwatch.GetTimestamp() - start;
        ReflowCalls++;
    }

    // Called once per frame while the settings menu is open.
    public static void Pump()
    {
        float now = Time.unscaledTime;

        // First frame after an open (or the very first ever): the previous window is stale — it spans
        // however long the menu was closed — so drop it and start a fresh 5s window.
        if (now - LastPump > 2f || NextDump == 0f)
        {
            Reset();
            LastPump = now;
            NextDump = now + 5f;
            return;
        }

        LastPump = now;
        Frames++;

        if (now < NextDump) return;

        NextDump = now + 5f;

        var totalRows = 0;
        var activeRows = 0;

        foreach (var kv in ModGameOptionsMenu.BehaviourList)
        {
            OptionBehaviour ob = kv.Value;
            if (!ob) continue;

            totalRows++;

            try
            {
                if (ob.gameObject.activeInHierarchy) activeRows++;
            }
            catch
            {
                // a stale IL2CPP wrapper mid-destroy — counting it as inactive is fine for a probe
            }
        }

        Logger.Info($"5s window: frames={Frames} ({Frames / 5f:F0}fps) rows={activeRows} active / {totalRows} built, rowFixedUpdate={Ms(RowFuTicks):F1}ms/{RowFuCalls} calls, preview={Ms(PreviewTicks):F1}ms/{PreviewCalls}, reflow={Ms(ReflowTicks):F1}ms/{ReflowCalls}, {Patches.OptionSearchSuggestPatch.DebugState()}", "MenuPerf");

        Reset();
        return;

        static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
    }

    private static void Reset()
    {
        Frames = 0;
        RowFuTicks = 0;
        RowFuCalls = 0;
        PreviewTicks = 0;
        PreviewCalls = 0;
        ReflowTicks = 0;
        ReflowCalls = 0;
    }
}
