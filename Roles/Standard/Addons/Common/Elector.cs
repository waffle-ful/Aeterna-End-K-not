namespace EndKnot.Roles;

internal class Elector : IAddon
{
    public AddonTypes Type => AddonTypes.Harmful;

    public void SetupCustomOption()
    {
        Options.SetupAdtRoleOptions(20040, CustomRoles.Elector, canSetNum: true, teamSpawnOptions: true);
    }
}
