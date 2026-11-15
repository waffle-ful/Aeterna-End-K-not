namespace EndKnot;

// Kill-switch + shared state for the redesigned two-pane role/options menu.
//
// The new menu lives entirely in Patches/NewRoleMenu/* and is reached only through a few
// pure-addition dispatch hooks in GameOptionsMenuPatch (CreateSettingsPrefix / ReCreateSettings),
// each gated on Active. With Active == false the original single-column renderer runs unchanged,
// so the menu can be rolled back at runtime without touching the Model (OptionItem) at all.
//
// Scope: the two-pane layout only ever intercepts the six role tabs
// (TabGroup.ImpostorRoles .. TabGroup.OtherRoles). Settings tabs (System/Game/Task) and the
// PresetExplorer always fall through to the existing renderer — they are flat top-level option
// groups with no Parent/Children master-detail structure.
public static class NewRoleMenuState
{
    // Master switch. Default false = original menu (two-stage rollout; flip to true to preview).
    // NOTE: temporarily true for Phase 1 in-game verification (faithful-copy sanity check).
    // Revert to false before committing / when not actively previewing.
    public static bool Active = false;

    // Index into OptionItem.AllOptions of the currently selected master row (role / top-level
    // option), or -1 when nothing is selected. Used by the detail pane in later phases.
    public static int SelectedIndex = -1;

    // ---- L2 detail header state ----
    // settingsContainer local-Y of the selected master row — written during Build/Reflow,
    // read by RebuildDetailHead to position the header at the same Y.
    public static float SelectedHeaderY;

    // SelectedIndex value at the last RebuildDetailHead call — used in Reflow to skip
    // rebuilding when the selection hasn't changed (only value changed, not selection).
    public static int LastBuiltSelectedIndex = -2; // -2 so first build always triggers

    // Destroyed + rebuilt on every selection change (L2 lifecycle).
    // NOT in SpawnedUI — has its own teardown path; DestroyOption clears it too.
    public static readonly System.Collections.Generic.List<UnityEngine.GameObject> DetailObjects = new();

    // ---- Selection bar geometry (set by BuildPaneBackgrounds, read by Build/Reflow) ----
    // The thin faction-color left bar that marks the selected master row.
    // Placed at the far-left edge of the left pane (world x ≈ wxL), which is empty space outside
    // the vanilla StringOption frame — so it is always visible above the pane background.
    public static float BarLocalX   = -5.38f; // default; overwritten by BuildPaneBackgrounds
    public static float BarWidth    = 0.07f;  // width in settingsContainer local space
    public static float BarHeight   = 0.42f;  // height (slightly less than RowStep for gap)
    public static int   BarSortLayerID;
    public static int   BarSortOrder;         // = pane sortingOrder + 1 (just above pane, behind content)

    // Per-master-row faction bars created by Build(), updated by Reflow().
    // Keyed by OptionItem.AllOptions index. Cleared on each Build() entry.
    public static readonly System.Collections.Generic.Dictionary<int, UnityEngine.SpriteRenderer> SelBars = new();

    // Active underline SpriteRenderers per tab, created by RoleMenuTabBar.Decorate().
    // Cleared in the same two places as ModSettingsButtons (DestroySetting + StartPostfix).
    public static readonly System.Collections.Generic.Dictionary<TabGroup, UnityEngine.SpriteRenderer> TabUnderlines = new();
}
