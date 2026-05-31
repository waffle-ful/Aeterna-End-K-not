using System;
using System.Collections;
using System.Linq;
using System.Text.RegularExpressions;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.Events;

namespace EndKnot;

// Redesigned two-pane role/options view. Reached only via the Active-gated dispatch hooks in
// GameOptionsMenuPatch (role tabs only). Model (OptionItem) is never mutated here: rows are the
// same vanilla OptionBehaviour prefab clones registered in ModGameOptionsMenu.OptionList, so the
// existing ToggleOption/NumberOption/StringOption value patches keep firing and SetValue drives
// sync + save unchanged.
//
// Phase 3a: LEFT pane = category headers + top-level spawn-chance rows (Parent == null).
//           RIGHT pane = selected role's children (max count + sub-options).
//           Click selection wiring arrives in Phase 3b; default selection = first selectable master.
public static class NewRoleMenuView
{
    // Walk up to root option (Parent == null ancestor).
    private static OptionItem TopMaster(OptionItem o)
    {
        while (o.Parent != null) o = o.Parent;
        return o;
    }

    // Top-level non-header option = a role's spawn-chance row (selectable master).
    internal static bool IsSelectableMaster(OptionItem o) => o.Parent == null && o is not TextOptionItem;

    // Returns the currently selected master for modTab, or null if SelectedIndex is invalid / wrong tab.
    private static OptionItem SelectedMaster(TabGroup modTab)
    {
        int idx = NewRoleMenuState.SelectedIndex;
        if (idx < 0 || idx >= OptionItem.AllOptions.Count) return null;
        OptionItem o = OptionItem.AllOptions[idx];
        return o.Tab == modTab && IsSelectableMaster(o) ? o : null;
    }

    // If no valid selection exists for modTab, pick the first selectable master in that tab.
    private static void EnsureSelection(TabGroup modTab)
    {
        if (SelectedMaster(modTab) != null) return;

        for (var i = 0; i < OptionItem.AllOptions.Count; i++)
        {
            OptionItem o = OptionItem.AllOptions[i];
            if (o.Tab == modTab && IsSelectableMaster(o))
            {
                NewRoleMenuState.SelectedIndex = i;
                return;
            }
        }

        NewRoleMenuState.SelectedIndex = -1;
    }

    // Hide the native "Darkener" overlays (children of GameSettingsLabel, z=-20, black α0.588) that AU
    // activates over the left/right of the options view. They live in whichever PlayerOptionsMenu the
    // active role-tab menu belongs to, so search from this menu's root (the StartPostfix hide targets a
    // different instance and misses them). Pure View-side; only the cosmetic dim is removed.
    private static void HideNativeDarkeners(GameOptionsMenu menu)
    {
        try
        {
            foreach (SpriteRenderer sr in menu.transform.root.GetComponentsInChildren<SpriteRenderer>(true))
            {
                string n = sr.gameObject.name;
                if (n is "LeftDarkener" or "RightDarkener")
                    sr.gameObject.SetActive(false);
            }
        }
        catch (Exception e) { Utils.ThrowException(e); }
    }

    // ---- Pane backgrounds (fixed chrome, mock two-pane) ----
    // Parented to menu.transform (NOT settingsContainer) so they do NOT scroll and need no mask.
    // This turns the dark "empty left" into an intentional left list-pane + right detail-pane.
    private static Sprite _solid;

    private static Sprite Solid()
    {
        if (_solid != null) return _solid;
        var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
        tex.SetPixel(0, 0, Color.white);
        tex.Apply();
        tex.hideFlags = HideFlags.HideAndDontSave;
        _solid = Sprite.Create(tex, new Rect(0f, 0f, 1f, 1f), new Vector2(0.5f, 0.5f), 1f);
        _solid.hideFlags = HideFlags.HideAndDontSave;
        return _solid;
    }

    private static void BuildPaneBackgrounds(GameOptionsMenu menu)
    {
        try
        {
            if (menu.transform.Find("RM_LeftPane") != null) return;

            // Hide PanelSprite (dark panel background art — sole opaque blocker, confirmed via ProbeBlackLeft).
            // Target THIS menu's PlayerOptionsMenu ancestor, not the offscreen template clone.
            Transform playerOptRoot = menu.transform.parent;
            while (playerOptRoot != null && !playerOptRoot.name.StartsWith("PlayerOptionsMenu"))
                playerOptRoot = playerOptRoot.parent;
            Transform panelSpriteT = playerOptRoot != null ? playerOptRoot.Find("PanelSprite") : null;
            if (panelSpriteT != null) panelSpriteT.gameObject.SetActive(false);

            // Panes go BEHIND content rows. Spike B confirmed content rows are at sortingOrder 0–1;
            // panes at panelSr.order - 2 = -2 are safely behind them yet in front of game-world.
            // settingsContainer stays at its original localPosition (0,0,-4) — B confirmed no move needed.
            SpriteRenderer panelSr = panelSpriteT != null ? panelSpriteT.GetComponent<SpriteRenderer>() : null;
            int layer = panelSr != null ? panelSr.sortingLayerID : 0;
            int order = (panelSr != null ? panelSr.sortingOrder : 0) - 2;
            float wz  = RoleMenuLayout.PosZ;

            if (RoleMenuDiag.PaneDiag && panelSr != null)
                Logger.Info($"[PaneDiag] PanelSprite mat='{(panelSr.sharedMaterial != null ? panelSr.sharedMaterial.name : "null")}' layer='{panelSr.sortingLayerName}'({panelSr.sortingLayerID}) order={panelSr.sortingOrder}", "PaneDiag");

            // Use the EXACT world bounds of PanelSprite so panes align pixel-perfectly.
            Bounds pb = panelSr != null ? panelSr.bounds : new Bounds(new Vector3(-1.6f, 0.2f), new Vector3(9.12f, 4.25f));
            float wxL = pb.min.x;
            float wxR = pb.max.x;
            const float ExpandedWyT = 5.0f;   // extend top above PanelSprite (was ≈4.14)
            const float ExpandedWyB = -0.5f;  // extend bottom below PanelSprite (was ≈-0.10)
            float split = wxL + (wxR - wxL) * 0.30f; // 30/70 split
            Logger.Info($"[PaneB] PanelSprite bounds x={wxL:F2}..{wxR:F2} y={pb.min.y:F2}..{pb.max.y:F2} → wyT={ExpandedWyT:F2} wyB={ExpandedWyB:F2} split={split:F2}", "PaneB");

            // Store selection-bar geometry for Build/Reflow (bar sits at the far-left edge of
            // the left pane, in the empty space outside the vanilla StringOption frame).
            {
                Vector3 scPos = menu.settingsContainer.position;
                float scSx = menu.settingsContainer.lossyScale.x;
                float barWorldX = wxL + 0.05f; // 5cm inside the left panel edge
                NewRoleMenuState.BarLocalX = (barWorldX - scPos.x) / scSx;
                NewRoleMenuState.BarSortLayerID = layer;
                NewRoleMenuState.BarSortOrder = order + 1; // one step above the pane backgrounds

                if (RoleMenuDiag.PaneDiag)
                {
                    // Content-start calibration: where does the first row land vs the clip top / tab bar?
                    // firstRowWorldY ≈ settingsContainer.worldY + TopY*lossyScale.y. To fill the header
                    // band, raise TopY until firstRowWorldY ≈ ClipWyT (just under the tab bar ~2.55).
                    float scSy = menu.settingsContainer.lossyScale.y;
                    float firstRowWorldY = scPos.y + NewRoleMenuLayout.TopY * scSy;
                    Logger.Info($"[PaneDiag] settingsContainer world=({scPos.x:F2},{scPos.y:F2}) lossyScale.y={scSy:F2} TopY={NewRoleMenuLayout.TopY:F2} -> firstRowWorldY~{firstRowWorldY:F2} | ClipWyT={NewRoleMenuLayout.ClipWyT:F2} tabBarY~2.55", "PaneDiag");
                }
            }

            MakePane(menu, "RM_LeftPane",  wxL,           split,          ExpandedWyT, ExpandedWyB, RoleMenuTokens.E1,   layer, order, wz);
            MakePane(menu, "RM_RightPane", split,         wxR,            ExpandedWyT, ExpandedWyB, RoleMenuTokens.Bg,   layer, order, wz);
            MakePane(menu, "RM_Divider",   split - 0.03f, split + 0.03f, ExpandedWyT, ExpandedWyB, RoleMenuTokens.Line, layer, order + 1, wz - 0.01f);

            // Scale menu.MaskBg (the DepthMaskedTexture SR defining TMP clip viewport) to cover
            // the scroll-content area below the tab bar. Top edge uses ClipWyT (not ExpandedWyT) so
            // the mask stops at the tab bar, preventing scrolled content from bleeding into the tab strip.
            // Panes remain at ExpandedWyT=5.0 (forming the header band above the clip region).
            const float clipWyT = NewRoleMenuLayout.ClipWyT;
            var maskBg = menu.MaskBg;
            if (maskBg != null)
            {
                Bounds mb = maskBg.bounds;
                Logger.Info($"[MaskBg] pre x={mb.min.x:F2}..{mb.max.x:F2} y={mb.min.y:F2}..{mb.max.y:F2} ls={maskBg.transform.localScale}", "MaskBg");
                float curW = mb.size.x, curH = mb.size.y;
                float tgtW = wxR - wxL, tgtH = clipWyT - ExpandedWyB;
                if (curW > 0.01f && curH > 0.01f)
                {
                    Vector3 ls2 = maskBg.transform.localScale;
                    maskBg.transform.localScale = new Vector3(ls2.x * tgtW / curW, ls2.y * tgtH / curH, ls2.z);
                    Vector3 pos = maskBg.transform.position;
                    maskBg.transform.position = new Vector3((wxL + wxR) / 2f, (clipWyT + ExpandedWyB) / 2f, pos.z);
                    Logger.Info($"[MaskBg] → {tgtW:F2}×{tgtH:F2} center=({(wxL+wxR)/2f:F2},{(clipWyT+ExpandedWyB)/2f:F2})", "MaskBg");
                    if (RoleMenuDiag.PaneDiag)
                        Logger.Info($"[PaneDiag] MaskBg mat='{(maskBg.sharedMaterial != null ? maskBg.sharedMaterial.name : "null")}' layer='{maskBg.sortingLayerName}'({maskBg.sortingLayerID}) order={maskBg.sortingOrder}", "PaneDiag");
                }
            }
            else
            {
                Logger.Warn("[MaskBg] menu.MaskBg is null — TMP clip not expanded", "MaskBg");
            }

            RoleMenuDiag.ProbeColumn(menu); // DIAG: full-scene render-stack sweep (gated by PaneDiag)
        }
        catch (Exception e) { Utils.ThrowException(e); }
    }

    private static void MakePane(GameOptionsMenu menu, string name, float wxL, float wxR, float wyT, float wyB, Color32 color, int layer, int order, float wz)
    {
        var go = new GameObject(name);
        go.transform.SetParent(menu.transform, false);
        go.transform.position = new Vector3((wxL + wxR) / 2f, (wyT + wyB) / 2f, wz);
        Vector3 ls = menu.transform.lossyScale;
        go.transform.localScale = new Vector3(Mathf.Abs(wxR - wxL) / ls.x, Mathf.Abs(wyT - wyB) / ls.y, 1f);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = Solid();
        sr.color = color;
        sr.sortingLayerID = layer;
        sr.sortingOrder = order;

        if (RoleMenuDiag.PaneDiag)
        {
            // Pane coverage confirmed (2026-06-01): panes are opaque Sprites-Default at wy(-0.5..5.0),
            // in front of the lobby — the "invisible upper half" was the empty header band, not bleed.
            // Magenta override removed; keep the dump (now incl. content-start) to calibrate the band.
            Bounds pbnd = sr.bounds;
            Logger.Info($"[PaneDiag] '{name}' mat='{(sr.sharedMaterial != null ? sr.sharedMaterial.name : "null")}' order={sr.sortingOrder} wy=({pbnd.min.y:F2}..{pbnd.max.y:F2}) wx=({pbnd.min.x:F2}..{pbnd.max.x:F2})", "PaneDiag");
        }

        ModGameOptionsMenu.Track(go);
    }

    private static string GetPath(Transform t, Transform root)
    {
        var parts = new System.Collections.Generic.List<string>();
        while (t != null && t != root) { parts.Insert(0, t.name); t = t.parent; }
        return string.Join("/", parts);
    }

    public static void Build(GameOptionsMenu menu, TabGroup modTab)
    {
        if (RoleMenuDiag.Enabled) RoleMenuDiag.Run(menu);
        HideNativeDarkeners(menu);
        BuildPaneBackgrounds(menu);
        NewRoleMenuState.SelBars.Clear();
        EnsureSelection(modTab);
        menu.scrollBar.SetYBoundsMax(CalculateScrollBarYBoundsMax(modTab));
        menu.StartCoroutine(CoRoutine().WrapToIl2Cpp());
        return;

        IEnumerator CoRoutine()
        {
            float numL = NewRoleMenuLayout.TopY; // left pane y cursor (master rows)
            float numR = NewRoleMenuLayout.TopY; // right pane y cursor (detail rows)
            const float posZ = NewRoleMenuLayout.PosZ;

            TextOptionItem header = null;
            OptionItem selected = SelectedMaster(modTab);

            for (var index = 0; index < OptionItem.AllOptions.Count; index++)
            {
                try
                {
                    OptionItem option = OptionItem.AllOptions[index];
                    if (option.Tab != modTab) continue;

                    bool enabledOrNotCollapsed = !option.IsCurrentlyHidden() && GameOptionsMenuPatch.AllParentsEnabledAndVisible(option.Parent);
                    bool enabled = !option.IsCurrentlyHidden(checkCollapsedSection: false) && GameOptionsMenuPatch.AllParentsEnabledAndVisible(option.Parent, checkCollapsedSection: false);

                    bool isLeft = option is TextOptionItem || option.Parent == null;

                    if (option is TextOptionItem toi)
                    {
                        CategoryHeaderMasked categoryHeaderMasked = ModGameOptionsMenu.Track(Object.Instantiate(menu.categoryHeaderOrigin, Vector3.zero, Quaternion.identity, menu.settingsContainer));
                        categoryHeaderMasked.SetHeader(StringNames.RolesCategory, 20);
                        categoryHeaderMasked.Title.SetText(option.GetName(disableColor: true).Trim('★', ' '));
                        categoryHeaderMasked.Background.color = categoryHeaderMasked.Divider.color = option.NameColor;
                        categoryHeaderMasked.transform.localScale = Vector3.one * 0.63f;
                        categoryHeaderMasked.transform.localPosition = new(NewRoleMenuLayout.MasterHeaderX, numL, posZ);
                        var chmText = categoryHeaderMasked.transform.FindChild("HeaderText").GetComponent<TextMeshPro>();
                        chmText.fontStyle = FontStyles.Bold | FontStyles.SmallCaps;
                        chmText.fontWeight = FontWeight.Black;
                        chmText.outlineWidth = 0.17f;
                        var chmCollider = categoryHeaderMasked.gameObject.AddComponent<BoxCollider2D>();
                        chmCollider.size = new Vector2(7, 0.7f);
                        chmCollider.offset = new Vector2(1.5f, -0.3f);
                        var chmButton = categoryHeaderMasked.gameObject.AddComponent<PassiveButton>();
                        chmButton.ClickSound = menu.BackButton.GetComponent<PassiveButton>().ClickSound;
                        chmButton.OnMouseOver = new();
                        chmButton.OnMouseOut = new();
                        chmButton.OnClick.AddListener((UnityAction)(() =>
                        {
                            toi.CollapsesSection = !toi.CollapsesSection;
                            GameOptionsMenuPatch.ReCreateSettings(menu);
                        }));
                        chmButton.SetButtonEnableState(true);
                        categoryHeaderMasked.gameObject.SetActive(enabled);
                        ModGameOptionsMenu.CategoryHeaderList[index] = categoryHeaderMasked;

                        // One-shot TMP material diagnostic (first category header only).
                        if (header == null)
                        {
                            var titleTmp = categoryHeaderMasked.Title;
                            Logger.Info($"[TmpD] Title mat='{titleTmp?.fontSharedMaterial?.name}' color={titleTmp?.color} enabled={titleTmp?.enabled}", "TmpD");
                            var bgSr = categoryHeaderMasked.Background;
                            Logger.Info($"[TmpD] Background SR mat='{bgSr?.material?.name}' maskInt={bgSr?.maskInteraction}", "TmpD");
                        }

                        if (enabledOrNotCollapsed) numL -= NewRoleMenuLayout.HeaderStep;
                        header = toi;
                        continue;
                    }

                    option.Header = header;

                    if (isLeft)
                    {
                        if (option.IsHeader && enabledOrNotCollapsed)
                            numL -= NewRoleMenuLayout.SubHeaderGap;
                    }
                    else
                    {
                        bool showDetail = selected != null && TopMaster(option) == selected;
                        if (option.IsHeader && showDetail)
                            numR -= NewRoleMenuLayout.SubHeaderGap;
                    }

                    BaseGameSetting baseGameSetting = GameOptionsMenuPatch.GetSetting(option);
                    if (!baseGameSetting) continue;

                    OptionBehaviour optionBehaviour;

                    try
                    {
                        optionBehaviour = baseGameSetting.Type switch
                        {
                            OptionTypes.Checkbox => ModGameOptionsMenu.Track(Object.Instantiate(menu.checkboxOrigin, Vector3.zero, Quaternion.identity, menu.settingsContainer)),
                            OptionTypes.String => ModGameOptionsMenu.Track(Object.Instantiate(menu.stringOptionOrigin, Vector3.zero, Quaternion.identity, menu.settingsContainer)),
                            OptionTypes.Float or OptionTypes.Int => ModGameOptionsMenu.Track(Object.Instantiate(menu.numberOptionOrigin, Vector3.zero, Quaternion.identity, menu.settingsContainer)),
                            _ => throw new Exception()
                        };
                    }
                    catch { continue; }

                    if (isLeft)
                    {
                        optionBehaviour.transform.localPosition = new(NewRoleMenuLayout.MasterRowX, numL, posZ);
                        GameOptionsMenuPatch.OptionBehaviourSetSizeAndPosition(optionBehaviour, option, baseGameSetting.Type);

                        if (!ModGameOptionsMenu.OptionList.ContainsValue(index) && option.Name == "Preset")
                            GameSettingMenuPatch.PresetBehaviour = (NumberOption)optionBehaviour;

                        optionBehaviour.transform.localPosition = new(NewRoleMenuLayout.MasterRowX, numL, posZ);
                        optionBehaviour.SetClickMask(menu.ButtonClickMask);
                        optionBehaviour.SetUpFromData(baseGameSetting, 20);
                        optionBehaviour.gameObject.SetActive(enabledOrNotCollapsed);
                        optionBehaviour.OnValueChanged = new Action<OptionBehaviour>(menu.ValueChanged);

                        ModGameOptionsMenu.OptionList[optionBehaviour] = index;
                        ModGameOptionsMenu.BehaviourList[index] = optionBehaviour;

                        menu.Children.Add(optionBehaviour);
                        option.OptionBehaviour = optionBehaviour;

                        // A: de-emphasis — fade TitleText for disabled (0%) master rows.
                        // Absolute target (never multiply) so Reflow can safely reset to 1.0.
                        {
                            float a = IsSelectableMaster(option) && !option.GetBool() ? 0.45f : 1f;
                            var titleTmp = optionBehaviour.GetComponentInChildren<TextMeshPro>(true);
                            if (titleTmp) titleTmp.alpha = a;
                        }

                        float masterY = numL; // capture Y before advancing (same as row position)
                        if (enabledOrNotCollapsed)
                        {
                            if (option == selected)
                            {
                                NewRoleMenuState.SelectedHeaderY = numL;
                                numR = numL - NewRoleMenuLayout.DetailHeaderHeight; // leave space for L2 header
                            }
                            numL -= NewRoleMenuLayout.RowStep;
                        }

                        // Selection bar: thin faction-color strip at the far-left edge of the left
                        // pane. Lives in settingsContainer (scrolls with content). Placed where no
                        // StringOption content reaches, so always visible above the pane background.
                        {
                            bool isSel = option == selected;
                            var bar = new GameObject("RM_SelBar");
                            bar.transform.SetParent(menu.settingsContainer, false);
                            bar.transform.localPosition = new Vector3(NewRoleMenuState.BarLocalX, masterY, NewRoleMenuLayout.PosZ);
                            bar.transform.localScale = new Vector3(NewRoleMenuState.BarWidth, NewRoleMenuState.BarHeight, 1f);
                            var barSr = bar.AddComponent<SpriteRenderer>();
                            barSr.sprite = Solid();
                            UnityEngine.Color nc = option.NameColor;
                            barSr.color = isSel ? new UnityEngine.Color(nc.r, nc.g, nc.b, 1f) : UnityEngine.Color.clear;
                            barSr.sortingLayerID = NewRoleMenuState.BarSortLayerID;
                            barSr.sortingOrder   = NewRoleMenuState.BarSortOrder;
                            bar.SetActive(enabledOrNotCollapsed && isSel);
                            ModGameOptionsMenu.Track(bar);
                            NewRoleMenuState.SelBars[index] = barSr;
                        }

                        // Phase 3b: click-to-select overlay (label area, left of value +/- buttons).
                        {
                            int capturedIndex = index;
                            var selGo = new GameObject("RM_SelectBtn");
                            selGo.transform.SetParent(optionBehaviour.transform, false);
                            selGo.transform.localPosition = new Vector3(-1.5f, 0f, -0.5f);
                            var selCol = selGo.AddComponent<BoxCollider2D>();
                            selCol.size = new Vector2(2.5f, 0.4f);
                            var selBtn = selGo.AddComponent<PassiveButton>();
                            selBtn.ClickSound = menu.BackButton.GetComponent<PassiveButton>().ClickSound;
                            selBtn.OnMouseOver = new();
                            selBtn.OnMouseOut = new();
                            selBtn.OnClick.AddListener((UnityAction)(() =>
                            {
                                if (!menu || NewRoleMenuState.SelectedIndex == capturedIndex) return;
                                NewRoleMenuState.SelectedIndex = capturedIndex;
                                GameOptionsMenuPatch.ReCreateSettings(menu);
                            }));
                            selBtn.SetButtonEnableState(true);
                        }
                    }
                    else
                    {
                        bool show = selected != null && TopMaster(option) == selected;

                        optionBehaviour.transform.localPosition = new(NewRoleMenuLayout.DetailRowX, numR, posZ);
                        GameOptionsMenuPatch.OptionBehaviourSetSizeAndPosition(optionBehaviour, option, baseGameSetting.Type);

                        if (!ModGameOptionsMenu.OptionList.ContainsValue(index) && option.Name == "Preset")
                            GameSettingMenuPatch.PresetBehaviour = (NumberOption)optionBehaviour;

                        optionBehaviour.transform.localPosition = new(NewRoleMenuLayout.DetailRowX, numR, posZ);
                        optionBehaviour.SetClickMask(menu.ButtonClickMask);
                        optionBehaviour.SetUpFromData(baseGameSetting, 20);
                        optionBehaviour.gameObject.SetActive(show);
                        optionBehaviour.OnValueChanged = new Action<OptionBehaviour>(menu.ValueChanged);

                        ModGameOptionsMenu.OptionList[optionBehaviour] = index;
                        ModGameOptionsMenu.BehaviourList[index] = optionBehaviour;

                        menu.Children.Add(optionBehaviour);
                        option.OptionBehaviour = optionBehaviour;

                        if (show) numR -= NewRoleMenuLayout.RowStep;
                    }
                }
                catch (Exception e) { Utils.ThrowException(e); }

                if (index % 100 == 0) yield return null;
            }

            // Rebuild L2 detail header chrome after the full list is built.
            RebuildDetailHead(menu, SelectedMaster(modTab), modTab);

            menu.ControllerSelectable.Clear();

            foreach (UiElement x in menu.scrollBar.GetComponentsInChildren<UiElement>())
                menu.ControllerSelectable.Add(x);
        }
    }

    public static void Reflow(GameOptionsMenu menu, TabGroup modTab)
    {
        float numL = NewRoleMenuLayout.TopY;
        float numR = NewRoleMenuLayout.TopY;

        OptionItem selected = SelectedMaster(modTab);

        for (var index = 0; index < OptionItem.AllOptions.Count; index++)
        {
            OptionItem option = OptionItem.AllOptions[index];
            if (option.Tab != modTab) continue;

            bool enabledOrNotCollapsed = !option.IsCurrentlyHidden() && GameOptionsMenuPatch.AllParentsEnabledAndVisible(option.Parent);
            bool enabled = !option.IsCurrentlyHidden(checkCollapsedSection: false) && GameOptionsMenuPatch.AllParentsEnabledAndVisible(option.Parent, checkCollapsedSection: false);

            bool isLeft = option is TextOptionItem || option.Parent == null;

            if (ModGameOptionsMenu.CategoryHeaderList.TryGetValue(index, out CategoryHeaderMasked categoryHeaderMasked))
            {
                categoryHeaderMasked.transform.localPosition = new(NewRoleMenuLayout.MasterHeaderX, numL, NewRoleMenuLayout.PosZ);
                categoryHeaderMasked.gameObject.SetActive(enabled);
                if (enabledOrNotCollapsed) numL -= NewRoleMenuLayout.HeaderStep;
            }
            else if (isLeft && option.IsHeader && enabledOrNotCollapsed)
            {
                numL -= NewRoleMenuLayout.SubHeaderGap;
            }
            else if (!isLeft)
            {
                bool showDetail = selected != null && TopMaster(option) == selected;
                if (option.IsHeader && showDetail)
                    numR -= NewRoleMenuLayout.SubHeaderGap;
            }

            if (ModGameOptionsMenu.BehaviourList.TryGetValue(index, out OptionBehaviour optionBehaviour))
            {
                if (isLeft)
                {
                    optionBehaviour.transform.localPosition = new(NewRoleMenuLayout.MasterRowX, numL, NewRoleMenuLayout.PosZ);
                    optionBehaviour.gameObject.SetActive(enabledOrNotCollapsed);

                    // A: de-emphasis — update dim on value change (0% enable/disable toggle).
                    {
                        float a = IsSelectableMaster(option) && !option.GetBool() ? 0.45f : 1f;
                        var titleTmp = optionBehaviour.GetComponentInChildren<TextMeshPro>(true);
                        if (titleTmp) titleTmp.alpha = a;
                    }

                    // Update selection bar for this row (repositioned at numL before Y advance).
                    if (NewRoleMenuState.SelBars.TryGetValue(index, out var barSr) && barSr)
                    {
                        bool isSel = option == selected;
                        barSr.transform.localPosition = new Vector3(NewRoleMenuState.BarLocalX, numL, NewRoleMenuLayout.PosZ);
                        UnityEngine.Color nc = option.NameColor;
                        barSr.color = isSel ? new UnityEngine.Color(nc.r, nc.g, nc.b, 1f) : UnityEngine.Color.clear;
                        barSr.gameObject.SetActive(enabledOrNotCollapsed && isSel);
                    }

                    if (enabledOrNotCollapsed)
                    {
                        if (option == selected)
                        {
                            NewRoleMenuState.SelectedHeaderY = numL;
                            numR = numL - NewRoleMenuLayout.DetailHeaderHeight; // leave space for L2 header
                        }
                        numL -= NewRoleMenuLayout.RowStep;
                    }
                }
                else
                {
                    bool show = selected != null && TopMaster(option) == selected;
                    optionBehaviour.transform.localPosition = new(NewRoleMenuLayout.DetailRowX, numR, NewRoleMenuLayout.PosZ);
                    optionBehaviour.gameObject.SetActive(show);
                    if (show) numR -= NewRoleMenuLayout.RowStep;
                }
            }
        }

        // Rebuild L2 detail header only when selection changed (not on every value-change Reflow).
        if (NewRoleMenuState.SelectedIndex != NewRoleMenuState.LastBuiltSelectedIndex)
            RebuildDetailHead(menu, SelectedMaster(modTab), modTab);

        menu.ControllerSelectable.Clear();

        foreach (UiElement x in menu.scrollBar.GetComponentsInChildren<UiElement>())
            menu.ControllerSelectable.Add(x);

        menu.scrollBar.SetYBoundsMax(-Math.Min(numL, numR) - NewRoleMenuLayout.ScrollBottomPad);
    }

    private static float CalculateScrollBarYBoundsMax(TabGroup modTab)
    {
        float numL = NewRoleMenuLayout.TopY;
        float numR = NewRoleMenuLayout.TopY;

        OptionItem selected = SelectedMaster(modTab);

        foreach (OptionItem option in OptionItem.AllOptions)
        {
            if (option.Tab != modTab) continue;

            bool enabledOrNotCollapsed = !option.IsCurrentlyHidden() && GameOptionsMenuPatch.AllParentsEnabledAndVisible(option.Parent);

            bool isLeft = option is TextOptionItem || option.Parent == null;

            if (option is TextOptionItem)
            {
                if (enabledOrNotCollapsed) numL -= NewRoleMenuLayout.HeaderStep;
            }
            else if (isLeft)
            {
                if (enabledOrNotCollapsed)
                {
                    if (option.IsHeader) numL -= NewRoleMenuLayout.SubHeaderGap;
                    if (option == selected) numR = numL - NewRoleMenuLayout.DetailHeaderHeight;
                    numL -= NewRoleMenuLayout.RowStep;
                }
            }
            else
            {
                bool show = selected != null && TopMaster(option) == selected;
                if (show)
                {
                    if (option.IsHeader) numR -= NewRoleMenuLayout.SubHeaderGap;
                    numR -= NewRoleMenuLayout.RowStep;
                }
            }
        }

        return -Math.Min(numL, numR) - NewRoleMenuLayout.ScrollBottomPad;
    }

    // ===================================================================================
    //  L2 detail header — rebuilt on selection change (not build-all-toggle).
    //  Uses CategoryHeaderMasked clones so the masked material is guaranteed correct.
    // ===================================================================================

    private static void RebuildDetailHead(GameOptionsMenu menu, OptionItem selected, TabGroup modTab)
    {
        foreach (var go in NewRoleMenuState.DetailObjects) { if (go) Object.Destroy(go); }
        NewRoleMenuState.DetailObjects.Clear();
        NewRoleMenuState.LastBuiltSelectedIndex = NewRoleMenuState.SelectedIndex;

        if (selected == null || !menu.categoryHeaderOrigin) return;

        float y = NewRoleMenuState.SelectedHeaderY;
        const float posZ = NewRoleMenuLayout.PosZ;
        const float hx   = -1.5f;  // settingsContainer local X — near right-pane left edge
        // Size via localScale only (fontSizeMin/Max are in world-units and would be huge at face value;
        // PxToFontSize calibration is a later gate). 0.63 matches the working category-header scale.
        const float nameScale = 0.63f;
        const float subScale  = 0.44f; // 0.63 * 0.70 — smaller for stars / description

        string rawName = selected.GetName(disableColor: true);
        string roleName = Regex.Replace(rawName, @"<[^>]*>", "").Trim('★', '☆', ' ');

        string infoLong = "";
        try { infoLong = Translator.GetString($"{selected.Name}InfoLong"); } catch { }
        int filledStars = infoLong.Count(c => c == '★');
        int emptyStars  = infoLong.Count(c => c == '☆');

        Color nc = selected.NameColor;

        // 1. Role name — faction-colored header banner
        {
            var clone = Object.Instantiate(menu.categoryHeaderOrigin, Vector3.zero, Quaternion.identity, menu.settingsContainer);
            NewRoleMenuState.DetailObjects.Add(clone.gameObject);
            clone.SetHeader(StringNames.RolesCategory, 20);
            clone.Title.SetText(roleName);
            clone.Background.color = new Color(nc.r, nc.g, nc.b, 0.85f);
            clone.Divider.color    = new Color(nc.r, nc.g, nc.b, 0.6f);
            clone.transform.localScale    = Vector3.one * nameScale;
            clone.transform.localPosition = new Vector3(hx, y - 0.10f, posZ);
        }

        float cursor = y - 0.42f;

        // 2. Difficulty stars — smaller via subScale, no font-size override
        if (filledStars + emptyStars > 0)
        {
            string starsStr = new string('★', filledStars) + new string('☆', emptyStars);
            var clone = Object.Instantiate(menu.categoryHeaderOrigin, Vector3.zero, Quaternion.identity, menu.settingsContainer);
            NewRoleMenuState.DetailObjects.Add(clone.gameObject);
            clone.SetHeader(StringNames.RolesCategory, 20);
            clone.Title.SetText(starsStr);
            clone.Title.color    = RoleMenuTokens.StarFilled;
            clone.Background.enabled = false;
            clone.Divider.enabled    = false;
            clone.transform.localScale    = Vector3.one * subScale;
            clone.transform.localPosition = new Vector3(hx, cursor, posZ);
            cursor -= 0.22f;
        }

        // 3. Description — short one-line {role}Info (clean: no rich tags / stars / line breaks).
        // InfoLong is reserved for the ★ count only; using it as the body produced the messy
        // multi-line "[Skills]"-leaking text seen in earlier builds.
        string desc = Translator.GetString($"{selected.Name}Info");
        if (!string.IsNullOrEmpty(desc) && !desc.StartsWith('*')) // '*' = missing-key fallback
        {
            if (desc.Length > 60) desc = desc[..60] + "…";
            var clone = Object.Instantiate(menu.categoryHeaderOrigin, Vector3.zero, Quaternion.identity, menu.settingsContainer);
            NewRoleMenuState.DetailObjects.Add(clone.gameObject);
            clone.SetHeader(StringNames.RolesCategory, 20);
            clone.Title.SetText(desc);
            clone.Title.color    = RoleMenuTokens.TMid;
            clone.Background.enabled = false;
            clone.Divider.enabled    = false;
            clone.transform.localScale    = Vector3.one * subScale;
            clone.transform.localPosition = new Vector3(hx, cursor, posZ);
        }
    }

    // ===================================================================================
    //  Consolidated settings tab — System + Mod + Task merged into one sectioned column.
    //  View-only re-grouping: each option keeps its real Tab/Id/Name; we just render the
    //  three setting TabGroups back-to-back (their existing TextOptionItem headers stay as
    //  sub-sections). Full single column (all rows incl. children), original centre layout.
    // ===================================================================================
    public static bool IsConsolidatedSettingsTab(TabGroup t) => t is TabGroup.SystemSettings or TabGroup.GameSettings or TabGroup.TaskSettings;
    private static bool InSettingsScope(OptionItem o) => o.Tab is TabGroup.SystemSettings or TabGroup.GameSettings or TabGroup.TaskSettings;

    public static void BuildSettings(GameOptionsMenu menu)
    {
        menu.scrollBar.SetYBoundsMax(CalcSettingsBounds());
        menu.StartCoroutine(CoRoutine().WrapToIl2Cpp());
        return;

        IEnumerator CoRoutine()
        {
            var num = NewRoleMenuLayout.TopY;
            const float posZ = NewRoleMenuLayout.PosZ;

            TextOptionItem header = null;

            for (var index = 0; index < OptionItem.AllOptions.Count; index++)
            {
                try
                {
                    OptionItem option = OptionItem.AllOptions[index];
                    if (!InSettingsScope(option)) continue;

                    bool enabledOrNotCollapsed = !option.IsCurrentlyHidden() && GameOptionsMenuPatch.AllParentsEnabledAndVisible(option.Parent);
                    bool enabled = !option.IsCurrentlyHidden(checkCollapsedSection: false) && GameOptionsMenuPatch.AllParentsEnabledAndVisible(option.Parent, checkCollapsedSection: false);

                    if (option is TextOptionItem toi)
                    {
                        CategoryHeaderMasked categoryHeaderMasked = ModGameOptionsMenu.Track(Object.Instantiate(menu.categoryHeaderOrigin, Vector3.zero, Quaternion.identity, menu.settingsContainer));
                        categoryHeaderMasked.SetHeader(StringNames.RolesCategory, 20);
                        categoryHeaderMasked.Title.SetText(option.GetName(disableColor: true).Trim('★', ' '));
                        categoryHeaderMasked.Background.color = categoryHeaderMasked.Divider.color = option.NameColor;
                        categoryHeaderMasked.transform.localScale = Vector3.one * 0.63f;
                        categoryHeaderMasked.transform.localPosition = new(NewRoleMenuLayout.SettingsHeaderX, num, posZ);
                        var chmText = categoryHeaderMasked.transform.FindChild("HeaderText").GetComponent<TextMeshPro>();
                        chmText.fontStyle = FontStyles.Bold | FontStyles.SmallCaps;
                        chmText.fontWeight = FontWeight.Black;
                        chmText.outlineWidth = 0.17f;
                        var chmCollider = categoryHeaderMasked.gameObject.AddComponent<BoxCollider2D>();
                        chmCollider.size = new Vector2(7, 0.7f);
                        chmCollider.offset = new Vector2(1.5f, -0.3f);
                        var chmButton = categoryHeaderMasked.gameObject.AddComponent<PassiveButton>();
                        chmButton.ClickSound = menu.BackButton.GetComponent<PassiveButton>().ClickSound;
                        chmButton.OnMouseOver = new();
                        chmButton.OnMouseOut = new();
                        chmButton.OnClick.AddListener((UnityAction)(() =>
                        {
                            toi.CollapsesSection = !toi.CollapsesSection;
                            GameOptionsMenuPatch.ReCreateSettings(menu);
                        }));
                        chmButton.SetButtonEnableState(true);
                        categoryHeaderMasked.gameObject.SetActive(enabled);
                        ModGameOptionsMenu.CategoryHeaderList[index] = categoryHeaderMasked;

                        if (enabledOrNotCollapsed) num -= NewRoleMenuLayout.HeaderStep;
                        header = toi;
                        continue;
                    }

                    option.Header = header;

                    if (option.IsHeader && enabledOrNotCollapsed)
                        num -= NewRoleMenuLayout.SubHeaderGap;

                    BaseGameSetting baseGameSetting = GameOptionsMenuPatch.GetSetting(option);
                    if (!baseGameSetting) continue;

                    OptionBehaviour optionBehaviour;

                    try
                    {
                        optionBehaviour = baseGameSetting.Type switch
                        {
                            OptionTypes.Checkbox => ModGameOptionsMenu.Track(Object.Instantiate(menu.checkboxOrigin, Vector3.zero, Quaternion.identity, menu.settingsContainer)),
                            OptionTypes.String => ModGameOptionsMenu.Track(Object.Instantiate(menu.stringOptionOrigin, Vector3.zero, Quaternion.identity, menu.settingsContainer)),
                            OptionTypes.Float or OptionTypes.Int => ModGameOptionsMenu.Track(Object.Instantiate(menu.numberOptionOrigin, Vector3.zero, Quaternion.identity, menu.settingsContainer)),
                            _ => throw new Exception()
                        };
                    }
                    catch { continue; }

                    optionBehaviour.transform.localPosition = new(NewRoleMenuLayout.SettingsRowX, num, posZ);
                    GameOptionsMenuPatch.OptionBehaviourSetSizeAndPosition(optionBehaviour, option, baseGameSetting.Type);

                    if (!ModGameOptionsMenu.OptionList.ContainsValue(index) && option.Name == "Preset")
                        GameSettingMenuPatch.PresetBehaviour = (NumberOption)optionBehaviour;

                    optionBehaviour.transform.localPosition = new(NewRoleMenuLayout.SettingsRowX, num, posZ);
                    optionBehaviour.SetClickMask(menu.ButtonClickMask);
                    optionBehaviour.SetUpFromData(baseGameSetting, 20);
                    optionBehaviour.gameObject.SetActive(enabledOrNotCollapsed);
                    optionBehaviour.OnValueChanged = new Action<OptionBehaviour>(menu.ValueChanged);

                    ModGameOptionsMenu.OptionList[optionBehaviour] = index;
                    ModGameOptionsMenu.BehaviourList[index] = optionBehaviour;

                    menu.Children.Add(optionBehaviour);
                    option.OptionBehaviour = optionBehaviour;

                    if (enabledOrNotCollapsed) num -= NewRoleMenuLayout.RowStep;
                }
                catch (Exception e) { Utils.ThrowException(e); }

                if (index % 100 == 0) yield return null;
            }

            menu.ControllerSelectable.Clear();

            foreach (UiElement x in menu.scrollBar.GetComponentsInChildren<UiElement>())
                menu.ControllerSelectable.Add(x);
        }
    }

    public static void ReflowSettings(GameOptionsMenu menu)
    {
        var num = NewRoleMenuLayout.TopY;

        for (var index = 0; index < OptionItem.AllOptions.Count; index++)
        {
            OptionItem option = OptionItem.AllOptions[index];
            if (!InSettingsScope(option)) continue;

            bool enabledOrNotCollapsed = !option.IsCurrentlyHidden() && GameOptionsMenuPatch.AllParentsEnabledAndVisible(option.Parent);
            bool enabled = !option.IsCurrentlyHidden(checkCollapsedSection: false) && GameOptionsMenuPatch.AllParentsEnabledAndVisible(option.Parent, checkCollapsedSection: false);

            if (ModGameOptionsMenu.CategoryHeaderList.TryGetValue(index, out CategoryHeaderMasked categoryHeaderMasked))
            {
                categoryHeaderMasked.transform.localPosition = new(NewRoleMenuLayout.SettingsHeaderX, num, NewRoleMenuLayout.PosZ);
                categoryHeaderMasked.gameObject.SetActive(enabled);
                if (enabledOrNotCollapsed) num -= NewRoleMenuLayout.HeaderStep;
            }
            else if (option.IsHeader && enabledOrNotCollapsed) num -= NewRoleMenuLayout.SubHeaderGap;

            if (ModGameOptionsMenu.BehaviourList.TryGetValue(index, out OptionBehaviour optionBehaviour))
            {
                optionBehaviour.transform.localPosition = new(NewRoleMenuLayout.SettingsRowX, num, NewRoleMenuLayout.PosZ);
                optionBehaviour.gameObject.SetActive(enabledOrNotCollapsed);
                if (enabledOrNotCollapsed) num -= NewRoleMenuLayout.RowStep;
            }
        }

        menu.ControllerSelectable.Clear();

        foreach (UiElement x in menu.scrollBar.GetComponentsInChildren<UiElement>())
            menu.ControllerSelectable.Add(x);

        menu.scrollBar.SetYBoundsMax(-num - NewRoleMenuLayout.ScrollBottomPad);
    }

    private static float CalcSettingsBounds()
    {
        var num = NewRoleMenuLayout.TopY;

        foreach (OptionItem option in OptionItem.AllOptions)
        {
            if (!InSettingsScope(option)) continue;

            bool enabledOrNotCollapsed = !option.IsCurrentlyHidden() && GameOptionsMenuPatch.AllParentsEnabledAndVisible(option.Parent);

            if (option is TextOptionItem)
                num -= NewRoleMenuLayout.HeaderStep;
            else if (enabledOrNotCollapsed)
            {
                if (option.IsHeader) num -= NewRoleMenuLayout.SubHeaderGap;
                num -= NewRoleMenuLayout.RowStep;
            }
        }

        return -num - NewRoleMenuLayout.ScrollBottomPad;
    }
}
