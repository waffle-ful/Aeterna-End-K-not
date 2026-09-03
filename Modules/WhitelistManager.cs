using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using InnerNet;
using UnityEngine;
using static EndKnot.Translator;

namespace EndKnot.Modules;

// FriendCode/hashedPuid どちらでも照合できる入室ホワイトリスト。ファイル自体は
// BanManager.cs が起動時に作成済み (WhiteList.txt) だが、これまで照合は未実装だった。
public static class WhitelistManager
{
    private static readonly string WhiteListPath = $"{Main.DataPath}/EndKnot_DATA/WhiteList.txt";

    private static OptionItem EnableWhitelist;
    private static OptionItem KickNonListed;
    private static OptionItem ExemptModerators;

    // 値は FriendCode か hashedPuid のどちらか (小文字化して保持)
    private static readonly HashSet<string> Entries = [];

    public static void SetupCustomOption()
    {
        new TextOptionItem(110050, "MenuTitle.Whitelist", TabGroup.GameSettings)
            .SetColor(new Color32(255, 214, 92, byte.MaxValue))
            .SetHeader(true);

        EnableWhitelist = new BooleanOptionItem(960000, "EnableWhitelist", false, TabGroup.GameSettings)
            .SetColor(new Color32(255, 214, 92, byte.MaxValue));

        KickNonListed = new BooleanOptionItem(960001, "WhitelistKickNonListed", true, TabGroup.GameSettings)
            .SetParent(EnableWhitelist)
            .SetColor(new Color32(255, 214, 92, byte.MaxValue));

        ExemptModerators = new BooleanOptionItem(960002, "WhitelistExemptModerators", true, TabGroup.GameSettings)
            .SetParent(EnableWhitelist)
            .SetColor(new Color32(255, 214, 92, byte.MaxValue));

        Reload();
    }

    private static string NormalizeKey(string raw)
    {
        string key = raw.Trim();
        if (key.StartsWith("friendcode:", StringComparison.OrdinalIgnoreCase)) key = key[11..];
        else if (key.StartsWith("puid:", StringComparison.OrdinalIgnoreCase)) key = key[5..];
        return key.Replace(':', '#').Trim().ToLowerInvariant();
    }

    public static void Reload()
    {
        Entries.Clear();

        try
        {
            if (!Directory.Exists($"{Main.DataPath}/EndKnot_DATA")) Directory.CreateDirectory($"{Main.DataPath}/EndKnot_DATA");
            if (!File.Exists(WhiteListPath)) { File.Create(WhiteListPath).Close(); return; }

            foreach (string raw in File.ReadAllLines(WhiteListPath))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;

                string key = NormalizeKey(line.Split(',')[0]);
                if (key.Length > 0) Entries.Add(key);
            }

            Logger.Info($"Whitelist loaded ({Entries.Count} entries)", "Whitelist");
        }
        catch (Exception ex) { Logger.Exception(ex, "Whitelist.Reload"); }
    }

    private static bool IsListed(ClientData client)
    {
        string fc = client.FriendCode?.Replace(':', '#').Trim().ToLowerInvariant() ?? "";
        if (fc.Length > 0 && Entries.Contains(fc)) return true;

        // 無効な PUID (空/短い) は誰でも同じハッシュに化けるので照合キーとして使わない
        if (!client.HasValidPuid()) return false;

        string puid = client.GetHashedPuid().ToLowerInvariant();
        return puid.Length > 0 && Entries.Contains(puid);
    }

    // 戻り値 = この客を蹴ったかどうか。蹴ったなら後続の入室チェックは走らせない。
    public static bool CheckJoiningPlayer(ClientData client)
    {
        if (!AmongUsClient.Instance.AmHost || client == null) return false;
        if (EnableWhitelist?.GetBool() != true) return false;
        if (client.Id == AmongUsClient.Instance.HostId) return false;
        if (GameStates.CurrentServerType is GameStates.ServerType.Local) return false;
        if (IsListed(client)) return false;
        if (ExemptModerators.GetBool() && ChatCommands.IsPlayerModerator(client.FriendCode)) return false;

        if (!KickNonListed.GetBool())
        {
            Utils.SendMessage(string.Format(GetString("Message.WhitelistNotifyOnly"), client.PlayerName), PlayerControl.LocalPlayer.PlayerId);
            Logger.Info($"{client.PlayerName} is not on the whitelist (notify only)", "Whitelist");
            return false;
        }

        AmongUsClient.Instance.KickPlayer(client.Id, false);
        Logger.SendInGame(string.Format(GetString("Message.KickedByWhitelist"), client.PlayerName), Color.yellow);
        Logger.Info($"{client.PlayerName} was kicked because they are not on the whitelist", "Whitelist");
        return true;
    }

    // 対象のプレイヤーを追加する。FriendCode が空 (Epic/エミュ等) なら hashedPuid を代わりに書く。
    public static (bool Success, string Key, bool ByPuid) AddFromPlayer(PlayerControl target)
    {
        ClientData client = target?.GetClient();
        if (client == null) return (false, "", false);

        string fc = client.FriendCode?.Replace(':', '#').Trim() ?? "";
        bool byPuid = fc.Length == 0;
        if (byPuid && !client.HasValidPuid()) return (false, "", false);

        string rawKey = byPuid ? client.GetHashedPuid() : fc;
        if (rawKey.Length == 0) return (false, "", false);

        string normalized = NormalizeKey(rawKey);
        if (Entries.Add(normalized))
        {
            try
            {
                File.AppendAllText(WhiteListPath, $"{(byPuid ? "puid:" : "friendcode:")}{rawKey},{client.PlayerName?.RemoveHtmlTags()}\n");
            }
            catch (Exception ex) { Logger.Exception(ex, "Whitelist.AddFromPlayer"); }
        }

        return (true, rawKey, byPuid);
    }

    public static bool RemoveKey(string rawKey)
    {
        string normalized = NormalizeKey(rawKey);
        if (!Entries.Remove(normalized)) return false;

        try
        {
            var remaining = File.ReadAllLines(WhiteListPath).Where(l => l.Trim().Length == 0 || NormalizeKey(l.Split(',')[0]) != normalized);
            File.WriteAllLines(WhiteListPath, remaining);
        }
        catch (Exception ex) { Logger.Exception(ex, "Whitelist.RemoveKey"); }

        return true;
    }

    public static IEnumerable<string> ListEntries() => Entries.OrderBy(x => x);
}
