using EndKnot.Modules.CalamityMenu;
using TMPro;
using UnityEngine;
using UnityEngine.Events;

namespace EndKnot.Patches.CalamityMenu;

/// <summary>
/// Hides Calamity menu content (buttons/logo/overlay) while a vanilla popover (FreeplayPopover,
/// OptionsMenuBehaviour, CreditsScreenPopUp) is open, so they don't appear on top of the popover
/// or steal clicks. Background and particle layers stay visible for visual continuity.
/// Also shows a Calamity-styled BACK button while a popover is up.
/// </summary>
public static class CalamityVisibility
{
    private static bool _menuHidden;
    private static GameObject _backButton;

    // Set to true while user wants the freeplay popover open. Tick re-asserts content
    // activation each frame to defeat vanilla Show()/Awake coroutines that deactivate
    // content shortly after we open it (popover flashes then disappears symptom).
    public static bool FreeplayShouldStayOpen;

    // Where the popover should live while open — typically MainMenuManager's transform.
    // Tick uses this to reparent it back if a vanilla coroutine moves it under the
    // inactive Scaler or any other hierarchy.
    public static Transform PopoverDesiredParent;

    private static int _tickDiagFrame;
    private static bool _prevShouldStayOpen;
    private static bool _prevPopExists;
    private static bool _prevContentExists;

    public static void HideMenuContent()
    {
        if (_menuHidden) return;
        SetLayerActive("ButtonLayer", false);
        SetLayerActive("LogoLayer", false);
        SetLayerActive("OverlayLayer", false);
        ShowBackButton();
        _menuHidden = true;
    }

    public static void Tick()
    {
        // Log every transition of FreeplayShouldStayOpen so we can see if/when something
        // resets it after OpenFreeplayPopover sets it to true.
        if (FreeplayShouldStayOpen != _prevShouldStayOpen)
        {
            Logger.Info($"FreeplayShouldStayOpen transition: {_prevShouldStayOpen} -> {FreeplayShouldStayOpen}", "CalamityVisibility");
            _prevShouldStayOpen = FreeplayShouldStayOpen;
        }

        // Keep freeplay popover pinned while user wants it open. Vanilla Awake/scene
        // wiring runs an async coroutine that deactivates content / reparents the popover
        // back under inactive Scaler shortly after we open it on the second menu scene
        // load — re-asserting parent, position, scale, and activeSelf every frame defeats
        // it (popover flashes then disappears symptom).
        if (FreeplayShouldStayOpen)
        {
            var pop = Object.FindObjectOfType<FreeplayPopover>(true);

            bool popExists = pop != null;
            if (popExists != _prevPopExists)
            {
                Logger.Info($"popover instance transition: exists={popExists}", "CalamityVisibility");
                _prevPopExists = popExists;
            }

            if (pop != null)
            {
                var pTr = pop.transform;
                bool contentExists = pop.content != null;
                if (contentExists != _prevContentExists)
                {
                    Logger.Info($"popover content transition: exists={contentExists}", "CalamityVisibility");
                    _prevContentExists = contentExists;
                }

                if (PopoverDesiredParent != null && pTr.parent != PopoverDesiredParent)
                {
                    Logger.Info($"Tick reparent: parent was {(pTr.parent != null ? pTr.parent.name : "null")}, restoring", "CalamityVisibility");
                    pTr.SetParent(PopoverDesiredParent, worldPositionStays: false);
                }

                if (pTr.localPosition != Vector3.zero) pTr.localPosition = Vector3.zero;
                if (pTr.localScale != Vector3.one) pTr.localScale = Vector3.one;

                if (!pop.gameObject.activeSelf) pop.gameObject.SetActive(true);
                if (pop.content != null && !pop.content.activeSelf) pop.content.SetActive(true);
                if (pop.background != null && !pop.background.activeSelf) pop.background.SetActive(true);

                // Periodic diagnostic — once every ~1s — so the log shows whether vanilla
                // is fighting us across the "few seconds" window.
                _tickDiagFrame++;
                if (_tickDiagFrame == 60 || _tickDiagFrame == 180 || _tickDiagFrame == 300)
                {
                    Logger.Info($"popover diag t={_tickDiagFrame}: parent={(pTr.parent != null ? pTr.parent.name : "null")} parentActive={(pTr.parent != null ? pTr.parent.gameObject.activeInHierarchy.ToString() : "?")} activeSelf={pop.gameObject.activeSelf} contentSelf={(pop.content != null && pop.content.activeSelf)} contentHier={(pop.content != null && pop.content.activeInHierarchy)} world={pTr.position} scale={pTr.localScale}", "CalamityVisibility");
                }
            }
        }
        else
        {
            _tickDiagFrame = 0;
            _prevPopExists = false;
            _prevContentExists = false;
        }

        if (!_menuHidden) return;

        if (IsAnyPopoverOpen()) return;

        SetLayerActive("ButtonLayer", true);
        SetLayerActive("LogoLayer", true);
        SetLayerActive("OverlayLayer", true);
        if (_backButton != null) _backButton.SetActive(false);
        _menuHidden = false;
    }

    public static void Reset()
    {
        _menuHidden = false;
        _backButton = null;
        FreeplayShouldStayOpen = false;
        PopoverDesiredParent = null;
        _tickDiagFrame = 0;
        _prevShouldStayOpen = false;
        _prevPopExists = false;
        _prevContentExists = false;
    }

    private static void ShowBackButton()
    {
        if (_backButton == null) CreateBackButton();
        else _backButton.SetActive(true);
    }

    private static void CreateBackButton()
    {
        var root = CalamityMenuState.Root;
        if (root == null) return;

        var go = new GameObject("CalamityBackButton");
        go.transform.SetParent(root.transform);
        go.transform.localPosition = new Vector3(-4.5f, -2.6f, 0f); // bottom-left, outside popover content
        go.transform.localScale = Vector3.one;

        var tmp = go.AddComponent<TextMeshPro>();
        tmp.text             = "< BACK";
        tmp.fontSize         = 2.2f;
        tmp.fontSizeMin      = 2.2f;
        tmp.fontSizeMax      = 2.2f;
        tmp.enableAutoSizing = false;
        tmp.alignment        = TextAlignmentOptions.Center;
        tmp.fontStyle        = FontStyles.Bold;
        tmp.color            = new Color(0.9f, 0.85f, 0.65f, 1f);
        tmp.outlineColor     = new Color32(0, 0, 0, 220);
        tmp.outlineWidth     = 0.18f;
        tmp.sortingOrder     = 30; // above popover content
        CalamityFonts.Apply(tmp);

        var col    = go.AddComponent<BoxCollider2D>();
        col.size   = new Vector2(2.0f, 0.7f);
        col.offset = Vector2.zero;

        var btn         = go.AddComponent<PassiveButton>();
        btn.OnClick     = new();
        btn.OnMouseOver = new();
        btn.OnMouseOut  = new();

        var normal = new Color(0.9f, 0.85f, 0.65f, 1f);
        var hover  = Color.white;

        btn.OnClick    .AddListener((UnityAction)CloseAnyPopover);
        btn.OnMouseOver.AddListener((UnityAction)(() => { tmp.color = hover;  go.transform.localScale = Vector3.one * 1.08f; }));
        btn.OnMouseOut .AddListener((UnityAction)(() => { tmp.color = normal; go.transform.localScale = Vector3.one; }));

        _backButton = go;
    }

    private static void CloseAnyPopover()
    {
        Logger.Info("CloseAnyPopover entered (BACK button)", "CalamityVisibility");
        // Multiplayer RightPanel: HideRightPanel just sets ShowingPanel=false; the slide
        // animation in MainMenuManager_LateUpdate handles sliding it off-screen.
        if (MainMenuManagerPatch.ShowingPanel)
        {
            MainMenuManagerPatch.HideRightPanel();
            return;
        }

        var pop = Object.FindObjectOfType<FreeplayPopover>(true);
        if (pop != null && IsFreeplayOpen(pop))
        {
            // Drop the keep-alive flag FIRST so Tick stops re-asserting content active
            // (otherwise BACK can't actually close the popover).
            FreeplayShouldStayOpen = false;

            // Skip vanilla pop.Close() — it does internal cleanup (de-registering listeners,
            // resetting button visuals) that leaves the popover in a half-broken state when
            // reopened later, which manifests as missing map images / unresponsive map clicks.
            //
            // Also keep pop.gameObject active so OnDisable/OnEnable doesn't cycle on the
            // popover or its children (which broke map button rendering on second open).
            // Toggling content + background alone is enough for visibility and the
            // IsFreeplayOpen check (which reads content.activeInHierarchy).
            if (pop.content    != null) pop.content.SetActive(false);
            if (pop.background != null) pop.background.SetActive(false);
            return;
        }

        var opt = Object.FindObjectOfType<OptionsMenuBehaviour>(true);
        if (opt != null && opt.gameObject.activeInHierarchy)
        {
            opt.Close();
            opt.gameObject.SetActive(false);
            return;
        }

        var cre = Object.FindObjectOfType<CreditsScreenPopUp>(true);
        if (cre != null && cre.gameObject.activeInHierarchy) { cre.gameObject.SetActive(false); return; }
    }

    private static bool IsAnyPopoverOpen()
    {
        // RightPanel (Multiplayer): keep menu hidden while ShowingPanel is true OR while
        // the panel is still mid-slide off-screen. RightPanel.x ~ Op.x means slid in,
        // ~ Op.x+10 means fully off-screen.
        if (MainMenuManagerPatch.ShowingPanel) return true;
        if (TitleLogoPatch.RightPanel != null
            && TitleLogoPatch.RightPanel.transform.localPosition.x < TitleLogoPatch.RightPanelOp.x + 9f)
            return true;

        var pop = Object.FindObjectOfType<FreeplayPopover>(true);
        if (pop != null && IsFreeplayOpen(pop)) return true;

        var opt = Object.FindObjectOfType<OptionsMenuBehaviour>(true);
        if (opt != null && opt.gameObject.activeInHierarchy) return true;

        var cre = Object.FindObjectOfType<CreditsScreenPopUp>(true);
        if (cre != null && cre.gameObject.activeInHierarchy) return true;

        return false;
    }

    // FreeplayPopover.Close() may only deactivate content/background, not the wrapper.
    // Treat the popover as open if EITHER its content or its own GO is active.
    private static bool IsFreeplayOpen(FreeplayPopover pop)
    {
        if (pop.content != null) return pop.content.activeInHierarchy;
        return pop.gameObject.activeInHierarchy;
    }

    private static void SetLayerActive(string layerName, bool active)
    {
        var t = CalamityMenuState.Root?.transform.Find(layerName);
        if (t != null) t.gameObject.SetActive(active);
    }
}
