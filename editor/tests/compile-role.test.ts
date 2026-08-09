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
});
