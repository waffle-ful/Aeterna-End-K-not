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

    // spawn 中に GameData slot 0 へ被さった空 PlayerName を後から戻すための退避値。
    // spawn 開始時にセットし、復元処理 + GameStart ガードで参照される。
    private static string SavedHostName;

    // リンク劣化ゲートの延期状態 (BUG-20260820-06 緩和)
    private static int DeferredSpawnAttempts;
    private static bool DeferralPending;
    private const int MaxDeferredSpawnAttempts = 12; // 5 秒間隔 × 12 = 最大 60 秒待って諦める

    // ゲーム開始コミットラッチ。BeginGame Prefix (EnsureHostNameRestored) で立ち、Reset (新ロビー) で降りる。
    // GameStates.IsLobby は「ネットワーク層が Joined を抜けた瞬間」にしかフリップしないため、
    // 開始コミット〜遷移完了の隙間に劣化ゲートの延期リトライが滑り込むと、開始直後スポーン窓 (P6 近傍)
    // で corpse burst を新規開始してしまう。そのレースをこのラッチで封じる。
    private static bool GameStartCommitted;

    public static void Reset()
    {
        BasePos = null;
        CurrentPositions.Clear();
        SpawnInProgress = false;
        ReplayPending = false;
        NextReplayTime = 0f;
        LastJoinTime = 0f;
        LastSpawnTime = 0f;
        SavedHostName = null;
        DeferredSpawnAttempts = 0;
        DeferralPending = false;
        GameStartCommitted = false;
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
        if (GameStartCommitted) return;
        if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
        if (!Options.LobbyCorpseEnabled.GetBool()) return;
        if (PlayerControl.LocalPlayer == null || PlayerControl.LocalPlayer.Data == null) return;

        // リンク劣化ゲート (BUG-20260820-06 緩和): 公式鯖の Hacking キックは「劣化リンク × バースト」の
        // 連言でのみ観測されている (同一スポーン列は健全リンクで多数生存 — 2026-08-29 実測 18 ロビー無傷 vs
        // 劣化時 1 キック)。装飾なので劣化中は送らず、健全化するまで 5 秒間隔で延期する。
        // 60 秒待っても回復しなければこのロビーでは諦める (次のロビーで Reset される)。
        if (HealthLog.IsLinkDegradedNow(out string linkDetail))
        {
            if (DeferralPending) return;

            if (++DeferredSpawnAttempts <= MaxDeferredSpawnAttempts)
            {
                DeferralPending = true;
                Logger.Info($"LobbyCorpses: spawn deferred, link degraded ({linkDetail}) attempt={DeferredSpawnAttempts}/{MaxDeferredSpawnAttempts}", "LobbyCorpses");

                LateTask.New(() =>
                {
                    DeferralPending = false;
                    if (!GameStates.IsLobby || GameStates.InGame) return;
                    StartSpawn();
                }, 5f, "LobbyCorpses.DeferredSpawn");
            }
            else
                Logger.Warn($"LobbyCorpses: spawn skipped, link stayed degraded for 60s ({linkDetail})", "LobbyCorpses");

            return;
        }

        DeferredSpawnAttempts = 0;

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
        // host の真の名前を static に退避。
        // 注意: lp.Data.PlayerName を直接読むと ApplySuffix で適用された色 tag 付きの長い装飾名
        // (`<color=#00ffa5>End K not - ホスト</color>...`) が入っていることがあり、それを outfit に
        // 書き戻すと AU server anti-cheat が「outfit PlayerName 長すぎ/不正」で host を Hacking kick する
        // (2026-05-26 再現)。Main.AllPlayerNames は装飾なしの生名を持つのでそちらを優先する。
        SavedHostName = Main.AllPlayerNames.TryGetValue(lp.PlayerId, out string raw) && !string.IsNullOrEmpty(raw)
            ? raw
            : lp.Data.PlayerName;
        byte colorId = (byte)lp.Data.DefaultOutfit.ColorId;
        int spawned = 0;

        // 公式鯖 anti-cheat 緩和: 偽死体の高速 spawn は host を reason=Hacking で落とすため、
        // 1 体ずつ間隔を空けて撒く (FakeBodyBurst.Gentle)。体数はホスト設定 (LobbyCorpseCount) を尊重し
        // レートのみ絞る。kill switch (disable_overkill_body_cap.txt) で旧 4 体/フレームに戻せる。
        for (int i = 0; i < CurrentPositions.Count; i++)
        {
            // 開始コミット/ロビー離脱を検知したら残りを撒かない (走り出したループは StartSpawn 入口の
            // GameStartCommitted ゲートの射程外。劣化 spacing 1.5s ではテイルが最大 ~43s まで伸び、
            // 開始バーストへ装飾 spawn が食い込む窓になる — 2026-08-31 確認)。yield break でなく
            // break で抜けて、後続の復元 Action enqueue と SpawnInProgress 解除は必ず走らせる。
            if (GameStartCommitted || !GameStates.IsLobby) break;

            Utils.RpcCreateDeadBody(
                CurrentPositions[i],
                colorId,
                lp,
                SendOption.Reliable);
            spawned++;
            if (FakeBodyBurst.Gentle) yield return new WaitForSecondsRealtime(FakeBodyBurst.CurrentSpacingSeconds);
            else if (i % 4 == 3) yield return null;
        }

        // 復元 Action を同じ rate limiter キューに積む → 5 体 spawn の直後に必ず実行される。
        // 以前は WaitForSecondsRealtime(0.5f) で待っていたが、待ち中に host が Start を押すと
        // GameData slot 0 の PlayerName が空のまま StartGameHost に入り「プレイヤー」表示 →
        // vanilla クライアント側で通信エラー (Hacking kick) を誘発するレースがあった。
        // rate limiter は順序保証付きなので、enqueue 順で必ず spawn の後に restore が走る。
        int capturedSpawned = spawned;
        DataFlagRateLimiter.Enqueue(() =>
        {
            try
            {
                ApplyHostNameRestore(SavedHostName);
                Logger.Info($"LobbyCorpses: spawned {capturedSpawned} corpses, host name restored to '{SavedHostName}'", "LobbyCorpses");
            }
            catch (Exception ex) { Logger.Warn($"Host re-sync failed: {ex.Message}", "LobbyCorpses"); }

            LastSpawnTime = Time.time;
            SpawnInProgress = false;
        }, calls: 2);
    }

    // 復元の実体 — SpawnCoroutine 末尾と EnsureHostNameRestored() の両方から呼ぶ。
    //
    // 設計修正 (2026-05-26): 旧版は `DefaultOutfit.PlayerName = name + SetDirtyBit(uint.MaxValue)` で
    // outfit を DataMessage 経由 sync していたが、これが AU server anti-cheat の検査対象で
    // PlayerName が長い (`<color>End K not - ホスト</color>...` の装飾名等) と Hacking kick になる。
    // 実際には host のローカル Data.PlayerName は corpse spawn で壊れない (クライアント側 NetObject だけが
    // transient PC spawn でリセットされる) ので、outfit 書き込みは不要。
    // `lp.RpcSetName(name)` 単独で十分 — RpcCalls.SetName 経由は EHR が NotifyRoles で常時使う安全な経路。
    private static void ApplyHostNameRestore(string name)
    {
        if (string.IsNullOrEmpty(name)) return;
        PlayerControl lp = PlayerControl.LocalPlayer;
        if (lp == null || lp.Data == null) return;

        // 直近ブロードキャスト済みの装飾名 (ホストタグ等) があればそちらで復元する。
        // 生名で上書きすると表示が素の名前に戻る一方、FixedUpdatePatch.LastBroadcastName には
        // 装飾名が残ったままなので dirty-check が「送信済み」と誤判定し、チャット送信まで
        // タグが消えたままになる。RpcSetName 経由の装飾名は FixedUpdate が常用する安全経路
        // (kick を誘発した 2026-05-26 の outfit PlayerName 書込みとは別物)。
        if (FixedUpdatePatch.LastBroadcastName.TryGetValue(lp.PlayerId, out string decorated) && !string.IsNullOrEmpty(decorated))
            name = decorated;

        // クライアント側 host label を再 sync するだけ。outfit / SetDirtyBit には触らない
        lp.RpcSetName(name);
    }

    // ゲーム開始ガード: BeginGame Prefix から呼ばれる。
    // SpawnCoroutine の中で rate limiter 待ち中に host が Play 押下した場合に備えて、
    // host 名が SavedHostName と乖離していたら同期的に強制復元する。
    // 既に rate limiter 経由で復元済みなら no-op。
    public static void EnsureHostNameRestored()
    {
        // 名前復元の要否と無関係に、開始コミットのラッチだけは必ず立てる (延期リトライの滑り込み防止)
        GameStartCommitted = true;

        if (string.IsNullOrEmpty(SavedHostName)) return;
        if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
        PlayerControl lp = PlayerControl.LocalPlayer;
        if (lp == null || lp.Data == null) return;
        if (lp.Data.PlayerName == SavedHostName) return;

        try
        {
            ApplyHostNameRestore(SavedHostName);
            Logger.Warn($"LobbyCorpses: host name was corrupted at game start, force-restored to '{SavedHostName}'", "LobbyCorpses");
        }
        catch (Exception ex) { Logger.Warn($"EnsureHostNameRestored failed: {ex.Message}", "LobbyCorpses"); }
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
