# End K not マップエディタ (EKM v1)

End K not のカスタムマップ (`.ekmap.json`) をブラウザで作成・編集する Web エディタ (Phase 1 MVP)。
床/壁/奈落のペイント、decor スタンプ、スポーン配置、通行○×プレビュー、Undo/Redo、
IndexedDB 自動保存、`.ekmap.json` 入出力、マップコード (`EKM1.…`) のコピー/読込に対応。

## 開発

```
npm install
npm run dev      # 開発サーバ (http://localhost:5173)
npm run build    # 型チェック + dist/ へ静的ビルド (PWA: SW + manifest 生成)
npm test         # vitest (契約テスト)
```

## デスクトップアプリ (Tauri exe) のビルド

リリースは「ターミナルを使わずダブルクリックで起動できる exe」を本命とする (Tauri)。
Web アプリを小さなネイティブ exe で包み、オフライン動作 + フル FS アクセス
(「▶ ゲームで試す」が `Documents/EndKnot/EKMaps` へ直書き) になる。

**前提: Rust ツールチェーン (一度だけ)** — https://rustup.rs から rustup を入れる。
Windows は MSVC ビルドツール (Visual Studio C++ Build Tools) も必要。

```
npm run tauri:dev     # 開発: ネイティブウィンドウで起動 (vite dev を内包)
npm run tauri:build   # リリース: src-tauri/target/release/bundle/ に exe + インストーラ
```

- 設定: `src-tauri/tauri.conf.json` (識別子 `net.endknot.ekmap`、ウィンドウ 1280×800)。
- ファイル書込権限: `src-tauri/capabilities/default.json` で `Documents/EndKnot/EKMaps` 配下のみ許可。
- アプリ側の保存ロジックは `src/playtest.ts` (Tauri 検出 → `@tauri-apps/plugin-fs` で直書き / ブラウザ → File System Access API / 未対応 → ダウンロード)。
- アイコンは Tauri 既定のプレースホルダ。差し替えは `npm run tauri icon <png>`。
- 注: ブラウザ版は PWA としてインストールも可能 (オフライン)。配布の暫定手段として `dist/` を静的ホスティングしても良い。

## 仕様準拠先

マップ形式・検証規則・マップコード (`EKM1.` + base64url(deflate-raw))・壁/柱の導出は
**EKM v1 仕様 (リポジトリ内部文書 `docs/ekmap-spec.md`、凍結 2026-06-10)** に厳密準拠。
エンコードや検証の挙動を変えるときは仕様側・モッド側ローダー・golden テストを同時に更新すること。

## TODO (仕様の曖昧点 — 仕様側で要確認)

- decor が 1024 件を超えた場合の扱いが §9 の拒否リストに無い (§2 は「最大 1024 件」とのみ記載)。
  本エディタは **超過分のみ警告スキップ** (マップ全体は拒否しない) として実装。
- author が 32 文字を超えた場合も §9 の拒否リストに無いため、**警告 + 切り詰め** として実装。
- decor/spawn の float 座標 → セルの対応は「セル中心基準」から **四捨五入** (±0.5 がセル境界) と解釈。
- 同一セルへの複数 decor は仕様上可能だが、エディタの配置 UI は 1 セル 1 個
  (別種を置くと置換、同種の再クリックで除去。インポートした複数 decor は保持・表示する)。
