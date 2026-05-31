using System;
using TMPro;
using UnityEngine;

namespace EndKnot;

// Temporary diagnostic helper for Phase 1 in-game measurement gates.
// Logs viewport geometry and spawns test objects to confirm:
//   (A) Scroll-clip behavior (raw SpriteRenderer vs masked control)
//   (B) Typography calibration (fontSize vs on-screen px for 16/27/42pt tiers)
//   (C) Viewport bottom-y measurement via SpriteMask bounds
// Remove or disable (Enabled = false) after Phase 1 gates are confirmed.
public static class RoleMenuDiag
{
    // Clip gate confirmed (2026-05-31): raw SR bleeds; categoryHeaderOrigin clone clips correctly.
    // All custom chrome in the new menu MUST use masked material (copy from categoryHeaderOrigin child).
    public static bool Enabled = false;

    // Focused pane diagnostic (2026-06-01): paints the panes opaque magenta + logs their material/
    // sorting/bounds (plus PanelSprite + MaskBg) to discriminate why the header band shows the lobby
    // (pane clipped by MaskBg material vs pane sorted behind the lobby). No full test-object clutter.
    public static bool PaneDiag = false; // (2026-06-01) cause found = Backrooms overlay tinting the world-space menu

    private static bool _loggedOnce;

    public static void Run(GameOptionsMenu menu)
    {
        if (!Enabled) return;
        try
        {
            RunImpl(menu);
        }
        catch (Exception ex)
        {
            Logger.Warn($"RoleMenuDiag.Run threw: {ex.Message}", "RoleMenuDiag");
        }
    }

    private static void RunImpl(GameOptionsMenu menu)
    {
        // (A) Logger dump — once per session
        if (!_loggedOnce)
        {
            _loggedOnce = true;
            try { DumpLog(menu); }
            catch (Exception ex) { Logger.Warn($"DumpLog threw: {ex.Message}", "RoleMenuDiag"); }
        }

        // (D) Synchronous black-left probe — walk THIS menu's root (the role-tab clone, where rows
        // render) for active sprites covering a known black point and report the frontmost opaque one.
        ProbeBlackLeft(menu);

        // (B)(C) Spawn test objects — only if not already present (survives Track/DestroyOption)
        if (menu.settingsContainer != null &&
            menu.settingsContainer.Find("RMDiag_RawSR") == null)
        {
            try { SpawnTestObjects(menu); }
            catch (Exception ex) { Logger.Warn($"SpawnTestObjects threw: {ex.Message}", "RoleMenuDiag"); }
        }
    }

    // ---- (A) Logger dump ----

    private static void DumpLog(GameOptionsMenu menu)
    {
        Logger.Info("=== ROLE MENU DIAG START ===", "RoleMenuDiag");

        // menu transform info
        Logger.Info($"menu.transform.parent.name = {menu.transform.parent?.name ?? "(null)"}", "RoleMenuDiag");
        Logger.Info($"menu.transform.position = {menu.transform.position}", "RoleMenuDiag");

        // settingsContainer
        Logger.Info($"menu.transform.lossyScale = {menu.transform.lossyScale}", "RoleMenuDiag");
        if (menu.settingsContainer != null)
        {
            Logger.Info($"settingsContainer.position(world) = {menu.settingsContainer.position}", "RoleMenuDiag");
            Logger.Info($"settingsContainer.localPosition = {menu.settingsContainer.localPosition}", "RoleMenuDiag");
            Logger.Info($"settingsContainer.lossyScale = {menu.settingsContainer.lossyScale}", "RoleMenuDiag");
        }
        else
        {
            Logger.Warn("settingsContainer is null", "RoleMenuDiag");
        }

        // scrollBar
        if (menu.scrollBar != null)
        {
            Logger.Info($"scrollBar.transform.position = {menu.scrollBar.transform.position}", "RoleMenuDiag");
            // ContentXBounds is the only confirmed bounds API; Y analog not present — log X as reference
            try { Logger.Info($"scrollBar.ContentXBounds = {menu.scrollBar.ContentXBounds}", "RoleMenuDiag"); }
            catch { Logger.Warn("scrollBar.ContentXBounds not accessible", "RoleMenuDiag"); }
        }
        else
        {
            Logger.Warn("scrollBar is null", "RoleMenuDiag");
        }

        // SpriteMask children — this is the key ViewportBottomY source
        // The repo has zero SpriteMask usages; an empty result here means clip is material-based (§1.6 outcome).
        SpriteMask[] masks = menu.GetComponentsInChildren<SpriteMask>(true);
        Logger.Info($"SpriteMask count under menu = {masks.Length}", "RoleMenuDiag");
        foreach (SpriteMask sm in masks)
        {
            try
            {
                Bounds b = sm.bounds;
                Logger.Info($"  SpriteMask '{sm.gameObject.name}' pos={sm.transform.position} bounds.min.y={b.min.y:F4} bounds.max.y={b.max.y:F4}", "RoleMenuDiag");
            }
            catch
            {
                Logger.Info($"  SpriteMask '{sm.gameObject.name}' pos={sm.transform.position} (bounds not accessible)", "RoleMenuDiag");
            }
        }

        Logger.Info("=== ROLE MENU DIAG END ===", "RoleMenuDiag");
    }

    // ---- (B)(C) Test object spawning ----

    private static void SpawnTestObjects(GameOptionsMenu menu)
    {
        if (menu.settingsContainer == null) return;

        // --- Viewport-boundary test: raw SpriteRenderer at FAR-LEFT (local x=-5 -> world ~-5.8) ---
        // Raw sprites use NO masked material, so they render even OUTSIDE the content viewport. If the
        // magenta shows at far-left but the masked CLIP-CTRL below it does NOT, the content viewport
        // excludes the far-left (the "black left" is outside-viewport panel, not an occluding object).
        var rawSrGo = new GameObject("RMDiag_RawSR");
        rawSrGo.transform.SetParent(menu.settingsContainer, false);
        rawSrGo.transform.localPosition = new Vector3(-5f, NewRoleMenuLayout.TopY - 0.3f, RoleMenuLayout.PosZ - 0.1f);
        rawSrGo.transform.localScale = new Vector3(0.5f, 0.5f, 1f);
        var rawSr = rawSrGo.AddComponent<SpriteRenderer>();
        rawSr.sprite = MakeSolidSprite();
        rawSr.color = new Color(1f, 0.2f, 0.8f, 0.9f); // bright magenta — easy to spot
        rawSr.sortingOrder = 50;
        ModGameOptionsMenu.Track(rawSrGo);
        Logger.Info($"RawSR localPos=(0,{NewRoleMenuLayout.TopY - 0.3f:F2}) -> world={rawSrGo.transform.position}", "RoleMenuDiag");

        // --- Clip control: masked CategoryHeaderMasked clone (B-ctrl) ---
        // Using categoryHeaderOrigin clone exactly as NewRoleMenuView does.
        if (menu.categoryHeaderOrigin != null)
        {
            CategoryHeaderMasked ctrl = ModGameOptionsMenu.Track(
                Object.Instantiate(menu.categoryHeaderOrigin, Vector3.zero, Quaternion.identity, menu.settingsContainer));
            ctrl.transform.localPosition = new Vector3(0f, NewRoleMenuLayout.TopY - 0.3f, RoleMenuLayout.PosZ - 0.05f);
            ctrl.transform.localScale = Vector3.one * 0.63f;
            var ctrlTmp = ctrl.transform.FindChild("HeaderText")?.GetComponent<TextMeshPro>();
            if (ctrlTmp != null)
            {
                ctrlTmp.text = "CLIP-CTRL";
                ctrlTmp.color = Color.cyan;
            }
            // FAR-LEFT, just below the raw magenta: if this masked clone is CLIPPED (invisible) while
            // the magenta shows, the content viewport does not reach the far-left.
            ctrl.transform.localPosition = new Vector3(-5f, NewRoleMenuLayout.TopY - 0.9f, RoleMenuLayout.PosZ - 0.05f);
        }

        // --- Typography reference: 3 TMP at Sz(16/27/42) anchored to menu.transform (C) ---
        // These are NOT under settingsContainer — they are fixed (non-scrolling) for px measurement.
        if (menu.categoryHeaderOrigin != null)
        {
            SpawnTypoRef(menu, RoleMenuType.Sz(16f), "16px AÁ0123", 0.0f);
            SpawnTypoRef(menu, RoleMenuType.Sz(27f), "27px AÁ0123", -0.5f);
            SpawnTypoRef(menu, RoleMenuType.Sz(42f), "42px AÁ0123", -1.2f);
        }

        // --- Canvas-width markers: thin vertical sprites at x=-6 and x=+6 (C) ---
        SpawnVMarker(menu, -6.0f, "RMDiag_MarkerL");
        SpawnVMarker(menu, +6.0f, "RMDiag_MarkerR");
    }

    private static void SpawnTypoRef(GameOptionsMenu menu, float fontSize, string label, float yOffset)
    {
        CategoryHeaderMasked clone = ModGameOptionsMenu.Track(
            Object.Instantiate(menu.categoryHeaderOrigin, Vector3.zero, Quaternion.identity, menu.transform));
        // parent to menu.transform (non-scrolling / fixed)
        clone.transform.localPosition = new Vector3(-5.5f, 0.5f + yOffset, RoleMenuLayout.PosZ - 0.1f);
        clone.transform.localScale = Vector3.one;
        var tmp = clone.transform.FindChild("HeaderText")?.GetComponent<TextMeshPro>();
        if (tmp != null)
        {
            tmp.text = label;
            tmp.fontSize = fontSize;
            tmp.color = Color.yellow;
        }
    }

    private static void SpawnVMarker(GameOptionsMenu menu, float x, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(menu.transform, false);
        go.transform.localPosition = new Vector3(x, 0f, RoleMenuLayout.PosZ - 0.1f);
        go.transform.localScale = new Vector3(0.02f, 8f, 1f); // thin vertical bar
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = MakeSolidSprite();
        sr.color = new Color(1f, 1f, 0f, 0.5f); // yellow semi-transparent
        sr.sortingOrder = 60;
        ModGameOptionsMenu.Track(go);
    }

    private static Sprite _cachedSolidSprite;

    private static Sprite MakeSolidSprite()
    {
        if (_cachedSolidSprite != null) return _cachedSolidSprite;
        var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
        tex.SetPixel(0, 0, Color.white);
        tex.Apply();
        tex.hideFlags = HideFlags.HideAndDontSave;
        _cachedSolidSprite = Sprite.Create(tex, new Rect(0f, 0f, 1f, 1f), new Vector2(0.5f, 0.5f), 1f);
        _cachedSolidSprite.hideFlags = HideFlags.HideAndDontSave;
        return _cachedSolidSprite;
    }

    // ---- Left-panel hierarchy dump ----
    // Identifies the dark sprite / native chrome that occludes the new master list, so we can hide
    // it precisely instead of guessing IL2CPP object names. Root = GameSettingsButton.parent.parent
    // (the left-panel area that also holds GameSettingsLabel).
    private static bool _leftPanelDumped;

    public static void DumpLeftPanel(GameSettingMenu menu)
    {
        if (!Enabled || _leftPanelDumped) return;
        _leftPanelDumped = true;
        try
        {
            Transform root = menu.GameSettingsButton != null ? menu.GameSettingsButton.transform.parent?.parent : null;
            if (root == null) { Logger.Warn("DumpLeftPanel: root null", "RoleMenuDiag"); return; }
            Logger.Info($"=== LEFT PANEL DUMP (root='{root.name}') ===", "RoleMenuDiag");
            DumpTransform(root, 0);
            Logger.Info("=== LEFT PANEL DUMP END ===", "RoleMenuDiag");
        }
        catch (Exception ex) { Logger.Warn($"DumpLeftPanel threw: {ex.Message}", "RoleMenuDiag"); }
    }

    private static void DumpTransform(Transform t, int depth)
    {
        if (depth > 4) return;
        string indent = new(' ', depth * 2);
        var sr = t.GetComponent<SpriteRenderer>();
        string srInfo = "";
        if (sr != null)
        {
            Bounds b = sr.bounds;
            srInfo = $" [SR sprite='{(sr.sprite != null ? sr.sprite.name : "null")}' color={sr.color} order={sr.sortingOrder} enabled={sr.enabled} wx=({b.min.x:F2}..{b.max.x:F2})]";
        }

        Vector3 wp = t.position;
        Logger.Info($"{indent}'{t.name}' active={t.gameObject.activeSelf} world=({wp.x:F2},{wp.y:F2}) local={t.localPosition}{srInfo}", "RoleMenuDiag");
        for (var i = 0; i < t.childCount; i++)
            DumpTransform(t.GetChild(i), depth + 1);
    }

    // ---- (D) Synchronous black-left probe ----
    // Walks the active role-tab menu's whole root for every active+enabled sprite whose world bounds
    // contain a known black-left point, logging path/sprite/color/sortingLayer/order/z. The frontmost
    // opaque hit IS the visible black there (no timing dependency, right object tree).
    private static bool _probed;

    private static void ProbeBlackLeft(GameOptionsMenu menu)
    {
        if (_probed) return;
        _probed = true;
        try
        {
            Vector2 p = new(-5f, 2f); // a point inside the "black left" region (world)
            Logger.Info($"=== BLACK-LEFT PROBE at world {p} (root='{menu.transform.root.name}') ===", "RoleMenuDiag");
            SpriteRenderer front = null;
            foreach (SpriteRenderer sr in menu.transform.root.GetComponentsInChildren<SpriteRenderer>(false)) // active-in-hierarchy only
            {
                if (sr == null || !sr.enabled || sr.sprite == null) continue;
                Color c = sr.color;
                if (c.a < 0.2f) continue;
                Bounds b = sr.bounds;
                if (p.x < b.min.x || p.x > b.max.x || p.y < b.min.y || p.y > b.max.y) continue; // point not covered
                float z = sr.transform.position.z;
                Logger.Info($"  HIT '{PathOf(sr.transform)}' sprite='{sr.sprite.name}' rgb=({c.r:F2},{c.g:F2},{c.b:F2}) a={c.a:F2} layer='{sr.sortingLayerName}' order={sr.sortingOrder} z={z:F2} wx=({b.min.x:F2}..{b.max.x:F2}) wy=({b.min.y:F2}..{b.max.y:F2})", "RoleMenuDiag");
                // frontmost: higher sortingOrder wins; tie-break smaller z (closer to camera in AU)
                if (front == null || sr.sortingOrder > front.sortingOrder ||
                    (sr.sortingOrder == front.sortingOrder && z < front.transform.position.z))
                    front = sr;
            }

            if (front != null)
                Logger.Info($"  >>> FRONTMOST = '{PathOf(front.transform)}' sprite='{front.sprite.name}' rgb=({front.color.r:F2},{front.color.g:F2},{front.color.b:F2}) order={front.sortingOrder} layer='{front.sortingLayerName}'", "RoleMenuDiag");
            else
                Logger.Info("  (no active sprite covers the point — left is genuinely empty/clipped)", "RoleMenuDiag");

            Logger.Info("=== BLACK-LEFT PROBE END ===", "RoleMenuDiag");
        }
        catch (Exception ex) { Logger.Warn($"ProbeBlackLeft threw: {ex.Message}", "RoleMenuDiag"); }
    }

    private static string PathOf(Transform t)
    {
        string p = t.name;
        for (Transform c = t.parent; c != null; c = c.parent) p = c.name + "/" + p;
        return p;
    }

    // ---- Pane-vs-lobby column probe (2026-06-01) ----
    // Walks the ENTIRE scene (not just menu.transform.root, so the lobby/ship SRs are included) and
    // logs every opaque SpriteRenderer covering a vertical sweep of world points. This definitively
    // shows, at each height, whether a pane or the lobby is in front — breaking the "magenta covers
    // but E1 doesn't" paradox by exposing the real render stack (layer + order + color).
    public static void ProbeColumn(GameOptionsMenu menu)
    {
        if (!PaneDiag) return;
        try
        {
            float[] ys = { 3.4f, 2.8f, 2.2f, 1.6f, 1.0f, 0.2f };
            const float x = -2.0f;
            SpriteRenderer[] all = Object.FindObjectsOfType<SpriteRenderer>();
            Logger.Info($"=== PANE COLUMN PROBE x={x} (scene SR count={all.Length}) ===", "ProbeCol");
            foreach (float y in ys)
            {
                Vector2 p = new(x, y);
                int hits = 0;
                foreach (SpriteRenderer sr in all)
                {
                    if (sr == null || !sr.enabled || sr.sprite == null) continue;
                    if (!sr.gameObject.activeInHierarchy) continue;
                    Color c = sr.color;
                    if (c.a < 0.2f) continue;
                    Bounds b = sr.bounds;
                    if (p.x < b.min.x || p.x > b.max.x || p.y < b.min.y || p.y > b.max.y) continue;
                    if (hits++ >= 8) break;
                    Logger.Info($"[ProbeCol] y={y:F1} '{sr.gameObject.name}' layer='{sr.sortingLayerName}'({sr.sortingLayerID}) order={sr.sortingOrder} rgb=({c.r:F2},{c.g:F2},{c.b:F2}) a={c.a:F2} z={sr.transform.position.z:F2} mat='{(sr.sharedMaterial != null ? sr.sharedMaterial.name : "null")}'", "ProbeCol");
                }
                if (hits == 0) Logger.Info($"[ProbeCol] y={y:F1} (no opaque SR covers it)", "ProbeCol");
            }
            Logger.Info("=== PANE COLUMN PROBE END ===", "ProbeCol");
        }
        catch (Exception ex) { Logger.Warn($"ProbeColumn threw: {ex.Message}", "ProbeCol"); }
    }
}
