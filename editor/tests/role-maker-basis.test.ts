// 役職メーカー (role-maker.ts) の「はつどうのしかた」(basis) 下書き復元ヘルパーのみを対象にした
// テスト (Wave 9・契約 §1)。role-maker-team.test.ts の normalizeTeamDraft と同じ理由で、DOM を
// 必要としない純粋ロジックだけをここで検証する。

import { describe, expect, it } from "vitest";
import { normalizeBasisDraft } from "../src/logic/role-maker";
import { EKR_BASIS_DEFAULT, EKR_BASIS_VALUES } from "../src/roledef";

describe("normalizeBasisDraft (role-maker.ts のフォーム下書き復元)", () => {
    it("pet/shapeshift/phantom の3値はそのまま通す (Wave 11 で phantom 追加)", () => {
        for (const basis of EKR_BASIS_VALUES) {
            expect(normalizeBasisDraft(basis)).toBe(basis);
        }
    });

    it("不正な basis 文字列 (未知の値・大文字小文字違い含む) は既定 pet へフォールバックする", () => {
        for (const bad of ["vanish", "PET", "Shapeshift", "Phantom", " pet", ""]) {
            expect(normalizeBasisDraft(bad)).toBe(EKR_BASIS_DEFAULT);
        }
    });

    it("文字列以外 (undefined/null/数値/配列/オブジェクト) は既定 pet へフォールバックする", () => {
        for (const bad of [undefined, null, 5, ["shapeshift"], { basis: "shapeshift" }]) {
            expect(normalizeBasisDraft(bad)).toBe(EKR_BASIS_DEFAULT);
        }
    });
});
