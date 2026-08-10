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

        // spec §3: 会議中はアクション系 op は no-op (notify のみ例外で有効)。「会議中」には追放演出中も
        // 含む (v1.1 監査追記 2026-08-09) — 投票終了で MeetingHud が閉じると IsMeeting=false/IsInTask=true
        // になり、ガード無しだと追放演出中に on_second 等が発火して spawn 系 op が会議クローズ送信
        // (SetRole 全員分+Desync+NotifyRoles スイープ) と同一窓に重なる — BUG-20260803-07 の合算キック窓
        // そのもの。CorpseSpawn の個別ガードはこの共通関所への二重防御として残置。
        if (node.Op != "notify" && (GameStates.IsMeeting || ExileController.Instance)) return;

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
            case "dummy_spawn": DummySpawn(node, ctx); break;
            case "corpse_spawn": CorpseSpawn(node, ctx); break;
            case "marker_save": MarkerSave(node, ctx); break; // v1.2
            case "teleport_other": TeleportOther(node, ctx); break; // v1.2
            case "portal_place": PortalPlace(node, ctx); break; // v1.2
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

        Vector2? dest = ResolveTeleportToPosition(node.Target, ctx);
        if (dest == null) return;

        // to:"ctx"/マーカーで目的地が近距離 (1.5u 未満) だと Utils.TP が内部で None へ降格し、実際には
        // 飛ばないのに SnapTo cap もこの下のグローバル予算も消費してしまう既知型を避ける (spec §3
        // 2026-08-09 追記・v1.2 でマーカーにも拡張。memory: short-tp-none-downgrade-wastes-cap と同型)。
        if (node.Target != "random" && Vector2.Distance(holderPc.Pos(), dest.Value) < 1.5f) return;

        // spec §3 2026-08-09 追記: EKR 全体で ≤2/秒 (cross-holder 予算 — Maximum=15 全員が同時に撃っても
        // Utils.TP の共有 SnapTo cap を枯渇させない)。ホルダー毎の ≤1/2秒は下の minInterval で別途強制。
        if (!EkrManager.TryConsumeGlobalTeleportBudget()) return;

        // minInterval=2f が spec の「≤1/2秒/ホルダー」を Utils.TP 側の既存レート機構でそのまま強制する
        // (毎フレーム TP 経路を自前で作らない — Utils.TP の SnapTo トークンバケットに完全に乗る)。
        holderPc.TP(dest.Value, minInterval: 2f);

        // spec §2 v1.2: EKR 起因の TP は着地点で半径内の全接触センサーにラッチ済み扱い (ポータル
        // ping-pong の構造的回避 — teleport でポータルの目の前に飛ばされても即座に再発火しない)。
        EkrManager.PrelatchTouchSensorsNear(holderPc.PlayerId, dest.Value);
    }

    // v1.2: teleport.to / teleport_other.to の共通解決 (random は teleport 専用)。
    private static Vector2? ResolveTeleportToPosition(string to, EkrActionContext ctx)
    {
        return to switch
        {
            "random" => ResolveRandomPosition(),
            "ctx" => ResolveCtxPosition(ctx),
            "marker1" => ResolveMarkerPosition(ctx, 1),
            "marker2" => ResolveMarkerPosition(ctx, 2),
            "marker3" => ResolveMarkerPosition(ctx, 3),
            "marker4" => ResolveMarkerPosition(ctx, 4),
            _ => null
        };
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

    // v1.2 (marker_save の消費側): 未保存 (null) のマーカーはそのまま null を返す (呼び出し元で
    // ドロップ・cap 非消費・spec §3)。
    private static Vector2? ResolveMarkerPosition(EkrActionContext ctx, int markerNumber1Based)
    {
        EkrHolderState state = EkrManager.GetHolderState(ctx.HolderId);
        return state?.Markers[markerNumber1Based - 1];
    }

    // v1.2: marker_save.at の cno1..3 解決。未実体化/未使用 slot は null (no-op)。
    private static Vector2? ResolveOwnCnoPosition(EkrActionContext ctx, int slotIndex0Based)
    {
        EkrHolderState state = EkrManager.GetHolderState(ctx.HolderId);
        if (state == null) return null;

        if (state.CnoSlots[slotIndex0Based] is not CustomNetObject cno || !cno.playerControl) return null;
        return cno.Position;
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

        // spec §5 (v1.1): 会議明けから10秒間はドロップ (dummy_spawn と同じ規約)。基底 CreateNetObject の
        // 待ちは intro 直後 (tooEarly) 分岐にしか無く、会議明けの新規 spawn は待ちなしで
        // DataFlagRateLimiter へ直行するため、ここで落とさないと追放スイープとの合算 nests に乗る。
        if (EkrManager.LastMeetingEndTime >= 0f && now - EkrManager.LastMeetingEndTime < 10f) return;

        int idx = node.Slot - 1;
        IEkrSlotCno existing = state.CnoSlots[idx];

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

        // cross-holder 予算 (spec §5 v1.1 監査追記) — 他の drop 理由を全て通過した「本当に spawn する」
        // 直前でだけ消費する (先に消費すると、後段の理由で drop されるのに予算だけ減る無駄撃ちになる)。
        if (!EkrManager.TryConsumeGlobalCnoSpawnBudget()) return;

        // 同一 slot への再 spawn は先に消してから作る (L2 リンタの代替案をモッド側でも保証する)。
        // ここに来る時点で existing は null か実体化済みのどちらかなので ReleaseCnoSlot は必ず効く。
        EkrManager.ReleaseCnoSlot(state, node.Slot);

        var cno = new EkrCno(pos.Value, node.Text, node.Size, def.Color);
        EkrManager.OccupyCnoSlot(state, node.Slot, cno); // 上限は通過済みなので必ず成功する

        // spec §2 v1.2: 設置時に半径内へ既にいるプレイヤーはラッチ済み扱い (placer self-grab 既知型の
        // 構造的回避)。
        EkrManager.PrimeTouchSensor(state, node.Slot - 1, pos.Value, isPortal: false);

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
        IEkrSlotCno cno = state.CnoSlots[idx];
        if (cno == null) return;

        // 実体化前は「まだ出ていないものは変えられない」(spec §5・他 op と同じ一貫ガード)。TP() は
        // Despawn を呼ばないため孤児化リスクこそ無いが、実体化時に CreateNetObject コルーチンが
        // Position を spawn 位置で再代入するため実体化前の move はどのみち上書き消滅する — ガード無し
        // だとレート予算 (0.5秒/slot) だけ無駄に消費する (完成前監査指摘・2026-08-09)。
        if (!cno.IsInstantiated) return;

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

        // v1.1: cno_show はダミー (EkrDummyCno) には no-op — despawn→respawn で outfit 再送を伴い高価な
        // ため非対応 (spec §3)。`is not EkrCno` は null (未占有 slot) も自然に弾く。
        if (state.CnoSlots[idx] is not EkrCno cno) return;

        // 実体化前は「まだ出ていないものは変えられない」— ドロップ (spec §5 孤児コルーチン防止裁定)。
        if (!cno.IsInstantiated) return;

        float now = Time.realtimeSinceStartup;
        // spec §5 (2026-08-09 監査改定): cno_show は cno_spawn と共用せず独自の ≤1/3秒/ホルダー バケット
        // (despawn→respawn の fan-out 未課金コスト分を織り込んで spawn より厳しくする)。
        if (state.LastCnoShowTime >= 0f && now - state.LastCnoShowTime < 3f) return;

        // spec §5 (v1.1): 会議明けから10秒間はドロップ (dummy_spawn と同じ規約)。実装が despawn→respawn
        // = spawn と同じ付帯送信を伴う以上、生成系と同じ位相ガードが要る。
        if (EkrManager.LastMeetingEndTime >= 0f && now - EkrManager.LastMeetingEndTime < 10f) return;

        PlayerControl holderPc = ctx.HolderId.GetPlayer();
        if (!holderPc) return;

        // cross-holder 予算 (spec §5 v1.1 監査追記): 実装が despawn→respawn = spawn と同じ付帯送信を
        // 伴うため、生成系共通の予算にも課金する。per-holder バケット (LastCnoShowTime) の更新は
        // ここを通過した後 — グローバル拒否時に per-holder 分まで消費しない。
        if (!EkrManager.TryConsumeGlobalCnoSpawnBudget()) return;
        state.LastCnoShowTime = now;

        cno.SetVisibility(node.Who == "self", holderPc);
    }

    // ── dummy_spawn (v1.1 spec §3,§5: spawn≤1/3秒/ホルダー・会議明け10秒ドロップ・全体≤10体はcno_spawnと合算) ──
    // slot は cno_spawn と共有 (IEkrSlotCno 経由で cno_move/cno_despawn がそのまま効く)。cno_show だけ
    // 非対応 (CnoShow 側の `is not EkrCno` で弾かれる)。

    private static void DummySpawn(EkrNode node, EkrActionContext ctx)
    {
        EkrHolderState state = EkrManager.GetHolderState(ctx.HolderId);
        if (state == null) return;

        float now = Time.realtimeSinceStartup;
        if (state.LastDummySpawnTime >= 0f && now - state.LastDummySpawnTime < 3f) return;

        // spec §5 (v1.1): 会議明けから10秒間はドロップ (会議クローズ+追放スイープとの合算 nests キック
        // 回避・BUG-20260803-07 と同型)。未記録 (-1) は通す。
        if (EkrManager.LastMeetingEndTime >= 0f && now - EkrManager.LastMeetingEndTime < 10f) return;

        int idx = node.Slot - 1;
        IEkrSlotCno existing = state.CnoSlots[idx];

        // cno_spawn と同じ孤児コルーチン防止裁定 (spec §5) — 実体化前の同一 slot への再 spawn はドロップ。
        if (existing != null && !existing.IsInstantiated) return;

        if (!EkrManager.CanOccupyCnoSlot()) return; // 全体 ≤10体 (cno_spawn と合算・spec §5)

        Vector2? pos = node.Target switch
        {
            "ctx" => ResolveCtxPosition(ctx),
            _ => ResolveSelfPosition(ctx)
        };

        if (pos == null) return;

        // cross-holder 予算 (spec §5 v1.1 監査追記) — CnoSpawn と同じく「本当に spawn する」直前で消費。
        if (!EkrManager.TryConsumeGlobalCnoSpawnBudget()) return;

        // 同一 slot への再 spawn は先に消してから作る (cno_spawn と同じ「消してから作る」規約)。
        // ここに来る時点で existing は null か実体化済みのどちらかなので ReleaseCnoSlot は必ず効く。
        EkrManager.ReleaseCnoSlot(state, node.Slot);

        var dummy = new EkrDummyCno(pos.Value, node.Name, node.Killable);
        EkrManager.OccupyCnoSlot(state, node.Slot, dummy); // 上限は通過済みなので必ず成功する

        // spec §2 v1.2: 設置時に半径内へ既にいるプレイヤーはラッチ済み扱い (cno_spawn と同じ規約)。
        EkrManager.PrimeTouchSensor(state, node.Slot - 1, pos.Value, isPortal: false);

        state.LastDummySpawnTime = now;
    }

    // ── corpse_spawn (v1.1 spec §3,§5: ≤1/2秒/ホルダー・タスク中のみ) ───────────────────────────
    // 「通報できる普通の偽死体」— 特別扱い登録簿 (ReportDeadBodyPatch.DummyCorpseBodyIds 等) には載せない
    // (spec §3)。CNO は Data を持たず死体の親になれないため、生存実プレイヤーを無作為に借用する
    // (Trapster.OnVanish と同じ借用パターン — 借りた側の色は下で colorId 上書きするため見た目には出ない)。
    // アライブ判定は意図的に付けない — spec §2 は cno_* 系を on_death 起点 fiber からも実行可としており、
    // corpse_spawn はその仲間 (ResolveSelfPosition はゴーストでも解決できる)。

    private static void CorpseSpawn(EkrNode node, EkrActionContext ctx)
    {
        EkrHolderState state = EkrManager.GetHolderState(ctx.HolderId);
        if (state == null) return;

        float now = Time.realtimeSinceStartup;
        if (state.LastCorpseSpawnTime >= 0f && now - state.LastCorpseSpawnTime < 2f) return;

        // spec §5: ペット0.2秒遅延発動が会議/追放演出へ滑り込む既知型の回避。GameStates.IsInTask は
        // InGame&&!IsMeeting と等価だが、追放演出中は MeetingHud が既に閉じていて IsInTask だけでは
        // すり抜けるため ExileController.Instance も明示的に見る (spec 指定どおり)。
        if (!GameStates.IsInTask || ExileController.Instance) return;

        PlayerControl holderPc = ctx.HolderId.GetPlayer();
        if (!holderPc) return;

        Vector2? pos = node.Target switch
        {
            "ctx" => ResolveCtxPosition(ctx),
            _ => ResolveSelfPosition(ctx)
        };

        if (pos == null) return;

        PlayerControl parent = Main.EnumerateAlivePlayerControls().RandomElement();
        if (!parent) return;

        byte colorId = node.Color == "random"
            ? (byte)IRandom.Instance.Next(0, Palette.PlayerColors.Length)
            : (byte)holderPc.Data.DefaultOutfit.ColorId;

        state.LastCorpseSpawnTime = now;

        Utils.RpcCreateDeadBody(pos.Value, colorId, parent);
    }

    // ── marker_save (v1.2 spec §3: 予算なし・ctx 無し/未実体化 CNO 参照は no-op) ────────────────────

    private static void MarkerSave(EkrNode node, EkrActionContext ctx)
    {
        EkrHolderState state = EkrManager.GetHolderState(ctx.HolderId);
        if (state == null) return;

        Vector2? pos = node.Target switch
        {
            "self" => ResolveSelfPosition(ctx),
            "ctx" => ResolveCtxPosition(ctx),
            "cno1" => ResolveOwnCnoPosition(ctx, 0),
            "cno2" => ResolveOwnCnoPosition(ctx, 1),
            "cno3" => ResolveOwnCnoPosition(ctx, 2),
            _ => null
        };

        if (pos == null) return; // no-op (spec §3)

        state.Markers[node.Slot - 1] = pos;
    }

    // ── teleport_other (v1.2 spec §3,§5: target=ctx のみ・teleport と同一 per-holder バケット/
    // cross-holder 予算を共有・死者/ctx 無しは no-op) ─────────────────────────────────────────
    // 「同一バケットを共有」は Utils.TP の LastRateLimitedTPTime を teleport と同じ minInterval=2f で
    // 経由することで担保する (新しい EkrHolderState フィールドを追加しない — spec 2026-08-10 指示)。
    // teleport は「動かす対象=holder」なのでそのバケットが自然に holder キーになるのに対し、
    // teleport_other は「動かす対象=ctx」なのでキーは ctx 側になる — 同一の下層レート機構を再利用する
    // という意味での「共有」であり、per-holder という言葉どおりの厳密なホルダーキー分離ではない。

    private static void TeleportOther(EkrNode node, EkrActionContext ctx)
    {
        PlayerControl holderPc = ctx.HolderId.GetPlayer();
        if (!holderPc || !holderPc.IsAlive()) return; // teleport/kill/speed と同じ「死者は常に no-op」

        if (ctx.CtxId == byte.MaxValue) return; // ctx 無しは no-op (spec §3)
        PlayerControl targetPc = ctx.CtxId.GetPlayer();
        if (!targetPc || !targetPc.IsAlive()) return; // 死者は no-op (spec §3)

        Vector2? dest = node.Target switch
        {
            "self" => ResolveSelfPosition(ctx),
            "marker1" => ResolveMarkerPosition(ctx, 1),
            "marker2" => ResolveMarkerPosition(ctx, 2),
            "marker3" => ResolveMarkerPosition(ctx, 3),
            "marker4" => ResolveMarkerPosition(ctx, 4),
            _ => null
        };

        if (dest == null) return; // 未保存マーカーはドロップ (cap 非消費・spec §3)

        // 1.5u 未満ドロップ (teleport と同じ既知型回避・spec §3)。
        if (Vector2.Distance(targetPc.Pos(), dest.Value) < 1.5f) return;

        // EKR 全体 ≤2/秒 (teleport と共有・spec §5)。
        if (!EkrManager.TryConsumeGlobalTeleportBudget()) return;

        Utils.TP(targetPc.NetTransform, dest.Value, minInterval: 2f);

        // spec §2 v1.2: 着地点で半径内の全接触センサーにラッチ済み扱い。
        EkrManager.PrelatchTouchSensorsNear(targetPc.PlayerId, dest.Value);
    }

    // ── portal_place (v1.2 マクロ spec §3,§5) ───────────────────────────────────────────────
    // A/B 2枚で1対のワープポータル。実体は EkrCno 流用 (会議明け自動復活エンジンに乗る)。
    // per-holder ≤1/2秒 + CNO 生成系 cross-holder 予算 + 会議明け10秒ドロップ (生成系4兄弟目)。

    private const string PortalSprite = "◎";
    private const int PortalSize = 6;

    private static void PortalPlace(EkrNode node, EkrActionContext ctx)
    {
        EkrHolderState state = EkrManager.GetHolderState(ctx.HolderId);
        if (state == null) return;

        float now = Time.realtimeSinceStartup;
        if (state.LastPortalPlaceTime >= 0f && now - state.LastPortalPlaceTime < 2f) return;

        // spec §5 (v1.2): 生成系4兄弟目 — 会議明けから10秒間はドロップ。
        if (EkrManager.LastMeetingEndTime >= 0f && now - EkrManager.LastMeetingEndTime < 10f) return;

        int idx = node.Which == "b" ? 1 : 0;
        IEkrSlotCno existing = state.Portals[idx];

        // 孤児コルーチン防止裁定 (spec §5) — 実体化前の同一 which への再設置はドロップ。
        if (existing != null && !existing.IsInstantiated) return;

        if (!EkrManager.CanOccupyCnoSlot()) return; // 全体 ≤10体 (CnoSlots と合算・spec §5)

        Vector2? pos = ResolveSelfPosition(ctx); // portal_place は常に自分の足元 (spec §3)
        if (pos == null) return;

        EkrDefinition def = EkrManager.GetDefinition(ctx.Slot);
        if (def == null) return;

        // cross-holder 予算 (spec §5) — 「本当に spawn する」直前で消費 (cno_spawn と同じ順序)。
        if (!EkrManager.TryConsumeGlobalCnoSpawnBudget()) return;

        // 同じ which への再実行は「移設」— 旧位置の実体を消してから張り直す (spec §3)。
        EkrManager.ReleasePortalSlot(state, idx);

        var portal = new EkrCno(pos.Value, PortalSprite, PortalSize, def.Color);
        EkrManager.OccupyPortalSlot(state, idx, portal);

        // spec §2 v1.2: 設置時に半径内へ既にいるプレイヤーはラッチ済み扱い。
        EkrManager.PrimeTouchSensor(state, idx, pos.Value, isPortal: true);

        state.LastPortalPlaceTime = now;
    }
}
