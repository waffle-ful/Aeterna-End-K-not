using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace EndKnot.Modules.VoiceVox;

// Host-local per-crew text-to-speech. Reads genuine broadcast player chat aloud on the streamer's
// machine using a per-player VoiceVox voice. NEVER sends any RPC/packet (100% host-local: HTTP to a
// local engine + Unity audio). The leak-safe gate lives in Patches/VoiceVoxChatPatch.cs; this class
// only synthesizes + plays what it is handed.
//
// Threading contract (hard rule): profile resolution + audio decode/create/play happen on the Unity
// MAIN THREAD (Speak is called from a Harmony prefix; Tick from FixedUpdateCaller). Only managed
// HTTP + byte[] handling runs on the Task.Run worker. Never touch IL2CPP/Unity objects off-thread.
internal static class VoiceVoxManager
{
    private const int MaxTextQueue = 20;   // burst guard: drop oldest un-synthesized lines
    private const int MaxAudioQueue = 10;  // burst guard: drop oldest ready clips
    private const float GapSeconds = 0.15f; // small silence between consecutive utterances

    // Pending (already-resolved) lines waiting to be synthesized by the worker.
    private static readonly Queue<(string text, VoiceProfile profile)> TextQueue = new();
    private static readonly object TextLock = new();

    // Synthesized WAV bytes waiting to be played on the main thread.
    private static readonly Queue<byte[]> AudioQueue = new();
    private static readonly object AudioLock = new();

    private static bool _workerRunning;

    // Per-game PlayerId -> voice. Only touched on the main thread (Speak / Reset). Cleared each game.
    private static readonly Dictionary<byte, VoiceProfile> Assigned = new();
    private static int _poolCursor;

    // Style ids discovered from GET /speakers. Written by the worker, read on the main thread -> lock.
    private static List<int> _availableStyleIds = new();
    private static readonly object PoolLock = new();

    private static AudioSource _currentSource;
    private static AudioClip _currentClip;
    private static float _nextPlayTime;

    // Called once at startup (Main) and whenever the feature is toggled on or the URL changes.
    public static void Init()
    {
        if (Main.EnableVoiceVox?.Value == true) RefreshSpeakers();
    }

    // Background fetch of the installed voice list -> caches the auto-assign pool + writes the
    // discovery dump so the streamer can look up style ids for the override config.
    public static void RefreshSpeakers()
    {
        if (!OperatingSystem.IsWindows()) return;
        string url = Main.VoiceVoxEngineUrl?.Value ?? "http://127.0.0.1:50021";

        Task.Run(async () =>
        {
            try
            {
                List<(int id, string label)> list = await VoiceVoxFetcher.GetSpeakersAsync(url);
                if (list.Count == 0) return;

                lock (PoolLock) { _availableStyleIds = list.Select(x => x.id).ToList(); }
                VoiceVoxVoiceConfig.WriteSpeakerDump(list);
                Logger.Info($"Loaded {list.Count} VoiceVox styles from {url}", "VoiceVoxManager");
            }
            catch (Exception ex) { Logger.Warn($"RefreshSpeakers error: {ex.Message}", "VoiceVoxManager"); }
        });
    }

    // MAIN THREAD. Called by the leak-safe prefix once a message is confirmed speakable.
    public static void Speak(PlayerControl player, string rawText)
    {
        try
        {
            if (!Main.EnableVoiceVox.Value || !OperatingSystem.IsWindows() || player == null) return;

            string text = Sanitize(rawText);
            if (text.Length == 0) return;

            VoiceProfile profile = ResolveProfile(player); // IL2CPP access (FriendCode/name) — main thread only
            if (profile == null) return;

            lock (TextLock)
            {
                TextQueue.Enqueue((text, profile));
                while (TextQueue.Count > MaxTextQueue) TextQueue.Dequeue();
            }

            EnsureWorker();
        }
        catch (Exception e) { Utils.ThrowException(e); }
    }

    // MAIN THREAD. Drains one ready clip when nothing is currently speaking -> sequential no-overlap FIFO.
    public static void Tick()
    {
        try
        {
            if (!Main.EnableVoiceVox.Value || !OperatingSystem.IsWindows()) return;
            if (Time.realtimeSinceStartup < _nextPlayTime) return; // previous utterance still playing

            byte[] wav;
            lock (AudioLock)
            {
                if (AudioQueue.Count == 0) return;
                wav = AudioQueue.Dequeue();
            }

            PlayWav(wav);
        }
        catch (Exception e) { Utils.ThrowException(e); }
    }

    // MAIN THREAD. Clear per-game state at game start / lobby so voice assignments are per-game.
    public static void Reset()
    {
        lock (TextLock) TextQueue.Clear();
        lock (AudioLock) AudioQueue.Clear();
        Assigned.Clear();
        _poolCursor = 0;
        _nextPlayTime = 0f;
        try { if (_currentSource != null) _currentSource.Stop(); }
        catch { /* pooled source may be gone */ }
        _currentSource = null;
        DestroyCurrentClip();
    }

    private static void EnsureWorker()
    {
        lock (TextLock)
        {
            if (_workerRunning) return;
            _workerRunning = true;
        }

        Task.Run(WorkerLoop);
    }

    // WORKER THREAD. Managed types only (HTTP + byte[]). Never touches Unity/IL2CPP.
    private static async Task WorkerLoop()
    {
        try
        {
            while (true)
            {
                (string text, VoiceProfile profile) item;
                lock (TextLock)
                {
                    if (TextQueue.Count == 0) { _workerRunning = false; return; }
                    item = TextQueue.Dequeue();
                }

                string url = Main.VoiceVoxEngineUrl?.Value ?? "http://127.0.0.1:50021";
                byte[] wav = await VoiceVoxFetcher.SynthesizeAsync(item.text, item.profile, url);
                if (wav == null || wav.Length == 0) continue; // engine down / unknown style -> skip

                lock (AudioLock)
                {
                    AudioQueue.Enqueue(wav);
                    while (AudioQueue.Count > MaxAudioQueue) AudioQueue.Dequeue();
                }
            }
        }
        catch (Exception ex) { Logger.Warn($"WorkerLoop error: {ex.Message}", "VoiceVoxManager"); }
        finally { lock (TextLock) { _workerRunning = false; } }
    }

    // MAIN THREAD. IL2CPP audio decode + play, reusing the WAV recipe from CustomSoundsManager.
    private static void PlayWav(byte[] bytes)
    {
        // Managed byte[] -> Il2CppStructArray<byte> via Marshal.Copy (per-element indexer is a trap — MEMORY.md).
        var il2cppBytes = new Il2CppStructArray<byte>(bytes.Length);
        Marshal.Copy(bytes, 0, IntPtr.Add(il2cppBytes.Pointer, IntPtr.Size * 4), bytes.Length);

        var wav = new CustomSoundsManager.WAV(il2cppBytes);
        if (wav.SampleCount <= 0 || wav.Frequency <= 0) return;

        // Free the previous utterance's native PCM buffer. The _nextPlayTime gate guarantees it has
        // finished playing before we get here, so destroying it now is safe. DontUnloadUnusedAsset
        // opts a clip out of the only automatic reclaim path, so it MUST be destroyed explicitly or
        // it leaks native memory for the whole (24h) session.
        DestroyCurrentClip();

        AudioClip clip = AudioClip.Create("VVTTS", wav.SampleCount, 1, wav.Frequency, false, false);
        clip.SetData(wav.LeftChannel, 0);
        clip.hideFlags |= HideFlags.DontUnloadUnusedAsset; // survive the settings-menu GC.Collect while in-flight
        _currentClip = clip;

        float vol = Main.VoiceVoxVolume?.Value ?? 1f;
        _currentSource = SoundManager.Instance.PlaySound(clip, false, vol);

        float len = (float)wav.SampleCount / wav.Frequency;
        _nextPlayTime = Time.realtimeSinceStartup + len + GapSeconds;
    }

    private static void DestroyCurrentClip()
    {
        if (_currentClip == null) return;
        try
        {
            _currentClip.hideFlags &= ~HideFlags.DontUnloadUnusedAsset;
            UnityEngine.Object.Destroy(_currentClip);
        }
        catch { /* already destroyed */ }

        _currentClip = null;
    }

    // MAIN THREAD. Priority: config override (friendCode first, then name) -> pool auto-assign.
    private static VoiceProfile ResolveProfile(PlayerControl player)
    {
        byte id = player.PlayerId;
        if (Assigned.TryGetValue(id, out VoiceProfile existing)) return existing;

        string fc = player.FriendCode ?? string.Empty;
        string nm = player.GetRealName() ?? string.Empty;
        VoiceProfile profile = null;

        foreach ((string friendCode, string name, VoiceProfile p) o in VoiceVoxVoiceConfig.Overrides)
        {
            if (!string.IsNullOrEmpty(o.friendCode) && o.friendCode == fc) { profile = o.p; break; }
        }

        if (profile == null)
        {
            foreach ((string friendCode, string name, VoiceProfile p) o in VoiceVoxVoiceConfig.Overrides)
            {
                if (!string.IsNullOrEmpty(o.name) && string.Equals(o.name, nm, StringComparison.OrdinalIgnoreCase))
                {
                    profile = o.p;
                    break;
                }
            }
        }

        if (profile == null)
        {
            int[] pool = GetPool();
            int styleId = pool.Length > 0 ? pool[_poolCursor % pool.Length] : 3;
            _poolCursor++;
            profile = new VoiceProfile { StyleId = styleId };
        }

        Assigned[id] = profile;
        return profile;
    }

    // config explicit pool > /speakers-derived > hardcoded fallback
    private static int[] GetPool()
    {
        if (VoiceVoxVoiceConfig.DefaultPool.Count > 0) return VoiceVoxVoiceConfig.DefaultPool.ToArray();
        lock (PoolLock) { if (_availableStyleIds.Count > 0) return _availableStyleIds.ToArray(); }
        return VoiceVoxVoiceConfig.GetHardcodedPool();
    }

    private static readonly Regex TagRegex = new("<[^>]*>", RegexOptions.Compiled);
    private static readonly Regex UrlRegex = new(@"https?://\S+", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    // Strip rich-text/TMP tags, URLs, leading newlines; collapse whitespace; truncate.
    private static string Sanitize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        string s = TagRegex.Replace(raw, string.Empty);
        s = UrlRegex.Replace(s, string.Empty);
        s = WhitespaceRegex.Replace(s, " ").Trim();

        int max = Main.VoiceVoxMaxChars?.Value ?? 120;
        if (max > 0 && s.Length > max) s = s[..max];

        return s;
    }
}
