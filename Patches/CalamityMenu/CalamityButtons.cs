using System;
using System.Reflection;
using AmongUs.GameOptions;
using EndKnot.Patches;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.Events;

namespace EndKnot.Patches.CalamityMenu;

public static class CalamityButtons
{
    private static readonly Color NormalColor = new(0.65f, 0.70f, 0.88f, 1f);
    private static readonly Color HoverColor  = Color.white;
    private const float NormalScale = 1.00f;
    private const float HoverScale  = 1.08f;
    private const float FontSize    = 2.5f;

    public static void Build(MainMenuManager mm, Transform buttonLayer)
    {
        // Spacing 0.5, group center shifted to y=-0.1 to leave room for logo above
        var defs = new (string label, float y, Action onClick)[]
        {
            (Translator.GetString("MainMenu.Calamity.SinglePlayer"), +0.9f,
                () =>
                {
                    Logger.Info("SinglePlayer clicked", "CalamityButtons");
                    CalamityVisibility.HideMenuContent();
                    OpenFreeplayPopover(mm);
                }),

            // Multiplayer: AU 2026 changed `mm.OpenGameModeMenu()` to scene-transition into
            // MatchMaking the first time it's called (loads MMOnlineManager); the user comes
            // back to MainMenu and only the SECOND OpenGameModeMenu call opens the menu in
            // place. From the user's POV the first click "did nothing" then second click works.
            //
            // Fire the vanilla PlayOnlineButton's full OnClick chain instead — that mirrors
            // a real button press (ResetScreen + OpenGameModeMenu + SceneChanger.Click), so
            // the scene transition is intentional and the first click reaches MatchMaking
            // cleanly. mm.playButton.gameObject is SetActive(false) by VanillaSuppressor but
            // UnityEvent.Invoke fires listeners regardless of activeInHierarchy.
            // AU 2026 changed `mm.OpenGameModeMenu()` so the FIRST call scene-transitions
            // into MatchMaking (loading MMOnlineManager); the user comes back to MainMenu
            // and only the SECOND call opens the menu in place. From the user's POV the
            // first click "did nothing" then second click works. Tried PlayOnlineButton.
            // OnClick.Invoke() with temporary playButton activation — vanilla listeners
            // still no-op, so reverted to the direct OpenGameModeMenu call. Workaround
            // for users: click Multi twice. Real fix needs vanilla AU 2026 source review.
            (Translator.GetString("MainMenu.Calamity.Multiplayer"),  +0.4f,
                () =>
                {
                    Logger.Info($"Multiplayer clicked. ShowingPanel={MainMenuManagerPatch.ShowingPanel}", "CalamityButtons");
                    MultiplayerMapIdFix();
                    CalamityVisibility.HideMenuContent();
                    mm.OpenGameModeMenu();
                    Logger.Info("OpenGameModeMenu returned", "CalamityButtons");
                }),

            (Translator.GetString("MainMenu.Calamity.Settings"),     -0.1f,
                () => { CalamityVisibility.HideMenuContent(); mm.settingsButton.OnClick.Invoke(); }),

            (Translator.GetString("MainMenu.Calamity.Credits"),      -0.6f,
                () => { CalamityVisibility.HideMenuContent(); mm.creditsButton.OnClick.Invoke(); }),

            (Translator.GetString("MainMenu.Calamity.Quit"),         -1.1f,
                () => mm.quitButton.OnClick.Invoke()),
        };

        foreach (var (label, y, onClick) in defs)
            CreateTextButton(buttonLayer, label, new Vector3(0f, y, 0f), onClick);
    }

    private static void CreateTextButton(Transform parent, string label, Vector3 pos, Action onClick)
    {
        var go = new GameObject($"CalamityBtn_{label}");
        go.transform.SetParent(parent);
        go.transform.localPosition = pos;
        go.transform.localScale    = Vector3.one;

        var tmp = go.AddComponent<TextMeshPro>();
        tmp.text               = label;
        tmp.fontSize           = FontSize;
        tmp.fontSizeMin        = FontSize;
        tmp.fontSizeMax        = FontSize;
        tmp.enableAutoSizing   = false;
        tmp.alignment          = TextAlignmentOptions.Center;
        tmp.fontStyle          = FontStyles.Bold;
        tmp.outlineColor       = new Color32(0, 0, 0, 200);
        tmp.outlineWidth       = 0.18f;
        tmp.color              = NormalColor;
        tmp.sortingOrder       = 10;
        tmp.characterSpacing   = -3f;
        CalamityFonts.Apply(tmp);

        // Collider for mouse events
        var col = go.AddComponent<BoxCollider2D>();
        col.size   = new Vector2(5f, 0.65f);
        col.offset = Vector2.zero;

        var btn         = go.AddComponent<PassiveButton>();
        btn.OnClick     = new();
        btn.OnMouseOver = new();
        btn.OnMouseOut  = new();

        btn.OnClick    .AddListener((UnityAction)onClick);
        btn.OnMouseOver.AddListener((UnityAction)(() => SetHover(go.transform, tmp, true)));
        btn.OnMouseOut .AddListener((UnityAction)(() => SetHover(go.transform, tmp, false)));

        SetHover(go.transform, tmp, false);
    }

    private static void SetHover(Transform t, TextMeshPro tmp, bool hover)
    {
        tmp.color          = hover ? HoverColor  : NormalColor;
        t.localScale       = Vector3.one * (hover ? HoverScale : NormalScale);
    }

    private static void MultiplayerMapIdFix()
    {
        GameOptionsManager.Instance.Initialize();
        var opts = GameOptionsManager.Instance.normalGameHostOptions;
        if (opts.MapId == 3 || (opts.MapId > 5 && !SubmergedCompatibility.Loaded))
        {
            opts.MapId = 0;
            GameOptionsManager.Instance.SaveNormalHostOptions();
        }
    }

    // freePlayButton is SetActive(false) by EHR; invoke works even when inactive
    // but if the underlying UnityEvent is guarded, re-enable briefly.
    private static void InvokeSafe(PassiveButton btn)
    {
        if (btn == null) return;
        bool wasActive = btn.gameObject.activeSelf;
        if (!wasActive) btn.gameObject.SetActive(true);
        btn.OnClick.Invoke();
        if (!wasActive) btn.gameObject.SetActive(false);
    }

    // FreeplayPopover (and its child SkeldButton/content/background) live somewhere
    // under the inactive vanilla menu hierarchy in Calamity mode. Reparent to the active
    // MainMenuManager so children become activeInHierarchy=true, then activate everything
    // and run our injection. Skipping InvokeSafe(freePlayButton) — vanilla's OnClick may
    // toggle the popover (close on second click), which broke re-open after a BACK press.
    //
    // We do NOT call popover.Show(). On the second menu scene load it kicks off a vanilla
    // coroutine that ends up deactivating content (or reparenting it back under the
    // inactive Scaler) within seconds — the user-visible "popover briefly shows and then
    // disappears" symptom. Vanilla button click wiring lives in FreeplayPopoverButton.Awake
    // (already fired when the prefab instantiates), and we override OnClick handlers via
    // RewireMapClicks anyway, so Show() is unnecessary for our use.
    private static FreeplayPopover _popoverShowed;

    private static void OpenFreeplayPopover(MainMenuManager mm)
    {
        Logger.Info("OpenFreeplayPopover entered", "CalamityButtons");
        var popover = Object.FindObjectOfType<FreeplayPopover>(true);
        if (popover == null)
        {
            Logger.Warn("FreeplayPopover not found in scene", "CalamityButtons");
            return;
        }

        var pTr = popover.transform;
        Logger.Info($"popover found: parent={(pTr.parent != null ? pTr.parent.name : "null")} parentActive={(pTr.parent != null ? pTr.parent.gameObject.activeInHierarchy.ToString() : "?")} activeSelf={popover.gameObject.activeSelf} world={pTr.position} _popoverShowed={(_popoverShowed != null ? "set" : "null")}", "CalamityButtons");

        // Reparent to mm.transform — vanilla parent (Scaler) is inactiveInHierarchy in
        // Calamity mode (LeftPanel-driven cascade), so the popover would be invisible
        // even with activeSelf=true. Force-position to (0,0,0) so it lands at screen
        // center regardless of whatever local offset vanilla left in the prefab.
        if (pTr.parent != mm.transform)
            pTr.SetParent(mm.transform, worldPositionStays: false);
        pTr.localPosition = Vector3.zero;
        pTr.localScale = Vector3.one;

        if (!popover.gameObject.activeSelf) popover.gameObject.SetActive(true);
        if (popover.content != null) popover.content.SetActive(true);
        if (popover.background != null) popover.background.SetActive(true);

        if (_popoverShowed != popover)
        {
            InjectDleksButton(popover);
            _popoverShowed = popover;
        }

        // Tell Tick to keep state pinned each frame — defends against any vanilla
        // animation or coroutine that reparents/repositions/deactivates content.
        CalamityVisibility.PopoverDesiredParent = mm.transform;
        CalamityVisibility.FreeplayShouldStayOpen = true;

        RewireMapClicks(popover);
        RealignBottomRow(popover);

        Logger.Info($"OpenFreeplayPopover done; popover world={pTr.position} content.activeInHierarchy={(popover.content != null && popover.content.activeInHierarchy)}", "CalamityButtons");
    }

    public static void ResetPopoverShowState() => _popoverShowed = null;

    // Move Fungle (vanilla centers it alone in row 3) to the left column, place injected
    // Dleks button on the right column. Indexed access — vanilla buttons[] array order is
    // Skeld(0), MiraHQ(1), Polus(2), Airship(3), Fungle(4) (Dleks is skipped in vanilla),
    // and GetComponentsInChildren returns them in scene-hierarchy order matching this.
    private static void RealignBottomRow(FreeplayPopover popover)
    {
        try
        {
            if (popover == null || popover.content == null) return;
            var allBtns = popover.content.GetComponentsInChildren<FreeplayPopoverButton>(true);
            if (allBtns == null || allBtns.Length < 5) return;

            var skeldBtn  = allBtns[0];
            var miraBtn   = allBtns[1];
            var fungleBtn = allBtns[4];
            if (skeldBtn == null || miraBtn == null || fungleBtn == null) return;

            float colLeftX  = skeldBtn.transform.localPosition.x;
            float colRightX = miraBtn.transform.localPosition.x;

            Vector3 fp = fungleBtn.transform.localPosition;
            fungleBtn.transform.localPosition = new Vector3(colLeftX, fp.y, fp.z);

            var dleksGo = popover.content.transform.Find("DleksButton");
            if (dleksGo != null)
            {
                Vector3 dp = dleksGo.localPosition;
                dleksGo.localPosition = new Vector3(colRightX, fungleBtn.transform.localPosition.y, dp.z);
            }
        }
        catch (Exception ex) { Logger.Exception(ex, "RealignBottomRow"); }
    }

    // Vanilla FreeplayPopover.PlayMap(map) routes through a private hostGameButton field
    // that doesn't reliably fire under Calamity's reparented hierarchy — clicks select a
    // map (green border) but the scene never loads. Replace every map button's OnClick
    // with our own handler that drives HostLocalGameButton directly.
    private static void RewireMapClicks(FreeplayPopover popover)
    {
        try
        {
            if (popover == null || popover.content == null) return;

            // popover.GetComponentInChildren can return null on the second scene load
            // (the serialized hostGameButton is sometimes outside popover's hierarchy in
            // vanilla AU's prefab). Fall back to a scene-wide search.
            var hostBtn = popover.GetComponentInChildren<HostLocalGameButton>(true)
                          ?? Object.FindObjectOfType<HostLocalGameButton>(true);

            var allBtns = popover.content.GetComponentsInChildren<FreeplayPopoverButton>(true);
            for (int i = 0; i < allBtns.Length; i++)
            {
                var btn = allBtns[i];
                if (btn == null) continue;
                var passive = btn.GetComponent<PassiveButton>() ?? btn.GetComponentInChildren<PassiveButton>(true);
                if (passive == null) continue;

                MapNames map = btn.Map;
                HostLocalGameButton capturedHost = hostBtn; // may be null; LaunchFreeplay re-resolves
                passive.OnClick = new();
                passive.OnClick.AddListener((UnityAction)(() => LaunchFreeplay(capturedHost, map)));
            }
        }
        catch (Exception ex) { Logger.Exception(ex, "RewireMapClicks"); }
    }

    // Adds a "Dleks" (reverse-Skeld) button to the freeplay map list. Clones Skeld at
    // index 0, places it on the right of row 3, mirrors visuals, and wires the launch
    // handler. Indexed lookups — see RealignBottomRow comment for vanilla button order.
    private static void InjectDleksButton(FreeplayPopover popover)
    {
        try
        {
            if (popover == null || popover.content == null) return;
            if (popover.content.transform.Find("DleksButton") != null) return; // idempotent

            var allBtns = popover.content.GetComponentsInChildren<FreeplayPopoverButton>(true);
            if (allBtns == null || allBtns.Length < 5) return;

            var skeldBtn  = allBtns[0];
            var miraBtn   = allBtns[1];
            var fungleBtn = allBtns[4];
            if (skeldBtn == null || miraBtn == null || fungleBtn == null) return;

            // Column X coords from row 1 (Skeld | Mira)
            float colLeftX  = skeldBtn.transform.localPosition.x;
            float colRightX = miraBtn.transform.localPosition.x;

            // Move Fungle to the left column (vanilla centers it alone in row 3)
            Vector3 fp = fungleBtn.transform.localPosition;
            fungleBtn.transform.localPosition = new Vector3(colLeftX, fp.y, fp.z);

            var dleksGo = Object.Instantiate(skeldBtn.gameObject, skeldBtn.transform.parent);
            dleksGo.name = "DleksButton";

            // Right column of row 3 (matches the realigned Fungle's Y/Z)
            Vector3 fungleNow = fungleBtn.transform.localPosition;
            dleksGo.transform.localPosition = new Vector3(colRightX, fungleNow.y, fungleNow.z);

            // Mirror visuals to convey "reversed Skeld"
            var sprites = dleksGo.GetComponentsInChildren<SpriteRenderer>(true);
            for (int i = 0; i < sprites.Length; i++)
                if (sprites[i] != null) sprites[i].flipX = !sprites[i].flipX;

            // The cloned FreeplayPopoverButton component still reports Map=Skeld via its
            // serialized field; rewire OnClick directly so it launches Dleks instead.
            var passive = dleksGo.GetComponent<PassiveButton>() ?? dleksGo.GetComponentInChildren<PassiveButton>(true);
            if (passive == null) return;

            var hostBtn = popover.GetComponentInChildren<HostLocalGameButton>(true)
                          ?? Object.FindObjectOfType<HostLocalGameButton>(true);

            HostLocalGameButton capturedHost = hostBtn; // may be null; LaunchFreeplay re-resolves
            passive.OnClick = new();
            passive.OnClick.AddListener((UnityAction)(() => LaunchFreeplay(capturedHost, MapNames.Dleks)));
        }
        catch (Exception ex) { Logger.Exception(ex, "InjectDleksButton"); }
    }

    // popover.hostGameButton is the SerializeField the vanilla popover uses internally —
    // grabbing it directly avoids picking up some other HostLocalGameButton (e.g. the main
    // menu's freePlayButton's component) when FindObjectOfType is the fallback. We resolve
    // the FieldInfo lazily once and reuse it.
    private static FieldInfo _popoverHostGameButtonField;

    private static FieldInfo GetPopoverHostGameButtonField()
    {
        return _popoverHostGameButtonField ??=
            AccessTools.Field(typeof(FreeplayPopover), "hostGameButton");
    }

    private static HostLocalGameButton ResolveHostBtn(FreeplayPopover popover, HostLocalGameButton fallback)
    {
        try
        {
            var field = GetPopoverHostGameButtonField();
            if (field != null && popover != null)
            {
                var serialized = field.GetValue(popover) as HostLocalGameButton;
                if (serialized != null) return serialized;
            }
        }
        catch (Exception ex) { Logger.Exception(ex, "ResolveHostBtn"); }
        return fallback ?? Object.FindObjectOfType<HostLocalGameButton>(true);
    }

    private static void LaunchFreeplay(HostLocalGameButton hostBtn, MapNames map)
    {
        Logger.Info($"LaunchFreeplay entered: map={map} cachedHostBtn={(hostBtn != null ? "set" : "null")}", "LaunchFreeplay");
        try
        {
            // Dleks (id 3) is a real vanilla map — Skeld with X-flipped visuals applied
            // by ShipStatus.Awake when MapId == 3. Passing targetMapId=0 + SetDleks=true
            // doesn't work for the freeplay/tutorial scene because no patch flips
            // Skeld→Dleks based on SetDleks alone there (DleksPatch.Prefix_UpdateMapImage
            // only retouches the lobby's MapImage UI, not the actual ship). Use the real
            // id and let vanilla handle the flip.
            byte targetMapId = (byte)map;
            GameOptionsMapPickerPatch.SetDleks = (map == MapNames.Dleks);

            // FreePlay reads from AmongUsClient.TutorialMapId, NOT NormalOptions.MapId
            // (confirmed via Mechanic.cs:220 and the field offset on AmongUsClient).
            // Setting NormalOptions made every selection load as Skeld (default 0).
            if (AmongUsClient.Instance != null)
            {
                Logger.Info($"AmongUsClient pre-launch: NetworkMode={AmongUsClient.Instance.NetworkMode} AmHost={AmongUsClient.Instance.AmHost} IsGameStarted={AmongUsClient.Instance.IsGameStarted} TutorialMapId={AmongUsClient.Instance.TutorialMapId}", "LaunchFreeplay");

                // Reset NetworkMode defensively. The actual second-click silent failure
                // root cause turned out to be DisconnectInternalPatch.Prefix throwing NRE
                // (ErrorText.Instance null) inside vanilla's CoStartGame teardown — fixed
                // separately. NetworkMode reset is kept as a low-cost belt for vanilla
                // session-state guards we may not see.
                AmongUsClient.Instance.NetworkMode = default;
                AmongUsClient.Instance.TutorialMapId = targetMapId;
            }

            // Also set normalGameHostOptions for visual consistency on map selection
            // visuals when other patches read it.
            if (Main.NormalOptions != null) Main.NormalOptions.MapId = targetMapId;
            var hostOpts = GameOptionsManager.Instance.normalGameHostOptions;
            if (hostOpts != null) hostOpts.MapId = targetMapId;

            // ShipStatus.Awake reads MapId from GameOptionsManager.CurrentGameOptions
            // (an IGameOptions wrapper, NOT NormalOptions), and that's the value vanilla
            // checks to apply the Dleks X-flip. Without this, Dleks freeplay loads as a
            // non-mirrored Skeld even though TutorialMapId=3.
            var current = GameOptionsManager.Instance.CurrentGameOptions;
            if (current != null) current.SetByte(ByteOptionNames.MapId, targetMapId);

            // Always re-resolve hostBtn via the popover's serialized hostGameButton field.
            // The cached reference from RewireMapClicks may have been a stale orphan from
            // a previous menu scene — using the popover's own private field guarantees we
            // hit the button vanilla would actually use.
            var popover = Object.FindObjectOfType<FreeplayPopover>(true);
            hostBtn = ResolveHostBtn(popover, hostBtn);
            if (hostBtn == null)
            {
                Logger.Warn("HostLocalGameButton not found at launch", "LaunchFreeplay");
                return;
            }

            Logger.Info($"LaunchFreeplay: targetMapId={targetMapId} hostBtn parent={(hostBtn.transform.parent != null ? hostBtn.transform.parent.name : "null")} parentActive={(hostBtn.transform.parent != null ? hostBtn.transform.parent.gameObject.activeInHierarchy.ToString() : "?")} activeSelf={hostBtn.gameObject.activeSelf} activeInHier={hostBtn.gameObject.activeInHierarchy}", "LaunchFreeplay");

            // Drop the popover keep-alive flag so the Tick doesn't re-activate content
            // while the freeplay scene is loading.
            CalamityVisibility.FreeplayShouldStayOpen = false;

            // OnClick reads AmongUsClient.TutorialMapId (set above) for the map; the button's
            // own NetworkMode is what gets copied to the client inside OnClick.
            hostBtn.NetworkMode = NetworkModes.FreePlay;
            hostBtn.OnClick();

            string postState = AmongUsClient.Instance != null
                ? $"NetworkMode={AmongUsClient.Instance.NetworkMode} TutorialMapId={AmongUsClient.Instance.TutorialMapId}"
                : "AmongUsClient=null";
            Logger.Info($"LaunchFreeplay: hostBtn.OnClick() returned. Post-state: {postState}", "LaunchFreeplay");
        }
        catch (Exception ex) { Logger.Exception(ex, "LaunchFreeplay"); }
    }
}
