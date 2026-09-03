using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Text;

namespace EndKnot.Modules;

// managed アロケーションの「型別」帰属計器。AllocProbe (系統別ブラケット) が「どこで」を答えるのに対し、
// こちらは「何を」確保しているかを答える。ランタイムの GCAllocationTick イベント (約 100KB 確保ごとに
// 1 発・その閾値を跨いだ確保の型名を載せる) を in-process の EventListener で受け、5 秒窓で型別に
// 積算して Health.log に ALLOCTYPE 1 行を出す。サンプリングなので 1 窓 (~1MB/s なら ~50 発) でも
// 上位の型は安定して見える。コールバックは確保スレッドで走るため lock 付きで積み、書き出しは
// AllocProbe.FrameEnd (メインスレッド) から呼ばれる FlushWindow で行う。
//
// ⚠ 観測コスト (2026-09-04 実機・4 人 InTask): GC キーワードを Verbose で有効にすると GCAllocationTick 以外に
// FinalizeObject 等の「GC 1 回につきオブジェクト数ぶん」のイベントも流れ、Il2CppInterop ラッパーの finalizer が
// 数千個走る gen0 GC のたびにリスナー側で 10〜20MB を確保して次の GC を呼ぶ 20 秒周期 (200ms HITCH・gen0 3〜4/窓)
// を作った (OFF の 09-02 実測 InTask HITCH 11 件 → ON で 444 件)。イベント ID での事前フィルタは EventListener に
// 無いため、既定 OFF の測定専用スイッチとし、読むときは gen0=0 の静穏窓の型だけを信用する。
public sealed class AllocTypeSampler : EventListener
{
    private const EventKeywords GcKeyword = (EventKeywords)0x1;
    private const int AllocationTickEventId = 10;

    private static AllocTypeSampler instance;
    private static bool startFailed;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, (long Bytes, int Ticks)> Window = new(64);
    private static readonly List<KeyValuePair<string, (long Bytes, int Ticks)>> Sorted = new(64);
    private static readonly StringBuilder Sb = new(512);
    private static long windowTotal;
    private static int windowTicks;

    private AllocTypeSampler() { }

    public static void EnsureStarted()
    {
        if (instance != null || startFailed) return;
        if (Main.AllocTypeSampling is not { Value: true }) return;

        try { instance = new AllocTypeSampler(); }
        catch (Exception e)
        {
            startFailed = true;
            Logger.Warn($"GCAllocationTick listener unavailable: {e.GetType().Name}: {e.Message}", "AllocTypeSampler");
        }
    }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        // 基底コンストラクタの途中でも呼ばれる (インスタンス側の状態には触らないこと)
        if (eventSource.Name == "Microsoft-Windows-DotNETRuntime")
            EnableEvents(eventSource, EventLevel.Verbose, GcKeyword);
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (e.EventId != AllocationTickEventId || e.Payload == null) return;

        try
        {
            // GCAllocationTick_V2 以降: [0]=AllocationAmount(uint) [1]=AllocationKind [2]=ClrInstanceID
            // [3]=AllocationAmount64(ulong) [4]=TypeID [5]=TypeName [6]=HeapIndex [7]=Address [8]=ObjectSize(V4)
            int nameIdx = e.PayloadNames?.IndexOf("TypeName") ?? 5;
            int amtIdx = e.PayloadNames?.IndexOf("AllocationAmount64") ?? 3;
            if (nameIdx < 0 || amtIdx < 0 || nameIdx >= e.Payload.Count || amtIdx >= e.Payload.Count) return;

            string type = e.Payload[nameIdx] as string ?? "?";
            long amount = Convert.ToInt64(e.Payload[amtIdx]);

            lock (Gate)
            {
                Window.TryGetValue(type, out (long Bytes, int Ticks) acc);
                Window[type] = (acc.Bytes + amount, acc.Ticks + 1);
                windowTotal += amount;
                windowTicks++;
            }
        }
        catch { /* 計器は本体の動作に影響させない */ }
    }

    // 静かな窓 (AllocProbe 側が ALLOC 行を省いた時) は型サンプルも捨てて次窓へ。
    public static void DropWindow()
    {
        EnsureStarted();
        if (instance == null) return;

        lock (Gate)
        {
            Window.Clear();
            windowTotal = 0;
            windowTicks = 0;
        }
    }

    // ALLOC 行の直後に ALLOCTYPE 1 行 (上位 10 型・KB と tick 数) を出す。
    public static void FlushWindow()
    {
        EnsureStarted();
        if (instance == null) return;

        lock (Gate)
        {
            if (windowTicks == 0) return;

            Sorted.Clear();
            foreach (var kv in Window) Sorted.Add(kv);
            Sorted.Sort((a, b) => b.Value.Bytes.CompareTo(a.Value.Bytes));

            Sb.Clear();
            Sb.Append("ALLOCTYPE win=5s ticks=").Append(windowTicks).Append(" sampledKB=").Append(windowTotal / 1024);

            int n = Math.Min(10, Sorted.Count);
            for (int i = 0; i < n; i++)
            {
                var kv = Sorted[i];
                Sb.Append(' ').Append(ShortName(kv.Key)).Append('=').Append(kv.Value.Bytes / 1024).Append("KB/").Append(kv.Value.Ticks);
            }

            Sb.Append(" t=").Append(Utils.TimeStamp);
            Window.Clear();
            windowTotal = 0;
            windowTicks = 0;
        }

        HealthLog.Note(Sb.ToString());
    }

    // 名前空間を落として読みやすくする (ジェネリック引数の内側はそのまま)。空白は行の区切りを壊すので消す。
    private static string ShortName(string full)
    {
        if (string.IsNullOrEmpty(full)) return "?";
        int lt = full.IndexOf('[');
        string head = lt >= 0 ? full[..lt] : full;
        int dot = head.LastIndexOf('.');
        string name = dot >= 0 ? head[(dot + 1)..] : head;
        if (lt >= 0) name += full[lt..];
        return name.Replace(" ", "");
    }
}
