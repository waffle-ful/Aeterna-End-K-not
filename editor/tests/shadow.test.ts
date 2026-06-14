// §25 影レイヤー 契約テスト
// - validate: shadow の検証 (正常 / 奇数長 / 上限超過 / 非有限)
// - round-trip: docToJsonV3 → validateEkmapV3 で lines 一致
// - history: 追加 / 削除の Undo/Redo

import { describe, expect, it } from "vitest";
import tilesetB64 from "./fixtures/tileset8x2.b64.txt?raw";
import {
    type MapDocV3,
    SHADOW_MAX_LINES,
    SHADOW_MAX_POINTS_PER_LINE,
    docToJsonV3,
} from "../src/model";
import { validateEkmapV3, validateShadow } from "../src/validate";
import { History, applyPatch } from "../src/history";

const IMG = "data:image/png;base64," + tilesetB64.replace(/\s+/g, "");

// ============================================================
// フィクスチャ
// ============================================================

/** 通行可セルあり (spawn: (1,1)) の最小 v3 マップ */
// eslint-disable-next-line @typescript-eslint/no-explicit-any
function makeMinV3(): any {
    return {
        ekm: 3,
        name: "Shadow Test",
        author: "test",
        width: 4,
        height: 3,
        tilesets: [
            {
                tileSize: 32,
                columns: 8,
                image: IMG,
                tiles: [],
            },
        ],
        layers: [
            {
                tileset: 0,
                cells: [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
                above: false,
            },
        ],
        decor: [],
        spawn: { x: 1, y: 1 },
        ambient: { visionRadius: 8 },
    };
}

// ============================================================
// validateShadow 単体テスト
// ============================================================

describe("validateShadow — 正常系", () => {
    it("2 点線分 (4 要素) を受け入れる", () => {
        const warnings: string[] = [];
        const result = validateShadow({ shadow: { lines: [[1, 1, 5, 1]] } }, warnings);
        expect(result).toBeDefined();
        expect(result!.lines.length).toBe(1);
        expect(result!.lines[0]).toEqual([1, 1, 5, 1]);
        expect(warnings).toHaveLength(0);
    });

    it("3 点折れ線 (6 要素) を受け入れる", () => {
        const warnings: string[] = [];
        const result = validateShadow({ shadow: { lines: [[2, 3, 2, 6, 4, 6]] } }, warnings);
        expect(result!.lines[0]).toEqual([2, 3, 2, 6, 4, 6]);
    });

    it("float 座標 (半整数) を受け入れる", () => {
        const warnings: string[] = [];
        const result = validateShadow({ shadow: { lines: [[0.5, 0.5, 3.5, 0.5]] } }, warnings);
        expect(result!.lines[0]).toEqual([0.5, 0.5, 3.5, 0.5]);
        expect(warnings).toHaveLength(0);
    });

    it("shadow が undefined のとき undefined を返す (warnings なし)", () => {
        const warnings: string[] = [];
        const result = validateShadow({}, warnings);
        expect(result).toBeUndefined();
        expect(warnings).toHaveLength(0);
    });

    it("lines が空配列のとき lines:[] の ShadowData を返す", () => {
        const warnings: string[] = [];
        const result = validateShadow({ shadow: { lines: [] } }, warnings);
        expect(result).toBeDefined();
        expect(result!.lines).toHaveLength(0);
    });
});

describe("validateShadow — 不正な線はスキップ", () => {
    it("奇数長の線はスキップ (警告あり)", () => {
        const warnings: string[] = [];
        const result = validateShadow({ shadow: { lines: [[1, 1, 5]] } }, warnings);
        expect(result!.lines).toHaveLength(0);
        expect(warnings.length).toBeGreaterThan(0);
        expect(warnings.join()).toMatch(/奇数/);
    });

    it("4 要素未満 (2 要素) の線はスキップ", () => {
        const warnings: string[] = [];
        const result = validateShadow({ shadow: { lines: [[1, 1]] } }, warnings);
        expect(result!.lines).toHaveLength(0);
        expect(warnings.length).toBeGreaterThan(0);
    });

    it("非有限値 (NaN) を含む線はスキップ", () => {
        const warnings: string[] = [];
        const result = validateShadow({ shadow: { lines: [[1, 1, NaN, 5]] } }, warnings);
        expect(result!.lines).toHaveLength(0);
        expect(warnings.length).toBeGreaterThan(0);
    });

    it("非有限値 (Infinity) を含む線はスキップ", () => {
        const warnings: string[] = [];
        const result = validateShadow({ shadow: { lines: [[1, 1, Infinity, 5]] } }, warnings);
        expect(result!.lines).toHaveLength(0);
    });

    it("不正な線のみスキップして正常線は残る (マップ全体は拒否しない)", () => {
        const warnings: string[] = [];
        const result = validateShadow({
            shadow: {
                lines: [
                    [1, 1, 5, 1],        // 正常
                    [1, 1, 5],           // 奇数長 → スキップ
                    [2, 3, 2, 6, 4, 6],  // 正常
                ],
            },
        }, warnings);
        expect(result!.lines).toHaveLength(2);
        expect(warnings).toHaveLength(1);
    });
});

describe("validateShadow — 上限超過", () => {
    it(`点数 ${SHADOW_MAX_POINTS_PER_LINE + 1} 点の線は切り詰めて警告`, () => {
        const warnings: string[] = [];
        const oversizedLine: number[] = [];
        for (let i = 0; i <= SHADOW_MAX_POINTS_PER_LINE; i++) oversizedLine.push(i, 0);
        const result = validateShadow({ shadow: { lines: [oversizedLine] } }, warnings);
        expect(result!.lines).toHaveLength(1);
        expect(result!.lines[0].length).toBe(SHADOW_MAX_POINTS_PER_LINE * 2);
        expect(warnings.length).toBeGreaterThan(0);
        expect(warnings.join()).toMatch(/切り詰め/);
    });

    it(`本数 ${SHADOW_MAX_LINES + 1} 本の lines は ${SHADOW_MAX_LINES} 本に切り捨てて警告`, () => {
        const warnings: string[] = [];
        const lines: number[][] = [];
        for (let i = 0; i <= SHADOW_MAX_LINES; i++) lines.push([i, 0, i + 1, 0]);
        const result = validateShadow({ shadow: { lines } }, warnings);
        expect(result!.lines).toHaveLength(SHADOW_MAX_LINES);
        expect(warnings.join()).toMatch(/スキップ/);
    });
});

// ============================================================
// round-trip: docToJsonV3 → validateEkmapV3
// ============================================================

describe("shadow round-trip (docToJsonV3 → validateEkmapV3)", () => {
    function getDoc(): MapDocV3 {
        const r = validateEkmapV3(makeMinV3());
        if (!r.ok) throw new Error("fixture invalid: " + r.errors.join(", "));
        return r.doc;
    }

    it("shadow あり → JSON → 再検証で lines 一致", () => {
        const d = getDoc();
        d.shadow = { lines: [[1, 1, 5, 1], [2, 3, 2, 6, 4, 6]] };
        const json = docToJsonV3(d);
        expect(json.shadow).toBeDefined();
        expect(json.shadow!.lines).toEqual([[1, 1, 5, 1], [2, 3, 2, 6, 4, 6]]);

        const r2 = validateEkmapV3(JSON.parse(JSON.stringify(json)));
        expect(r2.ok).toBe(true);
        if (!r2.ok) return;
        expect(r2.doc.shadow?.lines).toEqual([[1, 1, 5, 1], [2, 3, 2, 6, 4, 6]]);
    });

    it("shadow なし (lines 空) → JSON では shadow フィールド省略", () => {
        const d = getDoc();
        d.shadow = { lines: [] };
        const json = docToJsonV3(d);
        expect(json.shadow).toBeUndefined();
    });

    it("shadow が undefined → JSON では shadow フィールド省略", () => {
        const d = getDoc();
        delete d.shadow;
        const json = docToJsonV3(d);
        expect(json.shadow).toBeUndefined();
    });

    it("shadow の lines が deep copy される (参照が独立)", () => {
        const d = getDoc();
        const original = [1, 1, 5, 1];
        d.shadow = { lines: [original] };
        const json = docToJsonV3(d);
        // JSON 側を変更しても元 doc に影響しない
        json.shadow!.lines[0][0] = 999;
        expect(d.shadow.lines[0][0]).toBe(1);
    });

    it("shadow 付き v3 が validateEkmapV3 を通過し warnings なし", () => {
        const m = makeMinV3();
        m.shadow = { lines: [[0.5, 0.5, 3.5, 0.5]] };
        const r = validateEkmapV3(m);
        expect(r.ok).toBe(true);
        if (!r.ok) return;
        expect(r.warnings).toHaveLength(0);
        expect(r.doc.shadow?.lines[0]).toEqual([0.5, 0.5, 3.5, 0.5]);
    });

    it("shadow に不正な線が混入しても v3 全体は拒否されない (warning のみ)", () => {
        const m = makeMinV3();
        m.shadow = { lines: [[1, 1, 5, 1], [1, 1, 5]] }; // 2本目は奇数長
        const r = validateEkmapV3(m);
        expect(r.ok).toBe(true);
        if (!r.ok) return;
        expect(r.doc.shadow?.lines).toHaveLength(1);
        expect(r.warnings.length).toBeGreaterThan(0);
    });
});

// ============================================================
// history: 追加 / 削除の Undo / Redo
// ============================================================

describe("shadow history (Undo/Redo)", () => {
    function getDoc(): MapDocV3 {
        const r = validateEkmapV3(makeMinV3());
        if (!r.ok) throw new Error("fixture invalid");
        return r.doc;
    }

    it("shadowBefore/shadowAfter の Patch を push → Undo/Redo で lines が往復する", () => {
        const d = getDoc();
        const h = new History();

        // 追加操作
        const before: number[][] = [];
        d.shadow = { lines: [[1, 1, 5, 1]] };
        const after = d.shadow.lines.map((l) => [...l]);

        const p = { shadowBefore: before, shadowAfter: after };
        h.push(p);
        expect(h.canUndo).toBe(true);
        expect(h.canRedo).toBe(false);

        // Undo: 空に戻る
        const undo = h.undo();
        expect(undo).toBeDefined();
        applyPatch(d, undo!, "undo");
        expect(d.shadow?.lines ?? []).toHaveLength(0);

        // Redo: 復元
        const redo = h.redo();
        expect(redo).toBeDefined();
        applyPatch(d, redo!, "redo");
        expect(d.shadow?.lines).toEqual([[1, 1, 5, 1]]);
    });

    it("shadowBefore が undefined の Patch は push ガードに弾かれる (silent no-op 防止)", () => {
        const h = new History();
        // cells/decorBefore/spawnBefore/shadowBefore が全て undefined
        h.push({ shadowAfter: [[1, 1, 5, 1]] });
        expect(h.canUndo).toBe(false);
    });

    it("削除 Undo/Redo: 削除→Undo で元の lines が戻る", () => {
        const d = getDoc();
        d.shadow = { lines: [[1, 1, 5, 1], [2, 3, 2, 6]] };
        const h = new History();

        // 1本目を削除
        const before = d.shadow.lines.map((l) => [...l]);
        d.shadow.lines.splice(0, 1);
        const after = d.shadow.lines.map((l) => [...l]);

        h.push({ shadowBefore: before, shadowAfter: after });

        // 削除後 1 本
        expect(d.shadow.lines).toHaveLength(1);

        // Undo: 2 本に戻る
        const undo = h.undo()!;
        applyPatch(d, undo, "undo");
        expect(d.shadow?.lines).toHaveLength(2);
        expect(d.shadow?.lines[0]).toEqual([1, 1, 5, 1]);
    });

    it("Undo 後に Redo → lines が再び削除後の状態になる", () => {
        const d = getDoc();
        d.shadow = { lines: [[1, 1, 5, 1], [2, 3, 2, 6]] };
        const h = new History();

        const before = d.shadow.lines.map((l) => [...l]);
        d.shadow.lines.splice(0, 1);
        const after = d.shadow.lines.map((l) => [...l]);
        h.push({ shadowBefore: before, shadowAfter: after });

        const undo = h.undo()!;
        applyPatch(d, undo, "undo");

        const redo = h.redo()!;
        applyPatch(d, redo, "redo");
        expect(d.shadow?.lines).toHaveLength(1);
        expect(d.shadow?.lines[0]).toEqual([2, 3, 2, 6]);
    });

    it("shadowAfter が空 → Undo 後は doc.shadow が削除される", () => {
        const d = getDoc();
        d.shadow = { lines: [[1, 1, 5, 1]] };
        const h = new History();

        const before = d.shadow.lines.map((l) => [...l]);
        d.shadow = { lines: [] };
        const after: number[][] = [];

        h.push({ shadowBefore: before, shadowAfter: after });
        // Undo: 元に戻す
        const undo = h.undo()!;
        applyPatch(d, undo, "undo");
        expect(d.shadow?.lines).toHaveLength(1);
    });
});
