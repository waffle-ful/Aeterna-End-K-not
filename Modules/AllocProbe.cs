using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace EndKnot.Modules;

// managed アロケーションの系統別帰属計器。試合中の CLR ヒープ膨張 (ソロでも数十 MB/ゲーム) が
// どのサブシステム由来かは一発ログでは分からないため、FixedUpdateCaller の各系統の前後で
// GC.GetAllocatedBytesForCurrentThread() (精密・呼び出しコスト数十ns) を差分し、5 秒窓で集計して
// Health.log に 1 行出す。全スレッド計 (GetTotalAllocatedBytes) との差分が「FixedUpdate 外
// (レンダ系 Update パッチ / ネットワーク受信 / TMP 等)」の取り分になる。
public static class AllocProbe
{
    private static readonly Dictionary<string, (long Bytes, int Calls)> Buckets = new(16);
    private static readonly StringBuilder Sb = new(256);
    private static float nextDump;
    private static long windowGlobalStart = -1;
    private static int frames;

    public static long Now() => GC.GetAllocatedBytesForCurrentThread();

    // prev から現在までの確保量を bucket に積み、次区間の起点を返す。
    public static long Mark(string bucket, long prev)
    {
        long now = GC.GetAllocatedBytesForCurrentThread();
        long delta = now - prev;

        if (delta > 0)
        {
            Buckets.TryGetValue(bucket, out (long Bytes, int Calls) acc);
            Buckets[bucket] = (acc.Bytes + delta, acc.Calls + 1);
        }

        return now;
    }

    // 毎 tick の締め。5 秒ごとに集計行を吐いてリセットする。
    public static void FrameEnd()
    {
        frames++;
        float now = Time.unscaledTime;
        if (nextDump == 0f) nextDump = now + 5f;
        if (now < nextDump) return;

        nextDump = now + 5f;

        long globalNow = GC.GetTotalAllocatedBytes(false);
        long globalDelta = windowGlobalStart >= 0 ? globalNow - windowGlobalStart : -1;
        windowGlobalStart = globalNow;

        long tickTotal = 0;
        foreach (var kv in Buckets) tickTotal += kv.Value.Bytes;

        // 窓全体で 256KB 未満なら静かな状態 — 行を出さない (ログ肥大防止)
        if (globalDelta >= 0 && globalDelta < 256 * 1024 && tickTotal < 256 * 1024)
        {
            Buckets.Clear();
            frames = 0;
            return;
        }

        Sb.Clear();
        Sb.Append("ALLOC win=5s frames=").Append(frames);
        Sb.Append(" globalKB=").Append(globalDelta >= 0 ? globalDelta / 1024 : -1);
        Sb.Append(" tickKB=").Append(tickTotal / 1024);
        Sb.Append(" otherKB=").Append(globalDelta >= 0 ? (globalDelta - tickTotal) / 1024 : -1);

        foreach (var kv in Buckets)
        {
            if (kv.Value.Bytes < 16 * 1024) continue; // 16KB/5s 未満の系統は省略
            Sb.Append(' ').Append(kv.Key).Append('=').Append(kv.Value.Bytes / 1024).Append("KB/").Append(kv.Value.Calls);
        }

        Sb.Append(" t=").Append(Utils.TimeStamp);
        HealthLog.Note(Sb.ToString());

        Buckets.Clear();
        frames = 0;
    }
}
