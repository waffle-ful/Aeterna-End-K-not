namespace EndKnot.Roles;

internal class Connecting : IAddon
{
    public AddonTypes Type => AddonTypes.Helpful;

    public void SetupCustomOption()
    {
        Options.SetupAdtRoleOptions(20020, CustomRoles.Connecting, canSetNum: true, teamSpawnOptions: true);
    }
}
