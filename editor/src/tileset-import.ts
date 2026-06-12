/**
 * タイルセットインポート純関数層 (DOM 非依存 — vitest node 環境で動く)
 *
 * 担当:
 *   - タイルサイズ/余白/間隔の整合候補列挙 (enumerateCandidates)
 *   - 境界コントラスト比スコアリング (scoreCandidates)
 *   - margin/spacing なし PNG の全透明タイル検出 (countTransparentTiles)
 *   - margin/spacing 付き PNG から隙間なしアトラスへのリベイク (rebakeAtlas)
 *
 * Canvas 操作 (ピクセル取得・再エンコード) は main.ts 側の DOM 経路が担う。
 * ここは RGBA Uint8Array を受け取って結果を返す純計算のみ。
 */

/** スライス設定 */
export interface SliceParams {
    tileSize: number;
    margin: number;
    spacing: number;
    cols: number;
    rows: number;
}

/** 候補 (整合検証済み) */
export interface SliceCandidate {
    params: SliceParams;
    /** 境界コントラスト比スコア (0〜∞、高いほどグリッドが自然) */
    score: number;
    /** 信頼度ラベル */
    confidence: "高" | "中" | "低";
}

/** タイルサイズ候補 */
const TILE_SIZES = [8, 16, 24, 32, 40, 48, 64, 80, 96, 128] as const;

/**
 * 画像寸法 W×H から整合するすべての SliceParams を列挙する。
 * W = 2*margin + cols*tileSize + (cols-1)*spacing を満たす整数の組。
 * max tilecount = 4096 を超えるものは除外。
 */
export function enumerateCandidates(imageWidth: number, imageHeight: number): SliceParams[] {
    const results: SliceParams[] = [];
    for (const tileSize of TILE_SIZES) {
        for (let margin = 0; margin <= 4; margin++) {
            for (let spacing = 0; spacing <= 4; spacing++) {
                // W = 2m + cols*s + (cols-1)*g  →  cols = (W - 2m + g) / (s + g)
                const wNom = imageWidth - 2 * margin + spacing;
                const wDen = tileSize + spacing;
                if (wNom <= 0 || wDen <= 0 || wNom % wDen !== 0) continue;
                const cols = wNom / wDen;
                if (cols < 1) continue;

                const hNom = imageHeight - 2 * margin + spacing;
                const hDen = tileSize + spacing;
                if (hNom <= 0 || hDen <= 0 || hNom % hDen !== 0) continue;
                const rows = hNom / hDen;
                if (rows < 1) continue;

                if (cols * rows > 4096) continue;

                results.push({ tileSize, margin, spacing, cols, rows });
            }
        }
    }
    return results;
}

/**
 * RGBA ピクセル配列を使って各候補を境界コントラスト比スコアでランク付けする。
 *
 * score = (グリッド境界線上の隣接ピクセル差分平均) / (全体の隣接差分平均 + 1)
 *
 * @param rgba Uint8ClampedArray (RGBA, length = width*height*4)
 * @param imageWidth 画像幅 (px)
 * @param imageHeight 画像高さ (px)
 * @param candidates enumerateCandidates の結果
 * @returns candidates を score 降順で返す。score が NaN の候補は末尾。
 */
export function scoreCandidates(
    rgba: Uint8Array | Uint8ClampedArray,
    imageWidth: number,
    imageHeight: number,
    candidates: SliceParams[],
): SliceCandidate[] {
    if (candidates.length === 0) return [];

    // 全体の水平隣接差分平均 (サンプリング: 縦 1/4 おきの行のみ)
    let globalSum = 0;
    let globalCount = 0;
    const step = Math.max(1, Math.floor(imageHeight / 64));
    for (let y = 0; y < imageHeight; y += step) {
        for (let x = 0; x < imageWidth - 1; x++) {
            const i = (y * imageWidth + x) * 4;
            const dr = Math.abs(rgba[i] - rgba[i + 4]);
            const dg = Math.abs(rgba[i + 1] - rgba[i + 5]);
            const db = Math.abs(rgba[i + 2] - rgba[i + 6]);
            globalSum += (dr + dg + db) / 3;
            globalCount++;
        }
    }
    // 垂直隣接も追加
    for (let y = 0; y < imageHeight - 1; y += step) {
        for (let x = 0; x < imageWidth; x++) {
            const i = (y * imageWidth + x) * 4;
            const j = i + imageWidth * 4;
            const dr = Math.abs(rgba[i] - rgba[j]);
            const dg = Math.abs(rgba[i + 1] - rgba[j + 1]);
            const db = Math.abs(rgba[i + 2] - rgba[j + 2]);
            globalSum += (dr + dg + db) / 3;
            globalCount++;
        }
    }
    const globalAvg = globalCount > 0 ? globalSum / globalCount : 0;

    const scored: SliceCandidate[] = candidates.map((params) => {
        const score = computeBoundaryScore(rgba, imageWidth, imageHeight, params, globalAvg);
        return { params, score, confidence: "低" };
    });

    // score 降順ソート
    scored.sort((a, b) => (isNaN(b.score) ? -1 : isNaN(a.score) ? 1 : b.score - a.score));

    // 信頼度ラベル付与
    if (scored.length > 0) {
        const best = scored[0].score;
        for (const c of scored) {
            if (c.score >= best * 0.9) c.confidence = "高";
            else if (c.score >= best * 0.7) c.confidence = "中";
            else c.confidence = "低";
        }
        // 全体スコアが低い (グラデ等で境界が立たない) 場合は全て "低"
        if (best < 1.05) {
            for (const c of scored) c.confidence = "低";
        }
    }

    return scored;
}

/**
 * 単一 params の境界コントラストスコアを計算する。
 * 境界線列/行 上の差分平均 / globalAvg。グリッド境界が色不連続ならスコアが上がる。
 */
function computeBoundaryScore(
    rgba: Uint8Array | Uint8ClampedArray,
    imageWidth: number,
    imageHeight: number,
    p: SliceParams,
    globalAvg: number,
): number {
    const { tileSize, margin, spacing, cols, rows } = p;

    // 垂直境界線の x 座標列 (タイル境界の隣接ペア)
    const vBoundaries: Array<[number, number]> = [];
    for (let c = 0; c < cols - 1; c++) {
        // タイル c の右端 (最後のピクセル) と タイル c+1 の左端
        const xRight = margin + c * (tileSize + spacing) + tileSize - 1;
        const xLeft = margin + c * (tileSize + spacing) + tileSize + spacing; // spacing がある場合は spacing 分飛ぶ
        if (spacing === 0) {
            // 境界そのもの: x=xRight と x=xRight+1
            vBoundaries.push([xRight, xRight + 1]);
        } else {
            // spacing の両端
            vBoundaries.push([xRight, xRight + 1]);
            vBoundaries.push([xLeft - 1, xLeft]);
        }
    }

    const hBoundaries: Array<[number, number]> = [];
    for (let r = 0; r < rows - 1; r++) {
        const yBottom = margin + r * (tileSize + spacing) + tileSize - 1;
        const yTop = margin + r * (tileSize + spacing) + tileSize + spacing;
        if (spacing === 0) {
            hBoundaries.push([yBottom, yBottom + 1]);
        } else {
            hBoundaries.push([yBottom, yBottom + 1]);
            hBoundaries.push([yTop - 1, yTop]);
        }
    }

    if (vBoundaries.length === 0 && hBoundaries.length === 0) {
        // 1x1 グリッドは境界が無いので評価不能
        return cols === 1 && rows === 1 ? 0.5 : 0;
    }

    // サンプリング: 高さ / 列の最大 64 点
    const yStep = Math.max(1, Math.floor(imageHeight / 64));
    const xStep = Math.max(1, Math.floor(imageWidth / 64));

    let sum = 0;
    let count = 0;

    for (const [x0, x1] of vBoundaries) {
        if (x0 < 0 || x1 >= imageWidth) continue;
        for (let y = 0; y < imageHeight; y += yStep) {
            const i0 = (y * imageWidth + x0) * 4;
            const i1 = (y * imageWidth + x1) * 4;
            const dr = Math.abs(rgba[i0] - rgba[i1]);
            const dg = Math.abs(rgba[i0 + 1] - rgba[i1 + 1]);
            const db = Math.abs(rgba[i0 + 2] - rgba[i1 + 2]);
            sum += (dr + dg + db) / 3;
            count++;
        }
    }

    for (const [y0, y1] of hBoundaries) {
        if (y0 < 0 || y1 >= imageHeight) continue;
        for (let x = 0; x < imageWidth; x += xStep) {
            const i0 = (y0 * imageWidth + x) * 4;
            const i1 = (y1 * imageWidth + x) * 4;
            const dr = Math.abs(rgba[i0] - rgba[i1]);
            const dg = Math.abs(rgba[i0 + 1] - rgba[i1 + 1]);
            const db = Math.abs(rgba[i0 + 2] - rgba[i1 + 2]);
            sum += (dr + dg + db) / 3;
            count++;
        }
    }

    if (count === 0) return 0;
    const boundaryAvg = sum / count;
    return boundaryAvg / (globalAvg + 1);
}

/**
 * margin=0 かつ spacing=0 の隙間なし PNG で全透明タイルを検出する。
 * RGBA 配列でアルファチャンネルが全ピクセル 0 のタイル index のセットを返す。
 *
 * @param rgba Uint8ClampedArray (RGBA)
 * @param cols タイル列数
 * @param rows タイル行数
 * @param tileSize タイルサイズ(px)
 */
export function detectTransparentTiles(
    rgba: Uint8Array | Uint8ClampedArray,
    cols: number,
    rows: number,
    tileSize: number,
): Set<number> {
    const imageWidth = cols * tileSize;
    const transparent = new Set<number>();

    for (let r = 0; r < rows; r++) {
        for (let c = 0; c < cols; c++) {
            let allTransparent = true;
            outer: for (let ty = 0; ty < tileSize; ty++) {
                for (let tx = 0; tx < tileSize; tx++) {
                    const px = (r * tileSize + ty) * imageWidth + (c * tileSize + tx);
                    const alpha = rgba[px * 4 + 3];
                    if (alpha > 0) {
                        allTransparent = false;
                        break outer;
                    }
                }
            }
            if (allTransparent) {
                transparent.add(r * cols + c);
            }
        }
    }

    return transparent;
}

/**
 * margin/spacing 付き RGBA から隙間なし (margin=0, spacing=0) アトラスの RGBA を生成する。
 *
 * m=0 かつ spacing=0 の場合は元のバッファをそのまま返す (コピーしない)。
 * それ以外の場合のみタイルを詰め直したバッファを返す。
 *
 * @returns { rgba: Uint8ClampedArray, width: number, height: number, reencoded: boolean }
 *   reencoded が false のときは入力バッファの参照をそのまま返しているので変更しないこと。
 */
export function rebakeAtlas(
    rgba: Uint8Array | Uint8ClampedArray,
    params: SliceParams,
): { rgba: Uint8ClampedArray; width: number; height: number; reencoded: boolean } {
    const { tileSize, margin, spacing, cols, rows } = params;

    // m=0 かつ spacing=0 → パススルー (spec §罠16 の「バイト無変更」を実現)
    if (margin === 0 && spacing === 0) {
        return {
            rgba: rgba as Uint8ClampedArray,
            width: cols * tileSize,
            height: rows * tileSize,
            reencoded: false,
        };
    }

    const srcWidth = 2 * margin + cols * tileSize + (cols - 1) * spacing;
    const dstWidth = cols * tileSize;
    const dstHeight = rows * tileSize;
    const dst = new Uint8ClampedArray(dstWidth * dstHeight * 4);

    for (let r = 0; r < rows; r++) {
        for (let c = 0; c < cols; c++) {
            // ソースタイルの左上ピクセル座標
            const srcX = margin + c * (tileSize + spacing);
            const srcY = margin + r * (tileSize + spacing);
            // 宛先タイルの左上
            const dstX = c * tileSize;
            const dstY = r * tileSize;

            for (let ty = 0; ty < tileSize; ty++) {
                for (let tx = 0; tx < tileSize; tx++) {
                    const srcIdx = ((srcY + ty) * srcWidth + (srcX + tx)) * 4;
                    const dstIdx = ((dstY + ty) * dstWidth + (dstX + tx)) * 4;
                    dst[dstIdx] = rgba[srcIdx];
                    dst[dstIdx + 1] = rgba[srcIdx + 1];
                    dst[dstIdx + 2] = rgba[srcIdx + 2];
                    dst[dstIdx + 3] = rgba[srcIdx + 3];
                }
            }
        }
    }

    return { rgba: dst, width: dstWidth, height: dstHeight, reencoded: true };
}
