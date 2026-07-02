// Crew Run のゲート演算 (applyGate / gateLabel) の単体テスト。
// PIXI 非依存の gate.ts を直接テストする (crewrun.ts は import しない)。

import { describe, expect, it } from "vitest";
import { applyGate, gateLabel, MAX_CREW } from "../src/minigame/gate";

describe("applyGate — 四則とクランプ", () => {
    it("加算", () => {
        expect(applyGate(10, { kind: "+", value: 5 })).toBe(15);
    });
    it("減算", () => {
        expect(applyGate(10, { kind: "-", value: 3 })).toBe(7);
    });
    it("乗算", () => {
        expect(applyGate(10, { kind: "*", value: 2 })).toBe(20);
    });
    it("除算は切り捨て", () => {
        expect(applyGate(11, { kind: "/", value: 2 })).toBe(5);
    });

    it("0 未満は 0 にクランプ", () => {
        expect(applyGate(3, { kind: "-", value: 10 })).toBe(0);
        expect(applyGate(0, { kind: "/", value: 2 })).toBe(0);
    });
    it("上限 MAX_CREW にクランプ", () => {
        expect(applyGate(MAX_CREW, { kind: "*", value: 5 })).toBe(MAX_CREW);
        expect(applyGate(5000, { kind: "+", value: 9000 })).toBe(MAX_CREW);
    });

    it("人数 0 からはどの演算でも 0 のまま (除算/乗算)", () => {
        expect(applyGate(0, { kind: "*", value: 2 })).toBe(0);
        expect(applyGate(0, { kind: "+", value: 0 })).toBe(0);
    });
});

describe("gateLabel — 表示文字列", () => {
    it("各種別を記号付きで表示する", () => {
        expect(gateLabel({ kind: "+", value: 10 })).toBe("+10");
        expect(gateLabel({ kind: "-", value: 3 })).toBe("-3");
        expect(gateLabel({ kind: "*", value: 2 })).toBe("x2");
        expect(gateLabel({ kind: "/", value: 2 })).toBe("÷2");
    });
});
