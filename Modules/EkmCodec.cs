using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace EndKnot.Modules;

// EKM マップコード (仕様 §8): "EKM1." + base64url( deflate-raw( UTF-8 JSON ) )
// エディタ側 (editor/src/mapcode.ts + base64url.ts) と完全に同一契約:
//  - deflate は RAW deflate (zlib ヘッダ無し)。C# の DeflateStream は raw deflate なので素のまま一致。
//  - base64url = RFC 4648 §5 (英字+数字+'-''_')。エンコードはパディング無し、デコードはパディング有無・空白どちらも受理。
public static class EkmCodec
{
    public const string Prefix = "EKM1.";

    // JSON テキスト → マップコード。失敗時は false + error。
    public static bool TryEncode(string json, out string code, out string error)
    {
        code = null;
        error = null;
        if (json == null)
        {
            error = "json is null";
            return false;
        }

        try
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(json);
            byte[] deflated;
            using (var ms = new MemoryStream())
            {
                // leaveOpen=true で ds.Dispose() 後も ms から ToArray できる。
                // ds の Dispose が deflate ストリームを flush する (これより前に ToArray すると不完全)。
                using (var ds = new DeflateStream(ms, CompressionMode.Compress, leaveOpen: true))
                    ds.Write(utf8, 0, utf8.Length);
                deflated = ms.ToArray();
            }

            code = Prefix + Base64UrlEncode(deflated);
            return true;
        }
        catch (Exception ex)
        {
            error = $"encode error: {ex.Message}";
            return false;
        }
    }

    // マップコード → JSON テキスト。失敗時は false + error (日本語混じりでユーザーに出せる文)。
    public static bool TryDecode(string code, out string json, out string error)
    {
        json = null;
        error = null;
        if (string.IsNullOrWhiteSpace(code))
        {
            error = "コードが空です";
            return false;
        }

        string trimmed = code.Trim();
        if (!trimmed.StartsWith(Prefix, StringComparison.Ordinal))
        {
            error = $"マップコードは {Prefix} で始まる必要があります";
            return false;
        }

        byte[] deflated;
        try
        {
            deflated = Base64UrlDecode(trimmed.Substring(Prefix.Length));
        }
        catch (Exception ex)
        {
            error = $"コードを base64url として読めません ({ex.Message})。コードが途中で切れていないか確認してください";
            return false;
        }

        try
        {
            using var input = new MemoryStream(deflated);
            using var ds = new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            ds.CopyTo(output);
            json = Encoding.UTF8.GetString(output.ToArray());
            return true;
        }
        catch (Exception ex)
        {
            error = $"コードの展開 (deflate) に失敗しました ({ex.Message})";
            return false;
        }
    }

    // ── base64url (editor/src/base64url.ts と同一仕様) ──────────────────────────

    private static string Base64UrlEncode(byte[] bytes)
    {
        // 標準 base64 → URL 安全文字へ置換 + パディング '=' 除去
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static byte[] Base64UrlDecode(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            // 空白・改行・パディングは除去 (TS 版 /[\s=]+/ と同等)
            if (char.IsWhiteSpace(c) || c == '=') continue;
            sb.Append(c == '-' ? '+' : c == '_' ? '/' : c);
        }

        string s = sb.ToString();
        switch (s.Length % 4)
        {
            case 1: throw new FormatException("base64url の長さが不正です");
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }

        return Convert.FromBase64String(s);
    }
}
