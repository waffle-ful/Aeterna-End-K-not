using System;
using System.Collections.Generic;
using UnityEngine;

namespace EndKnot.SettingsUI;

// 設定画面の夜空の絵を式から焼く (読み込む素材は無い)。
// 一度焼いた絵は同じ大きさの間は使い回す (画面を開くたびに焼き直さない)。
// 大きな配列は SetPixels でなく LoadRawTextureData で渡す (il2cpp 側に一時配列を作らない)
internal static class SpaceArt
{
    private const int SkyW = 1024, NebulaW = 320;

    private static readonly Dictionary<long, Sprite> Cache = new();
    private static Sprite _streak;

    // 角の丸い四角の夜空 (幅 worldW・高さ worldH・角の半径 radius、すべて世界の単位)。
    // 上は深い紺・下は少し紫に寄せ、暗い小さな星を焼き込む。sprite の 1 単位 = 世界の 1 単位
    public static Sprite Sky(float worldW, float worldH, float radius)
    {
        int h = Math.Max(8, (int)(SkyW * worldH / worldW + 0.5f));
        long key = 1L << 40 | (long)SkyW << 20 | (uint)h;
        if (Cache.TryGetValue(key, out var cached) && cached) return cached;

        int w = SkyW;
        float u = worldW / w; // 1 画素の世界の大きさ
        var rgb = new float[w * h * 3];
        var alpha = new float[w * h];
        for (int y = 0; y < h; y++)
        {
            float fy = (y + 0.5f) / h;
            // 上 (fy=1) ほど暗く青く・下ほど紫を少し
            float r = Lerp(0.085f, 0.035f, fy), g = Lerp(0.062f, 0.05f, fy), b = Lerp(0.19f, 0.13f, fy);
            for (int x = 0; x < w; x++)
            {
                float fx = (x + 0.5f) / w;
                // 縁へ向かって少し沈める (中身の周りが真ん中より明るく見えないように)
                float vx = fx * 2f - 1f, vy = fy * 2f - 1f;
                float vig = 1f - 0.22f * Clamp01((vx * vx + vy * vy - 0.35f) / 1.65f);
                int i = y * w + x;
                rgb[i * 3] = r * vig;
                rgb[i * 3 + 1] = g * vig;
                rgb[i * 3 + 2] = b * vig;
                float px = (fx - 0.5f) * worldW, py = (fy - 0.5f) * worldH;
                alpha[i] = Clamp01(0.5f - RoundRectDist(px, py, worldW * 0.5f, worldH * 0.5f, radius) / u);
            }
        }

        // 星: 暗い物がほとんどで、明るい物はまれ (べき乗で偏らせる)
        var rng = new System.Random(4242);
        int count = w * h / 700;
        for (int s = 0; s < count; s++)
        {
            float cx = (float)rng.NextDouble() * w, cy = (float)rng.NextDouble() * h;
            float k = (float)rng.NextDouble();
            float bright = 0.1f + 0.55f * k * k * k * k * k * k;
            float sig = 0.45f + 0.55f * MathF.Pow((float)rng.NextDouble(), 4f);
            float tint = (float)rng.NextDouble();
            // 淡い青から淡いクリームまで
            float sr = Lerp(0.72f, 1f, tint), sg = Lerp(0.82f, 0.93f, tint), sb = Lerp(1f, 0.82f, tint);
            int x0 = (int)cx, y0 = (int)cy;
            for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
            {
                int x = x0 + dx, y = y0 + dy;
                if (x < 0 || y < 0 || x >= w || y >= h) continue;
                float ddx = x + 0.5f - cx, ddy = y + 0.5f - cy;
                float v = bright * MathF.Exp(-(ddx * ddx + ddy * ddy) / (2f * sig * sig));
                if (v < 0.004f) continue;
                int i = y * w + x;
                rgb[i * 3] += sr * v;
                rgb[i * 3 + 1] += sg * v;
                rgb[i * 3 + 2] += sb * v;
            }
        }

        var px8 = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            px8[i * 4] = ToByte(rgb[i * 3]);
            px8[i * 4 + 1] = ToByte(rgb[i * 3 + 1]);
            px8[i * 4 + 2] = ToByte(rgb[i * 3 + 2]);
            px8[i * 4 + 3] = ToByte(alpha[i]);
        }
        var sp = Make("SettingsUiSky", w, h, px8, w / worldW, new Vector2(0.5f, 0.5f));
        Cache[key] = sp;
        return sp;
    }

    // 星雲の雲 (縦横比 aspect = 幅/高さ)。色は絵に焼き、濃さは α に入れる。
    // (cx, cy) と (rx, ry) は絵の中の 0..1 の位置と広がり。四辺の 8% は必ず透明 (少し漂わせても夜空の外へ出ない)
    public static Sprite Nebula(int seed, float aspect, float cx, float cy, float rx, float ry, Color core, Color edge)
    {
        int w = NebulaW, h = Math.Max(8, (int)(NebulaW / aspect + 0.5f));
        long key = 2L << 40 | (long)seed << 20 | (uint)h;
        if (Cache.TryGetValue(key, out var cached) && cached) return cached;

        var px8 = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            float fx = (x + 0.5f) / w, fy = (y + 0.5f) / h;
            // 雲の形 = 歪ませた fBm (渦を巻いたような筋が出る)
            float px = fx * 3.2f * aspect / 1.8f, py = fy * 3.2f;
            float qx = Fbm(px + 5.2f, py + 1.3f, seed), qy = Fbm(px + 1.7f, py + 9.2f, seed);
            float n = Fbm(px + 1.9f * qx, py + 1.9f * qy, seed + 17);
            float ex = (fx - cx) / rx, ey = (fy - cy) / ry;
            float env = Clamp01(1f - MathF.Sqrt(ex * ex + ey * ey));
            env = env * env * (3f - 2f * env);
            float border = Smooth(0.08f, 0.2f, fx) * Smooth(0.08f, 0.2f, 1f - fx) * Smooth(0.08f, 0.2f, fy) * Smooth(0.08f, 0.2f, 1f - fy);
            float d = Smooth(0.38f, 0.82f, n) * env * border;
            // 濃い所ほど芯の色
            float t = Clamp01(d * 1.6f);
            int i = (y * w + x) * 4;
            px8[i] = ToByte(Lerp(edge.r, core.r, t));
            px8[i + 1] = ToByte(Lerp(edge.g, core.g, t));
            px8[i + 2] = ToByte(Lerp(edge.b, core.b, t));
            px8[i + 3] = ToByte(d);
        }
        var sp = Make("SettingsUiNebula", w, h, px8, w, new Vector2(0.5f, 0.5f));
        Cache[key] = sp;
        return sp;
    }

    // 流れ星の尾 (幅 1 単位・高さ 1/8 単位)。右の端が頭で一番明るく、左へ消える。支点は頭
    public static Sprite Streak()
    {
        if (_streak) return _streak;
        const int w = 128, h = 16;
        var px8 = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            float fx = (x + 0.5f) / w, fy = (y + 0.5f) / h * 2f - 1f;
            float along = MathF.Pow(fx, 2.4f);
            // 頭に近いほど太く
            float width = 0.25f + 0.55f * fx;
            float across = MathF.Exp(-(fy * fy) / (2f * width * width * 0.18f));
            int i = (y * w + x) * 4;
            px8[i] = px8[i + 1] = px8[i + 2] = 255;
            px8[i + 3] = ToByte(along * across);
        }
        _streak = Make("SettingsUiStreak", w, h, px8, w, new Vector2(1f, 0.5f));
        return _streak;
    }

    private static Sprite Make(string name, int w, int h, byte[] px, float ppu, Vector2 pivot)
    {
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = name, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        tex.LoadRawTextureData(px);
        tex.Apply(false, true);
        tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
        var sp = Sprite.Create(tex, new Rect(0, 0, w, h), pivot, ppu, 0, SpriteMeshType.FullRect);
        sp.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return sp;
    }

    // 角の丸い四角 (中心原点・半分の幅 hw・半分の高さ hh) の縁からの距離。内側で負
    private static float RoundRectDist(float x, float y, float hw, float hh, float r)
    {
        float qx = MathF.Abs(x) - (hw - r), qy = MathF.Abs(y) - (hh - r);
        float ox = MathF.Max(qx, 0f), oy = MathF.Max(qy, 0f);
        return MathF.Sqrt(ox * ox + oy * oy) + MathF.Min(MathF.Max(qx, qy), 0f) - r;
    }

    private static float Fbm(float x, float y, int seed)
    {
        float sum = 0f, amp = 0.5f;
        for (int o = 0; o < 5; o++)
        {
            sum += amp * ValueNoise(x, y, seed + o * 31);
            x = x * 2.03f + 7.1f;
            y = y * 2.03f + 3.7f;
            amp *= 0.5f;
        }
        return sum / 0.97f;
    }

    private static float ValueNoise(float x, float y, int seed)
    {
        int ix = (int)MathF.Floor(x), iy = (int)MathF.Floor(y);
        float fx = x - ix, fy = y - iy;
        fx = fx * fx * (3f - 2f * fx);
        fy = fy * fy * (3f - 2f * fy);
        float a = Hash(ix, iy, seed), b = Hash(ix + 1, iy, seed), c = Hash(ix, iy + 1, seed), d = Hash(ix + 1, iy + 1, seed);
        return Lerp(Lerp(a, b, fx), Lerp(c, d, fx), fy);
    }

    private static float Hash(int x, int y, int seed)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + seed * 1442695041);
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (h & 0xFFFFFF) / 16777215f;
        }
    }

    private static float Smooth(float e0, float e1, float v)
    {
        float t = Clamp01((v - e0) / (e1 - e0));
        return t * t * (3f - 2f * t);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
    private static byte ToByte(float v) => (byte)(Clamp01(v) * 255f + 0.5f);
}
