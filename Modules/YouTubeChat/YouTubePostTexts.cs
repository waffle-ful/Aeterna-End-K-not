using System;
using System.Linq;
using BepInEx.Configuration;
using InnerNet;

namespace EndKnot.Modules.YouTubeChat;

public enum YouTubePostTextKind
{
    Welcome,
    Countdown,
    Rotation,
    Promo
}

// 自動投稿の文言。ホストが書いた文言は Main の ConfigEntry に置き、空なら翻訳の既定文を使う。
// 差し込み記号は名前付き ({code} {current} {needed}) で、置換は string.Replace だけで行う —
// ホストの書いた文に「{」が混ざっても string.Format のように例外で投稿ごと落ちることがない。
public static class YouTubePostTexts
{
    // ローテーション文言は cfg では改行区切りだが、1行の入力欄ではこの区切りで並べて編集する
    public const char RotationSeparator = '|';

    private const string CodeToken = "{code}";
    private const string CurrentToken = "{current}";
    private const string NeededToken = "{needed}";
    private const int MaxPostLength = 200;

    public static ConfigEntry<string> Entry(YouTubePostTextKind kind) => kind switch
    {
        YouTubePostTextKind.Welcome => Main.YouTubePostWelcomeText,
        YouTubePostTextKind.Countdown => Main.YouTubePostCountdownText,
        YouTubePostTextKind.Rotation => Main.YouTubePostMessages,
        YouTubePostTextKind.Promo => Main.YouTubePostPromoText,
        _ => null
    };

    public static bool IsCustom(YouTubePostTextKind kind) => !string.IsNullOrWhiteSpace(Entry(kind)?.Value);

    // 翻訳の既定文を名前付きの差し込み記号に書き換えたもの (編集欄の初期値と、既定へ戻したかの判定に使う)
    public static string DefaultTemplate(YouTubePostTextKind kind) => kind switch
    {
        YouTubePostTextKind.Welcome => Translator.GetString("YouTubePost.Welcome").Replace("{0}", CodeToken),
        YouTubePostTextKind.Countdown => Translator.GetString("YouTubePost.Countdown").Replace("{0}", CurrentToken).Replace("{1}", NeededToken),
        YouTubePostTextKind.Promo => Translator.GetString("YouTubePost.Promo"),
        _ => ""
    };

    public static string Template(YouTubePostTextKind kind) => IsCustom(kind) ? Entry(kind).Value.Trim() : DefaultTemplate(kind);

    public static string[] RotationLines()
    {
        string raw = Main.YouTubePostMessages?.Value;
        if (string.IsNullOrWhiteSpace(raw)) return [];
        return raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public static string ToEditorText(YouTubePostTextKind kind) => kind == YouTubePostTextKind.Rotation
        ? string.Join($" {RotationSeparator} ", RotationLines())
        : Template(kind);

    // 戻り値: 既定の文言へ戻った (= 保存先を空にした) か
    public static bool SaveFromEditor(YouTubePostTextKind kind, string text)
    {
        ConfigEntry<string> entry = Entry(kind);
        if (entry == null) return true;

        text = (text ?? "").Replace("\r", "").Trim();

        if (kind == YouTubePostTextKind.Rotation)
        {
            string[] lines = text.Split([RotationSeparator, '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            entry.Value = string.Join('\n', lines);
            return lines.Length == 0;
        }

        text = text.Replace('\n', ' ');
        bool reset = text.Length == 0 || text == DefaultTemplate(kind);
        entry.Value = reset ? "" : text;
        return reset;
    }

    // 投稿する本文。ロビーコードが取れない (ロビー外) 間は null。
    public static string Render(YouTubePostTextKind kind, string template)
    {
        if (string.IsNullOrWhiteSpace(template)) return null;

        string code = LobbyCode();
        string text = template;

        // 翻訳ファイルの書き方 ({0}/{1}) のまま貼られた文もそのまま通す
        if (kind == YouTubePostTextKind.Welcome) text = text.Replace("{0}", CodeToken);
        else if (kind == YouTubePostTextKind.Countdown) text = text.Replace("{0}", CurrentToken).Replace("{1}", NeededToken);

        // 参加募集文はコードを載せるための投稿なので、記号を書き忘れた文には末尾に足す
        if (kind == YouTubePostTextKind.Welcome && !text.Contains(CodeToken, StringComparison.Ordinal))
            text = $"{text} {CodeToken}";

        if (text.Contains(CodeToken, StringComparison.Ordinal))
        {
            if (string.IsNullOrEmpty(code)) return null;
            text = text.Replace(CodeToken, code);
        }

        if (text.Contains(CurrentToken, StringComparison.Ordinal))
            text = text.Replace(CurrentToken, (GameData.Instance != null ? GameData.Instance.PlayerCount : 0).ToString());

        if (text.Contains(NeededToken, StringComparison.Ordinal))
            text = text.Replace(NeededToken, (Options.PlayerAutoStart != null ? Options.PlayerAutoStart.GetInt() : 0).ToString());

        // YouTube のライブチャットは 1 投稿 200 文字まで (超えると API が弾いて何も出ない)。差し込み後の長さで切る。
        return text.Length > MaxPostLength ? text[..MaxPostLength] : text;
    }

    public static string Render(YouTubePostTextKind kind) => Render(kind, Template(kind));

    // 編集欄を閉じた後にホストへ見せる確認用の文 (ロビーコードが取れない時は記号のまま)
    public static string Preview(YouTubePostTextKind kind)
    {
        if (kind == YouTubePostTextKind.Rotation)
        {
            string[] lines = RotationLines();
            return string.Join(" / ", lines.Select(l => Render(kind, l) ?? l));
        }

        return Render(kind) ?? Template(kind);
    }

    private static string LobbyCode()
    {
        try { return AmongUsClient.Instance ? GameCode.IntToGameName(AmongUsClient.Instance.GameId) : null; }
        catch { return null; }
    }
}
