// Based on https://github.com/haru03741-glitch/TownOfHost_L Modules/SettingsMenu/UI/MenuArt.cs (GPL-3.0),
// which is based on https://github.com/haru03741-glitch/More-Roles-Plus src/Options/MenuArt.cs (GPL-3.0)
using System;
using System.Collections.Generic;
using UnityEngine;

namespace EndKnot.SettingsUI;

// 設定画面の飾りの絵 (角の丸い板・虫眼鏡)。白い絵を 1 枚だけ作り、色は SpriteRenderer.color で付ける。
// 角の丸い板は 9 分割で伸ばすので、大きさの違う板も同じ絵を使い回す (丸みの半径ごとに Sprite だけ作る)。
// 一度作ったら捨てない (シーンを跨いで使う)。
internal static class MenuArt
{
    private const int Size = 64, Radius = 28;
    private static Texture2D _roundTex, _glassTex;
    private static readonly Dictionary<int, Sprite> Rounds = new();
    private static Sprite _glass;
    private static Material _masked;

    // 角の半径が radius (世界の単位) の板。大きさは size で決める (2 × radius 以上)
    public static Sprite Round(float radius)
    {
        int key = (int)(radius * 1000f + 0.5f);
        if (Rounds.TryGetValue(key, out var sp) && sp) return sp;
        var tex = RoundTex();
        sp = Sprite.Create(tex, new Rect(0, 0, Size, Size), new Vector2(0.5f, 0.5f), Radius / radius, 0,
            SpriteMeshType.FullRect, new Vector4(Radius, Radius, Radius, Radius));
        sp.hideFlags = HideFlags.DontUnloadUnusedAsset;
        Rounds[key] = sp;
        return sp;
    }

    // 虫眼鏡 (幅 0.3 単位)
    public static Sprite Glass()
    {
        if (_glass) return _glass;
        _glassTex = Bake(32, (x, y) =>
        {
            // 輪 (中心 (13,19)・半径 9・太さ 3.2) と、右下へ伸びる柄
            float dx = x - 13f, dy = y - 19f;
            float ring = Math.Abs(MathF.Sqrt(dx * dx + dy * dy) - 9f) - 1.6f;
            float t = Math.Clamp(((x - 19f) - (y - 13f)) / 16f, 0f, 1f);
            float hx = x - (19f + 8f * t), hy = y - (13f - 8f * t);
            float handle = MathF.Sqrt(hx * hx + hy * hy) - 2.2f;
            return Math.Min(ring, handle);
        });
        _glass = Sprite.Create(_glassTex, new Rect(0, 0, 32, 32), new Vector2(0.5f, 0.5f), 32f / 0.3f);
        _glass.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return _glass;
    }

    // スクロールする欄の中に置く板のマテリアル。本編の行の背景 (層ごとに切り抜く) を 1 回だけ写して使い回す
    public static Material Masked(SpriteRenderer vanillaMasked)
    {
        if (_masked || !vanillaMasked || !vanillaMasked.sharedMaterial) return _masked;
        _masked = new Material(vanillaMasked.sharedMaterial) { name = "SettingsUiMaskedPanel" };
        _masked.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return _masked;
    }

    // parent の子に板を 1 枚置く。material が null なら SpriteRenderer の既定のまま
    public static SpriteRenderer Panel(Transform parent, string name, float radius, Vector2 size, Vector3 pos, Color c, Material material)
    {
        var go = new GameObject(name);
        go.layer = parent.gameObject.layer;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        var sr = go.AddComponent<SpriteRenderer>();
        if (material) sr.sharedMaterial = material;
        sr.sprite = Round(radius);
        sr.drawMode = SpriteDrawMode.Sliced;
        sr.size = size;
        sr.color = c;
        return sr;
    }

    private static Texture2D RoundTex()
    {
        if (_roundTex) return _roundTex;
        // 中央 8 画素は平ら・四隅は半径 28 の四分円
        const float lo = Radius, hi = Size - Radius;
        _roundTex = Bake(Size, (x, y) =>
        {
            float cx = Math.Clamp(x, lo, hi), cy = Math.Clamp(y, lo, hi);
            float dx = x - cx, dy = y - cy;
            return MathF.Sqrt(dx * dx + dy * dy) - (Radius - 0.5f);
        });
        return _roundTex;
    }

    // 距離 d (内側が負) から白の不透明度を作る。画素の中心で測る
    private static Texture2D Bake(int n, Func<float, float, float> dist)
    {
        var px = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float d = dist(x + 0.5f, y + 0.5f);
            float a = Math.Clamp(0.5f - d, 0f, 1f);
            int i = (y * n + x) * 4;
            px[i] = px[i + 1] = px[i + 2] = 255;
            px[i + 3] = (byte)(a * 255f + 0.5f);
        }
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = "SettingsUiMenuArt" };
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.LoadRawTextureData(px);
        tex.Apply(false, true);
        tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return tex;
    }
}
