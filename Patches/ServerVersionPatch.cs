using HarmonyLib;

namespace EndKnot.Patches;

[HarmonyPatch(typeof(Constants), nameof(Constants.GetBroadcastVersion))]
internal static class ServerUpdatePatch
{
    public static void Postfix(ref int __result)
    {
        if (GameStates.IsLocalGame) Logger.Info($"IsLocalGame: {__result}", "VersionServer");

        if (GameStates.IsOnlineGame)
        {
            // Changing server version for AU mods.
            // The +25 modded flag (Innersloth's "host authority mode") is signaled by
            // making the version's revision-mod-50 land in [25, 50). Only add 25 when
            // we're not already in that range — adding unconditionally on a repeat call
            // pushes the version OUT of the modded band into the next major group,
            // which Innersloth's anti-cheat may treat as garbage and kick.
            var revision = __result % 50;
            if (revision < 25)
                __result += 25;
            Logger.Info($"IsOnlineGame: {__result}", "VersionServer");
        }
    }
}

[HarmonyPatch(typeof(Constants), nameof(Constants.IsVersionModded))]
public static class IsVersionModdedPatch
{
    public static bool Prefix(ref bool __result)
    {
        __result = true;
        return false;
    }
}