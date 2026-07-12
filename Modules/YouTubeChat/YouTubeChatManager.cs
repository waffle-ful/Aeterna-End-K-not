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


    // /yt <url> から呼ばれる。失敗時は理由文字列を返す（成功時 null）。
    public static string Start(string url)
    {
        if (OperatingSystem.IsAndroid()) return "android_unsupported";
        if (string.IsNullOrWhiteSpace(url)) return "empty_url";

        string videoId = ExtractVideoId(url);
        if (string.IsNullOrEmpty(videoId)) return "invalid_url";

        Stop(silent: true);

        CurrentUrl = url.Trim();
        CurrentVideoId = videoId;
        fetcher = new YouTubeChatFetcher(videoId);
        consecutiveFailures = 0;
        consecutiveNotFound = 0;
        backoffSeconds = 0f;
        lastHintSeconds = 0f;
        secondsSinceLastFetch = 0f;
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

        if (!IsActive || fetcher == null || fetchInFlight) return;

        // 毎 tick option を取得して反映。option 値と server hint の最大値を待ち時間とする
        // （hint は monotonically grow させずに直近値で再評価）。
        int optInterval = YouTubeChatOptions.PollingInterval?.GetInt() ?? 5;
        float currentInterval = Math.Max(optInterval, lastHintSeconds);

        secondsSinceLastFetch += deltaTime;
        float requiredWait = currentInterval + backoffSeconds;
        if (secondsSinceLastFetch < requiredWait) return;

        secondsSinceLastFetch = 0f;
        fetchInFlight = true;

        var fetcherSnapshot = fetcher;
        Task.Run(async () =>
        {
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
