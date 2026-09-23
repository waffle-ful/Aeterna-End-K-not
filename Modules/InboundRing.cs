using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using HarmonyLib;
using Hazel;
using InnerNet;

namespace EndKnot.Modules;

// 受信側の事後解析計器 (挙動変更なし・送信ゼロ)。
// 送信側のリング (PacketRateGate の DCRING) は「こちらが何を送ったか」しか見えないため、
// 「直前 10 秒に何も送っていないのに Hacking」という標本 (ロビー作成 +13〜15 秒の署名) を前にすると
// サーバーが自分の都合で切ったのか、サーバーから届いた何かにこちらが反応したのかを分けられない。
// ここでは受信メッセージの見出し (root tag / 先頭サブ tag / RPC 番号 / 長さ) だけを秒単位で溜め、
// 切断時に DCRX として DCRING の隣へ残す。あわせて DCRING に写らない Unreliable 送信の本数と、
// ロビー齢 / プロセス齢を同じ行に載せる。
// 生還したロビーの対照群として、ホストのオンラインロビーでは作成 +20 秒に LOBBYRX を 1 行出す。
public static class InboundRing
{
    private const int RingSize = 512;
    private const int SubScanCap = 64;
    private const int ControlDumpDelaySec = 20;

    private struct Record
    {
        public long UnixSec;
        public int Length;
        public byte Tag;
        public byte InnerTag;
        public byte SubCount;
        public short RpcCallId; // -1 = RPC ではない / 読めなかった
        public bool Reliable;
    }

    private static readonly object Gate = new();
    private static readonly Record[] Ring = new Record[RingSize];
    private static int RingPos;
    private static int RingCount;

    // Unreliable 送信の秒バケット (送信はメインスレッドのみ)。
    private struct UnrelBucket
    {
        public long Sec;
        public int Count;
        public int Bytes;
        public int GameData;
        public int Other;
    }

    private static readonly UnrelBucket[] Unrel = new UnrelBucket[64];

    private static long _joinedAtUnix;
    private static int _joinEpoch;
    private static readonly long ProcStartUnix = ReadProcStart();

    private static long ReadProcStart()
    {
        try { return new DateTimeOffset(Process.GetCurrentProcess().StartTime).ToUnixTimeSeconds(); }
        catch { return 0; }
    }

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>ネットワークスレッドから呼ばれる。reader の Position には触れない (Buffer を添字で覗くだけ)。</summary>
    internal static void RecordInbound(MessageReader reader, SendOption option)
    {
        try
        {
            if (reader == null) return;

            var rec = new Record
            {
                UnixSec = Now,
                Length = reader.Length,
                Tag = reader.Tag,
                RpcCallId = -1,
                Reliable = option == SendOption.Reliable
            };

            if (rec.Tag is 5 or 6) PeekGameData(reader, rec.Tag, ref rec);

            lock (Gate)
            {
                Ring[RingPos] = rec;
                RingPos = (RingPos + 1) % RingSize;
                if (RingCount < RingSize) RingCount++;
            }
        }
        catch { /* 計器は受信処理を止めてはいけない */ }
    }

    // tag 5: [gameId int32][sub...] / tag 6: [gameId int32][packed targetClientId][sub...]
    // sub: [len u16][tag][payload]、RPC(2) の payload は [packed netId][callId]。
    private static void PeekGameData(MessageReader reader, byte tag, ref Record rec)
    {
        var buf = reader.Buffer;
        int start = reader.Offset;
        int end = start + reader.Length;
        if (buf == null || end > buf.Length) return;

        int pos = start + 4;
        if (tag == 6 && !SkipPacked(buf, end, ref pos)) return;

        bool first = true;
        int subs = 0;

        while (pos + 3 <= end && subs < SubScanCap)
        {
            int len = buf[pos] | (buf[pos + 1] << 8);
            byte subTag = buf[pos + 2];
            int payload = pos + 3;
            if (payload + len > end) break;

            if (first)
            {
                rec.InnerTag = subTag;
                first = false;

                if (subTag == 2)
                {
                    int p = payload;
                    if (SkipPacked(buf, payload + len, ref p) && p < payload + len) rec.RpcCallId = buf[p];
                }
            }

            subs++;
            pos = payload + len;
        }

        rec.SubCount = (byte)Math.Min(subs, 255);
    }

    private static bool SkipPacked(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<byte> buf, int end, ref int pos)
    {
        for (int i = 0; i < 5; i++)
        {
            if (pos >= end) return false;
            byte b = buf[pos++];
            if ((b & 0x80) == 0) return true;
        }

        return false;
    }

    /// <summary>PacketRateGate.RecordInstrumentation から Unreliable 送信ごとに呼ぶ。本数と大分類だけ数える (コピー無し)。</summary>
    internal static void NoteUnreliableSend(MessageWriter msg)
    {
        try
        {
            long now = Now;
            ref UnrelBucket b = ref Unrel[now % Unrel.Length];

            if (b.Sec != now) b = new UnrelBucket { Sec = now };

            b.Count++;
            b.Bytes += msg.Length;

            // None の先頭 1byte ヘッダの直後が [len u16][tag]。
            byte top = msg.Length >= 4 ? msg.Buffer[3] : (byte)0;
            if (top is 5 or 6) b.GameData++;
            else b.Other++;
        }
        catch { }
    }

    internal static void OnGameJoined()
    {
        try
        {
            _joinedAtUnix = Now;
            int epoch = ++_joinEpoch;

            if (!AmongUsClient.Instance || !AmongUsClient.Instance.AmHost) return;
            if (AmongUsClient.Instance.NetworkMode != NetworkModes.OnlineGame) return;

            LateTask.New(() =>
            {
                if (epoch != _joinEpoch || !GameStates.IsLobby) return;
                HealthLog.NoteAnom($"LOBBYRX {Describe()}");
            }, ControlDumpDelaySec, "InboundRing control dump", log: false);
        }
        catch { }
    }

    private static string AgeFields()
    {
        long now = Now;
        string lobby = _joinedAtUnix > 0 ? (now - _joinedAtUnix).ToString() : "?";
        string proc = ProcStartUnix > 0 ? (now - ProcStartUnix).ToString() : "?";
        return $"lobbyAge={lobby} procAge={proc}";
    }

    /// <summary>切断時 / 対照ダンプ用の 1 行。受信の秒ヒストグラム + Unreliable 送信 + 齢。</summary>
    public static string Describe()
    {
        try
        {
            long now = Now;
            var sb = new StringBuilder();
            sb.Append(AgeFields());
            sb.Append(" recv(sec ago -> count/bytes): ");

            Record[] snap;
            int count, pos;

            lock (Gate)
            {
                snap = (Record[])Ring.Clone();
                count = RingCount;
                pos = RingPos;
            }

            var bySec = new SortedDictionary<long, (int count, int bytes, SortedDictionary<string, int> keys)>();
            int start = count < RingSize ? 0 : pos;

            for (int i = 0; i < count; i++)
            {
                Record r = snap[(start + i) % RingSize];
                if (now - r.UnixSec > 60) continue;

                if (!bySec.TryGetValue(r.UnixSec, out var agg)) agg = (0, 0, new SortedDictionary<string, int>());
                agg.count++;
                agg.bytes += r.Length;

                string key = (r.Reliable ? "r" : "u") + r.Tag;
                if (r.Tag is 5 or 6) key += $".{r.InnerTag}";
                if (r.RpcCallId >= 0) key += $"c{r.RpcCallId}";
                if (r.SubCount > 1) key += $"/n{r.SubCount}";

                agg.keys.TryGetValue(key, out int k);
                agg.keys[key] = k + 1;
                bySec[r.UnixSec] = agg;
            }

            if (bySec.Count == 0) sb.Append("none");

            bool first = true;

            foreach (var kv in bySec)
            {
                if (!first) sb.Append(", ");
                first = false;
                sb.Append($"{now - kv.Key}s:{kv.Value.count}/{kv.Value.bytes}B[");
                bool firstKey = true;

                foreach (var k in kv.Value.keys)
                {
                    if (!firstKey) sb.Append(' ');
                    firstKey = false;
                    sb.Append($"{k.Key}x{k.Value}");
                }

                sb.Append(']');
            }

            sb.Append(" unrelSent(sec ago -> count/bytes gd/other): ");
            var buckets = new List<UnrelBucket>();
            foreach (UnrelBucket b in Unrel)
                if (b.Sec > 0 && now - b.Sec <= 30) buckets.Add(b);
            buckets.Sort((a, b) => a.Sec.CompareTo(b.Sec));

            if (buckets.Count == 0) sb.Append("none");

            for (int i = 0; i < buckets.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                UnrelBucket b = buckets[i];
                sb.Append($"{now - b.Sec}s:{b.Count}/{b.Bytes}B {b.GameData}/{b.Other}");
            }

            return sb.ToString();
        }
        catch (Exception e) { return $"InboundRing.Describe failed: {e.Message}"; }
    }
}

[HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.HandleMessage), typeof(MessageReader), typeof(SendOption))]
internal static class InboundRingHandleMessagePatch
{
    public static void Prefix(MessageReader reader, SendOption sendOption) => InboundRing.RecordInbound(reader, sendOption);
}
