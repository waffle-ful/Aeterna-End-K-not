// テンプレートの buildJson が正常な JSON 構造を返すことを確認するテスト

import { describe, expect, it } from "vitest";
import { EKM_TEMPLATES } from "../src/templates";
import { validateEkmapAny } from "../src/validate";

describe("EKM_TEMPLATES — buildJson が正常な JSON 構造を返す", () => {
    // "空の Backrooms" は全セルが空 (-1) なので spawn が通行可セル上にない → ok=false は仕様どおり
    // (presets.test.ts の createBackroomsDoc テストでも同様に言及済み)
    it('テンプレート "空の Backrooms" の buildJson が ekm フィールドを含む JSON を返す', () => {
        const tpl = EKM_TEMPLATES.find((t) => t.id === "empty-backrooms")!;
        expect(tpl).toBeDefined();
        const json = tpl.buildJson(tpl.name, "Test Author") as Record<string, unknown>;
        expect(json.ekm).toBeDefined();
        // 名前・作者が引数で上書きされること
        expect(json.name).toBe(tpl.name);
        expect(json.author).toBe("Test Author");
    });

    // サンプルの部屋・大広間は spawn が床上にあるので strict 検証も通る
    for (const tpl of EKM_TEMPLATES.filter((t) => t.id !== "empty-backrooms")) {
        it(`テンプレート "${tpl.name}" (id="${tpl.id}") が validateEkmapAny を通る`, () => {
            const json = tpl.buildJson(tpl.name, "Test Author");
            const text = JSON.stringify(json);
            const bytes = new TextEncoder().encode(text).length;
            const r = validateEkmapAny(json, bytes);
            if (!r.ok) {
                throw new Error(`Template "${tpl.name}" validation failed: ${r.errors.join(", ")}`);
            }
            expect(r.ok).toBe(true);
            // 名前・作者が引数で上書きされること
            expect(r.doc.name).toBe(tpl.name);
            expect(r.doc.author).toBe("Test Author");
        });
    }
});
