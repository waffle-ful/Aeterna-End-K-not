#if !ANDROID
using System;
using System.IO;
using EndKnot.Modules.CalamityMenu;
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

    // 起動時に先行して用意しておく火 (下の Prewarm を参照)。メニューが組み上がった時点で
    // _surface へ引き継ぐ。_surface と別フィールドなのは Tick を確実に素通りさせるため —
    // 未装着の間に Tick が走ると、まだ画面に置いていない映像を再生開始してしまう。
    private static VideoSurface _prewarmed;
    private static GameObject _prewarmHolder;
    private static bool _prewarmAttempted;
    private static float _prewarmStartedAt;

    /// <summary>
    /// メニューが構築されるより前 (スプラッシュ中) に VideoPlayer の準備を始めておく。
    /// 準備完了までは実測で約2秒かかり、メニュー構築時に始めるとその間だけ背景から火が消える。
    /// 準備はネイティブ側で自走するのでポーリングは不要 — シーンを跨いで生かしておくだけでよい。
    /// </summary>
    public static void Prewarm()
    {
        if (_prewarmAttempted) return;
        _prewarmAttempted = true;

        // 不発時は理由を残す: この機能の効果は「起動直後の数秒だけ見える差」なので、
        // ログが無いと prewarm が働かなかったのかどうかすら実機で判別できない。
        if (!CalamityMenuState.Active) { Logger.Info("Prewarm skipped: Calamity menu inactive", "CalamityFire"); return; }
        if (!VideoSurface.IsSupported) { Logger.Info("Prewarm skipped: VideoPlayer type unavailable", "CalamityFire"); return; }
        if (Main.MenuFireEnabled is not { Value: true }) { Logger.Info("Prewarm skipped: disabled by config", "CalamityFire"); return; }
        if (File.Exists($"{Main.DataPath}/EndKnot_DATA/disable_menu_fire.txt")) { Logger.Warn("Prewarm skipped: kill switch ENGAGED (disable_menu_fire.txt)", "CalamityFire"); return; }

        try
        {
            string path = ResolveVideoPath();
            if (path == null) { Logger.Info($"Prewarm skipped: no {FireVideoFileName}", "CalamityFire"); return; }

            _prewarmHolder = new GameObject("EndKnotFirePrewarm");
            UnityEngine.Object.DontDestroyOnLoad(_prewarmHolder);

            var surface = new VideoSurface();
            if (!surface.TryCreate(path, _prewarmHolder.transform))
            {
                surface.Dispose();
                DisposePrewarmHolder();
                return;
            }

            _prewarmed = surface;
            _prewarmStartedAt = Time.realtimeSinceStartup;
            Logger.Info("fire video prewarm started", "CalamityFire");
        }
        catch (Exception e)
        {
            Utils.ThrowException(e);
            DisposePrewarm();
        }
    }

    public static void Build(Transform backgroundLayer)
    {
        DisposeSurface();

        // 火を出さないと決まった場合は、先行準備済みの分もここで手放す (抱えたままにすると
        // 画面に出ないデコーダをセッション中ずっと保持することになる)。
        if (backgroundLayer == null) { DisposePrewarm(); return; }
        if (!VideoSurface.IsSupported) { DisposePrewarm(); Logger.Info("Build skipped: VideoPlayer type unavailable", "CalamityFire"); return; }
        if (Main.MenuFireEnabled is not { Value: true }) { DisposePrewarm(); Logger.Info("Build skipped: disabled by config", "CalamityFire"); return; }
        // kill switch (再ビルド不要の A/B 手段): 存在する間は火を出さない。
        if (File.Exists($"{Main.DataPath}/EndKnot_DATA/disable_menu_fire.txt")) { DisposePrewarm(); Logger.Warn("Build skipped: kill switch ENGAGED (disable_menu_fire.txt)", "CalamityFire"); return; }

        try
        {
            // 先行準備済みの火があればそれを持ってくる (作り直すと準備待ちが再発する)。
            // シーンを跨いできた参照なので、ネイティブ側が生きているかを Unity の比較演算子で
            // 確かめてから使う。C# 側の参照が非 null でも中身が破棄されていることがある。
            if (_prewarmed != null)
            {
                if (_prewarmed.GameObject != null && _prewarmed.Renderer != null)
                {
                    _surface = _prewarmed;
                    _prewarmed = null;

                    Transform t = _surface.GameObject.transform;
                    t.SetParent(backgroundLayer, false);
                    t.localPosition = Vector3.zero;
                    _surface.GameObject.layer = backgroundLayer.gameObject.layer;
                    _surface.Renderer.sortingOrder = -99;
                    _layer = backgroundLayer;

                    DisposePrewarmHolder();
                    Logger.Info($"fire video mounted from prewarm, nativePrepared={_surface.NativePrepared}, waited={Time.realtimeSinceStartup - _prewarmStartedAt:F2}s", "CalamityFire");
                    return;
                }

                // 先行分が失われていた: 手放して下の通常生成へ落とす (ここで return すると
                // そのセッション中ずっと火が出なくなる)。
                Logger.Warn("prewarmed fire surface did not survive the scene load — building fresh", "CalamityFire");
                DisposePrewarm();
            }

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

    private static void DisposePrewarm()
    {
        try { _prewarmed?.Dispose(); }
        catch (Exception e) { Utils.ThrowException(e); }
        _prewarmed = null;
        DisposePrewarmHolder();
    }

    // 中身を backgroundLayer へ移し替えた後の空き箱を片付ける。
    private static void DisposePrewarmHolder()
    {
        if (_prewarmHolder == null) return;
        try { UnityEngine.Object.Destroy(_prewarmHolder); }
        catch (Exception e) { Utils.ThrowException(e); }
        _prewarmHolder = null;
    }
}
#else
namespace EndKnot.Patches.CalamityMenu;

// Android ビルド (VideoModule 非搭載) 用の no-op スタブ。
public static class CalamityFire
{
    public static void Prewarm() { }
    public static void Build(UnityEngine.Transform backgroundLayer) { }
    public static void Tick() { }
}
#endif
