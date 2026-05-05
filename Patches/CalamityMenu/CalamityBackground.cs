using System;
using UnityEngine;

namespace EndKnot.Patches.CalamityMenu;

public static class CalamityBackground
{
    // Placeholder: deep Calamity-style dark purple-black.
    // Replace sprite loading when Resources/Images/MainMenu/bg_main.png is ready.
    private static readonly Color PlaceholderColor = new(0.05f, 0.04f, 0.10f);

    public static void Build(Transform backgroundLayer)
    {
        Sprite bg = TryLoadSprite();

        var go = new GameObject("CalamityBG");
        go.transform.SetParent(backgroundLayer);
        go.transform.localPosition = Vector3.zero;

        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = bg ?? CreateSolidSprite(PlaceholderColor);
        sr.sortingOrder = -100;

        FitToScreen(go.transform, sr);
    }

    private static void FitToScreen(Transform t, SpriteRenderer sr)
    {
        Vector2 spriteSize = sr.sprite.bounds.size;
        if (spriteSize.x < 0.001f || spriteSize.y < 0.001f) return;

        Camera cam = Camera.main;
        // Camera.main can be null on the second menu scene load (vanilla camera not yet
        // resolved at Postfix time). Without a fallback the sprite stays at scale 1 and
        // becomes a 4×4-px speck → user sees a black screen instead of the background.
        float camH = cam != null ? cam.orthographicSize * 2f : 6f;  // default ortho size 3
        float camW = cam != null ? camH * cam.aspect          : camH * (16f / 9f);

        float scaleX = camW / spriteSize.x;
        float scaleY = camH / spriteSize.y;
        float scale = Math.Max(scaleX, scaleY); // cover mode (no black bars)
        t.localScale = new Vector3(scale, scale, 1f);
    }

    private static Sprite TryLoadSprite()
    {
        try
        {
            return Utils.LoadSprite("EndKnot.Resources.Images.MainMenu.bg_main.png", 100f);
        }
        catch
        {
            return null;
        }
    }

    // Creates a 4x4 white texture tinted by the SpriteRenderer color field.
    private static Sprite CreateSolidSprite(Color tint)
    {
        var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false);
        var pixels = new Color[16];
        for (int i = 0; i < 16; i++) pixels[i] = tint;
        tex.SetPixels(pixels);
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 100f);
    }
}
