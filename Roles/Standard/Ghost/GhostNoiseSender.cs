using System.Collections.Generic;
using System.Linq;
using System.Text;
using AmongUs.GameOptions;
using EndKnot.Modules;
using UnityEngine;

namespace EndKnot.Roles;

// Crewmate ghost. The protect button "bugs" a living player for a timed window.
// If a bugged player is later murdered (a real kill that leaves a corpse), a loud noise plays for
// everyone and a directional arrow to the kill location is shown to every living player for a few
// seconds — reliably revealing where the kill happened. (We do NOT rely on the vanilla Noisemaker
// alert, which is an off-by-default, impostor-only option.)
internal class GhostNoiseSender : IGhostRole
{
    private static OptionItem CD;
    private static OptionItem Duration;
    private static OptionItem AlertDuration;

    // Per-instance set of currently-bugged target PlayerIds (a managed object, safe to capture in closures).
    public HashSet<byte> MarkedTargets = [];

    // Kill locations currently being revealed to everyone (shown via GetSuffix arrows). Static because
    // the reveal is global; each entry is removed by its own LateTask after AlertDuration.
    private static readonly List<Vector2> ActiveAlerts = [];

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

        AlertDuration = new IntegerOptionItem(650604, "GhostNoiseSenderAlertDuration", new(1, 60, 1), 10, TabGroup.OtherRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.GhostNoiseSender])
            .SetValueFormat(OptionFormat.Seconds);
    }

    // Called from CheckMurderPatch IMMEDIATELY BEFORE a confirmed, corpse-leaving kill (the real kill-button
    // path — note the host /kill command bypasses CheckMurder, so it won't trigger this). If the victim is
    // bugged, blast a noise to everyone and point every living player to the kill spot for AlertDuration secs.
    public static void OnTargetMurdered(PlayerControl target)
    {
        if (target == null || !AmongUsClient.Instance.AmHost) return;

        bool bugged = GhostRolesManager.AssignedGhostRoles.Values.Any(g => g.Instance is GhostNoiseSender gns && gns.MarkedTargets.Contains(target.PlayerId));
        if (!bugged) return;

        Vector2 pos = target.Pos();
        ActiveAlerts.Add(pos);

        CustomSoundsManager.RPCPlayCustomSoundAll("FlashBang");
        foreach (PlayerControl pc in Main.AllAlivePlayerControls)
            if (pc != null)
                LocateArrow.Add(pc.PlayerId, pos);

        Utils.NotifyRoles();

        LateTask.New(() =>
        {
            ActiveAlerts.Remove(pos);
            foreach (PlayerControl pc in Main.AllPlayerControlsToArray)
                if (pc != null)
                    LocateArrow.Remove(pc.PlayerId, pos);

            Utils.NotifyRoles();
        }, AlertDuration.GetInt(), "GhostNoiseSender Alert");
    }

    // Shows the kill-location arrow(s) to EVERY seer while an alert is active (not gated on the seer's role).
    public static string GetSuffix(PlayerControl seer)
    {
        if (seer == null || GameStates.IsMeeting || ActiveAlerts.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        foreach (Vector2 pos in ActiveAlerts)
            sb.Append(LocateArrow.GetArrow(seer, pos));

        return sb.ToString();
    }
}
