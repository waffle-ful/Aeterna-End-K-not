import { defineConfig } from "vitest/config";
import { VitePWA } from "vite-plugin-pwa";

export default defineConfig({
    base: "./",
    plugins: [
        VitePWA({
            // 配布は Tauri exe に決定したため Service Worker は不要。
            // selfDestroying = 既に登録済みの SW を自己消去しキャッシュを掃除する SW を出力する
            // (古い画面がキャッシュで残る事故を根絶。exe/ブラウザとも常に最新を表示)。
            // manifest はインストール性のため残す。
            selfDestroying: true,
            registerType: "autoUpdate",
            manifest: {
                name: "EKMap エディタ",
                short_name: "EKMap",
                description: "End K not カスタムマップエディタ",
                lang: "ja",
                theme_color: "#141419",
                background_color: "#141419",
                display: "standalone",
                start_url: "./",
                scope: "./",
                icons: [
                    { src: "icon-192.png", sizes: "192x192", type: "image/png" },
                    { src: "icon-512.png", sizes: "512x512", type: "image/png" },
                    { src: "icon-512.png", sizes: "512x512", type: "image/png", purpose: "maskable" },
                ],
            },
            workbox: {
                // 静的アセットを precache してオフライン編集を可能にする (IndexedDB 完結なのでサーバ不要)
                globPatterns: ["**/*.{js,css,html,png,svg,woff2}"],
                navigateFallback: "index.html",
            },
        }),
    ],
    test: {
        environment: "node",
        include: ["tests/**/*.test.ts"],
    },
});
