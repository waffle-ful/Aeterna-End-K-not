using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;

namespace EndKnot.Modules;

// NullReferenceException swallowed by empty catch blocks leaves no trace in the logs, yet each one
// costs an exception throw, and on arm64-on-x86 translators (houdini) the hardware null fault is never
// handed back to the runtime, so the main thread spins forever. Log every throwing site once, with the
// stack at the throw point, so such sites can be found on a real device and turned into null checks.
internal static class FirstChanceNreLog
{
    private const int MaxSites = 200;
    private const int MaxFrames = 10;

    private static readonly ConcurrentDictionary<string, int> Sites = new();

    [ThreadStatic] private static bool InHandler;

    public static void Install()
    {
        AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;
    }

    private static void OnFirstChance(object sender, FirstChanceExceptionEventArgs e)
    {
        if (InHandler || e.Exception is not NullReferenceException ex) return;
        InHandler = true;

        try
        {
            string site = ex.TargetSite is { } m ? $"{m.DeclaringType?.FullName}.{m.Name}" : "?";
            int count = Sites.AddOrUpdate(site, 1, (_, c) => c + 1);
            if (count != 1 || Sites.Count > MaxSites) return;

            var sb = new StringBuilder();
            sb.Append("NRE site=").Append(site).Append(" stack=");
            StackFrame[] frames = new StackTrace(1, false).GetFrames();
            var written = 0;

            foreach (StackFrame frame in frames)
            {
                if (written >= MaxFrames) break;
                var method = frame.GetMethod();
                if (method == null || method.DeclaringType == typeof(FirstChanceNreLog)) continue;
                if (written > 0) sb.Append(" < ");
                sb.Append(method.DeclaringType?.Name).Append('.').Append(method.Name);
                written++;
            }

            Logger.Warn(sb.ToString(), "FirstChanceNRE");
        }
        catch { }
        finally { InHandler = false; }
    }
}
