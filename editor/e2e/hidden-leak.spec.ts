import { test, expect, type Page } from "@playwright/test";

/**
 * 「隠したはずの UI が消えていない」の回帰ガード。
 *
 * HTML の `hidden` 属性と、閉じている `popover` は、どちらも UA スタイルの
 * `display: none` で実現されている。そこへ独自に `display: flex` などを当てると
 * 黙って打ち消され、JS 側が `hidden = true` にしても消えない。例外も出ないので
 * 気づけない — この repo では実際に #shadow-snap-wrap が影レイヤー以外でも
 * 33px 居座り続けていた (他に 3 要素が同じ状態だった)。
 *
 * 対策として style.css で `[hidden] { display: none !important }` を効かせている。
 * このテストは (1) その規則が外れていないこと (2) 逆に効きすぎて出るべき UI まで
 * 消していないこと、の両方を主要な画面状態で確かめる。
 */

/** hidden / 閉じた popover なのに表示されている要素 */
function leaks(page: Page) {
    return page.evaluate(() =>
        Array.from(document.querySelectorAll("[hidden], [popover]:not(:popover-open)"))
            .filter((e) => getComputedStyle(e as HTMLElement).display !== "none")
            .map((e) => `${e.id || (e as HTMLElement).className}: ${getComputedStyle(e as HTMLElement).display}`));
}

/** その状態で見えていなければおかしい要素が、実際に見えているか */
async function expectVisible(page: Page, sels: string[]): Promise<void> {
    for (const s of sels) await expect(page.locator(s)).toBeVisible();
}

async function dismissCoach(page: Page): Promise<void> {
    const skip = page.locator("#coach-skip");
    if (await skip.isVisible().catch(() => false)) {
        await skip.click();
        await page.waitForTimeout(300);
    }
}

test("隠した UI が残らず、出すべき UI は消えない", async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 800 });
    await page.context().grantPermissions(["clipboard-read", "clipboard-write"]);
    await page.goto("/");

    // ── スタート画面 ────────────────────────────────────────────
    await page.locator("#start-screen").waitFor({ state: "visible", timeout: 8000 });
    expect(await leaks(page), "スタート画面").toEqual([]);
    await expectVisible(page, ["#start-templates", ".onsen-water"]);

    // ── エディタ本体 ────────────────────────────────────────────
    await page.locator("#start-skip").click();
    await page.waitForTimeout(800);
    await dismissCoach(page);
    expect(await leaks(page), "エディタ本体").toEqual([]);
    await expectVisible(page, ["#tools-v2", ".ops", "#layer-vtabs"]);

    // ── パレット展開 ────────────────────────────────────────────
    await page.locator("#btn-palette-expand").click();
    await page.waitForTimeout(500);
    expect(await leaks(page), "パレット展開").toEqual([]);
    await expectVisible(page, ["#palette-overlay"]);
    await page.keyboard.press("Escape");
    await page.waitForTimeout(300);

    // ── 影レイヤー: ここでだけ吸着トグルとヒントが出る ──────────────
    await page.locator('#layer-vtabs .lvtab[data-layer="shadow"]').click();
    await page.waitForTimeout(400);
    expect(await leaks(page), "影レイヤー").toEqual([]);
    await expectVisible(page, ["#shadow-snap-wrap", "#shadow-hint"]);

    // 影レイヤーを抜けたら引っ込むこと (打ち消しが起きていれば居座る)
    await page.locator('#layer-vtabs .lvtab[data-layer="ground"]').click();
    await page.waitForTimeout(400);
    await expect(page.locator("#shadow-snap-wrap")).toBeHidden();
    await expect(page.locator("#shadow-hint")).toBeHidden();

    // ── ⋯ メニュー (popover) ────────────────────────────────────
    await expect(page.locator("#more-menu")).toBeHidden();
    await page.locator("#btn-more").click();
    await page.waitForTimeout(300);
    await expectVisible(page, ["#more-menu", "#btn-copy-code"]);
    await page.locator("#btn-copy-code").click(); // 中のボタンで閉じる配線
    await page.waitForTimeout(300);
    await expect(page.locator("#more-menu")).toBeHidden();
    // コピー結果の通知ダイアログが出るので閉じてから次へ (開いたままだと後続のクリックを遮る)
    const msg = page.locator("#dlg-msg");
    if (await msg.isVisible().catch(() => false)) {
        await page.keyboard.press("Escape");
        await expect(msg).toBeHidden();
    }

    // ── ダイアログ ──────────────────────────────────────────────
    await page.locator("#btn-new").click();
    await page.waitForTimeout(400);
    expect(await leaks(page), "新規ダイアログ").toEqual([]);
    await expectVisible(page, ["#dlg-new"]);
    await page.keyboard.press("Escape");
    await page.waitForTimeout(300);

    // ── 役職メーカー (基本情報 / ロジック) ─────────────────────────
    await page.locator("#btn-role-maker").click();
    await page.waitForTimeout(1000);
    expect(await leaks(page), "役職メーカー(基本情報)").toEqual([]);
    await expectVisible(page, ["#rm-panel-basic", "#rm-preview"]);

    await page.locator("#rm-tabs button").nth(1).click();
    await page.waitForTimeout(1800);
    expect(await leaks(page), "役職メーカー(ロジック)").toEqual([]);
    await expectVisible(page, ["#rm-panel-logic", "#rm-blockly-container"]);
});
