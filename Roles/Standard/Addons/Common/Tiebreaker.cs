using static EndKnot.Options;

namespace EndKnot.Roles;

internal class Tiebreaker : IAddon
{
    public AddonTypes Type => AddonTypes.Mixed;

    public void SetupCustomOption()
    {
        SetupAdtRoleOptions(14900, CustomRoles.Tiebreaker, canSetNum: true, teamSpawnOptions: true);
    }
}