using EndKnot.Modules.CalamityMenu;
using HarmonyLib;
using UnityEngine;

namespace EndKnot.Patches.CalamityMenu;

[HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.Start))]
[HarmonyPriority(Priority.First)]
public static class CalamityMenuPatch
{
    private static AccountTab _accountTab;

    [HarmonyPostfix]
    public static void Postfix(MainMenuManager __instance)
    {
        if (!CalamityMenuState.Active) return;

        Logger.Info("Calamity menu setup begin", "CalamityMenuPatch");

        // Reset per-scene state so VanillaSuppressor re-runs on scene reload
        CalamityMenuState.VanillaSuppressed = false;
        CalamityVisibility.Reset();
        CalamityButtons.ResetPopoverShowState();
        _accountTab = null;

        // Capture vanilla TMP font BEFORE we suppress vanilla buttons (which would
        // SetActive(false) the source button — its TMP font is still accessible
        // via the field even when inactive, but we capture early to be safe).
        SafeStep("Fonts",       () => CalamityFonts.Capture(__instance));
        SafeStep("Suppressor",  () => VanillaSuppressor.Apply(__instance));
        SafeStep("MenuRoot",    () => MenuRoot.Create(__instance));
        SafeStep("Background",  () => CalamityBackground.Build(MenuRoot.GetLayer("BackgroundLayer")));
        SafeStep("Particles",   () => CalamityParticles.Init(MenuRoot.GetLayer("ParticleLayer")));
        SafeStep("Logo",        () => CalamityLogo.Build(MenuRoot.GetLayer("LogoLayer")));
        SafeStep("Buttons",     () => CalamityButtons.Build(__instance, MenuRoot.GetLayer("ButtonLayer")));
        SafeStep("FeatureBridge", () => EndKnotFeatureBridge.Init(__instance, MenuRoot.GetLayer("OverlayLayer")));
        SafeStep("FadeIn",      () => CalamityFadeIn.Build(CalamityMenuState.Root.transform));

        var cam = Camera.main;
        if (cam != null)
            Logger.Info($"Calamity menu setup done; cam={cam.name} pos={cam.transform.position} ortho={cam.orthographicSize} aspect={cam.aspect}", "CalamityMenuPatch");
        else
            Logger.Info("Calamity menu setup done; cam.main=null", "CalamityMenuPatch");
    }

    private static void SafeStep(string name, System.Action action)
    {
        try { action(); }
        catch (System.Exception ex) { Logger.Exception(ex, $"CalamityMenuPatch.{name}"); }
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
        _accountTab ??= Object.FindObjectOfType<AccountTab>();
        if (_accountTab != null && _accountTab.gameObject.activeSelf)
            _accountTab.gameObject.SetActive(false);
    }
}
