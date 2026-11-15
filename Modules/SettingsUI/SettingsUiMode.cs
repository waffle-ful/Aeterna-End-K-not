namespace EndKnot.SettingsUI;

// 設定画面の見た目の選択。画面が開く瞬間 (GameSettingMenu.OnEnable) に一度だけ固定し、閉じるまで変えない。
// 開いている間にクライアント設定を切り替えても、その画面は最初に選んだ見た目のまま最後まで動く。
public static class SettingsUiMode
{
    private static bool _warned;

    public static bool Active { get; private set; }

    // 新しい画面の部品の元になる本編の絵が取れていない時は、画面が空になるので今の画面で開く
    public static void Latch()
    {
        Active = Main.NewSettingsUI != null && Main.NewSettingsUI.Value;
        if (!Active) return;
        UiAssets.Revalidate();
        if (UiAssets.Box) return;
        Active = false;
        if (_warned) return;
        _warned = true;
        Logger.Warn("settings UI parts are not loaded; opening the standard settings screen", "SettingsUiMode");
    }

    public static void Release() => Active = false;
}
