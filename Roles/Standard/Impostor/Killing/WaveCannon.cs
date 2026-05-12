using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AmongUs.GameOptions;
using EndKnot.Modules;
using UnityEngine;
using static EndKnot.Translator;

namespace EndKnot.Roles;

public class WaveCannon : RoleBase
{
    private const int Id = 703500;
    public static bool On;
    private static List<WaveCannon> Instances = [];

    public static OptionItem AbilityCooldown;
    private static OptionItem ChargeDuration;
    private static OptionItem WarningDuration;
    private static OptionItem FiringDuration;
    private static OptionItem BeamThickness;
    private static OptionItem FriendlyFire;

    private const int BeamCharCount = 20;
    private const int BeamSizeUnit = 30; // 1 thickness 単位あたりのフォントサイズ
    private const float BeamHalfWidthUnit = 0.075f; // 1 thickness 単位あたりの判定半厚 (世界座標)
    private const float BeamLengthUnit = 0.015f; // 1 char 1 size 単位ありの世界距離
    private const float BeamHitboxMargin = 0.5f;   // プレイヤー本体半径ぶんの判定マージン
    private const int WarningCharCount = 20;

    // ゲート (発射元の portal 風 CNO) のレイアウト
    private const float GateForwardOffset = 1.5f; // プレイヤー中心からゲート中心までの前方距離
    private const float GateUpOffset = 0.3f;      // ゲートの縦位置 (胸の高さ)
    private const float GateRadius = 0.5f;        // ゲート半径 = ビーム発射点までのオフセット (size 140% に対応)

    private const float BeamBackwardReach = GateForwardOffset + GateRadius; // 1.5 unit、根本判定をプレイヤー位置まで伸ばす

    private enum Phase { Idle, Charging, Warning, Firing }

    private Phase CurrentPhase;
    private long PhaseEndTS;
    private Vector2 StartPosition;
    private Vector2 Direction;
    private Vector2 LastTrackedPos;
    private NetworkedPlayerInfo.PlayerOutfit OriginalOutfit;
    private readonly HashSet<byte> AlreadyKilled = [];
    private bool PhaseEntryDone;
    private float ShakePhaseOffset;
    private WaveCannonWarning WarningCNO;
    private WaveCannonBeamSegment BeamCNO;
    private WaveCannonGate GateCNO;
    private PlayerControl WaveCannonPC;

    public override bool IsEnable => On;

    public override void SetupCustomOption()
    {
        StartSetup(Id)
            .AutoSetupOption(ref AbilityCooldown, 30, new IntegerValueRule(10, 60, 1), OptionFormat.Seconds)
            .AutoSetupOption(ref ChargeDuration, 3, new IntegerValueRule(1, 10, 1), OptionFormat.Seconds)
            .AutoSetupOption(ref WarningDuration, 1, new IntegerValueRule(1, 5, 1), OptionFormat.Seconds)
            .AutoSetupOption(ref FiringDuration, 3, new IntegerValueRule(1, 10, 1), OptionFormat.Seconds)
            .AutoSetupOption(ref BeamThickness, 2, new IntegerValueRule(1, 8, 1), OptionFormat.Times)
            .AutoSetupOption(ref FriendlyFire, false);
    }

    public override void Init()
    {
        On = false;
        Instances = [];
    }

    public override void Add(byte playerId)
    {
        On = true;
        Instances.Add(this);
        WaveCannonPC = playerId.GetPlayer();
        ResetState();
    }

    public override void Remove(byte playerId)
    {
        Instances.RemoveAll(x => x.WaveCannonPC?.PlayerId == playerId);
        if (Instances.Count == 0) On = false;
    }

    private void ResetState()
    {
        CurrentPhase = Phase.Idle;
        PhaseEndTS = 0;
        StartPosition = Vector2.zero;
        Direction = Vector2.right;
        LastTrackedPos = Vector2.zero;
        AlreadyKilled.Clear();
        PhaseEntryDone = false;
        DespawnAllCNOs();
    }

    private void DespawnAllCNOs()
    {
        WarningCNO?.Despawn();
        WarningCNO = null;
        BeamCNO?.Despawn();
        BeamCNO = null;
        GateCNO?.Despawn();
        GateCNO = null;
    }

    // 発動はファントムボタン (vanish) のみに統一する。
    // 非モッド側で pet 撫でアニメ → cosmetics.FlipX 書換 → 方向制御不可になるため
    // OnPet は意図的に削除。OnShapeshift も Phantom basis では呼ばれないが念のため残す
    public override bool OnShapeshift(PlayerControl shapeshifter, PlayerControl target, bool shapeshifting)
    {
        if (!shapeshifting) return true;
        TriggerCharge(shapeshifter);
        return false;
    }

    public override bool OnVanish(PlayerControl pc)
    {
        TriggerCharge(pc);
        return false;
    }

    private void TriggerCharge(PlayerControl pc)
    {
        if (!pc.IsAlive() || CurrentPhase != Phase.Idle || !GameStates.IsInTask) return;

        StartPosition = pc.GetTruePosition();
        // pc.cosmetics.FlipX は非モッド側で pet animation により書き換わるため使えない。
        // OnFixedUpdate の Idle 中位置追跡で更新した Direction をそのまま使う
        EnterCharging(pc);
    }

    private void EnterCharging(PlayerControl pc)
    {
        CurrentPhase = Phase.Charging;
        PhaseEndTS = Utils.TimeStamp + ChargeDuration.GetInt();
        PhaseEntryDone = false;

        byte id = pc.PlayerId;
        if (Camouflage.PlayerSkins.TryGetValue(id, out var original))
        {
            OriginalOutfit = original;
            var chargeOutfit = new NetworkedPlayerInfo.PlayerOutfit()
                .Set(original.PlayerName, original.ColorId, "", "skin_rhm", "", "", "");
            Camouflage.PlayerSkins[id] = chargeOutfit;
            Camouflage.BlockCamouflage = true;
            if (!Camouflage.IsCamouflage)
                Utils.RpcChangeSkin(pc, chargeOutfit);
        }

        Vector2 gatePos = GatePosition();
        Utils.CombineSendTimeLowering(() =>
        {
            GateCNO = new WaveCannonGate(gatePos);
        });
    }

    private void RestoreSkin(PlayerControl pc)
    {
        if (OriginalOutfit == null) return;
        Camouflage.PlayerSkins[pc.PlayerId] = OriginalOutfit;
        Camouflage.BlockCamouflage = false;
        if (!Camouflage.IsCamouflage)
            Utils.RpcChangeSkin(pc, OriginalOutfit);
        OriginalOutfit = null;
    }

    private void RestoreSkinDeathPreserve(PlayerControl pc)
    {
        if (OriginalOutfit == null) return;
        Camouflage.PlayerSkins[pc.PlayerId] = OriginalOutfit;
        Camouflage.BlockCamouflage = false;
        OriginalOutfit = null;
    }

    public override void OnFixedUpdate(PlayerControl pc)
    {
        if (!GameStates.IsInTask) return;

        if (!pc.IsAlive())
        {
            if (CurrentPhase != Phase.Idle)
            {
                DespawnAllCNOs();
                RestoreSkinDeathPreserve(pc);
                CurrentPhase = Phase.Idle;
            }
            return;
        }

        if (CurrentPhase == Phase.Idle)
        {
            Vector2 pos = pc.GetTruePosition();
            if (LastTrackedPos == Vector2.zero) { LastTrackedPos = pos; return; }
            float deltaX = pos.x - LastTrackedPos.x;
            if (Mathf.Abs(deltaX) > 0.05f)
                Direction = deltaX > 0 ? Vector2.right : Vector2.left;
            LastTrackedPos = pos;
            return;
        }

        pc.TP(StartPosition, log: false);

        switch (CurrentPhase)
        {
            case Phase.Charging:
                if (Utils.TimeStamp >= PhaseEndTS)
                {
                    CurrentPhase = Phase.Warning;
                    PhaseEndTS = Utils.TimeStamp + WarningDuration.GetInt();
                    PhaseEntryDone = false;
                }
                break;

            case Phase.Warning:
                if (!PhaseEntryDone)
                {
                    SpawnWarning();
                    PhaseEntryDone = true;
                }
                if (Utils.TimeStamp >= PhaseEndTS)
                {
                    CurrentPhase = Phase.Firing;
                    PhaseEndTS = Utils.TimeStamp + FiringDuration.GetInt();
                    PhaseEntryDone = false;
                    ShakePhaseOffset = UnityEngine.Random.Range(0f, 100f);
                }
                break;

            case Phase.Firing:
                if (!PhaseEntryDone)
                {
                    DespawnWarning();
                    SpawnBeam();
                    AlreadyKilled.Clear();
                    PhaseEntryDone = true;
                }

                if (BeamCNO != null)
                {
                    float t = Time.time + ShakePhaseOffset;
                    float wave = Mathf.Sin(t * 47f) * 0.7f + Mathf.Sin(t * 113f + 1.3f) * 0.3f;
                    // 振幅も halfWidth と同じスケール (BeamHalfWidthUnit 比例) に追従
                    float amp = BeamHalfWidthUnit * BeamThickness.GetInt() * 0.2f;
                    Vector2 perp = new(-Direction.y, Direction.x);
                    BeamCNO.Position = BeamCNOPosition() + perp * (wave * amp);
                }

                CheckBeamKills(pc);
                if (Utils.TimeStamp >= PhaseEndTS)
                {
                    DespawnAllCNOs();
                    RestoreSkin(pc);
                    pc.SetKillCooldown();
                    // Phantom cooldown が AU 側で自動でかかるので AddAbilityCD 不要
                    CurrentPhase = Phase.Idle;
                }
                break;
        }
    }

    private Vector2 GatePosition()
    {
        // ゲート中心: プレイヤーから前方 GateForwardOffset、上方 GateUpOffset
        return StartPosition + new Vector2(Direction.x * GateForwardOffset, GateUpOffset);
    }

    private Vector2 BeamStartPoint()
    {
        // 描画と判定の共通起点 = ゲートの前端 (発射方向側の縁)
        return GatePosition() + Direction * GateRadius;
    }

    private Vector2 BeamCNOPosition()
    {
        // padding ありで TMP は visible+padding 全体を CNO 中心に配置 → visible 開始点 = CNO 中心
        return BeamStartPoint();
    }

    private Vector2 WarningCNOPosition()
    {
        // padding パターンで visible 開始 = CNO 中心。⚠ visible は gate 前端から発射方向に伸びる
        return BeamStartPoint();
    }

    private void SpawnWarning()
    {
        Vector2 pos = WarningCNOPosition();
        string sprite = WarningSprite();
        Utils.CombineSendTimeLowering(() =>
        {
            WarningCNO = new WaveCannonWarning(pos, sprite);
        });
    }

    private void DespawnWarning()
    {
        WarningCNO?.Despawn();
        WarningCNO = null;
    }

    private void SpawnBeam()
    {
        Vector2 pos = BeamCNOPosition();
        string sprite = BeamSprite();
        Utils.CombineSendTimeLowering(() =>
        {
            BeamCNO = new WaveCannonBeamSegment(pos, sprite);
        });
    }

    private string WarningSprite()
    {
        // padding パターンで発射方向側のみに ⚠ を表示 (反対側は <alpha=#00> で透明)
        string chars = string.Join("     ", Enumerable.Repeat("⚠", WarningCharCount));
        bool firingRight = Direction.x > 0;
        return firingRight
            ? $"<size=16><alpha=#00>{chars}<color=#FFFF00FF>{chars}</color></size>"
            : $"<size=16><color=#FFFF00FF>{chars}</color><alpha=#00>{chars}</size>";
    }

    private string BeamSprite()
    {
        // padding 込み (visible のみだと TMP 実描画幅と推定値が一致せず位置がずれる)
        // padding 透明化は Tree.cs 流の <alpha=#00> を使用 → font outline も含めて完全透明
        string row = new('━', BeamCharCount);
        bool firingRight = Direction.x > 0;
        int size = BeamThickness.GetInt() * BeamSizeUnit;
        string transparent = $"<alpha=#00>{row}";
        string visible = $"<color=#FF6600FF>{row}</color>";
        return firingRight
            ? $"<size={size}>{transparent}{visible}</size>"
            : $"<size={size}>{visible}{transparent}</size>";
    }

    private void CheckBeamKills(PlayerControl shooter)
    {
        int thick = BeamThickness.GetInt();
        int size = thick * BeamSizeUnit;
        // visible 開始点 (ゲート前端) と一致 → 視覚と判定がぴったり揃う
        Vector2 eyePos = BeamStartPoint();
        // プレイヤー本体半径 + ゲート垂直オフセット を吸収するマージン
        float halfWidth = BeamHalfWidthUnit * thick + BeamHitboxMargin;
        float beamLength = BeamLengthUnit * size * BeamCharCount;

        foreach (PlayerControl target in Main.EnumerateAlivePlayerControls())
        {
            if (target.PlayerId == shooter.PlayerId) continue;
            if (AlreadyKilled.Contains(target.PlayerId)) continue;
            if (!FriendlyFire.GetBool() && target.GetCustomRole().IsImpostor()) continue;
            if (Pelican.IsEaten(target.PlayerId)) continue;
            if (target.Is(CustomRoles.Pestilence)) continue;

            Vector2 delta = target.GetTruePosition() - eyePos;
            float along = Vector2.Dot(delta, Direction);
            // 根本側はプレイヤー位置まで、前端は本体半径ぶんの猶予を持たせる
            if (along < -BeamBackwardReach || along > beamLength + BeamHitboxMargin) continue;

            Vector2 lateral = delta - Direction * along;
            if (lateral.magnitude > halfWidth) continue;

            AlreadyKilled.Add(target.PlayerId);

            // CheckMurder バイパスで確定キル (Abyssbringer.cs:183-196 と同じパターン)
            target.RpcExileV2();
            RPC.PlaySoundRPC(shooter.PlayerId, Sounds.KillSound);

            PlayerState state = Main.PlayerStates[target.PlayerId];
            state.deathReason = PlayerState.DeathReason.Kill;
            state.RealKiller = (DateTime.Now, shooter.PlayerId);
            state.SetDead();

            Utils.AfterPlayerDeathTasks(target);
        }
    }

    public override void OnReportDeadBody()
    {
        if (CurrentPhase == Phase.Idle) return;
        DespawnAllCNOs();
        if (WaveCannonPC != null) RestoreSkin(WaveCannonPC);
        CurrentPhase = Phase.Idle;
        PhaseEntryDone = false;
        AlreadyKilled.Clear();
    }

    public override void ApplyGameOptions(IGameOptions opt, byte playerId)
    {
        // Phantom basis 化に伴い、AU の vanish/phantom cooldown を発動クールタイムに使う。
        // PhantomDuration は 0.1s (実質瞬間 vanish→appear) にして可視性を維持。
        // PhantomCooldown は (charge+warning+firing) 全体時間 + AbilityCooldown にして、
        // 発射シーケンス終了後におよそ AbilityCooldown 秒の待機が残るようにする。
        AURoleOptions.PhantomDuration = 0.1f;
        AURoleOptions.PhantomCooldown =
            ChargeDuration.GetFloat() + WarningDuration.GetFloat() + FiringDuration.GetFloat()
            + AbilityCooldown.GetFloat();
        Logger.Info($"id={playerId} ability={AbilityCooldown.GetFloat()} charge={ChargeDuration.GetFloat()} warn={WarningDuration.GetFloat()} fire={FiringDuration.GetFloat()} → PhantomCD={AURoleOptions.PhantomCooldown}", "WaveCannon.ApplyGameOptions");
    }
}
