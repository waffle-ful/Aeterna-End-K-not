using static EndKnot.Options;

namespace EndKnot.Roles;

internal class Seer : IAddon
{
    public AddonTypes Type => AddonTypes.Helpful;

    public void SetupCustomOption()
    {
        SetupAdtRoleOptions(14800, CustomRoles.Seer, canSetNum: true, teamSpawnOptions: true);
    }
}