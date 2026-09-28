using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace EndKnot.Modules.YouTubeChat;

// YouTube ライブチャット連携の状態管理層。Static singleton。
//
// 責務:
//   - URL のパースと video ID 抽出
//   - polling timer (Time.fixedDeltaTime ベース、Manager.Tick から駆動)
//   - YouTubeChatFetcher のライフサイクル
//   - exponential backoff (429/Forbidden で 30s/60s/300s)
//   - 新着 ChatMessage を OnMessage event で公開 (UI 層が購読)
//
// Android では完全無効。FetchAsync は Task.Run でメインスレッドから切り離す。
//
// 注意: 非公式 API 直叩き、YouTube ToS のグレーゾーン。default OFF + 初回起動時警告。
public static class YouTubeChatManager
{
    public static event Action<string, string> OnMessage; // (author, text)
    public static event Action<string> OnStatusChanged;   // localized status string for UI

    public static bool IsActive { get; private set; }
    public static string CurrentVideoId { get; private set; } = "";
    public static string CurrentUrl { get; private set; } = "";

    private static YouTubeChatFetcher fetcher;
    private static float secondsSinceLastFetch;
    private static float lastHintSeconds; // YouTube から指示された pollingIntervalMillis 由来 (秒)
    private static int consecutiveFailures;
    private static int consecutiveNotFound;
    private static float backoffSeconds; // > 0 のときはこの秒数だけ追加で待機
    private static bool fetchInFlight;
    private static bool autoResumeAttempted;

    // 配信枠の終了検知。取得の合間に一定間隔で watch ページを確かめ、取得の準備に失敗した時は次回すぐ確かめる。
    private const float LiveCheckIntervalSeconds = 120f;
    private static float secondsSinceLiveCheck;
    private static volatile bool liveCheckForced;
    private static volatile bool endedDetected; // 取得スレッド → メインスレッド

    // 終了後の新しい枠の追跡 (メインスレッドのみで進める)。再接続で新しい枠が現れるまで少し掛かるので数回探す。
    private const float TrackRetrySeconds = 60f;
    private const int TrackMaxAttempts = 5;
    private static string trackingEndedVideoId = "";
    private static int trackingAttempts;
    private static float secondsUntilNextTrack;
    private static bool trackInFlight;
    private static int trackGeneration;
    private static volatile bool trackResultReady;
    private static volatile string trackResultVideoId;

    // /yt を打たなくても、OAuth 認証済みなら配信中の枠を定期的に探して自動で取得を始める (1 回 1 unit)。
    // /yt off で止めた後は、次に /yt で始めるまで勝手に再開しない。
    private const float AutoDetectIntervalSeconds = 180f;
    private static float secondsUntilAutoDetect;
    private static bool autoDetectInFlight;
    private static volatile bool autoDetectResultReady;
    private static volatile string autoDetectResultVideoId;
    public static bool AutoDetectSuppressed { get; set; }

    private static readonly List<string> pendingNotices = [];


    // /yt <url> から呼ばれる。失敗時は理由文字列を返す（成功時 null）。
    public static string Start(string url)
    {
        if (OperatingSystem.IsAndroid()) return "android_unsupported";
        if (string.IsNullOrWhiteSpace(url)) return "empty_url";

        string videoId = ExtractVideoId(url);
        if (string.IsNullOrEmpty(videoId)) return "invalid_url";

        Stop(silent: true);

        AutoDetectSuppressed = false;
        CurrentUrl = url.Trim();
        CurrentVideoId = videoId;
        fetcher = new YouTubeChatFetcher(videoId);
        consecutiveFailures = 0;
        consecutiveNotFound = 0;
        backoffSeconds = 0f;
        lastHintSeconds = 0f;
        secondsSinceLastFetch = 0f;
        secondsSinceLiveCheck = 0f;
        IsActive = true;

        OnStatusChanged?.Invoke($"started:{videoId}");
        Logger.Info($"YouTube chat polling started for video {videoId}", "YouTubeChatManager");
        return null;
    }

    public static void Stop(bool silent = false)
    {
        if (fetcher != null)
        {
            fetcher.Dispose();
            fetcher = null;
        }

        IsActive = false;
        CurrentVideoId = "";
        CurrentUrl = "";
        consecutiveFailures = 0;
        consecutiveNotFound = 0;
        backoffSeconds = 0f;
        lastHintSeconds = 0f;
        secondsSinceLastFetch = 0f;
        secondsSinceLiveCheck = 0f;
        liveCheckForced = false;
        endedDetected = false;
        ClearTracking();

        if (!silent)
        {
            OnStatusChanged?.Invoke("stopped");
            Logger.Info("YouTube chat polling stopped", "YouTubeChatManager");
        }
    }

    // FixedUpdateCaller から毎 fixed update で呼ばれる。
    public static void Tick(float deltaTime)
    {
        // 起動後初回かつ option=ON かつ保存URLがあれば自動再開（process あたり 1 回のみ）。
        if (!autoResumeAttempted && YouTubeChatOptions.Enabled != null && YouTubeChatOptions.Enabled.GetBool())
        {
            autoResumeAttempted = true;
            string saved = Main.YouTubeStreamUrl?.Value;
            if (!string.IsNullOrWhiteSpace(saved))
            {
                string err = Start(saved);
                if (err == null)
                {
                    YouTubeChatOverlay.EnsureSubscribed();
                    Logger.Info($"YouTube chat auto-resumed for saved URL", "YouTubeChatManager");
                }
            }
        }

        HandleEndedAndTracking(deltaTime);
        MaybeAutoDetect(deltaTime);
        FlushNotices();

        if (!IsActive || fetcher == null || fetchInFlight) return;

        secondsSinceLiveCheck += deltaTime;

        // 毎 tick option を取得して反映。option 値と server hint の最大値を待ち時間とする
        // （hint は monotonically grow させずに直近値で再評価）。
        int optInterval = YouTubeChatOptions.PollingInterval?.GetInt() ?? 5;
        float currentInterval = Math.Max(optInterval, lastHintSeconds);

        secondsSinceLastFetch += deltaTime;
        float requiredWait = currentInterval + backoffSeconds;
        if (secondsSinceLastFetch < requiredWait) return;

        secondsSinceLastFetch = 0f;
        fetchInFlight = true;

        bool checkLive = liveCheckForced || secondsSinceLiveCheck >= LiveCheckIntervalSeconds;
        if (checkLive)
        {
            liveCheckForced = false;
            secondsSinceLiveCheck = 0f;
        }

        var fetcherSnapshot = fetcher;
        Task.Run(async () =>
        {
            if (checkLive && await fetcherSnapshot.CheckLiveStateAsync() == LiveState.Ended)
            {
                if (IsActive && fetcher == fetcherSnapshot) endedDetected = true;
                fetchInFlight = false;
                return;
            }

            FetchResult result = await fetcherSnapshot.FetchAsync();
            // Unity main-thread 同期が無いので、event purchaser 側が thread-safe であること。
            // UI 側 (TMP 操作) は LateTask で main thread に戻す前提。
            HandleResult(result, fetcherSnapshot);
        });
    }

    private static void HandleResult(FetchResult result, YouTubeChatFetcher snapshot)
    {
        try
        {
            // Stop() が間に挟まったら無視
            if (!IsActive || fetcher != snapshot) return;

            if (!result.Success)
            {
                consecutiveFailures++;
                if (result.Error == FetchError.NotFound) consecutiveNotFound++;

                // 終了済みの枠は live_chat が「チャットは無効」になり準備段階で失敗し続ける。次回は枠の状態を先に確かめる。
                if (result.Error == FetchError.InitFailed) liveCheckForced = true;

                // 配信終了/未開始の 404 が3連続したら自動停止＋保存URL消去（無限ループ防止）。
                if (consecutiveNotFound >= 3)
                {
                    Logger.Warn("Auto-stop: stream not found 3 times in a row (likely ended)", "YouTubeChatManager");
                    Stop();
                    Main.YouTubeStreamUrl.Value = "";
                    OnStatusChanged?.Invoke("auto_stopped_not_found");
                    return;
                }

                backoffSeconds = result.Error switch
                {
                    FetchError.RateLimited => MinBackoff(consecutiveFailures, 30f, 60f, 300f),
                    FetchError.Forbidden => MinBackoff(consecutiveFailures, 60f, 180f, 600f),
                    FetchError.NotFound => 600f, // 配信終了/未開始系。10 分後に再試行（3回まで）
                    _ => MinBackoff(consecutiveFailures, 5f, 15f, 60f)
                };

                Logger.Warn($"Fetch failed: {result.Error}, backoff={backoffSeconds}s, fails={consecutiveFailures}", "YouTubeChatManager");
                OnStatusChanged?.Invoke($"error:{result.Error}");
                return;
            }

            consecutiveFailures = 0;
            consecutiveNotFound = 0;
            backoffSeconds = 0f;

            // pollingIntervalMillis を尊重: server hint を保持して Tick 側で option 値と max を取る。
            int? hint = snapshot.PollingIntervalMillis;
            if (hint is > 0) lastHintSeconds = hint.Value / 1000f;

            // 新着ゼロが延々続く無音停止 (continuation 更新失敗など) を後からログで切り分けられるよう、新着があった時だけ件数を残す。
            if (result.Messages.Count > 0) Logger.Info($"Fetched {result.Messages.Count} new chat message(s)", "YouTubeChatManager");

            foreach (var msg in result.Messages)
            {
                try { OnMessage?.Invoke(msg.Author, msg.Text); }
                catch (Exception ex) { Logger.Exception(ex, "YouTubeChatManager.OnMessage"); }
            }
        }
        finally
        {
            fetchInFlight = false;
        }
    }

    // 枠の終了を受けて取得を止め、OAuth 認証済みなら配信中の新しい枠を探して自動で乗り換える。
    // 見つからない/未認証ならホストに新しい URL での /yt を促す。
    private static void HandleEndedAndTracking(float deltaTime)
    {
        if (endedDetected)
        {
            endedDetected = false;
            string endedId = CurrentVideoId;
            Logger.Warn($"Stream {endedId} has ended (broadcast closed); chat polling stopped", "YouTubeChatManager");

            Stop(silent: true);
            Main.YouTubeStreamUrl.Value = "";
            OnStatusChanged?.Invoke("stream_ended");

            if (YouTubeChatPoster.IsConfigured)
            {
                trackingEndedVideoId = endedId;
                trackingAttempts = 0;
                secondsUntilNextTrack = 0f;
                Notify(string.Format(Translator.GetString("YouTubeChat.StreamEnded.Searching"), endedId));
            }
            else
                Notify(string.Format(Translator.GetString("YouTubeChat.StreamEnded.Stopped"), endedId));
        }

        if (string.IsNullOrEmpty(trackingEndedVideoId)) return;

        if (trackResultReady)
        {
            trackResultReady = false;
            trackInFlight = false;
            string newId = trackResultVideoId;

            if (!string.IsNullOrEmpty(newId) && newId != trackingEndedVideoId)
            {
                string url = "https://youtube.com/live/" + newId;
                if (Start(url) == null)
                {
                    Main.YouTubeStreamUrl.Value = url;
                    YouTubeChatOverlay.EnsureSubscribed();
                    Logger.Info($"Followed the stream to new video {newId}", "YouTubeChatManager");
                    Notify(string.Format(Translator.GetString("YouTubeChat.StreamEnded.Switched"), newId));
                }

                return;
            }

            if (trackingAttempts >= TrackMaxAttempts)
            {
                Logger.Warn("No active broadcast found after the stream ended; giving up", "YouTubeChatManager");
                Notify(Translator.GetString("YouTubeChat.StreamEnded.NotFound"));
                ClearTracking();
                return;
            }

            secondsUntilNextTrack = TrackRetrySeconds;
        }

        if (trackInFlight) return;

        secondsUntilNextTrack -= deltaTime;
        if (secondsUntilNextTrack > 0f) return;

        trackInFlight = true;
        trackingAttempts++;
        int generation = trackGeneration;
        YouTubeChatPoster.DetectStream((found, _, _) =>
        {
            // /yt や停止で追跡が打ち切られた後に届いた結果は捨てる
            if (generation != trackGeneration) return;
            trackResultVideoId = found ? YouTubeChatPoster.AutoDetectedVideoId : null;
            trackResultReady = true;
        });
    }

    private static void MaybeAutoDetect(float deltaTime)
    {
        if (autoDetectResultReady)
        {
            autoDetectResultReady = false;
            autoDetectInFlight = false;
            string id = autoDetectResultVideoId;

            // 結果待ちの間に /yt・追跡・停止が挟まっていたら使わない
            if (string.IsNullOrEmpty(id) || IsActive || AutoDetectSuppressed || !string.IsNullOrEmpty(trackingEndedVideoId)) return;

            string url = "https://youtube.com/live/" + id;
            if (Start(url) == null)
            {
                Main.YouTubeStreamUrl.Value = url;
                YouTubeChatOverlay.EnsureSubscribed();
                Logger.Info($"Auto-detected live stream {id}; chat polling started", "YouTubeChatManager");
                Notify(string.Format(Translator.GetString("YouTubeChat.AutoStarted"), id));
            }

            return;
        }

        if (autoDetectInFlight || IsActive || AutoDetectSuppressed || !string.IsNullOrEmpty(trackingEndedVideoId)) return;
        if (OperatingSystem.IsAndroid()) return;
        if (YouTubeChatOptions.Enabled == null || !YouTubeChatOptions.Enabled.GetBool()) return;
        if (!YouTubeChatPoster.IsConfigured) return;

        secondsUntilAutoDetect -= deltaTime;
        if (secondsUntilAutoDetect > 0f) return;

        secondsUntilAutoDetect = AutoDetectIntervalSeconds;
        autoDetectInFlight = true;
        YouTubeChatPoster.DetectStream((found, _, _) =>
        {
            autoDetectResultVideoId = found ? YouTubeChatPoster.AutoDetectedVideoId : null;
            autoDetectResultReady = true;
        });
    }

    private static void ClearTracking()
    {
        trackingEndedVideoId = "";
        trackingAttempts = 0;
        secondsUntilNextTrack = 0f;
        trackInFlight = false;
        trackResultReady = false;
        trackResultVideoId = null;
        trackGeneration++;
    }

    private static void Notify(string text) => pendingNotices.Add(text);

    // ホストのチャットへ出せる状態 (ロビー/ゲーム内) になるまで溜めておく。
    private static void FlushNotices()
    {
        if (pendingNotices.Count == 0) return;
        if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost || PlayerControl.LocalPlayer == null) return;

        foreach (string text in pendingNotices)
        {
            try { Utils.SendMessage(text, PlayerControl.LocalPlayer.PlayerId); }
            catch (Exception e) { Logger.Warn($"Notice failed: {e.Message}", "YouTubeChatManager"); }
        }

        pendingNotices.Clear();
    }

    private static float MinBackoff(int fails, float a, float b, float c)
    {
        return fails switch { 1 => a, 2 => b, _ => c };
    }

    // https://youtu.be/<id>, https://youtube.com/live/<id>,
    // https://www.youtube.com/watch?v=<id> の3形式に対応。
    private static readonly Regex YoutuBeRegex = new(@"youtu\.be/([A-Za-z0-9_-]{11})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LiveRegex = new(@"youtube\.com/live/([A-Za-z0-9_-]{11})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WatchRegex = new(@"[?&]v=([A-Za-z0-9_-]{11})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BareIdRegex = new(@"^[A-Za-z0-9_-]{11}$", RegexOptions.Compiled);

    public static string ExtractVideoId(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        url = url.Trim();

        if (BareIdRegex.IsMatch(url)) return url;
        Match m;
        if ((m = YoutuBeRegex.Match(url)).Success) return m.Groups[1].Value;
        if ((m = LiveRegex.Match(url)).Success) return m.Groups[1].Value;
        if ((m = WatchRegex.Match(url)).Success) return m.Groups[1].Value;
        return null;
    }
}
