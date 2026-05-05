using System;
using EndKnot.Modules;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using static EndKnot.Translator;

namespace EndKnot.Patches.CalamityMenu;

/// <summary>
/// EHR 既存機能（BGM / UpdateButton / 季節メッセージ / ソーシャルボタン）を
/// Calamity メニューレイアウトに移植するブリッジ。
/// </summary>
public static class EndKnotFeatureBridge
{
    // Social URLs — fill these in when the official EndKnot destinations are decided.
    // Empty string = button is no-op (still visible as a placeholder).
    private const string DiscordUrl = "";
    private const string GitHubUrl  = "";
    private const string WebsiteUrl = "";

    // BGM state (mirrors MainMenuManagerPatch private fields)
    private static bool _bgmInitPending;
    private static bool _bgmStarted;
    private static float _bgmSilenceUntil;

    // References for LateUpdate visibility check
    private static GameObject _updateButtonGo;

    public static void Init(MainMenuManager mm, Transform overlayLayer)
    {
        // Suppress the vanilla-style UpdateButton that Start_Prefix already created
        if (MainMenuManagerPatch.UpdateButton != null)
            MainMenuManagerPatch.UpdateButton.gameObject.SetActive(false);

        Application.targetFrameRate = Main.UnlockFps.Value ? 120 : 60;

        // BGM init (mirrors MainMenuManagerPatch.Start_Prefix timing)
        _bgmInitPending = true;
        _bgmStarted     = false;
        _bgmSilenceUntil = Time.realtimeSinceStartup + 2.5f;

        // Update notification button (Calamity style, upper-right, within screen)
        _updateButtonGo = CreateTextButton(
            overlayLayer,
            GetString("updateButton"),
            new Vector3(3.2f, 2.0f, 0f),
            new Color(1.0f, 0.65f, 0.0f, 1f),  // orange
            new Color(1.0f, 0.80f, 0.0f, 1f),  // bright orange on hover
            2.2f,
            () => ModUpdater.StartUpdate(ModUpdater.DownloadUrl, true));

        _updateButtonGo.SetActive(ModUpdater.HasUpdate);

        // Seasonal special message
        CreateSpecialMessage(overlayLayer);

        // VanillaSuppressor hides VersionShower in Calamity mode, so social buttons reclaim
        // their bottom-of-screen slot without overlap.
        CreateTextButton(overlayLayer, "Discord", new Vector3(-1.5f, -2.1f, 0f),
            new Color(0.55f, 0.63f, 0.95f, 1f), new Color(0.75f, 0.80f, 1.00f, 1f), 1.8f, () => OpenUrlIfSet(DiscordUrl));
        CreateTextButton(overlayLayer, "GitHub", new Vector3(0.0f, -2.1f, 0f),
            new Color(0.60f, 0.60f, 0.60f, 1f), new Color(0.85f, 0.85f, 0.85f, 1f), 1.8f, () => OpenUrlIfSet(GitHubUrl));
        CreateTextButton(overlayLayer, "Website", new Vector3(1.5f, -2.1f, 0f),
            new Color(0.80f, 0.40f, 0.30f, 1f), new Color(0.95f, 0.60f, 0.45f, 1f), 1.8f, () => OpenUrlIfSet(WebsiteUrl));
    }

    private static void OpenUrlIfSet(string url)
    {
        if (!string.IsNullOrEmpty(url)) Application.OpenURL(url);
    }

    /// <summary>Called every LateUpdate from CalamityMenuPatch while Active.</summary>
    public static void Tick()
    {
        // BGM
        if (_bgmInitPending)
        {
            if (SoundManager.Instance != null)
            {
                BGMManager.SilenceVanillaAudio();
                if (!_bgmStarted) { BGMManager.SetMenuBGM(); _bgmStarted = true; }
            }

            if (Time.realtimeSinceStartup >= _bgmSilenceUntil)
                _bgmInitPending = false;
        }

        // Update button visibility (ModUpdater.HasUpdate can change async)
        if (_updateButtonGo != null)
            _updateButtonGo.SetActive(ModUpdater.HasUpdate);
    }

    // ── Seasonal message ────────────────────────────────────────────────

    private static void CreateSpecialMessage(Transform parent)
    {
        DateTime now      = DateTime.Now;
        bool holidays     = now is { Month: 12, Day: < 24 or > 26 };
        bool christmas    = now is { Month: 12, Day: >= 24 and <= 26 };
        bool newYear      = now is { Month: 1,  Day: <= 6 };
        bool easter       = IsEasterPeriod();

        if (!holidays && !christmas && !newYear && !easter) return;

        string key = holidays ? "HolidayGreeting"
                   : christmas ? "ChristmasGreeting"
                   : newYear   ? "NewYearGreeting"
                               : "EasterGreeting";

        var go = new GameObject("SpecialMessage");
        go.transform.SetParent(parent);
        go.transform.localPosition = new Vector3(0f, -2.8f, 0f);

        var tmp = go.AddComponent<TextMeshPro>();
        tmp.text                = $"<b>{GetString(key)}</b>";
        tmp.alignment           = TextAlignmentOptions.Center;
        tmp.fontSize            = 2.5f;
        tmp.color               = Color.white;
        tmp.outlineColor        = Color.black;
        tmp.outlineWidth        = 0.2f;
        tmp.enableWordWrapping  = false;
        tmp.fontWeight          = FontWeight.Black;
        tmp.sortingOrder        = 10;
        CalamityFonts.Apply(tmp);
    }

    private static bool IsEasterPeriod(int daysBefore = 2, int daysAfter = 1)
    {
        DateTime today  = DateTime.Today;
        DateTime easter = GetEasterSunday(today.Year);
        return today >= easter.AddDays(-daysBefore) && today <= easter.AddDays(daysAfter);
    }

    private static DateTime GetEasterSunday(int year)
    {
        int a = year % 19, b = year / 100, c = year % 100;
        int d = b / 4, e = b % 4, f = (b + 8) / 25;
        int g = (b - f + 1) / 3, h = (19 * a + b - d - g + 15) % 30;
        int i = c / 4, j = c % 4, k = (32 + 2 * e + 2 * i - h - j) % 7;
        int l = (a + 11 * h + 22 * k) / 451;
        int month = (h + k - 7 * l + 114) / 31;
        int day   = ((h + k - 7 * l + 114) % 31) + 1;
        return new DateTime(year, month, day);
    }

    // ── Generic TMP text button ─────────────────────────────────────────

    private static GameObject CreateTextButton(Transform parent, string label, Vector3 pos,
        Color normalColor, Color hoverColor, float fontSize, Action onClick)
    {
        var go = new GameObject($"EHRBtn_{label}");
        go.transform.SetParent(parent);
        go.transform.localPosition = pos;
        go.transform.localScale    = Vector3.one;

        var tmp = go.AddComponent<TextMeshPro>();
        tmp.text              = label;
        tmp.fontSize          = fontSize;
        tmp.fontSizeMin       = fontSize;
        tmp.fontSizeMax       = fontSize;
        tmp.enableAutoSizing  = false;
        tmp.alignment         = TextAlignmentOptions.Center;
        tmp.fontStyle         = FontStyles.Bold;
        tmp.outlineColor      = new Color32(0, 0, 0, 180);
        tmp.outlineWidth      = 0.15f;
        tmp.color             = normalColor;
        tmp.sortingOrder      = 10;
        CalamityFonts.Apply(tmp);

        var col    = go.AddComponent<BoxCollider2D>();
        col.size   = new Vector2(Mathf.Max(2f, fontSize * 0.8f), fontSize * 0.55f);
        col.offset = Vector2.zero;

        var btn         = go.AddComponent<PassiveButton>();
        btn.OnClick     = new();
        btn.OnMouseOver = new();
        btn.OnMouseOut  = new();

        btn.OnClick    .AddListener((UnityAction)onClick);
        btn.OnMouseOver.AddListener((UnityAction)(() => { tmp.color = hoverColor;  go.transform.localScale = Vector3.one * 1.06f; }));
        btn.OnMouseOut .AddListener((UnityAction)(() => { tmp.color = normalColor; go.transform.localScale = Vector3.one; }));

        return go;
    }
}
