using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using InnerNet;

namespace EndKnot.Modules;

public static class LobbySharingAPI
{
    public static long LastRequestTimeStamp;
    public static string LastRoomCode = string.Empty;

    public static void NotifyLobbyStatusChanged(LobbyStatus status) { }
}

[SuppressMessage("ReSharper", "InconsistentNaming")]
public enum LobbyStatus
{
    In_Lobby,
    In_Game,
    Ended,
    Closed
}

[HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.DisconnectInternal))]
internal static class ExitGamePatch
{
    public static void Prefix(InnerNetClient __instance, DisconnectReasons reason)
    {
        if (__instance is not AmongUsClient) return;

        Logger.Msg($"Exiting game - reason: {reason}", "ExitGamePatch.Prefix");

        GameStates.InGame = false;
        Main.RealOptionsData?.Restore(GameOptionsManager.Instance.CurrentGameOptions);
        DataFlagRateLimiter.DropQueue();

        if (SetUpRoleTextPatch.IsInIntro)
        {
            SetUpRoleTextPatch.IsInIntro = false;
            Utils.NotifyRoles(ForceLoop: true);
        }
    }

    public static void Postfix(InnerNetClient __instance)
    {
        if (__instance is not AmongUsClient) return;

        GameEndChecker.LoadingEndScreen = false;
    }
}
