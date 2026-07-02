/**
 * オートタイル (スマートブラシ) 純関数層 — DOM 非依存 (vitest node 環境で動く)
 *
 * 設計の正典: docs/ekm-studio/design_autotile.md
 *
 * 核心思想 (§0): オートタイルは「ランタイム機能」ではなく「エディタのブラシ」。
 *   塗った時点で接続を解決し、最終 tileId を layers[].cells[] に焼き込む。
 *   → C# ランタイム無変更・RPC ゼロ・現行ローダーで requires 無しに開ける (Tier-1 安全)。
 *
 * MVP の方式 = edge-4 (上下左右 4 近傍・16 枚)。WYSIWYG (ハーフセルオフセット無し)。
 *   bitmask: code = (N?1) | (E?2) | (S?4) | (W?8)。lut[code] = 焼く tileId。
 *
 * メンバーシップはサイドカーを持たず「セルの tileId が LUT 出力集合に含まれるか」で導出する (§3)。
 *   → Undo/再読込で破損しない (history.ts 無改造)。制約: 2 つの autotile が同じ出力 tileId を共有不可。
 */

/** 対応スキーム (MVP は edge4 のみ) */
export type AutotileScheme = "edge4";

/** edge-4 近傍ビット (design_autotile.md §6.2 と一致) */
export const EDGE4_N = 1;
export const EDGE4_E = 2;
export const EDGE4_S = 4;
export const EDGE4_W = 8;

/** edge-4 の LUT スロット数 */
export const EDGE4_SLOTS = 16;

/**
 * オートタイル定義 (MVP ではエディタ autosave のみに保存・.ekmap には出さない)。
 * lut[code] = そのビット構成で焼く tileId。-1 = 未割当 (fallback を使う)。
 */
export interface AutotileDef {
    /** ブラシパレットの表示名 */
    name: string;
    scheme: AutotileScheme;
    /** この autotile が指す tilesets[] のインデックス (どのチップセットの tileId か) */
    tileset: number;
    /** 壁として扱い影を落とすか (LUT 出力タイルの light に焼かれる、§3 H2) */
    blocksLight: boolean;
    /** 前後バンドを全 LUT エントリで揃える (§3 H3)。over 相当 */
    band: "ground" | "above";
    /** mask(0〜15) → tileId。length は EDGE4_SLOTS。-1 = 未割当 */
    lut: number[];
    /** 未割当スロットに使う tileId。-1 なら未設定 (空マップ生成) */
    fallback: number;
}

/** 空の edge-4 定義を作る */
export function createEdge4Def(name: string, tileset: number): AutotileDef {
    return {
        name,
        scheme: "edge4",
        tileset,
        blocksLight: true,
        band: "ground",
        lut: new Array<number>(EDGE4_SLOTS).fill(-1),
        fallback: -1,
    };
}

/** edge-4 の 4 近傍メンバーシップからビットコード (0〜15) を作る */
export function edge4Code(n: boolean, e: boolean, s: boolean, w: boolean): number {
    return (n ? EDGE4_N : 0) | (e ? EDGE4_E : 0) | (s ? EDGE4_S : 0) | (w ? EDGE4_W : 0);
}

/**
 * code から焼く tileId を引く。未割当スロットは fallback。
 * fallback も未設定 (-1) ならそのまま -1 (= 空セル) を返す。
 */
export function resolveEdge4(def: AutotileDef, code: number): number {
    const id = def.lut[code] ?? -1;
    return id >= 0 ? id : def.fallback;
}

/**
 * この autotile の「メンバーシップ集合」= LUT 出力 tileId 全部 (+ fallback)。
 * セルの tileId がこの集合に入っていれば、そのセルはこの autotile の一部。
 * サイドカーを持たずセルから導出する (§3) ための逆引き表。
 */
export function membershipSet(def: AutotileDef): Set<number> {
    const set = new Set<number>();
    for (const id of def.lut) if (id >= 0) set.add(id);
    if (def.fallback >= 0) set.add(def.fallback);
    return set;
}

/** lut が 1 つも割り当てられていない (fallback も無い) か */
export function isLutEmpty(def: AutotileDef): boolean {
    return def.fallback < 0 && def.lut.every((id) => id < 0);
}

/** light/over だけを持つタイル属性の構造的最小型 (model.ts に依存しないための decouple) */
export interface TileLightOver {
    light: boolean;
    over: boolean;
}

/** RGB 色 (0-255) */
export interface Rgb {
    r: number;
    g: number;
    b: number;
}

/**
 * edge-4 の 16 通りの「つなぎ目壁」タイルを RGBA で自動生成する (色を選ぶだけジェネレータの心臓部)。
 * 4×4 レイアウト・**タイル index == 接続コード** なので、生成後に lut[code]=code で一発割当できる。
 * 各タイル: 中央ブロック + つながる方向 (N/E/S/W) へバーを伸ばす + 上端に暗バンド (立体感)。
 * 背景は透過。マップと同じ tileSize で作るので**サイズ不一致が起きない**。
 *
 * @param tileSize タイルサイズ (px)。マップの現行セットに合わせて渡すこと。
 * @param color    壁の色
 * @returns { rgba, cols: 4, rows: 4 } (rgba は 4*tileSize × 4*tileSize の RGBA)
 */
export function generateEdge4WallSheet(tileSize: number, color: Rgb): { rgba: Uint8ClampedArray; cols: number; rows: number } {
    const T = Math.max(4, Math.floor(tileSize));
    const cols = 4;
    const rows = 4;
    const W = cols * T;
    const H = rows * T;
    const rgba = new Uint8ClampedArray(W * H * 4); // 透過 (0 埋め)

    const wall = { r: clamp255(color.r), g: clamp255(color.g), b: clamp255(color.b) };
    const dark = { r: Math.round(wall.r * 0.62), g: Math.round(wall.g * 0.62), b: Math.round(wall.b * 0.62) };

    const q = Math.max(1, Math.floor(T / 4));   // バー/中央の太さ基準 (T=32 → 8)
    const h = Math.floor(T / 2);                 // 半セル (T=32 → 16)
    const band = Math.max(1, Math.floor(T / 8)); // 上暗バンド (T=32 → 4)

    for (let code = 0; code < EDGE4_SLOTS; code++) {
        const ox = (code % cols) * T;
        const oy = Math.floor(code / cols) * T;
        const n = (code & EDGE4_N) !== 0;
        const e = (code & EDGE4_E) !== 0;
        const s = (code & EDGE4_S) !== 0;
        const w = (code & EDGE4_W) !== 0;

        // 中央ブロック (常時)
        fillRect(rgba, W, ox + q, oy + q, h, h, wall);
        // つながる方向へバー
        if (n) fillRect(rgba, W, ox + q, oy + 0, h, h, wall);
        if (s) fillRect(rgba, W, ox + q, oy + h, h, h, wall);
        if (w) fillRect(rgba, W, ox + 0, oy + q, h, h, wall);
        if (e) fillRect(rgba, W, ox + h, oy + q, h, h, wall);
        // 上端の暗バンド (上につながる時は天辺、そうでなければ中央上端)
        if (n) fillRect(rgba, W, ox + q, oy + 0, h, band, dark);
        else fillRect(rgba, W, ox + q, oy + q, h, band, dark);
    }

    return { rgba, cols, rows };
}

function clamp255(v: number): number {
    return Math.max(0, Math.min(255, Math.round(v)));
}

/** rgba(W幅) の矩形 (x,y,w,h) を不透明色で塗る */
function fillRect(rgba: Uint8ClampedArray, W: number, x: number, y: number, w: number, h: number, c: Rgb): void {
    for (let py = y; py < y + h; py++) {
        for (let px = x; px < x + w; px++) {
            const i = (py * W + px) * 4;
            rgba[i] = c.r;
            rgba[i + 1] = c.g;
            rgba[i + 2] = c.b;
            rgba[i + 3] = 255;
        }
    }
}

/**
 * def の LUT 出力 tileId 全件 (+ fallback) の tiles[] に blocksLight/band を焼き込む (§3 H2/H3)。
 * 焼かないと: 拾い物シートに light メタが無く影 occluder に穴が空く / over が混在して z バンドがちらつく。
 * tiles はタイル id で添字する dense 配列 (穴は undefined 許容)。この関数は tiles を破壊的に変更する。
 */
export function bakeAutotileTileAttrs(tiles: (TileLightOver | undefined)[], def: AutotileDef): void {
    const over = def.band === "above";
    for (const id of membershipSet(def)) {
        if (id < 0 || id >= tiles.length) continue;
        const t = tiles[id];
        if (!t) continue;
        t.light = def.blocksLight;
        t.over = over;
    }
}

/** 割り当て済みスロット数 (ダウングレード判定・完成度表示用) */
export function filledSlotCount(def: AutotileDef): number {
    return def.lut.reduce((n, id) => n + (id >= 0 ? 1 : 0), 0);
}

// ─── グリッド再解決 ────────────────────────────────────────────────────────────

/** 1 レイヤー分のセル配列ビュー (再解決対象) */
export interface GridView {
    width: number;
    height: number;
    /** width*height のフラット配列。-1 = 空 */
    cells: number[];
}

/** 1 セルの書き換え結果 */
export interface CellEdit {
    x: number;
    y: number;
    /** 焼く tileId。-1 = 空 (消去) */
    id: number;
}

/** (x,y) が範囲内かつ tileId が autotile のメンバーか。OOB は非メンバー (§6.2 OOB rule) */
function isMemberAt(set: Set<number>, view: GridView, x: number, y: number, override: Map<number, number>): boolean {
    if (x < 0 || y < 0 || x >= view.width || y >= view.height) return false;
    const i = y * view.width + x;
    const tid = override.has(i) ? (override.get(i) as number) : view.cells[i];
    return tid >= 0 && set.has(tid);
}

/** override を加味した上で (x,y) のメンバーシップから接続コードを計算し焼く tileId を返す */
function resolveMemberCell(def: AutotileDef, set: Set<number>, view: GridView, x: number, y: number, override: Map<number, number>): number {
    const n = isMemberAt(set, view, x, y - 1, override);
    const e = isMemberAt(set, view, x + 1, y, override);
    const s = isMemberAt(set, view, x, y + 1, override);
    const w = isMemberAt(set, view, x - 1, y, override);
    return resolveEdge4(def, edge4Code(n, e, s, w));
}

/**
 * スマートブラシで (x,y) を塗る/消すときに書き換えるべき全セルを計算する純関数。
 *
 * - paint: (x,y) を新規メンバーとして接続解決し、その後 4 近傍のメンバーセルも再解決する。
 * - erase: (x,y) を空 (-1) にし、4 近傍のメンバーセルを再解決する。
 *
 * 返り値は適用すべき CellEdit の配列 (self + 影響を受けた近傍のみ)。
 * Undo の Patch にはこの全セルを記録すること (近傍が stale 解決にならないように、§5.1)。
 *
 * @param view  対象レイヤーの現在状態 (この関数は view を変更しない)
 * @param mode  "paint" = 壁を置く / "erase" = 消す
 */
export function computeAutotileEdits(def: AutotileDef, view: GridView, x: number, y: number, mode: "paint" | "erase"): CellEdit[] {
    if (x < 0 || y < 0 || x >= view.width || y >= view.height) return [];
    const set = membershipSet(def);
    const ti = y * view.width + x;

    // override: これから書き込む暫定状態 (近傍の再解決はこの暫定状態を見る)
    const override = new Map<number, number>();
    if (mode === "paint") {
        // まず (x,y) を「メンバーである」と確定させるため、近傍を見ない code=0 の暫定 id を置く。
        // (fallback も lut[0] も無い場合は塗れない → 空配列)
        const seed = resolveEdge4(def, edge4Code(false, false, false, false));
        if (seed < 0) return [];
        override.set(ti, seed);
    } else {
        override.set(ti, -1);
    }

    const edits: CellEdit[] = [];
    // self (paint 時のみ。erase は self を -1 にする)
    if (mode === "paint") {
        const id = resolveMemberCell(def, set, view, x, y, override);
        override.set(ti, id);
        edits.push({ x, y, id });
    } else {
        edits.push({ x, y, id: -1 });
    }

    // 4 近傍: メンバーセルだけ再解決
    const neighbors: Array<[number, number]> = [[x, y - 1], [x + 1, y], [x, y + 1], [x - 1, y]];
    for (const [nx, ny] of neighbors) {
        if (!isMemberAt(set, view, nx, ny, override)) continue;
        const id = resolveMemberCell(def, set, view, nx, ny, override);
        const ni = ny * view.width + nx;
        override.set(ni, id);
        edits.push({ x: nx, y: ny, id });
    }

    return edits;
}
