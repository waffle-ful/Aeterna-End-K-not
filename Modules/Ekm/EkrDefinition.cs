using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EndKnot.Modules.Ekm;

// R2 (docs/ekn-r2-contract.md §1): 役職コードが名乗れる陣営。madmate / coven は R2 対象外 (受理しない)。
// neutral のサブカテゴリ (Killing/Benign) は canKill から導出するので、ここには出さない。
public enum EkrTeam
{
    Crewmate,
    Impostor,
    Neutral
}

// EKN 役職コードのデータ契約 (R0: フォーム式テンプレートのみ・ロジック無し)。
// 計画正典: docs/ekn-api-plan.md。EHR 内部語彙 (enum 名/option ID/RPC 番号) を一切含まないこと。
public sealed class EkrDefinition
{
    // R1 契約解決 (docs/ekr-logic-spec.md §1): ekr キーは必須。既定値 1 の初期化子だと「省略」と
    // 「明示的に1」を区別できないため nullable にする (R0 は accept していたが、これは意図的な修正)。
    [JsonPropertyName("ekr")]
    public int? Ekr { get; set; }

    // R1 (docs/ekr-logic-spec.md)。任意 — 無しは R0 動作 (完全後方互換)。C# は生 JsonElement を保持し、
    // Validate() 内で EkrLogicDef.TryParse に渡して初めて型チェックする (blockly は不透明— パースしない)。
    [JsonPropertyName("logic")]
    public JsonElement? Logic { get; set; }

    // Validate() 成功後にのみ非 null になる、logic の検証済み AST。実行時 (EkrManager) はこちらだけを読む。
    [JsonIgnore]
    public EkrLogicDef ParsedLogic { get; private set; }

    // Wave 1 (docs/ekr-logic-spec.md §1.1): パッシブ層。logic と並置され、logic 無しでも passives 単独で有効。
    // 生 JsonElement を保持し Validate() 内で EkrPassives.TryParse に渡す (logic と同じ作法)。
    [JsonPropertyName("passives")]
    public JsonElement? Passives { get; set; }

    // Validate() 成功後の検証済みパッシブ。passives 未指定でも共有の既定インスタンスが入るので
    // 消費側 (EkrManager/EkmTemplateRole) は null チェック不要。
    [JsonIgnore]
    public EkrPassives ParsedPassives { get; private set; } = EkrPassives.Default;

    [JsonPropertyName("requires")]
    public List<string> Requires { get; set; } = [];

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("author")]
    public string Author { get; set; } = "";

    // plan §7 Tier 1 #2: 役職の説明文。任意 — 空なら lang の既定文言 (スロット共通の案内文) がそのまま出る
    // (完全後方互換)。短文は頂上の役職パネル/イントロ (Info キー)、詳細文は /h r やオプションメニュー
    // (InfoLong キー) に出る — どちらも ExtendedPlayerControl.GetRoleInfo が読む2キーへ束縛時に上書きする。
    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("descriptionLong")]
    public string DescriptionLong { get; set; } = "";

    [JsonPropertyName("color")]
    public string Color { get; set; } = "#8f8f8f";

    [JsonPropertyName("team")]
    public string Team { get; set; } = "crewmate";

    // Validate() 成功後にのみ意味を持つ、Team の enum 版。⚠️ これは「役職コードが名乗る陣営」であって
    // 「実際に効く陣営」ではない — 実陣営はスロット種 (EkmImpRole*/EkmNeuRole*/EkmCustomRole*) が静的に
    // 決める (EkrManager.GetTeam)。この値は束縛時の一致検証にのみ使うこと (docs/ekn-r2-contract.md §1)。
    [JsonIgnore]
    public EkrTeam ParsedTeam { get; private set; } = EkrTeam.Crewmate;

    [JsonPropertyName("canKill")]
    public bool CanKill { get; set; }

    [JsonPropertyName("killCooldown")]
    public float KillCooldown { get; set; } = 25f;

    [JsonPropertyName("canVent")]
    public bool CanVent { get; set; }

    [JsonPropertyName("visionMultiplier")]
    public float VisionMultiplier { get; set; } = 1f;

    [JsonPropertyName("winCondition")]
    public string WinCondition { get; set; } = "team";

    // 説明文の改行潰し (TS 側 roledef.ts の /[\r\n]+/g と同一規則 — 連続改行は空白1つへ畳む)。
    private static readonly System.Text.RegularExpressions.Regex NewlineRun = new("[\r\n]+");

    // 文字数での切り詰めは TMP タグの途中に落ちうる (`…<color=#ff00` が残ると TMP パーサが後続テキストまで
    // 巻き込んで壊す — イントロの <size=…> 包みや /h r の連結メッセージが実際の巻き添え先)。
    // ⚠️ `CustomNetObject.DropUnterminatedTag` と **同一実装の複製**。テストプロジェクト (EndKnot.Tests) は
    // Unity 依存を避けるため CustomNetObject.cs をコンパイル対象に含めておらず、そちらを参照できない。
    // 片方だけ直すと契約が割れるので、変更するときは必ず両方 + TS の dropUnterminatedTag を揃えること。
    private static string DropUnterminatedTag(string s)
    {
        int lastOpen = s.LastIndexOf('<');
        if (lastOpen < 0) return s;
        return s.IndexOf('>', lastOpen) < 0 ? s[..lastOpen] : s;
    }

    /// <summary>
    /// `{スロット}InfoLong` へ上書きする文字列を組み立てる。**「見出し + 空行 + 本文」の形を必ず作る**こと。
    /// 理由: 非モッド客の画面では役職説明が名前ペイロードに載る (`Utils.SetupLongRoleDescriptions` →
    /// `WriteSetNameRpcsToSender` の `ChangeNameToRoleInfo` 分岐)。そこで使われるのは **最初の `\n\n` の手前だけ**で、
    /// 既存役職の lang InfoLong はどれも「キャッチ文 + 空行 + 本文」構造のおかげで短く収まっている
    /// (ja_JP の 691 件中、この切り出しが 296 字を超えるのは 3 件だけ)。自由記述の詳細文をそのまま渡すと
    /// この構造的マージンが無くなり、日本語で約 235 字を超えたあたりから名前予算 (705B) を食い潰して
    /// **末尾の役職マーク (矢印/♥/メディック印) が無音で切り落とされる**。
    /// 短文が空のときは詳細文の 1 行目 (≤80字) を見出しに使い、構造だけは必ず維持する。
    /// </summary>
    public string BuildInfoLongOverride()
    {
        if (DescriptionLong.Length == 0) return Description;

        string headline = Description;

        if (headline.Length == 0)
        {
            headline = DescriptionLong.Split('\n')[0].Trim();
            if (headline.Length > 80) headline = DropUnterminatedTag(headline[..80]);
        }

        // 見出しが本文そのものなら二重に出さない (改行の無い短い詳細文を書いた場合)。
        return headline.Length == 0 || headline == DescriptionLong ? DescriptionLong : $"{headline}\n\n{DescriptionLong}";
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true
    };

    // R0 が対応する capability の集合 (現状は空 = ロジック等は一切未対応)。
    // ekmap-spec §20.1 と同型の「拒否は平易文言」パターンをそのまま踏襲する。
    private static readonly HashSet<string> SupportedCapabilities = [];

    public static bool TryParse(string json, out EkrDefinition def, out string error)
    {
        def = null;
        error = null;

        try
        {
            def = JsonSerializer.Deserialize<EkrDefinition>(json, JsonOpts);
        }
        catch (Exception ex)
        {
            error = $"役職コードの読み取りに失敗しました ({ex.Message})";
            return false;
        }

        if (def == null)
        {
            error = "役職コードの中身が空です";
            return false;
        }

        return def.Validate(out error);
    }

    // R0 の受け入れ基準に沿った検証。失敗時は「なぜダメか」を日本語の平易文で返す。
    public bool Validate(out string error)
    {
        error = null;

        if (Ekr is null)
        {
            error = "役職コードに ekr がありません (新しい形式の役職コードではないか、コードが壊れています)";
            return false;
        }

        if (Ekr != 1)
        {
            error = $"このバージョンの End K not では読み込めない役職コードです (ekr={Ekr})。End K not を更新してください";
            return false;
        }

        Requires ??= [];
        foreach (string cap in Requires)
        {
            if (!SupportedCapabilities.Contains(cap))
            {
                error = $"この役職コードは未対応の機能 ({cap}) を必要としています。End K not を更新するか、対応済みの役職コードを使ってください";
                return false;
            }
        }

        Name = (Name ?? "").Trim();
        if (Name.Length == 0)
        {
            error = "役職コードに名前 (name) がありません";
            return false;
        }
        if (Name.Length > 24) Name = Name[..24];

        Author = (Author ?? "").Trim();
        if (Author.Length > 24) Author = Author[..24];

        // 説明文 (plan §7 Tier 1 #2)。上限は roledef.ts の ROLE_DESCRIPTION_MAX / ROLE_DESCRIPTION_LONG_MAX と同値。
        // 短文は1行表示の場所 (頂上パネル/イントロ) に出るので改行を空白へ潰す (連続改行は空白1つ = TS 側と同一規則)。
        // 詳細文は改行をそのまま許す (CRLF だけ LF へ正規化)。
        Description = NewlineRun.Replace(Description ?? "", " ").Trim();
        if (Description.Length > 80) Description = DropUnterminatedTag(Description[..80]);

        DescriptionLong = (DescriptionLong ?? "").Replace("\r\n", "\n").Trim();
        if (DescriptionLong.Length > 400) DescriptionLong = DropUnterminatedTag(DescriptionLong[..400]);

        // エディタ側 (roledef.ts の COLOR_RE) と同じ「# + 6桁16進」のみ受理し、# 無し入力は # 付きへ正規化して
        // 格納する。ColorUtility (Unity) を使うと 3/4/8桁・named color まで通ってしまい、TS 側との契約が割れる上、
        // 生文字列のまま <color=...> タグへ埋め込まれる消費箇所 (Utils.GetRoleColorCode) の表示保証もできない。
        Color = (Color ?? "").Trim();
        if (!Color.StartsWith('#')) Color = $"#{Color}";
        if (!System.Text.RegularExpressions.Regex.IsMatch(Color, "^#[0-9a-fA-F]{6}$"))
            Color = "#8f8f8f";

        Team = (Team ?? "crewmate").Trim().ToLowerInvariant();
        // R2 (docs/ekn-r2-contract.md §1): 3値を受理する。R0/R1 が impostor/neutral を拒否していたのは
        // 陣営判定の静的 switch を動的束縛で安全に動かせなかったため — R2 では「陣営はスロット種が静的に
        // 決める」形にしたので解消済み (陣営別スロット EkmImpRole*/EkmNeuRole* が受け皿)。
        // madmate / coven は R2 対象外。
        switch (Team)
        {
            case "crewmate": ParsedTeam = EkrTeam.Crewmate; break;
            case "impostor": ParsedTeam = EkrTeam.Impostor; break;
            case "neutral": ParsedTeam = EkrTeam.Neutral; break;
            default:
                error = $"team=\"{Team}\" は使えません (使えるのは crewmate / impostor / neutral の3つです)";
                return false;
        }

        KillCooldown = Math.Clamp(float.IsFinite(KillCooldown) ? KillCooldown : 25f, 1f, 180f);
        VisionMultiplier = Math.Clamp(float.IsFinite(VisionMultiplier) ? VisionMultiplier : 1f, 0.25f, 5f);

        WinCondition = (WinCondition ?? "team").Trim().ToLowerInvariant();
        // team 以外の値は受理はするが R0 では常に通常のクルー勝利条件にフォールバックする (機能未対応)。

        // R1: logic は任意。無ければ R0 動作のまま (ParsedLogic は null)。
        if (Logic.HasValue)
        {
            if (!EkrLogicDef.TryParse(Logic.Value, out EkrLogicDef parsedLogic, out error))
                return false;

            ParsedLogic = parsedLogic;
        }

        // Wave 1: passives も任意。無ければ EkrPassives.Default のまま (全キー既定 = 何も変えない)。
        if (Passives.HasValue)
        {
            if (!EkrPassives.TryParse(Passives.Value, out EkrPassives parsedPassives, out error))
                return false;

            ParsedPassives = parsedPassives;
        }

        return true;
    }
}
