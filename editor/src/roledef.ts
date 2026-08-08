// EKN 役職コード (EKR1.) のデータ契約 — Modules/Ekm/EkrDefinition.cs の TS 側ミラー (R0: フォームのみ・ロジック無し)。
// 計画正典: docs/ekn-api-plan.md。フィールド名・デフォルト値・検証規則は C# 側の EkrDefinition.Validate() と
// 一致させること (契約の正は C# 側)。役職コードはマップコード (docs/ekmap-spec.md) とは独立した契約であり、
// このファイルは model.ts/validate.ts (マップ契約) には一切依存しない。
//
// 意図的な逸脱 (C# は JSON 型不一致で例外を投げて拒否するが、TS 側は貼り付けコードの手編集ミスに
// 寛容な方が UX 上望ましいため、型が違う値は「無いもの」として既定値へ黙ってフォールバックする):
//   - canKill/canVent: bool 以外 → false
//   - killCooldown/visionMultiplier: number 以外 (NaN 含む) → 既定値からクランプ
// 一方で契約上意味のある拒否 (ekr 不一致 / name 空 / team 不一致 / requires 未対応) はエラーのまま維持する。

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

// R0 が対応する capability の集合 (現状は空 = requires は常に空配列でなければ拒否)。
const SUPPORTED_CAPABILITIES: ReadonlySet<string> = new Set();

// input type="color" は常に #rrggbb (小文字6桁) しか出力しないため、この契約でも #rrggbb のみを
// 正とする。Unity の ColorUtility.TryParseHtmlString は名前付き色や短縮形も受理するがそれより狭い —
// エディタから作られるコードの実際の値域に合わせた意図的な narrowing。
const COLOR_RE = /^#[0-9a-fA-F]{6}$/;

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

/**
 * JSON.parse 済みの値を検証して EkrDefinition に変換する (Modules/Ekm/EkrDefinition.cs の
 * Validate() と同じ規則)。失敗時は日本語の平易文でエラーを返す。数値クランプ/文字切詰め/色
 * フォールバックは黙って補正する (エラーにしない) — C# 側もこれらは Validate() 内で黙って補正している。
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

    // name: 型が違えば「無し」として扱う (トリム後に空なら拒否)。C# の `(Name ?? "").Trim()` と同じ収束。
    let name = typeof value.name === "string" ? value.name.trim() : "";
    if (name.length === 0) {
        return { ok: false, error: "役職コードに名前 (name) がありません" };
    }
    if (name.length > ROLE_NAME_MAX) name = name.slice(0, ROLE_NAME_MAX);

    let author = typeof value.author === "string" ? value.author.trim() : "";
    if (author.length > ROLE_AUTHOR_MAX) author = author.slice(0, ROLE_AUTHOR_MAX);

    const color = normalizeColor(value.color);

    // team: キー省略/null は既定 "crewmate" に収束するが、明示的な空文字/他の値はエラー
    // (C# の `(Team ?? "crewmate").Trim().ToLowerInvariant()` → crewmate 比較と同じ非対称性)。
    const rawTeam = value.team;
    const team = rawTeam === undefined || rawTeam === null
        ? SUPPORTED_TEAM
        : (typeof rawTeam === "string" ? rawTeam : String(rawTeam)).trim().toLowerCase();
    if (team !== SUPPORTED_TEAM) {
        return { ok: false, error: `この End K not のバージョンでは team="${team}" の役職コードにはまだ対応していません (現在は team="${SUPPORTED_TEAM}" のみ対応)` };
    }

    const canKill = value.canKill === true;
    const canVent = value.canVent === true;

    const killCooldown = normalizeKillCooldown(value.killCooldown);
    const visionMultiplier = normalizeVisionMultiplier(value.visionMultiplier);

    // winCondition: team と違い R0 でも他の値をそのまま保持する (C# 側にエラー分岐が無い —
    // ゲーム側が現状常に通常のクルー勝利にフォールバックして使うだけの未使用フィールド)。
    const rawWin = value.winCondition;
    const winCondition = rawWin === undefined || rawWin === null
        ? DEFAULT_WIN_CONDITION
        : (typeof rawWin === "string" ? rawWin : String(rawWin)).trim().toLowerCase();

    return {
        ok: true,
        def: { ekr: 1, requires, name, author, color, team, canKill, killCooldown, canVent, visionMultiplier, winCondition },
    };
}
