namespace EndKnot;
using UnityEngine;

public static class RoleMenuTokens
{
    static Color32 H(byte r,byte g,byte b,byte a=255)=>new(r,g,b,a);

    // ---- dark 4-tone (the 60%) ----
    public static readonly Color32 Bg     = H(0x14,0x14,0x14);
    public static readonly Color32 E1     = H(0x1C,0x1C,0x1E);
    public static readonly Color32 E2     = H(0x23,0x23,0x26);
    public static readonly Color32 E3     = H(0x2C,0x2C,0x30);
    public static readonly Color32 Nest1  = H(0x23,0x23,0x26);
    public static readonly Color32 Nest2  = H(0x27,0x27,0x2A);
    public static readonly Color32 Nest3  = H(0x2B,0x2B,0x2E);
    public static readonly Color32 PageBg = H(0x0E,0x0E,0x0E);

    // ---- text (the 30%) ----
    public static readonly Color32 THi    = H(0xE6,0xE6,0xE6);
    public static readonly Color32 TMid   = H(0x99,0x99,0x99);
    public static readonly Color32 TDis   = H(0x61,0x61,0x61);

    // ---- lines ----
    public static readonly Color32 Line     = H(0x30,0x30,0x34);
    public static readonly Color32 LineSoft  = H(0x2A,0x2A,0x2D);

    // ---- faction (the 10% accents) ----
    public static readonly Color32 Imp   = H(0xE0,0x55,0x5A);
    public static readonly Color32 Crew  = H(0x45,0xC8,0xC0); // NOT cta
    public static readonly Color32 Neu   = H(0xE0,0xA3,0x3A);
    public static readonly Color32 Cov   = H(0xA0,0x6C,0xE0);
    public static readonly Color32 Addon = H(0x8A,0x8F,0x9A);
    public static readonly Color32 Mod   = H(0x6F,0xA0,0xF0);

    // faction tag TEXT colors
    public static readonly Color32 FacImpText  = H(0xF0,0x98,0x9B);
    public static readonly Color32 FacCrewText = H(0x8B,0xE2,0xDC);
    public static readonly Color32 FacNeuText  = H(0xEB,0xC0,0x7E);
    public static readonly Color32 FacCovText  = H(0xC6,0xA2,0xEF);
    public static readonly Color32 FacAddText  = H(0xC0,0xC4,0xCC);
    public static readonly Color32 FacModText  = H(0xA6,0xC4,0xF5);

    // ---- editing / state ----
    public static readonly Color32 Cta     = H(0x3F,0xB6,0xAE);
    public static readonly Color32 CtaDim  = H(0x2C,0x7E,0x78);
    public static readonly Color32 Edit    = H(0x4F,0xC3,0xBC);
    public static readonly Color32 WarnMod = H(0xD9,0xA4,0x41); // modified — distinct from star
    public static readonly Color32 Off     = H(0x5A,0x5A,0x60);
    public static readonly Color32 Focus   = H(0x7F,0xD8,0xFF);

    // ---- misc raw hexes ----
    public static readonly Color32 KnobRing  = H(0x0E,0x2A,0x28);
    public static readonly Color32 ToggleHandleOff = H(0xD8,0xD8,0xD8);
    public static readonly Color32 ToggleHandleOn  = H(0xEA,0xFB,0xF9);
    public static readonly Color32 ScrollThumb     = H(0x3A,0x3A,0x3E);
    public static readonly Color32 ChipOnText      = H(0xCF,0xF3,0xF0);
    public static readonly Color32 HlText          = H(0xCF,0xEF,0xFF);
    public static readonly Color32 StarFilled      = H(0xD9,0xB6,0x50); // ≠ WarnMod
    public static readonly Color32 GradHeaderTop   = H(0x22,0x22,0x26);
    public static readonly Color32 GradDark        = H(0x1A,0x1A,0x1C);
    public static readonly Color32 GradCoreEnd     = H(0x20,0x20,0x23);
    public static readonly Color32 Shadow          = H(0x00,0x00,0x00,90); // offset drop-shadow sprite

    public static Color32 FacFill(string fac)=>fac switch{
        "imp"=>new(0xE0,0x55,0x5A,41),"crew"=>new(0x45,0xC8,0xC0,41),
        "neu"=>new(0xE0,0xA3,0x3A,41),"cov"=>new(0xA0,0x6C,0xE0,41),
        "addon"=>new(0x8A,0x8F,0x9A,46),"mod"=>new(0x6F,0xA0,0xF0,41),_=>E2};

    // matching TEXT color for the SAME fac key (pill is two-color: fill + text)
    public static Color32 FacText(string fac)=>fac switch{
        "imp"=>FacImpText,"crew"=>FacCrewText,"neu"=>FacNeuText,
        "cov"=>FacCovText,"addon"=>FacAddText,"mod"=>FacModText,_=>TMid};

    public static Color32 FactionColor(TabGroup t)=>t switch{
        TabGroup.ImpostorRoles=>Imp, TabGroup.CrewmateRoles=>Crew,
        TabGroup.NeutralRoles=>Neu, TabGroup.CovenRoles=>Cov, _=>Mod};

    public static readonly Color32 ChipOnFill   = new(0x3F,0xB6,0xAE,36);
    public static readonly Color32 ModPillFill  = new(0xD9,0xA4,0x41,41);
    public static readonly Color32 HlFill       = new(0x7F,0xD8,0xFF,56);

    // alpha-multiply a color for per-child disabled-row dimming (~0.45)
    public static Color32 Dim(Color32 c, float f=0.45f) => new(c.r, c.g, c.b, (byte)(c.a * f));
}
