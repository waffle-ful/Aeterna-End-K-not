using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace EndKnot.Modules;

// ============================================================================
// Backrooms 壁のシャドウキャスター生成 (Phase 2b: back-edge 方式)
//
// 「オブジェクトの作りの違い」= バニラは影 caster を壁の見える**面の「裏」**に置く。光が面に届いて
// 面が照らされ、裏の caster がそこから奥を遮蔽する。我々が試した「前面/全周」caster は壁タイル全体を
// 影の umbra に落とす (= 壁が真っ黒)。直し = caster を壁の**裏面 1 辺**だけにする。
//   ・WallH (壁紙の面が下向き・上に dark band) → 裏面 = **上辺** (y=cy+0.5)。下の部屋から面が見え奥が暗い。
//   ・WallV (0.45 幅・面が右向き) → 裏面 = **左辺** (x=cx-0.5)。右の部屋から面が見え奥が暗い。
//   ・H/V は AABB の縦横比で判別 (WallH=正方 halfX>=halfY / WallV=0.45 幅 halfX<halfY)。
//   ・裏面は全て半整数グリッド線上で端点がグリッド頂点一致 → 別 collider でも端点共有で角が連結。
//   ・同一直線で連続する裏面を最大長 run にマージ → per-segment layer10 EdgeCollider2D。
//
// ※これは「単面」: 壁は面側から見ると照らされ、裏側から見ると暗い (部屋は 2 面 lit + 2 面 dark)。
//   バニラは部屋ごとに壁=2 枚背中合わせ (全周 lit) なので完全一致ではない。まず「面の裏に caster=面が光る」
//   機構を最小コストで画面検証する版。OK なら単面 vs 二重壁 (バニラ正確) をユーザーが選ぶ。
//
// per-segment 短 collider の理由: OverlapCircle は radius 圏内しか返さないので GPU の辺処理仕様に依存しない。
// ============================================================================
public static class BackroomsCasters
{
    private const string Tag = "BBShadow";

    private static readonly List<GameObject> _casters = [];

    // 裏面エッジの作業バッファ (再利用で alloc 回避。Rebuild は _occludersDirty 時のみで per-frame ではない)。
    private static readonly List<(int line, int cell)> _hEdges = []; // 水平辺: line=2*yWorld (整数化), cell=x column
    private static readonly List<(int line, int cell)> _vEdges = []; // 垂直辺: line=2*xWorld (整数化), cell=y row

    // WallAabbs (per-cell grid box) から壁の裏面 1 辺ずつ caster を作り直す。壁が cull/stream で変わった時だけ呼ぶ。
    public static void Rebuild(List<(float cx, float cy, float halfX, float halfY)> wallCells)
    {
        Clear();
        if (wallCells == null || wallCells.Count == 0) return;

        // 各壁セルの裏面 1 辺だけを出す。H/V は AABB 縦横比で判別。
        _hEdges.Clear();
        _vEdges.Clear();
        int hCells = 0, vCells = 0;
        foreach (var w in wallCells)
        {
            int ix = Mathf.RoundToInt(w.cx), iy = Mathf.RoundToInt(w.cy);
            if (w.halfX < w.halfY) // WallV (0.45 幅) → 裏面 = 左辺 x=ix-0.5
            {
                _vEdges.Add((2 * ix - 1, iy));
                vCells++;
            }
            else // WallH (正方) → 裏面 = 上辺 y=iy+0.5
            {
                _hEdges.Add((2 * iy + 1, ix));
                hCells++;
            }
        }

        // 共線マージ → per-segment EdgeCollider2D
        int hRuns = EmitRuns(_hEdges, horizontal: true);
        int vRuns = EmitRuns(_vEdges, horizontal: false);

        Logger.Info($"WallCasters rebuilt (back-edge): {_casters.Count} segs (hRun={hRuns} vRun={vRuns}) from H={hCells} V={vCells} cells", Tag);
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
