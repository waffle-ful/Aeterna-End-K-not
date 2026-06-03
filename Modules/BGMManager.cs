using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using UnityEngine;

namespace EndKnot.Modules;

public static class BGMManager
{
    public const int BGMOptionId = 44500;
    private const float FadeOutDuration = 1.5f;

    public static OptionItem ClimaxCount;

    public static readonly string BGMPath = $"{Environment.CurrentDirectory.Replace(@"\", "/")}/BepInEx/resources/BGM/";

    private static AudioSource currentSource;
    private static string currentSlot = string.Empty;
    private static BGMEntry currentEntry;

    public static string CurrentBGMName => currentSlot;
    private static readonly Dictionary<string, AudioClip> BgmCache = [];

    public static bool RoleOverrideActive;

    // ── BGMEntry ─────────────────────────────────────────────────────────────

    public class BGMEntry
    {
        public string file   { get; set; }
        public int    weight { get; set; } = 1;
        public string title  { get; set; }
        public string author { get; set; }
    }

    // ── Playlist loading ─────────────────────────────────────────────────────

    private static Dictionary<string, List<BGMEntry>> _playlist;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        ReadCommentHandling    = JsonCommentHandling.Skip,
        AllowTrailingCommas    = true,
        PropertyNameCaseInsensitive = true,
    };

    private static Dictionary<string, List<BGMEntry>> EnsurePlaylist()
    {
        _playlist ??= LoadPlaylist();
        return _playlist;
    }

    public static void InvalidatePlaylist() => _playlist = null;

    private static Dictionary<string, List<BGMEntry>> LoadPlaylist()
    {
        var result = LoadEmbeddedPlaylist();

        string userPath = BGMPath + "bgm_config.json";
        if (!File.Exists(userPath)) return result;

        try
        {
            using FileStream fs = File.OpenRead(userPath);
            var overrides = JsonSerializer.Deserialize<Dictionary<string, List<BGMEntry>>>(fs, JsonOpts) ?? [];
            foreach (var (slot, entries) in overrides)
            {
                if (entries == null) continue;
                var valid = entries.FindAll(e => e?.file != null && e.weight > 0);
                if (valid.Count > 0) result[slot] = valid;
            }
        }
        catch (Exception ex) { Logger.Exception(ex, "BGMManager.LoadPlaylist.User"); }

        return result;
    }

    private static Dictionary<string, List<BGMEntry>> LoadEmbeddedPlaylist()
    {
        try
        {
            Stream stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("EndKnot.Resources.Sounds.BGM.bgm_titles.json");
            if (stream == null)
            {
                Logger.Warn("Embedded bgm_titles.json not found", "BGMManager");
                return [];
            }
            using (stream)
            {
                var raw = JsonSerializer.Deserialize<Dictionary<string, List<BGMEntry>>>(stream, JsonOpts) ?? [];
                var result = new Dictionary<string, List<BGMEntry>>();
                foreach (var (slot, entries) in raw)
                    result[slot] = entries?.FindAll(e => e?.file != null && e.weight > 0) ?? [];
                return result;
            }
        }
        catch (Exception ex)
        {
            Logger.Exception(ex, "BGMManager.LoadEmbeddedPlaylist");
            return [];
        }
    }

    // ── Weighted random selection ─────────────────────────────────────────────

    // BGM 専用の独立した乱数。IRandom.Instance はゲーム開始時に役職割当アルゴリズム
    // (決定論的 seed の場合あり) で再シードされ得るため、共有すると偏りの原因になる。
    private static readonly System.Random BgmRandom = new();

    private static BGMEntry PickTrack(string slot)
    {
        var pl = EnsurePlaylist();
        if (!pl.TryGetValue(slot, out List<BGMEntry> entries) || entries == null || entries.Count == 0)
            return null;

        if (entries.Count == 1) return entries[0];

        int total = 0;
        foreach (var e in entries) total += e.weight;
        if (total <= 0) return entries[0];

        int roll = BgmRandom.Next(total);
        int acc = 0;
        foreach (var e in entries)
        {
            acc += e.weight;
            if (roll < acc) return e;
        }
        return entries[^1];
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public static void SetupCustomOption()
    {
        ClimaxCount = new IntegerOptionItem(BGMOptionId + 1, "BGMClimaxCount", new(2, 15, 1), 6, TabGroup.SystemSettings)
            .SetHeader(true)
            .SetValueFormat(OptionFormat.Players);
    }

    public static void SetMenuBGM()    => Play("menu");
    public static void SetLobbyBGM()   => Play("lobby");
    public static void SetMeetingBGM() => Play("meeting");
    public static void SetEndingBGM()  => Play("result");

    public static void RetryCurrentCredit()
    {
        if (currentEntry == null) return;
        BGMInfoDisplay.Show(currentEntry.title, currentEntry.author, currentEntry.file);
    }

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
                    if (p?.Player == null || !p.Player.isPlaying) continue;
                    // BGMManager が管理している AudioSource は止めない。
                    // SoundManager が stop 後に native object を回収すると IL2CPP fake-null になり
                    // currentSource != null ガードが壊れるため。
                    if (currentSource != null && p.Player.Pointer == currentSource.Pointer) continue;
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
        if (currentSource == null) { currentSlot = string.Empty; currentEntry = null; return; }

        AudioSource fading = currentSource;
        currentSource = null;
        currentSlot   = string.Empty;
        currentEntry  = null;
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
            // SoundManager.PlaySound はプール済み AudioSource を再利用するため、フェード対象の
            // source が直後の Play() で新トラックとして採用されることがある。そのまま絞り続けると
            // 鳴り始めた新 BGM を 0 まで下げて Stop してしまう (= 一瞬鳴って消える) ので中止する。
            if (IsAdoptedAsCurrent(src))
            {
                Logger.Info("Fade aborted: source re-adopted as current BGM", "BGMManager");
                yield break;
            }
            src.volume = startVol * (1f - t / FadeOutDuration);
            yield return null;
        }
        if (src != null && !IsAdoptedAsCurrent(src)) { src.volume = 0f; src.Stop(); }
    }

    private static bool IsAdoptedAsCurrent(AudioSource src)
        => src != null && currentSource != null && src.Pointer == currentSource.Pointer;

    private static bool IsEnabled() => Main.EnableBGM?.Value ?? false;

    private static void Play(string slot)
    {
        try
        {
            if (!IsEnabled() || !OperatingSystem.IsWindows()) return;

            BGMEntry entry;
            if (currentSlot == slot && currentEntry != null)
            {
                // このスロットには既に曲をコミット済み。
                if (currentSource != null && currentSource.isPlaying)
                {
                    // ちゃんと鳴っている → クレジット再試行だけで終了（再抽選しない）。
                    if ((Main.ShowBGMInfo?.Value ?? true) && !BGMInfoDisplay.HasDisplay)
                        RetryCurrentCredit();
                    return;
                }

                // まだ鳴っていない（OnGameJoined の初回 SetLobbyBGM は AudioListener 準備前で
                // 発音しないことがある／途中で停止した等）→ 同じ曲を鳴らし直す。
                // ここで PickTrack し直すと別の曲が一瞬鳴る "0の部屋一瞬" バグになるので必ず同一 entry。
                entry = currentEntry;
            }
            else
                entry = PickTrack(slot);

            if (entry == null)
            {
                // スロットに曲がない場合は前の曲をフェードアウト
                Stop();
                return;
            }

            AudioClip clip = LoadBGM(entry.file);
            if (clip == null)
            {
                Stop();
                return;
            }

            if (currentSource != null)
            {
                // currentSource を先に null 化してから fade に渡す。逆順だと StartCoroutine の
                // 同期初回イテレーションで「src == currentSource」を満たして即 abort してしまう。
                AudioSource previous = currentSource;
                currentSource = null;
                StartFadeOut(previous);
            }

            SilenceVanillaAudio();

            float vol = Main.BGMVolume?.Value ?? 0.7f;
            currentSource = SoundManager.Instance.PlaySound(clip, true, vol);
            currentSlot   = slot;
            currentEntry  = entry;
            Logger.Info($"Playing BGM: slot={slot}, file={entry.file}", "BGMManager");

            if (Main.ShowBGMInfo?.Value ?? true)
                BGMInfoDisplay.Show(entry.title, entry.author, entry.file);
        }
        catch (Exception ex) { Utils.ThrowException(ex); }
    }

    // ── File loading ──────────────────────────────────────────────────────────

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
            GenerateExampleConfig();
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

            if (clip != null)
            {
                // ロビーで設定メニューを閉じると GameOptionsMenuPatch.Cleanup が GC.Collect +
                // Resources.UnloadUnusedAssets を呼ぶ (Backrooms 経路でも同様)。静的キャッシュの
                // 管理参照だけでは IL2CPP の UnloadUnusedAssets から守れず、再生中のクリップが
                // 消されて無音化する罠 (BackroomsAmbient と同じ 2026-05-23 の教訓)。
                clip.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                BgmCache[name] = clip;
            }

            return clip;
        }
        catch (Exception ex)
        {
            Logger.Exception(ex, "BGMManager.LoadBGM");
            return null;
        }
    }

    private static void GenerateExampleConfig()
    {
        string examplePath = BGMPath + "bgm_config.example.jsonc";
        if (File.Exists(examplePath)) return;

        const string example =
            "// BGM ランダム再生設定サンプル / BGM random playlist config sample\n" +
            "// このファイルを \"bgm_config.json\" にリネームして使ってください。\n" +
            "// Rename this file to \"bgm_config.json\" to use it.\n" +
            "//\n" +
            "// 【ファイルの置き方 / File placement】\n" +
            "//   このファイルと音声ファイル (.ogg/.mp3/.wav) を同じフォルダに置いてください。\n" +
            "//   Place audio files (.ogg/.mp3/.wav) in the same folder as this config.\n" +
            "//\n" +
            "// 【weight（重み）について / About weight】\n" +
            "//   整数で指定します。大きいほど選ばれやすくなります（0 は無効）。\n" +
            "//   Set as integer. Higher = more likely to be selected. 0 = disabled.\n" +
            "//   例 / Example: weight 3 と weight 1 なら 75% / 25% の確率で選ばれます。\n" +
            "//                 weight 3 and weight 1 = 75% / 25% chance.\n" +
            "//\n" +
            "// 【スロット一覧 / Available slots】\n" +
            "//   menu    ... メインメニュー / Main menu\n" +
            "//   lobby   ... ロビー / Lobby\n" +
            "//   intask  ... ゲーム中（通常）/ In-game (normal)\n" +
            "//   climax  ... ゲーム中（クライマックス）/ In-game (climax, few players remaining)\n" +
            "//   meeting ... 会議中 / During meeting\n" +
            "//   result  ... リザルト画面 / Results screen\n" +
            "//\n" +
            "// 【注意 / Note】\n" +
            "//   スロットを書くとそのスロットのデフォルト BGM は上書きされます。\n" +
            "//   If a slot is listed here, it replaces the built-in BGM for that slot.\n" +
            "{\n" +
            "  \"lobby\": [\n" +
            "    { \"file\": \"my_lobby_track1\", \"weight\": 2, \"title\": \"My Lobby Song\",   \"author\": \"Artist A\" },\n" +
            "    { \"file\": \"my_lobby_track2\", \"weight\": 1, \"title\": \"Chill Vibes\",     \"author\": \"Artist B\" }\n" +
            "  ],\n" +
            "  \"intask\": [\n" +
            "    { \"file\": \"my_intask\",       \"weight\": 1, \"title\": \"Focus Mode\",      \"author\": \"Artist C\" }\n" +
            "  ],\n" +
            "  \"climax\": [\n" +
            "    { \"file\": \"my_climax\",       \"weight\": 1, \"title\": \"Final Countdown\", \"author\": \"Artist C\" }\n" +
            "  ]\n" +
            "}\n";

        try { File.WriteAllText(examplePath, example, System.Text.Encoding.UTF8); }
        catch (Exception ex) { Logger.Exception(ex, "BGMManager.GenerateExampleConfig"); }
    }
}
