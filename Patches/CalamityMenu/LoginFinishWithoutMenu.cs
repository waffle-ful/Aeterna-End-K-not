using HarmonyLib;
using UnityEngine;

namespace EndKnot.Patches.CalamityMenu;

// The last part of the vanilla login flow assumes the main menu is still loaded: it starts the
// eject button animation on the menu and initialises the cosmetics store that lives in the menu.
// Vanilla hides the menu behind a curtain until login is done, but the Calamity menu is usable
// right away, so starting Freeplay (or anything else that leaves the menu scene) in the few
// seconds after the platform login throws inside that part. The coroutine dies there: no friend
// code, no inventory, loginFlowFinished stays false, and hosting later gets kicked.
// When the menu is gone, skip only the menu-bound pieces and let the rest of the flow run.
internal static class LoginFinishWithoutMenu
{
    // BeginFinalPartsOfLoginFlow = eject button animation + WaitForStorageToSave (which goes on to
    // the friend code, the store and EndFinalPartsOfLoginFlow*).
    [HarmonyPatch(typeof(EOSManager), nameof(EOSManager.BeginFinalPartsOfLoginFlow))]
    private static class BeginFinalPartsPatch
    {
        public static bool Prefix(EOSManager __instance)
        {
            if (Object.FindObjectOfType<MainMenuManager>() != null) return true;

            Logger.Info("Main menu gone before login finished; continuing the login without it", "LoginFinishWithoutMenu");
            __instance.StartCoroutine(__instance.WaitForStorageToSave());
            return false;
        }
    }

    // The store initialisation further down the same flow, when the store went away with the menu.
    // Vanilla initialises it again the next time the main menu opens.
    [HarmonyPatch(typeof(StoreMenu), nameof(StoreMenu.Initialize))]
    private static class StoreInitializePatch
    {
        public static bool Prefix(StoreMenu __instance)
        {
            if (__instance != null) return true;

            Logger.Info("Store gone before login finished; skipped its setup", "LoginFinishWithoutMenu");
            return false;
        }
    }
}
