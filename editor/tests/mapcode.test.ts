// マップコード (仕様 §8): EKM1. + base64url(deflate-raw)、パディング無し出力・有無両受理

import { describe, expect, it } from "vitest";
import { b64urlDecode, b64urlEncode } from "../src/base64url";
import { MAPCODE_PREFIX, decodeMapCode, encodeMapCode } from "../src/mapcode";
import { golden, goldenClone } from "./golden";

describe("マップコード (仕様 §8)", () => {
    it("golden サンプルのエンコード→デコード ラウンドトリップが一致する", () => {
        const code = encodeMapCode(JSON.stringify(golden));
        expect(code.startsWith(MAPCODE_PREFIX)).toBe(true);
        // base64url: パディング '=' 無し、'+' '/' を含まない
        expect(code.slice(MAPCODE_PREFIX.length)).toMatch(/^[A-Za-z0-9_-]+$/);
        const back = JSON.parse(decodeMapCode(code));
        expect(back).toEqual(golden);
    });

    it("マルチバイト (日本語 + 絵文字) name もラウンドトリップする", () => {
        const m = goldenClone();
        m.name = "黄色い廊下の家🌀";
        const back = JSON.parse(decodeMapCode(encodeMapCode(JSON.stringify(m))));
        expect(back).toEqual(m);
    });

    it("デコーダはパディング有無どちらも受理する", () => {
        const code = encodeMapCode(JSON.stringify(golden));
        const body = code.slice(MAPCODE_PREFIX.length);
        const padded = body + "=".repeat((4 - (body.length % 4)) % 4);
        expect(JSON.parse(decodeMapCode(MAPCODE_PREFIX + padded))).toEqual(golden);
        // 単体: 'hello' (生 base64 で 'aGVsbG8=')
        expect(new TextDecoder().decode(b64urlDecode("aGVsbG8"))).toBe("hello");
        expect(new TextDecoder().decode(b64urlDecode("aGVsbG8="))).toBe("hello");
    });

    it("b64urlEncode は = + / を含まず、全長さ剰余でラウンドトリップする", () => {
        for (let n = 1; n <= 8; n++) {
            const bytes = new Uint8Array(n).map((_, i) => (i * 37 + n) & 255);
            const s = b64urlEncode(bytes);
            expect(s).toMatch(/^[A-Za-z0-9_-]*$/);
            expect(b64urlDecode(s)).toEqual(bytes);
        }
    });

    it("EKM1. 接頭辞が無いコードは拒否する", () => {
        expect(() => decodeMapCode("XKM1.abcd")).toThrow();
    });

    it("base64url として壊れたコードは拒否する", () => {
        expect(() => decodeMapCode("EKM1.@@@@")).toThrow();
    });
});
