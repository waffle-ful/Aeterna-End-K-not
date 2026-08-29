// 役職ロジック用 Blockly ブロック定義 (docs/ekr-logic-spec.md §2〜§4 のブロック UI 化)。
// zelos レンダラー素のまま・カテゴリ標準色 (イベント=黄/動き=青/見た目=紫/制御=橙/変数=赤 —
// spec §7)。ここでは Blockly 標準の HSV 色相 (0〜360) をそのまま使う: LOGIC_HUE=210 等の
// Blockly 自身の標準カテゴリ色相と同じ考え方 (実測: node_modules/blockly/msg/en.js の
// VARIABLES_HUE=330 は「赤」寄りの色相で、spec の「変数=赤」と一致する)。
//
// 5カテゴリへの opcode 割り当ては spec に明記が無いため、このファイルが実質的な定義になる
// (「制御」に kill/set_kill_cooldown を入れているのは、専用カテゴリが無い中で「役職の状態を
// 変える」opcode の置き場として一番近いため。「変数」には演算子ブロック [add/sub/... ] も
// まとめて入れている — spec の5分類に「演算子」枠が無いため、データを扱うという共通点で変数と
// 同居させている)。
//
// このファイルは Blockly ランタイム (DOM 必須) に依存するため、vitest (environment:"node") からは
// 一切 import しないこと。role-maker.ts が「ロジック」タブを初めて開いたときにだけ動的 import する。
//
// Apache-2.0 帰属表示は editor/ASSET_CREDITS.md 参照。「Scratch」の名称・ロゴは使わない
// (呼称は常に「ブロック」)。

import * as Blockly from "blockly/core";
import "blockly/blocks"; // math_number/logic_boolean 等の標準ブロックを Blockly.Blocks へ登録する副作用 import
import * as jaMsg from "blockly/msg/ja";
import { ALIVE_COUNT_VALUE_MAX, ALIVE_COUNT_VALUE_MIN, LOGIC_WHEN_VALUES, RECRUIT_SLOT_MAX, type LogicWhen } from "../roledef";

// 標準ブロック (logic_boolean 等) はラベルを %{BKY_...} メッセージキーで持つため、ロケールを
// ロードしないと生キーがそのまま表示される。カスタムブロックは全て日本語直書きなので影響しない。
Blockly.setLocale(jaMsg as unknown as { [key: string]: string });
// 公式 ja の「真/偽」は他ブロックのひらがな文言 (「じぶんが死んだとき」等) から浮くため上書き
Blockly.Msg["LOGIC_BOOLEAN_TRUE"] = "ほんとう";
Blockly.Msg["LOGIC_BOOLEAN_FALSE"] = "うそ";

// カテゴリ標準色 (0〜360 の HSV 色相)
const HUE_EVENT = 45; // イベント = 黄
const HUE_MOTION = 210; // 動き = 青
const HUE_LOOKS = 290; // 見た目 = 紫
const HUE_CONTROL = 20; // 制御 = 橙
const HUE_VAR = 330; // 変数 = 赤 (Blockly 標準の VARIABLES_HUE と同値)
// v1.2 (spec §6/§7 2026-08-10 追記) — 「ひっさつわざ」カテゴリ (マーカー/相手ワープ/ポータル)。
// v1.3 (spec §3 2026-08-11 追記) で pull/drag/field (ひっぱる・ひきずる・フィールド) を同カテゴリに追加。
// 既存5色 (45/210/290/20/330) と衝突しない色相を選ぶ。
const HUE_ULTIMATE = 150; // ひっさつわざ = 緑
// Wave 2 (docs/ekn-wave2-contract.md 2026-08-11 追記) — 「しらべる」(情報開示) と「とうひょう」
// (会議・投票操作) の2カテゴリを追加。既存6色 (45/210/290/20/330/150) の隙間から選ぶ
// (45〜150 の隙間に HUE_INFO、210〜290 の隙間に HUE_MEETING)。
const HUE_INFO = 100; // しらべる = 黄緑
const HUE_MEETING = 260; // とうひょう = 青紫
// Wave 4 (docs/ekn-wave4-contract.md 2026-08-25) — 「つなぐ」(リンク・勧誘) カテゴリ。
// 既存8色 (45/210/290/20/330/150/100/260) の隙間 (150〜210) から選ぶ — 動き (210) と
// ひっさつわざ (150) のあいだで視覚的に区別できる青緑。
const HUE_LINK = 190; // つなぐ = 青緑

// R2 (docs/ekn-r2-contract.md §3b): on_attacked の「こうげきのしゅるい」と on_death の「死にかた」。
// 先頭の "" は「すべて」= AST でフィールドごと省略する (= 全種にマッチ)。
const ATTACK_KIND_OPTIONS: [string, string][] = [
    ["こうげき (ぜんぶ)", ""],
    ["キル", "kill"],
    ["かんせつこうげき", "indirect"],
    ["きょうせいキル", "force"],
    ["すいそく", "guess"],
];

const DEATH_CAUSE_OPTIONS: [string, string][] = [
    ["なにか (ぜんぶ)", ""],
    ["キル", "kill"],
    ["ついほう", "vote"],
    ["すいそく", "guess"],
    ["ばくはつ", "bomb"],
    ["どく・のろい", "poison-curse"],
    ["かんきょう", "environment"],
    ["じさつ", "suicide"],
    ["そのほか", "other"],
];

// on_cno_touch は動的な SLOT ドロップダウンを持つため WHEN_LABELS/WHEN_TOOLTIPS には残すが、
// jsonBlockDefs 側では他のイベントと分けて個別のブロック定義を書く (下記参照)。
// Wave 3 (docs/ekn-wave3-contract.md §1 2026-08-14) — 状態条件トリガのちょうど/いか/いじょう。
// on_var / on_alive_count のどちらの比較にも使う (見え方が違うだけの同じ3値)。
const CMP_OPTIONS: [string, string][] = [
    ["ちょうど", "eq"],
    ["いか", "le"],
    ["いじょう", "ge"],
];

// Wave 4 (docs/ekn-wave4-contract.md §1) — on_near/on_far の はんい (radius tier) と あいて (who)。
// radius の3語は「ブラックホール」(field) と同じ字面だが別スケール (契約 §1.2「同語別値」) なので、
// tooltip で別ものであることを明示する。
const NEAR_RADIUS_OPTIONS: [string, string][] = [
    ["ちかく", "small"],
    ["そこそこ", "medium"],
    ["とおく", "large"],
];
const NEAR_WHO_OPTIONS: [string, string][] = [
    ["だれでも", "anyone"],
    ["つないだ人", "linked"],
    ["おぼえた人1", "saved1"],
    ["おぼえた人2", "saved2"],
];
// on_far は「だれでも」を出さない (who 必須・anyone は検証 reject — 契約 §1.3)。
const FAR_WHO_OPTIONS: [string, string][] = NEAR_WHO_OPTIONS.filter(([, v]) => v !== "anyone");

export const WHEN_LABELS: Record<LogicWhen, string> = {
    on_game_start: "ゲームが始まったとき",
    // Wave 1 (spec §2 2026-08-11): 発動トリガ統合により「ペット」ではなく汎用の発動ボタンを指す
    // ラベルへ変更 (AST の id `on_pet` は不変 — 表示だけの変更)。
    on_pet: "とくいわざボタンをおしたとき",
    on_kill: "キルしたとき",
    on_death: "じぶんが死んだとき",
    on_meeting_start: "会議が始まったとき",
    on_meeting_end: "会議が終わったとき",
    on_task_complete: "タスクを1つ終えたとき",
    on_vent_enter: "ベントに入ったとき",
    on_report: "死体を通報したとき",
    on_second: "毎秒くりかえす",
    on_cno_touch: "オブジェクトにだれかが触れたとき",
    on_attacked: "こうげきされたとき",
    // Wave 2 (docs/ekn-wave2-contract.md §1.1/§1.2 2026-08-11)
    on_meeting_vote: "かいぎで投票したとき",
    on_meeting_pick: "かいぎであいてをえらんだとき",
    // Wave 3 (docs/ekn-wave3-contract.md §1 2026-08-14) — 状態条件トリガ。
    on_var: "へんすうが条件になったとき",
    on_alive_count: "いきのこりが◯人になったとき",
    on_vent_exit: "ベントから出たとき",
    // Wave 4 (docs/ekn-wave4-contract.md §1〜§3 2026-08-25) — つなぐ。
    on_near: "だれかが 近づいたとき",
    on_far: "あのひとが はなれたとき",
    on_room_enter: "へやに 入ったとき",
    on_room_exit: "へやから 出たとき",
    on_linked_death: "つないだ人が 死んだとき",
    // Wave 6 (docs/ekn-wave6-contract.md §2/§3 2026-08-29) — 残イベント2種。
    on_sabotage: "だれかがサボタージュをおこしたとき",
    on_revive: "いきかえったとき",
};

const WHEN_TOOLTIPS: Record<LogicWhen, string> = {
    on_game_start: "タスクフェーズが始まったときに1回だけ実行します。「秒待つ」を挟んだ続きは会議が始まると取り消されるので、開始すぐに会議になる設定だと動かないことがあります。",
    on_pet: "とくいわざのボタン (能力ボタン) を発動したときに実行します。",
    on_kill: "自分のキルが成立した直後に実行します。",
    on_death: "自分が死亡したときに実行します (追放されたときや会議で亡くなったときも含みます。切断は含みません)。",
    on_meeting_start: "会議がはじまるとき (会議画面が開く直前) に実行します。通報・緊急ボタンのどちらでも発火します。",
    on_meeting_end: "追放処理のあと、タスクが再開するときに実行します。",
    on_task_complete: "自分がタスクを1つ終えるたびに実行します。見せかけのタスク (インポスターなどの偽タスク) では発火しません。",
    on_vent_enter: "自分がベントに入れたとき (封鎖などの妨害をくぐり抜けて実際に入れたとき) に実行します。",
    // Wave 3 (docs/ekn-wave3-contract.md §2 2026-08-14): 合成通報 (他の役職の能力・コマンドが
    // 起こす偽装通報) では発火しないことを明記した文言へ更新。
    on_report: "自分が死体を見つけて通報したときに実行します (他の役職の能力で会議になったときは実行されません)。",
    on_second: "タスク中、自分が生きている間、毎秒くりかえし実行します (処理が重いことはしないでね)。",
    on_cno_touch: "自分が出したオブジェクト(スロットで指定)に、生きているプレイヤーが触れたときに実行します。触れた人が「相手」になります。一度触れると、その人が離れるまで(または離れてから触れ直すまで)は再発火しません。",
    // Wave 1 (spec §2 2026-08-11)。同期プロローグ (最初の「秒待つ」までしか攻撃を止められない)
    // をユーザーの言葉で言い切る一文にする — リンタ L17 の文言と同じ趣旨。
    on_attacked: "このときの「あいて」= 攻撃してきた人。ふせぐのは一番はじめに置こう",
    // Wave 2 (docs/ekn-wave2-contract.md §1.1/§1.2 2026-08-11)
    on_meeting_vote: "このときの「あいて」= 投票した相手 (スキップでは発火しません)。票を取り消せるのは一番はじめだけです。",
    on_meeting_pick: "このときの「あいて」= えらんだ相手 (会議のボタン、または /pick コマンドどちらでも発火します)。票そのものには関係ありません。",
    // Wave 3 (docs/ekn-wave3-contract.md §1 2026-08-14) — 状態条件トリガ (エッジ発火・共通意味論)。
    on_var: "この変数の値が指定した条件になったしゅんかんに1回だけ実行します。条件を満たしたままだと再発火せず、いちど条件から外れてからまた満たすと、もう1回発火します。ゲーム開始時にすでに条件を満たしていても、そのときは発火しません。",
    on_alive_count: "生きている人数が指定した条件になったしゅんかんに1回だけ実行します (ダミー人形は数えません)。自分が死んだあとは発火しません。条件を満たしたままだと再発火せず、いちど条件から外れてからまた満たすと、もう1回発火します。",
    // 契約 §1.4: 「ベントから出たときに実行します (追い出されたときも含みます)。」の verbatim に加え、
    // 「on_vent_enter とのペアは保証しない (入ったときと数が合わないことがあるよ)」を tooltip に
    // 明記する要件が別途あるため、注意書きの一文を続ける (2つの要件を1文で満たす)。
    on_vent_exit: "ベントから出たときに実行します (追い出されたときも含みます)。入ったときと数が合わないことがあるよ。",
    // Wave 4 (docs/ekn-wave4-contract.md §1〜§3 2026-08-25) — つなぐ。radius は field (ブラックホール)
    // と同語別値 (契約 §1.2) なので「別もの」の一文を必ず残すこと。
    on_near: "選んだ範囲に生きている人が入ったときに1回だけ実行します (このときの「あいて」= 近づいた人)。その人が出ていくまでは、もう一度は実行されません。ワープでとなりに現れた人には反応しません (歩いて近づいたときだけ)。はんいは「ブラックホール」の大きさとは別ものだよ。",
    on_far: "つないだ人/おぼえた人が、いちど近づいてから はなれたときに1回実行します (このときの「あいて」= はなれた人)。さいしょから遠いときは実行されません。はんいは「ブラックホール」の大きさとは別ものだよ。",
    on_room_enter: "名前のある部屋に入ったときに実行します。ろうか・外は部屋ではありません。ベントやワープで入っても実行されます。",
    on_room_exit: "名前のある部屋から出たときに実行します。ろうか・外は部屋ではありません。ベントやワープで出ても実行されます。",
    on_linked_death: "「このひとと つなぐ」でつないだ人が死んだときに1回実行します (このときの「あいて」= 死んだ人)。切断でいなくなったときは実行されません。",
    // Wave 6 (docs/ekn-wave6-contract.md §2/§3 2026-08-29) — 残イベント2種。
    on_sabotage: "だれかがサボタージュ (電気・酸素・爆弾・通信など) を成功させたときに実行します (このときの「あいて」= サボタージュをおこした人)。だれの役職でも、生きている全員に実行されます。おなじサボタージュを連打しても、しばらくは続けて実行されません。",
    on_revive: "自分が生き返ったときに実行します (「あいて」はいません)。変数やここまでの進み具合はそのまま続きます。",
};

// ---------------------------------------------------------------------------
// 統一セレクタ語彙 (spec §3 — UI ラベルは表の日本語をそのまま使う)
// ---------------------------------------------------------------------------
// 単数セレクタ: kill / teleport_other / remember など「1人に効く」op はこれだけを出す
// (複数形を出さないのがブロック側の型規律 — 検証側も reject する)。
const TARGET_SINGLE_OPTIONS: [string, string][] = [
    ["じぶん", "self"],
    ["あいて", "ctx"],
    // Wave 4 (docs/ekn-wave4-contract.md §3.4): つないだ人 — saved1/2 が出るすべてのドロップダウン
    // (kill/teleport_other/remember/inspect/reveal/arrow_show/vote_block/exile/notify) に出す。
    ["つないだ人", "linked"],
    ["おぼえた人1", "saved1"],
    ["おぼえた人2", "saved2"],
    ["いちばん近くの人", "nearest"],
    ["だれか (ランダム)", "random"],
];
// teleport_other は「相手を飛ばす」op なので じぶん を出さない (spec §3 のアクション表どおり)。
const TARGET_OTHER_OPTIONS: [string, string][] = TARGET_SINGLE_OPTIONS.filter(([, v]) => v !== "self");
// Wave 5 (docs/ekn-wave5-contract.md §2): recruit の「かえるさき」。先頭の "" = 「じぶんとおなじ」で、
// compile-role が正準形からフィールドごと省略する (ATTACK_KIND_OPTIONS の「すべて」と同じ作法)。
// スロット番号はロビー構成への相対参照 (中身はホストが決める) なので、絶対語彙の禁止には触れない。
const RECRUIT_SLOT_OPTIONS: [string, string][] = [
    ["じぶんとおなじ", ""],
    ...Array.from({ length: RECRUIT_SLOT_MAX }, (_, i) => [`スロット${i + 1}`, String(i + 1)] as [string, string]),
];
// 複数セレクタを出せるのは notify だけ (Wave 1 のホワイトリスト)。
const TARGET_NOTIFY_OPTIONS: [string, string][] = [
    ...TARGET_SINGLE_OPTIONS,
    ["みんな", "all"],
    ["おなじ部屋のみんな", "room"],
];
// 空間セレクタ「どこ」に追加された cno1..3 (spec §3)。
const CNO_PLACE_OPTIONS: [string, string][] = [
    ["じぶんのオブジェクト1のところ", "cno1"],
    ["じぶんのオブジェクト2のところ", "cno2"],
    ["じぶんのオブジェクト3のところ", "cno3"],
];

// ---------------------------------------------------------------------------
// 変数ドロップダウン (Blockly 標準の変数機構は使わず、role-maker.ts が管理する
// spec の variables 配列と対応させる — 決定事項: 自前の軽量ドロップダウンで実装)。
// ---------------------------------------------------------------------------

let availableVariableNames: string[] = [];

/** role-maker.ts の変数リストが変わるたびに呼ぶ。ドロップダウン内の選択肢を更新する。 */
export function setAvailableVariableNames(names: readonly string[]): void {
    availableVariableNames = [...names];
}

function variableDropdownOptions(): Blockly.MenuOption[] {
    if (availableVariableNames.length === 0) return [["(変数がありません)", "__none__"]];
    return availableVariableNames.map((n): Blockly.MenuOption => [n, n]);
}

// ---------------------------------------------------------------------------
// ブロック定義 (JSON 形式で書けるものはまとめて登録、動的ドロップダウンが要るものだけ命令形で登録)
// ---------------------------------------------------------------------------

function jsonBlockDefs(): unknown[] {
    // on_cno_touch は動的な SLOT ドロップダウン (1..3) を持つ唯一のイベント (spec §2 v1.2) —
    // 他の when と同じ汎用テンプレートには乗せず、個別のブロック定義を書く。
    // R2 (docs/ekn-r2-contract.md §3b): on_attacked / on_death も任意フィルタのドロップダウンを
    // 持つので、on_cno_touch と同じく汎用テンプレートから外して個別に書く。
    // Wave 3 (docs/ekn-wave3-contract.md §1 2026-08-14): on_alive_count も cmp/value のドロップダウン+
    // 数値欄を持つので同様に個別定義。on_var はさらに変数ドロップダウンが動的 (変数リストの増減に
    // 追随する必要がある) ため、ekr_expr_var/ekr_do_var_set と同じく defineDynamicVariableBlocks()
    // 側で命令形登録する (on_vent_exit はフィールドを持たないのでこのフィルタに含めず生成のまま)。
    // Wave 4 (docs/ekn-wave4-contract.md §1/§3): on_near (RADIUS+WHO)・on_far (RADIUS+WHO)・
    // on_linked_death (CAUSE) もドロップダウンを持つので個別定義 (on_room_enter/on_room_exit は
    // フィールドを持たないので生成のまま)。
    const eventBlocks = LOGIC_WHEN_VALUES
        .filter((when) => when !== "on_cno_touch" && when !== "on_attacked" && when !== "on_death" && when !== "on_alive_count" && when !== "on_var"
            && when !== "on_near" && when !== "on_far" && when !== "on_linked_death")
        .map((when) => ({
            type: `ekr_when_${when}`,
            message0: WHEN_LABELS[when],
            nextStatement: null,
            colour: HUE_EVENT,
            tooltip: WHEN_TOOLTIPS[when],
        }));

    return [
        ...eventBlocks,
        {
            type: "ekr_when_on_cno_touch",
            message0: "オブジェクト %1 にだれかが触れたとき",
            args0: [{ type: "field_dropdown", name: "SLOT", options: [["1", "1"], ["2", "2"], ["3", "3"]] }],
            nextStatement: null,
            colour: HUE_EVENT,
            tooltip: WHEN_TOOLTIPS.on_cno_touch,
        },
        {
            type: "ekr_when_on_attacked",
            message0: "%1 されたとき",
            args0: [{ type: "field_dropdown", name: "KIND", options: ATTACK_KIND_OPTIONS }],
            nextStatement: null,
            colour: HUE_EVENT,
            tooltip: WHEN_TOOLTIPS.on_attacked,
        },
        {
            type: "ekr_when_on_death",
            message0: "じぶんが %1 で死んだとき",
            args0: [{ type: "field_dropdown", name: "CAUSE", options: DEATH_CAUSE_OPTIONS }],
            nextStatement: null,
            colour: HUE_EVENT,
            tooltip: WHEN_TOOLTIPS.on_death,
        },
        // Wave 3 (docs/ekn-wave3-contract.md §1.3 2026-08-14) — いきのこりが◯人になったら。
        {
            type: "ekr_when_on_alive_count",
            message0: "いきのこりが %1 人 %2 になったら",
            args0: [
                { type: "field_number", name: "VALUE", value: 3, min: ALIVE_COUNT_VALUE_MIN, max: ALIVE_COUNT_VALUE_MAX, precision: 1 },
                { type: "field_dropdown", name: "CMP", options: CMP_OPTIONS },
            ],
            inputsInline: true,
            nextStatement: null,
            colour: HUE_EVENT,
            tooltip: WHEN_TOOLTIPS.on_alive_count,
        },
        // Wave 4 (docs/ekn-wave4-contract.md §1.2/§1.3) — 対人近接。on_near の WHO は「だれでも」
        // 込み・on_far は「だれでも」抜き (anyone は検証 reject)。
        {
            type: "ekr_when_on_near",
            message0: "%1 が 近づいたとき (はんい %2 )",
            args0: [
                { type: "field_dropdown", name: "WHO", options: NEAR_WHO_OPTIONS },
                { type: "field_dropdown", name: "RADIUS", options: NEAR_RADIUS_OPTIONS },
            ],
            inputsInline: true,
            nextStatement: null,
            colour: HUE_EVENT,
            tooltip: WHEN_TOOLTIPS.on_near,
        },
        {
            type: "ekr_when_on_far",
            message0: "%1 が はなれたとき (はんい %2 )",
            args0: [
                { type: "field_dropdown", name: "WHO", options: FAR_WHO_OPTIONS },
                { type: "field_dropdown", name: "RADIUS", options: NEAR_RADIUS_OPTIONS },
            ],
            inputsInline: true,
            nextStatement: null,
            colour: HUE_EVENT,
            tooltip: WHEN_TOOLTIPS.on_far,
        },
        // Wave 4 (契約 §3.3) — つないだ人の死。CAUSE は on_death と同じ8バケット +「ぜんぶ」
        // (空文字 = フィールドごと省略) のドロップダウンを流用する。
        {
            type: "ekr_when_on_linked_death",
            message0: "つないだ人が %1 で死んだとき",
            args0: [{ type: "field_dropdown", name: "CAUSE", options: DEATH_CAUSE_OPTIONS }],
            nextStatement: null,
            colour: HUE_EVENT,
            tooltip: WHEN_TOOLTIPS.on_linked_death,
        },

        // 制御
        {
            type: "ekr_if",
            message0: "もし %1 なら",
            args0: [{ type: "input_value", name: "COND" }],
            message1: "%1",
            args1: [{ type: "input_statement", name: "THEN" }],
            previousStatement: null,
            nextStatement: null,
            colour: HUE_CONTROL,
            tooltip: "条件が正しい (0 以外) ときだけ、中の処理を実行します。",
        },
        {
            type: "ekr_if_else",
            message0: "もし %1 なら",
            args0: [{ type: "input_value", name: "COND" }],
            message1: "%1",
            args1: [{ type: "input_statement", name: "THEN" }],
            message2: "それ以外なら",
            message3: "%1",
            args3: [{ type: "input_statement", name: "ELSE" }],
            previousStatement: null,
            nextStatement: null,
            colour: HUE_CONTROL,
            tooltip: "条件が正しい (0 以外) ならこちら、そうでなければ「それ以外なら」の中を実行します。",
        },
        {
            type: "ekr_do_wait",
            message0: "%1 秒待つ",
            args0: [{ type: "field_number", name: "SECONDS", value: 1, min: 0.1, max: 600, precision: 0.1 }],
            previousStatement: null,
            nextStatement: null,
            colour: HUE_CONTROL,
            tooltip: "指定した秒数だけ、ここで一時停止します (0.1〜600 秒)。待っている途中で会議が始まると、続きは取り消されます。",
        },
        {
            type: "ekr_do_stop",
            message0: "ここで止める",
            previousStatement: null,
            nextStatement: null,
            colour: HUE_CONTROL,
            tooltip: "この「〜したとき」の処理をここで終わらせます (これより下は実行しません)。",
        },
        {
            type: "ekr_do_kill",
            message0: "%1 をキルする",
            // Wave 1 (spec §3): 単数セレクタ全種を受理 (複数形は出さない = 型規律)。
            args0: [{ type: "field_dropdown", name: "TARGET", options: TARGET_SINGLE_OPTIONS }],
            previousStatement: null,
            nextStatement: null,
            colour: HUE_CONTROL,
            tooltip: "えらんだ人をキルします。「あいて」はこのできごとの相手 (いなければ何も起きません)、「おぼえた人」は「この人をおぼえる」で保存した人です (死んだり切断したりすると忘れます)。",
        },
        // Wave 1 (spec §3 2026-08-11) — こうげきをふせぐ。「こうげきされたとき」の中でしか使えない
        // (他のイベントに入れるとコードを書き出せない = 検証エラーになる)。
        {
            type: "ekr_do_cancel_attack",
            message0: "こうげきをふせぐ",
            previousStatement: null,
            nextStatement: null,
            colour: HUE_CONTROL,
            tooltip: "いま受けているこうげきを取り消します。「こうげきされたとき」の中だけで使えて、しかも「秒待つ」より前に置かないと間に合いません (ふせぐのは一番はじめに)。",
        },
        {
            type: "ekr_do_set_kill_cooldown",
            message0: "キルのクールダウンを %1 秒にする",
            args0: [{ type: "field_number", name: "SECONDS", value: 25, min: 1, max: 300, precision: 1 }],
            previousStatement: null,
            nextStatement: null,
            colour: HUE_CONTROL,
            tooltip: "今後ずっと使うキルクールダウンの秒数を変更します (1〜300 秒)。今すぐ反映され、これ以降キルを使うたびにこの秒数が基準になります。",
        },

        // 動き
        {
            type: "ekr_do_teleport",
            message0: "%1 にワープする",
            args0: [{
                type: "field_dropdown", name: "TO", options: [
                    ["ランダムな場所", "random"], ["相手の場所", "ctx"],
                    ["マーカー1", "marker1"], ["マーカー2", "marker2"], ["マーカー3", "marker3"], ["マーカー4", "marker4"],
                    // Wave 1 (spec §3): 空間セレクタに cno1..3 を追加
                    ...CNO_PLACE_OPTIONS,
                ],
            }],
            previousStatement: null,
            nextStatement: null,
            colour: HUE_MOTION,
            tooltip: "ランダムな場所、相手 (このできごとに相手がいるとき) の場所、「いまの場所をおぼえる」で保存したマーカーの場所、または自分が出したオブジェクトの場所にワープします。マーカーが未保存だったりオブジェクトを出していなかったりすると何も起きません。",
        },
        {
            type: "ekr_do_speed",
            message0: "スピードを %1 倍にする (%2 秒間)",
            args0: [
                { type: "field_number", name: "MULT", value: 1.5, min: 0.5, max: 3.0, precision: 0.1 },
                { type: "field_number", name: "SECONDS", value: 5, min: 1, max: 60, precision: 1 },
            ],
            inputsInline: true,
            previousStatement: null,
            nextStatement: null,
            colour: HUE_MOTION,
            tooltip: "指定した秒数だけ、移動スピードを変更します (0.5〜3.0 倍・1〜60 秒)。",
        },
        {
            type: "ekr_do_cno_move",
            message0: "オブジェクト %1 をスポーン地点から (%2 , %3 ) の場所へ動かす",
            args0: [
                { type: "field_dropdown", name: "SLOT", options: [["1", "1"], ["2", "2"], ["3", "3"]] },
                { type: "field_number", name: "DX", value: 0, min: -50, max: 50, precision: 1 },
                { type: "field_number", name: "DY", value: 0, min: -50, max: 50, precision: 1 },
            ],
            inputsInline: true,
            previousStatement: null,
            nextStatement: null,
            colour: HUE_MOTION,
            // spec §3: dx/dy は「出した時の場所」からの絶対オフセット (毎回の積み上げではない —
            // 暴走ドリフト防止)。同じ数値で何度動かしても毎回同じ場所に着地する。
            tooltip: "出したときの場所を基準にした位置 (x, y) へ動かします。同じ数値で何度も動かすと毎回同じ場所に戻ります (動かした分がどんどん積み上がることはありません)。",
        },
        // Wave 6 (docs/ekn-wave6-contract.md §1 2026-08-29 追記) — とばすもの (発射体プリミティブ)。
        {
            type: "ekr_do_cno_launch",
            message0: "オブジェクト %1 を %2 の方向へ %3 でとばす",
            args0: [
                { type: "field_dropdown", name: "SLOT", options: [["1", "1"], ["2", "2"], ["3", "3"]] },
                {
                    type: "field_dropdown", name: "DIR", options: [
                        ["すすんでいる方向", "move"], ["相手の方向", "ctx"],
                        ["マーカー1の方向", "marker1"], ["マーカー2の方向", "marker2"],
                        ["マーカー3の方向", "marker3"], ["マーカー4の方向", "marker4"],
                    ],
                },
                { type: "field_dropdown", name: "SPEED", options: [["ゆっくり", "slow"], ["ふつう", "medium"], ["はやく", "fast"]] },
            ],
            inputsInline: true,
            previousStatement: null,
            nextStatement: null,
            colour: HUE_MOTION,
            tooltip: "出したオブジェクトを、決めた方向へ飛ばします。飛んでいる間に壁にぶつかったり、遠くまで飛んだり、しばらく時間がたったりすると自動的に消えます (スロットがあくのでまた出せます)。「あいて」や「マーカー」の方向は飛ばした瞬間に1回だけ決まり、あとから追いかけません。「すすんでいる方向」は止まっていると飛びません。当たったときの処理は「オブジェクトにだれかが触れたとき」で決めよう (当たっても弾は消えないので、消したいときは「オブジェクトを消す」も置こう)。オブジェクトを出していないスロットを指定しても何も起きません。",
        },

        // 見た目
        {
            type: "ekr_do_notify",
            message0: "%1 に「 %2 」と %3 秒間おしらせする",
            args0: [
                // Wave 1 (spec §3): 複数セレクタ (みんな/おなじ部屋のみんな) を出せる唯一の op。
                { type: "field_dropdown", name: "TARGET", options: TARGET_NOTIFY_OPTIONS },
                { type: "field_input", name: "TEXT", text: "おしらせ" },
                { type: "field_number", name: "SECONDS", value: 3, min: 1, max: 30, precision: 1 },
            ],
            inputsInline: true,
            previousStatement: null,
            nextStatement: null,
            colour: HUE_LOOKS,
            // spec §3: 会議中は画面上への表示ができないため、ホストになりすましたプライベート
            // チャットの1行として届く (会議中だけ出せる頻度も 1/5秒 に下がる)。< > は文字として
            // 使えず全角 〈〉 に変わる。
            tooltip: "えらんだ人の画面にメッセージを表示します (最大120字。出せるのは1秒に1回まで)。「みんな」や「おなじ部屋のみんな」も選べます (人数分ぶんの回数を使うので、多いと後の方は届かないことがあります)。会議中はチャット欄への個人メッセージとして届きます (出せる頻度も下がります)。「<」「>」は使えず、自動的に「〈」「〉」に変わります。",
        },
        {
            type: "ekr_do_cno_spawn",
            message0: "オブジェクト %1 を「 %2 」(大きさ %3 ) で %4 に出す",
            args0: [
                { type: "field_dropdown", name: "SLOT", options: [["1", "1"], ["2", "2"], ["3", "3"]] },
                { type: "field_input", name: "TEXT", text: "!" },
                { type: "field_number", name: "SIZE", value: 3, min: 1, max: 12, precision: 1 },
                { type: "field_dropdown", name: "AT", options: [["自分の場所", "self"], ["相手の場所", "ctx"]] },
            ],
            inputsInline: true,
            previousStatement: null,
            nextStatement: null,
            colour: HUE_LOOKS,
            tooltip: "文字のオブジェクトを出します (文字は8字まで・同時に3個まで)。「<」「>」は使えず、自動的に「〈」「〉」に変わります。",
        },
        {
            type: "ekr_do_cno_despawn",
            message0: "オブジェクト %1 を消す",
            args0: [{ type: "field_dropdown", name: "SLOT", options: [["1", "1"], ["2", "2"], ["3", "3"]] }],
            previousStatement: null,
            nextStatement: null,
            colour: HUE_LOOKS,
            tooltip: "出したオブジェクトを消します。",
        },
        {
            type: "ekr_do_cno_show",
            message0: "オブジェクト %1 を見せる相手を %2 にする",
            args0: [
                { type: "field_dropdown", name: "SLOT", options: [["1", "1"], ["2", "2"], ["3", "3"]] },
                { type: "field_dropdown", name: "WHO", options: [["みんな", "all"], ["自分だけ", "self"]] },
            ],
            inputsInline: true,
            previousStatement: null,
            nextStatement: null,
            colour: HUE_LOOKS,
            tooltip: "出したオブジェクトを誰に見せるかを変更します。",
        },
        // v1.1 (spec §3 2026-08-09 追記) — SLOT は ekr_do_cno_spawn と同じ枠を共有する (同時3個まで)。
        {
            type: "ekr_do_dummy_spawn",
            message0: "ダミー人形 %1 を「 %2 」の名前で %3 に出す ( %4 )",
            args0: [
                { type: "field_dropdown", name: "SLOT", options: [["1", "1"], ["2", "2"], ["3", "3"]] },
                { type: "field_input", name: "NAME", text: "ダミー" },
                { type: "field_dropdown", name: "AT", options: [["自分の場所", "self"], ["相手の場所", "ctx"]] },
                { type: "field_dropdown", name: "KILLABLE", options: [["こわせない", "0"], ["キルでこわせる", "1"]] },
            ],
            inputsInline: true,
            previousStatement: null,
            nextStatement: null,
            colour: HUE_LOOKS,
            tooltip: "人間そっくりのダミー人形を出します (名前は8字まで・オブジェクトと同じ枠を使うので同時に3個まで)。「動かす」「消す」ブロックはそのまま使えますが、「見せる相手を変える」は効きません。会議が始まると自動的に消えます (会議のあとに自動では戻りません)。出せるのは3秒に1体まで、会議のあとは10秒たたないと出せません。「キルでこわせる」にすると、キルできる人がこわせるようになります。",
        },
        {
            type: "ekr_do_corpse_spawn",
            message0: "%1 の死体を %2 に置く",
            args0: [
                { type: "field_dropdown", name: "COLOR", options: [["自分の色", "self"], ["ランダムな色", "random"]] },
                { type: "field_dropdown", name: "AT", options: [["自分の場所", "self"], ["相手の場所", "ctx"]] },
            ],
            inputsInline: true,
            previousStatement: null,
            nextStatement: null,
            colour: HUE_LOOKS,
            tooltip: "偽物の死体を置きます。ふつうに通報できます。会議が始まると自動的に消えます。出せるのは2秒に1回まで、追放の演出中は出せません。",
        },

        // ひっさつわざ (v1.2 spec §3 追記 — マーカー/相手ワープ/ポータル)
        {
            type: "ekr_do_marker_save",
            message0: "いまの場所をマーカー %1 におぼえる ( %2 )",
            args0: [
                { type: "field_dropdown", name: "SLOT", options: [["1", "1"], ["2", "2"], ["3", "3"], ["4", "4"]] },
                {
                    type: "field_dropdown", name: "AT", options: [
                        ["じぶん", "self"], ["相手", "ctx"],
                        ["オブジェクト1", "cno1"], ["オブジェクト2", "cno2"], ["オブジェクト3", "cno3"],
                    ],
                },
            ],
            inputsInline: true,
            previousStatement: null,
            nextStatement: null,
            colour: HUE_ULTIMATE,
            tooltip: "いまの場所を、あとで「ワープする」で使えるように覚えておきます (4つまで)。「相手」や「オブジェクト」を選んでも、このできごとに相手やそのオブジェクトがいなければ何も起きません。会議をまたいでも覚えていますが、ゲームが始まると全部忘れます。",
        },
        {
            type: "ekr_do_teleport_other",
            message0: "%1 を %2 にワープさせる",
            args0: [
                // Wave 1 (spec §3): 単数セレクタへ拡張 (じぶんは出さない — 自分を飛ばすのは「ワープする」)。
                { type: "field_dropdown", name: "TARGET", options: TARGET_OTHER_OPTIONS },
                {
                    type: "field_dropdown", name: "TO", options: [
                        ["じぶんのところ", "self"],
                        ["マーカー1", "marker1"], ["マーカー2", "marker2"], ["マーカー3", "marker3"], ["マーカー4", "marker4"],
                        ...CNO_PLACE_OPTIONS,
                    ],
                },
            ],
            inputsInline: true,
            previousStatement: null,
            nextStatement: null,
            colour: HUE_ULTIMATE,
            tooltip: "えらんだ人をワープさせます (その人がいなければ何も起きません。マーカーが未保存だったりオブジェクトを出していなかったりしても何も起きません)。ワープは自分の役職だけでなく全部の役職まとめて1秒に2回までしか使えないので、ここぞというときに使おう。",
        },
        // Wave 1 (spec §3 2026-08-11) — remember (marker_save の人間版・slot は 2 つ)
        {
            type: "ekr_do_remember",
            message0: "%1 を おぼえた人 %2 としておぼえる",
            args0: [
                { type: "field_dropdown", name: "TARGET", options: TARGET_SINGLE_OPTIONS },
                { type: "field_dropdown", name: "SLOT", options: [["1", "1"], ["2", "2"]] },
            ],
            inputsInline: true,
            previousStatement: null,
            nextStatement: null,
            colour: HUE_ULTIMATE,
            tooltip: "えらんだ人を「おぼえた人1／2」として覚えておきます (2人まで)。あとで「キルする」や「ワープさせる」で指定できます。おぼえた人が死んだり切断したりすると忘れ、そのときは何も起きません。会議をまたいでも覚えていますが、ゲームが始まると忘れます。",
        },
        {
            type: "ekr_do_portal_place",
            message0: "ポータル %1 をここに置く",
            args0: [{ type: "field_dropdown", name: "WHICH", options: [["A", "a"], ["B", "b"]] }],
            previousStatement: null,
            nextStatement: null,
            colour: HUE_ULTIMATE,
            tooltip: "自分の足元にポータルを置きます。AとB両方置くと、ワープでつながります (生きているプレイヤーが触れると反対側へワープします)。同じ方をもう一度置くと、そちらだけ引っ越します。",
        },
        // v1.3 (spec §3 2026-08-11 追記) — ひっぱる・ひきずる・フィールド
        {
            type: "ekr_do_pull",
            message0: "あいてをひきよせる",
            previousStatement: null,
            nextStatement: null,
            colour: HUE_ULTIMATE,
            tooltip: "このできごとの相手を、自分のいまいる場所へ一気に引き寄せます (相手がいなければ何も起きません)。ワープと同じ予算 (全部の役職まとめて1秒に2回まで) を使うので、ここぞというときに使おう。",
        },
        {
            type: "ekr_do_drag",
            message0: "相手をつかんでひきずる ( %1 びょう)",
            args0: [{ type: "field_number", name: "SECONDS", value: 3, min: 1, max: 10, precision: 1 }],
            inputsInline: true,
            previousStatement: null,
            nextStatement: null,
            colour: HUE_ULTIMATE,
            tooltip: "このできごとの相手を、指定した秒数のあいだつかんで自分のそばへ引きずり続けます (1〜10秒)。会議が始まると自動的にはなします。ひきずるのとフィールドは同時に1つしか使えないので、使っているときに新しく始めても効きません。",
        },
        {
            type: "ekr_do_field",
            message0: "%1 に ( 半径 %2 ・ 強さ %3 ) の ブラックホールをつくる ( %4 びょう)",
            args0: [
                {
                    type: "field_dropdown", name: "AT", options: [
                        ["じぶんの場所", "self"], ["相手の場所", "ctx"],
                        ["マーカー1", "marker1"], ["マーカー2", "marker2"], ["マーカー3", "marker3"], ["マーカー4", "marker4"],
                    ],
                },
                { type: "field_dropdown", name: "RADIUS", options: [["小", "small"], ["中", "medium"], ["大", "large"]] },
                { type: "field_dropdown", name: "STRENGTH", options: [["弱", "weak"], ["中", "medium"], ["強", "strong"]] },
                { type: "field_number", name: "SECONDS", value: 5, min: 1, max: 15, precision: 1 },
            ],
            inputsInline: true,
            previousStatement: null,
            nextStatement: null,
            colour: HUE_ULTIMATE,
            tooltip: "指定した場所にブラックホールを出します。半径の中にいる生きているプレイヤー (自分以外) を、指定した秒数のあいだ中心へ引き寄せ続けます (1〜15秒)。会議が始まったり時間が終わったりすると消えます。ひきずるのとフィールドは同時に1つしか使えないので、使っているときに新しく置いても出ません。マーカーが未保存だと出ません。",
        },

        // しらべる (Wave 2 spec §2 追記 — 情報開示制御)
        {
            type: "ekr_do_inspect",
            message0: "%1 の %2 をしらべる",
            args0: [
                { type: "field_dropdown", name: "TARGET", options: TARGET_OTHER_OPTIONS },
                { type: "field_dropdown", name: "DEPTH", options: [["じんえい", "team"], ["やくしょく", "role"]] },
            ],
            message1: "(はずれる確率 %1 ％ ・ まぜるダミー %2 こ)",
            args1: [
                { type: "field_number", name: "FAILCHANCE", value: 0, min: 0, max: 100, precision: 1 },
                { type: "field_number", name: "NOISE", value: 0, min: 0, max: 5, precision: 1 },
            ],
            inputsInline: true,
            previousStatement: null,
            nextStatement: null,
            colour: HUE_INFO,
            tooltip: "えらんだ人をしらべて、自分だけに結果を知らせます。「じんえい」は陣営 (クルー等)、「やくしょく」は正確な役職名です。「はずれる確率」を上げると、たまにウソの結果を見せます。「まぜるダミー」は「やくしょく」のときだけ効き、本物の役職とダミーの役職をまとめてリスト表示します (「◯◯か△△か××のようだ」)。しらべられるのは1秒に1回まで (会議中は5秒に1回)。死んでいる人はしらべられません。",
        },
        {
            type: "ekr_do_reveal",
            message0: "%1 の役職がゲームが終わるまでずっと見えるようになる",
            args0: [{ type: "field_dropdown", name: "TARGET", options: TARGET_OTHER_OPTIONS }],
            previousStatement: null,
            nextStatement: null,
            colour: HUE_INFO,
            tooltip: "えらんだ人の役職名と役職の色が、これ以降ずっと (タスク中も会議中も) 自分にだけ見えるようになります。ウソはつきません (「しらべる」とは別物)。効かせられるのは1秒に1回まで。",
        },
        {
            type: "ekr_do_arrow_show",
            message0: "%1 を追いかける矢印をだす ( %2 びょう)",
            args0: [
                { type: "field_dropdown", name: "TARGET", options: TARGET_OTHER_OPTIONS },
                { type: "field_number", name: "SECONDS", value: 10, min: 5, max: 600, precision: 1 },
            ],
            inputsInline: true,
            previousStatement: null,
            nextStatement: null,
            colour: HUE_INFO,
            tooltip: "えらんだ人を追いかけ続ける矢印を出します (タスク中だけ見えます。会議中は出ません)。その人が死ぬと矢印も消えます。「場所への矢印」と合わせて同時に4本まで、出せるのは1秒に1回までです。",
        },
        {
            type: "ekr_do_arrow_mark",
            message0: "%1 に矢印をおく ( %2 びょう)",
            args0: [
                {
                    type: "field_dropdown", name: "AT", options: [
                        ["あいての場所", "ctx"],
                        ["マーカー1", "marker1"], ["マーカー2", "marker2"], ["マーカー3", "marker3"], ["マーカー4", "marker4"],
                        ...CNO_PLACE_OPTIONS,
                    ],
                },
                { type: "field_number", name: "SECONDS", value: 10, min: 5, max: 600, precision: 1 },
            ],
            inputsInline: true,
            previousStatement: null,
            nextStatement: null,
            colour: HUE_INFO,
            tooltip: "指定した場所に、その場から動かない矢印を出します (このできごとに「あいて」がいなければ「あいての場所」は何も起きません。マーカーやオブジェクトが未保存でも同じです)。「人への矢印」と合わせて同時に4本まで、出せるのは1秒に1回までです。",
        },
        {
            type: "ekr_do_arrow_hide",
            message0: "だしている矢印をぜんぶ消す",
            previousStatement: null,
            nextStatement: null,
            colour: HUE_INFO,
            tooltip: "自分が出した矢印 (人への矢印・場所への矢印の両方) を全部消します。",
        },

        // とうひょう (Wave 2 spec §1.3/§3 追記 — 会議・投票操作)
        {
            type: "ekr_do_cancel_vote",
            message0: "票をつかわずにえらぶ",
            previousStatement: null,
            nextStatement: null,
            colour: HUE_MEETING,
            tooltip: "いま入れようとしている票を取り消して、選び直せるようにします。「かいぎで投票したとき」の中だけで使えて、しかも「秒待つ」より前に置かないと間に合いません。ひと会議に1回だけ効きます (2回目以降はふつうに票が入ります)。",
        },
        {
            type: "ekr_do_vote_weight_set",
            message0: "自分の票のちからを %1 にする",
            args0: [{ type: "field_number", name: "VALUE", value: 1, min: 0, max: 3, precision: 1 }],
            previousStatement: null,
            nextStatement: null,
            colour: HUE_MEETING,
            tooltip: "今後の会議すべてで、自分の1票の重さを変更します (0〜3・0だと投票しても数えられません)。今すぐ反映されます。",
        },
        {
            type: "ekr_do_vote_block",
            message0: "%1 の票をなくす",
            args0: [{ type: "field_dropdown", name: "TARGET", options: TARGET_OTHER_OPTIONS }],
            previousStatement: null,
            nextStatement: null,
            colour: HUE_MEETING,
            tooltip: "この会議だけ、えらんだ人の票を無効にします (すでに入れていた票も無効になります)。相手には知らせません。会議中でしか効きません。",
        },
        {
            type: "ekr_do_vote_swap",
            message0: "おぼえた人1と2の票をいれかえる",
            previousStatement: null,
            nextStatement: null,
            colour: HUE_MEETING,
            tooltip: "「おぼえた人1」と「おぼえた人2」に入った票を入れかえます (どちらか忘れていたら何も起きません)。会議中でしか効きません。全部の役職まとめて1つの会議で1回までです。",
        },
        {
            type: "ekr_do_exile",
            message0: "%1 をつよせいついほうする",
            args0: [{ type: "field_dropdown", name: "TARGET", options: TARGET_SINGLE_OPTIONS }],
            previousStatement: null,
            nextStatement: null,
            colour: HUE_MEETING,
            tooltip: "投票を待たずに、えらんだ人をすぐ追放して会議を終わらせます (自分をえらぶこともできます)。会議中でしか効かず、ひと会議に1回だけです。何回使えるようにするかは、変数を使って自分で決めよう。",
        },

        // つなぐ (Wave 4 docs/ekn-wave4-contract.md §3/§4 — リンクと勧誘)
        {
            type: "ekr_do_link",
            message0: "%1 と つなぐ",
            args0: [{
                type: "field_dropdown", name: "TARGET", options: [
                    ["あいて", "ctx"],
                    ["おぼえた人1", "saved1"], ["おぼえた人2", "saved2"],
                    ["いちばん近くの人", "nearest"], ["だれか (ランダム)", "random"],
                ],
            }],
            previousStatement: null,
            nextStatement: null,
            colour: HUE_LINK,
            tooltip: "えらんだ人と つなぎます (つなげるのは1人だけ。もう一度つなぐと、前のつなぎは外れて新しい人につなぎ直します)。つないだ人は「つないだ人」としてキルやワープなどで指定でき、その人が死ぬと「つないだ人が 死んだとき」が動きます。会議をまたいでもつながったままですが、ゲームが始まると外れます (切断でいなくなったときは、なにも起きずに外れます)。会議中でも使えます。",
        },
        {
            type: "ekr_do_unlink",
            message0: "つなぎを とく",
            previousStatement: null,
            nextStatement: null,
            colour: HUE_LINK,
            tooltip: "いまのつなぎを外します (つないでいなければ何も起きません)。会議中でも使えます。",
        },
        {
            type: "ekr_do_recruit",
            // Wave 5 (docs/ekn-wave5-contract.md §2): かえるさき (SLOT) は任意。既定の "" =
            // 「じぶんとおなじ」で、compile-role が正準形からフィールドごと省略する。
            message0: "%1 を なかまにする (かえるさき %2)",
            args0: [{
                type: "field_dropdown", name: "TARGET", options: [
                    ["あいて", "ctx"], ["つないだ人", "linked"],
                    ["おぼえた人1", "saved1"], ["おぼえた人2", "saved2"],
                    ["いちばん近くの人", "nearest"], ["だれか (ランダム)", "random"],
                ],
            }, {
                type: "field_dropdown", name: "SLOT", options: RECRUIT_SLOT_OPTIONS,
            }],
            inputsInline: true,
            previousStatement: null,
            nextStatement: null,
            colour: HUE_LINK,
            // 契約 §4: on_game_start 再発火とインポスター人数枠の増加は tooltip 明記が契約要件。
            tooltip: "えらんだ人が、自分と同じ役職になります。会議中は効きません。同じ役職の人には効きません。しばらく間をあけないと連続では効きません。なかまに した/された ときも「ゲームがはじまったとき」が動くよ。じぶんがインポスターの役職なら、なかまにした人も本物のインポスターになるよ (インポスターの人数がふえる)。「かえるさき」でスロット番号をえらぶと、その役職にすることもできるよ (そのスロットに役職が入っていないときは なにも起きません)。",
        },

        // 持続効果 (Wave 5 docs/ekn-wave5-contract.md §1 — こうかをかける)
        {
            type: "ekr_do_effect_give",
            message0: "%1 を %2 %3 秒",
            args0: [
                { type: "field_dropdown", name: "TARGET", options: TARGET_SINGLE_OPTIONS },
                {
                    type: "field_dropdown", name: "KIND", options: [
                        ["はやくする", "haste"], ["おそくする", "slow"],
                        ["こおらせる", "freeze"], ["くらくする", "blind"],
                    ],
                },
                { type: "field_number", name: "SECONDS", value: 5, min: 1, max: 30, precision: 1 },
            ],
            inputsInline: true,
            previousStatement: null,
            nextStatement: null,
            colour: HUE_LINK,
            // 契約 §1.1: 実効値 (×1.5 / ×0.5 / 視界 0.3) は作者に開けないので、tooltip も数字を出さない。
            // freeze だけ上限 10 秒なので、そこは明記する (超えると保存時にエラーになるため)。
            tooltip: "えらんだ人に、しばらくのあいだ こうかをかけます。はやくする / おそくする / こおらせる (うごけなくする) / くらくする (見えるはんいをせまくする) の4つから えらべます。おなじ人に かけなおすと、あとからかけた方が勝ちます (かさなりません)。会議がはじまると ぜんぶ消えます。かける人が死んでも、じかんが終わるまで こうかは のこります。こおらせるは 1〜10 秒、ほかは 1〜30 秒までです。",
        },

        // 変数・式 (動的ドロップダウンが不要なもののみ。var_set/var_add/変数の値 は命令形で別途登録)
        {
            type: "ekr_expr_arith",
            message0: "%1 %2 %3",
            args0: [
                { type: "input_value", name: "A" },
                { type: "field_dropdown", name: "OP", options: [["+", "add"], ["−", "sub"], ["×", "mul"], ["÷", "div"]] },
                { type: "input_value", name: "B" },
            ],
            inputsInline: true,
            output: null,
            colour: HUE_VAR,
            tooltip: "2つの数の計算結果です。",
        },
        {
            type: "ekr_expr_compare",
            message0: "%1 %2 %3",
            args0: [
                { type: "input_value", name: "A" },
                { type: "field_dropdown", name: "OP", options: [["=", "eq"], ["≠", "ne"], ["<", "lt"], ["≦", "le"], [">", "gt"], ["≧", "ge"]] },
                { type: "input_value", name: "B" },
            ],
            inputsInline: true,
            output: null,
            colour: HUE_VAR,
            tooltip: "2つの数を比べます (正しければ1、正しくなければ0)。",
        },
        {
            type: "ekr_expr_logic",
            message0: "%1 %2 %3",
            args0: [
                { type: "input_value", name: "A" },
                { type: "field_dropdown", name: "OP", options: [["かつ", "and"], ["または", "or"]] },
                { type: "input_value", name: "B" },
            ],
            inputsInline: true,
            output: null,
            colour: HUE_VAR,
            tooltip: "2つの条件を組み合わせます。",
        },
        {
            type: "ekr_expr_not",
            message0: "%1 ではない",
            args0: [{ type: "input_value", name: "A" }],
            inputsInline: true,
            output: null,
            colour: HUE_VAR,
            tooltip: "条件を反対にします (正しい ⇔ 正しくない)。",
        },
        {
            type: "ekr_expr_rand",
            message0: "%1 から %2 までのランダムな数",
            args0: [{ type: "input_value", name: "A" }, { type: "input_value", name: "B" }],
            inputsInline: true,
            output: null,
            colour: HUE_VAR,
            tooltip: "指定した範囲のランダムな整数です。",
        },
    ];
}

function defineDynamicVariableBlocks(): void {
    Blockly.Blocks["ekr_expr_var"] = {
        init(this: Blockly.Block): void {
            this.appendDummyInput().appendField(new Blockly.FieldDropdown(variableDropdownOptions), "VAR");
            this.setOutput(true, null);
            this.setColour(HUE_VAR);
            this.setTooltip("変数の今の値です。");
        },
    };

    Blockly.Blocks["ekr_do_var_set"] = {
        init(this: Blockly.Block): void {
            this.appendDummyInput()
                .appendField("変数")
                .appendField(new Blockly.FieldDropdown(variableDropdownOptions), "VAR")
                .appendField("を");
            this.appendValueInput("VALUE");
            this.appendDummyInput().appendField("にする");
            this.setInputsInline(true);
            this.setPreviousStatement(true, null);
            this.setNextStatement(true, null);
            this.setColour(HUE_VAR);
            this.setTooltip("変数の値を指定した値に変更します。");
        },
    };

    // Wave 3 (docs/ekn-wave3-contract.md §1.2 2026-08-14) — 「へんすう◯が◯になったら」。
    // 変数ドロップダウンが動的なので ekr_expr_var/ekr_do_var_set と同じく命令形で登録する
    // (JSON block defs の field_dropdown は静的な options 配列しか持てない)。
    Blockly.Blocks["ekr_when_on_var"] = {
        init(this: Blockly.Block): void {
            this.appendDummyInput()
                .appendField("へんすう")
                .appendField(new Blockly.FieldDropdown(variableDropdownOptions), "VAR")
                .appendField("が")
                .appendField(new Blockly.FieldNumber(0), "VALUE")
                .appendField(new Blockly.FieldDropdown(CMP_OPTIONS), "CMP")
                .appendField("になったら");
            this.setInputsInline(true);
            this.setNextStatement(true, null);
            this.setColour(HUE_EVENT);
            this.setTooltip(WHEN_TOOLTIPS.on_var);
        },
    };

    Blockly.Blocks["ekr_do_var_add"] = {
        init(this: Blockly.Block): void {
            this.appendDummyInput()
                .appendField("変数")
                .appendField(new Blockly.FieldDropdown(variableDropdownOptions), "VAR")
                .appendField("を");
            this.appendValueInput("VALUE");
            this.appendDummyInput().appendField("ふやす (へらすときはマイナスの数)");
            this.setInputsInline(true);
            this.setPreviousStatement(true, null);
            this.setNextStatement(true, null);
            this.setColour(HUE_VAR);
            this.setTooltip("変数の値に、指定した数を足します (マイナスの数を足せば減らせます)。");
        },
    };
}

let blocksDefined = false;

/** ブロック定義をまとめて登録する。何度呼んでも二重登録しない。 */
export function defineRoleBlocks(): void {
    if (blocksDefined) return;
    blocksDefined = true;
    Blockly.defineBlocksWithJsonArray(jsonBlockDefs());
    defineDynamicVariableBlocks();
}

// ---------------------------------------------------------------------------
// テーマ (エディタのダークテーマに合わせる)
// ---------------------------------------------------------------------------
//
// 罠: Blockly の既定テーマ (Classic/Zelos とも) はワークスペース背景が白固定。
// .blocklyToolboxDiv のようにあとから CSS で塗り替える手も使えるが、ワークスペース本体の
// 背景 (.blocklyMainBackground) は Blockly がリサイズ・再描画のたびに再生成するため CSS
// override が安定して効かない。theme オプションの componentStyles で指定するのが正攻法
// (これを見落とすと、grid の colour を薄い白にしても白背景に白い線で完全に不可視になる —
// 実際にこの実装時に screenshot で発見した罠)。
let cachedTheme: Blockly.Theme | null = null;

/** エディタのダークパレット (--bg-1/--bg-2/--text-primary 相当) に合わせた Blockly テーマ。 */
export function buildRoleTheme(): Blockly.Theme {
    if (cachedTheme) return cachedTheme;
    cachedTheme = Blockly.Theme.defineTheme("ekmDark", {
        name: "ekmDark",
        base: Blockly.Themes.Zelos,
        componentStyles: {
            workspaceBackgroundColour: "#2a2a33",
            toolboxBackgroundColour: "#2a2a33",
            toolboxForegroundColour: "#e8e8ee",
            flyoutBackgroundColour: "#1f1f26",
            flyoutForegroundColour: "#e8e8ee",
            flyoutOpacity: 1,
            scrollbarColour: "#5a5a68",
            insertionMarkerColour: "#ffd75e",
        },
    });
    return cachedTheme;
}

/** ツールボックス (JSON 形式)。5カテゴリ = spec §7 のカテゴリ標準色。 */
export function buildRoleToolbox(): Blockly.utils.toolbox.ToolboxDefinition {
    return {
        kind: "categoryToolbox",
        contents: [
            {
                kind: "category",
                name: "イベント",
                colour: String(HUE_EVENT),
                contents: LOGIC_WHEN_VALUES.map((when) => ({ kind: "block", type: `ekr_when_${when}` })),
            },
            {
                kind: "category",
                name: "制御",
                colour: String(HUE_CONTROL),
                contents: [
                    { kind: "block", type: "ekr_if" },
                    { kind: "block", type: "ekr_if_else" },
                    { kind: "block", type: "ekr_do_wait" },
                    { kind: "block", type: "ekr_do_stop" },
                    { kind: "block", type: "ekr_do_kill" },
                    { kind: "block", type: "ekr_do_cancel_attack" },
                    { kind: "block", type: "ekr_do_set_kill_cooldown" },
                ],
            },
            {
                kind: "category",
                name: "動き",
                colour: String(HUE_MOTION),
                contents: [
                    { kind: "block", type: "ekr_do_teleport" },
                    { kind: "block", type: "ekr_do_speed" },
                    { kind: "block", type: "ekr_do_cno_move" },
                    { kind: "block", type: "ekr_do_cno_launch" },
                ],
            },
            {
                kind: "category",
                name: "見た目",
                colour: String(HUE_LOOKS),
                contents: [
                    { kind: "block", type: "ekr_do_notify" },
                    { kind: "block", type: "ekr_do_cno_spawn" },
                    { kind: "block", type: "ekr_do_cno_despawn" },
                    { kind: "block", type: "ekr_do_cno_show" },
                    { kind: "block", type: "ekr_do_dummy_spawn" },
                    { kind: "block", type: "ekr_do_corpse_spawn" },
                ],
            },
            {
                kind: "category",
                name: "ひっさつわざ",
                colour: String(HUE_ULTIMATE),
                contents: [
                    { kind: "block", type: "ekr_do_marker_save" },
                    { kind: "block", type: "ekr_do_remember" },
                    { kind: "block", type: "ekr_do_teleport_other" },
                    { kind: "block", type: "ekr_do_portal_place" },
                    { kind: "block", type: "ekr_do_pull" },
                    { kind: "block", type: "ekr_do_drag" },
                    { kind: "block", type: "ekr_do_field" },
                ],
            },
            {
                kind: "category",
                name: "つなぐ",
                colour: String(HUE_LINK),
                contents: [
                    { kind: "block", type: "ekr_do_link" },
                    { kind: "block", type: "ekr_do_unlink" },
                    { kind: "block", type: "ekr_do_recruit" },
                    { kind: "block", type: "ekr_do_effect_give" },
                ],
            },
            {
                kind: "category",
                name: "しらべる",
                colour: String(HUE_INFO),
                contents: [
                    { kind: "block", type: "ekr_do_inspect" },
                    { kind: "block", type: "ekr_do_reveal" },
                    { kind: "block", type: "ekr_do_arrow_show" },
                    { kind: "block", type: "ekr_do_arrow_mark" },
                    { kind: "block", type: "ekr_do_arrow_hide" },
                ],
            },
            {
                kind: "category",
                name: "とうひょう",
                colour: String(HUE_MEETING),
                contents: [
                    { kind: "block", type: "ekr_do_cancel_vote" },
                    { kind: "block", type: "ekr_do_vote_weight_set" },
                    { kind: "block", type: "ekr_do_vote_block" },
                    { kind: "block", type: "ekr_do_vote_swap" },
                    { kind: "block", type: "ekr_do_exile" },
                ],
            },
            {
                kind: "category",
                name: "変数",
                colour: String(HUE_VAR),
                contents: [
                    { kind: "block", type: "ekr_do_var_set" },
                    { kind: "block", type: "ekr_do_var_add" },
                    { kind: "block", type: "ekr_expr_var" },
                    { kind: "block", type: "math_number" },
                    { kind: "block", type: "logic_boolean" },
                    { kind: "block", type: "ekr_expr_arith" },
                    { kind: "block", type: "ekr_expr_compare" },
                    { kind: "block", type: "ekr_expr_logic" },
                    { kind: "block", type: "ekr_expr_not" },
                    { kind: "block", type: "ekr_expr_rand" },
                ],
            },
        ],
    };
}

export { Blockly };
