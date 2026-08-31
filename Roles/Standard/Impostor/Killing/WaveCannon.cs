using System;
using System.Collections;
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
    private static OptionItem DirectionDetectDuration;
    private static OptionItem ChargeDuration;
    private static OptionItem WarningDuration;
    private static OptionItem FiringDuration;
    private static OptionItem BeamThickness;
    private static OptionItem FriendlyFire;
    private static OptionItem LastImpostorSuperCannon;
    private static OptionItem SuperCannonType;
    private static OptionItem SuperChargeDuration;
    private static OptionItem SuperBeamThickness;

    private const int BeamCharCount = 20;
    private const int BeamSizeUnit = 30; // 1 thickness 単位あたりのフォントサイズ
    private const float BeamHalfWidthUnit = 0.12f; // 1 thickness 単位あたりの判定半厚 (世界座標)
    private const float BeamLengthUnit = 0.015f; // 1 char 1 size 単位ありの世界距離
    private const float BeamHitboxMargin = 0.85f;   // プレイヤー本体半径ぶんの判定マージン
    private const int WarningCharCount = 20;

    // ゲート (発射元の portal 風 CNO) のレイアウト
    private const float GateForwardOffset = 1.5f; // プレイヤー中心からゲート中心までの前方距離
    private const float GateUpOffset = 0.3f;      // ゲートの縦位置 (胸の高さ)
    private const float GateRadius = 0.5f;        // ゲート半径 = ビーム発射点までのオフセット (size 140% に対応)

    private const float BeamBackwardReach = GateForwardOffset + GateRadius; // 1.5 unit、根本判定をプレイヤー位置まで伸ばす

    // ホストローカル効果音 (Resources/Sounds/*.ogg)。チャージはクレッシェンド、発射は持続轟音 (26s > FiringDuration 最大)
    private const string ChargeSoundName = "WaveCannonCharge";
    private const string FireSoundName = "WaveCannonFire";
    private const float FireSoundFadeSeconds = 0.3f;

    private enum Phase { Idle, DirectionDetect, Charging, Warning, Firing }

    // Sniper 風方向確定 phase の秒数は option で可変 (DirectionDetectDuration)。
    // Utils.TimeStamp (秒精度 long) だと秒境界をまたぐと即 Charging に入ってしまうため、
    // Time.time (float) で計測する。

    private Phase CurrentPhase;
    private long PhaseEndTS;
    private float DirectionDetectEndTime;
    private Vector2 StartPosition;
    private Vector2 Direction;
    private Vector2 LastTrackedPos;
    private float OriginalSpeed;
    private NetworkedPlayerInfo.PlayerOutfit OriginalOutfit;
    private readonly HashSet<byte> AlreadyKilled = [];
    private bool PhaseEntryDone;
    private float ShakePhaseOffset;
    private WaveCannonWarning WarningCNO;
    private WaveCannonBeamSegment BeamCNO;
    private WaveCannonGate GateCNO;
    private PlayerControl WaveCannonPC;
    private bool IsSuperShot;
    private SuperCannonShot Super;
    private AudioSource ChargeAudio;
    private AudioClip ChargeAudioClip;
    private int ShotSeq; // 発射シーケンス世代。中断→再発射時に旧 LateTask のチャージ音を無効化する

    public override bool IsEnable => On;

    public override void SetupCustomOption()
    {
        StartSetup(Id)
            .AutoSetupOption(ref AbilityCooldown, 30, new IntegerValueRule(10, 60, 1), OptionFormat.Seconds)
            .AutoSetupOption(ref DirectionDetectDuration, 1f, new FloatValueRule(0f, 10f, 0.5f), OptionFormat.Seconds)
            .AutoSetupOption(ref ChargeDuration, 3, new IntegerValueRule(1, 10, 1), OptionFormat.Seconds)
            .AutoSetupOption(ref WarningDuration, 1, new IntegerValueRule(1, 5, 1), OptionFormat.Seconds)
            .AutoSetupOption(ref FiringDuration, 3, new IntegerValueRule(1, 10, 1), OptionFormat.Seconds)
            .AutoSetupOption(ref BeamThickness, 2, new IntegerValueRule(1, 8, 1), OptionFormat.Times)
            .AutoSetupOption(ref FriendlyFire, false)
            .AutoSetupOption(ref LastImpostorSuperCannon, true, overrideName: "WaveCannon.LastImpostorSuperCannon")
            .AutoSetupOption(ref SuperCannonType, 0, SuperCannonShot.TypeOptionNames, overrideName: "WaveCannon.SuperCannonType", overrideParent: LastImpostorSuperCannon)
            .AutoSetupOption(ref SuperChargeDuration, 5, new IntegerValueRule(1, 15, 1), OptionFormat.Seconds, overrideName: "WaveCannon.SuperChargeDuration", overrideParent: LastImpostorSuperCannon)
            .AutoSetupOption(ref SuperBeamThickness, 4, new IntegerValueRule(1, 8, 1), OptionFormat.Times, overrideName: "WaveCannon.SuperBeamThickness", overrideParent: LastImpostorSuperCannon);
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
        OriginalSpeed = 0f;
        AlreadyKilled.Clear();
        PhaseEntryDone = false;
        IsSuperShot = false;
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
        Super?.Despawn();
        Super = null;
    }

    // 発動はファントムボタン (vanish) のみに統一する。
    // OnPet は意図的に削除 (非モッド側 pet animation で FlipX が書換わるため)。
    // OnShapeshift は Phantom basis では呼ばれないが念のため残す
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

    // 公式鯖 Hacking キック切り分け用。/wcdbg {mask} で設定。
    // bit1=発射シーケンス全体スキップ(ボタン押下のみ) bit2=チャージ外見スキップ bit4=gate CNO スキップ bit8=速度ロックスキップ
    public static int DebugSkipMask;

    private void TriggerCharge(PlayerControl pc)
    {
        if ((DebugSkipMask & 1) != 0) return;
        if (!pc.IsAlive() || CurrentPhase != Phase.Idle || !GameStates.IsInTask) return;

        // ラストインポスター (Last- prefix 付与中) は超波動砲を放てる
        IsSuperShot = LastImpostorSuperCannon.GetBool() && LastImpostor.CurrentId == pc.PlayerId;

        // Sniper 風方向確定 phase に入る。この秒数だけ自由に動けて発射方向を
        // 微調整できる。cosmetics.flipX はホスト側で同期されない仕様のため、
        // deltaX tracking で向きを取るしかない → 動きながら向きを決める設計。
        float detectDur = DirectionDetectDuration.GetFloat();
        if (detectDur <= 0f)
        {
            // 0 秒設定 = 旧挙動 (即 Charging)
            StartPosition = pc.GetTruePosition();
            EnterCharging(pc);
            return;
        }
        CurrentPhase = Phase.DirectionDetect;
        DirectionDetectEndTime = Time.time + detectDur;
        PhaseEntryDone = false;
        pc.Notify(GetString("WaveCannon.DirectionDetect"));
    }

    private void EnterCharging(PlayerControl pc)
    {
        CurrentPhase = Phase.Charging;
        PhaseEndTS = Utils.TimeStamp + (IsSuperShot ? SuperChargeDuration.GetInt() : ChargeDuration.GetInt());
        PhaseEntryDone = false;
        ShotSeq++;
        StartChargeSound(pc);

        byte id = pc.PlayerId;

        // 移動ロック: 旧実装は OnFixedUpdate で毎フレーム pc.TP(StartPosition) で
        // 発射元に固定していたが、50fps × 7秒 = 350 SnapTo で nt.lastSequenceId が
        // ushort overflow → 以降ホスト側で非モッドからの位置 update を「古い sid」
        // 判定で取りこぼし、非モッドが動いても止まって見える同期破綻が発生した。
        // 速度を Main.MinSpeed (0.0001) に落とすことで TP 不要でロックする。
        if ((DebugSkipMask & 8) == 0)
        {
            OriginalSpeed = Main.AllPlayerSpeed[id];
            Main.AllPlayerSpeed[id] = Main.MinSpeed;
            pc.MarkDirtySettings();
        }

        if ((DebugSkipMask & 2) == 0 && Camouflage.PlayerSkins.TryGetValue(id, out var original))
        {
            OriginalOutfit = original;
            var chargeOutfit = new NetworkedPlayerInfo.PlayerOutfit()
                .Set(original.PlayerName, original.ColorId, "", "skin_rhm", "", "", "");
            Camouflage.PlayerSkins[id] = chargeOutfit;
            Camouflage.BlockCamouflage = true;
            if (!Camouflage.IsCamouflage)
                Utils.RpcChangeSkin(pc, chargeOutfit);
        }

        // 超発射で変種が選ばれていれば共有エンジンへ委譲 (Classic / 発動不能時は従来経路の赤太ビーム)
        if (IsSuperShot)
        {
            SuperCannonShot.Variant? variant = SuperCannonShot.PickVariant(SuperCannonType.GetValue());
            if (variant != null)
            {
                Super = new SuperCannonShot(pc, variant.Value, SuperBeamThickness.GetInt(),
                    t => !FriendlyFire.GetBool() && t.GetCustomRole().IsImpostor());
                if (!Super.Begin(StartPosition, Direction))
                {
                    // 変種が発動不能 (確殺: 有効な対象が 1 人もいない 等) → 従来の波動砲で撃つ。
                    // 無言で落ちると「自分の目の前にゲートが出た = 自分がターゲットにされた」と誤認されるため通知する。
                    Super = null;
                    pc.Notify(GetString("WaveCannon.SuperFallback"));
                }
            }
        }

        if (Super == null && (DebugSkipMask & 4) == 0)
        {
            Vector2 gatePos = GatePosition();
            Utils.CombineSendTimeLowering(() =>
            {
                GateCNO = new WaveCannonGate(gatePos);
            });
        }

        // 超チャージは全プレイヤーへ周期キルフラッシュで予告 (JackalHadouHo 双子)。
        // KillFlash は per-player の Reliable RPC → 0.1s 周期だと 10×N msg/s で PacketRateGate (25/s)
        // を持続超過するため 0.5s 周期。PhaseEndTS キャプチャで中断→再発射時の旧タスク混入も防ぐ。
        if (IsSuperShot)
        {
            FlashAll();
            long chargeEndTS = PhaseEndTS;
            const float flashInterval = 0.5f;
            int count = (int)(SuperChargeDuration.GetFloat() / flashInterval);
            for (int i = 1; i <= count; i++)
            {
                float t = i * flashInterval;
                LateTask.New(() =>
                {
                    if (CurrentPhase != Phase.Charging || PhaseEndTS != chargeEndTS || !IsSuperShot || !pc.IsAlive()) return;
                    FlashAll();
                }, t, log: false);
            }
        }
    }

    // チャージ音 (クレッシェンド) は「尻を発射の瞬間 (Firing 開始) に合わせる」端点合わせで鳴らす。
    // 素材は音程が上がり続ける作りなのでループ・尺伸縮は使えない: 残り時間 (charge+warning) が
    // クリップより短ければ途中から再生し、長ければ開始を遅らせて、ピークを必ず発射に着地させる。
    private void StartChargeSound(PlayerControl pc)
    {
        float clipLen = CustomSoundsManager.GetClipLength(ChargeSoundName);
        if (clipLen <= 0f) return;

        float preFire = (IsSuperShot ? SuperChargeDuration.GetInt() : ChargeDuration.GetInt()) + WarningDuration.GetInt();
        if (clipLen >= preFire)
        {
            PlayChargeSound(clipLen - preFire);
        }
        else
        {
            // WarningDuration (最大5s) がクリップ長 4.76s を超えうるため、遅延着火は Warning 中にも落ちる。
            // 世代 (ShotSeq) で中断→再発射時の旧タスクだけを弾く (FlashAll の PhaseEndTS capture と同型)。
            int seq = ShotSeq;
            LateTask.New(() =>
            {
                if (ShotSeq != seq || CurrentPhase is not (Phase.Charging or Phase.Warning) || !pc.IsAlive()) return;
                PlayChargeSound(0f);
            }, preFire - clipLen, log: false);
        }
    }

    private void PlayChargeSound(float startOffset)
    {
        // モッドクライアントにも同じ offset で同期 (ホストローカルの成否とは独立に送る)
        CustomSoundsManager.RpcPlayControllableAll(ChargeSoundName, 1f, startOffset);

        AudioSource src = CustomSoundsManager.PlayControllable(ChargeSoundName);
        if (src == null) return;

        ChargeAudio = src;
        ChargeAudioClip = src.clip;
        if (startOffset > 0f) src.time = startOffset;
        if (Main.Instance != null) Main.Instance.StartCoroutine(ChargeSoundWatch(src, src.clip));
    }

    private void StopChargeSound()
    {
        CustomSoundsManager.RpcStopControllableAll(ChargeSoundName, 0f);
        if (ChargeAudio != null && ChargeAudio.clip == ChargeAudioClip) ChargeAudio.Stop();
        ChargeAudio = null;
        ChargeAudioClip = null;
    }

    private void StartFireSound()
    {
        StopChargeSound();
        CustomSoundsManager.RpcPlayControllableAll(FireSoundName, 1f, 0f);

        AudioSource src = CustomSoundsManager.PlayControllable(FireSoundName);
        if (src == null) return;

        if (Main.Instance != null) Main.Instance.StartCoroutine(FireSoundWatch(src, src.clip));
    }

    // 中断・終了時のクライアント側停止 (ホストローカル側は各 watch が phase 逸脱を検知して止まる)
    private void StopSyncedSounds()
    {
        CustomSoundsManager.RpcStopControllableAll(ChargeSoundName, 0f);
        CustomSoundsManager.RpcStopControllableAll(FireSoundName, FireSoundFadeSeconds);
    }

    // SoundManager のプール済み AudioSource は別の音に再利用されうる (AudienceCutscene.FadeSoundRoutine と同じ罠)。
    // 掴んだ時点のクリップから変わっていたら以降は一切触らない。yield は null のみ (managed enumerator ネスト禁止)。
    private IEnumerator ChargeSoundWatch(AudioSource src, AudioClip ownClip)
    {
        while (src != null && src.clip == ownClip && src.isPlaying)
        {
            // 会議/死亡/中断 (Idle 戻し) で即停止。Firing 遷移時は StartFireSound 側が停止済み
            if (!GameStates.IsInTask || CurrentPhase is Phase.Idle or Phase.Firing)
            {
                src.Stop();
                yield break;
            }

            yield return null;
        }
    }

    // 発射音は素材 26s > FiringDuration 最大 10s のため自然終了に頼らず、Firing を抜けた時点で短フェード停止する
    private IEnumerator FireSoundWatch(AudioSource src, AudioClip ownClip)
    {
        while (src != null && src.clip == ownClip && src.isPlaying)
        {
            if (!GameStates.IsInTask || CurrentPhase != Phase.Firing) break;
            yield return null;
        }

        if (src == null || src.clip != ownClip || !src.isPlaying) yield break;

        float startVol = src.volume;
        for (float t = 0f; t < FireSoundFadeSeconds; t += Time.deltaTime)
        {
            if (src == null || src.clip != ownClip || !src.isPlaying) yield break;
            src.volume = startVol * Mathf.Clamp01(1f - t / FireSoundFadeSeconds);
            yield return null;
        }

        if (src != null && src.clip == ownClip)
        {
            src.volume = 0f;
            src.Stop();
        }
    }

    private static void FlashAll()
    {
        foreach (PlayerControl p in Main.AllAlivePlayerControlsToList)
            p.KillFlash();
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

    // ゲーム終了時にホストから呼ぶ。チャージ/発射中に試合が決着すると OnFixedUpdate 冒頭の
    // !GameStates.IsInTask で降りてしまい、復元経路が3本とも止まって充填中の見た目がロビーへ残る。
    public void RestoreOnGameEnd(byte id)
    {
        PlayerControl pc = Utils.GetPlayerById(id);
        if (pc != null) RestoreSkin(pc);
    }

    private void RestoreSkinDeathPreserve(PlayerControl pc)
    {
        if (OriginalOutfit == null) return;
        Camouflage.PlayerSkins[pc.PlayerId] = OriginalOutfit;
        Camouflage.BlockCamouflage = false;
        OriginalOutfit = null;
    }

    private void RestoreSpeed(PlayerControl pc)
    {
        if (OriginalSpeed <= 0f) return;
        Main.AllPlayerSpeed[pc.PlayerId] = OriginalSpeed;
        pc.MarkDirtySettings();
        OriginalSpeed = 0f;
    }

    private void RestoreSpeedDeathPreserve(PlayerControl pc)
    {
        if (OriginalSpeed <= 0f) return;
        Main.AllPlayerSpeed[pc.PlayerId] = OriginalSpeed;
        OriginalSpeed = 0f;
    }

    public override void OnFixedUpdate(PlayerControl pc)
    {
        if (!AmongUsClient.Instance.AmHost) return;
        if (!GameStates.IsInTask) return;

        if (!pc.IsAlive())
        {
            if (CurrentPhase != Phase.Idle)
            {
                DespawnAllCNOs();
                StopSyncedSounds();
                RestoreSkinDeathPreserve(pc);
                RestoreSpeedDeathPreserve(pc);
                CurrentPhase = Phase.Idle;
            }
            return;
        }

        if (CurrentPhase == Phase.Idle || CurrentPhase == Phase.DirectionDetect)
        {
            // 向き tracking。Idle 中と DirectionDetect 中の両方で deltaX を取り続けて、
            // Charging に入る直前の最新の Direction を確定する。
            Vector2 pos = pc.GetTruePosition();
            if (LastTrackedPos == Vector2.zero) LastTrackedPos = pos;
            else
            {
                float deltaX = pos.x - LastTrackedPos.x;
                if (Mathf.Abs(deltaX) > 0.05f)
                    Direction = deltaX > 0 ? Vector2.right : Vector2.left;
                LastTrackedPos = pos;
            }

            if (CurrentPhase == Phase.Idle) return;

            // DirectionDetect 経過 → Charging へ。動いた先から発射する。
            // Time.time (float) で計測 — Utils.TimeStamp は秒精度 long のため、
            // 秒境界をまたぐと即 trigger される取りこぼし問題があった。
            if (Time.time >= DirectionDetectEndTime)
            {
                StartPosition = pc.GetTruePosition();
                EnterCharging(pc);
            }
            return;
        }

        // 旧 pc.TP(StartPosition) 連発を削除。速度ロック (EnterCharging で MinSpeed) で
        // 移動を抑止しているため、毎フレーム TP は不要。SnapTo の sequenceId 汚染も解消。

        switch (CurrentPhase)
        {
            case Phase.Charging:
                Super?.UpdateCharging();
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
                    if (Super != null) Super.EnterWarning();
                    else SpawnWarning();
                    PhaseEntryDone = true;
                }
                Super?.UpdateWarning();
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
                    if (Super != null)
                    {
                        Super.EnterFiring(FiringDuration.GetFloat());
                    }
                    else
                    {
                        DespawnWarning();
                        SpawnBeam();
                    }
                    AlreadyKilled.Clear();
                    PhaseEntryDone = true;
                    StartFireSound();
                }

                if (Super != null)
                {
                    Super.UpdateFiring();
                }
                else
                {
                    if (BeamCNO != null)
                    {
                        float t = Time.time + ShakePhaseOffset;
                        float wave = Mathf.Sin(t * 47f) * 0.7f + Mathf.Sin(t * 113f + 1.3f) * 0.3f;
                        // 振幅も halfWidth と同じスケール (BeamHalfWidthUnit 比例) に追従
                        float amp = BeamHalfWidthUnit * CurrentThickness() * 0.2f;
                        Vector2 perp = new(-Direction.y, Direction.x);
                        BeamCNO.TP(BeamCNOPosition() + perp * (wave * amp)); // 揺れを毎フレ TP→base が 10Hz(ForceSnapMinInterval)に間引いて滑らかに同期
                    }

                    CheckBeamKills(pc);
                }

                if (Utils.TimeStamp >= PhaseEndTS)
                {
                    DespawnAllCNOs();
                    CustomSoundsManager.RpcStopControllableAll(FireSoundName, FireSoundFadeSeconds);
                    RestoreSkin(pc);
                    RestoreSpeed(pc);
                    pc.SetKillCooldown();
                    // Phantom cooldown が AU 側で自動でかかるので AddAbilityCD 不要
                    CurrentPhase = Phase.Idle;
                    IsSuperShot = false;
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
        if ((DebugSkipMask & 4) != 0) return;
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
        if ((DebugSkipMask & 4) != 0) return;
        Vector2 pos = BeamCNOPosition();
        string sprite = BeamSprite();
        Utils.CombineSendTimeLowering(() =>
        {
            BeamCNO = new WaveCannonBeamSegment(pos, sprite);
        });
    }

    // 超発射 (Classic) は太さとビーム色だけ差し替えて従来経路で撃つ
    private int CurrentThickness()
    {
        return IsSuperShot ? SuperBeamThickness.GetInt() : BeamThickness.GetInt();
    }

    private string WarningSprite()
    {
        // padding パターンで発射方向側のみに ⚠ を表示 (反対側は <alpha=#00> で透明)
        // 区切りは 3 スペース: 5 だとスプライトが 370B になり公式鯖の ~680B パケット制限 (チャンク≈343B+sprite) を超えてキックされる
        string chars = string.Join("   ", Enumerable.Repeat("⚠", WarningCharCount));
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
        int size = CurrentThickness() * BeamSizeUnit;
        string transparent = $"<alpha=#00>{row}";
        string visible = IsSuperShot ? $"<color=#ff0000>{row}</color>" : $"<color=#FF6600FF>{row}</color>";
        return firingRight
            ? $"<size={size}>{transparent}{visible}</size>"
            : $"<size={size}>{visible}{transparent}</size>";
    }

    private void CheckBeamKills(PlayerControl shooter)
    {
        int thick = CurrentThickness();
        int size = thick * BeamSizeUnit;
        // visible 開始点 (ゲート前端) と一致 → 視覚と判定がぴったり揃う
        Vector2 eyePos = BeamStartPoint();
        // プレイヤー本体半径 + ゲート垂直オフセット を吸収するマージン
        float halfWidth = BeamHalfWidthUnit * thick + BeamHitboxMargin;
        float beamLength = BeamLengthUnit * size * BeamCharCount;

        // /hitbox 可視化: 下の判定式と同じ値をそのまま描く (ホストローカルのみ)
        HitboxDebug.DrawBeamRect($"WaveCannon.{shooter.PlayerId}", eyePos, Direction, BeamBackwardReach, beamLength + BeamHitboxMargin, halfWidth);

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
        StopSyncedSounds();
        if (WaveCannonPC != null)
        {
            RestoreSkin(WaveCannonPC);
            RestoreSpeed(WaveCannonPC);
        }
        CurrentPhase = Phase.Idle;
        PhaseEntryDone = false;
        AlreadyKilled.Clear();
        IsSuperShot = false;
    }

    public override void ApplyGameOptions(IGameOptions opt, byte playerId)
    {
        // Phantom basis 化に伴い、AU の vanish/phantom cooldown を発動クールタイムに使う。
        // PhantomDuration は 0.1s (実質瞬間 vanish→appear) にして可視性を維持。
        // PhantomCooldown は (charge+warning+firing) 全体時間 + AbilityCooldown にして、
        // 発射シーケンス終了後におよそ AbilityCooldown 秒の待機が残るようにする。
        // 超波動砲解禁時はチャージの長い方を上限として確保する。
        float charge = LastImpostorSuperCannon.GetBool()
            ? Math.Max(ChargeDuration.GetFloat(), SuperChargeDuration.GetFloat())
            : ChargeDuration.GetFloat();
        AURoleOptions.PhantomDuration = 0.1f;
        AURoleOptions.PhantomCooldown =
            charge + WarningDuration.GetFloat() + FiringDuration.GetFloat()
            + AbilityCooldown.GetFloat();
        Logger.Info($"id={playerId} ability={AbilityCooldown.GetFloat()} charge={ChargeDuration.GetFloat()} warn={WarningDuration.GetFloat()} fire={FiringDuration.GetFloat()} → PhantomCD={AURoleOptions.PhantomCooldown}", "WaveCannon.ApplyGameOptions");
    }
}
