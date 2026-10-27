using System;
using AmongUs.GameOptions;
using UnityEngine;

namespace EndKnot.Roles;

// コンビ役職の相方 (影)。光が近くにいる間だけ、キルとベントの気配を感じ取れる。
// 出現率とオプションは光 (LightPair.cs) 側に集約している。
public class ShadowPair : RoleBase
{
    public static bool On;
    public override bool IsEnable => On;

    public override void SetupCustomOption() { }

    public override void Init()
    {
        On = false;
        LightPair.ShadowId = byte.MaxValue;
    }

    public override void Add(byte playerId)
    {
        On = true;
        LightPair.ShadowId = playerId;
    }

    public override void Remove(byte playerId)
    {
        if (LightPair.ShadowId == playerId) LightPair.ShadowId = byte.MaxValue;
        On = LightPair.ShadowId != byte.MaxValue;
    }

    public override void ApplyGameOptions(IGameOptions opt, byte playerId)
    {
        LightPair.ApplyVision(opt);
    }

    public override void AfterMeetingTasks()
    {
        LightPair.ProcessPending();
    }

    public override void OnReportDeadBody()
    {
        if (LightPair.ShadowId != byte.MaxValue) LocateArrow.RemoveAllTarget(LightPair.ShadowId);
    }

    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud = false, bool meeting = false)
    {
        if (meeting || seer.PlayerId != LightPair.ShadowId || target.PlayerId != seer.PlayerId || !seer.IsAlive()) return string.Empty;

        string arrows = LocateArrow.GetArrows(seer);
        return string.IsNullOrEmpty(arrows) ? string.Empty : Utils.ColorString(LightPair.TeamColor, arrows);
    }

    private static long NextFlashTick;

    // 影が気配を感じ取れる状態か (影も光も生きていて、有効距離内にいる)
    private static PlayerControl GetSensor()
    {
        if (!LightPair.Near || !LightPair.IsPair(out PlayerControl light, out PlayerControl shadow)) return null;
        return light.IsAlive() && shadow.IsAlive() ? shadow : null;
    }

    private static bool InRange(PlayerControl shadow, Vector2 pos)
    {
        // 3: 影の周囲の一定距離で起きたものだけ感知する
        return LightPair.SenseMode.GetValue() != 2 || FastVector2.DistanceWithinRange(shadow.Pos(), pos, LightPair.SenseRadius.GetFloat());
    }

    // Utils.TargetDies から呼ばれる (キルの気配)
    public static void OnAnyoneKilled(PlayerControl target)
    {
        if (GameStates.IsMeeting) return;

        PlayerControl shadow = GetSensor();
        if (!shadow || shadow.PlayerId == target.PlayerId) return;

        Vector2 pos = target.Pos();
        if (!InRange(shadow, pos)) return;

        // 同じ瞬間の複数死 (爆発・貫通) でフラッシュを死者の数だけ送らない
        long now = Environment.TickCount64;
        if (now >= NextFlashTick)
        {
            NextFlashTick = now + 500;
            shadow.KillFlash();
        }


        shadow.Notify(Utils.ColorString(LightPair.TeamColor, Translator.GetString("ShadowKillSensed")), LightPair.NoticeDuration.GetInt());
        AddArrow(shadow, pos);
    }

    // PlayerControlPatch の EnterVent から呼ばれる (ベントの気配)
    public static void OnAnyoneEnterVent(PlayerControl pc, Vent vent)
    {
        PlayerControl shadow = GetSensor();
        if (!shadow || shadow.PlayerId == pc.PlayerId) return;

        Vector2 pos = vent.transform.position;
        if (!InRange(shadow, pos)) return;

        shadow.Notify(Utils.ColorString(LightPair.TeamColor, Translator.GetString("ShadowVentSensed")), LightPair.NoticeDuration.GetInt());
        AddArrow(shadow, pos);
    }

    private static void AddArrow(PlayerControl shadow, Vector2 pos)
    {
        if (LightPair.SenseMode.GetValue() != 1) return;

        byte id = shadow.PlayerId;
        Vector3 at = pos;
        LocateArrow.Add(id, at);

        LateTask.New(() =>
        {
            LocateArrow.Remove(id, at);
            PlayerControl pc = Utils.GetPlayerById(id);
            if (pc && pc.IsAlive() && !GameStates.IsMeeting) Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc);
        }, LightPair.ArrowDuration.GetInt(), "ShadowPair.RemoveArrow", log: false);
    }
}
