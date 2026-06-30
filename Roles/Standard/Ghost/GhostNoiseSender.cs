using System.Collections.Generic;
using AmongUs.GameOptions;
using EndKnot.Modules;

namespace EndKnot.Roles;

// Crewmate ghost. The protect button "bugs" a living player for a timed window.
// If a bugged player is later murdered (a kill that leaves a corpse), the victim is
// desync-converted to a vanilla Noisemaker on every client right before the kill, so the
// vanilla Noisemaker death-ping reveals the kill location to everyone.
// Templated on MeetingAngel.cs (mark + external static method) and Shade.cs (per-instance marked set).
internal class GhostNoiseSender : IGhostRole
{
    private static OptionItem CD;
    private static OptionItem Duration;

    // Per-instance set of currently-bugged target PlayerIds (a managed object, safe to capture in closures).
    public HashSet<byte> MarkedTargets = [];

    public Team Team => Team.Crewmate;
    public RoleTypes RoleTypes => RoleTypes.GuardianAngel;
    public int Cooldown => CD.GetInt();

    public void OnProtect(PlayerControl pc, PlayerControl target)
    {
        if (pc == null || target == null) return;

        byte targetId = target.PlayerId;
        MarkedTargets.Add(targetId);

        // Capture ONLY the value-type id (never an Il2Cpp PlayerControl); the HashSet is a managed object.
        LateTask.New(() => MarkedTargets.Remove(targetId), Duration.GetInt(), "GhostNoiseSender Unmark");

        pc.AddAbilityCD(Cooldown);
        pc.Notify(Translator.GetString("GhostNoiseSenderMarked"), 5f);
    }

    public void OnAssign(PlayerControl pc)
    {
        MarkedTargets = [];
    }

    public void SetupCustomOption()
    {
        Options.SetupRoleOptions(650600, TabGroup.OtherRoles, CustomRoles.GhostNoiseSender);

        CD = new IntegerOptionItem(650602, "AbilityCooldown", new(0, 180, 1), 25, TabGroup.OtherRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.GhostNoiseSender])
            .SetValueFormat(OptionFormat.Seconds);

        Duration = new IntegerOptionItem(650603, "GhostNoiseSenderTime", new(1, 180, 1), 7, TabGroup.OtherRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.GhostNoiseSender])
            .SetValueFormat(OptionFormat.Seconds);
    }

    // Called from CheckMurderPatch IMMEDIATELY BEFORE the kill RPC is sent (corpse-leaving murders only).
    // If the victim is currently bugged by any GhostNoiseSender, convert them to a vanilla Noisemaker on
    // every client so the vanilla Noisemaker death-ping fires for everyone.
    public static void OnTargetMurdered(PlayerControl target)
    {
        if (target == null || !AmongUsClient.Instance.AmHost) return;

        var bugged = false;
        foreach ((CustomRoles _, IGhostRole instance) in GhostRolesManager.AssignedGhostRoles.Values)
        {
            if (instance is GhostNoiseSender gns && gns.MarkedTargets.Contains(target.PlayerId))
            {
                bugged = true;
                break;
            }
        }

        if (!bugged) return;

        // The victim is about to die, so overriding their (now irrelevant) per-client RoleBehaviour view is
        // harmless. RpcSetRoleDesync auto-routes the host's own client through CoSetRole.
        foreach (PlayerControl seer in Main.AllPlayerControlsToArray)
        {
            if (seer == null || seer.OwnerId < 0) continue;
            target.RpcSetRoleDesync(RoleTypes.Noisemaker, seer.OwnerId);
        }
    }
}
