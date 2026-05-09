# End K not

[English](README-EN.md)

[![Discord](https://img.shields.io/badge/Discord-join-5865F2?logo=discord&logoColor=white)](https://discord.gg/sEYAFzD3a)

## このMod について

**End K not** は、[Endless Host Roles (EHR)](https://github.com/Gurge44/EndlessHostRoles) をベースとした Among Us の非公式個人フォークです。EHR の **450+ の役職と 16 のゲームモード**に加えて、[TownOfHost-K (TOHK)](https://github.com/KYMario/TownOfHost-K) 由来の役職を順次 RoleBase システム上で再実装しています。

ホストのクライアントに導入するだけで動作し、他のプレイヤーは Mod を導入せずに追加役職を楽しめます。

このMod は非公式のものであり、Among Us の開発元である Innersloth は一切関与していません。**このMod の問題に関して Innersloth へ問い合わせないでください。**

> [!WARNING]
> End K not は **alpha 段階**です。未テスト役職や WIP 機能を含みます。不具合報告や提案は [GitHub Issues](../../issues) または [Discord](https://discord.gg/sEYAFzD3a) へお願いします。

対応 Among Us バージョン : **2026.3.31**

## End K not の特徴

- **EHR + TOHK の役職統合** — EHR の役職セットに加えて、TOHK 由来の役職を RoleBase 化して移植中
- **Calamity テーマのメインメニュー** *(開発中)* — Calamity 風カスタムメインメニュー UI を実装中
- **BGM システム** — メニュー / ロビー / 任務中 / 会議 / 結果画面の BGM をホストが差し替え可能。デフォルト BGM 同梱
- **外部通信の無効化** — EHR 上流が行っていた実績 API・オンラインプリセット・ニュース取得などの通信を無効化。自Mod の更新確認（GitHub API）と Bard・アナグラム等の一部役職ゲーム機能を除き、外部への通信は行いません
- **GPL-3.0 オープンソース** — ソースコード全公開、改変・再配布自由

## インストール

1. [BepInEx IL2CPP](https://github.com/BepInEx/BepInEx) を Among Us フォルダに導入
2. [Releases](../../releases) から最新の `EndKnot.dll` をダウンロード
3. `Among Us/BepInEx/plugins/` に配置
4. Among Us を起動

## BGM のカスタマイズ

ホストが自前の楽曲に差し替えられます:

- 場所 : `Among Us/BepInEx/resources/BGM/`
- 対応形式 : `.ogg` / `.mp3` / `.wav`
- 対応スロット : `menu` / `lobby` / `intask` / `climax` / `meeting` / `result`
- ファイル名例 : `menu.ogg`、`lobby.mp3` など

`bgm_titles.json` を編集すると BGM 再生時のタイトル / 作者表示も切り替え可能です。ディスクに該当ファイルがあればそちらが優先され、無ければ同梱 BGM が再生されます。

## コミュニティ

- **Discord** : https://discord.gg/sEYAFzD3a — バグ報告・質問・雑談（推奨）
- **Issues** : [GitHub Issues](../../issues) — 確認が遅れる場合があります
- [`CODE_OF_CONDUCT.md`](./CODE_OF_CONDUCT.md) | [`CONTRIBUTING.md`](./CONTRIBUTING.md) | [`SECURITY.md`](./SECURITY.md) | [`SUPPORT.md`](./SUPPORT.md)

## ライセンス

このプロジェクトは **GNU General Public License v3.0** の下で公開されています。詳細は [`LICENSE`](./LICENSE) を参照してください。

End K not は [Endless Host Roles](https://github.com/Gurge44/EndlessHostRoles) の派生プロジェクトです。**2026 年 4 月以降の改変**は waffle-ful により行われており、改変履歴は本リポジトリの git log および [`CHANGELOG.md`](./CHANGELOG.md) で追跡できます (GPL-3.0 §5 準拠)。

## クレジット

- **[Endless Host Roles](https://github.com/Gurge44/EndlessHostRoles)** (Gurge44 他) — ベース Mod、GPL-3.0
- **[TownOfHost-K](https://github.com/KYMario/TownOfHost-K)** (KYMario 他) — 移植元役職、GPL-3.0
- **[SuperNewRoles](https://github.com/SuperNewRoles/SuperNewRoles)** (SuperNewRoles 開発チーム) — 波動砲 (WaveCannon) の設計参考、GPL-3.0
- **[TownOfHost-Pko](https://github.com/satokazoku/TownOfHost-Pko)** (satokazoku 他) — 波動砲の設計参考、GPL-3.0
- **[Town Of Host](https://github.com/tukasa0001/TownOfHost)** (tukasa0001 他) — TOH 系列の祖、README フォーマット参考
- **[Town Of Host_ForE](https://github.com/AsumuAkaguma/TownOfHost_ForE)** — README 構成参考

その他、各役職の移植元については [`CHANGELOG.md`](./CHANGELOG.md) や各 commit メッセージを参照してください。

---

Among Us is © 2018–2026 Innersloth LLC. End K not は Innersloth と提携・公認されていません。Among Us の素材の一部は Innersloth LLC の財産です。
