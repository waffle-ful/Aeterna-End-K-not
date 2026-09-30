using AmongUs.GameOptions;

namespace EndKnot.Roles;

// The vanilla Influencer (SpiritGuide) as a Crewmate ghost role.
// Its picture cards go straight from the ghost's client to the chosen player, so the host only hands out the role.
internal class Influencer : IGhostRole
{
    private static OptionItem CD;

    public static float CooldownSeconds => CD?.GetFloat() ?? 30f;

    public Team Team => Team.Crewmate;
    public RoleTypes RoleTypes => RoleTypes.SpiritGuide;
    public int Cooldown => (int)CooldownSeconds;

    // The Influencer has no protect button.
    public bool OnProtect(PlayerControl pc, PlayerControl target) => false;

    public void OnAssign(PlayerControl pc) { }

    public void SetupCustomOption()
    {
        Options.SetupRoleOptions(707900, TabGroup.OtherRoles, CustomRoles.Influencer);

        CD = new FloatOptionItem(707902, "InfluencerCooldown", new(5f, 120f, 5f), 30f, TabGroup.OtherRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Influencer])
            .SetValueFormat(OptionFormat.Seconds);
    }
}
