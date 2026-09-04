using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace EndKnot.Modules;

// managed / il2cpp 両ヒープのアロケーションを系統別に帰属する計器。試合中のヒープ膨張 (CLR 側は数十 MB/ゲーム、
// il2cpp Boehm 側は数 MB/20 秒でその回収が周期ヒッチになる) がどのサブシステム由来かは一発ログでは分からないため、
// FixedUpdateCaller の各系統や Update 系パッチの前後で
//   ・GC.GetAllocatedBytesForCurrentThread() (精密・数十 ns)
//   ・il2cpp_gc_get_used_size() (Boehm 使用量・GC を跨いだ区間は負になるので捨てる)
// を差分し、5 秒窓で集計して Health.log に 1 行出す。全スレッド計 (GetTotalAllocatedBytes) との差分が
// 「FixedUpdate 外 (レンダ系 Update パッチ / ネットワーク受信 / TMP 等)」の取り分になる。
public static class AllocProbe
{
    // 区間の起点。managed と il2cpp を同時に持つ (呼び出し側は var で受けて Mark に返すだけ)。
    public readonly struct Cursor
    {
        public readonly long Managed;
        public readonly long Il2;
        public readonly long Ts; // Stopwatch.GetTimestamp() — 区間の実時間 (ヒッチ帰属計器)

        public Cursor(long managed, long il2, long ts)
        {
            Managed = managed;
            Il2 = il2;
            Ts = ts;
        }
    }

    // Calls = ブラケット通過回数 (確保の有無を問わない — 時間集計のため毎回積む)。
    // Ticks/MaxTicks = 系統が使った実時間 (窓合計 / 最大 1 回)。GC 外のサブ秒ヒッチ (50〜65ms・gc0d=0) が
    // mod ブラケット内で起きているか外 (バニラ本体 / 未ブラケットのパッチ / レンダ) かを分ける計器。
    private static readonly Dictionary<string, (long Bytes, int Calls, long Il2, long Ticks, long MaxTicks)> Buckets = new(16);
    private static readonly double TicksPerMs = System.Diagnostics.Stopwatch.Frequency / 1000.0;

    // 直前の HealthLog.Tick 以降にブラケット内で使った実時間 (親系統のみ合算) と最大 1 回の系統。
    // HITCH 行が「その窓の停止のうち mod ブラケット内は何 ms か」を答えるための窓 (Tick 毎にリセット)。
    private static int MainThreadId; // 初回 FrameEnd (FixedUpdate) で確定。0 の間は AddTime を捨てる
    private static long tickWinTicks;
    private static long tickWinTopTicks;
    private static string tickWinTopName;

    // Tick 間隔のヒストグラム (ms 帯 ≥20/30/40/50/70/100)。HITCH 閾値 50ms の手前に山があるか裾だけかを ALLOC 行で見る。
    private static readonly int[] GapBins = new int[6];
    private static readonly int[] GapEdges = { 20, 30, 40, 50, 70, 100 };
    private static readonly StringBuilder Sb = new(320);
    private static float nextDump;
    private static long windowGlobalStart = -1;
    private static long windowMainStart = -1;   // メインスレッドの累計確保 (otherKB を「メイン内・FixedUpdate 外」と「他スレッド」に割る)
    private static long windowIl2Start = -1;    // il2cpp 使用量 (窓内で GC が走ると負になる → -1 表示)
    private static int windowGen0Start = -1;    // GC.CollectionCount(0) — churn が実際に gen0 GC を何回起こしているか (HITCH と相関を見る)
    private static int windowFrameStart = -1; // 窓開始時の Time.frameCount (FrameEnd は FixedUpdate 駆動 ~30Hz 定数なので、呼び出し回数を数えても描画フレーム数にならない)

    private static bool TrackIl2 => Main.AllocIl2Tracking is { Value: true };

    private static long Il2Now() => TrackIl2 ? GcPrepass.BoehmUsedBytes() : 0;

    public static Cursor Now() => new(GC.GetAllocatedBytesForCurrentThread(), Il2Now(), System.Diagnostics.Stopwatch.GetTimestamp());

    // prev から現在までの確保量と実時間を bucket に積み、次区間の起点を返す。
    public static Cursor Mark(string bucket, Cursor prev)
    {
        long now = GC.GetAllocatedBytesForCurrentThread();
        long il2 = Il2Now();
        long ts = System.Diagnostics.Stopwatch.GetTimestamp();
        long delta = now - prev.Managed;
        long il2Delta = il2 - prev.Il2; // Boehm GC を跨ぐと負 → その区間の il2 は数えない
        long dt = Math.Max(ts - prev.Ts, 0);

        Buckets.TryGetValue(bucket, out (long Bytes, int Calls, long Il2, long Ticks, long MaxTicks) acc);
        Buckets[bucket] = (acc.Bytes + Math.Max(delta, 0), acc.Calls + 1, acc.Il2 + Math.Max(il2Delta, 0), acc.Ticks + dt, Math.Max(acc.MaxTicks, dt));
        NoteTickWindow(bucket, dt);

        return new Cursor(now, il2, ts);
    }

    // ブラケットで囲めない同期処理 (ログ I/O 等) の実時間だけを積む (アロケは数えない)。
    public static void AddTime(string bucket, long startTs)
    {
        // Buckets はメインスレッド専用 (Dictionary は非同期安全でない)。Health.log は他スレッドからも書かれ得るので弾く。
        if (MainThreadId != System.Threading.Thread.CurrentThread.ManagedThreadId) return;
        long dt = Math.Max(System.Diagnostics.Stopwatch.GetTimestamp() - startTs, 0);
        Buckets.TryGetValue(bucket, out (long Bytes, int Calls, long Il2, long Ticks, long MaxTicks) acc);
        Buckets[bucket] = (acc.Bytes, acc.Calls + 1, acc.Il2, acc.Ticks + dt, Math.Max(acc.MaxTicks, dt));
        NoteTickWindow(bucket, dt);
    }

    private static void NoteTickWindow(string bucket, long dt)
    {
        if (bucket.IndexOf('.') < 0) tickWinTicks += dt; // "親.子" は親の内側なので合算しない

        if (dt > tickWinTopTicks)
        {
            tickWinTopTicks = dt;
            tickWinTopName = bucket;
        }
    }

    // HITCH 行用: 直前 Tick 以降の mod ブラケット合計 ms と最大 1 回の系統。ヒッチ時だけ呼ばれる (文字列合成はここだけ)。
    public static string TickWindowSuffix()
    {
        if (tickWinTopName == null) return " modMs=0";
        return $" modMs={(long)(tickWinTicks / TicksPerMs)} top={tickWinTopName}:{(long)(tickWinTopTicks / TicksPerMs)}";
    }

    // 毎 Tick の締め (HealthLog.Tick から)。gapMs をヒストグラムへ積み、Tick 窓の時間集計をリセットする。
    public static void EndTickWindow(long gapMs)
    {
        for (int i = GapEdges.Length - 1; i >= 0; i--)
        {
            if (gapMs < GapEdges[i]) continue;
            GapBins[i]++;
            break;
        }

        tickWinTicks = 0;
        tickWinTopTicks = 0;
        tickWinTopName = null;
    }

    // 毎 tick の締め。5 秒ごとに集計行を吐いてリセットする。
    public static void FrameEnd()
    {
        float now = Time.unscaledTime;

        if (nextDump == 0f)
        {
            nextDump = now + 5f;
            windowFrameStart = Time.frameCount;
            MainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
        }

        if (now < nextDump) return;

        nextDump = now + 5f;
        int fc = Time.frameCount;
        int frames = windowFrameStart >= 0 ? fc - windowFrameStart : -1;
        windowFrameStart = fc;

        long globalNow = GC.GetTotalAllocatedBytes(false);
        long globalDelta = windowGlobalStart >= 0 ? globalNow - windowGlobalStart : -1;
        windowGlobalStart = globalNow;

        long mainNow = GC.GetAllocatedBytesForCurrentThread();
        long mainDelta = windowMainStart >= 0 ? mainNow - windowMainStart : -1;
        windowMainStart = mainNow;

        long il2Now = Il2Now();
        long il2WinDelta = windowIl2Start >= 0 ? il2Now - windowIl2Start : -1;
        windowIl2Start = il2Now;

        int gen0Now = GC.CollectionCount(0);
        int gen0Delta = windowGen0Start >= 0 ? gen0Now - windowGen0Start : -1;
        windowGen0Start = gen0Now;

        long tickTotal = 0, il2Total = 0;

        foreach (var kv in Buckets)
        {
            if (kv.Key.IndexOf('.') >= 0) continue; // "親.子" のサブ系統 — 合計へは親だけを足す
            tickTotal += kv.Value.Bytes;
            il2Total += kv.Value.Il2;
        }

        // 窓全体で 256KB 未満なら静かな状態 — 行を出さない (ログ肥大防止)
        if (globalDelta >= 0 && globalDelta < 256 * 1024 && tickTotal < 256 * 1024 && il2Total < 256 * 1024)
        {
            Buckets.Clear();
            Array.Clear(GapBins, 0, GapBins.Length);
            AllocTypeSampler.DropWindow();
            return;
        }

        Sb.Clear();
        Sb.Append("ALLOC win=5s frames=").Append(frames);
        Sb.Append(" globalKB=").Append(globalDelta >= 0 ? globalDelta / 1024 : -1);
        Sb.Append(" tickKB=").Append(tickTotal / 1024);
        Sb.Append(" otherKB=").Append(globalDelta >= 0 ? (globalDelta - tickTotal) / 1024 : -1);
        // mainKB = メインスレッド内で FixedUpdate 系統外 (Update パッチ/受信/コルーチン)、thrKB = 他スレッド (ログ書出/裏デコード等)
        Sb.Append(" mainKB=").Append(mainDelta >= 0 ? (mainDelta - tickTotal) / 1024 : -1);
        Sb.Append(" thrKB=").Append(globalDelta >= 0 && mainDelta >= 0 ? (globalDelta - mainDelta) / 1024 : -1);
        Sb.Append(" gen0=").Append(gen0Delta);

        if (TrackIl2)
        {
            // il2KB = 系統ブラケット内で増えた Boehm 使用量の合計、il2WinKB = 窓全体の増分 (窓内で Boehm GC が走ると -1)
            Sb.Append(" il2KB=").Append(il2Total / 1024);
            Sb.Append(" il2WinKB=").Append(il2WinDelta >= 0 ? il2WinDelta / 1024 : -1);
        }

        // gap = Tick 間隔の度数 (≥20/≥30/≥40/≥50/≥70/≥100 ms)。HITCH 行はレート制限されるので真の頻度はこちらで読む。
        Sb.Append(" gap=");
        for (int i = 0; i < GapBins.Length; i++)
        {
            if (i > 0) Sb.Append('/');
            Sb.Append(GapBins[i]);
        }

        Array.Clear(GapBins, 0, GapBins.Length);

        foreach (var kv in Buckets)
        {
            long ms = (long)(kv.Value.Ticks / TicksPerMs);
            long maxMs = (long)(kv.Value.MaxTicks / TicksPerMs);
            // 16KB/5s 未満かつ 1ms/5s 未満の系統は省略
            if (kv.Value.Bytes < 16 * 1024 && kv.Value.Il2 < 16 * 1024 && ms < 1) continue;
            Sb.Append(' ').Append(kv.Key).Append('=').Append(kv.Value.Bytes / 1024).Append("KB/").Append(kv.Value.Calls);
            if (TrackIl2) Sb.Append('~').Append(kv.Value.Il2 / 1024); // ~ の後が同区間の il2cpp 側 KB
            if (ms >= 1 || maxMs >= 1) Sb.Append(':').Append(ms).Append('/').Append(maxMs); // : の後が窓合計 ms / 最大 1 回 ms
        }

        Sb.Append(" t=").Append(Utils.TimeStamp);
        HealthLog.Note(Sb.ToString());

        Buckets.Clear();
        AllocTypeSampler.FlushWindow();
    }
}
