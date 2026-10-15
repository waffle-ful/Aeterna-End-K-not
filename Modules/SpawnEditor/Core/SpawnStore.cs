using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SpawnEditorKit;

public static class SpawnStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static SpawnLibrary _library;
    private static ISpawnEditorHost _host;

    public static ISpawnEditorHost Host => _host;

    public static void Init(ISpawnEditorHost host) => _host = host;

    public static SpawnLibrary Library
    {
        get
        {
            if (_library == null) Load();
            return _library;
        }
    }

    public static void Load()
    {
        _library = null;

        try
        {
            string path = _host?.SavePath;

            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                string json = File.ReadAllText(path);
                if (json.Length > 0) _library = JsonSerializer.Deserialize<SpawnLibrary>(json, JsonOptions);
            }
        }
        catch (Exception e) { _host?.Warn($"Spawn points could not be read: {e.Message}"); }

        _library ??= new SpawnLibrary();
        Sanitize(_library);
    }

    public static void Save()
    {
        if (_library == null || _host == null) return;

        try
        {
            string path = _host.SavePath;
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // Serialize first and swap the file in, so a failure never leaves a truncated file behind.
            string json = JsonSerializer.Serialize(_library, JsonOptions);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, true);
        }
        catch (Exception e) { _host.Warn($"Spawn points could not be saved: {e.Message}"); }
    }

    // Enabled points of the current preset for a map, or null when there are none.
    public static List<SpawnPoint> GetEnabledPoints(int mapId)
    {
        List<SpawnPoint> points = Library.Current.GetPoints(mapId, false);
        if (points == null) return null;

        List<SpawnPoint> result = null;

        foreach (SpawnPoint p in points)
        {
            if (!p.Enabled) continue;
            (result ??= []).Add(p);
        }

        return result;
    }

    private static void Sanitize(SpawnLibrary lib)
    {
        lib.Presets ??= [];

        foreach (SpawnPreset preset in lib.Presets)
        {
            preset.Name ??= "";
            preset.Maps ??= [];

            foreach (SpawnMapPoints map in preset.Maps.Values)
            {
                map.Points ??= [];
                map.Points.RemoveAll(p => p == null || float.IsNaN(p.X) || float.IsNaN(p.Y) || float.IsInfinity(p.X) || float.IsInfinity(p.Y));

                foreach (SpawnPoint p in map.Points)
                {
                    p.Name ??= "";
                    p.Color = SpawnPalette.Wrap(p.Color);
                    p.Weight = Math.Clamp(p.Weight, SpawnPoint.MinWeight, SpawnPoint.MaxWeight);
                }
            }
        }

        _ = lib.Current;
    }
}

public static class SpawnPicker
{
    private static readonly Random Rng = new();

    // Weighted draw over the custom points plus (when mixing) the built-in rooms, each room weighing
    // SpawnPoint.DefaultRoomWeight. Returns false when a built-in room won the draw.
    public static bool TryPick(List<SpawnPoint> points, int defaultRoomCount, bool customOnly, out SpawnPoint picked)
    {
        picked = null;
        if (points == null || points.Count == 0) return false;

        int total = 0;
        foreach (SpawnPoint p in points) total += p.Weight;
        int defaults = customOnly ? 0 : Math.Max(0, defaultRoomCount) * SpawnPoint.DefaultRoomWeight;

        int roll = Rng.Next(total + defaults);
        if (roll >= total) return false;

        foreach (SpawnPoint p in points)
        {
            roll -= p.Weight;
            if (roll >= 0) continue;
            picked = p;
            return true;
        }

        picked = points[^1];
        return true;
    }
}
