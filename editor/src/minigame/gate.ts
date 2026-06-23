// Crew Run のゲート演算 (純ロジック・PIXI 非依存)。
// テストから PIXI を巻き込まないよう crewrun.ts から分離している。

export const MAX_CREW = 9999; // 人数の上限 (オーバーフロー防止)

export type GateKind = "+" | "-" | "*" | "/";

export interface GateOp {
    kind: GateKind;
    value: number;
}

/** 群衆人数 count にゲート op を適用した結果を返す。0 未満は 0、上限 MAX_CREW でクランプ。 */
export function applyGate(count: number, op: GateOp): number {
    let n: number;
    switch (op.kind) {
        case "+":
            n = count + op.value;
            break;
        case "-":
            n = count - op.value;
            break;
        case "*":
            n = count * op.value;
            break;
        case "/":
            n = Math.floor(count / op.value);
            break;
    }
    return Math.max(0, Math.min(MAX_CREW, Math.round(n)));
}

/** ゲート op を人が読める短い文字列にする ("x2" / "+10" / "-3" / "÷2")。 */
export function gateLabel(op: GateOp): string {
    switch (op.kind) {
        case "+":
            return `+${op.value}`;
        case "-":
            return `-${op.value}`;
        case "*":
            return `x${op.value}`;
        case "/":
            return `÷${op.value}`;
    }
}
