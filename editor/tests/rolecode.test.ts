// 役職コード (計画正典: docs/ekn-api-plan.md) — EKR1. + base64url(deflate-raw)、
// パディング無し出力・有無両受理。mapcode.test.ts (仕様 §8) と同型だが役職コードは独立契約。

import { describe, expect, it } from "vitest";
import { b64urlDecode } from "../src/base64url";
import { ROLECODE_PREFIX, decodeRoleCode, encodeRoleCode } from "../src/rolecode";
import { defaultEkrDefinition } from "../src/roledef";

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

describe("役職コード (EKR1., 計画正典: docs/ekn-api-plan.md)", () => {
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
