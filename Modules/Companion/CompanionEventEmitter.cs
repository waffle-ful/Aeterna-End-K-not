using System;
using System.IO;
using System.Text.Json;
using EndKnot.Modules.Audience;
using EndKnot.Modules.YouTubeChat;

namespace EndKnot.Modules.Companion;

// AI実況相棒アプリ (別プロセス) 向けのイベント出力層。
// ネットワーク送信ゼロ・完全ホストローカル。相棒アプリは companion-events.jsonl を tail して読む想定。
// 例外は絶対に呼び出し元へ漏らさない (内部 try/catch + Logger.Warn、連続失敗時はログ抑制)。
public static class CompanionEventEmitter
{
    private static readonly string EventsPath = $"{Main.DataPath}/EndKnot_DATA/companion-events.jsonl";

    private const long MaxFileBytes = 5 * 1024 * 1024;

    private static readonly object FileLock = new();
    private static bool TruncateChecked;
    private static bool WarnedOnce;

    private static bool Enabled => Main.EnableAICommentary != null && Main.EnableAICommentary.Value;

    // ---- YouTube chat 購読 (worker thread から発火するため lock 経由で直接書く) ----
    private static bool ChatSubscribed;

    private static void EnsureChatSubscribed()
    {
        if (ChatSubscribed) return;
        ChatSubscribed = true;
        YouTubeChatManager.OnMessage += OnChatMessage;
    }

    private static void OnChatMessage(string author, string text)
    {
        if (!Enabled) return;
        Emit("chat", w =>
        {
            w.WriteString("author", author);
            w.WriteString("text", text);
        });
    }

    // ---- Tick 駆動 (main thread, FixedUpdateCaller 経由) ----
    private static bool WasLobby, WasInGame, WasMeeting, WasEnded;
    private static bool PhaseInitialized;

    private static long NextDemoTime;
    private static readonly string[] DemoRotation = ["Earthquake", "Meteor", "Blackout", "FakeBody", "Curse"];
    private static int DemoRotationIndex;

    public static void ResetForNewGame()
    {
        NextDemoTime = 0;
        DemoRotationIndex = 0;
    }

    public static void Tick()
    {
        EnsureChatSubscribed();

        if (!Enabled) return;
        if (!AmongUsClient.Instance || !AmongUsClient.Instance.AmHost) return;

        TickPhase();
        TickLobbyDemo();
    }

    private static void TickPhase()
    {
        bool isLobby = GameStates.IsLobby;
        bool inGame = GameStates.InGame;
        bool isMeeting = GameStates.IsMeeting;
        bool isEnded = GameStates.IsEnded;

        if (!PhaseInitialized)
        {
            PhaseInitialized = true;
            WasLobby = isLobby;
            WasInGame = inGame;
            WasMeeting = isMeeting;
            WasEnded = isEnded;
            return;
        }

        if (isLobby == WasLobby && inGame == WasInGame && isMeeting == WasMeeting && isEnded == WasEnded) return;

        string phase = isEnded ? "ended" : isMeeting ? "meeting" : inGame ? "ingame" : isLobby ? "lobby" : null;

        WasLobby = isLobby;
        WasInGame = inGame;
        WasMeeting = isMeeting;
        WasEnded = isEnded;

        if (phase == null) return;

        Emit("phase", w => w.WriteString("phase", phase));
    }

    // ロビー放置画面を「動いてる」ように見せるホストローカル演出。実サボタージュ/RPC は撃たない。
    private static void TickLobbyDemo()
    {
        if (!GameStates.IsLobby) return;

        long now = Utils.TimeStamp;
        if (NextDemoTime == 0)
        {
            NextDemoTime = now + IRandom.Instance.Next(60, 91);
            return;
        }

        if (now < NextDemoTime) return;

        NextDemoTime = now + IRandom.Instance.Next(60, 91);

        string kind = DemoRotation[DemoRotationIndex % DemoRotation.Length];
        DemoRotationIndex++;

        string demoAuthor = GetString("Audience.Demo.Author");

        try
        {
            AudienceCutscene.Play(kind, demoAuthor, byte.MaxValue);
            if (kind == "Earthquake") AudienceCutscene.ShakeCamera(4f, 0.3f);
        }
        catch (Exception ex) { Logger.Exception(ex, "CompanionEventEmitter.TickLobbyDemo"); }

        string kindName = GetString($"Audience.Cutscene.{kind}");
        Emit("demo", w =>
        {
            w.WriteString("kind", kind);
            w.WriteString("kindName", kindName);
        });
    }

    private static string GetString(string key)
    {
        try { return Translator.GetString(key); }
        catch { return key; }
    }

    // ---- join/leave/intervention フック (Patches/*.cs から呼ぶ) ----
    public static void OnPlayerJoin(string name)
    {
        if (!Enabled) return;
        Emit("join", w => w.WriteString("name", name));
    }

    public static void OnPlayerLeave(string name)
    {
        if (!Enabled) return;
        Emit("leave", w => w.WriteString("name", name));
    }

    public static void OnIntervention(string kind, string kindName, string author)
    {
        if (!Enabled) return;
        Emit("intervention", w =>
        {
            w.WriteString("kind", kind);
            w.WriteString("kindName", kindName);
            w.WriteString("author", author);
        });
    }

    // ---- 書き込み本体 ----
    private static void Emit(string type, Action<Utf8JsonWriter> writeFields)
    {
        try
        {
            lock (FileLock)
            {
                EnsureDirectoryAndTruncate();

                using var stream = new MemoryStream();
                using (var writer = new Utf8JsonWriter(stream))
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    writer.WriteString("type", type);
                    writeFields(writer);
                    writer.WriteEndObject();
                }

                string line = System.Text.Encoding.UTF8.GetString(stream.ToArray());
                File.AppendAllText(EventsPath, line + "\n");
            }
        }
        catch (Exception ex)
        {
            // 連続失敗時にログを埋め尽くさないよう、1度だけ警告する。
            if (!WarnedOnce)
            {
                WarnedOnce = true;
                Logger.Warn($"CompanionEventEmitter.Emit failed: {ex.Message}", "CompanionEventEmitter");
            }
        }
    }

    private static void EnsureDirectoryAndTruncate()
    {
        string dir = $"{Main.DataPath}/EndKnot_DATA";
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        if (TruncateChecked) return;
        TruncateChecked = true;

        try
        {
            if (File.Exists(EventsPath) && new FileInfo(EventsPath).Length > MaxFileBytes)
                File.Delete(EventsPath);
        }
        catch (Exception ex) { Logger.Exception(ex, "CompanionEventEmitter.EnsureDirectoryAndTruncate"); }
    }
}
