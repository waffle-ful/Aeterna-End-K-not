using Hazel;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace EndKnot.Modules;

public static class CustomSoundsManager
{
    private static readonly string SoundsPath = $"{Environment.CurrentDirectory.Replace(@"\", "/")}/BepInEx/resources/";
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

            string foundPath = ResolveSoundPath(sound);
            if (foundPath == null)
            {
                Logger.Warn($"Could not find sound: {sound}", "CustomSounds");
                return;
            }

            StartPlay(foundPath, volume, pitch);
            Logger.Msg($"Playing sound: {sound} ({Path.GetExtension(foundPath)})", "CustomSounds");
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

            string foundPath = ResolveSoundPath(sound);
            if (foundPath == null)
            {
                Logger.Warn($"Could not find sound: {sound}", "CustomSounds");
                return null;
            }

            AudioClip clip = LoadClip(foundPath);
            if (clip == null) return null;

            Logger.Msg($"Playing sound (controllable): {sound} ({Path.GetExtension(foundPath)})", "CustomSounds");
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

            string foundPath = ResolveSoundPath(sound);
            if (foundPath == null) return 0f;

            AudioClip clip = LoadClip(foundPath);
            return clip ? clip.length : 0f;
        }
        catch (Exception e)
        {
            Utils.ThrowException(e);
            return 0f;
        }
    }

    // BepInEx/resources 内の実ファイル → 埋込リソース展開 の順で音源パスを解決する (無ければ null)。
    private static string ResolveSoundPath(string sound)
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
            Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("EndKnot.Resources.Sounds." + sound + ext);
            if (stream == null) continue;

            string foundPath = SoundsPath + sound + ext;
            using FileStream fileStream = File.Create(foundPath);
            stream.CopyTo(fileStream);
            return foundPath;
        }

        return null;
    }

    private static readonly Dictionary<string, AudioClip> audioCache = [];

    private static void StartPlay(string path, float volume = 1f, float pitch = 1f)
    {
        AudioClip clip = LoadClip(path);
        if (clip)
            SoundManager.Instance.PlaySoundImmediate(clip, false, volume);
    }

    // path 単位でデコード結果をキャッシュしつつ AudioClip を返す (初回のみデコード)。
    // シーン遷移 (ゲーム終了→ロビー等) の暗黙 UnloadUnusedAssets は、managed dict からしか参照されていない
    // AudioClip をネイティブ側で破棄する。破棄済みクリップは Play 側の `if (clip)` が黙って false になり
    // 「ログは出るのに無音」になる (2026-07-14 実機確認) ため、fake-null を検出して再デコードし、
    // 以後は DontUnloadUnusedAsset で保護する。
    private static AudioClip LoadClip(string path)
    {
        if (!audioCache.TryGetValue(path, out var clip) || !clip)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            clip = ext switch
            {
                ".ogg" => LoadOGG(path),
                ".mp3" => LoadMP3(path),
                _ => LoadWAV(path)
            };
            if (clip) clip.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            audioCache[path] = clip;
        }

        return clip;
    }

    internal static AudioClip LoadWAV(string path)
    {
        var fileData = Il2CppSystem.IO.File.ReadAllBytes(path);
        WAV wav = new(fileData);

        Logger.Info($"[WAV: LeftChannel={wav.LeftChannel}, RightChannel={wav.RightChannel}, ChannelCount={wav.ChannelCount}, SampleCount={wav.SampleCount}, Frequency={wav.Frequency}]", "CustomSounds");

        AudioClip clip = AudioClip.Create(Path.GetFileNameWithoutExtension(path), wav.SampleCount, 1, wav.Frequency, false, false);
        clip.SetData(wav.LeftChannel, 0);

        return clip;
    }

    internal static AudioClip LoadOGG(string path)
    {
        using var reader = new NVorbis.VorbisReader(path);
        int channels = reader.Channels;
        int sampleRate = reader.SampleRate;
        long totalSamples = reader.TotalSamples;

        if (totalSamples <= 0) throw new IOException($"OGG TotalSamples invalid: {totalSamples}");

        int interleavedLen = (int)(totalSamples * channels);
        float[] buffer = new float[interleavedLen];
        int read = reader.ReadSamples(buffer, 0, interleavedLen);

        Logger.Info($"[OGG: Channels={channels}, SampleRate={sampleRate}, SamplesRead={read}]", "CustomSounds");

        Il2CppStructArray<float> il2cppBuffer = new(read);
        for (int i = 0; i < read; i++) il2cppBuffer[i] = buffer[i];

        AudioClip clip = AudioClip.Create(Path.GetFileNameWithoutExtension(path), read / channels, channels, sampleRate, false);
        clip.SetData(il2cppBuffer, 0);
        return clip;
    }

    internal static AudioClip LoadMP3(string path)
    {
        using var reader = new NLayer.MpegFile(path);
        int channels = reader.Channels;
        int sampleRate = reader.SampleRate;
        long totalInterleaved = reader.Length;

        if (totalInterleaved <= 0) throw new IOException($"MP3 Length invalid: {totalInterleaved}");

        int interleavedLen = (int)totalInterleaved;
        float[] buffer = new float[interleavedLen];
        int read = reader.ReadSamples(buffer, 0, interleavedLen);

        Logger.Info($"[MP3: Channels={channels}, SampleRate={sampleRate}, SamplesRead={read}]", "CustomSounds");

        Il2CppStructArray<float> il2cppBuffer = new(read);
        for (int i = 0; i < read; i++) il2cppBuffer[i] = buffer[i];

        AudioClip clip = AudioClip.Create(Path.GetFileNameWithoutExtension(path), read / channels, channels, sampleRate, false);
        clip.SetData(il2cppBuffer, 0);
        return clip;
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

        private static int BytesToInt(Il2CppStructArray<byte> bytes, int offset = 0)
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

        public WAV(Il2CppStructArray<byte> wav)
        {
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

            // Allocate memory (right will be null if only mono sound)
            LeftChannel = new Il2CppStructArray<float>(SampleCount);
            if (ChannelCount == 2) RightChannel = new Il2CppStructArray<float>(SampleCount);
            else RightChannel = null;

            int end = pos + dataSize;
            // Write to double array/s:
            int i = 0;

            while (pos + (ChannelCount * 2) <= end && i < SampleCount)
            {
                LeftChannel[i] = BytesToFloat(wav[pos], wav[pos + 1]);
                pos += 2;

                if (ChannelCount == 2)
                {
                    RightChannel[i] = BytesToFloat(wav[pos], wav[pos + 1]);
                    pos += 2;
                }
                i++;
            }
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
