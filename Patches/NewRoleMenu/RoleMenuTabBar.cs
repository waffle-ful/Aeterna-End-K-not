using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

namespace EndKnot;

// Overlay chrome for the top tab bar in the new two-pane role menu.
// All GameObjects are parented to menu.transform (sibling of the pane backgrounds),
// NOT to the PassiveButton — the button has a non-uniform scale (0.33, 0.3, 1) that
// would distort dots and badges if they were children.
// All objects are registered via ModGameOptionsMenu.Track so DestroyOption() cleans them up.
public static class RoleMenuTabBar
{
    // ---- cached rounded sprites ----
    private static Sprite _dotSprite;   // 8×8 fully-rounded dot
    private static Sprite _pillSprite;  // badge pill (high radius)
    private static Sprite _solidSprite; // 1×1 flat (reuses NewRoleMenuView._solid via Solid())

    // Solid 1×1 sprite (shared with NewRoleMenuView via own cached field).
    private static Sprite SolidSprite()
    {
        if (_solidSprite != null) return _solidSprite;
        var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
        tex.SetPixel(0, 0, Color.white);
        tex.Apply();
        tex.hideFlags = HideFlags.HideAndDontSave;
        _solidSprite = Sprite.Create(tex, new Rect(0f, 0f, 1f, 1f), new Vector2(0.5f, 0.5f), 1f);
        _solidSprite.hideFlags = HideFlags.HideAndDontSave;
        return _solidSprite;
    }

    // Rounded-corner sprite. radius=size/2 gives a circle/pill.
    private static Sprite MakeRoundedSprite(int w, int h, int radius)
    {
        w = Mathf.Max(1, w);
        h = Mathf.Max(1, h);
        radius = Mathf.Clamp(radius, 0, Mathf.Min(w, h) / 2);
        var tex = new Texture2D(w, h, TextureFormat.ARGB32, false) { filterMode = FilterMode.Bilinear };
        for (int py = 0; py < h; py++)
        {
            for (int px = 0; px < w; px++)
            {
                float a = CornerAlpha(px, py, w, h, radius);
                tex.SetPixel(px, py, new Color(1f, 1f, 1f, a));
            }
        }
        tex.Apply();
        tex.hideFlags = HideFlags.HideAndDontSave;
        var spr = Sprite.Create(tex, new Rect(0f, 0f, w, h), new Vector2(0.5f, 0.5f), Mathf.Max(w, h));
        spr.hideFlags = HideFlags.HideAndDontSave;
        return spr;
    }

    // Ported from ClientControlGUI.CornerAlpha (private there).
    private static float CornerAlpha(int px, int py, int w, int h, int r)
    {
        int cx, cy;
        if      (px < r    && py < r    ) { cx = r;     cy = r;     }
        else if (px >= w-r && py < r    ) { cx = w - r; cy = r;     }
        else if (px < r    && py >= h-r ) { cx = r;     cy = h - r; }
        else if (px >= w-r && py >= h-r ) { cx = w - r; cy = h - r; }
        else return 1f;

        float d = Mathf.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
        if (d >= r + 1f) return 0f;
        if (d <= r - 1f) return 1f;
        return r + 0.5f - d;
    }

    private static Sprite DotSprite()
    {
        if (_dotSprite == null) _dotSprite = MakeRoundedSprite(8, 8, 4);
        return _dotSprite;
    }

    private static Sprite PillSprite()
    {
        if (_pillSprite == null) _pillSprite = MakeRoundedSprite(16, 10, 5);
        return _pillSprite;
    }

    // Obtains an unmasked TMP_Text by cloning the tab button's own label TMP.
    // This gives us the vanilla font without the DepthMasked material that
    // categoryHeaderOrigin uses (DepthMasked clips above the MaskBg band).
    private static TMP_Text MakeUnmaskedTmp(PassiveButton sourceButton, Transform parent, string name)
    {
        TMP_Text src = sourceButton.GetComponentInChildren<TMP_Text>();
        if (src == null) return null;

        // Use SetActive(false) → config → ForceMeshUpdate → SetActive(true) to avoid stale-mesh issue.
        var go = Object.Instantiate(src.gameObject, parent);
        go.SetActive(false);
        go.name = name;
        // Remove any translator component that might override our text.
        var tmp = go.GetComponent<TMP_Text>();
        if (tmp != null) tmp.DestroyTranslator();
        return tmp;
    }

    // World-space TMP (TextMeshPro) renders via a MeshRenderer; match the tab-bar sorting so the
    // text sits above the pill / panes (same reason as the sprite chrome — Default layer falls behind).
    private static void SetTmpSorting(TMP_Text tmp, int layer, int order)
    {
        var mr = tmp.GetComponent<MeshRenderer>();
        if (mr != null) { mr.sortingLayerID = layer; mr.sortingOrder = order; }
    }

    // ---- main entry point ----
    // Called once per tab button after its localPosition has been set (in StartPostfix, Active branch).
    // menu      : the GameSettingMenu (chrome parent — does not scroll)
    // tab       : which TabGroup this button belongs to
    // button    : the PassiveButton for this tab
    // orderedTabs: full ordered list so we can compute divider x
    // idx       : this button's index in orderedTabs
    public static void Decorate(
        GameSettingMenu menu,
        TabGroup tab,
        PassiveButton button,
        List<TabGroup> orderedTabs,
        int idx)
    {
        try
        {
            bool isRoleTab = tab is >= TabGroup.ImpostorRoles and <= TabGroup.OtherRoles;

            // World position of the button center (button.transform.position).
            // We drive chrome from world coords so non-uniform parent scale doesn't matter.
            Vector3 btnWorld = button.transform.position;
            float tabBarY = btnWorld.y; // Y of the tab strip in world space

            // Chrome must render at the tab strip's sorting layer — raw SpriteRenderers on the
            // default "Default" layer fall BEHIND the panel/panes (MakePane copies panelSr sorting
            // for exactly this reason; known pitfall). Reference the button's own (now alpha-0)
            // sprite, which is already placed correctly at the tab strip.
            var btnRefSr = button.inactiveSprites != null ? button.inactiveSprites.GetComponent<SpriteRenderer>() : null;
            int chromeLayer = btnRefSr != null ? btnRefSr.sortingLayerID : 0;
            int chromeOrder = btnRefSr != null ? btnRefSr.sortingOrder : 0;

            // ---- faction dot (role tabs only) ----
            if (isRoleTab)
            {
                Color32 dotColor = RoleMenuTokens.FactionColor(tab);
                var dotGo = new GameObject($"RM_Dot_{tab}");
                dotGo.transform.SetParent(menu.transform, false);
                // Position: above button center
                dotGo.transform.position = new Vector3(btnWorld.x, tabBarY + NewRoleMenuLayout.DotOffsetY, NewRoleMenuLayout.PosZ - 0.1f);
                float sz = NewRoleMenuLayout.DotSize;
                dotGo.transform.localScale = Vector3.one * sz;
                var sr = dotGo.AddComponent<SpriteRenderer>();
                sr.sprite = DotSprite();
                sr.color = dotColor;
                sr.sortingLayerID = chromeLayer;
                sr.sortingOrder = chromeOrder + 2;
                ModGameOptionsMenu.Track(dotGo);
            }

            // ---- badge (role tabs only) ----
            if (isRoleTab)
            {
                int count = 0;
                if (Options.GroupedOptions.TryGetValue(tab, out var items))
                    count = items.Count(NewRoleMenuView.IsSelectableMaster);

                if (count > 0)
                {
                    string countStr = count.ToString();

                    var badgeGo = new GameObject($"RM_Badge_{tab}");
                    badgeGo.transform.SetParent(menu.transform, false);
                    float bh = NewRoleMenuLayout.BadgeH;
                    // Simple width estimate: each char ~0.13 world + padding
                    float bw = countStr.Length * 0.13f + NewRoleMenuLayout.BadgePadX * 2f;
                    badgeGo.transform.position = new Vector3(
                        btnWorld.x + 0.35f, // to the right of button center
                        tabBarY + 0.20f,
                        NewRoleMenuLayout.PosZ - 0.1f);
                    badgeGo.transform.localScale = new Vector3(bw, bh, 1f);
                    var badgeSr = badgeGo.AddComponent<SpriteRenderer>();
                    badgeSr.sprite = PillSprite();
                    badgeSr.color = RoleMenuTokens.E2;
                    badgeSr.sortingLayerID = chromeLayer;
                    badgeSr.sortingOrder = chromeOrder + 1;
                    ModGameOptionsMenu.Track(badgeGo);

                    // Badge number TMP — unmasked clone of button label
                    TMP_Text badgeTmp = MakeUnmaskedTmp(button, menu.transform, $"RM_BadgeNum_{tab}");
                    if (badgeTmp != null)
                    {
                        badgeTmp.text = countStr;
                        badgeTmp.color = (Color)RoleMenuTokens.TMid;
                        badgeTmp.alignment = TextAlignmentOptions.Center;
                        badgeTmp.ForceMeshUpdate();
                        badgeTmp.gameObject.SetActive(true);
                        badgeTmp.transform.SetParent(menu.transform, false);
                        badgeTmp.transform.position = badgeGo.transform.position;
                        badgeTmp.transform.localScale = Vector3.one * 0.40f;
                        SetTmpSorting(badgeTmp, chromeLayer, chromeOrder + 3);
                        ModGameOptionsMenu.Track(badgeTmp.gameObject);
                    }
                }
            }

            // ---- active underline (every tab) ----
            {
                var ulGo = new GameObject($"RM_Underline_{tab}");
                ulGo.transform.SetParent(menu.transform, false);
                // Width: match button width in world space = localScale.x * TopTabStepX * button parent scale
                // Simpler: use a fixed world width matching TopTabStepX * button_parent_lossyScale.x
                float parentScaleX = button.transform.parent != null
                    ? button.transform.parent.lossyScale.x
                    : 1f;
                float ulW = NewRoleMenuLayout.TopTabStepX * parentScaleX * 0.9f;
                float ulH = NewRoleMenuLayout.UnderlineH;
                ulGo.transform.position = new Vector3(
                    btnWorld.x,
                    tabBarY + NewRoleMenuLayout.UnderlineOffY,
                    NewRoleMenuLayout.PosZ - 0.05f);
                ulGo.transform.localScale = new Vector3(ulW, ulH, 1f);
                var ulSr = ulGo.AddComponent<SpriteRenderer>();
                ulSr.sprite = SolidSprite();
                ulSr.color = isRoleTab
                    ? (Color)RoleMenuTokens.FactionColor(tab)
                    : (Color)RoleMenuTokens.TMid;
                ulSr.sortingLayerID = chromeLayer;
                ulSr.sortingOrder = chromeOrder + 2;
                ulSr.enabled = false; // disabled until SetActiveTab / StartPostfix enable it
                NewRoleMenuState.TabUnderlines[tab] = ulSr;
                ModGameOptionsMenu.Track(ulGo);
            }

            // ---- vertical divider between role tabs and system tab (idx == 6) ----
            if (idx == 6 && orderedTabs.Count > idx)
            {
                // x = midpoint between previous tab (idx 5) and this tab (idx 6)
                float prevX = idx > 0
                    ? (-(orderedTabs.Count - 1) * NewRoleMenuLayout.TopTabStepX / 2f) + ((idx - 1) * NewRoleMenuLayout.TopTabStepX)
                    : btnWorld.x;
                // Compute in parent space then convert to world
                Transform btnParent = button.transform.parent;
                float parentLsx = btnParent != null ? btnParent.lossyScale.x : 1f;
                float parentLsy = btnParent != null ? btnParent.lossyScale.y : 1f;
                float parentPx  = btnParent != null ? btnParent.position.x : 0f;
                float prevWorldX = parentPx + prevX * parentLsx;
                float divX = (prevWorldX + btnWorld.x) / 2f;

                var divGo = new GameObject("RM_Divider_Settings");
                divGo.transform.SetParent(menu.transform, false);
                divGo.transform.position = new Vector3(divX, tabBarY, NewRoleMenuLayout.PosZ - 0.1f);
                divGo.transform.localScale = new Vector3(NewRoleMenuLayout.DividerW, NewRoleMenuLayout.DividerH, 1f);
                var divSr = divGo.AddComponent<SpriteRenderer>();
                divSr.sprite = SolidSprite();
                divSr.color = RoleMenuTokens.Line;
                divSr.sortingLayerID = chromeLayer;
                divSr.sortingOrder = chromeOrder + 2;
                ModGameOptionsMenu.Track(divGo);

                // "設定" label below the divider
                TMP_Text divTmp = MakeUnmaskedTmp(button, menu.transform, "RM_DividerLabel");
                if (divTmp != null)
                {
                    divTmp.text = Translator.GetString("TabGroup.Short.Settings");
                    divTmp.color = (Color)RoleMenuTokens.TDis;
                    divTmp.alignment = TextAlignmentOptions.Center;
                    divTmp.ForceMeshUpdate();
                    divTmp.gameObject.SetActive(true);
                    divTmp.transform.SetParent(menu.transform, false);
                    divTmp.transform.position = new Vector3(divX, tabBarY - 0.20f, NewRoleMenuLayout.PosZ - 0.1f);
                    divTmp.transform.localScale = Vector3.one * 0.28f;
                    SetTmpSorting(divTmp, chromeLayer, chromeOrder + 3);
                    ModGameOptionsMenu.Track(divTmp.gameObject);
                }
            }
        }
        catch (System.Exception e) { Utils.ThrowException(e); }
    }

    // Enable only the underline for 'current', disable all others.
    public static void SetActiveTab(TabGroup current)
    {
        foreach (var kv in NewRoleMenuState.TabUnderlines)
        {
            if (!kv.Value) continue;
            kv.Value.enabled = kv.Key == current;
        }
    }

    // Clear the underlines dict. Mirrors ModSettingsButtons.Clear() lifecycle.
    public static void Reset()
    {
        NewRoleMenuState.TabUnderlines.Clear();
    }
}
