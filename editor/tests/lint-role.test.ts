// lint-role.ts (docs/ekr-logic-spec.md §6 の 12 ルール — v1.1 で L9/L10・v1.2 で L11/L12 追加) のテスト。既に
// 検証済みという前提の RoleLogic 値を直接組み立ててテストする (Blockly/roledef の検証を経由する
// 必要はない — リンターは「妥当な AST に対して組み方のヒントを出す」だけの層のため)。

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

    // v1.1 (docs/ekr-logic-spec.md §6): L1 の検知対象に dummy_spawn を追加
    it("on_second + dummy_spawn (直下) も L1 を警告する", () => {
        const l = logic([{ when: "on_second", do: [{ op: "dummy_spawn", slot: 1, name: "ダミー", killable: false, at: "self" }] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L1");
    });

    it("on_second + dummy_spawn (if の中にネスト) も検知する", () => {
        const nested: LogicNode = {
            op: "if",
            cond: { e: "lit", v: 1 },
            then: [{ op: "dummy_spawn", slot: 1, name: "ダミー", killable: false, at: "self" }],
        };
        const l = logic([{ when: "on_second", do: [nested] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L1");
    });

    it("on_second で cno_spawn と dummy_spawn が両方あっても L1 は1回だけ警告する", () => {
        const l = logic([
            {
                when: "on_second",
                do: [
                    { op: "cno_spawn", slot: 1, text: "!", size: 1, at: "self" },
                    { op: "dummy_spawn", slot: 2, name: "ダミー", killable: false, at: "self" },
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

describe("lint-role: L7 (on_second 配下の wait 合計 ≥1秒 — fiber cap 独占)", () => {
    it("on_second + wait 1.0 (ちょうど) は L7 を警告する", () => {
        const l = logic([{ when: "on_second", do: [{ op: "wait", seconds: 1.0 }] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L7");
    });

    it("on_second + wait 0.5×2 (合計1.0) も合算して警告する", () => {
        const l = logic([
            { when: "on_second", do: [{ op: "wait", seconds: 0.5 }, { op: "wait", seconds: 0.5 }] },
        ]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L7");
    });

    it("if の中の wait も合算する (静的近似: 分岐は区別しない)", () => {
        const nested: LogicNode = {
            op: "if",
            cond: { e: "lit", v: 1 },
            then: [{ op: "wait", seconds: 0.6 }],
            else: [{ op: "wait", seconds: 0.6 }],
        };
        const l = logic([{ when: "on_second", do: [nested] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L7");
    });

    it("on_second + wait 0.9 (1秒未満) は警告しない", () => {
        const l = logic([{ when: "on_second", do: [{ op: "wait", seconds: 0.9 }] }]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L7");
    });

    it("on_second 以外の when では長い wait でも警告しない", () => {
        const l = logic([{ when: "on_pet", do: [{ op: "wait", seconds: 10 }] }]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L7");
    });
});

describe("lint-role: L8 (前の cno_spawn からの累積 wait <1秒の cno_spawn — 1秒1個レートのドロップ)", () => {
    it("wait なしで cno_spawn を2連発すると L8 を警告する (slot が違っても)", () => {
        const l = logic([
            {
                when: "on_game_start",
                do: [
                    { op: "cno_spawn", slot: 1, text: "!", size: 1, at: "self" },
                    { op: "cno_spawn", slot: 2, text: "!", size: 1, at: "self" },
                ],
            },
        ]);
        expect(lintRoleLogic(l).filter((w) => w.rule === "L8")).toHaveLength(1);
    });

    it("間に wait 1.1 を挟めば警告しない (fixture の正しい作法)", () => {
        const l = logic([
            {
                when: "on_game_start",
                do: [
                    { op: "cno_spawn", slot: 1, text: "!", size: 1, at: "self" },
                    { op: "wait", seconds: 1.1 },
                    { op: "cno_spawn", slot: 2, text: "!", size: 1, at: "self" },
                ],
            },
        ]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L8");
    });

    it("間の wait が累積1秒未満なら警告する", () => {
        const l = logic([
            {
                when: "on_pet",
                do: [
                    { op: "cno_spawn", slot: 1, text: "!", size: 1, at: "self" },
                    { op: "wait", seconds: 0.5 },
                    { op: "cno_spawn", slot: 2, text: "!", size: 1, at: "self" },
                ],
            },
        ]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L8");
    });

    it("wait を分割しても累積1秒以上なら警告しない", () => {
        const l = logic([
            {
                when: "on_pet",
                do: [
                    { op: "cno_spawn", slot: 1, text: "!", size: 1, at: "self" },
                    { op: "wait", seconds: 0.5 },
                    { op: "wait", seconds: 0.6 },
                    { op: "cno_spawn", slot: 2, text: "!", size: 1, at: "self" },
                ],
            },
        ]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L8");
    });

    it("cno_spawn 単発は警告しない (初回はレートバケットの初期トークンで必ず出る)", () => {
        const l = logic([{ when: "on_pet", do: [{ op: "cno_spawn", slot: 1, text: "!", size: 1, at: "self" }] }]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L8");
    });

    it("先頭に wait があっても初回 spawn の判定には影響しない", () => {
        const l = logic([
            {
                when: "on_pet",
                do: [
                    { op: "wait", seconds: 0.2 },
                    { op: "cno_spawn", slot: 1, text: "!", size: 1, at: "self" },
                ],
            },
        ]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L8");
    });
});

// v1.1 (docs/ekr-logic-spec.md §6): dummy_spawn 新設に伴う L9/L10 追加
describe("lint-role: L9 (on_meeting_end 配下・会議明けから10秒未満で出す dummy_spawn)", () => {
    it("on_meeting_end + dummy_spawn (wait 無し・直後) は L9 を警告する", () => {
        const l = logic([{ when: "on_meeting_end", do: [{ op: "dummy_spawn", slot: 1, name: "ダミー", killable: false, at: "self" }] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L9");
    });

    it("on_meeting_end + wait 10 (ちょうど) → dummy_spawn は警告しない (境界値)", () => {
        const l = logic([
            {
                when: "on_meeting_end",
                do: [{ op: "wait", seconds: 10 }, { op: "dummy_spawn", slot: 1, name: "ダミー", killable: false, at: "self" }],
            },
        ]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L9");
    });

    it("on_meeting_end + wait 9.9 → dummy_spawn は警告する (10秒未満)", () => {
        const l = logic([
            {
                when: "on_meeting_end",
                do: [{ op: "wait", seconds: 9.9 }, { op: "dummy_spawn", slot: 1, name: "ダミー", killable: false, at: "self" }],
            },
        ]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L9");
    });

    it("dummy_spawn が先で wait が後ろだと、合計は10秒以上でも警告する (位置を区別する — L7 の単純合算とは異なる)", () => {
        const l = logic([
            {
                when: "on_meeting_end",
                do: [{ op: "dummy_spawn", slot: 1, name: "ダミー", killable: false, at: "self" }, { op: "wait", seconds: 10 }],
            },
        ]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L9");
    });

    it("wait を分割しても spawn より前に合計10秒以上あれば警告しない", () => {
        const l = logic([
            {
                when: "on_meeting_end",
                do: [
                    { op: "wait", seconds: 5 },
                    { op: "wait", seconds: 5 },
                    { op: "dummy_spawn", slot: 1, name: "ダミー", killable: false, at: "self" },
                ],
            },
        ]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L9");
    });

    it("if/else 両方の wait を合算する (静的近似: 分岐は区別しない — 各枝6秒だと合算12秒で警告しない)", () => {
        const nested: LogicNode = {
            op: "if",
            cond: { e: "lit", v: 1 },
            then: [{ op: "wait", seconds: 6 }],
            else: [{ op: "wait", seconds: 6 }],
        };
        const l = logic([
            { when: "on_meeting_end", do: [nested, { op: "dummy_spawn", slot: 1, name: "ダミー", killable: false, at: "self" }] },
        ]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L9");
    });

    it("dummy_spawn が無ければ wait が無くても警告しない", () => {
        const l = logic([{ when: "on_meeting_end", do: [{ op: "notify", text: "hi", seconds: 1 }] }]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L9");
    });

    it("on_meeting_end 以外の when では wait 無し dummy_spawn でも警告しない", () => {
        const l = logic([{ when: "on_pet", do: [{ op: "dummy_spawn", slot: 1, name: "ダミー", killable: false, at: "self" }] }]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L9");
    });
});

describe("lint-role: L10 (前の dummy_spawn からの累積 wait <3秒の dummy_spawn)", () => {
    function dummy(slot: 1 | 2 | 3): LogicNode {
        return { op: "dummy_spawn", slot, name: "ダミー", killable: false, at: "self" };
    }

    it("wait なしで dummy_spawn を2連発すると L10 を警告する (slot が違っても)", () => {
        const l = logic([{ when: "on_game_start", do: [dummy(1), dummy(2)] }]);
        expect(lintRoleLogic(l).filter((w) => w.rule === "L10")).toHaveLength(1);
    });

    it("間に wait 3.1 を挟めば警告しない (正しい作法)", () => {
        const l = logic([{ when: "on_game_start", do: [dummy(1), { op: "wait", seconds: 3.1 }, dummy(2)] }]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L10");
    });

    it("間の wait が累積3秒未満なら警告する", () => {
        const l = logic([{ when: "on_pet", do: [dummy(1), { op: "wait", seconds: 1 }, dummy(2)] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L10");
    });

    it("wait を分割しても累積3秒以上なら警告しない", () => {
        const l = logic([{ when: "on_pet", do: [dummy(1), { op: "wait", seconds: 1.5 }, { op: "wait", seconds: 1.6 }, dummy(2)] }]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L10");
    });

    it("dummy_spawn 単発は警告しない (初回はレートバケットの初期トークンで必ず出る)", () => {
        const l = logic([{ when: "on_pet", do: [dummy(1)] }]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L10");
    });

    it("先頭に wait があっても初回 spawn の判定には影響しない", () => {
        const l = logic([{ when: "on_pet", do: [{ op: "wait", seconds: 0.2 }, dummy(1)] }]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L10");
    });

    it("cno_spawn と dummy_spawn は別バケットなので間をあけずに並べても互いに影響しない", () => {
        const l = logic([
            {
                when: "on_pet",
                do: [{ op: "cno_spawn", slot: 1, text: "!", size: 1, at: "self" }, dummy(1)],
            },
        ]);
        const ids = ruleIds(lintRoleLogic(l));
        expect(ids).not.toContain("L8");
        expect(ids).not.toContain("L10");
    });
});

// v1.2 (docs/ekr-logic-spec.md §6 2026-08-10 追記): L9 の対象 op を4種 (cno_spawn/cno_show/
// dummy_spawn/portal_place) に拡大
describe("lint-role: L9 拡大 (v1.2 — cno_spawn/cno_show/portal_place も同じ会議明け窓で検知)", () => {
    it("on_meeting_end + cno_spawn (wait 無し・直後) は L9 を警告する", () => {
        const l = logic([{ when: "on_meeting_end", do: [{ op: "cno_spawn", slot: 1, text: "!", size: 1, at: "self" }] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L9");
    });

    it("on_meeting_end + cno_show (wait 無し・直後) は L9 を警告する", () => {
        const l = logic([{ when: "on_meeting_end", do: [{ op: "cno_show", slot: 1, who: "all" }] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L9");
    });

    it("on_meeting_end + portal_place (wait 無し・直後) は L9 を警告する", () => {
        const l = logic([{ when: "on_meeting_end", do: [{ op: "portal_place", which: "a" }] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L9");
    });

    it("on_meeting_end + wait 10.5 → cno_spawn/cno_show/portal_place は警告しない (正しい作法)", () => {
        const l = logic([
            { when: "on_meeting_end", do: [{ op: "wait", seconds: 10.5 }, { op: "cno_spawn", slot: 1, text: "!", size: 1, at: "self" }] },
            { when: "on_meeting_end", do: [{ op: "wait", seconds: 10.5 }, { op: "cno_show", slot: 1, who: "all" }] },
            { when: "on_meeting_end", do: [{ op: "wait", seconds: 10.5 }, { op: "portal_place", which: "b" }] },
        ]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L9");
    });

    // v1.3 (docs/ekr-logic-spec.md §6 2026-08-11 追記): L9 の対象 op に field を追加
    it("on_meeting_end + field (wait 無し・直後) は L9 を警告する", () => {
        const l = logic([{
            when: "on_meeting_end",
            do: [{ op: "field", at: "self", radius: "small", strength: "weak", seconds: 3 }],
        }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L9");
    });

    it("on_meeting_end + wait 10.5 → field は警告しない (正しい作法)", () => {
        const l = logic([{
            when: "on_meeting_end",
            do: [{ op: "wait", seconds: 10.5 }, { op: "field", at: "self", radius: "small", strength: "weak", seconds: 3 }],
        }]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L9");
    });
});

describe("lint-role: L11 (on_second 配下の teleport_other / portal_place — L3 の兄弟)", () => {
    it("on_second + teleport_other は L11 を警告する", () => {
        const l = logic([{ when: "on_second", do: [{ op: "teleport_other", target: "ctx", to: "self" }] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L11");
    });

    it("on_second + portal_place は L11 を警告する", () => {
        const l = logic([{ when: "on_second", do: [{ op: "portal_place", which: "a" }] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L11");
    });

    it("on_second + teleport_other (if の中にネスト) も検知する", () => {
        const nested: LogicNode = {
            op: "if",
            cond: { e: "lit", v: 1 },
            then: [{ op: "teleport_other", target: "ctx", to: "marker1" }],
        };
        const l = logic([{ when: "on_second", do: [nested] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L11");
    });

    it("on_second 以外の when では L11 を警告しない", () => {
        const l = logic([
            { when: "on_pet", do: [{ op: "teleport_other", target: "ctx", to: "self" }] },
            { when: "on_kill", do: [{ op: "portal_place", which: "b" }] },
        ]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L11");
    });
});

// v1.3 (docs/ekr-logic-spec.md §6 2026-08-11 追記): L13 = L3/L11 の兄弟 (pull/drag/field)
describe("lint-role: L13 (on_second 配下の pull / drag / field)", () => {
    it("on_second + pull は L13 を警告する", () => {
        const l = logic([{ when: "on_second", do: [{ op: "pull" }] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L13");
    });

    it("on_second + drag は L13 を警告する", () => {
        const l = logic([{ when: "on_second", do: [{ op: "drag", seconds: 3 }] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L13");
    });

    it("on_second + field は L13 を警告する", () => {
        const l = logic([{ when: "on_second", do: [{ op: "field", at: "self", radius: "small", strength: "weak", seconds: 3 }] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L13");
    });

    it("on_second + pull (if の中にネスト) も検知する", () => {
        const nested: LogicNode = { op: "if", cond: { e: "lit", v: 1 }, then: [{ op: "pull" }] };
        const l = logic([{ when: "on_second", do: [nested] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L13");
    });

    it("on_second 以外の when では L13 を警告しない", () => {
        const l = logic([
            { when: "on_pet", do: [{ op: "pull" }] },
            { when: "on_kill", do: [{ op: "drag", seconds: 5 }] },
            { when: "on_death", do: [{ op: "field", at: "self", radius: "large", strength: "strong", seconds: 15 }] },
        ]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L13");
    });
});

describe("lint-role: L12 (on_cno_touch 配下の cno_spawn/dummy_spawn/cno_show/portal_place)", () => {
    it("on_cno_touch + cno_spawn は L12 を警告する", () => {
        const l = logic([{ when: "on_cno_touch", slot: 1, do: [{ op: "cno_spawn", slot: 2, text: "!", size: 1, at: "self" }] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L12");
    });

    it("on_cno_touch + dummy_spawn は L12 を警告する", () => {
        const l = logic([{ when: "on_cno_touch", slot: 1, do: [{ op: "dummy_spawn", slot: 2, name: "ダミー", killable: false, at: "self" }] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L12");
    });

    it("on_cno_touch + cno_show は L12 を警告する", () => {
        const l = logic([{ when: "on_cno_touch", slot: 1, do: [{ op: "cno_show", slot: 2, who: "all" }] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L12");
    });

    it("on_cno_touch + portal_place は L12 を警告する", () => {
        const l = logic([{ when: "on_cno_touch", slot: 1, do: [{ op: "portal_place", which: "a" }] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L12");
    });

    // v1.3 (docs/ekr-logic-spec.md §6 2026-08-11 追記): L12 の対象 op に field を追加
    it("on_cno_touch + field は L12 を警告する", () => {
        const l = logic([{ when: "on_cno_touch", slot: 1, do: [{ op: "field", at: "self", radius: "small", strength: "weak", seconds: 3 }] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L12");
    });

    it("on_cno_touch + notify/kill/teleport_other/pull/drag は L12 を警告しない (対象外の op)", () => {
        const l = logic([
            { when: "on_cno_touch", slot: 1, do: [{ op: "notify", text: "やあ", seconds: 3 }] },
            { when: "on_cno_touch", slot: 2, do: [{ op: "kill", target: "ctx" }] },
            { when: "on_cno_touch", slot: 3, do: [{ op: "teleport_other", target: "ctx", to: "self" }] },
            { when: "on_cno_touch", slot: 1, do: [{ op: "pull" }] },
            { when: "on_cno_touch", slot: 2, do: [{ op: "drag", seconds: 3 }] },
        ]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L12");
    });

    it("on_cno_touch 以外の when では L12 を警告しない (同じ op でも)", () => {
        const l = logic([{ when: "on_pet", do: [{ op: "cno_spawn", slot: 1, text: "!", size: 1, at: "self" }] }]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L12");
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

    // v1.1 (docs/ekr-logic-spec.md §6): dummy_spawn/corpse_spawn の正しい作法は警告0件
    it("on_pet 単発の dummy_spawn/corpse_spawn は何も警告しない", () => {
        const l = logic([
            {
                when: "on_pet",
                do: [
                    { op: "dummy_spawn", slot: 1, name: "ダミー", killable: true, at: "self" },
                    { op: "corpse_spawn", color: "random", at: "ctx" },
                ],
            },
        ]);
        expect(lintRoleLogic(l)).toEqual([]);
    });

    it("on_meeting_end で10.5秒待ってから dummy_spawn するのは警告0件 (L9 の代替案どおりの作法)", () => {
        const l = logic([
            {
                when: "on_meeting_end",
                do: [
                    { op: "wait", seconds: 10.5 },
                    { op: "dummy_spawn", slot: 1, name: "ダミー", killable: false, at: "self" },
                ],
            },
        ]);
        expect(lintRoleLogic(l)).toEqual([]);
    });

    it("dummy_spawn を3.1秒あけて2体出すのは警告0件 (L10 の代替案どおりの作法)", () => {
        const l = logic([
            {
                when: "on_game_start",
                do: [
                    { op: "dummy_spawn", slot: 1, name: "ダミー", killable: false, at: "self" },
                    { op: "wait", seconds: 3.1 },
                    { op: "dummy_spawn", slot: 2, name: "ダミー", killable: false, at: "self" },
                ],
            },
        ]);
        expect(lintRoleLogic(l)).toEqual([]);
    });

    // v1.2 (docs/ekr-logic-spec.md §6 2026-08-10 追記): on_cno_touch/marker_save/teleport_other/
    // portal_place の正しい作法は警告0件
    it("on_cno_touch でマーカー保存+相手ワープのみを行うのは警告0件", () => {
        const l = logic([
            {
                when: "on_cno_touch",
                slot: 1,
                do: [
                    { op: "marker_save", slot: 1, at: "self" },
                    { op: "teleport_other", target: "ctx", to: "marker1" },
                ],
            },
        ]);
        expect(lintRoleLogic(l)).toEqual([]);
    });

    it("on_game_start でポータルを1回だけ置くのは警告0件", () => {
        const l = logic([{ when: "on_game_start", do: [{ op: "portal_place", which: "a" }, { op: "portal_place", which: "b" }] }]);
        expect(lintRoleLogic(l)).toEqual([]);
    });

    // v1.3 (docs/ekr-logic-spec.md §6 2026-08-11 追記): pull/drag/field の正しい作法は警告0件
    it("on_kill でひとつだけ pull/drag/field を使うのは警告0件", () => {
        const l = logic([
            { when: "on_kill", do: [{ op: "pull" }] },
            { when: "on_kill", do: [{ op: "drag", seconds: 5 }] },
            { when: "on_kill", do: [{ op: "field", at: "self", radius: "medium", strength: "medium", seconds: 8 }] },
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
