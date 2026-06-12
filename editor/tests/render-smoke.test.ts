// 描画駆動データ層の回帰スモーク (Stage 0)
//
// 目的: render.ts (MapRenderer) は PIXI/DOM 依存で node の vitest からは画素検査できないため、
// 「描画を駆動する純粋な per-cell 視覚分類」(getCell / resolveCellV2 / タイル属性) をグリッド全走査して
// 決定論ハッシュ化し、(1) 同入力で同ハッシュが必ず出ること (決定論性そのものの検証) と
// (2) 凍結ベースライン (inline snapshot) と一致することを保証する。
//
// これは Stage 3 のレイヤー分割 (ground/upper canvas 分割・over スパース Sprite 化・前後関係) が
// 「セルごとの視覚決定」を退行させていないかを WebGL 無しで検出するゲートになる。
//
// 注: 真の画素レベルスモーク (PIXI renderer.extract によるレンダーテクスチャのハッシュ比較) は
// node に WebGL が無いためここでは行わない。ブラウザテストランナー (vitest browser mode / Playwright) が
// 要るが、WebGL ドライバ/AA 差で環境依存になりやすい (設計 ROADMAP の判断どおり) ため Stage 3 着手時に
// 別途仕込む。本スモークは「描画の入力となるデータ決定」の決定論ゲートに限定する。

import { describe, expect, it } from "vitest";
import tilesetB64 from "./fixtures/tileset8x2.b64.txt?raw";
import { type MapDocV2, getCell, resolveCellV2 } from "../src/model";
import { validateEkmapV2 } from "../src/validate";

const IMG = "data:image/png;base64," + tilesetB64.replace(/\s+/g, "");

// 依存ゼロの決定論ハッシュ (FNV-1a 32bit → 8 桁 hex)
function fnv1a(s: string): string {
    let h = 0x811c9dc5;
    for (let i = 0; i < s.length; i++) {
        h ^= s.charCodeAt(i);
        h = Math.imul(h, 0x01000193);
    }
    return (h >>> 0).toString(16).padStart(8, "0");
}

// ── 代表 v1 マップ (床/壁/奈落 + 孤立柱) ─────────────────────────────────────
// getCell は "." 床 / "#" 壁 / "-" 奈落 を返す。柱判定 (4 近傍) は classify.test.ts が別途網羅するが、
// ここでは getCell の生分類をグリッド全走査でハッシュし、データ決定の安定性を見る。
function makeV1() {
    const cells = [
        "######",
        "#....#",
        "#.#..#",
        "#..-.#",
        "#....#",
        "######",
    ];
    return {
        ekm: 1 as const,
        name: "Smoke V1",
        author: "test",
        width: 6,
        height: 6,
        grid: cells.join("").split(""),
        decor: [],
        spawn: { x: 2, y: 1 },
        ambient: { visionRadius: 8 },
    };
}

// ── 代表 v2 マップ (タイルセット + ground/upper 2 レイヤー + over/light/void) ──
function makeV2(): MapDocV2 {
    const json = {
        ekm: 2,
        name: "Smoke V2",
        author: "test",
        width: 4,
        height: 3,
        tileset: {
            tileSize: 32,
            columns: 8,
            image: IMG,
            tiles: [
                { id: 1, pass: "x", over: false, light: true, tag: 0, dir: 15 }, // 壁 (通行不可・遮光)
                { id: 2, pass: "o", over: true, light: false, tag: 0, dir: 15 }, // 屋根 (プレイヤーより手前)
                { id: 3, pass: "v", over: false, light: false, tag: 0, dir: 15 }, // 下層追従
            ],
        },
        layers: {
            // ground: 一部 void (-1) を混ぜる
            ground: [
                0, 1, 0, -1,
                0, 0, 1, 0,
                -1, 0, 0, 0,
            ],
            // upper: over タイルと空 (-1) を混ぜる
            upper: [
                -1, -1, 2, -1,
                -1, 3, -1, -1,
                -1, -1, 2, -1,
            ],
        },
        decor: [],
        spawn: { x: 0, y: 0 },
        ambient: { visionRadius: 6 },
    };
    const res = validateEkmapV2(json);
    if (!res.ok) throw new Error("fixture invalid: " + res.errors.join(", "));
    return res.doc;
}

// グリッド全走査して視覚記述子の連結文字列を作る
function v1Descriptor(doc: ReturnType<typeof makeV1>): string {
    let s = "";
    for (let y = 0; y < doc.height; y++)
        for (let x = 0; x < doc.width; x++) s += getCell(doc as never, x, y);
    return s;
}

function v2Descriptor(doc: MapDocV2): string {
    let s = "";
    for (let y = 0; y < doc.height; y++) {
        for (let x = 0; x < doc.width; x++) {
            const i = y * doc.width + x;
            const r = resolveCellV2(doc, x, y);
            const g = doc.ground[i];
            const u = doc.upper[i];
            // over は upper タイルの属性。Stage 3 の前後関係 (z) 分割が触る最重要ビット
            const over = u >= 0 ? (doc.tileset.tiles[u]?.over ?? false) : false;
            s += `${r.isVoid ? "V" : "."}${r.passable ? "P" : "x"}${r.blocksLight ? "L" : "."}${over ? "O" : "."}g${g}u${u}|`;
        }
    }
    return s;
}

describe("render-smoke: 描画駆動データ層の決定論ゲート", () => {
    it("v1: getCell グリッド分類が決定論的 (同入力で同ハッシュ)", () => {
        const a = fnv1a(v1Descriptor(makeV1()));
        const b = fnv1a(v1Descriptor(makeV1()));
        expect(a).toBe(b); // 決定論性そのものの検証
    });

    it("v1: 凍結ベースラインと一致", () => {
        expect(fnv1a(v1Descriptor(makeV1()))).toMatchInlineSnapshot(`"d044fdad"`);
    });

    it("v2: resolveCellV2 + over/light/void/tile-id がグリッド全域で決定論的", () => {
        const a = fnv1a(v2Descriptor(makeV2()));
        const b = fnv1a(v2Descriptor(makeV2()));
        expect(a).toBe(b);
    });

    it("v2: 凍結ベースラインと一致", () => {
        expect(fnv1a(v2Descriptor(makeV2()))).toMatchInlineSnapshot(`"0fb08db8"`);
    });
});
