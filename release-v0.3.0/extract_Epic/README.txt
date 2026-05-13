EndKnot v0.3.0-alpha (テスター配布版)
=========================================

EHR (https://github.com/Gurge44/EndlessHostRoles) の派生プロジェクトです。
ソースコード: https://github.com/waffle-ful/End-K-not
ライセンス: GNU GPL v3.0

== インストール手順 ==
1. Among Us を完全に終了する
2. このフォルダの中身すべてを Among Us インストールフォルダに上書き展開
   - Steam: <Steam>\steamapps\common\Among Us\
   - Epic:  C:\Program Files\Epic Games\AmongUs\
3. Among Us を起動する (ホストのみ MOD 必要、参加者は不要)

== BGM について ==
デフォルト BGM (menu / lobby) は DLL に同梱されているので、何もしなくても再生されます。
独自の楽曲に差し替えたい場合は以下のフォルダに OGG / MP3 / WAV を配置してください:

   <AmongUs>\BepInEx\resources\BGM\

  対応スロット:  menu  /  lobby  /  intask  /  climax  /  meeting  /  result
  (拡張子 .ogg .mp3 .wav いずれか)

ディスク上の同名ファイルが優先され、なければ同梱 BGM が使われます。
クレジット表示は同フォルダの bgm_titles.json で title / author を編集すると有効化されます。
両方空欄のままなら表示はスキップされます。

== カスタム SFX について (v0.3.0 新規) ==
従来 WAV のみだったカスタムサウンドが OGG / MP3 にも対応しました。
<AmongUs>\BepInEx\resources\ 以下に同名 .ogg / .mp3 / .wav を置けば優先順で読み込まれます。

== v0.3.0-alpha の主な変更 ==
- 新役職: Skinwalker (皮仲ばね魔, Impostor)
    死体に重なって通報すれば、その死体を被って完全に成り代われる。Doppelganger と違い
    プレイヤーレベルまでコピーする。ペットボタンで脱ぐと死体が同じ位置に再出現する。
    使用回数制限あり。
- 新役職: Sandbox (サンドボックス, Crewmate)
    ペットボタンで足元にブロックを設置する。ブロックは壁として機能し、触れたプレイヤー
    (自分を含む) は最も近い空きスペースに弾かれる。同時設置数は設定可能で、超えると
    一番古いブロックが消える。会議中は一時的に消えるが会議後に同じ位置に再出現。
- 闇鍋サポート (Chaos Pot Support) 新規:
    /yaminabe コマンドで陣営別の役職一覧を発表する機能。会議冒頭でホスト向け右上に
    陣営別ロスター表示。Impostor / Neutral / Coven / Crewmate それぞれ個別に表示 ON/OFF。
- カスタム SFX が WAV に加え OGG / MP3 対応。
- WaveCannon の発射準備中スキンを skin_rhm に変更 (透明 outfit 回避)。

詳細は CHANGELOG.md または git log を参照してください。

== 同梱物について ==
- Mini.RegionInstall.dll は別作者 (duikbo) のカスタムリージョン追加 MOD です。
  EHR 上流配布と同じく同梱しています。EndKnot 本体とは別ライセンスです。

== 既知の問題 / 注意事項 ==
- テスター段階のため、未テストの役職・機能があります (Skinwalker / Sandbox は実機検証前)
- バグ報告は Discord または GitHub Issues へお願いします
  Discord: https://discord.gg/sEYAFzD3a

== ライセンス ==
本ファイルは GNU General Public License v3.0 のもとで配布されています。
ソースコードは上記 GitHub リポジトリで入手できます。
