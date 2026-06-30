using AmongUs.GameOptions;
using EndKnot.Modules;

namespace EndKnot.Roles;

// Impostor-team "Mad Ghost". Fully passive: no Guardian-Angel protect button, no target, no cooldown.
// On death it is assigned RoleTypes.ImpostorGhost (GhostRolesManager already calls
// pc.RpcSetRoleDesync(instance.RoleTypes) on assignment), which natively grants the dead player the
// impostor SABOTAGE button. The mechanic is wired through external patches (HudPatch map mode +
// dead-branch button, SabotageSystemType.CheckSabotage) that key off IsAssigned().
// Ported from TownOfHost-K/Roles/Ghost/Role/DemonicSupporter.cs.
internal class DemonicSupporter : IGhostRole
{
    public Team Team => Team.Impostor;
    public RoleTypes RoleTypes => RoleTypes.ImpostorGhost;
    public int Cooldown => 0;

    // Passive role: there is no protect button, so nothing fires here.
    public void OnProtect(PlayerControl pc, PlayerControl target) { }

    // GhostRolesManager.AssignGhostRole already does RpcSetRoleDesync(RoleTypes == ImpostorGhost),
    // which gives the dead Madmate the vanilla impostor-ghost sabotage UI. Nothing extra to do.
    public void OnAssign(PlayerControl pc) { }

    public void SetupCustomOption()
    {
        Options.SetupRoleOptions(650900, TabGroup.OtherRoles, CustomRoles.DemonicSupporter);
    }

    // True when the given player is currently an assigned DemonicSupporter ghost. Used by the external
    // sabotage wiring: HudPatch button visibility / sabotage-map mode, and the host-side CheckSabotage gate.
    public static bool IsAssigned(byte id) =>
        GhostRolesManager.AssignedGhostRoles.TryGetValue(id, out (CustomRoles Role, IGhostRole Instance) gr) && gr.Role == CustomRoles.DemonicSupporter;
}
