// compile-role.ts (Blockly serialization JSON → logic AST 入力) のテスト。
// Blockly ランタイムは一切使わず、Blockly.serialization.workspaces.save() が返す形を模した
// 手組みの SerializedBlock フィクスチャで検証する (vitest は environment:"node" — DOM/Blockly 不可)。
// 検証ロジックの二重実装を避けるため、多くのテストは compile → roledef.validateRoleLogic まで
// 通して「実際に使える AST になっているか」を確認する (統合テスト寄り)。

import { describe, expect, it } from "vitest";
import {
    compileTopBlocksToRules,
    compileWorkspaceToLogicInput,
    findEmptyWhenBlocks,
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

// Wave 2 (docs/ekn-wave2-contract.md 2026-08-11 追記) — しらべる系 / とうひょう系
describe("compile-role: しらべる/とうひょう ブロックのフィールド分担 (Wave 2)", () => {
    it("inspect: depth:role のとき failChance/noise が0でなければそれぞれ出力される", () => {
        const blocks = [{
            type: "ekr_when_on_kill",
            next: { block: { type: "ekr_do_inspect", fields: { TARGET: "ctx", DEPTH: "role", FAILCHANCE: 10, NOISE: 2 } } },
        }];
        expect(compileTopBlocksToRules(blocks)).toEqual([
            { when: "on_kill", do: [{ op: "inspect", target: "ctx", depth: "role", failChance: 10, noise: 2 }] },
        ]);
    });

    it("inspect: failChance/noise が0 (既定) のときはキー自体を出力しない (notify.target と同じ既定値省略の作法)", () => {
        const blocks = [{
            type: "ekr_when_on_kill",
            next: { block: { type: "ekr_do_inspect", fields: { TARGET: "ctx", DEPTH: "role", FAILCHANCE: 0, NOISE: 0 } } },
        }];
        expect(compileTopBlocksToRules(blocks)).toEqual([
            { when: "on_kill", do: [{ op: "inspect", target: "ctx", depth: "role" }] },
        ]);
    });

    it("inspect: depth:team のときは NUMBER フィールドに値が残っていても noise を出力しない (failChance は出す)", () => {
        const blocks = [{
            type: "ekr_when_on_kill",
            next: { block: { type: "ekr_do_inspect", fields: { TARGET: "ctx", DEPTH: "team", FAILCHANCE: 20, NOISE: 3 } } },
        }];
        expect(compileTopBlocksToRules(blocks)).toEqual([
            { when: "on_kill", do: [{ op: "inspect", target: "ctx", depth: "team", failChance: 20 }] },
        ]);
    });

    it("reveal は target のみ", () => {
        const blocks = [{ type: "ekr_when_on_kill", next: { block: { type: "ekr_do_reveal", fields: { TARGET: "ctx" } } } }];
        expect(compileTopBlocksToRules(blocks)).toEqual([{ when: "on_kill", do: [{ op: "reveal", target: "ctx" }] }]);
    });

    it("arrow_show は target/seconds を持つ", () => {
        const blocks = [{
            type: "ekr_when_on_kill",
            next: { block: { type: "ekr_do_arrow_show", fields: { TARGET: "nearest", SECONDS: "30" } } },
        }];
        expect(compileTopBlocksToRules(blocks)).toEqual([
            { when: "on_kill", do: [{ op: "arrow_show", target: "nearest", seconds: 30 }] },
        ]);
    });

    it("arrow_mark は at/seconds を持つ", () => {
        const blocks = [{
            type: "ekr_when_on_kill",
            next: { block: { type: "ekr_do_arrow_mark", fields: { AT: "marker1", SECONDS: 60 } } },
        }];
        expect(compileTopBlocksToRules(blocks)).toEqual([
            { when: "on_kill", do: [{ op: "arrow_mark", at: "marker1", seconds: 60 }] },
        ]);
    });

    it("arrow_hide は引数を持たない", () => {
        const blocks = [{ type: "ekr_when_on_pet", next: { block: { type: "ekr_do_arrow_hide" } } }];
        expect(compileTopBlocksToRules(blocks)).toEqual([{ when: "on_pet", do: [{ op: "arrow_hide" }] }]);
    });

    it("cancel_vote は引数を持たない", () => {
        const blocks = [{ type: "ekr_when_on_meeting_vote", next: { block: { type: "ekr_do_cancel_vote" } } }];
        expect(compileTopBlocksToRules(blocks)).toEqual([{ when: "on_meeting_vote", do: [{ op: "cancel_vote" }] }]);
    });

    it("vote_weight_set は value を持つ (数値化込み)", () => {
        const blocks = [{ type: "ekr_when_on_pet", next: { block: { type: "ekr_do_vote_weight_set", fields: { VALUE: "2" } } } }];
        expect(compileTopBlocksToRules(blocks)).toEqual([{ when: "on_pet", do: [{ op: "vote_weight_set", value: 2 }] }]);
    });

    it("vote_block は target のみ", () => {
        const blocks = [{
            type: "ekr_when_on_meeting_start",
            next: { block: { type: "ekr_do_vote_block", fields: { TARGET: "nearest" } } },
        }];
        expect(compileTopBlocksToRules(blocks)).toEqual([
            { when: "on_meeting_start", do: [{ op: "vote_block", target: "nearest" }] },
        ]);
    });

    it("vote_swap は引数を持たない", () => {
        const blocks = [{ type: "ekr_when_on_meeting_pick", next: { block: { type: "ekr_do_vote_swap" } } }];
        expect(compileTopBlocksToRules(blocks)).toEqual([{ when: "on_meeting_pick", do: [{ op: "vote_swap" }] }]);
    });

    it("exile は target のみ (self も転記できる)", () => {
        const blocks = [{
            type: "ekr_when_on_meeting_vote",
            next: { block: { type: "ekr_do_exile", fields: { TARGET: "self" } } },
        }];
        expect(compileTopBlocksToRules(blocks)).toEqual([
            { when: "on_meeting_vote", do: [{ op: "exile", target: "self" }] },
        ]);
    });

    it("Wave 2 の全ブロックが roledef.validateRoleLogic を通る (統合)", () => {
        const blocks: SerializedBlock[] = [
            { type: "ekr_when_on_meeting_vote", next: { block: { type: "ekr_do_cancel_vote" } } },
            {
                type: "ekr_when_on_meeting_pick",
                next: {
                    block: {
                        type: "ekr_do_vote_swap",
                        next: { block: { type: "ekr_do_exile", fields: { TARGET: "ctx" } } },
                    },
                },
            },
            {
                type: "ekr_when_on_kill",
                next: {
                    block: {
                        type: "ekr_do_inspect",
                        fields: { TARGET: "ctx", DEPTH: "role", FAILCHANCE: 10, NOISE: 2 },
                        next: {
                            block: {
                                type: "ekr_do_reveal",
                                fields: { TARGET: "ctx" },
                                next: {
                                    block: {
                                        type: "ekr_do_arrow_show",
                                        fields: { TARGET: "ctx", SECONDS: 30 },
                                        next: {
                                            block: {
                                                type: "ekr_do_arrow_mark",
                                                fields: { AT: "ctx", SECONDS: 30 },
                                                next: {
                                                    block: {
                                                        type: "ekr_do_arrow_hide",
                                                        next: {
                                                            block: { type: "ekr_do_vote_weight_set", fields: { VALUE: 2 } },
                                                        },
                                                    },
                                                },
                                            },
                                        },
                                    },
                                },
                            },
                        },
                    },
                },
            },
        ];
        const compiled = compileWorkspaceToLogicInput(ws(blocks), []);
        expect(compiled).not.toBeNull();
        const result = validateRoleLogic(compiled);
        expect(result.ok, result.ok ? "" : result.error).toBe(true);
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

// Wave 3 (docs/ekn-wave3-contract.md §1 2026-08-14) — じょうたいと数値の新3イベント
describe("compile-role: Wave 3 ブロック (on_var / on_alive_count / on_vent_exit)", () => {
    it("ekr_when_on_var は var/cmp/value を rule に転記する", () => {
        const blocks: SerializedBlock[] = [
            { type: "ekr_when_on_var", fields: { VAR: "カウント", VALUE: 5, CMP: "ge" }, next: { block: { type: "ekr_do_stop" } } },
        ];
        expect(compileTopBlocksToRules(blocks)).toEqual([
            { when: "on_var", var: "カウント", cmp: "ge", value: 5, do: [{ op: "stop" }] },
        ]);
    });

    it("ekr_when_on_alive_count は cmp/value を rule に転記する (var は付かない)", () => {
        const blocks: SerializedBlock[] = [
            { type: "ekr_when_on_alive_count", fields: { VALUE: 3, CMP: "le" }, next: { block: { type: "ekr_do_stop" } } },
        ];
        expect(compileTopBlocksToRules(blocks)).toEqual([
            { when: "on_alive_count", cmp: "le", value: 3, do: [{ op: "stop" }] },
        ]);
    });

    it("ekr_when_on_vent_exit は追加フィールド無しの通常イベントとして転記される", () => {
        const blocks: SerializedBlock[] = [
            { type: "ekr_when_on_vent_exit", next: { block: { type: "ekr_do_stop" } } },
        ];
        expect(compileTopBlocksToRules(blocks)).toEqual([{ when: "on_vent_exit", do: [{ op: "stop" }] }]);
    });

    it("on_var のワークスペースが validateRoleLogic まで通る (変数を宣言した状態で)", () => {
        const w = ws([
            { type: "ekr_when_on_var", fields: { VAR: "カウント", VALUE: 7, CMP: "eq" }, next: { block: { type: "ekr_do_stop" } } },
        ]);
        const compiled = compileWorkspaceToLogicInput(w, [{ name: "カウント", init: 0 }]);
        const r = validateRoleLogic(compiled);
        expect(r.ok, r.ok ? "" : (r as { error: string }).error).toBe(true);
        if (r.ok) expect(r.logic.rules[0]).toEqual({ when: "on_var", var: "カウント", cmp: "eq", value: 7, do: [{ op: "stop" }] });
    });

    it("on_var のワークスペースは変数未宣言だと validateRoleLogic で reject される", () => {
        const w = ws([
            { type: "ekr_when_on_var", fields: { VAR: "カウント", VALUE: 7, CMP: "eq" }, next: { block: { type: "ekr_do_stop" } } },
        ]);
        const r = validateRoleLogic(compileWorkspaceToLogicInput(w, []));
        expect(r.ok).toBe(false);
    });
});

describe("compile-role: 空っぽのきっかけブロック検出", () => {
    it("下に何も繋がっていないハットだけを id 付きで拾う", () => {
        const w = ws([
            { type: "ekr_when_on_pet", id: "A", next: { block: { type: "ekr_do_stop" } } },
            { type: "ekr_when_on_vent_enter", id: "B" },
            { type: "ekr_do_stop", id: "C" }, // 孤立した非ハットは対象外
            { type: "ekr_when_on_second", id: "D" },
        ]);
        expect(findEmptyWhenBlocks(w)).toEqual([
            { id: "B", when: "on_vent_enter" },
            { id: "D", when: "on_second" },
        ]);
    });

    it("空のハットは validateRoleLogic では index 表記でしか場所が分からない (この検出が必要な理由)", () => {
        const w = ws([
            { type: "ekr_when_on_pet", next: { block: { type: "ekr_do_stop" } } },
            { type: "ekr_when_on_vent_enter" },
        ]);
        const r = validateRoleLogic(compileWorkspaceToLogicInput(w, []));
        expect(r.ok).toBe(false);
        expect(findEmptyWhenBlocks(w).map((e) => e.when)).toEqual(["on_vent_enter"]);

        // 空のハットを取り除いた同じワークスペースは通る (= 空ハットだけが原因であることの片側検証)
        const fixed = ws([{ type: "ekr_when_on_pet", next: { block: { type: "ekr_do_stop" } } }]);
        expect(validateRoleLogic(compileWorkspaceToLogicInput(fixed, [])).ok).toBe(true);
    });

    it("全てのハットに中身があれば空配列", () => {
        const w = ws([{ type: "ekr_when_on_pet", next: { block: { type: "ekr_do_stop" } } }]);
        expect(findEmptyWhenBlocks(w)).toEqual([]);
    });
});

// Wave 4 (docs/ekn-wave4-contract.md 2026-08-25) — つなぐ
describe("compile-role: Wave 4 ブロック (on_near / on_far / on_room_enter / on_room_exit / on_linked_death / link / unlink / recruit)", () => {
    it("ekr_when_on_near は radius を転記し、WHO が既定の anyone ならフィールドごと省略する", () => {
        const blocks: SerializedBlock[] = [
            { type: "ekr_when_on_near", fields: { RADIUS: "small", WHO: "anyone" }, next: { block: { type: "ekr_do_stop" } } },
        ];
        expect(compileTopBlocksToRules(blocks)).toEqual([
            { when: "on_near", radius: "small", do: [{ op: "stop" }] },
        ]);
    });

    it("ekr_when_on_near は WHO が anyone 以外なら who を転記する", () => {
        const blocks: SerializedBlock[] = [
            { type: "ekr_when_on_near", fields: { RADIUS: "medium", WHO: "linked" }, next: { block: { type: "ekr_do_stop" } } },
        ];
        expect(compileTopBlocksToRules(blocks)).toEqual([
            { when: "on_near", radius: "medium", who: "linked", do: [{ op: "stop" }] },
        ]);
    });

    it("ekr_when_on_far は radius/who を両方転記する (who は必須)", () => {
        const blocks: SerializedBlock[] = [
            { type: "ekr_when_on_far", fields: { RADIUS: "large", WHO: "saved2" }, next: { block: { type: "ekr_do_stop" } } },
        ];
        expect(compileTopBlocksToRules(blocks)).toEqual([
            { when: "on_far", radius: "large", who: "saved2", do: [{ op: "stop" }] },
        ]);
    });

    it("ekr_when_on_linked_death は CAUSE 空文字 (すべて) をフィールドごと省略し、指定時は cause を転記する", () => {
        const all: SerializedBlock[] = [
            { type: "ekr_when_on_linked_death", fields: { CAUSE: "" }, next: { block: { type: "ekr_do_stop" } } },
        ];
        expect(compileTopBlocksToRules(all)).toEqual([{ when: "on_linked_death", do: [{ op: "stop" }] }]);

        const filtered: SerializedBlock[] = [
            { type: "ekr_when_on_linked_death", fields: { CAUSE: "kill" }, next: { block: { type: "ekr_do_stop" } } },
        ];
        expect(compileTopBlocksToRules(filtered)).toEqual([{ when: "on_linked_death", cause: "kill", do: [{ op: "stop" }] }]);
    });

    it("on_room_enter / on_room_exit は追加フィールド無しの生成ハットとして転記される", () => {
        const blocks: SerializedBlock[] = [
            { type: "ekr_when_on_room_enter", next: { block: { type: "ekr_do_stop" } } },
            { type: "ekr_when_on_room_exit", next: { block: { type: "ekr_do_stop" } } },
        ];
        expect(compileTopBlocksToRules(blocks)).toEqual([
            { when: "on_room_enter", do: [{ op: "stop" }] },
            { when: "on_room_exit", do: [{ op: "stop" }] },
        ]);
    });

    it("ekr_do_link / ekr_do_unlink / ekr_do_recruit をコンパイルする", () => {
        const blocks: SerializedBlock[] = [
            {
                type: "ekr_when_on_kill",
                next: {
                    block: {
                        type: "ekr_do_link",
                        fields: { TARGET: "ctx" },
                        next: {
                            block: {
                                type: "ekr_do_recruit",
                                fields: { TARGET: "linked" },
                                next: { block: { type: "ekr_do_unlink" } },
                            },
                        },
                    },
                },
            },
        ];
        expect(compileTopBlocksToRules(blocks)).toEqual([
            {
                when: "on_kill",
                do: [
                    { op: "link", target: "ctx" },
                    { op: "recruit", target: "linked" },
                    { op: "unlink" },
                ],
            },
        ]);
    });

    // Wave 5 (docs/ekn-wave5-contract.md §1/§2)
    it("ekr_do_recruit の SLOT は既定 \"\" でフィールドごと省略し、指名時だけ数値で転記する", () => {
        expect(compileTopBlocksToRules([
            { type: "ekr_when_on_kill", next: { block: { type: "ekr_do_recruit", fields: { TARGET: "ctx", SLOT: "" } } } },
        ])).toEqual([{ when: "on_kill", do: [{ op: "recruit", target: "ctx" }] }]);

        expect(compileTopBlocksToRules([
            { type: "ekr_when_on_kill", next: { block: { type: "ekr_do_recruit", fields: { TARGET: "ctx", SLOT: "12" } } } },
        ])).toEqual([{ when: "on_kill", do: [{ op: "recruit", target: "ctx", slot: 12 }] }]);
    });

    it("ekr_do_effect_give をコンパイルする (3フィールドとも常時書き出し)", () => {
        expect(compileTopBlocksToRules([
            { type: "ekr_when_on_pet", next: { block: { type: "ekr_do_effect_give", fields: { TARGET: "nearest", KIND: "freeze", SECONDS: 5 } } } },
        ])).toEqual([{ when: "on_pet", do: [{ op: "effect_give", target: "nearest", kind: "freeze", seconds: 5 }] }]);
    });

    it("「つなぐ→リンク死で道連れ」ワークスペースが validateRoleLogic まで通る", () => {
        const w = ws([
            { type: "ekr_when_on_kill", next: { block: { type: "ekr_do_link", fields: { TARGET: "ctx" } } } },
            { type: "ekr_when_on_linked_death", fields: { CAUSE: "" }, next: { block: { type: "ekr_do_kill", fields: { TARGET: "self" } } } },
            { type: "ekr_when_on_far", fields: { RADIUS: "medium", WHO: "linked" }, next: { block: { type: "ekr_do_kill", fields: { TARGET: "linked" } } } },
        ]);
        const compiled = compileWorkspaceToLogicInput(w, []);
        expect(compiled).not.toBeNull();
        const r = validateRoleLogic(compiled);
        expect(r.ok, r.ok ? "" : (r as { error: string }).error).toBe(true);
        if (r.ok) {
            expect(r.logic.rules).toEqual([
                { when: "on_kill", do: [{ op: "link", target: "ctx" }] },
                { when: "on_linked_death", do: [{ op: "kill", target: "self" }] },
                { when: "on_far", radius: "medium", who: "linked", do: [{ op: "kill", target: "linked" }] },
            ]);
        }
    });

    it("on_near ハットの RADIUS が欠落したままだと validateRoleLogic で reject される (欠落を既定値に化けさせない)", () => {
        const w = ws([{ type: "ekr_when_on_near", next: { block: { type: "ekr_do_stop" } } }]);
        const r = validateRoleLogic(compileWorkspaceToLogicInput(w, []));
        expect(r.ok).toBe(false);
    });
});
