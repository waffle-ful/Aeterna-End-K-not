using TMPro;
using UnityEngine;

namespace EndKnot.Patches.CalamityMenu;

public static class CalamityLogo
{
    public static void Build(Transform logoLayer)
    {
        Logger.Info($"Build start, logoLayer={(logoLayer != null ? logoLayer.name : "NULL")}, fontVanilla={(CalamityFonts.Vanilla != null ? "set" : "null")}", "CalamityLogo");

        // ── Among Us text logo (always shown) ────────────────────────────
        // On the second menu scene load (after a freeplay session), reparenting vanilla
        // LOGO-AU sometimes leaves it invisible (renderer state lost between scenes).
        // Always render a TMP "AMONG  US" text at the same slot — reliable and consistent.
        var fallbackGo = new GameObject("AmongUsTextLogo");
        fallbackGo.transform.SetParent(logoLayer);
        fallbackGo.transform.localPosition = new Vector3(0f, 1.95f, 0f);

        var fb = fallbackGo.AddComponent<TextMeshPro>();
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
        Logger.Info($"AMONG US TMP created at world {fallbackGo.transform.position}, activeInHierarchy={fallbackGo.activeInHierarchy}, font={(fb.font != null ? fb.font.name : "null")}", "CalamityLogo");

        // Keep the vanilla LOGO-AU spawn lookup so we can hide it if it shows up alongside
        // (otherwise it would render on top of our text from its default scene position).
        var auLogo = FindAULogo();
        if (auLogo != null) auLogo.SetActive(false);

        // ── "End K not" subtitle ─────────────────────────────────────────
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

        // ── Thin divider ─────────────────────────────────────────────────
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
