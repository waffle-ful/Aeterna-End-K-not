using UnityEngine;

namespace EndKnot.Roles;

// 自分以外でタスクを全部終えた人の名前の横に印が出る (マッドメイトは☆・それ以外は〇)
internal class ProgressSeer : IAddon
{
    private static OptionItem MadmateSeen;
    private static OptionItem WorkhorseSeen;

    public AddonTypes Type => AddonTypes.ImpOnly;

    public void SetupCustomOption()
    {
        Options.SetupAdtRoleOptions(710700, CustomRoles.ProgressSeer, canSetNum: true, tab: TabGroup.Addons);

        MadmateSeen = new BooleanOptionItem(710710, "ProgressKillerMadseen", true, TabGroup.Addons)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.ProgressSeer]);

        WorkhorseSeen = new BooleanOptionItem(710711, "ProgressWorkhorseseen", true, TabGroup.Addons)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.ProgressSeer]);
    }

    private static Color MarkColor => Utils.GetRoleColor(CustomRoles.ProgressSeer);

    public static string GetMark(PlayerControl seer, PlayerControl target)
    {
        if (seer.PlayerId == target.PlayerId || !seer.IsAlive() || !seer.Is(CustomRoles.ProgressSeer)) return string.Empty;
        if (!target.GetTaskState().IsTaskFinished) return string.Empty;

        bool isMadmate = target.IsMadmate();
        if (MadmateSeen.GetBool() && isMadmate) return Utils.ColorString(MarkColor, "☆");
        if (WorkhorseSeen.GetBool() && !isMadmate) return Utils.ColorString(MarkColor, "〇");
        return string.Empty;
    }

    // インサイダーには、持ち主が見ているマッドメイトの☆だけを見せる
    public static string GetInsiderMark(PlayerControl holder, PlayerControl target)
    {
        if (holder.PlayerId == target.PlayerId || !holder.Is(CustomRoles.ProgressSeer)) return string.Empty;
        if (!MadmateSeen.GetBool() || !target.IsMadmate() || !target.GetTaskState().IsTaskFinished) return string.Empty;
        return Utils.ColorString(MarkColor, "☆");
    }

    // 印は客ごとに名前を送り直さないと出ないので、誰かがタスクを終えた時に持ち主へ送る
    public static void OnTaskFinished(PlayerControl player)
    {
        // 会議中は会議の通常の名前更新が印を拾う
        if (!AmongUsClient.Instance.AmHost || !GameStates.IsInTask) return;

        foreach (PlayerControl pc in Main.CachedAlivePlayerControls())
        {
            if (pc.PlayerId != player.PlayerId && pc.Is(CustomRoles.ProgressSeer))
                Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: player);
        }
    }
}
