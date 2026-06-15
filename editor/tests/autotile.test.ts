// オートタイル (edge-4 スマートブラシ) 純関数層のテスト
// 設計: docs/ekm-studio/design_autotile.md §7 MVP (項目 1/3/4 のコア)

import { describe, expect, it } from "vitest";
import {
    type AutotileDef,
    type GridView,
    EDGE4_E,
    EDGE4_N,
    EDGE4_S,
    EDGE4_SLOTS,
    EDGE4_W,
    computeAutotileEdits,
    createEdge4Def,
    edge4Code,
    filledSlotCount,
    isLutEmpty,
    membershipSet,
    resolveEdge4,
} from "../src/autotile";

// 各 code をそのまま tileId にマップした分かりやすい LUT (lut[c] = 100 + c)、fallback = 199
function makeDef(over: Partial<AutotileDef> = {}): AutotileDef {
    const def = createEdge4Def("wall", 0);
    def.lut = Array.from({ length: EDGE4_SLOTS }, (_, c) => 100 + c);
    def.fallback = 199;
    return { ...def, ...over };
}

function makeGrid(width: number, height: number, fill = -1): GridView {
    return { width, height, cells: new Array(width * height).fill(fill) };
}

function setCell(g: GridView, x: number, y: number, id: number): void {
    g.cells[y * g.width + x] = id;
}

function getCell(g: GridView, x: number, y: number): number {
    return g.cells[y * g.width + x];
}

function apply(g: GridView, edits: { x: number; y: number; id: number }[]): void {
    for (const e of edits) g.cells[e.y * g.width + e.x] = e.id;
}

describe("edge4Code: 近傍ビット", () => {
    it("N/E/S/W が 1/2/4/8 に対応", () => {
        expect(edge4Code(true, false, false, false)).toBe(EDGE4_N);
        expect(edge4Code(false, true, false, false)).toBe(EDGE4_E);
        expect(edge4Code(false, false, true, false)).toBe(EDGE4_S);
        expect(edge4Code(false, false, false, true)).toBe(EDGE4_W);
        expect(edge4Code(false, false, false, false)).toBe(0);
        expect(edge4Code(true, true, true, true)).toBe(15);
    });
});

describe("resolveEdge4 / membership", () => {
    it("割当済みスロットは LUT、未割当は fallback", () => {
        const def = makeDef();
        expect(resolveEdge4(def, 0)).toBe(100);
        expect(resolveEdge4(def, 8)).toBe(108);
        def.lut[5] = -1;
        expect(resolveEdge4(def, 5)).toBe(199); // fallback
    });

    it("fallback も未設定なら -1 (空)", () => {
        const def = makeDef({ lut: new Array(EDGE4_SLOTS).fill(-1), fallback: -1 });
        expect(resolveEdge4(def, 3)).toBe(-1);
        expect(isLutEmpty(def)).toBe(true);
    });

    it("membershipSet は LUT 出力 + fallback", () => {
        const def = makeDef();
        const set = membershipSet(def);
        expect(set.has(100)).toBe(true);
        expect(set.has(115)).toBe(true);
        expect(set.has(199)).toBe(true);
        expect(set.has(50)).toBe(false);
        expect(filledSlotCount(def)).toBe(16);
    });
});

describe("computeAutotileEdits: paint", () => {
    it("孤立して塗ると code=0 (近傍なし) で lut[0]", () => {
        const g = makeGrid(5, 5);
        const edits = computeAutotileEdits(makeDef(), g, 2, 2, "paint");
        expect(edits).toEqual([{ x: 2, y: 2, id: 100 }]);
    });

    it("既存メンバーの隣に塗ると両セルが再解決される", () => {
        const def = makeDef();
        const g = makeGrid(5, 5);
        setCell(g, 1, 2, 100); // 左に孤立メンバー (code0 相当の絵)
        const edits = computeAutotileEdits(def, g, 2, 2, "paint");
        apply(g, edits);
        // (2,2): 西だけ接続 → code=W(8) → 108
        expect(getCell(g, 2, 2)).toBe(108);
        // (1,2): 東だけ接続 → code=E(2) → 102
        expect(getCell(g, 1, 2)).toBe(102);
    });

    it("3 連続の真ん中を塗ると左右が内側へ再解決 (横一直線)", () => {
        const def = makeDef();
        const g = makeGrid(5, 5);
        setCell(g, 1, 2, 102); // 既に東向き
        setCell(g, 3, 2, 108); // 既に西向き
        const edits = computeAutotileEdits(def, g, 2, 2, "paint");
        apply(g, edits);
        // (2,2): 東西接続 → code=E|W(2|8=10) → 110
        expect(getCell(g, 2, 2)).toBe(110);
        // (1,2): 東のみ → 102 / (3,2): 西のみ → 108
        expect(getCell(g, 1, 2)).toBe(102);
        expect(getCell(g, 3, 2)).toBe(108);
    });

    it("4 方向に囲まれて塗ると code=15", () => {
        const def = makeDef();
        const g = makeGrid(5, 5);
        setCell(g, 2, 1, 100);
        setCell(g, 3, 2, 100);
        setCell(g, 2, 3, 100);
        setCell(g, 1, 2, 100);
        const edits = computeAutotileEdits(def, g, 2, 2, "paint");
        apply(g, edits);
        expect(getCell(g, 2, 2)).toBe(115); // N|E|S|W
    });

    it("マップ端で塗ると OOB 近傍は非メンバー扱い", () => {
        const def = makeDef();
        const g = makeGrid(3, 3);
        const edits = computeAutotileEdits(def, g, 0, 0, "paint");
        apply(g, edits);
        // (0,0) は上・左が OOB → 接続なし → code=0
        expect(getCell(g, 0, 0)).toBe(100);
    });
});

describe("computeAutotileEdits: erase", () => {
    it("消すと self が -1、近傍メンバーが再解決される", () => {
        const def = makeDef();
        const g = makeGrid(5, 5);
        setCell(g, 1, 2, 102); // 東向き
        setCell(g, 2, 2, 110); // 東西
        setCell(g, 3, 2, 108); // 西向き
        const edits = computeAutotileEdits(def, g, 2, 2, "erase");
        apply(g, edits);
        expect(getCell(g, 2, 2)).toBe(-1);
        // (1,2): 東が消えた → 孤立 → 100 / (3,2): 西が消えた → 100
        expect(getCell(g, 1, 2)).toBe(100);
        expect(getCell(g, 3, 2)).toBe(100);
    });
});

describe("computeAutotileEdits: 境界条件", () => {
    it("空 LUT (fallback も無し) では塗れない", () => {
        const def = makeDef({ lut: new Array(EDGE4_SLOTS).fill(-1), fallback: -1 });
        const g = makeGrid(5, 5);
        expect(computeAutotileEdits(def, g, 2, 2, "paint")).toEqual([]);
    });

    it("lut[0] 未割当でも fallback があれば塗れる", () => {
        const def = makeDef({ fallback: 199 });
        def.lut[0] = -1;
        const g = makeGrid(5, 5);
        const edits = computeAutotileEdits(def, g, 2, 2, "paint");
        expect(edits).toEqual([{ x: 2, y: 2, id: 199 }]); // code0 → fallback
    });

    it("範囲外座標は無視", () => {
        const g = makeGrid(5, 5);
        expect(computeAutotileEdits(makeDef(), g, -1, 2, "paint")).toEqual([]);
        expect(computeAutotileEdits(makeDef(), g, 5, 2, "paint")).toEqual([]);
    });
});
