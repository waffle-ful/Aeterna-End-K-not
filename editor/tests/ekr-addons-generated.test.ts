// 生成物 editor/src/generated/ekr-addons.ts (tools/gen-ekr-addons.ps1 が焼く) の整合テスト。
// C# 側は tests/EndKnot.Tests の AddonCatalog_MatchesGeneratedTs が EkrAddonCatalog.All との
// 集合一致を見る (この TS テストは TS 側単体で壊れていないかだけを見る — 二重管理はしない)。

import { describe, expect, it } from "vitest";
import { ADDON_BY_ID, ADDON_META, ADDON_VALUES, type AddonGroup } from "../src/generated/ekr-addons";

const ADDON_GROUPS: readonly AddonGroup[] = ["Helpful", "Harmful", "ImpOnly", "Mixed"];

describe("生成物 ekr-addons.ts の整合性 (契約 §2 — IAddon 実装 121 種)", () => {
    it("121 件ちょうど", () => {
        expect(ADDON_META).toHaveLength(121);
        expect(ADDON_VALUES).toHaveLength(121);
    });

    it("id に重複が無い", () => {
        expect(new Set(ADDON_VALUES).size).toBe(ADDON_VALUES.length);
    });

    it("各エントリの ja/en/info/infoEn が空でない", () => {
        for (const a of ADDON_META) {
            expect(a.ja.length > 0, `${a.id}.ja`).toBe(true);
            expect(a.en.length > 0, `${a.id}.en`).toBe(true);
            expect(a.info.length > 0, `${a.id}.info`).toBe(true);
            expect(a.infoEn.length > 0, `${a.id}.infoEn`).toBe(true);
        }
    });

    it("group は Helpful/Harmful/ImpOnly/Mixed のいずれか", () => {
        for (const a of ADDON_META) {
            expect(ADDON_GROUPS, a.id).toContain(a.group);
        }
    });

    it("ADDON_BY_ID は ADDON_META と同じ 121 件を id で引ける", () => {
        expect(ADDON_BY_ID.size).toBe(121);
        for (const a of ADDON_META) {
            expect(ADDON_BY_ID.get(a.id)).toEqual(a);
        }
    });

    it("ゴースト役職/陣営変換タグ (CustomRoles enum の NotAssigned 以降) は集合に含まれない", () => {
        // 契約 §2: enum 末尾をそのまま値集合にしない — 混入した場合に検知できるよう代表例を張っておく。
        for (const notAddon of ["GA", "EvilSpirit", "Haunter", "Warden", "Charmed", "Undead", "Contagious", "Entranced", "Insane"]) {
            expect(ADDON_VALUES, notAddon).not.toContain(notAddon);
        }
    });
});
