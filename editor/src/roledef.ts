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

// R0 が対応する唯一の team。他の値は読込時にエラー (EkrDefinition.cs Validate と同じ — R0 は
// Crewmate 非キル系優先の決定に基づき team!=crewmate をハード拒否する)。
export const SUPPORTED_TEAM = "crewmate";
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
] as const;
export type LogicWhen = (typeof LOGIC_WHEN_VALUES)[number];
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
    | { op: "notify"; text: string; seconds: number }
    | { op: "teleport"; to: "random" | "ctx" }
    | { op: "kill"; target: "self" | "ctx" }
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
    | { op: "corpse_spawn"; color: "self" | "random"; at: "self" | "ctx" };

export interface LogicRule {
    when: LogicWhen;
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

    // team: キー省略/null は既定 "crewmate" に収束するが、明示的な空文字/他の値はエラー
    // (C# の `(Team ?? "crewmate").Trim().ToLowerInvariant()` → crewmate 比較と同じ非対称性)。
    const teamField = readStringField(value, "team", SUPPORTED_TEAM);
    if (!teamField.ok) return teamField;
    const team = teamField.v.trim().toLowerCase();
    if (team !== SUPPORTED_TEAM) {
        return { ok: false, error: `この End K not のバージョンでは team="${team}" の役職コードにはまだ対応していません (現在は team="${SUPPORTED_TEAM}" のみ対応)` };
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

function validateNodeArray(raw: unknown, varNames: ReadonlySet<string>, path: string): NodeArrayValidation {
    if (!Array.isArray(raw)) fail(`${path} は配列である必要があります`);
    let depth = 0;
    let count = 0;
    const nodes: LogicNode[] = [];
    raw.forEach((item, i) => {
        const r = validateNode(item, varNames, `${path}[${i}]`);
        nodes.push(r.node);
        depth = Math.max(depth, r.depth);
        count += r.count;
    });
    return { nodes, depth, count };
}

function validateNode(raw: unknown, varNames: ReadonlySet<string>, path: string): NodeValidation {
    if (!isRecord(raw)) fail(`${path} のノードが正しくありません`);
    const op = raw.op;
    switch (op) {
        case "if": {
            const cond = validateExpr(raw.cond, varNames, `${path}.cond`);
            if (raw.then === undefined || raw.then === null) fail(`${path}.then が必要です`);
            const thenList = validateNodeArray(raw.then, varNames, `${path}.then`);
            const hasElse = raw.else !== undefined && raw.else !== null;
            const elseList = hasElse ? validateNodeArray(raw.else, varNames, `${path}.else`) : { nodes: [] as LogicNode[], depth: 0, count: 0 };
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
            return { node: { op: "notify", text, seconds }, depth: 1, count: 1 };
        }
        case "teleport": {
            const to = expectEnum(raw.to, ["random", "ctx"] as const, `${path}.to`);
            return { node: { op: "teleport", to }, depth: 1, count: 1 };
        }
        case "kill": {
            const target = expectEnum(raw.target, ["self", "ctx"] as const, `${path}.target`);
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
    const doPath = `rules[${index}].do`;
    if (raw.do === undefined || raw.do === null) fail(`${doPath} が必要です`);
    const doResult = validateNodeArray(raw.do, varNames, doPath);
    if (doResult.count < LOGIC_RULE_NODES_MIN || doResult.count > LOGIC_RULE_NODES_MAX) {
        fail(`${doPath} のノード数は ${LOGIC_RULE_NODES_MIN}〜${LOGIC_RULE_NODES_MAX} 個である必要があります (現在 ${doResult.count} 個)`);
    }
    if (doResult.depth > LOGIC_AST_DEPTH_MAX) {
        fail(`${doPath} の入れ子が深すぎます (上限 ${LOGIC_AST_DEPTH_MAX})`);
    }
    return { when: when as LogicWhen, do: doResult.nodes };
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
        if (rawVariables !== undefined && rawVariables !== null) {
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
