using System.Text;
using UnityEngine;

namespace EndKnot;

// Builds the upstream-style developer tag: VCR font + per-letter static rainbow + red star.
// Static string (no per-frame recolor) so it rides the existing name path without extra RpcSetName traffic.
public static class DevTag
{
    private const string TagText = "Developer";

    private static string _cachedLocalDev;

    // Shared tag rendered above every local-dev friend code's name.
    public static string LocalDev => _cachedLocalDev ??= BuildRainbowTag(TagText);

    public static string BuildRainbowTag(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var sb = new StringBuilder();
        sb.Append("<font=\"VCR SDF\" material=\"VCR Black Outline\"><size=1.7>");
        AppendRainbow(sb, text);
        sb.Append("<color=#ff2d2d>★</color></size></font>\r\n");
        return sb.ToString();
    }

    // Renders the name body itself in the same VCR font + static rainbow as the tag (no star, no newline),
    // so a local dev's Among-Us name is overwritten to match the Developer★ look.
    // VCR SDF is Latin-only: any non-ASCII char would tofu, so such names are left undecorated.
    public static string BuildRainbowName(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;

        foreach (char ch in name)
            if (ch > 0x7F) return name;

        var sb = new StringBuilder();
        sb.Append("<font=\"VCR SDF\" material=\"VCR Black Outline\">");
        AppendRainbow(sb, name);
        sb.Append("</font>");
        return sb.ToString();
    }

    // Static (non-animated) per-letter hue sweep — no per-frame recolor, so it adds no RpcSetName traffic.
    private static void AppendRainbow(StringBuilder sb, string text)
    {
        int n = text.Length;
        for (int i = 0; i < n; i++)
        {
            float hue = n <= 1 ? 0f : (float)i / n; // sweep hue across the letters
            Color c = Color.HSVToRGB(hue, 0.85f, 1f);
            sb.Append($"<color=#{ColorUtility.ToHtmlStringRGB(c)}>{text[i]}</color>");
        }
    }
}
