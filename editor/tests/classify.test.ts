// 壁ビジュアル導出 (仕様 §5.1 / §5.4 golden 導出ベクタ)
// P = 孤立柱 (4 近傍に # 無し), H = 標準壁

import { describe, expect, it } from "vitest";
import { isPillar } from "../src/model";
import { golden } from "./golden";

function classify(rows: string[]): string[] {
    const h = rows.length;
    const w = rows[0].length;
    return rows.map((row, y) =>
        row
            .split("")
            .map((c, x) => (c === "#" ? (isPillar(rows, w, h, x, y) ? "P" : "H") : c))
            .join(""),
    );
}

describe("壁ビジュアル導出 (仕様 §5.1)", () => {
    it("§5.4 の 4×4 golden 導出ベクタと一致する", () => {
        const input = ["####", "#..#", "#.#.", "#..."];
        const expected = ["HHHH", "H..H", "H.P.", "H..."];
        expect(classify(input)).toEqual(expected);
    });

    it("16×16 golden: row13 の x=3 / x=12 が孤立柱、外周は標準壁", () => {
        const rows = golden.cells;
        expect(isPillar(rows, 16, 16, 3, 13)).toBe(true);
        expect(isPillar(rows, 16, 16, 12, 13)).toBe(true);
        expect(isPillar(rows, 16, 16, 0, 0)).toBe(false); // 外周角 → 標準壁
        expect(isPillar(rows, 16, 16, 0, 13)).toBe(false); // 外周 (縦に # 連続) → 標準壁
        expect(isPillar(rows, 16, 16, 7, 7)).toBe(false); // 床は柱ではない
    });

    it("1×1 の単独 # はグリッド外 (= void 扱い) に囲まれて柱になる", () => {
        expect(classify(["#"])).toEqual(["P"]);
    });
});
