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
//   ekr_when_<when-id>          … イベントハット (10種、id は spec §2 の when 値そのもの)
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
        case "ekr_do_notify":
            return { op: "notify", text: b.fields?.TEXT, seconds: toNum(b.fields?.SECONDS) };
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
            rules.push({ when: b.type.slice(WHEN_BLOCK_PREFIX.length), do: chainToNodes(b.next?.block) });
        }
    }
    return rules;
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
