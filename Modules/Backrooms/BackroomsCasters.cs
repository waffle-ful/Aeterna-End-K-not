using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace EndKnot.Modules;

// ============================================================================
// Backrooms 壁の輪郭線シャドウキャスター生成 (Phase 2b: 厚い壁マップ)
//
// 厚い壁 (>=3 セル) の **interior (壁の芯)** の境界辺を layer10 EdgeCollider2D 線分に変換する。
// バニラ GPU 影はこれを Physics2D で拾って遮蔽メッシュ化。
//
// なぜ「壁全体の境界」でなく「interior の境界」か — 「壁の面が見える + 奥が暗い」を出すため:
//   ・壁全体の境界 (= floor に接する面) を caster にすると near face で遮蔽 → 壁丸ごと影 (壁が真っ黒)。
//   ・interior = 全 4 近傍が壁のセル (= 床に接しない壁の芯)。その境界を caster にすると、
//     外側 1 セル (ring = 床に接する壁) が lit (見える壁の面)、interior 芯が occluder (奥が暗い) になる。
//   ・両側の部屋がそれぞれ自分側の ring を見て、中心 occluder で奥が暗くなる = バニラ AU / LevelImposter 同型。
//   ・1 セル壁では interior が無く caster が消える → 厚い壁 (ClassifyCellOutline) が前提。
//
// なぜ「中心線」でなく「境界辺」か (Phase 2a の中心線の欠陥を直す):
//   ① 角で影が切れる: 境界辺は全て半整数グリッド線上で端点がグリッド頂点一致 → 別 collider でも端点共有で連続。
//   ② 影が壁の中央から出る: 境界辺はセル境界 (= 壁の面) にあるので影が面から出る。
//
// アルゴリズム (interior 抽出 → 境界辺キャンセル → 共線マージ → per-segment collider):
//   1. WallAabbs 中心を RoundToInt して full-cell 壁占有格子 _cells を作る。
//   2. _interior = 全 4 近傍が _cells のセル (床に接しない芯)。
//   3. 各 interior セルの 4 面のうち隣が interior でない面だけを境界辺に (interior 同士の共有面は自動キャンセル)。
//   4. 同一直線で連続する辺を最大長 run にマージ → 各 run を 2 点 layer10 EdgeCollider2D に。
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
    private static readonly HashSet<long> _cells = [];    // 全壁セル
    private static readonly HashSet<long> _interior = []; // 全 4 近傍が壁のセル (壁の芯 = 影 caster の母集団)
    private static readonly List<(int line, int cell)> _hEdges = []; // 水平辺: line=2*yWorld (整数化), cell=x column
    private static readonly List<(int line, int cell)> _vEdges = []; // 垂直辺: line=2*xWorld (整数化), cell=y row

    // WallAabbs (per-cell grid box) から interior 境界辺 caster を作り直す。壁が cull/stream で変わった時だけ呼ぶ。
    // 引数は WallAabbs そのもの (cx,cy のみ使用。halfX/halfY は full-cell 占有のため無視)。
    public static void Rebuild(List<(float cx, float cy, float halfX, float halfY)> wallCells)
    {
        Clear();
        if (wallCells == null || wallCells.Count == 0) return;

        // 1. full-cell 壁占有格子 (中心を整数セルにスナップ。collider offset は 0 なので RoundToInt が exact)
        _cells.Clear();
        foreach (var w in wallCells)
            _cells.Add(PackCell(Mathf.RoundToInt(w.cx), Mathf.RoundToInt(w.cy)));

        // 2. interior = 全 4 近傍が壁のセル (床に接しない壁の芯)。外側 1 セル (ring) は床に接するので除外され lit になる。
        _interior.Clear();
        foreach (var w in wallCells)
        {
            int ix = Mathf.RoundToInt(w.cx), iy = Mathf.RoundToInt(w.cy);
            if (_cells.Contains(PackCell(ix - 1, iy)) && _cells.Contains(PackCell(ix + 1, iy)) &&
                _cells.Contains(PackCell(ix, iy - 1)) && _cells.Contains(PackCell(ix, iy + 1)))
                _interior.Add(PackCell(ix, iy));
        }

        // 3. interior の境界辺抽出 — 隣が interior でない面のみ出す (interior 同士の共有内部辺は自動キャンセル)
        _hEdges.Clear();
        _vEdges.Clear();
        foreach (long key in _interior)
        {
            int ix = (int)(key >> 32);
            int iy = (int)(uint)key; // 下位 32bit を符号付き int に戻す
            if (!_interior.Contains(PackCell(ix, iy - 1))) _hEdges.Add((2 * iy - 1, ix)); // 下面 yWorld=iy-0.5
            if (!_interior.Contains(PackCell(ix, iy + 1))) _hEdges.Add((2 * iy + 1, ix)); // 上面 yWorld=iy+0.5
            if (!_interior.Contains(PackCell(ix - 1, iy))) _vEdges.Add((2 * ix - 1, iy)); // 左面 xWorld=ix-0.5
            if (!_interior.Contains(PackCell(ix + 1, iy))) _vEdges.Add((2 * ix + 1, iy)); // 右面 xWorld=ix+0.5
        }

        // 4. 共線マージ → per-segment EdgeCollider2D
        int hRuns = EmitRuns(_hEdges, horizontal: true);
        int vRuns = EmitRuns(_vEdges, horizontal: false);

        Logger.Info($"WallCasters rebuilt: {_casters.Count} segs (h={hRuns} v={vRuns}) interior={_interior.Count}/{_cells.Count} cells", Tag);
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
