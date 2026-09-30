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
        Splash = 14,

        // 人が火柱に包まれて燃え上がる: Pos = 燃えた人、Radius = 火の大きさ (1 前後)
        Ignite = 15,

        // 渦に吸い込まれて消える: Pos = 吸い込まれた人、Radius = 渦の大きさ (1 前後)
        Swallow = 16,

        // 虚空が破れて噴き出す: Pos = 破れた場所、Radius = 大きさ (1 前後)
        VoidBurst = 17,

        // 煙玉で消える / 現れる: Pos = 本人、Radius = 煙の大きさ (1 前後)
        Smoke = 18,

        // 地面に潜る / 地面から這い出る: Pos = 本人、Radius = 大きさ (1 前後)
        BurrowIn = 19,
        BurrowOut = 20,

        // Fire と同じ爆発。巻き込まれた人は演出の後に死ぬので、範囲内の自分の画面でも待たずにすぐ見せる
        Blast = 21,

        // ここから下の波動砲は A・B の 2 値も送る (HasExtra)。色は B に CannonPalette を載せる

        // 魔法陣が描かれて力を溜める: Pos = ゲートの中心、Radius = 発射までの秒数、A = ビームの太さ
        CannonChargeRight = 22,
        CannonChargeLeft = 23,

        // 魔法陣からビームが出る: Pos = ゲートの中心、Radius = 撃ち続ける秒数、A = ビームの太さ
        CannonBeamRight = 24,
        CannonBeamLeft = 25,

        // 全幅のビームが縦に掃く (超波動砲ダイナミック): Pos = 始点でのビームの中心、Radius = 秒数、A = 太さ、B = 終点の高さ
        CannonSweep = 26,

        // 撃ち終わって消える / 途中で止められて魔法陣が砕ける: Pos = ゲートの中心 (近くの波動砲の演出だけを消す)
        CannonEnd = 27,
        CannonBreak = 28,

        // 撃った瞬間の必殺技カットイン (画面に出す): Radius = CannonPalette + 1、A = 撃ち手の PlayerId、B = CannonTitle
        CannonCutIn = 29,

        // 血を吸い尽くされる: Pos = 吸われた人 (演出が始まってから倒れる)、Radius = 吸われた人の PlayerId + 1 (血はその人の体の色)
        Drain = 30,

        // 毒が回る: Pos = 毒で倒れる人
        Poison = 31,

        // 死体が石になる: Pos = 死体 (近くの死体を灰色に塗る)
        Petrify = 32,

        // 操り糸で吊られた人が人を殺す / 呪われた人が人を殺す: Pos = 操られた人、Radius = 殺される人の PlayerId + 1
        PuppetStrings = 33,
        CurseStrings = 34,

        // 竜巻: Pos = 竜巻の中心、Radius = 残る秒数 (その間ずっと渦を巻く)
        Tornado = 35,

        // 竜巻に巻き上げられて消える: Pos = 巻き上げられた人
        TornadoLift = 36
    }

    // カットインに出す技名
    public enum CannonTitle : byte
    {
        WaveCannon,
        SuperCannon,
        BlackHole,
        Twin,
        Dynamic,
        CertainKill
    }

    public enum CannonPalette : byte
    {
        Orange,
        Cyan,
        Rainbow,
        Crimson,
        Void
    }

    // LocalOnly はまとめ送信に載せない (PlayFor が宛先を絞って自前で送る)
    private readonly record struct Request(Kind Kind, Vector2 Pos, float Radius, bool LocalOnly = false, float A = 0f, float B = 0f);

    private static bool HasExtra(Kind kind) => kind is >= Kind.CannonChargeRight and <= Kind.CannonCutIn;

    private enum Shape : byte
    {
        Cloud,
        Flame,
        Ring,
        Ray,
        Chunk,
        Star,
        Glow,
        Solid,
        Sigil,
        SigilInner,
        Beam,
        Bat
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
        public byte Tag;
        public bool IsBeam;
        public float AnchorX, AnchorY;
        public float Flap;
    }

    // 一度に大量に起爆する役職 (複数の爆弾を同じフレームで起爆するもの) でも RPC は 1 本にまとめ、
    // 呼び出し元の頻度に関わらず送信は SendInterval 秒に 1 本までに抑える (待つ間は Unsent に溜める)。
    private static readonly List<Request> Pending = [];
    private static readonly List<Request> Unsent = [];
    private static readonly List<Particle> Active = [];

    // 1 発で数百粒を出すので GameObject は使い回す (毎回 new/Destroy するとフレームが引っかかる)。
    private static readonly Stack<(GameObject Go, SpriteRenderer Sr)> Pool = [];

    // 1 件 13 バイト (波動砲は 21 バイト)。1 本は MaxSendBytes までにして、溢れた分は次の送信へ回す (15 人全員をワープさせると出発と到着で 28 件)
    private const int MaxPendingPerFrame = 48;
    private const int MaxSendBytes = 630;
    private const float SendInterval = 0.1f;
    private const int MaxActive = 3000;
    private const int WarmPool = 800;
    private const int SortingOrder = 150;
    private const float RayAspect = 4f;
    private const float BeamAspect = 4f;
    private static float _lastSendTime = -1f;
    private static float _holdUntil = -1f;
    private static bool _pauseForKill;

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
    private static Sprite _sigil;
    private static Sprite _sigilInner;
    private static Sprite _beam;
    private static Sprite _bat;

    // ホストが呼ぶ。描画とモッドクライアントへの送信は次の HudManager.Update 以降でまとめて行う。
    public static void Play(Kind kind, Vector2 pos, float radius)
    {
        if (!AmongUsClient.Instance || !AmongUsClient.Instance.AmHost || !GameStates.InGame) return;
        if (Pending.Count >= MaxPendingPerFrame) return;

        Pending.Add(new Request(kind, pos, ClampRadius(kind, radius)));
    }

    // 波動砲の演出。gate = ゲートの中心、right = 右へ撃つか
    public static void CannonCharge(Vector2 gate, bool right, float seconds, int thickness, CannonPalette palette)
    {
        PlayExtra(right ? Kind.CannonChargeRight : Kind.CannonChargeLeft, gate, seconds, thickness, (float)palette);
    }

    public static void CannonBeam(Vector2 gate, bool right, float seconds, int thickness, CannonPalette palette)
    {
        PlayExtra(right ? Kind.CannonBeamRight : Kind.CannonBeamLeft, gate, seconds, thickness, (float)palette);
    }

    // center = 始点の高さでのビームの中心
    public static void CannonSweep(Vector2 center, float endY, float seconds, int thickness)
    {
        PlayExtra(Kind.CannonSweep, center, seconds, thickness, endY);
    }

    // broken = 撃ち終わる前に止められた (魔法陣が砕ける)
    public static void CannonEnd(Vector2 gate, CannonPalette palette, bool broken)
    {
        PlayExtra(broken ? Kind.CannonBreak : Kind.CannonEnd, gate, 1f, 0f, (float)palette);
    }

    // カットインは発射のこの秒数前に出す (帯が閉じ切る頃にビームが出る)
    public const float CannonCutInLead = 1.1f;

    public static void CannonCutIn(byte shooterId, CannonTitle title, CannonPalette palette)
    {
        PlayExtra(Kind.CannonCutIn, Vector2.zero, (float)palette + 1f, shooterId, (float)title);
    }

    internal static void PlayExtra(Kind kind, Vector2 pos, float radius, float a, float b)
    {
        if (!AmongUsClient.Instance || !AmongUsClient.Instance.AmHost || !GameStates.InGame) return;
        if (Pending.Count >= MaxPendingPerFrame) return;

        Pending.Add(new Request(kind, pos, ClampRadius(kind, radius), A: a, B: b));
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
            if (HasExtra(r.Kind))
            {
                writer.Write(r.A);
                writer.Write(r.B);
            }

            EarlyWarning.OnPacket("PlayVisualFx", writer.Length, writer.Length, "None");
            AmongUsClient.Instance.FinishRpcImmediately(writer);
        }
        catch (System.Exception e) { Utils.ThrowException(e); }
    }

    // 凍結系・波動砲は Radius に秒数を載せるので、効果時間の設定の上限 (180 秒) まで通す
    private static float ClampRadius(Kind kind, float radius)
    {
        if (kind is Kind.PuppetStrings or Kind.CurseStrings) return FxMath.Clamp(radius, 0f, 256f);
        return kind is Kind.Freeze or Kind.TimeStop or Kind.Tornado || HasExtra(kind) ? FxMath.Clamp(radius, 0.3f, 180f) : FxMath.Clamp(radius, 0.3f, 15f);
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
            float a = 0f, b = 0f;
            if (HasExtra(kind))
            {
                a = reader.ReadSingle();
                b = reader.ReadSingle();
            }

            if (!GameStates.InGame) continue;

            SpawnLocal(new Request(kind, new Vector2(x, y), ClampRadius(kind, radius), A: a, B: b));
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
                TornadoEmitters.Clear();
                RecentStrings.Clear();
                StoneTints.Clear();
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

            // 竜巻は残っている間ずっと少しずつ粒を足す (効果時間ぶんを最初に全部作ると、見えない粒が何百も待機する)
            if (TornadoEmitters.Count > 0 && !GameStates.IsMeeting) PulseTornados();
            if (StoneTints.Count > 0) TickStoneTints();

            // 自分がこの爆発で死んだ時はキル演出が画面を覆うので、明けるまで演出を止めておいて後から見せる
            // 止めるのは即死する爆発に巻き込まれた時だけ。演出の後に死ぬもの (Blast・スーパーノヴァ・火柱) は裏で進めて、明けた時には終わっている方が自然
            if (Active.Count == 0) _pauseForKill = false;

            if (Active.Count > 0 && Time.time >= _holdUntil && !(_pauseForKill && KillOverlayOpen()))
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
        int done = Unsent.Count;

        try
        {
            if (AmongUsClient.Instance.AmHost && AnyOtherModdedClient())
            {
                int bytes = 1;
                int n = 0;

                foreach (Request r in Unsent)
                {
                    int size = HasExtra(r.Kind) ? 21 : 13;
                    if (n > 0 && bytes + size > MaxSendBytes) break;

                    bytes += size;
                    n++;
                }

                done = n;
                _lastSendTime = Time.unscaledTime;
                MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(PlayerControl.LocalPlayer.NetId, (byte)CustomRPC.PlayVisualFx, SendOption.None);
                writer.Write((byte)n);

                for (int i = 0; i < n; i++)
                {
                    Request r = Unsent[i];
                    writer.Write((byte)r.Kind);
                    writer.Write(r.Pos.x);
                    writer.Write(r.Pos.y);
                    writer.Write(r.Radius);

                    if (HasExtra(r.Kind))
                    {
                        writer.Write(r.A);
                        writer.Write(r.B);
                    }
                }

                EarlyWarning.OnPacket("PlayVisualFx", writer.Length, writer.Length, "None");
                AmongUsClient.Instance.FinishRpcImmediately(writer);
            }
        }
        catch (System.Exception e) { Utils.ThrowException(e); }
        finally { Unsent.RemoveRange(0, FxMath.Min(done, Unsent.Count)); }
    }

    private static bool KillOverlayOpen()
    {
        return ExplosionKillOverlay.Showing || CannonKillOverlay.Showing || SpecialKillOverlay.Showing || (HudManager.InstanceExists && HudManager.Instance.KillOverlay && HudManager.Instance.KillOverlay.IsOpen);
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
            // (スーパーノヴァ・Blast・火柱は演出を見せてから死なせるので待たない)
            PlayerControl lp = PlayerControl.LocalPlayer;
            bool lethal = r.Kind is Kind.Fire;
            if (lethal && lp && lp.IsAlive() && Vector2.Distance(lp.GetTruePosition(), r.Pos) <= r.Radius + 0.5f)
            {
                _holdUntil = Time.time + 0.35f;
                _pauseForKill = true;
            }

            Logger.Info($"{r.Kind} at ({r.Pos.x:F2}, {r.Pos.y:F2}) r={r.Radius:F1}", "ExplosionFx");

            // 知らない種類 (新しい版のホストが送ってきたもの) は描かない
            switch (r.Kind)
            {
                case Kind.Fire:
                case Kind.Blast:
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
                case Kind.Ignite:
                    SpawnIgnite(r.Pos, r.Radius);
                    break;
                case Kind.Swallow:
                    SpawnSwallow(r.Pos, r.Radius);
                    break;
                case Kind.VoidBurst:
                    SpawnVoidBurst(r.Pos, r.Radius);
                    break;
                case Kind.Smoke:
                    SpawnSmoke(r.Pos, r.Radius);
                    break;
                case Kind.BurrowIn:
                    SpawnBurrowIn(r.Pos, r.Radius);
                    break;
                case Kind.BurrowOut:
                    SpawnBurrowOut(r.Pos, r.Radius);
                    break;
                case Kind.CannonChargeRight:
                case Kind.CannonChargeLeft:
                    SpawnCannonCharge(r.Pos, r.Radius, r.A, r.B, r.Kind == Kind.CannonChargeRight ? 1f : -1f);
                    break;
                case Kind.CannonBeamRight:
                case Kind.CannonBeamLeft:
                    SpawnCannonBeam(r.Pos, r.Radius, r.A, r.B, r.Kind == Kind.CannonBeamRight ? 1f : -1f);
                    break;
                case Kind.CannonSweep:
                    SpawnCannonSweep(r.Pos, r.Radius, r.A, r.B);
                    break;
                case Kind.CannonEnd:
                case Kind.CannonBreak:
                    SpawnCannonEnd(r.Pos, r.B, r.Kind == Kind.CannonBreak);
                    break;
                case Kind.CannonCutIn:
                {
                    CannonColors k = CannonPal(r.Radius - 1f);
                    Modules.CannonCutIn.Show((byte)r.A, (CannonTitle)(byte)r.B, k.Light, k.Main, k.Deep);
                    break;
                }
                case Kind.Drain:
                    SpawnDrain(r.Pos, (int)(r.Radius + 0.5f) - 1);
                    break;
                case Kind.Poison:
                    SpawnPoison(r.Pos);
                    break;
                case Kind.Petrify:
                    SpawnPetrify(r.Pos);
                    break;
                case Kind.PuppetStrings:
                case Kind.CurseStrings:
                    SpawnStrings(r.Pos, (int)(r.Radius + 0.5f) - 1, r.Kind == Kind.CurseStrings);
                    break;
                case Kind.Tornado:
                    StartTornado(r.Pos, r.Radius);
                    break;
                case Kind.TornadoLift:
                    SpawnTornadoLift(r.Pos);
                    break;
            }
        }
        catch (System.Exception e) { Utils.ThrowException(e); }
        finally { _tag = 0; }
    }

    // ── 演出の中身 ─────────────────────────────────────────────────────

    private static float Rnd(float min, float max) => FxMath.Range(min, max);

    private static Vector2 Dir()
    {
        float ang = FxMath.Range(0f, 2f * FxMath.PI);
        return FxMath.V2(FxMath.Cos(ang), FxMath.Sin(ang));
    }

    // 近くで起きた爆発ほど強く、画面全体の閃光とカメラの揺れを返す (自分の画面だけ・送信なし)。
    private static void Impact(Vector2 c, float r, Color flash, float flashAlpha, float shake, float shakeDuration, float delay = 0f)
    {
        Camera cam = Camera.main;
        if (!cam) return;

        float reach = r + 9f;
        float dist = Vector2.Distance(cam.transform.position, c);
        if (dist > reach) return;

        float k = 1f - dist / reach;
        Add(Shape.Solid, c, Vector2.zero, 0.6f, 1f, 1f, flash, flash, flashAlpha * (0.35f + 0.65f * k), 0.004f, 0.08f, delay: delay, followCamera: true);
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

    private static readonly Color BlazeWhite = new(1f, 0.97f, 0.85f);
    private static readonly Color BlazeYellow = new(1f, 0.8f, 0.22f);
    private static readonly Color BlazeOrange = new(1f, 0.42f, 0.06f);
    private static readonly Color BlazeRed = new(0.72f, 0.1f, 0.03f);
    private static readonly Color Charred = new(0.09f, 0.06f, 0.05f);
    private static readonly Color Ash = new(0.3f, 0.27f, 0.26f);

    private static int _igniteFlashFrame = -1;

    // 燃え上がる: 足元から噴き上がる火の筋と閃光 → 体を包んで揺らめきながら立ちのぼる炎の舌 → 舞い上がる火の粉と黒煙
    // → 火が収まると床の焦げ跡と舞い落ちる灰が残る。一度に大勢が燃える (アーソニストの全焼) と粒の上限に届くので、
    // 既に多く出ている時は数を半分にする
    private static void SpawnIgnite(Vector2 c, float size)
    {
        float r = FxMath.Clamp(size, 0.5f, 2.5f);
        Vector2 f = c + Feet;
        float q = Active.Count > 1200 ? 0.5f : 1f;

        // 大勢が同時に燃えても画面の閃光と揺れは 1 回分だけにする (重ねると画面が橙一色に潰れる)
        if (_igniteFlashFrame != Time.frameCount)
        {
            _igniteFlashFrame = Time.frameCount;
            Impact(c, 1.5f * r, new Color(1f, 0.7f, 0.35f), 0.3f, 0.1f, 0.25f);
        }

        // 着火: 足元で弾ける光と、床を這う熱の輪
        Add(Shape.Glow, f, Vector2.zero, 0.5f, 0.5f * r, 3f * r, BlazeWhite, BlazeOrange, 1f, 0.02f, 0.3f, sy0: 0.3f * r, sy1: 1.4f * r);
        Add(Shape.Ray, f, Vector2.zero, 0.7f, 2.5f * r, 5.5f * r, BlazeWhite, BlazeOrange, 1f, 0.01f, 0.35f, rot: 90f, sy0: 1.3f * r, sy1: 0.2f * r);

        for (int k = 0; k < 3; k++)
        {
            float to = (2.6f + k * 0.8f) * r;
            Add(Shape.Ring, f, Vector2.zero, 0.6f + k * 0.12f, 0.3f * r, to, k == 0 ? BlazeWhite : BlazeYellow, BlazeRed, 1f - k * 0.2f, 0.02f, 0.4f,
                delay: k * 0.08f, rot: 0f, sy0: 0.1f * r, sy1: to * 0.35f);
        }

        // 体を包む火の芯 (燃えている間ずっと脈打つ)
        Add(Shape.Glow, c, Vector2.zero, 1.9f, 1.2f * r, 1.8f * r, BlazeYellow, BlazeRed, 0.85f, 0.05f, 0.65f, twinkle: 0.3f, twinkleSpeed: 18f, sy0: 1.8f * r, sy1: 2.6f * r);
        Add(Shape.Glow, f, Vector2.zero, 2f, 2.6f * r, 2.2f * r, BlazeOrange, BlazeRed, 0.55f, 0.05f, 0.6f, twinkle: 0.25f, twinkleSpeed: 11f, sy0: 1f * r, sy1: 0.8f * r, order: 0);

        // 体にまとわりついて揺らめく大きな炎 (燃えている間ずっとその場で渦を巻き、膨らんでは縮む)
        for (int i = 0; i < 9 * q; i++)
        {
            float s = Rnd(0.8f, 1.15f) * r;
            Add(Shape.Flame, c + new Vector2(Rnd(-0.3f, 0.3f) * r, Rnd(-0.45f, 0.25f) * r), new Vector2(0f, Rnd(0.1f, 0.3f)), Rnd(0.9f, 1.3f), s * 0.6f, s,
                BlazeYellow, BlazeRed, Rnd(0.75f, 0.95f), 0.1f, 0.55f, delay: Rnd(0f, 0.7f), spin: Rnd(-90f, 90f), twinkle: 0.35f, twinkleSpeed: Rnd(10f, 18f),
                sy0: s * 0.8f, sy1: s * 1.4f, colorMid: BlazeOrange);
        }

        // 炎の舌: 足元から体の高さまで伸び、膨らみながら赤く冷えて消える。速さの向きへ引き伸ばして縦長の炎にする
        // (速すぎると頭より上へ抜けて、体が燃えているように見えない)
        int tongues = (int)(80 * q);

        for (int i = 0; i < tongues; i++)
        {
            float x = Rnd(-0.45f, 0.45f) * r;
            float s = Rnd(0.45f, 0.75f) * r * (1f - FxMath.Abs(x) / r * 0.6f);
            Add(Shape.Flame, f + new Vector2(x, Rnd(0f, 0.55f) * r), new Vector2(-x * Rnd(0.5f, 1.2f), Rnd(1.3f, 2.4f) * r), Rnd(0.45f, 0.7f), s * 0.7f, s * 1.1f,
                i % 3 == 0 ? BlazeWhite : BlazeYellow, BlazeRed, 1f, 0.06f, 0.45f, drag: 1.6f, delay: Rnd(0f, 1.5f), spin: Rnd(-60f, 60f), stretch: 0.3f,
                colorMid: BlazeOrange);
        }

        // 炎の根元で白く燃える芯 (炎より手前に重ねて、外側の橙との濃淡を作る)
        for (int i = 0; i < 12 * q; i++)
        {
            float s = Rnd(0.3f, 0.5f) * r;
            Add(Shape.Glow, c + new Vector2(Rnd(-0.2f, 0.2f) * r, Rnd(-0.45f, -0.05f) * r), new Vector2(0f, Rnd(0.3f, 0.8f)), Rnd(0.35f, 0.55f), s, s * 1.3f,
                BlazeWhite, BlazeYellow, Rnd(0.6f, 0.85f), 0.1f, 0.5f, delay: Rnd(0.05f, 1.4f), sy0: s * 1.5f, sy1: s * 2f);
        }

        // 炎の縁で上へ舐めるように伸びては消える細い火先
        for (int i = 0; i < 30 * q; i++)
        {
            float x = Rnd(-0.4f, 0.4f) * r;
            float l = Rnd(0.5f, 0.9f) * r;
            Add(Shape.Ray, c + new Vector2(x, Rnd(0f, 0.35f) * r), new Vector2(0f, Rnd(0.6f, 1.2f)), Rnd(0.22f, 0.38f), l * 0.4f, l, BlazeWhite, BlazeOrange, 0.8f, 0.1f, 0.4f,
                delay: Rnd(0.05f, 1.5f), rot: 90f + x * 40f + Rnd(-10f, 10f), sy0: 0.34f * r, sy1: 0.12f * r, colorMid: BlazeYellow);
        }

        // 最初の一瞬だけ高く噴き上がる火柱
        for (int i = 0; i < 14 * q; i++)
        {
            float s = Rnd(0.6f, 1f) * r;
            Add(Shape.Flame, f + new Vector2(Rnd(-0.2f, 0.2f) * r, 0f), new Vector2(Rnd(-0.4f, 0.4f), Rnd(6f, 9f) * r), Rnd(0.5f, 0.7f), s, s * 0.3f,
                BlazeWhite, BlazeRed, 1f, 0.02f, 0.45f, drag: 2.2f, delay: Rnd(0f, 0.12f), stretch: 0.1f, colorMid: BlazeYellow);
        }

        // 火の粉: ゆらゆら横に揺れながら高く昇って瞬く
        for (int i = 0; i < 40 * q; i++)
        {
            float s = Rnd(0.14f, 0.26f);
            Add(Shape.Star, c + new Vector2(Rnd(-0.5f, 0.5f) * r, Rnd(-0.4f, 0.6f) * r), new Vector2(Rnd(-1.2f, 1.2f), Rnd(0.5f, 2f)), Rnd(1f, 1.9f), s, s * 0.4f,
                BlazeWhite, BlazeOrange, 1f, 0.03f, 0.55f, drag: 1.2f, delay: Rnd(0f, 1.6f), rise: Rnd(0.8f, 1.8f), twinkle: 0.5f, twinkleSpeed: Rnd(12f, 22f));
        }

        // 黒煙: 炎の上から遅れて湧き、膨らみながら昇る (炎より奥に描く)
        for (int i = 0; i < 14 * q; i++)
        {
            Add(Shape.Cloud, c + new Vector2(Rnd(-0.3f, 0.3f) * r, Rnd(0.5f, 1f) * r), new Vector2(Rnd(-0.4f, 0.4f), 0f), Rnd(1.6f, 2.4f), 0.5f * r, Rnd(1.4f, 2.2f) * r,
                Ash, Charred, Rnd(0.4f, 0.6f), 0.15f, 0.45f, delay: Rnd(0.3f, 1.6f), spin: Rnd(-40f, 40f), rise: Rnd(0.9f, 1.5f), order: 0);
        }

        // 焦げ跡 (縁だけしばらく赤く燻る) と、火が収まった後に舞い落ちる灰
        Add(Shape.Cloud, f, Vector2.zero, 3.6f, 1.4f * r, 2.2f * r, Charred, Charred, 0.9f, 0.06f, 0.7f, rot: 0f, sy0: 0.55f * r, sy1: 0.8f * r, order: 0);
        Add(Shape.Ring, f, Vector2.zero, 2.4f, 1.6f * r, 2f * r, BlazeOrange, BlazeRed, 0.55f, 0.1f, 0.5f, delay: 0.3f, rot: 0f, twinkle: 0.4f, twinkleSpeed: 6f,
            sy0: 0.6f * r, sy1: 0.72f * r, order: 0);

        for (int i = 0; i < 16 * q; i++)
        {
            float s = Rnd(0.1f, 0.18f);
            Add(Shape.Glow, c + new Vector2(Rnd(-0.7f, 0.7f) * r, Rnd(0.4f, 1.1f) * r), new Vector2(Rnd(-0.25f, 0.25f), 0f), Rnd(1.3f, 2f), s, s * 0.7f,
                Ash, Charred, 0.8f, 0.1f, 0.6f, delay: Rnd(1.1f, 1.9f), rise: -0.6f);
        }
    }

    private static readonly Color VoidBlack = new(0.03f, 0.01f, 0.08f);
    private static readonly Color VoidDeep = new(0.16f, 0.04f, 0.32f);
    private static readonly Color VoidPurple = new(0.5f, 0.18f, 0.95f);
    private static readonly Color VoidMagenta = new(0.95f, 0.3f, 1f);
    private static readonly Color VoidBlue = new(0.3f, 0.45f, 1f);
    private static readonly Color VoidRim = new(0.9f, 0.85f, 1f);

    private static Color VoidVivid(int i) => (i % 3) switch
    {
        0 => VoidMagenta,
        1 => VoidBlue,
        _ => VoidRim
    };

    // 吸い込まれる: 足元に暗い渦が口を開け、縁の輪が内へ縮みながら光の筋と塵を巻き込む → 一点に潰れて白く弾け、
    // 小さな黒い点が残って消える。粒は寿命の終わりにちょうど中心へ届く向きと速さで放つ (少し回り込ませて渦に見せる)
    private static void SpawnSwallow(Vector2 c, float size)
    {
        float r = FxMath.Clamp(size, 0.5f, 3f);
        const float collapse = 0.95f;

        Impact(c, 2f * r, VoidPurple, 0.3f, 0.12f, 0.35f);

        // 渦の口: 膨らむ暗い円盤 → 潰れて閉じる (1 粒では膨らんで縮めないので 2 粒をつなぐ)
        Add(Shape.Glow, c, Vector2.zero, collapse, 1f * r, 4f * r, VoidPurple, VoidDeep, 0.6f, 0.08f, 0.8f, order: 0);
        Add(Shape.Cloud, c, Vector2.zero, collapse, 0.4f * r, 3f * r, VoidBlack, VoidBlack, 1f, 0.06f, 0.99f, spin: -220f, order: 1);
        Add(Shape.Cloud, c, Vector2.zero, collapse, 0.3f * r, 2.2f * r, VoidBlack, VoidBlack, 1f, 0.06f, 0.99f, spin: 160f, order: 1);
        Add(Shape.Cloud, c, Vector2.zero, 0.3f, 2.6f * r, 0.05f, VoidBlack, VoidDeep, 1f, 0.01f, 0.8f, delay: collapse - 0.02f, spin: -400f, order: 1);

        // 渦の腕: 中心を軸に回りながら縮んでいく光条 (根元が中心なので、回すと風車のように渦を巻く)
        for (int i = 0; i < 10; i++)
        {
            bool outer = i % 2 == 0;
            float len = (outer ? 3.4f : 2.6f) * r;
            Add(Shape.Ray, c, Vector2.zero, collapse, len, 0.2f * r, outer ? VoidMagenta : VoidBlue, VoidPurple, 0.85f, 0.15f, 0.85f,
                rot: i * 36f + Rnd(-8f, 8f), spin: outer ? -320f : -420f, sy0: 0.26f * r, sy1: 0.06f * r, order: 2);
        }

        // 内へ縮んでいく縁の輪
        for (int k = 0; k < 5; k++)
        {
            float from = (4.2f - k * 0.35f) * r;
            Add(Shape.Ring, c, Vector2.zero, collapse - k * 0.12f, from, 0.2f * r, k % 2 == 0 ? VoidRim : VoidMagenta, VoidPurple, 0.9f - k * 0.1f, 0.15f, 0.85f,
                delay: k * 0.12f);
        }

        // 巻き込まれる光の筋
        for (int i = 0; i < 70; i++)
        {
            Vector2 d = Dir();
            float R = Rnd(1.3f, 2.8f) * r;
            float life = Rnd(0.35f, 0.6f);
            float twist = Rnd(0.8f, 1.3f);
            Vector2 end = FxMath.V2(d.x * FxMath.Cos(twist) - d.y * FxMath.Sin(twist), d.x * FxMath.Sin(twist) + d.y * FxMath.Cos(twist)) * 0.15f * r;
            Vector2 start = d * R;
            Add(Shape.Star, c + start, (end - start) / life, life, Rnd(0.4f, 0.6f), 0.14f, VoidVivid(i), VoidPurple, 1f, 0.15f, 0.8f,
                delay: Rnd(0f, collapse - life), stretch: 0.09f);
        }

        // 引き寄せられる紫の塵
        for (int i = 0; i < 18; i++)
        {
            Vector2 d = Dir();
            Vector2 start = d * Rnd(1.5f, 2.6f) * r;
            float life = Rnd(0.5f, 0.75f);
            Add(Shape.Cloud, c + start, -start / life * 0.9f, life, 0.9f * r, 0.2f * r, VoidPurple, VoidDeep, 0.45f, 0.2f, 0.7f,
                delay: Rnd(0f, collapse - life), spin: Rnd(-200f, -80f));
        }

        // 潰れて弾ける
        Add(Shape.Glow, c, Vector2.zero, 0.35f, 0.3f * r, 2.4f * r, WindWhite, VoidMagenta, 1f, 0.01f, 0.3f, delay: collapse);
        Add(Shape.Star, c, Vector2.zero, 0.45f, 0.4f * r, 3.2f * r, WindWhite, VoidBlue, 1f, 0.01f, 0.3f, delay: collapse, spin: 200f);
        Add(Shape.Ring, c, Vector2.zero, 0.45f, 0.2f * r, 3f * r, VoidRim, VoidPurple, 0.9f, 0.01f, 0.35f, delay: collapse);

        for (int i = 0; i < 24; i++)
        {
            Add(Shape.Star, c, Dir() * Rnd(3f, 7f) * r, Rnd(0.35f, 0.6f), Rnd(0.3f, 0.45f), 0.1f, VoidVivid(i), VoidPurple, 1f, 0.01f, 0.5f,
                drag: 3f, delay: collapse, stretch: 0.1f);
        }

        Add(Shape.Glow, c, Vector2.zero, 0.9f, 0.35f * r, 0.1f, VoidBlack, VoidBlack, 0.9f, 0.05f, 0.5f, delay: collapse + 0.1f, order: 0);
    }

    // 虚空が破れる: 暗い球が渦を巻きながら膨らんで力を溜め、縁の輪が締まる → 紫の閃光と揺れとともに破れ、三重の輪と光条が走る
    // → 尾を引く光の筋と星屑が噴き出す → 破れ目の周りに星の瞬く紫の霧がしばらく漂う
    private static void SpawnVoidBurst(Vector2 c, float size)
    {
        float r = FxMath.Clamp(size, 0.5f, 3f);
        const float pop = 0.28f;

        // 溜め
        Add(Shape.Glow, c, Vector2.zero, pop + 0.05f, 0.5f * r, 2.6f * r, VoidPurple, VoidMagenta, 0.7f, 0.1f, 0.9f, order: 0);
        Add(Shape.Cloud, c, Vector2.zero, pop + 0.05f, 0.2f * r, 1.8f * r, VoidBlack, VoidBlack, 1f, 0.15f, 0.9f, spin: -300f, order: 1);
        Add(Shape.Ring, c, Vector2.zero, pop, 3f * r, 1.7f * r, VoidRim, VoidMagenta, 0.9f, 0.2f, 0.9f);

        for (int i = 0; i < 20; i++)
        {
            Vector2 d = Dir();
            Vector2 start = d * Rnd(1.4f, 2.2f) * r;
            Add(Shape.Star, c + start, -start / pop * 0.9f, pop, 0.4f, 0.15f, VoidVivid(i), VoidPurple, 1f, 0.2f, 0.9f, stretch: 0.06f);
        }

        // 破れる
        Impact(c, 2.5f * r, VoidMagenta, 0.26f, 0.16f, 0.35f, pop);
        Add(Shape.Cloud, c, Vector2.zero, 0.55f, 1.8f * r, 3.6f * r, VoidBlack, VoidDeep, 0.95f, 0.01f, 0.3f, delay: pop, spin: 120f, order: 1);
        Add(Shape.Glow, c, Vector2.zero, 0.45f, 0.6f * r, 3.6f * r, VoidRim, VoidMagenta, 1f, 0.01f, 0.3f, delay: pop);
        Add(Shape.Star, c, Vector2.zero, 0.55f, 0.4f * r, 4.4f * r, WindWhite, VoidPurple, 1f, 0.01f, 0.3f, delay: pop, spin: -150f);

        for (int k = 0; k < 3; k++)
        {
            Add(Shape.Ring, c, Vector2.zero, 0.55f + k * 0.12f, 0.6f * r, (3.8f + k * 1.2f) * r, k == 1 ? VoidMagenta : VoidRim, VoidPurple, 1f - k * 0.2f, 0.01f, 0.35f,
                delay: pop + k * 0.07f);
        }

        for (int i = 0; i < 12; i++)
        {
            float w = Rnd(0.14f, 0.24f) * r;
            Add(Shape.Ray, c, Vector2.zero, Rnd(0.35f, 0.55f), 0.4f * r, Rnd(2.8f, 4.2f) * r, VoidRim, VoidPurple, 0.9f, 0.01f, 0.3f,
                delay: pop, rot: i * 30f + Rnd(-12f, 12f), sy0: w, sy1: w * 0.2f);
        }

        // 尾を引いて噴き出す光の筋
        for (int i = 0; i < 26; i++)
        {
            Vector2 d = Dir();
            Add(Shape.Star, c + d * 0.3f * r, d * Rnd(6f, 11f) * r, Rnd(0.45f, 0.7f), Rnd(0.5f, 0.75f), 0.2f, VoidVivid(i), VoidPurple, 1f, 0.01f, 0.5f,
                drag: 3.2f, delay: pop, stretch: 0.14f);
        }

        for (int i = 0; i < 40; i++)
        {
            Add(Shape.Star, c, Dir() * Rnd(3f, 8f) * r, Rnd(0.5f, 0.9f), Rnd(0.3f, 0.5f), 0.12f, VoidVivid(i), VoidPurple, 1f, 0.01f, 0.5f,
                drag: 2.8f, delay: pop, stretch: 0.1f, twinkle: 0.3f, twinkleSpeed: Rnd(16f, 26f));
        }

        for (int i = 0; i < 10; i++)
        {
            Vector2 d = Dir();
            Add(Shape.Cloud, c + d * Rnd(0.2f, 0.8f) * r, d * Rnd(0.4f, 1f), Rnd(1.3f, 1.9f), 0.8f * r, Rnd(1.6f, 2.3f) * r, VoidPurple, VoidDeep, 0.4f, 0.08f, 0.45f,
                drag: 1f, delay: pop + Rnd(0.05f, 0.2f), spin: Rnd(-50f, 50f), order: 0);
        }

        for (int i = 0; i < 18; i++)
        {
            Add(Shape.Star, c + FxMath.InsideUnitCircle() * 1.6f * r, FxMath.InsideUnitCircle() * 0.3f, Rnd(1f, 1.7f), Rnd(0.24f, 0.36f), 0.14f, VoidVivid(i), VoidBlue, 1f, 0.1f, 0.55f,
                delay: pop + Rnd(0.1f, 0.5f), twinkle: 0.8f, twinkleSpeed: Rnd(8f, 14f));
        }
    }

    private static readonly Color SmokeWhite = new(0.93f, 0.93f, 0.96f);
    private static readonly Color SmokeGrey = new(0.62f, 0.63f, 0.68f);
    private static readonly Color SmokeShade = new(0.38f, 0.39f, 0.45f);

    // 煙玉: 小さく弾ける閃光 → 一瞬で体を覆い隠すほど膨らむ白い煙 (奥に影の煙を重ねて立体に見せる) と床を這う煙の輪
    // → ほどけながら昇って消える。煙の奥で消えた / 現れたように見えるよう、最初の 0.5 秒で体を覆い切る
    private static void SpawnSmoke(Vector2 c, float size)
    {
        float r = FxMath.Clamp(size, 0.5f, 2.5f);
        Vector2 f = c + Feet;

        Add(Shape.Glow, c, Vector2.zero, 0.25f, 0.3f * r, 2f * r, WindWhite, SmokeWhite, 0.9f, 0.01f, 0.3f);
        Add(Shape.Ring, c, Vector2.zero, 0.35f, 0.3f * r, 2.6f * r, WindWhite, SmokeGrey, 0.7f, 0.01f, 0.35f);
        Add(Shape.Ring, f, Vector2.zero, 0.6f, 0.4f * r, 4f * r, SmokeWhite, SmokeGrey, 0.8f, 0.02f, 0.4f, rot: 0f, sy0: 0.14f * r, sy1: 1.4f * r);

        // 影の煙 (奥)
        for (int i = 0; i < 18; i++)
        {
            Vector2 d = Dir();
            Add(Shape.Cloud, c + d * 0.2f * r + new Vector2(0f, -0.1f * r), d * Rnd(1.5f, 3f) * r, Rnd(1.3f, 1.8f), 0.7f * r, Rnd(2f, 2.6f) * r, SmokeGrey, SmokeShade,
                1f, 0.03f, 0.5f, drag: 3.2f, spin: Rnd(-60f, 60f), rise: 0.15f, order: 0);
        }

        // 手前の白い煙
        for (int i = 0; i < 32; i++)
        {
            Vector2 d = Dir();
            Add(Shape.Cloud, c + d * 0.15f * r, d * Rnd(1.2f, 3.2f) * r, Rnd(1.1f, 1.6f), 0.6f * r, Rnd(1.5f, 2.1f) * r, SmokeWhite, SmokeGrey,
                1f, 0.02f, 0.45f, drag: 3.6f, spin: Rnd(-80f, 80f), rise: Rnd(0.1f, 0.35f));
        }

        // 床を這って広がる煙
        for (int i = 0; i < 12; i++)
        {
            float ang = Rnd(0f, 2f * FxMath.PI);
            Vector2 dir = FxMath.V2(FxMath.Cos(ang), FxMath.Sin(ang) * 0.4f);
            Add(Shape.Cloud, f + dir * 0.3f * r, dir * Rnd(3f, 5f) * r, Rnd(1f, 1.4f), 0.4f * r, Rnd(1f, 1.4f) * r, SmokeWhite, SmokeGrey, 0.7f, 0.03f, 0.4f,
                drag: 2.6f, spin: Rnd(-50f, 50f));
        }

        // ほどけて昇る細い煙と、煙の中できらめく粒
        for (int i = 0; i < 10; i++)
        {
            Add(Shape.Cloud, c + FxMath.InsideUnitCircle() * 0.7f * r, new Vector2(Rnd(-0.3f, 0.3f), Rnd(0.4f, 0.8f)), Rnd(1.2f, 1.8f), 0.4f * r, Rnd(0.9f, 1.3f) * r,
                SmokeWhite, SmokeGrey, 0.5f, 0.1f, 0.4f, delay: Rnd(0.4f, 0.8f), spin: Rnd(-40f, 40f), sy0: 0.25f * r, sy1: 0.6f * r);
        }

        for (int i = 0; i < 10; i++)
        {
            Add(Shape.Star, c + FxMath.InsideUnitCircle() * 1f * r, Vector2.zero, Rnd(0.4f, 0.7f), 0.3f, 0.1f, WindWhite, SmokeWhite, 1f, 0.1f, 0.5f,
                delay: Rnd(0.05f, 0.5f), twinkle: 0.6f, twinkleSpeed: Rnd(14f, 22f));
        }
    }

    private static readonly Color Soil = new(0.46f, 0.33f, 0.21f);
    private static readonly Color SoilDark = new(0.24f, 0.16f, 0.1f);
    private static readonly Color Hole = new(0.07f, 0.05f, 0.04f);

    // 地面に残る穴: 暗い穴と、掘り返した土の縁
    private static void BurrowHole(Vector2 f, float r, float delay)
    {
        Add(Shape.Cloud, f, Vector2.zero, 2.2f, 0.4f * r, 1.3f * r, Hole, SoilDark, 0.95f, 0.05f, 0.65f, delay: delay, rot: 0f, sy0: 0.16f * r, sy1: 0.5f * r, order: 0);
        Add(Shape.Ring, f, Vector2.zero, 2.2f, 0.6f * r, 1.6f * r, Soil, SoilDark, 0.9f, 0.05f, 0.65f, delay: delay, rot: 0f, sy0: 0.24f * r, sy1: 0.6f * r, order: 0);

        for (int i = 0; i < 9; i++)
        {
            float ang = Rnd(0f, 2f * FxMath.PI);
            float s = Rnd(0.14f, 0.26f) * r;
            Add(Shape.Cloud, f + FxMath.V2(FxMath.Cos(ang) * 0.75f * r, FxMath.Sin(ang) * 0.3f * r), Vector2.zero, 2.2f, s * 1.6f, s * 1.6f, Soil, SoilDark, 1f, 0.05f, 0.65f,
                delay: delay, rot: Rnd(0f, 360f), sy0: s, sy1: s, order: 0);
        }
    }

    // 土を跳ね上げる (潜る時は外へ低く、出てくる時は真上へ高く)
    private static void SoilSpray(Vector2 f, float r, int count, float up, float side, float delay)
    {
        for (int i = 0; i < count; i++)
        {
            float s = Rnd(0.22f, 0.4f) * r;
            Add(Shape.Cloud, f + new Vector2(Rnd(-0.3f, 0.3f) * r, 0f), new Vector2(Rnd(-side, side) * r, Rnd(up * 0.5f, up) * r), Rnd(0.7f, 1.1f), s, s * 0.8f, Soil, SoilDark,
                1f, 0.01f, 0.7f, drag: 0.8f, delay: delay + Rnd(0f, 0.25f), spin: Rnd(-540f, 540f), rise: -6f);
        }
    }

    // 地面に潜る: 足元の地面が渦を巻いて削れ、土が外へ跳ね上がる → 体が土煙に呑まれる → 掘り返した穴がしばらく残る
    private static void SpawnBurrowIn(Vector2 c, float size)
    {
        float r = FxMath.Clamp(size, 0.5f, 2.5f);
        Vector2 f = c + Feet;

        Impact(c, 1.2f * r, new Color(0.9f, 0.8f, 0.65f), 0.12f, 0.14f, 0.45f);

        for (int k = 0; k < 3; k++)
        {
            float from = (3.2f - k * 0.6f) * r;
            Add(Shape.Ring, f, Vector2.zero, 0.5f, from, 0.4f * r, Dust, SoilDark, 0.8f, 0.1f, 0.8f, delay: k * 0.1f, rot: 0f, sy0: from * 0.38f, sy1: 0.15f * r);
        }

        // 足元へ吸い込まれながら渦を巻く土煙
        for (int i = 0; i < 16; i++)
        {
            float ang = Rnd(0f, 2f * FxMath.PI);
            Vector2 at = FxMath.V2(FxMath.Cos(ang) * 1.6f * r, FxMath.Sin(ang) * 0.6f * r);
            Vector2 tangent = FxMath.V2(-FxMath.Sin(ang) * 1.6f, FxMath.Cos(ang) * 0.6f);
            Add(Shape.Cloud, f + at, -at * 1.4f + tangent * 1.2f * r, Rnd(0.5f, 0.8f), 1.2f * r, 0.3f * r, Dust, DustDark, 0.7f, 0.1f, 0.6f,
                delay: Rnd(0f, 0.25f), spin: Rnd(-200f, -100f));
        }

        SoilSpray(f, r, 26, 5f, 4.5f, 0.05f);

        // 体を呑む土煙
        for (int i = 0; i < 22; i++)
        {
            Vector2 d = Dir();
            Add(Shape.Cloud, c + d * 0.2f * r + new Vector2(0f, -0.2f * r), d * Rnd(1f, 2.4f) * r, Rnd(1f, 1.5f), 0.6f * r, Rnd(1.5f, 2f) * r, i % 3 == 0 ? Soil : Dust, DustDark, 1f, 0.03f, 0.45f,
                drag: 2.8f, delay: Rnd(0.1f, 0.3f), spin: Rnd(-60f, 60f), rise: 0.2f);
        }

        BurrowHole(f, r, 0.2f);
    }

    // 地面から這い出る: 地面が盛り上がって割れ、閃光とともに土柱が真上へ噴き上がる → 降ってくる土と広がる土煙 → 穴が残る
    private static void SpawnBurrowOut(Vector2 c, float size)
    {
        float r = FxMath.Clamp(size, 0.5f, 2.5f);
        Vector2 f = c + Feet;

        Impact(c, 1.5f * r, new Color(0.95f, 0.85f, 0.7f), 0.18f, 0.2f, 0.45f);

        Add(Shape.Ray, f, Vector2.zero, 0.6f, 1.4f * r, 4f * r, Dust, DustDark, 0.9f, 0.02f, 0.4f, rot: 90f, sy0: 1.4f * r, sy1: 0.5f * r);
        Add(Shape.Ring, f, Vector2.zero, 0.7f, 0.4f * r, 4.4f * r, Dust, DustDark, 1f, 0.02f, 0.4f, rot: 0f, sy0: 0.15f * r, sy1: 1.6f * r);
        Add(Shape.Ring, f, Vector2.zero, 0.8f, 0.3f * r, 3f * r, WindWhite, Dust, 0.8f, 0.02f, 0.4f, delay: 0.06f, rot: 0f, sy0: 0.12f * r, sy1: 1.1f * r);

        // 噴き上がる土柱
        for (int i = 0; i < 24; i++)
        {
            Add(Shape.Cloud, f + new Vector2(Rnd(-0.3f, 0.3f) * r, 0f), new Vector2(Rnd(-0.6f, 0.6f), Rnd(3f, 7f) * r), Rnd(0.7f, 1.1f), 0.7f * r, Rnd(1.3f, 1.8f) * r,
                i % 3 == 0 ? Soil : Dust, DustDark, 1f, 0.02f, 0.5f, drag: 3f, delay: Rnd(0f, 0.12f), spin: Rnd(-80f, 80f), stretch: 0.05f);
        }

        SoilSpray(f, r, 34, 8f, 3f, 0f);

        for (int i = 0; i < 18; i++)
        {
            float ang = Rnd(0f, 2f * FxMath.PI);
            Vector2 dir = FxMath.V2(FxMath.Cos(ang), FxMath.Sin(ang) * 0.4f);
            Add(Shape.Cloud, f + dir * 0.3f * r, dir * Rnd(3.5f, 6f) * r, Rnd(1f, 1.6f), 0.5f * r, Rnd(1.3f, 2f) * r, Dust, DustDark, 0.65f, 0.05f, 0.35f,
                drag: 2.4f, delay: Rnd(0.05f, 0.15f), spin: Rnd(-90f, 90f), rise: 0.3f);
        }

        BurrowHole(f, r, 0f);
    }

    // ── パーティクル ───────────────────────────────────────────────────

    // sy0/sy1 を省略すると縦横同じ倍率。stretch > 0 は速度方向へ尾を伸ばして進行方向を向く。
    // ── 波動砲 ─────────────────────────────────────────────────────────

    private const byte TagCircle = 1;
    private const byte TagBeam = 2;

    // 片付けの宛先はゲートの中心そのもの (同じ撃ち手なら座標が一致する)。近くにいる別の撃ち手の演出は巻き込まない
    private const float CannonAnchorReach = 0.3f;

    // ツインは 2 本同時に撃つので、画面の閃光と揺れはフレームごとに 1 回だけにする
    private static int _cannonFlashFrame = -1;

    // after > 0 はその秒数あとに一回り小さな余震を重ねる
    // rumble > 0 はその秒数のあいだ弱い揺れを続ける (撃ち続けている間の地響き)
    private static void CannonImpact(Vector2 c, float r, Color flash, float alpha, float shake, float duration, float after = 0f, float rumble = 0f)
    {
        if (_cannonFlashFrame == Time.frameCount) return;

        _cannonFlashFrame = Time.frameCount;
        Impact(c, r, flash, alpha, shake, duration);
        if (after > 0f) Impact(c, r * 0.7f, CannonWhite, alpha * 0.45f, shake * 0.6f, duration * 0.6f, after);
        if (rumble > 0f) Impact(c, r, flash, 0f, shake * 0.3f, rumble, after + duration * 0.6f);
    }

    // 発射の瞬間に砲口から弾ける光: 一瞬の白い膨らみ、放射状の光の筋、ビームに沿って走る衝撃の輪
    private static void CannonMuzzleBurst(Vector2 o, float s, float length, float width, float grow, CannonColors k)
    {
        Add(Shape.Glow, o, Vector2.zero, 0.32f, width * 1.5f, width * 7f, CannonWhite, k.Light, 1f, 0.01f, 0.25f, order: 11);

        for (int i = 0; i < 14; i++)
        {
            // 前方へ寄せた扇 (後ろへは短く)
            float ang = (i / 14f) * 360f + Rnd(-8f, 8f);
            float fwd = FxMath.Cos(ang * FxMath.PI / 180f) * s;
            float reach = fwd > 0f ? width * Rnd(3.5f, 5.5f) : width * Rnd(1.6f, 2.6f);
            Add(Shape.Ray, o, Vector2.zero, Rnd(0.22f, 0.36f), width * 0.4f, reach, CannonWhite, CannonAccent(k, i), Rnd(0.75f, 1f), 0.01f, 0.2f,
                rot: ang, sy0: width * 0.35f, sy1: width * 0.06f, order: 10);
        }

        for (int j = 0; j < 3; j++)
        {
            float f = 0.22f + j * 0.28f;
            var p = FxMath.V2(o.x + s * length * f, o.y);
            Add(Shape.Ring, p, Vector2.zero, 0.4f, 0.1f, width * 1.4f, CannonWhite, k.Light, 0.95f, 0.01f, 0.25f,
                delay: grow * f, rot: 0f, sy0: width * 0.6f, sy1: width * 3.6f, order: 7);
        }
    }

    // WaveCannon / JackalHadouHo / SuperCannonShot の幾何と同じ値 (文字のビームと重ねるため)
    private const float CannonGateRadius = 0.5f;
    private const float CannonLengthPerThickness = 9f; // 20 文字 × size 30 × 0.015
    private const float CannonSweepLengthPerThickness = 18f; // ダイナミックは 40 文字

    // 光線の見た目の太さ。当たり判定の幅 (太さ 1 あたり 0.545) より少し細く、外側の霞が判定の縁まで届く
    private static float CannonWidth(float th) => 0.42f * th + 0.4f;

    private static readonly Color CannonWhite = new(1f, 1f, 1f);

    private readonly record struct CannonColors(Color Light, Color Main, Color Deep, Color Rune, bool Rainbow, bool Void);

    private static readonly CannonColors[] CannonPalettes =
    [
        new(new Color(1f, 0.88f, 0.55f), new Color(1f, 0.45f, 0.08f), new Color(0.7f, 0.14f, 0.02f), new Color(1f, 0.78f, 0.3f), false, false),
        new(new Color(0.65f, 0.96f, 1f), new Color(0f, 0.7f, 0.92f), new Color(0.05f, 0.25f, 0.72f), new Color(0.5f, 0.9f, 1f), false, false),
        new(new Color(0.9f, 0.95f, 1f), new Color(0.6f, 0.78f, 1f), new Color(0.45f, 0.2f, 0.85f), new Color(1f, 0.9f, 0.6f), true, false),
        new(new Color(1f, 0.5f, 0.42f), new Color(1f, 0.08f, 0.1f), new Color(0.45f, 0f, 0.06f), new Color(1f, 0.28f, 0.22f), false, false),
        new(new Color(0.86f, 0.62f, 1f), new Color(0.58f, 0.12f, 1f), new Color(0.1f, 0f, 0.2f), new Color(0.72f, 0.38f, 1f), false, true)
    ];

    private static readonly Color[] RainbowCols =
    [
        new(1f, 0.2f, 0.2f), new(1f, 0.6f, 0.1f), new(1f, 0.95f, 0.2f), new(0.2f, 1f, 0.35f), new(0.2f, 0.55f, 1f), new(0.45f, 0.2f, 0.9f), new(0.8f, 0.3f, 1f)
    ];

    private static CannonColors CannonPal(float b) => CannonPalettes[FxMath.Clamp((int)b, 0, CannonPalettes.Length - 1)];

    // 流れる光や飛沫の色。虹の時だけ七色を順に回す
    private static Color CannonAccent(CannonColors k, int i) => k.Rainbow ? RainbowCols[i % RainbowCols.Length] : (i % 3 == 0 ? CannonWhite : k.Light);

    private static float CannonThickness(float a) => a < 1f ? 2f : FxMath.Clamp(a, 1f, 8f);

    // 魔法陣の直径 (ビームが太いほど大きい)
    private static float SigilSize(float th) => 1.7f + th * 0.14f;

    // ── 吸血・毒・石化・操り糸・竜巻 ─────────────────────────────────────

    private static readonly Color BatBlack = new(0.08f, 0.02f, 0.07f);

    // クルーの血はその人の体の色 (明るい飛沫・本体・影)。色が分からなければ赤
    internal static void BloodColors(int colorId, out Color bright, out Color main, out Color dark)
    {
        if (colorId < 0 || colorId >= Palette.PlayerColors.Length)
        {
            bright = FxMath.Rgba(1f, 0.24f, 0.3f);
            main = FxMath.Rgba(0.78f, 0.03f, 0.1f);
            dark = FxMath.Rgba(0.3f, 0.01f, 0.05f);
            return;
        }

        Color32 m = Palette.PlayerColors[colorId];
        Color32 d = Palette.ShadowColors[colorId];
        main = FxMath.Rgba(m.r / 255f, m.g / 255f, m.b / 255f);
        bright = FxMath.Rgba(0.6f * main.r + 0.4f, 0.6f * main.g + 0.4f, 0.6f * main.b + 0.4f);
        dark = FxMath.Rgba(d.r / 255f * 0.6f, d.g / 255f * 0.6f, d.b / 255f * 0.6f);
    }
    private static readonly Color FangWhite = new(1f, 0.92f, 0.94f);

    // 血を吸われる: 首筋の牙の跡が 2 つ光る → 体の周りから血の粒が頭上の一点へ吸い上げられ、血の霧が立ちのぼる
    // → 集まった血が弾けてコウモリの群れになって飛び去る → 倒れた足元に血だまりが残る
    private static void SpawnDrain(Vector2 c, int victimId)
    {
        int colorId = -1;

        try
        {
            if (victimId is >= 0 and <= 254 && GameData.Instance) colorId = GameData.Instance.GetPlayerById((byte)victimId)?.DefaultOutfit.ColorId ?? -1;
        }
        catch (System.Exception e) { Utils.ThrowException(e); }

        BloodColors(colorId, out Color BloodBright, out Color Blood, out Color BloodDark);
        Vector2 f = c + Feet;
        Vector2 top = c + new Vector2(0f, 1.35f);
        const float gather = 0.8f;
        float q = Active.Count > 1200 ? 0.5f : 1f;

        Impact(c, 1.6f, Blood, 0.28f, 0.06f, 0.2f);

        // 牙が刺さる: 首筋 (バイザーより下) に一瞬だけ光る 2 つの刺し傷と、垂れる血
        for (int s = -1; s <= 1; s += 2)
        {
            Vector2 n = c + new Vector2(0.18f + 0.07f * s, -0.08f);
            Add(Shape.Star, n, Vector2.zero, 0.3f, 0.4f, 0.1f, FangWhite, BloodBright, 1f, 0.02f, 0.3f, rot: 45f);
            Add(Shape.Glow, n, Vector2.zero, 0.35f, 0.25f, 0.4f, BloodBright, Blood, 0.9f, 0.02f, 0.4f);
            Add(Shape.Glow, n, new Vector2(0f, -0.55f), 0.55f, 0.09f, 0.06f, BloodBright, Blood, 1f, 0.05f, 0.6f, delay: 0.1f, sy0: 0.16f, sy1: 0.1f);
        }

        // 体を包む血の色の脈動
        Add(Shape.Glow, c, Vector2.zero, 1.1f, 1.4f, 1.1f, Blood, BloodDark, 0.6f, 0.05f, 0.5f, twinkle: 0.5f, twinkleSpeed: 16f, sy0: 2f, sy1: 1.6f, order: 0);

        // 吸い上げられる血の粒 (寿命の終わりにちょうど頭上の一点へ届く速さで放つ)
        for (int i = 0; i < 80 * q; i++)
        {
            Vector2 start = c + new Vector2(Rnd(-0.45f, 0.45f), Rnd(-0.55f, 0.35f));
            float life = Rnd(0.3f, 0.5f);
            Vector2 end = top + new Vector2(Rnd(-0.08f, 0.08f), Rnd(-0.08f, 0.08f));
            Add(Shape.Star, start, (end - start) / life, life, Rnd(0.32f, 0.46f), 0.12f, i % 4 == 0 ? BloodBright : Blood, BloodDark, 1f, 0.15f, 0.85f,
                delay: Rnd(0.05f, gather - life), stretch: 0.1f);
        }

        for (int i = 0; i < 10 * q; i++)
        {
            Add(Shape.Cloud, c + new Vector2(Rnd(-0.35f, 0.35f), Rnd(-0.3f, 0.4f)), new Vector2(Rnd(-0.2f, 0.2f), 0f), Rnd(0.9f, 1.3f), 0.4f, Rnd(1f, 1.4f),
                Blood, BloodDark, 0.6f, 0.15f, 0.5f, delay: Rnd(0f, 0.5f), spin: Rnd(-60f, 60f), rise: Rnd(0.6f, 1.1f), order: 0);
        }

        // 頭上に集まって膨らむ血の玉 → 弾ける
        Add(Shape.Glow, top, Vector2.zero, gather, 0.15f, 0.85f, BloodBright, Blood, 0.95f, 0.1f, 0.95f);
        Add(Shape.Star, top, Vector2.zero, gather, 0.1f, 0.6f, FangWhite, BloodBright, 0.8f, 0.2f, 0.95f, spin: 240f);
        Add(Shape.Glow, top, Vector2.zero, 0.3f, 0.8f, 2.2f, FangWhite, BloodBright, 1f, 0.01f, 0.2f, delay: gather);
        Add(Shape.Ring, top, Vector2.zero, 0.45f, 0.2f, 2.6f, BloodBright, BloodDark, 0.9f, 0.01f, 0.3f, delay: gather);

        for (int i = 0; i < 16 * q; i++)
        {
            Add(Shape.Glow, top, Dir() * Rnd(2.5f, 5f), Rnd(0.35f, 0.55f), 0.16f, 0.08f, BloodBright, Blood, 1f, 0.01f, 0.5f, drag: 3f, delay: gather, rise: -2f);
        }

        // コウモリの群れ: 上半分へ羽ばたいて散る (縦を潰したり戻したりして羽ばたかせる)
        for (int i = 0; i < 18 * q; i++)
        {
            float ang = Rnd(15f, 165f) / FxMath.Rad2Deg;
            float s = Rnd(0.5f, 0.75f);
            Vector2 v = FxMath.V2(FxMath.Cos(ang), FxMath.Sin(ang)) * Rnd(2.5f, 4.5f);
            Add(Shape.Bat, top, v, Rnd(1.1f, 1.5f), s * 0.7f, s, BatBlack, BloodDark, 1f, 0.03f, 0.7f,
                drag: 0.6f, delay: gather + Rnd(0f, 0.12f), rot: FxMath.Clamp(-v.x * 6f, -25f, 25f), sy0: s * 0.7f, sy1: s, flap: Rnd(16f, 24f));
        }

        // 血だまり (倒れた後もしばらく残る)
        Add(Shape.Cloud, f, Vector2.zero, 2.8f, 0.3f, 1.4f, Blood, BloodDark, 0.85f, 0.05f, 0.7f, delay: 0.45f, rot: 0f, sy0: 0.12f, sy1: 0.5f, order: 0);
        Add(Shape.Glow, f, Vector2.zero, 2.4f, 0.4f, 1.2f, BloodBright, BloodDark, 0.5f, 0.05f, 0.6f, delay: 0.5f, sy0: 0.14f, sy1: 0.4f, order: 0);
    }

    private static readonly Color ToxLime = new(0.75f, 1f, 0.3f);
    private static readonly Color ToxGreen = new(0.28f, 0.8f, 0.12f);
    private static readonly Color ToxPurple = new(0.55f, 0.15f, 0.85f);
    private static readonly Color ToxDeep = new(0.2f, 0.06f, 0.28f);

    // 毒が回る: 体が緑と紫に脈打ち、足元から毒の輪が這い上がる → 泡が次々に湧いて昇り、てっぺんで弾ける
    // → 紫の毒気が立ちのぼり、しずくが垂れる → 倒れた足元に泡立つ毒の水たまりが残る
    private static void SpawnPoison(Vector2 c)
    {
        Vector2 f = c + Feet;
        float q = Active.Count > 1200 ? 0.5f : 1f;

        Impact(c, 1.5f, ToxGreen, 0.22f, 0.05f, 0.2f);

        Add(Shape.Glow, c, Vector2.zero, 1.2f, 1.3f, 1.5f, ToxGreen, ToxPurple, 0.85f, 0.05f, 0.55f, twinkle: 0.5f, twinkleSpeed: 14f, sy0: 1.9f, sy1: 2.2f, order: 0);
        Add(Shape.Ring, f, Vector2.zero, 0.9f, 0.3f, 2.6f, ToxLime, ToxPurple, 0.9f, 0.02f, 0.4f, rot: 0f, sy0: 0.1f, sy1: 0.9f);
        Add(Shape.Ring, f, Vector2.zero, 1f, 0.3f, 1.8f, ToxGreen, ToxDeep, 0.8f, 0.02f, 0.4f, delay: 0.12f, rot: 0f, sy0: 0.1f, sy1: 0.65f);

        // 泡: 等速で昇らせ、弾ける位置は寿命から出す
        for (int i = 0; i < 34 * q; i++)
        {
            Vector2 p0 = c + new Vector2(Rnd(-0.4f, 0.4f), Rnd(-0.5f, 0.3f));
            var v = new Vector2(Rnd(-0.25f, 0.25f), Rnd(0.7f, 1.4f));
            float life = Rnd(0.45f, 0.9f);
            float d = Rnd(0f, 1.1f);
            float s = Rnd(0.24f, 0.42f);
            Add(Shape.Ring, p0, v, life, s * 0.4f, s, i % 3 == 0 ? ToxLime : ToxGreen, ToxPurple, 1f, 0.1f, 0.9f, delay: d, rot: 0f);
            Add(Shape.Glow, p0, v, life, s * 0.3f, s * 0.9f, ToxGreen, ToxPurple, 0.6f, 0.1f, 0.9f, delay: d);
            Add(Shape.Star, p0 + v * life, Vector2.zero, 0.18f, s * 0.8f, s * 1.7f, WindWhite, ToxLime, 0.9f, 0.01f, 0.2f, delay: d + life);
        }

        for (int i = 0; i < 12 * q; i++)
        {
            Add(Shape.Cloud, c + new Vector2(Rnd(-0.35f, 0.35f), Rnd(-0.1f, 0.5f)), new Vector2(Rnd(-0.25f, 0.25f), 0f), Rnd(1f, 1.5f), 0.4f, Rnd(1f, 1.5f),
                ToxPurple, ToxDeep, 0.6f, 0.15f, 0.5f, delay: Rnd(0.1f, 0.9f), spin: Rnd(-50f, 50f), rise: Rnd(0.5f, 0.9f), order: 0);
        }

        for (int i = 0; i < 10 * q; i++)
        {
            Add(Shape.Glow, c + new Vector2(Rnd(-0.35f, 0.35f), Rnd(-0.3f, 0.2f)), new Vector2(0f, -0.4f), Rnd(0.4f, 0.6f), 0.12f, 0.08f, ToxLime, ToxGreen, 1f, 0.05f, 0.7f,
                delay: Rnd(0.1f, 1f), rise: -1.2f, sy0: 0.2f, sy1: 0.14f);
        }

        // 毒の水たまり と、その上でしばらく弾ける小さな泡
        Add(Shape.Cloud, f, Vector2.zero, 2.8f, 0.3f, 1.6f, ToxGreen, ToxDeep, 0.95f, 0.05f, 0.7f, delay: 0.3f, rot: 0f, sy0: 0.12f, sy1: 0.55f, order: 0);
        Add(Shape.Ring, f, Vector2.zero, 2.4f, 1.1f, 1.4f, ToxLime, ToxPurple, 0.5f, 0.1f, 0.6f, delay: 0.5f, rot: 0f, twinkle: 0.4f, twinkleSpeed: 5f, sy0: 0.42f, sy1: 0.5f, order: 0);

        for (int i = 0; i < 10 * q; i++)
        {
            float s = Rnd(0.1f, 0.18f);
            Vector2 p = f + new Vector2(Rnd(-0.55f, 0.55f), Rnd(-0.14f, 0.14f));
            float d = Rnd(0.7f, 2.4f);
            Add(Shape.Ring, p, new Vector2(0f, 0.15f), 0.35f, s * 0.3f, s, ToxLime, ToxGreen, 0.9f, 0.1f, 0.8f, delay: d, rot: 0f);
            Add(Shape.Star, p + new Vector2(0f, 0.05f), Vector2.zero, 0.12f, s, s * 1.8f, WindWhite, ToxLime, 0.8f, 0.01f, 0.2f, delay: d + 0.35f);
        }
    }

    private static readonly Color GazeGreen = new(0.6f, 1f, 0.72f);
    private static readonly Color StonePale = new(0.8f, 0.78f, 0.75f);

    // 死体が石になる: 緑の閃き → 灰色の波が床を走り、死体の色が抜けて石の灰色になる → ひびが入り、砂埃と小石がこぼれる
    private static void SpawnPetrify(Vector2 c)
    {
        Impact(c, 1.4f, GazeGreen, 0.2f, 0.1f, 0.25f);

        Add(Shape.Glow, c, Vector2.zero, 0.45f, 0.4f, 2.4f, WindWhite, GazeGreen, 1f, 0.02f, 0.3f);
        Add(Shape.Star, c, Vector2.zero, 0.5f, 0.4f, 2.2f, WindWhite, GazeGreen, 1f, 0.02f, 0.3f, rot: 0f);
        Add(Shape.Ring, c, Vector2.zero, 0.8f, 0.3f, 3.6f, StonePale, StoneDark, 1f, 0.02f, 0.4f, rot: 0f, sy0: 0.12f, sy1: 1.3f);
        Add(Shape.Ring, c, Vector2.zero, 0.9f, 0.3f, 2.4f, Stone, StoneDark, 0.8f, 0.02f, 0.4f, delay: 0.1f, rot: 0f, sy0: 0.1f, sy1: 0.9f);

        // 石の殻が張り付いていく
        for (int i = 0; i < 10; i++)
        {
            float s = Rnd(0.16f, 0.28f);
            Add(Shape.Chunk, c + new Vector2(Rnd(-0.45f, 0.45f), Rnd(-0.15f, 0.25f)), Vector2.zero, 2.2f, 0.04f, s, i % 2 == 0 ? Stone : StonePale, StoneDark, 0.9f, 0.1f, 0.75f,
                delay: 0.2f + i * 0.03f, sy0: 0.03f, sy1: s * 0.7f);
        }

        // ひび: 死体の上を折れ線で走る
        for (int i = 0; i < 4; i++)
        {
            float ang = Rnd(0f, 360f);
            Vector2 from = c + new Vector2(Rnd(-0.25f, 0.25f), Rnd(-0.05f, 0.2f));

            for (int j = 0; j < 3; j++)
            {
                ang += Rnd(-40f, 40f);
                float a = ang / FxMath.Rad2Deg;
                float l = Rnd(0.12f, 0.22f);
                Vector2 to = from + FxMath.V2(FxMath.Cos(a) * l, FxMath.Sin(a) * l * 0.6f);
                float dx = to.x - from.x, dy = to.y - from.y;
                float w = 0.05f * (1f - j * 0.25f);
                float seg = FxMath.Sqrt(dx * dx + dy * dy) + w;
                Add(Shape.Solid, FxMath.V2((from.x + to.x) * 0.5f, (from.y + to.y) * 0.5f), Vector2.zero, 1.8f, seg, seg, StoneDark, StoneDark, 0.85f, 0.02f, 0.7f,
                    delay: 0.55f + j * 0.04f, rot: FxMath.Atan2(dy, dx) * FxMath.Rad2Deg, sy0: w, sy1: w);
                from = to;
            }
        }

        for (int i = 0; i < 12; i++)
        {
            float ang = Rnd(0f, 2f * FxMath.PI);
            Vector2 dir = FxMath.V2(FxMath.Cos(ang), FxMath.Sin(ang) * 0.5f);
            Add(Shape.Cloud, c + dir * 0.3f, dir * Rnd(1.5f, 3f), Rnd(0.9f, 1.3f), 0.4f, Rnd(0.9f, 1.3f), StonePale, Stone, 0.55f, 0.05f, 0.4f,
                drag: 2.2f, delay: Rnd(0.5f, 0.7f), spin: Rnd(-60f, 60f), rise: 0.3f);
        }

        for (int i = 0; i < 10; i++)
        {
            float s = Rnd(0.1f, 0.18f);
            Add(Shape.Chunk, c + new Vector2(Rnd(-0.4f, 0.4f), Rnd(0f, 0.25f)), new Vector2(Rnd(-1.2f, 1.2f), Rnd(0.8f, 2f)), Rnd(0.6f, 0.9f), s, s, Stone, StoneDark, 1f, 0.01f, 0.7f,
                drag: 0.8f, delay: Rnd(0.55f, 0.75f), spin: Rnd(-540f, 540f), rise: -4f);
        }

        // 石の艶
        Add(Shape.Star, c + new Vector2(0.15f, 0.15f), Vector2.zero, 0.5f, 0.2f, 0.7f, WindWhite, StonePale, 0.9f, 0.1f, 0.4f, delay: 0.95f, rot: 0f);

        StoneTints.Add(new StoneTint { Pos = c, Start = Time.time + 0.2f });
    }

    // 石にする死体の色は数フレームかけて灰色へ寄せる (瞬時に変えると閃光の裏で色が飛んで見えない)
    private struct StoneTint
    {
        public Vector2 Pos;
        public float Start;
        public Material[] Mats;
        public Color[] From;
    }

    private static readonly List<StoneTint> StoneTints = [];
    private const float StoneTintTime = 0.7f;

    private static void TickStoneTints()
    {
        for (int i = StoneTints.Count - 1; i >= 0; i--)
        {
            StoneTint st = StoneTints[i];
            if (Time.time < st.Start) continue;

            try
            {
                if (st.Mats == null)
                {
                    DeadBody body = null;
                    float best = 1.2f;

                    foreach (DeadBody b in Object.FindObjectsOfType<DeadBody>())
                    {
                        if (!b) continue;
                        Vector3 bp = b.transform.position;
                        float d = FxMath.Sqrt((bp.x - st.Pos.x) * (bp.x - st.Pos.x) + (bp.y - st.Pos.y) * (bp.y - st.Pos.y));
                        if (d >= best) continue;
                        best = d;
                        body = b;
                    }

                    if (!body)
                    {
                        StoneTints.RemoveAt(i);
                        continue;
                    }

                    var mats = new List<Material>();

                    foreach (SpriteRenderer r in body.bodyRenderers)
                        if (r && r.material && r.material.HasProperty("_BodyColor")) mats.Add(r.material);

                    if (body.bloodSplatter && body.bloodSplatter.material && body.bloodSplatter.material.HasProperty("_BodyColor")) mats.Add(body.bloodSplatter.material);

                    st.Mats = mats.ToArray();
                    st.From = new Color[st.Mats.Length * 3];

                    for (int k = 0; k < st.Mats.Length; k++)
                    {
                        st.From[k * 3] = st.Mats[k].GetColor("_BodyColor");
                        st.From[k * 3 + 1] = st.Mats[k].HasProperty("_BackColor") ? st.Mats[k].GetColor("_BackColor") : st.From[k * 3];
                        st.From[k * 3 + 2] = st.Mats[k].HasProperty("_VisorColor") ? st.Mats[k].GetColor("_VisorColor") : st.From[k * 3];
                    }
                }

                float t = FxMath.Clamp01((Time.time - st.Start) / StoneTintTime);

                for (int k = 0; k < st.Mats.Length; k++)
                {
                    Material m = st.Mats[k];
                    if (!m) continue;

                    m.SetColor("_BodyColor", LerpRgb(st.From[k * 3], StonePale, t));
                    if (m.HasProperty("_BackColor")) m.SetColor("_BackColor", LerpRgb(st.From[k * 3 + 1], Stone, t));
                    if (m.HasProperty("_VisorColor")) m.SetColor("_VisorColor", LerpRgb(st.From[k * 3 + 2], StoneDark, t));
                }

                if (t >= 1f) StoneTints.RemoveAt(i);
                else StoneTints[i] = st;
            }
            catch (System.Exception e)
            {
                StoneTints.RemoveAt(i);
                Utils.ThrowException(e);
            }
        }
    }

    private static Color LerpRgb(Color a, Color b, float t) => FxMath.Rgba(a.r + (b.r - a.r) * t, a.g + (b.g - a.g) * t, a.b + (b.b - a.b) * t, a.a);

    private static readonly Color StringLight = new(0.96f, 0.85f, 1f);
    private static readonly Color StringMain = new(0.72f, 0.38f, 1f);
    private static readonly Color StringDeep = new(0.26f, 0.08f, 0.42f);
    private static readonly Color CurseLight = new(1f, 0.78f, 0.72f);
    private static readonly Color CurseMain = new(0.92f, 0.12f, 0.2f);
    private static readonly Color CurseDeep = new(0.32f, 0.02f, 0.07f);

    // 操られた人が殺しに行った時と、殺された本人の画面のキル演出を突き合わせるための記録 (送信はしない)
    private static readonly List<(float Time, byte Victim, bool Curse)> RecentStrings = [];

    // victim が直前の操り糸 / 呪いで殺されたなら true (curse = 呪いの方)
    internal static bool WasStringsKill(byte victim, out bool curse)
    {
        curse = false;

        for (int i = RecentStrings.Count - 1; i >= 0; i--)
        {
            (float time, byte v, bool c) = RecentStrings[i];
            if (Time.time - time > 3f) break;
            if (v != victim) continue;

            curse = c;
            return true;
        }

        return false;
    }

    // 操り糸: 頭上に十字の操り木が現れ、糸が頭と両手へ張る (細かく震える) → 殺す瞬間に糸が白く引き絞られ、相手の体に斬撃が走る
    // → 糸が切れて上下に散る。呪いは赤い糸と、足元の赤い魔法陣から立ちのぼる火の粉
    private static void SpawnStrings(Vector2 c, int victimId, bool curse)
    {
        if (victimId is >= 0 and <= 255)
        {
            RecentStrings.Add((Time.time, (byte)victimId, curse));
            if (RecentStrings.Count > 8) RecentStrings.RemoveAt(0);
        }

        Color light = curse ? CurseLight : StringLight;
        Color main = curse ? CurseMain : StringMain;
        Color deep = curse ? CurseDeep : StringDeep;
        const float yank = 0.4f;
        const float cut = 1.05f;

        Vector2 bar = c + new Vector2(0f, 2.6f);

        // 操り木
        Add(Shape.Glow, bar, Vector2.zero, 1.35f, 1.8f, 2f, main, deep, 0.5f, 0.1f, 0.75f, sy0: 0.7f, sy1: 0.8f, order: 0);
        Add(Shape.Solid, bar, Vector2.zero, 1.35f, 1.3f, 1.3f, light, main, 0.95f, 0.08f, 0.75f, rot: 0f, sy0: 0.08f, sy1: 0.08f);
        Add(Shape.Solid, bar + new Vector2(0f, 0.05f), Vector2.zero, 1.35f, 0.08f, 0.08f, light, main, 0.95f, 0.08f, 0.75f, rot: 0f, sy0: 0.7f, sy1: 0.7f);

        Vector2[] tops = [bar + new Vector2(-0.62f, 0f), bar + new Vector2(-0.2f, 0.3f), bar + new Vector2(0f, -0.3f), bar + new Vector2(0.2f, 0.3f), bar + new Vector2(0.62f, 0f)];
        Vector2[] ends = [c + new Vector2(-0.44f, 0.02f), c + new Vector2(-0.16f, 0.4f), c + new Vector2(0f, 0.5f), c + new Vector2(0.16f, 0.4f), c + new Vector2(0.44f, 0.02f)];

        for (int i = 0; i < tops.Length; i++)
        {
            Vector2 a = tops[i], b = ends[i];
            float dx = b.x - a.x, dy = b.y - a.y;
            float len = FxMath.Sqrt(dx * dx + dy * dy);
            float rot = FxMath.Atan2(dy, dx) * FxMath.Rad2Deg;
            Vector2 mid = FxMath.V2((a.x + b.x) * 0.5f, (a.y + b.y) * 0.5f);

            Add(Shape.Solid, mid, Vector2.zero, cut, len, len, main, deep, 0.3f, 0.1f, 0.85f, rot: rot, sy0: 0.14f, sy1: 0.14f, order: 0);
            Add(Shape.Solid, mid, Vector2.zero, cut, len, len, light, main, 0.95f, 0.1f, 0.9f, rot: rot, sy0: 0.035f, sy1: 0.035f, twinkle: 0.2f, twinkleSpeed: 45f);
            Add(Shape.Glow, b, Vector2.zero, cut, 0.3f, 0.25f, light, main, 0.9f, 0.1f, 0.85f);

            // 引き絞る瞬間に白く光る
            Add(Shape.Solid, mid, Vector2.zero, 0.22f, len, len, WindWhite, light, 1f, 0.01f, 0.3f, delay: yank, rot: rot, sy0: 0.08f, sy1: 0.05f);

            // 糸が切れる: 上半分は操り木へ跳ね上がり、下半分は垂れ落ちる
            float half = len * 0.5f;
            Vector2 up = FxMath.V2((a.x + mid.x) * 0.5f, (a.y + mid.y) * 0.5f);
            Vector2 down = FxMath.V2((b.x + mid.x) * 0.5f, (b.y + mid.y) * 0.5f);
            Add(Shape.Solid, up, new Vector2(Rnd(-0.3f, 0.3f), 2.2f), 0.45f, half, half * 0.6f, light, main, 0.9f, 0.01f, 0.4f, drag: 2f, delay: cut, rot: rot,
                spin: Rnd(-120f, 120f), sy0: 0.035f, sy1: 0.03f);
            Add(Shape.Solid, down, new Vector2(Rnd(-0.3f, 0.3f), 0.3f), 0.55f, half, half * 0.7f, light, main, 0.9f, 0.01f, 0.4f, delay: cut, rot: rot,
                spin: Rnd(-200f, 200f), rise: -3.5f, sy0: 0.035f, sy1: 0.03f);
        }

        // 糸を伝って降りる光の粒
        for (int i = 0; i < 14; i++)
        {
            int k = i % tops.Length;
            float life = Rnd(0.35f, 0.6f);
            Add(Shape.Star, tops[k], (ends[k] - tops[k]) / life, life, 0.2f, 0.12f, WindWhite, light, 1f, 0.1f, 0.8f, delay: Rnd(0f, cut - life));
        }

        Add(Shape.Sigil, c + Feet, Vector2.zero, cut + 0.3f, 0.5f, 1.7f, main, deep, curse ? 0.8f : 0.5f, 0.1f, 0.7f, spin: curse ? -70f : 50f, rot: 0f, sy0: 0.2f, sy1: 0.62f,
            order: 0);

        if (curse)
        {
            for (int i = 0; i < 10; i++)
            {
                Add(Shape.Star, c + Feet + new Vector2(Rnd(-0.8f, 0.8f), Rnd(-0.25f, 0.25f)), Vector2.zero, Rnd(0.6f, 0.9f), 0.12f, 0.3f, light, main, 1f, 0.1f, 0.6f,
                    delay: Rnd(0f, 0.5f), rise: Rnd(0.8f, 1.4f), twinkle: 0.5f, twinkleSpeed: 18f);
            }
        }

        // 殺される人の体に走る斬撃 (相手の位置は受け取った側で引く)
        PlayerControl victim = victimId is >= 0 and <= 255 ? Utils.GetPlayerById((byte)victimId) : null;
        if (!victim) return;

        Vector2 vp = victim.Pos();
        Add(Shape.Glow, vp, Vector2.zero, 0.5f, 0.6f, 2f, light, deep, 0.9f, 0.01f, 0.3f, delay: yank);
        Add(Shape.Solid, vp, Vector2.zero, 0.35f, 0.2f, 1.6f, WindWhite, main, 1f, 0.01f, 0.35f, delay: yank, rot: 35f, sy0: 0.1f, sy1: 0.05f);
        Add(Shape.Solid, vp, Vector2.zero, 0.35f, 0.2f, 1.6f, WindWhite, main, 1f, 0.01f, 0.35f, delay: yank + 0.06f, rot: -35f, sy0: 0.1f, sy1: 0.05f);

        for (int i = 0; i < 12; i++)
        {
            Add(Shape.Star, vp, Dir() * Rnd(2.5f, 5f), Rnd(0.3f, 0.45f), 0.3f, 0.1f, light, main, 1f, 0.01f, 0.5f, drag: 3f, delay: yank, stretch: 0.1f);
        }
    }

    // 竜巻: 残っている間 0.1 秒ごとに少しずつ粒を足す
    private struct TornadoEmitter
    {
        public Vector2 Pos;
        public float Until;
        public float Next;
        public int Beat;
    }

    private static readonly List<TornadoEmitter> TornadoEmitters = [];

    // 竜巻の筋は明るい床でも暗い床でも読めるよう、濃い灰色を主にして白い筋を少し混ぜる
    private static readonly Color TornFront = new(0.52f, 0.49f, 0.46f);
    private static readonly Color TornMid = new(0.32f, 0.3f, 0.29f);
    private static readonly Color TornBack = new(0.2f, 0.19f, 0.19f);
    private const float TornadoHeight = 4.4f;

    // 竜巻が現れる: 砂煙の輪が弾けて、その場で渦を巻き始める (同じ場所に二重には立てない)
    private static void StartTornado(Vector2 c, float seconds)
    {
        for (int i = 0; i < TornadoEmitters.Count; i++)
        {
            TornadoEmitter e = TornadoEmitters[i];
            if (FxMath.Abs(e.Pos.x - c.x) > 0.3f || FxMath.Abs(e.Pos.y - c.y) > 0.3f) continue;

            e.Until = Time.time + seconds;
            TornadoEmitters[i] = e;
            return;
        }

        if (TornadoEmitters.Count >= 8) TornadoEmitters.RemoveAt(0);
        TornadoEmitters.Add(new TornadoEmitter { Pos = c, Until = Time.time + seconds });

        Vector2 f = c + Feet;
        Impact(c, 2f, Dust, 0.16f, 0.12f, 0.35f);
        Add(Shape.Ring, f, Vector2.zero, 0.8f, 0.4f, 4.4f, Dust, DustDark, 1f, 0.02f, 0.4f, rot: 0f, sy0: 0.15f, sy1: 1.6f);
        Add(Shape.Ring, f, Vector2.zero, 0.9f, 0.3f, 3f, SmokeWhite, SmokeGrey, 0.85f, 0.02f, 0.4f, delay: 0.08f, rot: 0f, sy0: 0.12f, sy1: 1.1f);

        for (int i = 0; i < 16; i++)
        {
            float ang = Rnd(0f, 2f * FxMath.PI);
            Vector2 dir = FxMath.V2(FxMath.Cos(ang), FxMath.Sin(ang) * 0.4f);
            Add(Shape.Cloud, f + dir * 0.3f, dir * Rnd(3f, 5.5f), Rnd(0.9f, 1.4f), 0.5f, Rnd(1.2f, 1.8f), Dust, DustDark, 0.6f, 0.05f, 0.35f,
                drag: 2.4f, spin: Rnd(-90f, 90f), rise: 0.4f);
        }
    }

    private static void PulseTornados()
    {
        float now = Time.time;

        for (int i = TornadoEmitters.Count - 1; i >= 0; i--)
        {
            TornadoEmitter e = TornadoEmitters[i];

            if (now >= e.Until)
            {
                TornadoEmitters.RemoveAt(i);
                SpawnTornadoEnd(e.Pos);
                continue;
            }

            if (now < e.Next) continue;

            e.Next = now + 0.1f;
            e.Beat++;
            TornadoEmitters[i] = e;

            if (Active.Count < 2000) SpawnTornadoPulse(e.Pos, e.Beat, FxMath.Clamp01((e.Until - now) / 0.8f));
        }
    }

    // 漏斗の半幅 (h = 足元からの高さ)。根元は細く、上へ行くほど大きく開く
    private static float TornadoHalfWidth(float h)
    {
        float k = FxMath.Clamp01(h / TornadoHeight);
        return 0.3f + 1.9f * FxMath.Pow(k, 1.6f);
    }

    // 渦の 1 拍: 足元から楕円の輪 (煙の胴と縁) が次々に湧いて、広がりながら昇る = 輪を積んだ漏斗。
    // その上を筋が横切り (手前は明るく右へ、奥は暗く左へ流すと回って見える)、破片が振り回されて飛び、足元で砂煙が渦を巻く
    private static void SpawnTornadoPulse(Vector2 c, int beat, float fade)
    {
        Vector2 b = c + Feet;
        float sway = FxMath.Sin(Time.time * 1.3f) * 0.35f;
        float swayVel = FxMath.Cos(Time.time * 1.3f) * 0.35f;
        const float climb = 1.1f;
        float w0 = TornadoHalfWidth(0f) * 2.2f, w1 = TornadoHalfWidth(TornadoHeight) * 2.2f;

        for (int i = 0; i < 2; i++)
        {
            float d = i * 0.05f;
            var v = FxMath.V2(swayVel * 0.6f, 0f);
            // 胴: 濃い煙を 2 枚ずらして重ね、向こうが透けない塊にする
            Add(Shape.Cloud, FxMath.V2(b.x + Rnd(-0.08f, 0.08f), b.y), v, climb, w0, w1, TornMid, TornBack, 0.85f * fade, 0.12f, 0.75f, delay: d, rot: 0f,
                rise: TornadoHeight / climb, sy0: w0 * 0.34f, sy1: w1 * 0.34f, order: 0);
            Add(Shape.Cloud, FxMath.V2(b.x + Rnd(-0.1f, 0.1f), b.y + 0.05f), v, climb, w0 * 0.8f, w1 * 0.85f, TornBack, TornBack, 0.7f * fade, 0.12f, 0.75f, delay: d + 0.02f,
                rot: 180f, rise: TornadoHeight / climb, sy0: w0 * 0.3f, sy1: w1 * 0.3f, order: 0);
            bool bright = (beat + i) % 5 == 0;
            Add(Shape.Ring, FxMath.V2(b.x, b.y), v, climb, w0, w1, bright ? SmokeGrey : TornFront, TornBack, (bright ? 0.7f : 0.95f) * fade, 0.12f, 0.75f, delay: d, rot: 0f,
                rise: TornadoHeight / climb, sy0: w0 * 0.3f, sy1: w1 * 0.3f);
        }

        for (int i = 0; i < 10; i++)
        {
            float h = Rnd(0f, TornadoHeight);
            float hw = TornadoHalfWidth(h);
            float cx = b.x + sway * h / TornadoHeight;
            float life = Rnd(0.22f, 0.34f);
            int kind = i % 3;
            bool front = kind != 1;
            float x0 = front ? cx - hw : cx + hw;
            float sz = Rnd(0.5f, 0.8f) * (0.7f + h / TornadoHeight * 0.6f);

            if (kind == 0)
                Add(Shape.Star, FxMath.V2(x0, b.y + h), FxMath.V2(2f * hw / life, 0f), life, sz, sz * 0.8f, TornFront, TornMid, fade, 0.2f, 0.7f, rise: 0.8f, stretch: 0.1f);
            else if (kind == 2)
                Add(Shape.Star, FxMath.V2(x0, b.y + h - 0.05f), FxMath.V2(2f * hw / life, 0f), life, sz * 0.6f, sz * 0.5f, WindWhite, SmokeGrey, 0.8f * fade, 0.2f, 0.7f, rise: 0.8f,
                    stretch: 0.12f);
            else
                Add(Shape.Star, FxMath.V2(x0, b.y + h + 0.08f), FxMath.V2(-2f * hw / life, 0f), life, sz, sz * 0.8f, TornMid, TornBack, 0.85f * fade, 0.2f, 0.7f, rise: 0.8f,
                    stretch: 0.1f, order: 0);
        }

        // 足元で渦を巻いて噴き出す砂煙
        for (int i = 0; i < 3; i++)
        {
            float dir = (beat + i) % 2 == 0 ? -1f : 1f;
            Add(Shape.Cloud, b + FxMath.V2(dir * 0.4f, Rnd(-0.12f, 0.05f)), FxMath.V2(dir * Rnd(3f, 5.5f), Rnd(0f, 0.5f)), Rnd(0.7f, 1f), 0.6f, Rnd(1.6f, 2.2f), Dust, DustDark,
                0.9f * fade, 0.1f, 0.5f, drag: 1.6f, spin: Rnd(-90f, 90f), rise: 0.4f, sy0: 0.35f, sy1: 0.8f);
        }

        // 振り回されて飛ぶ破片 (漏斗の縁から横へ放り出され、落ちる)
        for (int i = 0; i < 2; i++)
        {
            float dir = Rnd(0f, 1f) < 0.5f ? -1f : 1f;
            float sz = Rnd(0.12f, 0.24f);
            float h = Rnd(TornadoHeight * 0.3f, TornadoHeight);
            Add(Shape.Chunk, FxMath.V2(b.x + dir * TornadoHalfWidth(h), b.y + h), FxMath.V2(dir * Rnd(2f, 4f), Rnd(0.5f, 2f)), Rnd(0.8f, 1.1f), sz, sz, Stone, StoneDark, fade,
                0.05f, 0.7f, drag: 0.4f, spin: Rnd(-900f, 900f), rise: -3.5f);
        }

        // 足元の暗い渦と、近くにいる時の地響き
        if (beat % 3 == 0)
            Add(Shape.Cloud, b, Vector2.zero, 0.6f, 1.6f, 2f, TornBack, DustDark, 0.55f * fade, 0.3f, 0.6f, rot: 0f, sy0: 0.5f, sy1: 0.62f, order: 0);

        if (beat % 12 == 0) Impact(c, 3f, Dust, 0f, 0.05f, 0.5f);
    }

    // 竜巻が消える: 渦がほどけて砂煙が四方へ散る
    private static void SpawnTornadoEnd(Vector2 c)
    {
        if (GameStates.IsMeeting) return;

        Vector2 b = c + Feet;

        for (int i = 0; i < 12; i++)
        {
            float h = Rnd(0f, TornadoHeight);
            float ang = Rnd(0f, 2f * FxMath.PI);
            Vector2 dir = FxMath.V2(FxMath.Cos(ang), FxMath.Sin(ang) * 0.5f);
            Add(Shape.Cloud, FxMath.V2(b.x, b.y + h), dir * Rnd(1.5f, 3f), Rnd(0.8f, 1.2f), 0.6f, Rnd(1.2f, 1.7f), TornMid, TornBack, 0.55f, 0.05f, 0.4f,
                drag: 1.8f, spin: Rnd(-90f, 90f), rise: 0.3f);
        }

        Add(Shape.Ring, b, Vector2.zero, 0.8f, 0.5f, 3.4f, Dust, DustDark, 0.8f, 0.02f, 0.4f, rot: 0f, sy0: 0.18f, sy1: 1.2f);
    }

    // 竜巻に巻き上げられる: 足元の砂煙 → 体の周りを筋が回りながら昇り、塵と小石が舞い上がる → 頭上へ抜けて消える
    private static void SpawnTornadoLift(Vector2 c)
    {
        Vector2 f = c + Feet;

        Impact(c, 1.4f, SmokeWhite, 0.15f, 0.08f, 0.25f);
        Add(Shape.Ring, f, Vector2.zero, 0.6f, 0.3f, 2.8f, Dust, DustDark, 0.9f, 0.02f, 0.4f, rot: 0f, sy0: 0.1f, sy1: 1f);

        for (int i = 0; i < 40; i++)
        {
            float h = Rnd(-0.5f, 0.6f);
            float hw = 0.45f + Rnd(0f, 0.25f);
            float life = Rnd(0.22f, 0.34f);
            bool front = i % 2 == 0;
            float x0 = front ? c.x - hw : c.x + hw;
            float s = Rnd(0.4f, 0.6f);
            Add(Shape.Star, FxMath.V2(x0, c.y + h), FxMath.V2((front ? 2f : -2f) * hw / life, 0f), life, s, s * 0.7f, front ? TornFront : TornMid, front ? TornMid : TornBack,
                front ? 1f : 0.75f, 0.2f, 0.7f, delay: Rnd(0f, 0.45f), rise: Rnd(2.5f, 4.5f), stretch: 0.1f, order: front ? -1 : 0);
        }

        for (int i = 0; i < 8; i++)
        {
            Add(Shape.Cloud, c + new Vector2(Rnd(-0.3f, 0.3f), Rnd(-0.4f, 0.2f)), new Vector2(Rnd(-0.4f, 0.4f), 0f), Rnd(0.7f, 1f), 0.5f, Rnd(1f, 1.4f), Dust, DustDark,
                0.65f, 0.1f, 0.5f, delay: Rnd(0f, 0.3f), spin: Rnd(-200f, 200f), rise: Rnd(2f, 3f));
        }

        for (int i = 0; i < 10; i++)
        {
            float s = Rnd(0.1f, 0.16f);
            Add(Shape.Chunk, f + new Vector2(Rnd(-0.5f, 0.5f), 0f), new Vector2(Rnd(-0.8f, 0.8f), Rnd(3f, 5f)), Rnd(0.5f, 0.7f), s, s, Stone, StoneDark, 1f, 0.02f, 0.7f,
                drag: 0.8f, delay: Rnd(0f, 0.3f), spin: Rnd(-720f, 720f));
        }

        Add(Shape.Glow, c + new Vector2(0f, 1.3f), Vector2.zero, 0.35f, 0.3f, 1.4f, WindWhite, SmokeGrey, 0.8f, 0.02f, 0.3f, delay: 0.4f);

        // 体を包んで昇っていく濃い渦 (白い床でも筋が見えるよう影を重ねる)
        Add(Shape.Glow, c, Vector2.zero, 0.7f, 1.2f, 1.6f, TornMid, TornBack, 0.45f, 0.05f, 0.5f, sy0: 1.8f, sy1: 2.6f, rise: 1.2f, order: 0);
    }

    private static byte _tag;
    private static float _anchorX, _anchorY;

    private static void Tag(byte tag, Vector2 anchor)
    {
        _tag = tag;
        _anchorX = anchor.x;
        _anchorY = anchor.y;
    }

    // 砲身: 発射方向へ段々小さくなる魔法陣を 3 枚、横から見た向き (縦長の楕円) で並べる
    private static void CannonBarrel(Vector2 c, float s, float size, CannonColors k, float life, float delay, float alpha)
    {
        for (int j = 0; j < 3; j++)
        {
            float h = size * (j switch { 0 => 0.82f, 1 => 0.64f, _ => 0.48f });
            float w = h * 0.28f;
            Vector2 p = FxMath.V2(c.x + s * size * (0.62f + j * 0.4f), c.y);
            float d = delay + j * 0.1f;
            Add(Shape.Sigil, p, Vector2.zero, life - j * 0.1f, w * 0.3f, w, k.Light, k.Rune, alpha, FxMath.Min(0.5f, 0.15f / life), 1f - FxMath.Min(0.5f, 0.3f / life),
                delay: d, rot: 0f, sy0: h * 0.3f, sy1: h, twinkle: 0.25f, twinkleSpeed: 12f, order: 6);
            Add(Shape.Ring, p, Vector2.zero, 0.35f, w * 0.5f, w * 1.6f, CannonWhite, k.Main, 0.8f, 0.02f, 0.3f, delay: d, rot: 0f, sy0: h * 0.5f, sy1: h * 1.6f, order: 7);
        }
    }

    // 魔法陣が描かれ、周りから光を吸い込んで力を溜める。Radius の秒数が過ぎる頃に砲身の魔法陣が並ぶ
    private static void SpawnCannonCharge(Vector2 c, float seconds, float thickness, float palette, float s)
    {
        CannonColors k = CannonPal(palette);
        float th = CannonThickness(thickness);
        float size = SigilSize(th);
        float charge = FxMath.Max(seconds, 0.3f);
        // 役職側のフェーズは秒単位で切り上がるので実際の発射は予定より早い。発射の演出が来た時点でこちらを消すので長めに残す
        float hold = charge + 0.8f;
        const float draw = 0.6f;
        float body = hold - draw;

        Tag(TagCircle, c);

        // 背後の淡い光
        Add(Shape.Glow, c, Vector2.zero, hold, size * 1.2f, size * 1.7f, k.Main, k.Main, 0.35f, 0.3f / hold, 0.9f, twinkle: 0.25f, twinkleSpeed: 3f, order: 0);

        if (k.Void)
        {
            // ブラックホール: 中心に回る暗黒
            Add(Shape.Cloud, c, Vector2.zero, hold, size * 0.1f, size * 0.78f, VoidBlack, VoidDeep, 1f, 0.2f / hold, 0.9f, spin: 70f, order: 2);
        }

        // 外周の魔法陣: 速く回りながら描かれてから、ゆっくり回り続ける
        float spinOuter = -28f * s;
        float rot0 = Rnd(0f, 360f);
        Add(Shape.Sigil, c, Vector2.zero, draw, size * 0.35f, size, k.Light, k.Rune, 1f, 0.35f, 1.1f, rot: rot0, spin: spinOuter * 7f, order: 3);
        Add(Shape.Sigil, c, Vector2.zero, body, size, size, k.Rune, k.Light, 1f, 0.001f, 1f - 0.4f / body,
            delay: draw, rot: rot0 + spinOuter * 7f * draw, spin: spinOuter, twinkle: 0.12f, twinkleSpeed: 5f, order: 3);

        // 内側の魔法陣は逆回り
        float spinInner = 45f * s;
        float rot1 = Rnd(0f, 360f);
        Add(Shape.SigilInner, c, Vector2.zero, draw, size * 0.15f, size * 0.62f, k.Light, k.Rune, 0.95f, 0.4f, 1.1f, delay: 0.1f, rot: rot1, spin: spinInner * 6f, order: 4);
        Add(Shape.SigilInner, c, Vector2.zero, body - 0.1f, size * 0.62f, size * 0.62f, k.Rune, k.Light, 0.95f, 0.001f, 1f - 0.4f / body,
            delay: draw + 0.1f, rot: rot1 + spinInner * 6f * draw, spin: spinInner, order: 4);

        // 描き始めの光の輪
        Add(Shape.Ring, c, Vector2.zero, 0.55f, size * 0.25f, size * 1.1f, CannonWhite, k.Main, 0.9f, 0.03f, 0.35f, order: 5);

        // 中心に溜まっていく光
        // ブラックホールは中心の暗黒を塗りつぶさないよう、光を小さく色付きにする
        Color core = k.Void ? k.Light : CannonWhite;
        float coreSize = k.Void ? 0.3f : 0.5f;
        Add(Shape.Glow, c, Vector2.zero, hold, 0.15f, size * coreSize, core, k.Light, 1f, 0.1f, 1f - 0.4f / hold, twinkle: 0.35f, twinkleSpeed: 18f, order: 8);
        Add(Shape.Star, c, Vector2.zero, hold, 0.2f, size * (coreSize + 0.05f), core, k.Light, 0.9f, 0.2f, 1f - 0.4f / hold, spin: 90f, order: 9);

        // 周りから吸い込まれる光の粒
        int motes = FxMath.Min(110, (int)(charge * 16f));
        for (int i = 0; i < motes; i++)
        {
            Vector2 d = Dir();
            float dist = size * Rnd(0.8f, 1.4f);
            float speed = Rnd(2.2f, 3.4f);
            Add(Shape.Star, FxMath.V2(c.x + d.x * dist, c.y + d.y * dist), FxMath.V2(-d.x * speed, -d.y * speed), dist / speed, Rnd(0.08f, 0.15f), 0.03f,
                CannonAccent(k, i), k.Main, 1f, 0.25f, 0.75f, delay: Rnd(0f, FxMath.Max(0f, charge - 0.3f)), stretch: 0.06f, order: 7);
        }

        // 溜めの後半で砲身が並び、光が一度脈打つ
        float barrelAt = charge * 0.55f;
        CannonBarrel(c, s, size, k, hold - barrelAt, barrelAt, 0.9f);
        Add(Shape.Ring, c, Vector2.zero, 0.6f, size * 0.4f, size * 1.6f, k.Light, k.Main, 0.8f, 0.03f, 0.3f, delay: barrelAt, order: 5);
    }

    // ビームを構成する光の層。ゲートから先端へ一瞬で伸びてから、撃ち終わるまで残る
    private static void BeamLayer(Vector2 o, Vector2 vel, float length, float width, float rot, float grow, float hold, Color c0, Color c1, float alpha, float twinkle, int order)
    {
        Add(Shape.Beam, o, vel, grow, 0.01f, length, c0, c0, alpha, 0.01f, 1.1f, rot: rot, sy0: width * 0.6f, sy1: width, order: order);
        Vector2 later = FxMath.V2(o.x + vel.x * grow, o.y + vel.y * grow);
        Add(Shape.Beam, later, vel, hold - grow, length, length, c0, c1, alpha, 0.001f, 1f - 0.3f / (hold - grow),
            delay: grow, rot: rot, sy0: width, sy1: width, twinkle: twinkle, twinkleSpeed: 25f, order: order);
    }

    // 芯の白から外側の霞まで 4 層
    private static void BeamBody(Vector2 o, Vector2 vel, float length, float width, float rot, float grow, float hold, CannonColors k)
    {
        BeamLayer(o, vel, length, width * 2.4f, rot, grow, hold, k.Deep, k.Main, 0.45f, 0.1f, 1);
        if (k.Rainbow)
        {
            // 虹は七色の帯を縦に並べる
            for (int i = 0; i < RainbowCols.Length; i++)
            {
                var band = FxMath.V2(o.x, o.y + (i - 3) * width * 0.14f);
                BeamLayer(band, vel, length, width * 0.22f, rot, grow, hold, RainbowCols[i], RainbowCols[i], 0.85f, 0.1f, 2);
            }
        }
        else BeamLayer(o, vel, length, width, rot, grow, hold, k.Main, k.Main, 0.85f, 0.12f, 2);

        // 外側で脈打つ光の圧
        BeamLayer(o, vel, length, width * 3.4f, rot, grow, hold, k.Main, k.Deep, 0.15f, 0.55f, 0);

        if (k.Void)
        {
            // ブラックホールは芯が光を呑む: 明るい縁の内側に闇の芯
            BeamLayer(o, vel, length, width * 0.62f, rot, grow, hold, VoidMagenta, k.Light, 0.95f, 0.25f, 3);
            BeamLayer(o, vel, length, width * 0.34f, rot, grow, hold, VoidBlack, VoidBlack, 1f, 0f, 4);
            return;
        }

        BeamLayer(o, vel, length, width * 0.55f, rot, grow, hold, k.Light, k.Light, 0.95f, 0f, 3);
        BeamLayer(o, vel, length, width * 0.22f, rot, grow, hold, CannonWhite, CannonWhite, 1f, 0.2f, 4);
    }

    // ビームに巻き付いて先へ流れる二重螺旋。止まった波形を撃ち出すので、並ぶと螺旋が流れて見える
    private static void BeamHelix(Vector2 o, float s, float length, float width, float grow, float firing, CannonColors k)
    {
        const float speed = 12f;
        const float step = 0.045f;
        float amp = width * 1.2f;
        float wave = width * 3.2f;
        float phaseStep = 2f * FxMath.PI * step * speed / wave;
        float life = length / speed;
        // ツインや複数の砲が重なって粒が多い時は間引く (上限に届くと後から出す演出が欠ける)
        float q = Active.Count > 1200 ? 0.5f : 1f;
        int n = (int)(FxMath.Min(85, (int)((firing - grow) / step)) * q);

        for (int i = 0; i < n; i++)
        {
            float delay = grow + i * step;

            for (int strand = 0; strand < 2; strand++)
            {
                float ph = i * phaseStep + strand * FxMath.PI;
                float y = FxMath.Sin(ph) * amp;
                // 手前 (cos > 0) を大きく明るく、奥を小さく濃い色に沈めて奥行きを出す
                float front = FxMath.Cos(ph);
                float size = 0.4f + 0.25f * front;
                Color col = front < -0.2f ? k.Deep : k.Void ? (strand == 0 ? VoidMagenta : k.Light) : strand == 0 ? CannonWhite : CannonAccent(k, i);
                Add(Shape.Star, FxMath.V2(o.x, o.y + y), FxMath.V2(s * speed, 0f), life, size, size, col, k.Main, 0.7f + 0.3f * front, 0.02f, 0.85f,
                    delay: delay, stretch: 0.05f, order: front > 0f ? 9 : 1);
            }
        }
    }

    // 1 本の線分を細い四角で描く (稲妻の折れ線の 1 画)
    private static void Stroke(Vector2 a, Vector2 b, float thick, float life, float delay, Color col, Color glow)
    {
        float dx = b.x - a.x, dy = b.y - a.y;
        float len = FxMath.Sqrt(dx * dx + dy * dy);
        float ang = FxMath.Atan2(dy, dx) * 180f / FxMath.PI;
        var mid = FxMath.V2((a.x + b.x) * 0.5f, (a.y + b.y) * 0.5f);
        Add(Shape.Solid, mid, Vector2.zero, life, len, len, col, col, 1f, 0.005f, 0.45f, delay: delay, rot: ang, sy0: thick, sy1: thick, twinkle: 0.5f, twinkleSpeed: 45f, order: 9);
        Add(Shape.Glow, mid, Vector2.zero, life, len * 1.2f, len * 1.2f, glow, glow, 0.55f, 0.005f, 0.45f, delay: delay, rot: ang, sy0: thick * 5f, sy1: thick * 5f, order: 8);
    }

    // ビームの縁から外へ走る稲妻 (折れ曲がる 3 画 + 枝)
    private static void BeamArcs(Vector2 o, float s, float vy, float length, float width, float grow, float firing, CannonColors k)
    {
        // 同時に見えるのは数本なので、画面に入りやすい根元寄りに多めに散らす
        int n = (int)(FxMath.Min(90, (int)(firing * 20f)) * (Active.Count > 1200 ? 0.5f : 1f));
        Color col = k.Void ? VoidMagenta : CannonWhite;

        for (int i = 0; i < n; i++)
        {
            float delay = Rnd(grow, firing);
            float life = Rnd(0.14f, 0.22f);
            float side = i % 2 == 0 ? 1f : -1f;
            float along = Rnd(0f, 1f);
            var p = FxMath.V2(o.x + s * (0.03f + 0.9f * along * along) * length, o.y + vy * delay + side * width * 0.3f);
            float reach = width * Rnd(0.9f, 1.7f);
            float seg = reach / 3f;

            for (int j = 0; j < 3; j++)
            {
                var q = FxMath.V2(p.x + Rnd(-0.8f, 0.8f) * seg, p.y + side * seg);
                Stroke(p, q, 0.055f - j * 0.012f, life, delay, col, k.Main);

                // 2 画目の角から枝分かれ
                if (j == 1) Stroke(q, FxMath.V2(q.x + Rnd(-1f, 1f) * seg, q.y + side * seg * 0.8f), 0.035f, life * 0.8f, delay, col, k.Main);

                p = q;
            }
        }
    }

    // ブラックホール砲: 周りの光の粒がビームへ吸い込まれていく
    private static void BeamInfall(Vector2 o, float s, float length, float width, float grow, float firing, CannonColors k)
    {
        int n = (int)(FxMath.Min(90, (int)(firing * 22f)) * (Active.Count > 1200 ? 0.5f : 1f));

        for (int i = 0; i < n; i++)
        {
            float delay = Rnd(grow, firing);
            float side = i % 2 == 0 ? 1f : -1f;
            float dist = width * Rnd(1.6f, 3.2f);
            float t = Rnd(0.35f, 0.6f);
            var p = FxMath.V2(o.x + s * Rnd(0.02f, 0.98f) * length, o.y + side * dist);
            // 軸へ向かいながら少し先へ流される
            var v = FxMath.V2(s * Rnd(1f, 3f), -side * dist / t);
            Color col = i % 3 == 0 ? CannonWhite : i % 3 == 1 ? VoidMagenta : k.Light;
            Add(Shape.Glow, p, v, t, 0.26f, 0.08f, col, VoidPurple, 1f, 0.1f, 0.7f, delay: delay, stretch: 0.06f, order: 7);
        }
    }

    // ビームの中を流れる光の筋と、先へ飛んでいく光の輪
    private static void BeamFlow(Vector2 o, float s, float vy, float length, float width, float grow, float firing, CannonColors k)
    {
        int streaks = FxMath.Min(130, (int)(firing * 14f));
        for (int i = 0; i < streaks; i++)
        {
            float delay = Rnd(grow, firing);
            float speed = Rnd(16f, 26f);
            float lateral = Rnd(-0.35f, 0.35f) * width;
            float len = Rnd(0.8f, 1.6f);
            float w = Rnd(0.04f, 0.09f);
            // 光の筋は根元が明るいので、進む向きの反対へ尾を引かせる
            Add(Shape.Ray, FxMath.V2(o.x, o.y + vy * delay + lateral), FxMath.V2(s * speed, vy), length / speed, len, len, CannonAccent(k, i), k.Main,
                Rnd(0.6f, 0.95f), 0.05f, 0.8f, delay: delay, rot: s > 0f ? 180f : 0f, sy0: w, sy1: w, order: 5);
        }

        int rings = FxMath.Min(36, (int)(firing * 3.5f));
        for (int i = 0; i < rings; i++)
        {
            float delay = grow + i * (firing / rings);
            float h = width * Rnd(1.3f, 1.6f);
            Add(Shape.Ring, FxMath.V2(o.x, o.y + vy * delay), FxMath.V2(s * 10f, vy), length / 10f, h * 0.22f, h * 0.3f, CannonWhite, CannonAccent(k, i + 1),
                0.8f, 0.05f, 0.6f, delay: delay, rot: 0f, sy0: h, sy1: h * 1.25f, order: 6);
        }
    }

    // 撃たれた本人のキル演出を、自分を撃ち抜いた光線の色と向きに合わせるため、最近の光線を覚えておく (送信なし)。
    // 同時に何本も撃たれていても、本人の位置を通る光線を選べるよう 1 本ずつ残す
    private struct CannonShot
    {
        public float Time, X, Y, S, Palette, Width, Duration, EndY;
        public bool Sweep;
    }

    private static readonly CannonShot[] RecentShots = new CannonShot[8];
    private static int _shotCursor;

    private static void RememberShot(CannonShot shot)
    {
        RecentShots[_shotCursor] = shot;
        _shotCursor = (_shotCursor + 1) % RecentShots.Length;
    }

    // 波動砲で死んだ本人の画面に出すキル演出 (死因が「蒸発」のとき)。victim = 本人の位置
    internal static void ShowCannonKill(Vector2 victim)
    {
        float now = Time.time;
        int best = -1, latest = -1;
        float bestGap = float.MaxValue, latestTime = -1f;

        for (int i = 0; i < RecentShots.Length; i++)
        {
            CannonShot shot = RecentShots[i];
            if (shot.Time <= 0f || now - shot.Time > shot.Duration + 3f) continue;

            if (shot.Time > latestTime)
            {
                latestTime = shot.Time;
                latest = i;
            }

            float gap;

            if (shot.Sweep)
            {
                // 縦に掃いた範囲に入っていれば当たり
                float lo = FxMath.Min(shot.Y, shot.EndY) - shot.Width, hi = FxMath.Max(shot.Y, shot.EndY) + shot.Width;
                if (victim.y < lo || victim.y > hi) continue;
                gap = 0.5f;
            }
            else
            {
                // 魔法陣より前にいて、光線の高さに近いほど有力
                if ((victim.x - shot.X) * shot.S < -1f) continue;
                gap = FxMath.Abs(victim.y - shot.Y);
                if (gap > shot.Width * 1.5f + 1f) continue;
            }

            if (gap < bestGap)
            {
                bestGap = gap;
                best = i;
            }
        }

        int pick = best >= 0 ? best : latest;
        float palette = pick >= 0 ? RecentShots[pick].Palette : 0f;
        // 全幅の掃射には左右がないので、画面の左から来たことにする
        bool fromLeft = pick < 0 || RecentShots[pick].Sweep || RecentShots[pick].S > 0f;
        Logger.Info($"cannon kill overlay: shot={pick} (matched={best >= 0}) palette={palette} fromLeft={fromLeft}", "CannonKillOverlay");
        CannonColors k = CannonPal(palette);
        CannonKillOverlay.Show(fromLeft, k.Light, k.Main, k.Deep);
    }

    // 魔法陣から放たれるビーム: 発射の閃光 → 4 層の光線 → 流れる光と光の輪 → 先端の飛沫。撃ち終わりは CannonEnd が畳む
    private static void SpawnCannonBeam(Vector2 c, float firing, float thickness, float palette, float s)
    {
        // 溜めの魔法陣は、ここで出す発射用の魔法陣と入れ替える
        FadeTagged(c, 0.15f, false);

        CannonColors k = CannonPal(palette);
        float th = CannonThickness(thickness);
        float size = SigilSize(th);
        float length = CannonLengthPerThickness * th;
        float width = CannonWidth(th);
        float dur = FxMath.Max(firing, 0.5f);
        float hold = dur + 0.8f;
        const float grow = 0.14f;
        float rot = s > 0f ? 0f : 180f;
        var o = FxMath.V2(c.x + s * CannonGateRadius, c.y);
        var tip = FxMath.V2(o.x + s * length, o.y);

        RememberShot(new CannonShot { Time = Time.time, X = c.x, Y = c.y, S = s, Palette = palette, Width = width, Duration = dur });

        Tag(TagBeam, c);

        CannonImpact(o, 3f, k.Light, 0.8f, 0.34f, 1f, 0.2f, dur);
        CannonMuzzleBurst(o, s, length, width, grow, k);

        // 発射中は魔法陣が速く回る
        if (k.Void) Add(Shape.Cloud, c, Vector2.zero, hold, size * 0.78f, size * 0.78f, VoidBlack, VoidDeep, 1f, 0.02f, 1f - 0.3f / hold, spin: 160f, order: 2);
        // 発射の瞬間だけ白く光り、あとは色のついたまま回る
        Add(Shape.Sigil, c, Vector2.zero, 0.35f, size * 1.3f, size * 1.05f, CannonWhite, k.Rune, 1f, 0.01f, 0.2f, spin: -200f * s, order: 5);
        Add(Shape.Sigil, c, Vector2.zero, hold, size * 1.3f, size * 1.05f, k.Rune, k.Rune, 1f, 0.02f, 1f - 0.3f / hold, spin: -200f * s, order: 3);
        Add(Shape.SigilInner, c, Vector2.zero, hold, size * 0.75f, size * 0.62f, k.Light, k.Rune, 1f, 0.02f, 1f - 0.3f / hold, spin: 260f * s, order: 4);
        CannonBarrel(c, s, size, k, hold, 0f, 0.95f);

        BeamBody(o, Vector2.zero, length, width, rot, grow, hold, k);

        // 発射の衝撃波 (ビームに垂直な楕円と、魔法陣から広がる円)
        Add(Shape.Ring, o, Vector2.zero, 0.45f, 0.1f, width * 0.9f, CannonWhite, k.Main, 1f, 0.01f, 0.3f, rot: 0f, sy0: 0.4f, sy1: width * 3f, order: 7);
        Add(Shape.Ring, c, Vector2.zero, 0.6f, size * 0.5f, size * 2.4f, k.Light, k.Main, 0.9f, 0.01f, 0.3f, order: 7);

        // 砲口の光
        Add(Shape.Glow, o, Vector2.zero, hold, width * 2.4f, width * 1.8f, CannonWhite, k.Light, 1f, 0.01f, 1f - 0.3f / hold, twinkle: 0.3f, twinkleSpeed: 20f, order: 9);
        Add(Shape.Star, o, Vector2.zero, hold, width * 2.8f, width * 2f, CannonWhite, k.Light, 1f, 0.01f, 1f - 0.3f / hold, spin: 40f, twinkle: 0.25f, twinkleSpeed: 16f, order: 10);

        BeamFlow(o, s, 0f, length, width, grow, dur, k);
        BeamHelix(o, s, length, width, grow, dur, k);
        BeamArcs(o, s, 0f, length, width, grow, dur, k);

        if (k.Void)
        {
            BeamInfall(o, s, length, width, grow, dur, k);

            // 魔法陣の周りを回る降着円盤 (傾いた楕円が 2 枚、逆向きに回る)
            Add(Shape.Ring, c, Vector2.zero, hold, size * 1.2f, size * 1.9f, VoidMagenta, VoidPurple, 0.85f, 0.05f, 1f - 0.3f / hold, spin: 140f, sy0: size * 0.4f, sy1: size * 0.62f, order: 6);
            Add(Shape.Ring, c, Vector2.zero, hold, size * 1.5f, size * 2.3f, k.Light, VoidDeep, 0.6f, 0.05f, 1f - 0.3f / hold, spin: -95f, sy0: size * 0.3f, sy1: size * 0.5f, order: 1);
        }

        // 先端の光と飛沫
        Add(Shape.Glow, tip, Vector2.zero, hold - grow, width * 1.8f, width * 1.8f, k.Light, k.Main, 0.9f, 0.05f, 1f - 0.3f / hold, delay: grow * 0.9f, twinkle: 0.4f, twinkleSpeed: 22f, order: 8);

        int sparks = FxMath.Min(70, (int)(dur * 8f));
        for (int i = 0; i < sparks; i++)
        {
            Vector2 d = FxMath.V2(s * Rnd(0.2f, 1f), Rnd(-1f, 1f));
            float speed = Rnd(2f, 4.5f);
            Add(Shape.Star, tip, FxMath.V2(d.x * speed, d.y * speed), Rnd(0.3f, 0.6f), Rnd(0.1f, 0.2f), 0.03f, CannonAccent(k, i), k.Main, 1f, 0.05f, 0.5f,
                drag: 2.5f, delay: Rnd(grow, dur), stretch: 0.08f, order: 9);
        }

        // 砲口から後ろへ散る火花
        int back = FxMath.Min(60, (int)(dur * 6f));
        for (int i = 0; i < back; i++)
        {
            Vector2 d = FxMath.V2(-s * Rnd(0.1f, 0.8f), Rnd(-1f, 1f));
            float speed = Rnd(1.5f, 3.5f);
            Add(Shape.Star, o, FxMath.V2(d.x * speed, d.y * speed), Rnd(0.3f, 0.55f), Rnd(0.08f, 0.16f), 0.03f, CannonAccent(k, i + 2), k.Main, 1f, 0.05f, 0.5f,
                drag: 2.5f, delay: Rnd(grow, dur), stretch: 0.08f, order: 9);
        }
    }

    // 全幅のビームが縦に掃いていく (超波動砲ダイナミック)。c = 始点の高さでのビームの中心
    private static void SpawnCannonSweep(Vector2 c, float firing, float thickness, float endY)
    {
        CannonColors k = CannonPal((float)CannonPalette.Crimson);
        float th = CannonThickness(thickness);
        float length = CannonSweepLengthPerThickness * th;
        float width = CannonWidth(th);
        float dur = FxMath.Max(firing, 0.5f);

        RememberShot(new CannonShot { Time = Time.time, X = c.x, Y = c.y, S = 1f, Palette = (float)CannonPalette.Crimson, Width = width, Duration = dur, EndY = endY, Sweep = true });
        float hold = dur + 0.8f;
        const float grow = 0.2f;
        float vy = (endY - c.y) / dur;
        var o = FxMath.V2(c.x - length * 0.5f, c.y);
        var vel = FxMath.V2(0f, vy);

        Tag(TagBeam, c);

        CannonImpact(c, 5f, k.Light, 0.8f, 0.34f, 1f, 0.2f, dur);

        BeamBody(o, vel, length, width, 0f, grow, hold, k);
        BeamFlow(o, 1f, vy, length, width, grow, dur, k);
        BeamArcs(o, 1f, vy, length, width, grow, dur, k);

        // 両端の光
        for (int e = 0; e < 2; e++)
        {
            var p = FxMath.V2(o.x + e * length, o.y + vy * grow);
            Add(Shape.Glow, p, vel, hold - grow, width * 2f, width * 2f, CannonWhite, k.Main, 0.9f, 0.05f, 1f - 0.3f / hold, delay: grow, twinkle: 0.4f, twinkleSpeed: 20f, order: 8);
        }
    }

    // 波動砲の演出を片付ける。撃ち終わりはビームが細くなって消え、中断は魔法陣が砕け散る
    private static void SpawnCannonEnd(Vector2 c, float palette, bool broken)
    {
        FadeTagged(c, broken ? 0.08f : 0.35f, true);
        if (!broken) return;

        CannonColors k = CannonPal(palette);
        Add(Shape.Ring, c, Vector2.zero, 0.4f, 0.8f, 2.6f, CannonWhite, k.Main, 0.9f, 0.01f, 0.3f, order: 7);
        Add(Shape.Glow, c, Vector2.zero, 0.3f, 1.6f, 0.4f, CannonWhite, k.Light, 0.9f, 0.01f, 0.2f, order: 8);

        // 魔法陣の欠片 (光る粒なので三角の破片には見えない)
        for (int i = 0; i < 36; i++)
        {
            Vector2 d = Dir();
            float at = Rnd(0.3f, 0.9f);
            float speed = Rnd(1.5f, 4f);
            Add(Shape.Star, FxMath.V2(c.x + d.x * at, c.y + d.y * at), FxMath.V2(d.x * speed, d.y * speed), Rnd(0.4f, 0.8f), Rnd(0.1f, 0.2f), 0.02f,
                CannonAccent(k, i), k.Deep, 1f, 0.02f, 0.4f, drag: 2.2f, stretch: 0.06f, twinkle: 0.4f, twinkleSpeed: 20f, order: 9);
        }
    }

    // c の近くにある波動砲の演出を seconds 秒で消す。collapse なら光線は細くなりながら消える (魔法陣は潰さずに薄れるだけ)
    private static void FadeTagged(Vector2 c, float seconds, bool collapse)
    {
        float reach2 = CannonAnchorReach * CannonAnchorReach;

        for (int i = Active.Count - 1; i >= 0; i--)
        {
            Particle p = Active[i];
            if (p.Tag == 0) continue;

            float dx = p.AnchorX - c.x, dy = p.AnchorY - c.y;
            if (dx * dx + dy * dy > reach2) continue;

            if (p.Delay > 0f || p.Age >= p.Life)
            {
                Release(p);
                Active.RemoveAt(i);
                continue;
            }

            // 今見えている大きさ・色・濃さのまま固定して、そこから消していく
            float t = p.Age / p.Life;
            float u = 1f - t;
            float ease = 1f - u * u * u;
            float sx = p.Sx0 + (p.Sx1 - p.Sx0) * ease;
            float sy = p.Sy0 + (p.Sy1 - p.Sy0) * ease;
            float a = p.Alpha * FxMath.Clamp01(t / p.FadeIn);
            if (t > p.FadeOutFrom) a *= 1f - (t - p.FadeOutFrom) / (1f - p.FadeOutFrom);

            Color from = p.Color0, to = p.Color1;
            float kk = t;
            if (p.HasMid)
            {
                if (t < 0.5f) { to = p.ColorMid; kk = t * 2f; }
                else { from = p.ColorMid; kk = (t - 0.5f) * 2f; }
            }

            Color now = FxMath.Rgba(from.r + (to.r - from.r) * kk, from.g + (to.g - from.g) * kk, from.b + (to.b - from.b) * kk, 1f);

            p.Sx0 = sx;
            p.Sx1 = sx;
            p.Sy0 = sy;
            p.Sy1 = collapse && p.IsBeam ? sy * 0.05f : sy;
            p.Color0 = now;
            p.Color1 = now;
            p.HasMid = false;
            p.Alpha = a;
            p.Age = 0f;
            p.Life = FxMath.Max(seconds, 0.02f);
            p.FadeIn = 0.001f;
            p.FadeOutFrom = 0f;
            p.Twinkle = 0f;
            Active[i] = p;
        }
    }

    private static void Add(Shape shape, Vector2 pos, Vector2 vel, float life, float sx0, float sx1, Color color0, Color color1,
                            float alpha, float fadeIn, float fadeOutFrom, float drag = 0f, float delay = 0f, float spin = 0f, float? rot = null,
                            float sy0 = -1f, float sy1 = -1f, float twinkle = 0f, float twinkleSpeed = 0f, float rise = 0f,
                            float stretch = 0f, bool followCamera = false, int order = -1, Color? colorMid = null, float flap = 0f)
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
            Shape.Sigil => _sigil,
            Shape.SigilInner => _sigilInner,
            Shape.Beam => _beam,
            Shape.Bat => _bat,
            _ => _glow
        };
        // 奥から (order=0 の背景) → 雲 → 衝撃波 → 光条 → 破片 → 星 → 光 → 画面の閃光 の順に重ねる
        sr.sortingOrder = SortingOrder + (order >= 0 ? order : shape == Shape.Solid ? 20 : (int)shape + 1);
        sr.color = FxMath.Rgba(color0.r, color0.g, color0.b, 0f);
        go.SetActive(true);

        // 光条のテクスチャは横長 (4:1) なので、縦の指定値がそのまま太さ (単位) になるよう補正する
        float aspect = shape switch { Shape.Ray => RayAspect, Shape.Beam => BeamAspect, _ => 1f };

        Active.Add(new Particle
        {
            Go = go, Sr = sr, Tf = tf, Pos = pos, Vel = vel, Drag = drag, Rise = rise, Delay = delay, Life = life,
            Sx0 = sx0, Sx1 = sx1, Sy0 = (sy0 < 0f ? sx0 : sy0) * aspect, Sy1 = (sy1 < 0f ? sx1 : sy1) * aspect,
            Rot = rot ?? Rnd(0f, 360f), Spin = spin,
            Color0 = color0, Color1 = color1, ColorMid = colorMid ?? color0, HasMid = colorMid.HasValue, Alpha = alpha, FadeIn = FxMath.Max(fadeIn, 0.001f), FadeOutFrom = fadeOutFrom,
            Twinkle = twinkle, TwinkleSpeed = twinkleSpeed, Phase = Rnd(0f, 6.28f), Stretch = stretch, FollowCamera = followCamera,
            // 画面全体の閃光は片付けの対象にしない (消すと揺れが最初からやり直しになる)
            Tag = followCamera ? (byte)0 : _tag, IsBeam = shape == Shape.Beam, AnchorX = _anchorX, AnchorY = _anchorY, Flap = flap
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

            // 羽ばたき: 縦を潰したり戻したりする
            if (p.Flap > 0f) sy *= 0.3f + 0.7f * FxMath.Abs(FxMath.Sin(p.Age * p.Flap));

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
        if (!_bat) _bat = MakeSprite(128, 128, BatAlpha);
        if (!_sigil) _sigil = MakeSprite(256, 256, SigilAlpha);
        if (!_sigilInner) _sigilInner = MakeSprite(256, 256, SigilInnerAlpha);

        if (!_beam)
        {
            // 横に一様・縦にぼけた光の帯。pivot は根元 (左端) で、両端だけ柔らかく消える
            _beam = MakeSprite(64, (int)(64 / BeamAspect), (x, y) =>
            {
                float u = (x + 1f) * 0.5f;
                float ends = FxMath.Clamp01(u / 0.03f) * FxMath.Clamp01((1f - u) / 0.08f);
                return FxMath.Clamp01(FxMath.Exp(-y * y * 2.4f) * ends);
            }, new Vector2(0f, 0.5f));
        }

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

    // 細い光の線 (d = 線までの距離、w = 太さ)
    private static float Line(float d, float w) => FxMath.Exp(-(d * d) / (w * w));

    // 正三角形の辺 (外接円の半径 r、一つの頂点の向き = start 度) を線の濃さで返す
    private static float TriangleLines(float x, float y, float r, float start, float w)
    {
        float a = 0f;
        float half = r * 0.5f;

        for (int i = 0; i < 3; i++)
        {
            // 辺の法線は向かいの頂点の反対側を向く
            float ang = (start + 180f + i * 120f) * FxMath.Deg2Rad;
            float d = x * FxMath.Cos(ang) + y * FxMath.Sin(ang);
            bool inside = true;

            for (int j = 0; j < 3; j++)
            {
                if (j == i) continue;
                float aj = (start + 180f + j * 120f) * FxMath.Deg2Rad;
                if (x * FxMath.Cos(aj) + y * FxMath.Sin(aj) > half + w) inside = false;
            }

            if (inside) a = FxMath.Max(a, Line(d - half, w));
        }

        return a;
    }

    // 外周の魔法陣: 二重の縁・文字の帯・六芒星・頂点の小円・内側の輪
    private static float SigilAlpha(float x, float y)
    {
        float d = FxMath.Sqrt(x * x + y * y);
        if (d > 1f) return 0f;

        float a = FxMath.Max(Line(d - 0.955f, 0.016f), Line(d - 0.9f, 0.009f));
        a = FxMath.Max(a, Line(d - 0.755f, 0.009f));

        // 文字の帯: 36 マスそれぞれに縦棒・横棒・斜め・小円を組み合わせた記号を置く
        if (d > 0.77f && d < 0.885f)
        {
            float ang = FxMath.Atan2(y, x);
            float cf = (ang / (2f * FxMath.PI) + 0.5f) * 36f;
            int ci = (int)cf;
            float u = cf - ci;
            float v = (d - 0.775f) / 0.105f;
            int h = (ci * 7 + 3) % 6;
            const float sw = 0.1f;
            float g = 0f;

            if (u > 0.2f && u < 0.8f)
            {
                if (h != 3) g = FxMath.Max(g, Line(u - 0.5f, sw));
                if (h is 0 or 3 or 4) g = FxMath.Max(g, Line(v - 0.5f, sw * 0.9f));
                if (h is 1 or 5) g = FxMath.Max(g, Line(v - 0.15f, sw * 0.9f));
                if (h is 2 or 4) g = FxMath.Max(g, Line(u - 0.5f - (v - 0.5f) * 0.6f, sw));
                if (h == 5) g = FxMath.Max(g, Line(FxMath.Sqrt((u - 0.5f) * (u - 0.5f) + (v - 0.72f) * (v - 0.72f)) - 0.14f, sw * 0.7f));
            }

            a = FxMath.Max(a, g * FxMath.Clamp01((0.8f - FxMath.Abs(u - 0.5f) * 1.6f) * 4f));
        }

        // 六芒星と、その頂点の小さな円
        a = FxMath.Max(a, TriangleLines(x, y, 0.72f, 90f, 0.011f));
        a = FxMath.Max(a, TriangleLines(x, y, 0.72f, 270f, 0.011f));

        for (int i = 0; i < 6; i++)
        {
            float ang = (90f + i * 60f) * FxMath.Deg2Rad;
            float vx = x - FxMath.Cos(ang) * 0.72f, vy = y - FxMath.Sin(ang) * 0.72f;
            a = FxMath.Max(a, Line(FxMath.Sqrt(vx * vx + vy * vy) - 0.055f, 0.009f));
        }

        a = FxMath.Max(a, Line(d - 0.36f, 0.011f));
        a = FxMath.Max(a, Line(d - 0.31f, 0.007f));

        // 縁の内側をうっすら満たす
        float fill = d < 0.955f ? 0.07f + 0.05f * FxMath.Pow(d / 0.955f, 4f) : 0f;
        return FxMath.Clamp01(a + fill);
    }

    // 内側の魔法陣: 八芒星 {8/3}・目盛り・三重の輪
    private static float SigilInnerAlpha(float x, float y)
    {
        float d = FxMath.Sqrt(x * x + y * y);
        if (d > 1f) return 0f;

        float a = FxMath.Max(Line(d - 0.95f, 0.014f), Line(d - 0.5f, 0.012f));
        a = FxMath.Max(a, Line(d - 0.2f, 0.012f));

        // 頂点 k と k+3 を結ぶ 8 本の弦。弦は外接円の内側にしかないので d で切るだけでよい
        const float r = 0.93f;
        float chord = r * FxMath.Cos(3f * FxMath.PI / 8f);
        if (d < r)
        {
            for (int i = 0; i < 8; i++)
            {
                float ang = (22.5f + i * 45f) * FxMath.Deg2Rad;
                float dist = x * FxMath.Cos(ang) + y * FxMath.Sin(ang) - chord;
                a = FxMath.Max(a, Line(dist, 0.011f));
            }
        }

        // 目盛り 24 本
        if (d > 0.56f && d < 0.66f)
        {
            float cf = (FxMath.Atan2(y, x) / (2f * FxMath.PI) + 0.5f) * 24f;
            float u = cf - (int)cf;
            a = FxMath.Max(a, Line(u - 0.5f, 0.07f));
        }

        return FxMath.Clamp01(a);
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
    // コウモリの影: 胴と頭と 2 本の耳、先へ向けて持ち上がり下の縁が 3 つに波打つ翼 (x, y は中心 0・縁 ±1)
    internal static float BatAlpha(float x, float y)
    {
        const float aa = 0.035f;
        float u = FxMath.Abs(x);

        float body = Ellipse(x, y + 0.02f, 0.15f, 0.27f, aa);
        float head = Ellipse(x, y - 0.28f, 0.13f, 0.12f, aa);

        float ear = 0f;
        if (y > 0.3f && y < 0.54f)
        {
            float w = 0.06f * (0.54f - y) / 0.24f;
            ear = FxMath.Clamp01((w - FxMath.Abs(u - 0.085f)) / aa + 0.5f);
        }

        float wing = 0f;
        if (u > 0.06f && u < 1f)
        {
            float k = (u - 0.06f) / 0.94f;
            float top = 0.08f + 0.42f * FxMath.Sin(k * FxMath.PI * 0.55f);
            float bottom = -0.24f + 0.42f * k + 0.17f * FxMath.Abs(FxMath.Sin(k * 3f * FxMath.PI)) + 0.3f * FxMath.Max(0f, k - 0.8f) / 0.2f;
            wing = FxMath.Clamp01(FxMath.Min(top - y, y - bottom) / aa + 0.5f);
        }

        return FxMath.Max(FxMath.Max(body, head), FxMath.Max(ear, wing));
    }

    private static float Ellipse(float dx, float dy, float rx, float ry, float aa)
    {
        float d = FxMath.Sqrt(dx / rx * (dx / rx) + dy / ry * (dy / ry));
        return FxMath.Clamp01((1f - d) * FxMath.Min(rx, ry) / aa + 0.5f);
    }

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
