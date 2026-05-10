using static EndKnot.Options;

namespace EndKnot.Roles;

internal class Serial : IAddon
{
    public static OptionItem KillCooldown;
    public AddonTypes Type => AddonTypes.ImpOnly;

    public void SetupCustomOption()
    {
        SetupAdtRoleOptions(20260, CustomRoles.Serial, canSetNum: true, teamSpawnOptions: true);

        KillCooldown = new FloatOptionItem(20270, "SerialKillCooldown", new(0f, 180f, 0.5f), 15f, TabGroup.Addons)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Serial])
            .SetValueFormat(OptionFormat.Seconds);
    }
}
