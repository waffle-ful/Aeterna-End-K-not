namespace EndKnot.Roles;

internal class Opener : IAddon
{
    public AddonTypes Type => AddonTypes.Mixed;

    public void SetupCustomOption()
    {
        Options.SetupAdtRoleOptions(20240, CustomRoles.Opener, canSetNum: true, teamSpawnOptions: true);
    }
}
