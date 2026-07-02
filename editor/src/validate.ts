// 検証規則 (仕様 §9 = v1 / §16 = v2) — ローダー/エディタ共通契約の TS 実装
//
// v1 拒否: ekm != 1 / 寸法範囲外 / cells 寸法不一致 / 不正文字 /
//          spawn 欠落・床以外・範囲外 / name 制約違反 / JSON 512 KB 超
// v2 拒否 (§16): tileset 欠落・制約違反 (寸法不一致/PNG でない/1MB 超/tilecount>4096) /
//          layers.ground 欠落・長さ不一致・値範囲外 / cells キーの存在 /
//          spawn が実効通行不可セル / JSON 4 MB 超
// 警告スキップ: 未知 decor kind / decor 範囲外
// 黙って許容: 未知トップレベルキー / ambient 値クランプ / tiles[] の未知キー / dir・tag の存在

import {
    AUTHOR_MAX,
    CELL_FLOOR,
    DECOR_KINDS,
    type DecorEntry,
    MAX_DECOR,
    MAX_DIM,
    MAX_JSON_BYTES,
    MAX_JSON_BYTES_V2,
    MAX_TILECOUNT,
    MAX_TILESET_IMAGE_BYTES,
    MAX_TILESETS,
    MIN_DIM,
    type MapDoc,
    type MapDocV2,
    type MapDocV3,
    type LayerDocV3,
    NAME_MAX,
    PASS_VALUES,
    type PassValue,
    SHADOW_MAX_LINES,
    SHADOW_MAX_POINTS_PER_LINE,
    type ShadowData,
    TAG_MAX,
    TILESIZE_MAX,
    TILESIZE_MIN,
    type TileAttr,
    type TilesetDoc,
    V3_LAYER_COUNT,
    VISION_DEFAULT,
    VISION_MAX,
    VISION_MIN,
    coordToCell,
    defaultTileAttr,
    resolveCellV2,
    resolveCellV3,
    upgradeV2DocToV3,
} from "./model";
import { parsePngDataUri } from "./png";
import { v1ToV3 } from "./presets";

export type ValidationResult =
    | { ok: true; doc: MapDoc; warnings: string[] }
    | { ok: false; errors: string[] };

export type ValidationResultV2 =
    | { ok: true; doc: MapDocV2; warnings: string[] }
    | { ok: false; errors: string[] };

export type ValidationResultV3 =
    | { ok: true; doc: MapDocV3; warnings: string[] }
    | { ok: false; errors: string[] };

export type ValidationResultAny =
    | { ok: true; doc: MapDoc | MapDocV2 | MapDocV3; warnings: string[] }
    | { ok: false; errors: string[] };

const CELL_RE = /^[.#-]*$/;

function isRecord(v: unknown): v is Record<string, unknown> {
    return typeof v === "object" && v !== null && !Array.isArray(v);
}

/**
 * 生 JSON 値を検証して内部ドキュメントへ変換する。
 * @param value JSON.parse 済みの値
 * @param jsonByteLength 元 JSON テキストの UTF-8 バイト長 (512KB 上限チェック用)。省略時は再シリアライズで概算
 */
export function validateEkmap(value: unknown, jsonByteLength?: number): ValidationResult {
    const errors: string[] = [];
    const warnings: string[] = [];

    if (!isRecord(value)) {
        return { ok: false, errors: ["JSON のトップレベルがオブジェクトではありません"] };
    }

    // JSON 512 KB 超 → 拒否
    const bytes = jsonByteLength ?? new TextEncoder().encode(JSON.stringify(value)).length;
    if (bytes > MAX_JSON_BYTES) {
        errors.push(`JSON が 512 KB を超えています (${(bytes / 1024).toFixed(1)} KB)`);
    }

    // ekm != 1 → 拒否
    if (value.ekm !== 1) {
        errors.push(`ekm が 1 ではありません (値: ${JSON.stringify(value.ekm)})`);
    }

    // name 制約違反 → 拒否 (1〜64 文字)
    const name = value.name;
    if (typeof name !== "string" || name.length < 1 || name.length > NAME_MAX) {
        errors.push("name は 1〜64 文字の文字列が必要です");
    }

    // author (任意, 0〜32 文字)。仕様 §9 の拒否リストに author は無いため超過は警告+切り詰めで続行
    let author = "";
    if (value.author !== undefined) {
        if (typeof value.author !== "string") {
            warnings.push("author が文字列でないため無視しました");
        } else if (value.author.length > AUTHOR_MAX) {
            warnings.push(`author が ${AUTHOR_MAX} 文字を超えるため切り詰めました`);
            author = value.author.slice(0, AUTHOR_MAX);
        } else {
            author = value.author;
        }
    }

    // 寸法範囲外 → 拒否
    const width = value.width;
    const height = value.height;
    const dimOk = (v: unknown): v is number => Number.isInteger(v) && (v as number) >= MIN_DIM && (v as number) <= MAX_DIM;
    if (!dimOk(width)) errors.push(`width は ${MIN_DIM}〜${MAX_DIM} の整数が必要です`);
    if (!dimOk(height)) errors.push(`height は ${MIN_DIM}〜${MAX_DIM} の整数が必要です`);

    // cells 寸法不一致 / 不正文字 → 拒否
    const cells = value.cells;
    let cellRows: string[] | null = null;
    if (!Array.isArray(cells)) {
        errors.push("cells が配列ではありません");
    } else if (dimOk(width) && dimOk(height)) {
        if (cells.length !== height) {
            errors.push(`cells の行数 (${cells.length}) が height (${height}) と一致しません`);
        } else {
            let bad = false;
            for (let y = 0; y < cells.length; y++) {
                const row = cells[y];
                if (typeof row !== "string" || row.length !== width) {
                    errors.push(`cells[${y}] の長さが width (${width}) と一致しません`);
                    bad = true;
                    break;
                }
                if (!CELL_RE.test(row)) {
                    const m = row.match(/[^.#-]/);
                    errors.push(`cells[${y}] に不正な文字 '${m?.[0] ?? "?"}' があります (使用可: . # -)`);
                    bad = true;
                    break;
                }
            }
            if (!bad) cellRows = cells as string[];
        }
    }

    // spawn 欠落・床以外・範囲外 → 拒否
    let spawn: { x: number; y: number } | null = null;
    if (!isRecord(value.spawn) || typeof value.spawn.x !== "number" || typeof value.spawn.y !== "number" || !Number.isFinite(value.spawn.x) || !Number.isFinite(value.spawn.y)) {
        errors.push("spawn ({x, y} 数値) がありません");
    } else if (cellRows) {
        const sx = coordToCell(value.spawn.x);
        const sy = coordToCell(value.spawn.y);
        if (sx < 0 || sy < 0 || sx >= (width as number) || sy >= (height as number)) {
            errors.push(`spawn (${value.spawn.x}, ${value.spawn.y}) がマップ範囲外です`);
        } else if (cellRows[sy][sx] !== CELL_FLOOR) {
            errors.push(`spawn (${value.spawn.x}, ${value.spawn.y}) が床セル上にありません`);
        } else {
            spawn = { x: value.spawn.x, y: value.spawn.y };
        }
    }

    // decor: 未知 kind / 範囲外 → そのエントリだけ警告スキップ
    const decor: DecorEntry[] = [];
    if (value.decor !== undefined) {
        if (!Array.isArray(value.decor)) {
            warnings.push("decor が配列でないため無視しました");
        } else {
            for (let i = 0; i < value.decor.length; i++) {
                if (decor.length >= MAX_DECOR) {
                    warnings.push(`decor が ${MAX_DECOR} 件を超えるため残りをスキップしました`);
                    break;
                }
                const d = value.decor[i];
                if (!isRecord(d) || typeof d.kind !== "string" || typeof d.x !== "number" || typeof d.y !== "number" || !Number.isFinite(d.x) || !Number.isFinite(d.y)) {
                    warnings.push(`decor[${i}] の形式が不正なためスキップしました`);
                    continue;
                }
                if (!(DECOR_KINDS as readonly string[]).includes(d.kind)) {
                    warnings.push(`decor[${i}] の kind '${d.kind}' は未知のためスキップしました`);
                    continue;
                }
                if (cellRows && dimOk(width) && dimOk(height)) {
                    const cx = coordToCell(d.x);
                    const cy = coordToCell(d.y);
                    if (cx < 0 || cy < 0 || cx >= width || cy >= height) {
                        warnings.push(`decor[${i}] (${d.x}, ${d.y}) がマップ範囲外のためスキップしました`);
                        continue;
                    }
                }
                decor.push({ kind: d.kind, x: d.x, y: d.y });
            }
        }
    }

    // ambient: visionRadius はクランプして黙って許容。未知サブキーは generic に保全する (仕様 §9 黙って許容)
    let vision = VISION_DEFAULT;
    const ambientExtra: Record<string, unknown> = {};
    if (isRecord(value.ambient)) {
        // visionRadius 以外の既知キーを保全
        for (const [k, v] of Object.entries(value.ambient)) {
            if (k !== "visionRadius") ambientExtra[k] = v;
        }
        if (typeof value.ambient.visionRadius === "number" && Number.isFinite(value.ambient.visionRadius)) {
            vision = Math.min(VISION_MAX, Math.max(VISION_MIN, value.ambient.visionRadius));
        }
    }

    if (errors.length > 0) return { ok: false, errors };

    const w = width as number;
    const h = height as number;
    const grid: string[] = new Array(w * h);
    for (let y = 0; y < h; y++) {
        for (let x = 0; x < w; x++) grid[y * w + x] = cellRows![y][x];
    }

    return {
        ok: true,
        doc: {
            ekm: 1,
            name: name as string,
            author,
            width: w,
            height: h,
            grid,
            decor,
            spawn: spawn!,
            ambient: { ...ambientExtra, visionRadius: vision },
        },
        warnings,
    };
}

// ============================================================
// v2 検証 (仕様 §13/§14/§16)
// ============================================================

/**
 * tileset オブジェクトの検証 (仕様 §14)。エラー時は errors に積む。
 * label を指定するとエラーメッセージに「tilesets[i].」プレフィックスが付く。
 */
export function validateTileset(value: unknown, errors: string[], label = "tileset"): TilesetDoc | null {
    if (!isRecord(value)) {
        errors.push(`${label} がありません (v2/v3 では必須)`);
        return null;
    }

    const tileSize = value.tileSize;
    if (!Number.isInteger(tileSize) || (tileSize as number) < TILESIZE_MIN || (tileSize as number) > TILESIZE_MAX) {
        errors.push(`${label}.tileSize は ${TILESIZE_MIN}〜${TILESIZE_MAX} の整数が必要です`);
        return null;
    }
    const ts = tileSize as number;

    const columns = value.columns;
    if (!Number.isInteger(columns) || (columns as number) < 1) {
        errors.push(`${label}.columns は 1 以上の整数が必要です`);
        return null;
    }
    const cols = columns as number;

    const image = value.image;
    if (typeof image !== "string") {
        errors.push(`${label}.image (data:image/png;base64, の data URI) がありません`);
        return null;
    }
    const png = parsePngDataUri(image);
    if (!png.ok) {
        errors.push(`${label}.image: ${png.error}`);
        return null;
    }
    if (png.byteLength > MAX_TILESET_IMAGE_BYTES) {
        errors.push(`${label}.image の PNG が 1 MB を超えています (${(png.byteLength / 1024).toFixed(1)} KB)`);
        return null;
    }
    if (png.width !== cols * ts) {
        errors.push(`${label} 画像幅 (${png.width}px) が columns×tileSize (${cols}×${ts}=${cols * ts}px) と一致しません`);
        return null;
    }
    if (png.height % ts !== 0) {
        errors.push(`${label} 画像高さ (${png.height}px) が tileSize (${ts}px) の倍数ではありません`);
        return null;
    }
    const rows = png.height / ts;
    const count = cols * rows;
    if (count > MAX_TILECOUNT) {
        errors.push(`${label} tilecount (${count}) が上限 ${MAX_TILECOUNT} を超えています`);
        return null;
    }

    // tiles[]: 疎 → dense。id 範囲外・重複 / pass・tag・dir の範囲外値は検証エラー。未知キーは無視
    const tiles: TileAttr[] = Array.from({ length: count }, () => defaultTileAttr());
    if (value.tiles !== undefined) {
        if (!Array.isArray(value.tiles)) {
            errors.push(`${label}.tiles が配列ではありません`);
            return null;
        }
        if (value.tiles.length > count) {
            errors.push(`${label}.tiles が tilecount (${count}) を超える件数です`);
            return null;
        }
        const seen = new Set<number>();
        for (let i = 0; i < value.tiles.length; i++) {
            const t = value.tiles[i];
            if (!isRecord(t) || !Number.isInteger(t.id)) {
                errors.push(`${label}.tiles[${i}] の id が整数ではありません`);
                return null;
            }
            const id = t.id as number;
            if (id < 0 || id >= count) {
                errors.push(`${label}.tiles[${i}] の id (${id}) が範囲外です (0〜${count - 1})`);
                return null;
            }
            if (seen.has(id)) {
                errors.push(`${label}.tiles の id ${id} が重複しています`);
                return null;
            }
            seen.add(id);
            const attr = defaultTileAttr();
            if (t.pass !== undefined) {
                if (typeof t.pass !== "string" || !(PASS_VALUES as readonly string[]).includes(t.pass)) {
                    errors.push(`${label}.tiles[${i}] の pass '${String(t.pass)}' が不正です (使用可: o x v)`);
                    return null;
                }
                attr.pass = t.pass as PassValue;
            }
            if (t.over !== undefined) attr.over = t.over === true;
            if (t.light !== undefined) attr.light = t.light === true;
            if (t.tag !== undefined) {
                if (!Number.isInteger(t.tag) || (t.tag as number) < 0 || (t.tag as number) > TAG_MAX) {
                    errors.push(`${label}.tiles[${i}] の tag が範囲外です (0〜${TAG_MAX})`);
                    return null;
                }
                attr.tag = t.tag as number;
            }
            if (t.dir !== undefined) {
                if (!Number.isInteger(t.dir) || (t.dir as number) < 0 || (t.dir as number) > 15) {
                    errors.push(`${label}.tiles[${i}] の dir が範囲外です (0〜15)`);
                    return null;
                }
                attr.dir = t.dir as number; // 予約のみ — 保存するが実装しない (仕様 §14.1)
            }
            tiles[id] = attr;
        }
    }

    return { tileSize: ts, columns: cols, rows, image, tiles };
}

function validateLayer(raw: unknown, label: string, len: number, tilecount: number, errors: string[]): number[] | null {
    if (!Array.isArray(raw)) {
        errors.push(`layers.${label} が配列ではありません`);
        return null;
    }
    if (raw.length !== len) {
        errors.push(`layers.${label} の長さ (${raw.length}) が width×height (${len}) と一致しません`);
        return null;
    }
    const out = new Array<number>(len);
    for (let i = 0; i < len; i++) {
        const v = raw[i];
        if (!Number.isInteger(v) || (v as number) < -1 || (v as number) >= tilecount) {
            errors.push(`layers.${label}[${i}] の値 (${JSON.stringify(v)}) が範囲外です (-1〜${tilecount - 1})`);
            return null;
        }
        out[i] = v as number;
    }
    return out;
}

/**
 * v2 生 JSON 値を検証して内部ドキュメントへ変換する (仕様 §16)。
 * @param jsonByteLength 元 JSON テキストの UTF-8 バイト長 (4MB 上限チェック用)。省略時は再シリアライズで概算
 */
export function validateEkmapV2(value: unknown, jsonByteLength?: number): ValidationResultV2 {
    const errors: string[] = [];
    const warnings: string[] = [];

    if (!isRecord(value)) {
        return { ok: false, errors: ["JSON のトップレベルがオブジェクトではありません"] };
    }

    // JSON 4 MB 超 → 拒否 (v2 は 4MB、v1 の 512KB とは別)
    const bytes = jsonByteLength ?? new TextEncoder().encode(JSON.stringify(value)).length;
    if (bytes > MAX_JSON_BYTES_V2) {
        errors.push(`JSON が 4 MB を超えています (${(bytes / 1024 / 1024).toFixed(2)} MB)`);
    }

    if (value.ekm !== 2) {
        errors.push(`ekm が 2 ではありません (値: ${JSON.stringify(value.ekm)})`);
    }

    // v2 では cells キーが存在してはならない (仕様 §13)
    if (value.cells !== undefined) {
        errors.push("v2 に cells キーは存在してはなりません (layers を使用)");
    }

    const name = value.name;
    if (typeof name !== "string" || name.length < 1 || name.length > NAME_MAX) {
        errors.push("name は 1〜64 文字の文字列が必要です");
    }

    let author = "";
    if (value.author !== undefined) {
        if (typeof value.author !== "string") {
            warnings.push("author が文字列でないため無視しました");
        } else if (value.author.length > AUTHOR_MAX) {
            warnings.push(`author が ${AUTHOR_MAX} 文字を超えるため切り詰めました`);
            author = value.author.slice(0, AUTHOR_MAX);
        } else {
            author = value.author;
        }
    }

    const width = value.width;
    const height = value.height;
    const dimOk = (v: unknown): v is number => Number.isInteger(v) && (v as number) >= MIN_DIM && (v as number) <= MAX_DIM;
    if (!dimOk(width)) errors.push(`width は ${MIN_DIM}〜${MAX_DIM} の整数が必要です`);
    if (!dimOk(height)) errors.push(`height は ${MIN_DIM}〜${MAX_DIM} の整数が必要です`);

    const tileset = validateTileset(value.tileset, errors);

    // layers
    let ground: number[] | null = null;
    let upper: number[] | null = null;
    if (!isRecord(value.layers)) {
        errors.push("layers ({ground, upper?}) がありません");
    } else if (tileset && dimOk(width) && dimOk(height)) {
        const len = width * height;
        const count = tileset.columns * tileset.rows;
        ground = validateLayer(value.layers.ground, "ground", len, count, errors);
        if (value.layers.upper !== undefined) {
            upper = validateLayer(value.layers.upper, "upper", len, count, errors);
        } else {
            upper = new Array<number>(len).fill(-1); // 省略 = 全 -1 (仕様 §13)
        }
    }

    // decor: v1 と同一規則 (未知 kind / 範囲外 → エントリ警告スキップ)
    const decor: DecorEntry[] = [];
    if (value.decor !== undefined) {
        if (!Array.isArray(value.decor)) {
            warnings.push("decor が配列でないため無視しました");
        } else {
            for (let i = 0; i < value.decor.length; i++) {
                if (decor.length >= MAX_DECOR) {
                    warnings.push(`decor が ${MAX_DECOR} 件を超えるため残りをスキップしました`);
                    break;
                }
                const d = value.decor[i];
                if (!isRecord(d) || typeof d.kind !== "string" || typeof d.x !== "number" || typeof d.y !== "number" || !Number.isFinite(d.x) || !Number.isFinite(d.y)) {
                    warnings.push(`decor[${i}] の形式が不正なためスキップしました`);
                    continue;
                }
                if (!(DECOR_KINDS as readonly string[]).includes(d.kind)) {
                    warnings.push(`decor[${i}] の kind '${d.kind}' は未知のためスキップしました`);
                    continue;
                }
                if (dimOk(width) && dimOk(height)) {
                    const cx = coordToCell(d.x);
                    const cy = coordToCell(d.y);
                    if (cx < 0 || cy < 0 || cx >= width || cy >= height) {
                        warnings.push(`decor[${i}] (${d.x}, ${d.y}) がマップ範囲外のためスキップしました`);
                        continue;
                    }
                }
                decor.push({ kind: d.kind, x: d.x, y: d.y });
            }
        }
    }

    // ambient: v1 と同一規則 (クランプして黙って許容)。未知サブキーは generic に保全する
    let vision = VISION_DEFAULT;
    const ambientExtraV2: Record<string, unknown> = {};
    if (isRecord(value.ambient)) {
        for (const [k, v] of Object.entries(value.ambient)) {
            if (k !== "visionRadius") ambientExtraV2[k] = v;
        }
        if (typeof value.ambient.visionRadius === "number" && Number.isFinite(value.ambient.visionRadius)) {
            vision = Math.min(VISION_MAX, Math.max(VISION_MIN, value.ambient.visionRadius));
        }
    }

    // spawn: 欠落・範囲外 → 拒否。床判定は「実効通行可セル」基準 (仕様 §15/§16)
    let spawn: { x: number; y: number } | null = null;
    if (!isRecord(value.spawn) || typeof value.spawn.x !== "number" || typeof value.spawn.y !== "number" || !Number.isFinite(value.spawn.x) || !Number.isFinite(value.spawn.y)) {
        errors.push("spawn ({x, y} 数値) がありません");
    } else {
        spawn = { x: value.spawn.x, y: value.spawn.y };
    }

    if (errors.length > 0) return { ok: false, errors };

    const doc: MapDocV2 = {
        ekm: 2,
        name: name as string,
        author,
        width: width as number,
        height: height as number,
        tileset: tileset!,
        ground: ground!,
        upper: upper!,
        decor,
        spawn: spawn!,
        ambient: { ...ambientExtraV2, visionRadius: vision },
    };

    const sx = coordToCell(spawn!.x);
    const sy = coordToCell(spawn!.y);
    if (sx < 0 || sy < 0 || sx >= doc.width || sy >= doc.height) {
        return { ok: false, errors: [`spawn (${spawn!.x}, ${spawn!.y}) がマップ範囲外です`] };
    }
    const res = resolveCellV2(doc, sx, sy);
    if (!res.passable) {
        return { ok: false, errors: [`spawn (${spawn!.x}, ${spawn!.y}) が実効通行可セル上にありません`] };
    }

    return { ok: true, doc, warnings };
}

// ============================================================
// v3 検証 (仕様 §19/§20)
// ============================================================

/** v3 layers[] 1 要素を検証して LayerDocV3 に変換。tilesets の長さで tileset index を検証する */
function validateLayerV3(
    raw: unknown,
    label: string,
    len: number,
    tilesetCount: number,
    errors: string[],
    tilecountFor: (tsIndex: number) => number,
): LayerDocV3 | null {
    if (!isRecord(raw)) {
        errors.push(`${label} がオブジェクトではありません`);
        return null;
    }

    const tsIndex = raw.tileset;
    if (!Number.isInteger(tsIndex) || (tsIndex as number) < 0 || (tsIndex as number) >= tilesetCount) {
        errors.push(`${label}.tileset の値 (${JSON.stringify(tsIndex)}) が範囲外です (0〜${tilesetCount - 1})`);
        return null;
    }
    const ti = tsIndex as number;
    const tilecount = tilecountFor(ti);

    const cells = raw.cells;
    if (!Array.isArray(cells)) {
        errors.push(`${label}.cells が配列ではありません`);
        return null;
    }
    if (cells.length !== len) {
        errors.push(`${label}.cells の長さ (${cells.length}) が width×height (${len}) と一致しません`);
        return null;
    }
    const out = new Array<number>(len);
    for (let i = 0; i < len; i++) {
        const v = cells[i];
        if (!Number.isInteger(v) || (v as number) < -1 || (v as number) >= tilecount) {
            errors.push(`${label}.cells[${i}] の値 (${JSON.stringify(v)}) が範囲外です (-1〜${tilecount - 1})`);
            return null;
        }
        out[i] = v as number;
    }

    const above = raw.above === true;
    return { tileset: ti, cells: out, above };
}

/**
 * v3 生 JSON 値を検証して内部ドキュメントへ変換する (仕様 §20)。
 * 読込後 4 層へのパディングも行う。
 */
export function validateEkmapV3(value: unknown, jsonByteLength?: number): ValidationResultV3 {
    const errors: string[] = [];
    const warnings: string[] = [];

    if (!isRecord(value)) {
        return { ok: false, errors: ["JSON のトップレベルがオブジェクトではありません"] };
    }

    // JSON 4 MB 超 → 拒否
    const bytes = jsonByteLength ?? new TextEncoder().encode(JSON.stringify(value)).length;
    if (bytes > MAX_JSON_BYTES_V2) {
        errors.push(`JSON が 4 MB を超えています (${(bytes / 1024 / 1024).toFixed(2)} MB)`);
    }

    if (value.ekm !== 3) {
        errors.push(`ekm が 3 ではありません (値: ${JSON.stringify(value.ekm)})`);
    }

    // v3 では cells / tileset(単数) が存在してはならない (§20)
    if (value.cells !== undefined) {
        errors.push("v3 に cells キーは存在してはなりません (layers を使用)");
    }
    if (value.tileset !== undefined) {
        errors.push("v3 に tileset (単数形) は存在してはなりません (tilesets 配列を使用)");
    }

    // requires: 未対応 capability があれば拒否 (§20.1)
    // v3 凍結時点での対応 capability は空 Set
    let requires: string[] | undefined;
    if (value.requires !== undefined) {
        if (!Array.isArray(value.requires)) {
            errors.push("requires が配列ではありません");
        } else {
            const supported = new Set<string>(); // 現時点では空
            const unknown: string[] = [];
            for (const cap of value.requires) {
                if (typeof cap === "string" && !supported.has(cap)) unknown.push(cap);
            }
            if (unknown.length > 0) {
                errors.push(`このマップを開くには新しいバージョンが必要です(必要機能: ${unknown.join(", ")})`);
            }
            requires = value.requires.filter((c: unknown) => typeof c === "string") as string[];
        }
    }

    // name
    const name = value.name;
    if (typeof name !== "string" || name.length < 1 || name.length > NAME_MAX) {
        errors.push("name は 1〜64 文字の文字列が必要です");
    }

    // author
    let author = "";
    if (value.author !== undefined) {
        if (typeof value.author !== "string") {
            warnings.push("author が文字列でないため無視しました");
        } else if (value.author.length > AUTHOR_MAX) {
            warnings.push(`author が ${AUTHOR_MAX} 文字を超えるため切り詰めました`);
            author = value.author.slice(0, AUTHOR_MAX);
        } else {
            author = value.author;
        }
    }

    // width / height
    const width = value.width;
    const height = value.height;
    const dimOk = (v: unknown): v is number => Number.isInteger(v) && (v as number) >= MIN_DIM && (v as number) <= MAX_DIM;
    if (!dimOk(width)) errors.push(`width は ${MIN_DIM}〜${MAX_DIM} の整数が必要です`);
    if (!dimOk(height)) errors.push(`height は ${MIN_DIM}〜${MAX_DIM} の整数が必要です`);

    // tilesets: 1〜4 要素、各要素は §14 検証
    const tilesets: TilesetDoc[] = [];
    if (!Array.isArray(value.tilesets) || value.tilesets.length < 1) {
        errors.push("tilesets は 1〜4 要素の配列が必要です");
    } else if (value.tilesets.length > MAX_TILESETS) {
        errors.push(`tilesets が ${MAX_TILESETS} 個を超えています (${value.tilesets.length} 個)`);
    } else {
        for (let i = 0; i < value.tilesets.length; i++) {
            const ts = validateTileset(value.tilesets[i], errors, `tilesets[${i}]`);
            if (ts) tilesets.push(ts);
            else tilesets.push({ tileSize: 32, columns: 1, rows: 1, image: "", tiles: [] }); // placeholder
        }
    }

    // layers: 1〜4 要素
    const rawLayers = value.layers;
    const layers: LayerDocV3[] = [];
    if (!Array.isArray(rawLayers) || rawLayers.length < 1) {
        errors.push("layers は 1〜4 要素の配列が必要です");
    } else if (rawLayers.length > V3_LAYER_COUNT) {
        errors.push(`layers が ${V3_LAYER_COUNT} 個を超えています (${rawLayers.length} 個)`);
    } else if (dimOk(width) && dimOk(height) && tilesets.length > 0) {
        const len = width * height;
        const tilecountFor = (ti: number): number => {
            const ts = tilesets[ti];
            return ts ? ts.columns * ts.rows : 0;
        };
        for (let i = 0; i < rawLayers.length; i++) {
            const layer = validateLayerV3(rawLayers[i], `layers[${i}]`, len, tilesets.length, errors, tilecountFor);
            if (layer) layers.push(layer);
        }
    }

    // decor
    const decor: DecorEntry[] = [];
    if (value.decor !== undefined) {
        if (!Array.isArray(value.decor)) {
            warnings.push("decor が配列でないため無視しました");
        } else {
            for (let i = 0; i < value.decor.length; i++) {
                if (decor.length >= MAX_DECOR) {
                    warnings.push(`decor が ${MAX_DECOR} 件を超えるため残りをスキップしました`);
                    break;
                }
                const d = value.decor[i];
                if (!isRecord(d) || typeof d.kind !== "string" || typeof d.x !== "number" || typeof d.y !== "number" || !Number.isFinite(d.x) || !Number.isFinite(d.y)) {
                    warnings.push(`decor[${i}] の形式が不正なためスキップしました`);
                    continue;
                }
                if (!(DECOR_KINDS as readonly string[]).includes(d.kind)) {
                    warnings.push(`decor[${i}] の kind '${d.kind}' は未知のためスキップしました`);
                    continue;
                }
                if (dimOk(width) && dimOk(height)) {
                    const cx = coordToCell(d.x);
                    const cy = coordToCell(d.y);
                    if (cx < 0 || cy < 0 || cx >= width || cy >= height) {
                        warnings.push(`decor[${i}] (${d.x}, ${d.y}) がマップ範囲外のためスキップしました`);
                        continue;
                    }
                }
                decor.push({ kind: d.kind, x: d.x, y: d.y });
            }
        }
    }

    // ambient
    let vision = VISION_DEFAULT;
    const ambientExtra: Record<string, unknown> = {};
    if (isRecord(value.ambient)) {
        for (const [k, v] of Object.entries(value.ambient)) {
            if (k !== "visionRadius") ambientExtra[k] = v;
        }
        if (typeof value.ambient.visionRadius === "number" && Number.isFinite(value.ambient.visionRadius)) {
            vision = Math.min(VISION_MAX, Math.max(VISION_MIN, value.ambient.visionRadius));
        }
    }

    // spawn
    let spawn: { x: number; y: number } | null = null;
    if (!isRecord(value.spawn) || typeof value.spawn.x !== "number" || typeof value.spawn.y !== "number" || !Number.isFinite(value.spawn.x) || !Number.isFinite(value.spawn.y)) {
        errors.push("spawn ({x, y} 数値) がありません");
    } else {
        spawn = { x: value.spawn.x, y: value.spawn.y };
    }

    if (errors.length > 0) return { ok: false, errors };

    // 4 層にパディング (§22.1)
    while (layers.length < V3_LAYER_COUNT) {
        layers.push({
            tileset: 0,
            cells: new Array((width as number) * (height as number)).fill(-1),
            above: false,
        });
    }

    // §25 — shadow (任意・lenient)
    const shadow = validateShadow(value, warnings);

    const doc: MapDocV3 = {
        ekm: 3,
        name: name as string,
        author,
        width: width as number,
        height: height as number,
        tilesets,
        layers: layers as [LayerDocV3, LayerDocV3, LayerDocV3, LayerDocV3],
        decor,
        spawn: spawn!,
        ambient: { ...ambientExtra, visionRadius: vision },
    };
    if (requires !== undefined) doc.requires = requires;
    if (shadow && shadow.lines.length > 0) doc.shadow = shadow;

    // spawn: 実効通行可セル基準 (§20/§21)
    const sx = coordToCell(spawn!.x);
    const sy = coordToCell(spawn!.y);
    if (sx < 0 || sy < 0 || sx >= doc.width || sy >= doc.height) {
        return { ok: false, errors: [`spawn (${spawn!.x}, ${spawn!.y}) がマップ範囲外です`] };
    }
    const res = resolveCellV3(doc, sx, sy);
    if (!res.passable) {
        return { ok: false, errors: [`spawn (${spawn!.x}, ${spawn!.y}) が実効通行可セル上にありません`] };
    }

    return { ok: true, doc, warnings };
}

// ============================================================
// §25 — 影レイヤー検証 (version 非依存・任意フィールド)
// ============================================================

/**
 * トップレベル `shadow` フィールドを lenient に検証して ShadowData を返す。
 * - shadow が存在しない/配列でない → undefined (警告なし)
 * - 個々の lines[i] が不正 (奇数長/点不足/非有限) → その 1 本だけ警告スキップ
 * - 点数超過 → 末尾切り詰め (警告あり)
 * - 本数超過 → 超過分を捨てる (警告あり)
 */
export function validateShadow(value: Record<string, unknown>, warnings: string[]): ShadowData | undefined {
    const raw = value.shadow;
    if (raw === undefined || raw === null) return undefined;
    if (!isRecord(raw) || !Array.isArray(raw.lines)) {
        warnings.push("shadow の形式が不正なため無視しました");
        return undefined;
    }

    const lines: number[][] = [];
    const rawLines = raw.lines as unknown[];

    for (let i = 0; i < rawLines.length; i++) {
        if (lines.length >= SHADOW_MAX_LINES) {
            warnings.push(`shadow.lines が ${SHADOW_MAX_LINES} 本を超えるため残りをスキップしました`);
            break;
        }
        const rl = rawLines[i];
        if (!Array.isArray(rl)) {
            warnings.push(`shadow.lines[${i}] が配列でないためスキップしました`);
            continue;
        }
        // 偶数長チェック
        if (rl.length % 2 !== 0) {
            warnings.push(`shadow.lines[${i}] の要素数が奇数 (${rl.length}) のためスキップしました`);
            continue;
        }
        // 最低 2 点 (= 4 要素)
        if (rl.length < 4) {
            warnings.push(`shadow.lines[${i}] の点数が不足 (最低 2 点 = 4 要素必要) のためスキップしました`);
            continue;
        }
        // number 配列チェック + 非有限チェック
        let hasInvalid = false;
        for (let j = 0; j < rl.length; j++) {
            if (typeof rl[j] !== "number" || !Number.isFinite(rl[j])) {
                warnings.push(`shadow.lines[${i}][${j}] が有効な数値でないためスキップしました`);
                hasInvalid = true;
                break;
            }
        }
        if (hasInvalid) continue;

        // 点数上限チェック (点数 = rl.length / 2)
        let line = rl as number[];
        if (rl.length / 2 > SHADOW_MAX_POINTS_PER_LINE) {
            warnings.push(`shadow.lines[${i}] の点数が ${SHADOW_MAX_POINTS_PER_LINE} を超えるため切り詰めました`);
            line = rl.slice(0, SHADOW_MAX_POINTS_PER_LINE * 2) as number[];
        }

        lines.push([...line]);
    }

    return { lines };
}

/** ekm 値で v1/v2/v3 を振り分ける共通入口。v2 は成功後に v3 へ自動変換する。v1 も v3 へ変換する */
export function validateEkmapAny(value: unknown, jsonByteLength?: number): ValidationResultAny {
    if (isRecord(value) && value.ekm === 3) return validateEkmapV3(value, jsonByteLength);
    if (isRecord(value) && value.ekm === 2) {
        const r2 = validateEkmapV2(value, jsonByteLength);
        if (!r2.ok) return r2;
        // v2 → v3 自動変換 (仕様 §22.2)
        const docV3 = upgradeV2DocToV3(r2.doc);
        const warnings = [...r2.warnings, "v2 マップを v3 形式に変換しました(次回保存から ekm:3)"];
        return { ok: true, doc: docV3, warnings };
    }
    // v1 → v3 自動変換 (タスク B: エディタは v3 に一本化)
    const r1 = validateEkmap(value, jsonByteLength);
    if (!r1.ok) return r1;
    const docV3 = v1ToV3(r1.doc);
    const warnings = [...r1.warnings, "v1 マップを Backrooms タイルセットで v3 形式に変換しました(次回保存から ekm:3)"];
    return { ok: true, doc: docV3, warnings };
}
