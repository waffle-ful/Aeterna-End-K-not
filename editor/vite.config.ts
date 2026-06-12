import { defineConfig } from "vitest/config";
import { VitePWA } from "vite-plugin-pwa";

export default defineConfig({
    base: "./",
    plugins: [
        VitePWA({
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
