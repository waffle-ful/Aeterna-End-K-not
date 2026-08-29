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

    // Wave 3 (docs/ekn-wave3-contract.md §1.2): on_var 専用の必須フィールド。監視する変数名。
    // 他イベントでは null。
    public string VarName;

    // Wave 3 (§1.2 / §1.3): on_var と on_alive_count の必須フィールド。比較演算子 (EkrValueCmps) と閾値。
    // 他イベントでは Cmp == null。閾値は整数リテラルのみ (式は受理しない — 動く閾値は if で組む)。
    public string Cmp;
    public int CmpValue;

    // Wave 3 (§1.1): このルールが「じょうたいトリガ」(エッジ発火エンジンが管理するもの) か。
    // パース時に確定する導出値 — 呼び出し元 (EkrManager) が武装配列を組むときの唯一の判定基準。
    public bool IsStateTrigger;

    // Wave 4 (docs/ekn-wave4-contract.md §1): on_near / on_far 専用フィールド。radius は両者必須
    // ("small" | "medium" | "large")。Who は on_near では任意 (欠落 = "anyone" をパース時に焼き込む —
    // notify.target の既定 self と同じ方式)・on_far では必須 ("linked" | "saved1" | "saved2"、
    // "anyone" は文書 reject)。他イベントでは両方とも null。
    public string Radius;
    public string Who;
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

    // Wave 5 (docs/ekn-wave5-contract.md §1 effect_give): かける効果の種類 ("haste" | "slow" | "freeze" | "blind")。
    public string EffectKind;

    // Wave 6 (docs/ekn-wave6-contract.md §1 cno_launch): とばす向き ("move" | "ctx" | "marker1".."marker4") と
    // 速さ tier ("slow" | "medium" | "fast")。速さは任意フィールドで、省略時はパース時に "medium" を焼き込む
    // (notify.target の既定 self と同じ方式 — 実行側で既定値を再解釈しない)。
    public string LaunchDir;
    public string LaunchSpeed;

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
        "on_meeting_vote", "on_meeting_pick", // Wave 2 (docs/ekn-wave2-contract.md §1)
        // Wave 3 (docs/ekn-wave3-contract.md §1): じょうたいトリガ2種 + ベント退出。
        "on_var", "on_alive_count", "on_vent_exit",
        // Wave 4 (docs/ekn-wave4-contract.md §0): 対人近接2種 + 部屋2種 + リンク死。
        "on_near", "on_far", "on_room_enter", "on_room_exit", "on_linked_death",
        // Wave 6 (docs/ekn-wave6-contract.md §2,§3): サボタージュ成立 (グローバル・ctx=起こした人) と
        // 蘇生 (ホルダー限定・ctx 無し — 蘇生させた人は RpcRevive のシグネチャに存在しないため渡せない)。
        "on_sabotage", "on_revive"
    ];

    // Wave 3 (§1.2): 比較演算子。**綴りは ExprKinds の流用** (新語彙を作らない)。
    // ⚠️ TS 側 (editor/src/roledef.ts) と同じ並び・同じ綴りを保つこと。
    public static readonly string[] EkrValueCmps = ["eq", "le", "ge"];

    // Wave 3 (§1.1): エッジ発火エンジンが武装状態を管理するイベント。
    public static bool IsStateTriggerEvent(string when) => when is "on_var" or "on_alive_count";

    // R2 (docs/ekn-r2-contract.md §3b): on_attacked の種別と on_death の死因バケット。
    // ⚠️ TS 側 (editor/src/roledef.ts) と同じ並び・同じ綴りを保つこと (drift 検出は共有 fixture)。
    public static readonly string[] EkrAttackKinds = ["kill", "indirect", "force", "guess"];

    public static readonly string[] EkrDeathCauses = ["kill", "vote", "guess", "bomb", "poison-curse", "environment", "suicide", "other"];

    // rule 直下の任意フィルタ (kind/cause) の読み取り。指定できるイベントが決まっており、
    // 他イベントに置かれていたら slot と同じく reject する (静的に検査できるものは no-op でなく reject)。
    // Wave 4 (docs/ekn-wave4-contract.md §3.3): cause は on_death と on_linked_death の2イベントで
    // 受理するため、対象イベントは単数でなく配列で受ける。
    private static bool TryReadRuleFilter(JsonElement ruleEl, string when, string field, string[] onlyEvents, string[] allowed, out string value, out string error)
    {
        value = null;
        error = null;

        if (!ruleEl.TryGetProperty(field, out JsonElement el)) return true;

        if (Array.IndexOf(onlyEvents, when) < 0)
        {
            error = $"when=\"{when}\" の rule に {field} は指定できません ({string.Join(" / ", onlyEvents)} 専用です)";
            return false;
        }

        if (el.ValueKind != JsonValueKind.String)
        {
            error = $"{when} の {field} は文字列で指定してください";
            return false;
        }

        string raw = el.GetString();

        if (Array.IndexOf(allowed, raw) < 0)
        {
            error = $"{when} の {field}=\"{raw}\" は未対応です (使えるのは {string.Join(" / ", allowed)})";
            return false;
        }

        value = raw;
        return true;
    }

    // Wave 4 (docs/ekn-wave4-contract.md §1/§6): 対人近接 (on_near/on_far) の rule 直下フィールド。
    // radius は両イベントの必須。who は on_near では任意 (既定 "anyone")・on_far では必須かつ
    // "anyone" 不可 (「知らない誰かが遠くにいる」は常時成立で意味を持たない — 契約 §1.3)。
    // 他イベントに付いていたら on_cno_touch の slot と同じく文書 reject する (対称検査)。
    // ⚠️ TS 側 (editor/src/roledef.ts) と同じ並び・同じ綴りを保つこと (drift 検出は共有 fixture)。
    public static readonly string[] EkrProximityRadii = ["small", "medium", "large"];
    public static readonly string[] EkrNearWhoValues = ["anyone", "linked", "saved1", "saved2"];
    public static readonly string[] EkrFarWhoValues = ["linked", "saved1", "saved2"];

    private static bool TryReadProximityFields(JsonElement ruleEl, string when, out string radius, out string who, out string error)
    {
        radius = null;
        who = null;
        error = null;

        if (when is not "on_near" and not "on_far")
        {
            if (ruleEl.TryGetProperty("radius", out _))
            {
                error = $"when=\"{when}\" の rule に radius は指定できません (on_near / on_far 専用です)";
                return false;
            }

            if (ruleEl.TryGetProperty("who", out _))
            {
                error = $"when=\"{when}\" の rule に who は指定できません (on_near / on_far 専用です)";
                return false;
            }

            return true;
        }

        if (!ruleEl.TryGetProperty("radius", out JsonElement radiusEl) || radiusEl.ValueKind != JsonValueKind.String || Array.IndexOf(EkrProximityRadii, radiusEl.GetString()) < 0)
        {
            error = $"{when} の radius が不正です (使えるのは {string.Join(" / ", EkrProximityRadii)})";
            return false;
        }

        radius = radiusEl.GetString();

        if (when == "on_near")
        {
            if (!ruleEl.TryGetProperty("who", out JsonElement whoEl))
            {
                who = "anyone"; // 欠落 = anyone (契約 §1.2 — 既定値はパース時に焼き込む)
                return true;
            }

            if (whoEl.ValueKind != JsonValueKind.String || Array.IndexOf(EkrNearWhoValues, whoEl.GetString()) < 0)
            {
                error = $"on_near の who が不正です (使えるのは {string.Join(" / ", EkrNearWhoValues)})";
                return false;
            }

            who = whoEl.GetString();
            return true;
        }

        // on_far: who は必須・"anyone" は文書 reject (契約 §1.3)。
        if (!ruleEl.TryGetProperty("who", out JsonElement farWhoEl) || farWhoEl.ValueKind != JsonValueKind.String || Array.IndexOf(EkrFarWhoValues, farWhoEl.GetString()) < 0)
        {
            error = $"on_far の who が不正です (使えるのは {string.Join(" / ", EkrFarWhoValues)})";
            return false;
        }

        who = farWhoEl.GetString();
        return true;
    }

    // Wave 3 (docs/ekn-wave3-contract.md §1.2/§1.3): じょうたいトリガの rule 直下フィールドの読み取り。
    // `var` は on_var のみ・`cmp`/`value` は on_var と on_alive_count のみ (どちらも必須)。
    // 他イベントに付いていたら on_cno_touch の slot と同じく文書 reject する
    // (静的に検査できるものは no-op でなく reject — spec §1 の総則)。
    private static bool TryReadStateTriggerFields(JsonElement ruleEl, string when, HashSet<string> knownVars, out string varName, out string cmp, out int cmpValue, out string error)
    {
        varName = null;
        cmp = null;
        cmpValue = 0;
        error = null;

        if (when == "on_var")
        {
            if (!ruleEl.TryGetProperty("var", out JsonElement varEl) || varEl.ValueKind != JsonValueKind.String)
            {
                error = "on_var の rule には var (みはる変数の名前) が必要です";
                return false;
            }

            // 宣言側 (TryParseInner) と同じ trim 慣行で照合する (spec §1 2026-08-09 裁定)。
            varName = (varEl.GetString() ?? "").Trim();

            if (!knownVars.Contains(varName))
            {
                error = $"未定義の変数を参照しています ({varName})";
                return false;
            }
        }
        else if (ruleEl.TryGetProperty("var", out _))
        {
            error = $"when=\"{when}\" の rule に var は指定できません (on_var 専用です)";
            return false;
        }

        bool wantsCmp = IsStateTriggerEvent(when);

        if (!wantsCmp)
        {
            if (ruleEl.TryGetProperty("cmp", out _))
            {
                error = $"when=\"{when}\" の rule に cmp は指定できません (on_var / on_alive_count 専用です)";
                return false;
            }

            if (ruleEl.TryGetProperty("value", out _))
            {
                error = $"when=\"{when}\" の rule に value は指定できません (on_var / on_alive_count 専用です)";
                return false;
            }

            return true;
        }

        if (!ruleEl.TryGetProperty("cmp", out JsonElement cmpEl) || cmpEl.ValueKind != JsonValueKind.String || Array.IndexOf(EkrValueCmps, cmpEl.GetString()) < 0)
        {
            error = $"{when} の cmp が不正です (使えるのは {string.Join(" / ", EkrValueCmps)})";
            return false;
        }

        cmp = cmpEl.GetString();

        // 契約 §1.2: value は整数リテラルのみ (式は受理しない — 動く閾値は作者が if で組む)。
        if (!ruleEl.TryGetProperty("value", out JsonElement valueEl) || !EkrJson.TryReadInt(valueEl, out cmpValue))
        {
            error = $"{when} の value が不正です (整数で指定してください)";
            return false;
        }

        // 契約 §1.3: 生存人数だけ 1..15 の範囲を持つ。**on_var の value に範囲は無い** (変数の値域その
        // ものなので契約が範囲を定めていない — ここに独自の上下限を足すと TS 側と非対称になる)。
        if (when == "on_alive_count" && cmpValue is < 1 or > 15)
        {
            error = "on_alive_count の value が範囲外です (1〜15)";
            return false;
        }

        return true;
    }

    // Wave 1 (spec §3 統一セレクタ語彙)。単数セレクタのみ受理する op (kill/teleport_other/remember 等) と、
    // 複数セレクタも受理する明示ホワイトリスト op (Wave 1 では notify だけ) を分ける。
    // Wave 4 (docs/ekn-wave4-contract.md §3.4): "linked" (つないだ人) を追加 — 「saved1/saved2 が受理される
    // すべての箇所」= SingleSelectors / OtherSelectors / MultiSelectors (TS 側は TARGET_SINGLE_VALUES からの
    // 導出で notify も構造的に受理するため、こちらも追加しないと検証が非対称になる)。at/to (空間セレクタ)
    // には追加しない (§3.4 明示除外 — 人参照であって位置参照ではない)。
    // ⚠️ TS 側 (editor/src/roledef.ts) と同じ並び ("ctx" の直後に "linked")・同じ綴りを保つこと。
    private static readonly string[] SingleSelectors = ["self", "ctx", "linked", "saved1", "saved2", "nearest", "random"];
    private static readonly string[] MultiSelectors = ["self", "ctx", "linked", "saved1", "saved2", "nearest", "random", "all", "room"];

    // teleport_other.target は「他人」を動かす op なので self を含まない (自分は teleport の役目・spec §3)。
    private static readonly string[] OtherSelectors = ["ctx", "linked", "saved1", "saved2", "nearest", "random"];

    // Wave 4 (契約 §3.1): link.target は self 不可かつ linked 不可 (「つないだ人と つなぐ」は恒等)。
    // OtherSelectors は Wave 4 で linked を含むため専用リストにする。
    private static readonly string[] LinkTargetSelectors = ["ctx", "saved1", "saved2", "nearest", "random"];

    // Wave 5 (docs/ekn-wave5-contract.md §1): effect_give の効果種別と kind 別の秒数上限。
    // freeze だけ上限が短い (移動権の剥奪なので drag の 1..10 と同格 — 契約 §1)。
    // ⚠️ TS 側 (editor/src/roledef.ts) と同じ並び・同じ綴りを保つこと (drift 検出は共有 fixture)。
    public static readonly string[] EkrEffectKinds = ["haste", "slow", "freeze", "blind"];

    // Wave 6 (docs/ekn-wave6-contract.md §1): cno_launch の dir / speed 受理値。
    // ⚠️ TS 側 (editor/src/roledef.ts) と同じ並び・同じ綴りを保つこと (drift 検出は共有 fixture)。
    public static readonly string[] EkrLaunchDirs = ["move", "ctx", "marker1", "marker2", "marker3", "marker4"];

    public static readonly string[] EkrLaunchSpeeds = ["slow", "medium", "fast"];

    public static float EffectMaxSeconds(string kind) => kind == "freeze" ? 10f : 30f;

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
        "cancel_vote", "vote_weight_set", "vote_block", "vote_swap", "exile",
        // Wave 4 (docs/ekn-wave4-contract.md §3,§4): リンクと変換
        "link", "unlink", "recruit",
        // Wave 5 (docs/ekn-wave5-contract.md §1): 持続効果
        "effect_give",
        // Wave 6 (docs/ekn-wave6-contract.md §1): 発射体プリミティブ
        "cno_launch",
        // Wave 7 (docs/ekn-wave7-contract.md §1,§2): 勝利条件
        "win", "win_join"
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
            // Wave 4 (契約 §3.3): cause は on_linked_death でも受理する (同じ 8 バケット・同じ任意性 —
            // DeathCauseBucket は FireDeath で計算済みなので増分ゼロ)。
            if (!TryReadRuleFilter(ruleEl, when, "kind", ["on_attacked"], EkrAttackKinds, out string ruleKind, out error)) return false;
            if (!TryReadRuleFilter(ruleEl, when, "cause", ["on_death", "on_linked_death"], EkrDeathCauses, out string ruleCause, out error)) return false;

            // Wave 4 (契約 §1/§6): on_near/on_far の radius/who (他イベントへの付着 reject を含む)。
            if (!TryReadProximityFields(ruleEl, when, out string ruleRadius, out string ruleWho, out error)) return false;

            // Wave 3 (契約 §1.2/§1.3): じょうたいトリガの必須フィールド。slot と同じ厳格側 —
            // 付ける場所を間違えたら「静かに効かない」ではなく文書 reject にする。
            if (!TryReadStateTriggerFields(ruleEl, when, knownVarNames, out string ruleVar, out string ruleCmp, out int ruleCmpValue, out error))
                return false;

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

            parsed.Rules.Add(new EkrRule
            {
                When = when,
                Do = doNodes,
                Slot = ruleSlot,
                Kind = ruleKind,
                Cause = ruleCause,
                VarName = ruleVar,
                Cmp = ruleCmp,
                CmpValue = ruleCmpValue,
                IsStateTrigger = IsStateTriggerEvent(when),
                Radius = ruleRadius,
                Who = ruleWho
            });
        }

        def = parsed;
        return true;
    }

    // cancel_attack のスコープ検証用の再帰探索 (if の then/else も潜る)。深さは既に MaxDepth で
    // 制限済みなので追加のガードは不要。
    private static bool ContainsCancelAttack(List<EkrNode> nodes) => ContainsOp(nodes, "cancel_attack");

    // Wave 7 (docs/ekn-wave7-contract.md §1): 「かちにする」(win) の neutral スロット限定検査を
    // EkrDefinition.Validate 側で行うための露出。team は文書レベルの情報でパーサからは見えない。
    public bool ContainsWinOp()
    {
        foreach (EkrRule rule in Rules)
            if (ContainsOp(rule.Do, "win"))
                return true;

        return false;
    }

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

            // ── Wave 4 (docs/ekn-wave4-contract.md §3,§4): リンクと変換 ─────────────────────────

            // §3.1: self 不可・linked 不可 (LinkTargetSelectors 参照)。
            case "link":
                if (!TryGetEnum(nodeEl, "target", LinkTargetSelectors, out n.Target, out err)) return false;
                break;

            // §3.2: 引数なし。
            case "unlink":
                break;

            // §4: self 不可・linked 可 (OtherSelectors = Wave 4 の受理集合そのもの)。
            // Wave 5 (docs/ekn-wave5-contract.md §2): 任意の slot (1..18) で変換先スロットを指名できる。
            // 省略 = 自分と同じ役職 (完全後方互換)。CNO slot (1..3) と意味論が別物なので Slot ではなく
            // IntArg に入れる (0 = 省略)。
            case "recruit":
                if (!TryGetEnum(nodeEl, "target", OtherSelectors, out n.Target, out err)) return false;

                if (nodeEl.TryGetProperty("slot", out _))
                {
                    if (!TryGetInt(nodeEl, "slot", 1, 18, out n.IntArg, out err)) return false;
                }

                break;

            // ── Wave 5 (docs/ekn-wave5-contract.md §1): 持続効果 ────────────────────────────────

            // §1: target/kind/seconds すべて必須 (既定を作らない — 「相手にかける」が本義)。
            // seconds の上限は kind 別 (freeze ≤10 / 他 ≤30)。
            case "effect_give":
                if (!TryGetEnum(nodeEl, "target", SingleSelectors, out n.Target, out err)) return false;
                if (!TryGetEnum(nodeEl, "kind", EkrEffectKinds, out n.EffectKind, out err)) return false;
                if (!TryGetFloat(nodeEl, "seconds", out float effectSec, out err)) return false;

                if (effectSec < 1f || effectSec > EffectMaxSeconds(n.EffectKind))
                {
                    err = $"effect_give の秒数が範囲外です ({n.EffectKind} は 1〜{EffectMaxSeconds(n.EffectKind)} 秒)";
                    return false;
                }

                n.Seconds = effectSec;
                break;

            // ── Wave 6 (docs/ekn-wave6-contract.md §1): 発射体プリミティブ ──────────────────────

            // §1: slot (1..3) と dir は必須・speed は任意 (省略 = "medium" を焼き込む = 正準形で
            // 書き出さない側と対応する)。
            case "cno_launch":
                if (!TryGetInt(nodeEl, "slot", 1, 3, out n.Slot, out err)) return false;
                if (!TryGetEnum(nodeEl, "dir", EkrLaunchDirs, out n.LaunchDir, out err)) return false;

                n.LaunchSpeed = "medium";
                if (nodeEl.TryGetProperty("speed", out _) && !TryGetEnum(nodeEl, "speed", EkrLaunchSpeeds, out n.LaunchSpeed, out err)) return false;

                break;

            // ── Wave 7 (docs/ekn-wave7-contract.md §1,§2): 勝利条件 ─────────────────────────────

            // §1/§2: target は任意 (既定 "self"・self 可)。受理値は SingleSelectors 全種 (複数形は
            // 単発強効果 op の型規律どおり reject)。win の neutral スロット限定 (§1) はここでは見ない —
            // team は文書レベルの情報なので EkrDefinition.Validate 側が ContainsWinOp で reject する。
            case "win":
            case "win_join":
                if (nodeEl.TryGetProperty("target", out _))
                {
                    if (!TryGetEnum(nodeEl, "target", SingleSelectors, out n.Target, out err)) return false;
                }
                else n.Target = "self";

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

    // Wave 3 (docs/ekn-wave3-contract.md §1.1): じょうたいトリガ (on_var/on_alive_count) 起点の fiber か。
    // kill 連鎖ガードと同型の per-fiber 焼き込み — この fiber が行った変数書込みは「武装状態の遷移は
    // 起こすが新規発火は生まない」(ピンポン構造の排除・深さ1)。
    public bool FromVarChain;

    // Wave 3 (§1.1): 前回のドレイン以降にこの fiber が書き換えた変数名。呼び出し元 (EkrManager) が
    // Pump 直後に回収して空にする — 「fiber にフラグを載せ呼び出し元が読む」慣行 (FromKillChain/Aborted
    // と同型) で、汎用エンジンに「エッジ発火」という役職語彙を持ち込まないための境界。
    // ⚠️ 変数への書込み経路をこの先で増やすときは、必ずここへの記録もセットで行うこと
    // (記録を漏らすとその書込みだけ on_var が無音で鳴らなくなる)。現在の書込み口は var_set / var_add の
    // 2つだけ (+ 初期値の焼き込みは EkrManager.InitRuntime — こちらは「遷移ではない」ので記録しない)。
    public readonly HashSet<string> WrittenVars = [];

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

    public static EkrFiber Spawn(IReadOnlyList<EkrNode> nodes, Dictionary<string, float> variables, object context, bool fromKillChain, bool fromVarChain = false)
    {
        var fiber = new EkrFiber { Variables = variables, Context = context, FromKillChain = fromKillChain, FromVarChain = fromVarChain };

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
                    fiber.WrittenVars.Add(node.VarName); // Wave 3: エッジ発火の書込み記録 (EkrFiber.WrittenVars 参照)
                    continue;

                case "var_add":
                    fiber.Variables[node.VarName] = fiber.Variables.GetValueOrDefault(node.VarName) + Eval(node.Value, fiber.Variables);
                    fiber.WrittenVars.Add(node.VarName);
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

    // Wave 3 (docs/ekn-wave3-contract.md §1.2): じょうたいトリガの条件評価。等値は Eval の "eq" と
    // 同じ素の float 比較にする (変数は整数運用が前提で、engine 全体で許容誤差を持たない規約に揃える)。
    public static bool CompareValue(float actual, string cmp, int threshold)
    {
        return cmp switch
        {
            "eq" => actual == threshold,
            "le" => actual <= threshold,
            "ge" => actual >= threshold,
            _ => false
        };
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
