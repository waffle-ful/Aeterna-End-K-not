using System.Collections.Generic;

// Data model for user-defined spawn points. This folder depends only on Unity/.NET and on
// ISpawnEditorHost, so it can be dropped into another mod as-is.
namespace SpawnEditorKit;

public sealed class SpawnPoint
{
    public const int MinWeight = 1;
    public const int MaxWeight = 10;

    // Weight of one built-in room when custom points are mixed with the default rooms.
    public const int DefaultRoomWeight = 5;

    public string Name { get; set; } = "";
    public int Color { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public int Weight { get; set; } = DefaultRoomWeight;
    public bool Enabled { get; set; } = true;
}

public sealed class SpawnMapPoints
{
    public List<SpawnPoint> Points { get; set; } = [];
}

public sealed class SpawnPreset
{
    public string Name { get; set; } = "";

    // Key = vanilla map id (0 Skeld, 1 Mira, 2 Polus, 4 Airship, 5 Fungle).
    public Dictionary<int, SpawnMapPoints> Maps { get; set; } = [];

    public List<SpawnPoint> GetPoints(int mapId, bool create)
    {
        if (Maps.TryGetValue(mapId, out SpawnMapPoints map)) return map.Points;
        if (!create) return null;
        map = new SpawnMapPoints();
        Maps[mapId] = map;
        return map.Points;
    }
}

public sealed class SpawnLibrary
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public int CurrentPreset { get; set; }
    public List<SpawnPreset> Presets { get; set; } = [];

    public SpawnPreset Current
    {
        get
        {
            if (Presets.Count == 0) Presets.Add(new SpawnPreset { Name = "1" });
            if (CurrentPreset < 0 || CurrentPreset >= Presets.Count) CurrentPreset = 0;
            return Presets[CurrentPreset];
        }
    }
}

public static class SpawnPalette
{
    // Marker colors; points store an index so the file stays readable and theme-independent.
    public static readonly (byte r, byte g, byte b)[] Colors =
    [
        (255, 255, 255),
        (255, 92, 92),
        (255, 168, 64),
        (255, 226, 72),
        (110, 230, 110),
        (80, 220, 230),
        (90, 140, 255),
        (190, 110, 255),
        (255, 120, 200)
    ];

    public static int Wrap(int index) => ((index % Colors.Length) + Colors.Length) % Colors.Length;
}
