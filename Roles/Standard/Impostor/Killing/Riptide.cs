using System;
using System.Collections.Generic;
using System.Linq;
using AmongUs.GameOptions;
using EndKnot.Modules;
using UnityEngine;
using static EndKnot.Options;
using static EndKnot.Translator;

namespace EndKnot.Roles;

// ============================================================
// Riptide (リップタイド) — Impostor/Killing
//
// コンセプト:
//   マップ全体を覆う巨大な波が画面端から押し寄せ、
//   波内にいると速度低下 + 一定秒数で即死する。
//   会議で波が引いて、次のタスクターン開始でまた迫ってくる。
//   ゲーム進行 (会議回数) に伴って波速度が加速、方向数も増える。
//   Riptide が死亡/追放されたら波は止まる。
//   本人のみ免疫。他 Impostor 仲間も巻き込む (FFA 風)。
//
// 注意: 他 Impostor の巻き込みは CheckMurder 経路ではなく
//       直接 RpcExileV2 で行うため、Madmate 等の通常ガード外。
//       これは設計上の意図的仕様。
//
// 波の spawn は AfterMeetingTasks での fire-and-forget。
// 各波はマップを横断し終えたら自然消滅。
// 会議入りで全波即 cleanup。Riptide 死亡でも全波 cleanup + 以後 spawn 停止。
// ============================================================
public class Riptide : RoleBase
{
    private const int Id = 703780;
    public static bool On;
    private static List<Riptide> Instances = [];

    // ---- static options (読み込みは Init() で) ----
    private static OptionItem EnableAccelerationOpt;
    private static OptionItem AccelerationRateOpt;
    private static OptionItem BaseWaveSpeedOpt;
    private static OptionItem SlowMultiplierOpt;
    private static OptionItem DeathStaySecondsOpt;
    private static OptionItem MaxConcurrentDirectionsOpt;
    private static OptionItem CanKillManuallyOpt;
    private static OptionItem KillCooldownOpt;
    private static OptionItem WaveBandThicknessOpt;
    private static OptionItem WaveLateralExtentOpt;
    private static OptionItem ShowPredictiveGhostOpt;

    // ---- cached option values (Init() で読む) ----
    private static float BaseWaveSpeed;
    private static bool EnableAcceleration;
    private static float AccelerationRate;
    private static float SlowMultiplier;
    private static float DeathStaySeconds;
    private static int MaxConcurrentDirections;
    private static bool CanKillManually;
    private static int KillCooldown;
    private static float WaveBandThickness;
    private static float WaveLateralExtent;
    private static bool ShowPredictiveGhost;

    // ---- per-instance fields ----
    private PlayerControl RiptidePC;
    private bool Dead;                  // 役職死亡済フラグ (cleanup 二重発火防止)
    private int LastSpawnMeetingNum;    // AfterMeetingTasks 重複発火防止
    private readonly List<RiptideWaveState> ActiveWaves = [];

    // ---- グローバル速度状態 (バグ修正 #fix: per-wave OriginalSpeeds は複数波重複で壊れるため統合) ----
    // 複数の波に同時に入っている場合、最初の波入場時の素の速度を保持し参照カウントで管理
    private Dictionary<byte, (float OriginalSpeed, int WaveRefCount)> GlobalSlowState = [];

    // ---- 方向定義 (4 方向) ----
    // DirectionIndex: 0=左→右, 1=右→左, 2=上→下, 3=下→上
    private static readonly Vector2[] WaveDirections =
    {
        Vector2.right,  // 0: 左→右
        Vector2.left,   // 1: 右→左
        Vector2.down,   // 2: 上→下
        Vector2.up,     // 3: 下→上
    };

    // マップ境界 (Init/Add で取得)
    private static bool MapBoundsValid;
    private static Vector2 MapMin;
    private static Vector2 MapMax;
    private static Vector2 MapCenter;

    // CNO 視覚オフセット (TMP bottom-center anchor 仕様):
    //   FontSizeAbsolute=40 × 8 行 ≈ 53u 高さ → 半分 ~27u
    private const float VisualVerticalOffset = 27f;
    // スプライト視覚幅/高さ (size=40 で ~53u/CNO):
    private const float VisualSpriteExtent = 53f;

    public override bool IsEnable => On;

    // ============================================================
    // Setup
    // ============================================================
    public override void SetupCustomOption()
    {
        var setup = StartSetup(Id);

        // id+0 = spawn chance, id+1 = count (SetupRoleOptions が消費)
        // AutoSetupOption は id+2 から開始

        setup
            // 8×8 W グリッド size=20 で視覚 ~28u → 帯厚も 28u 相当 (半分 14)、波速度も少し速め
            .AutoSetupOption(ref BaseWaveSpeedOpt, 2.5f, new FloatValueRule(0.5f, 10f, 0.25f), OptionFormat.Multiplier)
            .AutoSetupOption(ref EnableAccelerationOpt, true)
            .AutoSetupOption(ref AccelerationRateOpt, 0.15f, new FloatValueRule(0f, 1f, 0.05f), OptionFormat.Multiplier, overrideParent: EnableAccelerationOpt)
            .AutoSetupOption(ref SlowMultiplierOpt, 0.5f, new FloatValueRule(0.1f, 1f, 0.05f), OptionFormat.Multiplier)
            .AutoSetupOption(ref DeathStaySecondsOpt, 3f, new FloatValueRule(0.5f, 15f, 0.5f), OptionFormat.Seconds)
            .AutoSetupOption(ref MaxConcurrentDirectionsOpt, 3, new IntegerValueRule(1, 8, 1), OptionFormat.Times)
            .AutoSetupOption(ref CanKillManuallyOpt, true)
            .AutoSetupOption(ref KillCooldownOpt, 30, new IntegerValueRule(0, 120, 1), OptionFormat.Seconds, overrideParent: CanKillManuallyOpt)
            .AutoSetupOption(ref WaveBandThicknessOpt, 60f, new FloatValueRule(0.5f, 120f, 0.5f), OptionFormat.Multiplier)
            .AutoSetupOption(ref WaveLateralExtentOpt, 80f, new FloatValueRule(10f, 200f, 5f), OptionFormat.Multiplier)
            .AutoSetupOption(ref ShowPredictiveGhostOpt, false);
    }

    public override void Init()
    {
        On = false;
        Instances = [];

        // option 読込
        BaseWaveSpeed = BaseWaveSpeedOpt?.GetFloat() ?? 1.0f;
        EnableAcceleration = EnableAccelerationOpt?.GetBool() ?? true;
        AccelerationRate = AccelerationRateOpt?.GetFloat() ?? 0.15f;
        SlowMultiplier = SlowMultiplierOpt?.GetFloat() ?? 0.5f;
        DeathStaySeconds = DeathStaySecondsOpt?.GetFloat() ?? 3f;
        MaxConcurrentDirections = MaxConcurrentDirectionsOpt?.GetInt() ?? 3;
        CanKillManually = CanKillManuallyOpt?.GetBool() ?? true;
        KillCooldown = KillCooldownOpt?.GetInt() ?? 30;
        WaveBandThickness = WaveBandThicknessOpt?.GetFloat() ?? 4f;
        WaveLateralExtent = WaveLateralExtentOpt?.GetFloat() ?? 40f;
        ShowPredictiveGhost = ShowPredictiveGhostOpt?.GetBool() ?? false;

        // マップ境界
        try
        {
            var spawnMap = RandomSpawn.SpawnMap.GetSpawnMap();
            var positions = spawnMap.Positions.Values.ToList();
            if (positions.Count > 0)
            {
                float minX = float.MaxValue, minY = float.MaxValue;
                float maxX = float.MinValue, maxY = float.MinValue;
                foreach (var pos in positions)
                {
                    if (pos.x < minX) minX = pos.x;
                    if (pos.y < minY) minY = pos.y;
                    if (pos.x > maxX) maxX = pos.x;
                    if (pos.y > maxY) maxY = pos.y;
                }
                // spawn 点はマップ実端から 20 unit 外側 (margin 15 + GetWaveStartPosition の -5)。
                // 視覚スプライト幅 ~28u の半分が wave.Position 中心から +X (or 対称) 方向に張り出すので、
                // 実質スプライト前縁はマップ端から 6u 外側 → spawn 時に画面外から登場する演出。
                // 過去 (margin 50f) は spawn が遠すぎて波が到達するまで時間がかかりすぎていた。
                MapMin = new Vector2(minX - 15f, minY - 15f);
                MapMax = new Vector2(maxX + 15f, maxY + 15f);
                MapCenter = new Vector2((minX + maxX) / 2f, (minY + maxY) / 2f);
                MapBoundsValid = true;
            }
            else
            {
                MapBoundsValid = false;
            }
        }
        catch (Exception e)
        {
            Logger.Error($"Riptide: MapBounds 計算失敗 → 全機能無効化: {e.Message}", "Riptide.Init");
            MapBoundsValid = false;
        }
    }

    public override void Add(byte playerId)
    {
        On = true;
        Instances.Add(this);
        RiptidePC = playerId.GetPlayer();
        Dead = false;
        LastSpawnMeetingNum = -1;
        ActiveWaves.Clear();
        GlobalSlowState = [];
    }

    public override void Remove(byte playerId)
    {
        Instances.RemoveAll(x => x.RiptidePC?.PlayerId == playerId);
        if (Instances.Count == 0) On = false;
    }

    // ============================================================
    // ApplyGameOptions — 通常 Impostor なので kill cooldown のみ設定
    // ============================================================
    public override void ApplyGameOptions(IGameOptions opt, byte playerId)
    {
        if (CanKillManually)
            Main.AllPlayerKillCooldown[playerId] = KillCooldown;
    }

    // ============================================================
    // OnCheckMurder — 手動キル制御
    // ============================================================
    public override bool OnCheckMurder(PlayerControl killer, PlayerControl target)
    {
        if (!CanKillManually) return false;
        return true;
    }

    // ============================================================
    // AfterMeetingTasks — 会議後に波を spawn (fire-and-forget)
    // ============================================================
    public override void AfterMeetingTasks()
    {
        if (!AmongUsClient.Instance.AmHost) return;
        if (!MapBoundsValid) return;
        if (RiptidePC == null || !RiptidePC.IsAlive()) return;
        if (Dead) return;

        // 初回会議はスキップ (罠対策 #3)
        if (MeetingStates.FirstMeeting) return;

        // 同一 MeetingNum での二重発火防止 (罠対策 #2)
        int meetingNum = MeetingStates.MeetingNum;
        if (LastSpawnMeetingNum == meetingNum) return;
        LastSpawnMeetingNum = meetingNum;

        // この会議ラウンドの方向数 = min(MeetingNum, MaxConcurrentDirections)
        // (MeetingNum は AfterMeetingTasks 時点で 1 以上、初回会議後で 1)
        int directionCount = Math.Min(meetingNum, MaxConcurrentDirections);

        // 波速度 = base × (1 + rate × MeetingNum) — acceleration option
        float speed = BaseWaveSpeed;
        if (EnableAcceleration)
            speed *= (1f + AccelerationRate * meetingNum);

        // LateTask + frame 分散で CNO packet を分割 (罠対策 #1, #13)
        // CreateNetObject は PlayerControl.AllPlayerControls を Add するため
        // 親 foreach を破壊する。LateTask で遅延させる (DummySpawner パターン)
        for (int i = 0; i < directionCount; i++)
        {
            int dirIdx = i % WaveDirections.Length;
            float capturedSpeed = speed;
            int capturedDir = dirIdx;
            // 1 CNO spawn は 3 段の reliable packet (Spawn / Hide loop / Shapeshift trick) を内部で発信し、
            // バーストすると vanilla anti-cheat の DataFlag rate limit に触れて Hacking kick される (2026-05-28 実機確認)。
            // sub-CNO 1 個ごとに SubSpawnInterval 秒、方向 1 個ごとに DirectionSpawnInterval 秒の間隔を取る。
            float waveDelay = DirectionSpawnInterval * i;

            LateTask.New(() =>
            {
                if (!AmongUsClient.Instance.AmHost) return;
                if (GameStates.IsEnded || GameStates.IsMeeting) return;
                if (RiptidePC == null || !RiptidePC.IsAlive() || Dead) return;

                Vector2 startPos = GetWaveStartPosition(capturedDir);
                Vector2 direction = WaveDirections[capturedDir];
                Vector2 perp = Mathf.Abs(direction.x) > 0.5f ? Vector2.up : Vector2.right;

                // wave を先に ActiveWaves に追加してから sub-CNO を spawn する。
                // sub-CNO の LateTask が wave.Position を current 値で参照できるよう
                // この時点で wave を登録しておく。
                var wave = new RiptideWaveState(startPos, direction, capturedSpeed, capturedDir);
                ActiveWaves.Add(wave);

                // 2 個の sub-CNO を SubSpawnInterval 間隔でずらして spawn (packet 分散)。
                // CombineSendTimeLowering は send timer を巻き戻して即時送信を促す副作用があり、
                // ここでは逆効果なので使わない (CreateNetObject は内部で DataFlagRateLimiter を経由する)。
                var capturedWave = wave;
                for (int j = 0; j < SubLayout.Length; j++)
                {
                    int capturedJ = j;
                    LateTask.New(() =>
                    {
                        if (!AmongUsClient.Instance.AmHost) return;
                        if (GameStates.IsEnded || GameStates.IsMeeting) return;
                        if (Dead || !ActiveWaves.Contains(capturedWave)) return;
                        Vector2 subPos = capturedWave.Position
                            + perp * SubLayout[capturedJ].x
                            + direction * SubLayout[capturedJ].y;
                        capturedWave.SubCNOs.Add(new RiptideWaveCNO(subPos, capturedDir));
                    }, SubSpawnInterval * j, $"Riptide.SubCNOSpawn.dir{capturedDir}.sub{j}");
                }

                if (ShowPredictiveGhost)
                {
                    // Ghost sub-CNO は本体 2 個 spawn が終わった後から SubSpawnInterval 間隔で続けて spawn
                    float ghostStart = SubSpawnInterval * SubLayout.Length;
                    for (int j = 0; j < SubLayout.Length; j++)
                    {
                        int capturedJ = j;
                        LateTask.New(() =>
                        {
                            if (!AmongUsClient.Instance.AmHost) return;
                            if (GameStates.IsEnded || GameStates.IsMeeting) return;
                            if (Dead || !ActiveWaves.Contains(capturedWave)) return;
                            Vector2 ghostSubPos = capturedWave.Position
                                + perp * SubLayout[capturedJ].x
                                + direction * (SubLayout[capturedJ].y - 10f);
                            capturedWave.GhostSubCNOs.Add(new RiptidePredictiveGhostCNO(ghostSubPos, capturedDir));
                        }, ghostStart + SubSpawnInterval * j, $"Riptide.GhostSubCNOSpawn.dir{capturedDir}.sub{j}");
                    }
                }
            }, waveDelay, $"Riptide.WaveSpawn.dir{dirIdx}");
        }
    }

    // ============================================================
    // OnFixedUpdate — 波の移動・判定・cleanup
    // ============================================================
    public override void OnFixedUpdate(PlayerControl pc)
    {
        // host-only (罠対策 #4)
        if (!AmongUsClient.Instance.AmHost) return;
        if (!GameStates.IsInTask) return;

        // 役職者死亡 → 全 cleanup (罠対策 #7)
        if (!pc.IsAlive())
        {
            if (!Dead)
            {
                Dead = true;
                CleanupAllWaves();
            }
            return;
        }

        if (ActiveWaves.Count == 0) return;

        float dt = Time.fixedDeltaTime;  // 計時は float で (罠対策 #5)

        for (int i = ActiveWaves.Count - 1; i >= 0; i--)
        {
            var wave = ActiveWaves[i];

            // sub-CNO がある場合、null 混入チェック (spawn 完了後の null = Despawn 済み)
            // sub-CNO がまだ 0 個の場合はスタガー中なので cleanup しない (transient state)
            if (wave.SubCNOs.Count > 0 && wave.SubCNOs.Any(c => c == null))
            {
                RestoreSlowedPlayersForWave(wave);
                foreach (var sc in wave.SubCNOs) sc?.Despawn();
                foreach (var gc in wave.GhostSubCNOs) gc?.Despawn();
                ActiveWaves.RemoveAt(i);
                continue;
            }

            // 波を移動 — TP() で RpcSnapTo を発火させないと CNO は spawn 位置で静止する。
            // 単純な Position 代入は field 更新だけで sync しないため不可 (2026-05-25 root cause).
            wave.Position += wave.Direction * wave.Speed * dt;

            // TP throttle (2026-05-28 anti-cheat 対策):
            //   TP() は毎フレーム呼ぶと毎フレーム RpcSnapTo を発射する仕様
            //   (TP→SnapToSendFrameCount=30→次フレ即発射→0 clamp、を毎フレ反復)。
            //   6 sub × 50fps = 300 RPC/秒 で anti-cheat の rate limit を踏むため
            //   sync 頻度を TPSyncInterval (10Hz 程度) に絞る。
            //   非モッド側は前回 SnapTo 位置から次まで滑らかに補間されるので視覚断裂は最小。
            wave.TPSyncTimer += dt;
            if (wave.TPSyncTimer >= TPSyncInterval)
            {
                wave.TPSyncTimer = 0f;

                // 全 sub-CNO を 2D レイアウトに従って同期更新
                Vector2 perpDir = Mathf.Abs(wave.Direction.x) > 0.5f ? Vector2.up : Vector2.right;
                for (int j = 0; j < wave.SubCNOs.Count; j++)
                    wave.SubCNOs[j]?.TP(wave.Position
                        + perpDir * SubLayout[j].x
                        + wave.Direction * SubLayout[j].y);

                // Predictive ghost を波前方に追従
                for (int j = 0; j < wave.GhostSubCNOs.Count; j++)
                    wave.GhostSubCNOs[j]?.TP(wave.Position
                        + perpDir * SubLayout[j].x
                        + wave.Direction * (SubLayout[j].y - 10f));
            }

            // マップ外に出たら despawn (罠対策 #6)
            // parallel オフセット最大 +40f の後端 CNO が完全に画面外になるよう +50f バッファ
            bool outOfBounds = wave.Direction.x > 0 && wave.Position.x > MapMax.x + 50f
                            || wave.Direction.x < 0 && wave.Position.x < MapMin.x - 50f
                            || wave.Direction.y < 0 && wave.Position.y < MapMin.y - 50f
                            || wave.Direction.y > 0 && wave.Position.y > MapMax.y + 50f;

            if (outOfBounds)
            {
                RestoreSlowedPlayersForWave(wave);
                foreach (var sc in wave.SubCNOs) sc?.Despawn();
                foreach (var gc in wave.GhostSubCNOs) gc?.Despawn();
                ActiveWaves.RemoveAt(i);
                continue;
            }

            // プレイヤーとの判定
            ProcessWavePlayerInteraction(pc, wave, dt);
        }
    }

    private void ProcessWavePlayerInteraction(PlayerControl riptidePlayer, RiptideWaveState wave, float dt)
    {
        var changedPids = new HashSet<byte>();

        foreach (PlayerControl target in Main.EnumerateAlivePlayerControls())
        {
            // Riptide 本人は免疫
            if (target.PlayerId == riptidePlayer.PlayerId) continue;
            if (Pelican.IsEaten(target.PlayerId)) continue;
            if (target.Is(CustomRoles.Pestilence)) continue;

            bool inWave = IsPlayerInWave(target, wave);

            if (inWave)
            {
                // 速度低下 (罠対策 #8: SlowedPlayers HashSet で追跡)
                if (wave.SlowedPlayers.Add(target.PlayerId))
                {
                    // この波への初入場 — グローバル refcount をインクリメント
                    byte pid = target.PlayerId;
                    if (GlobalSlowState.TryGetValue(pid, out var state))
                    {
                        // 既に別の波でスロー中 → refcount だけ増やす、speed は触らない
                        GlobalSlowState[pid] = (state.OriginalSpeed, state.WaveRefCount + 1);
                    }
                    else
                    {
                        // 最初の波 → 素の速度を記録してスロー適用
                        if (Main.AllPlayerSpeed.ContainsKey(pid))
                        {
                            float orig = Main.AllPlayerSpeed[pid];
                            GlobalSlowState[pid] = (orig, 1);
                            Main.AllPlayerSpeed[pid] = orig * SlowMultiplier;
                            changedPids.Add(pid);
                        }
                    }
                }

                // 滞在タイマー加算 (罠対策 #5: float 累積)
                wave.ExposureTime.TryGetValue(target.PlayerId, out float elapsed);
                wave.ExposureTime[target.PlayerId] = elapsed + dt;

                // 死亡判定
                if (wave.ExposureTime[target.PlayerId] >= DeathStaySeconds)
                {
                    if (!wave.AlreadyKilled.Contains(target.PlayerId))
                    {
                        wave.AlreadyKilled.Add(target.PlayerId);
                        if (DecrementRefCountAndRestore(target.PlayerId))
                            changedPids.Add(target.PlayerId);
                        KillByRiptide(riptidePlayer, target, wave);
                    }
                }
            }
            else
            {
                // 波の外に出たら refcount を減らし 0 なら速度復元 (罠対策 #8)
                if (wave.SlowedPlayers.Remove(target.PlayerId))
                {
                    if (DecrementRefCountAndRestore(target.PlayerId))
                        changedPids.Add(target.PlayerId);
                }
                // 波の外に出たら露出タイマーリセット
                wave.ExposureTime.Remove(target.PlayerId);
            }
        }

        foreach (byte pid in changedPids)
            Utils.GetPlayerById(pid)?.MarkDirtySettings();
    }

    private static bool IsPlayerInWave(PlayerControl target, RiptideWaveState wave)
    {
        // TMP bottom-center anchor 仕様により、視覚スプライト中心は
        // sub-CNO transform.position から +Y 方向に VisualVerticalOffset シフトする。
        // wave.Position は sub-CNO 位置なので、ヒット判定は視覚中心を基準にすべき。
        // この補正により「波に当たって見えるのに死なない」問題を解消 (2026-05-27)。
        Vector2 visualCenter = wave.Position + new Vector2(0f, VisualVerticalOffset);
        Vector2 delta = target.GetTruePosition() - visualCenter;
        float along = Vector2.Dot(delta, wave.Direction);
        // 判定: 視覚中心 ±WaveBandThickness (motion 方向、前後対称)
        if (Mathf.Abs(along) > WaveBandThickness) return false;
        // perpendicular: 視覚中心からの距離が半マップ幅以内
        float lateralSq = (delta - wave.Direction * along).sqrMagnitude;
        float halfExtent = WaveLateralExtent / 2f;
        return lateralSq <= halfExtent * halfExtent;
    }

    // ============================================================
    // KillByRiptide — 波による確定キル
    //   注意: 他 Impostor 仲間も巻き込む (FFA 風)。
    //         CheckMurder 経路ではなく直接 RpcExileV2 で殺すため
    //         Madmate 等の通常ガード対象外。これは設計上の意図的仕様。
    // ============================================================
    private void KillByRiptide(PlayerControl riptidePlayer, PlayerControl target, RiptideWaveState killingWave)
    {
        byte pid = target.PlayerId;

        // target が killingWave 以外の波にも入っている場合、全波の SlowedPlayers から除去し
        // refcount を正しく減算する (複数波同時入場時の永続スロー防止)
        // killingWave 分は ProcessWavePlayerInteraction 内の DecrementRefCountAndRestore で既に処理済み
        bool speedRestored = false;
        foreach (var w in ActiveWaves)
        {
            if (w == killingWave) continue;
            if (w.SlowedPlayers.Remove(pid))
            {
                w.ExposureTime.Remove(pid);
                if (DecrementRefCountAndRestore(pid))
                    speedRestored = true;
            }
        }

        // GlobalSlowState に残余がある場合は強制クリア (refcount が不整合でも速度を必ず復元)
        if (GlobalSlowState.TryGetValue(pid, out var remaining))
        {
            Main.AllPlayerSpeed[pid] = remaining.OriginalSpeed;
            GlobalSlowState.Remove(pid);
            speedRestored = true;
        }

        if (speedRestored)
            target.MarkDirtySettings();

        // 確定キル (CheckMurder バイパス)
        target.RpcExileV2();
        RPC.PlaySoundRPC(riptidePlayer.PlayerId, Sounds.KillSound);

        PlayerState state = Main.PlayerStates[pid];
        state.deathReason = PlayerState.DeathReason.RiptideKilled;
        state.RealKiller = (DateTime.Now, riptidePlayer.PlayerId);
        state.SetDead();

        Utils.AfterPlayerDeathTasks(target);

        Logger.Info($"Riptide: {target.GetNameWithRole()} が波に呑まれて死亡 (dir={killingWave.DirectionIndex})", "Riptide.KillByRiptide");
    }

    // ============================================================
    // OnReportDeadBody — 会議入りで全波即 cleanup
    // ============================================================
    public override void OnReportDeadBody()
    {
        CleanupAllWaves();
    }

    // ============================================================
    // CleanupAllWaves — 全波 despawn + 速度全復元 (ただし CNO は Despawn 経由)
    //   全 cleanup 経路 (会議入り / 役職死亡 / 自然消滅) から呼ばれる
    // ============================================================
    private void CleanupAllWaves()
    {
        // 全波の SlowedPlayers を集計して GlobalSlowState から速度復元
        var toRestore = new HashSet<byte>();
        foreach (var wave in ActiveWaves)
        {
            foreach (byte pid in wave.SlowedPlayers)
                toRestore.Add(pid);
            foreach (var sc in wave.SubCNOs) sc?.Despawn();
            foreach (var gc in wave.GhostSubCNOs) gc?.Despawn();
        }
        ActiveWaves.Clear();

        // GlobalSlowState を参照して速度を復元 (refcount 問わず全員)
        foreach (byte pid in toRestore)
        {
            if (GlobalSlowState.TryGetValue(pid, out var s))
                Main.AllPlayerSpeed[pid] = s.OriginalSpeed;
        }
        GlobalSlowState.Clear();

        // 速度が変わった player だけ MarkDirtySettings
        foreach (byte pid in toRestore)
            Utils.GetPlayerById(pid)?.MarkDirtySettings();
    }

    private void RestoreSlowedPlayersForWave(RiptideWaveState wave)
    {
        var changedPids = new HashSet<byte>();
        foreach (byte pid in wave.SlowedPlayers.ToArray())
        {
            if (DecrementRefCountAndRestore(pid))
                changedPids.Add(pid);
        }
        wave.SlowedPlayers.Clear();

        foreach (byte pid in changedPids)
            Utils.GetPlayerById(pid)?.MarkDirtySettings();
    }

    // refcount を 1 減らし、0 になったら速度を復元して GlobalSlowState から削除。
    // 速度復元が発生した場合は true を返す (呼び出し元が MarkDirtySettings を集約)。
    private bool DecrementRefCountAndRestore(byte playerId)
    {
        if (!GlobalSlowState.TryGetValue(playerId, out var state)) return false;

        int newCount = state.WaveRefCount - 1;
        if (newCount <= 0)
        {
            Main.AllPlayerSpeed[playerId] = state.OriginalSpeed;
            GlobalSlowState.Remove(playerId);
            return true;
        }

        GlobalSlowState[playerId] = (state.OriginalSpeed, newCount);
        return false;
    }

    // ============================================================
    // GetSuffix — 波の方向・個数を表示
    // ============================================================
    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud = false, bool meeting = false)
    {
        if (meeting) return string.Empty;
        if (seer.PlayerId != target.PlayerId) return string.Empty;
        if (!seer.Is(CustomRoles.Riptide)) return string.Empty;
        if (ActiveWaves.Count == 0) return string.Empty;

        return $"<size=70%><color=#0073ff>~</color> {ActiveWaves.Count}</size>";
    }

    // ============================================================
    // GetWaveStartPosition — 方向別の初期位置 (マップ外)
    // ============================================================
    private static Vector2 GetWaveStartPosition(int directionIndex)
    {
        if (!MapBoundsValid)
            return Vector2.zero;

        // parallel オフセット +40f の前端 CNO がマップ外になるよう -60f マージン
        return directionIndex switch
        {
            0 => new Vector2(MapMin.x - 60f, MapCenter.y),  // 左→右: 左端外
            1 => new Vector2(MapMax.x + 60f, MapCenter.y),  // 右→左: 右端外
            2 => new Vector2(MapCenter.x, MapMax.y + 60f),  // 上→下: 上端外
            3 => new Vector2(MapCenter.x, MapMin.y - 60f),  // 下→上: 下端外
            _ => MapCenter
        };
    }

    // spawn 間隔 (anti-cheat バースト防止、2026-05-28 実機 Hacking kick の root cause):
    //   1 CNO spawn = Spawn/Hide-loop/Shapeshift-trick の 3 段 reliable packet。
    //   2 個 × 4 方向を 0.05f 間隔で連射すると vanilla anti-cheat の rate limit を踏み抜く。
    //   個別 spawn coroutine が yield する 0.55s より長めに取り、bursts を回避する。
    private const float SubSpawnInterval = 0.6f;
    private const float DirectionSpawnInterval = 4.5f;  // 1 方向 = 2 sub × 0.6s = 1.2s + 余裕

    // TP() を呼ぶ頻度 (anti-cheat の RPC rate limit 対策、2026-05-28 実機 Hacking kick で確定):
    //   CustomNetObject.TP() は SnapToSendFrameCount=30 を立てて次フレで RpcSnapTo 発射する。
    //   毎フレ TP() = 毎フレ RpcSnapTo (SendOption.None でも anti-cheat の rate にカウントされる)。
    //   10Hz→5Hz に半減。SnapTo(None) の本数を減らし公式 anti-cheat のレート負荷を下げる。
    //   非モッド側は前回 SnapTo から補間されるので視覚断裂は最小。
    private const float TPSyncInterval = 0.18f; // 2026-06-30: 0.2f→0.18f に 1 フレーム(50fps)ぶん短縮し波を少し滑らかに。送信増は約11%で anti-cheat 閾値の遥か下

    // sub-CNO 2D レイアウト (2026-06-04 改: 公式 anti-cheat 対策で 6→2 に削減):
    //   各エントリ = (perp オフセット, parallel オフセット)
    //   size=40 の sub-CNO 2枚を perp 方向 (波の進行に垂直 = マップ断面) に ±10u で並べ、
    //   重ね合わせて隙間のない連続した波として見せる。parallel 方向の展開は廃止 (motion 方向は薄い壁でよい)。
    //   左右波なら縦2枚 / 上下波なら横2枚 に perp ベクトルで自動配置される。
    private static readonly Vector2[] SubLayout =
    {
        new(-10f, 0f),
        new( 10f, 0f),
    };

    // ============================================================
    // RiptideWaveState — per-wave 状態管理
    // ============================================================
    private sealed class RiptideWaveState
    {
        public readonly List<RiptideWaveCNO> SubCNOs = new(1);
        public readonly List<RiptidePredictiveGhostCNO> GhostSubCNOs = new(1);
        public Vector2 Position;
        public readonly Vector2 Direction;
        public readonly float Speed;   // 生成時に capture (罠対策 #11)
        public readonly int DirectionIndex;

        // 速度低下追跡 (罠対策 #8) — OriginalSpeeds は GlobalSlowState に統合済み
        public readonly HashSet<byte> SlowedPlayers = [];

        // 露出タイマー (罠対策 #5: float 累積)
        public readonly Dictionary<byte, float> ExposureTime = [];

        // 確定キル済みセット (重複キル防止)
        public readonly HashSet<byte> AlreadyKilled = [];

        // TP() throttle 用 (anti-cheat RPC rate 対策、2026-05-28)
        public float TPSyncTimer;

        public RiptideWaveState(Vector2 startPos, Vector2 dir, float speed, int dirIdx)
        {
            Position = startPos;
            Direction = dir;
            Speed = speed;
            DirectionIndex = dirIdx;
        }
    }
}
