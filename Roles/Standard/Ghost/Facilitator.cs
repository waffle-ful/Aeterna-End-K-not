using AmongUs.GameOptions;

namespace EndKnot.Roles;

public class Facilitator : IGhostRole
{
    private static OptionItem CD;

    public Team Team => Team.Coven;
    public RoleTypes RoleTypes => RoleTypes.GuardianAngel;
    public int Cooldown => CD.GetInt();

    public bool OnProtect(PlayerControl pc, PlayerControl target)
    {
        if (!Main.PlayerStates.TryGetValue(target.PlayerId, out PlayerState state) || state.Role is not CovenBase covenRole) return false;

        covenRole.HasNecronomicon = true;
        covenRole.OnReceiveNecronomicon();
        return true;
    }

    public void OnAssign(PlayerControl pc) { }

    public void SetupCustomOption()
    {
        Options.SetupRoleOptions(654600, TabGroup.OtherRoles, CustomRoles.Facilitator);

        CD = new IntegerOptionItem(654602, "AbilityCooldown", new(0, 120, 1), 60, TabGroup.OtherRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Facilitator])
            .SetValueFormat(OptionFormat.Seconds);
    }
}