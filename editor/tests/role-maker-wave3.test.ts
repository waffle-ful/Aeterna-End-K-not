// @vitest-environment jsdom
//
// Wave 3 (§3/§4) のフォーム欄 (progress / hostOptions) を、
// role-maker.ts の実際の DOM 配線を通して検証する。dlg-role-maker のマークアップは
// index.html から実際に切り出して使う (role-maker-disguise.test.ts と同じ理由 — id ドリフト検出)。
//
// 契約 §4.3 の 🔴 (トップレベル新キーはフォーム層5経路すべて配線すること — R2 の passives.disguise
// silent drop 再発防止) をこのテストで確認する: フォーム入力 → コピー(定義構築) → コード →
// 読込 → フォームの往復で、progress/hostOptions が欠落しないこと。

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

/** 役職メーカーの DOM を初期状態へ戻し、role-maker.ts をフレッシュに読み込み直す */
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

function fireInput(el: Element): void {
    el.dispatchEvent(new Event("input", { bubbles: true }));
}

function setValidName(): void {
    ($("rm-name") as HTMLInputElement).value = "テスト役職";
}

/** rm-copy を押して #rm-manual-copy に出る役職コードを取り出す (jsdom はクリップボード非対応なので必ず手動コピー経路になる) */
async function copyAndReadCode(): Promise<string> {
    ($("rm-copy") as HTMLButtonElement).click();
    await Promise.resolve();
    await Promise.resolve();
    const code = ($("rm-manual-copy") as HTMLTextAreaElement).value;
    expect(code.length).toBeGreaterThan(0);
    return code;
}

describe("役職メーカー: progress フォーム (Wave 3 契約 §3)", () => {
    beforeEach(() => {
        localStorage.clear();
    });
    afterEach(() => {
        localStorage.clear();
    });

    it("なまえのよこに出す文字 がフォーム→定義→コード→読込の往復で保たれる", async () => {
        const rm = await freshRoleMaker();
        rm.initRoleMaker();
        setValidName();

        const input = $("rm-progress-text") as HTMLInputElement;
        input.value = "のこり1つ";
        fireInput(input);

        const code = await copyAndReadCode();
        const def = JSON.parse(decodeRoleCode(code)) as { progress?: { text: string } };
        expect(def.progress).toEqual({ text: "のこり1つ" });

        ($("rm-load-text") as HTMLTextAreaElement).value = code;
        ($("rm-load-btn") as HTMLButtonElement).click();
        expect(($("rm-progress-text") as HTMLInputElement).value).toBe("のこり1つ");
    });

    it("空欄なら progress キー自体が書き出されない (既定値キー省略の規約)", async () => {
        const rm = await freshRoleMaker();
        rm.initRoleMaker();
        setValidName();

        const code = await copyAndReadCode();
        const def = JSON.parse(decodeRoleCode(code)) as Record<string, unknown>;
        expect(Object.prototype.hasOwnProperty.call(def, "progress")).toBe(false);
    });
});

describe("役職メーカー: hostOptions フォーム (Wave 3 契約 §4)", () => {
    beforeEach(() => {
        localStorage.clear();
    });
    afterEach(() => {
        localStorage.clear();
    });

    it("固定キーの数値露出がフォーム→定義→コード→読込の往復で保たれる", async () => {
        const rm = await freshRoleMaker();
        rm.initRoleMaker();
        setValidName();

        ($("rm-hostopts-add") as HTMLButtonElement).click();
        const row = document.querySelector("#rm-hostopts-list .rm-var-row") as HTMLElement;
        const labelInput = row.querySelector("input.rm-var-name") as HTMLInputElement;
        labelInput.value = "キルクールダウン";
        fireInput(labelInput);

        const code = await copyAndReadCode();
        const def = JSON.parse(decodeRoleCode(code)) as { hostOptions?: { key: string; label: string }[] };
        // addHostOption() は「選択肢の中でまだ使われていない最初のキー」を既定にする —
        // 固定6キーの先頭 (shield.count) がまだ使われていないので、それが選ばれる。
        expect(def.hostOptions).toEqual([{ key: "shield.count", label: "キルクールダウン" }]);

        ($("rm-load-text") as HTMLTextAreaElement).value = code;
        ($("rm-load-btn") as HTMLButtonElement).click();
        const reloadedLabel = document.querySelector("#rm-hostopts-list input.rm-var-name") as HTMLInputElement;
        expect(reloadedLabel.value).toBe("キルクールダウン");
        const reloadedKey = document.querySelector("#rm-hostopts-list select") as HTMLSelectElement;
        expect(reloadedKey.value).toBe("shield.count");
    });

    it("var:<宣言済み変数> キーへ切り替えると min/max 入力欄が現れ、既定値が自動で入る", async () => {
        const rm = await freshRoleMaker();
        rm.initRoleMaker();

        // 「変数」だけを #rm-vars-add で足しても、Blockly ワークスペース (ロジックタブを一度も
        // 開いていない) が無いと currentLogicCandidate() が undefined を返し、logic キー自体が
        // 出力されない (= declaredVarNames が空集合になり var: が reject される)。これは既存の
        // ロジック層の仕様であり Wave 3 側の不具合ではないため、テストは「variables 込みの
        // logic を持つコードを読み込む」形で現実的な前提を作る (noBlocklyPassthrough 経路)。
        const seedDef = {
            ekr: 1, requires: [], name: "シード", author: "", color: "#8f8f8f", team: "crewmate",
            canKill: false, killCooldown: 25, canVent: false, visionMultiplier: 1, winCondition: "team",
            logic: { version: 1, variables: [{ name: "たま", init: 0 }], rules: [{ when: "on_pet", do: [{ op: "stop" }] }] },
        };
        ($("rm-load-text") as HTMLTextAreaElement).value = encodeRoleCode(JSON.stringify(seedDef));
        ($("rm-load-btn") as HTMLButtonElement).click();

        ($("rm-hostopts-add") as HTMLButtonElement).click();
        const row = document.querySelector("#rm-hostopts-list .rm-var-row") as HTMLElement;
        const keySelect = row.querySelector("select") as HTMLSelectElement;
        keySelect.value = "var:たま";
        fireChange(keySelect);

        const numberInputs = document.querySelectorAll("#rm-hostopts-list input[type='number']");
        expect(numberInputs.length).toBe(2); // min/max

        const labelInput = document.querySelector("#rm-hostopts-list input.rm-var-name") as HTMLInputElement;
        labelInput.value = "たまの数";
        fireInput(labelInput);

        const code = await copyAndReadCode();
        const def = JSON.parse(decodeRoleCode(code)) as { hostOptions?: { key: string; label: string; min?: number; max?: number }[] };
        expect(def.hostOptions).toEqual([{ key: "var:たま", label: "たまの数", min: -9999, max: 9999 }]);

        ($("rm-load-text") as HTMLTextAreaElement).value = code;
        ($("rm-load-btn") as HTMLButtonElement).click();
        const reloadedNumberInputs = document.querySelectorAll("#rm-hostopts-list input[type='number']");
        expect(reloadedNumberInputs.length).toBe(2);
    });

    it("0件なら hostOptions キー自体が書き出されない (既定値キー省略の規約)", async () => {
        const rm = await freshRoleMaker();
        rm.initRoleMaker();
        setValidName();

        const code = await copyAndReadCode();
        const def = JSON.parse(decodeRoleCode(code)) as Record<string, unknown>;
        expect(Object.prototype.hasOwnProperty.call(def, "hostOptions")).toBe(false);
    });

    // 回帰テスト: addHostOption() が label:"" の行を作ると、ラベルを一度も編集しないままコピーした
    // ときに validateHostOptions が「label は1〜24文字」で reject し続け、フォームが永久にコピー
    // 不能になる (passives と同じ「壊れた下書きでも必ずコピーできる」不変条件への違反)。
    it("「＋ 数値を追加」直後・ラベル未編集のままでもコピーできる (label は空文字にしない)", async () => {
        const rm = await freshRoleMaker();
        rm.initRoleMaker();
        setValidName();

        ($("rm-hostopts-add") as HTMLButtonElement).click();
        const labelInput = document.querySelector("#rm-hostopts-list input.rm-var-name") as HTMLInputElement;
        expect(labelInput.value.trim().length).toBeGreaterThan(0);

        const code = await copyAndReadCode();
        const def = JSON.parse(decodeRoleCode(code)) as { hostOptions?: { key: string; label: string }[] };
        expect(def.hostOptions).toHaveLength(1);
        expect(def.hostOptions?.[0].label.length).toBeGreaterThan(0);
    });

    it("localStorage の下書きに壊れた hostOptions が入っていても既定 (0件) へフォールバックして開ける", async () => {
        localStorage.setItem(
            "ekm.roleMaker",
            JSON.stringify({
                name: "壊れた下書き",
                hostOptions: [{ key: "voteWeight" /* label 欠落 */ }, { key: "vision", label: "" /* 空ラベル */ }, { key: "vision", label: "   " /* 空白のみ */ }, "not an object", 5],
            }),
        );
        const rm = await freshRoleMaker();
        rm.initRoleMaker();

        // sanitizeHostOptionsDraft は label 欠落/空/空白のみの行を丸ごと捨てる (寛容側フォールバック —
        // 空ラベルの行を残すと、これも上のテストと同じ「永久にコピー不能」を再現してしまう)。
        expect(document.querySelectorAll("#rm-hostopts-list .rm-var-row").length).toBe(0);

        setValidName();
        const code = await copyAndReadCode();
        const def = JSON.parse(decodeRoleCode(code)) as Record<string, unknown>;
        expect(Object.prototype.hasOwnProperty.call(def, "hostOptions")).toBe(false);
    });
});
