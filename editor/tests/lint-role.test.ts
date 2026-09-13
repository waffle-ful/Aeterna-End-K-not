// lint-role.ts (§6 の 12 ルール — v1.1 で L9/L10・v1.2 で L11/L12 追加) のテスト。既に
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

    // v1.1 (§6): L1 の検知対象に dummy_spawn を追加
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

// v1.1 (§6): dummy_spawn 新設に伴う L9/L10 追加
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

// v1.2 (§6 2026-08-10 追記): L9 の対象 op を4種 (cno_spawn/cno_show/
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

    // v1.3 (§6 2026-08-11 追記): L9 の対象 op に field を追加
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

// v1.3 (§6 2026-08-11 追記): L13 = L3/L11 の兄弟 (pull/drag/field)
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

    // v1.3 (§6 2026-08-11 追記): L12 の対象 op に field を追加
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

// Wave 1 (§6 2026-08-11 併合): L14〜L17
describe("lint-role: L14 (ctx 無しイベント配下の ctx セレクタ)", () => {
    it("on_pet + kill(target:ctx) は L14 を警告する", () => {
        const l = logic([{ when: "on_pet", do: [{ op: "kill", target: "ctx" }] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L14");
    });

    it("at / to フィールドの ctx も検知する", () => {
        const atRule = logic([{ when: "on_game_start", do: [{ op: "cno_spawn", slot: 1, text: "!", size: 1, at: "ctx" }] }]);
        expect(ruleIds(lintRoleLogic(atRule))).toContain("L14");
        const toRule = logic([{ when: "on_second", do: [{ op: "teleport", to: "ctx" }] }]);
        expect(ruleIds(lintRoleLogic(toRule))).toContain("L14");
    });

    it("if の中にネストした ctx も検知する", () => {
        const nested: LogicNode = { op: "if", cond: { e: "lit", v: 1 }, then: [{ op: "corpse_spawn", color: "self", at: "ctx" }] };
        const l = logic([{ when: "on_vent_enter", do: [nested] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L14");
    });

    it("ctx を持つイベント (on_kill/on_death/on_report/on_cno_touch/on_attacked) では警告しない", () => {
        const l = logic([
            { when: "on_kill", do: [{ op: "kill", target: "ctx" }] },
            { when: "on_death", do: [{ op: "corpse_spawn", color: "self", at: "ctx" }] },
            { when: "on_report", do: [{ op: "teleport", to: "ctx" }] },
            { when: "on_cno_touch", slot: 1, do: [{ op: "marker_save", slot: 1, at: "ctx" }] },
            { when: "on_attacked", do: [{ op: "kill", target: "ctx" }] },
        ]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L14");
    });

    // フィールドを持たない ctx 暗黙 op (pull/drag) も L14 の対象
    it("ctx 無しイベント配下の pull / drag は L14 を警告する (引数が無くても ctx 依存のため)", () => {
        expect(ruleIds(lintRoleLogic(logic([{ when: "on_pet", do: [{ op: "pull" }] }])))).toContain("L14");
        expect(ruleIds(lintRoleLogic(logic([{ when: "on_game_start", do: [{ op: "drag", seconds: 3 }] }])))).toContain("L14");
        const nested: LogicNode = { op: "if", cond: { e: "lit", v: 1 }, then: [{ op: "pull" }] };
        expect(ruleIds(lintRoleLogic(logic([{ when: "on_vent_enter", do: [nested] }])))).toContain("L14");
    });

    it("ctx を持つイベント配下の pull / drag は警告しない", () => {
        const l = logic([
            { when: "on_attacked", do: [{ op: "pull" }] },
            { when: "on_kill", do: [{ op: "drag", seconds: 3 }] },
            { when: "on_cno_touch", slot: 1, do: [{ op: "pull" }] },
        ]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L14");
    });

    it("ctx を使わなければ ctx 無しイベントでも警告しない", () => {
        const l = logic([{ when: "on_pet", do: [{ op: "kill", target: "nearest" }, { op: "teleport", to: "random" }] }]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L14");
    });

    // Wave 2 (§6 2026-08-11 併合): inspect/reveal/arrow_show/vote_block/
    // exile の target:"ctx"、arrow_mark の at:"ctx" は selectorTokens の target/at/to 汎用スキャン
    // だけで拾える (新イベント2種を対象から外した以外、L14 の実装を変える必要が無い契約どおり)。
    it("ctx 無しイベント配下の inspect/reveal/arrow_show/vote_block/exile(target:ctx) / arrow_mark(at:ctx) は L14 を警告する", () => {
        expect(ruleIds(lintRoleLogic(logic([{ when: "on_pet", do: [{ op: "inspect", target: "ctx", depth: "role" }] }])))).toContain("L14");
        expect(ruleIds(lintRoleLogic(logic([{ when: "on_pet", do: [{ op: "reveal", target: "ctx" }] }])))).toContain("L14");
        expect(ruleIds(lintRoleLogic(logic([{ when: "on_pet", do: [{ op: "arrow_show", target: "ctx", seconds: 10 }] }])))).toContain("L14");
        expect(ruleIds(lintRoleLogic(logic([{ when: "on_pet", do: [{ op: "arrow_mark", at: "ctx", seconds: 10 }] }])))).toContain("L14");
        expect(ruleIds(lintRoleLogic(logic([{ when: "on_pet", do: [{ op: "vote_block", target: "ctx" }] }])))).toContain("L14");
        expect(ruleIds(lintRoleLogic(logic([{ when: "on_pet", do: [{ op: "exile", target: "ctx" }] }])))).toContain("L14");
    });

    it("on_meeting_vote / on_meeting_pick 配下の target:ctx は L14 を警告しない (新イベント2種は ctx あり)", () => {
        const l = logic([
            { when: "on_meeting_vote", do: [{ op: "exile", target: "ctx" }] },
            { when: "on_meeting_pick", do: [{ op: "vote_block", target: "ctx" }] },
        ]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L14");
    });
});

describe("lint-role: L15 (未保存マーカーの行き先)", () => {
    it("marker_save が無いのに teleport(to:markerN) すると警告する", () => {
        const l = logic([{ when: "on_pet", do: [{ op: "teleport", to: "marker1" }] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L15");
    });

    it("別の rule に marker_save(同じ番号) があれば警告しない", () => {
        const l = logic([
            { when: "on_game_start", do: [{ op: "marker_save", slot: 2, at: "self" }] },
            { when: "on_pet", do: [{ op: "teleport", to: "marker2" }] },
        ]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L15");
    });

    it("番号が違う marker_save では救われない", () => {
        const l = logic([
            { when: "on_game_start", do: [{ op: "marker_save", slot: 1, at: "self" }] },
            { when: "on_pet", do: [{ op: "teleport", to: "marker2" }] },
        ]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L15");
    });

    it("teleport_other(to:markerN) / field(at:markerN) も対象", () => {
        const other = logic([{ when: "on_kill", do: [{ op: "teleport_other", target: "ctx", to: "marker3" }] }]);
        expect(ruleIds(lintRoleLogic(other))).toContain("L15");
        const field = logic([{ when: "on_kill", do: [{ op: "field", at: "marker4", radius: "small", strength: "weak", seconds: 3 }] }]);
        expect(ruleIds(lintRoleLogic(field))).toContain("L15");
    });
});

describe("lint-role: L16 (未生成の cnoN / 未保存の savedN 参照)", () => {
    it("cno_spawn が無いのに teleport(to:cnoN) すると警告する", () => {
        const l = logic([{ when: "on_pet", do: [{ op: "teleport", to: "cno2" }] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L16");
    });

    it("同じ番号の cno_spawn / dummy_spawn があれば警告しない", () => {
        const spawn = logic([
            { when: "on_game_start", do: [{ op: "cno_spawn", slot: 2, text: "!", size: 1, at: "self" }] },
            { when: "on_pet", do: [{ op: "teleport", to: "cno2" }] },
        ]);
        expect(ruleIds(lintRoleLogic(spawn))).not.toContain("L16");
        const dummy = logic([
            { when: "on_game_start", do: [{ op: "dummy_spawn", slot: 3, name: "ダミー", killable: false, at: "self" }] },
            { when: "on_pet", do: [{ op: "marker_save", slot: 1, at: "cno3" }] },
        ]);
        expect(ruleIds(lintRoleLogic(dummy))).not.toContain("L16");
    });

    it("remember が無いのに savedN を参照すると警告する", () => {
        const l = logic([{ when: "on_pet", do: [{ op: "kill", target: "saved1" }] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L16");
    });

    it("同じ番号の remember があれば警告しない (番号違いは警告する)", () => {
        const ok = logic([
            { when: "on_kill", do: [{ op: "remember", slot: 1, target: "ctx" }] },
            { when: "on_pet", do: [{ op: "kill", target: "saved1" }] },
        ]);
        expect(ruleIds(lintRoleLogic(ok))).not.toContain("L16");
        const ng = logic([
            { when: "on_kill", do: [{ op: "remember", slot: 1, target: "ctx" }] },
            { when: "on_pet", do: [{ op: "kill", target: "saved2" }] },
        ]);
        expect(ruleIds(lintRoleLogic(ng))).toContain("L16");
    });

    it("cno_move/cno_despawn の slot 指定は L16 の対象外 (トークン参照ではない)", () => {
        const l = logic([{ when: "on_pet", do: [{ op: "cno_move", slot: 1, dx: 1, dy: 0 }, { op: "cno_despawn", slot: 2 }] }]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L16");
    });
});

describe("lint-role: L17 (on_attacked 配下・wait より後の cancel_attack)", () => {
    it("wait のあとの cancel_attack は警告する", () => {
        const l = logic([{ when: "on_attacked", do: [{ op: "wait", seconds: 1 }, { op: "cancel_attack" }] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L17");
    });

    it("wait より前の cancel_attack は警告しない", () => {
        const l = logic([{ when: "on_attacked", do: [{ op: "cancel_attack" }, { op: "wait", seconds: 1 }, { op: "notify", text: "ふせいだ", seconds: 3 }] }]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L17");
    });

    it("if の中の cancel_attack も訪問順で判定する", () => {
        const nested: LogicNode = { op: "if", cond: { e: "lit", v: 1 }, then: [{ op: "cancel_attack" }] };
        const after = logic([{ when: "on_attacked", do: [{ op: "wait", seconds: 0.5 }, nested] }]);
        expect(ruleIds(lintRoleLogic(after))).toContain("L17");
        const before = logic([{ when: "on_attacked", do: [nested, { op: "wait", seconds: 0.5 }] }]);
        expect(ruleIds(lintRoleLogic(before))).not.toContain("L17");
    });
});

// Wave 2 (§6 2026-08-11 併合): L18〜L20 + L16 拡張
describe("lint-role: L18 (会議専用 op が会議系イベント以外の rule 配下)", () => {
    it("on_pet + vote_block/vote_swap/exile はそれぞれ L18 を警告する", () => {
        expect(ruleIds(lintRoleLogic(logic([{ when: "on_pet", do: [{ op: "vote_block", target: "ctx" }] }])))).toContain("L18");
        expect(ruleIds(lintRoleLogic(logic([{ when: "on_pet", do: [{ op: "vote_swap" }] }])))).toContain("L18");
        expect(ruleIds(lintRoleLogic(logic([{ when: "on_pet", do: [{ op: "exile", target: "self" }] }])))).toContain("L18");
    });

    it("on_meeting_start / on_meeting_vote / on_meeting_pick 配下では L18 を警告しない", () => {
        const l = logic([
            { when: "on_meeting_start", do: [{ op: "vote_block", target: "ctx" }] },
            { when: "on_meeting_vote", do: [{ op: "vote_swap" }] },
            { when: "on_meeting_pick", do: [{ op: "exile", target: "ctx" }] },
        ]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L18");
    });

    it("if の中にネストした vote_block も検知する", () => {
        const nested: LogicNode = { op: "if", cond: { e: "lit", v: 1 }, then: [{ op: "vote_block", target: "ctx" }] };
        const l = logic([{ when: "on_kill", do: [nested] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L18");
    });

    it("cancel_vote は L18 の対象外 (検証で構造的 reject されるため重ねて警告しない)", () => {
        const l = logic([{ when: "on_meeting_vote", do: [{ op: "cancel_vote" }] }]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L18");
    });

    it("on_chat 配下では L18 を警告しない (on_chat は会議中にしか発火しないため)", () => {
        const l = logic([{ when: "on_chat", do: [{ op: "exile", target: "ctx" }] }]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L18");
    });
});

describe("lint-role: L19 (on_meeting_vote 配下・wait より後の cancel_vote)", () => {
    it("wait のあとの cancel_vote は警告する", () => {
        const l = logic([{ when: "on_meeting_vote", do: [{ op: "wait", seconds: 1 }, { op: "cancel_vote" }] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L19");
    });

    it("wait より前の cancel_vote は警告しない", () => {
        const l = logic([{ when: "on_meeting_vote", do: [{ op: "cancel_vote" }, { op: "wait", seconds: 1 }] }]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L19");
    });

    it("if の中の cancel_vote も訪問順で判定する", () => {
        const nested: LogicNode = { op: "if", cond: { e: "lit", v: 1 }, then: [{ op: "cancel_vote" }] };
        const after = logic([{ when: "on_meeting_vote", do: [{ op: "wait", seconds: 0.5 }, nested] }]);
        expect(ruleIds(lintRoleLogic(after))).toContain("L19");
        const before = logic([{ when: "on_meeting_vote", do: [nested, { op: "wait", seconds: 0.5 }] }]);
        expect(ruleIds(lintRoleLogic(before))).not.toContain("L19");
    });
});

describe("lint-role: L22 (会議で起きないこうげきのしゅるい × 会議専用のちから — R2)", () => {
    it("kind:kill の下の exile は警告する", () => {
        const l = logic([{ when: "on_attacked", kind: "kill", do: [{ op: "exile", target: "ctx" }] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L22");
    });

    it("kind:guess の下なら警告しない (推測は会議中に起きる)", () => {
        const l = logic([{ when: "on_attacked", kind: "guess", do: [{ op: "exile", target: "ctx" }] }]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L22");
    });

    it("kind 省略 (すべて) なら警告しない — 推測も含まれるため", () => {
        const l = logic([{ when: "on_attacked", do: [{ op: "exile", target: "ctx" }] }]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L22");
    });
});

describe("lint-role: L23 (会議中に決まる死にかた × タスク中しか効かないちから — R2)", () => {
    it("cause:vote の下の teleport は警告する", () => {
        const l = logic([{ when: "on_death", cause: "vote", do: [{ op: "teleport", to: "marker1" }] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L23");
    });

    it("cause:guess の下の drag も警告する", () => {
        const l = logic([{ when: "on_death", cause: "guess", do: [{ op: "drag", seconds: 3 }] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L23");
    });

    it("cause:vote でも notify は警告しない (会議中も有効な op)", () => {
        const l = logic([{ when: "on_death", cause: "vote", do: [{ op: "notify", target: "self", text: "やられた", seconds: 3 }] }]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L23");
    });

    it("矢印も会議中は効かないので対象に含める (L23 の対象漏れを埋める)", () => {
        const l = logic([{ when: "on_death", cause: "vote", do: [{ op: "arrow_show", target: "ctx", seconds: 5 }] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L23");
    });

    it("cause:kill (タスク中に決まる死にかた) なら teleport でも警告しない", () => {
        const l = logic([{ when: "on_death", cause: "kill", do: [{ op: "teleport", to: "marker1" }] }]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L23");
    });

    it("cause:vote の下の recruit も警告する (会議中 op 白名単の補集合)", () => {
        const l = logic([{ when: "on_death", cause: "vote", do: [{ op: "recruit", target: "nearest" }] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L23");
    });

    it("cause:vote の下の addon_give/addon_remove も警告する (Wave 10・会議中 op 白名単の補集合)", () => {
        const give = logic([{ when: "on_death", cause: "vote", do: [{ op: "addon_give", target: "nearest", addon: "Flash" }] }]);
        expect(ruleIds(lintRoleLogic(give))).toContain("L23");
        const remove = logic([{ when: "on_death", cause: "vote", do: [{ op: "addon_remove", target: "nearest", addon: "Flash" }] }]);
        expect(ruleIds(lintRoleLogic(remove))).toContain("L23");
    });
});

describe("lint-role: L21 (wait より後の exile — L17/L19 の兄弟)", () => {
    it("wait のあとの exile は警告する", () => {
        const l = logic([{ when: "on_meeting_pick", do: [{ op: "wait", seconds: 2 }, { op: "exile", target: "ctx" }] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L21");
    });

    it("wait より前の exile は警告しない", () => {
        const l = logic([{ when: "on_meeting_pick", do: [{ op: "exile", target: "ctx" }, { op: "wait", seconds: 2 }] }]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L21");
    });

    it("if の中の exile も訪問順で判定する", () => {
        const nested: LogicNode = { op: "if", cond: { e: "lit", v: 1 }, then: [{ op: "exile", target: "ctx" }] };
        const after = logic([{ when: "on_meeting_pick", do: [{ op: "wait", seconds: 0.5 }, nested] }]);
        expect(ruleIds(lintRoleLogic(after))).toContain("L21");
        const before = logic([{ when: "on_meeting_pick", do: [nested, { op: "wait", seconds: 0.5 }] }]);
        expect(ruleIds(lintRoleLogic(before))).not.toContain("L21");
    });

    it("vote_block / vote_swap は L21 の対象外 (予約は集計時に読まれるので wait を挟んでも成立しうる)", () => {
        const block = logic([{ when: "on_meeting_pick", do: [{ op: "wait", seconds: 2 }, { op: "vote_block", target: "ctx" }] }]);
        expect(ruleIds(lintRoleLogic(block))).not.toContain("L21");
        const swap = logic([{ when: "on_meeting_pick", do: [{ op: "wait", seconds: 2 }, { op: "vote_swap" }] }]);
        expect(ruleIds(lintRoleLogic(swap))).not.toContain("L21");
    });
});

describe("lint-role: L20 (on_second 配下の arrow_show/arrow_mark/inspect/reveal — L3/L4 の兄弟)", () => {
    it("on_second + arrow_show/arrow_mark/inspect/reveal はそれぞれ L20 を警告する", () => {
        expect(ruleIds(lintRoleLogic(logic([{ when: "on_second", do: [{ op: "arrow_show", target: "nearest", seconds: 10 }] }])))).toContain("L20");
        expect(ruleIds(lintRoleLogic(logic([{ when: "on_second", do: [{ op: "arrow_mark", at: "ctx", seconds: 10 }] }])))).toContain("L20");
        expect(ruleIds(lintRoleLogic(logic([{ when: "on_second", do: [{ op: "inspect", target: "nearest", depth: "role" }] }])))).toContain("L20");
        expect(ruleIds(lintRoleLogic(logic([{ when: "on_second", do: [{ op: "reveal", target: "nearest" }] }])))).toContain("L20");
    });

    it("on_second 以外の when では L20 を警告しない", () => {
        const l = logic([
            { when: "on_kill", do: [{ op: "arrow_show", target: "ctx", seconds: 10 }] },
            { when: "on_pet", do: [{ op: "inspect", target: "nearest", depth: "team" }] },
        ]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L20");
    });
});

describe("lint-role: L16 拡張 (vote_swap の暗黙 saved1/saved2 参照)", () => {
    it("remember(1)/remember(2) が無い vote_swap は L16 を警告する", () => {
        const l = logic([{ when: "on_meeting_pick", do: [{ op: "vote_swap" }] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L16");
    });

    it("remember(1) だけ (2が無い) でも L16 を警告する", () => {
        const l = logic([
            { when: "on_meeting_vote", do: [{ op: "remember", slot: 1, target: "ctx" }] },
            { when: "on_meeting_pick", do: [{ op: "vote_swap" }] },
        ]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L16");
    });

    it("別の rule に remember(1) と remember(2) が両方あれば警告しない", () => {
        const l = logic([
            { when: "on_meeting_vote", do: [{ op: "remember", slot: 1, target: "ctx" }] },
            { when: "on_meeting_pick", do: [{ op: "remember", slot: 2, target: "ctx" }, { op: "vote_swap" }] },
        ]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L16");
    });

    it("vote_swap を使わなければ remember の有無に関わらず警告しない", () => {
        const l = logic([{ when: "on_meeting_vote", do: [{ op: "remember", slot: 1, target: "ctx" }] }]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L16");
    });
});

describe("lint-role: 問題のないロジックは警告0件", () => {
    // Wave 1 で L14 (ctx 無しイベント配下の ctx セレクタ) が入ったため、on_pet の例では ctx を
    // 使わない形に直した (on_pet には「あいて」がいないので、元の形は警告が出るのが正しい)。
    it("on_pet 単発の kill/teleport/notify/cno_spawn/cno_show は何も警告しない", () => {
        const l = logic([
            {
                when: "on_pet",
                do: [
                    { op: "kill", target: "nearest" },
                    { op: "teleport", to: "random" },
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

    // v1.1 (§6): dummy_spawn/corpse_spawn の正しい作法は警告0件
    it("on_pet 単発の dummy_spawn/corpse_spawn は何も警告しない", () => {
        const l = logic([
            {
                when: "on_pet",
                do: [
                    { op: "dummy_spawn", slot: 1, name: "ダミー", killable: true, at: "self" },
                    // at:"ctx" は Wave 1 の L14 対象 (on_pet に「あいて」はいない) — self へ変更
                    { op: "corpse_spawn", color: "random", at: "self" },
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

    // v1.2 (§6 2026-08-10 追記): on_cno_touch/marker_save/teleport_other/
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

    // Wave 1 (§6 2026-08-11 併合): 反射役職の模範形は警告0件
    it("on_attacked で「ふせぐ→おぼえる→反撃」と組むのは警告0件", () => {
        const l = logic([
            {
                when: "on_attacked",
                do: [
                    { op: "cancel_attack" },
                    { op: "remember", slot: 1, target: "ctx" },
                    { op: "notify", text: "ふせいだ！", seconds: 3, target: "all" },
                    { op: "kill", target: "saved1" },
                ],
            },
        ]);
        expect(lintRoleLogic(l)).toEqual([]);
    });

    // v1.3 (§6 2026-08-11 追記): pull/drag/field の正しい作法は警告0件
    it("on_kill でひとつだけ pull/drag/field を使うのは警告0件", () => {
        const l = logic([
            { when: "on_kill", do: [{ op: "pull" }] },
            { when: "on_kill", do: [{ op: "drag", seconds: 5 }] },
            { when: "on_kill", do: [{ op: "field", at: "self", radius: "medium", strength: "medium", seconds: 8 }] },
        ]);
        expect(lintRoleLogic(l)).toEqual([]);
    });

    // Wave 2 (§6 2026-08-11 併合): 会議・投票の模範形は警告0件
    it("on_meeting_vote で「おぼえる→票をつかわずにえらぶ」と組むのは警告0件", () => {
        const l = logic([
            {
                when: "on_meeting_vote",
                do: [
                    { op: "remember", slot: 1, target: "ctx" },
                    { op: "cancel_vote" },
                    { op: "notify", text: "また今度…", seconds: 3 },
                ],
            },
        ]);
        expect(lintRoleLogic(l)).toEqual([]);
    });

    it("on_meeting_pick で remember(1)/remember(2) を両方入れてから vote_swap するのは警告0件", () => {
        const l = logic([
            { when: "on_meeting_vote", do: [{ op: "remember", slot: 1, target: "ctx" }] },
            { when: "on_meeting_pick", do: [{ op: "remember", slot: 2, target: "ctx" }, { op: "vote_swap" }] },
        ]);
        expect(lintRoleLogic(l)).toEqual([]);
    });

    it("on_meeting_start でひとつだけ vote_block/exile を使うのは警告0件", () => {
        const l = logic([
            { when: "on_meeting_start", do: [{ op: "vote_block", target: "nearest" }] },
            { when: "on_meeting_vote", do: [{ op: "exile", target: "ctx" }] },
        ]);
        expect(lintRoleLogic(l)).toEqual([]);
    });

    it("on_kill でひとつだけ inspect/reveal/arrow_show/arrow_mark/arrow_hide を使うのは警告0件", () => {
        const l = logic([
            { when: "on_kill", do: [{ op: "inspect", target: "ctx", depth: "role", failChance: 10, noise: 2 }] },
            { when: "on_kill", do: [{ op: "reveal", target: "ctx" }] },
            { when: "on_kill", do: [{ op: "arrow_show", target: "ctx", seconds: 30 }] },
            { when: "on_kill", do: [{ op: "arrow_mark", at: "ctx", seconds: 30 }] },
            { when: "on_kill", do: [{ op: "arrow_hide" }] },
            { when: "on_pet", do: [{ op: "vote_weight_set", value: 2 }] },
        ]);
        expect(lintRoleLogic(l)).toEqual([]);
    });
});

// Wave 3 (§6 2026-08-14) — L24/L25 は on_var の監視変数を扱うので、
// variables 宣言込みの RoleLogic を組む専用ヘルパーを使う (logic() は variables:[] 固定のため)。
function logicWithVars(variables: RoleLogic["variables"], rules: RoleLogic["rules"]): RoleLogic {
    return { version: 1, variables, rules };
}

describe("lint-role: L24 (on_var 配下で自分が監視している変数を書き換えている)", () => {
    it("on_var の中で監視変数そのものを var_set していると L24 を警告する", () => {
        const l = logicWithVars(
            [{ name: "カウント", init: 0 }],
            [{ when: "on_var", var: "カウント", cmp: "eq", value: 5, do: [{ op: "var_set", name: "カウント", value: { e: "lit", v: 0 } }] }],
        );
        expect(ruleIds(lintRoleLogic(l))).toContain("L24");
    });

    it("on_var の中で監視変数を var_add していても L24 を警告する (if の中にネストしていても検知)", () => {
        const nested: LogicNode = {
            op: "if",
            cond: { e: "lit", v: 1 },
            then: [{ op: "var_add", name: "カウント", delta: { e: "lit", v: 1 } }],
        };
        const l = logicWithVars(
            [{ name: "カウント", init: 0 }],
            [{ when: "on_var", var: "カウント", cmp: "eq", value: 5, do: [nested] }],
        );
        expect(ruleIds(lintRoleLogic(l))).toContain("L24");
    });

    it("on_var の中で別の変数を書き換えても L24 は警告しない", () => {
        const l = logicWithVars(
            [{ name: "カウント", init: 0 }, { name: "べつ", init: 0 }],
            [{ when: "on_var", var: "カウント", cmp: "eq", value: 5, do: [{ op: "var_set", name: "べつ", value: { e: "lit", v: 1 } }] }],
        );
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L24");
    });
});

describe("lint-role: L25 (on_var の監視変数・progress.text の参照変数がどこでも書かれない)", () => {
    it("on_var の監視変数がどの rule でも var_set/var_add されていなければ L25 を警告する", () => {
        const l = logicWithVars(
            [{ name: "カウント", init: 0 }],
            [{ when: "on_var", var: "カウント", cmp: "eq", value: 5, do: [{ op: "notify", text: "!", seconds: 1 }] }],
        );
        expect(ruleIds(lintRoleLogic(l))).toContain("L25");
    });

    it("別の rule (on_pet 等) で監視変数が書かれていれば L25 は警告しない", () => {
        const l = logicWithVars(
            [{ name: "カウント", init: 0 }],
            [
                { when: "on_var", var: "カウント", cmp: "eq", value: 5, do: [{ op: "notify", text: "!", seconds: 1 }] },
                { when: "on_pet", do: [{ op: "var_add", name: "カウント", delta: { e: "lit", v: 1 } }] },
            ],
        );
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L25");
    });

    it("progress.text が {変数名} を参照していて、その変数がどこでも書かれていなければ L25 を警告する", () => {
        const l = logicWithVars([{ name: "たま", init: 0 }], [{ when: "on_pet", do: [{ op: "stop" }] }]);
        expect(ruleIds(lintRoleLogic(l, "のこり{たま}発"))).toContain("L25");
    });

    it("progress.text が参照する変数がどこかで書かれていれば L25 を警告しない", () => {
        const l = logicWithVars(
            [{ name: "たま", init: 0 }],
            [{ when: "on_pet", do: [{ op: "var_add", name: "たま", delta: { e: "lit", v: 1 } }] }],
        );
        expect(ruleIds(lintRoleLogic(l, "のこり{たま}発"))).not.toContain("L25");
    });

    it("progress.text が宣言されていない名前を {◯◯} で参照していても L25 は警告しない (固定文字列は正当ユース)", () => {
        const l = logicWithVars([], [{ when: "on_pet", do: [{ op: "stop" }] }]);
        expect(ruleIds(lintRoleLogic(l, "のこり{たま}発"))).not.toContain("L25");
    });

    it("progressText を渡さなければ progress 側の L25 検査はスキップされる", () => {
        const l = logicWithVars([{ name: "たま", init: 0 }], [{ when: "on_pet", do: [{ op: "stop" }] }]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L25");
    });
});

// ---------------------------------------------------------------------------
// Wave 4 (§6 2026-08-25) — L26/L27 + L12/L14 の対象拡大
// ---------------------------------------------------------------------------

describe("lint-role: L26 (linked 参照があるのにどの rule にも link op が無い)", () => {
    it("kill(target:linked) があって link が無ければ L26 を警告する", () => {
        const l = logic([{ when: "on_kill", do: [{ op: "kill", target: "linked" }] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L26");
    });

    it("on_far(who:linked) があって link が無ければ L26 を警告する", () => {
        const l = logic([{ when: "on_far", radius: "medium", who: "linked", do: [{ op: "stop" }] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L26");
    });

    it("on_linked_death rule があって link が無ければ L26 を警告する", () => {
        const l = logic([{ when: "on_linked_death", do: [{ op: "stop" }] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L26");
    });

    it("recruit(target:linked) も linked トークン参照として検知する", () => {
        const l = logic([{ when: "on_kill", do: [{ op: "recruit", target: "linked" }] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L26");
    });

    // Wave 7 (§5): win/win_join の linked 参照は selectorTokens の
    // target 総称読みで L26 に自動で乗る (新ルール無し) — その契約主張の証明。
    it("win(target:linked) / win_join(target:linked) も linked トークン参照として検知する", () => {
        expect(ruleIds(lintRoleLogic(logic([{ when: "on_kill", do: [{ op: "win", target: "linked" }] }])))).toContain("L26");
        expect(ruleIds(lintRoleLogic(logic([{ when: "on_kill", do: [{ op: "win_join", target: "linked" }] }])))).toContain("L26");
    });

    it("別の rule に link op があれば L26 は警告しない (rule をまたいで解決)", () => {
        const l = logic([
            { when: "on_kill", do: [{ op: "link", target: "ctx" }] },
            { when: "on_linked_death", do: [{ op: "kill", target: "linked" }] },
            { when: "on_far", radius: "small", who: "linked", do: [{ op: "stop" }] },
        ]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L26");
    });

    it("if の入れ子の中の link op も「置いてある」と数える", () => {
        const l = logic([
            {
                when: "on_kill",
                do: [{ op: "if", cond: { e: "lit", v: 1 }, then: [{ op: "link", target: "ctx" }] }],
            },
            { when: "on_linked_death", do: [{ op: "stop" }] },
        ]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L26");
    });

    it("on_far(who:saved1) は linked を参照しないので L26 を警告しない", () => {
        const l = logic([{ when: "on_far", radius: "small", who: "saved1", do: [{ op: "stop" }] }]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L26");
    });

    it("on_near(who:linked) も link 無しでは一生発火しないので L26 を警告する (契約 §6 改定 2026-08-25)", () => {
        const l = logic([{ when: "on_near", radius: "small", who: "linked", do: [{ op: "stop" }] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L26");
    });

    it("on_near(who:linked) でも link op がどこかにあれば L26 を警告しない", () => {
        const l = logic([
            { when: "on_near", radius: "small", who: "linked", do: [{ op: "stop" }] },
            { when: "on_pet", do: [{ op: "link", target: "nearest" }] },
        ]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L26");
    });
});

describe("lint-role: L27 (on_second 配下の recruit — L5 の兄弟)", () => {
    it("on_second + recruit (直下) は L27 を警告する", () => {
        const l = logic([{ when: "on_second", do: [{ op: "recruit", target: "nearest" }] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L27");
    });

    it("on_second + recruit (if の中にネスト) も検知する", () => {
        const l = logic([
            {
                when: "on_second",
                do: [{ op: "if", cond: { e: "lit", v: 1 }, then: [{ op: "recruit", target: "nearest" }] }],
            },
        ]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L27");
    });

    it("on_pet (on_second 以外) の recruit は L27 を警告しない", () => {
        const l = logic([{ when: "on_pet", do: [{ op: "recruit", target: "nearest" }] }]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L27");
    });
});

// Wave 5 (§4 2026-08-27) — L28
describe("lint-role: L28 (on_second 配下の effect_give — L5/L27 の兄弟)", () => {
    it("on_second + effect_give (直下) は L28 を警告する", () => {
        const l = logic([{ when: "on_second", do: [{ op: "effect_give", target: "nearest", kind: "slow", seconds: 5 }] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L28");
    });

    it("on_second + effect_give (if の中にネスト) も検知する", () => {
        const l = logic([
            {
                when: "on_second",
                do: [{ op: "if", cond: { e: "lit", v: 1 }, then: [{ op: "effect_give", target: "nearest", kind: "freeze", seconds: 3 }] }],
            },
        ]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L28");
    });

    it("on_second 以外の effect_give は L28 を警告しない", () => {
        const l = logic([{ when: "on_pet", do: [{ op: "effect_give", target: "nearest", kind: "blind", seconds: 5 }] }]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L28");
    });
});

describe("lint-role: L12 の Wave 4 対象拡大 (on_near/on_room_enter/on_room_exit)", () => {
    it("on_near + cno_spawn は L12 を警告する", () => {
        const l = logic([{ when: "on_near", radius: "small", do: [{ op: "cno_spawn", slot: 1, text: "!", size: 1, at: "self" }] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L12");
    });

    it("on_room_enter + dummy_spawn / on_room_exit + field も L12 を警告する", () => {
        const enter = logic([{ when: "on_room_enter", do: [{ op: "dummy_spawn", slot: 1, name: "ダミー", killable: false, at: "self" }] }]);
        expect(ruleIds(lintRoleLogic(enter))).toContain("L12");
        const exit = logic([{ when: "on_room_exit", do: [{ op: "field", at: "self", radius: "small", strength: "weak", seconds: 3 }] }]);
        expect(ruleIds(lintRoleLogic(exit))).toContain("L12");
    });

    it("on_far は L12 の対象外 (往復が要るぶん頻度が落ちる — 契約 §6 の改定リストどおり)", () => {
        const l = logic([{ when: "on_far", radius: "small", who: "saved1", do: [{ op: "cno_spawn", slot: 1, text: "!", size: 1, at: "self" }] }]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L12");
    });

    it("on_near + notify (生成系以外) は L12 を警告しない", () => {
        const l = logic([{ when: "on_near", radius: "small", do: [{ op: "notify", text: "!", seconds: 1 }] }]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L12");
    });
});

describe("lint-role: L14 の Wave 4 対象拡大 (on_room_enter/on_room_exit は ctx 無し)", () => {
    it("on_room_enter + kill(target:ctx) は L14 を警告する", () => {
        const l = logic([{ when: "on_room_enter", do: [{ op: "kill", target: "ctx" }] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L14");
    });

    it("on_room_exit + pull (ctx 暗黙 op) も L14 を警告する", () => {
        const l = logic([{ when: "on_room_exit", do: [{ op: "pull" }] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L14");
    });

    it("on_near/on_far/on_linked_death は ctx を持つので L14 を警告しない", () => {
        expect(ruleIds(lintRoleLogic(logic([{ when: "on_near", radius: "small", do: [{ op: "kill", target: "ctx" }] }])))).not.toContain("L14");
        expect(ruleIds(lintRoleLogic(logic([{ when: "on_far", radius: "small", who: "saved1", do: [{ op: "teleport", to: "ctx" }] }])))).not.toContain("L14");
        expect(ruleIds(lintRoleLogic(logic([{ when: "on_linked_death", do: [{ op: "kill", target: "self" }, { op: "pull" }] }])))).not.toContain("L14");
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

// Wave 6 (§5/§3 2026-08-29) — L29 + CTXLESS_WHENS (on_revive)
describe("lint-role: L29 (on_second 配下の cno_launch — L5/L27/L28 の兄弟)", () => {
    it("on_second + cno_launch (直下) は L29 を警告する", () => {
        const l = logic([{ when: "on_second", do: [{ op: "cno_launch", slot: 1, dir: "move" }] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L29");
    });

    it("on_second + cno_launch (if の中にネスト) も検知する", () => {
        const l = logic([
            {
                when: "on_second",
                do: [{ op: "if", cond: { e: "lit", v: 1 }, then: [{ op: "cno_launch", slot: 1, dir: "ctx" }] }],
            },
        ]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L29");
    });

    it("on_pet (on_second 以外) の cno_launch は L29 を警告しない", () => {
        const l = logic([{ when: "on_pet", do: [{ op: "cno_launch", slot: 1, dir: "move" }] }]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L29");
    });
});

describe("lint-role: L14 の Wave 6 対象拡大 (on_revive は ctx 無し)", () => {
    it("on_revive + kill(target:ctx) は L14 を警告する", () => {
        const l = logic([{ when: "on_revive", do: [{ op: "kill", target: "ctx" }] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L14");
    });

    it("on_sabotage は ctx を持つので L14 を警告しない", () => {
        const l = logic([{ when: "on_sabotage", do: [{ op: "notify", text: "!", seconds: 1, target: "ctx" }] }]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L14");
    });
});

// Wave 8 (§4 2026-08-30) — L30 + L16 拡張 (forget)
describe("lint-role: L30 (on_chat 配下・タスク中しか効かないちから — L23 の兄弟)", () => {
    it("on_chat + teleport は L30 を警告する", () => {
        const l = logic([{ when: "on_chat", do: [{ op: "teleport", to: "random" }] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L30");
    });

    it("on_chat + kill は L30 を警告する", () => {
        const l = logic([{ when: "on_chat", do: [{ op: "kill", target: "ctx" }] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L30");
    });

    it("on_chat + notify/remember/forget (会議中も有効な op) は L30 を警告しない", () => {
        const l = logic([
            { when: "on_chat", do: [{ op: "notify", text: "やあ", seconds: 3, target: "ctx" }] },
            { when: "on_chat", do: [{ op: "remember", slot: 1, target: "ctx" }] },
            { when: "on_chat", do: [{ op: "forget", slot: 1 }] },
        ]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L30");
    });

    it("on_chat + exile/vote_block (会議専用 op) は L30 を警告しない (TASK_ONLY_LINT_OPS の対象外)", () => {
        const l = logic([
            { when: "on_chat", do: [{ op: "exile", target: "ctx" }] },
            { when: "on_chat", do: [{ op: "vote_block", target: "ctx" }] },
        ]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L30");
    });

    it("if の中にネストした teleport も検知する", () => {
        const nested: LogicNode = { op: "if", cond: { e: "lit", v: 1 }, then: [{ op: "teleport", to: "random" }] };
        const l = logic([{ when: "on_chat", do: [nested] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L30");
    });

    it("on_chat 以外の when では同じ op でも L30 を警告しない", () => {
        const l = logic([{ when: "on_pet", do: [{ op: "teleport", to: "random" }] }]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L30");
    });

    it("on_chat + effect_give も警告する (会議中 op 白名単の補集合)", () => {
        const l = logic([{ when: "on_chat", do: [{ op: "effect_give", target: "nearest", kind: "slow", seconds: 5 }] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L30");
    });

    it("on_chat + addon_give/addon_remove も警告する (Wave 10・会議中 op 白名単の補集合)", () => {
        const give = logic([{ when: "on_chat", do: [{ op: "addon_give", target: "ctx", addon: "Flash" }] }]);
        expect(ruleIds(lintRoleLogic(give))).toContain("L30");
        const remove = logic([{ when: "on_chat", do: [{ op: "addon_remove", target: "ctx", addon: "Flash" }] }]);
        expect(ruleIds(lintRoleLogic(remove))).toContain("L30");
    });
});

describe("lint-role: L16 拡張 (forget(N) があるのに remember(N) が無い)", () => {
    it("remember が無いのに forget(1) すると警告する", () => {
        const l = logic([{ when: "on_chat", do: [{ op: "forget", slot: 1 }] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L16");
    });

    it("同じ番号の remember が別の rule にあれば警告しない (番号違いは警告する)", () => {
        const ok = logic([
            { when: "on_pet", do: [{ op: "remember", slot: 1, target: "nearest" }] },
            { when: "on_chat", do: [{ op: "forget", slot: 1 }] },
        ]);
        expect(ruleIds(lintRoleLogic(ok))).not.toContain("L16");
        const ng = logic([
            { when: "on_pet", do: [{ op: "remember", slot: 1, target: "nearest" }] },
            { when: "on_chat", do: [{ op: "forget", slot: 2 }] },
        ]);
        expect(ruleIds(lintRoleLogic(ng))).toContain("L16");
    });

    it("forget を使わなければ remember の有無に関わらず警告しない", () => {
        const l = logic([{ when: "on_pet", do: [{ op: "remember", slot: 1, target: "nearest" }] }]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L16");
    });
});

// Wave 9 (§6 2026-09-12 併合): L14 の on_pet 条件分岐 + L31〜L33
describe("lint-role: L14 の Wave 9 対象分岐 (on_pet は basis == \"shapeshift\" のときだけ ctx あり)", () => {
    it("basis 省略 (docContext 無し) では on_pet + target:ctx は従来どおり L14 を警告する", () => {
        const l = logic([{ when: "on_pet", do: [{ op: "kill", target: "ctx" }] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L14");
    });

    it("docContext.basis == \"pet\" でも on_pet + target:ctx は L14 を警告する", () => {
        const l = logic([{ when: "on_pet", do: [{ op: "kill", target: "ctx" }] }]);
        expect(ruleIds(lintRoleLogic(l, undefined, { basis: "pet" }))).toContain("L14");
    });

    it("docContext.basis == \"shapeshift\" なら on_pet + target:ctx は L14 を警告しない", () => {
        const l = logic([{ when: "on_pet", do: [{ op: "kill", target: "ctx" }] }]);
        expect(ruleIds(lintRoleLogic(l, undefined, { basis: "shapeshift" }))).not.toContain("L14");
    });

    it("basis == \"shapeshift\" でも on_pet 以外の ctx 無しイベントは引き続き L14 を警告する", () => {
        const l = logic([{ when: "on_game_start", do: [{ op: "kill", target: "ctx" }] }]);
        expect(ruleIds(lintRoleLogic(l, undefined, { basis: "shapeshift" }))).toContain("L14");
    });

    it("basis == \"shapeshift\" の on_pet + pull (ctx 暗黙 op) も警告しない", () => {
        const l = logic([{ when: "on_pet", do: [{ op: "pull" }] }]);
        expect(ruleIds(lintRoleLogic(l, undefined, { basis: "shapeshift" }))).not.toContain("L14");
    });
});

describe("lint-role: L31 (かちのかぞえかたは crewmate だけ効く)", () => {
    it("countsAs != 1 かつ team != crewmate なら L31 を警告する", () => {
        const l = logic([{ when: "on_pet", do: [{ op: "stop" }] }]);
        expect(ruleIds(lintRoleLogic(l, undefined, { team: "impostor", countsAs: 2 }))).toContain("L31");
        expect(ruleIds(lintRoleLogic(l, undefined, { team: "neutral", countsAs: 0 }))).toContain("L31");
    });

    it("team == crewmate なら countsAs != 1 でも警告しない", () => {
        const l = logic([{ when: "on_pet", do: [{ op: "stop" }] }]);
        expect(ruleIds(lintRoleLogic(l, undefined, { team: "crewmate", countsAs: 2 }))).not.toContain("L31");
    });

    it("countsAs == 1 (既定と同じ) なら team に関わらず警告しない", () => {
        const l = logic([{ when: "on_pet", do: [{ op: "stop" }] }]);
        expect(ruleIds(lintRoleLogic(l, undefined, { team: "impostor", countsAs: 1 }))).not.toContain("L31");
    });

    it("countsAs / team が docContext に無ければ警告しない", () => {
        const l = logic([{ when: "on_pet", do: [{ op: "stop" }] }]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L31");
        expect(ruleIds(lintRoleLogic(l, undefined, { team: "impostor" }))).not.toContain("L31");
    });
});

describe("lint-role: L32 (とくいわざの まちじかんは「だれかを えらぶ」のときだけ効く)", () => {
    it("abilityCooldown が既定 (30) 以外 かつ basis == \"pet\" なら L32 を警告する", () => {
        const l = logic([{ when: "on_pet", do: [{ op: "stop" }] }]);
        expect(ruleIds(lintRoleLogic(l, undefined, { basis: "pet", abilityCooldown: 90 }))).toContain("L32");
    });

    it("basis == \"shapeshift\" なら abilityCooldown をいくつにしても警告しない", () => {
        const l = logic([{ when: "on_pet", do: [{ op: "stop" }] }]);
        expect(ruleIds(lintRoleLogic(l, undefined, { basis: "shapeshift", abilityCooldown: 90 }))).not.toContain("L32");
    });

    it("abilityCooldown が既定値 (30) のままなら basis == \"pet\" でも警告しない", () => {
        const l = logic([{ when: "on_pet", do: [{ op: "stop" }] }]);
        expect(ruleIds(lintRoleLogic(l, undefined, { basis: "pet", abilityCooldown: 30 }))).not.toContain("L32");
    });

    it("abilityCooldown が docContext に無ければ警告しない", () => {
        const l = logic([{ when: "on_pet", do: [{ op: "stop" }] }]);
        expect(ruleIds(lintRoleLogic(l, undefined, { basis: "pet" }))).not.toContain("L32");
    });
});

describe("lint-role: L33 (「だれかを えらぶ」なのに on_pet のルールが無い)", () => {
    it("basis == \"shapeshift\" で on_pet ルールが無ければ L33 を警告する", () => {
        const l = logic([{ when: "on_game_start", do: [{ op: "stop" }] }]);
        expect(ruleIds(lintRoleLogic(l, undefined, { basis: "shapeshift" }))).toContain("L33");
    });

    it("basis == \"shapeshift\" でも on_pet ルールが1つあれば警告しない", () => {
        const l = logic([
            { when: "on_game_start", do: [{ op: "stop" }] },
            { when: "on_pet", do: [{ op: "kill", target: "ctx" }] },
        ]);
        expect(ruleIds(lintRoleLogic(l, undefined, { basis: "shapeshift" }))).not.toContain("L33");
    });

    it("basis == \"pet\" (または docContext 無し) では on_pet が無くても警告しない", () => {
        const l = logic([{ when: "on_game_start", do: [{ op: "stop" }] }]);
        expect(ruleIds(lintRoleLogic(l, undefined, { basis: "pet" }))).not.toContain("L33");
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L33");
    });

    // Wave 11 (契約 §2/§7): "phantom" (きえるボタンをおす) も "shapeshift" と同じく
    // on_pet ルールが要る。
    it("basis == \"phantom\" で on_pet ルールが無ければ L33 を警告する", () => {
        const l = logic([{ when: "on_game_start", do: [{ op: "stop" }] }]);
        expect(ruleIds(lintRoleLogic(l, undefined, { basis: "phantom" }))).toContain("L33");
    });

    it("basis == \"phantom\" でも on_pet ルールが1つあれば警告しない", () => {
        const l = logic([
            { when: "on_game_start", do: [{ op: "stop" }] },
            { when: "on_pet", do: [{ op: "effect_give", target: "self", kind: "invisible", seconds: 5 }] },
        ]);
        expect(ruleIds(lintRoleLogic(l, undefined, { basis: "phantom" }))).not.toContain("L33");
    });
});

// Wave 11 (契約 §7): effect_give(invisible) の2ヒント。
describe("lint-role: L39 (invisible × hideFrom:\"crewmates\"/\"enemies\" は通常ゲームモード限定)", () => {
    it("hideFrom:\"crewmates\" は L39 を警告する", () => {
        const l = logic([{ when: "on_pet", do: [{ op: "effect_give", target: "self", kind: "invisible", seconds: 5, hideFrom: "crewmates" }] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L39");
    });

    it("hideFrom:\"enemies\" も L39 を警告する (crewmates と同じ述語付き経路・Standard 限定)", () => {
        const l = logic([{ when: "on_pet", do: [{ op: "effect_give", target: "self", kind: "invisible", seconds: 5, hideFrom: "enemies" }] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L39");
    });

    it("hideFrom:\"everyone\" (既定) は L39 を警告しない", () => {
        const everyone = logic([{ when: "on_pet", do: [{ op: "effect_give", target: "self", kind: "invisible", seconds: 5, hideFrom: "everyone" }] }]);
        expect(ruleIds(lintRoleLogic(everyone))).not.toContain("L39");
    });

    it("kind が invisible 以外なら hideFrom は付かないので L39 の対象にならない", () => {
        const l = logic([{ when: "on_pet", do: [{ op: "effect_give", target: "self", kind: "haste", seconds: 5 }] }]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L39");
    });
});

describe("lint-role: L40 (invisible の interval < 3 はみだれうちの kick リスク)", () => {
    it("interval < 3 (1/2) は L40 を警告する", () => {
        const one = logic([{ when: "on_pet", do: [{ op: "effect_give", target: "self", kind: "invisible", seconds: 5, interval: 1 }] }]);
        expect(ruleIds(lintRoleLogic(one))).toContain("L40");
        const two = logic([{ when: "on_pet", do: [{ op: "effect_give", target: "self", kind: "invisible", seconds: 5, interval: 2 }] }]);
        expect(ruleIds(lintRoleLogic(two))).toContain("L40");
    });

    it("interval == 3 (境界値) は L40 を警告しない (< 3 のときだけ)", () => {
        const l = logic([{ when: "on_pet", do: [{ op: "effect_give", target: "self", kind: "invisible", seconds: 5, interval: 3 }] }]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L40");
    });

    it("interval 省略 (既定5) は L40 を警告しない", () => {
        const l = logic([{ when: "on_pet", do: [{ op: "effect_give", target: "self", kind: "invisible", seconds: 5 }] }]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L40");
    });
});

// Wave 10 (§8 2026-09-13 併合): L34〜L38 (つける・はがす)
describe("lint-role: L34 (addon_give のアドオンがクライアント描画依存 — 生成物の clientOnly フラグ)", () => {
    it("addon_give(Blind) は L34 を警告する (clientOnly: true)", () => {
        const l = logic([{ when: "on_pet", do: [{ op: "addon_give", target: "ctx", addon: "Blind" }] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L34");
    });

    it("addon_give(Flash) (clientOnly ではない) は L34 を警告しない", () => {
        const l = logic([{ when: "on_pet", do: [{ op: "addon_give", target: "ctx", addon: "Flash" }] }]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L34");
    });

    it("addon_remove(Blind) は L34 の対象外 (L34 は addon_give だけを見る)", () => {
        const l = logic([{ when: "on_pet", do: [{ op: "addon_remove", target: "ctx", addon: "Blind" }] }]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L34");
    });

    it("if の中にネストした addon_give(Blind) も検知する", () => {
        const nested: LogicNode = { op: "if", cond: { e: "lit", v: 1 }, then: [{ op: "addon_give", target: "ctx", addon: "Blind" }] };
        const l = logic([{ when: "on_pet", do: [nested] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L34");
    });
});

describe("lint-role: L35 (on_second 配下の addon_give/addon_remove — L5/L27/L28/L29 の兄弟)", () => {
    it("on_second + addon_give (直下) は L35 を警告する", () => {
        const l = logic([{ when: "on_second", do: [{ op: "addon_give", target: "ctx", addon: "Flash" }] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L35");
    });

    it("on_second + addon_remove (直下) も L35 を警告する", () => {
        const l = logic([{ when: "on_second", do: [{ op: "addon_remove", target: "ctx", addon: "Flash" }] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L35");
    });

    it("on_second + addon_give (if の中にネスト) も検知する", () => {
        const nested: LogicNode = { op: "if", cond: { e: "lit", v: 1 }, then: [{ op: "addon_give", target: "ctx", addon: "Flash" }] };
        const l = logic([{ when: "on_second", do: [nested] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L35");
    });

    it("on_pet (on_second 以外) の addon_give/addon_remove は L35 を警告しない", () => {
        const l = logic([{ when: "on_pet", do: [{ op: "addon_give", target: "ctx", addon: "Flash" }] }]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L35");
    });
});

describe("lint-role: L36 (同じ rule 内で同じ相手に同じアドオンを give→remove または逆)", () => {
    it("同じ target/addon の addon_give → addon_remove は L36 を警告する", () => {
        const l = logic([{
            when: "on_pet",
            do: [
                { op: "addon_give", target: "ctx", addon: "Flash" },
                { op: "addon_remove", target: "ctx", addon: "Flash" },
            ],
        }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L36");
    });

    it("逆順 (addon_remove → addon_give) でも L36 を警告する (順序は問わない静的近似)", () => {
        const l = logic([{
            when: "on_pet",
            do: [
                { op: "addon_remove", target: "ctx", addon: "Flash" },
                { op: "addon_give", target: "ctx", addon: "Flash" },
            ],
        }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L36");
    });

    it("target が違えば L36 を警告しない", () => {
        const l = logic([{
            when: "on_pet",
            do: [
                { op: "addon_give", target: "ctx", addon: "Flash" },
                { op: "addon_remove", target: "nearest", addon: "Flash" },
            ],
        }]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L36");
    });

    it("addon が違えば L36 を警告しない", () => {
        const l = logic([{
            when: "on_pet",
            do: [
                { op: "addon_give", target: "ctx", addon: "Flash" },
                { op: "addon_remove", target: "ctx", addon: "Torch" },
            ],
        }]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L36");
    });

    it("別の rule に分かれていれば L36 を警告しない (rule 単位のヒント)", () => {
        const l = logic([
            { when: "on_pet", do: [{ op: "addon_give", target: "ctx", addon: "Flash" }] },
            { when: "on_meeting_end", do: [{ op: "addon_remove", target: "saved1", addon: "Flash" }] },
        ]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L36");
    });

    it("addon_give だけ・addon_remove だけでは L36 を警告しない", () => {
        const giveOnly = logic([{ when: "on_pet", do: [{ op: "addon_give", target: "ctx", addon: "Flash" }] }]);
        expect(ruleIds(lintRoleLogic(giveOnly))).not.toContain("L36");
        const removeOnly = logic([{ when: "on_pet", do: [{ op: "addon_remove", target: "ctx", addon: "Flash" }] }]);
        expect(ruleIds(lintRoleLogic(removeOnly))).not.toContain("L36");
    });
});

describe("lint-role: L37 (addon_give が『はじめからしか つけられない』9種 / 『とちゅうで つけても きほんは かわらない』7種)", () => {
    it("addon_give(Busy) (midGameForbidden) は L37 を警告する", () => {
        const l = logic([{ when: "on_pet", do: [{ op: "addon_give", target: "ctx", addon: "Busy" }] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L37");
    });

    it("addon_give(Bloodlust) (basisChanging) も L37 を警告する", () => {
        const l = logic([{ when: "on_pet", do: [{ op: "addon_give", target: "ctx", addon: "Bloodlust" }] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L37");
    });

    it("addon_give(Flash) (どちらのフラグも無い) は L37 を警告しない", () => {
        const l = logic([{ when: "on_pet", do: [{ op: "addon_give", target: "ctx", addon: "Flash" }] }]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L37");
    });

    it("addon_remove(Busy) は L37 の対象外 (L37 は addon_give だけを見る)", () => {
        const l = logic([{ when: "on_pet", do: [{ op: "addon_remove", target: "ctx", addon: "Busy" }] }]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L37");
    });

    it("midGameForbidden と basisChanging を両方使うと L37 が2件出る (別々の警告)", () => {
        const l = logic([{
            when: "on_pet",
            do: [
                { op: "addon_give", target: "ctx", addon: "Busy" },
                { op: "addon_give", target: "nearest", addon: "Bloodlust" },
            ],
        }]);
        expect(lintRoleLogic(l).filter((w) => w.rule === "L37")).toHaveLength(2);
    });
});

describe("lint-role: L38 (addon_remove の対象が Lovers または all)", () => {
    it("addon_remove(Lovers) は L38 を警告する", () => {
        const l = logic([{ when: "on_pet", do: [{ op: "addon_remove", target: "ctx", addon: "Lovers" }] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L38");
    });

    it("addon_remove(all) も L38 を警告する", () => {
        const l = logic([{ when: "on_pet", do: [{ op: "addon_remove", target: "ctx", addon: "all" }] }]);
        expect(ruleIds(lintRoleLogic(l))).toContain("L38");
    });

    it("addon_remove(Flash) (Lovers でも all でもない) は L38 を警告しない", () => {
        const l = logic([{ when: "on_pet", do: [{ op: "addon_remove", target: "ctx", addon: "Flash" }] }]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L38");
    });

    it("addon_give(Lovers) は L38 の対象外 (L38 は addon_remove だけを見る)", () => {
        const l = logic([{ when: "on_pet", do: [{ op: "addon_give", target: "ctx", addon: "Lovers" }] }]);
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L38");
    });
});

// Wave 12 (契約 §7 2026-09-14): L41/L42 (みられかた)。どちらも rule に紐づかない文書単位の
// ヒント (L31〜L33 と同型・ruleIndex は -1) なので docContext 経由でのみ検査される。
describe("lint-role: L41 (anonymousVote が有効なのに票の重みが目立つ)", () => {
    it("anonymousVote: true + voteWeight >= 2 は L41 を警告する", () => {
        const l = logic([{ when: "on_pet", do: [{ op: "stop" }] }]);
        expect(ruleIds(lintRoleLogic(l, undefined, { anonymousVote: true, voteWeight: 2 }))).toContain("L41");
        expect(ruleIds(lintRoleLogic(l, undefined, { anonymousVote: true, voteWeight: 3 }))).toContain("L41");
    });

    it("anonymousVote: true + vote_weight_set の使用も L41 を警告する (voteWeight 省略でも)", () => {
        const l = logic([{ when: "on_game_start", do: [{ op: "vote_weight_set", value: 2 }] }]);
        expect(ruleIds(lintRoleLogic(l, undefined, { anonymousVote: true }))).toContain("L41");
    });

    it("anonymousVote: true でも voteWeight < 2 かつ vote_weight_set 未使用なら警告しない", () => {
        const l = logic([{ when: "on_pet", do: [{ op: "stop" }] }]);
        expect(ruleIds(lintRoleLogic(l, undefined, { anonymousVote: true, voteWeight: 1 }))).not.toContain("L41");
        expect(ruleIds(lintRoleLogic(l, undefined, { anonymousVote: true }))).not.toContain("L41");
    });

    it("anonymousVote が無効/未指定なら voteWeight や vote_weight_set があっても警告しない", () => {
        const l = logic([{ when: "on_game_start", do: [{ op: "vote_weight_set", value: 3 }] }]);
        expect(ruleIds(lintRoleLogic(l, undefined, { anonymousVote: false, voteWeight: 3 }))).not.toContain("L41");
        expect(ruleIds(lintRoleLogic(l, undefined, { voteWeight: 3 }))).not.toContain("L41");
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L41");
    });
});

describe("lint-role: L42 (disguise.deep なのに見せる陣営が自陣営と同じ)", () => {
    it("disguiseDeep: true + disguiseTeam == team は L42 を警告する", () => {
        const l = logic([{ when: "on_pet", do: [{ op: "stop" }] }]);
        expect(ruleIds(lintRoleLogic(l, undefined, { team: "crewmate", disguiseTeam: "crewmate", disguiseDeep: true }))).toContain("L42");
    });

    it("disguiseDeep: true でも disguiseTeam != team なら警告しない (別陣営に見せる正当ユース)", () => {
        const l = logic([{ when: "on_pet", do: [{ op: "stop" }] }]);
        expect(ruleIds(lintRoleLogic(l, undefined, { team: "impostor", disguiseTeam: "crewmate", disguiseDeep: true }))).not.toContain("L42");
    });

    it("disguiseDeep: false (既定) なら disguiseTeam == team でも警告しない", () => {
        const l = logic([{ when: "on_pet", do: [{ op: "stop" }] }]);
        expect(ruleIds(lintRoleLogic(l, undefined, { team: "crewmate", disguiseTeam: "crewmate", disguiseDeep: false }))).not.toContain("L42");
        expect(ruleIds(lintRoleLogic(l, undefined, { team: "crewmate", disguiseTeam: "crewmate" }))).not.toContain("L42");
    });

    it("disguiseTeam / team が docContext に無ければ警告しない", () => {
        const l = logic([{ when: "on_pet", do: [{ op: "stop" }] }]);
        expect(ruleIds(lintRoleLogic(l, undefined, { disguiseDeep: true }))).not.toContain("L42");
        expect(ruleIds(lintRoleLogic(l))).not.toContain("L42");
    });
});
