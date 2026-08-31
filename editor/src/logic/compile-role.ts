// Blockly ワークスペース (serialization JSON) → 役職ロジック AST (spec: docs/ekr-logic-spec.md)。
// 「コード生成なし・JSON データのみ」(spec §7) — Blockly のコードジェネレータ機構は一切使わない。
//
// 意図的に blockly を実行時にも型としても import しない (compile-role.ts は DOM 非依存の純関数層 —
// vitest は environment:"node" で回るため、Blockly ランタイムへの依存はテストを壊す)。入力は
// `Blockly.serialization.workspaces.save(workspace)` が返す**プレーンな JSON** の形を、この
// ファイル内でローカルに宣言した最小限の構造 (SerializedBlock/SerializedWorkspace) として
// 受け取る。呼び出し元 (role-maker.ts) だけが実際の Blockly モジュールを知っていればよい。
//
// このモジュールの出力は「検証前の生データ」(型は緩め・unknown 混じり) であり、契約の強制
// (レンジ・上限・未定義変数参照など) は一切行わない — 唯一の検証実装は roledef.ts の
// validateRoleLogic() であり、ここで作った出力は必ずそこを通してから使うこと (DRY: 検証ロジックを
// 二重実装しない)。ブロックの入力が未接続などで構造が壊れていても、ここでは例外を投げず
// `undefined` 等を素通しするだけに留める — validateRoleLogic 側が「型が違う/欠落している」として
// 分かりやすい日本語エラーに変換してくれる。
//
// ブロック type 命名規約 (blocks-role.ts と1対1で対応させること):
//   ekr_when_<when-id>          … イベントハット (24種 — 正典は roledef.ts の LOGIC_WHEN_VALUES。
//                                  id は spec §2 の when 値そのもの)
//   ekr_if / ekr_if_else        … 制御構文 if (else 無し/else 付きの2ブロックに分離、mutator 不使用)
//   ekr_do_<op>                 … その他の制御/アクション opcode (spec §3)
//   math_number / logic_boolean … Blockly 標準ブロックをそのまま「lit」式として再利用
//   ekr_expr_var                … 変数参照 (自前のドロップダウン。Blockly 標準の変数機構は使わない)
//   ekr_expr_arith/compare/logic/not/rand … 演算子式 (spec §4 の kind をまとめたブロック群)

import type { LogicVariable } from "../roledef";

export interface SerializedBlock {
    type: string;
    id?: string;
    fields?: Record<string, unknown>;
    inputs?: Record<string, { block?: SerializedBlock; shadow?: SerializedBlock }>;
    next?: { block?: SerializedBlock };
}

export interface SerializedWorkspace {
    blocks?: {
        languageVersion?: number;
        blocks?: SerializedBlock[];
    };
}

const WHEN_BLOCK_PREFIX = "ekr_when_";
const DO_BLOCK_PREFIX = "ekr_do_";

/** value input (デフォルト値を提供する shadow ブロックも見る — 未接続=無いものとして undefined) */
function inputBlock(b: SerializedBlock, name: string): SerializedBlock | undefined {
    const inp = b.inputs?.[name];
    return inp?.block ?? inp?.shadow;
}

/** statement input (THEN/ELSE の C 型スロットの先頭ブロック) */
function statementBlock(b: SerializedBlock, name: string): SerializedBlock | undefined {
    return b.inputs?.[name]?.block;
}

/** Blockly の field_number は number、field_dropdown は string で保存されるため、
 *  どちらでも数値として読めるように寄せる (未接続/欠落は NaN → 後段の validateRoleLogic が
 *  「数値ではない」として reject してくれる)。 */
function toNum(raw: unknown): number {
    if (typeof raw === "number") return raw;
    if (typeof raw === "string" && raw.trim() !== "") return Number(raw);
    return NaN;
}

function blockToExpr(b: SerializedBlock | undefined): unknown {
    if (!b) return undefined;
    switch (b.type) {
        case "math_number":
            return { e: "lit", v: toNum(b.fields?.NUM) };
        case "logic_boolean":
            return { e: "lit", v: b.fields?.BOOL === "TRUE" ? 1 : 0 };
        case "ekr_expr_var":
            return { e: "var", name: b.fields?.VAR };
        case "ekr_expr_arith":
        case "ekr_expr_compare":
        case "ekr_expr_logic":
            return {
                e: "op",
                kind: b.fields?.OP,
                a: blockToExpr(inputBlock(b, "A")),
                b: blockToExpr(inputBlock(b, "B")),
            };
        case "ekr_expr_not":
            return { e: "op", kind: "not", a: blockToExpr(inputBlock(b, "A")) };
        case "ekr_expr_rand":
            return { e: "op", kind: "rand", a: blockToExpr(inputBlock(b, "A")), b: blockToExpr(inputBlock(b, "B")) };
        default:
            // 未知のブロック型 → そのまま「不明な e」として通す。validateRoleLogic が
            // 「logic.rules[i].do[j]... の e の種類が不明です (型名)」と分かりやすく reject する。
            return { e: b.type };
    }
}

function blockToNode(b: SerializedBlock): Record<string, unknown> {
    switch (b.type) {
        case "ekr_if":
            return { op: "if", cond: blockToExpr(inputBlock(b, "COND")), then: chainToNodes(statementBlock(b, "THEN")) };
        case "ekr_if_else":
            return {
                op: "if",
                cond: blockToExpr(inputBlock(b, "COND")),
                then: chainToNodes(statementBlock(b, "THEN")),
                else: chainToNodes(statementBlock(b, "ELSE")),
            };
        case "ekr_do_var_set":
            return { op: "var_set", name: b.fields?.VAR, value: blockToExpr(inputBlock(b, "VALUE")) };
        case "ekr_do_var_add":
            return { op: "var_add", name: b.fields?.VAR, delta: blockToExpr(inputBlock(b, "VALUE")) };
        case "ekr_do_wait":
            return { op: "wait", seconds: toNum(b.fields?.SECONDS) };
        case "ekr_do_stop":
            return { op: "stop" };
        case "ekr_do_notify": {
            // Wave 1 (spec §3): target は任意・既定 self。"self" と欠落 (Wave 1 より前に保存された
            // ワークスペース) はどちらもキーを付けない — AST を最小に保ち、既存コードの再書き出しが
            // バイト単位で不動点のままになるようにする。
            const target = b.fields?.TARGET;
            const node: Record<string, unknown> = { op: "notify", text: b.fields?.TEXT, seconds: toNum(b.fields?.SECONDS) };
            if (target !== undefined && target !== "self") node.target = target;
            return node;
        }
        case "ekr_do_teleport":
            return { op: "teleport", to: b.fields?.TO };
        case "ekr_do_kill":
            return { op: "kill", target: b.fields?.TARGET };
        case "ekr_do_set_kill_cooldown":
            return { op: "set_kill_cooldown", seconds: toNum(b.fields?.SECONDS) };
        case "ekr_do_speed":
            return { op: "speed", mult: toNum(b.fields?.MULT), seconds: toNum(b.fields?.SECONDS) };
        case "ekr_do_cno_spawn":
            return { op: "cno_spawn", slot: toNum(b.fields?.SLOT), text: b.fields?.TEXT, size: toNum(b.fields?.SIZE), at: b.fields?.AT };
        case "ekr_do_cno_move":
            return { op: "cno_move", slot: toNum(b.fields?.SLOT), dx: toNum(b.fields?.DX), dy: toNum(b.fields?.DY) };
        case "ekr_do_cno_despawn":
            return { op: "cno_despawn", slot: toNum(b.fields?.SLOT) };
        case "ekr_do_cno_show":
            return { op: "cno_show", slot: toNum(b.fields?.SLOT), who: b.fields?.WHO };
        // Wave 6 (docs/ekn-wave6-contract.md §1) — とばす。SPEED が既定 "medium" のときはフィールドごと
        // 省略する (on_near.WHO の "anyone" 省略と同じ作法 — recruit.slot と同種の畳み込み)。
        case "ekr_do_cno_launch": {
            const slot = toNum(b.fields?.SLOT);
            const dir = b.fields?.DIR;
            const speed = b.fields?.SPEED;
            const node: Record<string, unknown> = { op: "cno_launch", slot, dir };
            if (typeof speed === "string" && speed !== "" && speed !== "medium") node.speed = speed;
            return node;
        }
        case "ekr_do_dummy_spawn":
            // KILLABLE は field_dropdown なので Blockly 上は "1"/"0" の文字列 (options のキー側の
            // 値そのもの) — roledef.ts の dummy_spawn.killable は真の boolean のみ受理するため
            // ここで変換する。フィールドが本当に欠落している (undefined) 場合はここで undefined を
            // 通し、validateRoleLogic 側の「boolean である必要があります」reject に委ねる —
            // `=== "1"` 一発判定にすると欠落が黙って false (=こわせない) に化けてしまい、他の
            // フィールド (SLOT/AT 等、欠落がそのまま validateRoleLogic の型エラーになる) と
            // 挙動が揃わなくなる。
            return {
                op: "dummy_spawn",
                slot: toNum(b.fields?.SLOT),
                name: b.fields?.NAME,
                killable: b.fields?.KILLABLE === undefined ? undefined : b.fields.KILLABLE === "1",
                at: b.fields?.AT,
            };
        case "ekr_do_corpse_spawn":
            return { op: "corpse_spawn", color: b.fields?.COLOR, at: b.fields?.AT };
        case "ekr_do_marker_save":
            return { op: "marker_save", slot: toNum(b.fields?.SLOT), at: b.fields?.AT };
        case "ekr_do_teleport_other":
            // TARGET は Wave 1 で追加されたフィールド。`?? "ctx"` は「Wave 1 より前に保存された
            // ワークスペースの移行既定値」であって、フィールド欠落一般に既定値を当てる方針では
            // ない (dummy_spawn.KILLABLE のコメント参照 — あちらは欠落を素通しして reject させる)。
            // 旧ブロックは target が "ctx" 固定だったので、旧データの意味をそのまま復元している。
            return { op: "teleport_other", target: b.fields?.TARGET ?? "ctx", to: b.fields?.TO };
        case "ekr_do_portal_place":
            return { op: "portal_place", which: b.fields?.WHICH };
        // Wave 1 (spec §3 2026-08-11) — おぼえる / こうげきをふせぐ
        case "ekr_do_remember":
            return { op: "remember", slot: toNum(b.fields?.SLOT), target: b.fields?.TARGET };
        case "ekr_do_cancel_attack":
            // 配置 (on_attacked 配下かどうか) の検査は roledef.ts の validateRoleLogic が行う —
            // compile 側は契約を一切強制しない (ファイル冒頭の方針)。
            return { op: "cancel_attack" };
        // Wave 2 (docs/ekn-wave2-contract.md §2.1 2026-08-11 追記) — しらべる系。failChance/noise は
        // 「任意・既定0」(spec 表) なので notify.target と同じ既定値省略の作法に倣い、値が既定 (0)
        // なら AST にキーを足さない。noise はさらに depth:"team" のとき常に省略する — 契約は
        // 「noise は depth:"role" のみ受理 (team との併用は文書 reject)」だが、ドロップダウンを
        // "team" に切り替えても NUMBER フィールドの残留値は消えない (Blockly の一般挙動) ため、
        // 省略しないと切替前の値が意図せず出力されてしまう。roledef.ts の validateNode は
        // noise=0 + depth:"team" を許容する側に倒しているが (noise>0 のときだけ reject)、
        // compile 側はそもそもこの組み合わせを一切生成しない — どちらの厳格さで読んでも
        // エディタ生成コードは通る (解釈の余地があることは spec 併合時に確認すること)。
        case "ekr_do_inspect": {
            const target = b.fields?.TARGET;
            const depth = b.fields?.DEPTH;
            const node: Record<string, unknown> = { op: "inspect", target, depth };
            const failChance = toNum(b.fields?.FAILCHANCE);
            if (failChance !== 0) node.failChance = failChance;
            if (depth === "role") {
                const noise = toNum(b.fields?.NOISE);
                if (noise !== 0) node.noise = noise;
            }
            return node;
        }
        case "ekr_do_reveal":
            return { op: "reveal", target: b.fields?.TARGET };
        case "ekr_do_arrow_show":
            return { op: "arrow_show", target: b.fields?.TARGET, seconds: toNum(b.fields?.SECONDS) };
        case "ekr_do_arrow_mark":
            return { op: "arrow_mark", at: b.fields?.AT, seconds: toNum(b.fields?.SECONDS) };
        case "ekr_do_arrow_hide":
            return { op: "arrow_hide" };
        // Wave 2 — とうひょう
        case "ekr_do_cancel_vote":
            // 配置 (on_meeting_vote 配下かどうか) の検査は roledef.ts の validateRoleLogic が行う。
            return { op: "cancel_vote" };
        case "ekr_do_vote_weight_set":
            return { op: "vote_weight_set", value: toNum(b.fields?.VALUE) };
        case "ekr_do_vote_block":
            return { op: "vote_block", target: b.fields?.TARGET };
        case "ekr_do_vote_swap":
            return { op: "vote_swap" };
        case "ekr_do_exile":
            return { op: "exile", target: b.fields?.TARGET };
        // Wave 7 (docs/ekn-wave7-contract.md §1,§2) — 勝利条件。target はドロップダウン値をそのまま
        // 渡す (明示 "self" も validate 層は受理する — notify と同じ「畳まない」側)。
        case "ekr_do_win":
            return { op: "win", target: b.fields?.TARGET };
        case "ekr_do_win_join":
            return { op: "win_join", target: b.fields?.TARGET };
        // Wave 4 (docs/ekn-wave4-contract.md §3/§4) — つなぐ (link/unlink/recruit)
        case "ekr_do_link":
            return { op: "link", target: b.fields?.TARGET };
        case "ekr_do_unlink":
            return { op: "unlink" };
        case "ekr_do_recruit": {
            // Wave 5 (docs/ekn-wave5-contract.md §2): SLOT が既定の "" (じぶんとおなじ) のときは
            // フィールドごと省略する (正準形を最小に保つ — on_near.who の "anyone" 省略と同じ作法)。
            const slot = b.fields?.SLOT;
            if (typeof slot === "string" && slot !== "") return { op: "recruit", target: b.fields?.TARGET, slot: toNum(slot) };
            if (typeof slot === "number") return { op: "recruit", target: b.fields?.TARGET, slot };
            return { op: "recruit", target: b.fields?.TARGET };
        }
        // Wave 5 (docs/ekn-wave5-contract.md §1) — こうかをかける
        case "ekr_do_effect_give":
            return { op: "effect_give", target: b.fields?.TARGET, kind: b.fields?.KIND, seconds: toNum(b.fields?.SECONDS) };
        // v1.3 (spec §3 2026-08-11 追記) — ひっぱる・ひきずる・フィールド
        case "ekr_do_pull":
            return { op: "pull" };
        case "ekr_do_drag":
            return { op: "drag", seconds: toNum(b.fields?.SECONDS) };
        case "ekr_do_field":
            return {
                op: "field",
                at: b.fields?.AT,
                radius: b.fields?.RADIUS,
                strength: b.fields?.STRENGTH,
                seconds: toNum(b.fields?.SECONDS),
            };
        default:
            if (b.type.startsWith(DO_BLOCK_PREFIX)) return { op: b.type.slice(DO_BLOCK_PREFIX.length) };
            // 未知のブロック型 → そのまま「不明な op」として通す (blockToExpr と同じ方針)。
            return { op: b.type };
    }
}

function chainToNodes(first: SerializedBlock | undefined): unknown[] {
    const out: unknown[] = [];
    let cur = first;
    while (cur) {
        out.push(blockToNode(cur));
        cur = cur.next?.block;
    }
    return out;
}

/**
 * ワークスペースのトップレベルブロックから rule ([{when, do}]) を組み立てる。イベントハット
 * (`ekr_when_*`) 以外の孤立したトップレベルブロック (未接続の切れ端) は無視する — rule に
 * 属さない断片をエラーにはしない (Blockly 上で「まだどこにも繋いでいない部品」を置いておくのは
 * 普通の編集途中の状態のため)。
 */
export function compileTopBlocksToRules(topBlocks: SerializedBlock[]): unknown[] {
    const rules: unknown[] = [];
    for (const b of topBlocks) {
        if (b.type.startsWith(WHEN_BLOCK_PREFIX)) {
            const when = b.type.slice(WHEN_BLOCK_PREFIX.length);
            const rule: Record<string, unknown> = { when, do: chainToNodes(b.next?.block) };
            // v1.2 (spec §2): on_cno_touch は必須の slot フィールド (ekr_when_on_cno_touch の
            // 動的 SLOT ドロップダウン) を持つ唯一のイベント。他の when には付けない。
            if (when === "on_cno_touch") rule.slot = toNum(b.fields?.SLOT);

            // R2 (docs/ekn-r2-contract.md §3b): on_attacked の kind / on_death の cause。
            // ドロップダウンの「すべて」= 空文字 は **フィールドごと省略** する (= 全種にマッチ)。
            if (when === "on_attacked") {
                const kind = b.fields?.KIND;
                if (typeof kind === "string" && kind !== "") rule.kind = kind;
            }
            if (when === "on_death") {
                const cause = b.fields?.CAUSE;
                if (typeof cause === "string" && cause !== "") rule.cause = cause;
            }

            // Wave 3 (docs/ekn-wave3-contract.md §1.2/§1.3 2026-08-14): on_var は変数名/比較/値の
            // 3フィールドとも必須。on_alive_count は比較/値のみ (var は持たない)。欠落しているフィールドは
            // そのまま undefined を通し (欠落を既定値に化けさせない — dummy_spawn.KILLABLE のコメントと
            // 同じ方針)、validateRoleLogic 側の「必須です」reject に委ねる。
            if (when === "on_var") {
                rule.var = b.fields?.VAR;
                rule.cmp = b.fields?.CMP;
                rule.value = toNum(b.fields?.VALUE);
            }
            if (when === "on_alive_count") {
                rule.cmp = b.fields?.CMP;
                rule.value = toNum(b.fields?.VALUE);
            }

            // Wave 4 (docs/ekn-wave4-contract.md §1.2/§1.3/§3.3): on_near は radius 必須 + who 任意 —
            // WHO が既定の "anyone" のときはフィールドごと省略する (欠落 = anyone。正準形を最小に
            // 保つ notify.target の "self" 省略と同じ作法)。on_far は radius/who とも必須なので
            // 両方そのまま転記する。on_linked_death の CAUSE は on_death と同じ「すべて = 空文字は
            // フィールドごと省略」。
            if (when === "on_near") {
                rule.radius = b.fields?.RADIUS;
                const who = b.fields?.WHO;
                if (typeof who === "string" && who !== "" && who !== "anyone") rule.who = who;
            }
            if (when === "on_far") {
                rule.radius = b.fields?.RADIUS;
                rule.who = b.fields?.WHO;
            }
            if (when === "on_linked_death") {
                const cause = b.fields?.CAUSE;
                if (typeof cause === "string" && cause !== "") rule.cause = cause;
            }

            rules.push(rule);
        }
    }
    return rules;
}

/** 中身が空っぽのイベントハット (下に1つもブロックが繋がっていないもの)。 */
export interface EmptyWhenBlock {
    /** Blockly のブロック id (ワークスペース上で該当ブロックへジャンプするために使う) */
    id?: string;
    /** spec §2 の when 値 (`ekr_when_` を除いた部分) */
    when: string;
}

/**
 * 「置いただけで中身が空っぽのきっかけブロック」を全て列挙する。
 *
 * 空のハットは compileTopBlocksToRules が `{ when, do: [] }` として素直に積むため、
 * validateRoleLogic が `rules[i].do のノード数は 1〜64 個…(現在 0 個)` で reject する。検証は
 * 最初の1件で打ち切るので、この状態のまま他の場所へブロックを足してもエラー文が1文字も
 * 変わらず「ノード数がカウントされない」ように見えてしまう (2026-08-11 に判明)。
 * 呼び出し元 (role-maker.ts) はこれを使って、index ではなくきっかけ名で場所を伝える。
 */
export function findEmptyWhenBlocks(serialized: SerializedWorkspace): EmptyWhenBlock[] {
    const out: EmptyWhenBlock[] = [];
    for (const b of serialized.blocks?.blocks ?? []) {
        if (b.type.startsWith(WHEN_BLOCK_PREFIX) && !b.next?.block) {
            out.push({ id: b.id, when: b.type.slice(WHEN_BLOCK_PREFIX.length) });
        }
    }
    return out;
}

/** ワークスペースに rule が1つも無い (イベントハットが無い) かどうか。R0 互換出力判定に使う。 */
export function hasNoRules(serialized: SerializedWorkspace): boolean {
    const blocks = serialized.blocks?.blocks ?? [];
    return !blocks.some((b) => b.type.startsWith(WHEN_BLOCK_PREFIX));
}

/**
 * ワークスペース全体を「検証前の logic 入力」に変換する。そのまま roledef.ts の
 * validateRoleLogic() に渡すことを前提とした形 (unknown 混じり)。rules が空なら
 * (イベントハットが1つも無いなら) null を返す — 呼び出し元はこの場合 logic キー自体を
 * 省略して R0 互換のコードを出力すること (spec: 「logic 未使用なら従来どおり logic 無しコードを出力」)。
 */
export function compileWorkspaceToLogicInput(
    serialized: SerializedWorkspace,
    variables: LogicVariable[],
): { version: 1; variables: LogicVariable[]; rules: unknown[]; blockly: SerializedWorkspace } | null {
    const topBlocks = serialized.blocks?.blocks ?? [];
    const rules = compileTopBlocksToRules(topBlocks);
    if (rules.length === 0) return null;
    return { version: 1, variables, rules, blockly: serialized };
}
