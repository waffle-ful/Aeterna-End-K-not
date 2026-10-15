using System;
using HarmonyLib;
using UnityEngine;

namespace SpawnEditorKit;

// Ported from haru03741-glitch/More-Roles-Plus@6cc7e63 (ability button copies in the bottom-right grid)
// Freeplay buttons of the spawn editor: copies of the vanilla ability button, laid out by the vanilla GridArrange.
// Clicks are taken in a DoClick prefix, not a UnityEvent listener: a managed lambda on a UnityEvent keeps a
// reference cycle across the two runtimes and is never collected.
[HarmonyPatch]
public static class SpawnEditorButtons
{
    private static AbilityButton _pin, _settings;
    private static IntPtr _pinPtr, _settingsPtr;

    // The cooldown display gives each copy its own material, which is not destroyed with the GameObject.
    private static Material _pinMat, _settingsMat;

    private static float _nextTry;

    // Creates the two buttons when the HUD has an ability button to copy. Retries at most once a second.
    public static void Ensure(string pinLabel, string settingsLabel)
    {
        if (_pin && _settings) return;

        float now = Time.unscaledTime;
        if (now < _nextTry) return;
        _nextTry = now + 1f;

        if (!HudManager.InstanceExists) return;
        AbilityButton src = HudManager.Instance.AbilityButton;
        if (!src) return;

        Destroy();
        _pin = Create(src, "SpawnEditorPin", SpawnEditorArt.ButtonPin, pinLabel, out _pinMat);
        _settings = Create(src, "SpawnEditorSettings", SpawnEditorArt.ButtonGear, settingsLabel, out _settingsMat);
        _pinPtr = _pin.Pointer;
        _settingsPtr = _settings.Pointer;
        Arrange();
    }

    private static AbilityButton Create(AbilityButton src, string name, Sprite icon, string label, out Material material)
    {
        material = null;
        AbilityButton b = UnityEngine.Object.Instantiate(src, src.transform.parent);

        try
        {
            Setup(b, name, icon, label);

            // Only a material of the copy's own is ours to destroy; the original's must stay.
            Material own = b.graphic.material;
            Material shared = src.graphic ? src.graphic.sharedMaterial : null;
            if (own && (!shared || own.Pointer != shared.Pointer)) material = own;
            return b;
        }
        catch
        {
            if (b && b.graphic && b.graphic.material && (!src.graphic || b.graphic.material.Pointer != src.graphic.sharedMaterial?.Pointer))
                UnityEngine.Object.Destroy(b.graphic.material);
            if (b) UnityEngine.Object.Destroy(b.gameObject);
            throw;
        }
    }

    private static void Setup(AbilityButton b, string name, Sprite icon, string label)
    {
        b.name = name;

        // The vanilla label component would write the role's ability name back over ours.
        var translator = b.buttonLabelText.GetComponent<TextTranslatorTMP>();
        if (translator) UnityEngine.Object.Destroy(translator);
        b.buttonLabelText.text = label;

        b.graphic.sprite = icon;
        b.graphic.SetCooldownNormalizedUvs();

        Transform uses = b.transform.Find("Uses");
        if (uses) uses.gameObject.SetActive(false);

        b.SetCoolDown(0f, 1f);
        b.SetEnabled();
        b.gameObject.SetActive(true);
    }

    public static void SetPinLabel(string label)
    {
        if (_pin) _pin.buttonLabelText.text = label;
    }

    public static void Destroy()
    {
        // Hidden first: Destroy waits for the end of the frame, and the grid below must not count them.
        if (_pin)
        {
            _pin.gameObject.SetActive(false);
            UnityEngine.Object.Destroy(_pin.gameObject);
        }

        if (_settings)
        {
            _settings.gameObject.SetActive(false);
            UnityEngine.Object.Destroy(_settings.gameObject);
        }

        if (_pinMat) UnityEngine.Object.Destroy(_pinMat);
        if (_settingsMat) UnityEngine.Object.Destroy(_settingsMat);
        _pin = _settings = null;
        _pinMat = _settingsMat = null;
        _pinPtr = _settingsPtr = IntPtr.Zero;
        Arrange();
    }

    private static void Arrange()
    {
        if (!HudManager.InstanceExists) return;
        AbilityButton src = HudManager.Instance.AbilityButton;
        if (!src || !src.transform.parent) return;
        var grid = src.transform.parent.GetComponent<GridArrange>();
        if (grid) grid.ArrangeChilds();
    }

    [HarmonyPatch(typeof(AbilityButton), nameof(AbilityButton.DoClick))]
    [HarmonyPrefix]
    public static bool DoClickPrefix(AbilityButton __instance)
    {
        IntPtr p = __instance.Pointer;
        if (p == IntPtr.Zero) return true;

        // A destroyed button's address can be reused by another object, so only live buttons count.
        bool pin = p == _pinPtr && _pin;
        bool settings = p == _settingsPtr && _settings;
        if (!pin && !settings) return true;

        // Ours never falls through to the vanilla click, which would use the copied role ability.
        try
        {
            SpawnEditorUi ui = SpawnEditorUi.Instance;
            if (!ui) return false;
            if (pin) ui.PressPin();
            else ui.PressSettings();
        }
        catch (Exception e) { SpawnStore.Host?.Warn($"Spawn editor button failed: {e}"); }

        return false;
    }
}
