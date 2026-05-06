using EndKnot.Modules.CalamityMenu;
using UnityEngine;

namespace EndKnot.Patches.CalamityMenu;

public static class VanillaSuppressor
{
    public static void Apply(MainMenuManager mm)
    {
        if (CalamityMenuState.VanillaSuppressed) return;

        // EHR feature bridge needs this before Start_Postfix is skipped
        SimpleButton.SetBase(mm.creditsButton);

        // SpriteRenderer / ambient objects
        DisableByName("BackgroundTexture");
        DisableByName("WindowShine");
        DisableByName("ScreenCover");
        DisableByName("Ambience");
        DisableByName("Divider");
        // LOGO-AU is kept alive; CalamityLogo repositions it

        // LeftPanel: reparent its children out FIRST (mirroring vanilla EHR TitleLogoPatch),
        // otherwise FreeplayPopover and other overlays parented under it become
        // activeInHierarchy=false and are invisible even when their OpenOverlayMenu fires.
        var leftPanel = GameObject.Find("LeftPanel");
        if (leftPanel != null)
        {
            var leftParent = leftPanel.transform.parent;
            leftPanel.ForEachChild((Il2CppSystem.Action<GameObject>)(x => x.transform.SetParent(leftParent)));
            leftPanel.SetActive(false);
        }

        // RightPanel: stays active and is configured by TitleLogoPatch.SetupRightPanelForCalamity
        // (off-screen position, close button, tint). The Multiplayer button slides it in via
        // mm.OpenGameModeMenu() through the existing RightPanel slide animation.

        // Account / social UI that sits above MainMenuButtons
        Object.FindObjectOfType<AccountTab>()?.gameObject.SetActive(false);
        DisableByName("FriendsButton");
        DisableByName("NewRequest");

        // AccountManager root container (re-enables on async sign-in events; we
        // also kill the whole hierarchy so its children can't pop back).
        Object.FindObjectOfType<AccountManager>()?.gameObject.SetActive(false);

        // StoreMenu (vanilla cosmetics store) sits behind the menu and bleeds
        // through with the Calamity background.
        DisableByName("StoreMenu");

        // Vanilla menu shows a "Dropship + idle" PoolablePlayer character
        // standing in the background; sprite-name based suppression because
        // the GameObject chain is unstable across scene loads.
        foreach (SpriteRenderer sr in Object.FindObjectsOfType<SpriteRenderer>(true))
        {
            if (sr == null || sr.sprite == null) continue;
            string spriteName = sr.sprite.name;
            if (spriteName == "Dropship" || spriteName == "idle")
                sr.enabled = false;
        }

        // SaveIconCamera renders the "save in progress" indicator on top of the
        // Calamity menu — disable the camera itself rather than the GameObject
        // (the GO is shared with other systems).
        foreach (Camera cam in Object.FindObjectsOfType<Camera>(true))
        {
            if (cam != null && cam.name == "SaveIconCamera")
            {
                cam.enabled = false;
                break;
            }
        }

        // Vanilla EjectMainMenu (red X eject easter-egg button bottom-right)
        Object.FindObjectOfType<EjectMainMenu>()?.gameObject.SetActive(false);

        // Vanilla VersionShower ("vX.Y.Z (build num: ...)") sits at bottom-center and
        // overlaps the Calamity social buttons. EHR's PingTracker shows our own credentials.
        Object.FindObjectOfType<VersionShower>()?.gameObject.SetActive(false);

        // All vanilla main-menu buttons
        foreach (var btn in new[]
        {
            mm.playButton?.gameObject,
            mm.myAccountButton?.gameObject,
            mm.settingsButton?.gameObject,
            mm.creditsButton?.gameObject,
            mm.quitButton?.gameObject,
            mm.inventoryButton?.gameObject,
            mm.shopButton?.gameObject,
            mm.newsButton?.gameObject,
            mm.freePlayButton?.gameObject,
            mm.howToPlayButton?.gameObject,
        })
            btn?.SetActive(false);

        // PlayerParticles: keep alive for EjectMainMenu, just hide
        Object.FindObjectOfType<PlayerParticles>()?.gameObject.SetActive(false);

        // screenTint — mm.screenTint reference goes stale after scene reload
        // (Multi → lobby → Exit re-parents Tint back under MainUI). Disabling the
        // captured reference does nothing then. GameObject.Find("Tint") is the
        // reliable path; this is the actual fix for the right-half darkening.
        if (mm.screenTint != null) mm.screenTint.enabled = false;
        GameObject.Find("Tint")?.SetActive(false);

        CalamityMenuState.VanillaSuppressed = true;
    }

    private static void DisableByName(string name)
    {
        GameObject.Find(name)?.SetActive(false);
    }
}
