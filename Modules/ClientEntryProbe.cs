using System;
using System.Collections;
using System.Collections.Generic;
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
// ⚠️ 未実証の前提: 「intro 中は NetTransform の seq が進まない」は実機ログで未裏取り。そのため
// per-player の確認時刻を必ずログに出す — もし全員 +0.0s で即confirm するならこの信号は無効
// (その場合ゲートは最短待ち (従来+1s) に縮退するだけで、現状より悪化はしない)。
//
// Rollback bit: create EndKnot_DATA/disable_entry_gate.txt (発火毎に素読み・再ビルド不要) →
// 従来どおり intro 終了 +1 秒の固定 LateTask に戻る。
public static class ClientEntryProbe
{
    private const float MinWaitSeconds = 1f; // 従来アンカー (TOHK 互換 +1s) を下回らない
    private const float HardCapSeconds = 8f; // AFK/立ち止まり/切断で永久に待たないための保険

    /// <summary>FirstTurnMeeting の発火をエントリゲートに乗せる。kill switch 時は従来 +1s 固定。</summary>
    public static void StartGate(Action fire)
    {
        if (DisableEntryGate())
        {
            Logger.Warn("disable_entry_gate.txt present: falling back to fixed intro+1s trigger", "ClientEntryProbe");
            LateTask.New(fire, MinWaitSeconds, "FirstTurnMeetingTrigger");
            return;
        }

        Main.Instance.StartCoroutine(CoWaitAndFire(fire));
    }

    private static IEnumerator CoWaitAndFire(Action fire)
    {
        float start = Time.realtimeSinceStartup;

        // ベースライン: ホスト intro 終了時点の各リモートクライアントの seq をスナップショット。
        // ホスト intro は最短 (GM 短縮) なので、この時点で客はまだ intro 中のはず。
        var baseline = new Dictionary<byte, ushort>();
        var pending = new HashSet<byte>();

        foreach (PlayerControl pc in Main.AllAlivePlayerControls)
        {
            if (pc.IsHost() || pc.PlayerId >= 200 || pc.AmOwner) continue;
            if (!pc.NetTransform) continue;

            baseline[pc.PlayerId] = pc.NetTransform.lastSequenceId;
            pending.Add(pc.PlayerId);
        }

        Logger.Info($"entry gate armed: waiting for {pending.Count} remote clients (min {MinWaitSeconds}s, cap {HardCapSeconds}s)", "ClientEntryProbe");

        var confirmLog = new List<string>();

        while (Time.realtimeSinceStartup - start < HardCapSeconds)
        {
            // ゲームが死んだら発火せず撤収 (fire 側にもガードはあるが、待ち続ける意味がない)
            if (!GameStates.IsInGame || AmongUsClient.Instance.IsGameOver || GameStates.IsLobby) yield break;

            float elapsed = Time.realtimeSinceStartup - start;

            foreach (PlayerControl pc in Main.AllAlivePlayerControls)
            {
                if (!pending.Contains(pc.PlayerId)) continue;

                // seq が進んだ = そのクライアントから移動パケットが届いた = intro 明けの証拠
                if (pc.NetTransform && pc.NetTransform.lastSequenceId != baseline[pc.PlayerId])
                {
                    pending.Remove(pc.PlayerId);
                    confirmLog.Add($"{pc.GetRealName()}(id {pc.PlayerId}, {pc.GetClient()?.PlatformData?.Platform}) +{elapsed:F2}s");
                }
            }

            // 切断/退出済みプレイヤーを pending から除去 (残すとハードキャップまで空待ちする)
            if (pending.Count > 0)
            {
                pending.RemoveWhere(id =>
                {
                    PlayerControl pc = Utils.GetPlayerById(id, fast: false);
                    return !pc || !pc.IsAlive() || pc.Data == null || pc.Data.Disconnected;
                });
            }

            if (pending.Count == 0 && elapsed >= MinWaitSeconds) break;

            yield return null;
        }

        float total = Time.realtimeSinceStartup - start;
        string confirms = confirmLog.Count > 0 ? string.Join(", ", confirmLog) : "(none)";

        if (pending.Count == 0)
            Logger.Info($"entry gate: all clients confirmed, firing at +{total:F2}s — confirms: {confirms}", "ClientEntryProbe");
        else
            Logger.Warn($"entry gate: hard cap {HardCapSeconds}s reached with {pending.Count} unconfirmed, firing anyway — confirms: {confirms}", "ClientEntryProbe");

        fire();
    }

    private static bool DisableEntryGate()
    {
        try { return System.IO.File.Exists($"{Main.DataPath}/EndKnot_DATA/disable_entry_gate.txt"); }
        catch { return false; }
    }
}
