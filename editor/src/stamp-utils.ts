/**
 * スタンプ操作・描画ユーティリティ (純関数・DOM非依存)
 * vitest node 環境で動作すること。
 */

export interface StampBlock {
    w: number;
    h: number;
    ids: number[];
}

/**
 * ブロックスタンプを左右反転 (H flip) する。
 * 各行の列を逆順にする。w/h は不変。
 */
export function flipStampH(block: StampBlock): StampBlock {
    const { w, h, ids } = block;
    const result: number[] = new Array(w * h);
    for (let r = 0; r < h; r++) {
        for (let c = 0; c < w; c++) {
            result[r * w + c] = ids[r * w + (w - 1 - c)];
        }
    }
    return { w, h, ids: result };
}

/**
 * ブロックスタンプを上下反転 (V flip) する。
 * 行の順序を逆にする。w/h は不変。
 */
export function flipStampV(block: StampBlock): StampBlock {
    const { w, h, ids } = block;
    const result: number[] = new Array(w * h);
    for (let r = 0; r < h; r++) {
        for (let c = 0; c < w; c++) {
            result[r * w + c] = ids[(h - 1 - r) * w + c];
        }
    }
    return { w, h, ids: result };
}

/**
 * ブレゼンハムの直線アルゴリズムでセル座標列を列挙する。
 * 始点・終点を含む整数セル座標の配列を返す。
 */
export function bresenhamLine(
    x0: number, y0: number,
    x1: number, y1: number,
): Array<{ x: number; y: number }> {
    const cells: Array<{ x: number; y: number }> = [];
    let dx = Math.abs(x1 - x0);
    let dy = Math.abs(y1 - y0);
    let sx = x0 < x1 ? 1 : -1;
    let sy = y0 < y1 ? 1 : -1;
    let err = dx - dy;
    let cx = x0;
    let cy = y0;
    // 最大イテレーション安全ガード (マップ最大 256×256)
    const maxIter = dx + dy + 2;
    for (let i = 0; i <= maxIter; i++) {
        cells.push({ x: cx, y: cy });
        if (cx === x1 && cy === y1) break;
        const e2 = 2 * err;
        if (e2 > -dy) { err -= dy; cx += sx; }
        if (e2 < dx) { err += dx; cy += sy; }
    }
    return cells;
}

/**
 * 矩形 (x0..x1, y0..y1) の外周セル座標を列挙する。
 * 塗り矩形ツールの「枠だけ」モード用。
 */
export function rectOutlineCells(
    x0: number, y0: number,
    x1: number, y1: number,
): Array<{ x: number; y: number }> {
    const cells: Array<{ x: number; y: number }> = [];
    for (let y = y0; y <= y1; y++) {
        for (let x = x0; x <= x1; x++) {
            if (y === y0 || y === y1 || x === x0 || x === x1) {
                cells.push({ x, y });
            }
        }
    }
    return cells;
}
