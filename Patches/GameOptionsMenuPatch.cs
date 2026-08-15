using System;
using System.Collections;
using System.Linq;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using EndKnot.Gamemodes;
using EndKnot.Modules;
using EndKnot.Patches;
using EndKnot.Roles;
using HarmonyLib;
using Il2CppSystem.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Events;

// ReSharper disable PossibleLossOfFraction

namespace EndKnot;
// Credit: https://github.com/Yumenopai/TownOfHost_Y

public static class ModGameOptionsMenu
{
    public static int TabIndex;
    public static readonly Dictionary<OptionBehaviour, int> OptionList = new();
    public static readonly Dictionary<int, OptionBehaviour> BehaviourList = new();
    public static readonly Dictionary<int, CategoryHeaderMasked> CategoryHeaderList = new();
    public static readonly System.Collections.Generic.Dictionary<CustomRoles, Transform> HelpIconList = [];
    public static readonly System.Collections.Generic.Dictionary<OptionItem, BaseGameSetting> BaseGameSettingCache = [];

    // Nothing reads the tracked set since DestroyOption was removed; keeping one would only pin
    // references to destroyed GameObjects for the lifetime of the process.
    public static T Track<T>(T obj) where T : Object
    {
        return obj;
    }

    // OptionList は OptionBehaviour インスタンス自体がキーのため、メニュー再構築のたびに
    // 破棄済み個体のエントリが積み残る。生きている他タブの項目は消せないので、死んだキーだけ間引く。
    public static void PruneDeadEntries()
    {
        System.Collections.Generic.List<OptionBehaviour> dead = null;
        foreach (OptionBehaviour key in OptionList.Keys)
            if (!key)
                (dead ??= new()).Add(key);

        if (dead == null) return;
        foreach (OptionBehaviour key in dead) OptionList.Remove(key);
    }

    public static Color32 GetCurrentThemeColor(TabGroup? tab = null)
    {
        CustomGameMode gm = Options.CurrentGameMode;
        Color32 color = gm == CustomGameMode.Standard ? (tab ?? (TabGroup)(TabIndex - 3)) switch
        {
            TabGroup.ImpostorRoles => new Color32(255, 25, 25, 255),
            TabGroup.CrewmateRoles => new Color32(140, 255, 255, 255),
            TabGroup.NeutralRoles => new Color32(255, 171, 27, 255),
            TabGroup.CovenRoles => new Color32(123, 63, 187, 255),
            _ => new Color32(0, 165, 255, 255)
        } : Main.GameModeColors.TryGetValue(gm, out var c) ? c : new Color32(0, 165, 255, 255);
        return color;
    }
}

[HarmonyPatch(typeof(GameOptionsMenu))]
public static class GameOptionsMenuPatch
{
    private static readonly Dictionary<GameOptionsMenu, Coroutine> BuildCoroutines = new();
    private static readonly Dictionary<GameOptionsMenu, int> BuildGenerations = new();

    // メモリリーク計器 (BUG-20260706-01): 一度ビルドした index が再ビルドされたら、その option 名と
    // 「dict 欠落 / Unity-dead キャッシュ」のどちらが原因かを記録する。初回ビルドは記録しない (スパム防止)。
    private static readonly System.Collections.Generic.HashSet<int> EverBuiltRows = new();
    private static readonly System.Collections.Generic.HashSet<int> EverBuiltHeaders = new();

    // 意図的なキャッシュ全破棄 (GameSettingMenuPatch.PurgeUiCache) の後始末。EverBuilt* を残すと
    // 次回オープンの正当な再ビルドが MenuLeak の「row rebuild (dead-cache)」として誤記録される。
    internal static void OnUiCachePurged()
    {
        EverBuiltRows.Clear();
        EverBuiltHeaders.Clear();
        BuildCoroutines.Clear(); // メニュー個体ごと破棄済み — StopCoroutine は不要 (個体の死で止まる)
        BuildGenerations.Clear();

        if (ReCreateAllCoroutine != null)
        {
            Main.Instance.StopCoroutine(ReCreateAllCoroutine);
            ReCreateAllCoroutine = null;
        }

        ReCreateGeneration++;
    }

    [HarmonyPatch(nameof(GameOptionsMenu.Initialize))]
    [HarmonyPrefix]
    private static bool InitializePrefix(GameOptionsMenu __instance)
    {
        ModGameOptionsMenu.PruneDeadEntries();

        if (ModGameOptionsMenu.TabIndex < 3) return true;

        if (__instance.Children == null || __instance.Children.Count == 0)
        {
            __instance.MapPicker.gameObject.SetActive(false);
            __instance.Children = new();
            __instance.CreateSettings();
            __instance.cachedData = GameOptionsManager.Instance.CurrentGameOptions;
            __instance.InitializeControllerNavigation();
        }

        return false;
    }

    [HarmonyPatch(nameof(GameOptionsMenu.Initialize))]
    [HarmonyPostfix]
    private static void InitializePostfix()
    {
        GameObject optionMenu = GameObject.Find("PlayerOptionsMenu(Clone)");
        if (!optionMenu) return;

        optionMenu.transform.FindChild("Background")?.gameObject.SetActive(false);

        // By TommyXL
        LateTask.New(() =>
        {
            if (!optionMenu) return;

            Transform menuDescription = optionMenu.transform.FindChild("What Is This?");
            if (!menuDescription) return;

            Transform infoImage = menuDescription.transform.FindChild("InfoImage");

            if (infoImage)
            {
                infoImage.transform.localPosition = new(-4.65f, 0.16f, -1f);
                infoImage.transform.localScale = new(0.2202f, 0.2202f, 0.3202f);
            }

            Transform infoText = menuDescription.transform.FindChild("InfoText");

            if (infoText)
            {
                infoText.transform.localPosition = new(-3.5f, 0.83f, -2f);
                infoText.transform.localScale = new(1f, 1f, 1f);
            }

            Transform cubeObject = menuDescription.transform.FindChild("Cube");

            if (cubeObject)
            {
                cubeObject.transform.localPosition = new(-3.2f, 0.55f, -0.1f);
                cubeObject.transform.localScale = new(0.61f, 0.64f, 1f);
            }

            if (GameSettingMenu.Instance && GameSettingMenu.Instance.MenuDescriptionText)
                GameSettingMenu.Instance.MenuDescriptionText.m_marginWidth = 2.5f;
        }, 0.2f, log: false);
    }

    [HarmonyPatch(nameof(GameOptionsMenu.CreateSettings))]
    [HarmonyPrefix]
    private static bool CreateSettingsPrefix(GameOptionsMenu __instance)
    {
        if (ModGameOptionsMenu.TabIndex < 3) return true;

        var modTab = (TabGroup)(ModGameOptionsMenu.TabIndex - 3);
        
        if (modTab == TabGroup.PresetExplorer)
        {
            OnlinePresetsManager.CreatePresetExplorerUI(__instance);
            return false;
        }

        // New menu (kill-switch: NewRoleMenuState.Active). Role tabs -> two-pane master/detail.
        // The three setting TabGroups (System/Mod/Task) -> one consolidated, sectioned column.
        // PresetExplorer (handled above) is the only tab that still uses the original renderer.
        // A search result list spans every tab, so it always goes through the original renderer.
        if (NewRoleMenuState.Active && !OptionSearch.Active && modTab is >= TabGroup.ImpostorRoles and <= TabGroup.OtherRoles)
        {
            NewRoleMenuView.Build(__instance, modTab);
            return false;
        }

        if (NewRoleMenuState.Active && !OptionSearch.Active && NewRoleMenuView.IsConsolidatedSettingsTab(modTab))
        {
            NewRoleMenuView.BuildSettings(__instance);
            return false;
        }

        if (!__instance.gameObject.activeInHierarchy) return false;

        __instance.scrollBar.SetYBoundsMax(CalculateScrollBarYBoundsMax());

        if (BuildCoroutines.TryGetValue(__instance, out Coroutine existing) && existing != null)
        {
            __instance.StopCoroutine(existing);
            BuildCoroutines.Remove(__instance);
        }

        BuildGenerations.TryGetValue(__instance, out int currentGen);
        currentGen++;
        BuildGenerations[__instance] = currentGen;

        BuildCoroutines[__instance] = __instance.StartCoroutine(CoRoutine(currentGen).WrapToIl2Cpp());
        return false;

        IEnumerator CoRoutine(int gen)
        {
            var num = 2.0f;
            const float posX = 0.952f;
            const float posZ = -2.0f;

            TextOptionItem header = null;

            HideRowsNotInView(__instance, modTab);

            for (var index = 0; index < OptionItem.AllOptions.Count; index++)
            {
                try
                {
                    OptionItem option = OptionItem.AllOptions[index];
                    if (!OptionSearch.IsInView(option, index, modTab)) continue;

                    bool enabledOrNotCollapsed = RowVisibleInView(option);
                    bool enabled = !option.IsCurrentlyHidden(checkCollapsedSection: false) && AllParentsEnabledAndVisible(option.Parent, checkCollapsedSection: false);

                    if (option is TextOptionItem toi)
                    {
                        // Reuse the cached header only if it hasn't been destroyed (a scene reload / rehost destroys
                        // the GameObjects but leaves this static dict pointing at them). A stale destroyed ref must
                        // fall through to a fresh Instantiate, otherwise SetHeader below throws and the item vanishes.
                        bool chmHadEntry = ModGameOptionsMenu.CategoryHeaderList.TryGetValue(index, out CategoryHeaderMasked cacheCHM);
                        bool freshCHM = !(chmHadEntry && cacheCHM);
                        if (freshCHM && !EverBuiltHeaders.Add(index))
                            Logger.Info($"header rebuild: {option.Name} idx={index} tab={modTab} reason={(chmHadEntry ? "dead-cache" : "no-entry")}", "MenuLeak");
                        else EverBuiltHeaders.Add(index);
                        CategoryHeaderMasked categoryHeaderMasked = freshCHM ? ModGameOptionsMenu.Track(Object.Instantiate(__instance.categoryHeaderOrigin, Vector3.zero, Quaternion.identity, __instance.settingsContainer)) : cacheCHM;
                        // SetHeader (native) re-instances the masked sprite/font materials on every call — running it
                        // on a cached header leaked Material instances (DepthMaskedTexture/SDF "(Instance)") each menu
                        // open (BUG-20260706-01 round8 ③). Mask setup and the one-time font styling (outlineWidth also
                        // touches fontMaterial) only need to happen when the header is freshly instantiated.
                        if (freshCHM)
                        {
                            categoryHeaderMasked.SetHeader(StringNames.RolesCategory, 20);
                            var chmText = categoryHeaderMasked.transform.FindChild("HeaderText").GetComponent<TextMeshPro>();
                            chmText.fontStyle = FontStyles.Bold | FontStyles.SmallCaps;
                            chmText.fontWeight = FontWeight.Black;
                            chmText.outlineWidth = 0.17f;
                        }
                        categoryHeaderMasked.Title.SetText(option.GetName(disableColor: true).Trim('★', ' '));
                        categoryHeaderMasked.Background.color = categoryHeaderMasked.Divider.color = option.NameColor;
                        categoryHeaderMasked.transform.localScale = Vector3.one * 0.63f;
                        categoryHeaderMasked.transform.localPosition = new(-0.903f, num, posZ);
                        // Collider + click handler only on a freshly-instantiated header; re-running AddComponent and
                        // AddListener on a reused header stacks duplicate colliders/buttons/handlers every rebuild.
                        if (freshCHM)
                        {
                            var chmCollider = categoryHeaderMasked.gameObject.AddComponent<BoxCollider2D>();
                            chmCollider.size = new Vector2(7, 0.7f);
                            chmCollider.offset = new Vector2(1.5f, -0.3f);
                            var chmButton = categoryHeaderMasked.gameObject.AddComponent<PassiveButton>();
                            chmButton.ClickSound = __instance.BackButton.GetComponent<PassiveButton>().ClickSound;
                            chmButton.OnMouseOver = new();
                            chmButton.OnMouseOut = new();
                            chmButton.OnClick.AddListener((UnityAction)(() =>
                            {
                                toi.CollapsesSection = !toi.CollapsesSection;
                                ReCreateSettings(__instance);
                            }));
                            chmButton.SetButtonEnableState(true);
                        }
                        categoryHeaderMasked.gameObject.SetActive(enabled);
                        ModGameOptionsMenu.CategoryHeaderList[index] = categoryHeaderMasked;

                        if (enabledOrNotCollapsed) num -= 0.63f;
                        header = toi;
                        continue;
                    }

                    // A search result list carries no category headers, so it must not overwrite the
                    // header each option was filed under in its own tab.
                    if (!OptionSearch.Active) option.Header = header;

                    if (option.IsHeader && enabledOrNotCollapsed)
                        num -= 0.18f;

                    BaseGameSetting baseGameSetting = GetSetting(option);
                    if (!baseGameSetting) continue;

                    // `|| !optionBehaviour`: a cached behaviour destroyed by a scene reload / rehost must be treated
                    // as absent and re-instantiated, or the reuse branch below touches a destroyed object and throws
                    // (per-item catch then swallows it → the row silently vanishes). Mirrors the L505/L513 guards.
                    bool rowHadEntry = ModGameOptionsMenu.BehaviourList.TryGetValue(index, out OptionBehaviour optionBehaviour);
                    bool freshRow = !rowHadEntry || !optionBehaviour;
                    if (freshRow)
                    {
                        if (!EverBuiltRows.Add(index))
                            Logger.Info($"row rebuild: {option.Name} idx={index} tab={modTab} reason={(rowHadEntry ? "dead-cache" : "no-entry")}", "MenuLeak");

                        try
                        {
                            optionBehaviour = baseGameSetting.Type switch
                            {
                                OptionTypes.Checkbox => ModGameOptionsMenu.Track(Object.Instantiate(__instance.checkboxOrigin, Vector3.zero, Quaternion.identity, __instance.settingsContainer)),
                                OptionTypes.String => ModGameOptionsMenu.Track(Object.Instantiate(__instance.stringOptionOrigin, Vector3.zero, Quaternion.identity, __instance.settingsContainer)),
                                OptionTypes.Float or OptionTypes.Int => ModGameOptionsMenu.Track(Object.Instantiate(__instance.numberOptionOrigin, Vector3.zero, Quaternion.identity, __instance.settingsContainer)),
                                _ => throw new Exception()
                            };
                        }
                        catch { continue; }

                        optionBehaviour.transform.localPosition = new(posX, num, posZ);
                        OptionBehaviourSetSizeAndPosition(optionBehaviour, option, baseGameSetting.Type);
                    }
                    else
                    {
                        AdoptRow(optionBehaviour, __instance, index);
                        optionBehaviour.transform.localPosition = new(posX, num, posZ);
                    }

                    if (!ModGameOptionsMenu.OptionList.ContainsValue(index) && option.Name == "Preset")
                        GameSettingMenuPatch.PresetBehaviour = (NumberOption)optionBehaviour;

                    optionBehaviour.transform.localPosition = new(0.952f, num, -2f);
                    optionBehaviour.SetClickMask(__instance.ButtonClickMask);
                    // SetUpFromData (native) re-instances the masked materials of every renderer/TMP in the row on
                    // each call — this was the steady +380〜905 Material/(open+close) leak (BUG-20260706-01 round8 ③).
                    // Only fresh rows need it (mask + data binding); reused rows get the same managed value refresh
                    // that preset switches already rely on (RefreshSettingValues path), which touches no materials.
                    if (freshRow) optionBehaviour.SetUpFromData(baseGameSetting, 20);
                    optionBehaviour.gameObject.SetActive(enabledOrNotCollapsed);
                    optionBehaviour.OnValueChanged = new Action<OptionBehaviour>(__instance.ValueChanged);

                    EverBuiltRows.Add(index);
                    ModGameOptionsMenu.OptionList[optionBehaviour] = index;
                    ModGameOptionsMenu.BehaviourList[index] = optionBehaviour;
                    if (!freshRow) ReassertRowValue(optionBehaviour, option);

                    __instance.Children.Add(optionBehaviour);
                    option.OptionBehaviour = optionBehaviour;

                    if (enabledOrNotCollapsed) num -= 0.45f;
                }
                catch (Exception e) { Utils.ThrowException(e); }

                // 100 件刻みだと建設フレームが 1 フレーム 60-119ms 級の停止になる (50 件でもまだ 50-70ms)。
                // 30 件刻みで HITCH 閾値 (50ms) を下回らせる (総ビルド時間はフレーム数が増えても体感差なし)。
                if (index % 30 == 0) yield return null;
            }

            // Bail if a newer build coroutine was started on this instance while we were running
            if (!BuildGenerations.TryGetValue(__instance, out int latestGen) || latestGen != gen)
            {
                BuildCoroutines.Remove(__instance);
                yield break;
            }

            __instance.ControllerSelectable.Clear();

            foreach (UiElement x in __instance.scrollBar.GetComponentsInChildren<UiElement>())
                __instance.ControllerSelectable.Add(x);

            Canvas.ForceUpdateCanvases();
            // Cached toggle rows no longer pass through SetUpFromData→Initialize on reopen, so re-assert the
            // theme colors here (a game-mode change while the menu was closed would otherwise leave them stale).
            RefreshCheckMarkColors();
            BuildCoroutines.Remove(__instance);
        }

        float CalculateScrollBarYBoundsMax()
        {
            var num = 2.0f;

            for (var index = 0; index < OptionItem.AllOptions.Count; index++)
            {
                OptionItem option = OptionItem.AllOptions[index];
                if (!OptionSearch.IsInView(option, index, modTab)) continue;

                bool enabledOrNotCollapsed = RowVisibleInView(option);

                if (option is TextOptionItem)
                    num -= 0.63f;
                else if (enabledOrNotCollapsed)
                {
                    if (option.IsHeader) num -= 0.18f;
                    num -= 0.45f;
                }
            }

            return -num - 1.65f;
        }
    }

    // Whether a row that belongs in the current view is actually drawn. In a search result list the
    // ancestor and collapse gates do not apply: the list is exactly what the search admitted, so a hit
    // under a disabled role — or inside a collapsed section, whose header is not drawn here — still
    // shows. The same predicate has to decide the layout height, or the scrollbar range desyncs.
    private static bool RowVisibleInView(OptionItem option)
    {
        return OptionSearch.Active
            ? !option.IsCurrentlyHidden(checkCollapsedSection: false)
            : !option.IsCurrentlyHidden() && AllParentsEnabledAndVisible(option.Parent);
    }

    // A search result list is drawn from options that live in other tabs, and their rows were built
    // under those tabs' containers. Whichever menu draws a row takes it over; a later build of the
    // tab the option really belongs to takes it back the same way.
    private static void AdoptRow(OptionBehaviour row, GameOptionsMenu menu, int index)
    {
        if (!row || !menu || !menu.settingsContainer || row.transform.parent == menu.settingsContainer) return;

        OptionSearch.RememberBorrowedRow(index, row.transform.parent);
        row.transform.SetParent(menu.settingsContainer, false);
        row.SetClickMask(menu.ButtonClickMask); // the click mask is per tab, so it has to follow the row
    }

    // The build loop only touches the rows it draws. Rows this menu drew for an earlier view (its own
    // options before a search, or a previous search's results) have to be switched off here, or they
    // stay on screen at their old positions underneath the new list.
    private static void HideRowsNotInView(GameOptionsMenu menu, TabGroup modTab)
    {
        if (!menu || !menu.settingsContainer) return;

        foreach (var kv in ModGameOptionsMenu.BehaviourList)
        {
            OptionBehaviour ob = kv.Value;
            if (ob && ob.transform.parent == menu.settingsContainer && !OptionSearch.IsInView(kv.Key, modTab))
                ob.gameObject.SetActive(false);
        }

        foreach (var kv in ModGameOptionsMenu.CategoryHeaderList)
        {
            CategoryHeaderMasked chm = kv.Value;
            if (chm && chm.transform.parent == menu.settingsContainer && !OptionSearch.IsInView(kv.Key, modTab))
                chm.gameObject.SetActive(false);
        }
    }

    public static bool AllParentsEnabledAndVisible(OptionItem o, bool checkCollapsedSection = true)
    {
        while (true)
        {
            if (o == null) return true;
            if (o.IsCurrentlyHidden(forLobbyView: false, checkCollapsedSection: checkCollapsedSection) || !o.GetBool()) return false;
            o = o.Parent;
        }
    }

    public static void OptionBehaviourSetSizeAndPosition(OptionBehaviour optionBehaviour, OptionItem option, OptionTypes type)
    {
        Vector3 positionOffset = new(0f, 0f, 0f);
        Vector3 scaleOffset = new(0f, 0f, 0f);
        Color color = new(0.35f, 0.35f, 0.35f);
        var sizeDeltaX = 5.7f;

        if (option.Parent?.Parent?.Parent != null)
        {
            scaleOffset = new(-0.18f, 0, 0);
            positionOffset = new(0.3f, 0f, 0f);
            color = new(0.35f, 0f, 0f);
            sizeDeltaX = 5.1f;
        }
        else if (option.Parent?.Parent != null)
        {
            scaleOffset = new(-0.12f, 0, 0);
            positionOffset = new(0.2f, 0f, 0f);
            color = new(0.35f, 0.35f, 0f);
            sizeDeltaX = 5.3f;
        }
        else if (option.Parent != null)
        {
            scaleOffset = new(-0.05f, 0, 0);
            positionOffset = new(0.1f, 0f, 0f);
            color = new(0f, 0f, 0.35f);
            sizeDeltaX = 5.5f;
        }

        Transform labelBackground = optionBehaviour.transform.FindChild("LabelBackground");
        labelBackground.GetComponent<SpriteRenderer>().color = color;
        labelBackground.localScale += new Vector3(0.9f, -0.2f, 0f) + scaleOffset;
        labelBackground.localPosition += new Vector3(-0.4f, 0f, 0f) + positionOffset;

        Transform titleText = optionBehaviour.transform.FindChild("Title Text");
        titleText.localPosition += new Vector3(-0.4f, 0f, 0f) + positionOffset;
        titleText.GetComponent<RectTransform>().sizeDelta = new(sizeDeltaX, 0.37f);
        var textMeshPro = titleText.GetComponent<TextMeshPro>();
        textMeshPro.alignment = TextAlignmentOptions.MidlineLeft;
        textMeshPro.fontStyle = FontStyles.Bold;
        textMeshPro.outlineWidth = 0.17f;

        switch (type)
        {
            case OptionTypes.Checkbox:
            {
                optionBehaviour.transform.FindChild("Toggle").localPosition = new(1.46f, -0.042f);
                break;
            }
            case OptionTypes.String:
            {
                Transform plusButton = optionBehaviour.transform.FindChild("PlusButton");
                Transform minusButton = optionBehaviour.transform.FindChild("MinusButton");
                plusButton.localPosition += new Vector3(option.IsText ? 500f : 1.7f, option.IsText ? 500f : 0f, option.IsText ? 500f : 0f);
                minusButton.localPosition += new Vector3(option.IsText ? 500f : 0.9f, option.IsText ? 500f : 0f, option.IsText ? 500f : 0f);
                Transform valueTMP = optionBehaviour.transform.FindChild("Value_TMP (1)");
                valueTMP.localPosition += new Vector3(1.3f, 0f, 0f);
                valueTMP.GetComponent<RectTransform>().sizeDelta = new(2.3f, 0.4f);
                goto default;
            }
            case OptionTypes.Float:
            case OptionTypes.Int:
            {
                optionBehaviour.transform.FindChild("PlusButton").localPosition += new Vector3(option.IsText ? 500f : 1.7f, option.IsText ? 500f : 0f, option.IsText ? 500f : 0f);
                optionBehaviour.transform.FindChild("MinusButton").localPosition += new Vector3(option.IsText ? 500f : 0.9f, option.IsText ? 500f : 0f, option.IsText ? 500f : 0f);
                optionBehaviour.transform.FindChild("Value_TMP").localPosition += new Vector3(1.3f, 0f, 0f);
                goto default;
            }
            default:
            {
                Transform valueBox = optionBehaviour.transform.FindChild("ValueBox");
                valueBox.localScale += new Vector3(0.2f, 0f, 0f);
                valueBox.localPosition += new Vector3(1.3f, 0f, 0f);
                break;
            }
        }
    }
    [HarmonyPatch(nameof(GameOptionsMenu.Update))]
    [HarmonyPrefix]
    public static void UpdatePostfix(GameOptionsMenu __instance)
    {
        // Disable scroll options when chat open
        if (!HudManager.InstanceExists || !__instance.gameObject.activeSelf) return;

        __instance.scrollBar.enabled = !HudManager.Instance.Chat.IsOpenOrOpening;
    }
    [HarmonyPatch(nameof(GameOptionsMenu.ValueChanged))]
    [HarmonyPrefix]
    private static bool ValueChangedPrefix(GameOptionsMenu __instance, OptionBehaviour option)
    {
        if (ModGameOptionsMenu.TabIndex < 3) return true;

        if (ModGameOptionsMenu.OptionList.TryGetValue(option, out int index))
        {
            OptionItem item = OptionItem.AllOptions[index];
            if (item != null && item.Children.Count > 0) ReCreateSettings(__instance);
        }

        return false;
    }

    internal static int ReCreateGeneration;
    internal static Coroutine ReCreateAllCoroutine;

    public static void ReCreateAllSettings()
    {
        ReCreateGeneration++;
        int generation = ReCreateGeneration;

        if (ReCreateAllCoroutine != null)
        {
            Main.Instance.StopCoroutine(ReCreateAllCoroutine);
            ReCreateAllCoroutine = null;
        }

        ReCreateAllCoroutine = Main.Instance.StartCoroutine(CoReCreateAll(generation));
        return;

        IEnumerator CoReCreateAll(int gen)
        {
            var tabs = GameSettingMenuPatch.GetModSettingsTabs();
            foreach (var (tab, menu) in tabs)
            {
                if (gen != ReCreateGeneration) yield break;
                if (!menu) { yield return null; continue; }

                while (BuildCoroutines.TryGetValue(menu, out Coroutine existing) && existing != null)
                {
                    if (gen != ReCreateGeneration) yield break;
                    yield return null;
                }

                if (gen != ReCreateGeneration) yield break;

                ReCreateSettings(menu, tab);
                yield return null;
            }
            ReCreateAllCoroutine = null;
        }
    }

    public static void ReCreateSettings(GameOptionsMenu __instance, TabGroup? modTabOverride = null)
    {
        if (!modTabOverride.HasValue && ModGameOptionsMenu.TabIndex < 3) return;
        if (!__instance) return;

        TabGroup modTab = modTabOverride ?? (TabGroup)(ModGameOptionsMenu.TabIndex - 3);

        // Only the tab on screen redraws the search result list. A background tab redrawing it would pull
        // the shared rows into its own container and leave the visible tab empty (preset switches and
        // game-mode changes call this for every tab at once).
        if (OptionSearch.Active && modTab != (TabGroup)(ModGameOptionsMenu.TabIndex - 3)) return;

        // Mirror of the CreateSettings dispatch: ValueChanged/collapse re-layouts route through
        // here too, so the new view must own reflow for its tabs or build/reflow desync.
        if (NewRoleMenuState.Active && !OptionSearch.Active && modTab is >= TabGroup.ImpostorRoles and <= TabGroup.OtherRoles)
        {
            NewRoleMenuView.Reflow(__instance, modTab);
            return;
        }

        if (NewRoleMenuState.Active && !OptionSearch.Active && NewRoleMenuView.IsConsolidatedSettingsTab(modTab))
        {
            NewRoleMenuView.ReflowSettings(__instance);
            return;
        }

        var num = 2.0f;

        long reflowT0 = MenuPerfProbe.Stamp();

        HideRowsNotInView(__instance, modTab);

        for (var index = 0; index < OptionItem.AllOptions.Count; index++)
        {
            OptionItem option = OptionItem.AllOptions[index];
            if (!OptionSearch.IsInView(option, index, modTab)) continue;

            bool enabledOrNotCollapsed = RowVisibleInView(option);
            bool enabled = !option.IsCurrentlyHidden(checkCollapsedSection: false) && AllParentsEnabledAndVisible(option.Parent, checkCollapsedSection: false);

            if (ModGameOptionsMenu.CategoryHeaderList.TryGetValue(index, out CategoryHeaderMasked categoryHeaderMasked) && categoryHeaderMasked)
            {
                categoryHeaderMasked.transform.localPosition = new(-0.903f, num, -2f);
                categoryHeaderMasked.gameObject.SetActive(enabled);
                if (enabledOrNotCollapsed) num -= 0.63f;
            }
            else if (option.IsHeader && enabledOrNotCollapsed) num -= 0.18f;

            if (ModGameOptionsMenu.BehaviourList.TryGetValue(index, out OptionBehaviour optionBehaviour) && optionBehaviour)
            {
                AdoptRow(optionBehaviour, __instance, index);
                optionBehaviour.transform.localPosition = new(0.952f, num, -2f);
                optionBehaviour.gameObject.SetActive(enabledOrNotCollapsed);
                if (enabledOrNotCollapsed) num -= 0.45f;
            }
        }

        if (!__instance.scrollBar)
        {
            MenuPerfProbe.Reflow(reflowT0);
            return;
        }

        __instance.ControllerSelectable.Clear();

        foreach (UiElement x in __instance.scrollBar.GetComponentsInChildren<UiElement>())
            __instance.ControllerSelectable.Add(x);

        __instance.scrollBar.SetYBoundsMax(-num - 1.65f);
        MenuPerfProbe.Reflow(reflowT0);
    }
    public static BaseGameSetting GetSetting(OptionItem item)
    {
        if (ModGameOptionsMenu.BaseGameSettingCache.TryGetValue(item, out BaseGameSetting cached)) return cached;

        BaseGameSetting baseGameSetting;

        switch (item)
        {
            case BooleanOptionItem:
                var checkboxGameSetting = ScriptableObject.CreateInstance<CheckboxGameSetting>();
                checkboxGameSetting.Type = OptionTypes.Checkbox;
                baseGameSetting = checkboxGameSetting;
                break;
            case IntegerOptionItem integerOptionItem:
                var intGameSetting = ScriptableObject.CreateInstance<IntGameSetting>();
                intGameSetting.Type = OptionTypes.Int;
                intGameSetting.Value = integerOptionItem.GetInt();
                intGameSetting.Increment = integerOptionItem.Rule.Step;
                intGameSetting.ValidRange = new(integerOptionItem.Rule.MinValue, integerOptionItem.Rule.MaxValue);
                intGameSetting.ZeroIsInfinity = false;
                intGameSetting.SuffixType = NumberSuffixes.Multiplier;
                intGameSetting.FormatString = string.Empty;
                baseGameSetting = intGameSetting;
                break;
            case FloatOptionItem floatOptionItem:
                var floatGameSetting = ScriptableObject.CreateInstance<FloatGameSetting>();
                floatGameSetting.Type = OptionTypes.Float;
                floatGameSetting.Value = floatOptionItem.GetFloat();
                floatGameSetting.Increment = floatOptionItem.Rule.Step;
                floatGameSetting.ValidRange = new(floatOptionItem.Rule.MinValue, floatOptionItem.Rule.MaxValue);
                floatGameSetting.ZeroIsInfinity = false;
                floatGameSetting.SuffixType = NumberSuffixes.Multiplier;
                floatGameSetting.FormatString = string.Empty;
                baseGameSetting = floatGameSetting;
                break;
            case StringOptionItem stringOptionItem:
                var stringGameSetting = ScriptableObject.CreateInstance<StringGameSetting>();
                stringGameSetting.Type = OptionTypes.String;
                stringGameSetting.Values = new StringNames[stringOptionItem.Selections.Count];
                stringGameSetting.Index = stringOptionItem.GetInt();
                baseGameSetting = stringGameSetting;
                break;
            case PresetOptionItem presetOptionItem:
                var presetIntGameSetting = ScriptableObject.CreateInstance<IntGameSetting>();
                presetIntGameSetting.Type = OptionTypes.Int;
                presetIntGameSetting.Value = presetOptionItem.GetInt();
                presetIntGameSetting.Increment = presetOptionItem.Rule.Step;
                presetIntGameSetting.ValidRange = new(presetOptionItem.Rule.MinValue, presetOptionItem.Rule.MaxValue);
                presetIntGameSetting.ZeroIsInfinity = false;
                presetIntGameSetting.SuffixType = NumberSuffixes.Multiplier;
                presetIntGameSetting.FormatString = string.Empty;
                baseGameSetting = presetIntGameSetting;
                break;
            default:
                baseGameSetting = null;
                break;
        }

        if (baseGameSetting)
        {
            baseGameSetting.Title = StringNames.Accept;
            ModGameOptionsMenu.BaseGameSettingCache[item] = baseGameSetting;
        }
        return baseGameSetting;
    }

    public static void ReloadUI()
    {
        if (!GameSettingMenu.Instance) return;

        foreach (var (tab, menu) in GameSettingMenuPatch.GetModSettingsTabs())
            ReCreateSettings(menu, tab);

        RefreshSettingValues();

        // The preset number label lives in SetupExtendedUI and used to be rebuilt by the old Close+reInstantiate flow.
        // Since ReloadUI no longer rebuilds the menu, refresh the label here so the displayed preset matches CurrentPreset.
        if (GameSettingMenuPatch.PresetValueText)
            GameSettingMenuPatch.PresetValueText.SetText(Translator.GetString($"Preset_{OptionItem.CurrentPreset + 1}"));

        // Game-mode switches and preset switches (presets carry a game mode) can both change CurrentGameMode,
        // so every ReloadUI must also re-assert the mode-dependent chrome: tab button visibility, checkmark
        // theme colors and the mode label — these used to refresh only on menu reopen.
        RefreshTabButtons();
        RefreshCheckMarkColors();
        if (GameSettingMenuPatch.GameModeLabelText)
            GameSettingMenuPatch.GameModeLabelText.SetText($"<size=50%>{Translator.GetString($"Mode{Options.CurrentGameMode}")}</size>\n");
    }

    public static void RefreshSettingValues()
    {
        if (ModGameOptionsMenu.TabIndex < 3) return;
        var activeModTab = (TabGroup)(ModGameOptionsMenu.TabIndex - 3);
        if (!GameSettingMenuPatch.GetModSettingsTabs().TryGetValue(activeModTab, out GameOptionsMenu activeTab) || !activeTab) return;

        foreach (var kv in ModGameOptionsMenu.OptionList)
        {
            OptionBehaviour ob = kv.Key;
            if (!ob) continue;
            OptionItem item = OptionItem.AllOptions[kv.Value];
            if (item == null) continue;
            // This loop spans every tab, so a search view has to be respected here as well — otherwise a
            // preset / game-mode / streamer-mode refresh switches the whole tab back on underneath the
            // result list (ReloadUI runs this right after the rebuild that filtered it).
            // (Outside a search this must stay tab-blind: rows of the other tabs are refreshed here too,
            // and their tab is shown again without a rebuild.)
            bool inView = !OptionSearch.Active || OptionSearch.IsInView(item, kv.Value, activeModTab);
            ob.gameObject.SetActive(inView && RowVisibleInView(item));

            // Re-apply the displayed value from the option's current value. Rows are reused across preset
            // switches without a rebuild, so without this the checkmarks / value texts keep showing the
            // previous preset's values.
            ReassertRowValue(ob, item);
        }

        // Do NOT touch activeTab.scrollBar here: ReloadUI already ran ReCreateSettings on every tab, which sets
        // each scrollbar's bound from its real content height. A fixed bound here would clamp the active tab's
        // scroll to nothing (scrollbar disappears) — that was the "System Settings can't scroll" bug.
    }

    // Managed-only display refresh for a reused row. Shared by RefreshSettingValues (preset switches) and the
    // cached-row path in CreateSettings (menu reopen) — must never touch native SetUpFromData/materials.
    // Values are written directly (no UpdateValue) so no change notifications fire and nothing is synced from here.
    public static void ReassertRowValue(OptionBehaviour ob, OptionItem item)
    {
        switch (item)
        {
            case BooleanOptionItem when ob is ToggleOption toggleOption:
                if (toggleOption.CheckMark) toggleOption.CheckMark.enabled = item.GetBool();
                break;
            case StringOptionItem stringOptionItem when ob is StringOption stringOption:
                stringOption.Value = stringOption.oldValue = stringOptionItem.GetInt();
                string selection = stringOptionItem.Selections[stringOptionItem.Rule.GetValueByIndex(stringOption.Value)];
                if (!stringOptionItem.noTranslation) selection = Translator.GetString(selection);
                stringOption.ValueText.SetText(selection);
                // Restore the styled title (role color band / pet indicator) — reused rows can end up
                // showing the plain unstyled name after preset switches (upstream #637 NameCache fix).
                if (!StringOptionPatch.NameCache.TryGetValue(stringOption, out string cachedName))
                    stringOption.Initialize();
                else
                    stringOption.TitleText.SetText(cachedName);
                break;
            case FloatOptionItem or IntegerOptionItem or PresetOptionItem when ob is NumberOption numberOption:
                numberOption.Value = item.GetFloat();
                break;
        }
    }

    public static void RefreshTabButtons()
    {
        foreach (var (tab, button) in GameSettingMenuPatch.GetModSettingsButtons())
            if (button) button.gameObject.SetActive(tab.IsRelevant());
    }

    public static void RefreshCheckMarkColors()
    {
        foreach (var kvp in ModGameOptionsMenu.OptionList)
        {
            if (kvp.Key is ToggleOption toggleOption && toggleOption)
            {
                Color color = ModGameOptionsMenu.GetCurrentThemeColor(OptionItem.AllOptions[kvp.Value].Tab);
                toggleOption.CheckMark.color = color;
                toggleOption.CheckMark.transform.parent.FindChild("ActiveSprite").GetComponent<SpriteRenderer>().color = color;
            }
        }
    }
}

[HarmonyPatch(typeof(ToggleOption))]
public static class ToggleOptionPatch
{
    [HarmonyPatch(nameof(ToggleOption.Initialize))]
    [HarmonyPrefix]
    private static bool InitializePrefix(ToggleOption __instance)
    {
        if (ModGameOptionsMenu.OptionList.TryGetValue(__instance, out int index))
        {
            OptionItem item = OptionItem.AllOptions[index];

            CustomGameMode gm = Options.CurrentGameMode;
            Color32 color = gm == CustomGameMode.Standard ? (TabGroup)(ModGameOptionsMenu.TabIndex - 3) switch
            {
                TabGroup.ImpostorRoles => new Color32(255, 25, 25, 255),
                TabGroup.CrewmateRoles => new Color32(140, 255, 255, 255),
                TabGroup.NeutralRoles => new Color32(255, 171, 27, 255),
                TabGroup.CovenRoles => new Color32(123, 63, 187, 255),
                _ => new Color32(0, 165, 255, 255)
            } : Main.GameModeColors.TryGetValue(gm, out var c) ? c : new Color32(0, 165, 255, 255);
            __instance.CheckMark.sprite = Utils.LoadSprite("EndKnot.Resources.Images.Checkmark.png", 100f);
            __instance.CheckMark.color = color;
            var renderer = __instance.CheckMark.transform.parent.FindChild("ActiveSprite").GetComponent<SpriteRenderer>();
            renderer.sprite = Utils.LoadSprite("EndKnot.Resources.Images.CheckMarkBox.png", 100f);
            renderer.color = color;
            
            __instance.TitleText.SetText(item.GetName());
            __instance.CheckMark.enabled = item.GetBool();
            item.OptionBehaviour = __instance;
            return false;
        }

        return true;
    }

    // For some reason, ToggleOption.UpdateValue isn't called for Steam users
    [HarmonyPatch(nameof(ToggleOption.Toggle))]
    [HarmonyPrefix]
    private static bool TogglePrefix(ToggleOption __instance)
    {
        if (ModGameOptionsMenu.OptionList.TryGetValue(__instance, out int index))
        {
            __instance.CheckMark.enabled = !__instance.CheckMark.enabled;
            OptionItem item = OptionItem.AllOptions[index];
            item.SetValue(__instance.GetBool() ? 1 : 0, doSync: false);
            __instance.OnValueChanged?.Invoke(__instance);
            NotificationPopperPatch.AddSettingsChangeMessage(item, true);
            return false;
        }

        return true;
    }
}

[HarmonyPatch(typeof(NumberOption))]
public static class NumberOptionPatch
{
    private static bool IsVanillaServer => GameStates.CurrentServerType == GameStates.ServerType.Vanilla;

    private static int IncrementMultiplier
    {
        get
        {
            if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) return 5;
            if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) return 10;
            return 1;
        }
    }

    [HarmonyPatch(nameof(NumberOption.Initialize))]
    [HarmonyPrefix]
    private static bool InitializePrefix(NumberOption __instance)
    {
        switch (__instance.Title)
        {
            case StringNames.GameVotingTime:
                __instance.ValidRange = new(0, 600);
                __instance.Value = (float)Math.Round(__instance.Value, 2);
                break;
            case StringNames.GameShortTasks:
            case StringNames.GameLongTasks:
            case StringNames.GameCommonTasks:
                __instance.ValidRange = new(0, 90);
                __instance.Value = (float)Math.Round(__instance.Value, 2);
                break;
            case StringNames.GameKillCooldown:
                __instance.ValidRange = new(0, 180);
                __instance.Increment = 0.5f;
                __instance.Value = (float)Math.Round(__instance.Value, 2);
                break;
            case StringNames.GamePlayerSpeed:
                if (IsVanillaServer)
                    __instance.ValidRange = new(Main.MinSpeed, 3f);
                
                __instance.Increment = 0.05f;
                __instance.Value = Mathf.Clamp((float)Math.Round(__instance.Value, 2), __instance.ValidRange.min, __instance.ValidRange.max);
                break;
            case StringNames.GameCrewLight:
            case StringNames.GameImpostorLight:
                __instance.Increment = 0.05f;
                __instance.Value = (float)Math.Round(__instance.Value, 2);
                break;
            case StringNames.GameNumImpostors:
                __instance.ValidRange = GameStates.CurrentServerType != GameStates.ServerType.Vanilla 
                    ? new(0, Crowded.MaxImpostors) : new(1, 3);
                __instance.Value = Mathf.Clamp((float)Math.Round(__instance.Value, 2), __instance.ValidRange.min, __instance.ValidRange.max);
                if (DebugModeManager.AmDebugger) __instance.ValidRange.min = 0;
                break;
            case StringNames.CapacityLabel:
                __instance.ValidRange = IsVanillaServer ? new(4, 15) : new(4, 127);
                __instance.Value = Mathf.Clamp(__instance.Value, __instance.ValidRange.min, __instance.ValidRange.max);
                break;
        }

        if (ModGameOptionsMenu.OptionList.TryGetValue(__instance, out int index))
        {
            OptionItem item = OptionItem.AllOptions[index];
            __instance.TitleText.SetText(item.GetName());
            item.OptionBehaviour = __instance;
            return false;
        }

        return true;
    }

    [HarmonyPatch(nameof(NumberOption.UpdateValue))]
    [HarmonyPrefix]
    private static bool UpdateValuePrefix(NumberOption __instance)
    {
        if (ModGameOptionsMenu.OptionList.TryGetValue(__instance, out int index))
        {
            OptionItem item = OptionItem.AllOptions[index];

            switch (item)
            {
                case IntegerOptionItem integerOptionItem:
                    integerOptionItem.SetValue(integerOptionItem.Rule.GetNearestIndex(__instance.GetInt()), doSync: false);
                    break;
                case FloatOptionItem floatOptionItem:
                    floatOptionItem.SetValue(floatOptionItem.Rule.GetNearestIndex(__instance.GetFloat()), doSync: false);
                    break;
                case PresetOptionItem presetOptionItem:
                    presetOptionItem.SetValue(presetOptionItem.Rule.GetNearestIndex(__instance.GetInt()));
                    GameOptionsMenuPatch.ReloadUI();
                    break;
            }

            NotificationPopperPatch.AddSettingsChangeMessage(item, true);
            return false;
        }

        return true;
    }

    [HarmonyPatch(nameof(NumberOption.FixedUpdate))]
    [HarmonyPrefix]
    private static bool FixedUpdatePrefix(NumberOption __instance)
    {
        if (ModGameOptionsMenu.OptionList.TryGetValue(__instance, out int index))
        {
            long t0 = MenuPerfProbe.Stamp();

            // Nothing disables these buttons between asserts (returning false skips the vanilla
            // FixedUpdate for mod rows entirely), so re-asserting on every physics tick was pure
            // per-row IL2CPP overhead — at hundreds of active rows, a real slice of the menu's frame
            // time. A staggered periodic assert keeps the safety net at 1/16 the cost.
            if (((Time.frameCount + index) & 15) == 0)
            {
                __instance.MinusBtn.SetInteractable(true);
                __instance.PlusBtn.SetInteractable(true);
            }

            if (!Mathf.Approximately(__instance.oldValue, __instance.Value))
            {
                __instance.oldValue = __instance.Value;
                __instance.ValueText.SetText(GetValueString(__instance, __instance.Value, OptionItem.AllOptions[index]));

                // A value change is the one moment vanilla can have touched the buttons behind our
                // back: a +/- click that lands on a range boundary runs the vanilla Increase/Decrease
                // (see IncreaseFinalizer), whose AdjustButtonsActiveState disables a button. Re-assert
                // unconditionally HERE so the periodic assert's worst-case lag never leaves a button
                // dead after a boundary click.
                __instance.MinusBtn.SetInteractable(true);
                __instance.PlusBtn.SetInteractable(true);
            }

            MenuPerfProbe.RowFixedUpdate(t0);
            return false;
        }

        // 投票時間/キルクールダウン等の他バニラ数値は InitializePrefix で広げた ValidRange がそのまま効くが、
        // タスク数3種だけはバニラが FixedUpdate ごとに自前の狭いタスク範囲へ ValidRange を再上書きし、
        // AdjustButtonsActiveState が境界で +/- ボタンをグレーアウトさせてしまう。mod オプションと同じく
        // 範囲(0,90)を毎フレーム再表明しボタンを強制有効化して、Increase/Decrease prefix に委ねる。
        switch (__instance.Title)
        {
            case StringNames.GameShortTasks:
            case StringNames.GameLongTasks:
            case StringNames.GameCommonTasks:
                __instance.ValidRange = new(0, 90);
                __instance.MinusBtn.SetInteractable(true);
                __instance.PlusBtn.SetInteractable(true);

                if (!Mathf.Approximately(__instance.oldValue, __instance.Value))
                {
                    __instance.oldValue = __instance.Value;
                    __instance.ValueText.SetText(GetValueString(__instance, __instance.Value, null));
                }

                return false;
        }

        return true;
    }

    private static string GetValueString(NumberOption __instance, float value, OptionItem item)
    {
        if (__instance.ZeroIsInfinity && Mathf.Abs(value) < 0.0001f) return "<b>∞</b>";

        return item == null ? value.ToString(__instance.FormatString) : item.GetString();
    }

    [HarmonyPatch(nameof(NumberOption.Increase))]
    [HarmonyPrefix]
    public static bool IncreasePrefix(NumberOption __instance)
    {
        if (Mathf.Approximately(__instance.Value, __instance.ValidRange.max))
        {
            __instance.Value = __instance.ValidRange.min;
            __instance.UpdateValue();
            __instance.OnValueChanged?.Invoke(__instance);
            return false;
        }

        float increment = IncrementMultiplier * __instance.Increment;

        if (__instance.Value + increment < __instance.ValidRange.max)
        {
            __instance.Value += increment;
            __instance.UpdateValue();
            __instance.OnValueChanged?.Invoke(__instance);
            return false;
        }

        return true;
    }

    [HarmonyPatch(nameof(NumberOption.Decrease))]
    [HarmonyPrefix]
    public static bool DecreasePrefix(NumberOption __instance)
    {
        if (Mathf.Approximately(__instance.Value, __instance.ValidRange.min))
        {
            __instance.Value = __instance.ValidRange.max;
            __instance.UpdateValue();
            __instance.OnValueChanged?.Invoke(__instance);
            return false;
        }

        float increment = IncrementMultiplier * __instance.Increment;

        if (__instance.Value - increment > __instance.ValidRange.min)
        {
            __instance.Value -= increment;
            __instance.UpdateValue();
            __instance.OnValueChanged?.Invoke(__instance);
            return false;
        }

        return true;
    }

    // 境界(最小/最大)に着地する一歩だけ上の prefix がバニラ Increase/Decrease に委譲する。
    // その際バニラ AdjustButtonsActiveState が、EHR が使っていないネイティブ +/- ボタンの
    // null SpriteRenderer に触れて NRE る (例: プリセットを最小まで下げた瞬間)。
    // 値変更自体は完了済みなので握りつぶすが、別原因で投げたら気付けるよう WARN だけ残す。
    [HarmonyPatch(nameof(NumberOption.Increase))]
    [HarmonyFinalizer]
    private static System.Exception IncreaseFinalizer(System.Exception __exception) => SwallowButtonStateNre(__exception, "Increase");

    [HarmonyPatch(nameof(NumberOption.Decrease))]
    [HarmonyFinalizer]
    private static System.Exception DecreaseFinalizer(System.Exception __exception) => SwallowButtonStateNre(__exception, "Decrease");

    private static System.Exception SwallowButtonStateNre(System.Exception ex, string method)
    {
        if (ex != null) Logger.Warn($"NumberOption.{method} threw (swallowed): {ex.Message}", "NumberOptionPatch");
        return null;
    }
}

[HarmonyPatch(typeof(StringOption))]
public static class StringOptionPatch
{
    private static long HelpShowEndTS;

    // Final styled title (role color band / pet indicator / size wrap) per row. Rows are reused across
    // preset switches and menu reopens without re-running Initialize, so anything that resets TitleText
    // in between leaves the plain unstyled name on screen. RefreshSettingValues restores from this cache
    // (upstream #637 "Use NameCache to update name of StringOption").
    internal static readonly Dictionary<StringOption, string> NameCache = new();

    private static bool IsVanillaServer => GameStates.CurrentServerType == GameStates.ServerType.Vanilla;

    private static void ClampOfficialStringOption(StringOption option)
    {
        if (!IsVanillaServer) return;

        switch (option.Title)
        {
            case StringNames.GameKillDistance:
                option.Value = Mathf.Clamp(option.Value, 0, Math.Min(2, option.Values.Length - 1));
                break;
        }
    }

    [HarmonyPatch(nameof(StringOption.Initialize))]
    [HarmonyPrefix]
    private static bool InitializePrefix(StringOption __instance)
    {
        if (__instance.name.StartsWith(nameof(OnlinePresetsManager))) return true;
        ClampOfficialStringOption(__instance);
        
        if (ModGameOptionsMenu.OptionList.TryGetValue(__instance, out int index))
        {
            OptionItem item = OptionItem.AllOptions[index];
            string name = item.GetName();
            item.OptionBehaviour = __instance;
            string name1 = name;

            if (Main.CustomRoleValues.FindFirst(x => Translator.GetString($"{x}") == name1.RemoveHtmlTags(), out CustomRoles role))
            {
                if (role.ToString().Contains("GuardianAngel")) role = CustomRoles.GA;

                name = name.RemoveHtmlTags();

                switch (Options.UsePets.GetBool())
                {
                    case false when role.OnlySpawnsWithPets():
                        name += Translator.GetString("RequiresPetIndicator");
                        break;
                    case true when role.PetActivatedAbility():
                        name += Translator.GetString("SupportsPetIndicator");
                        break;
                }

                if (role.IsExperimental()) name += $"<size=2>{Translator.GetString("ExperimentalRoleIndicator")}</size>";
                if (role.IsGhostRole()) name += GetGhostRoleTeam(role);
                if (role.IsDevFavoriteRole()) name += "  <size=2><#00ffff>★</color></size>";

                __instance.TitleText.fontWeight = FontWeight.Black;
                __instance.TitleText.outlineColor = new(255, 255, 255, 255);
                __instance.TitleText.outlineWidth = 0.04f;
                __instance.LabelBackground.color = Utils.GetRoleColor(role);
                __instance.TitleText.color = Color.white;
                name = $"<size=3.5>{name}</size>";
                SetupHelpIcon(role, __instance);
            }
            // UpdateValuePrefix と同じ汚染ガード (BUG-20260810-02): 役職名検出が失敗しても
            // 以前の styled 名を素の name で上書きしない。
            else if (NameCache.TryGetValue(__instance, out string prevStyled) && prevStyled.StartsWith("<size=3.5>"))
            {
                Logger.Warn($"role-name detection missed on Initialize: raw='{name}' prev='{prevStyled}'", "BUG-20260810-02");
                name = prevStyled;
            }

            __instance.TitleText.SetText(name);
            NameCache[__instance] = name;
            return false;
        }

        return true;
    }

    private static void SetupHelpIcon(CustomRoles role, StringOption option)
    {
        Transform template = option.MinusBtn.transform;

        // Reuse a cached "?" icon; only instantiate a fresh one when none exists or it was destroyed (scene reload /
        // rehost). Instantiating unconditionally stacked another icon onto the reused option row on every rebuild.
        if (!(ModGameOptionsMenu.HelpIconList.TryGetValue(role, out Transform icon) && icon))
        {
            icon = ModGameOptionsMenu.Track(Object.Instantiate(template, template.parent, true));
            icon.name = $"{role}HelpIcon";
            ModGameOptionsMenu.HelpIconList[role] = icon;
        }

        var text = icon.GetComponentInChildren<TextMeshPro>();
        text.SetText("?");
        text.color = Color.white;
        icon.FindChild("ButtonSprite").GetComponent<SpriteRenderer>().color = Color.black;
        var gameOptionButton = icon.GetComponent<GameOptionButton>();
        gameOptionButton.OnClick = new();

        gameOptionButton.OnClick.AddListener((Action)(() =>
        {
            if (ModGameOptionsMenu.OptionList.TryGetValue(option, out int index))
            {
                OptionItem item = OptionItem.AllOptions[index];
                string name = item.GetName();

                if (Main.CustomRoleValues.FindFirst(x => Translator.GetString($"{x}") == name.RemoveHtmlTags(), out CustomRoles value))
                {
                    string roleName = value.IsVanilla() ? value + "EndKnot" : value.ToString();
                    string str = Translator.GetString($"{roleName}InfoLong").FixRoleName(value);
                    string infoLong;

                    try { infoLong = CustomHnS.AllHnSRoles.Contains(value) ? str : str[(str.IndexOf('\n') + 1)..str.Split("\n\n")[0].Length]; }
                    catch { infoLong = str; }

                    GameObject.Find("PlayerOptionsMenu(Clone)").transform.FindChild("What Is This?").gameObject.SetActive(true);
                    GameSettingMenuPatch.GMButtons.ForEach(x => { if (x) x.gameObject.SetActive(false); });

                    var info = $"{value.ToColoredString()}: {infoLong}";
                    GameSettingMenu.Instance.MenuDescriptionText.SetText(info);

                    long now = Utils.TimeStamp;
                    bool startCoRoutine = now > HelpShowEndTS;
                    HelpShowEndTS = now + 15;
                    if (startCoRoutine)
                        Main.Instance.StartCoroutine(CoRoutine());

                    IEnumerator CoRoutine()
                    {
                        while (HelpShowEndTS > Utils.TimeStamp)
                            yield return new WaitForSecondsRealtime(1f);

                        GameObject gameObject = GameObject.Find("PlayerOptionsMenu(Clone)");

                        if (gameObject)
                        {
                            Transform findChild = gameObject.transform.FindChild("What Is This?");
                            if (findChild) findChild.gameObject.SetActive(false);
                        }

                        // メニューが既に閉じている場合は再点灯しない: このコルーチンは Main.Instance 上で走るため
                        // メニュー close 後も生存し、close 時に DontDestroyOnLoad の「EHR Settings UI Cache Root」
                        // (world 原点) へ退避・消灯済みの GMButtons を無条件に SetActive(true) すると、モードボタン列が
                        // ロビーのワールド空間や結果画面に浮遊するゴーストとして残留する (次のメニュー再オープンまで回収されない)。
                        if (GameSettingMenu.Instance)
                            GameSettingMenuPatch.GMButtons.ForEach(x => { if (x) x.gameObject.SetActive(true); });
                    }
                }
            }
        }));

        gameOptionButton.interactableColor = Color.black;
        gameOptionButton.interactableHoveredColor = new Color32(0, 165, 255, 255);
        icon.localPosition = template.localPosition + new Vector3(-0.8f, 0f, 0f);
        icon.SetAsLastSibling();
    }

    public static string GetGhostRoleTeam(CustomRoles role)
    {
        IGhostRole instance = GhostRolesManager.CreateGhostRoleInstance(role);
        if (instance == null) return string.Empty;

        Team team = instance.Team;
        if ((int)team is 1 or 2 or 4 or 8) return $"    <size=2>{GetColoredShortTeamName(team)}</size>";

        Team[] teams = (int)team switch
        {
            3 => [Team.Impostor, Team.Neutral],
            5 => [Team.Impostor, Team.Crewmate],
            6 => [Team.Neutral, Team.Crewmate],
            7 => [Team.Impostor, Team.Neutral, Team.Crewmate],
            9 => [Team.Impostor, Team.Coven],
            10 => [Team.Neutral, Team.Coven],
            11 => [Team.Impostor, Team.Neutral, Team.Coven],
            12 => [Team.Crewmate, Team.Coven],
            13 => [Team.Impostor, Team.Crewmate, Team.Coven],
            14 => [Team.Neutral, Team.Crewmate, Team.Coven],
            15 => [Team.Impostor, Team.Neutral, Team.Crewmate, Team.Coven],
            _ => []
        };

        return $"    <size=2>{string.Join('/', teams.Select(GetColoredShortTeamName))}</size>";

        string GetColoredShortTeamName(Team t) => Utils.ColorString(t.GetColor(), Translator.GetString($"ShortTeamName.{t}").ToUpper());
    }

    [HarmonyPatch(nameof(StringOption.UpdateValue))]
    [HarmonyPrefix]
    private static bool UpdateValuePrefix(StringOption __instance)
    {
        if (__instance.name.StartsWith(nameof(OnlinePresetsManager))) return false;
        
        if (ModGameOptionsMenu.OptionList.TryGetValue(__instance, out int index))
        {
            OptionItem item = OptionItem.AllOptions[index];

            item.SetValue(__instance.GetInt(), doSync: false);
            string name = item.GetName();

            string name1 = name;

            if (Main.CustomRoleValues.FindFirst(x => Translator.GetString($"{x}") == name1.RemoveHtmlTags(), out CustomRoles role))
            {
                if (role.ToString().Contains("GuardianAngel")) role = CustomRoles.GA;

                name = name.RemoveHtmlTags();

                switch (Options.UsePets.GetBool())
                {
                    case true when role.PetActivatedAbility():
                        name += Translator.GetString("SupportsPetIndicator");
                        break;
                    case false when role.OnlySpawnsWithPets():
                        name += Translator.GetString("RequiresPetIndicator");
                        Prompt.Show(Translator.GetString("Promt.RequiresPets"), () => Options.UsePets.SetValue(1), () => { });
                        break;
                }

                if (role.IsExperimental()) name += $"<size=2>{Translator.GetString("ExperimentalRoleIndicator")}</size>";
                if (role.IsGhostRole()) name += GetGhostRoleTeam(role);
                if (role.IsDevFavoriteRole()) name += "  <size=2><#00ffff>★</color></size>";

                __instance.TitleText.fontWeight = FontWeight.Black;
                __instance.TitleText.outlineColor = new(255, 255, 255, 255);
                __instance.TitleText.outlineWidth = 0.04f;
                __instance.LabelBackground.color = Utils.GetRoleColor(role);
                __instance.TitleText.color = Color.white;
                name = $"<size=3.5>{name}</size>";
                NotificationPopperPatch.AddRoleSettingsChangeMessage(item, role, true);
            }
            else
            {
                NotificationPopperPatch.AddSettingsChangeMessage(item, true);

                // 役職行なのに役職名検出が失敗すると、素の name が TitleText と NameCache の両方へ入り
                // 装飾タイトル (白文字+色帯+size wrap) がメニュー開閉でも直らない形で退行する
                // (BUG-20260810-02)。以前の styled 名があるなら保持し、失敗の現場をログへ残す。
                if (NameCache.TryGetValue(__instance, out string prevStyled) && prevStyled.StartsWith("<size=3.5>"))
                {
                    Logger.Warn($"role-name detection missed on UpdateValue: raw='{name}' prev='{prevStyled}'", "BUG-20260810-02");
                    name = prevStyled;
                }
            }

            __instance.TitleText.SetText(name);
            NameCache[__instance] = name;
            return false;
        }

        return true;
    }

    [HarmonyPatch(nameof(StringOption.FixedUpdate))]
    [HarmonyPrefix]
    private static bool FixedUpdatePrefix(StringOption __instance)
    {
        // Mod rows are resolved through the managed dict FIRST: the name probe below is an IL2CPP
        // get_name + string marshal, and paying it per row per physics tick for every settings row was
        // pure overhead (mod rows can never be OnlinePresetsManager rows).
        if (ModGameOptionsMenu.OptionList.TryGetValue(__instance, out int index))
        {
            long t0 = MenuPerfProbe.Stamp();

            // Same staggered periodic assert as the NumberOption prefix (see the comment there).
            if (((Time.frameCount + index) & 15) == 0)
            {
                __instance.MinusBtn.SetInteractable(true);
                __instance.PlusBtn.SetInteractable(true);
            }

            if (__instance.oldValue != __instance.Value && OptionItem.AllOptions[index] is StringOptionItem stringOptionItem)
            {
                __instance.oldValue = __instance.Value;
                string selection = stringOptionItem.Selections[stringOptionItem.Rule.GetValueByIndex(__instance.Value)];
                if (!stringOptionItem.noTranslation) selection = Translator.GetString(selection);
                __instance.ValueText.SetText(selection);

                // Same boundary-click hole as the NumberOption prefix (see the comment there).
                __instance.MinusBtn.SetInteractable(true);
                __instance.PlusBtn.SetInteractable(true);
            }

            MenuPerfProbe.RowFixedUpdate(t0);
            return false;
        }

        if (__instance.name.StartsWith(nameof(OnlinePresetsManager)))
        {
            __instance.TitleText.SetText(__instance.name.Split(';')[1]);
            return false;
        }

        ClampOfficialStringOption(__instance);

        return true;
    }

    [HarmonyPatch(nameof(StringOption.Increase))]
    [HarmonyPrefix]
    public static bool IncreasePrefix(StringOption __instance)
    {
        if (__instance.Value == __instance.Values.Length - 1)
        {
            __instance.Value = 0;
            __instance.UpdateValue();
            __instance.OnValueChanged?.Invoke(__instance);
            return false;
        }

        return true;
    }

    [HarmonyPatch(nameof(StringOption.Decrease))]
    [HarmonyPrefix]
    public static bool DecreasePrefix(StringOption __instance)
    {
        if (__instance.Value == 0)
        {
            __instance.Value = __instance.Values.Length - 1;
            __instance.UpdateValue();
            __instance.OnValueChanged?.Invoke(__instance);
            return false;
        }

        return true;
    }
}

[HarmonyPatch(typeof(GameSettingMenu))]
public static class GameSettingMenuPatch
{
    public static readonly System.Collections.Generic.List<GameObject> GMButtons = [];

    private static readonly Vector3 ButtonPositionLeft = new(-3.9f, -0.55f, 0f);
    private static readonly Vector3 ButtonPositionRight = new(-2.4f, -0.55f, 0f);

    private static readonly Vector3 ButtonSize = new(0.45f, 0.35f, 1f);

    private static GameOptionsMenu TemplateGameOptionsMenu;
    private static PassiveButton TemplateGameSettingsButton;

    private static readonly System.Collections.Generic.Dictionary<TabGroup, PassiveButton> ModSettingsButtons = [];
    private static readonly System.Collections.Generic.Dictionary<TabGroup, GameOptionsMenu> ModSettingsTabs = [];

    public static NumberOption PresetBehaviour;
    public static long LastPresetChange;
    public static TextMeshPro PresetValueText;
    public static TextMeshPro GameModeLabelText; // mode name label on the left panel; refreshed by ReloadUI
    private static GameObject PresetSelector;
    private static GameObject PresetMinusButton;
    private static GameObject PresetPlusButton;

    public static FreeChatInputField InputField;
    public static Action SearchForOptionsAction;

    private static int NumImpsOnOpen = 1;
    private static int MinImpsOnOpen = 1;
    private static int MaxImpsOnOpen = 1;

    private static float VanillaKillCooldownOnOpen = 1;
    private static float ModdedKillCooldownOnOpen = 1;

    private static Transform TempParent;

    [HarmonyPatch(nameof(GameSettingMenu.Start))]
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    public static void StartPostfix(GameSettingMenu __instance)
    {
        try
        {
            (OptionItem MinSetting, OptionItem MaxSetting) = Options.FactionMinMaxSettings[Team.Impostor];
            MinImpsOnOpen = MinSetting.GetInt();
            MaxImpsOnOpen = MaxSetting.GetInt();
            NumImpsOnOpen = Main.NormalOptions.NumImpostors;

            VanillaKillCooldownOnOpen = Main.NormalOptions.KillCooldown;
            ModdedKillCooldownOnOpen = Options.FallBackKillCooldownValue.GetFloat();
        }
        catch (Exception e) { Utils.ThrowException(e); }

        RoleMenuTabBar.Reset();
        // Create buttons/tabs for every TabGroup and gate visibility with IsRelevant instead of
        // filtering the list — an in-menu game-mode switch can then reveal tabs that were
        // irrelevant when the menu opened (upstream parity; RefreshTabButtons flips visibility).
        TabGroup[] tabGroups = Main.TabGroupValues;

        // New menu: one compact centered top row. Mod/Task tab buttons are folded into the System
        // button (relabelled "設定"), which renders System+Mod+Task as one sectioned column.
        // Display order: role tabs first, then the consolidated 設定, then preset.
        bool active = NewRoleMenuState.Active;
        System.Collections.Generic.List<TabGroup> orderedTabs = null;
        if (active)
        {
            var shown = tabGroups.Where(t => t is not (TabGroup.GameSettings or TabGroup.TaskSettings));
            orderedTabs = shown.Where(t => t is >= TabGroup.ImpostorRoles and <= TabGroup.OtherRoles)
                .Concat(shown.Where(t => t is not (>= TabGroup.ImpostorRoles and <= TabGroup.OtherRoles)))
                .ToList();
        }

        foreach (TabGroup tab in tabGroups)
        {
            // Fold the secondary setting tabs into the consolidated System ("設定") tab.
            if (active && tab is TabGroup.GameSettings or TabGroup.TaskSettings) continue;

            bool cachedButton = ModSettingsButtons.TryGetValue(tab, out PassiveButton button) && button;
            if (!cachedButton)
                button = ModGameOptionsMenu.Track(Object.Instantiate(TemplateGameSettingsButton, __instance.GameSettingsButton.transform.parent));
            else
                button.transform.SetParent(__instance.GameSettingsButton.transform.parent, false);
            button.gameObject.SetActive(tab.IsRelevant());
            button.name = "Button_" + tab;
            var label = button.GetComponentInChildren<TextMeshPro>();
            label.DestroyTranslator();
            label.SetText(Translator.GetString(
                active
                    ? (tab == TabGroup.SystemSettings ? "TabGroup.Short.Settings" : $"TabGroup.Short.{tab}")
                    : $"TabGroup.{tab}"));
            label.color = Color.white;
            button.activeTextColor = button.inactiveTextColor = Color.white;
            button.selectedTextColor = new(0.7f, 0.7f, 0.7f);
            Color color = tab.GetTabColor();

            if (active)
            {
                // Flatten the pill backgrounds to invisible so only our chrome (dot/badge/underline) shows.
                // Must use alpha=0 (not SetActive) because SelectButton() re-activates them internally.
                var invisible = new Color(0f, 0f, 0f, 0f);
                button.inactiveSprites.GetComponent<SpriteRenderer>().color = invisible;
                button.activeSprites.GetComponent<SpriteRenderer>().color = invisible;
                button.selectedSprites.GetComponent<SpriteRenderer>().color = invisible;
            }
            else
            {
                button.inactiveSprites.GetComponent<SpriteRenderer>().color = color;
                button.activeSprites.GetComponent<SpriteRenderer>().color = color;
                button.selectedSprites.GetComponent<SpriteRenderer>().color = color;
            }

            if (active)
            {
                // New menu: one compact centered top row. Pure repositioning — ChangeTab/TabIndex unchanged.
                int idx = orderedTabs.IndexOf(tab);
                int n = orderedTabs.Count;
                float x = (-(n - 1) * NewRoleMenuLayout.TopTabStepX / 2f) + (idx * NewRoleMenuLayout.TopTabStepX);
                button.transform.localPosition = new(x, NewRoleMenuLayout.TopTabY, 0f);
                button.transform.localScale = NewRoleMenuLayout.TopTabScale;
                RoleMenuTabBar.Decorate(__instance, tab, button, orderedTabs, idx);
            }
            else
            {
                // ReSharper disable once PossibleLossOfFraction
                Vector3 offset = new(0f, (0.3f * (((int)tab + 1) / 2)), 0f);
                button.transform.localPosition = (((int)tab + 1) % 2 == 0 ? ButtonPositionLeft : ButtonPositionRight) - offset;
                button.transform.localScale = ButtonSize;
            }

            var buttonComponent = button.GetComponent<PassiveButton>();
            buttonComponent.OnClick = new();
            buttonComponent.OnClick.AddListener((Action)(() => __instance.ChangeTab((int)tab + 3, false)));

            ModSettingsButtons[tab] = button;
        }

        foreach (TabGroup tab in tabGroups)
        {
            bool cachedTab = ModSettingsTabs.TryGetValue(tab, out GameOptionsMenu setTab) && setTab;
            if (!cachedTab)
            {
                setTab = ModGameOptionsMenu.Track(Object.Instantiate(TemplateGameOptionsMenu, __instance.GameSettingsTab.transform.parent));
                setTab.name = "tab_" + tab;
            }
            else
            {
                setTab.transform.SetParent(__instance.GameSettingsTab.transform.parent, false);
            }
            setTab.gameObject.SetActive(false);

            ModSettingsTabs[tab] = setTab;
        }

        foreach (TabGroup tab in tabGroups)
        {
            if (ModSettingsButtons.TryGetValue(tab, out PassiveButton button))
                __instance.ControllerSelectable.Add(button);
        }

        // A search does not survive the menu being closed. Its result list borrowed rows from the tabs
        // it drew from, so dropping it owes every tab a rebuild that puts those rows back.
        if (OptionSearch.Clear()) GameOptionsMenuPatch.ReCreateAllSettings();

        // Isolated: an exception inside the extended UI (e.g. BUG-20260712-01's template NRE) must not
        // abort the rest of the menu build. XuiStage pinpoints the failing section — the native get_name
        // NRE (2026-07-12 12:42 実機) erases line info at the IL2CPP boundary, so a breadcrumb is the
        // only way to attribute it.
        try { SetupExtendedUI(__instance); }
        catch (Exception e)
        {
            Logger.Warn($"SetupExtendedUI failed at stage '{XuiStage}': {e.Message}", "MenuLeak");
            Utils.ThrowException(e);
        }

        if (NewRoleMenuState.Active)
        {
            // Diagnostic: dump the left-panel hierarchy (before hiding) so we can identify the dark
            // occluding sprite / native chrome by exact name + sortingOrder + world bounds.
            if (RoleMenuDiag.Enabled) RoleMenuDiag.DumpLeftPanel(__instance);

            // Declutter the left panel for the new menu: hide the game-mode grid + its label
            // (the mode buttons are parented to GameSettingsLabel). They'll be relocated to a
            // top context row in a later step. Pure View-side hide — game-mode state untouched.
            Transform gsl = __instance.GameSettingsButton.transform.parent.parent.FindChild("GameSettingsLabel");
            if (gsl) gsl.gameObject.SetActive(false);

            // Hide the leftover native "バニラの設定" (vanilla settings) button — it floats over the
            // new master list. The relocated tabs use a separate Template clone, so hiding the
            // original GameSettingsButton is safe. View-side only.
            __instance.GameSettingsButton.gameObject.SetActive(false);

            // Hide the dark left tint (PlayerOptionsMenu(Clone)/PanelSprite/LeftSideTint, RGBA 0.118,
            // wx -6.67..-3.89) — identified via DumpLeftPanel as the sprite occluding the left ~30%
            // and clipping the master role-name labels. Phase 2 renders our own pane backgrounds.
            Transform leftRoot = __instance.GameSettingsButton.transform.parent.parent;
            Transform leftTint = leftRoot.Find("PanelSprite/LeftSideTint");
            if (leftTint) leftTint.gameObject.SetActive(false);

            // Hide the preset ± UI injected by SetupExtendedUI (ModeValue clone + GamePresetsButton)
            // — they float over the new master list when the two-pane menu is active.
            __instance.GamePresetsButton.gameObject.SetActive(false);
            Transform presetUIParent = __instance.GamePresetsButton.transform.parent;
            Transform modeValueClone = presetUIParent.Find("ModeValue(Clone)");
            if (modeValueClone) modeValueClone.gameObject.SetActive(false);

            // Enable the underline for the currently active tab (TabIndex-3 maps to the TabGroup).
            if (ModGameOptionsMenu.TabIndex >= 3)
            {
                var initialTab = (TabGroup)(ModGameOptionsMenu.TabIndex - 3);
                RoleMenuTabBar.SetActiveTab(initialTab);
            }
        }
    }

    // Breadcrumb for the StartPostfix catch — which SetupExtendedUI section was running when it threw.
    private static string XuiStage = "-";

    // Thanks: Drakos for the preset button and search bar code (https://github.com/0xDrMoe/TownofHost-Enhanced/pull/1115)
    private static void SetupExtendedUI(GameSettingMenu __instance)
    {
        XuiStage = "enter";
        Transform parentLeftPanel = __instance.GamePresetsButton.transform.parent;
        bool russian = TranslationController.Instance.currentLanguage.languageID == SupportedLangs.Russian;

        // The preset selector (ModeValue clone + - / + buttons) is created once and cached, then reattached
        // on every reopen — mirroring the GMButtons / InputField caching below — so it no longer leaks per open.
        GameObject preset = null, gMinus = null, plusFab = null;
        TextMeshPro plusLabel = null;

        if (PresetSelector)
        {
            XuiStage = "preset-cached";
            preset = PresetSelector;
            preset.transform.SetParent(parentLeftPanel, false);
            preset.SetActive(true);
            gMinus = PresetMinusButton;
            plusFab = PresetPlusButton;
            // Find が null を返すと GetComponent で NRE → SetupExtendedUI ごと中断し、末尾の検索欄構築まで
            // 道連れになる (BUG-20260725-02 と同型)。しかもここはキャッシュ経路なので、一度テンプレート階層が
            // ズレると以後の全リオープンで同じ所で落ち続ける。plusLabel は font 差替にしか使わず、利用側
            // (`if (plusLabel)`) が null 許容なので、取れなければ黙って null のまま進める。
            Transform plusLabelTf = plusFab ? plusFab.transform.Find("FontPlacer/Text_TMP") : null;
            plusLabel = plusLabelTf ? plusLabelTf.GetComponent<TextMeshPro>() : null;
            if (PresetValueText) PresetValueText.SetText(Translator.GetString($"Preset_{OptionItem.CurrentPreset + 1}"));
        }
        else
        {
            // BUG-20260712-01: the vanilla "ModeValue"/"MinusButton" templates aren't always findable
            // (GameObject.Find only sees active objects) and Instantiate(null) NREs inside get_name,
            // killing the rest of the menu build. Isolate the whole preset build: on any failure, drop
            // the half-built selector and carry on without a preset UI for this open (next open retries).
            try
            {
            XuiStage = "preset-fresh";
            GameObject modeValueTemplate = GameObject.Find("ModeValue");
            GameObject minusTemplate = GameObject.Find("MinusButton");
            if (!modeValueTemplate || !minusTemplate)
                throw new InvalidOperationException($"vanilla templates missing (ModeValue={(bool)modeValueTemplate}, MinusButton={(bool)minusTemplate})");

            preset = ModGameOptionsMenu.Track(Object.Instantiate(modeValueTemplate, parentLeftPanel));

            preset.transform.localPosition = new(-1.8f, 0f, -2f);
            preset.transform.localScale = new(0.65f, 0.63f, 1f);
            var renderer = preset.GetComponentInChildren<SpriteRenderer>();
            renderer.color = Color.white;
            renderer.sprite = null;

            var presetTmp = preset.GetComponentInChildren<TextMeshPro>();
            presetTmp.DestroyTranslator();
            presetTmp.SetText(Translator.GetString($"Preset_{OptionItem.CurrentPreset + 1}"));
            PresetValueText = presetTmp; // keep a handle so ReloadUI can refresh the label without rebuilding the UI

            float size = !russian ? 2.45f : 1.45f;
            presetTmp.fontSizeMax = presetTmp.fontSizeMin = size;


            GameObject tempMinus = minusTemplate;
            gMinus = ModGameOptionsMenu.Track(Object.Instantiate(__instance.GamePresetsButton.gameObject, preset.transform));
            gMinus.gameObject.SetActive(true);
            gMinus.transform.localScale = new(0.08f, 0.4f, 1f);

            var mLabel = gMinus.transform.Find("FontPlacer/Text_TMP").GetComponent<TextMeshPro>();
            mLabel.alignment = TextAlignmentOptions.Center;
            mLabel.DestroyTranslator();
            mLabel.SetText("-");
            mLabel.transform.localPosition = new(mLabel.transform.localPosition.x, mLabel.transform.localPosition.y + 0.26f, mLabel.transform.localPosition.z);
            mLabel.color = Color.white;
            mLabel.SetFaceColor(new Color(255f, 255f, 255f));
            mLabel.transform.localScale = new(12f, 4f, 1f);

            var minus = gMinus.GetComponent<PassiveButton>();
            minus.OnClick.RemoveAllListeners();

            minus.OnClick.AddListener((Action)(() =>
            {
                // Drive the always-valid Preset model directly. The per-menu PresetBehaviour NumberOption can be
                // null/stale after the cached-UI reopen flow, and the null fallback ChangeTab(3) opens ImpostorRoles
                // (not the SystemSettings tab the Preset lives in), so +/- went unresponsive. RepeatIndex wraps.
                LastPresetChange = Utils.TimeStamp;
                Options.Preset.SetValue(OptionItem.CurrentPreset - 1);
                GameOptionsMenuPatch.ReloadUI();
                Logger.Info($"Current preset: {OptionItem.CurrentPreset + 1}", "GameOptionsMenuPatch");
            }));

            minus.activeTextColor = minus.inactiveTextColor = minus.disabledTextColor = minus.selectedTextColor = Color.white;
            minus.transform.localPosition = new(-2f, -3.37f, -4f);

            var inactiveSprites = minus.inactiveSprites.GetComponent<SpriteRenderer>();
            var activeSprites = minus.activeSprites.GetComponent<SpriteRenderer>();
            var selectedSprites = minus.selectedSprites.GetComponent<SpriteRenderer>();

            inactiveSprites.sprite = tempMinus.GetComponentInChildren<SpriteRenderer>().sprite;
            activeSprites.sprite = tempMinus.GetComponentInChildren<SpriteRenderer>().sprite;
            selectedSprites.sprite = tempMinus.GetComponentInChildren<SpriteRenderer>().sprite;

            inactiveSprites.color = new Color32(55, 59, 60, 255);
            activeSprites.color = new Color32(0, 255, 165, 255);
            selectedSprites.color = new Color32(0, 165, 255, 255);


            plusFab = ModGameOptionsMenu.Track(Object.Instantiate(gMinus, preset.transform));
            plusLabel = plusFab.transform.Find("FontPlacer/Text_TMP").GetComponent<TextMeshPro>();
            plusLabel.alignment = TextAlignmentOptions.Center;
            plusLabel.DestroyTranslator();
            plusLabel.SetText("+");
            plusLabel.color = Color.white;
            plusLabel.transform.localPosition = new(plusLabel.transform.localPosition.x, plusLabel.transform.localPosition.y + 0.26f, plusLabel.transform.localPosition.z);
            plusLabel.transform.localScale = new(18f, 4f, 1f);

            var plus = plusFab.GetComponent<PassiveButton>();
            plus.OnClick.RemoveAllListeners();

            plus.OnClick.AddListener((Action)(() =>
            {
                // See the minus handler: drive the Preset model directly instead of the fragile PresetBehaviour.
                LastPresetChange = Utils.TimeStamp;
                Options.Preset.SetValue(OptionItem.CurrentPreset + 1);
                GameOptionsMenuPatch.ReloadUI();
                Logger.Info($"Current preset: {OptionItem.CurrentPreset + 1}", "GameOptionsMenuPatch");
            }));

            plus.activeTextColor = plus.inactiveTextColor = plus.disabledTextColor = plus.selectedTextColor = Color.white;
            plus.transform.localPosition = new(-0.4f, -3.37f, -4f);

            PresetSelector = preset;
            PresetMinusButton = gMinus;
            PresetPlusButton = plusFab;
            }
            catch (Exception e)
            {
                Logger.Warn($"preset selector build failed — skipping preset UI this open: {e.Message}", "MenuLeak");
                if (preset) Object.Destroy(preset); // don't leave a half-built selector in the scene
                preset = null;
                gMinus = null;
                plusFab = null;
                plusLabel = null;
            }
        }


        XuiStage = "what-is-this";
        Transform whatIsThis = GameObject.Find("PlayerOptionsMenu(Clone)")?.transform.FindChild("What Is This?");
        if (whatIsThis) whatIsThis.gameObject.SetActive(false);

        XuiStage = "mode-label";
        Transform gslTf = __instance.GameSettingsButton.transform.parent.parent.FindChild("GameSettingsLabel");
        if (!gslTf)
        {
            // ⚠️ この return は SetupExtendedUI を抜けるので、末尾の**検索欄構築もまとめてスキップ**される
            // (旧ログ文言はモードラベルとモードボタンしか挙げておらず、検索欄が消えた回の原因が読めなかった)。
            Logger.Warn("GameSettingsLabel not found — skipping mode label + game-mode buttons + the option search box", "MenuLeak");
            return;
        }
        var gameSettingsLabel = gslTf.GetComponent<TextMeshPro>();
        gameSettingsLabel.DestroyTranslator();
        gameSettingsLabel.SetText($"<size=50%>{Translator.GetString($"Mode{Options.CurrentGameMode}")}</size>\n");
        GameModeLabelText = gameSettingsLabel;
        gameSettingsLabel.transform.localPosition += new Vector3(0f, 0.1f, 0f);

        if (russian)
        {
            gameSettingsLabel.transform.localScale = new(0.7f, 0.7f, 1f);
            gameSettingsLabel.transform.localPosition = new(-3.77f, 1.62f, -4);
        }

        Vector3 gameSettingsLabelPos = gameSettingsLabel.transform.localPosition;

        var gms = Main.CustomGameModeValues[..^1].ToList();
        gms.Remove(CustomGameMode.TheMindGame);
        
        if (SubmergedCompatibility.Loaded && Main.NormalOptions.MapId == 6)
            gms.RemoveAll(SubmergedCompatibility.IsNotSupported);

        if (Main.LIMap)
        {
            gms.Remove(CustomGameMode.CaptureTheFlag);
            gms.Remove(CustomGameMode.Quiz);
            gms.Remove(CustomGameMode.TheMindGame);
            gms.Remove(CustomGameMode.BedWars);
            gms.Remove(CustomGameMode.Deathrace);
        }
        
        const int totalCols = 3;

        for (var index = 0; index < gms.Count; index++)
        {
            CustomGameMode gm = gms[index];

            XuiStage = $"gm-buttons[{index}]";
            bool cachedGM = index < GMButtons.Count && GMButtons[index];
            // gMinus can be null when the preset-selector build was skipped above; fall back to the raw
            // GamePresetsButton so the game-mode buttons still exist (slightly plainer look, never an NRE).
            GameObject gmTemplate = gMinus ? gMinus : __instance.GamePresetsButton.gameObject;
            var gmButton = cachedGM ? GMButtons[index] : ModGameOptionsMenu.Track(Object.Instantiate(gmTemplate, gameSettingsLabel.transform, true));
            if (cachedGM)
                gmButton.transform.SetParent(gameSettingsLabel.transform, false);
            gmButton.SetActive(true);
            gmButton.transform.localPosition = new Vector3((((index / 8) - ((totalCols - 1) / 2f)) * 1.4f) + 0.86f, gameSettingsLabelPos.y - 1.9f - (0.22f * (index % 8)), -1f);

            gmButton.transform.localScale = new(0.4f, 0.3f, 1f);
            // ラベルの化粧は「取れなければ諦めてよい」処理。無ガードの Find(...).GetComponent で NRE を出すと
            // SetupExtendedUI ごと中断し、後続のモードボタンも末尾の検索欄構築も丸ごと消える (BUG-20260725-02
            // と同型の道連れ)。ボタン自体のクリック配線は下でそのまま続行させる。
            Transform gmButtonTextTf = gmButton.transform.Find("FontPlacer/Text_TMP");
            var gmButtonTmp = gmButtonTextTf ? gmButtonTextTf.GetComponent<TextMeshPro>() : null;
            if (gmButtonTmp)
            {
                gmButtonTmp.alignment = TextAlignmentOptions.Center;
                gmButtonTmp.DestroyTranslator();
                gmButtonTmp.SetText(Translator.GetString(gm.ToString()).ToUpper());
                gmButtonTmp.color = Main.GameModeColors[gm];
                gmButtonTmp.transform.localPosition = new(gameSettingsLabelPos.x + 3.35f, gameSettingsLabelPos.y - 1.62f, gameSettingsLabelPos.z);
                gmButtonTmp.transform.localScale = new(1f, 1f, 1f);
            }
            else Logger.Warn($"game-mode button [{index}] has no FontPlacer/Text_TMP — label styling skipped", "MenuLeak");

            var gmPassiveButton = gmButton.GetComponent<PassiveButton>();
            gmPassiveButton.OnClick.RemoveAllListeners();
            gmPassiveButton.OnClick.AddListener((Action)(() =>
            {
                // If the currently open tab doesn't exist in the new game mode, escape to Game Settings
                // before switching, or the user is left staring at a dead tab.
                if (GameSettingMenu.Instance && ModGameOptionsMenu.TabIndex >= 3 && !((TabGroup)(ModGameOptionsMenu.TabIndex - 3)).IsRelevant(gm))
                    GameSettingMenu.Instance.ChangeTab((int)TabGroup.GameSettings + 3, false);

                Options.GameMode.SetValue((int)gm - 1);
                GameOptionsMenuPatch.ReloadUI();
            }));
            gmPassiveButton.activeTextColor = gmPassiveButton.inactiveTextColor = gmPassiveButton.disabledTextColor = gmPassiveButton.selectedTextColor = Main.GameModeColors[gm];

            // dead-cache slot must be REPLACED in place. Appending instead grew the list past gms.Count, and
            // the trim loop below then destroyed the freshly-created buttons from the tail while the dead
            // entries stayed — the "menu empty after DC→rehost" state (BUG-20260706-01 round8 ②/⑤).
            if (index < GMButtons.Count) GMButtons[index] = gmButton;
            else GMButtons.Add(gmButton);
        }

        while (GMButtons.Count > gms.Count)
        {
            Object.Destroy(GMButtons[^1]);
            GMButtons.RemoveAt(GMButtons.Count - 1);
        }


        if (!HudManager.InstanceExists) return;

        XuiStage = "search-field";
        bool cachedInputField = InputField;
        FreeChatInputField field;
        if (cachedInputField)
        {
            field = InputField;
            field.transform.SetParent(parentLeftPanel.parent, false);
            field.gameObject.SetActive(true);
        }
        else
        {
            FreeChatInputField freeChatField = HudManager.Instance.Chat.freeChatField;
            if (!freeChatField)
            {
                Logger.Warn("freeChatField unavailable — skipping the option search box this open", "MenuLeak");
                return;
            }
            XuiStage = "search-field/clone";
            field = ModGameOptionsMenu.Track(Object.Instantiate(freeChatField, parentLeftPanel.parent));

            XuiStage = "search-field/ghost-strip";
            StripPlaceHolderGhosts(field);
        }

        XuiStage = "search-field/skin";
        field.transform.localScale = new(0.3f, 0.59f, 1);
        field.transform.localPosition = new(-0.7f, -2.5f, -5f);

        if (field.textArea && field.textArea.outputText)
        {
            field.textArea.outputText.transform.localScale = new(3.5f, 2f, 1f);
            if (plusLabel) field.textArea.outputText.font = plusLabel.font;
        }
        else Logger.Warn("search box clone has no outputText — text styling skipped (BUG-20260725-02)", "MenuLeak");

        Transform button = field.transform.FindChild("ChatSendButton");
        if (!button) { BailSearchField("no ChatSendButton in the clone"); return; }

        Transform buttonNormal = button.FindChild("Normal");
        Transform buttonHover = button.FindChild("Hover");
        Transform buttonDisabled = button.FindChild("Disabled");
        if (!buttonNormal || !buttonHover || !buttonDisabled) { BailSearchField("ChatSendButton is missing a state child"); return; }

        // 送信ボタンの Icon/Text 破棄 (チャット欄→検索欄の再スキン) は以前「新規 clone のときだけ」走らせて
        // いた。この区間で例外が飛ぶと未スキンの個体が InputField にキャッシュされ、以降は毎回キャッシュ経路
        // を通って再スキンが二度と走らない → 「設定の検索欄が生のチャット入力欄のまま」がアプリ再起動まで
        // 固定される (BUG-20260725-02)。毎回・冪等 (存在チェック付き) に走らせて自己修復させる。
        int stripped = StripChatIcon(buttonNormal) + StripChatIcon(buttonHover) + StripChatIcon(buttonDisabled);

        Transform buttonText = button.FindChild("Text");
        if (buttonText)
        {
            var textTmp = buttonText.GetComponent<TextMeshPro>();
            if (textTmp) { Object.Destroy(textTmp); stripped++; }
        }

        // キャッシュ済み個体に生チャットの部品が残っていた = 汚染状態の直接観測。真因特定用の唯一の証拠。
        if (cachedInputField && stripped > 0)
            Logger.Warn($"cached search box still carried {stripped} raw chat-skin part(s) — re-stripped (BUG-20260725-02)", "MenuLeak");

        Transform buttonNormalBackground = buttonNormal.FindChild("Background");
        Transform buttonHoverBackground = buttonHover.FindChild("Background");
        Transform buttonDisabledBackground = buttonDisabled.FindChild("Background");
        if (!buttonNormalBackground || !buttonHoverBackground || !buttonDisabledBackground) { BailSearchField("ChatSendButton state has no Background"); return; }

        SetSearchIcon(buttonNormalBackground, "EndKnot.Resources.Images.SearchIconActive.png");
        SetSearchIcon(buttonHoverBackground, "EndKnot.Resources.Images.SearchIconHover.png");
        SetSearchIcon(buttonDisabledBackground, "EndKnot.Resources.Images.SearchIcon.png");

        // 再スキンを最後まで通した個体だけをキャッシュする。途中で落ちた個体を掴んだままにすると、次回以降
        // キャッシュ経路に入って永久に未スキンのままになる (上のコメントの汚染そのもの)。
        InputField = field;

        if (russian)
        {
            Vector3 fixedScale = new(0.7f, 1f, 1f);
            buttonNormalBackground.transform.localScale = fixedScale;
            buttonHoverBackground.transform.localScale = fixedScale;
            buttonDisabledBackground.transform.localScale = fixedScale;
        }


        XuiStage = "search-field/wire";
        var passiveButton = button.GetComponent<PassiveButton>();

        passiveButton.OnClick = new();
        passiveButton.OnClick.AddListener((Action)(() => SearchForOptions(field)));

        // An empty box is a valid input now — that is how the search is left, so Enter must go through.
        SearchForOptionsAction = () => SearchForOptions(field);

        return;

        // 検索欄の組み立てを途中で諦める共通経路。新規 clone なら破棄する (放置すると開くたびに増える)。
        void BailSearchField(string why)
        {
            Logger.Warn($"search box: {why} — skipping the option search box this open", "MenuLeak");
            if (!cachedInputField && field) Object.Destroy(field.gameObject);
        }

        // Object.Instantiate deep-clones the whole chat field. If the chat command-autocomplete ghost
        // (TextBoxPatch.PlaceHolderText, a child of the live freeChatField subtree) happens to exist when
        // Settings is opened, the clone copies it → a "PlaceHolderText(Clone)" that ends up cached in the
        // DontDestroyOnLoad UI root and accumulates on every reopen, leaking its GameObject name into the
        // UI. Strip any such ghost out of this clone so the search box only carries its own text field.
        // DestroyImmediate: 遅延 Destroy だとフレーム末まで実体が残り、その間の UiAnomalyWatch スキャンに
        // live=2 (DUP) と映るため即時破棄する。
        // ただし clone 自身の outputText は絶対に巻き込まない。チャット側で参照すり替わり (UiAnomalyWatch の
        // DRIFT 状態) が起きていると textArea.outputText が "PlaceHolderText" 名の個体を指しており、名前だけ
        // で消すと clone の本体テキストごと消えて以降の styling が NRE → 検索欄が生チャットのまま残る。
        //
        // ⚠️ この掃除は**リーク衛生であって必須処理ではない**。ゴーストが1個残る損害より、ここで例外が出て
        // 以降の再スキン (scale/位置/アイコン差替/OnClick 配線/InputField キャッシュ) が丸ごと飛ぶ損害の方が
        // 遥かに大きい — clone だけが生のチャット入力欄の姿で設定画面に残り、InputField にも載らないので
        // 開き直しても毎回同じ経路で落ちて直らない (BUG-20260725-02 の「まんまチャット欄・検索も効かない・
        // 部屋を建て直すまで固定」がこれ)。ghost.name は IL2CPP の native get_name で、解放済み/再利用
        // スロットの TMP を掴むと NRE を投げうる (2026-07-12 実機の get_name NRE と同型)。よって probe /
        // scan / 各要素をそれぞれ隔離し、失敗しても必ず再スキンへ抜ける。
        static void StripPlaceHolderGhosts(FreeChatInputField field)
        {
            TextMeshPro clonedOutput;
            try { clonedOutput = field.textArea ? field.textArea.outputText : null; }
            catch (Exception e) { Logger.Warn($"search box: outputText probe failed ({e.Message}) — ghost strip skipped (BUG-20260725-02)", "MenuLeak"); return; }

            Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<TextMeshPro> ghosts;
            try { ghosts = field.GetComponentsInChildren<TextMeshPro>(true); }
            catch (Exception e) { Logger.Warn($"search box: ghost scan failed ({e.Message}) — ghost strip skipped (BUG-20260725-02)", "MenuLeak"); return; }

            if (ghosts == null) return;

            foreach (TextMeshPro ghost in ghosts)
            {
                try
                {
                    if (ghost && ghost.name.StartsWith("PlaceHolderText") && ghost != clonedOutput)
                        Object.DestroyImmediate(ghost.gameObject);
                }
                catch (Exception e) { Logger.Warn($"search box: skipped one ghost ({e.Message}) (BUG-20260725-02)", "MenuLeak"); }
            }
        }

        // 既に剥がされていれば何もしない冪等版。Destroy(null) を interop 境界に投げない。
        static int StripChatIcon(Transform state)
        {
            Transform icon = state.FindChild("Icon");
            if (!icon) return 0;

            var sr = icon.GetComponent<SpriteRenderer>();
            if (!sr) return 0;

            Object.Destroy(sr);
            return 1;
        }

        static void SetSearchIcon(Transform background, string resource)
        {
            var sr = background.GetComponent<SpriteRenderer>();
            if (!sr) return;

            Sprite sprite = Utils.LoadSprite(resource, 100f);
            if (sprite) sr.sprite = sprite;
        }

        void SearchForOptions(FreeChatInputField textField)
        {
            if (ModGameOptionsMenu.TabIndex < 3) return;

            // What the user SEES in the box, IME composition included — the suggestion patch's managed
            // snapshot. Reading the field back gets only the committed half, so an Enter mid-composition
            // searched a different (often empty) string than the preview line was scoring.
            string text = OptionSearchSuggestPatch.CurrentQueryText;
            if (text.Length == 0) text = TextBoxPatch.SafeChatText(textField.textArea).Trim();

            // Empty box = leave the search. The result list borrowed rows from every tab it drew from,
            // so all tabs have to be rebuilt for those rows to go home.
            if (text.Length == 0)
            {
                if (OptionSearch.Clear()) GameOptionsMenuPatch.ReCreateAllSettings();
                return;
            }

            OptionSearch.SearchResult result = OptionSearch.Apply(text);

            if (result.Total == 0)
            {
                Logger.SendInGame(Translator.GetString("SearchNoResult"), Palette.Orange);
                return;
            }

            // The whole result list — every matching option from every tab — is drawn into the tab that
            // is open, so the hits sit together in one place instead of the user chasing them per tab.
            // (PresetExplorer is the one tab that draws something else entirely; results show up there
            // as soon as any other tab is opened.)
            var openTab = (TabGroup)(ModGameOptionsMenu.TabIndex - 3);

            if (openTab != TabGroup.PresetExplorer && ModSettingsTabs.TryGetValue(openTab, out GameOptionsMenu gameSettings) && gameSettings)
            {
                if (gameSettings.scrollBar) gameSettings.scrollBar.ScrollToTop();
                gameSettings.CreateSettings();
            }

            string tabs = string.Join(", ", result.Counts.Select(kvp => $"{Translator.GetString($"TabGroup.{kvp.Key}")} ({kvp.Value})"));

            Logger.SendInGame(result.Shown < result.Total
                ? string.Format(Translator.GetString("SearchResultsCapped"), result.Total, result.Shown, tabs)
                : string.Format(Translator.GetString("SearchResultsShown"), result.Total, tabs));

            textField.Clear();
            OptionSearchSuggestPatch.NotifyCleared(); // FreeChatInputField.Clear may not raise TextBoxTMP.Clear — the snapshot must not survive a search
        }
    }

    private static void SetDefaultButton(GameSettingMenu __instance)
    {
        __instance.GamePresetsButton.gameObject.SetActive(false);

        PassiveButton gameSettingButton = __instance.GameSettingsButton;
        gameSettingButton.transform.localPosition = new(-3f, -0.55f, 0f);

        var textLabel = gameSettingButton.GetComponentInChildren<TextMeshPro>();
        textLabel.DestroyTranslator();
        textLabel.SetText(Translator.GetString("TabGroup.VanillaSettings"));

        gameSettingButton.activeTextColor = gameSettingButton.inactiveTextColor = Color.white;
        gameSettingButton.selectedTextColor = Color.gray;

        gameSettingButton.inactiveSprites.GetComponent<SpriteRenderer>().color = Color.black;
        gameSettingButton.activeSprites.GetComponent<SpriteRenderer>().color = Color.gray;
        gameSettingButton.selectedSprites.GetComponent<SpriteRenderer>().color = Color.black;

        gameSettingButton.transform.localPosition = ButtonPositionLeft;
        gameSettingButton.transform.localScale = ButtonSize;

        __instance.RoleSettingsButton.gameObject.SetActive(false);

        __instance.DefaultButtonSelected = gameSettingButton;
        __instance.ControllerSelectable = new();
        __instance.ControllerSelectable.Add(gameSettingButton);
    }

    [HarmonyPatch(nameof(GameSettingMenu.ChangeTab))]
    [HarmonyPrefix]
    public static bool ChangeTabPrefix(GameSettingMenu __instance, ref int tabNum, [HarmonyArgument(1)] bool previewOnly)
    {
        if (!previewOnly || tabNum != 1) ModGameOptionsMenu.TabIndex = tabNum;

        GameOptionsMenu settingsTab;
        PassiveButton button;

        if ((previewOnly && Controller.currentTouchType == Controller.TouchType.Joystick) || !previewOnly)
        {
            TabGroup[] tabGroups = Main.TabGroupValues;

            foreach (TabGroup tab in tabGroups)
            {
                if (ModSettingsTabs.TryGetValue(tab, out settingsTab) && settingsTab)
                    settingsTab.gameObject.SetActive(false);
            }

            foreach (TabGroup tab in tabGroups)
            {
                if (ModSettingsButtons.TryGetValue(tab, out button) && button)
                    button.SelectButton(false);
            }

            // Disable all underlines after deselect-all (active menu only; do not touch in preview-only hover path).
            if (NewRoleMenuState.Active)
            {
                foreach (var kv in NewRoleMenuState.TabUnderlines)
                    if (kv.Value) kv.Value.enabled = false;
            }
        }

        if (tabNum < 3) return true;

        var tabGroup = (TabGroup)(tabNum - 3);
        
        if (!OnlinePresetsManager.PresetsLoaded && tabGroup == TabGroup.PresetExplorer)
        {
            __instance.StartCoroutine(
                OnlinePresetsManager.FetchPresetList(list =>
                {
                    OnlinePresetsManager.CachedPresets = list;
                    OnlinePresetsManager.PresetsLoaded = true;
                    if (ModSettingsTabs.TryGetValue(TabGroup.PresetExplorer, out GameOptionsMenu presetTab) && presetTab)
                        OnlinePresetsManager.CreatePresetExplorerUI(presetTab);
                }).WrapToIl2Cpp()
            );
        }

        if ((previewOnly && Controller.currentTouchType == Controller.TouchType.Joystick) || !previewOnly)
        {
            __instance.PresetsTab.gameObject.SetActive(false);
            __instance.GameSettingsTab.gameObject.SetActive(false);
            __instance.RoleSettingsTab.gameObject.SetActive(false);
            __instance.GamePresetsButton.SelectButton(false);
            __instance.GameSettingsButton.SelectButton(false);
            __instance.RoleSettingsButton.SelectButton(false);

            if (ModSettingsTabs.TryGetValue(tabGroup, out settingsTab) && settingsTab)
            {
                settingsTab.gameObject.SetActive(true);
                __instance.MenuDescriptionText.DestroyTranslator();
                __instance.MenuDescriptionText.SetText(Translator.GetString("TabInfoTip"));
            }
        }

        if (previewOnly)
        {
            __instance.ToggleLeftSideDarkener(false);
            __instance.ToggleRightSideDarkener(true);
            return false;
        }

        __instance.ToggleLeftSideDarkener(true);
        __instance.ToggleRightSideDarkener(false);

        // While a search is on, every tab draws the same cross-tab result list. A tab only builds itself
        // once, and the rows the list needs may be parented under the tab the search was run from, so
        // the tab being shown has to redraw instead of putting up its old contents.
        // PresetExplorer draws an online preset browser instead of options — it never shows results.
        if (OptionSearch.Active && tabGroup != TabGroup.PresetExplorer && ModSettingsTabs.TryGetValue(tabGroup, out GameOptionsMenu searchTab) && searchTab)
        {
            if (searchTab.scrollBar) searchTab.scrollBar.ScrollToTop();
            searchTab.CreateSettings();
        }

        if (ModSettingsButtons.TryGetValue(tabGroup, out button) && button) button.SelectButton(true);

        // Enable the underline for the newly selected tab (active menu only; only on commit, not preview-only).
        if (NewRoleMenuState.Active)
        {
            if (NewRoleMenuState.TabUnderlines.TryGetValue(tabGroup, out var underlineSr) && underlineSr)
                underlineSr.enabled = true;
        }

        return false;
    }

    [HarmonyPatch(nameof(GameSettingMenu.OnEnable))]
    [HarmonyPrefix]
    private static bool OnEnablePrefix(GameSettingMenu __instance)
    {
        // In the Backrooms lobby, hide the full-screen creepy overlay while the menu is open — its
        // order-100 tint would otherwise paint the world-space menu yellow. No-op outside Backrooms.
        BackroomsLobby.SetOverlaySuppressed(true);

        // Opening the settings menu while the Among Us chat is still open is an abnormal state (normally
        // unreachable): the chat input field is left over the menu, and — since the mod's search box is a
        // clone of that chat field — the two coexist and role "?" help clicks stop resolving. Force the chat
        // closed on open so the menu is always the only active surface.
        if (HudManager.InstanceExists && HudManager.Instance.Chat && HudManager.Instance.Chat.IsOpenOrOpening)
            HudManager.Instance.Chat.ForceClosed();

        if (!TemplateGameOptionsMenu)
        {
            TemplateGameOptionsMenu = ModGameOptionsMenu.Track(Object.Instantiate(__instance.GameSettingsTab, __instance.GameSettingsTab.transform.parent));
            TemplateGameOptionsMenu.gameObject.SetActive(false);
        }

        if (!TemplateGameSettingsButton)
        {
            TemplateGameSettingsButton = ModGameOptionsMenu.Track(Object.Instantiate(__instance.GameSettingsButton, __instance.GameSettingsButton.transform.parent));
            TemplateGameSettingsButton.gameObject.SetActive(false);
        }

        SetDefaultButton(__instance);

        ControllerManager.Instance.OpenOverlayMenu(__instance.name, __instance.BackButton, __instance.DefaultButtonSelected, __instance.ControllerSelectable);
        if (HudManager.InstanceExists) HudManager.Instance.menuNavigationPrompts.SetActive(false);
        if (Controller.currentTouchType != Controller.TouchType.Joystick) __instance.ChangeTab(1, false);

        __instance.StartCoroutine(__instance.CoSelectDefault());

        return false;
    }

    [HarmonyPatch(nameof(GameSettingMenu.Close))]
    [HarmonyPrefix]
    public static void Close_Prefix()
    {
        BackroomsLobby.SetOverlaySuppressed(false); // restore the Backrooms overlay when the menu closes

        if (TempParent == null)
        {
            var go = new GameObject("EHR Settings UI Cache Root");
            Object.DontDestroyOnLoad(go);
            go.SetActive(true);
            TempParent = go.transform;
        }

        GameOptionsMenuPatch.ReCreateGeneration++;

        if (GameOptionsMenuPatch.ReCreateAllCoroutine != null)
        {
            Main.Instance.StopCoroutine(GameOptionsMenuPatch.ReCreateAllCoroutine);
            GameOptionsMenuPatch.ReCreateAllCoroutine = null;
        }

        // Unity-null ガード + each-item 例外隔離 (BUG-20260706-01 round8 ①): rehost/シーン再ロードで
        // 破棄された個体が dict に残ると、`if (x)` の生存チェック後でも IL2CPP 側の破棄タイミング次第で
        // 引数評価 x.gameObject が Il2CppException を投げ得る。1個の失敗で退避ループ全体がアボートすると、
        // 以降の GMButtons(モード名ラベル)等が非表示・退避されず active のままシーンに残留する
        // (結果画面への漏れ + 次オープンで再生成され旧個体が回収されず累積)。項目単位で隔離する。
        foreach (var x in ModSettingsTabs.Values) StashSafely(() => x ? x.gameObject : null, "ModSettingsTabs");
        foreach (var x in ModSettingsButtons.Values) StashSafely(() => x ? x.gameObject : null, "ModSettingsButtons");

        // Clear GMButton listeners before hiding them so they can't fire stale callbacks
        foreach (var x in GMButtons)
        {
            StashSafely(() =>
            {
                if (x)
                {
                    var pb = x.GetComponent<PassiveButton>();
                    if (pb) pb.OnClick.RemoveAllListeners();
                }
                return x;
            }, "GMButtons");
        }
        StashSafely(() => InputField ? InputField.gameObject : null, "InputField");
        StashSafely(() => PresetSelector, "PresetSelector"); // preset selector trio — kept alive so it isn't re-created (and re-Tracked) each open
        StashSafely(() => TemplateGameOptionsMenu ? TemplateGameOptionsMenu.gameObject : null, "TemplateGameOptionsMenu");
        StashSafely(() => TemplateGameSettingsButton ? TemplateGameSettingsButton.gameObject : null, "TemplateGameSettingsButton");

        // 行/ヘッダを含む退避 UI にはこれまで Destroy 経路が存在せず、キャッシュ dict から外れた世代が
        // TempParent 配下に孤児として永久残留していた (round8 ④の +2万GO級ジャンプの受け皿)。close の
        // たびに TempParent 直下を白リスト照合し、どの cache からも参照されない個体を破棄する。
        try
        {
            System.Collections.Generic.HashSet<int> keep = [];
            foreach (var x in ModSettingsTabs.Values) if (x) keep.Add(x.gameObject.GetInstanceID());
            foreach (var x in ModSettingsButtons.Values) if (x) keep.Add(x.gameObject.GetInstanceID());
            foreach (var x in GMButtons) if (x) keep.Add(x.GetInstanceID());
            if (InputField) keep.Add(InputField.gameObject.GetInstanceID());
            if (PresetSelector) keep.Add(PresetSelector.GetInstanceID());
            if (TemplateGameOptionsMenu) keep.Add(TemplateGameOptionsMenu.gameObject.GetInstanceID());
            if (TemplateGameSettingsButton) keep.Add(TemplateGameSettingsButton.gameObject.GetInstanceID());

            var orphans = 0;
            for (int i = TempParent.childCount - 1; i >= 0; i--)
            {
                GameObject child = TempParent.GetChild(i).gameObject;
                if (!keep.Contains(child.GetInstanceID()))
                {
                    orphans++;
                    Object.Destroy(child);
                }
            }

            if (orphans > 0) Logger.Info($"orphan sweep: destroyed {orphans} unreferenced cached object(s)", "MenuLeak");
        }
        catch (Exception e) { Logger.Warn($"orphan sweep failed: {e.Message}", "MenuLeak"); }

        return;

        void StashSafely(Func<GameObject> resolve, string what)
        {
            try
            {
                GameObject go = resolve();
                if (!go) return;
                go.transform.SetParent(TempParent, false);
                go.SetActive(false);
            }
            catch (Exception e) { Logger.Warn($"stash {what} failed: {e.Message}", "MenuLeak"); }
        }
    }

    // 設定 UI キャッシュの全破棄。キャッシュ (行 ~1万GO / マテリアル ~9千 / Boehm 100MB級) は再オープンを
    // 速くするためだけの物で、メニューが開けない試合中には丸ごと死荷重 — incremental GC 無効環境では
    // 全 GC の stop-the-world マーク時間と、シーン遷移の暗黙 Resources.UnloadUnusedAssets の全走査時間の
    // 両方を試合の間じゅう延ばし続ける。ゲーム開始確定点 (StartGameHost、直後に GCPRE loading が走り
    // ロード画面で回収が隠れる) で破棄し、次にロビーでメニューを開いた時に既存の遅延ビルドで作り直す。
    // ロビー内での開閉はキャッシュがそのまま残るので今まで通り即時。
    public static void PurgeUiCache(string reason)
    {
        if (GameSettingMenu.Instance) return; // 開いている最中の UI は絶対に壊さない (通常この状況で呼ばれない)

        try
        {
            var destroyed = 0;

            if (TempParent)
            {
                for (int i = TempParent.childCount - 1; i >= 0; i--)
                {
                    Object.Destroy(TempParent.GetChild(i).gameObject);
                    destroyed++;
                }
            }

            // 破棄した個体への静的参照をすべて手放す。取りこぼしても既存の dead-cache ガード
            // (CreateSettings / StartPostfix の Unity-null 判定) が次回オープンで拾うが、参照が残る限り
            // マネージド側 (IL2CPP ラッパー + dict) が回収されないので、ここで明示的に空にする。
            ModSettingsTabs.Clear();
            ModSettingsButtons.Clear();
            GMButtons.Clear();
            InputField = null;
            PresetSelector = null;
            PresetMinusButton = null;
            PresetPlusButton = null;
            PresetBehaviour = null;
            PresetValueText = null;
            GameModeLabelText = null;
            TemplateGameOptionsMenu = null;
            TemplateGameSettingsButton = null;

            ModGameOptionsMenu.OptionList.Clear();
            ModGameOptionsMenu.BehaviourList.Clear();
            ModGameOptionsMenu.CategoryHeaderList.Clear();
            ModGameOptionsMenu.HelpIconList.Clear();
            ModGameOptionsMenu.BaseGameSettingCache.Clear();

            foreach (OptionItem option in OptionItem.AllOptions) option.OptionBehaviour = null;

            GameOptionsMenuPatch.OnUiCachePurged();
            OptionSearch.Clear();
            NewRoleMenuState.DetailObjects.Clear();
            NewRoleMenuState.SelBars.Clear();
            NewRoleMenuState.LastBuiltSelectedIndex = -2;
            RoleMenuTabBar.Reset();

            Logger.Info($"UI cache purged ({reason}): destroyed {destroyed} cached root object(s)", "MenuLeak");
        }
        catch (Exception e) { Logger.Warn($"UI cache purge failed: {e.Message}", "MenuLeak"); }
    }
    [HarmonyPatch(nameof(GameSettingMenu.Close))]
    [HarmonyPostfix]
    public static void Close_Postfix()
    {
        try
        {
            int numImpostors = Main.NormalOptions.NumImpostors;
            (OptionItem MinSetting, OptionItem MaxSetting) = Options.FactionMinMaxSettings[Team.Impostor];

            if (numImpostors != NumImpsOnOpen && MinImpsOnOpen == MinSetting.GetInt() && MaxImpsOnOpen == MaxSetting.GetInt())
            {
                MinSetting.SetValue(numImpostors);
                MaxSetting.SetValue(numImpostors);
                Logger.SendInGame(string.Format(Translator.GetString("MinMaxModdedImpCountsSettingsChangedAuto"), numImpostors));
            }


            float vanillaKillCooldown = Main.NormalOptions.KillCooldown;
            float moddedKillCooldown = Options.FallBackKillCooldownValue.GetFloat();

            bool vanillaChanged = !Mathf.Approximately(vanillaKillCooldown, VanillaKillCooldownOnOpen);
            bool moddedChanged = !Mathf.Approximately(moddedKillCooldown, ModdedKillCooldownOnOpen);

            if (!Mathf.Approximately(vanillaKillCooldown, moddedKillCooldown) && vanillaChanged && !moddedChanged)
                Options.FallBackKillCooldownValue.SetValue((int)Math.Round((Math.Round(vanillaKillCooldown, 1) / 0.5f) - 1));
        }
        catch (Exception e) { Utils.ThrowException(e); }

        // Each menu open+close orphans material instances (the vanilla menu masks its own panels with
        // fresh "(Instance)" materials and is then destroyed — destroying a renderer never destroys its
        // instanced material). かつてはここで GC+UnloadUnusedAssets+GC を打っていたが、Backrooms ロビーは
        // GameObject 1万超なので Unload の全走査だけで 600-700ms 級の体感ヒッチになる (プレイヤーが操作中の
        // ロビーで最悪の瞬間に止まる)。孤児マテリアルは native メモリを食うだけで GC 停止時間には効かないため、
        // 回収はゲーム開始時の UI キャッシュ purge + シーン遷移 (ゲーム終了→ロビー等) の暗黙 Unload に任せる。

        try
        {
            if (Options.AutoGMRotationRecompileOnClose)
                Options.CompileAutoGMRotationSettings();
        }
        catch (Exception e) { Utils.ThrowException(e); }
        
        Options.AutoSetFactionMinMaxSettings();

        OptionItem.SyncAllOptions();

        //Main.Instance.StartCoroutine(OptionShower.GetText());
    }

    public static System.Collections.Generic.Dictionary<TabGroup, GameOptionsMenu> GetModSettingsTabs() => ModSettingsTabs;
    public static System.Collections.Generic.Dictionary<TabGroup, PassiveButton> GetModSettingsButtons() => ModSettingsButtons;
}

[HarmonyPatch(typeof(FreeChatInputField), nameof(FreeChatInputField.UpdateCharCount))]
public static class FixInputChatField
{
    public static bool Prefix(FreeChatInputField __instance)
    {
        // textArea/Background 欠損の個体でも InputField にキャッシュされうる (ゴースト掃除を非致命化した結果、
        // "search box clone has no outputText" の Warn を出した clone もキャッシュまで到達する)。無ガードだと
        // ここが毎フレーム NRE になり、しかも return false 済みでバニラの UpdateCharCount まで止まる。
        if (GameSettingMenuPatch.InputField && __instance == GameSettingMenuPatch.InputField && __instance.gameObject.activeSelf && __instance.Background && __instance.textArea)
        {
            Vector2 size = __instance.Background.size;
            size.y = Math.Max(0.62f, __instance.textArea.TextHeight + 0.2f);
            __instance.Background.size = size;
            return false;
        }

        return true;
    }
}

[HarmonyPatch(typeof(ChatController), nameof(ChatController.Update))]
public static class FixDarkThemeForSearchBar
{
    public static void Postfix()
    {
        if (!GameSettingMenu.Instance) return;

        MenuPerfProbe.Pump();

        FreeChatInputField field = GameSettingMenuPatch.InputField;

        if (!field || !field.gameObject.activeSelf) return;

        OptionSearchSuggestPatch.Pump();

        if (field.background) field.background.color = new Color32(40, 40, 40, byte.MaxValue);

        // outputText を欠いた clone もキャッシュされうる (FixInputChatField 側のコメント参照)。ここは
        // ChatController.Update ごとに走るので、無ガードだと毎フレーム NRE になる。
        if (field.textArea && field.textArea.outputText) field.textArea.outputText.color = Color.white;
    }
}
