using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace EndKnot.Modules;

// Phase 0: ロビーシーンの ShipOnly collider を診断 / トグル
// 後続 Phase で生成/配置/TP も同モジュールに統合予定
public static class BackroomsLobby
{
    private static readonly List<Collider2D> DisabledColliders = [];

    public static void DumpLobbyColliders(byte targetPid)
    {
        if (LobbyBehaviour.Instance == null)
        {
            Utils.SendMessage("Not in lobby", targetPid);
            return;
        }

        Collider2D[] colliders = LobbyBehaviour.Instance.GetComponentsInChildren<Collider2D>(true);
        int shipMask = Constants.ShipOnlyMask;

        StringBuilder sb = new();
        sb.AppendLine($"=== Backrooms Diag: {colliders.Length} colliders under LobbyBehaviour ===");

        int shipCount = 0;
        Dictionary<int, int> layerHist = [];

        foreach (Collider2D c in colliders)
        {
            int layer = c.gameObject.layer;
            bool isShip = (shipMask & (1 << layer)) != 0;
            if (isShip) shipCount++;

            layerHist.TryGetValue(layer, out int n);
            layerHist[layer] = n + 1;

            sb.AppendLine($"{c.gameObject.name} | L{layer} ({LayerMask.LayerToName(layer)}) | {c.GetType().Name} | en={c.enabled} | ship={isShip}");
        }

        sb.AppendLine("--- Layer histogram ---");
        foreach ((int layer, int n) in layerHist)
            sb.AppendLine($"L{layer} ({LayerMask.LayerToName(layer)}): {n}");

        sb.AppendLine($"ShipOnlyMask = 0x{shipMask:X8}");
        sb.AppendLine($"Total ShipOnly = {shipCount}");

        Logger.Info(sb.ToString(), "BackroomsDiag");
        Utils.SendMessage($"Dumped {colliders.Length} colliders ({shipCount} ShipOnly). See log.", targetPid);
    }

    public static void ToggleShipColliders(byte targetPid)
    {
        if (LobbyBehaviour.Instance == null)
        {
            Utils.SendMessage("Not in lobby", targetPid);
            return;
        }

        if (DisabledColliders.Count > 0)
        {
            int restored = 0;
            foreach (Collider2D c in DisabledColliders)
            {
                if (c == null) continue;
                c.enabled = true;
                restored++;
            }

            Utils.SendMessage($"Restored {restored} colliders.", targetPid);
            Logger.Info($"Restored {restored} ShipOnly colliders", "BackroomsDiag");
            DisabledColliders.Clear();
            return;
        }

        Collider2D[] colliders = LobbyBehaviour.Instance.GetComponentsInChildren<Collider2D>(true);
        int shipMask = Constants.ShipOnlyMask;

        foreach (Collider2D c in colliders)
        {
            int layer = c.gameObject.layer;
            bool isShip = (shipMask & (1 << layer)) != 0;
            if (!isShip || !c.enabled) continue;
            c.enabled = false;
            DisabledColliders.Add(c);
        }

        Utils.SendMessage($"Disabled {DisabledColliders.Count} ShipOnly colliders.", targetPid);
        Logger.Info($"Disabled {DisabledColliders.Count} ShipOnly colliders", "BackroomsDiag");
    }

    // Phase 1: タイルスポーン API
    // Phase 5 で BaselineSprite を実 PNG (Utils.LoadSprite) に置換予定

    private static readonly List<GameObject> SpawnedTiles = [];
    private static Sprite _baselineSprite;
    private static Sprite _wallSpriteH;
    private static Sprite _wallSpriteV;

    private static Sprite BaselineSprite
    {
        get
        {
            if (_baselineSprite != null) return _baselineSprite;
            Texture2D tex = new(4, 4, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
            Color[] pixels = new Color[16];
            for (int i = 0; i < 16; i++) pixels[i] = Color.white;
            tex.SetPixels(pixels);
            tex.Apply();
            _baselineSprite = Sprite.Create(tex, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 4f);
            _baselineSprite.hideFlags |= HideFlags.HideAndDontSave;
            return _baselineSprite;
        }
    }

    // 横長壁 (床と接する面): 壁紙テクスチャ + 上端 ~19% を暗くして「厚み」を演出
    private static Sprite WallSpriteH
    {
        get
        {
            if (_wallSpriteH != null) return _wallSpriteH;
            const int N = 16;
            Texture2D tex = new(N, N, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
            Color body = new(0.85f, 0.65f, 0.25f);
            Color depth = new(0.06f, 0.03f, 0.01f);
            Color[] pixels = new Color[N * N];
            for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
                pixels[y * N + x] = y >= N - 3 ? depth : body; // 上 3 列を厚みに
            tex.SetPixels(pixels);
            tex.Apply();
            _wallSpriteH = Sprite.Create(tex, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f), N);
            _wallSpriteH.hideFlags |= HideFlags.HideAndDontSave;
            return _wallSpriteH;
        }
    }

    // 縦長壁 (部屋の左右端): 床色背景 + 中央に細い暗線 (= 壁の厚み断面)
    private static Sprite WallSpriteV
    {
        get
        {
            if (_wallSpriteV != null) return _wallSpriteV;
            const int N = 16;
            Texture2D tex = new(N, N, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
            Color floor = new(0.35f, 0.22f, 0.10f);
            Color edge = new(0.06f, 0.03f, 0.01f);
            Color[] pixels = new Color[N * N];
            for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                bool isLine = x == N / 2 - 1 || x == N / 2; // 中央 2 列を暗線に
                pixels[y * N + x] = isLine ? edge : floor;
            }
            tex.SetPixels(pixels);
            tex.Apply();
            _wallSpriteV = Sprite.Create(tex, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f), N);
            _wallSpriteV.hideFlags |= HideFlags.HideAndDontSave;
            return _wallSpriteV;
        }
    }

    private static Color GetTileColor(string kind) => kind switch
    {
        "wall"    => new Color(0.85f, 0.65f, 0.25f),
        "floor"   => new Color(0.35f, 0.22f, 0.10f),
        "ceiling" => new Color(0.55f, 0.55f, 0.55f),
        "light"   => new Color(1.00f, 0.95f, 0.60f),
        "door"    => new Color(0.45f, 0.30f, 0.15f),
        "corner"  => new Color(0.75f, 0.55f, 0.20f),
        "stain"   => new Color(0.25f, 0.15f, 0.05f),
        "vent"    => new Color(0.40f, 0.40f, 0.40f),
        _         => Color.magenta
    };

    private static int GetSortingOrder(string kind) => kind switch
    {
        "floor" or "stain" => -10,
        "ceiling"          => 10,
        "light"            => 5,
        _                  => 0
    };

    public static GameObject SpawnTile(string kind, Vector2 pos, float scale = 1f)
    {
        GameObject go = new($"BackroomsTile_{kind}");
        go.transform.SetParent(null, false);
        go.transform.position = new Vector3(pos.x, pos.y, 0f);
        go.transform.localScale = new Vector3(scale, scale, 1f);

        SpriteRenderer sr = go.AddComponent<SpriteRenderer>();

        switch (kind)
        {
            case "wall":   // 後方互換 (test pattern 用)
            case "wall_h":
                sr.sprite = WallSpriteH;
                sr.color = Color.white;
                break;
            case "wall_v":
                sr.sprite = WallSpriteV;
                sr.color = Color.white;
                break;
            default:
                sr.sprite = BaselineSprite;
                sr.color = GetTileColor(kind);
                break;
        }

        sr.sortingOrder = GetSortingOrder(kind);

        SpawnedTiles.Add(go);
        return go;
    }

    public static void SpawnTestPattern(byte targetPid)
    {
        if (LobbyBehaviour.Instance == null)
        {
            Utils.SendMessage("Not in lobby", targetPid);
            return;
        }

        if (PlayerControl.LocalPlayer == null) return;

        Vector2 origin = PlayerControl.LocalPlayer.Pos();

        // 3x3 grid: 中央 floor + 4辺 wall + 4角 corner
        string[,] pattern =
        {
            { "corner", "wall",  "corner" },
            { "wall",   "floor", "wall"   },
            { "corner", "wall",  "corner" }
        };

        for (int y = 0; y < 3; y++)
        for (int x = 0; x < 3; x++)
        {
            Vector2 pos = origin + new Vector2(x - 1, -(y - 1));
            SpawnTile(pattern[y, x], pos);
        }

        SpawnTile("light",   origin + new Vector2(0f, 2.5f));
        SpawnTile("door",    origin + new Vector2(3f, 0f));
        SpawnTile("stain",   origin + new Vector2(-2f, -1.5f));
        SpawnTile("ceiling", origin + new Vector2(0f, -3f));
        SpawnTile("vent",    origin + new Vector2(2f, 2f));

        Utils.SendMessage($"Spawned {SpawnedTiles.Count} test tiles around {origin}", targetPid);
        Logger.Info($"SpawnTestPattern at {origin}, total={SpawnedTiles.Count}", "BackroomsDiag");
    }

    public static void ClearTiles(byte targetPid)
    {
        int cleared = 0;
        foreach (GameObject go in SpawnedTiles)
        {
            if (go == null) continue;
            Object.Destroy(go);
            cleared++;
        }

        SpawnedTiles.Clear();
        Utils.SendMessage($"Cleared {cleared} tiles.", targetPid);
        Logger.Info($"Cleared {cleared} tiles", "BackroomsDiag");
    }

    // Phase 2: Seeded chunk procgen
    // Backrooms 風: 部屋境界に決定論的 opening、それ以外は壁

    private const int ChunkSize = 16;
    private const int GenerationRadius = 1; // 3x3 = 9 チャンク = 2304 タイル
    private const int RoomSize = 6;

    public static void GenerateLobby(uint seed, byte targetPid)
    {
        if (LobbyBehaviour.Instance == null)
        {
            Utils.SendMessage("Not in lobby", targetPid);
            return;
        }

        if (PlayerControl.LocalPlayer == null) return;

        // 既存タイル全消去 (procgen と test pattern を同じ list で管理)
        int wiped = 0;
        foreach (GameObject go in SpawnedTiles)
        {
            if (go == null) continue;
            Object.Destroy(go);
            wiped++;
        }

        SpawnedTiles.Clear();

        Vector2 origin = PlayerControl.LocalPlayer.Pos();
        int centerCx = Mathf.FloorToInt(origin.x / ChunkSize);
        int centerCy = Mathf.FloorToInt(origin.y / ChunkSize);

        int generated = 0;
        for (int dcx = -GenerationRadius; dcx <= GenerationRadius; dcx++)
        for (int dcy = -GenerationRadius; dcy <= GenerationRadius; dcy++)
            generated += GenerateChunk(centerCx + dcx, centerCy + dcy, seed);

        int chunkCount = (GenerationRadius * 2 + 1) * (GenerationRadius * 2 + 1);
        Utils.SendMessage($"Gen seed={seed}: wiped {wiped}, generated {generated} tiles in {chunkCount} chunks around ({centerCx},{centerCy})", targetPid);
        Logger.Info($"GenerateLobby seed={seed} chunks={chunkCount} tiles={generated} center=({centerCx},{centerCy})", "BackroomsGen");
    }

    private enum CellKind { Floor, WallH, WallV }

    private static int GenerateChunk(int cx, int cy, uint seed)
    {
        int count = 0;
        int baseX = cx * ChunkSize;
        int baseY = cy * ChunkSize;

        for (int lx = 0; lx < ChunkSize; lx++)
        for (int ly = 0; ly < ChunkSize; ly++)
        {
            int wx = baseX + lx;
            int wy = baseY + ly;
            string kind = ClassifyCell(wx, wy, seed) switch
            {
                CellKind.WallH => "wall_h",
                CellKind.WallV => "wall_v",
                _              => "floor"
            };
            SpawnTile(kind, new Vector2(wx, wy));
            count++;
        }

        return count;
    }

    private static CellKind ClassifyCell(int wx, int wy, uint seed)
    {
        int inRoomX = Mod(wx, RoomSize);
        int inRoomY = Mod(wy, RoomSize);
        int roomX = (wx - inRoomX) / RoomSize;
        int roomY = (wy - inRoomY) / RoomSize;

        bool onLeftBorder = inRoomX == 0;
        bool onBottomBorder = inRoomY == 0;

        if (!onLeftBorder && !onBottomBorder) return CellKind.Floor;

        if (onLeftBorder)
        {
            uint h = WallHash(roomX, roomY, seed, 'V');
            int openingY = (int)(h % (uint)(RoomSize - 2)) + 1; // [1..RoomSize-2]
            if (inRoomY == openingY) return CellKind.Floor;
        }

        if (onBottomBorder)
        {
            uint h = WallHash(roomX, roomY, seed, 'H');
            int openingX = (int)(h % (uint)(RoomSize - 2)) + 1;
            if (inRoomX == openingX) return CellKind.Floor;
        }

        // 横壁 (床と接する面) を優先 — 角もこちらで描画される
        if (onBottomBorder) return CellKind.WallH;
        return CellKind.WallV;
    }

    private static int Mod(int a, int n) => ((a % n) + n) % n;

    private static uint WallHash(int a, int b, uint seed, char tag)
    {
        uint h = seed;
        h = (h ^ unchecked((uint)a)) * 16777619u;
        h = (h ^ unchecked((uint)b)) * 16777619u;
        h = (h ^ tag) * 16777619u;
        return h;
    }
}
