using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace EndKnot.Modules.VoiceVox;

// Loads per-crew voice overrides from BepInEx/config/EndKnot_VoiceVox_Voices.json.
// Matched by friendCode (non-empty) first, then name (case-insensitive).
// Also manages the speaker discovery dump file.
internal static class VoiceVoxVoiceConfig
{
    // Hardcoded fallback pool (standard VoiceVox default style ids).
    // Used when /speakers is unavailable AND defaultPool in config is empty.
    private static readonly int[] HardcodedPool = [3, 2, 8, 10, 9, 11, 12, 13, 14, 16, 20, 21, 23, 47, 51];

    private static readonly string ConfigPath =
        Path.Combine(BepInEx.Paths.ConfigPath, "EndKnot_VoiceVox_Voices.json");

    private static readonly string DumpPath =
        Path.Combine(BepInEx.Paths.ConfigPath, "EndKnot_VoiceVox_Speakers.txt");

    // Overrides: (friendCode, name, profile). friendCode takes priority if non-empty.
    internal static List<(string friendCode, string name, VoiceProfile profile)> Overrides { get; private set; } = [];

    // Explicit auto-assign pool from config. If empty, falls back to availableStyleIds from /speakers.
    internal static List<int> DefaultPool { get; private set; } = [];

    private const string DefaultConfigTemplate = """
{
  "overrides": [
    {
      "friendCode": "",
      "name": "ずんだ好き",
      "styleId": 3,
      "speed": 1.0,
      "pitch": 0.0,
      "intonation": 1.0,
      "volume": 1.0
    }
  ],
  "defaultPool": []
}
""";

    static VoiceVoxVoiceConfig()
    {
        if (!File.Exists(ConfigPath))
        {
            try { File.WriteAllText(ConfigPath, DefaultConfigTemplate, Encoding.UTF8); }
            catch (Exception ex) { Logger.Warn($"Could not create config: {ex.Message}", "VoiceVoxVoiceConfig"); }
        }
        Load();
    }

    public static void ReloadFromDisk() => Load();

    private static void Load()
    {
        var newOverrides = new List<(string, string, VoiceProfile)>();
        var newPool = new List<int>();
        try
        {
            if (!File.Exists(ConfigPath)) return;
            string text = File.ReadAllText(ConfigPath, Encoding.UTF8);
            var root = JsonNode.Parse(text);
            if (root == null) return;

            var overridesNode = root["overrides"]?.AsArray();
            if (overridesNode != null)
            {
                foreach (var o in overridesNode)
                {
                    if (o == null) continue;
                    string fc = o["friendCode"]?.GetValue<string>() ?? "";
                    string nm = o["name"]?.GetValue<string>() ?? "";
                    int sid = o["styleId"]?.GetValue<int>() ?? 3;
                    float speed = o["speed"]?.GetValue<float>() ?? 1.0f;
                    float pitch = o["pitch"]?.GetValue<float>() ?? 0.0f;
                    float inton = o["intonation"]?.GetValue<float>() ?? 1.0f;
                    float vol = o["volume"]?.GetValue<float>() ?? 1.0f;
                    newOverrides.Add((fc, nm, new VoiceProfile
                    {
                        StyleId = sid, Speed = speed, Pitch = pitch,
                        Intonation = inton, Volume = vol
                    }));
                }
            }

            var poolNode = root["defaultPool"]?.AsArray();
            if (poolNode != null)
            {
                foreach (var p in poolNode)
                {
                    if (p != null) newPool.Add(p.GetValue<int>());
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Config load error: {ex.Message}", "VoiceVoxVoiceConfig");
        }
        Overrides = newOverrides;
        DefaultPool = newPool;
    }

    // Write speaker discovery file so streamers can look up style ids for the override config.
    public static void WriteSpeakerDump(List<(int id, string label)> speakers)
    {
        try
        {
            using var sw = new StreamWriter(DumpPath, false, Encoding.UTF8);
            foreach (var (id, label) in speakers)
                sw.WriteLine($"{label}: {id}");
            Logger.Info($"Speaker dump written to {DumpPath}", "VoiceVoxVoiceConfig");
        }
        catch (Exception ex)
        {
            Logger.Warn($"WriteSpeakerDump error: {ex.Message}", "VoiceVoxVoiceConfig");
        }
    }

    public static int[] GetHardcodedPool() => HardcodedPool;
    public static string GetDumpPath() => DumpPath;
}
