using System;
using System.Collections.Generic;
using System.Diagnostics;
using Hazel;

namespace EndKnot.Modules;

public static class DataFlagRateLimiter
{
    public class QueuedAction
    {
        public Action Action;
        public Action Cleanup;
        public int Cost;
        public bool Done;
        public bool Dropped;

        public System.Collections.IEnumerator Wait()
        {
            while (!Done)
                yield return null;
        }
    }

    private static int LastPingMs;

    /// <summary>両チャンネルの待機件数 (ゲーム開始直送窓の実ドレイン判定用)。</summary>
    public static int PendingCount => ReliableQueue.Count + UnreliableQueue.Count;

    /// <summary>ゲーム開始の fake Disconnected→roles→復元シーケンス専用の直送窓 (v4 暗転根治)。
    /// true の間、キューが空なら予算を無視して即実行する — qa.Wait() 完了が実ワイヤ送出と一致する。
    /// キュー非空時は追い越し防止のため通常キューに落ちる。PacketRateGate.StartWindowBypass と対で使う。</summary>
    public static bool StartWindowBypass;

    // =========================
    // RELIABLE
    // =========================

    private static readonly Queue<QueuedAction> ReliableQueue = new();
    private static readonly Channel Reliable = new();

    private const int ReliableRateLimitPerSecond = 23;

    // =========================
    // UNRELIABLE (SendOption.None)
    // =========================

    private static readonly Queue<QueuedAction> UnreliableQueue = new();
    private static readonly Channel Unreliable = new();

    private const int UnreliableRateLimitPerSecond = 23;

    /// <summary>1 チャンネルぶんの予算計数。固定窓とスライド窓の両方を常に進めておき、読む側で切り替える。</summary>
    private sealed class Channel
    {
        public readonly Queue<(long At, int Cost)> History = new();
        public readonly Stopwatch WindowTimer = Stopwatch.StartNew();
        public int SentThisWindow;
    }

    // =========================
    // 予算の数え方
    // =========================

    // 既定は固定窓 (1 秒ごとに計数を 0 に戻す)。これは窓の末尾で上限ぶん・次の窓の先頭でもう一度
    // 上限ぶんを連続で出せるため、1 秒のスライド窓で見ると上限の 2 倍近くがワイヤへ出る (23/s 設定で
    // 実測 40 本/秒)。StrictSendRateAccounting を ON にすると送出時刻を覚えて「直近 1 秒に出した合計」で
    // 判定し、公式サーバーが実際に数えている本数と会計が一致する。
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    // 毎エンキュー GetBool() を叩かないよう 1Hz サンプリングで拾う (WindowMs が更新)。
    private static bool StrictAccounting;

    // 窓の長さ。ping が改善した分だけ窓を伸ばして保守側へ倒す (従来の計数リセット条件と同じ意図)。
    private static long WindowMs()
    {
        int ping = AmongUsClient.Instance != null ? AmongUsClient.Instance.Ping : 0;

        if (Clock.ElapsedMilliseconds - LastPingSampleMs >= 1000)
        {
            LastPingSampleMs = Clock.ElapsedMilliseconds;
            LastPingMs = ping;
            try { StrictAccounting = Options.StrictSendRateAccounting?.GetBool() == true; }
            catch { StrictAccounting = false; }
        }

        return 1000 + Math.Max(0, LastPingMs - ping);
    }

    private static long LastPingSampleMs;

    /// <summary>この窓で使った合計。スライド窓では窓から出た記録を捨ててから数える。</summary>
    private static int Used(Channel ch)
    {
        long window = WindowMs();

        if (!StrictAccounting)
        {
            // 固定窓: 1 秒経ったら計数を 0 に戻す (従来の挙動)。
            if (ch.WindowTimer.ElapsedMilliseconds >= window)
            {
                ch.WindowTimer.Restart();
                ch.SentThisWindow = 0;
            }

            return ch.SentThisWindow;
        }

        long cutoff = Clock.ElapsedMilliseconds - window;
        var used = 0;

        while (ch.History.Count > 0 && ch.History.Peek().At <= cutoff)
            ch.History.Dequeue();

        foreach ((long _, int cost) in ch.History) used += cost;

        return used;
    }

    // どちらの数え方に切り替えても破綻しないよう、両方の計数を常に進める。
    private static void NoteSent(Channel ch, int cost)
    {
        ch.History.Enqueue((Clock.ElapsedMilliseconds, cost));
        ch.SentThisWindow += cost;
    }

    // =========================
    // PUBLIC API
    // =========================

    public static QueuedAction Enqueue(Action action, SendOption channel = SendOption.Reliable, int calls = 1, Action cleanup = null)
    {
        var qa = new QueuedAction
        {
            Action = action,
            Cleanup = cleanup,
            Cost = calls,
            Done = false
        };

        // Not needed on modded regions
        if (GameStates.CurrentServerType is not (GameStates.ServerType.Local or GameStates.ServerType.Vanilla))
        {
            Execute(qa);
            return qa;
        }

        // 試合終了後に積まれた RPC は要らない (rate-limit 待ち中にもう不要なら drop)
        if (GameStates.IsEnded && !GameStates.IsLobby)
        {
            Drop(qa);
            return qa;
        }

        switch (channel)
        {
            case SendOption.Reliable:
                EnqueueInternal(ReliableQueue, Reliable, ReliableRateLimitPerSecond, qa);
                break;

            case SendOption.None: // Unreliable
                EnqueueInternal(UnreliableQueue, Unreliable, UnreliableRateLimitPerSecond, qa);
                break;
        }

        return qa;
    }

    // Called once per frame
    public static void OnFixedUpdate()
    {
        ProcessQueue(ReliableQueue, Reliable, ReliableRateLimitPerSecond);
        ProcessQueue(UnreliableQueue, Unreliable, UnreliableRateLimitPerSecond);
    }

    // =========================
    // INTERNAL LOGIC
    // =========================

    private static void EnqueueInternal(
        Queue<QueuedAction> queue,
        Channel ch,
        int limit,
        QueuedAction qa)
    {
        // Try immediate execution if no backlog
        if (queue.Count == 0 && (Used(ch) + qa.Cost <= limit || StartWindowBypass))
        {
            Execute(qa);
            NoteSent(ch, qa.Cost);
            return;
        }

        queue.Enqueue(qa);
    }

    private static void ProcessQueue(
        Queue<QueuedAction> queue,
        Channel ch,
        int limit)
    {
        int used = Used(ch);

        while (queue.Count > 0)
        {
            var next = queue.Peek();

            if (used + next.Cost > limit)
                break;

            queue.Dequeue();

            Execute(next);
            NoteSent(ch, next.Cost);
            used += next.Cost;
        }
    }

    private static void Execute(QueuedAction qa)
    {
        try
        {
            qa.Action();
        }
        catch (Exception e)
        {
            Utils.ThrowException(e);
        }
        finally
        {
            qa.Dropped = false;
            qa.Done = true;
        }
    }

    public static void DropQueue()
    {
        ClearQueue(ReliableQueue);
        ClearQueue(UnreliableQueue);

        ResetChannel(Reliable);
        ResetChannel(Unreliable);
    }

    private static void ResetChannel(Channel ch)
    {
        ch.History.Clear();
        ch.SentThisWindow = 0;
        ch.WindowTimer.Restart();
    }

    private static void ClearQueue(Queue<QueuedAction> queue)
    {
        while (queue.Count > 0)
        {
            var qa = queue.Dequeue();
            Drop(qa);
        }
    }

    private static void Drop(QueuedAction qa)
    {
        try
        {
            qa.Cleanup?.Invoke();
        }
        catch (Exception e)
        {
            Utils.ThrowException(e);
        }

        qa.Dropped = true;
        qa.Done = true;
    }
}
