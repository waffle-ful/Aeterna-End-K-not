using static EndKnot.Options;

namespace EndKnot.Roles;

internal class Serial : IAddon
{
    public static OptionItem KillCooldown;
    public AddonTypes Type => AddonTypes.ImpOnly;

    public void SetupCustomOption()
    {
        SetupAdtRoleOptions(20260, CustomRoles.Serial, canSetNum: true, teamSpawnOptions: true);

        // インポスター限定アドオンなので、陣営トグルのうちインポスター以外は常に効かない (CheckAddonConflict の ImpOnly ゲートが先に落とす)
        (_, OptionItem neutral, OptionItem crew, OptionItem coven) = AddonCanBeSettings[CustomRoles.Serial];
        neutral.SetHidden(true);
        crew.SetHidden(true);
        coven.SetHidden(true);

        KillCooldown = new FloatOptionItem(20270, "SerialKillCooldown", new(0f, 180f, 0.5f), 25f, TabGroup.Addons)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Serial])
            .SetValueFormat(OptionFormat.Seconds);
    }
}
