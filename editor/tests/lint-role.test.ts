// lint-role.ts (docs/ekr-logic-spec.md §6 の 6 ルール) のテスト。既に検証済みという前提の
// RoleLogic 値を直接組み立ててテストする (Blockly/roledef の検証を経由する必要はない —
// リンターは「妥当な AST に対して組み方のヒントを出す」だけの層のため)。

import { describe, expect, it } from "vitest";
import { formatLintWarning, lintRoleLogic, type LintRuleId } from "../src/logic/lint-role";
import type { LogicNode, RoleLogic } from "../src/roledef";

function logic(rules: RoleLogic["rules"]): RoleLogic {
    return { version: 1, variables: [], rules };
}

function ruleIds(warnings: { rule: LintRuleId }[]): LintRuleId[] {
    return warnings.map((w) => w.rule);
}

describe("lint-role: L1 (on_second 配下の cno_spawn)", () => {
    it("on_second + cno_spawn (直下) は L1 を警告する", () => {
        const l = logic([{ when: "on_second", do: [{ op: "cno_spawn", slot: 1, text: "!", size: 1, at: "self" }] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L1");
    });

    it("on_second + cno_spawn (if の中にネスト) も検知する", () => {
        const nested: LogicNode = {
            op: "if",
            cond: { e: "lit", v: 1 },
            then: [{ op: "cno_spawn", slot: 1, text: "!", size: 1, at: "self" }],
        };
        const l = logic([{ when: "on_second", do: [nested] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L1");
    });

    it("on_pet (on_second 以外) の cno_spawn は L1 を警告しない", () => {
        const l = logic([{ when: "on_pet", do: [{ op: "cno_spawn", slot: 1, text: "!", size: 1, at: "self" }] }]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L1");
    });

    it("同じ rule 内に cno_spawn が複数あっても L1 は1回だけ警告する (rule 単位で重複排除)", () => {
        const l = logic([
            {
                when: "on_second",
                do: [
                    { op: "cno_spawn", slot: 1, text: "!", size: 1, at: "self" },
                    { op: "cno_spawn", slot: 2, text: "!", size: 1, at: "self" },
                ],
            },
        ]);
        expect(lintRoleLogic(l).filter((w) => w.rule === "L1")).toHaveLength(1);
    });
});

describe("lint-role: L2 (despawn 無しで同一 slot へ複数 cno_spawn)", () => {
    it("同一 slot への cno_spawn が2回以上・despawn 無しなら警告する", () => {
        const l = logic([
            {
                when: "on_pet",
                do: [
                    { op: "cno_spawn", slot: 1, text: "!", size: 1, at: "self" },
                    { op: "cno_spawn", slot: 1, text: "!", size: 1, at: "self" },
                ],
            },
        ]);
        const warnings = lintRoleLogic(l).filter((w) => w.rule === "L2");
        expect(warnings).toHaveLength(1);
    });

    it("同一 slot への cno_spawn が1回だけなら警告しない", () => {
        const l = logic([{ when: "on_pet", do: [{ op: "cno_spawn", slot: 1, text: "!", size: 1, at: "self" }] }]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L2");
    });

    it("cno_despawn が同じ rule 内にあれば警告しない (静的近似: 個数だけ見る)", () => {
        const l = logic([
            {
                when: "on_pet",
                do: [
                    { op: "cno_spawn", slot: 1, text: "!", size: 1, at: "self" },
                    { op: "cno_despawn", slot: 1 },
                    { op: "cno_spawn", slot: 1, text: "!", size: 1, at: "self" },
                ],
            },
        ]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L2");
    });

    it("異なる slot へならそれぞれ1回ずつなので警告しない", () => {
        const l = logic([
            {
                when: "on_pet",
                do: [
                    { op: "cno_spawn", slot: 1, text: "!", size: 1, at: "self" },
                    { op: "cno_spawn", slot: 2, text: "!", size: 1, at: "self" },
                ],
            },
        ]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L2");
    });

    it("L2 は on_second 以外の when でも検知する (on_second 限定ではない)", () => {
        const l = logic([
            {
                when: "on_kill",
                do: [
                    { op: "cno_spawn", slot: 3, text: "!", size: 1, at: "self" },
                    { op: "cno_spawn", slot: 3, text: "!", size: 1, at: "self" },
                ],
            },
        ]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L2");
    });
});

describe("lint-role: L3/L4/L5/L6 (on_second 配下の teleport/notify/kill/cno_show)", () => {
    it("on_second + teleport は L3 を警告する", () => {
        const l = logic([{ when: "on_second", do: [{ op: "teleport", to: "random" }] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L3");
    });

    it("on_second + notify は L4 を警告する", () => {
        const l = logic([{ when: "on_second", do: [{ op: "notify", text: "hi", seconds: 1 }] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L4");
    });

    it("on_second + kill は L5 を警告する", () => {
        const l = logic([{ when: "on_second", do: [{ op: "kill", target: "ctx" }] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L5");
    });

    it("on_second + cno_show は L6 を警告する", () => {
        const l = logic([{ when: "on_second", do: [{ op: "cno_show", slot: 1, who: "all" }] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L6");
    });

    it("on_second + cno_show (if の中にネスト) も検知する", () => {
        const nested: LogicNode = {
            op: "if",
            cond: { e: "lit", v: 1 },
            then: [{ op: "cno_show", slot: 2, who: "self" }],
        };
        const l = logic([{ when: "on_second", do: [nested] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L6");
    });

    it("on_second 以外の when では L3/L4/L5/L6 とも警告しない", () => {
        const l = logic([
            { when: "on_kill", do: [{ op: "teleport", to: "random" }] },
            { when: "on_report", do: [{ op: "notify", text: "hi", seconds: 1 }] },
            { when: "on_meeting_end", do: [{ op: "kill", target: "self" }] },
            { when: "on_pet", do: [{ op: "cno_show", slot: 1, who: "all" }] },
        ]);
        const ids = ruleIds(lintRoleLogic(l));
        expect(ids).not.toContain("L3");
        expect(ids).not.toContain("L4");
        expect(ids).not.toContain("L5");
        expect(ids).not.toContain("L6");
    });
});

describe("lint-role: 問題のないロジックは警告0件", () => {
    it("on_pet 単発の kill/teleport/notify/cno_spawn/cno_show は何も警告しない", () => {
        const l = logic([
            {
                when: "on_pet",
                do: [
                    { op: "kill", target: "ctx" },
                    { op: "teleport", to: "ctx" },
                    { op: "notify", text: "やった", seconds: 3 },
                    { op: "cno_spawn", slot: 1, text: "!", size: 1, at: "self" },
                    { op: "cno_show", slot: 1, who: "all" },
                ],
            },
        ]);
        expect(lintRoleLogic(l)).toEqual([]);
    });

    it("on_second で var_set/var_add/if/stop だけを使うロジックは警告0件", () => {
        const l = logic([
            {
                when: "on_second",
                do: [
                    { op: "var_add", name: "count", delta: { e: "lit", v: 1 } },
                    { op: "if", cond: { e: "lit", v: 1 }, then: [{ op: "stop" }] },
                ],
            },
        ]);
        expect(lintRoleLogic(l)).toEqual([]);
    });
});

describe("lint-role: formatLintWarning", () => {
    it("メッセージと代替案を連結した1行を返す", () => {
        const l = logic([{ when: "on_second", do: [{ op: "kill", target: "self" }] }]);
        const warnings = lintRoleLogic(l);
        expect(warnings).toHaveLength(1);
        const text = formatLintWarning(warnings[0]);
        expect(text).toContain(warnings[0].message);
        expect(text).toContain(warnings[0].suggestion);
    });
});
