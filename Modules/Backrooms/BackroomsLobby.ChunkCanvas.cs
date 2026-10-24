using System;
using UnityEngine;

namespace EndKnot.Modules;

// procgen Backrooms をシェーダで描く。読み込み済みのセルを 1 枚の「セル表」テクスチャ (1 セル 1 バイト・
// CanvasRing セル四方で巻き戻る) に書き、それを覆う四角形 1 枚を EndKnot/BackroomsChunk で塗る。
// 床・壁・影・シミの重ね方はシェーダが近傍のセルを見て画素ごとに決めるので、チャンクの境目でも隣の
// チャンクのセルがそのまま見え、セルを 1 バイト書き換えれば絵も変わる。
// 当たり判定と視界・影の WallAabbs はメッシュ方式と同じ物を BuildChunkMesh が作る (チャンク 1 つに GO 1 個)。
// バンドルにシェーダが無い時はメッシュ方式で描く。
public static partial class BackroomsLobby
{
    // 読み込み窓は最大 (6+1)*2+1 = 15 チャンク = 240 セル。巻き戻りで別の場所と重ならない大きさにする
    private const int CanvasRing = 512;

    // GenerateLobby で _chunkMeshMode と一緒に決める
    private static bool _chunkShaderMode;
    // Android は全画面の画素処理の重さを測るまでメッシュ方式 (/bbshadow chunk shader で切り替えられる)
    private static bool _preferChunkShader = Application.platform != RuntimePlatform.Android;

    private static byte[] _canvasCells;
    private static Texture2D _canvasTex;
    private static Material _canvasMat;
    private static Mesh _canvasMesh;
    private static GameObject _canvasGO;
    private static bool _canvasDirty;
    private static Texture2D _canvasLookFloor, _canvasLookWall;
    private static bool _canvasLookBand;
    private static int _canvasCx = int.MinValue, _canvasCy = int.MinValue;

    private static bool CanUseChunkShader => _preferChunkShader && FxShaderBundle.BackroomsChunk != null;

    private static void EnsureCanvas()
    {
        _canvasCells ??= new byte[CanvasRing * CanvasRing];

        if (_canvasTex == null)
        {
            _canvasTex = new Texture2D(CanvasRing, CanvasRing, TextureFormat.R8, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Repeat,
                name = "BackroomsCanvasCells",
                hideFlags = HideFlags.HideAndDontSave
            };
            Array.Clear(_canvasCells, 0, _canvasCells.Length);
            _canvasDirty = true;
        }

        if (_canvasMat == null)
        {
            _canvasMat = new Material(FxShaderBundle.BackroomsChunk) { name = "BackroomsCanvas", hideFlags = HideFlags.HideAndDontSave };
            _canvasMat.SetTexture("_CellTex", _canvasTex);
            _canvasMat.SetFloat("_Ring", CanvasRing);
        }

        if (_canvasMesh == null)
        {
            const float h = CanvasRing * 0.5f;
            _canvasMesh = new Mesh { name = "BackroomsCanvasQuad", hideFlags = HideFlags.HideAndDontSave };
            _canvasMesh.vertices = new[] { FxMath.V3(-h, -h, 0f), FxMath.V3(h, -h, 0f), FxMath.V3(h, h, 0f), FxMath.V3(-h, h, 0f) };
            _canvasMesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            _canvasMesh.RecalculateBounds();
        }

        if (_canvasGO == null)
        {
            _canvasGO = new GameObject("BackroomsCanvas");
            _canvasGO.AddComponent<MeshFilter>().sharedMesh = _canvasMesh;
            MeshRenderer mr = _canvasGO.AddComponent<MeshRenderer>();
            mr.sharedMaterial = _canvasMat;
            mr.sortingLayerID = 0;
            mr.sortingOrder = -5;
            _canvasCx = int.MinValue;
        }
    }

    // チャンクを組むたびに呼ぶが、渡した床・壁のテクスチャと帯の有無が変わった時だけ書く
    // (床・壁の絵は入室前に非同期で読まれるので、後から差し替わることがある)
    private static void ApplyCanvasLook()
    {
        Texture2D floorTex = _chunkSlotTex[_floorSlot], wallTex = _chunkSlotTex[SlotWall];
        bool band = BackroomsConfig.ShowWallHTopBand;
        if (floorTex == _canvasLookFloor && wallTex == _canvasLookWall && band == _canvasLookBand && _canvasLookFloor != null) return;
        _canvasLookFloor = floorTex;
        _canvasLookWall = wallTex;
        _canvasLookBand = band;
        ref SlotSprite fl = ref _slotSprites[_floorSlot];
        ref SlotSprite wl = ref _slotSprites[SlotWall];
        _canvasMat.SetTexture("_FloorTex", _chunkSlotTex[_floorSlot]);
        _canvasMat.SetVector("_FloorB", new Vector4(fl.MinX, fl.MinY, fl.MaxX, fl.MaxY));
        _canvasMat.SetVector("_FloorUV", new Vector4(fl.U0, fl.V0, fl.U1, fl.V1));
        _canvasMat.SetTexture("_WallTex", _chunkSlotTex[SlotWall]);
        _canvasMat.SetVector("_WallB", new Vector4(wl.MinX, wl.MinY, wl.MaxX, wl.MaxY));
        _canvasMat.SetVector("_WallUV", new Vector4(wl.U0, wl.V0, wl.U1, wl.V1));
        _canvasMat.SetColor("_FloorCol", _floorSlot == SlotFloor ? Color.white : FloorBaseColor);
        _canvasMat.SetColor("_DarkCol", WallDarkColor);
        _canvasMat.SetColor("_EdgeCol", WallEdgeHighlight);
        _canvasMat.SetColor("_StainCol", FxMath.Rgba(0.16f, 0.11f, 0.06f, 0.50f));
        _canvasMat.SetFloat("_ShadowA", 0.45f);
        _canvasMat.SetFloat("_TopBand", BackroomsConfig.ShowWallHTopBand ? 1f : 0f);
    }

    // _chunkCells (BuildChunkMesh が引いた 16×16) をセル表へ書く。0 = 未読み込み
    private static void WriteCanvasChunk(int baseX, int baseY)
    {
        EnsureCanvas();
        for (int ly = 0; ly < ChunkSize; ly++)
        {
            int row = ((baseY + ly) & (CanvasRing - 1)) * CanvasRing;
            for (int lx = 0; lx < ChunkSize; lx++)
            {
                CellKind k = _chunkCells[(ly + 1) * ChunkSize + lx];
                _canvasCells[row + ((baseX + lx) & (CanvasRing - 1))] = k == CellKind.Void ? (byte)0 : (byte)((int)k + 1);
            }
        }

        _canvasDirty = true;
    }

    private static void ClearCanvasChunk(long key)
    {
        if (_canvasCells == null) return;
        UnpackChunkKey(key, out int cx, out int cy);
        // 巻き戻りで同じ場所に当たるチャンクが別に組まれていれば、そのセルなので消さない
        // (CanvasRing を超える瞬間移動で、新しいチャンクが古いチャンクより先に組まれた時)
        const int ringChunks = CanvasRing / ChunkSize;
        foreach (long other in _chunkRenders.Keys)
        {
            UnpackChunkKey(other, out int ox, out int oy);
            if (((ox - cx) & (ringChunks - 1)) == 0 && ((oy - cy) & (ringChunks - 1)) == 0) return;
        }

        int baseX = cx * ChunkSize, baseY = cy * ChunkSize;
        for (int ly = 0; ly < ChunkSize; ly++)
        {
            int row = ((baseY + ly) & (CanvasRing - 1)) * CanvasRing;
            for (int lx = 0; lx < ChunkSize; lx++)
                _canvasCells[row + ((baseX + lx) & (CanvasRing - 1))] = 0;
        }

        _canvasDirty = true;
    }

    private static void ResetCanvas()
    {
        if (_canvasCells != null) Array.Clear(_canvasCells, 0, _canvasCells.Length);
        _canvasDirty = true;
        _canvasLookFloor = null;
        if (_canvasGO != null) UnityEngine.Object.Destroy(_canvasGO);
        _canvasGO = null;
    }

    // 1 フレームに 1 回: 書いたセルをまとめて送り、四角形をプレイヤーのいるチャンクへ寄せる
    private static unsafe void FlushCanvas()
    {
        if (!_chunkShaderMode || _canvasTex == null) return;

        if (_canvasDirty)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                fixed (byte* p = _canvasCells) _canvasTex.LoadRawTextureData((IntPtr)p, _canvasCells.Length);
                _canvasTex.Apply(false, false);
                _canvasDirty = false;
            }
            catch (Exception e)
            {
                // 送れないとセル表が空のまま (当たり判定だけある透明な部屋) になるので、メッシュ方式で組み直す
                Logger.Error($"Backrooms canvas upload failed, falling back to mesh: {e.Message}", "BackroomsGen");
                _preferChunkShader = false;
                if (PlayerControl.LocalPlayer != null) GenerateLobby(_lastSeed, PlayerControl.LocalPlayer.PlayerId, silent: true);
                return;
            }

            _canvasUploadTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            _canvasUploadCount++;
        }

        if (_canvasGO == null || PlayerControl.LocalPlayer == null) return;
        Vector2 feet = LocalPlayerFeet();
        int cx = Mathf.FloorToInt(feet.x / ChunkSize), cy = Mathf.FloorToInt(feet.y / ChunkSize);
        if (cx == _canvasCx && cy == _canvasCy) return;
        _canvasCx = cx;
        _canvasCy = cy;
        _canvasGO.transform.position = FxMath.V3(cx * ChunkSize + ChunkSize * 0.5f, cy * ChunkSize + ChunkSize * 0.5f, 0f);
    }
}
