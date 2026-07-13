namespace EndKnot.Modules.YouTubeChat;

// オプション一式。SystemSettings タブ配下、ID 範囲 44740〜44745。
// Enabled は default OFF。YouTubeChatOptions/AudienceOptions 隣接に並べる想定。
// OAuth 認証情報 (ClientId/ClientSecret/RefreshToken) は Option ではなく Main.cs の
// Config.Bind (cfg ファイル直接編集/外部ヘルパー) 経由。ここには出さない。
public static class YouTubePostOptions
{
    public const int OptionIdBase = 44740;

    public static OptionItem Enabled;
    public static OptionItem Interval;
    public static OptionItem WelcomeEnabled;
    public static OptionItem CountdownEnabled;
    public static OptionItem RotationEnabled;
    public static OptionItem InterventionAnnounceEnabled;

    public static void SetupCustomOption()
    {
        Enabled = new BooleanOptionItem(OptionIdBase + 0, "YouTubePostEnable", false, TabGroup.SystemSettings)
            .SetHeader(true);

        Interval = new IntegerOptionItem(OptionIdBase + 1, "YouTubePostInterval", new(60, 600, 10), 120, TabGroup.SystemSettings)
            .SetParent(Enabled)
            .SetValueFormat(OptionFormat.Seconds);

        WelcomeEnabled = new BooleanOptionItem(OptionIdBase + 2, "YouTubePostWelcomeEnabled", true, TabGroup.SystemSettings)
            .SetParent(Enabled);

        CountdownEnabled = new BooleanOptionItem(OptionIdBase + 3, "YouTubePostCountdownEnabled", true, TabGroup.SystemSettings)
            .SetParent(Enabled);

        RotationEnabled = new BooleanOptionItem(OptionIdBase + 4, "YouTubePostRotationEnabled", true, TabGroup.SystemSettings)
            .SetParent(Enabled);

        InterventionAnnounceEnabled = new BooleanOptionItem(OptionIdBase + 5, "YouTubePostInterventionAnnounceEnabled", true, TabGroup.SystemSettings)
            .SetParent(Enabled);
    }
}
