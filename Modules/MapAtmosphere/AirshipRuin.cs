using System.Collections.Generic;
using UnityEngine;
using Kind = EndKnot.Modules.MapAtmosphere.AirshipRuinLayout.Kind;
using Placement = EndKnot.Modules.MapAtmosphere.AirshipRuinLayout.Placement;

namespace EndKnot.Modules.MapAtmosphere;

// The Airship の「見ていない所だけ朽ちている船」。AirshipLiminal が組み立て・毎フレーム更新・片付けを呼ぶ。
// 部屋の一枚絵のすぐ手前に汚れ (シミ・カビ・錆だれ・ひび・ほこり・脈打つ血管・濡れた粘液・破れたカーペット・
// 血の手形・壁で途切れる足跡・引っかき傷と正の字・髪の毛・蜘蛛の巣・息をする肉の瘤・きのこ・焦げ跡・火花を散らす配線・汚水・血を引きずった跡・血しぶき・錆・煤) を敷き、
// 家具を倒し (AirshipWreck)、棚や机の周りを荒らし、便器や家具を汚す (AirshipRansack)。
// 何をどこに置くかは AirshipRuinLayout が部屋ごとに決める (シャワー室だけは下の表で手置き)。
// 視界の外に見えている船は、ShadowCamera (layer 9〜12 を描く) の絵を ShadowQuad が暗くして貼ったもので、
// メインカメラは layer 10 を描かない。汚れを layer 10 に置くと影の中にだけ写り、光の届く所ではきれいな船のままになる。
// 視界の形 (壁の裏・扉の隙間) もバニラの影そのものなので、ピクセル単位で合う。
// 破損の間は同じ汚れをもう一組 layer 0 (メインカメラだけ) に重ねて濃くし、視界の中まで朽ちた船にする。
// 幽霊は影が無いので、その一組を常に出して船じゅう朽ちたまま見せる。
// 影の暗さ (ShadowQuad の _Color・バニラは灰 0.275) に赤みを差して、視界の外の船全体を赤く沈める。
// 視界の縁のぼかし (LightCutaway の _EdgeBlur) も広げて、見えている所と見えていない所の境目をぼやけさせる。
// 影の中の船は霞ませる: ShadowCamera の絵を縮めて広げ直したぼけた写しを ShadowQuad に貼るので、視界の外だけがピンぼけする。さらに layer 10 に大きなもやをカメラの周りで漂わせ、奥を見通せなくする。
// 朽ちた船が視界の中まで入り込む間と幽霊の間は、視界の中の船も別のカメラで写して同じだけぼかし、全画面で重ねる。
// 影の色・縁のぼかし・描き先は、どれも船を出る時に必ず戻す。
// 自分の画面の描き分けだけなので、他人の位置や出来事は何も映さない (送信ゼロ)。
internal static class AirshipRuin
{
    private const string Res = "EndKnot.Resources.Images.MapAtmosphere.";

    private const int ShadowOnlyLayer = 10;

    // 脈やツヤを動かすのは、カメラからこの距離までの物だけ (画面の外で動かしても見えない)。
    private const float AnimateRange = 9f;

    // シャワー室の汚れ (手置き・ワールド座標)。x, y, 幅, 高さ (0=画像の縦横比), 種類, 絵の番号, 回転, 濃さ
    private static readonly (float X, float Y, float W, float H, Kind Kind, int V, float Rot, float A)[] Spots =
    [
        // シャワー室: 奥の区画 (白タイルの壁と水色の床)
        (21.65f, 3.2f, 0.32f, 0.62f, Kind.Drip, 0, 0f, 0.9f),
        (22.48f, 3.2f, 0.32f, 0.62f, Kind.Drip, 1, 0f, 0.8f),
        (23.27f, 3.2f, 0.32f, 0.62f, Kind.Drip, 0, 0f, 0.7f),
        (24.1f, 3.2f, 0.32f, 0.62f, Kind.Drip, 1, 0f, 0.9f),
        (20.5f, 3.15f, 0.85f, 0f, Kind.Mold, 0, 0f, 1f),
        (24.6f, 2.3f, 0.95f, 0f, Kind.Mold, 2, 40f, 1f),
        (20.55f, 2.25f, 0.75f, 0f, Kind.Mold, 1, 0f, 1f),
        (22.1f, 2.2f, 0.6f, 0f, Kind.Mold, 1, 120f, 0.9f),
        (23.8f, 2.95f, 0.5f, 0f, Kind.Mold, 0, 200f, 0.85f),
        (21.2f, 2.5f, 0.9f, 0f, Kind.Stain, 0, 0f, 1f),
        (23.0f, 2.45f, 1.1f, 0f, Kind.Stain, 2, 70f, 1f),
        (21.0f, 2.95f, 0.55f, 0f, Kind.Mold, 2, 300f, 0.9f),
        (22.9f, 3.05f, 0.5f, 0f, Kind.Mold, 1, 20f, 0.85f),
        (24.2f, 3.1f, 0.6f, 0f, Kind.Mold, 0, 90f, 0.9f),
        (24.0f, 2.2f, 0.8f, 0f, Kind.Stain, 1, 200f, 0.9f),
        // シャワー室: 上のタオル置き場
        (19.2f, 4.8f, 1.2f, 0f, Kind.Stain, 1, 30f, 1f),
        (17.6f, 4.7f, 0.9f, 0f, Kind.Crack, 0, 0f, 1f),
        (18.0f, 5.05f, 0.3f, 0.5f, Kind.Drip, 1, 0f, 0.7f),
        (17.5f, 5.3f, 0.28f, 0.5f, Kind.Drip, 0, 0f, 0.8f),
        (20.8f, 4.7f, 0.7f, 0f, Kind.Mold, 1, 60f, 0.9f),
        // シャワー室: 左の暗い床
        (18.8f, 1.8f, 1.3f, 0f, Kind.Stain, 3, 0f, 1f),
        (19.6f, 2.4f, 1.0f, 0f, Kind.Crack, 1, 50f, 1f),
        (17.8f, 2.9f, 0.8f, 0f, Kind.Mold, 2, 150f, 0.9f),
        (17.5f, 0.8f, 1.1f, 0f, Kind.Stain, 2, 10f, 1f),
        (18.3f, -0.5f, 1.2f, 0f, Kind.Crack, 0, 250f, 1f),
        // シャワー室: ロッカーと手前の床
        (20.9f, 1.3f, 0.3f, 0.7f, Kind.Drip, 0, 0f, 0.6f),
        (22.6f, 1.3f, 0.3f, 0.7f, Kind.Drip, 1, 0f, 0.6f),
        (19.5f, -1.2f, 1.4f, 0f, Kind.Stain, 0, 160f, 1f),
        (24.3f, -0.5f, 0.8f, 0f, Kind.Mold, 2, 0f, 1f),
        (21.5f, -2.2f, 1.0f, 0f, Kind.Stain, 3, 90f, 1f),
        (23.5f, -0.4f, 0.9f, 0f, Kind.Crack, 0, 110f, 1f),
        (20.5f, -0.8f, 1.2f, 0f, Kind.Stain, 1, 300f, 1f),
        (23.0f, -1.9f, 1.3f, 0f, Kind.Stain, 2, 130f, 0.9f),
        (22.0f, -2.3f, 0.7f, 0f, Kind.Mold, 0, 0f, 0.9f),
        (19.2f, 0.2f, 1.1f, 0f, Kind.Crack, 1, 190f, 1f),
        // 血管: カビとシミから這い広がり、心臓のように二度ずつ脈打つ
        (20.5f, 3.05f, 1.5f, 0f, Kind.Vein, 0, 20f, 0.95f),
        (24.5f, 2.4f, 1.7f, 0f, Kind.Vein, 1, 160f, 0.95f),
        (17.8f, 2.9f, 1.4f, 0f, Kind.Vein, 2, 80f, 0.9f),
        (22.0f, -2.2f, 1.6f, 0f, Kind.Vein, 0, 250f, 0.95f),
        (24.3f, -0.5f, 1.3f, 0f, Kind.Vein, 1, 330f, 0.9f),
        (19.3f, 4.75f, 1.4f, 0f, Kind.Vein, 2, 200f, 0.9f),
        // 粘液: 壁を垂れる筋と床の溜まり。ツヤだけ別の絵でゆっくり揺らす
        (22.05f, 3.15f, 0.4f, 0.75f, Kind.Slime, 0, 0f, 0.95f),
        (23.7f, 3.15f, 0.4f, 0.75f, Kind.Slime, 1, 0f, 0.9f),
        (21.75f, 1.25f, 0.38f, 0.8f, Kind.Slime, 1, 0f, 0.85f),
        (18.4f, 5.2f, 0.35f, 0.6f, Kind.Slime, 0, 0f, 0.85f),
        (22.5f, 2.35f, 1.2f, 0f, Kind.Pool, 0, 10f, 0.95f),
        (18.9f, 1.1f, 1.4f, 0f, Kind.Pool, 1, 200f, 0.95f),
        (21.0f, -1.6f, 1.3f, 0f, Kind.Pool, 0, 110f, 0.9f),
        // 排水口に絡んだ髪、タイルに残る手形、壁の手前で途切れる足跡
        (22.3f, 2.05f, 0.5f, 0f, Kind.Hair, 0, 0f, 0.95f),
        (19.4f, 0.6f, 0.45f, 0f, Kind.Hair, 1, 80f, 0.9f),
        (21.2f, 3.35f, 0.32f, 0f, Kind.Hand, 1, -10f, 0.9f),
        (24.0f, 3.45f, 0.3f, 0f, Kind.Hand, 0, 15f, 0.85f),
        (20.2f, 1.9f, 1.5f, 0f, Kind.Feet, 1, 75f, 0.85f),
        (24.9f, 0.9f, 0.45f, 0f, Kind.Shroom, 0, 0f, 0.9f),
        (17.4f, 1.9f, 0.4f, 0f, Kind.Shroom, 1, 0f, 0.9f),
        // 部屋全体のくすみと、隅の濃い汚れ
        (20.86f, 1.62f, 8.7f, 9.0f, Kind.Dust, 0, 0f, 0.6f),
        (18.3f, 2.0f, 4f, 4f, Kind.Dust, 0, 90f, 0.5f),
        (22.8f, -1.3f, 4f, 3f, Kind.Dust, 0, 180f, 0.5f)
    ];

    private const float DecalZ = 4.9f; // シャワー室の一枚絵 (z=5) のすぐ手前。人や小物 (z≒0) の下に入る
    private const float DustZ = 4.95f;

    // 影を暗く・赤く寄せすぎると汚れまで赤一色に溶けるので、灰に赤みを差す程度にとどめる。
    private const float VanillaShadow = 0.275f;
    private static readonly Color ShadowRed = new(0.46f, 0.27f, 0.25f, 1f);
    private static readonly Color ShadowInvaded = new(0.52f, 0.22f, 0.2f, 1f);
    private static Color _shadowTint = ShadowRed;

    private const float EdgeBlurScale = 2.5f;

    // 影の中の絵をどこまで縮めてから広げ直すか (1 = ぼかさない・小さいほどぼける)。
    // ShadowCamera の描き先は 512x512 と元から粗く、全画面に引き伸ばされている。半分 (256x256) まで縮めると
    // 升目が目に付くモザイクになるので、七割ほどにとどめる。半分より小さくする時は半分ずつ段を踏む。
    private const float HazeScale = 0.7f;
    private static float _hazeScale = HazeScale, _hazeBuiltScale = -1f;
    private static Camera _shadowCam;
    private static Material _hazeMat;
    private static RenderTexture _hazeOrig, _hazeOut;
    private static RenderTexture[] _hazeSteps = [];
    private static bool _hazeWarned;

    // 視界の外を覆うもや (カメラの周りを漂う大きな塊)。x, y はカメラからのずれ (画面の幅・高さ比)、大きさは画面の幅比。
    private static readonly (float X, float Y, float Size, float SpeedX, float SpeedY)[] Veils =
    [
        (-0.3f, 0.2f, 1.1f, 0.05f, 0.037f),
        (0.35f, -0.15f, 1.2f, 0.041f, 0.053f),
        (0.1f, 0.35f, 0.9f, 0.063f, 0.029f),
        (-0.2f, -0.3f, 1.0f, 0.033f, 0.047f)
    ];

    private const float VeilAlpha = 0.55f;
    private const float VeilZ = -1f; // 船の絵・汚れ・小物よりも手前
    private static float _veilAlpha = VeilAlpha;
    private static SpriteRenderer[] _veils = [];
    private static float _veilUnits = 1f; // もやの絵の幅 (単位)。毎フレーム sprite.rect を読まないために組み立て時に控える
    private static bool _invadeOn, _peopleOn, _shadowCopyOn, _peopleSync; // 板 / 人カメラ / 影の写し が出ているか、0.25 秒ごとの追従が必要か
    private static float _shownVeil = -1f;

    // 朽ちた船が視界の中まで入り込む間と幽霊の間は、視界の中も影の中と同じぼけた絵で覆う。
    // ShadowCamera の絵は視界の形が黒く塗り抜かれている (ShadowQuad はそこを透かして視界を見せる) ので、視界の中の写しには使えない。
    // 代わりにメインカメラの複製で、人 (Players 層) 以外の船の全部 (部屋の絵・汚れ・家具・小物) を同じ大きさの描き先へ描き、
    // 影と同じ割合でぼかして貼る。複製は覆う間だけ動かす。描いた絵は半透明の重なりで透明度が 1 を割ることがあるので、
    // UI/Default の _TextureSampleAdd で透明度を 1 に底上げして貼る (板の濃さは _Color の透明度で決める)。
    // 板はメインカメラの子にしてカメラに遅れず重ね、カメラの写す範囲いっぱいに広げて一番手前に置く。
    // 船の外の雲 (CloudGenerator) は ShadowQuad より手前にあるので、それより手前でないと引いて見た時に雲だけくっきり残る。
    // ShadowQuad の子にはできない (奥行きの拡大率が 0 で、子を奥へずらせない)。
    // 板は人も影も覆うので、人だけを別のカメラでメインカメラの後にくっきり描き直し、同じカメラで ShadowQuad の写し
    // (専用の層) を人の手前に重ねる。影の中の人は影で隠れたままになる。
    // 人と家具の前後は、人がいつも手前になる (家具の奥を歩く人も家具の上に描かれる)。
    private const float InvadeHazeZ = -900f;
    private const int ShadowCopyLayer = 31; // メインカメラも ShadowCamera も描かない層
    private static int _playerMask;
    private static Camera _peopleCam;
    private static GameObject _shadowCopy;
    private static MeshRenderer _shadowCopyRenderer;
    private static GameObject _invadeGo;
    private static MeshRenderer _invadeRenderer;
    private static Material _invadeMat;
    private static Mesh _invadeMesh;
    private static float _shownInvadeHaze = -1f;
    private static Camera _invadeCam;
    private static RenderTexture _invadeSrc, _invadeOut;
    private static RenderTexture[] _invadeSteps = [];
    private static bool _invadeWarm;

    private static GameObject _root;
    private static SpriteRenderer[] _shadow, _light, _sheenShadow, _sheenLight;
    private static Transform[] _shadowTf, _lightTf;
    private static Vector3[] _baseScale;
    private static float[] _alpha, _threshold, _shown, _phase, _x, _y;
    private static bool[] _breathes, _sparks;
    private static Color[] _tint;
    private static int[] _animated = [];
    private static float _probe, _clock, _shownLit, _shownFade = -1f, _shownDecay = -1f, _shownInvade = -1f, _forcedDecay = -1f;
    private static bool _ghost;
    private static Material _shadowMat, _blurMat;
    private static Color _shadowOrig;
    private static float _blurOrig, _blurScale = EdgeBlurScale;

    // 朽ち跡の素材 (ruin_*.png 約130枚) はイントロ中に数枚ずつ先読みし、船を降りたらテクスチャごと常駐から外す。
    private static string[] _preloadNames;
    private static int _preloadNext;

    public static void Preload()
    {
        _preloadNames ??= System.Array.FindAll(System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceNames(),
            n => n.StartsWith(Res + "ruin_", System.StringComparison.Ordinal) && n.EndsWith(".png", System.StringComparison.Ordinal));
        int end = System.Math.Min(_preloadNames.Length, _preloadNext + 6);
        for (; _preloadNext < end; _preloadNext++) Utils.LoadSprite(_preloadNames[_preloadNext], 100f);
    }

    public static bool Preloaded => _preloadNext > 0;

    public static void ReleaseSprites()
    {
        _preloadNext = 0;
        int n = Utils.ReleaseSprites(Res);
        if (n > 0) Logger.Info($"released {n} map atmosphere sprites", "AirshipLiminal");
    }

    public static void Build(ShipStatus ship)
    {
        Transform shipTf = ship.transform;
        _root = new GameObject("EK_AirshipRuin") { layer = 0 };
        _root.transform.SetParent(shipTf, false);
        Vector3 ls = shipTf.lossyScale;
        _root.transform.localScale = FxMath.V3(1f / ls.x, 1f / ls.y, 1f / ls.z);

        AirshipRuinLayout.Build(ship);
        var all = new List<Placement>(AirshipRuinLayout.Placements.Count + Spots.Length);
        for (int i = 0; i < Spots.Length; i++)
        {
            (float x, float y, float w, float h, Kind kind, int v, float rot, float a) = Spots[i];
            all.Add(new Placement
            {
                X = x, Y = y, Z = kind == Kind.Dust ? DustZ : DecalZ - i * 0.0005f, W = w, H = h,
                Kind = kind, V = v, Rot = rot, A = a, Tint = FxMath.Rgba(1f, 1f, 1f)
            });
        }

        all.AddRange(AirshipRuinLayout.Placements);

        int n = all.Count;
        _shadow = new SpriteRenderer[n];
        _light = new SpriteRenderer[n];
        _sheenShadow = new SpriteRenderer[n];
        _sheenLight = new SpriteRenderer[n];
        _shadowTf = new Transform[n];
        _lightTf = new Transform[n];
        _baseScale = new Vector3[n];
        _alpha = new float[n];
        _threshold = new float[n];
        _shown = new float[n];
        _phase = new float[n];
        _x = new float[n];
        _y = new float[n];
        _tint = new Color[n];
        _breathes = new bool[n];
        _sparks = new bool[n];
        var animated = new List<int>();

        for (int i = 0; i < n; i++)
        {
            Placement p = all[i];
            string file = p.Kind switch
            {
                Kind.Stain => $"ruin_stain{p.V}.png",
                Kind.Mold => $"ruin_mold{p.V}.png",
                Kind.Drip => $"ruin_drip{p.V}.png",
                Kind.Crack => $"ruin_crack{p.V}.png",
                Kind.Vein => $"ruin_vein{p.V}.png",
                Kind.Slime => $"ruin_slime{p.V}.png",
                Kind.Pool => $"ruin_pool{p.V}.png",
                Kind.Tear => $"ruin_tear{p.V}.png",
                Kind.Hand => $"ruin_hand{p.V}.png",
                Kind.Feet => $"ruin_feet{p.V}.png",
                Kind.Scratch => $"ruin_scratch{p.V}.png",
                Kind.Hair => $"ruin_hair{p.V}.png",
                Kind.Web => $"ruin_web{p.V}.png",
                Kind.Growth => $"ruin_growth{p.V}.png",
                Kind.Shroom => $"ruin_shroom{p.V}.png",
                Kind.Burn => $"ruin_burn{p.V}.png",
                Kind.Wire => $"ruin_wire{p.V}.png",
                Kind.Sewage => $"ruin_sewage{p.V}.png",
                Kind.Smear => $"ruin_smear{p.V}.png",
                Kind.Spatter => $"ruin_spatter{p.V}.png",
                Kind.Rust => $"ruin_rust{p.V}.png",
                Kind.Soot => $"ruin_soot{p.V}.png",
                _ => "ruin_dust.png"
            };

            Sprite sprite = Utils.LoadSprite(Res + file, 100f);
            if (!sprite) continue;

            float sw = sprite.rect.width / 100f, sh = sprite.rect.height / 100f;
            float sx = p.W / sw;
            float sy = p.H > 0f ? p.H / sh : sx;
            Vector3 pos = FxMath.V3(p.X, p.Y, p.Z);
            Vector3 scale = FxMath.V3(sx, sy, 1f);

            _shadow[i] = Make($"Ruin{i}", ShadowOnlyLayer, sprite, pos, scale, p.Rot);
            _light[i] = Make($"Ruin{i}Lit", 0, sprite, pos, scale, p.Rot);
            _shadowTf[i] = _shadow[i].transform;
            _lightTf[i] = _light[i].transform;
            _baseScale[i] = scale;
            _alpha[i] = p.A;
            _tint[i] = p.Tint;
            _x[i] = p.X;
            _y[i] = p.Y;
            _phase[i] = FxMath.Range(0f, 10f);

            // 粘液のツヤ・配線の火花は、同じ大きさの別の絵を重ねて明るさだけ動かす。
            if (p.Kind is Kind.Slime or Kind.Pool or Kind.Sewage or Kind.Wire)
            {
                Sprite sheen = Utils.LoadSprite(Res + file.Replace(".png", p.Kind == Kind.Wire ? "_spark.png" : "_sheen.png"), 100f);
                if (sheen)
                {
                    Vector3 sp = FxMath.V3(pos.x, pos.y, pos.z - 0.0002f);
                    _sheenShadow[i] = Make($"Ruin{i}Sheen", ShadowOnlyLayer, sheen, sp, scale, p.Rot);
                    _sheenLight[i] = Make($"Ruin{i}SheenLit", 0, sheen, sp, scale, p.Rot);
                }
            }

            _breathes[i] = p.Kind == Kind.Growth;
            _sparks[i] = p.Kind == Kind.Wire;
            if (p.Kind is Kind.Vein or Kind.Growth || _sheenShadow[i]) animated.Add(i);
            // 三分の二は最初から、残りは試合が長引くにつれて一つずつ増える。
            _threshold[i] = p.Kind == Kind.Dust || FxMath.Value < 0.66f ? 0f : FxMath.Range(0.1f, 0.9f);
        }

        _animated = animated.ToArray();
        _clock = 0f;
        _probe = 0f;
        _shownFade = _shownDecay = _shownInvade = -1f;
        _shownLit = 0f;
        _ghost = false;
        _shadowMat = null;
        _blurMat = null;
        Sprite mist = Utils.LoadSprite(Res + "ruin_mist.png", 100f);
        _veils = new SpriteRenderer[mist ? Veils.Length : 0];
        _veilUnits = mist ? mist.rect.width / 100f : 1f;
        for (int i = 0; i < _veils.Length; i++)
            _veils[i] = Make($"Veil{i}", ShadowOnlyLayer, mist, FxMath.V3(0f, 0f, VeilZ - i * 0.001f), FxMath.V3(1f, 1f, 1f), FxMath.Range(0f, 360f));

        _shownVeil = -1f;
        _hazeWarned = false;
        AirshipVermin.Build(_root.transform, AirshipRuinLayout.Grids);
        AirshipWreck.Build(ship, _root.transform);
        AirshipRansack.Build(ship, _root.transform);
        Logger.Info($"AirshipRuin built decals={n} animated={_animated.Length}", "AirshipLiminal");
    }

    private static SpriteRenderer Make(string name, int layer, Sprite sprite, Vector3 pos, Vector3 scale, float rot)
    {
        var go = new GameObject(name) { layer = layer };
        Transform tf = go.transform;
        tf.SetParent(_root.transform, false);
        tf.position = pos;
        tf.localRotation = FxMath.RotZ(rot);
        tf.localScale = scale;
        SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.color = default;
        return sr;
    }

    // fadeIn: 組み立て直後のフェード (0→1)。invade: 視界の中への侵食 (0=視界はきれい・1=視界の中まで朽ちる)。decay: 試合の長さ (0→1)。
    // camX/camY: カメラの位置 (近くの物だけ動かすため)。
    // 幽霊かどうかは 0.25 秒ごとに読む (毎フレーム PlayerControl を引かない)。
    public static bool Ghost => _ghost;

    // main: 呼び出し側がこのフレームに確かめたメインカメラ。camW/camH: その写す範囲 (単位)。
    // interop の値型 getter は 1 本ごとに il2cpp 側のゴミになるので、カメラの寸法は呼び出し側で 1 回だけ読んで受け取る。
    public static void Animate(float dt, float fadeIn, float invade, float decay, Camera main, float camW, float camH, float camX, float camY)
    {
        if (_root is null) return;

        if ((_probe -= dt) <= 0f)
        {
            _probe = 0.25f;
            PlayerControl lp = PlayerControl.LocalPlayer;
            _ghost = lp && lp.Data != null && lp.Data.IsDead;
            _peopleSync = true;
            BlurVisionEdge(lp);
            HazeShadow();
            PlaceInvadeHaze();
        }

        Blur(_hazeOrig, _hazeSteps, _hazeOut);
        DriftVeils(fadeIn, camW, camH, camX, camY);
        // 視界の中は、朽ちた船が入り込んでいる間だけぼかす。幽霊は影が無く船じゅう朽ちて見えるので、画面全体を常にぼかす。
        ShowInvadeHaze(main, camW, camH, _ghost ? fadeIn : invade * fadeIn);

        if (_ghost) invade = 1f;
        if (_forcedDecay >= 0f) decay = _forcedDecay;
        // 試合の長さは連続して増えるので、刻んでから比べる (毎フレーム全部の汚れの色を塗り直さないため)。
        decay = (int)(decay * 200f) / 200f;
        if (fadeIn != _shownFade || decay != _shownDecay || invade != _shownInvade)
        {
            // 視界の中の濃さが変わった時だけ全部塗り直す。それ以外は濃さの変わった汚れ (しきい値を跨いだもの) だけ塗る。
            bool relight = invade != _shownInvade;
            _shownFade = fadeIn;
            _shownDecay = decay;
            _shownInvade = invade;
            _shownLit = invade;

            for (int i = 0; i < _shadow.Length; i++)
            {
                if (_shadow[i] is null) continue;
                float grow = _threshold[i] <= 0f ? 1f : FxMath.Clamp01((decay - _threshold[i]) / 0.1f);
                float shown = _alpha[i] * fadeIn * grow;
                if (!relight && shown == _shown[i]) continue;
                _shown[i] = shown;
                SetAlpha(i, 1f, 1f);
            }

            TintShadow(fadeIn, invade);
        }

        AirshipVermin.Animate(dt, fadeIn, invade, camX, camY);
        AirshipWreck.Animate(fadeIn, invade, decay);
        AirshipRansack.Animate(fadeIn, invade, decay);

        // 血管の脈・肉の瘤の息・粘液のツヤ・配線の火花だけは毎フレーム動かす。
        _clock += dt;
        foreach (int i in _animated)
        {
            if (_shown[i] <= 0f) continue;
            float dx = _x[i] - camX, dy = _y[i] - camY;
            if (dx * dx + dy * dy > AnimateRange * AnimateRange) continue;

            if (_sparks[i])
            {
                // 火花: 数秒おきに「バチッ、バチッ」と二度光る。間隔は配線ごとにずらす。
                float t = FxMath.Repeat(_clock + _phase[i] * 3f, 2.2f + _phase[i] * 0.35f);
                float spark = FxMath.Exp(-(t - 0.04f) * (t - 0.04f) / 0.0006f) + 0.7f * FxMath.Exp(-(t - 0.19f) * (t - 0.19f) / 0.0004f);
                SetAlpha(i, 1f, FxMath.Clamp01(spark));
                continue;
            }

            if (_sheenShadow[i] is not null)
            {
                // ツヤ: 二つの遅い波を重ねて、ぬめりが光を拾っては失うように揺らす。
                float w = 0.5f + 0.3f * FxMath.Sin(_clock * 1.3f + _phase[i]) + 0.2f * FxMath.Sin(_clock * 3.1f + _phase[i] * 2f);
                SetAlpha(i, 1f, 0.3f + 0.7f * FxMath.Clamp01(w));
                continue;
            }

            Vector3 sc = _baseScale[i];
            Vector3 scaled;
            float body;
            if (_breathes[i])
            {
                // 肉の瘤: 3.4 秒かけてゆっくり息を吸って吐く。吸う時は横より縦に膨らむ。
                float br = 0.5f + 0.5f * FxMath.Sin((_clock + _phase[i]) * 1.85f);
                br *= br;
                scaled = FxMath.V3(sc.x * (1f + 0.04f * br), sc.y * (1f + 0.07f * br), 1f);
                body = 0.9f + 0.1f * br;
            }
            else
            {
                // 脈: 1.7 秒ごとに「ドクン、ドクン」と二度膨らむ。
                float t = FxMath.Repeat(_clock + _phase[i], 1.7f);
                float beat = FxMath.Exp(-(t - 0.1f) * (t - 0.1f) / 0.004f) + 0.6f * FxMath.Exp(-(t - 0.36f) * (t - 0.36f) / 0.006f);
                float k = 1f + 0.07f * beat;
                scaled = FxMath.V3(sc.x * k, sc.y * k, 1f);
                body = 0.7f + 0.3f * beat;
            }

            _shadowTf[i].localScale = scaled;
            if (_shownLit > 0f) _lightTf[i].localScale = scaled;
            SetAlpha(i, body, 1f);
        }
    }

    private static void SetAlpha(int i, float bodyMul, float sheenMul)
    {
        float a = _shown[i] * bodyMul;
        Color c = _tint[i];
        // 描画物はルートの子で、ルートごと壊れるまで生きている。存在の有無は managed の null 比較で足りる。
        _shadow[i].color = FxMath.Rgba(c.r, c.g, c.b, a);
        if (_light[i] is not null) _light[i].color = FxMath.Rgba(c.r, c.g, c.b, a * _shownLit);
        if (_sheenShadow[i] is null) return;
        float sa = _shown[i] * sheenMul;
        _sheenShadow[i].color = FxMath.Rgba(1f, 1f, 1f, sa);
        _sheenLight[i].color = FxMath.Rgba(1f, 1f, 1f, sa * _shownLit);
    }

    // バニラが材質を作り直しても追従できるよう、0.25 秒ごとに掛け直す。
    private static void BlurVisionEdge(PlayerControl lp)
    {
        if (!lp || !lp.lightSource) return;
        Material m = lp.lightSource.LightCutawayMaterial;
        if (!m) return;
        // 材質が前回と同じなら持っている性質も同じ (名前で引く HasProperty は文字列を渡すたびに il2cpp 側へ複製される)。
        if (m != _blurMat)
        {
            if (!m.HasProperty(EdgeBlurId)) return;
            if (!_blurMat) _blurOrig = m.GetFloat(EdgeBlurId);
            _blurMat = m;
            Logger.Info($"ruin edge blur orig={_blurOrig:0.###}", "AirshipLiminal");
        }

        m.SetFloat(EdgeBlurId, _blurOrig * _blurScale);
    }

    private static readonly int EdgeBlurId = Shader.PropertyToID("_EdgeBlur");
    private static readonly int ColorId = Shader.PropertyToID("_Color");

    // ShadowCamera が描いた絵を、半分・四分の一と縮めてから元の大きさへ広げ直した写しを ShadowQuad に貼る。
    // 一段ずつ補間して縮め・広げるので、一気に引き伸ばした時のような角ばったにじみにならず、滑らかにぼける。
    // 写しは毎フレーム描き直す。バニラは画面の縦横比が変わると描き先を作り直すので、0.25 秒ごとに見て作り直しに追従する。
    private static void HazeShadow()
    {
        if (!HudManager.InstanceExists || !HudManager.Instance.ShadowQuad) return;
        if (!_shadowCam)
        {
            ShadowCollab collab = Object.FindObjectOfType<ShadowCollab>();
            if (!collab || !collab.ShadowCamera) return;
            _shadowCam = collab.ShadowCamera;
        }

        RenderTexture cur = _shadowCam.targetTexture;
        if (!cur) return;
        if (_hazeOut && cur == _hazeOrig && _hazeBuiltScale == _hazeScale) return;

        Material m = HudManager.Instance.ShadowQuad.material;
        if (!m) return;
        Texture shown = m.mainTexture;
        if (shown != cur && !(_hazeOut && shown == HazeShown()))
        {
            if (_hazeWarned) return;
            _hazeWarned = true;
            Logger.Warn($"ruin haze skipped: ShadowQuad does not show the shadow camera ({(shown ? shown.name : "null")})", "AirshipLiminal");
            return;
        }

        ReleaseHaze(false);
        _hazeOrig = cur;
        _hazeMat = m;
        _hazeBuiltScale = _hazeScale;
        var ratios = new List<float>();
        float r = 1f;
        while (r * 0.5f > _hazeScale + 0.001f)
        {
            r *= 0.5f;
            ratios.Add(r);
        }

        if (_hazeScale < 0.999f) ratios.Add(_hazeScale);
        _hazeSteps = new RenderTexture[ratios.Count];
        for (int i = 0; i < _hazeSteps.Length; i++)
            _hazeSteps[i] = NewHazeTexture(System.Math.Max(8, (int)(cur.width * ratios[i])), System.Math.Max(8, (int)(cur.height * ratios[i])), cur.format);

        _hazeOut = NewHazeTexture(cur.width, cur.height, cur.format);
        m.mainTexture = _hazeSteps.Length > 0 ? HazeShown() : cur;

        // 視界の中の写しも同じ大きさ・同じ割合でぼかす (視界の縁で内と外のぼけ方が揃う)。
        _invadeSrc = NewHazeTexture(cur.width, cur.height, cur.format);
        _invadeSteps = new RenderTexture[_hazeSteps.Length];
        for (int i = 0; i < _invadeSteps.Length; i++)
            _invadeSteps[i] = NewHazeTexture(_hazeSteps[i].width, _hazeSteps[i].height, cur.format);
        _invadeOut = NewHazeTexture(cur.width, cur.height, cur.format);
        _invadeWarm = false;
        Logger.Info($"ruin haze {cur.width}x{cur.height} scale={_hazeScale:0.##} steps={_hazeSteps.Length}", "AirshipLiminal");
    }

    private static RenderTexture NewHazeTexture(int w, int h, RenderTextureFormat format)
    {
        var rt = new RenderTexture(w, h, 0, format)
        {
            name = "EK_RuinHaze",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };
        rt.hideFlags |= HideFlags.HideAndDontSave;
        return rt;
    }

    // 一番小さい描き先から一段ずつ広げ直す (複製は一番小さい描き先へ直接描いてある)。一段だけなら板がそのまま引き伸ばす。
    private static void BlurUp(RenderTexture[] steps, RenderTexture dst)
    {
        if (!dst || steps.Length < 2) return;
        Texture src = steps[^1];
        for (int i = steps.Length - 2; i >= 0; i--)
        {
            Graphics.Blit(src, steps[i]);
            src = steps[i];
        }

        Graphics.Blit(src, dst);
    }

    private static void Blur(Texture orig, RenderTexture[] steps, RenderTexture dst)
    {
        if (!dst || !orig || steps.Length == 0) return;
        Texture src = orig;
        foreach (RenderTexture step in steps)
        {
            Graphics.Blit(src, step);
            src = step;
        }

        // 一段だけなら縮めた絵を ShadowQuad がそのまま引き伸ばして貼る (広げ直す手間を省く)。
        if (steps.Length == 1) return;
        for (int i = steps.Length - 2; i >= 0; i--)
        {
            Graphics.Blit(src, steps[i]);
            src = steps[i];
        }

        Graphics.Blit(src, dst);
    }

    // ShadowQuad に貼る影のぼけた写し。一段だけなら縮めた絵そのもの。
    private static Texture HazeShown() => _hazeSteps.Length == 1 ? _hazeSteps[0] : _hazeOut;

    // restore: ShadowQuad をバニラの絵へ戻す (船を出る時)。作り直しの途中では戻さずに捨てるだけ。
    private static void ReleaseHaze(bool restore)
    {
        if (restore && _hazeMat && _hazeOrig && _hazeOut && _hazeMat.mainTexture == HazeShown()) _hazeMat.mainTexture = _hazeOrig;
        if (_invadeCam) _invadeCam.targetTexture = null;
        if (_invadeMat) _invadeMat.mainTexture = null;

        foreach (RenderTexture rt in _hazeSteps) Destroy(rt);
        foreach (RenderTexture rt in _invadeSteps) Destroy(rt);
        Destroy(_hazeOut);
        Destroy(_invadeSrc);
        Destroy(_invadeOut);
        _hazeSteps = [];
        _invadeSteps = [];
        _invadeSrc = _invadeOut = null;
        _invadeWarm = false;
        _hazeOut = null;
        _hazeOrig = null;
        _hazeMat = null;
        _hazeBuiltScale = -1f;

        static void Destroy(RenderTexture rt)
        {
            if (!rt) return;
            rt.Release();
            Object.Destroy(rt);
        }
    }

    // 視界の中を覆う板を ShadowQuad と同じ大きさで作り、位置と貼る絵を合わせ直す (0.25 秒ごと)。
    // メインカメラごと作り直されたら子の板と複製も消えるので、次の呼び出しで作り直す。
    private static void PlaceInvadeHaze()
    {
        if (!HudManager.InstanceExists) return;
        MeshRenderer sq = HudManager.Instance.ShadowQuad;
        Camera main = Camera.main;
        if (!sq || !main) return;
        Transform sqTf = sq.transform, camTf = main.transform;

        if (!_invadeGo)
        {
            ReleaseInvadeHaze();
            Shader shader = Shader.Find("UI/Default");
            if (!shader) return;

            // 大きさ 1 の四角を作り、毎フレームカメラの写す範囲いっぱいに広げる (拡大・縮小しても画面の端まで覆う)。
            // UI/Default は頂点色を掛けるので、白を明示する (無いと真っ黒や透明になる環境がある)。
            const float x0 = -0.5f, x1 = 0.5f, y0 = -0.5f, y1 = 0.5f;
            _invadeMesh = new Mesh { name = "EK_RuinInvadeHaze" };
            _invadeMesh.vertices = new[] { FxMath.V3(x0, y0, 0f), FxMath.V3(x1, y0, 0f), FxMath.V3(x0, y1, 0f), FxMath.V3(x1, y1, 0f) };
            _invadeMesh.uv = new[] { FxMath.V2(0f, 0f), FxMath.V2(1f, 0f), FxMath.V2(0f, 1f), FxMath.V2(1f, 1f) };
            _invadeMesh.colors = new[] { Color.white, Color.white, Color.white, Color.white };
            _invadeMesh.triangles = new[] { 0, 2, 1, 2, 3, 1 };
            _invadeMesh.RecalculateBounds();
            _invadeMesh.hideFlags |= HideFlags.HideAndDontSave;

            _invadeGo = new GameObject("EK_RuinInvadeHaze") { layer = 0 };
            _invadeGo.transform.SetParent(camTf, false);
            _invadeGo.AddComponent<MeshFilter>().sharedMesh = _invadeMesh;
            _invadeMat = new Material(shader) { name = "EK_RuinInvadeHaze" };
            _invadeMat.hideFlags |= HideFlags.HideAndDontSave;
            _invadeMat.SetVector("_TextureSampleAdd", new Vector4(0f, 0f, 0f, 1f));
            _invadeRenderer = _invadeGo.AddComponent<MeshRenderer>();
            _invadeRenderer.sharedMaterial = _invadeMat;
            _invadeRenderer.enabled = false;
            _shownInvadeHaze = -1f;
            Logger.Info($"ruin invade haze built sqZ={sqTf.position.z:0.##}", "AirshipLiminal");
        }

        if (!_invadeCam)
        {
            var camGo = new GameObject("EK_RuinInvadeCam");
            _invadeCam = camGo.AddComponent<Camera>();
            _invadeCam.CopyFrom(main);
            camGo.transform.SetParent(main.transform, false);
            camGo.transform.localPosition = FxMath.V3(0f, 0f, 0f);
            camGo.transform.localRotation = FxMath.RotZ(0f);
            _invadeCam.enabled = false;
            _invadeOn = false;
            _invadeWarm = false;
        }

        // 板より奥の全部を描く (板そのものは写さない。人は cullingMask で外す)。
        _invadeCam.depth = main.depth - 1f;
        _invadeCam.clearFlags = CameraClearFlags.SolidColor;
        _invadeCam.nearClipPlane = InvadeHazeZ + 0.01f - _invadeCam.transform.position.z;
        _invadeCam.farClipPlane = main.farClipPlane;
        // 複製は一番小さい描き先へ直接描く (大きく描いてから縮める手間を省く)。一段だけなら、それを板が引き伸ばして貼るだけでぼける。
        RenderTexture target = _invadeSteps.Length > 0 ? _invadeSteps[^1] : _invadeSrc;
        if (_invadeCam.targetTexture != target) _invadeCam.targetTexture = target;
        Texture tex = _invadeSteps.Length > 1 ? _invadeOut : target;
        if (tex && _invadeMat.mainTexture != tex) _invadeMat.mainTexture = tex;
    }

    // camW/camH: メインカメラの写す範囲 (単位)。呼び出し側が読んだ値を受け取る。
    private static void ShowInvadeHaze(Camera main, float camW, float camH, float a)
    {
        // 何も出ていなくて出す必要もない時は、部品の生存確認すらしない (定常時の毎フレーム経路)。
        if (a <= 0f && !_invadeOn) return;
        if (!_invadeRenderer) return;
        if (!_invadeCam || !_invadeSrc || !_invadeCam.targetTexture) a = 0f;

        if (a > 0f)
        {
            if (main)
            {
                _invadeCam.aspect = camW / camH;
                _invadeCam.orthographicSize = camH * 0.5f;
                // 板はカメラの写す範囲いっぱい (正射影なので奥へずらしても画面上の大きさは変わらない)。
                Transform planeTf = _invadeGo.transform, camTf = main.transform;
                Vector3 camScale = camTf.lossyScale;
                float h = camH * 1.02f, w = camW * 1.02f;
                planeTf.localPosition = FxMath.V3(0f, 0f, InvadeHazeZ - camTf.position.z);
                planeTf.localScale = FxMath.V3(w / camScale.x, h / camScale.y, 1f);
                // 空の色は毎フレーム変わりうる (染まる演出)。描く層は出す時に一度だけ合わせる。
                _invadeCam.backgroundColor = main.backgroundColor;
            }

            // 動かし始めたフレームはまだ何も描けていないので、次のフレームから貼る (黒い一瞬を出さない)。
            if (!_invadeOn)
            {
                EnsurePlayerMask();
                if (main) _invadeCam.cullingMask = main.cullingMask & ~_playerMask & ~(1 << ShadowCopyLayer);
                _invadeGo.transform.localRotation = FxMath.RotZ(0f);
                _invadeCam.enabled = true;
                _invadeOn = true;
                _invadeWarm = false;
            }
            else
            {
                BlurUp(_invadeSteps, _invadeOut);
                _invadeWarm = true;
            }

            if (!_invadeWarm) a = 0f;
        }
        else if (_invadeOn)
        {
            if (_invadeCam) _invadeCam.enabled = false;
            _invadeOn = false;
            _invadeWarm = false;
        }

        ShowPeople(main, camW, camH, a > 0f);
        if (a == _shownInvadeHaze) return;
        _shownInvadeHaze = a;
        _invadeRenderer.enabled = a > 0f;
        if (a > 0f) _invadeMat.color = FxMath.Rgba(1f, 1f, 1f, a);
    }

    private static void EnsurePlayerMask()
    {
        if (_playerMask != 0) return;
        int players = LayerMask.NameToLayer("Players");
        _playerMask = 1 << (players >= 0 ? players : 8);
        int ghost = LayerMask.NameToLayer("Ghost");
        if (ghost >= 0) _playerMask |= 1 << ghost;
    }

    // 板が出ている間だけ、人と ShadowQuad の写しをメインカメラの後に描く。
    private static void ShowPeople(Camera main, float camW, float camH, bool on)
    {
        MeshRenderer sq = on && HudManager.InstanceExists ? HudManager.Instance.ShadowQuad : null;
        if (!on || !main || !sq)
        {
            if (_peopleOn && _peopleCam) _peopleCam.enabled = false;
            if (_shadowCopyOn && _shadowCopy) _shadowCopy.SetActive(false);
            _peopleOn = _shadowCopyOn = false;
            return;
        }

        EnsurePlayerMask();

        if (!_peopleCam)
        {
            _peopleOn = false; // 作り直したカメラは設定し直す
            var camGo = new GameObject("EK_RuinPeopleCam");
            _peopleCam = camGo.AddComponent<Camera>();
            _peopleCam.CopyFrom(main);
            camGo.transform.SetParent(main.transform, false);
            camGo.transform.localPosition = FxMath.V3(0f, 0f, 0f);
            camGo.transform.localRotation = FxMath.RotZ(0f);
            _peopleCam.clearFlags = CameraClearFlags.Nothing;
            _peopleCam.targetTexture = null;
        }

        // 描く順・描く層・奥行きは出す時と 0.25 秒ごとに合わせる。写す範囲だけ毎フレーム追う (ズームで変わる)。
        bool sync = !_peopleOn || _peopleSync;
        _peopleSync = false;
        if (sync)
        {
            _peopleCam.depth = main.depth + 0.5f;
            _peopleCam.nearClipPlane = main.nearClipPlane;
            _peopleCam.farClipPlane = main.farClipPlane;
            int mask = main.cullingMask;
            _peopleCam.cullingMask = (mask & _playerMask) | (1 << ShadowCopyLayer);
            if (_invadeOn) _invadeCam.cullingMask = mask & ~_playerMask & ~(1 << ShadowCopyLayer);
        }

        _peopleCam.aspect = camW / camH;
        _peopleCam.orthographicSize = camH * 0.5f;
        if (!_peopleOn) _peopleCam.enabled = true;
        _peopleOn = true;

        // ShadowQuad の写し: 同じ板・同じ材質を同じ位置に重ねる (幽霊で影が消えている時は消す)。
        // 親ごと壊されていたら (0.25 秒ごとに確かめる) 作り直す。
        if (sync && !_shadowCopy) _shadowCopy = null;
        if (_shadowCopy is null)
        {
            MeshFilter mf = sq.GetComponent<MeshFilter>();
            if (mf && mf.sharedMesh)
            {
                _shadowCopy = new GameObject("EK_RuinShadowCopy") { layer = ShadowCopyLayer };
                _shadowCopy.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
                _shadowCopyRenderer = _shadowCopy.AddComponent<MeshRenderer>();
                _shadowCopyOn = true;
                sync = true;
            }
            else return;
        }

        bool shadowOn = sq.enabled && sq.gameObject.activeInHierarchy;
        if (_shadowCopyOn != shadowOn)
        {
            _shadowCopy.SetActive(shadowOn);
            _shadowCopyOn = shadowOn;
        }

        if (!shadowOn || !sync) return;
        // ShadowQuad と同じ親の下に置けば、カメラが動いても同じフレームで一緒に動く。
        Transform st = sq.transform, ct = _shadowCopy.transform;
        if (ct.parent != st.parent) ct.SetParent(st.parent, false);
        ct.localPosition = st.localPosition;
        ct.localRotation = st.localRotation;
        ct.localScale = st.localScale;
        if (_shadowCopyRenderer.sharedMaterial != sq.sharedMaterial) _shadowCopyRenderer.sharedMaterial = sq.sharedMaterial;
        // 描く順も ShadowQuad に揃える (揃えないと名前の文字が影より後に描かれ、影の中の人の名前が透ける)。
        _shadowCopyRenderer.sortingLayerID = sq.sortingLayerID;
        _shadowCopyRenderer.sortingOrder = sq.sortingOrder;
    }

    private static void ReleaseInvadeHaze()
    {
        if (_invadeCam) Object.Destroy(_invadeCam.gameObject);
        _invadeCam = null;
        if (_peopleCam) Object.Destroy(_peopleCam.gameObject);
        _peopleCam = null;
        if (_shadowCopy) Object.Destroy(_shadowCopy);
        _shadowCopy = null;
        _shadowCopyRenderer = null;
        _invadeWarm = false;
        if (_invadeGo) Object.Destroy(_invadeGo);
        if (_invadeMat) Object.Destroy(_invadeMat);
        if (_invadeMesh) Object.Destroy(_invadeMesh);
        _invadeGo = null;
        _invadeRenderer = null;
        _invadeMat = null;
        _invadeMesh = null;
        _shownInvadeHaze = -1f;
        _invadeOn = _peopleOn = _shadowCopyOn = false;
    }

    // もやの塊をカメラの周りでゆっくり漂わせる。影の中 (layer 10) にだけ写るので、視界の中はきれいなまま。
    private static void DriftVeils(float fadeIn, float w, float h, float camX, float camY)
    {
        if (_veils.Length == 0) return;

        float a = _veilAlpha * fadeIn;
        bool recolor = a != _shownVeil;
        _shownVeil = a;

        for (int i = 0; i < _veils.Length; i++)
        {
            SpriteRenderer sr = _veils[i];
            if (sr is null) continue;
            (float x, float y, float size, float sx, float sy) = Veils[i];
            float t = _clock + i * 7.3f;
            float px = camX + w * (x + 0.25f * FxMath.Sin(t * sx * 6.28f));
            float py = camY + h * (y + 0.25f * FxMath.Cos(t * sy * 6.28f));
            Transform tf = sr.transform;
            tf.position = FxMath.V3(px, py, VeilZ - i * 0.001f);
            float s = w * size / _veilUnits;
            tf.localScale = FxMath.V3(s, s * 0.8f, 1f);
            if (recolor) sr.color = FxMath.Rgba(1f, 1f, 1f, a);
        }
    }

    private static void TintShadow(float fadeIn, float invade)
    {
        if (!_shadowMat)
        {
            if (!HudManager.InstanceExists || !HudManager.Instance.ShadowQuad) return;
            Material m = HudManager.Instance.ShadowQuad.material;
            if (!m || !m.HasProperty(ColorId)) return;
            _shadowMat = m;
            _shadowOrig = m.GetColor(ColorId);
        }

        float r = FxMath.Lerp(FxMath.Lerp(_shadowOrig.r, _shadowTint.r, fadeIn), ShadowInvaded.r, invade);
        float g = FxMath.Lerp(FxMath.Lerp(_shadowOrig.g, _shadowTint.g, fadeIn), ShadowInvaded.g, invade);
        float b = FxMath.Lerp(FxMath.Lerp(_shadowOrig.b, _shadowTint.b, fadeIn), ShadowInvaded.b, invade);
        _shadowMat.SetColor(ColorId, FxMath.Rgba(r, g, b));
    }

    // TestBridge から、試合の長さを固定して汚れの増え方と空の色を確かめる。負の値で解除。
    public static float ForcedDecay => _forcedDecay;

    public static bool DebugDecay(float decay)
    {
        if (!_root) return false;
        _forcedDecay = decay;
        _shownDecay = -1f;
        return true;
    }

    // TestBridge から視界の縁のぼかしの倍率を差し替えて見比べる。
    public static bool DebugBlur(float scale)
    {
        if (!_root) return false;
        _blurScale = scale;
        return true;
    }

    // TestBridge から影の中の霞を差し替えて見比べる。scale: 縮める割合 (1=ぼかさない・0.5=半分)。veil: もやの濃さ。
    public static bool DebugHaze(float scale, float veil)
    {
        if (!_root) return false;
        _hazeScale = System.Math.Clamp(scale, 0.05f, 1f);
        _veilAlpha = FxMath.Clamp01(veil);
        return true;
    }

    // TestBridge から影の色を差し替えて見比べる。
    public static bool DebugShadow(float r, float g, float b)
    {
        if (!_root) return false;
        _shadowTint = FxMath.Rgba(r, g, b);
        _shownFade = -1f;
        return true;
    }

    public static void Teardown()
    {
        if (_shadowMat) _shadowMat.SetColor("_Color", _shadowOrig);
        else if (HudManager.InstanceExists && HudManager.Instance.ShadowQuad)
        {
            // 影の材質を取る前に船ごと壊れた時も、赤く残っていればバニラの灰色へ戻す。
            Material m = HudManager.Instance.ShadowQuad.material;
            if (m && m.HasProperty("_Color"))
            {
                Color c = m.GetColor("_Color");
                if (c.r - c.g > 0.02f) m.SetColor("_Color", FxMath.Rgba(VanillaShadow, VanillaShadow, VanillaShadow));
            }
        }

        if (_blurMat) _blurMat.SetFloat("_EdgeBlur", _blurOrig);
        ReleaseHaze(true);
        ReleaseInvadeHaze();
        _shadowCam = null;
        _hazeScale = HazeScale;
        _veilAlpha = VeilAlpha;
        _veils = [];
        _shadowMat = null;
        _blurMat = null;
        _blurScale = EdgeBlurScale;
        AirshipVermin.Teardown();
        AirshipWreck.Teardown();
        AirshipRansack.Teardown();
        AirshipRuinLayout.Clear();

        if (_root) Object.Destroy(_root);
        _root = null;
        _shadow = _light = _sheenShadow = _sheenLight = null;
        _breathes = _sparks = null;
        _shadowTf = _lightTf = null;
        _animated = [];
        _forcedDecay = -1f;
        _shadowTint = ShadowRed;
    }
}
