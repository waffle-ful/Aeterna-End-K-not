// PIXI.js v8 グリッド描画
// v1: 床/壁/奈落 + 孤立柱導出プレビュー (仕様 §5.1) + 通行○×オーバーレイ + decor/spawn マーカー
// v3: タイルセット切り出し描画 (4 層昇順 + ★バッジ) + §21 解決規則の通行オーバーレイ

import { Application, Container, Graphics, Sprite, Text, Texture } from "pixi.js";
import {
    type AnyDoc,
    CELL_FLOOR,
    CELL_WALL,
    type DecorEntry,
    type MapDoc,
    type MapDocV3,
    type TilesetDoc,
    cellAboveMask,
    getCell,
    isV3Doc,
    resolveCellV3,
} from "./model";

/** ワールド px / セル */
export const CELL = 32;
/** v1 テクスチャ px / セル (256×256 でもキャンバス 3072px に収める) */
const TEXPX_V1 = 12;
/** キャンバス辺の上限 (モバイル Safari 互換の安全圏) */
const MAX_CANVAS_EDGE = 4096;

export const MIN_SCALE = 2 / CELL;
export const MAX_SCALE = 160 / CELL;

const COLOR_VOID = "#0b0b0e";
const COLOR_FLOOR = "#5a3a1a";
const COLOR_WALL = "#d9a640";
const COLOR_WALL_BAND = "rgba(40,25,0,0.30)";
const COLOR_PILLAR_BG = "#181206";
const COLOR_PILLAR = "#e7c468";
const COLOR_GRID = "rgba(255,255,255,0.13)";

// ---------- タイルセット画像ローダ (data URI → HTMLImageElement, キャッシュ付き) ----------

const imageCache = new Map<string, Promise<HTMLImageElement>>();

export function loadTilesetImage(dataUri: string): Promise<HTMLImageElement> {
    let p = imageCache.get(dataUri);
    if (!p) {
        p = new Promise<HTMLImageElement>((resolve, reject) => {
            const img = new Image();
            img.onload = () => resolve(img);
            img.onerror = () => reject(new Error("タイルセット画像を読み込めません"));
            img.src = dataUri;
        });
        imageCache.set(dataUri, p);
        p.catch(() => imageCache.delete(dataUri)); // 失敗はキャッシュしない
    }
    return p;
}

interface DecorStyle {
    color: number;
    shape: "circle" | "rect";
    label: string;
}

const DECOR_STYLE: Record<string, DecorStyle> = {
    light: { color: 0xffe066, shape: "circle", label: "灯" },
    stain: { color: 0x6b6b3a, shape: "circle", label: "染" },
    door: { color: 0xa0622d, shape: "rect", label: "扉" },
    vent: { color: 0x9aa0a8, shape: "rect", label: "ベ" },
    ceiling: { color: 0xcfcfcf, shape: "rect", label: "天" },
};
const DECOR_FALLBACK: DecorStyle = { color: 0xff66ff, shape: "circle", label: "?" };

function makeDecorIcon(d: DecorEntry): Container {
    const st = DECOR_STYLE[d.kind] ?? DECOR_FALLBACK;
    const c = new Container();
    const g = new Graphics();
    if (st.shape === "circle") {
        g.circle(0, 0, CELL * 0.34).fill({ color: st.color, alpha: 0.92 }).stroke({ width: 1.5, color: 0x000000, alpha: 0.6 });
    } else {
        g.roundRect(-CELL * 0.34, -CELL * 0.34, CELL * 0.68, CELL * 0.68, CELL * 0.1).fill({ color: st.color, alpha: 0.92 }).stroke({ width: 1.5, color: 0x000000, alpha: 0.6 });
    }
    const label = new Text({
        text: st.label,
        style: { fontSize: CELL * 0.4, fill: 0x141414, fontFamily: "sans-serif", fontWeight: "bold" },
        resolution: 2,
    });
    label.anchor.set(0.5);
    c.addChild(g, label);
    c.position.set((d.x + 0.5) * CELL, (d.y + 0.5) * CELL);
    return c;
}

function buildSpawnMarker(): Container {
    const c = new Container();
    const g = new Graphics();
    g.circle(0, 0, CELL * 0.4).fill({ color: 0x2e8b3d, alpha: 0.95 }).stroke({ width: 3, color: 0xb8ffc4, alpha: 0.95 });
    const t = new Text({
        text: "S",
        style: { fontSize: CELL * 0.45, fill: 0xffffff, fontFamily: "sans-serif", fontWeight: "bold" },
        resolution: 2,
    });
    t.anchor.set(0.5);
    c.addChild(g, t);
    return c;
}

/**
 * ドラッグ可能なゴーストプレイヤー人形 (前後関係プレビュー用)。
 * クルーメイト風シルエット: 半透明の胴体 + バイザー + 接地影。中心が原点。
 */
function buildGhost(): Container {
    const c = new Container();
    const bw = CELL * 0.52; // 体の幅
    const bh = CELL * 0.62; // 体の高さ
    const g = new Graphics();
    // 接地影
    g.ellipse(0, bh * 0.46, bw * 0.5, CELL * 0.1).fill({ color: 0x000000, alpha: 0.28 });
    // バックパック
    g.roundRect(bw * 0.26, -bh * 0.28, bw * 0.26, bh * 0.5, bw * 0.12).fill({ color: 0xc0413f, alpha: 0.85 });
    // 胴体 (カプセル)
    g.roundRect(-bw * 0.5, -bh * 0.5, bw, bh, bw * 0.32).fill({ color: 0xe0524f, alpha: 0.9 }).stroke({ width: 1.5, color: 0x3a0d0c, alpha: 0.6 });
    // 脚
    g.roundRect(-bw * 0.34, bh * 0.34, bw * 0.26, bh * 0.22, bw * 0.08).fill({ color: 0xc0413f, alpha: 0.9 });
    g.roundRect(bw * 0.08, bh * 0.34, bw * 0.26, bh * 0.22, bw * 0.08).fill({ color: 0xc0413f, alpha: 0.9 });
    // バイザー
    g.roundRect(-bw * 0.12, -bh * 0.22, bw * 0.5, bh * 0.28, bh * 0.12).fill({ color: 0x9fdcef, alpha: 0.92 }).stroke({ width: 1.5, color: 0x2a5663, alpha: 0.6 });
    c.addChild(g);
    return c;
}

export class MapRenderer {
    readonly app = new Application();
    readonly world = new Container();

    private doc: AnyDoc | null = null;
    private baseCtx: CanvasRenderingContext2D | null = null;
    private ovlCtx: CanvasRenderingContext2D | null = null;
    private baseTexture: Texture | null = null;
    private ovlTexture: Texture | null = null;
    private baseSprite: Sprite | null = null;
    private ovlSprite: Sprite | null = null;
    /**
     * over キャンバス: ghostVisible 時、プレイヤーより前面 (cellAboveMask のビットが立つ) タイルだけを描く。
     * ゴースト人形の z 上に重ねることで「プレイヤーの前に出るタイル」を実描画でプレビューする。
     * base + over の 2 枚構成 (罠15 のメモリ予算「2 枚 + over はスパース」の上限内)。
     */
    private overCtx: CanvasRenderingContext2D | null = null;
    private overTexture: Texture | null = null;
    private overSprite: Sprite | null = null;
    private readonly decorLayer = new Container();
    private spawnMarker: Container | null = null;
    /** ドラッグ可能なゴーストプレイヤー人形 (前後関係プレビュー用、.ekmap には保存しない) */
    private ghostPlayer: Container | null = null;
    /** ゴースト表示中か (v3 既定 ON)。false で従来のフラット描画にフォールバック */
    private ghostVisible = true;
    /** ゴースト中心のセル座標 (float)。setDoc で spawn 中央にリセット */
    private ghostX = 0;
    private ghostY = 0;
    private readonly hover = new Graphics();
    private readonly rectPreview = new Graphics();
    /** 影線オーバーレイ: baseSprite → decorLayer → spawnMarker → shadowLayer → ovlSprite → rectPreview → hover の順 */
    private readonly shadowLayer = new Graphics();
    /** 影モード中のドラッグプレビュー線 */
    private readonly shadowPreview = new Graphics();
    /** 影モード (true のとき影線を濃く表示) */
    private shadowModeOn = false;
    private overlayOn = false;
    /** テクスチャ px / セル (v1 固定 12 / v3 はマップ寸法に応じて適応) */
    private texpx = TEXPX_V1;
    /**
     * v3 タイルセット画像群 (非同期ロード完了後にセット)。
     * インデックスは doc.tilesets に対応。
     */
    private tilesetImgs: (HTMLImageElement | null)[] = [];
    /** setDoc 世代 (非同期ロードの取り違え防止) */
    private docGen = 0;
    /**
     * 薄表示: アクティブレイヤー index (0〜3)、null = 全層 100% (decor/spawn タブ時)。
     * setActiveLayerIndex() から変更する。
     */
    private activeLayerIdx: number | null = null;

    async init(host: HTMLElement): Promise<void> {
        await this.app.init({
            resizeTo: host,
            background: "#101014",
            antialias: true,
            resolution: Math.min(globalThis.devicePixelRatio || 1, 2),
            autoDensity: true,
        });
        host.appendChild(this.app.canvas);
        this.app.canvas.style.cursor = "crosshair";
        this.app.stage.addChild(this.world);
        this.hover.rect(0, 0, CELL, CELL).stroke({ width: 2, color: 0xffe066, alpha: 0.9 });
        this.hover.visible = false;
        this.rectPreview.visible = false;
    }

    setDoc(doc: AnyDoc): void {
        this.doc = doc;
        this.docGen++;
        this.tilesetImgs = [];
        this.world.removeChildren();
        if (this.baseSprite) {
            this.baseSprite.destroy();
            this.baseSprite = null;
        }
        if (this.baseTexture) {
            this.baseTexture.destroy(true);
            this.baseTexture = null;
        }
        if (this.ovlSprite) {
            this.ovlSprite.destroy();
            this.ovlSprite = null;
        }
        if (this.ovlTexture) {
            this.ovlTexture.destroy(true);
            this.ovlTexture = null;
        }
        if (this.overSprite) {
            this.overSprite.destroy();
            this.overSprite = null;
        }
        if (this.overTexture) {
            this.overTexture.destroy(true);
            this.overTexture = null;
        }

        // v3 はタイルの絵を保つため可能な範囲で高解像度に (キャンバス辺 4096px 以内)
        if (isV3Doc(doc)) {
            const maxTileSize = Math.max(...doc.tilesets.map((ts: TilesetDoc) => ts.tileSize));
            this.texpx = Math.max(4, Math.min(maxTileSize, Math.floor(MAX_CANVAS_EDGE / Math.max(doc.width, doc.height))));
        } else {
            this.texpx = TEXPX_V1;
        }

        const base = document.createElement("canvas");
        base.width = doc.width * this.texpx;
        base.height = doc.height * this.texpx;
        this.baseCtx = base.getContext("2d");
        if (this.baseCtx) this.baseCtx.imageSmoothingEnabled = false; // NEAREST (仕様 §14: FilterMode.Point 相当)
        this.baseTexture = Texture.from(base);
        this.baseTexture.source.scaleMode = "nearest";
        this.baseSprite = new Sprite(this.baseTexture);
        this.baseSprite.scale.set(CELL / this.texpx);

        const ovl = document.createElement("canvas");
        ovl.width = doc.width * this.texpx;
        ovl.height = doc.height * this.texpx;
        this.ovlCtx = ovl.getContext("2d");
        this.ovlTexture = Texture.from(ovl);
        this.ovlSprite = new Sprite(this.ovlTexture);
        this.ovlSprite.scale.set(CELL / this.texpx);
        this.ovlSprite.alpha = 0.95;
        this.ovlSprite.visible = this.overlayOn;

        // over キャンバス (base と同寸法・NEAREST)。前面タイルだけを描く透過レイヤー。
        // v3 のときだけ確保する (v1/v2 は層/above 概念が無く無駄なので作らない = メモリ予算節約)。
        const v3 = isV3Doc(doc);
        if (v3) {
            const over = document.createElement("canvas");
            over.width = doc.width * this.texpx;
            over.height = doc.height * this.texpx;
            this.overCtx = over.getContext("2d");
            if (this.overCtx) this.overCtx.imageSmoothingEnabled = false;
            this.overTexture = Texture.from(over);
            this.overTexture.source.scaleMode = "nearest";
            this.overSprite = new Sprite(this.overTexture);
            this.overSprite.scale.set(CELL / this.texpx);
            this.overSprite.visible = this.ghostVisible;
        }

        this.spawnMarker ??= buildSpawnMarker();
        this.ghostPlayer ??= buildGhost();
        // ゴーストを spawn 中央へ。v3 のときだけ表示する。
        this.ghostX = doc.spawn.x + 0.5;
        this.ghostY = doc.spawn.y + 0.5;
        this.ghostPlayer.visible = v3 && this.ghostVisible;
        this.updateGhost();
        // z 順: base(背面) → 装飾 → spawn → 影 → 👤ゴースト → over(前面) → 通行overlay → 矩形 → ホバー
        this.world.addChild(this.baseSprite, this.decorLayer, this.spawnMarker, this.shadowLayer, this.shadowPreview, this.ghostPlayer);
        if (this.overSprite) this.world.addChild(this.overSprite);
        this.world.addChild(this.ovlSprite, this.rectPreview, this.hover);
        this.rectPreview.visible = false;

        if (isV3Doc(doc)) {
            const gen = this.docGen;
            // 全タイルセット画像を並列ロード
            this.tilesetImgs = new Array(doc.tilesets.length).fill(null);
            for (let ti = 0; ti < doc.tilesets.length; ti++) {
                const tsIndex = ti;
                void loadTilesetImage(doc.tilesets[ti].image)
                    .then((img) => {
                        if (gen !== this.docGen) return; // doc が差し替わっていたら破棄
                        this.tilesetImgs[tsIndex] = img;
                        this.redrawAll();
                        this.flush();
                    })
                    .catch(() => {
                        // 画像破損 — 検証を通った doc では起きない想定。プレースホルダのまま
                    });
            }
        }

        this.redrawAll();
        this.rebuildDecor();
        this.updateSpawn();
        this.rebuildShadow();
        this.flush();
    }

    private isPillarAt(doc: MapDoc, x: number, y: number): boolean {
        if (getCell(doc, x, y) !== CELL_WALL) return false;
        return getCell(doc, x - 1, y) !== CELL_WALL && getCell(doc, x + 1, y) !== CELL_WALL && getCell(doc, x, y - 1) !== CELL_WALL && getCell(doc, x, y + 1) !== CELL_WALL;
    }

    private drawCellBase(x: number, y: number): void {
        const doc = this.doc;
        const ctx = this.baseCtx;
        if (!doc || !ctx) return;
        if (x < 0 || y < 0 || x >= doc.width || y >= doc.height) return;
        const t = this.texpx;
        const px = x * t;
        const py = y * t;
        if (isV3Doc(doc)) {
            this.drawCellBaseV3(doc, ctx, x, y, px, py, t);
        } else {
            this.drawCellBaseV1(doc as MapDoc, ctx, x, y, px, py, t);
        }
        // グリッド線 (各セルの上辺・左辺)
        ctx.fillStyle = COLOR_GRID;
        ctx.fillRect(px, py, t, 1);
        ctx.fillRect(px, py, 1, t);
    }

    private drawCellBaseV1(doc: MapDoc, ctx: CanvasRenderingContext2D, x: number, y: number, px: number, py: number, t: number): void {
        const c = doc.grid[y * doc.width + x];
        if (c === CELL_WALL) {
            if (this.isPillarAt(doc, x, y)) {
                // 孤立柱 (仕様 §5.1): 標準壁と違う見た目でプレビュー
                ctx.fillStyle = COLOR_PILLAR_BG;
                ctx.fillRect(px, py, t, t);
                ctx.fillStyle = COLOR_PILLAR;
                ctx.fillRect(px + t * 0.25, py + t * 0.25, t * 0.5, t * 0.5);
            } else {
                ctx.fillStyle = COLOR_WALL;
                ctx.fillRect(px, py, t, t);
                ctx.fillStyle = COLOR_WALL_BAND;
                ctx.fillRect(px, py, t, Math.max(1, Math.floor(t * 0.25)));
            }
        } else if (c === CELL_FLOOR) {
            ctx.fillStyle = COLOR_FLOOR;
            ctx.fillRect(px, py, t, t);
        } else {
            ctx.fillStyle = COLOR_VOID;
            ctx.fillRect(px, py, t, t);
        }
    }

    /**
     * タイル 1 枚を描画する。img がまだロード中なら placeholder を描く。
     * ts: TilesetDoc、id: タイル id (0-based)
     */
    private drawTile(
        ctx: CanvasRenderingContext2D,
        ts: TilesetDoc,
        img: HTMLImageElement | null,
        id: number,
        px: number,
        py: number,
        t: number,
    ): void {
        const sx = (id % ts.columns) * ts.tileSize;
        const sy = Math.floor(id / ts.columns) * ts.tileSize;
        if (img) {
            ctx.drawImage(img, sx, sy, ts.tileSize, ts.tileSize, px, py, t, t);
        } else {
            // 画像ロード中のプレースホルダ
            ctx.fillStyle = "#3a3a45";
            ctx.fillRect(px, py, t, t);
        }
    }

    /**
     * v3 セルの描画: 層 0〜3 昇順でタイルを重ねる (§21.4)。
     * 薄表示: activeLayerIdx が null でないとき、非アクティブ層は α=0.35 で描く。
     * ★バッジ: cellAboveMask が非ゼロ (layer.above=true または チップ over=true) のセルに表示。
     */
    private drawCellBaseV3(doc: MapDocV3, ctx: CanvasRenderingContext2D, x: number, y: number, px: number, py: number, t: number): void {
        const i = y * doc.width + x;
        ctx.fillStyle = COLOR_VOID;
        ctx.fillRect(px, py, t, t);

        const activeIdx = this.activeLayerIdx;
        const aboveMask = cellAboveMask(doc, i);
        // ghostVisible 時は前面タイルを over キャンバスへ分離。over の同セルは先に消す。
        const split = this.ghostVisible && this.overCtx != null;
        if (split) this.overCtx?.clearRect(px, py, t, t);

        // 層 0〜3 昇順で描画 (薄表示対応)。前面タイルは over、それ以外は base へ。
        for (let li = 0; li < doc.layers.length; li++) {
            const layer = doc.layers[li];
            const tid = layer.cells[i];
            if (tid < 0) continue;
            const ts = doc.tilesets[layer.tileset];
            if (!ts) continue;
            const img = this.tilesetImgs[layer.tileset] ?? null;
            const alpha = (activeIdx === null || li === activeIdx) ? 1 : 0.35;
            const isAbove = split && (aboveMask & (1 << li)) !== 0;
            const target = isAbove ? (this.overCtx as CanvasRenderingContext2D) : ctx;
            if (alpha !== 1) {
                target.globalAlpha = alpha;
            }
            this.drawTile(target, ts, img, tid, px, py, t);
            if (alpha !== 1) {
                target.globalAlpha = 1;
            }
        }

        // ★バッジ (over=true のタイルを含むセル、または layer.above=true の層があるセル)。
        // ゴースト表示中は実際に前面描画されるので冗長だが、ゴースト OFF 時の静的ヒントとして残す。
        if (aboveMask !== 0) {
            ctx.fillStyle = "rgba(0,0,0,0.55)";
            ctx.fillRect(px + t * 0.62, py, t * 0.38, t * 0.38);
            ctx.fillStyle = "#ffd75e";
            ctx.font = `bold ${Math.max(6, Math.floor(t * 0.34))}px sans-serif`;
            ctx.textAlign = "center";
            ctx.textBaseline = "middle";
            ctx.fillText("★", px + t * 0.81, py + t * 0.21);
        }
    }

    /**
     * アクティブレイヤー index を設定する。
     * 値が変わったときのみ redrawAll+flush を実行する。
     * null = 全層 100% (decor/spawn タブ時)。
     */
    setActiveLayerIndex(idx: number | null): void {
        if (this.activeLayerIdx === idx) return;
        this.activeLayerIdx = idx;
        this.redrawAll();
        this.flush();
    }

    // ---- ゴーストプレイヤー (前後関係プレビュー) ----------------------------

    /** ゴースト人形の位置を現在の ghostX/ghostY に同期 */
    private updateGhost(): void {
        if (this.ghostPlayer) this.ghostPlayer.position.set(this.ghostX * CELL, this.ghostY * CELL);
    }

    getGhostVisible(): boolean {
        return this.ghostVisible;
    }

    /** ゴースト表示を切り替える。OFF で従来のフラット描画 (全タイルを base) に戻る */
    setGhostVisible(on: boolean): void {
        if (this.ghostVisible === on) return;
        this.ghostVisible = on;
        const v3 = this.doc != null && isV3Doc(this.doc);
        if (this.ghostPlayer) this.ghostPlayer.visible = v3 && on;
        if (this.overSprite) this.overSprite.visible = v3 && on;
        // base/over の振り分けが変わるので全再描画
        this.redrawAll();
        this.flush();
    }

    /** float セル座標がゴースト人形の当たり判定内か (掴み判定) */
    ghostHitTest(fx: number, fy: number): boolean {
        if (!this.ghostVisible || this.doc == null || !isV3Doc(this.doc)) return false;
        return Math.abs(fx - this.ghostX) <= 0.5 && Math.abs(fy - this.ghostY) <= 0.6;
    }

    /** ゴーストをドラッグ移動 (float セル座標、マップ内にクランプ) */
    setGhostCellFloat(fx: number, fy: number): void {
        const doc = this.doc;
        if (!doc) return;
        this.ghostX = Math.max(0.5, Math.min(doc.width - 0.5, fx));
        this.ghostY = Math.max(0.5, Math.min(doc.height - 0.5, fy));
        this.updateGhost();
    }

    beginGhostDrag(): void {
        this.app.canvas.style.cursor = "grabbing";
    }

    endGhostDrag(): void {
        this.app.canvas.style.cursor = "crosshair";
    }

    private drawCellOvl(x: number, y: number): void {
        const doc = this.doc;
        const ctx = this.ovlCtx;
        if (!doc || !ctx) return;
        if (x < 0 || y < 0 || x >= doc.width || y >= doc.height) return;
        const t = this.texpx;
        const px = x * t;
        const py = y * t;
        ctx.clearRect(px, py, t, t);
        // 通行可否: v1 = 床のみ○ (仕様 §5.3) / v3 = §21 の解決規則
        let passable: boolean;
        if (isV3Doc(doc)) {
            passable = resolveCellV3(doc, x, y).passable;
        } else {
            passable = (doc as MapDoc).grid[y * doc.width + x] === CELL_FLOOR;
        }
        if (passable) {
            ctx.strokeStyle = "rgba(96,230,140,0.95)";
            ctx.lineWidth = Math.max(1.4, t * 0.1);
            ctx.beginPath();
            ctx.arc(px + t / 2, py + t / 2, t * 0.3, 0, Math.PI * 2);
            ctx.stroke();
        } else {
            ctx.strokeStyle = "rgba(255,96,96,0.95)";
            ctx.lineWidth = Math.max(1.4, t * 0.1);
            ctx.beginPath();
            ctx.moveTo(px + t * 0.28, py + t * 0.28);
            ctx.lineTo(px + t * 0.72, py + t * 0.72);
            ctx.moveTo(px + t * 0.72, py + t * 0.28);
            ctx.lineTo(px + t * 0.28, py + t * 0.72);
            ctx.stroke();
        }
    }

    redrawAll(): void {
        const doc = this.doc;
        if (!doc) return;
        for (let y = 0; y < doc.height; y++) {
            for (let x = 0; x < doc.width; x++) {
                this.drawCellBase(x, y);
                this.drawCellOvl(x, y);
            }
        }
    }

    /** セル変更時の再描画。v1 の柱判定は近傍に波及するため 4 近傍も描き直す */
    cellChanged(x: number, y: number): void {
        this.drawCellBase(x, y);
        if (this.doc && !isV3Doc(this.doc)) {
            this.drawCellBase(x - 1, y);
            this.drawCellBase(x + 1, y);
            this.drawCellBase(x, y - 1);
            this.drawCellBase(x, y + 1);
        }
        this.drawCellOvl(x, y);
    }

    /** キャンバス → GPU テクスチャ反映 */
    flush(): void {
        this.baseTexture?.source.update();
        this.ovlTexture?.source.update();
        this.overTexture?.source.update();
    }

    rebuildDecor(): void {
        for (const c of this.decorLayer.removeChildren()) c.destroy({ children: true });
        const doc = this.doc;
        if (!doc) return;
        for (const d of doc.decor) this.decorLayer.addChild(makeDecorIcon(d));
    }

    /**
     * 影線オーバーレイを再描画する。
     * 影モード時は濃いシアン (alpha 0.9)、それ以外は薄いシアン (alpha 0.35)。
     */
    rebuildShadow(): void {
        this.shadowLayer.clear();
        const doc = this.doc;
        if (!doc || !isV3Doc(doc) || !doc.shadow || doc.shadow.lines.length === 0) return;
        const alpha = this.shadowModeOn ? 0.9 : 0.35;
        const width = this.shadowModeOn ? 2.5 : 1.5;
        this.shadowLayer.setStrokeStyle({ width, color: 0x00e5ff, alpha });
        for (const line of doc.shadow.lines) {
            if (line.length < 4) continue;
            this.shadowLayer.moveTo(line[0] * CELL, line[1] * CELL);
            for (let j = 2; j < line.length; j += 2) {
                this.shadowLayer.lineTo(line[j] * CELL, line[j + 1] * CELL);
            }
            this.shadowLayer.stroke();
        }
    }

    /**
     * ドラッグ中の影線プレビューを更新する。
     * @param sx 始点 X (セル座標)
     * @param sy 始点 Y (セル座標)
     * @param ex 終点 X (セル座標)
     * @param ey 終点 Y (セル座標)
     */
    setShadowPreview(sx: number, sy: number, ex: number, ey: number): void {
        this.shadowPreview.clear();
        this.shadowPreview.setStrokeStyle({ width: 2.5, color: 0x00e5ff, alpha: 0.75 });
        // 始点と終点に小丸を描く
        this.shadowPreview.circle(sx * CELL, sy * CELL, 4).fill({ color: 0x00e5ff, alpha: 0.8 });
        this.shadowPreview.moveTo(sx * CELL, sy * CELL).lineTo(ex * CELL, ey * CELL).stroke();
        this.shadowPreview.circle(ex * CELL, ey * CELL, 4).fill({ color: 0x00e5ff, alpha: 0.8 });
    }

    clearShadowPreview(): void {
        this.shadowPreview.clear();
    }

    setShadowMode(on: boolean): void {
        if (this.shadowModeOn === on) return;
        this.shadowModeOn = on;
        this.rebuildShadow();
    }

    updateSpawn(): void {
        const doc = this.doc;
        if (!doc || !this.spawnMarker) return;
        this.spawnMarker.position.set((doc.spawn.x + 0.5) * CELL, (doc.spawn.y + 0.5) * CELL);
    }

    setOverlay(on: boolean): void {
        this.overlayOn = on;
        if (this.ovlSprite) this.ovlSprite.visible = on;
    }

    get overlay(): boolean {
        return this.overlayOn;
    }

    setHover(x: number | null, y: number | null): void {
        const doc = this.doc;
        if (x === null || y === null || !doc || x < 0 || y < 0 || x >= doc.width || y >= doc.height) {
            this.hover.visible = false;
            return;
        }
        this.hover.position.set(x * CELL, y * CELL);
        this.hover.visible = true;
    }

    /** 矩形ツールのドラッグ中プレビュー (セル座標は両端含む) */
    setRectPreview(x0: number, y0: number, x1: number, y1: number): void {
        const lx = Math.min(x0, x1);
        const ly = Math.min(y0, y1);
        const w = (Math.abs(x1 - x0) + 1) * CELL;
        const h = (Math.abs(y1 - y0) + 1) * CELL;
        this.rectPreview.clear();
        this.rectPreview.rect(lx * CELL, ly * CELL, w, h).fill({ color: 0x7ec8ff, alpha: 0.22 }).stroke({ width: 2, color: 0x7ec8ff, alpha: 0.95 });
        this.rectPreview.visible = true;
    }

    clearRectPreview(): void {
        this.rectPreview.visible = false;
        this.rectPreview.clear();
    }

    /** マップ全体が収まるようにビューをリセット */
    fitView(): void {
        const doc = this.doc;
        if (!doc) return;
        const sw = this.app.screen.width;
        const sh = this.app.screen.height;
        const mw = doc.width * CELL;
        const mh = doc.height * CELL;
        let s = Math.min(sw / mw, sh / mh) * 0.92;
        s = Math.min(MAX_SCALE, Math.max(MIN_SCALE, s));
        this.world.scale.set(s);
        this.world.position.set((sw - mw * s) / 2, (sh - mh * s) / 2);
    }
}
