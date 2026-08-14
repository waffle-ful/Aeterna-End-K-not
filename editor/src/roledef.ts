// EKN 役職コード (EKR1.) のデータ契約 — Modules/Ekm/EkrDefinition.cs の TS 側ミラー。
// R0: フォームのみ (name/color/canKill/killCooldown/canVent/vision/team)。
// R1: 上記に加え、任意の `logic` (Blockly ロジック AST)。契約の正典は docs/ekr-logic-spec.md —
// C# (Modules/Ekm/EkrDefinition.cs 予定) と本ファイルは同書のミラー。矛盾を見つけたら実装で
// 吸収せず spec 改訂を先に行うこと。役職コードはマップコード (docs/ekmap-spec.md) とは独立した
// 契約であり、このファイルは model.ts/validate.ts (マップ契約) には一切依存しない。
//
// 型不一致の扱い (docs/ekr-logic-spec.md §1 「契約解決」= C# に合わせて統一済み。旧版はここに
// フィールド毎の「TS だけ緩い」逸脱リストがあったが、以下の統一ルールで解消したため削除した):
//   - 値型フィールド (canKill/canVent の bool、killCooldown/visionMultiplier の number ——
//     C# 側は非 nullable 値型): 省略 → 既定値 / 明示的な null → 拒否 (STJ は非 nullable 値型に
//     null を入れられず例外) / 型不一致 → 拒否 / 有限数だが範囲外 → クランプ (既定挙動を維持) /
//     数値だが非有限 (NaN・巨大リテラルの Infinity オーバーフロー) → 既定値へフォールバック
//     (C# の `float.IsFinite(x) ? x : 既定値` を模倣。ここだけは意図して reject にしない)。
//   - 参照型フィールド (name/author/color/team/winCondition の string、requires の配列):
//     省略または明示的な null → 既定値 (STJ は string へ null を代入できるため例外にならず、
//     Validate() 側の `?? 既定値` で吸収される) / 型不一致 → 拒否 / 文字列だが書式が不正
//     (color の正規表現不一致等) → 各フィールドの既存の寛容フォールバックを維持。
// この統一により `team: ["crewmate"]` (配列を String() 経由で偶然 "crewmate" に一致させて
// すり抜ける旧バグ) 等の「C# は拒否するが TS は受理する」抜け穴も併せて塞がれる。
//
// normalizeColor/normalizeKillCooldown/normalizeVisionMultiplier は上記の厳格化と別枠で、
// 意図して緩いままにしてある — role-maker.ts の localStorage 下書き復元・フォーム欄書き込みが
// 「壊れたデータでもフォーム自体は必ず開ける」ことに依存しているため (これらのヘルパーを厳格化すると
// 下書き復元が壊れる)。

export const EKR_VERSION = 1;

export const ROLE_NAME_MAX = 24;
export const ROLE_AUTHOR_MAX = 24;

export const KILL_COOLDOWN_MIN = 1;
export const KILL_COOLDOWN_MAX = 180;
export const KILL_COOLDOWN_DEFAULT = 25;

export const VISION_MULTIPLIER_MIN = 0.25;
export const VISION_MULTIPLIER_MAX = 5;
export const VISION_MULTIPLIER_DEFAULT = 1;

export const DEFAULT_COLOR = "#8f8f8f";

// R2 (docs/ekn-r2-contract.md §1): 受理する team は3値。madmate / coven は対象外。
// C# 側 (EkrDefinition.Validate) と同じ集合・同じ既定値を保つこと。
export const SUPPORTED_TEAMS = ["crewmate", "impostor", "neutral"] as const;
export type EkrTeam = (typeof SUPPORTED_TEAMS)[number];
/** 省略時の既定 team。 */
export const SUPPORTED_TEAM: EkrTeam = "crewmate";
export const DEFAULT_WIN_CONDITION = "team";

// R0 が対応する capability の集合 (logic は requires 経由の capability ゲートではなく、
// トップレベル `logic` キーの有無そのものが対応判定になる — R1 実装後もこの集合は変えない)。
const SUPPORTED_CAPABILITIES: ReadonlySet<string> = new Set();

// input type="color" は常に #rrggbb (小文字6桁) しか出力しないため、この契約でも #rrggbb のみを
// 正とする。Unity の ColorUtility.TryParseHtmlString は名前付き色や短縮形も受理するがそれより狭い —
// エディタから作られるコードの実際の値域に合わせた意図的な narrowing。
const COLOR_RE = /^#[0-9a-fA-F]{6}$/;

// ============================================================================
// Logic (R1) — docs/ekr-logic-spec.md §2〜§6 のミラー
// ============================================================================

export const LOGIC_VERSION = 1;

export const LOGIC_WHEN_VALUES = [
    "on_game_start",
    "on_pet",
    "on_kill",
    "on_death",
    "on_meeting_start",
    "on_meeting_end",
    "on_task_complete",
    "on_vent_enter",
    "on_report",
    "on_second",
    "on_cno_touch",
    // Wave 1 (spec §2 2026-08-11): 自分へのキル試行時。ctx = 攻撃者。slot は持たない
    // (slot は on_cno_touch 専用 — validateRule の既存 else 分岐がそのまま reject する)。
    "on_attacked",
    // Wave 2 (docs/ekn-wave2-contract.md §1.1/§1.2 2026-08-11): 会議中の対象選択2系統。
    // どちらも ctx を持つ (投票先/選んだ相手) — CTXLESS_WHENS 相当の対象には入れない
    // (lint-role.ts 側の判定で同じ扱い)。slot は持たない (on_cno_touch 専用のまま)。
    "on_meeting_vote",
    "on_meeting_pick",
] as const;
export type LogicWhen = (typeof LOGIC_WHEN_VALUES)[number];

// R2 (docs/ekn-r2-contract.md §3b): on_attacked の種別と on_death の死因バケット。
// ⚠️ C# 側 (EkmLogicRuntime.EkrAttackKinds / EkrDeathCauses) と同じ並び・同じ綴りを保つこと。
export const ATTACK_KIND_VALUES = ["kill", "indirect", "force", "guess"] as const;
export type AttackKind = (typeof ATTACK_KIND_VALUES)[number];

export const DEATH_CAUSE_VALUES = [
    "kill", "vote", "guess", "bomb", "poison-curse", "environment", "suicide", "other",
] as const;
export type DeathCause = (typeof DEATH_CAUSE_VALUES)[number];
const LOGIC_WHEN_SET: ReadonlySet<string> = new Set(LOGIC_WHEN_VALUES);

export const EXPR_OP_KINDS = [
    "add", "sub", "mul", "div", "eq", "ne", "lt", "le", "gt", "ge", "and", "or", "not", "rand",
] as const;
export type ExprOpKind = (typeof EXPR_OP_KINDS)[number];
const EXPR_OP_KIND_SET: ReadonlySet<string> = new Set(EXPR_OP_KINDS);

// 検証上限 (spec §1・両側で同一に検証)
export const LOGIC_VARIABLES_MAX = 16;
export const LOGIC_VAR_NAME_MAX = 20;
export const LOGIC_RULES_MIN = 1;
export const LOGIC_RULES_MAX = 32;
export const LOGIC_RULE_NODES_MIN = 1;
export const LOGIC_RULE_NODES_MAX = 64;
export const LOGIC_AST_DEPTH_MAX = 8;

// opcode ごとの引数レンジ (spec §3)
export const WAIT_SECONDS_MIN = 0.1;
export const WAIT_SECONDS_MAX = 600;
export const NOTIFY_TEXT_MAX = 120;
export const NOTIFY_SECONDS_MIN = 1;
export const NOTIFY_SECONDS_MAX = 30;
// 役職フォームの killCooldown (1〜180・上の KILL_COOLDOWN_MIN/MAX) とは別レンジ — ロジックの
// set_kill_cooldown だけ 1〜300 まで許容される (spec §3 表)。混同禁止。
export const LOGIC_SET_KILL_COOLDOWN_MIN = 1;
export const LOGIC_SET_KILL_COOLDOWN_MAX = 300;
export const SPEED_MULT_MIN = 0.5;
export const SPEED_MULT_MAX = 3.0;
export const SPEED_SECONDS_MIN = 1;
export const SPEED_SECONDS_MAX = 60;
export const CNO_SLOT_MIN = 1;
export const CNO_SLOT_MAX = 3;
export const CNO_SPAWN_TEXT_MAX = 8;
export const CNO_SPAWN_SIZE_MIN = 1;
export const CNO_SPAWN_SIZE_MAX = 12;
export const CNO_MOVE_DELTA_MIN = -50;
export const CNO_MOVE_DELTA_MAX = 50;
// dummy_spawn.name の上限 (spec §3 v1.1 追記)。数値は cno_spawn.text と同じ 8 だが、意味の異なる
// 別フィールドなので別定数にする (どちらかのレンジだけ将来変わっても連動しないように)。
export const DUMMY_SPAWN_NAME_MAX = 8;
export const DUMMY_SPAWN_DEFAULT_NAME = "Dummy";

// v1.2 (spec §2/§3 2026-08-10 追記) — 位置と接触
export const MARKER_SLOT_MIN = 1;
export const MARKER_SLOT_MAX = 4;
// Wave 1 (spec §3 統一セレクタ語彙 2026-08-11): 空間セレクタ「どこ」に cno1..3 を追加。
// 追加先は teleport.to / teleport_other.to の2つだけ — spec §3 の一般文は「at/to に追加」と
// 広く書いてあるが、各 op の引数表 (および Wave 1 実装スコープの明示列挙) はこの2フィールドに
// 限定しているため、狭い方 (per-op 表) に合わせる。marker_save.at は元から cno1..3 を持ち、
// field.at は Wave 1 では拡張しない。
export const TELEPORT_TO_VALUES = ["random", "ctx", "marker1", "marker2", "marker3", "marker4", "cno1", "cno2", "cno3"] as const;
export const TELEPORT_OTHER_TO_VALUES = ["self", "marker1", "marker2", "marker3", "marker4", "cno1", "cno2", "cno3"] as const;
export const MARKER_SAVE_AT_VALUES = ["self", "ctx", "cno1", "cno2", "cno3"] as const;
export const PORTAL_WHICH_VALUES = ["a", "b"] as const;

// ---------------------------------------------------------------------------
// Wave 1 (spec §3「統一セレクタ語彙」2026-08-11) — 対象セレクタ「だれに」
// ---------------------------------------------------------------------------
// 単数セレクタ = 1人に解決するもの。kill/teleport_other/remember 等の単発強効果 op はここまで。
export const TARGET_SINGLE_VALUES = ["self", "ctx", "saved1", "saved2", "nearest", "random"] as const;
// 複数セレクタ = 集合に解決するもの。受理する op は明示ホワイトリストのみ (Wave 1 は notify だけ)。
export const TARGET_MULTI_VALUES = ["all", "room"] as const;
export const TARGET_ANY_VALUES = [...TARGET_SINGLE_VALUES, ...TARGET_MULTI_VALUES] as const;
export type TargetSingle = (typeof TARGET_SINGLE_VALUES)[number];
export type TargetAny = (typeof TARGET_ANY_VALUES)[number];

// kill.target は self を含む (自殺が正当ユース) が、teleport_other.target は含まない —
// spec §3 のアクション表がその非対称のまま凍結されているので揃えない。
export const KILL_TARGET_VALUES = TARGET_SINGLE_VALUES;
export const TELEPORT_OTHER_TARGET_VALUES = ["ctx", "saved1", "saved2", "nearest", "random"] as const;
export const REMEMBER_TARGET_VALUES = TARGET_SINGLE_VALUES;
// remember の slot は 2 まで (cno の3 slot・marker の4 slot とは別枠・別定数 —
// どれか一つのレンジが将来変わっても連動しないようにする)。
export const REMEMBER_SLOT_MIN = 1;
export const REMEMBER_SLOT_MAX = 2;
// notify.target は「複数セレクタを受理する唯一の op」(spec §3 型規律)。省略時は self。
export const NOTIFY_TARGET_VALUES = TARGET_ANY_VALUES;
export const NOTIFY_TARGET_DEFAULT: TargetAny = "self";

// v1.3 (spec §3 2026-08-11 追記) — ひっぱる・ひきずる・フィールド
export const DRAG_SECONDS_MIN = 1;
export const DRAG_SECONDS_MAX = 10;
export const FIELD_AT_VALUES = ["self", "ctx", "marker1", "marker2", "marker3", "marker4"] as const;
export const FIELD_RADIUS_VALUES = ["small", "medium", "large"] as const;
export const FIELD_STRENGTH_VALUES = ["weak", "medium", "strong"] as const;
export const FIELD_SECONDS_MIN = 1;
export const FIELD_SECONDS_MAX = 15;

// ---------------------------------------------------------------------------
// Wave 2 (docs/ekn-wave2-contract.md 2026-08-11) — 情報と会議
// ---------------------------------------------------------------------------
// inspect/reveal/arrow_show/vote_block はどれも「self 不可の単数セレクタ」を共有する
// (契約 §2.1/§2.2/§2.3/§3.2「inspect と同じ受理値」)。TELEPORT_OTHER_TARGET_VALUES と
// 値集合が完全一致するため、意味を明示する別名として再利用する (別々に配列リテラルを
// 書くと将来どちらかだけ更新されてズレる)。
export const SELF_EXCLUDED_TARGET_VALUES = TELEPORT_OTHER_TARGET_VALUES;
export const INSPECT_TARGET_VALUES = SELF_EXCLUDED_TARGET_VALUES;
export const REVEAL_TARGET_VALUES = SELF_EXCLUDED_TARGET_VALUES;
export const ARROW_SHOW_TARGET_VALUES = SELF_EXCLUDED_TARGET_VALUES;
export const VOTE_BLOCK_TARGET_VALUES = SELF_EXCLUDED_TARGET_VALUES;
// exile.target は self を含む唯一の Wave 2 セレクタ (契約 §3.4「自分を追放させる」演出が正当ユース)。
export const EXILE_TARGET_VALUES = TARGET_SINGLE_VALUES;

export const INSPECT_DEPTH_VALUES = ["team", "role"] as const;
export const INSPECT_FAIL_CHANCE_MIN = 0;
export const INSPECT_FAIL_CHANCE_MAX = 100;
export const INSPECT_NOISE_MIN = 0;
export const INSPECT_NOISE_MAX = 5;

export const ARROW_SECONDS_MIN = 5;
export const ARROW_SECONDS_MAX = 600;
// arrow_mark.at は「どこ」セレクタだが self を含まない独自集合 (契約 §2.3 — teleport.to 等の
// 空間セレクタとは別枠。marker_save.at や teleport.to と値集合が重なる部分はあるが、
// 契約表がこの独自集合を明記しているため専用定数にする)。
export const ARROW_MARK_AT_VALUES = ["ctx", "marker1", "marker2", "marker3", "marker4", "cno1", "cno2", "cno3"] as const;

// vote_weight_set.value は passives.voteWeight (PASSIVE_VOTE_WEIGHT_MIN/MAX) と同じ範囲 0..3 だが、
// 「実行時オーバーライド」という別物のフィールドなので別定数にする (DUMMY_SPAWN_NAME_MAX と同じ方針 —
// 片方だけ将来変わっても連動しないように)。
export const VOTE_WEIGHT_SET_MIN = 0;
export const VOTE_WEIGHT_SET_MAX = 3;

// ---------------------------------------------------------------------------
// パッシブ層 passives (Wave 1・spec §1.1) — トップレベルの固定キーオブジェクト
// ---------------------------------------------------------------------------
// logic とは独立 (logic 無しでも passives 単独で可)。Blockly ワークスペースには置かない —
// 基本情報タブの「とくせい」フォームセクションが唯一の UI (spec §1.1 の裁定)。
// 検証: 内部の未知キーは黙って無視 / 既知キーの型不一致・範囲外は文書全体 reject (spec §1 総則)。
// ロジック op と数値レンジが重なるもの (speedMult ↔ speed.mult の 0.5〜3.0) も別定数にする —
// 片方だけ将来変わっても連動しないように (DUMMY_SPAWN_NAME_MAX と同じ方針)。
export const PASSIVE_SPEED_MULT_MIN = 0.5;
export const PASSIVE_SPEED_MULT_MAX = 3.0;
export const PASSIVE_KILL_DISTANCE_VALUES = ["short", "medium", "long"] as const;
export const PASSIVE_SHIELD_COUNT_MIN = 1;
export const PASSIVE_SHIELD_COUNT_MAX = 9;
export const PASSIVE_CORPSE_VALUES = ["normal", "noReport", "vanish"] as const;
export const PASSIVE_VOTE_WEIGHT_MIN = 0;
export const PASSIVE_VOTE_WEIGHT_MAX = 3;
export const PASSIVE_DOOM_SECONDS_MIN = 30;
export const PASSIVE_DOOM_SECONDS_MAX = 600;

export interface RolePassives {
    speedMult?: number;
    killDistance?: (typeof PASSIVE_KILL_DISTANCE_VALUES)[number];
    shield?: { count: number };
    corpse?: (typeof PASSIVE_CORPSE_VALUES)[number];
    voteWeight?: number;
    doom?: { seconds: number };
    // R2 (契約 §4): 表示層だけの陣営偽装。
    disguise?: { team: EkrTeam };
}

export interface LogicVariable {
    name: string;
    init: number;
}

export type LogicExpr =
    | { e: "lit"; v: number }
    | { e: "var"; name: string }
    | { e: "op"; kind: ExprOpKind; a: LogicExpr; b?: LogicExpr };

export type LogicNode =
    | { op: "if"; cond: LogicExpr; then: LogicNode[]; else?: LogicNode[] }
    | { op: "wait"; seconds: number }
    | { op: "stop" }
    | { op: "var_set"; name: string; value: LogicExpr }
    | { op: "var_add"; name: string; delta: LogicExpr }
    // target は任意 (省略 = self)。Wave 1 で複数セレクタ (all/room) を受理する唯一の op。
    // 省略された target は AST に足さない (再エンコード不動点を保つため — 既定値の明示化はしない)。
    | { op: "notify"; text: string; seconds: number; target?: TargetAny }
    | { op: "teleport"; to: (typeof TELEPORT_TO_VALUES)[number] }
    | { op: "kill"; target: TargetSingle }
    | { op: "set_kill_cooldown"; seconds: number }
    | { op: "speed"; mult: number; seconds: number }
    | { op: "cno_spawn"; slot: 1 | 2 | 3; text: string; size: number; at: "self" | "ctx" }
    // cno_move/cno_despawn/cno_show は3つとも対象を `slot` で指定し、move だけ追加で dx/dy、
    // show だけ追加で who を取る (despawn は slot のみ) — spec §3 で確定済み。
    // dx/dy は「出した時点のアンカー位置からの絶対オフセット」(spec §3 追記・C# 側確定事項)。
    // 呼ぶたびに積み上がる相対移動ではない (暴走ドリフト防止の裁定) — 同じ値で複数回呼んでも
    // 毎回同じ位置に着地する。compile-role.ts は Blockly の DX/DY フィールド値をそのまま
    // 転記するだけで良い (エディタ側で積算する必要はない)。
    | { op: "cno_move"; slot: 1 | 2 | 3; dx: number; dy: number }
    | { op: "cno_despawn"; slot: 1 | 2 | 3 }
    | { op: "cno_show"; slot: 1 | 2 | 3; who: "all" | "self" }
    // v1.1 (spec §3 2026-08-09 追記) — slot は cno_spawn/cno_move/cno_despawn と同じ枠を共有する
    // (「同時3 slot/ホルダー」の対象に dummy も入る)。cno_show はダミーには no-op (実装都合・仕様は
    // TS 側の検証対象外なのでここでは触れない)。
    | { op: "dummy_spawn"; slot: 1 | 2 | 3; name: string; killable: boolean; at: "self" | "ctx" }
    | { op: "corpse_spawn"; color: "self" | "random"; at: "self" | "ctx" }
    // v1.2 (spec §3 2026-08-10 追記) — 位置と接触。marker_save の slot は cno_spawn とは別枠
    // (per-holder の位置メモリ4スロット・§3「マーカー」)。
    | { op: "marker_save"; slot: 1 | 2 | 3 | 4; at: (typeof MARKER_SAVE_AT_VALUES)[number] }
    | { op: "teleport_other"; target: (typeof TELEPORT_OTHER_TARGET_VALUES)[number]; to: (typeof TELEPORT_OTHER_TO_VALUES)[number] }
    | { op: "portal_place"; which: (typeof PORTAL_WHICH_VALUES)[number] }
    // v1.3 (spec §3 2026-08-11 追記) — ひっぱる・ひきずる・フィールド。pull は引数なし
    // (ctx 暗黙。teleport_other の to:"self" と実行時は完全共有だが opcode としては独立 — spec §3)。
    | { op: "pull" }
    | { op: "drag"; seconds: number }
    | {
        op: "field";
        at: (typeof FIELD_AT_VALUES)[number];
        radius: (typeof FIELD_RADIUS_VALUES)[number];
        strength: (typeof FIELD_STRENGTH_VALUES)[number];
        seconds: number;
    }
    // Wave 1 (spec §3 2026-08-11) — marker_save の人間版。おぼえた人は死亡・切断で自動失効。
    | { op: "remember"; slot: 1 | 2; target: TargetSingle }
    // Wave 1 (spec §3 2026-08-11) — 引数なし。on_attacked 配下でのみ書ける (他イベント配下は
    // 実行時 no-op ではなく**検証 reject** — 静的に判定できるため厳格側に倒す裁定)。
    | { op: "cancel_attack" }
    // Wave 2 (docs/ekn-wave2-contract.md §2 2026-08-11) — しらべる系。failChance/noise は
    // 「任意・既定0」だが、この実装では Blockly ブロックが常に数値を持つため常に出力する
    // (省略時の意味と値0の意味が同じなので害はない — 実装裁量。手書き JSON では省略もできる)。
    | { op: "inspect"; target: (typeof INSPECT_TARGET_VALUES)[number]; depth: (typeof INSPECT_DEPTH_VALUES)[number]; failChance?: number; noise?: number }
    | { op: "reveal"; target: (typeof REVEAL_TARGET_VALUES)[number] }
    // Wave 2 (§2.3) — 矢印3 op。
    | { op: "arrow_show"; target: (typeof ARROW_SHOW_TARGET_VALUES)[number]; seconds: number }
    | { op: "arrow_mark"; at: (typeof ARROW_MARK_AT_VALUES)[number]; seconds: number }
    | { op: "arrow_hide" }
    // Wave 2 (§1.3) — 票をつかわずにえらぶ。on_meeting_vote 配下でのみ書ける (cancel_attack と
    // 同じ厳格側の裁定 — 他イベント配下は検証 reject)。
    | { op: "cancel_vote" }
    // Wave 2 (§3.1) — 常時 op (会議専用ではない)。
    | { op: "vote_weight_set"; value: number }
    // Wave 2 (§3.2〜§3.4) — 会議専用 op 3つ。配置 (会議系イベント配下か) は検証では強制せず
    // リンタ L18 のヒントに委ねる (契約: cancel_vote だけが構造的 reject 対象)。
    | { op: "vote_block"; target: (typeof VOTE_BLOCK_TARGET_VALUES)[number] }
    // vote_swap は引数なし (saved1/saved2 固定 — remember との合成を強制する設計)。
    | { op: "vote_swap" }
    | { op: "exile"; target: (typeof EXILE_TARGET_VALUES)[number] };

export interface LogicRule {
    when: LogicWhen;
    // on_cno_touch の必須フィールド (spec §2 v1.2)。他イベントでは付与禁止 (検証 reject)。
    slot?: 1 | 2 | 3;
    // R2: on_attacked / on_death 専用の任意フィルタ。省略 = 全種にマッチ。他イベントでは付与禁止。
    kind?: AttackKind;
    cause?: DeathCause;
    do: LogicNode[];
}

export interface RoleLogic {
    version: 1;
    variables: LogicVariable[];
    rules: LogicRule[];
    // Blockly serialization (再編集用)。C# はパースも型チェックもしない (spec §1) —
    // ここでも中身は一切検証せず、存在すれば値をそのまま通す (unknown のまま)。
    blockly?: unknown;
}

export interface EkrDefinition {
    ekr: 1;
    requires: string[];
    name: string;
    author: string;
    color: string;
    team: string;
    canKill: boolean;
    killCooldown: number;
    canVent: boolean;
    visionMultiplier: number;
    winCondition: string;
    // 省略 = R0 動作 (完全後方互換)。defaultEkrDefinition() には含めない
    // (rolecode.test.ts の凍結フィクスチャがこのキー無し形状に依存する)。
    logic?: RoleLogic;
    // Wave 1 (spec §1.1)。logic と同じ理由で defaultEkrDefinition() には含めない。
    // 既知キーが1つも無いとき (空オブジェクト) はキー自体を出力しない (バニラ既定 = 欠落)。
    passives?: RolePassives;
}

export function defaultEkrDefinition(): EkrDefinition {
    return {
        ekr: 1,
        requires: [],
        name: "",
        author: "",
        color: DEFAULT_COLOR,
        team: SUPPORTED_TEAM,
        canKill: false,
        killCooldown: KILL_COOLDOWN_DEFAULT,
        canVent: false,
        visionMultiplier: VISION_MULTIPLIER_DEFAULT,
        winCondition: DEFAULT_WIN_CONDITION,
    };
}

export type EkrValidationResult =
    | { ok: true; def: EkrDefinition }
    | { ok: false; error: string };

export type LogicValidationResult =
    | { ok: true; logic: RoleLogic }
    | { ok: false; error: string };

export type PassivesValidationResult =
    | { ok: true; passives: RolePassives }
    | { ok: false; error: string };

function isRecord(v: unknown): v is Record<string, unknown> {
    return typeof v === "object" && v !== null && !Array.isArray(v);
}

function clampNum(v: number, min: number, max: number): number {
    return Math.min(max, Math.max(min, v));
}

/**
 * #rrggbb (先頭 # 省略可) のみ受理し、大小文字はそのまま保持する (EkrDefinition.cs は正規化時に
 * 大小文字を変えない — ここで小文字化すると不要な逸脱になる)。不正なら既定色へ黙ってフォールバック。
 * killCooldown/visionMultiplier と同様、UI からもラウンドトリップの安全弁として直接呼べるよう export する。
 * 意図して緩いまま (型不一致の厳格化対象外) — フォーマット妥当性の話であり型の話ではない。
 * 呼び出し元 (validateEkrDefinition) 側で「色フィールドの型自体が string でない」場合は先に reject する。
 */
export function normalizeColor(raw: unknown): string {
    if (typeof raw !== "string") return DEFAULT_COLOR;
    const s = raw.trim();
    if (s.length === 0) return DEFAULT_COLOR;
    const withHash = s.startsWith("#") ? s : `#${s}`;
    return COLOR_RE.test(withHash) ? withHash : DEFAULT_COLOR;
}

/**
 * killCooldown の唯一のクランプ実装 (検証・フォーム双方から呼ぶ — 実装が2つに分かれると
 * どちらかだけ更新され忘れる事故になる)。有限数でなければ既定値、そうでなければ 1〜180 にクランプ。
 * 四捨五入はしない (C# も float のまま保持するため 27.5 等の小数は契約上合法)。
 * normalizeColor と同様、意図して緩いまま (localStorage 下書き復元がこの緩さに依存する)。
 */
export function normalizeKillCooldown(raw: unknown): number {
    const n = typeof raw === "number" ? raw : NaN;
    return Number.isFinite(n) ? clampNum(n, KILL_COOLDOWN_MIN, KILL_COOLDOWN_MAX) : KILL_COOLDOWN_DEFAULT;
}

/** visionMultiplier の唯一のクランプ実装。0.25〜5、既定 1 (normalizeKillCooldown と同じ方針)。 */
export function normalizeVisionMultiplier(raw: unknown): number {
    const n = typeof raw === "number" ? raw : NaN;
    return Number.isFinite(n) ? clampNum(n, VISION_MULTIPLIER_MIN, VISION_MULTIPLIER_MAX) : VISION_MULTIPLIER_DEFAULT;
}

// ---------------------------------------------------------------------------
// R0 フィールド読み取りヘルパー (値型/参照型で挙動が違う — ファイル冒頭のコメント参照)
// ---------------------------------------------------------------------------

type FieldRead<T> = { ok: true; v: T } | { ok: false; error: string };

/** bool フィールド (canKill/canVent): 省略→false / null→拒否 / 型不一致→拒否 */
function readBoolField(value: Record<string, unknown>, key: string): FieldRead<boolean> {
    const raw = value[key];
    if (raw === undefined) return { ok: true, v: false };
    if (raw === null || typeof raw !== "boolean") {
        return { ok: false, error: `役職コードの読み取りに失敗しました (${key} は true/false である必要があります)` };
    }
    return { ok: true, v: raw };
}

/**
 * number フィールド (killCooldown/visionMultiplier): 省略→既定値 / null→拒否 / 型不一致→拒否 /
 * 非有限 (NaN・Infinity オーバーフロー) → 既定値へフォールバック (reject しない — C# の
 * `float.IsFinite(x) ? x : 既定値` と同じ)。範囲クランプは呼び出し元の責務のまま。
 */
function readNumberField(value: Record<string, unknown>, key: string, defaultVal: number): FieldRead<number> {
    const raw = value[key];
    if (raw === undefined) return { ok: true, v: defaultVal };
    if (raw === null || typeof raw !== "number") {
        return { ok: false, error: `役職コードの読み取りに失敗しました (${key} は数値である必要があります)` };
    }
    return { ok: true, v: Number.isFinite(raw) ? raw : defaultVal };
}

/** string フィールド (name/author/color/team/winCondition): 省略/null→既定値 / 型不一致→拒否 */
function readStringField(value: Record<string, unknown>, key: string, defaultVal: string): FieldRead<string> {
    const raw = value[key];
    if (raw === undefined || raw === null) return { ok: true, v: defaultVal };
    if (typeof raw !== "string") {
        return { ok: false, error: `役職コードの読み取りに失敗しました (${key} は文字列である必要があります)` };
    }
    return { ok: true, v: raw };
}

/**
 * JSON.parse 済みの値を検証して EkrDefinition に変換する (Modules/Ekm/EkrDefinition.cs の
 * Validate() と同じ規則)。失敗時は日本語の平易文でエラーを返す。数値クランプ/文字列切詰め/色
 * フォーマットのフォールバックは黙って補正する (エラーにしない) — C# 側も同様。一方 JSON の
 * 型不一致 (期待する型と違う値が来た) は文書全体を reject する (ファイル冒頭コメント参照)。
 */
export function validateEkrDefinition(value: unknown): EkrValidationResult {
    if (!isRecord(value)) {
        return { ok: false, error: "役職コードの中身が正しくありません (JSON オブジェクトが必要です)" };
    }

    const ekr = value.ekr;
    if (ekr !== 1) {
        return { ok: false, error: `このバージョンの End K not では読み込めない役職コードです (ekr=${JSON.stringify(ekr)})。End K not を更新してください` };
    }

    const rawRequires = value.requires;
    let requires: string[];
    if (rawRequires === undefined || rawRequires === null) {
        requires = [];
    } else if (Array.isArray(rawRequires) && rawRequires.every((x) => typeof x === "string")) {
        requires = rawRequires as string[];
    } else {
        return { ok: false, error: "役職コードの読み取りに失敗しました (requires が文字列の配列ではありません)" };
    }
    for (const cap of requires) {
        if (!SUPPORTED_CAPABILITIES.has(cap)) {
            return { ok: false, error: `この役職コードは未対応の機能 (${cap}) を必要としています。End K not を更新するか、対応済みの役職コードを使ってください` };
        }
    }

    const nameField = readStringField(value, "name", "");
    if (!nameField.ok) return nameField;
    let name = nameField.v.trim();
    if (name.length === 0) {
        return { ok: false, error: "役職コードに名前 (name) がありません" };
    }
    if (name.length > ROLE_NAME_MAX) name = name.slice(0, ROLE_NAME_MAX);

    const authorField = readStringField(value, "author", "");
    if (!authorField.ok) return authorField;
    let author = authorField.v.trim();
    if (author.length > ROLE_AUTHOR_MAX) author = author.slice(0, ROLE_AUTHOR_MAX);

    const colorField = readStringField(value, "color", "");
    if (!colorField.ok) return colorField;
    const color = normalizeColor(colorField.v);

    // team: キー省略/null は既定 "crewmate" に収束するが、明示的な空文字/対象外の値はエラー
    // (C# の `(Team ?? "crewmate").Trim().ToLowerInvariant()` → switch と同じ非対称性)。
    const teamField = readStringField(value, "team", SUPPORTED_TEAM);
    if (!teamField.ok) return teamField;
    const team = teamField.v.trim().toLowerCase();
    if (!(SUPPORTED_TEAMS as readonly string[]).includes(team)) {
        return { ok: false, error: `team="${team}" は使えません (使えるのは ${SUPPORTED_TEAMS.join(" / ")} の3つです)` };
    }

    const canKillField = readBoolField(value, "canKill");
    if (!canKillField.ok) return canKillField;
    const canKill = canKillField.v;

    const canVentField = readBoolField(value, "canVent");
    if (!canVentField.ok) return canVentField;
    const canVent = canVentField.v;

    const killCooldownField = readNumberField(value, "killCooldown", KILL_COOLDOWN_DEFAULT);
    if (!killCooldownField.ok) return killCooldownField;
    const killCooldown = clampNum(killCooldownField.v, KILL_COOLDOWN_MIN, KILL_COOLDOWN_MAX);

    const visionField = readNumberField(value, "visionMultiplier", VISION_MULTIPLIER_DEFAULT);
    if (!visionField.ok) return visionField;
    const visionMultiplier = clampNum(visionField.v, VISION_MULTIPLIER_MIN, VISION_MULTIPLIER_MAX);

    // winCondition: team と違い R0 でも他の値をそのまま保持する (C# 側にエラー分岐が無い —
    // ゲーム側が現状常に通常のクルー勝利にフォールバックして使うだけの未使用フィールド)。
    const winField = readStringField(value, "winCondition", DEFAULT_WIN_CONDITION);
    if (!winField.ok) return winField;
    const winCondition = winField.v.trim().toLowerCase();

    const def: EkrDefinition = { ekr: 1, requires, name, author, color, team, canKill, killCooldown, canVent, visionMultiplier, winCondition };

    // logic: 省略/null = R0 動作 (キー自体を付けない)。存在すれば全体を検証し、失敗時は
    // 役職コード全体を reject する (「壊れたロジックだけ黙って捨てる」は spec の方針に反する)。
    if (value.logic !== undefined && value.logic !== null) {
        const logicResult = validateRoleLogic(value.logic);
        if (!logicResult.ok) return { ok: false, error: logicResult.error };
        def.logic = logicResult.logic;
    }

    // passives (Wave 1・spec §1.1): logic と同じ扱い — 省略/null は「とくせい無し」、
    // 中身が壊れていれば役職コード全体を reject する。既知キーが1つも無ければキーを付けない
    // (バニラ既定はキーの欠落で表すため、空オブジェクトを書き出す意味が無い)。
    if (value.passives !== undefined && value.passives !== null) {
        const passivesResult = validatePassives(value.passives);
        if (!passivesResult.ok) return { ok: false, error: passivesResult.error };
        if (Object.keys(passivesResult.passives).length > 0) def.passives = passivesResult.passives;
    }

    return { ok: true, def };
}

// ---------------------------------------------------------------------------
// Logic 検証本体 (docs/ekr-logic-spec.md §2〜§4)
// ---------------------------------------------------------------------------

class EkrLogicError extends Error {}

function fail(msg: string): never {
    throw new EkrLogicError(msg);
}

function expectRangeNumber(raw: unknown, min: number, max: number, path: string): number {
    if (typeof raw !== "number" || !Number.isFinite(raw)) fail(`${path} は数値である必要があります`);
    if (raw < min || raw > max) fail(`${path} は ${min}〜${max} の範囲である必要があります (現在 ${raw})`);
    return raw;
}

function expectRangeInt(raw: unknown, min: number, max: number, path: string): number {
    const n = expectRangeNumber(raw, min, max, path);
    if (!Number.isInteger(n)) fail(`${path} は整数である必要があります (現在 ${n})`);
    return n;
}

function expectString(raw: unknown, maxLen: number, path: string): string {
    if (typeof raw !== "string") fail(`${path} は文字列である必要があります`);
    if (raw.length > maxLen) fail(`${path} は ${maxLen} 文字までです`);
    return raw;
}

/**
 * notify.text / cno_spawn.text 限定のサニタイズ (spec §3: 「パース時に全角 〈〉 へサニタイズ」—
 * TMP タグ注入防御)。C# 側もパース (Validate) 時に同じ変換を行い、その結果を正準値として
 * 保持する — TS 側もここで同じ変換を適用し、検証後の値が両実装で一致するようにする。
 * 呼び出し順は sanitizeAngleBrackets(expectString(...)) — 文字数チェック→サニタイズの順。
 * C# 側 (EkmLogicRuntime.TryGetString → SanitizeUserText) も同じ順序で reject 判定してから変換する
 * (どちらも「上限超過は reject であり truncate ではない」で揃っている)。`<`→`〈` `>`→`〉` は
 * 1 UTF-16 単位→1 UTF-16 単位の置換なので長さを変えず、順序を入れ替えても結果は変わらない
 * (sanitize してから文字数を数えても同じ reject/accept 判定になる) — が、C# と明示的に順序を
 * 揃えてあるので、どちらかだけ変えないこと。
 * 他の文字列フィールド (変数名・color 等) には適用しない。
 */
function sanitizeAngleBrackets(s: string): string {
    return s.replace(/</g, "〈").replace(/>/g, "〉");
}

function expectEnum<T extends string>(raw: unknown, options: readonly T[], path: string): T {
    if (typeof raw !== "string" || !(options as readonly string[]).includes(raw)) {
        fail(`${path} の値が正しくありません (${JSON.stringify(raw)})`);
    }
    return raw as T;
}

/** dummy_spawn.killable 専用 (spec §3 v1.1)。canKill/canVent (R0 field) と同じく、1/0 等は許容せず
 *  真の JSON boolean のみを受理する (compile-role.ts が Blockly の "1"/"0" 文字列から変換してから渡す)。 */
function expectBoolean(raw: unknown, path: string): boolean {
    if (typeof raw !== "boolean") fail(`${path} は true/false である必要があります`);
    return raw;
}

function expectVarName(raw: unknown, varNames: ReadonlySet<string>, path: string): string {
    if (typeof raw !== "string" || !varNames.has(raw)) {
        fail(`${path} が未定義の変数を参照しています (${JSON.stringify(raw)})`);
    }
    return raw;
}

interface ExprValidation { expr: LogicExpr; depth: number }
interface NodeValidation { node: LogicNode; depth: number; count: number }
interface NodeArrayValidation { nodes: LogicNode[]; depth: number; count: number }

// AST 深さ (spec §1: 「node と expr を合算」・上限8) の数え方 — spec §1 (2026-08-09 裁定) が
// この実装を正典として明文化した (「子へ潜る遷移はすべて +1 — if.then/if.else の子 node、
// op 式の a/b、および node から自身の expr への突入 (if.cond/var_set.value/var_add.delta) も
// +1。expr 起点でも制御起点でも数え方は同一」)。1本の木として「ノード配下へ潜る」
// 「式配下へ潜る」の両方を同じカウンタで +1 する (ノード用・式用の2つの独立予算ではない):
//   depth(葉ノード/葉式) = 1
//   depth(if ノード)      = 1 + max(depth(cond式), depth(then配列), depth(else配列))
//   depth(値を持つノード: var_set/var_add) = 1 + depth(値の式)
//   depth(op式)           = 1 + max(depth(a), depth(b))  (kind="not" は b 無し)
//   depth(ノード配列)     = 配列内ノードの depth の最大値 (0 個なら 0 — 空配列は深さを増やさない)
//   ルール全体の深さ = depth(rule.do)
function validateExpr(raw: unknown, varNames: ReadonlySet<string>, path: string): ExprValidation {
    if (!isRecord(raw)) fail(`${path} の式が正しくありません`);
    const e = raw.e;
    if (e === "lit") {
        const v = raw.v;
        if (typeof v !== "number" || !Number.isFinite(v)) fail(`${path}.v は数値である必要があります`);
        return { expr: { e: "lit", v }, depth: 1 };
    }
    if (e === "var") {
        const name = expectVarName(raw.name, varNames, `${path}.name`);
        return { expr: { e: "var", name }, depth: 1 };
    }
    if (e === "op") {
        const kind = raw.kind;
        if (typeof kind !== "string" || !EXPR_OP_KIND_SET.has(kind)) {
            fail(`${path}.kind が不明です (${JSON.stringify(kind)})`);
        }
        const a = validateExpr(raw.a, varNames, `${path}.a`);
        if (kind === "not") {
            // spec §4: not は a のみ。b が同梱されていても (C# の JSON 逆シリアライズが
            // 余分なプロパティを無視するのと同じく) 無視するだけで reject しない。
            return { expr: { e: "op", kind: kind as ExprOpKind, a: a.expr }, depth: 1 + a.depth };
        }
        const b = validateExpr(raw.b, varNames, `${path}.b`);
        return { expr: { e: "op", kind: kind as ExprOpKind, a: a.expr, b: b.expr }, depth: 1 + Math.max(a.depth, b.depth) };
    }
    fail(`${path}.e の種類が不明です (${JSON.stringify(e)})`);
}

// Wave 1: cancel_attack の配置検査 (on_attacked 以外の rule 配下なら reject) のために、
// 「今どのイベント配下を検証しているか」を do 配列の入れ子の奥まで持ち回る。if の then/else を
// 通っても値は変わらないので、入れ子の中の cancel_attack も同じ判定になる。
function validateNodeArray(raw: unknown, varNames: ReadonlySet<string>, path: string, when: string): NodeArrayValidation {
    if (!Array.isArray(raw)) fail(`${path} は配列である必要があります`);
    let depth = 0;
    let count = 0;
    const nodes: LogicNode[] = [];
    raw.forEach((item, i) => {
        const r = validateNode(item, varNames, `${path}[${i}]`, when);
        nodes.push(r.node);
        depth = Math.max(depth, r.depth);
        count += r.count;
    });
    return { nodes, depth, count };
}

function validateNode(raw: unknown, varNames: ReadonlySet<string>, path: string, when: string): NodeValidation {
    if (!isRecord(raw)) fail(`${path} のノードが正しくありません`);
    const op = raw.op;
    switch (op) {
        case "if": {
            const cond = validateExpr(raw.cond, varNames, `${path}.cond`);
            if (raw.then === undefined || raw.then === null) fail(`${path}.then が必要です`);
            const thenList = validateNodeArray(raw.then, varNames, `${path}.then`, when);
            const hasElse = raw.else !== undefined && raw.else !== null;
            const elseList = hasElse ? validateNodeArray(raw.else, varNames, `${path}.else`, when) : { nodes: [] as LogicNode[], depth: 0, count: 0 };
            const node: LogicNode = hasElse
                ? { op: "if", cond: cond.expr, then: thenList.nodes, else: elseList.nodes }
                : { op: "if", cond: cond.expr, then: thenList.nodes };
            return { node, depth: 1 + Math.max(cond.depth, thenList.depth, elseList.depth), count: 1 + thenList.count + elseList.count };
        }
        case "wait": {
            const seconds = expectRangeNumber(raw.seconds, WAIT_SECONDS_MIN, WAIT_SECONDS_MAX, `${path}.seconds`);
            return { node: { op: "wait", seconds }, depth: 1, count: 1 };
        }
        case "stop":
            return { node: { op: "stop" }, depth: 1, count: 1 };
        case "var_set": {
            const name = expectVarName(raw.name, varNames, `${path}.name`);
            const value = validateExpr(raw.value, varNames, `${path}.value`);
            return { node: { op: "var_set", name, value: value.expr }, depth: 1 + value.depth, count: 1 };
        }
        case "var_add": {
            const name = expectVarName(raw.name, varNames, `${path}.name`);
            const delta = validateExpr(raw.delta, varNames, `${path}.delta`);
            return { node: { op: "var_add", name, delta: delta.expr }, depth: 1 + delta.depth, count: 1 };
        }
        case "notify": {
            const text = sanitizeAngleBrackets(expectString(raw.text, NOTIFY_TEXT_MAX, `${path}.text`));
            const seconds = expectRangeNumber(raw.seconds, NOTIFY_SECONDS_MIN, NOTIFY_SECONDS_MAX, `${path}.seconds`);
            // Wave 1 (spec §3): target は任意・既定 self・複数セレクタ (all/room) を受理する唯一の op。
            // 省略時は AST にキーを足さない (「既定値の明示化」をすると再エンコード不動点が崩れる)。
            if (raw.target === undefined) return { node: { op: "notify", text, seconds }, depth: 1, count: 1 };
            const target = expectEnum(raw.target, NOTIFY_TARGET_VALUES, `${path}.target`);
            return { node: { op: "notify", text, seconds, target }, depth: 1, count: 1 };
        }
        case "teleport": {
            const to = expectEnum(raw.to, TELEPORT_TO_VALUES, `${path}.to`);
            return { node: { op: "teleport", to }, depth: 1, count: 1 };
        }
        case "kill": {
            // Wave 1: 単数セレクタ全種 (self/ctx/saved1/saved2/nearest/random) を受理。
            const target = expectEnum(raw.target, KILL_TARGET_VALUES, `${path}.target`);
            return { node: { op: "kill", target }, depth: 1, count: 1 };
        }
        case "set_kill_cooldown": {
            const seconds = expectRangeNumber(raw.seconds, LOGIC_SET_KILL_COOLDOWN_MIN, LOGIC_SET_KILL_COOLDOWN_MAX, `${path}.seconds`);
            return { node: { op: "set_kill_cooldown", seconds }, depth: 1, count: 1 };
        }
        case "speed": {
            const mult = expectRangeNumber(raw.mult, SPEED_MULT_MIN, SPEED_MULT_MAX, `${path}.mult`);
            const seconds = expectRangeNumber(raw.seconds, SPEED_SECONDS_MIN, SPEED_SECONDS_MAX, `${path}.seconds`);
            return { node: { op: "speed", mult, seconds }, depth: 1, count: 1 };
        }
        case "cno_spawn": {
            const slot = expectRangeInt(raw.slot, CNO_SLOT_MIN, CNO_SLOT_MAX, `${path}.slot`) as 1 | 2 | 3;
            const text = sanitizeAngleBrackets(expectString(raw.text, CNO_SPAWN_TEXT_MAX, `${path}.text`));
            const size = expectRangeInt(raw.size, CNO_SPAWN_SIZE_MIN, CNO_SPAWN_SIZE_MAX, `${path}.size`);
            const at = expectEnum(raw.at, ["self", "ctx"] as const, `${path}.at`);
            return { node: { op: "cno_spawn", slot, text, size, at }, depth: 1, count: 1 };
        }
        case "cno_move": {
            const slot = expectRangeInt(raw.slot, CNO_SLOT_MIN, CNO_SLOT_MAX, `${path}.slot`) as 1 | 2 | 3;
            const dx = expectRangeNumber(raw.dx, CNO_MOVE_DELTA_MIN, CNO_MOVE_DELTA_MAX, `${path}.dx`);
            const dy = expectRangeNumber(raw.dy, CNO_MOVE_DELTA_MIN, CNO_MOVE_DELTA_MAX, `${path}.dy`);
            return { node: { op: "cno_move", slot, dx, dy }, depth: 1, count: 1 };
        }
        case "cno_despawn": {
            const slot = expectRangeInt(raw.slot, CNO_SLOT_MIN, CNO_SLOT_MAX, `${path}.slot`) as 1 | 2 | 3;
            return { node: { op: "cno_despawn", slot }, depth: 1, count: 1 };
        }
        case "cno_show": {
            const slot = expectRangeInt(raw.slot, CNO_SLOT_MIN, CNO_SLOT_MAX, `${path}.slot`) as 1 | 2 | 3;
            const who = expectEnum(raw.who, ["all", "self"] as const, `${path}.who`);
            return { node: { op: "cno_show", slot, who }, depth: 1, count: 1 };
        }
        case "dummy_spawn": {
            // v1.1 (spec §3): slot は cno_spawn と共有の同じ枠。
            const slot = expectRangeInt(raw.slot, CNO_SLOT_MIN, CNO_SLOT_MAX, `${path}.slot`) as 1 | 2 | 3;
            // name の検証順序は cno_spawn.text (「文字数チェック→サニタイズ」を raw のまま行う) とは
            // 意図して異なる: ここは trim → サニタイズ → 文字数チェック → 空なら既定名、の順。
            // spec §3 (2026-08-09 裁定) がこの順序を明文で規定しており、C# 側 (EkmLogicRuntime の
            // dummy_spawn case) も同順序で実装済み。前後の空白を詰めた結果が8字以内なら通る
            // (例: 生の長さが8字を超えていても trim 後に収まれば受理する)。
            if (typeof raw.name !== "string") fail(`${path}.name は文字列である必要があります`);
            const trimmedName = sanitizeAngleBrackets(raw.name.trim());
            if (trimmedName.length > DUMMY_SPAWN_NAME_MAX) {
                fail(`${path}.name は ${DUMMY_SPAWN_NAME_MAX} 文字までです`);
            }
            const name = trimmedName.length === 0 ? DUMMY_SPAWN_DEFAULT_NAME : trimmedName;
            const killable = expectBoolean(raw.killable, `${path}.killable`);
            const at = expectEnum(raw.at, ["self", "ctx"] as const, `${path}.at`);
            return { node: { op: "dummy_spawn", slot, name, killable, at }, depth: 1, count: 1 };
        }
        case "corpse_spawn": {
            const color = expectEnum(raw.color, ["self", "random"] as const, `${path}.color`);
            const at = expectEnum(raw.at, ["self", "ctx"] as const, `${path}.at`);
            return { node: { op: "corpse_spawn", color, at }, depth: 1, count: 1 };
        }
        case "marker_save": {
            // v1.2 (spec §3): slot は per-holder の位置メモリ4スロット (cno_spawn の3 slot 枠とは別)。
            const slot = expectRangeInt(raw.slot, MARKER_SLOT_MIN, MARKER_SLOT_MAX, `${path}.slot`) as 1 | 2 | 3 | 4;
            const at = expectEnum(raw.at, MARKER_SAVE_AT_VALUES, `${path}.at`);
            return { node: { op: "marker_save", slot, at }, depth: 1, count: 1 };
        }
        case "teleport_other": {
            // v1.2 では "ctx" の一択だったが、Wave 1 で単数セレクタへ拡張 (self を含まないのは
            // spec §3 のアクション表どおり — 自分を飛ばすのは teleport の役目)。
            const target = expectEnum(raw.target, TELEPORT_OTHER_TARGET_VALUES, `${path}.target`);
            const to = expectEnum(raw.to, TELEPORT_OTHER_TO_VALUES, `${path}.to`);
            return { node: { op: "teleport_other", target, to }, depth: 1, count: 1 };
        }
        case "portal_place": {
            const which = expectEnum(raw.which, PORTAL_WHICH_VALUES, `${path}.which`);
            return { node: { op: "portal_place", which }, depth: 1, count: 1 };
        }
        // v1.3 (spec §3): pull/drag/field
        case "pull":
            return { node: { op: "pull" }, depth: 1, count: 1 };
        case "drag": {
            const seconds = expectRangeNumber(raw.seconds, DRAG_SECONDS_MIN, DRAG_SECONDS_MAX, `${path}.seconds`);
            return { node: { op: "drag", seconds }, depth: 1, count: 1 };
        }
        case "field": {
            const at = expectEnum(raw.at, FIELD_AT_VALUES, `${path}.at`);
            const radius = expectEnum(raw.radius, FIELD_RADIUS_VALUES, `${path}.radius`);
            const strength = expectEnum(raw.strength, FIELD_STRENGTH_VALUES, `${path}.strength`);
            const seconds = expectRangeNumber(raw.seconds, FIELD_SECONDS_MIN, FIELD_SECONDS_MAX, `${path}.seconds`);
            return { node: { op: "field", at, radius, strength, seconds }, depth: 1, count: 1 };
        }
        // Wave 1 (spec §3 2026-08-11)
        case "remember": {
            const slot = expectRangeInt(raw.slot, REMEMBER_SLOT_MIN, REMEMBER_SLOT_MAX, `${path}.slot`) as 1 | 2;
            const target = expectEnum(raw.target, REMEMBER_TARGET_VALUES, `${path}.target`);
            return { node: { op: "remember", slot, target }, depth: 1, count: 1 };
        }
        case "cancel_attack": {
            // spec §3: on_attacked 以外の rule 配下に現れたら文書 reject (静的に検査できるので
            // no-op ではなく reject 側に倒す — on_cno_touch の slot 必須と同じ厳格側の裁定)。
            // if の入れ子の中でも when は持ち回られているため同じ判定になる。
            if (when !== "on_attacked") {
                fail(`${path} の「こうげきをふせぐ」はイベント "${when}" では使えません (「こうげきされたとき」の中だけで使えます)`);
            }
            return { node: { op: "cancel_attack" }, depth: 1, count: 1 };
        }
        // Wave 2 (docs/ekn-wave2-contract.md §2 2026-08-11) — しらべる系
        case "inspect": {
            const target = expectEnum(raw.target, INSPECT_TARGET_VALUES, `${path}.target`);
            const depth = expectEnum(raw.depth, INSPECT_DEPTH_VALUES, `${path}.depth`);
            const failChance = raw.failChance !== undefined
                ? expectRangeInt(raw.failChance, INSPECT_FAIL_CHANCE_MIN, INSPECT_FAIL_CHANCE_MAX, `${path}.failChance`)
                : undefined;
            let noise: number | undefined;
            if (raw.noise !== undefined) {
                noise = expectRangeInt(raw.noise, INSPECT_NOISE_MIN, INSPECT_NOISE_MAX, `${path}.noise`);
                // 契約 §2.1: noise は depth:"role" のみ受理。noise=0 は「まぜない」既定と同義なので
                // team との併用でも害が無く許容する (0 は実質「noise を使っていない」と区別できない —
                // 実装裁量。1以上を team と併用したときだけ reject する)。
                if (depth === "team" && noise > 0) {
                    fail(`${path}.noise は depth:"role" のときだけ使えます (depth:"team" では意味がありません)`);
                }
            }
            const node: LogicNode = {
                op: "inspect", target, depth,
                ...(failChance !== undefined ? { failChance } : {}),
                ...(noise !== undefined ? { noise } : {}),
            };
            return { node, depth: 1, count: 1 };
        }
        case "reveal": {
            const target = expectEnum(raw.target, REVEAL_TARGET_VALUES, `${path}.target`);
            return { node: { op: "reveal", target }, depth: 1, count: 1 };
        }
        case "arrow_show": {
            const target = expectEnum(raw.target, ARROW_SHOW_TARGET_VALUES, `${path}.target`);
            const seconds = expectRangeNumber(raw.seconds, ARROW_SECONDS_MIN, ARROW_SECONDS_MAX, `${path}.seconds`);
            return { node: { op: "arrow_show", target, seconds }, depth: 1, count: 1 };
        }
        case "arrow_mark": {
            const at = expectEnum(raw.at, ARROW_MARK_AT_VALUES, `${path}.at`);
            const seconds = expectRangeNumber(raw.seconds, ARROW_SECONDS_MIN, ARROW_SECONDS_MAX, `${path}.seconds`);
            return { node: { op: "arrow_mark", at, seconds }, depth: 1, count: 1 };
        }
        case "arrow_hide":
            return { node: { op: "arrow_hide" }, depth: 1, count: 1 };
        // Wave 2 (§1.3) — 票をつかわずにえらぶ。cancel_attack と同じ配置検査 (静的に判定できるので reject)。
        case "cancel_vote": {
            if (when !== "on_meeting_vote") {
                fail(`${path} の「票をつかわずにえらぶ」はイベント "${when}" では使えません (「かいぎで投票したとき」の中だけで使えます)`);
            }
            return { node: { op: "cancel_vote" }, depth: 1, count: 1 };
        }
        // Wave 2 (§3.1〜§3.4) — 票操作。vote_block/vote_swap/exile は配置を検証で強制しない
        // (契約: 会議専用 op の配置ヒントはリンタ L18 に委ねる — cancel_vote だけが構造的 reject)。
        case "vote_weight_set": {
            const value = expectRangeInt(raw.value, VOTE_WEIGHT_SET_MIN, VOTE_WEIGHT_SET_MAX, `${path}.value`);
            return { node: { op: "vote_weight_set", value }, depth: 1, count: 1 };
        }
        case "vote_block": {
            const target = expectEnum(raw.target, VOTE_BLOCK_TARGET_VALUES, `${path}.target`);
            return { node: { op: "vote_block", target }, depth: 1, count: 1 };
        }
        case "vote_swap":
            return { node: { op: "vote_swap" }, depth: 1, count: 1 };
        case "exile": {
            const target = expectEnum(raw.target, EXILE_TARGET_VALUES, `${path}.target`);
            return { node: { op: "exile", target }, depth: 1, count: 1 };
        }
        default:
            fail(`${path}.op が不明です (${JSON.stringify(op)})`);
    }
}

function validateRule(raw: unknown, varNames: ReadonlySet<string>, index: number): LogicRule {
    if (!isRecord(raw)) fail(`rules[${index}] が正しくありません`);
    const when = raw.when;
    if (typeof when !== "string" || !LOGIC_WHEN_SET.has(when)) {
        fail(`rules[${index}].when が不明です (${JSON.stringify(when)})`);
    }

    // v1.2 (spec §2): on_cno_touch は必須フィールド slot (1..3) を持つ唯一のイベント。
    // 他イベントで slot が同梱されていれば reject (逆方向も reject)。
    let slot: 1 | 2 | 3 | undefined;
    if (when === "on_cno_touch") {
        slot = expectRangeInt(raw.slot, CNO_SLOT_MIN, CNO_SLOT_MAX, `rules[${index}].slot`) as 1 | 2 | 3;
    } else if (raw.slot !== undefined) {
        fail(`rules[${index}].slot はイベント "${when}" では使えません (on_cno_touch 専用です)`);
    }

    // R2 (契約 §3b): kind は on_attacked 専用 / cause は on_death 専用。省略可 (= 全種にマッチ)。
    // 他イベントに付いていたら slot と同じく reject する。
    const kind = readRuleFilter(raw, "kind", when, "on_attacked", ATTACK_KIND_VALUES, index) as AttackKind | undefined;
    const cause = readRuleFilter(raw, "cause", when, "on_death", DEATH_CAUSE_VALUES, index) as DeathCause | undefined;

    const doPath = `rules[${index}].do`;
    if (raw.do === undefined || raw.do === null) fail(`${doPath} が必要です`);
    const doResult = validateNodeArray(raw.do, varNames, doPath, when);
    if (doResult.count < LOGIC_RULE_NODES_MIN || doResult.count > LOGIC_RULE_NODES_MAX) {
        fail(`${doPath} のノード数は ${LOGIC_RULE_NODES_MIN}〜${LOGIC_RULE_NODES_MAX} 個である必要があります (現在 ${doResult.count} 個)`);
    }
    if (doResult.depth > LOGIC_AST_DEPTH_MAX) {
        fail(`${doPath} の入れ子が深すぎます (上限 ${LOGIC_AST_DEPTH_MAX})`);
    }
    const rule: LogicRule = { when: when as LogicWhen, do: doResult.nodes };
    if (slot !== undefined) rule.slot = slot;
    if (kind !== undefined) rule.kind = kind;
    if (cause !== undefined) rule.cause = cause;
    return rule;
}

/** rule 直下の任意フィルタ (kind/cause) を読む。指定できるイベントが決まっている (R2 契約 §3b)。 */
function readRuleFilter(
    raw: Record<string, unknown>,
    field: string,
    when: string,
    onlyEvent: LogicWhen,
    allowed: readonly string[],
    index: number,
): string | undefined {
    const v = raw[field];
    if (v === undefined) return undefined;
    if (when !== onlyEvent) {
        fail(`rules[${index}].${field} はイベント "${when}" では使えません (${onlyEvent} 専用です)`);
    }
    return expectEnum(v, allowed, `rules[${index}].${field}`);
}

/**
 * `logic` オブジェクト (EkrDefinition の任意フィールド) を検証する。docs/ekr-logic-spec.md §1〜§4
 * のミラー。失敗時は日本語の平易文でエラーを返す — 呼び出し元 (validateEkrDefinition) は
 * これを役職コード全体の reject に変換する (ロジックだけ黙って捨てることはしない)。
 */
export function validateRoleLogic(value: unknown): LogicValidationResult {
    try {
        if (!isRecord(value)) fail("logic の中身が正しくありません (JSON オブジェクトが必要です)");

        if (value.version !== LOGIC_VERSION) {
            fail(`このバージョンの End K not では読み込めないロジックです (logic.version=${JSON.stringify(value.version)})。End K not を更新してください`);
        }

        const variables: LogicVariable[] = [];
        const rawVariables = value.variables;
        // spec §1 (2026-08-11 裁定): 省略 (undefined) だけが「変数なし」。明示的な null は
        // **型不一致として文書全体 reject** (「JSON 型不一致は文書全体 reject」の総則どおり —
        // C# 側は List<T> へ null が来ても後段で落ちるため、TS だけ寛容にしない)。
        if (rawVariables !== undefined) {
            if (!Array.isArray(rawVariables)) fail("logic.variables は配列である必要があります");
            if (rawVariables.length > LOGIC_VARIABLES_MAX) fail(`変数は最大 ${LOGIC_VARIABLES_MAX} 個までです`);
            const seen = new Set<string>();
            rawVariables.forEach((raw, i) => {
                if (!isRecord(raw)) fail(`logic.variables[${i}] が正しくありません`);
                if (typeof raw.name !== "string") fail(`logic.variables[${i}].name は文字列である必要があります`);
                const name = raw.name.trim();
                if (name.length === 0) fail(`logic.variables[${i}].name が空です`);
                if (name.length > LOGIC_VAR_NAME_MAX) fail(`変数名は ${LOGIC_VAR_NAME_MAX} 文字までです (${name})`);
                if (seen.has(name)) fail(`変数名が重複しています: ${name}`);
                seen.add(name);
                if (typeof raw.init !== "number" || !Number.isFinite(raw.init)) fail(`logic.variables[${i}].init は数値である必要があります (${name})`);
                variables.push({ name, init: raw.init });
            });
        }
        const varNames: ReadonlySet<string> = new Set(variables.map((v) => v.name));

        const rawRules = value.rules;
        if (!Array.isArray(rawRules)) fail("logic.rules は配列である必要があります");
        if (rawRules.length < LOGIC_RULES_MIN || rawRules.length > LOGIC_RULES_MAX) {
            fail(`logic.rules の数は ${LOGIC_RULES_MIN}〜${LOGIC_RULES_MAX} 個である必要があります (現在 ${rawRules.length} 個)`);
        }
        const rules = rawRules.map((raw, i) => validateRule(raw, varNames, i));

        const logic: RoleLogic = { version: 1, variables, rules };
        if (isRecord(value) && "blockly" in value && value.blockly !== undefined) {
            logic.blockly = value.blockly;
        }
        return { ok: true, logic };
    } catch (e) {
        if (e instanceof EkrLogicError) return { ok: false, error: e.message };
        throw e;
    }
}

/**
 * `passives` オブジェクト (Wave 1・spec §1.1) を検証する。固定キーなので順序・重複・参照整合性の
 * 問題が構造的に存在しない — 「既知キーだけを読み、型不一致/範囲外なら文書全体 reject、未知キーは
 * 黙って無視」だけを行う。キーの欠落 = バニラ既定 (何も適用しない) であり、既定値を埋めて返すことは
 * しない (埋めると「明示的にバニラ相当を指定した」と区別できなくなり、再エンコード不動点も崩れる)。
 */
export function validatePassives(value: unknown): PassivesValidationResult {
    try {
        if (!isRecord(value)) fail("passives の中身が正しくありません (JSON オブジェクトが必要です)");
        const p: RolePassives = {};

        if (value.speedMult !== undefined) {
            p.speedMult = expectRangeNumber(value.speedMult, PASSIVE_SPEED_MULT_MIN, PASSIVE_SPEED_MULT_MAX, "passives.speedMult");
        }
        if (value.killDistance !== undefined) {
            p.killDistance = expectEnum(value.killDistance, PASSIVE_KILL_DISTANCE_VALUES, "passives.killDistance");
        }
        if (value.shield !== undefined) {
            // shield が存在する = 「まもりを付ける」宣言なので、count 欠落は黙って無視せず reject
            // (欠落を「まもり無し」に落とすと、作者が意図した防御が無音で消える)。
            if (!isRecord(value.shield)) fail("passives.shield は { count: 回数 } の形である必要があります");
            const count = expectRangeInt(value.shield.count, PASSIVE_SHIELD_COUNT_MIN, PASSIVE_SHIELD_COUNT_MAX, "passives.shield.count");
            p.shield = { count };
        }
        if (value.corpse !== undefined) {
            p.corpse = expectEnum(value.corpse, PASSIVE_CORPSE_VALUES, "passives.corpse");
        }
        if (value.voteWeight !== undefined) {
            p.voteWeight = expectRangeInt(value.voteWeight, PASSIVE_VOTE_WEIGHT_MIN, PASSIVE_VOTE_WEIGHT_MAX, "passives.voteWeight");
        }
        if (value.doom !== undefined) {
            if (!isRecord(value.doom)) fail("passives.doom は { seconds: 秒数 } の形である必要があります");
            const seconds = expectRangeInt(value.doom.seconds, PASSIVE_DOOM_SECONDS_MIN, PASSIVE_DOOM_SECONDS_MAX, "passives.doom.seconds");
            p.doom = { seconds };
        }

        // R2 (契約 §4): 他の人からの見え方だけを偽る陣営。shield/doom と同じネスト形。
        if (value.disguise !== undefined) {
            if (!isRecord(value.disguise)) fail("passives.disguise は { team: 陣営 } の形である必要があります");
            const team = expectEnum(value.disguise.team, SUPPORTED_TEAMS, "passives.disguise.team");
            p.disguise = { team: team as EkrTeam };
        }

        return { ok: true, passives: p };
    } catch (e) {
        if (e instanceof EkrLogicError) return { ok: false, error: e.message };
        throw e;
    }
}
