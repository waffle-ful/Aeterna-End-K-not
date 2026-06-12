// タイルセットインポート純関数層の契約テスト (DOM 非依存)

import { describe, expect, it } from "vitest";
import {
    type SliceParams,
    appendTileToAtlas,
    detectTransparentTiles,
    enumerateCandidates,
    rebakeAtlas,
    resizeNearestNeighbor,
    scoreCandidates,
} from "../src/tileset-import";

// ============================================================
// enumerateCandidates
// ============================================================

describe("enumerateCandidates", () => {
    it("256×64 (8col×2row 32px) は margin=0 spacing=0 tileSize=32 を含む", () => {
        const candidates = enumerateCandidates(256, 64);
        const found = candidates.find(
            (c) => c.tileSize === 32 && c.margin === 0 && c.spacing === 0 && c.cols === 8 && c.rows === 2,
        );
        expect(found).toBeTruthy();
    });

    it("margin=2 spacing=1 付き素材: 2*2 + 4*(32+1) -1 = 143 幅 → 列挙に含まれる", () => {
        // cols=4, rows=2, size=32, margin=2, spacing=1
        // W = 2*2 + 4*32 + 3*1 = 4+128+3 = 135
        const W = 2 * 2 + 4 * 32 + 3 * 1; // 135
        const H = 2 * 2 + 2 * 32 + 1 * 1; // 69
        const candidates = enumerateCandidates(W, H);
        const found = candidates.find(
            (c) => c.tileSize === 32 && c.margin === 2 && c.spacing === 1 && c.cols === 4 && c.rows === 2,
        );
        expect(found).toBeTruthy();
    });

    it("tilecount > 4096 になる組み合わせは含まない", () => {
        // 8px tile, 512×512 で cols=rows=64, count=4096 は境界上なので通る
        const candidates = enumerateCandidates(512, 512);
        for (const c of candidates) {
            expect(c.cols * c.rows).toBeLessThanOrEqual(4096);
        }
    });

    it("1×1 の最小タイルセット (8px) は通る", () => {
        const candidates = enumerateCandidates(8, 8);
        const found = candidates.find((c) => c.tileSize === 8 && c.cols === 1 && c.rows === 1);
        expect(found).toBeTruthy();
    });

    it("割り切れない寸法の場合は候補が0件になり得る (例: 17×17)", () => {
        // 17 は 8,16,24,... のどれでも 2m+c*s+(c-1)*g で整合させにくい
        // 17=2*0+1*17+0 → 17 は TILE_SIZES にないのでこのサイズは候補無し
        const candidates = enumerateCandidates(17, 17);
        // 実際にどうなるかは仕様依存だが、少なくとも 4096 超えは無いことを確認
        for (const c of candidates) {
            expect(c.cols * c.rows).toBeLessThanOrEqual(4096);
        }
    });
});

// ============================================================
// scoreCandidates
// ============================================================

describe("scoreCandidates", () => {
    /**
     * 2x2 タイル (各 16px) で作ったテスト RGBA。
     * タイル境界はっきり: 左列=黒、右列=白。
     * 上段と下段は同色なので垂直境界の対比が出る。
     */
    function makeContrastRgba(
        cols: number,
        rows: number,
        tileSize: number,
    ): Uint8ClampedArray {
        const w = cols * tileSize;
        const h = rows * tileSize;
        const rgba = new Uint8ClampedArray(w * h * 4);
        for (let y = 0; y < h; y++) {
            for (let x = 0; x < w; x++) {
                const col = Math.floor(x / tileSize) % 2; // 偶数列=黒, 奇数列=白
                const v = col === 0 ? 0 : 255;
                const idx = (y * w + x) * 4;
                rgba[idx] = v;
                rgba[idx + 1] = v;
                rgba[idx + 2] = v;
                rgba[idx + 3] = 255;
            }
        }
        return rgba;
    }

    it("境界が高コントラストな候補のスコアが均一画像より高い", () => {
        const cols = 4;
        const rows = 2;
        const size = 16;
        const rgba = makeContrastRgba(cols, rows, size);
        const candidates = enumerateCandidates(cols * size, rows * size);
        const scored = scoreCandidates(rgba, cols * size, rows * size, candidates);

        // 正解候補 (size=16, margin=0, spacing=0)
        const best = scored.find(
            (c) => c.params.tileSize === size && c.params.margin === 0 && c.params.spacing === 0,
        );
        expect(best).toBeTruthy();
        // 均一画像なら score ≈ 0 なので、コントラストがあればスコアが正
        expect(best!.score).toBeGreaterThan(0);
    });

    it("空の候補リストでは空配列を返す", () => {
        const rgba = new Uint8ClampedArray(4); // 1px
        expect(scoreCandidates(rgba, 1, 1, [])).toEqual([]);
    });

    it("結果は score 降順", () => {
        const rgba = makeContrastRgba(2, 1, 16);
        const candidates = enumerateCandidates(32, 16);
        const scored = scoreCandidates(rgba, 32, 16, candidates);
        for (let i = 0; i < scored.length - 1; i++) {
            expect(scored[i].score).toBeGreaterThanOrEqual(scored[i + 1].score);
        }
    });
});

// ============================================================
// detectTransparentTiles
// ============================================================

describe("detectTransparentTiles", () => {
    it("全ピクセル alpha=0 のタイルを検出する", () => {
        // 2x1, tileSize=4: タイル0=透明, タイル1=不透明
        const cols = 2;
        const rows = 1;
        const size = 4;
        const rgba = new Uint8ClampedArray(cols * size * rows * size * 4);
        // タイル1 (右側) だけ alpha=255 にする
        for (let y = 0; y < size; y++) {
            for (let x = size; x < cols * size; x++) {
                const idx = (y * cols * size + x) * 4;
                rgba[idx + 3] = 255;
            }
        }
        const transparent = detectTransparentTiles(rgba, cols, rows, size);
        expect(transparent.has(0)).toBe(true); // 左タイルが透明
        expect(transparent.has(1)).toBe(false); // 右タイルは不透明
    });

    it("全タイルに色があれば空セットを返す", () => {
        const rgba = new Uint8ClampedArray(4 * 4 * 4).fill(128); // alpha=128
        const transparent = detectTransparentTiles(rgba, 2, 2, 2);
        expect(transparent.size).toBe(0);
    });

    it("全タイルが透明なら全インデックスを返す", () => {
        const rgba = new Uint8ClampedArray(16 * 16 * 4); // alpha=0
        const transparent = detectTransparentTiles(rgba, 4, 4, 4);
        expect(transparent.size).toBe(16);
    });
});

// ============================================================
// rebakeAtlas
// ============================================================

describe("rebakeAtlas", () => {
    it("margin=0 spacing=0 のときは入力バッファをそのまま返す (reencoded=false)", () => {
        const rgba = new Uint8ClampedArray([1, 2, 3, 4]);
        const params: SliceParams = { tileSize: 1, margin: 0, spacing: 0, cols: 1, rows: 1 };
        const result = rebakeAtlas(rgba, params);
        expect(result.reencoded).toBe(false);
        // 同一バッファの参照
        expect(result.rgba).toBe(rgba);
    });

    it("margin/spacing あり素材を隙間なしアトラスに変換する", () => {
        // 2x2 タイル (size=2), margin=1, spacing=1
        // W = 2*1 + 2*2 + 1*1 = 7, H = 7
        const cols = 2;
        const rows = 2;
        const size = 2;
        const margin = 1;
        const spacing = 1;
        const srcW = 2 * margin + cols * size + (cols - 1) * spacing; // 7
        const srcH = 2 * margin + rows * size + (rows - 1) * spacing; // 7

        // ソース RGBA: タイルに tileIndex 値 (0〜3) を書き込む
        const src = new Uint8ClampedArray(srcW * srcH * 4);
        const tilePositions = [
            { c: 0, r: 0 },
            { c: 1, r: 0 },
            { c: 0, r: 1 },
            { c: 1, r: 1 },
        ];
        for (let ti = 0; ti < tilePositions.length; ti++) {
            const { c, r } = tilePositions[ti];
            const ox = margin + c * (size + spacing);
            const oy = margin + r * (size + spacing);
            for (let ty = 0; ty < size; ty++) {
                for (let tx = 0; tx < size; tx++) {
                    const idx = ((oy + ty) * srcW + (ox + tx)) * 4;
                    src[idx] = ti * 10;     // R にタイル番号を書く
                    src[idx + 3] = 255;
                }
            }
        }

        const params: SliceParams = { tileSize: size, margin, spacing, cols, rows };
        const result = rebakeAtlas(src, params);

        expect(result.reencoded).toBe(true);
        expect(result.width).toBe(cols * size);   // 4
        expect(result.height).toBe(rows * size);  // 4

        // リベイク後: タイル0 の R 値が 0、タイル1 の R 値が 10...
        for (let ti = 0; ti < 4; ti++) {
            const tc = ti % cols;
            const tr = Math.floor(ti / cols);
            const dx = tc * size;
            const dy = tr * size;
            const idx = (dy * result.width + dx) * 4;
            expect(result.rgba[idx]).toBe(ti * 10);
        }
    });

    it("margin=0 spacing=1 のとき reencoded=true になる", () => {
        // spacing > 0 なので再エンコードが必要
        const cols = 2;
        const rows = 1;
        const size = 4;
        const spacing = 1;
        // W = 2*0 + 2*4 + 1*1 = 9
        const srcW = 2 * 0 + cols * size + (cols - 1) * spacing;
        const src = new Uint8ClampedArray(srcW * size * 4).fill(128);
        const params: SliceParams = { tileSize: size, margin: 0, spacing: 1, cols, rows };
        const result = rebakeAtlas(src, params);
        expect(result.reencoded).toBe(true);
        expect(result.width).toBe(cols * size);
    });

    it("リベイク後の画像サイズは columns×tileSize × rows×tileSize (spec v2 契約)", () => {
        const params: SliceParams = { tileSize: 32, margin: 2, spacing: 2, cols: 4, rows: 3 };
        const srcW = 2 * params.margin + params.cols * params.tileSize + (params.cols - 1) * params.spacing;
        const srcH = 2 * params.margin + params.rows * params.tileSize + (params.rows - 1) * params.spacing;
        const src = new Uint8ClampedArray(srcW * srcH * 4);
        const result = rebakeAtlas(src, params);
        expect(result.width).toBe(params.cols * params.tileSize);
        expect(result.height).toBe(params.rows * params.tileSize);
    });
});

// ============================================================
// appendTileToAtlas
// ============================================================

describe("appendTileToAtlas", () => {
    const TS = 4; // tileSize

    /** 全ピクセルを指定 RGBA 値で埋めた tileSize×tileSize バッファを作る */
    function solidTile(r: number, g: number, b: number, a: number): Uint8ClampedArray {
        const buf = new Uint8ClampedArray(TS * TS * 4);
        for (let i = 0; i < TS * TS; i++) {
            buf[i * 4] = r; buf[i * 4 + 1] = g; buf[i * 4 + 2] = b; buf[i * 4 + 3] = a;
        }
        return buf;
    }

    /** columns×rows アトラスで、指定スロットだけ色付き (alpha=255)、残りは透明 */
    function atlasWithSolid(
        columns: number,
        rows: number,
        solidSlots: number[],
        color: [number, number, number] = [200, 200, 200],
    ): Uint8ClampedArray {
        const w = columns * TS;
        const h = rows * TS;
        const buf = new Uint8ClampedArray(w * h * 4);
        for (const slot of solidSlots) {
            const col = slot % columns;
            const row = Math.floor(slot / columns);
            for (let ty = 0; ty < TS; ty++) {
                for (let tx = 0; tx < TS; tx++) {
                    const idx = ((row * TS + ty) * w + (col * TS + tx)) * 4;
                    buf[idx] = color[0]; buf[idx + 1] = color[1]; buf[idx + 2] = color[2]; buf[idx + 3] = 255;
                }
            }
        }
        return buf;
    }

    it("末尾に透明スロットがある場合は行を増やさずに書き込む", () => {
        // 2列×1行、スロット0が使用中、スロット1が透明
        const atlas = atlasWithSolid(2, 1, [0]);
        const tile = solidTile(100, 0, 0, 255);
        const { rgba, newRows, newTileIndex } = appendTileToAtlas(atlas, 2, 1, TS, tile);
        expect(newRows).toBe(1);       // 行は増えない
        expect(newTileIndex).toBe(1);  // スロット1に追記
        // スロット1の先頭ピクセルが tile の色になっているか
        const dstX = 1 * TS;
        const dstY = 0;
        const idx = (dstY * 2 * TS + dstX) * 4;
        expect(rgba[idx]).toBe(100);
        expect(rgba[idx + 3]).toBe(255);
    });

    it("末尾行が満杯なら新しい行を1行追加して先頭セルに配置する", () => {
        // 2列×1行、スロット0・1両方使用中
        const atlas = atlasWithSolid(2, 1, [0, 1]);
        const tile = solidTile(77, 0, 0, 255);
        const { rgba, newRows, newTileIndex } = appendTileToAtlas(atlas, 2, 1, TS, tile);
        expect(newRows).toBe(2);       // 1行増える
        expect(newTileIndex).toBe(2);  // 新行先頭 = インデックス 2
        // 新行の先頭ピクセルが tile の色になっているか
        const dstX = 0;
        const dstY = 1 * TS;
        const idx = (dstY * 2 * TS + dstX) * 4;
        expect(rgba[idx]).toBe(77);
        expect(rgba[idx + 3]).toBe(255);
        // 新行の残り (スロット3) は透明のまま
        const slot3X = 1 * TS;
        const slot3Idx = (dstY * 2 * TS + slot3X) * 4;
        expect(rgba[slot3Idx + 3]).toBe(0);
    });

    it("全スロット透明のアトラスでも先頭スロットに配置できる (1列×1行)", () => {
        const atlas = new Uint8ClampedArray(TS * TS * 4); // 全透明
        const tile = solidTile(55, 55, 55, 255);
        const { rgba, newRows, newTileIndex } = appendTileToAtlas(atlas, 1, 1, TS, tile);
        expect(newRows).toBe(1);
        expect(newTileIndex).toBe(0);
        expect(rgba[3]).toBe(255); // 先頭ピクセルの alpha
    });

    it("複数列で末尾から連続した透明スロットの先頭に書き込む", () => {
        // 3列×1行、スロット0のみ使用中 → スロット1,2が透明 → スロット1に書き込む
        const atlas = atlasWithSolid(3, 1, [0]);
        const tile = solidTile(33, 33, 33, 255);
        const { newRows, newTileIndex } = appendTileToAtlas(atlas, 3, 1, TS, tile);
        expect(newRows).toBe(1);
        expect(newTileIndex).toBe(1);
    });

    it("新行追加時に元データが保持されている", () => {
        // 2列×1行、両スロット使用中
        const atlas = atlasWithSolid(2, 1, [0, 1], [10, 20, 30]);
        const tile = solidTile(99, 0, 0, 255);
        const { rgba } = appendTileToAtlas(atlas, 2, 1, TS, tile);
        // 元の行 (スロット0) のデータが変わっていないか
        const idx = 0;
        expect(rgba[idx]).toBe(10);
        expect(rgba[idx + 1]).toBe(20);
        expect(rgba[idx + 2]).toBe(30);
        expect(rgba[idx + 3]).toBe(255);
    });
});

// ============================================================
// resizeNearestNeighbor
// ============================================================

describe("resizeNearestNeighbor", () => {
    it("2×2 → 4×4 に拡大すると各ピクセルが 2×2 に伸びる", () => {
        // 2×2 src: 左上=赤, 右上=緑, 左下=青, 右下=白
        const src = new Uint8ClampedArray([
            255, 0, 0, 255,   0, 255, 0, 255,
            0, 0, 255, 255,   255, 255, 255, 255,
        ]);
        const dst = resizeNearestNeighbor(src, 2, 2, 4, 4);
        // 左上 2×2 ブロックは赤
        expect(dst[0]).toBe(255); // R
        expect(dst[1]).toBe(0);   // G
        expect(dst[2]).toBe(0);   // B
        // 右上 2×2 ブロックの先頭 (x=2, y=0) は緑
        const idx = (0 * 4 + 2) * 4;
        expect(dst[idx]).toBe(0);
        expect(dst[idx + 1]).toBe(255);
    });

    it("4×4 → 2×2 に縮小するとピクセルが間引かれる", () => {
        const src = new Uint8ClampedArray(4 * 4 * 4);
        // 左列 (x=0,1) を赤, 右列 (x=2,3) を青にする
        for (let y = 0; y < 4; y++) {
            for (let x = 0; x < 4; x++) {
                const i = (y * 4 + x) * 4;
                if (x < 2) { src[i] = 255; src[i + 3] = 255; }
                else { src[i + 2] = 255; src[i + 3] = 255; }
            }
        }
        const dst = resizeNearestNeighbor(src, 4, 4, 2, 2);
        expect(dst.length).toBe(2 * 2 * 4);
        // x=0 は赤 (src の x=0 から)
        expect(dst[0]).toBe(255); expect(dst[2]).toBe(0);
        // x=1 は青 (src の x=2 から)
        expect(dst[4]).toBe(0); expect(dst[6]).toBe(255);
    });

    it("同一サイズで呼んだ場合は入力と同内容を返す", () => {
        const src = new Uint8ClampedArray([1, 2, 3, 255, 4, 5, 6, 255, 7, 8, 9, 255, 10, 11, 12, 255]);
        const dst = resizeNearestNeighbor(src, 2, 2, 2, 2);
        expect([...dst]).toEqual([...src]);
    });
});
