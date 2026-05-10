namespace EndKnot.Roles;

internal class Moon : IAddon
{
    public AddonTypes Type => AddonTypes.Helpful;

    public void SetupCustomOption()
    {
        Options.SetupAdtRoleOptions(20140, CustomRoles.Moon, canSetNum: true, teamSpawnOptions: true);
    }
}
