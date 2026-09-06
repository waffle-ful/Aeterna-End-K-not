using System.Collections.Generic;
using System.Text;

namespace EndKnot.Modules.Setup;

// Main.AICommentaryArgs (companion.py の起動引数文字列) のトークン化・組み立てのみを行う。
// GUI/Config/スレッドに一切触れない純粋関数群 (StreamSetupState から呼ぶ)。
// companion.py の argparse (tools/companion/companion.py 末尾) が読む引数のうち、
// StreamSetupGUI のトグル行が管理するのは --tts / --duo / --duo-tts2 / --quiet-meeting の4つだけ。
// それ以外 (--voice / --audio-device 等、ユーザーが cfg に直接書いた引数) はトークン単位でそのまま
// 温存し、順序も変えない。
internal static class CompanionArgs
{
    internal sealed class Parsed
    {
        internal string Tts = "gemini";
        internal bool Duo;
        internal string DuoTts2 = "voicevox";
        internal bool QuietMeeting;
        internal readonly List<string> Unmanaged = new();
    }

    internal static Parsed Parse(string raw)
    {
        var result = new Parsed();
        List<string> tokens = Tokenize(raw);

        for (int i = 0; i < tokens.Count; i++)
        {
            switch (tokens[i])
            {
                case "--tts" when i + 1 < tokens.Count:
                    result.Tts = tokens[++i];
                    break;
                case "--duo":
                    result.Duo = true;
                    break;
                case "--duo-tts2" when i + 1 < tokens.Count:
                    result.DuoTts2 = tokens[++i];
                    break;
                case "--quiet-meeting":
                    result.QuietMeeting = true;
                    break;
                default:
                    result.Unmanaged.Add(tokens[i]);
                    break;
            }
        }

        return result;
    }

    // --duo-tts2 は --duo が立っている時だけ意味を持つため、Duo=false なら書かない
    // (StreamSetupState 側で Duo を ON にする時に DuoTts2="gemini" を明示的にセットする)。
    internal static string Compose(Parsed p)
    {
        var parts = new List<string>(p.Unmanaged.Count + 4);
        parts.AddRange(p.Unmanaged);

        parts.Add("--tts");
        parts.Add(p.Tts);

        if (p.Duo)
        {
            parts.Add("--duo");
            parts.Add("--duo-tts2");
            parts.Add(p.DuoTts2);
        }

        if (p.QuietMeeting) parts.Add("--quiet-meeting");

        var sb = new StringBuilder();
        for (int i = 0; i < parts.Count; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(Quote(parts[i]));
        }

        return sb.ToString();
    }

    // companion-run.cmd は cmd.exe 経由でこの文字列をそのまま展開する
    // (`python companion.py --events "%EK_COMPANION_EVENTS%" %EK_COMPANION_ARGS%`) ため、
    // 空白を含む値は二重引用符で囲む (--audio-device "CABLE Input" と同じ書式)。
    private static string Quote(string token)
        => token.Length == 0 || token.Contains(' ') ? $"\"{token}\"" : token;

    // ダブルクォート内の空白を1トークンとして扱う、cmd.exe 相当の最小トークナイザ。
    // クォート文字自体はトークンの内容から取り除く (Quote が再度付け直す)。
    private static List<string> Tokenize(string raw)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(raw)) return tokens;

        int i = 0;
        int n = raw.Length;

        while (i < n)
        {
            while (i < n && char.IsWhiteSpace(raw[i])) i++;
            if (i >= n) break;

            var sb = new StringBuilder();
            bool inQuotes = false;

            while (i < n && (inQuotes || !char.IsWhiteSpace(raw[i])))
            {
                char c = raw[i];
                if (c == '"') { inQuotes = !inQuotes; i++; continue; }
                sb.Append(c);
                i++;
            }

            tokens.Add(sb.ToString());
        }

        return tokens;
    }
}
