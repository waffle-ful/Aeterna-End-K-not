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

    test("スタート画面の独立項目から開ける / 全画面で開く / 閉じるとスタート画面へ戻る", async ({ page }) => {
        await page.goto("/");
        // ここではスタート画面を閉じない — 「マップを一切触らずに役職メーカーへ入れる」ことが主題
        const startBtn = page.locator("#start-role-maker");
        await expect(startBtn).toBeVisible({ timeout: 5000 });
        await startBtn.click();

        const dlg = page.locator("#dlg-role-maker");
        await expect(dlg).toBeVisible();

        // 全画面化の回帰止め: ビューポートのほぼ全面を占めていること
        const box = await dlg.boundingBox();
        const vp = page.viewportSize();
        expect(box).not.toBeNull();
        expect(vp).not.toBeNull();
        if (!box || !vp) return;
        expect(box.width).toBeGreaterThan(vp.width * 0.95);
        expect(box.height).toBeGreaterThan(vp.height * 0.95);

        await page.locator("#rm-close").click();
        await expect(dlg).toBeHidden();
        // スタート画面は隠していないので、閉じると元の画面に戻る
        await expect(page.locator("#start-screen")).toBeVisible();
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

        // ComfyUI 風パン/ズーム設定 (inject options に move/zoom/grid を追加) の回帰止め。
        // 操作シミュレーションまではしない (壊れやすいため) — zoom.controls:true が実際に
        // ズームボタン (+/−/リセットの3グループ) を描画していることの DOM 存在確認のみ。
        await expect(page.locator("#rm-blockly-container .blocklyZoom")).toHaveCount(3);
        await expect(page.locator("#rm-blockly-container .blocklyZoomIn")).toBeVisible();
        await expect(page.locator("#rm-blockly-container .blocklyZoomOut")).toBeVisible();
        await expect(page.locator("#rm-blockly-container .blocklyZoomReset")).toBeVisible();

        // 作業スペースはロジックパネルの残り全部を占める (固定高 min(58vh,480px) へ戻す回帰の検出)
        const containerBox = await page.locator("#rm-blockly-container").boundingBox();
        const vpSize = page.viewportSize();
        expect(containerBox).not.toBeNull();
        expect(vpSize).not.toBeNull();
        if (containerBox && vpSize) {
            expect(containerBox.height).toBeGreaterThan(vpSize.height * 0.5);
        }

        // 変数セクションは <details> で既定は閉じているので、まず開く (中の #rm-vars-add は
        // 閉じている間クリックできない)。開閉でワークスペースの高さが変わるので、既存の
        // ResizeObserver→svgResize がちゃんと追随して Blockly の描画を壊さないことも
        // このあとの操作 (ツールボックスからのドラッグ配置) が成功することで間接的に確認できる。
        await expect(page.locator("#rm-vars-section")).not.toHaveAttribute("open", "");
        await page.locator("#rm-vars-summary").click();
        await expect(page.locator("#rm-vars-section")).toHaveAttribute("open", "");

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

    test("v1.1 ブロック (ダミー人形/偽の死体) が blocks-role.ts↔compile-role.ts のフィールド名で正しく往復する", async ({ page }) => {
        // vitest (environment:"node") は blocks-role.ts を一切 import しない (DOM 必須のため) —
        // そのためブロック定義の args0 フィールド名 (SLOT/NAME/AT/KILLABLE/COLOR) と compile-role.ts
        // の b.fields?.XXX 読み出しが一致しているかは、ここでしか検証できない (tsc は素通しする —
        // どちらも文字列リテラルで結ばれた untyped な契約のため)。ドラッグ&ドロップには頼らず、
        // Blockly の実シリアライズ形をそのまま下書きへ注入する: ワークスペース初期化時に
        // Blockly.serialization.workspaces.load() がこの fields をブロック定義と突き合わせて
        // 実ブロックへ復元し、「コードをコピー」時の save()→compileWorkspaceToLogicInput が
        // 同じ名前で読み出せるかを実 Blockly を通して検証する。
        const cap = capture(page);
        await page.addInitScript(() => {
            localStorage.setItem(
                "ekm.roleMaker",
                JSON.stringify({
                    name: "v1.1ブロックテスト役職",
                    logicBlockly: {
                        blocks: {
                            languageVersion: 0,
                            blocks: [
                                {
                                    type: "ekr_when_on_pet",
                                    next: {
                                        block: {
                                            type: "ekr_do_dummy_spawn",
                                            fields: { SLOT: "2", NAME: "ヤギさん", AT: "ctx", KILLABLE: "1" },
                                            next: {
                                                block: {
                                                    type: "ekr_do_corpse_spawn",
                                                    fields: { COLOR: "random", AT: "self" },
                                                },
                                            },
                                        },
                                    },
                                },
                            ],
                        },
                    },
                }),
            );
        });

        await page.goto("/");
        await dismissStartScreen(page);
        await page.context().grantPermissions(["clipboard-read", "clipboard-write"]);

        await page.locator("#btn-role-maker").click();
        await expect(page.locator("#dlg-role-maker")).toBeVisible();
        await expect(page.locator("#rm-name")).toHaveValue("v1.1ブロックテスト役職");

        await page.locator('#rm-tabs .rm-tab[data-rm-tab="logic"]').click();
        await expect(page.locator("#rm-blockly-container svg.blocklySvg")).toBeVisible({ timeout: 30000 });

        // 復元/フィールド名不一致があれば検証エラー通知が出る (壊れたブロック復元 or
        // validateRoleLogic の型エラー) — 出ていないことをまず確認する
        await expect(page.locator("#rm-logic-validity")).toBeHidden();

        await page.locator("#rm-copy").click();
        await expect(page.locator("#rm-status")).toContainText("コピーしました");
        const code = await page.evaluate(() => navigator.clipboard.readText());

        const jsonText = await page.evaluate(async (c: string) => {
            const mod = await import("/src/rolecode.ts");
            return mod.decodeRoleCode(c);
        }, code);
        const parsed = JSON.parse(jsonText) as { logic?: { rules: { when: string; do: unknown[] }[] } };
        expect(parsed.logic).toBeDefined();
        const doNodes = parsed.logic!.rules[0].do;
        expect(doNodes[0]).toEqual({ op: "dummy_spawn", slot: 2, name: "ヤギさん", killable: true, at: "ctx" });
        expect(doNodes[1]).toEqual({ op: "corpse_spawn", color: "random", at: "self" });

        // buildRoleToolbox() の2エントリ (ekr_do_dummy_spawn/ekr_do_corpse_spawn) はブロック type と
        // 文字列リテラルで結ばれているだけなので tsc は誤字を検出できない (上のアサーションは
        // ワークスペースへ直接注入した経路のみを通るため、パレットからの到達性は別に確認が要る)。
        // 「見た目」カテゴリのフライアウトに両ブロックの日本語ラベルが実際に出ることを確認する。
        await page.locator(".blocklyToolboxDiv .blocklyTreeRow", { hasText: "見た目" }).click();
        // Blockly は使っていない (未描画/幅0の) 予備の .blocklyFlyout SVG を残すことがあるため、
        // 実際に中身が入っている方を hasText で一意に絞り込む (素の ".blocklyFlyout" だと
        // strict mode で複数ヒットして落ちる)。
        const flyout = page.locator(".blocklyFlyout", { hasText: "ダミー人形" });
        await expect(flyout).toBeVisible();
        await expect(flyout).toContainText("死体");

        console.log("=== page console ===\n" + (cap.console.join("\n") || "(なし)"));
        console.log("=== page errors ===\n" + (cap.errors.join("\n") || "(なし)"));
        expect(cap.errors, "未捕捉の例外あり").toEqual([]);
    });
});

test.describe("役職メーカー (ライブプレビュー)", () => {
    test("名前・色・保存済みロジック下書きがプレビューへ反映される / ロジックタブへ切替えると基本情報パネルが隠れる", async ({ page }) => {
        const cap = capture(page);

        // ロジック要約行 (項目d) は「ロジックタブを一度も開いていない」状態でも
        // pendingBlocklyRestore 経由で表示できる必要がある (role-maker.ts の adoptLoadedLogic
        // コメント参照)。addInitScript で main.ts 初回実行より前に下書きを仕込み、
        // その経路 (ワークスペース未生成のまま currentBlocklyState() が下書きをそのまま返す方) を通す。
        await page.addInitScript(() => {
            localStorage.setItem(
                "ekm.roleMaker",
                JSON.stringify({
                    name: "テスト役職",
                    logicBlockly: { blocks: { languageVersion: 0, blocks: [{ type: "ekr_when_on_pet" }] } },
                }),
            );
        });

        await page.goto("/");
        await dismissStartScreen(page);

        await page.locator("#btn-role-maker").click();
        await expect(page.locator("#dlg-role-maker")).toBeVisible();

        // 下書きの名前がそのまま出ている ＝ ロジックタブを開く前の openRoleMaker() 直後の
        // renderPreview() 呼び出しが効いていることの確認を兼ねる
        await expect(page.locator("#rm-preview-head-name")).toHaveText("テスト役職");
        await expect(page.locator("#rm-preview-banner-name")).toHaveText("テスト役職");
        await expect(page.locator("#rm-preview-logic-summary")).toBeVisible();
        await expect(page.locator("#rm-preview-logic-summary")).toHaveText("ブロックロジック: きっかけ 1 種");

        await page.locator("#rm-color").fill("#ff0000");
        await expect(page.locator("#rm-preview-head-name")).toHaveCSS("color", "rgb(255, 0, 0)");
        await expect(page.locator("#rm-preview-banner-name")).toHaveCSS("color", "rgb(255, 0, 0)");

        // #rm-panel-basic を display:flex の2カラムにした変更の回帰止め: ロジックタブへ切替えたら
        // (dialog 直下の [hidden] 上書きが display:flex 化後も生きていて) ちゃんと隠れること。
        await page.locator('#rm-tabs .rm-tab[data-rm-tab="logic"]').click();
        await expect(page.locator("#rm-panel-basic")).toBeHidden();
        await expect(page.locator("#rm-blockly-container svg.blocklySvg")).toBeVisible({ timeout: 30000 });

        console.log("=== page console ===\n" + (cap.console.join("\n") || "(なし)"));
        console.log("=== page errors ===\n" + (cap.errors.join("\n") || "(なし)"));
        expect(cap.errors, "未捕捉の例外あり").toEqual([]);
    });
});

test.describe("役職メーカー (ロジックタブの全画面フローティング UI)", () => {
    test("ワークスペースがビューポートの大部分を占め、タブ/menu がその上に浮いて見える", async ({ page }) => {
        const cap = capture(page);
        await page.goto("/");
        await dismissStartScreen(page);
        await page.evaluate(() => localStorage.removeItem("ekm.roleMaker"));

        await page.locator("#btn-role-maker").click();
        await page.locator('#rm-tabs .rm-tab[data-rm-tab="logic"]').click();
        await expect(page.locator("#rm-blockly-container svg.blocklySvg")).toBeVisible({ timeout: 30000 });

        // #dlg-role-maker の data-rm-tab がロジックタブへ切替えた CSS の入口になっている回帰止め
        await expect(page.locator("#dlg-role-maker")).toHaveAttribute("data-rm-tab", "logic");

        // ワークスペースがビューポートの大部分を占めること。
        // 注意: 文字どおりの全面 (inset:0) にはしていない — Blockly のツールボックス/フライアウトは
        // #rm-tabs 等の浮きパネルより z-index で勝てない (#rm-blockly-container が独自の
        // スタッキングコンテキストを作るため子孫の z-index は外に漏れない) ので、素の全面だと
        // フライアウト序盤のブロックがタブ切替ボタンの下に隠れてドラッグ不能になる実害があった。
        // そのため #rm-tabs/menu 用に上部 120px を予約している (実測面積比 ≈0.85)。しきい値は
        // それより十分低い 0.75 に置き、実装の erosion (予約帯を広げすぎる将来の変更) だけを検出する。
        const containerBox = await page.locator("#rm-blockly-container").boundingBox();
        const vp = page.viewportSize();
        expect(containerBox).not.toBeNull();
        expect(vp).not.toBeNull();
        if (containerBox && vp) {
            const areaRatio = (containerBox.width * containerBox.height) / (vp.width * vp.height);
            expect(areaRatio).toBeGreaterThan(0.75);
        }

        // タブ/menu が (ダイアログの装飾ではなく) ワークスペースの上に浮くフローティングパネルに
        // なっていること。
        await expect(page.locator("#rm-tabs")).toBeVisible();
        await expect(page.locator("#rm-tabs")).toHaveCSS("position", "absolute");
        await expect(page.locator("#dlg-role-maker > menu")).toBeVisible();
        await expect(page.locator("#dlg-role-maker > menu")).toHaveCSS("position", "absolute");
        await expect(page.locator("#rm-copy")).toBeVisible();
        await expect(page.locator("#rm-close")).toBeVisible();

        // 基本情報タブへ戻ると data-rm-tab が戻り、フローティング化も解除されること
        await page.locator('#rm-tabs .rm-tab[data-rm-tab="basic"]').click();
        await expect(page.locator("#dlg-role-maker")).toHaveAttribute("data-rm-tab", "basic");
        await expect(page.locator("#dlg-role-maker > menu")).toHaveCSS("position", "static");

        console.log("=== page console ===\n" + (cap.console.join("\n") || "(なし)"));
        console.log("=== page errors ===\n" + (cap.errors.join("\n") || "(なし)"));
        expect(cap.errors, "未捕捉の例外あり").toEqual([]);
    });

    test("ワークスペースの上端予約が変数パネルの実寸に追随する (開閉・変数の増減)", async ({ page }) => {
        const cap = capture(page);
        await page.goto("/");
        await dismissStartScreen(page);
        await page.evaluate(() => localStorage.removeItem("ekm.roleMaker"));

        await page.locator("#btn-role-maker").click();
        await page.locator('#rm-tabs .rm-tab[data-rm-tab="logic"]').click();
        await expect(page.locator("#rm-blockly-container svg.blocklySvg")).toBeVisible({ timeout: 30000 });

        // 予約帯 = calc(76px + var(--rm-vars-h))。--rm-vars-h は ResizeObserver が rAF 経由で
        // 書くため反映は非同期 — expect.poll で「パネル実寸 + 76px」への収束を待つ。
        // (この追随はブラウザのフレーム駆動 (rAF/RO) 依存のため、バックグラウンドタブでは
        // 動かない — 手動確認で「壊れている」と誤診しないこと。Playwright は描画有効で走る。)
        const expectTopTracksPanel = async () => {
            await expect
                .poll(
                    () =>
                        page.evaluate(() => {
                            const top = document.getElementById("rm-blockly-container")!.getBoundingClientRect().top;
                            const panelH = document.getElementById("rm-vars-section")!.offsetHeight;
                            return Math.abs(top - (76 + panelH));
                        }),
                    { timeout: 5000 }
                )
                .toBeLessThan(3);
        };

        // 閉状態 (summary 1行) でまず追随していること
        await expectTopTracksPanel();

        // パネルを開く → 実寸ぶんだけ予約が広がる (旧実装の固定 22vh 予約への回帰も
        // ここで検出される: 変数0個のパネル実寸は 22vh よりずっと小さい)
        await page.locator("#rm-vars-section > summary").click();
        await expect(page.locator("#rm-vars-section")).toHaveAttribute("open", "");
        await expectTopTracksPanel();

        // 変数を2個追加してパネルが伸びても追随すること
        await page.locator("#rm-vars-add").click();
        await page.locator("#rm-vars-add").click();
        await expect(page.locator(".rm-var-row")).toHaveCount(2);
        await expectTopTracksPanel();

        // 閉じると予約も縮んで戻ること
        await page.locator("#rm-vars-section > summary").click();
        await expect(page.locator("#rm-vars-section")).not.toHaveAttribute("open", "");
        await expectTopTracksPanel();

        console.log("=== page console ===\n" + (cap.console.join("\n") || "(なし)"));
        console.log("=== page errors ===\n" + (cap.errors.join("\n") || "(なし)"));
        expect(cap.errors, "未捕捉の例外あり").toEqual([]);
    });
});
