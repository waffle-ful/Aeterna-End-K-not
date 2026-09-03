using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using InnerNet;
using UnityEngine;
using static EndKnot.Translator;

namespace EndKnot.Modules;

// 前の試合に居た人が続けて次の部屋にも入ってきた場合に弾く (野良配信の回転率確保)。
// 再ホスト (Application.Quit → 番犬再起動) を跨いでも直前の試合ぶんを覚えていられるよう、
// 記憶は EndKnot_DATA 配下のファイルへ試合終了ごとに1回だけ書き出す。
public static class ConsecutiveJoinKick
{
    private static readonly string HistoryPath = $"{Main.DataPath}/EndKnot_DATA/ConsecutiveJoinHistory.txt";
    private const int MaxHistoryEntries = 5; // CooldownGames の最大値と揃える

    private static OptionItem EnableConsecutiveJoinKick;
    private static OptionItem NotifyOnly;
    private static OptionItem ExemptModerators;
    private static OptionItem SkipAbortedGames;
    private static OptionItem CooldownGames;

    // 古い順に並ぶ、試合ごとの参加者 hashedPuid 集合
    private static readonly Queue<(long Timestamp, HashSet<string> Puids)> History = [];

    // この試合が始まった時点の参加者 (ゲーム終了時にここから History へ積む)
    private static HashSet<string> CurrentMatchPuids = [];

    // 今回の join に限り通す (モデレーターが /aj で個別に許可した相手)
    private static readonly HashSet<string> TempAllowed = [];

    // 恒久的に対象外にする (/ex で追加、セッション中のみ保持)
    private static readonly HashSet<string> PermanentExempt = [];

    // 名前 (小文字・タグ除去) -> hashedPuid。既に退出済みの相手を /ex /aj で名指しできるようにするための直近キャッシュ
    private static readonly Dictionary<string, string> RecentNameToHashedPuid = [];

    public static void SetupCustomOption()
    {
        new TextOptionItem(110060, "MenuTitle.ConsecutiveJoinKick", TabGroup.GameSettings)
            .SetColor(new Color32(255, 150, 90, byte.MaxValue))
            .SetHeader(true);

        EnableConsecutiveJoinKick = new BooleanOptionItem(960020, "EnableConsecutiveJoinKick", false, TabGroup.GameSettings)
            .SetColor(new Color32(255, 150, 90, byte.MaxValue));

        NotifyOnly = new BooleanOptionItem(960021, "ConsecutiveJoinKickNotifyOnly", false, TabGroup.GameSettings)
            .SetParent(EnableConsecutiveJoinKick)
            .SetColor(new Color32(255, 150, 90, byte.MaxValue));

        ExemptModerators = new BooleanOptionItem(960022, "ConsecutiveJoinKickExemptModerators", true, TabGroup.GameSettings)
            .SetParent(EnableConsecutiveJoinKick)
            .SetColor(new Color32(255, 150, 90, byte.MaxValue));

        SkipAbortedGames = new BooleanOptionItem(960023, "ConsecutiveJoinKickSkipAbortedGames", true, TabGroup.GameSettings)
            .SetParent(EnableConsecutiveJoinKick)
            .SetColor(new Color32(255, 150, 90, byte.MaxValue));

        CooldownGames = new IntegerOptionItem(960024, "ConsecutiveJoinKickCooldownGames", new(1, MaxHistoryEntries, 1), 1, TabGroup.GameSettings)
            .SetParent(EnableConsecutiveJoinKick)
            .SetColor(new Color32(255, 150, 90, byte.MaxValue));

        LoadHistory();
    }

    private static void LoadHistory()
    {
        History.Clear();

        try
        {
            if (!Directory.Exists($"{Main.DataPath}/EndKnot_DATA")) Directory.CreateDirectory($"{Main.DataPath}/EndKnot_DATA");
            if (!File.Exists(HistoryPath)) { File.Create(HistoryPath).Close(); return; }

            foreach (string raw in File.ReadAllLines(HistoryPath))
            {
                if (raw.Trim().Length == 0) continue;

                string[] parts = raw.Split(',');
                if (parts.Length == 0 || !long.TryParse(parts[0], out long ts)) continue;

                History.Enqueue((ts, new HashSet<string>(parts.Skip(1).Where(p => p.Length > 0))));
            }

            while (History.Count > MaxHistoryEntries) History.Dequeue();

            Logger.Info($"Consecutive-join history loaded ({History.Count} past game(s))", "ConsecutiveJoinKick");
        }
        catch (Exception ex) { Logger.Exception(ex, "ConsecutiveJoinKick.LoadHistory"); }
    }

    private static void PersistHistory()
    {
        try
        {
            var lines = History.Select(h => $"{h.Timestamp},{string.Join(',', h.Puids)}");
            File.WriteAllLines(HistoryPath, lines);
        }
        catch (Exception ex) { Logger.Exception(ex, "ConsecutiveJoinKick.PersistHistory"); }
    }

    private static bool IsFlagged(string hashedPuid)
    {
        if (History.Count == 0) return false;

        int n = Math.Clamp(CooldownGames.GetInt(), 1, History.Count);
        return History.Skip(History.Count - n).Any(h => h.Puids.Contains(hashedPuid));
    }

    private static void RememberName(ClientData client)
    {
        if (!client.HasValidPuid()) return;

        string name = (client.PlayerName ?? "").RemoveHtmlTags().Trim();
        string puid = client.GetHashedPuid();
        if (name.Length == 0 || puid.Length == 0) return;

        RecentNameToHashedPuid[name.ToLowerInvariant()] = puid;
    }

    public static void CheckJoiningPlayer(ClientData client)
    {
        if (!AmongUsClient.Instance.AmHost || client == null) return;

        RememberName(client);

        if (EnableConsecutiveJoinKick?.GetBool() != true) return;
        if (client.Id == AmongUsClient.Instance.HostId) return;
        if (GameStates.CurrentServerType is GameStates.ServerType.Local) return;

        // 無効な PUID (空/短い) は誰でも同じハッシュに化けるので、この人はまるごと追跡対象から外す
        if (!client.HasValidPuid()) return;

        string hashedPuid = client.GetHashedPuid();
        if (hashedPuid.Length == 0) return;

        if (TempAllowed.Remove(hashedPuid))
        {
            Logger.Info($"{client.PlayerName} was let back in via /aj", "ConsecutiveJoinKick");
            return;
        }

        if (PermanentExempt.Contains(hashedPuid)) return;
        if (ExemptModerators.GetBool() && ChatCommands.IsPlayerModerator(client.FriendCode)) return;
        if (!IsFlagged(hashedPuid)) return;

        if (!NotifyOnly.GetBool())
        {
            AmongUsClient.Instance.KickPlayer(client.Id, false);
            Logger.SendInGame(string.Format(GetString("Message.KickedByConsecutiveJoin"), client.PlayerName), Color.yellow);
            Logger.Info($"{client.PlayerName} was kicked for rejoining after the previous match", "ConsecutiveJoinKick");
            return;
        }

        Utils.SendMessage(string.Format(GetString("Message.ConsecutiveJoinNotifyOnly"), client.PlayerName), PlayerControl.LocalPlayer.PlayerId);
        Logger.Info($"{client.PlayerName} rejoined after the previous match (notify only)", "ConsecutiveJoinKick");
    }

    // ── 試合ライフサイクル ──────────────────────────────────────────────

    public static void OnMatchStart()
    {
        if (!AmongUsClient.Instance.AmHost || EnableConsecutiveJoinKick?.GetBool() != true) return;

        CurrentMatchPuids = [];
        foreach (ClientData client in AmongUsClient.Instance.allClients)
        {
            if (!client.HasValidPuid()) continue;

            string puid = client.GetHashedPuid();
            if (puid.Length > 0) CurrentMatchPuids.Add(puid);
        }
    }

    public static void OnMatchEnd()
    {
        if (!AmongUsClient.Instance.AmHost || EnableConsecutiveJoinKick?.GetBool() != true || CurrentMatchPuids.Count == 0) return;

        if (SkipAbortedGames.GetBool() && CustomWinnerHolder.WinnerTeam == CustomWinner.Draw)
        {
            Logger.Info("Not recording this match for consecutive-join tracking (aborted/draw)", "ConsecutiveJoinKick");
            CurrentMatchPuids = [];
            return;
        }

        History.Enqueue((Utils.TimeStamp, CurrentMatchPuids));
        while (History.Count > MaxHistoryEntries) History.Dequeue();
        PersistHistory();

        Logger.Info($"Recorded {CurrentMatchPuids.Count} player(s) from this match for consecutive-join tracking", "ConsecutiveJoinKick");
        CurrentMatchPuids = [];
    }

    // ── コマンド用ヘルパー ──────────────────────────────────────────────

    public static string ResolveHashedPuid(string arg, out string displayName)
    {
        displayName = arg;

        if (byte.TryParse(arg, out byte id))
        {
            PlayerControl pc = Utils.GetPlayerById(id);
            ClientData client = pc?.GetClient();
            if (client != null && client.HasValidPuid())
            {
                displayName = pc.Data?.PlayerName?.RemoveHtmlTags() ?? arg;
                return client.GetHashedPuid();
            }
        }

        string needle = arg.Trim().ToLowerInvariant();

        foreach (PlayerControl pc in PlayerControl.AllPlayerControls)
        {
            string name = (pc.Data?.PlayerName ?? "").RemoveHtmlTags().Trim();
            if (!name.ToLowerInvariant().Equals(needle)) continue;

            ClientData client = pc.GetClient();
            if (client == null || !client.HasValidPuid()) continue;

            displayName = name;
            return client.GetHashedPuid();
        }

        if (RecentNameToHashedPuid.TryGetValue(needle, out string cachedPuid))
            return cachedPuid;

        return null;
    }

    public static bool AddExempt(string arg, out string displayName)
    {
        string puid = ResolveHashedPuid(arg, out displayName);
        if (puid == null) return false;

        PermanentExempt.Add(puid);
        return true;
    }

    public static bool RemoveExempt(string arg, out string displayName)
    {
        string puid = ResolveHashedPuid(arg, out displayName);
        if (puid == null) return false;

        return PermanentExempt.Remove(puid);
    }

    public static string GetExemptListText()
    {
        return PermanentExempt.Count == 0 ? GetString("Message.ConsecutiveJoinExemptListEmpty") : string.Join('\n', PermanentExempt);
    }

    public static bool AllowNextJoin(string arg, out string displayName)
    {
        string puid = ResolveHashedPuid(arg, out displayName);
        if (puid == null) return false;

        TempAllowed.Add(puid);
        return true;
    }

    public static void ClearTempAllow() => TempAllowed.Clear();

    public static int KickAllFlagged()
    {
        if (!AmongUsClient.Instance.AmHost) return 0;

        int count = 0;
        foreach (ClientData client in AmongUsClient.Instance.allClients.ToArray())
        {
            if (client.Id == AmongUsClient.Instance.HostId) continue;
            if (!client.HasValidPuid()) continue;

            string hashedPuid = client.GetHashedPuid();
            if (hashedPuid.Length == 0) continue;
            if (PermanentExempt.Contains(hashedPuid) || TempAllowed.Contains(hashedPuid)) continue;
            if (ExemptModerators.GetBool() && ChatCommands.IsPlayerModerator(client.FriendCode)) continue;
            if (!IsFlagged(hashedPuid)) continue;

            AmongUsClient.Instance.KickPlayer(client.Id, false);
            Logger.Info($"{client.PlayerName} bulk-kicked via /kp", "ConsecutiveJoinKick");
            count++;
        }

        return count;
    }
}

[HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.StartGame))]
internal static class ConsecutiveJoinKickStartGamePatch
{
    public static void Prefix() => ConsecutiveJoinKick.OnMatchStart();
}

[HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameEnd))]
internal static class ConsecutiveJoinKickGameEndPatch
{
    public static void Postfix() => ConsecutiveJoinKick.OnMatchEnd();
}
