# End Knot

[English](README-EN.md)

## このModについて

**End Knot** は、[Endless Host Roles (EHR)](https://github.com/Gurge44/EndlessHostRoles) をベースにした Among Us の非公式個人フォークです。

このMod は非公式のものであり、Among Us の開発元である Innersloth は一切関与していません。  
このMod の問題に関して公式に問い合わせないでください。

ホストのクライアントに導入するだけで動作し、他のプレイヤーはModを導入せずに追加役職を楽しめます。

対応 Among Us バージョン : **2026.3.31**

## 上流（EHR）との主な違い

- **リブランド** : Plugin GUID・Mod名を `EndKnot` / "End Knot" に変更
- **外部通信無効化** : アップデート確認・実績API・ニュース取得など上流の外部通信を全て無効化
- **TOHK役職の移植** : [TownOfHost-K](https://github.com/KYMario/TownOfHost-K) の役職を EHR の RoleBase システムに移植中
- **Calamity テーマのメインメニュー** (開発中)
- **BGM システム** : ゲーム内BGMの差し替え機能 (開発中)

## インストール

1. [BepInEx IL2CPP](https://github.com/BepInEx/BepInEx) を Among Us フォルダに導入する
2. [Releases](../../releases) から最新の `EndKnot.dll` をダウンロードする
3. `EndKnot.dll` を `Among Us/BepInEx/plugins/` に配置する
4. Among Us を起動する

## ライセンス

このプロジェクトは **GPL-3.0** ライセンスの下で公開されています。詳細は [`LICENSE`](./LICENSE) を参照してください。

End Knot は [Endless Host Roles](https://github.com/Gurge44/EndlessHostRoles) の派生プロジェクトです。**2026年4月以降の改変** は waffle-ful により行われており、改変履歴は本リポジトリの git log および [`CHANGELOG.md`](./CHANGELOG.md) で追跡できます。

## クレジット

- **[Endless Host Roles](https://github.com/Gurge44/EndlessHostRoles)** (Gurge44 他) — ベースMod、GPL-3.0
- **[TownOfHost-K](https://github.com/KYMario/TownOfHost-K)** (KYMario) — 移植元役職、GPL-3.0
- **[Town Of Host](https://github.com/tukasa0001/TownOfHost)** (tukasa0001 他) — READMEフォーマット参考
