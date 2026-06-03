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

        // 転ばぬ先の杖: 公式サーバーで「Hacking」切断されたら、着地画面で理由+対処をホストに知らせる。
        // NetworkMode はこの Prefix 時点ではまだ OnlineGame なので IsOfficialServer() が正しく判定できる。
        // 配信者向け: 公式 kick / エラー / タイムアウトで切断されたら新しい部屋を自動で立て直す。
        // この Prefix 時点でしか AmHost / NetworkMode が正しく取れないので、ここで判定を始める。
        // (立て直す場合は OfficialServerNotice を抑制したいので、Hacking 警告より先に呼ぶ)
        AutoRehost.OnDisconnect(reason);

        if (reason == DisconnectReasons.Hacking)
            OfficialServerNotice.WarnAfterHackingKick();

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
