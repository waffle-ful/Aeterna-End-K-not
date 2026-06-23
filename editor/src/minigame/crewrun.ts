// =============================================================
//  Crew Run — スタート画面のおまけミニゲーム (擬似3D版)
//  広告でよく見るクラウドランナー。消失点へ伸びる道に沿って、
//  ゲート/雑魚/アイテムが「奥から拡大しながら」迫ってくる 3rd person 風。
//  本物の 3D は使わず、PIXI 2D の遠近投影 (project) だけで表現する。
//  エディタ本体とは完全に独立した別 PIXI Application を全画面オーバーレイに立てる。
//  閉じる時は必ず destroy() で破棄する (リーク防止)。
// =============================================================

import { Application, Container, Graphics, Text, type Ticker } from "pixi.js";
import { applyGate, type GateOp, gateLabel, MAX_CREW } from "./gate";

export { applyGate, type GateOp, gateLabel } from "./gate";

/** 最終スコア = 残った人数。ベストスコアは localStorage に保存。 */
const BEST_KEY = "ekm_crewrun_best";
export function loadBest(): number {
    const v = Number(localStorage.getItem(BEST_KEY) ?? "0");
    return Number.isFinite(v) ? v : 0;
}
export function saveBest(score: number): number {
    const best = Math.max(loadBest(), score);
    localStorage.setItem(BEST_KEY, String(best));
    return best;
}

// ---------- チューニング定数 ----------

const MAX_SPRITES = 90; // 実際に描画するクルー最大数 (実数はラベル表示)
const CREW_H = 46; // クルー1体の基準の高さ (px・near サイズ)
const START_CREW = 6; // 開始人数 (やや少なめ = 序盤から選択が重要)
const BASE_PSPEED = 0.16; // 進行度の基準速度 (これまでの 1/2)

const COLOR_ALLY_BODY = 0x3d7bff;
const COLOR_ALLY_DARK = 0x2a57bd;
const COLOR_ALLY_VISOR = 0xbfe6ff;
const COLOR_ENEMY_BODY = 0xe0524f;
const COLOR_ENEMY_DARK = 0xb53c39;
const COLOR_ENEMY_VISOR = 0xf6c0be;

const COLOR_GATE_GOOD = 0x2f6fed;
const COLOR_GATE_BAD = 0xd64541;
const COLOR_BARREL = 0xd9772b; // 樽 = リスク (爆発)
const COLOR_GLASS = 0x7fd6e8; // ガラス = 安全

// 遠近投影パラメータ
const HORIZON_RATIO = 0.22; // 消失点 (道の最奥) の縦位置 (上げて奥行きを確保)
const FRONT_RATIO = 0.9; // クルー前線 (画面下) の縦位置
const CROWD_P = 0.88; // 群衆がいる道の進行度 (0 奥 → 1 手前)
const PERSP_POW = 1.5; // 大きいほど手前で急加速 (下げて奥の板も見えるように)
const SCALE_FAR = 0.22; // 最奥の拡大率 (上げて奥の物が小さすぎないように)
const SCALE_NEAR = 1.3; // 最手前の拡大率
const ROAD_FAR_HALF = 22; // 最奥の道の半幅 (px)
const ROAD_NEAR_HALF_RATIO = 0.52; // 最手前の道の半幅 (× W)

const FIRE_FACTOR = 8; // 火力 → ダメージ毎秒の係数
const BOSS_DIST = 3600; // この距離でボス戦へ

// ---------- アイテム種別 ----------

type ItemKind = "gun" | "minigun" | "armor" | "robot";
const ITEM_META: Record<ItemKind, { label: string; color: number }> = {
    gun: { label: "GUN", color: 0xffd75e },
    minigun: { label: "MINIGUN", color: 0xff9f43 },
    armor: { label: "ARMOR", color: 0x7bed9f },
    robot: { label: "ROBOT", color: 0xa55eea },
};

// ---------- エンティティ ----------
// p: 進行度 (0 奥 → 1 手前)。wx: 道内の横位置 (-1 左端 〜 +1 右端)。

interface EntBase {
    node: Container;
    p: number;
    resolved: boolean;
}
interface GateEnt extends EntBase {
    type: "gate";
    leftOp: GateOp;
    rightOp: GateOp;
}
interface MobEnt extends EntBase {
    type: "mob";
    wx: number;
    hp: number;
    max: number;
    hpBar: Graphics;
}
interface ArcherEnt extends EntBase {
    type: "archer";
    wx: number;
    hp: number;
    max: number;
    hpBar: Graphics;
    shootTimer: number;
}
interface HazardEnt extends EntBase {
    type: "hazard";
    wx: number;
}
interface ItemEnt extends EntBase {
    type: "item";
    wx: number;
    item: ItemKind;
    carrier: "none" | "barrel" | "glass";
}
type Entity = GateEnt | MobEnt | ArcherEnt | HazardEnt | ItemEnt;

/** 2 色を t (0..1) で線形補間する (RGB)。 */
function lerpColor(a: number, b: number, t: number): number {
    const ar = (a >> 16) & 0xff;
    const ag = (a >> 8) & 0xff;
    const ab = a & 0xff;
    const br = (b >> 16) & 0xff;
    const bg = (b >> 8) & 0xff;
    const bb = b & 0xff;
    const r = Math.round(ar + (br - ar) * t);
    const gg = Math.round(ag + (bg - ag) * t);
    const bl = Math.round(ab + (bb - ab) * t);
    return (r << 16) | (gg << 8) | bl;
}

// ---------- クルー描画 ----------

/** ミニクルーを g に 1 体描く (足元原点・高さ h)。ally なら青、敵なら赤。weapon>=0 で武器も描く。 */
function drawCrew(g: Graphics, ally: boolean, h = 22, weapon = -1): void {
    const body = ally ? COLOR_ALLY_BODY : COLOR_ENEMY_BODY;
    const dark = ally ? COLOR_ALLY_DARK : COLOR_ENEMY_DARK;
    const visor = ally ? COLOR_ALLY_VISOR : COLOR_ENEMY_VISOR;
    const w = h * 0.74;
    // 足元 (y=0) に立つように上方向へ描く
    g.ellipse(0, 2, w * 0.55, h * 0.14).fill({ color: 0x000000, alpha: 0.28 }); // 接地影
    g.roundRect(w * 0.2, -h * 0.78, w * 0.3, h * 0.55, w * 0.12).fill({ color: dark }); // バックパック
    g.roundRect(-w * 0.5, -h, w, h, w * 0.34).fill({ color: body }); // 胴
    g.roundRect(-w * 0.42, -h * 0.18, w * 0.34, h * 0.2, 3).fill({ color: dark }); // 足
    g.roundRect(w * 0.08, -h * 0.18, w * 0.34, h * 0.2, 3).fill({ color: dark });
    g.roundRect(-w * 0.46, -h * 0.84, w * 0.7, h * 0.34, w * 0.18).fill({ color: visor }); // バイザー
    // ハイライト (立体感)
    g.roundRect(-w * 0.42, -h * 0.95, w * 0.3, h * 0.5, w * 0.16).fill({ color: 0xffffff, alpha: 0.12 });
    if (weapon >= 0) drawWeapon(g, weapon, h, w);
}

/** クルーの手元に武器を描く (上＝前方に銃口)。tier 0 ピストル / 1 ライフル / 2 ミニガン。 */
function drawWeapon(g: Graphics, tier: number, h: number, w: number): void {
    const hx = w * 0.36; // 手元の x
    const hy = -h * 0.46; // 手元の y
    if (tier <= 0) {
        // ピストル
        g.roundRect(hx - 2, hy - h * 0.18, 4, h * 0.22, 1.5).fill({ color: 0x2b2f36 }); // 銃身
        g.roundRect(hx - 3, hy, 6, h * 0.12, 1).fill({ color: 0x1c1f24 }); // グリップ
    } else if (tier === 1) {
        // ライフル
        g.roundRect(hx - 2.5, hy - h * 0.38, 5, h * 0.42, 2).fill({ color: 0x23262c }); // 長い銃身
        g.roundRect(hx - 3.5, hy - h * 0.02, 7, h * 0.16, 1.5).fill({ color: 0x14161a }); // 機関部
        g.circle(hx, hy - h * 0.38, 2).fill({ color: 0x3a3f48 }); // 銃口
    } else {
        // ミニガン (太い多銃身＋発光)
        g.roundRect(hx - 6, hy - h * 0.1, 12, h * 0.26, 3).fill({ color: 0x14161a }); // 本体
        for (let b = -1; b <= 1; b++) {
            g.roundRect(hx + b * 3 - 1.4, hy - h * 0.42, 2.8, h * 0.36, 1).fill({ color: 0x2b2f36 }); // 3 銃身
        }
        g.circle(hx, hy - h * 0.42, 4).fill({ color: 0xffb13b, alpha: 0.55 }); // 銃口の熱
        g.roundRect(hx - 7, hy + h * 0.08, 5, h * 0.16, 1.5).fill({ color: 0x6a3fbf }); // 弾倉 (紫)
    }
}

function makeCrewSprite(ally: boolean, h?: number, weapon = -1): Graphics {
    const g = new Graphics();
    drawCrew(g, ally, h, weapon);
    return g;
}

/** 援軍ロボット (紫メカ) を描く。高さ h・足元原点。 */
function drawRobot(g: Graphics, h: number): void {
    const w = h * 0.82;
    g.ellipse(0, 2, w * 0.6, h * 0.13).fill({ color: 0x000000, alpha: 0.3 }); // 影
    g.roundRect(-w * 0.5, -h * 0.92, w, h * 0.92, w * 0.16).fill({ color: 0x6a3fbf }); // 胴
    g.roundRect(-w * 0.5, -h * 0.92, w, h * 0.3, w * 0.16).fill({ color: 0x8455e0 }); // 上部明
    g.roundRect(-w * 0.6, -h * 0.7, w * 0.2, h * 0.5, 3).fill({ color: 0x4f2e94 }); // 左腕
    g.roundRect(w * 0.4, -h * 0.7, w * 0.2, h * 0.5, 3).fill({ color: 0x4f2e94 }); // 右腕
    g.roundRect(-w * 0.34, -h * 0.66, w * 0.68, h * 0.22, 4).fill({ color: 0x120a22 }); // 顔パネル
    g.roundRect(-w * 0.24, -h * 0.6, w * 0.48, h * 0.08, 3).fill({ color: 0xff5a5a }); // 赤い目
    g.roundRect(-w * 0.24, -h * 0.6, w * 0.48, h * 0.08, 3).stroke({ color: 0xffd0d0, width: 1, alpha: 0.6 });
    g.roundRect(-w * 0.36, -h * 0.18, w * 0.28, h * 0.18, 2).fill({ color: 0x4f2e94 }); // 脚
    g.roundRect(w * 0.08, -h * 0.18, w * 0.28, h * 0.18, 2).fill({ color: 0x4f2e94 });
}

/** 敵アーチャー (緑のフード弓兵) を描く。高さ h・足元原点。 */
function drawArcher(g: Graphics, h: number): void {
    const w = h * 0.72;
    g.ellipse(0, 2, w * 0.55, h * 0.13).fill({ color: 0x000000, alpha: 0.28 }); // 影
    g.roundRect(-w * 0.5, -h, w, h, w * 0.3).fill({ color: 0x2f7d4f }); // 胴 (緑フード)
    g.roundRect(-w * 0.5, -h, w, h * 0.34, w * 0.3).fill({ color: 0x3c9c62 }); // フード上部明
    g.roundRect(-w * 0.4, -h * 0.74, w * 0.55, h * 0.26, w * 0.14).fill({ color: 0xe8c9a0 }); // 顔
    g.roundRect(-w * 0.4, -h * 0.18, w * 0.34, h * 0.2, 3).fill({ color: 0x215a39 }); // 脚
    g.roundRect(w * 0.06, -h * 0.18, w * 0.34, h * 0.2, 3).fill({ color: 0x215a39 });
    // 弓 (右手前で構える)
    g.arc(w * 0.4, -h * 0.5, h * 0.34, -Math.PI * 0.55, Math.PI * 0.55).stroke({ color: 0x8a5a2b, width: 3 });
    g.moveTo(w * 0.4, -h * 0.84).lineTo(w * 0.4, -h * 0.16).stroke({ color: 0xeeeeee, width: 1.5, alpha: 0.8 }); // 弦
}

// ---------- ゲート op 生成 ----------

function randGoodOp(diff: number): GateOp {
    const r = Math.random();
    if (r < 0.55) return { kind: "*", value: 2 + (diff > 0.55 ? 1 : 0) };
    if (r < 0.85) return { kind: "+", value: 6 + Math.floor(diff * 28) };
    return { kind: "*", value: 3 };
}
function randBadOp(diff: number): GateOp {
    const r = Math.random();
    if (r < 0.5) return { kind: "/", value: 2 + (diff > 0.5 ? 1 : 0) };
    if (r < 0.85) return { kind: "-", value: 8 + Math.floor(diff * 40) };
    return { kind: "+", value: 2 };
}

/** ゲート対を作る。両得 (大きい方を取れ)・両損 (小さい方を取れ)・得損混在 の3モードで読みを要求する。 */
function makeGatePair(diff: number): { left: GateOp; right: GateOp } {
    const mode = Math.random();
    if (mode < 0.25) {
        // 両得: より増える方を選ばせる
        return { left: randGoodOp(diff), right: randGoodOp(diff) };
    }
    if (mode < 0.5) {
        // 両損: 損が小さい方を選ばせる (避けられない)
        return { left: randBadOp(diff), right: randBadOp(diff) };
    }
    // 得損混在
    const goodLeft = Math.random() < 0.5;
    return {
        left: goodLeft ? randGoodOp(diff) : randBadOp(diff),
        right: goodLeft ? randBadOp(diff) : randGoodOp(diff),
    };
}

// ---------- ゲーム本体 ----------

type Phase = "running" | "battle" | "clear" | "result";

export interface CrewRunHandle {
    destroy(): void;
}

/**
 * Crew Run を host 要素内で起動する。onExit は「もどる」押下/閉じるで呼ばれる。
 * 返り値の destroy() で PIXI を完全破棄する。
 */
export async function launchCrewRun(host: HTMLElement, onExit: () => void): Promise<CrewRunHandle> {
    const app = new Application();
    await app.init({
        resizeTo: host,
        background: "#6fb3d6",
        antialias: true,
        resolution: Math.min(globalThis.devicePixelRatio || 1, 2),
        autoDensity: true,
    });
    host.appendChild(app.canvas);
    app.canvas.style.touchAction = "none";

    const game = new CrewRun(app, onExit);
    game.start();

    return {
        destroy() {
            game.dispose();
            app.destroy(true, { children: true, texture: true });
        },
    };
}

class CrewRun {
    private readonly app: Application;
    private readonly onExit: () => void;

    private readonly scene = new Container(); // 画面シェイク対象 (UI は除外)
    private readonly bg = new Container();
    private readonly rungs = new Graphics(); // 流れる路面ライン
    private readonly worldLayer = new Container(); // 流れてくるエンティティ
    private readonly crowdLayer = new Container(); // クルー軍団
    private readonly fxLayer = new Container(); // 弾・エフェクト
    private readonly uiLayer = new Container(); // HUD / リザルト
    private shake = 0; // 画面シェイク残量

    private phase: Phase = "running";
    private crew = START_CREW;
    private weaponTier = 0; // 0 ピストル / 1 ガン / 2 ミニガン
    private armor = 0;
    private robots = 0;

    private steerWx = 0; // 群衆の目標 wx (-1〜1)
    private centroidWx = 0; // 群衆の現在 wx
    private distance = 0; // 進んだ距離 (スコア兼進行度)
    private pSpeed = BASE_PSPEED; // 進行度の進む速さ (1/sec)
    private stage = 1; // ステージ番号 (クリアで増加)
    private clearTimer = 0; // ステージクリア演出の残り秒
    private roadPhase = 0; // 路面ラインのスクロール位相
    private spawnTimer = 0;
    private spawnInterval = 1.0;
    private nextLane = 0;

    private readonly entities: Entity[] = [];
    private readonly crewSprites: Graphics[] = [];
    private readonly crewOffsets: { wx: number; row: number }[] = [];
    private readonly robotSprites: Graphics[] = [];
    private readonly bullets: { node: Graphics; vx: number; vy: number; life: number }[] = [];
    private readonly enemyShots: { node: Graphics; wx: number; p: number; resolved: boolean }[] = []; // 敵の矢

    private boss: { node: Container; hp: number; max: number; hpBar: Graphics; p: number } | null = null;
    private fireCooldown = 0;
    private time = 0;

    private hudCount!: Text;
    private hudWeapon!: Text;
    private hudDist!: Text;
    private bestAtStart = loadBest();

    private readonly tick = (t: Ticker) => this.update(t.deltaMS / 1000);
    private disposed = false;

    constructor(app: Application, onExit: () => void) {
        this.app = app;
        this.onExit = onExit;
        this.worldLayer.sortableChildren = true;
        this.crowdLayer.sortableChildren = true;
        this.scene.addChild(this.bg, this.rungs, this.worldLayer, this.crowdLayer, this.fxLayer);
        app.stage.addChild(this.scene, this.uiLayer);
    }

    private get W() {
        return this.app.screen.width;
    }
    private get H() {
        return this.app.screen.height;
    }
    private get firepower() {
        const mult = this.weaponTier === 2 ? 3 : this.weaponTier === 1 ? 1.8 : 1;
        return this.crew * mult + this.robots * 45;
    }
    private get lossMult() {
        return 1 / (1 + this.armor * 0.6);
    }
    private get diff() {
        return Math.min(1, this.distance / 3200);
    }

    // ---------- 遠近投影 ----------
    /** 進行度 p (0 奥→1 手前) を画面座標・拡大率・道半幅へ写す。 */
    private project(p: number): { y: number; scale: number; halfW: number } {
        const horizonY = this.H * HORIZON_RATIO;
        const frontY = this.H * FRONT_RATIO;
        const pe = Math.pow(Math.max(0, p), PERSP_POW);
        const y = horizonY + (frontY - horizonY) * pe;
        const peC = Math.min(pe, 1.15);
        const scale = SCALE_FAR + (SCALE_NEAR - SCALE_FAR) * peC;
        const nearHalf = this.W * ROAD_NEAR_HALF_RATIO;
        const halfW = ROAD_FAR_HALF + (nearHalf - ROAD_FAR_HALF) * pe;
        return { y, scale, halfW };
    }
    private screenX(p: number, wx: number): number {
        return this.W / 2 + wx * this.project(p).halfW;
    }
    /** ビルボード (near サイズで焼いたスプライト) を p に応じて縮める倍率。 */
    private bill(p: number): number {
        return this.project(p).scale / SCALE_NEAR;
    }

    start(): void {
        this.buildBackground();
        this.buildHud();
        this.rebuildCrowd();
        this.bindInput();
        this.app.ticker.add(this.tick);
    }

    dispose(): void {
        if (this.disposed) return;
        this.disposed = true;
        this.app.ticker.remove(this.tick);
        this.unbindInput();
    }

    // ---------- 背景 (空・海・桟橋) ----------
    private buildBackground(): void {
        this.bg.removeChildren();
        const g = new Graphics();
        const horizonY = this.H * HORIZON_RATIO;
        const cx = this.W / 2;
        // 空 (上から下へ青のグラデを帯で近似)
        const skyBands = 8;
        for (let i = 0; i < skyBands; i++) {
            const t = i / skyBands;
            const col = lerpColor(0x4a86c4, 0xbfe0f2, t);
            g.rect(0, (horizonY * i) / skyBands, this.W, horizonY / skyBands + 1).fill({ color: col });
        }
        // 海 (地平線から下、奥は明るく手前は濃く)
        const seaBands = 10;
        for (let i = 0; i < seaBands; i++) {
            const t = i / seaBands;
            const col = lerpColor(0x4fb3d9, 0x1d6f9e, t);
            g.rect(0, horizonY + ((this.H - horizonY) * i) / seaBands, this.W, (this.H - horizonY) / seaBands + 1).fill({
                color: col,
            });
        }
        // 海のさざ波 (薄い横線)
        for (let i = 1; i < 9; i++) {
            const y = horizonY + ((this.H - horizonY) * i) / 9;
            g.rect(0, y, this.W, 2).fill({ color: 0xffffff, alpha: 0.06 + i * 0.01 });
        }
        // 桟橋 (台形の板敷き): 奥は細く手前は太く
        const top = this.project(0);
        const bot = this.project(1.0);
        g.poly([cx - top.halfW, top.y, cx + top.halfW, top.y, cx + bot.halfW, bot.y, cx - bot.halfW, bot.y]).fill({
            color: 0xb07a43,
        });
        // 桟橋の縁 (濃い木の側面)
        g.poly([cx - top.halfW, top.y, cx - bot.halfW, bot.y, cx - bot.halfW + 10, bot.y, cx - top.halfW, top.y]).fill({
            color: 0x7c5128,
        });
        g.poly([cx + top.halfW, top.y, cx + bot.halfW, bot.y, cx + bot.halfW - 10, bot.y, cx + top.halfW, top.y]).fill({
            color: 0x7c5128,
        });
        // 霧 (地平線をぼかす)
        g.rect(0, horizonY - 14, this.W, 28).fill({ color: 0xbfe0f2, alpha: 0.4 });
        this.bg.addChild(g);
    }

    /** 桟橋の板の継ぎ目を毎フレーム再描画してスクロールさせる (スピード感)。 */
    private drawRungs(): void {
        const g = this.rungs;
        g.clear();
        const N = 16;
        const cx = this.W / 2;
        for (let k = 0; k < N; k++) {
            const p = (k / N + this.roadPhase) % 1;
            const pr = this.project(p);
            // 板1枚ぶんの陰影 (継ぎ目の濃い線＋上の明るい面)
            g.rect(cx - pr.halfW, pr.y, pr.halfW * 2, Math.max(1.5, p * 5)).fill({ color: 0x6e4624, alpha: 0.55 });
            g.rect(cx - pr.halfW, pr.y - Math.max(1, p * 3), pr.halfW * 2, Math.max(1, p * 3)).fill({
                color: 0xc89055,
                alpha: 0.35,
            });
        }
        // 板の縦の継ぎ目 (3本) で板張り感
        for (const f of [-0.5, 0, 0.5]) {
            const a = this.project(0.04);
            const b = this.project(1.0);
            g.poly([this.screenX(0.04, f), a.y, this.screenX(1.0, f), b.y]).stroke({
                color: 0x6e4624,
                width: 2,
                alpha: 0.35,
            });
        }
    }

    // ---------- HUD ----------
    private buildHud(): void {
        const bar = new Graphics();
        bar.roundRect(8, 8, this.W - 16, 40, 10).fill({ color: 0x000000, alpha: 0.4 });
        this.uiLayer.addChild(bar);

        this.hudCount = this.label("👥 6", 20, "#bfe6ff", 700);
        this.hudCount.position.set(18, 16);
        this.hudWeapon = this.label("🔫 ピストル", 16, "#ffd75e", 600);
        this.hudWeapon.position.set(this.W * 0.4, 19);
        this.hudDist = this.label("0m", 16, "#9fb3c8", 600);
        this.hudDist.anchor.set(1, 0);
        this.hudDist.position.set(this.W - 60, 19);
        this.uiLayer.addChild(this.hudCount, this.hudWeapon, this.hudDist);

        const close = this.button("✕", this.W - 46, 14, 32, 28, 0xd64541);
        close.on("pointertap", () => this.exit());
        this.uiLayer.addChild(close);
    }

    private label(text: string, size: number, fill: string, weight = 400): Text {
        return new Text({
            text,
            style: {
                fill,
                fontSize: size,
                fontWeight: String(weight) as "400" | "600" | "700",
                fontFamily: "system-ui, -apple-system, sans-serif",
                align: "center",
                stroke: { color: 0x06101c, width: Math.max(2, size * 0.12) },
            },
        });
    }

    private button(text: string, x: number, y: number, w: number, h: number, color: number): Container {
        const c = new Container();
        c.position.set(x, y);
        const g = new Graphics();
        g.roundRect(0, 0, w, h, 8).fill({ color });
        const t = this.label(text, Math.min(18, h * 0.6), "#ffffff", 700);
        t.anchor.set(0.5);
        t.position.set(w / 2, h / 2);
        c.addChild(g, t);
        c.eventMode = "static";
        c.cursor = "pointer";
        return c;
    }

    private updateHud(): void {
        this.hudCount.text = `👥 ${this.crew}`;
        const wname = this.weaponTier === 2 ? "ミニガン" : this.weaponTier === 1 ? "ガン" : "ピストル";
        const extra = this.robots > 0 ? ` 🤖×${this.robots}` : "";
        const arm = this.armor > 0 ? ` 🛡×${this.armor}` : "";
        this.hudWeapon.text = `🔫 ${wname}${extra}${arm}`;
        this.hudDist.text = `S${this.stage} · ${Math.floor(this.distance / 10)}m`;
    }

    // ---------- 入力 ----------
    private pointerDown = false;
    private readonly onPointerDown = (e: PointerEvent) => {
        this.pointerDown = true;
        this.steerFromClient(e.clientX);
    };
    private readonly onPointerMove = (e: PointerEvent) => {
        if (this.pointerDown) this.steerFromClient(e.clientX);
    };
    private readonly onPointerUp = () => {
        this.pointerDown = false;
    };

    private steerFromClient(clientX: number): void {
        const rect = this.app.canvas.getBoundingClientRect();
        const rel = (clientX - rect.left) / rect.width; // 0〜1
        this.steerWx = Math.max(-0.92, Math.min(0.92, rel * 2 - 1));
    }

    private bindInput(): void {
        const c = this.app.canvas;
        c.addEventListener("pointerdown", this.onPointerDown);
        c.addEventListener("pointermove", this.onPointerMove);
        globalThis.addEventListener("pointerup", this.onPointerUp);
    }
    private unbindInput(): void {
        const c = this.app.canvas;
        c.removeEventListener("pointerdown", this.onPointerDown);
        c.removeEventListener("pointermove", this.onPointerMove);
        globalThis.removeEventListener("pointerup", this.onPointerUp);
    }

    // ---------- 群衆 ----------
    private rebuildCrowd(): void {
        const want = Math.min(MAX_SPRITES, Math.max(1, this.crew));
        while (this.crewSprites.length < want) {
            const s = makeCrewSprite(true, CREW_H, this.weaponTier);
            this.crowdLayer.addChild(s);
            this.crewSprites.push(s);
        }
        while (this.crewSprites.length > want) {
            const s = this.crewSprites.pop()!;
            s.destroy();
        }
        // 隊列オフセット再計算
        this.crewOffsets.length = 0;
        const n = this.crewSprites.length;
        const cols = Math.max(1, Math.ceil(Math.sqrt(n * 1.7)));
        for (let i = 0; i < n; i++) {
            const col = i % cols;
            const row = Math.floor(i / cols);
            this.crewOffsets.push({ wx: (col - (cols - 1) / 2) * 0.058, row });
        }
    }

    /** 武器を取得したとき、既存クルーの絵を現在の武器で描き直す。 */
    private restyleCrew(): void {
        for (const s of this.crewSprites) {
            s.clear();
            drawCrew(s, true, CREW_H, this.weaponTier);
        }
    }

    /** 援軍ロボットの数を robots に合わせる (最大6体表示)。 */
    private rebuildRobots(): void {
        const want = Math.min(6, this.robots);
        while (this.robotSprites.length < want) {
            const g = new Graphics();
            drawRobot(g, CREW_H * 1.9);
            this.crowdLayer.addChild(g);
            this.robotSprites.push(g);
        }
        while (this.robotSprites.length > want) {
            this.robotSprites.pop()!.destroy();
        }
    }

    private layoutCrowd(): void {
        const n = this.crewSprites.length;
        for (let i = 0; i < n; i++) {
            const off = this.crewOffsets[i];
            const jitter = Math.sin(this.time * 9 + i) * 0.004;
            const p = CROWD_P - off.row * 0.022;
            const wx = this.centroidWx + off.wx + jitter;
            const pr = this.project(p);
            const s = this.crewSprites[i];
            s.position.set(this.screenX(p, wx), pr.y + Math.abs(Math.sin(this.time * 9 + i)) * 2);
            s.scale.set(this.bill(p));
            s.zIndex = pr.y;
        }
        // ロボットは群衆の少し後方・左右に配置 (大きく目立つ)
        const rn = this.robotSprites.length;
        for (let i = 0; i < rn; i++) {
            const p = CROWD_P - 0.06;
            const side = (i % 2 === 0 ? -1 : 1) * (0.28 + Math.floor(i / 2) * 0.16);
            const wx = this.centroidWx + side;
            const pr = this.project(p);
            const r = this.robotSprites[i];
            r.position.set(this.screenX(p, wx), pr.y + Math.abs(Math.sin(this.time * 5 + i)) * 2);
            r.scale.set(this.bill(p));
            r.zIndex = pr.y - 1;
        }
    }

    // ---------- メインループ ----------
    private update(dt: number): void {
        dt = Math.min(dt, 0.05);
        this.time += dt;
        if (this.phase === "running") this.updateRunning(dt);
        else if (this.phase === "battle") this.updateBattle(dt);
        else if (this.phase === "clear") this.updateClear(dt);

        this.centroidWx += (this.steerWx - this.centroidWx) * Math.min(1, dt * 11);
        this.roadPhase = (this.roadPhase + dt * (0.35 + this.pSpeed * 0.6)) % 1;
        this.drawRungs();
        this.layoutCrowd();
        this.updateBullets(dt);
        this.updateEnemyShots(dt);
        this.updateHud();

        // 画面シェイク
        this.shake *= 0.86;
        if (this.shake > 0.3) {
            this.scene.position.set((Math.random() - 0.5) * this.shake, (Math.random() - 0.5) * this.shake);
        } else if (this.scene.x !== 0 || this.scene.y !== 0) {
            this.scene.position.set(0, 0);
        }
    }

    private updateRunning(dt: number): void {
        this.distance += (70 + this.diff * 65) * dt;
        this.pSpeed = BASE_PSPEED + this.diff * 0.15;
        this.spawnInterval = 1.05 - this.diff * 0.45;

        this.spawnTimer -= dt;
        if (this.spawnTimer <= 0) {
            this.spawnTimer = this.spawnInterval;
            this.spawnRandom();
        }

        // ステージ目標距離に到達したらボス戦へ
        if (this.distance > this.stageGoal && !this.boss && this.entities.length === 0) {
            this.startBattle();
            return;
        }

        this.autoFire(dt);

        for (const e of this.entities) {
            e.p += this.pSpeed * dt;
            this.placeEntity(e);
            this.resolveEntity(e, dt);
        }
        for (let i = this.entities.length - 1; i >= 0; i--) {
            const e = this.entities[i];
            const dead = (e.type === "mob" || e.type === "archer") && e.hp <= 0;
            if (e.p > 1.12 || dead) {
                if (dead) this.spark(e.node.x, e.node.y, 0xff8f5a);
                e.node.destroy();
                this.entities.splice(i, 1);
            }
        }

        if (this.crew <= 0) this.defeat();
    }

    /** ステージ目標距離 (ステージが進むほど長い)。 */
    private get stageGoal(): number {
        return BOSS_DIST + (this.stage - 1) * 700;
    }
    /** 敵の強さ倍率 (ステージで上昇)。 */
    private get stageMul(): number {
        return 1 + (this.stage - 1) * 0.4;
    }

    private updateClear(dt: number): void {
        this.clearTimer -= dt;
        if (this.clearTimer <= 0) this.nextStage();
    }

    /** エンティティを現在の p に応じて配置・拡大する。 */
    private placeEntity(e: Entity): void {
        const pr = this.project(e.p);
        if (e.type === "gate") {
            e.node.position.set(this.W / 2, pr.y);
            e.node.scale.set(this.bill(e.p));
        } else {
            e.node.position.set(this.screenX(e.p, e.wx), pr.y);
            e.node.scale.set(this.bill(e.p));
        }
        e.node.zIndex = pr.y;
    }

    // ---------- スポーン ----------
    private spawnRandom(): void {
        const r = Math.random();
        this.nextLane++;
        // ステージが上がるほどギミックが解禁: アーチャー(2〜)・スパイク(2〜・3 で増加)
        const archerCh = this.stage >= 2 ? 0.12 + (this.stage - 2) * 0.04 : 0;
        const hazardCh = this.stage >= 2 ? 0.1 + (this.stage - 2) * 0.04 : 0;
        if (r < 0.4) this.spawnGate();
        else if (r < 0.4 + archerCh) this.spawnArcher();
        else if (r < 0.4 + archerCh + hazardCh) this.spawnHazard();
        else if (r < 0.72) this.spawnMob();
        else this.spawnItem();
    }

    private spawnGate(): void {
        const node = new Container();
        const { left, right } = makeGatePair(this.diff);
        const nearHalf = this.W * ROAD_NEAR_HALF_RATIO;
        const h = nearHalf * 0.62;
        const mk = (op: GateOp, sign: -1 | 1) => {
            const inc = applyGate(this.crew, op) >= this.crew;
            const metal = inc ? COLOR_GATE_GOOD : COLOR_GATE_BAD;
            const metalLo = inc ? 0x1f4fb0 : 0x9e2f2c;
            const wrap = new Container();
            const w = nearHalf * 0.94;
            const g = new Graphics();
            // 木箱 (クレート) の本体
            g.roundRect(-w / 2, -h, w, h, 6).fill({ color: 0x9a6b3a });
            g.roundRect(-w / 2, -h, w, h * 0.16, 6).fill({ color: 0xc08f54 }); // 上面の明るい面
            // 木の縦板
            for (let i = 1; i < 4; i++) {
                const x = -w / 2 + (w * i) / 4;
                g.rect(x - 1, -h, 2, h).fill({ color: 0x7c5128, alpha: 0.5 });
            }
            // 金属の枠 (上下の帯＋角)
            g.roundRect(-w / 2, -h, w, h * 0.26, 6).fill({ color: metal });
            g.roundRect(-w / 2, -h * 0.26, w, h * 0.26, 6).fill({ color: metalLo });
            g.roundRect(-w / 2, -h, w, h, 6).stroke({ color: 0x10131a, width: 3, alpha: 0.5 });
            // リベット
            for (const rx of [-w / 2 + 8, w / 2 - 8]) {
                g.circle(rx, -h + h * 0.13, 3).fill({ color: 0xd9e2ec, alpha: 0.8 });
                g.circle(rx, -h * 0.13, 3).fill({ color: 0xd9e2ec, alpha: 0.8 });
            }
            const t = this.label(gateLabel(op), 52, "#ffffff", 700);
            t.anchor.set(0.5);
            t.position.set(0, -h * 0.5);
            wrap.addChild(g, t);
            wrap.position.set(sign * (nearHalf / 2), 0);
            return wrap;
        };
        node.addChild(mk(left, -1));
        node.addChild(mk(right, 1));
        this.worldLayer.addChild(node);
        this.entities.push({ type: "gate", node, p: 0, resolved: false, leftOp: left, rightOp: right });
    }

    private spawnMob(): void {
        const node = new Container();
        const count = Math.floor((6 + this.diff * 34 + Math.random() * 14) * this.stageMul);
        const wx = this.laneWx();
        const show = Math.min(10, count);
        for (let i = 0; i < show; i++) {
            const s = makeCrewSprite(false, 26);
            const col = i % 4;
            const row = Math.floor(i / 4);
            s.position.set((col - 1.5) * 18, -row * 14);
            s.zIndex = -row;
            node.addChild(s);
        }
        const hpBar = new Graphics();
        hpBar.position.set(0, 8);
        node.addChild(hpBar);
        const lbl = this.label(`${count}`, 22, "#ffd0cf", 700);
        lbl.anchor.set(0.5, 1);
        lbl.position.set(0, -44);
        node.addChild(lbl);
        this.worldLayer.addChild(node);
        const e: MobEnt = { type: "mob", node, p: 0, resolved: false, wx, hp: count, max: count, hpBar };
        this.placeEntity(e);
        this.drawMobHp(e);
        this.entities.push(e);
    }

    private spawnArcher(): void {
        const node = new Container();
        const wx = Math.random() < 0.5 ? -0.6 - Math.random() * 0.3 : 0.6 + Math.random() * 0.3;
        const hp = Math.ceil((10 + this.diff * 18) * this.stageMul);
        const a = new Graphics();
        drawArcher(a, 46);
        node.addChild(a);
        const hpBar = new Graphics();
        hpBar.position.set(0, 8);
        node.addChild(hpBar);
        const lbl = this.label("🏹", 22, "#ffe0a0", 700);
        lbl.anchor.set(0.5, 1);
        lbl.position.set(0, -52);
        node.addChild(lbl);
        this.worldLayer.addChild(node);
        const e: ArcherEnt = { type: "archer", node, p: 0, resolved: false, wx, hp, max: hp, hpBar, shootTimer: 0.8 };
        this.placeEntity(e);
        this.drawMobHp(e);
        this.entities.push(e);
    }

    private spawnHazard(): void {
        const node = new Container();
        const wx = this.laneWx();
        const g = new Graphics();
        // スパイク台 (避けて通る障害)
        g.roundRect(-34, -20, 68, 20, 5).fill({ color: 0x4a4f57 }); // 台座
        g.roundRect(-34, -20, 68, 6, 5).fill({ color: 0x6b727c });
        for (let i = -2; i <= 2; i++) {
            g.poly([i * 13 - 6, -20, i * 13 + 6, -20, i * 13, -44]).fill({ color: 0xcfd6df }); // 棘
            g.poly([i * 13 - 6, -20, i * 13 + 6, -20, i * 13, -44]).stroke({ color: 0x8a929c, width: 1 });
        }
        node.addChild(g);
        this.worldLayer.addChild(node);
        const e: HazardEnt = { type: "hazard", node, p: 0, resolved: false, wx };
        this.placeEntity(e);
        this.entities.push(e);
    }

    private spawnItem(): void {
        const node = new Container();
        const kinds: ItemKind[] = ["gun", "minigun", "armor", "robot"];
        const item = kinds[Math.floor(Math.random() * kinds.length)];
        const wx = this.laneWx();
        const cr = Math.random();
        const carrier: ItemEnt["carrier"] = cr < 0.45 ? "none" : cr < 0.72 ? "glass" : "barrel";
        const meta = ITEM_META[item];

        if (carrier !== "none") {
            const cc = carrier === "barrel" ? COLOR_BARREL : COLOR_GLASS;
            const box = new Graphics();
            box.roundRect(-32, -56, 64, 56, 8).fill({ color: cc, alpha: carrier === "glass" ? 0.5 : 0.96 }); // 正面
            box.roundRect(-32, -56, 64, 12, 8).fill({ color: 0xffffff, alpha: 0.18 }); // 上面
            box.roundRect(-32, -56, 64, 56, 8).stroke({ color: 0xffffff, width: 3, alpha: 0.7 });
            if (carrier === "barrel") {
                box.rect(-32, -38, 64, 6).fill({ color: 0x8a4a18 });
                box.rect(-32, -22, 64, 6).fill({ color: 0x8a4a18 });
            }
            node.addChild(box);
            const warn = this.label(carrier === "barrel" ? "⚠" : "🔒", 26, "#ffffff", 700);
            warn.anchor.set(0.5);
            warn.position.set(0, -70);
            node.addChild(warn);
        }
        const ic = new Graphics();
        ic.circle(0, -22, 22).fill({ color: meta.color });
        ic.circle(-7, -29, 7).fill({ color: 0xffffff, alpha: 0.5 }); // ハイライト
        ic.circle(0, -22, 22).stroke({ color: 0xffffff, width: 3 });
        node.addChild(ic);
        const t = this.label(meta.label, 15, "#1a1a1a", 700);
        t.anchor.set(0.5);
        t.position.set(0, 6);
        node.addChild(t);

        this.worldLayer.addChild(node);
        const e: ItemEnt = { type: "item", node, p: 0, resolved: false, wx, item, carrier };
        this.placeEntity(e);
        this.entities.push(e);
    }

    /** 道内のランダム横位置 (左右どちらかへ寄せて読みを作る)。 */
    private laneWx(): number {
        const side = (this.nextLane % 2 === 0) === Math.random() < 0.5;
        return side ? -0.2 - Math.random() * 0.45 : 0.2 + Math.random() * 0.45;
    }

    // ---------- エンティティ解決 ----------
    private resolveEntity(e: Entity, dt: number): void {
        if (e.type === "mob") {
            if (!e.resolved && e.p >= CROWD_P) {
                e.resolved = true;
                if (e.hp > 0) {
                    this.changeCrew(-Math.ceil(e.hp * this.lossMult));
                    this.flashDamage();
                }
            }
            this.drawMobHp(e);
            return;
        }
        if (e.type === "archer") {
            // 接近中は一定間隔で矢を放つ
            if (e.hp > 0 && e.p > 0.15 && e.p < CROWD_P) {
                e.shootTimer -= dt;
                if (e.shootTimer <= 0) {
                    e.shootTimer = 1.4;
                    this.fireArrow(e);
                }
            }
            if (!e.resolved && e.p >= CROWD_P) {
                e.resolved = true;
                if (e.hp > 0) {
                    this.changeCrew(-Math.ceil(4 * this.stageMul * this.lossMult));
                    this.flashDamage();
                }
            }
            this.drawMobHp(e);
            return;
        }
        if (e.resolved) return;
        if (e.p < CROWD_P) return;
        e.resolved = true;

        if (e.type === "gate") {
            const op = this.centroidWx < 0 ? e.leftOp : e.rightOp;
            const before = this.crew;
            this.crew = applyGate(this.crew, op);
            this.rebuildCrowd();
            this.popText(
                gateLabel(op),
                this.screenX(CROWD_P, this.centroidWx),
                this.project(CROWD_P).y - 70,
                this.crew >= before ? "#7bd6ff" : "#ff8f8f",
            );
            if (this.crew <= 0) this.defeat();
            return;
        }

        if (e.type === "hazard") {
            // スパイク: 重なって踏むとクルーを失う (避けて通れ)
            if (Math.abs(this.centroidWx - e.wx) <= 0.16) {
                this.changeCrew(-Math.ceil(this.crew * 0.15 * this.lossMult + 4));
                this.popText("✖", this.screenX(CROWD_P, e.wx), this.project(CROWD_P).y - 40, "#ff8f8f");
                this.flashDamage();
                if (this.crew <= 0) this.defeat();
            }
            return;
        }

        // item
        if (Math.abs(this.centroidWx - e.wx) <= 0.22) {
            const ix = this.screenX(CROWD_P, e.wx);
            if (e.carrier === "barrel") {
                this.changeCrew(-Math.max(3, Math.ceil(this.crew * 0.2 * this.lossMult)));
                this.popText("💥", ix, this.project(CROWD_P).y - 50, "#ffb86b");
                this.flashDamage();
            } else if (e.carrier === "glass") {
                this.popText("🔓", ix, this.project(CROWD_P).y - 50, "#9fe9ff");
            }
            this.applyItem(e.item, ix);
        }
    }

    private applyItem(item: ItemKind, sx: number): void {
        switch (item) {
            case "gun":
                this.weaponTier = Math.max(this.weaponTier, 1);
                this.restyleCrew();
                break;
            case "minigun":
                this.weaponTier = 2;
                this.restyleCrew();
                break;
            case "armor":
                this.armor++;
                break;
            case "robot":
                this.robots++;
                this.rebuildRobots();
                break;
        }
        this.popText(ITEM_META[item].label, sx, this.project(CROWD_P).y - 90, "#fff3b0");
        this.shake = Math.max(this.shake, 6);
    }

    private changeCrew(delta: number): void {
        this.crew = Math.max(0, Math.min(MAX_CREW, this.crew + delta));
        this.rebuildCrowd();
    }

    private drawMobHp(e: MobEnt | ArcherEnt): void {
        const w = 56;
        e.hpBar.clear();
        e.hpBar.roundRect(-w / 2, 0, w, 7, 3).fill({ color: 0x000000, alpha: 0.5 });
        const ratio = Math.max(0, e.hp / e.max);
        e.hpBar.roundRect(-w / 2, 0, w * ratio, 7, 3).fill({ color: 0xff5a5a });
    }

    // ---------- 射撃 ----------
    private autoFire(dt: number): void {
        // 最も手前の生きた敵 (雑魚 or アーチャー) を狙う
        let target: MobEnt | ArcherEnt | null = null;
        for (const e of this.entities) {
            if ((e.type === "mob" || e.type === "archer") && e.hp > 0 && e.p < CROWD_P && e.p > 0.12) {
                if (!target || e.p > target.p) target = e;
            }
        }
        if (target) {
            target.hp -= this.firepower * FIRE_FACTOR * dt;
            this.spawnBullets(dt, target.node.x, target.node.y);
        }
    }

    /** アーチャーが現在のクルー位置へ矢を放つ。 */
    private fireArrow(a: ArcherEnt): void {
        const g = new Graphics();
        g.roundRect(-2, -11, 4, 22, 1.5).fill({ color: 0x5a3a1c });
        g.poly([-5, -7, 5, -7, 0, -15]).fill({ color: 0xd0d6de }); // 鏃
        g.rotation = 0; // 進行方向は updateEnemyShots でそのまま下向き
        this.fxLayer.addChild(g);
        // 発射時のクルー位置へ向けて落とす (少しブレ)
        const targetWx = this.centroidWx + (Math.random() - 0.5) * 0.2;
        this.enemyShots.push({ node: g, wx: targetWx, p: Math.max(0.18, a.p), resolved: false });
    }

    private updateEnemyShots(dt: number): void {
        const speed = this.pSpeed * 2.4;
        for (let i = this.enemyShots.length - 1; i >= 0; i--) {
            const s = this.enemyShots[i];
            s.p += speed * dt;
            const pr = this.project(s.p);
            s.node.position.set(this.screenX(s.p, s.wx), pr.y);
            s.node.scale.set(this.bill(s.p));
            if (!s.resolved && s.p >= CROWD_P) {
                s.resolved = true;
                if (Math.abs(this.centroidWx - s.wx) <= 0.16) {
                    this.changeCrew(-Math.ceil(3 * this.stageMul * this.lossMult));
                    this.flashDamage();
                    if (this.crew <= 0) this.defeat();
                }
            }
            if (s.p > 1.1) {
                s.node.destroy();
                this.enemyShots.splice(i, 1);
            }
        }
    }

    private spawnBullets(dt: number, tx: number, ty: number): void {
        this.fireCooldown -= dt;
        if (this.fireCooldown > 0) return;
        const tier = this.weaponTier;
        this.fireCooldown = tier === 2 ? 0.03 : tier === 1 ? 0.05 : 0.08;
        const n = tier === 2 ? 6 : tier === 1 ? 3 : 1;
        const color = tier === 2 ? 0xffae3b : tier === 1 ? 0xffe066 : 0xfff27a;
        const bw = tier === 2 ? 3.2 : 2.2;
        const sp = tier === 2 ? 1700 : tier === 1 ? 1500 : 1300;
        const spread = tier === 2 ? 70 : tier === 1 ? 46 : 26;
        const ox = this.screenX(CROWD_P, this.centroidWx);
        const oy = this.project(CROWD_P).y - CREW_H * 0.6;
        for (let i = 0; i < n; i++) {
            const b = new Graphics();
            b.roundRect(-bw, -bw * 3, bw * 2, bw * 6, bw).fill({ color });
            const sx = ox + (Math.random() - 0.5) * spread;
            b.position.set(sx, oy);
            this.fxLayer.addChild(b);
            const dx = tx - sx;
            const dy = ty - oy;
            const len = Math.hypot(dx, dy) || 1;
            this.bullets.push({ node: b, vx: (dx / len) * sp, vy: (dy / len) * sp, life: 0.45 });
        }
        this.muzzleFlash(ox, oy, tier, color);
        // 着弾スパーク
        if (Math.random() < 0.6) this.spark(tx, ty, color);
        if (tier === 2) this.shake = Math.max(this.shake, 2.4);
    }

    /** 銃口の発光 (マズルフラッシュ)。 */
    private muzzleFlash(x: number, y: number, tier: number, color: number): void {
        const g = new Graphics();
        const r = tier === 2 ? 16 : tier === 1 ? 11 : 8;
        g.star(x, y, tier === 2 ? 6 : 4, r, r * 0.4).fill({ color: 0xfff4c2 });
        g.circle(x, y, r * 0.6).fill({ color, alpha: 0.85 });
        this.fxLayer.addChild(g);
        let life = 0.07;
        const fade = (tk: Ticker) => {
            life -= tk.deltaMS / 1000;
            g.alpha = Math.max(0, life / 0.07);
            g.scale.set(1 + (0.07 - life) * 6);
            if (life <= 0) {
                this.app.ticker.remove(fade);
                g.destroy();
            }
        };
        this.app.ticker.add(fade);
    }

    /** 着弾の小さな火花。 */
    private spark(x: number, y: number, color: number): void {
        const g = new Graphics();
        g.star(x, y, 5, 8, 3).fill({ color });
        this.fxLayer.addChild(g);
        let life = 0.13;
        const fade = (tk: Ticker) => {
            life -= tk.deltaMS / 1000;
            g.alpha = Math.max(0, life / 0.13);
            g.scale.set(1 + (0.13 - life) * 5);
            if (life <= 0) {
                this.app.ticker.remove(fade);
                g.destroy();
            }
        };
        this.app.ticker.add(fade);
    }

    private updateBullets(dt: number): void {
        for (let i = this.bullets.length - 1; i >= 0; i--) {
            const bl = this.bullets[i];
            bl.node.x += bl.vx * dt;
            bl.node.y += bl.vy * dt;
            bl.life -= dt;
            if (bl.life <= 0) {
                bl.node.destroy();
                this.bullets.splice(i, 1);
            }
        }
    }

    // ---------- ボス戦 ----------
    private startBattle(): void {
        this.phase = "battle";
        for (const e of this.entities) e.node.destroy();
        this.entities.length = 0;

        const node = new Container();
        node.sortableChildren = true;
        const max = Math.floor((320 + this.distance * 0.16) * this.stageMul);
        for (let i = 0; i < 14; i++) {
            const s = makeCrewSprite(false, 40 + Math.random() * 14);
            s.position.set((Math.random() - 0.5) * 220, -Math.random() * 40);
            s.zIndex = s.y;
            node.addChild(s);
        }
        const boss = makeCrewSprite(false, 150);
        boss.position.set(0, 0);
        boss.zIndex = 999;
        node.addChild(boss);
        const hpBar = new Graphics();
        hpBar.position.set(0, -170);
        hpBar.zIndex = 1000;
        node.addChild(hpBar);
        const lbl = this.label(`STAGE ${this.stage} BOSS`, 26, "#ffd0cf", 700);
        lbl.anchor.set(0.5);
        lbl.position.set(0, -200);
        lbl.zIndex = 1000;
        node.addChild(lbl);
        this.worldLayer.addChild(node);
        this.boss = { node, hp: max, max, hpBar, p: 0 };
        this.placeBoss();
        this.popText("⚔ BOSS ⚔", this.W / 2, this.H * 0.42, "#ff8f8f");
    }

    private placeBoss(): void {
        const boss = this.boss;
        if (!boss) return;
        const pr = this.project(boss.p);
        boss.node.position.set(this.W / 2, pr.y);
        boss.node.scale.set(this.bill(boss.p));
        boss.node.zIndex = pr.y;
    }

    private updateBattle(dt: number): void {
        const boss = this.boss;
        if (!boss) return;
        const stopP = 0.52;
        if (boss.p < stopP) {
            boss.p += this.pSpeed * 0.55 * dt;
            this.placeBoss();
        } else {
            boss.hp -= this.firepower * FIRE_FACTOR * dt;
            const bossDps = (16 + this.diff * 40) * this.stageMul * this.lossMult;
            this.changeCrew(-bossDps * dt);
            this.spawnBullets(dt, boss.node.x, boss.node.y - 40);
        }
        const w = this.W * 0.5;
        boss.hpBar.clear();
        boss.hpBar.roundRect(-w / 2, 0, w, 14, 7).fill({ color: 0x000000, alpha: 0.5 });
        boss.hpBar.roundRect(-w / 2, 0, w * Math.max(0, boss.hp / boss.max), 14, 7).fill({ color: 0xff5a5a });

        if (boss.hp <= 0) this.stageClear();
        else if (this.crew <= 0) this.defeat();
    }

    // ---------- ステージクリア ----------
    private stageClear(): void {
        if (this.phase !== "battle") return;
        this.phase = "clear";
        this.clearTimer = 2.0;
        if (this.boss) {
            this.boss.node.destroy();
            this.boss = null;
        }
        for (const s of this.enemyShots) s.node.destroy();
        this.enemyShots.length = 0;
        this.crew = Math.min(MAX_CREW, Math.ceil(this.crew * 1.1)); // クリア報酬: +10%
        this.rebuildCrowd();
        this.shake = Math.max(this.shake, 8);
        this.popText(`✨ STAGE ${this.stage} CLEAR! ✨`, this.W / 2, this.H * 0.4, "#ffe27a");
        this.popText("クルー +10%", this.W / 2, this.H * 0.4 + 44, "#7bd6ff");
    }

    private nextStage(): void {
        this.stage++;
        this.distance = 0;
        this.spawnTimer = 0.6;
        this.pSpeed = BASE_PSPEED;
        this.phase = "running";
        this.popText(`STAGE ${this.stage}`, this.W / 2, this.H * 0.36, "#ffffff");
    }

    // ---------- 敗北 / リザルト ----------
    private defeat(): void {
        if (this.phase === "result") return;
        this.phase = "result";
        const score = this.crew;
        const best = saveBest(score);
        this.showResult(score, best);
    }

    private showResult(score: number, best: number): void {
        const panel = new Container();
        const pw = Math.min(360, this.W * 0.86);
        const ph = 280;
        const px = (this.W - pw) / 2;
        const py = (this.H - ph) / 2;
        const bg = new Graphics();
        bg.roundRect(px, py, pw, ph, 16).fill({ color: 0x0e2236, alpha: 0.97 });
        bg.roundRect(px, py, pw, ph, 16).stroke({ color: 0xd64541, width: 3 });
        panel.addChild(bg);

        const title = this.label("💀 やられた…", 30, "#ff8f8f", 700);
        title.anchor.set(0.5);
        title.position.set(this.W / 2, py + 44);
        const st = this.label(`STAGE ${this.stage} まで到達`, 19, "#ffe27a", 700);
        st.anchor.set(0.5);
        st.position.set(this.W / 2, py + 86);
        const sc = this.label(`生き残ったクルー: ${score}`, 17, "#e6eef6", 600);
        sc.anchor.set(0.5);
        sc.position.set(this.W / 2, py + 118);
        const isNew = score >= best && score > this.bestAtStart;
        const bs = this.label(`ベスト人数: ${best}${isNew ? "  🆕" : ""}`, 15, "#9fb3c8", 600);
        bs.anchor.set(0.5);
        bs.position.set(this.W / 2, py + 146);
        panel.addChild(title, st, sc, bs);

        const retry = this.button("もう一回", this.W / 2 - 150, py + 184, 140, 48, 0x2f6fed);
        retry.on("pointertap", () => this.restart());
        const back = this.button("もどる", this.W / 2 + 10, py + 184, 140, 48, 0x40526a);
        back.on("pointertap", () => this.exit());
        panel.addChild(retry, back);

        this.uiLayer.addChild(panel);
    }

    private restart(): void {
        for (const e of this.entities) e.node.destroy();
        this.entities.length = 0;
        for (const b of this.bullets) b.node.destroy();
        this.bullets.length = 0;
        if (this.boss) {
            this.boss.node.destroy();
            this.boss = null;
        }
        for (const s of this.enemyShots) s.node.destroy();
        this.enemyShots.length = 0;
        this.uiLayer.removeChildren();
        for (const s of this.crewSprites) s.destroy();
        this.crewSprites.length = 0;
        this.crewOffsets.length = 0;
        for (const r of this.robotSprites) r.destroy();
        this.robotSprites.length = 0;

        this.phase = "running";
        this.stage = 1;
        this.crew = START_CREW;
        this.weaponTier = 0;
        this.armor = 0;
        this.robots = 0;
        this.distance = 0;
        this.pSpeed = BASE_PSPEED;
        this.spawnTimer = 0;
        this.steerWx = 0;
        this.centroidWx = 0;
        this.shake = 0;
        this.scene.position.set(0, 0);
        this.bestAtStart = loadBest();

        this.buildHud();
        this.rebuildCrowd();
    }

    private exit(): void {
        this.onExit();
    }

    // ---------- エフェクト ----------
    private popText(text: string, x: number, y: number, color: string): void {
        const t = this.label(text, 26, color, 700);
        t.anchor.set(0.5);
        t.position.set(x, y);
        this.fxLayer.addChild(t);
        let life = 0.8;
        const fade = (tk: Ticker) => {
            const d = tk.deltaMS / 1000;
            life -= d;
            t.y -= 46 * d;
            t.alpha = Math.max(0, life / 0.8);
            if (life <= 0) {
                this.app.ticker.remove(fade);
                t.destroy();
            }
        };
        this.app.ticker.add(fade);
    }

    private flashDamage(): void {
        this.shake = Math.max(this.shake, 10);
        const g = new Graphics();
        g.rect(0, 0, this.W, this.H).fill({ color: 0xff0000, alpha: 0.2 });
        this.fxLayer.addChild(g);
        let life = 0.25;
        const fade = (tk: Ticker) => {
            life -= tk.deltaMS / 1000;
            g.alpha = Math.max(0, (life / 0.25) * 0.2);
            if (life <= 0) {
                this.app.ticker.remove(fade);
                g.destroy();
            }
        };
        this.app.ticker.add(fade);
    }
}
