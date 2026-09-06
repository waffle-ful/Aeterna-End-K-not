using System;
using System.Text.RegularExpressions;
using System.Threading;
using EndKnot.Modules.YouTubeChat;

namespace EndKnot.Modules.Setup;

// Google Cloud の 4 URL は Google 側の UI/URL 変更を 1 行で追従できるよう、ここに 1 箇所へまとめる。
internal static class GoogleConsoleLinks
{
    internal const string ProjectCreate = "https://console.cloud.google.com/projectcreate";
    internal const string EnableApi = "https://console.cloud.google.com/apis/library/youtube.googleapis.com";
    internal const string AuthOverview = "https://console.cloud.google.com/auth/overview";
    internal const string AuthAudience = "https://console.cloud.google.com/auth/audience";
    internal const string ClientCreate = "https://console.cloud.google.com/auth/clients/create";
}

// StreamSetupGUI の「YouTube 自動投稿」セクションが描くだけの状態を持つクラス。
// Modules/Setup/StreamSetupState.cs と同じ設計 (Dirty フラグは共有・ワーカーは言語非依存の状態だけを
// 書き込み、文言解決は StreamSetupGUI がメインスレッドで行う)。ここで発生するイベント (保存完了・
// device flow の進捗・検出結果・テスト投稿結果) は全て StreamSetupState.Dirty / ClipboardClearPending を
// 立てる側で、専用のフラグは別に持たない (消費し忘れの経路を増やさないため)。
internal static class StreamSetupYouTubeState
{
    internal enum ConnectState { Unknown, Requesting, WaitingForUser, Connected, Failed }
    internal enum DetectState { Unknown, Checking, Found, NotFound, TokenInvalid }
    internal enum TestPostState { Idle, Posting, Success, Failed }
    internal enum SaveErrorKind { None, IdInvalid, SecretInvalid }

    // ── Google Cloud の準備 (ClientId / ClientSecret) ──
    internal static volatile SaveErrorKind SaveError = SaveErrorKind.None;

    // ── YouTube との接続 (device flow) ──
    internal static volatile ConnectState Connect = ConnectState.Unknown;
    internal static volatile string DeviceUserCode = "";
    internal static volatile string DeviceVerificationUrl = "";
    internal static volatile string ConnectErrorCode = "";
    internal static volatile bool ConnectBusy;
    private static CancellationTokenSource _connectCts;

    // device flow のワーカースレッドが受け取った refresh token。ConfigEntry への書込みは
    // ConfigFile.Save を伴うためメインスレッド専用とし、StreamSetupGUI.Update() が
    // ClipboardClearPending と同じやり方でここを消費する。
    internal static volatile string PendingRefreshToken;

    // ── 投稿先の配信 ──
    internal static volatile DetectState Detect = DetectState.Unknown;
    internal static volatile string DetectedTitle = "";
    internal static volatile bool DetectBusy;

    // ── テスト投稿 ──
    internal static volatile TestPostState TestPost = TestPostState.Idle;
    internal static volatile string TestPostErrorKind = ""; // "401" / "403" / "404" / "busy" / "other"
    internal static volatile bool TestPostBusy;

    // long は volatile 修飾できない (CS0677) ため、Volatile.Read/Write で明示的にメモリ可視性を
    // 保証する (投稿ワーカースレッドの書込みと GUI 側の読み取りが別スレッド)。
    private static long _testPostCooldownUntil;
    internal const int TestPostCooldownSeconds = 60;

    // クライアント ID は数字-英小文字.apps.googleusercontent.com、シークレットは GOCSPX- 始まりの
    // Google 公式フォーマット。
    private static readonly Regex ClientIdPattern = new(@"^[0-9]+-[a-z0-9]+\.apps\.googleusercontent\.com$", RegexOptions.Compiled);
    private static readonly Regex ClientSecretPattern = new(@"^GOCSPX-[A-Za-z0-9_\-]{20,}$", RegexOptions.Compiled);

    internal static bool ClientIdSet => !string.IsNullOrWhiteSpace(Main.YouTubePostClientId?.Value);
    internal static bool ClientSecretSet => !string.IsNullOrWhiteSpace(Main.YouTubePostClientSecret?.Value);
    internal static bool RefreshTokenSet => !string.IsNullOrWhiteSpace(Main.YouTubePostRefreshToken?.Value);

    // ウィンドウを開いた時に呼ぶ。表示専用の一時状態 (device flow の進行中表示・エラー表示) をたたむ。
    internal static void Refresh()
    {
        // 進行中の処理 (Busy) はここでリセットしない。ウィンドウを閉じて開き直しただけで
        // device flow / 検出 / テスト投稿の表示が消えてしまうのを防ぐ (バックグラウンド側は動き続けている)。
        if (!ConnectBusy) Connect = RefreshTokenSet ? ConnectState.Connected : ConnectState.Unknown;
        SaveError = SaveErrorKind.None;
        if (!DetectBusy) Detect = DetectState.Unknown;
        if (!TestPostBusy) TestPost = TestPostState.Idle;
        StreamSetupState.Dirty = true;
    }

    // クリップボードの読取はメインスレッド (OnGUI) 側で行う。設定ファイルへの書込は
    // ConfigEntry の同期処理で軽量 (/yt が YouTubeStreamUrl.Value をメインスレッドで直接書くのと同じ)
    // なのでスレッドを分けない。
    internal static void SaveClientIdFromClipboard(string rawClipboard)
    {
        string candidate = rawClipboard?.Trim() ?? "";
        if (!ClientIdPattern.IsMatch(candidate))
        {
            SaveError = SaveErrorKind.IdInvalid;
            StreamSetupState.Dirty = true;
            return;
        }

        SaveError = SaveErrorKind.None;
        Main.YouTubePostClientId.Value = candidate;
        StreamSetupState.ClipboardClearPending = true;
        StreamSetupState.Dirty = true;
    }

    internal static void SaveClientSecretFromClipboard(string rawClipboard)
    {
        string candidate = rawClipboard?.Trim() ?? "";
        if (!ClientSecretPattern.IsMatch(candidate))
        {
            SaveError = SaveErrorKind.SecretInvalid;
            StreamSetupState.Dirty = true;
            return;
        }

        SaveError = SaveErrorKind.None;
        Main.YouTubePostClientSecret.Value = candidate;
        StreamSetupState.ClipboardClearPending = true;
        StreamSetupState.Dirty = true;
    }

    // 接続 (device flow) を開始する。ClientId/Secret が未保存なら呼ばない (GUI 側でボタンを隠す)。
    internal static void StartConnect()
    {
        if (ConnectBusy) return;
        if (!ClientIdSet || !ClientSecretSet) return;

        _connectCts?.Cancel();
        _connectCts = new CancellationTokenSource();
        CancellationToken ct = _connectCts.Token;

        ConnectBusy = true;
        Connect = ConnectState.Requesting;
        DeviceUserCode = "";
        DeviceVerificationUrl = "";
        ConnectErrorCode = "";
        StreamSetupState.Dirty = true;

        YouTubeOAuthDeviceFlow.Start(
            Main.YouTubePostClientId.Value,
            Main.YouTubePostClientSecret.Value,
            ct,
            onCodeReady: (code, url) =>
            {
                DeviceUserCode = code;
                DeviceVerificationUrl = url;
                Connect = ConnectState.WaitingForUser;
                StreamSetupState.Dirty = true;
            },
            onSuccess: refreshToken =>
            {
                // ConfigEntry への書込み (ConfigFile.Save を伴う) はメインスレッド専用。ここでは
                // まだ ConnectBusy を落とさず、PendingRefreshToken 経由で Update() に橋渡しするだけ
                // にとどめる。ConnectBusy=false と Connect=Connected は ConsumePendingRefreshToken
                // が書込み成功を確認してから立てる。
                DeviceUserCode = "";
                DeviceVerificationUrl = "";
                Logger.Info("YouTube OAuth device flow succeeded", "StreamSetupYouTubeState");
                PendingRefreshToken = refreshToken;
                StreamSetupState.Dirty = true;
            },
            onError: errorCode =>
            {
                ConnectErrorCode = errorCode;
                Connect = ConnectState.Failed;
                ConnectBusy = false;
                DeviceUserCode = "";
                DeviceVerificationUrl = "";
                StreamSetupState.Dirty = true;
            });
    }

    internal static void CancelConnect()
    {
        _connectCts?.Cancel();
        _connectCts = null;
    }

    // StreamSetupGUI.Update() からメインスレッドでのみ呼ぶ。PendingRefreshToken が無ければ何もしない。
    internal static void ConsumePendingRefreshToken()
    {
        string token = PendingRefreshToken;
        if (token == null) return;

        PendingRefreshToken = null;

        try
        {
            Main.YouTubePostRefreshToken.Value = token;
            Connect = ConnectState.Connected;
        }
        catch (Exception e)
        {
            Logger.Warn($"Failed to save refresh token: {e.Message}", "StreamSetupYouTubeState");
            ConnectErrorCode = "save_failed";
            Connect = ConnectState.Failed;
        }
        finally
        {
            ConnectBusy = false;
            StreamSetupState.Dirty = true;
        }
    }

    // 投稿先の手動検出ボタン。ロビー入室時の自動検出は YouTubeChatPoster 側に独立実装がある
    // (Modules/Setup が Modules/YouTubeChat へ依存する向きを保ち、逆依存を作らないため)。
    internal static void DetectStream()
    {
        if (DetectBusy) return;

        DetectBusy = true;
        Detect = DetectState.Checking;
        StreamSetupState.Dirty = true;

        YouTubeChatPoster.DetectStream((found, title, tokenInvalid) =>
        {
            Detect = tokenInvalid ? DetectState.TokenInvalid : found ? DetectState.Found : DetectState.NotFound;
            DetectedTitle = found ? SanitizeTitle(title) : "";
            DetectBusy = false;
            StreamSetupState.Dirty = true;
        });
    }

    // YouTube のタイトルはリッチテキスト記号やサロゲートペア (絵文字) を含みうる。TMP/GUI 表示前に
    // 無害化し、40 文字を超える場合はサロゲートペアの途中で切らないようにする。
    private static string SanitizeTitle(string title)
    {
        if (string.IsNullOrEmpty(title)) return "";

        string s = title.Replace("<", "＜").Replace("\n", " ").Replace("\r", "");
        if (s.Length <= 40) return s;

        int cut = 40;
        if (char.IsHighSurrogate(s[cut - 1])) cut--;
        return s[..cut] + "…";
    }

    internal static bool TestPostOnCooldown => Utils.TimeStamp < Volatile.Read(ref _testPostCooldownUntil);

    internal static int TestPostCooldownRemaining()
    {
        long remain = Volatile.Read(ref _testPostCooldownUntil) - Utils.TimeStamp;
        return remain > 0 ? (int)remain : 0;
    }

    // text はメインスレッド (GUI クリックハンドラ) で Translator.GetString 済みのものを渡すこと。
    // Translator は IL2CPP の TranslationController に触るためバックグラウンドスレッド不可。
    internal static void RunTestPost(string text)
    {
        if (TestPostBusy || TestPostOnCooldown) return;

        TestPostBusy = true;
        TestPost = TestPostState.Posting;
        TestPostErrorKind = "";
        StreamSetupState.Dirty = true;

        bool dispatched = YouTubeChatPoster.TestPost(text, (ok, status) =>
        {
            TestPostBusy = false;
            Volatile.Write(ref _testPostCooldownUntil, Utils.TimeStamp + TestPostCooldownSeconds);

            if (ok)
            {
                TestPost = TestPostState.Success;
            }
            else
            {
                TestPost = TestPostState.Failed;
                TestPostErrorKind = status switch
                {
                    401 => "401",
                    403 => "403",
                    404 => "404",
                    _ => "other"
                };
            }

            StreamSetupState.Dirty = true;
        });

        // 未設定/連投下限/in-flight で送信自体が起きなかった場合はクールダウンを焼かない
        // (焼くと「ボタンを押しても無反応のまま60秒待たされる」体験になる)。
        if (!dispatched)
        {
            TestPostBusy = false;
            TestPost = TestPostState.Failed;
            // 動画IDが解決できているのに送信されなかったのは postGate/連投下限が理由 (別の投稿が
            // 進行中) であって通信エラーではないので、専用の "busy" 種別で区別する。
            TestPostErrorKind = YouTubeChatPoster.HasResolvedVideoId ? "busy" : "404";
            StreamSetupState.Dirty = true;
        }
    }
}
