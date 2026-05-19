using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using InnerNet;
using UnityEngine;

namespace EndKnot.Modules;

// Posts lobby announcements to the Cloudflare Workers relay (see relay/ in the
// repo for the server side). Off by default; opt-in via Client Options.
//
// Wire format: relay/CONTRACT.md
//   POST /api/announce  on host's LobbyBehaviour.Start  (online room with valid code)
//   POST /api/start     on ShipStatus.Begin             (lobby moved to in-game)
//   POST /api/end       on AmongUsClient.OnGameEnd      (also fires on quit-to-menu)
//
// The relay URL + HMAC key + FC salt are baked into the DLL. They are EXTRACTABLE
// — that's by design (see relay/README.md threat model). The HMAC only stops
// casual curl-attacks; a determined reverser bypasses it. Rotation = ship a
// new DLL release with new constants and rotate the Worker secret.
internal static class LobbyShare
{
    // ─── operator-baked constants ─────────────────────────────────────────────
    // Real values live in Modules/LobbyShareSecrets.cs (gitignored). Source-built
    // copies fall back to LobbyShareSecrets.Default.cs which has empty strings,
    // so IsConfigured returns false and nothing leaves the host.
    // See relay/DEPLOY.md Step 7.

    private const int HttpTimeoutSeconds = 8;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(HttpTimeoutSeconds) };

    // Throttle for /api/update — minimum interval between live player-count PATCHes.
    // Discord rate-limits PATCH on a single message; 5s leaves plenty of headroom.
    private const float UpdateThrottleSeconds = 5f;

    private static string activeCode;
    private static string activeFcHash;
    private static volatile bool announceSucceeded;
    // inGame: true between ShipStatus.Begin and AmongUsClient.OnGameEnd. Used by
    // OnLobbyDestroyed to distinguish "lobby → ship transition" (skip /api/close)
    // from "host actually left the lobby" (fire /api/close).
    private static volatile bool inGame;
    // Last values pushed via /api/announce or /api/update. -1 / null sentinel until
    // the first announce fires, so MaybeUpdatePlayerCount doesn't try to PATCH before
    // there's even an embed to PATCH.
    private static int lastSentPlayerCount = -1;
    private static int lastSentMax = -1;
    private static string lastSentMode;
    private static float lastUpdateAt;
    // pendingNotification: staged from background HTTP completion; drained on the
    // main thread by PumpNotifications() (called from LobbyBehaviour.Update).
    // Utils.SendMessage cannot be called from a worker thread — it touches Unity RPC.
    private static string pendingNotification;

    public static bool IsConfigured => LobbyShareSecrets.RelayUrl.Length > 0 && LobbyShareSecrets.HmacKey.Length > 0;

    // ─── public entry points ──────────────────────────────────────────────────

    public static void TryAnnounce()
    {
        Logger.Info("TryAnnounce called (LobbyBehaviour.Start Postfix)", "LobbyShare");

        // No reset-at-start here — when a lobby returns from a game (Play Again), the
        // existing activeCode/activeFcHash should persist so the server-side idempotent
        // PATCH can identify the same lobby. New-lobby-session resets happen in
        // OnLobbyDestroyed (clears state after /api/close).

        try
        {
            if (!ShouldAnnounce(out string code, out string region, out string fcHash, out int players, out int max, out string mode, out string hostName)) return;
            Logger.Info($"announcing code={code} region={region} players={players}/{max} mode={mode}", "LobbyShare");

            var body = new AnnounceBody
            {
                code = code,
                region = region,
                players = players,
                max = max,
                mode = mode,
                modVersion = Main.PluginVersion,
                hostName = hostName,
                fcHash = fcHash,
            };

            activeCode = code;
            activeFcHash = fcHash;
            lastSentPlayerCount = players;
            lastSentMax = max;
            lastSentMode = mode;
            lastUpdateAt = Time.time;
            // Don't reset announceSucceeded — may already be true from re-entry to
            // the same lobby. Server PATCHes existing entries idempotently.

            FireAndForget(PostSignedAsync("/api/announce", body, isAnnounce: true));
        }
        catch (Exception ex)
        {
            Logger.Warn($"TryAnnounce failed: {ex.Message}", "LobbyShare");
        }
    }

    public static void OnGameStarting()
    {
        // Set inGame BEFORE the lobby→ship scene transition (which fires
        // LobbyBehaviour.OnDestroy) so OnLobbyDestroyed doesn't mistake it for a
        // host-quit. GameStartManager.BeginGame Postfix is the right place:
        // it fires when host clicks Start, before the scene unloads, and Harmony
        // skips Postfixes if any Prefix returned false (so aborted starts don't
        // leave inGame stuck at true).
        inGame = true;
        Logger.Info("OnGameStarting: inGame=true (host clicked Start)", "LobbyShare");
    }

    public static void OnGameStarted()
    {
        if (!CanLifecycle()) return;
        // OnGameStarting already set inGame=true. Defensive re-set in case the
        // BeginGame hook missed (alternate start paths).
        inGame = true;
        FireAndForget(PostSignedAsync("/api/start", new LifecycleBody { code = activeCode, fcHash = activeFcHash }));
    }

    public static void OnGameEnded()
    {
        if (!CanLifecycle()) return;
        inGame = false;
        // Lobby is still alive — keep activeCode/activeFcHash for the upcoming
        // /api/end PATCH and the subsequent Play-Again re-announce / future /api/close.
        FireAndForget(PostSignedAsync("/api/end", new LifecycleBody { code = activeCode, fcHash = activeFcHash }));
    }

    public static void OnLobbyDestroyed()
    {
        // OnDestroy fires in two scenarios:
        //   (a) Game starting — lobby scene unloads to load ship scene. inGame=true.
        //       SKIP /api/close — the embed should stay (it'll flip to in-game next).
        //   (b) Host actually leaving the lobby. inGame=false. Call /api/close so the
        //       Discord embed and KV entry are cleaned up promptly (without waiting
        //       for the 3h TTL).
        if (inGame)
        {
            Logger.Info("OnLobbyDestroyed: inGame=true, skipping /api/close (lobby → ship transition)", "LobbyShare");
            return;
        }

        FireClose("OnLobbyDestroyed");
    }

    // Catches the mid-game disconnect path that OnLobbyDestroyed can't see:
    // LobbyBehaviour.OnDestroy already fired during the lobby→ship transition
    // (with inGame=true, correctly skipped) and there is no second LobbyBehaviour
    // to OnDestroy when the host quits mid-game. Without this hook the embed sits
    // until KV TTL (~3h). Idempotent with OnLobbyDestroyed — whichever runs first
    // clears activeCode, the second sees CanLifecycle()=false and no-ops.
    public static void OnHostDisconnected()
    {
        FireClose("OnHostDisconnected");
    }

    private static void FireClose(string source)
    {
        if (!CanLifecycle()) return;
        Logger.Info($"{source}: firing /api/close for code={activeCode}", "LobbyShare");
        string code = activeCode;
        string fc = activeFcHash;
        activeCode = null;
        activeFcHash = null;
        announceSucceeded = false;
        inGame = false;
        lastSentPlayerCount = -1;
        lastSentMax = -1;
        lastSentMode = null;
        FireAndForget(PostSignedAsync("/api/close", new LifecycleBody { code = code, fcHash = fc }));
    }

    // Called every tick by LobbyShareTickHook. Cheap diff-check + 5s throttle gates
    // actual HTTP. Only fires during the lobby phase (inGame=false) — in-game changes
    // don't matter for "is this lobby joinable" signaling.
    //
    // Watches three fields:
    //   - players (host count via AllPlayerControls.Count)
    //   - max (host-side MaxPlayers setting)
    //   - mode (host changed the EHR custom game mode)
    // A single 5s throttle gates all of them together; the same /api/update call
    // ships whatever fields are present.
    public static void MaybeUpdatePlayerCount()
    {
        if (!CanLifecycle()) return;
        if (inGame) return;
        if (!(Main.ShareLobbyToDiscord?.Value ?? false)) return;

        int count = Main.AllPlayerControls?.Count ?? 0;
        if (count < 1) return;
        int max = Main.NormalOptions?.MaxPlayers ?? 15;
        if (max < 1 || max > 15) max = 15;
        string mode = FormatMode(Options.CurrentGameMode);

        if (count == lastSentPlayerCount && max == lastSentMax && mode == lastSentMode) return;

        float now = Time.time;
        if (now - lastUpdateAt < UpdateThrottleSeconds) return;

        lastSentPlayerCount = count;
        lastSentMax = max;
        lastSentMode = mode;
        lastUpdateAt = now;

        FireAndForget(PostSignedAsync("/api/update", new UpdateBody { code = activeCode, fcHash = activeFcHash, players = count, max = max, mode = mode }));
    }

    // ─── eligibility ──────────────────────────────────────────────────────────

    private static bool ShouldAnnounce(out string code, out string region, out string fcHash, out int players, out int max, out string mode, out string hostName)
    {
        code = region = fcHash = mode = hostName = null;
        players = max = 0;

        if (!IsConfigured) { Logger.Info("skip: not configured (LobbyShareSecrets empty)", "LobbyShare"); return false; }
        if (!(Main.ShareLobbyToDiscord?.Value ?? false)) { Logger.Info("skip: ShareLobbyToDiscord toggle is OFF", "LobbyShare"); return false; }
        if (!AmongUsClient.Instance || !AmongUsClient.Instance.AmHost) { Logger.Info($"skip: not host (AmHost={AmongUsClient.Instance?.AmHost})", "LobbyShare"); return false; }
        if (AmongUsClient.Instance.NetworkMode != NetworkModes.OnlineGame) { Logger.Info($"skip: NetworkMode={AmongUsClient.Instance.NetworkMode} (need OnlineGame)", "LobbyShare"); return false; }

        string rawCode = GameCode.IntToGameName(AmongUsClient.Instance.GameId);
        if (string.IsNullOrEmpty(rawCode) || rawCode.Length != 6) { Logger.Info($"skip: invalid game code '{rawCode}'", "LobbyShare"); return false; }
        code = rawCode.ToUpperInvariant();

        region = NormalizeRegion(ServerManager.Instance?.CurrentRegion?.Name);
        if (region == null) return false;

        PlayerControl local = PlayerControl.LocalPlayer;
        if (!local) { Logger.Info("skip: PlayerControl.LocalPlayer null", "LobbyShare"); return false; }
        fcHash = HashFriendCode(local.FriendCode ?? string.Empty);

        players = Main.AllPlayerControls.Count;
        max = Main.NormalOptions?.MaxPlayers ?? 15;
        if (players < 1) players = 1;
        if (max < 1 || max > 15) max = 15;

        mode = FormatMode(Options.CurrentGameMode);
        hostName = local.Data?.PlayerName ?? string.Empty;

        return true;
    }

    private static bool CanLifecycle() => IsConfigured && announceSucceeded && !string.IsNullOrEmpty(activeCode) && !string.IsNullOrEmpty(activeFcHash);

    private static string NormalizeRegion(string raw)
    {
        // Maps whatever AU's CurrentRegion.Name returns (vendor-format unverified across
        // AU versions) to the canonical 2-char codes the relay expects. The relay also
        // accepts long-form names — we still normalize here so failure is observable
        // via the unrecognized-region log line below.
        if (string.IsNullOrEmpty(raw)) return null;
        string u = raw.ToUpperInvariant().Trim();
        if (u.Contains("NORTH AMERICA")) return "NA";
        if (u.Contains("EUROPE")) return "EU";
        if (u.Contains("ASIA")) return "AS";
        if (u == "NA" || u == "EU" || u == "AS") return u;
        // Log raw value so the first-test path is debuggable. Otherwise the feature
        // silently no-ops and the operator has no breadcrumb.
        Logger.Info($"unrecognized region: '{raw}' — announce skipped", "LobbyShare");
        return null;
    }

    private static string FormatMode(CustomGameMode m)
    {
        // The relay caps mode at 32 chars. Use enum name as-is.
        string s = m.ToString();
        return s.Length > 32 ? s[..32] : s;
    }

    // ─── HTTP ─────────────────────────────────────────────────────────────────

    private static async Task PostSignedAsync(string path, object body, bool isAnnounce = false)
    {
        string json = null;
        try
        {
            json = JsonSerializer.Serialize(body);
            string ts = ((long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds).ToString();
            string sig = SignHmac(ts + "." + json);

            using var req = new HttpRequestMessage(HttpMethod.Post, LobbyShareSecrets.RelayUrl.TrimEnd('/') + path);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            req.Headers.Add("X-Timestamp", ts);
            req.Headers.Add("X-Signature", sig);

            using HttpResponseMessage resp = await Http.SendAsync(req).ConfigureAwait(false);
            string respBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (resp.IsSuccessStatusCode)
            {
                Logger.Info($"{path} ok: {Trunc(respBody, 120)}", "LobbyShare");
                if (isAnnounce) announceSucceeded = true;
            }
            else
            {
                Logger.Warn($"{path} {(int)resp.StatusCode}: {Trunc(respBody, 200)}", "LobbyShare");
                if (isAnnounce) NotifyHostOfFailure((int)resp.StatusCode, respBody);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"{path} exception: {ex.Message}", "LobbyShare");
            if (isAnnounce) NotifyHostOfFailure(-1, ex.Message);
        }
    }

    private static void FireAndForget(Task t)
    {
        // Swallow background-task exceptions so an unhandled task never bubbles
        // into Unity's main loop. Logging already happens inside PostSignedAsync.
        _ = t.ContinueWith(static x =>
        {
            if (x.Exception != null) Logger.Warn($"background task fault: {x.Exception.GetBaseException().Message}", "LobbyShare");
        }, TaskScheduler.Default);
    }

    private static void NotifyHostOfFailure(int code, string detail)
    {
        // Stage the message — actual SendMessage call happens on main thread in PumpNotifications().
        string codeStr = code > 0 ? code.ToString() : "ERR";
        string detailStr = Trunc(detail, 80);
        string format;
        try { format = Translator.GetString("LobbyShare.AnnounceFailed"); }
        catch { format = "Lobby Share failed ({0}): {1}"; }
        Interlocked.Exchange(ref pendingNotification, string.Format(format, codeStr, detailStr));
    }

    internal static void PumpNotifications()
    {
        string msg = Interlocked.Exchange(ref pendingNotification, null);
        if (msg == null) return;
        try
        {
            if (!PlayerControl.LocalPlayer) return;
            byte hostId = PlayerControl.LocalPlayer.PlayerId;
            Utils.SendMessage(msg, hostId, Translator.GetString("LobbyShare.Title"));
        }
        catch (Exception ex)
        {
            Logger.Warn($"PumpNotifications failed: {ex.Message}", "LobbyShare");
        }
    }

    // ─── crypto helpers ───────────────────────────────────────────────────────

    private static string HashFriendCode(string fc)
    {
        using SHA256 sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(fc + LobbyShareSecrets.FcSalt));
        return ToHex(hash);
    }

    private static string SignHmac(string message)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(LobbyShareSecrets.HmacKey));
        byte[] mac = hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
        return ToHex(mac);
    }

    private static string ToHex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (byte b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    private static string Trunc(string s, int max) => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max];

    // ─── DTOs (lowercase to match relay wire format verbatim) ─────────────────

    private sealed class AnnounceBody
    {
        public string code { get; set; }
        public string region { get; set; }
        public int players { get; set; }
        public int max { get; set; }
        public string mode { get; set; }
        public string modVersion { get; set; }
        public string hostName { get; set; }
        public string fcHash { get; set; }
    }

    private sealed class LifecycleBody
    {
        public string code { get; set; }
        public string fcHash { get; set; }
    }

    private sealed class UpdateBody
    {
        public string code { get; set; }
        public string fcHash { get; set; }
        public int players { get; set; }
        public int max { get; set; }
        public string mode { get; set; }
    }
}

// ─── Harmony hooks ────────────────────────────────────────────────────────────

[HarmonyPatch(typeof(LobbyBehaviour), nameof(LobbyBehaviour.Start))]
internal static class LobbyShareAnnounceHook
{
    // LobbyBehaviour.Start fires before PlayerControl.LocalPlayer is spawned (the
    // host connection completes a few ticks later). Delay so LocalPlayer + region
    // + game-code are all available when ShouldAnnounce runs.
    public static void Postfix() => LateTask.New(LobbyShare.TryAnnounce, 3f, "LobbyShare.TryAnnounce");
}

[HarmonyPatch(typeof(GameStartManager), nameof(GameStartManager.BeginGame))]
internal static class LobbyShareGameStartingHook
{
    // Postfix only runs if all Prefixes returned true — aborted starts (invalid
    // color, etc.) skip this, so inGame doesn't get stuck on a non-started game.
    public static void Postfix() => LobbyShare.OnGameStarting();
}

[HarmonyPatch(typeof(ShipStatus), nameof(ShipStatus.Begin))]
internal static class LobbyShareStartHook
{
    public static void Postfix() => LobbyShare.OnGameStarted();
}

[HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameEnd))]
internal static class LobbyShareEndHook
{
    public static void Postfix() => LobbyShare.OnGameEnded();
}

[HarmonyPatch(typeof(LobbyBehaviour), nameof(LobbyBehaviour.OnDestroy))]
internal static class LobbyShareCloseHook
{
    public static void Postfix() => LobbyShare.OnLobbyDestroyed();
}

[HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.DisconnectInternal))]
internal static class LobbyShareDisconnectHook
{
    public static void Postfix(InnerNetClient __instance)
    {
        if (__instance is not AmongUsClient) return;
        LobbyShare.OnHostDisconnected();
    }
}

[HarmonyPatch(typeof(LobbyBehaviour), nameof(LobbyBehaviour.Update))]
internal static class LobbyShareTickHook
{
    public static void Postfix()
    {
        LobbyShare.PumpNotifications();
        LobbyShare.MaybeUpdatePlayerCount();
    }
}
