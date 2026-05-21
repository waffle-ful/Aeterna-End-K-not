using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
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

    // 壁 AABB を spawn 時に cache (UpdateVision の毎フレーム GetComponent ストーム回避)
    // entry: (cx, cy, halfX, halfY) — 中心と半サイズ
    private static readonly List<(float cx, float cy, float halfX, float halfY)> WallAabbs = [];

    private static Sprite _baselineSprite;
    private static Sprite _wallSpriteH;

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

    // 壁の dark mass / 暗帯に使う黒系色。vanilla Skeld の wall back と同程度
    private static readonly Color WallDarkColor = new(0.04f, 0.03f, 0.01f);

    // WallV body の縁にのせる sharp edge highlight (Polus 風 3D outline)
    private static readonly Color WallEdgeHighlight = new(0.12f, 0.09f, 0.03f);

    // floor 色 (kind="floor" の GetTileColor と一致)。WallV cell 内側の床背景に使用
    private static readonly Color FloorBaseColor = new(0.35f, 0.22f, 0.10f);

    // wall.png PNG ロード — Resources/Images/Backrooms/wall.png があれば優先使用、なければ null fallback
    private static Sprite _wallPngSprite;
    private static bool _wallPngTried;
    private static Sprite WallPngSprite
    {
        get
        {
            if (_wallPngSprite != null) return _wallPngSprite;
            if (_wallPngTried) return null;
            _wallPngTried = true;
            try { _wallPngSprite = Utils.LoadSprite("EndKnot.Resources.Images.Backrooms.wall.png", 1024f); }
            catch { _wallPngSprite = null; }
            return _wallPngSprite;
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
        "floor" or "stain"             => -10,
        "wall" or "wall_h" or "wall_v" => -5, // 床より前、player より背面
        "ceiling"                      => 10,
        "light"                        => 5,
        _                              => 0
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
                // 親 SR は使わず、face (PNG/procedural) + 上端 dark band を子で描画。
                // dark band は立体感の源 — wall.png PNG だと texture に band が含まれないので
                // 別 sprite で必ず重ねる
                sr.enabled = false;
                BuildWallHComposite(go);
                BoxCollider2D boxH = go.AddComponent<BoxCollider2D>(); // 全面 1x1 で衝突
                boxH.size = Vector2.one; // 親 SR disable で auto-size=(0,0) になる罠を回避
                break;
            case "wall_v":
                // Polus 風 outlined dark mass: floor 背景 + 0.4 dark body + 両端 0.025 edge highlight
                sr.enabled = false;
                BuildWallV(go);
                BoxCollider2D colV = go.AddComponent<BoxCollider2D>();
                colV.size = new Vector2(0.45f, 1f); // body 0.4 + edges 0.025*2 = 0.45
                break;
            default:
                sr.sprite = BaselineSprite;
                sr.color = GetTileColor(kind);
                break;
        }

        sr.sortingOrder = GetSortingOrder(kind);

        SpawnedTiles.Add(go);

        // wall タイル限定で AABB を cache (UpdateVision で GetComponent 回避)
        if (kind is "wall" or "wall_h" or "wall_v")
        {
            BoxCollider2D bc = go.GetComponent<BoxCollider2D>();
            if (bc != null)
            {
                Vector2 c = (Vector2)go.transform.position + bc.offset;
                Vector3 ls = go.transform.localScale;
                float hx = bc.size.x * 0.5f * Mathf.Abs(ls.x);
                float hy = bc.size.y * 0.5f * Mathf.Abs(ls.y);
                WallAabbs.Add((c.x, c.y, hx, hy));

                // (vanilla shadow hijack 路線は 2026-05-21 dead — caster 追加無し)
            }
        }

        return go;
    }

    // WallH 合成: face (PNG 優先 / procedural fallback) + 上端 dark band を子で重ね描き
    // sortingOrder spec (player は約 0、wall children は player より奥 / body を覆わない):
    //   face = -5 (floor -10 より前、player より奥)
    //   variant body / connector main = -4 (face と top の間)
    //   variant edges / highlights = -3
    //   top dark band = -3 (band は最前面の中で重ねる)
    // 体が壁を覆う = "player が壁の前に立ってる" 表現。Among Us 正規の peek effect (head 突き出し)
    // は player の hat sortingOrder が触れないので諦め、player 全体が前面に来る方を優先
    private static void BuildWallHComposite(GameObject parent)
    {
        // 壁本体
        GameObject face = new("WallHFace");
        face.transform.SetParent(parent.transform, false);
        face.transform.localPosition = Vector3.zero;
        SpriteRenderer faceSr = face.AddComponent<SpriteRenderer>();
        faceSr.sprite = WallPngSprite ?? WallSpriteH;
        faceSr.color = Color.white;
        faceSr.sortingLayerName = "Default";
        faceSr.sortingOrder = -5;

        // 上端 20% の dark band (立体感の源)
        GameObject top = new("WallHTopBand");
        top.transform.SetParent(parent.transform, false);
        top.transform.localPosition = new Vector3(0f, 0.4f, 0f);
        top.transform.localScale = new Vector3(1f, 0.2f, 1f);
        SpriteRenderer topSr = top.AddComponent<SpriteRenderer>();
        topSr.sprite = BaselineSprite;
        topSr.color = WallDarkColor;
        topSr.sortingLayerName = "Default";
        topSr.sortingOrder = -3;
    }

    // WallH cell の南半分を直下の WallV outline で覆って、V column と上端 dark band を L 字に視覚連結
    //   V の outline (floor 背景なし) を 1x0.8 で重ね描き
    //   高さ = 0.8 (cell 下端 -0.5 から dark band 下端 +0.3 まで)、center localY = -0.1
    //   body + edge highlights が同比率で H 連結部に延びる
    public static void AddWallHBottomConnector(GameObject wallH)
    {
        if (wallH == null) return;

        GameObject conn = new("WallHBottomConnector");
        conn.transform.SetParent(wallH.transform, false);
        conn.transform.localPosition = new Vector3(0f, -0.1f, 0f);
        conn.transform.localScale = new Vector3(1f, 0.8f, 1f);
        BuildWallVOutline(conn);
    }

    // cell 全面に procedural floor 背景を敷く (kind="floor" と同じ手触り)
    private static void DrawFloorBackground(GameObject parent, int sortingOrder = -10)
    {
        GameObject floor = new("Floor");
        floor.transform.SetParent(parent.transform, false);
        floor.transform.localPosition = Vector3.zero;
        SpriteRenderer sr = floor.AddComponent<SpriteRenderer>();
        sr.sprite = BaselineSprite;
        sr.color = FloorBaseColor;
        sr.sortingLayerName = "Default";
        sr.sortingOrder = sortingOrder;
    }

    // Polus 風 outlined dark mass: floor bg + 0.4 wide dark body + 両端 0.025 wide lighter edge highlights
    // sharp outline こそ Polus の壁が立体に見える核
    private static void BuildWallV(GameObject parent)
    {
        DrawFloorBackground(parent);
        BuildWallVOutline(parent);
    }

    private static void BuildWallVOutline(GameObject parent)
    {
        // Body: 0.4 wide dark mass
        GameObject body = new("WallVBody");
        body.transform.SetParent(parent.transform, false);
        body.transform.localPosition = Vector3.zero;
        body.transform.localScale = new Vector3(0.4f, 1f, 1f);
        SpriteRenderer bsr = body.AddComponent<SpriteRenderer>();
        bsr.sprite = BaselineSprite;
        bsr.color = WallDarkColor;
        bsr.sortingLayerName = "Default";
        bsr.sortingOrder = -4;

        // West edge highlight: 0.025 wide, slightly lighter than body
        GameObject west = new("WallVWestEdge");
        west.transform.SetParent(parent.transform, false);
        west.transform.localPosition = new Vector3(-0.2125f, 0f, 0f);
        west.transform.localScale = new Vector3(0.025f, 1f, 1f);
        SpriteRenderer wsr = west.AddComponent<SpriteRenderer>();
        wsr.sprite = BaselineSprite;
        wsr.color = WallEdgeHighlight;
        wsr.sortingLayerName = "Default";
        wsr.sortingOrder = -3;

        // East edge highlight
        GameObject east = new("WallVEastEdge");
        east.transform.SetParent(parent.transform, false);
        east.transform.localPosition = new Vector3(+0.2125f, 0f, 0f);
        east.transform.localScale = new Vector3(0.025f, 1f, 1f);
        SpriteRenderer esr = east.AddComponent<SpriteRenderer>();
        esr.sprite = BaselineSprite;
        esr.color = WallEdgeHighlight;
        esr.sortingLayerName = "Default";
        esr.sortingOrder = -3;
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
        WallAabbs.Clear();
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
        WallAabbs.Clear();

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
            CellKind cell = ClassifyCell(wx, wy, seed);
            string kind = cell switch
            {
                CellKind.WallH => "wall_h",
                CellKind.WallV => "wall_v",
                _              => "floor"
            };
            GameObject go = SpawnTile(kind, new Vector2(wx, wy));

            if (cell == CellKind.WallH)
            {
                CellKind south = ClassifyCell(wx, wy - 1, seed);
                // 真下が WallV なら L 字 connector で V column と上端 dark band を連結
                if (south == CellKind.WallV) AddWallHBottomConnector(go);
            }

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

    // Phase 3: Backrooms 入退場 (Matrix 構造の心臓部)
    // モッド client は遠隔座標 (BackroomsX, BackroomsY) に物理移動する
    // 非モッド client から見るとホストが通常ロビーの外へフェードアウト → Backrooms 内部の動きは観測不能

    private const float BackroomsX = 100f;
    private const float BackroomsY = 100f;

    // vanilla shadow hijack は 2026-05-21 dead (lobby で LightSource activation 不可、SetupLightingForGameplay
    // が ShipStatus 依存で NRE)。Layer 10 const は /bblightprobe diag で参照のため残置
    private const int ShadowLayer = 10;

    private static bool _inBackrooms;

    public static void EnterBackrooms(uint seed, byte targetPid)
    {
        if (LobbyBehaviour.Instance == null)
        {
            Utils.SendMessage("Not in lobby", targetPid);
            return;
        }

        if (PlayerControl.LocalPlayer == null) return;

        // 1. ロビー collider を disable (見えない壁を消して移動自由化)
        int disabled = 0;
        if (DisabledColliders.Count == 0)
        {
            Collider2D[] colliders = LobbyBehaviour.Instance.GetComponentsInChildren<Collider2D>(true);
            int shipMask = Constants.ShipOnlyMask;
            foreach (Collider2D c in colliders)
            {
                int layer = c.gameObject.layer;
                bool isShip = (shipMask & (1 << layer)) != 0;
                if (!isShip || !c.enabled) continue;
                c.enabled = false;
                DisabledColliders.Add(c);
                disabled++;
            }
        }

        // 2. Backrooms 座標へ瞬間移動 (Utils.TP は EAC 例外登録済)
        PlayerControl.LocalPlayer.TP(new Vector2(BackroomsX, BackroomsY));

        // 3. 新座標を中心に procgen 生成
        GenerateLobby(seed, targetPid);

        // 4. custom mesh 視界システム起動 (vanilla hijack 路線は dead — reference 参照)
        CreateVision();
        _visionPaused = false;
        _inBackrooms = true;

        Logger.Info($"Entered Backrooms at ({BackroomsX},{BackroomsY}) seed={seed} disabled={disabled}", "BackroomsGen");
    }

    public static void ExitBackrooms(byte targetPid)
    {
        if (LobbyBehaviour.Instance == null)
        {
            Utils.SendMessage("Not in lobby", targetPid);
            return;
        }

        if (PlayerControl.LocalPlayer == null) return;

        // 0. custom mesh 視界システム停止
        _inBackrooms = false;
        _visionPaused = false;
        DestroyVision();

        // 1. ロビー中央へ帰還 TP
        PlayerControl.LocalPlayer.TP(new Vector2(0f, 0f));

        // 2. Backrooms タイル全消去
        int wiped = 0;
        foreach (GameObject go in SpawnedTiles)
        {
            if (go == null) continue;
            Object.Destroy(go);
            wiped++;
        }

        SpawnedTiles.Clear();
        WallAabbs.Clear();

        // 3. ロビー collider 復元
        int restored = 0;
        foreach (Collider2D c in DisabledColliders)
        {
            if (c == null) continue;
            c.enabled = true;
            restored++;
        }

        DisabledColliders.Clear();

        Utils.SendMessage($"Exited Backrooms. Cleared {wiped} tiles, restored {restored} colliders.", targetPid);
        Logger.Info($"Exited Backrooms cleared={wiped} restored={restored}", "BackroomsGen");
    }

    // Phase 4: 視界システム (corner-based polygon raycast — YouTube short / doc/amongusfog.md 通り)
    //   1. spawn 時 cache 済み WallAabbs から VisionRadius 圏内を pre-filter
    //   2. 8 cardinal 保険レイ + 各 wall corner に ±ε / 直接の 3 本レイ
    //   3. (ray 角度, hit 距離) を angle 順にソート
    //   4. (inner @ hit_dist, outer @ DarkRadius) の donut mesh をハードエッジ・solid black で構築
    //   5. mesh は player に追従、List ベース buffers で variable size に対応 (Mesh.SetVertices は internal buf 再利用)
    //   ※ 直接ヒット点から direction を逆算するのは NG (inside-AABB で hit≈player → atan2(0,0)=0 で
    //      全ヒット同一方向に collapse → 一方向だけ見える縮退多角形バグ)。angle 自体を保持して sort/build に使う

    private const float VisionRadius = 8f;     // 可視半径
    // ray dist のフロア値 — player radius (~0.3) 程度。これより大きいと「触れた壁の先が見える」バグ発生
    // (polygon 内側が壁を超えて player から離れて配置されるため、壁の向こうの floor が dark mesh で覆われない)
    private const float MinHitDistance = 0.3f;
    private const float DarkRadius = 60f;      // dark mesh 外周
    // 360 ray fan (1° 刻み)。Mesh.SetVertices + static bounds で marshal/recalculate コスト削減
    // を試す。vanilla hijack は 2026-05-21 dead (lobby で LightSource activation 不可、reference 参照)
    private const int RayFanCount = 360;

    private static GameObject _visionGO;
    private static MeshFilter _visionMF;
    private static Mesh _visionMesh;
    private static Material _visionMat;

    private static readonly List<(float cx, float cy, float halfX, float halfY)> _nearbyAabbs = new(64);

    // 静的事前計算バッファ — RayFanCount は const なので 1 度組んだら触らない
    //   _rayCos/Sin: 毎フレーム 720 回の Mathf.Cos/Sin を回避
    //   _vertsBuf  : 毎フレーム new Vector3[720] を回避 (GC 圧軽減)
    //   _trisBuf   : 三角形 index は topology 由来で完全に不変 — 1 度 SetTriangles すれば永久
    //   outer ring (i+N) も Vec3 が固定なので pre-fill
    private static readonly float[] _rayCos = new float[RayFanCount];
    private static readonly float[] _raySin = new float[RayFanCount];
    private static readonly Vector3[] _vertsBuf = new Vector3[2 * RayFanCount];
    private static readonly int[] _trisBuf = new int[6 * RayFanCount];
    private static bool _staticBuffersBuilt;
    private static bool _trisUploaded;

    private static void CreateVision()
    {
        if (_visionGO != null) return;

        _visionGO = new GameObject("BackroomsVision");
        _visionGO.transform.SetParent(null);
        _visionGO.transform.position = new Vector3(BackroomsX, BackroomsY, 0f);

        _visionMF = _visionGO.AddComponent<MeshFilter>();
        _visionMesh = new Mesh { name = "BackroomsVisionMesh" };
        _visionMesh.MarkDynamic();
        // static bounds で per-frame RecalculateBounds を省略。DarkRadius を完全に覆う AABB
        _visionMesh.bounds = new Bounds(Vector3.zero, new Vector3(2f * DarkRadius, 2f * DarkRadius, 1f));
        _visionMF.mesh = _visionMesh;

        MeshRenderer mr = _visionGO.AddComponent<MeshRenderer>();

        // shader 取得: Sprites/Default → stripped 時は既存タイルの material からコピー
        // 色: 完全黒 alpha 1.0 — 「Backrooms らしさ」のソリッド黒。壁は sortingOrder で dark の上に来るので隠れない
        Material src = BorrowTileMaterialOrNull();
        Shader sd = Shader.Find("Sprites/Default");
        if (sd != null)
            _visionMat = new Material(sd) { color = Color.black };
        else if (src != null)
            _visionMat = new Material(src) { color = Color.black };
        else
        {
            Logger.Error("Vision: Sprites/Default not found AND no tile material to borrow — mesh will be invisible", "BackroomsGen");
            _visionMat = new Material(Shader.Find("Hidden/Internal-Colored")) { color = Color.black };
        }

        mr.material = _visionMat;
        mr.sortingLayerName = "Default";
        // sortingOrder spec: floor=-10 < dark mesh=-7 < walls=-5/-4/-3 < player=0
        //   → dark は floor を覆う、walls / player は dark の上に描画されて常に可視
        //   この設定こそが「壁が影で消える」「player 周りに謎の影」の本質的解決
        mr.sortingOrder = -7;

        Logger.Info($"Vision created: shader='{_visionMat.shader?.name}' sortingLayer='{mr.sortingLayerName}' order={mr.sortingOrder} worldPos={_visionGO.transform.position} layer={_visionGO.layer}", "BackroomsGen");
    }

    // 既に画面に出ている (= 描画経路 OK な) SpriteRenderer から shared material を借りる fallback
    private static Material BorrowTileMaterialOrNull()
    {
        foreach (GameObject go in SpawnedTiles)
        {
            if (go == null) continue;
            SpriteRenderer sr = go.GetComponentInChildren<SpriteRenderer>(true);
            if (sr != null && sr.sharedMaterial != null) return sr.sharedMaterial;
        }
        return null;
    }

    private static void DestroyVision()
    {
        if (_visionGO == null) return;
        UnityEngine.Object.Destroy(_visionGO);
        _visionGO = null;
        _visionMF = null;
        _visionMesh = null;
        _visionMat = null;
        _trisUploaded = false; // 次回 CreateVision 時に再アップロード必要
        Logger.Info("Vision destroyed", "BackroomsGen");
    }

    private static void EnsureStaticBuffers()
    {
        if (_staticBuffersBuilt) return;
        const float twoPi = Mathf.PI * 2f;
        int n = RayFanCount;
        for (int i = 0; i < n; i++)
        {
            float ang = (i / (float)n) * twoPi;
            float cos = Mathf.Cos(ang);
            float sin = Mathf.Sin(ang);
            _rayCos[i] = cos;
            _raySin[i] = sin;
            // outer ring (i+n) は player 中心の DarkRadius 円上で固定
            _vertsBuf[i + n] = new Vector3(cos * DarkRadius, sin * DarkRadius, 0f);
        }
        for (int i = 0; i < n; i++)
        {
            int next = (i + 1) % n;
            int t = i * 6;
            _trisBuf[t + 0] = i;
            _trisBuf[t + 1] = i + n;
            _trisBuf[t + 2] = next + n;
            _trisBuf[t + 3] = i;
            _trisBuf[t + 4] = next + n;
            _trisBuf[t + 5] = next;
        }
        _staticBuffersBuilt = true;
    }

    // 各フレーム呼び出し: player 位置に追従して polygon を再構築
    // hot path 最適化:
    //   - alloc ゼロ (verts/tris は pre-allocated static buf)
    //   - trig ゼロ (cos/sin は table lookup)
    //   - triangle marshal 1 回のみ (topology 不変)
    //   - _hits list 廃止 (角度生成順 = 既にソート済 → sort 不要)
    private static bool _visionPaused;

    public static void ToggleVisionPaused(byte targetPid)
    {
        _visionPaused = !_visionPaused;
        if (_visionGO != null) _visionGO.SetActive(!_visionPaused);
        Utils.SendMessage($"Vision paused = {_visionPaused}", targetPid);
        Logger.Info($"Vision paused = {_visionPaused}", "BackroomsGen");
    }

    public static void UpdateVision()
    {
        if (!_inBackrooms || _visionGO == null || _visionMesh == null) return;
        if (_visionPaused) return;
        if (PlayerControl.LocalPlayer == null) return;

        EnsureStaticBuffers();

        Vector2 player = PlayerControl.LocalPlayer.Pos();
        _visionGO.transform.position = new Vector3(player.x, player.y, 0f);

        // 1. VisionRadius 圏内の wall AABB を pre-filter
        _nearbyAabbs.Clear();
        float r2 = VisionRadius * VisionRadius;
        for (int wi = 0; wi < WallAabbs.Count; wi++)
        {
            var w = WallAabbs[wi];
            float dx = Mathf.Max(Mathf.Abs(w.cx - player.x) - w.halfX, 0f);
            float dy = Mathf.Max(Mathf.Abs(w.cy - player.y) - w.halfY, 0f);
            if (dx * dx + dy * dy > r2) continue;
            _nearbyAabbs.Add(w);
        }

        // 2. 360 ray fan — 直接 _vertsBuf に書き込み (中間 list 不要)
        int n = RayFanCount;
        for (int i = 0; i < n; i++)
        {
            float cos = _rayCos[i];
            float sin = _raySin[i];
            float dist = CastRayLength(player, cos, sin);
            _vertsBuf[i] = new Vector3(cos * dist, sin * dist, 0f);
        }

        // 3. mesh upload
        //    - SetVertices(): mesh.vertices setter より少し速い (validation 省略) — Unity 公式推奨
        //    - SetTriangles(_, 0, false): 第3引数 calculateBounds=false で per-frame bounds 計算回避
        //    - static bounds は CreateVision で 1 回設定 (DarkRadius を覆う大 AABB)
        _visionMesh.SetVertices(_vertsBuf);
        if (!_trisUploaded)
        {
            _visionMesh.SetTriangles(_trisBuf, 0, calculateBounds: false);
            _trisUploaded = true;
        }
    }

    // 与えられた単位方向 (cos, sin) に origin から ray を撃ち、最近接の AABB ヒット距離を返す
    // ヒットなし → VisionRadius、origin が AABB 内 → その AABB は skip (player が壁の縁に
    // float 精度で「内側」判定されると全方位 t=0 になり screen 全黒のバグ発生 — 触れた壁は
    // 遮蔽しないものとして扱う = 触れた壁の向こうは普通に見える)
    private static float CastRayLength(Vector2 origin, float cos, float sin)
    {
        float tMin = VisionRadius;
        for (int i = 0; i < _nearbyAabbs.Count; i++)
        {
            var w = _nearbyAabbs[i];
            float minX = w.cx - w.halfX;
            float maxX = w.cx + w.halfX;
            float minY = w.cy - w.halfY;
            float maxY = w.cy + w.halfY;

            // origin が AABB 内ならこの壁は無視 (continue) — 全黒バグ回避
            if (origin.x >= minX && origin.x <= maxX && origin.y >= minY && origin.y <= maxY)
                continue;

            // Slab method
            // 軸平行 ray (cos=0 or sin=0) の罠: 単に t±inf を返すと「origin が slab 外」のケースで
            // wall を誤検知して 4 cardinal 方向に false-hit による黒スパイクが出る。
            // sin=0 なら ray は y=origin.y に貼り付くので、AABB y 範囲外なら絶対に当たらない。
            float tx1, tx2;
            if (Mathf.Abs(cos) < 1e-6f)
            {
                if (origin.x < minX || origin.x > maxX) continue;
                tx1 = float.NegativeInfinity;
                tx2 = float.PositiveInfinity;
            }
            else
            {
                tx1 = (minX - origin.x) / cos;
                tx2 = (maxX - origin.x) / cos;
                if (tx1 > tx2) { (tx1, tx2) = (tx2, tx1); }
            }

            float ty1, ty2;
            if (Mathf.Abs(sin) < 1e-6f)
            {
                if (origin.y < minY || origin.y > maxY) continue;
                ty1 = float.NegativeInfinity;
                ty2 = float.PositiveInfinity;
            }
            else
            {
                ty1 = (minY - origin.y) / sin;
                ty2 = (maxY - origin.y) / sin;
                if (ty1 > ty2) { (ty1, ty2) = (ty2, ty1); }
            }

            float tNear = Mathf.Max(tx1, ty1);
            float tFar = Mathf.Min(tx2, ty2);

            if (tFar < 0f || tNear > tFar) continue;
            if (tNear > 0f && tNear < tMin) tMin = tNear;
        }
        // 「壁に張り付くと polygon が dip → 黒の楔が視界に出る」罠を MinHitDistance で防ぐ
        // (player 周囲 0.5 unit は壁の有無に関わらず常に可視ゾーン化)
        return tMin < MinHitDistance ? MinHitDistance : tMin;
    }

    // 診断: AU vanilla の vision system (ShadowCollab / ShadowCamera / ShadowQuad) の runtime state を log
    // hijack 可能性を評価するため
    public static void DumpShadowSystem(byte targetPid)
    {
        StringBuilder sb = new();
        sb.AppendLine("=== AU Vanilla Shadow System Diagnostic ===");

        // 1. HudManager.ShadowQuad の GameObject hierarchy
        if (HudManager.InstanceExists && HudManager.Instance != null && HudManager.Instance.ShadowQuad != null)
        {
            MeshRenderer sq = HudManager.Instance.ShadowQuad;
            sb.AppendLine($"-- HudManager.ShadowQuad --");
            sb.AppendLine($"  GO name: {sq.gameObject.name} active={sq.gameObject.activeInHierarchy} layer={sq.gameObject.layer}({LayerMask.LayerToName(sq.gameObject.layer)})");
            sb.AppendLine($"  enabled={sq.enabled} sortingLayer='{sq.sortingLayerName}' order={sq.sortingOrder}");
            sb.AppendLine($"  material shader='{sq.material?.shader?.name}'");
            sb.AppendLine($"  worldPos={sq.transform.position}");

            // parent chain
            Transform t = sq.transform.parent;
            int depth = 1;
            while (t != null && depth < 10)
            {
                Component[] comps = t.GetComponents<Component>();
                sb.Append($"  parent[{depth}]: {t.name} (");
                foreach (Component c in comps)
                    if (c != null) sb.Append(c.GetType().Name).Append(' ');
                sb.AppendLine(")");
                t = t.parent;
                depth++;
            }

            // child chain (any ShadowCollab/ShadowCamera in same hierarchy?)
            Component[] siblings = sq.transform.parent?.GetComponentsInChildren<Component>(true);
            if (siblings != null)
            {
                int sc = 0, scam = 0;
                foreach (Component c in siblings)
                {
                    if (c == null) continue;
                    string n = c.GetType().Name;
                    if (n == "ShadowCollab") sc++;
                    if (n == "ShadowCamera") scam++;
                }
                sb.AppendLine($"  sibling/descendant counts: ShadowCollab={sc}, ShadowCamera={scam}");
            }
        }
        else
        {
            sb.AppendLine("HudManager.ShadowQuad: NULL or HudManager missing");
        }

        // 2. 全 scene 内の ShadowCollab を列挙 (IL2CPP 用 generic FindObjectsOfType)
        var allCollabs = Object.FindObjectsOfType<ShadowCollab>(true);
        sb.AppendLine($"-- FindObjectsOfType<ShadowCollab>(true): {allCollabs.Count} instance(s) --");
        foreach (ShadowCollab sc in allCollabs)
        {
            if (sc == null) continue;
            sb.AppendLine($"  '{sc.gameObject.name}' active={sc.gameObject.activeInHierarchy} enabled={sc.enabled}");
            sb.AppendLine($"    ShadowCamera={(sc.ShadowCamera != null ? sc.ShadowCamera.name : "NULL")}, ShadowQuad={(sc.ShadowQuad != null ? sc.ShadowQuad.name : "NULL")}");
            if (sc.ShadowCamera != null)
            {
                sb.AppendLine($"    cam: enabled={sc.ShadowCamera.enabled} cullingMask=0x{sc.ShadowCamera.cullingMask:X8} clearFlags={sc.ShadowCamera.clearFlags}");
                sb.AppendLine($"    cam: targetTex={(sc.ShadowCamera.targetTexture != null ? $"{sc.ShadowCamera.targetTexture.width}x{sc.ShadowCamera.targetTexture.height}" : "null")}");
            }
        }

        // 3. ShadowCamera 単独でも探す
        var allCams = Object.FindObjectsOfType<ShadowCamera>(true);
        sb.AppendLine($"-- FindObjectsOfType<ShadowCamera>(true): {allCams.Count} instance(s) --");
        foreach (ShadowCamera sc in allCams)
        {
            if (sc == null) continue;
            sb.AppendLine($"  '{sc.gameObject.name}' active={sc.gameObject.activeInHierarchy} Shadozer='{(sc.Shadozer != null ? sc.Shadozer.name : "NULL")}'");
        }

        // 4. ShadowMask layer の名前を log
        sb.AppendLine($"-- Constants.ShadowMask = 0x{Constants.ShadowMask:X8} --");
        for (int i = 0; i < 32; i++)
        {
            if ((Constants.ShadowMask & (1 << i)) == 0) continue;
            sb.AppendLine($"  Layer {i}: '{LayerMask.LayerToName(i)}'");
        }

        Logger.Info(sb.ToString(), "BackroomsShadowDiag");
        Utils.SendMessage($"Shadow diag dumped. Check log (BackroomsShadowDiag category).", targetPid);
    }

    // vanilla 影 hijack 着手前 probe: LightSource pipeline の lobby state を log
    //   - LightPrefab/lightSource の有無
    //   - ShipStatus 依存の度合
    //   - NoShadows/OneWayShadows 辞書の lobby state
    //   - PlayerControl 子の Light* component 一覧
    public static void ProbeLightSystem(byte targetPid)
    {
        StringBuilder sb = new();
        sb.AppendLine("=== Light System Probe ===");

        PlayerControl lp = PlayerControl.LocalPlayer;
        if (lp == null)
        {
            sb.AppendLine("LocalPlayer: NULL");
            Logger.Info(sb.ToString(), "LightProbe");
            Utils.SendMessage("LocalPlayer null", targetPid);
            return;
        }

        sb.AppendLine($"-- LocalPlayer --");
        sb.AppendLine($"  name={lp.name} pos={lp.Pos()}");

        // PlayerControl 配下の Light* component を全列挙
        sb.AppendLine($"-- PlayerControl GO components --");
        Component[] selfComps = lp.GetComponents<Component>();
        foreach (Component c in selfComps)
        {
            if (c == null) continue;
            string n = c.GetType().Name;
            if (n.Contains("Light") || n.Contains("Shadow") || n.Contains("Cutaway"))
                sb.AppendLine($"  self: {n}");
        }
        Component[] childComps = lp.GetComponentsInChildren<Component>(true);
        foreach (Component c in childComps)
        {
            if (c == null) continue;
            string n = c.GetType().Name;
            if (n.Contains("Light") || n.Contains("Shadow") || n.Contains("Cutaway"))
                sb.AppendLine($"  child[{c.gameObject.name}]: {n}");
        }

        // LightPrefab (prefab reference assigned in editor)
        sb.AppendLine($"-- LightPrefab --");
        LightSource prefab = lp.LightPrefab;
        if (prefab != null)
        {
            sb.AppendLine($"  name={prefab.name}");
            sb.AppendLine($"  viewDistance={prefab.viewDistance}");
            sb.AppendLine($"  rendererType={prefab.rendererType}");
            sb.AppendLine($"  gpuShadowmapResolution={prefab.gpuShadowmapResolution}");
            sb.AppendLine($"  raycastMinRayCount={prefab.raycastMinRayCount}");
            sb.AppendLine($"  raycastTolerance={prefab.raycastTolerance}");
            sb.AppendLine($"  useFlashlight={prefab.useFlashlight}");
            sb.AppendLine($"  flashlightSize={prefab.flashlightSize}");
            sb.AppendLine($"  lightOffset={prefab.lightOffset}");
        }
        else
        {
            sb.AppendLine("  NULL (prefab not bound — may be assigned at game start)");
        }

        // lightSource (instance)
        sb.AppendLine($"-- lightSource (instance) --");
        LightSource ls = lp.lightSource;
        if (ls != null)
        {
            sb.AppendLine($"  name={ls.name} active={ls.gameObject.activeInHierarchy} enabled={ls.enabled}");
            sb.AppendLine($"  viewDistance={ls.viewDistance}");
            sb.AppendLine($"  rendererType={ls.rendererType}");
            sb.AppendLine($"  pos={ls.transform.position}");
        }
        else
        {
            sb.AppendLine("  NULL (not instantiated — AdjustLighting not called yet)");
        }

        // 静的辞書 (LightSource が register する shadow caster 辞書)
        sb.AppendLine($"-- Static shadow dicts --");
        try { sb.AppendLine($"  NoShadows.Count={LightSource.NoShadows?.Count ?? -1}"); }
        catch (Exception ex) { sb.AppendLine($"  NoShadows: EXC {ex.Message}"); }
        try { sb.AppendLine($"  OneWayShadows.Count={LightSource.OneWayShadows?.Count ?? -1}"); }
        catch (Exception ex) { sb.AppendLine($"  OneWayShadows: EXC {ex.Message}"); }

        // 依存先 (lobby で null になりがちなもの)
        sb.AppendLine($"-- Dependency state --");
        sb.AppendLine($"  ShipStatus.Instance={(ShipStatus.Instance != null ? "exists" : "NULL")}");
        sb.AppendLine($"  LobbyBehaviour.Instance={(LobbyBehaviour.Instance != null ? "exists" : "NULL")}");
        sb.AppendLine($"  HudManager.ShadowQuad active={(HudManager.InstanceExists && HudManager.Instance.ShadowQuad != null ? HudManager.Instance.ShadowQuad.gameObject.activeInHierarchy.ToString() : "missing")}");
        sb.AppendLine($"  Camera.main.orthographicSize={(Camera.main != null ? Camera.main.orthographicSize.ToString() : "null")} (Zoom.cs gate: ShadowQuad active iff ≈ 3.0)");

        // post-enter discriminator: Layer 10 shadow caster の存在確認 + LightSource renderer state
        sb.AppendLine($"-- Post-enter shadow caster state --");
        int layer10Count = 0;
        int spawnedWalls = 0;
        foreach (GameObject go in SpawnedTiles)
        {
            if (go == null) continue;
            if (go.name.StartsWith("BackroomsTile_wall")) spawnedWalls++;
            // IL2CPP: Transform の foreach enumerator は Il2CppSystem.Object を返すので indexer 必須
            int childCount = go.transform.childCount;
            for (int i = 0; i < childCount; i++)
            {
                Transform t = go.transform.GetChild(i);
                if (t != null && t.gameObject.layer == ShadowLayer) layer10Count++;
            }
        }
        sb.AppendLine($"  Layer-10 children under SpawnedTiles: {layer10Count} (wall tiles spawned: {spawnedWalls})");
        if (ls != null)
        {
            sb.AppendLine($"  lightSource.renderer = {(ls.renderer != null ? ls.renderer.GetType().Name : "NULL")}");
        }

        // ShadowCamera state を post-enter で確認
        ShadowCamera shadowCam = Object.FindObjectOfType<ShadowCamera>(true);
        if (shadowCam != null)
        {
            Camera cam = shadowCam.GetComponent<Camera>();
            sb.AppendLine($"-- ShadowCamera post-enter --");
            sb.AppendLine($"  worldPos={shadowCam.transform.position}");
            sb.AppendLine($"  parent={(shadowCam.transform.parent != null ? shadowCam.transform.parent.name : "ROOT")}");
            if (cam != null)
            {
                sb.AppendLine($"  Camera.orthographicSize={cam.orthographicSize}");
                sb.AppendLine($"  Camera.farClipPlane={cam.farClipPlane}");
                sb.AppendLine($"  Camera.cullingMask=0x{cam.cullingMask:X8}");
                sb.AppendLine($"  Camera.enabled={cam.enabled}");
            }
        }

        // 1 つ目の wall caster の状態
        foreach (GameObject wallGo in SpawnedTiles)
        {
            if (wallGo == null || !wallGo.name.StartsWith("BackroomsTile_wall")) continue;
            int cc = wallGo.transform.childCount;
            for (int i = 0; i < cc; i++)
            {
                Transform ch = wallGo.transform.GetChild(i);
                if (ch == null || ch.gameObject.layer != ShadowLayer) continue;
                SpriteRenderer csr = ch.GetComponent<SpriteRenderer>();
                sb.AppendLine($"-- First ShadowCaster sample --");
                sb.AppendLine($"  parent={wallGo.name} caster.worldPos={ch.position} layer={ch.gameObject.layer}");
                if (csr != null)
                {
                    sb.AppendLine($"  SR enabled={csr.enabled} sprite={(csr.sprite != null ? csr.sprite.name : "null")} bounds.size={csr.bounds.size}");
                    sb.AppendLine($"  SR material.shader={csr.sharedMaterial?.shader?.name}");
                }
                goto sampledOnce; // 1 つ取れたら終了
            }
        }
        sampledOnce: ;

        Logger.Info(sb.ToString(), "LightProbe");
        Utils.SendMessage("Light probe dumped. See log (LightProbe).", targetPid);
    }
}

// LobbyBehaviour.Update に乗っかって毎フレーム視界更新
[HarmonyPatch(typeof(LobbyBehaviour), nameof(LobbyBehaviour.Update))]
internal static class BackroomsVisionUpdateHook
{
    public static void Postfix()
    {
        BackroomsLobby.UpdateVision();
    }
}
