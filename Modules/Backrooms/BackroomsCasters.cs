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

    // 面位置の微調整スケール (/bbshadow inset <V> [H])。V=垂直 run(縦壁の左右面)・H=水平 run(横壁の上下面) を個別に。
    // 1.0=可視壁面(min 半幅)ぴったり、>1=影を壁から離す、<1=壁中心へ寄せる。
    // 既定 V=0.6 / H=0.8 はユーザー実機較正値 (2026-06-09・影が壁に密着してちょうど良いと確認)。
    public static float FaceScaleV = 0.6f;
    public static float FaceScaleH = 0.8f;

    // caster 生成範囲 (player 中心・チェビシェフ距離)。影に光学的に使われるのは光半径 5u 内の caster だけ
    // なのに全壁分の GO を作ると /bbrange 5 (約3万タイル) で ~6000 GO の破棄+再生成が rebuild ごとに走り
    // FPS が崩壊する。32u = 光半径 5u + チャンク跨ぎ rebuild 間隔 16u に十分な余裕。
    private const float CasterRadius = 32f;

    // 占有格子 + run 候補バッファ (再利用で alloc 回避。Rebuild は _occludersDirty 時のみ)。
    // half = run に垂直方向の可視半幅 (H run→halfY, V run→halfX)。面を cell 端でなく可視壁面に置くため。
    private static readonly HashSet<long> _cells = [];
    private static readonly List<(int key, int cell, float half)> _hCells = []; // 水平 run 候補: key=row iy, cell=ix, half=halfY
    private static readonly List<(int key, int cell, float half)> _vCells = []; // 垂直 run 候補: key=col ix, cell=iy, half=halfX

    // WallAabbs (per-cell grid box) から二重壁 far-face caster を作り直す。壁が cull/stream で変わった時だけ呼ぶ。
    public static void Rebuild(List<(float cx, float cy, float halfX, float halfY)> wallCells, Vector2 center)
    {
        Clear();
        if (wallCells == null || wallCells.Count == 0) return;

        // 占有格子 (中心を整数セルにスナップ。collider offset=0 なので RoundToInt が exact)
        _cells.Clear();
        foreach (var w in wallCells)
        {
            if (Mathf.Abs(w.cx - center.x) > CasterRadius || Mathf.Abs(w.cy - center.y) > CasterRadius) continue; // player 周辺だけ caster 化
            _cells.Add(PackCell(Mathf.RoundToInt(w.cx), Mathf.RoundToInt(w.cy)));
        }

        // 各壁セルを「水平壁か / 垂直壁か」で run 候補に振り分ける (角は両方・直線は片方=ヒゲ防止)
        // half = 面位置に使う可視半幅: 水平 run は y 方向 (halfY)、垂直 run は x 方向 (halfX)。
        //   WallH は halfX=halfY=0.5 (cell 全面)、WallV は halfX=0.225 (薄い縦壁)。
        // ★ジレンマと解 (min-half / no-split): 縦 run に角の WallH(0.5) が混じると、
        //   ・半幅一致でマージ条件を割ると run が途切れて角に隙間 → 奥が透ける (旧 93020f88)
        //   ・全部 0.5 にそろえると薄 V 柱の面が cell 端へ離れて見た目と影が分離 + 反対側の床が透ける (今朝の退行)
        //   → EmitRuns で「半幅で run を割らず、run 内の最小可視半幅 (min) に面をそろえる」。
        //     薄 V 柱は 0.225 に密着、角 WallH の far 端 ~0.28u だけ僅かに暗くなる (角なので目立たない)・透けは出ない。
        _hCells.Clear();
        _vCells.Clear();
        foreach (var w in wallCells)
        {
            if (Mathf.Abs(w.cx - center.x) > CasterRadius || Mathf.Abs(w.cy - center.y) > CasterRadius) continue; // player 周辺だけ caster 化
            int ix = Mathf.RoundToInt(w.cx), iy = Mathf.RoundToInt(w.cy);
            bool hasH = _cells.Contains(PackCell(ix + 1, iy)) || _cells.Contains(PackCell(ix - 1, iy));
            bool hasV = _cells.Contains(PackCell(ix, iy + 1)) || _cells.Contains(PackCell(ix, iy - 1));
            bool isolated = !hasH && !hasV;
            if (hasH || isolated) _hCells.Add((iy, ix, w.halfY)); // 水平 run へ (面は y=iy±halfY)
            if (hasV || isolated) _vCells.Add((ix, iy, w.halfX)); // 垂直 run へ (面は x=ix±halfX)
        }

        int hRuns = EmitRuns(_hCells, horizontal: true);
        int vRuns = EmitRuns(_vCells, horizontal: false);

        Logger.Info($"WallCasters rebuilt (double-wall far-face): {_casters.Count} GOs / {_runs.Count} runs (h={hRuns} v={vRuns}) from {_cells.Count} cells", Tag);
    }

    // run 候補 (key=固定軸 integer 中心, cell=可変軸セル, half=垂直可視半幅)。連続 cell をマージして両面 caster を spawn。
    // run-axis は cell 端 (start-0.5 .. end+0.5) = 角で隣 run と頂点一致 (面位置を内へ寄せても run-axis は cell 端のまま)。
    // ★half で run を割らない: 連続セルは隣接だけでマージし、面距離は run 内の最小可視半幅 (min) を使う。
    //   → 薄 V 柱 (0.225) に角 WallH (0.5) が混じっても面は 0.225 に密着 (面ジャンプ=「3 種類目の壁」が消える)。
    //   min は決して lit スリバーを残さない (面がセル可視半幅より内側=far 端を僅かに余計に暗くするだけ)。
    private static int EmitRuns(List<(int key, int cell, float half)> cells, bool horizontal)
    {
        if (cells.Count == 0) return 0;
        cells.Sort((a, b) => a.key != b.key ? a.key.CompareTo(b.key) : a.cell.CompareTo(b.cell));

        int runs = 0;
        for (int i = 0; i < cells.Count;)
        {
            int key = cells[i].key, start = cells[i].cell, end = start, j = i + 1;
            float half = cells[i].half; // run 全体で共有する最小可視半幅 (以下のマージで min を取る)
            while (j < cells.Count && cells[j].key == key && cells[j].cell <= end + 1)
            {
                if (cells[j].cell > end) end = cells[j].cell;
                half = Mathf.Min(half, cells[j].half); // 角 WallH 0.5 が混じっても薄 V 0.225 にそろえる
                j++;
            }

            float lo = start - 0.5f;          // run の始端 (先頭セルの外辺)
            float hi = end + 0.5f;             // run の終端 (末尾セルの外辺)
            float gate = key;                  // 中心線 (player < gate の側で Hi 面が遠面)
            float face = half * (horizontal ? FaceScaleH : FaceScaleV); // 中心からの面距離。H/V 個別スケール

            // 両面を spawn。初期は両方 enabled、毎フレ gating で片方に絞る。
            // ★AU の GPU 影 caster は片面 (winding 依存): edge は light が「front 法線側」にある時だけ影を落とす
            //   (= 法線が光を向く時だけ反対側へ影。実機検証で確定した規約)。
            // ── 水平壁 (WallH): face は南向き。「裏から見ると壁が暗い」(バニラ AU 風・ユーザー選択 2026-06-10)。
            //   遮蔽線を常に壁の北端 (key+face) に置く: 前(南)から見ると壁は線の手前=lit、裏(北)から見ると壁は
            //   線の奥=影に沈む。両面とも北端に置き winding だけ反転して光の向きに応じ遠側へ影を出す:
            //     Hi=表向き(法線 -y / player 南で active→影は北、壁 lit) ・ Lo=裏向き(法線 +y / player 北で active→影は南=壁を覆い暗く)。
            //   → 旧 far-face の「南端に cyan 発射→明るい壁の真下に影が来て壁が浮く」を解消。
            //   WallHDarkFromBehind=false で旧 far-face (Lo=南端=両側 lit) に戻せる (/bbshadow hback で live A/B)。
            // ── 垂直壁 (WallV): 実機で漏れ報告無し・far-face のまま (Lo=左面 / Hi=右面)。V を反転すると壊れる (観察 > 理屈)。
            // vis 色: Lo 面=シアン / Hi 面=マゼンタ (両 emission 線を区別して目視)
            Color loVis = new(0f, 1f, 1f, 0.9f), hiVis = new(1f, 0f, 1f, 0.9f);
            if (horizontal)
            {
                // 裏暗モード=北端 (key+face)、旧 far-face=南端 (key-face)。winding は両方とも反転 (法線 +y) のまま。
                float loY = BackroomsConfig.WallHDarkFromBehind ? key + face : key - face;
                Collider2D loCol = SpawnSegment(new Vector2(hi, loY), new Vector2(lo, loY), loVis);              // 裏向き面 (winding 反転→法線 +y)
                Collider2D hiCol = SpawnSegment(new Vector2(lo, key + face), new Vector2(hi, key + face), hiVis); // 表向き面・北端 (法線 -y)
                _runs.Add(new RunCaster { Lo = loCol, Hi = hiCol, Horizontal = true, Gate = gate, State = 0 });
                runs++;
            }
            else
            {
                // ★V 面は全面セル (WallH 角・junction・柱 = half≥0.45) の lit 帯をまたがない (2026-06-11):
                //   全面セルの WallH 発射線は北端 (r + 0.5*FaceScaleH)。それより南は「明るく見える壁面」なのに、
                //   旧実装は V 面がセル南端 (r-0.5) まで届いていて、横向きの影が lit 面/床を斜めに横切る
                //   黒い楔 =「壁Hなのに横から発射」の正体だった (ユーザー仮説 2026-06-11 的中)。
                //   → 全面セルの寄与区間を [r + 0.5*FaceScaleH, r+0.5] に切り詰め、途切れたら別セグメントに分割。
                //   セグメント下端 (key±face, r+0.5*FaceScaleH) は H 発射線 (y=r+0.5*FaceScaleH, x はセル幅全域) 上に
                //   乗るので角は頂点一致のまま = 透けは出ない。薄 V セルは従来どおり全高 [r-0.5, r+0.5]。
                //   柱 (孤立 WallH) も同式で北端スリバーだけになり、lit 面に楔が落ちない。
                float segLo = float.NaN, segHi = 0f;
                for (int k = i; k < j; k++)
                {
                    float r = cells[k].cell;
                    float cellLo = cells[k].half >= 0.45f ? r + 0.5f * FaceScaleH : r - 0.5f; // 全面セルは H 発射線から北だけ
                    float cellHi = r + 0.5f;
                    if (float.IsNaN(segLo)) { segLo = cellLo; segHi = cellHi; }
                    else if (cellLo <= segHi + 0.001f) { segHi = cellHi; } // 連続 → 伸長
                    else { SpawnVerticalPair(key, face, segLo, segHi, gate, loVis, hiVis); runs++; segLo = cellLo; segHi = cellHi; } // lit 帯ギャップ → 分割
                }

                if (!float.IsNaN(segLo)) { SpawnVerticalPair(key, face, segLo, segHi, gate, loVis, hiVis); runs++; }
            }

            i = j;
        }

        return runs;
    }

    // 垂直 run の 1 セグメント分の両面 caster (左=Lo / 右=Hi) を spawn して gating に登録。
    // lit 帯分割で 1 つの V run が複数セグメントになり得るため、セグメントごとに RunCaster を持つ (gate は run 共通)。
    private static void SpawnVerticalPair(float key, float face, float lo, float hi, float gate, Color loVis, Color hiVis)
    {
        Collider2D loCol = SpawnSegment(new Vector2(key - face, lo), new Vector2(key - face, hi), loVis); // 左面
        Collider2D hiCol = SpawnSegment(new Vector2(key + face, lo), new Vector2(key + face, hi), hiVis); // 右面
        _runs.Add(new RunCaster { Lo = loCol, Hi = hiCol, Horizontal = false, Gate = gate, State = 0 });
    }

    // プレイヤー位置に応じて各 run の片面だけ active にする (毎フレ RunPerFrameUpdates から)。
    // player が gate より小さい側に居る → Hi 面、逆は Lo 面。
    //   V (far-face): Hi=遠い面 (壁を lit に保つ)。
    //   H (裏暗モード): Hi=表向き(player 南=前)・Lo=裏向き(player 北=裏で壁を影に沈める)。両面とも北端。
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
    private static Collider2D SpawnSegment(Vector2 p0, Vector2 p1, Color visColor)
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

        // 診断: 影 caster の「発射線」を可視化 (/bbshadow casters on)。emission 位置を実機で目視する用。
        if (Visualize)
        {
            Vector2 d = p1 - p0;
            float len = d.magnitude;
            float ang = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;
            GameObject vis = new("BBCasterVis");
            vis.transform.SetParent(go.transform, false);
            vis.transform.localPosition = Vector3.zero;
            vis.transform.localRotation = Quaternion.Euler(0f, 0f, ang);
            vis.transform.localScale = new Vector3(Mathf.Max(len, 0.05f), 0.07f, 1f);
            SpriteRenderer sr = vis.AddComponent<SpriteRenderer>();
            sr.sprite = LineSprite;
            sr.color = visColor;
            sr.sortingLayerName = "Default";
            sr.sortingOrder = 120; // 全部の前に出す
        }

        return ec;
    }

    // 影 caster 発射線の可視化フラグ (/bbshadow casters on|off)。トグル時は _occludersDirty で rebuild が要る。
    public static bool Visualize;

    private static Sprite _lineSprite;
    private static Sprite LineSprite
    {
        get
        {
            if (_lineSprite != null) return _lineSprite;
            Texture2D tex = new(2, 2, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
            Color[] px = [Color.white, Color.white, Color.white, Color.white];
            tex.SetPixels(px);
            tex.Apply();
            _lineSprite = Sprite.Create(tex, new Rect(0, 0, 2, 2), new Vector2(0.5f, 0.5f), 2f);
            _lineSprite.hideFlags |= HideFlags.HideAndDontSave;
            return _lineSprite;
        }
    }

    // 診断: 現在 active な遠面の本数 (Hi 面 / Lo 面)。dark-walls 報告時に「gating 未実行 vs 効いてない」を切り分け。
    public static (int hi, int lo) GateCounts()
    {
        int hi = 0, lo = 0;
        foreach (RunCaster rc in _runs)
        {
            if (rc.State == 1) hi++;
            else if (rc.State == -1) lo++;
        }

        return (hi, lo);
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
