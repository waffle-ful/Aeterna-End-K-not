using System;
using HarmonyLib;
using InnerNet;

namespace EndKnot.Patches;

// These methods sometimes throw random exceptions in the base game code and stop the code after them from executing
// Simply swallow the exception and continue as if nothing happened

[HarmonyPatch(typeof(AbilityButton), nameof(AbilityButton.SetFromSettings))]
[HarmonyPatch(typeof(ActionButton), nameof(ActionButton.SetEnabled))]
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.RawSetName))]
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.SetName))]
[HarmonyPatch(typeof(CosmeticsLayer), nameof(CosmeticsLayer.UpdateBodyMaterial))]
[HarmonyPatch(typeof(CosmeticsCache), nameof(CosmeticsCache.ClearUnusedCosmetics))]
[HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.SendOrDisconnect))]
[HarmonyPatch(typeof(DisconnectPopup), nameof(DisconnectPopup.DoShow))]
[HarmonyPatch(typeof(IGameOptionsExtensions), nameof(IGameOptionsExtensions.GetValue))]
// PackAndSendQueuedMessages: 死体装飾の transient Data 残骸など、null string を持つ message が
// queue に紛れ込むと毎 frame ArgumentNullException で SendAllStreamedObjects が壊れ、joiner が
// ロード固着で落ちる症状を防ぐ。bad msg は dequeue 済みなので次 frame には消える + dirty walk で
// 再 enqueue されても他の good msg が先に処理されるので最終的に sync が完走する。
[HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.PackAndSendQueuedMessages))]
static class ExceptionSwallowers
{
    public static Exception Finalizer()
    {
        return null;
    }
}
