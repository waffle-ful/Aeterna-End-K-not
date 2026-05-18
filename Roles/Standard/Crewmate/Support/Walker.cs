using System.Collections.Generic;
using EndKnot.Modules;
using UnityEngine;
using static EndKnot.Translator;

namespace EndKnot.Roles;

public class Walker : RoleBase
{
    private const int Id = 702900;
    public static List<byte> PlayerIdList = [];

    private static OptionItem OptionWalkTaskCount;

    private static Dictionary<byte, HashSet<SystemTypes>> VisitedRooms = [];

    private byte WalkerId;

    public override bool IsEnable => PlayerIdList.Count > 0;

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.CrewmateRoles, CustomRoles.Walker);

        OptionWalkTaskCount = new IntegerOptionItem(Id + 10, "WalkerWalkTaskCount", new(1, 99, 1), 5, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Walker]);
    }

    public override void Init()
    {
        PlayerIdList = [];
        VisitedRooms = [];
    }

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);
        WalkerId = playerId;
        VisitedRooms[playerId] = [];
    }

    public override void Remove(byte playerId)
    {
        PlayerIdList.Remove(playerId);
    }

    public override void OnFixedUpdate(PlayerControl pc)
    {
        if (!AmongUsClient.Instance.AmHost) return;
        if (!pc.IsAlive()) return;

        PlainShipRoom room = pc.GetPlainShipRoom();
        if (room == null) return;

        if (!VisitedRooms.TryGetValue(pc.PlayerId, out var visited)) return;

        int before = visited.Count;
        if (!visited.Add(room.RoomId)) return;

        int required = OptionWalkTaskCount.GetInt();
        if (before < required && visited.Count >= required)
            ForceCompleteTasks(pc);
    }

    private static void ForceCompleteTasks(PlayerControl pc)
    {
        TaskState ts = pc.GetTaskState();
        if (!ts.HasTasks || ts.IsTaskFinished) return;

        foreach (PlayerTask task in pc.myTasks)
        {
            if (!task.IsComplete)
                pc.RpcCompleteTask(task.Id);
        }
    }

    public static bool HasCompletedTour(byte playerId)
    {
        int visited = VisitedRooms.TryGetValue(playerId, out var set) ? set.Count : 0;
        return visited >= OptionWalkTaskCount.GetInt();
    }

    public override string GetProgressText(byte playerId, bool comms)
    {
        if (playerId != WalkerId) return string.Empty;
        int visited = VisitedRooms.TryGetValue(WalkerId, out var set) ? set.Count : 0;
        int required = OptionWalkTaskCount.GetInt();
        bool done = visited >= required;
        return Utils.ColorString(done ? Color.gray : Color.cyan, $"({visited}/{required})");
    }

    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud = false, bool meeting = false)
    {
        if (meeting || seer.PlayerId != WalkerId || target.PlayerId != WalkerId || !hud) return string.Empty;
        int visited = VisitedRooms.TryGetValue(WalkerId, out var set) ? set.Count : 0;
        int required = OptionWalkTaskCount.GetInt();
        if (visited >= required) return string.Empty;
        PlainShipRoom room = seer.GetPlainShipRoom();
        if (room == null) return string.Empty;
        return Utils.ColorString(Color.cyan, GetString(room.RoomId.ToString()));
    }
}
