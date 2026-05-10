# コマンド一覧

End K not で使えるチャットコマンドを権限別にまとめました。ゲーム内で `/help` を実行すると、現在の状況で使えるコマンドだけが表示されます。

## 表記

- **コマンド** 列にスラッシュで区切って複数並んでいるものは、どれを打っても同じ動作になります（例: `/r` と `/role` は同じ）。
- **引数** 列は `{}` が必須、`[]` が任意です。
- **タイミング** 列は実行できる場面の制限です。
  - 常時 — いつでも
  - ロビー — ロビー画面のみ
  - ゲーム中 — ラウンド進行中
  - 会議中 — 会議画面でのみ
  - 死亡後 — 自分が死亡したあと（ゲーム中限定）
  - 死亡後 / ロビー — 死亡後またはロビーのいずれか
- **権限** はセクションごとに分けています:
  - 全員 — 誰でも
  - Mod クライアント — End K not の DLL を導入したクライアントのみ
  - ホスト — ロビーホストのみ
  - ホスト / モデレーター — ホストおよび `Moderators.txt` 登録ユーザー
  - ホスト / 管理者 — ホストおよび `Admins.txt` 登録ユーザー
  - 役職コマンド・隠しコマンド — 特定の役職時のみ機能、または開発用 (`[dev]`)

## 全員が使用可能

| コマンド | 引数 | タイミング | 説明 |
|----------|------|-----------|------|
| /アナグラム | — | 死亡後 / ロビー | 1つのランダムに並び替えられたワードを得て、それから 元のワードの推測を 試してみよう。 |
| /ans / /answer | `{number}` | 会議中 | 数学者による 1つの出題された 数学の問題に答える |
| /ci / /chemistinfo | — | 常時 | 化学者の 利用可能なすべてのプロセスを表示する。 |
| /colour / /color | `{color}` | ロビー | あなたのスキンカラーをセットする |
| /combo | `{mode} {role} {addon} [all]` | 常時 | 常に もしくは 決して一緒に出現させない 役割 および 属性をセットする |
| /coveninfo | — | 常時 | View information on how the Coven faction works |
| /death / /d | `[id]` | 死亡後 | 誰があなたを殺害したか および その人の役割を見る |
| /draft | `{number}` | ロビー | ドラフトで 1つの役割を選択する |
| /dd / /draftdesc / /draftdescription | `{index}` | ロビー | ドラフトで あなたが得た役割の 1つの説明を見る |
| /8ball | `[question]` | 常時 | 魔法の8-ボールに 1つの質問を問う。 |
| /gm / /gml / /gamemodes / /gamemodelist | — | 常時 | modのすべてのゲームモードのリストを見る。 |
| /gno | `{number}` | 死亡後 / ロビー | 数字のミニ-ゲームを推測する |
| /h / /help | — | 常時 | コマンドのリストを見る |
| /hm | `{id}` | 死亡後 | メッセンジャーとして メッセージの1つを送信する |
| /id / /guesslist | — | 常時 | すべてのプレイヤーのIDを見る |
| /ic / /impct / /impstorchat | `{message}` | ゲーム中 | インポスターチームと死亡プレイヤーのみに見えるプライベートメッセージを送信 |
| /jc / /jacct / /jackalchat | `{message}` | ゲーム中 | ジャッカルチームと死亡プレイヤーのみに見えるプライベートメッセージを送信 |
| /gamestate / /gstate / /gs / /kcount / /kc | — | ゲーム中 | インポスター と ニュートラル キラーの数を見る |
| /l / /lastresult | — | ロビー | 最後のゲームでの プレイヤーの 役割 と 属性を見る |
| /lc / /loverchat / /loverschat | `{message}` | ゲーム中 | 恋人と死亡プレイヤーのみに見えるプライベートメッセージを送信 |
| /m / /myrole | — | ゲーム中 | あなたの役割の説明 および 設定を見る |
| /negotiation / /neg | `{number}` | 会議中 | 交渉人により 求められたときに あなたの交渉方法を選ぶ |
| /neutralinfo | — | 常時 | View information on how the Neutral faction works |
| /n / /now | — | 常時 | 現在の設定を見る |
| /pi / /playerinfo | `[id]` | 常時 | See a player's ID, friend code, hashed PUID, and platform info |
| /pv | `{vote}` | 常時 | 1つのpollに投票する |
| /qa | `{letter}` | 会議中 | クイズマスターによる 1つの出題された 問題に答える |
| /qs | — | 会議中 | クイズマスターによる 出題された 現在の問題を見る |
| /r | `[role]` | 常時 | 役割のリスト または 特定の役割の説明を見る |
| /ready | — | ロビー | 準備完了として 自分自身をマークする |
| /rn / /rename / /name | `{name}` | ロビー | あなたに 新しい名前を与える |
| /rl / /rolelist | — | 常時 | 派閥ごと および 出現する役割のサブカテゴリーは いくつの制限があるかを見る。 |
| /spectate | `[id]` | ロビー | 次のゲームで 1人の観戦者をあなたが作る。 あなたは ホストとして ゲームマスターを有効にしましょう。 |
| /t / /template | `{tag}` | 常時 | 1つのメッセージテンプレートを送信する (template.txtから) |
| /til / /timelimit | — | ゲーム中 | Show the remaining game time (when the time limit is enabled) |
| /tpin | — | ロビー | ロビーの中にテレポートする |
| /tpout | — | ロビー | ロビーの外にテレポートする |
| /vs / /votestart | — | ロビー | Vote to start the game |
| /win / /winner | — | ロビー | 最後のゲームの勝者を見る |
| /xor | `{role} {role}` | 常時 | 特定の 2 つの役割を 同じゲームで 決してスポーンさせない。 |
| /lt | — | ロビー | ロビータイマーを見る |

## Mod クライアント限定

End K not の DLL を導入したクライアントだけで使えるコマンド。バニラクライアントから打っても無視されます。

| コマンド | 引数 | タイミング | 説明 |
|----------|------|-----------|------|
| /achievements | — | 常時 | あなたが アンロックした および していない 実績を見れる |
| /cosid | `[id]` | 常時 | あなたのコスミックを見る |
| /csd | `{sound}` | 常時 | 1つのカスタムサウンドを再生する |
| /dump | — | 常時 | デスクトップに ログファイルをdumpする |
| /sd | `{sound}` | 常時 | ゲームから 1つのカスタムサウンドを再生する |
| /uiscale | `{scale}` | 常時 | Set the scale of the buttons in the UI |
| /v / /version | — | 常時 | End K notの現在のバージョンを見る |

## ホスト専用

ロビーのホスト（Mod 導入者）だけが使えるコマンド。

| コマンド | 引数 | タイミング | 説明 |
|----------|------|-----------|------|
| /addadmin | `{id}` | 常時 | Add a player to the list of admins |
| /addmod | `{id}` | 常時 | 指定されたプレイヤーを モレデーターにする |
| /addtag / /createtag | `{id} {color} {tag}` | 常時 | 1人のプレイヤーに 1つのタグを追加する (あなたのロビーでのみ 見える) (名前の列に-沿って、前に表示される)。 |
| /addvip | `{id}` | 常時 | VIPリストに 1人のプレイヤーを追加する |
| /changerole | `{role}` | ゲーム中 | あなたの役割を変更する |
| /cs / /changesetting | `{name} {?} [?]` | ロビー | ゲーム設定を変更する |
| /copypreset / /presetcopy | `{sourcepreset} {targetpreset}` | ロビー | 1つのプリセットから 別のプリセットへ すべての設定をコピーする。 |
| /deleteadmin | `{id}` | 常時 | Remove a player from the list of admins |
| /deletemod | `{id}` | 常時 | モレデーターのリストから 指定されたプレイヤーを削除する |
| /deletetag | `{id}` | ロビー | 1人のプレイヤーから 1つのタグを消去する。 |
| /deletevip | `{id}` | 常時 | VIPリストから 1人のプレイヤーを削除する |
| /dis / /disconnect | `{team}` | ゲーム中 | 指定されたチームの すべてのプレイヤーを除外する |
| /eff / /effect | `{effect}` | ゲーム中 | 自分自身に 1つのランダマイザーの効果を適用する |
| /enableallroles | — | ロビー | すべての 役割 と 属性を有効にする |
| /givekill / /gk | `{id}` | ロビー | ロビーでプレイヤーにキル能力を付与する。 |
| /hn / /hidename | — | ロビー | あなたの名前を隠す |
| /hw / /hwhisper | `{id} {message}` | 常時 | ホストとして 1人のプレイヤーに 1つの助けを乞うメッセージをささやく。 |
| /暗殺 | `{id}` | 常時 | 1人のプレイヤーを殺害する |
| /level | `{level}` | ロビー | あなたのレベルを変更する |
| /mw / /messagewait | `{duration}` | 常時 | システムメッセージの バッファ時間をセットする |
| /mt / /hy | — | ゲーム中 | 会議を呼ぶ あるいは 現在の会議を終了する |
| /復活 | `{id}` | ゲーム中 | Revives a dead player. Requires special access or No Game End enabled. |
| /setrole / /setaddon | `{id} {role}` | ロビー | 次のゲームでの プレイヤーの 役割/属性をセット |
| /up | `{role}` | ロビー | [非推奨] 代わりに /setrole または /setaddon を使用しましょう |

## ホスト / モデレーター

ホスト、または `Among Us/BepInEx/EndKnot_DATA/Moderators.txt` に登録された FriendCode を持つプレイヤーが使えるコマンド。

| コマンド | 引数 | タイミング | 説明 |
|----------|------|-----------|------|
| /ban / /kick | `{id} [reason]` | 常時 | ロビーから 1人のプレイヤーを Ban または Kickする |
| /draftstart / /ds | — | ロビー | ドラフトを開始する、その間は 次のゲームの彼らの役割を選べます |
| /fix / /blackscreenfix / /fixblackscreen | `{id}` | ゲーム中 | 特定のプレイヤーの暗転バグを修正する。 |
| /gmp / /gmpoll / /pollgm / /gamemodepoll | — | ロビー | 次のゲームモードのための 投票させる 1つのpollを開始する。 |
| /mp / /mpoll / /pollm / /mappoll | — | ロビー | Start a poll to vote for the next map. |
| /mute | `{id} [duration]` | 死亡後 / ロビー | 誰かをミュートにする。 ミュートされたプレイヤーは チャットができません。 ゲームの開始時に 全員をミュート解除します。 |
| /poll | `{question} {answerA} {answerB} [answerC] [answerD]` | 常時 | 1つのpollを始める |
| /rc / /readycheck | — | ロビー | 開始の準備確認をする (誰が AFKかを確認) (30s後に 結果が表示される) |
| /say / /s | `{message}` | 常時 | ホスト/モデレーターとして 1つのメッセージを送る |
| /start | — | ロビー | Starts the game |

## ホスト / 管理者

ホスト、または `Among Us/BepInEx/EndKnot_DATA/Admins.txt` に登録された FriendCode を持つプレイヤーが使えるコマンド。モデレーターより上位の権限。

| コマンド | 引数 | タイミング | 説明 |
|----------|------|-----------|------|
| /afkexempt | `{id}` | 常時 | AFK検出による確認から 1人のプレイヤーを除外する |
| /end | — | ゲーム中 | ゲームを修了する |
| /exe | `{id}` | 常時 | 1人のプレイヤーを処刑する |
| /os / /optionset | `{chance} {role}` | ロビー | 1つの役割の出現確率をセットする |
| /unmute | `{id}` | 常時 | 誰かのミュートを解除する。 |

## 役職コマンド・隠しコマンド

特定の役職時にだけ機能するコマンド、または開発・デバッグ用 (`[dev]`)。`/help` には通常表示されません。

| コマンド | 引数 | タイミング | 説明 |
|----------|------|-----------|------|
| /ask | `{number1} {number2}` | 会議中 | 数学者として 1つの問題を出題する |
| /assume | `{id} {number}` | 会議中 | 想定者として 1人のプレイヤーに いくつ投票を受け取るかを想定する |
| /cd / /setcd | `[id] {seconds}` | ゲーム中 | キルクールダウンをライブで設定 [dev] |
| /chat | `{message}` | 会議中 | 腹話術師として あなたのターゲットに 1つのメッセージを送信させる |
| /check | `{id} {role}` | 会議中 | 質問者として 1人のプレイヤーが その役割を持っているかどうか確認する |
| /choose / /pick | `{role}` | 会議中 | すべてのタスクを完了した後のポーンとして あなたの役割を選択する。 |
| /daybreak / /db | — | 会議中 | スタースポーンとして 現在の会議に夜明けを呼ぶ。 |
| /dn / /deathnote | `{name}` | 会議中 | ノートキラーとして 誰かの本名を推測する |
| /decree | `{number}` | 会議中 | 大統領として 1つの法令に使用する |
| /devtp | `{x} {y} [id]` | ゲーム中 | プレイヤーを座標へテレポート [dev] |
| /devtpto | `{srcId} {dstId}` | ゲーム中 | プレイヤーを別のプレイヤーの位置へテレポート [dev] |
| /dummy | `[count]` | ゲーム中 | 視覚専用ダミーマーカーを出現させる [dev] |
| /fabricate | `{deathreason}` | 会議中 | Set a fake death reason for your next kill as the Fabricator |
| /forge | `{id} {role}` | 会議中 | 偽造者として、1人のプレイヤーを 1つの偽の役割で偽造する。 |
| /imitate | `{id}` | 会議中 | Imitate a dead player's role as the Imitator |
| /inspect | `[id]` | ゲーム中 | プレイヤーの内部ロール状態をダンプ [dev] |
| /jt / /jailtalk | `{message}` | 会議中 | 投獄されたプレイヤー または 反対として 牢屋で話す。 |
| /lobbyKillAction | `{targetId}` | ロビー | (内部 RPC: lobby kill 同期用) |
| /note | `{action} [?]` | 会議中 | ジャーナリストとして あなたのノートを管理する |
| /optdump | `[tab]` | 常時 | タブの全オプション値をダンプ [dev] |
| /ret / /retribute | `{id}` | 会議中 | Guess who killed the player you camped as the Retributionist |
| /select | `{id} {role}` | 会議中 | Select your impostor partner and their role. |
| /summon | `{id}` | 会議中 | Summon a player to kill for you as the Summoner |
| /ターゲット | `{id}` | 会議中 | 腹話術師として 1人のプレイヤーをターゲットにする |
| /undummy | — | ゲーム中 | 全ダミーマーカーを消去する [dev] |
| /投票 | `{id}` | 会議中 | 1人のプレイヤーの投票する |
| /w / /whisper | `{ids} {message}` | 会議中 | Whisper a message privately to the specified players. |

---

> このドキュメントはソース (`Patches/ChatCommandPatch.cs` の `LoadCommands()` および `Resources/Lang/ja_JP.jsonc`) から自動抽出して整形しています。一部の説明文に英語表記が残っているのは、対応する `CommandDescription.<key>` の日本語訳が未整備のためです。
