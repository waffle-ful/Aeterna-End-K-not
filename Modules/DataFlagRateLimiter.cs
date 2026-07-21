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
    private static readonly Stopwatch ReliableTimer = Stopwatch.StartNew();
    private static int ReliableSent;

    private const int ReliableRateLimitPerSecond = 23;

    // =========================
    // UNRELIABLE (SendOption.None)
    // =========================

    private static readonly Queue<QueuedAction> UnreliableQueue = new();
    private static readonly Stopwatch UnreliableTimer = Stopwatch.StartNew();
    private static int UnreliableSent;

    private const int UnreliableRateLimitPerSecond = 23;

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
                EnqueueInternal(ReliableQueue, ref ReliableSent, ReliableRateLimitPerSecond, qa);
                break;

            case SendOption.None: // Unreliable
                EnqueueInternal(UnreliableQueue, ref UnreliableSent, UnreliableRateLimitPerSecond, qa);
                break;
        }

        return qa;
    }

    // Called once per frame
    public static void OnFixedUpdate()
    {
        ProcessQueue(ReliableQueue, ref ReliableSent, ReliableTimer, ReliableRateLimitPerSecond);
        ProcessQueue(UnreliableQueue, ref UnreliableSent, UnreliableTimer, UnreliableRateLimitPerSecond);
    }

    // =========================
    // INTERNAL LOGIC
    // =========================

    private static void EnqueueInternal(
        Queue<QueuedAction> queue,
        ref int sent,
        int limit,
        QueuedAction qa)
    {
        // Try immediate execution if no backlog
        if (queue.Count == 0 && (sent + qa.Cost <= limit || StartWindowBypass))
        {
            Execute(qa);
            sent += qa.Cost;
            return;
        }

        queue.Enqueue(qa);
    }

    private static void ProcessQueue(
        Queue<QueuedAction> queue,
        ref int sent,
        Stopwatch timer,
        int limit)
    {
        // Reset window every second
        if (timer.ElapsedMilliseconds >= 1000 + Math.Max(0, LastPingMs - AmongUsClient.Instance.Ping))
        {
            LastPingMs = AmongUsClient.Instance.Ping;
            timer.Restart();
            sent = 0;
        }

        while (queue.Count > 0)
        {
            var next = queue.Peek();

            if (sent + next.Cost > limit)
                break;

            queue.Dequeue();

            Execute(next);
            sent += next.Cost;
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

        ReliableSent = 0;
        UnreliableSent = 0;

        ReliableTimer.Restart();
        UnreliableTimer.Restart();
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
