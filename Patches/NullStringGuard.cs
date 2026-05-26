using System;
using HarmonyLib;
using Hazel;

namespace EndKnot.Patches;

// Hazel.MessageWriter.Write(string) は内部で System.Text.Encoding.GetBytes(s) を呼ぶため、
// s が null だと ArgumentNullException を投げる。本パッチは **置換しない** — throw を素通りさせて
// ExceptionSwallowers (PackAndSendQueuedMessages の Finalizer) に吸収させる。
//
// 過去版で `value = string.Empty` 置換を入れたところ、空文字 PlayerName が wire に乗って
// AU server 側 anti-cheat が host を kick する回帰を起こした (2026-05-26)。
// 置換は禁止 — 純粋に「null が来た callsite を managed stack でログに残す」だけのプローブとして使う。
//
// throttle: 起動から 5 件 + 以降は 600 呼び出しに 1 件で打ち切り (ログ膨張防止)。
[HarmonyPatch(typeof(MessageWriter), nameof(MessageWriter.Write), new[] { typeof(string) })]
internal static class MessageWriterWriteStringNullGuard
{
    private static int LoggedCount;
    private static int CallsSinceLastLog;
    private const int InitialBurstCap = 5;
    private const int ThrottleEveryNCalls = 600;

    public static void Prefix(MessageWriter __instance, string value)
    {
        if (value != null) return;

        CallsSinceLastLog++;
        bool inBurst = LoggedCount < InitialBurstCap;
        bool throttleTick = CallsSinceLastLog >= ThrottleEveryNCalls;

        if (!inBurst && !throttleTick) return;

        LoggedCount++;
        CallsSinceLastLog = 0;
        // Environment.StackTrace は managed 側のスタックのみ拾えるが、IL2CPP からのコール
        // でも EHR 側 callsite (CustomRpcSender / Modules/*) が露出することが多い
        string stack;
        try { stack = Environment.StackTrace; } catch { stack = "<stack unavailable>"; }
        Logger.Warn($"[NullStringWrite #{LoggedCount}] MessageWriter.Write(string) called with null. Letting throw propagate to ExceptionSwallowers. Stack:\n{stack}", "NullStringGuard");
    }
}
