using System.Collections.Generic;
using EndKnot.Modules;
using UnityEngine;

namespace EndKnot.Roles;

public class NiceLogger : RoleBase
{
    private const int Id = 701200;
    private static List<byte> PlayerIdList = [];

    private static OptionItem OptionCoolTime;

    private byte NiceLoggerId;
    private bool Taskmode;
    private Vector2? LogPos;
    private string SetRoom;
    private List<byte> Log = [];
    private float Cooltime;

    public override bool IsEnable => PlayerIdList.Count > 0;

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.CrewmateRoles, CustomRoles.NiceLogger);

        OptionCoolTime = new FloatOptionItem(Id + 10, "NiceLoggerCoolTime", new(0.5f, 60f, 0.5f), 3f, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.NiceLogger])
            .SetValueFormat(OptionFormat.Seconds);
    }

    public override void Init()
    {
        PlayerIdList = [];
    }

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);
        NiceLoggerId = playerId;
        Taskmode = false;
        LogPos = null;
        Log = [];
        Cooltime = 0f;
        SetRoom = string.Empty;
    }

    public override void Remove(byte playerId)
    {
        PlayerIdList.Remove(playerId);
    }

    public override void OnPet(PlayerControl pc)
    {
        OnAbility(pc);
    }

    private void OnAbility(PlayerControl pc)
    {
        if (Taskmode) return;
        if (ShipStatus.Instance == null) return;

        float minDist = float.MaxValue;
        OpenableDoor nearestDoor = null;
        Vector2 pos = pc.Pos();

        foreach (OpenableDoor door in ShipStatus.Instance.AllDoors)
        {
            float d = Vector2.Distance(pos, door.transform.position);
            if (d >= minDist) continue;
            minDist = d;
            nearestDoor = door;
        }

        if (nearestDoor == null) return;

        LogPos = nearestDoor.transform.position;
        SetRoom = Translator.GetString(nearestDoor.Room.ToString());
        Cooltime = 0f;
        Taskmode = true;

        pc.SyncSettings();
        Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc);
    }

    public override void OnFixedUpdate(PlayerControl pc)
    {
        if (!AmongUsClient.Instance.AmHost || !pc.IsAlive()) return;
        if (!Taskmode || !LogPos.HasValue) return;
        if (GameStates.IsLobby) return;

        Cooltime += Time.fixedDeltaTime;
        if (Cooltime < OptionCoolTime.GetFloat()) return;

        Vector2 logPos = LogPos.Value;
        foreach (PlayerControl player in Main.AllAlivePlayerControls)
        {
            if (player.PlayerId == NiceLoggerId) continue;
            if (!player.CanMove) continue;
            if (!FastVector2.DistanceWithinRange(logPos, player.Pos(), 0.5f)) continue;

            Log.Add(player.PlayerId);
            Cooltime = 0f;
            break;
        }
    }

    public override void OnReportDeadBody()
    {
        if (!AmongUsClient.Instance.AmHost) return;
        if (!LogPos.HasValue) return;

        PlayerControl logger = NiceLoggerId.GetPlayer();
        if (logger == null || !logger.IsAlive()) return;

        string send = "<size=70%>";
        if (Log.Count != 0)
        {
            foreach (byte id in Log)
            {
                string coloredName = id.ColoredPlayerName();
                send += string.Format(Translator.GetString("NiceLoggerAbility"), coloredName, SetRoom);
            }
        }
        else
        {
            send += string.Format(Translator.GetString("NiceLoggerAbility2"), SetRoom);
        }

        LateTask.New(() => Utils.SendMessage(send, NiceLoggerId,
            Utils.ColorString(Utils.GetRoleColor(CustomRoles.NiceLogger), Translator.GetString("NiceLoggerTitle"))),
            4f, "NiceLoggerSend");
    }

    public override void AfterMeetingTasks()
    {
        Log = [];
        LogPos = null;

        PlayerControl pc = NiceLoggerId.GetPlayer();
        Taskmode = pc == null || !pc.IsAlive();

        if (!Taskmode && pc != null)
        {
            pc.SyncSettings();
            Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc);
        }
    }

    public override void SetButtonTexts(HudManager hud, byte id)
    {
        hud.PetButton?.OverrideText(Translator.GetString("NiceLogger_Ability"));
    }

    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud = false, bool meeting = false)
    {
        if (seer.PlayerId != NiceLoggerId || seer.PlayerId != target.PlayerId || meeting || !seer.IsAlive() || Taskmode) return string.Empty;
        return Translator.GetString("NiceLoggerLower");
    }

    public override string GetProgressText(byte playerId, bool comms)
    {
        if (playerId != NiceLoggerId) return string.Empty;
        if (!Taskmode || !LogPos.HasValue) return string.Empty;
        return Utils.ColorString(Utils.GetRoleColor(CustomRoles.NiceLogger), $"[{Log.Count}]");
    }
}
