namespace EndKnot.Modules.Audience;

// オプション一式。SystemSettings タブ配下、ID 範囲 44710〜44732。
// Enabled は default OFF。YouTubeChatOptions/LobbyCodeBubbleOptions 隣接に並べる想定。
public static class AudienceOptions
{
    public const int OptionIdBase = 44710;

    public static OptionItem Enabled;
    public static OptionItem PointsPerMessage;
    public static OptionItem PointsCooldown;
    public static OptionItem GlobalInterventionInterval;
    public static OptionItem TargetCooldown;
    public static OptionItem CurseDuration;
    public static OptionItem BlessDuration;

    public static OptionItem ShowInfoOverlay;
    public static OptionItem InfoOverlayScale;

    public static OptionItem BlackoutEnabled;
    public static OptionItem BlackoutPrice;
    public static OptionItem ReactorEnabled;
    public static OptionItem ReactorPrice;
    public static OptionItem CommsEnabled;
    public static OptionItem CommsPrice;
    public static OptionItem DoorsEnabled;
    public static OptionItem DoorsPrice;
    public static OptionItem CurseEnabled;
    public static OptionItem CursePrice;
    public static OptionItem BlessEnabled;
    public static OptionItem BlessPrice;
    public static OptionItem MeteorEnabled;
    public static OptionItem MeteorPrice;

    public static void SetupCustomOption()
    {
        Enabled = new BooleanOptionItem(OptionIdBase + 0, "AudienceEnable", false, TabGroup.SystemSettings)
            .SetHeader(true);

        PointsPerMessage = new IntegerOptionItem(OptionIdBase + 1, "AudiencePointsPerMessage", new(1, 100, 1), 10, TabGroup.SystemSettings)
            .SetParent(Enabled);

        PointsCooldown = new FloatOptionItem(OptionIdBase + 2, "AudiencePointsCooldown", new(0f, 300f, 5f), 30f, TabGroup.SystemSettings)
            .SetParent(Enabled)
            .SetValueFormat(OptionFormat.Seconds);

        GlobalInterventionInterval = new FloatOptionItem(OptionIdBase + 3, "AudienceGlobalInterventionInterval", new(1f, 120f, 1f), 10f, TabGroup.SystemSettings)
            .SetParent(Enabled)
            .SetValueFormat(OptionFormat.Seconds);

        TargetCooldown = new FloatOptionItem(OptionIdBase + 4, "AudienceTargetCooldown", new(0f, 600f, 5f), 60f, TabGroup.SystemSettings)
            .SetParent(Enabled)
            .SetValueFormat(OptionFormat.Seconds);

        CurseDuration = new FloatOptionItem(OptionIdBase + 5, "AudienceCurseDuration", new(1f, 60f, 1f), 10f, TabGroup.SystemSettings)
            .SetParent(Enabled)
            .SetValueFormat(OptionFormat.Seconds);

        BlessDuration = new FloatOptionItem(OptionIdBase + 6, "AudienceBlessDuration", new(1f, 60f, 1f), 10f, TabGroup.SystemSettings)
            .SetParent(Enabled)
            .SetValueFormat(OptionFormat.Seconds);

        // 配信画面に「!コマンドで参加できる」旨を周知する回転バナー。Audience 有効時は既定 ON。
        ShowInfoOverlay = new BooleanOptionItem(OptionIdBase + 21, "AudienceShowInfoOverlay", true, TabGroup.SystemSettings)
            .SetParent(Enabled);

        InfoOverlayScale = new IntegerOptionItem(OptionIdBase + 22, "AudienceInfoOverlayScale", new(50, 400, 10), 150, TabGroup.SystemSettings)
            .SetParent(ShowInfoOverlay)
            .SetValueFormat(OptionFormat.Percent);

        BlackoutEnabled = new BooleanOptionItem(OptionIdBase + 7, "AudienceBlackoutEnabled", true, TabGroup.SystemSettings)
            .SetParent(Enabled);

        BlackoutPrice = new IntegerOptionItem(OptionIdBase + 8, "AudienceBlackoutPrice", new(0, 10000, 10), 100, TabGroup.SystemSettings)
            .SetParent(BlackoutEnabled);

        ReactorEnabled = new BooleanOptionItem(OptionIdBase + 9, "AudienceReactorEnabled", true, TabGroup.SystemSettings)
            .SetParent(Enabled);

        ReactorPrice = new IntegerOptionItem(OptionIdBase + 10, "AudienceReactorPrice", new(0, 10000, 10), 150, TabGroup.SystemSettings)
            .SetParent(ReactorEnabled);

        CommsEnabled = new BooleanOptionItem(OptionIdBase + 11, "AudienceCommsEnabled", true, TabGroup.SystemSettings)
            .SetParent(Enabled);

        CommsPrice = new IntegerOptionItem(OptionIdBase + 12, "AudienceCommsPrice", new(0, 10000, 10), 150, TabGroup.SystemSettings)
            .SetParent(CommsEnabled);

        DoorsEnabled = new BooleanOptionItem(OptionIdBase + 13, "AudienceDoorsEnabled", true, TabGroup.SystemSettings)
            .SetParent(Enabled);

        DoorsPrice = new IntegerOptionItem(OptionIdBase + 14, "AudienceDoorsPrice", new(0, 10000, 10), 80, TabGroup.SystemSettings)
            .SetParent(DoorsEnabled);

        CurseEnabled = new BooleanOptionItem(OptionIdBase + 15, "AudienceCurseEnabled", true, TabGroup.SystemSettings)
            .SetParent(Enabled);

        CursePrice = new IntegerOptionItem(OptionIdBase + 16, "AudienceCursePrice", new(0, 10000, 10), 120, TabGroup.SystemSettings)
            .SetParent(CurseEnabled);

        BlessEnabled = new BooleanOptionItem(OptionIdBase + 17, "AudienceBlessEnabled", true, TabGroup.SystemSettings)
            .SetParent(Enabled);

        BlessPrice = new IntegerOptionItem(OptionIdBase + 18, "AudienceBlessPrice", new(0, 10000, 10), 120, TabGroup.SystemSettings)
            .SetParent(BlessEnabled);

        MeteorEnabled = new BooleanOptionItem(OptionIdBase + 19, "AudienceMeteorEnabled", true, TabGroup.SystemSettings)
            .SetParent(Enabled);

        MeteorPrice = new IntegerOptionItem(OptionIdBase + 20, "AudienceMeteorPrice", new(0, 10000, 10), 200, TabGroup.SystemSettings)
            .SetParent(MeteorEnabled);
    }
}
