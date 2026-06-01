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

    // WallAabbs と完全に index 一致する ghost SpriteRenderer cache (2026-05-27)。
    // 各壁の子 GO「WallGhost」に着く SR で、視界外 (occlusion) のとき alpha=0.30 で
    // wall.png を Upper dark mesh の上から透過表示し「壁うっすら見える」絵を作る。
    // null 許容: ghost 子が無い test pattern や failed spawn 時に index 整合を保つため。
    // WallAabbs を mutate する全 site で同期維持必須
    private static readonly List<SpriteRenderer> WallGhostRenderers = [];

    // Entity visibility cache (2026-05-28): DeadBody / 非 local PlayerControl の SR を
    // 視界内外で enabled toggle するため、毎フレ FindObjectsOfType + GetComponentsInChildren を
    // 走らせると IL2CPP interop が高い。0.5s ごとに refresh + parallel index で SR array を持つ。
    // 用途: Upper dark mesh の gradient alpha だと body/player が半透明で透けてしまう問題を
    // ハードカット (vanilla AU shadow と同じ感覚)。map (壁/床) は Upper dark gradient のままで薄く可視
    private static readonly List<DeadBody> _entityBodies = [];
    private static readonly List<SpriteRenderer[]> _entityBodyRenderers = [];
    private static readonly List<PlayerControl> _entityPlayers = [];
    private static readonly List<SpriteRenderer[]> _entityPlayerRenderers = [];
    private static float _entityCacheTime = -1f;
    private const float EntityCacheRefreshInterval = 0.5f;

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

    // 壁側面 AO 用 horizontal gradient sprite。WallShadowGradientSprite を 90°回転で
    // 使い回すと cell 境界でポツポツに見える症状が出た (2026-05-23) ので、横方向 gradient
    // を専用に焼いて rotation を排除。左 alpha 1.0 (body 側) → 右 alpha 0 (cell 外側)、
    // flipX で左右両対応。縦方向は全 col 同じ alpha なので隣 cell と境界ぴったり連続
    private static Sprite _wallShadowGradientHSprite;
    private static Sprite WallShadowGradientHSprite
    {
        get
        {
            if (_wallShadowGradientHSprite != null) return _wallShadowGradientHSprite;
            const int N = 32;
            Texture2D tex = new(N, N, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            Color[] px = new Color[N * N];
            for (int x = 0; x < N; x++)
            {
                float t = 1f - x / (float)(N - 1); // x=0 で 1, x=N-1 で 0
                float a = t * t;
                for (int y = 0; y < N; y++)
                    px[y * N + x] = new Color(1f, 1f, 1f, a);
            }
            tex.SetPixels(px);
            tex.Apply();
            _wallShadowGradientHSprite = Sprite.Create(tex, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f), N);
            _wallShadowGradientHSprite.hideFlags |= HideFlags.HideAndDontSave;
            return _wallShadowGradientHSprite;
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
                // ghost SR は BuildWallHComposite / wall_v case 内で生成済 (FindGhostInChildren で取得)
                WallGhostRenderers.Add(FindGhostInChildren(go));

                // (vanilla shadow hijack 路線は 2026-05-21 dead — caster 追加無し)
            }
        }

        return go;
    }

    // ========================================================================
    // per-wall ghost SR feature (2026-05-28 v7 で「暗色 overlay」として再有効化)
    //   旧設計: wall.png を α=0.30 で silhouette 描画 (Upper dark の上に「うっすら見える壁」)
    //     → cell 境界の alpha バラつき + tFar chain で「奥の壁が visible 扱い」になる副作用で 2 度 disable
    //   新設計 (v7): 黒 solid overlay を α gradient で重ねて壁本体を直接 darken。
    //     - donut mesh は chain 復活 (smooth) で V 字段差を回避
    //     - per-wall α は `CastRayFirstHit` (chain なし first-hit) で「occluder か遮蔽中か」を smoothstep gradient で判定
    //     - smoothstep が cell 境界の浮動小数ノイズを吸収して V 字を出さない
    // ========================================================================
    private const bool EnableWallGhost = true;

    // Ghost overlay color。暗色 overlay として壁全体を覆って darken する用途。
    //   color = 黒 (0,0,0)、α は per-frame UpdateVision で 0 → WallGhostAlphaDarkZone に gradient で動く
    //   旧設計の wall.png silhouette とは別物 (今は壁本体を黒で塗り潰す形)
    private static readonly Color WallGhostColorH = new(0f, 0f, 0f, 0f);
    private static readonly Color WallGhostColorV = new(0f, 0f, 0f, 0f);

    // 完全 occluded 時の overlay 最大 α。0.30 = 30% 黒 overlay で壁が薄暗い感。0.20-0.50 範囲で要調整
    private const float WallGhostAlphaDarkZone = 0.30f;

    // overlay α が 0 → WallGhostAlphaDarkZone に smoothstep で上がりきるまでの距離 (u)。
    // CastRayFirstHit の hit dist から wall 中心がこの距離を超えると max alpha。
    // 大きいほど cell 境界での α 段差が滑らか (V 字抑制) 、小さいほど rapid な fade。
    // 0.5 = cell 1 個分の半分で fade 完了 (wall 内側まで影が浸透する形)
    private const float WallShadowThreshold = 0.5f;

    // Directional shadow: player の南 (y < player.y) にある壁を darken。AU vanilla の
    // 擬似 3D 視点 (camera は player のやや南からやや北向き) で「player の背中側 = 南側」
    // の壁は見えにくいという見え方を再現する。
    // user 観察 (2026-05-28): 「player より上の壁は見える、下の壁は見えない」
    // 値は dy (= wall.cy - player.y) が -WallShadowYThreshold を超えると max alpha。
    // 小さい値 (0.3-0.5) だと「player 真南から急に影」、大きい値 (1.5-2.0) だと緩やかな fade
    private const float WallShadowYThreshold = 0.5f;

    // 壁の closest point がこの距離以内なら occluder 確定 (occlusion 判定をスキップ)。
    // wall center で ray test すると隣接壁の角を grazing して「後ろにいる」誤判定が出るため、
    // 「壁の AABB の一部が player の immediate vicinity にあるなら直接見える」と扱う安全網。
    // 1.0 = cell ぴったり 1 個分。adjacent 壁の closest corner (~0.5u) は内側、奥の壁 (~1.5u) は外側
    private const float CloseRangeNoOcclusionRadius = 1.0f;

    // BuildWallHComposite / WallV case が末尾で 1 個だけ生成する子 GO「WallGhost」を逆引きする。
    // SpawnTile 末尾の WallGhostRenderers.Add で使用。failed spawn / test pattern では null 返却。
    private static SpriteRenderer FindGhostInChildren(GameObject parent)
    {
        if (parent == null) return null;
        Transform t = parent.transform.Find("WallGhost");
        return t == null ? null : t.GetComponent<SpriteRenderer>();
    }

    // 各壁の最上層に乗せる「うっすら見える壁」用 SR を子 GO として生成。sortingOrder=60 で Upper dark (+50) の上、
    // overlay (100) の下。alpha 0 で default off、UpdateVision の occlusion ループで alpha を toggle。
    // localScale: WallH は 1×1 (cell 全面)、WallV は body と同じ 0.4×1.0 (隣 cell に被らないため)。
    //
    // EnableWallGhost=false 時は早期 return して GO 生成自体しない (WallGhostRenderers は null 詰めで index 同期維持)。
    private static void AddWallGhost(GameObject parent, bool isH)
    {
        if (!EnableWallGhost) return;

        GameObject ghost = new("WallGhost");
        ghost.transform.SetParent(parent.transform, false);
        ghost.transform.localPosition = Vector3.zero;
        ghost.transform.localScale = isH ? new Vector3(1f, 1f, 1f) : new Vector3(0.4f, 1f, 1f);
        SpriteRenderer sr = ghost.AddComponent<SpriteRenderer>();
        // 黒 overlay として壁を覆う (v7) ため sprite は H/V 共通の BaselineSprite (procedural white)
        sr.sprite = BaselineSprite;
        sr.color = isH ? WallGhostColorH : WallGhostColorV;
        sr.sortingLayerName = "Default";
        sr.sortingOrder = 60;
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

        // dark zone 用 ghost overlay (sortingOrder=60、default α=0)
        AddWallGhost(parent, isH: true);
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
    // sharp outline こそ Polus の壁が立体に見える核。
    // 下方向 AO はここでは出さない: 連続柱の各 cell 下方向 AO が次 cell の左右 AO と
    // cell 境界で空間的に重なり alpha 加算でシミ化する (2026-05-23 修正)。
    // 柱は床から生えるのではなく天井から床まで通る構造なので連続 cell に下方向 AO は不要。
    // V 終端 cell (真下が floor) のみ AddWallVBottomCap 内で下方向 AO を生やす
    private static void BuildWallV(GameObject parent)
    {
        DrawFloorBackground(parent);
        BuildWallVOutline(parent);
        AddWallVSideShadow(parent, isLeft: true);  // 左側面 AO
        AddWallVSideShadow(parent, isLeft: false); // 右側面 AO

        // dark zone 用 ghost overlay (sortingOrder=60、default α=0)
        AddWallGhost(parent, isH: false);
    }

    // WallV の左右側面に AO 影。柱が床に接する縦のラインを暗くして「柱が立ってる」感を強化。
    // rotation 排除版 (2026-05-23): WallShadowGradientHSprite (横 gradient 専用) を flipX
    // で左右両対応。回転すると cell 境界でポツポツに見える症状が出るので素直に横 sprite を別焼き
    private static void AddWallVSideShadow(GameObject wallParent, bool isLeft)
    {
        const float shadowWidth = 0.18f;
        const float bodyOuterEdge = 0.2125f; // body 半幅 0.2 + edge highlight 半幅 0.0125

        GameObject shadow = new($"WallVSideShadow_{(isLeft ? "L" : "R")}");
        shadow.transform.SetParent(wallParent.transform, false);

        float sign = isLeft ? -1f : +1f;
        // body 外端からさらに外 (cell 端側) へ shadowWidth/2 離した位置を中心に
        float centerX = sign * (bodyOuterEdge + shadowWidth / 2f);
        shadow.transform.localPosition = new Vector3(centerX, 0f, 0f);
        // 縦方向 1u + 4% overlap で隣接 WallV cell との sub-pixel gap を埋める。
        // alpha フラットな縦方向なので overlap 領域も blend で目に見える濃淡は出ない
        shadow.transform.localScale = new Vector3(shadowWidth, 1.04f, 1f);

        SpriteRenderer sr = shadow.AddComponent<SpriteRenderer>();
        sr.sprite = WallShadowGradientHSprite;
        // 元 sprite は「左濃→右薄」。
        //   左 AO: body 側=右 で濃が欲しい → flipX=true で「右濃←左薄」に
        //   右 AO: body 側=左 で濃が欲しい → flipX=false でそのまま
        sr.flipX = isLeft;
        sr.color = new Color(0f, 0f, 0f, 0.45f);
        sr.sortingLayerName = "Default";
        // sortingOrder=-6: vignette mesh (-7) より前、wall body (-5/-4/-3) より後。
        // 柱の左右 0.075u 隙間は vision raycast の解像度より狭く、cell ごとに
        // vignette が「開く/閉じる」して AO がポツポツ見えてた問題への対処 (2026-05-23)
        sr.sortingOrder = -6;
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

        // 終端 cell は下に floor が来るので下方向 AO を生やす (柱が床に「ぶつかって終わる」根本)。
        // 連続柱の中間 cell では生やさない (BuildWallV のコメント参照)
        AddWallContactShadow(wallV, 0.5f);
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
        WallGhostRenderers.Clear();
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
        WallGhostRenderers.Clear();
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
                    WallGhostRenderers.RemoveAt(j); // parallel
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
            WallGhostRenderers.RemoveAt(i); // parallel
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
            if (PerfLogEnabled) _perfStreamUpdates++;
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

        // 部屋 merge: 隣接部屋と seeded で結合判定 → 壁ごと消滅して大きい部屋に
        // (両側 Sector で同じ key を引くので両側 agree)。25% で merge → 6×6 単独 ≈ 56% / 12×6 等 ≈ 38% / 12×12 ≈ 6%
        if (onLeftBorder && RoomsMergeHorizontal(roomX - 1, roomY, seed))
            onLeftBorder = false;
        if (onBottomBorder && RoomsMergeVertical(roomX, roomY - 1, seed))
            onBottomBorder = false;

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

    // 部屋 merge 判定 (2026-05-23 追加): 旧 6×6 一律から可変サイズへ。
    // 確率は両側部屋で同じ hash key 引くので両側 agree、整合性破綻無し
    private const uint MergeProbability = 25; // %

    private static bool RoomsMergeHorizontal(int leftRoomX, int roomY, uint seed)
    {
        uint h = WallHash(leftRoomX, roomY, seed, 'M');
        return (h % 100u) < MergeProbability;
    }

    private static bool RoomsMergeVertical(int roomX, int bottomRoomY, uint seed)
    {
        uint h = WallHash(roomX, bottomRoomY, seed, 'N');
        return (h % 100u) < MergeProbability;
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
        // 6. 不気味アンビエントサウンド (蛍光灯ハム) を BGM に重ねて流す。
        // Stop は LobbyBehaviour.OnDestroy + ExitBackrooms で対応
        BackroomsAmbient.Start();
        _visionPaused = false;
        _inBackrooms = true;
        _lastVisionValid = false; // idle skip cache invalidate — 次フレームで強制 rebuild
        _cullValid = false; // 距離 cull cache invalidate — 次フレで全 tile sweep

        Logger.Info($"Entered Backrooms (no-TP) seed={seed} disabledCols={disabledCols} disabledRs={disabledRs} tiles={SpawnedTiles.Count}", "BackroomsGen");
    }

    public static void ExitBackrooms(byte targetPid, bool silent = false)
    {
        if (LobbyBehaviour.Instance == null)
        {
            if (!silent) Utils.SendMessage("Not in lobby", targetPid);
            return;
        }

        if (PlayerControl.LocalPlayer == null) return;

        // 0. custom mesh 視界システム停止 + アンビエントも止める (/bbexit 経路用)
        _inBackrooms = false;
        _visionPaused = false;
        _lastVisionValid = false;
        _cullValid = false;
        DestroyVision();
        DestroyOverlay();
        BackroomsAmbient.Stop();
        RestoreEntityVisibility();

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
        WallGhostRenderers.Clear();
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

        if (!silent) Utils.SendMessage($"Exited Backrooms. Cleared {wiped} tiles, restored {restoredC} cols + {restoredR} SRs.", targetPid);
        Logger.Info($"Exited Backrooms cleared={wiped} restoredC={restoredC} restoredR={restoredR}", "BackroomsGen");
    }

    // クライアント設定 (OptionsMenuBehaviour) の Backrooms ロビー トグル切替時に呼ばれる。
    // ロビー滞在中なら即座に反映する: ON → 船を隠して入室 / OFF → 退室して船を復元。
    // メインメニュー/ゲーム中 (LobbyBehaviour 不在) では次回ロビー入室時に LobbyPatch 側で効く
    public static void OnEnabledToggled()
    {
        if (LobbyBehaviour.Instance == null || PlayerControl.LocalPlayer == null) return;

        bool enabled = Main.BackroomsEnabled?.Value ?? true;

        if (enabled)
        {
            if (_inBackrooms) return; // 既に入室済
            uint seed = AmongUsClient.Instance != null ? unchecked((uint)AmongUsClient.Instance.GameId) : 0u;
            if (seed == 0u) seed = _lastSeed != 0u ? _lastSeed : (uint)UnityEngine.Random.Range(1, int.MaxValue);
            HideVanillaShipImmediate();
            EnterBackrooms(seed, byte.MaxValue, silent: true);
        }
        else
        {
            // _inBackrooms でなくても HideVanillaShipImmediate で船が隠れている可能性があるので
            // ExitBackrooms に復元させる (Disabled* リストが空なら no-op)
            ExitBackrooms(byte.MaxValue, silent: true);
        }
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
        RestoreEntityVisibility();

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
        WallGhostRenderers.Clear();
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
        _upperVisionGO = null;
        _upperVisionMF = null;
        _upperVisionMesh = null;
        _upperVisionMat = null;
        _overlayGO = null; // 同上 — camera 子なので scene unload で消える
        _overlaySR = null;

        SpawnedTiles.Clear();
        SpawnedTilePositions.Clear();
        SpawnedTileChunkKeys.Clear();
        WallAabbs.Clear();
        WallAabbChunkKeys.Clear();
        WallGhostRenderers.Clear();
        _loadedChunks.Clear();
        _streamValid = false;
        // scene unload で entity 参照は dangling になるので cache だけクリア (SR 復元は不要)
        _entityBodies.Clear();
        _entityBodyRenderers.Clear();
        _entityPlayers.Clear();
        _entityPlayerRenderers.Clear();
        _entityCacheTime = -1f;
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
    // dark mesh の不透明度 (vertex color α、Lower/Upper 共通)。0〜1 範囲 (byte 0〜255 に変換)。
    //   ShadowMinAlpha: inner ring (視界境界) の α。0 だと視界境界で完全透明 → 影の輪郭が
    //     ぼやけて「影自体が薄い」感が出る。上げると視界境界が hard edge 寄りになり影が濃く感じる。
    //   ShadowMaxAlpha: outer ring (DarkRadius 端) の α。1.0 = 完全不可視、0.95 = 5% map 透過。
    // bodies/players は SR.enabled で独立に hard-cut されるので、ここを下げても body 可視性には影響しない。
    // user 要望 (2026-05-28): 影の中でも map が薄く見えるが、影自体は濃く感じるように
    private const float ShadowMinAlpha = 0.65f;
    private const float ShadowMaxAlpha = 0.95f;
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

    // Upper dark mesh (sortingOrder=+50): player/DeadBody/cosmetic を dark zone で覆う。
    // 2026-05-27 v3: mesh は Lower (_visionMesh) と **分離** (旧: 共有)。
    // 真因 — corner ray が donut inner ring に notch を作り、Upper (壁の前面) では notch dark が
    // 壁の上面を覆って「cell 境界の黒い縦線」「top band 消失」の症状を出す ([[plans/image-1-h-h-glowing-haven]])。
    // Upper は base ray fan のみで smooth donut を構築し、Lower は従来通り corner ray ありで sharp に保つ。
    private static GameObject _upperVisionGO;
    private static MeshFilter _upperVisionMF;
    private static Mesh _upperVisionMesh;
    private static Material _upperVisionMat;

    private static readonly List<(float cx, float cy, float halfX, float halfY)> _nearbyAabbs = new(64);

    // Per-ray cache: 1 回の RayAabbIntersect 結果を _nearbyAabbs と同 index で保持する。
    // Phase 1 (first hit 探索) で全壁の (tNear, tFar) を一度だけ計算しキャッシュ、
    // Phase 2 (chain traversal) はキャッシュを参照するだけで RayAabbIntersect 再呼びを 0 にする。
    //  - tNearCache[i] = float.NegativeInfinity → この壁とは hit 無し (Phase 2 で skip)
    //  - tFarCache 値は tNearCache が有効な場合のみ valid
    // size = 256 にしているのは _nearbyAabbs 上限 (実測 ~50 を上回ることがある最悪ケース) 確保のため。
    private const int RayCastWallCacheSize = 256;
    private static readonly float[] _tNearCache = new float[RayCastWallCacheSize];
    private static readonly float[] _tFarCache = new float[RayCastWallCacheSize];

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
    // Upper mesh 専用 buffer (2026-05-27 v3): base ray fan のみで smooth donut。
    // corner ray を含まないので _vertsBuf より小さく済むが、サイズ計算を簡単にするため同サイズ確保
    private static readonly Vector3[] _upperVertsBuf = new Vector3[2 * MaxRays];
    private static readonly int[] _upperTrisBuf = new int[6 * MaxRays];
    // Upper mesh の vertex color alpha gradient (2026-05-27 soft shadow 対応):
    //   inner ring 頂点 α=0 (donut 穴側、透過) → outer ring 頂点 α=255 (DarkRadius 側、不透明)
    //   donut の厚みを使って自然な fade を作る → 壁が「dark の中に浮く」感を解消
    //   shader 側は Sprites/Default 系 (vertex color × material color) で動くことを前提。
    //   borrowed shader が vertex color 非対応なら gradient は ignored になり旧動作 (hard edge) へ fallback
    private static readonly Color32[] _upperColorsBuf = new Color32[2 * MaxRays];

    // Lower mesh の vertex color alpha gradient (2026-05-28 影透明度調整):
    //   Upper と同形状の gradient (inner=0 → outer=ShadowMaxAlpha) を Lower にも適用。
    //   視界境界で床と壁が同じスピードで fade するため、discontinuity (床いきなり覆われる) を防ぐ。
    //   Lower mesh は corner ray 含む sharp donut なので size は _vertsBuf と一致 (2*MaxRays)
    private static readonly Color32[] _lowerColorsBuf = new Color32[2 * MaxRays];
    private static int _cosTableBuiltFor; // 0 = 未構築、>0 = この値で build 済

    // Idle skip: 直前 rebuild 時の player 位置。動いてなければ rebuild も transform 更新も skip。
    // ロビーは idle 時間が支配的なので、これ単体で大幅な FPS 回復が見込める
    private static Vector2 _lastVisionPlayer;
    private static bool _lastVisionValid;

    // ===== 距離 cull システム (2026-05-22 / 2026-05-23 改) =====
    // 視界 (VisionRadius=8u) 外は dark mesh で必ず黒く塗られるので、cull radius は vision 同等で十分。
    //   CullRadius=10u: vision 8u + safety 2u (CullMoveSqrThr=4 の cull center lag 吸収)
    //   → camera diagonal ≈7.1u を全方向でカバー、進行方向 screen edge のタイル pop-in を抑止
    //   active 領域 π×10² ≈ 314 cells
    // Wall AABB cache は SetActive 状態と独立に保持されるので視界 raycast は正しく occlude する。
    // CullMoveSqrThr=4 (sqrt=2u) — player 2u 進むまで cull 再判定 skip
    private static Vector2 _lastCullPlayer;
    private static bool _cullValid;
    private const float CullRadius = 10f;
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
    private static bool _overlaySuppressed; // hidden while an options/role menu is open — see SetOverlaySuppressed

    // 黄色フィルター基本値。明るすぎを避けるため RGB を少し下げて alpha を上げ、
    // 全体に「暗く黄ばんだ蛍光灯下」感を出す (2026-05-23 明度下げチューニング)
    private static readonly Color OverlayYellowBase = new(0.78f, 0.72f, 0.42f, 0.22f);
    // フリッカー時の覆い。「真っ暗」だと唐突なので alpha 0.32 黒で「明るさを下げる」感じに
    private static readonly Color OverlayBlackout = new(0f, 0f, 0f, 0.32f);
    // vignette mesh の色 — ほぼ黒だが僅かに暖色を残す (≒ #0a0805) で「淀んだ空気」感を出す。
    // alpha 1.0 で不透明維持。dark mesh は二段構成 (2026-05-27 改):
    //   Lower (sortingOrder=-7): floor を dark zone で覆う (壁は -3 以上で素通り)
    //   Upper (sortingOrder=+50): player/DeadBody/cosmetic を dark zone で覆う (local は donut 中心で常に可視)
    // 視界外の壁の表現は per-wall ghost sprite (sortingOrder=+60) で別途 alpha 制御
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

    // Hide/show the full-screen creepy overlay. The overlay is sortingOrder=100 (in front of player/
    // walls/floor) and assumes only the AU UI Canvas (chat/settings buttons) renders above it. But the
    // options/role menu is world-space SpriteRenderers on the Default layer, so the overlay tints it
    // yellow ("the lobby is bleeding through" — it isn't; it's this tint). Suppress while a menu is open.
    // No-op outside Backrooms (_overlayGO is null there).
    public static void SetOverlaySuppressed(bool suppressed)
    {
        _overlaySuppressed = suppressed;
        if (_overlayGO != null) _overlayGO.SetActive(!suppressed);
    }

    // UpdateVision 頭から呼ばれる。idle skip の影響を受けず毎フレ走らせて flicker を維持
    private static void UpdateOverlay()
    {
        if (_overlaySR == null || _overlaySuppressed) return;

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
        // sortingOrder spec (2026-05-27 v3): 二段 dark mesh + per-wall ghost 構成
        //   floor=-10 < Lower dark=-7 < walls=-5/-4/-3 < player/corpse=0 < Upper dark=+50 < ghost=+60 < overlay=+100
        //   Lower (-7, corner ray あり / sharp): floor を dark zone で覆う。壁 (-3 以上) は素通り
        //   Upper (+50, base ray のみ / smooth): player/DeadBody/cosmetic を dark zone で覆う。
        //     corner ray を入れない理由 — 壁角の notch が Upper では壁の上面を darken し
        //     「cell 境界の黒線」「top band 消失」を引き起こす ([[plans/image-1-h-h-glowing-haven]])。
        //   Ghost (+60, per-wall child): Upper dark の上に wall.png α=0.30 で透過、壁テクスチャをうっすら描画
        //   CastRayLength は tFar 返却で donut 穴が壁の向こうまで広がり、視界内の壁は full color で見える
        mr.sortingOrder = -7;

        Logger.Info($"Vision created: shader='{_visionMat.shader?.name}' sortingLayer='{mr.sortingLayerName}' order={mr.sortingOrder} worldPos={_visionGO.transform.position} layer={_visionGO.layer}", "BackroomsGen");

        // Upper dark mesh: mesh (_visionMesh) を Lower と共有。GO・renderer・material は別。
        // sortingOrder=+50 で player/DeadBody/cosmetic (sortingOrder ~0) を dark zone で覆う。
        // donut 形状なので donut hole (visible 領域) には triangle が無く local player は常に可視。
        _upperVisionGO = new GameObject("BackroomsVisionUpper");
        _upperVisionGO.transform.SetParent(null);
        _upperVisionGO.transform.position = _visionGO.transform.position;

        _upperVisionMF = _upperVisionGO.AddComponent<MeshFilter>();
        // Upper mesh は独立 alloc (2026-05-27 v3 改): Lower と別 topology を持たせるため。
        // Lower は corner ray ありで sharp、Upper は base ray のみで smooth。
        _upperVisionMesh = new Mesh { name = "BackroomsVisionMeshUpper" };
        _upperVisionMesh.MarkDynamic();
        _upperVisionMesh.bounds = new Bounds(Vector3.zero, new Vector3(2f * DarkRadius, 2f * DarkRadius, 1f));
        _upperVisionMF.sharedMesh = _upperVisionMesh;

        MeshRenderer umr = _upperVisionGO.AddComponent<MeshRenderer>();
        if (sd != null)
            _upperVisionMat = new Material(sd) { color = VignetteWarmDark };
        else if (src != null)
            _upperVisionMat = new Material(src) { color = VignetteWarmDark };
        else
            _upperVisionMat = new Material(Shader.Find("Hidden/Internal-Colored")) { color = VignetteWarmDark };
        umr.material = _upperVisionMat;
        umr.sortingLayerName = "Default";
        umr.sortingOrder = 50;

        Logger.Info($"Upper vision created: sortingOrder={umr.sortingOrder}", "BackroomsGen");
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
        if (_upperVisionGO != null)
        {
            UnityEngine.Object.Destroy(_upperVisionGO);
            _upperVisionGO = null;
            _upperVisionMF = null;
            _upperVisionMesh = null;
            _upperVisionMat = null;
        }

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
        if (_upperVisionGO != null) _upperVisionGO.SetActive(!_visionPaused);
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
        if (PerfLogEnabled) _perfCullSweeps++;

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

    // ─── Perf 診断 (/bbperf) ───────────────────────────────────────────────
    // 「動いてないのに重い / 画面半分が読み込まれてない」の原因切り分け用。
    // 1 秒ウィンドウごとに以下を "BackroomsPerf" タグでログに吐く:
    //   - FPS / 平均フレーム時間 / 最悪フレーム時間 (unscaledDeltaTime ベース)
    //   - Backrooms の per-frame CPU 時間 (Update{Streaming,Culling,Vision} を Stopwatch で計測)
    //       → BB CPU が小さいのに FPS が低い = GPU fill (dark mesh 面積) が支配。CPU 律速ではない
    //   - 視界 rebuild / cull sweep / streaming load の回数 (静止中=rebuild ≒ 0 を確認できる)
    //   - active/total tile 数・loaded chunk 数・ray 本数・隠したバニラ renderer 数
    //   - Camera.orthographicSize と CullRadius/DarkRadius の比較
    //       → ズームアウトで可視幅が Cull/Dark 半径を超えていれば「画面端の空白」はバグでなく仕様
    //         (タイルは 10u 圏外で SetActive(false)、dark mesh は 25u まで)
    // 計測 OFF 時は完全素通しで一切オーバーヘッドなし。_inBackrooms ではなく PerfLogEnabled だけで
    // 走るので、新オプションで Backrooms を ON/OFF した FPS を同じログで A/B 比較できる。
    public static bool PerfLogEnabled;
    private const float PerfLogInterval = 1f;
    private static long _perfWorkTicks;
    private static int _perfFrames;
    private static float _perfDeltaSum;
    private static float _perfMaxDelta;
    private static int _perfVisionRebuilds;
    private static int _perfCullSweeps;
    private static int _perfStreamUpdates;
    private static int _lastVisionRayCount;

    public static void TogglePerfLog(byte targetPid)
    {
        PerfLogEnabled = !PerfLogEnabled;
        ResetPerfCounters();
        string state = PerfLogEnabled ? "ON" : "OFF";
        Utils.SendMessage($"Backrooms perf logging = {state}. See log (tag: BackroomsPerf).", targetPid);
        Logger.Info($"Perf logging toggled {state}", "BackroomsPerf");
    }

    private static void ResetPerfCounters()
    {
        _perfWorkTicks = 0;
        _perfFrames = 0;
        _perfDeltaSum = 0f;
        _perfMaxDelta = 0f;
        _perfVisionRebuilds = 0;
        _perfCullSweeps = 0;
        _perfStreamUpdates = 0;
    }

    // LobbyBehaviour.Update の per-frame hook から呼ぶ per-frame 更新の入口。
    // 計測 ON の時だけ Stopwatch で Backrooms の CPU 時間を測る。
    public static void RunPerFrameUpdates()
    {
        if (!PerfLogEnabled)
        {
            UpdateStreaming();
            UpdateCulling();
            UpdateVision();
            return;
        }

        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        UpdateStreaming();
        UpdateCulling();
        UpdateVision();
        _perfWorkTicks += System.Diagnostics.Stopwatch.GetTimestamp() - start;

        _perfFrames++;
        float dt = Time.unscaledDeltaTime;
        _perfDeltaSum += dt;
        if (dt > _perfMaxDelta) _perfMaxDelta = dt;

        if (_perfDeltaSum >= PerfLogInterval) FlushPerfLog();
    }

    private static void FlushPerfLog()
    {
        int frames = _perfFrames;
        if (frames <= 0) { ResetPerfCounters(); return; }

        float avgFrameMs = _perfDeltaSum / frames * 1000f;
        float fps = frames / _perfDeltaSum;
        float worstMs = _perfMaxDelta * 1000f;
        double bbCpuPerFrameMs = (double)_perfWorkTicks / System.Diagnostics.Stopwatch.Frequency * 1000.0 / frames;
        double bbCpuPct = avgFrameMs > 0f ? bbCpuPerFrameMs / avgFrameMs * 100.0 : 0.0;

        // active/total tile sweep — interop が走るが診断時のみ・1 秒に 1 回なので許容
        int total = SpawnedTiles.Count;
        int active = 0;
        for (int i = 0; i < total; i++)
            if (SpawnedTiles[i] != null && SpawnedTiles[i].activeSelf) active++;

        Camera cam = Camera.main;
        float ortho = cam != null ? cam.orthographicSize : 0f;        // 可視域の half-height
        float aspect = cam != null ? cam.aspect : 16f / 9f;
        float halfW = ortho * aspect;                                  // 可視域の half-width
        bool viewBeyondCull = halfW > CullRadius;                      // 端にタイル消失帯が見える
        bool viewBeyondDark = halfW > DarkRadius;                      // 端に dark mesh 外の素抜けが見える

        Logger.Info(
            $"[{PerfLogInterval:0.#}s] FPS={fps:0.0} (avg {avgFrameMs:0.0}ms, worst {worstMs:0.0}ms) | " +
            $"BB CPU={bbCpuPerFrameMs:0.00}ms/f ({bbCpuPct:0.0}% of frame) | " +
            $"rebuilds={_perfVisionRebuilds}/{frames} cull={_perfCullSweeps} stream={_perfStreamUpdates} | " +
            $"tiles active={active}/{total} chunks={_loadedChunks.Count} rays={_lastVisionRayCount} renderersOff={DisabledRenderers.Count} | " +
            $"cam ortho={ortho:0.0} halfW={halfW:0.0}u vs Cull={CullRadius}u Dark={DarkRadius}u" +
            (viewBeyondCull ? " [VIEW>CULL: 端にタイル消失帯]" : string.Empty) +
            (viewBeyondDark ? " [VIEW>DARK: 端に dark mesh 外の素抜け]" : string.Empty) +
            $" | inBackrooms={_inBackrooms} paused={_visionPaused} reduceRays={Main.BackroomsReduceRays?.Value ?? false} throttle={Main.BackroomsThrottleVision?.Value ?? false} reduceProcgen={Main.BackroomsReduceProcgen?.Value ?? false}",
            "BackroomsPerf");

        ResetPerfCounters();
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
        if (PerfLogEnabled) _perfVisionRebuilds++;

        EnsureCosTable();

        _visionGO.transform.position = new Vector3(player.x, player.y, 0f);
        if (_upperVisionGO != null) _upperVisionGO.transform.position = _visionGO.transform.position;

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
        //    _rays[0..rfc) に angle 昇順で詰める (i/rfc * 2π を直接代入 → ソート不要)
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

        // 2b. Upper mesh は **ここで** base ray のみで build する (2026-05-27 v3)。
        //     corner ray を append する前に build することで、Upper donut inner ring は base ray のみの
        //     smooth polygon になり、壁角での notch (dip) が発生しない。
        //     → 視界内の壁上面が Upper dark に覆われない (= 黒い縦線 / top band 消失の症状解消)。
        //     許容 trade-off: 壁角の外側に thin 三角形 leak (corner leak) が出るが、Backrooms
        //     ロビーで cosmetic 上問題なし (壁が連続するため leak の角度範囲が極小)
        if (_upperVisionMesh != null)
            BuildDonutMesh(rfc, _upperVertsBuf, _upperTrisBuf, _upperColorsBuf, _upperVisionMesh);

        // 3. Corner rays — 各 visible AABB corner に ±ε rays で sharp shadow boundary を作る (Lower mesh 専用)
        //    これがないと角の向こうに広い扇形 (corner leak) が visible 領域として残る
        //    Upper mesh には影響しない (2b で先に build 済)
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
        _lastVisionRayCount = count; // perf 診断用 (base fan + corner rays の合計本数)

        // 5-8. Lower mesh build (corner ray 含む sharp donut)。
        //   vertex color gradient で Upper と同じ inner=0/outer=ShadowMaxAlpha の fade を作り、
        //   視界境界で床と壁が同期して fade するようにする (床が「いきなり覆われる」不連続を解消)
        BuildDonutMesh(count, _vertsBuf, _trisBuf, _lowerColorsBuf, _visionMesh);

        // 9. Per-entity visibility hard-cut (2026-05-28)
        //    Upper dark mesh は gradient alpha (inner 0 → outer 1) なので、視界外でも body/player が
        //    透けて見える。user spec「player/死体は完全に不可視、map は薄く見える」に合わせ、
        //    DeadBody / 非 local PlayerControl の SR を視界判定で enabled toggle。
        //    map (壁/床) は触らず Upper dark gradient のまま「薄く見える」を維持。
        UpdateEntityVisibility(player);

        // 10. Per-wall ghost α 更新 (v8 2026-05-28 — directional + occlusion の二重判定)
        //    各 wall に 2 種類の影要因を計算して max を取る:
        //
        //    (a) Directional: player の y より南 (dy < 0) にある壁を darken
        //        擬似 3D 視点で「背中側 = 南側」の壁を見えにくくする (AU vanilla 風)
        //    (b) Occlusion: `CastRayFirstHit` で別の壁の奥に隠れた壁を darken
        //        per-ray な occluder 判定
        //
        //    targetAlpha = max(directional, occlusion) * WallGhostAlphaDarkZone
        //    → 北側で occluder の壁: α=0 (明るい)
        //    → 北側で occluded の壁: α 増 (薄暗い)
        //    → 南側の壁: 全て darken (occlusion 不問)
        if (!EnableWallGhost) return;

        float darkR2 = DarkRadius * DarkRadius;
        int wallCount = WallAabbs.Count;
        for (int wi = 0; wi < wallCount; wi++)
        {
            SpriteRenderer ghost = wi < WallGhostRenderers.Count ? WallGhostRenderers[wi] : null;
            if (ghost == null) continue;

            var w = WallAabbs[wi];
            float dx = w.cx - player.x;
            float dy = w.cy - player.y;
            float d2 = dx * dx + dy * dy;

            // DarkRadius 圏外: dark mesh の範囲外なので alpha 0 (camera 視野からも遠い)
            if (d2 > darkR2)
            {
                if (ghost.color.a > 0.01f) ghost.color = SetAlpha(ghost.color, 0f);
                continue;
            }

            float dist = Mathf.Sqrt(d2);
            if (dist < 1e-4f) { ghost.color = SetAlpha(ghost.color, 0f); continue; }

            // (a) Directional shadow: dy < 0 (南側) で α 増
            //     -dy / threshold で 0→1 に smoothstep
            float dirT = Mathf.Clamp01(-dy / WallShadowYThreshold);
            float dirAlpha = dirT * dirT * (3f - 2f * dirT);

            // (b) Occlusion shadow: 別の壁の奥にいるか。
            //     center ray だけだと近接 wall の grazing で誤判定するので、壁の AABB の
            //     4 corner それぞれに ray を撃ち、最も visible な corner (= minOvershoot) を採用。
            //     wall の一部でも player から直接見えるなら「occluder」と判定 → α=0
            //     close-range guard も維持 (corner test を skip して高速 path)
            float closestX = Mathf.Clamp(player.x, w.cx - w.halfX, w.cx + w.halfX);
            float closestY = Mathf.Clamp(player.y, w.cy - w.halfY, w.cy + w.halfY);
            float closestDx = closestX - player.x;
            float closestDy = closestY - player.y;
            float closestDist2 = closestDx * closestDx + closestDy * closestDy;

            float occAlpha = 0f;
            if (closestDist2 > CloseRangeNoOcclusionRadius * CloseRangeNoOcclusionRadius)
            {
                float minOvershoot = float.MaxValue;
                for (int ci = 0; ci < 4; ci++)
                {
                    float cornerX = w.cx + ((ci & 1) == 0 ? -w.halfX : w.halfX);
                    float cornerY = w.cy + ((ci & 2) == 0 ? -w.halfY : w.halfY);
                    float cdx = cornerX - player.x;
                    float cdy = cornerY - player.y;
                    float cd2 = cdx * cdx + cdy * cdy;
                    if (cd2 < 1e-4f) { minOvershoot = -1f; break; }
                    float cd = Mathf.Sqrt(cd2);
                    float ccos = cdx / cd;
                    float csin = cdy / cd;
                    float cornerHit = CastRayFirstHit(player, ccos, csin);
                    float ov = cd - cornerHit;
                    if (ov < minOvershoot) minOvershoot = ov;
                }
                float occT = Mathf.Clamp01(minOvershoot / WallShadowThreshold);
                occAlpha = occT * occT * (3f - 2f * occT);
            }

            // max を取る (どちらかの条件が満たされれば darken)
            float targetAlpha = Mathf.Max(dirAlpha, occAlpha) * WallGhostAlphaDarkZone;

            if (Mathf.Abs(ghost.color.a - targetAlpha) > 0.01f)
                ghost.color = SetAlpha(ghost.color, targetAlpha);
        }
    }

    // SpriteRenderer.color の alpha だけ更新する helper (struct copy + mutate + assign)
    private static Color SetAlpha(Color c, float a) { c.a = a; return c; }

    // DeadBody / 非 local PlayerControl の SR を視界判定で enabled toggle。
    // 視界内 = enabled true (vanilla 表示)、視界外 = enabled false (完全不可視)。
    // map (壁/床) は触らず、Upper dark gradient のまま薄く見える挙動を維持
    private static void UpdateEntityVisibility(Vector2 player)
    {
        if (_entityCacheTime < 0f || Time.time - _entityCacheTime > EntityCacheRefreshInterval)
        {
            RefreshEntityCache();
            _entityCacheTime = Time.time;
        }

        float darkR2 = DarkRadius * DarkRadius;

        // DeadBodies (LobbyCorpses 含む)
        for (int i = 0; i < _entityBodies.Count; i++)
        {
            DeadBody body = _entityBodies[i];
            if (body == null) continue;
            Vector3 bp = body.transform.position;
            bool visible = ComputeEntityVisible(player, bp.x, bp.y, darkR2);
            SetRenderersEnabled(_entityBodyRenderers[i], visible);
        }

        // 非 local PlayerControl (host 自身は常時可視 = donut 中心)
        PlayerControl lp = PlayerControl.LocalPlayer;
        for (int i = 0; i < _entityPlayers.Count; i++)
        {
            PlayerControl pc = _entityPlayers[i];
            if (pc == null || pc == lp) continue;
            Vector3 pp = pc.transform.position;
            bool visible = ComputeEntityVisible(player, pp.x, pp.y, darkR2);
            SetRenderersEnabled(_entityPlayerRenderers[i], visible);
        }
    }

    // entity 1 個に対して「player から見て donut hole の中か」を判定。
    // CastRayLength を再利用 — 角度 (cos, sin) を計算して 1 ray 撃つだけ
    private static bool ComputeEntityVisible(Vector2 player, float entX, float entY, float darkR2)
    {
        float dx = entX - player.x;
        float dy = entY - player.y;
        float d2 = dx * dx + dy * dy;
        if (d2 > darkR2) return false;          // DarkRadius 圏外は完全不可視
        if (d2 < 1e-4f) return true;            // player と重なる位置 (host 自身など) は可視
        float dist = Mathf.Sqrt(d2);
        float cos = dx / dist;
        float sin = dy / dist;
        float hit = CastRayLength(player, cos, sin);
        return dist <= hit + 0.05f;             // donut inner ring の内側 = 視界内
    }

    // 0.5s ごとに呼ばれる FindObjectsOfType ベースの cache 更新。
    // body/player の出入りは lobby ではまれなので 0.5s 粒度で十分。SR array も同時に cache
    private static void RefreshEntityCache()
    {
        _entityBodies.Clear();
        _entityBodyRenderers.Clear();
        DeadBody[] bodies = Object.FindObjectsOfType<DeadBody>();
        for (int i = 0; i < bodies.Length; i++)
        {
            if (bodies[i] == null) continue;
            _entityBodies.Add(bodies[i]);
            _entityBodyRenderers.Add(bodies[i].GetComponentsInChildren<SpriteRenderer>());
        }

        _entityPlayers.Clear();
        _entityPlayerRenderers.Clear();
        foreach (PlayerControl pc in PlayerControl.AllPlayerControls)
        {
            if (pc == null) continue;
            _entityPlayers.Add(pc);
            _entityPlayerRenderers.Add(pc.GetComponentsInChildren<SpriteRenderer>());
        }
    }

    // SR array の enabled を一括 toggle。null チェック + 不変なら interop skip
    private static void SetRenderersEnabled(SpriteRenderer[] srs, bool enabled)
    {
        if (srs == null) return;
        for (int i = 0; i < srs.Length; i++)
        {
            if (srs[i] == null) continue;
            if (srs[i].enabled != enabled) srs[i].enabled = enabled;
        }
    }

    // Backrooms 退出時 cleanup — UpdateEntityVisibility で false にした SR を全部 true に戻し、
    // cache を解放。これを呼ばないと vanilla ロビーに戻った後も body/player が消えたまま
    private static void RestoreEntityVisibility()
    {
        for (int i = 0; i < _entityBodyRenderers.Count; i++)
            SetRenderersEnabled(_entityBodyRenderers[i], true);
        for (int i = 0; i < _entityPlayerRenderers.Count; i++)
            SetRenderersEnabled(_entityPlayerRenderers[i], true);
        _entityBodies.Clear();
        _entityBodyRenderers.Clear();
        _entityPlayers.Clear();
        _entityPlayerRenderers.Clear();
        _entityCacheTime = -1f;
    }

    // _rays[0..n) を inner ring、DarkRadius を outer ring とする donut mesh を build して mesh に upload (2026-05-27 v3)。
    //   verts: 上限 2*MaxRays、未使用 slot は前 frame の stale data だが triangle が指さないので無害
    //   tris : 6*MaxRays、未使用 slot は全 index=0 (degenerate) で埋めて Mesh.Clear 回避
    //   colors: null なら vertex color を触らない (Lower mesh は solid 暗色)。
    //           non-null なら inner ring α=0 (透過)、outer ring α=255 (不透明) で gradient (Upper mesh の soft edge)。
    //   _rays は angle 昇順前提 (base ray のみなら自然順、corner ray 含むなら呼び出し前に Array.Sort)
    private static void BuildDonutMesh(int n, Vector3[] verts, int[] tris, Color32[] colors, Mesh mesh)
    {
        if (n < 2 || mesh == null) return;
        bool hasColors = colors != null;
        Color32 innerCol = new(255, 255, 255, (byte)(ShadowMinAlpha * 255f));
        Color32 outerCol = new(255, 255, 255, (byte)(ShadowMaxAlpha * 255f));

        // Build vertices: inner ring [0..n) は hit dist、outer ring [n..2*n) は DarkRadius
        for (int i = 0; i < n; i++)
        {
            float c = _rays[i].Cos;
            float s = _rays[i].Sin;
            float d = _rays[i].Dist;
            verts[i] = new Vector3(c * d, s * d, 0f);
            verts[i + n] = new Vector3(c * DarkRadius, s * DarkRadius, 0f);
            if (hasColors)
            {
                colors[i] = innerCol;     // inner ring 透過
                colors[i + n] = outerCol; // outer ring 不透明
            }
        }

        // Build triangles (donut topology — slice i は inner[i,next] / outer[i,next] の 2 tri)
        for (int i = 0; i < n; i++)
        {
            int next = (i + 1) % n;
            int t = i * 6;
            tris[t + 0] = i;
            tris[t + 1] = i + n;
            tris[t + 2] = next + n;
            tris[t + 3] = i;
            tris[t + 4] = next + n;
            tris[t + 5] = next;
        }

        // 未使用 triangle slot を degenerate 化
        int validTris = n * 6;
        for (int i = validTris; i < tris.Length; i++)
            tris[i] = 0;

        // Upload
        //   partial upload (n*2 だけ送る) は 2026-05-22 に試したが画面崩れで没。full vert upload に固定。
        //   static bounds は CreateVision で 1 度設定済 → calculateBounds=false
        mesh.SetVertices(verts);
        mesh.SetTriangles(tris, 0, tris.Length, 0, calculateBounds: false);
        if (hasColors) mesh.SetColors(colors);
    }

    // 与えられた単位方向 (cos, sin) に origin から ray を撃ち、最初に当たった「連続壁チェーン」の
    // 向こう側距離 (tFar) を返す。
    //
    // ## 仕様遷移 (重要)
    //
    // 旧 v1: tNear (最近接壁の手前面) を返す
    // v2 (2026-05-27 前段): tFar (最近接壁の向こう側) を返す
    //   - 当時 dark mesh が壁より前面 (sortingOrder=-1) で、tNear だと壁本体が dark mesh に塗りつぶされて見えなくなる、が理由
    // v3 (2026-05-27 後段): **連続する隣接壁を traverse してチェーン全体の tFar を返す**
    //   - 連続 cell の V 字状段差を chain でならす (並列壁が一つの壁として動く)
    // v4 (2026-05-28 前段): v1 (tNear) に回帰 — 「壁が自分の影に被って暗くなる」効果のため
    // v5 (2026-05-28 後段、現状): **v3 (chain tFar) に再回帰**
    //   - user 要望: 「同じ壁でも視点角度によって影が乗ったり乗らなかったりするように」(2026-05-28)
    //   - tFar の donut hole は最近接壁の向こうまで延びるので、その壁を occluder とする ray では
    //     壁が donut hole 内 = 影なし、別の ray で他の壁の奥に隠れる場合は donut shadow 内 = 影あり
    //   - per-ray の occlusion 判定が「同じ壁でも見える角度では明るく、隠れる角度では暗く」を自然に実現
    //
    // ## ケース
    // - 通常 (origin が AABB 外): 最小 tNear の AABB を選び、その tFar から chain を伸ばす
    // - origin が AABB 内 (player が壁の中に詰まった): その AABB の tFar から chain を伸ばす
    //   → vision が壁の出口表面でクリップされて「壁を貫通して見える」バグを防止
    private static float CastRayLength(Vector2 origin, float cos, float sin)
    {
        int count = _nearbyAabbs.Count;
        if (count > RayCastWallCacheSize) count = RayCastWallCacheSize;

        bool useCosInv = Mathf.Abs(cos) >= 1e-6f;
        bool useSinInv = Mathf.Abs(sin) >= 1e-6f;
        float invCos = useCosInv ? 1f / cos : 0f;
        float invSin = useSinInv ? 1f / sin : 0f;

        // --- Phase 1: 全壁の (tNear, tFar) を 1 度だけ計算してキャッシュ + first hit 確定 ---
        // chain traversal で同じ wall を 16 回 RayAabbIntersect し直すのを避けるため、
        // Phase 2 はキャッシュ参照だけで済む形にする (IL2CPP 関数呼び出しオーバーヘッド削減)
        float bestHitDist = VisionRadius;
        float bestExitDist = VisionRadius;
        int bestIdx = -1;
        for (int i = 0; i < count; i++)
        {
            var w = _nearbyAabbs[i];
            float minX = w.cx - w.halfX;
            float maxX = w.cx + w.halfX;
            float minY = w.cy - w.halfY;
            float maxY = w.cy + w.halfY;

            float tx1, tx2;
            if (!useCosInv)
            {
                if (origin.x < minX || origin.x > maxX) { _tNearCache[i] = float.NegativeInfinity; continue; }
                tx1 = float.NegativeInfinity;
                tx2 = float.PositiveInfinity;
            }
            else
            {
                tx1 = (minX - origin.x) * invCos;
                tx2 = (maxX - origin.x) * invCos;
                if (tx1 > tx2) { (tx1, tx2) = (tx2, tx1); }
            }

            float ty1, ty2;
            if (!useSinInv)
            {
                if (origin.y < minY || origin.y > maxY) { _tNearCache[i] = float.NegativeInfinity; continue; }
                ty1 = float.NegativeInfinity;
                ty2 = float.PositiveInfinity;
            }
            else
            {
                ty1 = (minY - origin.y) * invSin;
                ty2 = (maxY - origin.y) * invSin;
                if (ty1 > ty2) { (ty1, ty2) = (ty2, ty1); }
            }

            float tN = Mathf.Max(tx1, ty1);
            float tF = Mathf.Min(tx2, ty2);

            if (tF < 0f || tN > tF) { _tNearCache[i] = float.NegativeInfinity; continue; }

            _tNearCache[i] = tN;
            _tFarCache[i] = tF;

            float thisHit;
            if (tN > 0f)        thisHit = tN;
            else if (tF > 0f)   thisHit = 0f;
            else                continue;

            if (thisHit < bestHitDist)
            {
                bestHitDist = thisHit;
                bestExitDist = tF;
                bestIdx = i;
            }
        }

        if (bestIdx < 0) return VisionRadius < MinHitDistance ? MinHitDistance : VisionRadius;

        // --- Phase 2: 隣接壁 chain を traverse して bestExitDist を伸ばす (キャッシュ参照のみ) ---
        // ある壁の tFar 出口点 = 別壁の tNear 入射点 (差 ≤ eps) なら、その壁を chain に取り込む。
        // 連続 WallH 行 / WallV 列 / L 字接合などで「壁の塊」として動く。
        //
        // v7 (2026-05-28): donut mesh は chain 復活で smooth 描画 (V 字段差を donut shape から排除)。
        //   per-wall occlusion 暗化は別途 `CastRayFirstHit` (chain なしの first-hit tFar) を使い、
        //   ghost overlay で表現する。
        const int ChainExtensionDepth = 8;
        if (ChainExtensionDepth > 0)
        {
            const float chainEps = 0.02f;
            int safety = ChainExtensionDepth;
            bool extended;
            do
            {
                extended = false;
                float exitNow = bestExitDist;
                float exitMinusEps = exitNow - chainEps;
                float exitPlusEps = exitNow + chainEps;
                for (int i = 0; i < count; i++)
                {
                    if (i == bestIdx) continue;
                    float tN = _tNearCache[i];
                    if (tN < exitMinusEps || tN > exitPlusEps) continue;     // 隣接条件外
                    float tF = _tFarCache[i];
                    if (tF <= exitNow + 1e-4f) continue;                     // chain 前進無し
                    bestExitDist = tF;
                    bestIdx = i;
                    extended = true;
                    break;
                }
            }
            while (extended && --safety > 0);
        }

        return bestExitDist < MinHitDistance ? MinHitDistance : bestExitDist;
    }

    // Phase 1 only — chain extension を走らせず、最初に当たった壁の tFar を返す。
    // per-wall occlusion 判定用 (chain で繋がった奥の壁を「visible」扱いしないため)。
    // `CastRayLength` と同じ Phase 1 ロジックだが、戻り値が chain 拡張前の first-hit tFar
    private static float CastRayFirstHit(Vector2 origin, float cos, float sin)
    {
        int count = _nearbyAabbs.Count;
        if (count > RayCastWallCacheSize) count = RayCastWallCacheSize;

        bool useCosInv = Mathf.Abs(cos) >= 1e-6f;
        bool useSinInv = Mathf.Abs(sin) >= 1e-6f;
        float invCos = useCosInv ? 1f / cos : 0f;
        float invSin = useSinInv ? 1f / sin : 0f;

        float bestHitDist = VisionRadius;
        float bestExitDist = VisionRadius;
        for (int i = 0; i < count; i++)
        {
            var w = _nearbyAabbs[i];
            float minX = w.cx - w.halfX;
            float maxX = w.cx + w.halfX;
            float minY = w.cy - w.halfY;
            float maxY = w.cy + w.halfY;

            float tx1, tx2;
            if (!useCosInv)
            {
                if (origin.x < minX || origin.x > maxX) continue;
                tx1 = float.NegativeInfinity;
                tx2 = float.PositiveInfinity;
            }
            else
            {
                tx1 = (minX - origin.x) * invCos;
                tx2 = (maxX - origin.x) * invCos;
                if (tx1 > tx2) { (tx1, tx2) = (tx2, tx1); }
            }

            float ty1, ty2;
            if (!useSinInv)
            {
                if (origin.y < minY || origin.y > maxY) continue;
                ty1 = float.NegativeInfinity;
                ty2 = float.PositiveInfinity;
            }
            else
            {
                ty1 = (minY - origin.y) * invSin;
                ty2 = (maxY - origin.y) * invSin;
                if (ty1 > ty2) { (ty1, ty2) = (ty2, ty1); }
            }

            float tN = Mathf.Max(tx1, ty1);
            float tF = Mathf.Min(tx2, ty2);
            if (tF < 0f || tN > tF) continue;

            float thisHit;
            if (tN > 0f)        thisHit = tN;
            else if (tF > 0f)   thisHit = 0f;
            else                continue;

            if (thisHit < bestHitDist)
            {
                bestHitDist = thisHit;
                bestExitDist = tF;
            }
        }

        return bestExitDist < MinHitDistance ? MinHitDistance : bestExitDist;
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
        // UpdateStreaming (chunk 境界跨ぎで Load/Unload 差分・早期 return が普通) → UpdateCulling → UpdateVision を
        // まとめて呼ぶ。/bbperf ON 時はこの 3 つの CPU 時間を計測して 1 秒ごとにログ出力する
        BackroomsLobby.RunPerFrameUpdates();
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
