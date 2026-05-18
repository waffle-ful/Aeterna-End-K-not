using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using Hazel;
using InnerNet;
using UnityEngine;

namespace EndKnot.Modules;

// ロビーに vanilla DeadBody を複数配置する装飾機能 (mod/非モッド両方に見える)。
//
// 仕組み: Utils.RpcCreateDeadBody (LobbyKill/Overkiller と同じパス) を host を parent に複数回呼ぶ。
//   - 一過性 PlayerControl の spawn+despawn を 1 batched message で済ませるので
//     relay anti-cheat には残らず非モッド入室で kick されない
//   - 各 client は MurderPlayer RPC を受けて vanilla 経由でローカル DeadBody を描画
//   - 結果として PlayerControl-based CNO と違い「永続 ghost player」が存在せず安全
//
// 副作用対策: transient PC が host と同じ PlayerId を持つため、Data.Serialize で
// 空 PlayerName が PlayerId 0 に被さって host の名前が空 → vanilla default「プレイヤー」表示
// になる。spawn 前に PlayerName/outfit を退避 → spawn 後に保存名で明示復元する
internal static class LobbyCorpses
{
    private static Vector2? BasePos;
    private static readonly List<Vector2> CurrentPositions = [];

    // 多重 join coalesce 用 state
    private static bool SpawnInProgress;
    private static bool ReplayPending;
    private static float NextReplayTime;

    // 二重発火抑止: InitialSpawn が join 後に発火していれば該当 client は
    // すでに body を受け取っているので Replay は不要
    private static float LastJoinTime;
    private static float LastSpawnTime;

    public static void Reset()
    {
        BasePos = null;
        CurrentPositions.Clear();
        SpawnInProgress = false;
        ReplayPending = false;
        NextReplayTime = 0f;
        LastJoinTime = 0f;
        LastSpawnTime = 0f;
    }

    // 初期 spawn (LobbyBehaviour.Start から)
    public static void RequestInitialSpawn()
    {
        if (SpawnInProgress) return;
        StartSpawn();
    }

    // 入室時 replay (OnPlayerJoined から)。
    // 3 秒スライディング debounce — 連続入室は最後の join から 3 秒経った時点で 1 回だけ実行
    public static void RequestReplay()
    {
        LastJoinTime = Time.time;
        NextReplayTime = Time.time + 3f;
        if (ReplayPending) return;
        ReplayPending = true;
        Main.Instance.StartCoroutine(DebouncedReplay());
    }

    private static IEnumerator DebouncedReplay()
    {
        // 連続 join が来てる間 NextReplayTime が push back され続ける → 待ち
        while (Time.time < NextReplayTime) yield return null;

        // 直前の spawn 走行中なら完了待ち (初期 spawn と replay の競合防止)
        while (SpawnInProgress) yield return null;

        ReplayPending = false;
        if (!GameStates.IsLobby) yield break;

        // 直近の join より新しい spawn がすでに走っていれば、該当 client は
        // その broadcast を受信済み → replay は無駄なのでスキップ
        if (LastSpawnTime > LastJoinTime)
        {
            Logger.Info($"LobbyCorpses: replay skipped (LastSpawnTime={LastSpawnTime:F2} > LastJoinTime={LastJoinTime:F2})", "LobbyCorpses");
            yield break;
        }

        StartSpawn();
    }

    private static void StartSpawn()
    {
        if (SpawnInProgress) return;
        if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
        if (!Options.LobbyCorpseEnabled.GetBool()) return;
        if (PlayerControl.LocalPlayer == null || PlayerControl.LocalPlayer.Data == null) return;

        // 初回呼出時に位置を確定。replay 時は同じ位置で再 broadcast → 既存 client では
        // 同位置 stack で視覚的に等価 (no per-client targeting in RpcCreateDeadBody)
        if (CurrentPositions.Count == 0)
        {
            BasePos ??= PlayerControl.LocalPlayer.GetTruePosition();
            int count = Options.LobbyCorpseCount.GetInt();
            float radius = Options.LobbyCorpseSpreadRadius.GetFloat();

            IRandom rd = IRandom.Instance;
            for (int i = 0; i < count; i++)
            {
                float dx = (rd.Next(0, 201) - 100) / 100f * radius;
                float dy = (rd.Next(0, 201) - 100) / 100f * radius;
                CurrentPositions.Add(BasePos.Value + new Vector2(dx, dy));
            }
        }

        SpawnInProgress = true;
        Main.Instance.StartCoroutine(SpawnCoroutine());
    }

    private static IEnumerator SpawnCoroutine()
    {
        PlayerControl lp = PlayerControl.LocalPlayer;
        // host の真の名前を退避 (transient PC の Data.Serialize で巻き戻る)
        string savedName = lp.Data.PlayerName;
        byte colorId = (byte)lp.Data.DefaultOutfit.ColorId;
        int spawned = 0;

        // Overkiller パターン: 4 個ごとに 1 フレーム待って RPC バースト緩和
        for (int i = 0; i < CurrentPositions.Count; i++)
        {
            Utils.RpcCreateDeadBody(
                CurrentPositions[i],
                colorId,
                lp,
                SendOption.Reliable);
            spawned++;
            if (i % 4 == 3) yield return null;
        }

        // 全 spawn 完了後にホスト名前を明示復元 (匿名化対策)
        yield return new WaitForSecondsRealtime(0.5f);

        try
        {
            if (lp != null && lp.Data != null)
            {
                // PlayerName 自体を保存値で上書き (Serialize で空に巻き戻った状態を復元)
                lp.Data.PlayerName = savedName;
                lp.Data.DefaultOutfit.PlayerName = savedName;
                lp.Data.IsDead = false;
                lp.Data.SetDirtyBit(uint.MaxValue);
                AmongUsClient.Instance.SendAllStreamedObjects();
                // host 自身の HUD と他クライアントの label を即時更新
                lp.RpcSetName(savedName);
                Logger.Info($"LobbyCorpses: spawned {spawned} corpses, host name restored to '{savedName}'", "LobbyCorpses");
            }
        }
        catch (Exception ex) { Logger.Warn($"Host re-sync failed: {ex.Message}", "LobbyCorpses"); }

        LastSpawnTime = Time.time;
        SpawnInProgress = false;
    }
}

// 新規ロビー入室時に位置をリセット + 3 秒後に初期 spawn
[HarmonyPatch(typeof(LobbyBehaviour), nameof(LobbyBehaviour.Start))]
internal static class LobbyCorpsesStartHook
{
    public static void Postfix()
    {
        if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;

        LobbyCorpses.Reset();

        LateTask.New(() =>
        {
            if (!GameStates.IsLobby || GameStates.InGame) return;
            LobbyCorpses.RequestInitialSpawn();
        }, 3f, "LobbyCorpses.InitialSpawn");
    }
}

// プレイヤー入室時に replay 要求。多重 join は 3 秒スライディング debounce で
// 1 回の spawn に coalesce される (anti-cheat バースト対策)
[HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnPlayerJoined))]
internal static class LobbyCorpsesJoinHook
{
    public static void Postfix([HarmonyArgument(0)] ClientData client)
    {
        if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
        if (client == null) return;
        if (!GameStates.IsLobby) return;

        LobbyCorpses.RequestReplay();
    }
}
