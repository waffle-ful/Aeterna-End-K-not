using System.Collections.Generic;
using AmongUs.GameOptions;
using EndKnot.Modules;

namespace EndKnot.Roles;

internal class GhostReseter : IGhostRole
{
    private static readonly Dictionary<byte, int> Counts = [];

    private static OptionItem CD;
    private static OptionItem ResetAbilityCool;
    private static OptionItem MaxCount;
    private static OptionItem AssignMadmate;

    public Team Team => (AssignMadmate?.GetBool() ?? false) ? Team.Impostor : Team.Crewmate;
    public RoleTypes RoleTypes => RoleTypes.GuardianAngel;
    public int Cooldown => CD.GetInt();

    public void OnProtect(PlayerControl pc, PlayerControl target)
    {
        if (!Counts.ContainsKey(pc.PlayerId)) Counts[pc.PlayerId] = MaxCount.GetInt();
        if (Counts[pc.PlayerId] <= 0) return;

        Counts[pc.PlayerId]--;

        // Set the target's kill cooldown back to full, stalling killers
        target.SetKillCooldown();
        if (ResetAbilityCool.GetBool()) target.RpcResetAbilityCooldown();

        Utils.NotifyRoles(SpecifySeer: pc);
        pc.AddAbilityCD(Cooldown);
    }

    public void OnAssign(PlayerControl pc)
    {
        Counts.Remove(pc.PlayerId);
    }

    public void SetupCustomOption()
    {
        Options.SetupRoleOptions(650400, TabGroup.OtherRoles, CustomRoles.GhostReseter);

        CD = new IntegerOptionItem(650402, "AbilityCooldown", new(0, 180, 1), 25, TabGroup.OtherRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.GhostReseter])
            .SetValueFormat(OptionFormat.Seconds);

        ResetAbilityCool = new BooleanOptionItem(650403, "GhostReseterResetAbilityCool", true, TabGroup.OtherRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.GhostReseter]);

        MaxCount = new IntegerOptionItem(650404, "GhostReseterCount", new(1, 99, 1), 2, TabGroup.OtherRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.GhostReseter])
            .SetValueFormat(OptionFormat.Times);

        AssignMadmate = new BooleanOptionItem(650405, "GhostReseterAssignMadmate", false, TabGroup.OtherRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.GhostReseter]);
    }

    public static string GetSuffix(PlayerControl seer)
    {
        if (seer == null) return string.Empty;
        if (!GhostRolesManager.AssignedGhostRoles.TryGetValue(seer.PlayerId, out (CustomRoles Role, IGhostRole Instance) gr) || gr.Role != CustomRoles.GhostReseter) return string.Empty;

        int count = Counts.GetValueOrDefault(seer.PlayerId, MaxCount.GetInt());
        return Utils.ColorString(Utils.GetRoleColor(CustomRoles.GhostReseter), $" ({count}/{MaxCount.GetInt()})");
    }
}
