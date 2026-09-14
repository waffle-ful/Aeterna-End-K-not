// @vitest-environment jsdom
//
// Wave 12 (契約 §2〜§5)「みられかた」のフォーム欄 (disguise.role/deep・corpse:"anonymous"・
// anonymousKills/anonymousVote) を、role-maker.ts の実際の DOM 配線を通して検証する。
// dlg-role-maker のマークアップは index.html から実際に切り出して使う (role-maker-wave9.test.ts /
// role-maker-disguise.test.ts と同じ理由 — id ドリフト検出)。
//
// 契約 §6 の 🔴 (トップレベル新キーはフォーム層5経路すべて配線すること — R2 の
// passives.disguise silent drop 再発防止) をこのテストで確認する: フォーム入力 → コピー(定義構築) →
// コード → 読込 → フォームの往復で、disguise.role/deep・anonymousKills/anonymousVote が
// 欠落しないこと。

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import INDEX_HTML from "../index.html?raw";
import { decodeRoleCode } from "../src/rolecode";

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

describe("役職メーカー: べつの陣営に見せる役職名 + しらべられても ばれない (Wave 12 契約 §2)", () => {
    beforeEach(() => {
        localStorage.clear();
    });
    afterEach(() => {
        localStorage.clear();
    });

    it("既定 (disguise しない) では役職名/deep 欄が隠れている", () => {
        document.body.innerHTML = DIALOG_HTML;
        expect(($("rm-p-disguise") as HTMLSelectElement).value).toBe("");
        expect($("rm-p-disguise-role-row").hidden).toBe(true);
        expect($("rm-p-disguise-deep-row").hidden).toBe(true);
    });

    it("team を選ぶと役職名/deep 欄が現れ、role select の選択肢はその陣営の役職だけになる", async () => {
        const rm = await freshRoleMaker();
        rm.initRoleMaker();

        const teamSelect = $("rm-p-disguise") as HTMLSelectElement;
        teamSelect.value = "crewmate";
        fireChange(teamSelect);

        expect($("rm-p-disguise-role-row").hidden).toBe(false);
        expect($("rm-p-disguise-deep-row").hidden).toBe(false);

        const roleSelect = $("rm-p-disguise-role") as HTMLSelectElement;
        const optionValues = Array.from(roleSelect.options).map((o) => o.value);
        expect(optionValues).toContain("Sheriff"); // crewmate
        expect(optionValues).not.toContain("Amnesiac"); // neutral (別陣営)
    });

    it("team を切り替えると、別陣営の役職だった選択がリセットされる (repopulate で stale な値を残さない)", async () => {
        const rm = await freshRoleMaker();
        rm.initRoleMaker();

        const teamSelect = $("rm-p-disguise") as HTMLSelectElement;
        const roleSelect = $("rm-p-disguise-role") as HTMLSelectElement;

        teamSelect.value = "crewmate";
        fireChange(teamSelect);
        roleSelect.value = "Sheriff";
        fireChange(roleSelect);
        expect(roleSelect.value).toBe("Sheriff");

        teamSelect.value = "neutral";
        fireChange(teamSelect);
        // Sheriff は neutral の選択肢に無いので空欄へ落ちる。
        expect(roleSelect.value).toBe("");
    });

    it("disguise.role/deep がフォーム→定義→コード→読込の往復で保たれる", async () => {
        const rm = await freshRoleMaker();
        rm.initRoleMaker();
        setValidName();

        const teamSelect = $("rm-p-disguise") as HTMLSelectElement;
        teamSelect.value = "crewmate";
        fireChange(teamSelect);

        const roleSelect = $("rm-p-disguise-role") as HTMLSelectElement;
        roleSelect.value = "Sheriff";
        fireChange(roleSelect);

        const deepCheckbox = $("rm-p-disguise-deep") as HTMLInputElement;
        deepCheckbox.checked = true;
        fireChange(deepCheckbox);

        const code = await copyAndReadCode();
        const def = JSON.parse(decodeRoleCode(code)) as { passives?: { disguise?: { team: string; role?: string; deep?: boolean } } };
        expect(def.passives?.disguise).toEqual({ team: "crewmate", role: "Sheriff", deep: true });

        ($("rm-load-text") as HTMLTextAreaElement).value = code;
        ($("rm-load-btn") as HTMLButtonElement).click();

        expect((teamSelect as HTMLSelectElement).value).toBe("crewmate");
        expect((roleSelect as HTMLSelectElement).value).toBe("Sheriff");
        expect((deepCheckbox as HTMLInputElement).checked).toBe(true);
    });

    it("role を「とくに指定しない」のままコピーすると disguise.role キー自体が出力されない", async () => {
        const rm = await freshRoleMaker();
        rm.initRoleMaker();
        setValidName();

        const teamSelect = $("rm-p-disguise") as HTMLSelectElement;
        teamSelect.value = "impostor";
        fireChange(teamSelect);

        const code = await copyAndReadCode();
        const def = JSON.parse(decodeRoleCode(code)) as { passives?: { disguise?: Record<string, unknown> } };
        expect(def.passives?.disguise).toEqual({ team: "impostor" });
        if (def.passives?.disguise) {
            expect(Object.prototype.hasOwnProperty.call(def.passives.disguise, "role")).toBe(false);
            expect(Object.prototype.hasOwnProperty.call(def.passives.disguise, "deep")).toBe(false);
        }
    });
});

describe("役職メーカー: じぶんの死体は だれのか わからない (Wave 12 契約 §3・corpse:\"anonymous\")", () => {
    beforeEach(() => {
        localStorage.clear();
    });
    afterEach(() => {
        localStorage.clear();
    });

    it("corpse=anonymous がフォーム→定義→コード→読込の往復で保たれる", async () => {
        const rm = await freshRoleMaker();
        rm.initRoleMaker();
        setValidName();

        const select = $("rm-p-corpse") as HTMLSelectElement;
        select.value = "anonymous";
        fireChange(select);

        const code = await copyAndReadCode();
        const def = JSON.parse(decodeRoleCode(code)) as { passives?: { corpse?: string } };
        expect(def.passives?.corpse).toBe("anonymous");

        ($("rm-load-text") as HTMLTextAreaElement).value = code;
        ($("rm-load-btn") as HTMLButtonElement).click();
        expect((select as HTMLSelectElement).value).toBe("anonymous");
    });
});

describe("役職メーカー: ころした死体は だれのか わからない / じぶんの票は 見えない (Wave 12 契約 §4/§5)", () => {
    beforeEach(() => {
        localStorage.clear();
    });
    afterEach(() => {
        localStorage.clear();
    });

    it("両方チェックすると passives.anonymousKills/anonymousVote が true でコードに出る", async () => {
        const rm = await freshRoleMaker();
        rm.initRoleMaker();
        setValidName();

        const killsCheckbox = $("rm-p-anonymous-kills") as HTMLInputElement;
        killsCheckbox.checked = true;
        fireChange(killsCheckbox);

        const voteCheckbox = $("rm-p-anonymous-vote") as HTMLInputElement;
        voteCheckbox.checked = true;
        fireChange(voteCheckbox);

        const code = await copyAndReadCode();
        const def = JSON.parse(decodeRoleCode(code)) as { passives?: { anonymousKills?: boolean; anonymousVote?: boolean } };
        expect(def.passives?.anonymousKills).toBe(true);
        expect(def.passives?.anonymousVote).toBe(true);

        ($("rm-load-text") as HTMLTextAreaElement).value = code;
        ($("rm-load-btn") as HTMLButtonElement).click();
        expect((killsCheckbox as HTMLInputElement).checked).toBe(true);
        expect((voteCheckbox as HTMLInputElement).checked).toBe(true);
    });

    it("どちらもチェックしないままコピーすると、両キーとも出力されない", async () => {
        const rm = await freshRoleMaker();
        rm.initRoleMaker();
        setValidName();

        const code = await copyAndReadCode();
        const def = JSON.parse(decodeRoleCode(code)) as { passives?: Record<string, unknown> };
        if (def.passives) {
            expect(Object.prototype.hasOwnProperty.call(def.passives, "anonymousKills")).toBe(false);
            expect(Object.prototype.hasOwnProperty.call(def.passives, "anonymousVote")).toBe(false);
        }
    });

    it("localStorage の下書きに壊れた disguise.role (陣営不一致) が入っていても role 欄は空欄へフォールバックして開ける", async () => {
        localStorage.setItem(
            "ekm.roleMaker",
            JSON.stringify({
                name: "壊れた下書き",
                passives: { disguise: { team: "crewmate", role: "Amnesiac", deep: true } },
            }),
        );
        const rm = await freshRoleMaker();
        rm.initRoleMaker();

        expect(($("rm-p-disguise") as HTMLSelectElement).value).toBe("crewmate");
        // Amnesiac は neutral 役職なので crewmate との組み合わせは寛容化で落ちる (role 欄は空欄)。
        expect(($("rm-p-disguise-role") as HTMLSelectElement).value).toBe("");
        // deep 自体は team/role とは独立した真偽値なので保たれる。
        expect(($("rm-p-disguise-deep") as HTMLInputElement).checked).toBe(true);
    });
});
