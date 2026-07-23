#if !ANDROID
using System;
using System.IO;
using EndKnot.Modules.Media;
using UnityEngine;

namespace EndKnot.Patches.CalamityMenu;

// Calamity メニュー背景の火エフェクト (VideoPlayer 再生・ホストローカル描画のみ・送信ゼロ)。
// 素材はアルファ付き VP8 WebM (オフラインで輝度→アルファを焼いたもの)。加算シェーダ路線は
// 2026-07-24 の実機シェーダ census (全31種列挙) で AU ビルドに加算系が皆無と確定して廃案 —
// アルファは動画自体に持たせ、実証済みの Sprites/Default アルファ合成で重ねるのが正。
public static class CalamityFire
{
    private const string FireVideoFileName = "menu_fire.webm";
    private const string EmbeddedResourceName = "EndKnot.Resources.Media.menu_fire.webm";

    private static VideoSurface _surface;
    private static Transform _layer;

    public static void Build(Transform backgroundLayer)
    {
        DisposeSurface();

        if (backgroundLayer == null) return;
        if (!VideoSurface.IsSupported) { Logger.Info("Build skipped: VideoPlayer type unavailable", "CalamityFire"); return; }
        if (Main.MenuFireEnabled is not { Value: true }) { Logger.Info("Build skipped: disabled by config", "CalamityFire"); return; }
        // kill switch (再ビルド不要の A/B 手段): 存在する間は火を出さない。
        if (File.Exists($"{Main.DataPath}/EndKnot_DATA/disable_menu_fire.txt")) { Logger.Warn("Build skipped: kill switch ENGAGED (disable_menu_fire.txt)", "CalamityFire"); return; }

        try
        {
            string path = ResolveVideoPath();
            if (path == null) { Logger.Info($"Build skipped: no {FireVideoFileName}", "CalamityFire"); return; }

            _surface = new VideoSurface();
            if (!_surface.TryCreate(path, backgroundLayer))
            {
                Logger.Warn("Build aborted: VideoSurface.TryCreate failed", "CalamityFire");
                DisposeSurface();
                return;
            }

            // 背景 (CalamityBG, sortingOrder=-100) の直前・ロゴ/ボタンより後ろ。
            _surface.Renderer.sortingOrder = -99;
            _layer = backgroundLayer;
            Logger.Info($"fire video mounted, path={path}", "CalamityFire");
        }
        catch (Exception e)
        {
            Utils.ThrowException(e);
            DisposeSurface();
        }
    }

    // FixedUpdateCaller から無条件で毎フレーム叩かれる (メニュー以外では _surface==null で即 return)。
    public static void Tick()
    {
        if (_surface == null) return;

        try
        {
            // メニューシーンが破棄された: GameObject 側はシーンごと片付くが、HideAndDontSave の
            // RenderTexture/Texture2D/Sprite は明示 Dispose しないと回収されない (LoadingScreenVideo と同じ罠)。
            if (_layer == null)
            {
                DisposeSurface();
                return;
            }

            _surface.Tick();
            if (_surface.Prepared) FitCover();
        }
        catch (Exception e)
        {
            Utils.ThrowException(e);
            DisposeSurface();
        }
    }

    // ① BepInEx/plugins/EndKnot/Media/menu_fire.webm → ② DLL 埋込リソース抽出、の順で解決
    // (LoadingScreenVideo.ResolveVideoPath と同形)。見当たらなければ null で静かに無効化。
    private static string ResolveVideoPath()
    {
        try
        {
            string mediaDir = Path.Combine(BepInEx.Paths.PluginPath, "EndKnot", "Media");
            if (!Directory.Exists(mediaDir)) Directory.CreateDirectory(mediaDir);

            string path = Path.Combine(mediaDir, FireVideoFileName);
            if (File.Exists(path)) return path;

            using Stream stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedResourceName);
            if (stream == null) return null;

            using (FileStream fileStream = File.Create(path))
                stream.CopyTo(fileStream);

            return path;
        }
        catch (Exception e)
        {
            Utils.ThrowException(e);
            return null;
        }
    }

    // CalamityBackground.FitToScreen と同じ cover 方式 (黒帯なし)。炎は素材の下半分に
    // 寄っているので、画面全体を覆えばちょうど画面下部に火が敷かれる。
    private static void FitCover()
    {
        if (_surface?.Renderer == null) return;

        int pixelWidth = _surface.PixelWidth;
        int pixelHeight = _surface.PixelHeight;
        if (pixelWidth <= 0 || pixelHeight <= 0) return;

        float spriteWidth = pixelWidth / _surface.PixelsPerUnitValue;
        float spriteHeight = pixelHeight / _surface.PixelsPerUnitValue;

        Camera cam = Camera.main;
        float camH = cam != null ? cam.orthographicSize * 2f : 6f;
        float camW = cam != null ? camH * cam.aspect : camH * (16f / 9f);

        float scale = Math.Max(camW / spriteWidth, camH / spriteHeight);
        _surface.Renderer.transform.localScale = new Vector3(scale, scale, 1f);
    }

    private static void DisposeSurface()
    {
        try { _surface?.Dispose(); }
        catch (Exception e) { Utils.ThrowException(e); }
        _surface = null;
        _layer = null;
    }
}
#else
namespace EndKnot.Patches.CalamityMenu;

// Android ビルド (VideoModule 非搭載) 用の no-op スタブ。
public static class CalamityFire
{
    public static void Build(UnityEngine.Transform backgroundLayer) { }
    public static void Tick() { }
}
#endif
