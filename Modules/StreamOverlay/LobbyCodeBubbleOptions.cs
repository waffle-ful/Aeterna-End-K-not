namespace EndKnot.Modules.StreamOverlay;

// オプション一式。SystemSettings タブ配下、ID 44700。
// Enabled は default OFF（host が設定から手動で ON にする運用）。
public static class LobbyCodeBubbleOptions
{
    public const int OptionIdBase = 44700;

    public static OptionItem Enabled;
    public static OptionItem Scale;

    public static void SetupCustomOption()
    {
        Enabled = new BooleanOptionItem(OptionIdBase + 0, "LobbyCodeBubbleEnable", false, TabGroup.SystemSettings)
            .SetHeader(true);

        Scale = new IntegerOptionItem(OptionIdBase + 1, "LobbyCodeBubbleScale", new(50, 400, 10), 150, TabGroup.SystemSettings)
            .SetParent(Enabled)
            .SetValueFormat(OptionFormat.Percent);
    }
}
