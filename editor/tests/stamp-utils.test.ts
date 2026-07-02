import { describe, expect, it } from "vitest";
import {
    bresenhamLine,
    flipStampH,
    flipStampV,
    rectOutlineCells,
} from "../src/stamp-utils";

describe("flipStampH", () => {
    it("1×1 は変化なし", () => {
        const b = { w: 1, h: 1, ids: [5] };
        expect(flipStampH(b)).toEqual({ w: 1, h: 1, ids: [5] });
    });

    it("1×3 横1行の反転", () => {
        const b = { w: 3, h: 1, ids: [1, 2, 3] };
        expect(flipStampH(b).ids).toEqual([3, 2, 1]);
    });

    it("2×2 の反転", () => {
        // 元: [0,1, 2,3] → 左右 flip → [1,0, 3,2]
        const b = { w: 2, h: 2, ids: [0, 1, 2, 3] };
        expect(flipStampH(b).ids).toEqual([1, 0, 3, 2]);
    });

    it("w/h は変化しない", () => {
        const b = { w: 3, h: 2, ids: [0, 1, 2, 3, 4, 5] };
        const r = flipStampH(b);
        expect(r.w).toBe(3);
        expect(r.h).toBe(2);
    });

    it("2回反転で元に戻る", () => {
        const b = { w: 3, h: 2, ids: [0, 1, 2, 10, 11, 12] };
        expect(flipStampH(flipStampH(b)).ids).toEqual(b.ids);
    });
});

describe("flipStampV", () => {
    it("1×1 は変化なし", () => {
        const b = { w: 1, h: 1, ids: [7] };
        expect(flipStampV(b)).toEqual({ w: 1, h: 1, ids: [7] });
    });

    it("3×1 縦3行の反転", () => {
        // 3列1行 → 上下 flip は行を逆順 → 同じ
        const b = { w: 3, h: 1, ids: [1, 2, 3] };
        expect(flipStampV(b).ids).toEqual([1, 2, 3]);
    });

    it("1×3 縦1列の反転", () => {
        const b = { w: 1, h: 3, ids: [1, 2, 3] };
        expect(flipStampV(b).ids).toEqual([3, 2, 1]);
    });

    it("2×2 の反転", () => {
        // 元: [0,1, 2,3] → 上下 flip → [2,3, 0,1]
        const b = { w: 2, h: 2, ids: [0, 1, 2, 3] };
        expect(flipStampV(b).ids).toEqual([2, 3, 0, 1]);
    });

    it("2回反転で元に戻る", () => {
        const b = { w: 3, h: 2, ids: [0, 1, 2, 10, 11, 12] };
        expect(flipStampV(flipStampV(b)).ids).toEqual(b.ids);
    });
});

describe("bresenhamLine", () => {
    it("同じ点 → 1点のみ", () => {
        const pts = bresenhamLine(3, 3, 3, 3);
        expect(pts).toEqual([{ x: 3, y: 3 }]);
    });

    it("水平線", () => {
        const pts = bresenhamLine(0, 0, 3, 0);
        expect(pts).toEqual([
            { x: 0, y: 0 },
            { x: 1, y: 0 },
            { x: 2, y: 0 },
            { x: 3, y: 0 },
        ]);
    });

    it("垂直線", () => {
        const pts = bresenhamLine(2, 0, 2, 3);
        expect(pts.every(p => p.x === 2)).toBe(true);
        expect(pts.length).toBe(4);
    });

    it("斜め45°線", () => {
        const pts = bresenhamLine(0, 0, 2, 2);
        expect(pts).toEqual([
            { x: 0, y: 0 },
            { x: 1, y: 1 },
            { x: 2, y: 2 },
        ]);
    });

    it("始点と終点を含む", () => {
        const pts = bresenhamLine(1, 2, 5, 4);
        expect(pts[0]).toEqual({ x: 1, y: 2 });
        expect(pts[pts.length - 1]).toEqual({ x: 5, y: 4 });
    });
});

describe("rectOutlineCells", () => {
    it("1×1 → 1セルのみ", () => {
        const cells = rectOutlineCells(2, 2, 2, 2);
        expect(cells).toEqual([{ x: 2, y: 2 }]);
    });

    it("2×2 → 4セル (全て外周)", () => {
        const cells = rectOutlineCells(0, 0, 1, 1);
        expect(cells.length).toBe(4);
    });

    it("3×3 → 内側1セルを除く8セル", () => {
        const cells = rectOutlineCells(0, 0, 2, 2);
        expect(cells.length).toBe(8);
        // 中央 (1,1) は含まれない
        expect(cells.some(c => c.x === 1 && c.y === 1)).toBe(false);
    });

    it("2×3 → 正しい外周", () => {
        const cells = rectOutlineCells(0, 0, 1, 2);
        // 2*3=6マス全て外周 (幅2の矩形は常に枠のみ)
        expect(cells.length).toBe(6);
    });
});
