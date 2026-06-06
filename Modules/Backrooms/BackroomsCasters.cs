using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace EndKnot.Modules;

// ============================================================================
// Backrooms 壁のシャドウキャスター生成 (Phase 2c: 二重壁 far-face gating)
//
// skeleton (壁中心線) 方式は左右対称・角連結・綺麗だが、caster が壁の中心に居るため
// 影境界が壁の真ん中→プレイヤー側の半分しか lit しない (ズレが壁厚に比例して見える)。
// これを直すため本家 AU の「二重壁」を再現する:
//   ・壁を水平 / 垂直の run にまとめ、各 run の **両面** (上下 or 左右) に caster を置く。
//   ・毎フレ、プレイヤーが run のどちら側に居るかで「**遠い面**」だけ active にする。
//     → 壁は 光(プレイヤー) と 遠面 caster の間に入る = 壁全体が lit、遠面より奥が暗い。
//     → プレイヤーが反対側へ回ると active 面が入れ替わる = 両側から見て常にフル lit (本家二重壁)。
//   ・角: 角セルは H run と V run の両方に属し、各 run の面は cell 端 (±0.5) まで伸びるので、
//     角の外頂点 (例 SW なら (ix-0.5, iy-0.5)) で H 面と V 面が一致 → 連結・漏れなし。
//   ・直線壁セル (片方向の隣接のみ) はその向きの run だけに入る → 横方向のヒゲ caster は出ない。
//   ・孤立壁 (4 近傍床) は H/V 両方の 1-cell run 扱い → 遠側 2 面が L 字に出て遮蔽。
//
// per-segment 短 collider の理由: OverlapCircle は radius 圏内しか返さないので GPU の辺処理仕様に非依存。
// 近い面を active にすると壁の near 面が壁自身を影に落とす (= 全部暗い) ので、遠い面 gating が必須。
// ============================================================================
public static class BackroomsCasters
{
    private const string Tag = "BBShadow";

    // 1 本の壁 run。両面 (Lo/Hi) の caster を持ち、プレイヤー位置で遠い面だけ enable する。
    private sealed class RunCaster
    {
        public Collider2D Lo;   // H run=下面(y=gate-0.5) / V run=左面(x=gate-0.5)
        public Collider2D Hi;   // H run=上面(y=gate+0.5) / V run=右面(x=gate+0.5)
        public bool Horizontal; // true=水平 run (gate は row iy)、false=垂直 run (gate は col ix)
        public float Gate;      // run の中心線 (整数)。player < Gate の側に居る時 Hi 面が遠面=active
        public sbyte State;     // 0=未設定 / 1=Hi active / -1=Lo active (変化時のみ interop)
    }

    private static readonly List<GameObject> _casters = [];
    private static readonly List<RunCaster> _runs = [];

    // 占有格子 + run 候補バッファ (再利用で alloc 回避。Rebuild は _occludersDirty 時のみ)。
    private static readonly HashSet<long> _cells = [];
    private static readonly List<(int key, int cell)> _hCells = []; // 水平 run 候補: key=row iy, cell=ix
    private static readonly List<(int key, int cell)> _vCells = []; // 垂直 run 候補: key=col ix, cell=iy

    // WallAabbs (per-cell grid box) から二重壁 far-face caster を作り直す。壁が cull/stream で変わった時だけ呼ぶ。
    public static void Rebuild(List<(float cx, float cy, float halfX, float halfY)> wallCells)
    {
        Clear();
        if (wallCells == null || wallCells.Count == 0) return;

        // 占有格子 (中心を整数セルにスナップ。collider offset=0 なので RoundToInt が exact)
        _cells.Clear();
        foreach (var w in wallCells) _cells.Add(PackCell(Mathf.RoundToInt(w.cx), Mathf.RoundToInt(w.cy)));

        // 各壁セルを「水平壁か / 垂直壁か」で run 候補に振り分ける (角は両方・直線は片方=ヒゲ防止)
        _hCells.Clear();
        _vCells.Clear();
        foreach (var w in wallCells)
        {
            int ix = Mathf.RoundToInt(w.cx), iy = Mathf.RoundToInt(w.cy);
            bool hasH = _cells.Contains(PackCell(ix + 1, iy)) || _cells.Contains(PackCell(ix - 1, iy));
            bool hasV = _cells.Contains(PackCell(ix, iy + 1)) || _cells.Contains(PackCell(ix, iy - 1));
            bool isolated = !hasH && !hasV;
            if (hasH || isolated) _hCells.Add((iy, ix)); // 水平 run へ
            if (hasV || isolated) _vCells.Add((ix, iy)); // 垂直 run へ
        }

        int hRuns = EmitRuns(_hCells, horizontal: true);
        int vRuns = EmitRuns(_vCells, horizontal: false);

        Logger.Info($"WallCasters rebuilt (double-wall far-face): {_casters.Count} GOs / {_runs.Count} runs (h={hRuns} v={vRuns}) from {_cells.Count} cells", Tag);
    }

    // run 候補 (key=固定軸 integer 中心, cell=可変軸セル)。連続 cell をマージして両面 caster を spawn。
    // run は cell [start..end] を覆い、面は cell 端まで (start-0.5 .. end+0.5) = 角で隣 run と頂点一致。
    private static int EmitRuns(List<(int key, int cell)> cells, bool horizontal)
    {
        if (cells.Count == 0) return 0;
        cells.Sort((a, b) => a.key != b.key ? a.key.CompareTo(b.key) : a.cell.CompareTo(b.cell));

        int runs = 0;
        for (int i = 0; i < cells.Count;)
        {
            int key = cells[i].key, start = cells[i].cell, end = start, j = i + 1;
            while (j < cells.Count && cells[j].key == key && cells[j].cell <= end + 1)
            {
                if (cells[j].cell > end) end = cells[j].cell;
                j++;
            }

            float lo = start - 0.5f;  // run の始端 (先頭セルの外辺)
            float hi = end + 0.5f;     // run の終端 (末尾セルの外辺)
            float gate = key;          // 中心線 (player < gate の側で Hi 面が遠面)

            // 両面を spawn (Lo=gate-0.5 面 / Hi=gate+0.5 面)。初期は両方 enabled、毎フレ gating で片方に絞る。
            Collider2D loCol, hiCol;
            if (horizontal)
            {
                loCol = SpawnSegment(new Vector2(lo, key - 0.5f), new Vector2(hi, key - 0.5f)); // 下面
                hiCol = SpawnSegment(new Vector2(lo, key + 0.5f), new Vector2(hi, key + 0.5f)); // 上面
            }
            else
            {
                loCol = SpawnSegment(new Vector2(key - 0.5f, lo), new Vector2(key - 0.5f, hi)); // 左面
                hiCol = SpawnSegment(new Vector2(key + 0.5f, lo), new Vector2(key + 0.5f, hi)); // 右面
            }

            _runs.Add(new RunCaster { Lo = loCol, Hi = hiCol, Horizontal = horizontal, Gate = gate, State = 0 });
            runs++;
            i = j;
        }

        return runs;
    }

    // プレイヤー位置に応じて各 run の「遠い面」だけ active にする (毎フレ RunPerFrameUpdates から)。
    // player が gate より小さい側に居る → 遠い面は Hi 面 (gate+0.5)。逆は Lo 面。
    public static void UpdateGating(float px, float py)
    {
        foreach (RunCaster rc in _runs)
        {
            if (rc.Lo == null || rc.Hi == null) continue;
            bool hiActive = (rc.Horizontal ? py : px) < rc.Gate;
            sbyte want = (sbyte)(hiActive ? 1 : -1);
            if (rc.State == want) continue; // 変化時のみ collider.enabled を叩く (interop 節約)
            rc.State = want;
            rc.Hi.enabled = hiActive;
            rc.Lo.enabled = !hiActive;
        }
    }

    // 1 本の直線セグメントを layer10 の 2 点 EdgeCollider2D として spawn。GO は中点・点は local。
    private static Collider2D SpawnSegment(Vector2 p0, Vector2 p1)
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
        return ec;
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
        _runs.Clear();
        return n;
    }

    public static int Count => _casters.Count;
}
