namespace EndKnot;

// Tunable layout constants for the two-pane role menu (Patches/NewRoleMenu/*).
// These are pure View geometry — change + rebuild to retune; no Model impact.
//
// Original single-column reference (GameOptionsMenuPatch.CreateSettings CoRoutine):
//   category header localPosition.x = -0.903, control row localPosition.x = 0.952, z = -2,
//   y cursor starts 2.0 and steps: header -0.63, sub-header gap -0.18, control row -0.45.
//   GameOptionsMenu canvas spans roughly x in [-6, 6] (ref: LobbyViewSettingsPane maskBg width ~12).
public static class NewRoleMenuLayout
{
    // ---- shared vertical rhythm (kept identical to the original renderer) ----
    public const float TopY = 0.5f;
    public const float RowStep = 0.45f;       // control row
    public const float HeaderStep = 0.63f;    // category header
    public const float SubHeaderGap = 0.18f;  // extra gap before an IsHeader option
    public const float ScrollBottomPad = 1.65f;
    public const float PosZ = -2.0f;

    // Space reserved at the top of the right pane for the L2 detail header chrome.
    // Sub-options start this many units below the selected master row Y.
    public const float DetailHeaderHeight = 1.2f;

    // ---- master pane (left): role rows + category headers ----
    // MasterRowX = -3.0 → world ≈ -3.96 (left of split ≈ -3.41); row label in left pane, value+buttons near split.
    public const float MasterRowX    = -3.0f;
    public const float MasterHeaderX = -4.85f; // original offset from row preserved (-1.85 local)

    // ---- detail pane (right): selected role's children ----
    public const float DetailRowX = 1.0f;
    public const float DetailHeaderX = 0.0f;

    // ---- consolidated settings tab (System + Mod + Task merged into one sectioned column) ----
    // Settings are flat top-level groups, not master-detail, so they use the original centre column.
    public const float SettingsRowX = 0.952f;
    public const float SettingsHeaderX = -0.903f;

    // ---- top tab bar (relocates GameSettingMenu's left tab strip to ONE compact top row) ----
    // After folding Mod/Task into the "設定" tab there are ~8 tabs (6 roles + 設定 + preset),
    // which fit a single compact centered row. Order: role tabs first, then 設定, then preset.
    public const float TopTabY = 2.55f;
    public const float TopTabStepX = 1.42f;
    public static readonly UnityEngine.Vector3 TopTabScale = new(0.33f, 0.3f, 1f);

    // ---- tab chrome (overlay on menu.transform, NOT button children) ----
    // ClipWyT: new MaskBg top edge (replaces ExpandedWyT=5.0 for clip only; panes stay at 5.0).
    // TopY is also pulled down to match so scroll content starts below the tab bar.
    public const float ClipWyT = 2.4f;

    // Tab chrome dimensions (world-space after factoring GameSettingMenu lossyScale ~1.0).
    public const float DotSize       = 0.14f;  // rounded dot sprite side length (world units)
    public const float DotOffsetY    = 0.18f;  // dot center relative to tab button center Y
    public const float BadgePadX     = 0.05f;  // badge horizontal padding
    public const float BadgeH        = 0.22f;  // badge pill height
    public const float UnderlineH    = 0.04f;  // active-tab underline thickness
    public const float UnderlineOffY = -0.28f; // underline center offset below button center Y
    public const float DividerH      = 0.55f;  // vertical divider height
    public const float DividerW      = 0.04f;  // vertical divider width
}
