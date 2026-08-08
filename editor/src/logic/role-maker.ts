// 役職メーカー (EKN R0: フォームのみ・ロジック無し) の UI モジュール。
// 計画正典: docs/ekn-api-plan.md §4 (R0)。契約/検証は ../roledef.ts、コード入出力は ../rolecode.ts。
//
// main.ts との接点は `initRoleMaker()` の起動呼び出し1回だけ (マップ doc とは無関係な独立機能なので
// 「doc 読み書き + dirty 通知」の接点すら持たない — main.ts を太らせない方針の最も狭い形)。
// ダイアログの開閉トリガー (#btn-role-maker) の配線もこのモジュール内で自己完結させる。
//
// 通知は #toast / dlg-msg を再利用しない: どちらも dlg-role-maker の ::backdrop の下に描画されて
// 見えなくなる (ネイティブ <dialog> の backdrop は同時に開いている他要素より手前に乗る) ため、
// ダイアログ内のインライン状態表示 (#rm-status) で完結させる。

import {
    DEFAULT_WIN_CONDITION,
    type EkrDefinition,
    SUPPORTED_TEAM,
    defaultEkrDefinition,
    normalizeColor,
    normalizeKillCooldown,
    normalizeVisionMultiplier,
    validateEkrDefinition,
} from "../roledef";
import { decodeRoleCode, encodeRoleCode } from "../rolecode";

const STORAGE_KEY = "ekm.roleMaker";

/** localStorage に保存する「編集中フォームの生の値」。R0 で固定のフィールド (ekr/requires/team/winCondition) は含めない */
interface FormState {
    name: string;
    author: string;
    color: string;
    canKill: boolean;
    killCooldown: number;
    canVent: boolean;
    visionMultiplier: number;
}

function defaultFormState(): FormState {
    const d = defaultEkrDefinition();
    return {
        name: d.name,
        author: d.author,
        color: d.color,
        canKill: d.canKill,
        killCooldown: d.killCooldown,
        canVent: d.canVent,
        visionMultiplier: d.visionMultiplier,
    };
}

function $<T extends HTMLElement>(id: string): T {
    return document.getElementById(id) as T;
}

function setStatus(msg: string, isError: boolean): void {
    const el = $("rm-status");
    el.textContent = msg;
    el.hidden = msg.length === 0;
    el.classList.toggle("rm-status-error", isError);
    el.classList.toggle("rm-status-ok", !isError && msg.length > 0);
}

function refreshKillCdVisibility(): void {
    $("rm-kill-cd-row").hidden = !$<HTMLInputElement>("rm-can-kill").checked;
}

/** フォーム入力欄 → 生の値 (未検証・未クランプ)。name/author はユーザーの入力途中の値をそのまま読む */
function readForm(): FormState {
    return {
        name: $<HTMLInputElement>("rm-name").value,
        author: $<HTMLInputElement>("rm-author").value,
        color: $<HTMLInputElement>("rm-color").value,
        canKill: $<HTMLInputElement>("rm-can-kill").checked,
        killCooldown: Number($<HTMLInputElement>("rm-kill-cd").value),
        canVent: $<HTMLInputElement>("rm-can-vent").checked,
        visionMultiplier: Number($<HTMLInputElement>("rm-vision").value),
    };
}

/**
 * フォーム入力欄 ← 状態を反映。color/killCooldown/visionMultiplier は normalize* を必ず経由する
 * (input type="color" に不正な文字列を代入すると黙って #000000 にリセットされる仕様があるため、
 * 呼び出し元の由来 [既定値/localStorage/読込コード] を問わずここで安全な値であることを保証する)。
 */
function writeForm(s: FormState): void {
    $<HTMLInputElement>("rm-name").value = s.name;
    $<HTMLInputElement>("rm-author").value = s.author;
    $<HTMLInputElement>("rm-color").value = normalizeColor(s.color);
    $<HTMLInputElement>("rm-can-kill").checked = s.canKill;
    $<HTMLInputElement>("rm-kill-cd").value = String(normalizeKillCooldown(s.killCooldown));
    $<HTMLInputElement>("rm-can-vent").checked = s.canVent;
    $<HTMLInputElement>("rm-vision").value = String(normalizeVisionMultiplier(s.visionMultiplier));
    refreshKillCdVisibility();
}

function saveFormToStorage(): void {
    try {
        localStorage.setItem(STORAGE_KEY, JSON.stringify(readForm()));
    } catch {
        // QuotaExceeded 等が起きても機能は継続する (保存できないだけ)
    }
}

/**
 * 保存されたドラフトは「まだ名前を入れていない途中経過」を許す必要があるため、
 * validateEkrDefinition (name 必須などのハード拒否を含む) には通さず、フィールドごとに寛容に復元する。
 * 数値/色だけは normalize* を通し、壊れたデータでもフォーム自体は必ず開ける状態にする。
 */
function loadFormFromStorage(): FormState {
    const d = defaultFormState();
    try {
        const raw = localStorage.getItem(STORAGE_KEY);
        if (!raw) return d;
        const parsed = JSON.parse(raw) as Partial<Record<keyof FormState, unknown>>;
        return {
            name: typeof parsed.name === "string" ? parsed.name : d.name,
            author: typeof parsed.author === "string" ? parsed.author : d.author,
            color: normalizeColor(parsed.color),
            canKill: typeof parsed.canKill === "boolean" ? parsed.canKill : d.canKill,
            killCooldown: normalizeKillCooldown(parsed.killCooldown),
            canVent: typeof parsed.canVent === "boolean" ? parsed.canVent : d.canVent,
            visionMultiplier: normalizeVisionMultiplier(parsed.visionMultiplier),
        };
    } catch {
        return d;
    }
}

/** フォーム → EkrDefinition (常に requires:[] / team:crewmate / winCondition:team を強制する) */
function buildDefinitionFromForm(): EkrDefinition | null {
    const raw = readForm();
    const candidate = {
        ekr: 1,
        requires: [] as string[],
        name: raw.name,
        author: raw.author,
        color: raw.color,
        team: SUPPORTED_TEAM,
        canKill: raw.canKill,
        killCooldown: raw.killCooldown,
        canVent: raw.canVent,
        visionMultiplier: raw.visionMultiplier,
        winCondition: DEFAULT_WIN_CONDITION,
    };
    const r = validateEkrDefinition(candidate);
    if (!r.ok) {
        setStatus(r.error, true);
        return null;
    }
    return r.def;
}

async function copyCode(): Promise<void> {
    const def = buildDefinitionFromForm();
    if (!def) return;
    const code = encodeRoleCode(JSON.stringify(def));
    const manualTa = $<HTMLTextAreaElement>("rm-manual-copy");
    try {
        await navigator.clipboard.writeText(code);
        manualTa.hidden = true;
        setStatus("コードをコピーしました！ゲームのチャットで /role import してください", false);
    } catch {
        // クリップボード不可 → 手動コピー用に表示 (openCodeDialog の「手動コピー」パターンに倣う)
        manualTa.value = code;
        manualTa.hidden = false;
        manualTa.select();
        setStatus("コピーできませんでした。下のコードを選択してコピーしてください (Ctrl+C)", true);
    }
}

function loadCode(): void {
    const codeText = $<HTMLTextAreaElement>("rm-load-text").value;
    let jsonText: string;
    try {
        jsonText = decodeRoleCode(codeText);
    } catch (e) {
        setStatus((e as Error).message, true);
        return;
    }

    let parsed: unknown;
    try {
        parsed = JSON.parse(jsonText);
    } catch {
        setStatus("役職コードの中身が JSON として読めません", true);
        return;
    }

    const r = validateEkrDefinition(parsed);
    if (!r.ok) {
        setStatus(r.error, true);
        return;
    }

    writeForm({
        name: r.def.name,
        author: r.def.author,
        color: r.def.color,
        canKill: r.def.canKill,
        killCooldown: r.def.killCooldown,
        canVent: r.def.canVent,
        visionMultiplier: r.def.visionMultiplier,
    });
    saveFormToStorage();
    $<HTMLTextAreaElement>("rm-load-text").value = "";
    setStatus("コードを読み込みました (フォームに反映しました)", false);
}

let wired = false;

function wire(): void {
    if (wired) return;
    wired = true;

    writeForm(loadFormFromStorage());

    $<HTMLInputElement>("rm-name").addEventListener("input", saveFormToStorage);
    $<HTMLInputElement>("rm-author").addEventListener("input", saveFormToStorage);
    $<HTMLInputElement>("rm-color").addEventListener("input", saveFormToStorage);
    $<HTMLInputElement>("rm-can-vent").addEventListener("change", saveFormToStorage);

    $<HTMLInputElement>("rm-can-kill").addEventListener("change", () => {
        refreshKillCdVisibility();
        saveFormToStorage();
    });

    $<HTMLInputElement>("rm-kill-cd").addEventListener("change", () => {
        const el = $<HTMLInputElement>("rm-kill-cd");
        const raw = el.value.trim();
        el.value = String(normalizeKillCooldown(raw === "" ? NaN : Number(raw)));
        saveFormToStorage();
    });

    $<HTMLInputElement>("rm-vision").addEventListener("change", () => {
        const el = $<HTMLInputElement>("rm-vision");
        const raw = el.value.trim();
        el.value = String(normalizeVisionMultiplier(raw === "" ? NaN : Number(raw)));
        saveFormToStorage();
    });

    $("rm-copy").addEventListener("click", () => void copyCode());
    $("rm-load-btn").addEventListener("click", () => loadCode());
    $("rm-close").addEventListener("click", () => $<HTMLDialogElement>("dlg-role-maker").close());

    $("btn-role-maker").addEventListener("click", () => {
        setStatus("", false);
        $<HTMLTextAreaElement>("rm-manual-copy").hidden = true;
        $<HTMLDialogElement>("dlg-role-maker").showModal();
    });
}

/** main.ts からの唯一の呼び出し口。何度呼んでも二重配線しない。 */
export function initRoleMaker(): void {
    wire();
}
