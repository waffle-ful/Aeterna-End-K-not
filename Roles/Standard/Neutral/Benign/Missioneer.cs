using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AmongUs.GameOptions;
using EndKnot.Modules;
using Hazel;
using UnityEngine;
using static EndKnot.Options;
using static EndKnot.Translator;

namespace EndKnot.Roles;

public class Missioneer : RoleBase
{
    private const int Id = 704300;
    public static bool On;

    private static OptionItem KillCooldown;
    private static OptionItem MeetingAssignmentCount;
    private static OptionItem WinAssignmentPoint;
    private static OptionItem AddWinAssignmentPoint;
    private static OptionItem Lv1Point;
    private static OptionItem Lv2Point;
    private static OptionItem Lv3Point;
    private static OptionItem Lv4Point;
    private static OptionItem KillPoint;
    private static OptionItem MovePoint;
    private static OptionItem TaskPoint;

    private byte MissioneerId;
    private MissionKind NowMission;
    private int NowPoint;
    private byte TargetPlayerId;
    private SystemTypes? TargetRoom;
    private int TargetVentId;
    private Vector3 TargetVentPos;
    private bool Gotovent;
    private bool IsSelectingMode;
    private bool HasSetMission;
    private float ProximityTimer;
    private bool AddWin;
    private int VotesReceivedThisMeeting;
    private Dictionary<byte, MissionKind> CurrentMissionList;

    public override bool IsEnable => On;

    public enum MissionKind
    {
        None = -1,
        Kill = 0, KillToVent = 1, KillRoom = 2, KillPlayer = 3,
        GoRoom = 10, GoVent = 11, SeePlayer = 12, MorePlayer = 13,
        Task = 20, Vote = 21, AllTaskComp = 22, Report = 23
    }

    private static readonly MissionKind[] AllMissions =
    [
        MissionKind.Kill, MissionKind.KillToVent, MissionKind.KillRoom, MissionKind.KillPlayer,
        MissionKind.GoRoom, MissionKind.GoVent, MissionKind.SeePlayer, MissionKind.MorePlayer,
        MissionKind.Task, MissionKind.Vote, MissionKind.AllTaskComp, MissionKind.Report
    ];

    public override void SetupCustomOption()
    {
        StartSetup(Id)
            .AutoSetupOption(ref KillCooldown, 20f, new FloatValueRule(0f, 120f, 0.5f), OptionFormat.Seconds)
            .AutoSetupOption(ref MeetingAssignmentCount, 3, new IntegerValueRule(1, 5, 1))
            .AutoSetupOption(ref WinAssignmentPoint, 30, new IntegerValueRule(1, 300, 1))
            .AutoSetupOption(ref AddWinAssignmentPoint, 20, new IntegerValueRule(0, 300, 1))
            .AutoSetupOption(ref Lv1Point, 0, new IntegerValueRule(0, 25, 1))
            .AutoSetupOption(ref Lv2Point, 1, new IntegerValueRule(0, 25, 1))
            .AutoSetupOption(ref Lv3Point, 3, new IntegerValueRule(0, 25, 1))
            .AutoSetupOption(ref Lv4Point, 5, new IntegerValueRule(0, 25, 1))
            .AutoSetupOption(ref KillPoint, 5, new IntegerValueRule(0, 25, 1))
            .AutoSetupOption(ref MovePoint, 2, new IntegerValueRule(0, 25, 1))
            .AutoSetupOption(ref TaskPoint, 1, new IntegerValueRule(0, 25, 1))
            .CreateOverrideTasksData();
    }

    public override void Init()
    {
        On = false;
    }

    public override void Add(byte playerId)
    {
        On = true;
        MissioneerId = playerId;
        NowMission = MissionKind.None;
        NowPoint = 0;
        TargetPlayerId = byte.MaxValue;
        TargetRoom = null;
        TargetVentId = -1;
        TargetVentPos = Vector3.zero;
        Gotovent = false;
        IsSelectingMode = false;
        HasSetMission = false;
        ProximityTimer = 0f;
        AddWin = false;
        VotesReceivedThisMeeting = 0;
        CurrentMissionList = [];
    }

    public override void Remove(byte playerId)
    {
        if (MissioneerId == playerId) On = false;
    }

    public override void SetKillCooldown(byte id)
    {
        Main.AllPlayerKillCooldown[id] = KillCooldown.GetFloat();
    }

    public override void ApplyGameOptions(IGameOptions opt, byte id)
    {
        opt.SetVision(false);
        AURoleOptions.EngineerCooldown = 0f;
        AURoleOptions.EngineerInVentMaxTime = 0f;
    }

    public override bool CanUseKillButton(PlayerControl pc)
    {
        return IsKillMission() && pc.IsAlive();
    }

    public override bool CanUseImpostorVentButton(PlayerControl pc)
    {
        return pc.IsAlive();
    }

    public override bool CanUseSabotage(PlayerControl pc) => false;

    private bool IsKillMission() => NowMission != MissionKind.None && (int)NowMission < 10;

    private int GetPoint(MissionKind mission)
    {
        int lv, category;
        if ((int)mission < 10) { category = KillPoint.GetInt(); lv = (int)mission; }
        else if ((int)mission < 20) { category = MovePoint.GetInt(); lv = (int)mission - 10; }
        else { category = TaskPoint.GetInt(); lv = (int)mission - 20; }

        int lvBonus = lv switch
        {
            0 => Lv1Point.GetInt(),
            1 => Lv2Point.GetInt(),
            2 => Lv3Point.GetInt(),
            _ => Lv4Point.GetInt()
        };
        return category + lvBonus;
    }

    private void CompleteMission()
    {
        if (!AmongUsClient.Instance.AmHost) return;

        int gained = GetPoint(NowMission);
        NowPoint += gained;
        Logger.Info($"Missioneer completed {NowMission}: +{gained} = {NowPoint}", "Missioneer");

        if (WinAssignmentPoint.GetInt() > 0 && NowPoint >= WinAssignmentPoint.GetInt())
        {
            CustomWinnerHolder.ResetAndSetWinner(CustomWinner.Missioneer);
            CustomWinnerHolder.WinnerIds.Add(MissioneerId);
        }

        if (AddWinAssignmentPoint.GetInt() > 0 && NowPoint >= AddWinAssignmentPoint.GetInt())
            AddWin = true;

        if (IsKillMission())
        {
            PlayerControl pc = Utils.GetPlayerById(MissioneerId);
            if (pc != null) pc.RpcSetRoleDesync(RoleTypes.Engineer, pc.OwnerId);
        }

        if (TargetVentPos != Vector3.zero)
        {
            LocateArrow.Remove(MissioneerId, TargetVentPos);
            TargetVentPos = Vector3.zero;
            TargetVentId = -1;
        }

        NowMission = MissionKind.None;
        TargetPlayerId = byte.MaxValue;
        TargetRoom = null;
        Gotovent = false;
        SendRPC();

        PlayerControl mpc = Utils.GetPlayerById(MissioneerId);
        if (mpc != null) mpc.KillFlash();
        Utils.NotifyRoles(SpecifySeer: mpc, SpecifyTarget: mpc);
    }

    public override bool OnCheckMurder(PlayerControl killer, PlayerControl target)
    {
        if (!IsKillMission()) return false;

        switch (NowMission)
        {
            case MissionKind.Kill:
                CompleteMission();
                return true;
            case MissionKind.KillToVent:
                Gotovent = true;
                SendRPC();
                return true;
            case MissionKind.KillPlayer when target.PlayerId == TargetPlayerId:
                CompleteMission();
                return true;
            case MissionKind.KillRoom when target.GetPlainShipRoom()?.RoomId == TargetRoom:
                CompleteMission();
                return true;
            default:
                return false;
        }
    }

    public override void OnEnterVent(PlayerControl pc, Vent vent)
    {
        if (!pc.IsAlive() || pc.PlayerId != MissioneerId) return;

        if (Gotovent && NowMission == MissionKind.KillToVent)
        {
            CompleteMission();
            return;
        }

        if (NowMission == MissionKind.GoVent && vent.Id == TargetVentId)
            CompleteMission();
    }

    public override bool CheckReportDeadBody(PlayerControl reporter, NetworkedPlayerInfo target, PlayerControl killer)
    {
        if (reporter.PlayerId == MissioneerId && target != null && NowMission == MissionKind.Report)
            CompleteMission();
        return true;
    }

    public override void OnReportDeadBody()
    {
        int maxMissions = Math.Min(MeetingAssignmentCount.GetInt(), Main.AllAlivePlayerControlsToList.Count - 1);
        CurrentMissionList = BuildMissionList(maxMissions);
        HasSetMission = false;
        ProximityTimer = 0f;
        VotesReceivedThisMeeting = 0;
    }

    private Dictionary<byte, MissionKind> BuildMissionList(int count)
    {
        var result = new Dictionary<byte, MissionKind>();
        var others = Main.AllAlivePlayerControlsToList.Where(p => p.PlayerId != MissioneerId).ToList();

        for (int i = 0; i < count && i < others.Count; i++)
            result[others[i].PlayerId] = AllMissions[IRandom.Instance.Next(AllMissions.Length)];

        return result;
    }

    public override void OnFixedUpdate(PlayerControl pc)
    {
        if (!pc.IsAlive() || pc.PlayerId != MissioneerId || !AmongUsClient.Instance.AmHost) return;

        if (NowMission is MissionKind.SeePlayer or MissionKind.MorePlayer)
        {
            ProximityTimer += Time.fixedDeltaTime;
            if (ProximityTimer < 10f) return;

            Vector2 myPos = pc.GetTruePosition();

            if (NowMission == MissionKind.MorePlayer)
            {
                int nearby = Main.AllAlivePlayerControlsToList.Count(p => Vector2.Distance(p.GetTruePosition(), myPos) < 2.5f);
                if (nearby > 3) CompleteMission(); // self included, so >3 means 3+ others
            }
            else
            {
                bool seeTarget = Main.AllAlivePlayerControlsToList.Any(p => p.PlayerId == TargetPlayerId && Vector2.Distance(p.GetTruePosition(), myPos) < 2.5f);
                if (seeTarget) CompleteMission();
            }

            return;
        }

        if (NowMission == MissionKind.GoRoom && pc.GetPlainShipRoom()?.RoomId == TargetRoom)
            CompleteMission();
    }

    public override void OnTaskComplete(PlayerControl pc, int completedTaskCount, int totalTaskCount)
    {
        if (pc.PlayerId != MissioneerId) return;
        if (NowMission == MissionKind.Task) CompleteMission();
        else if (NowMission == MissionKind.AllTaskComp && completedTaskCount >= totalTaskCount) CompleteMission();
    }

    public override bool OnVote(PlayerControl voter, PlayerControl target)
    {
        if (voter.PlayerId != MissioneerId) return false;
        if (!voter.IsAlive()) return false;
        if (HasSetMission) return false;
        if (!AmongUsClient.Instance.AmHost) return false;
        if (target == null) return false;

        if (!IsSelectingMode)
        {
            if (target.PlayerId != MissioneerId) return false;
            IsSelectingMode = true;
            if (CurrentMissionList.Count == 0)
                CurrentMissionList = BuildMissionList(Math.Min(MeetingAssignmentCount.GetInt(), Main.AllAlivePlayerControlsToList.Count - 1));
            ShowMissionList();
            return true;
        }

        if (target.PlayerId == MissioneerId) return true; // eat self-votes while in selection mode
        SetMission(target.PlayerId);
        return true;
    }

    private void ShowMissionList()
    {
        if (CurrentMissionList.Count == 0)
        {
            Utils.SendMessage(GetString("MissioneerNoMissions"), MissioneerId);
            IsSelectingMode = false;
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"<size=80%>{GetString("MissioneerSelectMission")}");
        foreach (var (pid, mission) in CurrentMissionList)
        {
            string pName = Utils.GetPlayerById(pid)?.GetRealName() ?? "?";
            int pts = GetPoint(mission);
            sb.AppendLine($"  {pName} → {GetString($"Mission.{mission}")} (+{pts}pt)");
        }
        sb.Append("</size>");
        Utils.SendMessage(sb.ToString().Trim(), MissioneerId);
    }

    private void SetMission(byte votedPlayerId)
    {
        if (!CurrentMissionList.TryGetValue(votedPlayerId, out var mission))
        {
            Utils.SendMessage(GetString("MissioneerInvalidTarget"), MissioneerId);
            IsSelectingMode = false;
            return;
        }

        if (TargetVentPos != Vector3.zero)
        {
            LocateArrow.Remove(MissioneerId, TargetVentPos);
            TargetVentPos = Vector3.zero;
            TargetVentId = -1;
        }

        IsSelectingMode = false;
        HasSetMission = true;
        TargetPlayerId = byte.MaxValue;
        Gotovent = false;
        TargetRoom = null;
        NowMission = mission;

        if (mission is MissionKind.KillPlayer or MissionKind.SeePlayer)
        {
            var others = Main.AllAlivePlayerControlsToList.Where(p => p.PlayerId != MissioneerId).ToArray();
            if (others.Length > 0) TargetPlayerId = others[IRandom.Instance.Next(others.Length)].PlayerId;
        }

        if (mission is MissionKind.GoRoom or MissionKind.KillRoom && ShipStatus.Instance != null)
        {
            var rooms = ShipStatus.Instance.AllRooms
                .Select(r => r.RoomId)
                .Where(r => r != SystemTypes.Hallway)
                .ToList();
            if (rooms.Count > 0) TargetRoom = rooms[IRandom.Instance.Next(rooms.Count)];
        }

        if (mission == MissionKind.GoVent && ShipStatus.Instance != null)
        {
            var vents = ShipStatus.Instance.AllVents.ToList();
            if (vents.Count > 0)
            {
                var v = vents[IRandom.Instance.Next(vents.Count)];
                TargetVentId = v.Id;
                TargetVentPos = v.transform.position;
                LocateArrow.Add(MissioneerId, TargetVentPos);
            }
        }

        Utils.SendMessage(string.Format(GetString("MissioneerMissionSet"), GetString($"Mission.{NowMission}")), MissioneerId);
        SendRPC();
        PlayerControl mpc = Utils.GetPlayerById(MissioneerId);
        Utils.NotifyRoles(SpecifySeer: mpc, SpecifyTarget: mpc);
    }

    public override void AfterMeetingTasks()
    {
        if (NowMission == MissionKind.Vote && VotesReceivedThisMeeting >= 2)
            CompleteMission();

        VotesReceivedThisMeeting = 0;
        IsSelectingMode = false;

        if (!AmongUsClient.Instance.AmHost) return;
        PlayerControl pc = Utils.GetPlayerById(MissioneerId);
        if (pc == null || !pc.IsAlive()) return;

        if (IsKillMission())
        {
            pc.RpcSetRoleDesync(RoleTypes.Impostor, pc.OwnerId);
            // Make all other alive players appear as Crewmate to the Missioneer's client
            // so the kill button lights up regardless of their actual roles.
            // Without this, Impostor-team players can never be valid kill targets.
            foreach (var other in Main.AllAlivePlayerControlsToList)
            {
                if (other.PlayerId != MissioneerId)
                    other.RpcSetRoleDesync(RoleTypes.Crewmate, pc.OwnerId);
            }
            LateTask.New(() =>
            {
                PlayerControl p = Utils.GetPlayerById(MissioneerId);
                if (p != null && p.IsAlive()) p.SetKillCooldownNonSync(KillCooldown.GetFloat());
            }, 0.2f, log: false);
        }

        SendRPC();
    }

    public static void TrackVoteReceived(byte targetId)
    {
        if (!On || !AmongUsClient.Instance.AmHost) return;
        if (Main.PlayerStates.TryGetValue(targetId, out var state) && state.Role is Missioneer m && m.NowMission == MissionKind.Vote)
            m.VotesReceivedThisMeeting++;
    }

    private void SendRPC()
    {
        Utils.SendRPC(CustomRPC.SyncRoleData, MissioneerId,
            (int)NowMission,
            NowPoint,
            TargetPlayerId,
            TargetRoom.HasValue ? (int)TargetRoom.Value : -1,
            TargetVentId,
            AddWin ? 1 : 0,
            Gotovent ? 1 : 0);
    }

    public void ReceiveRPC(MessageReader reader)
    {
        NowMission = (MissionKind)reader.ReadPackedInt32();
        NowPoint = reader.ReadPackedInt32();
        TargetPlayerId = reader.ReadByte();
        int roomInt = reader.ReadPackedInt32();
        TargetRoom = roomInt == -1 ? null : (SystemTypes)roomInt;
        TargetVentId = reader.ReadPackedInt32();
        AddWin = reader.ReadPackedInt32() == 1;
        Gotovent = reader.ReadPackedInt32() == 1;

        if (TargetVentId != -1 && ShipStatus.Instance != null)
        {
            var vent = ShipStatus.Instance.AllVents.FirstOrDefault(v => v.Id == TargetVentId);
            TargetVentPos = vent != null ? vent.transform.position : Vector3.zero;
        }
        else TargetVentPos = Vector3.zero;
    }

    public override string GetProgressText(byte playerId, bool comms)
    {
        if (playerId != MissioneerId) return string.Empty;
        return Utils.ColorString(Utils.GetRoleColor(CustomRoles.Missioneer), $"({NowPoint}/{WinAssignmentPoint.GetInt()})");
    }

    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud = false, bool meeting = false)
    {
        if (seer.PlayerId != MissioneerId || seer.PlayerId != target.PlayerId) return string.Empty;
        if (!seer.IsAlive()) return string.Empty;

        if (meeting)
        {
            if (IsSelectingMode) return $"<size=40%><color=#b1ae8f>{GetString("MissioneerSelectingHint")}</color></size>";
            if (!HasSetMission && CurrentMissionList.Count > 0) return $"<size=40%><color=#b1ae8f>{GetString("MissioneerSelfVoteHint")}</color></size>";
            return string.Empty;
        }

        if (!hud && !seer.IsModdedClient()) return string.Empty;
        if (NowMission == MissionKind.None) return string.Empty;

        var sb = new StringBuilder();
        sb.Append(Utils.ColorString(Utils.GetRoleColor(CustomRoles.Missioneer), GetString($"Mission.{NowMission}")));

        if (NowMission is MissionKind.KillPlayer or MissionKind.SeePlayer)
        {
            string tName = Utils.GetPlayerById(TargetPlayerId)?.GetRealName() ?? "?";
            sb.Append($"\n<size=80%>{GetString("MissioneerTarget")}: {tName}</size>");
        }
        else if ((NowMission is MissionKind.GoRoom or MissionKind.KillRoom) && TargetRoom.HasValue)
        {
            string rName = GetString(TargetRoom.Value.ToString());
            sb.Append($"\n<size=80%>{GetString("MissioneerRoom")}: {rName}</size>");
        }
        else if (NowMission == MissionKind.GoVent && TargetVentPos != Vector3.zero)
        {
            sb.Append($"\n{LocateArrow.GetArrow(seer, TargetVentPos)}");
        }

        if (AddWin) sb.Append(" ★");

        return sb.ToString().Trim();
    }

    public bool IsWon => AddWin;
}
