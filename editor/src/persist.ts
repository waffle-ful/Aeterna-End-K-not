// IndexedDB 自動保存 (単一スロット) + 復元
// 自動保存は編集途中の不正状態 (spawn が床以外など) でも作業を失わないため、
// 正規検証に落ちた場合も構造チェックのみの lenient 復元を試みる。

import {
    AUTHOR_MAX,
    type AnyDoc,
    CELL_FLOOR,
    CELL_VOID,
    CELL_WALL,
    DECOR_KINDS,
    type DecorEntry,
    MAX_DECOR,
    MAX_DIM,
    MAX_TILESETS,
    MIN_DIM,
    type MapDocV2,
    type MapDocV3,
    type LayerDocV3,
    NAME_MAX,
    type SpawnPoint,
    V3_LAYER_COUNT,
    VISION_DEFAULT,
    VISION_MAX,
    VISION_MIN,
    coordToCell,
} from "./model";
import { validateEkmap, validateEkmapV2, validateEkmapV3, validateShadow, validateTileset } from "./validate";
import { v1ToV3 } from "./presets";

const DB_NAME = "ekmap-editor";
const STORE = "docs";
const KEY = "autosave";
/** 新規作成・ファイル読込・コード読込の直前に退避するバックアップスロット */
const KEY_BACKUP = "autosave-backup";
/** オートタイル定義 (エディタ専用・.ekmap には出さない) の保存キー */
const KEY_AUTOTILES = "autosave-autotiles";

function openDb(): Promise<IDBDatabase> {
    return new Promise((resolve, reject) => {
        const req = indexedDB.open(DB_NAME, 1);
        req.onupgradeneeded = () => {
            req.result.createObjectStore(STORE);
        };
        req.onsuccess = () => resolve(req.result);
        req.onerror = () => reject(req.error);
    });
}

/**
 * 現在の autosave を backup スロットへ退避する。
 * 新規作成・ファイル読込・コード読込の直前に呼ぶこと。
 * 失敗しても本処理を妨げないよう例外は握りつぶす。
 */
export async function backupAutosave(): Promise<void> {
    try {
        const current = await loadAutosave();
        if (current === undefined) return; // 退避するものがない
        const db = await openDb();
        try {
            await new Promise<void>((resolve, reject) => {
                const tx = db.transaction(STORE, "readwrite");
                tx.objectStore(STORE).put(current, KEY_BACKUP);
                tx.oncomplete = () => resolve();
                tx.onerror = () => reject(tx.error);
            });
        } finally {
            db.close();
        }
    } catch {
        // バックアップ失敗は握りつぶして本処理を妨げない
    }
}

/** 失敗しても編集は続行できるよう例外は握りつぶす */
export async function saveAutosave(value: unknown): Promise<void> {
    try {
        const db = await openDb();
        try {
            await new Promise<void>((resolve, reject) => {
                const tx = db.transaction(STORE, "readwrite");
                tx.objectStore(STORE).put(value, KEY);
                tx.oncomplete = () => resolve();
                tx.onerror = () => reject(tx.error);
            });
        } finally {
            db.close();
        }
    } catch {
        // IndexedDB 不可の環境 (プライベートモード等) では自動保存なしで続行
    }
}

/** オートタイル定義リストを別キーで保存する (失敗は握りつぶす) */
export async function saveAutotiles(value: unknown): Promise<void> {
    try {
        const db = await openDb();
        try {
            await new Promise<void>((resolve, reject) => {
                const tx = db.transaction(STORE, "readwrite");
                tx.objectStore(STORE).put(value, KEY_AUTOTILES);
                tx.oncomplete = () => resolve();
                tx.onerror = () => reject(tx.error);
            });
        } finally {
            db.close();
        }
    } catch {
        // IndexedDB 不可の環境では無視
    }
}

/** オートタイル定義リストを読み込む (失敗時は undefined) */
export async function loadAutotiles(): Promise<unknown> {
    try {
        const db = await openDb();
        try {
            return await new Promise<unknown>((resolve, reject) => {
                const tx = db.transaction(STORE, "readonly");
                const req = tx.objectStore(STORE).get(KEY_AUTOTILES);
                req.onsuccess = () => resolve(req.result);
                req.onerror = () => reject(req.error);
            });
        } finally {
            db.close();
        }
    } catch {
        return undefined;
    }
}

export async function loadAutosave(): Promise<unknown> {
    try {
        const db = await openDb();
        try {
            return await new Promise<unknown>((resolve, reject) => {
                const tx = db.transaction(STORE, "readonly");
                const req = tx.objectStore(STORE).get(KEY);
                req.onsuccess = () => resolve(req.result);
                req.onerror = () => reject(req.error);
            });
        } finally {
            db.close();
        }
    } catch {
        return undefined;
    }
}

function isRecord(v: unknown): v is Record<string, unknown> {
    return typeof v === "object" && v !== null && !Array.isArray(v);
}

/** 自動保存スロットからの復元。正規検証 → 駄目なら構造のみの lenient 復元 → 駄目なら null */
export function tryRestoreDoc(value: unknown): AnyDoc | null {
    // v3: 正規検証 → 駄目なら lenient 復元
    if (isRecord(value) && value.ekm === 3) {
        const r3 = validateEkmapV3(value);
        if (r3.ok) return r3.doc;
        return tryRestoreDocV3Lenient(value);
    }

    // v2: 正規検証 → 駄目なら構造のみの lenient 復元 (spawn 不正・layer 値不正で作業を失わない)
    if (isRecord(value) && value.ekm === 2) {
        const r2 = validateEkmapV2(value);
        if (r2.ok) return r2.doc;
        return tryRestoreDocV2Lenient(value);
    }

    // v1: 正規検証 → 成功なら v3 に変換して返す
    const r = validateEkmap(value);
    if (r.ok) return v1ToV3(r.doc);

    // v1 lenient 復元 → v3 変換
    if (!isRecord(value)) return null;
    const w = value.width;
    const h = value.height;
    const dimOk = (v: unknown): v is number => Number.isInteger(v) && (v as number) >= MIN_DIM && (v as number) <= MAX_DIM;
    if (!dimOk(w) || !dimOk(h)) return null;

    const rows = Array.isArray(value.cells) ? value.cells : [];
    const grid: string[] = new Array(w * h).fill(CELL_VOID);
    for (let y = 0; y < h; y++) {
        const row = typeof rows[y] === "string" ? (rows[y] as string) : "";
        for (let x = 0; x < w; x++) {
            const c = row[x];
            grid[y * w + x] = c === CELL_FLOOR || c === CELL_WALL ? c : CELL_VOID;
        }
    }

    const name = typeof value.name === "string" && value.name.length > 0 ? value.name.slice(0, NAME_MAX) : "新しいマップ";
    const author = typeof value.author === "string" ? value.author.slice(0, AUTHOR_MAX) : "";

    let spawn: SpawnPoint = { x: Math.floor(w / 2), y: Math.floor(h / 2) };
    if (isRecord(value.spawn) && typeof value.spawn.x === "number" && typeof value.spawn.y === "number" && Number.isFinite(value.spawn.x) && Number.isFinite(value.spawn.y)) {
        spawn = { x: value.spawn.x, y: value.spawn.y };
    }

    const decor: DecorEntry[] = [];
    if (Array.isArray(value.decor)) {
        for (const d of value.decor) {
            if (decor.length >= MAX_DECOR) break;
            if (!isRecord(d) || typeof d.kind !== "string" || typeof d.x !== "number" || typeof d.y !== "number") continue;
            if (!(DECOR_KINDS as readonly string[]).includes(d.kind)) continue;
            const cx = coordToCell(d.x);
            const cy = coordToCell(d.y);
            if (cx < 0 || cy < 0 || cx >= w || cy >= h) continue;
            decor.push({ kind: d.kind, x: d.x, y: d.y });
        }
    }

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

    // lenient 復元した v1 doc を v3 に変換して返す
    const lenientV1 = { ekm: 1 as const, name, author, width: w as number, height: h as number, grid, decor, spawn, ambient: { ...ambientExtra, visionRadius: vision } };
    return v1ToV3(lenientV1);
}

/** v3 の lenient 復元。tilesets が全滅している場合のみ諦める (描画に必須のため) */
function tryRestoreDocV3Lenient(value: Record<string, unknown>): MapDocV3 | null {
    const w = value.width;
    const h = value.height;
    const dimOk = (v: unknown): v is number => Number.isInteger(v) && (v as number) >= MIN_DIM && (v as number) <= MAX_DIM;
    if (!dimOk(w) || !dimOk(h)) return null;

    // tilesets の lenient 復元 (最低 1 セット必要)
    const rawTilesets = Array.isArray(value.tilesets) ? value.tilesets : [];
    const tilesets: NonNullable<ReturnType<typeof validateTileset>>[] = [];
    for (let i = 0; i < Math.min(rawTilesets.length, MAX_TILESETS); i++) {
        const ts = validateTileset(rawTilesets[i], []);
        if (ts) tilesets.push(ts);
    }
    if (tilesets.length === 0) return null;

    const len = w * h;
    const coerceLayerCells = (raw: unknown, tilecount: number): number[] => {
        const out = new Array<number>(len).fill(-1);
        if (Array.isArray(raw)) {
            for (let i = 0; i < Math.min(len, raw.length); i++) {
                const v = raw[i];
                out[i] = Number.isInteger(v) && (v as number) >= -1 && (v as number) < tilecount ? (v as number) : -1;
            }
        }
        return out;
    };

    // layers の lenient 復元
    const rawLayers = Array.isArray(value.layers) ? value.layers : [];
    const layers: LayerDocV3[] = [];
    for (let i = 0; i < Math.min(rawLayers.length, V3_LAYER_COUNT); i++) {
        const rl = rawLayers[i];
        if (typeof rl !== "object" || rl === null) continue;
        const rlRec = rl as Record<string, unknown>;
        const tsIdx = Number.isInteger(rlRec.tileset) && (rlRec.tileset as number) >= 0 && (rlRec.tileset as number) < tilesets.length
            ? (rlRec.tileset as number) : 0;
        const tilecount = tilesets[tsIdx].columns * tilesets[tsIdx].rows;
        const cells = coerceLayerCells(rlRec.cells, tilecount);
        const above = rlRec.above === true;
        layers.push({ tileset: tsIdx, cells, above });
    }
    // layers[0] は必須
    if (layers.length === 0) {
        layers.push({ tileset: 0, cells: new Array(len).fill(-1), above: false });
    }
    // 4 層にパディング
    while (layers.length < V3_LAYER_COUNT) {
        layers.push({ tileset: 0, cells: new Array(len).fill(-1), above: false });
    }

    const name = typeof value.name === "string" && value.name.length > 0 ? value.name.slice(0, NAME_MAX) : "新しいマップ";
    const author = typeof value.author === "string" ? value.author.slice(0, AUTHOR_MAX) : "";

    let spawn: SpawnPoint = { x: Math.floor(w / 2), y: Math.floor(h / 2) };
    if (isRecord(value.spawn) && typeof value.spawn.x === "number" && typeof value.spawn.y === "number" && Number.isFinite(value.spawn.x) && Number.isFinite(value.spawn.y)) {
        spawn = { x: value.spawn.x, y: value.spawn.y };
    }

    const decor: DecorEntry[] = [];
    if (Array.isArray(value.decor)) {
        for (const d of value.decor) {
            if (decor.length >= MAX_DECOR) break;
            if (!isRecord(d) || typeof d.kind !== "string" || typeof d.x !== "number" || typeof d.y !== "number") continue;
            if (!(DECOR_KINDS as readonly string[]).includes(d.kind)) continue;
            const cx = coordToCell(d.x);
            const cy = coordToCell(d.y);
            if (cx < 0 || cy < 0 || cx >= w || cy >= h) continue;
            decor.push({ kind: d.kind, x: d.x, y: d.y });
        }
    }

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

    const requires = Array.isArray(value.requires)
        ? (value.requires.filter((c: unknown) => typeof c === "string") as string[])
        : undefined;

    // §25 — shadow (lenient 復元: 不正線はスキップ)
    const lenientWarnings: string[] = [];
    const shadow = validateShadow(value, lenientWarnings);

    const doc: MapDocV3 = {
        ekm: 3,
        name,
        author,
        width: w,
        height: h,
        tilesets,
        layers: layers as [LayerDocV3, LayerDocV3, LayerDocV3, LayerDocV3],
        decor,
        spawn,
        ambient: { ...ambientExtra, visionRadius: vision },
    };
    if (requires && requires.length > 0) doc.requires = requires;
    if (shadow && shadow.lines.length > 0) doc.shadow = shadow;
    return doc;
}

/** v2 の lenient 復元。tileset が壊れている場合のみ諦める (描画に必須のため) */
function tryRestoreDocV2Lenient(value: Record<string, unknown>): MapDocV2 | null {
    const w = value.width;
    const h = value.height;
    const dimOk = (v: unknown): v is number => Number.isInteger(v) && (v as number) >= MIN_DIM && (v as number) <= MAX_DIM;
    if (!dimOk(w) || !dimOk(h)) return null;

    const tileset = validateTileset(value.tileset, []);
    if (!tileset) return null;

    const len = w * h;
    const count = tileset.columns * tileset.rows;
    const coerceLayer = (raw: unknown): number[] => {
        const out = new Array<number>(len).fill(-1);
        if (Array.isArray(raw)) {
            for (let i = 0; i < Math.min(len, raw.length); i++) {
                const v = raw[i];
                out[i] = Number.isInteger(v) && (v as number) >= -1 && (v as number) < count ? (v as number) : -1;
            }
        }
        return out;
    };
    const layers = isRecord(value.layers) ? value.layers : {};
    const ground = coerceLayer(layers.ground);
    const upper = coerceLayer(layers.upper);

    const name = typeof value.name === "string" && value.name.length > 0 ? value.name.slice(0, NAME_MAX) : "新しいマップ";
    const author = typeof value.author === "string" ? value.author.slice(0, AUTHOR_MAX) : "";

    let spawn: SpawnPoint = { x: Math.floor(w / 2), y: Math.floor(h / 2) };
    if (isRecord(value.spawn) && typeof value.spawn.x === "number" && typeof value.spawn.y === "number" && Number.isFinite(value.spawn.x) && Number.isFinite(value.spawn.y)) {
        spawn = { x: value.spawn.x, y: value.spawn.y };
    }

    const decor: DecorEntry[] = [];
    if (Array.isArray(value.decor)) {
        for (const d of value.decor) {
            if (decor.length >= MAX_DECOR) break;
            if (!isRecord(d) || typeof d.kind !== "string" || typeof d.x !== "number" || typeof d.y !== "number") continue;
            if (!(DECOR_KINDS as readonly string[]).includes(d.kind)) continue;
            const cx = coordToCell(d.x);
            const cy = coordToCell(d.y);
            if (cx < 0 || cy < 0 || cx >= w || cy >= h) continue;
            decor.push({ kind: d.kind, x: d.x, y: d.y });
        }
    }

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

    return { ekm: 2, name, author, width: w, height: h, tileset, ground, upper, decor, spawn, ambient: { ...ambientExtraV2, visionRadius: vision } };
}
