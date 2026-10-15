using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SpawnEditorKit;

// One map's points as a JSON file in a shared folder, for handing a layout to another player.
public static class SpawnShareFile
{
    private const int MaxPoints = 200;
    private const int MaxFiles = 30;
    private const int MaxScanned = 100;
    private const long MaxBytes = 256 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private sealed class Payload
    {
        public int Version { get; set; } = 1;
        public int Map { get; set; }
        public List<SpawnPoint> Points { get; set; } = [];
    }

    public static string MapName(int mapId) => mapId switch
    {
        0 => "Skeld",
        1 => "MiraHQ",
        2 => "Polus",
        4 => "Airship",
        5 => "Fungle",
        _ => $"Map{mapId}"
    };

    // Writes the file and returns its name.
    public static string Export(string folder, int mapId, List<SpawnPoint> points)
    {
        Directory.CreateDirectory(folder);
        string name = $"{MapName(mapId)}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.json";
        string json = JsonSerializer.Serialize(new Payload { Map = mapId, Points = points }, JsonOptions);
        File.WriteAllText(Path.Combine(folder, name), json);
        return name;
    }

    // Files in the folder that hold points for this map, newest first.
    public static List<string> List(string folder, int mapId)
    {
        var result = new List<string>();
        if (!Directory.Exists(folder)) return result;

        var files = new List<FileInfo>(new DirectoryInfo(folder).GetFiles("*.json"));
        files.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));

        for (int i = 0; i < files.Count && i < MaxScanned; i++)
        {
            FileInfo file = files[i];
            if (result.Count >= MaxFiles) break;
            if (Read(file.FullName, mapId) != null) result.Add(file.FullName);
        }

        return result;
    }

    // Points of a file, or null when it is not a spawn file for this map.
    public static List<SpawnPoint> Read(string path, int mapId)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxBytes) return null;

            var payload = JsonSerializer.Deserialize<Payload>(File.ReadAllText(path));
            if (payload?.Points == null || payload.Map != mapId) return null;

            var points = new List<SpawnPoint>();
            foreach (SpawnPoint p in payload.Points)
            {
                if (p == null || !float.IsFinite(p.X) || !float.IsFinite(p.Y)) continue;
                p.Name = (p.Name ?? "").Replace("\n", " ").Replace("\r", " ").Replace('<', '＜');
                if (p.Name.Length > 40) p.Name = p.Name[..40];
                p.Color = SpawnPalette.Wrap(p.Color);
                p.Weight = Math.Clamp(p.Weight, SpawnPoint.MinWeight, SpawnPoint.MaxWeight);
                points.Add(p);
                if (points.Count >= MaxPoints) break;
            }

            return points;
        }
        catch { return null; }
    }
}
