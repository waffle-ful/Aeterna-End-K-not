// 役職コード — EKR1. + base64url(deflate-raw)、
// パディング無し出力・有無両受理。mapcode.test.ts (仕様 §8) と同型だが役職コードは独立契約。

import { describe, expect, it } from "vitest";
import { b64urlDecode } from "../src/base64url";
import { ROLECODE_PREFIX, decodeRoleCode, encodeRoleCode } from "../src/rolecode";
import { SUPPORTED_TEAMS, defaultEkrDefinition, validateEkrDefinition } from "../src/roledef";

// このオブジェクトと下の FIXED_CODE は組で凍結されたフィクスチャ (レグレッション用の一方向ロック)。
// FIXED_CODE は「今日の TS 実装」で1回だけ生成したバイト列を literal として埋め込んだもの —
// encode() の出力を都度アサートするのではなく、decode() が過去の既知バイト列を今も正しく開けることを
// 保証する (encode→decode の往復だけのテストだと両方が一緒に変わる回帰は検知できないため)。
// 注意: inflateSync は zlib ヘッダ付きストリームも自動判別して読めてしまうため、この decode 方向の
// テストだけでは「encodeRoleCode が raw deflate ではなく zlib ラップに変わった」事故は検知できない
// (decode 側は変わらず読めてしまう)。そちらは下の「encode の出力は raw deflate」テストが担当する。
const FIXED_SAMPLE = {
    ekr: 1,
    requires: [] as string[],
    name: "見習いエンジニア",
    author: "テスト作者",
    color: "#3fa9f5",
    team: "crewmate",
    canKill: false,
    killCooldown: 25,
    canVent: true,
    visionMultiplier: 1.25,
    winCondition: "team",
};
const FIXED_CODE =
    "EKR1.HY6xCsIwFEX_5bkWQcXBrI7i6iIOoX3F0DTRNLWDCNqqCN2cnRzE3UUH8WOeYD_DtOO75zzuXQNGBljHA4PLVBhMgE1nHigeIzCobuXvc6bdnvI7FQ_Kn1SUlF_BA57auXavQMWR8hcVp-_7Um0PDvlaNqTVC_kg7LvEIo9d4BvMYm6xdrgaCSmBhVwm6EHkjqHWMtCZAtbtN8YElQVmTeqElUiEVuNUWrGQAuvR7VrLhBpqFQjrqKtomjZ_";

describe("役職コード (EKR1.)", () => {
    it("凍結フィクスチャ: 固定バイト列が今も正しく decode できる (フレーミング変更の回帰ガード)", () => {
        const back = JSON.parse(decodeRoleCode(FIXED_CODE));
        expect(back).toEqual(FIXED_SAMPLE);
    });

    it("EkrDefinition の既定値サンプルのエンコード→デコード ラウンドトリップが一致する", () => {
        const def = defaultEkrDefinition();
        def.name = "サンプル役職";
        const code = encodeRoleCode(JSON.stringify(def));
        expect(code.startsWith(ROLECODE_PREFIX)).toBe(true);
        // base64url: パディング '=' 無し、'+' '/' を含まない
        expect(code.slice(ROLECODE_PREFIX.length)).toMatch(/^[A-Za-z0-9_-]+$/);
        const back = JSON.parse(decodeRoleCode(code));
        expect(back).toEqual(def);
    });

    it("マルチバイト (日本語 + 絵文字) name もラウンドトリップする", () => {
        const def = defaultEkrDefinition();
        def.name = "黄色い忍者🥷";
        const back = JSON.parse(decodeRoleCode(encodeRoleCode(JSON.stringify(def))));
        expect(back).toEqual(def);
    });

    // R2 (§1 2026-08-11): role-maker.ts の陣営セレクタが持ちうる3値それぞれで
    // 「フォーム相当のオブジェクト → 検証 → コード化 → 復号 → 再検証」の往復が保たれることを確認する
    // (role-maker.ts 自体は DOM 依存で直接テストできないため、buildDefinitionFromForm が組み立てる
    // 形と同じ素の EkrDefinition 候補で代替する)。
    it.each(SUPPORTED_TEAMS)("team=%s のフォーム相当オブジェクトはコード化→読込の往復で team を保つ", (team) => {
        const candidate = { ...defaultEkrDefinition(), name: "陣営テスト役職", team };
        const validated = validateEkrDefinition(candidate);
        expect(validated.ok).toBe(true);
        if (!validated.ok) return;
        expect(validated.def.team).toBe(team);

        const code = encodeRoleCode(JSON.stringify(validated.def));
        const roundTripped = validateEkrDefinition(JSON.parse(decodeRoleCode(code)));
        expect(roundTripped.ok).toBe(true);
        if (!roundTripped.ok) return;
        expect(roundTripped.def).toEqual(validated.def);
        expect(roundTripped.def.team).toBe(team);
    });

    it("デコーダはパディング有無どちらも受理する", () => {
        const code = encodeRoleCode(JSON.stringify(defaultEkrDefinition()));
        const body = code.slice(ROLECODE_PREFIX.length);
        const padded = body + "=".repeat((4 - (body.length % 4)) % 4);
        expect(JSON.parse(decodeRoleCode(ROLECODE_PREFIX + padded))).toEqual(defaultEkrDefinition());
    });

    it("EKR1. 接頭辞が無いコードは拒否する (マップコード EKM1. も拒否される)", () => {
        expect(() => decodeRoleCode("EKM1.abcd")).toThrow();
        expect(() => decodeRoleCode("XKR1.abcd")).toThrow();
    });

    it("base64url として壊れたコードは拒否する", () => {
        expect(() => decodeRoleCode("EKR1.@@@@")).toThrow();
    });

    it("base64url の生プリミティブ自体は mapcode と共有している (b64urlDecode を直接使っても崩れない)", () => {
        expect(new TextDecoder().decode(b64urlDecode("aGVsbG8"))).toBe("hello");
    });

    it("encodeRoleCode の出力は raw deflate (zlib ヘッダ無し) — C# 側の DeflateStream(raw) がそのまま読める形", () => {
        const code = encodeRoleCode("{}");
        const payload = b64urlDecode(code.slice(ROLECODE_PREFIX.length));
        // zlib ヘッダ (RFC1950) は先頭2バイトが CMF/FLG で、圧縮法=deflate (下位4bit=8) かつ
        // (CMF<<8|FLG) が 31 の倍数になる。raw deflate はこの条件を満たさない —
        // deflateSync が zlibSync に変わってしまう事故 (decode 側の自動判別では検知できない) を検知する。
        const cmf = payload[0];
        const flg = payload[1];
        const looksLikeZlibHeader = (cmf & 0x0f) === 8 && ((cmf << 8) | flg) % 31 === 0;
        expect(looksLikeZlibHeader).toBe(false);
    });
});

// rolecode.ts (codec) は中身を一切解釈しないので、logic 付きの役職コードでも変更なく通るはず —
// ただし「本当に通しで壊れないか」「blockly (不透明フィールド) が生き残るか」は明示的に確認しておく
// (role-maker.ts 実装タスク item 7)。
describe("logic 込みのラウンドトリップ (R1)", () => {
    function definitionWithLogic(): Record<string, unknown> {
        const base = defaultEkrDefinition();
        return {
            ...base,
            name: "ロジック付きテスト役職",
            logic: {
                version: 1,
                variables: [{ name: "カウント", init: 0 }],
                rules: [
                    {
                        when: "on_second",
                        do: [
                            { op: "var_add", name: "カウント", delta: { e: "lit", v: 1 } },
                            {
                                op: "if",
                                cond: { e: "op", kind: "gt", a: { e: "var", name: "カウント" }, b: { e: "lit", v: 5 } },
                                then: [{ op: "notify", text: "がんばって！", seconds: 3 }],
                            },
                        ],
                    },
                ],
                // Blockly serialization は不透明データ (C# は一切読まない) — ネストしたオブジェクト/
                // 配列/数値/文字列が混在する現実的な形を模して、深い構造でも壊れず生き残るか確認する。
                blockly: {
                    blocks: {
                        languageVersion: 0,
                        blocks: [
                            {
                                type: "ekr_when_on_second",
                                id: "abc123",
                                x: 20,
                                y: 40,
                                next: { block: { type: "ekr_do_var_add", id: "def456", fields: { VAR: "カウント" } } },
                            },
                        ],
                    },
                },
            },
        };
    }

    it("def → encode → decode → parse → validateEkrDefinition が完全に一致する (blockly フィールド込み)", () => {
        const validated = validateEkrDefinition(definitionWithLogic());
        expect(validated.ok).toBe(true);
        if (!validated.ok) return;

        const code = encodeRoleCode(JSON.stringify(validated.def));
        expect(code.startsWith(ROLECODE_PREFIX)).toBe(true);

        const roundTripped = validateEkrDefinition(JSON.parse(decodeRoleCode(code)));
        expect(roundTripped.ok).toBe(true);
        if (!roundTripped.ok) return;

        expect(roundTripped.def).toEqual(validated.def);
        // blockly (不透明フィールド) が構造ごと生き残っていることを明示的に確認する
        expect(roundTripped.def.logic?.blockly).toEqual(validated.def.logic?.blockly);
    });

    it("logic 無しのコードは従来どおり def.logic が付かない (R0 互換)", () => {
        const def = defaultEkrDefinition();
        def.name = "ロジック無し役職";
        const back = validateEkrDefinition(JSON.parse(decodeRoleCode(encodeRoleCode(JSON.stringify(def)))));
        expect(back.ok).toBe(true);
        if (back.ok) expect(back.def.logic).toBeUndefined();
    });
});
