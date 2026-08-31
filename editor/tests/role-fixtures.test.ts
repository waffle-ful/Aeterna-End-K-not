// logic 付き .ekrole.json の golden fixture (docs/ekr-logic-spec.md §1〜§6 の共有検証資材)。
// AST 深さ不一致のような実装差分は、共有の実物 .ekrole.json が無いと自動検出できない —
// このテストはその穴を塞ぐ。fixtures/ のファイルは vitest 専用ではなく、そのまま
// Documents/EndKnot/EKRoles/ に置いて実機 /role import テストにも使う想定 (現実的な日本語の
// 役職名・flavor text で書く)。そのため fixture 自体を書き換えるときはこのテストと変更内容を
// 揃えること (golden.ts の「仕様書から逐語コピー・改変禁止」ほど厳格な凍結ではないが、
// 意図せず壊すと実機テスト側の資材も一緒に壊れる)。

import { describe, expect, it } from "vitest";
import fullCourseRaw from "./fixtures/role-full-course.ekrole.json?raw";
import cnoShowcaseRaw from "./fixtures/role-cno-showcase.ekrole.json?raw";
import dummyShowcaseRaw from "./fixtures/role-dummy-showcase.ekrole.json?raw";
import yukidamaShowcaseRaw from "./fixtures/role-yukidama-showcase.ekrole.json?raw";
import koorinotamaShowcaseRaw from "./fixtures/role-koorinotama-showcase.ekrole.json?raw";
import beamShowcaseRaw from "./fixtures/role-beam-showcase.ekrole.json?raw";
import collectorShowcaseRaw from "./fixtures/role-collector-showcase.ekrole.json?raw";
import parasiteShowcaseRaw from "./fixtures/role-parasite-showcase.ekrole.json?raw";
import { ROLECODE_PREFIX, decodeRoleCode, encodeRoleCode } from "../src/rolecode";
import { LOGIC_WHEN_VALUES, validateEkrDefinition, type LogicNode, type LogicWhen } from "../src/roledef";
import { lintRoleLogic } from "../src/logic/lint-role";

function collectOps(nodes: LogicNode[], into: Set<string>): void {
    for (const n of nodes) {
        into.add(n.op);
        if (n.op === "if") {
            collectOps(n.then, into);
            if (n.else) collectOps(n.else, into);
        }
    }
}

describe("golden fixture: role-full-course.ekrole.json (10イベント・主要opcode・式ネスト・変数を網羅)", () => {
    it("validate に合格する", () => {
        const parsed = JSON.parse(fullCourseRaw);
        const result = validateEkrDefinition(parsed);
        expect(result.ok).toBe(true);
    });

    // plan §7 Tier 1 #2: 説明文2欄。C# 側 (EkrDefinitionTests.FullCourseFixture_ExposesDescriptions)
    // が同じファイルの同じ値を読むので、片側だけ実装が抜けるとどちらかが落ちる。
    it("説明文2欄を保持する (詳細文の改行はそのまま残る)", () => {
        const parsed = JSON.parse(fullCourseRaw);
        const result = validateEkrDefinition(parsed);
        if (!result.ok) throw new Error(result.error);

        expect(result.def.description).toBe("影を渡り歩き、触れた者の運命を書き換える");
        expect(result.def.descriptionLong).toContain("\n");
        expect(result.def.descriptionLong?.startsWith("影を渡り歩く役職です。")).toBe(true);
    });

    // Wave 3 (docs/ekn-wave3-contract.md §3/§4 2026-08-14): progress/hostOptions。
    // C# 側 (EkrDefinitionTests) が同じファイルの同じ値を読むので、片側だけ実装が抜けるとどちらかが落ちる。
    it("progress.text と hostOptions を保持する", () => {
        const parsed = JSON.parse(fullCourseRaw);
        const result = validateEkrDefinition(parsed);
        if (!result.ok) throw new Error(result.error);

        expect(result.def.progress).toEqual({ text: "うらみ{うらみ}" });
        expect(result.def.hostOptions).toEqual([
            { key: "shield.count", label: "かげのまもり" },
            { key: "killCooldown", label: "キルクールダウン" },
            { key: "var:うらみ", label: "はじめのうらみ", min: 0, max: 20 },
        ]);
    });

    it("24 種類のイベントを1回ずつカバーしている (Wave 4 で on_near/on_far/on_room_enter/on_room_exit/on_linked_death・Wave 6 で on_sabotage/on_revive を追加)", () => {
        const parsed = JSON.parse(fullCourseRaw);
        const result = validateEkrDefinition(parsed);
        if (!result.ok) throw new Error(result.error);
        if (!result.def.logic) throw new Error("fixture は logic を持つ前提");

        const whens = result.def.logic.rules.map((r) => r.when);
        expect(new Set(whens).size).toBe(LOGIC_WHEN_VALUES.length);
        for (const w of LOGIC_WHEN_VALUES) {
            expect(whens).toContain(w as LogicWhen);
        }
    });

    it("制御 op (if/wait/stop/var_set/var_add) とアクション op (v1.2 で marker_save/teleport_other・Wave 4 で link/unlink/recruit を追加) を全て少なくとも1回使っている", () => {
        const parsed = JSON.parse(fullCourseRaw);
        const result = validateEkrDefinition(parsed);
        if (!result.ok) throw new Error(result.error);
        if (!result.def.logic) throw new Error("fixture は logic を持つ前提");

        const ops = new Set<string>();
        for (const rule of result.def.logic.rules) collectOps(rule.do, ops);

        const expectedOps = [
            "if", "wait", "stop", "var_set", "var_add",
            "notify", "teleport", "kill", "set_kill_cooldown", "speed",
            "cno_spawn", "cno_move", "cno_despawn", "cno_show",
            "marker_save", "teleport_other",
            // Wave 4 (docs/ekn-wave4-contract.md §3/§4): リンクと変換の3 op。
            "link", "unlink", "recruit",
            // Wave 5 (docs/ekn-wave5-contract.md §1): 持続効果。
            "effect_give",
            // Wave 6 (docs/ekn-wave6-contract.md §1): とばす。
            "cno_launch",
            // Wave 7 (docs/ekn-wave7-contract.md §2): 便乗勝ち。win (即勝ち) はこの fixture には
            // 入れられない (crewmate 文書は検証 reject — win の C# パース網羅は
            // role-collector-showcase.ekrole.json が担う)。
            "win_join",
        ];
        for (const op of expectedOps) {
            expect(ops.has(op), `op "${op}" が fixture 内で使われていない`).toBe(true);
        }
    });

    // Wave 1 (spec §1.1): passives の全キーを実物の .ekrole.json で1度ずつ使う
    // (C# 側の検証と突き合わせる共有資材にするため — 型/レンジの実装差分はここで露見する)。
    // R2 (docs/ekn-r2-contract.md §4): disguise を追加して7キーになった。
    it("passives の7キーすべてを使っている (検証を通り、そのまま保持される)", () => {
        const parsed = JSON.parse(fullCourseRaw);
        const result = validateEkrDefinition(parsed);
        if (!result.ok) throw new Error(result.error);
        expect(result.def.passives).toEqual({
            speedMult: 1.2,
            killDistance: "short",
            shield: { count: 2 },
            corpse: "noReport",
            voteWeight: 2,
            doom: { seconds: 300 },
            disguise: { team: "neutral" },
        });
    });

    // R2 (契約 §3b): on_attacked の kind / on_death の cause も共有 fixture に載せて
    // TS↔C# の drift 検出網に入れる。
    it("Wave 3 の新3イベント (on_var/on_alive_count/on_vent_exit) の rule 形が保持される", () => {
        const parsed = JSON.parse(fullCourseRaw);
        const result = validateEkrDefinition(parsed);
        if (!result.ok) throw new Error(result.error);
        const rules = result.def.logic?.rules ?? [];

        const onVar = rules.find((r) => r.when === "on_var");
        expect(onVar?.var).toBe("うらみ");
        expect(onVar?.cmp).toBe("ge");
        expect(onVar?.value).toBe(7);

        const onAliveCount = rules.find((r) => r.when === "on_alive_count");
        expect(onAliveCount?.var).toBeUndefined();
        expect(onAliveCount?.cmp).toBe("le");
        expect(onAliveCount?.value).toBe(3);

        const onVentExit = rules.find((r) => r.when === "on_vent_exit");
        expect(onVentExit?.do.length).toBeGreaterThan(0);
    });

    // Wave 5 (docs/ekn-wave5-contract.md §1/§2): 持続効果と変換先スロット指名。C# 側
    // (EkrDefinitionTests.FullCourseFixture_ExposesWave5Vocabulary) が同じファイルの同じ値を読むので、
    // 片側だけ実装が抜けるとどちらかが落ちる。
    it("effect_give の4種と recruit.slot が保持される (kind 別の seconds 上限も含む)", () => {
        const parsed = JSON.parse(fullCourseRaw);
        const result = validateEkrDefinition(parsed);
        if (!result.ok) throw new Error(result.error);
        const rules = result.def.logic?.rules ?? [];

        const effects: { target: string; kind: string; seconds: number }[] = [];
        for (const rule of rules) {
            for (const n of rule.do) {
                if (n.op === "effect_give") effects.push({ target: n.target, kind: n.kind, seconds: n.seconds });
            }
        }

        // 4 kind すべてを1回以上 (movement 3種 + vision 1種)。
        expect(new Set(effects.map((e) => e.kind))).toEqual(new Set(["slow", "blind", "freeze", "haste"]));
        // freeze は上限 10 秒 (契約 §1) — fixture は境界を割る値で持つ。
        const freeze = effects.find((e) => e.kind === "freeze");
        expect(freeze?.seconds).toBe(8);
        expect(freeze?.target).toBe("nearest");
        // linked セレクタと self がどちらも受理されること (§1 の target 受理集合 = 単数セレクタ全種)。
        expect(effects.some((e) => e.target === "linked")).toBe(true);
        expect(effects.some((e) => e.target === "self")).toBe(true);

        // recruit.slot: 指名あり (§2)。省略時にフィールドを持たないことは compile-role 側で検証する。
        const recruit = rules.flatMap((r) => r.do).find((n) => n.op === "recruit");
        expect(recruit && "slot" in recruit ? recruit.slot : undefined).toBe(3);
    });

    it("on_attacked の kind と on_death の cause が保持される", () => {
        const parsed = JSON.parse(fullCourseRaw);
        const result = validateEkrDefinition(parsed);
        if (!result.ok) throw new Error(result.error);
        const rules = result.def.logic?.rules ?? [];
        expect(rules.find((r) => r.when === "on_attacked")?.kind).toBe("force");
        expect(rules.find((r) => r.when === "on_death")?.cause).toBe("poison-curse");
    });

    // Wave 4 (docs/ekn-wave4-contract.md §1〜§3): 近接/部屋/リンク死の rule 形。
    // C# 側 (EkrDefinitionTests.FullCourseFixture_ExposesWave4Triggers) が同じファイルの同じ値を
    // 読むので、片側だけ実装が抜けるとどちらかが落ちる。
    it("Wave 4 の新5イベントの rule 形が保持される (radius/who/cause の付着規則込み)", () => {
        const parsed = JSON.parse(fullCourseRaw);
        const result = validateEkrDefinition(parsed);
        if (!result.ok) throw new Error(result.error);
        const rules = result.def.logic?.rules ?? [];

        // on_near は radius 必須・who 任意 (省略 = anyone。省略キーは AST に載せない)
        const onNear = rules.find((r) => r.when === "on_near");
        expect(onNear?.radius).toBe("small");
        expect(onNear?.who).toBeUndefined();

        // on_far は radius/who とも必須 (who は anyone 不可)
        const onFar = rules.find((r) => r.when === "on_far");
        expect(onFar?.radius).toBe("medium");
        expect(onFar?.who).toBe("linked");

        // 部屋2種は追加フィールドなし (ctx 無しイベント)
        for (const when of ["on_room_enter", "on_room_exit"] as const) {
            const room = rules.find((r) => r.when === when);
            expect(room?.radius).toBeUndefined();
            expect(room?.who).toBeUndefined();
            expect((room?.do.length ?? 0) > 0).toBe(true);
        }

        // on_linked_death は cause を任意で受ける (on_death と同じ8バケット)
        expect(rules.find((r) => r.when === "on_linked_death")?.cause).toBe("kill");
    });

    // Wave 6 (docs/ekn-wave6-contract.md §1〜§3): とばす + 残イベント2種。C# 側 (EkrDefinitionTests) が
    // 同じファイルの同じ値を読む想定なので、片側だけ実装が抜けるとどちらかが落ちる。
    it("cno_launch (slot/dir・speed 省略=medium) と on_sabotage/on_revive の rule 形が保持される", () => {
        const parsed = JSON.parse(fullCourseRaw);
        const result = validateEkrDefinition(parsed);
        if (!result.ok) throw new Error(result.error);
        const rules = result.def.logic?.rules ?? [];

        let launch: Extract<LogicNode, { op: "cno_launch" }> | undefined;
        for (const rule of rules) {
            for (const n of rule.do) {
                if (n.op === "cno_launch") launch = n;
            }
        }
        if (!launch) throw new Error("cno_launch が fixture 内にある前提");
        expect(launch.slot).toBe(2);
        expect(launch.dir).toBe("move");
        expect("speed" in launch).toBe(false);

        const onSabotage = rules.find((r) => r.when === "on_sabotage");
        expect(onSabotage?.do.length).toBeGreaterThan(0);

        const onRevive = rules.find((r) => r.when === "on_revive");
        expect(onRevive?.do.length).toBeGreaterThan(0);
    });

    it("リンター (spec §6・Wave 3 で L24/L25 含む) は警告0件 — golden fixture は模範的な組み方で書く", () => {
        const parsed = JSON.parse(fullCourseRaw);
        const result = validateEkrDefinition(parsed);
        if (!result.ok) throw new Error(result.error);
        if (!result.def.logic) throw new Error("fixture は logic を持つ前提");
        expect(lintRoleLogic(result.def.logic, result.def.progress?.text)).toEqual([]);
    });

    it("rolecode (EKR1.) のエンコード→デコード ラウンドトリップで AST が deep-equal になる", () => {
        const parsed = JSON.parse(fullCourseRaw);
        const validated = validateEkrDefinition(parsed);
        if (!validated.ok) throw new Error(validated.error);

        const code = encodeRoleCode(JSON.stringify(validated.def));
        expect(code.startsWith(ROLECODE_PREFIX)).toBe(true);

        const roundTripped = validateEkrDefinition(JSON.parse(decodeRoleCode(code)));
        if (!roundTripped.ok) throw new Error(roundTripped.error);

        expect(roundTripped.def).toEqual(validated.def);
        expect(roundTripped.def.logic).toEqual(validated.def.logic);

        // 一度検証済みになった形は不動点 (再エンコードしても同じバイト列になる) — 実機で
        // このファイルを import → エディタで再コピー → 出てきたコードを diff、が意図どおりに
        // 「差分なし」になることを保証する (validated.def 同士の比較だけでは検知できない)。
        expect(encodeRoleCode(JSON.stringify(roundTripped.def))).toBe(code);
    });
});

describe("golden fixture: role-cno-showcase.ekrole.json (CNO 演出中心・実機テスト用)", () => {
    it("validate に合格する", () => {
        const parsed = JSON.parse(cnoShowcaseRaw);
        const result = validateEkrDefinition(parsed);
        expect(result.ok).toBe(true);
    });

    it("cno_spawn/cno_move/cno_despawn/cno_show を使っている (CNO 演出中心)", () => {
        const parsed = JSON.parse(cnoShowcaseRaw);
        const result = validateEkrDefinition(parsed);
        if (!result.ok) throw new Error(result.error);
        if (!result.def.logic) throw new Error("fixture は logic を持つ前提");

        const ops = new Set<string>();
        for (const rule of result.def.logic.rules) collectOps(rule.do, ops);
        expect(ops.has("cno_spawn")).toBe(true);
        expect(ops.has("cno_move")).toBe(true);
        expect(ops.has("cno_despawn")).toBe(true);
        expect(ops.has("cno_show")).toBe(true);
    });

    it("cno_spawn.text の <灯> は検証時に全角 〈灯〉 へサニタイズされる (spec §3 TMP タグ注入防御・実機で目視確認できる)", () => {
        const parsed = JSON.parse(cnoShowcaseRaw);
        // 生ファイル側はまだ半角 <> のまま (人間がエディタで書く/読む生データはこの形)。
        // spawn レート (≤1/秒) に合わせて wait ノードが挟まっているため index 固定でなく slot で探す。
        const rawSlot3 = parsed.logic.rules[0].do.find((n: { op: string; slot?: number }) => n.op === "cno_spawn" && n.slot === 3);
        expect(rawSlot3.text).toBe("<灯>");

        const result = validateEkrDefinition(parsed);
        if (!result.ok) throw new Error(result.error);
        if (!result.def.logic) throw new Error("fixture は logic を持つ前提");
        const spawnSlot3 = result.def.logic.rules[0].do.find(n => n.op === "cno_spawn" && n.slot === 3);
        if (spawnSlot3?.op !== "cno_spawn") throw new Error("slot3 の cno_spawn が見つかる前提");
        expect(spawnSlot3.text).toBe("〈灯〉");
    });

    it("size の境界値 (1 と 12) をどちらも使っている", () => {
        const parsed = JSON.parse(cnoShowcaseRaw);
        const result = validateEkrDefinition(parsed);
        if (!result.ok) throw new Error(result.error);
        if (!result.def.logic) throw new Error("fixture は logic を持つ前提");

        const sizes: number[] = [];
        for (const rule of result.def.logic.rules) {
            for (const n of rule.do) {
                if (n.op === "cno_spawn") sizes.push(n.size);
            }
        }
        expect(sizes).toContain(1);
        expect(sizes).toContain(12);
    });

    it("リンター (spec §6) は警告0件", () => {
        const parsed = JSON.parse(cnoShowcaseRaw);
        const result = validateEkrDefinition(parsed);
        if (!result.ok) throw new Error(result.error);
        if (!result.def.logic) throw new Error("fixture は logic を持つ前提");
        expect(lintRoleLogic(result.def.logic)).toEqual([]);
    });

    it("rolecode (EKR1.) のエンコード→デコード ラウンドトリップで AST が deep-equal になる", () => {
        const parsed = JSON.parse(cnoShowcaseRaw);
        const validated = validateEkrDefinition(parsed);
        if (!validated.ok) throw new Error(validated.error);

        const code = encodeRoleCode(JSON.stringify(validated.def));
        const roundTripped = validateEkrDefinition(JSON.parse(decodeRoleCode(code)));
        if (!roundTripped.ok) throw new Error(roundTripped.error);

        expect(roundTripped.def).toEqual(validated.def);
        expect(roundTripped.def.logic).toEqual(validated.def.logic);

        // このファイルは検証前の生データに半角 <> が残っている (上のテスト参照) — 一度検証を
        // 通した後の形が不動点であること (サニタイズが二重適用されてもズレない) を確認する。
        expect(encodeRoleCode(JSON.stringify(roundTripped.def))).toBe(code);
    });
});

describe("golden fixture: role-dummy-showcase.ekrole.json (v1.1 dummy_spawn/corpse_spawn・実機テスト用)", () => {
    it("validate に合格する", () => {
        const parsed = JSON.parse(dummyShowcaseRaw);
        const result = validateEkrDefinition(parsed);
        expect(result.ok).toBe(true);
    });

    it("dummy_spawn/corpse_spawn の列挙値を全てカバーしている (slot 1/2・killable 両値・color 両値・at 両値)", () => {
        const parsed = JSON.parse(dummyShowcaseRaw);
        const result = validateEkrDefinition(parsed);
        if (!result.ok) throw new Error(result.error);
        if (!result.def.logic) throw new Error("fixture は logic を持つ前提");

        const dummies: Extract<LogicNode, { op: "dummy_spawn" }>[] = [];
        const corpses: Extract<LogicNode, { op: "corpse_spawn" }>[] = [];
        for (const rule of result.def.logic.rules) {
            for (const n of rule.do) {
                if (n.op === "dummy_spawn") dummies.push(n);
                if (n.op === "corpse_spawn") corpses.push(n);
            }
        }

        expect(dummies.map(d => d.slot)).toEqual(expect.arrayContaining([1, 2]));
        expect(dummies.map(d => d.killable)).toEqual(expect.arrayContaining([true, false]));
        expect(corpses.map(c => c.color)).toEqual(expect.arrayContaining(["random", "self"]));
        expect(corpses.map(c => c.at)).toEqual(expect.arrayContaining(["ctx", "self"]));
    });

    it("on_meeting_end のダミー再設置は 10.5 秒待ちが先 (L9 の模範形)、on_death の corpse_spawn は死亡時実行可 (spec §2 v1.1)", () => {
        const parsed = JSON.parse(dummyShowcaseRaw);
        const result = validateEkrDefinition(parsed);
        if (!result.ok) throw new Error(result.error);
        if (!result.def.logic) throw new Error("fixture は logic を持つ前提");

        const meetingEnd = result.def.logic.rules.find(r => r.when === "on_meeting_end");
        if (!meetingEnd) throw new Error("on_meeting_end ルールがある前提");
        expect(meetingEnd.do[0]).toEqual({ op: "wait", seconds: 10.5 });
        expect(meetingEnd.do[1]?.op).toBe("dummy_spawn");

        const onDeath = result.def.logic.rules.find(r => r.when === "on_death");
        if (!onDeath) throw new Error("on_death ルールがある前提");
        expect(onDeath.do.some(n => n.op === "corpse_spawn")).toBe(true);
    });

    it("リンター (spec §6 v1.1 — L9/L10 含む) は警告0件", () => {
        const parsed = JSON.parse(dummyShowcaseRaw);
        const result = validateEkrDefinition(parsed);
        if (!result.ok) throw new Error(result.error);
        if (!result.def.logic) throw new Error("fixture は logic を持つ前提");
        expect(lintRoleLogic(result.def.logic)).toEqual([]);
    });

    it("rolecode (EKR1.) のエンコード→デコード ラウンドトリップで AST が deep-equal になる", () => {
        const parsed = JSON.parse(dummyShowcaseRaw);
        const validated = validateEkrDefinition(parsed);
        if (!validated.ok) throw new Error(validated.error);

        const code = encodeRoleCode(JSON.stringify(validated.def));
        expect(code.startsWith(ROLECODE_PREFIX)).toBe(true);

        const roundTripped = validateEkrDefinition(JSON.parse(decodeRoleCode(code)));
        if (!roundTripped.ok) throw new Error(roundTripped.error);

        expect(roundTripped.def).toEqual(validated.def);
        expect(roundTripped.def.logic).toEqual(validated.def.logic);
        expect(encodeRoleCode(JSON.stringify(roundTripped.def))).toBe(code);
    });
});

// Wave 6 (docs/ekn-wave6-contract.md §9-3 2026-08-29): テンプレギャラリー見本3本。
// 「spawn❄ → launch → touch → kill」型 (ゆきだま/こおりのたま) と「大サイズ+fast」型 (ビームふう) の
// 最小構成 — role-cno-showcase/role-dummy-showcase と同じ「fixture 兼リグレッション資材」の扱い。
describe.each([
    { name: "role-yukidama-showcase.ekrole.json", raw: yukidamaShowcaseRaw },
    { name: "role-koorinotama-showcase.ekrole.json", raw: koorinotamaShowcaseRaw },
    { name: "role-beam-showcase.ekrole.json", raw: beamShowcaseRaw },
])("golden fixture: $name (Wave 6 テンプレギャラリー見本・とばすもの)", ({ raw }) => {
    it("validate に合格する", () => {
        const parsed = JSON.parse(raw);
        const result = validateEkrDefinition(parsed);
        expect(result.ok, result.ok ? "" : (result as { error: string }).error).toBe(true);
    });

    it("cno_spawn → cno_launch → (on_cno_touch) の並びを持つ", () => {
        const parsed = JSON.parse(raw);
        const result = validateEkrDefinition(parsed);
        if (!result.ok) throw new Error(result.error);
        if (!result.def.logic) throw new Error("fixture は logic を持つ前提");

        const ops = new Set<string>();
        for (const rule of result.def.logic.rules) collectOps(rule.do, ops);
        expect(ops.has("cno_spawn")).toBe(true);
        expect(ops.has("cno_launch")).toBe(true);
        expect(result.def.logic.rules.some((r) => r.when === "on_cno_touch")).toBe(true);
    });

    it("リンター (spec §6・L29 含む) は警告0件", () => {
        const parsed = JSON.parse(raw);
        const result = validateEkrDefinition(parsed);
        if (!result.ok) throw new Error(result.error);
        if (!result.def.logic) throw new Error("fixture は logic を持つ前提");
        expect(lintRoleLogic(result.def.logic)).toEqual([]);
    });

    it("rolecode (EKR1.) のエンコード→デコード ラウンドトリップで AST が deep-equal になる", () => {
        const parsed = JSON.parse(raw);
        const validated = validateEkrDefinition(parsed);
        if (!validated.ok) throw new Error(validated.error);

        const code = encodeRoleCode(JSON.stringify(validated.def));
        expect(code.startsWith(ROLECODE_PREFIX)).toBe(true);

        const roundTripped = validateEkrDefinition(JSON.parse(decodeRoleCode(code)));
        if (!roundTripped.ok) throw new Error(roundTripped.error);

        expect(roundTripped.def).toEqual(validated.def);
        expect(roundTripped.def.logic).toEqual(validated.def.logic);
        expect(encodeRoleCode(JSON.stringify(roundTripped.def))).toBe(code);
    });
});

// Wave 7 (docs/ekn-wave7-contract.md §6 2026-08-30): テンプレギャラリー見本2本。
// P2 型 (あつめや: on_task_complete → var_add → on_var ge 5 → win) と P7 型 (コバンザメ:
// on_game_start → win_join) の最小構成。あつめやは win の C# パース網羅も担う唯一の neutral fixture。
describe.each([
    { name: "role-collector-showcase.ekrole.json", raw: collectorShowcaseRaw, winOp: "win" },
    { name: "role-parasite-showcase.ekrole.json", raw: parasiteShowcaseRaw, winOp: "win_join" },
])("golden fixture: $name (Wave 7 テンプレギャラリー見本・かちまけ)", ({ raw, winOp }) => {
    it("validate に合格する", () => {
        const parsed = JSON.parse(raw);
        const result = validateEkrDefinition(parsed);
        expect(result.ok, result.ok ? "" : (result as { error: string }).error).toBe(true);
    });

    it(`勝利 op (${winOp}) を使っている`, () => {
        const parsed = JSON.parse(raw);
        const result = validateEkrDefinition(parsed);
        if (!result.ok) throw new Error(result.error);
        if (!result.def.logic) throw new Error("fixture は logic を持つ前提");

        const ops = new Set<string>();
        for (const rule of result.def.logic.rules) collectOps(rule.do, ops);
        expect(ops.has(winOp)).toBe(true);
    });

    it("リンター (spec §6) は警告0件", () => {
        const parsed = JSON.parse(raw);
        const result = validateEkrDefinition(parsed);
        if (!result.ok) throw new Error(result.error);
        if (!result.def.logic) throw new Error("fixture は logic を持つ前提");
        expect(lintRoleLogic(result.def.logic)).toEqual([]);
    });

    it("rolecode (EKR1.) のエンコード→デコード ラウンドトリップで AST が deep-equal になる", () => {
        const parsed = JSON.parse(raw);
        const validated = validateEkrDefinition(parsed);
        if (!validated.ok) throw new Error(validated.error);

        const code = encodeRoleCode(JSON.stringify(validated.def));
        expect(code.startsWith(ROLECODE_PREFIX)).toBe(true);

        const roundTripped = validateEkrDefinition(JSON.parse(decodeRoleCode(code)));
        if (!roundTripped.ok) throw new Error(roundTripped.error);

        expect(roundTripped.def).toEqual(validated.def);
        expect(encodeRoleCode(JSON.stringify(roundTripped.def))).toBe(code);
    });
});
