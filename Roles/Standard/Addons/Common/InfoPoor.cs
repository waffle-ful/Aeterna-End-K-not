namespace EndKnot.Roles;

internal class InfoPoor : IAddon
{
    public AddonTypes Type => AddonTypes.Harmful;

    public void SetupCustomOption()
    {
        Options.SetupAdtRoleOptions(20080, CustomRoles.InfoPoor, canSetNum: true, teamSpawnOptions: true);
    }
}
