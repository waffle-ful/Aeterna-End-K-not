using System.Collections.Generic;
using UnityEngine;

namespace EndKnot.Modules.MapAtmosphere;

// 朽ちた船の汚れと虫の置き場所を、部屋ごとの「何が朽ちているか」に合わせて決める。
// 置き場所は、部屋の範囲 (PlainShipRoom.roomArea) のうち壁の当たり判定の内側で実際に歩ける床だけを格子で調べて選ぶので、壁の中や船の外には出ない。
// 錆だれ・手形・引っかき傷・蜘蛛の巣・むき出しの配線は床の上端 (その上は壁) に、壁の絵へ掛かるように置く。
// 足跡は床を歩いて壁の手前で途切れる向きに置く。
// 格子は組み立て時に一度だけ作り、虫の歩き回りにも使う。
internal static class AirshipRuinLayout
{
    public enum Kind { Stain, Mold, Drip, Crack, Dust, Vein, Slime, Pool, Tear, Hand, Feet, Scratch, Hair, Web, Growth, Shroom, Burn, Wire, Sewage, Smear, Spatter, Rust, Soot }

    public struct Placement
    {
        public float X, Y, Z, W, H, Rot, A;
        public Kind Kind;
        public int V;
        public Color Tint;
    }

    public sealed class RoomGrid
    {
        public float X0, Y0, Z;
        public int W, H;
        public bool[] In;
        public readonly List<int> Cells = [];
        public Theme Theme;

        public int Ox, Oy; // 船全体の床の格子での、この部屋の格子の左下

        public bool Inside(float x, float y)
        {
            if (x < X0 || y < Y0) return false; // 負の値は 0 へ切り捨てられるので先に弾く
            int gx = (int)((x - X0) / Step), gy = (int)((y - Y0) / Step);
            return gx >= 0 && gy >= 0 && gx < W && gy < H && In[gy * W + gx];
        }

        public void Pick(out float x, out float y)
        {
            int c = Cells[FxMath.Range(0, Cells.Count)];
            x = X0 + (c % W + FxMath.Value) * Step;
            y = Y0 + (c / W + FxMath.Value) * Step;
        }

        // (x0,y0) から (x1,y1) へ、壁を越えずに一歩で行けるか。どちらも床の上である前提。
        public bool CanStep(float x0, float y0, float x1, float y1)
        {
            int ax = (int)((x0 - X0) / Step), ay = (int)((y0 - Y0) / Step);
            int bx = (int)((x1 - X0) / Step), by = (int)((y1 - Y0) / Step);
            int dx = bx - ax, dy = by - ay;
            if (dx == 0 && dy == 0) return true;
            ax += Ox;
            ay += Oy;
            bx += Ox;
            by += Oy;
            if (dx * dx + dy * dy == 1) return Open(ax, ay, bx, by);
            if (dx * dx != 1 || dy * dy != 1) return false;
            // 斜めは、横→縦・縦→横のどちらかの角を回って行ければ良い。
            return Open(ax, ay, bx, ay) && Open(bx, ay, bx, by) || Open(ax, ay, ax, by) && Open(ax, by, bx, by);
        }

        // (x,y) のいる格子から壁を越えずに行ける隣の格子を一つ選び、その中の一点を返す。無ければ部屋のどこか。
        public void PickNear(float x, float y, out float tx, out float ty)
        {
            int cx = (int)((x - X0) / Step), cy = (int)((y - Y0) / Step);
            int start = FxMath.Range(0, 4);
            for (int i = 0; i < 4; i++)
            {
                int d = (start + i) & 3;
                int nx = cx + (d == 0 ? 1 : d == 1 ? -1 : 0), ny = cy + (d == 2 ? 1 : d == 3 ? -1 : 0);
                if (nx < 0 || ny < 0 || nx >= W || ny >= H || !In[ny * W + nx]) continue;
                if (!Open(cx + Ox, cy + Oy, nx + Ox, ny + Oy)) continue;
                tx = X0 + (nx + 0.2f + 0.6f * FxMath.Value) * Step;
                ty = Y0 + (ny + 0.2f + 0.6f * FxMath.Value) * Step;
                return;
            }

            Pick(out tx, out ty);
        }
    }

    // 船全体の歩ける床。バニラの壁の当たり判定 (人の足を止めるもの) を、床と分かっている点 (ベント・出現位置・自分の足元) から
    // 格子で辿って作る。部屋の範囲 (roomArea) は部屋に入ったかの判定用で、壁の絵や船の外まで含むので床の判定には使えない。
    // 格子の隣同士の間に壁があるかも覚えておき、虫が壁をすり抜けないように使う。
    private static float _fx0, _fy0;
    private static int _fw, _fh;
    private static bool[] _floor = [];
    private static byte[] _edge = []; // bit0: 右へ開いている / bit1: 上へ開いている
    private const float FeetOffset = 0.3636f;
    private static int[] _label = []; // 格子を最初に塗った種
    private static readonly List<(float X, float Y)> _seedPts = [];
    private static readonly List<int> _root = []; // 種のまとまり (union-find)
    private static readonly List<bool> _leaky = [];

    private static int Find(int k)
    {
        while (_root[k] != k)
        {
            _root[k] = _root[_root[k]];
            k = _root[k];
        }

        return k;
    }

    private const int WallRows = 5; // 上端とみなすのに、上へ何マス床が無ければよいか (壁の絵の高さより低く、机の奥行きより高く)

    // (x, y) が歩ける床の上か。組み立て後だけ意味を持つ。
    public static bool IsFloorAt(float x, float y)
    {
        if (_fw == 0 || x < _fx0 || y < _fy0) return false;
        return IsFloor((int)((x - _fx0) / Step), (int)((y - _fy0) / Step));
    }

    private static bool IsFloor(int gx, int gy) => gx >= 0 && gy >= 0 && gx < _fw && gy < _fh && _floor[gy * _fw + gx];

    private static bool Open(int ax, int ay, int bx, int by)
    {
        if (!IsFloor(ax, ay) || !IsFloor(bx, by)) return false;
        if (bx == ax + 1 && by == ay) return (_edge[ay * _fw + ax] & 1) != 0;
        if (bx == ax - 1 && by == ay) return (_edge[by * _fw + bx] & 1) != 0;
        if (by == ay + 1 && bx == ax) return (_edge[ay * _fw + ax] & 2) != 0;
        if (by == ay - 1 && bx == ax) return (_edge[by * _fw + bx] & 2) != 0;
        return false;
    }

    // 壁 = 人の足を止める当たり判定。どの層が足を止めるかは物理設定 (層どうしの衝突表) で決まるので、
    // プレイヤーの層と衝突する層をそこから取る (船の外周の境界もこれに入る)。他のプレイヤー自身は除く。
    // 部屋の範囲などのトリガーは壁ではないので、呼ぶ側で Physics2D.queriesHitTriggers を切っておく。
    private static int _wallMask;

    private static int WallMask()
    {
        PlayerControl lp = PlayerControl.LocalPlayer;
        if (!lp) return Constants.ShipAndAllObjectsMask;
        int layer = lp.gameObject.layer;
        return Physics2D.GetLayerCollisionMask(layer) & ~(1 << layer);
    }

    // 線ではなく人の足ほどの太さで当てる。壁の辺の継ぎ目にわずかな隙間がある所 (エアシップの会議室のはしご脇は 0.1) を
    // 線だと素通りして、船の外まで床が広がる。
    private const float FootRadius = 0.1f;

    private static bool NoWall(float ax, float ay, float bx, float by)
    {
        float dx = bx - ax, dy = by - ay;
        float len = FxMath.Sqrt(dx * dx + dy * dy);
        if (len < 0.0001f) return !Physics2D.OverlapCircle(FxMath.V2(ax, ay), FootRadius, _wallMask);
        return !Physics2D.CircleCast(FxMath.V2(ax, ay), FootRadius, FxMath.V2(dx / len, dy / len), len, _wallMask).collider;
    }

    private static void BuildFloor(ShipStatus ship)
    {
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (PlainShipRoom room in ship.AllRooms)
        {
            if (!room || !room.roomArea) continue;
            Bounds b = room.roomArea.bounds;
            minX = FxMath.Min(minX, b.min.x);
            minY = FxMath.Min(minY, b.min.y);
            maxX = FxMath.Max(maxX, b.max.x);
            maxY = FxMath.Max(maxY, b.max.y);
        }

        _fw = _fh = 0;
        _floor = [];
        _edge = [];
        if (minX > maxX) return;

        _fx0 = System.MathF.Floor(minX) - 1f;
        _fy0 = System.MathF.Floor(minY) - 1f;
        _fw = (int)System.MathF.Ceiling((maxX + 1f - _fx0) / Step);
        _fh = (int)System.MathF.Ceiling((maxY + 1f - _fy0) / Step);
        if (_fw <= 0 || _fh <= 0 || _fw * _fh > 200000)
        {
            _fw = _fh = 0;
            return;
        }

        _wallMask = WallMask();
        _floor = new bool[_fw * _fh];
        _edge = new byte[_fw * _fh];
        _label = new int[_fw * _fh];
        _seedPts.Clear();
        _root.Clear();
        _leaky.Clear();
        var queue = new Queue<int>();

        var seeds = new List<(float X, float Y)>();
        foreach (Vent v in ship.AllVents)
        {
            if (!v) continue;
            Vector3 p = v.transform.position;
            seeds.Add((p.x, p.y));
        }

        foreach (Vector2 p in new RandomSpawn.AirshipSpawnMap().Positions.Values)
            seeds.Add((p.x, p.y - FeetOffset));

        // はしごの上下の降り口。会議室のようにベントも出現位置も無い所は、ここからしか辿れない。
        foreach (Ladder ladder in ship.GetComponentsInChildren<Ladder>(true))
        {
            if (!ladder) continue;
            Vector3 p = ladder.transform.position;
            seeds.Add((p.x, p.y));
        }

        foreach (PlayerControl pc in PlayerControl.AllPlayerControls)
        {
            if (!pc || pc.Data == null || pc.Data.IsDead) continue;
            Vector3 p = pc.transform.position;
            seeds.Add((p.x, p.y - FeetOffset));
        }

        foreach ((float sx, float sy) in seeds)
        {
            int gx = (int)((sx - _fx0) / Step), gy = (int)((sy - _fy0) / Step);
            if (gx < 0 || gy < 0 || gx >= _fw || gy >= _fh) continue;
            int c = gy * _fw + gx;
            if (_floor[c] || !NoWall(sx, sy, _fx0 + (gx + 0.5f) * Step, _fy0 + (gy + 0.5f) * Step)) continue;
            _floor[c] = true;
            _label[c] = _seedPts.Count;
            _seedPts.Add((sx, sy));
            _root.Add(_root.Count);
            _leaky.Add(false);
            queue.Enqueue(c);
        }

        // 種ごとに広げ、出会った種同士はまとめる。格子の外周 (船の外) まで届いたまとまりは、壁の外から広がったものなので捨てる
        // (出現位置が壁の当たり判定の外側にある部屋がある)。捨てると決まったまとまりはそれ以上広げない。
        // 各格子の右と上の辺だけを調べる (左と下は隣の格子の右と上)。辺は一度ずつしか調べない。
        var tested = new byte[_fw * _fh];
        while (queue.Count > 0)
        {
            int c = queue.Dequeue();
            int gx = c % _fw, gy = c / _fw;
            int root = Find(_label[c]);
            if (gx == 0 || gy == 0 || gx == _fw - 1 || gy == _fh - 1) _leaky[root] = true;
            if (_leaky[root]) continue;

            float cx = _fx0 + (gx + 0.5f) * Step, cy = _fy0 + (gy + 0.5f) * Step;
            for (int d = 0; d < 4; d++)
            {
                int nx = gx + (d == 0 ? 1 : d == 1 ? -1 : 0), ny = gy + (d == 2 ? 1 : d == 3 ? -1 : 0);
                if (nx < 0 || ny < 0 || nx >= _fw || ny >= _fh) continue;
                int n = ny * _fw + nx;
                int owner = d is 0 or 2 ? c : n;
                byte bit = d is 0 or 1 ? (byte)1 : (byte)2;
                if ((tested[owner] & bit) != 0) continue;
                tested[owner] |= bit;
                if (!NoWall(cx, cy, _fx0 + (nx + 0.5f) * Step, _fy0 + (ny + 0.5f) * Step)) continue;
                _edge[owner] |= bit;
                if (_floor[n])
                {
                    int other = Find(_label[n]);
                    if (other == root) continue;
                    _root[other] = root;
                    _leaky[root] |= _leaky[other];
                    continue;
                }

                _floor[n] = true;
                _label[n] = _label[c];
                queue.Enqueue(n);
            }
        }

        for (int i = 0; i < _floor.Length; i++)
        {
            if (!_floor[i] || !_leaky[Find(_label[i])]) continue;
            _floor[i] = false;
            _edge[i] = 0;
        }
    }

    public const float Step = 0.3f;

    public enum Theme { Rot, Wet, Clinic, Grand, Machine, Hall }

    // 部屋の名前 → 何が朽ちているか。シャワー室は手置きなのでここに無い。
    private static readonly Dictionary<string, Theme> Themes = new()
    {
        ["Kitchen"] = Theme.Rot,
        ["Lounge"] = Theme.Wet,
        ["Medbay"] = Theme.Clinic,
        ["MeetingRoom"] = Theme.Grand,
        ["Records"] = Theme.Grand,
        ["Vault"] = Theme.Grand,
        ["Cockpit"] = Theme.Grand,
        ["Comms"] = Theme.Grand,
        ["Brig"] = Theme.Grand,
        ["Security"] = Theme.Grand,
        ["Engine"] = Theme.Machine,
        ["Electrical"] = Theme.Machine,
        ["Storage"] = Theme.Machine,
        ["Ventilation"] = Theme.Machine,
        ["Armory"] = Theme.Machine,
        ["Ejection"] = Theme.Machine,
        ["GapRoom"] = Theme.Machine,
        ["HallwayL"] = Theme.Hall,
        ["HallwayMain"] = Theme.Hall,
        ["HallwayPortrait"] = Theme.Hall
    };

    // 床 1 平方単位あたりの汚れの数 / 上端 1 単位あたりの垂れ / 上端 1 単位あたりの壁の跡 (手形・引っかき傷・蜘蛛の巣・足跡)
    private static readonly Dictionary<Theme, (float Density, float Drips, float Marks)> Amounts = new()
    {
        [Theme.Rot] = (0.58f, 0.35f, 0.26f),
        [Theme.Wet] = (0.54f, 0.45f, 0.24f),
        [Theme.Clinic] = (0.52f, 0.3f, 0.36f),
        [Theme.Grand] = (0.45f, 0.2f, 0.3f),
        [Theme.Machine] = (0.45f, 0.4f, 0.26f),
        [Theme.Hall] = (0.38f, 0.2f, 0.3f)
    };

    // 上端 1 単位あたりのむき出しの配線。機械の部屋ほど多い。
    private static readonly Dictionary<Theme, float> Wires = new()
    {
        [Theme.Rot] = 0.05f,
        [Theme.Wet] = 0.04f,
        [Theme.Clinic] = 0.08f,
        [Theme.Grand] = 0.08f,
        [Theme.Machine] = 0.25f,
        [Theme.Hall] = 0.1f
    };

    // 床の汚れの選び方 (重み)。Oil は Stain を黒く沈めたもの。
    private enum Floor { Stain, Oil, BloodStain, Mold, Crack, Vein, Pool, Tear, Scratch, Hair, Growth, Shroom, Burn, Sewage, Smear, Spatter, Rust }

    private static readonly Dictionary<Theme, (Floor Kind, int Weight)[]> Weights = new()
    {
        [Theme.Rot] = [(Floor.Stain, 3), (Floor.Mold, 3), (Floor.Pool, 3), (Floor.Shroom, 2), (Floor.Hair, 1), (Floor.Growth, 1), (Floor.Vein, 1), (Floor.Crack, 1), (Floor.Sewage, 2), (Floor.Spatter, 1)],
        [Theme.Wet] = [(Floor.Mold, 4), (Floor.Pool, 3), (Floor.Hair, 3), (Floor.Shroom, 2), (Floor.Stain, 2), (Floor.Vein, 1), (Floor.Crack, 1), (Floor.Sewage, 3)],
        [Theme.Clinic] = [(Floor.Vein, 4), (Floor.Growth, 3), (Floor.BloodStain, 3), (Floor.Pool, 2), (Floor.Hair, 1), (Floor.Mold, 1), (Floor.Crack, 1), (Floor.Smear, 3), (Floor.Spatter, 3)],
        [Theme.Grand] = [(Floor.Tear, 4), (Floor.Stain, 3), (Floor.Crack, 2), (Floor.Scratch, 2), (Floor.Hair, 1), (Floor.Burn, 1), (Floor.Vein, 1), (Floor.Mold, 1), (Floor.Smear, 1), (Floor.Spatter, 1)],
        [Theme.Machine] = [(Floor.Oil, 4), (Floor.Burn, 3), (Floor.Crack, 3), (Floor.Scratch, 1), (Floor.Stain, 1), (Floor.Mold, 1), (Floor.Vein, 1), (Floor.Pool, 1), (Floor.Rust, 4), (Floor.Smear, 1)],
        [Theme.Hall] = [(Floor.Stain, 3), (Floor.Crack, 2), (Floor.Vein, 2), (Floor.Scratch, 1), (Floor.Hair, 1), (Floor.Growth, 1), (Floor.Burn, 1), (Floor.Mold, 1), (Floor.Pool, 1), (Floor.Tear, 1), (Floor.Oil, 1), (Floor.Smear, 2), (Floor.Spatter, 1), (Floor.Rust, 1), (Floor.Sewage, 1)]
    };

    // 壁の跡の選び方 (重み)。Wire (壁のパネルが剥がれて配線が垂れ下がったもの) は別枠で数を決める。
    private enum Wall { Hand, Scratch, Tally, Web, Feet, Wire, Spatter, Soot }

    private static readonly Dictionary<Theme, (Wall Kind, int Weight)[]> WallWeights = new()
    {
        [Theme.Rot] = [(Wall.Hand, 2), (Wall.Web, 2), (Wall.Feet, 1), (Wall.Scratch, 1), (Wall.Spatter, 1)],
        [Theme.Wet] = [(Wall.Hand, 2), (Wall.Feet, 2), (Wall.Web, 1), (Wall.Scratch, 1), (Wall.Spatter, 1)],
        [Theme.Clinic] = [(Wall.Hand, 4), (Wall.Scratch, 2), (Wall.Feet, 2), (Wall.Tally, 1), (Wall.Spatter, 3)],
        [Theme.Grand] = [(Wall.Web, 3), (Wall.Scratch, 2), (Wall.Tally, 2), (Wall.Hand, 1), (Wall.Feet, 1), (Wall.Soot, 2), (Wall.Spatter, 1)],
        [Theme.Machine] = [(Wall.Web, 3), (Wall.Scratch, 2), (Wall.Hand, 1), (Wall.Feet, 1), (Wall.Soot, 3)],
        [Theme.Hall] = [(Wall.Feet, 3), (Wall.Hand, 2), (Wall.Scratch, 2), (Wall.Tally, 1), (Wall.Web, 1), (Wall.Spatter, 2), (Wall.Soot, 1)]
    };

    private static readonly Color White = new(1f, 1f, 1f, 1f);
    private static readonly Color OilTint = new(0.32f, 0.33f, 0.38f, 1f);
    private static readonly Color BloodTint = new(1f, 0.45f, 0.4f, 1f);

    public static readonly List<Placement> Placements = [];
    public static readonly List<RoomGrid> Grids = [];

    public static void Build(ShipStatus ship)
    {
        Placements.Clear();
        Grids.Clear();
        int cellsTotal = 0;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool hitTriggers = Physics2D.queriesHitTriggers;
        Physics2D.queriesHitTriggers = false;
        try { BuildFloor(ship); }
        finally { Physics2D.queriesHitTriggers = hitTriggers; }
        long floorMs = sw.ElapsedMilliseconds;
        CollectLowProps(ship);
        int floorCells = 0;
        foreach (bool f in _floor)
            if (f) floorCells++;

        foreach (PlainShipRoom room in ship.AllRooms)
        {
            if (!room || !room.roomArea) continue;
            string name = room.name;
            bool showers = name == "Showers";
            if (!showers && !Themes.ContainsKey(name)) continue;

            RoomGrid grid = MakeGrid(room, showers ? Theme.Wet : Themes[name]);
            if (grid == null || grid.Cells.Count == 0) continue;
            Grids.Add(grid);
            cellsTotal += grid.Cells.Count;
            if (!showers) Scatter(room, grid);
        }

        int wires = 0;
        foreach (Placement p in Placements)
            if (p.Kind == Kind.Wire) wires++;
        Logger.Info($"AirshipRuin layout rooms={Grids.Count} cells={cellsTotal} decals={Placements.Count} wires={wires} floor={floorCells} floorMs={floorMs} totalMs={sw.ElapsedMilliseconds}", "AirshipLiminal");
    }

    private static RoomGrid MakeGrid(PlainShipRoom room, Theme theme)
    {
        if (_fw == 0) return null;
        Bounds b = room.roomArea.bounds;
        // 船全体の床の格子に升目を揃える。
        int ox = (int)System.MathF.Floor((b.min.x - _fx0) / Step), oy = (int)System.MathF.Floor((b.min.y - _fy0) / Step);
        var g = new RoomGrid
        {
            X0 = _fx0 + ox * Step,
            Y0 = _fy0 + oy * Step,
            Ox = ox,
            Oy = oy,
            Theme = theme,
            Z = ArtZ(room)
        };
        g.W = (int)System.MathF.Ceiling((b.max.x - g.X0) / Step);
        g.H = (int)System.MathF.Ceiling((b.max.y - g.Y0) / Step);
        if (g.W <= 0 || g.H <= 0 || g.W * g.H > 20000) return null;
        g.In = new bool[g.W * g.H];

        Collider2D area = room.roomArea;
        for (int y = 0; y < g.H; y++)
        {
            for (int x = 0; x < g.W; x++)
            {
                if (!IsFloor(x + ox, y + oy)) continue;
                if (!area.OverlapPoint(FxMath.V2(g.X0 + (x + 0.5f) * Step, g.Y0 + (y + 0.5f) * Step))) continue;
                g.In[y * g.W + x] = true;
                g.Cells.Add(y * g.W + x);
            }
        }

        return g;
    }

    // 部屋の床と壁の一枚絵のすぐ手前。z>1 の絵 (床・壁・奥の配管) の一番手前から少しだけ前に出す。
    // 小物 (机・手すり・扉) は z≒0〜1 なので、その下に入る。
    private static float ArtZ(PlainShipRoom room)
    {
        float z = float.MaxValue;
        foreach (SpriteRenderer sr in room.GetComponentsInChildren<SpriteRenderer>(true))
        {
            if (!sr || sr.gameObject.layer != 9) continue;
            float sz = sr.transform.position.z;
            if (sz > 1f && sz < z) z = sz;
        }

        return z == float.MaxValue ? 4.8f : z - 0.05f;
    }

    private static void Scatter(PlainShipRoom room, RoomGrid g)
    {
        (float density, float drips, float marks) = Amounts[g.Theme];
        (Floor Kind, int Weight)[] weights = Weights[g.Theme];
        int total = 0;
        foreach ((_, int w) in weights) total += w;

        float area = g.Cells.Count * Step * Step;
        int count = (int)System.MathF.Round(area * density);
        int order = 0; // 奥行きのずらしは部屋ごと (部屋の絵の z から引く量を小さく保つ)

        for (int i = 0; i < count; i++)
        {
            g.Pick(out float x, out float y);
            int roll = FxMath.Range(0, total);
            Floor kind = weights[0].Kind;
            foreach ((Floor k, int w) in weights)
            {
                if ((roll -= w) < 0) { kind = k; break; }
            }

            // 床からはみ出す (壁の絵に掛かる)・小物の下に潜る所は、場所を変えて引き直す。
            for (int t = 0; t < 4 && !Add(g, x, y, kind, order); t++) g.Pick(out x, out y);
            order++;
        }

        // 上端: その格子が床で、その上しばらく床が無い所 (壁の絵)。錆だれ (と粘液の筋) を壁の絵へ掛ける。
        // 机や箱の足元も上が床でなくなるが、奥にまた床があるので上端にしない。
        var tops = new List<int>();
        foreach (int c in g.Cells)
        {
            int gx = c % g.W + g.Ox, gy = c / g.W + g.Oy;
            bool wall = true;
            for (int k = 1; k <= WallRows && wall; k++)
                if (IsFloor(gx, gy + k)) wall = false;
            if (wall) tops.Add(c);
        }

        // 壁の跡 (垂れ・手形・配線など) は本当の壁にだけ付ける。上端には家具 (箱・台・壁に立て掛けた箒) の手前も混じるので、
        // 左右に同じ高さの上端が続かない所 (壁際の細い物) と、垂れた跡が家具の絵に重なる所 (箱・台・壁の小物) は除く。
        // 部屋の真ん中の仕切り壁 (武器庫など) は奥にも床があるが壁なので、奥の床の有無では分けない。
        List<Bounds> furniture = Furniture(room);
        var wallTops = new List<int>();
        var topSet = new HashSet<int>(tops);
        var rejected = new int[4];
        foreach (int c in tops)
        {
            int why = BareWallReject(g, c, topSet, furniture);
            if (why < 0) wallTops.Add(c);
            else rejected[why]++;
        }

        Logger.Info($"AirshipRuin walls {room.name} tops={tops.Count} bare={wallTops.Count} short={rejected[1]} furniture={rejected[2]} art={rejected[3]}", "AirshipLiminal");
        // 数は除く前の上端の長さで決める (家具の多い部屋でも壁の汚れが減りすぎないように)。
        // ただし残った壁が短い部屋で同じ所に重ならないよう、種類ごとに残った格子の数までにする。
        int topLength = tops.Count;
        tops = wallTops;

        int dripCount = System.Math.Min((int)System.MathF.Round(topLength * Step * drips), tops.Count);
        for (int i = 0; i < dripCount && tops.Count > 0; i++)
        {
            int c = tops[FxMath.Range(0, tops.Count)];
            float x = g.X0 + (c % g.W + FxMath.Value) * Step;
            float y = g.Y0 + (c / g.W + 1f) * Step + 0.3f;
            bool slime = g.Theme is Theme.Rot or Theme.Wet or Theme.Clinic ? FxMath.Value < 0.4f : FxMath.Value < 0.12f;
            float h = FxMath.Range(0.5f, 0.85f);
            if (UnderLowProp(x, y) || UnderLowProp(x, y - h * 0.5f)) continue;
            Placements.Add(new Placement
            {
                X = x, Y = y, Z = g.Z - (order++) * 0.0004f, W = slime ? 0.38f : 0.3f, H = h, Rot = 0f,
                A = FxMath.Range(0.6f, 0.9f), Kind = slime ? Kind.Slime : Kind.Drip, V = FxMath.Range(0, 2),
                Tint = g.Theme == Theme.Machine && !slime ? OilTint : White
            });
        }

        int markCount = System.Math.Min((int)System.MathF.Round(topLength * Step * marks), tops.Count);
        for (int i = 0; i < markCount && tops.Count > 0; i++)
            AddWallMark(g, tops[FxMath.Range(0, tops.Count)], order++, false);

        int wireCount = System.Math.Min((int)System.MathF.Round(topLength * Step * Wires[g.Theme]), tops.Count);
        for (int i = 0; i < wireCount && tops.Count > 0; i++)
            AddWallMark(g, tops[FxMath.Range(0, tops.Count)], order++, true);

        // 部屋全体のくすみ。部屋の歩ける範囲を覆う大きさで一枚。
        Bounds b = room.roomArea.bounds;
        Placements.Add(new Placement
        {
            X = b.center.x, Y = b.center.y, Z = g.Z + 0.03f, W = b.size.x + 1f, H = b.size.y + 1f, Rot = 0f,
            A = 0.55f, Kind = Kind.Dust, V = 0, Tint = White
        });
    }

    private const int WireRun = 2; // 配線を垂らす上端の左右に、同じ高さの上端が何マスずつ続いていればよいか
    private const float WireDrop = 1f; // 上端から上へ、配線と剥がれたパネルが掛かる高さ

    // 部屋の一枚絵に描き込まれた物 (ロッカー・棚・窓ガラス・調理台・木箱・額縁・機器など) の前の上端。
    // 床の当たり判定では壁の前と見分けられないので、部屋の絵の画素から決めた範囲を持つ (エアシップは配置が固定)。
    // (床の際の y, 端の格子の中心 x, もう一方の端の格子の中心 x)。
    private static readonly (float Edge, float X0, float X1)[] ArtClutter =
    [
        (-14.50f, 5.55f, 6.45f), (-14.50f, 7.65f, 8.85f), (-11.80f, -0.75f, -0.45f), (-11.80f, 0.75f, 1.05f),
        (-11.80f, 1.95f, 2.55f), (-11.80f, 3.45f, 3.75f), (-11.20f, -1.95f, -1.95f), (-10.90f, 13.95f, 15.15f),
        (-10.60f, 7.95f, 8.25f), (-10.60f, 15.45f, 15.45f), (-10.30f, 7.35f, 7.65f), (-10.30f, 8.55f, 8.85f),
        (-10.00f, 4.95f, 6.15f), (-10.00f, 9.15f, 9.15f), (-9.70f, -6.15f, -6.15f), (-9.40f, 24.15f, 24.45f),
        (-9.40f, 25.95f, 26.25f), (-9.10f, -5.55f, -2.55f), (-8.20f, -15.45f, -15.45f), (-8.20f, 11.25f, 11.25f),
        (-8.20f, 23.85f, 23.85f), (-7.90f, -13.65f, -13.65f), (-6.40f, 21.75f, 21.75f), (-6.10f, -8.25f, -7.05f),
        (-6.10f, 11.55f, 11.85f), (-6.10f, 14.55f, 14.85f), (-6.10f, 17.55f, 17.85f), (-5.80f, 9.45f, 11.25f),
        (-5.80f, 15.15f, 16.95f), (-5.80f, 22.05f, 22.35f), (-5.50f, 12.75f, 13.05f), (-5.50f, 22.65f, 22.65f),
        (-5.50f, 26.25f, 26.85f), (-5.20f, -13.05f, -13.05f), (-5.20f, 23.55f, 23.55f), (-5.20f, 27.15f, 29.25f),
        (-4.00f, 17.55f, 17.85f), (-3.40f, -14.85f, -14.55f), (-3.40f, -9.15f, -9.15f), (-3.10f, -14.25f, -14.25f),
        (-3.10f, 31.05f, 31.05f), (-3.10f, 36.75f, 36.75f), (-2.80f, 37.05f, 37.05f), (-2.50f, 31.95f, 32.25f),
        (-1.90f, -24.75f, -24.75f), (-1.30f, 1.95f, 1.95f), (-1.30f, 32.55f, 32.55f), (-1.00f, -16.35f, -16.05f),
        (-1.00f, -8.85f, -8.85f), (-1.00f, 30.15f, 31.65f), (-0.70f, -5.85f, -2.25f), (-0.40f, -24.45f, -24.15f),
        (-0.40f, 0.75f, 1.65f), (-0.10f, -23.85f, -23.85f), (-0.10f, 0.45f, 0.45f), (-0.10f, 17.25f, 17.25f),
        (0.20f, -23.55f, -23.25f), (0.20f, 2.25f, 3.75f), (0.50f, -22.95f, -22.65f), (0.50f, 5.55f, 5.55f),
        (0.80f, -22.35f, -21.45f), (0.80f, -16.65f, -16.65f), (0.80f, 19.95f, 23.25f), (0.80f, 24.75f, 24.75f),
        (1.10f, -21.15f, -16.95f), (1.10f, -7.35f, -7.35f), (1.10f, -6.15f, -6.15f), (1.10f, -2.25f, -2.25f),
        (1.10f, 0.15f, 0.15f), (1.10f, 19.05f, 19.35f), (1.40f, -7.65f, -7.65f), (1.40f, -7.05f, -7.05f),
        (1.40f, -5.85f, -5.85f), (1.40f, -4.95f, -4.35f), (1.40f, -3.75f, -3.75f), (1.40f, -1.35f, -1.35f),
        (1.40f, 36.75f, 38.85f), (1.70f, 31.05f, 31.05f), (2.00f, -14.25f, -12.45f), (2.00f, 8.25f, 8.25f),
        (2.00f, 30.15f, 30.15f), (2.00f, 30.75f, 30.75f), (2.00f, 35.55f, 35.55f), (2.30f, -14.85f, -14.85f),
        (2.30f, 8.55f, 9.15f), (2.30f, 12.15f, 13.05f), (2.30f, 15.45f, 16.05f), (2.60f, 11.55f, 11.85f),
        (2.60f, 16.35f, 16.35f), (3.20f, -12.15f, -11.85f), (3.20f, 6.45f, 6.75f), (3.20f, 9.45f, 9.45f),
        (3.20f, 10.35f, 10.35f), (3.50f, 5.85f, 6.15f), (3.50f, 9.75f, 10.05f), (3.50f, 14.55f, 15.15f),
        (4.40f, 18.45f, 18.75f), (4.70f, 19.05f, 19.05f), (5.00f, 25.35f, 26.55f), (5.30f, 17.25f, 17.25f),
        (5.60f, 27.75f, 27.75f), (5.60f, 28.35f, 28.35f), (5.90f, -8.25f, -8.25f), (6.50f, 27.45f, 27.45f),
        (7.70f, 25.35f, 26.55f), (8.90f, -11.55f, -10.05f), (8.90f, 13.95f, 14.25f), (9.20f, -9.75f, -9.45f),
        (9.20f, 14.55f, 14.85f), (9.50f, -8.25f, -8.25f), (9.50f, 3.15f, 3.45f), (9.50f, 13.05f, 13.65f),
        (9.50f, 15.15f, 15.15f), (9.50f, 16.65f, 16.65f), (9.80f, 15.45f, 16.05f), (9.80f, 16.95f, 16.95f),
        (9.80f, 22.95f, 23.55f), (9.80f, 24.45f, 24.75f), (10.10f, 25.05f, 27.15f), (10.70f, 22.65f, 22.65f),
        (11.00f, 17.25f, 17.25f), (11.90f, -5.55f, -5.55f), (12.20f, -5.85f, -5.85f), (12.50f, -11.55f, -11.25f),
        (12.50f, -6.75f, -6.15f), (12.80f, -10.65f, -10.35f), (12.80f, -7.05f, -7.05f), (13.10f, -10.05f, -9.45f),
        (13.10f, -8.25f, -7.35f), (14.30f, 9.75f, 9.75f), (14.30f, 10.65f, 10.65f), (14.30f, 12.15f, 12.15f),
        (14.30f, 13.05f, 13.35f), (15.50f, 7.05f, 7.95f), (15.50f, 14.55f, 14.85f), (15.80f, 5.25f, 5.55f),
        (15.80f, 6.45f, 6.75f), (15.80f, 15.15f, 15.75f), (16.10f, 3.75f, 4.35f)
    ];

    // 本当の壁なら -1。違えば理由 (1=左右に上端が続かない・2=家具の絵に重なる・3=部屋の絵に描き込まれた物の前)。
    private static int BareWallReject(RoomGrid g, int c, HashSet<int> tops, List<Bounds> furniture)
    {
        int cx = c % g.W, cy = c / g.W;

        // 斜めの壁 (45° まで) や当たり判定の段差で上端の高さはずれるので、k マス隣は上下 k 段までのずれを許す。
        for (int k = 1; k <= WireRun; k++)
        {
            if (cx - k < 0 || cx + k >= g.W) return 1;
            if (!NearTop(g, tops, cx - k, cy, k) || !NearTop(g, tops, cx + k, cy, k)) return 1;
        }

        float x0 = g.X0 + cx * Step, x1 = x0 + Step, y0 = g.Y0 + (cy + 1f) * Step - 0.3f, y1 = y0 + 0.3f + WireDrop;
        foreach (Bounds b in furniture)
            if (b.max.x > x0 && b.min.x < x1 && b.max.y > y0 && b.min.y < y1) return 2;

        // 壁の跡は格子一つより幅が広く、隣の格子の上まで掛かるので、描き込まれた物の両隣の格子も除く (物の前の上端が一段ずれていても)。
        float mid = x0 + Step * 0.5f, edge = y0 + 0.3f;
        foreach ((float e, float a, float b) in ArtClutter)
            if (System.MathF.Abs(e - edge) < Step * 1.5f && mid > a - Step * 1.5f && mid < b + Step * 1.5f) return 3;
        return -1;
    }

    private static bool NearTop(RoomGrid g, HashSet<int> tops, int x, int y, int reach)
    {
        for (int dy = -reach; dy <= reach; dy++)
        {
            int yy = y + dy;
            if (yy >= 0 && yy < g.H && tops.Contains(yy * g.W + x)) return true;
        }

        return false;
    }

    // 部屋の中の家具や小物の絵の範囲。部屋の一枚絵・壁の絵のような大きな物は除く。
    private static List<Bounds> Furniture(PlainShipRoom room)
    {
        var list = new List<Bounds>();
        foreach (SpriteRenderer sr in room.GetComponentsInChildren<SpriteRenderer>(false))
        {
            if (!sr || !sr.enabled || !sr.sprite) continue;
            Bounds b = sr.bounds;
            if (b.size.x * b.size.y > 12f || b.size.x < 0.05f || b.size.y < 0.05f) continue;
            if (sr.sprite.name == "blank") continue;
            list.Add(b);
        }

        return list;
    }

    private static bool Add(RoomGrid g, float x, float y, Floor floor, int order)
    {
        var p = new Placement { X = x, Y = y, Z = g.Z - order * 0.0004f, Rot = FxMath.Range(0f, 360f), A = FxMath.Range(0.8f, 1f), Tint = White };
        switch (floor)
        {
            case Floor.Stain:
            case Floor.Oil:
            case Floor.BloodStain:
                p.Kind = Kind.Stain;
                p.V = FxMath.Range(0, 4);
                p.W = FxMath.Range(0.8f, 1.5f);
                p.Tint = floor == Floor.Oil ? OilTint : floor == Floor.BloodStain ? BloodTint : White;
                break;
            case Floor.Mold:
                p.Kind = Kind.Mold;
                p.V = FxMath.Range(0, 3);
                p.W = FxMath.Range(0.5f, 1f);
                break;
            case Floor.Crack:
                p.Kind = Kind.Crack;
                p.V = FxMath.Range(0, 2);
                p.W = FxMath.Range(0.7f, 1.3f);
                break;
            case Floor.Vein:
                p.Kind = Kind.Vein;
                p.V = FxMath.Range(0, 3);
                p.W = FxMath.Range(1.1f, 1.8f);
                break;
            case Floor.Pool:
                p.Kind = Kind.Pool;
                p.V = FxMath.Range(0, 2);
                p.W = FxMath.Range(0.9f, 1.5f);
                break;
            case Floor.Scratch:
                p.Kind = Kind.Scratch;
                p.V = FxMath.Range(0, 2);
                p.W = FxMath.Range(0.6f, 0.9f);
                break;
            case Floor.Hair:
                p.Kind = Kind.Hair;
                p.V = FxMath.Range(0, 2);
                p.W = FxMath.Range(0.4f, 0.6f);
                break;
            case Floor.Growth:
                p.Kind = Kind.Growth;
                p.V = FxMath.Range(0, 2);
                p.W = FxMath.Range(0.7f, 1.1f);
                p.Rot = FxMath.Range(-20f, 20f); // 光の当たる向きが揃うよう、ほぼ正立
                break;
            case Floor.Shroom:
                p.Kind = Kind.Shroom;
                p.V = FxMath.Range(0, 2);
                p.W = FxMath.Range(0.4f, 0.6f);
                p.Rot = 0f; // 横から見た絵なので傾けない
                break;
            case Floor.Burn:
                p.Kind = Kind.Burn;
                p.V = FxMath.Range(0, 2);
                p.W = FxMath.Range(0.9f, 1.4f);
                break;
            case Floor.Sewage:
                p.Kind = Kind.Sewage;
                p.V = FxMath.Range(0, 2);
                p.W = FxMath.Range(0.9f, 1.5f);
                break;
            case Floor.Smear:
                // 引きずった跡は長いので、置く向きはそのまま自由に回す。
                p.Kind = Kind.Smear;
                p.V = FxMath.Range(0, 2);
                p.W = FxMath.Range(1.4f, 2.1f);
                break;
            case Floor.Spatter:
                p.Kind = Kind.Spatter;
                p.V = FxMath.Range(0, 2);
                p.W = FxMath.Range(0.6f, 1.1f);
                break;
            case Floor.Rust:
                p.Kind = Kind.Rust;
                p.V = FxMath.Range(0, 2);
                p.W = FxMath.Range(0.8f, 1.4f);
                break;
            default:
                p.Kind = Kind.Tear;
                p.V = FxMath.Range(0, 2);
                p.W = FxMath.Range(0.8f, 1.4f);
                p.H = p.W * FxMath.Range(0.5f, 0.9f); // 裂け目は形が揃わないよう縦横比を崩す
                break;
        }

        // 汚れの見える範囲 (絵の八割) が床からはみ出す所や小物の下に潜る所は、少しずつ小さくして収まるか試す。
        float aspect = p.H > 0f ? p.H / p.W : p.Kind == Kind.Smear ? 160f / 384f : 1f;
        foreach (float k in FitScales)
        {
            if (!Footprint(p.X, p.Y, p.W * k * 0.4f, p.W * k * aspect * 0.4f, p.Rot, true)) continue;
            p.W *= k;
            if (p.H > 0f) p.H *= k;
            Placements.Add(p);
            return true;
        }

        return false;
    }

    private static readonly float[] FitScales = [1f, 0.75f, 0.55f];

    // 中心 (x, y)・半径 (hx, hy)・向き rot の楕円の中心と縁 8 点が、小物の下でなく (floor なら床の上に) あるか。
    private static bool Footprint(float x, float y, float hx, float hy, float rot, bool floor)
    {
        float rad = rot * FxMath.Deg2Rad, cos = FxMath.Cos(rad), sin = FxMath.Sin(rad);
        for (int i = 0; i < 9; i++)
        {
            float lx = 0f, ly = 0f;
            if (i > 0)
            {
                float a = (i - 1) * (System.MathF.PI / 4f);
                lx = FxMath.Cos(a) * hx;
                ly = FxMath.Sin(a) * hy;
            }

            float px = x + lx * cos - ly * sin, py = y + lx * sin + ly * cos;
            if (floor && !IsFloorAt(px, py)) return false;
            if (UnderLowProp(px, py)) return false;
        }

        return true;
    }

    // 影のカメラ (layer 9〜12) が描かない layer 0 の小物 (倉庫の箱の山・手すり・椅子など)。影の中では汚れがこれらの上に写ってしまうので、
    // その範囲には汚れを置かない。部屋の一枚絵のような大きな物は除く。
    private static readonly List<Bounds> LowProps = [];

    private static void CollectLowProps(ShipStatus ship)
    {
        LowProps.Clear();
        foreach (PlainShipRoom room in ship.AllRooms)
        {
            if (!room) continue;
            foreach (SpriteRenderer sr in room.GetComponentsInChildren<SpriteRenderer>(false))
            {
                if (!sr || !sr.enabled || !sr.sprite || sr.gameObject.layer != 0) continue;
                Bounds b = sr.bounds;
                if (b.size.x * b.size.y > 12f || b.size.x < 0.05f || b.size.y < 0.05f) continue;
                LowProps.Add(b);
            }
        }
    }

    public static bool UnderLowProp(float x, float y)
    {
        foreach (Bounds b in LowProps)
            if (x >= b.min.x && x <= b.max.x && y >= b.min.y && y <= b.max.y) return true;
        return false;
    }

    // 上端の格子 c (その一つ上は壁) に壁の跡を一つ置く。wire なら剥がれたパネルと配線。
    private static void AddWallMark(RoomGrid g, int c, int order, bool wire)
    {
        (Wall Kind, int Weight)[] weights = WallWeights[g.Theme];
        int total = 0;
        foreach ((_, int w) in weights) total += w;
        int roll = FxMath.Range(0, total);
        Wall kind = wire ? Wall.Wire : weights[0].Kind;
        if (!wire)
        {
            foreach ((Wall k, int w) in weights)
            {
                if ((roll -= w) < 0) { kind = k; break; }
            }
        }

        int cx = c % g.W, cy = c / g.W;
        float x = g.X0 + (cx + FxMath.Value) * Step;
        float edge = g.Y0 + (cy + 1f) * Step;
        var p = new Placement { X = x, Z = g.Z - order * 0.0004f, A = FxMath.Range(0.8f, 0.95f), Tint = White };

        switch (kind)
        {
            case Wall.Hand:
                p.Kind = Kind.Hand;
                p.V = FxMath.Range(0, 2);
                p.W = FxMath.Range(0.28f, 0.36f);
                p.Y = edge + FxMath.Range(0.15f, 0.55f);
                p.Rot = FxMath.Range(-25f, 25f);
                break;
            case Wall.Scratch:
            case Wall.Tally:
                p.Kind = Kind.Scratch;
                p.V = kind == Wall.Tally ? 2 : FxMath.Range(0, 2);
                p.W = kind == Wall.Tally ? FxMath.Range(0.6f, 0.8f) : FxMath.Range(0.45f, 0.65f);
                p.Y = edge + FxMath.Range(0.2f, 0.5f);
                p.Rot = kind == Wall.Tally ? FxMath.Range(-6f, 6f) : FxMath.Range(-15f, 15f);
                break;
            case Wall.Web:
            {
                // 部屋の角 (左右どちらかが床でない上端) なら角の側へ張りつく向きに、壁の途中なら左右どちらかの向きで置く。
                bool leftOpen = cx == 0 || !g.In[c - 1];
                bool rightOpen = cx == g.W - 1 || !g.In[c + 1];
                bool right = rightOpen && !leftOpen || rightOpen == leftOpen && FxMath.Value < 0.5f;
                p.Kind = Kind.Web;
                p.V = FxMath.Range(0, 2);
                p.W = FxMath.Range(0.8f, 1.2f);
                // 絵は左上が角。右の角は時計回りに 90° 回すと右上が角になる。
                p.Rot = right ? 270f : 0f;
                float half = p.W * 0.5f;
                p.X = g.X0 + (right ? cx + 1 : cx) * Step + (right ? -half : half);
                p.Y = edge + 0.35f - half * 0.2f;
                p.A = FxMath.Range(0.65f, 0.85f);
                break;
            }
            case Wall.Spatter:
                p.Kind = Kind.Spatter;
                p.V = FxMath.Range(0, 2);
                p.W = FxMath.Range(0.5f, 0.8f);
                p.Y = edge + FxMath.Range(0.2f, 0.6f);
                p.Rot = FxMath.Range(0f, 360f);
                break;
            case Wall.Soot:
                // 煤は床の際から壁を這い上がる。絵は下端が床の際。
                p.Kind = Kind.Soot;
                p.V = FxMath.Range(0, 2);
                p.W = FxMath.Range(0.7f, 1.1f);
                p.H = p.W * 1.25f;
                p.Y = edge + p.H * 0.45f;
                p.Rot = 0f;
                p.A = FxMath.Range(0.65f, 0.85f);
                break;
            case Wall.Wire:
                // 穴は壁の絵に、垂れた配線の先は床の際まで届く。
                p.Kind = Kind.Wire;
                p.V = FxMath.Range(0, 2);
                p.W = FxMath.Range(0.45f, 0.6f);
                p.H = p.W / 0.6f;
                p.Y = edge + p.H * 0.43f;
                p.Rot = FxMath.Range(-4f, 4f);
                p.A = FxMath.Range(0.85f, 1f);
                break;
            default:
            {
                // 足跡の列: 床を歩いてきて、この上端 (壁) の手前で途切れる。絵は +x へ歩く向き。
                p.Kind = Kind.Feet;
                p.V = FxMath.Range(0, 2);
                p.W = FxMath.Range(1.3f, 1.7f);
                float ang = 90f + FxMath.Range(-30f, 30f);
                float rad = ang * (System.MathF.PI / 180f);
                float half = p.W * 0.5f;
                p.X = x - FxMath.Cos(rad) * half;
                p.Y = edge - FxMath.Sin(rad) * half;
                p.Rot = ang;
                // 歩き始めの床が部屋の外なら (細い通路の端など) 置かない。
                if (!g.Inside(x - FxMath.Cos(rad) * p.W, edge - FxMath.Sin(rad) * p.W)) return;
                break;
            }
        }

        if (!Footprint(p.X, p.Y, p.W * 0.3f, p.W * 0.3f, p.Rot, false)) return;
        Placements.Add(p);
    }

    // TestBridge から床の格子を画像 (PGM) に書き出す。白=床・灰=部屋の格子に入った床・黒=床でない。
    public static bool DebugDumpFloor(string path)
    {
        if (_fw == 0) return false;
        var px = new byte[_fw * _fh];
        for (int i = 0; i < px.Length; i++) px[i] = _floor[i] ? (byte)255 : (byte)0;
        foreach (RoomGrid g in Grids)
            foreach (int c in g.Cells)
                px[(c / g.W + g.Oy) * _fw + c % g.W + g.Ox] = 150;
        using var fs = System.IO.File.Create(path);
        byte[] head = System.Text.Encoding.ASCII.GetBytes($"P5 {_fw} {_fh} 255 ");
        fs.Write(head, 0, head.Length);
        for (int y = _fh - 1; y >= 0; y--) fs.Write(px, y * _fw, _fw);
        var owned = new int[_seedPts.Count];
        for (int i = 0; i < _floor.Length; i++)
            if (_floor[i]) owned[_label[i]]++;
        for (int k = 0; k < _seedPts.Count; k++)
            Logger.Info($"AirshipRuin floor seed ({_seedPts[k].X:0.##},{_seedPts[k].Y:0.##}) cells={owned[k]}{(_leaky[Find(k)] ? " dropped" : "")}", "AirshipLiminal");
        Logger.Info($"AirshipRuin floor dump origin=({_fx0},{_fy0}) step={Step} size={_fw}x{_fh} mask=0x{_wallMask:X}", "AirshipLiminal");
        return true;
    }

    public static void Clear()
    {
        Placements.Clear();
        Grids.Clear();
        LowProps.Clear();
        _fw = _fh = 0;
        _floor = [];
        _edge = [];
    }
}
