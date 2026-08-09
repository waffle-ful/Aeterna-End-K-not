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

// docs/ekr-logic-spec.md §1 の契約解決: 「JSON 型不一致は文書全体 reject」を C# (System.Text.Json の
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

describe("logic 検証 (docs/ekr-logic-spec.md §1〜§4・R1)", () => {
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
