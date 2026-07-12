using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EndKnot.Modules.Audience;

// 視聴者ポイント経済。author 文字列をキーにしたホストローカル永続化。
// 発言で貯まる (クールダウン付き) / 干渉で消費。BAN リストも同ファイルに同居。
public static class AudienceEconomy
{
    private class SaveData
    {
        [JsonPropertyName("Points")]
        public Dictionary<string, int> Points { get; set; } = [];

        [JsonPropertyName("Banned")]
        public List<string> Banned { get; set; } = [];
    }

    private static readonly string SaveFilePath = $"{Main.DataPath}/EndKnot_DATA/AudiencePoints.json";

    private static readonly Dictionary<string, int> Points = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, long> LastPointsGrantTS = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Banned = new(StringComparer.OrdinalIgnoreCase);

    private static bool Dirty;
    private static long LastSaveTS;
    private const long SaveDebounceSeconds = 30;

    private static bool Loaded;

    public static void EnsureLoaded()
    {
        if (Loaded) return;
        Loaded = true;

        try
        {
            if (!Directory.Exists($"{Main.DataPath}/EndKnot_DATA")) Directory.CreateDirectory($"{Main.DataPath}/EndKnot_DATA");

            if (File.Exists(SaveFilePath))
            {
                string json = File.ReadAllText(SaveFilePath);
                SaveData data = JsonSerializer.Deserialize<SaveData>(json);
                if (data != null)
                {
                    foreach (KeyValuePair<string, int> kvp in data.Points) Points[kvp.Key] = kvp.Value;
                    foreach (string author in data.Banned) Banned.Add(author);
                }
            }
        }
        catch (Exception ex) { Logger.Exception(ex, "AudienceEconomy.EnsureLoaded"); }
    }

    // 変更時デバウンス保存。AudienceManager.Tick から毎 tick 呼ばれる想定。
    public static void OnTick()
    {
        if (!Dirty) return;
        if (Utils.TimeStamp - LastSaveTS < SaveDebounceSeconds) return;

        SaveNow();
    }

    public static void SaveNow()
    {
        if (!Loaded) return;

        try
        {
            SaveData data = new()
            {
                Points = new Dictionary<string, int>(Points, StringComparer.Ordinal),
                Banned = [.. Banned]
            };

            string json = JsonSerializer.Serialize(data);
            File.WriteAllText(SaveFilePath, json);
            Dirty = false;
            LastSaveTS = Utils.TimeStamp;
        }
        catch (Exception ex) { Logger.Exception(ex, "AudienceEconomy.SaveNow"); }
    }

    public static bool IsBanned(string author)
    {
        EnsureLoaded();
        return !string.IsNullOrEmpty(author) && Banned.Contains(author);
    }

    public static void Ban(string author)
    {
        EnsureLoaded();
        if (string.IsNullOrEmpty(author)) return;
        if (Banned.Add(author)) Dirty = true;
    }

    public static void Unban(string author)
    {
        EnsureLoaded();
        if (string.IsNullOrEmpty(author)) return;
        if (Banned.Remove(author)) Dirty = true;
    }

    public static int GetPoints(string author)
    {
        EnsureLoaded();
        return !string.IsNullOrEmpty(author) && Points.TryGetValue(author, out int p) ? p : 0;
    }

    // 発言 1 回で加算。同一 author はクールダウン中スキップ。加算されたら true。
    public static bool TryGrantForMessage(string author)
    {
        EnsureLoaded();
        if (string.IsNullOrEmpty(author) || IsBanned(author)) return false;

        long now = Utils.TimeStamp;
        float cooldown = AudienceOptions.PointsCooldown.GetFloat();

        if (LastPointsGrantTS.TryGetValue(author, out long lastTS) && now - lastTS < (long)cooldown) return false;

        LastPointsGrantTS[author] = now;
        AddPoints(author, AudienceOptions.PointsPerMessage.GetInt());
        return true;
    }

    public static void AddPoints(string author, int amount)
    {
        EnsureLoaded();
        if (string.IsNullOrEmpty(author) || amount == 0) return;

        Points[author] = GetPoints(author) + amount;
        Dirty = true;
    }

    // 価格分のポイントが足りれば消費して true。足りなければ何もせず false。
    public static bool TrySpend(string author, int price)
    {
        EnsureLoaded();
        if (string.IsNullOrEmpty(author)) return false;
        if (price <= 0) return true;

        int current = GetPoints(author);
        if (current < price) return false;

        Points[author] = current - price;
        Dirty = true;
        return true;
    }

    public static IEnumerable<KeyValuePair<string, int>> TopPoints(int count)
    {
        EnsureLoaded();
        List<KeyValuePair<string, int>> list = [.. Points];
        list.Sort((a, b) => b.Value.CompareTo(a.Value));
        if (list.Count > count) list.RemoveRange(count, list.Count - count);
        return list;
    }
}
