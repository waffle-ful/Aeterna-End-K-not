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
    MAX_TILESETS,
    MIN_DIM,
    type MapDocV3,
    NAME_MAX,
    type PassValue,
    SHADOW_MAX_LINES,
    type SpawnPoint,
    TAG_MAX,
    TILESIZE_DEFAULT,
    TILESIZE_MAX,
    TILESIZE_MIN,
    type TileAttr,
    type TilesetDoc,
    coordToCell,
    createNewDocV3,
    defaultTileAttr,
    docToJsonAny,
    isV3Doc,
    tileCount,
} from "./model";
import {
    type SliceCandidate,
    type SliceParams,
    appendSheetToAtlas,
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
import { backupAutosave, deleteLibrary, getLibrary, listLibrary, loadAutosave, loadAutotiles, putLibrary, saveAutosave, saveAutotiles, tryRestoreDoc } from "./persist";
import {
    type AutotileDef,
    EDGE4_SLOTS,
    bakeAutotileTileAttrs,
    computeAutotileEdits,
    createEdge4Def,
    edge4Code,
    generateEdge4WallSheet,
    isLutEmpty,
    resolveEdge4,
} from "./autotile";
import { MapRenderer, loadTilesetImage } from "./render";
import { InputController } from "./input";
import { saveForPlaytest } from "./playtest";
import { createBackroomsDoc } from "./presets";
import { EKM_TEMPLATES, type EkmTemplate } from "./templates";

type ToolV1 = "floor" | "wall" | "void" | DecorKind | "spawn";
type ToolV2 = "pen" | "erase" | "rect" | "bucket" | "pick";
type LayerTab = "ground" | "upper" | "layer3" | "layer4" | "decor" | "spawn" | "shadow";

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
    ground: "レイヤー1 — マップの土台となるタイルを配置します",
    upper:  "レイヤー2 — プレイヤーの上に描画するタイル (屋根・梁など) を配置します",
    layer3: "レイヤー3 — 追加のタイル層です",
    layer4: "レイヤー4 — 追加のタイル層です",
    decor:  "装飾 — 灯りやドアなど小物を1マスずつ置きます。クリックで配置 / 再クリックで除去",
    spawn:  "スポーン — プレイヤーの初期位置を設定します。クリック / ドラッグで移動できます",
    shadow: "影 — 遮蔽辺を引きます。ドラッグで線分を追加、クリックで既存線を選択・削除できます。影だけ・通行はふさがないことに注意！",
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
    // 左レールの道具リモコン・右レールのレイヤー一覧/チップ属性も同期
    // (フッタ/縦タブの active/hidden は呼び出し時点で確定済み)
    refreshToolRail();
    refreshLayerList();
    refreshTileAttrs();
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

let doc: AnyDoc = createBackroomsDoc(32, 32, "新しいマップ", "");
let tool: ToolV1 = "wall";
let toolV2: ToolV2 = "pen";
let activeLayer: LayerTab = "ground";

// ---------- オートタイル状態 ----------
/** 現マップの autotile 定義リスト (エディタ専用・.ekmap には出さない) */
let autotiles: AutotileDef[] = [];
/** null = 通常ペイント / 数値 = そのブラシを使用 */
let activeAutotileIdx: number | null = null;
/** レイヤーごとの選択チップ id (layer0/1/2/3 に対応) */
let selectedTilePerLayer: [number, number, number, number] = [0, 0, 0, 0];
let decorKind2: DecorKind = "light";
const history = new History();
const renderer = new MapRenderer();

// ---------- 影モード 状態 ----------

/** 吸着 ON/OFF: 0.5 グリッド (セル角/辺/中心) にスナップ */
let shadowSnap = true;
/** ドラッグ中の始点 (セル座標 float) */
let shadowDragStart: { x: number; y: number } | null = null;
/** 選択中の影線インデックス (-1 = 未選択) */
let shadowSelectedIdx = -1;

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

// ---------- スタート画面 ----------

const COACH_DONE_KEY = "ekm.coachDone";
let startScreenShown = false;
let coachStep = 0;

/** コーチマークの5ステップ定義。targetId は候補列で、最初に表示されている要素を指す。 */
const COACH_STEPS: { targetId: string[]; title: string; body: string }[] = [
    {
        // v3 マップは #tools-v2、v1 マップは #tools が表示される (どちらか可視な方を指す)
        targetId: ["tools-v2", "tools"],
        title: "① 道具をえらぶ",
        body: "壁・床・灯りなど、置きたいものをここで選びます",
    },
    {
        targetId: ["viewport"],
        title: "② マップに描く",
        body: "えらんだ道具で、ここをドラッグして描きます",
    },
    {
        targetId: ["btn-export"],
        title: "③ 保存する",
        body: "作ったマップは書庫に保存。Ctrl+S でもサッと保存できます",
    },
    {
        targetId: ["btn-play"],
        title: "④ ゲームで試す",
        body: "ゲームのフォルダに出して、約2秒で実機に反映されます",
    },
    {
        targetId: ["btn-import"],
        title: "⑤ また開く",
        body: "保存したマップは「開く」からいつでも呼び出せます",
    },
];

/** コーチオーバーレイを指定ステップで表示する */
function showCoachStep(step: number): void {
    const overlay = $("coach-overlay");
    const steps = COACH_STEPS;

    // ステップ完了
    if (step >= steps.length) {
        endCoach();
        return;
    }

    // 対象要素の解決: 候補列から最初に表示されている (サイズ>0) 要素を選ぶ。
    // どれも非表示なら そのステップはスキップして次へ。
    const def = steps[step];
    let target: HTMLElement | null = null;
    for (const id of def.targetId) {
        const el = document.getElementById(id);
        if (!el) continue;
        const r = el.getBoundingClientRect();
        if (r.width > 0 && r.height > 0) { target = el; break; }
    }
    if (!target) {
        showCoachStep(step + 1);
        return;
    }
    const rect = target.getBoundingClientRect();
    if (rect.width === 0 || rect.height === 0) {
        showCoachStep(step + 1);
        return;
    }

    coachStep = step;
    overlay.hidden = false;

    // ハイライト位置
    const pad = 6;
    const hl = $("coach-highlight");
    hl.style.left = `${rect.left - pad}px`;
    hl.style.top = `${rect.top - pad}px`;
    hl.style.width = `${rect.width + pad * 2}px`;
    hl.style.height = `${rect.height + pad * 2}px`;

    // テキスト更新
    $("coach-title").textContent = def.title;
    $("coach-body").textContent = def.body;
    $("coach-step").textContent = `${step + 1} / ${steps.length}`;

    // 次へ/完了ボタン
    const nextBtn = $<HTMLButtonElement>("coach-next");
    nextBtn.textContent = step === steps.length - 1 ? "完了" : "次へ";

    // 吹き出し位置: 対象の下、画面内に収まらなければ上
    const tip = $("coach-tip");
    tip.style.left = "";
    tip.style.right = "";
    tip.style.top = "";
    tip.style.bottom = "";

    const TIP_W = 320;
    const TIP_H = 160; // 概算
    const GAP = 12;
    const vw = window.innerWidth;
    const vh = window.innerHeight;

    // 水平: 対象の左端に揃える (はみ出せば右端に寄せる)
    let tipLeft = rect.left;
    if (tipLeft + TIP_W > vw - 8) tipLeft = vw - TIP_W - 8;
    if (tipLeft < 8) tipLeft = 8;
    tip.style.left = `${tipLeft}px`;

    // 垂直: 対象の下優先、収まらなければ上
    const belowY = rect.bottom + pad + GAP;
    if (belowY + TIP_H <= vh - 8) {
        tip.style.top = `${belowY}px`;
    } else {
        const aboveY = rect.top - pad - GAP - TIP_H;
        tip.style.top = `${Math.max(8, aboveY)}px`;
    }
}

function startCoach(): void {
    showCoachStep(0);
}

function endCoach(): void {
    $("coach-overlay").hidden = true;
    localStorage.setItem(COACH_DONE_KEY, "1");
}

function maybeAutoCoach(): void {
    if (localStorage.getItem(COACH_DONE_KEY)) return;
    startCoach();
}

/** スタート画面の「テンプレートカード」を再描画する */
function renderStartTemplates(): void {
    const container = $("start-templates");
    container.innerHTML = "";
    for (const t of EKM_TEMPLATES) {
        const card = document.createElement("button");
        card.className = "start-tpl-card";
        card.innerHTML = `
            <span class="start-tpl-emoji">${t.emoji}</span>
            <span class="start-tpl-name">${t.name}</span>
            <span class="start-tpl-desc">${t.description}</span>
        `;
        card.addEventListener("click", () => void loadTemplate(t));
        container.appendChild(card);
    }
}

function showStartScreen(): void {
    renderStartTemplates();
    $("start-screen").hidden = false;
    startScreenShown = true;
}

function hideStartScreen(): void {
    $("start-screen").hidden = true;
    // 初回 dismiss 時にコーチを起動 (初回フラグを再利用)
    if (startScreenShown) {
        startScreenShown = false;
        maybeAutoCoach();
    }
}

// ---------- ミニゲーム (Crew Run) ----------
// 重いゲームコードは動的 import で本体バンドルから切り離す。
let minigameHandle: { destroy(): void } | null = null;
async function openMinigame(): Promise<void> {
    if (minigameHandle) return;
    const overlay = $("minigame-overlay");
    overlay.hidden = false;
    try {
        const { launchCrewRun } = await import("./minigame/crewrun");
        minigameHandle = await launchCrewRun(overlay, closeMinigame);
    } catch (err) {
        console.error("ミニゲームの起動に失敗しました", err);
        toast("ミニゲームを起動できませんでした");
        closeMinigame();
    }
}
function closeMinigame(): void {
    minigameHandle?.destroy();
    minigameHandle = null;
    const overlay = $("minigame-overlay");
    overlay.replaceChildren();
    overlay.hidden = true;
}

async function loadTemplate(t: EkmTemplate): Promise<void> {
    await backupAutosave();
    const author = doc.author ?? "";
    const json = t.buildJson(t.name, author);
    // 空の Backrooms は全セルが空 (-1) なので validateEkmapAny の spawn チェックで
    // ok=false になる。その場合は createBackroomsDoc 直呼びで setDocument する。
    if (t.id === "empty-backrooms") {
        selectedTilePerLayer = [0, 0, 0, 0];
        setDocument(createBackroomsDoc(32, 32, t.name, author));
        currentLibraryId = null;
        hideStartScreen();
        toast(`テンプレートから作りました: ${t.name}`);
        return;
    }
    if (loadFromJsonText(JSON.stringify(json))) {
        currentLibraryId = null;
        hideStartScreen();
        toast(`テンプレートから作りました: ${t.name}`);
    }
}

// ---------- 3ペインシェルの折りたたみレール ----------

/** 左レール / 右インスペクタの開閉を配線する。状態は localStorage に記憶 (既定は折りたたみ)。 */
function wireShellPanes(): void {
    const setup = (paneId: string, toggleId: string, key: string): void => {
        const pane = $(paneId);
        const toggle = $<HTMLButtonElement>(toggleId);
        const apply = (collapsed: boolean): void => {
            pane.classList.toggle("collapsed", collapsed);
            toggle.setAttribute("aria-expanded", String(!collapsed));
        };
        // 保存値が "open" のときだけ開く。未保存・"closed" は折りたたみ。
        apply(localStorage.getItem(key) !== "open");
        toggle.addEventListener("click", () => {
            const nextCollapsed = !pane.classList.contains("collapsed");
            apply(nextCollapsed);
            localStorage.setItem(key, nextCollapsed ? "closed" : "open");
            // viewport の幅が変わったので PIXI canvas を追従させる
            // (resizeTo は window resize でしか発火しないため明示的に再計算)
            renderer.app.resize();
            // 右インスペクタを開いたら最新状態に更新
            if (!nextCollapsed && paneId === "right-inspector") {
                refreshInspector();
                refreshInspectorValidation();
                refreshLayerList();
                refreshTileAttrs();
            }
        });
    };
    setup("left-rail", "left-rail-toggle", "ekm.leftRail");
    setup("right-inspector", "right-rail-toggle", "ekm.rightRail");
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
/** float セル座標のホバー位置 (影モードで始点/終点に使う) */
let lastHoverFloat: { x: number; y: number } | null = null;

function updateStatus(): void {
    const zoom = Math.round(renderer.world.scale.x * 100);
    const inGrid = lastHover && lastHover.x >= 0 && lastHover.y >= 0 && lastHover.x < doc.width && lastHover.y < doc.height;
    const cellTxt = inGrid && lastHover ? `(${lastHover.x}, ${lastHover.y})` : "--";
    const chipTxt = stampBlock && (stampBlock.w > 1 || stampBlock.h > 1)
        ? `ブロック ${stampBlock.w}×${stampBlock.h}`
        : `チップ ${getSelectedTile()}`;
    const v2Txt = isV3Doc(doc) ? ` | ${layerLabel(activeLayer)} | ${chipTxt}` : "";
    $("status").textContent = `${doc.width}×${doc.height} | セル ${cellTxt} | ${zoom}%${v2Txt} | decor ${doc.decor.length}/${MAX_DECOR}`;
}

function layerLabel(l: LayerTab): string {
    if (l === "ground") return "レイヤー1";
    if (l === "upper") return "レイヤー2";
    if (l === "layer3") return "レイヤー3";
    if (l === "layer4") return "レイヤー4";
    if (l === "decor") return "装飾";
    if (l === "shadow") return "影";
    return "スポーン";
}

/** アクティブレイヤー index (0〜3) — decor/spawn は描画層に影響しない */
function activeLayerIndex(): number {
    if (activeLayer === "upper") return 1;
    if (activeLayer === "layer3") return 2;
    if (activeLayer === "layer4") return 3;
    return 0;
}

/** アクティブレイヤーの選択チップ id を取得 */
function getSelectedTile(): number {
    return selectedTilePerLayer[activeLayerIndex()];
}

/** アクティブレイヤーの選択チップ id を更新 */
function setSelectedTile(id: number): void {
    selectedTilePerLayer[activeLayerIndex()] = id;
}

// ---------- 自動保存 (IndexedDB 単一スロット) ----------

let saveTimer: number | undefined;
let savePending = false;

function scheduleSave(): void {
    savePending = true;
    refreshInspector(); // 安価なフィールドは即時更新
    window.clearTimeout(saveTimer);
    saveTimer = window.setTimeout(() => {
        savePending = false;
        void saveAutosave(docToJsonAny(doc));
        void saveAutotiles(autotiles);
        refreshInspectorValidation(); // 検証はデバウンス内で
    }, 500);
}

// ---------- 右インスペクタ: マップ情報 ----------

/** マップ情報パネルの軽量フィールドを更新する (編集ごとに呼べる安価な処理)。 */
function refreshInspector(): void {
    $("info-name").textContent = doc.name || "(名前なし)";
    $("info-size").textContent = `${doc.width} × ${doc.height}`;
    $("info-format").textContent = isV3Doc(doc) ? "タイルセット式 (v3)" : "Backrooms式 (v1)";
    if (isV3Doc(doc)) {
        const tiles = doc.tilesets.reduce((sum, ts) => sum + tileCount(ts), 0);
        $("info-tiles").textContent = `${tiles} 個 / セット ${doc.tilesets.length}`;
    } else {
        $("info-tiles").textContent = "—";
    }
    $("info-decor").textContent = `${doc.decor.length} / ${MAX_DECOR}`;
    $("info-spawn").textContent = `(${doc.spawn.x}, ${doc.spawn.y})`;
}

/** 検証ステータスを更新する (やや重いので setDocument / 自動保存デバウンス内で呼ぶ)。 */
function refreshInspectorValidation(): void {
    const json = docToJsonAny(doc);
    const bytes = new TextEncoder().encode(JSON.stringify(json)).length;
    const r = validateEkmapAny(json, bytes);
    const el = $("info-valid");
    if (r.ok) {
        el.textContent = "OK";
        el.className = "info-valid-ok";
    } else {
        el.textContent = `NG: ${r.errors[0] ?? "エラー"}`;
        el.className = "info-valid-ng";
    }
}

// ---------- モード UI (v1 ツール列 ⇔ v2 レイヤータブ+ツール列) ----------

function isTileLayer(l: LayerTab): boolean {
    return l === "ground" || l === "upper" || l === "layer3" || l === "layer4";
}

function isShadowLayer(l: LayerTab): boolean {
    return l === "shadow";
}

function refreshModeUi(): void {
    const v3 = isV3Doc(doc);
    $("layer-tabs").hidden = !v3; // 旧フッタ横タブ (CSS で常時非表示だが状態は維持)
    $("layer-vtabs").hidden = !v3; // 新縦タブ: v3 のみ表示
    $("tools").hidden = v3;
    $("tools-v2").hidden = !v3 || !isTileLayer(activeLayer);
    $("tools-decor2").hidden = !v3 || activeLayer !== "decor";
    $("spawn-hint").hidden = !v3 || activeLayer !== "spawn";
    $("shadow-hint").hidden = !v3 || !isShadowLayer(activeLayer);
    $("shadow-snap-wrap").hidden = !v3 || !isShadowLayer(activeLayer);
    // ⚙ ボタン: アクティブなタイル層タブにのみ表示
    $("btn-layer-settings").hidden = !v3 || !isTileLayer(activeLayer);
    // オートタイルブラシ帯: v3 タイル層のみ表示
    $("autotile-strip").hidden = !v3 || !isTileLayer(activeLayer);
    // タブの「↑」バッジを更新
    if (v3) updateLayerAboveBadges(doc as import("./model").MapDocV3);
    updateRibbon();
    if (v3) void rebuildPicker();
}

/**
 * 各レイヤータブに above=true のバッジ (↑) を付け、ゴースト表示中は
 * 👤 区切り行を境に「前面 (above) を上 / 背面を下」に並べ替える。
 */
function updateLayerAboveBadges(d: import("./model").MapDocV3): void {
    const mapping: [LayerTab, number][] = [
        ["ground", 0], ["upper", 1], ["layer3", 2], ["layer4", 3],
    ];
    const tabs = new Map<LayerTab, HTMLButtonElement>();
    for (const [tabName, idx] of mapping) {
        const btn = document.querySelector<HTMLButtonElement>(`#layer-vtabs .lvtab[data-layer="${tabName}"]`);
        if (!btn) continue;
        const above = d.layers[idx]?.above ?? false;
        const baseLabel = tabName === "ground" ? "1" : tabName === "upper" ? "2" : tabName === "layer3" ? "3" : "4";
        btn.textContent = above ? `${baseLabel} ↑` : baseLabel;
        btn.title = above ? `レイヤー${baseLabel} (プレイヤーより上に描画)` : `レイヤー${baseLabel}`;
        btn.classList.toggle("above", above);
        tabs.set(tabName, btn);
    }

    // 👤 区切り行を含めた並べ替え。ゴースト表示中だけ前後で分け、それ以外は素直に 1→4。
    const divider = $("vtab-player");
    const anchor = $("btn-layer-settings"); // 区切り行・タブ群はこのボタンの直前にまとめる
    const container = $("layer-vtabs");
    const ghostOn = renderer.getGhostVisible();
    const above: HTMLElement[] = [];
    const below: HTMLElement[] = [];
    for (const [tabName, idx] of mapping) {
        const btn = tabs.get(tabName);
        if (!btn) continue;
        (ghostOn && (d.layers[idx]?.above ?? false) ? above : below).push(btn);
    }
    divider.hidden = !ghostOn;
    const order: HTMLElement[] = ghostOn ? [...above, divider, ...below] : [...above, ...below];
    for (const el of order) container.insertBefore(el, anchor);
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
    // 薄表示: decor/spawn/shadow は全層 100%、タイル層はアクティブ層だけ 100%
    renderer.setActiveLayerIndex(isTileLayer(l) ? activeLayerIndex() : null);
    // 影モード: 影タブ時は影線を濃く表示
    renderer.setShadowMode(isShadowLayer(l));
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

// ---------- 左レール: 道具リモコン ----------
// フッタの道具ボタン (.tool / .tool2 / .tool2d) を正本とし、左レールはその
// 「リモコン」として代理クリック + active ミラーするだけ。選択ロジックは二重化しない。

/** 現在フッタに表示されている道具群を左レールに鏡写しする。updateRibbon から毎回呼ばれる。 */
function refreshToolRail(): void {
    const rail = document.getElementById("left-rail-tools");
    if (!rail) return;
    // 表示中のフッタ道具セットを収集 (hidden でないものだけ)
    const srcButtons: HTMLButtonElement[] = [];
    for (const setId of ["tools", "tools-v2", "tools-decor2"]) {
        const set = document.getElementById(setId);
        if (!set || (set as HTMLElement).hidden) continue;
        for (const b of set.querySelectorAll<HTMLButtonElement>(".tool, .tool2, .tool2d")) {
            srcButtons.push(b);
        }
    }
    rail.innerHTML = "";
    for (const src of srcButtons) {
        const btn = document.createElement("button");
        btn.className = "rail-tool";
        btn.textContent = src.textContent;
        if (src.title) btn.title = src.title;
        btn.classList.toggle("active", src.classList.contains("active"));
        // 本物のフッタボタンを叩く → 正本ロジックが走り、updateRibbon が再び
        // refreshToolRail を呼んで active を更新する (自前同期は不要)。
        btn.addEventListener("click", () => src.click());
        rail.appendChild(btn);
    }
}

// ---------- 右インスペクタ: レイヤー一覧 ----------
// レイヤー縦タブ (#layer-vtabs .lvtab) を正本とし、右レールはそのリモコン。
// v3 マップのみレイヤーを持つ (v1 はレイヤーなし)。

/** レイヤー縦タブを右インスペクタに鏡写しする。updateRibbon から毎回呼ばれる。 */
function refreshLayerList(): void {
    const list = document.getElementById("right-layers");
    if (!list) return;
    list.innerHTML = "";
    if (!isV3Doc(doc)) {
        const note = document.createElement("p");
        note.className = "pane-placeholder";
        note.textContent = "このマップ形式にレイヤーはありません";
        list.appendChild(note);
        return;
    }
    for (const src of document.querySelectorAll<HTMLButtonElement>("#layer-vtabs .lvtab")) {
        const layer = src.dataset.layer as LayerTab;
        const row = document.createElement("button");
        row.className = "layer-row";
        // 縦タブは "1"/"装飾" など簡略表記なので、一覧では分かりやすい名前にする
        row.textContent = src.classList.contains("above") ? `${layerLabel(layer)} ↑` : layerLabel(layer);
        if (src.title) row.title = src.title;
        row.classList.toggle("active", src.classList.contains("active"));
        row.addEventListener("click", () => src.click());
        list.appendChild(row);
    }
}

// ---------- 右インスペクタ: チップ属性 (常駐編集) ----------
// 選択中チップの通行/上描画/遮光を、チップ設定ダイアログを開かずに編集する。
// 正本はタイルセットの TileAttr。cycleTileAttr と同じ副作用を踏襲する。

/** 選択中チップの属性を mutate し、描画・保存・各パネルへ反映する。 */
function applyTileAttrChange(mutate: (t: TileAttr) => void): void {
    if (!isV3Doc(doc) || !isTileLayer(activeLayer)) return;
    const id = getSelectedTile();
    const t = activeTileset(doc).tiles[id];
    if (!t) return;
    mutate(t);
    if ($<HTMLDialogElement>("dlg-tileset").open) drawTilesetPanel(); // ダイアログ併用時は同期
    renderer.redrawAll(); // ★バッジ / 通行オーバーレイに反映
    renderer.flush();
    scheduleSave();
    refreshTileAttrs();
}

/** チップ属性パネルを選択中チップの状態に更新する。updateRibbon / rebuildPicker から呼ばれる。 */
function refreshTileAttrs(): void {
    const controls = document.getElementById("tileattr-controls");
    const note = document.getElementById("tileattr-note");
    if (!controls || !note) return;
    // タイル層以外 (v1 / 装飾 / スポーン / 影) は編集不可 → 説明文のみ
    if (!isV3Doc(doc) || !isTileLayer(activeLayer)) {
        controls.hidden = true;
        note.hidden = false;
        return;
    }
    const id = getSelectedTile();
    const t = activeTileset(doc).tiles[id];
    if (!t) {
        controls.hidden = true;
        note.hidden = false;
        return;
    }
    controls.hidden = false;
    note.hidden = true;
    $("tileattr-id").textContent = String(id);
    const passLabel = t.pass === "o" ? "○ 通れる" : t.pass === "x" ? "× 壁" : "↓ 下層に従う";
    $("tileattr-pass").textContent = `通行: ${passLabel}`;
    const over = $("tileattr-over");
    over.textContent = `★ 上に描画: ${t.over ? "する" : "しない"}`;
    over.classList.toggle("on", t.over);
    const light = $("tileattr-light");
    light.textContent = `遮光: ${t.light ? "する" : "しない"}`;
    light.classList.toggle("on", t.light);
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
    refreshTileAttrs(); // 選択チップが変わったので属性パネルを更新
    if (!isV3Doc(doc)) return;
    const d = doc;
    const ts = activeTileset(d);
    const total = tileCount(ts);

    // 表示するチップid列: getSelectedTile() を中心に前後 STRIP_NEIGHBORS/2 ずつ
    const selTile = getSelectedTile();
    const half = Math.floor(STRIP_NEIGHBORS / 2);
    const start = Math.max(0, Math.min(selTile - half, total - STRIP_NEIGHBORS));
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
    const selIdx = ids.indexOf(selTile);
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
        ctx.fillStyle = id === selTile ? "#ffd75e" : "rgba(255,255,255,0.7)";
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

    const norm = filterText.trim();
    // 既定 (検索もフィルタも無し) は「元レイアウト保持」モード = BitGM 風。
    // 素材本来の列数・位置のまま全タイルを並べるので、窓などの複数マス物が崩れずに見える。
    const preserveLayout = norm === "" && !usedOnly;

    let filtered: number[];
    let cols: number;
    if (preserveLayout) {
        filtered = Array.from({ length: total }, (_, id) => id); // 全タイルを素材順のまま
        cols = Math.max(1, ts.columns);                          // 素材の列数で並べる (= 元の配置を再現)
    } else {
        // 検索 / 使用中フィルタ時は一覧性優先でコンパクトに詰める (探す用途)
        filtered = [];
        for (let id = 0; id < total; id++) {
            if (norm && !String(id).includes(norm)) continue;
            if (usedIds && !usedIds.has(id)) continue;
            filtered.push(id);
        }
        const wrap = $("palette-grid-wrap");
        const wrapW = wrap.clientWidth || window.innerWidth;
        cols = Math.max(1, Math.floor(wrapW / GRID_PX));
    }
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
        const curSel = getSelectedTile();
        ctx.strokeStyle = id === curSel ? "#ffd75e" : "rgba(255,255,255,0.18)";
        ctx.lineWidth = id === curSel ? 3 : 1;
        ctx.strokeRect(gx + 0.5, gy + 0.5, GRID_PX - 1, GRID_PX - 1);

        // 選択中は背景ハイライト
        if (id === curSel) {
            ctx.fillStyle = "rgba(255,215,94,0.18)";
            ctx.fillRect(gx + 1, gy + 1, GRID_PX - 2, GRID_PX - 2);
        }

        // 番号ラベル — レイアウト保持モードでは絵を隠さないよう控えめに (選択中のみ強調)
        if (preserveLayout && id !== curSel) {
            ctx.font = "9px sans-serif";
            ctx.textBaseline = "top";
            ctx.fillStyle = "rgba(0,0,0,0.55)";
            ctx.fillText(String(id), gx + 3, gy + 3);
            ctx.fillStyle = "rgba(255,255,255,0.55)";
            ctx.fillText(String(id), gx + 2, gy + 2);
        } else {
            ctx.fillStyle = "rgba(0,0,0,0.6)";
            ctx.fillRect(gx + 1, gy + 1, 26, 14);
            ctx.font = "bold 11px sans-serif";
            ctx.textBaseline = "top";
            ctx.fillStyle = id === curSel ? "#ffd75e" : "rgba(255,255,255,0.8)";
            ctx.fillText(String(id), gx + 3, gy + 2);
        }
    }

    // 何も無い場合はプレースホルダ
    if (filtered.length === 0) {
        ctx.fillStyle = "rgba(255,255,255,0.3)";
        ctx.font = "14px sans-serif";
        ctx.textAlign = "center";
        ctx.textBaseline = "middle";
        ctx.fillText("チップが見つかりません", cv.width / 2, cv.height / 2 || GRID_PX / 2);
    }

    // 範囲選択ドラッグの選択枠を毎フレーム重ねるためのスナップショットを保持
    paletteSnapshot ??= document.createElement("canvas");
    paletteSnapshot.width = cv.width;
    paletteSnapshot.height = cv.height;
    paletteSnapshot.getContext("2d")?.drawImage(cv, 0, 0);
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
/** パレットで範囲選択した「ブロックスタンプ」(複数チップを配置順のまま設置)。null = 単一チップ */
let stampBlock: { w: number; h: number; ids: number[] } | null = null;
/** 範囲選択ドラッグ中の選択枠を高速再描画するためのスナップショット */
let paletteSnapshot: HTMLCanvasElement | null = null;

function selectTile(id: number): void {
    if (!isV3Doc(doc)) return;
    stampBlock = null; // 単一チップを選んだらブロックスタンプは解除
    setSelectedTile(Math.min(Math.max(0, id), tileCount(activeTileset(doc)) - 1));
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
 * v3 アクティブレイヤーの cells 配列を返す (4 タブ対応)。
 */
function activeLayerArrV3(d: MapDocV3): number[] {
    return d.layers[activeLayerIndex()].cells;
}

/** v3 アクティブレイヤーの index (0〜3) を返す */
function activeLayerIndexV3(): number {
    return activeLayerIndex();
}

/**
 * v3 アクティブレイヤーが使用するタイルセットを返す。
 * layers[activeLayerIndex()].tileset が指す tilesets[i]。
 * タイルセットが無い場合は tilesets[0] にフォールバック。
 */
function activeTileset(d: MapDocV3): TilesetDoc {
    const li = activeLayerIndex();
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

/**
 * オートタイルスマートブラシで (x,y) を塗る/消す。
 * computeAutotileEdits が返す全セル (self + 影響を受けた近傍) を
 * paintCellV3 経由で適用 → Patch に全編集が記録され Undo で全セルが巻き戻る (§5.1)。
 */
/**
 * ブロックスタンプを (x,y) を左上に設置する。各セルは既存の paintCellV3 経由なので
 * Patch にまとめて記録され、Undo でブロック全体が一度に戻る。空きスロット (-1) は上書きしない。
 */
function stampBlockAt(d: MapDocV3, x: number, y: number): void {
    const blk = stampBlock;
    if (!blk) return;
    for (let r = 0; r < blk.h; r++) {
        for (let c = 0; c < blk.w; c++) {
            const id = blk.ids[r * blk.w + c];
            if (id < 0) continue;
            const tx = x + c, ty = y + r;
            if (tx < 0 || ty < 0 || tx >= d.width || ty >= d.height) continue;
            paintCellV3(d, tx, ty, id);
        }
    }
}

function paintAutotileV3(d: MapDocV3, x: number, y: number, mode: "paint" | "erase"): void {
    const def = activeAutotileIdx !== null ? autotiles[activeAutotileIdx] : null;
    if (!def) return;
    const layer = d.layers[activeLayerIndex()];
    const view = { width: d.width, height: d.height, cells: layer.cells };
    const edits = computeAutotileEdits(def, view, x, y, mode);
    for (const e of edits) {
        paintCellV3(d, e.x, e.y, e.id);
    }
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
            if (inGrid) {
                // オートタイルブラシが選択中かつアクティブ層のタイルセットが一致する場合はスマートブラシ
                // rect/bucket は通常動作のまま (autotile 中の rect/bucket は 1枚ペイントで据え置き)
                const atDef = activeAutotileIdx !== null ? autotiles[activeAutotileIdx] : null;
                if (atDef) {
                    const layerTsIdx = d.layers[activeLayerIndex()].tileset;
                    if (layerTsIdx === atDef.tileset) {
                        paintAutotileV3(d, x, y, "paint");
                    } else if (isStart) {
                        // タイルセット不一致: 塗らない (誤操作防止)。ドラッグ中の連続トーストは避ける
                        toast("このレイヤーは別のチップセットです");
                    }
                } else if (stampBlock && (stampBlock.w > 1 || stampBlock.h > 1)) {
                    // ブロックスタンプ: 範囲選択したチップ群を配置順のまま設置 (top-left = クリック位置)
                    stampBlockAt(d, x, y);
                } else {
                    paintCellV3(d, x, y, getSelectedTile());
                }
            }
            break;
        case "erase":
            if (inGrid) {
                // オートタイルブラシ中の消去も近傍を再解決する
                const atDefErase = activeAutotileIdx !== null ? autotiles[activeAutotileIdx] : null;
                if (atDefErase) {
                    const layerTsIdx = d.layers[activeLayerIndex()].tileset;
                    if (layerTsIdx === atDefErase.tileset) {
                        paintAutotileV3(d, x, y, "erase");
                    } else {
                        paintCellV3(d, x, y, -1);
                    }
                } else {
                    paintCellV3(d, x, y, -1);
                }
            }
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
            if (isStart && inGrid) floodFillV3(d, x, y, getSelectedTile());
            break;
        case "pick": {
            if (!isStart || !inGrid) break;
            const i = y * d.width + x;
            const curIdx = activeLayerIndex();
            const arr = activeLayerArrV3(d);
            // アクティブ層を優先、空なら上の層→下の層の順で探す
            let pickedTile = arr[i];
            let pickedLayerIdx = curIdx;
            if (pickedTile < 0) {
                // 上の層から降りてくる順で検索 (上位層優先)
                for (let li = d.layers.length - 1; li >= 0; li--) {
                    if (li === curIdx) continue;
                    const v = d.layers[li].cells[i];
                    if (v >= 0) { pickedTile = v; pickedLayerIdx = li; break; }
                }
            }
            if (pickedTile >= 0) {
                // 別の層から拾った場合はタブを自動切替
                if (pickedLayerIdx !== curIdx) {
                    const tabNames: LayerTab[] = ["ground", "upper", "layer3", "layer4"];
                    setActiveLayer(tabNames[pickedLayerIdx]);
                    toast(`レイヤー${pickedLayerIdx + 1} のチップ ${pickedTile} を選択しました`);
                } else {
                    toast(`チップ ${pickedTile} を選択しました`);
                }
                selectTile(pickedTile);
                setToolV2("pen");
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

// ---------- 影レイヤー ツール ----------

/** セル座標を 0.5 グリッドにスナップする (セル角/辺/中心) */
function snapShadow(v: number): number {
    return Math.round(v * 2) / 2;
}

/** 点 (px,py) から線分 (ax,ay)-(bx,by) までの距離の二乗 */
function pointSegDistSq(px: number, py: number, ax: number, ay: number, bx: number, by: number): number {
    const dx = bx - ax;
    const dy = by - ay;
    const len2 = dx * dx + dy * dy;
    if (len2 === 0) return (px - ax) ** 2 + (py - ay) ** 2;
    const t = Math.max(0, Math.min(1, ((px - ax) * dx + (py - ay) * dy) / len2));
    return (px - ax - t * dx) ** 2 + (py - ay - t * dy) ** 2;
}

/**
 * クリック点 (セル座標 float) に最も近い影線のインデックスを返す。
 * セル座標での距離が 0.5 以内なら選択対象。なければ -1。
 */
function findNearestShadowLine(cx: number, cy: number): number {
    if (!isV3Doc(doc) || !doc.shadow) return -1;
    const THRESHOLD_SQ = 0.5 * 0.5; // セル単位 0.5
    let best = -1;
    let bestDist = Infinity;
    for (let i = 0; i < doc.shadow.lines.length; i++) {
        const line = doc.shadow.lines[i];
        for (let j = 0; j < line.length - 2; j += 2) {
            const d = pointSegDistSq(cx, cy, line[j], line[j + 1], line[j + 2], line[j + 3]);
            if (d < THRESHOLD_SQ && d < bestDist) {
                bestDist = d;
                best = i;
            }
        }
    }
    return best;
}

/** 影線を追加してヒストリに積む */
function addShadowLine(sx: number, sy: number, ex: number, ey: number): void {
    if (!isV3Doc(doc)) return;
    if (Math.abs(ex - sx) < 0.01 && Math.abs(ey - sy) < 0.01) return; // 点なら追加しない
    const before = (doc.shadow?.lines ?? []).map((l) => [...l]);
    if (!doc.shadow) doc.shadow = { lines: [] };
    if (doc.shadow.lines.length >= SHADOW_MAX_LINES) {
        toast(`影線は最大 ${SHADOW_MAX_LINES} 本までです`);
        return;
    }
    doc.shadow.lines.push([sx, sy, ex, ey]);
    const after = doc.shadow.lines.map((l) => [...l]);
    history.push({ shadowBefore: before, shadowAfter: after });
    refreshUndoButtons();
    renderer.rebuildShadow();
    scheduleSave();
    updateStatus();
}

/** 選択中の影線を削除 */
function deleteSelectedShadowLine(): void {
    if (!isV3Doc(doc) || !doc.shadow || shadowSelectedIdx < 0) return;
    const before = doc.shadow.lines.map((l) => [...l]);
    doc.shadow.lines.splice(shadowSelectedIdx, 1);
    const after = doc.shadow.lines.map((l) => [...l]);
    history.push({ shadowBefore: before, shadowAfter: after });
    shadowSelectedIdx = -1;
    refreshUndoButtons();
    renderer.rebuildShadow();
    scheduleSave();
    updateStatus();
}

function onStrokeStart(x: number, y: number): void {
    // 影モード: float 座標でドラッグ開始 (整数 x,y は使わない)
    if (isV3Doc(doc) && activeLayer === "shadow") {
        const fp = lastHoverFloat;
        if (!fp) return;
        const sx = shadowSnap ? snapShadow(fp.x) : fp.x;
        const sy = shadowSnap ? snapShadow(fp.y) : fp.y;
        shadowDragStart = { x: sx, y: sy };
        renderer.setShadowPreview(sx, sy, sx, sy);
        return;
    }
    stroke = { cells: new Map(), decorBefore: null, spawnBefore: null };
    applyToolAt(x, y, true);
}

function onStrokeStep(x: number, y: number): void {
    // 影モード: ドラッグ中プレビューを更新 (整数 x,y は使わない)
    if (isV3Doc(doc) && activeLayer === "shadow") {
        if (!shadowDragStart) return;
        const fp = lastHoverFloat;
        if (!fp) return;
        const ex = shadowSnap ? snapShadow(fp.x) : fp.x;
        const ey = shadowSnap ? snapShadow(fp.y) : fp.y;
        renderer.setShadowPreview(shadowDragStart.x, shadowDragStart.y, ex, ey);
        return;
    }
    applyToolAt(x, y, false);
}

function onStrokeEnd(): void {
    // 影モード: ドラッグ確定 → 影線追加 or クリックで選択/削除
    if (isV3Doc(doc) && activeLayer === "shadow") {
        renderer.clearShadowPreview();
        if (!shadowDragStart) return;
        const fp = lastHoverFloat;
        const ex = fp ? (shadowSnap ? snapShadow(fp.x) : fp.x) : shadowDragStart.x;
        const ey = fp ? (shadowSnap ? snapShadow(fp.y) : fp.y) : shadowDragStart.y;
        const dx = ex - shadowDragStart.x;
        const dy = ey - shadowDragStart.y;
        const isDrag = dx * dx + dy * dy > 0.01; // 0.1 セル以上動いたらドラッグ
        if (isDrag) {
            addShadowLine(shadowDragStart.x, shadowDragStart.y, ex, ey);
        } else {
            // クリック: 近くの影線を選択/削除
            const idx = findNearestShadowLine(shadowDragStart.x, shadowDragStart.y);
            if (idx >= 0) {
                shadowSelectedIdx = idx;
                toast("影線を選択しました。Delete キーまたは右クリックで削除できます");
            } else {
                shadowSelectedIdx = -1;
            }
        }
        shadowDragStart = null;
        return;
    }
    if (!stroke) return;
    // 矩形ツール: ドラッグ確定 → 範囲を一括ペイント
    if (isV3Doc(doc) && rectDrag) {
        const d = doc;
        const x0 = Math.min(rectDrag.ax, rectDrag.cx);
        const x1 = Math.max(rectDrag.ax, rectDrag.cx);
        const y0 = Math.min(rectDrag.ay, rectDrag.cy);
        const y1 = Math.max(rectDrag.ay, rectDrag.cy);
        for (let y = y0; y <= y1; y++) {
            for (let x = x0; x <= x1; x++) paintCellV3(d, x, y, getSelectedTile());
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
    // 影モード: プレビュークリア
    if (isV3Doc(doc) && activeLayer === "shadow") {
        renderer.clearShadowPreview();
        shadowDragStart = null;
        return;
    }
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
    if (p.shadowBefore) renderer.rebuildShadow();
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

// ---------- tileScale UI (v3 専用) ----------

const TILE_SCALE_MIN = 0.5;
const TILE_SCALE_MAX = 1.0;
const TILE_SCALE_DEFAULT = 1.0;

/** v3 ドキュメントの ambient.tileScale をスライダーに反映する。v1 は非表示 */
function syncTileScaleUi(): void {
    const wrap = $("tile-scale-wrap");
    const slider = $<HTMLInputElement>("tile-scale");
    const val = $("tile-scale-val");
    if (!isV3Doc(doc)) {
        wrap.hidden = true;
        slider.disabled = true;
        return;
    }
    const current = typeof doc.ambient.tileScale === "number" ? (doc.ambient.tileScale as number) : TILE_SCALE_DEFAULT;
    const clamped = Math.min(TILE_SCALE_MAX, Math.max(TILE_SCALE_MIN, current));
    slider.value = String(clamped);
    val.textContent = clamped.toFixed(2) + "x";
    wrap.hidden = false;
    slider.disabled = false;
}

// ---------- ドキュメント切替 ----------

function setDocument(next: AnyDoc): void {
    doc = next;
    history.clear();
    stroke = null;
    rectDrag = null;
    renderer.clearRectPreview();
    // 別マップに切り替わったので書庫IDを破棄 (前のマップに誤って上書きしない)
    currentLibraryId = null;
    stampBlock = null; // 別マップのブロックスタンプは無効化
    // ドキュメント切替時にオートタイル状態をリセット
    autotiles = [];
    activeAutotileIdx = null;
    // DOM が存在する場合のみ更新 (boot 前にも呼ばれる可能性があるため getElementById チェック)
    if (document.getElementById("at-brush-select")) rebuildAutotileBrushSelect();
    if (isV3Doc(doc)) {
        activeLayer = "ground";
        // 各レイヤーの選択チップを tilecount にクランプ
        for (let li = 0; li < 4; li++) {
            const tsIdx = (doc as import("./model").MapDocV3).layers[li]?.tileset ?? 0;
            const ts = (doc as import("./model").MapDocV3).tilesets[tsIdx] ?? (doc as import("./model").MapDocV3).tilesets[0];
            const cnt = ts ? tileCount(ts) : 1;
            selectedTilePerLayer[li] = Math.min(selectedTilePerLayer[li], cnt - 1);
        }
        setActiveLayer("ground");
        setToolV2(toolV2);
    }
    renderer.setDoc(doc);
    renderer.fitView();
    $<HTMLInputElement>("map-name").value = doc.name;
    $<HTMLInputElement>("map-author").value = doc.author;
    syncTileScaleUi();
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
    // このタイルセットを使う全レイヤーの選択チップもクランプ
    for (let li = 0; li < d.layers.length; li++) {
        if (d.layers[li].tileset === tsIdx) {
            selectedTilePerLayer[li] = Math.min(selectedTilePerLayer[li], count - 1);
        }
    }
    history.clear(); // 旧 tilecount 前提のパッチは安全に適用できないため破棄
    refreshUndoButtons();
    renderer.setDoc(d);
    void rebuildPicker();
    drawTilesetPanel();
    scheduleSave();
    updateStatus();
    toast(cleared > 0 ? `差し替えました (範囲外になった ${cleared} セルを空にしました)` : "タイルセットを差し替えました");
}

// ---------- オートタイル UI ----------

/** ブラシ select を autotiles[] に合わせて再構築する */
function rebuildAutotileBrushSelect(): void {
    const sel = $<HTMLSelectElement>("at-brush-select");
    // 現在の選択を保存
    const prev = activeAutotileIdx;
    sel.replaceChildren();
    const opt0 = document.createElement("option");
    opt0.value = "-1";
    opt0.textContent = "通常";
    sel.appendChild(opt0);
    for (let i = 0; i < autotiles.length; i++) {
        const opt = document.createElement("option");
        opt.value = String(i);
        opt.textContent = `自動つなぎ: ${autotiles[i].name}`;
        sel.appendChild(opt);
    }
    // 選択を復元 (消えたブラシは null に戻す)
    if (prev !== null && prev < autotiles.length) {
        sel.value = String(prev);
        activeAutotileIdx = prev;
    } else {
        sel.value = "-1";
        activeAutotileIdx = null;
    }
}

/**
 * オートタイル定義の LUT 出力タイル全件 (+ fallback) に対して
 * blocksLight と band を tiles[] に焼き込む (§3 H2/H3)。
 * C# ローダーはこれを読んで BlocksLight OR と AboveMask を計算する。
 */
function bakeAutotileAttrs(d: MapDocV3, def: AutotileDef): void {
    const ts = d.tilesets[def.tileset];
    if (!ts) return;
    bakeAutotileTileAttrs(ts.tiles, def);
}

/** "#rrggbb" → {r,g,b}。失敗時はグレー */
function hexToRgb(hex: string): { r: number; g: number; b: number } {
    const m = /^#?([0-9a-fA-F]{6})$/.exec(hex.trim());
    if (!m) return { r: 160, g: 160, b: 160 };
    const n = parseInt(m[1], 16);
    return { r: (n >> 16) & 255, g: (n >> 8) & 255, b: n & 255 };
}

/**
 * 「色を選ぶだけ」自動つなぎ壁ジェネレータ。シート用意・サイズ合わせ・16スロット理解を全部スキップする。
 * 16通りのつなぎ目壁を**マップと同じタイルサイズで生成** → 現在のダイアログ選択セットに合体 →
 * LUT を連番で自動割当 → light/over をベイク → 自動つなぎブラシとして登録してすぐ使える状態にする。
 */
async function createWallAutotileFromColor(hex: string, name: string): Promise<void> {
    if (!isV3Doc(doc)) return;
    const d = doc;
    const tsIdx = Math.max(0, Math.min(atWorkDef.tileset, d.tilesets.length - 1));
    const ts = d.tilesets[tsIdx];
    if (!ts) return;
    const { tileSize, columns, rows } = ts;

    // 上限チェック (16 枚追加)
    if (tileCount(ts) + EDGE4_SLOTS > MAX_TILECOUNT) {
        showMessages("自動つなぎ壁を作れません", [
            `チップ数が上限 ${MAX_TILECOUNT} を超えます。別の (空きのある) セットを選んでください。`,
        ], []);
        return;
    }

    // 1. マップと同じ tileSize で 16 タイルを生成
    const sheet = generateEdge4WallSheet(tileSize, hexToRgb(hex));

    // 2. 現セットへ合体 (既存 id 不変・末尾に 16 枚)
    const atlasRgba = await getPngRgba(ts.image);
    if (!atlasRgba) { toast("タイルセット画像を読み込めませんでした"); return; }
    const baseCount = columns * rows;
    const merged = appendSheetToAtlas(atlasRgba.rgba, columns, rows, tileSize, sheet.rgba, sheet.cols, sheet.rows);
    const newImageUri = rgbaToPngDataUri(merged.rgba, columns * tileSize, merged.rows * tileSize);

    // 3. サイズ検証
    const approxBytes = Math.floor((newImageUri.length - PNG_DATA_URI_PREFIX.length) * 3 / 4);
    if (approxBytes > MAX_TILESET_IMAGE_BYTES || JSON.stringify({ image: newImageUri }).length > MAX_JSON_BYTES_V2) {
        showMessages("自動つなぎ壁を作れません", ["生成後のタイルセットが容量上限を超えます。別のセットを使うか素材を減らしてください。"], []);
        return;
    }

    // 4. tileset 差し替え (tiles[] を dense に拡張)
    const newTiles = [...ts.tiles];
    while (newTiles.length < columns * merged.rows) newTiles.push(defaultTileAttr());
    d.tilesets[tsIdx] = { tileSize, columns, rows: merged.rows, image: newImageUri, tiles: newTiles };

    // 5. 自動つなぎ定義を作る (tile index == code なので lut[code]=baseCount+code で一発)
    const def = createEdge4Def(name.trim() || "壁", tsIdx);
    def.blocksLight = true;
    def.band = "ground";
    for (let code = 0; code < EDGE4_SLOTS; code++) def.lut[code] = baseCount + code;
    def.fallback = baseCount;
    bakeAutotileAttrs(d, def);

    // 6. 登録 + アクティブ化 + 反映
    autotiles.push(def);
    activeAutotileIdx = autotiles.length - 1;
    history.clear();
    refreshUndoButtons();
    renderer.setDoc(d);
    void rebuildPicker();
    drawTilesetPanel();
    rebuildAutotileBrushSelect();
    $<HTMLSelectElement>("at-brush-select").value = String(activeAutotileIdx);
    refreshModeUi();
    scheduleSave();
    updateStatus();
    $<HTMLDialogElement>("dlg-autotile").close();
    toast(`自動つなぎ壁「${def.name}」を作りました。ペンで塗るとつながります！`);
}

// ── オートタイルダイアログ内部状態 ──

/** ダイアログ編集中の作業コピー */
let atWorkDef: AutotileDef = createEdge4Def("壁", 0);
/** 編集中のオートタイルインデックス (-1 = 新規追加) */
let atEditIdx = -1;
/** 現在クリックで選択中のスロットインデックス (null = 未選択) */
let atSelectedSlot: number | null = null;
/** fallbackスロットが選択されているか */
let atFallbackSelected = false;

const AT_SLOT_PX = 52; // スロットサムネサイズ
const AT_THUMB_PX = 40; // サムネ内タイル描画サイズ

/** 16 スロットパネルを再描画する */
async function drawAutotileSlotPanel(): Promise<void> {
    if (!isV3Doc(doc)) return;
    const d = doc;
    const ts = d.tilesets[atWorkDef.tileset];
    if (!ts) return;

    const panel = $("at-slot-panel");
    // 既存のボタンを再利用 or 生成
    if (panel.children.length !== EDGE4_SLOTS) {
        panel.replaceChildren();
        for (let code = 0; code < EDGE4_SLOTS; code++) {
            const btn = document.createElement("button");
            btn.type = "button";
            btn.className = "at-slot-btn";
            btn.dataset.slotCode = String(code);
            panel.appendChild(btn);
        }
    }

    let img: HTMLImageElement | null = null;
    try { img = await loadTilesetImage(ts.image); } catch { /* placeholder */ }
    if (doc !== d) return;

    for (let code = 0; code < EDGE4_SLOTS; code++) {
        const btn = panel.children[code] as HTMLButtonElement;
        const tileId = atWorkDef.lut[code];
        const isSelected = atSelectedSlot === code && !atFallbackSelected;

        // スロット背景
        btn.innerHTML = "";
        btn.className = "at-slot-btn" + (isSelected ? " at-slot-selected" : "");

        // 接続方向ピクトグラム (SVG インライン)
        const n = (code & 1) !== 0;
        const e = (code & 2) !== 0;
        const s = (code & 4) !== 0;
        const w = (code & 8) !== 0;
        const picto = buildConnectionPicto(n, e, s, w, AT_SLOT_PX);
        btn.appendChild(picto);

        // タイルサムネ (割当済みの場合)
        if (tileId >= 0 && img) {
            const cv = document.createElement("canvas");
            cv.width = AT_THUMB_PX;
            cv.height = AT_THUMB_PX;
            cv.className = "at-slot-tile-thumb";
            const ctx = cv.getContext("2d");
            if (ctx) {
                ctx.imageSmoothingEnabled = false;
                const sx = (tileId % ts.columns) * ts.tileSize;
                const sy = Math.floor(tileId / ts.columns) * ts.tileSize;
                ctx.drawImage(img, sx, sy, ts.tileSize, ts.tileSize, 0, 0, AT_THUMB_PX, AT_THUMB_PX);
            }
            btn.appendChild(cv);
        }

        // スロット番号
        const lbl = document.createElement("span");
        lbl.className = "at-slot-code";
        lbl.textContent = String(code);
        btn.appendChild(lbl);
    }

    // fallback スロット
    const fbCv = $<HTMLCanvasElement>("at-fallback-canvas");
    fbCv.width = AT_THUMB_PX;
    fbCv.height = AT_THUMB_PX;
    fbCv.style.width = `${AT_THUMB_PX}px`;
    fbCv.style.height = `${AT_THUMB_PX}px`;
    fbCv.className = "at-slot-thumb" + (atFallbackSelected ? " at-slot-selected" : "");
    const fbCtx = fbCv.getContext("2d");
    if (fbCtx) {
        fbCtx.fillStyle = "#14141a";
        fbCtx.fillRect(0, 0, AT_THUMB_PX, AT_THUMB_PX);
        if (atWorkDef.fallback >= 0 && img) {
            const sx = (atWorkDef.fallback % ts.columns) * ts.tileSize;
            const sy = Math.floor(atWorkDef.fallback / ts.columns) * ts.tileSize;
            fbCtx.imageSmoothingEnabled = false;
            fbCtx.drawImage(img, sx, sy, ts.tileSize, ts.tileSize, 0, 0, AT_THUMB_PX, AT_THUMB_PX);
        } else {
            fbCtx.fillStyle = "rgba(255,255,255,0.2)";
            fbCtx.font = "10px sans-serif";
            fbCtx.textAlign = "center";
            fbCtx.textBaseline = "middle";
            fbCtx.fillText("なし", AT_THUMB_PX / 2, AT_THUMB_PX / 2);
        }
    }
}

/**
 * 接続方向 (N/E/S/W) を示す矢印ピクトグラム SVG を生成する。
 * 接続あり → 太い実線、接続なし → 薄い点線で視覚的に区別。
 */
function buildConnectionPicto(n: boolean, e: boolean, s: boolean, w: boolean, size: number): SVGSVGElement {
    const svgNS = "http://www.w3.org/2000/svg";
    const svg = document.createElementNS(svgNS, "svg");
    svg.setAttribute("width", String(size));
    svg.setAttribute("height", String(size));
    svg.setAttribute("viewBox", `0 0 ${size} ${size}`);
    svg.classList.add("at-picto");
    const cx = size / 2;
    const cy = size / 2;
    const r = size * 0.12;

    // 中心円
    const circle = document.createElementNS(svgNS, "circle");
    circle.setAttribute("cx", String(cx));
    circle.setAttribute("cy", String(cy));
    circle.setAttribute("r", String(r));
    circle.setAttribute("fill", "#8888aa");
    svg.appendChild(circle);

    // 方向線
    const dirs: Array<[boolean, number, number, number, number]> = [
        [n, cx, cy - r, cx, size * 0.08],
        [e, cx + r, cy, size * 0.92, cy],
        [s, cx, cy + r, cx, size * 0.92],
        [w, cx - r, cy, size * 0.08, cy],
    ];
    for (const [on, x1, y1, x2, y2] of dirs) {
        const line = document.createElementNS(svgNS, "line");
        line.setAttribute("x1", String(x1));
        line.setAttribute("y1", String(y1));
        line.setAttribute("x2", String(x2));
        line.setAttribute("y2", String(y2));
        line.setAttribute("stroke", on ? "#ffd75e" : "rgba(255,255,255,0.18)");
        line.setAttribute("stroke-width", on ? "3" : "1");
        line.setAttribute("stroke-dasharray", on ? "none" : "3,3");
        svg.appendChild(line);
    }
    return svg;
}

/** オートタイルプレビュー canvas を更新する (中央+上下左右の 5 パターン) */
async function drawAutotilePreview(): Promise<void> {
    if (!isV3Doc(doc)) return;
    const d = doc;
    const ts = d.tilesets[atWorkDef.tileset];
    if (!ts) return;
    const cv = $<HTMLCanvasElement>("at-preview-canvas");
    const CELL = 36;
    const COLS = 5;
    const ROWS = 5;
    cv.width = COLS * CELL;
    cv.height = ROWS * CELL;
    cv.style.width = `${cv.width}px`;
    cv.style.height = `${cv.height}px`;
    const ctx = cv.getContext("2d");
    if (!ctx) return;
    ctx.imageSmoothingEnabled = false;
    ctx.fillStyle = "#14141a";
    ctx.fillRect(0, 0, cv.width, cv.height);

    // プレビュー用 5×5 グリッド: 中央 3×3 に壁を置いてみる
    const previewCells = new Array(COLS * ROWS).fill(-1);
    // 中央 3×3 をメンバーとして置く
    const positions = [[1,1],[2,1],[3,1],[1,2],[2,2],[3,2],[1,3],[2,3],[3,3]];
    for (const [px, py] of positions) {
        const n2 = py > 0 && positions.some(([a,b]) => a === px && b === py - 1);
        const e2 = px < COLS-1 && positions.some(([a,b]) => a === px+1 && b === py);
        const s2 = py < ROWS-1 && positions.some(([a,b]) => a === px && b === py + 1);
        const w2 = px > 0 && positions.some(([a,b]) => a === px-1 && b === py);
        const tileId = resolveEdge4(atWorkDef, edge4Code(n2, e2, s2, w2));
        previewCells[py * COLS + px] = tileId;
    }

    let img: HTMLImageElement | null = null;
    try { img = await loadTilesetImage(ts.image); } catch { /* placeholder */ }
    if (doc !== d) return;

    for (let i = 0; i < COLS * ROWS; i++) {
        const id = previewCells[i];
        if (id < 0 || !img) continue;
        const gx = (i % COLS) * CELL;
        const gy = Math.floor(i / COLS) * CELL;
        const sx = (id % ts.columns) * ts.tileSize;
        const sy = Math.floor(id / ts.columns) * ts.tileSize;
        ctx.drawImage(img, sx, sy, ts.tileSize, ts.tileSize, gx, gy, CELL, CELL);
    }
    // グリッド線
    ctx.strokeStyle = "rgba(255,255,255,0.1)";
    ctx.lineWidth = 1;
    for (let c = 0; c <= COLS; c++) { ctx.beginPath(); ctx.moveTo(c * CELL, 0); ctx.lineTo(c * CELL, cv.height); ctx.stroke(); }
    for (let r = 0; r <= ROWS; r++) { ctx.beginPath(); ctx.moveTo(0, r * CELL); ctx.lineTo(cv.width, r * CELL); ctx.stroke(); }
}

/** ダイアログ内のチップセット select を再構築する */
function rebuildAtTilesetSelect(): void {
    if (!isV3Doc(doc)) return;
    const d = doc;
    const sel = $<HTMLSelectElement>("at-tileset-select");
    sel.replaceChildren();
    for (let i = 0; i < d.tilesets.length; i++) {
        const opt = document.createElement("option");
        opt.value = String(i);
        opt.textContent = `セット ${i + 1} (${tileCount(d.tilesets[i])} チップ)`;
        sel.appendChild(opt);
    }
    sel.value = String(Math.min(atWorkDef.tileset, d.tilesets.length - 1));
}

/** オートタイル設定ダイアログを開く (新規 or 既存編集) */
async function openAutotileDialog(editIdx = -1): Promise<void> {
    if (!isV3Doc(doc)) return;
    atEditIdx = editIdx;
    atSelectedSlot = null;
    atFallbackSelected = false;

    if (editIdx >= 0 && editIdx < autotiles.length) {
        // 既存定義の編集: ディープコピー
        const src = autotiles[editIdx];
        atWorkDef = { ...src, lut: [...src.lut] };
    } else {
        // 新規作成
        const li = activeLayerIndex();
        const tsIdx = (doc as import("./model").MapDocV3).layers[li]?.tileset ?? 0;
        atWorkDef = createEdge4Def("壁", tsIdx);
    }

    $<HTMLInputElement>("at-name-input").value = atWorkDef.name;
    $<HTMLInputElement>("at-blocks-light").checked = atWorkDef.blocksLight;
    const bandInputs = document.querySelectorAll<HTMLInputElement>("input[name='at-band']");
    for (const inp of bandInputs) inp.checked = inp.value === atWorkDef.band;

    rebuildAtTilesetSelect();
    await drawAutotileSlotPanel();
    await drawAtTilePickerPanel();
    await drawAutotilePreview();

    $("dlg-autotile-title").textContent = editIdx >= 0 ? `自動つなぎブラシ「${atWorkDef.name}」を編集` : "自動つなぎブラシを新規作成";
    $<HTMLDialogElement>("dlg-autotile").showModal();
}

/** パレット canvas のクリックで LUT スロット or fallback に tileId を割当 */
function assignTileToActiveSlot(tileId: number): void {
    if (atFallbackSelected) {
        atWorkDef.fallback = tileId;
        atFallbackSelected = false;
    } else if (atSelectedSlot !== null) {
        const assigned = atSelectedSlot;
        atWorkDef.lut[assigned] = tileId;
        // 連続割当を楽にするため、割り当てたら自動で次のスロットへ進む (16 個目で解除)
        atSelectedSlot = assigned + 1 < EDGE4_SLOTS ? assigned + 1 : null;
    } else {
        return;
    }
    void drawAutotileSlotPanel();
    void drawAutotilePreview();
}

/**
 * チップ 0〜15 をコード 0〜15 にそのまま順番割当する時短 (コード順に並んだ専用シート向け)。
 * 拾い物シートで並びが違う場合は、そのあと個別スロットを直せばよい。
 */
function fillSequentialLut(): void {
    if (!isV3Doc(doc)) return;
    const ts = doc.tilesets[atWorkDef.tileset];
    if (!ts) return;
    const total = tileCount(ts);
    if (total < EDGE4_SLOTS) {
        toast(`このチップセットは ${total} 枚です。連番割当には 16 枚必要です`);
        return;
    }
    for (let code = 0; code < EDGE4_SLOTS; code++) atWorkDef.lut[code] = code;
    if (atWorkDef.fallback < 0) atWorkDef.fallback = 0;
    atSelectedSlot = null;
    void drawAutotileSlotPanel();
    void drawAutotilePreview();
    toast("チップ 0〜15 を順番に割り当てました (違う所は個別に直せます)");
}

/** ダイアログ内のタイル選択パネル (ts-canvas 流用) */
async function drawAtTilePickerPanel(): Promise<void> {
    if (!isV3Doc(doc)) return;
    const d = doc;
    const ts = d.tilesets[atWorkDef.tileset];
    if (!ts) return;
    // dlg-tileset の ts-canvas を流用して描画
    // 代わりに専用 canvas を使う (at-tile-picker-canvas)
    const cv = $<HTMLCanvasElement>("at-tile-picker-canvas");
    if (!cv) return;
    const PX = 40;
    const total = tileCount(ts);
    const cols = ts.columns;
    const rows = ts.rows;
    cv.width = cols * PX;
    cv.height = rows * PX;
    cv.style.width = `${cv.width}px`;
    cv.style.height = `${cv.height}px`;
    const ctx = cv.getContext("2d");
    if (!ctx) return;
    ctx.imageSmoothingEnabled = false;
    ctx.fillStyle = "#14141a";
    ctx.fillRect(0, 0, cv.width, cv.height);

    let img: HTMLImageElement | null = null;
    try { img = await loadTilesetImage(ts.image); } catch { /* placeholder */ }
    if (doc !== d) return;

    for (let id = 0; id < total; id++) {
        const gx = (id % cols) * PX;
        const gy = Math.floor(id / cols) * PX;
        if (img) {
            const sx = (id % ts.columns) * ts.tileSize;
            const sy = Math.floor(id / ts.columns) * ts.tileSize;
            ctx.drawImage(img, sx, sy, ts.tileSize, ts.tileSize, gx, gy, PX, PX);
        }
        ctx.strokeStyle = "rgba(255,255,255,0.18)";
        ctx.lineWidth = 1;
        ctx.strokeRect(gx + 0.5, gy + 0.5, PX - 1, PX - 1);
    }
}

// ---------- レイヤー設定ダイアログ (dlg-layer) ----------

/** レイヤー設定ダイアログを開く (アクティブなタイル層専用) */
async function openLayerDialog(): Promise<void> {
    if (!isV3Doc(doc)) return;
    const d = doc;
    const li = activeLayerIndex();
    const layer = d.layers[li];

    $("dlg-layer-title").textContent = `レイヤー${li + 1} の設定`;

    // above チェックボックス
    ($<HTMLInputElement>("dlg-layer-above")).checked = layer.above;

    // タイルセット選択 (サムネ + ラジオ)
    await buildLayerTilesetPicker(d, li);

    $<HTMLDialogElement>("dlg-layer").showModal();
}

/** dlg-layer のタイルセット選択UI を構築する */
async function buildLayerTilesetPicker(d: MapDocV3, layerIdx: number): Promise<void> {
    const container = $("dlg-layer-ts-list");
    container.replaceChildren();

    for (let ti = 0; ti < d.tilesets.length; ti++) {
        const ts = d.tilesets[ti];
        const isActive = d.layers[layerIdx].tileset === ti;

        const label = document.createElement("label");
        label.className = "layer-ts-item" + (isActive ? " active" : "");

        const radio = document.createElement("input");
        radio.type = "radio";
        radio.name = "layer-ts-radio";
        radio.value = String(ti);
        radio.checked = isActive;
        radio.className = "layer-ts-radio";

        // サムネ canvas
        const cv = document.createElement("canvas");
        cv.width = 48;
        cv.height = 48;
        cv.className = "layer-ts-thumb";
        const ctx = cv.getContext("2d");
        if (ctx) {
            ctx.imageSmoothingEnabled = false;
            ctx.fillStyle = "#14141a";
            ctx.fillRect(0, 0, 48, 48);
            try {
                const img = await loadTilesetImage(ts.image);
                // 先頭タイルをサムネとして表示
                ctx.drawImage(img, 0, 0, ts.tileSize, ts.tileSize, 0, 0, 48, 48);
            } catch { /* 画像未ロードは placeholder */ }
        }

        const info = document.createElement("span");
        info.className = "layer-ts-info";
        info.textContent = `セット ${ti + 1}`;

        label.appendChild(radio);
        label.appendChild(cv);
        label.appendChild(info);

        radio.addEventListener("change", () => {
            if (!isV3Doc(doc)) return;
            const d2 = doc;
            d2.layers[layerIdx].tileset = ti;
            // 範囲外チップ数を警告
            const newTs = d2.tilesets[ti];
            const cnt = tileCount(newTs);
            let outOfRange = 0;
            for (const v of d2.layers[layerIdx].cells) if (v >= cnt) outOfRange++;
            // 選択チップをクランプ
            selectedTilePerLayer[layerIdx] = Math.min(selectedTilePerLayer[layerIdx], cnt - 1);
            history.clear();
            refreshUndoButtons();
            renderer.setDoc(d2);
            void rebuildPicker();
            drawTilesetPanel();
            refreshModeUi();
            scheduleSave();
            updateStatus();
            // ラジオ全体の active クラスを更新
            for (const el of container.querySelectorAll<HTMLElement>(".layer-ts-item")) {
                el.classList.toggle("active", el.querySelector<HTMLInputElement>("input[type=radio]")?.checked ?? false);
            }
            if (outOfRange > 0) {
                toast(`セットを変更しました。このレイヤーに新セットの範囲外チップが ${outOfRange} 個あります`);
            } else {
                toast(`レイヤー${layerIdx + 1} のタイルセットをセット${ti + 1}に変更しました`);
            }
        });

        container.appendChild(label);
    }

    // 「新しいセットを追加…」ボタン
    const addBtn = document.createElement("button");
    addBtn.type = "button";
    addBtn.className = "layer-ts-add-btn";
    addBtn.textContent = d.tilesets.length >= MAX_TILESETS ? `セット上限 (${MAX_TILESETS}) に達しています` : "+ 新しいセットを追加…";
    addBtn.disabled = d.tilesets.length >= MAX_TILESETS;
    addBtn.addEventListener("click", () => {
        // dlg-layer を閉じてウィザード (dlg-new) を開く → 新規ドキュメント作成ではなくプール追加モードで使う
        $<HTMLDialogElement>("dlg-layer").close();
        openAddTilesetWizard();
    });
    container.appendChild(addBtn);
}

/** タイルセット追加ウィザードをプールへの追加モードで開く */
function openAddTilesetWizard(): void {
    if (!isV3Doc(doc)) return;
    if (doc.tilesets.length >= MAX_TILESETS) {
        toast(`タイルセットは最大 ${MAX_TILESETS} 個までです`);
        return;
    }
    // wizState をリセットしてダイアログを開く (addToPool フラグで完了時の動作を変える)
    wizReset();
    addTilesetToPool = true;
    $("wiz-add-pool-note").hidden = false;
    $<HTMLDialogElement>("dlg-new-tileset").showModal();
}

/** ウィザードをタイルセットプール追加モードで使うとき true */
let addTilesetToPool = false;

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

/** 現在編集中のマップが書庫に保存されている場合の書庫 ID。Ctrl+S / 保存ボタンの上書き先 */
let currentLibraryId: string | null = null;

/**
 * マップを書庫 (IndexedDB) に保存する。
 * - currentLibraryId があり !forceNew なら無言で上書き。
 * - 無ければプロンプトで名前を聞いて新規保存。
 * @param forceNew true で必ず新規として名前を聞く
 */
async function saveToLibrary(forceNew: boolean): Promise<void> {
    // 検証: buildValidatedJson が null なら NG (メッセージ済み)
    if (buildValidatedJson(true) === null) return;

    let id: string;
    let name: string;
    const isOverwrite = currentLibraryId !== null && !forceNew;

    if (isOverwrite) {
        // 既存スロットへの上書き
        id = currentLibraryId!;
        name = doc.name;
    } else {
        // 新規: 名前を聞く
        const input = window.prompt("書庫に保存する名前", doc.name);
        if (input === null || input.trim() === "") {
            toast("保存をキャンセルしました");
            return;
        }
        name = input.trim();
        id = crypto.randomUUID();
    }

    try {
        await putLibrary({ id, name, author: doc.author ?? "", updatedAt: Date.now(), doc: docToJsonAny(doc) });
        currentLibraryId = id;
        toast(isOverwrite ? "書庫に保存しました" : `書庫に保存しました (${name})`);
    } catch (e) {
        toast(`書庫への保存に失敗しました: ${(e as Error).message}`);
    }
}

/** 書庫ダイアログを開き、保存済みマップの一覧を表示する */
async function openLibraryDialog(): Promise<void> {
    const dlg = $<HTMLDialogElement>("dlg-library");
    await renderLibraryList();
    dlg.showModal();
}

/** 書庫一覧を再描画する */
async function renderLibraryList(): Promise<void> {
    const container = $("library-list");
    const entries = await listLibrary();
    container.innerHTML = "";

    if (entries.length === 0) {
        const empty = document.createElement("p");
        empty.className = "note";
        empty.textContent = "保存されたマップはありません";
        container.appendChild(empty);
        return;
    }

    for (const entry of entries) {
        const row = document.createElement("div");
        row.className = "library-row";

        const info = document.createElement("div");
        info.className = "library-info";

        const title = document.createElement("span");
        title.className = "library-name";
        title.textContent = entry.name;
        info.appendChild(title);

        const meta = document.createElement("span");
        meta.className = "library-meta";
        const authorPart = entry.author ? ` / ${entry.author}` : "";
        meta.textContent = `${new Date(entry.updatedAt).toLocaleString()}${authorPart}`;
        info.appendChild(meta);

        const btns = document.createElement("div");
        btns.className = "library-btns";

        const loadBtn = document.createElement("button");
        loadBtn.textContent = "読込";
        loadBtn.addEventListener("click", () => void (async () => {
            const e = await getLibrary(entry.id);
            if (!e) { toast("エントリが見つかりません"); return; }
            await backupAutosave();
            if (loadFromJsonText(JSON.stringify(e.doc))) {
                currentLibraryId = e.id;
                $<HTMLDialogElement>("dlg-library").close();
                toast(`読み込みました: ${e.name}`);
            }
        })());

        const dupBtn = document.createElement("button");
        dupBtn.textContent = "複製";
        dupBtn.addEventListener("click", () => void (async () => {
            const e = await getLibrary(entry.id);
            if (!e) { toast("エントリが見つかりません"); return; }
            await putLibrary({ id: crypto.randomUUID(), name: `${e.name} のコピー`, author: e.author, updatedAt: Date.now(), doc: e.doc });
            await renderLibraryList();
            toast(`複製しました: ${e.name} のコピー`);
        })());

        const delBtn = document.createElement("button");
        delBtn.textContent = "削除";
        delBtn.addEventListener("click", () => void (async () => {
            if (!window.confirm(`「${entry.name}」を書庫から削除しますか?`)) return;
            await deleteLibrary(entry.id);
            if (currentLibraryId === entry.id) currentLibraryId = null;
            await renderLibraryList();
        })());

        btns.appendChild(loadBtn);
        btns.appendChild(dupBtn);
        btns.appendChild(delBtn);
        row.appendChild(info);
        row.appendChild(btns);
        container.appendChild(row);
    }
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

/**
 * スライス UI のスコープ識別子。
 * "new"  = dlg-new (wiz- プレフィックス)
 * "pool" = dlg-new-tileset (nts- プレフィックス)
 */
type WizScope = "new" | "pool";

/**
 * スコープに応じたスライス UI 要素の id を返す小ヘルパー。
 * key は "size" | "margin" | "spacing" | "candidates" |
 *        "slice-canvas" | "stats" | "rebake-note"
 */
function wizEl(scope: WizScope, key: string): string {
    return scope === "pool" ? `nts-${key}` : `wiz-${key}`;
}

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

    // プール側 (dlg-new-tileset) のスライスセクションも隠す
    $("new-ts-drop-zone").hidden = false;
    $("new-ts-preview-wrap").hidden = true;
    $("nts-slice-section").hidden = true;
    $<HTMLButtonElement>("new-ts-ok-btn").disabled = true;
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

    // プール追加モード: dlg-new-tileset 側のスライス UI も更新する
    if (addTilesetToPool) {
        wizUpdateDropZoneForPool();
        $<HTMLButtonElement>("new-ts-ok-btn").disabled = false;
        wizUpdateCandidateChips("pool");
        wizSyncInputsFromParams("pool");
        wizUpdateSlicePreview("pool");
        wizUpdateStats("pool");
        $("nts-slice-section").hidden = false;
    }
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

/** dlg-new-tileset のドロップゾーンをウィザード状態に合わせて更新 */
function wizUpdateDropZoneForPool(): void {
    const hasFile = wizState.dataUri !== null;
    $("new-ts-drop-zone").hidden = hasFile;
    $("new-ts-preview-wrap").hidden = !hasFile;
    if (hasFile) {
        const cv = $<HTMLCanvasElement>("new-ts-preview-canvas");
        const img = new Image();
        img.onload = () => {
            const MAX_W = 200;
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
        $("new-ts-preview-info").textContent =
            `${wizState.file?.name ?? ""} — ${wizState.pngWidth}×${wizState.pngHeight}px　[クリックで変更]`;
    }
}

/** 候補チップを描画 (scope: "new"=dlg-new, "pool"=dlg-new-tileset) */
function wizUpdateCandidateChips(scope: WizScope = "new"): void {
    const container = $(wizEl(scope, "candidates"));
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
            wizUpdateCandidateChips(scope);
            wizSyncInputsFromParams(scope);
            wizUpdateSlicePreview(scope);
            wizUpdateStats(scope);
        });
        container.appendChild(chip);
    }
}

/** 手動入力欄に現在の params を反映 (scope: "new"=dlg-new, "pool"=dlg-new-tileset) */
function wizSyncInputsFromParams(scope: WizScope = "new"): void {
    $<HTMLInputElement>(wizEl(scope, "size")).value = String(wizState.params.tileSize);
    $<HTMLInputElement>(wizEl(scope, "margin")).value = String(wizState.params.margin);
    $<HTMLInputElement>(wizEl(scope, "spacing")).value = String(wizState.params.spacing);
}

/** 手動入力から params を更新し、UI を再描画 (scope: "new"=dlg-new, "pool"=dlg-new-tileset) */
function wizSyncParamsFromInputs(scope: WizScope = "new"): void {
    const size = Math.min(TILESIZE_MAX, Math.max(TILESIZE_MIN, Math.floor(Number($<HTMLInputElement>(wizEl(scope, "size")).value)) || TILESIZE_DEFAULT));
    const margin = Math.min(4, Math.max(0, Math.floor(Number($<HTMLInputElement>(wizEl(scope, "margin")).value)) || 0));
    const spacing = Math.min(4, Math.max(0, Math.floor(Number($<HTMLInputElement>(wizEl(scope, "spacing")).value)) || 0));

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

    wizUpdateCandidateChips(scope);
    wizUpdateSlicePreview(scope);
    wizUpdateStats(scope);
}

const GRID_LINE_COLOR = "rgba(255, 100, 100, 0.85)";
const GRID_LINE_WIDTH = 1;
/** スライスプレビューキャンバスにグリッド線を重畳描画 (scope: "new"=dlg-new, "pool"=dlg-new-tileset) */
function wizUpdateSlicePreview(scope: WizScope = "new"): void {
    if (!wizState.dataUri) return;
    const { tileSize, margin, spacing, cols, rows } = wizState.params;
    const MAX_PREVIEW = 480;
    const scale = Math.min(1, MAX_PREVIEW / Math.max(wizState.pngWidth, 1));
    const dW = Math.round(wizState.pngWidth * scale);
    const dH = Math.round(wizState.pngHeight * scale);

    const cv = $<HTMLCanvasElement>(wizEl(scope, "slice-canvas"));
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

/** 統計テキスト + リベイク警告を更新 (scope: "new"=dlg-new, "pool"=dlg-new-tileset) */
function wizUpdateStats(scope: WizScope = "new"): void {
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
    $(wizEl(scope, "stats")).textContent = `${cols} 列 × ${rows} 行 = ${total} チップ${transparentTxt}${overLimit}`;

    const rebakeNote = $(wizEl(scope, "rebake-note"));
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
    setSelectedTile(newTileIndex);
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

/**
 * 別の PNG シートを「現在アクティブな層のタイルセット」へまるごと合体する (チップセット合体)。
 * 複数素材を 1 セットに束ねて 1 レイヤーで自由に混在ペイントできるようにするための機能。
 * - 現在のタイルサイズで PNG をスライス (割り切れなければ中止)。
 * - appendSheetToAtlas で末尾に密に追記 → 既存タイル id は不変。
 * - 上限 (チップ数 / PNG バイト / JSON バイト) を検証してから差し替える。
 */
async function mergeSheetFromFile(file: File): Promise<void> {
    if (!isV3Doc(doc)) {
        toast("チップセット合体はタイルセットマップのみ対応しています");
        return;
    }
    if (!file.type.includes("png") && !file.name.toLowerCase().endsWith(".png")) {
        toast("PNG ファイルを選択してください");
        return;
    }
    const d = doc;
    const tsIdx = d.layers[activeLayerIndexV3()].tileset;
    const ts = d.tilesets[tsIdx] ?? d.tilesets[0];
    const { tileSize, columns, rows } = ts;

    // 1. PNG を読み込む
    const bytes = new Uint8Array(await file.arrayBuffer());
    const dataUri = PNG_DATA_URI_PREFIX + bytesToBase64(bytes);
    const pngInfo = parsePngDataUri(dataUri);
    if (!pngInfo.ok) {
        toast(`PNG エラー: ${pngInfo.error}`);
        return;
    }
    // 2. 現在のタイルサイズで割り切れるか
    if (pngInfo.width % tileSize !== 0 || pngInfo.height % tileSize !== 0) {
        showMessages("チップセットを合体できません", [
            `合体するには現在のセットと同じタイルサイズ (${tileSize}px) で割り切れる PNG が必要です。`,
            `このPNGは ${pngInfo.width}×${pngInfo.height}px で ${tileSize}px の格子に乗りません。`,
        ], []);
        return;
    }
    const sheetCols = pngInfo.width / tileSize;
    const sheetRows = pngInfo.height / tileSize;
    const addedCount = sheetCols * sheetRows;

    // 3. 上限チェック (チップ数)
    if (tileCount(ts) + addedCount > MAX_TILECOUNT) {
        showMessages("チップセットを合体できません", [
            `合体後のチップ数 ${tileCount(ts) + addedCount} が上限 ${MAX_TILECOUNT} を超えます。`,
        ], []);
        return;
    }

    const sheetRgba = await getPngRgba(dataUri);
    const atlasRgba = await getPngRgba(ts.image);
    if (!sheetRgba || !atlasRgba) {
        toast("画像の読み込みに失敗しました");
        return;
    }

    // 4. 合体 (純関数) → PNG data URI 再エンコード
    const merged = appendSheetToAtlas(atlasRgba.rgba, columns, rows, tileSize, sheetRgba.rgba, sheetCols, sheetRows);
    const newImageUri = rgbaToPngDataUri(merged.rgba, columns * tileSize, merged.rows * tileSize);

    // 5. PNG / JSON サイズ検証
    const approxBytes = Math.floor((newImageUri.length - PNG_DATA_URI_PREFIX.length) * 3 / 4);
    if (approxBytes > MAX_TILESET_IMAGE_BYTES) {
        showMessages("チップセットを合体できません", [
            `合体後のアトラス PNG が 1 MB を超えます (約 ${(approxBytes / 1024).toFixed(1)} KB)。素材を減らすか小さくしてください。`,
        ], []);
        return;
    }
    if (JSON.stringify({ image: newImageUri }).length > MAX_JSON_BYTES_V2) {
        showMessages("チップセットを合体できません", [
            `合体後のマップ JSON が 4 MB を超える見込みです。素材を減らしてください。`,
        ], []);
        return;
    }

    // 6. tiles[] を addedCount 分だけデフォルト属性で拡張して差し替え
    const newTiles = [...ts.tiles];
    while (newTiles.length < columns * merged.rows) newTiles.push(defaultTileAttr());
    const newTileset: TilesetDoc = { tileSize, columns, rows: merged.rows, image: newImageUri, tiles: newTiles };
    d.tilesets[tsIdx] = newTileset;

    // 7. 反映 (タイルセット差し替えは Undo 非対応 = 既存パターンどおり history.clear)
    history.clear();
    refreshUndoButtons();
    renderer.setDoc(d);
    void rebuildPicker();
    drawTilesetPanel();
    refreshModeUi();
    scheduleSave();
    updateStatus();
    toast(`シートを合体しました (+${addedCount} チップ → 計 ${tileCount(newTileset)} チップ)`);
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
    // ⚙ レイヤー設定ボタン
    $("btn-layer-settings").addEventListener("click", () => void openLayerDialog());

    // 👤 人形トグル: プレイヤー前後関係プレビューの表示切替
    $("btn-ghost").addEventListener("click", () => {
        const on = !renderer.getGhostVisible();
        renderer.setGhostVisible(on);
        $("btn-ghost").classList.toggle("active", on);
        if (isV3Doc(doc)) updateLayerAboveBadges(doc);
    });

    // dlg-layer: above チェックボックス
    $<HTMLInputElement>("dlg-layer-above").addEventListener("change", (e) => {
        if (!isV3Doc(doc)) return;
        const li = activeLayerIndex();
        doc.layers[li].above = (e.target as HTMLInputElement).checked;
        updateLayerAboveBadges(doc);
        renderer.redrawAll();
        renderer.flush();
        scheduleSave();
    });
    // dlg-layer: 閉じる
    $("dlg-layer-close").addEventListener("click", () => {
        $<HTMLDialogElement>("dlg-layer").close();
    });

    // dlg-new-tileset (タイルセット追加ウィザード)
    const dlgNewTs = $<HTMLDialogElement>("dlg-new-tileset");
    $("new-ts-drop-zone").addEventListener("click", () => {
        addTilesetToPool = true;
        (pngInput as HTMLInputElement).click();
    });
    $("new-ts-drop-zone").addEventListener("dragover", (e) => {
        e.preventDefault();
        $("new-ts-drop-zone").classList.add("drag-over");
    });
    $("new-ts-drop-zone").addEventListener("dragleave", () => {
        $("new-ts-drop-zone").classList.remove("drag-over");
    });
    $("new-ts-drop-zone").addEventListener("drop", async (e) => {
        e.preventDefault();
        $("new-ts-drop-zone").classList.remove("drag-over");
        const file = e.dataTransfer?.files[0];
        if (file) {
            addTilesetToPool = true;
            await wizLoadPng(file);
        }
    });
    $("new-ts-ok-btn").addEventListener("click", () => {
        void wizBuildTileset().then((r) => {
            if (!r.ok) {
                showMessages("タイルセットを追加できません", [r.error], []);
                return;
            }
            if (!isV3Doc(doc)) return;
            if (doc.tilesets.length >= MAX_TILESETS) {
                toast(`タイルセットは最大 ${MAX_TILESETS} 個までです`);
                return;
            }
            doc.tilesets.push(r.tileset);
            const newTsIdx = doc.tilesets.length - 1;
            // 追加したセットをアクティブ層に割り当てる
            const li = activeLayerIndex();
            doc.layers[li].tileset = newTsIdx;
            selectedTilePerLayer[li] = 0;
            history.clear();
            refreshUndoButtons();
            renderer.setDoc(doc);
            void rebuildPicker();
            drawTilesetPanel();
            refreshModeUi();
            scheduleSave();
            updateStatus();
            dlgNewTs.close();
            addTilesetToPool = false;
            $("wiz-add-pool-note").hidden = true;
            toast(`タイルセット${newTsIdx + 1}を追加しました (チップ ${tileCount(r.tileset)} 個)`);
        });
    });
    $("new-ts-cancel-btn").addEventListener("click", () => {
        dlgNewTs.close();
        addTilesetToPool = false;
        $("wiz-add-pool-note").hidden = true;
    });
    // プレビュー画像クリックで別の PNG に差し替え (pool モード)
    $("new-ts-preview-info").addEventListener("click", () => {
        addTilesetToPool = true;
        (pngInput as HTMLInputElement).click();
    });

    for (const b of document.querySelectorAll<HTMLButtonElement>("#tools-v2 .tool2")) {
        b.addEventListener("click", () => setToolV2(b.dataset.tool2 as ToolV2));
    }
    setToolV2(toolV2);

    // ── オートタイルブラシ select ──
    $<HTMLSelectElement>("at-brush-select").addEventListener("change", (e) => {
        const val = (e.target as HTMLSelectElement).value;
        const idx = parseInt(val, 10);
        activeAutotileIdx = idx >= 0 ? idx : null;
        if (activeAutotileIdx !== null) stampBlock = null; // オートタイルを選んだらブロックスタンプ解除
        updateStatus();
    });

    // ── オートタイル設定ダイアログを開くボタン ──
    $("btn-autotile-open").addEventListener("click", () => {
        // アクティブなブラシがあればそれを編集、なければ新規
        void openAutotileDialog(activeAutotileIdx ?? -1);
    });

    // ── ダイアログ内のチップセット変更 ──
    $<HTMLSelectElement>("at-tileset-select").addEventListener("change", (e) => {
        const idx = parseInt((e.target as HTMLSelectElement).value, 10);
        atWorkDef.tileset = idx;
        atSelectedSlot = null;
        atFallbackSelected = false;
        void drawAutotileSlotPanel();
        void drawAtTilePickerPanel();
        void drawAutotilePreview();
    });

    // ── スロットボタンのクリック (event delegation) ──
    $("at-slot-panel").addEventListener("click", (e) => {
        const btn = (e.target as HTMLElement).closest<HTMLButtonElement>(".at-slot-btn");
        if (!btn) return;
        const code = parseInt(btn.dataset.slotCode ?? "-1", 10);
        if (code < 0 || code >= EDGE4_SLOTS) return;
        if (atSelectedSlot === code) {
            // 再クリックで選択解除
            atSelectedSlot = null;
        } else {
            atSelectedSlot = code;
            atFallbackSelected = false;
        }
        void drawAutotileSlotPanel();
    });
    $("at-slot-panel").addEventListener("contextmenu", (e) => {
        e.preventDefault();
        const btn = (e.target as HTMLElement).closest<HTMLButtonElement>(".at-slot-btn");
        if (!btn) return;
        const code = parseInt(btn.dataset.slotCode ?? "-1", 10);
        if (code < 0 || code >= EDGE4_SLOTS) return;
        // 右クリックでスロットをクリア
        atWorkDef.lut[code] = -1;
        atSelectedSlot = null;
        void drawAutotileSlotPanel();
        void drawAutotilePreview();
    });

    // ── 連番で一括割当 (時短) ──
    $("at-btn-fill-seq").addEventListener("click", () => fillSequentialLut());

    // ── fallback スロット ──
    $<HTMLCanvasElement>("at-fallback-canvas").addEventListener("click", () => {
        atFallbackSelected = !atFallbackSelected;
        atSelectedSlot = null;
        void drawAutotileSlotPanel();
    });
    $("at-fallback-clear").addEventListener("click", () => {
        atWorkDef.fallback = -1;
        atFallbackSelected = false;
        void drawAutotileSlotPanel();
        void drawAutotilePreview();
    });

    // ── タイルピッカー canvas クリック ──
    $<HTMLCanvasElement>("at-tile-picker-canvas").addEventListener("click", (e) => {
        if (!isV3Doc(doc)) return;
        if (atSelectedSlot === null && !atFallbackSelected) {
            toast("先にスロットをクリックして選択してください");
            return;
        }
        const ts = (doc as import("./model").MapDocV3).tilesets[atWorkDef.tileset];
        if (!ts) return;
        const cv = $<HTMLCanvasElement>("at-tile-picker-canvas");
        const r = cv.getBoundingClientRect();
        const PX = 40;
        const col = Math.floor((e.clientX - r.left) / PX);
        const row = Math.floor((e.clientY - r.top) / PX);
        if (col < 0 || col >= ts.columns || row < 0 || row >= ts.rows) return;
        const tileId = row * ts.columns + col;
        assignTileToActiveSlot(tileId);
    });

    // ── blocksLight チェックボックス ──
    $<HTMLInputElement>("at-blocks-light").addEventListener("change", (e) => {
        atWorkDef.blocksLight = (e.target as HTMLInputElement).checked;
    });

    // ── band ラジオ ──
    for (const inp of document.querySelectorAll<HTMLInputElement>("input[name='at-band']")) {
        inp.addEventListener("change", () => {
            if (inp.checked) atWorkDef.band = inp.value as "ground" | "above";
        });
    }

    // ── 名前入力 ──
    $<HTMLInputElement>("at-name-input").addEventListener("input", (e) => {
        atWorkDef.name = (e.target as HTMLInputElement).value.trim() || "壁";
    });

    // ── 保存ボタン ──
    $("at-btn-save").addEventListener("click", () => {
        if (!isV3Doc(doc)) return;
        if (isLutEmpty(atWorkDef) && atWorkDef.fallback < 0) {
            toast("スロットを 1 つ以上割り当ててください");
            return;
        }
        const name = $<HTMLInputElement>("at-name-input").value.trim() || "壁";
        atWorkDef.name = name;

        // LUT ベイク: blocksLight / band を tiles[] に焼く (§3 H2/H3)
        bakeAutotileAttrs(doc as import("./model").MapDocV3, atWorkDef);

        if (atEditIdx >= 0 && atEditIdx < autotiles.length) {
            autotiles[atEditIdx] = { ...atWorkDef, lut: [...atWorkDef.lut] };
            activeAutotileIdx = atEditIdx;
        } else {
            autotiles.push({ ...atWorkDef, lut: [...atWorkDef.lut] });
            activeAutotileIdx = autotiles.length - 1;
        }

        rebuildAutotileBrushSelect();
        scheduleSave();
        toast(`自動つなぎブラシ「${name}」を保存しました`);
        $<HTMLDialogElement>("dlg-autotile").close();
    });

    // ── キャンセルボタン ──
    $("at-btn-cancel").addEventListener("click", () => {
        $<HTMLDialogElement>("dlg-autotile").close();
    });

    // ── かんたん作成「この色で作る」 ──
    $("at-easy-make").addEventListener("click", () => {
        const hex = $<HTMLInputElement>("at-easy-color").value;
        const name = $<HTMLInputElement>("at-easy-name").value;
        void createWallAutotileFromColor(hex, name);
    });

    // ── 脱出ボタン「オートタイルを使わない」 ──
    $("at-btn-skip").addEventListener("click", () => {
        activeAutotileIdx = null;
        rebuildAutotileBrushSelect();
        $<HTMLDialogElement>("dlg-autotile").close();
    });
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

    // 全画面グリッド: クリックで1チップ選択 / ドラッグで範囲選択 (= ブロックスタンプ)
    {
        const palCv = $<HTMLCanvasElement>("palette-canvas");
        let palDrag: { c0: number; r0: number } | null = null;

        const palCell = (e: PointerEvent): { col: number; row: number; cols: number; rows: number; ids: number[] } => {
            const r = palCv.getBoundingClientRect();
            const cols = Number(palCv.dataset.gridCols ?? "1");
            const ids = (palCv.dataset.filteredIds ?? "").split(",").map(Number).filter((n) => Number.isFinite(n));
            const rows = Math.max(1, Math.ceil(ids.length / cols));
            const col = Math.min(cols - 1, Math.max(0, Math.floor((e.clientX - r.left) / GRID_PX)));
            const row = Math.min(rows - 1, Math.max(0, Math.floor((e.clientY - r.top) / GRID_PX)));
            return { col, row, cols, rows, ids };
        };

        const drawPalSel = (ac: number, ar: number, bc: number, br: number): void => {
            const ctx = palCv.getContext("2d");
            if (!ctx || !paletteSnapshot) return;
            ctx.drawImage(paletteSnapshot, 0, 0); // ベースに戻してから枠を描く
            const x = Math.min(ac, bc) * GRID_PX;
            const y = Math.min(ar, br) * GRID_PX;
            const w = (Math.abs(bc - ac) + 1) * GRID_PX;
            const h = (Math.abs(br - ar) + 1) * GRID_PX;
            ctx.fillStyle = "rgba(255,215,94,0.22)";
            ctx.fillRect(x, y, w, h);
            ctx.strokeStyle = "#ffd75e";
            ctx.lineWidth = 2;
            ctx.strokeRect(x + 1, y + 1, w - 2, h - 2);
        };

        palCv.addEventListener("pointerdown", (e) => {
            if (!isV3Doc(doc)) return;
            const { col, row } = palCell(e);
            palDrag = { c0: col, r0: row };
            palCv.setPointerCapture(e.pointerId);
        });
        palCv.addEventListener("pointermove", (e) => {
            if (!palDrag) return;
            const { col, row } = palCell(e);
            drawPalSel(palDrag.c0, palDrag.r0, col, row);
        });
        palCv.addEventListener("pointerup", (e) => {
            const start = palDrag;
            palDrag = null;
            if (!start || !isV3Doc(doc)) return;
            const { col, row, cols, ids } = palCell(e);
            const c0 = Math.min(start.c0, col), c1 = Math.max(start.c0, col);
            const r0 = Math.min(start.r0, row), r1 = Math.max(start.r0, row);

            if (c0 === c1 && r0 === r1) {
                // 単一クリック → 従来どおり 1 チップ選択 (selectTile が overlay も閉じる)
                const idx = r0 * cols + c0;
                if (idx >= 0 && idx < ids.length) {
                    selectTile(ids[idx]);
                    if (toolV2 === "erase" || toolV2 === "pick") setToolV2("pen");
                }
                return;
            }

            // 範囲選択 → ブロックスタンプを作る (配置順そのまま、空きスロットは -1)
            const w = c1 - c0 + 1, h = r1 - r0 + 1;
            const block: number[] = [];
            for (let r = r0; r <= r1; r++) {
                for (let c = c0; c <= c1; c++) {
                    const idx = r * cols + c;
                    block.push(idx >= 0 && idx < ids.length ? ids[idx] : -1);
                }
            }
            stampBlock = { w, h, ids: block };
            activeAutotileIdx = null; // スタンプ中はオートタイル解除
            rebuildAutotileBrushSelect();
            if (toolV2 !== "pen") setToolV2("pen");
            closePaletteOverlay();
            toast(`${w}×${h} のチップブロックを選びました (クリックで設置・ドラッグで連続)`);
            updateStatus();
        });
    }

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
            // wizLoadPng 内で addTilesetToPool フラグを見てプール側 UI を更新する
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

    // チップの大きさスライダー (v3 専用)
    $<HTMLInputElement>("tile-scale").addEventListener("input", (e) => {
        if (!isV3Doc(doc)) return;
        const raw = parseFloat((e.target as HTMLInputElement).value);
        const clamped = Math.min(TILE_SCALE_MAX, Math.max(TILE_SCALE_MIN, raw));
        $("tile-scale-val").textContent = clamped.toFixed(2) + "x";
        if (clamped === TILE_SCALE_DEFAULT) {
            // 1.0 のときはキーを省略して JSON を綺麗に保つ
            delete doc.ambient.tileScale;
        } else {
            doc.ambient.tileScale = clamped;
        }
        scheduleSave();
    });

    // 新規
    const dlgNew = $<HTMLDialogElement>("dlg-new");
    const v2Section = $("new-v2-section");
    const typeRadios = [...document.querySelectorAll<HTMLInputElement>("input[name=new-type]")];
    const refreshTypeUi = (): void => {
        // "custom" = PNG カスタムタイルセット、それ以外 (backrooms) は Backrooms プリセット
        const isCustom = typeRadios.find((r) => r.checked)?.value === "custom";
        v2Section.hidden = !isCustom;
        if (isCustom) {
            // カスタム選択時: PNG が未投入なら作成ボタン無効
            $<HTMLButtonElement>("new-ok-btn").disabled = wizState.dataUri === null;
        } else {
            // Backrooms は常に有効
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

    // Ctrl+V クリップボード貼り付け (dlg-new または dlg-new-tileset が開いているときのみ)
    document.addEventListener("paste", async (e) => {
        const dlgNewTs = $<HTMLDialogElement>("dlg-new-tileset");
        if (!dlgNew.open && !dlgNewTs.open) return;
        const items = e.clipboardData?.items;
        if (!items) return;
        for (const item of items) {
            if (item.type === "image/png") {
                const file = item.getAsFile();
                if (file) {
                    // wizLoadPng 内で addTilesetToPool フラグを見てプール側 UI を更新する
                    await wizLoadPng(file);
                }
                break;
            }
        }
    });

    // 手動入力変更時: params 更新 → プレビュー更新 (new スコープ)
    const manualInputIds = ["wiz-size", "wiz-margin", "wiz-spacing"];
    for (const id of manualInputIds) {
        $<HTMLInputElement>(id).addEventListener("input", () => {
            if (wizState.dataUri) wizSyncParamsFromInputs();
        });
        $<HTMLInputElement>(id).addEventListener("change", () => {
            if (wizState.dataUri) wizSyncParamsFromInputs();
        });
    }

    // 手動入力変更時: params 更新 → プレビュー更新 (pool スコープ)
    const poolInputIds = ["nts-size", "nts-margin", "nts-spacing"];
    for (const id of poolInputIds) {
        $<HTMLInputElement>(id).addEventListener("input", () => {
            if (wizState.dataUri) wizSyncParamsFromInputs("pool");
        });
        $<HTMLInputElement>(id).addEventListener("change", () => {
            if (wizState.dataUri) wizSyncParamsFromInputs("pool");
        });
    }

    dlgNew.addEventListener("close", () => {
        if (dlgNew.returnValue !== "ok") return;
        const w = clampInt($<HTMLInputElement>("new-w").value, MIN_DIM, MAX_DIM, 32);
        const h = clampInt($<HTMLInputElement>("new-h").value, MIN_DIM, MAX_DIM, 32);
        const name = ($<HTMLInputElement>("new-name").value.trim() || "新しいマップ").slice(0, NAME_MAX);
        const author = $<HTMLInputElement>("new-author").value.slice(0, AUTHOR_MAX);
        const isCustom = typeRadios.find((r) => r.checked)?.value === "custom";
        if (!isCustom) {
            // Backrooms プリセット: タイルセット内蔵なので PNG 不要
            selectedTilePerLayer = [0, 0, 0, 0];
            void backupAutosave();
            setDocument(createBackroomsDoc(w, h, name, author));
            toast(`${w}×${h} の Backrooms マップを作りました (床・壁・奈落がすぐ使えるよ！)`);
            return;
        }
        if (!wizState.dataUri) {
            showMessages("新規マップ (カスタムタイルセット) を作成できません", ["タイルセット PNG が未選択です"], []);
            return;
        }
        void wizBuildTileset().then((r) => {
            if (!r.ok) {
                showMessages("新規マップ (カスタムタイルセット) を作成できません", [r.error], []);
                return;
            }
            selectedTilePerLayer = [0, 0, 0, 0];
            void backupAutosave();
            setDocument(createNewDocV3(w, h, name, author, r.tileset));
            const rebakeInfo = wizState.needsRebake ? " (余白を正規化しました)" : "";
            toast(`${w}×${h} のカスタムタイルセットマップを作りました (チップ ${tileCount(r.tileset)} 個)${rebakeInfo}`);
        });
    });

    // ファイル入出力
    const fileInput = $<HTMLInputElement>("file-input");
    // "開く" → 書庫ダイアログ
    $("btn-import").addEventListener("click", () => void openLibraryDialog());
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
    // "保存" → 書庫に保存
    $("btn-export").addEventListener("click", () => void saveToLibrary(false));
    // "ファイル書出" → ダウンロード
    $("btn-export-file").addEventListener("click", () => exportFile());
    $("btn-play").addEventListener("click", () => void playInGame());

    // 書庫ダイアログ
    $("library-import").addEventListener("click", () => {
        $<HTMLDialogElement>("dlg-library").close();
        fileInput.click();
    });
    $("library-close").addEventListener("click", () => $<HTMLDialogElement>("dlg-library").close());

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

    // チップセット合体: 別シートをまるごと現在のセットへ
    const mergeSheetInput = $<HTMLInputElement>("merge-sheet-input");
    $("btn-merge-sheet-overlay").addEventListener("click", () => {
        if (!isV3Doc(doc)) { toast("チップセット合体はタイルセットマップのみ対応しています"); return; }
        mergeSheetInput.click();
    });
    mergeSheetInput.addEventListener("change", async () => {
        const f = mergeSheetInput.files?.[0];
        mergeSheetInput.value = "";
        if (f) await mergeSheetFromFile(f);
    });

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
        // Ctrl+S / Cmd+S: マップを書庫に上書き保存 (ブラウザの「ページを保存」を抑止)。入力欄内でも有効。
        if ((e.ctrlKey || e.metaKey) && !e.altKey && e.key.toLowerCase() === "s") {
            e.preventDefault();
            void saveToLibrary(false);
            return;
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

    // Delete キーで選択中の影線を削除 (影モード時のみ)
    window.addEventListener("keydown", (e) => {
        const tag = (e.target as HTMLElement | null)?.tagName ?? "";
        if (tag === "INPUT" || tag === "TEXTAREA") return;
        if ((e.key === "Delete" || e.key === "Backspace") && isV3Doc(doc) && activeLayer === "shadow") {
            if (shadowSelectedIdx >= 0) {
                e.preventDefault();
                deleteSelectedShadowLine();
            }
        }
    });

    // 影モード: 吸着トグル
    $<HTMLInputElement>("shadow-snap-toggle").addEventListener("change", (e) => {
        shadowSnap = (e.target as HTMLInputElement).checked;
    });

    // 影モード: 右クリックで近くの影線を削除
    renderer.app.canvas.addEventListener("contextmenu", (e) => {
        if (!isV3Doc(doc) || activeLayer !== "shadow") return;
        e.preventDefault();
        // float セル座標を取得
        const r = renderer.app.canvas.getBoundingClientRect();
        const cx = e.clientX - r.left;
        const cy = e.clientY - r.top;
        const s = renderer.world.scale.x;
        const wx = (cx - renderer.world.x) / s / 32;
        const wy = (cy - renderer.world.y) / s / 32;
        const idx = findNearestShadowLine(wx, wy);
        if (idx >= 0) {
            shadowSelectedIdx = idx;
            deleteSelectedShadowLine();
        }
    });

    // 終了時に保留中の自動保存を投げる (ベストエフォート)
    window.addEventListener("beforeunload", () => {
        if (savePending) void saveAutosave(docToJsonAny(doc));
    });

    // 3ペインシェルの折りたたみレール
    wireShellPanes();

    // 右インスペクタ: チップ属性の編集ボタン (選択中チップに直接適用)
    $("tileattr-pass").addEventListener("click", () => applyTileAttrChange((t) => { t.pass = PASS_CYCLE[t.pass]; }));
    $("tileattr-over").addEventListener("click", () => applyTileAttrChange((t) => { t.over = !t.over; }));
    $("tileattr-light").addEventListener("click", () => applyTileAttrChange((t) => { t.light = !t.light; }));

    // スタート画面ボタン配線
    $("btn-home").addEventListener("click", () => showStartScreen());
    $("btn-help").addEventListener("click", () => startCoach());

    // スタート画面内ボタン
    $("start-new").addEventListener("click", () => {
        hideStartScreen();
        $("btn-new").click();
    });
    $("start-library").addEventListener("click", () => {
        hideStartScreen();
        void openLibraryDialog();
    });
    $("start-skip").addEventListener("click", () => hideStartScreen());
    $("start-minigame").addEventListener("click", () => void openMinigame());

    // コーチマークボタン配線
    $("coach-next").addEventListener("click", () => showCoachStep(coachStep + 1));
    $("coach-skip").addEventListener("click", () => endCoach());
}

// ---------- 起動 ----------

async function boot(): Promise<void> {
    await renderer.init($("viewport"));

    wireUi();

    const saved = await loadAutosave();
    const restored = saved === undefined || saved === null ? null : tryRestoreDoc(saved);

    if (restored === null) {
        // 初回 / 自動保存なし: 白紙を裏に置いてからスタート画面を表示
        setDocument(createBackroomsDoc(32, 32, "新しいマップ", ""));
    } else {
        setDocument(restored);
    }

    // オートタイル定義を復元 (緩い型チェック: Array かつ各要素が scheme==="edge4" && Array.isArray(lut))
    const savedAt = await loadAutotiles();
    if (Array.isArray(savedAt)) {
        const loaded: AutotileDef[] = [];
        for (const item of savedAt) {
            if (
                typeof item === "object" && item !== null &&
                (item as Record<string, unknown>).scheme === "edge4" &&
                Array.isArray((item as Record<string, unknown>).lut)
            ) {
                loaded.push(item as AutotileDef);
            }
        }
        if (loaded.length > 0) {
            autotiles = loaded;
            rebuildAutotileBrushSelect();
        }
    }

    if (restored) {
        toast("自動保存から復元しました");
        maybeAutoCoach();
    } else {
        showStartScreen();
    }

    new InputController(renderer.app.canvas, renderer.world, {
        strokeStart: onStrokeStart,
        strokeStep: onStrokeStep,
        strokeEnd: onStrokeEnd,
        strokeCancel: onStrokeCancel,
        hover: (fx, fy) => {
            lastHoverFloat = { x: fx, y: fy };
            lastHover = { x: Math.floor(fx), y: Math.floor(fy) };
            renderer.setHover(lastHover.x, lastHover.y);
            updateStatus();
        },
        viewChanged: updateStatus,
        // ゴーストプレイヤー (前後関係プレビュー): v3 のみ・人形の上で押下したら掴む
        tryGrabGhost: (fx, fy) => {
            if (!isV3Doc(doc) || !renderer.ghostHitTest(fx, fy)) return false;
            renderer.beginGhostDrag();
            return true;
        },
        ghostDragTo: (fx, fy) => renderer.setGhostCellFloat(fx, fy),
        ghostDragEnd: () => renderer.endGhostDrag(),
    });

    updateStatus();
}

void boot();
