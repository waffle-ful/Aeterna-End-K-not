// EKM v2 (仕様 §13〜§16): 検証規則 / セル解決規則 / マップコードラウンドトリップ

import { describe, expect, it } from "vitest";
// 8 列 × 2 行 (32px) = 256×64 PNG、tilecount 16
import tilesetB64 from "./fixtures/tileset8x2.b64.txt?raw";
import {
    type MapDocV2,
    type TileAttr,
    type TilesetDoc,
    defaultTileAttr,
    docToJsonV2,
    resolveCellV2,
} from "../src/model";
import { parsePngDataUri } from "../src/png";
import { validateEkmapAny, validateEkmapV2 } from "../src/validate";
import { decodeMapCode, encodeMapCode } from "../src/mapcode";
import { tryRestoreDoc } from "../src/persist";

const IMG = "data:image/png;base64," + tilesetB64.replace(/\s+/g, "");

/* eslint-disable @typescript-eslint/no-explicit-any */
function makeV2(): any {
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
            // id 1 = 通行不可+遮光
            tiles: [{ id: 1, pass: "x", over: false, light: true, tag: 0, dir: 15 }],
        },
        layers: {
            // ground: (0,0) は void、(2,1) は不可タイル 1
            ground: [-1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0],
            upper: [-1, -1, -1, -1, -1, 2, -1, -1, -1, -1, -1, -1],
        },
        decor: [{ kind: "light", x: 1, y: 1 }],
        spawn: { x: 1, y: 1 },
        ambient: { visionRadius: 8 },
    };
}

describe("v2 fixture (tileset8x2)", () => {
    it("256×64 の PNG として読める", () => {
        const png = parsePngDataUri(IMG);
        expect(png.ok).toBe(true);
        if (png.ok) {
            expect(png.width).toBe(256);
            expect(png.height).toBe(64);
            expect(png.byteLength).toBeLessThan(1024 * 1024);
        }
    });
});

describe("v2 検証規則 (仕様 §16)", () => {
    it("正常系: tileset が dense 化され layers/decor/spawn が通る", () => {
        const r = validateEkmapV2(makeV2());
        expect(r.ok).toBe(true);
        if (r.ok) {
            expect(r.warnings).toEqual([]);
            expect(r.doc.ekm).toBe(2);
            expect(r.doc.tileset.columns).toBe(8);
            expect(r.doc.tileset.rows).toBe(2);
            expect(r.doc.tileset.tiles.length).toBe(16);
            // 疎 tiles[] → dense: id 1 だけ非デフォルト
            expect(r.doc.tileset.tiles[1].pass).toBe("x");
            expect(r.doc.tileset.tiles[1].light).toBe(true);
            expect(r.doc.tileset.tiles[0]).toEqual(defaultTileAttr());
            expect(r.doc.ground.length).toBe(12);
            expect(r.doc.upper.length).toBe(12);
            expect(r.doc.decor.length).toBe(1);
        }
    });

    it("upper 省略は全 -1 として通る", () => {
        const m = makeV2();
        delete m.layers.upper;
        const r = validateEkmapV2(m);
        expect(r.ok).toBe(true);
        if (r.ok) expect(r.doc.upper.every((v: number) => v === -1)).toBe(true);
    });

    it("tileset 欠落は拒否", () => {
        const m = makeV2();
        delete m.tileset;
        const r = validateEkmapV2(m);
        expect(r.ok).toBe(false);
        if (!r.ok) expect(r.errors.join("\n")).toContain("tileset");
    });

    it("画像幅 ≠ columns×tileSize は拒否", () => {
        const m = makeV2();
        m.tileset.columns = 7; // 7×32=224 ≠ 256
        const r = validateEkmapV2(m);
        expect(r.ok).toBe(false);
        if (!r.ok) expect(r.errors.join("\n")).toContain("一致しません");
    });

    it("tileSize が画像高さを割り切れないのも拒否", () => {
        const m = makeV2();
        m.tileset.tileSize = 24; // 高さ 64 % 24 ≠ 0 (幅も不一致になるので columns を調整しない)
        expect(validateEkmapV2(m).ok).toBe(false);
    });

    it("PNG data URI でない image は拒否", () => {
        const m = makeV2();
        m.tileset.image = "data:image/jpeg;base64,AAAA";
        expect(validateEkmapV2(m).ok).toBe(false);
    });

    it("cells キーの存在は拒否 (v2 では layers のみ)", () => {
        const m = makeV2();
        m.cells = ["....", "....", "...."];
        const r = validateEkmapV2(m);
        expect(r.ok).toBe(false);
        if (!r.ok) expect(r.errors.join("\n")).toContain("cells");
    });

    it("layers.ground 欠落・長さ不一致は拒否", () => {
        const a = makeV2();
        delete a.layers.ground;
        expect(validateEkmapV2(a).ok).toBe(false);
        const b = makeV2();
        b.layers.ground = b.layers.ground.slice(0, 11); // 12 必要
        expect(validateEkmapV2(b).ok).toBe(false);
    });

    it("範囲外タイル id (tilecount=16 で 16) は拒否、-1 は許容", () => {
        const m = makeV2();
        m.layers.ground[1] = 16;
        const r = validateEkmapV2(m);
        expect(r.ok).toBe(false);
        if (!r.ok) expect(r.errors.join("\n")).toContain("範囲外");
    });

    it("tiles[] の id 重複・id 範囲外・pass 不正は拒否", () => {
        const a = makeV2();
        a.tileset.tiles = [
            { id: 1, pass: "x" },
            { id: 1, pass: "o" },
        ];
        expect(validateEkmapV2(a).ok).toBe(false);
        const b = makeV2();
        b.tileset.tiles = [{ id: 16, pass: "x" }];
        expect(validateEkmapV2(b).ok).toBe(false);
        const c = makeV2();
        c.tileset.tiles = [{ id: 0, pass: "w" }];
        expect(validateEkmapV2(c).ok).toBe(false);
    });

    it("tag / dir の範囲外は拒否、未知キーは黙って許容", () => {
        const a = makeV2();
        a.tileset.tiles = [{ id: 0, tag: 100 }];
        expect(validateEkmapV2(a).ok).toBe(false);
        const b = makeV2();
        b.tileset.tiles = [{ id: 0, dir: 16 }];
        expect(validateEkmapV2(b).ok).toBe(false);
        const c = makeV2();
        c.tileset.tiles = [{ id: 0, pass: "x", futureKey: 123 }];
        const r = validateEkmapV2(c);
        expect(r.ok).toBe(true);
        if (r.ok) expect(r.warnings).toEqual([]);
    });

    it("spawn が実効通行不可セル (void / pass=x) は拒否", () => {
        const a = makeV2();
        a.spawn = { x: 0, y: 0 }; // ground = -1 (void)
        const ra = validateEkmapV2(a);
        expect(ra.ok).toBe(false);
        if (!ra.ok) expect(ra.errors.join("\n")).toContain("通行可");
        const b = makeV2();
        b.spawn = { x: 2, y: 1 }; // ground = タイル 1 (pass "x")
        expect(validateEkmapV2(b).ok).toBe(false);
    });

    it("spawn 位置の upper が pass='o' なら下が x でも通る (実効通行は upper 優先)", () => {
        const m = makeV2();
        m.tileset.tiles = [
            { id: 1, pass: "x", light: true },
            { id: 2, pass: "o" },
        ];
        m.layers.upper[6] = 2; // (2,1) ground=1(x) の上に upper=2(o)
        m.spawn = { x: 2, y: 1 };
        expect(validateEkmapV2(m).ok).toBe(true);
    });

    it("JSON 4 MB 超は拒否 (512 KB は v2 では通る)", () => {
        expect(validateEkmapV2(makeV2(), 512 * 1024 + 1).ok).toBe(true);
        expect(validateEkmapV2(makeV2(), 4 * 1024 * 1024 + 1).ok).toBe(false);
    });

    it("未知 decor kind はエントリ警告スキップ (v1 同様)", () => {
        const m = makeV2();
        m.decor.push({ kind: "totem", x: 1, y: 1 });
        const r = validateEkmapV2(m);
        expect(r.ok).toBe(true);
        if (r.ok) {
            expect(r.warnings.length).toBe(1);
            expect(r.doc.decor.length).toBe(1);
        }
    });

    it("validateEkmapAny は ekm で振り分ける (ekm:2 → v2 / それ以外 → v1)", () => {
        const r2 = validateEkmapAny(makeV2());
        expect(r2.ok).toBe(true);
        if (r2.ok) expect(r2.doc.ekm).toBe(2);
        const r1 = validateEkmapAny({ ekm: 3 });
        expect(r1.ok).toBe(false); // v1 パスで ekm != 1 拒否
    });

    it("docToJsonV2 → 再検証 のラウンドトリップが成立する (tiles は疎で出力)", () => {
        const r = validateEkmapV2(makeV2());
        expect(r.ok).toBe(true);
        if (!r.ok) return;
        const json = docToJsonV2(r.doc);
        expect(json.tileset.tiles?.length).toBe(1); // デフォルト値のチップは省略
        const r2 = validateEkmapV2(JSON.parse(JSON.stringify(json)));
        expect(r2.ok).toBe(true);
        if (r2.ok) {
            expect(r2.doc.ground).toEqual(r.doc.ground);
            expect(r2.doc.upper).toEqual(r.doc.upper);
            expect(r2.doc.tileset.tiles).toEqual(r.doc.tileset.tiles);
        }
    });

    it("自動保存復元 (tryRestoreDoc) は v2 を strict → lenient の順で復元する", () => {
        const ok = tryRestoreDoc(makeV2());
        expect(ok?.ekm).toBe(2);
        // spawn が void 上 (strict 拒否) でも lenient 復元で作業は保持される
        const broken = makeV2();
        broken.spawn = { x: 0, y: 0 };
        const doc = tryRestoreDoc(broken);
        expect(doc?.ekm).toBe(2);
        expect(doc?.spawn).toEqual({ x: 0, y: 0 });
    });
});

describe("v2 セル解決規則 (仕様 §15)", () => {
    function attr(over: Partial<TileAttr>): TileAttr {
        return { ...defaultTileAttr(), ...over };
    }

    function makeDoc(tiles: TileAttr[], ground: number[], upper: number[]): MapDocV2 {
        const ts: TilesetDoc = { tileSize: 32, columns: tiles.length, rows: 1, image: IMG, tiles };
        return {
            ekm: 2,
            name: "t",
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

    // タイル 0 = o / 1 = x / 2 = v / 3 = o+light
    const tiles = [attr({}), attr({ pass: "x" }), attr({ pass: "v" }), attr({ light: true })];

    it("G = -1 は void (通行不可+遮光)、グリッド外も void", () => {
        const d = makeDoc(tiles, [-1], [-1]);
        expect(resolveCellV2(d, 0, 0)).toEqual({ isVoid: true, passable: false, blocksLight: true });
        expect(resolveCellV2(d, 5, 0)).toEqual({ isVoid: true, passable: false, blocksLight: true });
        expect(resolveCellV2(d, -1, 0).isVoid).toBe(true);
    });

    it("U.pass='v' は下層に従う / 'v' 以外は upper が勝つ", () => {
        // ground=x, upper=v → x のまま
        const a = makeDoc(tiles, [1], [2]);
        expect(resolveCellV2(a, 0, 0).passable).toBe(false);
        // ground=x, upper=o → 通行可
        const b = makeDoc(tiles, [1], [0]);
        expect(resolveCellV2(b, 0, 0).passable).toBe(true);
        // ground=o, upper=x → 不可
        const c = makeDoc(tiles, [0], [1]);
        expect(resolveCellV2(c, 0, 0).passable).toBe(false);
    });

    it("ground の 'v' は 'o' 扱い (仕様 §14.1)", () => {
        const d = makeDoc(tiles, [2], [-1]);
        const r = resolveCellV2(d, 0, 0);
        expect(r.passable).toBe(true);
        expect(r.isVoid).toBe(false);
    });

    it("遮光は G.light || U.light の OR (通行とは独立)", () => {
        expect(resolveCellV2(makeDoc(tiles, [3], [-1]), 0, 0).blocksLight).toBe(true); // ground のみ
        expect(resolveCellV2(makeDoc(tiles, [0], [3]), 0, 0).blocksLight).toBe(true); // upper のみ
        expect(resolveCellV2(makeDoc(tiles, [0], [0]), 0, 0).blocksLight).toBe(false); // 両方 false
        // light=true でも pass はデフォルト o のまま通行可
        expect(resolveCellV2(makeDoc(tiles, [3], [-1]), 0, 0).passable).toBe(true);
    });
});

describe("v2 マップコード (仕様 §8 と同一エンコード)", () => {
    it("エンコード→デコード ラウンドトリップが一致する (tileset data URI 込み)", () => {
        const m = makeV2();
        const code = encodeMapCode(JSON.stringify(m));
        expect(code.startsWith("EKM1.")).toBe(true);
        const back = JSON.parse(decodeMapCode(code));
        expect(back).toEqual(m);
        // デコード結果はそのまま v2 検証を通る
        expect(validateEkmapAny(back).ok).toBe(true);
    });
});
