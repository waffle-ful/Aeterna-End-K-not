using System.Collections.Generic;
using System.Linq;
using AmongUs.GameOptions;
using EndKnot.Modules;

namespace EndKnot.Roles;

internal class DemonicTracker : IGhostRole
{
    private static OptionItem CD;

    private byte MarkedTarget = byte.MaxValue;

    public Team Team => Team.Impostor;
    public RoleTypes RoleTypes => RoleTypes.GuardianAngel;
    public int Cooldown => CD.GetInt();

    public void OnProtect(PlayerControl pc, PlayerControl target)
    {
        PlayerControl[] impostors = Main.EnumerateAlivePlayerControls().Where(x => x.GetCustomRole().IsImpostor()).ToArray();

        if (MarkedTarget != byte.MaxValue)
            foreach (PlayerControl imp in impostors)
                TargetArrow.Remove(imp.PlayerId, MarkedTarget);

        MarkedTarget = target.PlayerId;

        foreach (PlayerControl imp in impostors)
            TargetArrow.Add(imp.PlayerId, target.PlayerId);

        pc.AddAbilityCD(Cooldown);
        Utils.NotifyRoles();
    }

    public void OnAssign(PlayerControl pc)
    {
        MarkedTarget = byte.MaxValue;
    }

    public void SetupCustomOption()
    {
        Options.SetupRoleOptions(650300, TabGroup.OtherRoles, CustomRoles.DemonicTracker);

        CD = new IntegerOptionItem(650302, "AbilityCooldown", new(0, 180, 1), 25, TabGroup.OtherRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.DemonicTracker])
            .SetValueFormat(OptionFormat.Seconds);
    }

    public static string GetSuffix(PlayerControl seer)
    {
        if (seer == null || GameStates.IsMeeting) return string.Empty;
        if (!seer.GetCustomRole().IsImpostor()) return string.Empty;

        List<byte> targets = [];
        foreach ((CustomRoles _, IGhostRole instance) in GhostRolesManager.AssignedGhostRoles.Values)
            if (instance is DemonicTracker tracker && tracker.MarkedTarget != byte.MaxValue)
                targets.Add(tracker.MarkedTarget);

        if (targets.Count == 0) return string.Empty;

        return Utils.ColorString(Utils.GetRoleColor(CustomRoles.DemonicTracker), TargetArrow.GetArrows(seer, targets.ToArray()));
    }
}
