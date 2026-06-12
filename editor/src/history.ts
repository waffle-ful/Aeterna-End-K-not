// Undo/Redo — セル diff ベース (200 手以上保持)
// v1 はセル値が文字 (./#/-)、v2 はタイル id (number, -1 = 空)。layer は v2 のみ意味を持つ

import { type AnyDoc, type DecorEntry, type SpawnPoint, isV2Doc } from "./model";

export type LayerName = "ground" | "upper";

export interface CellChange {
    /** フラット index (y * width + x) */
    i: number;
    before: string | number;
    after: string | number;
}

export interface Patch {
    /** v2 のみ: cells が適用されるレイヤー (省略時 ground) */
    layer?: LayerName;
    cells?: CellChange[];
    decorBefore?: DecorEntry[];
    decorAfter?: DecorEntry[];
    spawnBefore?: SpawnPoint;
    spawnAfter?: SpawnPoint;
}

export const HISTORY_LIMIT = 300;

export function applyPatch(doc: AnyDoc, p: Patch, dir: "undo" | "redo"): void {
    if (p.cells) {
        if (isV2Doc(doc)) {
            const layer = p.layer === "upper" ? doc.upper : doc.ground;
            for (const c of p.cells) layer[c.i] = (dir === "undo" ? c.before : c.after) as number;
        } else {
            for (const c of p.cells) doc.grid[c.i] = (dir === "undo" ? c.before : c.after) as string;
        }
    }
    if (p.decorBefore && p.decorAfter) {
        doc.decor = (dir === "undo" ? p.decorBefore : p.decorAfter).map((d) => ({ ...d }));
    }
    if (p.spawnBefore && p.spawnAfter) {
        doc.spawn = { ...(dir === "undo" ? p.spawnBefore : p.spawnAfter) };
    }
}

export class History {
    private undoStack: Patch[] = [];
    private redoStack: Patch[] = [];

    constructor(private readonly limit: number = HISTORY_LIMIT) {}

    push(p: Patch): void {
        if (!p.cells?.length && !p.decorBefore && !p.spawnBefore) return;
        this.undoStack.push(p);
        if (this.undoStack.length > this.limit) this.undoStack.shift();
        this.redoStack = [];
    }

    undo(): Patch | null {
        const p = this.undoStack.pop();
        if (!p) return null;
        this.redoStack.push(p);
        return p;
    }

    redo(): Patch | null {
        const p = this.redoStack.pop();
        if (!p) return null;
        this.undoStack.push(p);
        return p;
    }

    get canUndo(): boolean {
        return this.undoStack.length > 0;
    }

    get canRedo(): boolean {
        return this.redoStack.length > 0;
    }

    clear(): void {
        this.undoStack = [];
        this.redoStack = [];
    }
}
