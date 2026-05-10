namespace EndKnot.Roles;

internal class Notvoter : IAddon
{
    public AddonTypes Type => AddonTypes.Harmful;

    public void SetupCustomOption()
    {
        Options.SetupAdtRoleOptions(20200, CustomRoles.Notvoter, canSetNum: true, teamSpawnOptions: true);
    }
}
