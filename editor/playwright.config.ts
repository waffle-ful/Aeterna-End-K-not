import { defineConfig, devices } from "@playwright/test";

// Crew Run (three.js/WebGL) はレンダリング回帰ゲートではない。
// 描画回帰は tests/render-smoke.test.ts の PIXI extract ピクセルハッシュが正典
// (ROADMAP.md §Stage0 / crit_editorux.md: Playwright スクショは WebGL 環境差で不安定)。
// ここでの Playwright の役目 = コンソール/例外の捕捉・入力駆動・目視用スクショ。
export default defineConfig({
    testDir: "./e2e",
    testMatch: "**/*.spec.ts",
    outputDir: "./e2e/.artifacts",
    timeout: 60_000,
    fullyParallel: false,
    workers: 1,
    reporter: [["list"]],
    use: {
        baseURL: "http://localhost:5173",
        trace: "retain-on-failure",
        screenshot: "only-on-failure",
    },
    projects: [
        {
            name: "chromium",
            use: {
                ...devices["Desktop Chrome"],
                viewport: { width: 1280, height: 800 },
                launchOptions: {
                    // headless Chromium で three.js を動かすため SwiftShader を許可する。
                    // これが無いと "Error creating WebGL context" で真っ黒になる。
                    args: ["--enable-unsafe-swiftshader", "--use-gl=angle", "--ignore-gpu-blocklist"],
                },
            },
        },
    ],
    webServer: {
        command: "npm run dev -- --port 5173 --strictPort",
        url: "http://localhost:5173",
        reuseExistingServer: true,
        timeout: 120_000,
    },
});
