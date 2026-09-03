using Hazel;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using UnityEngine;

namespace EndKnot.Modules;

public static class CustomSoundsManager
{
    internal static readonly string SoundsPath = $"{Environment.CurrentDirectory.Replace(@"\", "/")}/BepInEx/resources/";
    private static readonly string[] SupportedExtensions = [".wav", ".ogg", ".mp3"];

    // PlaySoundRPC の broadcast を (player, sound) ごとに「同一秒に 1 回」へ間引く dedup 用。
    // bare timestamp ではなくキー付きにして、同一秒に鳴る別々の音 (ダブルキル等) は落とさない。
    public static readonly Dictionary<(byte PlayerId, Sounds Sound), long> LastSoundRPCTS = [];

    public static void RPCPlayCustomSound(this PlayerControl pc, string sound, float volume = 1f, float pitch = 1f, bool force = false)
    {
        try
        {
            if (!force && (!AmongUsClient.Instance.AmHost || !pc.IsModdedClient())) return;

            if (!pc || PlayerControl.LocalPlayer.PlayerId == pc.PlayerId)
            {
                Play(sound, volume, pitch);
                return;
            }

            MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(PlayerControl.LocalPlayer.NetId, (byte)CustomRPC.PlayCustomSound, SendOption.None, pc.OwnerId);
            writer.Write(sound);
            writer.Write(volume);
            writer.Write(pitch);
            AmongUsClient.Instance.FinishRpcImmediately(writer);
        }
        catch (Exception e) { Utils.ThrowException(e); }
    }

    public static void RPCPlayCustomSoundAll(string sound, float volume = 1f, float pitch = 1f)
    {
        try
        {
            if (!AmongUsClient.Instance.AmHost) return;

            Play(sound, volume, pitch);
        
            MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(PlayerControl.LocalPlayer.NetId, (byte)CustomRPC.PlayCustomSound, SendOption.None);
            writer.Write(sound);
            writer.Write(volume);
            writer.Write(pitch);
            AmongUsClient.Instance.FinishRpcImmediately(writer);
        }
        catch (Exception e) { Utils.ThrowException(e); }
    }

    public static void ReceiveRPC(MessageReader reader)
    {
        Play(reader.ReadString(), reader.ReadSingle(), reader.ReadSingle());
    }

    public static void Play(string sound, float volume = 1f, float pitch = 1f)
    {
        try
        {
            if (!Constants.ShouldPlaySfx() || !Main.EnableCustomSoundEffect.Value || !OperatingSystem.IsWindows()) return;

            string key = ResolveSoundKey(sound);
            if (key == null)
            {
                Logger.Warn($"Could not find sound: {sound}", "CustomSounds");
                return;
            }

            StartPlay(key, volume, pitch);
            Logger.Msg($"Playing sound: {sound} ({Path.GetExtension(key)})", "CustomSounds");
        }
        catch (Exception e) { Utils.ThrowException(e); }
    }

    // Play と同じ解決経路で鳴らすが、再生中の AudioSource を返してフェード等を外部から制御できるようにする。
    // PlaySoundImmediate は void なので、音量を時間変化させたい呼び出し側 (大地震のフェードアウト等) はこちらを使う。
    // 見つからない / 無効設定なら null を返す。ホストローカル・送信ゼロ。
    public static AudioSource PlayControllable(string sound, float volume = 1f)
    {
        try
        {
            if (!Constants.ShouldPlaySfx() || !Main.EnableCustomSoundEffect.Value || !OperatingSystem.IsWindows()) return null;

            string key = ResolveSoundKey(sound);
            if (key == null)
            {
                Logger.Warn($"Could not find sound: {sound}", "CustomSounds");
                return null;
            }

            AudioClip clip = LoadClip(key);
            if (clip == null) return null;

            Logger.Msg($"Playing sound (controllable): {sound} ({Path.GetExtension(key)})", "CustomSounds");
            return SoundManager.Instance.PlaySound(clip, false, volume);
        }
        catch (Exception e) { Utils.ThrowException(e); return null; }
    }

    // ── モッドクライアント効果音同期 (CustomRPC.ControllableSound) ──────────────────
    // 「offset 付き再生」「sound 名指定のフェード停止」を sub-op byte 1本の RPC で運ぶ。
    // 非モッド客は未知 RPC として無視。受信側は sound 名キーの registry で管理し (同名の再再生は
    // 旧セッションを置換)、会議・ゲーム終了時はローカル watchdog が自走停止する (stop RPC 欠落への保険)。
    // ホスト自身の再生はここを通らない (呼び出し側が PlayControllable でローカル管理する)。

    private static readonly Dictionary<string, (AudioSource Source, AudioClip Clip)> ControllableSessions = [];

    public static void RpcPlayControllableAll(string sound, float volume, float startOffset)
    {
        try
        {
            if (!AmongUsClient.Instance.AmHost) return;

            MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(PlayerControl.LocalPlayer.NetId, (byte)CustomRPC.ControllableSound, SendOption.Reliable);
            writer.Write((byte)0);
            writer.Write(sound);
            writer.Write(volume);
            writer.Write(startOffset);
            AmongUsClient.Instance.FinishRpcImmediately(writer);
        }
        catch (Exception e) { Utils.ThrowException(e); }
    }

    public static void RpcStopControllableAll(string sound, float fadeSeconds)
    {
        try
        {
            if (!AmongUsClient.Instance.AmHost) return;

            MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(PlayerControl.LocalPlayer.NetId, (byte)CustomRPC.ControllableSound, SendOption.Reliable);
            writer.Write((byte)1);
            writer.Write(sound);
            writer.Write(fadeSeconds);
            AmongUsClient.Instance.FinishRpcImmediately(writer);
        }
        catch (Exception e) { Utils.ThrowException(e); }
    }

    public static void ReceiveControllableRPC(MessageReader reader)
    {
        byte op = reader.ReadByte();
        string sound = reader.ReadString();
        switch (op)
        {
            case 0:
            {
                float volume = reader.ReadSingle();
                float offset = reader.ReadSingle();
                PlayControllableSession(sound, volume, offset);
                break;
            }
            case 1:
            {
                float fade = reader.ReadSingle();
                StopControllableSession(sound, fade);
                break;
            }
        }
    }

    private static void PlayControllableSession(string sound, float volume, float startOffset)
    {
        StopControllableSession(sound, 0f);

        AudioSource src = PlayControllable(sound, volume);
        if (src == null) return;

        if (startOffset > 0f && startOffset < src.clip.length) src.time = startOffset;
        ControllableSessions[sound] = (src, src.clip);
        if (Main.Instance != null) Main.Instance.StartCoroutine(ControllableWatchdog(sound, src, src.clip));
    }

    private static void StopControllableSession(string sound, float fadeSeconds)
    {
        if (!ControllableSessions.Remove(sound, out var session)) return;
        if (session.Source == null || session.Source.clip != session.Clip || !session.Source.isPlaying) return;

        if (fadeSeconds <= 0f)
        {
            session.Source.Stop();
            return;
        }

        if (Main.Instance != null) Main.Instance.StartCoroutine(FadeOutSession(session.Source, session.Clip, fadeSeconds));
        else session.Source.Stop();
    }

    // SoundManager のプール済み AudioSource は別の音に再利用されうるため、掴んだ時点の clip と
    // 一致している間だけ触る (AudienceCutscene.FadeSoundRoutine と同じ罠)。yield は null のみ。
    private static IEnumerator ControllableWatchdog(string sound, AudioSource src, AudioClip ownClip)
    {
        while (src != null && src.clip == ownClip && src.isPlaying)
        {
            // registry から外れた = 明示 stop 済み or 同名再再生で置換済み → この watchdog は退役
            if (!ControllableSessions.TryGetValue(sound, out var session) || session.Source != src) yield break;

            // ホストの stop RPC を待たずに自走停止する保険: 会議・追放・ゲーム終了で即止める
            if (!GameStates.InGame || GameStates.IsMeeting || ExileController.Instance)
            {
                ControllableSessions.Remove(sound);
                src.Stop();
                yield break;
            }

            yield return null;
        }

        // 自然終了/クリップ再利用で抜けたら registry を掃除 (自分が現役登録のままの場合のみ)
        if (ControllableSessions.TryGetValue(sound, out var s) && s.Source == src) ControllableSessions.Remove(sound);
    }

    private static IEnumerator FadeOutSession(AudioSource src, AudioClip ownClip, float fadeSeconds)
    {
        float startVol = src.volume;
        for (float t = 0f; t < fadeSeconds; t += Time.deltaTime)
        {
            if (src == null || src.clip != ownClip || !src.isPlaying) yield break;

            src.volume = startVol * Mathf.Clamp01(1f - t / fadeSeconds);
            yield return null;
        }

        if (src != null && src.clip == ownClip)
        {
            src.volume = 0f;
            src.Stop();
        }
    }

    // クリップ長 (秒) を返す。解決とデコードは Play と同じ経路 (audioCache 済みなら即答)。
    // 見つからない/再生不能環境では 0。再生開始前に「音の尻を演出タイミングに合わせる」逆算をしたい呼び出し側用。
    // EnableCustomSoundEffect ではゲートしない — ホストが音を切っていてもクライアント同期の逆算には長さが要る。
    public static float GetClipLength(string sound)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return 0f;

            string key = ResolveSoundKey(sound);
            if (key == null) return 0f;

            AudioClip clip = LoadClip(key);
            return clip ? clip.length : 0f;
        }
        catch (Exception e)
        {
            Utils.ThrowException(e);
            return 0f;
        }
    }

    // ── 音源の解決 (ディスク優先 → 埋込はメモリ内デコード) ────────────────────
    // 埋込音声を BepInEx/resources/ へ書き出す方式は廃止した。素材の再配布条項 (DOVA-SYNDROME の
    // 「コンバート等を行わず、エンドユーザーが容易に音源ファイルに音声ファイルとしてアクセス、複製が
    // 可能な状態での利用」禁止) には、DLL 埋め込みより先にディスク展開の方が当たるため。
    // 解決結果は「実ファイルパス」か "embedded:<リソース名>" の擬似キーで、どちらも OpenSound() で
    // Stream として開ける。ホストが自分で resources/ に置いた差し替えファイルは従来どおり優先。
    // ⚠️ このキーは audioCache のキーそのものなので、LoadClip / StartPlay / GetClipLength /
    //    プリロード経路で必ず同じ文字列を使うこと (ズレると二重デコードか無音になる)。
    internal const string EmbeddedKeyPrefix = "embedded:";
    private const string SoundResourcePrefix = "EndKnot.Resources.Sounds.";

    internal static bool IsEmbeddedKey(string key) => key != null && key.StartsWith(EmbeddedKeyPrefix, StringComparison.Ordinal);

    // 埋込リソース名の一覧は不変なので一度だけ作る。存在確認のたびに GetManifestResourceStream で
    // 開いて捨てると、音を鳴らすたびに Stream が 1〜3 個ゴミになる (解決は毎再生走る)。
    private static readonly Lazy<HashSet<string>> EmbeddedResourceNames = new(() =>
        new HashSet<string>(Assembly.GetExecutingAssembly().GetManifestResourceNames(), StringComparer.Ordinal));

    // 埋込リソースが存在すれば擬似キーを、無ければ null を返す (BGMManager からも使う)。
    internal static string TryEmbeddedKey(string resourceName)
        => EmbeddedResourceNames.Value.Contains(resourceName) ? EmbeddedKeyPrefix + resourceName : null;

    // 解決キーを読み出し用 Stream として開く (呼び出し側が using で閉じる)。
    // System.IO + マネージド Stream のみなのでバックグラウンドスレッドから呼んでよい。
    private static Stream OpenSound(string key)
    {
        Stream stream = IsEmbeddedKey(key)
            ? Assembly.GetExecutingAssembly().GetManifestResourceStream(key[EmbeddedKeyPrefix.Length..])
            : File.OpenRead(key);

        return stream ?? throw new IOException($"Sound source not found: {key}");
    }

    // AudioClip に付ける表示名。擬似キーをそのまま Path.GetFileNameWithoutExtension に通すと
    // "embedded:EndKnot.Resources.Sounds.…" が名前として残るので、リソース名側から素の音名を取る。
    private static string ClipNameOf(string key)
    {
        if (!IsEmbeddedKey(key)) return Path.GetFileNameWithoutExtension(key);

        string res = key[EmbeddedKeyPrefix.Length..];
        if (res.StartsWith(SoundResourcePrefix, StringComparison.Ordinal)) res = res[SoundResourcePrefix.Length..];

        int dot = res.LastIndexOf('.');
        return dot > 0 ? res[..dot] : res;
    }

    // キーの中身を丸ごと byte[] へ読む (WAV デコード用)。Stream.Read は要求より短く返しうるので
    // 必ず読み切る (File.ReadAllBytes と違い、短読みを見逃すと無言で音声が途中で切れる)。
    private static byte[] ReadAllBytes(string key)
    {
        using Stream stream = OpenSound(key);

        if (!stream.CanSeek)
        {
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }

        byte[] data = new byte[stream.Length];
        int off = 0;

        while (off < data.Length)
        {
            int n = stream.Read(data, off, data.Length - off);
            if (n <= 0) break;

            off += n;
        }

        if (off < data.Length) Array.Resize(ref data, off);
        return data;
    }

    // BepInEx/resources 内の実ファイル → 埋込リソース の順で音源キーを解決する (無ければ null)。
    private static string ResolveSoundKey(string sound)
    {
        if (!Directory.Exists(SoundsPath)) Directory.CreateDirectory(SoundsPath);

        DirectoryInfo folder = new(SoundsPath);
        if ((folder.Attributes & FileAttributes.Hidden) != FileAttributes.Hidden) folder.Attributes = FileAttributes.Hidden;

        foreach (string ext in SupportedExtensions)
        {
            string candidate = SoundsPath + sound + ext;
            if (File.Exists(candidate)) return candidate;
        }

        foreach (string ext in SupportedExtensions)
        {
            string key = TryEmbeddedKey(SoundResourcePrefix + sound + ext);
            if (key != null) return key;
        }

        return null;
    }

    private static readonly Dictionary<string, AudioClip> audioCache = [];

    private static void StartPlay(string key, float volume = 1f, float pitch = 1f)
    {
        AudioClip clip = LoadClip(key);
        if (clip)
            SoundManager.Instance.PlaySoundImmediate(clip, false, volume);
    }

    // キー単位でデコード結果をキャッシュしつつ AudioClip を返す (初回のみデコード)。
    // シーン遷移 (ゲーム終了→ロビー等) の暗黙 UnloadUnusedAssets は、managed dict からしか参照されていない
    // AudioClip をネイティブ側で破棄する。破棄済みクリップは Play 側の `if (clip)` が黙って false になり
    // 「ログは出るのに無音」になる (2026-07-14 実機確認) ため、fake-null を検出して再デコードし、
    // 以後は DontUnloadUnusedAsset で保護する。
    private static AudioClip LoadClip(string key)
    {
        if (!audioCache.TryGetValue(key, out var clip) || !clip)
        {
            string ext = Path.GetExtension(key).ToLowerInvariant();
            clip = ext switch
            {
                ".ogg" => LoadOGG(key),
                ".mp3" => LoadMP3(key),
                _ => LoadWAV(key)
            };
            if (clip) clip.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            audioCache[key] = clip;
        }

        return clip;
    }

    internal static AudioClip LoadWAV(string key)
    {
        var sw = Stopwatch.StartNew();
        (float[] buffer, int read, int channels, int sampleRate) = DecodeWav(key);
        long decodeMs = sw.ElapsedMilliseconds;

        AudioClip clip = CreateClip(key, buffer, read, channels, sampleRate, out long copyMs);
        Logger.Info($"[WAV: Channels={channels}, SampleRate={sampleRate}, SamplesRead={read}, decodeMs={decodeMs}, copyMs={copyMs}]", "CustomSounds");
        return clip;
    }

    internal static AudioClip LoadOGG(string key)
    {
        var sw = Stopwatch.StartNew();
        (float[] buffer, int read, int channels, int sampleRate) = DecodeOgg(key);
        long decodeMs = sw.ElapsedMilliseconds;

        (bool dsApplied, string dsMessage) = TryDownsampleBgm(key, buffer, ref read, channels, ref sampleRate);
        if (dsMessage != null)
        {
            if (dsApplied) Logger.Info(dsMessage, "CustomSounds");
            else Logger.Warn(dsMessage, "CustomSounds");
        }

        AudioClip clip = CreateClip(key, buffer, read, channels, sampleRate, out long copyMs);
        Logger.Info($"[OGG: Channels={channels}, SampleRate={sampleRate}, SamplesRead={read}, decodeMs={decodeMs}, copyMs={copyMs}]", "CustomSounds");
        return clip;
    }

    internal static AudioClip LoadMP3(string key)
    {
        var sw = Stopwatch.StartNew();
        (float[] buffer, int read, int channels, int sampleRate) = DecodeMp3(key);
        long decodeMs = sw.ElapsedMilliseconds;

        AudioClip clip = CreateClip(key, buffer, read, channels, sampleRate, out long copyMs);
        Logger.Info($"[MP3: Channels={channels}, SampleRate={sampleRate}, SamplesRead={read}, decodeMs={decodeMs}, copyMs={copyMs}]", "CustomSounds");
        return clip;
    }

    // OGG/MP3 のデコード本体。System.IO + マネージドデコーダのみ (Unity/Il2Cpp API 不使用) なので
    // バックグラウンドスレッドから呼んでよい。AudioClip 化 (CreateClip) はメインスレッド専用。
    // Stream 版コンストラクタを使うので、埋込リソースを直接 (ディスクを経由せず) デコードできる。
    private static (float[] Buffer, int Read, int Channels, int SampleRate) DecodeOgg(string key)
    {
        using Stream stream = OpenSound(key);
        // 第2引数 (dispose 時に stream を閉じるか) はバージョンによって意味が揺れうるので false 固定にし、
        // stream の寿命は上の using でこちらが持つ。
        using var reader = new NVorbis.VorbisReader(stream, false);
        int channels = reader.Channels;
        int sampleRate = reader.SampleRate;
        long totalSamples = reader.TotalSamples;

        if (totalSamples <= 0) throw new IOException($"OGG TotalSamples invalid: {totalSamples}");

        int interleavedLen = (int)(totalSamples * channels);
        float[] buffer = RentDecodeBuffer(interleavedLen);
        int read = reader.ReadSamples(buffer, 0, interleavedLen);
        return (buffer, read, channels, sampleRate);
    }

    // WAV のマネージドデコード。System.IO のみ (Il2Cpp API 不使用) なのでバックグラウンドスレッドから
    // 呼んでよい (旧実装は Il2CppSystem.IO.File 依存でメインスレッド固定だった)。
    // 対応フォーマットは BackroomsAmbient.LoadWavStrict と同じ: format 1 (PCM 16/24bit) +
    // format 3 (IEEE float 32bit)、mono / stereo。非対応は throw して呼び出し側の握りつぶしに任せる。
    private static (float[] Buffer, int Read, int Channels, int SampleRate) DecodeWav(string key)
    {
        byte[] data = ReadAllBytes(key);
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
        if (dataPos + dataSize > data.Length) dataSize = data.Length - dataPos; // 壊れた data チャンク長への保険
        int totalSamples = dataSize / bytesPerSample; // interleaved sample 数

        float[] interleaved = RentDecodeBuffer(totalSamples);

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

        return (interleaved, totalSamples, channels, sampleRate);
    }

    private static (float[] Buffer, int Read, int Channels, int SampleRate) DecodeMp3(string key)
    {
        using Stream stream = OpenSound(key);
        using var reader = new NLayer.MpegFile(stream);
        int channels = reader.Channels;
        int sampleRate = reader.SampleRate;
        long totalInterleaved = reader.Length;

        if (totalInterleaved <= 0) throw new IOException($"MP3 Length invalid: {totalInterleaved}");

        int interleavedLen = (int)totalInterleaved;
        float[] buffer = RentDecodeBuffer(interleavedLen);
        int read = reader.ReadSamples(buffer, 0, interleavedLen);
        return (buffer, read, channels, sampleRate);
    }

    // ── BGM メモリ節約: 半分サンプルレートへのダウンサンプル ──────────────────
    // BGM 1 曲の常駐 PCM (44.1kHz float ステレオで 33〜50MB、最大 4 曲同時常駐) を半分にする。
    // 2:1 間引き前に半帯域ローパス (23 タップ windowed-sinc、カットオフ = 入力ナイキストの半分)
    // を通してエイリアシングを防ぐ。BGM 級 (PooledBufferMinFloats 以上) のみに掛け、SFX は素通し。
    private static readonly float[] HalfBandTaps = BuildHalfBandTaps();

    private static float[] BuildHalfBandTaps()
    {
        const int n = 23;
        const int m = (n - 1) / 2;
        const double fc = 0.25; // 入力サンプルレートに対する正規化カットオフ (ナイキストの半分)

        double[] h = new double[n];
        double sum = 0;

        for (int i = 0; i < n; i++)
        {
            int k = i - m;
            double sinc = k == 0 ? 2 * fc : Math.Sin(2 * Math.PI * fc * k) / (Math.PI * k);
            double window = 0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (n - 1)); // Hamming
            h[i] = sinc * window;
            sum += h[i];
        }

        float[] taps = new float[n];
        for (int i = 0; i < n; i++) taps[i] = (float)(h[i] / sum); // DC ゲイン 1 に正規化
        return taps;
    }

    // buf を interleaved フレームとみなし 2:1 間引き (半帯域 FIR 込み) して先頭へ書き戻す。
    // 出力フレーム n は入力フレーム [2n-11, 2n+11] (範囲外は端でクランプ) の加重和。
    // 出力フレーム 0..11 は自分の書き込み位置より後ろの入力フレームをまだ必要とする出力と衝突しうる
    // ため、先に別バッファへ退避してから n>=12 を直接上書きし、最後に退避分を先頭へ書き戻す。
    private static int DownsampleHalfInPlace(float[] buf, int read, int channels)
    {
        int frames = read / channels;
        int outFrames = frames / 2;
        if (outFrames <= 0) return 0;

        const int taps = 23;
        const int half = 11;

        int savedFrames = Math.Min(12, outFrames);
        float[] saved = new float[savedFrames * channels];

        for (int n = 0; n < savedFrames; n++)
            for (int c = 0; c < channels; c++)
                saved[n * channels + c] = ComputeHalfBandFrame(buf, frames, channels, n, c, taps, half);

        for (int n = 12; n < outFrames; n++)
            for (int c = 0; c < channels; c++)
                buf[n * channels + c] = ComputeHalfBandFrame(buf, frames, channels, n, c, taps, half);

        Array.Copy(saved, 0, buf, 0, saved.Length);

        return outFrames * channels;
    }

    private static float ComputeHalfBandFrame(float[] buf, int frames, int channels, int outFrame, int channel, int taps, int half)
    {
        float acc = 0f;

        for (int k = 0; k < taps; k++)
        {
            int srcFrame = 2 * outFrame + k - half;
            if (srcFrame < 0) srcFrame = 0;
            else if (srcFrame >= frames) srcFrame = frames - 1;

            acc += HalfBandTaps[k] * buf[srcFrame * channels + channel];
        }

        return acc;
    }

    // BGM 級 (PooledBufferMinFloats 以上・44.1kHz 以上) のデコード結果だけを対象に半分レート化する。
    // 失敗時は元の buffer/rate をそのまま使う (この曲だけ節約を諦める)。
    // ログはここでは出さず (applied, message) を返す — 呼び出し側が自スレッドの Logger 可否に応じて
    // 出し方を選ぶ (BgmDecodeLoop の裏スレッドは Logger 直呼び禁止、下の BgmDownsampleLogs 参照)。
    private static (bool Applied, string Message) TryDownsampleBgm(string key, float[] buffer, ref int read, int channels, ref int sampleRate)
    {
        if (Main.BgmMemorySaver == null || !Main.BgmMemorySaver.Value) return (false, null);
        if (sampleRate < 44100 || read < PooledBufferMinFloats) return (false, null);

        try
        {
            var sw = Stopwatch.StartNew();
            int origRate = sampleRate;
            int origRead = read;

            int newRead = DownsampleHalfInPlace(buffer, read, channels);

            read = newRead;
            sampleRate /= 2;
            return (true, $"BGM downsampled: key={key} {origRate}->{sampleRate}Hz floats={origRead}->{read} ({sw.ElapsedMilliseconds}ms)");
        }
        catch (Exception ex)
        {
            return (false, $"BGM downsample failed for {key}: {ex.Message}");
        }
    }

    private static AudioClip CreateClip(string key, float[] buffer, int read, int channels, int sampleRate, out long copyMs)
    {
        var sw = Stopwatch.StartNew();
        Il2CppStructArray<float> il2cppBuffer = new(read);
        // Managed float[] -> Il2CppStructArray<float> via Marshal.Copy (per-element indexer is a trap)。
        // BGM 級 (23M サンプル) でインデクサループは実測 ~1000ms、一括コピーなら数十 ms。
        System.Runtime.InteropServices.Marshal.Copy(buffer, 0, IntPtr.Add(il2cppBuffer.Pointer, IntPtr.Size * 4), read);

        AudioClip clip = AudioClip.Create(ClipNameOf(key), read / channels, channels, sampleRate, false);
        clip.SetData(il2cppBuffer, 0);
        copyMs = sw.ElapsedMilliseconds;
        ReturnDecodeBuffer(buffer); // クリップ化が済んだらデコードバッファはプールへ (以後 buffer に触らないこと)
        return clip;
    }

    // ── デコードバッファの LOH プール ─────────────────────────────────────
    // BGM 1 曲のデコードバッファは最大 ~40MB の float[] で、毎回 new すると LOH/gen2 圧が
    // 状態遷移窓 (ロビー入り・ゲーム終了直後の複数曲プリロード) に集中し、遷移中の GC 停止を
    // 底上げする。BGM 級 (>= 1M float) だけ最大 1 本使い回す (worker のデコード中と pump の
    // クリップ化中が同時に走る一瞬は在庫を待たず新規確保に譲り、低スペック機の常駐 WS を優先する)。
    // SFX 級の小物はプールしない (確保が安価で、在庫を小物で埋めると BGM が借りられない)。
    private const int PooledBufferMinFloats = 1_000_000;
    private const long PoolIdleTrimSeconds = 60; // 60秒未使用なら在庫を返上 (低スペック機の常駐 WS 削減)
    private static readonly ConcurrentQueue<float[]> DecodeBufferPool = [];
    private static long _poolLastUseTs;

    private static float[] RentDecodeBuffer(int minLen)
    {
        _poolLastUseTs = Utils.TimeStamp;

        // 足りない在庫 (過去最大曲より短い) は捨てる — 在庫は自然に「最大曲長 1 本」へ収束する
        while (DecodeBufferPool.TryDequeue(out float[] pooled))
            if (pooled.Length >= minLen)
                return pooled;

        return new float[minLen];
    }

    private static void ReturnDecodeBuffer(float[] buffer)
    {
        if (buffer == null || buffer.Length < PooledBufferMinFloats) return;
        _poolLastUseTs = Utils.TimeStamp;
        if (DecodeBufferPool.Count < 1) DecodeBufferPool.Enqueue(buffer);
    }

    /// <summary>BGM デコードが長く走っていない (曲替えの無いロビー放置等) 間、~45MB の在庫を
    /// 抱え続けないための解放弁。次の曲替えで再確保されるが、確保は PreloadWorker の裏スレッドで
    /// 起きるためフレームヒッチにはならない (プールの本義 = 遷移窓の LOH/gen2 圧集中の緩和は、
    /// 曲替えが続く間はトリムが発火しないことで保たれる)。毎秒スケジューラから呼ぶ。</summary>
    internal static void TrimIdleDecodePool()
    {
        if (DecodeBufferPool.IsEmpty) return;
        if (Utils.TimeStamp - _poolLastUseTs < PoolIdleTrimSeconds) return;

        int dropped = 0;
        long droppedFloats = 0;

        while (DecodeBufferPool.TryDequeue(out float[] pooled))
        {
            dropped++;
            droppedFloats += pooled.Length;
        }

        if (dropped > 0) Logger.Info($"decode pool trimmed after {PoolIdleTrimSeconds}s idle: {dropped} buffers / {droppedFloats * 4 / (1024 * 1024)}MB returned to GC", "CustomSounds");
    }

    // ── 起動時サウンドプリロード ──────────────────────────────────────────
    // 初回再生時の同期フルデコードがメインスレッドを止める疑いへの根治
    // (ロビー放置中 27 秒 framestall の解除フレームに Earthquake MP3 の
    // 初回デコード完了ログが一致)。圧縮系 (.ogg/.mp3) はバックグラウンドスレッドで PCM へ
    // 先行デコードし、メインスレッドは 1 fixed update に 1 クリップだけ AudioClip 化して
    // audioCache を温める (.wav も DecodeWav のマネージド化で同じ裏スレッド経路)。
    // BGM/Backrooms サブフォルダは BGMManager
    // 管轄なので対象外 (SoundsPath 直下のみ列挙)。個別失敗は握りつぶし、その音は従来どおり
    // 初回再生時の同期デコードに任せる (プリロード自体が新たな障害点にならないこと優先)。
    private const int PreloadStartDelayTicks = 500; // 起動直後の CPU 競合を避ける (fixed 50Hz × 500 ≒ 10 秒)

    // これを超えるデコード結果は SFX でなく BGM 級とみなしてプリロードしない (≒90 秒ステレオ相当)。
    // ポンプの「1 tick 1 クリップ」はファイル数単位の分割であってサンプル数単位ではないため、
    // 巨大クリップ 1 本のクリップ化 (Il2Cpp 配列への逐次コピー) はそれ自体が framestall 源になる。
    private const int MaxPreloadSamples = 8_000_000;
    private static int preloadTicks;
    private static bool preloadStarted;
    // BgmName != null なら BGMManager 管轄のトラック (BgmCache へ届ける)。null なら SFX (audioCache へ)。
    private static readonly ConcurrentQueue<(string Key, float[] Buffer, int Read, int Channels, int SampleRate, string BgmName)> PreloadDecoded = [];

    // BGM チャンク分割クリップ化の進行状態 (1 曲分)。SetData を複数フレームへ割り、1 tick の停止を
    // HITCH 検出閾値 (50ms) 未満に抑える。完成するまで PrimeCache しないので途中データが鳴ることはない。
    private const int BgmCopyChunkFloats = 4_000_000; // ≒16MB/チャンク、Marshal.Copy+SetData で ~15ms 級
    private static (string BgmName, float[] Buffer, int Read, int Channels, AudioClip Clip, int Copied)? bgmClipInProgress;
    private static Il2CppStructArray<float> bgmChunkBuffer; // フルサイズチャンク再利用バッファ (端数チャンクのみ都度確保)

    private static void PumpBgmClipChunk()
    {
        var s = bgmClipInProgress.Value;

        try
        {
            HealthLog.NoteOp("BgmPump");
            int chunkFloats = BgmCopyChunkFloats - BgmCopyChunkFloats % s.Channels;
            int n = Math.Min(chunkFloats, s.Read - s.Copied);

            Il2CppStructArray<float> chunk = n == chunkFloats
                ? bgmChunkBuffer ??= new Il2CppStructArray<float>(chunkFloats)
                : new Il2CppStructArray<float>(n);

            System.Runtime.InteropServices.Marshal.Copy(s.Buffer, s.Copied, IntPtr.Add(chunk.Pointer, IntPtr.Size * 4), n);
            // SetData の第 2 引数はサンプルフレーム単位 (チャンネル込みではない) — chunkFloats は
            // channels の倍数に丸めてあるので Copied / Channels は常に割り切れる。
            s.Clip.SetData(chunk, s.Copied / s.Channels);
            s.Copied += n;

            if (s.Copied >= s.Read)
            {
                bgmClipInProgress = null;
                BgmInflight.Remove(s.BgmName);
                BGMManager.PrimeCache(s.BgmName, s.Clip);
                ReturnDecodeBuffer(s.Buffer);
                Logger.Info($"Preloaded BGM {s.BgmName} (chunked, {(s.Read + BgmCopyChunkFloats - 1) / BgmCopyChunkFloats} frames)", "CustomSounds");
            }
            else bgmClipInProgress = s;
        }
        catch (Exception ex)
        {
            // この曲だけ諦める。作りかけの clip は破棄し、失敗として通知する (同期ロードへの
            // フォールバックは存在しないので、通知しないと pending が無音のまま永久に待つ)。
            bgmClipInProgress = null;
            BgmInflight.Remove(s.BgmName);
            if (s.Clip) UnityEngine.Object.Destroy(s.Clip);
            BGMManager.OnPreloadFailed(s.BgmName);
            ReturnDecodeBuffer(s.Buffer);
            Logger.Exception(ex, "CustomSounds.PumpBgmClipChunk");
        }
    }

    // ── BGM の状態別遅延プリロード (リクエスト駆動) ──────────────────────
    // 起動時の全曲一括デコードは廃止。BGMManager の planner / Play が「今必要な曲」だけを
    // RequestBgmDecode で依頼し、短命の裏スレッドがデコードして PreloadDecoded 経由で届ける。
    // 同期ロード経路は存在しない (間に合わない時は無音 → 届いた瞬間フェードイン合流)。

    private static readonly ConcurrentQueue<string> BgmDecodeRequests = [];
    private static readonly ConcurrentQueue<string> BgmDecodeFailures = [];
    // TryDownsampleBgm の結果メッセージ (裏スレッドの BgmDecodeLoop は Logger 直呼び禁止 — LogText は
    // 全 Logger 呼び出しで共有する static StringBuilder なのでメインスレッドの Logger 呼び出しと競合する)。
    // メインスレッドの PreloadTick でまとめて Logger.Info/Warn へ出す。
    private static readonly ConcurrentQueue<(bool Applied, string Message)> BgmDownsampleLogs = [];
    // in-flight 集合 (依頼済み・未完了)。追加/削除はメインスレッドのみ — 二重デコード防止。
    private static readonly HashSet<string> BgmInflight = new(StringComparer.OrdinalIgnoreCase);
    private static Thread bgmWorker;

    // BgmBundle 経由で LoadAudioData() を呼んだ後、非同期ロード完了を PreloadTick で待つ分 (裏スレッドへは流さない)。
    // Reissued は Unloaded 停滞時の LoadAudioData() 再発行を 1 回だけに絞るためのフラグ。
    private static readonly List<(string Name, AudioClip Clip, Stopwatch Sw, bool Reissued)> BundlePending = [];
    private const double BundleLoadTimeoutSeconds = 15;
    private const double BundleLoadReissueSeconds = 2;

    // PreloadDecoded 内に滞留している「BGM 級」アイテム数 (Interlocked 専用)。裏デコードの
    // backpressure はこれを見る — 共有キューの IsEmpty を見ると起動時の SFX 一括プリロードの
    // 未消化分 (小物) にまで足止めされ、ロビー BGM の立ち上がりが不必要に遅れる。
    private static int bgmDecodedInQueue;

    // planner の GC 先撃ちタイミング判定用。「デコード中/クリップ化中/依頼残あり」の間は false。
    internal static bool IsBgmPipelineIdle
        => BgmInflight.Count == 0 && bgmClipInProgress == null && BgmDecodeRequests.IsEmpty && PreloadDecoded.IsEmpty && BundlePending.Count == 0;

    // メインスレッド専用。
    internal static void RequestBgmDecode(string name)
    {
        if (!OperatingSystem.IsWindows() || name == null) return;
        if (!BgmInflight.Add(name)) return;

        // ユーザーが resources/BGM/ に差し替えファイルを置いている場合はバンドルより優先する
        // (BGMManager.ResolveSource と同じ優先順位)。
        try
        {
            if (BgmBundle.IsEnabled && !BGMManager.HasUserOverride(name) && BgmBundle.TryGetClip(name, out AudioClip bundleClip))
            {
                bundleClip.LoadAudioData();
                BundlePending.Add((name, bundleClip, Stopwatch.StartNew(), false));
                return;
            }
        }
        catch (Exception e)
        {
            BgmInflight.Remove(name);
            Logger.Warn($"BGM bundle request failed for {name}, falling back to OGG decode: {e.Message}", "CustomSounds");
        }

        BgmDecodeRequests.Enqueue(name);
        EnsureBgmWorker();
    }

    private static void EnsureBgmWorker()
    {
        if (bgmWorker is { IsAlive: true }) return;

        bgmWorker = new Thread(BgmDecodeLoop) { IsBackground = true, Priority = System.Threading.ThreadPriority.BelowNormal, Name = "EndKnot.BgmDecode" };
        bgmWorker.Start();
    }

    // 裏スレッド本体。System.IO + マネージドデコーダのみ使用可 (Unity/Il2Cpp API 禁止、Logger も呼ばない)。
    // 1 本の PCM が数十 MB あるため、前の 1 本がメインスレッドポンプで消化されるまで次をデコードしない
    // (キュー滞留 = メモリ山を防ぐ)。キューが空になったら退出する (常駐しない)。
    private static void BgmDecodeLoop()
    {
        while (BgmDecodeRequests.TryDequeue(out string name))
        {
            try
            {
                // 前の BGM がメインスレッドポンプで消化されるまで待つ (BGM 級 PCM の滞留 = メモリ山を防ぐ)。
                // SFX の未消化分では待たない (BGM 専用カウンタを見る)。
                while (System.Threading.Interlocked.CompareExchange(ref bgmDecodedInQueue, 0, 0) > 0) Thread.Sleep(100);

                string key = BGMManager.ResolveSource(name);
                if (key == null) { BgmDecodeFailures.Enqueue(name); continue; }

                switch (Path.GetExtension(key).ToLowerInvariant())
                {
                    case ".ogg":
                    {
                        (float[] buffer, int read, int channels, int sampleRate) = DecodeOgg(key);
                        (bool dsApplied, string dsMessage) = TryDownsampleBgm(key, buffer, ref read, channels, ref sampleRate);
                        if (dsMessage != null) BgmDownsampleLogs.Enqueue((dsApplied, dsMessage));
                        System.Threading.Interlocked.Increment(ref bgmDecodedInQueue);
                        PreloadDecoded.Enqueue((key, buffer, read, channels, sampleRate, name));
                        break;
                    }
                    case ".mp3":
                    {
                        (float[] buffer, int read, int channels, int sampleRate) = DecodeMp3(key);
                        System.Threading.Interlocked.Increment(ref bgmDecodedInQueue);
                        PreloadDecoded.Enqueue((key, buffer, read, channels, sampleRate, name));
                        break;
                    }
                    case ".wav":
                    {
                        (float[] buffer, int read, int channels, int sampleRate) = DecodeWav(key);
                        System.Threading.Interlocked.Increment(ref bgmDecodedInQueue);
                        PreloadDecoded.Enqueue((key, buffer, read, channels, sampleRate, name));
                        break;
                    }
                    default:
                        BgmDecodeFailures.Enqueue(name);
                        break;
                }
            }
            catch
            {
                BgmDecodeFailures.Enqueue(name);
            }
        }
    }

    // FixedUpdateCaller から毎 fixed update で呼ばれる (メインスレッド専用ポンプ)。
    public static void PreloadTick()
    {
        if (!OperatingSystem.IsWindows()) return;

        // SFX の起動時一括プリロード (従来どおり、起動 ~10 秒後に 1 回だけ)。OFF の間は温めない
        // (再生されない音のデコード分だけ純増になるため)。ON に切り替えられたら始動する。
        if (!preloadStarted)
        {
            bool sfxOn = Main.EnableCustomSoundEffect?.Value ?? false;
            if (sfxOn && ++preloadTicks >= PreloadStartDelayTicks)
            {
                preloadStarted = true;
                var worker = new Thread(PreloadSfx) { IsBackground = true, Priority = System.Threading.ThreadPriority.BelowNormal, Name = "EndKnot.SoundPreload" };
                worker.Start();
            }
        }

        // BGM デコードの失敗通知を先に配る (pending の解除が遅れると無音が延びるため)
        while (BgmDecodeFailures.TryDequeue(out string failed))
        {
            BgmInflight.Remove(failed);
            BGMManager.OnPreloadFailed(failed);
        }

        // バンドル経由の BGM は LoadAudioData() が非同期 (数フレーム) なので loadState をここで拾う。
        for (int i = BundlePending.Count - 1; i >= 0; i--)
        {
            (string name, AudioClip clip, Stopwatch sw, bool reissued) = BundlePending[i];
            AudioDataLoadState state = clip.loadState;

            if (state == AudioDataLoadState.Loaded)
            {
                BundlePending.RemoveAt(i);
                BgmInflight.Remove(name);
                BGMManager.PrimeCache(name, clip);
                Logger.Info($"BGM bundle clip ready: {name} ({sw.ElapsedMilliseconds}ms)", "CustomSounds");
                continue;
            }

            // Failed に加え、LoadAudioData() が停滞したまま進まなくなるケースもタイムアウトで
            // 失敗扱いにする (放置すると BgmInflight にこの名前が居座り続け、IsBgmPipelineIdle も
            // 永久に false のままになって GC 先撃ちタイミングが失われる)。
            if (state == AudioDataLoadState.Failed || sw.Elapsed.TotalSeconds > BundleLoadTimeoutSeconds)
            {
                BundlePending.RemoveAt(i);
                BgmInflight.Remove(name);
                BGMManager.OnPreloadFailed(name);
                Logger.Warn($"BGM bundle clip load failed: {name} (state={state})", "CustomSounds");
                continue;
            }

            // Unloaded のまま既定秒数動かない場合は LoadAudioData() の呼び出しが取りこぼされた
            // 疑いがあるので 1 回だけ再発行する (以後は BundleLoadTimeoutSeconds の失敗判定に委ねる)。
            if (!reissued && state == AudioDataLoadState.Unloaded && sw.Elapsed.TotalSeconds >= BundleLoadReissueSeconds)
            {
                clip.LoadAudioData();
                BundlePending[i] = (name, clip, sw, true);
            }
        }

        // TryDownsampleBgm の結果ログをメインスレッドでまとめて出す (裏スレッドは Logger 直呼び禁止)。
        while (BgmDownsampleLogs.TryDequeue(out (bool Applied, string Message) log))
        {
            if (log.Applied) Logger.Info(log.Message, "CustomSounds");
            else Logger.Warn(log.Message, "CustomSounds");
        }

        // ワーカー退出レース対策: 「TryDequeue 空振り → 退出」の隙間にメインスレッドが enqueue した
        // 依頼は誰にも拾われない。キュー残があるのにワーカーが死んでいたらここで立て直す。
        if (!BgmDecodeRequests.IsEmpty) EnsureBgmWorker();

        // BGM のクリップ化進行中なら 1 tick 1 チャンクだけ書き進める (最優先・他の処理はしない)
        if (bgmClipInProgress != null)
        {
            PumpBgmClipChunk();
            return;
        }

        // 1 tick 1 件だけクリップ化する (まとめて処理すると自分がヒッチ源になる)
        if (PreloadDecoded.TryDequeue(out (string Key, float[] Buffer, int Read, int Channels, int SampleRate, string BgmName) d))
        {
            if (d.BgmName != null)
            {
                System.Threading.Interlocked.Decrement(ref bgmDecodedInQueue);

                // デコード中に状態が先へ進んで不要になったトラックはクリップ化せず捨てる
                // (数十 ms の SetData 連鎖と数十 MB の native 確保をまるごと節約)。
                if (!BGMManager.IsFileWantedOrPending(d.BgmName))
                {
                    BgmInflight.Remove(d.BgmName);
                    ReturnDecodeBuffer(d.Buffer);
                    return;
                }

                // BGM 級 (数十 MB PCM) は一括 SetData でも 50-70ms かかりサブ秒ヒッチ検出閾値を踏む
                // (実測: ロビー入り直後の preload 7 連発が「数秒かくかく」体感の一因)。
                // クリップだけ先に作り、データ書き込みはチャンク分割で複数フレームに薄める。
                AudioClip clip = AudioClip.Create(ClipNameOf(d.Key), d.Read / d.Channels, d.Channels, d.SampleRate, false);

                // ⚠️ 書き込みは複数フレームにまたがるので、その途中でシーン遷移が挟まると
                // UnloadUnusedAssets が作りかけのクリップを回収して SetData が落ちる。
                // 下の完成済みクリップと同じ扱いにして回収対象から外す。
                if (clip) clip.hideFlags |= HideFlags.DontUnloadUnusedAsset;

                bgmClipInProgress = (d.BgmName, d.Buffer, d.Read, d.Channels, clip, 0);
            }
            else if (!audioCache.TryGetValue(d.Key, out AudioClip cached) || !cached)
            {
                AudioClip clip = CreateClip(d.Key, d.Buffer, d.Read, d.Channels, d.SampleRate, out long copyMs);
                if (clip) clip.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                audioCache[d.Key] = clip;
                Logger.Info($"Preloaded {ClipNameOf(d.Key)} (copyMs={copyMs})", "CustomSounds");
            }

            return;
        }

        // 完全に暇になったら 16MB のチャンク再利用バッファも手放す (次のロードで再確保される)
        if (bgmChunkBuffer != null && IsBgmPipelineIdle) bgmChunkBuffer = null;
    }

    private static void PreloadSfx()
    {
        try
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            const string prefix = "EndKnot.Resources.Sounds.";

            foreach (string res in Assembly.GetExecutingAssembly().GetManifestResourceNames())
            {
                if (!res.StartsWith(prefix)) continue;

                string tail = res[prefix.Length..];
                if (Array.IndexOf(SupportedExtensions, Path.GetExtension(tail).ToLowerInvariant()) < 0) continue;

                string name = Path.GetFileNameWithoutExtension(tail);

                // 埋込名はサブフォルダも '.' 区切りで平坦化される (例 "BGM.climax.ogg")。名前に '.' が
                // 残る = サブフォルダ配下 (BGM/Backrooms — BGMManager 等の管轄) なので対象外。
                // 通すと ResolveSoundKey("BGM.climax") が埋込名を再構成できてしまい、BGM 級の
                // 長尺トラックがプリロードに混入する (ディスク側のトップレベル限定と意図を揃える)。
                if (name.Contains('.')) continue;

                names.Add(name);
            }

            if (Directory.Exists(SoundsPath))
            {
                foreach (string file in Directory.GetFiles(SoundsPath))
                {
                    if (Array.IndexOf(SupportedExtensions, Path.GetExtension(file).ToLowerInvariant()) < 0) continue;

                    names.Add(Path.GetFileNameWithoutExtension(file));
                }
            }

            foreach (string name in names)
            {
                try
                {
                    string key = ResolveSoundKey(name); // ディスクの差し替え優先、無ければ埋込リソース
                    if (key == null) continue;

                    switch (Path.GetExtension(key).ToLowerInvariant())
                    {
                        case ".ogg":
                        {
                            (float[] buffer, int read, int channels, int sampleRate) = DecodeOgg(key);
                            if (read <= MaxPreloadSamples) PreloadDecoded.Enqueue((key, buffer, read, channels, sampleRate, null));
                            else ReturnDecodeBuffer(buffer);
                            break;
                        }
                        case ".mp3":
                        {
                            (float[] buffer, int read, int channels, int sampleRate) = DecodeMp3(key);
                            if (read <= MaxPreloadSamples) PreloadDecoded.Enqueue((key, buffer, read, channels, sampleRate, null));
                            else ReturnDecodeBuffer(buffer);
                            break;
                        }
                        default: // .wav
                        {
                            (float[] buffer, int read, int channels, int sampleRate) = DecodeWav(key);
                            if (read <= MaxPreloadSamples) PreloadDecoded.Enqueue((key, buffer, read, channels, sampleRate, null));
                            else ReturnDecodeBuffer(buffer);
                            break;
                        }
                    }
                }
                catch
                {
                    // この音だけ諦めて次へ (初回再生時の同期デコードに戻るだけ)
                }
            }
        }
        catch
        {
            // 列挙ごと失敗しても従来動作に戻るだけ
        }
    }

    internal class WAV
    {
        // Convert two bytes to one float in the range -1 to 1
        private static float BytesToFloat(byte firstByte, byte secondByte)
        {
            // Convert two bytes to one short (little endian)
            short s = (short)((secondByte << 8) | firstByte);
            // Convert to range from -1 to (just below) 1
            return s / 32768.0F;
        }

        private static int BytesToInt(byte[] bytes, int offset = 0)
        {
            int value = 0;

            for (int i = 0; i < 4; i++)
                value |= bytes[offset + i] << (i * 8);
            return value;
        }

        // Properties
        public Il2CppStructArray<float> LeftChannel { get; }
        public Il2CppStructArray<float> RightChannel { get; }
        public int ChannelCount { get; }
        public int SampleCount { get; }
        public int Frequency { get; }

        public WAV(Il2CppStructArray<byte> wavIl2cpp)
        {
            // Il2Cpp インデクサの per-element 読みは罠 (大要素数だと interop 呼び出しが支配的)。
            // 先頭で一括 managed 化してから解析・変換し、最後にチャンネルごと 1 回の Marshal.Copy で書き戻す。
            byte[] wav = new byte[wavIl2cpp.Length];
            System.Runtime.InteropServices.Marshal.Copy(IntPtr.Add(wavIl2cpp.Pointer, IntPtr.Size * 4), wav, 0, wav.Length);

            // Determine if mono or stereo
            ChannelCount = wav[22]; // Forget byte 23 as 99.999% of WAVs are 1 or 2 channels
            // Get the frequency
            Frequency = BytesToInt(wav, 24);
            // Get past all the other sub chunks to get to the data subchunk:
            int pos = 12; // First Subchunk ID from 12 to 16

            // Keep iterating until we find the data chunk (i.e. 64 61 74 61 ...... (i.e. 100 97 116 97 in decimal))
            while (!(wav[pos] == 100 && wav[pos + 1] == 97 && wav[pos + 2] == 116 && wav[pos + 3] == 97))
            {
                pos += 4;
                int chunkSize = wav[pos] + wav[pos + 1] * 256 + wav[pos + 2] * 65536 + wav[pos + 3] * 16777216;
                pos += 4 + chunkSize;
            }

            pos += 4; // skip "data"
            int dataSize = BytesToInt(wav, pos);
            pos += 4; // now at PCM data

            // Pos is now positioned to start of actual sound data.
            SampleCount = dataSize / 2; // 2 bytes per sample (16 bit sound mono)
            if (ChannelCount == 2) SampleCount /= 2; // 4 bytes per sample (16 bit stereo)

            // 変換は managed 配列上で行い、Il2Cpp 側へは最後に一括転送する
            float[] left = new float[SampleCount];
            float[] right = ChannelCount == 2 ? new float[SampleCount] : null;

            int end = pos + dataSize;
            // Write to double array/s:
            int i = 0;

            while (pos + (ChannelCount * 2) <= end && i < SampleCount)
            {
                left[i] = BytesToFloat(wav[pos], wav[pos + 1]);
                pos += 2;

                if (ChannelCount == 2)
                {
                    right[i] = BytesToFloat(wav[pos], wav[pos + 1]);
                    pos += 2;
                }
                i++;
            }

            LeftChannel = new Il2CppStructArray<float>(SampleCount);
            System.Runtime.InteropServices.Marshal.Copy(left, 0, IntPtr.Add(LeftChannel.Pointer, IntPtr.Size * 4), SampleCount);

            if (ChannelCount == 2)
            {
                RightChannel = new Il2CppStructArray<float>(SampleCount);
                System.Runtime.InteropServices.Marshal.Copy(right, 0, IntPtr.Add(RightChannel.Pointer, IntPtr.Size * 4), SampleCount);
            }
            else RightChannel = null;
        }

        // Returns left and right double arrays. 'right' will be null if sound is mono.
        public Il2CppStructArray<float> GetStereoData()
        {
            if (RightChannel == null) return LeftChannel;

            var stereoData = new Il2CppStructArray<float>(SampleCount * 2);

            for (int i = 0; i < SampleCount; i++)
            {
                stereoData[i * 2] = LeftChannel[i]; // Left channel data
                stereoData[i * 2 + 1] = RightChannel[i]; // Right channel data
            }

            return stereoData;
        }
    }
}
