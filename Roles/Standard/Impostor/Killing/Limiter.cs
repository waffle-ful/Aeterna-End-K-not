using System.Collections.Generic;
using UnityEngine;

namespace EndKnot.Roles;

public class Limiter : RoleBase
{
    private const int Id = 699300;
    private static List<byte> PlayerIdList = [];

    private static OptionItem KillCooldown;
    private static OptionItem LastTurnKillCool;
    private static OptionItem TurnLimit;
    private static OptionItem TimeLimit;
    private static OptionItem KillLimit;
    private static OptionItem LimitMeeting;
    private static OptionItem BlastRange;

    private bool Limit;
    private float Timer;
    private int KillCount;
    private byte LimiterId;

    public override bool IsEnable => PlayerIdList.Count > 0;

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.ImpostorRoles, CustomRoles.Limiter);

        KillCooldown = new FloatOptionItem(Id + 10, "KillCooldown", new(0f, 180f, 0.5f), 25f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Limiter])
            .SetValueFormat(OptionFormat.Seconds);

        LastTurnKillCool = new FloatOptionItem(Id + 11, "LimiterLastTurnKillCool", new(0f, 180f, 0.5f), 5f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Limiter])
            .SetValueFormat(OptionFormat.Seconds);

        TurnLimit = new IntegerOptionItem(Id + 12, "LimiterTurnLimit", new(0, 15, 1), 3, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Limiter])
            .SetValueFormat(OptionFormat.Times);

        TimeLimit = new FloatOptionItem(Id + 13, "LimiterTimeLimit", new(0f, 300f, 5f), 180f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Limiter])
            .SetValueFormat(OptionFormat.Seconds);

        KillLimit = new IntegerOptionItem(Id + 14, "LimiterKillLimit", new(0, 15, 1), 4, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Limiter])
            .SetValueFormat(OptionFormat.Times);

        LimitMeeting = new BooleanOptionItem(Id + 15, "LimiterLimitMeeting", false, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Limiter]);

        BlastRange = new FloatOptionItem(Id + 16, "LimiterBlastRange", new(0.5f, 20f, 0.5f), 5f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Limiter]);
    }

    public override void Init()
    {
        PlayerIdList = [];
    }

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);
        Limit = false;
        Timer = 0f;
        KillCount = 0;
        LimiterId = playerId;
    }

    public override void Remove(byte playerId)
    {
        PlayerIdList.Remove(playerId);
    }

    public override void SetKillCooldown(byte id)
    {
        Main.AllPlayerKillCooldown[id] = KillCooldown.GetFloat();
    }

    public override bool OnCheckMurder(PlayerControl killer, PlayerControl target)
    {
        if (!Limit) return true;

        // 自爆：キル不発、範囲内の全員を爆殺してから自分も死ぬ
        float range = BlastRange.GetFloat();
        foreach (PlayerControl pc in Main.AllAlivePlayerControls)
        {
            if (pc.PlayerId == killer.PlayerId) continue;
            if (Vector3.Distance(killer.transform.position, pc.transform.position) > range) continue;

            pc.SetRealKiller(killer);
            pc.Suicide(PlayerState.DeathReason.Bombed, killer);
        }

        Main.PlayerStates[killer.PlayerId].deathReason = PlayerState.DeathReason.Bombed;
        killer.Suicide(PlayerState.DeathReason.Bombed);

        return false;
    }

    public override void OnMurder(PlayerControl killer, PlayerControl target)
    {
        if (Limit) return;
        if (KillLimit.GetInt() == 0) return;

        KillCount++;
        if (KillCount >= KillLimit.GetInt())
        {
            ActivateLimit(killer);
        }
    }

    public override void OnFixedUpdate(PlayerControl pc)
    {
        if (Limit || !pc.IsAlive() || GameStates.IsMeeting) return;
        if (TimeLimit.GetFloat() == 0f) return;

        Timer += Time.fixedDeltaTime;

        if (Timer >= TimeLimit.GetFloat())
        {
            ActivateLimit(pc);
        }
    }

    public override void AfterMeetingTasks()
    {
        if (Limit) return;
        if (TurnLimit.GetInt() == 0) return;

        PlayerControl pc = Utils.GetPlayerById(LimiterId);
        if (pc == null || !pc.IsAlive()) return;

        if (MeetingStates.MeetingNum >= TurnLimit.GetInt())
        {
            ActivateLimit(pc);
        }
    }

    public override void OnReportDeadBody()
    {
        if (!Limit) return;
        if (LimitMeeting.GetBool()) return;

        PlayerControl pc = Utils.GetPlayerById(LimiterId);
        if (pc == null || !pc.IsAlive()) return;

        pc.SetRealKiller(pc);
        pc.Suicide(PlayerState.DeathReason.Bombed);
    }

    private void ActivateLimit(PlayerControl pc)
    {
        Limit = true;
        LateTask.New(() =>
        {
            Main.AllPlayerKillCooldown[pc.PlayerId] = LastTurnKillCool.GetFloat();
            pc.SetKillCooldown();
            Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc);
        }, 0.3f, "Limiter.ActivateLimit");
    }

    public override string GetProgressText(byte playerId, bool comms)
    {
        if (playerId != LimiterId) return string.Empty;

        if (Limit)
            return Utils.ColorString(Color.red, Translator.GetString("LimiterBom"));

        var parts = new System.Text.StringBuilder();

        if (TimeLimit.GetFloat() > 0f)
        {
            int remaining = (int)(TimeLimit.GetFloat() - Timer);
            parts.Append($"(Ⓣ{remaining}s)");
        }

        if (TurnLimit.GetInt() > 0)
            parts.Append($"(Ⓓ{MeetingStates.MeetingNum}/{TurnLimit.GetInt()})");

        if (KillLimit.GetInt() > 0)
            parts.Append($"(Ⓚ{KillCount}/{KillLimit.GetInt()})");

        return parts.Length > 0
            ? Utils.ColorString(new Color32(255, 165, 0, 255), parts.ToString())
            : string.Empty;
    }

    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud = false, bool meeting = false)
    {
        if (seer.PlayerId != LimiterId || seer.PlayerId != target.PlayerId) return string.Empty;
        if (meeting) return string.Empty;

        if (Limit && seer.IsAlive())
            return Utils.ColorString(Color.red, Translator.GetString("LimiterBom"));

        return string.Empty;
    }

    public override void SetButtonTexts(HudManager hud, byte id)
    {
        if (Limit)
            hud.KillButton?.OverrideText(Translator.GetString("LimiterExplodeButtonText"));
    }
}
