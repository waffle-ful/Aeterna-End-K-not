using System;
using EndKnot.Modules.CalamityMenu;
using HarmonyLib;
using UnityEngine;

namespace EndKnot.Patches.CalamityMenu;

[HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.Start))]
public static class CalamityMenuPatch
{
    private static AccountTab _accountTab;

    // Hide vanilla 10-button strip BEFORE Start runs its sign-in / icon-loading
    // coroutines. SetActive(false) inside Postfix is too late — vanilla code
    // touches transforms during Start() and races with our suppression.
    [HarmonyPrefix]
    [HarmonyPriority(800)]   // run before TitleLogoPatch so vanilla buttons are gone before its setup
    public static void Prefix(MainMenuManager __instance)
    {
        if (!CalamityMenuState.Active) return;

        try
        {
            foreach (var btn in new[]
            {
                __instance.playButton?.gameObject,
                __instance.myAccountButton?.gameObject,
                __instance.settingsButton?.gameObject,
                __instance.creditsButton?.gameObject,
                __instance.quitButton?.gameObject,
                __instance.inventoryButton?.gameObject,
                __instance.shopButton?.gameObject,
                __instance.newsButton?.gameObject,
                __instance.freePlayButton?.gameObject,
                __instance.howToPlayButton?.gameObject,
            })
                btn?.SetActive(false);
        }
        catch (Exception ex) { Logger.Exception(ex, "CalamityMenuPatch.Prefix"); }
    }

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]   // run AFTER TitleLogoPatch so RightPanel/CloseRightButton/Tint setup happens before VanillaSuppressor disables LeftPanel
    public static void Postfix(MainMenuManager __instance)
    {
        if (!CalamityMenuState.Active) return;

        Logger.Info("Calamity menu setup begin", "CalamityMenuPatch");

        // Reset per-scene state so VanillaSuppressor re-runs on scene reload
        CalamityMenuState.VanillaSuppressed = false;
        CalamityVisibility.Reset();
        CalamityButtons.ResetPopoverShowState();
        _accountTab = null;

        // Order matters: Fonts → MenuRoot → FadeIn → Suppressor → visuals → Buttons → FeatureBridge.
        // FadeIn lives ABOVE the other layers so its overlay covers them as they're being built;
        // building it BEFORE Suppressor ensures the black overlay is already up when vanilla
        // teardown starts (avoids a flash of half-suppressed vanilla UI on the first frame).
        SafeStep("Fonts",       () => CalamityFonts.Capture(__instance));
        SafeStep("MenuRoot",    () => MenuRoot.Create(__instance));
        SafeStep("FadeIn",      () => CalamityFadeIn.Build(CalamityMenuState.Root.transform));
        SafeStep("Suppressor",  () => VanillaSuppressor.Apply(__instance));
        SafeStep("Background",  () => CalamityBackground.Build(MenuRoot.GetLayer("BackgroundLayer")));
        SafeStep("Particles",   () => CalamityParticles.Init(MenuRoot.GetLayer("ParticleLayer")));
        SafeStep("Logo",        () => CalamityLogo.Build(MenuRoot.GetLayer("LogoLayer")));
        SafeStep("Buttons",     () => CalamityButtons.Build(__instance, MenuRoot.GetLayer("ButtonLayer")));
        SafeStep("FeatureBridge", () => EndKnotFeatureBridge.Init(__instance, MenuRoot.GetLayer("OverlayLayer")));

        // RightPanel fallback init: if TitleLogoPatch.Postfix returned early (LeftPanel was
        // already suppressed by our Prefix), TitleLogoPatch.RightPanel/RightPanelOp stay
        // null/(0,0,0). Re-resolve here so MainMenuManagerPatch.LateUpdate's slide animation
        // and CalamityVisibility have correct anchors. Then push it 10 units off-screen X.
        var rp = TitleLogoPatch.RightPanel != null ? TitleLogoPatch.RightPanel : GameObject.Find("RightPanel");
        if (rp != null)
        {
            if (TitleLogoPatch.RightPanel == null || TitleLogoPatch.RightPanel != rp)
            {
                var ap = rp.GetComponent<AspectPosition>();
                if (ap != null) UnityEngine.Object.Destroy(ap);
                TitleLogoPatch.RightPanel = rp;
                TitleLogoPatch.RightPanelOp = rp.transform.localPosition;
                Logger.Info("RightPanel fallback-initialized in CalamityMenuPatch (TitleLogoPatch early-returned)", "CalamityMenuPatch");
            }
            rp.transform.localPosition = TitleLogoPatch.RightPanelOp + new Vector3(10f, 0f, 0f);
        }

        // Reset slide-state flag so the panel doesn't immediately slide in on scene load.
        MainMenuManagerPatch.ShowingPanel = false;

        Logger.Info("Calamity menu setup done", "CalamityMenuPatch");
    }

    private static void SafeStep(string name, Action action)
    {
        try { action(); }
        catch (Exception ex) { Logger.Exception(ex, $"CalamityMenuPatch.{name}"); }
    }

    [HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.LateUpdate))]
    [HarmonyPostfix]
    public static void LateUpdate_Postfix()
    {
        if (!CalamityMenuState.Active) return;
        CalamityParticles.UpdateAll(Time.deltaTime);
        EndKnotFeatureBridge.Tick();
        CalamityVisibility.Tick();
        CalamityFadeIn.Tick();

        // AccountTab re-enables itself via async sign-in events; keep it suppressed
        _accountTab ??= UnityEngine.Object.FindObjectOfType<AccountTab>();
        if (_accountTab != null && _accountTab.gameObject.activeSelf)
            _accountTab.gameObject.SetActive(false);
    }
}
