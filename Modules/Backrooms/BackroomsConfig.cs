namespace EndKnot.Modules;

// Backrooms 作り直し (バニラ GPU 影基盤) のフラグ / チューナブル中央集約。
//
// Phase 1 は runtime フラグのみ (Main.cs の ConfigEntry 化は Phase 4)。既定 false なので
// 通常ビルドは従来のカスタム CPU 視界のまま = 完全な退行ガード。/bbshadow on と /bbtestroom が
// runtime でこのフラグを立て、custom 視界を抑制してバニラ影ドライバに切り替える。
public static class BackroomsConfig
{
    // 新旧マスタゲート。true でバニラ GPU 影ドライバ起動 + custom 視界抑制、false で従来のカスタム視界。
    // Phase 2b back-edge テスト中は true: 入場で自動的に「caster を壁の裏面に置く」版を画面検証。
    // /bbshadow off で従来のカスタム視界に戻せる。
    public static bool UseVanillaShadow = true;

    // 影 caster を載せるレイヤー (Constants.ShadowMask に含まれる Shadow レイヤー)。
    public const int ShadowCasterLayer = 10;

    // ls.SetViewDistance に渡す光半径。ShipStatus 依存の CalculateLightRadius を回避するため固定値。
    public const float DefaultShadowRadius = 5f;

    // 影の暗さ (ShadowQuad._Color) のバニラ既定値。/bbshadow dark の基準。
    public const float DefaultDarkLevel = 0.275f;

    // LightCutaway._EdgeBlur の既定。< 0 はバニラ既定のまま触らない。
    public const float DefaultEdgeBlur = -1f;

    // ShadowQuad._Mask = 「バニラ影を受けるスプライト」の bitmask。既定 3 はバニラ map/船 (Unlit/MaskShader) のみ。
    // 7 に広げると Backrooms タイル (Sprites/Default) も影を受ける (LevelImposter と同方式・実機確認済み)。
    // バニラ影は per-sprite 受信 (スクリーン overlay でなく _Mask gated) なので、これが Backrooms 影の鍵。
    public const float ShadowReceiveMask = 7f;
}
