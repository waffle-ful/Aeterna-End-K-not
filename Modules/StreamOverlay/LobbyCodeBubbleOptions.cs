namespace EndKnot.Modules.StreamOverlay;

// 参加コードの看板のオプション。SystemSettings タブ配下、ID 44702〜44705 (44700・44701 は欠番)。
// default OFF（host が設定から手動で ON にする運用）。
public static class LobbyCodeBubbleOptions
{
    public const int OptionIdBase = 44700;

    public static OptionItem TickerEnabled;
    public static OptionItem TickerInterval;
    public static OptionItem TickerRunSeconds;
    public static OptionItem TickerScale;

    public static void SetupCustomOption()
    {
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
