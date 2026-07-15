using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using InnerNet;

namespace EndKnot.Modules.YouTubeChat;

// YouTube ライブチャットへの自動投稿層。完全ホストローカル・HTTP のみで完結し、
// ゲーム内 RPC は一切増やさない。
//
// 認証: Main.cs の Config.Bind から読む3値 (ClientId/ClientSecret/RefreshToken)。
// DLL には焼き込まず、3つとも揃わない限り常に no-op。取得用のヘルパーは別途用意される前提。
//
// 投稿内容は4種:
//   - 参加方法 + ロビーコード (ロビー中のみ、ローテ対象)
//   - あと何人で開始 (ロビー中のみ、ローテ対象)
//   - 配信趣旨ローテ (いつでも、cfg の複数行テキストを順番に、ローテ対象)
//   - !コマンド実況 (イベント駆動。AnnounceIntervention 経由、interval 無関係)
//
// Tick は FixedUpdateCaller から毎 fixed update で呼ばれる（YouTubeChatManager と同じ駆動）。
// HTTP 呼び出しは Task.Run でメインスレッドから切り離す。バックグラウンド側は Unity API に
// 触れない。共有 static 状態 (token/liveChatId キャッシュ) への多重書き込みは、Dispatch での
// Interlocked 原子確保 (postGate) + 連投下限で単一 in-flight に絞ることで防ぐ。
public static class YouTubeChatPoster
{
    private static readonly HttpClient client = new() { Timeout = TimeSpan.FromSeconds(15) };

    private static string accessToken;
    private static DateTime accessTokenExpiryUtc = DateTime.MinValue;

    private static string cachedLiveChatId;
    private static string cachedLiveChatIdVideoId;

    // 0 = 空き, 1 = 投稿中。bool の check-then-set は複数スレッドから同時に通過してしまう
    // (実機で1回の投稿指示に対し12重発火した)。Interlocked で原子的に確保する。
    private static int postGate;
    private static long lastPostTs;              // 直近投稿の Utils.TimeStamp (連投下限用)
    private const int MinPostGapSeconds = 3;     // これ未満の間隔での連投は捨てる (バースト防止)
    private static float secondsSinceLastPost;
    private static int rotationCursor;
    private static int messageLineCursor;

    private static bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Main.YouTubePostClientId?.Value) &&
        !string.IsNullOrWhiteSpace(Main.YouTubePostClientSecret?.Value) &&
        !string.IsNullOrWhiteSpace(Main.YouTubePostRefreshToken?.Value);

    public static void Tick(float deltaTime)
    {
        if (OperatingSystem.IsAndroid()) return;
        if (YouTubePostOptions.Enabled == null || !YouTubePostOptions.Enabled.GetBool()) return;

        // オンにした直後にホストへ丁寧なセットアップ説明を1回だけ出す（トークン未設定でも出す＝
        // 一番説明が要るタイミング）。config フラグで消費し再表示しない。
        MaybeShowSetupGuide();

        if (!IsConfigured) return;
        if (postGate != 0) return;

        string videoId = ResolveVideoId();
        if (string.IsNullOrEmpty(videoId)) return;

        secondsSinceLastPost += deltaTime;
        float interval = YouTubePostOptions.Interval?.GetInt() ?? 120;
        if (secondsSinceLastPost < interval) return;

        string text = BuildNextRotationMessage();
        if (string.IsNullOrEmpty(text)) return; // 今は有効な内容が無い(全トグルOFF/メッセージ空など)

        secondsSinceLastPost = 0f;
        Dispatch(videoId, text);
    }

    // /ytpost <text> からの手動投稿・疎通テスト用。
    public static void PostRaw(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        if (!IsConfigured)
        {
            Logger.Warn("PostRaw skipped: OAuth credentials not configured", "YouTubeChatPoster");
            return;
        }

        if (postGate != 0)
        {
            Logger.Warn("PostRaw skipped: another post in flight", "YouTubeChatPoster");
            return;
        }

        string videoId = ResolveVideoId();
        if (string.IsNullOrEmpty(videoId))
        {
            Logger.Warn("PostRaw skipped: no stream set (/yt <url> or set YouTubeStreamUrl first)", "YouTubeChatPoster");
            return;
        }

        Dispatch(videoId, text);
    }

    // !コマンド実況。AudienceManager の intervention 実行確定点から呼ばれる。interval 無関係。
    // kindKey は InterventionKind の enum 名 ("Earthquake" 等)。cutscene と同じ lang キー
    // ("Audience.Cutscene.<kindKey>") でローカライズ名 (大地震 等) に変換してから投稿する
    // (日本語チャットに生の英語名を出さないため)。
    public static void AnnounceIntervention(string author, string kindKey)
    {
        if (YouTubePostOptions.Enabled == null || !YouTubePostOptions.Enabled.GetBool()) return;
        if (YouTubePostOptions.InterventionAnnounceEnabled?.GetBool() != true) return;
        if (!IsConfigured || postGate != 0) return;

        string videoId = ResolveVideoId();
        if (string.IsNullOrEmpty(videoId)) return;

        string kindName = Translator.GetString($"Audience.Cutscene.{kindKey}");
        string text = string.Format(Translator.GetString("YouTubePost.InterventionAnnounce"), author, kindName);
        Dispatch(videoId, text);
    }

    // 視聴者が !コマンドを打ち間違えたとき (引数なし / 対象不在) に使い方を1行返す。
    // AudienceManager のパース失敗点から呼ばれる。放っておくと視聴者には「打っても無反応」に
    // しか見えず、機能不全と誤解されて離脱する (2026-07-14 配信で実害を確認)。
    //
    // API クォータ (1投稿=50ユニット/既定枠1万=200投稿/日) を守るため、種別ごとに
    // クールダウンを敷く。複数の視聴者が同じ打ち間違いを連発しても1回しか返さない。
    // ポイント不足は意図的に対象外 (発生頻度が読めずクォータを食い潰すため)。
    // キーは "usage:/notfound:" × Curse/Bless/Voice の最大5種で固定なので増え続けない。
    // ゲーム跨ぎのリセットも不要 (PlayerId キーではないので別人に誤爆しない。次ゲームへ
    // 持ち越しても最大60秒ヒントが1回抑制されるだけ)。
    private static readonly Dictionary<string, long> UsageHintCooldownUntil = [];
    private const int UsageHintCooldownSeconds = 60;

    public static void AnnounceUsageHint(string author, string kindKey)
    {
        // 使い方は既存のコマンド表記キー ("!祝福 <名前>") をそのまま流用する
        string usage = Translator.GetString($"AudienceCmd{kindKey}");
        PostHint($"usage:{kindKey}", "YouTubePost.UsageHint", author, usage);
    }

    // nameQuery は視聴者の生入力。これを**ホストのアカウント名義で**ライブチャットへ再投稿するので、
    // 長文スパム/暴言をそのまま垂れ流さないよう短くクランプする (プレイヤー名の照合用なので短くて足りる)。
    private const int EchoedQueryMaxLength = 20;

    public static void AnnounceTargetNotFound(string author, string kindKey, string nameQuery)
    {
        string safe = nameQuery.Length > EchoedQueryMaxLength
            ? nameQuery[..EchoedQueryMaxLength] + "…"
            : nameQuery;
        PostHint($"notfound:{kindKey}", "YouTubePost.TargetNotFound", author, safe);
    }

    private static void PostHint(string cooldownKey, string langKey, string author, string arg)
    {
        if (YouTubePostOptions.Enabled == null || !YouTubePostOptions.Enabled.GetBool()) return;
        if (YouTubePostOptions.InterventionAnnounceEnabled?.GetBool() != true) return;
        if (!IsConfigured || postGate != 0) return;

        long now = Utils.TimeStamp;
        if (UsageHintCooldownUntil.TryGetValue(cooldownKey, out long until) && now < until) return;

        string videoId = ResolveVideoId();
        if (string.IsNullOrEmpty(videoId)) return;

        // クールダウンは実際に投稿できたときだけ焼く。連投下限で捨てられた分まで数えると、
        // ヒントが無言で消えたうえ 60 秒沈黙する (直そうとしている症状そのものになる)。
        if (Dispatch(videoId, string.Format(Translator.GetString(langKey), author, arg)))
            UsageHintCooldownUntil[cooldownKey] = now + UsageHintCooldownSeconds;
    }

    // オンにした最初のロビー内 tick で、ホストのチャットへセットアップ手順を1回だけ送る。
    // /yt 初回警告 (ChatCommandPatch) と同じ Utils.SendMessage + config フラグ方式。
    private static void MaybeShowSetupGuide()
    {
        if (Main.YouTubePostExplained == null || Main.YouTubePostExplained.Value) return;
        if (!GameStates.IsLobby) return; // チャットが使えるロビーでのみ
        if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost || PlayerControl.LocalPlayer == null) return;

        Main.YouTubePostExplained.Value = true;
        Utils.SendMessage(Translator.GetString("YouTubePost.SetupGuide"), PlayerControl.LocalPlayer.PlayerId);
    }

    // 投稿先の videoId を解決する。/yt 実行中の読み取りセッションを優先し、無ければ
    // 保存済み配信 URL (Main.YouTubeStreamUrl) から解決する。両方無ければ null (= no-op)。
    private static string ResolveVideoId()
    {
        string id = YouTubeChatManager.CurrentVideoId;
        if (!string.IsNullOrEmpty(id)) return id;
        return YouTubeChatManager.ExtractVideoId(Main.YouTubeStreamUrl?.Value);
    }

    private static string BuildNextRotationMessage()
    {
        // 会議中/試合中に募集系(参加方法・人数)を出すべきでない。ロビー限定。
        bool isLobby = GameStates.IsLobby;

        var candidates = new List<Func<string>>();

        if (isLobby && YouTubePostOptions.WelcomeEnabled?.GetBool() == true)
            candidates.Add(BuildWelcomeMessage);

        if (isLobby && YouTubePostOptions.CountdownEnabled?.GetBool() == true)
            candidates.Add(BuildCountdownMessage);

        if (YouTubePostOptions.RotationEnabled?.GetBool() == true)
            candidates.Add(BuildRotationTextMessage);

        if (candidates.Count == 0) return null;

        // 先に読んでからインクリメント（初回に candidates[0] を飛ばさない）。
        int index = rotationCursor % candidates.Count;
        rotationCursor++;
        return candidates[index]();
    }

    private static string BuildWelcomeMessage()
    {
        string code = GameCode.IntToGameName(AmongUsClient.Instance.GameId);
        if (string.IsNullOrEmpty(code)) return null;
        return string.Format(Translator.GetString("YouTubePost.Welcome"), code);
    }

    private static string BuildCountdownMessage()
    {
        if (!GameStartManager.InstanceExists) return null;
        if (Options.PlayerAutoStart == null) return null;
        int current = GameData.Instance != null ? GameData.Instance.PlayerCount : 0;
        // しきい値は EHR 側の自動開始オプションを見る。バニラの GameStartManager.MinPlayers は
        // GameStartManagerPatch が毎フレーム 1 に固定している (ホストが常に開始できるように) ため、
        // 募集文言のしきい値としては使えず「現在5/1人」のような無意味な表示になる。
        int needed = Options.PlayerAutoStart.GetInt();
        return string.Format(Translator.GetString("YouTubePost.Countdown"), current, needed);
    }

    private static string BuildRotationTextMessage()
    {
        string raw = Main.YouTubePostMessages?.Value;
        if (string.IsNullOrWhiteSpace(raw)) return null;

        string[] lines = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0) return null;

        string line = lines[messageLineCursor % lines.Length];
        messageLineCursor++;
        return line;
    }

    // 戻り値: 実際に投稿へ送り出せたか (連投下限や in-flight で捨てた場合は false)。
    // 呼び出し側がクールダウンを焼くかどうかの判断に使う。
    private static bool Dispatch(string videoId, string text)
    {
        long now = Utils.TimeStamp;
        if (now - lastPostTs < MinPostGapSeconds) return false; // 連投下限: バースト・重複投稿を捨てる
        // 原子的に投稿スロットを確保。既に投稿中なら諦める (レースで複数通過させない)。
        if (System.Threading.Interlocked.CompareExchange(ref postGate, 1, 0) != 0) return false;
        lastPostTs = now;

        Task.Run(async () =>
        {
            try
            {
                bool ok = await PostAsync(videoId, text);
                if (!ok) Logger.Warn("YouTube chat post failed", "YouTubeChatPoster");
            }
            catch (Exception ex)
            {
                Logger.Warn($"Dispatch failed: {ex.Message}", "YouTubeChatPoster");
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref postGate, 0);
            }
        });

        return true;
    }

    private static async Task<bool> PostAsync(string videoId, string text)
    {
        string token = await EnsureAccessTokenAsync();
        if (token == null) return false;

        string liveChatId = await ResolveLiveChatIdAsync(videoId, token);
        if (liveChatId == null) return false;

        var body = new JsonObject
        {
            ["snippet"] = new JsonObject
            {
                ["liveChatId"] = liveChatId,
                ["type"] = "textMessageEvent",
                ["textMessageDetails"] = new JsonObject { ["messageText"] = text }
            }
        };

        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        // liveChatMessages リソースの HTTP パスは liveChat/messages (スラッシュ入り)。
        // liveChatMessages に投げると存在しないパス → 本文空の404 になる。
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://www.googleapis.com/youtube/v3/liveChat/messages?part=snippet") { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            string err = await response.Content.ReadAsStringAsync();
            Logger.Warn($"liveChatMessages POST failed: {(int)response.StatusCode} {response.StatusCode} reason='{response.ReasonPhrase}' liveChatId={liveChatId} body='{err}'", "YouTubeChatPoster");
            // 空本文の 404 は「認可アカウントに YouTube チャンネルが無い / ブランドアカウント選択ミス」で
            // 起きうる。切り分けのため、そのアカウントのチャンネルを一度だけ問い合わせて記録する。
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                await LogAuthorizedChannelAsync(token);
            return false;
        }

        return true;
    }

    // 診断用: 認可アカウントに紐づく YouTube チャンネル(id/名前)を記録する。
    // items が空なら「このアカウントにチャンネルが無い」= 投稿できない真因の可能性が高い。
    private static async Task LogAuthorizedChannelAsync(string token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://www.googleapis.com/youtube/v3/channels?part=id,snippet&mine=true");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request);
            string body = await response.Content.ReadAsStringAsync();
            Logger.Warn($"[diag] channels.list mine=true -> {(int)response.StatusCode}: {body}", "YouTubeChatPoster");
        }
        catch (Exception ex)
        {
            Logger.Warn($"[diag] channels.list failed: {ex.Message}", "YouTubeChatPoster");
        }
    }

    private static async Task<string> EnsureAccessTokenAsync()
    {
        if (accessToken != null && DateTime.UtcNow < accessTokenExpiryUtc) return accessToken;

        try
        {
            var form = new Dictionary<string, string>
            {
                ["client_id"] = Main.YouTubePostClientId.Value,
                ["client_secret"] = Main.YouTubePostClientSecret.Value,
                ["refresh_token"] = Main.YouTubePostRefreshToken.Value,
                ["grant_type"] = "refresh_token"
            };

            using var content = new FormUrlEncodedContent(form);
            using var response = await client.PostAsync("https://oauth2.googleapis.com/token", content);
            string body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Logger.Warn($"Token refresh failed: {response.StatusCode} {body}", "YouTubeChatPoster");
                return null;
            }

            var node = JsonNode.Parse(body);
            string token = node?["access_token"]?.ToString();
            if (string.IsNullOrEmpty(token)) return null;

            int expiresIn = node["expires_in"] != null ? node["expires_in"].GetValue<int>() : 3600;

            accessToken = token;
            // 期限前に余裕を持って再交換 (60秒バッファ)。
            accessTokenExpiryUtc = DateTime.UtcNow.AddSeconds(Math.Max(60, expiresIn - 60));
            return accessToken;
        }
        catch (Exception ex)
        {
            Logger.Warn($"EnsureAccessTokenAsync failed: {ex.Message}", "YouTubeChatPoster");
            return null;
        }
    }

    private static async Task<string> ResolveLiveChatIdAsync(string videoId, string token)
    {
        if (cachedLiveChatId != null && cachedLiveChatIdVideoId == videoId) return cachedLiveChatId;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://www.googleapis.com/youtube/v3/videos?part=liveStreamingDetails&id={videoId}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await client.SendAsync(request);
            string body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Logger.Warn($"videos.list failed: {response.StatusCode} {body}", "YouTubeChatPoster");
                return null;
            }

            var node = JsonNode.Parse(body);
            var items = node?["items"]?.AsArray();
            if (items == null || items.Count == 0)
            {
                // 空配列に [0] すると IndexOutOfRange。videoId が誤り/配信終了/非公開など。
                Logger.Warn($"videos.list returned no items for videoId={videoId} (wrong id / ended / private?)", "YouTubeChatPoster");
                return null;
            }

            string liveChatId = items[0]?["liveStreamingDetails"]?["activeLiveChatId"]?.ToString();
            if (string.IsNullOrEmpty(liveChatId))
            {
                Logger.Warn($"activeLiveChatId missing for videoId={videoId} (chat disabled / not live yet?)", "YouTubeChatPoster");
                return null;
            }

            Logger.Info($"Resolved liveChatId for videoId={videoId}: {liveChatId}", "YouTubeChatPoster");
            cachedLiveChatId = liveChatId;
            cachedLiveChatIdVideoId = videoId;
            return liveChatId;
        }
        catch (Exception ex)
        {
            Logger.Warn($"ResolveLiveChatIdAsync failed: {ex.Message}", "YouTubeChatPoster");
            return null;
        }
    }
}
