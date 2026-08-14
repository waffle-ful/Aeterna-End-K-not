// 役職メーカー (role-maker.ts) の陣営セレクタ関連の純粋ロジックのみを対象にしたテスト。
// role-maker.ts はトップレベルで DOM/localStorage に触れず、関数の中でのみ document.getElementById 等を
// 呼ぶ (main.ts との接点は initRoleMaker() の呼び出し1回だけ)。そのため normalizeTeamDraft のような
// フォーム値の下書き復元ヘルパーは、この environment: "node" の vitest からでも import して直接呼べる —
// DOM を必要とする wire()/readForm()/writeForm() 等は対象外 (dev サーバでの目視確認に委ねる)。
//
// docs/ekn-r2-contract.md §1 (R2): team は crewmate/impostor/neutral の3値。
// normalizeTeamDraft は「壊れた localStorage 下書き/読込値でもフォーム自体は必ず開ける」ための
// フォーム層専用のフォールバック実装 (roledef.ts の validateEkrDefinition は不正な team を
// reject するだけでフォールバックしない — 契約に手を入れずフォーム層だけで吸収する)。

import { describe, expect, it } from "vitest";
import { normalizeTeamDraft } from "../src/logic/role-maker";
import { SUPPORTED_TEAM, SUPPORTED_TEAMS } from "../src/roledef";

describe("normalizeTeamDraft (role-maker.ts のフォーム下書き復元)", () => {
    it("crewmate/impostor/neutral の3値はそのまま通す", () => {
        for (const team of SUPPORTED_TEAMS) {
            expect(normalizeTeamDraft(team)).toBe(team);
        }
    });

    it("不正な team 文字列 (未対応の陣営名・大文字小文字違い含む) は既定 crewmate へフォールバックする", () => {
        for (const bad of ["madmate", "coven", "CREWMATE", "Impostor", " neutral", ""]) {
            expect(normalizeTeamDraft(bad)).toBe(SUPPORTED_TEAM);
        }
    });

    it("文字列以外 (undefined/null/数値/配列/オブジェクト) は既定 crewmate へフォールバックする", () => {
        for (const bad of [undefined, null, 5, ["crewmate"], { team: "crewmate" }]) {
            expect(normalizeTeamDraft(bad)).toBe(SUPPORTED_TEAM);
        }
    });
});
