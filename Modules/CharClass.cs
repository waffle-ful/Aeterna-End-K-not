using System.Linq;

namespace EndKnot.Modules;

// 文字種縛り (Babel) の判定チョークポイント。Modules/WordLimit.cs の既存 Regex 4 本とは独立に持つ
// (WordLimit は出荷済みの別機能・リファクタ禁止)。範囲は Unicode ブロックの素朴な境界。
public static class CharClass
{
    public static bool IsHiragana(char c) => c >= 'ぁ' && c <= 'ゟ';

    // ・(U+30FB) と ー(U+30FC) は記号・音引きなので片仮名縛りの対象外。半角カナも同様に ｰ(U+FF70) を除く。
    public static bool IsKatakana(char c) =>
        (c >= '゠' && c <= 'ヿ' && c != '・' && c != 'ー') ||
        (c >= 'ｦ' && c <= 'ﾝ' && c != 'ｰ');

    public static bool IsHan(char c) => (c >= '一' && c <= '鿿') || (c >= '㐀' && c <= '䶿');

    public static bool IsLatin(char c) =>
        (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
        (c >= 'À' && c <= 'ɏ') ||
        // ベトナム語の重ね符号付き母音 (ế/ạ 等) は Latin-1/拡張 A/B の外、Latin Extended Additional に住む。
        (c >= 'Ḁ' && c <= 'ỿ') ||
        (c >= 'Ａ' && c <= 'Ｚ') || (c >= 'ａ' && c <= 'ｚ');

    public static bool IsDigit(char c) =>
        (c >= '0' && c <= '9') ||
        (c >= '０' && c <= '９') ||
        (c >= '٠' && c <= '٩') ||
        (c >= '۰' && c <= '۹');

    public static bool IsPunct(char c) =>
        (c >= '!' && c <= '/') || (c >= ':' && c <= '@') ||
        (c >= '[' && c <= '`') || (c >= '{' && c <= '~') ||
        (c >= '　' && c <= '〿') ||
        (c >= '！' && c <= '／') || (c >= '：' && c <= '＠') ||
        (c >= '［' && c <= '｀') || (c >= '｛' && c <= '･');

    // mode は Babel の縛りモード番号 (1-13, 0 は非適用)。lengthLimit はモード 12/13 (N文字以内/以上) の N。
    public static bool IsViolation(int mode, string text, int lengthLimit)
    {
        return mode switch
        {
            1 => text.Any(IsHiragana),
            2 => !text.Any(IsHiragana),
            3 => text.Any(IsKatakana),
            4 => !text.Any(IsKatakana),
            5 => text.Any(IsHan),
            6 => !text.Any(IsHan),
            7 => text.Any(IsLatin),
            8 => !text.Any(IsLatin),
            9 => text.Any(IsDigit),
            10 => !text.Any(IsDigit),
            11 => text.Any(IsPunct),
            12 => text.Length > lengthLimit,
            13 => text.Length < lengthLimit,
            _ => false
        };
    }
}
