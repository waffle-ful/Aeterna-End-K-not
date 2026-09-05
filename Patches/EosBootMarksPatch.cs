using AmongUs.Data.Player;
using EndKnot.Modules;
using HarmonyLib;

namespace EndKnot;

// 起動直後のバニラ EOS ログイン鎖 (DLC 受領検証 → インベントリ更新 → 統計初期化 → ログインフロー完了) の
// 節目を BOOT 行のマークとして刻む計器。ホストローカルの時刻記録だけで、送信も挙動変更も無い。
// 引数付きのメソッドは避けて引数なしの節目だけを選んでいる (il2cpp の struct 引数を持つメソッドへの
// パッチは実行時に無音で落ちる)。RefreshAll は参照型 1 引数なので安全。
[HarmonyPatch]
internal static class EosBootMarksPatch
{
    internal static bool Prepare() => Main.EosBootMarks.Value;

    [HarmonyPatch(typeof(InventoryManager), nameof(InventoryManager.RefreshAll))]
    [HarmonyPostfix]
    private static void RefreshAll_Postfix() => BootTimeline.Mark("eos.inv.begin");

    [HarmonyPatch(typeof(InventoryManager), nameof(InventoryManager.CheckEquipped))]
    [HarmonyPostfix]
    private static void CheckEquipped_Postfix() => BootTimeline.Mark("eos.inv.end");

    [HarmonyPatch(typeof(PlayerStatsData), nameof(PlayerStatsData.InitializeStats))]
    [HarmonyPostfix]
    private static void InitializeStats_Postfix() => BootTimeline.Mark("eos.stats");

    [HarmonyPatch(typeof(EOSManager), nameof(EOSManager.EndFinalPartsOfLoginFlowFullAccount))]
    [HarmonyPostfix]
    private static void EndFinalParts_Postfix() => BootTimeline.Mark("eos.flowend");
}
