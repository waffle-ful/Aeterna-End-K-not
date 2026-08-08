using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using UnityEngine;

namespace EndKnot.Modules.Ekm;

// EKN 役職コードのデータ契約 (R0: フォーム式テンプレートのみ・ロジック無し)。
// 計画正典: docs/ekn-api-plan.md。EHR 内部語彙 (enum 名/option ID/RPC 番号) を一切含まないこと。
public sealed class EkrDefinition
{
    [JsonPropertyName("ekr")]
    public int Ekr { get; set; } = 1;

    [JsonPropertyName("requires")]
    public List<string> Requires { get; set; } = [];

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("author")]
    public string Author { get; set; } = "";

    [JsonPropertyName("color")]
    public string Color { get; set; } = "#8f8f8f";

    [JsonPropertyName("team")]
    public string Team { get; set; } = "crewmate";

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

        if (string.IsNullOrWhiteSpace(Color) || !ColorUtility.TryParseHtmlString(Color.StartsWith('#') ? Color : $"#{Color}", out _))
            Color = "#8f8f8f";

        Team = (Team ?? "crewmate").Trim().ToLowerInvariant();
        // R0 スコープ: Crewmate 非キル系を優先して動作検証する決定 (ekn-api-plan §5) に基づき、
        // team=impostor/neutral は「フィールドとしては受け付けるが機能未対応」としてこの場で拒否する。
        // 理由: IsImpostor()/IsNeutral() 等の陣営判定は CustomRoles enum 静的 switch (CustomRolesHelper.cs) で、
        // ロビー確定後の動的束縛を安全に反映できない箇所がある (勝敗判定 GetCountTypes 等)。
        if (Team != "crewmate")
        {
            error = $"この End K not のバージョンでは team=\"{Team}\" の役職コードにはまだ対応していません (現在は team=\"crewmate\" のみ対応)";
            return false;
        }

        KillCooldown = Math.Clamp(float.IsFinite(KillCooldown) ? KillCooldown : 25f, 1f, 180f);
        VisionMultiplier = Math.Clamp(float.IsFinite(VisionMultiplier) ? VisionMultiplier : 1f, 0.25f, 5f);

        WinCondition = (WinCondition ?? "team").Trim().ToLowerInvariant();
        // team 以外の値は受理はするが R0 では常に通常のクルー勝利条件にフォールバックする (機能未対応)。

        return true;
    }
}
