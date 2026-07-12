using System.Linq;
using System.Text.Json.Nodes;

namespace EndKnot.Modules.YouTubeChat;

// YouTube 内部 endpoint (/youtubei/v1/live_chat/get_live_chat) の POST body 構築と
// continuation トークン更新を担当。TOHK の Modules/Streamer/ChatData.cs を起源とする。
internal class ChatPayload
{
    public readonly string Key;
    public string Continuation { get; private set; }
    public readonly string VisitorData;
    public readonly string ClientVersion;

    public int? PollingIntervalMillis { get; private set; }

    private bool warnedUnknownContinuation;

    public ChatPayload(string key, string continuation, string visitorData, string clientVersion)
    {
        Key = key;
        Continuation = continuation;
        VisitorData = visitorData;
        ClientVersion = clientVersion;
    }

    public void UpdateContinuation(string postResult)
    {
        var node = JsonNode.Parse(postResult);
        var continuations = node?["continuationContents"]?["liveChatContinuation"]?["continuations"]?.AsArray();
        if (continuations == null) return;

        // continuation が更新されないと以降のフェッチが同じ応答を返し続け、dedup で新着ゼロの無音停止になる。
        // 既知3型を全要素から探し、どれにも当たらなければ一度だけ実型名を警告して次回調査の手がかりを残す。
        foreach (var contRoot in continuations)
        {
            if (contRoot == null) continue;

            var contNode = contRoot["invalidationContinuationData"] ?? contRoot["timedContinuationData"] ?? contRoot["reloadContinuationData"];
            if (contNode == null) continue;

            var newCont = contNode["continuation"]?.ToString();
            if (!string.IsNullOrEmpty(newCont)) Continuation = newCont;

            // YouTube 側から「これ以下で叩くな」と返ってくる timeoutMs を尊重する。
            var timeoutMs = contNode["timeoutMs"]?.GetValue<int?>();
            if (timeoutMs is > 0) PollingIntervalMillis = timeoutMs;
            return;
        }

        if (!warnedUnknownContinuation)
        {
            warnedUnknownContinuation = true;
            string kinds = string.Join(",", continuations.Select(c => c is JsonObject obj ? string.Join("/", obj.Select(kvp => kvp.Key)) : "null"));
            Logger.Warn($"UpdateContinuation: no known continuation type in response (found: {kinds})", "YouTubeChatFetcher");
        }
    }

    public string Build()
    {
        return $"{{\"context\":{{\"client\":{{\"visitorData\":\"{VisitorData}\",\"clientName\":\"WEB\",\"clientVersion\":\"{ClientVersion}\"}}}},\"continuation\":\"{Continuation}\"}}";
    }
}
