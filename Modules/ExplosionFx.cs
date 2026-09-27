using System.Collections.Generic;
using EndKnot.Modules.Audience;
using HarmonyLib;
using Hazel;
using UnityEngine;

namespace EndKnot.Modules;

// 爆発や突風など、役職の能力に添える見た目だけの演出。ホストと End K not を入れているクライアントにだけ見える
// (バニラのクライアントは CustomRPC.PlayVisualFx を解釈しないので何も起きない)。
// 当たり判定やゲーム進行には一切関与しない。
public static class ExplosionFx
{
    public enum Kind : byte
    {
        Fire = 0,
        Supernova = 1,

        // 横風: Pos = 発動者、Radius = 押し出す距離
        GustRight = 2,
        GustLeft = 3,

        // 横風で吹き飛ばされた人の軌跡: Pos = 出発点、Radius = 実際に飛んだ距離
        WindTrailRight = 4,
        WindTrailLeft = 5,

        // ジェミニ: 分身を置いた (本人の画面だけ) / 分身が切られて砕けた
        GeminiSplit = 6,
        GeminiShatter = 7,

        // ワープ: Pos = 消えた場所 / 現れた場所
        WarpOut = 8,
        WarpIn = 9,

        // 凍結: Pos = 凍った人、Radius = 凍っている秒数 (その間ずっと氷が残り、最後に砕ける)
        Freeze = 10,

        // 時間停止: Pos = 発動者、Radius = 止まっている秒数
        TimeStop = 11,

        // 落雷: Pos = 打たれた人
        LightningStrike = 12,

        // 重い物が叩きつけられた衝撃: Radius = 衝撃の大きさ (1 前後)
        Slam = 13,

        // 水しぶき: Radius = しぶきの大きさ (1.2 以上で溺れた時の泡も出す)
        Splash = 14
    }

    // LocalOnly はまとめ送信に載せない (PlayFor が宛先を絞って自前で送る)
    private readonly record struct Request(Kind Kind, Vector2 Pos, float Radius, bool LocalOnly = false);

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

    // 1 件 13 バイトなので上限いっぱいでも 1 本 630 バイト前後に収まる (15 人全員をワープさせると出発と到着で 28 件)
    private const int MaxPendingPerFrame = 48;
    private const float SendInterval = 0.1f;
    private const int MaxActive = 3000;
    private const int WarmPool = 800;
    private const int SortingOrder = 150;
    private const float RayAspect = 4f;
    private static float _lastSendTime = -1f;
    private static float _holdUntil = -1f;

    private static bool _warm;
    private static bool _jitted;
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

        Pending.Add(new Request(kind, pos, ClampRadius(kind, radius)));
    }

    // viewer 1 人の画面にだけ出す。周りに見えると能力の意味が崩れる演出 (ジェミニの分身設置) 用。
    public static void PlayFor(Kind kind, Vector2 pos, float radius, PlayerControl viewer)
    {
        if (!AmongUsClient.Instance || !AmongUsClient.Instance.AmHost || !GameStates.InGame || !viewer) return;

        var r = new Request(kind, pos, ClampRadius(kind, radius), true);

        if (viewer.AmOwner)
        {
            if (Pending.Count < MaxPendingPerFrame) Pending.Add(r);
            return;
        }

        if (!viewer.IsModdedClient()) return;

        try
        {
            MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(PlayerControl.LocalPlayer.NetId, (byte)CustomRPC.PlayVisualFx, SendOption.None, viewer.OwnerId);
            writer.Write((byte)1);
            writer.Write((byte)r.Kind);
            writer.Write(r.Pos.x);
            writer.Write(r.Pos.y);
            writer.Write(r.Radius);
            EarlyWarning.OnPacket("PlayVisualFx", writer.Length, writer.Length, "None");
            AmongUsClient.Instance.FinishRpcImmediately(writer);
        }
        catch (System.Exception e) { Utils.ThrowException(e); }
    }

    // 凍結系は Radius に効果秒数を載せるので、効果時間の設定の上限 (180 秒) まで通す
    private static float ClampRadius(Kind kind, float radius)
    {
        return kind is Kind.Freeze or Kind.TimeStop ? FxMath.Clamp(radius, 0.3f, 180f) : FxMath.Clamp(radius, 0.3f, 15f);
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

            SpawnLocal(new Request(kind, new Vector2(x, y), ClampRadius(kind, radius)));
        }
    }

    internal static void Tick()
    {
        try
        {
            if (!GameStates.InGame)
            {
                _warm = false;
                if (Pending.Count > 0) Pending.Clear();
                if (Unsent.Count > 0) Unsent.Clear();
                if (Active.Count > 0) ClearAll();
                return;
            }

            var alloc = AllocProbe.Now();

            // 素材は試合が始まった時点 (イントロ中) に作っておき、最初の 1 発が引っかからないようにする
            if (!_warm)
            {
                _warm = true;
                EnsureSprites();

                // 起動後最初の 1 発はコードのコンパイル待ちでも引っかかるので、演出の関数を先にコンパイルしておく (起動ごとに 1 回)
                if (!_jitted)
                {
                    _jitted = true;

                    const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly;

                    foreach (System.Type type in new[] { typeof(ExplosionFx), typeof(FxMath) })
                    foreach (System.Reflection.MethodInfo m in type.GetMethods(flags))
                    {
                        if (m.IsGenericMethodDefinition || m.IsAbstract) continue;
                        try { System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(m.MethodHandle); }
                        catch { }
                    }
                }

                // 粒子の入れ物も先に用意しておく (1 発目で数百個を一度に作ると引っかかる)。前の試合の残りは使い回すので足りない分だけ
                if (Pool.Count > 0)
                {
                    var alive = new List<(GameObject Go, SpriteRenderer Sr)>(Pool.Count);
                    foreach ((GameObject Go, SpriteRenderer Sr) e in Pool)
                        if (e.Go && e.Sr) alive.Add(e);

                    Pool.Clear();
                    foreach ((GameObject Go, SpriteRenderer Sr) e in alive) Pool.Push(e);
                }

                while (Pool.Count < WarmPool)
                {
                    var go = new GameObject("ExplosionFx") { layer = 0 };
                    SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
                    go.SetActive(false);
                    Pool.Push((go, sr));
                }

                alloc = AllocProbe.Mark("fx.warm", alloc);
            }

            // 凍結の氷のように効果時間いっぱい残る演出があるので、会議が始まったら残りを消す
            if (Active.Count > 0 && GameStates.IsMeeting) ClearAll();

            if (Pending.Count > 0)
            {
                Flush();
                alloc = AllocProbe.Mark("fx.spawn", alloc);
            }

            if (Unsent.Count > 0 && Time.unscaledTime - _lastSendTime >= SendInterval) Send();

            // 自分がこの爆発で死んだ時はキル演出が画面を覆うので、明けるまで演出を止めておいて後から見せる
            if (Active.Count > 0 && Time.time >= _holdUntil && !KillOverlayOpen())
            {
                alloc = AllocProbe.Now();

                // 粒子の生存確認は毎フレームせず、外から壊されていて触れなくなった時だけ全部捨てて出直す
                try { Animate(Time.deltaTime); }
                catch (System.Exception e)
                {
                    Logger.Warn($"particles lost, clearing: {e}", "ExplosionFx");
                    ClearAll();
                }

                AllocProbe.Mark("fx.anim", alloc);
            }
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
                if (!r.LocalOnly && Unsent.Count < MaxPendingPerFrame) Unsent.Add(r);
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
            bool lethal = r.Kind is Kind.Fire or Kind.Supernova;
            if (lethal && lp && lp.IsAlive() && Vector2.Distance(lp.GetTruePosition(), r.Pos) <= r.Radius + 0.5f) _holdUntil = Time.time + 0.35f;

            Logger.Info($"{r.Kind} at ({r.Pos.x:F2}, {r.Pos.y:F2}) r={r.Radius:F1}", "ExplosionFx");

            // 知らない種類 (新しい版のホストが送ってきたもの) は描かない
            switch (r.Kind)
            {
                case Kind.Fire:
                    SpawnFire(r.Pos, r.Radius);
                    break;
                case Kind.Supernova:
                    SpawnSupernova(r.Pos, r.Radius);
                    break;
                case Kind.GustRight:
                case Kind.GustLeft:
                    SpawnGust(r.Pos, r.Radius, r.Kind == Kind.GustRight ? 1f : -1f);
                    break;
                case Kind.WindTrailRight:
                case Kind.WindTrailLeft:
                    SpawnWindTrail(r.Pos, r.Radius, r.Kind == Kind.WindTrailRight ? 1f : -1f);
                    break;
                case Kind.GeminiSplit:
                    SpawnGeminiSplit(r.Pos);
                    break;
                case Kind.GeminiShatter:
                    SpawnGeminiShatter(r.Pos);
                    break;
                case Kind.WarpOut:
                    SpawnWarpOut(r.Pos);
                    break;
                case Kind.WarpIn:
                    SpawnWarpIn(r.Pos);
                    break;
                case Kind.Freeze:
                    SpawnFreeze(r.Pos, r.Radius);
                    break;
                case Kind.TimeStop:
                    SpawnTimeStop(r.Pos, r.Radius);
                    break;
                case Kind.LightningStrike:
                    SpawnLightningStrike(r.Pos);
                    break;
                case Kind.Slam:
                    SpawnSlam(r.Pos, r.Radius);
                    break;
                case Kind.Splash:
                    SpawnSplash(r.Pos, r.Radius);
                    break;
            }
        }
        catch (System.Exception e) { Utils.ThrowException(e); }
    }

    // ── 演出の中身 ─────────────────────────────────────────────────────

    private static float Rnd(float min, float max) => FxMath.Range(min, max);

    private static Vector2 Dir()
    {
        float ang = FxMath.Range(0f, 2f * FxMath.PI);
        return FxMath.V2(FxMath.Cos(ang), FxMath.Sin(ang));
    }

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
            Color col = nebula[FxMath.Range(0, nebula.Length)];
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
            Color col = starTints[FxMath.Range(0, starTints.Length)];
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
            Add(Shape.Star, c + FxMath.InsideUnitCircle() * r * 1.3f, FxMath.InsideUnitCircle() * r * 0.15f, Rnd(2.5f, 4.3f), Rnd(0.08f, 0.2f), Rnd(0.03f, 0.08f),
                starTints[FxMath.Range(0, starTints.Length)], lavender, 1f, 0.15f, 0.55f,
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
            Add(Shape.Cloud, c + FxMath.InsideUnitCircle() * r * 0.5f, FxMath.InsideUnitCircle() * r * 0.35f, Rnd(2.4f, 3.6f), r * Rnd(0.3f, 0.5f), r * Rnd(1f, 1.5f),
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
            float out01 = FxMath.Value;
            Add(Shape.Flame, c + d * Rnd(0f, r * 0.25f), d * r * FxMath.Lerp(0.4f, 2f, out01), FxMath.Lerp(1.9f, 1.1f, out01), r * Rnd(0.3f, 0.45f), r * Rnd(0.8f, 1.25f),
                Color.Lerp(white, yellow, out01), red, 1f, 0.02f, 0.55f, drag: 2.6f, delay: Rnd(0f, 0.06f), spin: Rnd(-120f, 120f),
                colorMid: Color.Lerp(yellow, orange, 0.5f + 0.5f * out01));
        }

        // 誘爆
        for (int b = 0; b < 4; b++)
        {
            Vector2 at = c + FxMath.InsideUnitCircle() * r * 0.75f;
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

    // 風の色 (横風の突風と軌跡で共用)。明るい床の上でも筋が見えるよう、白ではなく澄んだ水色を主にする
    private static readonly Color WindWhite = new(1f, 1f, 1f);
    private static readonly Color WindMint = new(0.45f, 0.95f, 0.85f);
    private static readonly Color WindSky = new(0.3f, 0.72f, 1f);
    private static readonly Color WindDeep = new(0.12f, 0.45f, 0.85f);
    private static readonly Color Dust = new(0.78f, 0.66f, 0.48f);
    private static readonly Color DustDark = new(0.5f, 0.42f, 0.32f);

    private static Color WindColor(int i) => (i % 3) switch { 0 => WindWhite, 1 => WindMint, _ => WindSky };

    // 足元で渦を巻く突風 → 押し出す向きへ走る風の筋と巻き上がる砂ぼこり → 画面を横切る風の帯と木の葉。
    // s は向き (+1 = 右 / -1 = 左)。光条のスプライトは根元が明るいので、根元を進行方向に向けて尾を後ろへ引かせる。
    private static void SpawnGust(Vector2 c, float r, float s)
    {
        Vector2 d = new(s, 0f);
        float back = s > 0f ? 180f : 0f;
        Color[] leaves = [new(0.4f, 0.75f, 0.25f), new(0.65f, 0.82f, 0.2f), new(0.95f, 0.62f, 0.15f), new(0.85f, 0.35f, 0.15f)];

        // 渦の芯 (回転する霞) と、潰した輪を交互に逆回転させた竜巻の断面
        Add(Shape.Cloud, c, Vector2.zero, 1.1f, 0.8f, 3.2f, WindSky, WindDeep, 0.45f, 0.04f, 0.4f, spin: -520f * s, sy0: 0.5f, sy1: 2f);

        for (int k = 0; k < 4; k++)
        {
            float size = 1.3f + k * 0.8f;
            Add(Shape.Ring, c, Vector2.zero, 0.9f + k * 0.15f, size * 0.4f, size, WindColor(k), WindDeep, 0.95f - k * 0.12f, 0.04f, 0.4f,
                delay: k * 0.05f, spin: (k % 2 == 0 ? -460f : 320f) * s, sy0: size * 0.4f * 0.35f, sy1: size * 0.35f);
        }

        Add(Shape.Glow, c, Vector2.zero, 0.5f, 2.6f, 1.6f, WindWhite, WindSky, 0.7f, 0.02f, 0.3f);

        // 発動者から吹き出す風の筋
        for (int i = 0; i < 40; i++)
        {
            Vector2 at = c + new Vector2(-s * Rnd(0f, 1f), Rnd(-1.4f, 1.4f));
            float len = Rnd(1.4f, 3.2f);
            Add(Shape.Ray, at, d * Rnd(r * 1.8f, r * 3.2f), Rnd(0.5f, 0.9f), len * 0.4f, len, WindColor(i), WindDeep, Rnd(0.75f, 1f), 0.03f, 0.45f,
                drag: 1.2f, delay: Rnd(0f, 0.12f), rot: back, sy0: Rnd(0.12f, 0.22f), sy1: 0.06f);
        }

        // 巻き上がる砂ぼこり
        for (int i = 0; i < 18; i++)
        {
            Add(Shape.Cloud, c + new Vector2(Rnd(-0.6f, 0.6f), Rnd(-0.5f, 0.1f)), d * Rnd(2f, 5.5f) + new Vector2(0f, Rnd(-0.5f, 0.8f)), Rnd(0.9f, 1.5f), Rnd(0.3f, 0.5f), Rnd(1.1f, 1.9f),
                Dust, DustDark, Rnd(0.55f, 0.75f), 0.05f, 0.35f, drag: 2f, spin: Rnd(-90f, 90f), rise: 0.3f);
        }

        // 突風はマップ中の全員を押し流すので、どこにいる人の画面にも横切る風を流す (自分の画面だけ・送信なし)
        Camera cam = Camera.main;
        if (!cam) return;

        Vector2 cp = cam.transform.position;
        float halfH = cam.orthographicSize;
        float halfW = halfH * cam.aspect;
        float span = halfW * 2f + 10f;

        // 風の帯 (横に引き伸ばした霞が画面を駆け抜ける)
        for (int i = 0; i < 7; i++)
        {
            float life = Rnd(0.8f, 1.1f);
            float w = Rnd(5f, 8f);
            Vector2 at = new(cp.x - s * (halfW + w * 0.5f + Rnd(0f, 2f)), cp.y + Rnd(-halfH * 0.9f, halfH * 0.9f));
            Add(Shape.Cloud, at, d * span / life, life, w, w, i % 2 == 0 ? WindSky : WindMint, WindDeep, Rnd(0.18f, 0.28f), 0.15f, 0.6f,
                delay: Rnd(0f, 0.5f), rot: 0f, sy0: Rnd(0.8f, 1.4f), sy1: Rnd(0.8f, 1.4f));
        }

        for (int i = 0; i < 64; i++)
        {
            float life = Rnd(0.5f, 0.85f);
            float len = Rnd(2.5f, 5.5f);
            Vector2 at = new(cp.x - s * (halfW + Rnd(1f, 5f)), cp.y + Rnd(-halfH, halfH));
            Add(Shape.Ray, at, d * span / life, life, len, len, WindColor(i), WindSky, Rnd(0.5f, 0.85f), 0.1f, 0.6f,
                delay: Rnd(0.05f, 0.8f), rot: back, sy0: Rnd(0.08f, 0.16f), sy1: 0.08f);
        }

        // 風にさらわれて舞う木の葉
        for (int i = 0; i < 22; i++)
        {
            float life = Rnd(0.9f, 1.4f);
            float size = Rnd(0.26f, 0.4f);
            Vector2 at = new(cp.x - s * (halfW + Rnd(0.5f, 2.5f)), cp.y + Rnd(-halfH, halfH));
            Color col = leaves[FxMath.Range(0, leaves.Length)];
            Add(Shape.Chunk, at, d * span * Rnd(0.6f, 0.8f) / life + new Vector2(0f, Rnd(-1.5f, 1.5f)), life, size, size, col, Color.Lerp(col, DustDark, 0.3f), 1f, 0.05f, 0.75f,
                delay: Rnd(0.1f, 0.7f), spin: Rnd(-900f, 900f), sy0: size * 0.55f, sy1: size * 0.55f);
        }
    }

    // 吹き飛ばされた人の軌跡: 出発点の砂ぼこり → 着地点まで伸びる残像と風の筋 → 着地の土煙
    private static void SpawnWindTrail(Vector2 a, float len, float s)
    {
        Vector2 d = new(s, 0f);
        float back = s > 0f ? 180f : 0f;
        Vector2 b = a + d * len;
        Vector2 feet = new(0f, -0.35f);

        // 出発点の砂ぼこり (風下へ置いていかれる)
        for (int i = 0; i < 10; i++)
        {
            Add(Shape.Cloud, a + feet + FxMath.InsideUnitCircle() * 0.3f, -d * Rnd(0.3f, 1.2f) + new Vector2(0f, Rnd(0f, 0.6f)), Rnd(0.8f, 1.2f), Rnd(0.3f, 0.45f), Rnd(0.9f, 1.4f),
                Dust, DustDark, Rnd(0.55f, 0.7f), 0.05f, 0.35f, drag: 2f, spin: Rnd(-60f, 60f));
        }

        // 軌跡に残る残像 (出発点ほど薄く、早く消える)
        for (int i = 0; i < 7; i++)
        {
            float u = (i + 1) / 8f;
            Add(Shape.Glow, Vector2.Lerp(a, b, u), Vector2.zero, 0.4f + u * 0.35f, 1f, 1.2f, WindMint, WindSky, 0.3f + 0.35f * u, 0.02f, 0.2f,
                delay: u * 0.08f, sy0: 1.4f, sy1: 1.6f);
        }

        // 軌跡に沿った風の筋
        for (int i = 0; i < 18; i++)
        {
            Vector2 at = Vector2.Lerp(a, b, Rnd(0.2f, 1f)) + new Vector2(0f, Rnd(-0.65f, 0.65f));
            float l = Rnd(1.2f, len * 0.6f + 1.5f);
            Add(Shape.Ray, at, d * Rnd(2f, 4f), Rnd(0.45f, 0.75f), l, l * 0.7f, WindColor(i), WindDeep, Rnd(0.7f, 1f), 0.02f, 0.4f,
                drag: 1.5f, delay: Rnd(0f, 0.1f), rot: back, sy0: Rnd(0.1f, 0.18f), sy1: 0.05f);
        }

        // 着地: 地面に広がる輪と、横へ流れる土煙
        Add(Shape.Ring, b + feet, Vector2.zero, 0.6f, 0.3f, 2f, WindWhite, WindSky, 0.9f, 0.02f, 0.35f, delay: 0.08f, rot: 0f, sy0: 0.1f, sy1: 0.7f);

        for (int i = 0; i < 10; i++)
        {
            Add(Shape.Cloud, b + feet + new Vector2(Rnd(-0.3f, 0.3f), Rnd(-0.1f, 0.1f)), new Vector2(Rnd(-1.5f, 1.5f) + s * Rnd(0.5f, 1.5f), Rnd(0f, 0.5f)),
                Rnd(0.8f, 1.2f), Rnd(0.3f, 0.45f), Rnd(0.9f, 1.4f), Dust, DustDark, Rnd(0.55f, 0.7f), 0.05f, 0.35f, drag: 2.5f, delay: Rnd(0.05f, 0.15f), spin: Rnd(-60f, 60f));
        }
    }

    // ジェミニの色 (シアンとマゼンタの双子)。明るい床の上でも沈まないよう彩度を高めにとる
    private static readonly Color GeminiCyan = new(0.1f, 0.85f, 1f);
    private static readonly Color GeminiMagenta = new(1f, 0.25f, 0.85f);
    private static readonly Color GeminiLavender = new(0.7f, 0.6f, 1f);
    private static readonly Color GeminiViolet = new(0.45f, 0.25f, 0.9f);

    // 分身が生まれる: 左右から寄って重なるシアンとマゼンタの残像 → 鏡面の縦の光 → 重なった瞬間の閃き
    // → 足元に広がる二重の輪 → 双子星のきらめき
    private static void SpawnGeminiSplit(Vector2 c)
    {
        Color white = new(1f, 1f, 1f);

        // 残像は drag で止まるまでに v0 / drag = 1.2 単位進む → ちょうど中心で重なる。
        // 人の大きさの縦長の光を芯 (濃い) と外套 (淡い) の 2 枚重ねにする
        for (int k = -1; k <= 1; k += 2)
        {
            Color col = k < 0 ? GeminiCyan : GeminiMagenta;
            Vector2 from = c + new Vector2(1.2f * k, 0f);
            Vector2 v = new(-7.2f * k, 0f);
            Add(Shape.Cloud, from, v, 1.1f, 1.1f, 1f, col, GeminiViolet, 0.75f, 0.03f, 0.45f, drag: 6f, rot: 0f, sy0: 1.9f, sy1: 1.6f);
            Add(Shape.Glow, from, v, 1.1f, 1.8f, 1.4f, col, GeminiLavender, 1f, 0.03f, 0.4f, drag: 6f, sy0: 2.8f, sy1: 2.2f);
        }

        Add(Shape.Ray, c, Vector2.zero, 0.8f, 0.4f, 2.4f, white, GeminiCyan, 1f, 0.02f, 0.35f, rot: 90f, sy0: 0.2f, sy1: 0.05f);
        Add(Shape.Ray, c, Vector2.zero, 0.8f, 0.4f, 2.4f, white, GeminiMagenta, 1f, 0.02f, 0.35f, rot: 270f, sy0: 0.2f, sy1: 0.05f);

        Add(Shape.Star, c, Vector2.zero, 0.9f, 0.5f, 2.6f, white, GeminiLavender, 1f, 0.02f, 0.3f, delay: 0.3f, spin: 90f);
        Add(Shape.Glow, c, Vector2.zero, 0.8f, 2.4f, 1.6f, white, GeminiLavender, 1f, 0.02f, 0.3f, delay: 0.3f);

        Add(Shape.Ring, c + new Vector2(0f, -0.4f), Vector2.zero, 1f, 0.2f, 2.8f, GeminiCyan, GeminiViolet, 1f, 0.02f, 0.4f, delay: 0.3f, rot: 0f, sy0: 0.07f, sy1: 1f);
        Add(Shape.Ring, c + new Vector2(0f, -0.4f), Vector2.zero, 1.1f, 0.2f, 2.2f, GeminiMagenta, GeminiViolet, 0.9f, 0.02f, 0.4f, delay: 0.4f, rot: 0f, sy0: 0.07f, sy1: 0.8f);
        Add(Shape.Ring, c, Vector2.zero, 0.6f, 0.3f, 3f, white, GeminiLavender, 0.8f, 0.01f, 0.3f, delay: 0.3f);

        for (int i = 0; i < 40; i++)
        {
            Color col = (i % 3) switch { 0 => GeminiCyan, 1 => GeminiMagenta, _ => white };
            Add(Shape.Star, c + FxMath.InsideUnitCircle() * 1.1f, new Vector2(Rnd(-0.4f, 0.4f), Rnd(0.3f, 1f)), Rnd(1.1f, 1.8f), Rnd(0.18f, 0.34f), Rnd(0.06f, 0.1f),
                col, GeminiLavender, 1f, 0.1f, 0.55f, delay: Rnd(0.25f, 0.6f), spin: Rnd(-60f, 60f), twinkle: 0.8f, twinkleSpeed: Rnd(10f, 18f));
        }
    }

    // 分身が切られて砕ける: 閃光と輪 → ガラス片のように飛び散って落ちる破片 → きらめきと淡い煙
    private static void SpawnGeminiShatter(Vector2 c)
    {
        Color white = new(1f, 1f, 1f);

        Add(Shape.Glow, c, Vector2.zero, 0.45f, 2.4f, 1.6f, white, GeminiLavender, 1f, 0.01f, 0.3f);
        Add(Shape.Ring, c, Vector2.zero, 0.5f, 0.2f, 3f, white, GeminiCyan, 1f, 0.01f, 0.35f);
        Add(Shape.Ring, c, Vector2.zero, 0.7f, 0.2f, 2.4f, GeminiMagenta, GeminiViolet, 0.8f, 0.02f, 0.35f, delay: 0.06f);

        for (int i = 0; i < 34; i++)
        {
            Vector2 d = Dir();
            float size = Rnd(0.16f, 0.34f);
            Color col = Color.Lerp(i % 2 == 0 ? GeminiCyan : GeminiMagenta, white, Rnd(0.1f, 0.45f));
            Add(Shape.Chunk, c + d * 0.2f, d * Rnd(2.2f, 5.5f) + new Vector2(0f, 1.4f), Rnd(0.9f, 1.4f), size, size * 0.7f, col, GeminiViolet, 1f, 0.01f, 0.6f,
                drag: 2.2f, spin: Rnd(-720f, 720f), sy0: size * Rnd(0.35f, 0.7f), sy1: size * 0.3f, rise: -1.4f);
        }

        for (int i = 0; i < 30; i++)
        {
            Vector2 d = Dir();
            Add(Shape.Star, c, d * Rnd(2.5f, 6.5f), Rnd(0.5f, 1f), Rnd(0.24f, 0.4f), 0.08f, white, i % 2 == 0 ? GeminiCyan : GeminiMagenta, 1f, 0.01f, 0.5f,
                drag: 3f, stretch: 0.1f, twinkle: 0.4f, twinkleSpeed: Rnd(18f, 28f));
        }

        for (int i = 0; i < 8; i++)
        {
            Add(Shape.Cloud, c + FxMath.InsideUnitCircle() * 0.4f, FxMath.InsideUnitCircle() * 0.6f, Rnd(1f, 1.5f), 0.5f, Rnd(1.3f, 1.8f), GeminiLavender, GeminiViolet,
                Rnd(0.35f, 0.5f), 0.05f, 0.35f, drag: 1.5f, delay: Rnd(0.05f, 0.15f), spin: Rnd(-40f, 40f), rise: 0.3f);
        }
    }

    private static readonly Vector2 Feet = new(0f, -0.35f);

    private static readonly Color WarpCyan = new(0.35f, 0.95f, 1f);
    private static readonly Color WarpViolet = new(0.6f, 0.4f, 1f);
    private static readonly Color WarpPink = new(1f, 0.5f, 0.95f);

    private static Color WarpColor(int i) => (i % 4) switch { 0 => WindWhite, 1 => WarpCyan, 2 => WarpViolet, _ => WarpPink };

    // 明るい床でも沈まないよう、飛び散る粒は白を混ぜず彩度の高い色だけで出す
    private static Color WarpVivid(int i) => (i % 3) switch { 0 => WarpCyan, 1 => WarpViolet, _ => WarpPink };

    // 消える: 閃光 → 体を包む縦長の光が細く絞られる → 天へ昇る二重の光の柱 → 足元で縮む三重の輪
    // → 渦を巻いて吸い込まれる光の粒 → 最後に天へ打ち上がる光の粒
    private static void SpawnWarpOut(Vector2 c)
    {
        Impact(c, 1.5f, WarpCyan, 0.35f, 0.1f, 0.25f);

        Add(Shape.Glow, c, Vector2.zero, 0.8f, 2.2f, 0.15f, WindWhite, WarpCyan, 1f, 0.02f, 0.5f, sy0: 3.2f, sy1: 4.2f);
        Add(Shape.Cloud, c, Vector2.zero, 0.7f, 1.6f, 0.2f, WarpCyan, WarpViolet, 0.8f, 0.03f, 0.5f, rot: 0f, sy0: 2.4f, sy1: 3.2f);

        // 光条は根元が明るいので、根元を足元に置いて上へ向ける
        Add(Shape.Ray, c + Feet, Vector2.zero, 0.9f, 7f, 12f, WindWhite, WarpCyan, 1f, 0.02f, 0.4f, rot: 90f, sy0: 1.1f, sy1: 0.1f);
        Add(Shape.Ray, c + Feet, Vector2.zero, 1f, 5f, 10f, WarpViolet, WarpPink, 0.7f, 0.02f, 0.45f, delay: 0.05f, rot: 90f, sy0: 2f, sy1: 0.3f);

        for (int k = 0; k < 3; k++)
        {
            float from = 3.6f - k * 0.7f;
            Add(Shape.Ring, c + Feet, Vector2.zero, 0.55f + k * 0.1f, from, 0.2f, k == 1 ? WarpViolet : WarpCyan, WarpPink, 1f, 0.02f, 0.5f,
                delay: k * 0.07f, rot: 0f, sy0: from * 0.35f, sy1: 0.07f);
        }

        // 接線方向の速さと中心へ向かう速さを混ぜると、減速しながら渦を巻いて吸い込まれる
        for (int i = 0; i < 48; i++)
        {
            Vector2 d = Dir();
            Vector2 tangent = FxMath.V2(-d.y, d.x);
            float size = Rnd(0.3f, 0.46f);
            Add(Shape.Star, c + d * Rnd(1.2f, 2.4f), -d * Rnd(3f, 5f) + tangent * Rnd(3f, 5f) + new Vector2(0f, Rnd(0.5f, 2f)), Rnd(0.5f, 0.8f), size * 1.5f, size * 0.7f, WarpVivid(i), WarpViolet, 1f,
                0.05f, 0.5f, drag: 1.4f, stretch: 0.1f, twinkle: 0.4f, twinkleSpeed: Rnd(14f, 24f));
        }

        for (int i = 0; i < 30; i++)
        {
            Add(Shape.Star, c + new Vector2(Rnd(-0.35f, 0.35f), Rnd(-0.5f, 0.6f)), new Vector2(Rnd(-0.5f, 0.5f), Rnd(7f, 13f)), Rnd(0.6f, 1f), Rnd(0.5f, 0.75f), 0.26f,
                WarpVivid(i), WarpCyan, 1f, 0.02f, 0.5f, delay: Rnd(0.2f, 0.4f), stretch: 0.07f);
        }
    }

    // 現れる: 天から叩きつけるように降りる二重の光の柱 → 強い閃光と光の星 → 足元に広がる三重の輪と空へ広がる衝撃波
    // → 四方へ飛び散る火花 → しばらく漂って瞬く光の粒
    private static void SpawnWarpIn(Vector2 c)
    {
        Impact(c, 2f, WarpCyan, 0.5f, 0.16f, 0.3f);

        Add(Shape.Ray, c + Feet, Vector2.zero, 0.9f, 12f, 7f, WindWhite, WarpCyan, 1f, 0.01f, 0.35f, rot: 90f, sy0: 1.8f, sy1: 0.15f);
        Add(Shape.Ray, c + Feet, Vector2.zero, 1f, 10f, 6f, WarpViolet, WarpPink, 0.75f, 0.01f, 0.4f, rot: 90f, sy0: 3f, sy1: 0.3f);
        Add(Shape.Glow, c, Vector2.zero, 0.6f, 0.4f, 3.4f, WindWhite, WarpCyan, 1f, 0.02f, 0.3f, sy0: 0.8f, sy1: 4.4f);
        Add(Shape.Star, c, Vector2.zero, 0.7f, 0.4f, 4f, WindWhite, WarpViolet, 1f, 0.02f, 0.3f, spin: 120f);
        Add(Shape.Star, c, Vector2.zero, 0.6f, 0.3f, 2.8f, WarpPink, WarpViolet, 0.8f, 0.02f, 0.3f, rot: 45f, spin: -90f);
        Add(Shape.Ring, c, Vector2.zero, 0.6f, 0.4f, 5.5f, WindWhite, WarpCyan, 0.8f, 0.01f, 0.35f);

        for (int k = 0; k < 3; k++)
        {
            float to = 3.6f + k * 1f;
            Add(Shape.Ring, c + Feet, Vector2.zero, 0.8f + k * 0.12f, 0.3f, to, k == 1 ? WarpViolet : WarpCyan, WarpPink, 1f - k * 0.15f, 0.02f, 0.4f,
                delay: k * 0.08f, rot: 0f, sy0: 0.1f, sy1: to * 0.35f);
        }

        for (int i = 0; i < 56; i++)
        {
            Add(Shape.Star, c, Dir() * Rnd(3f, 8f), Rnd(0.5f, 0.9f), Rnd(0.5f, 0.75f), 0.24f, WarpVivid(i), WarpViolet, 1f, 0.01f, 0.5f,
                drag: 2.5f, stretch: 0.1f, twinkle: 0.3f, twinkleSpeed: Rnd(16f, 26f));
        }

        for (int i = 0; i < 24; i++)
        {
            Add(Shape.Star, c + FxMath.InsideUnitCircle() * 1.4f, new Vector2(Rnd(-0.2f, 0.2f), Rnd(0.3f, 0.9f)), Rnd(1.2f, 2f), Rnd(0.36f, 0.54f), 0.18f,
                WarpVivid(i), WarpCyan, 1f, 0.1f, 0.55f, delay: Rnd(0.1f, 0.5f), twinkle: 0.8f, twinkleSpeed: Rnd(8f, 14f));
        }
    }

    private static readonly Color IceWhite = new(0.9f, 0.97f, 1f);
    private static readonly Color IceCyan = new(0.6f, 0.9f, 1f);
    private static readonly Color IceBlue = new(0.3f, 0.65f, 1f);

    // 凍りつく瞬間: 白い閃光と、床を這って広がる霜の輪と冷気 → 凍っている間ずっと残る氷の膜・生えた結晶・舞い落ちる雪・きらめき
    // → 解ける瞬間に砕け散る。残る粒は寿命 = 凍結時間なので、フェードの割合は秒数から割り戻す (長い凍結でも出入りは一瞬にする)
    private static void SpawnFreeze(Vector2 c, float duration)
    {
        float d = FxMath.Clamp(duration, 0.5f, 180f);
        float fin = 0.12f / d;
        float fout = 1f - 0.12f / d;

        // 砕ける演出は氷が消えきる少し前から重ねる (間に何も無いコマを作らない)
        float br = FxMath.Max(d - 0.1f, 0f);

        Impact(c, 1.5f, IceWhite, 0.35f, 0.08f, 0.2f);

        Add(Shape.Glow, c, Vector2.zero, 0.5f, 0.4f, 3.2f, WindWhite, IceBlue, 1f, 0.02f, 0.3f);
        Add(Shape.Star, c, Vector2.zero, 0.5f, 0.4f, 3f, WindWhite, IceCyan, 1f, 0.02f, 0.3f, rot: 45f);
        Add(Shape.Ring, c + Feet, Vector2.zero, 0.9f, 0.3f, 5f, IceWhite, IceBlue, 1f, 0.02f, 0.45f, rot: 0f, sy0: 0.1f, sy1: 1.75f);
        Add(Shape.Ring, c + Feet, Vector2.zero, 1.1f, 0.3f, 3.4f, IceCyan, IceBlue, 0.8f, 0.02f, 0.45f, delay: 0.1f, rot: 0f, sy0: 0.1f, sy1: 1.2f);

        for (int i = 0; i < 16; i++)
        {
            float ang = Rnd(0f, 2f * FxMath.PI);
            Vector2 dir = FxMath.V2(FxMath.Cos(ang), FxMath.Sin(ang) * 0.4f);
            Add(Shape.Cloud, c + Feet, dir * Rnd(3f, 5f), Rnd(1f, 1.5f), Rnd(0.4f, 0.6f), Rnd(1.2f, 1.8f), IceWhite, IceBlue, 0.6f, 0.05f, 0.35f,
                drag: 2.2f, spin: Rnd(-60f, 60f));
        }

        // 霜の欠片が床を走る
        for (int i = 0; i < 14; i++)
        {
            float ang = Rnd(0f, 2f * FxMath.PI);
            Add(Shape.Star, c + Feet, FxMath.V2(FxMath.Cos(ang), FxMath.Sin(ang) * 0.4f) * Rnd(4f, 7f), Rnd(0.5f, 0.8f), Rnd(0.36f, 0.5f), 0.16f, WindWhite, IceCyan, 1f, 0.02f, 0.5f,
                drag: 3f, stretch: 0.08f);
        }

        Add(Shape.Glow, c, Vector2.zero, d, 1.5f, 1.5f, IceCyan, IceBlue, 0.65f, fin, fout, sy0: 2.1f, sy1: 2.1f);
        Add(Shape.Cloud, c, Vector2.zero, d, 1.3f, 1.3f, IceWhite, IceCyan, 0.35f, fin, fout, spin: 6f, sy0: 1.9f, sy1: 1.9f);
        Add(Shape.Cloud, c + Feet, Vector2.zero, d, 2.4f, 2.4f, IceWhite, IceCyan, 0.35f, fin, fout, rot: 0f, sy0: 0.8f, sy1: 0.8f);
        Add(Shape.Ring, c + Feet, Vector2.zero, d, 2.2f, 2.2f, IceWhite, IceCyan, 0.6f, fin, fout, rot: 0f, sy0: 0.75f, sy1: 0.75f);

        // 体を囲んで床から突き出した細長い結晶
        for (int i = 0; i < 12; i++)
        {
            float size = Rnd(0.35f, 0.7f);
            float x = Rnd(-0.75f, 0.75f);
            Add(Shape.Chunk, c + new Vector2(x, Rnd(-0.7f, 0.15f)), Vector2.zero, d, size * 0.6f, size * 0.6f, IceWhite, IceCyan, 0.9f, fin, fout,
                rot: -x * 30f + Rnd(-12f, 12f), sy0: size * 0.9f, sy1: size * Rnd(1.6f, 2.4f));
        }

        for (int i = 0; i < 10; i++)
        {
            Add(Shape.Star, c + new Vector2(Rnd(-0.7f, 0.7f), Rnd(-0.7f, 0.9f)), Vector2.zero, d, 0.28f, 0.28f, WindWhite, IceCyan, 1f, fin, fout,
                delay: Rnd(0f, 0.3f), twinkle: 0.9f, twinkleSpeed: Rnd(3f, 7f));
        }

        // 周りに舞い落ちる雪 (凍結中ずっと、上から落ちては消える)
        for (int i = 0; i < 16; i++)
        {
            float t0 = Rnd(0f, FxMath.Max(d - 1.5f, 0.1f));
            Add(Shape.Glow, c + new Vector2(Rnd(-1.4f, 1.4f), Rnd(1.2f, 2f)), new Vector2(Rnd(-0.2f, 0.2f), -Rnd(0.8f, 1.4f)), Rnd(1.5f, 2.2f), 0.14f, 0.1f, WindWhite, IceCyan, 0.9f,
                0.1f, 0.6f, delay: t0);
        }

        Add(Shape.Ring, c, Vector2.zero, 0.6f, 0.2f, 4f, WindWhite, IceBlue, 1f, 0.01f, 0.35f, delay: br);
        Add(Shape.Glow, c, Vector2.zero, 0.4f, 2.8f, 1.2f, WindWhite, IceCyan, 1f, 0.01f, 0.3f, delay: br);

        for (int i = 0; i < 24; i++)
        {
            Vector2 dir = Dir();
            float size = Rnd(0.18f, 0.4f);
            Add(Shape.Chunk, c + dir * 0.2f, dir * Rnd(3f, 7f) + new Vector2(0f, 1.5f), Rnd(0.8f, 1.3f), size, size * 0.7f, IceWhite, IceBlue, 1f, 0.01f, 0.6f,
                drag: 2.2f, delay: br, spin: Rnd(-720f, 720f), sy0: size * Rnd(0.35f, 0.7f), sy1: size * 0.3f, rise: -1.8f);
        }

        for (int i = 0; i < 12; i++)
        {
            Add(Shape.Star, c, Dir() * Rnd(3f, 7f), Rnd(0.4f, 0.8f), Rnd(0.4f, 0.55f), 0.18f, WindWhite, IceCyan, 1f, 0.01f, 0.5f,
                drag: 3f, delay: br, stretch: 0.1f);
        }

        for (int i = 0; i < 6; i++)
        {
            Add(Shape.Cloud, c + FxMath.InsideUnitCircle() * 0.5f, FxMath.InsideUnitCircle() * 0.8f, Rnd(1f, 1.5f), 0.6f, Rnd(1.5f, 2f), IceWhite, IceCyan,
                0.5f, 0.05f, 0.35f, drag: 1.5f, delay: br + Rnd(0.02f, 0.1f), spin: Rnd(-40f, 40f), rise: 0.3f);
        }
    }

    private static readonly Color TimeGold = new(1f, 0.85f, 0.45f);
    private static readonly Color TimePale = new(0.75f, 0.85f, 1f);
    private static readonly Color TimeDeep = new(0.25f, 0.3f, 0.6f);

    // 時間が止まる: 強い閃光と、世界を覆っていく五重の輪 → 止まっている間、画面全体の青い色味・頭上で針の止まった大時計・宙に止まった塵
    // → 動き出す瞬間に時計が縮んで弾け、もう一度閃く
    private static void SpawnTimeStop(Vector2 c, float duration)
    {
        float d = FxMath.Clamp(duration, 0.5f, 180f);
        float fin = 0.2f / d;
        float fout = 1f - 0.4f / d;

        Impact(c, 4f, TimePale, 0.7f, 0.22f, 0.5f);

        for (int k = 0; k < 5; k++)
            Add(Shape.Ring, c, Vector2.zero, 1.1f + k * 0.2f, 0.5f, 24f + k * 5f, k % 2 == 1 ? TimeGold : WindWhite, TimeDeep, 1f - k * 0.15f, 0.02f, 0.4f, delay: k * 0.1f);

        Add(Shape.Star, c, Vector2.zero, 0.8f, 0.5f, 5f, WindWhite, TimeGold, 1f, 0.02f, 0.3f, spin: 60f);
        Add(Shape.Glow, c, Vector2.zero, 0.7f, 4f, 2f, WindWhite, TimeGold, 1f, 0.02f, 0.3f);
        Add(Shape.Solid, c, Vector2.zero, d, 1f, 1f, TimeDeep, TimeDeep, 0.3f, FxMath.Min(fin * 1.5f, 0.5f), fout, followCamera: true);

        // 発動者は止まった時間の中を動き回るので、頭上の時計は発動直後だけ見せて消す (置き去りにしない)。
        // 頭上の名前に被らない高さに浮かべる
        Vector2 clock = c + new Vector2(0f, 2.3f);
        float cl = FxMath.Min(d, 1.8f);
        float cfin = 0.08f / cl;
        const float cfout = 0.65f;
        Add(Shape.Glow, clock, Vector2.zero, cl, 2.8f, 2.8f, TimeGold, TimePale, 0.45f, cfin, cfout);
        Add(Shape.Ring, clock, Vector2.zero, cl, 2f, 2f, TimeGold, TimeGold, 0.95f, cfin, cfout);
        Add(Shape.Ring, clock, Vector2.zero, cl, 1.7f, 1.7f, WindWhite, TimeGold, 0.5f, cfin, cfout);
        Add(Shape.Ring, clock, Vector2.zero, cl, 2.6f, 2.6f, TimePale, TimePale, 0.35f, cfin, cfout, spin: 20f);

        for (int i = 0; i < 12; i++)
        {
            float ang = i * 30f;
            float rad = ang / FxMath.Rad2Deg;
            float len = i % 3 == 0 ? 0.24f : 0.13f;
            Add(Shape.Solid, clock + new Vector2(FxMath.Cos(rad), FxMath.Sin(rad)) * 0.78f, Vector2.zero, cl, len, len, TimeGold, TimeGold, 1f, cfin, cfout,
                rot: ang, sy0: 0.05f, sy1: 0.05f, order: 8);
        }

        // 針は根元が明るい光条を中心から伸ばす (止まったまま動かない)
        Add(Shape.Ray, clock, Vector2.zero, cl, 0.72f, 0.72f, WindWhite, TimeGold, 1f, cfin, cfout, rot: 100f, sy0: 0.11f, sy1: 0.11f);
        Add(Shape.Ray, clock, Vector2.zero, cl, 0.5f, 0.5f, WindWhite, TimeGold, 1f, cfin, cfout, rot: 20f, sy0: 0.14f, sy1: 0.14f);
        Add(Shape.Star, clock, Vector2.zero, cl, 0.3f, 0.3f, WindWhite, TimeGold, 1f, cfin, cfout);

        Camera cam = Camera.main;

        if (cam)
        {
            Vector2 cp = cam.transform.position;
            float halfH = cam.orthographicSize;
            float halfW = halfH * cam.aspect;

            for (int i = 0; i < 60; i++)
            {
                float size = Rnd(0.14f, 0.28f);
                Add(Shape.Star, cp + new Vector2(Rnd(-halfW, halfW), Rnd(-halfH, halfH)), Vector2.zero, d, size, size, i % 4 == 0 ? TimeGold : TimePale, TimePale, 0.9f, fin, fout,
                    delay: Rnd(0f, 0.4f), twinkle: 0.7f, twinkleSpeed: Rnd(1.5f, 3f));
            }
        }

        // 動き出す瞬間: 画面全体がもう一度閃く (発動者はもう別の場所にいるので、位置に紐づく演出は出さない)
        Add(Shape.Solid, c, Vector2.zero, 0.5f, 1f, 1f, TimePale, WindWhite, 0.45f, 0.004f, 0.1f, delay: d, followCamera: true);
    }

    private static readonly Color BoltWhite = new(0.95f, 0.97f, 1f);
    private static readonly Color BoltBlue = new(0.45f, 0.65f, 1f);
    private static readonly Color BoltViolet = new(0.6f, 0.45f, 1f);

    // 稲妻 1 本分の線分: 白い芯と青い光のにじみを、a から b へ向けて置く (2 回瞬く)
    private static void BoltSegment(Vector2 a, Vector2 b, float width, float delay)
    {
        float dx = b.x - a.x, dy = b.y - a.y;
        float len = FxMath.Sqrt(dx * dx + dy * dy);
        float rot = FxMath.Atan2(dy, dx) * FxMath.Rad2Deg;
        Vector2 m = FxMath.V2((a.x + b.x) * 0.5f, (a.y + b.y) * 0.5f);

        Add(Shape.Solid, m, Vector2.zero, 0.45f, len, len, BoltWhite, BoltBlue, 1f, 0.01f, 0.25f, delay: delay, rot: rot, sy0: width, sy1: width * 0.5f,
            twinkle: 0.6f, twinkleSpeed: 40f, order: 8);
        Add(Shape.Glow, m, Vector2.zero, 0.5f, len * 1.2f, len * 1.2f, BoltBlue, BoltViolet, 1f, 0.01f, 0.3f, delay: delay, rot: rot, sy0: width * 13f, sy1: width * 6f);
        Add(Shape.Solid, m, Vector2.zero, 0.25f, len, len, BoltWhite, BoltBlue, 0.9f, 0.01f, 0.3f, delay: delay + 0.12f, rot: rot, sy0: width * 0.8f, sy1: width * 0.4f, order: 8);
    }

    // 空から着弾点まで、折れ曲がりながら落ちる稲妻を 1 本 (途中で 2 回枝分かれする)
    private static void Bolt(Vector2 c, float width, float delay)
    {
        Vector2 top = c + new Vector2(Rnd(-1.5f, 1.5f), 9f);
        Vector2 prev = top;
        const int segments = 11;

        for (int i = 1; i <= segments; i++)
        {
            Vector2 next = i == segments ? c : Vector2.Lerp(top, c, i / (float)segments) + new Vector2(Rnd(-0.6f, 0.6f), 0f);
            BoltSegment(prev, next, width, delay);

            if (i is 3 or 6 or 8)
            {
                // 枝は短い線分を細かく折りながら斜め下へ、先へ行くほど細くする
                float ang = Rnd(0f, 1f) < 0.5f ? Rnd(-75f, -35f) : Rnd(-145f, -105f);
                Vector2 from = next;

                for (int j = 0; j < 5; j++)
                {
                    ang += Rnd(-35f, 35f);
                    float a = ang / FxMath.Rad2Deg;
                    float l = Rnd(0.25f, 0.45f);
                    Vector2 to = from + FxMath.V2(FxMath.Cos(a) * l, FxMath.Sin(a) * l);
                    BoltSegment(from, to, width * 0.45f * (1f - j * 0.15f), delay);
                    from = to;
                }
            }

            prev = next;
        }
    }

    // 空が光る → 空から落ちる太い稲妻と、少し遅れてもう 1 本 → 着弾の閃光・星・輪・火花 → 焦げ跡と、しばらく走る放電と煙
    private static void SpawnLightningStrike(Vector2 c)
    {
        Impact(c, 3f, new Color(0.8f, 0.88f, 1f), 0.85f, 0.35f, 0.6f);

        Bolt(c, 0.17f, 0f);
        Bolt(c + new Vector2(Rnd(-0.3f, 0.3f), 0f), 0.11f, 0.2f);

        Add(Shape.Star, c, Vector2.zero, 0.6f, 0.5f, 5f, WindWhite, BoltBlue, 1f, 0.01f, 0.3f);
        Add(Shape.Glow, c, Vector2.zero, 0.6f, 4.5f, 2f, BoltWhite, BoltBlue, 1f, 0.01f, 0.3f);
        Add(Shape.Ring, c + Feet, Vector2.zero, 0.7f, 0.3f, 5f, BoltWhite, BoltBlue, 1f, 0.01f, 0.4f, rot: 0f, sy0: 0.1f, sy1: 1.75f);
        Add(Shape.Ring, c, Vector2.zero, 0.55f, 0.2f, 4f, WindWhite, BoltViolet, 0.9f, 0.01f, 0.35f);
        Add(Shape.Ring, c, Vector2.zero, 0.5f, 0.2f, 3f, WindWhite, BoltBlue, 0.8f, 0.01f, 0.35f, delay: 0.2f);

        for (int i = 0; i < 50; i++)
        {
            Add(Shape.Star, c, Dir() * Rnd(3f, 9f), Rnd(0.4f, 0.9f), Rnd(0.4f, 0.55f), 0.16f, i % 2 == 0 ? WindWhite : BoltBlue, BoltViolet, 1f, 0.01f, 0.5f,
                drag: 3f, stretch: 0.12f);
        }

        Add(Shape.Cloud, c + Feet, Vector2.zero, 2.6f, 2f, 2f, new Color(0.12f, 0.12f, 0.15f), new Color(0.2f, 0.2f, 0.22f), 0.7f, 0.02f, 0.5f,
            rot: 0f, sy0: 0.7f, sy1: 0.7f, order: 0);

        // 着弾点の周りで少し遅れてぱちぱちと走る放電
        for (int i = 0; i < 18; i++)
        {
            Vector2 a = c + FxMath.InsideUnitCircle() * 1.1f;
            float ang = Rnd(0f, 2f * FxMath.PI);
            float l = Rnd(0.3f, 0.7f);
            Vector2 b = a + FxMath.V2(FxMath.Cos(ang) * l, FxMath.Sin(ang) * l);
            Add(Shape.Solid, FxMath.V2((a.x + b.x) * 0.5f, (a.y + b.y) * 0.5f), Vector2.zero, Rnd(0.12f, 0.2f), l, l, BoltWhite, BoltBlue, 1f, 0.01f, 0.4f,
                delay: Rnd(0.1f, 1.5f), rot: ang * FxMath.Rad2Deg, sy0: 0.05f, sy1: 0.03f, order: 8);
            Add(Shape.Glow, FxMath.V2((a.x + b.x) * 0.5f, (a.y + b.y) * 0.5f), Vector2.zero, 0.2f, l, l, BoltBlue, BoltViolet, 0.6f, 0.01f, 0.4f,
                delay: Rnd(0.1f, 1.5f), rot: ang * FxMath.Rad2Deg, sy0: 0.3f, sy1: 0.2f);
        }

        for (int i = 0; i < 10; i++)
        {
            Add(Shape.Cloud, c + FxMath.InsideUnitCircle() * 0.5f, new Vector2(Rnd(-0.3f, 0.3f), 0f), Rnd(1.4f, 2.2f), 0.5f, Rnd(1.4f, 2f), new Color(0.35f, 0.35f, 0.4f),
                new Color(0.2f, 0.2f, 0.24f), 0.45f, 0.05f, 0.4f, delay: Rnd(0.1f, 0.3f), spin: Rnd(-40f, 40f), rise: 0.6f);
        }
    }

    private static readonly Color Stone = new(0.62f, 0.6f, 0.58f);
    private static readonly Color StoneDark = new(0.32f, 0.3f, 0.3f);

    // 重い物が叩きつけられる: 閃光と揺れ → 地面を走る平たい二重の衝撃の輪と空へ広がる輪 → 横へ噴き出す土煙
    // → 跳ね上がって落ちる破片 → しばらく残る放射状のひび
    private static void SpawnSlam(Vector2 c, float size)
    {
        float r = FxMath.Clamp(size, 0.3f, 3f);
        Vector2 f = c + Feet;

        Impact(c, r * 2f, new Color(1f, 0.93f, 0.8f), 0.12f + 0.12f * r, 0.12f + 0.16f * r, 0.4f);

        Add(Shape.Ring, f, Vector2.zero, 0.7f, 0.4f * r, 4.4f * r, Dust, DustDark, 1f, 0.02f, 0.4f, rot: 0f, sy0: 0.15f * r, sy1: 1.6f * r);
        Add(Shape.Ring, f, Vector2.zero, 0.8f, 0.3f * r, 3f * r, WindWhite, Dust, 0.85f, 0.02f, 0.4f, delay: 0.06f, rot: 0f, sy0: 0.12f * r, sy1: 1.1f * r);
        Add(Shape.Ring, c, Vector2.zero, 0.5f, 0.3f * r, 3.6f * r, WindWhite, Dust, 0.7f, 0.01f, 0.35f);
        Add(Shape.Glow, f, Vector2.zero, 0.4f, 2.4f * r, 1.2f * r, new Color(1f, 0.93f, 0.8f), Dust, 0.8f, 0.01f, 0.3f, sy0: 0.8f * r, sy1: 0.4f * r);

        // 土煙は大きさに比例させすぎると画面を覆うので、数と広がりは緩やかに増やす
        int dust = (int)(16 + 10 * r);
        float spread = FxMath.Min(r, 1.4f);

        for (int i = 0; i < dust; i++)
        {
            float ang = Rnd(0f, 2f * FxMath.PI);
            Vector2 dir = FxMath.V2(FxMath.Cos(ang), FxMath.Sin(ang) * 0.4f);
            Add(Shape.Cloud, f + dir * 0.3f * r, dir * Rnd(3.5f, 7f) * spread, Rnd(1f, 1.6f), 0.5f * spread, Rnd(1.3f, 2.1f) * spread, Dust, DustDark, 0.65f, 0.05f, 0.35f,
                drag: 2.4f, spin: Rnd(-90f, 90f), rise: 0.3f);
        }

        int debris = (int)(16 * r);

        for (int i = 0; i < debris; i++)
        {
            float s = Rnd(0.14f, 0.32f);
            Add(Shape.Chunk, f, new Vector2(Rnd(-3.5f, 3.5f) * r, Rnd(2.5f, 6f) * r), Rnd(0.8f, 1.2f), s, s, Stone, StoneDark, 1f, 0.01f, 0.6f,
                drag: 1.2f, spin: Rnd(-720f, 720f), rise: -4f);
        }

        // 床に走るひび: 短い線分を折り曲げながら放射状に伸ばす (床は斜めに見下ろしているので縦を潰す)。
        // 演出は全部キャラより手前に描かれるので、足元の外側から始めて、体と重なる奥 (画面の上) へは伸ばさない
        for (int i = 0; i < 8; i++)
        {
            float ang = -200f + i * 31f + Rnd(-10f, 10f);
            float a0 = ang / FxMath.Rad2Deg;
            Vector2 from = f + FxMath.V2(FxMath.Cos(a0) * 0.45f, FxMath.Sin(a0) * 0.22f);

            for (int j = 0; j < 3; j++)
            {
                ang += Rnd(-35f, 35f);
                float a = ang / FxMath.Rad2Deg;
                float l = Rnd(0.22f, 0.4f) * FxMath.Min(r, 2f);
                Vector2 to = from + FxMath.V2(FxMath.Cos(a) * l, FxMath.Sin(a) * l * 0.5f);
                float dx = to.x - from.x, dy = to.y - from.y;
                float w = 0.08f * (1f - j * 0.25f);
                // 線分どうしの継ぎ目に隙間が出ないよう、太さの分だけ長めに描く
                float seg = FxMath.Sqrt(dx * dx + dy * dy) + w;
                Add(Shape.Solid, FxMath.V2((from.x + to.x) * 0.5f, (from.y + to.y) * 0.5f), Vector2.zero, 1.8f, seg, seg,
                    StoneDark, StoneDark, 0.85f, 0.02f, 0.6f, delay: j * 0.03f, rot: FxMath.Atan2(dy, dx) * FxMath.Rad2Deg, sy0: w, sy1: w, order: 0);
                from = to;
            }
        }
    }

    private static readonly Color WaterFoam = new(0.9f, 0.97f, 1f);
    private static readonly Color WaterLight = new(0.55f, 0.85f, 1f);
    private static readonly Color WaterBlue = new(0.2f, 0.5f, 0.95f);
    private static readonly Color WaterDeep = new(0.1f, 0.3f, 0.7f);

    // 水しぶき: 立ち上がる水柱 → 足元に広がる四重の波紋 → 王冠のように跳ね上がる水の筋 → 飛び散って落ちる水滴と泡。
    // 大きいしぶき (溺れた時) は閃光と揺れ、水面から昇っては消える泡と、沈んだ跡の暗い水も出す
    private static void SpawnSplash(Vector2 c, float size)
    {
        float r = FxMath.Clamp(size, 0.3f, 3f);
        Vector2 f = c + Feet;
        bool big = r >= 1.2f;

        if (big) Impact(c, r * 2f, WaterLight, 0.3f, 0.18f, 0.35f);

        // 水柱 (根元の明るい光条を上へ向け、太く短く立てて細く伸ばす)
        Add(Shape.Ray, f, Vector2.zero, 0.6f, 1.2f * r, 3.2f * r, WaterFoam, WaterLight, 0.95f, 0.02f, 0.4f, rot: 90f, sy0: 1f * r, sy1: 0.2f * r);
        Add(Shape.Glow, f + new Vector2(0f, 0.6f * r), Vector2.zero, 0.5f, 1.2f * r, 2f * r, WaterFoam, WaterBlue, 0.8f, 0.02f, 0.35f, sy0: 2f * r, sy1: 3.2f * r);

        for (int k = 0; k < 4; k++)
        {
            float to = (3f + k * 0.9f) * r;
            Add(Shape.Ring, f, Vector2.zero, 0.8f + k * 0.15f, 0.3f * r, to, k % 2 == 0 ? WaterFoam : WaterLight, WaterBlue, 1f - k * 0.2f, 0.02f, 0.4f,
                delay: k * 0.12f, rot: 0f, sy0: 0.1f * r, sy1: to * 0.35f);
        }

        for (int i = 0; i < 20; i++)
        {
            Add(Shape.Ray, f, Vector2.zero, Rnd(0.45f, 0.75f), 0.6f * r, 1.8f * r, WaterFoam, WaterLight, 0.95f, 0.02f, 0.4f, rot: Rnd(45f, 135f), sy0: 0.2f, sy1: 0.06f);
        }

        int drops = (int)(36 * r);

        for (int i = 0; i < drops; i++)
        {
            float s = Rnd(0.14f, 0.28f);
            Add(Shape.Glow, f, new Vector2(Rnd(-3f, 3f) * r, Rnd(3f, 7f) * r), Rnd(0.7f, 1.1f), s, s * 0.7f, i % 3 == 0 ? WindWhite : WaterFoam, WaterBlue, 1f, 0.01f, 0.6f,
                drag: 1f, rise: -4.5f, stretch: 0.06f);
        }

        for (int i = 0; i < 10; i++)
        {
            Add(Shape.Cloud, f + FxMath.InsideUnitCircle() * 0.3f * r, FxMath.InsideUnitCircle() * 1.6f * r, Rnd(0.8f, 1.2f), 0.5f * r, Rnd(1.1f, 1.6f) * r, WaterFoam, WaterLight,
                0.65f, 0.03f, 0.4f, drag: 2f, spin: Rnd(-60f, 60f));
        }

        if (!big) return;

        Add(Shape.Cloud, f, Vector2.zero, 2f, 1.4f * r, 2.8f * r, WaterDeep, WaterBlue, 0.55f, 0.03f, 0.5f, rot: 0f, sy0: 0.5f * r, sy1: 0.95f * r, order: 0);

        for (int i = 0; i < 26; i++)
        {
            float s = Rnd(0.14f, 0.32f);
            Add(Shape.Ring, c + new Vector2(Rnd(-0.6f, 0.6f), Rnd(-0.4f, 0.2f)), new Vector2(Rnd(-0.2f, 0.2f), Rnd(0.6f, 1.6f)), Rnd(0.8f, 1.4f), s, s, WaterFoam, WaterLight, 1f, 0.05f, 0.6f,
                delay: Rnd(0f, 1.6f), twinkle: 0.3f, twinkleSpeed: 8f);
        }
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
        tf.position = FxMath.V3(pos.x, pos.y);
        tf.localScale = default;

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
        sr.color = FxMath.Rgba(color0.r, color0.g, color0.b, 0f);
        go.SetActive(true);

        // 光条のテクスチャは横長 (4:1) なので、縦の指定値がそのまま太さ (単位) になるよう補正する
        float aspect = shape == Shape.Ray ? RayAspect : 1f;

        Active.Add(new Particle
        {
            Go = go, Sr = sr, Tf = tf, Pos = pos, Vel = vel, Drag = drag, Rise = rise, Delay = delay, Life = life,
            Sx0 = sx0, Sx1 = sx1, Sy0 = (sy0 < 0f ? sx0 : sy0) * aspect, Sy1 = (sy1 < 0f ? sx1 : sy1) * aspect,
            Rot = rot ?? Rnd(0f, 360f), Spin = spin,
            Color0 = color0, Color1 = color1, ColorMid = colorMid ?? color0, HasMid = colorMid.HasValue, Alpha = alpha, FadeIn = FxMath.Max(fadeIn, 0.001f), FadeOutFrom = fadeOutFrom,
            Twinkle = twinkle, TwinkleSpeed = twinkleSpeed, Phase = Rnd(0f, 6.28f), Stretch = stretch, FollowCamera = followCamera
        });
    }

    private static void Release(Particle p)
    {
        if (!p.Go) return;

        p.Go.SetActive(false);
        Pool.Push((p.Go, p.Sr));
    }

    // 毎フレーム全粒子を回すので、Unity の Mathf / Vector / Color / Quaternion の関数・演算子・コンストラクタは使わず FxMath で計算する
    // (理由は FxMath 冒頭)。ゲーム本体へ渡すのは色・位置・大きさ・向きの 4 つだけ。
    private static void Animate(float dt)
    {
        bool camRead = false;
        float camX = 0f, camY = 0f, camW = 0f, camH = 0f;

        for (int i = Active.Count - 1; i >= 0; i--)
        {
            Particle p = Active[i];

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

            float damp = p.Drag > 0f ? FxMath.Exp(-p.Drag * dt) : 1f;
            float vx = p.Vel.x * damp, vy = p.Vel.y * damp;
            p.Vel.x = vx;
            p.Vel.y = vy;
            p.Pos.x += vx * dt;
            p.Pos.y += (vy + p.Rise) * dt;
            p.Rot += p.Spin * dt;

            float u = 1f - t;
            float ease = 1f - u * u * u;
            float sx = p.Sx0 + (p.Sx1 - p.Sx0) * ease;
            float sy = p.Sy0 + (p.Sy1 - p.Sy0) * ease;
            float rot = p.Rot;
            float px = p.Pos.x, py = p.Pos.y;

            if (p.Stretch > 0f)
            {
                float speed = FxMath.Sqrt(vx * vx + vy * vy);
                sx *= 1f + speed * p.Stretch;
                if (speed > 0.01f) rot = FxMath.Atan2(vy, vx) * FxMath.Rad2Deg;
            }

            if (p.FollowCamera)
            {
                if (!camRead)
                {
                    camRead = true;
                    Camera cam = Camera.main;

                    if (cam)
                    {
                        Vector3 cp = cam.transform.position;
                        camX = cp.x;
                        camY = cp.y;
                        camH = cam.orthographicSize * 2.4f;
                        camW = camH * cam.aspect;
                    }
                }

                if (camH > 0f)
                {
                    px = camX;
                    py = camY;
                    sx = camW;
                    sy = camH;
                    rot = 0f;
                }
            }

            float a = p.Alpha * FxMath.Clamp01(t / p.FadeIn);
            if (t > p.FadeOutFrom) a *= 1f - (t - p.FadeOutFrom) / (1f - p.FadeOutFrom);
            if (p.Twinkle > 0f) a *= 1f - p.Twinkle + p.Twinkle * ((FxMath.Sin(p.Age * p.TwinkleSpeed + p.Phase) + 1f) * 0.5f);

            Color from = p.Color0, to = p.Color1;
            float k = t;

            if (p.HasMid)
            {
                if (t < 0.5f)
                {
                    to = p.ColorMid;
                    k = t * 2f;
                }
                else
                {
                    from = p.ColorMid;
                    k = (t - 0.5f) * 2f;
                }
            }

            p.Sr.color = FxMath.Rgba(from.r + (to.r - from.r) * k, from.g + (to.g - from.g) * k, from.b + (to.b - from.b) * k, a);
            p.Tf.position = FxMath.V3(px, py);
            p.Tf.localScale = FxMath.V3(sx, sy, 1f);
            p.Tf.localRotation = FxMath.RotZ(rot);

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
        if (!_glow) _glow = MakeSprite(64, 64, (x, y) => FxMath.Pow(1f - FxMath.Clamp01(FxMath.Sqrt(x * x + y * y)), 2.4f));
        if (!_cloud) _cloud = MakeSprite(96, 96, CloudAlpha);
        if (!_flame) _flame = MakeSprite(96, 96, (x, y) => FxMath.Clamp01(FxMath.Pow(CloudAlpha(x, y), 0.45f) * 1.15f));
        if (!_solid) _solid = MakeSprite(4, 4, (_, _) => 1f);

        if (!_ring)
        {
            _ring = MakeSprite(128, 128, (x, y) =>
            {
                // 縁に細い光の輪、内側はうっすら満ちた膜
                float d = FxMath.Sqrt(x * x + y * y);
                float edge = FxMath.Exp(-FxMath.Pow((d - 0.93f) / 0.035f, 2f));
                float fill = d < 0.93f ? FxMath.Pow(d / 0.93f, 3f) * 0.18f : 0f;
                return FxMath.Clamp01(edge + fill);
            });
        }

        if (!_star)
        {
            _star = MakeSprite(64, 64, (x, y) =>
            {
                // 白熱した核 + 十字の光芒
                float d = FxMath.Sqrt(x * x + y * y);
                float core = FxMath.Pow(1f - FxMath.Clamp01(d / 0.35f), 2f);
                float ax = FxMath.Abs(x), ay = FxMath.Abs(y);
                float streakH = FxMath.Exp(-ay * 28f) * FxMath.Clamp01(1f - ax);
                float streakV = FxMath.Exp(-ax * 28f) * FxMath.Clamp01(1f - ay);
                return FxMath.Clamp01(FxMath.Max(core, FxMath.Pow(FxMath.Max(streakH, streakV), 1.5f)));
            });
        }

        if (!_ray)
        {
            // 根元 (左端) が太く明るく、先へ行くほど細く消える光の筋。pivot は根元。
            _ray = MakeSprite(128, (int)(128 / RayAspect), (x, y) =>
            {
                float u = (x + 1f) * 0.5f;
                float width = FxMath.Lerp(0.9f, 0.1f, u);
                float across = FxMath.Exp(-FxMath.Pow(y / width, 2f) * 3f);
                return FxMath.Clamp01(across * FxMath.Pow(1f - u, 1.3f));
            }, new Vector2(0f, 0.5f));
        }

        if (!_chunk)
        {
            // 角ばった不定形の破片
            _chunk = MakeSprite(32, 32, (x, y) =>
            {
                float ang = FxMath.Atan2(y, x);
                float edge = 0.7f + 0.2f * FxMath.Sin(ang * 3f + 1.3f) + 0.1f * FxMath.Sin(ang * 7f);
                float d = FxMath.Sqrt(x * x + y * y);
                return d < edge ? 1f : FxMath.Clamp01(1f - (d - edge) * 12f);
            });
        }
    }

    // 縁がふわっと崩れた柔らかい雲。完全な円だと「玉」に見えるので、角度方向に揺らぎを入れる。
    private static float CloudAlpha(float x, float y)
    {
        float d = FxMath.Sqrt(x * x + y * y);
        float ang = FxMath.Atan2(y, x);
        float wobble = 0.82f + 0.1f * FxMath.Sin(ang * 3f + 0.7f) + 0.08f * FxMath.Sin(ang * 5f + 2.1f);
        float n = Mathf.PerlinNoise(x * 2.3f + 5f, y * 2.3f + 5f);
        float body = 1f - FxMath.Clamp01(d / wobble);
        return FxMath.Clamp01(FxMath.Pow(body, 1.3f) * (0.65f + 0.35f * n));
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
                pixels[py * width + px] = FxMath.Rgba(1f, 1f, 1f, alpha((px - hx) / hx, (py - hy) / hy));
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
