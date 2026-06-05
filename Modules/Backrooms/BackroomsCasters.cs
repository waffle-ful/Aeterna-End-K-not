using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace EndKnot.Modules;

// ============================================================================
// Backrooms 壁の輪郭線シャドウキャスター生成 (Phase 2b)
//
// 壁セル (BackroomsLobby.WallAabbs を整数セルにスナップした占有格子) の **境界辺** だけを
// layer10 EdgeCollider2D 線分に変換する。バニラ GPU 影はこれを Physics2D で拾って遮蔽メッシュ化。
//
// なぜ Phase 2a の「中心線」でなく「境界辺」か — 中心線方式の 2 つの欠陥を直す:
//   ① 角で影が切れる: H 壁の水平中心線と V 壁の垂直中心線は端点を共有しない → 角に隙間。
//      境界辺は全て半整数グリッド線上にあり端点がグリッド頂点で一致するので、別 collider でも
//      端点共有で影が連続する (AU GPU 影は collider 跨ぎでも共有端点で繋がる)。
//   ② 影が壁の中央から出る: 中心線はセル中央を通る。境界辺はセル境界 (= 壁の面) にあるので
//      影が壁の面から出る。
//
// アルゴリズム (境界辺キャンセル → 共線マージ → per-segment collider):
//   1. WallAabbs 中心を RoundToInt して full-cell 占有格子 HashSet を作る。薄い WallV (0.45 幅) も
//      full-cell 扱い: 0.45 footprint を使うと H/V 幅不一致で角がズレる = 直したい不具合そのもの。
//      full-cell だと薄い壁の手前に半セル弱の暗がりが出るが「壁が手前の床を少し暗くする」自然な見え方。
//   2. 各壁セルの 4 面のうち隣セルが壁でない面だけを境界辺として出す。2 壁セル間の共有面は内部辺
//      として自動キャンセル — これが旧 per-cell 塗り箱の blocky 黒バー (共有内辺の degenerate geometry) を消す。
//   3. 同一直線上で連続する境界辺を最大長 run にマージ (collider 本数を ~20-40 に抑え GPU hits=100 に余裕)。
//   4. 各 run を 2 点 layer10 EdgeCollider2D として出す。
//
// per-segment 短 collider にする理由: OverlapCircle は radius 圏内の collider しか返さないので、
// GPU が collider の全辺を処理するか radius cull するか不明でも近傍辺しか pipeline に乗らない。
// 巨大ループ 1 本だと OverlapCircle が必ず返す → 全辺処理に賭ける形になる。per-segment はその賭けが消える。
// ============================================================================
public static class BackroomsCasters
{
    private const string Tag = "BBShadow";

    private static readonly List<GameObject> _casters = [];

    // 占有格子・境界辺の作業バッファ (再利用で alloc 回避。Rebuild は _occludersDirty 時のみで per-frame ではない)。
    private static readonly HashSet<long> _cells = [];
    private static readonly List<(int line, int cell)> _hEdges = []; // 水平辺: line=2*yWorld (整数化), cell=x column
    private static readonly List<(int line, int cell)> _vEdges = []; // 垂直辺: line=2*xWorld (整数化), cell=y row

    // WallAabbs (per-cell grid box) から境界辺 caster を作り直す。壁が cull/stream で変わった時だけ呼ぶ。
    // 引数は WallAabbs そのもの (cx,cy のみ使用。halfX/halfY は full-cell 占有のため無視)。
    public static void Rebuild(List<(float cx, float cy, float halfX, float halfY)> wallCells)
    {
        Clear();
        if (wallCells == null || wallCells.Count == 0) return;

        // 1. full-cell 占有格子 (中心を整数セルにスナップ。collider offset は 0 なので RoundToInt が exact)
        _cells.Clear();
        foreach (var w in wallCells)
            _cells.Add(PackCell(Mathf.RoundToInt(w.cx), Mathf.RoundToInt(w.cy)));

        // 2. 境界辺抽出 — 隣セルが壁でない面のみ出す (共有内部辺は出さない = 自動キャンセル)
        _hEdges.Clear();
        _vEdges.Clear();
        foreach (var w in wallCells)
        {
            int ix = Mathf.RoundToInt(w.cx), iy = Mathf.RoundToInt(w.cy);
            if (!_cells.Contains(PackCell(ix, iy - 1))) _hEdges.Add((2 * iy - 1, ix)); // 下面 yWorld=iy-0.5
            if (!_cells.Contains(PackCell(ix, iy + 1))) _hEdges.Add((2 * iy + 1, ix)); // 上面 yWorld=iy+0.5
            if (!_cells.Contains(PackCell(ix - 1, iy))) _vEdges.Add((2 * ix - 1, iy)); // 左面 xWorld=ix-0.5
            if (!_cells.Contains(PackCell(ix + 1, iy))) _vEdges.Add((2 * ix + 1, iy)); // 右面 xWorld=ix+0.5
        }

        // 3+4. 共線マージ → per-segment EdgeCollider2D
        int hRuns = EmitRuns(_hEdges, horizontal: true);
        int vRuns = EmitRuns(_vEdges, horizontal: false);

        Logger.Info($"WallCasters rebuilt: {_casters.Count} segs (h={hRuns} v={vRuns}) from {wallCells.Count} cells / {_cells.Count} uniq", Tag);
    }

    // 同一 line 上で cell index が連続する境界辺を最大長 run にマージし、各 run を EdgeCollider2D として出す。
    // horizontal: line=2*yWorld 固定、cell=x column が連続。vertical はその逆 (line=2*xWorld 固定、cell=y row)。
    private static int EmitRuns(List<(int line, int cell)> edges, bool horizontal)
    {
        if (edges.Count == 0) return 0;

        // line 昇順 → 同 line 内 cell 昇順 にソートして連続 run を線形検出
        edges.Sort((a, b) => a.line != b.line ? a.line.CompareTo(b.line) : a.cell.CompareTo(b.cell));

        int runs = 0;
        for (int i = 0; i < edges.Count;)
        {
            int line = edges[i].line;
            int start = edges[i].cell;
            int end = start;
            int j = i + 1;
            // 同 line かつ cell が連続(+1) または 重複(<=end) の間だけ伸ばす。+1 連続でドア=gap は途切れ
            // (光が漏れる)、重複吸収で WallAabbs に同セルが万一二重登録されても同一辺の二重 spawn
            // (= この書換が消そうとしている degenerate double-edge) を防ぐ。
            while (j < edges.Count && edges[j].line == line && edges[j].cell <= end + 1)
            {
                if (edges[j].cell > end) end = edges[j].cell;
                j++;
            }

            float lineWorld = line * 0.5f; // 固定軸の world 座標 (2*world を整数化したので 0.5 倍で戻す)
            float lo = start - 0.5f;        // run 始端 (先頭セル中心 - 0.5)
            float hi = end + 0.5f;          // run 終端 (末尾セル中心 + 0.5)
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
