import { test, expect, type Page } from "@playwright/test";

/**
 * Open the minigame, close it, and open it again.
 *
 * Why this exists: crew.glb / enemy.glb are cached in module-level singletons, but dispose()
 * used to free crewBodyGeo unconditionally. That meant the SECOND launch drew a disposed
 * geometry, and the shared bones came back still holding the last baked pose (initSkeleton
 * turns matrixAutoUpdate off), so boneInverses were computed from a posed skeleton. Neither
 * shows up on a single launch, which is all the other specs do.
 */
async function openMinigame(page: Page): Promise<void> {
    await page.locator("#start-minigame").click();
    await page.locator("#minigame-overlay canvas").waitFor({ timeout: 30_000 });
    await page.waitForTimeout(2000);
}

test("ミニゲームを開き直しても素体が壊れない", async ({ page }, testInfo) => {
    const errors: string[] = [];
    page.on("pageerror", (e) => errors.push(`${e.name}: ${e.message}`));
    page.on("console", (m) => {
        if (m.type() === "error") errors.push(`[console.error] ${m.text()}`);
    });

    await page.goto("/?crdebug");
    await openMinigame(page);

    // boneInverses は Skeleton 構築時のボーン matrixWorld から計算される。ボーンがレスト姿勢に
    // 戻っていなければここが launch ごとに変わる。頂点数や bbox は bind 姿勢の値なので変化せず、
    // geometry.dispose() も CPU 側の属性は残すため、これが唯一「壊れた」を捕まえられる指標。
    const probe = () => {
        const g = (globalThis as Record<string, unknown>).__crewRun as Record<string, any>;
        const geo = g.crewBodyGeo;
        geo.computeBoundingBox();
        const sk = g.crewSkeleton;
        const names: string[] = (g.crewBones as any[]).map((b) => b.name);
        const pick = ["thighR", "foreArmL", "head"];
        const inv: Record<string, string> = {};
        for (const n of pick) {
            const i = names.indexOf(n);
            if (i >= 0) inv[n] = Array.from(sk.boneInverses[i].elements as number[]).map((v) => v.toFixed(4)).join(",");
        }
        return { maxY: +geo.boundingBox.max.y.toFixed(4), verts: geo.getAttribute("position").count, inv };
    };

    const first = await page.evaluate(probe);

    // 閉じて開き直す。exit() が launchCrewRun に渡した onExit (= main.ts の closeMinigame) を叩き、
    // destroy() → dispose() まで通る = 実際にプレイヤーが「もどる」を押したのと同じ経路。
    await page.evaluate(() => {
        const g = (globalThis as Record<string, unknown>).__crewRun as { exit: () => void };
        g.exit();
    });
    await page.locator("#minigame-overlay canvas").waitFor({ state: "detached", timeout: 10_000 });
    await openMinigame(page);

    const second = await page.evaluate(probe);

    console.log("1回目:", JSON.stringify(first), "\n2回目:", JSON.stringify(second));
    testInfo.attach("relaunch", { body: JSON.stringify({ first, second }, null, 2), contentType: "application/json" });

    expect(second.verts, "2回目で頂点が消えている").toBe(first.verts);
    expect(second.maxY, "2回目で素体の高さが変わっている").toBe(first.maxY);
    expect(second.inv, "2回目の boneInverses がズレている = ボーンがレスト姿勢に戻っていない").toEqual(first.inv);
    expect(errors, "開き直しで例外が出ている").toEqual([]);
});
