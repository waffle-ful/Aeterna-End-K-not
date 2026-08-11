// 配布 exe (Tauri) でだけ使えるネイティブ機能。
// - ファイルを「開く / 名前を付けて保存」をOS標準ダイアログで行う
// - ゲームが読む EKMaps フォルダのマップを一覧して開く
//
// ブラウザ版では isTauri() が false なので、ここの関数は呼ばれない
// (呼び出し側が従来の <input type="file"> / ダウンロードに倒す)。
// @tauri-apps/* は動的 import — ブラウザ版のバンドルに載せないため (playtest.ts と同じ方針)。

import { isTauri } from "./playtest";

export { isTauri };

/** 保存/読込の結果。cancelled はユーザーがダイアログを閉じただけなので、エラー表示しない */
export type NativeResult<T> =
    | { ok: true; value: T }
    | { ok: false; reason: "cancelled" }
    | { ok: false; reason: "error"; message: string };

/** ゲームが読む EKMaps フォルダに置かれたマップ 1 件 */
export interface EkmapEntry {
    /** 表示名 (.ekmap.json を除いたもの) */
    name: string;
    /** 絶対パス */
    path: string;
    /** 更新時刻 (UNIX 秒)。Rust 側で新しい順に整列済み */
    modified: number;
}

const EKMAP_FILTER = { name: "EKM マップ", extensions: ["json"] };

/**
 * ダイアログの戻り値からパス文字列を取り出す。
 * バージョンによって文字列で返る場合と `{ path }` オブジェクトで返る場合があるため両対応する
 * (型定義は string でも実体が違うことがあり、型チェックでは検出できない)。
 */
function pickedPath(picked: unknown): string | null {
    if (typeof picked === "string") return picked;
    if (picked !== null && typeof picked === "object" && "path" in picked) {
        const p = (picked as { path?: unknown }).path;
        if (typeof p === "string") return p;
    }
    return null;
}

/**
 * 保存先を必ず `.ekmap.json` で終わらせる。
 * ダイアログのフィルタ拡張子は `json` なので、OS が `.json` だけを補って
 * `foo.json` や `foo.ekmap.json.json` を返すことがある。モッド側ローダーは
 * `*.ekmap.json` しか拾わないので、ここでずれると「保存できたのにゲームに出ない」
 * という無音の失敗になる。
 */
function ensureEkmapExt(path: string): string {
    if (path.endsWith(".ekmap.json")) return path;
    return `${path.replace(/(\.ekmap)?\.json$/i, "")}.ekmap.json`;
}

/**
 * OS標準の「開く」ダイアログでマップを選び、中身のテキストを返す。
 */
export async function openMapFileNative(): Promise<NativeResult<{ text: string; path: string }>> {
    try {
        const dialog = await import("@tauri-apps/plugin-dialog");
        const core = await import("@tauri-apps/api/core");
        const picked = await dialog.open({
            multiple: false,
            directory: false,
            filters: [EKMAP_FILTER],
        });
        const path = pickedPath(Array.isArray(picked) ? picked[0] : picked);
        if (path === null) return { ok: false, reason: "cancelled" };
        const text = await core.invoke<string>("read_text_file_abs", { path });
        return { ok: true, value: { text, path } };
    } catch (e) {
        return { ok: false, reason: "error", message: (e as Error).message ?? String(e) };
    }
}

/**
 * OS標準の「名前を付けて保存」ダイアログで保存先を選び、テキストを書き出す。
 * @param suggestedName 既定のファイル名 (拡張子込み)
 */
export async function saveMapFileNative(suggestedName: string, text: string): Promise<NativeResult<string>> {
    try {
        const dialog = await import("@tauri-apps/plugin-dialog");
        const core = await import("@tauri-apps/api/core");
        const picked = await dialog.save({
            defaultPath: suggestedName,
            filters: [EKMAP_FILTER],
        });
        const chosen = pickedPath(picked);
        if (chosen === null) return { ok: false, reason: "cancelled" };
        const path = ensureEkmapExt(chosen);
        await core.invoke("write_text_file_abs", { path, contents: text });
        return { ok: true, value: path };
    } catch (e) {
        return { ok: false, reason: "error", message: (e as Error).message ?? String(e) };
    }
}

/**
 * ゲームが読む &lt;Documents&gt;/EndKnot/EKMaps の中身を一覧する。
 * フォルダがまだ無い場合は空配列 (エラーにはしない)。
 */
export async function listEkmapsNative(): Promise<NativeResult<EkmapEntry[]>> {
    try {
        const path = await import("@tauri-apps/api/path");
        const core = await import("@tauri-apps/api/core");
        const docs = await path.documentDir();
        const dir = await path.join(docs, "EndKnot", "EKMaps");
        const list = await core.invoke<EkmapEntry[]>("list_ekmaps", { dir });
        return { ok: true, value: list };
    } catch (e) {
        return { ok: false, reason: "error", message: (e as Error).message ?? String(e) };
    }
}

/** EKMaps 一覧から選んだ 1 件を読む */
export async function readEkmapNative(path: string): Promise<NativeResult<string>> {
    try {
        const core = await import("@tauri-apps/api/core");
        const text = await core.invoke<string>("read_text_file_abs", { path });
        return { ok: true, value: text };
    } catch (e) {
        return { ok: false, reason: "error", message: (e as Error).message ?? String(e) };
    }
}
