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
    private readonly decorLayer = new Container();
    private spawnMarker: Container | null = null;
    private readonly hover = new Graphics();
    private readonly rectPreview = new Graphics();
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

        this.spawnMarker ??= buildSpawnMarker();
        this.world.addChild(this.baseSprite, this.decorLayer, this.spawnMarker, this.ovlSprite, this.rectPreview, this.hover);
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
     * ★バッジ: cellAboveMask が非ゼロ (layer.above=true または チップ over=true) のセルに表示。
     */
    private drawCellBaseV3(doc: MapDocV3, ctx: CanvasRenderingContext2D, x: number, y: number, px: number, py: number, t: number): void {
        const i = y * doc.width + x;
        ctx.fillStyle = COLOR_VOID;
        ctx.fillRect(px, py, t, t);

        // 層 0〜3 昇順で描画
        for (let li = 0; li < doc.layers.length; li++) {
            const layer = doc.layers[li];
            const tid = layer.cells[i];
            if (tid < 0) continue;
            const ts = doc.tilesets[layer.tileset];
            if (!ts) continue;
            const img = this.tilesetImgs[layer.tileset] ?? null;
            this.drawTile(ctx, ts, img, tid, px, py, t);
        }

        // ★バッジ (over=true のタイルを含むセル、または layer.above=true の層があるセル)
        const aboveMask = cellAboveMask(doc, i);
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
    }

    rebuildDecor(): void {
        for (const c of this.decorLayer.removeChildren()) c.destroy({ children: true });
        const doc = this.doc;
        if (!doc) return;
        for (const d of doc.decor) this.decorLayer.addChild(makeDecorIcon(d));
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
