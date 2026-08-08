// 役職コード (EKN R0 計画正典: docs/ekn-api-plan.md): "EKR1." + base64url( deflate-raw( UTF-8 JSON ) )
// mapcode.ts (仕様 §8 のマップコード) と同一の deflate-raw + base64url 契約だが、prefix が異なる
// 独立した契約 (役職コードは docs/ekmap-spec.md の管轄外)。C# 側は Modules/EkmCodec.cs の
// prefix 引数 (EkrManager.CodePrefix = "EKR1.") で同じ枠組みを再利用しているが、TS 側は
// mapcode.ts (凍結されたマップ契約ファイル) に触れないため、同型のロジックをこちらに独立して持つ。
// base64url のビット演算そのものは base64url.ts を共有する (base64url.ts 自体は特定の契約に属さない
// 純粋なエンコーディング原始関数のため)。

import { deflateSync, inflateSync } from "fflate";
import { b64urlDecode, b64urlEncode } from "./base64url";

export const ROLECODE_PREFIX = "EKR1.";

/** JSON テキスト → 役職コード。パディング '=' は出力しない */
export function encodeRoleCode(jsonText: string): string {
    const deflated = deflateSync(new TextEncoder().encode(jsonText));
    return ROLECODE_PREFIX + b64urlEncode(deflated);
}

/** 役職コード → JSON テキスト。失敗時は日本語メッセージの Error を投げる */
export function decodeRoleCode(code: string): string {
    const trimmed = code.trim();
    if (!trimmed.startsWith(ROLECODE_PREFIX)) {
        throw new Error(`役職コードは ${ROLECODE_PREFIX} で始まる必要があります`);
    }
    let bytes: Uint8Array;
    try {
        bytes = b64urlDecode(trimmed.slice(ROLECODE_PREFIX.length));
    } catch (e) {
        throw new Error(`役職コードを base64url として読めません: ${(e as Error).message}`);
    }
    let inflated: Uint8Array;
    try {
        inflated = inflateSync(bytes);
    } catch {
        throw new Error("役職コードの展開 (deflate) に失敗しました");
    }
    return new TextDecoder().decode(inflated);
}
