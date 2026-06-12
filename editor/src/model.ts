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

// ============================================================
// EKM v3 — 4 レイヤー + 共有タイルセットプール (仕様 §19〜§24)
// ============================================================

export const MAX_TILESETS = 4;
export const V3_LAYER_COUNT = 4;

/** v3 の 1 レイヤー内部表現 */
export interface LayerDocV3 {
    /** tilesets[] へのインデックス (0〜MAX_TILESETS-1) */
    tileset: number;
    /** width*height のフラット int 配列。-1 = 空 */
    cells: number[];
    /** true = この層のタイルをプレイヤーより上に描画 (仕様 §21.4) */
    above: boolean;
}

/** エディタ内部ドキュメント (v3) — layers は常に 4 要素にパディング済み */
export interface MapDocV3 {
    ekm: 3;
    name: string;
    author: string;
    width: number;
    height: number;
    /** 共有タイルセットプール (1〜4 要素) */
    tilesets: TilesetDoc[];
    /** 常に 4 要素 (パディング済み) */
    layers: [LayerDocV3, LayerDocV3, LayerDocV3, LayerDocV3];
    decor: DecorEntry[];
    spawn: SpawnPoint;
    /** visionRadius は検証・クランプ済み。未知サブキーも保全する */
    ambient: { visionRadius: number; [key: string]: unknown };
    /** requires は対応 capability 集合 (v3 凍結時点では空 Set のみ対応)。保全して出力する */
    requires?: string[];
}

export type AnyDoc = MapDoc | MapDocV2 | MapDocV3;

export function isV2Doc(doc: AnyDoc): doc is MapDocV2 {
    return doc.ekm === 2;
}

export function isV3Doc(doc: AnyDoc): doc is MapDocV3 {
    return doc.ekm === 3;
}

/** デフォルト (完全空) の LayerDocV3 を作る */
function makeDefaultLayer(width: number, height: number): LayerDocV3 {
    return { tileset: 0, cells: new Array(width * height).fill(-1), above: false };
}

/** 全セル空の新規 v3 マップ。tilesets[0] に渡した tileset を使う */
export function createNewDocV3(width: number, height: number, name: string, author: string, tileset: TilesetDoc): MapDocV3 {
    return {
        ekm: 3,
        name,
        author,
        width,
        height,
        tilesets: [tileset],
        layers: [
            makeDefaultLayer(width, height),
            makeDefaultLayer(width, height),
            makeDefaultLayer(width, height),
            makeDefaultLayer(width, height),
        ],
        decor: [],
        spawn: { x: Math.floor(width / 2), y: Math.floor(height / 2) },
        ambient: { visionRadius: VISION_DEFAULT },
    };
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

// ---------- v3 JSON (出力形) ----------

export interface LayerJsonV3 {
    tileset: number;
    cells: number[];
    above?: boolean; // false のとき省略しない (仕様: 読込時パディングはするが出力は明示)
}

export interface EkmapJsonV3 {
    ekm: 3;
    requires?: string[];
    name: string;
    author?: string;
    width: number;
    height: number;
    tilesets: TilesetJson[];
    layers: LayerJsonV3[];
    decor?: DecorEntry[];
    spawn: SpawnPoint;
    ambient?: { visionRadius?: number; [key: string]: unknown };
}

export type AnyEkmapJson = EkmapJson | EkmapJsonV2 | EkmapJsonV3;

/** TilesetDoc → TilesetJson (tiles 疎化ヘルパ) */
function tilesetToJson(ts: TilesetDoc): TilesetJson {
    const tiles: TileJsonEntry[] = [];
    for (let id = 0; id < ts.tiles.length; id++) {
        const t = ts.tiles[id];
        if (!isDefaultTileAttr(t)) tiles.push({ id, pass: t.pass, over: t.over, light: t.light, tag: t.tag, dir: t.dir });
    }
    const json: TilesetJson = { tileSize: ts.tileSize, columns: ts.columns, image: ts.image };
    if (tiles.length > 0) json.tiles = tiles;
    return json;
}

/**
 * 内部 v3 ドキュメント → .ekmap.json 構造。
 * 保存省略規則 (§22.1): 末尾から「cells 全 -1 かつ above=false かつ tileset=0」の層のみ省略。
 * layers[0] は常に出力。途中の層は省略不可。
 */
export function docToJsonV3(doc: MapDocV3): EkmapJsonV3 {
    // tilesets 全セット疎化
    const tilesetsJson: TilesetJson[] = doc.tilesets.map(tilesetToJson);

    // layers: 末尾デフォルト層を省略 (layers[0] は常に残す)
    const layersToWrite: LayerDocV3[] = [...doc.layers];
    while (layersToWrite.length > 1) {
        const last = layersToWrite[layersToWrite.length - 1];
        if (last.tileset === 0 && !last.above && last.cells.every((v) => v < 0)) {
            layersToWrite.pop();
        } else {
            break;
        }
    }

    const layersJson: LayerJsonV3[] = layersToWrite.map((l) => {
        const out: LayerJsonV3 = { tileset: l.tileset, cells: [...l.cells] };
        if (l.above) out.above = true;
        return out;
    });

    // 未知サブキーを保全しつつ visionRadius を上書き
    const { visionRadius, ...ambientExtra } = doc.ambient;
    const ambientOut: { visionRadius: number; [key: string]: unknown } = { ...ambientExtra, visionRadius };

    const json: EkmapJsonV3 = {
        ekm: 3,
        name: doc.name,
        author: doc.author,
        width: doc.width,
        height: doc.height,
        tilesets: tilesetsJson,
        layers: layersJson,
        spawn: { x: doc.spawn.x, y: doc.spawn.y },
        ambient: ambientOut,
    };
    if (doc.requires && doc.requires.length > 0) json.requires = [...doc.requires];
    if (doc.decor.length > 0) json.decor = doc.decor.map((d) => ({ kind: d.kind, x: d.x, y: d.y }));
    return json;
}

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

/** v1/v2/v3 共通の JSON 化 */
export function docToJsonAny(doc: AnyDoc): AnyEkmapJson {
    if (isV3Doc(doc)) return docToJsonV3(doc);
    if (isV2Doc(doc)) return docToJsonV2(doc);
    return docToJson(doc);
}

// ---------- v2 セル解決規則 (仕様 §15) ----------

export interface CellResolution {
    /** G = -1 またはグリッド外 (通行不可+遮光) */
    isVoid: boolean;
    passable: boolean;
    blocksLight: boolean;
}

/**
 * 仕様 §21 — v3 セル解決規則:
 * 1. T1 = -1 → void (通行不可+遮光)
 * 2. 実効通行: レイヤー 4→1 走査で最初の「タイルあり && pass != "v"」の pass を採用。
 *    全て空 or "v" → T1 の pass ("v" は "o" 扱い)
 * 3. 実効遮光: 全層の light の OR、または void
 * 5. グリッド外 = void
 */
export function resolveCellV3(doc: MapDocV3, x: number, y: number): CellResolution {
    if (!inBounds(doc, x, y)) return { isVoid: true, passable: false, blocksLight: true };
    const i = y * doc.width + x;

    // T1 = layers[0]
    const t1 = doc.layers[0].cells[i];
    if (t1 < 0) return { isVoid: true, passable: false, blocksLight: true };

    // 実効遮光: 全層の light OR
    let blocksLight = false;
    for (const layer of doc.layers) {
        const tid = layer.cells[i];
        if (tid < 0) continue;
        const ts = doc.tilesets[layer.tileset];
        if (ts && ts.tiles[tid]?.light) { blocksLight = true; break; }
    }

    // 実効通行: 層 4→1 走査 (index 3→0)
    let effectivePass: PassValue | null = null;
    for (let li = doc.layers.length - 1; li >= 0; li--) {
        const layer = doc.layers[li];
        const tid = layer.cells[i];
        if (tid < 0) continue;
        const ts = doc.tilesets[layer.tileset];
        const attr = ts?.tiles[tid] ?? defaultTileAttr();
        if (attr.pass !== "v") {
            effectivePass = attr.pass;
            break;
        }
    }
    if (effectivePass === null) {
        // 全て空 or "v" → T1 の pass ("v" は "o" 扱い)
        const ts1 = doc.tilesets[doc.layers[0].tileset];
        const t1attr = ts1?.tiles[t1] ?? defaultTileAttr();
        effectivePass = t1attr.pass === "v" ? "o" : t1attr.pass;
    }

    return { isVoid: false, passable: effectivePass === "o", blocksLight };
}

/**
 * セル i について「上帯に描画される層」のビットマスク (bit k = 1 ならば layer k が上帯)。
 * layer.above = true または チップ over = true のいずれかで上帯扱い (仕様 §21.4)。
 */
export function cellAboveMask(doc: MapDocV3, i: number): number {
    let mask = 0;
    for (let li = 0; li < doc.layers.length; li++) {
        const layer = doc.layers[li];
        const tid = layer.cells[i];
        if (layer.above) {
            mask |= (1 << li);
        } else if (tid >= 0) {
            const ts = doc.tilesets[layer.tileset];
            if (ts?.tiles[tid]?.over) mask |= (1 << li);
        }
    }
    return mask;
}

/**
 * v2 ドキュメントを v3 に変換する (仕様 §22.2 — 決定論的変換)。
 * tileset → tilesets[0]、ground → layers[0]、upper → layers[1]、層3/4 パディング。
 */
export function upgradeV2DocToV3(docV2: MapDocV2): MapDocV3 {
    const width = docV2.width;
    const height = docV2.height;
    return {
        ekm: 3,
        name: docV2.name,
        author: docV2.author,
        width,
        height,
        tilesets: [docV2.tileset],
        layers: [
            { tileset: 0, cells: [...docV2.ground], above: false },
            { tileset: 0, cells: [...docV2.upper], above: false },
            makeDefaultLayer(width, height),
            makeDefaultLayer(width, height),
        ],
        decor: docV2.decor.map((d) => ({ ...d })),
        spawn: { ...docV2.spawn },
        ambient: { ...docV2.ambient },
    };
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
