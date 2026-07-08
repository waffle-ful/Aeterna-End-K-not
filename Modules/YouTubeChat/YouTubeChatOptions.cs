namespace EndKnot.Modules.YouTubeChat;

// オプション一式。SystemSettings タブ配下、ID 範囲 44600〜44606。
// Enabled は default OFF。BGMShowInfo 隣接に並べる想定。
public static class YouTubeChatOptions
{
    public const int OptionIdBase = 44600;

    public static OptionItem Enabled;
    public static OptionItem DisplayCount;
    public static OptionItem PollingInterval;
    public static OptionItem HideDuringMeetings;
    public static OptionItem ShowAuthor;
    public static OptionItem PanelWidth;
    public static OptionItem PanelHeight;

    public static void SetupCustomOption()
    {
        Enabled = new BooleanOptionItem(OptionIdBase + 0, "YouTubeChatEnable", false, TabGroup.SystemSettings)
            .SetHeader(true);

        DisplayCount = new IntegerOptionItem(OptionIdBase + 1, "YouTubeChatDisplayCount", new(1, 30, 1), 10, TabGroup.SystemSettings)
            .SetParent(Enabled);

        PollingInterval = new IntegerOptionItem(OptionIdBase + 2, "YouTubeChatPollingInterval", new(3, 30, 1), 5, TabGroup.SystemSettings)
            .SetParent(Enabled)
            .SetValueFormat(OptionFormat.Seconds);

        HideDuringMeetings = new BooleanOptionItem(OptionIdBase + 3, "YouTubeChatHideDuringMeetings", true, TabGroup.SystemSettings)
            .SetParent(Enabled);

        ShowAuthor = new BooleanOptionItem(OptionIdBase + 4, "YouTubeChatShowAuthor", true, TabGroup.SystemSettings)
            .SetParent(Enabled);

        PanelWidth = new IntegerOptionItem(OptionIdBase + 5, "YouTubeChatPanelWidth", new(50, 1000, 10), 100, TabGroup.SystemSettings)
            .SetParent(Enabled)
            .SetValueFormat(OptionFormat.Percent);

        PanelHeight = new IntegerOptionItem(OptionIdBase + 6, "YouTubeChatPanelHeight", new(50, 1000, 10), 100, TabGroup.SystemSettings)
            .SetParent(Enabled)
            .SetValueFormat(OptionFormat.Percent);
    }
}
