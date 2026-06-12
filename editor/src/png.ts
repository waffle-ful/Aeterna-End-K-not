// PNG data URI のヘッダ解析 (仕様 §14 の tileset.image 検証用)
// DOM 非依存 (vitest の node 環境でも動く)。寸法は IHDR チャンクから直接読む。

export const PNG_DATA_URI_PREFIX = "data:image/png;base64,";

export type PngInfo =
    | { ok: true; width: number; height: number; byteLength: number }
    | { ok: false; error: string };

const PNG_SIG = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

/** data:image/png;base64,… を解析して寸法とデコード後バイト長を返す */
export function parsePngDataUri(uri: string): PngInfo {
    if (typeof uri !== "string" || !uri.startsWith(PNG_DATA_URI_PREFIX)) {
        return { ok: false, error: `image は ${PNG_DATA_URI_PREFIX} で始まる data URI が必要です` };
    }
    const body = uri.slice(PNG_DATA_URI_PREFIX.length).replace(/\s+/g, "");
    if (body.length === 0) return { ok: false, error: "image の base64 本体が空です" };
    if (!/^[A-Za-z0-9+/]*={0,2}$/.test(body)) return { ok: false, error: "image の base64 が不正です" };

    // デコード後バイト長 (パディング考慮)。全デコードせず先頭だけ読む
    const raw = body.replace(/=+$/, "");
    const byteLength = Math.floor((raw.length * 3) / 4);
    if (byteLength < 33) return { ok: false, error: "PNG として短すぎます" };

    // 先頭 33 バイト (シグネチャ 8 + チャンク長 4 + "IHDR" 4 + width 4 + height 4 + …) を含む 48 文字をデコード
    let head: Uint8Array;
    try {
        const chunk = raw.slice(0, 48);
        const padded = chunk + "=".repeat((4 - (chunk.length % 4)) % 4);
        const bin = atob(padded);
        head = new Uint8Array(bin.length);
        for (let i = 0; i < bin.length; i++) head[i] = bin.charCodeAt(i);
    } catch {
        return { ok: false, error: "image の base64 をデコードできません" };
    }

    for (let i = 0; i < 8; i++) {
        if (head[i] !== PNG_SIG[i]) return { ok: false, error: "PNG ではありません (シグネチャ不一致)" };
    }
    if (head[12] !== 0x49 || head[13] !== 0x48 || head[14] !== 0x44 || head[15] !== 0x52) {
        return { ok: false, error: "PNG の IHDR チャンクが見つかりません" };
    }
    const width = (head[16] << 24) | (head[17] << 16) | (head[18] << 8) | head[19];
    const height = (head[20] << 24) | (head[21] << 16) | (head[22] << 8) | head[23];
    if (width <= 0 || height <= 0) return { ok: false, error: "PNG の寸法が不正です" };
    return { ok: true, width, height, byteLength };
}
