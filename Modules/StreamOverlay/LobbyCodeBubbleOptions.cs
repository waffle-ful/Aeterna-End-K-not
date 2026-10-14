namespace EndKnot.Modules.StreamOverlay;

// オプション一式。SystemSettings タブ配下、ID 44700〜44705 (44702〜 は参加コードの看板)。
// Enabled は default OFF（host が設定から手動で ON にする運用）。
public static class LobbyCodeBubbleOptions
{
    public const int OptionIdBase = 44700;

    public static OptionItem Enabled;
    public static OptionItem Scale;

    public static OptionItem TickerEnabled;
    public static OptionItem TickerInterval;
    public static OptionItem TickerRunSeconds;
    public static OptionItem TickerScale;

    public static void SetupCustomOption()
    {
        Enabled = new BooleanOptionItem(OptionIdBase + 0, "LobbyCodeBubbleEnable", false, TabGroup.SystemSettings)
            .SetHeader(true);

        Scale = new IntegerOptionItem(OptionIdBase + 1, "LobbyCodeBubbleScale", new(50, 400, 10), 150, TabGroup.SystemSettings)
            .SetParent(Enabled)
            .SetValueFormat(OptionFormat.Percent);

        TickerEnabled = new BooleanOptionItem(OptionIdBase + 2, "LobbyCodeTickerEnable", false, TabGroup.SystemSettings)
            .SetHeader(true);

        TickerInterval = new FloatOptionItem(OptionIdBase + 3, "LobbyCodeTickerInterval", new(10f, 300f, 5f), 45f, TabGroup.SystemSettings)
            .SetParent(TickerEnabled)
            .SetValueFormat(OptionFormat.Seconds);

        TickerRunSeconds = new FloatOptionItem(OptionIdBase + 4, "LobbyCodeTickerRunSeconds", new(3f, 20f, 1f), 8f, TabGroup.SystemSettings)
            .SetParent(TickerEnabled)
            .SetValueFormat(OptionFormat.Seconds);

        TickerScale = new IntegerOptionItem(OptionIdBase + 5, "LobbyCodeTickerScale", new(50, 300, 10), 100, TabGroup.SystemSettings)
            .SetParent(TickerEnabled)
            .SetValueFormat(OptionFormat.Percent);
    }
}
