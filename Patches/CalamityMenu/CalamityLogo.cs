using TMPro;
using UnityEngine;

namespace EndKnot.Patches.CalamityMenu;

public static class CalamityLogo
{
    // App-lifetime flag: subtitle and divider show only on the first menu load.
    private static bool _subtitleShown;

    private static Transform _logoTransform;
    private static Vector3   _logoBasePos;
    private static float     _shakeTime;

    public static void Build(Transform logoLayer)
    {
        _logoTransform = null;
        _shakeTime     = 0f;

        Logger.Info($"Build start, logoLayer={(logoLayer != null ? logoLayer.name : "NULL")}", "CalamityLogo");

        // ── Among Us logo (vanilla sprite → TMP fallback) ────────────────
        // Create a fresh SpriteRenderer carrying the vanilla sprite instead of reparenting
        // LOGO-AU directly — reparenting loses renderer state on the second scene load.
        var auLogo    = FindAULogo();
        var auSr      = auLogo != null ? auLogo.GetComponent<SpriteRenderer>() : null;
        var logoSprite = auSr?.sprite;

        var basePos = new Vector3(0f, 1.95f, 0f);
        GameObject logoGo;

        if (logoSprite != null)
        {
            logoGo = new GameObject("CalamityLogoSprite");
            logoGo.transform.SetParent(logoLayer);
            logoGo.transform.localPosition = basePos;
            logoGo.transform.localScale    = auSr.transform.lossyScale;

            var sr = logoGo.AddComponent<SpriteRenderer>();
            sr.sprite         = logoSprite;
            sr.sortingLayerID = auSr.sortingLayerID;
            sr.sortingOrder   = auSr.sortingOrder + 1;

            Logger.Info($"Logo sprite: {logoSprite.name} {logoSprite.rect.width}x{logoSprite.rect.height}px lossyScale={auSr.transform.lossyScale} layer={auSr.sortingLayerName}/{auSr.sortingOrder}", "CalamityLogo");
        }
        else
        {
            // Vanilla sprite not found — fall back to TMP text.
            logoGo = new GameObject("AmongUsTextLogo");
            logoGo.transform.SetParent(logoLayer);
            logoGo.transform.localPosition = basePos;

            var fb = logoGo.AddComponent<TextMeshPro>();
            fb.text             = "AMONG  US";
            fb.fontSize         = 4.2f;
            fb.alignment        = TextAlignmentOptions.Center;
            fb.fontStyle        = FontStyles.Bold;
            fb.characterSpacing = 6f;
            fb.color            = Color.white;
            fb.outlineColor     = new Color32(40, 0, 60, 230);
            fb.outlineWidth     = 0.25f;
            fb.sortingOrder     = 20;
            CalamityFonts.Apply(fb);
            fb.ForceMeshUpdate();
            Logger.Info("AMONG US TMP fallback (vanilla sprite not found)", "CalamityLogo");
        }

        _logoTransform = logoGo.transform;
        _logoBasePos   = basePos;

        // Hide the original LOGO-AU to avoid a duplicate on screen.
        if (auLogo != null) auLogo.SetActive(false);

        // ── "End K not" subtitle + divider (startup only) ────────────────
        // Multi → lobby → Exit shouldn't show "End K not" again.
        if (!_subtitleShown)
        {
            _subtitleShown = true;

            var subGo = new GameObject("CalamitySubtitle");
            subGo.transform.SetParent(logoLayer);
            subGo.transform.localPosition = new Vector3(0f, 1.45f, 0f);

            var sub = subGo.AddComponent<TextMeshPro>();
            sub.text             = "End K not";
            sub.fontSize         = 1.8f;
            sub.alignment        = TextAlignmentOptions.Center;
            sub.fontStyle        = FontStyles.Bold;
            sub.characterSpacing = 6f;
            sub.color            = new Color(0.65f, 0.70f, 0.90f, 0.90f);
            sub.outlineColor     = new Color32(10, 5, 40, 200);
            sub.outlineWidth     = 0.18f;
            sub.sortingOrder     = 20;
            CalamityFonts.Apply(sub);

            var lineGo = new GameObject("CalamityDivider");
            lineGo.transform.SetParent(logoLayer);
            lineGo.transform.localPosition = new Vector3(0f, 1.25f, 0f);

            var line = lineGo.AddComponent<TextMeshPro>();
            line.text         = "──────────────────";
            line.fontSize     = 1.2f;
            line.alignment    = TextAlignmentOptions.Center;
            line.color        = new Color(0.40f, 0.45f, 0.65f, 0.50f);
            line.sortingOrder = 20;
            CalamityFonts.Apply(line);
        }
    }

    // Gentle logo vibration — two-frequency X and Y sines for an organic feel.
    public static void Tick(float dt)
    {
        if (_logoTransform == null) return;
        _shakeTime += dt;
        float ox = Mathf.Sin(_shakeTime * 11.0f) * 0.020f + Mathf.Sin(_shakeTime * 7.3f) * 0.008f;
        float oy = Mathf.Sin(_shakeTime *  8.0f + 0.7f) * 0.012f + Mathf.Sin(_shakeTime * 14.1f) * 0.005f;
        _logoTransform.localPosition = _logoBasePos + new Vector3(ox, oy, 0f);
    }

    private static GameObject FindAULogo()
    {
        // Active scene names first
        string[] active = { "LOGO-AU", "Logo-AU", "AULogo", "TitleLogo", "MainLogo" };
        foreach (var n in active)
        {
            var go = GameObject.Find(n);
            if (go != null) return go;
        }

        // Inactive-aware search through SpriteRenderers
        var all = Object.FindObjectsOfType<SpriteRenderer>(true);
        foreach (var sr in all)
        {
            if (sr == null) continue;
            var go = sr.gameObject;
            if (go == null) continue;

            string n = go.name;
            if (string.Equals(n, "LOGO-AU", System.StringComparison.OrdinalIgnoreCase) ||
                string.Equals(n, "Logo-AU", System.StringComparison.OrdinalIgnoreCase))
                return go;
        }

        return null;
    }
}
