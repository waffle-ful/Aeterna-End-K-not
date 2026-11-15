namespace EndKnot.Roles;

internal class Mimic : IAddon
{
    // キルしたとき、設定した確率で全員の画面にキルフラッシュを出す (TownOfHost-K の Notifier 由来)
    private static OptionItem KillFlashChance;

    public AddonTypes Type => AddonTypes.ImpOnly;

    public void SetupCustomOption()
    {
        Options.SetupAdtRoleOptions(16000, CustomRoles.Mimic, canSetNum: true, tab: TabGroup.Addons);

        Options.MimicCanSeeDeadRoles = new BooleanOptionItem(16010, "MimicCanSeeDeadRoles", true, TabGroup.Addons)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Mimic]);

        KillFlashChance = new IntegerOptionItem(16011, "MimicKillFlashChance", new(0, 100, 5), 0, TabGroup.Addons)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Mimic])
            .SetValueFormat(OptionFormat.Percent);
    }

    public static void OnMurder(PlayerControl killer, PlayerControl target)
    {
        if (killer == target || !killer.Is(CustomRoles.Mimic)) return;
        int chance = KillFlashChance.GetInt();
        if (chance <= 0 || IRandom.Instance.Next(1, 101) > chance) return;

        foreach (PlayerControl seer in Main.EnumeratePlayerControls())
            seer.KillFlash();
    }
}
