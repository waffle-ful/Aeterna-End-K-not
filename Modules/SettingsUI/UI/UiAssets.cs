// Based on https://github.com/haru03741-glitch/TownOfHost_L Modules/SettingsMenu/UI/UiAssets.cs (GPL-3.0),
// which is based on https://github.com/haru03741-glitch/More-Roles-Plus src/UI/UiAssets.cs (GPL-3.0),
// which is based on https://github.com/Dolly1016/Nebula-Public NebulaPluginNova/VanillaAsset.cs (GPL-3.0)
using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace EndKnot.SettingsUI;

// 自前の画面の部品に使う本編の絵と字の型。本編の Twitch の窓 (どの場面にも常に居る・非表示) の子から抜く。
// - Box = 白い縁の黒い箱 (本編の値の欄・窓の背景と同じ絵)。9 分割で伸ばし、色は SpriteRenderer.color で縁に付く。
// - Blank = 1x1 の白 (幕・当たり判定の下地・スクロールの棒)。
// - 字の型 = 窓の本文の TextMeshPro (本編の字の材質と、無い字の代わりのフォントの並びが付いている)。
// 一度抜いたら捨てない (場面を跨いで使う)。Twitch の窓はメインメニューでしか見つからないので、メインメニューで抜いておく。
[HarmonyPatch]
internal static class UiAssets
{
    [HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.Start)), HarmonyPostfix]
    public static void OnMainMenu() => Load();

    private static Sprite _box, _blank;
    private static TextMeshPro _textProto;
    private static bool _loaded;
    private static readonly Dictionary<System.IntPtr, Material> Masked = new();

    public static Sprite Box => Load() ? _box : null;
    public static Sprite Blank => Load() ? _blank : null;
    public static TextMeshPro TextProto => Load() ? _textProto : null;

    // 縁の滑らかな白い丸 (直径 1 単位)。変えた所の点とスライダーのつまみ。本編に合う丸の絵が無いので式から焼く
    private static Sprite _circle;
    public static Sprite Circle => _circle ? _circle : _circle = BakeCircle();

    // 直径 1 単位・64 画素の丸を焼く (縁は半画素ぼかす)
    private static Sprite BakeCircle()
    {
        const int n = 64;
        var px = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float fx = (x + 0.5f) / n - 0.5f, fy = (y + 0.5f) / n - 0.5f;
            float a = Clamp01((0.5f - MathF.Sqrt(fx * fx + fy * fy)) * n);
            int i = (y * n + x) * 4;
            px[i] = px[i + 1] = px[i + 2] = 255;
            px[i + 3] = (byte)(a * 255f + 0.5f);
        }
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = "SettingsUiCircle", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        tex.LoadRawTextureData(px);
        tex.Apply(false, true);
        tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
        var sp = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), n);
        sp.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return sp;
    }

    // 中が透明な箱 (Box と同じ二重の縁・中だけ抜く)。後ろに敷いた絵を見せたい欄で使う。
    // 本編の箱の絵 (100px 四方・9 分割の縁 25px・ppu 100) を測った寸法: 外の白 3.5px → 黒 3px → 内の白 2px・角の半径 12px
    private static Sprite _hollow;
    public static Sprite Hollow => _hollow ? _hollow : _hollow = BakeHollow();

    private static Sprite BakeHollow()
    {
        const int n = 100;
        const float r = 12f, half = n / 2f;
        var px = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            // 角の丸い四角の縁からの内向きの距離 (px)
            float qx = MathF.Abs(x + 0.5f - half) - (half - r), qy = MathF.Abs(y + 0.5f - half) - (half - r);
            float ox = MathF.Max(qx, 0f), oy = MathF.Max(qy, 0f);
            float d = r - MathF.Sqrt(ox * ox + oy * oy) - MathF.Min(MathF.Max(qx, qy), 0f);
            float white = Band(d, 0f, 3.5f) + Band(d, 6.5f, 8.5f);
            float alpha = Clamp01(d + 0.5f) * (1f - Clamp01(d - 8.5f + 0.5f));
            int i = (y * n + x) * 4;
            byte c = (byte)(Clamp01(white) * 255f + 0.5f);
            px[i] = px[i + 1] = px[i + 2] = c;
            px[i + 3] = (byte)(alpha * 255f + 0.5f);
        }
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = "SettingsUiHollow", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        tex.LoadRawTextureData(px);
        tex.Apply(false, true);
        tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
        var sp = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(25f, 25f, 25f, 25f));
        sp.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return sp;
    }

    private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

    // d が [a, b) の内なら 1 (縁は半画素ぼかす)
    private static float Band(float d, float a, float b) => Clamp01(d - a + 0.5f) * Clamp01(b - d + 0.5f);

    // 本編のボタンの押した音と当てた音 (Twitch の窓の ExitGame のボタンの物)。無ければ鳴らさない
    public static AudioClip ClickSound { get; private set; }
    public static AudioClip HoverSound { get; private set; }

    // 抜いた絵はシーンの移り変わりで消えることがある。画面を開く時に 1 回だけ生きているかを見直し、消えていれば抜き直せるようにする
    public static void Revalidate()
    {
        if (_loaded && (!_box || !_blank || !_textProto)) _loaded = false;
    }

    public static bool Load()
    {
        // 抜き終えた後は Unity の生存確認をしない (部品を作るたびに何度も呼ばれる)
        if (_loaded) return true;
        // Instance は無い時に読むと空の物を作るので、置かれている物を名前で探す
        var tm = GameObject.Find("TwitchManager");
        var popup = tm && tm.transform.childCount > 0 ? tm.transform.GetChild(0) : null;
        if (!popup) return false;
        // 子の並びは版で変わりうるので名前で探す
        var bg = popup.Find("Background");
        var blank = popup.Find("BackroundIgnore");
        var text = popup.Find("Text_TMP");
        var exit = popup.Find("ExitGame");
        if (exit)
        {
            var pb = exit.GetComponent<PassiveButton>();
            if (pb && pb.ClickSound) ClickSound = pb.ClickSound;
            var rh = exit.GetComponent<ButtonRolloverHandler>();
            if (rh && rh.HoverSound) HoverSound = rh.HoverSound;
            if (ClickSound) ClickSound.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            if (HoverSound) HoverSound.hideFlags |= HideFlags.DontUnloadUnusedAsset;
        }
        if (!_box && bg) _box = bg.GetComponent<SpriteRenderer>()?.sprite;
        if (!_blank && blank) _blank = blank.GetComponent<SpriteRenderer>()?.sprite;
        if (!_textProto && text)
        {
            var src = text.GetComponent<TextMeshPro>();
            if (src)
            {
                _textProto = Object.Instantiate(src);
                _textProto.name = "SettingsUiTextProto";
                _textProto.text = "";
                _textProto.gameObject.SetActive(false);
                _textProto.gameObject.hideFlags = HideFlags.HideAndDontSave;
                Object.DontDestroyOnLoad(_textProto.gameObject);
            }
        }
        if (_box) _box.hideFlags |= HideFlags.DontUnloadUnusedAsset;
        if (_blank) _blank.hideFlags |= HideFlags.DontUnloadUnusedAsset;
        _loaded = _box && _blank && _textProto;
        return _loaded;
    }

    // スクロール欄 (SpriteMask) の内側だけに字を描く材質。元の材質ごとに 1 つを全員で使う
    public static Material MaskedFont(Material src)
    {
        if (!src) return src;
        if (Masked.TryGetValue(src.Pointer, out var m) && m) return m;
        m = new Material(src) { name = src.name + " (SettingsUiMasked)" };
        // SpriteMask は型板 (stencil) に 1 を書く。その内側 = 1 以下と比べて通る所
        m.SetFloat(ShaderUtilities.ID_StencilID, 1f);
        m.SetFloat(ShaderUtilities.ID_StencilComp, 4f);
        m.hideFlags = HideFlags.DontUnloadUnusedAsset;
        Masked[src.Pointer] = m;
        return m;
    }
}
