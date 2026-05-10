namespace EndKnot.Roles;

internal class Transparent : IAddon
{
    public AddonTypes Type => AddonTypes.Harmful;

    public void SetupCustomOption()
    {
        Options.SetupAdtRoleOptions(20320, CustomRoles.Transparent, canSetNum: true, teamSpawnOptions: true);
    }
}
