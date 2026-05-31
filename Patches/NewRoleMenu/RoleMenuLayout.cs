namespace EndKnot;
using UnityEngine;

// World-space coordinate system derived from docs/mockups/role-screen-refined.html (1440x860).
// Anchor: canvas x in [-6,6] (width 12u), unitsPerPx = 12/1440 = 0.008333.
public static class RoleMenuLayout
{
    public const float UnitsPerPx = 12f / 1440f;          // 0.0083333
    public static float Px(float px) => px * UnitsPerPx;

    // ---- canvas extents ----
    public const float CanvasLeft   = -6f;
    public const float CanvasRight  =  6f;
    public const float CanvasWidth  = 12f;
    public const float PosZ         = -2.0f;
    public const float ChromeZ      = -2.5f;
    public const float TopY         =  2.0f;

    // ---- vertical zones (top edge world-y) ----
    public const float HeaderBandY   =  3.30f;
    public const float TabBarY       =  2.85f;
    public const float ToolbarY      =  2.55f;
    public const float CtxRowY       =  2.30f;
    public const float ViewportBottomY = -0.5f;            // verified in-game 2026-05-31 (ExpandedWyB)
    public const float FooterY       = -3.30f;

    // ---- horizontal split: left 30% / right 70% of [-6,6], 1px shared divider ----
    public const float LeftPaneL   = -6.0f;
    public const float LeftPaneR   = -2.4f;
    public const float LeftPaneW   =  3.6f;
    public const float RightPaneL  = -2.4f;
    public const float RightPaneR  =  6.0f;
    public const float RightPaneW  =  8.4f;
    public const float DividerX    = -2.4f;

    // left content anchors
    public const float MasterFBarX   = -5.95f;
    public const float MasterToggleX = -5.55f;
    public const float MasterNameX   = -5.10f;
    public const float MasterPctX    = -2.95f;
    public const float MasterMaxX    = -2.55f;
    public const float MasterRowX    = -5.10f;            // legacy single-anchor for vanilla leaf host
    public const float MasterHeaderX = -5.10f;            // REQUIRED — referenced at NewRoleMenuView.cs:93/:228

    // right (detail) anchors
    public const float DetailHeadX   = -2.0f;
    public const float DetailCoreX   = -1.6f;
    public const float DetailOptX    = -1.6f;
    public const float DetailRowX    =  3.2f;             // legacy single-anchor for vanilla leaf host

    // consolidated settings (full width)
    public const float SettingsHeaderX = -5.0f;
    public const float SettingsRowX    = -4.6f;

    // ---- row pitch (mock --rowh: compact 32 / default 46 / relaxed 56) ----
    public const float RowStepCompact = 32f * UnitsPerPx; // 0.267
    public const float RowStep        = 46f * UnitsPerPx; // 0.383
    public const float RowStepRelaxed = 56f * UnitsPerPx; // 0.467
    public const float HeaderStep     = 0.55f;
    public const float SubHeaderGap   = 0.18f;
    public const float ScrollBottomPad= 1.65f;

    // ---- paddings (per-region, distinct — do NOT globalize) ----
    public static float PadSearch  => Px(8f);
    public static float PadListHdr => Px(8f);
    public static float PadColHdr  => Px(5f);
    public static float PadCatHdr  => Px(5f);
    public static float PadDetail  => Px(28f);
    public static float PadSettings=> Px(32f);
    public static float NestIndent => Px(16f);            // per-depth indent for L2 sub-options

    // ---- fixed-px atoms ----
    public static float FBarW      => Px(4f);
    public static float SelEdgeW   => Px(3f);
    public static float ColPctW    => Px(62f);
    public static float ColMaxW    => Px(46f);
    public static float ToggleSmW  => Px(36f);
    public static float ToggleSmH  => Px(20f);
    public static float ToggleFullW=> Px(42f);
    public static float ToggleFullH=> Px(24f);
    public static float SliderKnob => Px(18f);
    public static float SliderTrackH=>Px(10f);
    public static float ScrollBarW => Px(10f);
}
