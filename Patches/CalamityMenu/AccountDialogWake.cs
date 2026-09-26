using HarmonyLib;
using UnityEngine;

namespace EndKnot.Patches.CalamityMenu;

// VanillaSuppressor keeps AccountManager inactive in the menu, but it also owns the vanilla
// login dialogs (sign-in, age gate, privacy policy, guardian e-mail, login success/failure).
// Some of those wait for a button press before the login flow continues, so an invisible one
// stalls the whole start-up. Wake AccountManager whenever vanilla is about to show one of them;
// CalamityVisibility puts it back to sleep once the dialog is gone.
internal static class AccountDialogWake
{
    private static void Wake()
    {
        try
        {
            if (!AccountManager.InstanceExists) return;

            AccountManager am = AccountManager.Instance;
            if (am == null || am.gameObject.activeSelf) return;

            am.gameObject.SetActive(true);
            CalamityVisibility.BeginAccountDialog(am);
            Logger.Info("AccountManager woken for a vanilla account dialog", "AccountDialogWake");
        }
        catch (System.Exception e) { Logger.Warn($"Wake: {e.Message}", "AccountDialogWake"); }
    }

    [HarmonyPatch(typeof(AccountManager), nameof(AccountManager.SignInFail))]
    private static class SignInFailPatch
    {
        public static void Prefix() => Wake();
    }

    [HarmonyPatch(typeof(AccountManager), nameof(AccountManager.SignInSuccess))]
    private static class SignInSuccessPatch
    {
        public static void Prefix() => Wake();
    }

    [HarmonyPatch(typeof(AccountManager), nameof(AccountManager.PlatformSignInFail))]
    private static class PlatformSignInFailPatch
    {
        public static void Prefix() => Wake();
    }

    [HarmonyPatch(typeof(AccountManager), nameof(AccountManager.ShowAgeGate))]
    private static class ShowAgeGatePatch
    {
        public static void Prefix() => Wake();
    }

    [HarmonyPatch(typeof(AccountManager), nameof(AccountManager.ShowPermissionsRequestForm))]
    private static class ShowPermissionsRequestFormPatch
    {
        public static void Prefix() => Wake();
    }

    [HarmonyPatch(typeof(AccountManager), nameof(AccountManager.ShowGuardianEmailSentConfirm))]
    private static class ShowGuardianEmailSentConfirmPatch
    {
        public static void Prefix() => Wake();
    }

    [HarmonyPatch(typeof(AccountManager), nameof(AccountManager.EditGuardianEmail))]
    private static class EditGuardianEmailPatch
    {
        public static void Prefix() => Wake();
    }

    [HarmonyPatch(typeof(AccountManager), nameof(AccountManager.UpdateMissingGuardianEmail))]
    private static class UpdateMissingGuardianEmailPatch
    {
        public static void Prefix() => Wake();
    }

    [HarmonyPatch(typeof(SignInScreen), nameof(SignInScreen.Open))]
    private static class SignInScreenOpenPatch
    {
        public static void Prefix() => Wake();
    }

    [HarmonyPatch(typeof(PrivacyPolicyScreen), nameof(PrivacyPolicyScreen.Show))]
    private static class PrivacyPolicyShowPatch
    {
        public static void Prefix() => Wake();
    }

    // True while any of the vanilla account dialogs is on screen.
    public static bool IsAnyOpen(AccountManager am)
    {
        if (am == null || !am.gameObject.activeInHierarchy) return false;

        return IsActive(am.genericInfoDisplayBox)
            || IsActive(am.enterDateOfBirthScreen)
            || IsActive(am.enterGuardianEmailWindow)
            || IsActive(am.updateGuardianEmailWindow)
            || IsActive(am.guardianEmailConfirmWindow)
            || IsActive(am.PrivacyPolicy)
            || (am.privacyPolicyBg != null && am.privacyPolicyBg.activeInHierarchy)
            || (am.signInScreen != null && am.signInScreen.IsOpen());
    }

    private static bool IsActive(Component c) => c != null && c.gameObject.activeInHierarchy;
}
