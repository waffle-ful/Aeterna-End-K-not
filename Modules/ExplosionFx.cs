using System.Collections.Generic;
using EndKnot.Modules.Audience;
using HarmonyLib;
using Hazel;
using UnityEngine;

namespace EndKnot.Modules;

// 爆発の見た目だけの演出。ホストと End K not を入れているクライアントにだけ見える
// (バニラのクライアントは CustomRPC.PlayVisualFx を解釈しないので何も起きない)。
// 当たり判定やゲーム進行には一切関与しない。
public static class ExplosionFx
{
    public enum Kind : byte
    {
        Fire = 0,
        Supernova = 1
    }

    private readonly record struct Request(Kind Kind, Vector2 Pos, float Radius);

    private enum Shape : byte
    {
        Cloud,
        Flame,
        Ring,
        Ray,
        Chunk,
        Star,
        Glow,
        Solid
    }

    private struct Particle
    {
        public GameObject Go;
        public SpriteRenderer Sr;
        public Transform Tf;
        public Vector2 Pos;
        public Vector2 Vel;
        public float Drag;
        public float Rise;
        public float Delay;
        public float Age;
        public float Life;
        public float Sx0, Sx1, Sy0, Sy1;
        public float Rot;
        public float Spin;
        public Color Color0;
        public Color Color1;
        public Color ColorMid;
        public bool HasMid;
        public float Alpha;
        public float FadeIn;
        public float FadeOutFrom;
        public float Twinkle;
        public float TwinkleSpeed;
        public float Phase;
        public float Stretch;
        public bool FollowCamera;
        public float ShakeAmplitude;
        public float ShakeDuration;
    }

    // 一度に大量に起爆する役職 (複数の爆弾を同じフレームで起爆するもの) でも RPC は 1 本にまとめ、
    // 呼び出し元の頻度に関わらず送信は SendInterval 秒に 1 本までに抑える (待つ間は Unsent に溜める)。
    private static readonly List<Request> Pending = [];
    private static readonly List<Request> Unsent = [];
    private static readonly List<Particle> Active = [];

    // 1 発で数百粒を出すので GameObject は使い回す (毎回 new/Destroy するとフレームが引っかかる)。
    private static readonly Stack<(GameObject Go, SpriteRenderer Sr)> Pool = [];

    private const int MaxPendingPerFrame = 24;
    private const float SendInterval = 0.1f;
    private const int MaxActive = 1500;
    private const int SortingOrder = 150;
    private const float RayAspect = 4f;
    private static float _lastSendTime = -1f;
    private static float _holdUntil = -1f;

    private static Sprite _glow;
    private static Sprite _cloud;
    private static Sprite _flame;
    private static Sprite _ring;
    private static Sprite _star;
    private static Sprite _ray;
    private static Sprite _chunk;
    private static Sprite _solid;

    // ホストが呼ぶ。描画とモッドクライアントへの送信は次の HudManager.Update 以降でまとめて行う。
    public static void Play(Kind kind, Vector2 pos, float radius)
    {
        if (!AmongUsClient.Instance || !AmongUsClient.Instance.AmHost || !GameStates.InGame) return;
        if (Pending.Count >= MaxPendingPerFrame) return;

        Pending.Add(new Request(kind, pos, Mathf.Clamp(radius, 0.3f, 15f)));
    }

    public static void ReceiveRPC(MessageReader reader)
    {
        int count = reader.ReadByte();

        for (int i = 0; i < count; i++)
        {
            var kind = (Kind)reader.ReadByte();
            float x = reader.ReadSingle();
            float y = reader.ReadSingle();
            float radius = reader.ReadSingle();
            if (!GameStates.InGame) continue;

            SpawnLocal(new Request(kind, new Vector2(x, y), Mathf.Clamp(radius, 0.3f, 15f)));
        }
    }

    internal static void Tick()
    {
        try
        {
            if (!GameStates.InGame)
            {
                if (Pending.Count > 0) Pending.Clear();
                if (Unsent.Count > 0) Unsent.Clear();
                if (Active.Count > 0) ClearAll();
                return;
            }

            if (Pending.Count > 0) Flush();
            if (Unsent.Count > 0 && Time.unscaledTime - _lastSendTime >= SendInterval) Send();
            // 自分がこの爆発で死んだ時はキル演出が画面を覆うので、明けるまで演出を止めておいて後から見せる
            if (Active.Count > 0 && Time.time >= _holdUntil && !KillOverlayOpen()) Animate(Time.deltaTime);
        }
        catch (System.Exception e) { Utils.ThrowException(e); }
    }

    private static void Flush()
    {
        try
        {
            foreach (Request r in Pending)
            {
                SpawnLocal(r);
                if (Unsent.Count < MaxPendingPerFrame) Unsent.Add(r);
            }
        }
        finally { Pending.Clear(); }
    }

    private static void Send()
    {
        try
        {
            if (AmongUsClient.Instance.AmHost && AnyOtherModdedClient())
            {
                _lastSendTime = Time.unscaledTime;
                MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(PlayerControl.LocalPlayer.NetId, (byte)CustomRPC.PlayVisualFx, SendOption.None);
                writer.Write((byte)Unsent.Count);

                foreach (Request r in Unsent)
                {
                    writer.Write((byte)r.Kind);
                    writer.Write(r.Pos.x);
                    writer.Write(r.Pos.y);
                    writer.Write(r.Radius);
                }

                EarlyWarning.OnPacket("PlayVisualFx", writer.Length, writer.Length, "None");
                AmongUsClient.Instance.FinishRpcImmediately(writer);
            }
        }
        catch (System.Exception e) { Utils.ThrowException(e); }
        finally { Unsent.Clear(); }
    }

    private static bool KillOverlayOpen()
    {
        return HudManager.InstanceExists && HudManager.Instance.KillOverlay && HudManager.Instance.KillOverlay.IsOpen;
    }

    private static bool AnyOtherModdedClient()
    {
        foreach (PlayerControl pc in Main.EnumeratePlayerControls())
        {
            if (pc && !pc.AmOwner && pc.IsModdedClient()) return true;
        }

        return false;
    }

    private static void SpawnLocal(Request r)
    {
        try
        {
            EnsureSprites();

            // 爆発の範囲内にいる自分は直後にキル演出が出るので、それが開くまでの一瞬だけ待つ
            PlayerControl lp = PlayerControl.LocalPlayer;
            if (lp && lp.IsAlive() && Vector2.Distance(lp.GetTruePosition(), r.Pos) <= r.Radius + 0.5f) _holdUntil = Time.time + 0.35f;

            Logger.Info($"{r.Kind} at ({r.Pos.x:F2}, {r.Pos.y:F2}) r={r.Radius:F1}", "ExplosionFx");

            switch (r.Kind)
            {
                case Kind.Supernova:
                    SpawnSupernova(r.Pos, r.Radius);
                    break;
                default:
                    SpawnFire(r.Pos, r.Radius);
                    break;
            }
        }
        catch (System.Exception e) { Utils.ThrowException(e); }
    }

    // ── 演出の中身 ─────────────────────────────────────────────────────

    private static float Rnd(float min, float max) => Random.Range(min, max);
    private static Vector2 Dir() => Random.insideUnitCircle.normalized;

    // 近くで起きた爆発ほど強く、画面全体の閃光とカメラの揺れを返す (自分の画面だけ・送信なし)。
    private static void Impact(Vector2 c, float r, Color flash, float flashAlpha, float shake, float shakeDuration)
    {
        Camera cam = Camera.main;
        if (!cam) return;

        float reach = r + 9f;
        float dist = Vector2.Distance(cam.transform.position, c);
        if (dist > reach) return;

        float k = 1f - dist / reach;
        Add(Shape.Solid, c, Vector2.zero, 0.6f, 1f, 1f, flash, flash, flashAlpha * (0.35f + 0.65f * k), 0.004f, 0.08f, followCamera: true);
        // 揺れは閃光が実際に画面に出た瞬間に始める (キル演出の裏で揺れを使い切らない)
        if (Active.Count > 0 && Active[^1].FollowCamera)
        {
            Particle p = Active[^1];
            p.ShakeAmplitude = shake * (0.3f + 0.7f * k);
            p.ShakeDuration = shakeDuration;
            Active[^1] = p;
        }
    }

    // 目も眩む閃光 → 光条と 4 重の衝撃波 → 青白いプラズマと尾を引く流星群 → 渦巻く星雲
    // → 中心に残ったパルサー (脈打つ核・回る降着円盤・灯台のように回転するジェット) → 星屑の余韻
    private static void SpawnSupernova(Vector2 c, float r)
    {
        Color white = new(1f, 1f, 1f);
        Color hot = new(0.72f, 0.86f, 1f);
        Color lavender = new(0.82f, 0.76f, 1f);
        Color violet = new(0.6f, 0.3f, 1f);
        Color cyan = new(0.3f, 0.92f, 1f);
        Color magenta = new(1f, 0.32f, 0.85f);
        Color deep = new(0.06f, 0.03f, 0.2f);
        Color[] nebula = [new(0.5f, 0.18f, 0.95f), new(0.18f, 0.38f, 1f), new(0.95f, 0.28f, 0.75f), new(0.18f, 0.78f, 0.95f), new(0.36f, 0.12f, 0.72f), new(1f, 0.55f, 0.85f), new(0.25f, 0.2f, 0.85f)];
        Color[] starTints = [white, new(0.72f, 0.88f, 1f), new(1f, 0.8f, 0.95f), new(0.85f, 0.78f, 1f), new(0.6f, 1f, 1f)];

        Impact(c, r, new Color(0.92f, 0.9f, 1f), 0.9f, 0.28f, 1.2f);
        CustomSoundsManager.Play("Boom", 1f, 0.55f);

        // 爆心に口を開ける宇宙空間 (最奥)。明るい床の上でも星と星雲が映えるよう暗く沈める
        Color space = new(0.02f, 0.01f, 0.08f);
        Color spaceEdge = new(0.1f, 0.03f, 0.25f);
        Add(Shape.Cloud, c, Vector2.zero, 4.4f, r * 0.2f, r * 2.3f, space, spaceEdge, 1f, 0.05f, 0.62f, delay: 0.05f, spin: 12f, order: 0);

        for (int i = 0; i < 6; i++)
        {
            Vector2 d = Dir();
            Add(Shape.Cloud, c + d * r * Rnd(0.2f, 0.5f), d * r * Rnd(0.15f, 0.35f), Rnd(3.6f, 4.4f), r * 0.2f, r * Rnd(1.1f, 1.5f), space, spaceEdge,
                Rnd(0.8f, 0.95f), 0.06f, 0.6f, drag: 0.5f, delay: Rnd(0.05f, 0.15f), spin: Rnd(-20f, 20f), order: 0);
        }

        // 星雲
        for (int i = 0; i < 18; i++)
        {
            Vector2 d = Dir();
            Color col = nebula[Random.Range(0, nebula.Length)];
            float s = r * Rnd(1.2f, 2f);
            Add(Shape.Cloud, c + d * Rnd(0f, r * 0.35f), d * Rnd(r * 0.25f, r * 0.7f), Rnd(3.4f, 4.6f), r * Rnd(0.3f, 0.5f), s,
                col, Color.Lerp(col, deep, 0.35f), Rnd(0.28f, 0.42f), 0.07f, 0.4f, drag: 0.7f, delay: Rnd(0f, 0.18f), spin: Rnd(-35f, 35f));
        }

        // 衝撃波 4 重 (2 本目の縁 = 実際の爆発半径)
        Add(Shape.Ring, c, Vector2.zero, 0.35f, r * 0.1f, r * 2.2f, white, cyan, 1f, 0.01f, 0.35f);
        Add(Shape.Ring, c, Vector2.zero, 0.6f, r * 0.05f, r * 2f, cyan, violet, 0.95f, 0.02f, 0.4f, delay: 0.05f);
        Add(Shape.Ring, c, Vector2.zero, 0.95f, r * 0.05f, r * 1.8f, magenta, violet, 0.75f, 0.03f, 0.35f, delay: 0.12f);
        Add(Shape.Ring, c, Vector2.zero, 1.6f, r * 0.2f, r * 3f, lavender, violet, 0.35f, 0.1f, 0.3f, delay: 0.25f);

        // 光条 (放射状に伸びる光の筋)
        const int rays = 20;
        for (int i = 0; i < rays; i++)
        {
            Color col = (i % 3) switch { 0 => white, 1 => cyan, _ => lavender };
            float w = r * Rnd(0.05f, 0.11f);
            Add(Shape.Ray, c, Vector2.zero, Rnd(0.9f, 1.5f), r * 0.3f, r * Rnd(1.9f, 2.8f), col, violet, Rnd(0.55f, 0.9f), 0.02f, 0.3f,
                rot: i * 360f / rays + Rnd(-7f, 7f), spin: 18f, sy0: w, sy1: w * 0.3f);
        }

        // プラズマの噴出
        for (int i = 0; i < 16; i++)
        {
            Vector2 d = Dir();
            Add(Shape.Cloud, c, d * r * Rnd(1.3f, 2.4f), Rnd(0.6f, 1.05f), r * 0.25f, r * Rnd(0.6f, 0.95f), hot, violet, 0.85f, 0.01f, 0.35f,
                drag: 3.5f, spin: Rnd(-90f, 90f));
        }

        // 尾を引いて飛び散る流星群
        for (int i = 0; i < 120; i++)
        {
            Vector2 d = Dir();
            Color col = starTints[Random.Range(0, starTints.Length)];
            Add(Shape.Star, c + d * r * 0.05f, d * r * Rnd(2.5f, 7f), Rnd(0.7f, 1.7f), Rnd(0.18f, 0.34f), Rnd(0.06f, 0.12f),
                col, Color.Lerp(col, violet, 0.6f), 1f, 0.01f, 0.5f, drag: 2.6f, stretch: 0.1f, twinkle: 0.3f, twinkleSpeed: Rnd(15f, 25f));
        }

        // 閃光の核
        Add(Shape.Glow, c, Vector2.zero, 0.5f, r * 2f, r * 0.45f, white, lavender, 1f, 0.004f, 0.3f);
        Add(Shape.Glow, c, Vector2.zero, 1.4f, r * 0.85f, r * 0.2f, hot, violet, 0.9f, 0.02f, 0.25f);

        // パルサー
        const float pulsar = 3.8f;
        Add(Shape.Ring, c, Vector2.zero, pulsar, r * 0.45f, r * 0.8f, cyan, violet, 0.75f, 0.2f, 0.6f,
            delay: 0.3f, rot: 20f, spin: 140f, sy0: r * 0.45f * 0.3f, sy1: r * 0.8f * 0.3f);
        Add(Shape.Ring, c, Vector2.zero, pulsar, r * 0.7f, r * 1.05f, magenta, violet, 0.5f, 0.2f, 0.6f,
            delay: 0.35f, rot: -35f, spin: -95f, sy0: r * 0.7f * 0.26f, sy1: r * 1.05f * 0.26f);

        for (int k = 0; k < 2; k++)
        {
            Add(Shape.Ray, c, Vector2.zero, pulsar - 0.2f, r * 1.3f, r * 1.9f, white, cyan, 0.8f, 0.2f, 0.55f,
                delay: 0.3f, rot: 70f + k * 180f, spin: 230f, sy0: r * 0.07f, sy1: r * 0.04f, twinkle: 0.35f, twinkleSpeed: 9f);
        }

        Add(Shape.Glow, c, Vector2.zero, pulsar, 1.1f, 0.6f, white, cyan, 1f, 0.15f, 0.7f, delay: 0.25f, twinkle: 0.55f, twinkleSpeed: 14f);
        Add(Shape.Star, c, Vector2.zero, pulsar, 1.2f, 0.7f, white, lavender, 1f, 0.15f, 0.7f, delay: 0.25f, spin: 60f);

        // 星屑の余韻
        for (int i = 0; i < 80; i++)
        {
            Add(Shape.Star, c + Random.insideUnitCircle * r * 1.3f, Random.insideUnitCircle * r * 0.15f, Rnd(2.5f, 4.3f), Rnd(0.08f, 0.2f), Rnd(0.03f, 0.08f),
                starTints[Random.Range(0, starTints.Length)], lavender, 1f, 0.15f, 0.55f,
                delay: Rnd(0.3f, 1.3f), spin: Rnd(-60f, 60f), twinkle: 0.85f, twinkleSpeed: Rnd(8f, 18f));
        }
    }

    // 閃光 → 光条と衝撃波 → 白から橙・深紅・煤へ燃え落ちる火球 → 周りで起こる誘爆
    // → 回転しながら飛ぶ破片と火花の尾 → 渦巻いて立ちのぼる黒煙
    private static void SpawnFire(Vector2 c, float r)
    {
        Color flash = new(1f, 0.96f, 0.8f);
        Color white = new(1f, 0.98f, 0.9f);
        Color yellow = new(1f, 0.85f, 0.3f);
        Color orange = new(1f, 0.45f, 0.08f);
        Color red = new(0.75f, 0.12f, 0.04f);
        Color soot = new(0.12f, 0.08f, 0.07f);
        Color smoke = new(0.2f, 0.18f, 0.18f);

        Impact(c, r, new Color(1f, 0.85f, 0.6f), 0.75f, 0.22f, 0.8f);

        // 黒煙 (最奥・遅れて立ちのぼる)
        for (int i = 0; i < 16; i++)
        {
            Add(Shape.Cloud, c + Random.insideUnitCircle * r * 0.5f, Random.insideUnitCircle * r * 0.35f, Rnd(2.4f, 3.6f), r * Rnd(0.3f, 0.5f), r * Rnd(1f, 1.5f),
                smoke, soot, Rnd(0.45f, 0.65f), 0.2f, 0.4f, drag: 0.8f, delay: Rnd(0.5f, 0.9f), spin: Rnd(-40f, 40f), rise: Rnd(0.25f, 0.55f));
        }

        Add(Shape.Ring, c, Vector2.zero, 0.3f, r * 0.1f, r * 2.1f, white, yellow, 1f, 0.01f, 0.3f);
        Add(Shape.Ring, c, Vector2.zero, 0.55f, r * 0.05f, r * 2f, yellow, red, 0.8f, 0.02f, 0.35f, delay: 0.05f);

        for (int i = 0; i < 12; i++)
        {
            float w = r * Rnd(0.06f, 0.12f);
            Add(Shape.Ray, c, Vector2.zero, Rnd(0.35f, 0.6f), r * 0.3f, r * Rnd(1.4f, 2.1f), white, orange, Rnd(0.5f, 0.8f), 0.01f, 0.25f,
                rot: i * 30f + Rnd(-10f, 10f), sy0: w, sy1: w * 0.2f);
        }

        // 火球 (外側ほど早く赤黒く冷え、中心は長く白熱する)
        for (int i = 0; i < 38; i++)
        {
            Vector2 d = Dir();
            float out01 = Random.value;
            Add(Shape.Flame, c + d * Rnd(0f, r * 0.25f), d * r * Mathf.Lerp(0.4f, 2f, out01), Mathf.Lerp(1.9f, 1.1f, out01), r * Rnd(0.3f, 0.45f), r * Rnd(0.8f, 1.25f),
                Color.Lerp(white, yellow, out01), red, 1f, 0.02f, 0.55f, drag: 2.6f, delay: Rnd(0f, 0.06f), spin: Rnd(-120f, 120f),
                colorMid: Color.Lerp(yellow, orange, 0.5f + 0.5f * out01));
        }

        // 誘爆
        for (int b = 0; b < 4; b++)
        {
            Vector2 at = c + Random.insideUnitCircle * r * 0.75f;
            float delay = Rnd(0.1f, 0.45f);
            float s = r * Rnd(0.25f, 0.4f);
            Add(Shape.Glow, at, Vector2.zero, 0.3f, s * 0.5f, s * 2.2f, white, yellow, 1f, 0.01f, 0.3f, delay: delay);
            Add(Shape.Ring, at, Vector2.zero, 0.35f, s * 0.2f, s * 2.4f, white, orange, 0.7f, 0.02f, 0.3f, delay: delay);

            for (int i = 0; i < 6; i++)
            {
                Vector2 d = Dir();
                Add(Shape.Flame, at, d * s * Rnd(1.5f, 3f), Rnd(0.8f, 1.2f), s * 0.6f, s * Rnd(1.3f, 1.8f),
                    white, red, 1f, 0.02f, 0.5f, drag: 3f, delay: delay, spin: Rnd(-120f, 120f), colorMid: orange);
            }
        }

        // 破片
        for (int i = 0; i < 22; i++)
        {
            Vector2 d = Dir();
            float s = Rnd(0.14f, 0.3f);
            Add(Shape.Chunk, c + d * r * 0.1f, d * r * Rnd(2f, 4.5f), Rnd(0.9f, 1.6f), s, s * 0.8f, new Color(0.35f, 0.22f, 0.15f), soot, 1f, 0.01f, 0.65f,
                drag: 2.4f, spin: Rnd(-720f, 720f), sy0: s * Rnd(0.5f, 1f), sy1: s * 0.5f);
        }

        // 火花
        for (int i = 0; i < 70; i++)
        {
            Vector2 d = Dir();
            Add(Shape.Star, c + d * r * 0.05f, d * r * Rnd(3f, 8f), Rnd(0.6f, 1.3f), Rnd(0.2f, 0.36f), 0.08f, white, red, 1f, 0.01f, 0.5f,
                drag: 3f, stretch: 0.12f, twinkle: 0.35f, twinkleSpeed: Rnd(20f, 30f));
        }

        // 灼熱の核
        Add(Shape.Glow, c, Vector2.zero, 0.35f, r * 1.8f, r * 0.5f, flash, yellow, 1f, 0.004f, 0.25f);
        Add(Shape.Glow, c, Vector2.zero, 1.3f, r * 1.1f, r * 0.4f, white, red, 1f, 0.02f, 0.4f, colorMid: yellow);
    }

    // ── パーティクル ───────────────────────────────────────────────────

    // sy0/sy1 を省略すると縦横同じ倍率。stretch > 0 は速度方向へ尾を伸ばして進行方向を向く。
    private static void Add(Shape shape, Vector2 pos, Vector2 vel, float life, float sx0, float sx1, Color color0, Color color1,
                            float alpha, float fadeIn, float fadeOutFrom, float drag = 0f, float delay = 0f, float spin = 0f, float? rot = null,
                            float sy0 = -1f, float sy1 = -1f, float twinkle = 0f, float twinkleSpeed = 0f, float rise = 0f,
                            float stretch = 0f, bool followCamera = false, int order = -1, Color? colorMid = null)
    {
        if (Active.Count >= MaxActive) return;

        GameObject go = null;
        SpriteRenderer sr = null;

        while (Pool.Count > 0)
        {
            (GameObject pgo, SpriteRenderer psr) = Pool.Pop();
            if (!pgo || !psr) continue;

            go = pgo;
            sr = psr;
            break;
        }

        if (!go)
        {
            go = new GameObject("ExplosionFx") { layer = 0 };
            sr = go.AddComponent<SpriteRenderer>();
        }

        Transform tf = go.transform;
        tf.position = new Vector3(pos.x, pos.y, 0f);
        tf.localScale = Vector3.zero;

        sr.sprite = shape switch
        {
            Shape.Cloud => _cloud,
            Shape.Flame => _flame,
            Shape.Ring => _ring,
            Shape.Ray => _ray,
            Shape.Chunk => _chunk,
            Shape.Star => _star,
            Shape.Solid => _solid,
            _ => _glow
        };
        // 奥から (order=0 の背景) → 雲 → 衝撃波 → 光条 → 破片 → 星 → 光 → 画面の閃光 の順に重ねる
        sr.sortingOrder = SortingOrder + (order >= 0 ? order : shape == Shape.Solid ? 20 : (int)shape + 1);
        sr.color = new Color(color0.r, color0.g, color0.b, 0f);
        go.SetActive(true);

        // 光条のテクスチャは横長 (4:1) なので、縦の指定値がそのまま太さ (単位) になるよう補正する
        float aspect = shape == Shape.Ray ? RayAspect : 1f;

        Active.Add(new Particle
        {
            Go = go, Sr = sr, Tf = tf, Pos = pos, Vel = vel, Drag = drag, Rise = rise, Delay = delay, Life = life,
            Sx0 = sx0, Sx1 = sx1, Sy0 = (sy0 < 0f ? sx0 : sy0) * aspect, Sy1 = (sy1 < 0f ? sx1 : sy1) * aspect,
            Rot = rot ?? Random.Range(0f, 360f), Spin = spin,
            Color0 = color0, Color1 = color1, ColorMid = colorMid ?? color0, HasMid = colorMid.HasValue, Alpha = alpha, FadeIn = Mathf.Max(fadeIn, 0.001f), FadeOutFrom = fadeOutFrom,
            Twinkle = twinkle, TwinkleSpeed = twinkleSpeed, Phase = Random.Range(0f, 6.28f), Stretch = stretch, FollowCamera = followCamera
        });
    }

    private static void Release(Particle p)
    {
        if (!p.Go) return;

        p.Go.SetActive(false);
        Pool.Push((p.Go, p.Sr));
    }

    private static void Animate(float dt)
    {
        Camera cam = null;

        for (int i = Active.Count - 1; i >= 0; i--)
        {
            Particle p = Active[i];

            if (!p.Go)
            {
                Active.RemoveAt(i);
                continue;
            }

            if (p.Delay > 0f)
            {
                p.Delay -= dt;
                Active[i] = p;
                continue;
            }

            if (p.Age == 0f && p.ShakeAmplitude > 0f) AudienceCutscene.ShakeCamera(p.ShakeDuration, p.ShakeAmplitude);

            p.Age += dt;
            float t = p.Age / p.Life;

            if (t >= 1f)
            {
                Release(p);
                Active.RemoveAt(i);
                continue;
            }

            p.Vel *= Mathf.Exp(-p.Drag * dt);
            p.Pos += (p.Vel + new Vector2(0f, p.Rise)) * dt;
            p.Rot += p.Spin * dt;

            float ease = 1f - (1f - t) * (1f - t) * (1f - t);
            float sx = Mathf.Lerp(p.Sx0, p.Sx1, ease);
            float sy = Mathf.Lerp(p.Sy0, p.Sy1, ease);
            float rot = p.Rot;

            if (p.Stretch > 0f)
            {
                float speed = p.Vel.magnitude;
                sx *= 1f + speed * p.Stretch;
                if (speed > 0.01f) rot = Mathf.Atan2(p.Vel.y, p.Vel.x) * Mathf.Rad2Deg;
            }

            Vector3 at = new(p.Pos.x, p.Pos.y, 0f);

            if (p.FollowCamera)
            {
                if (!cam) cam = Camera.main;

                if (cam)
                {
                    Vector3 cp = cam.transform.position;
                    at = new Vector3(cp.x, cp.y, 0f);
                    float h = cam.orthographicSize * 2.4f;
                    sx = h * cam.aspect;
                    sy = h;
                    rot = 0f;
                }
            }

            float a = p.Alpha * Mathf.Clamp01(t / p.FadeIn);
            if (t > p.FadeOutFrom) a *= 1f - (t - p.FadeOutFrom) / (1f - p.FadeOutFrom);
            if (p.Twinkle > 0f) a *= Mathf.Lerp(1f - p.Twinkle, 1f, (Mathf.Sin(p.Age * p.TwinkleSpeed + p.Phase) + 1f) * 0.5f);

            Color col = !p.HasMid ? Color.Lerp(p.Color0, p.Color1, t)
                : t < 0.5f ? Color.Lerp(p.Color0, p.ColorMid, t * 2f)
                : Color.Lerp(p.ColorMid, p.Color1, (t - 0.5f) * 2f);
            p.Sr.color = new Color(col.r, col.g, col.b, a);

            p.Tf.position = at;
            p.Tf.localScale = new Vector3(sx, sy, 1f);
            p.Tf.localRotation = Quaternion.Euler(0f, 0f, rot);

            Active[i] = p;
        }
    }

    private static void ClearAll()
    {
        foreach (Particle p in Active) Release(p);
        Active.Clear();
    }

    // ── 手続き生成スプライト (白基調・色は SpriteRenderer.color で付ける) ──────────

    private static void EnsureSprites()
    {
        if (!_glow) _glow = MakeSprite(64, 64, (x, y) => Mathf.Pow(1f - Mathf.Clamp01(Mathf.Sqrt(x * x + y * y)), 2.4f));
        if (!_cloud) _cloud = MakeSprite(96, 96, CloudAlpha);
        if (!_flame) _flame = MakeSprite(96, 96, (x, y) => Mathf.Clamp01(Mathf.Pow(CloudAlpha(x, y), 0.45f) * 1.15f));
        if (!_solid) _solid = MakeSprite(4, 4, (_, _) => 1f);

        if (!_ring)
        {
            _ring = MakeSprite(128, 128, (x, y) =>
            {
                // 縁に細い光の輪、内側はうっすら満ちた膜
                float d = Mathf.Sqrt(x * x + y * y);
                float edge = Mathf.Exp(-Mathf.Pow((d - 0.93f) / 0.035f, 2f));
                float fill = d < 0.93f ? Mathf.Pow(d / 0.93f, 3f) * 0.18f : 0f;
                return Mathf.Clamp01(edge + fill);
            });
        }

        if (!_star)
        {
            _star = MakeSprite(64, 64, (x, y) =>
            {
                // 白熱した核 + 十字の光芒
                float d = Mathf.Sqrt(x * x + y * y);
                float core = Mathf.Pow(1f - Mathf.Clamp01(d / 0.35f), 2f);
                float ax = Mathf.Abs(x), ay = Mathf.Abs(y);
                float streakH = Mathf.Exp(-ay * 28f) * Mathf.Clamp01(1f - ax);
                float streakV = Mathf.Exp(-ax * 28f) * Mathf.Clamp01(1f - ay);
                return Mathf.Clamp01(Mathf.Max(core, Mathf.Pow(Mathf.Max(streakH, streakV), 1.5f)));
            });
        }

        if (!_ray)
        {
            // 根元 (左端) が太く明るく、先へ行くほど細く消える光の筋。pivot は根元。
            _ray = MakeSprite(128, (int)(128 / RayAspect), (x, y) =>
            {
                float u = (x + 1f) * 0.5f;
                float width = Mathf.Lerp(0.9f, 0.1f, u);
                float across = Mathf.Exp(-Mathf.Pow(y / width, 2f) * 3f);
                return Mathf.Clamp01(across * Mathf.Pow(1f - u, 1.3f));
            }, new Vector2(0f, 0.5f));
        }

        if (!_chunk)
        {
            // 角ばった不定形の破片
            _chunk = MakeSprite(32, 32, (x, y) =>
            {
                float ang = Mathf.Atan2(y, x);
                float edge = 0.7f + 0.2f * Mathf.Sin(ang * 3f + 1.3f) + 0.1f * Mathf.Sin(ang * 7f);
                float d = Mathf.Sqrt(x * x + y * y);
                return d < edge ? 1f : Mathf.Clamp01(1f - (d - edge) * 12f);
            });
        }
    }

    // 縁がふわっと崩れた柔らかい雲。完全な円だと「玉」に見えるので、角度方向に揺らぎを入れる。
    private static float CloudAlpha(float x, float y)
    {
        float d = Mathf.Sqrt(x * x + y * y);
        float ang = Mathf.Atan2(y, x);
        float wobble = 0.82f + 0.1f * Mathf.Sin(ang * 3f + 0.7f) + 0.08f * Mathf.Sin(ang * 5f + 2.1f);
        float n = Mathf.PerlinNoise(x * 2.3f + 5f, y * 2.3f + 5f);
        float body = 1f - Mathf.Clamp01(d / wobble);
        return Mathf.Clamp01(Mathf.Pow(body, 1.3f) * (0.65f + 0.35f * n));
    }

    // alpha は中心を (0,0)・縁を ±1 とした正規化座標で受け取る。スプライトの 1 単位 = テクスチャの幅。
    private static Sprite MakeSprite(int width, int height, System.Func<float, float, float> alpha, Vector2? pivot = null)
    {
        var tex = new Texture2D(width, height, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        var pixels = new Color[width * height];
        float hx = (width - 1) * 0.5f, hy = (height - 1) * 0.5f;

        for (int py = 0; py < height; py++)
        {
            for (int px = 0; px < width; px++)
                pixels[py * width + px] = new Color(1f, 1f, 1f, alpha((px - hx) / hx, (py - hy) / hy));
        }

        tex.SetPixels(pixels);
        tex.Apply(false, true);
        tex.hideFlags |= HideFlags.HideAndDontSave;

        // ppu は幅基準 (幅 = 1 単位)。横長のテクスチャは縦が 1 単位未満になる
        Sprite sprite = Sprite.Create(tex, new Rect(0, 0, width, height), pivot ?? new Vector2(0.5f, 0.5f), width, 0, SpriteMeshType.FullRect);
        sprite.hideFlags |= HideFlags.HideAndDontSave;
        return sprite;
    }
}

[HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
internal static class ExplosionFxHudUpdatePatch
{
    public static void Postfix()
    {
        ExplosionFx.Tick();
    }
}
