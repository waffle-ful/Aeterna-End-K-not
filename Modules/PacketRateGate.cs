using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Hazel;
using InnerNet;

namespace EndKnot.Modules;

// PacketSplitPatch (関所) を通る全 Reliable パケットに対するグローバルレートゲート＋計装。
// DataFlagRateLimiter は opt-in の呼び出し元しか守らないが、ここは全送信が通る唯一の
// チョークポイントなので取り残しが無い。Unreliable (移動同期) には一切触れない。
// TryGate は「小型 (分割不要) パケット」と「分割済みチャンクの個別 Send()」の両方から呼ばれる
// 単一の入口: 全 Reliable 送信が単一 FIFO+単一予算に一本化されるので、追い越し/バーストが起きない。
public static class PacketRateGate
{
    private const int GateLimitPerSecond = 25; // DataFlagRateLimiter(23) よりわずかに上乗せ
    private const int QueueSafetyValve = 300; // 超過時はペース付き高速ドレインに切り替える閾値
    private const int SafetyValveDrainPerFrame = 15; // 安全弁作動中でも1フレームで吐き出す上限 (バーストを自作しない)
    private const int RingBufferSize = 512;
    private const int WarnWindowSeconds = 10;
    private const int WarnThresholdCount = 100; // 直近10秒合計の警告閾値 (TOHK warningThreshold と同値)

    private sealed class QueuedPacket
    {
        public byte[] Bytes;
        public SendOption SendOption;
    }

    private static readonly Queue<QueuedPacket> PendingReliableQueue = new();

    /// <summary>ゲート待ちの Reliable パケット数。ゲーム開始バースト (役職テーブル N² 本) の
    /// 排水完了を外から判定する用途 (FirstTurnMeetingTrigger が会議を早く始めすぎない為のシグナル)。</summary>
    public static int PendingCount => PendingReliableQueue.Count;
    private static readonly Stopwatch WindowTimer = Stopwatch.StartNew();
    private static int SentThisWindow;
    private static int LastPingMs;
    private static object LastConnection;
    private static bool SafetyValveActive;

    // --- リングバッファ (キック事後解析用) ---
    private struct Record
    {
        public long UnixSec;
        public int Length;
        public byte Tag;
        public SendOption Option;
        public bool Gated;
    }

    private static readonly Record[] Ring = new Record[RingBufferSize];
    private static int RingPos;
    private static int RingCount;

    // --- 1秒粒度の直近10秒メータ ---
    private static readonly int[] SecCounts = new int[WarnWindowSeconds];
    private static long CurrentSecBucket;
    private static int PeakWarned;

    /// <summary>関所の Prefix 冒頭で毎回呼ぶ。全パケットについて計装だけ行う (ゲート判定はしない)。</summary>
    public static void RecordInstrumentation(MessageWriter msg)
    {
        try
        {
            // リングバッファ/タグ覗き見は移動同期の Unreliable ホットパスに触れないよう Reliable のみに限定する。
            // タグ覗き見は msg.Buffer を直接参照するゼロコピー実装 (PeekTopTagZeroCopy 参照)。
            // EarlyWarning.OnPacket はコピー無しで安いので全種で呼ぶ。
            if (msg.SendOption == SendOption.Reliable)
            {
                byte tag = 0;
                try { tag = PeekTopTagZeroCopy(msg); } catch { /* best-effort */ }

                AppendRing(msg.Length, tag, msg.SendOption, gated: false);
                TickSecondMeter();
            }

            EarlyWarning.OnPacket("Chokepoint", msg.Length, msg.Length, msg.SendOption.ToString());
        }
        catch { /* 計装は送信を止めてはいけない */ }
    }

    /// <summary>
    /// Reliable パケット (小型パケット本体、または既存分割経路が作った各チャンク) に対する唯一のゲート入口。
    /// true = キューに積んだので呼び出し元は送信を行わず「送信責任はゲートへ引き受けた」ものとして扱ってよい。
    /// false = ゲート対象外 (Unreliable/非公式鯖) または予算内で素通り可能 (カウント済み・コピー無し) — 呼び出し元が自分で送信すること。
    /// 失敗時は必ず false を返す (ExceptionSwallowers の無音握り潰しでパケットが消えるより、素通りさせて実際に送る方が安全)。
    /// </summary>
    public static bool TryGate(InnerNetClient instance, MessageWriter msg)
    {
        try
        {
            if (msg.SendOption != SendOption.Reliable) return false;
            if (GameStates.CurrentServerType is not (GameStates.ServerType.Local or GameStates.ServerType.Vanilla)) return false;

            DetectReconnect(instance);
            ResetWindowIfNeeded();

            if (PendingReliableQueue.Count == 0 && SentThisWindow < GateLimitPerSecond)
            {
                SentThisWindow++;
                return false;
            }

            // 予算超過 or 追い越し禁止 (キュー非空) → コピーしてキューへ。msg は呼び出し元復帰後に
            // Recycle/Clear されうるため、byte[] としてコピーを取ってから保持する。
            PendingReliableQueue.Enqueue(new QueuedPacket { Bytes = msg.ToByteArray(false), SendOption = msg.SendOption });
            return true;
        }
        catch (Exception e)
        {
            Logger.Error($"PacketRateGate.TryGate failed, falling back to immediate send: {e}", "PacketRateGate");
            return false;
        }
    }

    /// <summary>毎 FixedUpdate で呼ぶ。予算の許す限りキュー先頭から送信する。</summary>
    public static void OnFixedUpdate()
    {
        try
        {
            var instance = AmongUsClient.Instance;
            if (instance == null) return;

            DetectReconnect(instance);
            ResetWindowIfNeeded();

            if (PendingReliableQueue.Count > QueueSafetyValve)
            {
                // 異常事態: desync の方が無音キックより悪いのでドロップはしないが、同一フレームで
                // 全件を吐き出すとゲートが防ぐべきバーストを自分で作ってしまうため、ペースを付けて
                // 複数フレームに分散する (予算チェックは無視してよいが、フレーム分散は必須)。
                if (!SafetyValveActive)
                {
                    SafetyValveActive = true;
                    Logger.Error($"PacketRateGate queue exceeded safety valve ({PendingReliableQueue.Count} > {QueueSafetyValve}); pacing at {SafetyValveDrainPerFrame}/frame ignoring budget", "PacketRateGate");
                }

                int n = Math.Min(SafetyValveDrainPerFrame, PendingReliableQueue.Count);
                for (int i = 0; i < n; i++)
                    SendQueued(instance, PendingReliableQueue.Dequeue());

                return;
            }

            SafetyValveActive = false;

            while (PendingReliableQueue.Count > 0 && SentThisWindow < GateLimitPerSecond)
            {
                SendQueued(instance, PendingReliableQueue.Dequeue());
                SentThisWindow++;
            }
        }
        catch (Exception e)
        {
            Logger.Error($"PacketRateGate.OnFixedUpdate failed: {e}", "PacketRateGate");
        }
    }

    private static void SendQueued(AmongUsClient instance, QueuedPacket qp)
    {
        var writer = MessageWriter.Get(qp.SendOption);
        try
        {
            writer.Write(qp.Bytes);
            var err = instance.connection.Send(writer);
            if (err != SendErrors.None)
                Logger.Info($"PacketRateGate SendMessage Error={err}", "PacketRateGate");
        }
        finally
        {
            writer.Recycle();
        }
    }

    private static void DetectReconnect(InnerNetClient instance)
    {
        object conn = instance != null ? instance.connection : null;
        if (ReferenceEquals(conn, LastConnection)) return;

        // 接続が変わった (再接続/切断) ので、宛先不明の待機パケットは捨てる。
        LastConnection = conn;
        PendingReliableQueue.Clear();
        SentThisWindow = 0;
        SafetyValveActive = false;
        WindowTimer.Restart();
    }

    private static void ResetWindowIfNeeded()
    {
        if (AmongUsClient.Instance == null) return;

        if (WindowTimer.ElapsedMilliseconds >= 1000 + Math.Max(0, LastPingMs - AmongUsClient.Instance.Ping))
        {
            LastPingMs = AmongUsClient.Instance.Ping;
            WindowTimer.Restart();
            SentThisWindow = 0;
        }
    }

    private static void TickSecondMeter()
    {
        long sec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (CurrentSecBucket == 0) CurrentSecBucket = sec;

        // 経過秒数ぶんシフト (欠けた秒は 0 埋め)
        long diff = sec - CurrentSecBucket;
        if (diff > 0)
        {
            int shift = (int)Math.Min(diff, WarnWindowSeconds);
            for (int i = 0; i < WarnWindowSeconds - shift; i++)
                SecCounts[i] = SecCounts[i + shift];
            for (int i = WarnWindowSeconds - shift; i < WarnWindowSeconds; i++)
                SecCounts[i] = 0;
            CurrentSecBucket = sec;
        }

        SecCounts[WarnWindowSeconds - 1]++;

        int total = 0;
        foreach (int c in SecCounts) total += c;

        if (total > WarnThresholdCount && total > PeakWarned)
        {
            PeakWarned = total;
            Logger.Warn($"Reliable packet burst: {total} packets in last {WarnWindowSeconds}s (threshold {WarnThresholdCount})", "PacketRateGate");
        }
        else if (total <= WarnThresholdCount)
        {
            PeakWarned = 0;
        }
    }

    private static void AppendRing(int length, byte tag, SendOption option, bool gated)
    {
        Ring[RingPos] = new Record
        {
            UnixSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Length = length,
            Tag = tag,
            Option = option,
            Gated = gated
        };
        RingPos = (RingPos + 1) % RingBufferSize;
        if (RingCount < RingBufferSize) RingCount++;
    }

    // msg.Buffer は MessageWriter.Get(sendOption) が Clear() 内で先頭に SendOption 1byte を書き込んでから
    // ペイロードを積む実装 (Hazel-Networking 標準) なので、ToByteArray(false) が剥がすのと同じ 1byte ヘッダを
    // コピー無しで直接スキップし、先頭サブメッセージの length(u16 LE)+tag だけを Buffer から直接読む。
    // 想定外の内部レイアウトだった場合は長さの整合性チェックで弾いて 0 (不明) を返す best-effort な診断用途。
    private static byte PeekTopTagZeroCopy(MessageWriter msg)
    {
        // Hazel の MessageWriter.Clear は Reliable だと先頭 3byte (SendOption 1 + reliable ID 2)、
        // None だと 1byte をヘッダとして確保する (ToByteArray(false) が剥がすのと同じ量)。
        int headerLen = msg.SendOption == SendOption.Reliable ? 3 : 1;
        const int SubHeaderLen = 3; // ushort length + byte tag

        byte[] buffer = msg.Buffer;
        if (buffer == null || msg.Length < headerLen + SubHeaderLen || buffer.Length < headerLen + SubHeaderLen) return 0;

        int subLength = buffer[headerLen] | (buffer[headerLen + 1] << 8);
        byte tag = buffer[headerLen + 2];

        // 整合性チェック: 覗いた長さが msg 全体を超えるなら想定したレイアウトが外れている → 諦める。
        if (subLength < 0 || headerLen + SubHeaderLen + subLength > msg.Length) return 0;

        return tag;
    }

    /// <summary>切断時にキック事後解析用に呼ぶ。直近リングバッファを 1 行のヒストグラムに集約して返す。</summary>
    public static string DumpRecent()
    {
        try
        {
            if (RingCount == 0) return "PacketRateGate: no recent packets recorded";

            var bySec = new SortedDictionary<long, (int count, int bytes)>();

            int start = RingCount < RingBufferSize ? 0 : RingPos;
            for (int i = 0; i < RingCount; i++)
            {
                var rec = Ring[(start + i) % RingBufferSize];
                bySec.TryGetValue(rec.UnixSec, out var agg);
                agg.count++;
                agg.bytes += rec.Length;
                bySec[rec.UnixSec] = agg;
            }

            var sb = new StringBuilder();
            sb.Append("PacketRateGate recent(sec ago -> count/bytes): ");
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            bool first = true;
            foreach (var kv in bySec)
            {
                if (!first) sb.Append(", ");
                first = false;
                sb.Append($"{now - kv.Key}s:{kv.Value.count}/{kv.Value.bytes}B");
            }

            return sb.ToString();
        }
        catch (Exception e)
        {
            return $"PacketRateGate.DumpRecent failed: {e}";
        }
    }
}
