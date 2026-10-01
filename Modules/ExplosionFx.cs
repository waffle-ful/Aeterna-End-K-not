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
        TornadoLift = 36,

        // 時間の巻き戻し: Pos = 発動者、Radius = 巻き戻している秒数
        TimeRewind = 37,

        // 巻き戻しで元の場所に戻った人 / 蘇った人: Pos = 着地点 / 死体の位置
        RewindLand = 38,
        RewindRevive = 39,

        // 時間を盗む: Pos = 殺された人
        TimeSteal = 40,

        // 時間魔術師が暴走を始める: Pos = 本人
        ChronoRampage = 41,

        // 迷彩の煙: Pos = 生存者の位置 (1 人ずつ)
        CamoMist = 42,

        // 油をかけられる (かけた人の画面だけ): Pos = かけられた人
        OilDrip = 43,

        // 時限爆弾の導火線 (殺した側の画面だけ): Pos = 本人、Radius = 爆発までの秒数
        DemoFuse = 44,

        // ハゲタカが死体を食らう: Pos = 死体、Radius = 死んだ人の PlayerId + 1 (血はその人の体の色)
        VultureFeast = 45,

        // 呪印 (呪った人の画面だけ): Pos = 呪われた人
        HexMark = 46,

        // 蜘蛛の巣を張る (張った人の画面だけ): Pos = 巣の中心、Radius = 罠の半径
        WebSpin = 47,

        // 巣に捕まる / 捕まった人が食われる: Pos = 捕まった人、Radius = 捕まっている秒数 (食われる時は 1)
        WebSnare = 48,
        WebDevour = 49,

        // 復讐のオーラ: Pos = 本人、Radius = 本人の PlayerId + 1 (覚醒の演出つき / オーラだけ)
        RevengeAwaken = 50,
        RevengeAura = 51
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
        Bat,
        HexSeal,
        ClockFace,
        WebDisc,
        FuseRope,
        Hourglass,
        ClockFine,
        ClockFineHalo,
        HexSealHalo,
        WebDiscHalo,
        FuseRopeHalo,
        HourglassHalo,
        VultureSil,
        VultureHalo,
        Feather,
        ClawSlash,
        SpiderSil,
        SpiderHalo,
        CrewSil
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
        public float Z;
        public float WobbleA, WobbleF;
        public bool Flat;
        public bool CamBand;
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
    private static Sprite _hexSeal;
    private static Sprite _clockFace;
    private static Sprite _webDisc;
    private static Sprite _fuseRope;
    private static Sprite _hourglass;
    private static Sprite _clockFine;
    private static Sprite _clockFineHalo;
    private static Sprite _hexSealHalo;
    private static Sprite _webDiscHalo;
    private static Sprite _fuseRopeHalo;
    private static Sprite _hourglassHalo;
    private static Sprite _vultureSil;
    private static Sprite _vultureHalo;
    private static Sprite _feather;
    private static Sprite _clawSlash;
    private static Sprite _spiderSil;
    private static Sprite _spiderHalo;
    private static Sprite _crewSil;
    private static Transform _flatRoot;

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
        if (kind is Kind.PuppetStrings or Kind.CurseStrings or Kind.RevengeAwaken or Kind.RevengeAura or Kind.VultureFeast or Kind.TimeSteal) return FxMath.Clamp(radius, 0f, 256f);
        return kind is Kind.Freeze or Kind.TimeStop or Kind.Tornado or Kind.TimeRewind or Kind.DemoFuse or Kind.WebSnare || HasExtra(kind) ? FxMath.Clamp(radius, 0.3f, 180f) : FxMath.Clamp(radius, 0.3f, 15f);
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
                SnareEmitters.Clear();
                FlowEmitters.Clear();
                SandEmitters.Clear();
                AuraEmitters.Clear();
                _awakenId = -1;
                _fuseUntil = 0f;
                RecentStrings.Clear();
                StoneTints.Clear();
                CarryJobs.Clear();
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

            // 巣の捕獲と導火線は会議で仕切り直しになる。復讐のオーラは会議を跨いで残す
            if (GameStates.IsMeeting)
            {
                if (SnareEmitters.Count > 0) SnareEmitters.Clear();
                FlowEmitters.Clear();
                SandEmitters.Clear();
                CarryJobs.Clear();
                _fuseUntil = 0f;
            }
            else if (!ExileController.Instance)
            {
                if (_awakenId >= 0 && GameStates.IsInTask) TryAwakenBurst();
                if (SnareEmitters.Count > 0) PulseSnares();
                if (FlowEmitters.Count > 0) PulseFlows();
                if (SandEmitters.Count > 0) PulseSands();
                if (_fuseUntil > 0f) PulseFuse();
                if (AuraEmitters.Count > 0) PulseAuras();
                if (CarryJobs.Count > 0) PulseCarries();
            }

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
                case Kind.TimeRewind:
                    SpawnTimeRewind(r.Pos, r.Radius);
                    FxSound.At("FxTimeRewind", r.Pos, 1f, everywhere: true);
                    break;
                case Kind.RewindLand:
                    SpawnRewindLand(r.Pos);
                    FxSound.At("FxRewindLand", r.Pos, 1f);
                    break;
                case Kind.RewindRevive:
                    SpawnRewindRevive(r.Pos);
                    FxSound.At("FxRewindRevive", r.Pos, 1f);
                    break;
                case Kind.TimeSteal:
                    SpawnTimeSteal(r.Pos, (int)(r.Radius + 0.5f) - 1);
                    FxSound.At("FxTimeSteal", r.Pos, 1f);
                    break;
                case Kind.ChronoRampage:
                    SpawnChronoRampage(r.Pos);
                    FxSound.At("FxChronoRampage", r.Pos, 1f);
                    break;
                case Kind.CamoMist:
                    SpawnCamoMist(r.Pos);
                    FxSound.At("FxCamoMist", r.Pos, 0.6f, everywhere: true);
                    break;
                case Kind.OilDrip:
                    SpawnOilDrip(r.Pos);
                    FxSound.At("FxOilDrip", r.Pos, 1f);
                    break;
                case Kind.DemoFuse:
                    StartFuse(r.Radius);
                    FxSound.At("FxDemoFuse", r.Pos, 1f, everywhere: true);
                    break;
                case Kind.VultureFeast:
                    SpawnVultureFeast(r.Pos, (int)(r.Radius + 0.5f) - 1);
                    FxSound.At("FxVultureFeast", r.Pos, 1f);
                    break;
                case Kind.HexMark:
                    SpawnHexMark(r.Pos);
                    FxSound.At("FxHexMark", r.Pos, 1f);
                    break;
                case Kind.WebSpin:
                    SpawnWebSpin(r.Pos, r.Radius);
                    FxSound.At("FxWebSpin", r.Pos, 1f);
                    break;
                case Kind.WebSnare:
                    StartSnare(r.Pos, r.Radius);
                    FxSound.At("FxWebSnare", r.Pos, 1f);
                    break;
                case Kind.WebDevour:
                    SpawnWebDevour(r.Pos);
                    FxSound.At("FxWebDevour", r.Pos, 1f);
                    break;
                case Kind.RevengeAwaken:
                    SpawnRevengeAwaken(r.Pos, (int)(r.Radius + 0.5f) - 1);
                    FxSound.At("FxRevengeAwaken", r.Pos, 1f);
                    break;
                case Kind.RevengeAura:
                    StartAura((int)(r.Radius + 0.5f) - 1);
                    FxSound.At("FxRevengeAura", r.Pos, 0.6f);
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
        Add(Shape.Glow, c, Vector2.zero, 0.7f, 1.2f, 1.6f, TornMid, TornBack, 0.45f, 0.05f, 0.5f, rot: 0f, sy0: 1.8f, sy1: 2.6f, rise: 1.2f, order: 0);
    }

    // ── 役職演出 第5弾 ───────────────────────────────────────────────────

    private static Vector2 Off(Vector2 c, float dx, float dy) => FxMath.V2(c.x + dx, c.y + dy);

    // 端点 a→b を結ぶ細い線 (中心置きの Solid)
    private static void Seg(Vector2 a, Vector2 b, float w, float life, Color c0, Color c1, float alpha, float fadeIn, float fadeOutFrom, float delay = 0f, float vy = 0f, int order = -1)
    {
        float dx = b.x - a.x, dy = b.y - a.y;
        float len = FxMath.Sqrt(dx * dx + dy * dy);
        float rot = FxMath.Atan2(dy, dx) * FxMath.Rad2Deg;
        Add(Shape.Solid, FxMath.V2((a.x + b.x) * 0.5f, (a.y + b.y) * 0.5f), FxMath.V2(0f, vy), life, len, len, c0, c1, alpha, fadeIn, fadeOutFrom, delay: delay, rot: rot, sy0: w, sy1: w, order: order);
    }

    private static readonly Color Sepia = new(0.85f, 0.62f, 0.32f);
    private static readonly Color SandLight = new(0.95f, 0.8f, 0.45f);
    private static readonly Color SandDark = new(0.7f, 0.5f, 0.25f);

    // 元の場所に戻る: 足元に金の波紋が二重に広がり、光の粒が体へ収束する
    private static void SpawnRewindLand(Vector2 c)
    {
        Vector2 f = Off(c, 0f, Feet.y);
        float q = Active.Count > 1200 ? 0.5f : 1f;

        // 上から光の筋が落ちてきて着地点を打ち、光の柱が立って細くなって消える (白い芯 + 金のにじみ)
        Add(Shape.Ray, Off(c, 0f, 4.2f), FxMath.V2(0f, -26f), 0.16f, 2.6f, 2.2f, WindWhite, TimeGold, 1f, 0.05f, 0.8f, rot: 90f, sy0: 0.14f, sy1: 0.1f, order: 9);
        Add(Shape.Glow, Off(c, 0f, 1.1f), Vector2.zero, 0.75f, 1f, 0.35f, TimeGold, Sepia, 0.85f, 0.03f, 0.35f, delay: 0.14f, rot: 0f, sy0: 3.6f, sy1: 3.8f, order: 0);
        Add(Shape.Glow, Off(c, 0f, 1.1f), Vector2.zero, 0.6f, 0.22f, 0.05f, WindWhite, TimeGold, 1f, 0.03f, 0.35f, delay: 0.14f, rot: 0f, sy0: 3.4f, sy1: 3.6f, twinkle: 0.3f, twinkleSpeed: 18f, order: 5);
        Add(Shape.Glow, f, Vector2.zero, 0.8f, 1.7f, 1.7f, RewindDark, RewindDark, 0.55f, 0.1f, 0.5f, delay: 0.1f, flat: true, order: 0);
        Add(Shape.Glow, f, Vector2.zero, 0.6f, 0.8f, 1.8f, TimeGold, Sepia, 0.75f, 0.03f, 0.3f, delay: 0.14f, flat: true, order: 1);
        Lit(Shape.ClockFine, Shape.ClockFineHalo, f, 0.75f, 1.1f, 1.25f, TimeGold, WindWhite, TimeGold, 0.95f, 0.05f, 0.5f, delay: 0.14f, rot: Rnd(0f, 360f), spin: -360f, flat: true);
        Add(Shape.Glow, Off(c, 0f, 0.2f), Vector2.zero, 0.2f, 0.5f, 1.5f, WindWhite, TimeGold, 0.8f, 0.02f, 0.3f, delay: 0.14f, order: 7);
        Add(Shape.Glow, f, Vector2.zero, 0.3f, 0.5f, 2.6f, WindWhite, ClockPale, 0.9f, 0.03f, 0.3f, delay: 0.14f, rot: 0f, sy0: 0.05f, sy1: 0.02f, order: 8);

        // 光の粒が体へ吸い寄せられる
        for (int i = 0; i < 16 * q; i++)
        {
            Vector2 d = Dir();
            float r = Rnd(0.9f, 1.5f);
            Vector2 start = Off(c, d.x * r, d.y * r);
            float life = Rnd(0.3f, 0.45f);
            Add(Shape.Star, start, FxMath.V2((c.x - start.x) / life, (c.y - start.y) / life), life, Rnd(0.07f, 0.13f), 0.03f, TimeGold, WindWhite, 1f, 0.15f, 0.8f, stretch: 0.08f, order: 8);
        }

        // 足元から昇って消える細かい火花
        for (int i = 0; i < 12 * q; i++)
        {
            Add(Shape.Star, Off(f, Rnd(-0.5f, 0.5f), Rnd(-0.1f, 0.1f)), FxMath.V2(Rnd(-0.2f, 0.2f), Rnd(1.5f, 3f)), Rnd(0.4f, 0.7f), Rnd(0.05f, 0.09f), 0.02f, WindWhite, TimeGold, 1f, 0.1f, 0.6f,
                drag: 1.5f, delay: Rnd(0.1f, 0.3f), twinkle: 0.6f, twinkleSpeed: Rnd(12f, 22f), order: 8);
        }
    }

    private static readonly Color ClockCrimson = new(0.95f, 0.12f, 0.15f);
    private static readonly Color ClockDeep = new(0.35f, 0.02f, 0.05f);
    private static readonly Color ClockVoid = new(0.12f, 0f, 0.02f);

    private static readonly Color MistViolet = new(0.5f, 0.42f, 0.62f);

    private static readonly Color OilAmber = new(0.35f, 0.22f, 0.05f);
    private static readonly Color OilBlack = new(0.04f, 0.03f, 0.02f);

    private static readonly Color FuseRed = new(1f, 0.12f, 0.1f);
    private static readonly Color FuseGlow = new(1f, 0.3f, 0.15f);
    private static float _fuseUntil;
    private static float _fuseFlip;
    private static float _fuseNext;
    private static bool _fuseOn;
    private static float _fuseTotal;
    private static float _fuseAngle;
    private static float _fuseRopeRot;
    private static int _fuseCount;
    private static readonly Color FuseDeep = new(0.4f, 0.03f, 0.03f);

    // 導火線: 残り時間のあいだ、自分の足元に赤い輪が点滅する (残り 1.5 秒で点滅が速くなる)
    private static void StartFuse(float seconds)
    {
        _fuseUntil = Time.time + seconds;
        _fuseTotal = seconds;
        _fuseAngle = 0f;
        _fuseCount = 0;
        _fuseFlip = 0f;
        _fuseNext = 0f;
        _fuseOn = false;
    }

    private static readonly Color FeatherDark = new(0.18f, 0.12f, 0.08f);
    private static readonly Color FeatherBrown = new(0.42f, 0.28f, 0.14f);

    private static readonly Color HexPurple = new(0.7f, 0.2f, 0.95f);
    private static readonly Color HexLight = new(0.9f, 0.6f, 1f);
    private static readonly Color HexDeep = new(0.35f, 0.08f, 0.55f);
    private static readonly Color HexVoid = new(0.1f, 0.02f, 0.16f);

    private static readonly Color WebLight = new(0.92f, 0.94f, 1f);
    private static readonly Color WebMid = new(0.55f, 0.58f, 0.7f);
    private static readonly Color WebShade = new(0.28f, 0.3f, 0.42f);
    private static readonly Color WebVoid = new(0.05f, 0.05f, 0.09f);

    private struct SnareEmitter
    {
        public Vector2 Pos;
        public float Until;
        public float Next;
        public int Beat;
    }

    private static readonly List<SnareEmitter> SnareEmitters = [];
    private static readonly Color WebDark = new(0.4f, 0.42f, 0.56f);

    // 巣に捕まる: 糸が体に巻き付き、足元の巣の模様が捕まっている間ずっと残る
    private static void StartSnare(Vector2 c, float seconds)
    {
        for (int i = 0; i < SnareEmitters.Count; i++)
        {
            SnareEmitter e = SnareEmitters[i];
            if (FxMath.Abs(e.Pos.x - c.x) > 0.3f || FxMath.Abs(e.Pos.y - c.y) > 0.3f) continue;

            e.Until = Time.time + seconds;
            SnareEmitters[i] = e;
            return;
        }

        if (SnareEmitters.Count >= 8) SnareEmitters.RemoveAt(0);
        SnareEmitters.Add(new SnareEmitter { Pos = c, Until = Time.time + seconds });
    }

    private static void PulseSnares()
    {
        float now = Time.time;

        for (int i = SnareEmitters.Count - 1; i >= 0; i--)
        {
            SnareEmitter e = SnareEmitters[i];

            if (now >= e.Until)
            {
                SnareEmitters.RemoveAt(i);
                if (Active.Count < 2000) SpawnSnareBreak(e.Pos);
                continue;
            }

            if (now < e.Next) continue;

            e.Next = now + 0.4f;
            e.Beat++;
            SnareEmitters[i] = e;

            if (Active.Count < 2000) SpawnSnarePulse(e.Pos, e.Beat, FxMath.Clamp01((e.Until - now) / 0.4f));
        }
    }

    private static readonly Color AwakenRed = new(0.8f, 0.02f, 0.05f);
    private static readonly Color AuraRed = new(0.95f, 0.1f, 0.12f);
    private static readonly Color AuraBlack = new(0.12f, 0f, 0.03f);
    private static readonly Color AuraGlow = new(0.45f, 0f, 0.05f);
    private static readonly Color AuraSpark = new(1f, 0.35f, 0.2f);

    // 2 重線: 外に太い暗色のにじみ、内に細い明色の芯 (芯だけ先に消したい時は coreFadeFrom を早める)
    private static void Line2(Vector2 a, Vector2 b, float w, float life, Color outer, Color core, float alpha, float fadeIn, float fadeOutFrom, float delay = 0f, float vy = 0f, float coreFadeFrom = -1f)
    {
        Seg(a, b, w * 3f, life, outer, outer, alpha * 0.4f, fadeIn, fadeOutFrom, delay, vy, 0);
        Seg(a, b, w, life, core, core, alpha, fadeIn, coreFadeFrom < 0f ? fadeOutFrom : coreFadeFrom, delay, vy);
    }

    // a→b を弧 (bow = 中央でずらす量) にして segs 本の 2 重線で描く
    private static void Arc(Vector2 a, Vector2 b, Vector2 bow, int segs, float w, float life, Color outer, Color core, float alpha, float fadeIn, float fadeOutFrom, float delay = 0f, float coreFadeFrom = -1f)
    {
        Vector2 prev = a;

        for (int i = 1; i <= segs; i++)
        {
            float t = (float)i / segs;
            float s = FxMath.Sin(t * FxMath.PI);
            Vector2 p = i == segs ? b : FxMath.V2(a.x + (b.x - a.x) * t + bow.x * s, a.y + (b.y - a.y) * t + bow.y * s);
            Line2(prev, p, w, life, outer, core, alpha, fadeIn, fadeOutFrom, delay, 0f, coreFadeFrom);
            prev = p;
        }
    }

    // 輪が描かれて残る: 描く 2 粒 (広がる → 残る) をつないで、描き終わりで止める
    private static void GrowRing(Vector2 p, float size, float squash, float grow, float hold, Color c0, Color c1, float alpha, float delay = 0f, float vy = 0f)
    {
        Add(Shape.Ring, p, Vector2.zero, grow, size * 0.3f, size, c0, c0, alpha, 0.1f, 1f, delay: delay, rot: 0f, sy0: size * 0.3f * squash, sy1: size * squash);
        Add(Shape.Ring, p, FxMath.V2(0f, vy), hold, size, size, c0, c1, alpha, 0.01f, 0.5f, delay: delay + grow, rot: 0f, sy0: size * squash, sy1: size * squash);
    }

    private static readonly Color SandCore = new(1f, 0.9f, 0.6f);

    // 導火線: 残り時間のあいだ、自分の足元に 2 重の赤い輪・輪を回る火花・残り時間の扇が出る (点滅に合わせる)
    private static void PulseFuse()
    {
        float now = Time.time;

        if (now >= _fuseUntil)
        {
            _fuseUntil = 0f;
            return;
        }

        if (now >= _fuseFlip)
        {
            _fuseOn = !_fuseOn;
            _fuseFlip = now + (_fuseUntil - now > 1.5f ? 0.3f : 0.075f);
        }

        if (!_fuseOn || now < _fuseNext) return;

        _fuseNext = now + 0.04f;

        PlayerControl lp = PlayerControl.LocalPlayer;

        if (!lp || !lp.IsAlive() || lp.inVent)
        {
            _fuseUntil = 0f;
            return;
        }

        if (Active.Count >= 2500) return;

        float remain = _fuseUntil - now;
        Vector2 f = Off(lp.Pos(), 0f, Feet.y);
        if (remain <= 1.5f) f = Off(f, Rnd(-0.02f, 0.02f), Rnd(-0.02f, 0.02f));
        _fuseCount++;
        _fuseAngle += (remain > 1.5f ? 1.2f : 3f) * 2f * FxMath.PI * 0.04f;

        _fuseRopeRot -= 20f * 0.04f;
        Add(Shape.Glow, f, Vector2.zero, 0.1f, 2.6f, 2.6f, FuseDeep, FuseDeep, 0.2f, 0.3f, 0.6f, flat: true, order: 0);
        Add(Shape.FuseRopeHalo, f, Vector2.zero, 0.1f, 2.1f, 2.1f, FuseGlow, FuseRed, 0.3f, 0.3f, 0.6f, rot: _fuseRopeRot, flat: true, order: 1);
        Add(Shape.FuseRope, f, Vector2.zero, 0.1f, 2.1f, 2.1f, FuseGlow, FuseDeep, 0.85f, 0.3f, 0.6f, rot: _fuseRopeRot, flat: true, order: 2);
        Add(Shape.Ring, f, Vector2.zero, 0.1f, 1.5f, 1.5f, FuseRed, FuseRed, 0.9f, 0.3f, 0.6f, rot: 0f, sy0: 0.525f, sy1: 0.525f);
        Add(Shape.Glow, f, Vector2.zero, 0.1f, 1.4f, 1.4f, FuseGlow, FuseRed, 0.6f, 0.3f, 0.6f, rot: 0f, sy0: 0.6f, sy1: 0.6f, wobble: 0.06f, wobbleHz: 5f, order: 0);

        // 輪の上を一周する導火線の火花
        Vector2 sp = Off(f, FxMath.Cos(_fuseAngle) * 0.75f, FxMath.Sin(_fuseAngle) * 0.26f);
        Add(Shape.Star, sp, Vector2.zero, 0.1f, 0.3f, 0.3f, WindWhite, FuseGlow, 1f, 0.2f, 0.6f);

        if (_fuseCount % 2 == 0)
        {
            float ea = Rnd(0f, 2f * FxMath.PI);
            Add(Shape.Glow, Off(f, FxMath.Cos(ea) * 1.0f, FxMath.Sin(ea) * 0.35f), FxMath.V2(Rnd(-0.2f, 0.2f), 0.6f), 0.4f, 0.1f, 0.03f, FuseGlow, FuseRed, 1f, 0.05f, 0.5f, twinkle: 0.6f, twinkleSpeed: 16f);

            for (int i = 0; i < 3; i++)
                Add(Shape.Glow, sp, FxMath.V2(Rnd(-1.5f, 1.5f), Rnd(-1.5f, 1.5f)), 0.25f, 0.08f, 0.03f, FuseGlow, FuseRed, 1f, 0.05f, 0.5f, drag: 2f, rise: 0.5f);
        }

        // 残り時間の扇: 時間が減るほど欠ける
        int n = _fuseCount % 2 == 0 ? (int)FxMath.Clamp(remain / FxMath.Max(_fuseTotal, 0.5f) * 12f + 0.99f, 0f, 12f) : 0;

        for (int k = 0; k < n; k++)
        {
            float ang = k * 30f;
            float rad = ang / FxMath.Rad2Deg;
            Add(Shape.Solid, Off(f, FxMath.Cos(rad) * 0.45f, FxMath.Sin(rad) * 0.16f), Vector2.zero, 0.18f, 0.14f, 0.14f, FuseRed, FuseRed, 0.65f, 0.3f, 0.6f, rot: ang, sy0: 0.06f, sy1: 0.06f);
        }
    }

    private static readonly Color HexOuter = new(0.3f, 0.05f, 0.45f);

    private static readonly Color WebBloom = new(0.4f, 0.45f, 0.6f);
    private static readonly Color WebCore = new(0.95f, 0.97f, 1f);

    // たるんだ環: 各辺を 3 分割して中央を中心側へ沈める
    private static void WebRing(Vector2 f, float rho, int n, float sag, float w, float life, float alpha, float fadeIn, float fadeOutFrom, float delay, float coreFadeFrom)
    {
        for (int k = 0; k < n; k++)
        {
            float a0 = k * 2f * FxMath.PI / n, a1 = (k + 1) * 2f * FxMath.PI / n;
            Vector2 a = Off(f, FxMath.Cos(a0) * rho, FxMath.Sin(a0) * rho);
            Vector2 b = Off(f, FxMath.Cos(a1) * rho, FxMath.Sin(a1) * rho);
            float am = (a0 + a1) * 0.5f;
            Arc(a, b, FxMath.V2(-FxMath.Cos(am) * sag, -FxMath.Sin(am) * sag), 3, w, life, WebBloom, WebCore, alpha, fadeIn, fadeOutFrom, delay, coreFadeFrom);
        }
    }

    private static readonly Color SnareOuter = new(0.35f, 0.4f, 0.55f);

    // 捕まった人を周りの壁へ張り付ける糸の端 (位置から決まるので拍ごとに同じ向き)
    private static Vector2 SnareAnchor(Vector2 c, int k)
    {
        float ang = (k * 60f + Hash01(c, 0) * 60f + (Hash01(c, k + 1) - 0.5f) * 30f) * FxMath.Deg2Rad;
        float len = 1.2f + 0.4f * Hash01(c, k + 11);
        return Off(c, FxMath.Cos(ang) * len, FxMath.Sin(ang) * len * 0.8f);
    }

    // 巣の 1 拍: 周り 6 方向から張った糸が震えて光沢が走り、体に細い糸が巻き付き、足元に小さな巣。最初の 1 拍は糸を撃ち込む
    private static void SpawnSnarePulse(Vector2 c, int beat, float fade)
    {
        const float life = 0.6f;
        float fi = beat == 1 ? 0.1f : 0.33f;
        Vector2 chest = Off(c, 0f, 0.05f);
        Vector2 f = Off(c, 0f, Feet.y);
        int glintAt = beat % 6;

        for (int k = 0; k < 6; k++)
        {
            Vector2 an = SnareAnchor(c, k);
            // 張った糸の震え: 拍ごとに少しだけ横へずらす
            Vector2 bow = FxMath.V2(Rnd(-0.03f, 0.03f), Rnd(-0.03f, 0.03f) - 0.05f);

            if (beat == 1)
            {
                Silk(an, chest, bow, 4, life + 0.1f, k * 0.04f, 0.12f, fade, 0.67f, false);
                // 壁に貼り付いた糸の根元
                Add(Shape.WebDisc, an, Vector2.zero, 0.9f, 0.15f, 0.32f, WebCore, WebMid, 0.8f, 0.2f, 0.6f, delay: k * 0.04f, order: 6);
                Add(Shape.Star, an, Vector2.zero, 0.3f, 0.24f, 0.08f, WindWhite, SilkCyan, 1f, 0.05f, 0.5f, delay: k * 0.04f, order: 9);
            }
            else
            {
                Silk(an, chest, bow, 4, life, 0f, 0f, 0.9f * fade, 0.67f, k == glintAt);

                // 糸に付いた露 (位置は糸ごとに固定・瞬きだけ変わる)
                for (int j = 0; j < 2; j++)
                {
                    float t = 0.2f + 0.55f * Hash01(c, k * 2 + j + 23);
                    float ds = 0.05f + 0.04f * Hash01(c, k * 2 + j + 41);
                    Add(Shape.Star, FxMath.V2(an.x + (chest.x - an.x) * t, an.y + (chest.y - an.y) * t), Vector2.zero, life, ds, ds, WindWhite, j == 0 ? SilkCyan : SilkPink, 0.95f * fade, fi, 0.67f,
                        twinkle: 0.6f, twinkleSpeed: 6f + 5f * Hash01(c, k + j + 57), order: 9);
                }
            }
        }

        // もがくたびに糸を体へ伝う震え
        if (beat > 1 && beat % 2 == 0)
        {
            Vector2 an = SnareAnchor(c, beat / 2 % 6);
            Add(Shape.Glow, an, FxMath.V2((chest.x - an.x) / 0.25f, (chest.y - an.y) / 0.25f), 0.25f, 0.12f, 0.22f, SilkCyan, WindWhite, 0.8f * fade, 0.1f, 0.6f, order: 9);
        }

        // 体に巻き付く細い糸 (体の後ろ側は薄い)
        int segs = Active.Count > 1200 ? 3 : 4;
        for (int i = 0; i < 8; i++)
        {
            float y = -0.4f + i * 0.8f / 7f;
            bool back = i % 2 == 1;
            float tilt = back ? -0.12f : 0.12f;
            Arc(Off(c, -0.42f, y), Off(c, 0.42f, y + tilt), FxMath.V2(0f, back ? 0.08f : -0.08f), segs, 0.014f, life, back ? WebBloom : SilkHalo, back ? WebMid : WindWhite, (back ? 0.4f : 1f) * fade,
                fi, 0.67f, delay: beat == 1 ? 0.2f + i * 0.04f : 0f);
        }

        // 足元の小さな巣
        Add(Shape.Glow, f, Vector2.zero, life, 1.4f, 1.4f, WebVoid, WebVoid, 0.3f * fade, fi, 0.67f, flat: true, order: 0);
        Lit(Shape.WebDisc, Shape.WebDiscHalo, f, life, 1.3f, 1.3f, WebBloom, WebCore, WebMid, 0.8f * fade, fi, 0.67f, rot: 15f * beat, wobble: 0.02f, wobbleHz: 7f, flat: true);

        if (beat % 3 == 0)
            Add(Shape.Star, Off(c, Rnd(-0.3f, 0.3f), Rnd(-0.4f, 0.4f)), Vector2.zero, 0.4f, 0.4f, 0.1f, WindWhite, SilkCyan, 1f, 0.15f, 0.5f, twinkle: 0.5f, twinkleSpeed: 20f);
    }

    // 捕獲が終わって糸が真ん中で切れ、両側へ弾けて縮む
    private static void SpawnSnareBreak(Vector2 c)
    {
        const float snap = 0.25f;
        Vector2 chest = Off(c, 0f, 0.05f);

        for (int k = 0; k < 6; k++)
        {
            Vector2 an = SnareAnchor(c, k);
            Vector2 mid = FxMath.V2((an.x + chest.x) * 0.5f, (an.y + chest.y) * 0.5f);
            float dx = an.x - chest.x, dy = an.y - chest.y;
            float half = FxMath.Sqrt(dx * dx + dy * dy) * 0.5f;
            float rot = FxMath.Atan2(dy, dx) * FxMath.Rad2Deg;

            // 壁側の半分は壁へ、体側の半分は体へ縮む
            Vector2 cA = FxMath.V2((an.x + mid.x) * 0.5f, (an.y + mid.y) * 0.5f);
            Vector2 cB = FxMath.V2((chest.x + mid.x) * 0.5f, (chest.y + mid.y) * 0.5f);
            Add(Shape.Solid, cA, FxMath.V2((an.x - cA.x) / snap, (an.y - cA.y) / snap), snap, half, 0.05f, WindWhite, SilkCyan, 1f, 0.01f, 0.5f, rot: rot, sy0: 0.016f, sy1: 0.016f, order: 8);
            Add(Shape.Solid, cB, FxMath.V2((chest.x - cB.x) / snap, (chest.y - cB.y) / snap), snap, half, 0.05f, WindWhite, SilkCyan, 1f, 0.01f, 0.5f, rot: rot, sy0: 0.016f, sy1: 0.016f, order: 8);
            Add(Shape.Star, mid, Vector2.zero, 0.2f, 0.26f, 0.08f, WindWhite, SilkCyan, 1f, 0.01f, 0.4f, order: 9);
        }

        for (int i = 0; i < 10; i++)
            Add(Shape.Solid, c, Dir() * Rnd(1f, 2f), Rnd(0.35f, 0.5f), 0.16f, 0.07f, WindWhite, WebMid, 1f, 0.02f, 0.5f, drag: 3f, spin: Rnd(-500f, 500f), sy0: 0.016f, sy1: 0.012f);
    }

    private static readonly Color WebVenom = new(0.35f, 0.6f, 0.2f);
    private static readonly Color WebVenomLight = new(0.7f, 1f, 0.45f);
    private static readonly Color SilkCyan = new(0.8f, 0.95f, 1f);
    private static readonly Color SilkPink = new(1f, 0.86f, 0.96f);
    private static readonly Color SilkHalo = new(0.7f, 0.82f, 1f);
    private static readonly Color SpiderBody = new(0.07f, 0.06f, 0.09f);

    // 画面全体に掛ける演出 (暗転・横切る影) を、遠くのプレイヤーの画面に出さないための距離判定
    private static bool NearCamera(Vector2 c, float reach)
    {
        Camera cam = Camera.main;
        if (!cam) return false;

        Vector3 p = cam.transform.position;
        float dx = p.x - c.x, dy = p.y - c.y;
        return dx * dx + dy * dy < reach * reach;
    }

    // 位置から決まる 0〜1 の乱数 (捕獲中は同じ糸を何度も描き直すので、毎回同じ向きになるようにする)
    private static float Hash01(Vector2 c, int k)
    {
        float v = FxMath.Sin(c.x * 12.9898f + c.y * 78.233f + k * 37.719f) * 43758.547f;
        return v - FxMath.Floor(v);
    }

    // 蜘蛛の糸 1 本: a→b を bow だけたわませ、segs 本の短い線で描く。芯は細い白 (照りが水色→桃へ移ってちらつく)・外に淡いにじみ。
    // grow 秒かけて a から b へ撃ち出し (先端に光)、glint なら描き終わってから光沢が糸を走る
    private static void Silk(Vector2 a, Vector2 b, Vector2 bow, int segs, float life, float delay, float grow, float alpha, float fadeOutFrom, bool glint, float w = 0.016f)
    {
        Vector2 prev = a;

        for (int i = 1; i <= segs; i++)
        {
            float t = (float)i / segs;
            float sn = FxMath.Sin(t * FxMath.PI);
            Vector2 p = FxMath.V2(a.x + (b.x - a.x) * t + bow.x * sn, a.y + (b.y - a.y) * t + bow.y * sn);
            float dx = p.x - prev.x, dy = p.y - prev.y;
            float len = FxMath.Sqrt(dx * dx + dy * dy) * 1.04f;
            float rot = FxMath.Atan2(dy, dx) * FxMath.Rad2Deg;
            Vector2 mid = FxMath.V2((p.x + prev.x) * 0.5f, (p.y + prev.y) * 0.5f);
            float d = delay + grow * (i - 1) / segs;
            float l = FxMath.Max(0.05f, life - (d - delay));
            float fo = FxMath.Clamp01(1f - (1f - fadeOutFrom) * life / l);
            Add(Shape.Solid, mid, Vector2.zero, l, len, len, SilkHalo, SilkHalo, alpha * 0.22f, 0.05f, fo, delay: d, rot: rot, sy0: w * 5f, sy1: w * 5f, order: 7);
            Add(Shape.Solid, mid, Vector2.zero, l, len, len, WindWhite, SilkPink, alpha, 0.05f, fo, delay: d, rot: rot, sy0: w, sy1: w, twinkle: 0.3f, twinkleSpeed: 6f, colorMid: SilkCyan, order: 8);
            prev = p;
        }

        if (grow > 0f)
            Add(Shape.Star, a, FxMath.V2((b.x - a.x) / grow, (b.y - a.y) / grow), grow, 0.22f, 0.16f, WindWhite, SilkCyan, 1f, 0.05f, 1f, delay: delay, order: 9);

        if (glint)
        {
            const float run = 0.35f;
            Add(Shape.Star, a, FxMath.V2((b.x - a.x) / run, (b.y - a.y) / run), run, 0.16f, 0.16f, WindWhite, SilkCyan, 0.9f, 0.1f, 0.7f, delay: delay + grow + Rnd(0f, 0.3f), order: 9);
        }
    }

    // 食われる: 頭上から蜘蛛が糸を垂らして降り、糸が体を下から上へ巻き上げて白い繭になり、震え、
    // 蜘蛛が噛みついて毒が光り、繭が潰れて糸屑と毒液が弾け、蜘蛛は糸を登って消える
    private static void SpawnWebDevour(Vector2 c)
    {
        for (int i = SnareEmitters.Count - 1; i >= 0; i--)
        {
            if (FxMath.Abs(SnareEmitters[i].Pos.x - c.x) < 0.6f && FxMath.Abs(SnareEmitters[i].Pos.y - c.y) < 0.6f) SnareEmitters.RemoveAt(i);
        }

        float q = Active.Count > 1200 ? 0.5f : 1f;
        const float descend = 0.3f;
        const float wrap = 0.4f;
        const float shake = 0.2f;
        const float bite = descend + wrap + shake;
        const float lunge = 0.08f;
        const float crush = bite + lunge;
        const float climb = 0.35f;
        const float top = 3f, start = 2f, hang = 0.95f, strike = 0.55f;
        const float size = 0.7f;

        // 蜘蛛: 頭を下 (対象) へ向けて降り、ぶら下がり、噛みつき、登って消える。後ろに毒の緑のリムライト
        Vector2 s0 = Off(c, 0f, start), s1 = Off(c, 0f, hang), s2 = Off(c, 0f, strike);
        float hold = bite - descend;
        SpiderPart(s0, FxMath.V2(0f, (hang - start) / descend), descend, size, 0f, 0.3f, 1f, 0.02f);
        SpiderPart(s1, Vector2.zero, hold, size, descend, 0.01f, 1f, 0.04f);
        SpiderPart(s1, FxMath.V2(0f, (strike - hang) / lunge), lunge, size, bite, 0.01f, 1f);
        SpiderPart(s2, FxMath.V2(0f, (top - strike) / climb), climb, size, crush, 0.01f, 0.4f);

        // 蜘蛛がぶら下がる糸: 上から蜘蛛のいる所まで伸び、登ると下から消える
        for (float h = top; h > hang; h -= 0.25f)
        {
            float appear = h > start ? 0f : (start - h) / (start - hang) * descend;
            float gone = crush + (h - strike) / (top - strike) * climb;
            float y2 = FxMath.Max(h - 0.25f, hang);
            Add(Shape.Solid, Off(c, 0f, (h + y2) * 0.5f), Vector2.zero, gone - appear, 0.016f, 0.016f, WindWhite, SilkCyan, 0.9f, 0.05f, 0.95f, delay: appear, rot: 0f, sy0: (h - y2) * 1.05f,
                sy1: (h - y2) * 1.05f, order: 8);
            Add(Shape.Solid, Off(c, 0f, (h + y2) * 0.5f), Vector2.zero, gone - appear, 0.08f, 0.08f, SilkHalo, SilkHalo, 0.2f, 0.05f, 0.95f, delay: appear, rot: 0f, sy0: (h - y2) * 1.05f,
                sy1: (h - y2) * 1.05f, order: 7);
        }

        // 後ろを暗く落として白い繭を浮かせる
        Add(Shape.Glow, c, Vector2.zero, crush + 0.3f, 1.6f, 1.6f, WebVoid, WebVoid, 0.5f, 0.2f, 0.7f, rot: 0f, sy0: 2f, sy1: 2f, order: 0);

        // 糸が下から上へ、体の丸みに沿って交差しながら巻き上がる
        for (int k = 0; k < 12; k++)
        {
            float y = -0.6f + k * 0.11f;
            float tilt = k % 2 == 0 ? 0.28f : -0.28f;
            float half = 0.5f - FxMath.Abs(y - 0.05f) * 0.35f;
            Silk(Off(c, -half, y - tilt * 0.5f), Off(c, half, y + tilt * 0.5f), FxMath.V2(0f, -0.09f), 4, crush + 0.1f - (descend + k * 0.03f), descend + k * 0.03f, 0.06f, 0.95f, 0.8f, false, 0.014f);
        }

        // 繭: 糸が巻き終わると白く満ち、震える
        Add(Shape.Glow, c, Vector2.zero, wrap + shake, 0.8f, 1.7f, WebBloom, WebBloom, 0.4f, 0.4f, 0.9f, delay: descend, rot: 0f, sy0: 1.2f, sy1: 2.4f, order: 0);
        Add(Shape.Glow, c, Vector2.zero, wrap, 0.4f, 1.2f, WebLight, WebLight, 0.8f, 0.5f, 1f, delay: descend, rot: 0f, sy0: 0.6f, sy1: 1.8f);

        for (int k = 0; k < 4; k++)
            Add(Shape.Glow, Off(c, k % 2 == 0 ? 0.04f : -0.04f, 0f), Vector2.zero, 0.06f, 1.2f, 1.2f, WebLight, WebLight, 0.9f, 0.05f, 0.9f, delay: descend + wrap + k * 0.05f, rot: 0f, sy0: 1.8f, sy1: 1.8f);

        // 待ち構える蜘蛛の牙から毒が滴る
        for (int k = 0; k < 3; k++)
        {
            Vector2 fang = Off(c, k % 2 == 0 ? -0.03f : 0.03f, hang - size * 0.2f);
            float dl = descend + 0.08f + k * 0.16f;
            Add(Shape.Glow, fang, FxMath.V2(0f, -0.2f), 0.3f, 0.05f, 0.07f, WebVenomLight, WebVenom, 1f, 0.2f, 0.7f, delay: dl, rise: -2.5f, rot: 0f, sy0: 0.07f, sy1: 0.13f, order: 12);
        }

        // 繭の糸に付いた露
        for (int i = 0; i < 10 * q; i++)
        {
            float ds = Rnd(0.04f, 0.08f);
            Add(Shape.Star, Off(c, Rnd(-0.3f, 0.3f), Rnd(-0.5f, 0.5f)), Vector2.zero, wrap + shake - 0.15f, ds, ds, WindWhite, i % 2 == 0 ? SilkCyan : SilkPink, 0.95f, 0.2f, 0.8f, delay: descend + 0.15f + Rnd(0f, 0.2f),
                twinkle: 0.7f, twinkleSpeed: Rnd(10f, 20f), order: 9);
        }

        // 噛みつき: 毒の閃光と 2 本の牙の光
        Add(Shape.Glow, Off(c, 0f, 0.4f), Vector2.zero, 0.3f, 0.3f, 1.2f, WebVenom, WebVenom, 0.85f, 0.02f, 0.3f, delay: crush, order: 7);
        Add(Shape.Star, Off(c, -0.06f, 0.42f), Vector2.zero, 0.2f, 0.32f, 0.08f, WindWhite, WebVenom, 1f, 0.02f, 0.4f, delay: crush, order: 9);
        Add(Shape.Star, Off(c, 0.06f, 0.42f), Vector2.zero, 0.2f, 0.32f, 0.08f, WindWhite, WebVenom, 1f, 0.02f, 0.4f, delay: crush, order: 9);

        // 潰れる
        Add(Shape.Glow, c, Vector2.zero, 0.25f, 1.2f, 0.9f, WebLight, WebMid, 0.95f, 0.02f, 0.5f, delay: crush, rot: 0f, sy0: 1.8f, sy1: 0.2f);

        for (int i = 0; i < 30 * q; i++)
            Add(Shape.Solid, c, Dir() * Rnd(1.5f, 3.2f), Rnd(0.4f, 0.7f), 0.18f, 0.08f, WindWhite, WebMid, 1f, 0.02f, 0.5f, drag: 3f, delay: crush, spin: Rnd(-500f, 500f), sy0: 0.016f, sy1: 0.012f);

        for (int i = 0; i < 12 * q; i++)
        {
            Vector2 d = Dir();
            Add(Shape.Glow, c, FxMath.V2(d.x * Rnd(1.5f, 3.5f), d.y * Rnd(1.5f, 3.5f) + 1f), Rnd(0.4f, 0.6f), 0.11f, 0.05f, WebVenomLight, WebVenom, 1f, 0.02f, 0.5f, drag: 2f, delay: crush, rise: -3f);
        }

        Add(Shape.Glow, c, Vector2.zero, 0.22f, 0.4f, 1.5f, WindWhite, WebLight, 0.85f, 0.02f, 0.3f, delay: crush, order: 7);
    }

    // 蜘蛛のシルエット 1 区間 (頭は下向き・毒の緑のリムライト付き・赤い目)
    private static void SpiderPart(Vector2 p, Vector2 v, float life, float size, float delay, float fadeIn, float fadeOutFrom, float wobble = 0f)
    {
        Add(Shape.SpiderHalo, p, v, life, size * 1.15f, size * 1.15f, WebVenom, WebVenom, 0.6f, fadeIn, fadeOutFrom, delay: delay, rot: 180f, wobble: wobble, wobbleHz: 6f, order: 10);
        Add(Shape.SpiderSil, p, v, life, size, size, SpiderBody, SpiderBody, 1f, fadeIn, fadeOutFrom, delay: delay, rot: 180f, wobble: wobble, wobbleHz: 6f, order: 11);
        Add(Shape.Star, Off(p, -0.04f * size, -0.33f * size * 0.5f), v, life, 0.09f * size, 0.09f * size, AuraRed, AuraRed, 1f, fadeIn, fadeOutFrom, delay: delay, twinkle: 0.4f, twinkleSpeed: 12f, order: 12);
        Add(Shape.Star, Off(p, 0.04f * size, -0.33f * size * 0.5f), v, life, 0.09f * size, 0.09f * size, AuraRed, AuraRed, 1f, fadeIn, fadeOutFrom, delay: delay, twinkle: 0.4f, twinkleSpeed: 12f, order: 12);
    }

    private static readonly Color FlameCore = new(1f, 0.55f, 0.35f);
    private static readonly Color AuraSmoke = new(0.1f, 0f, 0.03f);
    private static readonly Color AuraBright = new(0.7f, 0.05f, 0.1f);

    private static readonly Color AuraCrimson = new(0.75f, 0.04f, 0.08f);
    private static readonly Color AuraBlackRed = new(0.25f, 0f, 0.03f);

    // 体の奥に描く粒の z (プレイヤーの体より少し奥 = z が大きい方)
    private const float BackZ = 0.01f;

    private static int ReadBodyOrder(PlayerControl pc)
    {
        try
        {
            SpriteRenderer body = pc.cosmetics ? pc.cosmetics.currentBodySprite?.BodySprite : null;
            return body ? body.sortingOrder : 0;
        }
        catch { return 0; }
    }

    // 炎の舌 1 本 (胴は深紅 → 黒赤、明るい芯は寿命の最初の 15% だけ)。back のときは体の奥 (z, order) に描く
    private static void AuraFlame(Vector2 p, float upMin, float upMax, float sideDrift, float alpha, bool back, float z, int order, float width = 0.42f)
    {
        float life = Rnd(0.55f, 0.8f);
        Vector2 v = FxMath.V2(Rnd(-sideDrift, sideDrift), Rnd(upMin, upMax));

        if (back)
        {
            Add(Shape.Flame, p, v, life, 0.55f, 0.12f, AuraCrimson, AuraBlackRed, alpha, 0.1f, 0.4f, drag: 1.3f, sy0: width, sy1: 0.1f, stretch: 0.55f, z: z, absOrder: order);
            Add(Shape.Flame, p, v, life * 0.15f, 0.25f, 0.1f, FlameCore, AuraCrimson, 0.9f, 0.1f, 0.5f, drag: 1.3f, sy0: 0.15f, sy1: 0.08f, stretch: 0.55f, z: z - 0.001f, absOrder: order);
        }
        else
        {
            Add(Shape.Flame, p, v, life, 0.45f, 0.1f, AuraCrimson, AuraBlackRed, alpha, 0.1f, 0.4f, drag: 1.3f, sy0: 0.2f, sy1: 0.07f, stretch: 0.55f);
        }
    }

    private static readonly Color AuraBlack2 = new(0.08f, 0f, 0.02f);
    private static readonly Color AuraRed2 = new(0.9f, 0.06f, 0.1f);

    // 復讐のオーラ (最後のインポスターに付く炎): 残っている間 0.06 秒ごとに、そのときの本人の位置へ粒を足す
    private struct AuraEmitter
    {
        public byte Id;
        public PlayerControl Pc;
        public float Next;
        public float NextBeat;
        public float Beat2;
        public float LastBeat;
        public float LastStrength;
        public float NextBolt;
        public int Bolts;
        public float NextFoot;
        public float LastX, LastY;
        public bool HasLast;
        public int Count;
        public int BodyOrder;
    }

    private static readonly List<AuraEmitter> AuraEmitters = [];

    private static void StartAura(int id, float delay = 0f)
    {
        PlayerControl pc = id is >= 0 and <= 254 ? Utils.GetPlayerById((byte)id) : null;
        if (!pc) return;

        float now = Time.time;

        for (int i = 0; i < AuraEmitters.Count; i++)
        {
            if (AuraEmitters[i].Id != id) continue;

            AuraEmitter e = AuraEmitters[i];
            e.Pc = pc;
            AuraEmitters[i] = e;
            return;
        }

        if (AuraEmitters.Count >= 2) AuraEmitters.RemoveAt(0);
        AuraEmitters.Add(new AuraEmitter { Id = (byte)id, Pc = pc, BodyOrder = ReadBodyOrder(pc), Next = now + delay, NextBeat = now + delay + 0.5f, NextBolt = now + delay + 0.6f });
    }

    // 覚醒: 深紅の粒が体へ吸い込まれる → 閃光・二重の衝撃波・集中線・炎の噴出 → 火の粉が漂う余韻 → オーラが始まる
    private static int _awakenId = -1;

    private static void SpawnRevengeAwaken(Vector2 c, int id)
    {
        bool drawable = GameStates.IsInTask && !GameStates.IsMeeting && !ExileController.Instance;
        StartAura(id, drawable ? 0.25f : 0f);

        if (drawable) SpawnAwakenBurst(c, id);
        else _awakenId = id;
    }

    // 覚醒の爆発を保留していて、描ける状態になったら出す
    private static void TryAwakenBurst()
    {
        int id = _awakenId;
        _awakenId = -1;
        PlayerControl pc = id is >= 0 and <= 254 ? Utils.GetPlayerById((byte)id) : null;
        if (pc) SpawnAwakenBurst(pc.Pos(), id);
    }

    private static void SpawnAwakenBurst(Vector2 c, int id)
    {
        const float charge = 0.25f;
        float q = Active.Count > 1200 ? 0.5f : 1f;

        PlayerControl who = id is >= 0 and <= 254 ? Utils.GetPlayerById((byte)id) : null;
        int bo = who ? ReadBodyOrder(who) : 0;
        float zb = who ? who.transform.position.z + BackZ : 0f;
        int bk = who ? bo : int.MinValue;

        Add(Shape.Glow, c, Vector2.zero, charge, 2.6f, 0.8f, AuraRed2, AuraBlack2, 0.7f, 0.2f, 0.8f, order: 0, z: zb + 0.004f, absOrder: bk);

        for (int i = 0; i < 24 * q; i++)
        {
            Vector2 d = Dir();
            float rr = Rnd(2f, 3.2f);
            Vector2 start = Off(c, d.x * rr, d.y * rr);
            Add(Shape.Star, start, FxMath.V2((c.x - start.x) / charge, (c.y - start.y) / charge), charge, 0.3f, 0.1f, FlameCore, AuraRed2, 1f, 0.1f, 0.8f, stretch: 0.08f);
        }

        Impact(c, 6f, AwakenRed, 0.35f, 0.3f, 0.5f, delay: charge);
        Add(Shape.Glow, c, Vector2.zero, 0.4f, 1f, 4.5f, WindWhite, AuraRed2, 1f, 0.01f, 0.3f, delay: charge);
        Add(Shape.Ring, c, Vector2.zero, 0.7f, 0.5f, 10f, AuraRed2, AuraBlack2, 0.9f, 0.02f, 0.4f, delay: charge);
        Add(Shape.Ring, c, Vector2.zero, 0.7f, 0.4f, 7f, FlameCore, AuraRed2, 0.8f, 0.02f, 0.4f, delay: charge + 0.08f);

        for (int i = 0; i < 32 * q; i++)
        {
            float ang = i * 360f / (32f * q) + Rnd(-3f, 3f);
            float rad = ang / FxMath.Rad2Deg;
            float start = Rnd(1f, 1.6f);
            Add(Shape.Ray, Off(c, FxMath.Cos(rad) * start, FxMath.Sin(rad) * start), Vector2.zero, Rnd(0.35f, 0.55f), 0.5f, Rnd(6f, 9f), AuraRed2, AuraBlack2, 0.95f, 0.05f, 0.5f,
                delay: charge, rot: ang, sy0: 0.18f, sy1: 0.03f);
        }

        for (int i = 0; i < 30 * q; i++)
        {
            Vector2 d = Dir();
            Add(Shape.Flame, Off(c, d.x * 0.3f, d.y * 0.3f), FxMath.V2(d.x * Rnd(1.5f, 4f), FxMath.Abs(d.y) * Rnd(1f, 3f) + 1f), Rnd(0.6f, 0.9f), 0.7f, 0.15f, AuraCrimson, AuraBlackRed, 0.85f, 0.05f, 0.4f, drag: 2f,
                delay: charge, sy0: 0.5f, sy1: 0.12f, stretch: 0.5f, z: zb, absOrder: bk);
        }

        for (int i = 0; i < 40 * q; i++)
        {
            float s = Rnd(0.08f, 0.16f);
            Add(Shape.Star, Off(c, (FxMath.Value < 0.5f ? -1f : 1f) * Rnd(0.35f, 0.9f), Rnd(-0.6f, 0.6f)), FxMath.V2(Rnd(-0.5f, 0.5f), Rnd(0f, 0.6f)), Rnd(1f, 1.5f), s, s, AuraSpark, AuraRed2, 1f, 0.1f, 0.6f,
                delay: charge + Rnd(0f, 0.3f), rise: 0.8f, twinkle: 0.6f, twinkleSpeed: Rnd(10f, 18f));
        }

    }

    // 自分の画面のオーラだけを消す (検収用・送信なし)
    internal static void StopAuras() => AuraEmitters.Clear();

    private static void PulseAuras()
    {
        float now = Time.time;

        for (int i = AuraEmitters.Count - 1; i >= 0; i--)
        {
            AuraEmitter e = AuraEmitters[i];
            if (now < e.Next) continue;

            e.Next = now + 0.06f;
            e.Count++;
            AuraEmitters[i] = e;

            PlayerControl pc = e.Pc;

            if (!pc)
            {
                AuraEmitters.RemoveAt(i);
                continue;
            }

            PlayerControl lp = PlayerControl.LocalPlayer;

            if (lp && lp.IsAlive() && lp.PlayerId != e.Id)
            {
                AuraEmitters.RemoveAt(i);
                continue;
            }

            if (!pc.IsAlive())
            {
                AuraEmitters.RemoveAt(i);
                if (GameStates.IsInTask && !ExileController.Instance) SpawnAuraBurnout(pc.Pos());
                continue;
            }

            if (pc.inVent || Main.Invisible.Contains(e.Id) || pc.invisibilityAlpha < 0.9f || Active.Count >= 2500)
            {
                e.HasLast = false;
                AuraEmitters[i] = e;
                continue;
            }

            Vector2 c = pc.Pos();
            bool thin = Active.Count > 1200;
            int bo = e.BodyOrder;
            float zb = pc.transform.position.z + BackZ;

            // 鼓動 (1.2 秒周期・2 拍): 体の奥の光が跳ねる
            if (now >= e.NextBeat)
            {
                e.LastBeat = now;
                e.LastStrength = 1f;
                e.NextBeat += 1.2f;
                if (e.NextBeat < now) e.NextBeat = now + 1.2f;
                e.Beat2 = now + 0.18f;
            }
            else if (e.Beat2 > 0f && now >= e.Beat2)
            {
                e.LastBeat = now;
                e.LastStrength = 0.7f;
                e.Beat2 = 0f;
            }

            float b = e.LastStrength * FxMath.Clamp01(1f - (now - e.LastBeat) / 0.2f);
            float gs = 1f + 0.15f * b;

            // 奥: 背後の光 2 枚・黒い瘴気・炎の舌の大半
            Add(Shape.Glow, c, Vector2.zero, 0.12f, 1.9f * gs, 1.9f * gs, AuraGlow, AuraGlow, FxMath.Min(0.55f * (1f + 0.4f * b), 1f), 0.4f, 0.5f, z: zb + 0.004f, absOrder: bo);
            Add(Shape.Glow, c, Vector2.zero, 0.12f, 1.15f * gs, 1.15f * gs, AuraBright, AuraBright, FxMath.Min(0.7f * (1f + 0.4f * b), 1f), 0.4f, 0.5f, z: zb + 0.003f, absOrder: bo);

            if (e.Count % 2 == 0)
                Add(Shape.Cloud, Off(c, Rnd(-0.3f, 0.3f), Rnd(-0.3f, 0.2f)), FxMath.V2(Rnd(-0.1f, 0.1f), 0.6f), 0.9f, 0.5f, 0.9f, AuraSmoke, AuraSmoke, 0.6f, 0.2f, 0.5f, spin: Rnd(-40f, 40f), z: zb + 0.002f, absOrder: bo);

            for (int k = 0; k < (thin ? 1 : 3); k++)
            {
                float a = Rnd(0f, 2f * FxMath.PI);
                AuraFlame(Off(c, FxMath.Cos(a) * 0.45f, FxMath.Sin(a) * 0.45f), 1.4f, 2.2f, 0.15f, 0.85f, true, zb, bo);
            }

            // 手前: 輪郭の外周だけから出る細い炎 1 本 (体の中心 ±0.3 には置かない)
            float side = FxMath.Value < 0.5f ? -1f : 1f;
            AuraFlame(Off(c, side * Rnd(0.38f, 0.5f), Rnd(-0.4f, 0.3f)), 1.2f, 1.9f, 0.05f, 0.45f, false, 0f, 0);

            // 手前: 螺旋を描いて昇る火の粉 (外周のみ)
            if (e.Count % 2 == 0)
            {
                for (int k = 0; k < 2; k++)
                {
                    float s = Rnd(0.08f, 0.16f);
                    float dir = k == 0 ? -1f : 1f;
                    Vector2 p = Off(c, dir * Rnd(0.35f, 0.55f), Rnd(-0.3f, 0.3f));
                    Add(Shape.Star, p, FxMath.V2(0.5f * dir, 0.8f), 0.4f, s, s, AuraSpark, AuraSpark, 1f, 0.1f, 0.6f, twinkle: 0.6f, twinkleSpeed: 16f);
                    Add(Shape.Star, Off(p, 0.2f * dir, 0.32f), FxMath.V2(-0.2f * dir, 0.8f), 0.4f, s, s * 0.5f, AuraSpark, AuraRed2, 1f, 0.05f, 0.6f, delay: 0.4f, twinkle: 0.6f, twinkleSpeed: 16f);
                }
            }

            // 手前: 赤い稲光 (体の横)
            if (now >= e.NextBolt)
            {
                e.NextBolt = now + Rnd(0.3f, 0.5f);
                e.Bolts++;

                if (e.Bolts % 3 == 0)
                {
                    // 回る光点 2 つの間を、体の外側を回り込んで結ぶ放電
                    float ba = now / 1.4f * 2f * FxMath.PI;
                    float bx = FxMath.Cos(ba) * 0.55f, by = FxMath.Sin(ba) * 0.55f;
                    float sgn = FxMath.Value < 0.5f ? -1f : 1f;
                    Vector2 p = Off(c, bx, by - 0.05f);

                    for (int k = 1; k <= 7; k++)
                    {
                        float t = k / 7f;
                        float bow = FxMath.Sin(t * FxMath.PI) * 0.5f * sgn;
                        float jx = k == 7 ? 0f : Rnd(-0.04f, 0.04f), jy = k == 7 ? 0f : Rnd(-0.04f, 0.04f);
                        Vector2 n = Off(c, bx * (1f - 2f * t) - by * bow + jx, by * (1f - 2f * t) + bx * bow - 0.05f + jy);
                        Line2(p, n, 0.012f, 0.14f, AuraOuter, AuraBolt, 1f, 0.1f, 0.5f, delay: k * 0.008f);
                        p = n;
                    }
                }
                else
                {
                    float bs = FxMath.Value < 0.5f ? -1f : 1f;
                    Vector2 p = Off(c, bs * Rnd(0.4f, 0.5f), Rnd(-0.35f, 0.1f));
                    int segs = FxMath.Range(5, 7);

                    for (int k = 0; k < segs; k++)
                    {
                        Vector2 n = Off(p, (k % 2 == 0 ? 1f : -0.6f) * bs * Rnd(0.04f, 0.08f), Rnd(0.05f, 0.08f));
                        Line2(p, n, 0.012f, 0.12f, AuraOuter, AuraBolt, 1f, 0.1f, 0.5f, delay: k * 0.01f);

                        // 途中から 1 本だけ短い枝
                        if (k == 2) Line2(n, Off(n, bs * Rnd(0.06f, 0.1f), Rnd(-0.02f, 0.04f)), 0.009f, 0.1f, AuraOuter, AuraBolt, 0.8f, 0.1f, 0.5f, delay: 0.03f);
                        p = n;
                    }

                    Add(Shape.Star, p, Vector2.zero, 0.14f, 0.14f, 0.04f, WindWhite, AuraBolt, 1f, 0.05f, 0.4f, delay: segs * 0.01f);
                }
            }

            // 足元は歩いている時だけ燃える足跡を置く (体の奥)
            bool moved = e.HasLast && (c.x - e.LastX) * (c.x - e.LastX) + (c.y - e.LastY) * (c.y - e.LastY) >= 0.0025f;
            e.LastX = c.x;
            e.LastY = c.y;
            e.HasLast = true;

            if (moved && now >= e.NextFoot)
            {
                e.NextFoot = now + 0.22f;
                Vector2 f = Off(c, 0f, Feet.y);
                Add(Shape.Glow, f, Vector2.zero, 0.9f, 0.5f, 0.8f, AuraCrimson, AuraBlackRed, 0.8f, 0.1f, 0.5f, rot: 0f, sy0: 0.175f, sy1: 0.28f, z: zb + 0.003f, absOrder: bo);

                for (int k = 0; k < 2; k++)
                    AuraFlame(Off(f, Rnd(-0.25f, 0.25f), 0f), 0.6f, 1f, 0.1f, 0.85f, true, zb, bo, 0.3f);

                for (int k = 0; k < 2; k++)
                {
                    float sp = Rnd(0.08f, 0.14f);
                    Add(Shape.Star, Off(f, (k == 0 ? -1f : 1f) * Rnd(0.35f, 0.5f), 0f), FxMath.V2(Rnd(-0.2f, 0.2f), 0.2f), Rnd(0.4f, 0.7f), sp, sp, AuraSpark, AuraSpark, 1f, 0.1f, 0.6f, rise: 0.8f, twinkle: 0.6f, twinkleSpeed: 16f);
                }
            }

            // 体の周りを回る赤黒い光点 2 つ (1 周 1.4 秒・尾を引く)。奥側は体の奥、手前側は中心を避けた所だけ手前
            for (int k = 0; k < 2; k++)
            {
                float oa = now / 1.4f * 2f * FxMath.PI + k * FxMath.PI;
                float ox = FxMath.Cos(oa) * 0.55f, oy = FxMath.Sin(oa) * 0.55f;
                bool near = oy < 0f && FxMath.Abs(ox) >= 0.3f;
                float oz = near ? 0f : zb + 0.001f;
                int oo = near ? int.MinValue : bo;
                Vector2 op = Off(c, ox, oy - 0.05f);
                Add(Shape.Glow, op, Vector2.zero, 0.3f, 0.3f, 0.16f, AuraRed2, AuraBlackRed, 0.8f, 0.1f, 0.4f, z: oz, absOrder: oo);
                Add(Shape.Glow, op, Vector2.zero, 0.3f, 0.11f, 0.05f, AuraSpark, AuraRed2, 1f, 0.1f, 0.4f, z: oz, absOrder: oo);
            }

            AuraEmitters[i] = e;
        }
    }

    private static readonly Color AuraOuter = new(0.5f, 0f, 0.08f);
    private static readonly Color AuraBolt = new(1f, 0.3f, 0.3f);

    // 燃え尽きる: 黒い煙が立ちのぼる
    private static void SpawnAuraBurnout(Vector2 c)
    {
        for (int i = 0; i < 8; i++)
        {
            Add(Shape.Cloud, Off(c, Rnd(-0.3f, 0.3f), Rnd(-0.3f, 0.2f)), FxMath.V2(Rnd(-0.3f, 0.3f), Rnd(0.5f, 1.2f)), 0.8f, 0.4f, Rnd(0.9f, 1.3f), AuraBlack, SmokeShade, 0.8f, 0.1f, 0.5f,
                spin: Rnd(-60f, 60f));
        }
    }

    private static readonly Color OilRim = new(1f, 0.7f, 0.2f);

    // 油をかけられる: 頭頂から明るい縁の大きな滴がとろりと落ち、体を筋が伝い、足元に縁取りのある 3 層の油溜まりがにじむ
    private static void SpawnOilDrip(Vector2 c)
    {
        Vector2 f = Off(c, 0f, Feet.y);
        var highlight = new Color(1f, 0.95f, 0.8f);
        const float half = 0.6f;

        for (int i = 0; i < 10; i++)
        {
            float x = Rnd(-0.22f, 0.22f);
            float d0 = Rnd(0f, 0.3f);
            float s = Rnd(0.22f, 0.3f);
            // 落ち始めはゆっくり、後半は速く (合わせて 1.2 秒)
            for (int j = 0; j < 2; j++)
            {
                Vector2 p = Off(c, x, j == 0 ? 0.55f : 0.25f);
                Vector2 v = FxMath.V2(0f, j == 0 ? -0.5f : -1f);
                float d = d0 + j * half;
                float fo = j == 1 ? 0.85f : 1f;
                Add(Shape.Glow, p, v, half, s * 2f, s * 2f, OilRim, OilRim, 0.6f, 0.1f, fo, delay: d, order: 0);
                Add(Shape.Glow, p, v, half, s, s, OilAmber, OilAmber, 1f, 0.1f, fo, delay: d, rot: 0f, sy0: s * 1.3f, sy1: s * 1.3f);
                Add(Shape.Star, Off(p, -s * 0.2f, s * 0.2f), v, half, 0.09f, 0.09f, highlight, highlight, 0.95f, 0.1f, fo, delay: d);
            }
        }

        // 体を伝う筋
        for (int k = 0; k < 3; k++)
            Add(Shape.Beam, Off(c, -0.2f + k * 0.2f, 0.55f), Vector2.zero, 0.8f, 0.02f, 0.85f, OilRim, OilAmber, 0.9f, 0.05f, 0.6f, delay: k * 0.1f, rot: -90f, sy0: 0.06f, sy1: 0.06f);

        // 足元の油溜まり: 黒い面 → 琥珀 → 縁取りと白い照り返し → 虹の照り
        const float land = 1.1f;
        Add(Shape.Cloud, f, Vector2.zero, 2.5f, 0.5f, 1.4f, OilBlack, OilBlack, 0.6f, 0.2f, 0.8f, delay: land, rot: 0f, sy0: 0.15f, sy1: 0.42f, order: 0);
        Add(Shape.Cloud, f, Vector2.zero, 2.4f, 0.4f, 1f, OilAmber, OilBlack, 0.7f, 0.2f, 0.8f, delay: land + 0.1f, rot: 0f, sy0: 0.12f, sy1: 0.3f, order: 1);
        Add(Shape.Ring, f, Vector2.zero, 2.4f, 0.7f, 1.5f, OilRim, OilRim, 0.8f, 0.15f, 0.8f, delay: land, rot: 0f, sy0: 0.245f, sy1: 0.525f);
        Add(Shape.Glow, Off(f, -0.25f, 0.05f), Vector2.zero, 2.2f, 0.3f, 0.5f, WindWhite, WindWhite, 0.6f, 0.2f, 0.8f, delay: land + 0.2f, rot: 0f, sy0: 0.06f, sy1: 0.12f);
        Add(Shape.Glow, Off(f, 0.3f, -0.03f), Vector2.zero, 2.2f, 0.25f, 0.4f, WindWhite, WindWhite, 0.6f, 0.2f, 0.8f, delay: land + 0.3f, rot: 0f, sy0: 0.05f, sy1: 0.1f);

        for (int k = 0; k < 2; k++)
        {
            float s = k == 0 ? 1.1f : 0.9f;
            Add(Shape.Ring, Off(f, k * 0.1f - 0.05f, 0f), Vector2.zero, 2.2f, s * 0.6f, s, new Color(0.9f, 0.3f, 0.9f), new Color(0.95f, 0.85f, 0.3f), 0.6f, 0.2f, 0.7f, delay: land + 0.2f, rot: 0f,
                sy0: s * 0.18f, sy1: s * 0.3f, twinkle: 0.4f, twinkleSpeed: 3f + k * 2f, colorMid: new Color(0.3f, 0.9f, 0.9f));
        }
    }

    private static readonly Color VultureShadow = new(0.12f, 0.08f, 0.06f);
    private static readonly Color FeatherEdge = new(0.55f, 0.4f, 0.28f);
    private static readonly Color VultureEye = new(1f, 0.15f, 0.08f);

    private static readonly Color VultureRim = new(0.85f, 0.22f, 0.06f);

    // ハゲタカに持ち去られる死体 (この画面の中だけで動かす)
    private struct CarryJob
    {
        public DeadBody Body;
        public Vector3 From;
        public Vector2 V;
        public float Start;
        public float Dur;
    }

    private static readonly List<CarryJob> CarryJobs = [];

    private static DeadBody NearestBody(Vector2 c, float within)
    {
        DeadBody best = null;
        float bestD = within * within;

        foreach (DeadBody b in Object.FindObjectsOfType<DeadBody>())
        {
            if (!b || !b.gameObject.activeInHierarchy) continue;
            Vector3 bp = b.transform.position;
            float d = (bp.x - c.x) * (bp.x - c.x) + (bp.y - c.y) * (bp.y - c.y);
            if (d >= bestD) continue;
            bestD = d;
            best = b;
        }

        return best;
    }

    // つかまれてから飛び去るまで、死体を鳥の速さで運び、振り子のように揺らし、運び終えたら隠す
    private static void PulseCarries()
    {
        float now = Time.time;

        for (int i = CarryJobs.Count - 1; i >= 0; i--)
        {
            CarryJob j = CarryJobs[i];

            if (!j.Body)
            {
                CarryJobs.RemoveAt(i);
                continue;
            }

            float t = now - j.Start;
            if (t < 0f) continue;

            Transform tf = j.Body.transform;

            if (t >= j.Dur)
            {
                j.Body.gameObject.SetActive(false);
                CarryJobs.RemoveAt(i);
                continue;
            }

            // 最初の一瞬で少し持ち上げてから、爪の下にぶら下げて運ぶ
            float lift = FxMath.Min(1f, t / 0.08f) * 0.15f;
            tf.position = FxMath.V3(j.From.x + j.V.x * t, j.From.y + j.V.y * t + lift, j.From.z - 0.5f);
            tf.localRotation = FxMath.RotZ(FxMath.Sin(t * 14f) * 25f - 20f);
        }
    }

    private static readonly Color BoneWhite = new(1f, 0.95f, 0.85f);

    // ハゲタカ 1 区間: 黒いシルエットの後ろに暗赤のリムライト (逆光)・琥珀の目。rot は頭の向き (度)
    private static void VulturePart(Vector2 p, Vector2 v, float life, float size, float rot, float delay, float fadeIn, float fadeOutFrom, float alpha = 1f, float flap = 14f)
    {
        float rad = rot * FxMath.Deg2Rad;
        Vector2 eye = FxMath.V2(p.x + FxMath.Cos(rad) * size * 0.2f, p.y + FxMath.Sin(rad) * size * 0.2f);
        Add(Shape.VultureHalo, p, v, life, size * 1.08f, size * 1.08f, VultureRim, AwakenRed, 0.7f * alpha, fadeIn, fadeOutFrom, delay: delay, rot: rot, flap: flap, order: 10);
        Add(Shape.VultureSil, p, v, life, size, size, VultureShadow, VultureShadow, alpha, fadeIn, fadeOutFrom, delay: delay, rot: rot, flap: flap, order: 11);
        Add(Shape.Star, eye, v, life, 0.2f, 0.2f, VultureEye, VultureEye, alpha, fadeIn, fadeOutFrom, delay: delay, twinkle: 0.4f, twinkleSpeed: 14f, order: 12);
    }

    // ハゲタカが死体を持ち去る: 巨大な影が画面を横切る → 右上から急降下 (残像) → 止まらずに爪で死体をつかみ (爪痕・血・羽根が弾ける) →
    // 体色のクルーの形をぶら下げたまま左上へ高速で飛び去り、血が滴り、羽根が舞い落ちる
    private static void SpawnVultureFeast(Vector2 c, int victimId)
    {
        int colorId = -1;

        try
        {
            if (victimId is >= 0 and <= 254 && GameData.Instance) colorId = GameData.Instance.GetPlayerById((byte)victimId)?.DefaultOutfit.ColorId ?? -1;
        }
        catch (System.Exception e) { Utils.ThrowException(e); }

        BloodColors(colorId, out Color bloodBright, out Color blood, out Color bloodDark);
        float q = Active.Count > 1200 ? 0.5f : 1f;
        const float omen = 0.4f;
        const float dive = 0.22f;
        const float grab = omen + dive;
        const float carry = 0.55f;
        const float size = 2.8f;

        // 前触れ: 近くにいる人の画面だけ少し暗くなり、巨大な鳥の影が横切る
        if (NearCamera(c, 12f))
        {
            Add(Shape.Solid, c, Vector2.zero, grab + carry + 0.3f, 1f, 1f, VultureShadow, VultureShadow, 0.16f, 0.15f, 0.75f, followCamera: true);

            for (int k = 0; k < 3; k++)
            {
                Vector2 from = Off(c, 9f + k * 0.7f, 2f);
                Add(Shape.VultureSil, from, FxMath.V2(-18f, 0f), 1f, 7f, 7f, VultureShadow, VultureShadow, k == 0 ? 0.45f : 0.2f / k, 0.05f, 0.9f, delay: k * 0.03f, rot: 180f, flap: 6f, order: 1);
            }
        }

        // 急降下して死体をかすめ、そのまま左上へ抜ける (どちらも残像を引く)
        Vector2 p0 = Off(c, 3.4f, 3.6f), low = Off(c, 0f, 1.3f), gone = Off(c, -5f, 4f);
        float diveRot = FxMath.Atan2(low.y - p0.y, low.x - p0.x) * FxMath.Rad2Deg;
        float awayRot = FxMath.Atan2(gone.y - low.y, gone.x - low.x) * FxMath.Rad2Deg;
        var diveV = FxMath.V2((low.x - p0.x) / dive, (low.y - p0.y) / dive);
        var awayV = FxMath.V2((gone.x - low.x) / carry, (gone.y - low.y) / carry);

        for (int k = 3; k >= 0; k--)
        {
            float a = k == 0 ? 1f : k == 1 ? 0.5f : k == 2 ? 0.3f : 0.15f;
            VulturePart(p0, diveV, dive, size, diveRot, omen + k * 0.03f, 0.4f, 1f, a, 20f);
            VulturePart(low, awayV, carry, size, awayRot, grab + k * 0.03f, 0.01f, 0.6f, a, 24f);
        }

        // つかまれた死体: 本物の死体をこの画面の中だけで爪の下にぶら下げて運び、見えなくなったら隠す (食べられた死体は通報できない)。
        // 死体が見つからない時 (もう消えている等) は体色のクルーの形で代わりに見せる
        Vector2 hang = Off(c, 0f, -0.15f);
        DeadBody body = NearestBody(c, 1.2f);

        if (body)
        {
            if (CarryJobs.Count >= 4) CarryJobs.RemoveAt(0);
            CarryJobs.Add(new CarryJob { Body = body, From = body.transform.position, V = awayV, Start = Time.time + grab, Dur = carry });
        }
        else Add(Shape.CrewSil, hang, awayV, carry, 0.95f, 0.85f, blood, bloodDark, 1f, 0.01f, 0.6f, delay: grab, rot: -70f, spin: 90f, order: 10);

        // 滴る血
        for (int i = 0; i < 10 * q; i++)
        {
            float t = Rnd(0.05f, carry * 0.7f);
            Vector2 at = FxMath.V2(hang.x + awayV.x * t, hang.y + awayV.y * t);
            Add(Shape.Glow, at, FxMath.V2(awayV.x * 0.2f, -0.5f), Rnd(0.35f, 0.55f), 0.13f, 0.07f, bloodBright, bloodDark, 1f, 0.05f, 0.6f, delay: grab + t, rise: -3f, order: 9);
        }

        // 地面に落ちる影: 近付くほど大きく濃くなり、飛び去ると薄れる
        Vector2 ground = Off(c, 0f, Feet.y);
        Add(Shape.Glow, Off(ground, 1.4f, 0f), FxMath.V2(-1.4f / dive, 0f), dive, 0.8f, 2.4f, VultureShadow, VultureShadow, 0.8f, 0.6f, 1.1f, delay: omen, flat: true, order: 0);
        Add(Shape.Glow, ground, FxMath.V2(awayV.x * 0.5f, 0f), carry, 2.4f, 0.8f, VultureShadow, VultureShadow, 0.8f, 0.01f, 0.4f, delay: grab, flat: true, order: 0);

        // つかむ瞬間: 爪痕 3 本 (骨白の芯が体色の血へ)・暗赤の閃光と揺れ
        Impact(c, 3f, AwakenRed, 0.14f, 0.28f, 0.3f, delay: grab);

        for (int i = 0; i < 3; i++)
        {
            Vector2 o = Off(c, (i - 1) * 0.34f, (i - 1) * 0.25f + 0.1f);
            float d = grab - 0.04f + i * 0.035f;
            Add(Shape.Glow, o, Vector2.zero, 0.4f, 0.5f, 2.8f, AwakenRed, bloodDark, 0.6f, 0.03f, 0.4f, delay: d, rot: -35f, sy0: 0.35f, sy1: 0.6f, order: 8);
            Add(Shape.ClawSlash, o, Vector2.zero, 0.4f, 0.4f, 2.6f, BoneWhite, bloodBright, 1f, 0.02f, 0.45f, delay: d, rot: -35f, sy0: 0.3f, sy1: 0.55f, order: 9);
        }

        // 血しぶき (体の色)・床の砂埃
        for (int i = 0; i < 18 * q; i++)
        {
            Vector2 d = Dir();
            Add(Shape.Glow, Off(c, 0f, 0.1f), FxMath.V2(d.x * Rnd(1f, 2.6f) + awayV.x * 0.15f, FxMath.Abs(d.y) * Rnd(1f, 2.6f)), Rnd(0.3f, 0.45f), 0.15f, 0.06f, i % 2 == 0 ? bloodBright : blood, bloodDark,
                1f, 0.05f, 0.5f, drag: 1.5f, delay: grab, rise: -2f, order: 9);
        }

        for (int i = 0; i < 10 * q; i++)
        {
            Vector2 d = Dir();
            Add(Shape.Cloud, Off(ground, d.x * 0.3f, 0f), FxMath.V2(d.x * Rnd(2f, 4f), d.y * 0.5f), Rnd(0.6f, 0.9f), 0.4f, Rnd(1f, 1.4f), Dust, DustDark, 0.6f, 0.05f, 0.5f, drag: 2.5f, delay: grab);
        }

        // 羽根: つかむ瞬間に弾け、その後ゆっくり揺れて舞い落ちる (後ろに赤いリム)
        for (int i = 0; i < 16 * q; i++)
        {
            Vector2 d = Dir();
            var v = FxMath.V2(d.x * Rnd(2f, 4f) + awayV.x * 0.2f, d.y * Rnd(1.5f, 3f) + 1f);
            float rot = FxMath.Atan2(v.y, v.x) * FxMath.Rad2Deg;
            float len = Rnd(0.4f, 0.6f);
            Add(Shape.Feather, low, v, 0.35f, len * 1.1f, len * 1.1f, VultureRim, AwakenRed, 0.35f, 0.02f, 1.1f, drag: 4f, delay: grab, rot: rot, sy0: 0.17f, sy1: 0.17f, order: 8);
            Add(Shape.Feather, low, v, 0.35f, len, len, FeatherDark, FeatherBrown, 1f, 0.02f, 1.1f, drag: 4f, delay: grab, rot: rot, sy0: 0.12f, sy1: 0.12f, order: 9);
            float k = (1f - FxMath.Exp(-4f * 0.35f)) / 4f;
            Vector2 at = FxMath.V2(low.x + v.x * k, low.y + v.y * k);
            Add(Shape.Feather, at, FxMath.V2(Rnd(-0.4f, 0.4f), -0.45f), Rnd(1f, 1.5f), len, len, FeatherDark, FeatherBrown, 1f, 0.001f, 0.6f, delay: grab + 0.35f, rot: rot, spin: Rnd(-140f, 140f),
                sy0: 0.12f, sy1: 0.12f, order: 9);
        }
    }

    // ── 生きた演出 (第 5 弾 v6) ──────────────────────────────────────────

    // 流れる粒の emitter: 輪の円周を走る光点・中心へ吸い込まれる粒・輪の上の放電を、演出の尺いっぱい足し続ける
    private struct FlowEmitter
    {
        public Vector2 Pos;
        public float Until;
        public float Next;
        public float NextZap;
        public float Radius;
        public float Dir;
        public Color Light;
        public Color Main;
        public bool Motes;
        public bool Inward;
        public bool Zap;
        public bool Flat;
        public int Count;
    }

    private static readonly List<FlowEmitter> FlowEmitters = [];

    private static void StartFlow(Vector2 c, float seconds, float radius, float dir, Color light, Color main, bool motes, bool inward, bool zap, bool flat = false)
    {
        if (FlowEmitters.Count >= 6) FlowEmitters.RemoveAt(0);
        FlowEmitters.Add(new FlowEmitter { Pos = c, Until = Time.time + seconds, Radius = radius, Dir = dir, Light = light, Main = main, Motes = motes, Inward = inward, Zap = zap, Flat = flat });
    }

    private static void PulseFlows()
    {
        float now = Time.time;

        for (int i = FlowEmitters.Count - 1; i >= 0; i--)
        {
            FlowEmitter e = FlowEmitters[i];

            if (now >= e.Until)
            {
                FlowEmitters.RemoveAt(i);
                continue;
            }

            if (now < e.Next) continue;

            e.Next = now + 0.05f;
            e.Count++;
            bool thin = Active.Count > 1200;

            if (Active.Count < 2000)
            {
                float k = e.Flat ? 0.35f : 1f;
                Vector2 c = e.Pos;

                if (e.Motes)
                {
                    for (int m = 0; m < (thin ? 1 : 2); m++)
                    {
                        if (thin && e.Count % 2 == 0) break;

                        float a = Rnd(0f, 2f * FxMath.PI);
                        float sn = FxMath.Sin(a), cs = FxMath.Cos(a);
                        Add(Shape.Star, FxMath.V2(c.x + cs * e.Radius, c.y + sn * e.Radius * k), FxMath.V2(-sn * e.Dir * 3f, cs * k * e.Dir * 3f), 0.25f, 0.16f, 0.05f, e.Light, e.Main, 1f, 0.15f, 0.6f,
                            stretch: 0.08f, order: 9);
                    }
                }

                if (e.Inward && !(thin && e.Count % 2 == 0))
                {
                    Vector2 d = Dir();
                    float dist = e.Radius * Rnd(1f, 1.4f);
                    float speed = Rnd(2.2f, 3.4f);
                    float life = dist / speed;
                    Add(Shape.Star, FxMath.V2(c.x + d.x * dist, c.y + d.y * dist * k), FxMath.V2(-d.x * speed, -d.y * speed * k), life, Rnd(0.1f, 0.18f), 0.03f, e.Light, e.Main, 1f, 0.25f, 0.75f,
                        stretch: 0.06f, order: 8);
                }

                if (e.Zap && now >= e.NextZap && !thin)
                {
                    e.NextZap = now + Rnd(0.15f, 0.3f);
                    Zap(c, e.Radius, k, e.Light, e.Main);
                }
            }

            FlowEmitters[i] = e;
        }
    }

    // 放電: 輪の上の 2 点を結ぶ短いジグザグ (芯は白、主色のにじみ)
    private static void Zap(Vector2 c, float r, float k, Color light, Color main)
    {
        float a1 = Rnd(0f, 2f * FxMath.PI);
        float a2 = a1 + Rnd(1f, 2.6f) * (FxMath.Value < 0.5f ? -1f : 1f);
        Vector2 p1 = FxMath.V2(c.x + FxMath.Cos(a1) * r, c.y + FxMath.Sin(a1) * r * k);
        Vector2 p2 = FxMath.V2(c.x + FxMath.Cos(a2) * r, c.y + FxMath.Sin(a2) * r * k);
        Vector2 q1 = FxMath.V2(p1.x + (p2.x - p1.x) / 3f + Rnd(-0.15f, 0.15f) * r, p1.y + (p2.y - p1.y) / 3f + Rnd(-0.15f, 0.15f) * r);
        Vector2 q2 = FxMath.V2(p1.x + (p2.x - p1.x) * 2f / 3f + Rnd(-0.15f, 0.15f) * r, p1.y + (p2.y - p1.y) * 2f / 3f + Rnd(-0.15f, 0.15f) * r);

        Seg(p1, q1, 0.04f, 0.1f, main, main, 0.5f, 0.05f, 0.5f, 0f, 0f, 8);
        Seg(q1, q2, 0.04f, 0.1f, main, main, 0.5f, 0.05f, 0.5f, 0f, 0f, 8);
        Seg(q2, p2, 0.04f, 0.1f, main, main, 0.5f, 0.05f, 0.5f, 0f, 0f, 8);
        Seg(p1, q1, 0.012f, 0.1f, WindWhite, light, 1f, 0.05f, 0.5f, 0f, 0f, 9);
        Seg(q1, q2, 0.012f, 0.1f, WindWhite, light, 1f, 0.05f, 0.5f, 0f, 0f, 9);
        Seg(q2, p2, 0.012f, 0.1f, WindWhite, light, 1f, 0.05f, 0.5f, 0f, 0f, 9);
    }

    // 砂の帯: 被害者の胸から頭上の砂時計へ 0.9 秒のあいだ砂粒を流し続ける
    private struct SandEmitter
    {
        public Vector2 Pos;
        public float Until;
        public float Next;
        public int N;
        public PlayerControl To;
    }

    private static readonly List<SandEmitter> SandEmitters = [];

    private static void PulseSands()
    {
        float now = Time.time;

        for (int i = SandEmitters.Count - 1; i >= 0; i--)
        {
            SandEmitter e = SandEmitters[i];

            Vector2 to = e.To ? Off(e.To.Pos(), 0f, 0.05f) : Off(e.Pos, 0f, 1.1f);

            if (now >= e.Until)
            {
                SandEmitters.RemoveAt(i);
                if (Active.Count < 2000 && GameStates.IsInTask) SpawnSandArrive(to, e.To);
                continue;
            }

            if (now < e.Next) continue;

            e.Next = now + 0.05f;

            if (Active.Count < 2000)
            {
                int n = Active.Count > 1200 ? 1 : 2;

                for (int k = 0; k < n; k++)
                {
                    SandGrain(e.Pos, to, e.N);
                    e.N++;
                }
            }

            SandEmitters[i] = e;
        }
    }

    // 砂粒 1 つ: 被害者の体から浮き上がり、弧を描いて行き先へ吸い込まれる (3 区間の折れ線でたどる)
    private static void SandGrain(Vector2 c, Vector2 e, int n)
    {
        const float seg = 0.2f;
        Vector2 s = Off(c, Rnd(-0.22f, 0.22f), Rnd(-0.35f, 0.3f));
        float side = (n % 2 == 0 ? 1f : -1f) * Rnd(0.2f, 0.45f);
        Vector2 m = FxMath.V2((s.x + e.x) * 0.5f + side, (s.y + e.y) * 0.5f + Rnd(0.5f, 0.9f));
        float size = Rnd(0.06f, 0.11f);
        Vector2 prev = s;

        for (int j = 0; j < 3; j++)
        {
            float t = (j + 1) / 3f, u = 1f - t;
            Vector2 nx = j == 2 ? e : FxMath.V2(u * u * s.x + 2f * u * t * m.x + t * t * e.x, u * u * s.y + 2f * u * t * m.y + t * t * e.y);
            Vector2 v = FxMath.V2((nx.x - prev.x) / seg, (nx.y - prev.y) / seg);
            float d = j * seg;
            Add(Shape.Star, prev, v, seg, size, size * 0.7f, SandCore, SandLight, 1f, j == 0 ? 0.15f : 0.01f, j == 2 ? 0.6f : 1f, delay: d, twinkle: 0.4f, twinkleSpeed: 15f, order: 8);
            if (n % 2 == 0) Add(Shape.Glow, prev, v, seg, size * 2.6f, size * 1.8f, SandDark, SandDark, 0.45f, j == 0 ? 0.15f : 0.01f, j == 2 ? 0.6f : 1f, delay: d, order: 0);
            prev = nx;
        }
    }

    // 盗んだ時間が体に収まる: 金の閃光・横一文字の光条・足元の小さな輪
    private static void SpawnSandArrive(Vector2 p, PlayerControl to)
    {
        Add(Shape.Glow, p, Vector2.zero, 0.3f, 0.25f, 1f, WindWhite, SandLight, 0.9f, 0.03f, 0.3f, order: 7);
        Add(Shape.Glow, p, Vector2.zero, 0.3f, 0.5f, 2.2f, WindWhite, SandCore, 0.9f, 0.03f, 0.3f, rot: 0f, sy0: 0.06f, sy1: 0.025f, order: 8);
        Add(Shape.Star, p, Vector2.zero, 0.3f, 0.6f, 0.15f, WindWhite, SandLight, 1f, 0.02f, 0.3f, spin: 120f, order: 9);
        if (to) Add(Shape.Ring, Off(p, 0f, Feet.y - 0.05f), Vector2.zero, 0.4f, 0.3f, 1.1f, SandLight, SandDark, 0.7f, 0.03f, 0.4f, rot: 0f, sy0: 0.1f, sy1: 0.38f);

        for (int i = 0; i < 14; i++)
            Add(Shape.Star, p, Dir() * Rnd(0.8f, 2.2f), Rnd(0.3f, 0.5f), Rnd(0.04f, 0.08f), 0.02f, i % 3 == 0 ? WindWhite : SandCore, SandDark, 1f, 0.02f, 0.5f, drag: 3f, twinkle: 0.5f, twinkleSpeed: 18f, order: 8);
    }

    // 巻き戻し: 全体がセピアに沈み、頭上で歯車の輪が逆回りし、外の大きな輪がゆっくり逆へ回る。金の粒が時計へ逆流する
    // 光る模様 1 枚: にじみ版 (glow 色・既定で本体の 6 割の濃さ・ちらつく) の上に本体を重ねる
    private static void Lit(Shape tex, Shape halo, Vector2 p, float life, float s0, float s1, Color glow, Color c0, Color c1, float alpha, float fadeIn, float fadeOutFrom,
                            float delay = 0f, float rot = 0f, float spin = 0f, float twinkle = 0.1f, float wobble = 0f, float wobbleHz = 5f, bool flat = false, int order = 3, float haloK = 0.6f)
    {
        Add(halo, p, Vector2.zero, life, s0, s1, glow, glow, alpha * haloK, fadeIn, fadeOutFrom, delay: delay, rot: rot, spin: spin, twinkle: 0.3f, twinkleSpeed: 7f,
            wobble: wobble, wobbleHz: wobbleHz, flat: flat, order: order - 1);
        Add(tex, p, Vector2.zero, life, s0, s1, c0, c1, alpha, fadeIn, fadeOutFrom, delay: delay, rot: rot, spin: spin, twinkle: twinkle, twinkleSpeed: 5f,
            wobble: wobble, wobbleHz: wobbleHz, flat: flat, order: order);
    }

    private static readonly Color ClockPale = new(1f, 0.95f, 0.8f);
    private static readonly Color RewindDark = new(0.25f, 0.16f, 0.06f);

    // 巻き戻し: 画面に上へ流れる走査ノイズの帯、頭上に細密な時計盤が速く回りながら描かれてから逆回りを続け、
    // 針は残像を引き、足元から時計へ楕円の輪が昇り、床から逆さに金の雨が降る。最後は時計が縮んで弾ける
    private static void SpawnTimeRewind(Vector2 c, float seconds)
    {
        float d = FxMath.Clamp(seconds, 0.8f, 180f);
        const float draw = 0.3f;
        const float close = 0.35f;
        float body = d - draw - close;
        float q = Active.Count > 1200 ? 0.5f : 1f;
        const float size = 2.8f;
        const float spinDraw = -700f, spinBody = -40f;
        Vector2 clock = Off(c, 0f, 2.3f);
        float rot0 = Rnd(0f, 360f);
        float rotBody = rot0 + spinDraw * draw;
        float rotClose = rotBody + spinBody * body;

        // 画面: 薄いセピアと、描き終わりの金の閃光
        Add(Shape.Solid, c, Vector2.zero, d, 1f, 1f, Sepia, Sepia, 0.13f, FxMath.Min(0.3f / d, 0.5f), 1f - 0.4f / d, followCamera: true);
        Impact(c, 4f, TimeGold, 0.4f, 0.2f, 0.4f, delay: draw);

        // 巻き戻しの走査ノイズ: 画面の横帯が上へ流れてちらつく
        int bands = (int)(FxMath.Min(30f, d / 0.1f) * q);
        for (int i = 0; i < bands; i++)
        {
            bool thick = i % 4 == 3;
            bool dark = i % 3 == 1;
            float th = thick ? Rnd(0.12f, 0.3f) : Rnd(0.015f, 0.05f);
            float al = thick ? Rnd(0.12f, 0.18f) : dark ? Rnd(0.25f, 0.35f) : Rnd(0.3f, 0.5f);
            Color col = dark ? RewindDark : ClockPale;
            Add(Shape.Solid, FxMath.V2(0f, Rnd(-3.8f, 2.4f)), FxMath.V2(0f, Rnd(2.5f, 4.5f)), Rnd(0.35f, 0.8f), 1f, 1f, col, col, al, 0.15f, 0.6f,
                delay: Rnd(0.05f, d - 0.5f), sy0: th, sy1: th, twinkle: 0.5f, twinkleSpeed: Rnd(20f, 32f), camBand: true);
        }

        // 明るい床でも金の線が沈まないよう、盤の後ろを暗く落としてから光のにじみを重ねる
        Add(Shape.Glow, clock, Vector2.zero, d, 4.6f, 4.6f, RewindDark, RewindDark, 0.55f, 0.1f / d, 1f - 0.3f / d, order: 0);
        // 下地のにじみと、外側を逆へ回る淡い大きな盤
        Add(Shape.Glow, clock, Vector2.zero, d, 4.2f, 4.2f, Sepia, TimeGold, 0.25f, 0.1f / d, 1f - 0.3f / d, twinkle: 0.25f, twinkleSpeed: 3f, wobble: 0.06f, wobbleHz: 1.5f, order: 0);
        Add(Shape.ClockFine, clock, Vector2.zero, d - close, 4.8f, 4.8f, TimeGold, Sepia, 0.16f, 0.2f, 0.85f, rot: rot0 + 7f, spin: 20f, twinkle: 0.2f, twinkleSpeed: 4f, order: 1);

        // 本体の盤: 描く間は速く回りながら広がり、その後ゆっくり逆回り。にじみ版を下に重ねて光らせる
        Add(Shape.ClockFineHalo, clock, Vector2.zero, draw, size * 0.82f, size, WindWhite, TimeGold, 0.6f, 0.3f, 1.1f, rot: rot0, spin: spinDraw, order: 2);
        Add(Shape.ClockFine, clock, Vector2.zero, draw, size * 0.82f, size, WindWhite, ClockPale, 1f, 0.3f, 1.1f, rot: rot0, spin: spinDraw, order: 3);
        Add(Shape.ClockFineHalo, clock, Vector2.zero, body, size, size, TimeGold, TimeGold, 0.5f, 0.001f, 1.1f, delay: draw, rot: rotBody, spin: spinBody,
            twinkle: 0.3f, twinkleSpeed: 7f, wobble: 0.02f, wobbleHz: 5f, order: 2);
        Add(Shape.ClockFine, clock, Vector2.zero, body, size, size, ClockPale, TimeGold, 0.85f, 0.001f, 1.1f, delay: draw, rot: rotBody, spin: spinBody,
            twinkle: 0.1f, twinkleSpeed: 5f, wobble: 0.02f, wobbleHz: 5f, order: 3);

        // 針は反時計回り。後ろに残像を 4 本引く
        float handLife = d - draw - close * 0.5f;
        for (int k = 0; k < 5; k++)
        {
            float al = k == 0 ? 1f : 0.5f / k;
            Color h0 = k == 0 ? WindWhite : TimeGold;
            Add(Shape.Ray, clock, Vector2.zero, handLife, 1f, 1f, h0, TimeGold, al, 0.1f, 0.85f, delay: draw * 0.5f, rot: 100f + k * 10f, spin: -540f, sy0: 0.07f, sy1: 0.07f, order: 5);
            Add(Shape.Ray, clock, Vector2.zero, handLife, 0.65f, 0.65f, h0, TimeGold, al, 0.1f, 0.85f, delay: draw * 0.5f, rot: 20f + k * 5f, spin: -240f, sy0: 0.1f, sy1: 0.1f, order: 5);
        }

        // 中心の白い核
        Add(Shape.Glow, clock, Vector2.zero, d - close, 0.2f, 0.7f, WindWhite, TimeGold, 1f, 0.1f, 0.9f, twinkle: 0.35f, twinkleSpeed: 18f, order: 6);
        Add(Shape.Star, clock, Vector2.zero, d - close, 0.2f, 0.55f, WindWhite, TimeGold, 0.9f, 0.1f, 0.9f, spin: 90f, order: 7);

        // 盤の周りで瞬く極小の火花
        for (int i = 0; i < 50 * q; i++)
        {
            Vector2 dir = Dir();
            float r = Rnd(0.3f, 1.7f);
            Add(Shape.Star, Off(clock, dir.x * r, dir.y * r), Dir() * 0.2f, Rnd(0.4f, 0.9f), Rnd(0.04f, 0.08f), 0.02f, i % 3 == 0 ? WindWhite : TimeGold, TimeGold, 1f, 0.2f, 0.6f,
                delay: Rnd(draw, d - close - 0.3f), twinkle: 0.7f, twinkleSpeed: Rnd(12f, 25f), order: 8);
        }

        StartFlow(clock, d - close, 1.45f, -1f, TimeGold, Sepia, true, true, false);

        // 時間の柱: 足元から時計へ細い光と、昇りながら縮む楕円の輪
        Vector2 f = Off(c, 0f, Feet.y);
        Add(Shape.Glow, Off(c, 0f, 1f), Vector2.zero, d - close, 0.6f, 0.9f, TimeGold, Sepia, 0.2f, 0.1f, 0.85f, rot: 0f, sy0: 3.2f, sy1: 3.4f, twinkle: 0.3f, twinkleSpeed: 8f, order: 0);
        int rings = FxMath.Min(20, (int)((d - draw - close) / 0.22f));
        for (int i = 0; i < rings; i++)
        {
            Add(Shape.Ring, f, FxMath.V2(0f, 2.65f), 1f, 1.3f, 0.8f, TimeGold, WindWhite, 0.7f, 0.15f, 0.6f, delay: draw + i * 0.22f, rot: 0f, sy0: 0.45f, sy1: 0.28f, order: 2);
        }

        // 逆さの雨: 床から上へ抜けていく金の細い筋
        int rain = (int)(FxMath.Min(70f, d * 20f) * q);
        for (int i = 0; i < rain; i++)
        {
            float len = Rnd(0.5f, 1.1f);
            float w = Rnd(0.04f, 0.07f);
            Add(Shape.Ray, Off(c, Rnd(-3f, 3f), Rnd(-2.5f, 1.5f)), FxMath.V2(0f, Rnd(3f, 5f)), Rnd(0.35f, 0.6f), len, len, WindWhite, TimeGold, Rnd(0.75f, 1f), 0.2f, 0.6f,
                delay: Rnd(draw, d - 0.5f), rot: 270f, sy0: w, sy1: w, twinkle: 0.3f, twinkleSpeed: 20f, order: 6);
        }

        // 締め: 時計が回りながら少し縮んで白く溶け、弾ける
        float end = d - close;
        Add(Shape.ClockFineHalo, clock, Vector2.zero, close, size, size * 0.8f, TimeGold, WindWhite, 0.5f, 0.001f, 0.3f, delay: end, rot: rotClose, spin: -900f, order: 2);
        Add(Shape.ClockFine, clock, Vector2.zero, close, size, size * 0.8f, TimeGold, WindWhite, 0.85f, 0.001f, 0.3f, delay: end, rot: rotClose, spin: -900f, order: 3);
        Add(Shape.Glow, clock, Vector2.zero, 0.3f, 0.5f, 3f, WindWhite, TimeGold, 0.9f, 0.05f, 0.3f, delay: d - 0.1f, order: 7);
        Add(Shape.Ring, clock, Vector2.zero, 0.4f, 0.4f, 3.2f, WindWhite, TimeGold, 0.8f, 0.03f, 0.3f, delay: d - 0.1f, order: 5);
        for (int i = 0; i < 30 * q; i++)
        {
            Add(Shape.Star, clock, Dir() * Rnd(2f, 5f), Rnd(0.35f, 0.55f), Rnd(0.06f, 0.12f), 0.02f, WindWhite, TimeGold, 1f, 0.02f, 0.5f, drag: 3f, delay: d - 0.1f,
                stretch: 0.05f, twinkle: 0.5f, twinkleSpeed: 18f, order: 8);
        }
    }

    private static readonly Color ChronoHot = new(1f, 0.78f, 0.72f);
    private static readonly Color ChronoEmber = new(1f, 0.4f, 0.3f);

    // ベントの中や透明化で姿の見えない人
    private static bool Unseen(PlayerControl pc)
    {
        return pc.inVent || !pc.Visible || Main.Invisible.Contains(pc.PlayerId) || pc.invisibilityAlpha < 0.9f;
    }

    private static PlayerControl NearestPlayer(Vector2 c)
    {
        PlayerControl best = null;
        float bd = 0.64f;

        foreach (PlayerControl pc in Main.AllAlivePlayerControls)
        {
            // 切断で破棄された人が一覧に残っていることがある。姿の見えない人を拾うと演出がその居場所を指してしまう
            if (!pc || Unseen(pc)) continue;

            Vector2 p = pc.Pos();
            float dx = p.x - c.x, dy = p.y - c.y;
            float d = dx * dx + dy * dy;
            if (d >= bd) continue;

            bd = d;
            best = pc;
        }

        return best;
    }

    // 体の横を這い上がる細い放電 1 本 (枝 1 本・先端に火花)
    private static void MicroBolt(Vector2 p, float side, Color outer, Color core, float delay)
    {
        int segs = FxMath.Range(5, 7);

        for (int k = 0; k < segs; k++)
        {
            Vector2 n = Off(p, (k % 2 == 0 ? 1f : -0.6f) * side * Rnd(0.04f, 0.08f), Rnd(0.05f, 0.08f));
            Line2(p, n, 0.012f, 0.12f, outer, core, 1f, 0.1f, 0.5f, delay: delay + k * 0.01f);
            if (k == 2) Line2(n, Off(n, side * Rnd(0.06f, 0.1f), Rnd(-0.02f, 0.04f)), 0.009f, 0.1f, outer, core, 0.8f, 0.1f, 0.5f, delay: delay + 0.03f);
            p = n;
        }

        Add(Shape.Star, p, Vector2.zero, 0.14f, 0.14f, 0.04f, WindWhite, core, 1f, 0.05f, 0.4f, delay: delay + segs * 0.01f);
    }

    // 暴走の始まり: 体の奥で闇が脈打ち、深紅の粒と早回しの針が体へ集まる → 白い核と横一文字の光条が走って時間がひび割れ、
    // ぶれた残像が左右へ滑り、細い放電と火の粉が体の周りに残る
    private static void SpawnChronoRampage(Vector2 c)
    {
        const float charge = 0.3f;
        const float hold = 0.9f;
        const float total = charge + hold;
        float q = Active.Count > 1200 ? 0.5f : 1f;

        PlayerControl who = NearestPlayer(c);
        float zb = who ? who.transform.position.z + BackZ : 0f;
        int bk = who ? ReadBodyOrder(who) : int.MinValue;
        Vector2 f = Off(c, 0f, Feet.y);

        // 体の奥: 闇 → 深紅のにじみ → 熱い芯
        Add(Shape.Glow, c, Vector2.zero, total, 2.4f, 2.4f, ClockVoid, ClockVoid, 0.75f, 0.15f, 0.8f, z: zb + 0.004f, absOrder: bk);
        Add(Shape.Glow, c, Vector2.zero, total, 1.5f, 2f, ClockCrimson, ClockCrimson, 0.8f, 0.15f, 0.75f, twinkle: 0.3f, twinkleSpeed: 4f, z: zb + 0.003f, absOrder: bk);
        Add(Shape.Glow, c, Vector2.zero, total, 0.8f, 1.1f, ChronoHot, ClockCrimson, 0.8f, 0.2f, 0.7f, twinkle: 0.4f, twinkleSpeed: 18f, z: zb + 0.002f, absOrder: bk);

        // 溜め: 深紅の粒が体へ吸い込まれる
        for (int i = 0; i < 24 * q; i++)
        {
            Vector2 d = Dir();
            float rr = Rnd(1.1f, 1.8f);
            Vector2 start = Off(c, d.x * rr, d.y * rr);
            Add(Shape.Star, start, FxMath.V2((c.x - start.x) / charge, (c.y - start.y) / charge), charge, 0.16f, 0.05f, ChronoHot, ClockCrimson, 1f, 0.15f, 0.8f, stretch: 0.08f);
        }

        // 早回しの針 2 本 (残像 3 本ずつ)
        for (int k = 0; k < 4; k++)
        {
            float al = k == 0 ? 1f : 0.45f / k;
            Color h0 = k == 0 ? WindWhite : ClockCrimson;
            Add(Shape.Ray, c, Vector2.zero, total, 0.55f, 0.55f, h0, ClockCrimson, al, 0.1f, 0.7f, rot: 100f - k * 16f, spin: 1440f, sy0: 0.03f, sy1: 0.03f, order: 5);
            Add(Shape.Ray, c, Vector2.zero, total, 0.36f, 0.36f, h0, ClockCrimson, al, 0.1f, 0.7f, rot: 20f - k * 9f, spin: 600f, sy0: 0.045f, sy1: 0.045f, order: 5);
        }

        // 山場: 白い核・横一文字の光条・細い縦の光・床を走る輪
        Impact(c, 4f, ClockCrimson, 0.12f, 0.12f, 0.25f, delay: charge);
        Add(Shape.Glow, c, Vector2.zero, 0.3f, 0.3f, 1.5f, WindWhite, ClockCrimson, 1f, 0.02f, 0.3f, delay: charge, order: 7);
        Add(Shape.Glow, c, Vector2.zero, 0.4f, 1.2f, 3.2f, ClockCrimson, ClockDeep, 0.35f, 0.05f, 0.3f, delay: charge, rot: 0f, sy0: 0.4f, sy1: 0.25f, order: 6);
        Add(Shape.Glow, c, Vector2.zero, 0.35f, 0.6f, 3.4f, WindWhite, ChronoHot, 0.95f, 0.03f, 0.3f, delay: charge, rot: 0f, sy0: 0.07f, sy1: 0.03f, order: 8);
        Add(Shape.Glow, c, Vector2.zero, 0.25f, 0.08f, 0.05f, WindWhite, ChronoHot, 0.8f, 0.03f, 0.3f, delay: charge, rot: 0f, sy0: 0.8f, sy1: 1.9f, order: 8);
        Add(Shape.Star, c, Vector2.zero, 0.3f, 0.9f, 0.25f, WindWhite, ChronoHot, 1f, 0.02f, 0.3f, delay: charge, spin: 120f, order: 9);
        Add(Shape.Ring, f, Vector2.zero, 0.45f, 0.4f, 1.8f, ClockCrimson, ClockDeep, 0.75f, 0.03f, 0.4f, delay: charge, rot: 0f, sy0: 0.14f, sy1: 0.63f, z: zb + 0.003f, absOrder: bk);

        // 時間のひび: 体の縁から外へ走る細い亀裂 (奇数本目は枝付き)
        for (int k = 0; k < 7; k++)
        {
            float ang = (k * 51.4f + Rnd(-12f, 12f)) * FxMath.Deg2Rad;
            float len = Rnd(0.4f, 0.75f);
            float life = Rnd(0.45f, 0.75f);
            float dl = charge + k * 0.015f;
            Vector2 p = Off(c, FxMath.Cos(ang) * 0.3f, FxMath.Sin(ang) * 0.35f);

            for (int j = 1; j <= 3; j++)
            {
                float bend = ang + Rnd(-0.5f, 0.5f);
                Vector2 n = Off(p, FxMath.Cos(bend) * len / 3f, FxMath.Sin(bend) * len / 3f);
                Line2(p, n, 0.012f, life, ClockDeep, ChronoHot, 1f, 0.03f, 0.5f, delay: dl + j * 0.02f, coreFadeFrom: 0.3f);

                if (j == 2 && k % 2 == 1)
                {
                    float ba = ang + (FxMath.Value < 0.5f ? -0.9f : 0.9f);
                    Line2(n, Off(n, FxMath.Cos(ba) * len * 0.3f, FxMath.Sin(ba) * len * 0.3f), 0.009f, life, ClockDeep, ChronoHot, 0.8f, 0.03f, 0.5f, delay: dl + 0.06f, coreFadeFrom: 0.3f);
                }

                p = n;
            }
        }

        // ぶれた残像が左右へ滑って消える
        for (int k = 1; k <= 3; k++)
        {
            for (int sgn = -1; sgn <= 1; sgn += 2)
            {
                Add(Shape.CrewSil, c, FxMath.V2(sgn * (0.6f + 0.5f * k), 0f), 0.45f, 0.95f, 0.95f, ClockCrimson, ClockDeep, 0.6f / k, 0.05f, 0.3f, drag: 3f, delay: charge, rot: 0f, order: 4);
            }
        }

        // 砕けた時間の細片と極小の火花
        for (int i = 0; i < 16 * q; i++)
        {
            Add(Shape.Solid, c, Dir() * Rnd(1.5f, 3f), Rnd(0.4f, 0.6f), 0.14f, 0.06f, WindWhite, ClockCrimson, 1f, 0.02f, 0.5f, drag: 3f, delay: charge,
                spin: Rnd(-600f, 600f), sy0: 0.03f, sy1: 0.02f);
        }

        for (int i = 0; i < 40 * q; i++)
        {
            Add(Shape.Star, c, Dir() * Rnd(1f, 3.5f), Rnd(0.3f, 0.55f), Rnd(0.04f, 0.08f), 0.02f, i % 3 == 0 ? WindWhite : ChronoEmber, ClockDeep, 1f, 0.02f, 0.5f, drag: 3f, delay: charge,
                stretch: 0.05f, twinkle: 0.5f, twinkleSpeed: 20f, order: 8);
        }

        // 余韻: 体の横を這う放電と、昇る火の粉
        for (int i = 0; i < 7; i++)
        {
            float side = i % 2 == 0 ? -1f : 1f;
            MicroBolt(Off(c, side * Rnd(0.4f, 0.5f), Rnd(-0.35f, 0.1f)), side, ClockDeep, ChronoHot, charge + 0.08f + i * 0.11f);
        }

        for (int i = 0; i < 20 * q; i++)
        {
            float s = Rnd(0.05f, 0.1f);
            Add(Shape.Star, Off(c, (FxMath.Value < 0.5f ? -1f : 1f) * Rnd(0.3f, 0.6f), Rnd(-0.5f, 0.3f)), FxMath.V2(Rnd(-0.2f, 0.2f), Rnd(0.2f, 0.6f)), Rnd(0.5f, 0.8f), s, s * 0.4f, ChronoEmber, ClockCrimson, 1f, 0.1f, 0.6f,
                delay: charge + Rnd(0f, hold - 0.4f), rise: 0.8f, twinkle: 0.6f, twinkleSpeed: Rnd(12f, 20f));
        }

        // 時間が壊れる: 近くにいる人の画面に赤と黒の細い横帯が一瞬ずつ走る
        for (int i = 0; i < (NearCamera(c, 12f) ? 10 * q : 0); i++)
        {
            Color col = i % 2 == 0 ? ClockCrimson : ClockVoid;
            float th = i % 5 == 0 ? Rnd(0.1f, 0.2f) : Rnd(0.015f, 0.04f);
            Add(Shape.Solid, FxMath.V2(0f, Rnd(-3.6f, 3.6f)), FxMath.V2(0f, Rnd(-2f, 2f)), Rnd(0.08f, 0.18f), 1f, 1f, col, col, i % 5 == 0 ? 0.12f : 0.3f, 0.1f, 0.6f,
                delay: Rnd(charge, charge + 0.5f), sy0: th, sy1: th, twinkle: 0.6f, twinkleSpeed: 45f, camBand: true);
        }
    }

    // 蘇る: 光柱が立ち、足元の時計の魔法陣が速く回りながら描かれてゆっくり回り続け、光点が円周を走り、星が昇る
    private static void SpawnRewindRevive(Vector2 c)
    {
        Vector2 f = Off(c, 0f, Feet.y);
        float q = Active.Count > 1200 ? 0.5f : 1f;
        const float life = 1.6f;
        const float grow = 0.3f;
        float rot0 = Rnd(0f, 360f);

        // 下地のにじみと 3 本重ねの光柱
        Add(Shape.Glow, Off(c, 0f, 1.2f), Vector2.zero, life, 2.2f, 2.2f, TimeGold, SandDark, 0.4f, 0.1f, 0.5f, rot: 0f, sy0: 2.2f, sy1: 4f, twinkle: 0.25f, twinkleSpeed: 3f, wobble: 0.06f, wobbleHz: 1.5f, order: 0);
        Add(Shape.Glow, Off(c, 0f, 1.4f), Vector2.zero, life, 0.3f, 1.2f, TimeGold, SandDark, 0.3f, 0.1f, 0.5f, rot: 0f, sy0: 1f, sy1: 3.5f, twinkle: 0.3f, twinkleSpeed: 12f);
        Add(Shape.Glow, Off(c, 0f, 1.4f), Vector2.zero, life, 0.45f, 0.7f, TimeGold, SandDark, 0.6f, 0.1f, 0.5f, rot: 0f, sy0: 1f, sy1: 3.5f, twinkle: 0.3f, twinkleSpeed: 12f);
        Add(Shape.Glow, Off(c, 0f, 1.4f), Vector2.zero, life, 0.16f, 0.26f, WindWhite, TimeGold, 1f, 0.1f, 0.5f, rot: 0f, sy0: 1f, sy1: 3.5f, twinkle: 0.3f, twinkleSpeed: 12f);

        // 足元の時計の魔法陣 (楕円の床に寝かせる)。明るい床でも沈まないよう下を暗く落とす
        Add(Shape.Glow, f, Vector2.zero, life, 2.1f, 2.1f, RewindDark, RewindDark, 0.6f, 0.1f, 0.6f, flat: true, order: 0);
        Lit(Shape.ClockFine, Shape.ClockFineHalo, f, grow, 1.5f, 1.8f, TimeGold, WindWhite, ClockPale, 0.95f, 0.3f, 1.1f, rot: rot0, spin: 500f, flat: true);
        Lit(Shape.ClockFine, Shape.ClockFineHalo, f, life - grow, 1.8f, 1.8f, TimeGold, ClockPale, TimeGold, 0.9f, 0.01f, 0.5f, delay: grow, rot: rot0 + 500f * grow, spin: 40f, twinkle: 0.2f, flat: true);

        // 床から昇りながら縮む楕円の輪
        for (int i = 0; i < 5; i++)
            Add(Shape.Ring, f, FxMath.V2(0f, 2.4f), 1f, 1.1f, 0.5f, TimeGold, WindWhite, 0.65f, 0.15f, 0.6f, delay: grow + i * 0.2f, rot: 0f, sy0: 0.38f, sy1: 0.17f, order: 2);

        StartFlow(f, life, 0.75f, 1f, TimeGold, SandDark, true, true, false, true);

        // 螺旋に昇る金の星
        for (int i = 0; i < 30 * q; i++)
        {
            float a = Rnd(0f, 2f * FxMath.PI);
            Add(Shape.Star, Off(f, FxMath.Cos(a) * 0.55f, FxMath.Sin(a) * 0.2f), FxMath.V2(-FxMath.Sin(a) * 1.6f, FxMath.Cos(a) * 0.5f), Rnd(0.7f, 1f), Rnd(0.08f, 0.16f), 0.04f, TimeGold, WindWhite, 1f, 0.15f, 0.6f,
                drag: 1.5f, delay: Rnd(0f, 0.9f), rise: 1.8f, twinkle: 0.6f, twinkleSpeed: Rnd(12f, 20f));
        }

        // 最後の白い閃光
        Add(Shape.Glow, Off(c, 0f, 0.1f), Vector2.zero, 0.2f, 1f, 1.8f, WindWhite, TimeGold, 0.9f, 0.3f, 0.5f, delay: 1.4f);
        Add(Shape.Glow, Off(c, 0f, 0.1f), Vector2.zero, 0.3f, 0.6f, 3f, WindWhite, ClockPale, 0.95f, 0.1f, 0.3f, delay: 1.4f, rot: 0f, sy0: 0.06f, sy1: 0.025f, order: 8);

        for (int i = 0; i < 30 * q; i++)
        {
            Add(Shape.Star, Off(c, 0f, 0.1f), Dir() * Rnd(1f, 3f), Rnd(0.35f, 0.55f), Rnd(0.04f, 0.07f), 0.02f, i % 3 == 0 ? WindWhite : TimeGold, TimeGold, 1f, 0.02f, 0.5f, drag: 3f, delay: 1.4f,
                twinkle: 0.5f, twinkleSpeed: 18f, order: 8);
        }
    }

    private static readonly Color MistSpark = new(0.75f, 0.6f, 1f);

    // 呪印: 足元の闇から影の蔓が伸び、棘のある鎖が体を下から締め上げ、額に小さな印が白く焼き付き、鎖が締まって砕け紫の煙になる
    private static void SpawnHexMark(Vector2 c)
    {
        const float brand = 0.85f;
        const float crack = 1.6f;
        const float total = 2f;
        float q = Active.Count > 1200 ? 0.5f : 1f;
        Vector2 f = Off(c, 0f, Feet.y);
        Vector2 brow = Off(c, 0.06f, 0.3f);

        // 光の重ね: 闇 → 紫のにじみ。足元には闇の溜まり
        Add(Shape.Glow, c, Vector2.zero, total, 2.1f, 2.1f, HexVoid, HexVoid, 0.65f, 0.15f, 0.75f, order: 0);
        Add(Shape.Glow, c, Vector2.zero, total, 1.1f, 1.5f, HexPurple, HexDeep, 0.4f, 0.2f, 0.7f, twinkle: 0.3f, twinkleSpeed: 4f, wobble: 0.04f, wobbleHz: 1.5f, order: 0);
        Add(Shape.Glow, f, Vector2.zero, total, 0.6f, 1.5f, HexVoid, HexDeep, 0.8f, 0.2f, 0.7f, rot: 0f, sy0: 0.2f, sy1: 0.5f, order: 0);

        // 足元の闇から伸びる影の蔓
        for (int i = 0; i < 7; i++)
        {
            float x = -0.6f + i * 0.2f + Rnd(-0.05f, 0.05f);
            float lean = 90f - x * 45f + Rnd(-8f, 8f);
            float len = Rnd(0.5f, 0.9f);
            Add(Shape.Ray, Off(f, x, 0f), Vector2.zero, total - 0.3f - i * 0.03f, 0.1f, len, HexPurple, HexDeep, 0.8f, 0.2f, 0.7f, delay: i * 0.03f, rot: lean, sy0: 0.05f, sy1: 0.025f, wobble: 0.05f, wobbleHz: 3f, order: 1);
        }

        // 棘のある鎖が下から順に巻き付く (体の後ろ側は暗い)
        for (int i = 0; i < 9; i++)
        {
            float y = -0.45f + i * 0.1f;
            bool back = i % 2 == 1;
            float tilt = back ? -0.14f : 0.14f;
            float half = 0.44f - FxMath.Abs(y) * 0.15f;
            float dl = 0.15f + i * 0.06f;
            float life = crack - dl;
            Vector2 a = Off(c, -half, y), b = Off(c, half, y + tilt);
            Arc(a, b, FxMath.V2(0f, back ? 0.07f : -0.07f), 4, 0.016f, life, HexDeep, back ? HexPurple : HexLight, back ? 0.5f : 1f, 0.15f, 0.9f, dl);

            // 棘: 鎖の両端と中ほどから外へ
            if (!back)
            {
                Line2(a, Off(a, -0.09f, 0.05f), 0.012f, life, HexDeep, HexLight, 1f, 0.2f, 0.9f, dl + 0.05f);
                Line2(b, Off(b, 0.09f, 0.05f), 0.012f, life, HexDeep, HexLight, 1f, 0.2f, 0.9f, dl + 0.05f);
                Line2(Off(c, 0f, y + tilt * 0.5f - 0.07f), Off(c, Rnd(-0.03f, 0.03f), y + tilt * 0.5f - 0.15f), 0.012f, life, HexDeep, HexLight, 0.9f, 0.2f, 0.9f, dl + 0.05f);
            }

            // 鎖を描く先端の光
            Add(Shape.Star, a, FxMath.V2((b.x - a.x) / 0.12f, (b.y - a.y) / 0.12f), 0.12f, 0.14f, 0.08f, WindWhite, HexLight, 1f, 0.1f, 0.8f, delay: dl, order: 9);
        }

        // 体の周りを螺旋に昇る呪いの文字の欠片
        for (int i = 0; i < 26 * q; i++)
        {
            float a = Rnd(0f, 2f * FxMath.PI);
            float s = Rnd(0.05f, 0.1f);
            Add(Shape.Star, Off(c, FxMath.Cos(a) * 0.6f, -0.5f + FxMath.Sin(a) * 0.15f), FxMath.V2(-FxMath.Sin(a) * 1.4f, FxMath.Cos(a) * 0.35f), Rnd(0.6f, 0.9f), s, s * 0.4f, i % 3 == 0 ? WindWhite : HexLight, HexPurple, 1f, 0.15f, 0.6f,
                drag: 1.2f, delay: Rnd(0.1f, crack - 0.5f), rise: 1.3f, twinkle: 0.6f, twinkleSpeed: Rnd(12f, 22f), order: 8);
        }

        // 額に印が焼き付く: 白い閃光と横一文字の光条のあと、小さな印が残って瞬く
        Add(Shape.Glow, brow, Vector2.zero, 0.3f, 0.2f, 0.9f, WindWhite, HexLight, 1f, 0.03f, 0.3f, delay: brand, order: 9);
        Add(Shape.Glow, brow, Vector2.zero, 0.35f, 0.4f, 2.4f, WindWhite, HexLight, 0.95f, 0.03f, 0.3f, delay: brand, rot: 0f, sy0: 0.06f, sy1: 0.025f, order: 10);
        Add(Shape.Glow, brow, Vector2.zero, 0.25f, 0.06f, 0.04f, WindWhite, HexLight, 0.8f, 0.03f, 0.3f, delay: brand, rot: 0f, sy0: 0.5f, sy1: 1.2f, order: 10);
        Lit(Shape.HexSeal, Shape.HexSealHalo, brow, 0.15f, 0.7f, 0.4f, HexPurple, WindWhite, WindWhite, 1f, 0.2f, 1.1f, delay: brand, rot: 0f, spin: -400f, order: 11);
        Lit(Shape.HexSeal, Shape.HexSealHalo, brow, total - brand - 0.15f, 0.4f, 0.36f, HexPurple, WindWhite, HexLight, 1f, 0.001f, 0.7f, delay: brand + 0.15f, rot: -60f, spin: -25f, twinkle: 0.3f, order: 11);

        for (int i = 0; i < 12 * q; i++)
            Add(Shape.Star, brow, Dir() * Rnd(0.8f, 2f), Rnd(0.3f, 0.5f), Rnd(0.04f, 0.07f), 0.02f, WindWhite, HexLight, 1f, 0.02f, 0.5f, drag: 3f, delay: brand, twinkle: 0.5f, twinkleSpeed: 20f, order: 9);

        // 呪いが体を走る細い放電
        for (int i = 0; i < 4; i++)
        {
            float side = i % 2 == 0 ? -1f : 1f;
            MicroBolt(Off(c, side * Rnd(0.38f, 0.48f), Rnd(-0.35f, 0.05f)), side, HexDeep, HexLight, brand + 0.1f + i * 0.15f);
        }

        // 締まって砕ける: 鎖が一瞬白く光り、細片と紫の煙になって沈む
        Add(Shape.Glow, c, Vector2.zero, 0.2f, 1.1f, 0.8f, WindWhite, HexLight, 0.7f, 0.1f, 0.4f, delay: crack - 0.05f, rot: 0f, sy0: 1.3f, sy1: 1.1f, order: 7);

        for (int i = 0; i < 18 * q; i++)
        {
            Add(Shape.Solid, Off(c, Rnd(-0.35f, 0.35f), Rnd(-0.45f, 0.4f)), Dir() * Rnd(0.8f, 2f), Rnd(0.3f, 0.45f), 0.12f, 0.05f, HexLight, HexPurple, 1f, 0.02f, 0.5f, drag: 3f, delay: crack,
                spin: Rnd(-500f, 500f), sy0: 0.02f, sy1: 0.014f, order: 8);
        }

        for (int i = 0; i < 6 * q; i++)
            Add(Shape.Cloud, Off(c, Rnd(-0.35f, 0.35f), Rnd(-0.3f, 0.3f)), FxMath.V2(Rnd(-0.2f, 0.2f), -0.3f), 0.5f, 0.3f, Rnd(0.5f, 0.7f), HexPurple, HexDeep, 0.5f, 0.1f, 0.4f, delay: crack, spin: Rnd(-60f, 60f), order: 1);
    }

    // 蜘蛛の巣を張る: 中心から縦糸 12 本が撃ち出され、横糸が外から内へ一周ずつ描かれていく (描く点が光る)。
    // 描き終わると同じ形の巣の円盤に入れ替わって揺れ、露が瞬き、光が糸を伝って外へ走り、薄れて隠れる
    private static void SpawnWebSpin(Vector2 c, float radius)
    {
        float R = FxMath.Clamp(radius, 0.6f, 8f);
        Vector2 f = Off(c, 0f, Feet.y);
        float q = Active.Count > 1200 ? 0.5f : 1f;
        const float end = 1.8f;
        const float spokesEnd = 0.45f;
        const float drawn = 1.15f;
        const float swap = 0.3f;
        const int n = 12;
        float rot0 = Rnd(0f, 360f);
        float baseRad = rot0 * FxMath.Deg2Rad - FxMath.PI;

        Add(Shape.Glow, f, Vector2.zero, end, R * 1.3f, R * 1.3f, WebVoid, WebVoid, 0.5f, 0.1f, 0.6f, order: 0);

        // 縦糸: 巣の円盤と同じ角度 (円盤の回転 rot0 を足す)
        for (int k = 0; k < n; k++)
        {
            float ang = baseRad + k * 2f * FxMath.PI / n;
            Vector2 tip = Off(f, FxMath.Cos(ang) * R * 0.97f, FxMath.Sin(ang) * R * 0.97f);
            float d = k * 0.03f;
            Silk(f, tip, Vector2.zero, 3, drawn + swap - d, d, 0.12f, 1f, (drawn - d) / (drawn + swap - d), false);
        }

        // 横糸: 外の周から順に、縦糸の間をたわんだ弧でつないでいく
        int rings = 0;
        for (int j = 9; j >= 1; j--)
        {
            float rho = 0.08f + 0.098f * j + 0.002f * j * j;
            if (rho > 0.97f) continue;

            float ringAt = spokesEnd + rings * (drawn - spokesEnd) / 9f;
            rings++;

            for (int k = 0; k < n; k++)
            {
                float a0 = baseRad + k * 2f * FxMath.PI / n, a1 = baseRad + (k + 1) * 2f * FxMath.PI / n, am = (a0 + a1) * 0.5f;
                Vector2 p0 = Off(f, FxMath.Cos(a0) * rho * R, FxMath.Sin(a0) * rho * R);
                Vector2 p1 = Off(f, FxMath.Cos(a1) * rho * R, FxMath.Sin(a1) * rho * R);
                float chordMid = FxMath.Cos(FxMath.PI / n) * rho * R;
                float sagTo = rho * R * (1f - 0.06f);
                Vector2 bow = FxMath.V2(FxMath.Cos(am) * (sagTo - chordMid), FxMath.Sin(am) * (sagTo - chordMid));
                float d = ringAt + k * 0.075f / n;
                Silk(p0, p1, bow, 2, drawn + swap - d, d, 0f, 0.85f, (drawn - d) / (drawn + swap - d), false, 0.012f);
                if (k % 2 == 0) Add(Shape.Star, p1, Vector2.zero, 0.15f, 0.2f, 0.06f, WindWhite, SilkCyan, 1f, 0.05f, 0.4f, delay: d, order: 9);
            }
        }

        // 描き終わった巣を、同じ形の円盤 (にじみ付き) に入れ替えて揺らす
        Lit(Shape.WebDisc, Shape.WebDiscHalo, f, end - drawn, R * 2f, R * 2f, WebBloom, WebCore, WebMid, 0.55f, swap / (end - drawn), 0.65f, delay: drawn, rot: rot0, wobble: 0.02f, wobbleHz: 7f);

        // 巣の主: 中心へ糸で降りてきて、巣が張り終わると糸を登って隠れる
        const float spSize = 0.35f;
        Vector2 sp0 = Off(f, 0f, 1.2f), sp1 = Off(f, 0f, 0.12f);
        SpiderPart(sp0, FxMath.V2(0f, (0.12f - 1.2f) / spokesEnd), spokesEnd, spSize, 0f, 0.3f, 1f, 0.02f);
        SpiderPart(sp1, Vector2.zero, drawn - spokesEnd, spSize, spokesEnd, 0.01f, 1f, 0.05f);
        SpiderPart(sp1, FxMath.V2(0f, 1.6f / 0.35f), 0.35f, spSize, drawn, 0.01f, 0.4f, 0.02f);
        Add(Shape.Solid, Off(f, 0f, 0.9f), Vector2.zero, drawn + 0.3f, 0.012f, 0.012f, WindWhite, SilkCyan, 0.8f, 0.2f, 0.8f, rot: 0f, sy0: 1.6f, sy1: 1.6f, order: 8);

        // 中心から糸を伝って外へ走る光
        for (int w = 0; w < 2; w++)
        {
            for (int k = w; k < n; k += 3)
            {
                float rad = baseRad + k * 2f * FxMath.PI / n;
                Add(Shape.Star, f, FxMath.V2(FxMath.Cos(rad) * R / 0.4f, FxMath.Sin(rad) * R / 0.4f), 0.4f, 0.14f, 0.07f, WindWhite, SilkCyan, 1f, 0.05f, 0.7f, delay: drawn + 0.05f + w * 0.2f, order: 9);
            }
        }

        // 露が位相をずらして順に光る
        for (int i = 0; i < 24 * q; i++)
        {
            float rad = baseRad + FxMath.Range(0, n) * 2f * FxMath.PI / n;
            float rho = R * (0.08f + 0.098f * FxMath.Range(1, 10));
            float dl = drawn + Rnd(0f, 0.35f);
            Add(Shape.Star, Off(f, FxMath.Cos(rad) * rho, FxMath.Sin(rad) * rho), Vector2.zero, end - dl, Rnd(0.06f, 0.12f), 0.06f, WindWhite, SilkPink, 0.95f, 0.05f, 0.7f, delay: dl,
                twinkle: 0.7f, twinkleSpeed: Rnd(4f, 9f), order: 9);
        }
    }

    // 砂の行き先。番号の人が被害者のそばにいればその人 (姿が見えない間は居場所を指さないよう行き先なし)。
    // 番号を載せない版のホストからは 0 番が届くので、そばにいない時は最寄りの人で代える
    private static PlayerControl SandThief(Vector2 c, int thiefId)
    {
        PlayerControl pc = thiefId is >= 0 and <= 254 ? Utils.GetPlayerById((byte)thiefId) : null;
        if (!pc || !pc.IsAlive()) return NearestPlayer(c);

        Vector2 p = pc.Pos();
        float dx = p.x - c.x, dy = p.y - c.y;
        if (dx * dx + dy * dy > 9f) return NearestPlayer(c);

        return Unseen(pc) ? null : pc;
    }

    // 時間を盗む (約 1.5 秒): 被害者の影がセピアに固まって砂へ崩れ、周りの 12 の刻みが 1 つずつ引き抜かれ、
    // 金の砂が弧を描いて盗んだ者の体へ流れ込む (行き先が見つからなければ上へ昇って散る)
    private static void SpawnTimeSteal(Vector2 c, int thiefId)
    {
        float q = Active.Count > 1200 ? 0.5f : 1f;
        PlayerControl thief = SandThief(c, thiefId);
        SandEmitters.Add(new SandEmitter { Pos = c, Until = Time.time + 1.1f, Next = Time.time + 0.25f, To = thief });
        if (SandEmitters.Count > 6) SandEmitters.RemoveAt(0);

        Vector2 tp = thief ? Off(thief.Pos(), 0f, 0.05f) : Off(c, 0f, 1.1f);
        const float freeze = 0.3f;

        // 光の重ね: 暗い下地 → 金のにじみ → 白い核
        Add(Shape.Glow, c, Vector2.zero, 1.3f, 1.9f, 1.9f, RewindDark, RewindDark, 0.6f, 0.1f, 0.7f, order: 0);
        Add(Shape.Glow, c, Vector2.zero, 1.2f, 1.1f, 1.4f, SandLight, SandDark, 0.45f, 0.1f, 0.6f, twinkle: 0.3f, twinkleSpeed: 4f, order: 0);
        Add(Shape.Star, Off(c, 0f, 0.05f), Vector2.zero, 0.9f, 0.3f, 0.12f, WindWhite, SandCore, 1f, 0.1f, 0.6f, spin: 90f, twinkle: 0.4f, twinkleSpeed: 18f, order: 9);

        // 止まった一瞬: セピアの影が立ち、横一文字の光条が走る
        Add(Shape.CrewSil, c, Vector2.zero, freeze + 0.25f, 0.95f, 0.95f, SandCore, Sepia, 0.75f, 0.1f, 0.5f, rot: 0f, twinkle: 0.2f, twinkleSpeed: 30f, order: 4);
        Add(Shape.Glow, c, Vector2.zero, 0.3f, 0.5f, 2.6f, WindWhite, SandCore, 0.9f, 0.05f, 0.3f, rot: 0f, sy0: 0.06f, sy1: 0.025f, order: 8);

        // 影が砂になって崩れ落ちる
        for (int i = 0; i < 36 * q; i++)
        {
            float s = Rnd(0.05f, 0.1f);
            Add(Shape.Star, Off(c, Rnd(-0.3f, 0.3f), Rnd(-0.5f, 0.45f)), FxMath.V2(Rnd(-0.3f, 0.3f), Rnd(-0.2f, 0.3f)), Rnd(0.5f, 0.8f), s, s * 0.5f, i % 4 == 0 ? WindWhite : SandCore, SandDark, 1f, 0.05f, 0.5f,
                delay: freeze + Rnd(0f, 0.3f), rise: -2.2f, twinkle: 0.5f, twinkleSpeed: Rnd(12f, 22f), order: 6);
        }

        // 12 の刻み: 体の周りに現れ、時計回りに 1 つずつ引き抜かれて行き先へ飛ぶ
        for (int k = 0; k < 12; k++)
        {
            float ang = 90f - k * 30f;
            float rad = ang * FxMath.Deg2Rad;
            Vector2 at = Off(c, FxMath.Cos(rad) * 0.55f, FxMath.Sin(rad) * 0.6f);
            float pull = freeze + 0.1f + k * 0.055f;
            bool big = k % 3 == 0;
            float len = big ? 0.13f : 0.08f, w = big ? 0.022f : 0.014f;
            Add(Shape.Solid, at, Vector2.zero, pull - 0.05f, len, len, WindWhite, SandLight, 1f, 0.15f, 1f, delay: 0.05f, rot: ang, sy0: w, sy1: w, twinkle: 0.2f, twinkleSpeed: 9f, order: 7);
            Add(Shape.Solid, at, Vector2.zero, pull - 0.05f, len * 1.4f, len * 1.4f, SandLight, SandLight, 0.2f, 0.15f, 1f, delay: 0.05f, rot: ang, sy0: w * 2.5f, sy1: w * 2.5f, order: 6);
            Add(Shape.Solid, at, FxMath.V2((tp.x - at.x) / 0.3f, (tp.y - at.y) / 0.3f), 0.3f, len, len * 0.5f, WindWhite, SandCore, 1f, 0.01f, 0.7f, delay: pull, rot: ang, spin: 720f, sy0: w, sy1: w, order: 7);
            Add(Shape.Star, at, Vector2.zero, 0.15f, 0.16f, 0.04f, WindWhite, SandCore, 1f, 0.05f, 0.4f, delay: pull, order: 9);
        }

        // 抜け殻から立ちのぼる淡い金の靄
        for (int i = 0; i < 6 * q; i++)
            Add(Shape.Cloud, Off(c, Rnd(-0.25f, 0.25f), Rnd(-0.3f, 0.2f)), FxMath.V2(Rnd(-0.15f, 0.15f), 0.5f), 0.8f, 0.3f, 0.6f, SandDark, RewindDark, 0.35f, 0.2f, 0.5f, delay: freeze + i * 0.1f, spin: Rnd(-40f, 40f), order: 1);
    }

    // 迷彩の煙 (1.8 秒・1 人 40 粒以内): 足元で紫灰の煙が 1 周半渦を巻き、体を包んで立ち上り、中で光点が瞬き、最後に内側へ吸い込まれて消える
    private static void SpawnCamoMist(Vector2 c)
    {
        float q = Active.Count > 1200 ? 0.5f : 1f;
        Vector2 f = Off(c, 0f, Feet.y);
        const float seg = 0.3f;

        // 足元の渦: 雲 3 つが 90° ずつ 6 区間で 1 周半回る (床に寝かせた楕円)
        for (int k = 0; k < 3; k++)
        {
            float a0 = k * 2f * FxMath.PI / 3f;

            for (int j = 0; j < 6; j++)
            {
                float a1 = a0 + j * FxMath.PI / 2f, a2 = a1 + FxMath.PI / 2f;
                float r1 = 0.45f + j * 0.06f, r2 = 0.45f + (j + 1) * 0.06f;
                Vector2 p1 = Off(f, FxMath.Cos(a1) * r1, FxMath.Sin(a1) * r1 * 0.35f);
                Vector2 p2 = Off(f, FxMath.Cos(a2) * r2, FxMath.Sin(a2) * r2 * 0.35f);
                Add(Shape.Cloud, p1, FxMath.V2((p2.x - p1.x) / seg, (p2.y - p1.y) / seg), seg, 0.9f, 0.9f, SmokeGrey, MistViolet, 0.7f, j == 0 ? 0.3f : 0.001f, j == 5 ? 0.4f : 1.1f,
                    delay: j * seg, rot: 0f, sy0: 0.32f, sy1: 0.32f, order: 0);
            }
        }

        // 体を包んで立ち上る煙
        for (int i = 0; i < 6 * q; i++)
        {
            Vector2 d = Dir();
            Add(Shape.Cloud, Off(c, d.x * 0.35f, -0.3f + d.y * 0.15f), FxMath.V2(-d.y * 0.6f, 0f), 1.3f, 0.5f, 0.9f, SmokeGrey, MistViolet, 0.75f, 0.2f, 0.6f, delay: 0.15f + i * 0.08f,
                drag: 0.5f, rise: 0.55f, spin: Rnd(-40f, 40f));
        }

        // 煙の中で瞬く光点
        for (int i = 0; i < 10 * q; i++)
        {
            Add(Shape.Star, Off(c, Rnd(-0.5f, 0.5f), Rnd(-0.35f, 0.5f)), FxMath.V2(Rnd(-0.2f, 0.2f), Rnd(0f, 0.3f)), Rnd(0.5f, 0.8f), 0.12f, 0.05f, MistSpark, MistViolet, 1f, 0.15f, 0.6f,
                delay: Rnd(0.2f, 1.0f), twinkle: 0.7f, twinkleSpeed: Rnd(10f, 18f));
        }

        // 最後は内側へ縮みながら吸い込まれて消える
        Add(Shape.Ring, f, Vector2.zero, 0.5f, 1.6f, 0.3f, SmokeGrey, MistViolet, 0.7f, 0.1f, 0.5f, delay: 1.3f, rot: 0f, sy0: 0.56f, sy1: 0.105f);
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
                            float stretch = 0f, bool followCamera = false, int order = -1, Color? colorMid = null, float flap = 0f, float z = 0f, int absOrder = int.MinValue, float wobble = 0f, float wobbleHz = 5f, bool flat = false, bool camBand = false)
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

        // 床に寝かせる陣は、回転したあとに縦を潰す親の下に置く (回る模様が楕円の中に収まる)
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

        tf.position = FxMath.V3(pos.x, pos.y, z);
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
            Shape.HexSeal => _hexSeal,
            Shape.ClockFace => _clockFace,
            Shape.WebDisc => _webDisc,
            Shape.FuseRope => _fuseRope,
            Shape.Hourglass => _hourglass,
            Shape.ClockFine => _clockFine,
            Shape.ClockFineHalo => _clockFineHalo,
            Shape.HexSealHalo => _hexSealHalo,
            Shape.WebDiscHalo => _webDiscHalo,
            Shape.FuseRopeHalo => _fuseRopeHalo,
            Shape.HourglassHalo => _hourglassHalo,
            Shape.VultureSil => _vultureSil,
            Shape.VultureHalo => _vultureHalo,
            Shape.Feather => _feather,
            Shape.ClawSlash => _clawSlash,
            Shape.SpiderSil => _spiderSil,
            Shape.SpiderHalo => _spiderHalo,
            Shape.CrewSil => _crewSil,
            _ => _glow
        };
        // 奥から (order=0 の背景) → 雲 → 衝撃波 → 光条 → 破片 → 星 → 光 → 画面の閃光 の順に重ねる
        sr.sortingOrder = absOrder != int.MinValue ? absOrder : SortingOrder + (order >= 0 ? order : shape == Shape.Solid ? 20 : (int)shape + 1);
        sr.color = FxMath.Rgba(color0.r, color0.g, color0.b, 0f);
        go.SetActive(true);

        // 光条のテクスチャは横長 (4:1) なので、縦の指定値がそのまま太さ (単位) になるよう補正する
        float aspect = shape switch { Shape.Ray => RayAspect, Shape.Beam => BeamAspect, Shape.Feather or Shape.ClawSlash => 4f, _ => 1f };

        Active.Add(new Particle
        {
            Go = go, Sr = sr, Tf = tf, Pos = pos, Vel = vel, Drag = drag, Rise = rise, Delay = delay, Life = life,
            Sx0 = sx0, Sx1 = sx1, Sy0 = (sy0 < 0f ? sx0 : sy0) * aspect, Sy1 = (sy1 < 0f ? sx1 : sy1) * aspect,
            Rot = rot ?? Rnd(0f, 360f), Spin = spin,
            Color0 = color0, Color1 = color1, ColorMid = colorMid ?? color0, HasMid = colorMid.HasValue, Alpha = alpha, FadeIn = FxMath.Max(fadeIn, 0.001f), FadeOutFrom = fadeOutFrom,
            Twinkle = twinkle, TwinkleSpeed = twinkleSpeed, Phase = Rnd(0f, 6.28f), Stretch = stretch, FollowCamera = followCamera,
            // 画面全体の閃光は片付けの対象にしない (消すと揺れが最初からやり直しになる)
            Tag = followCamera || camBand ? (byte)0 : _tag, IsBeam = shape == Shape.Beam, AnchorX = _anchorX, AnchorY = _anchorY, Flap = flap, Z = z, WobbleA = wobble, WobbleF = wobbleHz, Flat = flat,
            CamBand = camBand
        });
    }

    private static void Release(Particle p)
    {
        if (!p.Go) return;

        if (p.Flat) p.Go.transform.SetParent(null, false);
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

            // 揺れ: 大きさを周期的に膨らませたり縮めたりする (エネルギーの脈動)
            if (p.WobbleA > 0f)
            {
                float w = 1f + p.WobbleA * FxMath.Sin(6.2832f * p.WobbleF * p.Age + p.Phase);
                sx *= w;
                sy *= w;
            }

            if (p.Stretch > 0f)
            {
                float speed = FxMath.Sqrt(vx * vx + vy * vy);
                sx *= 1f + speed * p.Stretch;
                if (speed > 0.01f) rot = FxMath.Atan2(vy, vx) * FxMath.Rad2Deg;
            }

            if (p.FollowCamera || p.CamBand)
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
                    // 横帯は画面の幅いっぱい・高さはカメラ中心からの Pos.y に置き、太さは指定のまま
                    px = camX;
                    py = p.CamBand ? camY + p.Pos.y : camY;
                    sx = camW;
                    if (!p.CamBand) sy = camH;
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
            p.Tf.position = FxMath.V3(px, py, p.Z);
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
        if (!_hexSeal) _hexSeal = MakeSprite(512, 512, HexSealAlpha);
        if (!_clockFace) _clockFace = MakeSprite(256, 256, ClockFaceAlpha);
        if (!_webDisc) _webDisc = MakeSprite(512, 512, WebDiscAlpha);
        if (!_fuseRope) _fuseRope = MakeSprite(512, 512, FuseRopeAlpha);
        if (!_hourglass) _hourglass = MakeSprite(512, 512, HourglassAlpha);
        if (!_hexSealHalo) _hexSealHalo = MakeHalo(HexSealAlpha);
        if (!_webDiscHalo) _webDiscHalo = MakeHalo(WebDiscAlpha);
        if (!_fuseRopeHalo) _fuseRopeHalo = MakeHalo(FuseRopeAlpha);
        if (!_hourglassHalo) _hourglassHalo = MakeHalo(HourglassAlpha);
        if (!_vultureSil) _vultureSil = MakeSprite(256, 256, VultureAlpha);
        if (!_vultureHalo) _vultureHalo = MakeHalo(VultureAlpha);
        if (!_feather) _feather = MakeSprite(128, 32, FeatherAlpha);
        if (!_clawSlash) _clawSlash = MakeSprite(256, 64, ClawSlashAlpha);
        if (!_spiderSil) _spiderSil = MakeSprite(256, 256, SpiderAlpha);
        if (!_spiderHalo) _spiderHalo = MakeHalo(SpiderAlpha);
        if (!_crewSil) _crewSil = MakeSprite(128, 128, CrewAlpha);
        if (!_clockFine) _clockFine = MakeSprite(512, 512, (x, y) => ClockFineAlpha(x, y, 1f));
        if (!_clockFineHalo) _clockFineHalo = MakeSprite(256, 256, (x, y) => ClockFineAlpha(x, y, 5f));

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

    // 点 (x, y) から線分 a-b までの距離
    private static float SegDist(float x, float y, float ax, float ay, float bx, float by)
    {
        float dx = bx - ax, dy = by - ay;
        float t = FxMath.Clamp01(((x - ax) * dx + (y - ay) * dy) / (dx * dx + dy * dy));
        float px = ax + dx * t - x, py = ay + dy * t - y;
        return FxMath.Sqrt(px * px + py * py);
    }

    // 呪印の陣: 二重円・不規則な記号が 48 並ぶ呪文帯・五芒星と頂点の小円・内円・中心の目
    internal static float HexSealAlpha(float x, float y)
    {
        float d = FxMath.Sqrt(x * x + y * y);
        if (d > 1f) return 0f;

        float a = FxMath.Max(Line(d - 0.955f, 0.016f), Line(d - 0.9f, 0.009f));
        a = FxMath.Max(a, Line(d - 0.76f, 0.009f));

        if (d > 0.775f && d < 0.885f)
        {
            float cf = (FxMath.Atan2(y, x) / (2f * FxMath.PI) + 0.5f) * 48f;
            int ci = (int)cf;
            float u = cf - ci;
            float v = (d - 0.775f) / 0.11f;
            float g = 0f;

            switch ((ci * 13 + 5) % 7)
            {
                case 0:
                    if (v > 0.15f && v < 0.85f) g = Line(u - 0.5f, 0.09f);
                    break;
                case 1:
                    g = Line(FxMath.Abs(u - 0.5f) / 0.3f + FxMath.Abs(v - 0.5f) / 0.36f - 1f, 0.18f);
                    break;
                case 2:
                    g = Line(FxMath.Sqrt((u - 0.5f) * (u - 0.5f) + (v - 0.5f) * (v - 0.5f)), 0.12f);
                    break;
                case 3:
                    if (v > 0.25f && v < 0.75f) g = FxMath.Max(Line(u - 0.3f, 0.07f), Line(u - 0.7f, 0.07f));
                    break;
                case 4:
                    if (v > 0.1f && v < 0.9f) g = Line(u - 0.5f - (v - 0.5f) * 0.8f, 0.08f);
                    break;
                case 6:
                    if (v < 0.8f) g = Line(u - 0.35f, 0.08f);
                    if (u > 0.3f && u < 0.75f) g = FxMath.Max(g, Line(v - 0.8f, 0.09f));
                    break;
            }

            a = FxMath.Max(a, g * FxMath.Clamp01((0.8f - FxMath.Abs(u - 0.5f) * 1.6f) * 4f));
        }

        // 五芒星と頂点の小円
        for (int k = 0; k < 5; k++)
        {
            float a0 = (90f + k * 72f) * FxMath.Deg2Rad, a1 = (90f + ((k + 2) % 5) * 72f) * FxMath.Deg2Rad;
            float ax = FxMath.Cos(a0) * 0.68f, ay = FxMath.Sin(a0) * 0.68f;
            a = FxMath.Max(a, Line(SegDist(x, y, ax, ay, FxMath.Cos(a1) * 0.68f, FxMath.Sin(a1) * 0.68f), 0.011f));
            a = FxMath.Max(a, Line(FxMath.Sqrt((x - ax) * (x - ax) + (y - ay) * (y - ay)) - 0.045f, 0.009f));
        }

        a = FxMath.Max(a, Line(d - 0.36f, 0.011f));
        a = FxMath.Max(a, Line(d - 0.31f, 0.007f));

        // 中心の目
        float ex = x / 0.22f;
        if (FxMath.Abs(ex) < 1f) a = FxMath.Max(a, Line(FxMath.Abs(y) - 0.11f * (1f - ex * ex), 0.009f));
        a = FxMath.Max(a, FxMath.Clamp01((0.05f - d) / 0.01f));

        float fill = d < 0.955f ? 0.06f + 0.04f * FxMath.Pow(d / 0.955f, 4f) : 0f;
        return FxMath.Clamp01(a + fill);
    }

    // 時計盤: 歯車の歯 36・目盛り 60 (5 の倍数は太く長い)・12 の記号 (菱形と十字)・内側の小歯車と輪軸
    internal static float ClockFaceAlpha(float x, float y)
    {
        float d = FxMath.Sqrt(x * x + y * y);
        if (d > 1f) return 0f;

        float ang = FxMath.Atan2(y, x) / (2f * FxMath.PI) + 0.5f;
        float a = Line(d - 0.875f, 0.012f);

        // 外周の歯
        float u36 = ang * 36f - (int)(ang * 36f);
        float tooth = FxMath.Clamp01((u36 - 0.15f) / 0.05f) * FxMath.Clamp01((0.85f - u36) / 0.05f);
        a = FxMath.Max(a, tooth * FxMath.Clamp01((d - 0.885f) / 0.012f) * FxMath.Clamp01((0.965f - d) / 0.012f) * 0.95f);

        // 目盛り
        float c60 = ang * 60f;
        int i60 = (int)c60;
        bool major = i60 % 5 == 0;
        float lo = major ? 0.7f : 0.74f;
        if (d > lo && d < 0.84f) a = FxMath.Max(a, Line(c60 - i60 - 0.5f, major ? 0.14f : 0.07f));

        // 12 の記号
        if (d > 0.55f && d < 0.66f)
        {
            float c12 = ang * 12f;
            int i12 = (int)c12;
            float u = c12 - i12 - 0.5f;
            float v = (d - 0.605f) / 0.055f;
            float g = i12 % 2 == 0
                ? Line(FxMath.Abs(u) / 0.22f + FxMath.Abs(v) / 0.9f - 1f, 0.18f)
                : FxMath.Max(Line(u, 0.06f) * (FxMath.Abs(v) < 0.9f ? 1f : 0f), Line(v * 0.4f, 0.08f) * (FxMath.Abs(u) < 0.2f ? 1f : 0f));
            a = FxMath.Max(a, g);
        }

        a = FxMath.Max(a, Line(d - 0.52f, 0.011f));

        // 内側の小歯車
        if (d < 0.47f)
        {
            float c12b = ang * 12f;
            float ub = c12b - (int)c12b;
            float t2 = FxMath.Clamp01((ub - 0.2f) / 0.05f) * FxMath.Clamp01((0.8f - ub) / 0.05f);
            a = FxMath.Max(a, t2 * FxMath.Clamp01((d - 0.36f) / 0.01f) * FxMath.Clamp01((0.46f - d) / 0.01f) * 0.9f);
            a = FxMath.Max(a, Line(d - 0.3f, 0.01f));

            if (d > 0.06f && d < 0.3f)
            {
                float c6 = ang * 6f;
                float near = FxMath.Abs(c6 - (int)(c6 + 0.5f)) / 6f * 2f * FxMath.PI;
                a = FxMath.Max(a, Line(d * FxMath.Sin(near), 0.01f));
            }

            a = FxMath.Max(a, FxMath.Clamp01((0.065f - d) / 0.01f));
        }

        float fill = d < 0.97f ? 0.05f : 0f;
        return FxMath.Clamp01(a + fill);
    }

    // 砂時計: 上下の枠板と柱 2 本・くびれのあるガラスの輪郭と反射・下の砂の山・上の砂・中を落ちる細い砂の筋
    internal static float HourglassAlpha(float x, float y)
    {
        float ax = FxMath.Abs(x), ay = FxMath.Abs(y);
        float a = 0f;

        // 枠板 (上下) と柱
        if (ay > 0.78f && ay < 0.9f) a = FxMath.Clamp01((0.6f - ax) / 0.02f) * FxMath.Clamp01((ay - 0.78f) / 0.01f) * FxMath.Clamp01((0.9f - ay) / 0.01f);
        if (ay <= 0.78f) a = FxMath.Max(a, Line(ax - 0.54f, 0.02f));

        if (ay < 0.78f)
        {
            // ガラス: くびれから上下へ広がる輪郭
            float w = 0.045f + 0.42f * FxMath.Pow(ay / 0.78f, 0.7f);
            a = FxMath.Max(a, Line(ax - w, 0.012f));
            float inside = ax < w ? 0.1f : 0f;
            a = FxMath.Max(a, inside);

            // ガラスの反射
            if (x < 0f && ay > 0.15f && ay < 0.7f) a = FxMath.Max(a, 0.55f * Line(x + w * 0.6f, 0.014f));

            // 下の砂の山 (表面は中央が少し凹む) と上に残る砂
            if (y < 0f && y < -0.36f + 0.06f * (x / FxMath.Max(w, 0.05f)) * (x / FxMath.Max(w, 0.05f)) && ax < w * 0.92f) a = FxMath.Max(a, 0.6f);
            if (y > 0.4f && y < 0.62f && ax < w * 0.92f) a = FxMath.Max(a, 0.45f);

            // 中を落ちる細い砂
            if (y > -0.36f && y < 0.02f) a = FxMath.Max(a, 0.85f * Line(x, 0.012f));
        }

        return FxMath.Clamp01(a);
    }

    // 蜘蛛の巣の円盤: 放射の糸 12 本・たるんだ 9 周の環・一部の交点に大きさの違う露・中心の密な渦
    internal static float WebDiscAlpha(float x, float y)
    {
        float d = FxMath.Sqrt(x * x + y * y);
        if (d > 1f) return 0f;

        const int n = 12;
        const float seg = 2f * FxMath.PI / n;
        float ang = FxMath.Atan2(y, x) + FxMath.PI;
        int k = (int)(ang / seg) % n;
        float rem = ang - k * seg;
        float frac = FxMath.Clamp01(rem / seg);
        float across = d * FxMath.Sin(FxMath.Min(rem, seg - rem));

        float a = d > 0.03f ? Line(across, 0.0045f) * FxMath.Clamp01((0.97f - d) / 0.03f) : 1f;
        float sag = FxMath.Sin(FxMath.PI * frac);

        for (int j = 1; j < 10; j++)
        {
            float rho = 0.08f + 0.098f * j + 0.002f * j * j;
            if (rho > 0.97f) break;

            a = FxMath.Max(a, Line(d - rho * (1f - 0.06f * sag), 0.0045f) * 0.95f);

            int h = (j * 7 + k * 13) % 5;

            if (h < 2)
            {
                float ds = 0.012f + 0.006f * h;
                float px = d * FxMath.Sin(rem), py = d - rho;
                a = FxMath.Max(a, FxMath.Exp(-(px * px + py * py) / (ds * ds)));
            }
        }

        if (d < 0.12f)
        {
            float t = d * 40f + ang / (2f * FxMath.PI);
            a = FxMath.Max(a, Line(t - (int)t - 0.5f, 0.12f) * 0.7f);
        }

        return FxMath.Clamp01(a);
    }

    // 導火線の縄: 縄を撚った輪 (斜めの縞)
    internal static float FuseRopeAlpha(float x, float y)
    {
        float d = FxMath.Sqrt(x * x + y * y);
        if (d > 1f) return 0f;

        float ang = FxMath.Atan2(y, x);
        float band = FxMath.Exp(-FxMath.Pow((d - 0.85f) / 0.07f, 2f));
        float stripe = 0.5f + 0.5f * FxMath.Sin(ang * 24f + (d - 0.85f) * 60f);
        float a = band * (0.55f + 0.45f * stripe);
        a = FxMath.Max(a, Line(d - 0.7f, 0.008f) * 0.6f);
        return FxMath.Clamp01(a);
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

    private static readonly string[] Roman = ["XII", "I", "II", "III", "IIII", "V", "VI", "VII", "VIII", "IX", "X", "XI"];

    // 細密な時計盤 (512px): 外周の細歯 96・微目盛り 120・分目盛り 60・ローマ数字・レースの輪・内歯車と輻。
    // wm は線幅の倍率。1 で本体、5 でにじみ版 (同じ線をぼかした光) を作る
    internal static float ClockFineAlpha(float x, float y, float wm)
    {
        float d = FxMath.Sqrt(x * x + y * y);
        if (d > 1f) return 0f;

        float ang = FxMath.Atan2(y, x) / (2f * FxMath.PI) + 0.5f;
        float a = FxMath.Max(Line(d - 0.985f, 0.004f * wm), Line(d - 0.945f, 0.005f * wm));
        a = FxMath.Max(a, FxMath.Max(Line(d - 0.895f, 0.003f * wm), Line(d - 0.79f, 0.004f * wm)));

        // 外周の細歯
        if (d > 0.94f && d < 0.985f)
        {
            float u = ang * 96f - (int)(ang * 96f);
            float e = 0.04f * wm;
            float tooth = FxMath.Clamp01((u - 0.28f) / e) * FxMath.Clamp01((0.72f - u) / e);
            a = FxMath.Max(a, tooth * FxMath.Clamp01((d - 0.945f) / (0.006f * wm)) * FxMath.Clamp01((0.972f - d) / (0.006f * wm)) * 0.9f);
        }

        // 微目盛り
        if (d > 0.9f && d < 0.94f)
        {
            float u = ang * 120f - (int)(ang * 120f);
            a = FxMath.Max(a, Line(u - 0.5f, 0.07f * wm) * 0.8f);
        }

        // 分目盛り (5 の倍数は長く太い)
        if (d > 0.8f && d < 0.89f)
        {
            float c60 = ang * 60f;
            int i60 = (int)c60;
            bool major = i60 % 5 == 0;
            if (major || d > 0.845f) a = FxMath.Max(a, Line(c60 - i60 - 0.5f, (major ? 0.09f : 0.045f) * wm));
        }

        // ローマ数字 (上辺が外側を向く)。字の中の座標は接線方向 s・動径方向 t (単位は半径)
        if (d > 0.64f && d < 0.79f)
        {
            // 12 時から時計回りに数えた時刻。字は各時刻の真上に中心を置く
            float hour = (1.75f - ang) * 12f;
            int ih = (int)(hour + 0.5f);
            float s = (hour - ih) / 12f * 2f * FxMath.PI * d;
            float t = d - 0.715f;
            string glyph = Roman[ih % 12];
            const float cw = 0.03f, gap = 0.012f, hh = 0.04f;
            float total = glyph.Length * cw + (glyph.Length - 1) * gap;
            float w = 0.0045f * wm;
            float g = 0f;

            if (FxMath.Abs(t) < hh + w * 3f)
            {
                // セリフ (上下の横線)
                if (FxMath.Abs(s) < total * 0.5f + 0.008f) g = FxMath.Max(Line(t - hh, w), Line(t + hh, w));

                float x0 = -total * 0.5f;

                for (int k = 0; k < glyph.Length; k++)
                {
                    float cx = x0 + cw * 0.5f + k * (cw + gap);
                    char ch = glyph[k];

                    if (ch == 'I') g = FxMath.Max(g, Line(SegDist(s, t, cx, -hh, cx, hh), w));
                    else if (ch == 'V')
                        g = FxMath.Max(g, FxMath.Max(Line(SegDist(s, t, cx - cw * 0.5f, hh, cx, -hh), w), Line(SegDist(s, t, cx + cw * 0.5f, hh, cx, -hh), w)));
                    else
                        g = FxMath.Max(g, FxMath.Max(Line(SegDist(s, t, cx - cw * 0.5f, hh, cx + cw * 0.5f, -hh), w), Line(SegDist(s, t, cx + cw * 0.5f, hh, cx - cw * 0.5f, -hh), w)));
                }
            }

            a = FxMath.Max(a, g);
        }

        a = FxMath.Max(a, Line(d - 0.63f, 0.003f * wm));

        // 点線の輪
        if (d > 0.595f && d < 0.625f)
        {
            float u = ang * 120f - (int)(ang * 120f);
            float px = (u - 0.5f) / 120f * 2f * FxMath.PI * 0.61f;
            float pd = FxMath.Sqrt(px * px + (d - 0.61f) * (d - 0.61f));
            a = FxMath.Max(a, Line(pd, 0.004f * wm));
        }

        // レースの輪: 重なる小円 24 個
        if (d > 0.44f && d < 0.6f)
        {
            float c24 = ang * 24f;
            for (int k = 0; k < 2; k++)
            {
                float ca = ((int)c24 + k + 0.5f) / 24f * 2f * FxMath.PI - FxMath.PI;
                float ox = FxMath.Cos(ca) * 0.52f, oy = FxMath.Sin(ca) * 0.52f;
                float r = FxMath.Sqrt((x - ox) * (x - ox) + (y - oy) * (y - oy));
                a = FxMath.Max(a, Line(r - 0.068f, 0.0035f * wm) * 0.85f);
            }
        }

        a = FxMath.Max(a, Line(d - 0.43f, 0.005f * wm));

        // 内歯車と輻
        if (d < 0.42f)
        {
            float u = ang * 24f - (int)(ang * 24f);
            float e = 0.05f * wm;
            float tooth = FxMath.Clamp01((u - 0.25f) / e) * FxMath.Clamp01((0.75f - u) / e);
            a = FxMath.Max(a, tooth * FxMath.Clamp01((d - 0.34f) / (0.006f * wm)) * FxMath.Clamp01((0.39f - d) / (0.006f * wm)) * 0.85f);
            a = FxMath.Max(a, Line(d - 0.33f, 0.004f * wm));
            a = FxMath.Max(a, Line(d - 0.2f, 0.003f * wm));

            if (d > 0.07f && d < 0.33f)
            {
                float c6 = ang * 6f;
                float near = FxMath.Abs(c6 - (int)(c6 + 0.5f)) / 6f * 2f * FxMath.PI;
                a = FxMath.Max(a, Line(d * FxMath.Sin(near), 0.004f * wm));
            }

            a = FxMath.Max(a, Line(d - 0.07f, 0.004f * wm));
            a = FxMath.Max(a, FxMath.Clamp01((0.035f - d) / (0.006f * wm)));
        }

        if (wm > 1f) return FxMath.Clamp01(a * 0.6f);

        float fill = d < 0.95f ? 0.035f : 0f;
        return FxMath.Clamp01(a + fill);
    }

    // 楕円の塗り (縁は soft の幅でぼかす)
    private static float Ell(float x, float y, float cx, float cy, float rx, float ry, float soft)
    {
        float dx = (x - cx) / rx, dy = (y - cy) / ry;
        return FxMath.Clamp01((1f - FxMath.Sqrt(dx * dx + dy * dy)) * FxMath.Min(rx, ry) / soft);
    }

    // 点 (x, y) から線分 a-b までの距離と、線分上の最寄り点の位置 t (0〜1)
    private static float SegDistT(float x, float y, float ax, float ay, float bx, float by, out float t)
    {
        float dx = bx - ax, dy = by - ay;
        t = FxMath.Clamp01(((x - ax) * dx + (y - ay) * dy) / (dx * dx + dy * dy));
        float px = ax + dx * t - x, py = ay + dy * t - y;
        return FxMath.Sqrt(px * px + py * py);
    }

    // 下から見たハゲタカ (頭は +x・翼は縦に広がる。flap で縦を潰すと羽ばたきになる)。
    // 幅広の翼の先に風切羽の指 6 本・首の襟巻き・細い首と鉤の頭・扇の尾
    internal static float VultureAlpha(float px, float py)
    {
        // 頭を +y に置いた座標で描く
        float x = py, y = px;
        const float e = 0.012f;
        float a = Ell(x, y, 0f, -0.05f, 0.12f, 0.3f, e);
        a = FxMath.Max(a, Ell(x, y, 0f, 0.22f, 0.11f, 0.07f, e));
        a = FxMath.Max(a, Ell(x, y, 0f, 0.31f, 0.03f, 0.08f, e));
        a = FxMath.Max(a, Ell(x, y, 0f, 0.41f, 0.045f, 0.05f, e));
        a = FxMath.Max(a, FxMath.Clamp01((0.022f - SegDistT(x, y, 0f, 0.44f, 0f, 0.5f, out _)) / e));
        float ax = FxMath.Abs(x);

        if (ax > 0.08f && ax < 0.66f)
        {
            float t = (ax - 0.08f) / 0.58f;
            float lead = 0.1f + 0.28f * t - 0.3f * t * t;
            float trail = -0.3f + 0.28f * t - 0.035f * FxMath.Abs(FxMath.Sin(t * FxMath.PI * 9f));
            a = FxMath.Max(a, FxMath.Clamp01(FxMath.Min(lead - y, y - trail) / e));
        }

        for (int k = 0; k < 6; k++)
        {
            float ang = (24f - k * 11f) * FxMath.Deg2Rad;
            float ln = 0.38f - FxMath.Abs(k - 1.5f) * 0.025f;
            float d = SegDistT(ax, y, 0.6f, 0.02f, 0.6f + FxMath.Cos(ang) * ln, 0.02f + FxMath.Sin(ang) * ln, out float tt);
            float w = 0.055f * FxMath.Pow(1f - tt, 0.7f) + 0.008f;
            a = FxMath.Max(a, FxMath.Clamp01((w - d) / e));
        }

        if (y > -0.62f && y < -0.28f)
        {
            float t = (-0.28f - y) / 0.34f;
            float hw = 0.1f + 0.1f * t;
            float notch = t > 0.85f ? 0.03f * FxMath.Abs(FxMath.Sin(x / hw * FxMath.PI * 2.5f)) : 0f;
            a = FxMath.Max(a, FxMath.Clamp01((hw - ax) / e) * FxMath.Clamp01((y + 0.62f - notch) / e));
        }

        return a;
    }

    // 羽根 1 枚 (横長 4:1・先端が +x): 羽軸と斜めの羽枝の縞
    internal static float FeatherAlpha(float x, float y)
    {
        float u = (x + 1f) * 0.5f;
        float w = u > 0.08f ? 0.85f * FxMath.Pow(FxMath.Sin(FxMath.PI * FxMath.Min(1f, u * 1.05f)), 0.6f) : 0f;
        float vane = FxMath.Clamp01((w - FxMath.Abs(y)) / 0.08f);
        float a = vane * (0.55f + 0.45f * FxMath.Sin((x + FxMath.Abs(y) * 0.3f) * 38f));
        if (FxMath.Abs(y) < 0.07f && x < 0.9f) a = 1f;
        return FxMath.Clamp01(a);
    }

    // 爪痕 (横長 4:1): 中央が太く両端が細い三日月
    internal static float ClawSlashAlpha(float x, float y)
    {
        float yc = 0.45f * (1f - x * x) - 0.2f;
        float th = 0.55f * FxMath.Pow(FxMath.Max(0f, 1f - x * x), 1.3f);
        return FxMath.Clamp01((th - FxMath.Abs(y - yc)) / 0.12f);
    }

    // 上から見た蜘蛛 (頭は +y): 腹・頭胸・牙・折れ曲がった脚 8 本・腹の砂時計模様 (少し透ける)
    internal static float SpiderAlpha(float x, float y)
    {
        const float e = 0.012f;
        float belly = Ell(x, y, 0f, -0.22f, 0.25f, 0.31f, e);
        float a = FxMath.Max(belly, Ell(x, y, 0f, 0.18f, 0.16f, 0.14f, e));
        a = FxMath.Max(a, FxMath.Max(Ell(x, y, -0.05f, 0.33f, 0.035f, 0.05f, e), Ell(x, y, 0.05f, 0.33f, 0.035f, 0.05f, e)));
        float ax = FxMath.Abs(x);

        for (int k = 0; k < 4; k++)
        {
            float y0 = k switch { 0 => 0.26f, 1 => 0.21f, 2 => 0.15f, _ => 0.09f };
            float kx = k switch { 0 => 0.55f, 1 => 0.6f, 2 => 0.6f, _ => 0.52f };
            float ky = k switch { 0 => 0.52f, 1 => 0.3f, 2 => 0.02f, _ => -0.2f };
            float fx = k switch { 0 => 0.72f, 1 => 0.9f, 2 => 0.88f, _ => 0.75f };
            float fy = k switch { 0 => 0.95f, 1 => 0.35f, 2 => -0.2f, _ => -0.75f };
            float d1 = SegDistT(ax, y, 0.12f, y0, kx, ky, out _);
            float d2 = SegDistT(ax, y, kx, ky, fx, fy, out float t2);
            a = FxMath.Max(a, FxMath.Max(FxMath.Clamp01((0.035f - d1) / e), FxMath.Clamp01((0.03f - 0.018f * t2 - d2) / e)));
        }

        if (belly > 0.99f && FxMath.Abs(y + 0.22f) < 0.16f && ax - 0.02f - FxMath.Abs(y + 0.22f) * 0.35f < 0f) a = 0.55f;
        return a;
    }

    // クルーの形 (右向き): 胴・背中のリュック・脚 2 本。バイザーは薄く抜いて体色より明るく見せる
    internal static float CrewAlpha(float x, float y)
    {
        const float e = 0.02f;
        float a = y > -0.55f && y < 0.35f ? FxMath.Clamp01((0.36f - FxMath.Abs(x + 0.02f)) / e) * FxMath.Clamp01((y + 0.55f) / e) : 0f;
        a = FxMath.Max(a, Ell(x, y, -0.02f, 0.35f, 0.36f, 0.3f, e));
        a = FxMath.Max(a, FxMath.Max(Ell(x, y, -0.2f, -0.62f, 0.13f, 0.14f, e), Ell(x, y, 0.16f, -0.62f, 0.13f, 0.14f, e)));
        a = FxMath.Max(a, FxMath.Clamp01(FxMath.Min(-0.34f - x, x + 0.56f) / e) * FxMath.Clamp01(FxMath.Min(y + 0.3f, 0.22f - y) / e));
        return FxMath.Max(0f, a - 0.55f * Ell(x, y, 0.16f, 0.3f, 0.24f, 0.13f, e));
    }

    // 線をぼかした光のにじみ版: 256px で描いてから横・縦の箱ぼかしを 2 回ずつ掛け、細い線も光るよう明るさを持ち上げる。
    // 円盤の内側の薄い塗りまでぼかすと面が一様に光って模様が埋もれるので、薄い値は先に落としておく
    private static Sprite MakeHalo(System.Func<float, float, float> alpha)
    {
        const int n = 256;
        const int r = 5;
        const float gain = 2.4f;
        float h = (n - 1) * 0.5f;
        var a = new float[n * n];
        var b = new float[n * n];

        for (int py = 0; py < n; py++)
        {
            for (int px = 0; px < n; px++)
                a[py * n + px] = FxMath.Max(0f, alpha((px - h) / h, (py - h) / h) - 0.15f);
        }

        for (int pass = 0; pass < 2; pass++)
        {
            BoxBlur(a, b, n, r, 1, n);
            BoxBlur(b, a, n, r, n, 1);
        }

        var pixels = new Color[n * n];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = FxMath.Rgba(1f, 1f, 1f, FxMath.Clamp01(a[i] * gain));

        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        tex.SetPixels(pixels);
        tex.Apply(false, true);
        tex.hideFlags |= HideFlags.HideAndDontSave;
        Sprite sprite = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), n, 0, SpriteMeshType.FullRect);
        sprite.hideFlags |= HideFlags.HideAndDontSave;
        return sprite;
    }

    // step 方向 (1 = 横, n = 縦) に半径 r の箱ぼかし。line は並ぶ列の間隔
    private static void BoxBlur(float[] src, float[] dst, int n, int r, int step, int line)
    {
        float inv = 1f / (2 * r + 1);

        for (int l = 0; l < n; l++)
        {
            int o = l * line;
            float sum = 0f;
            for (int k = -r; k <= r; k++) sum += src[o + FxMath.Clamp(k, 0, n - 1) * step];

            for (int i = 0; i < n; i++)
            {
                dst[o + i * step] = sum * inv;
                sum += src[o + FxMath.Min(i + r + 1, n - 1) * step] - src[o + FxMath.Max(i - r, 0) * step];
            }
        }
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
