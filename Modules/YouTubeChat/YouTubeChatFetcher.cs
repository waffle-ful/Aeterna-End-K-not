using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace EndKnot.Modules.YouTubeChat;

// YouTube ライブチャット取得層。TOHK の Modules/Streamer/Readcomment.cs から
// HTTP/JSON ロジックのみを抜き出し、ホワイトリスト/HopeInfo 連動は完全に削除。
//
// 1) FirstFetch: https://www.youtube.com/live_chat?v=<id> の HTML から
//    INNERTUBE_API_KEY / continuation / visitorData / clientVersion を Regex 抽出
// 2) FetchOnce: /youtubei/v1/live_chat/get_live_chat?key=... に POST してコメント JSON 取得
//
// HTTP 失敗 (429 / 403 / 404) は FetchResult.Failed で返し、Manager 側で backoff 制御。
internal sealed class YouTubeChatFetcher : IDisposable
{
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

    private static readonly Regex KeyRegex = new("\"INNERTUBE_API_KEY\":\"(.+?)\"", RegexOptions.Compiled);
    private static readonly Regex ContinuationRegex = new("\"continuation\":\"(.+?)\"", RegexOptions.Compiled);
    private static readonly Regex VisitorRegex = new("\"visitorData\":\"(.+?)\"", RegexOptions.Compiled);
    private static readonly Regex ClientVersionRegex = new("\"clientVersion\":\"(.+?)\"", RegexOptions.Compiled);
    private static readonly Regex BroadcastDetailsRegex = new("\"liveBroadcastDetails\":\\{[^}]*\\}", RegexOptions.Compiled);

    private readonly string videoId;
    private readonly HttpClient client;
    private ChatPayload payload;
    private readonly HashSet<string> seenIds = [];

    public int? PollingIntervalMillis => payload?.PollingIntervalMillis;

    public YouTubeChatFetcher(string videoId)
    {
        this.videoId = videoId;
        client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.Add("User-Agent", UserAgent);
    }

    public async Task<FetchResult> FetchAsync()
    {
        try
        {
            if (payload == null)
            {
                bool initialized = await InitPayloadAsync();
                if (!initialized) return FetchResult.Failed(FetchError.InitFailed);
            }

            using var content = new StringContent(payload.Build(), Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(
                "https://www.youtube.com/youtubei/v1/live_chat/get_live_chat?key=" + payload.Key,
                content);

            if (response.StatusCode == HttpStatusCode.TooManyRequests) return FetchResult.Failed(FetchError.RateLimited);
            if (response.StatusCode == HttpStatusCode.Forbidden) return FetchResult.Failed(FetchError.Forbidden);
            if (response.StatusCode == HttpStatusCode.NotFound) return FetchResult.Failed(FetchError.NotFound);
            if (!response.IsSuccessStatusCode) return FetchResult.Failed(FetchError.HttpError);

            string body = await response.Content.ReadAsStringAsync();
            var messages = ParseMessages(body);
            payload.UpdateContinuation(body);

            return FetchResult.Ok(messages);
        }
        catch (Exception ex)
        {
            Logger.Warn($"FetchAsync failed: {ex.Message}", "YouTubeChatFetcher");
            return FetchResult.Failed(FetchError.Exception);
        }
    }

    // 配信枠が今も配信中かを watch ページの liveBroadcastDetails で確かめる。
    // 回線の一瞬の切断などで YouTube が枠を閉じると、同じ URL は終了済みのアーカイブになり、
    // 取得は失敗ログも出さずに新着ゼロのまま止まる。終了時刻が付いていれば Ended と判定する。
    public async Task<LiveState> CheckLiveStateAsync()
    {
        try
        {
            using var response = await client.GetAsync("https://www.youtube.com/watch?v=" + videoId);
            if (!response.IsSuccessStatusCode) return LiveState.Unknown;

            string html = await response.Content.ReadAsStringAsync();
            Match m = BroadcastDetailsRegex.Match(html);
            if (!m.Success) return LiveState.Unknown;

            if (m.Value.Contains("\"isLiveNow\":true")) return LiveState.Live;
            if (m.Value.Contains("\"endTimestamp\"")) return LiveState.Ended;
            return LiveState.Unknown; // 開始前の待機枠など
        }
        catch (Exception ex)
        {
            Logger.Warn($"CheckLiveStateAsync failed: {ex.Message}", "YouTubeChatFetcher");
            return LiveState.Unknown;
        }
    }

    private async Task<bool> InitPayloadAsync()
    {
        try
        {
            using var response = await client.GetAsync("https://www.youtube.com/live_chat?v=" + videoId);
            if (!response.IsSuccessStatusCode) return false;

            string html = await response.Content.ReadAsStringAsync();

            Match keyM = KeyRegex.Match(html);
            Match contM = ContinuationRegex.Match(html);
            Match visM = VisitorRegex.Match(html);
            Match clientM = ClientVersionRegex.Match(html);

            if (!keyM.Success || !contM.Success || !visM.Success || !clientM.Success)
            {
                Logger.Warn("FirstFetch: required fields not found in live_chat HTML", "YouTubeChatFetcher");
                return false;
            }

            payload = new ChatPayload(
                keyM.Groups[1].Value,
                contM.Groups[1].Value,
                visM.Groups[1].Value,
                clientM.Groups[1].Value);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"InitPayloadAsync failed: {ex.Message}", "YouTubeChatFetcher");
            return false;
        }
    }

    private List<ChatMessage> ParseMessages(string body)
    {
        var result = new List<ChatMessage>();
        var node = JsonNode.Parse(body);
        var actions = node?["continuationContents"]?["liveChatContinuation"]?["actions"];
        if (actions == null) return result;

        foreach (var action in actions.AsArray())
        {
            var renderer = action?["addChatItemAction"]?["item"]?["liveChatTextMessageRenderer"];
            if (renderer == null) continue;

            string id = renderer["id"]?.ToString() ?? "";
            if (string.IsNullOrEmpty(id) || !seenIds.Add(id)) continue;

            string author = renderer["authorName"]?["simpleText"]?.ToString() ?? "";
            var runs = renderer["message"]?["runs"]?.AsArray();
            if (runs == null) continue;

            var sb = new StringBuilder();
            foreach (var run in runs)
            {
                var text = run?["text"]?.ToString();
                if (!string.IsNullOrEmpty(text)) sb.Append(text);
                else
                {
                    // emoji.shortcuts → ":smile:" 風に1個だけ拾う
                    var emoji = run?["emoji"]?["shortcuts"]?[0]?.ToString();
                    if (!string.IsNullOrEmpty(emoji)) sb.Append(emoji);
                }
            }

            string text2 = sb.ToString().Trim();
            if (text2.Length == 0) continue;

            result.Add(new ChatMessage(author, text2));
        }

        // メモリが膨れすぎないよう、古い id は適度に剥がす
        if (seenIds.Count > 2000) seenIds.Clear();

        return result;
    }

    public void Dispose() => client?.Dispose();
}

internal readonly record struct ChatMessage(string Author, string Text);

internal enum LiveState
{
    Unknown,
    Live,
    Ended
}

internal enum FetchError
{
    None,
    InitFailed,
    RateLimited,
    Forbidden,
    NotFound,
    HttpError,
    Exception
}

internal readonly struct FetchResult
{
    public readonly bool Success;
    public readonly FetchError Error;
    public readonly List<ChatMessage> Messages;

    private FetchResult(bool s, FetchError e, List<ChatMessage> m)
    {
        Success = s;
        Error = e;
        Messages = m;
    }

    public static FetchResult Ok(List<ChatMessage> m) => new(true, FetchError.None, m);
    public static FetchResult Failed(FetchError e) => new(false, e, null);
}
