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
    private const float FadeInDuration = 1.2f;

    public static OptionItem ClimaxCount;

    public static readonly string BGMPath = $"{Environment.CurrentDirectory.Replace(@"\", "/")}/BepInEx/resources/BGM/";

    private static AudioSource currentSource;
    private static string currentSlot = string.Empty;
    private static BGMEntry currentEntry;

    // 裏デコード待ちの再生予約。クリップが PrimeCache に届いた瞬間、フェードインで合流する。
    // 同期デコードへのフォールバックは存在しない (それが唯一メインスレッドを止める経路のため)。
    private static string pendingSlot;
    private static BGMEntry pendingEntry;

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
        PinnedPicks.Clear();
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

    // 抽選結果のスロット単位ピン。遅延プリロードでは「先読みするファイル」と「実際に鳴らすファイル」が
    // 同一でなければ意味がない (抽選が再生直前だと alt 曲を系ごと全部温める羽目になる) ため、最初の抽選で
    // 確定させて以後は同じエントリを返す。スロットが wanted から外れた時に planner が解除し、次は再抽選。
    private static readonly Dictionary<string, BGMEntry> PinnedPicks = [];

    private static BGMEntry PickTrack(string slot)
    {
        if (PinnedPicks.TryGetValue(slot, out BGMEntry pinned) && pinned != null) return pinned;

        BGMEntry chosen = PickTrackFresh(slot);
        if (chosen != null) PinnedPicks[slot] = chosen;
        return chosen;
    }

    private static BGMEntry PickTrackFresh(string slot)
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
        climaxArmed = false;
    }

    public static void Tick()
    {
        if (!IsEnabled() || !OperatingSystem.IsWindows()) return;

        // 状態別遅延プリロード。watchdog より先に毎秒無条件で回す (メニュー/ロビー/リザルトでも
        // 先読みと解放は進めたいので、下の InGame ガードより前に置く)。
        try { PlannerTick(); }
        catch (Exception ex) { Logger.Exception(ex, "BGMManager.PlannerTick"); }

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

            // 裏デコード待ち。届き次第 PrimeCache がフェードインで合流させるので、ここで再抽選や
            // Play 連打をしない (毎秒ログが流れるのも防ぐ)。ただしクリップが既にキャッシュ済みなら
            // 合流トリガーを取りこぼしている (稀なレース) ので、待たずに Play へ進んで自己回復する。
            if (pendingSlot == want && (pendingEntry?.file == null || TryGetCachedClip(pendingEntry.file) == null)) return;

            Logger.Info($"BGM watchdog: slot {(currentSlot.Length == 0 ? "(none)" : currentSlot)} -> {want}", "BGMManager");
            Play(want);

            // pending 化した場合は失敗ではない (デコード失敗は OnPreloadFailed が別途 failed 化する)。
            if (currentSlot != want && pendingSlot != want)
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
        pendingSlot = null;
        pendingEntry = null;
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

            AudioClip clip = TryGetCachedClip(entry.file);

            if (clip == null)
            {
                // 同期デコードはしない (唯一メインスレッドを止める経路)。裏スレッドへ依頼して pending 化し、
                // PrimeCache に届いた瞬間フェードインで合流する。それまでは前の曲をフェードアウトして無音待機。
                bool alreadyPending = pendingSlot == slot && pendingEntry != null
                    && string.Equals(pendingEntry.file, entry.file, StringComparison.OrdinalIgnoreCase);

                pendingSlot = slot;
                pendingEntry = entry;
                CustomSoundsManager.RequestBgmDecode(entry.file);

                if (!alreadyPending)
                    Logger.Info($"BGM not warm yet: slot={slot}, file={entry.file} - decoding in background", "BGMManager");

                if (currentSource != null)
                {
                    AudioSource previous = currentSource;
                    currentSource = null;
                    StartFadeOut(previous);
                }

                BGMInfoDisplay.Hide();
                currentSlot  = string.Empty;
                currentEntry = null;
                return;
            }

            pendingSlot  = null;
            pendingEntry = null;
            StartPlayback(slot, entry, clip, fadeIn: false);
        }
        catch (Exception ex) { Utils.ThrowException(ex); }
    }

    private static void StartPlayback(string slot, BGMEntry entry, AudioClip clip, bool fadeIn)
    {
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
        currentSource = SoundManager.Instance.PlaySound(clip, true, fadeIn ? 0f : vol);
        currentSlot   = slot;
        currentEntry  = entry;
        pendingSlot   = null;
        pendingEntry  = null;
        Logger.Info($"Playing BGM: slot={slot}, file={entry.file}{(fadeIn ? " (fade-in)" : "")}", "BGMManager");

        if (fadeIn && currentSource != null && Main.Instance != null)
            Main.Instance.StartCoroutine(FadeInRoutine(currentSource, vol));

        if (Main.ShowBGMInfo?.Value ?? true)
            BGMInfoDisplay.Show(entry.title, entry.author, entry.file);
    }

    private static IEnumerator FadeInRoutine(AudioSource src, float targetVol)
    {
        for (float t = 0f; t < FadeInDuration; t += Time.deltaTime)
        {
            if (src == null || !IsAdoptedAsCurrent(src)) yield break;
            src.volume = targetVol * (t / FadeInDuration);
            yield return null;
        }
        if (src != null && IsAdoptedAsCurrent(src)) src.volume = targetVol;
    }

    // BgmCache の取り出し口。fake-null (シーン遷移の暗黙 UnloadUnusedAssets 等で native 側だけ破棄) は
    // ここで検出して取り除き、呼び出し側には「未キャッシュ」として再デコードさせる。
    private static AudioClip TryGetCachedClip(string name)
    {
        if (name == null || !BgmCache.TryGetValue(name, out AudioClip cached)) return null;
        if (cached) return cached;
        BgmCache.Remove(name);
        return null;
    }

    // ── File loading ──────────────────────────────────────────────────────────

    private static readonly string[] SupportedExtensions = [".ogg", ".mp3", ".wav"];

    // path 解決 + 埋込リソースのディスク展開。ファイル I/O + managed Stream のみなので
    // バックグラウンドスレッド (CustomSoundsManager.BgmDecodeLoop) からも呼べる。
    // 複数スレッドが同じファイルを同時展開しないよう lock で直列化する。
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

    // preload ポンプ (メインスレッド) からクリップ化済み BGM を受け取る。二重デコードと
    // レースした場合は先勝ち (後着の clip は破棄して native リークを防ぐ)。
    internal static void PrimeCache(string name, AudioClip clip)
    {
        if (clip == null) return;

        if (BgmCache.TryGetValue(name, out AudioClip existing) && existing)
        {
            UnityEngine.Object.Destroy(clip);
            return;
        }

        // デコード中に状態が先へ進んで不要になったトラックはキャッシュしない。
        // 「せっかく作ったから」で持つと全曲常駐に逆戻りする。
        if (!IsFileWantedOrPending(name))
        {
            UnityEngine.Object.Destroy(clip);
            Logger.Info($"Discarding preloaded BGM {name} (no longer wanted)", "BGMManager");
            gcWanted = true;
            return;
        }

        // ロビーで設定メニューを閉じると GameOptionsMenuPatch.Cleanup が GC.Collect +
        // Resources.UnloadUnusedAssets を呼ぶ (Backrooms 経路でも同様)。静的キャッシュの
        // 管理参照だけでは IL2CPP の UnloadUnusedAssets から守れず、再生中のクリップが
        // 消されて無音化する罠 (BackroomsAmbient と同じ 2026-05-23 の教訓)。
        clip.hideFlags |= HideFlags.DontUnloadUnusedAsset;
        BgmCache[name] = clip;
        gcWanted = true;

        // この曲を待って無音で耐えていたなら、今この瞬間からフェードインで合流する。
        // BGM が OFF に切り替えられていた場合も pending は必ず畳む — 畳み損ねると再 ON 後に
        // watchdog が「デコード待ち」と誤認して毎秒 return し続け、無音が次の会議まで固定される。
        if (pendingSlot != null && pendingEntry?.file != null
            && string.Equals(pendingEntry.file, name, StringComparison.OrdinalIgnoreCase))
        {
            if (IsEnabled())
                StartPlayback(pendingSlot, pendingEntry, clip, fadeIn: true);
            else
            {
                pendingSlot  = null;
                pendingEntry = null;
            }
        }
    }

    // 裏デコードが失敗した (ファイル欠落/破損など) 通知。pending をここで畳まないと watchdog が
    // 永久に「デコード待ち」だと思い込んで無音のまま停滞する。また pending に限らず、この file を
    // 指しているスロットは全部 failed 化する — しないと planner の prefetch が毎秒デコード依頼→
    // 即失敗 (スレッド生成 churn) をゲーム終了まで無限に繰り返す。
    internal static void OnPreloadFailed(string name)
    {
        if (name == null) return;

        List<string> failedSlots = null;
        foreach (var (slot, entry) in PinnedPicks)
            if (entry?.file != null && string.Equals(entry.file, name, StringComparison.OrdinalIgnoreCase))
                (failedSlots ??= []).Add(slot);

        if (failedSlots != null)
        {
            foreach (string slot in failedSlots)
            {
                if (WatchdogFailedSlots.Add(slot))
                    Logger.Warn($"BGM decode failed for slot '{slot}' (file={name}) - giving up until next game", "BGMManager");
            }
        }

        if (pendingEntry?.file == null || !string.Equals(pendingEntry.file, name, StringComparison.OrdinalIgnoreCase)) return;

        if (pendingSlot != null && WatchdogFailedSlots.Add(pendingSlot))
            Logger.Warn($"BGM decode failed for pending slot '{pendingSlot}' (file={name}) - giving up until next game", "BGMManager");

        pendingSlot  = null;
        pendingEntry = null;
    }

    // ── 状態別遅延プリロード (planner) ────────────────────────────────────────
    // 全曲常駐 (7クリップ・float PCM で数百MB) をやめ、「今鳴っている曲 + 次に使う見込みの曲」だけを
    // 温める。毎秒 wanted 集合を計算し、足りないものは裏デコードへ依頼、要らなくなったものは
    // フェード完走猶予つきで破棄する。

    private const float ReleaseGraceSeconds = 3f; // FadeOutDuration (1.5s) の完走を待ってから破棄する

    // 破棄予約。BgmCache に入っている = ポンプ完了済みクリップのみがここへ来る (チャンク書込み中の
    // クリップは PrimeCache 前なので対象外 — SetData と Destroy の競合 (BUG-20260810-04) を踏まない)。
    private static readonly List<(string Name, AudioClip Clip, float Due)> PendingRelease = [];

    private static HashSet<string> lastWantedFiles = new(StringComparer.OrdinalIgnoreCase);
    private static bool gcWanted;
    private static bool climaxArmed;

    internal static bool IsFileWantedOrPending(string name)
    {
        if (name == null) return false;
        if (pendingEntry?.file != null && string.Equals(pendingEntry.file, name, StringComparison.OrdinalIgnoreCase)) return true;
        if (currentEntry?.file != null && string.Equals(currentEntry.file, name, StringComparison.OrdinalIgnoreCase)) return true;
        return lastWantedFiles.Contains(name);
    }

    private static HashSet<string> ComputeWantedSlots()
    {
        var slots = new HashSet<string>();

        if (currentSlot.Length > 0) slots.Add(currentSlot);
        if (pendingSlot != null) slots.Add(pendingSlot);

        if (GameStates.IsLobby)
        {
            // ロビー = 次のゲームの入口。intro (~6秒) の猶予があるので intask はここから温め始めれば
            // タスクフェーズ開始に間に合う。
            slots.Add("lobby");
            slots.Add("intask");
        }
        else if (GameStates.IsEnded)
        {
            // 勝敗判定が出た瞬間から result を温める (outro の SetEndingBGM に間に合わせる)。
            // ⚠️ この判定は InGame と独立に評価すること: OutroPatch の OnGameEnd が InGame=false を
            // 立てた後も GameState は Ended のままリザルト画面が続く。旧実装は
            // `if (InGame && !IsLobby)` の内側に IsEnded 分岐を置いていたため、この窓に毎秒 planner が
            // 着地すると「メニュー扱い」に落ちて result を破棄し、SetEndingBGM が cache miss →
            // デコード完了までバニラの勝敗スティンガーが露出していた (確率的再発の正体)。
            slots.Add("result");
            // result が鳴り始めたら次はロビーへ戻るので、ロビー曲を先に温めておく。
            if (currentSlot == "result") slots.Add("lobby");
        }
        else if (GameStates.InGame)
        {
            slots.Add(GetTaskSlot());
            slots.Add("meeting");

            // result は IsEnded から温めたのでは間に合わない — 勝敗確定から outro (SetEndingBGM) まで
            // 数秒しかなく、デコード完了までバニラの勝敗スティンガーが露出する (2026-08-15 実測 2〜3 秒)。
            // 試合は必ず終わるので、タスクフェーズの間ずっと wanted に含めて常温しておく。
            slots.Add("result");

            // climax は会議を待たずキル数発で突入し得るので、閾値+2人まで迫ったら先に温める。
            // 一度武装したら +4 人を超えるまで解除しない (蘇生等で境界を毎秒跨ぐと wanted が
            // トグルし、破棄→再デコード→GC 先撃ちの空回りが毎秒連発するため)。
            int alive = Main.AllAlivePlayerControlsToList?.Count ?? 15;
            int threshold = ClimaxCount?.GetInt() ?? 6;
            if (alive <= threshold + 2) climaxArmed = true;
            else if (alive > threshold + 4) climaxArmed = false;
            if (climaxArmed) slots.Add("climax");

            if (HasTracks("dead")) slots.Add("dead");
        }
        else
        {
            slots.Add("menu");
            // メニューからロビーを作成/参加した瞬間 (SetLobbyBGM) に間に合わせる。ここで温めておかないと
            // 入場から数秒無音になる (result→lobby 経路と同じ理屈の兄弟)。
            slots.Add("lobby");
        }

        return slots;
    }

    private static void PlannerTick()
    {
        HashSet<string> wantedSlots = ComputeWantedSlots();

        var wantedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string slot in wantedSlots)
        {
            if (WatchdogFailedSlots.Contains(slot)) continue;
            BGMEntry e = PickTrack(slot);
            if (e?.file != null) wantedFiles.Add(e.file);
        }

        if (currentEntry?.file != null) wantedFiles.Add(currentEntry.file);
        if (pendingEntry?.file != null) wantedFiles.Add(pendingEntry.file);
        lastWantedFiles = wantedFiles;

        // 不要になったスロットの抽選ピンを解除 (次に鳴る機会には再抽選される)
        List<string> stalePins = null;
        foreach (string slot in PinnedPicks.Keys)
            if (!wantedSlots.Contains(slot))
                (stalePins ??= []).Add(slot);
        if (stalePins != null)
            foreach (string slot in stalePins)
                PinnedPicks.Remove(slot);

        // 足りない wanted は裏デコードへ (二重依頼は in-flight 側で弾かれる)
        foreach (string file in wantedFiles)
            if (TryGetCachedClip(file) == null)
                CustomSoundsManager.RequestBgmDecode(file);

        // 要らなくなったキャッシュはフェード完走猶予つきで破棄予約
        List<string> staleClips = null;
        foreach (string name in BgmCache.Keys)
            if (!wantedFiles.Contains(name))
                (staleClips ??= []).Add(name);

        if (staleClips != null)
        {
            foreach (string name in staleClips)
            {
                AudioClip clip = BgmCache[name];
                BgmCache.Remove(name);
                if (clip) PendingRelease.Add((name, clip, Time.realtimeSinceStartup + ReleaseGraceSeconds));
                Logger.Info($"Releasing BGM {name} (not wanted in current state)", "BGMManager");
            }
        }

        for (int i = PendingRelease.Count - 1; i >= 0; i--)
        {
            (string _, AudioClip clip, float due) = PendingRelease[i];
            if (Time.realtimeSinceStartup < due) continue;
            PendingRelease.RemoveAt(i);
            if (clip) UnityEngine.Object.Destroy(clip);
            gcWanted = true;
        }

        // ロード完了/解放で出た managed ゴミ (数十MB の float[] デコードバッファ等) を、タスク中の
        // フレームを止めない局面でまとめて回収する (nos-gc-strategy の「先撃ちで隠す」)。
        if (gcWanted && CustomSoundsManager.IsBgmPipelineIdle && CanRunGcNow())
        {
            gcWanted = false;
            GC.Collect();
            Logger.Info("BGM preload GC sweep", "BGMManager");
        }
    }

    private static bool CanRunGcNow()
        => !GameStates.InGame || GameStates.IsLobby || GameStates.IsMeeting || GameStates.IsEnded || ExileController.Instance;

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

        // ここは裏スレッド (BgmDecodeLoop → ResolveOrExtract) からも呼ばれるため Logger は使えない。
        // サンプル生成の失敗は実害がない (次回起動時に再試行される) ので黙って諦める。
        try { File.WriteAllText(examplePath, example, System.Text.Encoding.UTF8); }
        catch { /* ignore */ }
    }
}
