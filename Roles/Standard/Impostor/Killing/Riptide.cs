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
            .AutoSetupOption(ref BaseWaveSpeedOpt, 1.0f, new FloatValueRule(0.5f, 10f, 0.25f), OptionFormat.Multiplier)
            .AutoSetupOption(ref EnableAccelerationOpt, true)
            .AutoSetupOption(ref AccelerationRateOpt, 0.15f, new FloatValueRule(0f, 1f, 0.05f), OptionFormat.Multiplier, overrideParent: EnableAccelerationOpt)
            .AutoSetupOption(ref SlowMultiplierOpt, 0.5f, new FloatValueRule(0.1f, 1f, 0.05f), OptionFormat.Multiplier)
            .AutoSetupOption(ref DeathStaySecondsOpt, 3f, new FloatValueRule(0.5f, 15f, 0.5f), OptionFormat.Seconds)
            .AutoSetupOption(ref MaxConcurrentDirectionsOpt, 3, new IntegerValueRule(1, 8, 1), OptionFormat.Times)
            .AutoSetupOption(ref CanKillManuallyOpt, true)
            .AutoSetupOption(ref KillCooldownOpt, 30, new IntegerValueRule(0, 120, 1), OptionFormat.Seconds, overrideParent: CanKillManuallyOpt)
            .AutoSetupOption(ref WaveBandThicknessOpt, 4f, new FloatValueRule(0.5f, 15f, 0.5f), OptionFormat.Multiplier)
            .AutoSetupOption(ref WaveLateralExtentOpt, 40f, new FloatValueRule(10f, 80f, 5f), OptionFormat.Multiplier)
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
                // spawn 点からマップ端を推定 (+20 unit のマージン)
                // CNO が TP() で動くようになった後 (2026-05-25 root cause fix) は、spawn を
                // マップ外側に十分遠くしても波がプレイヤーに届く。マージン +20 + GetWaveStartPosition
                // の -5 で spawn はマップ実端から 25 unit 外側 → 画面外から波が登場する演出。
                MapMin = new Vector2(minX - 50f, minY - 50f);
                MapMax = new Vector2(maxX + 50f, maxY + 50f);
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
            float waveDelay = 0.2f * i;

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

                // 4 個の sub-CNO を 0.05f * j 間隔でずらして spawn (packet 分散)
                var capturedWave = wave;
                for (int j = 0; j < SubOffsets.Length; j++)
                {
                    int capturedJ = j;
                    LateTask.New(() =>
                    {
                        if (!AmongUsClient.Instance.AmHost) return;
                        if (GameStates.IsEnded || GameStates.IsMeeting) return;
                        if (Dead || !ActiveWaves.Contains(capturedWave)) return;
                        Vector2 subPos = capturedWave.Position + perp * SubOffsets[capturedJ];
                        Utils.CombineSendTimeLowering(() =>
                        {
                            capturedWave.SubCNOs.Add(new RiptideWaveCNO(subPos, capturedDir));
                        });
                    }, 0.05f * j, $"Riptide.SubCNOSpawn.dir{capturedDir}.sub{j}");
                }

                if (ShowPredictiveGhost)
                {
                    // Ghost sub-CNO も同様に 4 個 × 0.05f 間隔で spawn
                    for (int j = 0; j < SubOffsets.Length; j++)
                    {
                        int capturedJ = j;
                        LateTask.New(() =>
                        {
                            if (!AmongUsClient.Instance.AmHost) return;
                            if (GameStates.IsEnded || GameStates.IsMeeting) return;
                            if (Dead || !ActiveWaves.Contains(capturedWave)) return;
                            Vector2 ghostSubPos = capturedWave.Position - direction * 10f + perp * SubOffsets[capturedJ];
                            Utils.CombineSendTimeLowering(() =>
                            {
                                capturedWave.GhostSubCNOs.Add(new RiptidePredictiveGhostCNO(ghostSubPos, capturedDir));
                            });
                        }, 0.2f + 0.05f * j, $"Riptide.GhostSubCNOSpawn.dir{capturedDir}.sub{j}");
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

            // 全 sub-CNO を wave.Position + 垂直オフセットで同期更新
            Vector2 perpDir = Mathf.Abs(wave.Direction.x) > 0.5f ? Vector2.up : Vector2.right;
            for (int j = 0; j < wave.SubCNOs.Count; j++)
                wave.SubCNOs[j]?.TP(wave.Position + perpDir * SubOffsets[j]);

            // Predictive ghost を波前方に追従
            for (int j = 0; j < wave.GhostSubCNOs.Count; j++)
                wave.GhostSubCNOs[j]?.TP(wave.Position - wave.Direction * 10f + perpDir * SubOffsets[j]);

            // マップ外に出たら despawn (罠対策 #6)
            bool outOfBounds = wave.Direction.x > 0 && wave.Position.x > MapMax.x + 10f
                            || wave.Direction.x < 0 && wave.Position.x < MapMin.x - 10f
                            || wave.Direction.y < 0 && wave.Position.y < MapMin.y - 10f
                            || wave.Direction.y > 0 && wave.Position.y > MapMax.y + 10f;

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
        Vector2 delta = target.GetTruePosition() - wave.Position;
        float along = Vector2.Dot(delta, wave.Direction);
        // 判定: 波の中心 ±WaveBandThickness (前後対称)。
        // 旧 along in [-WaveBandThickness, 0] (後方のみ) は TMP sprite の anchor が
        // 左下基準で、wave.Position と sprite 視覚中心がズレるため判定外れの原因に。
        // ForceField の HitRadiusScale パターン同様、視覚と判定を実機合わせで対称化。
        if (Mathf.Abs(along) > WaveBandThickness) return false;
        // 横方向
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

        return directionIndex switch
        {
            0 => new Vector2(MapMin.x - 5f, MapCenter.y),   // 左→右: 左端外
            1 => new Vector2(MapMax.x + 5f, MapCenter.y),   // 右→左: 右端外
            2 => new Vector2(MapCenter.x, MapMax.y + 5f),   // 上→下: 上端外
            3 => new Vector2(MapCenter.x, MapMin.y - 5f),   // 下→上: 下端外
            _ => MapCenter
        };
    }

    // sub-CNO の垂直オフセット (中心から ±7.5 unit、間隔 5)
    private static readonly float[] SubOffsets = { 7.5f, 2.5f, -2.5f, -7.5f };

    // ============================================================
    // RiptideWaveState — per-wave 状態管理
    // ============================================================
    private sealed class RiptideWaveState
    {
        public readonly List<RiptideWaveCNO> SubCNOs = new(4);
        public readonly List<RiptidePredictiveGhostCNO> GhostSubCNOs = new(4);
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

        public RiptideWaveState(Vector2 startPos, Vector2 dir, float speed, int dirIdx)
        {
            Position = startPos;
            Direction = dir;
            Speed = speed;
            DirectionIndex = dirIdx;
        }
    }
}
