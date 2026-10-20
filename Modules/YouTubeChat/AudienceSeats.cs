using System;
using System.Collections.Concurrent;
using TMPro;
using UnityEngine;

namespace EndKnot.Modules.YouTubeChat;

// 配信画面の左下に「観客席」を出す。2 段のひな壇に、コメントした視聴者がゲームのクルー (色と帽子は名前から決まる) として座り、
// コメントするとその子が跳ねて頭の上に吹き出しが出る。吹き出しは新しいものほど頭のすぐ上に積み、古いものは上へ押し上げて消す。
// !コマンドの吹き出しは干渉の色で縁取り、実際に発動するとその子が大きく跳ねて光る。
// 前の札をクリックすると過去のコメントの板 (YouTubeChatBubble) を開閉する。板は席の右隣に下端をそろえて貼り付く。
// 席とクルーは HUD のカメラに置く絵、吹き出しは IMGUI (絵の上に重なる)。100% host 画面ローカル (RPC / NetObject 不使用)。
public class AudienceSeats : MonoBehaviour
{
    public static AudienceSeats Instance;

    // 前列 5 席・後列 4 席 (後列は前列の間に半席ずらして座る)
    private const int FrontSeats = 5;
    private const int MaxSeats = 9;
    private const float Step = 74f;
    private const float RowLift = 46f;

    private const float BubbleSeconds = 5f;
    private const float BubbleFade = 0.6f;
    private const int MaxBubbles = 4;
    private const float ArriveSeconds = 0.35f;
    private const float HopSeconds = 0.32f;
    private const float GlowSeconds = 1.6f;
    private const int MaxTextLength = 40;
    private const float PicoGap = 0.12f;

    // 歩く速さ (px/秒・大きさ 1 の時) と、よちよち歩きの歩数 (歩/秒)
    private const float WalkSpeed = 170f;
    private const float StepsPerSecond = 4.5f;
    private const float SpinSeconds = 0.55f;

    // 座っている間の仕草
    private enum Fidget : byte { None, LookAround, Hop, Lean, Wiggle }

    private const int Layer = 5;

    private sealed class Seat
    {
        public string Author;
        public int ColorId;
        public string HatId;
        public float Arrive = -10f;
        public float LastActive;
        public float HopStart = -10f;
        public float HopHeight;
        public GUIContent Name, Text;
        public float BubbleStart = -10f;
        public bool IsCommand;
        public Color Accent;
        public bool Measured;
        public float BubbleW, TextH;
        public float Y;
        public bool HasY;
        public float GlowStart = -10f;
        public Color Glow;
        public PoolablePlayer Crew;
        public SpriteRenderer GlowSr;
        public bool FaceLeft;
        public float K;

        // 席まで歩いてくる: 出発点 (席からの横のずれ px) と歩き始め・歩く秒数
        public float WalkFrom;
        public float WalkStart = -10f;
        public float WalkSeconds;

        public Fidget Fidget;
        public float FidgetStart = -10f;
        public float FidgetSeconds;
        public float NextFidget;
        public float LeanDir;
        public float Breath;
        public float SpinStart = -10f;
        public float RetrySpawnAt;
        public bool GlowOn;
    }

    // 席を譲って歩き去っていく子
    private sealed class Leaver
    {
        public PoolablePlayer Crew;
        public float X, Y, Z, K, Start;
    }

    private readonly System.Collections.Generic.List<Leaver> _leavers = [];

    // 同時に何人も来た時は 1 人ずつ間を空けて入ってくる
    private float _nextWalkSlot;

    private readonly Seat[] _seats = new Seat[MaxSeats];
    private readonly int[] _order = new int[MaxSeats];

    private static readonly ConcurrentQueue<(string Author, string Text)> Incoming = new();
    private static readonly ConcurrentQueue<(string Author, string Kind)> Executed = new();
    private static bool _subscribed;

    private Vector2 _pos;
    private bool _posInitialized;
    private bool _dragging;
    private bool _didDrag;
    private bool _pressOnSign;
    private Vector2 _dragOffset;
    private bool _shown;
    private float _lastPico = -10f;

    // HUD のカメラに置く絵
    private GameObject _root;
    private bool _rootAlive;
    private float _nextRebuild;
    private float _builtWorldScale = -1f;
    private float _halfH = 3f, _halfW = 5.33f;

    // 絵を置いているカメラ。ズームで写す広さが変わっても、台全体を逆に拡縮して画面上の大きさと位置を保つ
    private Camera _cam;
    private static Sprite _white, _round, _glow;

    private Texture2D _whiteTex;
    private GUIStyle _whiteStyle, _textStyle, _nameStyle;
    private float _builtScale = -1f;

    private static float Scale => Il2Direct.ScreenWidth / 1080f * 0.5f * UserScale;
    private static float UserScale => (YouTubeChatOptions.SeatsScale?.GetInt() ?? 100) / 100f;

    private void Awake()
    {
        Instance = this;
        ImguiNoLayout.Apply(this);
    }

    // 視聴者コメントの受け口 (worker thread から呼ばれる)。ここでは積むだけ
    private static void OnChat(string author, string text)
    {
        if (string.IsNullOrWhiteSpace(author) || string.IsNullOrWhiteSpace(text)) return;
        if (Incoming.Count < 64) Incoming.Enqueue((author, text));
    }

    // 配信に繋がずに確かめる入口
    internal static void InjectForTest(string author, string text) => OnChat(author, text);

    // 干渉が実際に発動した (main thread)
    public static void OnExecuted(string author, string kindKey)
    {
        if (string.IsNullOrEmpty(author)) return;
        if (Executed.Count < 16) Executed.Enqueue((author, kindKey));
    }

    private static void EnsureSubscribed()
    {
        if (_subscribed) return;
        YouTubeChatManager.OnMessage += OnChat;
        _subscribed = true;
    }

    private static bool ShouldShow()
    {
        if (!HudManager.InstanceExists) return false;
        if (!Il2Direct.Alive(AmongUsClient.Instance) || !AmongUsClient.Instance.AmHost) return false;
        if (YouTubeChatOptions.Enabled == null || !YouTubeChatOptions.Enabled.GetBool()) return false;
        if (YouTubeChatOptions.SeatsEnabled == null || !YouTubeChatOptions.SeatsEnabled.GetBool()) return false;
        if (YouTubeChatOptions.HideDuringMeetings != null && YouTubeChatOptions.HideDuringMeetings.GetBool() && GameStates.IsMeeting) return false;
        return true;
    }

    private void Update()
    {
        try
        {
            EnsureSubscribed();
            float now = Time.realtimeSinceStartup;
            bool show = ShouldShow();

            while (Incoming.TryDequeue(out (string Author, string Text) m))
                if (show) TakeSeat(m.Author, m.Text, now);

            while (Executed.TryDequeue(out (string Author, string Kind) e))
            {
                Seat s = Find(e.Author);
                if (s == null) continue;
                s.GlowStart = now;
                s.Glow = Audience.AudienceCutscene.ThemeColor(e.Kind);
                s.HopStart = now;
                s.HopHeight = 34f;
                s.SpinStart = now;
                s.LastActive = now;
            }

            _shown = show;
            UpdateWorld(show, now);
        }
        catch (Exception ex) { Utils.ThrowException(ex); }
    }

    private Seat Find(string author)
    {
        for (int i = 0; i < MaxSeats; i++)
            if (_seats[i] != null && string.Equals(_seats[i].Author, author, StringComparison.Ordinal)) return _seats[i];
        return null;
    }

    private void TakeSeat(string author, string text, float now)
    {
        Seat s = Find(author);

        if (s == null)
        {
            // 空席 (前列から)、なければ一番長くコメントしていない人の席を譲ってもらう
            int pick = -1;
            for (int i = 0; i < MaxSeats; i++)
            {
                if (_seats[i] == null) { pick = i; break; }
                if (pick < 0 || _seats[i].LastActive < _seats[pick].LastActive) pick = i;
            }

            Seat old = _seats[pick];
            if (old != null) StartLeaving(old, pick, now);

            uint h = Hash(author);
            s = new Seat
            {
                Author = author, Arrive = now, Name = new GUIContent(Trim(author, 16)),
                ColorId = (int)(h % (uint)FxMath.Max(1, Palette.PlayerColors.Length)), HatId = HatFor(h >> 8), FaceLeft = (h & 0x10000) != 0
            };
            _seats[pick] = s;

            // ひな壇の左端の外から自分の席まで歩いてくる
            float sc = Scale;
            s.WalkFrom = _pos.x - 40f * sc - SeatX(pick, sc);
            s.WalkStart = FxMath.Max(now, _nextWalkSlot);
            _nextWalkSlot = s.WalkStart + 0.35f;
            s.WalkSeconds = -s.WalkFrom / (WalkSpeed * sc);
            s.Breath = (h & 0xff) / 255f * 6.28f;
            s.NextFidget = s.WalkStart + s.WalkSeconds + FxMath.Range(2f, 5f);
        }

        text = text.Trim().Replace('\n', ' ').Replace('\r', ' ');
        s.Text = new GUIContent(Trim(text, MaxTextLength));
        s.Measured = false;
        // 吹き出しは席に着いてから出す (歩いている子について横に動かさない)
        s.BubbleStart = FxMath.Max(now, s.WalkStart + s.WalkSeconds);
        s.HasY = false;
        s.LastActive = now;
        s.HopStart = now;
        s.HopHeight = 14f;
        s.IsCommand = Audience.AudienceManager.TryGetCommandKind(text, out string kind);
        s.Accent = s.IsCommand ? Audience.AudienceCutscene.ThemeColor(kind) : FxMath.Rgba(0.1f, 0.1f, 0.12f);

        // コメントが来た合図の「ピコッ」。立て続けに来ても間を空ける。コマンドは少し低く
        if (now - _lastPico >= PicoGap)
        {
            _lastPico = now;
            CustomSoundsManager.Play("AudiencePico", 0.55f, s.IsCommand ? 0.8f : FxMath.Range(0.95f, 1.12f));
        }
    }

    private static uint Hash(string s)
    {
        // 名前が同じなら毎回同じ見た目 (FNV-1a)
        uint h = 2166136261;
        foreach (char c in s) h = (h ^ c) * 16777619;
        return h;
    }

    // 席と同じ見た目 (干渉のカットインに同じ子が出てくるように)
    internal static NetworkedPlayerInfo.PlayerOutfit OutfitFor(string author)
    {
        uint h = Hash(author ?? "");
        return new NetworkedPlayerInfo.PlayerOutfit
        {
            PlayerName = "", ColorId = (byte)(h % (uint)FxMath.Max(1, Palette.PlayerColors.Length)), HatId = HatFor(h >> 8), SkinId = "skin_None",
            VisorId = "visor_EmptyVisor", PetId = "pet_EmptyPet", NamePlateId = "nameplate_NoPlate"
        };
    }

    // 3 人に 2 人くらいは帽子をかぶる
    private static string HatFor(uint h)
    {
        try
        {
            if (h % 3 == 0 || !HatManager.InstanceExists) return "hat_NoHat";
            var hats = HatManager.Instance.allHats;
            if (hats == null || hats.Length == 0) return "hat_NoHat";
            return hats[(int)(h % (uint)hats.Length)].ProdId;
        }
        catch { return "hat_NoHat"; }
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    // ── 席の並び (画面の px。y は上から) ─────────────────────────

    private float BenchWidth(float s) => (FrontSeats * Step + 30f) * s;

    private float SeatX(int i, float s) => i < FrontSeats
        ? _pos.x + (15f + Step * 0.5f + i * Step) * s
        : _pos.x + (15f + Step + (i - FrontSeats) * Step) * s;

    // 座った子の足元の高さ
    private float SeatFloor(int i, float s) => i < FrontSeats ? _pos.y : _pos.y - RowLift * s;

    private static float CrewHeight(float s) => 64f * s;

    private float CrewTop(Seat seat, int i, float s, float now)
    {
        float y = SeatFloor(i, s) - CrewHeight(s);

        // 歩いている間は一歩ごとに上下する
        float w = now - seat.WalkStart;
        if (w < seat.WalkSeconds) y -= FxMath.Abs(FxMath.Sin(w * FxMath.PI * StepsPerSecond)) * 5f * s;

        float h = (now - seat.HopStart) / HopSeconds;
        if (h < 1f) y -= seat.HopHeight * s * FxMath.Sin(h * FxMath.PI);

        if (seat.Fidget == Fidget.Hop)
        {
            float f = (now - seat.FidgetStart) / seat.FidgetSeconds;
            if (f < 1f) y -= 9f * s * FxMath.Sin(f * FxMath.PI);
        }

        return y;
    }

    private float CrewX(Seat seat, int i, float s, float now)
    {
        float x = SeatX(i, s);
        float w = now - seat.WalkStart;
        if (w < seat.WalkSeconds) x += seat.WalkFrom * (1f - w / seat.WalkSeconds);
        return x;
    }

    private static bool Walking(Seat seat, float now) => now - seat.WalkStart < seat.WalkSeconds;

    // 座っている間の仕草を時々選ぶ
    private static void TickFidget(Seat seat, float now)
    {
        if (Walking(seat, now)) return;
        if (seat.Fidget != Fidget.None && now - seat.FidgetStart < seat.FidgetSeconds) return;

        seat.Fidget = Fidget.None;
        if (now < seat.NextFidget) return;

        float r = FxMath.Range(0f, 1f);
        seat.Fidget = r < 0.35f ? Fidget.LookAround : r < 0.6f ? Fidget.Hop : r < 0.85f ? Fidget.Lean : Fidget.Wiggle;
        seat.FidgetStart = now;
        seat.FidgetSeconds = seat.Fidget switch { Fidget.LookAround => FxMath.Range(0.8f, 1.6f), Fidget.Hop => 0.3f, Fidget.Lean => 1.1f, _ => 0.9f };
        seat.LeanDir = FxMath.Range(0f, 1f) < 0.5f ? -1f : 1f;
        seat.NextFidget = now + seat.FidgetSeconds + FxMath.Range(2.5f, 6f);
    }

    // 傾き (度)。歩いている間は左右に揺れ、仕草で傾き、発動した時は一回転する
    private static float CrewTilt(Seat seat, float now)
    {
        float w = now - seat.WalkStart;
        if (w < seat.WalkSeconds) return FxMath.Sin(w * FxMath.PI * StepsPerSecond) * 7f;

        float spin = (now - seat.SpinStart) / SpinSeconds;
        if (spin < 1f) return spin * spin * (3f - 2f * spin) * 360f;

        float f = (now - seat.FidgetStart) / FxMath.Max(seat.FidgetSeconds, 0.01f);
        if (f >= 1f) return 0f;

        return seat.Fidget switch
        {
            // 傾けて少し止まり、戻す
            Fidget.Lean => seat.LeanDir * 9f * FxMath.Sin(FxMath.Min(f * 3f, 1f) * FxMath.PI * 0.5f) * (f > 0.75f ? (1f - f) * 4f : 1f),
            Fidget.Wiggle => FxMath.Sin(f * FxMath.PI * 6f) * 8f * (1f - f),
            _ => 0f
        };
    }

    // 向き: 歩いている間は進む方向、きょろきょろの間は反対を向く
    private static bool FacesLeft(Seat seat, float now)
    {
        if (Walking(seat, now)) return seat.WalkFrom > 0f;
        bool look = seat.Fidget == Fidget.LookAround && now - seat.FidgetStart < seat.FidgetSeconds;
        return look ? !seat.FaceLeft : seat.FaceLeft;
    }

    private void StartLeaving(Seat old, int index, float now)
    {
        if (old.Crew)
        {
            float sc = Scale;
            float u = PxToUnits;
            _leavers.Add(new Leaver
            {
                Crew = old.Crew, K = old.K, Start = now, Z = index < FrontSeats ? -2f : -1f,
                X = (SeatX(index, sc) - _pos.x) * u, Y = (CrewHeight(sc) * 0.5f - (SeatFloor(index, sc) - _pos.y)) * u
            });
            old.Crew = null;
        }

        DestroySeatObjects(old);
    }

    // 席を譲った子は右へ歩いて台の外へ出て消える
    private void TickLeavers(float s, float now)
    {
        float u = PxToUnits;
        float exit = (BenchWidth(s) + 50f * s) * u;

        for (int i = _leavers.Count - 1; i >= 0; i--)
        {
            Leaver l = _leavers[i];

            if (!l.Crew)
            {
                _leavers.RemoveAt(i);
                continue;
            }

            float t = now - l.Start;
            float x = l.X + t * WalkSpeed * s * u;

            if (x > exit)
            {
                Object.Destroy(l.Crew.gameObject);
                _leavers.RemoveAt(i);
                continue;
            }

            float bob = FxMath.Abs(FxMath.Sin(t * FxMath.PI * StepsPerSecond)) * 5f * s * u;
            Transform tf = l.Crew.transform;
            tf.localPosition = FxMath.V3(x, l.Y + bob, l.Z);
            tf.localRotation = FxMath.RotZ(FxMath.Sin(t * FxMath.PI * StepsPerSecond) * 7f);
            tf.localScale = FxMath.V3(l.K, l.K, 1f);
        }
    }

    // ── HUD のカメラに置く絵 ───────────────────────────────────

    private Vector3 ToLocal(float px, float py, float z, float zoom) =>
        FxMath.V3((px / Screen.width - 0.5f) * 2f * _halfW * zoom, (0.5f - py / Screen.height) * 2f * _halfH * zoom, z);

    // 台を作った時からカメラの写す広さが何倍になったか
    private float CameraZoom()
    {
        if (!_cam || _halfH <= 0f) return 1f;
        float zoom = _cam.orthographicSize / _halfH;
        return zoom > 0f ? zoom : 1f;
    }

    private float PxToUnits => 2f * _halfH / Screen.height;

    private void UpdateWorld(bool show, float now)
    {
        // 画面が作り直されると絵も一緒に消えるので、席の参照を捨てる
        if (_rootAlive && !_root)
        {
            _rootAlive = false;
            _root = null;
            for (int i = 0; i < MaxSeats; i++)
                if (_seats[i] != null) { _seats[i].Crew = null; _seats[i].GlowSr = null; }
            _leavers.Clear();
        }

        if (!show || !_posInitialized)
        {
            if (_rootAlive && _root.activeSelf) _root.SetActive(false);
            return;
        }

        float s = Scale;
        if (!_rootAlive && !BuildStand(s)) return;
        if (!_root.activeSelf) _root.SetActive(true);

        // 大きさが変わったら台を作り直す
        if (FxMath.Abs(_builtWorldScale - s) > _builtWorldScale * 0.02f && now >= _nextRebuild)
        {
            _nextRebuild = now + 1f;
            DestroyStand();
            if (!BuildStand(s)) return;
        }

        float zoom = CameraZoom();
        _root.transform.localPosition = ToLocal(_pos.x, _pos.y, -800f, zoom);
        _root.transform.localScale = FxMath.V3(zoom, zoom, 1f);
        TickLeavers(s, now);

        float u = PxToUnits;
        for (int i = 0; i < MaxSeats; i++)
        {
            Seat seat = _seats[i];
            if (seat == null) continue;

            if (!seat.Crew && now >= seat.RetrySpawnAt) SpawnCrew(seat, s, now);
            if (!seat.Crew) continue;

            TickFidget(seat, now);
            float top = CrewTop(seat, i, s, now);
            float cx = CrewX(seat, i, s, now);
            // クルーの絵は中心が体の真ん中
            float cy = top + CrewHeight(s) * 0.5f;
            // 後列は前列より奥に置く
            float z = i < FrontSeats ? -2f : -1f;
            Transform tf = seat.Crew.transform;
            tf.localPosition = FxMath.V3((cx - _pos.x) * u, -(cy - _pos.y) * u, z);
            tf.localRotation = FxMath.RotZ(CrewTilt(seat, now));
            // ゆっくりした呼吸で縦にわずかに伸び縮みする
            float breath = 1f + 0.025f * FxMath.Sin(now * 2.2f + seat.Breath);
            // 入場の順番待ちの間は見せない
            float k = now < seat.WalkStart ? 0f : seat.K;
            tf.localScale = FxMath.V3(FacesLeft(seat, now) ? -k : k, k * breath, 1f);

            float g = now - seat.GlowStart;
            if (seat.GlowSr)
            {
                if (g < GlowSeconds)
                {
                    float a = (1f - g / GlowSeconds) * (0.6f + 0.4f * FxMath.Sin(g * 18f));
                    seat.GlowSr.color = FxMath.Rgba(seat.Glow.r, seat.Glow.g, seat.Glow.b, a);
                    seat.GlowSr.transform.localPosition = FxMath.V3((cx - _pos.x) * u, -(cy - _pos.y) * u, z + 0.5f);
                    seat.GlowOn = true;
                }
                else if (seat.GlowOn)
                {
                    seat.GlowSr.color = FxMath.Rgba(1f, 1f, 1f, 0f);
                    seat.GlowOn = false;
                }
            }
        }
    }

    private bool BuildStand(float s)
    {
        try
        {
            Transform cam = HudManager.Instance.transform.parent;
            if (!cam) return false;

            Camera c = _cam = cam.GetComponent<Camera>();
            if (c)
            {
                _halfH = c.orthographicSize;
                _halfW = _halfH * c.aspect;
            }

            EnsureSprites();
            float u = PxToUnits;

            _root = new GameObject("AudienceSeats") { layer = Layer };
            _root.transform.SetParent(cam, false);
            Transform t = _root.transform;

            float w = BenchWidth(s) * u;
            float stepH = 14f * s * u;
            float lift = RowLift * s * u;

            // ひな壇: 後ろの段 → 前の段 (段の上面は明るく、側面は暗く)
            Color side = FxMath.Rgba(0.16f, 0.17f, 0.22f, 0.95f);
            Color topFace = FxMath.Rgba(0.36f, 0.38f, 0.46f, 1f);
            Color backTop = FxMath.Rgba(0.3f, 0.32f, 0.4f, 1f);
            Quad(t, _white, side, w * 0.5f, lift * 0.5f, w * 0.92f, lift + stepH, 3f);
            Quad(t, _white, backTop, w * 0.5f, lift, w * 0.92f, stepH * 0.45f, 2.9f);
            Quad(t, _white, side, w * 0.5f, -stepH * 0.5f, w, stepH * 2f, 2.5f);
            Quad(t, _white, topFace, w * 0.5f, 0f, w, stepH * 0.45f, 2.4f);

            // 座席の背もたれ (前列は赤、後列は青のスタジアムの椅子)
            float seatW = 40f * s * u, seatH = 34f * s * u;
            for (int i = 0; i < MaxSeats; i++)
            {
                float x = (SeatX(i, s) - _pos.x) * u;
                float y = -(SeatFloor(i, s) - _pos.y) * u + seatH * 0.55f;
                Color seatCol = i < FrontSeats ? FxMath.Rgba(0.78f, 0.18f, 0.2f, 1f) : FxMath.Rgba(0.2f, 0.36f, 0.78f, 1f);
                Quad(t, _round, FxMath.Rgba(0f, 0f, 0f, 0.6f), x, y - 2f * s * u, seatW * 1.1f, seatH * 1.08f, i < FrontSeats ? 2.2f : 2.8f);
                Quad(t, _round, seatCol, x, y, seatW, seatH, i < FrontSeats ? 2.1f : 2.7f);
            }

            // 前の手すり (細い棒と支柱)
            Color rail = FxMath.Rgba(0.82f, 0.84f, 0.9f, 1f);
            float railY = -stepH * 1.5f;
            Quad(t, _white, rail, w * 0.5f, railY + 18f * s * u, w, 4f * s * u, -3f);
            for (int k = 0; k <= 5; k++)
                Quad(t, _white, rail, w * k / 5f, railY + 9f * s * u, 3f * s * u, 18f * s * u, -3f);

            // 前の札
            MakeSign(t, w, railY, s, u);

            _builtWorldScale = s;
            _rootAlive = true;
            return true;
        }
        catch (Exception e)
        {
            Utils.ThrowException(e);
            if (_root) Object.Destroy(_root);
            _root = null;
            _rootAlive = false;
            return false;
        }
    }

    private void MakeSign(Transform t, float w, float railY, float s, float u)
    {
        try
        {
            float sw = 120f * s * u, sh = 26f * s * u;
            float sx = w * 0.5f, sy = railY - sh * 0.5f - 2f * s * u;
            Quad(t, _white, FxMath.Rgba(1f, 0.8f, 0.18f, 1f), sx, sy, sw + 4f * s * u, sh + 4f * s * u, -3.2f);
            Quad(t, _white, FxMath.Rgba(0.07f, 0.09f, 0.2f, 1f), sx, sy, sw, sh, -3.3f);

            TextMeshPro src = HudManager.Instance.KillButton ? HudManager.Instance.KillButton.buttonLabelText : null;
            if (!src) return;

            GameObject go = Object.Instantiate(src.gameObject, t);
            go.DestroyTranslator();
            go.name = "Sign";
            go.layer = Layer;
            go.transform.localPosition = FxMath.V3(sx, sy, -3.4f);
            go.transform.localScale = FxMath.V3(1f, 1f, 1f);
            TextMeshPro text = go.GetComponent<TextMeshPro>();
            text.enableAutoSizing = false;
            text.fontSize = sh * 6.5f;
            text.enableWordWrapping = false;
            text.alignment = TextAlignmentOptions.Center;
            text.color = FxMath.Rgba(1f, 0.85f, 0.3f, 1f);
            text.text = Translator.GetString("YouTubeChatSeatsSign");
            go.GetComponent<RectTransform>().sizeDelta = FxMath.V2(sw, sh);
        }
        catch (Exception e) { Utils.ThrowException(e); }
    }

    private void SpawnCrew(Seat seat, float s, float now)
    {
        try
        {
            if (!_rootAlive || !HudManager.Instance.IntroPrefab || !HudManager.Instance.IntroPrefab.PlayerPrefab) return;

            PoolablePlayer crew = Object.Instantiate(HudManager.Instance.IntroPrefab.PlayerPrefab, _root.transform);
            seat.Crew = crew;
            var outfit = new NetworkedPlayerInfo.PlayerOutfit
            {
                PlayerName = "", ColorId = (byte)seat.ColorId, HatId = seat.HatId, SkinId = "skin_None", VisorId = "visor_EmptyVisor", PetId = "pet_EmptyPet",
                NamePlateId = "nameplate_NoPlate"
            };
            crew.UpdateFromPlayerOutfit(outfit, PlayerMaterial.MaskType.None, false, false);
            crew.ToggleName(false);
            crew.TogglePet(false);
            float k = seat.K = CrewHeight(s) * PxToUnits / CrewUnitHeight;
            crew.transform.localScale = FxMath.V3(k, k, 1f);
            SetLayer(crew.transform);

            var glow = new GameObject("Glow") { layer = Layer };
            glow.transform.SetParent(_root.transform, false);
            seat.GlowSr = null;
            float gs = CrewHeight(s) * 2.2f * PxToUnits;
            glow.transform.localScale = FxMath.V3(gs, gs, 1f);
            SpriteRenderer sr = glow.AddComponent<SpriteRenderer>();
            sr.sprite = _glow;
            sr.color = FxMath.Rgba(1f, 1f, 1f, 0f);
            seat.GlowSr = sr;
        }
        catch (Exception e)
        {
            Utils.ThrowException(e);
            DestroySeatObjects(seat);
            seat.RetrySpawnAt = now + 5f;
        }
    }

    // PoolablePlayer を大きさ 1 で置いた時の、帽子を除いた体の高さ (ゲームの単位)
    private const float CrewUnitHeight = 1.6f;

    private static void DestroySeatObjects(Seat seat)
    {
        try
        {
            if (seat.Crew) Object.Destroy(seat.Crew.gameObject);
            if (seat.GlowSr) Object.Destroy(seat.GlowSr.gameObject);
        }
        catch (Exception e) { Utils.ThrowException(e); }

        seat.Crew = null;
        seat.GlowSr = null;
    }

    private void DestroyStand()
    {
        for (int i = 0; i < MaxSeats; i++)
            if (_seats[i] != null) DestroySeatObjects(_seats[i]);

        foreach (Leaver l in _leavers)
            if (l.Crew) Object.Destroy(l.Crew.gameObject);
        _leavers.Clear();

        if (_rootAlive && _root) Object.Destroy(_root);
        _root = null;
        _rootAlive = false;
    }

    private static SpriteRenderer Quad(Transform parent, Sprite sprite, Color color, float x, float y, float w, float h, float z)
    {
        var go = new GameObject("Part") { layer = Layer };
        go.transform.SetParent(parent, false);
        go.transform.localPosition = FxMath.V3(x, y, z);
        go.transform.localScale = FxMath.V3(w, h, 1f);
        SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.color = color;
        return sr;
    }

    private static void SetLayer(Transform t)
    {
        t.gameObject.layer = Layer;
        for (int i = 0; i < t.childCount; i++) SetLayer(t.GetChild(i));
    }

    private static void EnsureSprites()
    {
        _white ??= MakeSprite(4, 4, (_, _) => 1f);
        // 角の丸い背もたれ
        _round ??= MakeSprite(32, 32, (x, y) =>
        {
            float dx = FxMath.Max(FxMath.Abs(x - 15.5f) - 9.5f, 0f), dy = FxMath.Max(FxMath.Abs(y - 15.5f) - 9.5f, 0f);
            return FxMath.Clamp(6.5f - FxMath.Sqrt(dx * dx + dy * dy), 0f, 1f);
        });
        _glow ??= MakeSprite(64, 64, (x, y) =>
        {
            float dx = (x - 31.5f) / 32f, dy = (y - 31.5f) / 32f;
            float d = 1f - FxMath.Sqrt(dx * dx + dy * dy);
            return d <= 0f ? 0f : d * d;
        });
    }

    // 白い絵を 1 回だけ作る (色は SpriteRenderer.color で塗る)。大きさ 1 で 1 単位
    private static Sprite MakeSprite(int w, int h, Func<float, float, float> alpha)
    {
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        var px = new Color32[w * h];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
            px[y * w + x] = FxMath.Rgba32(255, 255, 255, (byte)(FxMath.Clamp(alpha(x, y), 0f, 1f) * 255f));
        tex.SetPixels32(px);
        tex.Apply(false, true);
        tex.hideFlags = HideFlags.HideAndDontSave;
        Sprite sp = Sprite.Create(tex, new Rect(0f, 0f, w, h), FxMath.V2(0.5f, 0.5f), FxMath.Max(w, h));
        sp.hideFlags = HideFlags.HideAndDontSave;
        return sp;
    }

    // ── 吹き出し (IMGUI) ───────────────────────────────────────

    private void OnGUI()
    {
        try
        {
            if (CleanShot.On) return;
            if (!_shown) return;

            float s = Scale;
            EnsureStyles(s);

            if (!_posInitialized)
            {
                _posInitialized = true;
                _pos = FxMath.V2(16f * s, Il2Direct.ScreenHeight * 0.88f);
            }

            float bw = BenchWidth(s);
            _pos = FxMath.V2(FxMath.Clamp(_pos.x, 0f, FxMath.Max(0f, Screen.width - bw)), FxMath.Clamp(_pos.y, FxMath.Min(200f * s, Screen.height), Screen.height));

            HandleDrag(s);
            if (Event.current.type != EventType.Repaint) return;

            DrawBubbles(s, Time.realtimeSinceStartup);
        }
        catch (Exception e) { Utils.ThrowException(e); }
    }

    // ひな壇を掴んで動かす。gameplay へイベントが漏れないよう e.Use()
    // 札を動かさずに離す = クリックで、過去のコメントの板を開閉する
    private void HandleDrag(float s)
    {
        Event e = Event.current;
        float w = BenchWidth(s);
        Rect stand = new(_pos.x, _pos.y - (RowLift + 20f) * s, w, (RowLift + 50f) * s);
        Rect sign = SignRect(s);

        switch (e.type)
        {
            case EventType.MouseDown when stand.Contains(e.mousePosition) || sign.Contains(e.mousePosition):
                _dragging = true;
                _didDrag = false;
                _pressOnSign = sign.Contains(e.mousePosition);
                _dragOffset = e.mousePosition - _pos;
                e.Use();
                break;
            case EventType.MouseDrag when _dragging:
                Vector2 np = e.mousePosition - _dragOffset;
                if ((np - _pos).sqrMagnitude > 36f) _didDrag = true;
                _pos = FxMath.V2(FxMath.Clamp(np.x, 0f, FxMath.Max(0f, Screen.width - w)), FxMath.Clamp(np.y, FxMath.Min(200f * s, Screen.height), FxMath.Max(0f, Screen.height - 40f * s)));
                e.Use();
                break;
            case EventType.MouseUp when _dragging:
                _dragging = false;
                if (_pressOnSign && !_didDrag) YouTubeChatBubble.Toggle();
                e.Use();
                break;
        }
    }

    // 前の札 (MakeSign) の画面上の範囲。手すりの下に吊ってある
    private Rect SignRect(float s)
    {
        float cx = _pos.x + BenchWidth(s) * 0.5f, cy = _pos.y + 36f * s;
        return new Rect(cx - 62f * s, cy - 15f * s, 124f * s, 30f * s);
    }

    // 過去のコメントの板を置く場所: 台の左右の端 (px) と、下端 (札の下端) と、台の幅
    public static bool TryGetHistoryAnchor(out float benchLeft, out float benchRight, out float bottom, out float width)
    {
        benchLeft = benchRight = bottom = width = 0f;
        AudienceSeats seats = Instance;
        if (!seats || !seats._shown || !seats._posInitialized) return false;

        float s = Scale;
        width = seats.BenchWidth(s);
        benchLeft = seats._pos.x;
        benchRight = seats._pos.x + width;
        bottom = seats.SignRect(s).yMax;
        return true;
    }

    private void DrawBubbles(float s, float now)
    {
        // 出ている吹き出しを新しい順に並べ、新しいものほど下 (頭のすぐ上) に積む。上限を超えた古いものは消す
        int n = 0;
        for (int i = 0; i < MaxSeats; i++)
        {
            Seat seat = _seats[i];
            if (seat == null || now < seat.BubbleStart || now - seat.BubbleStart >= BubbleSeconds) continue;
            int k = n++;
            while (k > 0 && _seats[_order[k - 1]].BubbleStart < seat.BubbleStart) { _order[k] = _order[k - 1]; k--; }
            _order[k] = i;
        }

        if (n > MaxBubbles) n = MaxBubbles;
        if (n == 0) return;

        float dt = FxMath.Min(Time.unscaledDeltaTime, 0.1f);
        float gap = 8f * s;
        float cursor = _pos.y - RowLift * s - CrewHeight(s) - 24f * s;

        for (int j = 0; j < n; j++)
        {
            Seat seat = _seats[_order[j]];
            Measure(seat, s);
            float target = cursor - BubbleHeight(seat, s);
            cursor = target - gap;

            // 積み直しで位置が変わる時は滑らかに寄せる (押し上げられる見え方)
            if (!seat.HasY) { seat.Y = target + 10f * s; seat.HasY = true; }
            seat.Y += (target - seat.Y) * FxMath.Min(1f, dt * 12f);
        }

        // 古いものから描いて、新しいものを上に重ねる
        for (int j = n - 1; j >= 0; j--)
        {
            int i = _order[j];
            DrawBubble(_seats[i], s, now, CrewX(_seats[i], i, s, now), CrewTop(_seats[i], i, s, now), j == n - 1 && n == MaxBubbles);
        }

        GUI.color = FxMath.Rgba(1f, 1f, 1f, 1f);
    }

    private void Measure(Seat seat, float s)
    {
        if (seat.Measured) return;
        seat.Measured = true;
        float pad = 9f * s;
        float tw = _textStyle.CalcSize(seat.Text).x;
        float nw = _nameStyle.CalcSize(seat.Name).x;
        seat.BubbleW = FxMath.Min(280f * s, FxMath.Max(tw, nw) + pad * 2f);
        seat.TextH = _textStyle.CalcHeight(seat.Text, seat.BubbleW - pad * 2f);
    }

    private static float BubbleHeight(Seat seat, float s) => 9f * s * 1.6f + 18f * s + seat.TextH;

    private void DrawBubble(Seat seat, float s, float now, float cx, float crewTop, bool oldest)
    {
        float pad = 9f * s, nameH = 18f * s, border = 2f * s;

        float t = now - seat.BubbleStart;
        float alpha = t > BubbleSeconds - BubbleFade ? (BubbleSeconds - t) / BubbleFade : 1f;
        // 上限で押し出されかけている一番古いものは薄くする
        if (oldest) alpha *= 0.55f;
        if (alpha <= 0f) return;

        float w = seat.BubbleW, h = BubbleHeight(seat, s);
        float by = seat.Y;
        float bx = FxMath.Clamp(cx - w * 0.5f, 4f * s, Screen.width - w - 4f * s);

        Color edge = seat.Accent;
        float bd = seat.IsCommand ? border * 1.8f : border;

        // 吹き出しから自分の頭まで細い線を下ろす (誰の発言かが分かる)
        float lineTop = by + h;
        float lineBottom = crewTop - 2f * s;
        if (lineBottom > lineTop)
        {
            GUI.color = FxMath.Rgba(edge.r, edge.g, edge.b, alpha * 0.8f);
            GUI.Box(new Rect(cx - 1.5f * s, lineTop, 3f * s, lineBottom - lineTop), GUIContent.none, _whiteStyle);
        }

        GUI.color = FxMath.Rgba(edge.r, edge.g, edge.b, alpha);
        GUI.Box(new Rect(bx - bd, by - bd, w + bd * 2f, h + bd * 2f), GUIContent.none, _whiteStyle);
        GUI.color = FxMath.Rgba(1f, 1f, 1f, alpha);
        GUI.Box(new Rect(bx, by, w, h), GUIContent.none, _whiteStyle);
        GUI.Label(new Rect(bx + pad, by + pad * 0.6f, w - pad * 2f, nameH), seat.Name, _nameStyle);
        GUI.Label(new Rect(bx + pad, by + pad * 0.6f + nameH, w - pad * 2f, seat.TextH), seat.Text, _textStyle);
    }

    private void EnsureStyles(float s)
    {
        if (_whiteStyle != null && FxMath.Abs(_builtScale - s) < 0.001f) return;
        _builtScale = s;

        if (!_whiteTex)
        {
            _whiteTex = new Texture2D(1, 1, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
            _whiteTex.SetPixel(0, 0, FxMath.Rgba(1f, 1f, 1f, 1f));
            _whiteTex.Apply(true, true);
            _whiteTex.hideFlags |= HideFlags.HideAndDontSave;
        }

        _whiteStyle = new GUIStyle();
        _whiteStyle.normal.background = _whiteTex;

        _textStyle = new GUIStyle { fontSize = Math.Max(12, (int)(21f * s + 0.5f)), wordWrap = true, richText = false, alignment = TextAnchor.UpperLeft };
        _textStyle.normal.textColor = FxMath.Rgba(0.08f, 0.08f, 0.1f, 1f);
        _nameStyle = new GUIStyle { fontSize = Math.Max(10, (int)(14f * s + 0.5f)), fontStyle = FontStyle.Bold, richText = false, alignment = TextAnchor.UpperLeft, clipping = TextClipping.Clip };
        _nameStyle.normal.textColor = FxMath.Rgba(0.42f, 0.42f, 0.48f, 1f);

        foreach (Seat seat in _seats)
            if (seat != null) seat.Measured = false;
    }
}
