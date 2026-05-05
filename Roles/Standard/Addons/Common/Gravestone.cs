using static EndKnot.Options;

namespace EndKnot.Roles;

internal class Gravestone : IAddon
{
    public AddonTypes Type => AddonTypes.Helpful;

    public void SetupCustomOption()
    {
        SetupAdtRoleOptions(14000, CustomRoles.Gravestone, canSetNum: true, teamSpawnOptions: true);
    }
}