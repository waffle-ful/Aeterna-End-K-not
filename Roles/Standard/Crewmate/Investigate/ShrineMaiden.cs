using System.Collections.Generic;
using UnityEngine;
using static EndKnot.Translator;

namespace EndKnot.Roles;

public class ShrineMaiden : RoleBase
{
    private const int Id = 702400;
    public static List<byte> PlayerIdList = [];

    private static OptionItem OptionMaxUses;
    private static OptionItem OptionVoteMode;
    private static OptionItem OptionTaskAwakening;
    private static OptionItem OptionAwakeningTaskCount;
    private static OptionItem OptionPerMeetingMax;

    public static NetworkedPlayerInfo CurrentReportTarget;

    private static Dictionary<byte, int> UseCount = [];
    private static Dictionary<byte, int> MeetingUseCount = [];
    private static Dictionary<byte, bool> IsReport = [];
    private static Dictionary<byte, byte> OnikuId = [];
    private static Dictionary<byte, bool> Awakened = [];
    private static Dictionary<byte, bool> IsSelecting = [];

    private byte ShrineMaidenId;

    private enum VoteMode { Normal, SelfVote }

    public override bool IsEnable => PlayerIdList.Count > 0;

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.CrewmateRoles, CustomRoles.ShrineMaiden);

        OptionMaxUses = new IntegerOptionItem(Id + 10, "ShrineMaidenMaxUses", new(1, 99, 1), 3, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.ShrineMaiden])
            .SetValueFormat(OptionFormat.Times);

        OptionVoteMode = new StringOptionItem(Id + 11, "ShrineMaidenVoteMode", ["Normal", "Self Vote"], 1, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.ShrineMaiden]);

        OptionTaskAwakening = new BooleanOptionItem(Id + 12, "ShrineMaidenTaskAwakening", false, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.ShrineMaiden]);

        OptionAwakeningTaskCount = new IntegerOptionItem(Id + 13, "ShrineMaidenAwakeningTaskCount", new(1, 99, 1), 5, TabGroup.CrewmateRoles)
            .SetParent(OptionTaskAwakening);

        OptionPerMeetingMax = new IntegerOptionItem(Id + 14, "ShrineMaidenPerMeetingMax", new(0, 99, 1), 0, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.ShrineMaiden])
            .SetValueFormat(OptionFormat.Times);
    }

    public override void Init()
    {
        PlayerIdList = [];
        UseCount = [];
        MeetingUseCount = [];
        IsReport = [];
        OnikuId = [];
        Awakened = [];
        IsSelecting = [];
        CurrentReportTarget = null;
    }

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);
        ShrineMaidenId = playerId;
        UseCount[playerId] = OptionMaxUses.GetInt();
        MeetingUseCount[playerId] = 0;
        IsReport[playerId] = false;
        OnikuId[playerId] = byte.MaxValue;
        IsSelecting[playerId] = false;

        bool requiresAwakening = OptionTaskAwakening.GetBool();
        Awakened[playerId] = !requiresAwakening;
    }

    public override void Remove(byte playerId)
    {
        PlayerIdList.Remove(playerId);
    }

    private bool CanUseAbility(byte id)
    {
        if (!Awakened.TryGetValue(id, out bool aw) || !aw) return false;
        if (!UseCount.TryGetValue(id, out int cnt) || cnt <= 0) return false;
        if (!IsReport.TryGetValue(id, out bool rep) || !rep) return false;
        int perMax = OptionPerMeetingMax.GetInt();
        if (perMax > 0 && MeetingUseCount.TryGetValue(id, out int mc) && mc >= perMax) return false;
        return true;
    }

    public override void OnTaskComplete(PlayerControl pc, int completedTaskCount, int totalTaskCount)
    {
        if (!Awakened.TryGetValue(pc.PlayerId, out bool aw) || aw) return;
        if (completedTaskCount + 1 >= OptionAwakeningTaskCount.GetInt())
        {
            Awakened[pc.PlayerId] = true;
            Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc);
        }
    }

    public override void OnReportDeadBody()
    {
        MeetingUseCount[ShrineMaidenId] = 0;
        IsSelecting[ShrineMaidenId] = false;

        if (CurrentReportTarget != null)
        {
            IsReport[ShrineMaidenId] = true;
            OnikuId[ShrineMaidenId] = CurrentReportTarget.PlayerId;
        }
        else
        {
            IsReport[ShrineMaidenId] = false;
            OnikuId[ShrineMaidenId] = byte.MaxValue;
        }
    }

    public override bool OnVote(PlayerControl voter, PlayerControl target)
    {
        if (voter.PlayerId != ShrineMaidenId) return false;
        if (!voter.IsAlive()) return false;
        if (!CanUseAbility(ShrineMaidenId)) return false;

        VoteMode mode = (VoteMode)OptionVoteMode.GetValue();

        if (mode == VoteMode.SelfVote)
        {
            bool selecting = IsSelecting.TryGetValue(ShrineMaidenId, out bool s) && s;
            if (!selecting)
            {
                if (target == null || target.PlayerId != ShrineMaidenId) return false;
                IsSelecting[ShrineMaidenId] = true;
                Utils.SendMessage(GetString("ShrineMaidenEnterMode"), ShrineMaidenId, importance: MessageImportance.High);
                return true;
            }

            IsSelecting[ShrineMaidenId] = false;
            if (target == null)
            {
                Utils.SendMessage(GetString("ShrineMaidenCancelMode"), ShrineMaidenId, importance: MessageImportance.High);
                return false;
            }
            if (!target.IsAlive() || target.PlayerId == ShrineMaidenId) return false;
            UseAbility(ShrineMaidenId, target.PlayerId);
            return true;
        }
        else
        {
            if (target == null || target.PlayerId == ShrineMaidenId || !target.IsAlive()) return false;
            UseAbility(ShrineMaidenId, target.PlayerId);
            return true;
        }
    }

    private void UseAbility(byte id, byte targetId)
    {
        UseCount[id]--;
        MeetingUseCount[id] = (MeetingUseCount.TryGetValue(id, out int mc) ? mc : 0) + 1;

        byte bodyId = OnikuId.TryGetValue(id, out byte oid) ? oid : byte.MaxValue;
        CustomRoleTypes bodyTeam = GetTeam(bodyId);
        CustomRoleTypes targetTeam = GetTeam(targetId);

        int remaining = UseCount.TryGetValue(id, out int cnt) ? cnt : 0;
        string bodyName = bodyId.ColoredPlayerName();
        string targetName = targetId.ColoredPlayerName();
        string remainStr = string.Format(GetString("ShrineMaidenRemaining"), remaining);

        string msg = bodyTeam == targetTeam
            ? string.Format(GetString("ShrineMaidenSameTeam"), bodyName, targetName) + "\n" + remainStr
            : string.Format(GetString("ShrineMaidenDifferentTeam"), bodyName, targetName) + "\n" + remainStr;

        Utils.SendMessage(msg, id, GetString("ShrineMaidenTitle"), importance: MessageImportance.High);
    }

    private static CustomRoleTypes GetTeam(byte playerId)
    {
        if (playerId == byte.MaxValue) return CustomRoleTypes.Crewmate;
        PlayerControl pc = Utils.GetPlayerById(playerId);
        if (pc != null && pc.Is(CustomRoles.Madmate)) return CustomRoleTypes.Impostor;
        if (!Main.PlayerStates.TryGetValue(playerId, out PlayerState st)) return CustomRoleTypes.Crewmate;
        return st.MainRole.GetCustomRoleTypes();
    }

    public override void AfterMeetingTasks()
    {
        IsReport[ShrineMaidenId] = false;
        OnikuId[ShrineMaidenId] = byte.MaxValue;
        IsSelecting[ShrineMaidenId] = false;
    }

    public override string GetProgressText(byte playerId, bool comms)
    {
        if (playerId != ShrineMaidenId) return string.Empty;
        int cnt = UseCount.TryGetValue(ShrineMaidenId, out int c) ? c : 0;
        return Utils.ColorString(cnt > 0 ? Color.cyan : Color.gray, $"({cnt})");
    }

    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud = false, bool meeting = false)
    {
        if (!meeting || seer.PlayerId != ShrineMaidenId || target.PlayerId != ShrineMaidenId) return string.Empty;
        if (!IsReport.TryGetValue(ShrineMaidenId, out bool rep) || !rep) return string.Empty;
        byte oid = OnikuId.TryGetValue(ShrineMaidenId, out byte o) ? o : byte.MaxValue;
        if (oid == byte.MaxValue) return string.Empty;
        return Utils.ColorString(Color.cyan, $"☯{oid.ColoredPlayerName()}");
    }
}
