// docs/ekr-logic-spec.md §6 — エディタ AST リンター (二層防御のヒント層)。
// 「ブロックは組める・書き出しもできる」が前提 — ここで見つけた問題は *警告* であり、
// roledef.ts の validateRoleLogic (契約違反=文書全体 reject) とは別物。呼び出し元は既に
// 検証済みの RoleLogic (roledef.ts の型) だけを渡すこと — このモジュール自身は妥当性検証をしない。
//
// 静的近似であり制御フロー解析はしない (spec §6 L2 の注記どおり)。例えば if/else の両方の枝で
// 同じ slot に cno_spawn していても、実行時にはどちらか一方しか通らない可能性があるが、
// このリンターはその区別をせず単純にノードの出現数を数える (蓄積 memory の kick 知見を
// ヒントとして出すのが目的であり、正確な静的解析エンジンではない)。

import type { LogicNode, LogicRule, RoleLogic } from "../roledef";

export type LintRuleId = "L1" | "L2" | "L3" | "L4" | "L5" | "L6" | "L7" | "L8";

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

// L8: 訪問順 (forEachNode と同じ depth-first) を疑似実行列とみなし、cno_spawn 間の累積 wait 秒を追う。
// 初回 spawn の前は Infinity (レートバケットに初期トークンがあるため必ず通る)。
function hasRapidConsecutiveSpawns(nodes: LogicNode[]): boolean {
    let sinceLastSpawn = Infinity;
    let violation = false;
    forEachNode(nodes, (n) => {
        if (n.op === "wait") sinceLastSpawn += n.seconds;
        else if (n.op === "cno_spawn") {
            if (sinceLastSpawn < 1) violation = true;
            sinceLastSpawn = 0;
        }
    });
    return violation;
}

function makeWarning(rule: LintRuleId, ruleIndex: number, when: string, message: string, suggestion: string): LintWarning {
    return { rule, ruleIndex, when, message, suggestion };
}

/**
 * 検証済みの RoleLogic に対して spec §6 の 8 ルールを静的検査する。ブロックの組み方に対する
 * ヒントであり、export 自体は妨げない (呼び出し元は結果を警告フッタに表示するだけ)。
 */
export function lintRoleLogic(logic: RoleLogic): LintWarning[] {
    const warnings: LintWarning[] = [];

    logic.rules.forEach((rule: LogicRule, ruleIndex: number) => {
        if (rule.when === "on_second") {
            if (hasOp(rule.do, "cno_spawn")) {
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
        if (hasRapidConsecutiveSpawns(rule.do)) {
            warnings.push(makeWarning(
                "L8", ruleIndex, rule.when,
                "オブジェクトを間をあけずに続けて出しています。",
                "出せるのは1秒に1個までで、2個目からは出ないよ。間に「1.1 秒待つ」を入れよう。",
            ));
        }
    });

    return warnings;
}

/** 警告フッタ表示用の1行サマリ (「⚠️ この組み方だと公式サーバーで蹴られたり動かなかったりするかも…」の後に続ける) */
export function formatLintWarning(w: LintWarning): string {
    return `${w.message} ${w.suggestion}`;
}
