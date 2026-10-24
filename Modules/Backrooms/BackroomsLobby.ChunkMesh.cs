using System.Collections.Generic;
using UnityEngine;

namespace EndKnot.Modules;

// procgen Backrooms のチャンク描画。タイル 1 枚 = GameObject 数個 (床 1・壁 5〜12) の作りだと 3×3 チャンクで
// 約 15,000 GO / 14,000 SpriteRenderer になり、ロビーの常駐メモリを数十 MB 押し上げる。
// ここではチャンク 1 枚を「(テクスチャ, sortingOrder) の組ごとに 1 メッシュ」+「矩形を結合した BoxCollider2D 数個」
// に畳み、GO をチャンクあたり 10 個前後にする。見た目は SpawnTile 系 (BuildWallHComposite / BuildWallV /
// AddWallVBottomCap / AddWallHBottomConnector / MaybeAddFloorStain / ApplyFloorVariation) の寸法をそのまま写す。
// 視界の遮蔽と影 caster は従来どおりセル単位の WallAabbs から作るので、ここでも同じ形で積む。
// 自前 CPU 視界モードは壁ごとのゴースト SR を毎フレーム書き換えるので、この方式はバニラ影モードの procgen 限定。
public static partial class BackroomsLobby
{
    private sealed class ChunkRender
    {
        public GameObject Root;
        public readonly List<Mesh> Meshes = [];
    }

    private static readonly Dictionary<long, ChunkRender> _chunkRenders = [];

    // GenerateLobby の時点で決める (途中で切り替えると旧方式のタイルと混ざるので、切替は再生成で行う)
    private static bool _chunkMeshMode;

    private static bool WantChunkMesh => (Main.BackroomsChunkMesh?.Value ?? true)
                                         && BackroomsConfig.UseVanillaShadow
                                         && EkmapLoader.ActiveSource == null;

    // 入室位置まわりの強制床セル (PackChunkKey(wx, wy) で詰める)。ClassifyCell が先に見るので、
    // 描画・当たり判定・影・経路探索・隣セルの継ぎ目判定がすべて同じ「床」を見る。
    private static readonly HashSet<long> _forcedFloorCells = [];

    private static void FillForcedSpawnFloor(Vector2 feet)
    {
        _forcedFloorCells.Clear();
        if (EkmapLoader.ActiveSource != null) return;
        int spx = Mathf.RoundToInt(feet.x);
        int spy = Mathf.RoundToInt(feet.y);
        _forcedFloorCells.Add(PackChunkKey(spx, spy));
        _forcedFloorCells.Add(PackChunkKey(spx + 1, spy));
        _forcedFloorCells.Add(PackChunkKey(spx - 1, spy));
        _forcedFloorCells.Add(PackChunkKey(spx, spy + 1));
        _forcedFloorCells.Add(PackChunkKey(spx, spy - 1));
    }

    // ── テクスチャ枠 ──
    private const int SlotFloor = 0, SlotWall = 1, SlotBase = 2, SlotGradV = 3, SlotGradH = 4, SlotStain = 5, SlotCount = 6;

    private struct SlotSprite
    {
        public bool Valid;
        public float MinX, MinY, MaxX, MaxY; // スプライトの局所 bounds (scale 1)
        public float U0, V0, U1, V1;
    }

    private static readonly SlotSprite[] _slotSprites = new SlotSprite[SlotCount];
    private static readonly Material[] _chunkSlotMats = new Material[SlotCount];
    private static readonly Texture2D[] _chunkSlotTex = new Texture2D[SlotCount];

    private static bool PrepareSlot(int slot, Sprite sp)
    {
        _slotSprites[slot].Valid = false;
        if (sp == null) return false;
        Texture2D tex = sp.texture;
        if (tex == null) return false;

        Bounds b = sp.bounds;
        Vector3 mn = b.min, mx = b.max;
        Rect tr = sp.textureRect;
        float w = tex.width, h = tex.height;
        _slotSprites[slot] = new SlotSprite
        {
            Valid = true,
            MinX = mn.x, MinY = mn.y, MaxX = mx.x, MaxY = mx.y,
            U0 = tr.x / w, V0 = tr.y / h, U1 = (tr.x + tr.width) / w, V1 = (tr.y + tr.height) / h
        };

        Material m = _chunkSlotMats[slot];
        if (m != null && _chunkSlotTex[slot] == tex) return true;
        // テクスチャが替わった時の古いマテリアルは、まだ組んであるチャンクが使っているので壊さない
        Material template = SpriteDefaultMaterialTemplate();
        m = template != null ? new Material(template) : new Material(Shader.Find("Sprites/Default"));
        m.mainTexture = tex;
        m.name = "BackroomsChunk" + slot;
        m.hideFlags = HideFlags.HideAndDontSave;
        _chunkSlotMats[slot] = m;
        _chunkSlotTex[slot] = tex;
        return true;
    }

    // SpriteRenderer の既定マテリアル (タイルが使っていた物) を写す。バニラ影の受信は ShadowQuad 側の
    // _Mask とこのマテリアルの組み合わせで決まるので、シェーダだけ同じ別物にしない。
    private static Material _spriteDefaultTemplate;
    private static Material SpriteDefaultMaterialTemplate()
    {
        if (_spriteDefaultTemplate != null) return _spriteDefaultTemplate;
        GameObject tmp = new("BackroomsChunkMatProbe");
        try
        {
            SpriteRenderer sr = tmp.AddComponent<SpriteRenderer>();
            Material shared = sr.sharedMaterial;
            if (shared != null)
            {
                _spriteDefaultTemplate = new Material(shared) { name = "BackroomsChunkTemplate", hideFlags = HideFlags.HideAndDontSave };
            }
        }
        finally { Object.DestroyImmediate(tmp); }

        return _spriteDefaultTemplate;
    }

    // ── 四角形の溜め込み ──
    private sealed class QuadBatch
    {
        public int Slot, Order;
        public readonly List<Vector3> V = [];
        public readonly List<Vector2> U = [];
        public readonly List<Color32> C = [];
    }

    private static readonly List<QuadBatch> _batchPool = [];
    private static int _batchUsed;

    private static QuadBatch GetBatch(int slot, int order)
    {
        for (int i = 0; i < _batchUsed; i++)
        {
            QuadBatch b = _batchPool[i];
            if (b.Slot == slot && b.Order == order) return b;
        }

        if (_batchUsed == _batchPool.Count) _batchPool.Add(new QuadBatch());
        QuadBatch nb = _batchPool[_batchUsed++];
        nb.Slot = slot;
        nb.Order = order;
        nb.V.Clear();
        nb.U.Clear();
        nb.C.Clear();
        return nb;
    }

    // SpriteRenderer (flipX/flipY) を transform (中心 c・回転 cos/sin・拡大 sx/sy) に置いた時の 4 隅を書く
    private static void EmitQuad(int slot, int order, float cx, float cy, float sx, float sy, float rc, float rs, bool flipX, bool flipY, Color32 col)
    {
        ref SlotSprite s = ref _slotSprites[slot];
        if (!s.Valid) return;
        QuadBatch b = GetBatch(slot, order);
        for (int k = 0; k < 4; k++)
        {
            bool right = k is 1 or 2, top = k >= 2;
            float lx = right ? s.MaxX : s.MinX;
            float ly = top ? s.MaxY : s.MinY;
            if (flipX) lx = -lx;
            if (flipY) ly = -ly;
            lx *= sx;
            ly *= sy;
            b.V.Add(FxMath.V3(cx + lx * rc - ly * rs, cy + lx * rs + ly * rc, 0f));
            b.U.Add(FxMath.V2(right ? s.U1 : s.U0, top ? s.V1 : s.V0));
            b.C.Add(col);
        }
    }

    private static void EmitRect(int slot, int order, float cx, float cy, float sx, float sy, Color32 col) =>
        EmitQuad(slot, order, cx, cy, sx, sy, 1f, 0f, false, false, col);

    private static Color32 ToColor32(Color c) =>
        FxMath.Rgba32((byte)(FxMath.Clamp01(c.r) * 255f + 0.5f), (byte)(FxMath.Clamp01(c.g) * 255f + 0.5f), (byte)(FxMath.Clamp01(c.b) * 255f + 0.5f), (byte)(FxMath.Clamp01(c.a) * 255f + 0.5f));

    // ── セルの中身 (SpawnTile 系と同じ寸法) ──
    private static int _floorSlot;
    private static Color32 _floorCol, _whiteCol, _darkCol, _edgeCol, _shadowCol, _stainCol;

    private static void FloorVariation(int cx, int cy, out bool flipX, out bool flipY, out bool rot90)
    {
        uint h = (uint)(cx * 73856093) ^ (uint)(cy * 19349663);
        h ^= h >> 16;
        h *= 0x85ebca6b;
        h ^= h >> 13;
        flipX = (h & 1u) != 0;
        flipY = (h & 2u) != 0;
        rot90 = (h & 4u) != 0;
    }

    private static void EmitFloorCell(int wx, int wy, bool withStain)
    {
        FloorVariation(wx, wy, out bool fx, out bool fy, out bool rot90);
        float rc = rot90 ? 0f : 1f, rs = rot90 ? 1f : 0f;
        EmitQuad(_floorSlot, -10, wx, wy, 1f, 1f, rc, rs, fx, fy, _floorCol);
        if (withStain) EmitFloorStain(wx, wy, rc, rs);
    }

    // MaybeAddFloorStain と同じハッシュ・同じ形。シミの子はタイルの回転 (rc, rs) を受ける
    private static void EmitFloorStain(int cx, int cy, float rc, float rs)
    {
        uint h = (uint)(cx * 374761393) ^ (uint)(cy * 668265263);
        h = (h ^ (h >> 13)) * 1274126177u;
        h ^= h >> 16;
        if ((h % 100u) >= 7u) return;

        float ox = ((((h >> 4) & 0x0Fu) / 15f) - 0.5f) * 0.4f;
        float oy = ((((h >> 12) & 0x0Fu) / 15f) - 0.5f) * 0.4f;

        for (int i = 0; i < 3; i++)
        {
            uint subH = h * (uint)(7919 + i * 31);
            subH ^= subH >> 13;
            subH *= 0x85ebca6bu;
            subH ^= subH >> 11;

            float sx = 0.30f + ((subH & 0xFFu) / 255f) * 0.30f;
            float sy = 0.30f + (((subH >> 8) & 0xFFu) / 255f) * 0.30f;
            float subOx = ((((subH >> 16) & 0x0Fu) / 15f) - 0.5f) * 0.22f;
            float subOy = ((((subH >> 20) & 0x0Fu) / 15f) - 0.5f) * 0.22f;
            float subRot = (((subH >> 24) & 0xFFu) / 255f) * 360f * (FxMath.PI / 180f);

            float lx = ox + subOx, ly = oy + subOy;
            float wx = cx + lx * rc - ly * rs;
            float wy = cy + lx * rs + ly * rc;
            float c2 = FxMath.Cos(subRot), s2 = FxMath.Sin(subRot);
            // タイル回転 ∘ ブロブ回転
            float rc2 = rc * c2 - rs * s2, rs2 = rs * c2 + rc * s2;
            EmitQuad(SlotStain, -9, wx, wy, sx, sy, rc2, rs2, false, false, _stainCol);
        }
    }

    private static void EmitWallVOutline(float cx, float cy, float heightScale, bool body)
    {
        if (body) EmitRect(SlotBase, -4, cx, cy, 0.4f, heightScale, _darkCol);
        EmitRect(SlotBase, -3, cx - 0.2125f, cy, 0.025f, heightScale, _edgeCol);
        EmitRect(SlotBase, -3, cx + 0.2125f, cy, 0.025f, heightScale, _edgeCol);
    }

    private static void EmitContactShadow(float cx, float cy, float widthScale) =>
        EmitRect(SlotGradV, -9, cx, cy - 0.61f, widthScale, 0.22f, _shadowCol);

    private static void EmitWallHCell(int wx, int wy, bool connector)
    {
        EmitRect(SlotWall, -5, wx, wy, 1f, 1f, _whiteCol);
        if (BackroomsConfig.ShowWallHTopBand) EmitRect(SlotBase, -3, wx, wy + 0.4f, 1f, 0.2f, _darkCol);
        EmitContactShadow(wx, wy, 1f);
        // 真下が縦壁: 縦壁の輪郭を南半分 (高さ 0.8) に延ばして上端の帯と L 字につなぐ
        if (connector) EmitWallVOutline(wx, wy - 0.1f, 0.8f, true);
    }

    private static void EmitWallVCell(int wx, int wy, bool bottomCap)
    {
        EmitFloorCell(wx, wy, false);
        EmitWallVOutline(wx, wy, 1f, !bottomCap);
        // 側面の接地影 (左は濃い側を body へ向けるため左右反転)
        EmitQuad(SlotGradH, -6, wx - 0.3025f, wy, 0.18f, 1.04f, 1f, 0f, true, false, _shadowCol);
        EmitQuad(SlotGradH, -6, wx + 0.3025f, wy, 0.18f, 1.04f, 1f, 0f, false, false, _shadowCol);
        if (!bottomCap) return;
        // 真下が床: 終端を横壁と同じ面で塗る。AddWallVBottomCap の上端の帯は同じ order の面の下に描かれて
        // 見えていない (実機確認) ので、見た目を揃えるため描かない
        EmitRect(SlotWall, -3, wx, wy, 0.4f, 1f, _whiteCol);
        EmitContactShadow(wx, wy, 0.5f);
    }

    private static readonly CellKind[] _chunkCells = new CellKind[ChunkSize * (ChunkSize + 1)];

    // チャンク (cx, cy) を 1 枚のメッシュ群 + 当たり判定に組む。既にあれば作り直す。
    private static int BuildChunkMesh(int cx, int cy, uint seed)
    {
        long key = PackChunkKey(cx, cy);
        DestroyChunkRender(key, removeAabbs: true);

        int baseX = cx * ChunkSize;
        int baseY = cy * ChunkSize;

        // セル種別を 1 回だけ引く (行 0 は南隣チャンクの最上段 = 継ぎ目判定用)
        for (int lx = 0; lx < ChunkSize; lx++)
        for (int ly = -1; ly < ChunkSize; ly++)
            _chunkCells[(ly + 1) * ChunkSize + lx] = ClassifyCell(baseX + lx, baseY + ly, seed);

        Sprite floorSp = FloorPngSprite;
        bool floorOk = PrepareSlot(SlotFloor, floorSp);
        if (!PrepareSlot(SlotWall, WallPngSprite ?? WallSpriteH)) PrepareSlot(SlotWall, BaselineSprite);
        PrepareSlot(SlotBase, BaselineSprite);
        PrepareSlot(SlotGradV, WallShadowGradientSprite);
        PrepareSlot(SlotGradH, WallShadowGradientHSprite);
        PrepareSlot(SlotStain, StainSprite);
        _floorSlot = floorOk ? SlotFloor : SlotBase;
        _floorCol = ToColor32(floorOk ? Color.white : FloorBaseColor);
        _whiteCol = FxMath.Rgba32(255, 255, 255, 255);
        _darkCol = ToColor32(WallDarkColor);
        _edgeCol = ToColor32(WallEdgeHighlight);
        _shadowCol = FxMath.Rgba32(0, 0, 0, 115); // α 0.45
        _stainCol = ToColor32(FxMath.Rgba(0.16f, 0.11f, 0.06f, 0.50f));

        _batchUsed = 0;
        int cells = 0;
        for (int lx = 0; lx < ChunkSize; lx++)
        for (int ly = 0; ly < ChunkSize; ly++)
        {
            int wx = baseX + lx, wy = baseY + ly;
            CellKind cell = _chunkCells[(ly + 1) * ChunkSize + lx];
            CellKind south = _chunkCells[ly * ChunkSize + lx];
            switch (cell)
            {
                case CellKind.Floor:
                    EmitFloorCell(wx, wy, true);
                    break;
                case CellKind.WallH:
                    EmitWallHCell(wx, wy, south == CellKind.WallV);
                    AddWallAabb(wx, wy, 0.5f, key);
                    break;
                case CellKind.WallV:
                    EmitWallVCell(wx, wy, south == CellKind.Floor);
                    AddWallAabb(wx, wy, 0.225f, key);
                    break;
                default:
                    continue;
            }

            cells++;
        }

        GameObject root = new("BackroomsChunk");
        root.transform.SetParent(null, false);
        root.transform.position = Vector3.zero;
        // 途中で例外が出ても次の作り直し・破棄で回収できるよう、先に登録する
        ChunkRender cr = new() { Root = root };
        _chunkRenders[key] = cr;

        for (int i = 0; i < _batchUsed; i++)
        {
            QuadBatch b = _batchPool[i];
            int nq = b.V.Count / 4;
            if (nq == 0) continue;
            int[] tris = new int[nq * 6];
            for (int q = 0; q < nq; q++)
            {
                int v = q * 4, t = q * 6;
                tris[t] = v;
                tris[t + 1] = v + 2;
                tris[t + 2] = v + 1;
                tris[t + 3] = v;
                tris[t + 4] = v + 3;
                tris[t + 5] = v + 2;
            }

            Mesh mesh = new() { name = "BackroomsChunkMesh" };
            mesh.vertices = b.V.ToArray();
            mesh.uv = b.U.ToArray();
            mesh.colors32 = b.C.ToArray();
            mesh.triangles = tris;
            mesh.RecalculateBounds();
            cr.Meshes.Add(mesh);

            GameObject part = new("BackroomsChunkPart");
            part.transform.SetParent(root.transform, false);
            part.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer mr = part.AddComponent<MeshRenderer>();
            mr.sharedMaterial = _chunkSlotMats[b.Slot];
            mr.sortingLayerID = 0;
            mr.sortingOrder = b.Order;
        }

        AddChunkColliders(root, baseX, baseY);
        _occludersDirty = true;
        return cells;
    }

    private static void AddWallAabb(int wx, int wy, float halfX, long key)
    {
        WallAabbs.Add((wx, wy, halfX, 0.5f));
        WallAabbChunkKeys.Add(key);
        WallGhostRenderers.Add(null);
    }

    // 横壁は行ごと・縦壁は列ごとに連続区間を 1 個の箱へ (寸法は SpawnTile の 1×1 / 0.45×1 と同じ)
    private static void AddChunkColliders(GameObject root, int baseX, int baseY)
    {
        for (int ly = 0; ly < ChunkSize; ly++)
        {
            int lx = 0;
            while (lx < ChunkSize)
            {
                if (_chunkCells[(ly + 1) * ChunkSize + lx] != CellKind.WallH) { lx++; continue; }
                int start = lx;
                while (lx < ChunkSize && _chunkCells[(ly + 1) * ChunkSize + lx] == CellKind.WallH) lx++;
                int n = lx - start;
                BoxCollider2D box = root.AddComponent<BoxCollider2D>();
                box.size = FxMath.V2(n, 1f);
                box.offset = FxMath.V2(baseX + start + (n - 1) * 0.5f, baseY + ly);
            }
        }

        for (int lx = 0; lx < ChunkSize; lx++)
        {
            int ly = 0;
            while (ly < ChunkSize)
            {
                if (_chunkCells[(ly + 1) * ChunkSize + lx] != CellKind.WallV) { ly++; continue; }
                int start = ly;
                while (ly < ChunkSize && _chunkCells[(ly + 1) * ChunkSize + lx] == CellKind.WallV) ly++;
                int n = ly - start;
                BoxCollider2D box = root.AddComponent<BoxCollider2D>();
                box.size = FxMath.V2(0.45f, n);
                box.offset = FxMath.V2(baseX + lx, baseY + start + (n - 1) * 0.5f);
            }
        }
    }

    // removeAabbs: 作り直しの時だけ true (UnloadChunk は WallAabbs を自分で消す)
    private static void DestroyChunkRender(long key, bool removeAabbs)
    {
        if (_chunkRenders.TryGetValue(key, out ChunkRender cr))
        {
            _chunkRenders.Remove(key);
            DisposeChunkRender(cr);
        }

        if (!removeAabbs) return;
        for (int i = WallAabbs.Count - 1; i >= 0; i--)
        {
            if (WallAabbChunkKeys[i] != key) continue;
            WallAabbs.RemoveAt(i);
            WallAabbChunkKeys.RemoveAt(i);
            WallGhostRenderers.RemoveAt(i);
        }

        _occludersDirty = true;
    }

    // Destroy(GameObject) は Mesh を消さないので明示的に破棄する。シーン破棄後 (根が fake-null) でも Mesh は残っている
    private static void DisposeChunkRender(ChunkRender cr)
    {
        for (int i = 0; i < cr.Meshes.Count; i++)
            if (cr.Meshes[i] != null) Object.Destroy(cr.Meshes[i]);
        cr.Meshes.Clear();
        if (cr.Root != null) Object.Destroy(cr.Root);
        cr.Root = null;
    }

    private static void DestroyAllChunkRenders()
    {
        foreach (ChunkRender cr in _chunkRenders.Values) DisposeChunkRender(cr);
        _chunkRenders.Clear();
    }

    // 入室時の近傍判定用: 点からチャンクの矩形までの距離²
    private static float ChunkDistSqr(long key, float px, float py)
    {
        UnpackChunkKey(key, out int cx, out int cy);
        float x0 = cx * ChunkSize - 0.5f, x1 = x0 + ChunkSize;
        float y0 = cy * ChunkSize - 0.5f, y1 = y0 + ChunkSize;
        float dx = px < x0 ? x0 - px : px > x1 ? px - x1 : 0f;
        float dy = py < y0 ? y0 - py : py > y1 ? py - y1 : 0f;
        return dx * dx + dy * dy;
    }

    // 影モードの切替・帯の表示切替など、組み方が変わる設定を変えた後に呼ぶ
    private static void RegenerateForRenderModeChange(byte pid)
    {
        if (!_inBackrooms || EkmapLoader.ActiveSource != null) return;
        if (_chunkMeshMode == WantChunkMesh && !_chunkMeshMode) return;
        GenerateLobby(_lastSeed, pid, silent: true);
    }
}
