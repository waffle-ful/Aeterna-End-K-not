using System;
using System.Collections.Generic;
using AmongUs.GameOptions;
using EndKnot.Modules;
using UnityEngine;

namespace EndKnot.Roles;

// コンビ役職の主役職 (光)。相方の影が近くにいる間、二人の視界が広がる。
// 片方が吊り以外で死ぬと相方も後を追い、追放された時は残った方が能力を失って別の役職へ変わる。
// オプションは相方の分も含めてここにぶら下げる (Modules/CombinationRoles.cs の Pairs 登録)。
public class LightPair : RoleBase
{
    public static bool On;
    public override bool IsEnable => On;

    public static byte LightId = byte.MaxValue;
    public static byte ShadowId = byte.MaxValue;

    // 二人が有効距離内にいるか。距離の判定は 0.5 秒ごとで、切り替わった時だけ設定を送り直す
    public static bool Near;
    private static long NextCheckTick;

    // 会議中・追放演出中は死亡を適用できないので、会議明けまで預かる
    private static readonly HashSet<byte> PendingFollow = [];
    private static readonly HashSet<byte> PendingChange = [];

    public const string TeamColorHex = "#fff2a8";
    public static readonly Color32 TeamColorValue = new(0xff, 0xf2, 0xa8, 0xff);
    public static Color TeamColor => TeamColorValue;

    public static OptionItem Distance;
    public static OptionItem VisionBonus;
    public static OptionItem SenseMode;
    public static OptionItem SenseRadius;
    public static OptionItem NoticeDuration;
    public static OptionItem ArrowDuration;
    public static OptionItem RoleAfterExile;

    public override void SetupCustomOption()
    {
        StartSetup(710200)
            .AutoSetupOption(ref Distance, 3f, new FloatValueRule(0.5f, 15f, 0.5f), overrideName: "LightPair.Distance")
            .AutoSetupOption(ref VisionBonus, 0.5f, new FloatValueRule(0.1f, 3f, 0.1f), overrideName: "LightPair.VisionBonus")
            .AutoSetupOption(ref SenseMode, 0, new[] { "LightPair.SenseMode.0", "LightPair.SenseMode.1", "LightPair.SenseMode.2" }, overrideName: "LightPair.SenseMode")
            .AutoSetupOption(ref SenseRadius, 8f, new FloatValueRule(1f, 30f, 0.5f), overrideName: "LightPair.SenseRadius")
            .AutoSetupOption(ref NoticeDuration, 3, new IntegerValueRule(1, 15, 1), OptionFormat.Seconds, overrideName: "LightPair.NoticeDuration")
            .AutoSetupOption(ref ArrowDuration, 5, new IntegerValueRule(1, 30, 1), OptionFormat.Seconds, overrideName: "LightPair.ArrowDuration")
            .AutoSetupOption(ref RoleAfterExile, 1, PairRoleChange.OptionNames(), overrideName: "LightPair.RoleAfterExile", noTranslation: true);
    }

    public override void Init()
    {
        On = false;
        LightId = byte.MaxValue;
        // ShadowId は影の Init が消す (影が先に配られた時に、ここで消すと相方を見失う)
        Near = false;
        NextCheckTick = 0;
        PendingFollow.Clear();
        PendingChange.Clear();
    }

    public override void Add(byte playerId)
    {
        On = true;
        LightId = playerId;
    }

    public override void Remove(byte playerId)
    {
        if (LightId == playerId) LightId = byte.MaxValue;
        SetNear(false);
        On = LightId != byte.MaxValue;
    }

    public override void ApplyGameOptions(IGameOptions opt, byte playerId)
    {
        ApplyVision(opt);
    }

    public static void ApplyVision(IGameOptions opt)
    {
        if (!Near) return;
        opt.SetFloat(FloatOptionNames.CrewLightMod, opt.GetFloat(FloatOptionNames.CrewLightMod) + VisionBonus.GetFloat());
    }

    public override void OnFixedUpdate(PlayerControl pc)
    {
        if (!AmongUsClient.Instance.AmHost || pc.PlayerId != LightId || !Main.IntroDestroyed || !GameStates.IsInTask || ExileController.Instance) return;

        long now = Environment.TickCount64;
        if (now < NextCheckTick) return;
        NextCheckTick = now + 500;

        UpdateNear();
    }

    public static bool IsPair(out PlayerControl light, out PlayerControl shadow)
    {
        light = LightId == byte.MaxValue ? null : Utils.GetPlayerById(LightId);
        shadow = ShadowId == byte.MaxValue ? null : Utils.GetPlayerById(ShadowId);
        return light && shadow && light.Is(CustomRoles.LightPair) && shadow.Is(CustomRoles.ShadowPair);
    }

    private static void UpdateNear()
    {
        bool near = false;

        if (IsPair(out PlayerControl light, out PlayerControl shadow) && light.IsAlive() && shadow.IsAlive())
        {
            // 離れる側の判定だけ 0.5 広げ、境界で立ち止まった時に設定送信が毎回往復しないようにする
            float range = Near ? Distance.GetFloat() + 0.5f : Distance.GetFloat();
            near = FastVector2.DistanceWithinRange(light.Pos(), shadow.Pos(), range);
        }

        SetNear(near);
    }

    private static void SetNear(bool near)
    {
        if (Near == near) return;
        Near = near;

        if (!AmongUsClient.Instance.AmHost || !GameStates.InGame) return;

        PlayerControl light = LightId == byte.MaxValue ? null : Utils.GetPlayerById(LightId);
        PlayerControl shadow = ShadowId == byte.MaxValue ? null : Utils.GetPlayerById(ShadowId);
        if (light) light.MarkDirtySettings();
        if (shadow) shadow.MarkDirtySettings();
    }

    // PlayerState.SetDead から呼ばれる。終了判定より前に相方の後追いを済ませる
    public static void OnAnyoneSetDead(byte deadId)
    {
        if (!On || !AmongUsClient.Instance.AmHost) return;
        if (deadId != LightId && deadId != ShadowId) return;
        if (!IsPair(out PlayerControl light, out PlayerControl shadow)) return;

        SetNear(false);

        PlayerControl partner = deadId == LightId ? shadow : light;
        if (!partner || !partner.IsAlive() || partner.Data.IsDead) return;

        if (!Main.PlayerStates.TryGetValue(deadId, out PlayerState state)) return;

        if (state.deathReason == PlayerState.DeathReason.Vote)
        {
            PendingChange.Add(partner.PlayerId);
            return;
        }

        Logger.Info($"{partner.GetNameWithRole().RemoveHtmlTags()} follows {deadId} in death", "LightPair");

        if (!GameStates.IsInTask || GameStates.IsMeeting || ExileController.Instance)
        {
            PendingFollow.Add(partner.PlayerId);
            return;
        }

        GameEndChecker.HoldCheckWhile(() => partner.Suicide(PlayerState.DeathReason.FollowingSuicide));
    }

    public override void AfterMeetingTasks()
    {
        ProcessPending();
    }

    public static void ProcessPending()
    {
        if (!AmongUsClient.Instance.AmHost || (PendingFollow.Count == 0 && PendingChange.Count == 0)) return;

        byte[] follow = [.. PendingFollow];
        byte[] change = [.. PendingChange];
        PendingFollow.Clear();
        PendingChange.Clear();

        GameEndChecker.HoldCheckWhile(() =>
        {
            foreach (byte id in follow)
            {
                PlayerControl pc = Utils.GetPlayerById(id);
                if (!pc || !pc.IsAlive() || pc.Data.IsDead || !(pc.Is(CustomRoles.LightPair) || pc.Is(CustomRoles.ShadowPair))) continue;

                // AfterMeetingTasks は追放演出がまだ残っている間に走り、Suicide() はそこで無言で止まる。
                // 追放と同じ手順 (RpcExileV2 → 死亡の確定) で直接落とす
                PlayerState state = Main.PlayerStates[id];
                pc.SetRealKiller(pc);
                state.deathReason = PlayerState.DeathReason.FollowingSuicide;
                pc.RpcExileV2();
                pc.Data.IsDead = true;
                state.SetDead();
                Utils.AfterPlayerDeathTasks(pc);
            }
        });

        foreach (byte id in change)
        {
            PlayerControl pc = Utils.GetPlayerById(id);
            if (pc && pc.IsAlive() && (pc.Is(CustomRoles.LightPair) || pc.Is(CustomRoles.ShadowPair)))
                PairRoleChange.Apply(pc, RoleAfterExile.GetValue());
        }

        GameEndChecker.ForceCheckEnd();
    }

    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud = false, bool meeting = false)
    {
        if (LightId == byte.MaxValue || ShadowId == byte.MaxValue) return string.Empty;

        bool pair = (seer.PlayerId == LightId && target.PlayerId == ShadowId) || (seer.PlayerId == ShadowId && target.PlayerId == LightId);
        return pair ? Utils.ColorString(TeamColor, "☆") : string.Empty;
    }

    public override bool KnowRole(PlayerControl seer, PlayerControl target)
    {
        if ((seer.Is(CustomRoles.LightPair) && target.Is(CustomRoles.ShadowPair)) || (seer.Is(CustomRoles.ShadowPair) && target.Is(CustomRoles.LightPair))) return true;

        return base.KnowRole(seer, target);
    }
}
