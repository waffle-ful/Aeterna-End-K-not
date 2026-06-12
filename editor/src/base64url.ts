// RFC 4648 §5 base64url。エンコードはパディング無し、デコードはパディング有無どちらも受理 (仕様 §8)

const ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

const REVERSE: Int16Array = (() => {
    const t = new Int16Array(128).fill(-1);
    for (let i = 0; i < ALPHABET.length; i++) t[ALPHABET.charCodeAt(i)] = i;
    return t;
})();

export function b64urlEncode(bytes: Uint8Array): string {
    let out = "";
    const len = bytes.length;
    for (let i = 0; i + 2 < len; i += 3) {
        const n = (bytes[i] << 16) | (bytes[i + 1] << 8) | bytes[i + 2];
        out += ALPHABET[(n >> 18) & 63] + ALPHABET[(n >> 12) & 63] + ALPHABET[(n >> 6) & 63] + ALPHABET[n & 63];
    }
    const rem = len % 3;
    if (rem === 1) {
        const n = bytes[len - 1] << 16;
        out += ALPHABET[(n >> 18) & 63] + ALPHABET[(n >> 12) & 63];
    } else if (rem === 2) {
        const n = (bytes[len - 2] << 16) | (bytes[len - 1] << 8);
        out += ALPHABET[(n >> 18) & 63] + ALPHABET[(n >> 12) & 63] + ALPHABET[(n >> 6) & 63];
    }
    return out;
}

/** 不正文字があれば Error を投げる。'=' パディングと空白/改行は許容して除去 */
export function b64urlDecode(text: string): Uint8Array {
    const s = text.replace(/[\s=]+/g, "");
    if (s.length % 4 === 1) throw new Error("base64url の長さが不正です");
    const vals: number[] = [];
    for (let i = 0; i < s.length; i++) {
        const c = s.charCodeAt(i);
        const v = c < 128 ? REVERSE[c] : -1;
        if (v < 0) throw new Error(`base64url に不正な文字があります: '${s[i]}'`);
        vals.push(v);
    }
    const outLen = Math.floor((vals.length * 3) / 4);
    const out = new Uint8Array(outLen);
    let o = 0;
    for (let i = 0; i + 3 < vals.length; i += 4) {
        const n = (vals[i] << 18) | (vals[i + 1] << 12) | (vals[i + 2] << 6) | vals[i + 3];
        out[o++] = (n >> 16) & 255;
        out[o++] = (n >> 8) & 255;
        out[o++] = n & 255;
    }
    const rem = vals.length % 4;
    const base = vals.length - rem;
    if (rem === 2) {
        out[o++] = ((vals[base] << 18) | (vals[base + 1] << 12)) >> 16;
    } else if (rem === 3) {
        const n = (vals[base] << 18) | (vals[base + 1] << 12) | (vals[base + 2] << 6);
        out[o++] = (n >> 16) & 255;
        out[o++] = (n >> 8) & 255;
    }
    return out;
}
