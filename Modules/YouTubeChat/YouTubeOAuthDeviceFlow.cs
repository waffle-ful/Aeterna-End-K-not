using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace EndKnot.Modules.YouTubeChat;

// OAuth device flow (RFC 8628) でホスト自身の Google アカウントから refresh token を取る。
// tools/youtube-token-helper.ps1 の手順をそのまま C# 化したもの。ホストローカルの HTTP のみで
// 完結し、ゲーム内 RPC は増えない。呼び出しは全てバックグラウンドスレッドで行い、進捗はコールバック
// 経由で呼び出し元 (Modules/Setup/StreamSetupYouTubeState.cs) の volatile フィールドへ書き戻す。
public static class YouTubeOAuthDeviceFlow
{
    // device flow が受け付けるのは youtube / youtube.readonly のみ。youtube.force-ssl を渡すと
    // invalid_scope で拒否される。liveChatMessages.insert への投稿は youtube スコープで足りる。
    private const string Scope = "https://www.googleapis.com/auth/youtube";
    private const string DeviceCodeUrl = "https://oauth2.googleapis.com/device/code";
    private const string TokenUrl = "https://oauth2.googleapis.com/token";

    private static readonly HttpClient client = new() { Timeout = TimeSpan.FromSeconds(15) };

    // token をキャンセルしたまま Task.Run に渡すと、既にキャンセル済みの場合に本体が一度も
    // 走らないまま破棄され、onError が呼ばれずに呼び出し元がビジー状態のまま固まる。
    // token は必ず RunAsync の中でだけ見る (Task.Run 自体には渡さない)。
    public static void Start(
        string clientId,
        string clientSecret,
        CancellationToken token,
        Action<string, string> onCodeReady, // (userCode, verificationUrl)
        Action<string> onSuccess,           // (refreshToken)
        Action<string> onError)             // (errorCode: "cancelled" / "expired" / "network" / "device_code_http_NNN" / 生の error 文字列)
    {
        Task.Run(() => RunAsync(clientId, clientSecret, token, onCodeReady, onSuccess, onError));
    }

    private static async Task RunAsync(
        string clientId,
        string clientSecret,
        CancellationToken token,
        Action<string, string> onCodeReady,
        Action<string> onSuccess,
        Action<string> onError)
    {
        try
        {
            var deviceForm = new Dictionary<string, string> { ["client_id"] = clientId, ["scope"] = Scope };
            using var deviceContent = new FormUrlEncodedContent(deviceForm);
            using var deviceResponse = await client.PostAsync(DeviceCodeUrl, deviceContent, token);
            string deviceBody = await deviceResponse.Content.ReadAsStringAsync(token);

            if (!deviceResponse.IsSuccessStatusCode)
            {
                onError?.Invoke($"device_code_http_{(int)deviceResponse.StatusCode}");
                return;
            }

            JsonNode deviceNode = JsonNode.Parse(deviceBody);
            string userCode = deviceNode?["user_code"]?.ToString();
            string verificationUrl = deviceNode?["verification_url"]?.ToString();
            string deviceCode = deviceNode?["device_code"]?.ToString();
            int interval = deviceNode?["interval"] != null ? deviceNode["interval"].GetValue<int>() : 5;
            if (interval < 5) interval = 5;
            int expiresIn = deviceNode?["expires_in"] != null ? deviceNode["expires_in"].GetValue<int>() : 1800;

            if (string.IsNullOrEmpty(userCode) || string.IsNullOrEmpty(deviceCode))
            {
                onError?.Invoke("device_code_missing_fields");
                return;
            }

            onCodeReady?.Invoke(userCode, verificationUrl);

            DateTime deadline = DateTime.UtcNow.AddSeconds(expiresIn);

            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(interval), token);

                var tokenForm = new Dictionary<string, string>
                {
                    ["client_id"] = clientId,
                    ["client_secret"] = clientSecret,
                    ["device_code"] = deviceCode,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
                };

                using var tokenContent = new FormUrlEncodedContent(tokenForm);
                using var tokenResponse = await client.PostAsync(TokenUrl, tokenContent, token);
                string tokenBody = await tokenResponse.Content.ReadAsStringAsync(token);

                if (tokenResponse.IsSuccessStatusCode)
                {
                    string refreshToken = JsonNode.Parse(tokenBody)?["refresh_token"]?.ToString();
                    if (string.IsNullOrEmpty(refreshToken))
                    {
                        onError?.Invoke("token_missing_refresh_token");
                        return;
                    }

                    onSuccess?.Invoke(refreshToken);
                    return;
                }

                string errorCode = TryExtractErrorCode(tokenBody);
                if (errorCode == "authorization_pending") continue;
                if (errorCode == "slow_down")
                {
                    interval += 5;
                    continue;
                }

                onError?.Invoke(string.IsNullOrEmpty(errorCode) ? $"token_http_{(int)tokenResponse.StatusCode}" : errorCode);
                return;
            }

            onError?.Invoke("expired");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            // token 自体はキャンセルされていないのに OperationCanceledException が飛ぶのは
            // HttpClient.Timeout (15秒) 到達のケース。これを「キャンセルしました」と誤表示しない。
            onError?.Invoke("network");
        }
        catch (OperationCanceledException)
        {
            onError?.Invoke("cancelled");
        }
        catch (Exception ex)
        {
            // トークン交換のエラー本文はここでは記録しない (client_secret を含む form は送信済みだが、
            // Google 側の応答本文にまで秘密が混ざる経路は無い。念のため型名のみ残す)。
            Logger.Warn($"Device flow failed: {ex.GetType().Name}", "YouTubeOAuthDeviceFlow");
            onError?.Invoke("network");
        }
    }

    private static string TryExtractErrorCode(string body)
    {
        try { return JsonNode.Parse(body)?["error"]?.ToString(); }
        catch { return null; }
    }
}
