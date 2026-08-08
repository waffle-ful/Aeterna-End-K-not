import { test, expect, type ConsoleMessage, type Page } from "@playwright/test";

/**
 * 役職メーカー (EKN R0) の DOM スモーク。PIXI/WebGL には触らないプレーンな <dialog> 機能なので、
 * crewrun 系のような SwiftShader 対策は不要 — 素の Playwright アサーションで十分。
 */

interface Captured {
    console: string[];
    errors: string[];
}

function capture(page: Page): Captured {
    const out: Captured = { console: [], errors: [] };
    page.on("console", (m: ConsoleMessage) => out.console.push(`[${m.type()}] ${m.text()}`));
    page.on("pageerror", (e) => out.errors.push(`${e.name}: ${e.message}`));
    return out;
}

/**
 * 初回訪問時の全面スタート画面を閉じる。boot() の自動保存復元チェックが非同期なので
 * goto() 直後はまだ非表示 (表示は少し遅れて来る) — 即時チェックだと表示前を素通りしてしまい、
 * 後続のクリックが overlay に intercept される。表示を数秒待ってから閉じる。
 */
async function dismissStartScreen(page: Page): Promise<void> {
    const skip = page.locator("#start-skip");
    try {
        await skip.waitFor({ state: "visible", timeout: 5000 });
        await skip.click();
    } catch {
        // 自動保存から復元されてスタート画面自体が出ないケース (既に復元済み) はそのまま進める
    }
}

test.describe("役職メーカー (EKN R0, フォームのみ)", () => {
    test("キルCD欄の表示切替・コード化・読込往復・localStorage 復元", async ({ page }) => {
        const cap = capture(page);
        await page.context().grantPermissions(["clipboard-read", "clipboard-write"]);

        await page.goto("/");
        await dismissStartScreen(page);
        // 前回セッションの下書きが残っていても影響しないよう、この spec は自前でクリアしてから開始する
        await page.evaluate(() => localStorage.removeItem("ekm.roleMaker"));

        const openBtn = page.locator("#btn-role-maker");
        await expect(openBtn).toBeVisible();
        await openBtn.click();

        const dlg = page.locator("#dlg-role-maker");
        await expect(dlg).toBeVisible();

        // canKill=false の既定ではキルCD欄は隠れている
        await expect(page.locator("#rm-kill-cd-row")).toBeHidden();
        await page.locator("#rm-can-kill").check();
        await expect(page.locator("#rm-kill-cd-row")).toBeVisible();

        // 範囲外の値 (999) → blur (change) で契約どおり 180 にクランプされる
        await page.locator("#rm-kill-cd").fill("999");
        await page.locator("#rm-kill-cd").blur();
        await expect(page.locator("#rm-kill-cd")).toHaveValue("180");
        await page.locator("#rm-kill-cd").fill("40");
        await page.locator("#rm-kill-cd").blur();

        await page.locator("#rm-name").fill("プレイライトテスト役職");

        // コードをコピー → クリップボードに EKR1. が乗る
        await page.locator("#rm-copy").click();
        await expect(page.locator("#rm-status")).toContainText("コピーしました");
        const code = await page.evaluate(() => navigator.clipboard.readText());
        expect(code.startsWith("EKR1.")).toBe(true);

        // フォームをリセットしてコードから読込 → 元の内容が復元される
        await page.locator("#rm-name").fill("");
        await page.locator("#rm-load-section summary").click();
        await page.locator("#rm-load-text").fill(code);
        await page.locator("#rm-load-btn").click();
        await expect(page.locator("#rm-status")).toContainText("読み込みました");
        await expect(page.locator("#rm-name")).toHaveValue("プレイライトテスト役職");
        await expect(page.locator("#rm-kill-cd")).toHaveValue("40");
        await expect(page.locator("#rm-can-kill")).toBeChecked();

        await page.locator("#rm-close").click();
        await expect(dlg).toBeHidden();

        // 再読込しても下書き (localStorage) が復元される
        await page.reload();
        await dismissStartScreen(page);
        await page.locator("#btn-role-maker").click();
        await expect(page.locator("#rm-name")).toHaveValue("プレイライトテスト役職");
        await expect(page.locator("#rm-kill-cd")).toHaveValue("40");

        console.log("=== page console ===\n" + (cap.console.join("\n") || "(なし)"));
        console.log("=== page errors ===\n" + (cap.errors.join("\n") || "(なし)"));
        expect(cap.errors, "未捕捉の例外あり").toEqual([]);
    });

    test("名前が空のままコピーしようとするとエラーが表示される (フォームは消えない)", async ({ page }) => {
        await page.goto("/");
        await dismissStartScreen(page);
        await page.evaluate(() => localStorage.removeItem("ekm.roleMaker"));

        await page.locator("#btn-role-maker").click();
        await page.locator("#rm-name").fill("");
        await page.locator("#rm-copy").click();
        await expect(page.locator("#rm-status")).toContainText("名前");
        await expect(page.locator("#dlg-role-maker")).toBeVisible();
    });
});
