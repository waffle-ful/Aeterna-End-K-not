using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace EndKnot.Modules.VoiceVox;

// Worker-thread only. Returns managed types (byte[], List) — no IL2CPP/Unity objects.
internal static class VoiceVoxFetcher
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(10) };

    // Two-step synthesis: audio_query (empty body) → patch JSON → synthesis → WAV bytes.
    // Returns null on any error or non-success HTTP status (caller skips + logs; never throws).
    public static async Task<byte[]> SynthesizeAsync(string text, VoiceProfile p, string engineUrl)
    {
        try
        {
            // Step 1: POST /audio_query (empty body; text and speaker are query params)
            string queryUrl = $"{engineUrl}/audio_query?text={Uri.EscapeDataString(text)}&speaker={p.StyleId}";
            using var queryResp = await Client.PostAsync(queryUrl, null);
            if (!queryResp.IsSuccessStatusCode)
            {
                Logger.Warn($"audio_query failed: HTTP {(int)queryResp.StatusCode} styleId={p.StyleId}", "VoiceVoxFetcher");
                return null;
            }

            string queryJson = await queryResp.Content.ReadAsStringAsync();

            // Step 2: patch the AudioQuery JSON scale fields
            var node = JsonNode.Parse(queryJson);
            if (node == null) return null;
            node["speedScale"] = p.Speed;
            node["pitchScale"] = p.Pitch;
            node["intonationScale"] = p.Intonation;
            node["volumeScale"] = p.Volume;
            string patchedJson = node.ToJsonString();

            // Step 3: POST /synthesis with patched AudioQuery JSON
            string synthUrl = $"{engineUrl}/synthesis?speaker={p.StyleId}";
            using var bodyContent = new StringContent(patchedJson, Encoding.UTF8, "application/json");
            using var synthResp = await Client.PostAsync(synthUrl, bodyContent);
            if (!synthResp.IsSuccessStatusCode)
            {
                Logger.Warn($"synthesis failed: HTTP {(int)synthResp.StatusCode} styleId={p.StyleId}", "VoiceVoxFetcher");
                return null;
            }

            return await synthResp.Content.ReadAsByteArrayAsync();
        }
        catch (Exception ex)
        {
            Logger.Warn($"SynthesizeAsync error: {ex.Message}", "VoiceVoxFetcher");
            return null;
        }
    }

    // GET /speakers → flatten talk-type styles into (id, "name（style）") list.
    public static async Task<List<(int id, string label)>> GetSpeakersAsync(string engineUrl)
    {
        var result = new List<(int, string)>();
        try
        {
            using var resp = await Client.GetAsync($"{engineUrl}/speakers");
            if (!resp.IsSuccessStatusCode) return result;

            string json = await resp.Content.ReadAsStringAsync();
            var root = JsonNode.Parse(json)?.AsArray();
            if (root == null) return result;

            foreach (var speaker in root)
            {
                string name = speaker?["name"]?.GetValue<string>() ?? "";
                var styles = speaker?["styles"]?.AsArray();
                if (styles == null) continue;
                foreach (var style in styles)
                {
                    string typeName = style?["type"]?.GetValue<string>() ?? "";
                    if (typeName != "talk") continue;
                    int id = style?["id"]?.GetValue<int>() ?? -1;
                    string styleName = style?["name"]?.GetValue<string>() ?? "";
                    if (id >= 0) result.Add((id, $"{name}（{styleName}）"));
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"GetSpeakersAsync error: {ex.Message}", "VoiceVoxFetcher");
        }
        return result;
    }
}
