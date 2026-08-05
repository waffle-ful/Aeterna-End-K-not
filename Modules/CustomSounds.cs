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

            // プリロードのワーカースレッドとメインスレッドが同じ音を同時に展開しうるため、
            // 書きかけの実ファイルを他方が読む窓を作らない: 一時名へ書き切ってから atomic move。
            // move 負けした側は勝った側の完成品をそのまま使う。
            string foundPath = SoundsPath + sound + ext;
            string tmpPath = foundPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

            using (FileStream fileStream = File.Create(tmpPath))
                stream.CopyTo(fileStream);

            try { File.Move(tmpPath, foundPath, true); }
            catch (IOException) when (File.Exists(foundPath))
            {
                try { File.Delete(tmpPath); }
                catch { /* 孤児 tmp は次回起動の掃除に任せる */ }
            }

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
        var sw = Stopwatch.StartNew();
        var fileData = Il2CppSystem.IO.File.ReadAllBytes(path);
        WAV wav = new(fileData);

        AudioClip clip = AudioClip.Create(Path.GetFileNameWithoutExtension(path), wav.SampleCount, 1, wav.Frequency, false, false);
        clip.SetData(wav.LeftChannel, 0);

        Logger.Info($"[WAV: LeftChannel={wav.LeftChannel}, RightChannel={wav.RightChannel}, ChannelCount={wav.ChannelCount}, SampleCount={wav.SampleCount}, Frequency={wav.Frequency}, loadMs={sw.ElapsedMilliseconds}]", "CustomSounds");

        return clip;
    }

    internal static AudioClip LoadOGG(string path)
    {
        var sw = Stopwatch.StartNew();
        (float[] buffer, int read, int channels, int sampleRate) = DecodeOgg(path);
        long decodeMs = sw.ElapsedMilliseconds;

        AudioClip clip = CreateClip(path, buffer, read, channels, sampleRate, out long copyMs);
        Logger.Info($"[OGG: Channels={channels}, SampleRate={sampleRate}, SamplesRead={read}, decodeMs={decodeMs}, copyMs={copyMs}]", "CustomSounds");
        return clip;
    }

    internal static AudioClip LoadMP3(string path)
    {
        var sw = Stopwatch.StartNew();
        (float[] buffer, int read, int channels, int sampleRate) = DecodeMp3(path);
        long decodeMs = sw.ElapsedMilliseconds;

        AudioClip clip = CreateClip(path, buffer, read, channels, sampleRate, out long copyMs);
        Logger.Info($"[MP3: Channels={channels}, SampleRate={sampleRate}, SamplesRead={read}, decodeMs={decodeMs}, copyMs={copyMs}]", "CustomSounds");
        return clip;
    }

    // OGG/MP3 のデコード本体。System.IO + マネージドデコーダのみ (Unity/Il2Cpp API 不使用) なので
    // バックグラウンドスレッドから呼んでよい。AudioClip 化 (CreateClip) はメインスレッド専用。
    private static (float[] Buffer, int Read, int Channels, int SampleRate) DecodeOgg(string path)
    {
        using var reader = new NVorbis.VorbisReader(path);
        int channels = reader.Channels;
        int sampleRate = reader.SampleRate;
        long totalSamples = reader.TotalSamples;

        if (totalSamples <= 0) throw new IOException($"OGG TotalSamples invalid: {totalSamples}");

        int interleavedLen = (int)(totalSamples * channels);
        float[] buffer = new float[interleavedLen];
        int read = reader.ReadSamples(buffer, 0, interleavedLen);
        return (buffer, read, channels, sampleRate);
    }

    private static (float[] Buffer, int Read, int Channels, int SampleRate) DecodeMp3(string path)
    {
        using var reader = new NLayer.MpegFile(path);
        int channels = reader.Channels;
        int sampleRate = reader.SampleRate;
        long totalInterleaved = reader.Length;

        if (totalInterleaved <= 0) throw new IOException($"MP3 Length invalid: {totalInterleaved}");

        int interleavedLen = (int)totalInterleaved;
        float[] buffer = new float[interleavedLen];
        int read = reader.ReadSamples(buffer, 0, interleavedLen);
        return (buffer, read, channels, sampleRate);
    }

    private static AudioClip CreateClip(string path, float[] buffer, int read, int channels, int sampleRate, out long copyMs)
    {
        var sw = Stopwatch.StartNew();
        Il2CppStructArray<float> il2cppBuffer = new(read);
        // Managed float[] -> Il2CppStructArray<float> via Marshal.Copy (per-element indexer is a trap — MEMORY.md)。
        // BGM 級 (23M サンプル) でインデクサループは実測 ~1000ms、一括コピーなら数十 ms。
        System.Runtime.InteropServices.Marshal.Copy(buffer, 0, IntPtr.Add(il2cppBuffer.Pointer, IntPtr.Size * 4), read);

        AudioClip clip = AudioClip.Create(Path.GetFileNameWithoutExtension(path), read / channels, channels, sampleRate, false);
        clip.SetData(il2cppBuffer, 0);
        copyMs = sw.ElapsedMilliseconds;
        return clip;
    }

    // ── 起動時サウンドプリロード ──────────────────────────────────────────
    // 初回再生時の同期フルデコードがメインスレッドを止める疑いへの根治
    // (BUG-20260729-17: ロビー放置中 27 秒 framestall の解除フレームに Earthquake MP3 の
    // 初回デコード完了ログが一致)。圧縮系 (.ogg/.mp3) はバックグラウンドスレッドで PCM へ
    // 先行デコードし、メインスレッドは 1 fixed update に 1 クリップだけ AudioClip 化して
    // audioCache を温める。.wav は Il2Cpp I/O 依存でスレッドに逃がせないが小さく速いので
    // メインスレッド側で 1 tick 1 件ずつ処理する。BGM/Backrooms サブフォルダは BGMManager
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
    private static readonly ConcurrentQueue<(string Path, float[] Buffer, int Read, int Channels, int SampleRate, string BgmName)> PreloadDecoded = [];
    private static readonly ConcurrentQueue<string> PreloadWavPaths = [];

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
                BGMManager.PrimeCache(s.BgmName, s.Clip);
                Logger.Info($"Preloaded BGM {s.BgmName} (chunked, {(s.Read + BgmCopyChunkFloats - 1) / BgmCopyChunkFloats} frames)", "CustomSounds");
            }
            else bgmClipInProgress = s;
        }
        catch (Exception ex)
        {
            // この曲だけ諦める (初回再生時の同期ロードに戻るだけ)。作りかけの clip は破棄する。
            bgmClipInProgress = null;
            if (s.Clip) UnityEngine.Object.Destroy(s.Clip);
            Logger.Exception(ex, "CustomSounds.PumpBgmClipChunk");
        }
    }

    // FixedUpdateCaller から毎 fixed update で呼ばれる (メインスレッド専用ポンプ)。
    public static void PreloadTick()
    {
        if (!OperatingSystem.IsWindows()) return;

        if (!preloadStarted)
        {
            // 対象系統 (SFX / BGM) が両方切られている間は温めない (再生されない音のデコード分だけ純増になるため)。
            // ON に切り替えられたら次の tick から通常どおり始動する。
            bool sfxOn = Main.EnableCustomSoundEffect?.Value ?? false;
            bool bgmOn = Main.EnableBGM?.Value ?? false;
            if (!sfxOn && !bgmOn) return;
            if (++preloadTicks < PreloadStartDelayTicks) return;

            preloadStarted = true;
            var worker = new Thread(() => PreloadWorker(sfxOn, bgmOn)) { IsBackground = true, Priority = System.Threading.ThreadPriority.BelowNormal, Name = "EndKnot.SoundPreload" };
            worker.Start();
            return;
        }

        // BGM のクリップ化進行中なら 1 tick 1 チャンクだけ書き進める (最優先・他の処理はしない)
        if (bgmClipInProgress != null)
        {
            PumpBgmClipChunk();
            return;
        }

        // 1 tick 1 件だけクリップ化する (まとめて処理すると自分がヒッチ源になる)
        if (PreloadDecoded.TryDequeue(out (string Path, float[] Buffer, int Read, int Channels, int SampleRate, string BgmName) d))
        {
            if (d.BgmName != null)
            {
                // BGM 級 (数十 MB PCM) は一括 SetData でも 50-70ms かかりサブ秒ヒッチ検出閾値を踏む
                // (実測: ロビー入り直後の preload 7 連発が「数秒かくかく」体感の一因)。
                // クリップだけ先に作り、データ書き込みはチャンク分割で複数フレームに薄める。
                AudioClip clip = AudioClip.Create(Path.GetFileNameWithoutExtension(d.Path), d.Read / d.Channels, d.Channels, d.SampleRate, false);
                bgmClipInProgress = (d.BgmName, d.Buffer, d.Read, d.Channels, clip, 0);
            }
            else if (!audioCache.TryGetValue(d.Path, out AudioClip cached) || !cached)
            {
                AudioClip clip = CreateClip(d.Path, d.Buffer, d.Read, d.Channels, d.SampleRate, out long copyMs);
                if (clip) clip.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                audioCache[d.Path] = clip;
                Logger.Info($"Preloaded {Path.GetFileName(d.Path)} (copyMs={copyMs})", "CustomSounds");
            }

            return;
        }

        if (PreloadWavPaths.TryDequeue(out string wavPath))
        {
            if (!audioCache.TryGetValue(wavPath, out AudioClip cached) || !cached)
            {
                LoadClip(wavPath); // キャッシュ格納と DontUnloadUnusedAsset は LoadClip 側で行われる
                Logger.Info($"Preloaded {Path.GetFileName(wavPath)}", "CustomSounds");
            }
        }
    }

    // バックグラウンドスレッド本体。System.IO + マネージドデコーダのみ使用可 (Unity/Il2Cpp API 禁止、
    // Logger も呼ばない)。audioCache には触らず、結果はキュー経由でメインスレッドに渡す。
    private static void PreloadWorker(bool sfxOn, bool bgmOn)
    {
        if (sfxOn) PreloadSfx();
        if (bgmOn) PreloadBgm();
    }

    // BGM トラック (BGMManager 管轄) の裏デコード。SFX と違い 1 本の PCM が数十 MB あるため、
    // 前の 1 本がメインスレッドポンプでクリップ化されるまで次をデコードしない (キュー滞留 = メモリ山を防ぐ)。
    private static void PreloadBgm()
    {
        try
        {
            foreach (string name in BGMManager.GetPreloadFiles())
            {
                try
                {
                    while (!PreloadDecoded.IsEmpty) Thread.Sleep(100);

                    string path = BGMManager.ResolveOrExtract(name);
                    if (path == null) continue;

                    switch (Path.GetExtension(path).ToLowerInvariant())
                    {
                        case ".ogg":
                        {
                            (float[] buffer, int read, int channels, int sampleRate) = DecodeOgg(path);
                            PreloadDecoded.Enqueue((path, buffer, read, channels, sampleRate, name));
                            break;
                        }
                        case ".mp3":
                        {
                            (float[] buffer, int read, int channels, int sampleRate) = DecodeMp3(path);
                            PreloadDecoded.Enqueue((path, buffer, read, channels, sampleRate, name));
                            break;
                        }
                        // .wav BGM は LoadWAV が Il2Cpp I/O 依存でスレッドに逃がせないため対象外
                        // (従来どおり初回再生時の同期ロード — Marshal.Copy 化で高速化済み)。
                    }
                }
                catch
                {
                    // このトラックだけ諦めて次へ (初回再生時の同期デコードに戻るだけ)
                }
            }
        }
        catch
        {
            // 列挙ごと失敗しても従来動作に戻るだけ
        }
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
                // 通すと ResolveSoundPath("BGM.climax") が埋込名を再構成できてしまい、BGM 級の
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
                    string path = ResolveSoundPath(name); // 埋込リソースのディスク展開込み
                    if (path == null) continue;

                    switch (Path.GetExtension(path).ToLowerInvariant())
                    {
                        case ".ogg":
                        {
                            (float[] buffer, int read, int channels, int sampleRate) = DecodeOgg(path);
                            if (read <= MaxPreloadSamples) PreloadDecoded.Enqueue((path, buffer, read, channels, sampleRate, null));
                            break;
                        }
                        case ".mp3":
                        {
                            (float[] buffer, int read, int channels, int sampleRate) = DecodeMp3(path);
                            if (read <= MaxPreloadSamples) PreloadDecoded.Enqueue((path, buffer, read, channels, sampleRate, null));
                            break;
                        }
                        default:
                            PreloadWavPaths.Enqueue(path);
                            break;
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
            // Il2Cpp インデクサの per-element 読みは罠 (MEMORY.md — 大要素数だと interop 呼び出しが支配的)。
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
