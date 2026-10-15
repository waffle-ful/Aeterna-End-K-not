using System;
using UnityEngine;

namespace SpawnEditorKit;

// Textures generated once in code (no image assets).
internal static class SpawnEditorArt
{
    private static Sprite _pin;

    // A map pin: round head, pointed tip at the bottom (pivot), dark rim and center dot.
    // The white body is tinted by SpriteRenderer.color; the dark parts stay dark.
    public static Sprite Pin
    {
        get
        {
            if (_pin) return _pin;

            const int w = 48, h = 72;
            const float cx = 23.5f, cy = 47f, r = 22f, rim = 3f;
            var px = new Color32[w * h];
            var clear = new Color32(0, 0, 0, 0);
            var white = new Color32(255, 255, 255, 255);
            var dark = new Color32(30, 30, 38, 255);

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float d = PinDistance(x + 0.5f - cx, y + 0.5f - cy, r);
                    float dc = MathF.Sqrt((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy));
                    Color32 c = d > 0f ? clear : d > -rim || dc < 7f ? dark : white;
                    if (d > 0f && d < 1f) c = new Color32(dark.r, dark.g, dark.b, (byte)(255 * (1f - d)));
                    px[y * w + x] = c;
                }
            }

            var tex = new Texture2D(w, h, TextureFormat.ARGB32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            tex.SetPixels32(px);
            tex.Apply(false, true);
            tex.hideFlags = HideFlags.HideAndDontSave;

            _pin = Sprite.Create(tex, new Rect(0f, 0f, w, h), new Vector2(0.5f, 0f), 120f, 0, SpriteMeshType.FullRect);
            _pin.hideFlags = HideFlags.HideAndDontSave;
            return _pin;
        }
    }

    private static Sprite _dot;

    // Soft round dot with a dark rim, for the scatter preview.
    public static Sprite Dot
    {
        get
        {
            if (_dot) return _dot;

            const int size = 32;
            const float c = 15.5f, r = 14f;
            var px = new Color32[size * size];

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float d = MathF.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) - r;
                    px[y * size + x] = d > 1f ? new Color32(0, 0, 0, 0)
                        : d > -2.5f ? new Color32(25, 25, 32, (byte)(255 * Math.Clamp(1f - d, 0f, 1f)))
                        : new Color32(255, 255, 255, 255);
                }
            }

            var tex = new Texture2D(size, size, TextureFormat.ARGB32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            tex.SetPixels32(px);
            tex.Apply(false, true);
            tex.hideFlags = HideFlags.HideAndDontSave;

            _dot = Sprite.Create(tex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
            _dot.hideFlags = HideFlags.HideAndDontSave;
            return _dot;
        }
    }

    private const int IconSize = 110;

    private static Sprite _buttonPin, _buttonGear;

    // Ability button icon: a large cyan pin with a dark rim.
    public static Sprite ButtonPin
    {
        get
        {
            if (_buttonPin) return _buttonPin;

            const float s = 1.45f, cx = IconSize * 0.5f, cy = 47f * s + 6f;
            var body = new Color32(80, 220, 230, 255);
            _buttonPin = Icon((x, y) =>
            {
                float dx = (x - cx) / s, dy = (y - cy) / s;
                float d = PinDistance(dx, dy, 22f) * s;
                float dc = MathF.Sqrt(dx * dx + dy * dy) * s;
                return (d, d > -4.5f || dc < 10f ? Dark : body);
            });
            return _buttonPin;
        }
    }

    // Ability button icon: a light gear with eight teeth.
    public static Sprite ButtonGear
    {
        get
        {
            if (_buttonGear) return _buttonGear;

            const float c = IconSize * 0.5f, hole = 15f;
            var body = new Color32(225, 230, 238, 255);
            _buttonGear = Icon((x, y) =>
            {
                float dx = x - c, dy = y - c;
                float r = MathF.Sqrt(dx * dx + dy * dy);
                float tooth = Math.Clamp((MathF.Cos(MathF.Atan2(dy, dx) * 8f) - 0.1f) * 3f, 0f, 1f);
                float outer = 38f + 10f * tooth;
                float d = MathF.Max(r - outer, hole - r);
                return (d, d > -4.5f ? Dark : body);
            });
            return _buttonGear;
        }
    }

    private static readonly Color32 Dark = new(30, 30, 38, 255);

    // Draws an icon from a signed distance (negative = inside) and a fill color per pixel, with a 1px soft edge.
    private static Sprite Icon(Func<float, float, (float Dist, Color32 Fill)> shape)
    {
        var px = new Color32[IconSize * IconSize];

        for (int y = 0; y < IconSize; y++)
        {
            for (int x = 0; x < IconSize; x++)
            {
                (float d, Color32 fill) = shape(x + 0.5f, y + 0.5f);
                px[y * IconSize + x] = d > 1f ? new Color32(0, 0, 0, 0)
                    : d > 0f ? new Color32(Dark.r, Dark.g, Dark.b, (byte)(255 * (1f - d)))
                    : fill;
            }
        }

        var tex = new Texture2D(IconSize, IconSize, TextureFormat.ARGB32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        tex.SetPixels32(px);
        tex.Apply(false, true);
        tex.hideFlags = HideFlags.HideAndDontSave;

        Sprite sprite = Sprite.Create(tex, new Rect(0f, 0f, IconSize, IconSize), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
        sprite.hideFlags = HideFlags.HideAndDontSave;
        return sprite;
    }

    // Signed distance to the pin outline (negative = inside): union of the head circle and the
    // triangle from the tip (0, -cy) to the circle's tangent points.
    private static float PinDistance(float dx, float dy, float r)
    {
        float circle = MathF.Sqrt(dx * dx + dy * dy) - r;
        if (dy > 0f) return circle;

        // The tip sits at dy = -cy (= -47); the cone narrows linearly from radius r*0.8 at dy=-r*0.6 to 0 at the tip.
        const float tip = -47f;
        float top = -r * 0.6f;
        if (dy < tip) return circle;
        float t = (dy - tip) / (top - tip);
        float half = r * 0.8f * Math.Clamp(t, 0f, 1f);
        float cone = MathF.Abs(dx) - half;
        return MathF.Min(circle, cone);
    }

    // Rounded rectangle with a 1px lighter rim, for IMGUI backgrounds.
    public static Texture2D Rounded(int width, int height, int radius, Color fill, Color edge)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        radius = Math.Clamp(radius, 0, Math.Min(width, height) / 2);
        var px = new Color32[width * height];
        var clear = new Color32(0, 0, 0, 0);
        Color32 solid = fill;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float a = CornerAlpha(x, y, width, height, radius);
                px[y * width + x] = a <= 0f ? clear : a >= 1f ? solid : (Color32)Color.Lerp(edge, fill, a);
            }
        }

        var tex = new Texture2D(width, height, TextureFormat.ARGB32, false) { filterMode = FilterMode.Bilinear };
        tex.SetPixels32(px);
        tex.Apply(false, true);
        tex.hideFlags = HideFlags.HideAndDontSave;
        return tex;
    }

    private static float CornerAlpha(int px, int py, int w, int h, int r)
    {
        int cx, cy;
        if (px < r && py < r) { cx = r; cy = r; }
        else if (px >= w - r && py < r) { cx = w - r; cy = r; }
        else if (px < r && py >= h - r) { cx = r; cy = h - r; }
        else if (px >= w - r && py >= h - r) { cx = w - r; cy = h - r; }
        else return 1f;

        float d = MathF.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
        if (d >= r + 1f) return 0f;
        if (d <= r - 1f) return 1f;
        return r + 0.5f - d;
    }
}
