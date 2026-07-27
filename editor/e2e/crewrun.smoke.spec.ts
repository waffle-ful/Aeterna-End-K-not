import { test, expect, type ConsoleMessage, type Page } from "@playwright/test";

/**
 * Crew Run 起動スモーク。
 * アサーションより先に「何が起きたか」を全部出す方針 — headless WebGL の失敗と
 * ミニゲーム自体のバグを取り違えないため、console/pageerror を素通しでログする。
 */

interface Captured {
    console: string[];
    errors: string[];
}

function capture(page: Page): Captured {
    const out: Captured = { console: [], errors: [] };
    page.on("console", (m: ConsoleMessage) => {
        out.console.push(`[${m.type()}] ${m.text()}`);
    });
    page.on("pageerror", (e) => {
        out.errors.push(`${e.name}: ${e.message}`);
    });
    return out;
}

test("スタート画面からミニゲームが起動して描画される", async ({ page }, testInfo) => {
    const cap = capture(page);

    await page.goto("/");

    // スタート画面のミニゲームボタン
    const btn = page.locator("#start-minigame");
    await expect(btn).toBeVisible({ timeout: 15_000 });
    await btn.click();

    // 動的 import (crewrun3d.ts は巨大) を待つ
    const overlay = page.locator("#minigame-overlay");
    await expect(overlay).toBeVisible();
    const canvas = overlay.locator("canvas");
    await expect(canvas).toBeVisible({ timeout: 30_000 });

    // WebGL コンテキストが本当に生きているか (SwiftShader 判定込み)
    const gl = await page.evaluate(() => {
        const c = document.querySelector("#minigame-overlay canvas") as HTMLCanvasElement | null;
        if (!c) return { ok: false, reason: "no canvas" };
        const ctx = (c.getContext("webgl2") ?? c.getContext("webgl")) as WebGLRenderingContext | null;
        if (!ctx) return { ok: false, reason: "no webgl context" };
        const dbg = ctx.getExtension("WEBGL_debug_renderer_info");
        return {
            ok: true,
            width: c.width,
            height: c.height,
            renderer: dbg ? String(ctx.getParameter(dbg.UNMASKED_RENDERER_WEBGL)) : "(unknown)",
        };
    });

    // 数フレーム回してから目視用スクショ
    await page.waitForTimeout(2500);
    // 目視用に固定パスへ保存 (成功時も残す — 「動いてるが絵が変」を見るため)
    const shot = await page.screenshot({ path: "e2e/.artifacts/crewrun-latest.png", fullPage: false });
    await testInfo.attach("crewrun.png", { body: shot, contentType: "image/png" });

    console.log("=== WebGL ===\n" + JSON.stringify(gl, null, 2));
    console.log("=== page console ===\n" + (cap.console.join("\n") || "(なし)"));
    console.log("=== page errors ===\n" + (cap.errors.join("\n") || "(なし)"));

    expect(gl.ok, `WebGL 不成立: ${JSON.stringify(gl)}`).toBe(true);
    expect(cap.errors, "未捕捉の例外あり").toEqual([]);
});
