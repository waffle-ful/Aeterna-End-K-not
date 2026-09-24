using System;
using UnityEngine;

namespace EndKnot.Modules.Media;

// フレームパック (連番 JPG) を 1 枚の Texture2D へ順に流し込んで動画のように見せる。
// VideoPlayer を持たない環境 (Android のゲーム純正 libunity) 向けだが、使う API は
// ImageConversion.LoadImage と Texture2D の生データ書き込みだけなので PC でも同じように動く。
// ネットワーク送信は一切行わない (ホストローカル描画のみ)。
public sealed class FrameSequencePlayer : IMediaSurface
{
    private const float PixelsPerUnit = 100f;

    public GameObject GameObject { get; private set; }
    public SpriteRenderer Renderer { get; private set; }
    public bool IsActive { get; private set; }
    public bool Prepared { get; private set; }
    public bool NativePrepared => _pack != null;
    public int PixelWidth => _pack?.Width ?? 0;
    public int PixelHeight => _pack?.Height ?? 0;
    public float PixelsPerUnitValue => PixelsPerUnit;
    public Action OnFirstFrame { get; set; }

    private FramePack _pack;

    // 表示用。毎コマ書き換えるので readable のまま持つ (Apply で CPU 側を捨てない)。
    private Texture2D _display;

    // 透明度ありの時だけ使う、縦 2 段の JPG を解凍する作業用テクスチャ。
    private Texture2D _scratch;

    private Sprite _sprite;
    private bool _visible = true;
    private bool _paused;
    private int _shownFrame = -1;
    private double _clock;
    private float _lastRealtime;

    public bool TryCreate(FramePack pack, Transform parent)
    {
        if (pack == null || pack.FrameCount == 0) return false;

        try
        {
            _pack = pack;

            GameObject = new GameObject("EndKnotFrameSequence");
            GameObject.transform.SetParent(parent, false);
            GameObject.layer = parent.gameObject.layer; // SetParent は layer を継がない

            Renderer = GameObject.AddComponent<SpriteRenderer>();
            var shader = Shader.Find("Sprites/Default");
            if (shader != null) Renderer.material = new Material(shader);
            Renderer.enabled = false;

            // 透明度なしは LoadImage が直接この面へ解凍する (RGB24 に作り替わる)。
            // 透明度ありは作業用テクスチャで解凍してから色と透明度を合成して書き込む。
            _display = new Texture2D(pack.Width, pack.Height, pack.StackedAlpha ? TextureFormat.RGBA32 : TextureFormat.RGB24, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };

            if (pack.StackedAlpha)
            {
                _scratch = new Texture2D(2, 2, TextureFormat.RGB24, false) { hideFlags = HideFlags.HideAndDontSave };
            }

            _sprite = Sprite.Create(_display, new Rect(0f, 0f, pack.Width, pack.Height), new Vector2(0.5f, 0.5f), PixelsPerUnit, 0, SpriteMeshType.FullRect);
            _sprite.hideFlags = HideFlags.HideAndDontSave;
            Renderer.sprite = _sprite;

            _clock = 0;
            _lastRealtime = Time.realtimeSinceStartup;
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

    public void SetVisible(bool visible)
    {
        _visible = visible;
        if (Renderer != null) Renderer.enabled = visible && Prepared;
    }

    public void Pause() => _paused = true;

    public void Resume()
    {
        if (!_paused) return;
        _paused = false;
        _lastRealtime = Time.realtimeSinceStartup; // 止めていた間の時間は進めない
    }

    // 1 コマの解凍コストは端末ごとに違うので、最初の DecodeSampleCount コマ分だけ計って 1 行にまとめる
    // (毎コマ出すとログが溢れる)。重ければパックの幅を下げる判断材料。
    private const int DecodeSampleCount = 60;
    private int _decodeSamples;
    private long _decodeTotalTicks;
    private long _decodeMaxTicks;

    private void ReportDecodeCost(long ticks)
    {
        if (_decodeSamples >= DecodeSampleCount) return;
        _decodeSamples++;
        _decodeTotalTicks += ticks;
        if (ticks > _decodeMaxTicks) _decodeMaxTicks = ticks;
        if (_decodeSamples < DecodeSampleCount) return;
        double toMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        Logger.Info($"decode cost over {DecodeSampleCount} frames: avg {_decodeTotalTicks * toMs / DecodeSampleCount:0.0}ms, max {_decodeMaxTicks * toMs:0.0}ms ({_pack.Width}x{_pack.Height} alpha={_pack.StackedAlpha})", "FrameSequence");
    }

    // 呼び出し側から毎 FixedUpdate 叩かれる。コマが変わった時だけ解凍する。
    public void Tick()
    {
        if (!IsActive || _pack == null) return;

        try
        {
            float now = Time.realtimeSinceStartup;
            if (!_paused) _clock += now - _lastRealtime;
            _lastRealtime = now;

            int frame = (int)(_clock * _pack.Fps) % _pack.FrameCount;
            if (frame == _shownFrame && Prepared) return;

            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (!ShowFrame(frame))
            {
                IsActive = false; // 毎フレ例外スパム禁止: 二度と描かない
                return;
            }
            ReportDecodeCost(System.Diagnostics.Stopwatch.GetTimestamp() - t0);

            _shownFrame = frame;

            if (!Prepared)
            {
                Prepared = true;
                Renderer.enabled = _visible;
                Logger.Info($"first frame shown: {_pack.Width}x{_pack.Height} @ {_pack.Fps:0.##}fps, frames={_pack.FrameCount}, alpha={_pack.StackedAlpha}, sorting={Renderer.sortingLayerID}/{Renderer.sortingOrder}, layer={GameObject.layer}", "FrameSequence");
                try { OnFirstFrame?.Invoke(); }
                catch (Exception e) { Utils.ThrowException(e); }
            }
        }
        catch (Exception e)
        {
            Utils.ThrowException(e);
            IsActive = false;
        }
    }

    private bool ShowFrame(int index)
    {
        if (!_pack.StackedAlpha)
        {
            if (_display.LoadImage(_pack.GetFrame(index), false)) return true;
            Logger.Warn($"frame {index} could not be decoded", "FrameSequence");
            return false;
        }

        if (!_scratch.LoadImage(_pack.GetFrame(index), false))
        {
            Logger.Warn($"frame {index} could not be decoded", "FrameSequence");
            return false;
        }

        int width = _pack.Width;
        int height = _pack.Height;
        TextureFormat format = _scratch.format;
        int bytesPerPixel = format switch
        {
            TextureFormat.RGB24 => 3,
            TextureFormat.RGBA32 => 4,
            _ => 0
        };

        if (bytesPerPixel == 0 || _scratch.width != width || _scratch.height != height * 2)
        {
            Logger.Warn($"unexpected decoded frame: {_scratch.width}x{_scratch.height} {format} (expected {width}x{height * 2} RGB24/RGBA32)", "FrameSequence");
            return false;
        }

        IntPtr source = _scratch.GetWritableImageData(0);
        IntPtr target = _display.GetWritableImageData(0);
        if (source == IntPtr.Zero || target == IntPtr.Zero)
        {
            Logger.Warn("texture pixel data is not accessible", "FrameSequence");
            return false;
        }

        // テクスチャの行は下から上へ並ぶ。JPG の上半分 (色) は後ろ半分の行、下半分 (透明度) は前半分の行。
        unsafe
        {
            byte* src = (byte*)source;
            byte* dst = (byte*)target;
            int rowBytes = width * bytesPerPixel;

            for (int y = 0; y < height; y++)
            {
                byte* alphaRow = src + (long)y * rowBytes;
                byte* colorRow = src + (long)(y + height) * rowBytes;
                byte* outRow = dst + (long)y * width * 4;

                for (int x = 0; x < width; x++)
                {
                    int s = x * bytesPerPixel;
                    int d = x * 4;
                    outRow[d] = colorRow[s];
                    outRow[d + 1] = colorRow[s + 1];
                    outRow[d + 2] = colorRow[s + 2];
                    outRow[d + 3] = alphaRow[s];
                }
            }
        }

        _display.Apply(false, false);
        return true;
    }

    public void Dispose()
    {
        IsActive = false;
        Prepared = false;

        if (_sprite != null)
        {
            UnityEngine.Object.Destroy(_sprite);
            _sprite = null;
        }

        if (_display != null)
        {
            UnityEngine.Object.Destroy(_display);
            _display = null;
        }

        if (_scratch != null)
        {
            UnityEngine.Object.Destroy(_scratch);
            _scratch = null;
        }

        if (Renderer != null && Renderer.material != null)
            UnityEngine.Object.Destroy(Renderer.material); // instanced material は自動回収されない

        if (GameObject != null)
        {
            UnityEngine.Object.Destroy(GameObject);
            GameObject = null;
        }

        Renderer = null;
        _pack = null; // パック本体は FramePack のキャッシュが持ち続ける
    }
}
