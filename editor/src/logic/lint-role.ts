// docs/ekr-logic-spec.md §6 — エディタ AST リンター (二層防御のヒント層)。
// 「ブロックは組める・書き出しもできる」が前提 — ここで見つけた問題は *警告* であり、
// roledef.ts の validateRoleLogic (契約違反=文書全体 reject) とは別物。呼び出し元は既に
// 検証済みの RoleLogic (roledef.ts の型) だけを渡すこと — このモジュール自身は妥当性検証をしない。
//
// 静的近似であり制御フロー解析はしない (spec §6 L2 の注記どおり)。例えば if/else の両方の枝で
// 同じ slot に cno_spawn していても、実行時にはどちらか一方しか通らない可能性があるが、
// このリンターはその区別をせず単純にノードの出現数を数える (蓄積 memory の kick 知見を
// ヒントとして出すのが目的であり、正確な静的解析エンジンではない)。
//
// v1.1 (2026-08-09): L9/L10 を追加 (dummy_spawn 新設に伴う会議明けドロップ窓/3秒バケットの輸出)。
// L1 の検知対象に dummy_spawn を追加。L2 は cno_spawn のみのまま (spec §6 表は dummy_spawn を
// 対象に含めていない — slot を共有していても L2 の対象を広げるのは契約外の拡張になる)。
// v1.2 (2026-08-10): L11/L12 を追加 (teleport_other/portal_place/on_cno_touch 新設の輸出)。
// L9 の対象 op を spec §6 表 (2026-08-10 モッド側の兄弟漏れ修正に合わせた4種: cno_spawn/
// cno_show/dummy_spawn/portal_place) に合わせて拡大する (旧実装は dummy_spawn のみだった)。
// v1.3 (2026-08-11): L13 を追加 (pull/drag/field 新設の輸出・L3/L11 の兄弟)。L9/L12 の対象 op に
// field を追加 (CNO 生成系防御3点セット + 会議明け10秒ドロップ + on_cno_touch 誤用検知の対象)。

import type { LogicNode, LogicRule, RoleLogic } from "../roledef";

// Wave 1 (2026-08-11): L14〜L17 を追加 (計17ルール)。L14 = ctx 無しイベント配下の ctx セレクタ、
// L15 = 未保存マーカーへの行き先、L16 = 未生成 CNO / 未保存 saved の参照、L17 = wait より後の
// cancel_attack。L15/L16 は「参照整合性3原則」③ (エディタは欠落を能動的に教える) の実装。
// Wave 2 (docs/ekn-wave2-contract.md §6 2026-08-11): L18〜L20 を追加 (計20ルール) + L16 拡張。
// L18 = 会議専用 op (vote_block/vote_swap/exile) が会議系イベント以外の rule 配下、L19 = L17 の
// 兄弟 (on_meeting_vote 配下で wait より後の cancel_vote)、L20 = L3/L4 のレート系兄弟 (on_second
// 配下の arrow_show/arrow_mark/inspect/reveal)。L16 拡張 = vote_swap の暗黙 saved1/saved2 参照。
// 2026-08-14 (BUG-20260813-04 の机上切り分け): L21 を追加 (計21ルール)。L17/L19 の兄弟で、
// wait より後の exile。

export type LintRuleId =
    | "L1" | "L2" | "L3" | "L4" | "L5" | "L6" | "L7" | "L8" | "L9" | "L10" | "L11" | "L12" | "L13"
    | "L14" | "L15" | "L16" | "L17" | "L18" | "L19" | "L20" | "L21";

export interface LintWarning {
    rule: LintRuleId;
    ruleIndex: number;
    when: string;
    message: string;
    suggestion: string;
}

function forEachNode(nodes: LogicNode[], visit: (n: LogicNode) => void): void {
    for (const n of nodes) {
        visit(n);
        if (n.op === "if") {
            forEachNode(n.then, visit);
            if (n.else) forEachNode(n.else, visit);
        }
    }
}

function hasOp(nodes: LogicNode[], op: LogicNode["op"]): boolean {
    let found = false;
    forEachNode(nodes, (n) => { if (n.op === op) found = true; });
    return found;
}

function countCnoBySlot(nodes: LogicNode[], op: "cno_spawn" | "cno_despawn"): Map<number, number> {
    const counts = new Map<number, number>();
    forEachNode(nodes, (n) => {
        if (n.op === op) counts.set(n.slot, (counts.get(n.slot) ?? 0) + 1);
    });
    return counts;
}

function sumWaitSeconds(nodes: LogicNode[]): number {
    let total = 0;
    forEachNode(nodes, (n) => { if (n.op === "wait") total += n.seconds; });
    return total;
}

// L8/L10: 訪問順 (forEachNode と同じ depth-first) を疑似実行列とみなし、同じ op 同士の間の累積
// wait 秒を追う。初回 spawn の前は Infinity (レートバケットに初期トークンがあるため必ず通る)。
// L8=cno_spawn/1秒、L10=dummy_spawn/3秒 (spec §5 のレート値がそのまま閾値)。cno_spawn と
// dummy_spawn は別バケットの実装なので、それぞれ別 op で呼び出す限り互いのカウンタに影響しない
// (op と一致しないノードは wait 以外なにも起こさず読み飛ばすだけ)。
function hasRapidConsecutiveOp(nodes: LogicNode[], op: "cno_spawn" | "dummy_spawn", thresholdSeconds: number): boolean {
    let sinceLastSpawn = Infinity;
    let violation = false;
    forEachNode(nodes, (n) => {
        if (n.op === "wait") sinceLastSpawn += n.seconds;
        else if (n.op === op) {
            if (sinceLastSpawn < thresholdSeconds) violation = true;
            sinceLastSpawn = 0;
        }
    });
    return violation;
}

// L9: L8/L10 とは起点が異なる — 「前の生成系 op」ではなく「ルール開始 (=会議終了の瞬間)」を
// elapsed=0 とし、一度も 0 にリセットしない訪問順の累積 wait を追う。対象 op (L9_OPS) に出会った
// 時点でまだ thresholdSeconds に届いていなければ違反 (spec §5 の「会議明けから10秒間はドロップ」の
// 静的近似・対象は生成系4兄弟 cno_spawn/cno_show/dummy_spawn/portal_place)。if 分岐は L7 と同じく
// 合算 (forEachNode が then/else 両方を訪れるため、分岐の択一は見ない — 「wait, dummy_spawn」の
// ような直列の前後関係だけを区別する)。
// v1.3 (spec §5/§6 2026-08-11 追記): field も CNO 生成系防御3点セット + 会議明け10秒ドロップの
// 対象 (生成系5兄弟目) に加わったため L9_OPS に追加する。
const L9_OPS: ReadonlySet<LogicNode["op"]> = new Set(["cno_spawn", "cno_show", "dummy_spawn", "portal_place", "field"]);

function hasGenerationOpBeforeElapsed(nodes: LogicNode[], ops: ReadonlySet<LogicNode["op"]>, thresholdSeconds: number): boolean {
    let elapsed = 0;
    let violation = false;
    forEachNode(nodes, (n) => {
        if (n.op === "wait") elapsed += n.seconds;
        else if (ops.has(n.op) && elapsed < thresholdSeconds) violation = true;
    });
    return violation;
}

// ---------------------------------------------------------------------------
// Wave 1: セレクタ参照の静的検査 (L14/L15/L16) 用の共通ヘルパー
// ---------------------------------------------------------------------------

// spec §6 L14 の対象イベント (「あいて」を持たないもの) をそのまま列挙する。
// ctx を持つイベント (on_kill/on_death/on_report/on_cno_touch/on_attacked) はここに入れない。
const CTXLESS_WHENS: ReadonlySet<string> = new Set([
    "on_game_start", "on_pet", "on_meeting_start", "on_meeting_end", "on_task_complete", "on_vent_enter", "on_second",
]);

/**
 * ノードが持つ「セレクタ値」(spec §3 のフィールド target/at/to の文字列値) を集める。
 * ※ 引数を持たない ctx 暗黙 op (`pull`/`drag`) はここには出てこないので、L14 側で別途 op 名で見る。
 * cno_show の `who` や cno_spawn の `slot` のような別意味のフィールドは対象外 (L16 は
 * `cnoN`/`savedN` という**トークン参照**だけを見る — slot 番号での指定は参照整合性の対象外)。
 */
function selectorTokens(n: LogicNode): string[] {
    const rec = n as unknown as Record<string, unknown>;
    const out: string[] = [];
    for (const key of ["target", "at", "to"]) {
        const v = rec[key];
        if (typeof v === "string") out.push(v);
    }
    return out;
}

function anySelectorToken(nodes: LogicNode[], predicate: (token: string) => boolean): boolean {
    return firstSelectorToken(nodes, predicate) !== null;
}

/** 条件に合う最初のセレクタ値を返す (警告文に実際の値を出して、同じ文言の重複を避けるため) */
function firstSelectorToken(nodes: LogicNode[], predicate: (token: string) => boolean): string | null {
    let found: string | null = null;
    forEachNode(nodes, (n) => {
        if (found !== null) return;
        for (const t of selectorTokens(n)) {
            if (predicate(t)) {
                found = t;
                return;
            }
        }
    });
    return found;
}

/** 全 rule を横断して「生成/保存側」の slot 番号を集める (L15/L16 は rule をまたいで解決する) */
function collectProvidedSlots(logic: RoleLogic, op: LogicNode["op"]): ReadonlySet<number> {
    const slots = new Set<number>();
    for (const rule of logic.rules) {
        forEachNode(rule.do, (n) => {
            if (n.op === op && "slot" in n && typeof n.slot === "number") slots.add(n.slot);
        });
    }
    return slots;
}

function tokenSlot(token: string, prefix: string): number | null {
    if (!token.startsWith(prefix)) return null;
    const n = Number(token.slice(prefix.length));
    return Number.isInteger(n) ? n : null;
}

/** L17: 訪問順で wait より後に現れる cancel_attack (同期プロローグを抜けた後は no-op) */
function hasCancelAttackAfterWait(nodes: LogicNode[]): boolean {
    let waited = false;
    let violation = false;
    forEachNode(nodes, (n) => {
        if (n.op === "wait") waited = true;
        else if (n.op === "cancel_attack" && waited) violation = true;
    });
    return violation;
}

/** L19 (Wave 2): L17 の兄弟 — 訪問順で wait より後に現れる cancel_vote */
function hasCancelVoteAfterWait(nodes: LogicNode[]): boolean {
    let waited = false;
    let violation = false;
    forEachNode(nodes, (n) => {
        if (n.op === "wait") waited = true;
        else if (n.op === "cancel_vote" && waited) violation = true;
    });
    return violation;
}

/**
 * L21 (2026-08-14): L17/L19 の兄弟 — 訪問順で wait より後に現れる exile。
 *
 * exile は EkrLogicOpcodes.Exile のフェーズゲート (MeetingHud 不在 / 追放演出中 / state が
 * Results・Proceeding) で弾かれる。wait を挟むと、その間に全員の投票が揃って Results へ遷移し
 * (CheckForEndVoting は投票受信ごとに走る)、再開時には既に手遅れというケースが構造的に起こる
 * — BUG-20260813-04 の実機2回とも追放されなかった件がこれ。弾かれても 1会議1回の枠は消費
 * しない (TryConsumeExile はゲートの後) ので、作者からは「無言で何も起きない」だけに見える。
 *
 * vote_block / vote_swap は対象にしない: どちらも「予約」を置くだけで実際に読まれるのは集計時
 * なので、Voting 中に wait を挟んでも予約さえ間に合えば成立する (待ってから撃つのが正当な
 * 使い方になり得る)。cancel_vote は L19 が既に見ている。
 */
function hasExileAfterWait(nodes: LogicNode[]): boolean {
    let waited = false;
    let violation = false;
    forEachNode(nodes, (n) => {
        if (n.op === "wait") waited = true;
        else if (n.op === "exile" && waited) violation = true;
    });
    return violation;
}

// L18 (Wave 2): 会議専用 op (vote_block/vote_swap/exile) の配置ヒント対象イベント。
// cancel_vote はここに含めない (roledef.ts の validateRoleLogic が on_meeting_vote 以外を
// 構造的に reject するため、リンタで重ねて警告する必要がない)。
const MEETING_ONLY_LINT_WHENS: ReadonlySet<string> = new Set(["on_meeting_start", "on_meeting_vote", "on_meeting_pick"]);
const MEETING_ONLY_LINT_OPS: readonly LogicNode["op"][] = ["vote_block", "vote_swap", "exile"];

function makeWarning(rule: LintRuleId, ruleIndex: number, when: string, message: string, suggestion: string): LintWarning {
    return { rule, ruleIndex, when, message, suggestion };
}

/**
 * 検証済みの RoleLogic に対して spec §6 の 21 ルール (v1.2 で L11/L12、v1.3 で L13、Wave 1 で
 * L14〜L17、Wave 2 で L18〜L20、2026-08-14 に L21 追加) を静的検査する。ブロックの組み方に対するヒントであり、
 * export 自体は妨げない (呼び出し元は結果を警告フッタに表示するだけ)。
 */
export function lintRoleLogic(logic: RoleLogic): LintWarning[] {
    const warnings: LintWarning[] = [];

    // L15/L16 は rule をまたいで解決する (「どの rule にも無い」が条件 — spec §6)。
    const savedMarkers = collectProvidedSlots(logic, "marker_save");
    const spawnedCnoSlots = new Set([
        ...collectProvidedSlots(logic, "cno_spawn"),
        ...collectProvidedSlots(logic, "dummy_spawn"),
    ]);
    const rememberedSlots = collectProvidedSlots(logic, "remember");

    logic.rules.forEach((rule: LogicRule, ruleIndex: number) => {
        if (rule.when === "on_second") {
            // v1.1: dummy_spawn も cno_spawn と同じく「毎秒出し続ける」誤用の対象 (文言は流用)。
            if (hasOp(rule.do, "cno_spawn") || hasOp(rule.do, "dummy_spawn")) {
                warnings.push(makeWarning(
                    "L1", ruleIndex, rule.when,
                    "「毎秒くりかえす」の中でオブジェクトを出しています。",
                    "出すのは1回にして、「毎秒くりかえす」では「動かす」を使おう。",
                ));
            }
            if (hasOp(rule.do, "teleport")) {
                warnings.push(makeWarning(
                    "L3", ruleIndex, rule.when,
                    "「毎秒くりかえす」の中でワープしています。",
                    "ワープは「ここぞ」で1回大きく。毎秒だとワープ予算が切れて他の能力まで止まるよ。",
                ));
            }
            if (hasOp(rule.do, "notify")) {
                warnings.push(makeWarning(
                    "L4", ruleIndex, rule.when,
                    "「毎秒くりかえす」の中でお知らせを出しています。",
                    "お知らせは1秒1回までしか出ないよ。",
                ));
            }
            if (hasOp(rule.do, "kill")) {
                warnings.push(makeWarning(
                    "L5", ruleIndex, rule.when,
                    "「毎秒くりかえす」の中でキルしています。",
                    "毎秒キルはゲームがすぐ終わっちゃうよ (本当にやりたいか確認してね)。",
                ));
            }
            if (hasOp(rule.do, "cno_show")) {
                warnings.push(makeWarning(
                    "L6", ruleIndex, rule.when,
                    "「毎秒くりかえす」の中で見せる相手を切り替えています。",
                    "見せる相手の切替は3秒に1回まで。毎秒だと切替が効かないよ。",
                ));
            }
            if (sumWaitSeconds(rule.do) >= 1) {
                warnings.push(makeWarning(
                    "L7", ruleIndex, rule.when,
                    "「毎秒くりかえす」の中で合計1秒以上待っています。",
                    "毎秒新しく始まるのに前のが終わらなくて、たまった分が他のイベントまで止めちゃうよ。長く待つのは「ゲームが始まったとき」などの1回きりのイベントにしよう。",
                ));
            }
            // L11 (v1.2): L3 (teleport) の兄弟 — teleport_other/portal_place も同じワープ予算を消費する。
            if (hasOp(rule.do, "teleport_other") || hasOp(rule.do, "portal_place")) {
                warnings.push(makeWarning(
                    "L11", ruleIndex, rule.when,
                    "「毎秒くりかえす」の中でワープ系の処理をしています。",
                    "ワープ系は「ここぞ」で1回。毎秒だとワープ予算が切れて他の能力まで止まるよ。",
                ));
            }
            // L13 (v1.3): L3/L11 の兄弟 — pull/drag/field。
            if (hasOp(rule.do, "pull") || hasOp(rule.do, "drag") || hasOp(rule.do, "field")) {
                warnings.push(makeWarning(
                    "L13", ruleIndex, rule.when,
                    "「毎秒くりかえす」の中でひっぱる系の処理をしています。",
                    "ひっぱる系は「ここぞ」で1回。毎秒だとワープ予算が切れて他の能力まで止まるよ (drag/field は同時1本なので毎秒かけ直しても効かないよ)。",
                ));
            }
            // L20 (Wave 2): L3/L4 のレート系兄弟 — arrow_show/arrow_mark/inspect/reveal。
            if (hasOp(rule.do, "arrow_show") || hasOp(rule.do, "arrow_mark") || hasOp(rule.do, "inspect") || hasOp(rule.do, "reveal")) {
                warnings.push(makeWarning(
                    "L20", ruleIndex, rule.when,
                    "「毎秒くりかえす」の中で、しらべたり矢印を出したりしています。",
                    "毎秒はやりすぎだよ。1秒に1回までしか効かないよ。",
                ));
            }
        }

        // L9 (v1.1 新設・v1.2 で対象拡大): on_meeting_end 限定。会議明けから10秒間のドロップ窓
        // (spec §5) の静的近似。対象は生成系4兄弟 (cno_spawn/cno_show/dummy_spawn/portal_place)。
        if (rule.when === "on_meeting_end" && hasGenerationOpBeforeElapsed(rule.do, L9_OPS, 10)) {
            warnings.push(makeWarning(
                "L9", ruleIndex, rule.when,
                "「会議が終わったとき」の中で、会議のあとすぐオブジェクトを出そうとしています。",
                "会議のあとすぐは出せないよ。先に「10.5 秒待つ」を入れよう。",
            ));
        }

        // L12 (v1.2・v1.3 で field 追加): on_cno_touch 限定。触れるたびに生成系 op を撃つ誤用の検知。
        if (rule.when === "on_cno_touch"
            && (hasOp(rule.do, "cno_spawn") || hasOp(rule.do, "dummy_spawn") || hasOp(rule.do, "cno_show")
                || hasOp(rule.do, "portal_place") || hasOp(rule.do, "field"))) {
            warnings.push(makeWarning(
                "L12", ruleIndex, rule.when,
                "「オブジェクトにだれかが触れたとき」の中でオブジェクトを出したり切り替えたりしています。",
                "触られるたびに出すのは出しすぎ。1秒に1個までしか出ないから、出すのは別のきっかけにしよう。",
            ));
        }

        // L2: on_second 限定ではない (rule あたり・静的近似 — 制御フローは見ない)
        const spawnCounts = countCnoBySlot(rule.do, "cno_spawn");
        const despawnCounts = countCnoBySlot(rule.do, "cno_despawn");
        for (const [slot, count] of spawnCounts) {
            if (count >= 2 && (despawnCounts.get(slot) ?? 0) === 0) {
                warnings.push(makeWarning(
                    "L2", ruleIndex, rule.when,
                    `オブジェクト(スロット${slot})を消さずに何度も出しています。`,
                    "前のオブジェクトを消してから出そう。",
                ));
            }
        }

        // L8: on_second 限定ではない (slot 不問 — L2 が「同一 slot の上書き」、こちらは「1秒1個レートのドロップ」)
        if (hasRapidConsecutiveOp(rule.do, "cno_spawn", 1)) {
            warnings.push(makeWarning(
                "L8", ruleIndex, rule.when,
                "オブジェクトを間をあけずに続けて出しています。",
                "出せるのは1秒に1個までで、2個目からは出ないよ。間に「1.1 秒待つ」を入れよう。",
            ));
        }

        // L10 (v1.1): on_second 限定ではない (L8 と同様 when 不問)。dummy_spawn 専用の3秒バケット。
        if (hasRapidConsecutiveOp(rule.do, "dummy_spawn", 3)) {
            warnings.push(makeWarning(
                "L10", ruleIndex, rule.when,
                "ダミー人形を間をあけずに続けて出しています。",
                "ダミーは3秒に1体まで、2体目からは出ないよ。間に「3.1 秒待つ」を入れよう。",
            ));
        }

        // L14 (Wave 1): 「あいて」がいないきっかけの中で「あいて」を使っている。
        // 対象は ①セレクタ値 (target/at/to) の "ctx" と ②引数を持たない **ctx 暗黙 op**
        // (`pull`/`drag` — フィールドは無いが実行時は ctx に依存するので、ctx 無しイベント配下では
        // 丸ごと no-op になる。2026-08-11 の三面監査裁定で L14 の対象へ拡張)。
        if (CTXLESS_WHENS.has(rule.when)
            && (anySelectorToken(rule.do, (t) => t === "ctx") || hasOp(rule.do, "pull") || hasOp(rule.do, "drag"))) {
            warnings.push(makeWarning(
                "L14", ruleIndex, rule.when,
                "「あいて」のいないきっかけの中で「あいて」を指定しています。",
                "このきっかけには「あいて」がいないよ。",
            ));
        }

        // L15 (Wave 1): teleport(to:markerN)/teleport_other(to:markerN)/field(at:markerN) の
        // 行き先に対応する marker_save(N) がどの rule にも無い。
        const missingMarker = firstSelectorToken(rule.do, (t) => {
            const slot = tokenSlot(t, "marker");
            return slot !== null && !savedMarkers.has(slot);
        });
        if (missingMarker !== null) {
            warnings.push(makeWarning(
                "L15", ruleIndex, rule.when,
                `まだおぼえていない場所 (マーカー${missingMarker.slice("marker".length)}) を使おうとしています。`,
                "おぼえていない場所へはワープできないよ。先に「いまの場所をおぼえる」を入れよう。",
            ));
        }

        // L16 (Wave 1・L15 と同型): cnoN / savedN の参照に対応する生成 (cno_spawn/dummy_spawn) /
        // 保存 (remember) op がどの rule にも無い。
        const missingRef = firstSelectorToken(rule.do, (t) => {
            const cnoSlot = tokenSlot(t, "cno");
            if (cnoSlot !== null) return !spawnedCnoSlots.has(cnoSlot);
            const savedSlot = tokenSlot(t, "saved");
            return savedSlot !== null && !rememberedSlots.has(savedSlot);
        });
        if (missingRef !== null) {
            const label = missingRef.startsWith("cno")
                ? `まだ出していないオブジェクト${missingRef.slice("cno".length)}`
                : `まだおぼえていない人 (おぼえた人${missingRef.slice("saved".length)})`;
            warnings.push(makeWarning(
                "L16", ruleIndex, rule.when,
                `${label} を指定しています。`,
                "出していないオブジェクト/おぼえていない人は使えないよ。先に「出す」か「おぼえる」を入れよう。",
            ));
        }

        // L17 (Wave 1): on_attacked の同期プロローグは最初の wait で終わる — その後の
        // cancel_attack は no-op (spec §2「同期プロローグ」)。
        if (rule.when === "on_attacked" && hasCancelAttackAfterWait(rule.do)) {
            warnings.push(makeWarning(
                "L17", ruleIndex, rule.when,
                "「秒待つ」のあとで「こうげきをふせぐ」を使っています。",
                "まってから防ぐことはできないよ。ふせぐのは一番はじめに。",
            ));
        }

        // L18 (Wave 2): 会議専用 op (vote_block/vote_swap/exile) が会議系イベント以外の rule 配下。
        if (!MEETING_ONLY_LINT_WHENS.has(rule.when) && MEETING_ONLY_LINT_OPS.some((op) => hasOp(rule.do, op))) {
            warnings.push(makeWarning(
                "L18", ruleIndex, rule.when,
                "会議専用のちからを、会議とは関係ないきっかけの中で使おうとしています。",
                "会議のあいだしか効かないよ。「かいぎで投票したとき」などに置こう。",
            ));
        }

        // L19 (Wave 2): L17 の兄弟 — on_meeting_vote の同期プロローグは最初の wait で終わる。
        if (rule.when === "on_meeting_vote" && hasCancelVoteAfterWait(rule.do)) {
            warnings.push(makeWarning(
                "L19", ruleIndex, rule.when,
                "「秒待つ」のあとで「票をつかわずにえらぶ」を使っています。",
                "まってから取り消すことはできないよ。取り消すのは一番はじめに。",
            ));
        }

        // L21 (2026-08-14): L17/L19 の兄弟 — wait を挟むと会議が終わっていて exile が届かない。
        if (hasExileAfterWait(rule.do)) {
            warnings.push(makeWarning(
                "L21", ruleIndex, rule.when,
                "「秒待つ」のあとで「ついほうする」を使っています。",
                "待っているあいだに会議が終わってしまうと、ついほうは何も起きないよ。ついほうは待たずにすぐ。",
            ));
        }

        // L16 拡張 (Wave 2): vote_swap の暗黙 saved1/saved2 参照。vote_swap は target フィールドを
        // 持たないため selectorTokens 経由の一般チェックには乗らない (pull/drag の L14 拡張と同型 —
        // 引数を持たない ctx/saved 暗黙 op は op 名で別途見る)。
        if (hasOp(rule.do, "vote_swap") && !(rememberedSlots.has(1) && rememberedSlots.has(2))) {
            warnings.push(makeWarning(
                "L16", ruleIndex, rule.when,
                "「おぼえた人1と2の票をいれかえる」を使っていますが、1と2の両方をおぼえていません。",
                "おぼえていない人の票は入れかえられないよ。先に「1をおぼえる」と「2をおぼえる」の両方を入れよう。",
            ));
        }
    });

    return warnings;
}

/** 警告フッタ表示用の1行サマリ (「⚠️ この組み方だと公式サーバーで蹴られたり動かなかったりするかも…」の後に続ける) */
export function formatLintWarning(w: LintWarning): string {
    return `${w.message} ${w.suggestion}`;
}
