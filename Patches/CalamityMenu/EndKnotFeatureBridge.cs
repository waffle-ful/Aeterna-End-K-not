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
    // Social destinations, shown as official-brand logo buttons along the bottom.
    private const string DiscordUrl = "https://discord.gg/s5J8c23Dms";
    private const string GitHubUrl  = "https://github.com/waffle-ful/Aeterna-End-K-not";
    private const string YouTubeUrl = "https://www.youtube.com/@wafflewafflewafflewafflewaffle";

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

        // Stream setup window (upper-left mirror of the update button). Streaming overlays are PC-only.
#if !ANDROID
        CreateTextButton(
            overlayLayer,
            GetString("Setup.MenuButton"),
            new Vector3(-3.2f, 2.0f, 0f),
            new Color(0.90f, 0.80f, 0.68f, 0.95f),  // parchment
            new Color(1.00f, 0.92f, 0.80f, 1f),     // brighter parchment on hover
            2.0f,
            EndKnot.Modules.Setup.StreamSetupGUI.Open);
#endif

        // Seasonal special message
        CreateSpecialMessage(overlayLayer);

        // VanillaSuppressor hides VersionShower in Calamity mode, so social buttons reclaim
        // their bottom-of-screen slot. EHR/TOHForE-style brand-colored buttons, cloned from the
        // vanilla quit button (the vanilla Start_Postfix skips its own Template in Calamity mode).
        if (CalamityDusk.Built) CreateLinkRow(mm, overlayLayer);
        else CreateSocialRow(mm.quitButton, overlayLayer, -2.1f);

        LateTask.New(() => ModUpdater.ShowAvailableUpdate(), 0.5f, "ShowUpdatePopupCalamity");
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
                if (!_bgmStarted)
                {
                    BGMManager.SetMenuBGM();
                    _bgmStarted = true;
                    // OGG 同期デコードで Init から実再生まで 2-4 秒空く。Init 基準の 2.5 秒
                    // 窓は BGM が鳴る頃に閉じてしまうので、実際に鳴った瞬間から 2.5 秒
                    // に張り直す。AU が遅れて再アームするアンビエントを確実に潰す。
                    _bgmSilenceUntil = Time.realtimeSinceStartup + 2.5f;
                }
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

    // ── Link row (bottom-left small text links, used with the dusk backdrop) ─

    private static readonly Color LinkColor      = new(0.90f, 0.80f, 0.68f, 0.85f);
    private static readonly Color LinkHoverColor = new(1.00f, 0.93f, 0.80f, 1f);

    // 絵の邪魔をしないよう、SNS とニュースを左下に小さな文字で一列に並べる。
    // ニュースは vanilla ボタンを見えなく・押せなくして、クリック処理だけ借りる
    // (公式サーバー警告を手動で開ける経路は vanilla のまま残す)。
    private static void CreateLinkRow(MainMenuManager mm, Transform parent)
    {
        Camera cam = Camera.main;
        float halfH = cam != null ? cam.orthographicSize : 3f;
        float halfW = halfH * (cam != null ? cam.aspect : 16f / 9f);
        float x = -halfW + 0.35f;
        float y = -halfH + 0.32f;
        const float fontSize = 1.5f;

        x = AddLink(parent, "Discord", x, y, fontSize, () => OpenUrlIfSet(DiscordUrl));
        x = AddSeparator(parent, x, y, fontSize);
        x = AddLink(parent, "GitHub", x, y, fontSize, () => OpenUrlIfSet(GitHubUrl));
        x = AddSeparator(parent, x, y, fontSize);
        x = AddLink(parent, "YouTube", x, y, fontSize, () => OpenUrlIfSet(YouTubeUrl));

        PassiveButton news = mm.newsButton;
        if (news == null) return;
        string label = "News";
        try
        {
            string s = TranslationController.Instance?.GetString(StringNames.NewsLabel);
            if (!string.IsNullOrWhiteSpace(s)) label = s.Trim();
        }
        catch (Exception ex) { Logger.Exception(ex, "EndKnotFeatureBridge.NewsLabel"); }
        foreach (Renderer r in news.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
        foreach (Collider2D c in news.GetComponentsInChildren<Collider2D>(true)) c.enabled = false;
        x = AddSeparator(parent, x, y, fontSize);
        AddLink(parent, label, x, y, fontSize, () =>
        {
            if (mm != null && mm.newsButton != null) mm.newsButton.OnClick.Invoke();
        });
    }

    private static float AddLink(Transform parent, string label, float x, float y, float fontSize, Action onClick)
    {
        GameObject go = CreateTextButton(parent, label, Vector3.zero, LinkColor, LinkHoverColor, fontSize, onClick);
        var tmp = go.GetComponent<TextMeshPro>();
        tmp.fontStyle    = FontStyles.Normal;
        tmp.outlineColor = new Color32(34, 18, 28, 220);
        tmp.outlineWidth = 0.2f;
        tmp.ForceMeshUpdate();
        float width = tmp.textBounds.size.x > 0.01f ? tmp.textBounds.size.x : label.Length * fontSize * 0.09f;
        go.transform.localPosition = new Vector3(x + width * 0.5f, y, 0f);
        var col = go.GetComponent<BoxCollider2D>();
        col.size   = new Vector2(width + 0.15f, fontSize * 0.22f);
        col.offset = new Vector2(tmp.textBounds.center.x, tmp.textBounds.center.y);
        return x + width;
    }

    private static float AddSeparator(Transform parent, float x, float y, float fontSize)
    {
        var go = new GameObject("EHRLinkSep");
        go.transform.SetParent(parent);
        go.transform.localScale = Vector3.one;
        var tmp = go.AddComponent<TextMeshPro>();
        tmp.text         = "·";
        tmp.fontSize     = fontSize;
        tmp.alignment    = TextAlignmentOptions.Center;
        tmp.color        = new Color(0.90f, 0.80f, 0.68f, 0.55f);
        tmp.sortingOrder = 10;
        CalamityFonts.Apply(tmp);
        const float gap = 0.32f;
        go.transform.localPosition = new Vector3(x + gap * 0.5f, y, 0f);
        return x + gap;
    }

    // ── Social buttons (EHR/TOHForE-style brand-colored vanilla buttons) ─

    private static void CreateSocialRow(PassiveButton template, Transform parent, float y)
    {
        if (template == null) return;

        // Cloned vanilla buttons tinted to each service's brand color — the familiar EHR /
        // TOHForE main-menu look. Fixed size + even center-to-center spacing = a tidy row.
        // Tints are boosted above the raw brand values to counter the button sprite's shading.
        var size = new Vector2(1.6f, 0.5f);
        const float step = 1.9f;

        CreateVanillaButton(template, parent, "DiscordButton", new Vector3(-step, y, 0f),
            new Color32(110, 126, 255, 255), new Color32(160, 172, 255, 255), size, "Discord", () => OpenUrlIfSet(DiscordUrl));
        CreateVanillaButton(template, parent, "GitHubButton", new Vector3(0f, y, 0f),
            new Color32(188, 188, 188, 255), new Color32(224, 224, 224, 255), size, "GitHub", () => OpenUrlIfSet(GitHubUrl));
        CreateVanillaButton(template, parent, "YouTubeButton", new Vector3(step, y, 0f),
            new Color32(236, 58, 52, 255), new Color32(255, 90, 82, 255), size, "YouTube", () => OpenUrlIfSet(YouTubeUrl));
    }

    /// <summary>
    /// Clones a vanilla PassiveButton (e.g. quitButton) into the Calamity overlay, tints its
    /// sliced sprite to a brand color and relabels it — the EHR / TOHForE social-button style.
    /// </summary>
    private static void CreateVanillaButton(PassiveButton template, Transform parent, string name, Vector3 pos,
        Color32 normalColor, Color32 hoverColor, Vector2 size, string label, Action onClick)
    {
        PassiveButton button = UnityEngine.Object.Instantiate(template, parent);
        // Cloned TMP inherits the template's "Quit" mesh; hide until reconfigured to avoid a 1-frame flash.
        button.gameObject.SetActive(false);
        button.name = name;
        UnityEngine.Object.Destroy(button.GetComponent<AspectPosition>());
        button.transform.localPosition = pos;

        button.OnClick = new();
        button.OnClick.AddListener((UnityAction)onClick);

        var buttonText = button.transform.Find("FontPlacer/Text_TMP").GetComponent<TMP_Text>();
        buttonText.DestroyTranslator();
        buttonText.fontSize = buttonText.fontSizeMax = buttonText.fontSizeMin = 2.9f;
        buttonText.enableWordWrapping = false;
        buttonText.text = label;

        var normalSprite = button.inactiveSprites.GetComponent<SpriteRenderer>();
        var hoverSprite  = button.activeSprites.GetComponent<SpriteRenderer>();
        normalSprite.color = normalColor;
        hoverSprite.color  = hoverColor;
        normalSprite.size  = hoverSprite.size = size;
        normalSprite.sortingOrder = hoverSprite.sortingOrder = 10;

        // Re-center the label inside the resized button (drop the vanilla AspectPosition offsets).
        Transform container = buttonText.transform.parent;
        UnityEngine.Object.Destroy(container.GetComponent<AspectPosition>());
        UnityEngine.Object.Destroy(buttonText.GetComponent<AspectPosition>());
        Vector3 cp = container.localPosition;            container.localPosition = new Vector3(0f, cp.y, cp.z);
        Vector3 tp = buttonText.transform.localPosition; buttonText.transform.localPosition = new Vector3(0f, tp.y, tp.z);
        buttonText.horizontalAlignment = HorizontalAlignmentOptions.Center;
        var tmpRenderer = buttonText.GetComponent<MeshRenderer>();
        if (tmpRenderer != null) tmpRenderer.sortingOrder = 12;

        var col    = button.GetComponent<BoxCollider2D>();
        col.size   = size;
        col.offset = Vector2.zero;

        button.gameObject.SetActive(true);
        buttonText.ForceMeshUpdate();
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
