namespace EndKnot.Roles;

internal class Water : IAddon
{
    public AddonTypes Type => AddonTypes.Harmful;

    public void SetupCustomOption()
    {
        Options.SetupAdtRoleOptions(20360, CustomRoles.Water, canSetNum: true, teamSpawnOptions: true);
    }
}
