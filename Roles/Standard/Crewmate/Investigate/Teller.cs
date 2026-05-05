using System;
using System.Collections.Generic;
using UnityEngine;
using static EndKnot.Translator;

namespace EndKnot.Roles;

public class Teller : RoleBase
{
    private const int Id = 703000;
    public static List<byte> PlayerIdList = [];

    private static OptionItem OptionMaxUses;
    private static OptionItem OptionMeetingMaxUses;
    private static OptionItem OptionVoteMode;
    private static OptionItem OptionShowRole;
    private static OptionItem OptionShowRoleName;
    private static OptionItem OptionTaskAwakening;
    private static OptionItem OptionAwakeningTaskCount;

    private byte TellerId;
    private int usecount;
    private int meetingUseCount;
    private bool IsSelecting;
    private bool awakened;
    private Dictionary<byte, CustomRoles> Divination = [];

    private enum VoteMode { Normal, SelfVote }

    public override bool IsEnable => PlayerIdList.Count > 0;

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.CrewmateRoles, CustomRoles.Teller);

        OptionMaxUses = new IntegerOptionItem(Id + 10, "TellerMaxUses", new(1, 99, 1), 3, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Teller])
            .SetValueFormat(OptionFormat.Times);

        OptionMeetingMaxUses = new IntegerOptionItem(Id + 11, "TellerMeetingMaxUses", new(0, 99, 1), 1, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Teller])
            .SetValueFormat(OptionFormat.Times);

        OptionVoteMode = new StringOptionItem(Id + 12, "TellerVoteMode", ["Normal", "Self Vote"], 1, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Teller]);

        OptionShowRoleName = new BooleanOptionItem(Id + 13, "TellerShowRoleName", true, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Teller]);

        OptionShowRole = new BooleanOptionItem(Id + 14, "TellerShowRole", true, TabGroup.CrewmateRoles)
            .SetParent(OptionShowRoleName);

        OptionTaskAwakening = new BooleanOptionItem(Id + 15, "TellerTaskAwakening", false, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Teller]);

        OptionAwakeningTaskCount = new IntegerOptionItem(Id + 16, "TellerAwakeningTaskCount", new(1, 99, 1), 5, TabGroup.CrewmateRoles)
            .SetParent(OptionTaskAwakening);
    }

    public override void Init()
    {
        PlayerIdList = [];
    }

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);
        TellerId = playerId;
        usecount = OptionMaxUses.GetInt();
        meetingUseCount = 0;
        IsSelecting = false;
        awakened = !OptionTaskAwakening.GetBool();
        Divination = [];
    }

    public override void Remove(byte playerId)
    {
        PlayerIdList.Remove(playerId);
    }

    private bool CanUseAbility()
    {
        if (!awakened) return false;
        if (usecount <= 0) return false;
        int perMax = OptionMeetingMaxUses.GetInt();
        if (perMax > 0 && meetingUseCount >= perMax) return false;
        return true;
    }

    public override void OnReportDeadBody()
    {
        meetingUseCount = 0;
        IsSelecting = false;
    }

    public override bool OnVote(PlayerControl voter, PlayerControl target)
    {
        if (voter.PlayerId != TellerId) return false;
        if (!voter.IsAlive()) return false;

        VoteMode mode = (VoteMode)OptionVoteMode.GetValue();

        if (mode == VoteMode.SelfVote)
        {
            if (!IsSelecting)
            {
                if (!CanUseAbility()) return false;
                if (target == null || target.PlayerId != TellerId) return false;
                IsSelecting = true;
                Utils.SendMessage(GetString("TellerEnterMode"), TellerId, importance: MessageImportance.High);
                return true;
            }

            IsSelecting = false;
            if (target == null)
            {
                Utils.SendMessage(GetString("TellerCancelMode"), TellerId, importance: MessageImportance.High);
                return false;
            }
            if (!target.IsAlive() || target.PlayerId == TellerId) return false;
            UseDivination(target.PlayerId);
            return true;
        }
        else
        {
            if (!CanUseAbility()) return false;
            if (target == null || target.PlayerId == TellerId || !target.IsAlive()) return false;
            UseDivination(target.PlayerId);
            return true;
        }
    }

    private void UseDivination(byte targetId)
    {
        usecount--;
        meetingUseCount++;

        PlayerControl target = Utils.GetPlayerById(targetId);
        if (target == null) return;

        CustomRoles role = target.GetCustomRole();
        Divination[targetId] = role;

        string roleText = OptionShowRole.GetBool()
            ? role.ToColoredString()
            : GetString(role.GetCustomRoleTypes().ToString());

        int remaining = usecount;
        string perMax = OptionMeetingMaxUses.GetInt() > 0
            ? string.Format(GetString("TellerRemainingMeeting"), Math.Min(OptionMeetingMaxUses.GetInt() - meetingUseCount, remaining))
            : string.Format(GetString("TellerRemaining"), remaining);

        string suffix = role.IsCrewmate() ? "!" : "...";
        Utils.SendMessage(
            string.Format(GetString("TellerResult"), targetId.ColoredPlayerName(), roleText) + GetString("TellerFin") + suffix + $"\n\n{perMax}",
            TellerId, GetString("TellerTitle"), importance: MessageImportance.High);
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
        if (playerId != TellerId) return string.Empty;
        return Utils.ColorString(awakened && usecount > 0 ? Utils.GetRoleColor(CustomRoles.Teller) : Color.gray, $"({usecount})");
    }

    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud = false, bool meeting = false)
    {
        if (seer == null || target == null) return string.Empty;
        if (seer.PlayerId != TellerId) return string.Empty;
        if (!OptionShowRoleName.GetBool()) return string.Empty;
        if (!Divination.TryGetValue(target.PlayerId, out CustomRoles role)) return string.Empty;

        return OptionShowRole.GetBool()
            ? role.ToColoredString()
            : Utils.ColorString(Color.white, GetString(role.GetCustomRoleTypes().ToString()));
    }
}
