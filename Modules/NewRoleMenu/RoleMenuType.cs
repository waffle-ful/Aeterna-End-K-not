namespace EndKnot;

public static class RoleMenuType
{
    public static float PxToFontSize = 1f;  // <-- PHASE 1 GATE: measured, then baked (§1.5)
    public static float Sz(float px) => px * PxToFontSize;

    // tier table (mock px):
    public const float CoreReadout = 42f; // .cval
    public const float DetailTitle = 27f; // .dh-name
    public const float SettingsTitle = 24f; // .sh-name
    public const float CoreUnit = 18f;
    public const float ValueTier = 16f;   // rn-name, rpct.v, rmax, numbox, sv, dd
    public const float Brand = 15f;
    public const float Body = 14f;        // tab, search, o-label, dinfo(13.5)
    public const float Label = 12f;       // toolbar/chips/footstats
    public const float Micro = 10.5f;     // cnt, tagc, rn-fac, colhdr, cathdr
    // weights: 700 = Bold/Black, 600 = SemiBold, 500 = Medium.
    // tabular nums: TMP IL2CPP lacks "tnum" feature -> use the embedded MONO digit font
    //   (§Open Decision 2) on readout/%/max/badges so right-aligned columns + drag readout don't jitter.
}
