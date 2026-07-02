// 入力: マウス (左ドラッグ=ペイント / 中ボタン or Space+左=パン / ホイール=ズーム)
//       タッチ (1 本指=ペイント / 2 本指=パン+ピンチズーム)。Pointer Events で実装。

import type { Container } from "pixi.js";
import { CELL, MAX_SCALE, MIN_SCALE } from "./render";

export interface GestureHandlers {
    /** 整数セル座標。グリッド外も来るので受け側で範囲チェック */
    strokeStart(x: number, y: number): void;
    strokeStep(x: number, y: number): void;
    strokeEnd(): void;
    /** 2 本指検出などでストローク全体を巻き戻す */
    strokeCancel(): void;
    /** float セル座標のホバー通知 */
    hover(x: number, y: number): void;
    viewChanged(): void;
    /**
     * ゴーストプレイヤー人形の掴み判定 (float セル座標)。
     * true を返すと塗りではなくゴーストドラッグに入る。
     */
    tryGrabGhost(x: number, y: number): boolean;
    /** ゴーストドラッグ中の移動 (float セル座標) */
    ghostDragTo(x: number, y: number): void;
    /** ゴーストドラッグ終了 */
    ghostDragEnd(): void;
}

type Mode = "idle" | "stroke" | "pan" | "pinch" | "ghostdrag";

interface Pt {
    x: number;
    y: number;
}

export class InputController {
    private readonly pointers = new Map<number, Pt>();
    private mode: Mode = "idle";
    private spaceDown = false;
    private lastPan: Pt = { x: 0, y: 0 };
    private pinch: { dist: number; s: number; wx: number; wy: number; cx0: number; cy0: number } | null = null;
    private lastCell: Pt | null = null;
    private strokeId = -1;

    constructor(
        private readonly canvas: HTMLCanvasElement,
        private readonly world: Container,
        private readonly h: GestureHandlers,
    ) {
        canvas.style.touchAction = "none";
        canvas.addEventListener("pointerdown", this.onDown);
        canvas.addEventListener("pointermove", this.onMove);
        canvas.addEventListener("pointerup", this.onUp);
        canvas.addEventListener("pointercancel", this.onCancel);
        canvas.addEventListener("wheel", this.onWheel, { passive: false });
        canvas.addEventListener("contextmenu", (e) => e.preventDefault());
        window.addEventListener("keydown", this.onKey);
        window.addEventListener("keyup", this.onKey);
    }

    private pos(e: PointerEvent | WheelEvent): Pt {
        const r = this.canvas.getBoundingClientRect();
        return { x: e.clientX - r.left, y: e.clientY - r.top };
    }

    private toCell(p: Pt): Pt {
        return {
            x: (p.x - this.world.position.x) / this.world.scale.x / CELL,
            y: (p.y - this.world.position.y) / this.world.scale.y / CELL,
        };
    }

    private beginStroke(id: number, p: Pt): void {
        this.mode = "stroke";
        this.strokeId = id;
        const c = this.toCell(p);
        this.lastCell = c;
        this.h.strokeStart(Math.floor(c.x), Math.floor(c.y));
    }

    /**
     * ゴースト人形の上で押下したらドラッグモードに入る。掴めたら true。
     * 掴めなかったときだけ通常の beginStroke を呼ぶこと。
     */
    private tryBeginGhost(id: number, p: Pt): boolean {
        const c = this.toCell(p);
        if (!this.h.tryGrabGhost(c.x, c.y)) return false;
        this.mode = "ghostdrag";
        this.strokeId = id;
        return true;
    }

    private startPinch(): void {
        const [a, b] = [...this.pointers.values()];
        this.mode = "pinch";
        this.pinch = {
            dist: Math.max(8, Math.hypot(b.x - a.x, b.y - a.y)),
            s: this.world.scale.x,
            wx: this.world.position.x,
            wy: this.world.position.y,
            cx0: (a.x + b.x) / 2,
            cy0: (a.y + b.y) / 2,
        };
    }

    private onDown = (e: PointerEvent): void => {
        this.canvas.setPointerCapture(e.pointerId);
        const p = this.pos(e);
        this.pointers.set(e.pointerId, p);
        if (e.pointerType === "touch") {
            if (this.pointers.size === 2) {
                // 2 本目の指 → ペイント中なら巻き戻してピンチ/パンへ移行
                if (this.mode === "stroke") {
                    this.h.strokeCancel();
                    this.lastCell = null;
                    this.strokeId = -1;
                }
                this.startPinch();
                return;
            }
            if (this.pointers.size > 2) return;
            if (this.mode === "idle" && !this.tryBeginGhost(e.pointerId, p)) this.beginStroke(e.pointerId, p);
        } else {
            if (e.button === 1 || (e.button === 0 && this.spaceDown)) {
                e.preventDefault();
                this.mode = "pan";
                this.lastPan = p;
                this.canvas.style.cursor = "grabbing";
            } else if (e.button === 0 && this.mode === "idle") {
                if (!this.tryBeginGhost(e.pointerId, p)) this.beginStroke(e.pointerId, p);
            }
        }
    };

    private onMove = (e: PointerEvent): void => {
        const p = this.pos(e);
        if (this.pointers.has(e.pointerId)) this.pointers.set(e.pointerId, p);
        const c = this.toCell(p);
        this.h.hover(c.x, c.y);

        if (this.mode === "stroke" && e.pointerId === this.strokeId) {
            // 高速ドラッグでセルが飛ばないよう前回位置から補間
            const prev = this.lastCell ?? c;
            const dx = c.x - prev.x;
            const dy = c.y - prev.y;
            const steps = Math.max(1, Math.ceil(Math.max(Math.abs(dx), Math.abs(dy)) * 2));
            let lx = Math.floor(prev.x);
            let ly = Math.floor(prev.y);
            for (let i = 1; i <= steps; i++) {
                const ix = Math.floor(prev.x + (dx * i) / steps);
                const iy = Math.floor(prev.y + (dy * i) / steps);
                if (ix !== lx || iy !== ly) {
                    this.h.strokeStep(ix, iy);
                    lx = ix;
                    ly = iy;
                }
            }
            this.lastCell = c;
        } else if (this.mode === "ghostdrag" && e.pointerId === this.strokeId) {
            this.h.ghostDragTo(c.x, c.y);
        } else if (this.mode === "pan" && this.pointers.size >= 1) {
            this.world.position.x += p.x - this.lastPan.x;
            this.world.position.y += p.y - this.lastPan.y;
            this.lastPan = p;
            this.h.viewChanged();
        } else if (this.mode === "pinch" && this.pointers.size >= 2 && this.pinch) {
            const [a, b] = [...this.pointers.values()];
            const dist = Math.max(8, Math.hypot(b.x - a.x, b.y - a.y));
            const cx = (a.x + b.x) / 2;
            const cy = (a.y + b.y) / 2;
            let s = this.pinch.s * (dist / this.pinch.dist);
            s = Math.min(MAX_SCALE, Math.max(MIN_SCALE, s));
            const k = s / this.pinch.s;
            this.world.scale.set(s);
            this.world.position.set(cx - (this.pinch.cx0 - this.pinch.wx) * k, cy - (this.pinch.cy0 - this.pinch.wy) * k);
            this.h.viewChanged();
        }
    };

    private onUp = (e: PointerEvent): void => {
        this.pointers.delete(e.pointerId);
        if (this.mode === "stroke" && e.pointerId === this.strokeId) {
            this.h.strokeEnd();
            this.mode = "idle";
            this.lastCell = null;
            this.strokeId = -1;
        } else if (this.mode === "ghostdrag" && e.pointerId === this.strokeId) {
            this.h.ghostDragEnd();
            this.mode = "idle";
            this.strokeId = -1;
        } else if (this.mode === "pinch") {
            if (this.pointers.size === 1) {
                // ピンチ → 残り 1 本指はパン継続
                this.mode = "pan";
                this.lastPan = [...this.pointers.values()][0];
            } else if (this.pointers.size === 0) {
                this.mode = "idle";
            }
        } else if (this.mode === "pan" && this.pointers.size === 0) {
            this.mode = "idle";
            this.canvas.style.cursor = this.spaceDown ? "grab" : "crosshair";
        }
        if (this.pointers.size === 0 && this.mode !== "idle") this.mode = "idle";
    };

    private onCancel = (e: PointerEvent): void => {
        this.pointers.delete(e.pointerId);
        if (this.mode === "stroke" && e.pointerId === this.strokeId) {
            this.h.strokeCancel();
            this.lastCell = null;
            this.strokeId = -1;
        } else if (this.mode === "ghostdrag" && e.pointerId === this.strokeId) {
            this.h.ghostDragEnd();
            this.strokeId = -1;
        }
        if (this.pointers.size === 0) this.mode = "idle";
        else if (this.pointers.size === 1 && this.mode === "pinch") {
            this.mode = "pan";
            this.lastPan = [...this.pointers.values()][0];
        }
    };

    private onWheel = (e: WheelEvent): void => {
        e.preventDefault();
        const p = this.pos(e);
        const s0 = this.world.scale.x;
        let s1 = s0 * Math.exp(-e.deltaY * 0.0012);
        s1 = Math.min(MAX_SCALE, Math.max(MIN_SCALE, s1));
        if (s1 === s0) return;
        this.world.position.x = p.x - ((p.x - this.world.position.x) * s1) / s0;
        this.world.position.y = p.y - ((p.y - this.world.position.y) * s1) / s0;
        this.world.scale.set(s1);
        this.h.viewChanged();
    };

    private onKey = (e: KeyboardEvent): void => {
        if (e.code !== "Space") return;
        const tag = (e.target as HTMLElement | null)?.tagName ?? "";
        if (tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT" || tag === "BUTTON") return;
        if (e.type === "keydown") {
            e.preventDefault(); // ページスクロール防止
            this.spaceDown = true;
            if (this.mode === "idle") this.canvas.style.cursor = "grab";
        } else {
            this.spaceDown = false;
            if (this.mode === "idle") this.canvas.style.cursor = "crosshair";
        }
    };
}
