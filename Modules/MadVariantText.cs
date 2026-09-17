using System.Text.RegularExpressions;

namespace EndKnot;

// マッドメイトが付いたときだけ差し替わる役職説明 ({role}MadInfo / {role}MadInfoLong)。
// キーが無い役職は通常の説明のまま。
public static class MadVariantText
{
    private const string HorrorColor = "#c00000";
    private static readonly Regex ColorTagRegex = new(@"</?color(=[^>]*)?>|<#[0-9a-fA-F]{3,8}>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // 英語は全キーが揃っている前提 (他言語は GetString が英語へフォールバックする)。
    public static bool Has(CustomRoles role) => Translator.HasTranslation($"{role}MadInfoLong", SupportedLangs.English);

    public static string Get(CustomRoles role, bool infoLong)
    {
        string text = Translator.GetString(infoLong ? $"{role}MadInfoLong" : $"{role}MadInfo");
        if (infoLong) text = text.FixRoleName(role);
        return Horrorize(text);
    }

    public static string Title(CustomRoles role) => $"<color={HorrorColor}>{Translator.GetString(role.ToString())}</color>";

    // 色指定を外して全体を暗い赤で包む。非モッド客向けのチャットは行の切れ目で分割され、
    // 名前欄には "):\n" より後ろだけが載るので、行ごとに赤を付け直す ("):\n" の並びは崩さない)。
    // 公式鯖では 1 通あたりの数字が 5 個までで、超えると数字基準の分割が入り、後続の分割片で見出しが外れたり
    // タグの途中で切れたりする。文言側は全文の数字をタグ込み 5 個以下に保つ。
    private static string Horrorize(string text)
    {
        var open = $"<color={HorrorColor}>";
        string[] lines = ColorTagRegex.Replace(text, string.Empty).Split('\n');
        var sb = new System.Text.StringBuilder(open).Append(lines[0]);

        // 空行には何も足さない: 呼び出し側が "\n\n" で段落を切り出すため。
        for (var i = 1; i < lines.Length; i++)
        {
            sb.Append('\n');
            if (lines[i].Length > 0) sb.Append("</color>").Append(open).Append(lines[i]);
        }

        return sb.Append("</color>").ToString();
    }
}
