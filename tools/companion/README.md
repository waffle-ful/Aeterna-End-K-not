# EndKnot AI実況 相棒アプリ

配信画面に「AI実況の声」を乗せるためのおまけツールです。
End K not (mod) 本体とは別プロセスで動き、mod が書き出すイベントを読んで
Gemini の音声で実況します。**API キーは各自で発行して使ってください。**

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

## カスタマイズ

- 実況者の人格: 同じフォルダに `persona.txt` を置くと差し替えられます。
- モデル: `--model` 引数か環境変数 `GEMINI_LIVE_MODEL` で変更できます。

## 注意

- 音声出力は API 使用量 (トークン) を消費します。無料枠を超える使い方をする
  場合は各自の課金設定を確認してください。
- API キーは自分だけのものです。配信画面やリポジトリに写さないように。
