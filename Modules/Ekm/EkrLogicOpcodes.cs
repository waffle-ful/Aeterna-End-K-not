using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace EndKnot.Modules.Ekm;

// EKR logic 契約 v1 のアクション系 op 実装 (契約正典: docs/ekr-logic-spec.md §3,§5)。
// 制御系 op (if/wait/stop/var_set/var_add) は EkmLogicRuntime 側で汎用処理される — ここは
// notify/teleport/kill/set_kill_cooldown/speed/cno_* の役職固有セマンティクスとレート予算の実装のみ。
// 予算はここが最後の砦 (エディタ側リンターは警告するだけ)。

// 役職 opcode の実行コンテキスト (EkrFiber.Context に載る不透明値)。汎用エンジンはこの型を知らない。
internal sealed class EkrActionContext
{
    public byte HolderId;
    public byte CtxId = byte.MaxValue; // byte.MaxValue = ctx 無し
    public CustomRoles Slot;
}

internal sealed class EkrActionSink : IEkrActionSink
{
    public static readonly EkrActionSink Instance = new();

    // kill opcode 連鎖ガード (spec §5「kill 連鎖は深さ1」)。RpcCheckAndMurder → (vanilla)MurderPlayer →
    // MurderPlayerPatch.Postfix (Patches/PlayerControlPatch.cs) が OnMurder/AfterPlayerDeathTasks を
    // 同一コールスタックで同期的に呼ぶことを確認済み (2026-08-09 コード確認)。よってこの静的フラグを
    // 呼び出しの前後で立てるだけで、その間に発火する on_kill/on_death の fiber 生成を正しく検出できる。
    private static bool _inOpcodeKill;
    public static bool InOpcodeKill => _inOpcodeKill;

    public void Execute(EkrNode node, EkrFiber fiber)
    {
        if (fiber.Context is not EkrActionContext ctx) return;

        // spec §3: 会議中はアクション系 op は no-op (notify のみ例外で有効)。
        if (node.Op != "notify" && GameStates.IsMeeting) return;

        switch (node.Op)
        {
            case "notify": Notify(node, ctx, fiber); break;
            case "teleport": Teleport(node, ctx); break;
            case "kill": Kill(node, ctx, fiber); break;
            case "set_kill_cooldown": SetKillCooldown(node, ctx); break;
            case "speed": Speed(node, ctx); break;
            case "cno_spawn": CnoSpawn(node, ctx); break;
            case "cno_move": CnoMove(node, ctx); break;
            case "cno_despawn": CnoDespawn(node, ctx); break;
            case "cno_show": CnoShow(node, ctx); break;
        }
    }

    // ── notify (spec: ≤1/秒/ホルダー・会議中も有効) ──────────────────────

    private static void Notify(EkrNode node, EkrActionContext ctx, EkrFiber fiber)
    {
        EkrHolderState state = EkrManager.GetHolderState(ctx.HolderId);
        if (state == null) return;

        bool meeting = GameStates.IsMeeting;
        float now = Time.realtimeSinceStartup;

        // 会議中はチャット私信 (下記) へ切り替わる — ワールド名札と違い「呼ぶたびにチャット欄へ1行
        // 追加」なので、通常の1秒バケットを共用すると64ノード予算いっぱいの notify/wait 連打で
        // 1回の会議中に数十行のスパムになりうる (advisor 指摘・2026-08-09)。会議中専用の粗いバケット
        // (5秒) で別途間引く。
        if (meeting)
        {
            if (state.LastMeetingNotifyTime >= 0f && now - state.LastMeetingNotifyTime < 5f) return;
            state.LastMeetingNotifyTime = now;
        }
        else
        {
            if (state.LastNotifyTime >= 0f && now - state.LastNotifyTime < 1f) return;
            state.LastNotifyTime = now;
        }

        PlayerControl holderPc = ctx.HolderId.GetPlayer();
        if (!holderPc) return;

        string text = SubstituteVariables(node.Text, fiber.Variables);

        // NameNotifyManager.Notify はワールド空間の名前ラベル描画専用で GameStates.IsInTask
        // (= !MeetingHud.Instance) を無条件に要求し、会議中は毎 OnFixedUpdate で全件 Reset() もされる
        // ため会議中は構造的に絶対表示されない (2026-08-09 コード確認)。spec は notify を「会議中も
        // 有効な唯一のアクション op」と明記しているため、会議中はチャット私信 (Utils.SendMessage,
        // sendTo=本人・host-only の既存経路・2引数呼び出しは内部で自己 flush 確認済み) へ切り替えて
        // 代替する。
        if (meeting)
            Utils.SendMessage(text, holderPc.PlayerId);
        else
            holderPc.Notify(text, node.Seconds);
    }

    private static readonly Regex VarPattern = new(@"\{([^{}]+)\}", RegexOptions.Compiled);

    // notify.text 内の {変数名} を現在値に置換する。未定義名はそのまま表示する (spec §3)。
    private static string SubstituteVariables(string text, Dictionary<string, float> vars)
    {
        if (string.IsNullOrEmpty(text) || vars == null || !text.Contains('{')) return text;

        return VarPattern.Replace(text, m =>
        {
            if (!vars.TryGetValue(m.Groups[1].Value, out float v)) return m.Value;
            return Mathf.Approximately(v, Mathf.Round(v)) ? Mathf.RoundToInt(v).ToString() : v.ToString("0.##");
        });
    }

    // ── teleport (Utils.TP の SnapTo トークンバケット共用 + ≤1/2秒/ホルダーは TP の minInterval で強制) ──

    private static void Teleport(EkrNode node, EkrActionContext ctx)
    {
        PlayerControl holderPc = ctx.HolderId.GetPlayer();
        if (!holderPc || !holderPc.IsAlive()) return;

        Vector2? dest = node.Target switch
        {
            "random" => ResolveRandomPosition(),
            "ctx" => ResolveCtxPosition(ctx),
            _ => null
        };

        if (dest == null) return;

        // to:"ctx" で相手が近距離 (1.5u 未満) だと Utils.TP が内部で None へ降格し、実際には飛ばないのに
        // SnapTo cap もこの下のグローバル予算も消費してしまう既知型を避ける (spec §3 2026-08-09 追記。
        // memory: short-tp-none-downgrade-wastes-cap と同型)。
        if (node.Target == "ctx" && Vector2.Distance(holderPc.Pos(), dest.Value) < 1.5f) return;

        // spec §3 2026-08-09 追記: EKR 全体で ≤2/秒 (cross-holder 予算 — Maximum=15 全員が同時に撃っても
        // Utils.TP の共有 SnapTo cap を枯渇させない)。ホルダー毎の ≤1/2秒は下の minInterval で別途強制。
        if (!EkrManager.TryConsumeGlobalTeleportBudget()) return;

        // minInterval=2f が spec の「≤1/2秒/ホルダー」を Utils.TP 側の既存レート機構でそのまま強制する
        // (毎フレーム TP 経路を自前で作らない — Utils.TP の SnapTo トークンバケットに完全に乗る)。
        holderPc.TP(dest.Value, minInterval: 2f);
    }

    private static Vector2? ResolveRandomPosition()
    {
        var positions = RandomSpawn.SpawnMap.GetSpawnMap()?.Positions?.Values;
        if (positions == null || positions.Count == 0) return null;
        return positions.RandomElement();
    }

    private static Vector2? ResolveCtxPosition(EkrActionContext ctx)
    {
        if (ctx.CtxId == byte.MaxValue) return null;
        PlayerControl ctxPc = ctx.CtxId.GetPlayer();
        return ctxPc ? ctxPc.Pos() : null;
    }

    // ── kill (spec: ≤1/秒/ホルダー・連鎖深さ1) ────────────────────────────

    private static void Kill(EkrNode node, EkrActionContext ctx, EkrFiber fiber)
    {
        if (fiber.FromKillChain) return;

        EkrHolderState state = EkrManager.GetHolderState(ctx.HolderId);
        if (state == null) return;

        float now = Time.realtimeSinceStartup;
        if (state.LastKillTime >= 0f && now - state.LastKillTime < 1f) return;

        PlayerControl holderPc = ctx.HolderId.GetPlayer();
        if (!holderPc || !holderPc.IsAlive()) return;

        PlayerControl targetPc;

        if (node.Target == "self") targetPc = holderPc;
        else
        {
            if (ctx.CtxId == byte.MaxValue) return;
            targetPc = ctx.CtxId.GetPlayer();
        }

        if (!targetPc || !targetPc.IsAlive()) return;

        state.LastKillTime = now;

        _inOpcodeKill = true;

        try { holderPc.RpcCheckAndMurder(targetPc); }
        finally { _inOpcodeKill = false; }
    }

    // ── set_kill_cooldown (レート制限なし。既存の pc.SetKillCooldown() 経路で即時反映) ──────────

    private static void SetKillCooldown(EkrNode node, EkrActionContext ctx)
    {
        EkrHolderState state = EkrManager.GetHolderState(ctx.HolderId);
        if (state == null) return;

        state.KillCooldownOverride = node.Seconds;

        PlayerControl holderPc = ctx.HolderId.GetPlayer();
        if (!holderPc) return;

        holderPc.SetKillCooldown();
    }

    // ── speed (spec: 同時1本・再発動は上書き) ─────────────────────────────
    // memory: allplayerspeed-temp-boost-restore-race — 復元は「凍結中スキップ + 捕捉フラグ」。
    // 再発動の上書きは世代カウンタで stale な復元タスクを弾く (memory: sfx-variable-timing-playbook §3 と同型)。

    private static void Speed(EkrNode node, EkrActionContext ctx)
    {
        EkrHolderState state = EkrManager.GetHolderState(ctx.HolderId);
        if (state == null) return;

        PlayerControl holderPc = ctx.HolderId.GetPlayer();
        if (!holderPc || !holderPc.IsAlive()) return;

        if (!state.SpeedBoostActive)
        {
            float current = Main.AllPlayerSpeed.GetValueOrDefault(ctx.HolderId, Main.RealOptionsData.GetFloat(AmongUs.GameOptions.FloatOptionNames.PlayerSpeedMod));

            // 捕捉時に他役職が既に MinSpeed で凍結中だと、その凍結値を「本来の速度」として捕捉してしまい、
            // 復元時に MinSpeed へ戻す = 永久凍結固定になる (memory: allplayerspeed-temp-boost-restore-race
            // の罠を捕捉側にも適用・2026-08-09 監査指摘)。凍結中はゲーム既定値を baseline に使う。
            state.SpeedBaseline = Mathf.Approximately(current, Main.MinSpeed)
                ? Main.RealOptionsData.GetFloat(AmongUs.GameOptions.FloatOptionNames.PlayerSpeedMod)
                : current;

            state.SpeedBoostActive = true;
        }

        int gen = ++state.SpeedGen;
        Main.AllPlayerSpeed[ctx.HolderId] = state.SpeedBaseline * node.Mult;
        holderPc.MarkDirtySettings();

        byte holderId = ctx.HolderId;
        float seconds = node.Seconds;

        LateTask.New(() =>
        {
            if (GameStates.IsEnded) return;

            EkrHolderState s = EkrManager.GetHolderState(holderId);
            if (s == null || s.SpeedGen != gen) return; // 再発動済み (世代不一致) = このタスクは stale

            // 凍結中は復元をスキップして相手の遅延タスクへ委ねる (触ると相手の凍結を解除してしまう)
            if (Mathf.Approximately(Main.AllPlayerSpeed.GetValueOrDefault(holderId), Main.MinSpeed)) return;

            Main.AllPlayerSpeed[holderId] = s.SpeedBaseline;
            s.SpeedBoostActive = false;

            PlayerControl pc = holderId.GetPlayer();
            if (pc) pc.MarkDirtySettings();
        }, seconds, log: false);
    }

    // ── CNO 系 (spec §5: 同時≤3slot/ホルダー・全役職合計≤10体・spawn≤1/秒/ホルダー・move≤2/秒/slot・
    // show≤1/3秒/ホルダー) ── 生存数のカウントは CustomNetObject.AllObjects を見ない — 基底
    // OnMeeting() の会議明け自動復活は Despawn→10s+3s 待ち→再生成の間 AllObjects から一時的に外れる
    // ため、その窓で数えると 0 になり上限が破れる (memory: cno_respawn_window_swallows_live_objects
    // と同型)。EkrManager 側の「スロットに割り当てているか」(実体化前の pending も含む) だけで数える。

    private static void CnoSpawn(EkrNode node, EkrActionContext ctx)
    {
        EkrHolderState state = EkrManager.GetHolderState(ctx.HolderId);
        if (state == null) return;

        float now = Time.realtimeSinceStartup;
        if (state.LastCnoSpawnTime >= 0f && now - state.LastCnoSpawnTime < 1f) return;

        int idx = node.Slot - 1;
        EkrCno existing = state.CnoSlots[idx];

        // 実体化前 (playerControl 未生成) の同一 slot への再 spawn はドロップ (spec §5 孤児コルーチン
        // 防止裁定・2026-08-09) — 基底 spawn コルーチンは Despawn で止まらないため、ここで切り離すと
        // 追跡外のまま居座る CNO を生む。ReleaseCnoSlot 側も同じ理由で no-op するが、それだけでは
        // 「消せない」というだけで下の新規 occupy は防げないため、spawn 自体をここで諦める必要がある。
        if (existing != null && !existing.IsInstantiated) return;

        if (!EkrManager.CanOccupyCnoSlot()) return; // 全役職合計の上限 (オブジェクトはまだ作らない — 先に弾く)

        Vector2? pos = node.Target switch
        {
            "ctx" => ResolveCtxPosition(ctx),
            _ => ResolveSelfPosition(ctx)
        };

        if (pos == null) return;

        EkrDefinition def = EkrManager.GetDefinition(ctx.Slot);
        if (def == null) return;

        // 同一 slot への再 spawn は先に消してから作る (L2 リンタの代替案をモッド側でも保証する)。
        // ここに来る時点で existing は null か実体化済みのどちらかなので ReleaseCnoSlot は必ず効く。
        EkrManager.ReleaseCnoSlot(state, node.Slot);

        var cno = new EkrCno(pos.Value, node.Text, node.Size, def.Color);
        EkrManager.OccupyCnoSlot(state, node.Slot, cno); // 上限は通過済みなので必ず成功する

        state.LastCnoSpawnTime = now;
    }

    private static Vector2? ResolveSelfPosition(EkrActionContext ctx)
    {
        PlayerControl pc = ctx.HolderId.GetPlayer();
        return pc ? pc.Pos() : null;
    }

    private static void CnoMove(EkrNode node, EkrActionContext ctx)
    {
        EkrHolderState state = EkrManager.GetHolderState(ctx.HolderId);
        if (state == null) return;

        int idx = node.Slot - 1;
        EkrCno cno = state.CnoSlots[idx];
        if (cno == null) return;

        float now = Time.realtimeSinceStartup;
        if (state.LastCnoMoveTime[idx] >= 0f && now - state.LastCnoMoveTime[idx] < 0.5f) return;
        state.LastCnoMoveTime[idx] = now;

        cno.MoveToOffset(node.Dx, node.Dy);
    }

    private static void CnoDespawn(EkrNode node, EkrActionContext ctx)
    {
        EkrHolderState state = EkrManager.GetHolderState(ctx.HolderId);
        if (state == null) return;

        EkrManager.ReleaseCnoSlot(state, node.Slot);
    }

    private static void CnoShow(EkrNode node, EkrActionContext ctx)
    {
        EkrHolderState state = EkrManager.GetHolderState(ctx.HolderId);
        if (state == null) return;

        int idx = node.Slot - 1;
        EkrCno cno = state.CnoSlots[idx];
        if (cno == null) return;

        // 実体化前は「まだ出ていないものは変えられない」— ドロップ (spec §5 孤児コルーチン防止裁定)。
        if (!cno.IsInstantiated) return;

        float now = Time.realtimeSinceStartup;
        // spec §5 (2026-08-09 監査改定): cno_show は cno_spawn と共用せず独自の ≤1/3秒/ホルダー バケット
        // (despawn→respawn の fan-out 未課金コスト分を織り込んで spawn より厳しくする)。
        if (state.LastCnoShowTime >= 0f && now - state.LastCnoShowTime < 3f) return;
        state.LastCnoShowTime = now;

        PlayerControl holderPc = ctx.HolderId.GetPlayer();
        if (!holderPc) return;

        cno.SetVisibility(node.Who == "self", holderPc);
    }
}
