// 役職コードの検証規則 (Modules/Ekm/EkrDefinition.cs の Validate() と同じ規則) の拒否/フォールバック/許容テスト

import { describe, expect, it } from "vitest";
import {
    ALIVE_COUNT_VALUE_MAX,
    ALIVE_COUNT_VALUE_MIN,
    DEFAULT_COLOR,
    HOST_OPTIONS_MAX,
    KILL_COOLDOWN_DEFAULT,
    KILL_COOLDOWN_MAX,
    KILL_COOLDOWN_MIN,
    PROGRESS_TEXT_MAX,
    ROLE_AUTHOR_MAX,
    ROLE_NAME_MAX,
    SUPPORTED_TEAM,
    SUPPORTED_TEAMS,
    VISION_MULTIPLIER_DEFAULT,
    VISION_MULTIPLIER_MAX,
    VISION_MULTIPLIER_MIN,
    defaultEkrDefinition,
    normalizeColor,
    normalizeKillCooldown,
    normalizeVisionMultiplier,
    validateEkrDefinition,
    validatePassives,
} from "../src/roledef";

function baseValid(): Record<string, unknown> {
    return { ...defaultEkrDefinition(), name: "見習いエンジニア" };
}

describe("役職コード検証規則 (EkrDefinition.cs 契約ミラー)", () => {
    it("既定値 + name のみのオブジェクトは通る", () => {
        const r = validateEkrDefinition(baseValid());
        expect(r.ok).toBe(true);
    });

    // plan §7 Tier 1 #2 の説明文2欄。C# 側 (EkrDefinitionTests) と同じ入力・同じ期待値で書くこと —
    // ここが両側の drift 検出網になる (連続改行の潰し方とタグ境界の扱いは実際に食い違っていた)。
    it("説明文: 空欄/欠落はキーごと落ちる (欠落 = ゲーム側の既定文言)", () => {
        const omitted = validateEkrDefinition(baseValid());
        if (!omitted.ok) throw new Error(omitted.error);
        expect(omitted.def.description).toBeUndefined();
        expect(omitted.def.descriptionLong).toBeUndefined();

        const blank = validateEkrDefinition({ ...baseValid(), description: "   ", descriptionLong: "" });
        if (!blank.ok) throw new Error(blank.error);
        expect(blank.def.description).toBeUndefined();
        expect(blank.def.descriptionLong).toBeUndefined();
    });

    it("説明文: 短文は連続改行を空白1つへ畳む (C# の [\\r\\n]+ と同一規則)", () => {
        const r = validateEkrDefinition({ ...baseValid(), description: "あ\r\nい\n\nう" });
        if (!r.ok) throw new Error(r.error);
        expect(r.def.description).toBe("あ い う");
    });

    it("説明文: 上限 (80/400) で切り、切り口に残った未閉じ TMP タグ断片は捨てる", () => {
        const body = "あ".repeat(76);
        const r = validateEkrDefinition({ ...baseValid(), description: `${body}<color=#ff0000>強調</color>` });
        if (!r.ok) throw new Error(r.error);
        expect(r.def.description).toBe(body);

        const longBody = "い".repeat(500);
        const r2 = validateEkrDefinition({ ...baseValid(), descriptionLong: longBody });
        if (!r2.ok) throw new Error(r2.error);
        expect(r2.def.descriptionLong?.length).toBe(400);
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

    it("team は省略/null で既定 crewmate に収束するが、空文字は拒否 (非対称)", () => {
        const omitted = baseValid();
        delete omitted.team;
        expect(validateEkrDefinition(omitted).ok).toBe(true);
        expect(validateEkrDefinition({ ...baseValid(), team: null }).ok).toBe(true);
        // 明示的な空文字はトリム後 "" になり3値のどれとも一致しないため拒否 (EkrDefinition.cs と同じ非対称性)
        expect(validateEkrDefinition({ ...baseValid(), team: "" }).ok).toBe(false);
    });

    // R2 (§1 2026-08-11): team は crewmate/impostor/neutral の3値を受理する
    // (madmate/coven は対象外のまま拒否)。
    it("team は crewmate/impostor/neutral の3値すべてを受理する", () => {
        for (const team of SUPPORTED_TEAMS) {
            const r = validateEkrDefinition({ ...baseValid(), team });
            expect(r.ok, `team=${team}`).toBe(true);
            if (r.ok) expect(r.def.team).toBe(team);
        }
    });

    it("team=madmate/coven 等の非対応値は拒否", () => {
        const r = validateEkrDefinition({ ...baseValid(), team: "madmate" });
        expect(r.ok).toBe(false);
        if (!r.ok) expect(r.error).toContain("madmate");

        const r2 = validateEkrDefinition({ ...baseValid(), team: "coven" });
        expect(r2.ok).toBe(false);
    });

    it("team は大文字・前後空白があっても対応する値として通る (3値それぞれで確認)", () => {
        const r1 = validateEkrDefinition({ ...baseValid(), team: "  CrewMate  " });
        expect(r1.ok).toBe(true);
        if (r1.ok) expect(r1.def.team).toBe(SUPPORTED_TEAM);

        const r2 = validateEkrDefinition({ ...baseValid(), team: "  Impostor  " });
        expect(r2.ok).toBe(true);
        if (r2.ok) expect(r2.def.team).toBe("impostor");

        const r3 = validateEkrDefinition({ ...baseValid(), team: "NEUTRAL" });
        expect(r3.ok).toBe(true);
        if (r3.ok) expect(r3.def.team).toBe("neutral");
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

    it("killCooldown が非有限 (NaN/Infinity)/欠落なら既定 25 にフォールバック (型不一致とは別扱い)", () => {
        expect(validateEkrDefinition({ ...baseValid(), killCooldown: NaN })).toMatchObject({ ok: true, def: { killCooldown: KILL_COOLDOWN_DEFAULT } });
        expect(validateEkrDefinition({ ...baseValid(), killCooldown: Infinity })).toMatchObject({ ok: true, def: { killCooldown: KILL_COOLDOWN_DEFAULT } });
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

// §1 の契約解決: 「JSON 型不一致は文書全体 reject」を C# (System.Text.Json の
// 素の挙動) に合わせて統一。値型 (bool/number) は明示的な null も拒否 (STJ が非 nullable 値型に
// null を入れられず例外になるのを模倣)、参照型 (string) は null を省略と同じ既定値扱いにする
// (STJ は string へ null を代入でき、Validate() 側の `??` が既定値に落とすのと同じ)。
describe("型不一致は文書全体 reject (C# の JsonSerializer 挙動に合わせて統一)", () => {
    it("canKill/canVent: 型不一致 (文字列/数値) は拒否、null も拒否 (bool は非 nullable 値型)", () => {
        expect(validateEkrDefinition({ ...baseValid(), canKill: "true" }).ok).toBe(false);
        expect(validateEkrDefinition({ ...baseValid(), canKill: 1 }).ok).toBe(false);
        expect(validateEkrDefinition({ ...baseValid(), canKill: null }).ok).toBe(false);
        expect(validateEkrDefinition({ ...baseValid(), canVent: "false" }).ok).toBe(false);
        expect(validateEkrDefinition({ ...baseValid(), canVent: null }).ok).toBe(false);
        // false 自体は正当な bool 値であり拒否されない
        expect(validateEkrDefinition({ ...baseValid(), canKill: false }).ok).toBe(true);
    });

    it("killCooldown/visionMultiplier: 型不一致 (文字列/配列) は拒否、null も拒否 (number は非 nullable 値型)", () => {
        expect(validateEkrDefinition({ ...baseValid(), killCooldown: "25" }).ok).toBe(false);
        expect(validateEkrDefinition({ ...baseValid(), killCooldown: [25] }).ok).toBe(false);
        expect(validateEkrDefinition({ ...baseValid(), killCooldown: null }).ok).toBe(false);
        expect(validateEkrDefinition({ ...baseValid(), visionMultiplier: "1" }).ok).toBe(false);
        expect(validateEkrDefinition({ ...baseValid(), visionMultiplier: null }).ok).toBe(false);
    });

    it("author: 型不一致 (数値) は拒否する (旧実装は黙って空文字にフォールバックしていたバグ)", () => {
        const r = validateEkrDefinition({ ...baseValid(), author: 123 });
        expect(r.ok).toBe(false);
    });

    it("author/name が null なら (型不一致でなく) 既定値へ収束する (string は参照型)", () => {
        expect(validateEkrDefinition({ ...baseValid(), author: null }).ok).toBe(true);
        // name は null → "" になった上で「空なら拒否」ルールに引っかかる (型不一致とは別経路)
        expect(validateEkrDefinition({ ...baseValid(), name: null }).ok).toBe(false);
    });

    it("color: 型不一致 (数値/配列) は拒否するが、文字列の書式不正は従来どおり既定色へフォールバック", () => {
        expect(validateEkrDefinition({ ...baseValid(), color: 123 }).ok).toBe(false);
        expect(validateEkrDefinition({ ...baseValid(), color: ["#fff"] }).ok).toBe(false);
        // 型は string だが正規表現に合わない → 引き続き reject でなくフォールバック
        const r = validateEkrDefinition({ ...baseValid(), color: "not-a-color" });
        expect(r.ok).toBe(true);
        if (r.ok) expect(r.def.color).toBe(DEFAULT_COLOR);
    });

    it("team: 配列を String() 経由で偶然一致させて素通りしていた抜け穴を塞ぐ (型不一致は拒否)", () => {
        // 旧実装は String(["crewmate"]) === "crewmate" になり誤って受理していた
        expect(validateEkrDefinition({ ...baseValid(), team: ["crewmate"] }).ok).toBe(false);
        expect(validateEkrDefinition({ ...baseValid(), team: 5 }).ok).toBe(false);
    });

    it("winCondition: 型不一致 (配列/数値) は拒否する (旧実装は String() 経由で受理していた)", () => {
        expect(validateEkrDefinition({ ...baseValid(), winCondition: ["team"] }).ok).toBe(false);
        expect(validateEkrDefinition({ ...baseValid(), winCondition: 5 }).ok).toBe(false);
    });
});

describe("logic 検証 (§1〜§4・R1)", () => {
    function baseLogic(overrides: Record<string, unknown> = {}): Record<string, unknown> {
        return {
            version: 1,
            rules: [{ when: "on_pet", do: [{ op: "stop" }] }],
            ...overrides,
        };
    }
    function withLogic(logic: unknown): Record<string, unknown> {
        return { ...baseValid(), logic };
    }

    it("logic 省略/null は R0 動作 (def.logic が付かない)", () => {
        const r1 = validateEkrDefinition(baseValid());
        expect(r1.ok).toBe(true);
        if (r1.ok) expect(r1.def.logic).toBeUndefined();
        const r2 = validateEkrDefinition(withLogic(null));
        expect(r2.ok).toBe(true);
        if (r2.ok) expect(r2.def.logic).toBeUndefined();
    });

    it("最小の logic (rule 1個・do 1ノード) は通る", () => {
        const r = validateEkrDefinition(withLogic(baseLogic()));
        expect(r.ok).toBe(true);
        if (r.ok) {
            expect(r.def.logic).toEqual({ version: 1, variables: [], rules: [{ when: "on_pet", do: [{ op: "stop" }] }] });
        }
    });

    it("version は 1 以外 (欠落・2・文字列) を拒否する", () => {
        expect(validateEkrDefinition(withLogic(baseLogic({ version: 2 }))).ok).toBe(false);
        expect(validateEkrDefinition(withLogic(baseLogic({ version: "1" }))).ok).toBe(false);
        const noVersion = baseLogic();
        delete noVersion.version;
        expect(validateEkrDefinition(withLogic(noVersion)).ok).toBe(false);
    });

    it("rules は 1〜32 個 (0個/33個/非配列 は拒否)", () => {
        expect(validateEkrDefinition(withLogic(baseLogic({ rules: [] }))).ok).toBe(false);
        expect(validateEkrDefinition(withLogic(baseLogic({ rules: "x" }))).ok).toBe(false);
        const rule = { when: "on_pet", do: [{ op: "stop" }] };
        const exactly32 = Array.from({ length: 32 }, () => rule);
        expect(validateEkrDefinition(withLogic(baseLogic({ rules: exactly32 }))).ok).toBe(true);
        const over32 = Array.from({ length: 33 }, () => rule);
        expect(validateEkrDefinition(withLogic(baseLogic({ rules: over32 }))).ok).toBe(false);
    });

    it("未知の when は拒否する", () => {
        const r = validateEkrDefinition(withLogic(baseLogic({ rules: [{ when: "on_flag_raise", do: [{ op: "stop" }] }] })));
        expect(r.ok).toBe(false);
    });

    it("10種類の when すべてを受理する", () => {
        const whens = [
            "on_game_start", "on_pet", "on_kill", "on_death", "on_meeting_start",
            "on_meeting_end", "on_task_complete", "on_vent_enter", "on_report", "on_second",
        ];
        for (const when of whens) {
            const r = validateEkrDefinition(withLogic(baseLogic({ rules: [{ when, do: [{ op: "stop" }] }] })));
            expect(r.ok, `when=${when} が拒否された`).toBe(true);
        }
    });

    it("未知の op は拒否する", () => {
        const r = validateEkrDefinition(withLogic(baseLogic({ rules: [{ when: "on_pet", do: [{ op: "explode" }] }] })));
        expect(r.ok).toBe(false);
    });

    it("未知の e (式の種類) は拒否する", () => {
        const rule = { when: "on_pet", do: [{ op: "wait", seconds: { e: "constant", v: 5 } }] };
        expect(validateEkrDefinition(withLogic(baseLogic({ rules: [rule] }))).ok).toBe(false);
    });

    it("do が空配列/欠落 は拒否する (1..64 の下限)", () => {
        expect(validateEkrDefinition(withLogic(baseLogic({ rules: [{ when: "on_pet", do: [] }] }))).ok).toBe(false);
        expect(validateEkrDefinition(withLogic(baseLogic({ rules: [{ when: "on_pet" }] }))).ok).toBe(false);
    });

    it("do ノード総数は64まで (then/else の入れ子込み) — 64個は通り65個は拒否", () => {
        const exactly64 = Array.from({ length: 64 }, () => ({ op: "stop" }));
        expect(validateEkrDefinition(withLogic(baseLogic({ rules: [{ when: "on_pet", do: exactly64 }] }))).ok).toBe(true);
        const over64 = Array.from({ length: 65 }, () => ({ op: "stop" }));
        expect(validateEkrDefinition(withLogic(baseLogic({ rules: [{ when: "on_pet", do: over64 }] }))).ok).toBe(false);

        // then/else の入れ子込みで数える (if 1 + then 63 = 64 は通る)
        const nestedOk = [{ op: "if", cond: { e: "lit", v: 1 }, then: Array.from({ length: 63 }, () => ({ op: "stop" })) }];
        expect(validateEkrDefinition(withLogic(baseLogic({ rules: [{ when: "on_pet", do: nestedOk }] }))).ok).toBe(true);
        const nestedOver = [{ op: "if", cond: { e: "lit", v: 1 }, then: Array.from({ length: 64 }, () => ({ op: "stop" })) }];
        expect(validateEkrDefinition(withLogic(baseLogic({ rules: [{ when: "on_pet", do: nestedOver }] }))).ok).toBe(false);
    });

    it("AST 深さは node/expr 合算で8まで — ちょうど8は通り9は拒否 (境界値)", () => {
        // if の入れ子を N 段作る: depth = N (各段が then に子1個を持つ if を積んだ場合)
        function nestedIf(depth: number): unknown {
            if (depth <= 1) return { op: "stop" };
            return { op: "if", cond: { e: "lit", v: 1 }, then: [nestedIf(depth - 1)] };
        }
        const depth8 = [nestedIf(8)];
        expect(validateEkrDefinition(withLogic(baseLogic({ rules: [{ when: "on_pet", do: depth8 }] }))).ok).toBe(true);
        const depth9 = [nestedIf(9)];
        expect(validateEkrDefinition(withLogic(baseLogic({ rules: [{ when: "on_pet", do: depth9 }] }))).ok).toBe(false);
    });

    it("AST 深さは expr の入れ子 (op の a/b) も node と合算してカウントする", () => {
        // wait.seconds は数値固定なので expr を持たない。var_set.value に op を N 段ネストして
        // 深さを作る: depth = 1 (var_set) + N (op の入れ子)
        function nestedOp(depth: number): unknown {
            if (depth <= 1) return { e: "lit", v: 1 };
            return { e: "op", kind: "add", a: nestedOp(depth - 1), b: { e: "lit", v: 1 } };
        }
        const varsOk = [{ name: "x", init: 0 }];
        const okNode = [{ op: "var_set", name: "x", value: nestedOp(7) }]; // 1(var_set) + 7 = 8
        expect(validateEkrDefinition(withLogic(baseLogic({ variables: varsOk, rules: [{ when: "on_pet", do: okNode }] }))).ok).toBe(true);
        const overNode = [{ op: "var_set", name: "x", value: nestedOp(8) }]; // 1 + 8 = 9
        expect(validateEkrDefinition(withLogic(baseLogic({ variables: varsOk, rules: [{ when: "on_pet", do: overNode }] }))).ok).toBe(false);
    });

    it("variables: 16個まで・name は20字まで・重複禁止・init は数値のみ", () => {
        const ok16 = Array.from({ length: 16 }, (_, i) => ({ name: `v${i}`, init: 0 }));
        expect(validateEkrDefinition(withLogic(baseLogic({ variables: ok16 }))).ok).toBe(true);
        const over17 = Array.from({ length: 17 }, (_, i) => ({ name: `v${i}`, init: 0 }));
        expect(validateEkrDefinition(withLogic(baseLogic({ variables: over17 }))).ok).toBe(false);

        expect(validateEkrDefinition(withLogic(baseLogic({ variables: [{ name: "A".repeat(20), init: 0 }] }))).ok).toBe(true);
        expect(validateEkrDefinition(withLogic(baseLogic({ variables: [{ name: "A".repeat(21), init: 0 }] }))).ok).toBe(false);

        expect(validateEkrDefinition(withLogic(baseLogic({ variables: [{ name: "x", init: 0 }, { name: "x", init: 1 }] }))).ok).toBe(false);

        expect(validateEkrDefinition(withLogic(baseLogic({ variables: [{ name: "x", init: "0" }] }))).ok).toBe(false);
        expect(validateEkrDefinition(withLogic(baseLogic({ variables: [{ name: "x", init: NaN }] }))).ok).toBe(false);
    });

    // spec §1: 省略だけが「変数なし」— 明示的な null は型不一致として reject
    it("variables: キー省略は受理・明示的な null は拒否 (型不一致は文書全体 reject)", () => {
        const omitted = validateEkrDefinition(withLogic({ version: 1, rules: [{ when: "on_pet", do: [{ op: "stop" }] }] }));
        expect(omitted.ok).toBe(true);
        if (omitted.ok) expect(omitted.def.logic?.variables).toEqual([]);

        expect(validateEkrDefinition(withLogic(baseLogic({ variables: null }))).ok).toBe(false);
        // 配列以外 (オブジェクト/数値/文字列) も同じく拒否
        expect(validateEkrDefinition(withLogic(baseLogic({ variables: {} }))).ok).toBe(false);
        expect(validateEkrDefinition(withLogic(baseLogic({ variables: 0 }))).ok).toBe(false);
    });

    it("var 式 / var_set / var_add は未定義の変数名を参照すると拒否する", () => {
        const varExpr = [{ op: "var_set", name: "count", value: { e: "var", name: "missing" } }];
        expect(validateEkrDefinition(withLogic(baseLogic({ rules: [{ when: "on_pet", do: varExpr }] }))).ok).toBe(false);

        const setUndeclared = [{ op: "var_set", name: "missing", value: { e: "lit", v: 1 } }];
        expect(validateEkrDefinition(withLogic(baseLogic({ rules: [{ when: "on_pet", do: setUndeclared }] }))).ok).toBe(false);

        const addUndeclared = [{ op: "var_add", name: "missing", delta: { e: "lit", v: 1 } }];
        expect(validateEkrDefinition(withLogic(baseLogic({ rules: [{ when: "on_pet", do: addUndeclared }] }))).ok).toBe(false);

        // 宣言済みなら通る
        const declared = [{ name: "count", init: 0 }];
        const setOk = [{ op: "var_set", name: "count", value: { e: "lit", v: 1 } }];
        expect(validateEkrDefinition(withLogic(baseLogic({ variables: declared, rules: [{ when: "on_pet", do: setOk }] }))).ok).toBe(true);
    });

    it("canKill=false の役職に on_kill ルールがあっても検証エラーにしない (発火しないだけ)", () => {
        const r = validateEkrDefinition({
            ...baseValid(),
            canKill: false,
            logic: baseLogic({ rules: [{ when: "on_kill", do: [{ op: "stop" }] }] }),
        });
        expect(r.ok).toBe(true);
    });

    it("if: then は必須 (省略/null は拒否)、else は省略可 (省略時は出力にキーが無い)", () => {
        const noThen = [{ op: "if", cond: { e: "lit", v: 1 } }];
        expect(validateEkrDefinition(withLogic(baseLogic({ rules: [{ when: "on_pet", do: noThen }] }))).ok).toBe(false);

        const withoutElse = [{ op: "if", cond: { e: "lit", v: 1 }, then: [{ op: "stop" }] }];
        const r = validateEkrDefinition(withLogic(baseLogic({ rules: [{ when: "on_pet", do: withoutElse }] })));
        expect(r.ok).toBe(true);
        if (r.ok) {
            const node = r.def.logic?.rules[0].do[0];
            expect(node).toEqual({ op: "if", cond: { e: "lit", v: 1 }, then: [{ op: "stop" }] });
            expect(node && "else" in node).toBe(false);
        }

        // then/else とも空配列は許容 (中身が空でも構造としては合法)
        const emptyThen = [{ op: "if", cond: { e: "lit", v: 1 }, then: [], else: [] }];
        expect(validateEkrDefinition(withLogic(baseLogic({ rules: [{ when: "on_pet", do: emptyThen }] }))).ok).toBe(true);
    });

    it("not は b が無くても良く、あっても無視される (reject しない)", () => {
        const notNode = [{ op: "var_set", name: "x", value: { e: "op", kind: "not", a: { e: "lit", v: 1 } } }];
        const vars = [{ name: "x", init: 0 }];
        expect(validateEkrDefinition(withLogic(baseLogic({ variables: vars, rules: [{ when: "on_pet", do: notNode }] }))).ok).toBe(true);

        const notWithB = [{ op: "var_set", name: "x", value: { e: "op", kind: "not", a: { e: "lit", v: 1 }, b: { e: "lit", v: 2 } } }];
        expect(validateEkrDefinition(withLogic(baseLogic({ variables: vars, rules: [{ when: "on_pet", do: notWithB }] }))).ok).toBe(true);
    });

    it("各アクション op の引数レンジを検証する (代表例: 範囲外は拒否・範囲内は通る)", () => {
        const cases: { node: unknown; ok: boolean }[] = [
            { node: { op: "wait", seconds: 0.1 }, ok: true },
            { node: { op: "wait", seconds: 600 }, ok: true },
            { node: { op: "wait", seconds: 0.05 }, ok: false },
            { node: { op: "wait", seconds: 600.1 }, ok: false },
            { node: { op: "notify", text: "x".repeat(120), seconds: 1 }, ok: true },
            { node: { op: "notify", text: "x".repeat(121), seconds: 1 }, ok: false },
            { node: { op: "notify", text: "hi", seconds: 0 }, ok: false },
            { node: { op: "notify", text: "hi", seconds: 31 }, ok: false },
            { node: { op: "teleport", to: "random" }, ok: true },
            { node: { op: "teleport", to: "ctx" }, ok: true },
            { node: { op: "teleport", to: "elsewhere" }, ok: false },
            { node: { op: "kill", target: "self" }, ok: true },
            { node: { op: "kill", target: "everyone" }, ok: false },
            { node: { op: "set_kill_cooldown", seconds: 300 }, ok: true },
            { node: { op: "set_kill_cooldown", seconds: 301 }, ok: false },
            { node: { op: "speed", mult: 0.5, seconds: 1 }, ok: true },
            { node: { op: "speed", mult: 3.0, seconds: 60 }, ok: true },
            { node: { op: "speed", mult: 3.1, seconds: 1 }, ok: false },
            { node: { op: "speed", mult: 1, seconds: 61 }, ok: false },
            { node: { op: "cno_spawn", slot: 1, text: "12345678", size: 12, at: "self" }, ok: true },
            { node: { op: "cno_spawn", slot: 4, text: "x", size: 1, at: "self" }, ok: false },
            { node: { op: "cno_spawn", slot: 1, text: "123456789", size: 1, at: "self" }, ok: false },
            { node: { op: "cno_spawn", slot: 1, text: "x", size: 13, at: "self" }, ok: false },
            { node: { op: "cno_spawn", slot: 1.5, text: "x", size: 1, at: "self" }, ok: false },
            { node: { op: "cno_move", slot: 1, dx: -50, dy: 50 }, ok: true },
            { node: { op: "cno_move", slot: 1, dx: -50.1, dy: 0 }, ok: false },
            { node: { op: "cno_despawn", slot: 3 }, ok: true },
            { node: { op: "cno_despawn", slot: 0 }, ok: false },
            { node: { op: "cno_show", slot: 1, who: "all" }, ok: true },
            { node: { op: "cno_show", slot: 1, who: "everyone" }, ok: false },
        ];
        for (const { node, ok } of cases) {
            const r = validateEkrDefinition(withLogic(baseLogic({ rules: [{ when: "on_pet", do: [node] }] })));
            expect(r.ok, `${JSON.stringify(node)} は ok=${ok} を期待`).toBe(ok);
        }
    });

    it("notify.text / cno_spawn.text の < > は全角 〈〉 へサニタイズされる (spec §3: TMP タグ注入防御)", () => {
        const notifyNode = { op: "notify", text: "<size=999>ばくはつ</size>", seconds: 3 };
        const r1 = validateEkrDefinition(withLogic(baseLogic({ rules: [{ when: "on_pet", do: [notifyNode] }] })));
        expect(r1.ok).toBe(true);
        if (r1.ok) {
            const node = r1.def.logic?.rules[0].do[0] as { text: string };
            expect(node.text).toBe("〈size=999〉ばくはつ〈/size〉");
        }

        const cnoNode = { op: "cno_spawn", slot: 1, text: "<b>", size: 1, at: "self" };
        const r2 = validateEkrDefinition(withLogic(baseLogic({ rules: [{ when: "on_pet", do: [cnoNode] }] })));
        expect(r2.ok).toBe(true);
        if (r2.ok) {
            const node = r2.def.logic?.rules[0].do[0] as { text: string };
            expect(node.text).toBe("〈b〉");
        }

        // サニタイズ後の文字数で上限判定するわけではない (全角化は1文字→1文字の置換なので影響しないが、
        // 念のため境界も確認しておく: ちょうど上限文字数の < > 混じり文字列も通る)
        const exactly8 = "<abcdef>"; // 8文字ちょうど (cno_spawn.text の上限)
        const r3 = validateEkrDefinition(withLogic(baseLogic({ rules: [{ when: "on_pet", do: [{ op: "cno_spawn", slot: 1, text: exactly8, size: 1, at: "self" }] }] })));
        expect(r3.ok).toBe(true);
        if (r3.ok) {
            const node = r3.def.logic?.rules[0].do[0] as { text: string };
            expect(node.text).toBe("〈abcdef〉");
        }
    });

    // v1.1 (§3 2026-08-09 追記): dummy_spawn / corpse_spawn
    it("dummy_spawn: slot/killable/at/name の範囲外・型不一致は拒否、範囲内は通る", () => {
        const cases: { node: unknown; ok: boolean }[] = [
            { node: { op: "dummy_spawn", slot: 1, name: "ダミー", killable: false, at: "self" }, ok: true },
            { node: { op: "dummy_spawn", slot: 3, name: "ダミー", killable: true, at: "ctx" }, ok: true },
            { node: { op: "dummy_spawn", slot: 0, name: "ダミー", killable: false, at: "self" }, ok: false },
            { node: { op: "dummy_spawn", slot: 4, name: "ダミー", killable: false, at: "self" }, ok: false },
            { node: { op: "dummy_spawn", slot: 1.5, name: "ダミー", killable: false, at: "self" }, ok: false },
            // killable は真の boolean のみ (canKill/canVent と同じ非対称 — "true"/1 は型不一致で拒否)
            { node: { op: "dummy_spawn", slot: 1, name: "ダミー", killable: "true", at: "self" }, ok: false },
            { node: { op: "dummy_spawn", slot: 1, name: "ダミー", killable: 1, at: "self" }, ok: false },
            { node: { op: "dummy_spawn", slot: 1, name: "ダミー", killable: false, at: "elsewhere" }, ok: false },
            { node: { op: "dummy_spawn", slot: 1, name: 123, killable: false, at: "self" }, ok: false },
            { node: { op: "dummy_spawn", slot: 1, name: "12345678", killable: false, at: "self" }, ok: true }, // ちょうど8字
            { node: { op: "dummy_spawn", slot: 1, name: "123456789", killable: false, at: "self" }, ok: false }, // 9字は拒否
        ];
        for (const { node, ok } of cases) {
            const r = validateEkrDefinition(withLogic(baseLogic({ rules: [{ when: "on_pet", do: [node] }] })));
            expect(r.ok, `${JSON.stringify(node)} は ok=${ok} を期待`).toBe(ok);
        }
    });

    it("dummy_spawn.name は trim してから8字判定する (trim 前が8字超でも trim 後8字以内なら通る)", () => {
        const padded = "  12345678  "; // trim すればちょうど8字
        const r = validateEkrDefinition(withLogic(baseLogic({
            rules: [{ when: "on_pet", do: [{ op: "dummy_spawn", slot: 1, name: padded, killable: false, at: "self" }] }],
        })));
        expect(r.ok).toBe(true);
        if (r.ok) {
            const node = r.def.logic?.rules[0].do[0] as { name: string };
            expect(node.name).toBe("12345678");
        }
    });

    it("dummy_spawn.name の < > は notify/cno_spawn と同じく全角 〈〉 へサニタイズされる", () => {
        const r = validateEkrDefinition(withLogic(baseLogic({
            rules: [{ when: "on_pet", do: [{ op: "dummy_spawn", slot: 1, name: "<a>", killable: false, at: "self" }] }],
        })));
        expect(r.ok).toBe(true);
        if (r.ok) {
            const node = r.def.logic?.rules[0].do[0] as { name: string };
            expect(node.name).toBe("〈a〉");
        }
    });

    it('dummy_spawn.name が空文字/空白のみなら "Dummy" に置換される (拒否ではない・spec §3)', () => {
        for (const rawName of ["", "   "]) {
            const r = validateEkrDefinition(withLogic(baseLogic({
                rules: [{ when: "on_pet", do: [{ op: "dummy_spawn", slot: 1, name: rawName, killable: false, at: "self" }] }],
            })));
            expect(r.ok, `name=${JSON.stringify(rawName)}`).toBe(true);
            if (r.ok) {
                const node = r.def.logic?.rules[0].do[0] as { name: string };
                expect(node.name).toBe("Dummy");
            }
        }
    });

    it("corpse_spawn: color/at の enum 外は拒否、有効な組み合わせは通る", () => {
        const cases: { node: unknown; ok: boolean }[] = [
            { node: { op: "corpse_spawn", color: "self", at: "self" }, ok: true },
            { node: { op: "corpse_spawn", color: "random", at: "ctx" }, ok: true },
            { node: { op: "corpse_spawn", color: "blue", at: "self" }, ok: false },
            { node: { op: "corpse_spawn", color: "self", at: "elsewhere" }, ok: false },
            { node: { op: "corpse_spawn", color: 1, at: "self" }, ok: false },
        ];
        for (const { node, ok } of cases) {
            const r = validateEkrDefinition(withLogic(baseLogic({ rules: [{ when: "on_pet", do: [node] }] })));
            expect(r.ok, `${JSON.stringify(node)} は ok=${ok} を期待`).toBe(ok);
        }
    });

    it("dummy_spawn/corpse_spawn は leaf ノード (depth 1, count 1) として数えられる — 深さ8ちょうどの入れ子でも通る", () => {
        function nestedIf(depth: number, leaf: unknown): unknown {
            if (depth <= 1) return leaf;
            return { op: "if", cond: { e: "lit", v: 1 }, then: [nestedIf(depth - 1, leaf)] };
        }
        const dummyLeaf = { op: "dummy_spawn", slot: 1, name: "ダミー", killable: false, at: "self" };
        const depth8 = [nestedIf(8, dummyLeaf)];
        expect(validateEkrDefinition(withLogic(baseLogic({ rules: [{ when: "on_pet", do: depth8 }] }))).ok).toBe(true);
        const depth9 = [nestedIf(9, dummyLeaf)];
        expect(validateEkrDefinition(withLogic(baseLogic({ rules: [{ when: "on_pet", do: depth9 }] }))).ok).toBe(false);

        const corpseLeaf = { op: "corpse_spawn", color: "self", at: "self" };
        const corpseDepth8 = [nestedIf(8, corpseLeaf)];
        expect(validateEkrDefinition(withLogic(baseLogic({ rules: [{ when: "on_pet", do: corpseDepth8 }] }))).ok).toBe(true);
        const corpseDepth9 = [nestedIf(9, corpseLeaf)];
        expect(validateEkrDefinition(withLogic(baseLogic({ rules: [{ when: "on_pet", do: corpseDepth9 }] }))).ok).toBe(false);
    });

    it("blockly フィールドは中身を検証せず、あればそのまま保持する (C# は一切見ない)", () => {
        const withBlockly = baseLogic({ blockly: { blocks: { blocks: [] }, arbitrary: "data" } });
        const r = validateEkrDefinition(withLogic(withBlockly));
        expect(r.ok).toBe(true);
        if (r.ok) expect(r.def.logic?.blockly).toEqual({ blocks: { blocks: [] }, arbitrary: "data" });
    });

    it("logic 自体が JSON オブジェクトでない (配列/文字列/数値) 場合は拒否する", () => {
        expect(validateEkrDefinition(withLogic([1, 2, 3])).ok).toBe(false);
        expect(validateEkrDefinition(withLogic("nope")).ok).toBe(false);
        expect(validateEkrDefinition(withLogic(5)).ok).toBe(false);
    });
});

// v1.2 (§2〜§3 2026-08-10 追記) — 位置と接触
describe("logic 検証 v1.2 (位置と接触)", () => {
    function baseLogic(overrides: Record<string, unknown> = {}): Record<string, unknown> {
        return {
            version: 1,
            rules: [{ when: "on_pet", do: [{ op: "stop" }] }],
            ...overrides,
        };
    }
    function withLogic(logic: unknown): Record<string, unknown> {
        return { ...baseValid(), logic };
    }

    it("on_cno_touch は 11 種目として受理される", () => {
        const r = validateEkrDefinition(withLogic(baseLogic({
            rules: [{ when: "on_cno_touch", slot: 1, do: [{ op: "stop" }] }],
        })));
        expect(r.ok).toBe(true);
        if (r.ok) expect(r.def.logic?.rules[0]).toMatchObject({ when: "on_cno_touch", slot: 1 });
    });

    it("on_cno_touch は slot (1..3) 必須 — 欠落・範囲外・非整数は拒否", () => {
        expect(validateEkrDefinition(withLogic(baseLogic({ rules: [{ when: "on_cno_touch", do: [{ op: "stop" }] }] }))).ok).toBe(false);
        expect(validateEkrDefinition(withLogic(baseLogic({ rules: [{ when: "on_cno_touch", slot: 0, do: [{ op: "stop" }] }] }))).ok).toBe(false);
        expect(validateEkrDefinition(withLogic(baseLogic({ rules: [{ when: "on_cno_touch", slot: 4, do: [{ op: "stop" }] }] }))).ok).toBe(false);
        expect(validateEkrDefinition(withLogic(baseLogic({ rules: [{ when: "on_cno_touch", slot: 1.5, do: [{ op: "stop" }] }] }))).ok).toBe(false);
        expect(validateEkrDefinition(withLogic(baseLogic({ rules: [{ when: "on_cno_touch", slot: 1, do: [{ op: "stop" }] }] }))).ok).toBe(true);
        expect(validateEkrDefinition(withLogic(baseLogic({ rules: [{ when: "on_cno_touch", slot: 3, do: [{ op: "stop" }] }] }))).ok).toBe(true);
    });

    it("on_cno_touch 以外の when に slot があれば拒否 (双方向の reject)", () => {
        const r = validateEkrDefinition(withLogic(baseLogic({ rules: [{ when: "on_pet", slot: 1, do: [{ op: "stop" }] }] })));
        expect(r.ok).toBe(false);
    });

    it("teleport.to にマーカー1..4 を受理する (未知の marker5 等は拒否)", () => {
        for (const to of ["marker1", "marker2", "marker3", "marker4"]) {
            const r = validateEkrDefinition(withLogic(baseLogic({ rules: [{ when: "on_pet", do: [{ op: "teleport", to }] }] })));
            expect(r.ok, `to=${to}`).toBe(true);
        }
        const bad = validateEkrDefinition(withLogic(baseLogic({ rules: [{ when: "on_pet", do: [{ op: "teleport", to: "marker5" }] }] })));
        expect(bad.ok).toBe(false);
    });

    it("marker_save: slot 1..4・at (self/ctx/cno1..3) の範囲外・型不一致は拒否、範囲内は通る", () => {
        const cases: { node: unknown; ok: boolean }[] = [
            { node: { op: "marker_save", slot: 1, at: "self" }, ok: true },
            { node: { op: "marker_save", slot: 4, at: "ctx" }, ok: true },
            { node: { op: "marker_save", slot: 4, at: "cno3" }, ok: true },
            { node: { op: "marker_save", slot: 0, at: "self" }, ok: false },
            { node: { op: "marker_save", slot: 5, at: "self" }, ok: false },
            { node: { op: "marker_save", slot: 1.5, at: "self" }, ok: false },
            { node: { op: "marker_save", slot: 1, at: "cno4" }, ok: false },
            { node: { op: "marker_save", slot: 1, at: "elsewhere" }, ok: false },
        ];
        for (const { node, ok } of cases) {
            const r = validateEkrDefinition(withLogic(baseLogic({ rules: [{ when: "on_pet", do: [node] }] })));
            expect(r.ok, `${JSON.stringify(node)} は ok=${ok} を期待`).toBe(ok);
        }
    });

    it("teleport_other: target は ctx のみ・to は self/marker1..4 のみ受理", () => {
        const cases: { node: unknown; ok: boolean }[] = [
            { node: { op: "teleport_other", target: "ctx", to: "self" }, ok: true },
            { node: { op: "teleport_other", target: "ctx", to: "marker1" }, ok: true },
            { node: { op: "teleport_other", target: "ctx", to: "marker4" }, ok: true },
            { node: { op: "teleport_other", target: "self", to: "self" }, ok: false },
            { node: { op: "teleport_other", target: "ctx", to: "random" }, ok: false },
            { node: { op: "teleport_other", target: "ctx", to: "ctx" }, ok: false },
        ];
        for (const { node, ok } of cases) {
            const r = validateEkrDefinition(withLogic(baseLogic({ rules: [{ when: "on_pet", do: [node] }] })));
            expect(r.ok, `${JSON.stringify(node)} は ok=${ok} を期待`).toBe(ok);
        }
    });

    it("portal_place: which は a/b のみ受理", () => {
        expect(validateEkrDefinition(withLogic(baseLogic({ rules: [{ when: "on_pet", do: [{ op: "portal_place", which: "a" }] }] }))).ok).toBe(true);
        expect(validateEkrDefinition(withLogic(baseLogic({ rules: [{ when: "on_pet", do: [{ op: "portal_place", which: "b" }] }] }))).ok).toBe(true);
        expect(validateEkrDefinition(withLogic(baseLogic({ rules: [{ when: "on_pet", do: [{ op: "portal_place", which: "c" }] }] }))).ok).toBe(false);
    });

    it("marker_save/teleport_other/portal_place は leaf ノード (depth 1, count 1)", () => {
        function nestedIf(depth: number, leaf: unknown): unknown {
            if (depth <= 1) return leaf;
            return { op: "if", cond: { e: "lit", v: 1 }, then: [nestedIf(depth - 1, leaf)] };
        }
        for (const leaf of [
            { op: "marker_save", slot: 1, at: "self" },
            { op: "teleport_other", target: "ctx", to: "self" },
            { op: "portal_place", which: "a" },
        ]) {
            const depth8 = [nestedIf(8, leaf)];
            expect(validateEkrDefinition(withLogic(baseLogic({ rules: [{ when: "on_pet", do: depth8 }] }))).ok, JSON.stringify(leaf)).toBe(true);
            const depth9 = [nestedIf(9, leaf)];
            expect(validateEkrDefinition(withLogic(baseLogic({ rules: [{ when: "on_pet", do: depth9 }] }))).ok, JSON.stringify(leaf)).toBe(false);
        }
    });
});

// v1.3 (§3 2026-08-11 追記) — ひっぱる・ひきずる・フィールド
describe("logic 検証 v1.3 (ひっぱる・ひきずる・フィールド)", () => {
    function baseLogic(overrides: Record<string, unknown> = {}): Record<string, unknown> {
        return {
            version: 1,
            rules: [{ when: "on_pet", do: [{ op: "stop" }] }],
            ...overrides,
        };
    }
    function withLogic(logic: unknown): Record<string, unknown> {
        return { ...baseValid(), logic };
    }

    it("pull は引数なしで受理される", () => {
        const r = validateEkrDefinition(withLogic(baseLogic({ rules: [{ when: "on_pet", do: [{ op: "pull" }] }] })));
        expect(r.ok).toBe(true);
        if (r.ok) expect(r.def.logic?.rules[0].do[0]).toEqual({ op: "pull" });
    });

    it("pull に余計なフィールドがあっても無視される (余剰キーは黙って無視)", () => {
        const r = validateEkrDefinition(withLogic(baseLogic({ rules: [{ when: "on_pet", do: [{ op: "pull", target: "ctx" }] }] })));
        expect(r.ok).toBe(true);
        if (r.ok) expect(r.def.logic?.rules[0].do[0]).toEqual({ op: "pull" });
    });

    it("drag: seconds は 1..10 (境界値は通り、範囲外/非数値は拒否)", () => {
        const cases: { node: unknown; ok: boolean }[] = [
            { node: { op: "drag", seconds: 1 }, ok: true },
            { node: { op: "drag", seconds: 10 }, ok: true },
            { node: { op: "drag", seconds: 0.9 }, ok: false },
            { node: { op: "drag", seconds: 10.1 }, ok: false },
            { node: { op: "drag", seconds: "3" }, ok: false },
        ];
        for (const { node, ok } of cases) {
            const r = validateEkrDefinition(withLogic(baseLogic({ rules: [{ when: "on_pet", do: [node] }] })));
            expect(r.ok, `${JSON.stringify(node)} は ok=${ok} を期待`).toBe(ok);
        }
    });

    it("field: at/radius/strength/seconds の範囲外・未知値は拒否、範囲内は通る", () => {
        const cases: { node: unknown; ok: boolean }[] = [
            { node: { op: "field", at: "self", radius: "small", strength: "weak", seconds: 1 }, ok: true },
            { node: { op: "field", at: "ctx", radius: "medium", strength: "medium", seconds: 15 }, ok: true },
            { node: { op: "field", at: "marker4", radius: "large", strength: "strong", seconds: 15 }, ok: true },
            { node: { op: "field", at: "marker5", radius: "small", strength: "weak", seconds: 1 }, ok: false },
            { node: { op: "field", at: "self", radius: "huge", strength: "weak", seconds: 1 }, ok: false },
            { node: { op: "field", at: "self", radius: "small", strength: "extreme", seconds: 1 }, ok: false },
            { node: { op: "field", at: "self", radius: "small", strength: "weak", seconds: 0.9 }, ok: false },
            { node: { op: "field", at: "self", radius: "small", strength: "weak", seconds: 15.1 }, ok: false },
        ];
        for (const { node, ok } of cases) {
            const r = validateEkrDefinition(withLogic(baseLogic({ rules: [{ when: "on_pet", do: [node] }] })));
            expect(r.ok, `${JSON.stringify(node)} は ok=${ok} を期待`).toBe(ok);
        }
    });

    it("pull/drag/field は leaf ノード (depth 1, count 1) — 深さ8ちょうどの入れ子でも通る", () => {
        function nestedIf(depth: number, leaf: unknown): unknown {
            if (depth <= 1) return leaf;
            return { op: "if", cond: { e: "lit", v: 1 }, then: [nestedIf(depth - 1, leaf)] };
        }
        for (const leaf of [
            { op: "pull" },
            { op: "drag", seconds: 3 },
            { op: "field", at: "self", radius: "small", strength: "weak", seconds: 3 },
        ]) {
            const depth8 = [nestedIf(8, leaf)];
            expect(validateEkrDefinition(withLogic(baseLogic({ rules: [{ when: "on_pet", do: depth8 }] }))).ok, JSON.stringify(leaf)).toBe(true);
            const depth9 = [nestedIf(9, leaf)];
            expect(validateEkrDefinition(withLogic(baseLogic({ rules: [{ when: "on_pet", do: depth9 }] }))).ok, JSON.stringify(leaf)).toBe(false);
        }
    });
});

// ---------------------------------------------------------------------------
// Wave 1 (§1.1/§2/§3 2026-08-11 併合)
// ---------------------------------------------------------------------------

describe("passives 検証 (Wave 1・spec §1.1)", () => {
    function withPassives(passives: unknown): Record<string, unknown> {
        return { ...baseValid(), passives };
    }

    it("6キーすべてを正常値で指定できる (そのまま保持される)", () => {
        const passives = {
            speedMult: 1.5,
            killDistance: "long",
            shield: { count: 3 },
            corpse: "vanish",
            voteWeight: 2,
            doom: { seconds: 90 },
        };
        const r = validateEkrDefinition(withPassives(passives));
        expect(r.ok).toBe(true);
        if (r.ok) expect(r.def.passives).toEqual(passives);
    });

    it("passives 省略/null は passives キー自体を持たない (R0 互換)", () => {
        const omitted = validateEkrDefinition(baseValid());
        expect(omitted.ok).toBe(true);
        if (omitted.ok) expect("passives" in omitted.def).toBe(false);

        const nulled = validateEkrDefinition(withPassives(null));
        expect(nulled.ok).toBe(true);
        if (nulled.ok) expect("passives" in nulled.def).toBe(false);
    });

    it("既知キーが1つも無い passives ({} / 未知キーのみ) はキーごと落とす (未知キーは黙って無視)", () => {
        for (const p of [{}, { みらいのとくせい: 1, visionMult: 2 }]) {
            const r = validateEkrDefinition(withPassives(p));
            expect(r.ok).toBe(true);
            if (r.ok) expect("passives" in r.def).toBe(false);
        }
    });

    it("既知キーと未知キーが混在しても、既知キーだけを拾う", () => {
        const r = validateEkrDefinition(withPassives({ voteWeight: 0, visionMult: 3 }));
        expect(r.ok).toBe(true);
        if (r.ok) expect(r.def.passives).toEqual({ voteWeight: 0 });
    });

    it("passives がオブジェクトでなければ拒否", () => {
        expect(validateEkrDefinition(withPassives(5)).ok).toBe(false);
        expect(validateEkrDefinition(withPassives("shield")).ok).toBe(false);
        expect(validateEkrDefinition(withPassives([{ voteWeight: 1 }])).ok).toBe(false);
    });

    it("speedMult は 0.5〜3.0 の範囲外/非数値を拒否 (クランプしない)", () => {
        expect(validateEkrDefinition(withPassives({ speedMult: 0.5 })).ok).toBe(true);
        expect(validateEkrDefinition(withPassives({ speedMult: 3 })).ok).toBe(true);
        expect(validateEkrDefinition(withPassives({ speedMult: 0.4 })).ok).toBe(false);
        expect(validateEkrDefinition(withPassives({ speedMult: 3.1 })).ok).toBe(false);
        expect(validateEkrDefinition(withPassives({ speedMult: "1.5" })).ok).toBe(false);
        expect(validateEkrDefinition(withPassives({ speedMult: null })).ok).toBe(false);
    });

    it("killDistance は short/medium/long のみ", () => {
        for (const v of ["short", "medium", "long"]) {
            expect(validateEkrDefinition(withPassives({ killDistance: v })).ok, v).toBe(true);
        }
        expect(validateEkrDefinition(withPassives({ killDistance: "veryLong" })).ok).toBe(false);
        expect(validateEkrDefinition(withPassives({ killDistance: 1 })).ok).toBe(false);
    });

    it("shield.count は 1〜9 の整数のみ (count 欠落は拒否 — shield があるのに数が無いのは誤り)", () => {
        expect(validateEkrDefinition(withPassives({ shield: { count: 1 } })).ok).toBe(true);
        expect(validateEkrDefinition(withPassives({ shield: { count: 9 } })).ok).toBe(true);
        expect(validateEkrDefinition(withPassives({ shield: { count: 0 } })).ok).toBe(false);
        expect(validateEkrDefinition(withPassives({ shield: { count: 10 } })).ok).toBe(false);
        expect(validateEkrDefinition(withPassives({ shield: { count: 1.5 } })).ok).toBe(false);
        expect(validateEkrDefinition(withPassives({ shield: {} })).ok).toBe(false);
        expect(validateEkrDefinition(withPassives({ shield: 3 })).ok).toBe(false);
    });

    it("corpse は normal/noReport/vanish のみ", () => {
        for (const v of ["normal", "noReport", "vanish"]) {
            expect(validateEkrDefinition(withPassives({ corpse: v })).ok, v).toBe(true);
        }
        expect(validateEkrDefinition(withPassives({ corpse: "noreport" })).ok).toBe(false);
    });

    it("voteWeight は 0〜3 の整数のみ", () => {
        for (const v of [0, 1, 2, 3]) {
            expect(validateEkrDefinition(withPassives({ voteWeight: v })).ok, String(v)).toBe(true);
        }
        expect(validateEkrDefinition(withPassives({ voteWeight: -1 })).ok).toBe(false);
        expect(validateEkrDefinition(withPassives({ voteWeight: 4 })).ok).toBe(false);
        expect(validateEkrDefinition(withPassives({ voteWeight: 1.5 })).ok).toBe(false);
    });

    it("doom.seconds は 30〜600 の整数のみ (seconds 欠落は拒否)", () => {
        expect(validateEkrDefinition(withPassives({ doom: { seconds: 30 } })).ok).toBe(true);
        expect(validateEkrDefinition(withPassives({ doom: { seconds: 600 } })).ok).toBe(true);
        expect(validateEkrDefinition(withPassives({ doom: { seconds: 29 } })).ok).toBe(false);
        expect(validateEkrDefinition(withPassives({ doom: { seconds: 601 } })).ok).toBe(false);
        expect(validateEkrDefinition(withPassives({ doom: { seconds: 60.5 } })).ok).toBe(false);
        expect(validateEkrDefinition(withPassives({ doom: {} })).ok).toBe(false);
    });

    it("logic 無しでも passives 単独で使える", () => {
        const r = validateEkrDefinition(withPassives({ shield: { count: 2 } }));
        expect(r.ok).toBe(true);
        if (r.ok) {
            expect(r.def.logic).toBeUndefined();
            expect(r.def.passives).toEqual({ shield: { count: 2 } });
        }
    });

    it("validatePassives を単体で呼んでも同じ規則 (フォーム側からの利用)", () => {
        expect(validatePassives({ voteWeight: 0 })).toEqual({ ok: true, passives: { voteWeight: 0 } });
        expect(validatePassives({ voteWeight: 9 }).ok).toBe(false);
    });
});

describe("logic 検証 Wave 1 (on_attacked / cancel_attack / remember / セレクタ拡張)", () => {
    function baseLogic(overrides: Record<string, unknown> = {}): Record<string, unknown> {
        return {
            version: 1,
            rules: [{ when: "on_pet", do: [{ op: "stop" }] }],
            ...overrides,
        };
    }
    function withLogic(logic: unknown): Record<string, unknown> {
        return { ...baseValid(), logic };
    }
    function withRule(rule: Record<string, unknown>): Record<string, unknown> {
        return withLogic(baseLogic({ rules: [rule] }));
    }

    it("on_attacked は 12 種目の when として受理される", () => {
        const r = validateEkrDefinition(withRule({ when: "on_attacked", do: [{ op: "cancel_attack" }] }));
        expect(r.ok).toBe(true);
        if (r.ok) expect(r.def.logic?.rules[0].when).toBe("on_attacked");
    });

    it("on_attacked に slot は付けられない (slot は on_cno_touch 専用)", () => {
        expect(validateEkrDefinition(withRule({ when: "on_attacked", slot: 1, do: [{ op: "stop" }] })).ok).toBe(false);
    });

    it("cancel_attack は on_attacked 配下でのみ受理 (他イベント配下は文書 reject)", () => {
        expect(validateEkrDefinition(withRule({ when: "on_attacked", do: [{ op: "cancel_attack" }] })).ok).toBe(true);
        for (const when of ["on_pet", "on_kill", "on_death", "on_second"]) {
            expect(validateEkrDefinition(withRule({ when, do: [{ op: "cancel_attack" }] })).ok, when).toBe(false);
        }
    });

    it("cancel_attack は if の入れ子の中でも配置検査される", () => {
        const nested = (thenNodes: unknown[]) => ({ op: "if", cond: { e: "lit", v: 1 }, then: thenNodes });
        expect(validateEkrDefinition(withRule({ when: "on_attacked", do: [nested([{ op: "cancel_attack" }])] })).ok).toBe(true);
        expect(validateEkrDefinition(withRule({ when: "on_pet", do: [nested([{ op: "cancel_attack" }])] })).ok).toBe(false);
        // else 側も同じ
        const withElse = { op: "if", cond: { e: "lit", v: 1 }, then: [{ op: "stop" }], else: [{ op: "cancel_attack" }] };
        expect(validateEkrDefinition(withRule({ when: "on_pet", do: [withElse] })).ok).toBe(false);
    });

    it("remember は slot 1..2 と単数セレクタのみ", () => {
        for (const target of ["self", "ctx", "saved1", "saved2", "nearest", "random"]) {
            expect(validateEkrDefinition(withRule({ when: "on_kill", do: [{ op: "remember", slot: 1, target }] })).ok, target).toBe(true);
        }
        expect(validateEkrDefinition(withRule({ when: "on_kill", do: [{ op: "remember", slot: 2, target: "ctx" }] })).ok).toBe(true);
        expect(validateEkrDefinition(withRule({ when: "on_kill", do: [{ op: "remember", slot: 3, target: "ctx" }] })).ok).toBe(false);
        expect(validateEkrDefinition(withRule({ when: "on_kill", do: [{ op: "remember", slot: 0, target: "ctx" }] })).ok).toBe(false);
        for (const target of ["all", "room", "marker1"]) {
            expect(validateEkrDefinition(withRule({ when: "on_kill", do: [{ op: "remember", slot: 1, target }] })).ok, target).toBe(false);
        }
    });

    it("kill.target は単数セレクタ全種を受理し、複数セレクタは拒否", () => {
        for (const target of ["self", "ctx", "saved1", "saved2", "nearest", "random"]) {
            expect(validateEkrDefinition(withRule({ when: "on_pet", do: [{ op: "kill", target }] })).ok, target).toBe(true);
        }
        for (const target of ["all", "room", "saved3"]) {
            expect(validateEkrDefinition(withRule({ when: "on_pet", do: [{ op: "kill", target }] })).ok, target).toBe(false);
        }
    });

    it("teleport_other.target は単数セレクタ (self は含まない)、to は cno1..3 も受理", () => {
        for (const target of ["ctx", "saved1", "saved2", "nearest", "random"]) {
            expect(validateEkrDefinition(withRule({ when: "on_kill", do: [{ op: "teleport_other", target, to: "self" }] })).ok, target).toBe(true);
        }
        expect(validateEkrDefinition(withRule({ when: "on_kill", do: [{ op: "teleport_other", target: "self", to: "self" }] })).ok).toBe(false);
        expect(validateEkrDefinition(withRule({ when: "on_kill", do: [{ op: "teleport_other", target: "all", to: "self" }] })).ok).toBe(false);
        for (const to of ["cno1", "cno2", "cno3", "marker4"]) {
            expect(validateEkrDefinition(withRule({ when: "on_kill", do: [{ op: "teleport_other", target: "ctx", to }] })).ok, to).toBe(true);
        }
        expect(validateEkrDefinition(withRule({ when: "on_kill", do: [{ op: "teleport_other", target: "ctx", to: "cno4" }] })).ok).toBe(false);
    });

    it("teleport.to は cno1..3 を受理する", () => {
        for (const to of ["cno1", "cno2", "cno3"]) {
            expect(validateEkrDefinition(withRule({ when: "on_pet", do: [{ op: "teleport", to }] })).ok, to).toBe(true);
        }
        expect(validateEkrDefinition(withRule({ when: "on_pet", do: [{ op: "teleport", to: "cno0" }] })).ok).toBe(false);
    });

    it("notify.target は省略可 (省略時は AST にキーを足さない)・単複とも受理", () => {
        const omitted = validateEkrDefinition(withRule({ when: "on_pet", do: [{ op: "notify", text: "やあ", seconds: 3 }] }));
        expect(omitted.ok).toBe(true);
        if (omitted.ok) expect(omitted.def.logic?.rules[0].do[0]).toEqual({ op: "notify", text: "やあ", seconds: 3 });

        for (const target of ["self", "ctx", "saved1", "saved2", "nearest", "random", "all", "room"]) {
            expect(validateEkrDefinition(withRule({ when: "on_kill", do: [{ op: "notify", text: "やあ", seconds: 3, target }] })).ok, target).toBe(true);
        }
        expect(validateEkrDefinition(withRule({ when: "on_kill", do: [{ op: "notify", text: "やあ", seconds: 3, target: "marker1" }] })).ok).toBe(false);
    });

    it("field.at / marker_save.at は Wave 1 で拡張していない (field は cno を受理しない・marker_save は元から受理する)", () => {
        expect(validateEkrDefinition(withRule({ when: "on_pet", do: [{ op: "field", at: "cno1", radius: "small", strength: "weak", seconds: 3 }] })).ok).toBe(false);
        expect(validateEkrDefinition(withRule({ when: "on_pet", do: [{ op: "marker_save", slot: 1, at: "cno1" }] })).ok).toBe(true);
    });
});

// ---------------------------------------------------------------------------
// Wave 2 (2026-08-11) — 情報と会議
// ---------------------------------------------------------------------------
describe("logic 検証 Wave 2 (on_meeting_vote / on_meeting_pick / inspect / reveal / arrow_* / vote_* / exile)", () => {
    function baseLogic(overrides: Record<string, unknown> = {}): Record<string, unknown> {
        return {
            version: 1,
            rules: [{ when: "on_pet", do: [{ op: "stop" }] }],
            ...overrides,
        };
    }
    function withLogic(logic: unknown): Record<string, unknown> {
        return { ...baseValid(), logic };
    }
    function withRule(rule: Record<string, unknown>): Record<string, unknown> {
        return withLogic(baseLogic({ rules: [rule] }));
    }

    it("14 種類の when すべてを受理する (Wave 2 の2種を含む)", () => {
        const whens = [
            "on_game_start", "on_pet", "on_kill", "on_death", "on_meeting_start",
            "on_meeting_end", "on_task_complete", "on_vent_enter", "on_report", "on_second",
            "on_attacked", "on_meeting_vote", "on_meeting_pick",
        ];
        for (const when of whens) {
            const r = validateEkrDefinition(withRule({ when, do: [{ op: "stop" }] }));
            expect(r.ok, `when=${when} が拒否された`).toBe(true);
        }
    });

    it("on_meeting_vote / on_meeting_pick に slot が付いていれば拒否 (on_cno_touch 専用)", () => {
        expect(validateEkrDefinition(withRule({ when: "on_meeting_vote", slot: 1, do: [{ op: "stop" }] })).ok).toBe(false);
        expect(validateEkrDefinition(withRule({ when: "on_meeting_pick", slot: 1, do: [{ op: "stop" }] })).ok).toBe(false);
    });

    it("cancel_vote は on_meeting_vote 配下でのみ受理 (他イベント配下は文書 reject・if の入れ子でも判定)", () => {
        expect(validateEkrDefinition(withRule({ when: "on_meeting_vote", do: [{ op: "cancel_vote" }] })).ok).toBe(true);
        for (const when of ["on_pet", "on_kill", "on_meeting_start", "on_meeting_pick", "on_second"]) {
            expect(validateEkrDefinition(withRule({ when, do: [{ op: "cancel_vote" }] })).ok, when).toBe(false);
        }
        const nested = { op: "if", cond: { e: "lit", v: 1 }, then: [{ op: "cancel_vote" }] };
        expect(validateEkrDefinition(withRule({ when: "on_pet", do: [nested] })).ok).toBe(false);
        expect(validateEkrDefinition(withRule({ when: "on_meeting_vote", do: [nested] })).ok).toBe(true);
    });

    it("on_meeting_vote は cancel_vote を伴わなくても正当 (「投票した人をおぼえる」だけの定義も通る)", () => {
        const r = validateEkrDefinition(withRule({ when: "on_meeting_vote", do: [{ op: "remember", slot: 1, target: "ctx" }] }));
        expect(r.ok).toBe(true);
    });

    it("inspect: target は self 不可・depth は team/role のみ・failChance/noise は範囲外を拒否", () => {
        const cases: { node: unknown; ok: boolean }[] = [
            { node: { op: "inspect", target: "ctx", depth: "team" }, ok: true },
            { node: { op: "inspect", target: "saved1", depth: "role" }, ok: true },
            { node: { op: "inspect", target: "nearest", depth: "role", failChance: 0, noise: 0 }, ok: true },
            { node: { op: "inspect", target: "random", depth: "role", failChance: 100, noise: 5 }, ok: true },
            { node: { op: "inspect", target: "self", depth: "team" }, ok: false },
            { node: { op: "inspect", target: "ctx", depth: "faction" }, ok: false },
            { node: { op: "inspect", target: "ctx", depth: "team", failChance: 101 }, ok: false },
            { node: { op: "inspect", target: "ctx", depth: "team", failChance: -1 }, ok: false },
            { node: { op: "inspect", target: "ctx", depth: "role", noise: 6 }, ok: false },
            { node: { op: "inspect", target: "ctx", depth: "role", noise: -1 }, ok: false },
            { node: { op: "inspect", target: "ctx", depth: "role", noise: 1.5 }, ok: false },
        ];
        for (const { node, ok } of cases) {
            const r = validateEkrDefinition(withRule({ when: "on_kill", do: [node] }));
            expect(r.ok, `${JSON.stringify(node)} は ok=${ok} を期待`).toBe(ok);
        }
    });

    it("inspect: noise>0 と depth:'team' の併用は拒否、noise=0 との併用は許容 (実装裁量)", () => {
        expect(validateEkrDefinition(withRule({ when: "on_kill", do: [{ op: "inspect", target: "ctx", depth: "team", noise: 1 }] })).ok).toBe(false);
        expect(validateEkrDefinition(withRule({ when: "on_kill", do: [{ op: "inspect", target: "ctx", depth: "team", noise: 0 }] })).ok).toBe(true);
    });

    it("inspect: failChance/noise は省略可 (省略時はキーを付けない)", () => {
        const r = validateEkrDefinition(withRule({ when: "on_kill", do: [{ op: "inspect", target: "ctx", depth: "role" }] }));
        expect(r.ok).toBe(true);
        if (r.ok) {
            const node = r.def.logic?.rules[0].do[0];
            expect(node).toEqual({ op: "inspect", target: "ctx", depth: "role" });
        }
    });

    it("reveal: target は self 不可の単数セレクタ", () => {
        for (const target of ["ctx", "saved1", "saved2", "nearest", "random"]) {
            expect(validateEkrDefinition(withRule({ when: "on_kill", do: [{ op: "reveal", target }] })).ok, target).toBe(true);
        }
        expect(validateEkrDefinition(withRule({ when: "on_kill", do: [{ op: "reveal", target: "self" }] })).ok).toBe(false);
        expect(validateEkrDefinition(withRule({ when: "on_kill", do: [{ op: "reveal", target: "all" }] })).ok).toBe(false);
    });

    it("arrow_show: target は self 不可・seconds は5..600", () => {
        expect(validateEkrDefinition(withRule({ when: "on_kill", do: [{ op: "arrow_show", target: "ctx", seconds: 5 }] })).ok).toBe(true);
        expect(validateEkrDefinition(withRule({ when: "on_kill", do: [{ op: "arrow_show", target: "ctx", seconds: 600 }] })).ok).toBe(true);
        expect(validateEkrDefinition(withRule({ when: "on_kill", do: [{ op: "arrow_show", target: "self", seconds: 10 }] })).ok).toBe(false);
        expect(validateEkrDefinition(withRule({ when: "on_kill", do: [{ op: "arrow_show", target: "ctx", seconds: 4.9 }] })).ok).toBe(false);
        expect(validateEkrDefinition(withRule({ when: "on_kill", do: [{ op: "arrow_show", target: "ctx", seconds: 600.1 }] })).ok).toBe(false);
    });

    it("arrow_mark: at は ctx/marker1..4/cno1..3・self は含まれない", () => {
        for (const at of ["ctx", "marker1", "marker4", "cno1", "cno3"]) {
            expect(validateEkrDefinition(withRule({ when: "on_kill", do: [{ op: "arrow_mark", at, seconds: 30 }] })).ok, at).toBe(true);
        }
        expect(validateEkrDefinition(withRule({ when: "on_kill", do: [{ op: "arrow_mark", at: "self", seconds: 30 }] })).ok).toBe(false);
        expect(validateEkrDefinition(withRule({ when: "on_kill", do: [{ op: "arrow_mark", at: "cno4", seconds: 30 }] })).ok).toBe(false);
        expect(validateEkrDefinition(withRule({ when: "on_kill", do: [{ op: "arrow_mark", at: "ctx", seconds: 3 }] })).ok).toBe(false);
    });

    it("arrow_hide: 引数なしで受理される", () => {
        const r = validateEkrDefinition(withRule({ when: "on_pet", do: [{ op: "arrow_hide" }] }));
        expect(r.ok).toBe(true);
        if (r.ok) expect(r.def.logic?.rules[0].do[0]).toEqual({ op: "arrow_hide" });
    });

    it("vote_weight_set: value は0..3の整数のみ (常時 op・when 不問)", () => {
        for (const value of [0, 1, 2, 3]) {
            expect(validateEkrDefinition(withRule({ when: "on_pet", do: [{ op: "vote_weight_set", value }] })).ok, String(value)).toBe(true);
        }
        expect(validateEkrDefinition(withRule({ when: "on_pet", do: [{ op: "vote_weight_set", value: -1 }] })).ok).toBe(false);
        expect(validateEkrDefinition(withRule({ when: "on_pet", do: [{ op: "vote_weight_set", value: 4 }] })).ok).toBe(false);
        expect(validateEkrDefinition(withRule({ when: "on_pet", do: [{ op: "vote_weight_set", value: 1.5 }] })).ok).toBe(false);
    });

    it("vote_block: target は self 不可の単数セレクタ", () => {
        for (const target of ["ctx", "saved1", "saved2", "nearest", "random"]) {
            expect(validateEkrDefinition(withRule({ when: "on_meeting_start", do: [{ op: "vote_block", target }] })).ok, target).toBe(true);
        }
        expect(validateEkrDefinition(withRule({ when: "on_meeting_start", do: [{ op: "vote_block", target: "self" }] })).ok).toBe(false);
    });

    it("vote_swap: 引数なしで受理される", () => {
        const r = validateEkrDefinition(withRule({ when: "on_meeting_pick", do: [{ op: "vote_swap" }] }));
        expect(r.ok).toBe(true);
        if (r.ok) expect(r.def.logic?.rules[0].do[0]).toEqual({ op: "vote_swap" });
    });

    it("exile: target は単数セレクタ全種を受理 (self も可)", () => {
        for (const target of ["self", "ctx", "saved1", "saved2", "nearest", "random"]) {
            expect(validateEkrDefinition(withRule({ when: "on_meeting_vote", do: [{ op: "exile", target }] })).ok, target).toBe(true);
        }
        expect(validateEkrDefinition(withRule({ when: "on_meeting_vote", do: [{ op: "exile", target: "all" }] })).ok).toBe(false);
    });

    it("vote_block/vote_swap/exile は会議系イベント以外の rule 配下でも構造的には受理される (配置ヒントはリンタ L18 に委ねる)", () => {
        expect(validateEkrDefinition(withRule({ when: "on_pet", do: [{ op: "vote_block", target: "ctx" }] })).ok).toBe(true);
        expect(validateEkrDefinition(withRule({ when: "on_pet", do: [{ op: "vote_swap" }] })).ok).toBe(true);
        expect(validateEkrDefinition(withRule({ when: "on_pet", do: [{ op: "exile", target: "self" }] })).ok).toBe(true);
    });

    it("Wave 2 op はすべて leaf ノード (depth 1, count 1) — 深さ8ちょうどの入れ子でも通る", () => {
        function nestedIf(depth: number, leaf: unknown): unknown {
            if (depth <= 1) return leaf;
            return { op: "if", cond: { e: "lit", v: 1 }, then: [nestedIf(depth - 1, leaf)] };
        }
        for (const leaf of [
            { op: "inspect", target: "ctx", depth: "role" },
            { op: "reveal", target: "ctx" },
            { op: "arrow_show", target: "ctx", seconds: 10 },
            { op: "arrow_mark", at: "ctx", seconds: 10 },
            { op: "arrow_hide" },
            { op: "vote_weight_set", value: 1 },
            { op: "vote_block", target: "ctx" },
            { op: "vote_swap" },
            { op: "exile", target: "self" },
        ]) {
            const depth8 = [nestedIf(8, leaf)];
            expect(validateEkrDefinition(withRule({ when: "on_kill", do: depth8 })).ok, JSON.stringify(leaf)).toBe(true);
            const depth9 = [nestedIf(9, leaf)];
            expect(validateEkrDefinition(withRule({ when: "on_kill", do: depth9 })).ok, JSON.stringify(leaf)).toBe(false);
        }
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

// ---------------------------------------------------------------------------
// Wave 3 (2026-08-14) — じょうたいと数値
// ---------------------------------------------------------------------------
describe("logic 検証 Wave 3 (on_var / on_alive_count / on_vent_exit)", () => {
    function baseLogic(overrides: Record<string, unknown> = {}): Record<string, unknown> {
        return {
            version: 1,
            variables: [{ name: "カウント", init: 0 }],
            rules: [{ when: "on_pet", do: [{ op: "stop" }] }],
            ...overrides,
        };
    }
    function withLogic(logic: unknown): Record<string, unknown> {
        return { ...baseValid(), logic };
    }
    function withRule(rule: Record<string, unknown>): Record<string, unknown> {
        return withLogic(baseLogic({ rules: [rule] }));
    }

    it("on_var: 宣言済み変数 + cmp(eq/le/ge) + 整数 value は受理される", () => {
        for (const cmp of ["eq", "le", "ge"]) {
            const r = validateEkrDefinition(withRule({ when: "on_var", var: "カウント", cmp, value: 3, do: [{ op: "stop" }] }));
            expect(r.ok, cmp).toBe(true);
            if (r.ok) expect(r.def.logic?.rules[0]).toEqual({ when: "on_var", var: "カウント", cmp, value: 3, do: [{ op: "stop" }] });
        }
    });

    it("on_var: 未定義の変数は文書 reject", () => {
        expect(validateEkrDefinition(withRule({ when: "on_var", var: "そんざいしない", cmp: "eq", value: 0, do: [{ op: "stop" }] })).ok).toBe(false);
    });

    // spec §1: 変数名は宣言側・参照側とも trim してから照合する。参照側の trim が抜けていると、
    // 手書き JSON の前後空白付き参照をローダー (C#) だけが受理する非対称になる。
    it("on_var: 前後に空白のある変数参照は trim してから照合される (C# 側と同一)", () => {
        const r = validateEkrDefinition(withRule({ when: "on_var", var: "  カウント  ", cmp: "eq", value: 0, do: [{ op: "stop" }] }));
        expect(r.ok).toBe(true);
        if (r.ok) expect(r.def.logic?.rules[0].var).toBe("カウント");
    });

    it("var_set/var_add/変数式の参照も同じく trim される", () => {
        const r = validateEkrDefinition(
            withRule({
                when: "on_pet",
                do: [
                    { op: "var_set", name: " カウント ", value: { e: "var", name: "カウント " } },
                    { op: "var_add", name: "カウント ", delta: { e: "lit", v: 1 } },
                ],
            }),
        );
        expect(r.ok).toBe(true);
        if (r.ok) expect(r.def.logic?.rules[0].do[0]).toEqual({ op: "var_set", name: "カウント", value: { e: "var", name: "カウント" } });
    });

    it("on_var: var/cmp/value のいずれかが欠けていたら reject", () => {
        expect(validateEkrDefinition(withRule({ when: "on_var", cmp: "eq", value: 0, do: [{ op: "stop" }] })).ok).toBe(false);
        expect(validateEkrDefinition(withRule({ when: "on_var", var: "カウント", value: 0, do: [{ op: "stop" }] })).ok).toBe(false);
        expect(validateEkrDefinition(withRule({ when: "on_var", var: "カウント", cmp: "eq", do: [{ op: "stop" }] })).ok).toBe(false);
    });

    it("on_var: cmp は eq/le/ge 以外は reject・value は整数以外 (小数/式オブジェクト) は reject", () => {
        expect(validateEkrDefinition(withRule({ when: "on_var", var: "カウント", cmp: "ne", value: 0, do: [{ op: "stop" }] })).ok).toBe(false);
        expect(validateEkrDefinition(withRule({ when: "on_var", var: "カウント", cmp: "eq", value: 1.5, do: [{ op: "stop" }] })).ok).toBe(false);
        expect(validateEkrDefinition(withRule({ when: "on_var", var: "カウント", cmp: "eq", value: { e: "lit", v: 1 }, do: [{ op: "stop" }] })).ok).toBe(false);
    });

    it("on_var 以外の rule に var が付いていたら reject (on_alive_count でも reject)", () => {
        expect(validateEkrDefinition(withRule({ when: "on_pet", var: "カウント", do: [{ op: "stop" }] })).ok).toBe(false);
        expect(validateEkrDefinition(withRule({ when: "on_alive_count", var: "カウント", cmp: "eq", value: 3, do: [{ op: "stop" }] })).ok).toBe(false);
    });

    it("on_alive_count: cmp + value(1..15) は受理・var は禁止", () => {
        const ok = validateEkrDefinition(withRule({ when: "on_alive_count", cmp: "le", value: 3, do: [{ op: "stop" }] }));
        expect(ok.ok).toBe(true);
        if (ok.ok) expect(ok.def.logic?.rules[0]).toEqual({ when: "on_alive_count", cmp: "le", value: 3, do: [{ op: "stop" }] });

        expect(validateEkrDefinition(withRule({ when: "on_alive_count", cmp: "le", value: ALIVE_COUNT_VALUE_MIN, do: [{ op: "stop" }] })).ok).toBe(true);
        expect(validateEkrDefinition(withRule({ when: "on_alive_count", cmp: "le", value: ALIVE_COUNT_VALUE_MAX, do: [{ op: "stop" }] })).ok).toBe(true);
        expect(validateEkrDefinition(withRule({ when: "on_alive_count", cmp: "le", value: ALIVE_COUNT_VALUE_MIN - 1, do: [{ op: "stop" }] })).ok).toBe(false);
        expect(validateEkrDefinition(withRule({ when: "on_alive_count", cmp: "le", value: ALIVE_COUNT_VALUE_MAX + 1, do: [{ op: "stop" }] })).ok).toBe(false);
    });

    it("on_alive_count: cmp/value のどちらかが欠けていたら reject", () => {
        expect(validateEkrDefinition(withRule({ when: "on_alive_count", value: 3, do: [{ op: "stop" }] })).ok).toBe(false);
        expect(validateEkrDefinition(withRule({ when: "on_alive_count", cmp: "le", do: [{ op: "stop" }] })).ok).toBe(false);
    });

    it("on_alive_count/on_var 以外の rule に cmp/value が付いていたら reject", () => {
        expect(validateEkrDefinition(withRule({ when: "on_pet", cmp: "eq", do: [{ op: "stop" }] })).ok).toBe(false);
        expect(validateEkrDefinition(withRule({ when: "on_pet", value: 1, do: [{ op: "stop" }] })).ok).toBe(false);
    });

    it("on_vent_exit: 追加フィールド無しで受理される", () => {
        const r = validateEkrDefinition(withRule({ when: "on_vent_exit", do: [{ op: "stop" }] }));
        expect(r.ok).toBe(true);
        if (r.ok) expect(r.def.logic?.rules[0]).toEqual({ when: "on_vent_exit", do: [{ op: "stop" }] });
    });

    it("17 種類すべてが LOGIC_WHEN_VALUES 経由で受理される (新3イベント込み)", () => {
        for (const when of ["on_var", "on_alive_count", "on_vent_exit"]) {
            const rule = when === "on_var"
                ? { when, var: "カウント", cmp: "eq", value: 0, do: [{ op: "stop" }] }
                : when === "on_alive_count"
                    ? { when, cmp: "eq", value: 1, do: [{ op: "stop" }] }
                    : { when, do: [{ op: "stop" }] };
            expect(validateEkrDefinition(withRule(rule)).ok, when).toBe(true);
        }
    });
});

describe("progress 検証 (Wave 3・契約 §3)", () => {
    it("progress キー省略は progress: undefined (現行挙動のまま)", () => {
        const r = validateEkrDefinition(baseValid());
        if (!r.ok) throw new Error(r.error);
        expect(r.def.progress).toBeUndefined();
    });

    it("text は必須。欠落/空文字/空白のみは reject", () => {
        expect(validateEkrDefinition({ ...baseValid(), progress: {} }).ok).toBe(false);
        expect(validateEkrDefinition({ ...baseValid(), progress: { text: "" } }).ok).toBe(false);
        expect(validateEkrDefinition({ ...baseValid(), progress: { text: "   " } }).ok).toBe(false);
    });

    it("text は trim される", () => {
        const r = validateEkrDefinition({ ...baseValid(), progress: { text: "  のこり1つ  " } });
        if (!r.ok) throw new Error(r.error);
        expect(r.def.progress).toEqual({ text: "のこり1つ" });
    });

    it(`text は ${PROGRESS_TEXT_MAX} 文字を超えるとクランプ (rejectではない)`, () => {
        const over = "あ".repeat(PROGRESS_TEXT_MAX + 5);
        const r = validateEkrDefinition({ ...baseValid(), progress: { text: over } });
        if (!r.ok) throw new Error(r.error);
        expect(r.def.progress?.text.length).toBe(PROGRESS_TEXT_MAX);
        expect(r.def.progress?.text).toBe("あ".repeat(PROGRESS_TEXT_MAX));
    });

    it("<> は全角 〈〉 へサニタイズされる", () => {
        const r = validateEkrDefinition({ ...baseValid(), progress: { text: "<強調>" } });
        if (!r.ok) throw new Error(r.error);
        expect(r.def.progress?.text).toBe("〈強調〉");
    });

    it("text が文字列でなければ reject", () => {
        expect(validateEkrDefinition({ ...baseValid(), progress: { text: 5 } }).ok).toBe(false);
        expect(validateEkrDefinition({ ...baseValid(), progress: "のこり1つ" }).ok).toBe(false);
    });
});

describe("hostOptions 検証 (Wave 3・契約 §4.1)", () => {
    function withVar(hostOptions: unknown): Record<string, unknown> {
        return {
            ...baseValid(),
            logic: { version: 1, variables: [{ name: "たま", init: 0 }], rules: [{ when: "on_pet", do: [{ op: "stop" }] }] },
            hostOptions,
        };
    }

    it("hostOptions キー省略/空配列は hostOptions: undefined (現行挙動のまま)", () => {
        const omitted = validateEkrDefinition(baseValid());
        if (!omitted.ok) throw new Error(omitted.error);
        expect(omitted.def.hostOptions).toBeUndefined();

        const empty = validateEkrDefinition({ ...baseValid(), hostOptions: [] });
        if (!empty.ok) throw new Error(empty.error);
        expect(empty.def.hostOptions).toBeUndefined();
    });

    it("固定6キーはすべて受理される (min/max無し)", () => {
        for (const key of ["shield.count", "doom.seconds", "speedMult", "voteWeight", "killCooldown", "vision"]) {
            const r = validateEkrDefinition({ ...baseValid(), hostOptions: [{ key, label: "らべる" }] });
            expect(r.ok, key).toBe(true);
        }
    });

    it("固定キーに min/max が付いていたら reject", () => {
        expect(validateEkrDefinition({ ...baseValid(), hostOptions: [{ key: "voteWeight", label: "ちから", min: 0, max: 3 }] }).ok).toBe(false);
    });

    it("var:<宣言済み変数> は min/max 必須で受理・min>=max は reject", () => {
        const ok = validateEkrDefinition(withVar([{ key: "var:たま", label: "たまの数", min: 0, max: 10 }]));
        expect(ok.ok).toBe(true);
        if (ok.ok) expect(ok.def.hostOptions).toEqual([{ key: "var:たま", label: "たまの数", min: 0, max: 10 }]);

        expect(validateEkrDefinition(withVar([{ key: "var:たま", label: "たまの数" }])).ok).toBe(false);
        expect(validateEkrDefinition(withVar([{ key: "var:たま", label: "たまの数", min: 10, max: 10 }])).ok).toBe(false);
        expect(validateEkrDefinition(withVar([{ key: "var:たま", label: "たまの数", min: 10, max: 5 }])).ok).toBe(false);
    });

    it("var:<未定義変数> は reject (logic 自体が無いときも reject)", () => {
        expect(validateEkrDefinition(withVar([{ key: "var:そんざいしない", label: "x", min: 0, max: 10 }])).ok).toBe(false);
        expect(validateEkrDefinition({ ...baseValid(), hostOptions: [{ key: "var:たま", label: "x", min: 0, max: 10 }] }).ok).toBe(false);
    });

    it("不正な key は reject", () => {
        expect(validateEkrDefinition({ ...baseValid(), hostOptions: [{ key: "not.a.key", label: "x" }] }).ok).toBe(false);
    });

    it("キーの重複は reject", () => {
        const dup = [{ key: "voteWeight", label: "A" }, { key: "voteWeight", label: "B" }];
        expect(validateEkrDefinition({ ...baseValid(), hostOptions: dup }).ok).toBe(false);
    });

    it("label は1..24字 (trim)。空/超過は reject・<> はサニタイズ", () => {
        expect(validateEkrDefinition({ ...baseValid(), hostOptions: [{ key: "voteWeight", label: "" }] }).ok).toBe(false);
        expect(validateEkrDefinition({ ...baseValid(), hostOptions: [{ key: "voteWeight", label: "   " }] }).ok).toBe(false);
        expect(validateEkrDefinition({ ...baseValid(), hostOptions: [{ key: "voteWeight", label: "あ".repeat(25) }] }).ok).toBe(false);

        const r = validateEkrDefinition({ ...baseValid(), hostOptions: [{ key: "voteWeight", label: "<強調>" }] });
        if (!r.ok) throw new Error(r.error);
        expect(r.def.hostOptions?.[0].label).toBe("〈強調〉");
    });

    it(`最大 ${HOST_OPTIONS_MAX} 件まで。超過は reject`, () => {
        expect(HOST_OPTIONS_MAX).toBe(8); // 上の定数が変わったらこのテストの前提を見直す合図
        // 重複キーを避けるため、固定キー6種 + var:2種 (2変数宣言) で8件ちょうど作る
        const fixedKeys = ["shield.count", "doom.seconds", "speedMult", "voteWeight", "killCooldown", "vision"];
        const eight = [
            ...fixedKeys.map((key, i) => ({ key, label: `A${i}` })),
            { key: "var:たま", label: "たまの数", min: 0, max: 10 },
            { key: "var:ぎん", label: "ぎんの数", min: 0, max: 10 },
        ];
        const threeVarsLogic = {
            version: 1,
            variables: [{ name: "たま", init: 0 }, { name: "ぎん", init: 0 }, { name: "どう", init: 0 }],
            rules: [{ when: "on_pet", do: [{ op: "stop" }] }],
        };
        expect(validateEkrDefinition({ ...baseValid(), logic: threeVarsLogic, hostOptions: eight }).ok).toBe(true);

        // 9件目 (var:どう) を足すと重複キー無しのまま純粋に件数だけ超過する
        const nine = [...eight, { key: "var:どう", label: "どうの数", min: 0, max: 10 }];
        expect(nine.length).toBe(HOST_OPTIONS_MAX + 1);
        expect(validateEkrDefinition({ ...baseValid(), logic: threeVarsLogic, hostOptions: nine }).ok).toBe(false);
    });
});

// ---------------------------------------------------------------------------
// Wave 4 (2026-08-25) — つなぐ
// ---------------------------------------------------------------------------
describe("logic 検証 Wave 4 (on_near / on_far / on_room_enter / on_room_exit / on_linked_death / link / unlink / recruit)", () => {
    function baseLogic(overrides: Record<string, unknown> = {}): Record<string, unknown> {
        return {
            version: 1,
            rules: [{ when: "on_pet", do: [{ op: "stop" }] }],
            ...overrides,
        };
    }
    function withLogic(logic: unknown): Record<string, unknown> {
        return { ...baseValid(), logic };
    }
    function withRule(rule: Record<string, unknown>): Record<string, unknown> {
        return withLogic(baseLogic({ rules: [rule] }));
    }

    it("on_near: radius (small/medium/large) 必須・who 省略可 (省略時は AST にキーが無い)", () => {
        for (const radius of ["small", "medium", "large"]) {
            const r = validateEkrDefinition(withRule({ when: "on_near", radius, do: [{ op: "stop" }] }));
            expect(r.ok, radius).toBe(true);
            if (r.ok) expect(r.def.logic?.rules[0]).toEqual({ when: "on_near", radius, do: [{ op: "stop" }] });
        }
        // radius 欠落・不正値は reject
        expect(validateEkrDefinition(withRule({ when: "on_near", do: [{ op: "stop" }] })).ok).toBe(false);
        expect(validateEkrDefinition(withRule({ when: "on_near", radius: "huge", do: [{ op: "stop" }] })).ok).toBe(false);
    });

    it("on_near: who は anyone/linked/saved1/saved2 を受理し、明示指定はそのまま保持される", () => {
        for (const who of ["anyone", "linked", "saved1", "saved2"]) {
            const r = validateEkrDefinition(withRule({ when: "on_near", radius: "small", who, do: [{ op: "stop" }] }));
            expect(r.ok, who).toBe(true);
            if (r.ok) expect(r.def.logic?.rules[0].who).toBe(who);
        }
        expect(validateEkrDefinition(withRule({ when: "on_near", radius: "small", who: "self", do: [{ op: "stop" }] })).ok).toBe(false);
        expect(validateEkrDefinition(withRule({ when: "on_near", radius: "small", who: "nearest", do: [{ op: "stop" }] })).ok).toBe(false);
    });

    it("on_far: radius/who とも必須・who は linked/saved1/saved2 のみ (anyone は reject)", () => {
        for (const who of ["linked", "saved1", "saved2"]) {
            const r = validateEkrDefinition(withRule({ when: "on_far", radius: "medium", who, do: [{ op: "stop" }] }));
            expect(r.ok, who).toBe(true);
            if (r.ok) expect(r.def.logic?.rules[0]).toEqual({ when: "on_far", radius: "medium", who, do: [{ op: "stop" }] });
        }
        expect(validateEkrDefinition(withRule({ when: "on_far", radius: "medium", who: "anyone", do: [{ op: "stop" }] })).ok).toBe(false);
        expect(validateEkrDefinition(withRule({ when: "on_far", radius: "medium", do: [{ op: "stop" }] })).ok).toBe(false);
        expect(validateEkrDefinition(withRule({ when: "on_far", who: "linked", do: [{ op: "stop" }] })).ok).toBe(false);
    });

    it("radius/who は on_near/on_far 以外のイベントに付いていたら reject (slot と同じ対称検査)", () => {
        for (const when of ["on_pet", "on_second", "on_room_enter", "on_linked_death"]) {
            expect(validateEkrDefinition(withRule({ when, radius: "small", do: [{ op: "stop" }] })).ok, `${when}+radius`).toBe(false);
            expect(validateEkrDefinition(withRule({ when, who: "linked", do: [{ op: "stop" }] })).ok, `${when}+who`).toBe(false);
        }
    });

    it("on_room_enter / on_room_exit: 追加フィールド無しで受理される", () => {
        for (const when of ["on_room_enter", "on_room_exit"]) {
            const r = validateEkrDefinition(withRule({ when, do: [{ op: "stop" }] }));
            expect(r.ok, when).toBe(true);
            if (r.ok) expect(r.def.logic?.rules[0]).toEqual({ when, do: [{ op: "stop" }] });
        }
    });

    it("on_linked_death: cause は on_death と同じ8バケットを任意受理 (省略 = 全死因)", () => {
        const omitted = validateEkrDefinition(withRule({ when: "on_linked_death", do: [{ op: "stop" }] }));
        expect(omitted.ok).toBe(true);
        if (omitted.ok) expect(omitted.def.logic?.rules[0]).toEqual({ when: "on_linked_death", do: [{ op: "stop" }] });

        for (const cause of ["kill", "vote", "guess", "bomb", "poison-curse", "environment", "suicide", "other"]) {
            const r = validateEkrDefinition(withRule({ when: "on_linked_death", cause, do: [{ op: "stop" }] }));
            expect(r.ok, cause).toBe(true);
            if (r.ok) expect(r.def.logic?.rules[0].cause).toBe(cause);
        }
        expect(validateEkrDefinition(withRule({ when: "on_linked_death", cause: "meteor", do: [{ op: "stop" }] })).ok).toBe(false);
    });

    it("cause は on_death でも従来どおり受理され、他イベント (on_near 含む) では reject のまま", () => {
        expect(validateEkrDefinition(withRule({ when: "on_death", cause: "kill", do: [{ op: "stop" }] })).ok).toBe(true);
        expect(validateEkrDefinition(withRule({ when: "on_pet", cause: "kill", do: [{ op: "stop" }] })).ok).toBe(false);
        expect(validateEkrDefinition(withRule({ when: "on_near", radius: "small", cause: "kill", do: [{ op: "stop" }] })).ok).toBe(false);
    });

    it("on_linked_death に kind / slot は付けられない (専用フィールドの対称検査)", () => {
        expect(validateEkrDefinition(withRule({ when: "on_linked_death", kind: "kill", do: [{ op: "stop" }] })).ok).toBe(false);
        expect(validateEkrDefinition(withRule({ when: "on_linked_death", slot: 1, do: [{ op: "stop" }] })).ok).toBe(false);
    });

    it("link: target は ctx/saved1/saved2/nearest/random のみ (self と linked は reject)", () => {
        for (const target of ["ctx", "saved1", "saved2", "nearest", "random"]) {
            expect(validateEkrDefinition(withRule({ when: "on_kill", do: [{ op: "link", target }] })).ok, target).toBe(true);
        }
        for (const target of ["self", "linked", "all", "room"]) {
            expect(validateEkrDefinition(withRule({ when: "on_kill", do: [{ op: "link", target }] })).ok, target).toBe(false);
        }
        // target 欠落も reject
        expect(validateEkrDefinition(withRule({ when: "on_kill", do: [{ op: "link" }] })).ok).toBe(false);
    });

    it("unlink: 引数なしで受理・余剰キーは黙って無視 (pull と同じ)", () => {
        const r = validateEkrDefinition(withRule({ when: "on_pet", do: [{ op: "unlink" }] }));
        expect(r.ok).toBe(true);
        if (r.ok) expect(r.def.logic?.rules[0].do[0]).toEqual({ op: "unlink" });

        const extra = validateEkrDefinition(withRule({ when: "on_pet", do: [{ op: "unlink", target: "ctx" }] }));
        expect(extra.ok).toBe(true);
        if (extra.ok) expect(extra.def.logic?.rules[0].do[0]).toEqual({ op: "unlink" });
    });

    it("recruit: target は ctx/linked/saved1/saved2/nearest/random (self は reject)", () => {
        for (const target of ["ctx", "linked", "saved1", "saved2", "nearest", "random"]) {
            expect(validateEkrDefinition(withRule({ when: "on_kill", do: [{ op: "recruit", target }] })).ok, target).toBe(true);
        }
        for (const target of ["self", "all", "room"]) {
            expect(validateEkrDefinition(withRule({ when: "on_kill", do: [{ op: "recruit", target }] })).ok, target).toBe(false);
        }
    });

    it("linked セレクタは saved1/2 と同じ受理箇所で使える (kill/teleport_other/inspect/reveal/arrow_show/remember/vote_block)", () => {
        const nodes: Record<string, unknown>[] = [
            { op: "kill", target: "linked" },
            { op: "teleport_other", target: "linked", to: "self" },
            { op: "inspect", target: "linked", depth: "team" },
            { op: "reveal", target: "linked" },
            { op: "arrow_show", target: "linked", seconds: 10 },
            { op: "remember", slot: 1, target: "linked" },
            { op: "vote_block", target: "linked" },
        ];
        for (const node of nodes) {
            expect(validateEkrDefinition(withRule({ when: "on_kill", do: [node] })).ok, String(node.op)).toBe(true);
        }
    });

    it("linked は空間セレクタ (teleport.to / marker_save.at / arrow_mark.at) には追加されていない", () => {
        expect(validateEkrDefinition(withRule({ when: "on_pet", do: [{ op: "teleport", to: "linked" }] })).ok).toBe(false);
        expect(validateEkrDefinition(withRule({ when: "on_pet", do: [{ op: "marker_save", slot: 1, at: "linked" }] })).ok).toBe(false);
        expect(validateEkrDefinition(withRule({ when: "on_pet", do: [{ op: "arrow_mark", at: "linked", seconds: 10 }] })).ok).toBe(false);
    });

    // ── Wave 5 (§1〜§3) ────────────────────────────────────────────────────────────────────
    it("recruit.slot: 任意・整数 1..18 のみ受理 (省略時はフィールドを持たない)", () => {
        const omitted = validateEkrDefinition(withRule({ when: "on_kill", do: [{ op: "recruit", target: "ctx" }] }));
        expect(omitted.ok).toBe(true);
        if (omitted.ok) expect("slot" in omitted.def.logic!.rules[0].do[0]).toBe(false);

        for (const slot of [1, 18, 3.0]) {
            expect(validateEkrDefinition(withRule({ when: "on_kill", do: [{ op: "recruit", target: "ctx", slot }] })).ok, String(slot)).toBe(true);
        }
        for (const slot of [0, 19, -1, 2.5, "3", null]) {
            expect(validateEkrDefinition(withRule({ when: "on_kill", do: [{ op: "recruit", target: "ctx", slot }] })).ok, String(slot)).toBe(false);
        }
    });

    it("effect_give: target/kind/seconds はすべて必須・未知 kind は reject", () => {
        expect(validateEkrDefinition(withRule({ when: "on_pet", do: [{ op: "effect_give", target: "ctx", kind: "haste", seconds: 10 }] })).ok).toBe(true);

        expect(validateEkrDefinition(withRule({ when: "on_pet", do: [{ op: "effect_give", kind: "haste", seconds: 10 }] })).ok).toBe(false);
        expect(validateEkrDefinition(withRule({ when: "on_pet", do: [{ op: "effect_give", target: "ctx", seconds: 10 }] })).ok).toBe(false);
        expect(validateEkrDefinition(withRule({ when: "on_pet", do: [{ op: "effect_give", target: "ctx", kind: "haste" }] })).ok).toBe(false);
        expect(validateEkrDefinition(withRule({ when: "on_pet", do: [{ op: "effect_give", target: "ctx", kind: "shield", seconds: 10 }] })).ok).toBe(false);
        expect(validateEkrDefinition(withRule({ when: "on_pet", do: [{ op: "effect_give", target: "all", kind: "haste", seconds: 10 }] })).ok).toBe(false);
    });

    it("effect_give: seconds の上限は kind 別 (freeze ≤10 / 他 ≤30)", () => {
        for (const kind of ["haste", "slow", "blind"]) {
            expect(validateEkrDefinition(withRule({ when: "on_pet", do: [{ op: "effect_give", target: "ctx", kind, seconds: 30 }] })).ok, kind).toBe(true);
            expect(validateEkrDefinition(withRule({ when: "on_pet", do: [{ op: "effect_give", target: "ctx", kind, seconds: 31 }] })).ok, kind).toBe(false);
        }
        expect(validateEkrDefinition(withRule({ when: "on_pet", do: [{ op: "effect_give", target: "ctx", kind: "freeze", seconds: 10 }] })).ok).toBe(true);
        expect(validateEkrDefinition(withRule({ when: "on_pet", do: [{ op: "effect_give", target: "ctx", kind: "freeze", seconds: 11 }] })).ok).toBe(false);
        expect(validateEkrDefinition(withRule({ when: "on_pet", do: [{ op: "effect_give", target: "ctx", kind: "freeze", seconds: 0 }] })).ok).toBe(false);
    });

    it("effect_give は leaf ノード (depth 1, count 1)・self も受理する", () => {
        const r = validateEkrDefinition(withRule({
            when: "on_pet",
            do: [{ op: "effect_give", target: "self", kind: "haste", seconds: 5 }],
        }));
        expect(r.ok).toBe(true);
        if (r.ok) expect(r.def.logic?.rules[0].do).toHaveLength(1);
    });

    it("link/unlink/recruit は leaf ノード (depth 1, count 1)", () => {
        const r = validateEkrDefinition(withRule({
            when: "on_kill",
            do: [
                { op: "link", target: "ctx" },
                { op: "unlink" },
                { op: "recruit", target: "nearest" },
            ],
        }));
        expect(r.ok).toBe(true);
        if (r.ok) expect(r.def.logic?.rules[0].do).toHaveLength(3);
    });

    it("22 種類すべてが LOGIC_WHEN_VALUES 経由で受理される (Wave 4 の新5イベント込み)", () => {
        for (const when of ["on_near", "on_far", "on_room_enter", "on_room_exit", "on_linked_death"]) {
            const rule = when === "on_near"
                ? { when, radius: "small", do: [{ op: "stop" }] }
                : when === "on_far"
                    ? { when, radius: "large", who: "saved1", do: [{ op: "stop" }] }
                    : { when, do: [{ op: "stop" }] };
            expect(validateEkrDefinition(withRule(rule)).ok, when).toBe(true);
        }
    });
});

describe("logic 検証 Wave 6 (cno_launch / on_sabotage / on_revive)", () => {
    function baseLogic(overrides: Record<string, unknown> = {}): Record<string, unknown> {
        return {
            version: 1,
            rules: [{ when: "on_pet", do: [{ op: "stop" }] }],
            ...overrides,
        };
    }
    function withLogic(logic: unknown): Record<string, unknown> {
        return { ...baseValid(), logic };
    }
    function withRule(rule: Record<string, unknown>): Record<string, unknown> {
        return withLogic(baseLogic({ rules: [rule] }));
    }

    it("cno_launch: slot(1..3)・dir 必須、speed は任意 (省略時は AST にキーが無い)", () => {
        for (const slot of [1, 2, 3]) {
            const r = validateEkrDefinition(withRule({ when: "on_pet", do: [{ op: "cno_launch", slot, dir: "move" }] }));
            expect(r.ok, String(slot)).toBe(true);
            if (r.ok) expect(r.def.logic?.rules[0].do[0]).toEqual({ op: "cno_launch", slot, dir: "move" });
        }
        expect(validateEkrDefinition(withRule({ when: "on_pet", do: [{ op: "cno_launch", slot: 0, dir: "move" }] })).ok).toBe(false);
        expect(validateEkrDefinition(withRule({ when: "on_pet", do: [{ op: "cno_launch", slot: 4, dir: "move" }] })).ok).toBe(false);
        expect(validateEkrDefinition(withRule({ when: "on_pet", do: [{ op: "cno_launch", dir: "move" }] })).ok).toBe(false);
    });

    it("cno_launch.dir: move/ctx/marker1..4 を受理し、未知値は reject", () => {
        for (const dir of ["move", "ctx", "marker1", "marker2", "marker3", "marker4"]) {
            const r = validateEkrDefinition(withRule({ when: "on_pet", do: [{ op: "cno_launch", slot: 1, dir }] }));
            expect(r.ok, dir).toBe(true);
        }
        expect(validateEkrDefinition(withRule({ when: "on_pet", do: [{ op: "cno_launch", slot: 1, dir: "self" }] })).ok).toBe(false);
        expect(validateEkrDefinition(withRule({ when: "on_pet", do: [{ op: "cno_launch", slot: 1 }] })).ok).toBe(false);
    });

    it("cno_launch.speed: slow/medium/fast を受理し、slow/fast は明示指定がそのまま保持される", () => {
        for (const speed of ["slow", "fast"]) {
            const r = validateEkrDefinition(withRule({ when: "on_pet", do: [{ op: "cno_launch", slot: 1, dir: "move", speed }] }));
            expect(r.ok, speed).toBe(true);
            if (r.ok) expect(r.def.logic?.rules[0].do[0]).toEqual({ op: "cno_launch", slot: 1, dir: "move", speed });
        }
        expect(validateEkrDefinition(withRule({ when: "on_pet", do: [{ op: "cno_launch", slot: 1, dir: "move", speed: "fastest" }] })).ok).toBe(false);
    });

    it("cno_launch.speed: 既定値 medium は省略時・明示時のどちらも AST でキーが畳み込まれる (契約 §1/§4)", () => {
        const omitted = validateEkrDefinition(withRule({ when: "on_pet", do: [{ op: "cno_launch", slot: 1, dir: "move" }] }));
        expect(omitted.ok).toBe(true);
        if (omitted.ok) expect(omitted.def.logic?.rules[0].do[0]).toEqual({ op: "cno_launch", slot: 1, dir: "move" });

        const explicitMedium = validateEkrDefinition(withRule({ when: "on_pet", do: [{ op: "cno_launch", slot: 1, dir: "move", speed: "medium" }] }));
        expect(explicitMedium.ok).toBe(true);
        if (explicitMedium.ok) {
            expect(explicitMedium.def.logic?.rules[0].do[0]).toEqual({ op: "cno_launch", slot: 1, dir: "move" });
            expect("speed" in explicitMedium.def.logic!.rules[0].do[0]).toBe(false);
        }

        // 不動点: 省略形と明示 medium 形は同じ正準 AST に収束する。
        expect(omitted.ok && explicitMedium.ok && JSON.stringify(omitted.def) === JSON.stringify(explicitMedium.def)).toBe(true);
    });

    it("cno_launch は leaf ノード (depth 1, count 1)", () => {
        const r = validateEkrDefinition(withRule({ when: "on_pet", do: [{ op: "cno_launch", slot: 1, dir: "move" }] }));
        expect(r.ok).toBe(true);
        if (r.ok) expect(r.def.logic?.rules[0].do).toHaveLength(1);
    });

    it("on_sabotage: rule フィルタなし (kind/cause/slot が付いていたら reject)", () => {
        expect(validateEkrDefinition(withRule({ when: "on_sabotage", do: [{ op: "stop" }] })).ok).toBe(true);
        expect(validateEkrDefinition(withRule({ when: "on_sabotage", kind: "kill", do: [{ op: "stop" }] })).ok).toBe(false);
        expect(validateEkrDefinition(withRule({ when: "on_sabotage", cause: "bomb", do: [{ op: "stop" }] })).ok).toBe(false);
        expect(validateEkrDefinition(withRule({ when: "on_sabotage", slot: 1, do: [{ op: "stop" }] })).ok).toBe(false);
    });

    it("on_revive: 追加フィールド無しで受理される (rule フィルタなし)", () => {
        const r = validateEkrDefinition(withRule({ when: "on_revive", do: [{ op: "stop" }] }));
        expect(r.ok).toBe(true);
        if (r.ok) expect(r.def.logic?.rules[0]).toEqual({ when: "on_revive", do: [{ op: "stop" }] });
        expect(validateEkrDefinition(withRule({ when: "on_revive", cause: "kill", do: [{ op: "stop" }] })).ok).toBe(false);
    });

    it("24 種類すべてが LOGIC_WHEN_VALUES 経由で受理される (Wave 6 の新2イベント込み)", () => {
        for (const when of ["on_sabotage", "on_revive"]) {
            expect(validateEkrDefinition(withRule({ when, do: [{ op: "stop" }] })).ok, when).toBe(true);
        }
    });
});

describe("logic 検証 Wave 7 (win / win_join)", () => {
    function withTeamLogic(team: string, rules: unknown[]): Record<string, unknown> {
        return { ...baseValid(), team, logic: { version: 1, rules } };
    }
    const WIN_TARGETS = ["self", "ctx", "linked", "saved1", "saved2", "nearest", "random"];

    it("win: neutral 文書で受理・target は任意 (省略時は AST にキーが無い — notify と同じ作法)", () => {
        const r = validateEkrDefinition(withTeamLogic("neutral", [{ when: "on_pet", do: [{ op: "win" }] }]));
        expect(r.ok).toBe(true);
        if (r.ok) {
            expect(r.def.logic?.rules[0].do[0]).toEqual({ op: "win" });
            expect("target" in r.def.logic!.rules[0].do[0]).toBe(false);
        }
    });

    it("win.target: self を含む単数セレクタ全種を受理・複数形/未知値は reject", () => {
        for (const target of WIN_TARGETS) {
            const r = validateEkrDefinition(withTeamLogic("neutral", [{ when: "on_kill", do: [{ op: "win", target }] }]));
            expect(r.ok, target).toBe(true);
            if (r.ok) expect(r.def.logic?.rules[0].do[0]).toEqual({ op: "win", target });
        }
        for (const bad of ["all", "room", "everyone"]) {
            expect(validateEkrDefinition(withTeamLogic("neutral", [{ when: "on_kill", do: [{ op: "win", target: bad }] }])).ok, bad).toBe(false);
        }
    });

    it("win: crewmate/impostor 文書は文書 reject (契約 §1 の neutral 限定)", () => {
        for (const team of ["crewmate", "impostor"]) {
            expect(validateEkrDefinition(withTeamLogic(team, [{ when: "on_pet", do: [{ op: "win" }] }])).ok, team).toBe(false);
        }
    });

    it("win: if の then/else に埋めても neutral 限定が検出される (再帰探索)", () => {
        const inThen = [{ op: "if", cond: { e: "lit", v: 1 }, then: [{ op: "win" }] }];
        expect(validateEkrDefinition(withTeamLogic("crewmate", [{ when: "on_pet", do: inThen }])).ok).toBe(false);
        const inElse = [{ op: "if", cond: { e: "lit", v: 1 }, then: [{ op: "stop" }], else: [{ op: "win" }] }];
        expect(validateEkrDefinition(withTeamLogic("crewmate", [{ when: "on_pet", do: inElse }])).ok).toBe(false);
        // 同じ形が neutral なら通る (ゲートが team だけを見ている証明)
        expect(validateEkrDefinition(withTeamLogic("neutral", [{ when: "on_pet", do: inThen }])).ok).toBe(true);
    });

    it("win_join: どの陣営の文書でも受理される (契約 §2 の全スロット可)", () => {
        for (const team of ["crewmate", "impostor", "neutral"]) {
            const r = validateEkrDefinition(withTeamLogic(team, [{ when: "on_game_start", do: [{ op: "win_join" }] }]));
            expect(r.ok, team).toBe(true);
            if (r.ok) expect(r.def.logic?.rules[0].do[0]).toEqual({ op: "win_join" });
        }
    });

    it("win_join.target: win と同じ受理集合", () => {
        for (const target of WIN_TARGETS) {
            const r = validateEkrDefinition(withTeamLogic("crewmate", [{ when: "on_kill", do: [{ op: "win_join", target }] }]));
            expect(r.ok, target).toBe(true);
        }
        expect(validateEkrDefinition(withTeamLogic("crewmate", [{ when: "on_kill", do: [{ op: "win_join", target: "all" }] }])).ok).toBe(false);
    });

    it("win/win_join は leaf ノード (depth 1, count 1)", () => {
        const r = validateEkrDefinition(withTeamLogic("neutral", [{ when: "on_pet", do: [{ op: "win" }, { op: "win_join" }] }]));
        expect(r.ok).toBe(true);
        if (r.ok) expect(r.def.logic?.rules[0].do).toHaveLength(2);
    });
});
