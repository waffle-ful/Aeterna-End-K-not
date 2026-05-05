using TMPro;

namespace EndKnot.Patches.CalamityMenu;

/// <summary>
/// Captures vanilla TMP fonts from existing UI elements so Calamity menu text
/// uses the same look as the rest of the game (no missing-glyph boxes for CJK etc).
/// </summary>
public static class CalamityFonts
{
    public static TMP_FontAsset Vanilla;

    public static void Capture(MainMenuManager mm)
    {
        // mm.quitButton.buttonText is a TMP_Text on a vanilla PassiveButton
        if (mm?.quitButton?.buttonText != null)
            Vanilla = mm.quitButton.buttonText.font;
    }

    public static void Apply(TMP_Text tmp)
    {
        if (Vanilla != null && tmp != null) tmp.font = Vanilla;
    }
}
