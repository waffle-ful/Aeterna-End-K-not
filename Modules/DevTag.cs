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

        int n = text.Length;
        for (int i = 0; i < n; i++)
        {
            float hue = n <= 1 ? 0f : (float)i / n; // sweep hue across the letters
            Color c = Color.HSVToRGB(hue, 0.85f, 1f);
            sb.Append($"<color=#{ColorUtility.ToHtmlStringRGB(c)}>{text[i]}</color>");
        }

        sb.Append("<color=#ff2d2d>★</color></size></font>\r\n");
        return sb.ToString();
    }
}
