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
    private static string LastPhase;
    private static bool PhaseInitialized;

    private static long NextDemoTime;
    private static readonly string[] DemoRotation = ["Earthquake", "Meteor", "Blackout", "FakeBody", "Curse"];
    private static int DemoRotationIndex;

    public static void ResetForNewGame()
    {
        NextDemoTime = 0;
        DemoRotationIndex = 0;
        LastSabotageKind = null;
        LastSabotageEmitTime = 0;
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
        // 重複判定は phase 文字列で行う。bool 4つの組で見ると、同じ phase のまま
        // bool が段階的に変わるケース (試合終了処理中など) で同一 phase を連発してしまう。
        string phase = GameStates.IsEnded ? "ended" : GameStates.IsMeeting ? "meeting" : GameStates.InGame ? "ingame" : GameStates.IsLobby ? "lobby" : null;

        if (!PhaseInitialized)
        {
            PhaseInitialized = true;
            LastPhase = phase;
            return;
        }

        if (phase == null || phase == LastPhase) return;

        bool resumed = phase == "ingame" && LastPhase == "meeting";
        LastPhase = phase;

        Emit("phase", w =>
        {
            w.WriteString("phase", phase);

            if (phase != "ingame") return;

            // 会議明けの再開は「試合開始」と区別する (companion.py はこのフラグで実況を変える)
            if (resumed) w.WriteBoolean("resumed", true);
            w.WriteString("map", GetString(Main.CurrentMap.ToString()).RemoveHtmlTags());
            w.WriteNumber("playerCount", Main.PlayerStates.Count);
            w.WriteString("mode", StripLabelPrefix(GetString($"Mode{Options.CurrentGameMode}")));
        });
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

    // "Gamemode: Standard" / "ゲームモード: 標準" のようなラベル付き文言からモード名だけを取り出す
    private static string StripLabelPrefix(string text)
    {
        int idx = text.LastIndexOfAny([':', '：']);
        return idx >= 0 ? text[(idx + 1)..].Trim() : text.Trim();
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

    // 追放は全プレイヤーに公開済みの結果のみ載せる (役職名は CEMode==2 で画面に実際に表示される場合のみ)
    public static void OnEject(NetworkedPlayerInfo exiled)
    {
        if (!Enabled) return;

        if (exiled == null)
        {
            Emit("eject", w => w.WriteBoolean("skipped", true));
            return;
        }

        string name = exiled.Object ? exiled.Object.GetRealName(true).RemoveHtmlTags() : Main.AllPlayerNames.TryGetValue(exiled.PlayerId, out string cachedName) ? cachedName : null;
        bool showRole = Options.CEMode.GetInt() == 2;
        string roleName = showRole ? Utils.GetRoleName(exiled.GetCustomRole()) : null;

        Emit("eject", w =>
        {
            w.WriteString("name", name);
            if (roleName != null) w.WriteString("roleName", roleName);
        });
    }

    public static void OnGameEnd(CustomWinner winnerTeam, System.Collections.Generic.List<string> winners)
    {
        if (!Enabled) return;

        string winnerTeamName = GetWinnerTeamName(winnerTeam);

        // 勝者がいない終了 (ホスト中断/全滅/タイマー/エラー) は winnerTeam に「理由の文言」が入る。
        // companion.py はこのフラグで「勝利したのは〜」の文型を避ける。
        bool noVictors = winnerTeam is CustomWinner.Draw or CustomWinner.None or CustomWinner.Error;

        Emit("gameEnd", w =>
        {
            if (noVictors) w.WriteBoolean("noVictors", true);
            w.WriteString("winnerTeam", winnerTeamName);
            w.WriteStartArray("winners");
            foreach (string winnerName in winners) w.WriteStringValue(winnerName.RemoveHtmlTags());
            w.WriteEndArray();
        });
    }

    // SetEverythingUpPatch (Patches/OutroPatch.cs) の勝敗画面と同じ lang キー規則を再利用。
    // 負値の特殊勝敗 (Draw/Neutrals/None/Error/CustomTeam) は CustomRoles に対応が無いため専用文言 (OutroPatch の switch と同じキー)。
    private static string GetWinnerTeamName(CustomWinner winnerTeam)
    {
        switch (winnerTeam)
        {
            case CustomWinner.Draw:
                return GetString("ForceEndText").RemoveHtmlTags();
            case CustomWinner.Neutrals:
                return GetString("NeutralsLeftText").RemoveHtmlTags();
            case CustomWinner.None:
                return GetString(Main.GameEndDueToTimer ? "GameTimerEnded" : "EveryoneDied").RemoveHtmlTags();
            case CustomWinner.Error:
                return GetString("ErrorEndTextDescription").RemoveHtmlTags();
            case CustomWinner.CustomTeam:
                try { return string.Format(GetString("CustomWinnerText"), CustomTeamManager.WinnerTeam.TeamName).RemoveHtmlTags(); }
                catch { return GetString("CustomWinnerText").RemoveHtmlTags(); }
        }

        var winnerRole = (CustomRoles)winnerTeam;
        string name = GetString($"WinnerRoleText.{winnerRole}");
        if (name == string.Empty || name.StartsWith("*") || name.StartsWith("<INVALID"))
            name = string.Format(GetString("WinnerRoleText.Default"), GetString($"{winnerRole}"));

        return name.RemoveHtmlTags();
    }

    private static long LastSabotageEmitTime;
    private static string LastSabotageKind;

    // 発動者は載せない (誰がサボしたかは公開情報ではない)
    public static void OnSabotageStart(SystemTypes systemTypes)
    {
        if (!Enabled) return;

        string kind = systemTypes switch
        {
            SystemTypes.Reactor or SystemTypes.Laboratory or SystemTypes.HeliSabotage => "Reactor",
            SystemTypes.LifeSupp => "O2",
            SystemTypes.Electrical => "Lights",
            SystemTypes.Comms => "Comms",
            SystemTypes.MushroomMixupSabotage => "MushroomMixup",
            _ => null
        };

        if (kind == null) return;

        long now = Utils.TimeStamp;
        if (kind == LastSabotageKind && now - LastSabotageEmitTime < 5) return;

        LastSabotageKind = kind;
        LastSabotageEmitTime = now;

        string kindName = GetString($"Companion.Sabotage.{kind}");
        Emit("sabotage", w =>
        {
            w.WriteString("kind", kind);
            w.WriteString("kindName", kindName);
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
