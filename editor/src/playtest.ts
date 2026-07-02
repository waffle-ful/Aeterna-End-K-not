// 「▶ ゲームで試す」: 現在のマップを Among Us (End K not) が読む EKMaps フォルダへ直接保存する。
// - Tauri (配布 exe): フル FS アクセスで <Documents>/EndKnot/EKMaps へ直書き。
// - ブラウザ (開発 / ホスト型 PWA): File System Access API でユーザーが一度選んだフォルダへ書き続ける。
//   ハンドルは IndexedDB に保存してセッションを跨いで再利用 (権限はその都度確認)。
//   未対応ブラウザ (Firefox/Safari) は呼び出し側でダウンロード fallback に倒す。
//
// モッド側はこの保存を L1 自動リロード (約2秒) で拾うので、保存するだけでゲームに反映される。

// Tauri 実行環境かどうか (配布 exe の中だけ true)
export function isTauri(): boolean {
    return typeof (window as { __TAURI_INTERNALS__?: unknown }).__TAURI_INTERNALS__ !== "undefined";
}

export type PlayResult =
    | { ok: true; where: string }
    | { ok: false; reason: "unsupported" | "cancelled" | "error"; message: string };

// ── IndexedDB に DirectoryHandle を保存/復元 (persist.ts とは別 DB) ─────────────
const FS_DB = "ekmap-fs";
const FS_STORE = "handles";
const HANDLE_KEY = "ekmaps-dir";

function openFsDb(): Promise<IDBDatabase> {
    return new Promise((resolve, reject) => {
        const req = indexedDB.open(FS_DB, 1);
        req.onupgradeneeded = () => {
            if (!req.result.objectStoreNames.contains(FS_STORE)) req.result.createObjectStore(FS_STORE);
        };
        req.onsuccess = () => resolve(req.result);
        req.onerror = () => reject(req.error);
    });
}

async function storeHandle(h: FileSystemHandle, key: string = HANDLE_KEY): Promise<void> {
    const db = await openFsDb();
    await new Promise<void>((resolve, reject) => {
        const tx = db.transaction(FS_STORE, "readwrite");
        tx.objectStore(FS_STORE).put(h, key);
        tx.oncomplete = () => resolve();
        tx.onerror = () => reject(tx.error);
    });
    db.close();
}

async function loadHandle<T extends FileSystemHandle>(key: string = HANDLE_KEY): Promise<T | null> {
    const db = await openFsDb();
    const h = await new Promise<T | null>((resolve, reject) => {
        const tx = db.transaction(FS_STORE, "readonly");
        const req = tx.objectStore(FS_STORE).get(key);
        req.onsuccess = () => resolve((req.result as T) ?? null);
        req.onerror = () => reject(req.error);
    });
    db.close();
    return h;
}

// FileSystemHandle の permission API は型定義に無いことがあるので緩く扱う
interface PermissionableHandle {
    queryPermission?(opts: { mode: "readwrite" }): Promise<PermissionState>;
    requestPermission?(opts: { mode: "readwrite" }): Promise<PermissionState>;
}

async function ensurePermission(h: FileSystemHandle): Promise<boolean> {
    const p = h as unknown as PermissionableHandle;
    const opts = { mode: "readwrite" as const };
    if (p.queryPermission && (await p.queryPermission(opts)) === "granted") return true;
    if (p.requestPermission) return (await p.requestPermission(opts)) === "granted";
    return true; // permission API 非対応ブラウザは書込時にエラーで弾かれる
}

// ── 保存本体 ────────────────────────────────────────────────────────────────
export async function saveForPlaytest(filename: string, text: string): Promise<PlayResult> {
    if (isTauri()) {
        return saveViaTauri(filename, text);
    }

    if (!("showDirectoryPicker" in window)) {
        return { ok: false, reason: "unsupported", message: "このブラウザはフォルダ直接保存に未対応です" };
    }

    // 1) 過去に確保したフォルダハンドルがあれば無言で書く
    try {
        const dir = await loadHandle<FileSystemDirectoryHandle>();
        if (dir && (await ensurePermission(dir))) {
            const fh = await dir.getFileHandle(filename, { create: true });
            const w = await fh.createWritable();
            await w.write(text);
            await w.close();
            return { ok: true, where: `${dir.name}/${filename}` };
        }
    } catch {
        /* フォルダ経路が死んでいたら次の経路へ */
    }

    // 2) このファイル名の保存先ファイルハンドルがあれば無言で上書き (ファイル方式の2回目以降)
    const fileKey = `ekmaps-file:${filename}`;
    try {
        const fh = await loadHandle<FileSystemFileHandle>(fileKey);
        if (fh && (await ensurePermission(fh))) {
            const w = await fh.createWritable();
            await w.write(text);
            await w.close();
            return { ok: true, where: fh.name };
        }
    } catch {
        /* 次の経路へ */
    }

    // 3) フォルダ選択を試す (成功すれば以後ずっと無言保存)。
    //    一部環境 (ドキュメントが OneDrive 配下等) では Chrome がフォルダ選択を
    //    「システムファイルがあるため開けません」でブロックする — その場合ユーザーは
    //    キャンセルするしかないので、4) のファイル保存ダイアログへフォールバックする。
    try {
        const picked = await (window as unknown as {
            showDirectoryPicker(o: { id?: string; mode: string; startIn?: string }): Promise<FileSystemDirectoryHandle>;
        }).showDirectoryPicker({ id: "ekmaps-docs", mode: "readwrite", startIn: "documents" });
        await ensurePermission(picked);
        // どこを選んでも <選択>/EndKnot/EKMaps に解決して作成 (モッドの読み込み先と一致)
        const dir = await resolveEkmapsDir(picked);
        await storeHandle(dir);
        const fh = await dir.getFileHandle(filename, { create: true });
        const w = await fh.createWritable();
        await w.write(text);
        await w.close();
        return { ok: true, where: `${dir.name}/${filename}` };
    } catch {
        /* キャンセル or ブロック → ファイル保存ダイアログへ */
    }

    // 4) ファイル保存ダイアログ (フォルダ制限の回避路)。
    //    初回だけ「ドキュメント > EndKnot > EKMaps」へ保存してもらい、ハンドルを記憶して
    //    以降は同じファイルに無言で上書きする。
    try {
        const fh = await (window as unknown as {
            showSaveFilePicker(o: {
                suggestedName?: string; startIn?: string; id?: string;
                types?: { description: string; accept: Record<string, string[]> }[];
            }): Promise<FileSystemFileHandle>;
        }).showSaveFilePicker({
            suggestedName: filename,
            startIn: "documents",
            id: "ekmaps-file",
            types: [{ description: "EKMap マップ", accept: { "application/json": [".json"] } }],
        });
        await ensurePermission(fh);
        await storeHandle(fh, fileKey);
        const w = await fh.createWritable();
        await w.write(text);
        await w.close();
        return { ok: true, where: fh.name };
    } catch (e) {
        const err = e as DOMException;
        if (err && (err.name === "AbortError" || err.name === "NotAllowedError")) {
            return { ok: false, reason: "cancelled", message: "保存がキャンセルされました" };
        }
        return { ok: false, reason: "error", message: (e as Error).message };
    }
}

// 選んだフォルダを「…/EndKnot/EKMaps」に解決する (無ければ作成)。
// ユーザーが Documents を選べば Documents/EndKnot/EKMaps、EndKnot を選べばその下の EKMaps、
// EKMaps 自体を選べばそのまま — どれを選んでもモッドの読み込み先に一致させる。
async function resolveEkmapsDir(picked: FileSystemDirectoryHandle): Promise<FileSystemDirectoryHandle> {
    if (picked.name === "EKMaps") return picked;
    const endknot = picked.name === "EndKnot"
        ? picked
        : await picked.getDirectoryHandle("EndKnot", { create: true });
    return endknot.getDirectoryHandle("EKMaps", { create: true });
}

// Tauri 経路: <Documents>/EndKnot/EKMaps/<filename> へ直書き。
// @tauri-apps/* は配布ビルドでのみ読み込む (ブラウザビルドは isTauri()=false でここに来ない)。
async function saveViaTauri(filename: string, text: string): Promise<PlayResult> {
    try {
        const path = await import("@tauri-apps/api/path");
        const fs = await import("@tauri-apps/plugin-fs");
        const docs = await path.documentDir();
        const dir = await path.join(docs, "EndKnot", "EKMaps");
        await fs.mkdir(dir, { recursive: true });
        const full = await path.join(dir, filename);
        await fs.writeTextFile(full, text);
        return { ok: true, where: full };
    } catch (e) {
        return { ok: false, reason: "error", message: (e as Error).message };
    }
}
