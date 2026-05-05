using EndKnot.Modules;
using TMPro;
using UnityEngine;

namespace EndKnot.Patches.CalamityMenu;

/// <summary>
/// Black overlay that fades the Calamity menu in over the first few seconds of startup.
/// Buttons are unresponsive while BGM init / scene warmup is running (~2.5s) — the fade
/// covers that period so the player just sees a stylish reveal instead of a frozen UI.
/// Center logo is a TMP placeholder; replace with a SpriteRenderer + sprite asset once
/// the EndKnot logo is finalized.
/// </summary>
public static class CalamityFadeIn
{
    // Three-phase timeline:
    //   [0,   LogoFadeInEnd) — overlay full black, logo fades 0 → 1
    //   [LogoFadeInEnd, FadeOutStart) — overlay full black, logo full alpha (the "blackout" hold)
    //   [FadeOutStart, TotalDuration) — overlay 1 → 0 and logo 1 → 0 together (the reveal)
    private const float LogoFadeInEnd = 0.7f;
    private const float FadeOutStart  = 3.0f;
    private const float TotalDuration = 7.5f;

    private static GameObject     _overlay;
    private static SpriteRenderer _overlayRenderer;
    private static GameObject     _logoText;
    private static TextMeshPro    _logoTmp;
    private static float          _startTime;
    private static bool           _done;

    public static void Build(Transform root)
    {
        Reset();
        _startTime = Time.realtimeSinceStartup;

        // Black sprite covering full camera frustum. Both overlay and logo sit at local z=0
        // (parent root is at world 0, well within camera frustum); sortingOrder controls
        // who renders on top.
        _overlay = new GameObject("CalamityFadeOverlay");
        _overlay.transform.SetParent(root);
        _overlay.transform.localPosition = Vector3.zero;

        _overlayRenderer = _overlay.AddComponent<SpriteRenderer>();
        _overlayRenderer.sprite       = CreateSolidBlack();
        _overlayRenderer.color        = new Color(0f, 0f, 0f, 1f);
        _overlayRenderer.sortingOrder = 10000;

        FitToScreen(_overlay.transform, _overlayRenderer);

        // Center logo placeholder. Swap to SpriteRenderer + sprite when logo asset arrives.
        // Wide gap from overlay (10100 vs 10000) so cross-renderer-type sorting can't
        // accidentally place the SpriteRenderer above the TMP MeshRenderer.
        _logoText = new GameObject("CalamityFadeLogo");
        _logoText.transform.SetParent(root);
        _logoText.transform.localPosition = Vector3.zero;

        _logoTmp                  = _logoText.AddComponent<TextMeshPro>();
        _logoTmp.text             = "End K not";
        _logoTmp.fontSize         = 6f;
        _logoTmp.fontSizeMin      = 6f;
        _logoTmp.fontSizeMax      = 6f;
        _logoTmp.enableAutoSizing = false;
        _logoTmp.alignment        = TextAlignmentOptions.Center;
        _logoTmp.fontStyle        = FontStyles.Bold;
        _logoTmp.color            = new Color(1f, 0.95f, 0.85f, 1f);
        _logoTmp.outlineColor     = Color.black;
        _logoTmp.outlineWidth     = 0.22f;
        _logoTmp.characterSpacing = 4f;
        _logoTmp.sortingOrder     = 10100;
        CalamityFonts.Apply(_logoTmp);

        // TMP builds its mesh lazily on the first frame. Force-build now so the text is
        // visible from t=0 — otherwise the user only sees it once the mesh finishes building,
        // which can be late enough that the overlay has already started fading out.
        _logoTmp.ForceMeshUpdate();
        var meshRenderer = _logoText.GetComponent<MeshRenderer>();
        if (meshRenderer != null) meshRenderer.sortingOrder = 10100;
    }

    public static void Tick()
    {
        if (_done || _overlay == null) return;

        float elapsed = Time.realtimeSinceStartup - _startTime;

        if (elapsed >= TotalDuration)
        {
            Object.Destroy(_overlay);
            Object.Destroy(_logoText);
            _overlay  = null;
            _logoText = null;
            _done     = true;

            // Re-trigger BGM credit display now that the menu is actually visible.
            // The original Show() fired during the blackout and finished before the player saw it.
            if (Main.ShowBGMInfo?.Value ?? true)
            {
                string bgm = BGMManager.CurrentBGMName;
                if (!string.IsNullOrEmpty(bgm)) BGMInfoDisplay.Show(bgm);
            }

            return;
        }

        float overlayAlpha;
        float logoAlpha;

        if (elapsed < LogoFadeInEnd)
        {
            // Phase 1: overlay full black, logo fading in
            overlayAlpha = 1f;
            logoAlpha    = elapsed / LogoFadeInEnd;
        }
        else if (elapsed < FadeOutStart)
        {
            // Phase 2: overlay full black, logo full alpha (the "blackout")
            overlayAlpha = 1f;
            logoAlpha    = 1f;
        }
        else
        {
            // Phase 3: overlay and logo fade out together
            float t = (elapsed - FadeOutStart) / (TotalDuration - FadeOutStart);
            overlayAlpha = 1f - t;
            logoAlpha    = 1f - t;
        }

        _overlayRenderer.color = new Color(0f, 0f, 0f, overlayAlpha);
        Color c = _logoTmp.color;
        _logoTmp.color = new Color(c.r, c.g, c.b, logoAlpha);
    }

    public static void Reset()
    {
        if (_overlay  != null) Object.Destroy(_overlay);
        if (_logoText != null) Object.Destroy(_logoText);
        _overlay  = null;
        _logoText = null;
        _done     = false;
    }

    private static void FitToScreen(Transform t, SpriteRenderer sr)
    {
        Vector2 spriteSize = sr.sprite.bounds.size;
        if (spriteSize.x < 0.001f || spriteSize.y < 0.001f) return;

        // Camera.main can be null on the second menu scene load. Without a fallback the
        // overlay stays at scale 1 (a 4×4-px speck) and the fade-in does nothing visible.
        Camera cam = Camera.main;
        float camH = cam != null ? cam.orthographicSize * 2f : 6f;
        float camW = cam != null ? camH * cam.aspect          : camH * (16f / 9f);

        // Slight overscan so edge AA doesn't expose the menu
        float scaleX = (camW * 1.1f) / spriteSize.x;
        float scaleY = (camH * 1.1f) / spriteSize.y;
        t.localScale = new Vector3(scaleX, scaleY, 1f);
    }

    private static Sprite CreateSolidBlack()
    {
        var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false);
        var pixels = new Color[16];
        for (int i = 0; i < 16; i++) pixels[i] = Color.black;
        tex.SetPixels(pixels);
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 100f);
    }
}
