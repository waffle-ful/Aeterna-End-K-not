// @vitest-environment jsdom
//
// passives.disguise (R2 契約 §4: docs/ekn-r2-contract.md) のフォーム欄を、role-maker.ts の実際の
// DOM 配線を通して検証する。dlg-role-maker のマークアップは index.html から実際に切り出して使う —
// テスト側で id を手書き複製すると id ドリフト (フォームとテストが同時にズレて green のまま壊れる事故)
// を検出できなくなるため。
//
// role-maker.ts はモジュールトップレベルの `wired` フラグで二重配線を防いでいる (initRoleMaker() の
// 契約: 「何度呼んでも二重配線しない」) ため、各テストで vi.resetModules() してフレッシュな
// モジュールインスタンスを読み込み直す。
//
// クリップボード API は jsdom に存在しない (navigator.clipboard === undefined) ため、
// copyCode() は常に catch 経路 (#rm-manual-copy への手動コピー表示) を通る — これはブラウザでの
// 「コピー成功」パスと出力される役職コードの中身は同一なので、テストとしての妥当性に影響しない。

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

function setValidName(): void {
    ($("rm-name") as HTMLInputElement).value = "テスト役職";
}

/** rm-copy を押して #rm-manual-copy に出る役職コードを取り出す (jsdom はクリップボード非対応なので必ず手動コピー経路になる) */
async function copyAndReadCode(): Promise<string> {
    ($("rm-copy") as HTMLButtonElement).click();
    // copyCode() は async だが、clipboard 未定義による例外は最初の await 到達前 (同期区間) で
    // catch されるため実際は同期完了する。将来 clipboard がポリフィルされても壊れないよう、
    // 念のため数ティック待つ。
    await Promise.resolve();
    await Promise.resolve();
    const code = ($("rm-manual-copy") as HTMLTextAreaElement).value;
    expect(code.length).toBeGreaterThan(0);
    return code;
}

describe("役職メーカー: passives.disguise フォーム (R2 契約 §4)", () => {
    beforeEach(() => {
        localStorage.clear();
    });
    afterEach(() => {
        localStorage.clear();
    });

    it.each(["", "crewmate", "impostor", "neutral"] as const)(
        "disguise=%s がフォーム→定義→コード→読込の往復で保たれる",
        async (teamValue) => {
            const rm = await freshRoleMaker();
            rm.initRoleMaker();
            setValidName();

            const select = $("rm-p-disguise") as HTMLSelectElement;
            select.value = teamValue;
            fireChange(select);
            expect(select.value).toBe(teamValue); // 4値とも <option> に存在すること自体の確認

            const code = await copyAndReadCode();
            const def = JSON.parse(decodeRoleCode(code)) as { passives?: { disguise?: { team: string } } };

            if (teamValue === "") {
                expect(def.passives?.disguise).toBeUndefined();
            } else {
                expect(def.passives?.disguise).toEqual({ team: teamValue });
            }

            // 読込: 同じコードをロード欄へ入れて読み込み、フォームに反映されることを確認する
            ($("rm-load-text") as HTMLTextAreaElement).value = code;
            ($("rm-load-btn") as HTMLButtonElement).click();

            expect((select as HTMLSelectElement).value).toBe(teamValue);
        },
    );

    it("「しない」を選ぶと passives.disguise キー自体が書き出されない (既定値キー省略の規約)", async () => {
        const rm = await freshRoleMaker();
        rm.initRoleMaker();
        setValidName();

        const select = $("rm-p-disguise") as HTMLSelectElement;
        select.value = "";
        fireChange(select);

        const code = await copyAndReadCode();
        const def = JSON.parse(decodeRoleCode(code)) as { passives?: Record<string, unknown> };
        if (def.passives) {
            expect(Object.prototype.hasOwnProperty.call(def.passives, "disguise")).toBe(false);
        }
    });

    it("localStorage の下書きに壊れた disguise.team が入っていても「しない」へフォールバックして開ける", async () => {
        localStorage.setItem(
            "ekm.roleMaker",
            JSON.stringify({
                name: "壊れた下書き",
                passives: { disguise: { team: "villain" } },
            }),
        );
        const rm = await freshRoleMaker();
        rm.initRoleMaker();

        const select = $("rm-p-disguise") as HTMLSelectElement;
        expect(select.value).toBe("");

        // フォールバック後にそのままコピーしても disguise キーは出てこない (壊れた値を素通ししない)
        setValidName();
        const code = await copyAndReadCode();
        const def = JSON.parse(decodeRoleCode(code)) as { passives?: Record<string, unknown> };
        if (def.passives) {
            expect(Object.prototype.hasOwnProperty.call(def.passives, "disguise")).toBe(false);
        }
    });
});
