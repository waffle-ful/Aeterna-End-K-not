using System.Collections.Generic;
using AmongUs.GameOptions;
using EndKnot.Modules;

namespace EndKnot.Roles;

// Crewmate ghost role (joins the Impostor team when AssignMadmate is enabled).
// Pressing the GuardianAngel protect button on ANY living player calls an
// emergency meeting from the grave (the target is ignored), unless a critical
// sabotage is currently active or the ghost is out of charges. Remaining uses
// are shown above the ghost's own head via GetSuffix.
// Ported from TownOfHost-K/Roles/Ghost/Role/Ghostbuttoner.cs.
internal class Ghostbuttoner : IGhostRole
{
    private static readonly Dictionary<byte, int> Counts = [];

    private static OptionItem CD;
    private static OptionItem MaxCount;
    private static OptionItem AssignMadmate;

    public Team Team => (AssignMadmate?.GetBool() ?? false) ? Team.Impostor : Team.Crewmate;
    public RoleTypes RoleTypes => RoleTypes.GuardianAngel;
    public int Cooldown => CD.GetInt();

    public void OnProtect(PlayerControl pc, PlayerControl target)
    {
        // The target is irrelevant — any living player works as a "button".
        // Block while a critical sabotage is running (mirrors the TOHK check list).
        if (Utils.IsActive(SystemTypes.Reactor)
            || Utils.IsActive(SystemTypes.Electrical)
            || Utils.IsActive(SystemTypes.Laboratory)
            || Utils.IsActive(SystemTypes.Comms)
            || Utils.IsActive(SystemTypes.LifeSupp)
            || Utils.IsActive(SystemTypes.HeliSabotage))
            return;

        if (!Counts.ContainsKey(pc.PlayerId)) Counts[pc.PlayerId] = MaxCount.GetInt();
        if (Counts[pc.PlayerId] <= 0) return;

        Counts[pc.PlayerId]--;
        Utils.NotifyRoles(SpecifySeer: pc);

        // Defer the meeting one tick so the vanilla protect flow finishes first.
        // Capture only the byte id (never an Il2Cpp object) and re-fetch inside.
        byte id = pc.PlayerId;
        LateTask.New(() =>
        {
            PlayerControl p = Utils.GetPlayerById(id);
            if (p == null) return;
            p.NoCheckStartMeeting(null, force: true);
        }, 0.2f, "Ghostbuttoner Emergency Meeting");
    }

    public void OnAssign(PlayerControl pc)
    {
        Counts.Remove(pc.PlayerId);
    }

    public void SetupCustomOption()
    {
        Options.SetupRoleOptions(650800, TabGroup.OtherRoles, CustomRoles.Ghostbuttoner);

        CD = new IntegerOptionItem(650802, "AbilityCooldown", new(0, 180, 1), 25, TabGroup.OtherRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Ghostbuttoner])
            .SetValueFormat(OptionFormat.Seconds);

        MaxCount = new IntegerOptionItem(650803, "GhostbuttonerCount", new(1, 9, 1), 1, TabGroup.OtherRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Ghostbuttoner])
            .SetValueFormat(OptionFormat.Times);

        AssignMadmate = new BooleanOptionItem(650804, "GhostbuttonerAssignMadmate", false, TabGroup.OtherRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Ghostbuttoner]);
    }

    public static string GetSuffix(PlayerControl seer)
    {
        if (seer == null) return string.Empty;
        if (!GhostRolesManager.AssignedGhostRoles.TryGetValue(seer.PlayerId, out (CustomRoles Role, IGhostRole Instance) gr) || gr.Role != CustomRoles.Ghostbuttoner) return string.Empty;

        int count = Counts.GetValueOrDefault(seer.PlayerId, MaxCount.GetInt());
        return Utils.ColorString(Utils.GetRoleColor(CustomRoles.Ghostbuttoner), $" ({count}/{MaxCount.GetInt()})");
    }
}
