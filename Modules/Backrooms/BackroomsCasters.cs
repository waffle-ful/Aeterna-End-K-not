using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace EndKnot.Modules;

// ============================================================================
// Backrooms 壁のシャドウキャスター生成 (Phase 2b: 壁中心線スケルトン)
//
// inset で壁を囲む方式は ①角でグリッド頂点共有が外れ隙間 ②床両側壁で上下2本のズレ線 になり、
// caster が「綺麗な壁の線」でなくなって影が歪んだ。これを直すため caster を壁の **中心線** にする:
//   ・隣接する壁セルの中心どうしを結ぶ辺 (medial axis = skeleton) を出す。
//   ・水平に連続する壁 → 中心を通る 1 本の水平線。垂直も同様。
//   ・角/十字: 角セルが上隣の壁へ縦辺を出すので H 中心線と V 中心線が角セル中心で必ず交わる → 連続。
//   ・run の端は壁の実端 (セル端 ±0.5) まで延長 → 壁の端でも遮蔽が途切れない。
//   ・孤立壁セル (4 近傍全て床) は中心線が作れないので full-cell box で囲む。
//
// 中心線なので影は左右対称・十字も塞ぐ・連続で綺麗。壁は「プレイヤー側の半分が lit」(中心から影)。
// 壁を両側フル lit にするには別途 壁スプライトの二重面化が要る (本家の二重壁方式)。
//
// per-segment 短 collider の理由: OverlapCircle は radius 圏内しか返さないので GPU の辺処理仕様に非依存。
// ============================================================================
public static class BackroomsCasters
{
    private const string Tag = "BBShadow";

    private static readonly List<GameObject> _casters = [];

    // 占有格子 + skeleton 辺バッファ (再利用で alloc 回避。Rebuild は _occludersDirty 時のみ)。
    private static readonly HashSet<long> _cells = [];
    private static readonly List<(int key, int cell)> _hEdges = []; // 水平 skeleton 辺: key=y row, cell=x (辺は中心 ix→ix+1)
    private static readonly List<(int key, int cell)> _vEdges = []; // 垂直 skeleton 辺: key=x col, cell=y (辺は中心 iy→iy+1)

    // WallAabbs (per-cell grid box) から壁中心線 skeleton caster を作り直す。壁が cull/stream で変わった時だけ呼ぶ。
    public static void Rebuild(List<(float cx, float cy, float halfX, float halfY)> wallCells)
    {
        Clear();
        if (wallCells == null || wallCells.Count == 0) return;

        // 占有格子 (中心を整数セルにスナップ。collider offset=0 なので RoundToInt が exact)
        _cells.Clear();
        foreach (var w in wallCells) _cells.Add(PackCell(Mathf.RoundToInt(w.cx), Mathf.RoundToInt(w.cy)));

        // 隣接壁セルの中心どうしを結ぶ skeleton 辺を出す (右隣・上隣だけ出して二重計上を防ぐ)
        _hEdges.Clear();
        _vEdges.Clear();
        int isolated = 0;
        foreach (var w in wallCells)
        {
            int ix = Mathf.RoundToInt(w.cx), iy = Mathf.RoundToInt(w.cy);
            bool right = _cells.Contains(PackCell(ix + 1, iy));
            bool up = _cells.Contains(PackCell(ix, iy + 1));
            if (right) _hEdges.Add((iy, ix)); // 中心 (ix,iy)→(ix+1,iy)
            if (up) _vEdges.Add((ix, iy));     // 中心 (ix,iy)→(ix,iy+1)

            // 孤立壁 (4 近傍全て床) は skeleton 辺が無いので full-cell box で囲う
            if (!right && !up && !_cells.Contains(PackCell(ix - 1, iy)) && !_cells.Contains(PackCell(ix, iy - 1)))
            {
                SpawnBox(ix, iy);
                isolated++;
            }
        }

        int hRuns = EmitSkeletonRuns(_hEdges, horizontal: true);
        int vRuns = EmitSkeletonRuns(_vEdges, horizontal: false);

        Logger.Info($"WallCasters rebuilt (skeleton): {_casters.Count} segs (hRun={hRuns} vRun={vRuns} iso={isolated}) from {_cells.Count} cells", Tag);
    }

    // skeleton 辺 (key=固定軸 integer 中心線, cell=可変軸の辺始点)。辺は cell→cell+1 (中心間)。
    // 連続する cell をマージ → run は中心 [start..end+1] を覆う。端を ±0.5 延長して壁の実端まで届かせる。
    private static int EmitSkeletonRuns(List<(int key, int cell)> edges, bool horizontal)
    {
        if (edges.Count == 0) return 0;
        edges.Sort((a, b) => a.key != b.key ? a.key.CompareTo(b.key) : a.cell.CompareTo(b.cell));

        int runs = 0;
        for (int i = 0; i < edges.Count;)
        {
            int key = edges[i].key, start = edges[i].cell, end = start, j = i + 1;
            while (j < edges.Count && edges[j].key == key && edges[j].cell <= end + 1)
            {
                if (edges[j].cell > end) end = edges[j].cell;
                j++;
            }

            // run は中心 start..end+1 を覆う。端を 0.5 延長 → 壁セルの外端 (start-0.5 .. end+1.5)
            float lineWorld = key;            // 中心線 (integer 軸)
            float lo = start - 0.5f;          // 始端 (先頭セルの外辺)
            float hi = end + 1.5f;            // 終端 (末尾セル end+1 の外辺)
            if (horizontal) SpawnSegment(new Vector2(lo, lineWorld), new Vector2(hi, lineWorld));
            else SpawnSegment(new Vector2(lineWorld, lo), new Vector2(lineWorld, hi));

            runs++;
            i = j;
        }

        return runs;
    }

    // 1 本の直線セグメントを layer10 の 2 点 EdgeCollider2D として spawn。GO は中点・点は local。
    private static void SpawnSegment(Vector2 p0, Vector2 p1)
    {
        Vector2 mid = (p0 + p1) * 0.5f;
        GameObject go = new("BBWallCaster");
        go.transform.position = new Vector3(mid.x, mid.y, 0f);
        go.layer = BackroomsConfig.ShadowCasterLayer;
        EdgeCollider2D ec = go.AddComponent<EdgeCollider2D>();
        Il2CppStructArray<Vector2> arr = new(2)
        {
            [0] = p0 - mid,
            [1] = p1 - mid
        };
        ec.points = arr;
        _casters.Add(go);
    }

    // 孤立壁セルを囲う full-cell 閉ループ box (4 辺)。
    private static void SpawnBox(int ix, int iy)
    {
        GameObject go = new("BBWallCasterBox");
        go.transform.position = new Vector3(ix, iy, 0f);
        go.layer = BackroomsConfig.ShadowCasterLayer;
        EdgeCollider2D ec = go.AddComponent<EdgeCollider2D>();
        Il2CppStructArray<Vector2> arr = new(5)
        {
            [0] = new Vector2(-0.5f, -0.5f),
            [1] = new Vector2(0.5f, -0.5f),
            [2] = new Vector2(0.5f, 0.5f),
            [3] = new Vector2(-0.5f, 0.5f),
            [4] = new Vector2(-0.5f, -0.5f)
        };
        ec.points = arr;
        _casters.Add(go);
    }

    // (x,y) 整数セルを long に詰める。x を上位 32bit、y を下位 32bit。
    private static long PackCell(int x, int y) => ((long)x << 32) ^ (uint)y;

    public static int Clear()
    {
        int n = 0;
        foreach (GameObject go in _casters)
        {
            if (go == null) continue;
            try { Object.Destroy(go); n++; } catch { /* 既に破棄 — 無視 */ }
        }

        _casters.Clear();
        return n;
    }

    public static int Count => _casters.Count;
}
