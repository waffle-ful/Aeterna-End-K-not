using System.Collections.Generic;
using UnityEngine;

namespace EndKnot.Modules;

// ノヴァの演出。操縦を始めると足元に星座の陣が開き、☆ (CNO の文字) を光る星に描き直して持ち主の周りを追わせる。
// 撃った矢は手元で飛ばし (向き・速さ・射程は 1 回だけ届く)、矢の CNO の文字も隠して光の尾を引く流星に描き直す。
// 当たった相手には星形の刻印と爆発。CNO はホストが動かすので、☆は CNO の位置を毎フレーム追い、矢は届いた値から自分で進める。
public static partial class ExplosionFx
{
    private static readonly Color NovaWhite = new(1f, 1f, 1f);
    private static readonly Color NovaGold = new(1f, 0.82f, 0.3f);
    private static readonly Color NovaAmber = new(1f, 0.58f, 0.12f);
    private static readonly Color NovaViolet = new(0.62f, 0.26f, 1f);
    private static readonly Color NovaDeep = new(0.3f, 0.06f, 0.56f);
    private static readonly Color NovaCyan = new(0.55f, 0.9f, 1f);

    private static Sprite _starFive;
    private static Sprite _starFiveHalo;

    // Radius に詰める整数の上限 (float が整数を正確に持てる 2^24 未満)
    internal const float NovaPackMax = 16777215f;

    // ☆と矢の CNO の文字 (Nova の CNO と同じ書き方)。これを含む名前の CNO を手元で隠す
    private const string NovaStarMarker = "<#ffd700>☆";
    private const string NovaArrowMarker = "<#ff3030>→";

    private const float NovaTailMax = 3.4f;

    // ── ホストが呼ぶ ──────────────────────────────────────────

    // 本人の PlayerId + 1 + 32 × 操縦の秒数 (1〜60) + 2048 × 周回半径の段 (0.5〜5 を 0.25 刻みで 0〜18) + 65536 × 矢が壁を貫通するか
    public static float PackNovaRise(byte id, float duration, float orbitRadius, bool piercesWalls = false)
    {
        int sec = FxMath.Clamp((int)System.MathF.Round(duration), 1, 60);
        int orbit = FxMath.Clamp((int)System.MathF.Round((orbitRadius - 0.5f) / 0.25f), 0, 18);
        return id + 1 + 32 * sec + 2048 * orbit + (piercesWalls ? 65536 : 0);
    }

    // 撃った人の PlayerId + 1 + 32 × 向き (0〜359°) + 11520 × 速さの段 (3〜30 → 0〜27) + 322560 × 射程の段 (3〜30 → 0〜27)
    public static float PackNovaShot(byte id, float angleDeg, float speed, float range)
    {
        int ang = (int)System.MathF.Round(angleDeg) % 360;
        if (ang < 0) ang += 360;
        int spd = FxMath.Clamp((int)System.MathF.Round(speed) - 3, 0, 27);
        int rng = FxMath.Clamp((int)System.MathF.Round(range) - 3, 0, 27);
        return id + 1 + 32 * ang + 11520 * spd + 322560 * rng;
    }

    // 操縦を始めた: 足元の陣と☆ (視界の中だけ) + 必殺技のカットイン (帯と技名は全員・本人の姿は本人だけ)
    public static void NovaRise(PlayerControl pc, float duration, float orbitRadius, bool piercesWalls)
    {
        if (!pc) return;
        Play(Kind.NovaRise, pc.Pos(), PackNovaRise(pc.PlayerId, duration, orbitRadius, piercesWalls));
        CannonCutIn(pc.PlayerId, CannonTitle.Nova, CannonPalette.Nova);
    }

    // 矢は CNO 自体が全員に見えるので、流星も壁越しに見せる (撃った瞬間の☆の光は視界の中だけ)
    public static void NovaShot(byte id, Vector2 origin, float angleDeg, float speed, float range)
    {
        Play(Kind.NovaShot, origin, PackNovaShot(id, angleDeg, speed, range), Seen.Everyone);
    }

    public static void NovaHit(byte id, Vector2 at, bool killed, bool stops)
    {
        Play(Kind.NovaHit, at, id + 1 + (killed ? 32 : 0) + (stops ? 64 : 0));
    }

    public static void NovaDown(byte id, Vector2 at)
    {
        Play(Kind.NovaDown, at, id + 1);
    }

    // ── 受け取って描く ────────────────────────────────────────

    private static void SpawnNova(Request r)
    {
        int v = (int)(r.Radius + 0.5f);
        int id = v % 32 - 1;

        switch (r.Kind)
        {
            case Kind.NovaRise:
            {
                PlayerControl pc = id is >= 0 and <= 30 ? Utils.GetPlayerById((byte)id) : null;
                if (id is >= 0 and < 32) NovaPiercesWalls[id] = (v & 65536) != 0;
                StartNovaAura(r.Pos, id, (v / 32) % 64, 0.5f + (v / 2048 % 32) * 0.25f);
                if (pc && !Unseen(pc, (byte)id)) FxSound.At("FxNovaRise", r.Pos);
                break;
            }
            case Kind.NovaShot:
            {
                int ang = (v / 32) % 360;
                float speed = 3f + (v / 11520) % 28;
                float range = 3f + v / 322560;
                StartNovaComet(r.Pos, id, ang, speed, range);
                FxSound.At("FxNovaShot", r.Pos, 1f, farGain: 0.25f);
                break;
            }
            case Kind.NovaHit:
            {
                bool killed = (v & 32) != 0;
                bool stops = (v & 64) != 0;
                if (stops) StopNearestComet(id, r.Pos);
                if (killed)
                {
                    SpawnNovaHit(r.Pos);
                    FxSound.At("FxNovaHit", r.Pos, 1f, farGain: 0.2f);
                }
                else
                {
                    SpawnNovaDeflect(r.Pos);
                    FxSound.At("FxNovaFizzle", r.Pos, 0.8f);
                }

                break;
            }
            case Kind.NovaDown:
                EndNovaAura(id);
                break;
        }
    }

    // ── 手元で動かし続ける絵 ──────────────────────────────────
    // 粒 (Add) は出したら動かせないので、☆と矢の頭・尾は自前の SpriteRenderer を持ち、毎フレーム位置を書く

    private sealed class NovaSprite
    {
        public GameObject Go;
        public SpriteRenderer Sr;
        public Transform Tf;
        public bool Flat;
        public int Order;
        public float Aspect = 1f;
        public bool InShadow;
    }

    private static NovaSprite NewNovaSprite(Sprite sprite, int order, bool flat = false, float aspect = 1f)
    {
        var go = new GameObject("NovaFx") { layer = 0 };
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.sortingOrder = SortingOrder + order;
        sr.color = FxMath.Rgba(1f, 1f, 1f, 0f);
        Transform tf = go.transform;

        if (flat)
        {
            if (!_flatRoot)
            {
                var root = new GameObject("ExplosionFxFlat");
                root.transform.localScale = FxMath.V3(1f, 0.35f, 1f);
                _flatRoot = root.transform;
            }

            tf.SetParent(_flatRoot, false);
        }

        return new NovaSprite { Go = go, Sr = sr, Tf = tf, Flat = flat, Order = order, Aspect = aspect };
    }

    // shadow = 視界の外で隠す時の影の板 (null = 隠さない)
    private static void PlaceNova(NovaSprite s, MeshRenderer shadow, Vector2 pos, float sx, float sy, float rot, Color col, float alpha)
    {
        if (s == null || !s.Go) return;

        bool inShadow = shadow;
        if (inShadow != s.InShadow)
        {
            s.InShadow = inShadow;
            if (inShadow)
            {
                s.Sr.sortingLayerID = shadow.sortingLayerID;
                s.Sr.sortingOrder = shadow.sortingOrder;
            }
            else
            {
                s.Sr.sortingLayerID = 0;
                s.Sr.sortingOrder = SortingOrder + s.Order;
            }
        }

        float z = !inShadow ? 0f : s.Flat ? pos.y / 1000f + 0.01f : -1f - s.Order * 0.01f;
        s.Tf.position = FxMath.V3(pos.x, pos.y, z);
        s.Tf.localScale = FxMath.V3(sx, sy * s.Aspect, 1f);
        s.Tf.localRotation = FxMath.RotZ(rot);
        s.Sr.color = FxMath.Rgba(col.r, col.g, col.b, alpha);
    }

    private static void HideNova(NovaSprite s)
    {
        if (s != null && s.Go) s.Sr.color = FxMath.Rgba(1f, 1f, 1f, 0f);
    }

    private static void DestroyNova(NovaSprite s)
    {
        if (s != null && s.Go) Object.Destroy(s.Go);
    }

    private sealed class NovaAura
    {
        public byte Id;
        public PlayerControl Pc;
        public bool Vision;
        public float Start, Until, Orbit;
        public CnoCover Cover;
        public Transform StarTf;
        public byte StarTfId;
        public Vector2 Star;
        public bool StarKnown;
        public Vector2 Origin;
        public int OrbitLine;
        public NovaSprite FloorOuter, FloorInner, Aim, StarHalo, StarGlow, StarBody, StarFlare, StarCore;
        public float NextDust, NextRim, NextPulse;
        public float DyingFrom = -1f, DyingFade;
    }

    private sealed class NovaComet
    {
        public byte Id;
        public Vector2 Pos, Dir;
        public float Angle, Speed, Left, Travelled, Age;
        public CnoCover Cover;
        public NovaSprite TailOuter, TailMid, TailCore, HeadHalo, HeadGlow, HeadBody, HeadFlare, HeadCore;
        public float NextDust, Helix, NextTrail;
        public Vector2 LastTrail;
        public float DyingFrom = -1f, DyingFade;
    }

    private static readonly List<NovaAura> NovaAuras = [];
    private static readonly List<NovaComet> NovaComets = [];

    // 矢が壁を貫通する設定か (人ごと)。矢の知らせには空きが無いので、操縦の始まりの知らせで受け取っておく
    private static readonly bool[] NovaPiercesWalls = new bool[32];

    // 演出が終わった後も少しの間だけ CNO を隠し続ける (☆は片付け、矢は退避座標への TP がこの後に届く。先に戻すと一瞬文字が見える)
    private static readonly List<(CnoCover Cover, float At)> NovaUncovers = [];

    // マップ外で待っている矢の CNO (撃つ時に☆の位置へ TP される)。撃った瞬間に先に隠しておき、流星が見つけた所で引き渡す。
    // 流星は TP が届いてから CNO を探すので、隠さないと見つけるまでの数フレーム文字が見える (ホストでは向きの差し替えが名前を出し直す)
    private static readonly List<CnoCover> NovaParked = [];

    // Nova.ArrowParkPos と同じ座標
    private static readonly Vector2 NovaParkPos = new(50f, 50f);

    private static bool NovaBusy => NovaAuras.Count > 0 || NovaComets.Count > 0 || NovaUncovers.Count > 0 || NovaParked.Count > 0;

    private static void StopAllNova()
    {
        foreach (CnoCover cover in NovaParked) UncoverCno(cover);
        NovaParked.Clear();
        foreach (NovaAura a in NovaAuras) ReleaseAura(a, true);
        foreach (NovaComet c in NovaComets) ReleaseComet(c, true);
        foreach ((CnoCover cover, _) in NovaUncovers) UncoverCno(cover);
        NovaAuras.Clear();
        NovaComets.Clear();
        NovaUncovers.Clear();
    }

    private static void ReleaseAura(NovaAura a, bool uncoverNow)
    {
        FxLines.Stop(a.OrbitLine, 0f);
        DestroyNova(a.FloorOuter);
        DestroyNova(a.FloorInner);
        DestroyNova(a.Aim);
        DestroyNova(a.StarHalo);
        DestroyNova(a.StarGlow);
        DestroyNova(a.StarBody);
        DestroyNova(a.StarFlare);
        DestroyNova(a.StarCore);
        Logger.Info($"NOVADIAG releaseAura id={a.Cover.Id} has={(bool)a.Cover.Name} now={uncoverNow} f={Time.frameCount}", "ExplosionFx"); // NOVADIAG
        if (uncoverNow) UncoverCno(a.Cover);
        else if (a.Cover.Name) NovaUncovers.Add((a.Cover, Time.time + 0.8f));
    }

    private static void ReleaseComet(NovaComet c, bool uncoverNow)
    {
        DestroyNova(c.TailOuter);
        DestroyNova(c.TailMid);
        DestroyNova(c.TailCore);
        DestroyNova(c.HeadHalo);
        DestroyNova(c.HeadGlow);
        DestroyNova(c.HeadBody);
        DestroyNova(c.HeadFlare);
        DestroyNova(c.HeadCore);
        Logger.Info($"NOVADIAG releaseComet id={c.Cover.Id} has={(bool)c.Cover.Name} now={uncoverNow} pos={c.Pos} f={Time.frameCount}", "ExplosionFx"); // NOVADIAG
        if (uncoverNow) UncoverCno(c.Cover);
        // 流星が壁で止まっても、ホストの矢は壁の継ぎ目を抜けて飛び続けることがある。射程の残りを飛び切る時間までは文字を隠しておく
        else if (c.Cover.Name) NovaUncovers.Add((c.Cover, Time.time + 0.6f + FxMath.Max(c.Left, 0f) / FxMath.Max(c.Speed, 1f)));
    }

    // ── 変身: 星座の陣が描かれ、星が弾けて光の柱が立つ ──

    private static readonly Vector2[] NovaPts = new Vector2[8];

    private static void StartNovaAura(Vector2 c, int id, int seconds, float orbit)
    {
        PlayerControl pc = id is >= 0 and <= 30 ? Utils.GetPlayerById((byte)id) : null;

        // 同じ人の前の操縦が残っていたら畳む (操縦の終わりの合図は落ちることがある)
        for (int i = NovaAuras.Count - 1; i >= 0; i--)
        {
            if (NovaAuras[i].Id != id) continue;
            ReleaseAura(NovaAuras[i], false);
            NovaAuras.RemoveAt(i);
        }

        // 透明・ベントの中で始めた時は、居場所が分かる光と音を出さない (続けて描く分も見えない間は隠す)
        if (!pc) return;
        if (!Unseen(pc, (byte)id)) SpawnNovaRiseBurst(c);

        float now = Time.time;
        var a = new NovaAura
        {
            Id = (byte)id, Pc = pc, Vision = _vision, Start = now, Until = now + FxMath.Max(seconds, 1) + 1.5f, Orbit = orbit, Origin = c,
            FloorOuter = NewNovaSprite(_sigil, 0, true),
            FloorInner = NewNovaSprite(_sigilInner, 0, true),
            Aim = NewNovaSprite(_ray, 5, aspect: RayAspect),
            StarHalo = NewNovaSprite(_starFiveHalo, 6),
            StarGlow = NewNovaSprite(_glow, 7),
            StarBody = NewNovaSprite(_starFive, 9),
            StarFlare = NewNovaSprite(_star, 10),
            StarCore = NewNovaSprite(_glow, 11),
            NextPulse = now + 0.6f
        };

        // 周回の軌道: 本人を中心に☆が回る円
        const int n = 48;
        for (int k = 0; k < n; k++)
        {
            float ang = k * 2f * FxMath.PI / n;
            LineBuf[k] = Off(c, FxMath.Cos(ang) * orbit, FxMath.Sin(ang) * orbit);
        }

        a.OrbitLine = LinesHeld(LineBuf, n, new FxLines.Spec
        {
            Width = 0.018f, Taper = FxLines.Taper.Even, Core = NovaGold, Under = NovaViolet, CoreAlpha = 0.7f, UnderAlpha = 0.45f, UnderMul = 5f, UnderGlow = true, CoreGlow = true,
            FadeIn = 0.5f, Loop = true
        }, 4);

        NovaAuras.Add(a);
    }

    private static void SpawnNovaRiseBurst(Vector2 c)
    {
        const float pop = 0.38f;
        float q = Active.Count > 1200 ? 0.5f : 1f;
        Vector2 feet = Off(c, 0f, -0.38f);

        // 溜め: 紫の光が周りから本人へ吸い込まれる
        for (int i = 0; i < 12 * q; i++)
        {
            Vector2 d = Dir();
            float rr = Rnd(1.4f, 2f);
            Vector2 st = Off(c, d.x * rr, d.y * rr * 0.8f);
            float life = pop - Rnd(0f, 0.12f);
            Add(Shape.Glow, st, FxMath.V2((c.x - st.x) / life, (c.y - st.y) / life), life, 0.32f, 0.08f, NovaViolet, NovaCyan, 0.8f, 0.2f, 0.85f, delay: pop - life, stretch: 0.05f, order: 7);
        }

        // 星座: 五芒星の頂点に星が灯り、一筆書きの線が走る
        for (int k = 0; k < 5; k++)
        {
            float ang = (90f + 72f * k) * FxMath.Deg2Rad;
            NovaPts[k] = Off(c, FxMath.Cos(ang) * 0.95f, FxMath.Sin(ang) * 0.95f + 0.05f);
            Add(Shape.Star, NovaPts[k], Vector2.zero, 0.55f, 0.42f, 0.18f, NovaWhite, NovaGold, 1f, 0.05f, 0.6f, delay: k * 0.05f, rot: 0f, twinkle: 0.4f, twinkleSpeed: 22f, order: 10);
        }

        for (int k = 0; k <= 5; k++) LineBuf[k] = NovaPts[k * 2 % 5];
        Lines(LineBuf, 6, new FxLines.Spec
        {
            Width = 0.035f, Taper = FxLines.Taper.Even, Core = NovaWhite, Under = NovaGold, CoreAlpha = 1f, UnderAlpha = 0.7f, UnderMul = 4f, UnderGlow = true, CoreGlow = true,
            Grow = pop - 0.06f, Life = pop + 0.35f, FadeOutFrom = 0.7f
        }, 8);

        // 弾ける: 金の五芒星が膨らんで回り、白い閃光と光の柱、床を走る輪
        Add(Shape.StarFiveHalo, c, Vector2.zero, 0.6f, 1.2f, 3.4f, NovaViolet, NovaDeep, 0.7f, 0.02f, 0.4f, delay: pop, spin: 90f, rot: 0f, order: 6);
        Add(Shape.StarFive, c, Vector2.zero, 0.55f, 0.4f, 2.3f, NovaWhite, NovaGold, 1f, 0.01f, 0.45f, delay: pop, spin: 220f, rot: 0f, order: 9);
        Add(Shape.Glow, c, Vector2.zero, 0.35f, 0.6f, 3.2f, NovaWhite, NovaGold, 1f, 0.01f, 0.3f, delay: pop, order: 10);
        Add(Shape.Ring, feet, Vector2.zero, 0.6f, 0.6f, 6.5f, NovaWhite, NovaGold, 0.9f, 0.01f, 0.35f, delay: pop, rot: 0f, flat: true, order: 2);
        Add(Shape.Ring, feet, Vector2.zero, 0.75f, 0.4f, 4.5f, NovaGold, NovaViolet, 0.7f, 0.01f, 0.35f, delay: pop + 0.08f, rot: 0f, flat: true, order: 2);
        Add(Shape.Ray, feet, Vector2.zero, 0.65f, 0.3f, 4.2f, NovaGold, NovaViolet, 0.75f, 0.02f, 0.4f, delay: pop, rot: 90f, sy0: 1.3f, sy1: 0.7f, order: 5);
        Add(Shape.Ray, feet, Vector2.zero, 0.55f, 0.3f, 3.6f, NovaWhite, NovaGold, 1f, 0.02f, 0.4f, delay: pop, rot: 90f, sy0: 0.45f, sy1: 0.2f, order: 8);

        for (int k = 0; k < 12; k++)
        {
            Add(Shape.Ray, c, Vector2.zero, 0.32f, 0.3f, 2f, NovaWhite, NovaGold, 0.9f, 0.01f, 0.3f, delay: pop, rot: k * 30f + 15f, sy0: 0.12f, sy1: 0.04f, order: 8);
        }

        // 螺旋に昇る火花
        for (int k = 0; k < 24 * q; k++)
        {
            float ang = k * 30f * FxMath.Deg2Rad;
            Vector2 st = Off(c, FxMath.Cos(ang) * 0.9f, FxMath.Sin(ang) * 0.3f - 0.35f);
            Add(Shape.Star, st, FxMath.V2(-FxMath.Sin(ang) * 1.6f, 1.5f + Rnd(0f, 0.6f)), Rnd(0.55f, 0.8f), Rnd(0.14f, 0.22f), 0.03f, NovaWhite, k % 2 == 0 ? NovaGold : NovaViolet, 1f, 0.05f, 0.55f,
                drag: 1.2f, delay: pop + k * 0.012f, stretch: 0.05f, twinkle: 0.4f, twinkleSpeed: 18f, order: 9);
        }

        Particles(FxParticles.Preset.Sparks, c, (int)(30 * q), NovaWhite, NovaGold, delay: pop, order: 9);
        Impact(c, 3f, NovaGold, 0.22f, 0.1f, 0.25f, delay: pop);
    }

    // ── ☆が消える: 光が中心へ吸い込まれて小さく弾ける ──

    private static void EndNovaAura(int id)
    {
        for (int i = NovaAuras.Count - 1; i >= 0; i--)
        {
            NovaAura a = NovaAuras[i];
            if (a.Id != id || a.DyingFrom >= 0f) continue;

            BeginAuraFade(a, 0.3f);
            if (!a.StarKnown || !a.Pc || Unseen(a.Pc, a.Id)) continue;

            _vision = a.Vision;
            Vector2 s = a.Star;
            for (int k = 0; k < 8; k++)
            {
                float ang = k * 45f * FxMath.Deg2Rad;
                Vector2 st = Off(s, FxMath.Cos(ang) * 0.9f, FxMath.Sin(ang) * 0.9f);
                Add(Shape.Star, st, FxMath.V2((s.x - st.x) / 0.22f, (s.y - st.y) / 0.22f), 0.22f, 0.2f, 0.06f, NovaWhite, NovaGold, 1f, 0.05f, 0.8f, stretch: 0.05f, order: 10);
            }

            Add(Shape.Ring, s, Vector2.zero, 0.3f, 0.2f, 1.3f, NovaWhite, NovaViolet, 0.8f, 0.01f, 0.3f, delay: 0.22f, order: 8);
            Add(Shape.Glow, s, Vector2.zero, 0.25f, 0.9f, 0.2f, NovaWhite, NovaGold, 0.9f, 0.01f, 0.3f, delay: 0.22f, order: 9);
            bool keep = FxSound.NeedSight;
            FxSound.NeedSight = a.Vision;
            try { FxSound.At("FxNovaDown", s, 0.7f); }
            finally { FxSound.NeedSight = keep; }
            _vision = false;
            return;
        }
    }

    private static void BeginAuraFade(NovaAura a, float seconds)
    {
        if (a.DyingFrom >= 0f) return;
        a.DyingFrom = Time.time;
        a.DyingFade = seconds;
        FxLines.Stop(a.OrbitLine, seconds);
        a.OrbitLine = 0;
    }

    // ── 矢: ☆から流星が飛ぶ ──

    private static void StartNovaComet(Vector2 origin, int id, int angleDeg, float speed, float range)
    {
        float rad = angleDeg * FxMath.Deg2Rad;
        var dir = FxMath.V2(FxMath.Cos(rad), FxMath.Sin(rad));

        // 撃った瞬間の☆の閃光は視界の中だけ (☆の位置は持ち主のすぐそば)
        bool keepVision = _vision, keepNear = _nearFlashOnly;
        _vision = true;
        _nearFlashOnly = true;
        SpawnNovaMuzzle(origin, dir, angleDeg);
        _vision = keepVision;
        _nearFlashOnly = keepNear;

        if (NovaComets.Count >= 10) return;

        HoldParkedArrows();

        NovaComets.Add(new NovaComet
        {
            Id = (byte)FxMath.Max(id, 0), Pos = origin, Dir = dir, Angle = angleDeg, Speed = speed, Left = range, LastTrail = origin,
            TailOuter = NewNovaSprite(_ray, 5, aspect: RayAspect),
            TailMid = NewNovaSprite(_ray, 6, aspect: RayAspect),
            TailCore = NewNovaSprite(_ray, 7, aspect: RayAspect),
            HeadHalo = NewNovaSprite(_starFiveHalo, 6),
            HeadGlow = NewNovaSprite(_glow, 8),
            HeadBody = NewNovaSprite(_starFive, 9),
            HeadFlare = NewNovaSprite(_star, 10),
            HeadCore = NewNovaSprite(_glow, 11)
        });
    }

    private static void SpawnNovaMuzzle(Vector2 o, Vector2 dir, float angleDeg)
    {
        Add(Shape.Glow, o, Vector2.zero, 0.3f, 0.5f, 2.2f, NovaWhite, NovaGold, 1f, 0.01f, 0.3f, order: 10);
        Add(Shape.StarFive, o, Vector2.zero, 0.3f, 1.3f, 0.5f, NovaWhite, NovaGold, 1f, 0.01f, 0.4f, spin: 300f, rot: angleDeg - 90f, order: 11);
        Add(Shape.Star, o, Vector2.zero, 0.22f, 2.4f, 0.6f, NovaWhite, NovaGold, 1f, 0.01f, 0.3f, rot: angleDeg, order: 12);

        for (int k = 0; k < 8; k++)
        {
            Add(Shape.Ray, o, Vector2.zero, 0.25f, 0.3f, 1.7f, NovaWhite, NovaGold, 0.9f, 0.01f, 0.3f, rot: angleDeg + k * 45f + 22.5f, sy0: 0.1f, sy1: 0.03f, order: 9);
        }

        // 音速の輪: 撃った向きへ 3 枚、進む向きに薄く横に広い楕円
        for (int j = 0; j < 3; j++)
        {
            float at = 0.45f + j * 0.55f;
            Add(Shape.Ring, Off(o, dir.x * at, dir.y * at), FxMath.V2(dir.x * 1.2f, dir.y * 1.2f), 0.32f, 0.12f, 0.3f, NovaWhite, NovaViolet, 0.9f, 0.01f, 0.3f,
                delay: j * 0.04f, rot: angleDeg, sy0: 0.5f + j * 0.15f, sy1: 1.5f + j * 0.35f, order: 8);
        }

        Particles(FxParticles.Preset.Sparks, o, 16, NovaWhite, NovaGold, size: 0.7f, order: 10, angle: angleDeg);
        Impact(o, 2f, NovaGold, 0.12f, 0.06f, 0.15f);
    }

    // 命中で止まる矢: 同じ人の矢のうち当たった所にいちばん近いものを止める
    private static void StopNearestComet(int id, Vector2 at)
    {
        int best = -1;
        float bestD = float.MaxValue;

        for (int i = 0; i < NovaComets.Count; i++)
        {
            NovaComet c = NovaComets[i];
            if (c.Id != id || c.DyingFrom >= 0f) continue;

            float dx = c.Pos.x - at.x, dy = c.Pos.y - at.y;
            float d = dx * dx + dy * dy;
            float reach = c.Speed * 0.5f + 2f;
            if (d > reach * reach || d >= bestD) continue;

            best = i;
            bestD = d;
        }

        if (best < 0) return;

        NovaComet hit = NovaComets[best];
        hit.Pos = at;
        hit.DyingFrom = Time.time;
        hit.DyingFade = 0.08f;
    }

    // ── 命中: 星形の刻印が回って砕け、紫と金の大爆発 ──

    private static void SpawnNovaHit(Vector2 c)
    {
        float q = Active.Count > 1200 ? 0.5f : 1f;

        Add(Shape.Glow, c, Vector2.zero, 0.3f, 0.8f, 3.6f, NovaWhite, NovaGold, 1f, 0.01f, 0.25f, order: 12);
        Add(Shape.Star, c, Vector2.zero, 0.3f, 4f, 0.8f, NovaWhite, NovaGold, 1f, 0.01f, 0.3f, rot: 0f, order: 13);
        Add(Shape.Star, c, Vector2.zero, 0.3f, 3f, 0.6f, NovaWhite, NovaViolet, 0.9f, 0.01f, 0.3f, rot: 45f, order: 13);

        // 刻印: 大きな五芒星が叩きつけられて縮みながら締まり (白 → 金 → 琥珀)、0.42 秒で砕ける
        const float shatter = 0.42f;
        Add(Shape.StarFiveHalo, c, Vector2.zero, shatter + 0.25f, 4.6f, 3f, NovaViolet, NovaDeep, 0.85f, 0.01f, 0.6f, spin: 30f, rot: -12f, order: 7);
        Add(Shape.StarFive, c, Vector2.zero, shatter + 0.06f, 3.6f, 2.3f, NovaWhite, NovaAmber, 1f, 0.01f, 0.8f, spin: 28f, rot: -12f, colorMid: NovaGold, order: 10);
        Add(Shape.StarFive, c, Vector2.zero, shatter + 0.06f, 1.6f, 1f, NovaWhite, NovaWhite, 0.9f, 0.01f, 0.6f, spin: 28f, rot: -12f, order: 11);
        Add(Shape.Glow, c, Vector2.zero, 0.3f, 1f, 3.4f, NovaWhite, NovaGold, 1f, 0.01f, 0.3f, delay: shatter, order: 12);
        Add(Shape.Ring, c, Vector2.zero, 0.4f, 1.2f, 5.5f, NovaWhite, NovaViolet, 0.9f, 0.01f, 0.35f, delay: shatter, order: 9);
        Impact(c, 4f, NovaGold, 0.25f, 0.18f, 0.3f, delay: shatter);

        // 爆発: 暗い紫の煙を下に敷き、金の炎と光を重ねる
        for (int i = 0; i < 14 * q; i++)
        {
            Vector2 d = Dir();
            float sp = Rnd(1.5f, 3.2f);
            Add(Shape.Cloud, Off(c, d.x * 0.2f, d.y * 0.2f), FxMath.V2(d.x * sp, d.y * sp * 0.8f), Rnd(0.8f, 1.2f), Rnd(0.5f, 0.8f), Rnd(1.4f, 2f), NovaDeep, NovaViolet, 0.6f, 0.05f, 0.4f,
                drag: 3f, delay: 0.05f, rise: 0.2f, order: 5);
        }

        for (int i = 0; i < 12 * q; i++)
        {
            Vector2 d = Dir();
            float sp = Rnd(2f, 4f);
            Add(Shape.Flame, Off(c, d.x * 0.15f, d.y * 0.15f), FxMath.V2(d.x * sp, d.y * sp * 0.8f), Rnd(0.4f, 0.65f), Rnd(0.5f, 0.8f), Rnd(1f, 1.4f), NovaWhite, NovaAmber, 0.95f, 0.02f, 0.4f,
                drag: 3.5f, delay: 0.05f, colorMid: NovaGold, order: 8);
        }

        Add(Shape.Ring, c, Vector2.zero, 0.45f, 0.5f, 5f, NovaWhite, NovaGold, 0.95f, 0.01f, 0.35f, order: 9);
        Add(Shape.Ring, c, Vector2.zero, 0.6f, 0.5f, 7f, NovaViolet, NovaDeep, 0.75f, 0.01f, 0.35f, delay: 0.08f, order: 8);

        for (int k = 0; k < 12; k++)
        {
            Add(Shape.Ray, c, Vector2.zero, 0.35f, 0.4f, Rnd(2f, 3f), NovaWhite, NovaGold, 0.95f, 0.01f, 0.3f, rot: k * 30f + Rnd(-8f, 8f), sy0: 0.16f, sy1: 0.04f, order: 9);
        }

        // 星の欠片がきらめきながら飛び散る
        for (int i = 0; i < 18 * q; i++)
        {
            Vector2 d = Dir();
            float sp = Rnd(2.5f, 5f);
            Add(Shape.StarFive, Off(c, d.x * 0.6f, d.y * 0.6f), FxMath.V2(d.x * sp, d.y * sp), Rnd(0.6f, 1f), Rnd(0.22f, 0.36f), 0.05f, NovaWhite, i % 3 == 0 ? NovaViolet : NovaGold, 1f, 0.02f, 0.6f,
                drag: 2.5f, delay: shatter, spin: Rnd(-540f, 540f), twinkle: 0.5f, twinkleSpeed: 20f, order: 11);
        }

        Particles(FxParticles.Preset.Sparks, c, (int)(40 * q), NovaWhite, NovaGold, order: 10);
        Add(Shape.FloorPool, Off(c, 0f, -0.35f), Vector2.zero, 1.4f, 2.6f, 3.2f, NovaAmber, NovaViolet, 0.4f, 0.05f, 0.4f, rot: 0f, flat: true, order: 0);
        Impact(c, 6f, NovaWhite, 0.4f, 0.28f, 0.45f);
    }

    // 守られて倒せなかった: 星が弾かれて火花が散るだけ
    private static void SpawnNovaDeflect(Vector2 c)
    {
        Add(Shape.StarFive, c, Vector2.zero, 0.3f, 1f, 0.4f, NovaWhite, NovaGold, 1f, 0.01f, 0.4f, spin: 400f, rot: 0f, order: 10);
        Add(Shape.Ring, c, Vector2.zero, 0.35f, 0.3f, 1.8f, NovaWhite, NovaViolet, 0.8f, 0.01f, 0.35f, order: 8);
        Particles(FxParticles.Preset.Sparks, c, 14, NovaWhite, NovaGold, size: 0.7f, order: 10);
    }

    private static void SpawnNovaWallBurst(Vector2 c, Vector2 dir, float angleDeg)
    {
        Add(Shape.StarFive, c, Vector2.zero, 0.25f, 1f, 0.3f, NovaWhite, NovaGold, 1f, 0.01f, 0.4f, spin: 400f, rot: 0f, order: 10);
        Add(Shape.Glow, c, Vector2.zero, 0.25f, 0.4f, 1.6f, NovaWhite, NovaGold, 0.9f, 0.01f, 0.3f, order: 9);
        Add(Shape.Ring, c, Vector2.zero, 0.3f, 0.2f, 1.5f, NovaWhite, NovaViolet, 0.8f, 0.01f, 0.35f, rot: angleDeg, sy0: 0.4f, sy1: 2f, order: 8);
        Particles(FxParticles.Preset.Sparks, Off(c, -dir.x * 0.1f, -dir.y * 0.1f), 18, NovaWhite, NovaGold, size: 0.7f, order: 10, angle: angleDeg + 180f);
    }

    // 射程の端: 流星がほどけて星屑になる
    private static void SpawnNovaDissolve(Vector2 c, Vector2 dir)
    {
        for (int i = 0; i < 14; i++)
        {
            Vector2 d = Dir();
            float sp = Rnd(0.4f, 1.4f);
            Add(Shape.Star, Off(c, d.x * 0.15f, d.y * 0.15f), FxMath.V2(d.x * sp + dir.x * 1.5f, d.y * sp + dir.y * 1.5f), Rnd(0.5f, 0.9f), Rnd(0.12f, 0.2f), 0.02f,
                NovaWhite, i % 2 == 0 ? NovaGold : NovaViolet, 1f, 0.05f, 0.5f, drag: 2f, twinkle: 0.6f, twinkleSpeed: 16f, order: 10);
        }

        Add(Shape.Glow, c, Vector2.zero, 0.3f, 1f, 0.3f, NovaGold, NovaViolet, 0.8f, 0.01f, 0.3f, order: 9);
    }

    // ほかのノヴァの演出がもう隠している CNO を除いて探す (☆も矢も同じ印なので、2 人が近いと取り違える)
    private static bool CoverNova(ref CnoCover cover, NovaAura selfAura, NovaComet selfComet, Vector2 at, float within, string marker)
    {
        if (!cover.Name)
        {
            foreach (NovaAura a in NovaAuras)
                if (a != selfAura && a.Cover.Name) CoverTaken.Add(a.Cover.Id);
            foreach (NovaComet c in NovaComets)
                if (c != selfComet && c.Cover.Name) CoverTaken.Add(c.Cover.Id);
        }

        bool had = cover.Name; // NOVADIAG
        try { return CoverCno(ref cover, at, within, marker, true); }
        finally
        {
            CoverTaken.Clear();
            if (had != (bool)cover.Name) Logger.Info($"NOVADIAG {(cover.Name ? "cover" : "lost")} {(selfAura != null ? "aura" : "comet")} id={cover.Id} f={Time.frameCount} at={at}", "ExplosionFx"); // NOVADIAG
        }
    }

    // 退避座標にいる矢の CNO を全部隠す (どれが撃たれたかは手元では分からない)
    private static void HoldParkedArrows()
    {
        try
        {
            var all = Object.FindObjectsOfType<PlayerControl>();
            for (int i = 0; i < all.Length; i++)
            {
                PlayerControl pc = all[i];
                if (!pc || pc.PlayerId < 200) continue;

                Vector3 p = pc.transform.position;
                float dx = p.x - NovaParkPos.x, dy = p.y - NovaParkPos.y;
                if (dx * dx + dy * dy > 9f) continue;

                bool held = false;
                foreach (CnoCover h in NovaParked)
                    if (h.Id == pc.PlayerId) held = true;
                if (held) continue;

                Transform names = pc.transform.FindChild("Names");
                Transform t = names ? names.FindChild("NameText_TMP") : null;
                TMPro.TextMeshPro tmp = t ? t.GetComponent<TMPro.TextMeshPro>() : null;
                string text = tmp ? tmp.text : null;
                if (text == null || !text.Contains(NovaArrowMarker, System.StringComparison.Ordinal)) continue;

                tmp.enabled = false;
                NovaParked.Add(new CnoCover { Name = tmp, Id = pc.PlayerId });
                Logger.Info($"NOVADIAG hold id={pc.PlayerId} f={Time.frameCount}", "ExplosionFx"); // NOVADIAG
            }
        }
        catch (System.Exception e) { Logger.Warn($"HoldParkedArrows: {e.Message}", "ExplosionFx"); }
    }

    // 壁の判定に使う層 (壁本体と影)。ゲーム側の値を毎フレーム読まないよう最初に 1 回だけ読む
    private static int _novaWallMask;

    private static int NovaWallMask()
    {
        if (_novaWallMask == 0) _novaWallMask = Constants.ShipAndObjectsMask | Constants.ShadowMask;
        return _novaWallMask;
    }

    // ── 毎フレーム ──

    private static void PulseNova()
    {
        float now = Time.time;
        float dt = Time.deltaTime;
        MeshRenderer shadowQuad = HudManager.InstanceExists ? HudManager.Instance.ShadowQuad : null;

        for (int i = NovaUncovers.Count - 1; i >= 0; i--)
        {
            (CnoCover cover, float at) = NovaUncovers[i];

            // 同じ矢の CNO を次の 1 発がもう隠していれば、戻さずに引き渡す
            bool reused = false;
            foreach (NovaComet c in NovaComets)
            {
                if (c.Cover.Name && c.Cover.Id == cover.Id) reused = true;
            }

            if (reused)
            {
                Logger.Info($"NOVADIAG handover id={cover.Id} f={Time.frameCount}", "ExplosionFx"); // NOVADIAG
                NovaUncovers.RemoveAt(i);
                continue;
            }

            if (now < at)
            {
                if (cover.Name && cover.Name.enabled) cover.Name.enabled = false;
                continue;
            }

            if (cover.Name) Logger.Info($"NOVADIAG uncover id={cover.Id} pos={cover.Name.transform.position} f={Time.frameCount}", "ExplosionFx"); // NOVADIAG
            UncoverCno(cover);
            NovaUncovers.RemoveAt(i);
        }

        // 先に隠した矢: 流星が見つけたら引き渡す。操縦が全部終わったら戻す (残りは片付けられるか退避座標にいる)
        for (int i = NovaParked.Count - 1; i >= 0; i--)
        {
            CnoCover cover = NovaParked[i];
            bool taken = false;
            foreach (NovaComet c in NovaComets)
            {
                if (c.Cover.Name && c.Cover.Id == cover.Id) taken = true;
            }

            if (!cover.Name || taken)
            {
                NovaParked.RemoveAt(i);
                continue;
            }

            if (NovaAuras.Count == 0)
            {
                UncoverCno(cover);
                NovaParked.RemoveAt(i);
                continue;
            }

            if (cover.Name.enabled) cover.Name.enabled = false;
        }

        // 1 つが例外を出しても、それだけ片付けて他の演出は続ける
        for (int i = NovaAuras.Count - 1; i >= 0; i--)
        {
            bool done;
            try { done = TickAura(NovaAuras[i], now, dt, shadowQuad); }
            catch (System.Exception e)
            {
                Logger.Warn($"NovaAura: {e.Message}", "ExplosionFx");
                done = true;
            }

            if (!done) continue;
            ReleaseAura(NovaAuras[i], false);
            NovaAuras.RemoveAt(i);
        }

        for (int i = NovaComets.Count - 1; i >= 0; i--)
        {
            bool done;
            try { done = TickComet(NovaComets[i], now, dt); }
            catch (System.Exception e)
            {
                Logger.Warn($"NovaComet: {e.Message}", "ExplosionFx");
                done = true;
            }

            if (!done) continue;
            ReleaseComet(NovaComets[i], false);
            NovaComets.RemoveAt(i);
        }

        _vision = false;
    }

    // true = 消し終わった
    private static bool TickAura(NovaAura a, float now, float dt, MeshRenderer shadowQuad)
    {
        PlayerControl pc = a.Pc;
        if (!pc) return true;
        if (a.DyingFrom < 0f && (!pc.IsAlive() || now >= a.Until)) BeginAuraFade(a, 0.35f);

        float fade = 1f;
        if (a.DyingFrom >= 0f)
        {
            fade = 1f - (now - a.DyingFrom) / a.DyingFade;
            if (fade <= 0f) return true;
        }

        _vision = a.Vision;
        MeshRenderer shadow = a.Vision ? shadowQuad : null;
        Vector2 c = pc.Pos();
        float t = now - a.Start;
        float fadeIn = FxMath.Clamp01(t / 0.35f);
        bool hide = Unseen(pc, a.Id);

        if (a.OrbitLine != 0)
        {
            FxLines.Move(a.OrbitLine, FxMath.V2(c.x - a.Origin.x, c.y - a.Origin.y));
            FxLines.SetMul(a.OrbitLine, hide ? 0f : 0.55f + 0.2f * FxMath.Sin(t * 3f));
        }

        // ☆の CNO を隠し、同じ所に星を描く。CNO は 0.1 秒ごとに跳ぶので、描く星は滑らかに追わせる。
        // 文字は名前の欄 (頭の上) に改行で下げて描かれ、見える☆の中心は CNO 本体の位置になる
        // ☆の CNO が片付けられたまま戻らなければ操縦は終わっている (終わりの知らせは落ちることがある)
        if (CoverNova(ref a.Cover, a, null, c, a.Orbit + 1.2f, NovaStarMarker) && a.DyingFrom < 0f) BeginAuraFade(a, 0.35f);
        if (a.Cover.Name)
        {
            if (!a.StarTf || a.StarTfId != a.Cover.Id)
            {
                Transform names = a.Cover.Name.transform.parent;
                a.StarTf = names ? names.parent : null;
                a.StarTfId = a.Cover.Id;
            }

            Vector3 p = a.StarTf ? a.StarTf.position : a.Cover.Name.transform.position;
            var target = FxMath.V2(p.x, p.y);
            float jx = target.x - a.Star.x, jy = target.y - a.Star.y;
            if (!a.StarKnown || jx * jx + jy * jy > 16f) a.Star = target;
            else
            {
                float k = 1f - FxMath.Exp(-dt * 22f);
                a.Star = FxMath.V2(a.Star.x + jx * k, a.Star.y + jy * k);
            }

            a.StarKnown = true;
        }

        if (hide)
        {
            HideNova(a.FloorOuter);
            HideNova(a.FloorInner);
            HideNova(a.Aim);
            HideNova(a.StarHalo);
            HideNova(a.StarGlow);
            HideNova(a.StarBody);
            HideNova(a.StarFlare);
            HideNova(a.StarCore);
            return false;
        }

        float al = fade * fadeIn;
        Vector2 feet = Off(c, 0f, -0.38f);
        PlaceNova(a.FloorOuter, shadow, feet, 2.6f + 0.06f * FxMath.Sin(t * 4f), 2.6f, t * 35f, NovaGold, 0.5f * al);
        PlaceNova(a.FloorInner, shadow, feet, 1.9f, 1.9f, -t * 55f, NovaViolet, 0.45f * al);

        if (a.StarKnown)
        {
            Vector2 s = a.Star;
            float beat = 1f + 0.07f * FxMath.Sin(t * 9f);
            PlaceNova(a.StarHalo, shadow, s, 1.5f * beat, 1.5f * beat, t * 40f, NovaViolet, 0.55f * al);
            PlaceNova(a.StarGlow, shadow, s, 1.05f * beat, 1.05f * beat, 0f, NovaGold, 0.7f * al);
            PlaceNova(a.StarBody, shadow, s, 0.62f * beat, 0.62f * beat, t * 120f, NovaGold, al);
            PlaceNova(a.StarFlare, shadow, s, 1.3f, 1.3f, 15f + 10f * FxMath.Sin(t * 2f), NovaWhite, 0.85f * al);
            PlaceNova(a.StarCore, shadow, s, 0.34f, 0.34f, 0f, NovaWhite, al);

            // 照準: ☆から撃つ向き (本人 → ☆) へ伸びる細い光
            float ax = s.x - c.x, ay = s.y - c.y;
            float aim = FxMath.Atan2(ay, ax) * FxMath.Rad2Deg;
            PlaceNova(a.Aim, shadow, s, 3.2f, 0.13f, aim, NovaGold, 0.65f * al * (0.75f + 0.25f * FxMath.Sin(t * 7f)));

            if (a.DyingFrom < 0f && Active.Count < 2000)
            {
                // 星屑の尾
                if (now >= a.NextDust)
                {
                    a.NextDust = now + 0.035f;
                    Add(Shape.Star, Off(s, Rnd(-0.12f, 0.12f), Rnd(-0.12f, 0.12f)), FxMath.V2(Rnd(-0.25f, 0.25f), Rnd(-0.15f, 0.3f)), Rnd(0.45f, 0.75f), Rnd(0.12f, 0.2f), 0.02f,
                        NovaWhite, FxMath.Value < 0.5f ? NovaGold : NovaViolet, 1f, 0.05f, 0.5f, twinkle: 0.5f, twinkleSpeed: Rnd(12f, 20f), order: 8);
                }

                // 鼓動: ☆から光の輪
                if (now >= a.NextPulse)
                {
                    a.NextPulse = now + 0.9f;
                    Add(Shape.Ring, s, Vector2.zero, 0.45f, 0.3f, 1.7f, NovaWhite, NovaGold, 0.7f, 0.01f, 0.3f, order: 7);
                    bool keep = FxSound.NeedSight;
                    FxSound.NeedSight = a.Vision;
                    try { FxSound.At("FxNovaPulse", s, 0.35f); }
                    finally { FxSound.NeedSight = keep; }
                }
            }
        }
        else
        {
            HideNova(a.StarHalo);
            HideNova(a.StarGlow);
            HideNova(a.StarBody);
            HideNova(a.StarFlare);
            HideNova(a.StarCore);
            HideNova(a.Aim);
        }

        // 本人の周りに金の粒が昇る
        if (a.DyingFrom < 0f && now >= a.NextRim && Active.Count < 2000)
        {
            a.NextRim = now + 0.09f;
            Add(Shape.Star, Off(c, Rnd(-0.42f, 0.42f), Rnd(-0.5f, 0.15f)), FxMath.V2(Rnd(-0.1f, 0.1f), Rnd(0.6f, 1.1f)), Rnd(0.5f, 0.7f), Rnd(0.1f, 0.16f), 0.02f,
                NovaWhite, FxMath.Value < 0.6f ? NovaGold : NovaViolet, 0.9f, 0.1f, 0.5f, twinkle: 0.4f, twinkleSpeed: 16f, order: 8);
        }

        return false;
    }

    // true = 消し終わった
    private static bool TickComet(NovaComet c, float now, float dt)
    {
        // 流星は壁越しにも見せる (影に隠さない)
        _vision = false;
        c.Age += dt;
        var perp = FxMath.V2(-c.Dir.y, c.Dir.x);

        if (c.DyingFrom < 0f)
        {
            float step = c.Speed * dt;
            if (step > c.Left) step = c.Left;

            // 矢の CNO はホストが壁で止める。同じ壁で止めておき、命中の知らせを待たずに済む所は手元で終える
            bool wall = step > 0f && !NovaPiercesWalls[c.Id & 31] && PhysicsHelpers.AnyNonTriggersBetween(c.Pos, c.Dir, step, NovaWallMask());
            if (wall)
            {
                SpawnNovaWallBurst(c.Pos, c.Dir, c.Angle);
                FxSound.At("FxNovaFizzle", c.Pos, 0.8f);
                c.DyingFrom = now;
                c.DyingFade = 0.1f;
            }
            else
            {
                c.Pos = FxMath.V2(c.Pos.x + c.Dir.x * step, c.Pos.y + c.Dir.y * step);
                c.Travelled += step;
                c.Left -= step;

                if (c.Left <= 0.001f)
                {
                    SpawnNovaDissolve(c.Pos, c.Dir);
                    c.DyingFrom = now;
                    c.DyingFade = 0.25f;
                }
            }
        }

        float fade = 1f;
        if (c.DyingFrom >= 0f)
        {
            fade = 1f - (now - c.DyingFrom) / c.DyingFade;
            if (fade <= 0f) return true;
        }

        // 矢の CNO の文字を隠す。CNO はホストの TP 間隔ぶん流星より後ろにいる
        float lag = c.Speed * 0.15f;
        CoverNova(ref c.Cover, null, c, Off(c.Pos, -c.Dir.x * lag, -c.Dir.y * lag), 1f + c.Speed * 0.25f, NovaArrowMarker);

        Vector2 p = c.Pos;
        float tail = FxMath.Min(c.Travelled, NovaTailMax);
        float back = c.Angle + 180f;
        float beat = 1f + 0.08f * FxMath.Sin(c.Age * 30f);
        float fadeIn = FxMath.Clamp01(c.Age / 0.05f);
        float al = fade * fadeIn;

        PlaceNova(c.TailOuter, null, p, tail, 1.05f, back, NovaViolet, 0.55f * al);
        PlaceNova(c.TailMid, null, p, tail * 0.85f, 0.5f, back, NovaGold, 0.95f * al);
        PlaceNova(c.TailCore, null, p, tail * 0.6f, 0.18f, back, NovaWhite, al);
        PlaceNova(c.HeadHalo, null, p, 1.9f * beat, 1.9f * beat, c.Age * 90f, NovaViolet, 0.6f * al);
        PlaceNova(c.HeadGlow, null, p, 1.15f * beat, 1.15f * beat, 0f, NovaGold, 0.9f * al);
        PlaceNova(c.HeadBody, null, p, 0.75f, 0.75f, c.Age * 540f, NovaGold, al);
        PlaceNova(c.HeadFlare, null, p, 2f, 0.7f, c.Angle, NovaWhite, 0.95f * al);
        PlaceNova(c.HeadCore, null, p, 0.42f, 0.42f, 0f, NovaWhite, al);

        if (c.DyingFrom >= 0f || Active.Count >= 2000) return false;

        bool thin = Active.Count > 1200;

        // 通り道に残る光の筋 (少しの間だけ残って消える)
        if (now >= c.NextTrail)
        {
            c.NextTrail = now + 0.03f;
            Line2(c.LastTrail, p, 0.1f, 0.4f, NovaGold, NovaWhite, 0.85f, 0.01f, 0.15f, order: 6);
            c.LastTrail = p;
        }

        // 2 本の螺旋 (紫とシアン / 金)
        c.Helix += dt * 18f;
        if (!thin || Time.frameCount % 2 == 0)
        {
            float off = FxMath.Sin(c.Helix) * 0.32f;
            Add(Shape.Glow, Off(p, perp.x * off, perp.y * off), FxMath.V2(-c.Dir.x * 0.3f, -c.Dir.y * 0.3f), 0.3f, 0.2f, 0.05f, NovaCyan, NovaViolet, 0.9f, 0.05f, 0.4f, order: 7);
            Add(Shape.Glow, Off(p, -perp.x * off, -perp.y * off), FxMath.V2(-c.Dir.x * 0.3f, -c.Dir.y * 0.3f), 0.3f, 0.2f, 0.05f, NovaWhite, NovaGold, 0.9f, 0.05f, 0.4f, order: 7);
        }

        // またたく星屑を残す
        if (now >= c.NextDust)
        {
            c.NextDust = now + (thin ? 0.05f : 0.025f);
            float side = Rnd(-0.3f, 0.3f);
            float drift = Rnd(-0.6f, 0.6f);
            Add(Shape.Star, Off(p, perp.x * side, perp.y * side), FxMath.V2(perp.x * drift - c.Dir.x * 0.5f, perp.y * drift - c.Dir.y * 0.5f), Rnd(0.35f, 0.6f), Rnd(0.12f, 0.2f), 0.02f,
                NovaWhite, FxMath.Value < 0.6f ? NovaGold : NovaViolet, 1f, 0.05f, 0.5f, twinkle: 0.5f, twinkleSpeed: Rnd(14f, 22f), order: 8);
        }

        return false;
    }

    // ── 五芒星の絵 (頂点が上) ──

    private static float StarFiveAlpha(float x, float y)
    {
        const float outer = 0.9f, inner = 0.38f;
        const float wedge = 2f * FxMath.PI / 5f;

        float r = FxMath.Sqrt(x * x + y * y);
        float theta = FxMath.Atan2(x, y);
        float phi = FxMath.Abs(FxMath.Repeat(theta + wedge * 0.5f, wedge) - wedge * 0.5f);

        // 先端 T = (0, outer)、くぼみ V = (inner sin(w/2), inner cos(w/2)) を結ぶ辺までの符号付き距離 (外が正)
        float vx = inner * FxMath.Sin(wedge * 0.5f), vy = inner * FxMath.Cos(wedge * 0.5f);
        float ex = vx, ey = vy - outer;
        float len = FxMath.Sqrt(ex * ex + ey * ey);
        float nx = -ey / len, ny = ex / len;
        if (nx * 0f + ny * -outer > 0f)
        {
            nx = -nx;
            ny = -ny;
        }

        float px = r * FxMath.Sin(phi), py = r * FxMath.Cos(phi);
        float d = (px - 0f) * nx + (py - outer) * ny;

        float body = FxMath.Clamp01(0.5f - d / 0.03f) * (0.78f + 0.22f * (1f - r));
        float glow = d > 0f ? FxMath.Exp(-d / 0.05f) * 0.35f : 0f;
        return FxMath.Clamp01(FxMath.Max(body, glow));
    }
}
