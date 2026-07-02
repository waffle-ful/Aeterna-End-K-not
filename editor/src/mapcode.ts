// マップコード (仕様 §8): EKM1. + base64url( deflate-raw( UTF-8 JSON ) )
// deflate は raw deflate (zlib ヘッダ無し)。fflate の deflateSync / inflateSync を使用。

import { deflateSync, inflateSync } from "fflate";
import { b64urlDecode, b64urlEncode } from "./base64url";

export const MAPCODE_PREFIX = "EKM1.";

/** JSON テキスト → マップコード。パディング '=' は出力しない */
export function encodeMapCode(jsonText: string): string {
    const deflated = deflateSync(new TextEncoder().encode(jsonText));
    return MAPCODE_PREFIX + b64urlEncode(deflated);
}

/** マップコード → JSON テキスト。失敗時は日本語メッセージの Error を投げる */
export function decodeMapCode(code: string): string {
    const trimmed = code.trim();
    if (!trimmed.startsWith(MAPCODE_PREFIX)) {
        throw new Error(`マップコードは ${MAPCODE_PREFIX} で始まる必要があります`);
    }
    let bytes: Uint8Array;
    try {
        bytes = b64urlDecode(trimmed.slice(MAPCODE_PREFIX.length));
    } catch (e) {
        throw new Error(`マップコードを base64url として読めません: ${(e as Error).message}`);
    }
    let inflated: Uint8Array;
    try {
        inflated = inflateSync(bytes);
    } catch {
        throw new Error("マップコードの展開 (deflate) に失敗しました");
    }
    return new TextDecoder().decode(inflated);
}
