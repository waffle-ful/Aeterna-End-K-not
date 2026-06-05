namespace EndKnot.Modules;

// Backrooms 作り直し (バニラ GPU 影基盤) のフラグ / チューナブル中央集約。
//
// Phase 1 は runtime フラグのみ (Main.cs の ConfigEntry 化は Phase 4)。既定 false なので
// 通常ビルドは従来のカスタム CPU 視界のまま = 完全な退行ガード。/bbshadow on と /bbtestroom が
// runtime でこのフラグを立て、custom 視界を抑制してバニラ影ドライバに切り替える。
public static class BackroomsConfig
{
    // 新旧マスタゲート。true でバニラ GPU 影ドライバ起動 + custom 視界抑制、false で従来通り。
    public static bool UseVanillaShadow;

    // 影 caster を載せるレイヤー (Constants.ShadowMask に含まれる Shadow レイヤー)。
    public const int ShadowCasterLayer = 10;

    // ls.SetViewDistance に渡す光半径。ShipStatus 依存の CalculateLightRadius を回避するため固定値。
    public const float DefaultShadowRadius = 5f;

    // 影の暗さ (ShadowQuad._Color) のバニラ既定値。/bbshadow dark の基準。
    public const float DefaultDarkLevel = 0.275f;

    // LightCutaway._EdgeBlur の既定。< 0 はバニラ既定のまま触らない。
    public const float DefaultEdgeBlur = -1f;
}
