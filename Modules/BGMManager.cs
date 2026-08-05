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

    public static void InvalidatePlaylist()
    {
        _playlist = null;
        ResetWatchdog();
    }

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
    public static void SetLobbyBGM()
    {
        // ロビーに戻る = 次のゲームの入口。前ゲームで諦めたスロットをここで再挑戦可能にする。
        ResetWatchdog();
        Play("lobby");
    }
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
        Play(GetTaskSlot());
    }

    // タスク中に鳴らすべきスロットを決める。死亡専用 BGM は「dead スロットに曲がある時だけ」有効で、
    // 無い場合は従来の intask/climax に落ちる。ここでフォールバックしないと PickTrack→null→Stop() で
    // 死亡後が無音になり、「死んだら BGM が止まる」という直したい症状そのものを作ってしまう。
    private static string GetTaskSlot()
    {
        // WatchdogFailedSlots も見る。エントリはあるがファイルが読めない (タイポ/欠落) 場合、
        // Play("dead") が LoadBGM 失敗 → Stop() で「今鳴っている intask/climax まで道連れ」になり、
        // 直したかった無音がユーザーの設定ミス経由で復活してしまう。
        if (IsLocalPlayerDead() && HasTracks("dead") && !WatchdogFailedSlots.Contains("dead")) return "dead";

        int alive = Main.AllAlivePlayerControlsToList?.Count ?? 15;
        int threshold = ClimaxCount?.GetInt() ?? 6;
        return alive <= threshold ? "climax" : "intask";
    }

    private static bool HasTracks(string slot)
        => EnsurePlaylist().TryGetValue(slot, out List<BGMEntry> entries) && entries is { Count: > 0 };

    // ローカル(=ホスト)の生死のみを見る。BGM はホストの手元でしか鳴らないので、EHR の IsAlive() ではなく
    // バニラの Data.IsDead が正しい真実 (IsAlive() はゲーム後ロビーで GM/観戦ホストが常に false になる)。
    private static bool IsLocalPlayerDead()
    {
        PlayerControl lp = PlayerControl.LocalPlayer;
        return lp != null && lp.Data != null && lp.Data.IsDead;
    }

    // 1秒に1回の番犬。SetTaskBGM の発火点は intro 終了・会議クローズ・追放後の3箇所しかないため、
    // (a) ラウンド中に死亡してもスロットが切り替わらない (b) 何らかの外部要因で AudioSource を失うと
    // 次の会議まで無音のまま、という2つの穴がある。ここで毎秒あるべき状態へ寄せ直す。
    private static int stoppedTicks;

    // Play() が失敗したスロット (曲ファイル欠落等) を覚えておく。覚えないと毎秒 LoadBGM を叩き直し、
    // OGG の同期デコードは実測 1〜2 秒かかる (climax.ogg) ためフレームレートが落ちる。
    private static readonly HashSet<string> WatchdogFailedSlots = [];

    public static void ResetWatchdog()
    {
        WatchdogFailedSlots.Clear();
        stoppedTicks = 0;
    }

    public static void Tick()
    {
        if (!IsEnabled() || !OperatingSystem.IsWindows()) return;

        if (!Main.IntroDestroyed || !GameStates.InGame || GameStates.IsEnded || GameStates.IsMeeting
            || ExileController.Instance || AntiBlackout.SkipTasks) return;

        // リザルト BGM は OutroPatch が鳴らす。GameEndChecker.Ended が立つ前に outro が始まる窓が
        // あっても番犬が result を intask で潰さないようにする。
        if (currentSlot == "result") return;

        if (PlayerControl.LocalPlayer == null) return;

        string want = GetTaskSlot();

        if (currentSlot != want)
        {
            stoppedTicks = 0;
            if (WatchdogFailedSlots.Contains(want)) return;

            Logger.Info($"BGM watchdog: slot {(currentSlot.Length == 0 ? "(none)" : currentSlot)} -> {want}", "BGMManager");
            Play(want);

            if (currentSlot != want)
            {
                WatchdogFailedSlots.Add(want);
                Logger.Warn($"BGM watchdog: could not start slot '{want}' - giving up until next game", "BGMManager");
            }

            return;
        }

        if (currentSource != null && currentSource.isPlaying) { stoppedTicks = 0; return; }

        // 1 tick だけの停止は SoundManager のプール入れ替え等で一過性に起きうる。2 tick 連続で
        // 止まっている時だけ鳴らし直す (毎秒 0:00 から鳴り直す吃音ループを避ける)。
        if (++stoppedTicks < 2) return;

        stoppedTicks = 0;
        Logger.Warn($"BGM stopped unexpectedly (slot={want}) - restarting", "BGMManager");
        Play(want);
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

    // path 解決 + 埋込リソースのディスク展開。ファイル I/O + managed Stream のみなので
    // バックグラウンドスレッド (CustomSoundsManager.PreloadWorker) からも呼べる。
    // Play 経由の同期ロードと preload worker が同じファイルを同時展開しないよう lock で直列化する。
    private static readonly object ExtractLock = new();

    internal static string ResolveOrExtract(string name)
    {
        lock (ExtractLock)
        {
            if (!Directory.Exists(BGMPath))
            {
                Directory.CreateDirectory(BGMPath);
                DirectoryInfo folder = new(BGMPath);
                if ((folder.Attributes & FileAttributes.Hidden) != FileAttributes.Hidden)
                    folder.Attributes = FileAttributes.Hidden;
                GenerateExampleConfig();
            }

            foreach (string ext in SupportedExtensions)
            {
                string candidate = BGMPath + name + ext;
                if (File.Exists(candidate)) return candidate;
            }

            Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"EndKnot.Resources.Sounds.BGM.{name}.ogg");
            if (stream == null) return null;

            string target = BGMPath + name + ".ogg";
            using (stream)
            using (FileStream fs = File.Create(target))
                stream.CopyTo(fs);
            return target;
        }
    }

    // preload worker が温めるべきトラック名一覧 (プレイリスト記載の全エントリ)。実際に鳴る順に近い
    // 優先度順で返す。_playlist キャッシュには触らず読み捨てでロードする (裏スレッドから呼ぶため)。
    internal static List<string> GetPreloadFiles()
    {
        var result = new List<string>();
        if (!IsEnabled()) return result;

        try
        {
            var pl = LoadPlaylist();
            string[] slotOrder = ["menu", "lobby", "intask", "climax", "meeting", "result", "dead"];
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string slot in slotOrder)
                if (pl.TryGetValue(slot, out List<BGMEntry> entries) && entries != null)
                    foreach (var e in entries)
                        if (e?.file != null && seen.Add(e.file))
                            result.Add(e.file);

            // slotOrder 外のユーザー定義スロットも末尾に足す
            foreach (var (_, entries) in pl)
                if (entries != null)
                    foreach (var e in entries)
                        if (e?.file != null && seen.Add(e.file))
                            result.Add(e.file);
        }
        catch { /* 列挙失敗は preload を諦めるだけ (従来の同期ロードに戻る) */ }

        return result;
    }

    // preload ポンプ (メインスレッド) からクリップ化済み BGM を受け取る。Play 側の同期ロードと
    // レースした場合は先勝ち (後着の clip は破棄して native リークを防ぐ)。
    internal static void PrimeCache(string name, AudioClip clip)
    {
        if (clip == null) return;

        if (BgmCache.TryGetValue(name, out AudioClip existing) && existing != null)
        {
            UnityEngine.Object.Destroy(clip);
            return;
        }

        clip.hideFlags |= HideFlags.DontUnloadUnusedAsset;
        BgmCache[name] = clip;
    }

    private static AudioClip LoadBGM(string name)
    {
        if (BgmCache.TryGetValue(name, out AudioClip cached) && cached != null) return cached;

        string foundPath = ResolveOrExtract(name);
        if (foundPath == null)
        {
            Logger.Warn($"BGM not found (disk or embedded): {name}", "BGMManager");
            return null;
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
            "//   dead    ... 自分が死亡中 / While you are dead (falls back to intask/climax if empty)\n" +
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
            "  ],\n" +
            "  \"dead\": [\n" +
            "    { \"file\": \"my_dead\",         \"weight\": 1, \"title\": \"Ghost Waltz\",     \"author\": \"Artist D\" }\n" +
            "  ]\n" +
            "}\n";

        try { File.WriteAllText(examplePath, example, System.Text.Encoding.UTF8); }
        catch (Exception ex) { Logger.Exception(ex, "BGMManager.GenerateExampleConfig"); }
    }
}
