// エディタ本体: ツール/ストローク/Undo/Redo/入出力/自動保存の配線
// v1 (Backrooms チップ) と v3 (カスタムタイルセット + 4 レイヤー) の両モードを扱う
// ランタイム doc は MapDoc (ekm:1) | MapDocV3 (ekm:3) のみ — v2 は validateEkmapAny で自動昇格

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
    MAX_TILECOUNT,
    MIN_DIM,
    type MapDocV3,
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
    createNewDocV3,
    defaultTileAttr,
    docToJsonAny,
    isV3Doc,
    tileCount,
} from "./model";
import {
    type SliceCandidate,
    type SliceParams,
    appendTileToAtlas,
    detectTransparentTiles,
    enumerateCandidates,
    rebakeAtlas,
    resizeNearestNeighbor,
    scoreCandidates,
} from "./tileset-import";
import { validateEkmapAny, validateTileset } from "./validate";
import { PNG_DATA_URI_PREFIX, parsePngDataUri } from "./png";
import { decodeMapCode, encodeMapCode } from "./mapcode";
import { type CellChange, History, type Patch, applyPatch } from "./history";
import { backupAutosave, loadAutosave, saveAutosave, tryRestoreDoc } from "./persist";
import { MapRenderer, loadTilesetImage } from "./render";
import { InputController } from "./input";
import { saveForPlaytest } from "./playtest";

type ToolV1 = "floor" | "wall" | "void" | DecorKind | "spawn";
type ToolV2 = "pen" | "erase" | "rect" | "bucket" | "pick";
type LayerTab = "ground" | "upper" | "decor" | "spawn";

const TOOL_CHAR: Partial<Record<ToolV1, string>> = { floor: CELL_FLOOR, wall: CELL_WALL, void: CELL_VOID };

// ---------- ツール説明リボン ----------

/** v2 ツールの一言説明 */
const TOOL_HINTS_V2: Record<ToolV2, string> = {
    pen:    "ペン — タイルを描きます。クリック / ドラッグで塗れます",
    erase:  "消去 — タイルを消します。ドラッグでまとめて消せます",
    rect:   "矩形 — ドラッグで四角く塗ります",
    bucket: "バケツ — つながった範囲をまとめて塗ります",
    pick:   "スポイト — タイルを吸い取って選びます。吸ったあとはペンに切り替わります",
};

/** v2 レイヤーのツールバー説明 */
const LAYER_HINTS_V2: Record<LayerTab, string> = {
    ground: "下層 — マップの土台となるタイルを配置します",
    upper:  "上層 — プレイヤーの上に描画するタイル (屋根・梁など) を配置します",
    decor:  "装飾 — 灯りやドアなど小物を1マスずつ置きます。クリックで配置 / 再クリックで除去",
    spawn:  "スポーン — プレイヤーの初期位置を設定します。クリック / ドラッグで移動できます",
};

/** v1 ツールの一言説明 */
const TOOL_HINTS_V1: Record<ToolV1, string> = {
    wall:    "壁 — 通行不可の壁を置きます",
    floor:   "床 — 通行可能な床を置きます",
    void:    "奈落 — 何もない空間 (落下) にします",
    spawn:   "スポーン — プレイヤーの出現位置を設定します",
    light:   "灯り — 光源を置きます",
    stain:   "シミ — 床のシミ装飾を置きます",
    door:    "扉 — 扉を置きます",
    vent:    "ベント — 通気口を置きます",
    ceiling: "天井 — 天井を置きます",
};

function updateRibbon(): void {
    const el = $("tool-ribbon");
    const v3 = isV3Doc(doc);
    if (!v3) {
        el.textContent = TOOL_HINTS_V1[tool] ?? "";
        return;
    }
    // v3: レイヤー説明 + ツール説明
    const layerHint = LAYER_HINTS_V2[activeLayer];
    if (activeLayer === "decor" || activeLayer === "spawn") {
        el.textContent = layerHint;
    } else {
        el.textContent = `${layerHint}  /  ${TOOL_HINTS_V2[toolV2]}`;
    }
}

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
    const v2Txt = isV3Doc(doc) ? ` | ${layerLabel(activeLayer)} | チップ ${selectedTile}` : "";
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
    const v3 = isV3Doc(doc);
    $("layer-tabs").hidden = !v3; // 旧フッタ横タブ (CSS で常時非表示だが状態は維持)
    $("layer-vtabs").hidden = !v3; // 新縦タブ: v3 のみ表示
    $("tools").hidden = v3;
    $("tools-v2").hidden = !v3 || (activeLayer !== "ground" && activeLayer !== "upper");
    $("tools-decor2").hidden = !v3 || activeLayer !== "decor";
    $("spawn-hint").hidden = !v3 || activeLayer !== "spawn";
    updateRibbon();
    if (v3) void rebuildPicker();
}

function setActiveLayer(l: LayerTab): void {
    activeLayer = l;
    // 旧フッタ横タブ (CSS で非表示だが active 状態は維持)
    for (const b of document.querySelectorAll<HTMLButtonElement>("#layer-tabs .ltab")) {
        b.classList.toggle("active", b.dataset.layer === l);
    }
    // 縦タブの active 表示を同期
    for (const b of document.querySelectorAll<HTMLButtonElement>("#layer-vtabs .lvtab")) {
        b.classList.toggle("active", b.dataset.layer === l);
    }
    refreshModeUi();
    updateRibbon();
    updateStatus();
}

function setToolV2(t: ToolV2): void {
    toolV2 = t;
    for (const b of document.querySelectorAll<HTMLButtonElement>("#tools-v2 .tool2")) {
        b.classList.toggle("active", b.dataset.tool2 === t);
    }
    updateRibbon();
}

// ---------- パレット (v2 チップ選択) ----------
// ・コンパクト帯 (#palette-strip / strip-canvas): 常時表示。選択中+近傍チップを横一列
// ・全画面グリッド (#palette-overlay / palette-canvas): ▲▲ で開く、選択で閉じる

/** 帯の1チップ表示サイズ (px) */
const STRIP_PX = 44;
/** 全画面グリッドの1チップ表示サイズ (px) */
const GRID_PX = 56;
/** 帯に表示する近傍チップ数 (選択中を中心とした前後合計) */
const STRIP_NEIGHBORS = 7;

/** 帯の canvas に選択中チップ + 近傍 STRIP_NEIGHBORS 個を描画 */
async function rebuildPicker(): Promise<void> {
    if (!isV3Doc(doc)) return;
    const d = doc;
    const ts = activeTileset(d);
    const total = tileCount(ts);

    // 表示するチップid列: selectedTile を中心に前後 STRIP_NEIGHBORS/2 ずつ
    const half = Math.floor(STRIP_NEIGHBORS / 2);
    const start = Math.max(0, Math.min(selectedTile - half, total - STRIP_NEIGHBORS));
    const end = Math.min(total - 1, start + STRIP_NEIGHBORS - 1);
    const ids: number[] = [];
    for (let i = start; i <= end; i++) ids.push(i);

    const cv = $<HTMLCanvasElement>("strip-canvas");
    cv.width = ids.length * STRIP_PX;
    cv.height = STRIP_PX;
    cv.style.width = `${cv.width}px`;
    cv.style.height = `${cv.height}px`;

    const ctx = cv.getContext("2d");
    if (!ctx) return;
    ctx.imageSmoothingEnabled = false;
    ctx.fillStyle = "#14141a";
    ctx.fillRect(0, 0, cv.width, cv.height);

    try {
        const img = await loadTilesetImage(ts.image);
        if (doc !== d) return;
        for (let idx = 0; idx < ids.length; idx++) {
            const id = ids[idx];
            const srcX = (id % ts.columns) * ts.tileSize;
            const srcY = Math.floor(id / ts.columns) * ts.tileSize;
            ctx.drawImage(img, srcX, srcY, ts.tileSize, ts.tileSize, idx * STRIP_PX, 0, STRIP_PX, STRIP_PX);
        }
    } catch {
        /* 画像破損時はプレースホルダのまま */
    }

    // 選択中チップの強調枠
    const selIdx = ids.indexOf(selectedTile);
    if (selIdx >= 0) {
        ctx.strokeStyle = "#ffd75e";
        ctx.lineWidth = 3;
        ctx.strokeRect(selIdx * STRIP_PX + 1.5, 1.5, STRIP_PX - 3, STRIP_PX - 3);
    }

    // 各チップ番号ラベル (小さく左上)
    ctx.font = "10px sans-serif";
    ctx.textBaseline = "top";
    for (let idx = 0; idx < ids.length; idx++) {
        const id = ids[idx];
        ctx.fillStyle = "rgba(0,0,0,0.55)";
        ctx.fillRect(idx * STRIP_PX + 1, 1, 22, 13);
        ctx.fillStyle = id === selectedTile ? "#ffd75e" : "rgba(255,255,255,0.7)";
        ctx.fillText(String(id), idx * STRIP_PX + 3, 2);
    }

    // 帯のクリックハンドラに id 列を伝えるため data 属性に保存
    cv.dataset.stripIds = ids.join(",");
}

/** 全画面グリッドの canvas を描画。filter が空でないとき id 文字列でマッチしたものだけ表示 */
async function rebuildPaletteGrid(filterText: string, usedOnly: boolean): Promise<void> {
    if (!isV3Doc(doc)) return;
    const d = doc;
    const ts = activeTileset(d);
    const total = tileCount(ts);

    // 使用中セル id セット (usedOnly のとき) — アクティブレイヤーのみ走査
    let usedIds: Set<number> | null = null;
    if (usedOnly) {
        usedIds = new Set<number>();
        for (const layer of d.layers) {
            for (const v of layer.cells) if (v >= 0) usedIds.add(v);
        }
    }

    // フィルタ適用: 番号が filterText を含むものだけ
    const norm = filterText.trim();
    const filtered: number[] = [];
    for (let id = 0; id < total; id++) {
        if (norm && !String(id).includes(norm)) continue;
        if (usedIds && !usedIds.has(id)) continue;
        filtered.push(id);
    }

    // グリッド列数: ウィンドウ幅に合わせる
    const wrap = $("palette-grid-wrap");
    const wrapW = wrap.clientWidth || window.innerWidth;
    const cols = Math.max(1, Math.floor(wrapW / GRID_PX));
    const rows = Math.ceil(filtered.length / cols) || 1;

    const cv = $<HTMLCanvasElement>("palette-canvas");
    cv.width = cols * GRID_PX;
    cv.height = rows * GRID_PX;
    cv.style.width = `${cv.width}px`;
    cv.style.height = `${cv.height}px`;
    // フィルタ結果を data 属性に保存 (クリック → id 変換用)
    cv.dataset.filteredIds = filtered.join(",");
    cv.dataset.gridCols = String(cols);

    const ctx = cv.getContext("2d");
    if (!ctx) return;
    ctx.imageSmoothingEnabled = false;
    ctx.fillStyle = "#14141a";
    ctx.fillRect(0, 0, cv.width, cv.height);

    let img: HTMLImageElement | null = null;
    try {
        img = await loadTilesetImage(ts.image);
        if (doc !== d) return;
    } catch {
        /* 画像破損時もラベルだけ出す */
    }

    for (let idx = 0; idx < filtered.length; idx++) {
        const id = filtered[idx];
        const gx = (idx % cols) * GRID_PX;
        const gy = Math.floor(idx / cols) * GRID_PX;

        if (img) {
            const srcX = (id % ts.columns) * ts.tileSize;
            const srcY = Math.floor(id / ts.columns) * ts.tileSize;
            ctx.drawImage(img, srcX, srcY, ts.tileSize, ts.tileSize, gx, gy, GRID_PX, GRID_PX);
        }

        // セル枠
        ctx.strokeStyle = id === selectedTile ? "#ffd75e" : "rgba(255,255,255,0.18)";
        ctx.lineWidth = id === selectedTile ? 3 : 1;
        ctx.strokeRect(gx + 0.5, gy + 0.5, GRID_PX - 1, GRID_PX - 1);

        // 選択中は背景ハイライト
        if (id === selectedTile) {
            ctx.fillStyle = "rgba(255,215,94,0.18)";
            ctx.fillRect(gx + 1, gy + 1, GRID_PX - 2, GRID_PX - 2);
        }

        // 番号ラベル
        ctx.fillStyle = "rgba(0,0,0,0.6)";
        ctx.fillRect(gx + 1, gy + 1, 26, 14);
        ctx.font = "bold 11px sans-serif";
        ctx.textBaseline = "top";
        ctx.fillStyle = id === selectedTile ? "#ffd75e" : "rgba(255,255,255,0.8)";
        ctx.fillText(String(id), gx + 3, gy + 2);
    }

    // 何も無い場合はプレースホルダ
    if (filtered.length === 0) {
        ctx.fillStyle = "rgba(255,255,255,0.3)";
        ctx.font = "14px sans-serif";
        ctx.textAlign = "center";
        ctx.textBaseline = "middle";
        ctx.fillText("チップが見つかりません", cv.width / 2, cv.height / 2 || GRID_PX / 2);
    }
}

/** 全画面グリッドを開く */
function openPaletteOverlay(): void {
    const overlay = $("palette-overlay");
    overlay.hidden = false;
    $<HTMLInputElement>("palette-search").value = paletteFilterText;
    $<HTMLInputElement>("palette-used-only").checked = paletteUsedOnly;
    void rebuildPaletteGrid(paletteFilterText, paletteUsedOnly);
    // フォーカスを検索フィールドへ
    $<HTMLInputElement>("palette-search").focus();
}

/** 全画面グリッドを閉じる */
function closePaletteOverlay(): void {
    $("palette-overlay").hidden = true;
}

let paletteFilterText = "";
let paletteUsedOnly = false;

function selectTile(id: number): void {
    if (!isV3Doc(doc)) return;
    selectedTile = Math.min(Math.max(0, id), tileCount(activeTileset(doc)) - 1);
    void rebuildPicker();
    // 全画面が開いていれば即時に閉じる (帯の再描画は rebuildPicker が担う)
    const overlay = $("palette-overlay");
    if (!overlay.hidden) closePaletteOverlay();
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

/**
 * v3 アクティブレイヤーの cells 配列を返す。
 * UI は現状 "ground"(index 0) / "upper"(index 1) の 2 タブのみ — 4 タブ化は次増分。
 */
function activeLayerArrV3(d: MapDocV3): number[] {
    const idx = activeLayer === "upper" ? 1 : 0;
    return d.layers[idx].cells;
}

/** v3 アクティブレイヤーの index (0〜3) を返す。現状は 0/1 のみ */
function activeLayerIndexV3(): number {
    return activeLayer === "upper" ? 1 : 0;
}

/**
 * v3 アクティブレイヤーが使用するタイルセットを返す。
 * 現状は layers[activeLayerIndexV3()].tileset が指す tilesets[i]。
 * タイルセットが無い場合は tilesets[0] にフォールバック。
 */
function activeTileset(d: MapDocV3): TilesetDoc {
    const li = activeLayerIndexV3();
    const tsIdx = d.layers[li]?.tileset ?? 0;
    return d.tilesets[tsIdx] ?? d.tilesets[0];
}


/** v3 セルにタイル id (-1 = 空) を置く。stroke バッファに diff を積む */
function paintCellV3(d: MapDocV3, x: number, y: number, value: number): void {
    if (!stroke) return;
    const i = y * d.width + x;
    const arr = activeLayerArrV3(d);
    const before = arr[i];
    if (before === value) return;
    const ex = stroke.cells.get(i);
    if (ex) ex.after = value;
    else stroke.cells.set(i, { i, before, after: value });
    arr[i] = value;
    renderer.cellChanged(x, y);
    // 下層(layer 0)を空にしたセルは void 扱いなので decor も除去
    if (activeLayer === "ground" && value === -1) removeDecorAt(x, y);
}

function floodFillV3(d: MapDocV3, x: number, y: number, value: number): void {
    const arr = activeLayerArrV3(d);
    const target = arr[y * d.width + x];
    if (target === value) return;
    const stack: number[] = [y * d.width + x];
    while (stack.length > 0) {
        const i = stack.pop()!;
        if (arr[i] !== target) continue;
        const cx = i % d.width;
        const cy = Math.floor(i / d.width);
        paintCellV3(d, cx, cy, value);
        if (cx > 0) stack.push(i - 1);
        if (cx < d.width - 1) stack.push(i + 1);
        if (cy > 0) stack.push(i - d.width);
        if (cy < d.height - 1) stack.push(i + d.width);
    }
}

function clampCell(v: number, max: number): number {
    return Math.min(Math.max(0, v), max - 1);
}

/** v3 ツール適用 (現状は ground/upper の 2 レイヤーのみ UI 対応 — 4 タブ化は次増分) */
function applyToolAtV3(d: MapDocV3, x: number, y: number, isStart: boolean): void {
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
        if (!isStart || !inGrid) return;
        placeDecor(decorKind2, x, y);
        return;
    }

    switch (toolV2) {
        case "pen":
            if (inGrid) paintCellV3(d, x, y, selectedTile);
            break;
        case "erase":
            if (inGrid) paintCellV3(d, x, y, -1);
            break;
        case "rect": {
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
            if (isStart && inGrid) floodFillV3(d, x, y, selectedTile);
            break;
        case "pick": {
            if (!isStart || !inGrid) break;
            const i = y * d.width + x;
            const arr = activeLayerArrV3(d);
            // アクティブ層を優先、空ならもう一方の層から拾う
            const otherIdx = activeLayer === "upper" ? 0 : 1;
            const v = arr[i] >= 0 ? arr[i] : d.layers[otherIdx].cells[i];
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
    if (isV3Doc(doc)) {
        applyToolAtV3(doc, x, y, isStart);
        renderer.flush();
        updateStatus();
        return;
    }
    // v1 分岐: ここでは doc が MapDoc であることが保証される
    const docV1 = doc as import("./model").MapDoc;
    if (x < 0 || y < 0 || x >= docV1.width || y >= docV1.height) return;
    const ch = TOOL_CHAR[tool];
    if (ch !== undefined) {
        const i = y * docV1.width + x;
        const before = docV1.grid[i];
        if (before !== ch) {
            const ex = stroke.cells.get(i);
            if (ex) ex.after = ch;
            else stroke.cells.set(i, { i, before, after: ch });
            docV1.grid[i] = ch;
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
    if (isV3Doc(doc) && rectDrag) {
        const d = doc;
        const x0 = Math.min(rectDrag.ax, rectDrag.cx);
        const x1 = Math.max(rectDrag.ax, rectDrag.cx);
        const y0 = Math.min(rectDrag.ay, rectDrag.cy);
        const y1 = Math.max(rectDrag.ay, rectDrag.cy);
        for (let y = y0; y <= y1; y++) {
            for (let x = x0; x <= x1; x++) paintCellV3(d, x, y, selectedTile);
        }
        renderer.clearRectPreview();
        renderer.flush();
        rectDrag = null;
    }
    const patch: Patch = {};
    const cells = [...stroke.cells.values()].filter((c) => c.before !== c.after);
    if (cells.length > 0) {
        patch.cells = cells;
        if (isV3Doc(doc)) patch.layer = activeLayerIndexV3();
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
        if (isV3Doc(doc)) {
            activeLayerArrV3(doc)[c.i] = c.before as number;
        } else {
            (doc as import("./model").MapDoc).grid[c.i] = c.before as string;
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
    if (isV3Doc(doc)) {
        activeLayer = "ground";
        selectedTile = Math.min(selectedTile, tileCount(activeTileset(doc)) - 1);
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
    if (!isV3Doc(doc)) return;
    const d = doc;
    const ts = activeTileset(d);
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
    if (!isV3Doc(doc)) return;
    const t = activeTileset(doc).tiles[id];
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
    if (!isV3Doc(doc)) return null;
    const ts = activeTileset(doc);
    const cv = $<HTMLCanvasElement>("ts-canvas");
    const r = cv.getBoundingClientRect();
    const x = Math.floor((e.clientX - r.left) / TS_PX);
    const y = Math.floor((e.clientY - r.top) / TS_PX);
    if (x < 0 || y < 0 || x >= ts.columns || y >= ts.rows) return null;
    return y * ts.columns + x;
}

/** タイルセット画像の差し替え。属性は id ベースで維持、範囲外になったレイヤー値は -1 に */
function replaceTileset(ts: TilesetDoc): void {
    if (!isV3Doc(doc)) return;
    const d = doc;
    const li = activeLayerIndexV3();
    const tsIdx = d.layers[li].tileset;
    const old = d.tilesets[tsIdx] ?? d.tilesets[0];
    const keep = Math.min(old.tiles.length, ts.tiles.length);
    for (let i = 0; i < keep; i++) ts.tiles[i] = { ...old.tiles[i] };
    d.tilesets[tsIdx] = ts;
    const count = tileCount(ts);
    let cleared = 0;
    // このタイルセットを使う全レイヤーの範囲外セルをクリア
    for (const layer of d.layers) {
        if (layer.tileset !== tsIdx) continue;
        for (let i = 0; i < layer.cells.length; i++) {
            if (layer.cells[i] >= count) {
                layer.cells[i] = -1;
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
        showMessages(`検証エラー (仕様 §${isV3Doc(doc) ? "20" : "9"}) — 修正してから出力してください`, r.errors, []);
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

// ▶ ゲームで試す: マップを EKMaps フォルダへ直接保存する。モッド側 L1 自動リロードが約2秒で拾う。
async function playInGame(): Promise<void> {
    const text = buildValidatedJson(true);
    if (text === null) return;

    const filename = `${sanitizeFileName(doc.name)}.ekmap.json`;
    const r = await saveForPlaytest(filename, text);

    if (r.ok) {
        // 自動リロードで反映される。初回や未ロード時のために /map load の案内も添える。
        const base = filename.replace(/\.ekmap\.json$/, "");
        toast(`保存しました → ${r.where}\nゲーム内で自動反映されます (初回は /map load ${base})`);
        return;
    }

    if (r.reason === "cancelled") {
        toast("キャンセルしました");
        return;
    }

    if (r.reason === "unsupported") {
        // FS 直書き未対応ブラウザ: ダウンロードに倒し、置き場所を案内
        exportFile();
        toast("このブラウザはフォルダ直接保存に未対応です。ダウンロードしたファイルを Documents/EndKnot/EKMaps に置いてください");
        return;
    }

    toast(`保存に失敗しました: ${r.message}`);
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

// ================================================================
// タイルセットインポートウィザード状態 (新規ダイアログ内)
// ================================================================

interface WizState {
    file: File | null;
    /** data:image/png;base64,… */
    dataUri: string | null;
    /** PNG の元の寸法 */
    pngWidth: number;
    pngHeight: number;
    /** Canvas から取得した RGBA ピクセル (rebake/スコアリング用) */
    rgba: Uint8ClampedArray | null;
    /** スコアリング済み候補リスト (score 降順) */
    scored: SliceCandidate[];
    /** 現在選択中のパラメータ */
    params: SliceParams;
    /** 確定した後にリベイクが必要か */
    needsRebake: boolean;
}

const wizState: WizState = {
    file: null,
    dataUri: null,
    pngWidth: 0,
    pngHeight: 0,
    rgba: null,
    scored: [],
    params: { tileSize: 32, margin: 0, spacing: 0, cols: 8, rows: 8 },
    needsRebake: false,
};

function wizReset(): void {
    wizState.file = null;
    wizState.dataUri = null;
    wizState.pngWidth = 0;
    wizState.pngHeight = 0;
    wizState.rgba = null;
    wizState.scored = [];
    wizState.params = { tileSize: 32, margin: 0, spacing: 0, cols: 8, rows: 8 };
    wizState.needsRebake = false;

    $("wiz-drop-zone").hidden = false;
    $("wiz-preview-wrap").hidden = true;
    $("wiz-step23").hidden = true;
    $<HTMLButtonElement>("new-ok-btn").disabled = true;
}

/** RGBA ピクセルを取得するためにオフスクリーン Canvas を使う (DOM 経路) */
async function getPngRgba(dataUri: string): Promise<{ rgba: Uint8ClampedArray; width: number; height: number } | null> {
    return new Promise((resolve) => {
        const img = new Image();
        img.onload = () => {
            const cv = document.createElement("canvas");
            cv.width = img.naturalWidth;
            cv.height = img.naturalHeight;
            const ctx = cv.getContext("2d");
            if (!ctx) { resolve(null); return; }
            ctx.drawImage(img, 0, 0);
            try {
                const data = ctx.getImageData(0, 0, cv.width, cv.height);
                resolve({ rgba: data.data, width: cv.width, height: cv.height });
            } catch {
                resolve(null);
            }
        };
        img.onerror = () => resolve(null);
        img.src = dataUri;
    });
}

/** オフスクリーン Canvas で RGBA → PNG data URI に変換 (リベイク後の再エンコード用) */
function rgbaToPngDataUri(rgba: Uint8ClampedArray, width: number, height: number): string {
    const cv = document.createElement("canvas");
    cv.width = width;
    cv.height = height;
    const ctx = cv.getContext("2d")!;
    // ImageData コンストラクタには Uint8ClampedArray with ArrayBuffer が必要
    // rebakeAtlas は new Uint8ClampedArray() で確保するので buffer は ArrayBuffer
    const imgData = ctx.createImageData(width, height);
    imgData.data.set(rgba);
    ctx.putImageData(imgData, 0, 0);
    return cv.toDataURL("image/png");
}

/**
 * PNG ファイルをウィザードに読み込む。
 * - data URI 変換
 * - RGBA 取得
 * - 候補列挙 & スコアリング
 * - UI 更新
 */
async function wizLoadPng(file: File): Promise<void> {
    if (file.size > MAX_TILESET_IMAGE_BYTES) {
        toast(`PNG が 1 MB を超えています (${(file.size / 1024).toFixed(1)} KB)`);
        return;
    }
    if (!file.type.includes("png") && !file.name.toLowerCase().endsWith(".png")) {
        toast("PNG ファイルを選択してください");
        return;
    }

    const bytes = new Uint8Array(await file.arrayBuffer());
    const b64 = bytesToBase64(bytes);
    const dataUri = PNG_DATA_URI_PREFIX + b64;

    const pngInfo = parsePngDataUri(dataUri);
    if (!pngInfo.ok) {
        toast(`PNG エラー: ${pngInfo.error}`);
        return;
    }

    // RGBA 取得 (Canvas DOM 経路)
    const rgbaResult = await getPngRgba(dataUri);
    if (!rgbaResult) {
        toast("画像の読み込みに失敗しました");
        return;
    }

    wizState.file = file;
    wizState.dataUri = dataUri;
    wizState.pngWidth = pngInfo.width;
    wizState.pngHeight = pngInfo.height;
    wizState.rgba = rgbaResult.rgba;

    // 候補列挙 & スコアリング
    const candidates = enumerateCandidates(pngInfo.width, pngInfo.height);
    wizState.scored = scoreCandidates(rgbaResult.rgba, pngInfo.width, pngInfo.height, candidates);

    // 最高スコア候補を初期選択
    if (wizState.scored.length > 0) {
        wizState.params = { ...wizState.scored[0].params };
    } else {
        // 整合候補なし → tileSize を画像幅に合わせて fallback
        const fallbackSize = Math.min(pngInfo.width, TILESIZE_MAX);
        wizState.params = {
            tileSize: fallbackSize,
            margin: 0,
            spacing: 0,
            cols: Math.floor(pngInfo.width / fallbackSize),
            rows: Math.floor(pngInfo.height / fallbackSize),
        };
    }
    wizState.needsRebake = wizState.params.margin > 0 || wizState.params.spacing > 0;

    // UI 反映
    wizUpdateDropZone();
    wizUpdateCandidateChips();
    wizSyncInputsFromParams();
    wizUpdateSlicePreview();
    wizUpdateStats();
    $("wiz-step23").hidden = false;
    $<HTMLButtonElement>("new-ok-btn").disabled = false;
}

/** ドロップゾーンとプレビューを更新 */
function wizUpdateDropZone(): void {
    const hasFile = wizState.dataUri !== null;
    $("wiz-drop-zone").hidden = hasFile;
    $("wiz-preview-wrap").hidden = !hasFile;

    if (!hasFile) return;
    // サムネイル表示
    const cv = $<HTMLCanvasElement>("wiz-preview-canvas");
    const img = new Image();
    img.onload = () => {
        const MAX_W = 300;
        const scale = Math.min(1, MAX_W / img.naturalWidth);
        cv.width = Math.round(img.naturalWidth * scale);
        cv.height = Math.round(img.naturalHeight * scale);
        cv.style.width = `${cv.width}px`;
        cv.style.height = `${cv.height}px`;
        const ctx = cv.getContext("2d");
        if (!ctx) return;
        ctx.imageSmoothingEnabled = false;
        ctx.drawImage(img, 0, 0, cv.width, cv.height);
    };
    img.src = wizState.dataUri!;
    const info = $("wiz-preview-info");
    info.textContent = `${wizState.file?.name ?? ""} — ${wizState.pngWidth}×${wizState.pngHeight}px (${((wizState.file?.size ?? 0) / 1024).toFixed(1)} KB)　[クリックで変更]`;
}

/** 候補チップを描画 */
function wizUpdateCandidateChips(): void {
    const container = $("wiz-candidates");
    container.replaceChildren();

    // 上位 4 件のみ表示 (同じ tileSize の best margin/spacing を代表として絞る)
    const shown: SliceCandidate[] = [];
    const seenSize = new Set<number>();
    for (const c of wizState.scored) {
        if (!seenSize.has(c.params.tileSize)) {
            seenSize.add(c.params.tileSize);
            shown.push(c);
        }
        if (shown.length >= 4) break;
    }

    for (const c of shown) {
        const chip = document.createElement("button");
        chip.type = "button";
        chip.className = "wiz-chip";
        if (c.params.tileSize === wizState.params.tileSize &&
            c.params.margin === wizState.params.margin &&
            c.params.spacing === wizState.params.spacing) {
            chip.classList.add("active");
        }
        const sizeSpan = document.createElement("span");
        sizeSpan.className = "wiz-chip-size";
        sizeSpan.textContent = `${c.params.tileSize}px`;
        const confSpan = document.createElement("span");
        confSpan.className = "wiz-chip-conf";
        const colsRows = `${c.params.cols}列×${c.params.rows}行`;
        const marginInfo = c.params.margin > 0 || c.params.spacing > 0
            ? ` 余白${c.params.margin} 間隔${c.params.spacing}`
            : "";
        confSpan.textContent = `${colsRows}${marginInfo} (${c.confidence})`;
        chip.appendChild(sizeSpan);
        chip.appendChild(confSpan);
        chip.addEventListener("click", () => {
            wizState.params = { ...c.params };
            wizState.needsRebake = c.params.margin > 0 || c.params.spacing > 0;
            wizUpdateCandidateChips();
            wizSyncInputsFromParams();
            wizUpdateSlicePreview();
            wizUpdateStats();
        });
        container.appendChild(chip);
    }
}

/** 手動入力欄に現在の params を反映 */
function wizSyncInputsFromParams(): void {
    $<HTMLInputElement>("wiz-size").value = String(wizState.params.tileSize);
    $<HTMLInputElement>("wiz-margin").value = String(wizState.params.margin);
    $<HTMLInputElement>("wiz-spacing").value = String(wizState.params.spacing);
}

/** 手動入力から params を更新し、UI を再描画 */
function wizSyncParamsFromInputs(): void {
    const size = Math.min(TILESIZE_MAX, Math.max(TILESIZE_MIN, Math.floor(Number($<HTMLInputElement>("wiz-size").value)) || TILESIZE_DEFAULT));
    const margin = Math.min(4, Math.max(0, Math.floor(Number($<HTMLInputElement>("wiz-margin").value)) || 0));
    const spacing = Math.min(4, Math.max(0, Math.floor(Number($<HTMLInputElement>("wiz-spacing").value)) || 0));

    // W = 2m + c*s + (c-1)*g  →  c = (W - 2m + g) / (s + g)
    const wNom = wizState.pngWidth - 2 * margin + spacing;
    const wDen = size + spacing;
    const cols = wDen > 0 && wNom > 0 && wNom % wDen === 0 ? wNom / wDen : Math.floor(wizState.pngWidth / size);
    const hNom = wizState.pngHeight - 2 * margin + spacing;
    const hDen = size + spacing;
    const rows = hDen > 0 && hNom > 0 && hNom % hDen === 0 ? hNom / hDen : Math.floor(wizState.pngHeight / size);

    wizState.params = {
        tileSize: size,
        margin,
        spacing,
        cols: Math.max(1, cols),
        rows: Math.max(1, rows),
    };
    wizState.needsRebake = margin > 0 || spacing > 0;

    wizUpdateCandidateChips();
    wizUpdateSlicePreview();
    wizUpdateStats();
}

const GRID_LINE_COLOR = "rgba(255, 100, 100, 0.85)";
const GRID_LINE_WIDTH = 1;
/** スライスプレビューキャンバスにグリッド線を重畳描画 */
function wizUpdateSlicePreview(): void {
    if (!wizState.dataUri) return;
    const { tileSize, margin, spacing, cols, rows } = wizState.params;
    const MAX_PREVIEW = 480;
    const scale = Math.min(1, MAX_PREVIEW / Math.max(wizState.pngWidth, 1));
    const dW = Math.round(wizState.pngWidth * scale);
    const dH = Math.round(wizState.pngHeight * scale);

    const cv = $<HTMLCanvasElement>("wiz-slice-canvas");
    cv.width = dW;
    cv.height = dH;
    cv.style.width = `${dW}px`;
    cv.style.height = `${dH}px`;

    const ctx = cv.getContext("2d");
    if (!ctx) return;

    const img = new Image();
    img.onload = () => {
        ctx.imageSmoothingEnabled = false;
        ctx.drawImage(img, 0, 0, dW, dH);

        ctx.strokeStyle = GRID_LINE_COLOR;
        ctx.lineWidth = GRID_LINE_WIDTH;

        // 縦線: 各タイル列の左端 + spacing があれば右端も
        for (let c = 0; c < cols; c++) {
            const xL = Math.round((margin + c * (tileSize + spacing)) * scale) + 0.5;
            ctx.beginPath(); ctx.moveTo(xL, 0); ctx.lineTo(xL, dH); ctx.stroke();
            if (spacing > 0) {
                const xR = Math.round((margin + c * (tileSize + spacing) + tileSize) * scale) + 0.5;
                ctx.beginPath(); ctx.moveTo(xR, 0); ctx.lineTo(xR, dH); ctx.stroke();
            }
        }
        // 右端の閉じ線 (spacing=0 のときだけ必要)
        if (spacing === 0) {
            const xFinal = Math.round((margin + cols * tileSize) * scale) + 0.5;
            ctx.beginPath(); ctx.moveTo(xFinal, 0); ctx.lineTo(xFinal, dH); ctx.stroke();
        }

        // 横線: 各タイル行の上端 + spacing があれば下端も
        for (let r = 0; r < rows; r++) {
            const yT = Math.round((margin + r * (tileSize + spacing)) * scale) + 0.5;
            ctx.beginPath(); ctx.moveTo(0, yT); ctx.lineTo(dW, yT); ctx.stroke();
            if (spacing > 0) {
                const yB = Math.round((margin + r * (tileSize + spacing) + tileSize) * scale) + 0.5;
                ctx.beginPath(); ctx.moveTo(0, yB); ctx.lineTo(dW, yB); ctx.stroke();
            }
        }
        if (spacing === 0) {
            const yFinal = Math.round((margin + rows * tileSize) * scale) + 0.5;
            ctx.beginPath(); ctx.moveTo(0, yFinal); ctx.lineTo(dW, yFinal); ctx.stroke();
        }
    };
    img.src = wizState.dataUri;
}

/** 統計テキスト + リベイク警告を更新 */
function wizUpdateStats(): void {
    const { cols, rows, tileSize, margin, spacing } = wizState.params;
    const total = cols * rows;

    // 全透明タイル数 (m=0/g=0 のときのみ即座に検出。それ以外は空白)
    let transparentTxt = "";
    if (margin === 0 && spacing === 0 && wizState.rgba) {
        const transparent = detectTransparentTiles(wizState.rgba, cols, rows, tileSize);
        if (transparent.size > 0) {
            transparentTxt = ` | 空タイル ${transparent.size} 個`;
        }
    }

    const overLimit = total > 4096 ? " — 上限 4096 を超えています！" : "";
    $("wiz-stats").textContent = `${cols} 列 × ${rows} 行 = ${total} チップ${transparentTxt}${overLimit}`;

    const rebakeNote = $("wiz-rebake-note");
    rebakeNote.hidden = !(margin > 0 || spacing > 0);
}

/**
 * ウィザード状態から TilesetDoc を構築して返す。
 * margin/spacing > 0 の場合は Canvas 経由でリベイク → 再エンコード。
 * m=0/g=0 の場合は元の dataUri バイトを無変更で使用。
 */
async function wizBuildTileset(): Promise<TilesetImport> {
    if (!wizState.dataUri || !wizState.rgba) {
        return { ok: false, error: "PNG が読み込まれていません" };
    }
    const { tileSize, margin, spacing, cols, rows } = wizState.params;

    if (tileSize < TILESIZE_MIN || tileSize > TILESIZE_MAX) {
        return { ok: false, error: `タイルの大きさは ${TILESIZE_MIN}〜${TILESIZE_MAX} px が必要です` };
    }
    if (cols < 1 || rows < 1) {
        return { ok: false, error: "列数・行数が 0 になっています。設定を確認してください" };
    }
    const total = cols * rows;
    if (total > 4096) {
        return { ok: false, error: `チップ数 (${total}) が上限 4096 を超えています` };
    }

    let finalUri = wizState.dataUri;

    if (margin > 0 || spacing > 0) {
        // リベイク: ピクセル操作 → Canvas で再エンコード
        const rebaked = rebakeAtlas(wizState.rgba, wizState.params);
        finalUri = rgbaToPngDataUri(rebaked.rgba, rebaked.width, rebaked.height);

        // リベイク後サイズ検証 (base64 → バイト概算)
        const b64Body = finalUri.slice(PNG_DATA_URI_PREFIX.length);
        const approxBytes = Math.floor(b64Body.length * 3 / 4);
        if (approxBytes > MAX_TILESET_IMAGE_BYTES) {
            return { ok: false, error: `リベイク後の PNG が 1 MB を超えています (約 ${(approxBytes / 1024).toFixed(1)} KB)。元の画像をもっと小さくするか、より大きなタイルサイズを選択してください` };
        }
    }

    const errors: string[] = [];
    const ts = validateTileset({ tileSize, columns: cols, image: finalUri }, errors);
    if (!ts) return { ok: false, error: errors[0] ?? "タイルセットを検証できません" };

    return { ok: true, tileset: ts, pngWidth: cols * tileSize, pngHeight: rows * tileSize };
}

// ================================================================
// タイル1枚追加フロー (addTile*)
// ================================================================

/** addTile ダイアログの状態 */
interface AddTileState {
    /** data:image/png;base64,… */
    dataUri: string | null;
    rgba: Uint8ClampedArray | null;
    srcW: number;
    srcH: number;
}

const addTileState: AddTileState = { dataUri: null, rgba: null, srcW: 0, srcH: 0 };

function addTileReset(): void {
    addTileState.dataUri = null;
    addTileState.rgba = null;
    addTileState.srcW = 0;
    addTileState.srcH = 0;
    $("add-tile-drop-zone").hidden = false;
    $("add-tile-preview-wrap").hidden = true;
    $("add-tile-size-note").hidden = true;
    $<HTMLButtonElement>("add-tile-ok").disabled = true;
}

function addTileUpdatePreview(): void {
    if (!addTileState.dataUri || !isV3Doc(doc)) return;
    const ts = activeTileset(doc);

    $("add-tile-drop-zone").hidden = true;
    $("add-tile-preview-wrap").hidden = false;

    // サムネイル描画
    const cv = $<HTMLCanvasElement>("add-tile-preview-canvas");
    const img = new Image();
    img.onload = () => {
        const SZ = Math.min(img.naturalWidth, 128);
        cv.width = SZ;
        cv.height = SZ;
        cv.style.width = `${SZ}px`;
        cv.style.height = `${SZ}px`;
        const ctx = cv.getContext("2d");
        if (!ctx) return;
        ctx.imageSmoothingEnabled = false;
        ctx.drawImage(img, 0, 0, SZ, SZ);
    };
    img.src = addTileState.dataUri;

    // 寸法説明
    const { srcW, srcH } = addTileState;
    const note = $("add-tile-size-note");
    if (srcW !== ts.tileSize || srcH !== ts.tileSize) {
        note.textContent = `元のサイズ ${srcW}×${srcH}px → タイルの大きさ (${ts.tileSize}×${ts.tileSize}px) に合わせます`;
        note.hidden = false;
    } else {
        note.hidden = true;
    }

    $("add-tile-preview-info").textContent = `${srcW}×${srcH}px`;
    $<HTMLButtonElement>("add-tile-ok").disabled = false;
}

async function addTileLoadPng(file: File): Promise<void> {
    if (!isV3Doc(doc)) return;

    if (file.size > MAX_TILESET_IMAGE_BYTES) {
        toast(`PNG が 1 MB を超えています (${(file.size / 1024).toFixed(1)} KB)`);
        return;
    }
    if (!file.type.includes("png") && !file.name.toLowerCase().endsWith(".png")) {
        toast("PNG ファイルを選択してください");
        return;
    }

    const bytes = new Uint8Array(await file.arrayBuffer());
    const dataUri = PNG_DATA_URI_PREFIX + bytesToBase64(bytes);
    const pngInfo = parsePngDataUri(dataUri);
    if (!pngInfo.ok) {
        toast(`PNG エラー: ${pngInfo.error}`);
        return;
    }

    const rgbaResult = await getPngRgba(dataUri);
    if (!rgbaResult) {
        toast("画像の読み込みに失敗しました");
        return;
    }

    addTileState.dataUri = dataUri;
    addTileState.rgba = rgbaResult.rgba;
    addTileState.srcW = pngInfo.width;
    addTileState.srcH = pngInfo.height;

    addTileUpdatePreview();
}

/** タイル追加を確定して tileset を更新する。DOM 経路 (canvas) を使う */
async function addTileCommit(): Promise<void> {
    if (!isV3Doc(doc) || !addTileState.rgba) return;
    const d = doc;
    const li = activeLayerIndexV3();
    const tsIdx = d.layers[li].tileset;
    const ts = d.tilesets[tsIdx] ?? d.tilesets[0];
    const { tileSize, columns, rows } = ts;

    // 上限チェック
    const currentCount = tileCount(ts);
    if (currentCount >= MAX_TILECOUNT) {
        showMessages("タイルを追加できません", [`チップ数が上限 ${MAX_TILECOUNT} に達しています`], []);
        return;
    }

    // 1. nearest-neighbor でタイルサイズに縮小/拡大
    let tilePx: Uint8ClampedArray;
    if (addTileState.srcW === tileSize && addTileState.srcH === tileSize) {
        tilePx = addTileState.rgba;
    } else {
        tilePx = resizeNearestNeighbor(addTileState.rgba, addTileState.srcW, addTileState.srcH, tileSize, tileSize);
    }

    // 2. アトラスにアペンド (純関数)
    const atlasRgbaResult = await getPngRgba(ts.image);
    if (!atlasRgbaResult) {
        toast("現在のタイルセット画像を読み込めませんでした");
        return;
    }

    const { rgba: newRgba, newRows, newTileIndex } = appendTileToAtlas(
        atlasRgbaResult.rgba,
        columns,
        rows,
        tileSize,
        tilePx,
    );

    // 3. RGBA → PNG data URI (DOM 経路)
    const newImageUri = rgbaToPngDataUri(newRgba, columns * tileSize, newRows * tileSize);

    // 4. PNG サイズ検証 (概算)
    const b64Body = newImageUri.slice(PNG_DATA_URI_PREFIX.length);
    const approxBytes = Math.floor(b64Body.length * 3 / 4);
    if (approxBytes > MAX_TILESET_IMAGE_BYTES) {
        showMessages("タイルを追加できません", [
            `追加後のアトラス PNG が 1 MB を超えます (約 ${(approxBytes / 1024).toFixed(1)} KB)。タイルセットを小さくしてから追加してください。`
        ], []);
        return;
    }

    // 5. JSON サイズ概算検証 (v2 4MB 上限)
    const newTileset: TilesetDoc = {
        tileSize,
        columns,
        rows: newRows,
        image: newImageUri,
        tiles: [...ts.tiles],
    };
    // 新タイルの属性を追加 (必要なら中間スロットも dense に保つ)
    while (newTileset.tiles.length < newTileIndex) {
        newTileset.tiles.push(defaultTileAttr());
    }
    if (newTileset.tiles.length === newTileIndex) {
        newTileset.tiles.push(defaultTileAttr());
    }

    // 粗い JSON サイズ見積もり
    const roughJson = JSON.stringify({ image: newImageUri });
    if (roughJson.length > MAX_JSON_BYTES_V2) {
        showMessages("タイルを追加できません", [
            `追加後のマップ JSON が 4 MB を超える見込みです。タイルセットを小さくしてから追加してください。`
        ], []);
        return;
    }

    // 6. tileset を差し替え (rows 以外は保持。history.clear を使う replaceTileset を流用)
    //    Undo は非対応: タイルセット差し替えは既存の replaceTileset でも history.clear するため
    d.tilesets[tsIdx] = newTileset;

    // 7. 選択タイルを新しいタイルに移す + 再描画
    history.clear();
    refreshUndoButtons();
    selectedTile = newTileIndex;
    renderer.setDoc(d);
    void rebuildPicker();
    drawTilesetPanel();
    scheduleSave();
    updateStatus();

    $<HTMLDialogElement>("dlg-add-tile").close();
    toast(`タイル ${newTileIndex} を追加しました (計 ${tileCount(newTileset)} チップ)`);
}

function openAddTileDialog(): void {
    if (!isV3Doc(doc)) {
        toast("タイル追加はタイルセットマップのみ対応しています");
        return;
    }
    addTileReset();
    $<HTMLDialogElement>("dlg-add-tile").showModal();
}

function wireUi(): void {
    // v1 ツールパレット
    const toolButtons = [...document.querySelectorAll<HTMLButtonElement>("#tools .tool")];
    const selectTool = (b: HTMLButtonElement): void => {
        tool = b.dataset.tool as ToolV1;
        for (const x of toolButtons) x.classList.toggle("active", x === b);
        updateRibbon();
    };
    for (const b of toolButtons) b.addEventListener("click", () => selectTool(b));
    const def = toolButtons.find((b) => b.dataset.tool === tool);
    if (def) selectTool(def);

    // v2 レイヤータブ (旧フッタ横タブ — CSS 非表示だがイベントは維持)
    for (const b of document.querySelectorAll<HTMLButtonElement>("#layer-tabs .ltab")) {
        b.addEventListener("click", () => setActiveLayer(b.dataset.layer as LayerTab));
    }
    // v2 レイヤー縦タブ (canvas 右端オーバーレイ)
    for (const b of document.querySelectorAll<HTMLButtonElement>("#layer-vtabs .lvtab")) {
        b.addEventListener("click", () => setActiveLayer(b.dataset.layer as LayerTab));
    }
    // 縦タブ内の「チップ設定」ボタン (フッタの btn-tileset と同じ動作)
    $("btn-tileset-vtab").addEventListener("click", () => {
        if (!isV3Doc(doc)) return;
        $("ts-hint").textContent = TS_HINTS[tsMode];
        $<HTMLDialogElement>("dlg-tileset").showModal();
        drawTilesetPanel();
    });
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

    // コンパクト帯: クリックで近傍チップを選択
    $<HTMLCanvasElement>("strip-canvas").addEventListener("click", (e) => {
        if (!isV3Doc(doc)) return;
        const cv = $<HTMLCanvasElement>("strip-canvas");
        const r = cv.getBoundingClientRect();
        const idx = Math.floor((e.clientX - r.left) / STRIP_PX);
        const raw = cv.dataset.stripIds ?? "";
        const ids = raw.split(",").map(Number).filter((n) => Number.isFinite(n));
        if (idx < 0 || idx >= ids.length) return;
        selectTile(ids[idx]);
        if (toolV2 === "erase" || toolV2 === "pick") setToolV2("pen");
    });

    // ▲▲ で全画面グリッドを開く
    $("btn-palette-expand").addEventListener("click", () => {
        if (!isV3Doc(doc)) return;
        openPaletteOverlay();
    });

    // ▼▼ で全画面グリッドを閉じる
    $("btn-palette-collapse").addEventListener("click", () => {
        closePaletteOverlay();
    });

    // 全画面グリッド: チップをクリックして選択
    $<HTMLCanvasElement>("palette-canvas").addEventListener("click", (e) => {
        if (!isV3Doc(doc)) return;
        const cv = $<HTMLCanvasElement>("palette-canvas");
        const r = cv.getBoundingClientRect();
        const cols = Number(cv.dataset.gridCols ?? "1");
        const ix = Math.floor((e.clientX - r.left) / GRID_PX);
        const iy = Math.floor((e.clientY - r.top) / GRID_PX);
        const idx = iy * cols + ix;
        const raw = cv.dataset.filteredIds ?? "";
        const ids = raw.split(",").map(Number).filter((n) => Number.isFinite(n));
        if (idx < 0 || idx >= ids.length) return;
        selectTile(ids[idx]);
        if (toolV2 === "erase" || toolV2 === "pick") setToolV2("pen");
    });

    // 検索フィールド: 入力で即時絞り込み
    $<HTMLInputElement>("palette-search").addEventListener("input", (e) => {
        paletteFilterText = (e.target as HTMLInputElement).value;
        void rebuildPaletteGrid(paletteFilterText, paletteUsedOnly);
    });

    // 使用中のみトグル
    $<HTMLInputElement>("palette-used-only").addEventListener("change", (e) => {
        paletteUsedOnly = (e.target as HTMLInputElement).checked;
        void rebuildPaletteGrid(paletteFilterText, paletteUsedOnly);
    });

    // Esc で全画面グリッドを閉じる (既存 keydown ハンドラの外でシンプルに追加)
    $("palette-overlay").addEventListener("keydown", (e) => {
        if (e.key === "Escape") { closePaletteOverlay(); e.stopPropagation(); }
    });

    // タイルセット設定パネル
    const dlgTs = $<HTMLDialogElement>("dlg-tileset");
    $("btn-tileset").addEventListener("click", () => {
        if (!isV3Doc(doc)) return;
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
    pngInput.addEventListener("change", async () => {
        const f = pngInput.files?.[0];
        pngInput.value = "";
        if (!f) return;
        if (pngTarget === "new") {
            await wizLoadPng(f);
            return;
        }
        if (!isV3Doc(doc)) return;
        const r = await importTilesetPng(f, activeTileset(doc).tileSize);
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
        if (v2) {
            // v2 選択時: PNG が未投入なら作成ボタン無効
            $<HTMLButtonElement>("new-ok-btn").disabled = wizState.dataUri === null;
        } else {
            // v1 は常に有効
            $<HTMLButtonElement>("new-ok-btn").disabled = false;
        }
    };
    for (const r of typeRadios) r.addEventListener("change", refreshTypeUi);
    $("btn-new").addEventListener("click", () => {
        $<HTMLInputElement>("new-name").value = "新しいマップ";
        $<HTMLInputElement>("new-author").value = doc.author;
        wizReset();
        refreshTypeUi();
        dlgNew.showModal();
    });

    // ドロップゾーン: クリックで file input を開く
    $("wiz-drop-zone").addEventListener("click", () => {
        pngTarget = "new";
        pngInput.click();
    });

    // ドロップゾーン: ドラッグ&ドロップ
    $("wiz-drop-zone").addEventListener("dragover", (e) => {
        e.preventDefault();
        $("wiz-drop-zone").classList.add("drag-over");
    });
    $("wiz-drop-zone").addEventListener("dragleave", () => {
        $("wiz-drop-zone").classList.remove("drag-over");
    });
    $("wiz-drop-zone").addEventListener("drop", async (e) => {
        e.preventDefault();
        $("wiz-drop-zone").classList.remove("drag-over");
        const file = e.dataTransfer?.files[0];
        if (file) await wizLoadPng(file);
    });

    // プレビュー画像クリック: 別の PNG に差し替え
    $("wiz-preview-info").addEventListener("click", () => {
        pngTarget = "new";
        pngInput.click();
    });

    // Ctrl+V クリップボード貼り付け (ダイアログが開いているときのみ)
    document.addEventListener("paste", async (e) => {
        if (!dlgNew.open) return;
        const items = e.clipboardData?.items;
        if (!items) return;
        for (const item of items) {
            if (item.type === "image/png") {
                const file = item.getAsFile();
                if (file) await wizLoadPng(file);
                break;
            }
        }
    });

    // 手動入力変更時: params 更新 → プレビュー更新
    const manualInputIds = ["wiz-size", "wiz-margin", "wiz-spacing"];
    for (const id of manualInputIds) {
        $<HTMLInputElement>(id).addEventListener("input", () => {
            if (wizState.dataUri) wizSyncParamsFromInputs();
        });
        $<HTMLInputElement>(id).addEventListener("change", () => {
            if (wizState.dataUri) wizSyncParamsFromInputs();
        });
    }

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
        if (!wizState.dataUri) {
            showMessages("新規マップ (タイルセット) を作成できません", ["タイルセット PNG が未選択です"], []);
            return;
        }
        void wizBuildTileset().then((r) => {
            if (!r.ok) {
                showMessages("新規マップ (タイルセット) を作成できません", [r.error], []);
                return;
            }
            selectedTile = 0;
            void backupAutosave();
            setDocument(createNewDocV3(w, h, name, author, r.tileset));
            const rebakeInfo = wizState.needsRebake ? " (余白を正規化しました)" : "";
            toast(`${w}×${h} のタイルセットマップを作成しました (チップ ${tileCount(r.tileset)} 個)${rebakeInfo}`);
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
    $("btn-play").addEventListener("click", () => void playInGame());

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

    // タイル1枚追加ボタン (オーバーレイ + 帯)
    $("btn-add-tile-overlay").addEventListener("click", () => openAddTileDialog());
    $("btn-add-tile-strip").addEventListener("click", () => openAddTileDialog());

    // タイル追加ダイアログ: ファイル入力
    const addTileInput = $<HTMLInputElement>("add-tile-input");
    $("add-tile-drop-zone").addEventListener("click", () => addTileInput.click());
    addTileInput.addEventListener("change", async () => {
        const f = addTileInput.files?.[0];
        addTileInput.value = "";
        if (f) await addTileLoadPng(f);
    });

    // ドラッグ&ドロップ
    $("add-tile-drop-zone").addEventListener("dragover", (e) => {
        e.preventDefault();
        $("add-tile-drop-zone").classList.add("drag-over");
    });
    $("add-tile-drop-zone").addEventListener("dragleave", () => {
        $("add-tile-drop-zone").classList.remove("drag-over");
    });
    $("add-tile-drop-zone").addEventListener("drop", async (e) => {
        e.preventDefault();
        $("add-tile-drop-zone").classList.remove("drag-over");
        const file = e.dataTransfer?.files[0];
        if (file) await addTileLoadPng(file);
    });

    // Ctrl+V 貼り付け (dlg-add-tile が開いているときのみ)
    const dlgAddTile = $<HTMLDialogElement>("dlg-add-tile");
    document.addEventListener("paste", async (e) => {
        if (!dlgAddTile.open) return;
        const items = e.clipboardData?.items;
        if (!items) return;
        for (const item of items) {
            if (item.type === "image/png") {
                const file = item.getAsFile();
                if (file) await addTileLoadPng(file);
                break;
            }
        }
    });

    // 追加確定 / キャンセル
    $("add-tile-ok").addEventListener("click", () => void addTileCommit());
    $("add-tile-cancel").addEventListener("click", () => dlgAddTile.close());

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
        // フッタを閉じたときは全画面グリッドも閉じる
        if (collapsed) closePaletteOverlay();
    });

    // キーボード: Ctrl+Z / Ctrl+Y / Ctrl+Shift+Z / Esc (全画面パレットを閉じる)
    window.addEventListener("keydown", (e) => {
        const tag = (e.target as HTMLElement | null)?.tagName ?? "";
        // Esc: 全画面パレットが開いていれば閉じる (入力欄内でも動作させる)
        if (e.key === "Escape") {
            const overlay = $("palette-overlay");
            if (!overlay.hidden) { closePaletteOverlay(); e.preventDefault(); return; }
        }
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
