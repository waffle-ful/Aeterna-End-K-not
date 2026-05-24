using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace EndKnot.Modules;

public static class BGMManager
{
    public const int BGMOptionId = 44500;
    private const float FadeOutDuration = 1.5f;

    public static OptionItem ClimaxCount;

    public static readonly string BGMPath = $"{Environment.CurrentDirectory.Replace(@"\", "/")}/BepInEx/resources/BGM/";

    private static AudioSource currentSource;
    private static string currentBGMName = string.Empty;

    public static string CurrentBGMName => currentBGMName;
    private static readonly Dictionary<string, AudioClip> BgmCache = [];

    public static bool RoleOverrideActive;

    public static void SetupCustomOption()
    {
        ClimaxCount = new IntegerOptionItem(BGMOptionId + 1, "BGMClimaxCount", new(2, 15, 1), 6, TabGroup.SystemSettings)
            .SetHeader(true)
            .SetValueFormat(OptionFormat.Players);
    }

    public static void SetMenuBGM() => Play("menu");
    public static void SetLobbyBGM() => Play("lobby");
    public static void SetMeetingBGM() => Play("meeting");
    public static void SetEndingBGM() => Play("result");

    public static void SilenceVanillaAudio()
    {
        try
        {
            // VanillaSuppressor.Apply は MainMenuManager.Start_Postfix で一度だけ走り、
            // その瞬間 Ambience GO がまだ生成されてないと永久に無効化されない。
            // ここで毎フレーム冪等に SetActive(false) を打つことで遅延生成を捕捉する。
            GameObject.Find("Ambience")?.SetActive(false);

            SoundManager sm = SoundManager.Instance;
            if (sm == null) return;

            sm.ChangeAmbienceVolume(0f);
            sm.StopNamedSound("MapTheme");

            if (sm.soundPlayers != null)
            {
                for (int i = sm.soundPlayers.Count - 1; i >= 0; i--)
                {
                    ISoundPlayer p = sm.soundPlayers[i];
                    if (p?.Player != null && p.Player.isPlaying)
                        p.Player.Stop();
                }
            }
        }
        catch { /* vanilla sound not present, ignore */ }
    }

    public static void SetTaskBGM()
    {
        if (!IsEnabled()) return;

        int alive = Main.AllAlivePlayerControlsToList?.Count ?? 15;
        int threshold = ClimaxCount?.GetInt() ?? 6;
        string bgm = alive <= threshold ? "climax" : "intask";
        Play(bgm);
    }

    public static void Stop()
    {
        BGMInfoDisplay.Hide();
        if (currentSource == null) { currentBGMName = string.Empty; return; }

        // Hand the source off to a fade coroutine and clear our reference, so a
        // subsequent Play() can start a new track at full volume immediately
        // while the old one tails out (cross-fade behaviour).
        AudioSource fading = currentSource;
        currentSource = null;
        currentBGMName = string.Empty;
        StartFadeOut(fading);
    }

    private static void StartFadeOut(AudioSource src)
    {
        if (src == null) return;
        if (Main.Instance == null) { src.Stop(); return; }
        Main.Instance.StartCoroutine(FadeOutRoutine(src));
    }

    private static IEnumerator FadeOutRoutine(AudioSource src)
    {
        if (src == null) yield break;
        float startVol = src.volume;
        for (float t = 0f; t < FadeOutDuration; t += Time.deltaTime)
        {
            if (src == null) yield break;
            src.volume = startVol * (1f - t / FadeOutDuration);
            yield return null;
        }
        if (src != null) { src.volume = 0f; src.Stop(); }
    }

    private static bool IsEnabled() => Main.EnableBGM?.Value ?? false;

    private static void Play(string name)
    {
        try
        {
            if (!IsEnabled() || !OperatingSystem.IsWindows()) return;

            // Audio is already playing this track — but the credit display may
            // have failed on first attempt (e.g. SetLobbyBGM fired from
            // OnGameJoined before HudManager spawned). Retry the credit so the
            // LobbyBehaviour.Start re-trigger can recover it.
            if (currentBGMName == name && currentSource != null && currentSource.isPlaying)
            {
                if ((Main.ShowBGMInfo?.Value ?? true) && !BGMInfoDisplay.HasDisplay)
                    BGMInfoDisplay.Show(name);
                return;
            }

            AudioClip clip = LoadBGM(name);
            if (clip == null)
            {
                // The new phase has no BGM file (e.g. user shipped only menu.ogg
                // + lobby.ogg, no intask/climax). Mirror the menu→lobby behaviour
                // and fade the previous track out instead of letting it bleed
                // across the phase boundary.
                Stop();
                return;
            }

            // Fade the previous track out asynchronously so the transition
            // sounds natural (cross-fade with the new clip starting below).
            if (currentSource != null)
            {
                StartFadeOut(currentSource);
                currentSource = null;
            }

            SilenceVanillaAudio();

            float vol = Main.BGMVolume?.Value ?? 0.7f;
            currentSource = SoundManager.Instance.PlaySound(clip, true, vol);
            currentBGMName = name;
            Logger.Info($"Playing BGM: {name}", "BGMManager");

            if (Main.ShowBGMInfo?.Value ?? true)
                BGMInfoDisplay.Show(name);
        }
        catch (Exception ex) { Utils.ThrowException(ex); }
    }

    private static readonly string[] SupportedExtensions = [".ogg", ".mp3", ".wav"];

    private static AudioClip LoadBGM(string name)
    {
        if (BgmCache.TryGetValue(name, out AudioClip cached) && cached != null) return cached;

        if (!Directory.Exists(BGMPath))
        {
            Directory.CreateDirectory(BGMPath);
            DirectoryInfo folder = new(BGMPath);
            if ((folder.Attributes & FileAttributes.Hidden) != FileAttributes.Hidden)
                folder.Attributes = FileAttributes.Hidden;
        }

        string foundPath = null;
        foreach (string ext in SupportedExtensions)
        {
            string candidate = BGMPath + name + ext;
            if (File.Exists(candidate)) { foundPath = candidate; break; }
        }

        if (foundPath == null)
        {
            Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"EndKnot.Resources.Sounds.BGM.{name}.ogg");
            if (stream == null)
            {
                Logger.Warn($"BGM not found (disk or embedded): {name}", "BGMManager");
                return null;
            }

            foundPath = BGMPath + name + ".ogg";
            using FileStream fs = File.Create(foundPath);
            stream.CopyTo(fs);
        }

        try
        {
            string ext = Path.GetExtension(foundPath).ToLowerInvariant();
            AudioClip clip = ext switch
            {
                ".ogg" => CustomSoundsManager.LoadOGG(foundPath),
                ".mp3" => CustomSoundsManager.LoadMP3(foundPath),
                ".wav" => CustomSoundsManager.LoadWAV(foundPath),
                _ => null
            };

            if (clip != null) BgmCache[name] = clip;
            return clip;
        }
        catch (Exception ex)
        {
            Logger.Exception(ex, "BGMManager.LoadBGM");
            return null;
        }
    }
}
