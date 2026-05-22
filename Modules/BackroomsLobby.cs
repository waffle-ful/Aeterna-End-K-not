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

    // 2026-05-21: モッドクライアント目線でバニラ船を完全に隠す。EnterBackrooms で
    // LobbyBehaviour 配下の Renderer 全部を退避 + disable、ExitBackrooms で復元。
    // 2026-05-22: SpriteRenderer 限定 → Renderer 基底に拡張 (ParticleSystemRenderer
    //   経由の流れ星/スクロール星空も catch)。SpawnedTiles は SetParent(null) なので
    //   配下走査からは外れる
    // OnGameStart 経路では scene unload に任せて参照クリアだけ
    private static readonly List<Renderer> DisabledRenderers = [];

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

    // SpawnedTiles と完全に index 一致する位置キャッシュ。
    // 距離 cull の hot loop で transform.position interop call を避けるため。
    // SpawnedTiles を mutate する全 site で同期維持必須
    private static readonly List<Vector2> SpawnedTilePositions = [];

    // 各タイルが所属する chunk の packed key (long)。streaming chunks (2026-05-22)
    // で chunk 単位の destroy を可能にするため。long.MinValue は「chunk 非所属」(test
    // pattern など)。0L だと PackChunkKey(0,0)==0L と衝突する罠 (advisor 指摘) を回避。
    // SpawnedTiles を mutate する全 site で同期維持必須
    private static readonly List<long> SpawnedTileChunkKeys = [];

    // 「chunk 非所属」を示す sentinel。PackChunkKey が long.MinValue を返すには
    // cx=int.MinValue かつ cy=0 が必須で、world 座標 -3.4×10¹⁰ なので歩行で到達不能
    private const long NoChunkKey = long.MinValue;

    // 壁 AABB を spawn 時に cache (UpdateVision の毎フレーム GetComponent ストーム回避)
    // entry: (cx, cy, halfX, halfY) — 中心と半サイズ
    private static readonly List<(float cx, float cy, float halfX, float halfY)> WallAabbs = [];

    // WallAabbs と完全に index 一致する chunk key cache。UnloadChunk で WallAabbs を
    // 部分削除するため。WallAabbs を mutate する全 site で同期維持必須
    private static readonly List<long> WallAabbChunkKeys = [];

    // SpawnTile が現在 spawn 中の chunk key を読むスクラッチ。GenerateChunk が
    // try/finally で set/clear、外部から SpawnTile が呼ばれた場合 (test pattern など) は
    // NoChunkKey = long.MinValue で残る → UnloadChunk が誤って消さない
    private static long _currentChunkKey = NoChunkKey;

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

    // 壁の根本 AO 用 vertical gradient sprite。32×32 だが横方向は全 row 同じ alpha で、
    // 隣 cell と境界がぴったり連続する (= 一続きの影ラインになる)。
    // alpha: 上端 1.0 (壁直下) → 下端 0 (床へ滲んで消える)、t² で上を急峻に
    private static Sprite _wallShadowGradientSprite;
    private static Sprite WallShadowGradientSprite
    {
        get
        {
            if (_wallShadowGradientSprite != null) return _wallShadowGradientSprite;
            const int N = 32;
            Texture2D tex = new(N, N, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            Color[] px = new Color[N * N];
            for (int y = 0; y < N; y++)
            {
                float t = y / (float)(N - 1); // y=N-1 (top) で 1, y=0 (bottom) で 0
                float a = t * t;
                for (int x = 0; x < N; x++)
                    px[y * N + x] = new Color(1f, 1f, 1f, a);
            }
            tex.SetPixels(px);
            tex.Apply();
            _wallShadowGradientSprite = Sprite.Create(tex, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f), N);
            _wallShadowGradientSprite.hideFlags |= HideFlags.HideAndDontSave;
            return _wallShadowGradientSprite;
        }
    }

    // 床シミ用のソフトエッジ円 sprite。procgen で 32×32 に距離フォールオフを焼く。
    // 中心 alpha 1.0 → 縁 alpha 0、a^2 で急峻化させてエッジを少しぼかし気味に。
    // 1 sprite を 3 サブブロブで重ねて回転＋楕円スケールすると amorphous なシミ形になる
    private static Sprite _stainSprite;
    private static Sprite StainSprite
    {
        get
        {
            if (_stainSprite != null) return _stainSprite;
            const int N = 32;
            Texture2D tex = new(N, N, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
            Color[] px = new Color[N * N];
            float c = N / 2f - 0.5f;
            float maxR = N / 2f;
            for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                float dx = x - c;
                float dy = y - c;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                float a = Mathf.Clamp01(1f - d / maxR);
                a *= a;
                px[y * N + x] = new Color(1f, 1f, 1f, a);
            }
            tex.SetPixels(px);
            tex.Apply();
            _stainSprite = Sprite.Create(tex, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f), N);
            _stainSprite.hideFlags |= HideFlags.HideAndDontSave;
            return _stainSprite;
        }
    }

    // 壁の dark mass / 暗帯に使う黒系色。vanilla Skeld の wall back と同程度
    private static readonly Color WallDarkColor = new(0.04f, 0.03f, 0.01f);

    // WallV body の縁にのせる sharp edge highlight (Polus 風 3D outline)
    private static readonly Color WallEdgeHighlight = new(0.12f, 0.09f, 0.03f);

    // floor 色 (kind="floor" の GetTileColor と一致)。WallV cell 内側の床背景に使用
    private static readonly Color FloorBaseColor = new(0.35f, 0.22f, 0.10f);

    // No-clip 中は Collider.offset が (0, 127) に書換えられて GetTruePosition() が
    // 127u 上空を返す ([[Patches/ControlPatch.cs:51]])。Backrooms には vent が無いので
    // transform.position から固定 body offset (-0.3636) を引いて足元を直接計算する。
    // これで vision/streaming が no-clip ON/OFF に関係なく player 実位置で動く。
    private static Vector2 LocalPlayerFeet()
    {
        Vector3 t = PlayerControl.LocalPlayer.transform.position;
        return new Vector2(t.x, t.y - 0.3636f);
    }

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

    // floor.png PNG ロード — wall.png と同じパターン
    private static Sprite _floorPngSprite;
    private static bool _floorPngTried;
    private static Sprite FloorPngSprite
    {
        get
        {
            if (_floorPngSprite != null) return _floorPngSprite;
            if (_floorPngTried) return null;
            _floorPngTried = true;
            try { _floorPngSprite = Utils.LoadSprite("EndKnot.Resources.Images.Backrooms.floor.png", 1024f); }
            catch { _floorPngSprite = null; }
            return _floorPngSprite;
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
            case "floor":
                // floor.png PNG があれば tint 無しで texture そのまま、なければ procedural fallback
                Sprite floorPng = FloorPngSprite;
                if (floorPng != null)
                {
                    sr.sprite = floorPng;
                    sr.color = Color.white;
                }
                else
                {
                    sr.sprite = BaselineSprite;
                    sr.color = GetTileColor(kind);
                }
                // 同じ模様の繰返し感を打ち消す per-cell 向き＋tint 揺らぎ
                ApplyFloorVariation(sr, go.transform, pos.x, pos.y);
                // ~2% 確率で汚れシミを重ねて古びたカーペット感を演出
                MaybeAddFloorStain(go, pos);
                break;
            default:
                sr.sprite = BaselineSprite;
                sr.color = GetTileColor(kind);
                break;
        }

        sr.sortingOrder = GetSortingOrder(kind);

        SpawnedTiles.Add(go);
        SpawnedTilePositions.Add(pos); // 距離 cull の hot loop で interop 回避するため
        SpawnedTileChunkKeys.Add(_currentChunkKey); // GenerateChunk 中なら chunk key、それ以外は 0L

        // 即時 cull: GenerateLobby が _spawnCullCenterValid を立てると、spawn 時点で
        // player から CullRadius 圏外なら inactive で生まれる。bulk sweep (~6k SetActive) を
        // 回避し freeze 短縮。LocalPlayer.GetTruePosition の interop は spawn ループ外で 1 回だけ
        if (_spawnCullCenterValid)
        {
            float ex = pos.x - _spawnCullCenter.x;
            float ey = pos.y - _spawnCullCenter.y;
            if (ex * ex + ey * ey >= CullRadiusSqr) go.SetActive(false);
        }

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
                WallAabbChunkKeys.Add(_currentChunkKey); // parallel cache for streaming unload

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

        // 下端の contact shadow (壁が床に接する根本の AO 風暗み)
        AddWallContactShadow(parent, 1f);
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
        Sprite floorPng = FloorPngSprite;
        if (floorPng != null)
        {
            sr.sprite = floorPng;
            sr.color = Color.white;
        }
        else
        {
            sr.sprite = BaselineSprite;
            sr.color = FloorBaseColor;
        }
        sr.sortingLayerName = "Default";
        sr.sortingOrder = sortingOrder;
        Vector3 wp = parent.transform.position;
        ApplyFloorVariation(sr, floor.transform, wp.x, wp.y);
    }

    // 床に時々 (~7%) シミを散らす。StainSprite (円ソフトエッジ) を 3 個ずらして重ね、
    // 各 sub-blob を楕円スケール (sx ≠ sy) + 回転で amorphous なブロブ形に。
    // hash 決定的なので cull の出し入れで形が変わらない
    private static void MaybeAddFloorStain(GameObject parent, Vector2 pos)
    {
        int cx = Mathf.RoundToInt(pos.x);
        int cy = Mathf.RoundToInt(pos.y);
        uint h = (uint)(cx * 374761393) ^ (uint)(cy * 668265263);
        h = (h ^ (h >> 13)) * 1274126177u;
        h ^= h >> 16;

        if ((h % 100u) >= 7u) return; // ~7%

        GameObject stain = new("FloorStain");
        stain.transform.SetParent(parent.transform, false);

        float ox = ((((h >> 4) & 0x0Fu) / 15f) - 0.5f) * 0.4f;
        float oy = ((((h >> 12) & 0x0Fu) / 15f) - 0.5f) * 0.4f;
        stain.transform.localPosition = new Vector3(ox, oy, 0f);

        Color blobColor = new(0.16f, 0.11f, 0.06f, 0.50f); // 暗茶半透明、重ね前提で少し薄め
        Sprite stainSp = StainSprite;

        for (int i = 0; i < 3; i++)
        {
            uint subH = h * (uint)(7919 + i * 31);
            subH ^= subH >> 13;
            subH *= 0x85ebca6bu;
            subH ^= subH >> 11;

            GameObject sub = new($"FloorStainBlob{i}");
            sub.transform.SetParent(stain.transform, false);

            float sx = 0.30f + ((subH & 0xFFu) / 255f) * 0.30f;          // 0.30 - 0.60
            float sy = 0.30f + (((subH >> 8) & 0xFFu) / 255f) * 0.30f;
            float subOx = ((((subH >> 16) & 0x0Fu) / 15f) - 0.5f) * 0.22f;
            float subOy = ((((subH >> 20) & 0x0Fu) / 15f) - 0.5f) * 0.22f;
            float subRot = (((subH >> 24) & 0xFFu) / 255f) * 360f;

            sub.transform.localPosition = new Vector3(subOx, subOy, 0f);
            sub.transform.localRotation = Quaternion.Euler(0f, 0f, subRot);
            sub.transform.localScale = new Vector3(sx, sy, 1f);

            SpriteRenderer sr = sub.AddComponent<SpriteRenderer>();
            sr.sprite = stainSp;
            sr.color = blobColor;
            sr.sortingLayerName = "Default";
            sr.sortingOrder = -9; // floor (-10) より前、wall (-5) より後
        }
    }

    // 床テクスチャの「同じ模様が並んでる」感を抑えるための per-cell variation。
    // cell 座標から決定的 hash を取り、(flipX, flipY, 90°回転) の 8 向きを割当てる。
    // 同じ cell は常に同じ向きになるので cull の出し入れでちらつかない。
    // SpriteRenderer.flipX/Y と transform.rotation は frame ごとのコストゼロ。1×1 正方 sprite
    // なので 90° 回転しても bounds は変わらず隣 cell にはみ出さない。
    //
    // 過去: 輝度 ±5% の tint も入れていたが、隣接 cell の明暗差が cell 境界を市松模様に
    // 浮き上がらせて「ブロック感」を生んだので削除 (2026-05-23)。texture seam による
    // 境目が残るかは flip/rotation 単独で再判定する。
    private static void ApplyFloorVariation(SpriteRenderer sr, Transform target, float worldX, float worldY)
    {
        int cx = Mathf.RoundToInt(worldX);
        int cy = Mathf.RoundToInt(worldY);
        uint h = (uint)(cx * 73856093) ^ (uint)(cy * 19349663);
        h ^= h >> 16;
        h *= 0x85ebca6b;
        h ^= h >> 13;
        sr.flipX = (h & 1u) != 0;
        sr.flipY = (h & 2u) != 0;
        if ((h & 4u) != 0)
            target.localRotation = Quaternion.Euler(0f, 0f, 90f);
    }

    // Polus 風 outlined dark mass: floor bg + 0.4 wide dark body + 両端 0.025 wide lighter edge highlights
    // sharp outline こそ Polus の壁が立体に見える核
    private static void BuildWallV(GameObject parent)
    {
        DrawFloorBackground(parent);
        BuildWallVOutline(parent);
        AddWallContactShadow(parent, 0.5f); // V は柱状なので影は控えめ
    }

    // 壁の下端から床側に soft な黒帯を伸ばして「物体が床に接触してる」影 (≒ AO) を表現。
    // 2D で「壁が浮いて見える」根本原因は地面との接地点に光が回り込まない部分の暗みが
    // 無いこと。WallShadowGradientSprite は左右フラットなので隣 cell と境界 alpha が
    // 一致 → 楕円ぽつぽつにならず連続した影ラインになる
    private static void AddWallContactShadow(GameObject wallParent, float widthScale)
    {
        GameObject shadow = new("WallContactShadow");
        shadow.transform.SetParent(wallParent.transform, false);
        // 上端 (sprite alpha 1.0 側) を壁下端 (-0.5) にぴったり合わせる → 中心 y = -0.5 - 0.11
        shadow.transform.localPosition = new Vector3(0f, -0.61f, 0f);
        shadow.transform.localScale = new Vector3(widthScale, 0.22f, 1f);
        SpriteRenderer sr = shadow.AddComponent<SpriteRenderer>();
        sr.sprite = WallShadowGradientSprite;
        sr.color = new Color(0f, 0f, 0f, 0.45f); // sprite alpha と掛けで実効平均 ~0.22
        sr.sortingLayerName = "Default";
        sr.sortingOrder = -9; // floor (-10) より前、wall body (-5/-4/-3) より後
    }

    // WallV cell の真下が Floor (= V が下向きに宙ぶらりんに終わる) のとき呼ぶ。
    // V 終端 cell の中身を WallH と同じ構造 (face 全面 + 上端 20% dark band) で塗り直す。
    // 幅は V 幅 (0.4u) を維持し、両端の V edge highlight は cap 外側 (±0.2125u) に残るので
    // V outline の中に H face が嵌まる絵になる。sortingOrder は V body (-4) より前 (-3)。
    //
    // perf: cap face は V body と完全に同サイズ・同位置で重なるので、V body の SR を disable
    // して GPU fill 量を倍化させない (memory: backrooms-perf-bottleneck-diagnosed — fill rate が支配)
    public static void AddWallVBottomCap(GameObject wallV)
    {
        if (wallV == null) return;

        // overdraw 抑止: cap face で完全に覆われる V body の SR を off
        Transform bodyT = wallV.transform.Find("WallVBody");
        if (bodyT != null)
        {
            SpriteRenderer bodySr = bodyT.GetComponent<SpriteRenderer>();
            if (bodySr != null) bodySr.enabled = false;
        }

        GameObject face = new("WallVBottomCapFace");
        face.transform.SetParent(wallV.transform, false);
        face.transform.localPosition = Vector3.zero;
        face.transform.localScale = new Vector3(0.4f, 1f, 1f);
        SpriteRenderer fsr = face.AddComponent<SpriteRenderer>();
        fsr.sprite = WallPngSprite ?? WallSpriteH ?? BaselineSprite;
        fsr.color = Color.white;
        fsr.sortingLayerName = "Default";
        fsr.sortingOrder = -3;

        GameObject band = new("WallVBottomCapBand");
        band.transform.SetParent(wallV.transform, false);
        band.transform.localPosition = new Vector3(0f, 0.4f, 0f);
        band.transform.localScale = new Vector3(0.4f, 0.2f, 1f);
        SpriteRenderer bsr = band.AddComponent<SpriteRenderer>();
        bsr.sprite = BaselineSprite;
        bsr.color = WallDarkColor;
        bsr.sortingLayerName = "Default";
        bsr.sortingOrder = -3;
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
        SpawnedTilePositions.Clear();
        SpawnedTileChunkKeys.Clear();
        WallAabbs.Clear();
        WallAabbChunkKeys.Clear();
        _loadedChunks.Clear();
        _streamValid = false;
        Utils.SendMessage($"Cleared {cleared} tiles.", targetPid);
        Logger.Info($"Cleared {cleared} tiles", "BackroomsDiag");
    }

    public static void DumpVisionDiagCurrentSeed(byte targetPid) => DumpVisionDiag(targetPid, _lastSeed);

    // /bbvisdiag — 視界 polygon と周辺 cell の整合性を診断
    //   wall pass-through bug の原因切り分け用:
    //   ・player cell type (floor/wall_h/wall_v) と procgen 期待値を表示
    //   ・周辺 5x5 cell の type grid を ASCII で
    //   ・各 cardinal 方向の ray hit distance と最近接 wall distance を比較
    //   ・nearbyAabbs の数と最近接 wall の位置
    public static void DumpVisionDiag(byte targetPid, uint seed)
    {
        if (PlayerControl.LocalPlayer == null) return;
        Vector2 p = PlayerControl.LocalPlayer.GetTruePosition();
        Vector2 pTrans = PlayerControl.LocalPlayer.transform.position;
        int px = Mathf.RoundToInt(p.x);
        int py = Mathf.RoundToInt(p.y);

        StringBuilder sb = new();
        sb.AppendLine($"=== Vision Diag === feet=({p.x:F3}, {p.y:F3}) transform=({pTrans.x:F3}, {pTrans.y:F3}) cell=({px},{py}) seed={seed}");

        CellKind pCell = ClassifyCell(px, py, seed);
        sb.AppendLine($"player cell kind: {pCell}");

        sb.AppendLine("5x5 cell grid centered on player (row=high y at top):");
        sb.Append("       ");
        for (int dx = -2; dx <= 2; dx++) sb.Append($"x={px + dx,3} ");
        sb.AppendLine();
        for (int dy = 2; dy >= -2; dy--)
        {
            sb.Append($"y={py + dy,3}: ");
            for (int dx = -2; dx <= 2; dx++)
            {
                CellKind k = ClassifyCell(px + dx, py + dy, seed);
                string label = k switch { CellKind.Floor => " .  ", CellKind.WallH => "[H] ", CellKind.WallV => "[V] ", _ => " ?  " };
                sb.Append(label).Append(' ');
            }
            sb.AppendLine();
        }

        sb.AppendLine($"\nNearbyAabbs count = {_nearbyAabbs.Count}");
        for (int i = 0; i < _nearbyAabbs.Count && i < 12; i++)
        {
            var w = _nearbyAabbs[i];
            float dx = Mathf.Max(Mathf.Abs(w.cx - p.x) - w.halfX, 0f);
            float dy = Mathf.Max(Mathf.Abs(w.cy - p.y) - w.halfY, 0f);
            float dist = Mathf.Sqrt(dx * dx + dy * dy);
            sb.AppendLine($"  [{i}] aabb center=({w.cx:F2},{w.cy:F2}) half=({w.halfX:F2},{w.halfY:F2}) closestDist={dist:F3}");
        }

        sb.AppendLine("\nCardinal ray hits (compare with expected from procgen):");
        (string name, float c, float s)[] dirs =
        {
            ("E  ", 1f, 0f), ("NE ", 0.707f, 0.707f),
            ("N  ", 0f, 1f), ("NW ", -0.707f, 0.707f),
            ("W  ", -1f, 0f), ("SW ", -0.707f, -0.707f),
            ("S  ", 0f, -1f), ("SE ", 0.707f, -0.707f)
        };
        foreach (var d in dirs)
        {
            float hit = CastRayLength(p, d.c, d.s);
            sb.AppendLine($"  {d.name} dist={hit:F3}");
        }

        Logger.Info(sb.ToString(), "BackroomsVisionDiag");
        Utils.SendMessage($"VisionDiag dumped to log (cell={pCell}, nearbyAabbs={_nearbyAabbs.Count}). See LogOutput.log", targetPid);
    }

    // /bbnocdiag — no-clip ON 時に真っ暗になる症状の切り分け用。
    //   transform.position / Collider.offset / GetTruePosition() / LocalPlayerFeet() / vision GO 位置 /
    //   camera 位置 / streaming center を全部 dump して GetTruePosition と camera の相対関係を見る
    public static void DumpNoClipDiag(byte targetPid)
    {
        if (PlayerControl.LocalPlayer == null)
        {
            Utils.SendMessage("LocalPlayer is null", targetPid);
            return;
        }

        var lp = PlayerControl.LocalPlayer;
        Vector3 tPos = lp.transform.position;
        Vector2 colOffset = lp.Collider != null ? lp.Collider.offset : Vector2.zero;
        Vector2 gtp = lp.GetTruePosition();
        Vector2 feet = LocalPlayerFeet();
        Vector3 visionPos = _visionGO != null ? _visionGO.transform.position : Vector3.zero;
        bool noClipFlag = ControllerManagerUpdatePatch.NoClipEnabled;
        bool ctrlHeld = Input.GetKey(KeyCode.LeftControl);

        Camera cam = Camera.main;
        Vector3 camPos = cam != null ? cam.transform.position : Vector3.zero;
        float camOrtho = cam != null ? cam.orthographicSize : 0f;

        StringBuilder sb = new();
        sb.AppendLine("=== NoClip Diag ===");
        sb.AppendLine($"  transform.position    = ({tPos.x:F3}, {tPos.y:F3}, {tPos.z:F3})");
        sb.AppendLine($"  Collider.offset       = ({colOffset.x:F3}, {colOffset.y:F3})");
        sb.AppendLine($"  GetTruePosition()     = ({gtp.x:F3}, {gtp.y:F3})  [transform + Collider.offset]");
        sb.AppendLine($"  LocalPlayerFeet()     = ({feet.x:F3}, {feet.y:F3})  [transform - (0, 0.3636)]");
        sb.AppendLine($"  _visionGO.transform   = ({visionPos.x:F3}, {visionPos.y:F3}, {visionPos.z:F3})");
        sb.AppendLine($"  Camera.main.position  = ({camPos.x:F3}, {camPos.y:F3}, {camPos.z:F3}) orthoSize={camOrtho:F3}");
        sb.AppendLine($"  noClipFlag={noClipFlag} ctrlHeld={ctrlHeld} inVent={lp.inVent}");
        sb.AppendLine($"  _inBackrooms={_inBackrooms} _visionPaused={_visionPaused} _lastVisionValid={_lastVisionValid}");
        sb.AppendLine($"  _streamCx={_streamCx} _streamCy={_streamCy} _streamValid={_streamValid} loadedChunks={_loadedChunks.Count} tiles={SpawnedTiles.Count}");
        sb.AppendLine($"  _lastVisionPlayer=({_lastVisionPlayer.x:F3}, {_lastVisionPlayer.y:F3})");

        // camera と feet の y 差。no-clip で camera が collider 追従なら +127 になっているはず
        float camYDelta = camPos.y - feet.y;
        sb.AppendLine($"  camera.y - feet.y     = {camYDelta:F3}  (>10 で camera が collider に引きずられている疑い)");

        Logger.Info(sb.ToString(), "BackroomsNoClipDiag");
        Utils.SendMessage($"NoClipDiag dumped. noclip={noClipFlag}, camDelta={camYDelta:F1}", targetPid);
    }

    // Phase 2: Seeded chunk procgen
    // Backrooms 風: 部屋境界に決定論的 opening、それ以外は壁

    private const int ChunkSize = 16;
    // ActiveChunkRadius (旧 GenerationRadius): player を中心とした (2r+1)² chunks を常にロード。
    //   1 = 3×3 chunks = 2304 tiles (≈ 旧 baseline) / 0 = 1 chunk = 256 tiles (極小モード)
    // 2026-05-22 v4: streaming chunks 導入 — player が chunk 境界を跨ぐと遠方 chunk を Destroy、
    //   新方向 chunk を Load。world は実質無限、瞬間ロード量は baseline 据置。前回 GenRad=2
    //   一括生成 (6400 tiles = 11k 子 SR) は inactive scene 管理 overhead で FPS=60→30 になった
    //   経路 → streaming で「探索可能距離 10×」を実現
    private const int BaselineActiveChunkRadius = 1;
    private const int ReducedActiveChunkRadius = 0;
    private static int ActiveChunkRadius => (Main.BackroomsReduceProcgen?.Value ?? false) ? ReducedActiveChunkRadius : BaselineActiveChunkRadius;
    private const int RoomSize = 6;

    // === Streaming chunks state ===
    // _loadedChunks: 現在シーンに展開済みの chunk key set。LoadChunk が add、UnloadChunk が remove
    // _streamCx/Cy: 直近 UpdateStreaming 時の player chunk。これと一致してれば早期 return
    // _streamValid: 初回 or 強制再評価 (RegenerateIfActive / toggle) で false
    private static readonly HashSet<long> _loadedChunks = [];
    private static int _streamCx, _streamCy;
    private static bool _streamValid;

    private static long PackChunkKey(int cx, int cy) => ((long)cx << 32) | (uint)cy;

    private static void UnpackChunkKey(long key, out int cx, out int cy)
    {
        cx = (int)(key >> 32);
        cy = (int)(uint)key;
    }

    public static void GenerateLobby(uint seed, byte targetPid, bool silent = false)
    {
        if (LobbyBehaviour.Instance == null)
        {
            if (!silent) Utils.SendMessage("Not in lobby", targetPid);
            return;
        }

        if (PlayerControl.LocalPlayer == null) return;

        // 既存タイル全消去 (procgen と test pattern を同じ list で管理) + streaming state リセット
        int wiped = 0;
        foreach (GameObject go in SpawnedTiles)
        {
            if (go == null) continue;
            Object.Destroy(go);
            wiped++;
        }

        SpawnedTiles.Clear();
        SpawnedTilePositions.Clear();
        SpawnedTileChunkKeys.Clear();
        WallAabbs.Clear();
        WallAabbChunkKeys.Clear();
        _loadedChunks.Clear();
        _streamValid = false;

        _lastSeed = seed;

        Vector2 origin = PlayerControl.LocalPlayer.Pos();
        int centerCx = Mathf.FloorToInt(origin.x / ChunkSize);
        int centerCy = Mathf.FloorToInt(origin.y / ChunkSize);

        // spawn ループ前に cull center を 1 回だけ取得 (interop 削減)。
        // SpawnTile が圏外なら inactive で生成 → bulk sweep 回避
        _spawnCullCenter = LocalPlayerFeet();
        _spawnCullCenterValid = true;

        int r = ActiveChunkRadius;
        for (int dx = -r; dx <= r; dx++)
        for (int dy = -r; dy <= r; dy++)
            LoadChunk(centerCx + dx, centerCy + dy, seed);

        _spawnCullCenterValid = false;

        // 後続 UpdateStreaming が同 chunk なら早期 return できるよう center を記録
        _streamCx = centerCx;
        _streamCy = centerCy;
        _streamValid = true;

        // 2026-05-21: spawn-in-wall 対策。procgen は player cell を考慮しないので
        // vanilla lobby spawn (~ -0.2, 1.3) → cell (0,1) が WallV になり player が壁中に
        // 詰まる事故が起きる (TP 廃止に伴い顕在化)。player + 4 cardinal cell を強制 floor 化。
        // origin は body center (Pos) で 0.36u 高い → cell が 1 ズレるので足元 (GetTruePosition)
        // で再取得。collision/vision と同じ cell に揃える ([[reference_pos_vs_gettrueposition]])
        EnsureSpawnFloor(LocalPlayerFeet());

        int chunkCount = _loadedChunks.Count;
        int activeAfterSpawn = 0;
        for (int i = 0; i < SpawnedTiles.Count; i++)
            if (SpawnedTiles[i] != null && SpawnedTiles[i].activeSelf) activeAfterSpawn++;
        if (!silent)
            Utils.SendMessage($"Gen seed={seed}: wiped {wiped}, generated {SpawnedTiles.Count} tiles ({activeAfterSpawn} active) in {chunkCount} chunks around ({centerCx},{centerCy})", targetPid);
        Logger.Info($"GenerateLobby seed={seed} chunks={chunkCount} tiles={SpawnedTiles.Count} active={activeAfterSpawn} center=({centerCx},{centerCy})", "BackroomsGen");
    }

    public static void DumpCullInfo(byte targetPid)
    {
        int total = SpawnedTiles.Count;
        int active = 0;
        for (int i = 0; i < total; i++)
            if (SpawnedTiles[i] != null && SpawnedTiles[i].activeSelf) active++;
        string msg = $"Cull: {active}/{total} active (radius={CullRadius}u, _inBackrooms={_inBackrooms}, _cullValid={_cullValid}, ActiveChunkRadius={ActiveChunkRadius}, loadedChunks={_loadedChunks.Count}, center=({_streamCx},{_streamCy}))";
        Utils.SendMessage(msg, targetPid);
        Logger.Info(msg, "BackroomsCull");
    }

    // scene 全体 Renderer scan — vanilla lobby の何が EnterBackrooms 経路で disable されてないか診断
    // Scene.GetRootGameObjects() は IL2CPP strip で使えないので FindObjectsOfType<Renderer> 経由
    public static void DumpSceneRenderers(byte targetPid)
    {
        StringBuilder sb = new();
        sb.AppendLine("=== Scene Renderer Inventory ===");

        Renderer[] all = Object.FindObjectsOfType<Renderer>(true);
        sb.AppendLine($"FindObjectsOfType<Renderer>(true) = {all.Length}");

        // root transform name で集計
        Dictionary<string, (int en, int dis, string layer)> byRoot = [];
        int totalEnabled = 0, totalDisabled = 0;
        foreach (Renderer r in all)
        {
            if (r == null) continue;
            Transform root = r.transform;
            while (root.parent != null) root = root.parent;
            string key = root.name;
            bool isOn = r.enabled && r.gameObject.activeInHierarchy;
            if (isOn) totalEnabled++; else totalDisabled++;
            if (!byRoot.TryGetValue(key, out (int en, int dis, string layer) v))
                v = (0, 0, LayerMask.LayerToName(root.gameObject.layer));
            if (isOn) v.en++; else v.dis++;
            byRoot[key] = v;
        }
        // counts 降順で sort
        List<KeyValuePair<string, (int en, int dis, string layer)>> sorted = [];
        foreach (var kv in byRoot) sorted.Add(kv);
        sorted.Sort((a, b) => (b.Value.en + b.Value.dis).CompareTo(a.Value.en + a.Value.dis));

        foreach (var kv in sorted)
        {
            int sum = kv.Value.en + kv.Value.dis;
            sb.AppendLine($"  '{kv.Key}' L={kv.Value.layer}: en={kv.Value.en} dis={kv.Value.dis}  (total {sum})");
        }
        sb.AppendLine($"Total: enabled={totalEnabled} disabled={totalDisabled}");
        sb.AppendLine($"LobbyBehaviour.Instance.transform.root: {(LobbyBehaviour.Instance != null ? LobbyBehaviour.Instance.transform.root.name : "null")}");

        Utils.SendMessage($"Scene renderers: en={totalEnabled} dis={totalDisabled} across {sorted.Count} roots. See log.", targetPid);
        Logger.Info(sb.ToString(), "BackroomsShipDiag");
    }

    // spawn 周辺の wall を強制 floor 化。player + 上下左右 4 cell の計 5 cell
    private static void EnsureSpawnFloor(Vector2 spawnPos)
    {
        int spx = Mathf.RoundToInt(spawnPos.x);
        int spy = Mathf.RoundToInt(spawnPos.y);

        (int x, int y)[] clearCells =
        {
            (spx, spy),
            (spx + 1, spy), (spx - 1, spy),
            (spx, spy + 1), (spx, spy - 1)
        };

        int patched = 0;
        foreach ((int cx, int cy) in clearCells)
            if (TryReplaceWallWithFloor(cx, cy)) patched++;

        if (patched > 0)
            Logger.Info($"EnsureSpawnFloor: patched {patched} cells around ({spx},{spy})", "BackroomsGen");
    }

    private static bool TryReplaceWallWithFloor(int cx, int cy)
    {
        for (int i = SpawnedTiles.Count - 1; i >= 0; i--)
        {
            GameObject t = SpawnedTiles[i];
            if (t == null) continue;
            Vector3 pos = t.transform.position;
            if (Mathf.RoundToInt(pos.x) != cx || Mathf.RoundToInt(pos.y) != cy) continue;
            if (!t.name.Contains("wall")) return false; // already floor

            long preservedKey = SpawnedTileChunkKeys[i]; // 新 floor は元 wall と同じ chunk に属させる

            for (int j = WallAabbs.Count - 1; j >= 0; j--)
            {
                var w = WallAabbs[j];
                if (Mathf.RoundToInt(w.cx) == cx && Mathf.RoundToInt(w.cy) == cy)
                {
                    WallAabbs.RemoveAt(j);
                    WallAabbChunkKeys.RemoveAt(j); // parallel
                }
            }

            Object.Destroy(t);
            SpawnedTiles.RemoveAt(i);
            SpawnedTilePositions.RemoveAt(i);
            SpawnedTileChunkKeys.RemoveAt(i); // parallel

            long prevKey = _currentChunkKey;
            _currentChunkKey = preservedKey;
            try { SpawnTile("floor", new Vector2(cx, cy)); }
            finally { _currentChunkKey = prevKey; }
            return true;
        }
        return false;
    }

    private enum CellKind { Floor, WallH, WallV }

    private static int GenerateChunk(int cx, int cy, uint seed)
    {
        int count = 0;
        int baseX = cx * ChunkSize;
        int baseY = cy * ChunkSize;
        long prevKey = _currentChunkKey;
        _currentChunkKey = PackChunkKey(cx, cy);

        try
        {
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
                else if (cell == CellKind.WallV)
                {
                    CellKind south = ClassifyCell(wx, wy - 1, seed);
                    // 真下が Floor (= V が下向きに宙ぶらりんに終わる) なら H 風の終端キャップ
                    if (south == CellKind.Floor) AddWallVBottomCap(go);
                }

                count++;
            }
        }
        finally
        {
            _currentChunkKey = prevKey;
        }

        return count;
    }

    // === Streaming chunks API (2026-05-22) ===

    // idempotent: 既ロード chunk は no-op。spawn cull center 設定は呼出側 (GenerateLobby /
    // UpdateStreaming) の責務 — まとめロード時の interop call を 1 回にまとめるため
    private static void LoadChunk(int cx, int cy, uint seed)
    {
        long key = PackChunkKey(cx, cy);
        if (!_loadedChunks.Add(key)) return;
        GenerateChunk(cx, cy, seed);
    }

    // chunk 内の全 tile / WallAabb を flat list から逆順削除。_loadedChunks からも除去。
    // 削除は parallel list (SpawnedTiles + Positions + ChunkKeys と WallAabbs + ChunkKeys)
    // を同一 index で同期 RemoveAt
    private static void UnloadChunk(int cx, int cy)
    {
        long key = PackChunkKey(cx, cy);
        if (key == NoChunkKey) return; // 防御: sentinel と衝突する key は無効化 (実機到達不能だが念のため)
        if (!_loadedChunks.Remove(key)) return;

        int destroyed = 0;
        for (int i = SpawnedTiles.Count - 1; i >= 0; i--)
        {
            if (SpawnedTileChunkKeys[i] != key) continue;
            GameObject t = SpawnedTiles[i];
            if (t != null) UnityEngine.Object.Destroy(t);
            SpawnedTiles.RemoveAt(i);
            SpawnedTilePositions.RemoveAt(i);
            SpawnedTileChunkKeys.RemoveAt(i);
            destroyed++;
        }

        for (int i = WallAabbs.Count - 1; i >= 0; i--)
        {
            if (WallAabbChunkKeys[i] != key) continue;
            WallAabbs.RemoveAt(i);
            WallAabbChunkKeys.RemoveAt(i);
        }

        if (destroyed > 0)
            Logger.Info($"UnloadChunk ({cx},{cy}): destroyed {destroyed} tiles", "BackroomsGen");
    }

    // 毎フレ呼び。player chunk が変わったら radius 内/外を再評価し、差分 load/unload。
    // force=true: 同 chunk でも強制再評価 (toggle / regen で呼ぶ)
    public static void UpdateStreaming(bool force = false)
    {
        if (!_inBackrooms) return;
        if (PlayerControl.LocalPlayer == null) return;

        // no-clip ON で GetTruePosition() が 127u 上空を返す罠を回避 ([[LocalPlayerFeet]])。
        // streaming center が空中にズレると player 周囲の chunk が unload されて真っ暗になる。
        Vector2 p = LocalPlayerFeet();
        int playerCx = Mathf.FloorToInt(p.x / ChunkSize);
        int playerCy = Mathf.FloorToInt(p.y / ChunkSize);

        if (!force && _streamValid && playerCx == _streamCx && playerCy == _streamCy) return;

        int r = ActiveChunkRadius;

        // 1. Unload: radius 外の chunk を削除。foreach 中 mutate を避けるため一旦 list に集める
        List<long> toUnload = null;
        foreach (long key in _loadedChunks)
        {
            UnpackChunkKey(key, out int kcx, out int kcy);
            if (Math.Abs(kcx - playerCx) > r || Math.Abs(kcy - playerCy) > r)
                (toUnload ??= []).Add(key);
        }

        if (toUnload != null)
        {
            for (int i = 0; i < toUnload.Count; i++)
            {
                UnpackChunkKey(toUnload[i], out int kcx, out int kcy);
                UnloadChunk(kcx, kcy);
            }
        }

        // 2. Load: radius 内の未ロード chunk を生成。spawn cull center をループ前に 1 回だけ取得
        _spawnCullCenter = p;
        _spawnCullCenterValid = true;

        int loadedNow = 0;
        for (int dx = -r; dx <= r; dx++)
        for (int dy = -r; dy <= r; dy++)
        {
            long key = PackChunkKey(playerCx + dx, playerCy + dy);
            if (_loadedChunks.Contains(key)) continue;
            LoadChunk(playerCx + dx, playerCy + dy, _lastSeed);
            loadedNow++;
        }

        _spawnCullCenterValid = false;

        _streamCx = playerCx;
        _streamCy = playerCy;
        _streamValid = true;

        // chunk set 変動時は vision / cull キャッシュを invalidate
        if (loadedNow > 0 || toUnload != null)
        {
            _lastVisionValid = false;
            _cullValid = false;
            Logger.Info($"UpdateStreaming center=({playerCx},{playerCy}) loaded+={loadedNow} unloaded={toUnload?.Count ?? 0} totalChunks={_loadedChunks.Count} totalTiles={SpawnedTiles.Count}", "BackroomsGen");
        }
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

    // Phase 3 改 (2026-05-21): TP 廃止 — モッドクライアント目線でバニラロビーを「上書き」
    // する Matrix 構造。プレイヤーは vanilla 座標に居たまま、SR 全 disable + 周囲に procgen 展開
    // で Backrooms が「見える」。非モッドクライアントは vanilla 船を見続け、モッドクライアント
    // 同士は互いに Backrooms 内を歩いて見える

    // vanilla shadow hijack は 2026-05-21 dead (lobby で LightSource activation 不可、SetupLightingForGameplay
    // が ShipStatus 依存で NRE)。Layer 10 const は /bblightprobe diag で参照のため残置
    private const int ShadowLayer = 10;

    private static bool _inBackrooms;
    private static uint _lastSeed; // /bbvisdiag で procgen 再現用

    // バニラ船 collider + Renderer を disable するだけの軽量パス。LocalPlayer 非依存なので
    // LobbyBehaviour.Start Postfix で即時に呼べる → 「ロビー入室後にバニラ船が数秒見える」ラグを消す。
    // procgen + vision は EnterBackrooms 側で LocalPlayer 揃ってから後置 (2026-05-22)
    public static (int cols, int rs) HideVanillaShipImmediate()
    {
        if (LobbyBehaviour.Instance == null) return (0, 0);

        // Collider: stale Unity ref を除去してから rescan
        DisabledColliders.RemoveAll(c => c == null);
        int disabledCols = 0;
        Collider2D[] colliders = LobbyBehaviour.Instance.GetComponentsInChildren<Collider2D>(true);
        int shipMask = Constants.ShipOnlyMask;
        foreach (Collider2D c in colliders)
        {
            int layer = c.gameObject.layer;
            bool isShip = (shipMask & (1 << layer)) != 0;
            if (!isShip || !c.enabled) continue;
            c.enabled = false;
            DisabledColliders.Add(c);
            disabledCols++;
        }

        // Renderer: SpriteRenderer + ParticleSystemRenderer (流れ星/星空) + MeshRenderer 全部 catch
        DisabledRenderers.RemoveAll(r => r == null);
        int disabledRs = 0;
        Renderer[] rs = LobbyBehaviour.Instance.GetComponentsInChildren<Renderer>(true);
        foreach (Renderer r in rs)
        {
            if (r == null || !r.enabled) continue;
            r.enabled = false;
            DisabledRenderers.Add(r);
            disabledRs++;
        }

        if (disabledCols > 0 || disabledRs > 0)
            Logger.Info($"HideVanillaShipImmediate: cols={disabledCols} rs={disabledRs}", "BackroomsGen");

        return (disabledCols, disabledRs);
    }

    public static void EnterBackrooms(uint seed, byte targetPid, bool silent = false)
    {
        if (LobbyBehaviour.Instance == null)
        {
            if (!silent) Utils.SendMessage("Not in lobby", targetPid);
            return;
        }

        if (PlayerControl.LocalPlayer == null) return;

        // 1+2. バニラ船を隠す (LobbyBehaviour.Start Postfix で既に走っていれば全 skip)
        (int disabledCols, int disabledRs) = HideVanillaShipImmediate();

        // 3. プレイヤー位置を中心に procgen (TP しない — player はそのまま)
        _lastSeed = seed;
        GenerateLobby(seed, targetPid, silent);

        // 4. custom mesh 視界システム起動 (vanilla hijack 路線は dead — reference 参照)
        CreateVision();
        // 5. 不気味系 overlay (黄色フィルター + 蛍光灯フリッカー)
        CreateOverlay();
        _visionPaused = false;
        _inBackrooms = true;
        _lastVisionValid = false; // idle skip cache invalidate — 次フレームで強制 rebuild
        _cullValid = false; // 距離 cull cache invalidate — 次フレで全 tile sweep

        Logger.Info($"Entered Backrooms (no-TP) seed={seed} disabledCols={disabledCols} disabledRs={disabledRs} tiles={SpawnedTiles.Count}", "BackroomsGen");
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
        _lastVisionValid = false;
        _cullValid = false;
        DestroyVision();
        DestroyOverlay();

        // 1. Backrooms タイル全消去
        int wiped = 0;
        foreach (GameObject go in SpawnedTiles)
        {
            if (go == null) continue;
            Object.Destroy(go);
            wiped++;
        }

        SpawnedTiles.Clear();
        SpawnedTilePositions.Clear();
        SpawnedTileChunkKeys.Clear();
        WallAabbs.Clear();
        WallAabbChunkKeys.Clear();
        _loadedChunks.Clear();
        _streamValid = false;

        // 2. ロビー collider 復元
        int restoredC = 0;
        foreach (Collider2D c in DisabledColliders)
        {
            if (c == null) continue;
            c.enabled = true;
            restoredC++;
        }

        DisabledColliders.Clear();

        // 3. バニラ Renderer 復元
        int restoredR = 0;
        foreach (Renderer r in DisabledRenderers)
        {
            if (r == null) continue;
            r.enabled = true;
            restoredR++;
        }

        DisabledRenderers.Clear();

        Utils.SendMessage($"Exited Backrooms. Cleared {wiped} tiles, restored {restoredC} cols + {restoredR} SRs.", targetPid);
        Logger.Info($"Exited Backrooms cleared={wiped} restoredC={restoredC} restoredR={restoredR}", "BackroomsGen");
    }

    // ロビー→ゲーム遷移時の cleanup。root GO のタイル / visionGO を破壊。
    // SR/Collider 参照は scene unload で自動的に消えるので list クリアだけ
    public static void OnGameStart()
    {
        if (!_inBackrooms && SpawnedTiles.Count == 0 && _visionGO == null) return;

        _inBackrooms = false;
        _visionPaused = false;
        _lastVisionValid = false;
        _cullValid = false;
        DestroyVision();
        DestroyOverlay();

        int wiped = 0;
        foreach (GameObject go in SpawnedTiles)
        {
            if (go == null) continue;
            Object.Destroy(go);
            wiped++;
        }

        SpawnedTiles.Clear();
        SpawnedTilePositions.Clear();
        SpawnedTileChunkKeys.Clear();
        WallAabbs.Clear();
        WallAabbChunkKeys.Clear();
        _loadedChunks.Clear();
        _streamValid = false;
        DisabledColliders.Clear();
        DisabledRenderers.Clear();

        Logger.Info($"OnGameStart cleanup: wiped {wiped} tiles", "BackroomsGen");
    }

    // session 跨ぎ (lobby → main menu → 新 lobby) で stale state を捨てる。
    // OnGameStart は ShipStatus.Awake hook なので game→lobby 復帰時しか走らない。
    // 新 LobbyBehaviour Start 時に呼び、_inBackrooms / 死 ref を全クリア
    public static void OnLobbyReload()
    {
        bool wasActive = _inBackrooms || SpawnedTiles.Count > 0 || _visionGO != null || DisabledRenderers.Count > 0;
        if (!wasActive) return;

        _inBackrooms = false;
        _visionPaused = false;
        _lastVisionValid = false;
        _cullValid = false;
        _spawnCullCenterValid = false;
        _visionGO = null; // scene unload で destroy 済 — 参照だけクリア (DestroyVision 経由は不要)
        _visionMF = null;
        _visionMesh = null;
        _visionMat = null;
        _overlayGO = null; // 同上 — camera 子なので scene unload で消える
        _overlaySR = null;

        SpawnedTiles.Clear();
        SpawnedTilePositions.Clear();
        SpawnedTileChunkKeys.Clear();
        WallAabbs.Clear();
        WallAabbChunkKeys.Clear();
        _loadedChunks.Clear();
        _streamValid = false;
        DisabledColliders.Clear();
        DisabledRenderers.Clear();

        Logger.Info("OnLobbyReload: stale Backrooms state cleared", "BackroomsGen");
    }

    // Phase 4: 視界システム (corner-aware polygon raycast — 2026-05-21 corner ray 実装)
    //   1. spawn 時 cache 済み WallAabbs から VisionRadius 圏内を pre-filter
    //   2. base = 360 uniform ray fan (隙間ない coverage の保険)
    //   3. + 各 visible AABB corner に ±ε rays (corner leak 防止 — 「壁角の向こうに広い扇形が見える」バグ)
    //   4. (angle, dist) を struct で持って angle 順にソート
    //   5. (inner @ hit_dist, outer @ DarkRadius) の donut mesh をハードエッジ・solid black で構築
    //   ※ 直接ヒット点から direction を逆算するのは NG (inside-AABB で hit≈player → atan2(0,0)=0 で
    //      全ヒット同一方向に collapse → 一方向だけ見える縮退多角形バグ)。angle 自体を保持して sort/build に使う

    // 2026-05-22 v4: vision/dark radius を絞って GPU 負荷 + corner ray 数を削減 (user request)
    //   8u → 5u: 視界半径 (60% に絞ると lit area 面積は 39%、より「Backrooms らしい」狭視界)
    //   60u → 25u: dark mesh 外周。プレイヤー本体が見える範囲を覆えれば十分 (camera ortho ~3.5u)
    // 2026-05-22 v5: streaming 実装後 perf 余裕が生まれたので VisionRadius を 8u に戻す
    //   camera diagonal ≈ sqrt(3.5^2 + 6.2^2) ≈ 7.1u なので 5u だと画面コーナーに dark boundary が見えてしまう
    private const float VisionRadius = 8f;
    // ray dist のフロア値 — 退化頂点 (distance≈0 で全方位 collapse) の防止だけが目的。
    // ここを player radius (~0.15) より大きくすると「触れた壁の先が見える」バグが発生する。
    // 0.05 は player collider が物理的に到達不可能な距離なので、実プレイ中は一度も clamp しない。
    private const float MinHitDistance = 0.05f;
    private const float DarkRadius = 25f;      // dark mesh 外周 (60→25: 描画 fill 量 17% に縮小)
    private const int MaxRayFanCount = 360;    // base ray fan 上限 (cos/sin table 確保サイズ)
    private const float CornerEps = 0.0005f;   // corner ray の左右オフセット (~0.03°)
    // pre-allocated buffer の上限。base 360 + max 256 corners × 2 = 872 < 1024
    private const int MaxRays = 1024;

    // ===== 2026-05-22: perf tuning (Client Options トグル経由で差分検証) =====
    //   ReduceRays      true: base ray 180 / false: 360
    //   ThrottleVision  true: idle skip 0.001 (sqrt~0.032u) / false: 0.0001 (sqrt~0.01u)
    // ※ PartialUpload は画面崩れで没 (2026-05-22) — full vert upload に固定
    private const int BaselineRayFanCount = 360;
    private const int ReducedRayFanCount = 180;
    private const float BaselineIdleSkipThreshold = 0.0001f;
    private const float ThrottledIdleSkipThreshold = 0.001f;

    private static int RayFanCount => (Main.BackroomsReduceRays?.Value ?? false) ? ReducedRayFanCount : BaselineRayFanCount;
    private static float IdleSkipThreshold => (Main.BackroomsThrottleVision?.Value ?? false) ? ThrottledIdleSkipThreshold : BaselineIdleSkipThreshold;

    private static GameObject _visionGO;
    private static MeshFilter _visionMF;
    private static Mesh _visionMesh;
    private static Material _visionMat;

    private static readonly List<(float cx, float cy, float halfX, float halfY)> _nearbyAabbs = new(64);

    // ray データ 1 件
    // IL2CPP の Comparer<T>.Default は値型 IComparable<T> で finicky なので、
    // 明示 IComparer<RayHit> インスタンスを Array.Sort に渡す
    private struct RayHit
    {
        public float Angle;
        public float Dist;
        public float Cos;
        public float Sin;
    }

    private sealed class RayHitAngleComparer : IComparer<RayHit>
    {
        public int Compare(RayHit x, RayHit y) => x.Angle.CompareTo(y.Angle);
    }

    private static readonly RayHitAngleComparer _rayHitComparer = new();

    // 静的事前計算バッファ — GC churn ゼロ
    //   _rayCos/Sin: base ray の cos/sin precompute (MaxRayFanCount で確保、_rayFanCount 分だけ使用)
    //   _rays      : この frame で cast した全 ray を一旦詰める作業領域 (sort 後 vertex/tri 化)
    //   _vertsBuf  : 上限サイズ確保。未使用 slot は前 frame の stale data だが triangle が指さないので無害
    //   _trisBuf   : 未使用 triangle slot は全 index=0 (degenerate) で埋める → Mesh.Clear 不要
    private static readonly float[] _rayCos = new float[MaxRayFanCount];
    private static readonly float[] _raySin = new float[MaxRayFanCount];
    private static readonly RayHit[] _rays = new RayHit[MaxRays];
    private static readonly Vector3[] _vertsBuf = new Vector3[2 * MaxRays];
    private static readonly int[] _trisBuf = new int[6 * MaxRays];
    private static int _cosTableBuiltFor; // 0 = 未構築、>0 = この値で build 済

    // Idle skip: 直前 rebuild 時の player 位置。動いてなければ rebuild も transform 更新も skip。
    // ロビーは idle 時間が支配的なので、これ単体で大幅な FPS 回復が見込める
    private static Vector2 _lastVisionPlayer;
    private static bool _lastVisionValid;

    // ===== 距離 cull システム (2026-05-22) =====
    // 視界 (VisionRadius=8u) 外は dark mesh で必ず黒く塗られるので、cull radius は vision 同等で十分。
    //   CullRadius=7u: vision 5u + safety 2u → walk 1 cycle (2u) 内に新タイル active 化、popup 不可視
    //   active 領域 π×7² ≈ 154 cells (旧 314 の半分)
    // Wall AABB cache は SetActive 状態と独立に保持されるので視界 raycast は正しく occlude する。
    // CullMoveSqrThr=4 (sqrt=2u) — player 2u 進むまで cull 再判定 skip
    private static Vector2 _lastCullPlayer;
    private static bool _cullValid;
    private const float CullRadius = 7f;
    private const float CullRadiusSqr = CullRadius * CullRadius;
    private const float CullMoveSqrThreshold = 4f;
    // SpawnTile 内 inline cull 用の cache。GenerateLobby が spawn ループ前に 1 度だけセット
    private static Vector2 _spawnCullCenter;
    private static bool _spawnCullCenterValid;
    // idle 判定閾値は _idleSkipThreshold に移行 (chat command で動的変更可)。
    // baseline 0.0001 → sqrt=0.01u → walk 中 (0.083u/frame) 毎フレーム rebuild
    // optimized 0.001  → sqrt=0.0316u → walk 中 ~2.5 frame 毎 rebuild (rebuild 30fps)

    // === Backrooms creepy overlay (full-screen yellow filter + fluorescent flicker) ===
    // 2026-05-23: 蛍光灯下の Backrooms 感を出すために画面全体に薄黄 SR を 1 枚張る。
    // camera 子なので player 移動に追従。flicker は UpdateVision tick 頭 (idle skip より前) で駆動。
    // sortingOrder=100 は player/walls/floor 全部の前、AU UI Canvas は別 sorting 系統なので
    // chat/settings ボタンは透ける

    private static GameObject _overlayGO;
    private static SpriteRenderer _overlaySR;

    // 黄色フィルター基本値。明るすぎを避けるため RGB を少し下げて alpha を上げ、
    // 全体に「暗く黄ばんだ蛍光灯下」感を出す (2026-05-23 明度下げチューニング)
    private static readonly Color OverlayYellowBase = new(0.78f, 0.72f, 0.42f, 0.22f);
    // フリッカー時の覆い。「真っ暗」だと唐突なので alpha 0.32 黒で「明るさを下げる」感じに
    private static readonly Color OverlayBlackout = new(0f, 0f, 0f, 0.32f);
    // vignette mesh の色 — ほぼ黒だが僅かに暖色を残す (≒ #0a0805) で「淀んだ空気」感を出す。
    // alpha 1.0 で不透明維持 (壁は sortingOrder で前面に来るので隠れない既存仕様)
    private static readonly Color VignetteWarmDark = new(0.04f, 0.03f, 0.02f, 1f);

    private static float _flickerNextEvalAt;
    private static float _flickerHoldUntil;
    private static bool _flickerInBlackout;

    private static void CreateOverlay()
    {
        if (_overlayGO != null) return;

        Camera cam = Camera.main;
        if (cam == null)
        {
            Logger.Warn("CreateOverlay: Camera.main null, skip", "BackroomsGen");
            return;
        }

        _overlayGO = new GameObject("BackroomsCreepyOverlay");
        _overlayGO.transform.SetParent(cam.transform, false);
        _overlayGO.transform.localPosition = new Vector3(0f, 0f, 1f); // camera 前 1u (z+)

        // ortho 半高 × 2 = 全高、× aspect = 全幅。余裕 ×1.3 で letterbox 内を完全カバー
        float fullH = cam.orthographicSize * 2f * 1.3f;
        float fullW = fullH * cam.aspect;
        _overlayGO.transform.localScale = new Vector3(fullW, fullH, 1f);

        _overlaySR = _overlayGO.AddComponent<SpriteRenderer>();
        _overlaySR.sprite = BaselineSprite;
        _overlaySR.color = OverlayYellowBase;
        _overlaySR.sortingLayerName = "Default";
        _overlaySR.sortingOrder = 100;

        _flickerNextEvalAt = Time.time + UnityEngine.Random.Range(60f, 120f); // 初回まで 1-2 分
        _flickerHoldUntil = 0f;
        _flickerInBlackout = false;

        Logger.Info($"Creepy overlay created scale=({fullW:F1}x{fullH:F1}) sortingOrder={_overlaySR.sortingOrder}", "BackroomsGen");
    }

    private static void DestroyOverlay()
    {
        if (_overlayGO == null) return;
        UnityEngine.Object.Destroy(_overlayGO);
        _overlayGO = null;
        _overlaySR = null;
    }

    // UpdateVision 頭から呼ばれる。idle skip の影響を受けず毎フレ走らせて flicker を維持
    private static void UpdateOverlay()
    {
        if (_overlaySR == null) return;

        float now = Time.time;

        // 1. 停電 hold 中: 暗化色を出し続け、hold 過ぎたら次回まで間隔セット (≈2 分に 1 回)
        if (_flickerInBlackout)
        {
            if (now >= _flickerHoldUntil)
            {
                _flickerInBlackout = false;
                _flickerNextEvalAt = now + UnityEngine.Random.Range(90f, 180f);
            }
            else
            {
                _overlaySR.color = OverlayBlackout;
                return;
            }
        }

        // 2. 通常時: Perlin で alpha 微揺らぎ (蛍光灯の薄い明滅感)
        float noise = (Mathf.PerlinNoise(now * 1.7f, 12.3f) - 0.5f) * 0.05f; // ±2.5%
        Color c = OverlayYellowBase;
        c.a = Mathf.Clamp01(c.a + noise);
        _overlaySR.color = c;

        // 3. 次の停電発火判定 (≈2 分に 1 回、ホールド 0.15-0.4s = 「明るさが落ちる」体感)
        if (now >= _flickerNextEvalAt)
        {
            _flickerInBlackout = true;
            _flickerHoldUntil = now + UnityEngine.Random.Range(0.15f, 0.4f);
        }
    }

    private static void CreateVision()
    {
        if (_visionGO != null) return;

        _visionGO = new GameObject("BackroomsVision");
        _visionGO.transform.SetParent(null);
        // 初期位置は player 直下。UpdateVision の最初の非 skip フレームで上書きされる
        Vector2 initPos = PlayerControl.LocalPlayer != null ? LocalPlayerFeet() : Vector2.zero;
        _visionGO.transform.position = new Vector3(initPos.x, initPos.y, 0f);

        _visionMF = _visionGO.AddComponent<MeshFilter>();
        _visionMesh = new Mesh { name = "BackroomsVisionMesh" };
        _visionMesh.MarkDynamic();
        // static bounds で per-frame RecalculateBounds を省略。DarkRadius を完全に覆う AABB
        _visionMesh.bounds = new Bounds(Vector3.zero, new Vector3(2f * DarkRadius, 2f * DarkRadius, 1f));
        _visionMF.mesh = _visionMesh;

        MeshRenderer mr = _visionGO.AddComponent<MeshRenderer>();

        // shader 取得: Sprites/Default → stripped 時は既存タイルの material からコピー
        // 色: 暖色寄りの極暗茶 (≒ #1a1410)。完全黒だと「闇」だが、僅かに暖色寄せると
        // 「淀んだ蛍光灯の届かない空気」感になって overlay の黄色フィルターと馴染む (2026-05-23)
        Material src = BorrowTileMaterialOrNull();
        Shader sd = Shader.Find("Sprites/Default");
        if (sd != null)
            _visionMat = new Material(sd) { color = VignetteWarmDark };
        else if (src != null)
            _visionMat = new Material(src) { color = VignetteWarmDark };
        else
        {
            Logger.Error("Vision: Sprites/Default not found AND no tile material to borrow — mesh will be invisible", "BackroomsGen");
            _visionMat = new Material(Shader.Find("Hidden/Internal-Colored")) { color = VignetteWarmDark };
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
        Logger.Info("Vision destroyed", "BackroomsGen");
    }

    // base ray の cos/sin だけ事前計算。RayFanCount 変化時に再構築。
    private static void EnsureCosTable()
    {
        int n = RayFanCount;
        if (_cosTableBuiltFor == n) return;
        const float twoPi = Mathf.PI * 2f;
        for (int i = 0; i < n; i++)
        {
            float ang = (i / (float)n) * twoPi;
            _rayCos[i] = Mathf.Cos(ang);
            _raySin[i] = Mathf.Sin(ang);
        }
        _cosTableBuiltFor = n;
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
        // pause = 診断モード = 全タイル可視化したい。reactivate して cull 中断
        if (_visionPaused) ForceActivateAllTiles();
        _cullValid = false; // 復帰時に再 cull
        Utils.SendMessage($"Vision paused = {_visionPaused}", targetPid);
        Logger.Info($"Vision paused = {_visionPaused}", "BackroomsGen");
    }

    // 距離 cull: player から CullRadius 圏外の tile を SetActive(false) で Unity 走査から外す
    // hot path:
    //   - player 移動 < 2u なら即 return (idle skip と同方針)
    //   - SpawnedTilePositions の cached Vector2 で interop 回避
    //   - 状態遷移時 (active↔inactive) だけ SetActive 呼び出し → 同状態 skip で interop コスト最小化
    public static void UpdateCulling()
    {
        if (!_inBackrooms || _visionPaused) return;
        if (PlayerControl.LocalPlayer == null) return;
        if (SpawnedTiles.Count == 0) return;

        // no-clip ON 時に GetTruePosition() が 127u 上空を返す罠 ([[LocalPlayerFeet]])。
        // ここで GetTruePosition を使うと全 tile が「player から遠い」扱いになり SetActive(false)
        // で Backrooms 全消失する症状の元凶 (2026-05-22)。
        Vector2 player = LocalPlayerFeet();

        if (_cullValid)
        {
            float ddx = player.x - _lastCullPlayer.x;
            float ddy = player.y - _lastCullPlayer.y;
            if (ddx * ddx + ddy * ddy < CullMoveSqrThreshold) return;
        }

        _lastCullPlayer = player;
        _cullValid = true;

        float px = player.x;
        float py = player.y;
        int n = SpawnedTiles.Count;
        for (int i = 0; i < n; i++)
        {
            GameObject go = SpawnedTiles[i];
            if (go == null) continue;
            Vector2 pos = SpawnedTilePositions[i];
            float ex = pos.x - px;
            float ey = pos.y - py;
            bool inRange = ex * ex + ey * ey < CullRadiusSqr;
            if (go.activeSelf != inRange) go.SetActive(inRange);
        }
    }

    // vision pause / diag 時に呼ぶ。全 tile を強制 activate
    private static void ForceActivateAllTiles()
    {
        for (int i = 0; i < SpawnedTiles.Count; i++)
        {
            GameObject go = SpawnedTiles[i];
            if (go != null && !go.activeSelf) go.SetActive(true);
        }
    }

    // toggle 切替時に呼ぶ。次フレームで cos table 再構築 + idle skip 再評価
    public static void InvalidatePerfCache()
    {
        _lastVisionValid = false;
        _cosTableBuiltFor = 0;
    }

    // procgen toggle 切替時に呼ぶ。Backrooms 滞在中なら新 ActiveChunkRadius を反映。
    // streaming に移行したので full regen は不要 — UpdateStreaming(force:true) で
    // 新 radius に応じた load/unload 差分だけ実行 (探索済 chunk の loss なし)
    public static void RegenerateIfActive()
    {
        if (!_inBackrooms) return;
        if (LobbyBehaviour.Instance == null || PlayerControl.LocalPlayer == null) return;
        if (_lastSeed == 0u) _lastSeed = 1u;
        UpdateStreaming(force: true);
        Logger.Info($"RegenerateIfActive: ActiveChunkRadius={ActiveChunkRadius} loadedChunks={_loadedChunks.Count} tiles={SpawnedTiles.Count}", "BackroomsPerf");
    }

    public static void UpdateVision()
    {
        if (!_inBackrooms || _visionGO == null || _visionMesh == null) return;

        // overlay の flicker は idle skip と独立に毎フレ駆動 (player 静止中もチカチカ続く)
        UpdateOverlay();

        if (_visionPaused) return;
        if (PlayerControl.LocalPlayer == null) return;

        // 足元位置で vision 展開。GetTruePosition() ではなく LocalPlayerFeet() を使う:
        // no-clip ON 時に Collider.offset が (0, 127) に書換わって GetTruePosition() が
        // 127u 上空を返し vision の穴が空中に作られて player が真っ暗な空間に立つ症状を回避。
        Vector2 player = LocalPlayerFeet();

        // Idle skip: player が動いていなければ rebuild も transform 更新も skip (transform 据置=最大効果)。
        // 閾値超えで初めて _lastVisionPlayer を更新 → 微小ドリフトの累積で誤更新を防ぐ
        if (_lastVisionValid)
        {
            float dx = player.x - _lastVisionPlayer.x;
            float dy = player.y - _lastVisionPlayer.y;
            if (dx * dx + dy * dy < IdleSkipThreshold) return;
        }

        _lastVisionPlayer = player;
        _lastVisionValid = true;

        EnsureCosTable();

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

        int count = 0;
        const float twoPi = Mathf.PI * 2f;

        // 2. Base uniform ray fan (gap-free coverage の保険)
        int rfc = RayFanCount;
        for (int i = 0; i < rfc; i++)
        {
            float cos = _rayCos[i];
            float sin = _raySin[i];
            float dist = CastRayLength(player, cos, sin);
            _rays[count].Angle = (i / (float)rfc) * twoPi;
            _rays[count].Dist = dist;
            _rays[count].Cos = cos;
            _rays[count].Sin = sin;
            count++;
        }

        // 3. Corner rays — 各 visible AABB corner に ±ε rays で sharp shadow boundary を作る
        //    これがないと角の向こうに広い扇形 (corner leak) が visible 領域として残る
        for (int wi = 0; wi < _nearbyAabbs.Count && count + 8 < MaxRays; wi++)
        {
            var w = _nearbyAabbs[wi];
            for (int ci = 0; ci < 4; ci++)
            {
                float cx = w.cx + ((ci & 1) == 0 ? -w.halfX : w.halfX);
                float cy = w.cy + ((ci & 2) == 0 ? -w.halfY : w.halfY);
                float dxc = cx - player.x;
                float dyc = cy - player.y;
                float d2 = dxc * dxc + dyc * dyc;
                if (d2 > r2 || d2 < 1e-6f) continue; // 圏外 or origin overlap は skip
                float baseAng = Mathf.Atan2(dyc, dxc);
                for (int k = 0; k < 2; k++)
                {
                    float a = baseAng + (k == 0 ? -CornerEps : CornerEps);
                    // [-π, π] → [0, 2π] に正規化 (sort の angle 単調性を維持)
                    if (a < 0f) a += twoPi;
                    else if (a >= twoPi) a -= twoPi;
                    float ca = Mathf.Cos(a);
                    float sa = Mathf.Sin(a);
                    _rays[count].Angle = a;
                    _rays[count].Dist = CastRayLength(player, ca, sa);
                    _rays[count].Cos = ca;
                    _rays[count].Sin = sa;
                    count++;
                }
            }
        }

        // 4. Sort by angle — 明示 IComparer 渡し (IL2CPP default Comparer 不安定回避)
        Array.Sort(_rays, 0, count, _rayHitComparer);

        // 5. Build vertices: inner ring [0..count) は hit dist、outer ring [count..2*count) は DarkRadius
        for (int i = 0; i < count; i++)
        {
            float c = _rays[i].Cos;
            float s = _rays[i].Sin;
            float d = _rays[i].Dist;
            _vertsBuf[i] = new Vector3(c * d, s * d, 0f);
            _vertsBuf[i + count] = new Vector3(c * DarkRadius, s * DarkRadius, 0f);
        }

        // 6. Build triangles (donut topology — slice i は inner[i,next] / outer[i,next] の 2 tri)
        for (int i = 0; i < count; i++)
        {
            int next = (i + 1) % count;
            int t = i * 6;
            _trisBuf[t + 0] = i;
            _trisBuf[t + 1] = i + count;
            _trisBuf[t + 2] = next + count;
            _trisBuf[t + 3] = i;
            _trisBuf[t + 4] = next + count;
            _trisBuf[t + 5] = next;
        }

        // 7. 未使用 triangle slot は全 index=0 で埋めて degenerate triangle 化
        //    (Mesh.Clear() を呼ばずに variable count を扱う trick)
        int validTris = count * 6;
        for (int i = validTris; i < _trisBuf.Length; i++)
            _trisBuf[i] = 0;

        // 8. Upload
        //    vertex buffer 全体 (2*MaxRays) を毎フレーム送るが、未使用 slot は triangle が指さないので無害。
        //    partial upload (count*2 だけ送る) は 2026-05-22 に試したが画面崩れで没。
        //    static bounds は CreateVision で 1 度設定済 → calculateBounds=false
        _visionMesh.SetVertices(_vertsBuf);
        _visionMesh.SetTriangles(_trisBuf, 0, _trisBuf.Length, 0, calculateBounds: false);
    }

    // 与えられた単位方向 (cos, sin) に origin から ray を撃ち、最近接の AABB ヒット距離を返す
    // - 通常 (origin が AABB 外): tNear = 入射距離を hit とする
    // - origin が AABB 内 (player が壁の中に詰まった場合): tFar = 出口距離を hit とする
    //   → vision が壁の出口表面でクリップされて「壁を貫通して見える」バグを防止
    //   (2026-05-21: 旧 continue skip ロジックは player が wall_h 内に詰まった時に上方向の
    //    視界が次の行まで突き抜ける症状の元凶 — VisionDiag で確認済)
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

            if (tNear > 0f)
            {
                // 通常ケース: origin が AABB 外、ray が tNear で入射 → tNear を hit に
                if (tNear < tMin) tMin = tNear;
            }
            else if (tFar > 0f)
            {
                // origin が AABB 内 (壁に詰まった時): tFar = 出口距離を hit に
                // → 壁の出口表面で vision がクリップされ、壁を貫通して見えなくなる
                if (tFar < tMin) tMin = tFar;
            }
        }
        // MinHitDistance は退化頂点 (全方位 t≈0 で polygon が点に collapse) の防止だけ。
        // 実ヒット距離はそのまま返す — 壁張り付き時に inner ring が壁の向こうへ食い込まないように。
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
        BackroomsLobby.UpdateStreaming(); // chunk 境界跨ぎで Load/Unload 差分。早期 return が普通
        BackroomsLobby.UpdateCulling();
        BackroomsLobby.UpdateVision();
    }
}

// ロビー→ゲーム遷移時 (ShipStatus.Awake) に root GO のタイル / visionGO を破壊。
// SetParent(null) のため scene unload では消えない root GO 群を明示的に Destroy
[HarmonyPatch(typeof(ShipStatus), nameof(ShipStatus.Awake))]
internal static class BackroomsShipStatusAwakePatch
{
    public static void Prefix()
    {
        try { BackroomsLobby.OnGameStart(); }
        catch (Exception ex) { Logger.Warn($"Backrooms OnGameStart failed: {ex.Message}", "BackroomsGen"); }
    }
}
