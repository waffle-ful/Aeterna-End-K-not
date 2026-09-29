using AmongUs.GameOptions;
using EndKnot.Modules;

namespace EndKnot.Roles;

// A Crewmate ghost (optionally a Madmate => Impostor team) that sends picture cards to a living player.
// The Crewmate version sends the danger pair only when a killer is within the radius of the target,
// and the check card otherwise. The Madmate version always sends the danger pair.
// The protect press carries no vanilla shield; only the cards are sent.
internal class Forewarner : IGhostRole
{
    private static OptionItem CD;
    private static OptionItem UseLimit;
    private static OptionItem Radius;
    private static OptionItem AssignMadmate;

    private int UsesLeft;

    public Team Team => (AssignMadmate?.GetBool() ?? false) ? Team.Impostor : Team.Crewmate;
    public RoleTypes RoleTypes => RoleTypes.GuardianAngel;
    public int Cooldown => CD.GetInt();

    public bool OnProtect(PlayerControl pc, PlayerControl target)
    {
        if (UsesLeft <= 0)
        {
            pc.Notify(Translator.GetString("ForewarnerNoUses"));
            return false;
        }

        if (target == null || !target.IsAlive()) return false;

        bool danger = Team == Team.Impostor || KillerNear(target);

        bool sent = danger
            ? SpiritGuideCards.Send(target, SpiritGuideCards.PirateFlag, SpiritGuideCards.Warning)
            : SpiritGuideCards.Send(target, SpiritGuideCards.CheckQuestion);

        if (!sent) return false;

        UsesLeft--;
        // No vanilla protect goes out for this role, so start the ghost's own button cooldown the way a protect would.
        pc.RpcResetAbilityCooldown();
        pc.Notify(string.Format(Translator.GetString(danger ? "ForewarnerNotifyDanger" : "ForewarnerNotifySafe"), target.GetRealName(), UsesLeft));
        return true;
    }

    private static bool KillerNear(PlayerControl target)
    {
        float radius = Radius.GetFloat();
        var targetPos = target.Pos();

        foreach (PlayerControl other in Main.AllAlivePlayerControls)
        {
            if (other.PlayerId == target.PlayerId) continue;
            // Madmate-type roles count as the impostor team but have no kill button, so they are not a danger here.
            if (!other.Is(CustomRoleTypes.Impostor) && !other.IsNeutralKiller()) continue;
            if (!other.CanUseKillButton()) continue;
            if (FastVector2.DistanceWithinRange(other.Pos(), targetPos, radius)) return true;
        }

        return false;
    }

    public void OnAssign(PlayerControl pc)
    {
        UsesLeft = UseLimit.GetInt();
    }

    public void SetupCustomOption()
    {
        Options.SetupRoleOptions(707800, TabGroup.OtherRoles, CustomRoles.Forewarner);

        CD = new IntegerOptionItem(707802, "AbilityCooldown", new(0, 180, 1), 25, TabGroup.OtherRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Forewarner])
            .SetValueFormat(OptionFormat.Seconds);

        UseLimit = new IntegerOptionItem(707803, "AbilityUseLimit", new(1, 30, 1), 3, TabGroup.OtherRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Forewarner]);

        Radius = new FloatOptionItem(707804, "ForewarnerRadius", new(1f, 10f, 0.5f), 3f, TabGroup.OtherRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Forewarner])
            .SetValueFormat(OptionFormat.Multiplier);

        AssignMadmate = new BooleanOptionItem(707805, "ForewarnerAssignMadmate", false, TabGroup.OtherRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Forewarner]);
    }
}
