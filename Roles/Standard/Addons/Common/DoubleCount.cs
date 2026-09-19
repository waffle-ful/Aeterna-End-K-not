using static EndKnot.Options;

namespace EndKnot.Roles;

internal class DoubleCount : IAddon
{
    public AddonTypes Type => AddonTypes.Mixed;

    public void SetupCustomOption()
    {
        SetupAdtRoleOptions(14700, CustomRoles.DoubleCount, canSetNum: true, teamSpawnOptions: true);

        DualVotes = new BooleanOptionItem(14712, "DualVotes", true, TabGroup.Addons)
            .SetParent(CustomRoleSpawnChances[CustomRoles.DoubleCount]);
    }
}