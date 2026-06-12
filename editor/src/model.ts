// EKM v1/v2 データモデル (docs/ekmap-spec.md 準拠)

export const CELL_FLOOR = ".";
export const CELL_WALL = "#";
export const CELL_VOID = "-";
export const CELL_CHARS = [CELL_FLOOR, CELL_WALL, CELL_VOID] as const;
export type CellChar = (typeof CELL_CHARS)[number];

export const DECOR_KINDS = ["light", "stain", "door", "vent", "ceiling"] as const;
export type DecorKind = (typeof DECOR_KINDS)[number];

export const MAX_DIM = 256;
export const MIN_DIM = 1;
export const MAX_DECOR = 1024;
export const MAX_JSON_BYTES = 512 * 1024;
export const NAME_MAX = 64;
export const AUTHOR_MAX = 32;
export const VISION_MIN = 4;
// 仕様 §7: v1 ローダーは 4〜8 にクランプ (9〜12 は v2 予約)
export const VISION_MAX = 8;
export const VISION_DEFAULT = 8;

export interface DecorEntry {
    kind: string;
    x: number;
    y: number;
}

export interface SpawnPoint {
    x: number;
    y: number;
}

/** .ekmap.json のトップレベル構造 (出力形) */
export interface EkmapJson {
    ekm: 1;
    name: string;
    author?: string;
    width: number;
    height: number;
    cells: string[];
    decor?: DecorEntry[];
    spawn: SpawnPoint;
    ambient?: { visionRadius?: number; [key: string]: unknown };
}

/** エディタ内部ドキュメント (v1)。grid は width*height のフラット配列 */
export interface MapDoc {
    ekm: 1;
    name: string;
    author: string;
    width: number;
    height: number;
    grid: string[];
    decor: DecorEntry[];
    spawn: SpawnPoint;
    /**
     * ambient.visionRadius は検証・クランプ済み。
     * _ambientExtra に未知サブキー (darkLevel/edgeBlur 等) を保全する。
     * このフィールドは永続化しない — docToJson 時に ambient に merge して出力する。
     */
    ambient: { visionRadius: number; [key: string]: unknown };
}

export function cellIndex(doc: { width: number }, x: number, y: number): number {
    return y * doc.width + x;
}

export function inBounds(doc: { width: number; height: number }, x: number, y: number): boolean {
    return x >= 0 && y >= 0 && x < doc.width && y < doc.height;
}

export function getCell(doc: MapDoc, x: number, y: number): string {
    if (!inBounds(doc, x, y)) return CELL_VOID; // グリッド外は void 扱い (仕様 §3)
    return doc.grid[cellIndex(doc, x, y)];
}

/** 全セル void の新規マップ。spawn は中央セル */
export function createNewDoc(width: number, height: number, name: string, author: string): MapDoc {
    return {
        ekm: 1,
        name,
        author,
        width,
        height,
        grid: new Array(width * height).fill(CELL_VOID),
        decor: [],
        spawn: { x: Math.floor(width / 2), y: Math.floor(height / 2) },
        ambient: { visionRadius: VISION_DEFAULT },
    };
}

/** 内部ドキュメント → .ekmap.json 構造 */
export function docToJson(doc: MapDoc): EkmapJson {
    const cells: string[] = [];
    for (let y = 0; y < doc.height; y++) {
        cells.push(doc.grid.slice(y * doc.width, (y + 1) * doc.width).join(""));
    }
    // 未知サブキーを保全しつつ visionRadius を上書き (仕様: 黙って許容 §9)
    const { visionRadius, ...ambientExtra } = doc.ambient;
    const ambientOut: { visionRadius: number; [key: string]: unknown } = { ...ambientExtra, visionRadius };
    const json: EkmapJson = {
        ekm: 1,
        name: doc.name,
        author: doc.author,
        width: doc.width,
        height: doc.height,
        cells,
        spawn: { x: doc.spawn.x, y: doc.spawn.y },
        ambient: ambientOut,
    };
    if (doc.decor.length > 0) json.decor = doc.decor.map((d) => ({ kind: d.kind, x: d.x, y: d.y }));
    return json;
}

/**
 * 壁ビジュアル導出 (仕様 §5.1):
 * `#` で上下左右 4 近傍に `#` が 1 つも無い → 孤立柱。
 * グリッド外・void・床は近傍として数えない。
 */
export function isPillar(cells: string[], width: number, height: number, x: number, y: number): boolean {
    const at = (cx: number, cy: number): string => {
        if (cx < 0 || cy < 0 || cx >= width || cy >= height) return CELL_VOID;
        return cells[cy][cx];
    };
    if (at(x, y) !== CELL_WALL) return false;
    return at(x - 1, y) !== CELL_WALL && at(x + 1, y) !== CELL_WALL && at(x, y - 1) !== CELL_WALL && at(x, y + 1) !== CELL_WALL;
}

/** decor/spawn の float 座標が指すセル (値はセル中心基準 → 最近傍セル) */
export function coordToCell(v: number): number {
    return Math.round(v);
}

// ============================================================
// EKM v2 — カスタムタイルセット + 2 レイヤー (仕様 §13〜§16)
// ============================================================

export const MAX_JSON_BYTES_V2 = 4 * 1024 * 1024;
export const TILESIZE_MIN = 8;
export const TILESIZE_MAX = 128;
export const TILESIZE_DEFAULT = 32;
export const MAX_TILECOUNT = 4096;
export const MAX_TILESET_IMAGE_BYTES = 1024 * 1024;
export const TAG_MAX = 99;
export const DIR_DEFAULT = 15;

export const PASS_VALUES = ["o", "x", "v"] as const;
/** "o"=通行可 / "x"=不可 / "v"=下層に従う (upper でのみ意味、ground では "o" 扱い) */
export type PassValue = (typeof PASS_VALUES)[number];

/** per-tile 属性 (仕様 §14.1)。dir は予約のみ (パース・保存するが実装しない) */
export interface TileAttr {
    pass: PassValue;
    over: boolean;
    light: boolean;
    tag: number;
    dir: number;
}

export function defaultTileAttr(): TileAttr {
    return { pass: "o", over: false, light: false, tag: 0, dir: DIR_DEFAULT };
}

export function isDefaultTileAttr(t: TileAttr): boolean {
    return t.pass === "o" && !t.over && !t.light && t.tag === 0 && t.dir === DIR_DEFAULT;
}

/** エディタ内部のタイルセット。tiles は dense (長さ = columns*rows) */
export interface TilesetDoc {
    tileSize: number;
    columns: number;
    rows: number;
    /** data:image/png;base64, の data URI */
    image: string;
    tiles: TileAttr[];
}

export function tileCount(ts: { columns: number; rows: number }): number {
    return ts.columns * ts.rows;
}

/** エディタ内部ドキュメント (v2)。ground/upper は width*height のフラット int 配列、-1 = 空 */
export interface MapDocV2 {
    ekm: 2;
    name: string;
    author: string;
    width: number;
    height: number;
    tileset: TilesetDoc;
    ground: number[];
    upper: number[];
    decor: DecorEntry[];
    spawn: SpawnPoint;
    /** visionRadius は検証・クランプ済み。未知サブキー (darkLevel/edgeBlur 等) も保全する */
    ambient: { visionRadius: number; [key: string]: unknown };
}

export type AnyDoc = MapDoc | MapDocV2;

export function isV2Doc(doc: AnyDoc): doc is MapDocV2 {
    return doc.ekm === 2;
}

/** 全セル空 (-1) の新規 v2 マップ。spawn は中央セル */
export function createNewDocV2(width: number, height: number, name: string, author: string, tileset: TilesetDoc): MapDocV2 {
    return {
        ekm: 2,
        name,
        author,
        width,
        height,
        tileset,
        ground: new Array(width * height).fill(-1),
        upper: new Array(width * height).fill(-1),
        decor: [],
        spawn: { x: Math.floor(width / 2), y: Math.floor(height / 2) },
        ambient: { visionRadius: VISION_DEFAULT },
    };
}

// ---------- v2 JSON (出力形) ----------

export interface TileJsonEntry {
    id: number;
    pass: PassValue;
    over: boolean;
    light: boolean;
    tag: number;
    dir: number;
}

export interface TilesetJson {
    tileSize: number;
    columns: number;
    image: string;
    tiles?: TileJsonEntry[];
}

export interface EkmapJsonV2 {
    ekm: 2;
    name: string;
    author?: string;
    width: number;
    height: number;
    tileset: TilesetJson;
    layers: { ground: number[]; upper?: number[] };
    decor?: DecorEntry[];
    spawn: SpawnPoint;
    ambient?: { visionRadius?: number; [key: string]: unknown };
}

export type AnyEkmapJson = EkmapJson | EkmapJsonV2;

/** 内部 v2 ドキュメント → .ekmap.json 構造。tiles は疎 (デフォルト値のチップは省略) */
export function docToJsonV2(doc: MapDocV2): EkmapJsonV2 {
    const tiles: TileJsonEntry[] = [];
    for (let id = 0; id < doc.tileset.tiles.length; id++) {
        const t = doc.tileset.tiles[id];
        if (!isDefaultTileAttr(t)) tiles.push({ id, pass: t.pass, over: t.over, light: t.light, tag: t.tag, dir: t.dir });
    }
    const tileset: TilesetJson = {
        tileSize: doc.tileset.tileSize,
        columns: doc.tileset.columns,
        image: doc.tileset.image,
    };
    if (tiles.length > 0) tileset.tiles = tiles;
    const layers: EkmapJsonV2["layers"] = { ground: [...doc.ground] };
    if (doc.upper.some((v) => v >= 0)) layers.upper = [...doc.upper];
    // 未知サブキーを保全しつつ visionRadius を上書き
    const { visionRadius, ...ambientExtra } = doc.ambient;
    const ambientOut: { visionRadius: number; [key: string]: unknown } = { ...ambientExtra, visionRadius };
    const json: EkmapJsonV2 = {
        ekm: 2,
        name: doc.name,
        author: doc.author,
        width: doc.width,
        height: doc.height,
        tileset,
        layers,
        spawn: { x: doc.spawn.x, y: doc.spawn.y },
        ambient: ambientOut,
    };
    if (doc.decor.length > 0) json.decor = doc.decor.map((d) => ({ kind: d.kind, x: d.x, y: d.y }));
    return json;
}

/** v1/v2 共通の JSON 化 */
export function docToJsonAny(doc: AnyDoc): AnyEkmapJson {
    return isV2Doc(doc) ? docToJsonV2(doc) : docToJson(doc);
}

// ---------- v2 セル解決規則 (仕様 §15) ----------

export interface CellResolution {
    /** G = -1 またはグリッド外 (通行不可+遮光) */
    isVoid: boolean;
    passable: boolean;
    blocksLight: boolean;
}

/**
 * 仕様 §15:
 * 1. G = -1 → void (通行不可+遮光)
 * 2. 実効通行: U があり U.pass != "v" → U.pass、それ以外 → G.pass ("v" は ground では "o" 扱い)
 * 3. 実効遮光: G.light || U.light || void
 * 5. グリッド外 = void
 */
export function resolveCellV2(doc: MapDocV2, x: number, y: number): CellResolution {
    if (!inBounds(doc, x, y)) return { isVoid: true, passable: false, blocksLight: true };
    const i = y * doc.width + x;
    const g = doc.ground[i];
    if (g < 0) return { isVoid: true, passable: false, blocksLight: true };
    const ga = doc.tileset.tiles[g] ?? defaultTileAttr();
    const u = doc.upper[i];
    const ua = u >= 0 ? (doc.tileset.tiles[u] ?? defaultTileAttr()) : null;
    let pass: PassValue = ua !== null && ua.pass !== "v" ? ua.pass : ga.pass;
    if (pass === "v") pass = "o"; // ground の "v" は "o" 扱い (仕様 §14.1)
    const blocksLight = ga.light || (ua !== null && ua.light);
    return { isVoid: false, passable: pass === "o", blocksLight };
}
