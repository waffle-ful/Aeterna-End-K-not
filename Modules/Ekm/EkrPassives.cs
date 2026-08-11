using System.Text.Json;

namespace EndKnot.Modules.Ekm;

// EKR 契約の JSON 読み取り補助 (EkrPassives / EkmLogicRuntime の両方から使う)。
// ⚠ UnityEngine 非依存を維持すること (EkrDefinition と同じ理由 — テストプロジェクトへ組込可能に保つ)。
internal static class EkrJson
{
    // spec §1「整数フィールドの数値トークン (2026-08-11 裁定)」: 小数点/指数表記でも値が整数と
    // 等価なら受理する (`2.0` = `2`)。TS は JSON.parse 後にトークン表記を区別できず構造的に受理するため、
    // C# 側を合わせるのが唯一の整合方向 — TryGetInt32 単独だと `2.0` で C# だけが reject して契約が割れる。
    public static bool TryReadInt(JsonElement el, out int value)
    {
        value = 0;

        if (el.ValueKind != JsonValueKind.Number) return false;
        if (el.TryGetInt32(out value)) return true;

        if (!el.TryGetDouble(out double d) || double.IsNaN(d) || double.IsInfinity(d)) return false;
        if (d != System.Math.Floor(d) || d < int.MinValue || d > int.MaxValue) return false;

        value = (int)d;
        return true;
    }
}

// EKR パッシブ層 (Wave 1・契約正典: docs/ekr-logic-spec.md §1.1)。
// `.ekrole.json` のトップレベル固定キーオブジェクト `passives` — 「when の無いブロック」ではなく
// フォーム+固定キーなので、順序・重複・参照整合性の問題が構造的に存在しない。
//
// ⚠ このファイルは EkrDefinition.cs と同じく **UnityEngine 非依存** に保つこと
// (2026-08-09 の完成前監査で EkrDefinition を Unity 非依存にして EndKnot.Tests へ組込可能にした経緯)。
// 実行時の適用 (AllPlayerSpeed / ApplyGameOptions / 死体 / 票 / doom) は EkrManager 側が行う。
public sealed class EkrPassives
{
    // 「passives 無し」と「全キー既定値」を同一に扱えるよう、共有の読み取り専用既定インスタンスを持つ。
    public static readonly EkrPassives Default = new();

    // いつものはやさ (0.5..3.0・既定 1.0 = 無効)
    public float SpeedMult { get; private set; } = 1f;

    // キルできるきょり: vanilla の 0/1/2 へ写像済みの値。-1 = 未指定 (ホスト設定のまま)。
    public int KillDistance { get; private set; } = -1;

    // まもり (さいしょの N 回ふせぐ)。0 = 無効。
    public int ShieldCount { get; private set; }

    // じぶんの死体のあつかい: "normal" | "noReport" | "vanish"
    public string Corpse { get; private set; } = "normal";

    // 票のちから (0..3・既定 1)。0 = 票なし。
    public int VoteWeight { get; private set; } = 1;

    // よわさ (時間がくると死ぬ)。0 = 無効。有効時は 30..600 秒。
    public int DoomSeconds { get; private set; }

    public bool HasSpeed => SpeedMult < 0.999f || SpeedMult > 1.001f;
    public bool HasShield => ShieldCount > 0;
    public bool HasDoom => DoomSeconds > 0;

    // spec §1 総則: 内部の未知キーは黙って無視、既知キーの型不一致・範囲外は文書全体 reject。
    public static bool TryParse(JsonElement root, out EkrPassives passives, out string error)
    {
        passives = null;
        error = null;

        if (root.ValueKind != JsonValueKind.Object)
        {
            error = "とくせい (passives) の形式が不正です";
            return false;
        }

        var p = new EkrPassives();

        if (root.TryGetProperty("speedMult", out JsonElement speedEl))
        {
            if (speedEl.ValueKind != JsonValueKind.Number || !speedEl.TryGetDouble(out double speed) || double.IsNaN(speed) || double.IsInfinity(speed) || speed is < 0.5 or > 3.0)
            {
                error = "とくせいの「いつものはやさ」が範囲外です (0.5〜3.0)";
                return false;
            }

            p.SpeedMult = (float)speed;
        }

        if (root.TryGetProperty("killDistance", out JsonElement kdEl))
        {
            if (kdEl.ValueKind != JsonValueKind.String)
            {
                error = "とくせいの「キルできるきょり」の値が不正です";
                return false;
            }

            switch (kdEl.GetString())
            {
                case "short": p.KillDistance = 0; break;
                case "medium": p.KillDistance = 1; break;
                case "long": p.KillDistance = 2; break;
                default:
                    error = "とくせいの「キルできるきょり」の値が不正です (short / medium / long)";
                    return false;
            }
        }

        if (root.TryGetProperty("shield", out JsonElement shieldEl))
        {
            if (!TryGetNestedInt(shieldEl, "count", 1, 9, out int count))
            {
                error = "とくせいの「まもり」の回数が範囲外です (1〜9)";
                return false;
            }

            p.ShieldCount = count;
        }

        if (root.TryGetProperty("corpse", out JsonElement corpseEl))
        {
            if (corpseEl.ValueKind != JsonValueKind.String)
            {
                error = "とくせいの「じぶんの死体のあつかい」の値が不正です";
                return false;
            }

            string corpse = corpseEl.GetString();

            if (corpse is not ("normal" or "noReport" or "vanish"))
            {
                error = "とくせいの「じぶんの死体のあつかい」の値が不正です (normal / noReport / vanish)";
                return false;
            }

            p.Corpse = corpse;
        }

        if (root.TryGetProperty("voteWeight", out JsonElement voteEl))
        {
            if (!EkrJson.TryReadInt(voteEl, out int vote) || vote is < 0 or > 3)
            {
                error = "とくせいの「票のちから」が範囲外です (0〜3)";
                return false;
            }

            p.VoteWeight = vote;
        }

        if (root.TryGetProperty("doom", out JsonElement doomEl))
        {
            if (!TryGetNestedInt(doomEl, "seconds", 30, 600, out int seconds))
            {
                error = "とくせいの「よわさ」の秒数が範囲外です (30〜600)";
                return false;
            }

            p.DoomSeconds = seconds;
        }

        passives = p;
        return true;
    }

    // shield/doom は `{ "count": N }` / `{ "seconds": N }` のネストしたオブジェクト形 (spec §1.1)。
    private static bool TryGetNestedInt(JsonElement el, string propName, int min, int max, out int value)
    {
        value = 0;

        if (el.ValueKind != JsonValueKind.Object) return false;
        if (!el.TryGetProperty(propName, out JsonElement inner)) return false;
        if (!EkrJson.TryReadInt(inner, out int i) || i < min || i > max) return false;

        value = i;
        return true;
    }
}
