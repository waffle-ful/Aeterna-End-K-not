using System;
using System.Collections.Generic;
using System.Linq;
using static EndKnot.Translator;

namespace EndKnot.Roles;

public class Satellite : RoleBase
{
    private const int Id = 702100;
    public static List<byte> PlayerIdList = [];

    private static OptionItem OptionMaxUses;
    private static OptionItem OptionMeetingMaxUses;
    private static OptionItem OptionTaskCount;

    private static Dictionary<byte, HashSet<SystemTypes>> AllPlayerVisitedRooms = [];
    private static Dictionary<byte, SystemTypes> AllPlayerLastRoom = [];

    private byte SatelliteId;
    private int usecount;
    private int meetingUseCount;
    private bool SatelliteActivated;
    private Dictionary<byte, SystemTypes?> SentPlayers = [];

    public override bool IsEnable => PlayerIdList.Count > 0;

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.CrewmateRoles, CustomRoles.Satellite);

        OptionMaxUses = new IntegerOptionItem(Id + 10, "SatelliteMaxUses", new(1, 99, 1), 2, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Satellite])
            .SetValueFormat(OptionFormat.Times);

        OptionMeetingMaxUses = new IntegerOptionItem(Id + 11, "SatelliteMeetingMaxUses", new(0, 99, 1), 1, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Satellite])
            .SetValueFormat(OptionFormat.Times);

        OptionTaskCount = new IntegerOptionItem(Id + 12, "SatelliteTaskCount", new(0, 99, 1), 5, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Satellite])
            .SetValueFormat(OptionFormat.Times);
    }

    public override void Init()
    {
        PlayerIdList = [];
        AllPlayerVisitedRooms = [];
        AllPlayerLastRoom = [];
    }

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);
        SatelliteId = playerId;
        usecount = OptionMaxUses.GetInt();
        meetingUseCount = 0;
        SatelliteActivated = false;
        SentPlayers = [];
    }

    public override void Remove(byte playerId)
    {
        PlayerIdList.Remove(playerId);
    }

    private bool CanUseAbility(PlayerControl pc)
    {
        int required = OptionTaskCount.GetInt();
        if (required > 0 && pc.GetTaskState().CompletedTasksCount < required) return false;
        return usecount > 0;
    }

    private bool MeetingLimitOk => OptionMeetingMaxUses.GetInt() == 0 || meetingUseCount < OptionMeetingMaxUses.GetInt();

    public override void OnFixedUpdate(PlayerControl pc)
    {
        if (!AmongUsClient.Instance.AmHost) return;
        if (!GameStates.IsInTask) return;
        if (Utils.IsActive(SystemTypes.Comms)) return;

        foreach (PlayerControl player in Main.AllAlivePlayerControls)
        {
            PlainShipRoom nowRoom = player.GetPlainShipRoom();
            if (!nowRoom) continue;

            if (AllPlayerLastRoom.TryGetValue(player.PlayerId, out SystemTypes lastRoom) && lastRoom == nowRoom.RoomId) continue;

            AllPlayerLastRoom[player.PlayerId] = nowRoom.RoomId;

            if (!AllPlayerVisitedRooms.TryGetValue(player.PlayerId, out HashSet<SystemTypes> visited))
            {
                visited = [];
                AllPlayerVisitedRooms[player.PlayerId] = visited;
            }

            visited.Add(nowRoom.RoomId);
        }
    }

    public override bool OnVote(PlayerControl voter, PlayerControl target)
    {
        if (voter.PlayerId != SatelliteId) return false;
        if (!voter.IsAlive()) return false;

        // Phase 1: self-vote to activate (no ability checks here)
        if (!SatelliteActivated)
        {
            if (target == null || target.PlayerId != SatelliteId) return false;
            if (!CanUseAbility(voter) || !MeetingLimitOk) return false;

            SatelliteActivated = true;
            Utils.SendMessage(GetString("SatelliteActivate"), SatelliteId, importance: MessageImportance.High);
            return true;
        }

        // Phase 2: vote a target to reveal a room
        SatelliteActivated = false;

        if (target == null) return false;

        UseAbility(target.PlayerId);
        return true;
    }

    private void UseAbility(byte targetId)
    {
        if (Utils.IsActive(SystemTypes.Comms))
        {
            Utils.SendMessage(GetString("SatelliteResultComms") + string.Format(GetString("SatelliteUsesLeft"), usecount),
                SatelliteId, importance: MessageImportance.High);
            return;
        }

        string title = string.Format(GetString("SatelliteResultTitle"), targetId.ColoredPlayerName());

        // Already scanned this meeting
        if (SentPlayers.TryGetValue(targetId, out SystemTypes? cached))
        {
            string again = cached.HasValue
                ? string.Format(GetString("SatelliteResultAgain"), targetId.ColoredPlayerName(), GetString(cached.Value.ToString()))
                : string.Format(GetString("SatelliteResultNoRooms"), targetId.ColoredPlayerName());
            Utils.SendMessage(again + string.Format(GetString("SatelliteUsesLeft"), usecount), SatelliteId, title, importance: MessageImportance.High);
            return;
        }

        // Pick one random visited room
        SystemTypes? room = null;
        if (AllPlayerVisitedRooms.TryGetValue(targetId, out HashSet<SystemTypes> visited) && visited.Count > 0)
            room = visited.OrderBy(_ => Guid.NewGuid()).First();

        SentPlayers[targetId] = room;
        usecount--;
        meetingUseCount++;

        string body = room.HasValue
            ? string.Format(GetString("SatelliteResult"), targetId.ColoredPlayerName(), GetString(room.Value.ToString()))
            : string.Format(GetString("SatelliteResultNoRooms"), targetId.ColoredPlayerName());

        Utils.SendMessage(body + string.Format(GetString("SatelliteUsesLeft"), usecount), SatelliteId, title, importance: MessageImportance.High);
    }

    public override void AfterMeetingTasks()
    {
        AllPlayerVisitedRooms.Clear();
        AllPlayerLastRoom.Clear();
        SentPlayers.Clear();
        meetingUseCount = 0;
        SatelliteActivated = false;
    }

    public override string GetProgressText(byte playerId, bool comms)
    {
        if (playerId != SatelliteId) return string.Empty;
        return Utils.ColorString(Utils.GetRoleColor(CustomRoles.Satellite), $"({usecount})");
    }

    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud = false, bool meeting = false)
    {
        if (!meeting || !seer.IsAlive() || seer.PlayerId != SatelliteId || seer.PlayerId != target.PlayerId) return string.Empty;
        if (usecount <= 0) return string.Empty;

        string hint = SatelliteActivated
            ? GetString("SatelliteHintActivated")
            : GetString("SatelliteSelfVoteHint");

        return $"<size=40%><color=#00e1ff>{hint}</color></size>";
    }
}
