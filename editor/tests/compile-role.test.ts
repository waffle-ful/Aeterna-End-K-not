// compile-role.ts (Blockly serialization JSON → logic AST 入力) のテスト。
// Blockly ランタイムは一切使わず、Blockly.serialization.workspaces.save() が返す形を模した
// 手組みの SerializedBlock フィクスチャで検証する (vitest は environment:"node" — DOM/Blockly 不可)。
// 検証ロジックの二重実装を避けるため、多くのテストは compile → roledef.validateRoleLogic まで
// 通して「実際に使える AST になっているか」を確認する (統合テスト寄り)。

import { describe, expect, it } from "vitest";
import {
    compileTopBlocksToRules,
    compileWorkspaceToLogicInput,
    hasNoRules,
    type SerializedBlock,
    type SerializedWorkspace,
} from "../src/logic/compile-role";
import { validateRoleLogic } from "../src/roledef";

function ws(blocks: SerializedBlock[]): SerializedWorkspace {
    return { blocks: { languageVersion: 0, blocks } };
}

describe("compile-role: ブロック直列 → do ノード配列", () => {
    it("イベントハット1個 + stop 1個 → rule 1個", () => {
        const blocks: SerializedBlock[] = [
            { type: "ekr_when_on_pet", next: { block: { type: "ekr_do_stop" } } },
        ];
        const rules = compileTopBlocksToRules(blocks);
        expect(rules).toEqual([{ when: "on_pet", do: [{ op: "stop" }] }]);
    });

    it("next チェーンで複数ブロックを1本の do 配列に連結する", () => {
        const blocks: SerializedBlock[] = [
            {
                type: "ekr_when_on_second",
                next: {
                    block: {
                        type: "ekr_do_wait",
                        fields: { SECONDS: 1 },
                        next: { block: { type: "ekr_do_stop" } },
                    },
                },
            },
        ];
        const rules = compileTopBlocksToRules(blocks);
        expect(rules).toEqual([{ when: "on_second", do: [{ op: "wait", seconds: 1 }, { op: "stop" }] }]);
    });

    it("複数のイベントハットはそれぞれ独立した rule になる (同じ when の重複も許容)", () => {
        const blocks: SerializedBlock[] = [
            { type: "ekr_when_on_pet", next: { block: { type: "ekr_do_stop" } } },
            { type: "ekr_when_on_pet", next: { block: { type: "ekr_do_stop" } } },
            { type: "ekr_when_on_death", next: { block: { type: "ekr_do_stop" } } },
        ];
        const rules = compileTopBlocksToRules(blocks);
        expect(rules).toHaveLength(3);
    });

    it("イベントハットに繋がっていない孤立ブロックは無視する (rule を作らない)", () => {
        const blocks: SerializedBlock[] = [
            { type: "ekr_do_stop" }, // どこにも繋がっていない断片
        ];
        expect(compileTopBlocksToRules(blocks)).toEqual([]);
    });

    it("空の do (next 無し) は空配列になる", () => {
        const blocks: SerializedBlock[] = [{ type: "ekr_when_on_pet" }];
        expect(compileTopBlocksToRules(blocks)).toEqual([{ when: "on_pet", do: [] }]);
    });
});

describe("compile-role: if / if_else", () => {
    it("ekr_if は else キーを一切出さない (省略時に else:[] を emit しない)", () => {
        const ifBlock: SerializedBlock = {
            type: "ekr_if",
            inputs: {
                COND: { block: { type: "logic_boolean", fields: { BOOL: "TRUE" } } },
                THEN: { block: { type: "ekr_do_stop" } },
            },
        };
        const rules = compileTopBlocksToRules([{ type: "ekr_when_on_pet", next: { block: ifBlock } }]);
        const node = (rules[0] as { do: unknown[] }).do[0] as Record<string, unknown>;
        expect(node.op).toBe("if");
        expect("else" in node).toBe(false);
        expect(node.then).toEqual([{ op: "stop" }]);
    });

    it("ekr_if_else は then/else 両方をコンパイルする", () => {
        const ifElse: SerializedBlock = {
            type: "ekr_if_else",
            inputs: {
                COND: { block: { type: "logic_boolean", fields: { BOOL: "FALSE" } } },
                THEN: { block: { type: "ekr_do_stop" } },
                ELSE: { block: { type: "ekr_do_wait", fields: { SECONDS: 2 } } },
            },
        };
        const rules = compileTopBlocksToRules([{ type: "ekr_when_on_pet", next: { block: ifElse } }]);
        expect(rules).toEqual([
            {
                when: "on_pet",
                do: [{ op: "if", cond: { e: "lit", v: 0 }, then: [{ op: "stop" }], else: [{ op: "wait", seconds: 2 }] }],
            },
        ]);
    });

    it("shadow ブロック (未接続時のデフォルト値) を block が無いときのフォールバックとして読む", () => {
        const ifBlock: SerializedBlock = {
            type: "ekr_if",
            inputs: {
                COND: { shadow: { type: "logic_boolean", fields: { BOOL: "TRUE" } } }, // block が無く shadow のみ
                THEN: { block: { type: "ekr_do_stop" } },
            },
        };
        const rules = compileTopBlocksToRules([{ type: "ekr_when_on_pet", next: { block: ifBlock } }]);
        const node = (rules[0] as { do: unknown[] }).do[0] as Record<string, unknown>;
        expect(node.cond).toEqual({ e: "lit", v: 1 });
    });
});

describe("compile-role: 式 (expr)", () => {
    it("math_number は lit 式になる", () => {
        const blocks = [{ type: "ekr_when_on_pet", next: { block: { type: "ekr_do_var_set", fields: { VAR: "x" }, inputs: { VALUE: { block: { type: "math_number", fields: { NUM: 42 } } } } } } }];
        const rules = compileTopBlocksToRules(blocks);
        expect(rules).toEqual([{ when: "on_pet", do: [{ op: "var_set", name: "x", value: { e: "lit", v: 42 } }] }]);
    });

    it("ekr_expr_var は変数参照式になる", () => {
        const blocks = [{ type: "ekr_when_on_pet", next: { block: { type: "ekr_do_var_add", fields: { VAR: "count" }, inputs: { VALUE: { block: { type: "ekr_expr_var", fields: { VAR: "other" } } } } } } }];
        const rules = compileTopBlocksToRules(blocks);
        expect(rules).toEqual([{ when: "on_pet", do: [{ op: "var_add", name: "count", delta: { e: "var", name: "other" } }] }]);
    });

    it("ekr_expr_arith/compare/logic は kind + a/b を持つ op 式になる", () => {
        function arithNode(kind: string): SerializedBlock {
            return {
                type: "ekr_do_var_set",
                fields: { VAR: "x" },
                inputs: {
                    VALUE: {
                        block: {
                            type: "ekr_expr_arith",
                            fields: { OP: kind },
                            inputs: {
                                A: { block: { type: "math_number", fields: { NUM: 1 } } },
                                B: { block: { type: "math_number", fields: { NUM: 2 } } },
                            },
                        },
                    },
                },
            };
        }
        const rules = compileTopBlocksToRules([{ type: "ekr_when_on_pet", next: { block: arithNode("add") } }]);
        expect(rules).toEqual([
            { when: "on_pet", do: [{ op: "var_set", name: "x", value: { e: "op", kind: "add", a: { e: "lit", v: 1 }, b: { e: "lit", v: 2 } } }] },
        ]);
    });

    it("ekr_expr_not は a のみで b を持たない", () => {
        const notBlock: SerializedBlock = {
            type: "ekr_expr_not",
            inputs: { A: { block: { type: "logic_boolean", fields: { BOOL: "TRUE" } } } },
        };
        const blocks = [{ type: "ekr_when_on_pet", next: { block: { type: "ekr_do_var_set", fields: { VAR: "x" }, inputs: { VALUE: { block: notBlock } } } } }];
        const rules = compileTopBlocksToRules(blocks);
        const value = (((rules[0] as { do: unknown[] }).do[0]) as Record<string, unknown>).value as Record<string, unknown>;
        expect(value).toEqual({ e: "op", kind: "not", a: { e: "lit", v: 1 } });
        expect("b" in value).toBe(false);
    });

    it("ekr_expr_rand は a/b を持つ op 式になる (kind=rand)", () => {
        const randBlock: SerializedBlock = {
            type: "ekr_expr_rand",
            inputs: {
                A: { block: { type: "math_number", fields: { NUM: 1 } } },
                B: { block: { type: "math_number", fields: { NUM: 6 } } },
            },
        };
        const blocks = [{ type: "ekr_when_on_pet", next: { block: { type: "ekr_do_var_set", fields: { VAR: "x" }, inputs: { VALUE: { block: randBlock } } } } }];
        const rules = compileTopBlocksToRules(blocks);
        const value = (((rules[0] as { do: unknown[] }).do[0]) as Record<string, unknown>).value;
        expect(value).toEqual({ e: "op", kind: "rand", a: { e: "lit", v: 1 }, b: { e: "lit", v: 6 } });
    });
});

describe("compile-role: 数値フィールドは文字列/数値どちらで来ても toNum で読める", () => {
    it("field_dropdown 由来の文字列値の SLOT も数値化される", () => {
        const blocks = [{ type: "ekr_when_on_pet", next: { block: { type: "ekr_do_cno_despawn", fields: { SLOT: "2" } } } }];
        const rules = compileTopBlocksToRules(blocks);
        expect(rules).toEqual([{ when: "on_pet", do: [{ op: "cno_despawn", slot: 2 }] }]);
    });

    it("field_number 由来の数値の SECONDS もそのまま読める", () => {
        const blocks = [{ type: "ekr_when_on_pet", next: { block: { type: "ekr_do_wait", fields: { SECONDS: 3.5 } } } }];
        const rules = compileTopBlocksToRules(blocks);
        expect(rules).toEqual([{ when: "on_pet", do: [{ op: "wait", seconds: 3.5 }] }]);
    });
});

describe("compile-role: CNO 系ブロックのフィールド分担", () => {
    it("cno_spawn は slot/text/size/at を持つ", () => {
        const blocks = [{ type: "ekr_when_on_pet", next: { block: { type: "ekr_do_cno_spawn", fields: { SLOT: 1, TEXT: "!", SIZE: 5, AT: "self" } } } }];
        expect(compileTopBlocksToRules(blocks)).toEqual([{ when: "on_pet", do: [{ op: "cno_spawn", slot: 1, text: "!", size: 5, at: "self" }] }]);
    });

    it("cno_move は slot/dx/dy を持つ", () => {
        const blocks = [{ type: "ekr_when_on_pet", next: { block: { type: "ekr_do_cno_move", fields: { SLOT: 1, DX: 10, DY: -5 } } } }];
        expect(compileTopBlocksToRules(blocks)).toEqual([{ when: "on_pet", do: [{ op: "cno_move", slot: 1, dx: 10, dy: -5 }] }]);
    });

    it("cno_show は slot/who を持つ", () => {
        const blocks = [{ type: "ekr_when_on_pet", next: { block: { type: "ekr_do_cno_show", fields: { SLOT: 1, WHO: "all" } } } }];
        expect(compileTopBlocksToRules(blocks)).toEqual([{ when: "on_pet", do: [{ op: "cno_show", slot: 1, who: "all" }] }]);
    });
});

// v1.1 (docs/ekr-logic-spec.md §3 2026-08-09 追記)
describe("compile-role: dummy_spawn / corpse_spawn ブロックのフィールド分担 (v1.1)", () => {
    it("dummy_spawn は slot/name/at/killable を持つ (KILLABLE は \"1\"/\"0\" 文字列→boolean 変換)", () => {
        const killableBlocks = [{ type: "ekr_when_on_pet", next: { block: { type: "ekr_do_dummy_spawn", fields: { SLOT: 1, NAME: "ダミー", AT: "self", KILLABLE: "1" } } } }];
        expect(compileTopBlocksToRules(killableBlocks)).toEqual([
            { when: "on_pet", do: [{ op: "dummy_spawn", slot: 1, name: "ダミー", killable: true, at: "self" }] },
        ]);

        const notKillableBlocks = [{ type: "ekr_when_on_pet", next: { block: { type: "ekr_do_dummy_spawn", fields: { SLOT: 2, NAME: "ダミー", AT: "ctx", KILLABLE: "0" } } } }];
        expect(compileTopBlocksToRules(notKillableBlocks)).toEqual([
            { when: "on_pet", do: [{ op: "dummy_spawn", slot: 2, name: "ダミー", killable: false, at: "ctx" }] },
        ]);
    });

    it("dummy_spawn の KILLABLE が欠落していると killable は undefined のまま (missing→false に化けない)", () => {
        const blocks = [{ type: "ekr_when_on_pet", next: { block: { type: "ekr_do_dummy_spawn", fields: { SLOT: 1, NAME: "ダミー", AT: "self" } } } }];
        const rules = compileTopBlocksToRules(blocks) as { do: Record<string, unknown>[] }[];
        const node = rules[0].do[0];
        expect(node.killable).toBeUndefined();
        expect("killable" in node).toBe(true);
    });

    it("corpse_spawn は color/at を持つ", () => {
        const blocks = [{ type: "ekr_when_on_pet", next: { block: { type: "ekr_do_corpse_spawn", fields: { COLOR: "random", AT: "ctx" } } } }];
        expect(compileTopBlocksToRules(blocks)).toEqual([{ when: "on_pet", do: [{ op: "corpse_spawn", color: "random", at: "ctx" }] }]);
    });
});

// v1.2 (docs/ekr-logic-spec.md §2〜§3 2026-08-10 追記) — 位置と接触
describe("compile-role: on_cno_touch / marker_save / teleport_other / portal_place ブロックのフィールド分担 (v1.2)", () => {
    it("ekr_when_on_cno_touch は rule に slot を付与する (SLOT フィールドの数値化込み)", () => {
        const blocks = [{ type: "ekr_when_on_cno_touch", fields: { SLOT: "2" }, next: { block: { type: "ekr_do_stop" } } }];
        expect(compileTopBlocksToRules(blocks)).toEqual([{ when: "on_cno_touch", slot: 2, do: [{ op: "stop" }] }]);
    });

    it("on_cno_touch 以外の when には slot キーを付与しない", () => {
        const blocks = [{ type: "ekr_when_on_pet", next: { block: { type: "ekr_do_stop" } } }];
        const rules = compileTopBlocksToRules(blocks) as Record<string, unknown>[];
        expect("slot" in rules[0]).toBe(false);
    });

    it("marker_save は slot/at を持つ", () => {
        const blocks = [{ type: "ekr_when_on_pet", next: { block: { type: "ekr_do_marker_save", fields: { SLOT: 3, AT: "cno1" } } } }];
        expect(compileTopBlocksToRules(blocks)).toEqual([{ when: "on_pet", do: [{ op: "marker_save", slot: 3, at: "cno1" }] }]);
    });

    // Wave 1 で TARGET フィールドが追加されたが、フィールドを持たない (Wave 1 より前に保存された)
    // ワークスペースは従来どおり target:"ctx" として復元される — 移行既定値の回帰テスト。
    it("teleport_other は TARGET 欠落時に target=\"ctx\" で emit し、to をそのまま転記する", () => {
        const blocks = [{ type: "ekr_when_on_pet", next: { block: { type: "ekr_do_teleport_other", fields: { TO: "marker2" } } } }];
        expect(compileTopBlocksToRules(blocks)).toEqual([{ when: "on_pet", do: [{ op: "teleport_other", target: "ctx", to: "marker2" }] }]);
    });

    it("portal_place は which を持つ", () => {
        const blocks = [{ type: "ekr_when_on_pet", next: { block: { type: "ekr_do_portal_place", fields: { WHICH: "b" } } } }];
        expect(compileTopBlocksToRules(blocks)).toEqual([{ when: "on_pet", do: [{ op: "portal_place", which: "b" }] }]);
    });

    it("teleport の TO にマーカー行き先を転記できる (既存 ekr_do_teleport の汎用転記のまま)", () => {
        const blocks = [{ type: "ekr_when_on_pet", next: { block: { type: "ekr_do_teleport", fields: { TO: "marker4" } } } }];
        expect(compileTopBlocksToRules(blocks)).toEqual([{ when: "on_pet", do: [{ op: "teleport", to: "marker4" }] }]);
    });
});

// v1.3 (docs/ekr-logic-spec.md §3 2026-08-11 追記) — ひっぱる・ひきずる・フィールド
describe("compile-role: pull/drag/field ブロックのフィールド分担 (v1.3)", () => {
    it("pull は引数を持たない", () => {
        const blocks = [{ type: "ekr_when_on_pet", next: { block: { type: "ekr_do_pull" } } }];
        expect(compileTopBlocksToRules(blocks)).toEqual([{ when: "on_pet", do: [{ op: "pull" }] }]);
    });

    it("drag は seconds を持つ (数値化込み)", () => {
        const blocks = [{ type: "ekr_when_on_pet", next: { block: { type: "ekr_do_drag", fields: { SECONDS: 4 } } } }];
        expect(compileTopBlocksToRules(blocks)).toEqual([{ when: "on_pet", do: [{ op: "drag", seconds: 4 }] }]);
    });

    it("field は at/radius/strength/seconds を持つ", () => {
        const blocks = [{
            type: "ekr_when_on_pet",
            next: { block: { type: "ekr_do_field", fields: { AT: "marker2", RADIUS: "medium", STRENGTH: "strong", SECONDS: 8 } } },
        }];
        expect(compileTopBlocksToRules(blocks)).toEqual([
            { when: "on_pet", do: [{ op: "field", at: "marker2", radius: "medium", strength: "strong", seconds: 8 }] },
        ]);
    });
});

describe("compile-role: hasNoRules / compileWorkspaceToLogicInput (R0 互換の判定)", () => {
    it("イベントハットが無いワークスペースは hasNoRules=true・compileWorkspaceToLogicInput は null", () => {
        expect(hasNoRules(ws([]))).toBe(true);
        expect(hasNoRules(ws([{ type: "ekr_do_stop" }]))).toBe(true); // ハットに繋がってない断片のみ
        expect(compileWorkspaceToLogicInput(ws([]), [])).toBeNull();
    });

    it("イベントハットが1つでもあれば hasNoRules=false・compileWorkspaceToLogicInput は非 null", () => {
        const w = ws([{ type: "ekr_when_on_pet", next: { block: { type: "ekr_do_stop" } } }]);
        expect(hasNoRules(w)).toBe(false);
        const compiled = compileWorkspaceToLogicInput(w, []);
        expect(compiled).not.toBeNull();
        expect(compiled?.version).toBe(1);
        expect(compiled?.rules).toEqual([{ when: "on_pet", do: [{ op: "stop" }] }]);
        expect(compiled?.blockly).toBe(w);
    });
});

describe("compile-role → roledef.validateRoleLogic: 統合 (実際に使える AST になっているか)", () => {
    it("wait + notify + var_set + if/else を含む複合ロジックが最後まで通る", () => {
        const w = ws([
            {
                type: "ekr_when_on_second",
                next: {
                    block: {
                        type: "ekr_do_var_set",
                        fields: { VAR: "count" },
                        inputs: { VALUE: { block: { type: "math_number", fields: { NUM: 0 } } } },
                        next: {
                            block: {
                                type: "ekr_if_else",
                                inputs: {
                                    COND: {
                                        block: {
                                            type: "ekr_expr_compare",
                                            fields: { OP: "gt" },
                                            inputs: {
                                                A: { block: { type: "ekr_expr_var", fields: { VAR: "count" } } },
                                                B: { block: { type: "math_number", fields: { NUM: 3 } } },
                                            },
                                        },
                                    },
                                    THEN: { block: { type: "ekr_do_notify", fields: { TEXT: "多い", SECONDS: 3 } } },
                                    ELSE: { block: { type: "ekr_do_wait", fields: { SECONDS: 1 } } },
                                },
                            },
                        },
                    },
                },
            },
        ]);
        const compiled = compileWorkspaceToLogicInput(w, [{ name: "count", init: 0 }]);
        expect(compiled).not.toBeNull();
        const r = validateRoleLogic(compiled);
        expect(r.ok, r.ok ? "" : (r as { error: string }).error).toBe(true);
    });

    it("未接続の value 入力 (VALUE 無し) は validateRoleLogic 側で分かりやすく reject される", () => {
        const w = ws([
            { type: "ekr_when_on_pet", next: { block: { type: "ekr_do_var_set", fields: { VAR: "count" } } } },
        ]);
        const compiled = compileWorkspaceToLogicInput(w, [{ name: "count", init: 0 }]);
        const r = validateRoleLogic(compiled);
        expect(r.ok).toBe(false);
    });

    it("宣言されていない変数を参照するロジックは reject される", () => {
        const w = ws([
            { type: "ekr_when_on_pet", next: { block: { type: "ekr_do_var_set", fields: { VAR: "ghost" }, inputs: { VALUE: { block: { type: "math_number", fields: { NUM: 1 } } } } } } },
        ]);
        const compiled = compileWorkspaceToLogicInput(w, []); // 変数を1つも宣言していない
        const r = validateRoleLogic(compiled);
        expect(r.ok).toBe(false);
    });

    // v1.1 (docs/ekr-logic-spec.md §3 2026-08-09 追記)
    it("会議明けに10.5秒待ってから dummy_spawn → corpse_spawn する複合ロジックが最後まで通る", () => {
        const w = ws([
            {
                type: "ekr_when_on_meeting_end",
                next: {
                    block: {
                        type: "ekr_do_wait",
                        fields: { SECONDS: 10.5 },
                        next: {
                            block: {
                                type: "ekr_do_dummy_spawn",
                                fields: { SLOT: 1, NAME: "ダミー", AT: "self", KILLABLE: "1" },
                                next: { block: { type: "ekr_do_corpse_spawn", fields: { COLOR: "self", AT: "self" } } },
                            },
                        },
                    },
                },
            },
        ]);
        const compiled = compileWorkspaceToLogicInput(w, []);
        expect(compiled).not.toBeNull();
        const r = validateRoleLogic(compiled);
        expect(r.ok, r.ok ? "" : (r as { error: string }).error).toBe(true);
        if (r.ok) {
            expect(r.logic.rules[0].do[1]).toEqual({ op: "dummy_spawn", slot: 1, name: "ダミー", killable: true, at: "self" });
            expect(r.logic.rules[0].do[2]).toEqual({ op: "corpse_spawn", color: "self", at: "self" });
        }
    });

    // v1.2 (docs/ekr-logic-spec.md §2〜§3 2026-08-10 追記)
    it("on_cno_touch でマーカーを保存し teleport_other で相手をワープさせる複合ロジックが最後まで通る", () => {
        const w = ws([
            {
                type: "ekr_when_on_cno_touch",
                fields: { SLOT: 1 },
                next: {
                    block: {
                        type: "ekr_do_marker_save",
                        fields: { SLOT: 1, AT: "self" },
                        next: { block: { type: "ekr_do_teleport_other", fields: { TO: "marker1" } } },
                    },
                },
            },
        ]);
        const compiled = compileWorkspaceToLogicInput(w, []);
        expect(compiled).not.toBeNull();
        const r = validateRoleLogic(compiled);
        expect(r.ok, r.ok ? "" : (r as { error: string }).error).toBe(true);
        if (r.ok) {
            expect(r.logic.rules[0]).toMatchObject({ when: "on_cno_touch", slot: 1 });
            expect(r.logic.rules[0].do[1]).toEqual({ op: "teleport_other", target: "ctx", to: "marker1" });
        }
    });

    // v1.3 (docs/ekr-logic-spec.md §3 2026-08-11 追記)
    it("on_kill で相手をひきよせ、つかんでひきずり、フィールドを出す複合ロジックが最後まで通る", () => {
        const w = ws([
            {
                type: "ekr_when_on_kill",
                next: {
                    block: {
                        type: "ekr_do_pull",
                        next: {
                            block: {
                                type: "ekr_do_drag",
                                fields: { SECONDS: 5 },
                                next: {
                                    block: {
                                        type: "ekr_do_field",
                                        fields: { AT: "self", RADIUS: "large", STRENGTH: "strong", SECONDS: 10 },
                                    },
                                },
                            },
                        },
                    },
                },
            },
        ]);
        const compiled = compileWorkspaceToLogicInput(w, []);
        expect(compiled).not.toBeNull();
        const r = validateRoleLogic(compiled);
        expect(r.ok, r.ok ? "" : (r as { error: string }).error).toBe(true);
        if (r.ok) {
            expect(r.logic.rules[0].do[0]).toEqual({ op: "pull" });
            expect(r.logic.rules[0].do[1]).toEqual({ op: "drag", seconds: 5 });
            expect(r.logic.rules[0].do[2]).toEqual({ op: "field", at: "self", radius: "large", strength: "strong", seconds: 10 });
        }
    });
});

// Wave 1 (docs/ekr-logic-spec.md §2/§3 2026-08-11 併合)
describe("compile-role: Wave 1 ブロック (on_attacked / cancel_attack / remember / セレクタ)", () => {
    it("ekr_when_on_attacked は when:\"on_attacked\" の rule になる (slot は付かない)", () => {
        const blocks: SerializedBlock[] = [
            { type: "ekr_when_on_attacked", next: { block: { type: "ekr_do_cancel_attack" } } },
        ];
        expect(compileTopBlocksToRules(blocks)).toEqual([{ when: "on_attacked", do: [{ op: "cancel_attack" }] }]);
    });

    it("ekr_do_remember は SLOT (文字列) を数値へ寄せ、TARGET をそのまま転記する", () => {
        const blocks: SerializedBlock[] = [
            { type: "ekr_when_on_kill", next: { block: { type: "ekr_do_remember", fields: { SLOT: "2", TARGET: "nearest" } } } },
        ];
        expect(compileTopBlocksToRules(blocks)).toEqual([
            { when: "on_kill", do: [{ op: "remember", slot: 2, target: "nearest" }] },
        ]);
    });

    it("notify の TARGET は self / 欠落なら AST に出さない (既定値の明示化はしない)", () => {
        const withSelf: SerializedBlock[] = [
            { type: "ekr_when_on_pet", next: { block: { type: "ekr_do_notify", fields: { TARGET: "self", TEXT: "やあ", SECONDS: 3 } } } },
        ];
        expect(compileTopBlocksToRules(withSelf)).toEqual([{ when: "on_pet", do: [{ op: "notify", text: "やあ", seconds: 3 }] }]);

        const legacy: SerializedBlock[] = [
            { type: "ekr_when_on_pet", next: { block: { type: "ekr_do_notify", fields: { TEXT: "やあ", SECONDS: 3 } } } },
        ];
        expect(compileTopBlocksToRules(legacy)).toEqual([{ when: "on_pet", do: [{ op: "notify", text: "やあ", seconds: 3 }] }]);
    });

    it("notify の TARGET が self 以外なら target を付ける (複数セレクタも)", () => {
        const blocks: SerializedBlock[] = [
            { type: "ekr_when_on_kill", next: { block: { type: "ekr_do_notify", fields: { TARGET: "room", TEXT: "やあ", SECONDS: 3 } } } },
        ];
        expect(compileTopBlocksToRules(blocks)).toEqual([
            { when: "on_kill", do: [{ op: "notify", text: "やあ", seconds: 3, target: "room" }] },
        ]);
    });

    it("teleport_other は TARGET があればそれを使う (Wave 1 のセレクタ拡張)", () => {
        const blocks: SerializedBlock[] = [
            { type: "ekr_when_on_pet", next: { block: { type: "ekr_do_teleport_other", fields: { TARGET: "saved1", TO: "cno2" } } } },
        ];
        expect(compileTopBlocksToRules(blocks)).toEqual([
            { when: "on_pet", do: [{ op: "teleport_other", target: "saved1", to: "cno2" }] },
        ]);
    });

    it("on_attacked の「ふせぐ→おぼえる→反撃」ワークスペースが validateRoleLogic まで通る", () => {
        const w = ws([
            {
                type: "ekr_when_on_attacked",
                next: {
                    block: {
                        type: "ekr_do_cancel_attack",
                        next: {
                            block: {
                                type: "ekr_do_remember",
                                fields: { SLOT: "1", TARGET: "ctx" },
                                next: { block: { type: "ekr_do_kill", fields: { TARGET: "saved1" } } },
                            },
                        },
                    },
                },
            },
        ]);
        const compiled = compileWorkspaceToLogicInput(w, []);
        expect(compiled).not.toBeNull();
        const r = validateRoleLogic(compiled);
        expect(r.ok, r.ok ? "" : (r as { error: string }).error).toBe(true);
        if (r.ok) {
            expect(r.logic.rules[0].when).toBe("on_attacked");
            expect(r.logic.rules[0].do).toEqual([
                { op: "cancel_attack" },
                { op: "remember", slot: 1, target: "ctx" },
                { op: "kill", target: "saved1" },
            ]);
        }
    });

    it("cancel_attack を on_attacked 以外に置いたワークスペースは validateRoleLogic で reject される", () => {
        const w = ws([{ type: "ekr_when_on_pet", next: { block: { type: "ekr_do_cancel_attack" } } }]);
        const r = validateRoleLogic(compileWorkspaceToLogicInput(w, []));
        expect(r.ok).toBe(false);
    });
});
