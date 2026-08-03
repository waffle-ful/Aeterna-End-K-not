namespace EndKnot.Roles;

public class Focused : IAddon
{
    public AddonTypes Type => AddonTypes.Mixed;

    public void SetupCustomOption()
    {
        Options.SetupAdtRoleOptions(706200, CustomRoles.Focused, canSetNum: true, teamSpawnOptions: true);
    }
}
