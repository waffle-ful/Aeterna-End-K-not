#if !ANDROID
using System;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Video;

namespace EndKnot.Modules.Media;

// Unity VideoPlayer 再生カプセル。SuperNewRoles Modules/AnnouncementImageSupport.cs の
// CreateVideoRenderer / OnVideoPrepared / 毎フレームコピー機構をそのまま踏襲する(前例の無い
// Unity API を発明しない)。ネットワーク送信は一切行わない(ホストローカル描画のみ)。
public sealed class VideoSurface
{
    private const float PixelsPerUnit = 100f;

    // VideoModule が unstrip されていない環境 (Android 等) では型解決自体が失敗するため、
    // 実行時にも二重で無効化できるようにしておく。
    private static readonly bool VideoPlayerTypeAvailable =
        (Type.GetType("UnityEngine.Video.VideoPlayer, UnityEngine.VideoModule")
         ?? Type.GetType("UnityEngine.Video.VideoPlayer, UnityEngine.CoreModule")) != null;

    public static bool IsSupported => VideoPlayerTypeAvailable;

    public GameObject GameObject { get; private set; }
    public SpriteRenderer Renderer { get; private set; }
    public bool IsActive { get; private set; }
    public bool Prepared { get; private set; }

    private VideoPlayer _player;
    private RenderTexture _renderTexture;
    private Texture2D _videoTexture;
    private Sprite _videoSprite;
    private VideoPlayer.ErrorEventHandler _errorHandler;
    private float _createdAtRealtime;
    private bool _pendingLogged;
    private bool _firstCopyLogged;

    public bool TryCreate(string absolutePath, Transform parent)
    {
        if (!VideoPlayerTypeAvailable) return false;
        if (string.IsNullOrWhiteSpace(absolutePath) || !File.Exists(absolutePath)) return false;

        try
        {
            // file:// URI にすると空白が %20 エスケープされ、Windows/WMF が "Cannot read file" で
            // 拒否する (Epic 版の C:\Program Files\... で必発)。素のフルパスなら Unity が内部処理する。
            string videoUrl = Path.GetFullPath(absolutePath);

            GameObject = new GameObject("EndKnotLoadingVideo");
            GameObject.transform.SetParent(parent, false);
            GameObject.layer = parent.gameObject.layer; // 子も明示継承 (SetParent は layer を継がない)

            Renderer = GameObject.AddComponent<SpriteRenderer>();
            var shader = Shader.Find("Sprites/Default");
            if (shader != null) Renderer.material = new Material(shader);
            Renderer.enabled = false;

            _renderTexture = new RenderTexture(2, 2, 0, RenderTextureFormat.ARGB32) { hideFlags = HideFlags.HideAndDontSave };
            _renderTexture.Create();

            _player = GameObject.AddComponent<VideoPlayer>();
            _player.playOnAwake = false;
            _player.isLooping = true;
            _player.audioOutputMode = VideoAudioOutputMode.None;
            _player.source = VideoSource.Url;
            _player.renderMode = VideoRenderMode.RenderTexture;
            _player.waitForFirstFrame = true;
            _player.skipOnDrop = true;
            _player.targetTexture = _renderTexture;
            _player.url = videoUrl;
            _errorHandler = (VideoPlayer.ErrorEventHandler)OnVideoError;
            _player.errorReceived += _errorHandler;

            _player.Prepare();
            _createdAtRealtime = Time.realtimeSinceStartup;
            IsActive = true;
            return true;
        }
        catch (Exception e)
        {
            Utils.ThrowException(e);
            Dispose();
            return false;
        }
    }

    // 呼び出し側 (LoadingScreenVideo) から毎 FixedUpdate 叩かれる想定。
    public void Tick()
    {
        if (!IsActive || _player == null) return;

        try
        {
            if (!Prepared)
            {
                if (!_player.isPrepared)
                {
                    // 計装: prepare が無音で終わらないケース (WMF 側スタック) を実機ログで検出する。
                    if (!_pendingLogged && Time.realtimeSinceStartup - _createdAtRealtime > 5f)
                    {
                        _pendingLogged = true;
                        Logger.Info("still not prepared after 5s (WMF stuck?)", "LoadingScreenVideo");
                    }
                    return;
                }

                OnPrepared();
                if (!Prepared) return;
                Logger.Info($"prepared: {_videoTexture.width}x{_videoTexture.height}, playing", "LoadingScreenVideo");
            }

            // SNR の毎フレームコピー機構と同じ二段構え: CopyTexture が使えればそちら優先、
            // ダメなら RenderTexture.active 経由の ReadPixels にフォールバック。
            if (SystemInfo.copyTextureSupport != CopyTextureSupport.None)
            {
                Graphics.CopyTexture(_renderTexture, _videoTexture);
                if (!_firstCopyLogged)
                {
                    _firstCopyLogged = true;
                    Logger.Info($"first frame copied (CopyTexture), renderer.enabled={Renderer.enabled}, sorting={Renderer.sortingLayerID}/{Renderer.sortingOrder}, scale={Renderer.transform.localScale.x:F2}, layer={GameObject.layer}, worldPos={Renderer.transform.position}", "LoadingScreenVideo");
                }
                return;
            }

            RenderTexture previous = RenderTexture.active;
            try
            {
                RenderTexture.active = _renderTexture;
                _videoTexture.ReadPixels(new Rect(0f, 0f, _renderTexture.width, _renderTexture.height), 0, 0);
                _videoTexture.Apply(false);
            }
            finally
            {
                RenderTexture.active = previous;
            }
        }
        catch (Exception e)
        {
            Utils.ThrowException(e);
            IsActive = false; // 毎フレ例外スパム禁止: 二度と描かない
        }
    }

    // 実寸判明後に RenderTexture/Texture2D/Sprite を作り直す (SNR OnVideoPrepared と同形)。
    private void OnPrepared()
    {
        int pixelWidth = _player.width > 0 ? (int)_player.width : 0;
        int pixelHeight = _player.height > 0 ? (int)_player.height : 0;
        if (pixelWidth <= 0 || pixelHeight <= 0) return;

        if (_renderTexture == null || _renderTexture.width != pixelWidth || _renderTexture.height != pixelHeight)
        {
            if (_renderTexture != null)
            {
                _renderTexture.Release();
                UnityEngine.Object.Destroy(_renderTexture);
            }

            _renderTexture = new RenderTexture(pixelWidth, pixelHeight, 0, RenderTextureFormat.ARGB32) { hideFlags = HideFlags.HideAndDontSave };
            _renderTexture.Create();
            _player.targetTexture = _renderTexture;
        }

        _videoTexture = new Texture2D(pixelWidth, pixelHeight, TextureFormat.ARGB32, false)
        {
            hideFlags = HideFlags.HideAndDontSave,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };

        _videoSprite = Sprite.Create(_videoTexture, new Rect(0f, 0f, pixelWidth, pixelHeight), new Vector2(0.5f, 0.5f), PixelsPerUnit);
        _videoSprite.hideFlags = HideFlags.HideAndDontSave;

        Renderer.sprite = _videoSprite;
        Renderer.enabled = true;

        Prepared = true;
        _player.Play();
    }

    public int PixelWidth => _videoTexture != null ? _videoTexture.width : 0;
    public int PixelHeight => _videoTexture != null ? _videoTexture.height : 0;
    public float PixelsPerUnitValue => PixelsPerUnit;

    private void OnVideoError(VideoPlayer player, string message)
    {
        Logger.Warn($"LoadingScreenVideo playback error: {message}", "LoadingScreenVideo");
        IsActive = false;
    }

    public void Dispose()
    {
        IsActive = false;
        Prepared = false;

        if (_player != null)
        {
            if (_errorHandler != null) _player.errorReceived -= _errorHandler;
            try { _player.Stop(); }
            catch { /* ignore: player may already be torn down by scene unload */ }
        }

        if (_renderTexture != null)
        {
            _renderTexture.Release();
            UnityEngine.Object.Destroy(_renderTexture);
            _renderTexture = null;
        }

        if (_videoSprite != null)
        {
            UnityEngine.Object.Destroy(_videoSprite);
            _videoSprite = null;
        }

        if (_videoTexture != null)
        {
            UnityEngine.Object.Destroy(_videoTexture);
            _videoTexture = null;
        }

        if (Renderer != null && Renderer.material != null)
            UnityEngine.Object.Destroy(Renderer.material); // instanced material は自動回収されない

        if (GameObject != null)
        {
            UnityEngine.Object.Destroy(GameObject);
            GameObject = null;
        }

        _player = null;
        Renderer = null;
        _errorHandler = null;
    }
}
#endif
