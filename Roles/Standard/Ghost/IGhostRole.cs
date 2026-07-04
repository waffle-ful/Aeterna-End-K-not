using AmongUs.GameOptions;

namespace EndKnot.Roles;

public interface IGhostRole
{
    public Team Team { get; }
    public RoleTypes RoleTypes { get; }
    public int Cooldown { get; }
    // Returns true iff the ability actually fired (a real effect was applied). Return false from an
    // internal validation guard (charges exhausted, on-a-jam, invalid target, already-protected, etc.)
    // so the caller knows nothing happened.
    public bool OnProtect(PlayerControl pc, PlayerControl target);
    public void OnAssign(PlayerControl pc);
    public void SetupCustomOption();
}