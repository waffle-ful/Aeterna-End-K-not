// @vitest-environment jsdom
//
// Wave 9 (契約 §1/§1.2) のフォーム欄 (basis / abilityCooldown) を、role-maker.ts の実際の
// DOM 配線を通して検証する。dlg-role-maker のマークアップは index.html から実際に切り出して
// 使う (role-maker-wave3.test.ts / role-maker-disguise.test.ts と同じ理由 — id ドリフト検出)。
//
// 契約 §4.3 の 🔴 (トップレベル新キーはフォーム層5経路すべて配線すること — R2 の
// passives.disguise silent drop 再発防止) をこのテストで確認する: フォーム入力 → コピー(定義構築) →
// コード → 読込 → フォームの往復で、basis/abilityCooldown が欠落しないこと。

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import INDEX_HTML from "../index.html?raw";
import { decodeRoleCode, encodeRoleCode } from "../src/rolecode";

const DIALOG_HTML = (() => {
    const marker = '<dialog id="dlg-role-maker">';
    const start = INDEX_HTML.indexOf(marker);
    if (start < 0) throw new Error("dlg-role-maker が index.html に見つかりません (テストのフィクスチャが壊れています)");
    const end = INDEX_HTML.indexOf("</dialog>", start) + "</dialog>".length;
    return INDEX_HTML.slice(start, end);
})();

async function freshRoleMaker() {
    document.body.innerHTML = DIALOG_HTML;
    vi.resetModules();
    return await import("../src/logic/role-maker");
}

function $(id: string): HTMLElement {
    const el = document.getElementById(id);
    if (!el) throw new Error(`#${id} が見つかりません`);
    return el;
}

function fireChange(el: Element): void {
    el.dispatchEvent(new Event("change", { bubbles: true }));
}

function setValidName(): void {
    ($("rm-name") as HTMLInputElement).value = "テスト役職";
}

async function copyAndReadCode(): Promise<string> {
    ($("rm-copy") as HTMLButtonElement).click();
    await Promise.resolve();
    await Promise.resolve();
    const code = ($("rm-manual-copy") as HTMLTextAreaElement).value;
    expect(code.length).toBeGreaterThan(0);
    return code;
}

describe("役職メーカー: はつどうのしかた + とくいわざの まちじかん (Wave 9 契約 §1/§1.2)", () => {
    beforeEach(() => {
        localStorage.clear();
    });
    afterEach(() => {
        localStorage.clear();
    });

    it("既定 (basis: pet) では abilityCooldown 欄が隠れており、コードには pet/既定30 が出力される", async () => {
        const rm = await freshRoleMaker();
        rm.initRoleMaker();
        setValidName();

        expect(($("rm-basis") as HTMLSelectElement).value).toBe("pet");
        expect($("rm-ability-cd-row").hidden).toBe(true);

        const code = await copyAndReadCode();
        const def = JSON.parse(decodeRoleCode(code)) as { basis?: string; abilityCooldown?: number };
        expect(def.basis).toBe("pet");
        expect(def.abilityCooldown).toBe(30);
    });

    it("「だれかを えらぶ」に切り替えると まちじかん欄が現れ、フォーム→定義→コード→読込の往復で保たれる", async () => {
        const rm = await freshRoleMaker();
        rm.initRoleMaker();
        setValidName();

        const basisSelect = $("rm-basis") as HTMLSelectElement;
        basisSelect.value = "shapeshift";
        fireChange(basisSelect);
        expect($("rm-ability-cd-row").hidden).toBe(false);

        const abilityCdInput = $("rm-ability-cd") as HTMLInputElement;
        abilityCdInput.value = "45";
        fireChange(abilityCdInput);

        const code = await copyAndReadCode();
        const def = JSON.parse(decodeRoleCode(code)) as { basis?: string; abilityCooldown?: number };
        expect(def.basis).toBe("shapeshift");
        expect(def.abilityCooldown).toBe(45);

        ($("rm-load-text") as HTMLTextAreaElement).value = code;
        ($("rm-load-btn") as HTMLButtonElement).click();
        expect(($("rm-basis") as HTMLSelectElement).value).toBe("shapeshift");
        expect($("rm-ability-cd-row").hidden).toBe(false);
        expect(($("rm-ability-cd") as HTMLInputElement).value).toBe("45");
    });

    it("まちじかん欄の入力値は 5〜180 にクランプされて書き戻される (契約の reject とは別の、フォーム層の安全弁)", async () => {
        const rm = await freshRoleMaker();
        rm.initRoleMaker();
        setValidName();

        const basisSelect = $("rm-basis") as HTMLSelectElement;
        basisSelect.value = "shapeshift";
        fireChange(basisSelect);

        const abilityCdInput = $("rm-ability-cd") as HTMLInputElement;
        abilityCdInput.value = "0";
        fireChange(abilityCdInput);
        expect(abilityCdInput.value).toBe("5");

        abilityCdInput.value = "9999";
        fireChange(abilityCdInput);
        expect(abilityCdInput.value).toBe("180");
    });
});

// passives.countsAs (契約 §3) は UI 欄を持たない (契約: ホスト露出なし・見本で足りる) が、
// 読み込んだ値は「フォーム欄が無いから」という理由だけで消えてはいけない (§4.3 のトップレベル
// 新キー5経路配線の不変条件 — passives.disguise の silent drop 再発防止と同じ理由)。
describe("役職メーカー: passives.countsAs のパススルー (Wave 9 契約 §3・UI 欄なしの値の保持)", () => {
    beforeEach(() => {
        localStorage.clear();
    });
    afterEach(() => {
        localStorage.clear();
    });

    it("countsAs 付きのコードを読み込んで何も編集せずに再コピーしても countsAs が保たれる", async () => {
        const rm = await freshRoleMaker();
        rm.initRoleMaker();

        const seedDef = {
            ekr: 1, requires: [], name: "ふたりぶんテスト", author: "", color: "#8f8f8f", team: "impostor",
            canKill: false, killCooldown: 25, canVent: false, visionMultiplier: 1, winCondition: "team",
            passives: { countsAs: 2 },
        };
        ($("rm-load-text") as HTMLTextAreaElement).value = encodeRoleCode(JSON.stringify(seedDef));
        ($("rm-load-btn") as HTMLButtonElement).click();
        expect(($("rm-name") as HTMLInputElement).value).toBe("ふたりぶんテスト");

        const code = await copyAndReadCode();
        const def = JSON.parse(decodeRoleCode(code)) as { passives?: { countsAs?: number } };
        expect(def.passives).toEqual({ countsAs: 2 });
    });

    it("countsAs 付きの下書き (localStorage) から再初期化しても countsAs が保たれる", async () => {
        const rm = await freshRoleMaker();
        rm.initRoleMaker();
        setValidName();

        const seedDef = {
            ekr: 1, requires: [], name: "ふたりぶんテスト", author: "", color: "#8f8f8f", team: "impostor",
            canKill: false, killCooldown: 25, canVent: false, visionMultiplier: 1, winCondition: "team",
            passives: { countsAs: 2 },
        };
        ($("rm-load-text") as HTMLTextAreaElement).value = encodeRoleCode(JSON.stringify(seedDef));
        ($("rm-load-btn") as HTMLButtonElement).click();

        // wire() をもう一度通す (ページ再読込相当) — localStorage 下書きから countsAs が
        // passivesPassthrough へ引き継がれることを確認する。
        const rm2 = await freshRoleMaker();
        rm2.initRoleMaker();
        const code = await copyAndReadCode();
        const def = JSON.parse(decodeRoleCode(code)) as { passives?: { countsAs?: number } };
        expect(def.passives).toEqual({ countsAs: 2 });
    });
});

// サボタージュを つかえる (Wave 9 追記): 省略 (陣営どおり) と明示 true/false を区別する
// 3状態セレクタ。フォーム層5経路 (readForm/writeForm/buildDefinitionFromForm/
// loadFormFromStorage/loadCode) すべてで欠落しないことを確認する。
describe("役職メーカー: サボタージュを つかえる (Wave 9 追記・3状態セレクタ)", () => {
    beforeEach(() => {
        localStorage.clear();
    });
    afterEach(() => {
        localStorage.clear();
    });

    it("既定 (陣営どおり) では canSabotage キー自体が出力されない", async () => {
        const rm = await freshRoleMaker();
        rm.initRoleMaker();
        setValidName();

        expect(($("rm-can-sabotage") as HTMLSelectElement).value).toBe("");

        const code = await copyAndReadCode();
        const def = JSON.parse(decodeRoleCode(code)) as Record<string, unknown>;
        expect(Object.prototype.hasOwnProperty.call(def, "canSabotage")).toBe(false);
    });

    it("「つかえる」を選ぶと canSabotage: true がフォーム→定義→コード→読込の往復で保たれる", async () => {
        const rm = await freshRoleMaker();
        rm.initRoleMaker();
        setValidName();

        const select = $("rm-can-sabotage") as HTMLSelectElement;
        select.value = "true";
        fireChange(select);

        const code = await copyAndReadCode();
        const def = JSON.parse(decodeRoleCode(code)) as { canSabotage?: boolean };
        expect(def.canSabotage).toBe(true);

        ($("rm-load-text") as HTMLTextAreaElement).value = code;
        ($("rm-load-btn") as HTMLButtonElement).click();
        expect(($("rm-can-sabotage") as HTMLSelectElement).value).toBe("true");
    });

    it("「つかえない」(明示 false) を選んでも省略と区別されて保たれる", async () => {
        const rm = await freshRoleMaker();
        rm.initRoleMaker();
        setValidName();

        const select = $("rm-can-sabotage") as HTMLSelectElement;
        select.value = "false";
        fireChange(select);

        const code = await copyAndReadCode();
        const def = JSON.parse(decodeRoleCode(code)) as Record<string, unknown>;
        expect(Object.prototype.hasOwnProperty.call(def, "canSabotage")).toBe(true);
        expect(def.canSabotage).toBe(false);

        ($("rm-load-text") as HTMLTextAreaElement).value = code;
        ($("rm-load-btn") as HTMLButtonElement).click();
        expect(($("rm-can-sabotage") as HTMLSelectElement).value).toBe("false");
    });

    it("localStorage 下書きから再初期化しても canSabotage: true が保たれる", async () => {
        const rm = await freshRoleMaker();
        rm.initRoleMaker();
        setValidName();

        const select = $("rm-can-sabotage") as HTMLSelectElement;
        select.value = "true";
        fireChange(select);
        // saveFormToStorage は入力イベントの後処理として走るので、値が draft に反映されるまで待つ
        await Promise.resolve();

        const rm2 = await freshRoleMaker();
        rm2.initRoleMaker();
        expect(($("rm-can-sabotage") as HTMLSelectElement).value).toBe("true");
        const code = await copyAndReadCode();
        const def = JSON.parse(decodeRoleCode(code)) as { canSabotage?: boolean };
        expect(def.canSabotage).toBe(true);
    });
});
