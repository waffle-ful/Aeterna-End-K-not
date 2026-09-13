// 役職メーカー (EKN R0 フォーム + R1 ブロックロジック) の UI モジュール。
// 契約の正典: spec。R0 のフォーム仕様は計画書 §4。
// 検証は ../roledef.ts、コード入出力は ../rolecode.ts、ブロック→AST 変換は ./compile-role.ts、
// 警告ヒントは ./lint-role.ts。Blockly 本体・ブロック定義 (./blocks-role.ts) は DOM/描画コストが
// 大きいため「ロジック」タブを初めて開いたときに dynamic import する (spec §7)。
//
// main.ts との接点は `initRoleMaker()` の起動呼び出し1回だけ (マップ doc とは無関係な独立機能なので
// 「doc 読み書き + dirty 通知」の接点すら持たない — main.ts を太らせない方針の最も狭い形)。
// 開閉トリガー (ヘッダーの #btn-role-maker と、スタート画面の独立項目 #start-role-maker) の配線も
// このモジュール内で自己完結させる。スタート画面は閉じない — showModal() の top layer が上に乗るので
// 隠す必要が無く、役職メーカーを閉じたらスタート画面へ戻る流れが自然なため。
//
// 通知は #toast / dlg-msg を再利用しない: どちらも dlg-role-maker の ::backdrop の下に描画されて
// 見えなくなる (ネイティブ <dialog> の backdrop は同時に開いている他要素より手前に乗る) ため、
// ダイアログ内のインライン状態表示 (#rm-status) で完結させる。
//
// ロジックの「現在値」の持ち方 (2026-08 R1 追加・重要): ワークスペース (Blockly) はタブを開くまで
// 存在しない。そのため「読み込んだが一度もロジックタブを開いていない」状態を無視すると、
// 名前だけ変えて再コピーした瞬間にロジックが消える事故になる。これを避けるため、
// buildDefinitionFromForm() は次の優先順で logic を組み立てる:
//   1) workspace が存在する → 常にそこから再コンパイルする (タブを開いた時点でワークスペースが正)
//   2) pendingBlocklyRestore (読み込み済み・タブ未初期化) があればそこから再コンパイルする
//   3) 上記どちらも無いが noBlocklyPassthrough (blockly フィールドを持たないロジック付きコードを
//      読み込んだ場合の退避) があればそのまま使う
//   4) いずれも無ければ logic キー自体を出力しない (= 従来どおり R0 互換コード)

import {
    DEFAULT_WIN_CONDITION,
    EKR_BASIS_DEFAULT,
    EKR_BASIS_VALUES,
    type EkrBasis,
    type EkrDefinition,
    type EkrTeam,
    HOST_OPTION_FIXED_KEYS,
    HOST_OPTION_LABEL_MAX,
    HOST_OPTION_MINMAX_MAX,
    HOST_OPTION_MINMAX_MIN,
    HOST_OPTION_VAR_KEY_PREFIX,
    HOST_OPTIONS_MAX,
    type HostOption,
    type HostOptionFixedKey,
    LOGIC_VARIABLES_MAX,
    LOGIC_VAR_NAME_MAX,
    type LogicVariable,
    type LogicWhen,
    PASSIVE_CORPSE_VALUES,
    PASSIVE_COUNTS_AS_MAX,
    PASSIVE_COUNTS_AS_MIN,
    PASSIVE_DOOM_SECONDS_MAX,
    PASSIVE_DOOM_SECONDS_MIN,
    PASSIVE_KILL_DISTANCE_VALUES,
    PASSIVE_SHIELD_COUNT_MAX,
    PASSIVE_SHIELD_COUNT_MIN,
    PASSIVE_SPEED_MULT_MAX,
    PASSIVE_SPEED_MULT_MIN,
    PASSIVE_VOTE_WEIGHT_MAX,
    PASSIVE_VOTE_WEIGHT_MIN,
    type RoleLogic,
    type RolePassives,
    SUPPORTED_TEAM,
    SUPPORTED_TEAMS,
    defaultEkrDefinition,
    normalizeAbilityCooldown,
    normalizeColor,
    normalizeKillCooldown,
    normalizeVisionMultiplier,
    validateEkrDefinition,
    validateRoleLogic,
} from "../roledef";
import { decodeRoleCode, encodeRoleCode } from "../rolecode";
// Wave 12 (契約 §2): 「見せる役職名」の選択肢は生成物からの import (ADDON_META と同型)。
import { ROLE_BY_ID, ROLE_META } from "../generated/ekr-roles";
import { compileWorkspaceToLogicInput, findEmptyWhenBlocks, type SerializedWorkspace } from "./compile-role";
import { formatLintWarning, lintRoleLogic } from "./lint-role";

// Blockly 本体には型としてもここでは触れない (import type は erase されるので実行時コストゼロ —
// 値としての `import * as Blockly from "blockly/core"` は絶対にしない。vitest は無関係だが、
// この方針を崩すと将来 dynamic import 化が壊れやすくなる)。
type BlocksRoleModule = typeof import("./blocks-role");
type BlocklyWorkspaceSvg = InstanceType<typeof import("blockly/core").WorkspaceSvg>;

const STORAGE_KEY = "ekm.roleMaker";

/** localStorage に保存する「編集中フォームの生の値」。R0 で固定のフィールド (ekr/requires/winCondition) は含めない。
 *  team は R2 (契約 §1) でフォーム項目になったためここに含める。 */
interface FormState {
    name: string;
    author: string;
    // plan §7 Tier 1 #2: 説明文 (短文/詳細)。空欄は書き出し時にキーごと落ちる。
    description: string;
    descriptionLong: string;
    color: string;
    team: EkrTeam;
    canKill: boolean;
    killCooldown: number;
    canVent: boolean;
    visionMultiplier: number;
    // Wave 9 (契約 §1): はつどうのしかた + とくいわざの まちじかん。
    basis: EkrBasis;
    abilityCooldown: number;
    // Wave 9 追記: サボタージュを つかえる。undefined = 「陣営どおり」(キー省略)。
    canSabotage: boolean | undefined;
    // Wave 1: とくせい (spec §1.1)。「バニラ既定 = キーの欠落」なので、既定のままの項目は
    // ここにも入らない (下書きの形と書き出しの形を一致させておく)。
    passives: RolePassives;
    // Wave 3 (§3): なまえのよこに出す文字。空欄 = progress キー省略。
    progressText: string;
    // Wave 3 (契約 §4): ホストがへんこうできる数値。0件 = hostOptions キー省略。
    hostOptions: HostOption[];
    // R1: ロジックの下書き。ワークスペースを一度も開いていなければ logicBlockly は null。
    logicVariables: LogicVariable[];
    logicBlockly: unknown | null;
    logicNoBlocklyPassthrough: RoleLogic | null;
}

/**
 * team の下書き/読込値を寛容に復元する唯一の実装 (localStorage 下書き復元・writeForm の防御的
 * フォールバックの両方から呼ぶ — sanitizePassivesDraft と同じ方針)。不正な値は既定 (crewmate) へ。
 * roledef.ts 側の validateEkrDefinition は不正な team を reject するだけでフォールバックしない
 * (契約は変更しない) — フォールバックはこのフォーム層だけの責務。
 */
export function normalizeTeamDraft(raw: unknown): EkrTeam {
    return typeof raw === "string" && (SUPPORTED_TEAMS as readonly string[]).includes(raw) ? (raw as EkrTeam) : SUPPORTED_TEAM;
}

/**
 * basis の下書き/読込値を寛容に復元する (Wave 9・normalizeTeamDraft と同じ方針)。不正な値は
 * 既定 ("pet") へ。roledef.ts 側の validateEkrDefinition は不正な basis を reject するだけで
 * フォールバックしない — フォールバックはこのフォーム層だけの責務。
 */
export function normalizeBasisDraft(raw: unknown): EkrBasis {
    return typeof raw === "string" && (EKR_BASIS_VALUES as readonly string[]).includes(raw) ? (raw as EkrBasis) : EKR_BASIS_DEFAULT;
}

/**
 * canSabotage の下書き/読込値を寛容に復元する (Wave 9 追記)。壊れた/不明な値は
 * 「陣営どおり」(undefined) へ — canSabotage は元々 undefined が正当値なので、
 * normalizeTeamDraft/normalizeBasisDraft と違い「壊れていたら既定へフォールバック」ではなく
 * 「boolean でなければ undefined」という単純な寛容化になる。
 */
export function normalizeCanSabotageDraft(raw: unknown): boolean | undefined {
    return typeof raw === "boolean" ? raw : undefined;
}

/** rm-can-sabotage の3値 <select> (""=陣営どおり/"true"/"false") ↔ canSabotage (boolean|undefined) の変換。 */
function canSabotageToSelectValue(v: boolean | undefined): string {
    return v === undefined ? "" : String(v);
}
function selectValueToCanSabotage(raw: string): boolean | undefined {
    return raw === "" ? undefined : raw === "true";
}

/** rm-team の <option> 文言と揃える JP ラベル (プレビューの陣営行に使う)。 */
const TEAM_LABELS: Record<EkrTeam, string> = {
    crewmate: "クルーメイト",
    impostor: "インポスター",
    neutral: "ニュートラル",
};

function defaultFormState(): FormState {
    const d = defaultEkrDefinition();
    return {
        name: d.name,
        author: d.author,
        description: "",
        descriptionLong: "",
        color: d.color,
        team: SUPPORTED_TEAM,
        canKill: d.canKill,
        killCooldown: d.killCooldown,
        canVent: d.canVent,
        visionMultiplier: d.visionMultiplier,
        basis: d.basis,
        abilityCooldown: d.abilityCooldown,
        canSabotage: d.canSabotage,
        passives: {},
        progressText: "",
        hostOptions: [],
        logicVariables: [],
        logicBlockly: null,
        logicNoBlocklyPassthrough: null,
    };
}

function $<T extends HTMLElement>(id: string): T {
    return document.getElementById(id) as T;
}

function setStatus(msg: string, isError: boolean): void {
    const el = $("rm-status");
    el.textContent = msg;
    el.hidden = msg.length === 0;
    el.classList.toggle("rm-status-error", isError);
    el.classList.toggle("rm-status-ok", !isError && msg.length > 0);
}

function refreshKillCdVisibility(): void {
    $("rm-kill-cd-row").hidden = !$<HTMLInputElement>("rm-can-kill").checked;
}

/** Wave 9 (契約 §1.2)・Wave 11 (契約 §2 CD 行): とくいわざの まちじかん欄は
 *  「だれかを えらぶ」「きえるボタンをおす」を選んだときに表示する ("ボタンをおす" のときだけ
 *  非表示 — その場合はホストが「ファントム化」を ON にしたときだけ効くので L32 でヒントする)。 */
function refreshAbilityCdVisibility(): void {
    $("rm-ability-cd-row").hidden = $<HTMLSelectElement>("rm-basis").value === "pet";
}

/** フォーム入力欄 → 生の値 (未検証・未クランプ)。name/author はユーザーの入力途中の値をそのまま読む。
 *  team は <select> なので不正値は原理上入らないが、念のため normalizeTeamDraft を通す (writeForm と対称)。 */
function readForm(): Omit<FormState, "passives" | "hostOptions" | "logicVariables" | "logicBlockly" | "logicNoBlocklyPassthrough"> {
    return {
        name: $<HTMLInputElement>("rm-name").value,
        author: $<HTMLInputElement>("rm-author").value,
        description: $<HTMLInputElement>("rm-desc").value,
        descriptionLong: $<HTMLTextAreaElement>("rm-desc-long").value,
        color: $<HTMLInputElement>("rm-color").value,
        team: normalizeTeamDraft($<HTMLSelectElement>("rm-team").value),
        canKill: $<HTMLInputElement>("rm-can-kill").checked,
        killCooldown: Number($<HTMLInputElement>("rm-kill-cd").value),
        canVent: $<HTMLInputElement>("rm-can-vent").checked,
        visionMultiplier: Number($<HTMLInputElement>("rm-vision").value),
        // Wave 9 (契約 §1): はつどうのしかた + とくいわざの まちじかん。
        basis: normalizeBasisDraft($<HTMLSelectElement>("rm-basis").value),
        abilityCooldown: Number($<HTMLInputElement>("rm-ability-cd").value),
        // Wave 9 追記: サボタージュを つかえる。""(陣営どおり) は undefined に変換する。
        canSabotage: selectValueToCanSabotage($<HTMLSelectElement>("rm-can-sabotage").value),
        // Wave 3 (契約 §3): なまえのよこに出す文字。description と同じ「空欄=キー省略」の単純テキスト欄。
        progressText: $<HTMLInputElement>("rm-progress-text").value,
    };
}

/**
 * フォーム入力欄 ← 状態を反映。color/killCooldown/visionMultiplier/team は normalize* を必ず経由する
 * (input type="color" に不正な文字列を代入すると黙って #000000 にリセットされる仕様があるため、
 * 呼び出し元の由来 [既定値/localStorage/読込コード] を問わずここで安全な値であることを保証する)。
 * team も同じ理由: <select> に <option> と一致しない値を代入すると選択が外れる (どの項目も
 * selected にならない) ため、normalizeTeamDraft で必ず既知の3値のどれかに丸めてから書き込む。
 */
function writeForm(s: Omit<FormState, "passives" | "hostOptions" | "logicVariables" | "logicBlockly" | "logicNoBlocklyPassthrough">): void {
    $<HTMLInputElement>("rm-name").value = s.name;
    $<HTMLInputElement>("rm-author").value = s.author;
    $<HTMLInputElement>("rm-desc").value = s.description;
    $<HTMLTextAreaElement>("rm-desc-long").value = s.descriptionLong;
    $<HTMLInputElement>("rm-color").value = normalizeColor(s.color);
    $<HTMLSelectElement>("rm-team").value = normalizeTeamDraft(s.team);
    $<HTMLInputElement>("rm-can-kill").checked = s.canKill;
    $<HTMLInputElement>("rm-kill-cd").value = String(normalizeKillCooldown(s.killCooldown));
    $<HTMLInputElement>("rm-can-vent").checked = s.canVent;
    $<HTMLInputElement>("rm-vision").value = String(normalizeVisionMultiplier(s.visionMultiplier));
    $<HTMLSelectElement>("rm-basis").value = normalizeBasisDraft(s.basis);
    $<HTMLInputElement>("rm-ability-cd").value = String(normalizeAbilityCooldown(s.abilityCooldown));
    $<HTMLSelectElement>("rm-can-sabotage").value = canSabotageToSelectValue(normalizeCanSabotageDraft(s.canSabotage));
    $<HTMLInputElement>("rm-progress-text").value = s.progressText;
    refreshKillCdVisibility();
    refreshAbilityCdVisibility();
}

// ---------------------------------------------------------------------------
// とくせい (Wave 1・spec §1.1) — 基本情報タブのフォームセクション
// ---------------------------------------------------------------------------
// 契約側は「範囲外なら文書全体 reject」(クランプしない) なので、フォーム側は常に範囲内の値だけを
// 返す責務を持つ (壊れた下書きを復元しても必ずコピーできる状態にする)。数値欄の change ハンドラも
// 既存の rm-kill-cd と同じ作法でクランプ結果を書き戻す。
// 「バニラ既定 (= 何も変えない)」はキーの欠落で表す — 既定のままの項目は読み取り時点で落とす。

/** フォーム表示用の既定値 (チェックを外しているときに数値欄へ入れておく値)。契約の既定値ではない */
const PASSIVE_FORM_DEFAULTS = {
    speedMult: 1,
    shieldCount: 1,
    voteWeight: 1,
    doomSeconds: 60,
} as const;

function clampToRange(raw: unknown, min: number, max: number, fallback: number): number {
    const n = typeof raw === "number" ? raw : Number(raw);
    if (!Number.isFinite(n)) return fallback;
    return Math.min(max, Math.max(min, n));
}

function clampIntToRange(raw: unknown, min: number, max: number, fallback: number): number {
    return Math.round(clampToRange(raw, min, max, fallback));
}

function refreshPassiveRowVisibility(): void {
    $("rm-p-shield-row").hidden = !$<HTMLInputElement>("rm-p-shield").checked;
    $("rm-p-doom-row").hidden = !$<HTMLInputElement>("rm-p-doom").checked;
    // Wave 12 (契約 §2): 「べつの陣営に見せる」を選んでいないときは役職名/deep 欄ごと隠す
    // (見せる陣営が無いのに役職名だけ選べても意味が無いため)。
    const disguised = $<HTMLSelectElement>("rm-p-disguise").value !== "";
    $("rm-p-disguise-role-row").hidden = !disguised;
    $("rm-p-disguise-deep-row").hidden = !disguised;
}

/**
 * 「見せる役職名」<select> の選択肢を、選ばれている disguise team に属する役職だけへ作り直す
 * (契約 §2.1: 値集合は「team と役職の陣営が一致」する組み合わせのみ)。呼ぶたびに先頭の
 * 「とくに指定しない」以外を作り直すため、team を切り替えるたびに呼び直すこと (呼び忘れると
 * 前の陣営の役職が選べたまま残る)。keepValue が新しい選択肢集合に無い場合は空欄へ落ちる
 * (壊れた下書き/team 切り替え直後の安全弁)。
 */
function populateDisguiseRoleOptions(team: string, keepValue: string): void {
    const select = $<HTMLSelectElement>("rm-p-disguise-role");
    select.replaceChildren();
    const blank = document.createElement("option");
    blank.value = "";
    blank.textContent = "とくに指定しない (陣営の名前だけ)";
    select.appendChild(blank);
    for (const role of ROLE_META) {
        if (role.team !== team) continue;
        const opt = document.createElement("option");
        opt.value = role.id;
        opt.textContent = role.ja;
        select.appendChild(opt);
    }
    select.value = ROLE_BY_ID.get(keepValue)?.team === team ? keepValue : "";
}

/** とくせいフォーム → RolePassives (既定のままの項目はキーごと落とす・値は必ず範囲内) */
function readPassivesForm(): RolePassives {
    const p: RolePassives = {};

    const speed = clampToRange($<HTMLInputElement>("rm-p-speed").value, PASSIVE_SPEED_MULT_MIN, PASSIVE_SPEED_MULT_MAX, PASSIVE_FORM_DEFAULTS.speedMult);
    if (Math.abs(speed - PASSIVE_FORM_DEFAULTS.speedMult) > 1e-9) p.speedMult = speed;

    const killDistance = $<HTMLSelectElement>("rm-p-killdist").value;
    if ((PASSIVE_KILL_DISTANCE_VALUES as readonly string[]).includes(killDistance)) {
        p.killDistance = killDistance as RolePassives["killDistance"];
    }

    if ($<HTMLInputElement>("rm-p-shield").checked) {
        p.shield = {
            count: clampIntToRange($<HTMLInputElement>("rm-p-shield-count").value, PASSIVE_SHIELD_COUNT_MIN, PASSIVE_SHIELD_COUNT_MAX, PASSIVE_FORM_DEFAULTS.shieldCount),
        };
    }

    const corpse = $<HTMLSelectElement>("rm-p-corpse").value;
    if ((PASSIVE_CORPSE_VALUES as readonly string[]).includes(corpse) && corpse !== "normal") {
        p.corpse = corpse as RolePassives["corpse"];
    }

    // Wave 12 (契約 §4): ころした死体は だれのか わからない (キー欠落 = 既定 false・checkbox 未チェック)。
    if ($<HTMLInputElement>("rm-p-anonymous-kills").checked) p.anonymousKills = true;

    const vote = clampIntToRange($<HTMLInputElement>("rm-p-vote").value, PASSIVE_VOTE_WEIGHT_MIN, PASSIVE_VOTE_WEIGHT_MAX, PASSIVE_FORM_DEFAULTS.voteWeight);
    if (vote !== PASSIVE_FORM_DEFAULTS.voteWeight) p.voteWeight = vote;

    // Wave 12 (契約 §5): じぶんの票は 見えない。
    if ($<HTMLInputElement>("rm-p-anonymous-vote").checked) p.anonymousVote = true;

    if ($<HTMLInputElement>("rm-p-doom").checked) {
        p.doom = {
            seconds: clampIntToRange($<HTMLInputElement>("rm-p-doom-seconds").value, PASSIVE_DOOM_SECONDS_MIN, PASSIVE_DOOM_SECONDS_MAX, PASSIVE_FORM_DEFAULTS.doomSeconds),
        };
    }

    const disguise = $<HTMLSelectElement>("rm-p-disguise").value;
    if ((SUPPORTED_TEAMS as readonly string[]).includes(disguise)) {
        const d: NonNullable<RolePassives["disguise"]> = { team: disguise as EkrTeam };
        // Wave 12 (契約 §2): 見せる役職名。role select の選択肢は既に team でフィルタ済みだが、
        // 念のためここでも team 一致を確認する (populateDisguiseRoleOptions を経由しない直接 DOM 操作
        // からの取りこぼしに対する二重の安全弁)。
        const roleVal = $<HTMLSelectElement>("rm-p-disguise-role").value;
        if (roleVal !== "" && ROLE_BY_ID.get(roleVal)?.team === disguise) {
            d.role = roleVal;
        }
        if ($<HTMLInputElement>("rm-p-disguise-deep").checked) d.deep = true;
        p.disguise = d;
    }

    return p;
}

/**
 * Wave 9 (契約 §3): passives.countsAs はフォーム UI を持たない (契約: ホスト露出なし・見本で
 * 足りる)。UI が表現できない値をコピー/保存のたびに黙って落とさないための保持だけの変数
 * (noBlocklyPassthrough と同じ発想 — フォームでは編集できないが、読み込んだ値は保つ)。
 */
let passivesPassthrough: Pick<RolePassives, "countsAs"> = {};

/** フォームの passives (readPassivesForm) + UI を持たない countsAs (passivesPassthrough) を合成する。
 *  buildDefinitionFromForm/saveFormToStorage/リンタへの受け渡しは必ずこちらを経由すること
 *  (readPassivesForm を直接使うと countsAs が消える)。 */
function currentPassives(): RolePassives {
    return { ...passivesPassthrough, ...readPassivesForm() };
}

/** RolePassives → とくせいフォーム (キーが無い項目は表示用の既定値を入れておく) */
function writePassivesForm(p: RolePassives): void {
    $<HTMLInputElement>("rm-p-speed").value = String(clampToRange(p.speedMult, PASSIVE_SPEED_MULT_MIN, PASSIVE_SPEED_MULT_MAX, PASSIVE_FORM_DEFAULTS.speedMult));
    $<HTMLSelectElement>("rm-p-killdist").value = (PASSIVE_KILL_DISTANCE_VALUES as readonly string[]).includes(p.killDistance ?? "") ? p.killDistance! : "";
    $<HTMLInputElement>("rm-p-shield").checked = p.shield !== undefined;
    $<HTMLInputElement>("rm-p-shield-count").value = String(clampIntToRange(p.shield?.count, PASSIVE_SHIELD_COUNT_MIN, PASSIVE_SHIELD_COUNT_MAX, PASSIVE_FORM_DEFAULTS.shieldCount));
    $<HTMLSelectElement>("rm-p-corpse").value = (PASSIVE_CORPSE_VALUES as readonly string[]).includes(p.corpse ?? "") ? p.corpse! : "normal";
    $<HTMLInputElement>("rm-p-anonymous-kills").checked = p.anonymousKills === true;
    $<HTMLInputElement>("rm-p-vote").value = String(clampIntToRange(p.voteWeight, PASSIVE_VOTE_WEIGHT_MIN, PASSIVE_VOTE_WEIGHT_MAX, PASSIVE_FORM_DEFAULTS.voteWeight));
    $<HTMLInputElement>("rm-p-anonymous-vote").checked = p.anonymousVote === true;
    $<HTMLInputElement>("rm-p-doom").checked = p.doom !== undefined;
    $<HTMLInputElement>("rm-p-doom-seconds").value = String(clampIntToRange(p.doom?.seconds, PASSIVE_DOOM_SECONDS_MIN, PASSIVE_DOOM_SECONDS_MAX, PASSIVE_FORM_DEFAULTS.doomSeconds));
    const disguiseTeam = (SUPPORTED_TEAMS as readonly string[]).includes(p.disguise?.team ?? "") ? p.disguise!.team : "";
    $<HTMLSelectElement>("rm-p-disguise").value = disguiseTeam;
    // Wave 12 (契約 §2): role select は team に応じて選択肢を作り直してから値を入れる
    // (populateDisguiseRoleOptions が team 不一致なら空欄へ落とすので、ここで別途チェック不要)。
    populateDisguiseRoleOptions(disguiseTeam, p.disguise?.role ?? "");
    $<HTMLInputElement>("rm-p-disguise-deep").checked = p.disguise?.deep === true;
    refreshPassiveRowVisibility();
}

/** 下書き (localStorage) の passives を寛容に復元する — 壊れていても既定 (とくせい無し) に落とす */
function sanitizePassivesDraft(raw: unknown): RolePassives {
    if (typeof raw !== "object" || raw === null) return {};
    const r = raw as Record<string, unknown>;
    const p: RolePassives = {};
    if (typeof r.speedMult === "number") p.speedMult = clampToRange(r.speedMult, PASSIVE_SPEED_MULT_MIN, PASSIVE_SPEED_MULT_MAX, PASSIVE_FORM_DEFAULTS.speedMult);
    if (typeof r.killDistance === "string" && (PASSIVE_KILL_DISTANCE_VALUES as readonly string[]).includes(r.killDistance)) {
        p.killDistance = r.killDistance as RolePassives["killDistance"];
    }
    if (typeof r.shield === "object" && r.shield !== null) {
        p.shield = { count: clampIntToRange((r.shield as Record<string, unknown>).count, PASSIVE_SHIELD_COUNT_MIN, PASSIVE_SHIELD_COUNT_MAX, PASSIVE_FORM_DEFAULTS.shieldCount) };
    }
    if (typeof r.corpse === "string" && (PASSIVE_CORPSE_VALUES as readonly string[]).includes(r.corpse) && r.corpse !== "normal") {
        p.corpse = r.corpse as RolePassives["corpse"];
    }
    if (typeof r.voteWeight === "number") p.voteWeight = clampIntToRange(r.voteWeight, PASSIVE_VOTE_WEIGHT_MIN, PASSIVE_VOTE_WEIGHT_MAX, PASSIVE_FORM_DEFAULTS.voteWeight);
    if (typeof r.doom === "object" && r.doom !== null) {
        p.doom = { seconds: clampIntToRange((r.doom as Record<string, unknown>).seconds, PASSIVE_DOOM_SECONDS_MIN, PASSIVE_DOOM_SECONDS_MAX, PASSIVE_FORM_DEFAULTS.doomSeconds) };
    }
    if (typeof r.disguise === "object" && r.disguise !== null) {
        const dr = r.disguise as Record<string, unknown>;
        const team = dr.team;
        if (typeof team === "string" && (SUPPORTED_TEAMS as readonly string[]).includes(team)) {
            const d: NonNullable<RolePassives["disguise"]> = { team: team as EkrTeam };
            // Wave 12 (契約 §2): role/deep も寛容に復元する (team 不一致・型不一致は黙って落とす —
            // 他の下書きフィールドと同じ「壊れていても既定へフォールバック」方針)。
            if (typeof dr.role === "string" && ROLE_BY_ID.get(dr.role)?.team === team) {
                d.role = dr.role;
            }
            if (typeof dr.deep === "boolean") d.deep = dr.deep;
            p.disguise = d;
        }
    }
    // Wave 9 (契約 §3): countsAs は UI を持たないため writePassivesForm では書き戻さないが、
    // 下書き自体には保持する (wire() が passivesPassthrough へ引き継ぐ)。
    if (typeof r.countsAs === "number" && Number.isInteger(r.countsAs) && r.countsAs >= PASSIVE_COUNTS_AS_MIN && r.countsAs <= PASSIVE_COUNTS_AS_MAX) {
        p.countsAs = r.countsAs;
    }
    // Wave 12 (契約 §4/§5): anonymousKills/anonymousVote も真偽値のときだけ復元する。
    if (typeof r.anonymousKills === "boolean") p.anonymousKills = r.anonymousKills;
    if (typeof r.anonymousVote === "boolean") p.anonymousVote = r.anonymousVote;
    return p;
}

/** プレビューの能力チップに出す「とくせい」の説明文 (ON のものだけ) */
function passiveChipTexts(p: RolePassives): string[] {
    const chips: string[] = [];
    if (p.speedMult !== undefined) chips.push(`🏃 いつものはやさ ${formatPreviewNumber(p.speedMult)} 倍`);
    if (p.killDistance !== undefined) {
        const label = p.killDistance === "short" ? "みじかい" : p.killDistance === "medium" ? "ふつう" : "ながい";
        chips.push(`🎯 キルできるきょり: ${label}`);
    }
    if (p.shield !== undefined) chips.push(`🛡 さいしょの ${p.shield.count} 回のこうげきをふせぐ`);
    if (p.corpse !== undefined) {
        const corpseLabel = p.corpse === "noReport" ? "じぶんの死体は通報できない"
            : p.corpse === "vanish" ? "じぶんの死体はすぐ消える"
            : "じぶんの死体は だれのか わからない"; // "anonymous" (Wave 12)
        chips.push(`🩸 ${corpseLabel}`);
    }
    // Wave 12 (契約 §4): ころした死体は だれのか わからない (自分の死体とは別枠のチップ)。
    if (p.anonymousKills) chips.push("🩸 ころした死体は だれのか わからない");
    if (p.voteWeight !== undefined) chips.push(p.voteWeight === 0 ? "🗳 票をもっていない" : `🗳 票のちから ${p.voteWeight}`);
    // Wave 12 (契約 §5): じぶんの票は 見えない。
    if (p.anonymousVote) chips.push("🗳 じぶんの票は 見えない");
    if (p.doom !== undefined) chips.push(`⏳ ${p.doom.seconds} 秒たつと死んでしまう`);
    if (p.disguise !== undefined) {
        // Wave 12 (契約 §2): role が指定されていれば役職名も見せる・deep なら情報役職にも効く旨を添える。
        const roleName = p.disguise.role !== undefined ? ROLE_BY_ID.get(p.disguise.role)?.ja : undefined;
        let chip = roleName !== undefined
            ? `🎭 ${roleName} (${TEAM_LABELS[p.disguise.team]}) に見える (見た目だけ)`
            : `🎭 ${TEAM_LABELS[p.disguise.team]} に見える (見た目だけ)`;
        if (p.disguise.deep) chip += "。しらべられても ばれない";
        chips.push(chip);
    }
    return chips;
}

// ---------------------------------------------------------------------------
// ホストがへんこうできる数値 (Wave 3・契約 §4) — 基本情報タブのフォームセクション
// ---------------------------------------------------------------------------
// hostOptions は passives と違って固定キーの単一オブジェクトではなく可変長のリストなので、
// logicVariables (下の「ロジック (R1) の状態」節) と同じ「モジュール直下の配列 + 都度フル再構築の
// render 関数」方式で持つ (renderVariablesList() と同型)。key ドロップダウンの選択肢
// (固定6種 + 宣言済み変数) は logicVariables の増減に追随する必要があるため、変数リストが変わる
// たびに renderHostOptionsList() も呼び直す (renderVariablesList() の末尾で呼ぶ)。

/** 固定キーの日本語ラベル (key ドロップダウン用)。契約 §4.1 表の6キー。 */
const HOST_OPTION_FIXED_LABELS: Record<HostOptionFixedKey, string> = {
    "shield.count": "まもりの回数",
    "doom.seconds": "よわさの秒数",
    speedMult: "はやさの倍率",
    voteWeight: "票のちから",
    killCooldown: "キルクールダウン",
    vision: "視界の広さ",
    // Wave 9 (契約 §1.2): とくいわざの まちじかん。
    abilityCooldown: "とくいわざの まちじかん",
};

let hostOptionsDraft: HostOption[] = [];

/** key ドロップダウンの選択肢 (固定6種 + 宣言済み変数)。呼ぶたびに現在の変数リストから作り直す。 */
function hostOptionKeyChoices(): [string, string][] {
    const fixed: [string, string][] = HOST_OPTION_FIXED_KEYS.map((k) => [HOST_OPTION_FIXED_LABELS[k], k]);
    const vars: [string, string][] = logicVariables.map((v) => [`へんすう: ${v.name}`, `${HOST_OPTION_VAR_KEY_PREFIX}${v.name}`]);
    return [...fixed, ...vars];
}

function isVarHostOptionKey(key: string): boolean {
    return key.startsWith(HOST_OPTION_VAR_KEY_PREFIX);
}

function renderHostOptionsList(): void {
    const choices = hostOptionKeyChoices();
    const validKeys = new Set(choices.map(([, v]) => v));

    $("rm-hostopts-summary").textContent = `(${hostOptionsDraft.length}/${HOST_OPTIONS_MAX})`;

    const list = $("rm-hostopts-list");
    list.replaceChildren();
    hostOptionsDraft.forEach((opt, i) => {
        const row = document.createElement("div");
        row.className = "rm-var-row";

        const keySelect = document.createElement("select");
        keySelect.className = "rm-var-name";
        keySelect.setAttribute("aria-label", "露出する項目");
        for (const [label, value] of choices) {
            const optionEl = document.createElement("option");
            optionEl.value = value;
            optionEl.textContent = label;
            keySelect.appendChild(optionEl);
        }
        // 変数が消えて選択肢から外れた key はそのまま残す (壊れたまま表示 — 契約 §4.1 の
        // 「var: は宣言済み変数のみ」は copyCode 時に validateEkrDefinition が reject で伝える)。
        if (validKeys.has(opt.key)) {
            keySelect.value = opt.key;
        } else {
            const orphan = document.createElement("option");
            orphan.value = opt.key;
            orphan.textContent = `(見つからない: ${opt.key})`;
            keySelect.insertBefore(orphan, keySelect.firstChild);
            keySelect.value = opt.key;
        }
        keySelect.addEventListener("change", () => {
            hostOptionsDraft[i] = { ...hostOptionsDraft[i], key: keySelect.value };
            if (isVarHostOptionKey(keySelect.value)) {
                // var: 系は min/max が必須 (契約 §4.1) — 見た目の初期表示 (opt.min ?? 既定) だけでなく
                // 実データにも既定値を焼き込む (触らずにコピーしても reject にならないように)。
                hostOptionsDraft[i].min ??= HOST_OPTION_MINMAX_MIN;
                hostOptionsDraft[i].max ??= HOST_OPTION_MINMAX_MAX;
            } else {
                delete hostOptionsDraft[i].min;
                delete hostOptionsDraft[i].max;
            }
            renderHostOptionsList();
            saveFormToStorage();
            renderPreview();
        });

        const labelInput = document.createElement("input");
        labelInput.className = "rm-var-name";
        labelInput.maxLength = HOST_OPTION_LABEL_MAX;
        labelInput.value = opt.label;
        labelInput.placeholder = "オプション画面での表示名";
        labelInput.setAttribute("aria-label", "表示名");
        labelInput.addEventListener("input", () => {
            hostOptionsDraft[i] = { ...hostOptionsDraft[i], label: labelInput.value };
            saveFormToStorage();
        });

        row.append(keySelect, labelInput);

        if (isVarHostOptionKey(opt.key)) {
            const minInput = document.createElement("input");
            minInput.type = "number";
            minInput.className = "rm-var-init";
            minInput.title = "さいしょう値";
            minInput.setAttribute("aria-label", "さいしょう値");
            minInput.value = String(opt.min ?? HOST_OPTION_MINMAX_MIN);
            minInput.addEventListener("change", () => {
                const n = clampIntToRange(minInput.value, HOST_OPTION_MINMAX_MIN, HOST_OPTION_MINMAX_MAX, HOST_OPTION_MINMAX_MIN);
                hostOptionsDraft[i] = { ...hostOptionsDraft[i], min: n };
                minInput.value = String(n);
                saveFormToStorage();
            });

            const maxInput = document.createElement("input");
            maxInput.type = "number";
            maxInput.className = "rm-var-init";
            maxInput.title = "さいだい値";
            maxInput.setAttribute("aria-label", "さいだい値");
            maxInput.value = String(opt.max ?? HOST_OPTION_MINMAX_MAX);
            maxInput.addEventListener("change", () => {
                const n = clampIntToRange(maxInput.value, HOST_OPTION_MINMAX_MIN, HOST_OPTION_MINMAX_MAX, HOST_OPTION_MINMAX_MAX);
                hostOptionsDraft[i] = { ...hostOptionsDraft[i], max: n };
                maxInput.value = String(n);
                saveFormToStorage();
            });

            row.append(minInput, maxInput);
        }

        const removeBtn = document.createElement("button");
        removeBtn.type = "button";
        removeBtn.className = "rm-var-remove";
        removeBtn.textContent = "✕";
        removeBtn.title = "この数値を削除";
        removeBtn.addEventListener("click", () => {
            hostOptionsDraft.splice(i, 1);
            renderHostOptionsList();
            saveFormToStorage();
            renderPreview();
        });
        row.append(removeBtn);

        list.appendChild(row);
    });
    $<HTMLButtonElement>("rm-hostopts-add").disabled = hostOptionsDraft.length >= HOST_OPTIONS_MAX;
}

function addHostOption(): void {
    if (hostOptionsDraft.length >= HOST_OPTIONS_MAX) return;
    const choices = hostOptionKeyChoices();
    const usedKeys = new Set(hostOptionsDraft.map((o) => o.key));
    const firstFree = choices.find(([, key]) => !usedKeys.has(key));
    const key = firstFree ? firstFree[1] : HOST_OPTION_FIXED_KEYS[0];
    // label は空文字にしない (契約 §4.1: label は1文字以上必須 — 空のままだと copyCode() が
    // 常に reject され続けてしまう。addVariable() が「へんすう1」を既定にするのと同じ理由 —
    // 「壊れた下書きを復元しても必ずコピーできる状態にする」をここでも守る)。
    // ドロップダウンの表示文言 (choices の label 側) をそのまま初期値にする — 作者は後から書き換えられる。
    const label = firstFree ? firstFree[0] : HOST_OPTION_FIXED_LABELS[HOST_OPTION_FIXED_KEYS[0]];
    const opt: HostOption = { key, label };
    if (isVarHostOptionKey(key)) {
        opt.min = HOST_OPTION_MINMAX_MIN;
        opt.max = HOST_OPTION_MINMAX_MAX;
    }
    hostOptionsDraft.push(opt);
    renderHostOptionsList();
    saveFormToStorage();
    renderPreview();
}

/** 下書き (localStorage) の hostOptions を寛容に復元する — 型が壊れていても既定 (0件) に落とす。
 *  範囲/重複/未定義変数などの契約検証はここでは行わない (壊れたままでもフォームは必ず開ける —
 *  最終的な reject は copyCode() 時の validateEkrDefinition に委ねる、sanitizePassivesDraft と同じ方針)。 */
function sanitizeHostOptionsDraft(raw: unknown): HostOption[] {
    if (!Array.isArray(raw)) return [];
    const out: HostOption[] = [];
    for (const item of raw.slice(0, HOST_OPTIONS_MAX)) {
        if (typeof item !== "object" || item === null) continue;
        const r = item as Record<string, unknown>;
        // label が空/空白のみの行は丸ごと捨てる (契約 §4.1: label は1文字以上必須。空のまま
        // 復元すると copyCode() が reject され続ける行が残ってしまう — addHostOption() が
        // 空ラベルを作らないのと同じ理由で、下書き復元側でも同じ不変条件を守る)。
        if (typeof r.key !== "string" || typeof r.label !== "string" || r.label.trim().length === 0) continue;
        const opt: HostOption = { key: r.key, label: r.label };
        if (typeof r.min === "number") opt.min = r.min;
        if (typeof r.max === "number") opt.max = r.max;
        out.push(opt);
    }
    return out;
}

// ---------------------------------------------------------------------------
// ロジック (R1) の状態
// ---------------------------------------------------------------------------

let logicVariables: LogicVariable[] = [];
/** ワークスペース初期化前に復元を待っている Blockly serialization (読み込み直後・ドラフト復元直後) */
let pendingBlocklyRestore: unknown | null = null;
/** blockly フィールドを持たないロジック付きコードを読み込んだ場合の退避 (タブを開かない限りそのまま使う) */
let noBlocklyPassthrough: RoleLogic | null = null;

let blocklyApi: BlocksRoleModule | null = null;
let workspace: BlocklyWorkspaceSvg | null = null;
let logicTabLoading = false;
let containerObserver: ResizeObserver | null = null;
let varsObserver: ResizeObserver | null = null;

function currentBlocklyState(): SerializedWorkspace | null {
    if (workspace && blocklyApi) {
        return blocklyApi.Blockly.serialization.workspaces.save(workspace) as SerializedWorkspace;
    }
    return (pendingBlocklyRestore as SerializedWorkspace | null) ?? null;
}

/** 現在のロジック入力 (未検証・compile-role.ts の生出力) を、優先順位に従って組み立てる */
function currentLogicCandidate(): unknown | undefined {
    const state = currentBlocklyState();
    if (state) {
        const compiled = compileWorkspaceToLogicInput(state, logicVariables);
        if (compiled) return compiled;
    }
    if (noBlocklyPassthrough) return noBlocklyPassthrough;
    return undefined;
}

function saveFormToStorage(): void {
    try {
        const state: FormState = {
            ...readForm(),
            passives: currentPassives(),
            hostOptions: hostOptionsDraft,
            logicVariables,
            logicBlockly: currentBlocklyState(),
            logicNoBlocklyPassthrough: noBlocklyPassthrough,
        };
        localStorage.setItem(STORAGE_KEY, JSON.stringify(state));
    } catch {
        // QuotaExceeded 等が起きても機能は継続する (保存できないだけ)
    }
}

/**
 * 保存されたドラフトは「まだ名前を入れていない途中経過」を許す必要があるため、
 * validateEkrDefinition (name 必須などのハード拒否を含む) には通さず、フィールドごとに寛容に復元する。
 * 数値/色だけは normalize* を通し、壊れたデータでもフォーム自体は必ず開ける状態にする。
 * logicVariables/logicBlockly/logicNoBlocklyPassthrough は「壊れていても実害が出ない」形
 * (最終的に必ず validateEkrDefinition を通ってからしか使われない) なので、型だけ緩く確認して素通しする。
 */
function loadFormFromStorage(): FormState {
    const d = defaultFormState();
    try {
        const raw = localStorage.getItem(STORAGE_KEY);
        if (!raw) return d;
        const parsed = JSON.parse(raw) as Partial<Record<keyof FormState, unknown>>;
        return {
            name: typeof parsed.name === "string" ? parsed.name : d.name,
            author: typeof parsed.author === "string" ? parsed.author : d.author,
            description: typeof parsed.description === "string" ? parsed.description : d.description,
            descriptionLong: typeof parsed.descriptionLong === "string" ? parsed.descriptionLong : d.descriptionLong,
            color: normalizeColor(parsed.color),
            team: normalizeTeamDraft(parsed.team),
            canKill: typeof parsed.canKill === "boolean" ? parsed.canKill : d.canKill,
            killCooldown: normalizeKillCooldown(parsed.killCooldown),
            canVent: typeof parsed.canVent === "boolean" ? parsed.canVent : d.canVent,
            visionMultiplier: normalizeVisionMultiplier(parsed.visionMultiplier),
            basis: normalizeBasisDraft(parsed.basis),
            abilityCooldown: normalizeAbilityCooldown(parsed.abilityCooldown),
            canSabotage: normalizeCanSabotageDraft(parsed.canSabotage),
            passives: sanitizePassivesDraft(parsed.passives),
            progressText: typeof parsed.progressText === "string" ? parsed.progressText : d.progressText,
            hostOptions: sanitizeHostOptionsDraft(parsed.hostOptions),
            logicVariables: Array.isArray(parsed.logicVariables) ? (parsed.logicVariables as LogicVariable[]) : d.logicVariables,
            logicBlockly: parsed.logicBlockly ?? d.logicBlockly,
            logicNoBlocklyPassthrough: (parsed.logicNoBlocklyPassthrough as RoleLogic | undefined) ?? d.logicNoBlocklyPassthrough,
        };
    } catch {
        return d;
    }
}

/** フォーム → EkrDefinition (常に requires:[] / winCondition:team を強制する。team はフォームの陣営セレクタから) */
function buildDefinitionFromForm(): EkrDefinition | null {
    const raw = readForm();
    const candidate: Record<string, unknown> = {
        ekr: 1,
        requires: [] as string[],
        name: raw.name,
        author: raw.author,
        // 空欄は validateEkrDefinition 側でキーごと落ちる (欠落 = ゲーム側の既定文言)。
        description: raw.description,
        descriptionLong: raw.descriptionLong,
        color: raw.color,
        team: raw.team,
        canKill: raw.canKill,
        killCooldown: raw.killCooldown,
        canVent: raw.canVent,
        visionMultiplier: raw.visionMultiplier,
        winCondition: DEFAULT_WIN_CONDITION,
        basis: raw.basis,
        abilityCooldown: raw.abilityCooldown,
        // undefined (陣営どおり) のときは validateEkrDefinition 側で省略と同じに扱われる
        // (value["canSabotage"] は object に無いキーでも常に undefined を返すため、代入自体は無害)。
        canSabotage: raw.canSabotage,
    };
    const logic = currentLogicCandidate();
    if (logic !== undefined) candidate.logic = logic;
    // とくせい: 既定のまま (キー0個) なら passives キー自体を書き出さない (バニラ既定 = 欠落)。
    // countsAs はフォーム欄を持たないため currentPassives() (passivesPassthrough 込み) を使う。
    const passives = currentPassives();
    if (Object.keys(passives).length > 0) candidate.passives = passives;

    // Wave 3 (契約 §3): なまえのよこに出す文字。空欄なら progress キー自体を書き出さない。
    const trimmedProgress = raw.progressText.trim();
    if (trimmedProgress.length > 0) candidate.progress = { text: trimmedProgress };

    // Wave 3 (契約 §4): ホストがへんこうできる数値。0件なら hostOptions キー自体を書き出さない。
    if (hostOptionsDraft.length > 0) candidate.hostOptions = hostOptionsDraft;

    const r = validateEkrDefinition(candidate);
    if (!r.ok) {
        // 空っぽのきっかけブロックが原因のときは index 表記ではなくきっかけ名で伝える
        // (refreshLogicPanel の同名処理と同じ理由 — あちらは常時表示、こちらはコピー押下時)。
        const state = currentBlocklyState();
        const empties = state ? findEmptyWhenBlocks(state) : [];
        if (empties.length > 0) {
            const names = empties.map((e) => `「${blocklyApi?.WHEN_LABELS[e.when as LogicWhen] ?? e.when}」`).join("・");
            setStatus(`${names} の中に何も入っていません (ブロックを1つ以上つなげるか、このきっかけを消してください)`, true);
            return null;
        }
        setStatus(r.error, true);
        return null;
    }
    return r.def;
}

async function copyCode(): Promise<void> {
    const def = buildDefinitionFromForm();
    if (!def) return;
    const code = encodeRoleCode(JSON.stringify(def));
    const manualTa = $<HTMLTextAreaElement>("rm-manual-copy");
    try {
        await navigator.clipboard.writeText(code);
        manualTa.hidden = true;
        setStatus("コードをコピーしました！ゲームのチャットで /role import してください", false);
    } catch {
        // クリップボード不可 → 手動コピー用に表示 (openCodeDialog の「手動コピー」パターンに倣う)
        manualTa.value = code;
        manualTa.hidden = false;
        manualTa.select();
        setStatus("コピーできませんでした。下のコードを選択してコピーしてください (Ctrl+C)", true);
    }
}

/** 読み込んだロジックを、初期化済みならワークスペースへ、未初期化ならペンディング状態へ反映する */
function adoptLoadedLogic(loaded: RoleLogic | undefined): void {
    logicVariables = loaded ? loaded.variables.map((v) => ({ ...v })) : [];
    noBlocklyPassthrough = loaded && loaded.blockly === undefined ? loaded : null;
    const blocklyState = loaded?.blockly ?? null;

    if (workspace && blocklyApi) {
        // 重要: ブロックを load() する前に変数ドロップダウンの選択肢を更新すること。逆にすると
        // 復元される var_set 等の VAR フィールドが「直前の (古い) 変数リスト」を見て検証され、
        // 新しい変数名が選択肢に無いまま読み込まれてしまう (FieldDropdown は現在の選択肢に
        // 無い値を許容しないため、値が欠落/先頭項目に化ける)。
        blocklyApi.setAvailableVariableNames(logicVariables.map((v) => v.name));
        workspace.clear();
        setLogicNotice("");
        if (blocklyState) {
            try {
                blocklyApi.Blockly.serialization.workspaces.load(blocklyState as Record<string, unknown>, workspace);
            } catch {
                setLogicNotice("読み込んだロジックの一部を復元できませんでした (ブロックが壊れている可能性があります)。");
            }
        } else if (loaded) {
            setLogicNotice("このロジックには編集用データが含まれていないため、ブロックとしては表示できません。このまま「コードをコピー」すると元のロジックはそのまま保持されます (このタブで組み替えると上書きされます)。");
        }
    } else {
        pendingBlocklyRestore = blocklyState;
    }
    renderVariablesList();
    refreshLogicPanel();
}

function loadCode(): void {
    const codeText = $<HTMLTextAreaElement>("rm-load-text").value;
    let jsonText: string;
    try {
        jsonText = decodeRoleCode(codeText);
    } catch (e) {
        setStatus((e as Error).message, true);
        return;
    }

    let parsed: unknown;
    try {
        parsed = JSON.parse(jsonText);
    } catch {
        setStatus("役職コードの中身が JSON として読めません", true);
        return;
    }

    const r = validateEkrDefinition(parsed);
    if (!r.ok) {
        setStatus(r.error, true);
        return;
    }

    writeForm({
        name: r.def.name,
        author: r.def.author,
        // キーが無い役職コード (説明文を書いていない・R2 以前のコード) は空欄として読み込む。
        description: r.def.description ?? "",
        descriptionLong: r.def.descriptionLong ?? "",
        color: r.def.color,
        // r.def.team は validateEkrDefinition 通過済みなので必ず3値のどれかだが、EkrDefinition.team の
        // 型は string (契約側は team を専用の enum 型にしていない) — normalizeTeamDraft で EkrTeam へ寄せる。
        team: normalizeTeamDraft(r.def.team),
        canKill: r.def.canKill,
        killCooldown: r.def.killCooldown,
        canVent: r.def.canVent,
        visionMultiplier: r.def.visionMultiplier,
        // Wave 9 (契約 §1): basis/abilityCooldown は team と同じく常に解決済みの値を持つ。
        basis: r.def.basis,
        abilityCooldown: r.def.abilityCooldown,
        // Wave 9 追記: キーが無い役職コードは「陣営どおり」(undefined) として読み込む。
        canSabotage: r.def.canSabotage,
        // Wave 3 (契約 §3): キーが無い役職コードは空欄として読み込む (description と同じ扱い)。
        progressText: r.def.progress?.text ?? "",
    });
    writePassivesForm(r.def.passives ?? {});
    // Wave 9 (契約 §3): countsAs は writePassivesForm の対象外 (UI 欄が無い) なので、
    // 読み込んだ値を passivesPassthrough へ引き継ぐ (引き継がないと再コピー時に無音で消える —
    // passives.disguise と同じ「トップレベル新キーはフォーム層5経路すべて配線する」不変条件)。
    passivesPassthrough = { countsAs: r.def.passives?.countsAs };
    // Wave 3 (契約 §4): adoptLoadedLogic() が末尾で renderVariablesList() → renderHostOptionsList()
    // を呼ぶため、その前に hostOptionsDraft を差し替えておく (key ドロップダウンの選択肢が
    // 新しい logicVariables を反映した状態で hostOptionsDraft の中身も一緒に描画されるように)。
    hostOptionsDraft = r.def.hostOptions ? r.def.hostOptions.map((o) => ({ ...o })) : [];
    adoptLoadedLogic(r.def.logic);
    saveFormToStorage();
    renderPreview();
    $<HTMLTextAreaElement>("rm-load-text").value = "";
    setStatus("コードを読み込みました (フォームに反映しました)", false);
}

// ---------------------------------------------------------------------------
// ロジックタブ: 変数リスト UI
// ---------------------------------------------------------------------------

function syncVariableNamesToBlockly(): void {
    blocklyApi?.setAvailableVariableNames(logicVariables.map((v) => v.name));
}

function renderVariablesList(): void {
    // <details> の summary (折りたたみ時にも見える個数表示)。details 要素自体は index.html の
    // 静的 DOM に置いたままなので、ここで中身 (#rm-vars-list) だけ再構築しても開閉状態は保たれる。
    $("rm-vars-summary").textContent = `変数 (${logicVariables.length}個)`;

    const list = $("rm-vars-list");
    list.replaceChildren();
    logicVariables.forEach((v, i) => {
        const row = document.createElement("div");
        row.className = "rm-var-row";

        const nameInput = document.createElement("input");
        nameInput.className = "rm-var-name";
        nameInput.maxLength = LOGIC_VAR_NAME_MAX;
        nameInput.value = v.name;
        nameInput.placeholder = "変数名";
        nameInput.setAttribute("aria-label", "変数名");
        nameInput.addEventListener("change", () => {
            const trimmed = nameInput.value.trim().slice(0, LOGIC_VAR_NAME_MAX);
            const newName = trimmed.length > 0 ? trimmed : logicVariables[i].name;
            logicVariables[i] = { ...logicVariables[i], name: newName };
            nameInput.value = newName;
            syncVariableNamesToBlockly();
            renderHostOptionsList();
            saveFormToStorage();
            refreshLogicPanel();
        });

        const initInput = document.createElement("input");
        initInput.className = "rm-var-init";
        initInput.type = "number";
        initInput.step = "1";
        initInput.value = String(v.init);
        initInput.title = "初期値";
        initInput.setAttribute("aria-label", `${v.name || "変数"} の初期値`);
        initInput.addEventListener("change", () => {
            const n = Number(initInput.value);
            logicVariables[i] = { ...logicVariables[i], init: Number.isFinite(n) ? n : 0 };
            initInput.value = String(logicVariables[i].init);
            saveFormToStorage();
            refreshLogicPanel();
        });

        const removeBtn = document.createElement("button");
        removeBtn.type = "button";
        removeBtn.className = "rm-var-remove";
        removeBtn.textContent = "✕";
        removeBtn.title = "この変数を削除";
        removeBtn.addEventListener("click", () => {
            logicVariables.splice(i, 1);
            syncVariableNamesToBlockly();
            renderVariablesList();
            saveFormToStorage();
            refreshLogicPanel();
        });

        row.append(nameInput, initInput, removeBtn);
        list.appendChild(row);
    });
    $<HTMLButtonElement>("rm-vars-add").disabled = logicVariables.length >= LOGIC_VARIABLES_MAX;
    // hostOptions の key ドロップダウン (固定6種 + 宣言済み変数) は変数リストの増減に追随する。
    renderHostOptionsList();
}

function addVariable(): void {
    if (logicVariables.length >= LOGIC_VARIABLES_MAX) return;
    const existing = new Set(logicVariables.map((v) => v.name));
    let n = logicVariables.length + 1;
    let name = `へんすう${n}`;
    while (existing.has(name)) {
        n++;
        name = `へんすう${n}`;
    }
    logicVariables.push({ name, init: 0 });
    syncVariableNamesToBlockly();
    renderVariablesList();
    saveFormToStorage();
    refreshLogicPanel();
}

// ---------------------------------------------------------------------------
// ロジックタブ: 検証状態 + 警告フッタ
// ---------------------------------------------------------------------------

function setLogicNotice(msg: string): void {
    const el = $("rm-logic-notice");
    el.textContent = msg;
    el.hidden = msg.length === 0;
}

/** ワークスペース (または読込済み state) を再コンパイル→検証し、検証エラー/リンター警告を表示する */
function refreshLogicPanel(): void {
    const validityEl = $("rm-logic-validity");
    const footer = $("rm-lint-footer");

    const state = currentBlocklyState();
    if (!state) {
        validityEl.hidden = true;
        footer.hidden = true;
        footer.replaceChildren();
        return;
    }

    // 空っぽのきっかけブロックは validateRoleLogic の「rules[i].do のノード数は…(現在 0 個)」で
    // 弾かれるが、検証は最初の1件で打ち切るため index 表記のままだと (a) どのブロックのことか
    // 分からず (b) 他の場所にブロックを足しても文面が変わらない。ここで先回りしてきっかけ名で
    // 伝え、該当ブロックへジャンプできるようにする。
    const empties = findEmptyWhenBlocks(state);
    if (empties.length > 0) {
        const names = empties.map((e) => `「${blocklyApi?.WHEN_LABELS[e.when as LogicWhen] ?? e.when}」`).join("・");
        validityEl.hidden = false;
        validityEl.replaceChildren(
            document.createTextNode(
                `⚠ このままだとコピーできません: ${names} の中に何も入っていません (ブロックを1つ以上つなげるか、このきっかけを消してください)`,
            ),
        );
        const jumpId = empties[0].id;
        if (jumpId !== undefined && workspace) {
            const jump = document.createElement("button");
            jump.type = "button";
            jump.className = "rm-validity-jump";
            jump.textContent = "その場所を見る";
            jump.addEventListener("click", () => {
                if (!workspace) return;
                workspace.centerOnBlock(jumpId);
                workspace.getBlockById(jumpId)?.select();
            });
            validityEl.appendChild(jump);
        }
        footer.hidden = true;
        footer.replaceChildren();
        return;
    }

    const compiled = compileWorkspaceToLogicInput(state, logicVariables);
    if (!compiled) {
        // ルールが1つも無い (何も組んでいない) — エラー表示はしない、ただの空状態
        validityEl.hidden = true;
        footer.hidden = true;
        footer.replaceChildren();
        return;
    }

    const validated = validateRoleLogic(compiled);
    if (!validated.ok) {
        validityEl.hidden = false;
        validityEl.textContent = `⚠ このままだとコピーできません: ${validated.error}`;
        footer.hidden = true;
        footer.replaceChildren();
        return;
    }
    validityEl.hidden = true;

    // Wave 3 (契約 §6 L25): progress.text の変数参照もリンタに渡す (トリム前の生値で良い —
    // extractProgressVarRefs は {変数名} トークンだけを見るので前後の空白は影響しない)。
    // Wave 9 (契約 §6 L31〜L33): team/basis/abilityCooldown/countsAs もまとめて渡す。
    // Wave 12 (契約 §7 L41/L42): voteWeight/anonymousVote/disguiseTeam/disguiseDeep も渡す。
    const raw = readForm();
    const passivesForLint = currentPassives();
    const warnings = lintRoleLogic(validated.logic, $<HTMLInputElement>("rm-progress-text").value, {
        team: raw.team,
        basis: raw.basis,
        abilityCooldown: raw.abilityCooldown,
        // countsAs はフォーム欄を持たないため currentPassives() (passivesPassthrough 込み) を使う
        // (L31 の対象は読み込んだコードの countsAs — readPassivesForm() だけだと常に undefined になる)。
        countsAs: passivesForLint.countsAs,
        voteWeight: passivesForLint.voteWeight,
        anonymousVote: passivesForLint.anonymousVote,
        disguiseTeam: passivesForLint.disguise?.team,
        disguiseDeep: passivesForLint.disguise?.deep,
    });
    footer.replaceChildren();
    if (warnings.length === 0) {
        footer.hidden = true;
        return;
    }
    footer.hidden = false;
    const heading = document.createElement("p");
    heading.className = "rm-lint-heading";
    heading.textContent = "⚠️ この組み方だと公式サーバーで蹴られたり動かなかったりするかも…";
    footer.appendChild(heading);
    const ul = document.createElement("ul");
    for (const w of warnings) {
        const li = document.createElement("li");
        li.textContent = formatLintWarning(w);
        ul.appendChild(li);
    }
    footer.appendChild(ul);
}

let autosaveTimer: ReturnType<typeof setTimeout> | null = null;

/** Blockly の change イベントはドラッグ中など高頻度で飛ぶため、少し待ってからまとめて保存/再検証する */
function scheduleWorkspaceAutosave(): void {
    if (autosaveTimer !== null) clearTimeout(autosaveTimer);
    autosaveTimer = setTimeout(() => {
        autosaveTimer = null;
        saveFormToStorage();
        refreshLogicPanel();
    }, 400);
}

// ---------------------------------------------------------------------------
// ライブプレビュー (「基本情報」タブ): ゲーム内でどう見えるかの簡易再現。
// renderPreview() は readForm() / currentLogicCandidate() など「今の状態」を読んで対応する
// #rm-preview-* 要素へ書き込むだけの純粋表示関数 (自分では状態を持たない)。
// ---------------------------------------------------------------------------

/** 見た目用の数値整形 (25 → "25", 1.5 → "1.5")。killCooldown/visionMultiplier 表示専用。 */
function formatPreviewNumber(n: number): string {
    return Number(n.toFixed(2)).toString();
}

/**
 * currentLogicCandidate() から「トリガー種別 (when) の数」だけを軽く読み取る。表示専用の概算であり
 * validateRoleLogic() のような完全な AST 検証はしない — renderPreview() は #rm-name への1打鍵ごとに
 * 呼ばれるため、毎回ワークスペース全体を再検証するのはコストに見合わない (検証そのものは
 * refreshLogicPanel() が別途・Blockly の change イベントに debounce して担当している)。
 * when が文字列でない/欠けている壊れた rule は単に数えないだけで、エラー表示はしない。
 */
function currentLogicTriggerCount(): number {
    const candidate = currentLogicCandidate() as { rules?: unknown[] } | undefined;
    if (!candidate || !Array.isArray(candidate.rules)) return 0;
    const whens = new Set<string>();
    for (const rule of candidate.rules) {
        const when = (rule as { when?: unknown } | null)?.when;
        if (typeof when === "string") whens.add(when);
    }
    return whens.size;
}

/** 「基本情報」タブのライブプレビューを今のフォーム状態に合わせて更新する。副作用は DOM 書き込みのみ。 */
function renderPreview(): void {
    const raw = readForm();
    const color = normalizeColor(raw.color);
    const trimmedName = raw.name.trim();
    const displayName = trimmedName.length > 0 ? trimmedName : "（役職名未設定）";

    const headName = $("rm-preview-head-name");
    headName.textContent = displayName;
    headName.style.color = color;

    const bannerName = $("rm-preview-banner-name");
    bannerName.textContent = displayName;
    bannerName.style.color = color;

    $("rm-preview-banner-team").textContent = TEAM_LABELS[raw.team];

    // 短い説明はゲーム側で Info キーとして役職バナー/パネルに出る。空欄なら行ごと隠す
    // (ゲーム側も空欄ならスロット共通の既定文言に戻るので、ここで既定文言は模写しない)。
    const bannerDesc = $("rm-preview-banner-desc");
    const previewDesc = raw.description.replace(/[\r\n]+/g, " ").trim();
    bannerDesc.textContent = previewDesc;
    bannerDesc.hidden = previewDesc.length === 0;

    $("rm-preview-avatar").style.setProperty("--rm-avatar-color", color);

    const killCd = normalizeKillCooldown(raw.killCooldown);
    const vision = normalizeVisionMultiplier(raw.visionMultiplier);
    const showVision = Math.abs(vision - 1) > 1e-6;

    const killChip = $("rm-preview-ability-kill");
    killChip.hidden = !raw.canKill;
    if (raw.canKill) killChip.textContent = `⚔ キルできる (再使用まで ${formatPreviewNumber(killCd)} 秒)`;

    $("rm-preview-ability-vent").hidden = !raw.canVent;

    const visionChip = $("rm-preview-ability-vision");
    visionChip.hidden = !showVision;
    if (showVision) visionChip.textContent = `👁 視界 ${formatPreviewNumber(vision)} 倍`;

    // とくせい (Wave 1): ON のものだけをチップとして並べる (項目数が可変なので毎回作り直す)。
    const passiveChips = passiveChipTexts(readPassivesForm());
    const passiveContainer = $("rm-preview-passive-chips");
    passiveContainer.replaceChildren();
    for (const text of passiveChips) {
        const chip = document.createElement("p");
        chip.className = "rm-preview-chip";
        chip.textContent = text;
        passiveContainer.appendChild(chip);
    }

    $("rm-preview-ability-none").hidden = raw.canKill || raw.canVent || showVision || passiveChips.length > 0;

    const triggerCount = currentLogicTriggerCount();
    const logicSummary = $("rm-preview-logic-summary");
    logicSummary.hidden = triggerCount === 0;
    if (triggerCount > 0) logicSummary.textContent = `ブロックロジック: きっかけ ${triggerCount} 種`;
}

// ---------------------------------------------------------------------------
// タブ切替 + Blockly 初期化 (dynamic import)
// ---------------------------------------------------------------------------

type RmTab = "basic" | "logic";

function setActiveTab(tab: RmTab): void {
    $("rm-panel-basic").hidden = tab !== "basic";
    $("rm-panel-logic").hidden = tab !== "logic";
    // ロジックタブ表示中は #dlg-role-maker 全体を ComfyUI 風の全画面フローティング UI へ
    // 切り替える (CSS 側の #dlg-role-maker[data-rm-tab="logic"] セレクタ群がこれを見て
    // dialog の padding・タブ/変数/menu/状態表示のフローティング化・ワークスペースの
    // 全面化を行う)。
    $<HTMLDialogElement>("dlg-role-maker").dataset.rmTab = tab;
    for (const btn of document.querySelectorAll<HTMLButtonElement>("#rm-tabs .rm-tab")) {
        btn.classList.toggle("active", btn.dataset.rmTab === tab);
    }
    if (tab === "logic") {
        if (workspace && blocklyApi) {
            // ダイアログを閉じている間コンテナは表示サイズ 0 になるため、Blockly が持つ
            // キャッシュ済みメトリクスが古くなる (再表示直後は空白/ズレて見えることがある)。
            // 全画面フローティングレイアウトへの切替は #rm-blockly-container のサイズを
            // position:absolute;inset:0 で一気に変えるため、ResizeObserver の非同期発火を
            // 待たず同期で1回呼び、念のため次フレームでもう一度呼ぶ (svgResize の重複呼び出しは無害)。
            blocklyApi.Blockly.svgResize(workspace);
            requestAnimationFrame(() => {
                if (workspace && blocklyApi) blocklyApi.Blockly.svgResize(workspace);
            });
        } else {
            void ensureLogicTabReady();
        }
    } else {
        // ロジックタブでブロックを組んでから基本情報タブへ戻ってきたときに、ロジック要約行
        // (renderPreview の項目d) が古いままにならないよう更新する。currentLogicCandidate() は
        // ワークスペースの現在値を都度読むので、この呼び出しだけで最新化できる。
        renderPreview();
    }
}

async function ensureLogicTabReady(): Promise<void> {
    if (workspace || logicTabLoading) return;
    logicTabLoading = true;
    try {
        blocklyApi = await import("./blocks-role");
        blocklyApi.defineRoleBlocks();
        blocklyApi.setAvailableVariableNames(logicVariables.map((v) => v.name));

        // 重要: WidgetDiv/DropDownDiv/Tooltip (フィールド編集用のフローティング要素) は
        // 「初回の Blockly.inject 時点」の親コンテナに描画される (Blockly.setParentContainer の
        // JSDoc: 「This method is a NOP if called after the first inject」) — 必ず inject() の
        // 前に呼ぶこと。既定は document.body 直下だが、このダイアログは showModal() で開くため
        // (ネイティブ <dialog> の top layer 昇格)、body 直下の要素は backdrop の下に隠れて
        // クリックできなくなる。ダイアログ自身を親にすることで同じ top layer に含める。
        blocklyApi.Blockly.setParentContainer($<HTMLDialogElement>("dlg-role-maker"));

        const container = $("rm-blockly-container");
        workspace = blocklyApi.Blockly.inject(container, {
            toolbox: blocklyApi.buildRoleToolbox(),
            renderer: "zelos",
            trashcan: true,
            // Blockly の既定テーマはワークスペース背景が白固定 (CSS だけでは上書きしきれない —
            // blocks-role.ts のコメント参照)。これを指定しないと直後の grid.colour (薄い白) が
            // 白背景に沈んで不可視になる。
            theme: blocklyApi.buildRoleTheme(),
            // ComfyUI 風の操作感 (全画面化しても作業スペースが手狭に感じる問題への対応):
            // 背景をつかんでドラッグでパンでき、素ホイールはズームに譲る。move.wheel と
            // zoom.wheel を両方 true にすると「ホイールがスクロールとズームの両方を起こす」
            // 挙動になる (Blockly 公式のズーム設定ガイドが明記する既知の組み合わせ事故) ため
            // 片方だけ true にする。
            move: {
                scrollbars: { horizontal: true, vertical: true },
                drag: true,
                wheel: false,
            },
            zoom: {
                controls: true,
                wheel: true,
                pinch: true,
                startScale: 1,
                minScale: 0.4,
                maxScale: 2.5,
                scaleSpeed: 1.08,
            },
            // ComfyUI 風の方眼背景。colour は SVG line の stroke 属性へそのまま渡るので
            // rgba() で薄い白にできる (ダーク背景 --bg-2 に馴染む主張しすぎない目盛り)。
            grid: {
                spacing: 24,
                length: 4,
                colour: "rgba(255, 255, 255, 0.06)",
                snap: false,
            },
        });

        if (pendingBlocklyRestore) {
            try {
                blocklyApi.Blockly.serialization.workspaces.load(pendingBlocklyRestore as Record<string, unknown>, workspace);
            } catch {
                setLogicNotice("読み込んだロジックの一部を復元できませんでした (ブロックが壊れている可能性があります)。");
            }
            pendingBlocklyRestore = null;
        } else if (noBlocklyPassthrough) {
            setLogicNotice("このロジックには編集用データが含まれていないため、ブロックとしては表示できません。このまま「コードをコピー」すると元のロジックはそのまま保持されます (このタブで組み替えると上書きされます)。");
        }

        // 全画面レイアウトでは作業スペースの寸法が「ウィンドウのリサイズ / 変数リストの増減 /
        // 警告フッタの出入り / ダイアログの再表示 (閉じている間は寸法 0)」で変わる。Blockly は
        // メトリクスをキャッシュするので、寸法が変わるたびに svgResize しないと描画がズレる。
        // コールバックは rAF に逃がす (RO コールバック内で同期的にレイアウトを触ると
        // "ResizeObserver loop" 警告の原因になるため)。
        if (typeof ResizeObserver !== "undefined") {
            containerObserver = new ResizeObserver(() => {
                requestAnimationFrame(() => {
                    if (workspace && blocklyApi) blocklyApi.Blockly.svgResize(workspace);
                });
            });
            containerObserver.observe(container);

            // 変数パネル (浮き details) の実高さを CSS 変数 --rm-vars-h へ流す。全画面レイアウトの
            // ワークスペース上端予約 (style.css: calc(76px + var(--rm-vars-h))) がパネルの開閉・
            // 変数の増減に実寸で追随するため。予約帯の変化はコンテナ寸法の変化として上の
            // containerObserver が拾い svgResize する (連鎖は一方向なのでループしない)。
            const varsSection = $("rm-vars-section");
            varsObserver = new ResizeObserver(() => {
                requestAnimationFrame(() => {
                    $<HTMLDialogElement>("dlg-role-maker").style.setProperty("--rm-vars-h", `${varsSection.offsetHeight}px`);
                });
            });
            varsObserver.observe(varsSection);
        }

        workspace.addChangeListener(() => scheduleWorkspaceAutosave());
        renderVariablesList();
        refreshLogicPanel();
    } catch (err) {
        console.error("ロジック機能の読み込みに失敗しました", err);
        setLogicNotice("ロジック機能を読み込めませんでした (ページを再読み込みしてもう一度お試しください)。");
    } finally {
        logicTabLoading = false;
    }
}

/** フォーム入力の共通後処理: 下書き保存 + プレビュー更新 (このペアは常にセットで呼ぶ)。 */
function onFormEdit(): void {
    saveFormToStorage();
    renderPreview();
}

let wired = false;

function wire(): void {
    if (wired) return;
    wired = true;

    const draft = loadFormFromStorage();
    writeForm(draft);
    writePassivesForm(draft.passives);
    // Wave 9 (契約 §3): countsAs は writePassivesForm の対象外 (UI 欄が無い) — loadCode() と同じく
    // passivesPassthrough へ引き継ぐ (引き継がないと下書き復元のたびに無音で消える)。
    passivesPassthrough = { countsAs: draft.passives.countsAs };
    hostOptionsDraft = draft.hostOptions;
    logicVariables = draft.logicVariables;
    pendingBlocklyRestore = draft.logicBlockly;
    noBlocklyPassthrough = draft.logicNoBlocklyPassthrough;
    renderVariablesList();
    renderPreview();

    $<HTMLInputElement>("rm-name").addEventListener("input", onFormEdit);
    $<HTMLInputElement>("rm-author").addEventListener("input", onFormEdit);
    $<HTMLInputElement>("rm-desc").addEventListener("input", onFormEdit);
    $<HTMLTextAreaElement>("rm-desc-long").addEventListener("input", onFormEdit);
    $<HTMLInputElement>("rm-color").addEventListener("input", onFormEdit);
    $<HTMLSelectElement>("rm-team").addEventListener("change", () => {
        onFormEdit();
        // Wave 9 (契約 §6 L31): かちのかぞえかたのヒントは陣営に依存するため再検査する。
        refreshLogicPanel();
    });
    $<HTMLInputElement>("rm-can-vent").addEventListener("change", onFormEdit);
    // Wave 9 追記: サボタージュを つかえる (3状態セレクタ・リンタ対象外なので refreshLogicPanel 不要)。
    $<HTMLSelectElement>("rm-can-sabotage").addEventListener("change", onFormEdit);

    // Wave 9 (契約 §1): はつどうのしかた + とくいわざの まちじかん。
    $<HTMLSelectElement>("rm-basis").addEventListener("change", () => {
        refreshAbilityCdVisibility();
        onFormEdit();
        refreshLogicPanel();
    });
    $<HTMLInputElement>("rm-ability-cd").addEventListener("change", () => {
        const el = $<HTMLInputElement>("rm-ability-cd");
        const raw = el.value.trim();
        el.value = String(normalizeAbilityCooldown(raw === "" ? NaN : Number(raw)));
        onFormEdit();
        refreshLogicPanel();
    });
    // Wave 3 (契約 §3): なまえのよこに出す文字。description と同じ単純テキスト欄の配線。
    $<HTMLInputElement>("rm-progress-text").addEventListener("input", () => {
        onFormEdit();
        refreshLogicPanel();
    });

    $<HTMLInputElement>("rm-can-kill").addEventListener("change", () => {
        refreshKillCdVisibility();
        onFormEdit();
    });

    $<HTMLInputElement>("rm-kill-cd").addEventListener("change", () => {
        const el = $<HTMLInputElement>("rm-kill-cd");
        const raw = el.value.trim();
        el.value = String(normalizeKillCooldown(raw === "" ? NaN : Number(raw)));
        onFormEdit();
    });

    $<HTMLInputElement>("rm-vision").addEventListener("change", () => {
        const el = $<HTMLInputElement>("rm-vision");
        const raw = el.value.trim();
        el.value = String(normalizeVisionMultiplier(raw === "" ? NaN : Number(raw)));
        onFormEdit();
    });

    // とくせい (Wave 1): 数値欄は change で範囲内へ丸めて書き戻す (rm-kill-cd と同じ作法 —
    // 契約側は範囲外を reject するので、フォームから出る値は常に範囲内であることを保証する)。
    const clampPassiveNumberOnChange = (id: string, min: number, max: number, fallback: number, integer: boolean): void => {
        $<HTMLInputElement>(id).addEventListener("change", () => {
            const el = $<HTMLInputElement>(id);
            const raw = el.value.trim();
            const n = raw === "" ? NaN : Number(raw);
            el.value = String(integer ? clampIntToRange(n, min, max, fallback) : clampToRange(n, min, max, fallback));
            onFormEdit();
        });
    };
    clampPassiveNumberOnChange("rm-p-speed", PASSIVE_SPEED_MULT_MIN, PASSIVE_SPEED_MULT_MAX, PASSIVE_FORM_DEFAULTS.speedMult, false);
    clampPassiveNumberOnChange("rm-p-shield-count", PASSIVE_SHIELD_COUNT_MIN, PASSIVE_SHIELD_COUNT_MAX, PASSIVE_FORM_DEFAULTS.shieldCount, true);
    clampPassiveNumberOnChange("rm-p-vote", PASSIVE_VOTE_WEIGHT_MIN, PASSIVE_VOTE_WEIGHT_MAX, PASSIVE_FORM_DEFAULTS.voteWeight, true);
    clampPassiveNumberOnChange("rm-p-doom-seconds", PASSIVE_DOOM_SECONDS_MIN, PASSIVE_DOOM_SECONDS_MAX, PASSIVE_FORM_DEFAULTS.doomSeconds, true);

    for (const id of ["rm-p-killdist", "rm-p-corpse"]) {
        $<HTMLSelectElement>(id).addEventListener("change", onFormEdit);
    }
    // Wave 12 (契約 §4): ころした死体は だれのか わからない (リンタ対象外の単純 checkbox)。
    $<HTMLInputElement>("rm-p-anonymous-kills").addEventListener("change", onFormEdit);
    // Wave 12 (契約 §7 L41): 票のちから (rm-p-vote) は anonymousVote と組み合わせてリンタが見るため、
    // クランプ後 (onFormEdit を呼ぶ既存リスナーの後) に refreshLogicPanel も呼ぶ。
    $<HTMLInputElement>("rm-p-vote").addEventListener("change", refreshLogicPanel);
    $<HTMLInputElement>("rm-p-anonymous-vote").addEventListener("change", () => {
        onFormEdit();
        refreshLogicPanel();
    });
    // Wave 12 (契約 §2): べつの陣営に見せる — team を切り替えたら役職名 select を作り直す
    // (populateDisguiseRoleOptions が team 不一致の残留値を空欄へ落とす)。
    $<HTMLSelectElement>("rm-p-disguise").addEventListener("change", () => {
        const team = $<HTMLSelectElement>("rm-p-disguise").value;
        populateDisguiseRoleOptions(team, $<HTMLSelectElement>("rm-p-disguise-role").value);
        refreshPassiveRowVisibility();
        onFormEdit();
        refreshLogicPanel();
    });
    $<HTMLSelectElement>("rm-p-disguise-role").addEventListener("change", onFormEdit);
    // Wave 12 (契約 §7 L42): deep はリンタが team との一致を見るため refreshLogicPanel も呼ぶ。
    $<HTMLInputElement>("rm-p-disguise-deep").addEventListener("change", () => {
        onFormEdit();
        refreshLogicPanel();
    });
    for (const id of ["rm-p-shield", "rm-p-doom"]) {
        $<HTMLInputElement>(id).addEventListener("change", () => {
            refreshPassiveRowVisibility();
            onFormEdit();
        });
    }

    $("rm-copy").addEventListener("click", () => void copyCode());
    $("rm-load-btn").addEventListener("click", () => loadCode());
    $("rm-close").addEventListener("click", () => $<HTMLDialogElement>("dlg-role-maker").close());

    for (const btn of document.querySelectorAll<HTMLButtonElement>("#rm-tabs .rm-tab")) {
        btn.addEventListener("click", () => setActiveTab(btn.dataset.rmTab as RmTab));
    }
    $("rm-vars-add").addEventListener("click", addVariable);
    $("rm-hostopts-add").addEventListener("click", addHostOption);

    // 入口は2つ: マップエディタのヘッダー (作業中に開く) と、スタート画面の独立項目
    // (マップを一切触らずに役職だけ作る導線)。どちらも同じ openRoleMaker() を呼ぶ。
    for (const id of ["btn-role-maker", "start-role-maker"]) {
        document.getElementById(id)?.addEventListener("click", openRoleMaker);
    }
}

/** 役職メーカーを開く (全画面モーダル)。入口が増えてもここだけを呼ぶこと */
function openRoleMaker(): void {
    setStatus("", false);
    $<HTMLTextAreaElement>("rm-manual-copy").hidden = true;
    $<HTMLDialogElement>("dlg-role-maker").showModal();
    renderPreview();
    // 閉じている間コンテナは寸法 0 なので、ロジックタブを開いたまま閉じて再表示すると
    // メトリクスが腐っている。ResizeObserver でも拾えるが、念のため明示的に合わせる。
    if (workspace && blocklyApi && !$("rm-panel-logic").hidden) {
        blocklyApi.Blockly.svgResize(workspace);
    }
}

/** main.ts からの唯一の呼び出し口。何度呼んでも二重配線しない。 */
export function initRoleMaker(): void {
    wire();
}
