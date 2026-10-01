using System;
using System.Collections.Generic;
using System.Linq;
using AmongUs.GameOptions;
using EndKnot.Modules;
using Hazel;
using static EndKnot.Translator;

namespace EndKnot.Roles;

public class EvilSatellite : RoleBase
{
    private const int Id = 699800;
    public static List<byte> PlayerIdList = [];

    private static OptionItem OptionKillCoolDown;
    private static OptionItem OptionRandom;
    private static OptionItem OptionMax;

    private static Dictionary<byte, List<SystemTypes>> AllAlivePlayerRoute = [];
    private static Dictionary<byte, SystemTypes> AllAlivePlayerLastRoom = [];

    private byte PlayerId;
    private int usecount;
    private bool SatelliteActivated;
    private Dictionary<byte, List<SystemTypes>> SentPlayerId = [];

    public override bool IsEnable => PlayerIdList.Count > 0;

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.ImpostorRoles, CustomRoles.EvilSatellite);

        OptionKillCoolDown = new FloatOptionItem(Id + 10, "EvilSatelliteKillCooldown", new(0f, 180f, 0.5f), 30f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.EvilSatellite])
            .SetValueFormat(OptionFormat.Seconds);

        OptionRandom = new BooleanOptionItem(Id + 11, "EvilSatelliteRandom", true, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.EvilSatellite]);

        OptionMax = new IntegerOptionItem(Id + 12, "EvilSatelliteMax", new(1, 99, 1), 5, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.EvilSatellite])
            .SetValueFormat(OptionFormat.Times);
    }

    public override void Init()
    {
        PlayerIdList = [];
        AllAlivePlayerRoute = [];
        AllAlivePlayerLastRoom = [];
    }

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);
        PlayerId = playerId;
        usecount = OptionMax.GetInt();
        SatelliteActivated = false;
        SentPlayerId = [];
    }

    public override void Remove(byte playerId)
    {
        PlayerIdList.Remove(playerId);
    }

    public override void SetKillCooldown(byte id)
    {
        Main.AllPlayerKillCooldown[id] = OptionKillCoolDown.GetFloat();
    }

    public override void OnFixedUpdate(PlayerControl pc)
    {
        if (!AmongUsClient.Instance.AmHost) return;
        if (!GameStates.IsInTask) return;

        foreach (PlayerControl player in Main.AllAlivePlayerControlsToList)
        {
            PlainShipRoom nowRoom = player.GetPlainShipRoom();
            if (!nowRoom) continue;

            if (AllAlivePlayerLastRoom.TryGetValue(player.PlayerId, out SystemTypes lastRoom) && lastRoom == nowRoom.RoomId) continue;

            AllAlivePlayerLastRoom[player.PlayerId] = nowRoom.RoomId;

            if (!AllAlivePlayerRoute.TryGetValue(player.PlayerId, out List<SystemTypes> route))
            {
                route = [];
                AllAlivePlayerRoute[player.PlayerId] = route;
            }

            route.Add(nowRoom.RoomId);
        }
    }

    // 会議中の案内文と残り回数は各クライアントが自分で組み立てるので、発動状態と残り回数をモッド客へ送る
    private void SendSync()
    {
        Utils.SendRPC(CustomRPC.SyncRoleData, PlayerId, usecount, SatelliteActivated);
    }

    public void ReceiveRPC(MessageReader reader)
    {
        usecount = reader.ReadPackedInt32();
        SatelliteActivated = reader.ReadBoolean();
    }

    public override bool OnVote(PlayerControl voter, PlayerControl target)
    {
        if (voter.PlayerId != PlayerId) return false;

        if (!SatelliteActivated)
        {
            if (target != null && target.PlayerId == PlayerId && usecount > 0)
            {
                SatelliteActivated = true;
                SendSync();
                Utils.SendMessage(GetString("EvilSatelliteActivate"), PlayerId, importance: MessageImportance.High);
                return true;
            }

            return false;
        }

        SatelliteActivated = false;

        if (target != null)
        {
            SendPlayerRoute(target.PlayerId);
            SendSync();
            return true;
        }

        SendSync();
        return false;
    }

    public override void AfterMeetingTasks()
    {
        AllAlivePlayerRoute.Clear();
        AllAlivePlayerLastRoom.Clear();

        if (SatelliteActivated)
        {
            SatelliteActivated = false;
            SendSync();
        }

        SentPlayerId.Clear();
    }

    public override string GetProgressText(byte playerId, bool comms)
    {
        return $"<color=#ff1919>({usecount})</color>";
    }

    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud = false, bool meeting = false)
    {
        if (!meeting || !seer.IsAlive() || seer.PlayerId != PlayerId || seer.PlayerId != target.PlayerId) return string.Empty;
        if (usecount <= 0) return string.Empty;

        string hint = SatelliteActivated
            ? GetString("EvilSatelliteHintActivated")
            : GetString("EvilSatelliteSelfVoteHint");

        return $"<size=40%><color=#ff1919>{hint}</color></size>";
    }

    private void SendPlayerRoute(byte playerId)
    {
        if (!AllAlivePlayerRoute.TryGetValue(playerId, out List<SystemTypes> routeList)) return;

        if (!SentPlayerId.TryGetValue(playerId, out List<SystemTypes> sentList))
        {
            if (usecount <= 0) return;
            usecount--;

            sentList = OptionRandom.GetBool()
                ? routeList.OrderBy(_ => Guid.NewGuid()).ToList()
                : [..routeList];

            SentPlayerId[playerId] = sentList;
        }

        string sendtext = "<size=60%><line-height=80%>";
        int index = 0;
        int count = 0;
        foreach (SystemTypes room in sentList)
        {
            sendtext += GetString(room.ToString())
                + (sentList.Count == count + 1 ? "" : (OptionRandom.GetBool() ? "・" : " → "))
                + (count > 3 ? "\n" : "");
            if (index > 3) index = 0;
            index++;
            count++;
        }

        sendtext += string.Format(GetString("EvilSatelliteRouteInfo"), playerId.ColoredPlayerName());
        if (OptionRandom.GetBool()) sendtext += GetString("EvilSatelliteRouteInfo2");
        sendtext += string.Format(GetString("EvilSatelliteRouteInfo3"), usecount);

        Utils.SendMessage(
            sendtext,
            PlayerId,
            string.Format($"<color=#ff1919>{GetString("EvilSatelliteRouteInfoTitle")}</color>", playerId.ColoredPlayerName()),
            importance: MessageImportance.High
        );
    }
}
