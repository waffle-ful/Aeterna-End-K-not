# Symbolon

コードを書かずに、Among Us のマップと役職をつくるエディタ。

カスタムマップ (`.ekmap.json`) の作成・編集に対応する。床/壁/奈落のペイント、decor スタンプ、
スポーン配置、通行○×プレビュー、Undo/Redo、IndexedDB 自動保存、`.ekmap.json` 入出力、
マップコード (`EKM1.…`) のコピー/読込、および役職メーカー。
デスクトップアプリ (Windows) とブラウザの両方で動作する。

> **名前について** — *symbolon* (σύμβολον) は古代ギリシアの割符。土器を割って二人が持ち合い、
> 合わせて本人であることを証明した。語源は σύν (共に) + βάλλειν (投げる) で、「投げ合わせる」。
> symbol の語源でもある。

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
npm run tauri:build   # リリース: src-tauri/target/release/bundle/nsis/ にインストーラ (.exe)
```

- 設定: `src-tauri/tauri.conf.json` (識別子 `net.symbolon.editor`、ウィンドウ 1280×800)。
  配布形式は NSIS インストーラのみ (`bundle.targets`)。
- ファイル書込権限: `src-tauri/capabilities/default.json` で `Documents/EndKnot/EKMaps` 配下のみ許可。
- アプリ側の保存ロジックは `src/playtest.ts` (Tauri 検出 → `@tauri-apps/plugin-fs` で直書き / ブラウザ → File System Access API / 未対応 → ダウンロード)。
- アイコンは Tauri 既定のプレースホルダ。差し替えは `npm run tauri icon <png>`。
- 注: ブラウザ版は PWA としてインストールも可能 (オフライン)。配布の暫定手段として `dist/` を静的ホスティングしても良い。

## マップ形式 (EKM v1)

マップ形式・検証規則・マップコード (`EKM1.` + base64url(deflate-raw))・壁/柱の導出は
EKM v1 で固定されている。この形式はモッド側のローダーと1バイト単位で噛み合っているので、
エンコードや検証の挙動を変えるときは、エディタ・ローダー・サンプルマップのテストを必ず同時に直す。
片方だけ動かすと「エディタでは保存できるのにゲームで開けないマップ」が生まれる。

## 境界値の扱い

形式が上限だけ決めていて、超えたときの動作までは決めていない箇所がある。
本エディタは次のように振る舞う。

- decor が 1024 件を超えたとき: **超過分だけを警告付きでスキップ**する (マップ全体は拒否しない)。
- author が 32 文字を超えたとき: **警告を出して切り詰める**。
- decor/spawn の float 座標 → セルの対応: セル中心を基準に **四捨五入** する (±0.5 がセル境界)。
- 同一セルへの複数 decor: 形式上は置けるが、配置 UI は 1 セル 1 個に絞っている
  (別種を置くと置換、同種を再クリックで除去)。インポートした複数 decor はそのまま保持・表示する。

## クレジット (3Dモデル・音声素材)

同梱ミニゲーム「Crew Run 3D」では、以下のサードパーティ素材を使用しています。
3Dモデルはいずれも [Creative Commons Attribution 4.0 (CC-BY 4.0)](http://creativecommons.org/licenses/by/4.0/) ライセンスです。

- **"Bacteria - Kane Pixels Backrooms"** by **Roman Trace** — CC-BY 4.0 ([Source](https://skfb.ly/pBZ8p))
- **"Backrooms partygoer"** by **wilderberry5150** — CC-BY 4.0 ([Source](https://skfb.ly/oJuCJ))
- 効果音素材: **[ポケットサウンド / 効果音素材](https://pocket-se.info/)**

詳細な帰属表記は [`ASSET_CREDITS.md`](./ASSET_CREDITS.md) を参照してください。
