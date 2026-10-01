using System.Collections.Generic;
using UnityEngine;
using RoomGrid = EndKnot.Modules.MapAtmosphere.AirshipRuinLayout.RoomGrid;
using Theme = EndKnot.Modules.MapAtmosphere.AirshipRuinLayout.Theme;

namespace EndKnot.Modules.MapAtmosphere;

// 朽ちた船に湧いているもの (AirshipRuin が組み立て・毎フレーム更新・片付けを呼ぶ)。
//   虫   — 部屋の床を走っては止まり、向きを変えてまた走る。脚は 2 コマで交互に動かす。床の外 (壁・船の外) には出ない。
//   腐った食べ物 — 床に転がる塊の周りで、ウジがうねりながらゆっくり這い回る。台所に多い。
//   もや — 影の中をゆっくり漂う薄いもや。影の赤に染まって視界の外をぼやけさせる。
// 汚れと同じく layer 10 (影の中だけ) と layer 0 (破損で視界に入り込んだ時だけ) の二組で描く。
// 動きはゲームの状態と無関係に乱数で決めるので、他人の位置や出来事は何も映さない。
internal static class AirshipVermin
{
    private const string Res = "EndKnot.Resources.Images.MapAtmosphere.";
    private const int ShadowOnlyLayer = 10;

    private const float RoachLength = 0.18f;
    private const float MaggotLength = 0.07f;
    private const int MaggotsPerFood = 7;
    private const float MoveRange = 10f; // 描画を動かすのはカメラからこの距離まで (虫の歩きそのものは全部進める)

    // 床 10 平方単位あたりの虫 / 腐った食べ物の数
    private static readonly Dictionary<Theme, (float Roaches, int Foods)> Amounts = new()
    {
        [Theme.Rot] = (2.2f, 4),
        [Theme.Wet] = (1.2f, 1),
        [Theme.Clinic] = (0.7f, 0),
        [Theme.Grand] = (0.6f, 1),
        [Theme.Machine] = (0.7f, 0),
        [Theme.Hall] = (0.9f, 1)
    };

    private sealed class Critter
    {
        public SpriteRenderer Sh, Li;
        public Transform TSh, TLi;
        public RoomGrid Grid;
        public float X, Y, Z, Heading, Speed, Timer, Step, Phase, Radius, Angle, Dir, Scale;
        public int Frame;
        public bool Running;
        // 最後に置いた位置と向き (止まっている虫を毎フレーム同じ場所へ置き直さないため)
        public float PlacedX = float.NaN, PlacedY, PlacedHeading;
        public bool PlacedLit;
    }

    private static Transform _root;
    private static Critter[] _roaches = [], _maggots = [], _mists = [];
    private static SpriteRenderer[] _foodSh = [], _foodLi = [];
    private static Sprite[] _roachFrames;
    private static float _clock, _shownAlpha = -1f, _shownLit = -1f;

    public static void Build(Transform root, List<RoomGrid> grids)
    {
        _root = root;
        _roachFrames = [Utils.LoadSprite(Res + "ruin_roach0.png", 100f), Utils.LoadSprite(Res + "ruin_roach1.png", 100f)];
        Sprite maggot = Utils.LoadSprite(Res + "ruin_maggot.png", 100f);
        Sprite mist = Utils.LoadSprite(Res + "ruin_mist.png", 100f);
        Sprite[] foods = [Utils.LoadSprite(Res + "ruin_food0.png", 100f), Utils.LoadSprite(Res + "ruin_food1.png", 100f)];

        var roaches = new List<Critter>();
        var maggots = new List<Critter>();
        var mists = new List<Critter>();
        var foodSh = new List<SpriteRenderer>();
        var foodLi = new List<SpriteRenderer>();

        foreach (RoomGrid g in grids)
        {
            float area = g.Cells.Count * AirshipRuinLayout.Step * AirshipRuinLayout.Step;
            (float roachDensity, int foodCount) = Amounts[g.Theme];
            float z = g.Z - 0.05f;

            if (_roachFrames[0] && _roachFrames[1])
            {
                float scale = RoachLength / (_roachFrames[0].rect.width / 100f);
                int count = (int)System.MathF.Round(area / 10f * roachDensity);
                for (int k = 0; k < count; k++)
                {
                    Critter c = MakeCritter("Roach", _roachFrames[0], z - roaches.Count * 0.0003f);
                    c.Grid = g;
                    c.Scale = scale;
                    g.Pick(out c.X, out c.Y);
                    c.Heading = FxMath.Range(0f, 360f);
                    c.Timer = FxMath.Range(0.2f, 3f);
                    SetScale(c, scale, scale);
                    roaches.Add(c);
                }
            }

            for (int f = 0; f < foodCount; f++)
            {
                Sprite food = foods[FxMath.Range(0, 2)];
                if (!food) continue;
                g.Pick(out float x, out float y);
                float w = FxMath.Range(0.45f, 0.65f);
                float s = w / (food.rect.width / 100f);
                float rot = FxMath.Range(0f, 360f);
                foodSh.Add(MakeRenderer("Food", ShadowOnlyLayer, food, FxMath.V3(x, y, z + 0.01f), s, rot));
                foodLi.Add(MakeRenderer("FoodLit", 0, food, FxMath.V3(x, y, z + 0.01f), s, rot));

                if (!maggot) continue;
                float ms = MaggotLength / (maggot.rect.width / 100f);
                for (int k = 0; k < MaggotsPerFood; k++)
                {
                    Critter c = MakeCritter("Maggot", maggot, z - 0.02f - maggots.Count * 0.0003f);
                    c.X = x;
                    c.Y = y;
                    c.Scale = ms;
                    c.Radius = w * FxMath.Range(0.15f, 0.45f);
                    c.Angle = FxMath.Range(0f, 2f * FxMath.PI);
                    c.Dir = FxMath.Value < 0.5f ? -1f : 1f;
                    c.Phase = FxMath.Range(0f, 10f);
                    maggots.Add(c);
                }
            }

            if (!mist) continue;
            int mistCount = (int)FxMath.Clamp(area / 25f, 1f, 3f);
            for (int k = 0; k < mistCount; k++)
            {
                Critter c = MakeCritter("Mist", mist, z - 0.1f - mists.Count * 0.001f);
                g.Pick(out c.X, out c.Y);
                c.Scale = FxMath.Range(3.5f, 5f) / (mist.rect.width / 100f);
                c.Phase = FxMath.Range(0f, 10f);
                SetScale(c, c.Scale, c.Scale * 0.7f);
                Place(c, c.X, c.Y, 0f, true);
                mists.Add(c);
            }
        }

        _roaches = roaches.ToArray();
        _maggots = maggots.ToArray();
        _mists = mists.ToArray();
        _foodSh = foodSh.ToArray();
        _foodLi = foodLi.ToArray();
        _clock = 0f;
        _shownAlpha = _shownLit = -1f;
        Logger.Info($"AirshipVermin roaches={_roaches.Length} foods={_foodSh.Length} maggots={_maggots.Length} mists={_mists.Length}", "AirshipLiminal");
    }

    private static SpriteRenderer MakeRenderer(string name, int layer, Sprite sprite, Vector3 pos, float scale, float rot)
    {
        var go = new GameObject(name) { layer = layer };
        Transform tf = go.transform;
        tf.SetParent(_root, false);
        tf.position = pos;
        tf.localRotation = FxMath.RotZ(rot);
        tf.localScale = FxMath.V3(scale, scale, 1f);
        SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.color = default;
        return sr;
    }

    private static Critter MakeCritter(string name, Sprite sprite, float z)
    {
        var c = new Critter
        {
            Z = z,
            Sh = MakeRenderer(name, ShadowOnlyLayer, sprite, FxMath.V3(0f, 0f, z), 1f, 0f),
            Li = MakeRenderer(name + "Lit", 0, sprite, FxMath.V3(0f, 0f, z), 1f, 0f)
        };
        c.TSh = c.Sh.transform;
        c.TLi = c.Li.transform;
        return c;
    }

    // alpha: 溶け込み (0→1)。lit: 視界の中への侵食 (0→1)。camX/camY: カメラの位置。
    public static void Animate(float dt, float alpha, float lit, float camX, float camY)
    {
        if (!_root) return;
        _clock += dt;
        bool litOn = lit > 0f;

        if (alpha != _shownAlpha || lit != _shownLit)
        {
            _shownAlpha = alpha;
            _shownLit = lit;
            foreach (Critter c in _roaches) SetAlpha(c, alpha, lit);
            foreach (Critter c in _maggots) SetAlpha(c, alpha * 0.95f, lit);
            foreach (Critter c in _mists) SetAlpha(c, alpha, lit * 0.6f);
            for (int i = 0; i < _foodSh.Length; i++)
            {
                _foodSh[i].color = FxMath.Rgba(1f, 1f, 1f, alpha);
                _foodLi[i].color = FxMath.Rgba(1f, 1f, 1f, alpha * lit);
            }
        }

        if (alpha <= 0f) return;

        foreach (Critter c in _roaches) MoveRoach(c, dt, litOn, Near(c.X, c.Y, camX, camY));
        foreach (Critter c in _maggots)
            if (Near(c.X, c.Y, camX, camY)) MoveMaggot(c, dt, litOn);
        foreach (Critter c in _mists)
        {
            if (!Near(c.X, c.Y, camX, camY)) continue;
            // 数十秒かけて少しずつ流れる。
            float t = _clock * 0.06f + c.Phase;
            Place(c, c.X + 0.4f * FxMath.Sin(t), c.Y + 0.25f * FxMath.Cos(t * 1.3f), 0f, litOn);
        }
    }

    private static bool Near(float x, float y, float camX, float camY)
    {
        float dx = x - camX, dy = y - camY;
        return dx * dx + dy * dy < MoveRange * MoveRange;
    }

    private static void MoveRoach(Critter c, float dt, bool litOn, bool draw)
    {
        c.Timer -= dt;

        if (c.Running)
        {
            // 走りながら向きを小刻みに振る。床の外や壁の向こうへ出そうになったら、隣の床へ向き直る。
            c.Heading += FxMath.Range(-1f, 1f) * 240f * dt;
            float rad = c.Heading * (FxMath.PI / 180f);
            float nx = c.X + FxMath.Cos(rad) * c.Speed * dt;
            float ny = c.Y + FxMath.Sin(rad) * c.Speed * dt;

            if (c.Grid.Inside(nx, ny) && c.Grid.CanStep(c.X, c.Y, nx, ny))
            {
                c.X = nx;
                c.Y = ny;
            }
            else
            {
                c.Grid.PickNear(c.X, c.Y, out float tx, out float ty);
                c.Heading = FxMath.Atan2(ty - c.Y, tx - c.X) * (180f / FxMath.PI);
            }

            if (draw && (c.Step -= dt) <= 0f)
            {
                c.Step = 0.045f;
                c.Frame ^= 1;
                c.Sh.sprite = _roachFrames[c.Frame];
                if (litOn) c.Li.sprite = _roachFrames[c.Frame];
            }

            if (c.Timer <= 0f)
            {
                c.Running = false;
                c.Timer = FxMath.Range(0.3f, 2.8f);
            }
        }
        else if (c.Timer <= 0f)
        {
            c.Running = true;
            c.Timer = FxMath.Range(0.25f, 1.3f);
            c.Speed = FxMath.Range(1.1f, 2.3f);
            c.Heading += FxMath.Range(-110f, 110f);
        }

        if (draw) Place(c, c.X, c.Y, c.Heading, litOn);
    }

    private static void MoveMaggot(Critter c, float dt, bool litOn)
    {
        // 食べ物の周りを行きつ戻りつ回り、体を縮めては伸ばしてうねる。
        float t = _clock + c.Phase;
        c.Angle += c.Dir * dt * (0.25f + 0.2f * FxMath.Sin(t * 0.7f));
        float r = c.Radius * (0.85f + 0.2f * FxMath.Sin(t * 0.9f));
        float x = c.X + FxMath.Cos(c.Angle) * r;
        float y = c.Y + FxMath.Sin(c.Angle) * r * 0.6f;
        float heading = c.Angle * (180f / FxMath.PI) + 90f * c.Dir + 28f * FxMath.Sin(t * 5f);
        float squirm = FxMath.Sin(t * 8.5f);
        SetScale(c, c.Scale * (1f + 0.2f * squirm), c.Scale * (1f - 0.12f * squirm), litOn);
        Place(c, x, y, heading, litOn);
    }

    private static void Place(Critter c, float x, float y, float heading, bool litOn)
    {
        if (x == c.PlacedX && y == c.PlacedY && heading == c.PlacedHeading && (c.PlacedLit || !litOn)) return;
        c.PlacedX = x;
        c.PlacedY = y;
        c.PlacedHeading = heading;
        c.PlacedLit = litOn;
        Vector3 p = FxMath.V3(x, y, c.Z);
        Quaternion q = FxMath.RotZ(heading);
        c.TSh.position = p;
        c.TSh.rotation = q;
        if (!litOn) return;
        c.TLi.position = p;
        c.TLi.rotation = q;
    }

    private static void SetScale(Critter c, float sx, float sy, bool litOn = true)
    {
        Vector3 s = FxMath.V3(sx, sy, 1f);
        c.TSh.localScale = s;
        if (litOn) c.TLi.localScale = s;
    }

    private static void SetAlpha(Critter c, float alpha, float lit)
    {
        c.Sh.color = FxMath.Rgba(1f, 1f, 1f, alpha);
        c.Li.color = FxMath.Rgba(1f, 1f, 1f, alpha * lit);
    }

    public static void Teardown()
    {
        // 描画物は AirshipRuin のルートと一緒に壊れる。
        _root = null;
        _roaches = _maggots = _mists = [];
        _foodSh = _foodLi = [];
        _roachFrames = null;
    }
}
