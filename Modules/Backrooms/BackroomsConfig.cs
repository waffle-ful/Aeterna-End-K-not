namespace EndKnot.Modules;

// Backrooms 作り直し (バニラ GPU 影基盤) のフラグ / チューナブル中央集約。
//
// Phase 1 は runtime フラグのみ (Main.cs の ConfigEntry 化は Phase 4)。既定 false なので
// 通常ビルドは従来のカスタム CPU 視界のまま = 完全な退行ガード。/bbshadow on と /bbtestroom が
// runtime でこのフラグを立て、custom 視界を抑制してバニラ影ドライバに切り替える。
public static class BackroomsConfig
{
    // 新旧マスタゲート。true でバニラ GPU 影ドライバ起動 + custom 視界抑制、false で従来のカスタム視界。
    // Phase 2b で既定 true: 厚い壁マップ (UseOutlineMap) はバニラ影前提の設計なので入場で自動適用。
    // /bbshadow off で従来のカスタム視界に戻せる (ロールバック)。
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

    // ── 厚い壁マップ (Phase 2b: 輪郭線ベース作り直し) ──────────────────────────
    // 旧 1 セル壁では「壁の面が見える + 奥が暗い」がバニラ影で原理的に出せなかった (壁帯両側が床なので
    // near face で遮蔽 → 壁丸ごと影、または中心線 → 影が真ん中から)。壁を厚く (>=3 セル) して影 caster を
    // 壁中心 (interior) の輪郭に置くと、外側 1 セルが lit ring (見える壁の面)・中心が occluder (奥が暗い) になる。
    // 部屋 + 通路 + 厚い壁 = LevelImposter / Submerged と同型のマップ構造。

    // true で新しい厚い壁レイアウト (ClassifyCellOutline)、false で旧 1 セル壁 (ClassifyCellLegacy) に戻す (ロールバック用)。
    public static bool UseOutlineMap = true;

    // 部屋を隔てる壁帯の厚み (セル)。3 = [lit ring 1][dark interior 1][lit ring 1]。
    // interior が存在する (>=3) ことで caster が壁中心を通れる。大きいほど暗い分離帯が太く壁が多い (perf 増)。
    public const int WallThickness = 3;

    // 隣接部屋を繋ぐドア (床通路) の幅 (セル)。壁帯を貫通する。
    public const int DoorWidth = 2;
}
