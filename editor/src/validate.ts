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
    MIN_DIM,
    type MapDoc,
    type MapDocV2,
    NAME_MAX,
    PASS_VALUES,
    type PassValue,
    TAG_MAX,
    TILESIZE_MAX,
    TILESIZE_MIN,
    type TileAttr,
    type TilesetDoc,
    VISION_DEFAULT,
    VISION_MAX,
    VISION_MIN,
    coordToCell,
    defaultTileAttr,
    resolveCellV2,
} from "./model";
import { parsePngDataUri } from "./png";

export type ValidationResult =
    | { ok: true; doc: MapDoc; warnings: string[] }
    | { ok: false; errors: string[] };

export type ValidationResultV2 =
    | { ok: true; doc: MapDocV2; warnings: string[] }
    | { ok: false; errors: string[] };

export type ValidationResultAny =
    | { ok: true; doc: MapDoc | MapDocV2; warnings: string[] }
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

/** tileset オブジェクトの検証 (仕様 §14)。エラー時は errors に積む */
export function validateTileset(value: unknown, errors: string[]): TilesetDoc | null {
    if (!isRecord(value)) {
        errors.push("tileset がありません (v2 では必須)");
        return null;
    }

    const tileSize = value.tileSize;
    if (!Number.isInteger(tileSize) || (tileSize as number) < TILESIZE_MIN || (tileSize as number) > TILESIZE_MAX) {
        errors.push(`tileset.tileSize は ${TILESIZE_MIN}〜${TILESIZE_MAX} の整数が必要です`);
        return null;
    }
    const ts = tileSize as number;

    const columns = value.columns;
    if (!Number.isInteger(columns) || (columns as number) < 1) {
        errors.push("tileset.columns は 1 以上の整数が必要です");
        return null;
    }
    const cols = columns as number;

    const image = value.image;
    if (typeof image !== "string") {
        errors.push("tileset.image (data:image/png;base64, の data URI) がありません");
        return null;
    }
    const png = parsePngDataUri(image);
    if (!png.ok) {
        errors.push(`tileset.image: ${png.error}`);
        return null;
    }
    if (png.byteLength > MAX_TILESET_IMAGE_BYTES) {
        errors.push(`tileset.image の PNG が 1 MB を超えています (${(png.byteLength / 1024).toFixed(1)} KB)`);
        return null;
    }
    if (png.width !== cols * ts) {
        errors.push(`tileset 画像幅 (${png.width}px) が columns×tileSize (${cols}×${ts}=${cols * ts}px) と一致しません`);
        return null;
    }
    if (png.height % ts !== 0) {
        errors.push(`tileset 画像高さ (${png.height}px) が tileSize (${ts}px) の倍数ではありません`);
        return null;
    }
    const rows = png.height / ts;
    const count = cols * rows;
    if (count > MAX_TILECOUNT) {
        errors.push(`tilecount (${count}) が上限 ${MAX_TILECOUNT} を超えています`);
        return null;
    }

    // tiles[]: 疎 → dense。id 範囲外・重複 / pass・tag・dir の範囲外値は検証エラー。未知キーは無視
    const tiles: TileAttr[] = Array.from({ length: count }, () => defaultTileAttr());
    if (value.tiles !== undefined) {
        if (!Array.isArray(value.tiles)) {
            errors.push("tileset.tiles が配列ではありません");
            return null;
        }
        if (value.tiles.length > count) {
            errors.push(`tileset.tiles が tilecount (${count}) を超える件数です`);
            return null;
        }
        const seen = new Set<number>();
        for (let i = 0; i < value.tiles.length; i++) {
            const t = value.tiles[i];
            if (!isRecord(t) || !Number.isInteger(t.id)) {
                errors.push(`tileset.tiles[${i}] の id が整数ではありません`);
                return null;
            }
            const id = t.id as number;
            if (id < 0 || id >= count) {
                errors.push(`tileset.tiles[${i}] の id (${id}) が範囲外です (0〜${count - 1})`);
                return null;
            }
            if (seen.has(id)) {
                errors.push(`tileset.tiles の id ${id} が重複しています`);
                return null;
            }
            seen.add(id);
            const attr = defaultTileAttr();
            if (t.pass !== undefined) {
                if (typeof t.pass !== "string" || !(PASS_VALUES as readonly string[]).includes(t.pass)) {
                    errors.push(`tileset.tiles[${i}] の pass '${String(t.pass)}' が不正です (使用可: o x v)`);
                    return null;
                }
                attr.pass = t.pass as PassValue;
            }
            if (t.over !== undefined) attr.over = t.over === true;
            if (t.light !== undefined) attr.light = t.light === true;
            if (t.tag !== undefined) {
                if (!Number.isInteger(t.tag) || (t.tag as number) < 0 || (t.tag as number) > TAG_MAX) {
                    errors.push(`tileset.tiles[${i}] の tag が範囲外です (0〜${TAG_MAX})`);
                    return null;
                }
                attr.tag = t.tag as number;
            }
            if (t.dir !== undefined) {
                if (!Number.isInteger(t.dir) || (t.dir as number) < 0 || (t.dir as number) > 15) {
                    errors.push(`tileset.tiles[${i}] の dir が範囲外です (0〜15)`);
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

/** ekm 値で v1/v2 を振り分ける共通入口 */
export function validateEkmapAny(value: unknown, jsonByteLength?: number): ValidationResultAny {
    if (isRecord(value) && value.ekm === 2) return validateEkmapV2(value, jsonByteLength);
    return validateEkmap(value, jsonByteLength);
}
