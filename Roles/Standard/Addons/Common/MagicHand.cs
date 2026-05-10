using static EndKnot.Options;

namespace EndKnot.Roles;

internal class MagicHand : IAddon
{
    public static OptionItem KillDistance;
    public AddonTypes Type => AddonTypes.Mixed;

    public void SetupCustomOption()
    {
        SetupAdtRoleOptions(20120, CustomRoles.MagicHand, canSetNum: true, teamSpawnOptions: true);

        KillDistance = new IntegerOptionItem(20130, "MagicHandKillDistance", new(0, 2, 1), 2, TabGroup.Addons)
            .SetParent(CustomRoleSpawnChances[CustomRoles.MagicHand]);
    }
}
