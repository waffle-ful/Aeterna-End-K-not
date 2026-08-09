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
import { LOGIC_WHEN_VALUES, type LogicWhen } from "../roledef";

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

const WHEN_LABELS: Record<LogicWhen, string> = {
    on_game_start: "ゲームが始まったとき",
    on_pet: "ボタンをおしたとき",
    on_kill: "キルしたとき",
    on_death: "じぶんが死んだとき",
    on_meeting_start: "会議が始まったとき",
    on_meeting_end: "会議が終わったとき",
    on_task_complete: "タスクを1つ終えたとき",
    on_vent_enter: "ベントに入ったとき",
    on_report: "死体を通報したとき",
    on_second: "毎秒くりかえす",
};

const WHEN_TOOLTIPS: Record<LogicWhen, string> = {
    on_game_start: "タスクフェーズが始まったときに1回だけ実行します。「秒待つ」を挟んだ続きは会議が始まると取り消されるので、開始すぐに会議になる設定だと動かないことがあります。",
    on_pet: "ペット能力 (ボタン) を発動したときに実行します。",
    on_kill: "自分のキルが成立した直後に実行します。",
    on_death: "自分が死亡したときに実行します (追放されたときや会議で亡くなったときも含みます。切断は含みません)。",
    on_meeting_start: "会議画面が開いたときに実行します。",
    on_meeting_end: "追放処理のあと、タスクが再開するときに実行します。",
    on_task_complete: "自分がタスクを1つ終えるたびに実行します。",
    on_vent_enter: "自分がベントに入ったときに実行します。",
    on_report: "自分が死体を通報したときに実行します。",
    on_second: "タスク中、自分が生きている間、毎秒くりかえし実行します (処理が重いことはしないでね)。",
};

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
    const eventBlocks = LOGIC_WHEN_VALUES.map((when) => ({
        type: `ekr_when_${when}`,
        message0: WHEN_LABELS[when],
        nextStatement: null,
        colour: HUE_EVENT,
        tooltip: WHEN_TOOLTIPS[when],
    }));

    return [
        ...eventBlocks,

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
            args0: [{ type: "field_dropdown", name: "TARGET", options: [["自分", "self"], ["相手", "ctx"]] }],
            previousStatement: null,
            nextStatement: null,
            colour: HUE_CONTROL,
            tooltip: "自分、または相手 (このできごとに相手がいるとき) をキルします。",
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
            args0: [{ type: "field_dropdown", name: "TO", options: [["ランダムな場所", "random"], ["相手の場所", "ctx"]] }],
            previousStatement: null,
            nextStatement: null,
            colour: HUE_MOTION,
            tooltip: "ランダムな場所、または相手 (このできごとに相手がいるとき) の場所にワープします。",
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

        // 見た目
        {
            type: "ekr_do_notify",
            message0: "「 %1 」と %2 秒間おしらせする",
            args0: [
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
            tooltip: "画面にメッセージを表示します (最大120字。出せるのは1秒に1回まで)。会議中はチャット欄への個人メッセージとして届きます (出せる頻度も下がります)。「<」「>」は使えず、自動的に「〈」「〉」に変わります。",
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
