using System.Collections.Generic;
using AmongUs.GameOptions;
using EndKnot.Modules;

namespace EndKnot.Roles;

// Ported from TownOfHost-K/Roles/Ghost/Role/AsistingAngel.cs.
// A PARASITIC Neutral ghost (assigned on death to a dead Crewmate OR Neutral). Single instance.
// Multi-mode GuardianAngel protect button:
//   1st press  -> BIND to a living "assist target".
//   later press on the bound target       -> grant it a timed kill-shield (GA-style).
//   later press on any other living player -> draw a directional arrow from the bound target toward them.
// Escalating cooldown (+AddCD per press) and a per-meeting deadline to bind (else locked out, CD 255).
// WINS iff its bound target wins — wired externally in CheckGameEndPatch (Lawyer-style parasitic win).
internal class AsistingAngel : IGhostRole
{
    private static OptionItem FirstCD;
    private static OptionItem AddCD;
    private static OptionItem GuardTime;
    private static OptionItem LimitDay;

    public byte AngelId = byte.MaxValue;
    public byte BoundTarget = byte.MaxValue;
    public byte ArrowTarget = byte.MaxValue;
    public int UseCount;
    public bool Guard;
    private int AssignedMeetingNum;

    public int DaysElapsed => MeetingStates.MeetingNum - AssignedMeetingNum;

    public Team Team => Team.Crewmate | Team.Neutral;
    public RoleTypes RoleTypes => RoleTypes.GuardianAngel;

    // Dynamic cooldown (Bloodmoon pattern): before binding = base (or 255 once the deadline passes);
    // after binding = base + AddCD per press.
    public int Cooldown => BoundTarget == byte.MaxValue
        ? (DaysElapsed > LimitDay.GetInt() ? 255 : FirstCD.GetInt())
        : FirstCD.GetInt() + (AddCD.GetInt() * UseCount);

    public void OnProtect(PlayerControl pc, PlayerControl target)
    {
        if (pc == null || target == null) return;

        if (BoundTarget == byte.MaxValue)
        {
            // 1st press: bind to the target (unless the deadline to bind has already passed).
            if (DaysElapsed > LimitDay.GetInt()) return;

            BoundTarget = target.PlayerId;
            pc.AddAbilityCD(Cooldown);
            Utils.NotifyRoles(SpecifySeer: pc);
            Utils.NotifyRoles(SpecifyTarget: target);
            return;
        }

        // Already bound: every later press escalates the cooldown.
        UseCount++;

        PlayerControl bound = Utils.GetPlayerById(BoundTarget);
        if (bound == null || !bound.IsAlive())
        {
            pc.AddAbilityCD(Cooldown);
            return;
        }

        if (target.PlayerId == BoundTarget)
        {
            // Shield the bound target: a timed kill-immunity window (consumed in OnCheckMurder).
            Guard = true;
            LateTask.New(() => Guard = false, GuardTime.GetInt(), "AsistingAngel Guard");
        }
        else
        {
            // Point the bound target (and the angel) toward this other player with a live-following arrow.
            RemoveArrows();
            ArrowTarget = target.PlayerId;
            TargetArrow.Add(BoundTarget, ArrowTarget);
            TargetArrow.Add(AngelId, ArrowTarget);
        }

        pc.AddAbilityCD(Cooldown);
        Utils.NotifyRoles();
    }

    public void OnAssign(PlayerControl pc)
    {
        AngelId = pc.PlayerId;
        BoundTarget = byte.MaxValue;
        ArrowTarget = byte.MaxValue;
        UseCount = 0;
        Guard = false;
        AssignedMeetingNum = MeetingStates.MeetingNum;
    }

    private void RemoveArrows()
    {
        if (ArrowTarget == byte.MaxValue) return;
        TargetArrow.Remove(BoundTarget, ArrowTarget);
        TargetArrow.Remove(AngelId, ArrowTarget);
    }

    public void SetupCustomOption()
    {
        Options.SetupRoleOptions(659000, TabGroup.OtherRoles, CustomRoles.AsistingAngel);

        FirstCD = new IntegerOptionItem(659002, "AbilityCooldown", new(0, 180, 1), 25, TabGroup.OtherRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.AsistingAngel])
            .SetValueFormat(OptionFormat.Seconds);

        AddCD = new IntegerOptionItem(659003, "AsistingAngelAddCooldown", new(0, 60, 1), 5, TabGroup.OtherRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.AsistingAngel])
            .SetValueFormat(OptionFormat.Seconds);

        GuardTime = new IntegerOptionItem(659004, "AsistingAngelGuardDuration", new(1, 90, 1), 5, TabGroup.OtherRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.AsistingAngel])
            .SetValueFormat(OptionFormat.Seconds);

        LimitDay = new IntegerOptionItem(659005, "AsistingAngelDeadline", new(1, 30, 1), 3, TabGroup.OtherRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.AsistingAngel]);
    }

    // Consumed by OnCheckMurder: true when a kill on 'targetId' should be blocked by the angel's shield.
    public static bool IsShielding(byte targetId)
    {
        foreach ((CustomRoles _, IGhostRole instance) in GhostRolesManager.AssignedGhostRoles.Values)
            if (instance is AsistingAngel aa && aa.Guard && aa.BoundTarget == targetId)
                return true;

        return false;
    }

    // Self-view: the deadline counter (unbound) or the tracking arrow (bound), on the angel's/target's own head.
    public static string GetSuffix(PlayerControl seer)
    {
        if (seer == null || GameStates.IsMeeting) return string.Empty;

        foreach ((CustomRoles _, IGhostRole instance) in GhostRolesManager.AssignedGhostRoles.Values)
        {
            if (instance is not AsistingAngel aa) continue;

            if (aa.BoundTarget == byte.MaxValue)
            {
                if (seer.PlayerId == aa.AngelId)
                    return Utils.ColorString(Utils.GetRoleColor(CustomRoles.AsistingAngel), $" ({aa.DaysElapsed}/{LimitDay.GetInt()})");
                return string.Empty;
            }

            if ((seer.PlayerId == aa.AngelId || seer.PlayerId == aa.BoundTarget) && aa.ArrowTarget != byte.MaxValue)
                return Utils.ColorString(Utils.GetRoleColor(CustomRoles.AsistingAngel), TargetArrow.GetArrows(seer, aa.ArrowTarget));

            return string.Empty;
        }

        return string.Empty;
    }

    // Over-other-players mark: an "＠" over the angel's AND the bound target's heads, visible to the angel,
    // the bound target, and any dead spectator (full TOHK parity via the seer/seen GetMark path).
    public static string GetMark(PlayerControl seer, PlayerControl seen, bool forMeeting = false)
    {
        if (seer == null || seen == null) return string.Empty;

        foreach ((CustomRoles _, IGhostRole instance) in GhostRolesManager.AssignedGhostRoles.Values)
        {
            if (instance is not AsistingAngel aa || aa.BoundTarget == byte.MaxValue) continue;

            bool seenIsMarked = seen.PlayerId == aa.AngelId || seen.PlayerId == aa.BoundTarget;
            if (!seenIsMarked) continue;

            bool seerCanSee = seer.PlayerId == aa.AngelId || seer.PlayerId == aa.BoundTarget || !seer.IsAlive();
            if (seerCanSee) return Utils.ColorString(Utils.GetRoleColor(CustomRoles.AsistingAngel), "＠");
        }

        return string.Empty;
    }
}
