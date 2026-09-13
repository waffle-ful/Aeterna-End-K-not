// 役職メーカー (role-maker.ts) の「サボタージュを つかえる」下書き復元ヘルパーのみを対象にした
// テスト (Wave 9 追記)。role-maker-team.test.ts / role-maker-basis.test.ts と同じ理由で、DOM を
// 必要としない純粋ロジックだけをここで検証する。

import { describe, expect, it } from "vitest";
import { normalizeCanSabotageDraft } from "../src/logic/role-maker";

describe("normalizeCanSabotageDraft (role-maker.ts のフォーム下書き復元)", () => {
    it("true/false はそのまま通す", () => {
        expect(normalizeCanSabotageDraft(true)).toBe(true);
        expect(normalizeCanSabotageDraft(false)).toBe(false);
    });

    it("真偽値以外 (undefined/null/文字列/数値/配列) は「陣営どおり」(undefined) へフォールバックする", () => {
        for (const bad of [undefined, null, "true", 1, []]) {
            expect(normalizeCanSabotageDraft(bad)).toBeUndefined();
        }
    });
});
