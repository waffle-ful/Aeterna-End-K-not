# EndKnot AI実況 相棒アプリ

配信画面に「AI実況の声」を乗せるためのおまけツールです。
End K not (mod) 本体とは別プロセスで動き、mod が書き出すイベントを読んで
Gemini の音声で実況します。**API キーは各自で発行して使ってください。**

## 一番かんたんな使い方 (ゲーム内から自動起動)

Python (3.10+、インストール時に "Add to PATH" にチェック) を入れて、
環境変数 `GEMINI_API_KEY` を設定した状態でゲーム内オプションの
**AI実況 (EnableAICommentary)** を ON にするだけです。mod が相棒アプリを
自動で起動します (初回のみ依存パッケージを自動インストール)。

- 実体は `Among Us/EndKnot_DATA/companion/` に書き出され、最小化された
  コンソールウィンドウで動きます (ログ確認はそこから)。
- オプション OFF かゲーム終了で自動的に止まります。
- `--voice` などの追加引数は BepInEx cfg の `AICommentaryArgs` に書けます
  (例: `--voice Kore --quiet-meeting --audio-device "CABLE Input"`)。
- キー未設定のときは起動せず、ロビーのチャットに設定手順を1回だけ表示します。

以下は手動で動かす場合の手順です。

## 必要なもの

- Python 3.10+
- Gemini API キー ([Google AI Studio](https://aistudio.google.com/) で無料発行)
- End K not 側の設定で `EnableAICommentary = true` (BepInEx の cfg)

## セットアップ

```powershell
cd tools/companion
pip install -r requirements.txt

# API キーを環境変数に設定 (システム環境変数に入れておけば毎回不要)
$env:GEMINI_API_KEY = "あなたのAPIキー"

python companion.py --events "C:\Path\To\Among Us\EndKnot_DATA\companion-events.jsonl"
```

`--events` には Among Us のインストールフォルダ直下にできる
`EndKnot_DATA\companion-events.jsonl` を指定します
(mod 起動後、`EnableAICommentary` が有効なら自動で作られます)。

## 配信への乗せ方

実況音声は既定の再生デバイスに出ます。OBS の「デスクトップ音声」がその
デバイスを拾っていればそのまま配信に乗ります。

さらに OBS 連携用のファイルを自動で出力します (置き場所は既定でこのフォルダ):

- `subtitle.txt` — いま実況が喋っている内容の字幕。OBS の
  「テキスト (GDI+)」ソースで「ファイルから読み取り」を指定するとリアルタイム字幕になります。
- `speaking.txt` — 発話中は `1`、無音時は `0`。立ち絵 (PNGTuber 風) の
  口パク切り替えなどに使えます。
- 出力先は `--subtitle` / `--speaking-file` で変更、`--no-obs-files` で無効化できます。

### 3D/Live2D アバターにリップシンクさせる場合

アバターアプリ (VTube Studio / VSeeFace 等) の音声リップシンクに直結するには、
実況音声を仮想オーディオケーブルへ出力します:

```powershell
# デバイス一覧を確認 (出力デバイスの名前か番号を控える)
python companion.py --list-audio-devices

# 例: VB-Audio Virtual Cable へ出力 (名前の一部でマッチします)
python companion.py --events "..." --audio-device "CABLE Input"
```

アバターアプリ側のマイク入力に `CABLE Output` を指定すれば、実況の声で
口が動きます。この場合 OBS には「アバターアプリの映像 + CABLE の音声」を
乗せてください (環境変数 `COMPANION_AUDIO_DEVICE` でも指定できます)。

## カスタマイズ

- 実況者の人格: 同じフォルダに `persona.txt` を置くと差し替えられます。
- モデル: `--model` 引数か環境変数 `GEMINI_LIVE_MODEL` で変更できます。
- ボイス: `--voice Kore` のように指定できます (例: Puck / Charon / Kore / Fenrir / Aoede)。
  環境変数 `GEMINI_VOICE` でも指定可。未指定ならモデル既定の声です。
- `--quiet-meeting` を付けると、会議中は場繋ぎとチャット読み上げを止めて
  プレイヤーの議論を邪魔しません。
- `--dormant-after 300` — 実イベント (参加/チャット等) がこの秒数無いと
  場繋ぎトークを休眠し、無人の放置時間帯に API 使用量を消費しません。
  イベントが来れば自動で再開します。

## 実況が拾うイベント

参加/退出、視聴者の !コマンド干渉、ロビー自動デモ、視聴者チャット、
試合開始 (マップ・人数)、緊急会議、サボタージュ発生、追放結果
(役職公開設定が有効な場合は役職名も)、試合結果 (勝利陣営・勝者名)。
いずれも「プレイヤー全員に公開済みの情報」だけを実況します
(キルの瞬間や生存者の役職などのネタバレはしません)。

## 会話ログ (変な発言の後追い用)

送った指示の全文と実況が実際に喋った内容 (文字起こし) を、このフォルダの
`companion-log.jsonl` に自動で追記します。「さっき変なこと言ってたな」を
後から文脈ごと確認できます。

- 1行 = 1レコードの JSONL。`kind` は `send` (こちらが送った指示全文) /
  `say` (実況の発話全文) / `session` (接続・再接続の区切り。引き継いだ
  あらすじ付き) / `session_end` (切断理由)。
- 時系列に並んでいるので、変な発言 (`say`) の直前の `send` と `session` を
  見れば「何を根拠に喋ったか」「再接続直後で文脈を失っていないか」が分かります。
- 出力先は `--conv-log` で変更、`--no-conv-log` で無効化できます。

## 注意

- 音声出力は API 使用量 (トークン) を消費します。無料枠を超える使い方をする
  場合は各自の課金設定を確認してください。
- API キーは自分だけのものです。配信画面やリポジトリに写さないように。
