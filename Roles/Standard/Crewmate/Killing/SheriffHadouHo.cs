// Ported from satokazoku/TownOfHost-Pko@42f64574 (Roles/Crewmate/SheriffHadouHo.cs)
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using AmongUs.GameOptions;
using EndKnot.Modules;
using UnityEngine;
using static EndKnot.Options;
using static EndKnot.Translator;

namespace EndKnot.Roles;

// タスクをこなすクルーが、ファントムボタンで向いた方向へ波動砲を撃つ役職。
// 当たった相手は陣営を問わず死ぬ。空振りや味方撃ちの自爆は設定で選べる。
public class SheriffHadouHo : RoleBase
{
    private const int Id = 710100;
    public static bool On;
    public static List<SheriffHadouHo> Instances = [];

    private static OptionItem AbilityCooldown;
    private static OptionItem ChargeDuration;
    private static OptionItem FiringDuration;
    private static OptionItem ShotLimitOpt;
    private static OptionItem SelfDestructModeOpt;
    private static OptionItem BeamColorModeOpt;
    private static OptionItem UnlockTasksOpt;
    private static OptionItem HitKillFlashOpt;

    // 自爆モードの選択肢 (StringOptionItem の値)
    private const int SelfDestructNever = 0;
    private const int SelfDestructOnNoNonCrewHit = 1;
    private const int SelfDestructOnCrewHit = 2;

    // ビーム色の選択肢 (StringOptionItem の値)
    private const int ColorYellow = 0;
    private const int ColorCyan = 1;
    private const int ColorRainbow = 2;

    private const int BeamCharCount = 20;
    private const int BeamSizeUnit = 30;
    private const float BeamHalfWidthUnit = 0.12f;
    private const float BeamLengthUnit = 0.015f;
    private const float BeamHitboxMargin = 0.85f;
    // 判定半幅は太さ 1 単位あたりの比例値。見た目 (<size=太さ×30>) は太さに完全比例するので、
    // 固定オフセットを足すと太い設定ほど見た目と判定がズレる。
    private const float BeamHalfWidthPerThickness = 0.545f;
    private const int BeamThickness = 2;
    private const float DirectionDetectSeconds = 1f;
    private const int WarningSeconds = 1;
    private const int WarningCharCount = 20;
    private const float GateForwardOffset = 1.5f;
    private const float GateUpOffset = 0.3f;
    private const float GateRadius = 0.5f;
    private const float BeamBackwardReach = GateForwardOffset + GateRadius;

    // ホストローカル効果音。チャージはクレッシェンド、発射は持続轟音 (素材は発射時間の最大より長い)
    private const string ChargeSoundName = "HadouCharge";
    private const string FireSoundName = "HadouFire";
    private const float FireSoundFadeSeconds = 0.3f;

    private enum Phase { Idle, DirectionDetect, Charging, Warning, Firing }

    private byte ShhId;
    private PlayerControl ShhPC;
    private Phase CurrentPhase;
    private long PhaseEndTS;
    private float DirectionDetectEndTime;
    private Vector2 StartPosition;
    private Vector2 Direction = Vector2.right;
    private Vector2 LastTrackedPos;
    private float OriginalSpeed;
    private NetworkedPlayerInfo.PlayerOutfit OriginalOutfit;
    private readonly HashSet<byte> AlreadyKilled = [];
    private bool PhaseEntryDone;
    private bool CutInSent;
    // この一発でビームの判定に入った相手の内訳 (キルが関所で弾かれても数える)
    private bool HitNonCrew;
    private bool HitCrew;
    private float ShakePhaseOffset;
    private WaveCannonWarning WarningCNO;
    private WaveCannonBeamSegment BeamCNO;
    private WaveCannonGate GateCNO;
    private Vector2? FxGate;
    private ExplosionFx.CannonPalette FxPal;
    private AudioSource ChargeAudio;
    private AudioClip ChargeAudioClip;
    private int ShotSeq; // 発射シーケンス世代。中断→再発射時に旧 LateTask のチャージ音を無効化する

    private int ShotsLeft;
    private (bool Busy, int Shots)? LastSent;

    public override bool IsEnable => Instances.Count > 0;

    public override void SetupCustomOption()
    {
        SetupRoleOptions(Id, TabGroup.CrewmateRoles, CustomRoles.SheriffHadouHo);

        AbilityCooldown = new FloatOptionItem(Id + 10, "SheriffHadouHoCooldown", new(0f, 60f, 0.5f), 30f, TabGroup.CrewmateRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.SheriffHadouHo])
            .SetValueFormat(OptionFormat.Seconds);
        ChargeDuration = new FloatOptionItem(Id + 11, "SheriffHadouHoChargeTime", new(0.5f, 10f, 0.5f), 3f, TabGroup.CrewmateRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.SheriffHadouHo])
            .SetValueFormat(OptionFormat.Seconds);
        FiringDuration = new FloatOptionItem(Id + 12, "SheriffHadouHoBeamTime", new(0.5f, 10f, 0.5f), 3f, TabGroup.CrewmateRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.SheriffHadouHo])
            .SetValueFormat(OptionFormat.Seconds);
        ShotLimitOpt = new IntegerOptionItem(Id + 13, "SheriffHadouHoShotLimit", new(1, 15, 1), 3, TabGroup.CrewmateRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.SheriffHadouHo])
            .SetValueFormat(OptionFormat.Times);
        SelfDestructModeOpt = new StringOptionItem(Id + 14, "SheriffHadouHoSelfDestruct", ["SheriffHadouHoSelfDestructNever", "SheriffHadouHoSelfDestructNoNonCrewHit", "SheriffHadouHoSelfDestructCrewHit"], SelfDestructOnNoNonCrewHit, TabGroup.CrewmateRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.SheriffHadouHo]);
        BeamColorModeOpt = new StringOptionItem(Id + 15, "SheriffHadouHoBeamColorMode", ["SheriffHadouHoBeamColorYellow", "SheriffHadouHoBeamColorCyan", "SheriffHadouHoBeamColorRainbow"], ColorYellow, TabGroup.CrewmateRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.SheriffHadouHo]);
        UnlockTasksOpt = new IntegerOptionItem(Id + 16, "SheriffHadouHoBeamUnlockTask", new(0, 10, 1), 0, TabGroup.CrewmateRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.SheriffHadouHo])
            .SetValueFormat(OptionFormat.Times);
        HitKillFlashOpt = new BooleanOptionItem(Id + 17, "SheriffHadouHoHitKillFlash", true, TabGroup.CrewmateRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.SheriffHadouHo]);
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
        ShhId = playerId;
        ShhPC = playerId.GetPlayer();
        ShotsLeft = ShotLimitOpt.GetInt();
        ResetState();
        LastSent = null;
    }

    public override void Remove(byte playerId)
    {
        Instances.RemoveAll(x => x.ShhId == playerId);
        if (Instances.Count == 0) On = false;
    }

    // 発射中かと残り回数は各クライアントが自分でボタンと名前の下の表示を決めるので、変わった時だけモッド客へ送る
    private void SyncToModdedClients()
    {
        (bool, int) now = (CurrentPhase != Phase.Idle, ShotsLeft);
        if (LastSent == now) return;

        LastSent = now;
        Utils.SendRPC(CustomRPC.SyncRoleData, ShhId, now.Item1, now.Item2);
    }

    public void ReceiveRPC(Hazel.MessageReader reader)
    {
        CurrentPhase = reader.ReadBoolean() ? Phase.Charging : Phase.Idle;
        ShotsLeft = reader.ReadInt32();
    }

    public override bool CanUseKillButton(PlayerControl pc) => false;

    public override bool CanUseImpostorVentButton(PlayerControl pc) => false;

    public override bool CanUseSabotage(PlayerControl pc) => false;

    public override void ApplyGameOptions(IGameOptions opt, byte playerId)
    {
        opt.SetVision(false);
        AURoleOptions.PhantomDuration = 0.1f;

        float cd = AbilityCooldown.GetFloat();

        // イントロ直後の PreventKill 窓では OnVanish が呼ばれず CD だけリセットされる (初回押下が無音で不発)。
        // 窓の長さは固定 10 秒ではなく Options.StartingKillCooldown なので、それに合わせてクランプする。
        if (IntroCutsceneDestroyPatch.PreventKill)
            cd = Mathf.Max(cd, (Options.StartingKillCooldown?.GetFloat() ?? 10f) + 2f);

        AURoleOptions.PhantomCooldown = cd;
    }

    private bool IsUnlocked(PlayerControl pc)
    {
        int need = UnlockTasksOpt.GetInt();
        return need <= 0 || pc.GetTaskState().CompletedTasksCount >= need;
    }

    public override bool OnVanish(PlayerControl pc)
    {
        TriggerCharge(pc);
        return false;
    }

    private void TriggerCharge(PlayerControl pc)
    {
        if (!pc.IsAlive() || CurrentPhase != Phase.Idle || !GameStates.IsInTask) return;

        if (ShotsLeft <= 0)
        {
            pc.Notify(GetString("SheriffHadouHo.HudEmpty"));
            return;
        }

        if (!IsUnlocked(pc))
        {
            int remaining = UnlockTasksOpt.GetInt() - pc.GetTaskState().CompletedTasksCount;
            pc.Notify(string.Format(GetString("SheriffHadouHo.Locked"), remaining));
            return;
        }

        CurrentPhase = Phase.DirectionDetect;
        DirectionDetectEndTime = Time.time + DirectionDetectSeconds;
        PhaseEntryDone = false;
        pc.Notify(GetString("JackalHadouHo.DirectionDetect"));
    }

    private void EnterCharging(PlayerControl pc)
    {
        CurrentPhase = Phase.Charging;
        CutInSent = false;
        float dur = ChargeDuration.GetFloat();
        PhaseEndTS = Utils.TimeStamp + (long)dur;
        PhaseEntryDone = false;
        ShotSeq++;

        byte id = pc.PlayerId;

        // 移動ロック (毎フレーム TP は SnapTo sid 汚染を引き起こすため速度方式)
        OriginalSpeed = Main.AllPlayerSpeed[id];
        Main.AllPlayerSpeed[id] = Main.MinSpeed;
        pc.MarkDirtySettings();

        if (Camouflage.PlayerSkins.TryGetValue(id, out var original))
        {
            OriginalOutfit = original;
            int chargeColor = original.ColorId == 5 ? 4 : 5;
            var chargeOutfit = new NetworkedPlayerInfo.PlayerOutfit()
                .Set(original.PlayerName, chargeColor, "", "", "", "", "");
            Camouflage.PlayerSkins[id] = chargeOutfit;
            Camouflage.BlockCamouflage = true;
            if (!Camouflage.IsCamouflage)
                Utils.RpcChangeSkin(pc, chargeOutfit);
        }

        StartChargeSound(pc);

        Vector2 gatePos = GatePosition();
        Utils.CombineSendTimeLowering(() =>
        {
            GateCNO = new WaveCannonGate(gatePos);
        });

        FxGate = gatePos;
        FxPal = BeamColorModeOpt.GetValue() switch
        {
            ColorCyan => ExplosionFx.CannonPalette.Cyan,
            ColorRainbow => ExplosionFx.CannonPalette.Rainbow,
            _ => ExplosionFx.CannonPalette.Orange
        };
        ExplosionFx.CannonCharge(gatePos, Direction.x > 0, dur + WarningSeconds, BeamThickness, FxPal);

        // 全プレイヤーにキルフラッシュ
        FlashAll();

        Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc);
    }

    private static void FlashAll()
    {
        foreach (PlayerControl p in Main.AllAlivePlayerControlsToList)
            p.KillFlash();
    }

    private void StartChargeSound(PlayerControl pc)
    {
        float clipLen = CustomSoundsManager.GetClipLength(ChargeSoundName);
        if (clipLen <= 0f) return;

        // フェーズの終端は秒の境目で切り替わるので、設定秒数でなく終端までの実時間から逆算する (設定秒数だと最大 1 秒ずれて頂点が切れる)
        float preFire = Mathf.Max(0.05f, (float)Utils.SecondsUntil(PhaseEndTS) + WarningSeconds);
        if (clipLen >= preFire)
        {
            PlayChargeSound(clipLen - preFire);
        }
        else
        {
            // 遅延着火は Warning 中にも落ちうる。
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
        // 音はゲートの位置から聞こえる。遠くの人には届かない
        Vector2 gate = GatePosition();
        CustomSoundsManager.RpcPlayControllableAll(ChargeSoundName, 1f, startOffset, gate);

        // 聞こえない距離でも音量 0 で鳴らしておき、ChargeSoundWatch が距離に合わせて付け直す
        AudioSource src = CustomSoundsManager.PlayControllable(ChargeSoundName, CustomSoundsManager.PositionalVolume(gate, gate));
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

    // 発射音の音源 = ビームの通り道 (ゲート前端から射程の端まで)
    private (Vector2 Start, Vector2 End) FireSoundSpan()
    {
        Vector2 start = BeamStartPoint();
        return (start, start + Direction * (BeamLengthUnit * CurrentThickness() * BeamSizeUnit * BeamCharCount));
    }

    private void StartFireSound()
    {
        StopChargeSound();
        // 発射音はビームの通り道 (ゲート前端から射程の端まで) に近いほど大きい
        (Vector2 beamStart, Vector2 beamEnd) = FireSoundSpan();
        CustomSoundsManager.RpcPlayControllableAll(FireSoundName, 1f, 0f, beamStart, beamEnd);

        AudioSource src = CustomSoundsManager.PlayControllable(FireSoundName, CustomSoundsManager.PositionalVolume(beamStart, beamEnd));
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

            if (Time.frameCount % 6 == 0)
            {
                Vector2 gate = GatePosition();
                src.volume = CustomSoundsManager.PositionalVolume(gate, gate);
            }

            yield return null;
        }
    }

    // 発射音は素材 16s > FiringDuration 最大 10s のため自然終了に頼らず、Firing を抜けた時点で短フェード停止する
    private IEnumerator FireSoundWatch(AudioSource src, AudioClip ownClip)
    {
        while (src != null && src.clip == ownClip && src.isPlaying)
        {
            if (!GameStates.IsInTask || CurrentPhase != Phase.Firing) break;

            if (Time.frameCount % 6 == 0)
            {
                (Vector2 beamStart, Vector2 beamEnd) = FireSoundSpan();
                src.volume = CustomSoundsManager.PositionalVolume(beamStart, beamEnd);
            }

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

    private void RestoreSkin(PlayerControl pc, bool deathPreserve = false)
    {
        if (OriginalOutfit == null) return;
        Camouflage.PlayerSkins[pc.PlayerId] = OriginalOutfit;
        Camouflage.BlockCamouflage = false;
        if (!deathPreserve && !Camouflage.IsCamouflage)
            Utils.RpcChangeSkin(pc, OriginalOutfit);
        OriginalOutfit = null;
    }

    // ゲーム終了時にホストから呼ぶ (WaveCannon と同型 — 復元経路が全て試合継続前提)
    public void RestoreOnGameEnd(byte id)
    {
        PlayerControl pc = Utils.GetPlayerById(id);
        if (pc != null) RestoreSkin(pc);
    }

    private void RestoreSpeed(PlayerControl pc, bool deathPreserve = false)
    {
        if (OriginalSpeed <= 0f) return;
        Main.AllPlayerSpeed[pc.PlayerId] = OriginalSpeed;
        if (!deathPreserve) pc.MarkDirtySettings();
        OriginalSpeed = 0f;
    }


    private void ResetState()
    {
        CurrentPhase = Phase.Idle;
        PhaseEndTS = 0;
        StartPosition = Vector2.zero;
        LastTrackedPos = Vector2.zero;
        OriginalSpeed = 0f;
        AlreadyKilled.Clear();
        PhaseEntryDone = false;
        HitNonCrew = false;
        HitCrew = false;
        DespawnAllCNOs();
    }

    private void DespawnAllCNOs()
    {
        EndCannonFx(true);
        WarningCNO?.Despawn();
        WarningCNO = null;
        BeamCNO?.Despawn();
        BeamCNO = null;
        GateCNO?.Despawn();
        GateCNO = null;
    }

    public override void OnFixedUpdate(PlayerControl pc)
    {
        if (!AmongUsClient.Instance.AmHost) return;
        if (!GameStates.IsInTask) return;

        SyncToModdedClients();

        if (!pc.IsAlive())
        {
            if (CurrentPhase != Phase.Idle)
            {
                DespawnAllCNOs();
                StopSyncedSounds();
                RestoreSkin(pc, deathPreserve: true);
                RestoreSpeed(pc, deathPreserve: true);
                CurrentPhase = Phase.Idle;
            }
            return;
        }

        if (CurrentPhase == Phase.Idle || CurrentPhase == Phase.DirectionDetect)
        {
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

            // 向き検出の時間が経ったら、動いた先からチャージを始める。
            // Time.time (float) で計測 — Utils.TimeStamp は秒境界またぎ取りこぼしあり。
            if (Time.time >= DirectionDetectEndTime)
            {
                StartPosition = pc.GetTruePosition();
                EnterCharging(pc);
            }
            return;
        }

        switch (CurrentPhase)
        {
            case Phase.Charging:
                TryCutIn(pc, Utils.SecondsUntil(PhaseEndTS) + WarningSeconds);
                if (Utils.TimeStamp >= PhaseEndTS)
                {
                    CurrentPhase = Phase.Warning;
                    PhaseEndTS = Utils.TimeStamp + WarningSeconds;
                    PhaseEntryDone = false;
                }
                break;

            case Phase.Warning:
                if (!PhaseEntryDone)
                {
                    SpawnWarning();
                    PhaseEntryDone = true;
                }
                TryCutIn(pc, Utils.SecondsUntil(PhaseEndTS));
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
                    HitNonCrew = false;
                    HitCrew = false;
                    ShotsLeft--;
                    PhaseEntryDone = true;
                    StartFireSound();
                }

                if (BeamCNO != null)
                {
                    float t = Time.time + ShakePhaseOffset;
                    float wave = Mathf.Sin(t * 47f) * 0.7f + Mathf.Sin(t * 113f + 1.3f) * 0.3f;
                    float amp = BeamHalfWidthUnit * BeamThickness * 0.2f;
                    Vector2 perp = new(-Direction.y, Direction.x);
                    BeamCNO.TP(BeamCNOPosition() + perp * (wave * amp)); // 揺れを毎フレ TP→base が 10Hz に間引いて滑らかに同期
                }

                CheckBeamKills(pc);

                if (Utils.TimeStamp >= PhaseEndTS)
                {
                    EndCannonFx(false);
                    DespawnAllCNOs();
                    CustomSoundsManager.RpcStopControllableAll(FireSoundName, FireSoundFadeSeconds);
                    RestoreSkin(pc);
                    RestoreSpeed(pc);

                    bool selfDestruct = SelfDestructModeOpt.GetValue() switch
                    {
                        SelfDestructOnNoNonCrewHit => !HitNonCrew,
                        SelfDestructOnCrewHit => HitCrew,
                        _ => false
                    };

                    CurrentPhase = Phase.Idle;
                    PhaseEntryDone = false;
                    HitNonCrew = false;
                    HitCrew = false;

                    if (selfDestruct)
                    {
                        pc.Suicide();
                    }
                    else
                    {
                        // 発射し終えた時点からクールダウンを数え直す
                        LateTask.New(() =>
                        {
                            if (pc != null && pc.IsAlive() && GameStates.IsInTask) pc.RpcResetAbilityCooldown();
                        }, 0.2f, log: false);
                    }

                    Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc);
                }
                break;
        }
    }

    private Vector2 GatePosition() => StartPosition + new Vector2(Direction.x * GateForwardOffset, GateUpOffset);
    private Vector2 BeamStartPoint() => GatePosition() + Direction * GateRadius;
    private Vector2 BeamCNOPosition() => BeamStartPoint();
    private Vector2 WarningCNOPosition() => BeamStartPoint();

    private void SpawnWarning()
    {
        Vector2 pos = WarningCNOPosition();
        string sprite = WarningSprite();
        Utils.CombineSendTimeLowering(() => { WarningCNO = new WaveCannonWarning(pos, sprite); });
    }

    private void DespawnWarning()
    {
        WarningCNO?.Despawn();
        WarningCNO = null;
    }

    // 帯が閉じ切るのと同時にビームが出るよう、発射の少し前にカットインを出す
    private void TryCutIn(PlayerControl pc, double untilFire)
    {
        if (CutInSent || untilFire > ExplosionFx.CannonCutInLead) return;

        CutInSent = true;
        ExplosionFx.CannonCutIn(pc.PlayerId, ExplosionFx.CannonTitle.WaveCannon, FxPal);
    }

    private void SpawnBeam()
    {
        Vector2 pos = BeamCNOPosition();
        string sprite = BeamSprite();
        Utils.CombineSendTimeLowering(() => { BeamCNO = new WaveCannonBeamSegment(pos, sprite); });

        if (FxGate is { } gate) ExplosionFx.CannonBeam(gate, Direction.x > 0, FiringDuration.GetFloat(), BeamThickness, FxPal);
    }

    private int CurrentThickness() => BeamThickness;

    // 魔法陣とビームの演出 (ホストと End K not のクライアントだけに見える)。撃ち終わりは消え、途中で止まったら砕ける
    private void EndCannonFx(bool broken)
    {
        if (FxGate is { } gate) ExplosionFx.CannonEnd(gate, FxPal, broken);
        FxGate = null;
    }

    private string WarningSprite()
    {
        // 区切り 3 スペース: 5 だと公式鯖の ~680B パケット制限を超えてキック (WaveCannon 側と同じ)
        string chars = string.Join("   ", Enumerable.Repeat("⚠", WarningCharCount));
        bool firingRight = Direction.x > 0;
        int size = BeamThickness * BeamSizeUnit;
        return firingRight
            ? $"<size={size}><alpha=#00>{chars}<color=#FFFF00FF>{chars}</color></size>"
            : $"<size={size}><color=#FFFF00FF>{chars}</color><alpha=#00>{chars}</size>";
    }

    private string BeamSprite()
    {
        bool firingRight = Direction.x > 0;
        int size = BeamThickness * BeamSizeUnit;
        string row = new('━', BeamCharCount);
        string transparent = $"<alpha=#00>{row}";
        string visible = BuildBeamVisible(row);
        return firingRight
            ? $"<size={size}>{transparent}{visible}</size>"
            : $"<size={size}>{visible}{transparent}</size>";
    }

    private string BuildBeamVisible(string row)
    {
        switch (BeamColorModeOpt.GetValue())
        {
            case ColorYellow:
                return $"<color=#f8cd46>{row}</color>";
            case ColorCyan:
                return $"<color=#00CFFF>{row}</color>";
        }

        // レインボー: 7 色を BeamCharCount 文字に均等配分
        string[] colors = { "#ff0000", "#ff7f00", "#ffff00", "#00ff00", "#0000ff", "#4b0082", "#8b00ff" };
        var sb = new System.Text.StringBuilder();
        int per = Mathf.Max(1, BeamCharCount / colors.Length);
        int written = 0;
        for (int i = 0; i < colors.Length; i++)
        {
            int len = (i == colors.Length - 1) ? BeamCharCount - written : per;
            if (len <= 0) continue;
            sb.Append($"<color={colors[i]}>").Append(new string('━', len)).Append("</color>");
            written += len;
        }
        return sb.ToString();
    }

    // 人外 = クルー陣営以外の全部 (マッドメイト属性・コンバート済みも含む)
    private static bool IsNonCrew(PlayerControl target)
    {
        return !target.IsCrewmate() || target.IsMadmate() || target.IsConverted();
    }

    private void CheckBeamKills(PlayerControl shooter)
    {
        int size = BeamThickness * BeamSizeUnit;
        Vector2 eyePos = BeamStartPoint();
        float halfWidth = BeamHalfWidthPerThickness * BeamThickness;
        float beamLength = BeamLengthUnit * size * BeamCharCount;

        // /hitbox 可視化: 下の判定式と同じ値をそのまま描く (ホストローカルのみ)
        HitboxDebug.DrawBeamRect($"SheriffHadouHo.{shooter.PlayerId}", eyePos, Direction, BeamBackwardReach, beamLength + BeamHitboxMargin, halfWidth);

        foreach (PlayerControl target in Main.EnumerateAlivePlayerControls())
        {
            if (target.PlayerId == shooter.PlayerId) continue;
            if (AlreadyKilled.Contains(target.PlayerId)) continue;
            if (Pelican.IsEaten(target.PlayerId)) continue;
            if (target.Is(CustomRoles.Pestilence)) continue;

            Vector2 delta = target.GetTruePosition() - eyePos;
            float along = Vector2.Dot(delta, Direction);
            if (along < -BeamBackwardReach || along > beamLength + BeamHitboxMargin) continue;
            Vector2 lateral = delta - Direction * along;
            if (lateral.magnitude > halfWidth) continue;

            // 弾かれた相手も「この一発では処理済み」にしておく。
            // 関所は弾くたびキラーへ通知を送るので、印を後回しにすると同じ相手への送信が毎フレーム繰り返される。
            AlreadyKilled.Add(target.PlayerId);

            // 判定に入った時点で命中として数える (関所でキルが弾かれても数える)
            if (IsNonCrew(target)) HitNonCrew = true;
            else HitCrew = true;
            if (HitKillFlashOpt.GetBool()) shooter.KillFlash();

            // 下の Kill() が弾く状況 (ロビー・試合外・イントロ中) では、死亡状態だけ同期されて死体が出ない半端な死に方になるので撃たない
            if (GameStates.IsLobby || !GameStates.InGame || !Main.IntroDestroyed) continue;

            if (!CheckMurderPatch.PassesGate(shooter, target, kind: AttackKind.Execution)) continue;
            PlayerState state = Main.PlayerStates[target.PlayerId];
            // 生フィールド代入だと客へ SetRealKiller の RPC が飛ばず、非ホスト側の死因表示がズレる。
            target.SetRealKiller(shooter);
            RPC.PlaySoundRPC(shooter.PlayerId, Sounds.KillSound);
            // 死体の残る普通のキルにする (死後の処理はキルのパッチ側で走る)。死因の「蒸発」で、撃たれた本人の画面には専用のキル演出が出る
            state.deathReason = PlayerState.DeathReason.Vaporized;
            state.SetDead();
            Medic.IsDead(target);
            target.Kill(target);
        }
    }

    public override void OnReportDeadBody()
    {
        if (CurrentPhase != Phase.Idle)
        {
            DespawnAllCNOs();
            StopSyncedSounds();
            if (ShhPC != null)
            {
                RestoreSkin(ShhPC);
                RestoreSpeed(ShhPC);
            }
            CurrentPhase = Phase.Idle;
            PhaseEntryDone = false;
            AlreadyKilled.Clear();
            HitNonCrew = false;
            HitCrew = false;
        }
    }

    // 発射シーケンス中は移動が縛られている。ジップラインと動く床で抜け出せないようにするための問い合わせ口
    public static bool IsInShotSequence(byte playerId)
    {
        return On && Instances.Exists(x => x.ShhId == playerId && x.CurrentPhase != Phase.Idle);
    }

    public override string GetProgressText(byte playerId, bool comms)
    {
        string shots = Utils.ColorString(ShotsLeft > 0 ? Color.yellow : Color.gray, $"({ShotsLeft})");
        return shots + Utils.GetTaskCount(playerId, comms);
    }

    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud = false, bool meeting = false)
    {
        if (meeting || seer.PlayerId != ShhId || seer.PlayerId != target.PlayerId || !seer.IsAlive()) return string.Empty;

        if (CurrentPhase != Phase.Idle)
            return $"<color=#ff0000>{GetString("SheriffHadouHo.HudCharging")}</color>";

        if (ShotsLeft <= 0)
            return $"<color=#888888>{GetString("SheriffHadouHo.HudEmpty")}</color>";

        if (!IsUnlocked(seer))
        {
            int remaining = UnlockTasksOpt.GetInt() - seer.GetTaskState().CompletedTasksCount;
            return $"<color=#888888>{string.Format(GetString("SheriffHadouHo.Locked"), remaining)}</color>";
        }

        return $"<color=#f8cd46>{GetString("SheriffHadouHo.HudReady")}</color>";
    }
}
