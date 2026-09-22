using HarmonyLib;

namespace EndKnot;
// https://github.com/tukasa0001/TownOfHost/pull/1274/commits/164d1463e46f0ec453e136c7a2f28a8039cd7fc4

[HarmonyPatch(typeof(MovingPlatformBehaviour))]
public static class MovingPlatformBehaviourPatch
{
    private static bool IsDisabled;

    [HarmonyPatch(nameof(MovingPlatformBehaviour.Start))]
    [HarmonyPrefix]
    public static void StartPrefix(MovingPlatformBehaviour __instance)
    {
        IsDisabled = Options.DisableAirshipMovingPlatform.GetBool();

        if (IsDisabled)
        {
            __instance.transform.localPosition = __instance.DisabledPosition;
            ShipStatus.Instance.CastFast<AirshipStatus>().outOfOrderPlat.SetActive(true);
        }
    }

    [HarmonyPatch(nameof(MovingPlatformBehaviour.IsDirty), MethodType.Getter)]
    [HarmonyPrefix]
    public static bool GetIsDirtyPrefix(ref bool __result)
    {
        if (IsDisabled)
        {
            __result = false;
            return false;
        }

        return true;
    }

    [HarmonyPatch(nameof(MovingPlatformBehaviour.Use), typeof(PlayerControl))]
    [HarmonyPrefix]
    public static bool UsePrefix([HarmonyArgument(0)] PlayerControl player)
    {
        if (IsDisabled) return false;

        // 波動砲のチャージ中は移動を縛られている側なので、動く床でも抜け出せない
        if (player != null && Roles.JackalHadouHo.IsInShotSequence(player.PlayerId)) return false;

        return true;
    }

    [HarmonyPatch(nameof(MovingPlatformBehaviour.SetSide))]
    [HarmonyPrefix]
    public static bool SetSidePrefix()
    {
        return !IsDisabled;
    }
}