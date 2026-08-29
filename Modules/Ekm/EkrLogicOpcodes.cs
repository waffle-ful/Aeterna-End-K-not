using System;
using System.Collections.Generic;
using System.Linq;
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

    // Wave 1 (spec §2 on_attacked 同期プロローグ): cancel_attack が有効なのは関所の中で同期実行されて
    // いる間 (最初の wait まで) だけ。wait を跨いだ後は攻撃が既に解決済みなので no-op (リンタ L17)。
    public bool AllowCancelAttack;
    public bool CancelAttack;

    // Wave 2 (docs/ekn-wave2-contract.md §1.1 on_meeting_vote 同期プロローグ): cancel_attack と同型。
    public bool AllowCancelVote;
    public bool CancelVote;
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
        // cancel_attack は完全にローカル判定 (送信ゼロ・ワールドへの作用ゼロ) なので notify と同じく
        // この関所から除外する。もっとも on_attacked は会議中に発火し得ないため実質到達しない防御。
        //
        // Wave 2 (docs/ekn-wave2-contract.md §4 会議中 op 白名単): remember/inspect/reveal/vote_weight_set は
        // 会議中も常時有効 (ローカル状態または notify と同じ既存チャネルのみ)。cancel_vote/vote_block/
        // vote_swap/exile は逆に「会議中のみ有効」(タスク中は no-op) — 投票操作はそもそも会議でしか
        // 意味を持たない。
        //
        // Wave 4 (docs/ekn-wave4-contract.md §3.1/§4): link/unlink は会議中も有効 (ローカル状態のみ・
        // 送信ゼロ —「かいぎで投票した人とつなぐ」on_meeting_vote → link(ctx) が正規の組み方で、
        // 抜けると無音で崩れる)。recruit はどちらの白名単にも**載せない** — 未分類 = task-only の既定が
        // そのまま契約 §4 の「会議中 (追放演出含む) は no-op」になる (RpcChangeRoleBasis の会議/追放中
        // コルーチン遅延に仕事をさせない)。
        bool meetingOrExile = GameStates.IsMeeting || ExileController.Instance;

        bool isMeetingOnly = node.Op is "cancel_vote" or "vote_block" or "vote_swap" or "exile";

        if (isMeetingOnly)
        {
            if (!meetingOrExile) return;
        }
        else if (node.Op is not "notify" and not "cancel_attack" and not "remember" and not "inspect" and not "reveal" and not "vote_weight_set" and not "link" and not "unlink" && meetingOrExile) return;

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
            // v1.3: pull は teleport_other の to:"self" 経路と完全共有 (spec §3「新バケットを作らない」)。
            // parser (EkmLogicRuntime.TryParseNode) が node.Target を "self" に固定しているのでそのまま渡せる。
            case "pull": TeleportOther(node, ctx); break;
            case "drag": Drag(node, ctx); break; // v1.3
            case "field": Field(node, ctx); break; // v1.3
            case "remember": Remember(node, ctx); break; // Wave 1
            case "cancel_attack": CancelAttack(ctx); break; // Wave 1
            // Wave 2 (docs/ekn-wave2-contract.md)
            case "inspect": Inspect(node, ctx, fiber); break;
            case "reveal": Reveal(node, ctx); break;
            case "arrow_show": ArrowShow(node, ctx); break;
            case "arrow_mark": ArrowMark(node, ctx); break;
            case "arrow_hide": ArrowHide(ctx); break;
            case "cancel_vote": CancelVote(ctx); break;
            case "vote_weight_set": VoteWeightSet(node, ctx); break;
            case "vote_block": VoteBlock(node, ctx); break;
            case "vote_swap": VoteSwap(ctx); break;
            case "exile": Exile(node, ctx); break;
            // Wave 4 (docs/ekn-wave4-contract.md §3,§4)
            case "link": Link(node, ctx); break;
            case "unlink": Unlink(ctx); break;
            case "recruit": Recruit(node, ctx); break;
            // Wave 5 (docs/ekn-wave5-contract.md §1)
            case "effect_give": EffectGive(node, ctx); break;
            // Wave 6 (docs/ekn-wave6-contract.md §1)
            case "cno_launch": CnoLaunch(node, ctx); break;
        }
    }

    // ── Wave 1: 統一セレクタ解決 (spec §3) ───────────────────────────────────────────────────
    // 壊れた参照 (ctx 無し / 失効 saved / 部屋外の room) は「静かに no-op・予算不消費」— 呼び出し元は
    // null / 空リストを見たら何もせず return する (参照整合性3原則②)。

    private static PlayerControl ResolveSingle(string selector, EkrActionContext ctx)
    {
        switch (selector)
        {
            case "self":
            {
                PlayerControl pc = ctx.HolderId.GetPlayer();
                return pc ? pc : null;
            }
            case "ctx":
            {
                if (ctx.CtxId == byte.MaxValue) return null;
                PlayerControl pc = ctx.CtxId.GetPlayer();
                return pc ? pc : null;
            }
            case "linked": return ResolveLinked(ctx); // Wave 4 (docs/ekn-wave4-contract.md §3.4)
            case "saved1": return ResolveSaved(ctx, 0);
            case "saved2": return ResolveSaved(ctx, 1);
            case "nearest": return ResolveNearest(ctx);
            case "random":
            {
                List<PlayerControl> others = AliveOthers(ctx.HolderId);
                return others.Count == 0 ? null : others[IRandom.Instance.Next(0, others.Count)];
            }
            default: return null;
        }
    }

    // spec §3: おぼえた人は死亡・切断で自動失効 → 参照は no-op (予算不消費)。失効を検出したら
    // スロットも消しておく (次回以降の再検証を省く。掃除の常駐処理は作らない)。
    private static PlayerControl ResolveSaved(EkrActionContext ctx, int index)
    {
        EkrHolderState state = EkrManager.GetHolderState(ctx.HolderId);
        if (state == null) return null;

        byte savedId = state.Saved[index];
        if (savedId == byte.MaxValue) return null;

        PlayerControl pc = savedId.GetPlayer();

        if (!pc || !pc.IsAlive() || pc.Data == null || pc.Data.Disconnected)
        {
            state.Saved[index] = byte.MaxValue;
            return null;
        }

        return pc;
    }

    // Wave 4 (docs/ekn-wave4-contract.md §3.4): つないだ人。失効 (リンク無し/死亡/切断) は静かに no-op。
    // 切断/消滅だけ lazy 解消する (ResolveSaved と同じ参照整合性3原則② — 掃除の常駐処理は作らない)。
    // ⚠️ 相手の死亡ではリンクを消さない: 追放死は FireDeath が死亡確定の 3.5 秒後に走るため
    // (ExilePatch.cs:72 の SetDead vs :132 の LateTask)、その窓に走った fiber がここでリンクを先に
    // 消すと on_linked_death が無音で落ちる (BUG-20260828-01)。EkrManager.ResolveWatchedId と同じ裁定で、
    // 死亡による解消の権限は FireDeath に一本化する。
    private static PlayerControl ResolveLinked(EkrActionContext ctx)
    {
        EkrHolderState state = EkrManager.GetHolderState(ctx.HolderId);
        if (state == null) return null;

        byte linkedId = state.LinkedId;
        if (linkedId == byte.MaxValue) return null;

        PlayerControl pc = linkedId.GetPlayer();

        if (!pc || pc.Data == null || pc.Data.Disconnected)
        {
            state.LinkedId = byte.MaxValue;
            return null;
        }

        return pc.IsAlive() ? pc : null; // 死亡 = 今は no-op・解消は FireDeath の領分
    }

    private static PlayerControl ResolveNearest(EkrActionContext ctx)
    {
        PlayerControl holderPc = ctx.HolderId.GetPlayer();
        if (!holderPc) return null;

        Vector2 from = holderPc.Pos();
        PlayerControl best = null;
        float bestDist = float.MaxValue;

        foreach (PlayerControl pc in Main.AllAlivePlayerControls)
        {
            if (pc.PlayerId == ctx.HolderId) continue;

            float dist = Vector2.Distance(pc.Pos(), from);
            if (dist >= bestDist) continue;

            bestDist = dist;
            best = pc;
        }

        return best;
    }

    private static List<PlayerControl> AliveOthers(byte holderId)
    {
        var list = new List<PlayerControl>();

        foreach (PlayerControl pc in Main.AllAlivePlayerControls)
            if (pc.PlayerId != holderId)
                list.Add(pc);

        return list;
    }

    // 複数セレクタ (all / room)。単数セレクタが来たら1件のリストに包んで返す — 呼び出し元
    // (notify) は単複を区別せず「1人ごとに個別課金」する。
    private static List<PlayerControl> ResolveMulti(string selector, EkrActionContext ctx)
    {
        switch (selector)
        {
            case "all":
                return AliveOthers(ctx.HolderId);

            case "room":
            {
                // spec §3: 「自分と同じ PlainShipRoom 内の生存者」— all と違い自分を除外する明記が無いので
                // ホルダー自身も含める (「おなじ部屋のみんな」の素直な読み)。部屋の外にいれば空集合 = no-op。
                var list = new List<PlayerControl>();

                PlayerControl holderPc = ctx.HolderId.GetPlayer();
                if (!holderPc) return list;

                PlainShipRoom room = holderPc.GetPlainShipRoom();
                if (!room) return list;

                foreach (PlayerControl pc in Main.AllAlivePlayerControls)
                {
                    PlainShipRoom other = pc.GetPlainShipRoom();
                    if (other && other.RoomId == room.RoomId) list.Add(pc);
                }

                return list;
            }

            default:
            {
                var list = new List<PlayerControl>();
                PlayerControl single = ResolveSingle(selector, ctx);
                if (single) list.Add(single);
                return list;
            }
        }
    }

    // ── notify (spec: ≤1/秒/ホルダー・会議中も有効) ──────────────────────

    private static void Notify(EkrNode node, EkrActionContext ctx, EkrFiber fiber)
    {
        EkrHolderState state = EkrManager.GetHolderState(ctx.HolderId);
        if (state == null) return;

        // Wave 1 (spec §3): target は任意 (既定 self)・複数セレクタを受理する唯一の op。
        List<PlayerControl> targets = ResolveMulti(node.Target ?? "self", ctx);
        if (targets.Count == 0) return; // 壊れた参照/部屋外は静かに no-op

        // 複数対象は EKR 全体の cross-holder 予算にも課金する (EkrManager 側のコメント参照 —
        // 満員 target:"all" = 1フレーム14本の RpcSetName、かつ複数ホルダーの同時発火が既定)。
        if (targets.Count > 1 && !EkrManager.TryConsumeGlobalNotifyBroadcastBudget()) return;

        bool meeting = GameStates.IsMeeting;
        float now = Time.realtimeSinceStartup;
        string text = SubstituteVariables(node.Text, fiber.Variables);

        foreach (PlayerControl targetPc in targets)
        {
            if (!targetPc) continue;

            // spec §3: 複数対象は「1人ごとに個別課金・超過分は静かにドロップ」。バケットは
            // per-(ホルダー, 受け取り手) — ホルダー1本の共有バケットにすると target:"all" が
            // 1人にしか届かず、複数セレクタ唯一の op が機能しなくなる。
            Dictionary<byte, float> bucket = meeting ? state.LastMeetingNotifyTime : state.LastNotifyTime;
            float interval = meeting ? 5f : 1f;

            if (bucket.TryGetValue(targetPc.PlayerId, out float last) && now - last < interval) continue;
            bucket[targetPc.PlayerId] = now;

            // NameNotifyManager.Notify はワールド空間の名前ラベル描画専用で GameStates.IsInTask
            // (= !MeetingHud.Instance) を無条件に要求し、会議中は毎 OnFixedUpdate で全件 Reset() もされる
            // ため会議中は構造的に絶対表示されない (2026-08-09 コード確認)。spec は notify を「会議中も
            // 有効な唯一のアクション op」と明記しているため、会議中はチャット私信 (Utils.SendMessage,
            // sendTo=本人・host-only の既存経路・2引数呼び出しは内部で自己 flush 確認済み) へ切り替えて
            // 代替する。
            if (meeting)
                Utils.SendMessage(text, targetPc.PlayerId);
            else
                targetPc.Notify(text, node.Seconds);
        }
    }

    // ── Wave 1: remember / cancel_attack (spec §3 — どちらも予算なし・ローカル状態のみ) ────────

    private static void Remember(EkrNode node, EkrActionContext ctx)
    {
        EkrHolderState state = EkrManager.GetHolderState(ctx.HolderId);
        if (state == null) return;

        PlayerControl targetPc = ResolveSingle(node.Target, ctx);
        if (!targetPc) return; // ctx 無し文脈・失効 saved 参照は no-op (spec §3)

        state.Saved[node.Slot - 1] = targetPc.PlayerId;
    }

    // spec §2: 攻撃を止められるのは on_attacked の同期プロローグ内だけ。wait 後は攻撃が既に
    // 解決済みなので no-op (リンタ L17 がエディタ側で先に教える)。
    private static void CancelAttack(EkrActionContext ctx)
    {
        if (ctx.AllowCancelAttack) ctx.CancelAttack = true;
    }

    private static readonly Regex VarPattern = new(@"\{([^{}]+)\}", RegexOptions.Compiled);

    // notify.text 内の {変数名} を現在値に置換する。未定義名はそのまま表示する (spec §3)。
    // Wave 3: progress.text も**同一規約・同一実装**を使う契約なので EkrManager からも呼ぶ
    // (EkrActionSink.SubstituteVariables として internal 公開)。
    internal static string SubstituteVariables(string text, Dictionary<string, float> vars)
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
            // Wave 1 (spec §3 空間セレクタ): じぶんのオブジェクト 1..3 のところ。未実体化なら no-op。
            "cno1" => ResolveOwnCnoPosition(ctx, 0),
            "cno2" => ResolveOwnCnoPosition(ctx, 1),
            "cno3" => ResolveOwnCnoPosition(ctx, 2),
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

        // Wave 1: 単数セレクタ全種 (self/ctx/saved1/saved2/nearest/random)。
        PlayerControl targetPc = ResolveSingle(node.Target, ctx);
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

        // Wave 5: 同じ Main.AllPlayerSpeed キーへ effect_give の movement 効果が乗っていたら先に畳む
        // (契約 §1.2 後勝ちの対称側)。畳まないと 2 つの書き手が baseline を交換して歪みが永続化する。
        EkrManager.ClearMovementEffect(ctx.HolderId);

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

            // 死亡/剥奪の復元経路が既にブーストを畳んでいたら何もしない (Wave 1 でパッシブ速度を
            // 足したため、ここで opcode の baseline を書き戻すとパッシブ復元を踏み潰す)。
            if (!s.SpeedBoostActive) return;

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
        if (!pc) return null;

        // Wave 1: 死後 (on_death 起点 fiber) は「最後に生きていた座標」を使う。passives.corpse="vanish" は
        // 死体を消すためにホルダーをマップ外 (50,50) へ飛ばしてから殺すので、素の Pos() だと
        // spec §2 v1.1 が正当ユースとして挙げる「死んだ場所に身代わりダミー/偽死体を残す」が壊れる。
        // 生存中は毎 FixedUpdate 更新しているので、通常の死亡では従来と同じ座標になる。
        if (!pc.IsAlive())
        {
            EkrHolderState state = EkrManager.GetHolderState(ctx.HolderId);
            if (state is { HasLastLivePosition: true }) return state.LastLivePosition;
        }

        return pc.Pos();
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

    // ── Wave 6 (docs/ekn-wave6-contract.md §1): とばす (発射体プリミティブ) ──────────────────────
    // 「とばせるのは cno_spawn で出したオブジェクトだけ」— 空 slot / ダミー (EkrDummyCno) / field の実体は
    // 無音 no-op (予算不消費)。ホルダー生存ガードは**付けない** (契約 §1.1: on_death 起点 fiber から
    // 「死に際に弾をはなつ」ができる — 死者への no-op が要る op は個別に IsAlive を見る側の作法)。
    private static void CnoLaunch(EkrNode node, EkrActionContext ctx)
    {
        EkrHolderState state = EkrManager.GetHolderState(ctx.HolderId);
        if (state == null) return;

        int idx = node.Slot - 1;

        // 対象は自分の CNO slot に居る EkrCno のみ。`is not EkrCno` が null (未占有) とダミーを同時に弾く。
        // 既に飛んでいる弾の二度撃ちも no-op (飛行中の向き変更は「追尾しない」裁定に反する)。
        if (state.CnoSlots[idx] is not EkrCno cno || cno.Launched)
        {
            EkrManager.FlightLog($"launch drop slot={node.Slot} reason=no-cno-or-already-launched");
            return;
        }

        float now = Time.realtimeSinceStartup;

        // 契約 §1.2: per-holder ≤1/2秒。超過は静かにドロップ (予算不消費)。
        if (state.LastCnoLaunchTime >= 0f && now - state.LastCnoLaunchTime < 2f)
        {
            EkrManager.FlightLog($"launch drop slot={node.Slot} reason=holder-rate");
            return;
        }

        // 契約 §1.1: 会議明け10秒ドロップは生成系5兄弟と同じ規約で適用する
        // (追放スイープ+レートゲートのドレインと同一窓に位置 update を重ねない)。
        if (EkrManager.LastMeetingEndTime >= 0f && now - EkrManager.LastMeetingEndTime < 10f)
        {
            EkrManager.FlightLog($"launch drop slot={node.Slot} reason=post-meeting-10s");
            return;
        }

        Vector2? dir = ResolveLaunchDirection(node.LaunchDir, cno, ctx, state);

        if (dir == null)
        {
            EkrManager.FlightLog($"launch drop slot={node.Slot} reason=no-direction dir={node.LaunchDir} primed={state.MoveHistPrimed} last=({state.MoveHistLast.x:F2},{state.MoveHistLast.y:F2}) lastlast=({state.MoveHistLastLast.x:F2},{state.MoveHistLastLast.y:F2})");
            return;
        }

        // 契約 §1.2: 同時飛行 EKR 全体 ≤2・per-holder ≤1 (母数は飛行台帳 — AllObjects は数えない)。
        if (!EkrManager.CanStartFlight(ctx.HolderId))
        {
            EkrManager.FlightLog($"launch drop slot={node.Slot} reason=concurrent-cap");
            return;
        }

        // 全体 ≤2/秒。他の drop 理由を全て通過した「本当に飛ばす」直前でだけ消費する (CnoSpawn の
        // cross-holder 予算と同じ位置取り)。
        if (!EkrManager.TryConsumeGlobalLaunchBudget())
        {
            EkrManager.FlightLog($"launch drop slot={node.Slot} reason=global-rate");
            return;
        }

        EkrManager.FlightLog($"launch ok slot={node.Slot} dir={node.LaunchDir}=({dir.Value.x:F2},{dir.Value.y:F2}) speed={node.LaunchSpeed} cnoPos=({cno.Position.x:F2},{cno.Position.y:F2}) instantiated={cno.IsInstantiated}");

        PlayerControl holderPc = ctx.HolderId.GetPlayer();

        EkrManager.StartFlight(ctx.HolderId, cno, node.Slot, dir.Value, node.LaunchSpeed, holderAlive: holderPc && holderPc.IsAlive());

        state.LastCnoLaunchTime = now;
    }

    // 契約 §1.1: 向きは launch 時に1回だけ確定し、以後は追尾しない。解決できないものは無音 no-op。
    private static Vector2? ResolveLaunchDirection(string dir, EkrCno cno, EkrActionContext ctx, EkrHolderState state)
    {
        if (dir == "move")
        {
            // ホルダーの最後の移動方向 (フル 2D)。Snowdown.cs:392 の実績形をそのまま採る。
            // FlipX は見ない (ホスト非同期のため — WaveCannon の教訓) ので on_pet 発動と両立する。
            // 移動履歴ゼロ (ゲーム開始から不動) は方向が定まらないので no-op。
            if (!state.MoveHistPrimed) return null;

            Vector2 delta = state.MoveHistLast - state.MoveHistLastLast;
            return delta.sqrMagnitude < 0.0001f ? null : delta.normalized;
        }

        Vector2? destination = dir switch
        {
            // 契約 §1.1: ctx 無し文脈・死者は no-op。ResolveCtxPosition は死者も返すので、生存を見る
            // 統一セレクタ側 (ResolveSingle) を使う。
            "ctx" => ResolveSingle("ctx", ctx) is { } ctxPc ? ctxPc.Pos() : null,
            "marker1" => ResolveMarkerPosition(ctx, 1),
            "marker2" => ResolveMarkerPosition(ctx, 2),
            "marker3" => ResolveMarkerPosition(ctx, 3),
            "marker4" => ResolveMarkerPosition(ctx, 4),
            _ => null
        };

        if (destination == null) return null;

        // 弾の現在位置から見た向き。0.5u 未満は方向が定まらないので no-op (契約 §1.1)。
        Vector2 toTarget = destination.Value - cno.Position;
        return toTarget.magnitude < 0.5f ? null : toTarget.normalized;
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

        // 親は生存者からの借り物なので、未通報のまま会議へ入っても借りた人が「通報済み」にならないよう登録する
        Utils.RpcCreateDeadBody(pos.Value, colorId, parent,
            onCreated: body => ReportDeadBodyPatch.BorrowedParentBodyIds.Add(body.GetInstanceID()));
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

        // Wave 1: 対象は単数セレクタ (ctx/saved1/saved2/nearest/random)。pull はパーサが "ctx" 固定。
        PlayerControl targetPc = ResolveSingle(node.Subject ?? "ctx", ctx);
        if (!targetPc || !targetPc.IsAlive()) return; // ctx 無し/失効 saved/死者は no-op (spec §3)

        Vector2? dest = node.Target switch
        {
            "self" => ResolveSelfPosition(ctx),
            "marker1" => ResolveMarkerPosition(ctx, 1),
            "marker2" => ResolveMarkerPosition(ctx, 2),
            "marker3" => ResolveMarkerPosition(ctx, 3),
            "marker4" => ResolveMarkerPosition(ctx, 4),
            "cno1" => ResolveOwnCnoPosition(ctx, 0),
            "cno2" => ResolveOwnCnoPosition(ctx, 1),
            "cno3" => ResolveOwnCnoPosition(ctx, 2),
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

    // ── drag (v1.3 マクロ spec §3,§5) ────────────────────────────────────────────────────────
    // 実際の持続 tick は EkrManager の crowd-control エンジン (fiber 外・EKR 全体同時1本) が管理する。
    // ここでは開始条件 (ctx 生存・死者/ctx 無しは no-op) を確認して起動を委譲するだけ — 発火1回=1命令で
    // 即継続する (spec §3「fiber は発火1回=1命令で即継続」)。

    private static void Drag(EkrNode node, EkrActionContext ctx)
    {
        PlayerControl holderPc = ctx.HolderId.GetPlayer();
        if (!holderPc || !holderPc.IsAlive()) return;

        if (ctx.CtxId == byte.MaxValue) return; // ctx 無しは no-op (spec §3)
        PlayerControl targetPc = ctx.CtxId.GetPlayer();
        if (!targetPc || !targetPc.IsAlive()) return;

        EkrManager.TryStartDrag(ctx.HolderId, ctx.CtxId, node.Seconds); // 稼働中なら静かにドロップ (spec §5)
    }

    // ── field (v1.3 マクロ spec §3,§5) ───────────────────────────────────────────────────────
    // CNO 生成系の必須防御3点 (TryConsumeGlobalCnoSpawnBudget 課金・Execute 共通関所の会議/追放中 no-op・
    // pending は slot 保持のまま回収) を cno_spawn/portal_place と同じ順序で適用したうえで
    // EkrManager.TryStartField に委譲する。持続 tick 自体はエンジン側 (EkrManager) が管理する。

    private static void Field(EkrNode node, EkrActionContext ctx)
    {
        EkrHolderState state = EkrManager.GetHolderState(ctx.HolderId);
        if (state == null) return;

        float now = Time.realtimeSinceStartup;
        if (state.LastFieldPlaceTime >= 0f && now - state.LastFieldPlaceTime < 2f) return;

        // spec §5: 生成系5兄弟目 — 会議明けから10秒間はドロップ。
        if (EkrManager.LastMeetingEndTime >= 0f && now - EkrManager.LastMeetingEndTime < 10f) return;

        // 稼働中 (drag/field 同枠) なら、CNO 生成コストを払う前に諦める (spec §5「稼働中の新規起動は
        // 静かにドロップ」)。TryStartField 側にも同じガードがあるが、ここで先に弾くことで
        // 予算だけ無駄撃ちする事故を避ける (cno_spawn の cross-holder 予算消費と同じ順序原則)。
        if (EkrManager.IsCrowdControlActive) return;

        // 共有 SnapTo 残量が乏しい局面も同様に、CNO を実体化する前に諦める (TryStartField 側と同じ判定)。
        if (!EkrManager.SnapToBudgetAllowsCrowdControl()) return;

        Vector2? pos = node.Target switch
        {
            "self" => ResolveSelfPosition(ctx),
            "ctx" => ResolveCtxPosition(ctx),
            "marker1" => ResolveMarkerPosition(ctx, 1),
            "marker2" => ResolveMarkerPosition(ctx, 2),
            "marker3" => ResolveMarkerPosition(ctx, 3),
            "marker4" => ResolveMarkerPosition(ctx, 4),
            _ => null
        };

        if (pos == null) return; // no-op (spec §3: ctx 無し/未保存マーカーは予算不消費)

        if (!EkrManager.CanOccupyCnoSlot()) return; // 全体 ≤10体 (spec §5)

        EkrDefinition def = EkrManager.GetDefinition(ctx.Slot);
        if (def == null) return;

        // cross-holder 予算 (spec §5) — 「本当に spawn する」直前で消費 (cno_spawn と同じ順序)。
        if (!EkrManager.TryConsumeGlobalCnoSpawnBudget()) return;

        float radius = node.RadiusTier switch { "small" => 3f, "large" => 7f, _ => 5f };
        float pullDistance = node.StrengthTier switch { "weak" => 1.75f, "strong" => 3.5f, _ => 2.5f };

        var fieldCno = new EkrFieldCno(pos.Value, def.Color);

        if (!EkrManager.TryStartField(ctx.HolderId, fieldCno, radius, pullDistance, node.Seconds))
        {
            // 稼働中への競合 (単一スレッド実行では通常到達しない防御的経路) — 孤児コルーチン防止裁定に
            // 従って回収する (spec §5: 実体化前は遅延 Despawn・実体化済みなら即 Despawn)。
            EkrManager.RetryDespawnOrphanFieldCno(fieldCno);
            return;
        }

        state.LastFieldPlaceTime = now;
    }

    // ── Wave 2 (docs/ekn-wave2-contract.md §2): しらべる系 ──────────────────────────────────────

    // inspect: notify と同一チャネル・同一バケットを共有 (spec §2.1 — 新バケットを作らない)。
    private static void Inspect(EkrNode node, EkrActionContext ctx, EkrFiber fiber)
    {
        EkrHolderState state = EkrManager.GetHolderState(ctx.HolderId);
        if (state == null) return;

        PlayerControl holderPc = ctx.HolderId.GetPlayer();
        if (!holderPc) return;

        PlayerControl targetPc = ResolveSingle(node.Target, ctx);
        if (!targetPc || !targetPc.IsAlive()) return; // 壊れた参照/死者は no-op (予算不消費・spec §2.1)

        bool meeting = GameStates.IsMeeting;
        float now = Time.realtimeSinceStartup;

        // notify と同一バケットを共有 (per-holder ≤1/秒・会議中は ≤1/5秒)。
        Dictionary<byte, float> bucket = meeting ? state.LastMeetingNotifyTime : state.LastNotifyTime;
        float interval = meeting ? 5f : 1f;

        if (bucket.TryGetValue(ctx.HolderId, out float last) && now - last < interval) return;
        bucket[ctx.HolderId] = now;

        string text = BuildInspectText(node, targetPc);

        if (meeting) Utils.SendMessage(text, ctx.HolderId);
        else holderPc.Notify(text, 5f);
    }

    // 嘘2型の合成順序 (spec §2.1): まず failChance で「見せる答え」を決め (Oracle.cs:91 型)、次に noise 件の
    // ダミーを混ぜてリストにする (FortuneTeller.cs:89 型 — 真値の挿入位置は無作為)。
    private static string BuildInspectText(EkrNode node, PlayerControl targetPc)
    {
        if (node.Depth == "team")
        {
            Team team = targetPc.GetTeam();

            if (node.FailChance > 0 && IRandom.Instance.Next(100) < node.FailChance)
            {
                Team[] decoys = Main.TeamValues.Where(t => t != team && t != Team.None).ToArray();
                if (decoys.Length > 0) team = decoys[IRandom.Instance.Next(0, decoys.Length)];
            }

            return string.Format(Translator.GetString("EkrInspectResult"), targetPc.GetRealName(), Translator.GetString($"Team{team}"));
        }

        CustomRoles trueRole = targetPc.GetCustomRole();
        CustomRoles shown = trueRole;

        if (node.FailChance > 0 && IRandom.Instance.Next(100) < node.FailChance)
            shown = PickRandomOtherRole(trueRole);

        if (node.Noise <= 0)
            return string.Format(Translator.GetString("EkrInspectResult"), targetPc.GetRealName(), shown.ToColoredString());

        var list = new List<CustomRoles> { shown };

        while (list.Count < node.Noise + 1)
        {
            CustomRoles candidate = PickRandomOtherRole(trueRole);
            if (!list.Contains(candidate)) list.Add(candidate);
        }

        // 真値 (shown) の挿入位置を無作為に (FortuneTeller.cs:89 型)。
        list.Remove(shown);
        list.Insert(IRandom.Instance.Next(0, list.Count + 1), shown);

        string joined = string.Join(", ", list.Select(x => x.ToColoredString()));
        return string.Format(Translator.GetString("EkrInspectResultNoise"), targetPc.GetRealName(), joined);
    }

    private static CustomRoles PickRandomOtherRole(CustomRoles exclude)
    {
        CustomRoles[] pool = Main.CustomRoleValues.Where(x => x != exclude && !x.IsVanilla() && !x.IsAdditionRole() && x is not CustomRoles.GM and not CustomRoles.NotAssigned).ToArray();
        return pool.Length == 0 ? exclude : pool[IRandom.Instance.Next(0, pool.Length)];
    }

    // reveal (spec §2.2): アンカーは EkmTemplateRole.KnowRole 1点のみ (集約側が4表示系を拾う)。ここは
    // per-holder の Revealed 集合へ登録するだけ。
    private static void Reveal(EkrNode node, EkrActionContext ctx)
    {
        EkrHolderState state = EkrManager.GetHolderState(ctx.HolderId);
        if (state == null) return;

        PlayerControl targetPc = ResolveSingle(node.Target, ctx);
        if (!targetPc) return;

        EkrManager.Reveal(ctx.HolderId, targetPc.PlayerId);

        float now = Time.realtimeSinceStartup;
        if (state.LastNotifyTime.TryGetValue(ctx.HolderId, out float last) && now - last < 1f) return; // ≤1/秒/ホルダー (spec §2.2)
        state.LastNotifyTime[ctx.HolderId] = now;

        // NotifyRoles 送信を伴う (spec §2.2) — 表示反映は次の自然な更新に相乗り (実装裁量)。
        // advisor 指摘 (2026-08-11): reveal は会議中も有効な白名単 op だが、NotifyRoles の明示送信は
        // タスク中限定にする (会議中は「次の自然な更新」= 会議明けの通常リフレッシュに任せ、write-barrier/
        // 追放スイープと重なる窓を作らない — memory 罠7の型)。
        if (GameStates.IsInTask) Utils.NotifyRoles(SpecifySeer: ctx.HolderId.GetPlayer(), ForceLoop: false);
    }

    // ── Wave 2 (docs/ekn-wave2-contract.md §2.3): 矢印3 op ──────────────────────────────────────
    // 予算: arrow_show+arrow_mark 合算 ≤1/秒/ホルダー + 同時 ≤4本/ホルダー (両種合算)。
    // タスクフェーズ限定 (基盤の IsInTask ガードどおり) — 会議中は Execute() 側の共通関所で既に no-op。

    private static void ArrowShow(EkrNode node, EkrActionContext ctx)
    {
        EkrHolderState state = EkrManager.GetHolderState(ctx.HolderId);
        if (state == null) return;
        if (!GameStates.IsInTask) return;

        PlayerControl targetPc = ResolveSingle(node.Target, ctx);
        if (!targetPc || !targetPc.IsAlive()) return;

        bool isNew = !state.ArrowTargetExpiry.ContainsKey(targetPc.PlayerId);

        if (isNew && EkrManager.CountActiveArrows(state) >= 4) return; // 同時 ≤4本 (再発行はカウント不変)

        float now = Time.realtimeSinceStartup;
        if (state.LastArrowTime >= 0f && now - state.LastArrowTime < 1f) return; // 合算 ≤1/秒/ホルダー
        state.LastArrowTime = now;

        EkrManager.RegisterArrowTarget(state, targetPc.PlayerId, node.Seconds);
        TargetArrow.Add(ctx.HolderId, targetPc.PlayerId);
    }

    private static void ArrowMark(EkrNode node, EkrActionContext ctx)
    {
        EkrHolderState state = EkrManager.GetHolderState(ctx.HolderId);
        if (state == null) return;
        if (!GameStates.IsInTask) return;

        Vector2? pos = node.Target switch
        {
            "ctx" => ResolveCtxPosition(ctx), // spec §2.3: ctx の現在位置は死者でも可 (追尾はしない)
            "marker1" => ResolveMarkerPosition(ctx, 1),
            "marker2" => ResolveMarkerPosition(ctx, 2),
            "marker3" => ResolveMarkerPosition(ctx, 3),
            "marker4" => ResolveMarkerPosition(ctx, 4),
            "cno1" => ResolveOwnCnoPosition(ctx, 0),
            "cno2" => ResolveOwnCnoPosition(ctx, 1),
            "cno3" => ResolveOwnCnoPosition(ctx, 2),
            _ => null
        };

        if (pos == null) return;

        bool wasAtCap = EkrManager.CountActiveArrows(state) >= 4; // 登録前の時点で既に4本埋まっていたか

        float now = Time.realtimeSinceStartup;
        if (state.LastArrowTime >= 0f && now - state.LastArrowTime < 1f) return; // 合算 ≤1/秒/ホルダー

        bool isNew = EkrManager.RegisterArrowMark(state, pos.Value, node.Seconds);
        if (isNew && wasAtCap) // 新規かつ既に4本埋まっていた場合はドロップ (登録を取り消す)
        {
            state.ArrowMarks.RemoveAll(x => x.Pos == (Vector3)pos.Value);
            return;
        }

        state.LastArrowTime = now;
        LocateArrow.Add(ctx.HolderId, pos.Value);
    }

    private static void ArrowHide(EkrActionContext ctx)
    {
        EkrHolderState state = EkrManager.GetHolderState(ctx.HolderId);
        if (state == null) return;

        EkrManager.HideArrows(state, ctx.HolderId);
    }

    // ── Wave 2 (docs/ekn-wave2-contract.md §3): 票操作 ─────────────────────────────────────────

    private static void CancelVote(EkrActionContext ctx)
    {
        if (ctx.AllowCancelVote) ctx.CancelVote = true;
    }

    private static void VoteWeightSet(EkrNode node, EkrActionContext ctx)
    {
        EkrManager.SetVoteWeightOverride(ctx.HolderId, node.IntArg);
    }

    private static void VoteBlock(EkrNode node, EkrActionContext ctx)
    {
        PlayerControl targetPc = ResolveSingle(node.Target, ctx);
        if (!targetPc) return;

        EkrManager.TryVoteBlock(ctx.HolderId, targetPc.PlayerId);
    }

    private static void VoteSwap(EkrActionContext ctx)
    {
        EkrHolderState state = EkrManager.GetHolderState(ctx.HolderId);
        if (state == null) return;

        // spec §3.3: saved1 ↔ saved2 固定 (remember との合成を強制する設計)。失効判定 (死亡/切断/未保存) は
        // ResolveSaved が既に行う — 片方でも欠けたら予約しない (集計側で改めて再検証もするが、ここでも
        // 「壊れた参照は静かに no-op」の総則に従って早期に諦める)。
        PlayerControl p1 = ResolveSaved(ctx, 0);
        PlayerControl p2 = ResolveSaved(ctx, 1);
        if (!p1 || !p2) return;

        EkrManager.TryReserveVoteSwap(ctx.HolderId, p1.PlayerId, p2.PlayerId);
    }

    // ── Wave 4 (docs/ekn-wave4-contract.md §3): link / unlink (予算なし・ローカル状態のみ・会議中も有効) ──

    private static void Link(EkrNode node, EkrActionContext ctx)
    {
        EkrHolderState state = EkrManager.GetHolderState(ctx.HolderId);
        if (state == null) return;

        PlayerControl targetPc = ResolveSingle(node.Target, ctx);
        if (!targetPc || !targetPc.IsAlive()) return; // ctx 無し/失効 saved/死者は no-op (§3.1)
        if (targetPc.PlayerId == ctx.HolderId) return; // セレクタ解決が自分に落ちた場合も no-op (§3.1 self 不可の実行時側)

        EkrManager.Link(state, targetPc.PlayerId);
    }

    private static void Unlink(EkrActionContext ctx)
    {
        EkrHolderState state = EkrManager.GetHolderState(ctx.HolderId);
        if (state == null) return;

        EkrManager.Unlink(state); // リンクが無ければ実質 no-op (§3.2)
    }

    // ── Wave 4 (契約 §4): recruit — 相手を「自分と同じ EKR 役職」へ変換 ──────────────────────
    // レート/no-op 条件/2呼び固定順は EkrManager.TryRecruit が正典。ここは解決と holder 実在の確認だけ。

    private static void Recruit(EkrNode node, EkrActionContext ctx)
    {
        EkrHolderState state = EkrManager.GetHolderState(ctx.HolderId);
        if (state == null) return;

        PlayerControl targetPc = ResolveSingle(node.Target, ctx);
        if (!targetPc) return; // 壊れた参照は静かに no-op・予算不消費 (§4)

        // Wave 5 (契約 §2): node.IntArg = 変換先スロット番号 (0 = 省略 = 自分と同じ役職)。
        EkrManager.TryRecruit(state, ctx.HolderId, targetPc, node.IntArg);
    }

    // ── Wave 5 (docs/ekn-wave5-contract.md §1): effect_give — 相手に持続効果をかける ────────────
    // 意味論・チャンネル・復元は EkrManager の持続効果エンジンが正典。ここは解決と予算だけ。

    private static void EffectGive(EkrNode node, EkrActionContext ctx)
    {
        EkrHolderState state = EkrManager.GetHolderState(ctx.HolderId);
        if (state == null) return;

        PlayerControl targetPc = ResolveSingle(node.Target, ctx);

        // 壊れた参照 (ctx 無し / 失効 saved・linked)・死者・切断は静かに no-op・予算不消費 (§1.3/§1.4)。
        // ⚠ 判定は EkrManager.ApplyEffect 側と**同じ厳しさ**にすること — 緩いと予算だけ消費して
        // 向こうで無音 no-op になり、「no-op は予算不消費」の契約が破れる (完成前監査指摘)。
        if (!targetPc || !targetPc.IsAlive() || targetPc.Data == null || targetPc.Data.Disconnected) return;

        // ⚠ ホルダー生存ガードは意図的に付けない — §1.3 は on_death 起点 fiber からの実行を許す
        // (「死んだら爆発で周囲を凍らせる」型)。死んだ本人への適用は上の死者 no-op で自然に落ちる。

        if (!EkrManager.TryConsumeEffectBudget(state)) return; // 超過は静かにドロップ (§1.4)

        EkrManager.ApplyEffect(targetPc.PlayerId, node.EffectKind, node.Seconds, ctx.HolderId);
    }

    private static void Exile(EkrNode node, EkrActionContext ctx)
    {
        // advisor 指摘 (2026-08-11): 共通の meetingOrExile ゲートは Results/Proceeding/追放演出中も通す
        // (vote_block/vote_swap はそこへ着地しても無害な no-op だが、exile は RpcVotingComplete を二重送信
        // しうる)。GuessManager.GuesserMsg (GuessManager.cs:67) と同じ投票フェーズ判定で narrow に絞る。
        if (!MeetingHud.Instance || ExileController.Instance || MeetingHud.Instance.state is MeetingHud.MeetingStates.Results or MeetingHud.MeetingStates.Proceeding) return;

        PlayerControl targetPc = ResolveSingle(node.Target, ctx);
        if (!targetPc || !targetPc.IsAlive()) return;

        if (!EkrManager.TryConsumeExile()) return; // 構造的 1/会議

        PlayerControl holderPc = ctx.HolderId.GetPlayer();
        Patches.CheckForEndVotingPatch.ForceExile(targetPc, holderPc);
    }
}
