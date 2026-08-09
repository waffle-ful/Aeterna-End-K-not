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

test.describe("役職メーカー (EKN R1, ブロックロジック)", () => {
    test("ロジックタブで Blockly が起動し、モーダル内でもブロック配置とドロップダウン操作ができる", async ({ page }) => {
        const cap = capture(page);
        await page.goto("/");
        await dismissStartScreen(page);
        await page.evaluate(() => localStorage.removeItem("ekm.roleMaker"));

        await page.locator("#btn-role-maker").click();
        await expect(page.locator("#dlg-role-maker")).toBeVisible();

        await page.locator('#rm-tabs .rm-tab[data-rm-tab="logic"]').click();
        await expect(page.locator("#rm-panel-logic")).toBeVisible();

        // Blockly の dynamic import + inject が終わるまで待つ (Vite dev server の初回コンパイルは
        // 数百モジュール分かかることがあるため、他の待ちより長めに取る)
        const blocklySvg = page.locator("#rm-blockly-container svg.blocklySvg");
        await expect(blocklySvg).toBeVisible({ timeout: 30000 });

        // 変数を1個追加 (自前の軽量ドロップダウン UI の動作確認を兼ねる)
        await page.locator("#rm-vars-add").click();
        await expect(page.locator(".rm-var-row")).toHaveCount(1);

        // ツールボックスの「変数」カテゴリを開く (toolbox 内に限定してクリック — ページ内の
        // 他の「変数」テキスト [変数リストの見出し] と誤ってマッチしないようスコープする)。
        // 行 (blocklyTreeRow) をクリックする — 内側の blocklyTreeLabel span だけを狙うと
        // 行の当たり判定に intercept されて Playwright の actionability チェックに失敗する。
        await page.locator(".blocklyToolboxDiv .blocklyTreeRow", { hasText: "変数" }).click();
        const flyoutBlock = page.locator(".blocklyFlyout .blocklyDraggable").first();
        await expect(flyoutBlock).toBeVisible({ timeout: 5000 });

        const srcBox = await flyoutBlock.boundingBox();
        const canvasBox = await blocklySvg.boundingBox();
        expect(srcBox).not.toBeNull();
        expect(canvasBox).not.toBeNull();
        if (!srcBox || !canvasBox) return;

        // Blockly は独自ポインタ実装 (native HTML5 drag-and-drop ではない) なので mouse.* で行う
        await page.mouse.move(srcBox.x + srcBox.width / 2, srcBox.y + srcBox.height / 2);
        await page.mouse.down();
        await page.mouse.move(canvasBox.x + canvasBox.width / 2, canvasBox.y + canvasBox.height / 2, { steps: 10 });
        await page.mouse.up();

        // ワークスペース上 (フライアウト外) にブロックが配置されたことを確認
        const placedBlock = page.locator("#rm-blockly-container .blocklyBlockCanvas .blocklyDraggable").first();
        await expect(placedBlock).toBeVisible({ timeout: 5000 });

        // ドロップダウンフィールドをクリック → メニューが実際に操作できることを検証する。
        // setParentContainer が効いていない (= WidgetDiv/DropDownDiv が document.body 直下のまま)
        // だと、showModal() の top layer 昇格により dialog の backdrop の下に隠れて
        // Playwright の actionability チェックに失敗しタイムアウト/例外になる。
        const dropdownField = placedBlock.locator(".blocklyDropdownText").first();
        await dropdownField.click();

        const menuItem = page.locator(".blocklyMenuItem").first();
        await expect(menuItem).toBeVisible({ timeout: 5000 });
        await menuItem.click();

        console.log("=== page console ===\n" + (cap.console.join("\n") || "(なし)"));
        console.log("=== page errors ===\n" + (cap.errors.join("\n") || "(なし)"));
        expect(cap.errors, "未捕捉の例外あり").toEqual([]);
    });

    test("ロジック無しで組んだ場合は従来どおり logic キー無しのコードが出力される (R0 互換)", async ({ page }) => {
        await page.goto("/");
        await dismissStartScreen(page);
        await page.context().grantPermissions(["clipboard-read", "clipboard-write"]);
        await page.evaluate(() => localStorage.removeItem("ekm.roleMaker"));

        await page.locator("#btn-role-maker").click();
        await page.locator("#rm-name").fill("ロジック無し役職");
        // ロジックタブを開くだけ (何もブロックを置かない) でも R0 互換を維持できるか確認する
        await page.locator('#rm-tabs .rm-tab[data-rm-tab="logic"]').click();
        await expect(page.locator("#rm-blockly-container svg.blocklySvg")).toBeVisible({ timeout: 30000 });

        await page.locator("#rm-copy").click();
        await expect(page.locator("#rm-status")).toContainText("コピーしました");
        const code = await page.evaluate(() => navigator.clipboard.readText());

        const jsonText = await page.evaluate(async (c: string) => {
            const mod = await import("/src/rolecode.ts");
            return mod.decodeRoleCode(c);
        }, code);
        const parsed = JSON.parse(jsonText) as Record<string, unknown>;
        expect("logic" in parsed).toBe(false);
    });
});
