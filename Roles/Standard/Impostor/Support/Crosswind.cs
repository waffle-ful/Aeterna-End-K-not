using System;
using AmongUs.GameOptions;
using EndKnot.Modules;
using EndKnot.Patches;
using UnityEngine;

namespace EndKnot.Roles;

public class Crosswind : RoleBase
{
    private const int Id = 705500;
    public static bool On;

    private static OptionItem KillCooldown;
    private static OptionItem AbilityCooldown;
    private static OptionItem SlideDistance;
    private static OptionItem AbilityUseLimit;
    private static OptionItem AbilityUseGainWithEachKill;

    // 距離の先が壁ならより近い距離へ段階的にフォールバック (Dossun.CheckBlockHits と同じノックバック処理)
    private static readonly float[] KnockbackFallbackMultipliers = [1f, 0.66f, 0.33f];

    // Utils.TP は移動距離が 1.5u 未満だと SendOption.None へ降格する (Utils.cs:202-207) = クライアントに届かない
    // 可能性がある一方、NumSnapToCallsThisRound は降格後も加算される (:253)。届かない TP でラウンド共有の
    // SnapTo 予算だけ削るのを避けるため、短すぎるフォールバック段は最初から撃たない。
    private const float MinReliableTpDistance = 1.6f;
    private const float DirectionTrackThreshold = 0.05f;

    private Vector2 Direction;
    private Vector2 LastTrackedPos;

    public override bool IsEnable => On;

    public override void SetupCustomOption()
    {
        StartSetup(Id)
            .AutoSetupOption(ref KillCooldown, 30, new IntegerValueRule(1, 120, 1), OptionFormat.Seconds)
            .AutoSetupOption(ref AbilityCooldown, 45, new IntegerValueRule(10, 120, 1), OptionFormat.Seconds)
            .AutoSetupOption(ref SlideDistance, 5f, new FloatValueRule(2f, 15f, 0.5f), OptionFormat.Multiplier)
            .AutoSetupOption(ref AbilityUseLimit, 3f, new FloatValueRule(0f, 20f, 0.05f), OptionFormat.Times)
            .AutoSetupOption(ref AbilityUseGainWithEachKill, 0.5f, new FloatValueRule(0f, 5f, 0.25f), OptionFormat.Times);
    }

    public override void Init()
    {
        On = false;
    }

    public override void Add(byte playerId)
    {
        On = true;
        Direction = Vector2.right;
        LastTrackedPos = Vector2.zero;
        playerId.SetAbilityUseLimit(AbilityUseLimit.GetFloat());
    }

    public override void SetKillCooldown(byte id)
    {
        Main.AllPlayerKillCooldown[id] = KillCooldown.GetFloat();
    }

    public override void ApplyGameOptions(IGameOptions opt, byte playerId)
    {
        // Phantom basis 化に伴い、AU の vanish/phantom cooldown を発動クールタイムに使う (WaveCannon と同型)。
        // AbilityCooldown の最小値 (10) は PreventKill の 10 秒ガードを僅かに下回りうるため、
        // イントロ直後は 12 秒未満にならないようクランプする (EvilBomber 等と同じパターン)。
        float cd = AbilityCooldown.GetFloat();
        if (IntroCutsceneDestroyPatch.PreventKill) cd = Mathf.Max(cd, 12f);

        AURoleOptions.PhantomDuration = 0.1f;
        AURoleOptions.PhantomCooldown = cd;
    }

    // 自分自身の左右移動方向を追跡し、発動時の押し出し方向として使う (WaveCannon の Idle/DirectionDetect tracking と同じ考え方)
    public override void OnFixedUpdate(PlayerControl pc)
    {
        if (!AmongUsClient.Instance.AmHost) return;
        if (!pc.IsAlive() || !GameStates.IsInTask) return;

        Vector2 pos = pc.GetTruePosition();
        if (LastTrackedPos == Vector2.zero)
        {
            LastTrackedPos = pos;
            return;
        }

        float deltaX = pos.x - LastTrackedPos.x;
        if (Mathf.Abs(deltaX) > DirectionTrackThreshold)
            Direction = deltaX > 0 ? Vector2.right : Vector2.left;
        LastTrackedPos = pos;
    }

    // 会議で全員スポーンへワープするため、直前の追跡位置を残すと復帰直後の deltaX がワープ量になり
    // 「直前の移動方向」の意図から外れる (WaveCannon/Dossun も会議時に状態をリセットするパターン)
    public override void OnReportDeadBody()
    {
        LastTrackedPos = Vector2.zero;
        Direction = Vector2.right;
    }

    public override bool OnVanish(PlayerControl pc)
    {
        if (!pc.IsAlive() || !GameStates.IsInTask) return false;

        if (pc.GetAbilityUseLimit() < 1f)
        {
            pc.Notify(Translator.GetString("Crosswind.NoUsesLeft"));
            return false;
        }

        pc.RpcRemoveAbilityUse();
        Gust(pc);

        // 対象への Notify は行わない (14 人同時 Notify = SetName RPC バースト回避)。本人のみ通知
        pc.Notify(string.Format(Translator.GetString("Crosswind.Activated"), Math.Round(pc.GetAbilityUseLimit(), 2)));
        return false;
    }

    private void Gust(PlayerControl pc)
    {
        Vector2 dir = Direction;
        float knockDist = SlideDistance.GetFloat();

        // スナップショットで列挙 (Dossun/WaveCannon と同じ理由 — 途中で生存キャッシュが変わっても影響を受けない)
        foreach (PlayerControl target in Main.AllAlivePlayerControlsToArray)
        {
            if (target.PlayerId == pc.PlayerId) continue;
            if (target.PlayerId >= 200) continue; // CNO 除外
            if (!target.IsAlive()) continue;

            // 被食者は IsAlive() のまま黒部屋に固定されており、吹き飛ばしても Pelican.OnFixedUpdate が
            // 引き戻すだけの二重 TP になる (WaveCannon/EvilBomber と同じ除外)
            if (Pelican.IsEaten(target.PlayerId)) continue;

            // 壁チェックは足元 (GetTruePosition = transform + Collider.offset) から、TP 先は body center (Pos) を基準にする。
            // Utils.TP → SnapTo は transform 空間に書くので、足元基準の座標をそのまま渡すと横移動に加えて毎回 0.36u 沈み、
            // 発動のたびに累積する (壁チェックした高さより下へ着地して地形にめり込む)
            Vector2 victimPos = target.GetTruePosition();
            Vector2 tpBase = target.Pos();

            foreach (float mult in KnockbackFallbackMultipliers)
            {
                float dist = knockDist * mult;

                // 倍率は降順なので、ここで短くなったら以降の段も全て短い = 押し出しを諦める
                if (dist < MinReliableTpDistance) break;

                if (!PhysicsHelpers.AnyNonTriggersBetween(victimPos, dir, dist, Constants.ShipAndObjectsMask))
                {
                    target.TP(tpBase + dir * dist);
                    break;
                }
            }
        }
    }
}
