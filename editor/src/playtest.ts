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

async function storeHandle(h: FileSystemDirectoryHandle): Promise<void> {
    const db = await openFsDb();
    await new Promise<void>((resolve, reject) => {
        const tx = db.transaction(FS_STORE, "readwrite");
        tx.objectStore(FS_STORE).put(h, HANDLE_KEY);
        tx.oncomplete = () => resolve();
        tx.onerror = () => reject(tx.error);
    });
    db.close();
}

async function loadHandle(): Promise<FileSystemDirectoryHandle | null> {
    const db = await openFsDb();
    const h = await new Promise<FileSystemDirectoryHandle | null>((resolve, reject) => {
        const tx = db.transaction(FS_STORE, "readonly");
        const req = tx.objectStore(FS_STORE).get(HANDLE_KEY);
        req.onsuccess = () => resolve((req.result as FileSystemDirectoryHandle) ?? null);
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

async function ensurePermission(h: FileSystemDirectoryHandle): Promise<boolean> {
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

    try {
        let dir = await loadHandle();
        if (!dir || !(await ensurePermission(dir))) {
            // 初回 or 権限失効: フォルダを選び直してもらう (Documents/EndKnot/EKMaps を選ぶ)
            dir = await (window as unknown as {
                showDirectoryPicker(o: { id: string; mode: string }): Promise<FileSystemDirectoryHandle>;
            }).showDirectoryPicker({ id: "ekmaps", mode: "readwrite" });
            await ensurePermission(dir);
            await storeHandle(dir);
        }

        const fh = await dir.getFileHandle(filename, { create: true });
        const w = await fh.createWritable();
        await w.write(text);
        await w.close();
        return { ok: true, where: `${dir.name}/${filename}` };
    } catch (e) {
        const err = e as DOMException;
        if (err && (err.name === "AbortError" || err.name === "NotAllowedError")) {
            return { ok: false, reason: "cancelled", message: "フォルダ選択がキャンセルされました" };
        }
        return { ok: false, reason: "error", message: (e as Error).message };
    }
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
