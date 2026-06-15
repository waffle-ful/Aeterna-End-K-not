// オートタイル UI 統合テスト (autotile-bake.test.ts)
// 設計の正典: docs/ekm-studio/design_autotile.md §7 MVP 項目 5/6/8
//
// テスト対象:
//   1. golden round-trip: edge-4 で焼いたセルを持つ v3 マップが
//      docToJsonV3 → validateEkmapV3 を通る (= C# 無変更の保証)
//   2. LUT ベイク (bakeAutotileAttrs 相当): tiles[] に light/over が正しく書かれること

import { describe, expect, it } from "vitest";
import tilesetB64 from "./fixtures/tileset8x2.b64.txt?raw";
import {
    type AutotileDef,
    type GridView,
    EDGE4_SLOTS,
    bakeAutotileTileAttrs,
    computeAutotileEdits,
    createEdge4Def,
    membershipSet,
} from "../src/autotile";
import { type MapDocV3, type TilesetDoc, defaultTileAttr, docToJsonV3, tileCount } from "../src/model";
import { validateEkmapAny, validateEkmapV3 } from "../src/validate";

const IMG = "data:image/png;base64," + tilesetB64.replace(/\s+/g, "");

// ============================================================
// テスト用ユーティリティ
// ============================================================

/** 8列×2行 (16チップ) のタイルセットを作る */
function makeTileset(): TilesetDoc {
    const tiles = Array.from({ length: 16 }, () => defaultTileAttr());
    return { tileSize: 32, columns: 8, rows: 2, image: IMG, tiles };
}

/**
 * edge-4 autotile def: code 0〜15 → tileId 0〜15、fallback = 0
 * (tileset8x2 はちょうど 16 チップなので 1:1 マッピング可能)
 */
function makeTestDef(tileset = 0): AutotileDef {
    const def = createEdge4Def("testWall", tileset);
    def.lut = Array.from({ length: EDGE4_SLOTS }, (_, c) => c);
    def.fallback = 0;
    def.blocksLight = true;
    def.band = "ground";
    return def;
}

/** 最小限の v3 マップドキュメントを作る。spawn(1,1) はタイル0 (通行可) を保証する */
function makeV3Doc(cells0?: number[]): MapDocV3 {
    const ts = makeTileset();
    const W = 8;
    const H = 5;
    const len = W * H;
    // デフォルトで spawn 位置 (1,1) にタイル 0 (通行可) を置く
    const cells = cells0 ? cells0.slice() : new Array(len).fill(-1);
    if (!cells0) cells[1 * W + 1] = 0; // spawn (1,1) を通行可に
    return {
        ekm: 3,
        name: "autotile test",
        author: "test",
        width: W,
        height: H,
        tilesets: [ts],
        layers: [
            { tileset: 0, cells: cells, above: false },
            { tileset: 0, cells: new Array(len).fill(-1), above: false },
            { tileset: 0, cells: new Array(len).fill(-1), above: false },
            { tileset: 0, cells: new Array(len).fill(-1), above: false },
        ],
        decor: [],
        spawn: { x: 1, y: 1 },
        ambient: { visionRadius: 8 },
    };
}

/** 本番 main.ts が呼ぶのと同じ純関数 (autotile.ts) で tiles[] にベイクする */
function bakeAutotileAttrsTest(ts: TilesetDoc, def: AutotileDef): void {
    bakeAutotileTileAttrs(ts.tiles, def);
}

/**
 * autotile ブラシで (x,y) を塗る: computeAutotileEdits の edits を cells[] に適用する純関数。
 */
function applyEdits(cells: number[], width: number, height: number, def: AutotileDef, x: number, y: number, mode: "paint" | "erase"): void {
    const view: GridView = { width, height, cells };
    const edits = computeAutotileEdits(def, view, x, y, mode);
    for (const e of edits) {
        cells[e.y * width + e.x] = e.id;
    }
}

// ============================================================
// 1. golden round-trip テスト
// ============================================================

describe("autotile golden round-trip: 焼いたセルは validateEkmapV3 を通る", () => {
    it("空マップは検証を通る", () => {
        const d = makeV3Doc();
        const json = docToJsonV3(d);
        const r = validateEkmapV3(json);
        expect(r.ok).toBe(true);
    });

    it("edge-4 で壁を3x3ブロック塗ったマップが v3 として正常に検証される", () => {
        const d = makeV3Doc();
        const def = makeTestDef(0);
        const cells = d.layers[0].cells;
        const W = d.width;
        const H = d.height;

        // 3x3 ブロックを塗る (1,1)〜(3,3)
        for (let y = 1; y <= 3; y++) {
            for (let x = 1; x <= 3; x++) {
                applyEdits(cells, W, H, def, x, y, "paint");
            }
        }

        // bake attrs
        bakeAutotileAttrsTest(d.tilesets[0], def);

        const json = docToJsonV3(d);
        const r = validateEkmapV3(json);
        if (!r.ok) console.error("validation errors:", r.errors);
        expect(r.ok).toBe(true);
    });

    it("焼いたマップの cells[] は普通の整数 (-1〜tilecount-1) で構成される", () => {
        const d = makeV3Doc();
        const def = makeTestDef(0);
        const cells = d.layers[0].cells;
        const W = d.width;
        const H = d.height;
        const maxId = tileCount(d.tilesets[0]) - 1;

        applyEdits(cells, W, H, def, 2, 2, "paint");
        applyEdits(cells, W, H, def, 3, 2, "paint");

        for (const v of cells) {
            expect(v).toBeGreaterThanOrEqual(-1);
            expect(v).toBeLessThanOrEqual(maxId);
        }
    });

    it("validateEkmapAny でも通る (bytes 引数あり)", () => {
        const d = makeV3Doc();
        const def = makeTestDef(0);
        const cells = d.layers[0].cells;
        applyEdits(cells, d.width, d.height, def, 2, 2, "paint");
        bakeAutotileAttrsTest(d.tilesets[0], def);

        const json = docToJsonV3(d);
        const text = JSON.stringify(json);
        const bytes = new TextEncoder().encode(text).length;
        const r = validateEkmapAny(json, bytes);
        expect(r.ok).toBe(true);
    });
});

// ============================================================
// 2. LUT ベイクテスト (bakeAutotileAttrs 相当)
// ============================================================

describe("LUT ベイク: tiles[] に light/over が正しく書かれる", () => {
    it("blocksLight=true → LUT 出力 tileId の light が true になる", () => {
        const ts = makeTileset();
        const def = makeTestDef(0);
        def.blocksLight = true;
        def.band = "ground";

        bakeAutotileAttrsTest(ts, def);

        const mset = membershipSet(def);
        for (const id of mset) {
            if (id < 0 || id >= ts.tiles.length) continue;
            expect(ts.tiles[id].light).toBe(true);
        }
    });

    it("blocksLight=false → LUT 出力 tileId の light が false になる", () => {
        const ts = makeTileset();
        const def = makeTestDef(0);
        def.blocksLight = false;
        def.band = "ground";

        bakeAutotileAttrsTest(ts, def);

        const mset = membershipSet(def);
        for (const id of mset) {
            if (id < 0 || id >= ts.tiles.length) continue;
            expect(ts.tiles[id].light).toBe(false);
        }
    });

    it("band=above → LUT 出力 tileId の over が true になる", () => {
        const ts = makeTileset();
        const def = makeTestDef(0);
        def.blocksLight = false;
        def.band = "above";

        bakeAutotileAttrsTest(ts, def);

        const mset = membershipSet(def);
        for (const id of mset) {
            if (id < 0 || id >= ts.tiles.length) continue;
            expect(ts.tiles[id].over).toBe(true);
        }
    });

    it("band=ground → LUT 出力 tileId の over が false になる", () => {
        const ts = makeTileset();
        const def = makeTestDef(0);
        def.blocksLight = false;
        def.band = "ground";

        bakeAutotileAttrsTest(ts, def);

        const mset = membershipSet(def);
        for (const id of mset) {
            if (id < 0 || id >= ts.tiles.length) continue;
            expect(ts.tiles[id].over).toBe(false);
        }
    });

    it("fallback tileId にも blocksLight/band が焼かれる", () => {
        const ts = makeTileset();
        const def = makeTestDef(0);
        def.lut = new Array(EDGE4_SLOTS).fill(-1); // LUT は全部未割当
        def.fallback = 5;
        def.blocksLight = true;
        def.band = "above";

        bakeAutotileAttrsTest(ts, def);

        expect(ts.tiles[5].light).toBe(true);
        expect(ts.tiles[5].over).toBe(true);
    });

    it("ベイク後もマップは validateEkmapV3 を通る (属性変更はフォーマット非破壊)", () => {
        const d = makeV3Doc();
        const def = makeTestDef(0);
        def.blocksLight = true;
        def.band = "ground";

        const cells = d.layers[0].cells;
        applyEdits(cells, d.width, d.height, def, 2, 2, "paint");

        // ベイク
        bakeAutotileAttrsTest(d.tilesets[0], def);

        const json = docToJsonV3(d);
        const r = validateEkmapV3(json);
        expect(r.ok).toBe(true);
    });
});

// ============================================================
// 3. Undo 一貫性 (セルから membership を導出しているため自動的に一貫)
// ============================================================

describe("membership はセルから導出 — undo後の再解決で stale にならない", () => {
    it("paint後にセルを手動で undo (before に戻す) すると membership も自動消滅", () => {
        const def = makeTestDef(0);
        const cells = new Array(5 * 5).fill(-1);
        const W = 5; const H = 5;

        // 塗る
        const view: GridView = { width: W, height: H, cells };
        const edits = computeAutotileEdits(def, view, 2, 2, "paint");
        for (const e of edits) cells[e.y * W + e.x] = e.id;

        const mset = membershipSet(def);
        expect(mset.has(cells[2 * W + 2])).toBe(true);

        // undo: 全 edits を before (-1) に戻す (history.applyPatch の模倣)
        for (const e of edits) cells[e.y * W + e.x] = -1;

        // 全セルが -1 → membership に合致するものは存在しない
        for (const v of cells) {
            expect(mset.has(v)).toBe(false);
        }
    });
});
