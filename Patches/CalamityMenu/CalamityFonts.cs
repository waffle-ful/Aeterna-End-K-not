using System;
using System.IO;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace EndKnot.Patches.CalamityMenu;

/// <summary>
/// Captures vanilla TMP fonts from existing UI elements so Calamity menu text
/// uses the same look as the rest of the game (no missing-glyph boxes for CJK etc).
/// With the dusk backdrop, menu text switches to a hand-lettered font (Yusei Magic, SIL OFL 1.1);
/// glyphs it lacks fall back to the vanilla font.
/// </summary>
public static class CalamityFonts
{
    public static TMP_FontAsset Vanilla;

    // false にすると夕暮れメニューでもバニラのフォントのまま。
    public static bool UseDuskFont = true;

    private const string DuskFontResource = "EndKnot.Resources.Fonts.YuseiMagic-Regular.ttf";
    private static TMP_FontAsset _dusk;
    private static bool _duskFailed;

    public static void Capture(MainMenuManager mm)
    {
        // mm.quitButton.buttonText is a TMP_Text on a vanilla PassiveButton
        if (mm?.quitButton?.buttonText != null)
            Vanilla = mm.quitButton.buttonText.font;
    }

    public static void Apply(TMP_Text tmp)
    {
        if (tmp == null) return;
        TMP_FontAsset font = CalamityDusk.Built ? GetDuskFont() : null;
        if (font == null) font = Vanilla;
        if (font == null) return;

        // フォントを替えるとマテリアルが作り直されて縁取りが消えるので、付け直す
        // (同じ値の代入は無視されるため、一度ずらしてから戻す)
        Color32 outlineColor = tmp.outlineColor;
        float outlineWidth = tmp.outlineWidth;
        tmp.font = font;
        if (outlineWidth > 0f)
        {
            tmp.outlineWidth = 0f;
            tmp.outlineWidth = outlineWidth;
            tmp.outlineColor = Color.clear;
            tmp.outlineColor = outlineColor;
        }
    }

    private static TMP_FontAsset GetDuskFont()
    {
        if (!UseDuskFont || _duskFailed || OperatingSystem.IsAndroid()) return null;
        if (_dusk != null) return _dusk;
        try
        {
            _dusk = CreateDuskFont();
            if (_dusk == null) _duskFailed = true;
        }
        catch (Exception ex)
        {
            Logger.Exception(ex, "CalamityFonts.Dusk");
            _duskFailed = true;
            _dusk = null;
        }
        return _dusk;
    }

    private static TMP_FontAsset CreateDuskFont()
    {
        // Font はファイルパスからしか作れないので、埋め込みの TTF を一度だけキャッシュへ書き出す
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(DuskFontResource);
        if (stream == null) return null;
        string dir = Path.Combine(BepInEx.Paths.CachePath, "EndKnot");
        string path = Path.Combine(dir, "YuseiMagic-Regular.ttf");
        if (!File.Exists(path) || new FileInfo(path).Length != stream.Length)
        {
            Directory.CreateDirectory(dir);
            using FileStream fs = File.Create(path);
            stream.CopyTo(fs);
        }

        var font = new Font(path);
        TMP_FontAsset fa = TMP_FontAsset.CreateFontAsset(font, 90, 9, GlyphRenderMode.SDFAA, 2048, 2048, AtlasPopulationMode.Dynamic, true);
        if (fa == null) return null;
        fa.name = "DuskMenuFont";

        // TMP のシェーダーがビルドに無いとマテリアルが空になるので、バニラのフォントのシェーダーを借りる
        if (Vanilla != null && Vanilla.material != null && (fa.material == null || fa.material.shader == null || !fa.material.shader.isSupported))
        {
            var mat = new Material(Vanilla.material);
            mat.SetTexture(ShaderUtilities.ID_MainTex, fa.atlasTexture);
            mat.SetFloat(ShaderUtilities.ID_TextureWidth, fa.atlasWidth);
            mat.SetFloat(ShaderUtilities.ID_TextureHeight, fa.atlasHeight);
            mat.SetFloat(ShaderUtilities.ID_GradientScale, fa.atlasPadding + 1);
            fa.material = mat;
        }

        // Mobile 系の SDF シェーダーは OUTLINE_ON が立っていないと縁取りを描かない
        fa.material?.EnableKeyword(ShaderUtilities.Keyword_Outline);
        // 線の細い手書き体なので、縁取りに食われないよう字面を少し太らせる
        fa.material?.SetFloat(ShaderUtilities.ID_FaceDilate, 0.25f);

        if (Vanilla != null)
        {
            fa.fallbackFontAssetTable ??= new Il2CppSystem.Collections.Generic.List<TMP_FontAsset>();
            fa.fallbackFontAssetTable.Add(Vanilla);
        }

        // シーン遷移の UnloadUnusedAssets で消されないように
        font.hideFlags = HideFlags.DontUnloadUnusedAsset;
        fa.hideFlags = HideFlags.DontUnloadUnusedAsset;
        if (fa.material != null) fa.material.hideFlags = HideFlags.DontUnloadUnusedAsset;
        if (fa.atlasTexture != null) fa.atlasTexture.hideFlags = HideFlags.DontUnloadUnusedAsset;

        Logger.Info($"Dusk menu font ready: shader={fa.material?.shader?.name} atlas={fa.atlasWidth}x{fa.atlasHeight}", "CalamityFonts");
        return fa;
    }
}
