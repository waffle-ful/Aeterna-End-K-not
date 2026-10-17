using System.Collections.Generic;
using AmongUs.GameOptions;
using EndKnot.Modules;
using EndKnot.Patches;
using UnityEngine;

namespace EndKnot.Roles;

// Ported from sijimi2222/TownOfHost-Shell
// Based on: TeamUchuaddon/UchuAddon (Nova)
// 押すと本人の足が鈍り、代わりに本人の周りへ出た☆が、歩こうとした向きへ回り込む。もう一度押すと
// ☆の位置から「本人 → ☆」の向きへ大きな矢を 1 発撃ち、最初にぶつかった相手を倒す。
// ホスト専用なので方向キーの入力そのものは届かない — 届くのは位置だけ。完全に止めると向きが読めないので
// 速度を落として「じわじわ動いた差分」の向きを読む (Car / Dossun と同じく位置の差分が入力の代わり)。
// 発動トリガ (ペット/Shapeshift/Vanish の三系統) は Atlas と同じ形。
public class Nova : RoleBase
{
    private const int Id = 709600;
    public static bool On;

    private static OptionItem KillCooldown;
    public static OptionItem AbilityCooldown;
    public static OptionItem AbilityDuration;
    private static OptionItem OrbitSpeed;
    private static OptionItem SlowMultiplier;
    private static OptionItem ArrowSpeed;
    private static OptionItem ArrowRange;
    private static OptionItem ArrowPierce;
    private static OptionItem ArrowFriendlyFire;
    private static OptionItem OrbitRadius;
    private static OptionItem ArrowSize;

    // 位置の差分を取る間隔。☆と矢の TP も同じ間隔で送る (CNO 側の ForceSnapMinInterval と揃える)
    private const float SampleInterval = 0.1f;
    private const float DirectionTrackThreshold = 0.01f;

    // 矢の当たり判定の半径 (プレイヤー本体の半径ぶんの猶予込み・Archer と同じ程度)
    private const float ArrowHitRadius = 0.6f;

    // 消える/変身ボタンで操縦中にもう一度押せるようにするための短いクールダウン
    private const float SteeringButtonCooldown = 1f;

    // CNO の生成は 1 個ごとに「宛先の人数ぶん」の配信予算 (12/秒) を使い、大人数だと実体化まで数秒かかる。
    // 撃った瞬間に出すと遅れるので、操縦を始めた時点でマップ外へ先に出しておき、撃つ時は TP で置くだけにする
    // (NaturalDisaster の使い回しと同じ退避座標)。
    private static readonly Vector2 ArrowParkPos = new(50f, 50f);

    public enum Phase { None, Steering, Flying }

    // Utils.ShouldNotApplyAbilityCooldown が参照するため public (操縦の開始ではクールダウンを付けず、発射で付ける)
    public Phase CurrentPhase;

    private byte NovaId;
    private float SteeringElapsed;
    private NovaStar StarCNO;
    private Vector2 StarPos;
    private float StarAngle;
    private float TargetAngle;
    private Vector2 FacingDir;
    private Vector2 LastSamplePos;
    private float SampleTimer;
    private int MovedSamples;
    private float StarStartAngle;

    private NovaArrow ArrowCNO;
    private bool ArrowPlaced;
    private Vector2 ArrowPos;
    private Vector2 ArrowDir;
    private float ArrowAngle;
    private float ArrowTravelled;
    private float ArrowTpTimer;
    private readonly HashSet<byte> AlreadyHit = [];
    private int Kills;

    // 負の速度 (反転効果) も正規の状態なので、捕捉できたかは専用の bool で持つ
    private bool SpeedCaptured;
    private float OriginalSpeed;
    private float WrittenSpeed;

    public override bool IsEnable => On;

    private static bool UsesPetButton => Options.UsePets.GetBool() && !Options.UsePhantomBasis.GetBool();

    private Vector2 StarDir => new(Mathf.Cos(StarAngle * Mathf.Deg2Rad), Mathf.Sin(StarAngle * Mathf.Deg2Rad));

    public override void SetupCustomOption()
    {
        StartSetup(Id)
            .AutoSetupOption(ref KillCooldown, 30f, new FloatValueRule(0f, 180f, 0.5f), OptionFormat.Seconds)
            .AutoSetupOption(ref AbilityCooldown, 20, new IntegerValueRule(5, 120, 1), OptionFormat.Seconds)
            .AutoSetupOption(ref AbilityDuration, 10, new IntegerValueRule(1, 60, 1), OptionFormat.Seconds)
            .AutoSetupOption(ref OrbitSpeed, 360, new IntegerValueRule(90, 720, 30), OptionFormat.None)
            // 下限 0.1: これより遅いと 0.1 秒の移動量が向きを読む閾値を割り、☆が回らなくなる
            .AutoSetupOption(ref SlowMultiplier, 0.2f, new FloatValueRule(0.1f, 1f, 0.05f), OptionFormat.Multiplier)
            .AutoSetupOption(ref ArrowSpeed, 10f, new FloatValueRule(3f, 30f, 1f), OptionFormat.Multiplier)
            .AutoSetupOption(ref ArrowRange, 12f, new FloatValueRule(3f, 30f, 1f), OptionFormat.Multiplier)
            .AutoSetupOption(ref ArrowPierce, false)
            .AutoSetupOption(ref ArrowFriendlyFire, false)
            .AutoSetupOption(ref OrbitRadius, 1.5f, new FloatValueRule(0.5f, 5f, 0.25f), OptionFormat.Multiplier)
            // 矢 1 文字の <size>。☆は 12
            .AutoSetupOption(ref ArrowSize, 30, new IntegerValueRule(8, 80, 2), OptionFormat.None);
    }

    public override void Init()
    {
        On = false;
    }

    public override void Add(byte playerId)
    {
        On = true;
        NovaId = playerId;
        CurrentPhase = Phase.None;
        SpeedCaptured = false;
        StarCNO = null;
        ArrowCNO = null;
        FacingDir = Vector2.right;
        LastSamplePos = Vector2.zero;
        SampleTimer = 0f;
    }

    public override void Remove(byte playerId)
    {
        EndAll(false);
    }

    public override void SetKillCooldown(byte id)
    {
        Main.AllPlayerKillCooldown[id] = KillCooldown.GetFloat();
    }

    public override void ApplyGameOptions(IGameOptions opt, byte playerId)
    {
        // 消える/変身ボタンは AU 側のクールダウンで律速される。操縦中だけ短くして 2 回目 (発射) を押せるようにし、
        // 撃った時/時間切れの時にクールダウンを張り直す (そこから AbilityCooldown 秒)
        float cd = CurrentPhase == Phase.Steering ? SteeringButtonCooldown : AbilityCooldown.GetFloat();
        if (IntroCutsceneDestroyPatch.PreventKill) cd = Mathf.Max(cd, (Options.StartingKillCooldown?.GetFloat() ?? 10f) + 2f);

        if (Options.UsePhantomBasis.GetBool())
            AURoleOptions.PhantomCooldown = cd;
        else if (!Options.UsePets.GetBool())
        {
            AURoleOptions.ShapeshifterCooldown = cd;
            AURoleOptions.ShapeshifterDuration = 1f;
        }
    }

    public override void SetButtonTexts(HudManager hud, byte id)
    {
        if (UsesPetButton)
            hud.PetButton?.OverrideText(Translator.GetString("NovaAbilityButtonText"));
        else
            hud.AbilityButton?.OverrideText(Translator.GetString("NovaAbilityButtonText"));
    }

    public override void OnPet(PlayerControl pc)
    {
        OnAbility(pc);
    }

    public override bool OnVanish(PlayerControl pc)
    {
        OnAbility(pc);
        return false;
    }

    public override bool OnShapeshift(PlayerControl shapeshifter, PlayerControl target, bool shapeshifting)
    {
        if (!shapeshifting) return true;
        shapeshifter.RpcRejectShapeshift();
        OnAbility(shapeshifter);
        return false;
    }

    private void OnAbility(PlayerControl pc)
    {
        if (!pc || !pc.IsAlive() || !GameStates.IsInTask || ReportDeadBodyPatch.MeetingStarted || GameStates.IsMeeting) return;

        switch (CurrentPhase)
        {
            case Phase.None:
                StartControl(pc);
                break;
            case Phase.Steering:
                Fire(pc);
                break;
        }
    }

    private void StartControl(PlayerControl pc)
    {
        // 他役職に凍結されている最中に控えると、解除時に凍結の値を書き戻して動けないままにしてしまう
        if (Mathf.Approximately(Main.AllPlayerSpeed[pc.PlayerId], Main.MinSpeed)) return;

        CurrentPhase = Phase.Steering;
        SteeringElapsed = 0f;

        OriginalSpeed = Main.AllPlayerSpeed[pc.PlayerId];
        WrittenSpeed = OriginalSpeed * SlowMultiplier.GetFloat();
        SpeedCaptured = true;
        Main.AllPlayerSpeed[pc.PlayerId] = WrittenSpeed;
        pc.MarkDirtySettings();
        if (!UsesPetButton) LateTask.New(pc.RpcResetAbilityCooldown, 0.2f, log: false);

        Vector2 pos = pc.Pos();
        LastSamplePos = pos;
        SampleTimer = 0f;
        MovedSamples = 0;
        StarAngle = TargetAngle = StarStartAngle = Mathf.Atan2(FacingDir.y, FacingDir.x) * Mathf.Rad2Deg;
        StarPos = OrbitPosition(pos);
        ArrowPlaced = false;
        Logger.Info($"Start: holder={pc.PlayerId} speed {OriginalSpeed} -> {WrittenSpeed} star={StarPos} angle={StarAngle:0}", "Nova");

        string parkedSprite = NovaArrow.Sprite(0f, ArrowSize.GetInt());

        // CNO の Position は生成コルーチン内で非同期に代入されるので、☆と矢の座標は役職側で持つ
        Utils.CombineSendTimeLowering(() =>
        {
            StarCNO = new NovaStar(StarPos);
            ArrowCNO = new NovaArrow(ArrowParkPos, parkedSprite);
        });
    }

    private Vector2 OrbitPosition(Vector2 center)
    {
        float r = OrbitRadius.GetFloat();
        Vector2 dir = StarDir;
        return new(center.x + (dir.x * r), center.y + (dir.y * r));
    }

    private void Fire(PlayerControl pc)
    {
        CurrentPhase = Phase.Flying;
        ArrowDir = StarDir;
        ArrowAngle = StarAngle;
        ArrowPos = StarPos;
        ArrowTravelled = 0f;
        ArrowTpTimer = 0f;
        AlreadyHit.Clear();
        Kills = 0;

        // ☆は持ち主から離れた所にいるので、間に壁があると壁の向こうから撃ててしまう。その時は不発にする
        Vector2 holderPos = pc.Pos();
        float sx = StarPos.x - holderPos.x;
        float sy = StarPos.y - holderPos.y;
        float starDist = Mathf.Sqrt((sx * sx) + (sy * sy));
        bool blocked = starDist > 0.01f && IsWallBetween(holderPos, new(sx / starDist, sy / starDist), starDist);

        Logger.Info($"Fire: holder={pc.PlayerId} origin={ArrowPos} dir={ArrowDir} angle={ArrowAngle:0} arrowReady={(ArrowCNO != null && ArrowCNO.playerControl)} blocked={blocked}", "Nova");

        // 撃った時点で操縦は終わり: ☆を消して足を戻す (矢は独立して飛ぶ)。
        // ペットのクールダウンはここで付ける — 飛んでいる間は汎用経路の付け直しを免除しているため
        DespawnWhenSpawned(StarCNO);
        StarCNO = null;
        RestoreSpeed(pc);
        if (UsesPetButton) Utils.AddAbilityCD(CustomRoles.Nova, pc.PlayerId, includeDuration: false);
        else LateTask.New(pc.RpcResetAbilityCooldown, 0.2f, log: false);

        if (blocked)
        {
            EndAll(false);
            return;
        }

        PlaceArrow();
    }

    // 壁本体 (ShipAndObjects) と影レイヤー (Shadow) は別マスクなので両方見る (Crosswind と同じ)
    private static bool IsWallBetween(Vector2 from, Vector2 dir, float dist)
    {
        return PhysicsHelpers.AnyNonTriggersBetween(from, dir, dist, Constants.ShipAndObjectsMask)
               || PhysicsHelpers.AnyNonTriggersBetween(from, dir, dist, Constants.ShadowMask);
    }

    // 退避させておいた矢を発射位置へ置いて向きを付ける。撃つまでに実体化が間に合わなかった時は、
    // 実体化した後の tick でここに来た時に置く (TP も見た目の差し替えも実体化前だと黙って捨てられるため)
    private void PlaceArrow()
    {
        if (ArrowPlaced || ArrowCNO == null || !ArrowCNO.playerControl) return;

        ArrowPlaced = true;
        ArrowCNO.TP(ArrowPos);
        ArrowCNO.RpcChangeSprite(NovaArrow.Sprite(ArrowAngle, ArrowSize.GetInt()));
    }

    private void RestoreSpeed(PlayerControl pc)
    {
        bool restore = SpeedCaptured;
        SpeedCaptured = false;

        // 操縦中に他役職が速度を書き換えた (凍結・加速など) なら、その値を尊重して戻さない
        if (restore && Mathf.Approximately(Main.AllPlayerSpeed[NovaId], WrittenSpeed))
            Main.AllPlayerSpeed[NovaId] = OriginalSpeed;

        if (pc) pc.MarkDirtySettings();
    }

    // 操縦の時間切れ・矢の消滅・会議・死亡・役職の解除をすべてここで畳む
    private void EndAll(bool steeringTimedOut)
    {
        if (CurrentPhase == Phase.None) return;
        bool wasSteering = CurrentPhase == Phase.Steering;
        CurrentPhase = Phase.None;

        Logger.Info($"End: holder={NovaId} steering={SteeringElapsed:0.0}s moved={MovedSamples} angle {StarStartAngle:0} -> {StarAngle:0} arrow={ArrowTravelled:0.0}u kills={Kills}", "Nova");
        DespawnWhenSpawned(StarCNO);
        StarCNO = null;
        DespawnWhenSpawned(ArrowCNO);
        ArrowCNO = null;

        if (!wasSteering) return;

        PlayerControl pc = Utils.GetPlayerById(NovaId);

        // ペットは操縦の開始でクールダウンを付けていないので、撃たずに時間切れになった時だけここで付ける
        if (steeringTimedOut && UsesPetButton) Utils.AddAbilityCD(CustomRoles.Nova, NovaId, includeDuration: false);

        RestoreSpeed(pc);
        if (pc && !UsesPetButton && pc.IsAlive()) LateTask.New(pc.RpcResetAbilityCooldown, 0.2f, log: false);
    }

    // 生成コルーチンは Despawn 済みかを見ずに実体化まで進む (会議中は会議明けまで待つ) ので、
    // 実体化前に Despawn すると後から追跡外の CNO が出てしまう。実体化を待ってから消す。
    // 生成自体が取り消された場合は実体化しないので、会議をまたいでも足りる回数 (約 5 分) で諦める。
    private static void DespawnWhenSpawned(CustomNetObject cno, int tries = 0)
    {
        if (cno == null) return;

        if (cno.playerControl)
        {
            cno.Despawn();
            return;
        }

        // 実体化前の Despawn は呼ばない — コルーチンがこの後使う PlayerId の確保まで解放してしまう
        if (!GameStates.InGame || GameStates.IsEnded || tries >= 1500) return;
        LateTask.New(() => DespawnWhenSpawned(cno, tries + 1), 0.2f, log: false);
    }

    public override void OnFixedUpdate(PlayerControl pc)
    {
        if (!AmongUsClient.Instance.AmHost) return;

        if (!GameStates.IsInTask || ExileController.Instance)
        {
            EndAll(false);
            return;
        }

        switch (CurrentPhase)
        {
            case Phase.Steering when !pc.IsAlive():
                EndAll(false);
                return;
            case Phase.Flying:
                FlyArrow(pc);
                return;
        }

        SampleTick(pc);
    }

    // 0.1 秒ごとに本人の位置の差分から入力の向きを読み、☆をその向きへ回り込ませる
    private void SampleTick(PlayerControl pc)
    {
        SampleTimer += Time.fixedDeltaTime;
        if (SampleTimer < SampleInterval) return;

        float dt = SampleTimer;
        SampleTimer = 0f;

        Vector2 pos = pc.Pos();
        float dx = pos.x - LastSamplePos.x;
        float dy = pos.y - LastSamplePos.y;
        bool firstSample = LastSamplePos == Vector2.zero;
        LastSamplePos = pos;

        if (firstSample) return;

        float dist = Mathf.Sqrt((dx * dx) + (dy * dy));
        bool moved = dist > DirectionTrackThreshold;
        if (moved) FacingDir = new(dx / dist, dy / dist);

        if (CurrentPhase != Phase.Steering) return;

        SteeringElapsed += dt;
        if (SteeringElapsed >= AbilityDuration.GetFloat())
        {
            EndAll(true);
            return;
        }

        if (moved)
        {
            TargetAngle = Mathf.Atan2(dy, dx) * Mathf.Rad2Deg;
            MovedSamples++;
        }

        StarAngle = Mathf.MoveTowardsAngle(StarAngle, TargetAngle, OrbitSpeed.GetFloat() * dt);

        // 本人が動けば☆もついていく
        Vector2 next = OrbitPosition(pos);
        float mx = next.x - StarPos.x;
        float my = next.y - StarPos.y;
        StarPos = next;
        if (StarCNO != null && (mx * mx) + (my * my) > 0.0001f) StarCNO.TP(StarPos);
    }

    // 矢を進め、通り道 (前の位置 → 今の位置の線分) にいる相手を倒す。壁か射程の端で消える
    private void FlyArrow(PlayerControl shooter)
    {
        PlaceArrow();

        float step = ArrowSpeed.GetFloat() * Time.fixedDeltaTime;
        float range = ArrowRange.GetFloat();
        if (ArrowTravelled + step > range) step = range - ArrowTravelled;

        Vector2 from = ArrowPos;

        // 壁を先に見る — 当たり判定を先にすると、壁で止まるはずの 1 歩で壁の向こうの相手まで撃ててしまう
        if (IsWallBetween(from, ArrowDir, step))
        {
            EndAll(false);
            return;
        }

        ArrowPos = new(from.x + (ArrowDir.x * step), from.y + (ArrowDir.y * step));
        ArrowTravelled += step;

        if (CheckArrowHits(shooter, from, step) || ArrowTravelled >= range - 0.001f)
        {
            EndAll(false);
            return;
        }

        ArrowTpTimer += Time.fixedDeltaTime;
        if (ArrowTpTimer < SampleInterval || !ArrowPlaced) return;
        ArrowTpTimer = 0f;
        ArrowCNO.TP(ArrowPos);
    }

    // true = 矢が止まった (貫通しない設定で誰かに当たった)
    private bool CheckArrowHits(PlayerControl shooter, Vector2 from, float length)
    {
        PlayerControl first = null;
        float firstAlong = float.MaxValue;
        bool pierce = ArrowPierce.GetBool();

        foreach (PlayerControl target in Main.EnumerateAlivePlayerControls())
        {
            if (target.PlayerId == shooter.PlayerId) continue;
            if (AlreadyHit.Contains(target.PlayerId)) continue;
            if (!ArrowFriendlyFire.GetBool() && target.GetCustomRole().IsImpostor()) continue;
            if (Pelican.IsEaten(target.PlayerId)) continue;

            Vector2 pos = target.GetTruePosition();
            float dx = pos.x - from.x;
            float dy = pos.y - from.y;
            float along = Mathf.Clamp((dx * ArrowDir.x) + (dy * ArrowDir.y), 0f, length);
            float lx = dx - (ArrowDir.x * along);
            float ly = dy - (ArrowDir.y * along);
            if ((lx * lx) + (ly * ly) > ArrowHitRadius * ArrowHitRadius) continue;

            if (pierce) TryKill(shooter, target);
            else if (along < firstAlong)
            {
                first = target;
                firstAlong = along;
            }
        }

        if (!first) return false;

        TryKill(shooter, first);
        return true;
    }

    private void TryKill(PlayerControl shooter, PlayerControl target)
    {
        // 弾かれた相手もこの一発では処理済みにする (関所は弾くたびキラーへ通知を送るので、毎フレームの再送を防ぐ)
        AlreadyHit.Add(target.PlayerId);

        // Kill() が弾く状況 (ロビー・試合外・イントロ中) では、死亡状態だけ同期されて死体が出ない半端な死に方になる
        if (GameStates.IsLobby || !GameStates.InGame || !Main.IntroDestroyed) return;
        if (!CheckMurderPatch.PassesGate(shooter, target, kind: AttackKind.Execution)) return;

        Kills++;
        RPC.PlaySoundRPC(shooter.PlayerId, Sounds.KillSound);

        PlayerState state = Main.PlayerStates[target.PlayerId];
        state.deathReason = PlayerState.DeathReason.Sniped;
        target.SetRealKiller(shooter);
        state.SetDead();
        Medic.IsDead(target);
        target.Kill(target);
    }

    // 会議で全員スポーンへワープするので、直前の位置を残すと会議明け最初の差分がワープ量になる
    public override void OnReportDeadBody()
    {
        EndAll(false);
        LastSamplePos = Vector2.zero;
    }
}
