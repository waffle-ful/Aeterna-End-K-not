using System;
using System.IO;
using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace EndKnot.Modules;

// Backrooms ロビーの環境音 (蛍光灯ハム / 空気感) を BGM に重ねて流す。
//
// 設計上の罠回避メモ:
//   ・SoundManager.Instance.PlaySound() 経由で出すと SilenceVanillaAudio() が
//     毎フレーム soundPlayers を走査して .Stop() するので、ここでは独自 GameObject に
//     生 AudioSource をぶら下げる。SoundManager の管理外なので silence パスに巻き込まれない。
//   ・GO は DontDestroyOnLoad にしない。ロビー → メインメニュー / ロビー → ゲーム
//     どちらでもシーン unload で自然に AudioSource ごと消える (= 退室で自動停止)。
//     `/bbexit` でロビーに残ったまま止めるケースだけ明示的 Stop() が要る。
//   ・Backrooms に入っている間だけループ再生。Enter/Exit/OnGameStart/OnLobbyReload +
//     LobbyBehaviour.OnDestroy の 5 経路から Stop() を呼ぶ。
//   ・WAV パーサは format=1 (PCM 16/24bit) と format=3 (IEEE float 32bit) を扱う。
//     CustomSoundsManager.LoadWAV は 16bit PCM mono 決め打ちで既存 SFX 専用なので
//     ここでは触らない (32bit float を 16bit 整数として解釈してノイズ化する罠)。
public static class BackroomsAmbient
{
    private const string AmbientName = "lobby-ambient";

    // BGM 本体より少し小さく重ねる。アンビエントは「空気」なので前に出すぎないように
    private const float AmbientMix = 0.6f;

    public static readonly string AmbientPath = $"{Environment.CurrentDirectory.Replace(@"\", "/")}/BepInEx/resources/Backrooms/";

    // AudioClip は HideFlags.DontUnloadUnusedAsset を付けてシーン unload を生き残らせる。
    // これを忘れると Resources.UnloadUnusedAssets() (scene 遷移時 auto 呼び) で消されて
    // 2 回目以降の Start() で「無音再入室」バグになる。_clip != null の Unity fake-null も
    // 罠で、_loadAttempted 系の retry-guard も入れない方向 (Unity-destroyed ref は再ロードしたい)
    private static AudioClip _clip;
    private static GameObject _go;   // シーン unload で死ぬ — 次 Start で EnsureSource が再生成
    private static AudioSource _source; // _go の子コンポーネント、同じく

    public static void Start()
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return;
            if (!(Main.EnableBGM?.Value ?? false)) return;

            AudioClip clip = LoadClip();
            if (clip == null) return;

            EnsureSource();
            if (_source == null) return;

            float vol = (Main.BGMVolume?.Value ?? 0.7f) * AmbientMix;
            _source.clip = clip;
            _source.loop = true;
            _source.volume = vol;
            if (!_source.isPlaying) _source.Play();
        }
        catch (Exception ex) { Utils.ThrowException(ex); }
    }

    public static void Stop()
    {
        try
        {
            // Unity 演算子で _source は destroyed Unity ref も null 判定される (fake-null)
            if (_source != null && _source.isPlaying) _source.Stop();
        }
        catch { /* AudioSource may already be torn down by scene unload — ignore */ }
    }

    // 音量設定をライブ反映 (オプション変更 → 即時音量更新したい用途で温存)
    public static void RefreshVolume()
    {
        if (_source == null) return;
        _source.volume = (Main.BGMVolume?.Value ?? 0.7f) * AmbientMix;
    }

    private static void EnsureSource()
    {
        // Unity の overloaded == で destroyed ref は null 扱い → 自動的に再生成路に入る
        if (_source != null) return;

        _go = new GameObject("BackroomsAmbient");
        _go.hideFlags |= HideFlags.HideInHierarchy;

        _source = _go.AddComponent<AudioSource>();
        _source.playOnAwake = false;
        _source.spatialBlend = 0f; // 2D — プレイヤー位置に依存しない部屋全体の空気
        _source.priority = 64;     // BGM (default 128) より優先度低め、SFX より高め
    }

    private static AudioClip LoadClip()
    {
        // Unity overloaded == は destroyed ref も null 判定 → 自動的に再ロード路に入る
        if (_clip != null) return _clip;

        try
        {
            if (!Directory.Exists(AmbientPath))
            {
                Directory.CreateDirectory(AmbientPath);
                DirectoryInfo folder = new(AmbientPath);
                if ((folder.Attributes & FileAttributes.Hidden) != FileAttributes.Hidden)
                    folder.Attributes = FileAttributes.Hidden;
            }

            string diskPath = AmbientPath + AmbientName + ".wav";

            // disk に user override 版があれば優先、なければ embedded を一度だけ展開
            if (!File.Exists(diskPath))
            {
                Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"EndKnot.Resources.Sounds.Backrooms.{AmbientName}.wav");
                if (stream == null)
                {
                    Logger.Warn($"BackroomsAmbient WAV not found (disk or embedded): {AmbientName}", "BackroomsAmbient");
                    return null;
                }

                using FileStream fs = File.Create(diskPath);
                stream.CopyTo(fs);
            }

            _clip = LoadWavStrict(diskPath);
            // シーン unload 時の Resources.UnloadUnusedAssets() で消されないように。
            // これを忘れると 2 回目以降の lobby 入室で「無音」になる罠 (2026-05-23)
            if (_clip != null) _clip.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            return _clip;
        }
        catch (Exception ex)
        {
            Logger.Exception(ex, "BackroomsAmbient.LoadClip");
            return null;
        }
    }

    // CustomSoundsManager.LoadWAV は 16bit PCM mono 決め打ちで、IEEE float / stereo を
    // 解釈できずノイズ化する。BackroomsAmbient 用に最低限のフォーマット対応版を持つ。
    //   ・format 1 (PCM): 16bit, 24bit
    //   ・format 3 (IEEE float): 32bit
    //   ・mono / stereo どちらも (Unity AudioClip にチャンネル数を渡してそのまま再生)
    private static AudioClip LoadWavStrict(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        if (data.Length < 44) throw new IOException("WAV too small");
        if (data[0] != (byte)'R' || data[1] != (byte)'I' || data[2] != (byte)'F' || data[3] != (byte)'F') throw new IOException("Not RIFF");
        if (data[8] != (byte)'W' || data[9] != (byte)'A' || data[10] != (byte)'V' || data[11] != (byte)'E') throw new IOException("Not WAVE");

        int fmtPos = -1;
        int dataPos = -1;
        int dataSize = 0;

        int pos = 12;
        while (pos + 8 <= data.Length)
        {
            int chunkId = BitConverter.ToInt32(data, pos);
            int chunkSize = BitConverter.ToInt32(data, pos + 4);
            int body = pos + 8;
            // 'fmt ' = 0x20746D66, 'data' = 0x61746164 (little endian on x86)
            if (chunkId == 0x20746D66) { fmtPos = body; }
            else if (chunkId == 0x61746164) { dataPos = body; dataSize = chunkSize; break; }
            pos = body + chunkSize;
            if ((chunkSize & 1) == 1) pos++; // RIFF: chunk body は 2byte 境界に padding
        }
        if (fmtPos < 0 || dataPos < 0) throw new IOException("WAV missing fmt/data chunk");

        ushort audioFormat = BitConverter.ToUInt16(data, fmtPos + 0);
        ushort channels    = BitConverter.ToUInt16(data, fmtPos + 2);
        int    sampleRate  = BitConverter.ToInt32(data, fmtPos + 4);
        ushort bps         = BitConverter.ToUInt16(data, fmtPos + 14);

        if (channels < 1 || channels > 2) throw new IOException($"WAV unsupported channels={channels}");
        if (bps == 0) throw new IOException("WAV bitsPerSample=0");

        int bytesPerSample = bps / 8;
        int totalSamples = dataSize / bytesPerSample; // interleaved sample 数
        int samplesPerChannel = totalSamples / channels;

        float[] interleaved = new float[totalSamples];

        switch (audioFormat, bps)
        {
            case (1, 16): // PCM 16-bit
                for (int i = 0; i < totalSamples; i++)
                {
                    int o = dataPos + i * 2;
                    short s = (short)(data[o] | (data[o + 1] << 8));
                    interleaved[i] = s / 32768f;
                }
                break;

            case (1, 24): // PCM 24-bit
                for (int i = 0; i < totalSamples; i++)
                {
                    int o = dataPos + i * 3;
                    int v = (data[o] << 8) | (data[o + 1] << 16) | (data[o + 2] << 24);
                    interleaved[i] = (v >> 8) / 8388608f;
                }
                break;

            case (3, 32): // IEEE float 32-bit
                Buffer.BlockCopy(data, dataPos, interleaved, 0, totalSamples * 4);
                break;

            default:
                throw new IOException($"WAV unsupported (audioFormat={audioFormat}, bps={bps})");
        }

        Il2CppStructArray<float> il2cppBuf = new(totalSamples);
        // Managed float[] -> Il2CppStructArray<float> via Marshal.Copy (per-element indexer is a trap — MEMORY.md)。
        // lobby-ambient.wav は ~3.5M サンプルあり、インデクサループはロビー入室時に ~150ms 級のヒッチ源になる。
        System.Runtime.InteropServices.Marshal.Copy(interleaved, 0, IntPtr.Add(il2cppBuf.Pointer, IntPtr.Size * 4), totalSamples);

        AudioClip clip = AudioClip.Create(Path.GetFileNameWithoutExtension(path), samplesPerChannel, channels, sampleRate, false);
        clip.SetData(il2cppBuf, 0);

        Logger.Info($"BackroomsAmbient WAV loaded: format={audioFormat} ch={channels} rate={sampleRate} bps={bps} samples/ch={samplesPerChannel}", "BackroomsAmbient");
        return clip;
    }
}
