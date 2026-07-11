using System;
using System.Collections.Generic;
using System.Linq;
using EndKnot.Modules;
using UnityEngine;

namespace EndKnot.Roles;

// 超波動砲の変種エンジン (JackalHadouHo / WaveCannon 共有)。
// フェーズ進行 (タイマー / スキン / 速度ロック / クールダウン) は各役職の既存ステートマシンが持ち、
// 本クラスは変種固有の CNO・挙動・キル判定だけを担当する。
// ライフサイクル: Begin → UpdateCharging → EnterWarning → UpdateWarning → EnterFiring → UpdateFiring → Despawn
// (会議 / 死亡での中断は役職側が Despawn を呼ぶ)
public class SuperCannonShot
{
    public enum Variant
    {
        BlackHole,
        Twin,
        Dynamic,
        CertainKill
    }

    // StringOptionItem 用の翻訳キー。並び順は PickVariant と対応。
    public static readonly string[] TypeOptionNames =
    [
        "SuperCannonTypeRandom",
        "SuperCannonTypeClassic",
        "SuperCannonTypeBlackHole",
        "SuperCannonTypeTwin",
        "SuperCannonTypeDynamic",
        "SuperCannonTypeCertainKill"
    ];

    // option 値 → 変種。Classic (=1) は null を返し、役職側の従来コード (太い赤ビーム) で撃つ。
    public static Variant? PickVariant(int optionValue)
    {
        return optionValue switch
        {
            0 => (Variant)IRandom.Instance.Next(0, 4),
            1 => null,
            _ => (Variant)(optionValue - 2)
        };
    }

    // 幾何定数は WaveCannon.cs / JackalHadouHo.cs の双子と同じ値 (視覚と判定の共通言語)
    private const int BeamCharCount = 20;
    private const int BeamSizeUnit = 30;
    private const float BeamHalfWidthUnit = 0.12f;
    private const float BeamLengthUnit = 0.015f;
    private const float BeamHitboxMargin = 0.85f;
    private const int WarningCharCount = 20;
    private const float GateForwardOffset = 1.5f;
    private const float GateUpOffset = 0.3f;
    private const float GateRadius = 0.5f;
    private const float BeamBackwardReach = GateForwardOffset + GateRadius;

    // ツイン: 2 本のビームラインの縦間隔
    private const float TwinGap = 2.5f;

    // ブラックホール: 引き寄せは tick 方式 (毎フレーム TP は SnapTo sid 汚染の実績があるため禁止)。
    // プレイヤー TP はラウンド共有の SnapTo cap (Utils.NumSnapToCallsThisRound 80/100) を消費するため、
    // per-player 間引きだけでは足りない — 1 tick の人数上限 (ラウンドロビン) + 1 発動あたりの総予算で
    // 合計消費を抑える (cap 枯渇 = そのラウンドの他役職 TP まで無音失敗する)。
    private const float PullInterval = 1.0f;
    private const float PullStep = 1.5f;
    private const float PullDeadzone = 1.2f;
    private const int PullPlayersPerTick = 5;
    private const int PullBudget = 45;

    // ダイナミック: 全幅ビームの文字数とマップ境界の余白
    private const int DynamicCharCount = 40;
    private const float DynamicBoundsExtend = 3f;

    public bool HasHit;

    private readonly PlayerControl Shooter;
    private readonly Variant Type;
    private readonly int Thickness;
    private readonly Func<PlayerControl, bool> IsImmune;
    private readonly HashSet<byte> AlreadyKilled = [];

    private Vector2 StartPosition;
    private Vector2 Direction;
    private float ShakeOffsetA;
    private float ShakeOffsetB;

    private WaveCannonGate GateA;
    private WaveCannonGate GateB;
    private WaveCannonWarning WarnA;
    private WaveCannonWarning WarnB;
    private WaveCannonBeamSegment BeamA;
    private WaveCannonBeamSegment BeamB;

    // CertainKill
    private byte CertainTargetId = byte.MaxValue;
    private Vector2 CertainGatePos;

    // BlackHole
    private float LastPullTime;
    private int PullSpent;
    private int PullRotation;

    // Dynamic
    private float MapCenterX;
    private float SweepStartY;
    private float SweepEndY;
    private float FiringStartTime;
    private float FiringDuration;

    public SuperCannonShot(PlayerControl shooter, Variant type, int thickness, Func<PlayerControl, bool> isImmune)
    {
        Shooter = shooter;
        Type = type;
        Thickness = thickness;
        IsImmune = isImmune;
    }

    private int FontSize => Thickness * BeamSizeUnit;
    private float LineHalfWidth => BeamHalfWidthUnit * Thickness + BeamHitboxMargin;
    private float LineBeamLength => BeamLengthUnit * FontSize * BeamCharCount;
    private float DynamicHalfLength => BeamLengthUnit * FontSize * DynamicCharCount / 2f;

    // チャージ開始。変種ごとのゲートを出す。false を返したら発動不能 (役職側はクラシック超にフォールバック)。
    public bool Begin(Vector2 startPosition, Vector2 direction)
    {
        StartPosition = startPosition;
        Direction = direction;

        switch (Type)
        {
            case Variant.BlackHole:
                Utils.CombineSendTimeLowering(() => { GateA = new WaveCannonGate(LineGatePos(0f), "#2a0033", "#4b0082", "#000000"); });
                return true;

            case Variant.Twin:
                Utils.CombineSendTimeLowering(() =>
                {
                    GateA = new WaveCannonGate(LineGatePos(TwinGap / 2f));
                    GateB = new WaveCannonGate(LineGatePos(-TwinGap / 2f));
                });
                return true;

            case Variant.Dynamic:
            {
                Dictionary<SystemTypes, Vector2>.ValueCollection rooms = RandomSpawn.SpawnMap.GetSpawnMap().Positions?.Values;
                if (rooms == null || rooms.Count == 0) return false;

                float[] xs = rooms.Select(r => r.x).ToArray();
                float[] ys = rooms.Select(r => r.y).ToArray();
                MapCenterX = (xs.Min() + xs.Max()) / 2f;
                float top = ys.Max() + DynamicBoundsExtend;
                float bottom = ys.Min() - DynamicBoundsExtend;
                bool topDown = IRandom.Instance.Next(2) == 0;
                SweepStartY = topDown ? top : bottom;
                SweepEndY = topDown ? bottom : top;

                Utils.CombineSendTimeLowering(() => { GateA = new WaveCannonGate(LineGatePos(0f)); });
                return true;
            }

            case Variant.CertainKill:
            {
                List<PlayerControl> candidates = Main.EnumerateAlivePlayerControls()
                    .Where(p => p.PlayerId != Shooter.PlayerId && !IsImmune(p) && !p.Is(CustomRoles.Pestilence) && !Pelican.IsEaten(p.PlayerId))
                    .ToList();
                if (candidates.Count == 0) return false;

                PlayerControl target = candidates[IRandom.Instance.Next(candidates.Count)];
                CertainTargetId = target.PlayerId;
                Direction = Vector2.right;
                CertainGatePos = CertainKillGatePos(target.GetTruePosition());
                Utils.CombineSendTimeLowering(() => { GateA = new WaveCannonGate(CertainGatePos, "#5e0000", "#cc0000", "#ff3333"); });
                return true;
            }

            default:
                return false;
        }
    }

    public void UpdateCharging()
    {
        if (Type == Variant.CertainKill) FollowCertainTarget();
    }

    public void EnterWarning()
    {
        switch (Type)
        {
            case Variant.BlackHole:
                Utils.CombineSendTimeLowering(() => { WarnA = new WaveCannonWarning(LineWarnPos(0f), DirectionalWarningSprite()); });
                break;

            case Variant.Twin:
                Utils.CombineSendTimeLowering(() =>
                {
                    WarnA = new WaveCannonWarning(LineWarnPos(TwinGap / 2f), DirectionalWarningSprite());
                    WarnB = new WaveCannonWarning(LineWarnPos(-TwinGap / 2f), DirectionalWarningSprite());
                });
                break;

            case Variant.Dynamic:
                Utils.CombineSendTimeLowering(() => { WarnA = new WaveCannonWarning(new Vector2(MapCenterX, SweepStartY), DynamicWarningSprite()); });
                break;

            case Variant.CertainKill:
                Utils.CombineSendTimeLowering(() => { WarnA = new WaveCannonWarning(CertainGatePos + Direction * GateRadius, DirectionalWarningSprite()); });
                break;
        }
    }

    public void UpdateWarning()
    {
        switch (Type)
        {
            case Variant.BlackHole:
                PullTick();
                break;

            case Variant.CertainKill:
                FollowCertainTarget();
                WarnA?.TP(CertainGatePos + Direction * GateRadius);
                break;
        }
    }

    public void EnterFiring(float firingDuration)
    {
        WarnA?.Despawn();
        WarnA = null;
        WarnB?.Despawn();
        WarnB = null;

        AlreadyKilled.Clear();
        ShakeOffsetA = UnityEngine.Random.Range(0f, 100f);
        ShakeOffsetB = UnityEngine.Random.Range(0f, 100f);

        switch (Type)
        {
            case Variant.BlackHole:
                Utils.CombineSendTimeLowering(() => { BeamA = new WaveCannonBeamSegment(LineBeamPos(0f), LineBeamSprite("#7f00ff")); });
                break;

            case Variant.Twin:
                Utils.CombineSendTimeLowering(() =>
                {
                    BeamA = new WaveCannonBeamSegment(LineBeamPos(TwinGap / 2f), LineBeamSprite("#ff0000"));
                    BeamB = new WaveCannonBeamSegment(LineBeamPos(-TwinGap / 2f), LineBeamSprite("#ff0000"));
                });
                break;

            case Variant.Dynamic:
                FiringStartTime = Time.time;
                FiringDuration = Mathf.Max(0.5f, firingDuration);
                Utils.CombineSendTimeLowering(() => { BeamA = new WaveCannonBeamSegment(new Vector2(MapCenterX, SweepStartY), DynamicBeamSprite()); });
                break;

            case Variant.CertainKill:
                // 発射突入時点の追従位置で固定して撃つ (直前まで追従しているため回避不能)
                Utils.CombineSendTimeLowering(() => { BeamA = new WaveCannonBeamSegment(CertainGatePos + Direction * GateRadius, LineBeamSprite("#ff0000")); });
                break;
        }
    }

    public void UpdateFiring()
    {
        switch (Type)
        {
            case Variant.BlackHole:
                PullTick();
                ShakeLineBeam(BeamA, LineBeamPos(0f), ShakeOffsetA);
                CheckLineKills(LineBeamOrigin(0f), "BlackHole");
                break;

            case Variant.Twin:
                ShakeLineBeam(BeamA, LineBeamPos(TwinGap / 2f), ShakeOffsetA);
                ShakeLineBeam(BeamB, LineBeamPos(-TwinGap / 2f), ShakeOffsetB);
                CheckLineKills(LineBeamOrigin(TwinGap / 2f), "TwinA");
                CheckLineKills(LineBeamOrigin(-TwinGap / 2f), "TwinB");
                break;

            case Variant.Dynamic:
            {
                float t = Mathf.Clamp01((Time.time - FiringStartTime) / FiringDuration);
                float y = Mathf.Lerp(SweepStartY, SweepEndY, t);
                BeamA?.TP(new Vector2(MapCenterX, y)); // 送信は ForceSnapMinInterval(0.1s) が間引く
                CheckDynamicKills(y);
                break;
            }

            case Variant.CertainKill:
                ShakeLineBeam(BeamA, CertainGatePos + Direction * GateRadius, ShakeOffsetA);
                CheckLineKills(CertainGatePos + Direction * GateRadius, "CertainKill");
                break;
        }
    }

    public void Despawn()
    {
        GateA?.Despawn();
        GateA = null;
        GateB?.Despawn();
        GateB = null;
        WarnA?.Despawn();
        WarnA = null;
        WarnB?.Despawn();
        WarnB = null;
        BeamA?.Despawn();
        BeamA = null;
        BeamB?.Despawn();
        BeamB = null;
    }

    // ---- 変種固有の挙動 ----

    private void FollowCertainTarget()
    {
        PlayerControl target = CertainTargetId.GetPlayer();
        if (target == null || !target.IsAlive()) return; // 対象死亡: 最終追従位置で固定

        CertainGatePos = CertainKillGatePos(target.GetTruePosition());
        GateA?.TP(CertainGatePos); // 送信は ForceSnapMinInterval が間引く
    }

    private void PullTick()
    {
        if (Time.time - LastPullTime < PullInterval) return;
        LastPullTime = Time.time;
        if (PullSpent >= PullBudget) return;

        Vector2 center = GatePosition();
        List<PlayerControl> players = Main.EnumerateAlivePlayerControls().Where(p => p.PlayerId != Shooter.PlayerId).ToList();
        if (players.Count == 0) return;

        var pulled = 0;
        for (var i = 0; i < players.Count && pulled < PullPlayersPerTick && PullSpent < PullBudget; i++)
        {
            PlayerControl p = players[(PullRotation + i) % players.Count];

            Vector2 pos = p.GetTruePosition();
            float dist = Vector2.Distance(pos, center);
            if (dist < PullDeadzone) continue;

            float step = Mathf.Min(PullStep, dist - PullDeadzone / 2f);
            Vector2 newPos = pos + ((center - pos).normalized * step);

            // 壁越えは引かない (壁内へ埋め込むと非モッドがスタックする)
            if (PhysicsHelpers.AnythingBetween(pos, newPos, Constants.ShipOnlyMask, false)) continue;

            // Utils.TP が inVent / ladder / movingPlat / AntiTP を弾く
            if (p.TP(newPos, log: false))
            {
                PullSpent++;
                pulled++;
            }
        }

        PullRotation = (PullRotation + PullPlayersPerTick) % players.Count;
    }

    private void ShakeLineBeam(WaveCannonBeamSegment beam, Vector2 basePos, float shakeOffset)
    {
        if (beam == null) return;

        float t = Time.time + shakeOffset;
        float wave = (Mathf.Sin(t * 47f) * 0.7f) + (Mathf.Sin(t * 113f + 1.3f) * 0.3f);
        float amp = BeamHalfWidthUnit * Thickness * 0.2f;
        Vector2 perp = new(-Direction.y, Direction.x);
        beam.TP(basePos + perp * (wave * amp)); // 送信は ForceSnapMinInterval(0.1s) が間引く
    }

    // ---- キル判定 ----

    private void CheckLineKills(Vector2 origin, string dbgKey)
    {
        // /hitbox 可視化: 下の判定式と同じ値をそのまま描く (ホストローカルのみ)
        HitboxDebug.DrawBeamRect($"Super.{Shooter.PlayerId}.{dbgKey}", origin, Direction, BeamBackwardReach, LineBeamLength + BeamHitboxMargin, LineHalfWidth);

        foreach (PlayerControl target in Main.EnumerateAlivePlayerControls())
        {
            if (!IsKillable(target)) continue;

            Vector2 delta = target.GetTruePosition() - origin;
            float along = Vector2.Dot(delta, Direction);
            if (along < -BeamBackwardReach || along > LineBeamLength + BeamHitboxMargin) continue;

            Vector2 lateral = delta - Direction * along;
            if (lateral.magnitude > LineHalfWidth) continue;

            Kill(target);
        }
    }

    private void CheckDynamicKills(float beamY)
    {
        // /hitbox 可視化: 下の判定式と同じ値をそのまま描く (ホストローカルのみ)
        HitboxDebug.DrawBand($"Super.{Shooter.PlayerId}.Dynamic", MapCenterX, beamY, DynamicHalfLength + BeamHitboxMargin, LineHalfWidth);

        foreach (PlayerControl target in Main.EnumerateAlivePlayerControls())
        {
            if (!IsKillable(target)) continue;

            Vector2 pos = target.GetTruePosition();
            if (Mathf.Abs(pos.y - beamY) > LineHalfWidth) continue;
            if (Mathf.Abs(pos.x - MapCenterX) > DynamicHalfLength + BeamHitboxMargin) continue;

            Kill(target);
        }
    }

    private bool IsKillable(PlayerControl target)
    {
        if (target.PlayerId == Shooter.PlayerId) return false;
        if (AlreadyKilled.Contains(target.PlayerId)) return false;
        if (IsImmune(target)) return false;
        if (Pelican.IsEaten(target.PlayerId)) return false;
        if (target.Is(CustomRoles.Pestilence)) return false;
        return true;
    }

    private void Kill(PlayerControl target)
    {
        AlreadyKilled.Add(target.PlayerId);

        // CheckMurder バイパスで確定キル (WaveCannon.CheckBeamKills と同じパターン)
        target.RpcExileV2();
        RPC.PlaySoundRPC(Shooter.PlayerId, Sounds.KillSound);

        PlayerState state = Main.PlayerStates[target.PlayerId];
        state.deathReason = PlayerState.DeathReason.Kill;
        state.RealKiller = (DateTime.Now, Shooter.PlayerId);
        state.SetDead();

        Utils.AfterPlayerDeathTasks(target);
        HasHit = true;
    }

    // ---- 幾何 ----

    private Vector2 GatePosition() => StartPosition + new Vector2(Direction.x * GateForwardOffset, GateUpOffset);
    private Vector2 LineGatePos(float yOffset) => GatePosition() + new Vector2(0f, yOffset);
    private Vector2 LineBeamOrigin(float yOffset) => LineGatePos(yOffset) + Direction * GateRadius;
    private Vector2 LineBeamPos(float yOffset) => LineBeamOrigin(yOffset);
    private Vector2 LineWarnPos(float yOffset) => LineBeamOrigin(yOffset);
    private Vector2 CertainKillGatePos(Vector2 targetPos) => targetPos + new Vector2(Direction.x * GateForwardOffset, GateUpOffset);

    // ---- スプライト ----

    private string DirectionalWarningSprite()
    {
        // 区切り 3 スペース: 5 だと公式鯖の ~680B パケット制限を超えてキック (WaveCannon 側と同じ)
        string chars = string.Join("   ", Enumerable.Repeat("⚠", WarningCharCount));
        bool firingRight = Direction.x > 0;
        return firingRight
            ? $"<size={FontSize}><alpha=#00>{chars}<color=#FFFF00FF>{chars}</color></size>"
            : $"<size={FontSize}><color=#FFFF00FF>{chars}</color><alpha=#00>{chars}</size>";
    }

    private string DynamicWarningSprite()
    {
        // 全幅掃引の予告: padding なし中央寄せ (CNO 中心 = マップ中央 X)
        // 区切り 3 スペース: 5 だと ⚠×40 で 360B になり公式鯖の ~680B パケット制限を超えてキック
        string chars = string.Join("   ", Enumerable.Repeat("⚠", DynamicCharCount));
        return $"<size={FontSize}><color=#FFFF00FF>{chars}</color></size>";
    }

    private string LineBeamSprite(string color)
    {
        string row = new('━', BeamCharCount);
        bool firingRight = Direction.x > 0;
        string transparent = $"<alpha=#00>{row}";
        string visible = $"<color={color}>{row}</color>";
        return firingRight
            ? $"<size={FontSize}>{transparent}{visible}</size>"
            : $"<size={FontSize}>{visible}{transparent}</size>";
    }

    private string DynamicBeamSprite()
    {
        // 全幅ビーム: padding なし中央寄せ
        string row = new('━', DynamicCharCount);
        return $"<size={FontSize}><color=#ff0000>{row}</color></size>";
    }
}
