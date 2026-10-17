using System.Collections.Generic;
using AmongUs.GameOptions;
using EndKnot.Modules;
using EndKnot.Patches;
using UnityEngine;

namespace EndKnot.Roles;

// Ported from sijimi2222/TownOfHost-Shell
// Based on: TeamUchuaddon/UchuAddon (Nova)
// 押すと本人の足が鈍り、代わりに本人の周りへ出た☆が、歩こうとした向きへ回り込む。操縦中にもう一度押すと
// ☆の位置から「本人 → ☆」の向きへ大きな矢を 1 発撃ち、最初にぶつかった相手を倒す。操縦は撃っても続き、
// 設定の回数を撃ち切るか時間が来ると終わる。
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
    private static OptionItem ShotCount;
    private static OptionItem ArrowPierceWalls;

    // 位置の差分を取る間隔。☆の TP も同じ間隔で送る (CNO 側の ForceSnapMinInterval と揃える)
    private const float SampleInterval = 0.1f;
    private const float DirectionTrackThreshold = 0.01f;

    // 矢の TP の間隔 (NovaArrow.SnapInterval と揃える)。同時に飛ぶ本数が多い時だけ粗くして、☆と合わせた SnapTo の本数を抑える
    private const float ArrowTpInterval = 0.1f;
    private const float CrowdedArrowTpInterval = 0.2f;
    private const int CrowdedArrowCount = 3;

    // 公式鯖が SnapTo の本数を数える窓 (秒) と、その窓で切られない本数の目安 (全 CNO 合計)
    private const float SnapToBudgetWindow = 6f;
    private const float SnapToBudgetPerWindow = 330f;

    // 矢の当たり判定の半径 (プレイヤー本体の半径ぶんの猶予込み・Archer と同じ程度)
    private const float ArrowHitRadius = 0.6f;

    // 操縦中に次の 1 発を撃てるまでの間隔。消える/変身ボタンはこの値を AU 側のクールダウンにし、
    // クールダウンの無いペットはこちらで押下を間引く
    private const float SteeringButtonCooldown = 1f;

    // CNO の生成は 1 個ごとに「宛先の人数ぶん」の配信予算 (12/秒) を使い、大人数だと実体化まで数秒かかる。
    // 撃った瞬間に出すと遅れるので、操縦を始めた時点でマップ外へ先に出しておき、撃つ時は TP で置くだけにする
    // (NaturalDisaster の使い回しと同じ退避座標)。飛び終わった矢もここへ戻して次の 1 発に使い回す。
    private static readonly Vector2 ArrowParkPos = new(50f, 50f);

    public enum Phase { None, Steering, Flying }

    // Utils.ShouldNotApplyAbilityCooldown が参照するため public (操縦の開始ではクールダウンを付けず、操縦の終わりで付ける)。
    // Flying = 操縦は終わったが、撃った矢がまだ飛んでいる
    public Phase CurrentPhase;

    private byte NovaId;
    private float SteeringElapsed;
    private NovaStar StarCNO;
    private bool StarSynced;
    private Vector2 StarPos;
    private float StarAngle;
    private float TargetAngle;
    private Vector2 FacingDir;
    private Vector2 LastSamplePos;
    private float SampleTimer;
    private int MovedSamples;
    private float StarStartAngle;
    private int ShotsFired;
    private float LastPressTime;
    private int Kills;

    private sealed class Arrow
    {
        public NovaArrow CNO;
        public bool Placed;
        public Vector2 Pos;
        public Vector2 Dir;
        public float Angle;
        public float Travelled;
        public float TpTimer;
        public readonly HashSet<byte> AlreadyHit = [];
    }

    private readonly List<Arrow> FlyingArrows = [];

    // 出してある矢の CNO 全部と、そのうち今は飛んでいない (退避座標で待っている) もの
    private readonly List<NovaArrow> ArrowCNOs = [];
    private readonly List<NovaArrow> ParkedArrows = [];

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
            .AutoSetupOption(ref AbilityDuration, 10, new IntegerValueRule(1, 60, 1), OptionFormat.Seconds, overrideName: "Nova.SteerDuration")
            .AutoSetupOption(ref OrbitSpeed, 360, new IntegerValueRule(90, 720, 30), OptionFormat.None)
            // 下限 0.1: これより遅いと 0.1 秒の移動量が向きを読む閾値を割り、☆が回らなくなる
            .AutoSetupOption(ref SlowMultiplier, 0.2f, new FloatValueRule(0.1f, 1f, 0.05f), OptionFormat.Multiplier)
            .AutoSetupOption(ref ArrowSpeed, 10f, new FloatValueRule(3f, 30f, 1f), OptionFormat.Multiplier)
            .AutoSetupOption(ref ArrowRange, 12f, new FloatValueRule(3f, 30f, 1f), OptionFormat.Multiplier)
            .AutoSetupOption(ref ArrowPierce, false)
            .AutoSetupOption(ref ArrowFriendlyFire, false)
            .AutoSetupOption(ref OrbitRadius, 1.5f, new FloatValueRule(0.5f, 5f, 0.25f), OptionFormat.Multiplier)
            // 矢 1 文字の <size>。☆は 12
            .AutoSetupOption(ref ArrowSize, 30, new IntegerValueRule(8, 80, 2), OptionFormat.None)
            // 上限 5: 同時に飛ぶ矢はそれぞれ位置を送り続けるので、☆と合わせた SnapTo の本数がこの辺りで頭打ちになるようにする
            .AutoSetupOption(ref ShotCount, 1, new IntegerValueRule(1, 5, 1), OptionFormat.Times)
            .AutoSetupOption(ref ArrowPierceWalls, false);
    }

    // ノヴァ全員が同時に操縦して撃ち切った時、SnapTo の本数が 1 窓ぶんの目安を超えるか。ゲーム開始時の設定チェックが使う。
    // 1 人なら最悪の設定でも目安に収まるので、2 人以上の時だけ超えうる
    public static bool ExceedsSnapToBudget(int novaCount)
    {
        if (novaCount < 2) return false;

        float window = SnapToBudgetWindow;
        float flightTime = ArrowRange.GetFloat() / ArrowSpeed.GetFloat();
        int shots = ShotCount.GetInt();

        // 1 秒おきに撃つので、同時に飛ぶ矢は「飛ぶ時間 (秒)」と「撃てる回数」の小さい方。本数が多いと送信を粗くする
        float flying = Mathf.Min(shots, flightTime / SteeringButtonCooldown);
        float arrowRate = 1f / (flying >= CrowdedArrowCount ? CrowdedArrowTpInterval : ArrowTpInterval);
        int parked = Mathf.Min(shots, Mathf.FloorToInt(flightTime / SteeringButtonCooldown) + 1);

        // ☆ (窓のあいだ送り続ける) + 矢 (1 本が窓の中で飛ぶ時間ぶん) + 待っている矢の周期送信 (1 本あたり 1 回/秒)
        float perNova = (window / SampleInterval)
                        + (Mathf.Min(shots, window) * Mathf.Min(flightTime, window) * arrowRate)
                        + (parked * window);

        return novaCount * perNova > SnapToBudgetPerWindow;
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
        FlyingArrows.Clear();
        ArrowCNOs.Clear();
        ParkedArrows.Clear();
        FacingDir = Vector2.right;
        LastSamplePos = Vector2.zero;
        SampleTimer = 0f;
    }

    public override void Remove(byte playerId)
    {
        EndAll();
    }

    public override void SetKillCooldown(byte id)
    {
        Main.AllPlayerKillCooldown[id] = KillCooldown.GetFloat();
    }

    public override void ApplyGameOptions(IGameOptions opt, byte playerId)
    {
        // 消える/変身ボタンは AU 側のクールダウンで律速される。操縦中だけ短くして次の 1 発を押せるようにし、
        // 操縦が終わった時にクールダウンを張り直す (そこから AbilityCooldown 秒)
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
            // 前の操縦で撃った矢が飛んでいる間でも、クールダウンが明けていれば次の操縦を始められる (矢はそのまま飛ぶ)
            case Phase.None or Phase.Flying:
                StartControl(pc);
                break;
            // 操縦中のペットは汎用経路のクールダウンが付かないので、連打で一度に撃ち切らないようここで間引く
            case Phase.Steering when Time.time - LastPressTime >= SteeringButtonCooldown - 0.05f:
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
        ShotsFired = 0;
        Kills = 0;
        LastPressTime = Time.time;

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
        StarSynced = false;

        // 先に出しておく矢は「同時に飛びうる本数」だけ。矢 1 本の飛ぶ時間と撃てる間隔から決まり、撃つ回数より多くは要らない
        float flightTime = ArrowRange.GetFloat() / ArrowSpeed.GetFloat();
        int poolSize = Mathf.Min(ShotCount.GetInt(), Mathf.FloorToInt(flightTime / SteeringButtonCooldown) + 1);
        Logger.Info($"Start: holder={pc.PlayerId} speed {OriginalSpeed} -> {WrittenSpeed} star={StarPos} angle={StarAngle:0} shots={ShotCount.GetInt()} pool={poolSize}", "Nova");

        string parkedSprite = NovaArrow.Sprite(0f, ArrowSize.GetInt());

        // CNO の Position は生成コルーチン内で非同期に代入されるので、☆と矢の座標は役職側で持つ
        Utils.CombineSendTimeLowering(() =>
        {
            StarCNO = new NovaStar(StarPos);

            for (int i = 0; i < poolSize; i++)
            {
                var cno = new NovaArrow(ArrowParkPos, parkedSprite);
                ArrowCNOs.Add(cno);
                ParkedArrows.Add(cno);
            }
        });

        ExplosionFx.NovaRise(pc, AbilityDuration.GetFloat(), OrbitRadius.GetFloat(), ArrowPierceWalls.GetBool());
    }

    private Vector2 OrbitPosition(Vector2 center)
    {
        float r = OrbitRadius.GetFloat();
        Vector2 dir = StarDir;
        return new(center.x + (dir.x * r), center.y + (dir.y * r));
    }

    private void Fire(PlayerControl pc)
    {
        LastPressTime = Time.time;
        ShotsFired++;

        // ☆は持ち主から離れた所にいるので、間に壁があると壁の向こうから撃ててしまう。その時は不発にする (回数は使う)
        Vector2 holderPos = pc.Pos();
        float sx = StarPos.x - holderPos.x;
        float sy = StarPos.y - holderPos.y;
        float starDist = Mathf.Sqrt((sx * sx) + (sy * sy));
        bool blocked = !ArrowPierceWalls.GetBool() && starDist > 0.01f && IsWallBetween(holderPos, new(sx / starDist, sy / starDist), starDist);

        if (!blocked)
        {
            // 演出側に届く向きは整数の度なので、矢も同じ向きで飛ばす (ずれると流星だけ壁で消えて矢の文字が見える)
            float angle = Mathf.Round(StarAngle);
            Arrow arrow = new()
            {
                CNO = TakeArrowCNO(),
                Dir = new(Mathf.Cos(angle * Mathf.Deg2Rad), Mathf.Sin(angle * Mathf.Deg2Rad)),
                Angle = angle,
                Pos = StarPos
            };

            FlyingArrows.Add(arrow);
            PlaceArrow(arrow);
            ExplosionFx.NovaShot(pc.PlayerId, StarPos, angle, ArrowSpeed.GetFloat(), ArrowRange.GetFloat());
        }
        else ExplosionFx.NovaHit(pc.PlayerId, StarPos, false, false);

        Logger.Info($"Fire: holder={pc.PlayerId} shot={ShotsFired}/{ShotCount.GetInt()} origin={StarPos} angle={StarAngle:0} blocked={blocked} flying={FlyingArrows.Count}", "Nova");

        if (ShotsFired >= ShotCount.GetInt())
        {
            EndSteering(true);
            return;
        }

        // 操縦は続くので、消える/変身ボタンの短いクールダウンだけ張り直す
        if (!UsesPetButton) LateTask.New(pc.RpcResetAbilityCooldown, 0.2f, log: false);
    }

    // 待っている矢のうち実体化済みのものを優先して使う。足りない時 (飛ぶ時間ぎりぎりで撃った等) だけ新しく出す
    private NovaArrow TakeArrowCNO()
    {
        int index = ParkedArrows.FindIndex(x => (bool)x.playerControl);
        if (index < 0 && ParkedArrows.Count > 0) index = 0;

        if (index >= 0)
        {
            NovaArrow parked = ParkedArrows[index];
            ParkedArrows.RemoveAt(index);
            return parked;
        }

        NovaArrow cno = null;
        string sprite = NovaArrow.Sprite(0f, ArrowSize.GetInt());
        Utils.CombineSendTimeLowering(() => cno = new NovaArrow(ArrowParkPos, sprite));
        ArrowCNOs.Add(cno);
        return cno;
    }

    // 壁本体 (ShipAndObjects) と影レイヤー (Shadow) は別マスクなので両方見る (Crosswind と同じ)
    private static bool IsWallBetween(Vector2 from, Vector2 dir, float dist)
    {
        return PhysicsHelpers.AnyNonTriggersBetween(from, dir, dist, Constants.ShipAndObjectsMask)
               || PhysicsHelpers.AnyNonTriggersBetween(from, dir, dist, Constants.ShadowMask);
    }

    // 退避させておいた矢を今の位置へ置いて向きを付ける。撃つまでに実体化が間に合わなかった時は、
    // 実体化した後の tick でここに来た時に置く (TP も見た目の差し替えも実体化前だと黙って捨てられるため)
    private void PlaceArrow(Arrow arrow)
    {
        if (arrow.Placed || arrow.CNO == null || !arrow.CNO.playerControl) return;

        arrow.Placed = true;
        arrow.TpTimer = 0f;
        arrow.CNO.TP(arrow.Pos);
        arrow.CNO.RpcChangeSprite(NovaArrow.Sprite(arrow.Angle, ArrowSize.GetInt()));
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

    // 操縦だけを畳む (撃ち切り・時間切れ・本人の死亡)。撃った矢は飛び続け、待っていた矢はもう使わないので消す。
    // addPetCooldown: ペットは操縦の開始でクールダウンを付けていないので、操縦をやり切った時だけここで付ける
    private void EndSteering(bool addPetCooldown)
    {
        if (CurrentPhase != Phase.Steering) return;

        // フェーズを先に変える — RestoreSpeed の MarkDirtySettings で ApplyGameOptions が本来のクールダウンを書くため
        CurrentPhase = FlyingArrows.Count > 0 ? Phase.Flying : Phase.None;

        Logger.Info($"EndSteering: holder={NovaId} steering={SteeringElapsed:0.0}s moved={MovedSamples} angle {StarStartAngle:0} -> {StarAngle:0} shots={ShotsFired} flying={FlyingArrows.Count}", "Nova");
        DespawnWhenSpawned(StarCNO);
        StarCNO = null;
        DespawnParkedArrows();
        ExplosionFx.NovaDown(NovaId, StarPos);

        PlayerControl pc = Utils.GetPlayerById(NovaId);
        if (addPetCooldown && UsesPetButton) Utils.AddAbilityCD(CustomRoles.Nova, NovaId, includeDuration: false);

        RestoreSpeed(pc);
        if (pc && !UsesPetButton && pc.IsAlive()) LateTask.New(pc.RpcResetAbilityCooldown, 0.2f, log: false);
    }

    // 会議・試合外・役職の解除で、操縦も飛んでいる矢もすべて畳む
    private void EndAll()
    {
        if (CurrentPhase == Phase.None) return;

        EndSteering(false);
        CurrentPhase = Phase.None;

        Logger.Info($"End: holder={NovaId} flying={FlyingArrows.Count} kills={Kills}", "Nova");
        FlyingArrows.Clear();
        ParkedArrows.Clear();
        ArrowCNOs.ForEach(x => DespawnWhenSpawned(x));
        ArrowCNOs.Clear();
    }

    private void DespawnParkedArrows()
    {
        foreach (NovaArrow cno in ParkedArrows)
        {
            ArrowCNOs.Remove(cno);
            DespawnWhenSpawned(cno);
        }

        ParkedArrows.Clear();
    }

    // 飛び終わった矢 (壁・命中・射程の端)。操縦中なら退避座標へ戻して次の 1 発に回し、操縦が終わっていれば消す
    private void FinishArrow(Arrow arrow)
    {
        FlyingArrows.Remove(arrow);
        NovaArrow cno = arrow.CNO;
        Logger.Info($"NOVADIAG finishArrow pid={(cno != null && cno.playerControl ? cno.playerControl.PlayerId : -1)} pos={arrow.Pos} travelled={arrow.Travelled:0.00} placed={arrow.Placed} f={Time.frameCount}", "Nova"); // NOVADIAG

        if (cno != null)
        {
            if (CurrentPhase == Phase.Steering)
            {
                if (arrow.Placed) cno.TP(ArrowParkPos);
                ParkedArrows.Add(cno);
            }
            else
            {
                ArrowCNOs.Remove(cno);
                DespawnWhenSpawned(cno);
            }
        }

        if (CurrentPhase == Phase.Flying && FlyingArrows.Count == 0)
        {
            CurrentPhase = Phase.None;
            Logger.Info($"End: holder={NovaId} kills={Kills}", "Nova");
        }
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
            EndAll();
            return;
        }

        if (CurrentPhase == Phase.Steering && !pc.IsAlive()) EndSteering(false);

        float tpInterval = FlyingArrows.Count >= CrowdedArrowCount ? CrowdedArrowTpInterval : ArrowTpInterval;

        for (int i = FlyingArrows.Count - 1; i >= 0; i--)
        {
            Arrow arrow = FlyingArrows[i];
            if (FlyArrow(pc, arrow, tpInterval)) FinishArrow(arrow);
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
            EndSteering(true);
            return;
        }

        if (moved)
        {
            TargetAngle = Mathf.Atan2(dy, dx) * Mathf.Rad2Deg;
            MovedSamples++;
        }

        StarAngle = Mathf.MoveTowardsAngle(StarAngle, TargetAngle, OrbitSpeed.GetFloat() * dt);

        // 本人が動けば☆もついていく。実体化前の TP は生成時の座標で上書きされるので、実体化した最初の tick で必ず 1 回送る
        Vector2 next = OrbitPosition(pos);
        float mx = next.x - StarPos.x;
        float my = next.y - StarPos.y;
        StarPos = next;
        if (StarCNO == null || !StarCNO.playerControl) return;
        if (StarSynced && (mx * mx) + (my * my) <= 0.0001f) return;
        StarSynced = true;
        StarCNO.TP(StarPos);
    }

    // 矢を進め、通り道 (前の位置 → 今の位置の線分) にいる相手を倒す。true = 壁か射程の端か命中で飛び終わった
    private bool FlyArrow(PlayerControl shooter, Arrow arrow, float tpInterval)
    {
        PlaceArrow(arrow);

        float step = ArrowSpeed.GetFloat() * Time.fixedDeltaTime;
        float range = ArrowRange.GetFloat();
        if (arrow.Travelled + step > range) step = range - arrow.Travelled;

        Vector2 from = arrow.Pos;

        // 壁を先に見る — 当たり判定を先にすると、壁で止まるはずの 1 歩で壁の向こうの相手まで撃ててしまう
        if (!ArrowPierceWalls.GetBool() && IsWallBetween(from, arrow.Dir, step)) return true;

        arrow.Pos = new(from.x + (arrow.Dir.x * step), from.y + (arrow.Dir.y * step));
        arrow.Travelled += step;

        if (CheckArrowHits(shooter, arrow, from, step) || arrow.Travelled >= range - 0.001f) return true;

        arrow.TpTimer += Time.fixedDeltaTime;
        if (arrow.TpTimer < tpInterval || !arrow.Placed) return false;
        arrow.TpTimer = 0f;
        arrow.CNO.SnapInterval = tpInterval;
        arrow.CNO.TP(arrow.Pos);
        return false;
    }

    // true = 矢が止まった (貫通しない設定で誰かに当たった)
    private bool CheckArrowHits(PlayerControl shooter, Arrow arrow, Vector2 from, float length)
    {
        PlayerControl first = null;
        float firstAlong = float.MaxValue;
        bool pierce = ArrowPierce.GetBool();
        Vector2 dir = arrow.Dir;

        foreach (PlayerControl target in Main.EnumerateAlivePlayerControls())
        {
            if (target.PlayerId == shooter.PlayerId) continue;
            if (arrow.AlreadyHit.Contains(target.PlayerId)) continue;
            if (!ArrowFriendlyFire.GetBool() && target.GetCustomRole().IsImpostor()) continue;
            if (Pelican.IsEaten(target.PlayerId)) continue;

            Vector2 pos = target.GetTruePosition();
            float dx = pos.x - from.x;
            float dy = pos.y - from.y;
            float along = Mathf.Clamp((dx * dir.x) + (dy * dir.y), 0f, length);
            float lx = dx - (dir.x * along);
            float ly = dy - (dir.y * along);
            if ((lx * lx) + (ly * ly) > ArrowHitRadius * ArrowHitRadius) continue;

            if (pierce) TryKill(shooter, target, arrow);
            else if (along < firstAlong)
            {
                first = target;
                firstAlong = along;
            }
        }

        if (!first) return false;

        TryKill(shooter, first, arrow);
        return true;
    }

    private void TryKill(PlayerControl shooter, PlayerControl target, Arrow arrow)
    {
        // 弾かれた相手もこの一発では処理済みにする (関所は弾くたびキラーへ通知を送るので、毎フレームの再送を防ぐ)
        arrow.AlreadyHit.Add(target.PlayerId);

        // Kill() が弾く状況 (ロビー・試合外・イントロ中) では、死亡状態だけ同期されて死体が出ない半端な死に方になる
        if (GameStates.IsLobby || !GameStates.InGame || !Main.IntroDestroyed) return;
        if (!CheckMurderPatch.PassesGate(shooter, target, kind: AttackKind.Execution))
        {
            ExplosionFx.NovaHit(shooter.PlayerId, target.Pos(), false, !ArrowPierce.GetBool());
            return;
        }

        ExplosionFx.NovaHit(shooter.PlayerId, target.Pos(), true, !ArrowPierce.GetBool());

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
        EndAll();
        LastSamplePos = Vector2.zero;
    }
}
