namespace EndKnot.Roles;

internal interface IAddon
{
    public AddonTypes Type { get; }
    public void SetupCustomOption();
}