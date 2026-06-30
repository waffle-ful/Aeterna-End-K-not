using System.Collections.Generic;
using System.Text;
using AmongUs.GameOptions;
using EndKnot.Modules;

namespace EndKnot.Roles;

// Ported from TownOfHost-K/Roles/Ghost/Role/GhostRumour.cs
// A Crewmate ghost (optionally a Madmate => Impostor team) that "spreads a rumour":
// the GuardianAngel protect button QUEUES a living target with NO immediate effect.
// At the opening of the next meeting, that target's TRUE role is announced (colored,
// but anonymous — the name is not revealed, matching TOHK) to everyone in a SINGLE
// chat message. One use per game; refunded if the target dies before the meeting.
internal class GhostRumour : IGhostRole
{
    private static OptionItem CD;
    private static OptionItem AssignMadmate;

    private byte QueuedTarget = byte.MaxValue;
    private bool Used;

    public Team Team => (AssignMadmate?.GetBool() ?? false) ? Team.Impostor : Team.Crewmate;
    public RoleTypes RoleTypes => RoleTypes.GuardianAngel;
    public int Cooldown => CD.GetInt();

    public void OnProtect(PlayerControl pc, PlayerControl target)
    {
        if (Used || QueuedTarget != byte.MaxValue) return;
        if (target == null || !target.IsAlive()) return;

        QueuedTarget = target.PlayerId;
        pc.AddAbilityCD(Cooldown);
        Utils.NotifyRoles(SpecifySeer: pc);
    }

    public void OnAssign(PlayerControl pc)
    {
        QueuedTarget = byte.MaxValue;
        Used = false;
    }

    public void SetupCustomOption()
    {
        Options.SetupRoleOptions(650700, TabGroup.OtherRoles, CustomRoles.GhostRumour);

        CD = new IntegerOptionItem(650702, "AbilityCooldown", new(0, 180, 1), 25, TabGroup.OtherRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.GhostRumour])
            .SetValueFormat(OptionFormat.Seconds);

        AssignMadmate = new BooleanOptionItem(650703, "GhostRumourAssignMadmate", false, TabGroup.OtherRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.GhostRumour]);
    }

    // Called host-only from MeetingHudPatch at meeting open. ALL queued reveals are merged into
    // ONE Utils.SendMessage to avoid the title-burst Hacking kick. State is mutated synchronously
    // here; only the immutable result string is captured by the LateTask closure (no Il2Cpp objects).
    public static void SendReveals()
    {
        if (!AmongUsClient.Instance.AmHost) return;

        var body = new StringBuilder();
        List<byte> revealed = [];

        foreach ((CustomRoles _, IGhostRole instance) in GhostRolesManager.AssignedGhostRoles.Values)
        {
            if (instance is not GhostRumour rumour) continue;
            if (rumour.Used || rumour.QueuedTarget == byte.MaxValue) continue;

            PlayerControl target = Utils.GetPlayerById(rumour.QueuedTarget);

            // Target died before the meeting: skip and refund the one-shot.
            if (target == null || !target.IsAlive())
            {
                rumour.QueuedTarget = byte.MaxValue;
                continue;
            }

            rumour.Used = true;
            rumour.QueuedTarget = byte.MaxValue;

            if (revealed.Contains(target.PlayerId)) continue;
            revealed.Add(target.PlayerId);

            body.Append(string.Format(Translator.GetString("GhostRumourAbilityMsg"), target.GetCustomRole().ToColoredString()));
            body.Append('\n');
        }

        if (body.Length == 0) return;

        string message = body.ToString().TrimEnd('\n');
        LateTask.New(() => Utils.SendMessage(message, title: CustomRoles.GhostRumour.ToColoredString()), 9.5f, "GhostRumour Reveal");
    }
}
