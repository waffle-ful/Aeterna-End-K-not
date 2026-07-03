using System;
using System.Text;
using EndKnot.Modules;
using Xunit;

namespace EndKnot.Tests;

// Modules/EkmCodec.cs — "EKM1." + base64url( deflate-raw( UTF-8 JSON ) ) の契約テスト。
public class EkmCodecTests
{
    // テスト用: 任意バイト列を EkmCodec と同一仕様の base64url にする (パディング無し)
    private static string Base64Url(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static string LargeJson()
    {
        var sb = new StringBuilder("{\"tiles\":[");
        for (var i = 0; i < 20000; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(i % 7);
        }

        sb.Append("]}");
        return sb.ToString();
    }

    // ── ラウンドトリップ ─────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"v\":1,\"name\":\"test map\"}")]
    [InlineData("{\"name\":\"日本語マップ🗺️\",\"desc\":\"改行\\nタブ\\tあり\"}")]
    [InlineData("not-even-json but arbitrary text ~!@#$%^&*()")]
    public void Roundtrip_RestoresOriginalText(string original)
    {
        Assert.True(EkmCodec.TryEncode(original, out string code, out string encodeError), encodeError);
        Assert.True(EkmCodec.TryDecode(code, out string decoded, out string decodeError), decodeError);
        Assert.Equal(original, decoded);
    }

    [Fact]
    public void Roundtrip_LargeInput()
    {
        string original = LargeJson();
        Assert.True(EkmCodec.TryEncode(original, out string code, out _));
        Assert.True(EkmCodec.TryDecode(code, out string decoded, out _));
        Assert.Equal(original, decoded);
    }

    // ── エンコード出力の形 ───────────────────────────────────────────

    [Fact]
    public void Encode_StartsWithPrefix()
    {
        Assert.True(EkmCodec.TryEncode("{\"a\":1}", out string code, out _));
        Assert.StartsWith(EkmCodec.Prefix, code, StringComparison.Ordinal);
    }

    [Fact]
    public void Encode_PayloadIsBase64Url_NoPlusSlashEquals()
    {
        // 圧縮結果に多様なバイトが出るよう大きめ入力で
        Assert.True(EkmCodec.TryEncode(LargeJson(), out string code, out _));
        string payload = code.Substring(EkmCodec.Prefix.Length);
        Assert.NotEmpty(payload);

        foreach (char c in payload)
        {
            bool ok = c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_';
            Assert.True(ok, $"base64url に許されない文字が含まれています: '{c}' (0x{(int)c:X2})");
        }
    }

    [Fact]
    public void Encode_NullInput_ReturnsFalse()
    {
        Assert.False(EkmCodec.TryEncode(null, out string code, out string error));
        Assert.Null(code);
        Assert.NotNull(error);
    }

    // ── デコード失敗系 (false を返し、例外を漏らさない) ─────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \t\n ")]
    [InlineData("ABCDEF")] // プレフィックス無し
    [InlineData("ekm1.AAAA")] // プレフィックスは大文字小文字を区別
    [InlineData("EKM1.!!!invalid!!!")] // base64 に無い文字
    [InlineData("EKM1.A")] // 長さ % 4 == 1 は base64 として不正
    public void Decode_InvalidInput_ReturnsFalseWithoutThrowing(string input)
    {
        bool ok = true;
        string json = null;
        string error = null;
        Exception ex = Record.Exception(() => ok = EkmCodec.TryDecode(input, out json, out error));

        Assert.Null(ex);
        Assert.False(ok);
        Assert.Null(json);
        Assert.NotNull(error);
    }

    [Fact]
    public void Decode_ValidBase64ButBrokenDeflate_ReturnsFalseWithoutThrowing()
    {
        // 0xFF 先頭 = BTYPE=11 (予約・不正) — deflate として確実に壊れているバイト列
        string code = EkmCodec.Prefix + Base64Url(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });

        bool ok = true;
        Exception ex = Record.Exception(() => ok = EkmCodec.TryDecode(code, out _, out string error));

        Assert.Null(ex);
        Assert.False(ok);
    }

    [Fact]
    public void Decode_TruncatedCode_DoesNotThrowAndNeverReturnsOriginal()
    {
        string original = LargeJson();
        Assert.True(EkmCodec.TryEncode(original, out string code, out _));

        // deflate ペイロードを途中でぶった切る
        string truncated = code.Substring(0, EkmCodec.Prefix.Length + 12);

        var ok = false;
        string json = null;
        Exception ex = Record.Exception(() => ok = EkmCodec.TryDecode(truncated, out json, out _));

        Assert.Null(ex);
        // 実装/ランタイム次第で「失敗」か「部分展開」— どちらでも元文字列が丸ごと返ることはない
        if (ok) Assert.NotEqual(original, json);
    }

    // ── デコード許容系 (パディング・空白の受理) ──────────────────────

    [Fact]
    public void Decode_ToleratesWhitespaceAndPadding()
    {
        const string original = "{\"v\":2,\"zones\":[1,2,3]}";
        Assert.True(EkmCodec.TryEncode(original, out string code, out _));

        string payload = code.Substring(EkmCodec.Prefix.Length);
        int mid = payload.Length / 2;
        string mangled = "  " + EkmCodec.Prefix + payload.Substring(0, mid) + "\r\n \t" + payload.Substring(mid) + "==  ";

        Assert.True(EkmCodec.TryDecode(mangled, out string decoded, out string error), error);
        Assert.Equal(original, decoded);
    }
}
