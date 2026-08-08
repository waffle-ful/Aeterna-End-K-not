// 役職コードの検証規則 (Modules/Ekm/EkrDefinition.cs の Validate() と同じ規則) の拒否/フォールバック/許容テスト

import { describe, expect, it } from "vitest";
import {
    DEFAULT_COLOR,
    KILL_COOLDOWN_DEFAULT,
    KILL_COOLDOWN_MAX,
    KILL_COOLDOWN_MIN,
    ROLE_AUTHOR_MAX,
    ROLE_NAME_MAX,
    SUPPORTED_TEAM,
    VISION_MULTIPLIER_DEFAULT,
    VISION_MULTIPLIER_MAX,
    VISION_MULTIPLIER_MIN,
    defaultEkrDefinition,
    normalizeColor,
    normalizeKillCooldown,
    normalizeVisionMultiplier,
    validateEkrDefinition,
} from "../src/roledef";

function baseValid(): Record<string, unknown> {
    return { ...defaultEkrDefinition(), name: "見習いエンジニア" };
}

describe("役職コード検証規則 (EkrDefinition.cs 契約ミラー)", () => {
    it("既定値 + name のみのオブジェクトは通る", () => {
        const r = validateEkrDefinition(baseValid());
        expect(r.ok).toBe(true);
    });

    it("JSON トップレベルがオブジェクトでなければ拒否 (null/配列/数値)", () => {
        expect(validateEkrDefinition(null).ok).toBe(false);
        expect(validateEkrDefinition([1, 2, 3]).ok).toBe(false);
        expect(validateEkrDefinition(5).ok).toBe(false);
        expect(validateEkrDefinition("hello").ok).toBe(false);
    });

    it("ekr != 1 は拒否 (欠落・2・文字列 いずれも)", () => {
        expect(validateEkrDefinition({ ...baseValid(), ekr: 2 }).ok).toBe(false);
        const noEkr = baseValid();
        delete noEkr.ekr;
        expect(validateEkrDefinition(noEkr).ok).toBe(false);
        expect(validateEkrDefinition({ ...baseValid(), ekr: "1" }).ok).toBe(false);
    });

    it("requires が空配列なら通る (省略/null も空配列に収束)", () => {
        expect(validateEkrDefinition({ ...baseValid(), requires: [] }).ok).toBe(true);
        const omitted = baseValid();
        delete omitted.requires;
        expect(validateEkrDefinition(omitted).ok).toBe(true);
        expect(validateEkrDefinition({ ...baseValid(), requires: null }).ok).toBe(true);
    });

    it("requires に何か1つでも要求があると拒否 (R0 の対応 capability は空集合)", () => {
        const r = validateEkrDefinition({ ...baseValid(), requires: ["blockly-logic"] });
        expect(r.ok).toBe(false);
        if (!r.ok) expect(r.error).toContain("blockly-logic");
    });

    it("requires が文字列配列でなければ拒否", () => {
        expect(validateEkrDefinition({ ...baseValid(), requires: [1, 2] }).ok).toBe(false);
        expect(validateEkrDefinition({ ...baseValid(), requires: "logic" }).ok).toBe(false);
    });

    it("name が空 (欠落/空文字/空白のみ) は拒否", () => {
        const noName = baseValid();
        delete noName.name;
        expect(validateEkrDefinition(noName).ok).toBe(false);
        expect(validateEkrDefinition({ ...baseValid(), name: "" }).ok).toBe(false);
        expect(validateEkrDefinition({ ...baseValid(), name: "   " }).ok).toBe(false);
    });

    it(`name はトリム後 ${ROLE_NAME_MAX} 文字まで — 境界値 (${ROLE_NAME_MAX} は通る/${ROLE_NAME_MAX + 1} は切り詰め)`, () => {
        const exact = "A".repeat(ROLE_NAME_MAX);
        const r1 = validateEkrDefinition({ ...baseValid(), name: exact });
        expect(r1.ok).toBe(true);
        if (r1.ok) expect(r1.def.name).toBe(exact);

        const over = "B".repeat(ROLE_NAME_MAX + 1);
        const r2 = validateEkrDefinition({ ...baseValid(), name: over });
        expect(r2.ok).toBe(true);
        if (r2.ok) expect(r2.def.name).toBe("B".repeat(ROLE_NAME_MAX));
    });

    it("name の切り詰めはトリムの後に行う (前後の空白を含めて24文字ちょうどなら削られない)", () => {
        const padded = `  ${"C".repeat(ROLE_NAME_MAX)}  `; // トリムすればちょうど24文字
        const r = validateEkrDefinition({ ...baseValid(), name: padded });
        expect(r.ok).toBe(true);
        if (r.ok) expect(r.def.name).toBe("C".repeat(ROLE_NAME_MAX));
    });

    it("name はマルチバイト (日本語+絵文字) でも UTF-16 コード単位で24文字に切り詰める", () => {
        const longName = "🥷".repeat(ROLE_NAME_MAX); // サロゲートペア = 1文字2コード単位
        const r = validateEkrDefinition({ ...baseValid(), name: longName });
        expect(r.ok).toBe(true);
        if (r.ok) expect(r.def.name).toBe(longName.slice(0, ROLE_NAME_MAX));
    });

    it(`author は任意・${ROLE_AUTHOR_MAX} 文字超は切り詰め (欠落/空文字は空で許容)`, () => {
        const noAuthor = baseValid();
        delete noAuthor.author;
        expect(validateEkrDefinition(noAuthor).ok).toBe(true);

        const over = "D".repeat(ROLE_AUTHOR_MAX + 5);
        const r = validateEkrDefinition({ ...baseValid(), author: over });
        expect(r.ok).toBe(true);
        if (r.ok) expect(r.def.author).toBe("D".repeat(ROLE_AUTHOR_MAX));
    });

    it("color は #rrggbb のみ受理し、大小文字はそのまま保持する", () => {
        const r = validateEkrDefinition({ ...baseValid(), color: "#3FA9F5" });
        expect(r.ok).toBe(true);
        if (r.ok) expect(r.def.color).toBe("#3FA9F5");
    });

    it("color が不正 (短縮形/名前付き/空/欠落) なら既定色へ黙ってフォールバック (エラーにしない)", () => {
        for (const bad of ["#fff", "red", "", "not-a-color", "12345g"]) {
            const r = validateEkrDefinition({ ...baseValid(), color: bad });
            expect(r.ok).toBe(true);
            if (r.ok) expect(r.def.color).toBe(DEFAULT_COLOR);
        }
        const noColor = baseValid();
        delete noColor.color;
        const r = validateEkrDefinition(noColor);
        expect(r.ok).toBe(true);
        if (r.ok) expect(r.def.color).toBe(DEFAULT_COLOR);
    });

    it("color は先頭 # を省略しても受理する", () => {
        const r = validateEkrDefinition({ ...baseValid(), color: "3fa9f5" });
        expect(r.ok).toBe(true);
        if (r.ok) expect(r.def.color).toBe("#3fa9f5");
    });

    it("team は crewmate 固定 — 欠落/null は crewmate に収束するが、空文字は拒否 (非対称)", () => {
        const omitted = baseValid();
        delete omitted.team;
        expect(validateEkrDefinition(omitted).ok).toBe(true);
        expect(validateEkrDefinition({ ...baseValid(), team: null }).ok).toBe(true);
        // 明示的な空文字はトリム後 "" になり crewmate と一致しないため拒否 (EkrDefinition.cs と同じ非対称性)
        expect(validateEkrDefinition({ ...baseValid(), team: "" }).ok).toBe(false);
    });

    it("team=impostor/neutral 等の非対応値は拒否", () => {
        const r = validateEkrDefinition({ ...baseValid(), team: "impostor" });
        expect(r.ok).toBe(false);
        if (!r.ok) expect(r.error).toContain("impostor");
    });

    it("team は大文字・前後空白があっても crewmate として通る", () => {
        const r = validateEkrDefinition({ ...baseValid(), team: "  CrewMate  " });
        expect(r.ok).toBe(true);
        if (r.ok) expect(r.def.team).toBe(SUPPORTED_TEAM);
    });

    it("canKill/canVent は真偽値そのまま反映し、既定は false", () => {
        const r1 = validateEkrDefinition({ ...baseValid(), canKill: true, canVent: true });
        expect(r1.ok).toBe(true);
        if (r1.ok) {
            expect(r1.def.canKill).toBe(true);
            expect(r1.def.canVent).toBe(true);
        }
        const r2 = validateEkrDefinition(baseValid());
        expect(r2.ok).toBe(true);
        if (r2.ok) {
            expect(r2.def.canKill).toBe(false);
            expect(r2.def.canVent).toBe(false);
        }
    });

    it(`killCooldown は ${KILL_COOLDOWN_MIN}〜${KILL_COOLDOWN_MAX} にクランプし、範囲外は境界値に丸める`, () => {
        expect(validateEkrDefinition({ ...baseValid(), killCooldown: 0 })).toMatchObject({ ok: true, def: { killCooldown: KILL_COOLDOWN_MIN } });
        expect(validateEkrDefinition({ ...baseValid(), killCooldown: 999 })).toMatchObject({ ok: true, def: { killCooldown: KILL_COOLDOWN_MAX } });
        // 小数はそのまま合法 (C# も float のまま保持、四捨五入しない)
        expect(validateEkrDefinition({ ...baseValid(), killCooldown: 27.5 })).toMatchObject({ ok: true, def: { killCooldown: 27.5 } });
    });

    it("killCooldown が非有限/非数値/欠落なら既定 25 にフォールバック", () => {
        expect(validateEkrDefinition({ ...baseValid(), killCooldown: NaN })).toMatchObject({ ok: true, def: { killCooldown: KILL_COOLDOWN_DEFAULT } });
        expect(validateEkrDefinition({ ...baseValid(), killCooldown: Infinity })).toMatchObject({ ok: true, def: { killCooldown: KILL_COOLDOWN_DEFAULT } });
        expect(validateEkrDefinition({ ...baseValid(), killCooldown: "25" })).toMatchObject({ ok: true, def: { killCooldown: KILL_COOLDOWN_DEFAULT } });
        const omitted = baseValid();
        delete omitted.killCooldown;
        expect(validateEkrDefinition(omitted)).toMatchObject({ ok: true, def: { killCooldown: KILL_COOLDOWN_DEFAULT } });
    });

    it(`visionMultiplier は ${VISION_MULTIPLIER_MIN}〜${VISION_MULTIPLIER_MAX} にクランプする`, () => {
        expect(validateEkrDefinition({ ...baseValid(), visionMultiplier: 0 })).toMatchObject({ ok: true, def: { visionMultiplier: VISION_MULTIPLIER_MIN } });
        expect(validateEkrDefinition({ ...baseValid(), visionMultiplier: 99 })).toMatchObject({ ok: true, def: { visionMultiplier: VISION_MULTIPLIER_MAX } });
    });

    it("visionMultiplier が非有限/欠落なら既定 1 にフォールバック", () => {
        expect(validateEkrDefinition({ ...baseValid(), visionMultiplier: NaN })).toMatchObject({ ok: true, def: { visionMultiplier: VISION_MULTIPLIER_DEFAULT } });
        const omitted = baseValid();
        delete omitted.visionMultiplier;
        expect(validateEkrDefinition(omitted)).toMatchObject({ ok: true, def: { visionMultiplier: VISION_MULTIPLIER_DEFAULT } });
    });

    it("winCondition は team と異なり非対応値でも拒否せずそのまま保持する (未使用フィールド)", () => {
        const r = validateEkrDefinition({ ...baseValid(), winCondition: "solo" });
        expect(r.ok).toBe(true);
        if (r.ok) expect(r.def.winCondition).toBe("solo");
    });

    it("winCondition が欠落/null なら既定 team に収束する", () => {
        const omitted = baseValid();
        delete omitted.winCondition;
        expect(validateEkrDefinition(omitted)).toMatchObject({ ok: true, def: { winCondition: "team" } });
        expect(validateEkrDefinition({ ...baseValid(), winCondition: null })).toMatchObject({ ok: true, def: { winCondition: "team" } });
    });
});

describe("normalize* ヘルパー (UI と検証が共有する唯一のクランプ実装)", () => {
    it("normalizeKillCooldown: 有限数はクランプ、非有限/非数値は既定値", () => {
        expect(normalizeKillCooldown(50)).toBe(50);
        expect(normalizeKillCooldown(0)).toBe(KILL_COOLDOWN_MIN);
        expect(normalizeKillCooldown(9999)).toBe(KILL_COOLDOWN_MAX);
        expect(normalizeKillCooldown(NaN)).toBe(KILL_COOLDOWN_DEFAULT);
        expect(normalizeKillCooldown("50")).toBe(KILL_COOLDOWN_DEFAULT);
        expect(normalizeKillCooldown(undefined)).toBe(KILL_COOLDOWN_DEFAULT);
    });

    it("normalizeVisionMultiplier: 有限数はクランプ、非有限/非数値は既定値", () => {
        expect(normalizeVisionMultiplier(2)).toBe(2);
        expect(normalizeVisionMultiplier(0)).toBe(VISION_MULTIPLIER_MIN);
        expect(normalizeVisionMultiplier(50)).toBe(VISION_MULTIPLIER_MAX);
        expect(normalizeVisionMultiplier(NaN)).toBe(VISION_MULTIPLIER_DEFAULT);
    });

    it("normalizeColor: 有効な#rrggbbはそのまま、それ以外は既定色", () => {
        expect(normalizeColor("#abcdef")).toBe("#abcdef");
        expect(normalizeColor("abcdef")).toBe("#abcdef");
        expect(normalizeColor("#ABC")).toBe(DEFAULT_COLOR);
        expect(normalizeColor(123)).toBe(DEFAULT_COLOR);
    });
});
