using System.Collections.Generic;
using UnityEngine;

namespace EndKnot.Roles;

public class Shyboy : RoleBase
{
    private const int Id = 702000;
    private static List<byte> PlayerIdList = [];

    private static OptionItem OptionShytime;
    private static OptionItem OptionNotShy;

    private byte ShyboyId;
    private float Shytime;
    private float Notshy;
    private float DetectionRadius;

    private float Shydeath;
    private float AfterMeeting;
    private bool Notify;

    public override bool IsEnable => PlayerIdList.Count > 0;

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.CrewmateRoles, CustomRoles.Shyboy);

        OptionShytime = new FloatOptionItem(Id + 10, "ShyboyShytime", new(0f, 15f, 0.5f), 5f, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Shyboy])
            .SetValueFormat(OptionFormat.Seconds);

        OptionNotShy = new FloatOptionItem(Id + 11, "ShyboyAfterMeetingNotShytime", new(0f, 30f, 1f), 10f, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Shyboy])
            .SetValueFormat(OptionFormat.Seconds);
    }

    public override void Init()
    {
        PlayerIdList = [];
    }

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);
        ShyboyId = playerId;
        Shytime = OptionShytime.GetFloat();
        Notshy = OptionNotShy.GetFloat();

        // Detection radius: 4.5x default crewmate vision, capped at 4
        DetectionRadius = Mathf.Min(Main.DefaultCrewmateVision * 4.5f, 4f);

        Shydeath = 0f;
        AfterMeeting = 0f;
        Notify = true;
    }

    public override void Remove(byte playerId)
    {
        PlayerIdList.Remove(playerId);
    }

    public override void AfterMeetingTasks()
    {
        Shydeath = 0f;
        AfterMeeting = 0f;
        Notify = true;
    }

    public override void OnFixedUpdate(PlayerControl pc)
    {
        if (!AmongUsClient.Instance.AmHost || !pc.IsAlive()) return;
        if (GameStates.IsLobby || GameStates.IsMeeting) return;

        AfterMeeting += Time.fixedDeltaTime;

        // Grace period after meeting: wait Notshy + 5 seconds before danger starts
        if (AfterMeeting < Notshy + 5f) return;

        if (Notify)
        {
            Notify = false;
            Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc);
        }

        Vector2 pos = pc.Pos();
        bool nearOthers = false;

        foreach (PlayerControl other in Main.AllAlivePlayerControls)
        {
            if (other.PlayerId == ShyboyId) continue;
            if (Vector2.Distance(pos, other.Pos()) <= DetectionRadius)
            {
                nearOthers = true;
                break;
            }
        }

        if (nearOthers)
        {
            Shydeath += Time.fixedDeltaTime;
        }
        else
        {
            Shydeath -= Time.fixedDeltaTime * 0.25f;
            if (Shydeath < 0f) Shydeath = 0f;
        }

        if (Shydeath >= Shytime)
        {
            pc.SetRealKiller(pc);
            pc.Suicide(PlayerState.DeathReason.Suicide);
            Shydeath = -1f; // prevent immediate re-trigger
        }
    }

    public override string GetProgressText(byte playerId, bool comms)
    {
        if (playerId != ShyboyId) return string.Empty;
        if (AfterMeeting < Notshy + 5f) return string.Empty;
        if (Shydeath <= 0f) return string.Empty;

        float danger = Mathf.Clamp01(Shydeath / Shytime);
        string bar = danger < 0.33f ? Utils.ColorString(new UnityEngine.Color32(0x64, 0xd9, 0x52, 0xff), "♥")
            : danger < 0.66f ? Utils.ColorString(new UnityEngine.Color32(0xff, 0xad, 0x00, 0xff), "♥♥")
            : Utils.ColorString(new UnityEngine.Color32(0xff, 0x30, 0x30, 0xff), "♥♥♥");
        return bar;
    }
}
