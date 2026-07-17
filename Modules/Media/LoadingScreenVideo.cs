#if !ANDROID
using System;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace EndKnot.Modules.Media;

// シーン遷移中 (ゲーム開始/次ゲームへ) に流すローディング画面動画。ネットワーク送信ゼロ・
// ホストローカル描画のみ。マウント先は HudManager (DDOL) 配下なのでシーン遷移を生き延びる。
// Show()/Hide() は host-only 機能ではない (非ホストクライアントでも呼ばれる想定)。
public static class LoadingScreenVideo
{
    private const string MediaSubDir = "EndKnot/Media";
    private const string DefaultVideoFileName = "loading_default.mp4";
    private const string EmbeddedDefaultResourceName = "EndKnot.Resources.Media.loading_default.mp4";
    private const float AutoHideSeconds = 40f;

    private static GameObject _container;
    private static VideoSurface _surface;
    private static float _shownAtRealtime;

    public static bool IsShowing => _surface is { IsActive: true };

    public static void Show()
    {
        if (!VideoSurface.IsSupported) return;
        if (Main.LoadingVideoEnabled is not { Value: true }) return;
        if (IsShowing) return; // 二重呼びは no-op

        try
        {
            if (!HudManager.InstanceExists) return;

            string path = ResolveVideoPath();
            if (string.IsNullOrWhiteSpace(path)) return;

            HudManager hud = HudManager.Instance;
            if (!hud.FullScreen) return;

            _container = new GameObject("EndKnotLoadingScreenVideo");
            _container.transform.SetParent(hud.transform, false);
            Vector3 basePos = hud.FullScreen.transform.localPosition;
            _container.transform.localPosition = new Vector3(basePos.x, basePos.y, -260f);

            _surface = new VideoSurface();
            if (!_surface.TryCreate(path, _container.transform))
            {
                Hide();
                return;
            }

            // z だけでは同一 sorting layer/order 内の順序にしか効かない。hud.FullScreen (画面全体の
            // 暗転スプライト) より確実に前面へ出すため、sorting layer/order 自体を追随させる。
            _surface.Renderer.sortingLayerID = hud.FullScreen.sortingLayerID;
            _surface.Renderer.sortingOrder = hud.FullScreen.sortingOrder + 1;

            _shownAtRealtime = Time.realtimeSinceStartup;
        }
        catch (Exception e)
        {
            Utils.ThrowException(e);
            Hide();
        }
    }

    public static void Hide()
    {
        try { _surface?.Dispose(); }
        catch (Exception e) { Utils.ThrowException(e); }
        _surface = null;

        if (_container != null)
        {
            try { Object.Destroy(_container); }
            catch { /* scene teardown may have already reclaimed it */ }
        }
        _container = null;
    }

    // FixedUpdateCaller から毎フレーム無条件で叩かれる。
    public static void Tick()
    {
        if (!IsShowing) return;

        try
        {
            if (!HudManager.InstanceExists)
            {
                // メインメニューへ戻った (HudManager 自体が既に破棄されている): GameObject 側は
                // シーンごと片付くが、HideAndDontSave 付きの RenderTexture/Texture2D/Sprite は
                // scene unload でも UnloadUnusedAssets でも回収されないため、明示 Dispose が必須
                // (fake-null 判定により破棄済みコンポーネントへは安全に no-op)。
                Hide();
                return;
            }

            if (Time.realtimeSinceStartup - _shownAtRealtime > AutoHideSeconds)
            {
                Hide();
                return;
            }

            _surface.Tick();

            // シーン遷移中は hud.FullScreen の bounds が過渡的にずれ得るため、一度きりで確定させず
            // 毎 Tick 追随させて自己修正する (安価な計算のみ)。
            if (_surface.Prepared) FitToScreen();
        }
        catch (Exception e)
        {
            Utils.ThrowException(e);
            Hide();
        }
    }

    // アスペクト比を保って hud.FullScreen が覆う範囲にレターボックス収まりさせる。
    // 余白は背後の黒フェード (CoFadeFullScreen) が埋める前提。
    private static void FitToScreen()
    {
        if (_surface?.Renderer == null || !HudManager.InstanceExists) return;
        HudManager hud = HudManager.Instance;
        if (!hud.FullScreen) return;

        int pixelWidth = _surface.PixelWidth;
        int pixelHeight = _surface.PixelHeight;
        if (pixelWidth <= 0 || pixelHeight <= 0) return;

        Bounds fullScreenBounds = hud.FullScreen.bounds;
        float availableWidth = fullScreenBounds.size.x;
        float availableHeight = fullScreenBounds.size.y;
        if (availableWidth <= 0f || availableHeight <= 0f) return;

        float spriteWidth = pixelWidth / _surface.PixelsPerUnitValue;
        float spriteHeight = pixelHeight / _surface.PixelsPerUnitValue;
        if (spriteWidth <= 0f || spriteHeight <= 0f) return;

        float scale = Mathf.Min(availableWidth / spriteWidth, availableHeight / spriteHeight);
        _surface.Renderer.transform.localScale = new Vector3(scale, scale, 1f);
    }

    // ① BepInEx/plugins/EndKnot/Media 内のユーザー指定/既定ファイル → ② DLL 埋込の既定動画抽出、の順で解決する。
    // mp4 が一切見当たらない場合は null を返し、呼び出し側で機能を静かに無効化する。
    private static string ResolveVideoPath()
    {
        try
        {
            string mediaDir = Path.Combine(BepInEx.Paths.PluginPath, MediaSubDir);
            if (!Directory.Exists(mediaDir)) Directory.CreateDirectory(mediaDir);

            string configuredFile = Main.LoadingVideoFile?.Value;
            if (!string.IsNullOrWhiteSpace(configuredFile))
            {
                string candidate = Path.IsPathRooted(configuredFile) ? configuredFile : Path.Combine(mediaDir, configuredFile);
                if (File.Exists(candidate)) return candidate;
            }

            string defaultPath = Path.Combine(mediaDir, DefaultVideoFileName);
            if (File.Exists(defaultPath)) return defaultPath;

            return ExtractEmbeddedDefault(mediaDir, defaultPath);
        }
        catch (Exception e)
        {
            Utils.ThrowException(e);
            return null;
        }
    }

    // 既定動画は生成プロセスがまだ mp4 を用意していない可能性があるため、リソースが
    // 見当たらなければ黙って抽出をスキップする (ビルドは常に通る)。
    private static string ExtractEmbeddedDefault(string mediaDir, string destinationPath)
    {
        try
        {
            using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedDefaultResourceName);
            if (stream == null) return null;

            using (FileStream fileStream = File.Create(destinationPath))
                stream.CopyTo(fileStream);

            return destinationPath;
        }
        catch (Exception e)
        {
            Utils.ThrowException(e);
            return null;
        }
    }
}
#else
namespace EndKnot.Modules.Media;

// Android ビルド (VideoModule 非搭載) 用の no-op スタブ。呼び出し側 (Patches/*) を
// #if で分岐させずに済むよう、同じ公開 API だけを残す。
public static class LoadingScreenVideo
{
    public static bool IsShowing => false;
    public static void Show() { }
    public static void Hide() { }
    public static void Tick() { }
}
#endif
