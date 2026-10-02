using System;
using System.Diagnostics.Tracing;
using System.IO;

namespace EndKnot.Modules;

// 共有 ArrayPool から大きな配列を借りた箇所のスタックを記録する診断。
// 借りた配列は返却後もスレッドごとの控えに残り続けるので、起動時の 1 回の大きな借用がそのまま常駐メモリになる。
// ENDKNOT_POOLTRACE=1 のときだけ動く (既定では何も登録しない)。
internal sealed class PoolTrace : EventListener
{
    private const int MinBytes = 1024 * 1024;
    private static PoolTrace _instance;
    private static string _path;
    [ThreadStatic] private static bool _busy;

    public static void InstallIfRequested()
    {
        if (_instance != null || Environment.GetEnvironmentVariable("ENDKNOT_POOLTRACE") != "1") return;
        try
        {
            _path = Path.Combine(Path.GetTempPath(), "endknot-pooltrace.txt");
            File.WriteAllText(_path, $"pooltrace start {DateTime.Now:O}\n");
            _instance = new PoolTrace();
        }
        catch { }
    }

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == "System.Buffers.ArrayPoolEventSource")
            EnableEvents(source, EventLevel.Verbose);
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (_busy || e.Payload == null || e.Payload.Count < 2) return;
        // BufferRented / BufferAllocated はどちらも 2 番目が bufferSize (要素数)。
        if (e.EventName is not ("BufferRented" or "BufferAllocated")) return;
        if (e.Payload[1] is not int size || size < MinBytes) return;
        _busy = true;
        try
        {
            lock (this)
                File.AppendAllText(_path, $"--- {e.EventName} size={size} thread={Environment.CurrentManagedThreadId} t={DateTime.Now:HH:mm:ss.fff}\n{Environment.StackTrace}\n");
        }
        catch { }
        finally { _busy = false; }
    }
}
