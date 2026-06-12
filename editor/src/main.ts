// エディタ本体: ツール/ストローク/Undo/Redo/入出力/自動保存の配線
// v1 (Backrooms チップ) と v2 (カスタムタイルセット + 2 レイヤー) の両モードを扱う

import "./style.css";
import {
    AUTHOR_MAX,
    type AnyDoc,
    CELL_FLOOR,
    CELL_VOID,
    CELL_WALL,
    type DecorEntry,
    type DecorKind,
    MAX_DECOR,
    MAX_DIM,
    MAX_JSON_BYTES,
    MAX_JSON_BYTES_V2,
    MAX_TILESET_IMAGE_BYTES,
    MIN_DIM,
    type MapDocV2,
    NAME_MAX,
    type PassValue,
    type SpawnPoint,
    TAG_MAX,
    TILESIZE_DEFAULT,
    TILESIZE_MAX,
    TILESIZE_MIN,
    type TilesetDoc,
    coordToCell,
    createNewDoc,
    createNewDocV2,
    docToJsonAny,
    isV2Doc,
    tileCount,
} from "./model";
import { validateEkmapAny, validateTileset } from "./validate";
import { PNG_DATA_URI_PREFIX, parsePngDataUri } from "./png";
import { decodeMapCode, encodeMapCode } from "./mapcode";
import { type CellChange, History, type Patch, applyPatch } from "./history";
import { backupAutosave, loadAutosave, saveAutosave, tryRestoreDoc } from "./persist";
import { MapRenderer, loadTilesetImage } from "./render";
import { InputController } from "./input";

type ToolV1 = "floor" | "wall" | "void" | DecorKind | "spawn";
type ToolV2 = "pen" | "erase" | "rect" | "bucket" | "pick";
type LayerTab = "ground" | "upper" | "decor" | "spawn";

const TOOL_CHAR: Partial<Record<ToolV1, string>> = { floor: CELL_FLOOR, wall: CELL_WALL, void: CELL_VOID };

function $<T extends HTMLElement>(id: string): T {
    return document.getElementById(id) as T;
}

let doc: AnyDoc = createNewDoc(32, 32, "新しいマップ", "");
let tool: ToolV1 = "wall";
let toolV2: ToolV2 = "pen";
let activeLayer: LayerTab = "ground";
let selectedTile = 0;
let decorKind2: DecorKind = "light";
const history = new History();
const renderer = new MapRenderer();

interface StrokeBuf {
    cells: Map<number, CellChange>;
    decorBefore: DecorEntry[] | null;
    spawnBefore: SpawnPoint | null;
}
let stroke: StrokeBuf | null = null;
/** 矩形ツールのドラッグ状態 (セル座標、マップ範囲にクランプ済み) */
let rectDrag: { ax: number; ay: number; cx: number; cy: number } | null = null;

// ---------- 通知 UI ----------

let toastTimer: number | undefined;

function toast(msg: string): void {
    const el = $("toast");
    el.textContent = msg;
    el.hidden = false;
    el.style.opacity = "1";
    window.clearTimeout(toastTimer);
    toastTimer = window.setTimeout(() => {
        el.style.opacity = "0";
        window.setTimeout(() => {
            el.hidden = true;
        }, 350);
    }, 2400);
}

function showMessages(title: string, errors: string[], warnings: string[]): void {
    $("msg-title").textContent = title;
    const ul = $("msg-list");
    ul.replaceChildren();
    for (const e of errors) {
        const li = document.createElement("li");
        li.className = "msg-error";
        li.textContent = e;
        ul.appendChild(li);
    }
    for (const w of warnings) {
        const li = document.createElement("li");
        li.className = "msg-warn";
        li.textContent = w;
        ul.appendChild(li);
    }
    $<HTMLDialogElement>("dlg-msg").showModal();
}

function refreshUndoButtons(): void {
    $<HTMLButtonElement>("btn-undo").disabled = !history.canUndo;
    $<HTMLButtonElement>("btn-redo").disabled = !history.canRedo;
}

let lastHover: { x: number; y: number } | null = null;

function updateStatus(): void {
    const zoom = Math.round(renderer.world.scale.x * 100);
    const inGrid = lastHover && lastHover.x >= 0 && lastHover.y >= 0 && lastHover.x < doc.width && lastHover.y < doc.height;
    const cellTxt = inGrid && lastHover ? `(${lastHover.x}, ${lastHover.y})` : "--";
    const v2Txt = isV2Doc(doc) ? ` | ${layerLabel(activeLayer)} | チップ ${selectedTile}` : "";
    $("status").textContent = `${doc.width}×${doc.height} | セル ${cellTxt} | ${zoom}%${v2Txt} | decor ${doc.decor.length}/${MAX_DECOR}`;
}

function layerLabel(l: LayerTab): string {
    return l === "ground" ? "下層" : l === "upper" ? "上層" : l === "decor" ? "装飾" : "スポーン";
}

// ---------- 自動保存 (IndexedDB 単一スロット) ----------

let saveTimer: number | undefined;
let savePending = false;

function scheduleSave(): void {
    savePending = true;
    window.clearTimeout(saveTimer);
    saveTimer = window.setTimeout(() => {
        savePending = false;
        void saveAutosave(docToJsonAny(doc));
    }, 500);
}

// ---------- モード UI (v1 ツール列 ⇔ v2 レイヤータブ+ツール列) ----------

function refreshModeUi(): void {
    const v2 = isV2Doc(doc);
    $("layer-tabs").hidden = !v2;
    $("tools").hidden = v2;
    $("tools-v2").hidden = !v2 || (activeLayer !== "ground" && activeLayer !== "upper");
    $("tools-decor2").hidden = !v2 || activeLayer !== "decor";
    $("spawn-hint").hidden = !v2 || activeLayer !== "spawn";
    if (v2) void rebuildPicker();
}

function setActiveLayer(l: LayerTab): void {
    activeLayer = l;
    for (const b of document.querySelectorAll<HTMLButtonElement>("#layer-tabs .ltab")) {
        b.classList.toggle("active", b.dataset.layer === l);
    }
    refreshModeUi();
    updateStatus();
}

function setToolV2(t: ToolV2): void {
    toolV2 = t;
    for (const b of document.querySelectorAll<HTMLButtonElement>("#tools-v2 .tool2")) {
        b.classList.toggle("active", b.dataset.tool2 === t);
    }
}

// ---------- タイルピッカー (v2 パレット = チップ一覧) ----------

const PICK_PX = 36;

async function rebuildPicker(): Promise<void> {
    if (!isV2Doc(doc)) return;
    const d = doc;
    const ts = d.tileset;
    const cv = $<HTMLCanvasElement>("picker-canvas");
    cv.width = ts.columns * PICK_PX;
    cv.height = ts.rows * PICK_PX;
    cv.style.width = `${cv.width}px`;
    cv.style.height = `${cv.height}px`;
    const ctx = cv.getContext("2d");
    if (!ctx) return;
    ctx.imageSmoothingEnabled = false;
    ctx.fillStyle = "#14141a";
    ctx.fillRect(0, 0, cv.width, cv.height);
    try {
        const img = await loadTilesetImage(ts.image);
        if (doc !== d) return; // doc が差し替わっていたら破棄
        ctx.drawImage(img, 0, 0, ts.columns * ts.tileSize, ts.rows * ts.tileSize, 0, 0, cv.width, cv.height);
    } catch {
        /* 画像破損時はプレースホルダのまま */
    }
    // 選択中チップの枠
    const sx = (selectedTile % ts.columns) * PICK_PX;
    const sy = Math.floor(selectedTile / ts.columns) * PICK_PX;
    ctx.strokeStyle = "#ffd75e";
    ctx.lineWidth = 3;
    ctx.strokeRect(sx + 1.5, sy + 1.5, PICK_PX - 3, PICK_PX - 3);
}

function selectTile(id: number): void {
    if (!isV2Doc(doc)) return;
    selectedTile = Math.min(Math.max(0, id), tileCount(doc.tileset) - 1);
    void rebuildPicker();
    updateStatus();
}

// ---------- ペイント (ストローク = 1 Undo 単位) ----------

function snapshotDecor(): void {
    if (stroke && !stroke.decorBefore) stroke.decorBefore = doc.decor.map((d) => ({ ...d }));
}

function removeDecorAt(x: number, y: number): void {
    if (!doc.decor.some((d) => coordToCell(d.x) === x && coordToCell(d.y) === y)) return;
    snapshotDecor();
    doc.decor = doc.decor.filter((d) => !(coordToCell(d.x) === x && coordToCell(d.y) === y));
    renderer.rebuildDecor();
}

function placeDecor(kind: DecorKind, x: number, y: number): void {
    snapshotDecor();
    const hadSame = doc.decor.some((d) => d.kind === kind && coordToCell(d.x) === x && coordToCell(d.y) === y);
    // 同セルの decor は一旦除去 (同種なら再クリック=除去、別種なら置換)
    doc.decor = doc.decor.filter((d) => !(coordToCell(d.x) === x && coordToCell(d.y) === y));
    if (!hadSame) {
        if (doc.decor.length >= MAX_DECOR) {
            toast(`decor は最大 ${MAX_DECOR} 件までです`);
            doc.decor = (stroke?.decorBefore ?? []).map((d) => ({ ...d }));
        } else {
            doc.decor.push({ kind, x, y });
        }
    }
    renderer.rebuildDecor();
}

function activeLayerArr(d: MapDocV2): number[] {
    return activeLayer === "upper" ? d.upper : d.ground;
}

/** v2 セルにタイル id (-1 = 空) を置く。stroke バッファに diff を積む */
function paintCellV2(d: MapDocV2, x: number, y: number, value: number): void {
    if (!stroke) return;
    const i = y * d.width + x;
    const arr = activeLayerArr(d);
    const before = arr[i];
    if (before === value) return;
    const ex = stroke.cells.get(i);
    if (ex) ex.after = value;
    else stroke.cells.set(i, { i, before, after: value });
    arr[i] = value;
    renderer.cellChanged(x, y);
    // 下層を空にしたセルは void になるので decor も除去 (v1 の奈落と同じ扱い)
    if (activeLayer === "ground" && value === -1) removeDecorAt(x, y);
}

function floodFillV2(d: MapDocV2, x: number, y: number, value: number): void {
    const arr = activeLayerArr(d);
    const target = arr[y * d.width + x];
    if (target === value) return;
    const stack: number[] = [y * d.width + x];
    while (stack.length > 0) {
        const i = stack.pop()!;
        if (arr[i] !== target) continue;
        const cx = i % d.width;
        const cy = Math.floor(i / d.width);
        paintCellV2(d, cx, cy, value);
        if (cx > 0) stack.push(i - 1);
        if (cx < d.width - 1) stack.push(i + 1);
        if (cy > 0) stack.push(i - d.width);
        if (cy < d.height - 1) stack.push(i + d.width);
    }
}

function clampCell(v: number, max: number): number {
    return Math.min(Math.max(0, v), max - 1);
}

function applyToolAtV2(d: MapDocV2, x: number, y: number, isStart: boolean): void {
    if (!stroke) return;
    const inGrid = x >= 0 && y >= 0 && x < d.width && y < d.height;

    if (activeLayer === "spawn") {
        if (!inGrid) return;
        stroke.spawnBefore ??= { ...d.spawn };
        d.spawn = { x, y };
        renderer.updateSpawn();
        return;
    }
    if (activeLayer === "decor") {
        if (!isStart || !inGrid) return; // クリック配置のみ
        placeDecor(decorKind2, x, y);
        return;
    }

    switch (toolV2) {
        case "pen":
            if (inGrid) paintCellV2(d, x, y, selectedTile);
            break;
        case "erase":
            if (inGrid) paintCellV2(d, x, y, -1);
            break;
        case "rect": {
            // ドラッグ中はプレビューのみ、確定は onStrokeEnd (範囲外はクランプ)
            const cx = clampCell(x, d.width);
            const cy = clampCell(y, d.height);
            if (isStart) rectDrag = { ax: cx, ay: cy, cx, cy };
            else if (rectDrag) {
                rectDrag.cx = cx;
                rectDrag.cy = cy;
            }
            if (rectDrag) renderer.setRectPreview(rectDrag.ax, rectDrag.ay, rectDrag.cx, rectDrag.cy);
            break;
        }
        case "bucket":
            if (isStart && inGrid) floodFillV2(d, x, y, selectedTile);
            break;
        case "pick": {
            if (!isStart || !inGrid) break;
            const i = y * d.width + x;
            // アクティブ層を優先、空ならもう一方の層から拾う
            const v = activeLayerArr(d)[i] >= 0 ? activeLayerArr(d)[i] : (activeLayer === "upper" ? d.ground : d.upper)[i];
            if (v >= 0) {
                selectTile(v);
                setToolV2("pen");
                toast(`チップ ${v} を選択しました`);
            }
            break;
        }
    }
}

function applyToolAt(x: number, y: number, isStart: boolean): void {
    if (!stroke) return;
    if (isV2Doc(doc)) {
        applyToolAtV2(doc, x, y, isStart);
        renderer.flush();
        updateStatus();
        return;
    }
    if (x < 0 || y < 0 || x >= doc.width || y >= doc.height) return;
    const ch = TOOL_CHAR[tool];
    if (ch !== undefined) {
        const i = y * doc.width + x;
        const before = doc.grid[i];
        if (before !== ch) {
            const ex = stroke.cells.get(i);
            if (ex) ex.after = ch;
            else stroke.cells.set(i, { i, before, after: ch });
            doc.grid[i] = ch;
            renderer.cellChanged(x, y);
        }
        if (ch === CELL_VOID) removeDecorAt(x, y); // 奈落/消去で decor 除去
        renderer.flush();
    } else if (tool === "spawn") {
        stroke.spawnBefore ??= { ...doc.spawn };
        doc.spawn = { x, y };
        renderer.updateSpawn();
    } else {
        if (!isStart) return; // decor はクリック配置のみ (ドラッグ連続スタンプはしない)
        placeDecor(tool as DecorKind, x, y); // floor/wall/void/spawn は上の分岐で処理済み
    }
    updateStatus();
}

function onStrokeStart(x: number, y: number): void {
    stroke = { cells: new Map(), decorBefore: null, spawnBefore: null };
    applyToolAt(x, y, true);
}

function onStrokeStep(x: number, y: number): void {
    applyToolAt(x, y, false);
}

function onStrokeEnd(): void {
    if (!stroke) return;
    // 矩形ツール: ドラッグ確定 → 範囲を一括ペイント
    if (isV2Doc(doc) && rectDrag) {
        const d = doc;
        const x0 = Math.min(rectDrag.ax, rectDrag.cx);
        const x1 = Math.max(rectDrag.ax, rectDrag.cx);
        const y0 = Math.min(rectDrag.ay, rectDrag.cy);
        const y1 = Math.max(rectDrag.ay, rectDrag.cy);
        for (let y = y0; y <= y1; y++) {
            for (let x = x0; x <= x1; x++) paintCellV2(d, x, y, selectedTile);
        }
        renderer.clearRectPreview();
        renderer.flush();
        rectDrag = null;
    }
    const patch: Patch = {};
    const cells = [...stroke.cells.values()].filter((c) => c.before !== c.after);
    if (cells.length > 0) {
        patch.cells = cells;
        if (isV2Doc(doc)) patch.layer = activeLayer === "upper" ? "upper" : "ground";
    }
    if (stroke.decorBefore && JSON.stringify(stroke.decorBefore) !== JSON.stringify(doc.decor)) {
        patch.decorBefore = stroke.decorBefore;
        patch.decorAfter = doc.decor.map((d) => ({ ...d }));
    }
    if (stroke.spawnBefore && (stroke.spawnBefore.x !== doc.spawn.x || stroke.spawnBefore.y !== doc.spawn.y)) {
        patch.spawnBefore = stroke.spawnBefore;
        patch.spawnAfter = { ...doc.spawn };
    }
    stroke = null;
    if (patch.cells || patch.decorBefore || patch.spawnBefore) {
        history.push(patch);
        refreshUndoButtons();
        scheduleSave();
        updateStatus();
    }
}

function onStrokeCancel(): void {
    if (!stroke) return;
    if (rectDrag) {
        renderer.clearRectPreview();
        rectDrag = null;
    }
    for (const c of stroke.cells.values()) {
        if (isV2Doc(doc)) {
            activeLayerArr(doc)[c.i] = c.before as number;
        } else {
            doc.grid[c.i] = c.before as string;
        }
        renderer.cellChanged(c.i % doc.width, Math.floor(c.i / doc.width));
    }
    if (stroke.decorBefore) {
        doc.decor = stroke.decorBefore;
        renderer.rebuildDecor();
    }
    if (stroke.spawnBefore) {
        doc.spawn = stroke.spawnBefore;
        renderer.updateSpawn();
    }
    renderer.flush();
    stroke = null;
}

// ---------- Undo / Redo ----------

function rerenderPatch(p: Patch): void {
    if (p.cells) {
        for (const c of p.cells) renderer.cellChanged(c.i % doc.width, Math.floor(c.i / doc.width));
        renderer.flush();
    }
    if (p.decorBefore) renderer.rebuildDecor();
    if (p.spawnBefore) renderer.updateSpawn();
    refreshUndoButtons();
    updateStatus();
    scheduleSave();
}

function doUndo(): void {
    const p = history.undo();
    if (!p) return;
    applyPatch(doc, p, "undo");
    rerenderPatch(p);
}

function doRedo(): void {
    const p = history.redo();
    if (!p) return;
    applyPatch(doc, p, "redo");
    rerenderPatch(p);
}

// ---------- ドキュメント切替 ----------

function setDocument(next: AnyDoc): void {
    doc = next;
    history.clear();
    stroke = null;
    rectDrag = null;
    renderer.clearRectPreview();
    if (isV2Doc(doc)) {
        activeLayer = "ground";
        selectedTile = Math.min(selectedTile, tileCount(doc.tileset) - 1);
        setActiveLayer("ground");
        setToolV2(toolV2);
    }
    renderer.setDoc(doc);
    renderer.fitView();
    $<HTMLInputElement>("map-name").value = doc.name;
    $<HTMLInputElement>("map-author").value = doc.author;
    refreshModeUi();
    refreshUndoButtons();
    updateStatus();
    scheduleSave();
}

// ---------- タイルセット PNG インポート ----------

function bytesToBase64(bytes: Uint8Array): string {
    let bin = "";
    const CHUNK = 0x8000;
    for (let i = 0; i < bytes.length; i += CHUNK) {
        bin += String.fromCharCode(...bytes.subarray(i, i + CHUNK));
    }
    return btoa(bin);
}

type TilesetImport = { ok: true; tileset: TilesetDoc; pngWidth: number; pngHeight: number } | { ok: false; error: string };

/** PNG ファイル + tileSize → 検証済み TilesetDoc (tiles は全デフォルト) */
async function importTilesetPng(file: File, tileSize: number): Promise<TilesetImport> {
    if (file.size > MAX_TILESET_IMAGE_BYTES) {
        return { ok: false, error: `PNG が 1 MB を超えています (${(file.size / 1024).toFixed(1)} KB)` };
    }
    if (!Number.isInteger(tileSize) || tileSize < TILESIZE_MIN || tileSize > TILESIZE_MAX) {
        return { ok: false, error: `タイルサイズは ${TILESIZE_MIN}〜${TILESIZE_MAX} の整数が必要です` };
    }
    const uri = PNG_DATA_URI_PREFIX + bytesToBase64(new Uint8Array(await file.arrayBuffer()));
    const png = parsePngDataUri(uri);
    if (!png.ok) return { ok: false, error: png.error };
    if (png.width % tileSize !== 0) {
        return { ok: false, error: `画像幅 (${png.width}px) がタイルサイズ (${tileSize}px) の倍数ではありません` };
    }
    const errors: string[] = [];
    const ts = validateTileset({ tileSize, columns: png.width / tileSize, image: uri }, errors);
    if (!ts) return { ok: false, error: errors[0] ?? "タイルセットを検証できません" };
    return { ok: true, tileset: ts, pngWidth: png.width, pngHeight: png.height };
}

// ---------- タイルセット設定パネル (ウディタ流) ----------

type TsMode = "pass" | "over" | "light" | "tag";
let tsMode: TsMode = "pass";
const TS_PX = 44;

const TS_HINTS: Record<TsMode, string> = {
    pass: "クリックで ○ (通行可) → × (不可) → ↓ (下層に従う) を巡回します。↓ は上層チップでのみ意味を持ちます。",
    over: "クリックで ★ (常にプレイヤーより上に描画) を切り替えます。屋根・梁向け。",
    light: "クリックで 遮 (視界・影を遮る) を切り替えます。通行とは独立です。",
    tag: `クリックで +1 / 右クリックで -1 (0〜${TAG_MAX})。ランタイムは無視する汎用タグです。`,
};

function drawTilesetPanel(): void {
    if (!isV2Doc(doc)) return;
    const d = doc;
    const ts = d.tileset;
    const cv = $<HTMLCanvasElement>("ts-canvas");
    cv.width = ts.columns * TS_PX;
    cv.height = ts.rows * TS_PX;
    cv.style.width = `${cv.width}px`;
    cv.style.height = `${cv.height}px`;
    const ctx = cv.getContext("2d");
    if (!ctx) return;
    ctx.imageSmoothingEnabled = false;
    ctx.fillStyle = "#14141a";
    ctx.fillRect(0, 0, cv.width, cv.height);

    const drawOverlays = (img: HTMLImageElement | null): void => {
        if (img) ctx.drawImage(img, 0, 0, ts.columns * ts.tileSize, ts.rows * ts.tileSize, 0, 0, cv.width, cv.height);
        ctx.textAlign = "center";
        ctx.textBaseline = "middle";
        for (let id = 0; id < ts.tiles.length; id++) {
            const t = ts.tiles[id];
            const px = (id % ts.columns) * TS_PX;
            const py = Math.floor(id / ts.columns) * TS_PX;
            // セル枠
            ctx.strokeStyle = "rgba(255,255,255,0.18)";
            ctx.lineWidth = 1;
            ctx.strokeRect(px + 0.5, py + 0.5, TS_PX - 1, TS_PX - 1);
            // 記号オーバーレイ (視認性のため下に影丸)
            let label = "";
            let color = "#ffffff";
            if (tsMode === "pass") {
                label = t.pass === "o" ? "○" : t.pass === "x" ? "×" : "↓";
                color = t.pass === "o" ? "#60e68c" : t.pass === "x" ? "#ff6060" : "#7ec8ff";
            } else if (tsMode === "over") {
                label = t.over ? "★" : "−";
                color = t.over ? "#ffd75e" : "rgba(255,255,255,0.35)";
            } else if (tsMode === "light") {
                label = t.light ? "遮" : "−";
                color = t.light ? "#ff9d5e" : "rgba(255,255,255,0.35)";
            } else {
                label = String(t.tag);
                color = t.tag > 0 ? "#e8e8ee" : "rgba(255,255,255,0.35)";
            }
            ctx.fillStyle = "rgba(0,0,0,0.55)";
            ctx.beginPath();
            ctx.arc(px + TS_PX / 2, py + TS_PX / 2, TS_PX * 0.3, 0, Math.PI * 2);
            ctx.fill();
            ctx.fillStyle = color;
            ctx.font = `bold ${Math.floor(TS_PX * 0.42)}px sans-serif`;
            ctx.fillText(label, px + TS_PX / 2, py + TS_PX / 2 + 1);
        }
    };

    drawOverlays(null);
    void loadTilesetImage(ts.image)
        .then((img) => {
            if (doc !== d || $<HTMLDialogElement>("dlg-tileset").open === false) return;
            ctx.clearRect(0, 0, cv.width, cv.height);
            ctx.fillStyle = "#14141a";
            ctx.fillRect(0, 0, cv.width, cv.height);
            drawOverlays(img);
        })
        .catch(() => {});
}

const PASS_CYCLE: Record<PassValue, PassValue> = { o: "x", x: "v", v: "o" };

function cycleTileAttr(id: number, dir: 1 | -1): void {
    if (!isV2Doc(doc)) return;
    const t = doc.tileset.tiles[id];
    if (!t) return;
    if (tsMode === "pass") t.pass = PASS_CYCLE[t.pass];
    else if (tsMode === "over") t.over = !t.over;
    else if (tsMode === "light") t.light = !t.light;
    else t.tag = (t.tag + dir + (TAG_MAX + 1)) % (TAG_MAX + 1);
    drawTilesetPanel();
    renderer.redrawAll(); // ★バッジ / 通行オーバーレイに反映
    renderer.flush();
    scheduleSave();
}

function tsCanvasCellId(e: MouseEvent): number | null {
    if (!isV2Doc(doc)) return null;
    const cv = $<HTMLCanvasElement>("ts-canvas");
    const r = cv.getBoundingClientRect();
    const x = Math.floor((e.clientX - r.left) / TS_PX);
    const y = Math.floor((e.clientY - r.top) / TS_PX);
    if (x < 0 || y < 0 || x >= doc.tileset.columns || y >= doc.tileset.rows) return null;
    return y * doc.tileset.columns + x;
}

/** タイルセット画像の差し替え。属性は id ベースで維持、範囲外になったレイヤー値は -1 に */
function replaceTileset(ts: TilesetDoc): void {
    if (!isV2Doc(doc)) return;
    const d = doc;
    const old = d.tileset;
    const keep = Math.min(old.tiles.length, ts.tiles.length);
    for (let i = 0; i < keep; i++) ts.tiles[i] = { ...old.tiles[i] };
    d.tileset = ts;
    const count = tileCount(ts);
    let cleared = 0;
    for (const arr of [d.ground, d.upper]) {
        for (let i = 0; i < arr.length; i++) {
            if (arr[i] >= count) {
                arr[i] = -1;
                cleared++;
            }
        }
    }
    selectedTile = Math.min(selectedTile, count - 1);
    history.clear(); // 旧 tilecount 前提のパッチは安全に適用できないため破棄
    refreshUndoButtons();
    renderer.setDoc(d);
    void rebuildPicker();
    drawTilesetPanel();
    scheduleSave();
    updateStatus();
    toast(cleared > 0 ? `差し替えました (範囲外になった ${cleared} セルを空にしました)` : "タイルセットを差し替えました");
}

// ---------- 入出力 (検証は仕様 §9/§16。エクスポート/コード生成前に必ず実施) ----------

function loadFromJsonText(text: string): boolean {
    const bytes = new TextEncoder().encode(text).length;
    let value: unknown;
    try {
        value = JSON.parse(text);
    } catch (e) {
        showMessages("読込エラー", [`JSON として解析できません: ${(e as Error).message}`], []);
        return false;
    }
    const r = validateEkmapAny(value, bytes);
    if (!r.ok) {
        showMessages("検証エラーのため読み込めません", r.errors, []);
        return false;
    }
    setDocument(r.doc);
    if (r.warnings.length > 0) showMessages("警告 (読込は続行しました)", [], r.warnings);
    else toast("読み込みました");
    return true;
}

/** 現在のドキュメントを検証して JSON テキスト化。失敗時はエラー表示して null */
function buildValidatedJson(pretty: boolean): string | null {
    const json = docToJsonAny(doc);
    const text = pretty ? JSON.stringify(json, null, 2) : JSON.stringify(json);
    const bytes = new TextEncoder().encode(text).length;
    const r = validateEkmapAny(json, bytes);
    if (!r.ok) {
        showMessages(`検証エラー (仕様 §${isV2Doc(doc) ? "16" : "9"}) — 修正してから出力してください`, r.errors, []);
        return null;
    }
    return text;
}

function sanitizeFileName(name: string): string {
    const s = name.replace(/[\\/:*?"<>|]/g, "_").trim();
    return s.length > 0 ? s : "map";
}

function exportFile(): void {
    const text = buildValidatedJson(true);
    if (text === null) return;
    const blob = new Blob([text], { type: "application/json" });
    const a = document.createElement("a");
    a.href = URL.createObjectURL(blob);
    a.download = `${sanitizeFileName(doc.name)}.ekmap.json`;
    a.click();
    window.setTimeout(() => URL.revokeObjectURL(a.href), 1000);
    toast("エクスポートしました (.ekmap.json)");
}

function formatKb(n: number): string {
    return n >= 1024 * 1024 ? `${(n / 1024 / 1024).toFixed(2)} MB` : `${(n / 1024).toFixed(1)} KB`;
}

async function copyMapCode(): Promise<void> {
    const text = buildValidatedJson(false); // §8: コードは minify
    if (text === null) return;
    const code = encodeMapCode(text);
    const sizeTxt = formatKb(code.length);
    try {
        await navigator.clipboard.writeText(code);
        if (code.length > MAX_JSON_BYTES) {
            // v2 のタイルセット内蔵で巨大化するケース (仕様 §17: コピー時にサイズ警告)
            showMessages("マップコードをコピーしました", [], [`コードが 512 KB を超えています (${sizeTxt})。チャット等に貼るには大きすぎる可能性があります。ファイル (.ekmap.json) での共有を推奨します。`]);
        } else {
            toast(`マップコードをコピーしました (${sizeTxt})`);
        }
    } catch {
        openCodeDialog("show", code); // クリップボード不可 → 手動コピー用に表示
    }
}

// ---------- ダイアログ ----------

function clampInt(text: string, min: number, max: number, fallback: number): number {
    const v = Math.floor(Number(text));
    if (!Number.isFinite(v)) return fallback;
    return Math.min(max, Math.max(min, v));
}

function openCodeDialog(mode: "load" | "show", code = ""): void {
    const ta = $<HTMLTextAreaElement>("code-text");
    $("code-title").textContent = mode === "load" ? "マップコードを貼り付けて読込" : "マップコード (選択してコピーしてください)";
    $<HTMLButtonElement>("code-ok").hidden = mode === "show";
    ta.value = code;
    $<HTMLDialogElement>("dlg-code").showModal();
    if (mode === "show") ta.select();
    else ta.focus();
}

/** 新規ダイアログで選択中の PNG (作成確定までの保持) */
let pendingPng: File | null = null;

function refreshNewTsInfo(): void {
    const info = $("new-ts-info");
    if (!pendingPng) {
        info.textContent = "未選択 (PNG ≤ 1 MB)";
        return;
    }
    info.textContent = `${pendingPng.name} (${(pendingPng.size / 1024).toFixed(1)} KB)`;
}

function wireUi(): void {
    // v1 ツールパレット
    const toolButtons = [...document.querySelectorAll<HTMLButtonElement>("#tools .tool")];
    const selectTool = (b: HTMLButtonElement): void => {
        tool = b.dataset.tool as ToolV1;
        for (const x of toolButtons) x.classList.toggle("active", x === b);
    };
    for (const b of toolButtons) b.addEventListener("click", () => selectTool(b));
    const def = toolButtons.find((b) => b.dataset.tool === tool);
    if (def) selectTool(def);

    // v2 レイヤータブ + ツール
    for (const b of document.querySelectorAll<HTMLButtonElement>("#layer-tabs .ltab")) {
        b.addEventListener("click", () => setActiveLayer(b.dataset.layer as LayerTab));
    }
    for (const b of document.querySelectorAll<HTMLButtonElement>("#tools-v2 .tool2")) {
        b.addEventListener("click", () => setToolV2(b.dataset.tool2 as ToolV2));
    }
    setToolV2(toolV2);
    const decorButtons = [...document.querySelectorAll<HTMLButtonElement>("#tools-decor2 .tool2d")];
    const selectDecor2 = (b: HTMLButtonElement): void => {
        decorKind2 = b.dataset.decor as DecorKind;
        for (const x of decorButtons) x.classList.toggle("active", x === b);
    };
    for (const b of decorButtons) b.addEventListener("click", () => selectDecor2(b));
    const defDecor = decorButtons.find((b) => b.dataset.decor === decorKind2);
    if (defDecor) selectDecor2(defDecor);

    // タイルピッカー
    $<HTMLCanvasElement>("picker-canvas").addEventListener("click", (e) => {
        if (!isV2Doc(doc)) return;
        const cv = $<HTMLCanvasElement>("picker-canvas");
        const r = cv.getBoundingClientRect();
        const x = Math.floor((e.clientX - r.left) / PICK_PX);
        const y = Math.floor((e.clientY - r.top) / PICK_PX);
        if (x < 0 || y < 0 || x >= doc.tileset.columns || y >= doc.tileset.rows) return;
        selectTile(y * doc.tileset.columns + x);
        if (toolV2 === "erase" || toolV2 === "pick") setToolV2("pen");
    });

    // タイルセット設定パネル
    const dlgTs = $<HTMLDialogElement>("dlg-tileset");
    $("btn-tileset").addEventListener("click", () => {
        if (!isV2Doc(doc)) return;
        $("ts-hint").textContent = TS_HINTS[tsMode];
        dlgTs.showModal();
        drawTilesetPanel();
    });
    $("ts-close").addEventListener("click", () => {
        dlgTs.close();
        void rebuildPicker();
    });
    for (const b of document.querySelectorAll<HTMLButtonElement>("#ts-modes .ts-mode")) {
        b.addEventListener("click", () => {
            tsMode = b.dataset.mode as TsMode;
            for (const x of document.querySelectorAll<HTMLButtonElement>("#ts-modes .ts-mode")) {
                x.classList.toggle("active", x === b);
            }
            $("ts-hint").textContent = TS_HINTS[tsMode];
            drawTilesetPanel();
        });
    }
    const tsCanvas = $<HTMLCanvasElement>("ts-canvas");
    tsCanvas.addEventListener("click", (e) => {
        const id = tsCanvasCellId(e);
        if (id !== null) cycleTileAttr(id, 1);
    });
    tsCanvas.addEventListener("contextmenu", (e) => {
        e.preventDefault();
        const id = tsCanvasCellId(e);
        if (id !== null) cycleTileAttr(id, -1);
    });

    // タイルセット画像差し替え (既存 v2 ドキュメント、tileSize は現状維持)
    const pngInput = $<HTMLInputElement>("png-input");
    let pngTarget: "new" | "replace" = "new";
    $("ts-replace").addEventListener("click", () => {
        pngTarget = "replace";
        pngInput.click();
    });
    $("new-ts-choose").addEventListener("click", () => {
        pngTarget = "new";
        pngInput.click();
    });
    pngInput.addEventListener("change", async () => {
        const f = pngInput.files?.[0];
        pngInput.value = "";
        if (!f) return;
        if (pngTarget === "new") {
            pendingPng = f;
            refreshNewTsInfo();
            return;
        }
        if (!isV2Doc(doc)) return;
        const r = await importTilesetPng(f, doc.tileset.tileSize);
        if (!r.ok) {
            showMessages("タイルセットを差し替えできません", [r.error], []);
            return;
        }
        replaceTileset(r.tileset);
    });

    // ヘッダ入力
    $<HTMLInputElement>("map-name").addEventListener("input", (e) => {
        doc.name = (e.target as HTMLInputElement).value.slice(0, NAME_MAX);
        scheduleSave();
    });
    $<HTMLInputElement>("map-author").addEventListener("input", (e) => {
        doc.author = (e.target as HTMLInputElement).value.slice(0, AUTHOR_MAX);
        scheduleSave();
    });

    // 新規
    const dlgNew = $<HTMLDialogElement>("dlg-new");
    const v2Section = $("new-v2-section");
    const typeRadios = [...document.querySelectorAll<HTMLInputElement>("input[name=new-type]")];
    const refreshTypeUi = (): void => {
        const v2 = typeRadios.find((r) => r.checked)?.value === "v2";
        v2Section.hidden = !v2;
    };
    for (const r of typeRadios) r.addEventListener("change", refreshTypeUi);
    $("btn-new").addEventListener("click", () => {
        $<HTMLInputElement>("new-name").value = "新しいマップ";
        $<HTMLInputElement>("new-author").value = doc.author;
        pendingPng = null;
        refreshNewTsInfo();
        refreshTypeUi();
        dlgNew.showModal();
    });
    dlgNew.addEventListener("close", () => {
        if (dlgNew.returnValue !== "ok") return;
        const w = clampInt($<HTMLInputElement>("new-w").value, MIN_DIM, MAX_DIM, 32);
        const h = clampInt($<HTMLInputElement>("new-h").value, MIN_DIM, MAX_DIM, 32);
        const name = ($<HTMLInputElement>("new-name").value.trim() || "新しいマップ").slice(0, NAME_MAX);
        const author = $<HTMLInputElement>("new-author").value.slice(0, AUTHOR_MAX);
        const isV2 = typeRadios.find((r) => r.checked)?.value === "v2";
        if (!isV2) {
            void backupAutosave();
            setDocument(createNewDoc(w, h, name, author));
            toast(`${w}×${h} の新規マップを作成しました (全セル奈落)`);
            return;
        }
        if (!pendingPng) {
            showMessages("新規マップ (v2) を作成できません", ["タイルセット PNG が未選択です"], []);
            return;
        }
        const tileSize = clampInt($<HTMLInputElement>("new-ts-size").value, TILESIZE_MIN, TILESIZE_MAX, TILESIZE_DEFAULT);
        void importTilesetPng(pendingPng, tileSize).then((r) => {
            if (!r.ok) {
                showMessages("新規マップ (v2) を作成できません", [r.error], []);
                return;
            }
            selectedTile = 0;
            void backupAutosave();
            setDocument(createNewDocV2(w, h, name, author, r.tileset));
            toast(`${w}×${h} の v2 マップを作成しました (チップ ${tileCount(r.tileset)} 個)`);
        });
    });

    // ファイル入出力
    const fileInput = $<HTMLInputElement>("file-input");
    $("btn-import").addEventListener("click", () => fileInput.click());
    fileInput.addEventListener("change", async () => {
        const f = fileInput.files?.[0];
        fileInput.value = "";
        if (!f) return;
        if (f.size > MAX_JSON_BYTES_V2) {
            showMessages("読込エラー", [`JSON が 4 MB を超えています (${formatKb(f.size)})`], []);
            return;
        }
        await backupAutosave();
        loadFromJsonText(await f.text());
    });
    $("btn-export").addEventListener("click", exportFile);

    // マップコード
    $("btn-copy-code").addEventListener("click", () => void copyMapCode());
    $("btn-load-code").addEventListener("click", () => openCodeDialog("load"));
    const dlgCode = $<HTMLDialogElement>("dlg-code");
    $("code-cancel").addEventListener("click", () => dlgCode.close());
    $("code-ok").addEventListener("click", () => {
        const codeText = $<HTMLTextAreaElement>("code-text").value;
        let jsonText: string;
        try {
            jsonText = decodeMapCode(codeText);
        } catch (e) {
            showMessages("コード読込エラー", [(e as Error).message], []);
            return;
        }
        void backupAutosave();
        if (loadFromJsonText(jsonText)) dlgCode.close();
    });

    $("msg-close").addEventListener("click", () => $<HTMLDialogElement>("dlg-msg").close());

    // 操作列
    $("btn-undo").addEventListener("click", doUndo);
    $("btn-redo").addEventListener("click", doRedo);
    const ovlBtn = $<HTMLButtonElement>("btn-overlay");
    ovlBtn.addEventListener("click", () => {
        renderer.setOverlay(!renderer.overlay);
        ovlBtn.classList.toggle("active", renderer.overlay);
    });
    $("btn-fit").addEventListener("click", () => {
        renderer.fitView();
        updateStatus();
    });
    const drawerBtn = $<HTMLButtonElement>("btn-drawer");
    drawerBtn.addEventListener("click", () => {
        const collapsed = $("tools").classList.toggle("collapsed");
        $("tools-v2").classList.toggle("collapsed", collapsed);
        $("tools-decor2").classList.toggle("collapsed", collapsed);
        drawerBtn.textContent = collapsed ? "▴" : "▾";
    });

    // キーボード: Ctrl+Z / Ctrl+Y / Ctrl+Shift+Z
    window.addEventListener("keydown", (e) => {
        const tag = (e.target as HTMLElement | null)?.tagName ?? "";
        if (tag === "INPUT" || tag === "TEXTAREA") return;
        if (!(e.ctrlKey || e.metaKey) || e.altKey) return;
        const k = e.key.toLowerCase();
        if (k === "z") {
            e.preventDefault();
            if (e.shiftKey) doRedo();
            else doUndo();
        } else if (k === "y") {
            e.preventDefault();
            doRedo();
        }
    });

    // 終了時に保留中の自動保存を投げる (ベストエフォート)
    window.addEventListener("beforeunload", () => {
        if (savePending) void saveAutosave(docToJsonAny(doc));
    });
}

// ---------- 起動 ----------

async function boot(): Promise<void> {
    await renderer.init($("viewport"));

    wireUi();

    const saved = await loadAutosave();
    const restored = saved === undefined || saved === null ? null : tryRestoreDoc(saved);
    setDocument(restored ?? createNewDoc(32, 32, "新しいマップ", ""));
    if (restored) toast("自動保存から復元しました");

    new InputController(renderer.app.canvas, renderer.world, {
        strokeStart: onStrokeStart,
        strokeStep: onStrokeStep,
        strokeEnd: onStrokeEnd,
        strokeCancel: onStrokeCancel,
        hover: (fx, fy) => {
            lastHover = { x: Math.floor(fx), y: Math.floor(fy) };
            renderer.setHover(lastHover.x, lastHover.y);
            updateStatus();
        },
        viewChanged: updateStatus,
    });

    updateStatus();
}

void boot();
