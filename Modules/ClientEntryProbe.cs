using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace EndKnot.Modules;

// クライアント入場完了確認方式 (2026-07-28・FirstTurnMeeting の暗転レース根治候補)。
//
// 機序: FirstTurnMeetingTrigger はホストの intro 終了 +1 秒の固定アンカーで発火するが、GM ホストの
// intro は短縮 (~7-9s)、遅いクライアント (Switch 等) は 10-11s かかるため、客の intro 中に会議が
// 着弾して暗転する既知レースがある (memory: gm_host_short_intro_rpc_lands_mid_client_intro)。
// 固定秒数の再調整は3連敗済みで禁止 — ここでは「観測」で置き換える。
//
// シグナル: 各クライアントの CustomNetworkTransform.lastSequenceId。バニラは intro 中プレイヤーを
// 動かせないため、この値が進み始めた瞬間 ≒ そのクライアントの intro が明けて操作可能になった瞬間。
// ホストは受信するだけで観測できる (送信ゼロ・非モッド客でも可・ホストローカル)。
//
// ⚠️ 信号の限界 (2026-07-28 実機確認): seq 前進は「移動入力が届いた」証明であって「intro 正常完了」
// の証明ではない — Switch は物理スティックで黒画面のまま盲目移動して confirm しうる。ゲートを
// 暗転検知器として読んではならない。一方 2026-08-04 の再発ゲームでは hard cap 到達時の未confirm
// 2名が暗転本人発症者と完全一致した (docs/blackout-resume.md 08-04) — 未confirm は発症予備軍として
// 扱い、cap 到達時は会議明けに FixBlackScreen 救済を撃つ (RunRescue)。
//
// 偽 confirm 対策 (2026-08-04): Utils.TP の nt.SnapTo はホストローカルの lastSequenceId を +328
// 直接書きするため、gate 武装後のホスト起因 TP (Submerged 補正・役職 TP) は客が動いていなくても
// seq が動いて偽 confirm になる (memory: tp_delivery_probe_pos_fallback)。Utils.TP から
// NoteHostSnapTo を受けて baseline を現値へ付け替えることで、クライアント自身の移動だけを confirm
// として数える。
//
// Rollback bit: EndKnot_DATA/disable_entry_gate.txt → 従来どおり intro 終了 +1 秒の固定 LateTask。
//               EndKnot_DATA/disable_entry_rescue.txt → ゲートは残したまま救済発射だけを無効化。
public static class ClientEntryProbe
{
    private const float MinWaitSeconds = 1f; // 従来アンカー (TOHK 互換 +1s) を下回らない

    // 8s では 2026-08-04 の未confirm 2名を待ち切れなかったため延長。15f にしないのは
    // IntroPatch の T2ClusterWatchdog (+15s) と同時着火のレースを避けるため (2秒マージン)。
    private const float HardCapSeconds = 13f;

    // gate 稼働中のみ有効な監視状態 (ホストローカル・主スレッドのみで触る)
    private static readonly Dictionary<byte, ushort> Baseline = [];
    private static readonly HashSet<byte> Pending = [];
    private static bool GateActive;

    // hard cap 到達時に未confirm だったプレイヤー = 発症予備軍。初手会議明けの救済対象。
    private static readonly List<byte> RescueTargets = [];

    /// <summary>
    /// 全状態を破棄する。StartGate 内だけでなく OnGameStartedPatch (全ゲームモード共通) からも
    /// 毎ゲーム必ず呼ぶこと — StartGate は Standard+FTM のゲームでしか走らないため、そこだけに
    /// リセットを置くと「FTM ゲームで積んだ RescueTargets が会議ゼロのまま終了 → 次の非FTMゲームの
    /// 会議明けに前ゲームの PlayerId へ誤射」する跨ゲーム汚染が起こる (2026-08-04 pitfall 監査)。
    /// </summary>
    public static void Reset()
    {
        Baseline.Clear();
        Pending.Clear();
        RescueTargets.Clear();
        GateActive = false;
    }

    /// <summary>FirstTurnMeeting の発火をエントリゲートに乗せる。kill switch 時は従来 +1s 固定。</summary>
    public static void StartGate(Action fire)
    {
        Reset();

        if (DisableEntryGate())
        {
            Logger.Warn("disable_entry_gate.txt present: falling back to fixed intro+1s trigger", "ClientEntryProbe");
            LateTask.New(fire, MinWaitSeconds, "FirstTurnMeetingTrigger");
            return;
        }

        Main.Instance.StartCoroutine(CoWaitAndFire(fire));
    }

    /// <summary>
    /// ホスト起因 SnapTo (Utils.TP) が lastSequenceId を直接書いた直後に呼ぶ。
    /// gate 監視中の相手なら confirm 扱いにせず baseline を現値へ付け替える。
    /// </summary>
    public static void NoteHostSnapTo(PlayerControl pc)
    {
        if (!GateActive || !pc || !pc.NetTransform || !Pending.Contains(pc.PlayerId)) return;

        Baseline[pc.PlayerId] = pc.NetTransform.lastSequenceId;
        Logger.Info($"entry gate: baseline rebased for {pc.GetRealName()} (host SnapTo wrote seq directly)", "ClientEntryProbe");
    }

    private static IEnumerator CoWaitAndFire(Action fire)
    {
        float start = Time.realtimeSinceStartup;

        // ベースライン: ホスト intro 終了時点の各リモートクライアントの seq をスナップショット。
        // ホスト intro は最短 (GM 短縮) なので、この時点で客はまだ intro 中のはず。
        foreach (PlayerControl pc in Main.AllAlivePlayerControls)
        {
            if (pc.IsHost() || pc.PlayerId >= 200 || pc.AmOwner) continue;
            if (!pc.NetTransform) continue;

            Baseline[pc.PlayerId] = pc.NetTransform.lastSequenceId;
            Pending.Add(pc.PlayerId);
        }

        GateActive = true;
        Logger.Info($"entry gate armed: waiting for {Pending.Count} remote clients (min {MinWaitSeconds}s, cap {HardCapSeconds}s)", "ClientEntryProbe");

        var confirmLog = new List<string>();

        while (Time.realtimeSinceStartup - start < HardCapSeconds)
        {
            // ゲームが死んだら発火せず撤収 (fire 側にもガードはあるが、待ち続ける意味がない)
            if (!GameStates.IsInGame || AmongUsClient.Instance.IsGameOver || GameStates.IsLobby)
            {
                GateActive = false;
                yield break;
            }

            float elapsed = Time.realtimeSinceStartup - start;

            foreach (PlayerControl pc in Main.AllAlivePlayerControls)
            {
                if (!Pending.Contains(pc.PlayerId)) continue;

                // seq が進んだ = そのクライアントから移動パケットが届いた = intro 明けの証拠
                if (pc.NetTransform && pc.NetTransform.lastSequenceId != Baseline[pc.PlayerId])
                {
                    Pending.Remove(pc.PlayerId);
                    confirmLog.Add($"{pc.GetRealName()}(id {pc.PlayerId}, {pc.GetClient()?.PlatformData?.Platform}) +{elapsed:F2}s");
                }
            }

            // 切断/退出済みプレイヤーを pending から除去 (残すとハードキャップまで空待ちする)
            if (Pending.Count > 0)
            {
                Pending.RemoveWhere(id =>
                {
                    PlayerControl pc = Utils.GetPlayerById(id, fast: false);
                    return !pc || !pc.IsAlive() || pc.Data == null || pc.Data.Disconnected;
                });
            }

            if (Pending.Count == 0 && elapsed >= MinWaitSeconds) break;

            yield return null;
        }

        GateActive = false;

        float total = Time.realtimeSinceStartup - start;
        string confirms = confirmLog.Count > 0 ? string.Join(", ", confirmLog) : "(none)";

        if (Pending.Count == 0)
            Logger.Info($"entry gate: all clients confirmed, firing at +{total:F2}s — confirms: {confirms}", "ClientEntryProbe");
        else
        {
            // 未confirm = 発症予備軍 (2026-08-04 実測: 未confirm 2名が暗転発症者と完全一致)。
            // ここで新規送信はしない (intro 構築中クライアントへのブロードキャストは自身が暗転容疑
            // — memory: gm_host_short_intro_rpc_lands_mid_client_intro の +8s ReactorFlash の教訓)。
            // 会議明け (Utils.AfterMeetingTasks) に FixBlackScreen 救済を撃つ。
            RescueTargets.AddRange(Pending);
            string names = string.Join(", ", Pending.Select(id => Utils.GetPlayerById(id, fast: false)?.GetRealName() ?? $"id {id}"));
            Logger.Warn($"entry gate: hard cap {HardCapSeconds}s reached with {Pending.Count} unconfirmed, firing anyway — unconfirmed: {names}, confirms: {confirms}", "ClientEntryProbe");
            Logger.Warn($"entry gate rescue: FixBlackScreen queued for after-meeting — targets: {names}", "ClientEntryProbe");
        }

        Pending.Clear();
        fire();
    }

    // 会議明けの同フレーム逐次発射数の上限 (identity系 nests 合算対策・2026-08-04 anticheat 監査)。
    // これを超える人数が未confirm になるロビーは回線全体が崩壊しており、救済より観察が正しい。
    private const int MaxRescueTargets = 4;

    /// <summary>初手会議明け (Utils.AfterMeetingTasks) に呼ばれ、未confirm だった客へ修復を撃つ。</summary>
    public static void RunRescue()
    {
        if (RescueTargets.Count == 0) return;

        List<byte> targets = [.. RescueTargets];
        RescueTargets.Clear(); // 1ゲーム1回・再入防止のため先に空にする

        if (DisableEntryRescue())
        {
            Logger.Warn("disable_entry_rescue.txt present: skipping entry gate rescue", "ClientEntryProbe");
            return;
        }

        if (targets.Count > MaxRescueTargets)
        {
            Logger.Warn($"entry gate rescue: {targets.Count} targets exceed cap {MaxRescueTargets}, rescuing first {MaxRescueTargets} only", "ClientEntryProbe");
            targets.RemoveRange(MaxRescueTargets, targets.Count - MaxRescueTargets);
        }

        // FixBlackScreen の重量パス (reactor desync flash) は死者が1人もいないと内部の待機コルーチンが
        // 「最初のキル」まで発射を保留する (ExtendedPlayerControl の dummyGhost ガード)。FTM 明けは
        // 死者ゼロが普通に起こる (NoVote 構成なら常に) ため、その場合はキル直後の RPC 群への近接着弾を
        // 避けて重量パスを撃たない (2026-08-04 両監査の 🔴)。
        bool anyDead = Main.EnumeratePlayerControls().FindFirst(x => !x.IsAlive(), out _);

        foreach (byte id in targets)
        {
            PlayerControl pc = Utils.GetPlayerById(id, fast: false);
            if (!pc || pc.Data == null || pc.Data.Disconnected) continue;

            if (pc.IsModdedClient())
            {
                Logger.Info($"entry gate rescue: {pc.GetRealName()} is modded, skipping (mod-side recovery exists)", "ClientEntryProbe");
                continue;
            }

            // 軽量修復: intro を見逃した客への役職 desync 単発再送。FixBlackScreen の FirstMeeting 分岐と
            // 同型だが、RunRescue 到達時点では MeetingStates.FirstMeeting が既に false のためここで直接撃つ
            // (会議明け +3.4s の静かな窓・targeted 1発なので合算安全)。
            Logger.Info($"entry gate rescue: resending role desync for {pc.GetNameWithRole()} (heavyRepair={anyDead})", "ClientEntryProbe");
            pc.RpcSetRoleDesync(pc.GetRoleTypes(), pc.OwnerId);

            // 重量修復 (reactor desync flash): 死亡していても撃つ (2026-08-04 のクッキーは暗転のまま
            // 殺されて幽霊 UI も壊れていた)。会議/追放中・本物サボ稼働中の待機は関数内の既存経路が担う。
            if (anyDead) pc.FixBlackScreen();
        }
    }

    private static bool DisableEntryGate()
    {
        try { return System.IO.File.Exists($"{Main.DataPath}/EndKnot_DATA/disable_entry_gate.txt"); }
        catch { return false; }
    }

    private static bool DisableEntryRescue()
    {
        try { return System.IO.File.Exists($"{Main.DataPath}/EndKnot_DATA/disable_entry_rescue.txt"); }
        catch { return false; }
    }
}
