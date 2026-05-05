using System.Collections.Generic;
using UnityEngine;
using static EndKnot.Translator;

namespace EndKnot.Roles;

public class AmateurTeller : RoleBase
{
    private const int Id = 703200;
    public static List<byte> PlayerIdList = [];

    private static OptionItem OptionMaxUses;
    private static OptionItem OptionVoteMode;
    private static OptionItem OptionShowRole;
    private static OptionItem OptionTargetCanSeePlayer;
    private static OptionItem OptionTargetCanSeeArrow;
    private static OptionItem OptionCanUseButton;
    private static OptionItem OptionTaskAwakening;
    private static OptionItem OptionAwakeningTaskCount;

    private byte AmateurTellerId;
    private int usecount;
    private bool IsSelecting;
    private bool awakened;
    private byte UseTarget;
    private List<byte> PastTargets = [];

    private enum VoteMode { Normal, SelfVote }

    public override bool IsEnable => PlayerIdList.Count > 0;

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.CrewmateRoles, CustomRoles.AmateurTeller);

        OptionMaxUses = new IntegerOptionItem(Id + 10, "AmateurTellerMaxUses", new(1, 99, 1), 3, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.AmateurTeller])
            .SetValueFormat(OptionFormat.Times);

        OptionVoteMode = new StringOptionItem(Id + 11, "AmateurTellerVoteMode", ["Normal", "Self Vote"], 1, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.AmateurTeller]);

        OptionShowRole = new BooleanOptionItem(Id + 12, "AmateurTellerShowRole", true, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.AmateurTeller]);

        OptionTargetCanSeePlayer = new BooleanOptionItem(Id + 13, "AmateurTellerTargetCanSeePlayer", true, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.AmateurTeller]);

        OptionTargetCanSeeArrow = new BooleanOptionItem(Id + 14, "AmateurTellerTargetCanSeeArrow", true, TabGroup.CrewmateRoles)
            .SetParent(OptionTargetCanSeePlayer);

        OptionCanUseButton = new BooleanOptionItem(Id + 15, "AmateurTellerCanUseButton", true, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.AmateurTeller]);

        OptionTaskAwakening = new BooleanOptionItem(Id + 16, "AmateurTellerTaskAwakening", false, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.AmateurTeller]);

        OptionAwakeningTaskCount = new IntegerOptionItem(Id + 17, "AmateurTellerAwakeningTaskCount", new(1, 99, 1), 5, TabGroup.CrewmateRoles)
            .SetParent(OptionTaskAwakening);
    }

    public override void Init()
    {
        PlayerIdList = [];
    }

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);
        AmateurTellerId = playerId;
        usecount = OptionMaxUses.GetInt();
        IsSelecting = false;
        awakened = !OptionTaskAwakening.GetBool();
        UseTarget = byte.MaxValue;
        PastTargets = [];
    }

    public override void Remove(byte playerId)
    {
        PlayerIdList.Remove(playerId);
    }

    private bool CanUseAbility()
    {
        if (!awakened) return false;
        if (usecount <= 0) return false;
        if (UseTarget != byte.MaxValue) return false; // already watching someone
        return true;
    }

    public override void OnReportDeadBody()
    {
        if (UseTarget != byte.MaxValue)
        {
            TargetArrow.Remove(AmateurTellerId, UseTarget);
            if (OptionTargetCanSeeArrow.GetBool())
                TargetArrow.Remove(UseTarget, AmateurTellerId);
            PastTargets.Add(UseTarget);
            UseTarget = byte.MaxValue;
        }
        IsSelecting = false;
    }

    public override void AfterMeetingTasks()
    {
        // Arrows were removed in OnReportDeadBody; nothing extra needed here
    }

    public override bool CheckReportDeadBody(PlayerControl pc, NetworkedPlayerInfo target, PlayerControl killer)
    {
        if (pc.PlayerId != AmateurTellerId) return true;
        if (target != null) return true; // allow reporting dead bodies
        if (UseTarget == byte.MaxValue) return true; // no active watch target
        if (OptionCanUseButton.GetBool()) return true;
        return false; // block emergency button while watching a target
    }

    public override bool OnVote(PlayerControl voter, PlayerControl target)
    {
        if (voter.PlayerId != AmateurTellerId) return false;
        if (!voter.IsAlive()) return false;

        VoteMode mode = (VoteMode)OptionVoteMode.GetValue();

        if (mode == VoteMode.SelfVote)
        {
            if (!IsSelecting)
            {
                if (!CanUseAbility()) return false;
                if (target == null || target.PlayerId != AmateurTellerId) return false;
                IsSelecting = true;
                Utils.SendMessage(GetString("AmateurTellerEnterMode"), AmateurTellerId, importance: MessageImportance.High);
                return true;
            }

            IsSelecting = false;
            if (target == null)
            {
                Utils.SendMessage(GetString("AmateurTellerCancelMode"), AmateurTellerId, importance: MessageImportance.High);
                return false;
            }
            if (!target.IsAlive() || target.PlayerId == AmateurTellerId) return false;
            UseAbility(target.PlayerId);
            return true;
        }
        else
        {
            if (!CanUseAbility()) return false;
            if (target == null || target.PlayerId == AmateurTellerId || !target.IsAlive()) return false;
            UseAbility(target.PlayerId);
            return true;
        }
    }

    private void UseAbility(byte targetId)
    {
        usecount--;
        UseTarget = targetId;

        TargetArrow.Add(AmateurTellerId, targetId);
        if (OptionTargetCanSeeArrow.GetBool())
            TargetArrow.Add(targetId, AmateurTellerId);

        Utils.SendMessage(
            string.Format(GetString("AmateurTellerSetTarget"), targetId.ColoredPlayerName()),
            AmateurTellerId, importance: MessageImportance.High);
    }

    public override void OnTaskComplete(PlayerControl pc, int completedTaskCount, int totalTaskCount)
    {
        if (awakened) return;
        if (completedTaskCount + 1 >= OptionAwakeningTaskCount.GetInt())
        {
            awakened = true;
            Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc);
        }
    }

    public override string GetProgressText(byte playerId, bool comms)
    {
        if (playerId != AmateurTellerId) return string.Empty;
        return Utils.ColorString(awakened && usecount > 0 ? Utils.GetRoleColor(CustomRoles.AmateurTeller) : Color.gray, $"({usecount})");
    }

    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud = false, bool meeting = false)
    {
        if (meeting || seer == null || target == null) return string.Empty;

        byte seerId = seer.PlayerId;
        byte targetId = target.PlayerId;

        // AT's view: show ★ on their UseTarget
        if (seerId == AmateurTellerId && targetId == UseTarget)
            return $"<color=#6b3ec3>★</color>";

        // AT's view: show past target info as team/role
        if (seerId == AmateurTellerId && PastTargets.Contains(targetId))
        {
            PlayerControl targetPc = Utils.GetPlayerById(targetId);
            if (targetPc == null) return string.Empty;

            if (OptionShowRole.GetBool())
                return targetPc.GetCustomRole().ToColoredString();

            CustomRoleTypes team = targetPc.GetCustomRoleTypes();
            (string text, Color color) = team switch
            {
                CustomRoleTypes.Impostor => (GetString("Impostor"), Color.red),
                CustomRoleTypes.Neutral => (GetString("Neutral"), Color.gray),
                _ => (GetString("Crewmate"), Utils.GetRoleColor(CustomRoles.Crewmate))
            };
            return Utils.ColorString(color, text);
        }

        if (!OptionTargetCanSeePlayer.GetBool()) return string.Empty;

        // UseTarget's self-view: show ★ (only if target is non-crewmate and actively being watched)
        if (seerId == UseTarget && targetId == UseTarget && !seer.IsCrewmate())
        {
            string ar = OptionTargetCanSeeArrow.GetBool()
                ? $"\n{TargetArrow.GetArrows(seer, AmateurTellerId)}"
                : string.Empty;
            return $"<color=#6b3ec3>★{ar}</color>";
        }

        // UseTarget's view of AT: show ★ on AT's name
        if (seerId == UseTarget && targetId == AmateurTellerId)
            return $"<color=#6b3ec3>★</color>";

        return string.Empty;
    }
}
