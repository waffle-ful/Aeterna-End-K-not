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

import type { LogicNode, LogicRule, RoleLogic } from "../roledef";

export type LintRuleId = "L1" | "L2" | "L3" | "L4" | "L5" | "L6" | "L7" | "L8" | "L9" | "L10";

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

// L9: L8/L10 とは起点が異なる — 「前の dummy_spawn」ではなく「ルール開始 (=会議終了の瞬間)」を
// elapsed=0 とし、一度も 0 にリセットしない訪問順の累積 wait を追う。dummy_spawn に出会った時点で
// まだ thresholdSeconds に届いていなければ違反 (spec §5 の「会議明けから10秒間はドロップ」の
// 静的近似)。if 分岐は L7 と同じく合算 (forEachNode が then/else 両方を訪れるため、分岐の択一は
// 見ない — 「wait, dummy_spawn」のような直列の前後関係だけを区別する)。
function hasDummySpawnBeforeElapsed(nodes: LogicNode[], thresholdSeconds: number): boolean {
    let elapsed = 0;
    let violation = false;
    forEachNode(nodes, (n) => {
        if (n.op === "wait") elapsed += n.seconds;
        else if (n.op === "dummy_spawn" && elapsed < thresholdSeconds) violation = true;
    });
    return violation;
}

function makeWarning(rule: LintRuleId, ruleIndex: number, when: string, message: string, suggestion: string): LintWarning {
    return { rule, ruleIndex, when, message, suggestion };
}

/**
 * 検証済みの RoleLogic に対して spec §6 の 10 ルールを静的検査する。ブロックの組み方に対する
 * ヒントであり、export 自体は妨げない (呼び出し元は結果を警告フッタに表示するだけ)。
 */
export function lintRoleLogic(logic: RoleLogic): LintWarning[] {
    const warnings: LintWarning[] = [];

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
        }

        // L9 (v1.1): on_meeting_end 限定。会議明けから10秒間のドロップ窓 (spec §5) の静的近似。
        if (rule.when === "on_meeting_end" && hasDummySpawnBeforeElapsed(rule.do, 10)) {
            warnings.push(makeWarning(
                "L9", ruleIndex, rule.when,
                "「会議が終わったとき」の中で、会議のあとすぐダミー人形を出そうとしています。",
                "会議のあとすぐはダミーを出せないよ。先に「10.5 秒待つ」を入れよう。",
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
    });

    return warnings;
}

/** 警告フッタ表示用の1行サマリ (「⚠️ この組み方だと公式サーバーで蹴られたり動かなかったりするかも…」の後に続ける) */
export function formatLintWarning(w: LintWarning): string {
    return `${w.message} ${w.suggestion}`;
}
