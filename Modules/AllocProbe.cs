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

        public Cursor(long managed, long il2)
        {
            Managed = managed;
            Il2 = il2;
        }
    }

    private static readonly Dictionary<string, (long Bytes, int Calls, long Il2)> Buckets = new(16);
    private static readonly StringBuilder Sb = new(320);
    private static float nextDump;
    private static long windowGlobalStart = -1;
    private static long windowMainStart = -1;   // メインスレッドの累計確保 (otherKB を「メイン内・FixedUpdate 外」と「他スレッド」に割る)
    private static long windowIl2Start = -1;    // il2cpp 使用量 (窓内で GC が走ると負になる → -1 表示)
    private static int windowGen0Start = -1;    // GC.CollectionCount(0) — churn が実際に gen0 GC を何回起こしているか (HITCH と相関を見る)
    private static int windowFrameStart = -1; // 窓開始時の Time.frameCount (FrameEnd は FixedUpdate 駆動 ~30Hz 定数なので、呼び出し回数を数えても描画フレーム数にならない)

    private static bool TrackIl2 => Main.AllocIl2Tracking is { Value: true };

    private static long Il2Now() => TrackIl2 ? GcPrepass.BoehmUsedBytes() : 0;

    public static Cursor Now() => new(GC.GetAllocatedBytesForCurrentThread(), Il2Now());

    // prev から現在までの確保量を bucket に積み、次区間の起点を返す。
    public static Cursor Mark(string bucket, Cursor prev)
    {
        long now = GC.GetAllocatedBytesForCurrentThread();
        long il2 = Il2Now();
        long delta = now - prev.Managed;
        long il2Delta = il2 - prev.Il2; // Boehm GC を跨ぐと負 → その区間の il2 は数えない

        if (delta > 0 || il2Delta > 0)
        {
            Buckets.TryGetValue(bucket, out (long Bytes, int Calls, long Il2) acc);
            Buckets[bucket] = (acc.Bytes + Math.Max(delta, 0), acc.Calls + 1, acc.Il2 + Math.Max(il2Delta, 0));
        }

        return new Cursor(now, il2);
    }

    // 毎 tick の締め。5 秒ごとに集計行を吐いてリセットする。
    public static void FrameEnd()
    {
        float now = Time.unscaledTime;

        if (nextDump == 0f)
        {
            nextDump = now + 5f;
            windowFrameStart = Time.frameCount;
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

        foreach (var kv in Buckets)
        {
            if (kv.Value.Bytes < 16 * 1024 && kv.Value.Il2 < 16 * 1024) continue; // 16KB/5s 未満の系統は省略
            Sb.Append(' ').Append(kv.Key).Append('=').Append(kv.Value.Bytes / 1024).Append("KB/").Append(kv.Value.Calls);
            if (TrackIl2) Sb.Append('~').Append(kv.Value.Il2 / 1024); // ~ の後が同区間の il2cpp 側 KB
        }

        Sb.Append(" t=").Append(Utils.TimeStamp);
        HealthLog.Note(Sb.ToString());

        Buckets.Clear();
        AllocTypeSampler.FlushWindow();
    }
}
