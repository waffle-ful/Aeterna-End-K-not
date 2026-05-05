using System;
using System.Collections.Generic;
using UnityEngine;
using static EndKnot.Translator;

namespace EndKnot.Roles;

public class PonkotuTeller : RoleBase
{
    private const int Id = 703100;
    public static List<byte> PlayerIdList = [];

    private static OptionItem OptionSuccessRate;
    private static OptionItem OptionMaxUses;
    private static OptionItem OptionMeetingMaxUses;
    private static OptionItem OptionVoteMode;
    private static OptionItem OptionShowRole;
    private static OptionItem OptionShowRoleName;
    private static OptionItem OptionDontChangeResult;
    private static OptionItem OptionTaskAwakening;
    private static OptionItem OptionAwakeningTaskCount;

    private byte PonkotuTellerId;
    private int usecount;
    private int meetingUseCount;
    private bool IsSelecting;
    private bool awakened;
    private Dictionary<byte, CustomRoles> Divination = [];
    private Dictionary<byte, CustomRoles> GameTell = [];

    private enum VoteMode { Normal, SelfVote }

    public override bool IsEnable => PlayerIdList.Count > 0;

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.CrewmateRoles, CustomRoles.PonkotuTeller);

        OptionSuccessRate = new IntegerOptionItem(Id + 10, "PonkotuTellerSuccessRate", new(0, 100, 5), 70, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.PonkotuTeller])
            .SetValueFormat(OptionFormat.Percent);

        OptionMaxUses = new IntegerOptionItem(Id + 11, "PonkotuTellerMaxUses", new(1, 99, 1), 3, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.PonkotuTeller])
            .SetValueFormat(OptionFormat.Times);

        OptionMeetingMaxUses = new IntegerOptionItem(Id + 12, "PonkotuTellerMeetingMaxUses", new(0, 99, 1), 1, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.PonkotuTeller])
            .SetValueFormat(OptionFormat.Times);

        OptionVoteMode = new StringOptionItem(Id + 13, "PonkotuTellerVoteMode", ["Normal", "Self Vote"], 1, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.PonkotuTeller]);

        OptionShowRoleName = new BooleanOptionItem(Id + 14, "PonkotuTellerShowRoleName", true, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.PonkotuTeller]);

        OptionShowRole = new BooleanOptionItem(Id + 15, "PonkotuTellerShowRole", true, TabGroup.CrewmateRoles)
            .SetParent(OptionShowRoleName);

        OptionDontChangeResult = new BooleanOptionItem(Id + 16, "PonkotuTellerDontChangeResult", false, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.PonkotuTeller]);

        OptionTaskAwakening = new BooleanOptionItem(Id + 17, "PonkotuTellerTaskAwakening", false, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.PonkotuTeller]);

        OptionAwakeningTaskCount = new IntegerOptionItem(Id + 18, "PonkotuTellerAwakeningTaskCount", new(1, 99, 1), 5, TabGroup.CrewmateRoles)
            .SetParent(OptionTaskAwakening);
    }

    public override void Init()
    {
        PlayerIdList = [];
    }

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);
        PonkotuTellerId = playerId;
        usecount = OptionMaxUses.GetInt();
        meetingUseCount = 0;
        IsSelecting = false;
        awakened = !OptionTaskAwakening.GetBool();
        Divination = [];
        GameTell = [];
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
        if (voter.PlayerId != PonkotuTellerId) return false;
        if (!voter.IsAlive()) return false;

        VoteMode mode = (VoteMode)OptionVoteMode.GetValue();

        if (mode == VoteMode.SelfVote)
        {
            if (!IsSelecting)
            {
                if (!CanUseAbility()) return false;
                if (target == null || target.PlayerId != PonkotuTellerId) return false;
                IsSelecting = true;
                Utils.SendMessage(GetString("PonkotuTellerEnterMode"), PonkotuTellerId, importance: MessageImportance.High);
                return true;
            }

            IsSelecting = false;
            if (target == null)
            {
                Utils.SendMessage(GetString("PonkotuTellerCancelMode"), PonkotuTellerId, importance: MessageImportance.High);
                return false;
            }
            if (!target.IsAlive() || target.PlayerId == PonkotuTellerId) return false;
            UseDivination(target.PlayerId);
            return true;
        }
        else
        {
            if (!CanUseAbility()) return false;
            if (target == null || target.PlayerId == PonkotuTellerId || !target.IsAlive()) return false;
            UseDivination(target.PlayerId);
            return true;
        }
    }

    private void UseDivination(byte targetId)
    {
        PlayerControl target = Utils.GetPlayerById(targetId);
        if (target == null) return;

        // If DontChangeResult is on and we already divined this target, show cached result
        if (OptionDontChangeResult.GetBool() && GameTell.TryGetValue(targetId, out CustomRoles cachedRole))
        {
            SendDivinationMessage(targetId, cachedRole, uncertain: true);
            return;
        }

        usecount--;
        meetingUseCount++;

        CustomRoles role = target.GetCustomRole();
        GameTell.TryAdd(targetId, role);
        Divination[targetId] = role;

        int successRate = OptionSuccessRate.GetInt();
        bool success = successRate > 0 && IRandom.Instance.Next(0, 100) < successRate;
        SendDivinationMessage(targetId, role, uncertain: !success);
    }

    private void SendDivinationMessage(byte targetId, CustomRoles role, bool uncertain)
    {
        string roleText = OptionShowRole.GetBool()
            ? role.ToColoredString()
            : GetString(role.GetCustomRoleTypes().ToString());

        int remaining = usecount;
        string remainStr = OptionMeetingMaxUses.GetInt() > 0
            ? string.Format(GetString("TellerRemainingMeeting"), Math.Min(OptionMeetingMaxUses.GetInt() - meetingUseCount, remaining))
            : string.Format(GetString("TellerRemaining"), remaining);

        string suffix = uncertain ? "..?" : (role.IsCrewmate() ? "!" : "...");
        Utils.SendMessage(
            string.Format(GetString("TellerResult"), targetId.ColoredPlayerName(), roleText) + GetString("TellerFin") + suffix + $"\n\n{remainStr}",
            PonkotuTellerId, GetString("PonkotuTellerTitle"), importance: MessageImportance.High);
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
        if (playerId != PonkotuTellerId) return string.Empty;
        return Utils.ColorString(awakened && usecount > 0 ? Utils.GetRoleColor(CustomRoles.PonkotuTeller) : Color.gray, $"({usecount})");
    }

    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud = false, bool meeting = false)
    {
        if (seer == null || target == null) return string.Empty;
        if (seer.PlayerId != PonkotuTellerId) return string.Empty;
        if (!OptionShowRoleName.GetBool()) return string.Empty;
        if (!Divination.TryGetValue(target.PlayerId, out CustomRoles role)) return string.Empty;

        return OptionShowRole.GetBool()
            ? role.ToColoredString()
            : Utils.ColorString(Color.white, GetString(role.GetCustomRoleTypes().ToString()));
    }
}
