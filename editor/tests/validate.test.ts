// 検証規則 (仕様 §9) の拒否/警告/許容テスト

import { describe, expect, it } from "vitest";
import { validateEkmap } from "../src/validate";
import { docToJson } from "../src/model";
import { golden, goldenClone } from "./golden";

describe("検証規則 (仕様 §9)", () => {
    it("golden サンプルは拒否も警告も無く通る", () => {
        const r = validateEkmap(goldenClone());
        expect(r.ok).toBe(true);
        if (r.ok) {
            expect(r.warnings).toEqual([]);
            expect(r.doc.width).toBe(16);
            expect(r.doc.height).toBe(16);
            expect(r.doc.decor.length).toBe(golden.decor.length);
        }
    });

    it("ekm != 1 は拒否", () => {
        const m = goldenClone();
        m.ekm = 2;
        const r = validateEkmap(m);
        expect(r.ok).toBe(false);
        if (!r.ok) expect(r.errors.join("\n")).toContain("ekm");
    });

    it("ekm 欠落も拒否", () => {
        const m = goldenClone();
        delete m.ekm;
        expect(validateEkmap(m).ok).toBe(false);
    });

    it("cells の行数が height と不一致は拒否", () => {
        const m = goldenClone();
        m.cells = m.cells.slice(0, 15);
        expect(validateEkmap(m).ok).toBe(false);
    });

    it("行の長さが width と不一致は拒否", () => {
        const m = goldenClone();
        m.cells[3] = m.cells[3] + ".";
        expect(validateEkmap(m).ok).toBe(false);
    });

    it("不正文字は拒否 (数字 0-9 は v1 予約)", () => {
        const m = goldenClone();
        m.cells[1] = "#5.....#.......#"; // 16 文字を維持して '5' を混入
        const r = validateEkmap(m);
        expect(r.ok).toBe(false);
        if (!r.ok) expect(r.errors.join("\n")).toContain("不正な文字");
    });

    it("spawn が壁上は拒否", () => {
        const m = goldenClone();
        m.spawn = { x: 0, y: 0 }; // golden の (0,0) は '#'
        const r = validateEkmap(m);
        expect(r.ok).toBe(false);
        if (!r.ok) expect(r.errors.join("\n")).toContain("床");
    });

    it("spawn 欠落は拒否", () => {
        const m = goldenClone();
        delete m.spawn;
        expect(validateEkmap(m).ok).toBe(false);
    });

    it("spawn 範囲外は拒否", () => {
        const m = goldenClone();
        m.spawn = { x: 99, y: 7 };
        expect(validateEkmap(m).ok).toBe(false);
    });

    it("name 制約違反 (空 / 65 文字) は拒否", () => {
        const a = goldenClone();
        a.name = "";
        expect(validateEkmap(a).ok).toBe(false);
        const b = goldenClone();
        b.name = "x".repeat(65);
        expect(validateEkmap(b).ok).toBe(false);
    });

    it("寸法範囲外 (0 / 257) は拒否", () => {
        const a = goldenClone();
        a.width = 0;
        expect(validateEkmap(a).ok).toBe(false);
        const b = goldenClone();
        b.height = 257;
        expect(validateEkmap(b).ok).toBe(false);
    });

    it("JSON 512 KB 超は拒否", () => {
        const r = validateEkmap(goldenClone(), 512 * 1024 + 1);
        expect(r.ok).toBe(false);
    });

    it("未知 decor kind はそのエントリだけ警告スキップ (マップ全体は拒否しない)", () => {
        const m = goldenClone();
        m.decor.push({ kind: "totem", x: 1, y: 1 });
        const r = validateEkmap(m);
        expect(r.ok).toBe(true);
        if (r.ok) {
            expect(r.warnings.length).toBe(1);
            expect(r.doc.decor.length).toBe(golden.decor.length); // 不正エントリのみ落ちる
        }
    });

    it("decor 範囲外はエントリ警告スキップ", () => {
        const m = goldenClone();
        m.decor.push({ kind: "light", x: 99, y: 1 });
        const r = validateEkmap(m);
        expect(r.ok).toBe(true);
        if (r.ok) expect(r.warnings.length).toBe(1);
    });

    it("未知トップレベルキーは黙って許容 (前方互換)", () => {
        const m = goldenClone();
        m.solid = [];
        m.futureKey = { a: 1 };
        const r = validateEkmap(m);
        expect(r.ok).toBe(true);
        if (r.ok) expect(r.warnings).toEqual([]);
    });

    it("ambient.visionRadius はクランプして黙って許容", () => {
        const m = goldenClone();
        m.ambient = { visionRadius: 99 };
        const r = validateEkmap(m);
        expect(r.ok).toBe(true);
        if (r.ok) {
            // 仕様 §7: v1 は 4〜8 にクランプ (9〜12 は v2 予約)
            expect(r.doc.ambient.visionRadius).toBe(8);
            expect(r.warnings).toEqual([]);
        }
    });

    it("spawn は float 可・セル中心基準 (7.4 → セル 7 の床で通る)", () => {
        const m = goldenClone();
        m.spawn = { x: 7.4, y: 7.4 };
        expect(validateEkmap(m).ok).toBe(true);
    });

    it("ambient の未知サブキーが doc 化→JSON 化で保全される (roundtrip)", () => {
        const m = goldenClone();
        m.ambient = { visionRadius: 6, darkLevel: 0.22, edgeBlur: 0.1 };
        const r = validateEkmap(m);
        expect(r.ok).toBe(true);
        if (!r.ok) return;
        // doc に 3 キー全て保持されているか
        expect(r.doc.ambient.visionRadius).toBe(6);
        expect(r.doc.ambient.darkLevel).toBe(0.22);
        expect(r.doc.ambient.edgeBlur).toBe(0.1);
        // docToJson でも 3 キーが保全されるか
        const json = docToJson(r.doc);
        expect(json.ambient?.visionRadius).toBe(6);
        expect(json.ambient?.darkLevel).toBe(0.22);
        expect(json.ambient?.edgeBlur).toBe(0.1);
    });
});
