// EKM v3 (仕様 §19〜§24): 検証規則 / セル解決規則 / マップコードラウンドトリップ
// golden ベクタ / 拒否ベクタ / v2→v3 等価性 / 保存省略往復不変 / persist lenient

import { describe, expect, it } from "vitest";
// 8 列 × 2 行 (32px) = 256×64 PNG、tilecount 16
import tilesetB64 from "./fixtures/tileset8x2.b64.txt?raw";
import {
    type MapDocV2,
    type MapDocV3,
    type TilesetDoc,
    type TileAttr,
    defaultTileAttr,
    docToJsonV3,
    resolveCellV2,
    resolveCellV3,
    upgradeV2DocToV3,
    cellAboveMask,
} from "../src/model";
import { validateEkmapAny, validateEkmapV3 } from "../src/validate";
import { decodeMapCode, encodeMapCode } from "../src/mapcode";
import { tryRestoreDoc } from "../src/persist";

const IMG = "data:image/png;base64," + tilesetB64.replace(/\s+/g, "");

/* eslint-disable @typescript-eslint/no-explicit-any */

// ============================================================
// フィクスチャ
// ============================================================

/**
 * tileset8x2 (8列×2行 = 16 チップ) を 2 エントリの tilesets で共有する v3 マップ。
 * width=8, height=5
 *
 * layers[0] (tileset=0): 下層タイル (id 0=通行可, id 1=通行不可+遮光)
 * layers[1] (tileset=1): 上層タイル (id 0=通行可, id 2=pass='v')
 * layers[2]: 空
 * layers[3]: 空
 *
 * 以下は spec §23 の golden ベクタに相当するレイアウト:
 * spawn: (1, 1) — layers[0][1+8*1]=0 (通行可) なので合格
 *
 * ベクタ列:
 *   (5,2): layers[0].tiles[1] = {pass:x, light:true} / layers[1].tiles[3] = {pass:v, over:true}
 *          → 通行不可 + 遮光 + ★バッジ
 *   (2,2)-(3,3): layers[0].cells = 1 (void bridge 相当: pass=x, light=true) → 通行不可+遮光
 *   (6,1): layers[0].cells=-1 (void) → isVoid=true
 *   (5,4): layers[0].tiles[1] = pass=x / layers[1].tiles[0] = pass=o (上層優先) → 通行可, 遮光なし
 */
function makeV3(): any {
    return {
        ekm: 3,
        name: "V3 Sample",
        author: "End K not",
        width: 8,
        height: 5,
        tilesets: [
            {
                // tileset 0: 8x2, tile 0=o, tile 1=x+light
                tileSize: 32,
                columns: 8,
                image: IMG,
                tiles: [
                    { id: 1, pass: "x", over: false, light: true, tag: 0, dir: 15 },
                ],
            },
            {
                // tileset 1: 8x2, tile 0=o, tile 2=v, tile 3=v+over
                tileSize: 32,
                columns: 8,
                image: IMG,
                tiles: [
                    { id: 2, pass: "v", over: false, light: false, tag: 0, dir: 15 },
                    { id: 3, pass: "v", over: true, light: false, tag: 0, dir: 15 },
                ],
            },
        ],
        layers: [
            {
                // layers[0]: tileset 0
                tileset: 0,
                // 8x5 = 40 cells. row-major: y*8+x
                // (0,0)=0  (1,0)=0  (2,0)=0  ...  外周は 0 (通行可)
                // (1,1)=0(spawn), (2,1)=0, (3,1)=0
                // (2,2)=1(wall), (3,2)=1(wall)
                // (2,3)=1, (3,3)=1
                // (5,2)=1 (x+light)
                // (5,4)=1 (x 下層、上層で上書き)
                // (6,1)=-1 (void)
                cells: [
                    // y=0: 全部 0
                    0, 0, 0, 0, 0, 0, 0, 0,
                    // y=1: spawn(1,1)=0, (6,1)=-1
                    0, 0, 0, 0, 0, 0, -1, 0,
                    // y=2: (2,2)=1, (3,2)=1, (5,2)=1
                    0, 0, 1, 1, 0, 1, 0, 0,
                    // y=3: (2,3)=1, (3,3)=1
                    0, 0, 1, 1, 0, 0, 0, 0,
                    // y=4: (5,4)=1
                    0, 0, 0, 0, 0, 1, 0, 0,
                ],
                above: false,
            },
            {
                // layers[1]: tileset 1
                // (5,2) に id=3 (v+over) → aboveMask
                // (5,4) に id=0 (o) → 通行不可の下層を上書き → 通行可
                tileset: 1,
                cells: [
                    // y=0: 全空
                    -1, -1, -1, -1, -1, -1, -1, -1,
                    // y=1: 全空
                    -1, -1, -1, -1, -1, -1, -1, -1,
                    // y=2: (5,2)=3 (v+over)
                    -1, -1, -1, -1, -1, 3, -1, -1,
                    // y=3: 全空
                    -1, -1, -1, -1, -1, -1, -1, -1,
                    // y=4: (5,4)=0 (o)
                    -1, -1, -1, -1, -1, 0, -1, -1,
                ],
                above: false,
            },
        ],
        decor: [{ kind: "light", x: 1, y: 1 }],
        spawn: { x: 1, y: 1 },
        ambient: { visionRadius: 8 },
    };
}

// v2 互換フィクスチャ (makeV2 相当、v2→v3 等価性テスト用)
function makeV2Compat(): any {
    return {
        ekm: 2,
        name: "V2 Sample",
        author: "End K not",
        width: 4,
        height: 3,
        tileset: {
            tileSize: 32,
            columns: 8,
            image: IMG,
            tiles: [{ id: 1, pass: "x", over: false, light: true, tag: 0, dir: 15 }],
        },
        layers: {
            ground: [-1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0],
            upper: [-1, -1, -1, -1, -1, 2, -1, -1, -1, -1, -1, -1],
        },
        decor: [{ kind: "light", x: 1, y: 1 }],
        spawn: { x: 1, y: 1 },
        ambient: { visionRadius: 8 },
    };
}

// ============================================================
// v3 フィクスチャ PNG 読み込み確認
// ============================================================

describe("v3 fixture (tileset8x2 × 2)", () => {
    it("256×64 の PNG として読める (両エントリで共有)", () => {
        const r = validateEkmapV3(makeV3());
        expect(r.ok).toBe(true);
        if (r.ok) {
            expect(r.doc.tilesets.length).toBe(2);
            for (const ts of r.doc.tilesets) {
                expect(ts.columns).toBe(8);
                expect(ts.rows).toBe(2);
                expect(ts.tiles.length).toBe(16);
            }
        }
    });
});

// ============================================================
// §23 期待ベクタ (golden)
// ============================================================

describe("v3 golden ベクタ (仕様 §23)", () => {
    function getDoc(): MapDocV3 {
        const r = validateEkmapV3(makeV3());
        if (!r.ok) throw new Error("fixture invalid: " + r.errors.join(", "));
        return r.doc;
    }

    it("(1,1) spawn — 通行可 (isVoid=false, passable=true)", () => {
        const d = getDoc();
        const r = resolveCellV3(d, 1, 1);
        expect(r.isVoid).toBe(false);
        expect(r.passable).toBe(true);
    });

    it("(5,2) — layers[0]=1(x+light), layers[1]=3(v+over) → 通行不可+遮光+★バッジ", () => {
        const d = getDoc();
        const r = resolveCellV3(d, 5, 2);
        expect(r.isVoid).toBe(false);
        expect(r.passable).toBe(false); // x が有効 (upper は v なので下層に従う)
        expect(r.blocksLight).toBe(true); // layers[0] の light=true
        // ★バッジ: layers[1].tiles[3].over=true → cellAboveMask ≠ 0
        const i = 2 * d.width + 5;
        expect(cellAboveMask(d, i)).not.toBe(0);
    });

    it("(2,2)-(3,3) void bridge — 通行不可+遮光 (layers[0]=1)", () => {
        const d = getDoc();
        for (const [x, y] of [[2, 2], [3, 2], [2, 3], [3, 3]] as [number, number][]) {
            const r = resolveCellV3(d, x, y);
            expect(r.isVoid).toBe(false);
            expect(r.passable).toBe(false);
            expect(r.blocksLight).toBe(true);
        }
    });

    it("(6,1) — layers[0]=-1 → isVoid=true (通行不可+遮光)", () => {
        const d = getDoc();
        const r = resolveCellV3(d, 6, 1);
        expect(r.isVoid).toBe(true);
        expect(r.passable).toBe(false);
        expect(r.blocksLight).toBe(true);
    });

    it("(5,4) — layers[0]=1(x+light), layers[1]=0(o) → 上層優先=通行可, 遮光あり (全層 light OR)", () => {
        const d = getDoc();
        const r = resolveCellV3(d, 5, 4);
        expect(r.isVoid).toBe(false);
        expect(r.passable).toBe(true);   // layers[1] pass=o が有効 (上層優先)
        // 遮光は全層 light の OR (仕様 §21): layers[0].tiles[1].light=true → blocksLight=true
        expect(r.blocksLight).toBe(true);
    });

    it("グリッド外は void", () => {
        const d = getDoc();
        expect(resolveCellV3(d, -1, 0).isVoid).toBe(true);
        expect(resolveCellV3(d, 8, 0).isVoid).toBe(true);
    });
});

// ============================================================
// §23 拒否ベクタ
// ============================================================

describe("v3 拒否ベクタ (仕様 §20)", () => {
    it("layers[i].tileset が tilesets の範囲外は拒否", () => {
        const m = makeV3();
        m.layers[0].tileset = 2; // tilesets は 2 個 (0〜1) なので 2 は範囲外
        const r = validateEkmapV3(m);
        expect(r.ok).toBe(false);
        if (!r.ok) expect(r.errors.join("\n")).toMatch(/範囲外/);
    });

    it("requires に未対応 capability は拒否", () => {
        const m = makeV3();
        m.requires = ["future-feature"];
        const r = validateEkmapV3(m);
        expect(r.ok).toBe(false);
        if (!r.ok) expect(r.errors.join("\n")).toMatch(/future-feature/);
    });

    it("tilesets が 5 個は拒否 (上限 4)", () => {
        const m = makeV3();
        m.tilesets = [m.tilesets[0], m.tilesets[1], m.tilesets[0], m.tilesets[1], m.tilesets[0]];
        const r = validateEkmapV3(m);
        expect(r.ok).toBe(false);
        if (!r.ok) expect(r.errors.join("\n")).toMatch(/4/);
    });

    it("layers[i].cells の長さが width×height と不一致は拒否", () => {
        const m = makeV3();
        m.layers[0].cells = m.layers[0].cells.slice(0, 39); // 40 必要
        const r = validateEkmapV3(m);
        expect(r.ok).toBe(false);
    });

    it("v3 に cells (単数形) キーは拒否", () => {
        const m = makeV3();
        (m as any).cells = ["........", "........", "........", "........", "........"];
        const r = validateEkmapV3(m);
        expect(r.ok).toBe(false);
        if (!r.ok) expect(r.errors.join("\n")).toMatch(/cells/);
    });

    it("v3 に tileset (単数形) キーは拒否", () => {
        const m = makeV3();
        (m as any).tileset = { tileSize: 32, columns: 8, image: IMG };
        const r = validateEkmapV3(m);
        expect(r.ok).toBe(false);
        if (!r.ok) expect(r.errors.join("\n")).toMatch(/tileset/);
    });

    it("spawn が void (layers[0]=-1) は拒否", () => {
        const m = makeV3();
        m.spawn = { x: 6, y: 1 }; // (6,1) は layers[0]=-1 → void
        const r = validateEkmapV3(m);
        expect(r.ok).toBe(false);
        if (!r.ok) expect(r.errors.join("\n")).toMatch(/通行可/);
    });

    it("spawn が実効通行不可 (pass=x) は拒否", () => {
        const m = makeV3();
        m.spawn = { x: 2, y: 2 }; // (2,2) は layers[0]=1(x) + layers[1]=-1 → 通行不可
        const r = validateEkmapV3(m);
        expect(r.ok).toBe(false);
        if (!r.ok) expect(r.errors.join("\n")).toMatch(/通行可/);
    });
});

// ============================================================
// v2 → v3 等価性質テスト
// ============================================================

describe("v2 → v3 等価性質テスト (仕様 §22.2)", () => {
    function attr(over: Partial<TileAttr>): TileAttr {
        return { ...defaultTileAttr(), ...over };
    }

    function makeV2Doc(tiles: TileAttr[], ground: number[], upper: number[]): MapDocV2 {
        const ts: TilesetDoc = { tileSize: 32, columns: tiles.length, rows: 1, image: IMG, tiles };
        return {
            ekm: 2,
            name: "equiv",
            author: "",
            width: ground.length,
            height: 1,
            tileset: ts,
            ground,
            upper,
            decor: [],
            spawn: { x: 0, y: 0 },
            ambient: { visionRadius: 8 },
        };
    }

    const tiles = [
        attr({}),             // 0: o
        attr({ pass: "x" }), // 1: x
        attr({ pass: "v" }), // 2: v
        attr({ light: true }), // 3: o+light
    ];

    it("全セルで resolveCellV2(v2doc) ≡ resolveCellV3(upgradeV2DocToV3(v2doc))", () => {
        // 4 セル: ground=[0,1,2,3], upper=[-1,2,0,-1]
        const v2doc = makeV2Doc(tiles, [0, 1, 2, 3], [-1, 2, 0, -1]);
        const v3doc = upgradeV2DocToV3(v2doc);

        for (let x = 0; x < 4; x++) {
            const r2 = resolveCellV2(v2doc, x, 0);
            const r3 = resolveCellV3(v3doc, x, 0);
            expect(r3.isVoid).toBe(r2.isVoid);
            expect(r3.passable).toBe(r2.passable);
            expect(r3.blocksLight).toBe(r2.blocksLight);
        }
    });

    it("upgradeV2DocToV3 で tilesets[0] = tileset / layers[0].cells = ground", () => {
        const v2doc = makeV2Doc(tiles, [0, 1, 2, 3], [-1, -1, -1, -1]);
        const v3doc = upgradeV2DocToV3(v2doc);
        expect(v3doc.tilesets.length).toBe(1);
        expect(v3doc.layers.length).toBe(4); // 4 層パディング済み
        expect(v3doc.layers[0].cells).toEqual(v2doc.ground);
        expect(v3doc.layers[1].cells).toEqual(v2doc.upper);
        expect(v3doc.layers[2].cells.every((v) => v === -1)).toBe(true);
        expect(v3doc.layers[3].cells.every((v) => v === -1)).toBe(true);
    });

    it("validateEkmapAny(v2 json) → v3 ドキュメントとして等価に扱える", () => {
        const v2json = makeV2Compat();
        const r = validateEkmapAny(v2json);
        expect(r.ok).toBe(true);
        if (!r.ok) return;
        expect(r.doc.ekm).toBe(3); // v3 に昇格
        const d3 = r.doc as MapDocV3;
        // spawn は元の v2 と同じ位置で通行可
        const spawn = resolveCellV3(d3, v2json.spawn.x, v2json.spawn.y);
        expect(spawn.passable).toBe(true);
    });
});

// ============================================================
// 保存省略の往復不変 (§22.1)
// ============================================================

describe("v3 保存省略往復不変 (仕様 §22.1)", () => {
    it("末尾デフォルト層は省略され、再読込で 4 層にパディングされる", () => {
        const r = validateEkmapV3(makeV3());
        expect(r.ok).toBe(true);
        if (!r.ok) return;

        const json = docToJsonV3(r.doc);
        // makeV3 は layers[0]/[1] に実データあり、[2]/[3] は空なので 2 層だけ出力される
        expect(json.layers.length).toBe(2);
        // 再検証で 4 層にパディング
        const r2 = validateEkmapV3(json);
        expect(r2.ok).toBe(true);
        if (!r2.ok) return;
        expect(r2.doc.layers.length).toBe(4);

        // データが同一
        for (let li = 0; li < 2; li++) {
            expect(r2.doc.layers[li].cells).toEqual(r.doc.layers[li].cells);
            expect(r2.doc.layers[li].tileset).toBe(r.doc.layers[li].tileset);
            expect(r2.doc.layers[li].above).toBe(r.doc.layers[li].above);
        }
        // パディング層は全 -1
        for (let li = 2; li < 4; li++) {
            expect(r2.doc.layers[li].cells.every((v) => v === -1)).toBe(true);
        }
    });

    it("above=true の層は省略されない (末尾でも)", () => {
        const m = makeV3();
        // layers[1] を above=true にする
        m.layers[1].above = true;
        const r = validateEkmapV3(m);
        expect(r.ok).toBe(true);
        if (!r.ok) return;

        const json = docToJsonV3(r.doc);
        // layers[1] は above=true → 省略不可
        expect(json.layers.length).toBeGreaterThanOrEqual(2);
        const l1 = json.layers[1];
        expect(l1.above).toBe(true);
    });

    it("layers[0] は常に出力される (全 -1 でも)", () => {
        // 全セルが空の最小マップ (spawn 位置を設定できないので spawn 検証をスキップ)
        // layers[0] のセル全 -1 なら spawn が void で拒否されるため、spawn セルだけ 0 にする
        const m = makeV3();
        // layers[0] の全セルを -1 にし、spawn セル (1,1) だけ 0 に戻す
        m.layers[0].cells = new Array(40).fill(-1);
        m.layers[0].cells[1 + 8 * 1] = 0; // spawn=(1,1)
        const r = validateEkmapV3(m);
        expect(r.ok).toBe(true);
        if (!r.ok) return;
        const json = docToJsonV3(r.doc);
        expect(json.layers.length).toBeGreaterThanOrEqual(1);
        expect(json.layers[0]).toBeDefined(); // layers[0] は常に出力
    });
});

// ============================================================
// マップコード roundtrip
// ============================================================

describe("v3 マップコード roundtrip", () => {
    it("encode → decode → validateEkmapV3 → 同一データ", () => {
        const m = makeV3();
        const code = encodeMapCode(JSON.stringify(m));
        expect(code.startsWith("EKM1.")).toBe(true);
        const back = JSON.parse(decodeMapCode(code));
        expect(back.ekm).toBe(3);
        const r = validateEkmapV3(back);
        expect(r.ok).toBe(true);
        if (r.ok) {
            expect(r.doc.width).toBe(m.width);
            expect(r.doc.height).toBe(m.height);
            expect(r.doc.tilesets.length).toBe(2);
        }
    });
});

// ============================================================
// persist lenient 復元 (仕様 §22: strict → lenient の 2 段復元)
// ============================================================

describe("v3 persist lenient 復元", () => {
    it("正常な v3 は strict 経由で復元される", () => {
        const r = validateEkmapV3(makeV3());
        expect(r.ok).toBe(true);
        if (!r.ok) return;
        const json = docToJsonV3(r.doc);
        const restored = tryRestoreDoc(json);
        expect(restored?.ekm).toBe(3);
    });

    it("spawn が不正でも lenient 復元で作業を保持する", () => {
        const m = makeV3();
        m.spawn = { x: 6, y: 1 }; // void 上 → strict 拒否
        const restored = tryRestoreDoc(m);
        expect(restored?.ekm).toBe(3);
        expect(restored?.spawn).toEqual({ x: 6, y: 1 }); // spawn はそのまま保持
    });

    it("tilesets が破損していると null (描画不可のため復元を諦める)", () => {
        const m = makeV3();
        m.tilesets = []; // 最低 1 セット必要
        const restored = tryRestoreDoc(m);
        expect(restored).toBeNull();
    });

    it("layers が部分的に不正でも lenient が -1 に置換して復元する", () => {
        const m = makeV3();
        // layers[0].cells に範囲外値を混入
        m.layers[0].cells[0] = 999; // tilecount=16 なので範囲外
        const restored = tryRestoreDoc(m);
        expect(restored?.ekm).toBe(3);
        if (restored?.ekm === 3) {
            // 範囲外は -1 に置換
            expect((restored as MapDocV3).layers[0].cells[0]).toBe(-1);
        }
    });
});

// ============================================================
// v3 検証規則の追加カバレッジ
// ============================================================

describe("v3 検証規則 (仕様 §20) 追加", () => {
    it("正常系: 全フィールドが検証を通る", () => {
        const r = validateEkmapV3(makeV3());
        expect(r.ok).toBe(true);
        if (r.ok) {
            expect(r.warnings).toEqual([]);
            expect(r.doc.ekm).toBe(3);
        }
    });

    it("JSON 4 MB 超は拒否", () => {
        expect(validateEkmapV3(makeV3(), 4 * 1024 * 1024 + 1).ok).toBe(false);
    });

    it("未知 decor kind はエントリ警告スキップ (v1/v2 同様)", () => {
        const m = makeV3();
        m.decor.push({ kind: "totem", x: 1, y: 1 });
        const r = validateEkmapV3(m);
        expect(r.ok).toBe(true);
        if (r.ok) {
            expect(r.warnings.length).toBe(1);
            expect(r.doc.decor.length).toBe(1);
        }
    });

    it("layers 配列 0 要素は拒否", () => {
        const m = makeV3();
        m.layers = [];
        expect(validateEkmapV3(m).ok).toBe(false);
    });

    it("layers 5 要素は拒否 (上限 4)", () => {
        const m = makeV3();
        const extra = { tileset: 0, cells: new Array(40).fill(-1) };
        m.layers = [m.layers[0], m.layers[1], extra, extra, extra];
        expect(validateEkmapV3(m).ok).toBe(false);
    });

    it("name 0 文字は拒否 / 65 文字は拒否", () => {
        const a = makeV3();
        a.name = "";
        expect(validateEkmapV3(a).ok).toBe(false);
        const b = makeV3();
        b.name = "a".repeat(65);
        expect(validateEkmapV3(b).ok).toBe(false);
    });

    it("docToJsonV3 → validateEkmapV3 ラウンドトリップが成立する", () => {
        const r = validateEkmapV3(makeV3());
        expect(r.ok).toBe(true);
        if (!r.ok) return;
        const json = docToJsonV3(r.doc);
        const r2 = validateEkmapV3(JSON.parse(JSON.stringify(json)));
        expect(r2.ok).toBe(true);
        if (r2.ok) {
            // layers[0]/[1] のデータが同一
            expect(r2.doc.layers[0].cells).toEqual(r.doc.layers[0].cells);
            expect(r2.doc.layers[1].cells).toEqual(r.doc.layers[1].cells);
        }
    });
});

// ============================================================
// render-smoke 相当: v3Descriptor の決定論ゲート
// ============================================================

describe("v3 render-smoke: 描画駆動データ層の決定論ゲート", () => {
    // 依存ゼロの決定論ハッシュ (FNV-1a 32bit → 8 桁 hex)
    function fnv1a(s: string): string {
        let h = 0x811c9dc5;
        for (let i = 0; i < s.length; i++) {
            h ^= s.charCodeAt(i);
            h = Math.imul(h, 0x01000193);
        }
        return (h >>> 0).toString(16).padStart(8, "0");
    }

    function getDoc(): MapDocV3 {
        const r = validateEkmapV3(makeV3());
        if (!r.ok) throw new Error("fixture invalid");
        return r.doc;
    }

    /**
     * v3 視覚記述子: 各セルの isVoid/passable/blocksLight/aboveMask/tileid×4 を連結。
     * render.ts の drawCellBaseV3 が使うデータを完全にカバーする。
     */
    function v3Descriptor(doc: MapDocV3): string {
        let s = "";
        for (let y = 0; y < doc.height; y++) {
            for (let x = 0; x < doc.width; x++) {
                const i = y * doc.width + x;
                const r = resolveCellV3(doc, x, y);
                const am = cellAboveMask(doc, i);
                const tids = doc.layers.map((l) => l.cells[i]).join(",");
                s += `${r.isVoid ? "V" : "."}${r.passable ? "P" : "x"}${r.blocksLight ? "L" : "."}a${am}t${tids}|`;
            }
        }
        return s;
    }

    it("v3: 描画駆動データ層が決定論的 (同入力で同ハッシュ)", () => {
        const a = fnv1a(v3Descriptor(getDoc()));
        const b = fnv1a(v3Descriptor(getDoc()));
        expect(a).toBe(b);
    });

    it("v3: 凍結ベースラインと一致", () => {
        expect(fnv1a(v3Descriptor(getDoc()))).toMatchInlineSnapshot(`"4f4d16e0"`);
    });
});

// ============================================================
// §21.2 "v" チェーン → T1 フォールバック (契約監査 2026-06-13 で欠落指摘されたベクタ)
// spec §23 の (5,4) 相当: 層4 "v" → 層3 空 → 層2 空 → 層1 "x" → 通行不可・遮光なし
// ============================================================

describe('v3 "v" チェーンのフォールバック (仕様 §21.2 / §23 (5,4) 相当)', () => {
    function makeVChain(): any {
        return {
            ekm: 3,
            name: "VChain",
            author: "test",
            width: 2,
            height: 1,
            tilesets: [
                {
                    tileSize: 32,
                    columns: 8,
                    image: IMG,
                    tiles: [
                        { id: 1, pass: "x", over: false, light: false, tag: 0, dir: 15 },
                        { id: 2, pass: "v", over: false, light: false, tag: 0, dir: 15 },
                    ],
                },
            ],
            layers: [
                { tileset: 0, cells: [2, 1] },   // (0,0)=T1 "v" / (1,0)=T1 "x"
                { tileset: 0, cells: [-1, -1] },
                { tileset: 0, cells: [-1, -1] },
                { tileset: 0, cells: [-1, 2] },  // (1,0) の層4 = "v"
            ],
            decor: [],
            spawn: { x: 0, y: 0 },
            ambient: { visionRadius: 8 },
        };
    }

    it('(1,0): 層4 "v" → 層3/2 空 → 層1 "x" → 通行不可・遮光なし', () => {
        const r = validateEkmapV3(makeVChain());
        expect(r.ok).toBe(true);
        if (!r.ok) return;
        const c = resolveCellV3(r.doc, 1, 0);
        expect(c.isVoid).toBe(false);
        expect(c.passable).toBe(false);    // "v" は素通りして T1 の "x" が効く
        expect(c.blocksLight).toBe(false); // どの層も light=false → 視界は通る
    });

    it('(0,0): 全層が "v" または空 → T1 の "v" は "o" 扱い = 通行可 (spawn 合格)', () => {
        const r = validateEkmapV3(makeVChain());
        expect(r.ok).toBe(true);
        if (!r.ok) return;
        const c = resolveCellV3(r.doc, 0, 0);
        expect(c.isVoid).toBe(false);
        expect(c.passable).toBe(true);
    });
});

// ============================================================
// §7 tileScale — ambient 経由 round-trip (v3 専用)
// ============================================================

describe("tileScale round-trip (仕様 §7)", () => {
    it("tileScale=0.75 が docToJsonV3 経由で ambient に残る", () => {
        const r = validateEkmapV3(makeV3());
        expect(r.ok).toBe(true);
        if (!r.ok) return;
        const d = r.doc;
        // 直接 ambient に書き込む (エディタの slider input と同じ操作)
        d.ambient.tileScale = 0.75;
        const json = docToJsonV3(d);
        expect((json.ambient as any).tileScale).toBe(0.75);
    });

    it("tileScale=1.0 はキーが省略される (既定値、JSON 省略設計)", () => {
        const r = validateEkmapV3(makeV3());
        expect(r.ok).toBe(true);
        if (!r.ok) return;
        const d = r.doc;
        // 1.0 のときはキーを削除する (エディタの挙動と同じ)
        delete d.ambient.tileScale;
        const json = docToJsonV3(d);
        // tileScale キーが存在しないか、あっても 1.0
        const ts = (json.ambient as any).tileScale;
        expect(ts === undefined || ts === 1.0).toBe(true);
    });

    it("tileScale=0.5 が validateEkmapV3 を通過し ambient に保全される", () => {
        const m = makeV3();
        m.ambient = { visionRadius: 8, tileScale: 0.5 };
        const r = validateEkmapV3(m);
        expect(r.ok).toBe(true);
        if (!r.ok) return;
        // 未知サブキー保全 (§9 黙って許容) — tileScale は generic 保全される
        expect((r.doc.ambient as any).tileScale).toBe(0.5);
    });

    it("tileScale=0.5 は docToJsonV3 → validateEkmapV3 でラウンドトリップする", () => {
        const r = validateEkmapV3(makeV3());
        expect(r.ok).toBe(true);
        if (!r.ok) return;
        r.doc.ambient.tileScale = 0.5;
        const json = docToJsonV3(r.doc);
        const r2 = validateEkmapV3(JSON.parse(JSON.stringify(json)));
        expect(r2.ok).toBe(true);
        if (!r2.ok) return;
        expect((r2.doc.ambient as any).tileScale).toBe(0.5);
    });
});
