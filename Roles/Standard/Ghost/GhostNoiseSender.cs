using System.Collections.Generic;
using System.Linq;
using AmongUs.GameOptions;
using EndKnot.Modules;

namespace EndKnot.Roles;

// Crewmate ghost. The protect button "bugs" a living player for a timed window.
// If a bugged player is later murdered (a real kill that leaves a corpse), the victim is desync-converted
// to a vanilla Noisemaker on every client right before the kill, so the vanilla Noisemaker death-ping
// reveals the kill location to everyone — exactly the "Noisy" addon's mechanism (RoleTypes.Noisemaker +
// the death alert), but the alert is FORCED on for everyone via PlayerGameOptionsSender so it works
// regardless of the host's vanilla Noisemaker settings.
internal class GhostNoiseSender : IGhostRole
{
    private static OptionItem CD;
    private static OptionItem Duration;
    private static OptionItem AlertDuration;

    // Per-instance set of currently-bugged target PlayerIds (a managed object, safe to capture in closures).
    public HashSet<byte> MarkedTargets = [];

    public Team Team => Team.Crewmate;
    public RoleTypes RoleTypes => RoleTypes.GuardianAngel;
    public int Cooldown => CD.GetInt();

    // True while any GhostNoiseSender is assigned — read by PlayerGameOptionsSender to force the vanilla
    // Noisemaker death alert ON for every client, so the kill-time conversion actually pings for everyone.
    public static bool AnyAssigned => GhostRolesManager.AssignedGhostRoles.Values.Any(g => g.Role == CustomRoles.GhostNoiseSender);
    public static float AlertDurationValue => AlertDuration?.GetFloat() ?? 10f;

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
        // Push the forced Noisemaker-alert option (applied in PlayerGameOptionsSender) to every client now.
        Utils.MarkEveryoneDirtySettings();
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

        AlertDuration = new IntegerOptionItem(650604, "GhostNoiseSenderAlertDuration", new(1, 60, 1), 10, TabGroup.OtherRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.GhostNoiseSender])
            .SetValueFormat(OptionFormat.Seconds);
    }

    // Called from CheckMurderPatch IMMEDIATELY BEFORE a confirmed, corpse-leaving kill (the real kill-button
    // path — note the host /kill command bypasses CheckMurder, so it won't trigger this). If the victim is
    // bugged, desync-convert them to a vanilla Noisemaker on every client so the (forced-on) Noisemaker death
    // ping fires for everyone the instant they die.
    public static void OnTargetMurdered(PlayerControl target)
    {
        if (target == null || !AmongUsClient.Instance.AmHost) return;

        bool bugged = GhostRolesManager.AssignedGhostRoles.Values.Any(g => g.Instance is GhostNoiseSender gns && gns.MarkedTargets.Contains(target.PlayerId));
        if (!bugged) return;

        // The victim dies one RPC later, so overriding their (now irrelevant) per-client RoleBehaviour is harmless.
        foreach (PlayerControl seer in Main.AllPlayerControlsToArray)
        {
            if (seer == null || seer.OwnerId < 0) continue;
            target.RpcSetRoleDesync(RoleTypes.Noisemaker, seer.OwnerId);
        }
    }
}
