using System;
using EndKnot.Modules.Extensions;
using HarmonyLib;
using InnerNet;
using UnityEngine;
using static EndKnot.Translator;

namespace EndKnot.Modules;

// 切断で試合が成立しなくなったときに自動で廃村する。
public static class AutoAbort
{
    private static OptionItem EnableAutoAbort;
    private static OptionItem DisconnectCount;
    private static OptionItem MinAlivePlayers;
    private static OptionItem DelaySeconds;
    private static OptionItem NotifyBeforeAbort;

    private static int DisconnectedAliveCount;
    private static CountdownTimer PendingTimer;

    public static void SetupCustomOption()
    {
        new TextOptionItem(110070, "MenuTitle.AutoAbort", TabGroup.GameSettings)
            .SetColor(new Color32(255, 110, 110, byte.MaxValue))
            .SetHeader(true);

        EnableAutoAbort = new BooleanOptionItem(960050, "EnableAutoAbort", false, TabGroup.GameSettings)
            .SetColor(new Color32(255, 110, 110, byte.MaxValue));

        DisconnectCount = new IntegerOptionItem(960051, "AutoAbortDisconnectCount", new(1, 14, 1), 3, TabGroup.GameSettings)
            .SetParent(EnableAutoAbort)
            .SetColor(new Color32(255, 110, 110, byte.MaxValue));

        MinAlivePlayers = new IntegerOptionItem(960052, "AutoAbortMinAlivePlayers", new(0, 14, 1), 0, TabGroup.GameSettings)
            .SetParent(EnableAutoAbort)
            .SetColor(new Color32(255, 110, 110, byte.MaxValue));

        DelaySeconds = new FloatOptionItem(960053, "AutoAbortDelaySeconds", new(0f, 30f, 1f), 5f, TabGroup.GameSettings)
            .SetParent(EnableAutoAbort)
            .SetValueFormat(OptionFormat.Seconds)
            .SetColor(new Color32(255, 110, 110, byte.MaxValue));

        NotifyBeforeAbort = new BooleanOptionItem(960054, "AutoAbortNotifyBeforeAbort", true, TabGroup.GameSettings)
            .SetParent(EnableAutoAbort)
            .SetColor(new Color32(255, 110, 110, byte.MaxValue));
    }

    public static void ResetForNewGame()
    {
        DisconnectedAliveCount = 0;
        PendingTimer?.Dispose();
        PendingTimer = null;
    }

    // AmongUsClient.OnPlayerLeft の Prefix から呼ぶこと。既存の OnPlayerLeftPatch.Postfix が
    // state.SetDead() を呼んで生存キャッシュを落とすより前に読まないと、切断者は常に「既に死亡」に見える。
    public static void OnPlayerLeftEarly(ClientData data, DisconnectReasons reason)
    {
        if (!AmongUsClient.Instance.AmHost || EnableAutoAbort?.GetBool() != true || !GameStates.InGame) return;
        if (data == null || !data.Character) return;
        if (GameStates.CurrentServerType is GameStates.ServerType.Local) return;

        // ホスト自身が蹴った/BAN した客は「事故で抜けた」わけではないので数えない。
        // (/kp の一括キックや荒らし対応で、ホストの意図しない自動廃村が起きるのを防ぐ)
        if (reason is DisconnectReasons.Kicked or DisconnectReasons.Banned)
        {
            Logger.Info($"Not counting {data.PlayerName} toward auto-abort (host-initiated: {reason})", "AutoAbort");
            return;
        }

        bool wasAlive = data.Character.IsAlive();
        int aliveAfter = Main.AllAlivePlayerControlsCount - (wasAlive ? 1 : 0);

        if (wasAlive) DisconnectedAliveCount++;

        bool countTrigger = DisconnectedAliveCount >= DisconnectCount.GetInt();
        bool minAliveTrigger = MinAlivePlayers.GetInt() > 0 && aliveAfter < MinAlivePlayers.GetInt();
        if (!countTrigger && !minAliveTrigger) return;

        Logger.Info($"Auto-abort condition met (disconnectedAlive={DisconnectedAliveCount}, aliveAfter={aliveAfter}), firing in {DelaySeconds.GetFloat():F1}s", "AutoAbort");

        // 連鎖切断のたびに再スケジュールするが、通知は「1回だけ」の約束どおり最初の1回だけ送る
        if (NotifyBeforeAbort.GetBool() && PendingTimer == null)
            Utils.SendMessage(GetString("Message.AutoAbortNotice"), byte.MaxValue);

        PendingTimer?.Dispose();
        float delay = Math.Max(0.1f, DelaySeconds.GetFloat());
        PendingTimer = new CountdownTimer(delay, Trigger, cancelOnMeeting: false, cancelOnGameEnd: false);
    }

    private static void Trigger()
    {
        PendingTimer = null;

        if (!AmongUsClient.Instance.AmHost || !GameStates.InGame)
        {
            Logger.Info("Auto-abort skipped: no longer in a game", "AutoAbort");
            return;
        }

        // 切断で「勝敗が別方向に自然成立」した後に上書きしないためのガード
        // (例: 全インポスターの切断でクルー勝利が同時成立するケース)。
        if (CustomWinnerHolder.WinnerTeam != CustomWinner.Default)
        {
            Logger.Info($"Auto-abort skipped: a winner was already decided ({CustomWinnerHolder.WinnerTeam})", "AutoAbort");
            return;
        }

        Logger.Info($"Auto-abort triggered (disconnectedAlive={DisconnectedAliveCount})", "AutoAbort");
        CustomWinnerHolder.ResetAndSetWinner(CustomWinner.Draw);
        GameEndChecker.CheckCustomEndCriteria();
    }
}

[HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.StartGame))]
internal static class AutoAbortStartGamePatch
{
    public static void Prefix() => AutoAbort.ResetForNewGame();
}

[HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnPlayerLeft))]
internal static class AutoAbortDisconnectPatch
{
    public static void Prefix([HarmonyArgument(0)] ClientData data, [HarmonyArgument(1)] DisconnectReasons reason)
    {
        try { AutoAbort.OnPlayerLeftEarly(data, reason); }
        catch (Exception e) { Utils.ThrowException(e); }
    }
}
