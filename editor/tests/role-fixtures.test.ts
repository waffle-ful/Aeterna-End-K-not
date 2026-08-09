// logic 付き .ekrole.json の golden fixture (docs/ekr-logic-spec.md §1〜§6 の共有検証資材)。
// 契約監査の指摘: 「AST 深さ不一致のような実装差分は、共有の実物 .ekrole.json が無いと自動検出
// できない」— このテストはその穴を塞ぐ。fixtures/ のファイルは vitest 専用ではなく、そのまま
// Documents/EndKnot/EKRoles/ に置いて実機 /role import テストにも使う想定 (現実的な日本語の
// 役職名・flavor text で書く)。そのため fixture 自体を書き換えるときはこのテストと変更内容を
// 揃えること (golden.ts の「仕様書から逐語コピー・改変禁止」ほど厳格な凍結ではないが、
// 意図せず壊すと実機テスト側の資材も一緒に壊れる)。

import { describe, expect, it } from "vitest";
import fullCourseRaw from "./fixtures/role-full-course.ekrole.json?raw";
import cnoShowcaseRaw from "./fixtures/role-cno-showcase.ekrole.json?raw";
import dummyShowcaseRaw from "./fixtures/role-dummy-showcase.ekrole.json?raw";
import { ROLECODE_PREFIX, decodeRoleCode, encodeRoleCode } from "../src/rolecode";
import { LOGIC_WHEN_VALUES, validateEkrDefinition, type LogicNode, type LogicWhen } from "../src/roledef";
import { lintRoleLogic } from "../src/logic/lint-role";

function collectOps(nodes: LogicNode[], into: Set<string>): void {
    for (const n of nodes) {
        into.add(n.op);
        if (n.op === "if") {
            collectOps(n.then, into);
            if (n.else) collectOps(n.else, into);
        }
    }
}

describe("golden fixture: role-full-course.ekrole.json (10イベント・主要opcode・式ネスト・変数を網羅)", () => {
    it("validate に合格する", () => {
        const parsed = JSON.parse(fullCourseRaw);
        const result = validateEkrDefinition(parsed);
        expect(result.ok).toBe(true);
    });

    it("10 種類のイベントすべてを1回ずつカバーしている", () => {
        const parsed = JSON.parse(fullCourseRaw);
        const result = validateEkrDefinition(parsed);
        if (!result.ok) throw new Error(result.error);
        if (!result.def.logic) throw new Error("fixture は logic を持つ前提");

        const whens = result.def.logic.rules.map((r) => r.when);
        expect(new Set(whens).size).toBe(LOGIC_WHEN_VALUES.length);
        for (const w of LOGIC_WHEN_VALUES) {
            expect(whens).toContain(w as LogicWhen);
        }
    });

    it("制御 op (if/wait/stop/var_set/var_add) とアクション op (9種) を全て少なくとも1回使っている", () => {
        const parsed = JSON.parse(fullCourseRaw);
        const result = validateEkrDefinition(parsed);
        if (!result.ok) throw new Error(result.error);
        if (!result.def.logic) throw new Error("fixture は logic を持つ前提");

        const ops = new Set<string>();
        for (const rule of result.def.logic.rules) collectOps(rule.do, ops);

        const expectedOps = [
            "if", "wait", "stop", "var_set", "var_add",
            "notify", "teleport", "kill", "set_kill_cooldown", "speed",
            "cno_spawn", "cno_move", "cno_despawn", "cno_show",
        ];
        for (const op of expectedOps) {
            expect(ops.has(op), `op "${op}" が fixture 内で使われていない`).toBe(true);
        }
    });

    it("リンター (spec §6) は警告0件 — golden fixture は模範的な組み方で書く", () => {
        const parsed = JSON.parse(fullCourseRaw);
        const result = validateEkrDefinition(parsed);
        if (!result.ok) throw new Error(result.error);
        if (!result.def.logic) throw new Error("fixture は logic を持つ前提");
        expect(lintRoleLogic(result.def.logic)).toEqual([]);
    });

    it("rolecode (EKR1.) のエンコード→デコード ラウンドトリップで AST が deep-equal になる", () => {
        const parsed = JSON.parse(fullCourseRaw);
        const validated = validateEkrDefinition(parsed);
        if (!validated.ok) throw new Error(validated.error);

        const code = encodeRoleCode(JSON.stringify(validated.def));
        expect(code.startsWith(ROLECODE_PREFIX)).toBe(true);

        const roundTripped = validateEkrDefinition(JSON.parse(decodeRoleCode(code)));
        if (!roundTripped.ok) throw new Error(roundTripped.error);

        expect(roundTripped.def).toEqual(validated.def);
        expect(roundTripped.def.logic).toEqual(validated.def.logic);

        // 一度検証済みになった形は不動点 (再エンコードしても同じバイト列になる) — 実機で
        // このファイルを import → エディタで再コピー → 出てきたコードを diff、が意図どおりに
        // 「差分なし」になることを保証する (validated.def 同士の比較だけでは検知できない)。
        expect(encodeRoleCode(JSON.stringify(roundTripped.def))).toBe(code);
    });
});

describe("golden fixture: role-cno-showcase.ekrole.json (CNO 演出中心・実機テスト用)", () => {
    it("validate に合格する", () => {
        const parsed = JSON.parse(cnoShowcaseRaw);
        const result = validateEkrDefinition(parsed);
        expect(result.ok).toBe(true);
    });

    it("cno_spawn/cno_move/cno_despawn/cno_show を使っている (CNO 演出中心)", () => {
        const parsed = JSON.parse(cnoShowcaseRaw);
        const result = validateEkrDefinition(parsed);
        if (!result.ok) throw new Error(result.error);
        if (!result.def.logic) throw new Error("fixture は logic を持つ前提");

        const ops = new Set<string>();
        for (const rule of result.def.logic.rules) collectOps(rule.do, ops);
        expect(ops.has("cno_spawn")).toBe(true);
        expect(ops.has("cno_move")).toBe(true);
        expect(ops.has("cno_despawn")).toBe(true);
        expect(ops.has("cno_show")).toBe(true);
    });

    it("cno_spawn.text の <灯> は検証時に全角 〈灯〉 へサニタイズされる (spec §3 TMP タグ注入防御・実機で目視確認できる)", () => {
        const parsed = JSON.parse(cnoShowcaseRaw);
        // 生ファイル側はまだ半角 <> のまま (人間がエディタで書く/読む生データはこの形)。
        // spawn レート (≤1/秒) に合わせて wait ノードが挟まっているため index 固定でなく slot で探す。
        const rawSlot3 = parsed.logic.rules[0].do.find((n: { op: string; slot?: number }) => n.op === "cno_spawn" && n.slot === 3);
        expect(rawSlot3.text).toBe("<灯>");

        const result = validateEkrDefinition(parsed);
        if (!result.ok) throw new Error(result.error);
        if (!result.def.logic) throw new Error("fixture は logic を持つ前提");
        const spawnSlot3 = result.def.logic.rules[0].do.find(n => n.op === "cno_spawn" && n.slot === 3);
        if (spawnSlot3?.op !== "cno_spawn") throw new Error("slot3 の cno_spawn が見つかる前提");
        expect(spawnSlot3.text).toBe("〈灯〉");
    });

    it("size の境界値 (1 と 12) をどちらも使っている", () => {
        const parsed = JSON.parse(cnoShowcaseRaw);
        const result = validateEkrDefinition(parsed);
        if (!result.ok) throw new Error(result.error);
        if (!result.def.logic) throw new Error("fixture は logic を持つ前提");

        const sizes: number[] = [];
        for (const rule of result.def.logic.rules) {
            for (const n of rule.do) {
                if (n.op === "cno_spawn") sizes.push(n.size);
            }
        }
        expect(sizes).toContain(1);
        expect(sizes).toContain(12);
    });

    it("リンター (spec §6) は警告0件", () => {
        const parsed = JSON.parse(cnoShowcaseRaw);
        const result = validateEkrDefinition(parsed);
        if (!result.ok) throw new Error(result.error);
        if (!result.def.logic) throw new Error("fixture は logic を持つ前提");
        expect(lintRoleLogic(result.def.logic)).toEqual([]);
    });

    it("rolecode (EKR1.) のエンコード→デコード ラウンドトリップで AST が deep-equal になる", () => {
        const parsed = JSON.parse(cnoShowcaseRaw);
        const validated = validateEkrDefinition(parsed);
        if (!validated.ok) throw new Error(validated.error);

        const code = encodeRoleCode(JSON.stringify(validated.def));
        const roundTripped = validateEkrDefinition(JSON.parse(decodeRoleCode(code)));
        if (!roundTripped.ok) throw new Error(roundTripped.error);

        expect(roundTripped.def).toEqual(validated.def);
        expect(roundTripped.def.logic).toEqual(validated.def.logic);

        // このファイルは検証前の生データに半角 <> が残っている (上のテスト参照) — 一度検証を
        // 通した後の形が不動点であること (サニタイズが二重適用されてもズレない) を確認する。
        expect(encodeRoleCode(JSON.stringify(roundTripped.def))).toBe(code);
    });
});

describe("golden fixture: role-dummy-showcase.ekrole.json (v1.1 dummy_spawn/corpse_spawn・実機テスト用)", () => {
    it("validate に合格する", () => {
        const parsed = JSON.parse(dummyShowcaseRaw);
        const result = validateEkrDefinition(parsed);
        expect(result.ok).toBe(true);
    });

    it("dummy_spawn/corpse_spawn の列挙値を全てカバーしている (slot 1/2・killable 両値・color 両値・at 両値)", () => {
        const parsed = JSON.parse(dummyShowcaseRaw);
        const result = validateEkrDefinition(parsed);
        if (!result.ok) throw new Error(result.error);
        if (!result.def.logic) throw new Error("fixture は logic を持つ前提");

        const dummies: Extract<LogicNode, { op: "dummy_spawn" }>[] = [];
        const corpses: Extract<LogicNode, { op: "corpse_spawn" }>[] = [];
        for (const rule of result.def.logic.rules) {
            for (const n of rule.do) {
                if (n.op === "dummy_spawn") dummies.push(n);
                if (n.op === "corpse_spawn") corpses.push(n);
            }
        }

        expect(dummies.map(d => d.slot)).toEqual(expect.arrayContaining([1, 2]));
        expect(dummies.map(d => d.killable)).toEqual(expect.arrayContaining([true, false]));
        expect(corpses.map(c => c.color)).toEqual(expect.arrayContaining(["random", "self"]));
        expect(corpses.map(c => c.at)).toEqual(expect.arrayContaining(["ctx", "self"]));
    });

    it("on_meeting_end のダミー再設置は 10.5 秒待ちが先 (L9 の模範形)、on_death の corpse_spawn は死亡時実行可の裁定素材 (spec §2 v1.1)", () => {
        const parsed = JSON.parse(dummyShowcaseRaw);
        const result = validateEkrDefinition(parsed);
        if (!result.ok) throw new Error(result.error);
        if (!result.def.logic) throw new Error("fixture は logic を持つ前提");

        const meetingEnd = result.def.logic.rules.find(r => r.when === "on_meeting_end");
        if (!meetingEnd) throw new Error("on_meeting_end ルールがある前提");
        expect(meetingEnd.do[0]).toEqual({ op: "wait", seconds: 10.5 });
        expect(meetingEnd.do[1]?.op).toBe("dummy_spawn");

        const onDeath = result.def.logic.rules.find(r => r.when === "on_death");
        if (!onDeath) throw new Error("on_death ルールがある前提");
        expect(onDeath.do.some(n => n.op === "corpse_spawn")).toBe(true);
    });

    it("リンター (spec §6 v1.1 — L9/L10 含む) は警告0件", () => {
        const parsed = JSON.parse(dummyShowcaseRaw);
        const result = validateEkrDefinition(parsed);
        if (!result.ok) throw new Error(result.error);
        if (!result.def.logic) throw new Error("fixture は logic を持つ前提");
        expect(lintRoleLogic(result.def.logic)).toEqual([]);
    });

    it("rolecode (EKR1.) のエンコード→デコード ラウンドトリップで AST が deep-equal になる", () => {
        const parsed = JSON.parse(dummyShowcaseRaw);
        const validated = validateEkrDefinition(parsed);
        if (!validated.ok) throw new Error(validated.error);

        const code = encodeRoleCode(JSON.stringify(validated.def));
        expect(code.startsWith(ROLECODE_PREFIX)).toBe(true);

        const roundTripped = validateEkrDefinition(JSON.parse(decodeRoleCode(code)));
        if (!roundTripped.ok) throw new Error(roundTripped.error);

        expect(roundTripped.def).toEqual(validated.def);
        expect(roundTripped.def.logic).toEqual(validated.def.logic);
        expect(encodeRoleCode(JSON.stringify(roundTripped.def))).toBe(code);
    });
});
