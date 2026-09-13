// 生成物 editor/src/generated/ekr-roles.ts (tools/gen-ekr-roles.ps1 が焼く想定) の整合テスト。
// C# 側は tests/EndKnot.Tests の RoleCatalog_MatchesGeneratedTs (契約 §8-6) が
// EkrRoleCatalog.All との集合一致を見る (この TS テストは TS 側単体で壊れていないかだけを見る —
// 二重管理はしない・ekr-addons-generated.test.ts と同じ方針)。

import { describe, expect, it } from "vitest";
import { ROLE_BY_ID, ROLE_META, ROLE_VALUES, type RoleCatalogTeam } from "../src/generated/ekr-roles";

const ROLE_TEAMS: readonly RoleCatalogTeam[] = ["crewmate", "impostor", "neutral"];

describe("生成物 ekr-roles.ts の整合性 (契約 §2.1 — Standard の出現可能役職カタログ)", () => {
    it("1件以上ある (空生成物の踏み逃し防止)", () => {
        expect(ROLE_META.length).toBeGreaterThan(0);
        expect(ROLE_VALUES.length).toBe(ROLE_META.length);
    });

    it("id に重複が無い", () => {
        expect(new Set(ROLE_VALUES).size).toBe(ROLE_VALUES.length);
    });

    it("各エントリの ja/en が空でない", () => {
        for (const r of ROLE_META) {
            expect(r.ja.length > 0, `${r.id}.ja`).toBe(true);
            expect(r.en.length > 0, `${r.id}.en`).toBe(true);
        }
    });

    it("team は crewmate/impostor/neutral のいずれか (madmate/coven は対象外 — spec §1 と同じ3値)", () => {
        for (const r of ROLE_META) {
            expect(ROLE_TEAMS, r.id).toContain(r.team);
        }
    });

    it("ROLE_BY_ID は ROLE_META と同じ件数を id で引ける", () => {
        expect(ROLE_BY_ID.size).toBe(ROLE_META.length);
        for (const r of ROLE_META) {
            expect(ROLE_BY_ID.get(r.id)).toEqual(r);
        }
    });

    it("契約 §2.1 の代表例 (Sheriff=crewmate) が集合に含まれる", () => {
        expect(ROLE_BY_ID.get("Sheriff")?.team).toBe("crewmate");
    });
});
