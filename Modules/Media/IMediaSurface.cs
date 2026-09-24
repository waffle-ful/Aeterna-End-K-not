using System;
using System.IO;
using UnityEngine;

namespace EndKnot.Modules.Media;

// 画面に動画を 1 枚貼る面。VideoPlayer 再生 (VideoSurface) と連番 JPG 再生 (FrameSequencePlayer) の
// どちらでも呼び出し側 (CalamityFire / LoadingScreenVideo) が同じ手順で扱えるようにする。
public interface IMediaSurface
{
    GameObject GameObject { get; }
    SpriteRenderer Renderer { get; }
    bool IsActive { get; }
    bool Prepared { get; }
    bool NativePrepared { get; }
    int PixelWidth { get; }
    int PixelHeight { get; }
    float PixelsPerUnitValue { get; }
    Action OnFirstFrame { get; set; }

    void SetVisible(bool visible);
    void Pause();
    void Resume();
    void Tick();
    void Dispose();
}

public static class MediaSurfaces
{
    // Android のゲーム純正 libunity には VideoPlayer が無いので常にフレームパックで再生する。
    // PC でも EndKnot_DATA/use_frame_video.txt がある間はフレームパックで再生する (見え方の比較用)。
    public static bool UseFramePack
    {
        get
        {
#if ANDROID
            return true;
#else
            if (!VideoSurface.IsSupported) return true;
            try { return File.Exists($"{Main.DataPath}/EndKnot_DATA/use_frame_video.txt"); }
            catch { return false; }
#endif
        }
    }

    /// <summary>
    /// 再生面を作る。フレームパック再生なら <paramref name="framePackFile"/> を、VideoPlayer 再生なら
    /// <paramref name="resolveVideoPath"/> が返す動画ファイルを使う。素材が無い・生成に失敗した時は null。
    /// </summary>
    public static IMediaSurface Create(string framePackFile, Func<string> resolveVideoPath, Transform parent, string logTag)
    {
        if (UseFramePack)
        {
            FramePack pack = FramePack.Get(framePackFile);
            if (pack == null) { Logger.Info($"no frame pack: {framePackFile}", logTag); return null; }

            var player = new FrameSequencePlayer();
            if (player.TryCreate(pack, parent)) return player;

            player.Dispose();
            return null;
        }

#if ANDROID
        return null;
#else
        string path = resolveVideoPath?.Invoke();
        if (string.IsNullOrWhiteSpace(path)) { Logger.Info("no video file", logTag); return null; }

        var surface = new VideoSurface();
        if (surface.TryCreate(path, parent)) return surface;

        surface.Dispose();
        return null;
#endif
    }
}
