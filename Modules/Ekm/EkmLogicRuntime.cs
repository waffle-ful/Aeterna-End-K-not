using System;
using System.Collections.Generic;
using System.Text.Json;
using UnityEngine;

namespace EndKnot.Modules.Ekm;

// EKR logic 契約 v1 の汎用インタープリタ (契約正典: docs/ekr-logic-spec.md)。
// 将来のマップロジック (docs/ekm-studio/ROADMAP.md Stage 5a) とも共用する設計のため、
// このファイルには「役職」固有の語彙 (PlayerControl/kill/teleport 等) を一切持ち込まない。
// 「自分/相手」の解決は EkrFiber.Context (不透明 object) 経由で呼び出し元 (EkrLogicOpcodes 等) が行う。
//
// 責務: AST 型定義 / JSON→AST パーサ (spec §1,§3,§4 の検証を含む) / fiber スタックマシン (spec §5 の
// 命令数予算をここで強制する — 二層防御のハード層)。役職固有の opcode 実装は EkrLogicOpcodes.cs。

// ── AST ────────────────────────────────────────────────────────────────

public sealed class EkrVariable
{
    public string Name;
    public float Init;
}

public sealed class EkrRule
{
    public string When;
    public List<EkrNode> Do;

    // on_cno_touch (v1.2) 専用の必須フィールド (1..3)。他イベントでは常に 0 (未使用)。
    public int Slot;

    // R2 (docs/ekn-r2-contract.md §3b): on_attacked 専用の任意フィールド。null = 全種にマッチ。
    // 値は EkrAttackKinds のいずれか。
    public string Kind;

    // R2 (同 §3b): on_death 専用の任意フィールド。null = 全死因にマッチ。値は EkrDeathCauses のいずれか。
    public string Cause;
}

// op ごとの引数はフラットに全部持つ (spec §3 の「args ラッパー無し」に対応)。未使用フィールドは既定値のまま。
// 値は TryParse 時点で spec の範囲チェック済みなので実行時に再チェックしない
// (予算 [命令数/fiber数/レート] だけは実行側 [EkmLogicRuntime / EkrLogicOpcodes] が別途強制する)。
public sealed class EkrNode
{
    public string Op;

    // if
    public EkrExpr Cond;
    public List<EkrNode> Then;
    public List<EkrNode> Else;

    // wait / notify.seconds / set_kill_cooldown.seconds / speed.seconds (共用: 「秒数」引数)
    public float Seconds;

    // var_set / var_add
    public string VarName;
    public EkrExpr Value;

    // notify.text / cno_spawn.text
    public string Text;

    // teleport.to / kill.target / cno_spawn.at ("self" | "ctx" | "random") / notify.target (Wave 1)
    public string Target;

    // Wave 1 (spec §3 統一セレクタ): 「行き先 (to/at)」と「だれに (target)」が同じノードに同居する op
    // (teleport_other / pull) 用。Target が行き先を保持するので、対象セレクタはこちらに入れる。
    public string Subject;

    // speed.mult
    public float Mult;

    // cno_spawn.slot / cno_move.slot / cno_despawn.slot / cno_show.slot (1-based, 1..3)
    public int Slot;

    // cno_spawn.size
    public int Size;

    // cno_move.dx / cno_move.dy (spawn アンカーからの絶対オフセット — spec §3 裁定準拠)
    public float Dx;
    public float Dy;

    // cno_show.who ("all" | "self")
    public string Who;

    // dummy_spawn.name (v1.1) — cno_spawn.text とは別の専用フィールド (spec §7 命名慣行に合わせて追加)
    public string Name;

    // dummy_spawn.killable (v1.1)
    public bool Killable;

    // corpse_spawn.color (v1.1) ("self" | "random")
    public string Color;

    // portal_place.which (v1.2) ("a" | "b")
    public string Which;

    // field.radius (v1.3) ("small" | "medium" | "large")
    public string RadiusTier;

    // field.strength (v1.3) ("weak" | "medium" | "strong")
    public string StrengthTier;

    // Wave 2 (docs/ekn-wave2-contract.md §2.1 inspect): depth ("team" | "role")
    public string Depth;

    // Wave 2 (inspect): failChance (0..100) / noise (0..5)
    public int FailChance;
    public int Noise;

    // Wave 2 (vote_weight_set.value 0..3): 汎用の整数引数置き場 (他 op の Slot/Size とは意味論が
    // 別物なので専用フィールドにする — 「票のちから」を CNO slot と誤読させない)。
    public int IntArg;
}

public sealed class EkrExpr
{
    public string E; // "lit" | "var" | "op"
    public float V;
    public string Name;
    public string Kind;
    public EkrExpr A;
    public EkrExpr B;
}

public sealed class EkrLogicDef
{
    public int Version;
    public List<EkrVariable> Variables = [];
    public List<EkrRule> Rules = [];

    private const int MaxVariables = 16;
    private const int MaxRules = 32;
    private const int MaxNodesPerRule = 64;
    private const int MaxDepth = 8;
    private const int MaxTextLength = 120;
    private const int MaxCnoTextLength = 8;

    private static readonly HashSet<string> KnownEvents =
    [
        "on_game_start", "on_pet", "on_kill", "on_death", "on_meeting_start",
        "on_meeting_end", "on_task_complete", "on_vent_enter", "on_report", "on_second",
        "on_cno_touch", // v1.2 (2026-08-10)
        "on_attacked", // Wave 1 (2026-08-11)
        "on_meeting_vote", "on_meeting_pick" // Wave 2 (docs/ekn-wave2-contract.md §1)
    ];

    // R2 (docs/ekn-r2-contract.md §3b): on_attacked の種別と on_death の死因バケット。
    // ⚠️ TS 側 (editor/src/roledef.ts) と同じ並び・同じ綴りを保つこと (drift 検出は共有 fixture)。
    public static readonly string[] EkrAttackKinds = ["kill", "indirect", "force", "guess"];

    public static readonly string[] EkrDeathCauses = ["kill", "vote", "guess", "bomb", "poison-curse", "environment", "suicide", "other"];

    // rule 直下の任意フィルタ (kind/cause) の読み取り。指定できるイベントが決まっており、
    // 他イベントに置かれていたら slot と同じく reject する (静的に検査できるものは no-op でなく reject)。
    private static bool TryReadRuleFilter(JsonElement ruleEl, string when, string field, string onlyEvent, string[] allowed, out string value, out string error)
    {
        value = null;
        error = null;

        if (!ruleEl.TryGetProperty(field, out JsonElement el)) return true;

        if (when != onlyEvent)
        {
            error = $"when=\"{when}\" の rule に {field} は指定できません ({onlyEvent} 専用です)";
            return false;
        }

        if (el.ValueKind != JsonValueKind.String)
        {
            error = $"{onlyEvent} の {field} は文字列で指定してください";
            return false;
        }

        string raw = el.GetString();

        if (Array.IndexOf(allowed, raw) < 0)
        {
            error = $"{onlyEvent} の {field}=\"{raw}\" は未対応です (使えるのは {string.Join(" / ", allowed)})";
            return false;
        }

        value = raw;
        return true;
    }

    // Wave 1 (spec §3 統一セレクタ語彙)。単数セレクタのみ受理する op (kill/teleport_other/remember 等) と、
    // 複数セレクタも受理する明示ホワイトリスト op (Wave 1 では notify だけ) を分ける。
    private static readonly string[] SingleSelectors = ["self", "ctx", "saved1", "saved2", "nearest", "random"];
    private static readonly string[] MultiSelectors = ["self", "ctx", "saved1", "saved2", "nearest", "random", "all", "room"];

    // teleport_other.target は「他人」を動かす op なので self を含まない (自分は teleport の役目・spec §3)。
    private static readonly string[] OtherSelectors = ["ctx", "saved1", "saved2", "nearest", "random"];

    private static readonly HashSet<string> ControlOps = ["if", "wait", "stop", "var_set", "var_add"];

    private static readonly HashSet<string> ActionOps =
    [
        "notify", "teleport", "kill", "set_kill_cooldown", "speed",
        "cno_spawn", "cno_move", "cno_despawn", "cno_show",
        "dummy_spawn", "corpse_spawn", // v1.1 (2026-08-09)
        "marker_save", "teleport_other", "portal_place", // v1.2 (2026-08-10)
        "pull", "drag", "field", // v1.3 (2026-08-11)
        "remember", "cancel_attack", // Wave 1 (2026-08-11)
        // Wave 2 (docs/ekn-wave2-contract.md §2,§3): 情報と会議
        "inspect", "reveal", "arrow_show", "arrow_mark", "arrow_hide",
        "cancel_vote", "vote_weight_set", "vote_block", "vote_swap", "exile"
    ];

    private static readonly HashSet<string> ExprKinds =
    [
        "add", "sub", "mul", "div", "eq", "ne", "lt", "le", "gt", "ge", "and", "or", "not", "rand"
    ];

    // spec §1: 未知の op/when/e = 文書全体 reject。型不一致も文書全体 reject (JSON 型不一致は System.Text.Json の
    // ValueKind を素直にチェックすることで担保する — フィールド毎の既定値補正はしない)。
    public static bool TryParse(JsonElement root, out EkrLogicDef def, out string error)
    {
        def = null;
        error = null;

        try
        {
            return TryParseInner(root, out def, out error);
        }
        catch (Exception ex)
        {
            def = null;
            error = $"ロジックの読み取り中に問題が発生しました ({ex.Message})";
            return false;
        }
    }

    private static bool TryParseInner(JsonElement root, out EkrLogicDef def, out string error)
    {
        def = null;
        error = null;

        if (root.ValueKind != JsonValueKind.Object)
        {
            error = "logic の形式が不正です";
            return false;
        }

        var parsed = new EkrLogicDef();

        if (!root.TryGetProperty("version", out JsonElement versionEl) || !EkrJson.TryReadInt(versionEl, out int version) || version != 1)
        {
            error = "このバージョンの End K not では読み込めないロジックです (logic.version)。End K not を更新してください";
            return false;
        }

        parsed.Version = version;

        var knownVarNames = new HashSet<string>();

        if (root.TryGetProperty("variables", out JsonElement varsEl))
        {
            if (varsEl.ValueKind != JsonValueKind.Array)
            {
                error = "logic.variables の形式が不正です";
                return false;
            }

            if (varsEl.GetArrayLength() > MaxVariables)
            {
                error = $"ロジックの変数が多すぎます (最大{MaxVariables}個)";
                return false;
            }

            foreach (JsonElement varEl in varsEl.EnumerateArray())
            {
                if (varEl.ValueKind != JsonValueKind.Object ||
                    !varEl.TryGetProperty("name", out JsonElement nameEl) || nameEl.ValueKind != JsonValueKind.String ||
                    !varEl.TryGetProperty("init", out JsonElement initEl) || initEl.ValueKind != JsonValueKind.Number || !initEl.TryGetDouble(out double initD) || !double.IsFinite(initD))
                {
                    error = "ロジックの変数定義が不正です (name/init を確認してください)";
                    return false;
                }

                // R0 の name/author と同じトリム慣行 (spec §1 2026-08-09 裁定) — 宣言側もここで trim してから
                // 空/長さ/重複判定する。参照側 (TryGetVarName / "var" 式) も同じ trim 済み文字列で照合する。
                string name = (nameEl.GetString() ?? "").Trim();

                if (string.IsNullOrEmpty(name) || name.Length > 20)
                {
                    error = "ロジックの変数名は1〜20文字にしてください";
                    return false;
                }

                if (!knownVarNames.Add(name))
                {
                    error = $"ロジックの変数名が重複しています ({name})";
                    return false;
                }

                parsed.Variables.Add(new EkrVariable { Name = name, Init = (float)initD });
            }
        }

        if (!root.TryGetProperty("rules", out JsonElement rulesEl) || rulesEl.ValueKind != JsonValueKind.Array)
        {
            error = "logic.rules がありません";
            return false;
        }

        int ruleCount = rulesEl.GetArrayLength();

        if (ruleCount is < 1 or > MaxRules)
        {
            error = $"ロジックの rules の数が範囲外です (1〜{MaxRules}個)";
            return false;
        }

        foreach (JsonElement ruleEl in rulesEl.EnumerateArray())
        {
            if (ruleEl.ValueKind != JsonValueKind.Object || !ruleEl.TryGetProperty("when", out JsonElement whenEl) || whenEl.ValueKind != JsonValueKind.String)
            {
                error = "ロジックの rule に when がありません";
                return false;
            }

            string when = whenEl.GetString();

            if (!KnownEvents.Contains(when))
            {
                error = $"ロジックの when が未対応です ({when})。End K not を更新してください";
                return false;
            }

            // v1.2 spec §2: on_cno_touch は rule に必須フィールド slot (1..3) を持つ唯一のイベント。
            // 他イベントに slot があれば reject、on_cno_touch に無くても reject。
            int ruleSlot = 0;

            if (when == "on_cno_touch")
            {
                if (!ruleEl.TryGetProperty("slot", out JsonElement ruleSlotEl) ||
                    !EkrJson.TryReadInt(ruleSlotEl, out ruleSlot) || ruleSlot is < 1 or > 3)
                {
                    error = "on_cno_touch の rule には slot (1〜3) が必要です";
                    return false;
                }
            }
            else if (ruleEl.TryGetProperty("slot", out _))
            {
                error = $"when=\"{when}\" の rule に slot は指定できません (on_cno_touch 専用です)";
                return false;
            }

            // R2 (docs/ekn-r2-contract.md §3b): on_attacked の任意フィールド kind / on_death の任意
            // フィールド cause。省略 = 全種にマッチ。他イベントに置かれていたら slot と同じく reject。
            if (!TryReadRuleFilter(ruleEl, when, "kind", "on_attacked", EkrAttackKinds, out string ruleKind, out error)) return false;
            if (!TryReadRuleFilter(ruleEl, when, "cause", "on_death", EkrDeathCauses, out string ruleCause, out error)) return false;

            if (!ruleEl.TryGetProperty("do", out JsonElement doEl) || doEl.ValueKind != JsonValueKind.Array)
            {
                error = "ロジックの rule に do がありません";
                return false;
            }

            var budget = new NodeBudget();

            if (!TryParseNodeList(doEl, knownVarNames, budget, 1, out List<EkrNode> doNodes, out error))
                return false;

            if (doNodes.Count == 0)
            {
                error = "ロジックの rule の do が空です";
                return false;
            }

            // Wave 1 (spec §3 cancel_attack): on_attacked 以外の rule 配下 (if の入れ子含む) に現れたら
            // 文書全体 reject。静的に検査できるので no-op ではなく reject する (on_cno_touch の slot 必須と
            // 同じ厳格側)。TryParseNode は「その時点で囲っている rule の when」を知らない (汎用エンジンに
            // 役職イベントの語彙を持ち込まないため) ので、rule の解析完了後にまとめて歩く。
            if (when != "on_attacked" && ContainsCancelAttack(doNodes))
            {
                error = $"「こうげきをふせぐ」は「こうげきされたとき」のブロックの中でだけ使えます (when=\"{when}\" の中では使えません)";
                return false;
            }

            // Wave 2 (docs/ekn-wave2-contract.md §1.3): cancel_vote は on_meeting_vote 以外の rule 配下に
            // 現れたら文書 reject (cancel_attack と同じ厳格側 — 静的に検査できるので no-op より reject)。
            if (when != "on_meeting_vote" && ContainsOp(doNodes, "cancel_vote"))
            {
                error = $"「票をつかわずにえらぶ」は「かいぎで投票したとき」のブロックの中でだけ使えます (when=\"{when}\" の中では使えません)";
                return false;
            }

            parsed.Rules.Add(new EkrRule { When = when, Do = doNodes, Slot = ruleSlot, Kind = ruleKind, Cause = ruleCause });
        }

        def = parsed;
        return true;
    }

    // cancel_attack のスコープ検証用の再帰探索 (if の then/else も潜る)。深さは既に MaxDepth で
    // 制限済みなので追加のガードは不要。
    private static bool ContainsCancelAttack(List<EkrNode> nodes) => ContainsOp(nodes, "cancel_attack");

    // Wave 2: cancel_vote も同型のスコープ検証を要る (§1.3) ので汎用化した。
    private static bool ContainsOp(List<EkrNode> nodes, string op)
    {
        if (nodes == null) return false;

        foreach (EkrNode node in nodes)
        {
            if (node.Op == op) return true;
            if (ContainsOp(node.Then, op)) return true;
            if (ContainsOp(node.Else, op)) return true;
        }

        return false;
    }

    private static bool TryParseNodeList(JsonElement arrEl, HashSet<string> knownVars, NodeBudget budget, int depth, out List<EkrNode> nodes, out string err)
    {
        nodes = null;
        err = null;

        if (depth > MaxDepth)
        {
            err = "ロジックの入れ子が深すぎます (最大8段)";
            return false;
        }

        var list = new List<EkrNode>();

        foreach (JsonElement nodeEl in arrEl.EnumerateArray())
        {
            budget.Count++;

            if (budget.Count > MaxNodesPerRule)
            {
                err = $"1つの rule のブロック数が多すぎます (最大{MaxNodesPerRule}個)";
                return false;
            }

            if (!TryParseNode(nodeEl, knownVars, budget, depth, out EkrNode node, out err))
                return false;

            list.Add(node);
        }

        nodes = list;
        return true;
    }

    private static bool TryParseNode(JsonElement nodeEl, HashSet<string> knownVars, NodeBudget budget, int depth, out EkrNode node, out string err)
    {
        node = null;
        err = null;

        if (nodeEl.ValueKind != JsonValueKind.Object || !nodeEl.TryGetProperty("op", out JsonElement opEl) || opEl.ValueKind != JsonValueKind.String)
        {
            err = "ロジックのブロックに op がありません";
            return false;
        }

        string op = opEl.GetString();

        if (!ControlOps.Contains(op) && !ActionOps.Contains(op))
        {
            err = $"ロジックの op が未対応です ({op})。End K not を更新してください";
            return false;
        }

        var n = new EkrNode { Op = op };

        switch (op)
        {
            case "if":
                // spec §1 (2026-08-09 裁定): node から自身の expr への突入も +1 (if.then/else の子 node や
                // op.a/op.b と同じ「潜る遷移は全て+1」ルール)。cond を depth のまま渡すのはオフバイワン。
                if (!TryGetExpr(nodeEl, "cond", knownVars, depth + 1, out n.Cond, out err)) return false;

                if (!nodeEl.TryGetProperty("then", out JsonElement thenEl) || thenEl.ValueKind != JsonValueKind.Array)
                {
                    err = "if ブロックに then がありません";
                    return false;
                }

                if (!TryParseNodeList(thenEl, knownVars, budget, depth + 1, out n.Then, out err)) return false;

                if (nodeEl.TryGetProperty("else", out JsonElement elseEl) && elseEl.ValueKind != JsonValueKind.Null)
                {
                    if (elseEl.ValueKind != JsonValueKind.Array)
                    {
                        err = "if ブロックの else の形式が不正です";
                        return false;
                    }

                    if (!TryParseNodeList(elseEl, knownVars, budget, depth + 1, out n.Else, out err)) return false;
                }
                else n.Else = [];

                break;

            case "wait":
                if (!TryGetFloat(nodeEl, "seconds", out float waitSec, out err)) return false;
                if (waitSec is < 0.1f or > 600f) { err = "wait の秒数が範囲外です (0.1〜600)"; return false; }
                n.Seconds = waitSec;
                break;

            case "stop":
                break;

            case "var_set":
            case "var_add":
                if (!TryGetVarName(nodeEl, "name", knownVars, out n.VarName, out err)) return false;
                // spec §1 (2026-08-09 裁定): var_set.value / var_add.delta への突入も +1 (if.cond と同型)。
                if (!TryGetExpr(nodeEl, op == "var_set" ? "value" : "delta", knownVars, depth + 1, out n.Value, out err)) return false;
                break;

            case "notify":
                if (!TryGetString(nodeEl, "text", MaxTextLength, out n.Text, out err)) return false;
                n.Text = SanitizeUserText(n.Text);
                if (!TryGetFloat(nodeEl, "seconds", out float notifySec, out err)) return false;
                if (notifySec is < 1f or > 30f) { err = "notify の秒数が範囲外です (1〜30)"; return false; }
                n.Seconds = notifySec;

                // Wave 1 (spec §3): target は任意 (既定 "self")。複数セレクタを受理する唯一の op。
                if (nodeEl.TryGetProperty("target", out _))
                {
                    if (!TryGetEnum(nodeEl, "target", MultiSelectors, out n.Target, out err)) return false;
                }
                else n.Target = "self";

                break;

            case "teleport":
                // v1.2: to にマーカー行き先 (marker1..4) を追加。Wave 1: cno1..3 を追加。
                if (!TryGetEnum(nodeEl, "to", ["random", "ctx", "marker1", "marker2", "marker3", "marker4", "cno1", "cno2", "cno3"], out n.Target, out err)) return false;
                break;

            // v1.2 (2026-08-10)
            case "marker_save":
                if (!TryGetInt(nodeEl, "slot", 1, 4, out n.Slot, out err)) return false;
                if (!TryGetEnum(nodeEl, "at", ["self", "ctx", "cno1", "cno2", "cno3"], out n.Target, out err)) return false;
                break;

            // v1.2 (2026-08-10) / Wave 1 拡張: target は単数セレクタ (self を除く)、to は行き先。
            // 行き先 (to) を teleport と同じ慣行で n.Target に、対象セレクタを n.Subject に格納する。
            case "teleport_other":
                if (!TryGetEnum(nodeEl, "target", OtherSelectors, out n.Subject, out err)) return false;
                if (!TryGetEnum(nodeEl, "to", ["self", "marker1", "marker2", "marker3", "marker4", "cno1", "cno2", "cno3"], out n.Target, out err)) return false;
                break;

            case "portal_place":
                if (!TryGetEnum(nodeEl, "which", ["a", "b"], out n.Which, out err)) return false;
                break;

            case "kill":
                // Wave 1: 単数セレクタ全種を受理 (saved1/saved2/nearest/random を追加)。
                if (!TryGetEnum(nodeEl, "target", SingleSelectors, out n.Target, out err)) return false;
                break;

            // Wave 1 (spec §3): marker_save の人間版。予算なし (ローカル状態のみ)。
            case "remember":
                if (!TryGetInt(nodeEl, "slot", 1, 2, out n.Slot, out err)) return false;
                if (!TryGetEnum(nodeEl, "target", SingleSelectors, out n.Target, out err)) return false;
                break;

            // Wave 1 (spec §3): 引数なし。on_attacked 配下でのみ有効 (スコープ検証は ValidateCancelAttackScope)。
            case "cancel_attack":
                break;

            case "set_kill_cooldown":
                if (!TryGetFloat(nodeEl, "seconds", out float kcdSec, out err)) return false;
                if (kcdSec is < 1f or > 300f) { err = "set_kill_cooldown の秒数が範囲外です (1〜300)"; return false; }
                n.Seconds = kcdSec;
                break;

            case "speed":
                if (!TryGetFloat(nodeEl, "mult", out float mult, out err)) return false;
                if (mult is < 0.5f or > 3.0f) { err = "speed の倍率が範囲外です (0.5〜3.0)"; return false; }
                n.Mult = mult;
                if (!TryGetFloat(nodeEl, "seconds", out float spdSec, out err)) return false;
                if (spdSec is < 1f or > 60f) { err = "speed の秒数が範囲外です (1〜60)"; return false; }
                n.Seconds = spdSec;
                break;

            case "cno_spawn":
                if (!TryGetInt(nodeEl, "slot", 1, 3, out n.Slot, out err)) return false;
                if (!TryGetString(nodeEl, "text", MaxCnoTextLength, out n.Text, out err)) return false;
                n.Text = SanitizeUserText(n.Text);
                if (!TryGetInt(nodeEl, "size", 1, 12, out n.Size, out err)) return false;
                if (!TryGetEnum(nodeEl, "at", ["self", "ctx"], out n.Target, out err)) return false;
                break;

            case "cno_move":
                if (!TryGetInt(nodeEl, "slot", 1, 3, out n.Slot, out err)) return false;
                if (!TryGetFloat(nodeEl, "dx", out n.Dx, out err)) return false;
                if (n.Dx is < -50f or > 50f) { err = "cno_move の dx が範囲外です (-50〜50)"; return false; }
                if (!TryGetFloat(nodeEl, "dy", out n.Dy, out err)) return false;
                if (n.Dy is < -50f or > 50f) { err = "cno_move の dy が範囲外です (-50〜50)"; return false; }
                break;

            case "cno_despawn":
                if (!TryGetInt(nodeEl, "slot", 1, 3, out n.Slot, out err)) return false;
                break;

            case "cno_show":
                if (!TryGetInt(nodeEl, "slot", 1, 3, out n.Slot, out err)) return false;
                if (!TryGetEnum(nodeEl, "who", ["all", "self"], out n.Who, out err)) return false;
                break;

            // v1.1 (2026-08-09)
            case "dummy_spawn":
                if (!TryGetInt(nodeEl, "slot", 1, 3, out n.Slot, out err)) return false;

                // spec §3: name は trim → サニタイズ (text と同じ関数) → 長さ判定 (≤8字) → 空なら "Dummy" の
                // 順。TryGetString は素の (未 trim) 文字列で長さ判定するため、trim してから空/長さを見る
                // ここでは使わず、素の文字列取得だけ流用する。
                if (!nodeEl.TryGetProperty("name", out JsonElement nameEl) || nameEl.ValueKind != JsonValueKind.String)
                {
                    err = "name の値が不正です";
                    return false;
                }

                string dummyName = SanitizeUserText((nameEl.GetString() ?? "").Trim());

                if (dummyName.Length > MaxCnoTextLength)
                {
                    err = $"name が長すぎます (最大{MaxCnoTextLength}文字)";
                    return false;
                }

                n.Name = dummyName.Length == 0 ? "Dummy" : dummyName;

                if (!TryGetBool(nodeEl, "killable", out n.Killable, out err)) return false;
                if (!TryGetEnum(nodeEl, "at", ["self", "ctx"], out n.Target, out err)) return false;
                break;

            case "corpse_spawn":
                if (!TryGetEnum(nodeEl, "color", ["self", "random"], out n.Color, out err)) return false;
                if (!TryGetEnum(nodeEl, "at", ["self", "ctx"], out n.Target, out err)) return false;
                break;

            // v1.3 (2026-08-11): pull は引数なし。実装 (EkrLogicOpcodes) が teleport_other の to:"self" 経路と
            // 完全共有するため、n.Target を "self" に固定しておく (spec §3「実装は teleport_other の to:"self"
            // と完全共有」)。
            case "pull":
                n.Target = "self";
                n.Subject = "ctx"; // spec §3: pull は ctx 暗黙 (引数なしの糖衣)
                break;

            case "drag":
                if (!TryGetFloat(nodeEl, "seconds", out float dragSec, out err)) return false;
                if (dragSec is < 1f or > 10f) { err = "drag の秒数が範囲外です (1〜10)"; return false; }
                n.Seconds = dragSec;
                break;

            case "field":
                if (!TryGetEnum(nodeEl, "at", ["self", "ctx", "marker1", "marker2", "marker3", "marker4"], out n.Target, out err)) return false;
                if (!TryGetEnum(nodeEl, "radius", ["small", "medium", "large"], out n.RadiusTier, out err)) return false;
                if (!TryGetEnum(nodeEl, "strength", ["weak", "medium", "strong"], out n.StrengthTier, out err)) return false;
                if (!TryGetFloat(nodeEl, "seconds", out float fieldSec, out err)) return false;
                if (fieldSec is < 1f or > 15f) { err = "field の秒数が範囲外です (1〜15)"; return false; }
                n.Seconds = fieldSec;
                break;

            // ── Wave 2 (docs/ekn-wave2-contract.md §2,§3): 情報と会議 ──────────────────────────

            case "inspect":
                // spec §2.1: self は受理しない (OtherSelectors = ctx/saved1/saved2/nearest/random)。
                if (!TryGetEnum(nodeEl, "target", OtherSelectors, out n.Target, out err)) return false;
                if (!TryGetEnum(nodeEl, "depth", ["team", "role"], out n.Depth, out err)) return false;

                n.FailChance = 0;
                if (nodeEl.TryGetProperty("failChance", out _) && !TryGetInt(nodeEl, "failChance", 0, 100, out n.FailChance, out err)) return false;

                n.Noise = 0;
                if (nodeEl.TryGetProperty("noise", out _) && !TryGetInt(nodeEl, "noise", 0, 5, out n.Noise, out err)) return false;

                // spec §2.1: noise は depth="role" のみ受理 (team との併用は文書 reject)。
                if (n.Noise > 0 && n.Depth != "role")
                {
                    err = "「まぜるダミー」は「やくしょくをみる」のときだけ使えます";
                    return false;
                }

                break;

            case "reveal":
                // spec §2.2: inspect と同じ受理値 (self 不可)。
                if (!TryGetEnum(nodeEl, "target", OtherSelectors, out n.Target, out err)) return false;
                break;

            case "arrow_show":
                // spec §2.3: inspect と同じ受理値 (self 不可)。
                if (!TryGetEnum(nodeEl, "target", OtherSelectors, out n.Target, out err)) return false;
                if (!TryGetFloat(nodeEl, "seconds", out float arrowShowSec, out err)) return false;
                if (arrowShowSec is < 5f or > 600f) { err = "arrow_show の秒数が範囲外です (5〜600)"; return false; }
                n.Seconds = arrowShowSec;
                break;

            case "arrow_mark":
                if (!TryGetEnum(nodeEl, "at", ["ctx", "marker1", "marker2", "marker3", "marker4", "cno1", "cno2", "cno3"], out n.Target, out err)) return false;
                if (!TryGetFloat(nodeEl, "seconds", out float arrowMarkSec, out err)) return false;
                if (arrowMarkSec is < 5f or > 600f) { err = "arrow_mark の秒数が範囲外です (5〜600)"; return false; }
                n.Seconds = arrowMarkSec;
                break;

            case "arrow_hide":
                break;

            // spec §1.3: 引数なし。on_meeting_vote 以外の rule 配下は文書 reject (上の ContainsOp 検査済み)。
            case "cancel_vote":
                break;

            case "vote_weight_set":
                if (!TryGetInt(nodeEl, "value", 0, 3, out n.IntArg, out err)) return false;
                break;

            case "vote_block":
                // spec §3.2: self 不可。
                if (!TryGetEnum(nodeEl, "target", OtherSelectors, out n.Target, out err)) return false;
                break;

            case "vote_swap":
                break;

            case "exile":
                // spec §3.4: self 可 (SingleSelectors は self を含む単数セレクタ全種)。
                if (!TryGetEnum(nodeEl, "target", SingleSelectors, out n.Target, out err)) return false;
                break;
        }

        node = n;
        return true;
    }

    private static bool TryGetExpr(JsonElement parentEl, string propName, HashSet<string> knownVars, int depth, out EkrExpr expr, out string err)
    {
        expr = null;
        err = null;

        if (!parentEl.TryGetProperty(propName, out JsonElement exprEl))
        {
            err = $"{propName} がありません";
            return false;
        }

        return TryParseExpr(exprEl, knownVars, depth, out expr, out err);
    }

    private static bool TryParseExpr(JsonElement el, HashSet<string> knownVars, int depth, out EkrExpr expr, out string err)
    {
        expr = null;
        err = null;

        if (depth > MaxDepth)
        {
            err = "ロジックの式の入れ子が深すぎます (最大8段)";
            return false;
        }

        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty("e", out JsonElement eKindEl) || eKindEl.ValueKind != JsonValueKind.String)
        {
            err = "ロジックの式 (e) が不正です";
            return false;
        }

        string kind = eKindEl.GetString();

        switch (kind)
        {
            case "lit":
                if (!el.TryGetProperty("v", out JsonElement vEl) || vEl.ValueKind != JsonValueKind.Number || !vEl.TryGetDouble(out double vd) || !double.IsFinite(vd))
                {
                    err = "リテラル式の v が不正です";
                    return false;
                }

                expr = new EkrExpr { E = "lit", V = (float)vd };
                return true;

            case "var":
                if (!el.TryGetProperty("name", out JsonElement nameEl) || nameEl.ValueKind != JsonValueKind.String)
                {
                    err = "変数式の name が不正です";
                    return false;
                }

                // 宣言側 (TryParseInner) と同じ trim 慣行 (spec §1 2026-08-09 裁定) — trim してから照合しないと
                // 宣言時に trim 済みの名前と一致しなくなる。
                string vname = (nameEl.GetString() ?? "").Trim();

                if (!knownVars.Contains(vname))
                {
                    err = $"未定義の変数を参照しています ({vname})";
                    return false;
                }

                expr = new EkrExpr { E = "var", Name = vname };
                return true;

            case "op":
                if (!el.TryGetProperty("kind", out JsonElement kindEl) || kindEl.ValueKind != JsonValueKind.String || !ExprKinds.Contains(kindEl.GetString()))
                {
                    err = "演算式の kind が不正です";
                    return false;
                }

                string opKind = kindEl.GetString();

                if (!TryGetExpr(el, "a", knownVars, depth + 1, out EkrExpr a, out err)) return false;

                EkrExpr b = null;
                if (opKind != "not" && !TryGetExpr(el, "b", knownVars, depth + 1, out b, out err)) return false;

                expr = new EkrExpr { E = "op", Kind = opKind, A = a, B = b };
                return true;

            default:
                err = $"ロジックの式が未対応です ({kind})";
                return false;
        }
    }

    private static bool TryGetVarName(JsonElement parentEl, string propName, HashSet<string> knownVars, out string name, out string err)
    {
        name = null;
        err = null;

        if (!parentEl.TryGetProperty(propName, out JsonElement el) || el.ValueKind != JsonValueKind.String)
        {
            err = $"{propName} がありません";
            return false;
        }

        // 宣言側 (TryParseInner) と同じ trim 慣行 (spec §1 2026-08-09 裁定)。
        name = (el.GetString() ?? "").Trim();

        if (!knownVars.Contains(name))
        {
            err = $"未定義の変数を参照しています ({name})";
            return false;
        }

        return true;
    }

    private static bool TryGetFloat(JsonElement parentEl, string propName, out float value, out string err)
    {
        value = 0f;
        err = null;

        if (!parentEl.TryGetProperty(propName, out JsonElement el) || el.ValueKind != JsonValueKind.Number || !el.TryGetDouble(out double d) || !double.IsFinite(d))
        {
            err = $"{propName} の値が不正です";
            return false;
        }

        value = (float)d;
        return true;
    }

    // v1.1 (2026-08-09): dummy_spawn.killable 用。JSON の true/false のみ受理する (0/1 等の数値は reject —
    // spec §1「型不一致は文書全体 reject」)。
    private static bool TryGetBool(JsonElement parentEl, string propName, out bool value, out string err)
    {
        value = false;
        err = null;

        if (!parentEl.TryGetProperty(propName, out JsonElement el) || (el.ValueKind != JsonValueKind.True && el.ValueKind != JsonValueKind.False))
        {
            err = $"{propName} の値が不正です";
            return false;
        }

        value = el.GetBoolean();
        return true;
    }

    private static bool TryGetInt(JsonElement parentEl, string propName, int min, int max, out int value, out string err)
    {
        value = 0;
        err = null;

        // spec §1 (2026-08-11 裁定): 小数点/指数表記でも整数と等価なら受理 (`2.0` = `2`)。
        if (!parentEl.TryGetProperty(propName, out JsonElement el) || !EkrJson.TryReadInt(el, out int i))
        {
            err = $"{propName} の値が不正です";
            return false;
        }

        if (i < min || i > max)
        {
            err = $"{propName} の値が範囲外です ({min}〜{max})";
            return false;
        }

        value = i;
        return true;
    }

    private static bool TryGetString(JsonElement parentEl, string propName, int maxLen, out string value, out string err)
    {
        value = null;
        err = null;

        if (!parentEl.TryGetProperty(propName, out JsonElement el) || el.ValueKind != JsonValueKind.String)
        {
            err = $"{propName} の値が不正です";
            return false;
        }

        value = el.GetString() ?? "";

        if (value.Length > maxLen)
        {
            err = $"{propName} が長すぎます (最大{maxLen}文字)";
            return false;
        }

        return true;
    }

    private static bool TryGetEnum(JsonElement parentEl, string propName, string[] allowed, out string value, out string err)
    {
        value = null;
        err = null;

        if (!parentEl.TryGetProperty(propName, out JsonElement el) || el.ValueKind != JsonValueKind.String)
        {
            err = $"{propName} の値が不正です";
            return false;
        }

        value = el.GetString();

        if (Array.IndexOf(allowed, value) < 0)
        {
            err = $"{propName} の値が不正です ({value})";
            return false;
        }

        return true;
    }

    // ユーザー入力 (notify.text / cno_spawn.text) は TMP タグの生埋め込み先になる (notify は Notify() 経由で
    // 他ロールと同じ表示経路、cno_spawn は CustomNetObject のスプライトとして他クライアントへ配信される)。
    // 開いた `<`/`>` を通すとタグ注入で表示崩壊やサイズ改変の余地が生まれるため、全角へ機械置換して無害化する。
    // 装飾目的の対応は R1 スコープ外 — 安全な平文表示のみ許可する。
    private static string SanitizeUserText(string s)
    {
        return string.IsNullOrEmpty(s) ? s : s.Replace('<', '〈').Replace('>', '〉');
    }

    private sealed class NodeBudget
    {
        public int Count;
    }
}

// ── 実行時 (fiber / 明示スタックマシン) ──────────────────────────────────

// アクション系 op (notify 等) の実行は呼び出し元 (役職なら EkrLogicOpcodes) に委譲する。
// 汎用エンジンはアクションの意味を一切知らない。
public interface IEkrActionSink
{
    void Execute(EkrNode node, EkrFiber fiber);
}

internal sealed class EkrFrame
{
    public IReadOnlyList<EkrNode> Nodes;
    public int Index;
}

// 1回のイベント発火が生む実行単位。明示スタック (EkrFrame の列) で if の入れ子を表現し、
// wait は C# の yield/コルーチンを使わず WakeAt を立てて即 return する
// (常駐コルーチン禁止 — 呼び出し元が毎 FixedUpdate 手動で Pump する。memory: nested-managed-enumerator-gchandle-leak)。
public sealed class EkrFiber
{
    // 役職なら EkrActionContext。汎用エンジンはこの中身を一切読まない (呼び出し元とアクション実装だけが読む)。
    public object Context;

    // 生成元の「所有者」が持つ変数ストア (参照共有 — 同じ所有者の複数 fiber が同じ変数を見る。
    // spec: variables は rule 横断で共有)。
    public Dictionary<string, float> Variables;

    public float WakeAt = -1f;
    public int InstrUsed;
    public bool Done;
    public bool Aborted;

    // kill opcode 連鎖ガード用 (spec §5「kill 連鎖は深さ1」)。fiber 生成時に一度だけ焼き込む
    // (生存中ずっと wait を挟んでも失われないよう、実行時に都度グローバルフラグを見ない設計)。
    public bool FromKillChain;

    internal readonly List<EkrFrame> Stack = [];
}

public static class EkmLogicRuntime
{
    // spec §5: 500/イベント発火 (fiber 1本の生涯合計) ・2000/フレーム (EKR 全体合計) ・fiber 同時8/ホルダー。
    public const int MaxInstructionsPerFiber = 500;
    public const int MaxInstructionsPerFrame = 2000;
    public const int MaxFibersPerHolder = 8;

    // フレーム境界の検出は Time.frameCount (int) を使う。Time.time (float) の相対誤差比較は稼働時間が
    // 伸びると隣接ステップが等値判定されて二度とリセットされなくなる罠がある (advisor 指摘・2026-08-09)。
    private static int _lastFrameCount = -1;
    private static int _globalInstrThisFrame;

    public static EkrFiber Spawn(IReadOnlyList<EkrNode> nodes, Dictionary<string, float> variables, object context, bool fromKillChain)
    {
        var fiber = new EkrFiber { Variables = variables, Context = context, FromKillChain = fromKillChain };

        if (nodes is { Count: > 0 }) fiber.Stack.Add(new EkrFrame { Nodes = nodes, Index = 0 });
        else fiber.Done = true;

        return fiber;
    }

    // 1つの fiber を「起きるまで/終わるまで」進める。呼び出し元は毎 FixedUpdate、保持者ごとの
    // アクティブ fiber リストを舐めてこれを呼ぶ。戻り値 false = 呼び出し元のリストから除去してよい
    // (Done か Aborted のどちらか — Aborted は fiber.Aborted で判別する)。
    // ignoreFrameBudget: この Pump 呼び出しの間だけ「2000/フレーム」による打ち切りを見送る
    // (実行した命令のフレーム集計への加算は維持・per-fiber 500 は通常どおり適用)。
    // 汎用エンジンはこのフラグの意味 (=どのイベントが特別か) を知らない — 呼び出し元が決める。
    public static bool Pump(EkrFiber fiber, IEkrActionSink sink, bool ignoreFrameBudget = false)
    {
        if (fiber.Done) return false;
        if (fiber.WakeAt >= 0f && Time.realtimeSinceStartup < fiber.WakeAt) return true;

        EnsureFrame();
        fiber.WakeAt = -1f;

        while (true)
        {
            if (fiber.Stack.Count == 0)
            {
                fiber.Done = true;
                return false;
            }

            EkrFrame top = fiber.Stack[^1];

            if (top.Index >= top.Nodes.Count)
            {
                fiber.Stack.RemoveAt(fiber.Stack.Count - 1);
                continue;
            }

            if (fiber.InstrUsed >= MaxInstructionsPerFiber || (!ignoreFrameBudget && _globalInstrThisFrame >= MaxInstructionsPerFrame))
            {
                fiber.Aborted = true;
                fiber.Done = true;
                return false;
            }

            EkrNode node = top.Nodes[top.Index];
            top.Index++;
            fiber.InstrUsed++;
            _globalInstrThisFrame++;

            switch (node.Op)
            {
                case "if":
                    List<EkrNode> branch = EvalTruthy(node.Cond, fiber.Variables) ? node.Then : node.Else;
                    if (branch is { Count: > 0 }) fiber.Stack.Add(new EkrFrame { Nodes = branch, Index = 0 });
                    continue;

                case "wait":
                    fiber.WakeAt = Time.realtimeSinceStartup + node.Seconds;
                    return true;

                case "stop":
                    fiber.Stack.Clear();
                    fiber.Done = true;
                    return false;

                case "var_set":
                    fiber.Variables[node.VarName] = Eval(node.Value, fiber.Variables);
                    continue;

                case "var_add":
                    fiber.Variables[node.VarName] = fiber.Variables.GetValueOrDefault(node.VarName) + Eval(node.Value, fiber.Variables);
                    continue;

                default:
                    sink.Execute(node, fiber);
                    continue;
            }
        }
    }

    private static void EnsureFrame()
    {
        int frame = Time.frameCount;
        if (frame == _lastFrameCount) return;
        _lastFrameCount = frame;
        _globalInstrThisFrame = 0;
    }

    public static bool EvalTruthy(EkrExpr expr, Dictionary<string, float> vars) => Eval(expr, vars) != 0f;

    public static float Eval(EkrExpr expr, Dictionary<string, float> vars)
    {
        if (expr == null) return 0f;

        switch (expr.E)
        {
            case "lit": return expr.V;
            case "var": return vars.GetValueOrDefault(expr.Name);
            case "op":
                float a = Eval(expr.A, vars);

                if (expr.Kind == "not") return a == 0f ? 1f : 0f;

                float b = Eval(expr.B, vars);

                return expr.Kind switch
                {
                    "add" => a + b,
                    "sub" => a - b,
                    "mul" => a * b,
                    "div" => b == 0f ? 0f : a / b,
                    "eq" => a == b ? 1f : 0f,
                    "ne" => a != b ? 1f : 0f,
                    "lt" => a < b ? 1f : 0f,
                    "le" => a <= b ? 1f : 0f,
                    "gt" => a > b ? 1f : 0f,
                    "ge" => a >= b ? 1f : 0f,
                    "and" => a != 0f && b != 0f ? 1f : 0f,
                    "or" => a != 0f || b != 0f ? 1f : 0f,
                    "rand" => IRandom.Instance.Next((int)Math.Min(a, b), (int)Math.Max(a, b) + 1),
                    _ => 0f
                };
            default: return 0f;
        }
    }
}
