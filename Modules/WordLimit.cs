using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using EndKnot.Patches;
using HarmonyLib;
using UnityEngine;
using static EndKnot.Translator;

namespace EndKnot.Modules;

// 発言に文字種の縛りをかける「言葉狩り」系の機能。特定のゲームモードにぶら下げず、
// どのゲームモードにも乗せられる「縛り」として実装する (CustomGameMode enum には触らない)。
public static class WordLimit
{
    private static OptionItem EnableWordLimit;
    private static OptionItem Rule;
    private static OptionItem Punishment;
    private static OptionItem GraceCount;
    private static OptionItem MinLength;
    private static OptionItem OnlyInMeeting;
    private static OptionItem ExemptHost;

    private static readonly string[] Rules =
    [
        "WordLimitRule.NoHiragana",
        "WordLimitRule.NoKatakana",
        "WordLimitRule.NoKanji",
        "WordLimitRule.NoAlphabet",
        "WordLimitRule.RequiredWord"
    ];

    private static readonly string[] Punishments =
    [
        "WordLimitPunishment.Warning",
        "WordLimitPunishment.Death"
    ];

    // 平仮名: 全域。片仮名: ・(U+30FB) と ー(U+30FC) は記号扱いで常に無罪なので除外する。
    private static readonly Regex HiraganaRegex = new(@"[ぁ-ゟ]", RegexOptions.Compiled);
    private static readonly Regex KatakanaRegex = new(@"[゠-ヿ-[・ー]]", RegexOptions.Compiled);
    private static readonly Regex KanjiRegex = new(@"[一-鿿]", RegexOptions.Compiled);
    private static readonly Regex AlphabetRegex = new("[A-Za-z]", RegexOptions.Compiled);

    // プレイヤーID -> 累積違反回数。試合開始でクリアする。
    private static readonly Dictionary<byte, int> ViolationCounts = [];

    // ホストが /wordlimit <語> で設定する必須語。StringOptionItem は自由入力できないためコマンドで持つ。
    // 試合をまたいで保持する (次の試合でまた消える方が不便なため、試合開始ではクリアしない)。
    private static string RequiredWord = "";

    public static void SetupCustomOption()
    {
        new TextOptionItem(110140, "MenuTitle.WordLimit", TabGroup.GameSettings)
            .SetColor(new Color32(255, 180, 120, byte.MaxValue))
            .SetHeader(true);

        EnableWordLimit = new BooleanOptionItem(960270, "EnableWordLimit", false, TabGroup.GameSettings)
            .SetColor(new Color32(255, 180, 120, byte.MaxValue));

        Rule = new StringOptionItem(960271, "WordLimitRule", Rules, 0, TabGroup.GameSettings)
            .SetParent(EnableWordLimit)
            .SetColor(new Color32(255, 180, 120, byte.MaxValue));

        Punishment = new StringOptionItem(960272, "WordLimitPunishment", Punishments, 0, TabGroup.GameSettings)
            .SetParent(EnableWordLimit)
            .SetColor(new Color32(255, 180, 120, byte.MaxValue));

        GraceCount = new IntegerOptionItem(960273, "WordLimitGraceCount", new(0, 5, 1), 1, TabGroup.GameSettings)
            .SetParent(EnableWordLimit)
            .SetValueFormat(OptionFormat.Times)
            .SetColor(new Color32(255, 180, 120, byte.MaxValue));

        MinLength = new IntegerOptionItem(960274, "WordLimitMinLength", new(1, 20, 1), 3, TabGroup.GameSettings)
            .SetParent(EnableWordLimit)
            .SetColor(new Color32(255, 180, 120, byte.MaxValue));

        OnlyInMeeting = new BooleanOptionItem(960275, "WordLimitOnlyInMeeting", true, TabGroup.GameSettings)
            .SetParent(EnableWordLimit)
            .SetColor(new Color32(255, 180, 120, byte.MaxValue));

        ExemptHost = new BooleanOptionItem(960276, "WordLimitExemptHost", true, TabGroup.GameSettings)
            .SetParent(EnableWordLimit)
            .SetColor(new Color32(255, 180, 120, byte.MaxValue));
    }

    public static bool IsEnabled => EnableWordLimit != null && EnableWordLimit.GetBool();

    public static void Reset() => ViolationCounts.Clear();

    public static void SetRequiredWord(string word) => RequiredWord = (word ?? "").Trim();

    public static string GetRequiredWord() => RequiredWord;

    // ChatManager.SendMessage の Prefix (VoiceVoxChatPatch と同じ「通常の自由発言だけが通る」チョークポイント) から呼ぶ。
    public static void OnChat(PlayerControl player, string message)
    {
        if (!IsEnabled || player == null || !player.IsAlive()) return;
        if (string.IsNullOrWhiteSpace(message)) return;

        string trimmed = message.Trim();
        if (trimmed.StartsWith('/')) return; // コマンドは対象外
        if (OnlyInMeeting.GetBool() && !GameStates.IsMeeting) return;
        if (trimmed.Length < MinLength.GetInt()) return; // 短い発言は判定しない

        if (!IsViolation(trimmed)) return;

        int count = ViolationCounts.TryGetValue(player.PlayerId, out int c) ? c + 1 : 1;
        ViolationCounts[player.PlayerId] = count;

        int grace = GraceCount.GetInt();
        if (count <= grace)
        {
            Utils.SendMessage(string.Format(GetString("WordLimit.Forgiven"), grace - count + 1), player.PlayerId, importance: MessageImportance.Low);
            return;
        }

        Punish(player);
    }

    private static bool IsViolation(string text)
    {
        return Rule.GetInt() switch
        {
            0 => HiraganaRegex.IsMatch(text),
            1 => KatakanaRegex.IsMatch(text),
            2 => KanjiRegex.IsMatch(text),
            3 => AlphabetRegex.IsMatch(text),
            4 => RequiredWord.Length > 0 && !text.Contains(RequiredWord),
            _ => false
        };
    }

    private static void Punish(PlayerControl player)
    {
        bool wantsDeath = Punishment.GetInt() == 1;
        bool hostExempt = player.IsHost() && ExemptHost.GetBool();

        if (!wantsDeath || hostExempt)
        {
            Utils.SendMessage(GetString("WordLimit.Warned"), player.PlayerId);
            return;
        }

        Utils.SendMessage(GetString("WordLimit.WillDie"), player.PlayerId);

        // 会議中は Kill 系 RPC が EAC に reject されるので、この会議の後始末 (ExilePatch) に乗せる。
        // 会議外の違反 (OnlyInMeeting=false 時) はその場で即死させて問題ない。
        if (GameStates.IsMeeting)
            CheckForEndVotingPatch.TryAddAfterMeetingDeathPlayers(PlayerState.DeathReason.WordLimit, player.PlayerId);
        else
            player.Suicide(PlayerState.DeathReason.WordLimit);
    }
}

// ChatManager.SendMessage は「通常の自由発言だけが通る」唯一のチョークポイント (VoiceVoxChatPatch 参照) —
// コマンドや囁き・派閥チャット等は '/' 判定またはこの経路に来ない形で既に除外されている。
[HarmonyPatch(typeof(ChatManager), nameof(ChatManager.SendMessage))]
internal static class WordLimitChatPatch
{
    public static void Prefix(PlayerControl player, string message)
    {
        try { WordLimit.OnChat(player, message); }
        catch (Exception e) { Logger.Exception(e, "WordLimitChatPatch"); }
    }
}
