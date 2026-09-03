using HarmonyLib;
using Hazel;

namespace EndKnot.Patches;

// From: https://github.com/Rabek009/MoreGamemodes/blob/master/Patches/ClientPatch.cs

[HarmonyPatch(typeof(VoteBanSystem), nameof(VoteBanSystem.CmdAddVote))]
internal static class CmdAddVotePatch
{
    public static bool Prefix([HarmonyArgument(0)] int clientId)
    {
        if (!AmongUsClient.Instance.AmHost) return true;

        PlayerControl pc = PlayerControl.LocalPlayer;
        PlayerControl target = Utils.GetClientById(clientId)?.Character;

        if (target != null)
        {
            Main.PlayerStates[pc.PlayerId].Role.OnVoteKick(pc, target);
            Logger.Info($" {pc.GetNameWithRole()} => {target.GetNameWithRole()}", "VoteKick");
        }

        return false;
    }
}

[HarmonyPatch(typeof(VoteBanSystem), nameof(VoteBanSystem.AddVote))]
internal static class AddVotePatch
{
    public static bool Prefix(VoteBanSystem __instance, [HarmonyArgument(0)] int srcClient, [HarmonyArgument(1)] int clientId)
    {
        if (!AmongUsClient.Instance.AmHost) return true;

        // srcClient は共有 NetId 経由で届くペイロードの自己申告で、受信層に送信者の identity が無い。
        // 実体解決に失敗した場合まで先へ進めない (ログ行も含めて null 参照させない)。
        PlayerControl pc = Utils.GetClientById(srcClient)?.Character;
        PlayerControl target = Utils.GetClientById(clientId)?.Character;

        if (pc != null && target != null)
        {
            Main.PlayerStates[pc.PlayerId].Role.OnVoteKick(pc, target);
            Logger.Info($" {pc.GetNameWithRole()} => {target.GetNameWithRole()}", "VoteKick");
        }

        if (AmongUsClient.Instance.ClientId == srcClient || __instance != VoteBanSystem.Instance) return false;

        VoteBanSystem.Instance = Object.Instantiate(AmongUsClient.Instance.VoteBanPrefab);
        AmongUsClient.Instance.Spawn(VoteBanSystem.Instance);

        LateTask.New(() =>
        {
            MessageWriter writer = MessageWriter.Get(SendOption.Reliable);
            writer.StartMessage(5);
            writer.Write(AmongUsClient.Instance.GameId);
            writer.StartMessage(5);
            writer.WritePacked(__instance.NetId);
            writer.EndMessage();
            writer.EndMessage();
            AmongUsClient.Instance.SendOrDisconnect(writer);
            writer.Recycle();
        }, 0.5f);

        LateTask.New(() =>
        {
            AmongUsClient.Instance.RemoveNetObject(__instance);
            Object.Destroy(__instance.gameObject);
        }, 5f);

        return false;
    }
}