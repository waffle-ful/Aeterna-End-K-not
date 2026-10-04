using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace EndKnot;

// Draws the settings search's result list (OptionSearch.Results).
//
// The list has its own container — a sibling of the open tab's settingsContainer, under the same scroller —
// and its own rows. A search never re-parents, hides or re-lays-out a tab's rows: showing the list moves
// the tab's container sideways out of the masked viewport and puts this one in its place, and ending the
// search moves it back. Whatever tabs were opened, built or switched between in the meantime, each tab
// is left exactly as its own build made it. (The tab's container is moved rather than switched off: a
// tab built for the first time while the list is up instantiates its rows under it, and rows created
// under an inactive parent would not run Awake until later.)
public static class OptionSearchView
{
    // Result rows, one per option index, kept across searches the same way the tabs keep theirs: a fresh
    // row's SetUpFromData instances the masked materials, so a repeated hit reuses its row.
    private static readonly Dictionary<int, OptionBehaviour> Rows = [];
    private static readonly Dictionary<int, CategoryHeaderMasked> Headers = [];

    // Instance ids of Rows — an IL2CPP wrapper fetched twice is not necessarily the same managed object.
    private static readonly HashSet<int> RowIds = [];

    private struct Placed
    {
        public GameObject Go;
        public float Y;
        public bool Logical;
    }

    private static readonly List<Placed> Layout = [];

    private static GameObject Container;
    private static GameOptionsMenu Host; // the tab the list is currently drawn over
    private static Vector3 HostHome; // Host.settingsContainer's own localPosition
    private static Transform HostInner; // the scroller's own Inner, while the list has taken its place
    private static readonly Vector3 HostAside = new(1000f, 0f, 0f);
    private static float BoundsMax;
    private static float LastCullRelY = float.NaN;
    private static bool CullDirty;

    public static bool IsSearchRow(OptionBehaviour ob) => ob && RowIds.Contains(ob.GetInstanceID());

    // Shows the current result list in place of the given tab's options.
    public static void ShowIn(GameOptionsMenu menu)
    {
        if (!OptionSearch.Active || !menu || !menu.settingsContainer) return;

        if (Host != menu || !Container)
        {
            Detach();

            Transform own = menu.settingsContainer;

            if (!Container)
            {
                // The list scrolls only if it shares the scroller's moving content with the tab's container.
                Transform scrollInner = menu.scrollBar ? menu.scrollBar.Inner : null;
                Logger.Info($"result list container: settingsContainer {(own == scrollInner ? "IS" : "is under")} the scroller's Inner (parent={(own.parent ? own.parent.name : "-")})", "OptionSearchView");

                Container = new GameObject("OptionSearchResults") { layer = own.gameObject.layer };
                Rows.Clear();
                Headers.Clear();
                RowIds.Clear();
            }

            // Exactly where the tab's container sits, then hung from the scroller's moving content so the
            // list scrolls whether the tab's container is a child or a deeper descendant of it.
            Transform inner = menu.scrollBar && menu.scrollBar.Inner && menu.scrollBar.Inner != own ? menu.scrollBar.Inner : own.parent;
            Container.transform.SetParent(own, false);
            Container.transform.localPosition = Vector3.zero;
            Container.transform.localRotation = Quaternion.identity;
            Container.transform.localScale = Vector3.one;
            Container.transform.SetParent(inner, true);

            // The click mask is per tab, so the rows have to take the new tab's.
            foreach (OptionBehaviour row in Rows.Values)
                if (row) row.SetClickMask(menu.ButtonClickMask);

            Host = menu;
            HostHome = own.localPosition;
            own.localPosition = HostHome + HostAside;

            // When the tab's container is itself the scroller's moving content, the list hangs beside it
            // and would stay put — the scroller moves the list instead until the search lets go of it.
            if (menu.scrollBar && menu.scrollBar.Inner == own)
            {
                HostInner = own;
                menu.scrollBar.Inner = Container.transform;
            }
        }

        Container.SetActive(true);
        if (menu.scrollBar) menu.scrollBar.ScrollToTop();
        Relayout();
    }

    // Takes the list off the tab it is drawn over (the search itself stays on).
    public static void Detach()
    {
        if (Container) Container.SetActive(false);
        if (Host && Host.scrollBar && HostInner) Host.scrollBar.Inner = HostInner;
        if (Host && Host.settingsContainer) Host.settingsContainer.localPosition = HostHome;

        Host = null;
        HostInner = null;
        Layout.Clear();
    }

    // Leaves the search and gives the open tab its own options back.
    public static void End()
    {
        GameOptionsMenu host = Host;
        var results = new List<int>(OptionSearch.Results);

        Detach();
        if (!OptionSearch.Clear()) return;

        // A result row's Initialize claimed option.OptionBehaviour, and a value changed from the list is
        // not on the tab's row yet.
        foreach (int index in results)
        {
            if (!ModGameOptionsMenu.BehaviourList.TryGetValue(index, out OptionBehaviour row) || !row) continue;

            OptionItem option = OptionItem.AllOptions[index];
            option.OptionBehaviour = row;
            GameOptionsMenuPatch.ReassertRowValue(row, option);
        }

        if (host && host.scrollBar) host.scrollBar.ScrollToTop();

        // Values changed from the list can show or hide options of any tab.
        GameOptionsMenuPatch.ReCreateAllSettings();
    }

    // The tab under the list just set its own scroll range and controller targets — put the list's back.
    public static void AfterTabLayout(GameOptionsMenu menu)
    {
        if (!OptionSearch.Active || !Host || menu != Host || !menu.scrollBar) return;

        menu.scrollBar.SetYBoundsMax(BoundsMax);
        RefreshSelectables();
    }

    // Controller navigation targets: everything active under the scroller except the rows of the tab that
    // was moved aside.
    private static void RefreshSelectables()
    {
        Host.ControllerSelectable.Clear();
        Transform aside = Host.settingsContainer;

        foreach (UiElement x in Host.scrollBar.GetComponentsInChildren<UiElement>())
            if (!aside || !x.transform.IsChildOf(aside)) Host.ControllerSelectable.Add(x);
    }

    public static void Relayout()
    {
        if (!OptionSearch.Active || !Host || !Container) return;

        var num = 2.0f;
        Layout.Clear();

        foreach (int index in OptionSearch.Results)
        {
            if (index < 0 || index >= OptionItem.AllOptions.Count) continue;

            try
            {
                OptionItem option = OptionItem.AllOptions[index];
                // The list is exactly what the search admitted: a hit under a disabled role, or inside a
                // collapsed section, still shows.
                bool visible = !option.IsCurrentlyHidden(checkCollapsedSection: false);

                if (option is TextOptionItem)
                {
                    CategoryHeaderMasked header = GetHeader(index, option);
                    if (!header) continue;

                    bool enabled = visible && GameOptionsMenuPatch.AllParentsEnabledAndVisible(option.Parent, checkCollapsedSection: false);
                    header.transform.localPosition = new(-0.903f, num, -2f);
                    Place(header.gameObject, num, enabled);
                    if (visible) num -= 0.63f;
                    continue;
                }

                if (option.IsHeader && visible) num -= 0.18f;

                OptionBehaviour row = GetRow(index, option);
                if (!row) continue;

                row.transform.localPosition = new(0.952f, num, -2f);
                Place(row.gameObject, num, visible);
                if (visible) num -= 0.45f;
            }
            catch (Exception e) { Utils.ThrowException(e); }
        }

        // Rows of an earlier search that this one did not admit.
        foreach (KeyValuePair<int, OptionBehaviour> kv in Rows)
            if (kv.Value && !OptionSearch.IsResult(kv.Key) && kv.Value.gameObject.activeSelf) kv.Value.gameObject.SetActive(false);

        foreach (KeyValuePair<int, CategoryHeaderMasked> kv in Headers)
            if (kv.Value && !OptionSearch.IsResult(kv.Key) && kv.Value.gameObject.activeSelf) kv.Value.gameObject.SetActive(false);

        BoundsMax = -num - 1.65f;
        if (!Host.scrollBar) return;

        Host.scrollBar.SetYBoundsMax(BoundsMax);
        RefreshSelectables();
    }

    private static void Place(GameObject go, float y, bool logical)
    {
        Layout.Add(new Placed { Go = go, Y = y, Logical = logical });
        CullDirty = true;

        bool active = logical && GameOptionsMenuPatch.RowInBand(Container.transform, y);
        if (go.activeSelf != active) go.SetActive(active);
    }

    // Same viewport culling the tabs get (GameOptionsMenuPatch.ViewportCullPass): only rows near the
    // camera band stay active, re-applied when the list scrolls.
    public static void CullPass(GameOptionsMenu menu)
    {
        if (!OptionSearch.Active || !Host || menu != Host || !Container || Layout.Count == 0) return;

        Transform t = Container.transform;
        float rel = t.position.y;
        if (!CullDirty && Mathf.Abs(rel - LastCullRelY) < 0.02f) return;

        LastCullRelY = rel;
        CullDirty = false;

        var logicalCount = 0;
        var inBandCount = 0;

        foreach (Placed p in Layout)
        {
            if (!p.Logical || !p.Go) continue;

            logicalCount++;
            if (GameOptionsMenuPatch.RowInBand(t, p.Y)) inBandCount++;
        }

        // A band that rejects every visible row means the camera model is off — show them all instead
        // of an empty list.
        bool failOpen = logicalCount > 0 && inBandCount == 0;

        foreach (Placed p in Layout)
        {
            if (!p.Go) continue;

            bool active = p.Logical && (failOpen || GameOptionsMenuPatch.RowInBand(t, p.Y));
            if (p.Go.activeSelf != active) p.Go.SetActive(active);
        }
    }

    private static OptionBehaviour GetRow(int index, OptionItem option)
    {
        if (Rows.TryGetValue(index, out OptionBehaviour cached) && cached)
        {
            GameOptionsMenuPatch.ReassertRowValue(cached, option);
            return cached;
        }

        BaseGameSetting setting = GameOptionsMenuPatch.GetSetting(option);
        if (!setting) return null;

        OptionBehaviour row = setting.Type switch
        {
            OptionTypes.Checkbox => Object.Instantiate(Host.checkboxOrigin, Vector3.zero, Quaternion.identity, Container.transform),
            OptionTypes.String => Object.Instantiate(Host.stringOptionOrigin, Vector3.zero, Quaternion.identity, Container.transform),
            OptionTypes.Float or OptionTypes.Int => Object.Instantiate(Host.numberOptionOrigin, Vector3.zero, Quaternion.identity, Container.transform),
            _ => null
        };

        if (!row) return null;

        Rows[index] = row;
        RowIds.Add(row.GetInstanceID());

        row.transform.localPosition = new(0.952f, 0f, -2f);
        GameOptionsMenuPatch.OptionBehaviourSetSizeAndPosition(row, option, setting.Type);
        row.SetClickMask(Host.ButtonClickMask);
        row.SetUpFromData(setting, 20);
        row.OnValueChanged = new Action<OptionBehaviour>(Host.ValueChanged);

        ModGameOptionsMenu.OptionList[row] = index;
        return row;
    }

    private static CategoryHeaderMasked GetHeader(int index, OptionItem option)
    {
        if (!Headers.TryGetValue(index, out CategoryHeaderMasked header) || !header)
        {
            header = Object.Instantiate(Host.categoryHeaderOrigin, Vector3.zero, Quaternion.identity, Container.transform);
            header.SetHeader(StringNames.RolesCategory, 20);
            var text = header.transform.FindChild("HeaderText").GetComponent<TextMeshPro>();
            text.fontStyle = FontStyles.Bold | FontStyles.SmallCaps;
            text.fontWeight = FontWeight.Black;
            text.outlineWidth = 0.17f;
            header.transform.localScale = Vector3.one * 0.63f;
            Headers[index] = header;
        }

        header.Title.SetText(option.GetName(disableColor: true).Trim('★', ' '));
        header.Background.color = header.Divider.color = option.NameColor;
        return header;
    }

    // The menus (and with them the container and its rows) were destroyed.
    public static void OnUiCachePurged()
    {
        Rows.Clear();
        Headers.Clear();
        RowIds.Clear();
        Layout.Clear();
        Container = null;
        Host = null;
        HostInner = null;
        LastCullRelY = float.NaN;
    }
}
