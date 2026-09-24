using UnityEngine;

namespace EndKnot;

// ColorUtility.TryParseHtmlString と同じ結果を返す色文字列の読み取り。
// ゲーム側がエンジンコードを削って配布しているプラットフォームでは ColorUtility のネイティブ実装が
// 存在せず、呼んだ時点で例外になるため、同じ規則をマネージド側で持つ。
//
// 受け付ける形 (Unity 2022.3 の実物と突き合わせて確認済み):
//  - '#' + 16 進 3 / 4 / 6 / 8 桁 (#RGB / #RGBA / #RRGGBB / #RRGGBBAA)。大文字小文字は問わない。
//    3 / 4 桁は各桁を 17 倍して 8 bit に広げる。アルファ省略時は不透明。
//  - 下の表の色名 23 個 (大文字小文字は問わない)。"gray" は受け付けない。
//  - 前後の空白・'#' の無い 16 進・全角文字は受け付けない。null は例外にせず false。
//  - 失敗時の out は白 (1, 1, 1, 1)。
public static class HtmlColor
{
    public static bool TryParse(string text, out Color color)
    {
        if (TryParse32(text, out uint rgba))
        {
            color = new Color((rgba >> 24) / 255f, ((rgba >> 16) & 0xFF) / 255f, ((rgba >> 8) & 0xFF) / 255f, (rgba & 0xFF) / 255f);
            return true;
        }

        color = Color.white;
        return false;
    }

    private static bool TryParse32(string text, out uint rgba)
    {
        rgba = 0;
        if (string.IsNullOrEmpty(text)) return false;
        if (text[0] != '#') return TryNamed(text, out rgba);

        int digits = text.Length - 1;
        if (digits is not (3 or 4 or 6 or 8)) return false;

        uint value = 0;
        for (var i = 1; i < text.Length; i++)
        {
            int nibble = HexValue(text[i]);
            if (nibble < 0) return false;
            value = (value << 4) | (uint)nibble;
        }

        switch (digits)
        {
            case 3:
                rgba = (Expand(value >> 8) << 24) | (Expand(value >> 4) << 16) | (Expand(value) << 8) | 0xFF;
                break;
            case 4:
                rgba = (Expand(value >> 12) << 24) | (Expand(value >> 8) << 16) | (Expand(value >> 4) << 8) | Expand(value);
                break;
            case 6:
                rgba = (value << 8) | 0xFF;
                break;
            default:
                rgba = value;
                break;
        }

        return true;

        static uint Expand(uint nibble) => (nibble & 0xF) * 17;
    }

    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1
    };

    private static bool TryNamed(string text, out uint rgba)
    {
        rgba = 0;

        // 色名はネイティブ側で C 文字列として照合されるため NUL 以降は見られない (16 進の側は見られる)。
        int length = text.IndexOf('\0');
        if (length < 0) length = text.Length;
        if (length > 11) return false;

        // 大文字小文字の同一視は ASCII だけ (ToLowerInvariant だと 'İ' などが別の文字に畳まれる)。
        var lower = new char[length];
        for (var i = 0; i < length; i++)
        {
            char c = text[i];
            if (c > 0x7F) return false;
            lower[i] = c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
        }

        uint? named = new string(lower) switch
        {
            "red" => 0xFF0000FF,
            "cyan" => 0x00FFFFFF,
            "blue" => 0x0000FFFF,
            "darkblue" => 0x00008BFF,
            "lightblue" => 0xADD8E6FF,
            "purple" => 0x800080FF,
            "yellow" => 0xFFFF00FF,
            "lime" => 0x00FF00FF,
            "fuchsia" => 0xFF00FFFF,
            "white" => 0xFFFFFFFF,
            "silver" => 0xC0C0C0FF,
            "grey" => 0x808080FF,
            "black" => 0x000000FF,
            "orange" => 0xFFA500FF,
            "brown" => 0xA52A2AFF,
            "maroon" => 0x800000FF,
            "green" => 0x008000FF,
            "olive" => 0x808000FF,
            "navy" => 0x000080FF,
            "teal" => 0x008080FF,
            "aqua" => 0x00FFFFFF,
            "magenta" => 0xFF00FFFF,
            "transparent" => 0x00000000,
            _ => null
        };

        if (named == null) return false;
        rgba = named.Value;
        return true;
    }
}
