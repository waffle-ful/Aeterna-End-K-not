using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using AmongUs.Data;
using EndKnot.Patches.CalamityMenu;
using HarmonyLib;
using UnityEngine;

namespace EndKnot.Modules.MapAtmosphere;

// The Airship の「誰も乗っていない飛行船の、深夜の夢」演出 (各クライアントのローカル描画のみ・送信ゼロ)。
//   常時 — 空を褪せた色に置き換え、画面全体をうっすら白っぽく色あせさせ、照明を一段落としたように少し暗くする。雲は止まったまま動かない。
//          空は試合が長引くにつれて、くすんだ灰紫から錆びた赤茶の夕暮れへ沈んでいく。朽ちた船が視界に入り込む間は赤黒くなる。
//          バニラの部屋ごとの環境音は止め、空調と電源の低いうなりと、ときどき遠くで閉まる重い扉の音だけにする。
//   廃墟 — 視界の外 (影の中) だけ、船が朽ちている (AirshipRuin)。光が届くと元のきれいな船に戻る。
//   破損 — 長い平穏のあとに、画面が一瞬止まって横に裂け、船じゅうの床と壁と空がありえない色に飛ぶ。
//          色は段飛びで何度か入れ替わったあと引いていき、代わりに朽ちた船が視界の中まで入り込む。
//          その間も過去の一瞬が細い帯になって紛れ込む。最後は裂け目と一緒に一瞬で元へ戻る。雲が急に流れ出す・部屋名が化ける/別の部屋の名前になる、のいくつかが重なり、BGM は毎回揺れる。
//          試合が長引くほど頻度と強さを上げる (一定の調子が続くと背景に慣れてしまうため)。
//          画面を止める・裂く・色がにじむ演出は点滅なので、画面フラッシュを切っている人には出さず、色はゆっくり染めて戻す。
// 染めるのは部屋の一枚絵 (layer 9) だけ。コンソールやドア (layer 12) は Outline シェーダの強調表示と喧嘩するので触らない。
// 船全体を同時に染め、画面の写しも自分の画面だけなので、他人の位置や出来事は何も漏れない。キルや死体にも反応しない。
// The Airship の空も画像ではなくカメラの backgroundColor (SolidColor) なので、退出時に必ず元へ戻す。
public static class AirshipLiminal
{
    private const int RoomArtLayer = 9;
    private const float WashZ = -35f;
    private const float TearZ = -36f;

    private static readonly Color FadedBg = new(0.74f, 0.68f, 0.72f, 1f);
    // 試合の半ばと終わりの空。褪せた薄紫 → くすんだ灰紫 → 錆びた赤茶の夕暮れ。
    private static readonly Color DuskBg = new(0.5f, 0.44f, 0.52f, 1f);
    private static readonly Color RotBg = new(0.36f, 0.22f, 0.2f, 1f);
    private static readonly Color InvadedBg = new(0.22f, 0.05f, 0.06f, 1f);
    private static readonly Color WashColor = new(1f, 0.95f, 0.86f, 0.08f);
    private static readonly Color InvadeWash = new(0.5f, 0.05f, 0.05f, 0.12f);
    // 照明を落とす黒い膜の濃さ。影の中も外も同じだけ暗くする。
    private const float DimAlpha = 0.2f;
    private static float _dimAlpha = DimAlpha;

    // 床と壁に掛ける「ありえない色」。テクスチャに乗算されるので、彩度の高い色ほど狂って見える。
    private static readonly Color[] WrongColors =
    [
        new(0.3f, 1f, 0.45f, 1f),
        new(1f, 0.3f, 0.75f, 1f),
        new(0.35f, 0.55f, 1f, 1f),
        new(1f, 0.85f, 0.2f, 1f),
        new(1f, 0.25f, 0.2f, 1f),
        new(0.55f, 0.3f, 1f, 1f)
    ];

    private static readonly Color[] WrongSkies =
    [
        new(0.85f, 0.1f, 0.55f, 1f),
        new(0.1f, 0.75f, 0.35f, 1f),
        new(0.05f, 0.03f, 0.08f, 1f),
        new(0.95f, 0.45f, 0.1f, 1f)
    ];

    // 部屋名を化けさせる文字。日本語フォントに確実にある全角カタカナと ASCII だけを使う。
    private const string GarbleChars = "ヲァィゥェォャュョッーヰヱヴ#%&?01";

    private static GameObject _root;
    private static ShipStatus _ship;
    private static ShipStatus _failedShip;
    private static Camera _cam;
    private static Color _origBg;
    private static bool _bgOverridden;
    private static SpriteRenderer _wash, _dim;
    private static SpriteRenderer[] _tears;
    private static Sprite _solidSprite;

    private sealed class RoomArt
    {
        public SpriteRenderer[] Renderers;
        public Color[] Original;
    }

    private static readonly Dictionary<SystemTypes, RoomArt> Rooms = [];
    private static SystemTypes[] _roomIds = [];

    private static CloudGenerator[] _clouds = [];

    private static float _fadeIn;
    private const float FadeInSeconds = 3f;
    private static float _age; // 組み立てからの経過秒 (会議中は止める)

    private enum Phase { None, Rising, Holding, Falling }

    // 破損の進み具合。_amount は染まり具合 (0=元の色・1=狂った色)。
    private static Phase _phase;
    private static float _phaseTime, _hold, _nextEvent, _amount;
    private static Color _wrong;
    private static bool _cloudEvent;
    private static Color _wrongSky;
    private static float _colorStep; // 染まり始めの段飛び (次に色を入れ替えるまで)
    private static float _appliedAmount = -1f;
    private static Color _appliedWrong;

    // 画面の写し。破損の始まりに1コマ撮り、止まった画面・横に裂けた帯・色のにじみとして重ねる。
    // Texture2D は画面寸法が変わるまで使い回し、船を出る時に捨てる (1080p で約 6MB)。
    private const float CaptureZ = -35.5f;
    private const int BandCount = 10;
    private static Texture2D _shot;
    private static Sprite _shotSprite;
    private static Sprite[] _bandSprites;
    private static float[] _bandCenters; // 画面の高さに対する帯の中心 (-0.5〜0.5)
    private static SpriteRenderer _freeze;
    private static SpriteRenderer[] _ghosts, _bands;
    private static bool _capturing, _shotReady;

    // 写しを見せている時間。_burstKind 0=破損の始まり・1=染まっている間の細い紛れ込み・2=戻る瞬間。
    private static float _burstAge = -1f, _burstLen, _burstStep, _microTimer;
    private static bool _burstHidden; // 裂け目の描画物を全部消してあるか
    private static float _shownW, _shownH, _shownDim = -1f, _sfx = 1f, _sfxTimer; // 前回反映したカメラ寸法・暗さ、効果音の音量 (0.5 秒ごとに読む)
    private static float _shownPitch = -1f, _shownVolume = -1f; // うなりに前回反映した高さ・大きさ
    private static bool _humMissing; // うなりの音源がまだ読めていない (間隔を空けて取り直す)
    private static int _burstKind;
    private static bool _microRecolor;
    private static Color _microSaved;

    // バニラの環境音 (部屋ごとのうなり・風など)。止めた部品を覚えておき、演出を外す時に戻す。
    private static readonly List<MonoBehaviour> MutedAmbience = [];
    private static readonly List<AudioClip> MutedClips = [];
    private static float _muteTimer;

    // 画面を横切る帯状のノイズ (破損が始まる瞬間だけ)。
    private static float _tearAge = -1f, _tearStep;

    // 部屋名の化け。_garbleMode 1=文字化けが揺れる・2=別の部屋の名前になる。
    private static int _garbleMode;
    private static float _garbleTime, _garbleStep;
    private static PlainShipRoom _garbleRoom;
    private static string _garbleOrig, _garbleShown;
    private static readonly StringBuilder Garble = new();
    private static int _forcedGarble = -1;

    // BGM の揺れ。鳴っている BGM の AudioSource を借りて音程・再生位置・ミュートだけを一時的に動かし、終われば必ず戻す。
    //   Skip — レコードの針飛び: 同じ一瞬を数回繰り返す。Sag — テープが伸びたように音程が沈んで戻る。Drop — 音が途切れ途切れになる。
    private enum Stutter { None, Skip, Sag, Drop }
    private static Stutter _stutter;
    private static AudioSource _stutterSrc;
    private static float _stutterTime, _stutterLen, _skipAnchor, _skipLen, _skipStep, _dropStep, _stutterTimer;
    private static int _skipsLeft;
    private static bool _stutterPending; // 破損の始まりに BGM を掴めなかった

    private const float HumVolume = 0.22f;
    private static AudioSource _humSrc, _sfxSrc;
    private static float _humRetry, _doorTimer;
    private static readonly Dictionary<string, AudioClip> Clips = [];

    public static void Tick()
    {
        try
        {
            ShipStatus ship = ShipStatus.Instance;
            // イントロ (役職発表) 中は組み立てない。明けてからフェードインさせる。
            bool onShip = Main.MapAtmosphere?.Value == true && ship && GameStates.IsInGame && Main.CurrentMap == MapNames.Airship
                          && HudManager.InstanceExists;
            // 組み立て済みならイントロの有無は見ない (毎フレームの bool 読みを 1 本減らす)。
            bool want = onShip && (_root is not null || !HudManager.Instance.IsIntroDisplayed);

            if (!_root)
            {
                if (_root is not null) Teardown(); // 船ごと破棄された (ロビー復帰)。背景色を戻す
                if (!want)
                {
                    // イントロ中に素材を数枚ずつ先読みして、明けた瞬間の組み立てで一度に全部デコードしない。
                    if (onShip && ship != _failedShip)
                    {
                        var pre = AllocProbe.Now();
                        AirshipRuin.Preload();
                        AllocProbe.Mark("atmo.preload", pre);
                    }
                    else if (AirshipRuin.Preloaded) AirshipRuin.ReleaseSprites(); // 組み立てる前に試合が終わった (先読み分を残さない)
                    return;
                }

                if (ship == _failedShip) return;
                Build(ship);
                return;
            }

            if (!want || ship != _ship)
            {
                Teardown();
                return;
            }

            var alloc = AllocProbe.Now();
            Animate(Time.deltaTime);
            AllocProbe.Mark("atmo.airship", alloc);
        }
        catch (Exception e)
        {
            Utils.ThrowException(e);
            _failedShip = ShipStatus.Instance;
            Teardown();
        }
    }

    private static void Build(ShipStatus ship)
    {
        _cam = Camera.main;
        if (!_cam) return;

        _ship = ship;
        _failedShip = null;

        _root = new GameObject("EK_AirshipLiminal") { layer = 0 };
        _root.transform.SetParent(ship.transform, false);
        // 船に縮尺が掛かっていると画面を覆う板が足りなくなるので、ルートで打ち消す。
        Vector3 ls = ship.transform.lossyScale;
        _root.transform.localScale = FxMath.V3(1f / ls.x, 1f / ls.y, 1f / ls.z);

        // 入場のたびに作り直すと Texture2D が孤立して残るので、1枚を使い回す (破棄済みなら作り直す)。
        if (!_solidSprite)
        {
            _solidSprite = CalamitySky.MakeSolidSprite(Color.white);
            _solidSprite.hideFlags |= HideFlags.HideAndDontSave;
            _solidSprite.texture.hideFlags |= HideFlags.HideAndDontSave;
        }

        _dim = MakeSolid("Dim", WashZ + 0.05f);
        _dim.color = FxMath.Rgba(0f, 0f, 0f, 0f);
        _wash = MakeSolid("Wash", WashZ);
        _wash.color = FxMath.Rgba(WashColor.r, WashColor.g, WashColor.b, 0f);
        _tears = new SpriteRenderer[4];
        for (int i = 0; i < _tears.Length; i++)
        {
            _tears[i] = MakeSolid("Tear", TearZ - i * 0.1f);
            _tears[i].enabled = false;
        }

        _freeze = MakeSolid("Freeze", CaptureZ);
        _freeze.enabled = false;
        _ghosts = new SpriteRenderer[2];
        for (int i = 0; i < _ghosts.Length; i++)
        {
            _ghosts[i] = MakeSolid("Ghost", CaptureZ - 0.1f - i * 0.05f);
            _ghosts[i].enabled = false;
        }

        _bands = new SpriteRenderer[BandCount];
        for (int i = 0; i < _bands.Length; i++)
        {
            _bands[i] = MakeSolid("Band", CaptureZ - 0.3f - i * 0.01f);
            _bands[i].enabled = false;
        }

        CollectRooms(ship);
        AirshipRuin.Build(ship);
        CollectClouds();
        MuteVanillaAmbience();
        SetupAudio();

        _origBg = _cam.backgroundColor;
        _bgOverridden = true;

        _fadeIn = 0f;
        _age = 0f;
        _phase = Phase.None;
        _amount = 0f;
        _appliedAmount = -1f;
        _tearAge = -1f;
        _burstAge = -1f;
        _capturing = _shotReady = false;
        _muteTimer = 1f;
        _garbleMode = 0;
        _nextEvent = FxMath.Range(45f, 80f);
        _doorTimer = FxMath.Range(20f, 40f);
        _humRetry = 0f;
        _shownW = _shownH = 0f;
        _shownDim = _shownPitch = _shownVolume = -1f;
        _sfxTimer = 0f;
        _burstHidden = false;
        _stutter = Stutter.None;
        _stutterPending = false;
        _stutterTimer = FxMath.Range(70f, 150f);

        Animate(0f);
        Logger.Info($"AirshipLiminal built rooms={Rooms.Count} clouds={_clouds.Length} scale={ls.x:0.##}", "AirshipLiminal");
    }

    private static SpriteRenderer MakeSolid(string name, float z)
    {
        var go = new GameObject(name) { layer = 0 }; // Main Camera の cullingMask に Default(0) は含まれる
        go.transform.SetParent(_root.transform, false);
        go.transform.localPosition = FxMath.V3(0f, 0f, z);
        SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = _solidSprite;
        return sr;
    }

    private static void CollectRooms(ShipStatus ship)
    {
        Rooms.Clear();
        int total = 0;

        foreach (PlainShipRoom room in ship.AllRooms)
        {
            if (!room || Rooms.ContainsKey(room.RoomId)) continue;

            var list = new List<SpriteRenderer>();
            foreach (SpriteRenderer sr in room.GetComponentsInChildren<SpriteRenderer>(true))
                if (sr && sr.gameObject.layer == RoomArtLayer) list.Add(sr);

            if (list.Count == 0) continue;

            var art = new RoomArt { Renderers = list.ToArray(), Original = new Color[list.Count] };
            for (int i = 0; i < art.Renderers.Length; i++) art.Original[i] = art.Renderers[i].color;
            Rooms[room.RoomId] = art;
            total += list.Count;
        }

        _roomIds = new SystemTypes[Rooms.Count];
        Rooms.Keys.CopyTo(_roomIds, 0);
        Logger.Info($"AirshipLiminal room art renderers={total}", "AirshipLiminal");
    }

    // 空の雲は CloudGenerator (上下2層) が毎フレーム動かしている。Direction を書き換えても流れている雲の速さは変わらず、
    // 逆向きにすると雲が画面外へ消えるだけなので、コンポーネントごと止めて空を静止させる。
    private static void CollectClouds()
    {
        var gens = UnityEngine.Object.FindObjectsOfType<CloudGenerator>();
        _clouds = new CloudGenerator[gens.Length];
        for (int i = 0; i < gens.Length; i++) _clouds[i] = gens[i];
        SetCloudsMoving(false);
    }

    private static void SetCloudsMoving(bool moving)
    {
        foreach (CloudGenerator g in _clouds)
            if (g) g.enabled = moving;
    }

    private static void Animate(float dt)
    {
        if (!_cam)
        {
            _cam = Camera.main;
            if (!_cam) return;
        }

        Vector3 camPos = _cam.transform.position;
        _root.transform.position = FxMath.V3(camPos.x, camPos.y, camPos.z);

        // カメラの写す範囲はここで 1 回だけ読み、下の朽ち演出へ渡す (値型 getter は 1 本ごとに il2cpp 側のゴミになる)。
        float h = _cam.orthographicSize * 2f;
        float w = h * _cam.aspect;
        if (w != _shownW || h != _shownH)
        {
            _shownW = w;
            _shownH = h;
            _wash.transform.localScale = FxMath.V3(w * 1.3f, h * 1.3f, 1f);
            _dim.transform.localScale = FxMath.V3(w * 1.3f, h * 1.3f, 1f);
        }

        float dimA = _dimAlpha * _fadeIn;
        if (dimA != _shownDim)
        {
            _shownDim = dimA;
            _dim.color = FxMath.Rgba(0f, 0f, 0f, dimA);
        }

        _fadeIn = FxMath.MoveTowards(_fadeIn, 1f, dt / FadeInSeconds);
        // 幽霊には、朽ちた船が視界に入り込んだ時の見た目 (赤い膜・赤い空・赤い影) をずっと見せる。幽霊かどうかは朽ち演出側が 0.25 秒ごとに読む。
        bool meeting = MeetingHud.Instance;
        float invade = meeting ? 0f : AirshipRuin.Ghost ? 1f : Invade;
        // 朽ちた船が視界に入り込んでいる間は、色あせの膜を血のような赤に寄せて濃くする。
        float red = invade;
        _wash.color = FxMath.Rgba(
            FxMath.Lerp(WashColor.r, InvadeWash.r, red),
            FxMath.Lerp(WashColor.g, InvadeWash.g, red),
            FxMath.Lerp(WashColor.b, InvadeWash.b, red),
            FxMath.Lerp(WashColor.a, InvadeWash.a, red) * _fadeIn);

        if (meeting)
        {
            // 会議中は全部元に戻して時計ごと止める。明けたら続きから。
            if (_phase != Phase.None) EndEvent();
            HideBurst();
            EndStutter();
        }
        else
        {
            _age += dt;
            UpdateCorruption(dt);
            UpdateStutter(dt);
            UpdateTear(dt, w, h);
            UpdateBurst(dt, w, h);
            UpdateGarble(dt);
        }

        UpdateSky(invade);
        AirshipRuin.Animate(dt, _fadeIn, invade, Decay, _cam, w, h, camPos.x, camPos.y);
        UpdateAudio(dt, meeting);
    }

    private static void UpdateSky(float inv)
    {
        float d = Decay;
        float half = FxMath.Clamp01(d * 2f), late = FxMath.Clamp01(d * 2f - 1f);
        float sr = FxMath.Lerp(FxMath.Lerp(FadedBg.r, DuskBg.r, half), RotBg.r, late);
        float sg = FxMath.Lerp(FxMath.Lerp(FadedBg.g, DuskBg.g, half), RotBg.g, late);
        float sb = FxMath.Lerp(FxMath.Lerp(FadedBg.b, DuskBg.b, half), RotBg.b, late);
        sr = FxMath.Lerp(sr, InvadedBg.r, inv);
        sg = FxMath.Lerp(sg, InvadedBg.g, inv);
        sb = FxMath.Lerp(sb, InvadedBg.b, inv);

        float r = FxMath.Lerp(_origBg.r, sr, _fadeIn);
        float g = FxMath.Lerp(_origBg.g, sg, _fadeIn);
        float b = FxMath.Lerp(_origBg.b, sb, _fadeIn);

        if (_phase != Phase.None)
        {
            float t = _amount * TintKeep;
            r = FxMath.Lerp(r, _wrongSky.r, t);
            g = FxMath.Lerp(g, _wrongSky.g, t);
            b = FxMath.Lerp(b, _wrongSky.b, t);
        }

        _cam.backgroundColor = FxMath.Rgba(r, g, b);
    }

    // 0 (開始直後) → 1 (約7分後)。破損の頻度と強さを上げていく。
    private static float Decay => AirshipRuin.ForcedDecay >= 0f ? AirshipRuin.ForcedDecay : FxMath.Clamp01((_age - 60f) / 360f);

    private static bool Abrupt => Main.MapAtmosphereFlash?.Value == true;

    // ありえない色は破損の入り口だけ。止まり切ったら色は引き、代わりに朽ちた船が視界の中へ入り込む。
    // 画面フラッシュを切っている人には、色が引くのと朽ちるのを 1 秒かけて入れ替える。
    private static float TintKeep => _phase switch
    {
        Phase.Rising => 1f,
        Phase.Holding => Abrupt ? 0f : 1f - FxMath.Clamp01(_phaseTime),
        _ => 0f
    };

    private static float Invade => _phase switch
    {
        Phase.Holding => Abrupt ? 1f : FxMath.Clamp01(_phaseTime),
        Phase.Falling => _amount,
        _ => 0f
    };

    private static void UpdateCorruption(float dt)
    {
        if (_phase == Phase.None)
        {
            if ((_nextEvent -= dt) > 0f) return;
            StartEvent();
            return;
        }

        // 始まりの1コマを撮り終えるまで染めない (写しに狂った色が混ざると「止まった画面」にならない)。
        if (_capturing && _phase == Phase.Rising) return;

        _phaseTime += dt;
        bool abrupt = Abrupt;

        switch (_phase)
        {
            case Phase.Rising:
            {
                if (abrupt)
                {
                    // 段飛び: 色と濃さを数回でたらめに入れ替えてから止まる。
                    const float len = 0.45f;
                    if ((_colorStep -= dt) <= 0f)
                    {
                        _colorStep = FxMath.Range(0.05f, 0.14f);
                        _wrong = WrongColors[FxMath.Range(0, WrongColors.Length)];
                        _wrongSky = WrongSkies[FxMath.Range(0, WrongSkies.Length)];
                        _amount = FxMath.Range(0f, 1f) < 0.3f ? FxMath.Range(0.25f, 0.5f) : 1f;
                    }

                    if (_phaseTime >= len) BeginHold();
                }
                else
                {
                    const float len = 1.4f;
                    _amount = _phaseTime / len;
                    if (_phaseTime >= len) BeginHold();
                }

                break;
            }
            case Phase.Holding:
                _amount = 1f;
                if (abrupt && _burstAge < 0f && !_capturing && (_microTimer -= dt) <= 0f)
                {
                    _microTimer = FxMath.Range(0.6f, 1.6f);
                    BeginBurst(1);
                }

                if (_phaseTime >= _hold)
                {
                    _phase = Phase.Falling;
                    _phaseTime = 0f;
                    if (_cloudEvent) SetCloudsMoving(false);
                    // 戻る瞬間の裂け目は、染まったままの画面を撮ってから一気に戻す。
                    if (abrupt) RequestCapture(2);
                }

                break;
            default:
            {
                if (abrupt)
                {
                    if (_capturing) return;
                    EndEvent();
                    return;
                }

                const float len = 1f;
                _amount = 1f - _phaseTime / len;
                if (_phaseTime >= len)
                {
                    EndEvent();
                    return;
                }

                break;
            }
        }

        _amount = FxMath.Clamp01(_amount);
        ApplyTint(_amount * TintKeep);
    }

    private static void BeginHold()
    {
        _phase = Phase.Holding;
        _phaseTime = 0f;
        _amount = 1f;
        _wrong = WrongColors[FxMath.Range(0, WrongColors.Length)];
        _microTimer = FxMath.Range(0.4f, 1f);
    }

    private static void StartEvent()
    {
        float decay = Decay;
        _wrong = WrongColors[FxMath.Range(0, WrongColors.Length)];
        _wrongSky = WrongSkies[FxMath.Range(0, WrongSkies.Length)];
        _hold = FxMath.Range(2.5f, 4f) + 4f * decay;
        _phase = Phase.Rising;
        _phaseTime = 0f;
        _colorStep = 0f;
        _amount = 0f;

        _cloudEvent = FxMath.Range(0f, 1f) < 0.2f + 0.5f * decay;
        if (_cloudEvent) SetCloudsMoving(true);

        if (Abrupt)
        {
            _tearAge = 0f;
            RequestCapture(0);
        }

        StartGarble(decay);
        PlaySfx("AirshipGlitch", 0.45f, FxMath.Range(0.9f, 1.1f));
        // 破損のたびに必ず BGM も揺らす。鳴っている曲を掴めなければ、破損の間は取り直し続ける。
        _stutterPending = !StartStutter((Stutter)FxMath.Range(1, 4), true);

        Logger.Info($"corrupt begins clouds={_cloudEvent} garble={_garbleMode} abrupt={Abrupt} decay={decay:0.00}", "AirshipLiminal");
    }

    // 手動での即時発火 (ショートカットキー・TestBridge)。garbleMode -1=抽選・0=化けなし・1/2=化け方を固定。
    public static bool DebugTrigger(int garbleMode)
    {
        if (!_root || MeetingHud.Instance) return false;
        if (_phase != Phase.None) EndEvent();
        _forcedGarble = garbleMode;
        StartEvent();
        _forcedGarble = -1;
        return _phase != Phase.None;
    }

    // TestBridge からの即時発火。kind 1=針飛び・2=音程の沈み・3=途切れ。
    public static bool DebugStutter(int kind)
    {
        if (!_root || kind is < 1 or > 3) return false;
        return StartStutter((Stutter)kind, true);
    }

    // TestBridge から照明を落とす膜の濃さを差し替えて見比べる。0=暗くしない。
    public static bool DebugDim(float alpha)
    {
        if (!_root) return false;
        _dimAlpha = FxMath.Clamp01(alpha);
        return true;
    }

    private static void EndEvent()
    {
        _amount = 0f;
        ApplyTint(0f);
        if (_cloudEvent) SetCloudsMoving(false);
        EndGarble();
        HideTears();
        // 戻る瞬間の裂け目 (kind 2) は色が戻った後もしばらく残すので消さない。会議はこの後 HideBurst を呼ぶ。
        if (_burstKind != 2) HideBurst();
        _phase = Phase.None;
        _cloudEvent = false;
        _stutterPending = false;
        float decay = Decay;
        _nextEvent = FxMath.Range(FxMath.Lerp(60f, 22f, decay), FxMath.Lerp(95f, 40f, decay));
    }

    // 船全体を同じ色に染める。一部屋だけだと「その部屋の照明が変わった」ように見えてしまう。
    private static void ApplyTint(float amount)
    {
        if (amount == _appliedAmount && _wrong.r == _appliedWrong.r && _wrong.g == _appliedWrong.g && _wrong.b == _appliedWrong.b) return;
        _appliedAmount = amount;
        _appliedWrong = _wrong;
        foreach (RoomArt art in Rooms.Values) Tint(art, amount);
    }

    private static void Tint(RoomArt art, float amount)
    {
        for (int i = 0; i < art.Renderers.Length; i++)
        {
            SpriteRenderer sr = art.Renderers[i];
            if (!sr) continue;
            Color o = art.Original[i];
            sr.color = FxMath.Rgba(
                FxMath.Lerp(o.r, o.r * _wrong.r, amount),
                FxMath.Lerp(o.g, o.g * _wrong.g, amount),
                FxMath.Lerp(o.b, o.b * _wrong.b, amount),
                o.a);
        }
    }

    // 破損の始まりに 0.3 秒だけ、色の付いた横帯を画面に走らせる。点滅なので画面フラッシュを切っている人には出さない。
    private static void UpdateTear(float dt, float w, float h)
    {
        if (_tearAge < 0f) return;
        _tearAge += dt;

        if (_tearAge >= 0.3f || !Abrupt)
        {
            HideTears();
            return;
        }

        if ((_tearStep -= dt) > 0f) return;
        _tearStep = 0.05f;

        for (int i = 0; i < _tears.Length; i++)
        {
            SpriteRenderer sr = _tears[i];
            bool on = FxMath.Range(0f, 1f) < 0.75f;
            sr.enabled = on;
            if (!on) continue;
            Color c = i % 2 == 0 ? _wrong : WrongSkies[FxMath.Range(0, WrongSkies.Length)];
            sr.color = FxMath.Rgba(c.r, c.g, c.b, FxMath.Range(0.18f, 0.4f));
            float bandH = h * FxMath.Range(0.02f, 0.09f);
            sr.transform.localScale = FxMath.V3(w * 1.3f, bandH, 1f);
            sr.transform.localPosition = FxMath.V3(FxMath.Range(-0.3f, 0.3f), FxMath.Range(-0.5f, 0.5f) * h, TearZ - i * 0.1f);
        }
    }

    private static void HideTears()
    {
        _tearAge = -1f;
        if (_tears == null) return;
        foreach (SpriteRenderer sr in _tears)
            if (sr) sr.enabled = false;
    }

    private static void StartGarble(float decay)
    {
        _garbleMode = 0;
        if (_forcedGarble == 0 || (_forcedGarble < 0 && FxMath.Range(0f, 1f) >= 0.55f + 0.3f * decay)) return;

        HudManager hud = HudManager.Instance;
        if (!hud || !hud.roomTracker || !hud.roomTracker.text) return;

        _garbleRoom = hud.roomTracker.LastRoom;
        if (!_garbleRoom) return;

        _garbleOrig = hud.roomTracker.text.text;
        if (string.IsNullOrEmpty(_garbleOrig)) return;

        _garbleMode = _forcedGarble > 0 ? _forcedGarble : FxMath.Range(0f, 1f) < 0.4f ? 2 : 1;
        _garbleTime = 0f;
        _garbleStep = 0f;

        if (_garbleMode == 2)
        {
            // 居るはずのない部屋の名前を出す。
            SystemTypes other = _garbleRoom.RoomId;
            for (int i = 0; i < 6 && other == _garbleRoom.RoomId && _roomIds.Length > 1; i++)
                other = _roomIds[FxMath.Range(0, _roomIds.Length)];
            _garbleShown = Translator.GetString($"{other}");
        }
    }

    private static void UpdateGarble(float dt)
    {
        if (_garbleMode == 0) return;

        HudManager hud = HudManager.Instance;
        if (!hud || !hud.roomTracker || !hud.roomTracker.text || hud.roomTracker.LastRoom != _garbleRoom)
        {
            // 部屋を出た = RoomTracker が新しい名前を書いたので、こちらは戻さずに手を引く。
            _garbleMode = 0;
            return;
        }

        _garbleTime += dt;
        if (_garbleMode == 1 && _garbleTime >= 1.2f)
        {
            EndGarble();
            return;
        }

        if (_garbleMode == 1 && (_garbleStep -= dt) <= 0f)
        {
            _garbleStep = 0.12f;
            Garble.Clear();
            foreach (char c in _garbleOrig)
                Garble.Append(FxMath.Range(0f, 1f) < 0.45f ? GarbleChars[FxMath.Range(0, GarbleChars.Length)] : c);
            _garbleShown = Garble.ToString();
        }

        if (hud.roomTracker.text.text != _garbleShown) hud.roomTracker.text.text = _garbleShown;
    }

    private static void EndGarble()
    {
        if (_garbleMode == 0) return;
        _garbleMode = 0;

        HudManager hud = HudManager.Instance;
        if (!hud || !hud.roomTracker || !hud.roomTracker.text) return;
        // 化けている最中に部屋を移っていたら、もう RoomTracker が正しい名前を書いている。
        if (hud.roomTracker.LastRoom == _garbleRoom && hud.roomTracker.text.text == _garbleShown)
            hud.roomTracker.text.text = _garbleOrig;
    }

    // 鳴っている BGM を掴めなかった時は false。log=true なら掴めなかった理由を残す。
    private static bool StartStutter(Stutter kind, bool log)
    {
        EndStutter();
        AudioSource src = BGMManager.CurrentSource;
        if (!src || !src.isPlaying || !src.clip)
        {
            if (log) Logger.Info($"bgm stutter {kind} skipped: {(!src ? "no source" : !src.isPlaying ? "not playing" : "no clip")}", "AirshipLiminal");
            return false;
        }

        _stutterSrc = src;
        _stutter = kind;
        _stutterTime = 0f;

        switch (kind)
        {
            case Stutter.Skip:
                _skipAnchor = src.time;
                _skipLen = FxMath.Range(0.1f, 0.22f);
                _skipStep = _skipLen;
                _skipsLeft = FxMath.Range(3, 7);
                break;
            case Stutter.Sag:
                _stutterLen = FxMath.Range(0.9f, 1.5f);
                break;
            default:
                _stutterLen = FxMath.Range(0.5f, 1.1f);
                _dropStep = 0f;
                break;
        }

        Logger.Info($"bgm stutter {kind}", "AirshipLiminal");
        return true;
    }

    private static void UpdateStutter(float dt)
    {
        if (_stutter == Stutter.None)
        {
            if (_stutterPending)
            {
                if (_phase is Phase.Rising or Phase.Holding) _stutterPending = !StartStutter((Stutter)FxMath.Range(1, 4), false);
                else _stutterPending = false;
                return;
            }

            // 破損の合間にも、試合が進んでいればたまに BGM だけが揺れる。
            if ((_stutterTimer -= dt) > 0f) return;
            _stutterTimer = FxMath.Range(70f, 150f);
            if (Decay > 0.2f) StartStutter((Stutter)FxMath.Range(1, 4), true);
            return;
        }

        // BGM が切り替わった (会議明け・クライマックス突入など)。新しい曲には手を出さない。
        // SoundManager は AudioSource を使い回すので、ラッパーではなく中身の同一性で比べる。
        AudioSource cur = BGMManager.CurrentSource;
        if (!_stutterSrc || !cur || _stutterSrc.Pointer != cur.Pointer)
        {
            EndStutter();
            return;
        }

        _stutterTime += dt;

        switch (_stutter)
        {
            case Stutter.Skip:
                if ((_skipStep -= dt) > 0f) return;
                if (_skipsLeft-- <= 0)
                {
                    EndStutter();
                    return;
                }

                _skipStep = _skipLen;
                _stutterSrc.time = FxMath.Clamp(_skipAnchor, 0f, FxMath.Max(0f, _stutterSrc.clip.length - 0.05f));
                break;
            case Stutter.Sag:
            {
                float t = _stutterTime / _stutterLen;
                if (t >= 1f)
                {
                    EndStutter();
                    return;
                }

                _stutterSrc.pitch = t < 0.35f ? FxMath.Lerp(1f, 0.62f, t / 0.35f) : FxMath.Lerp(0.62f, 1f, (t - 0.35f) / 0.65f);
                break;
            }
            default:
                if (_stutterTime >= _stutterLen)
                {
                    EndStutter();
                    return;
                }

                if ((_dropStep -= dt) > 0f) return;
                _dropStep = FxMath.Range(0.06f, 0.18f);
                _stutterSrc.mute = !_stutterSrc.mute;
                break;
        }
    }

    private static void EndStutter()
    {
        if (_stutter == Stutter.None) return;
        _stutter = Stutter.None;
        if (_stutterSrc)
        {
            _stutterSrc.pitch = 1f;
            _stutterSrc.mute = false;
        }

        _stutterSrc = null;
    }

    private static void SetupAudio()
    {
        if (!CustomSoundsManager.AudioPlatformSupported) return;

        // SoundManager 経由だと BGM 側の消音処理が soundPlayers を止めに来るので、独自の AudioSource を船の下に持つ。
        _humSrc = _root.AddComponent<AudioSource>();
        _humSrc.playOnAwake = false;
        _humSrc.spatialBlend = 0f;
        _humSrc.loop = true;
        _humSrc.volume = 0f;
        _humSrc.clip = GetClip("AirshipHum");
        _humMissing = !_humSrc.clip;
        if (!_humMissing) _humSrc.Play();

        _sfxSrc = _root.AddComponent<AudioSource>();
        _sfxSrc.playOnAwake = false;
        _sfxSrc.spatialBlend = 0f;
    }

    private static void UpdateAudio(float dt, bool meeting)
    {
        // 設定の音量は 0.5 秒ごとに読む (設定画面で変えた時に追従すれば足りる)。
        if ((_sfxTimer -= dt) <= 0f)
        {
            _sfxTimer = 0.5f;
            _sfx = DataManager.Settings?.Audio != null ? DataManager.Settings.Audio.SfxVolume : 1f;
        }

        // うなりの部品はルートに付いていて、ルートごと壊れるまで生きている。
        if (_humSrc is not null)
        {
            // 初回の読み込みに失敗しても試合中ずっと無音にならないよう、間隔を空けて取り直す。
            if (_humMissing && (_humRetry -= dt) <= 0f)
            {
                _humRetry = 2f;
                _humSrc.clip = GetClip("AirshipHum");
                _humMissing = !_humSrc.clip;
                if (!_humMissing) _humSrc.Play();
            }

            // 破損中はうなりが少し高く・大きくなる。
            float pitch = 1f + 0.06f * _amount;
            float volume = _sfx * HumVolume * (meeting ? 0.4f : 1f + 0.5f * _amount) * _fadeIn;
            if (pitch != _shownPitch) _humSrc.pitch = _shownPitch = pitch;
            if (volume != _shownVolume) _humSrc.volume = _shownVolume = volume;
        }

        // 部品を止めても、試合中にバニラが鳴らし直すことがあるので、ときどき止め直す。
        if ((_muteTimer -= dt) <= 0f)
        {
            _muteTimer = 1f;
            StopMutedClips();
        }

        if (meeting || (_doorTimer -= dt) > 0f) return;
        _doorTimer = FxMath.Range(35f, 90f);
        PlaySfx("AirshipFarDoor", FxMath.Range(0.3f, 0.55f), FxMath.Range(0.8f, 1.02f));
    }

    private static void PlaySfx(string name, float volume, float pitch)
    {
        if (!_sfxSrc) return;
        AudioClip clip = GetClip(name);
        if (!clip) return;
        float sfx = DataManager.Settings?.Audio != null ? DataManager.Settings.Audio.SfxVolume : 1f;
        _sfxSrc.pitch = pitch;
        _sfxSrc.PlayOneShot(clip, sfx * volume);
    }

    // SFX バンドルを優先し、無ければ埋め込み OGG をデコードする。シーン遷移の UnloadUnusedAssets で消されないよう保護する。
    private static AudioClip GetClip(string name)
    {
        try
        {
            if (SfxBundle.IsEnabled && SfxBundle.TryGetClip(name, out AudioClip bundled)) return bundled;
            if (Clips.TryGetValue(name, out AudioClip cached) && cached) return cached;

            string key = CustomSoundsManager.TryEmbeddedKey($"EndKnot.Resources.Sounds.MapAtmosphere.{name}.ogg");
            if (key == null) return null;

            AudioClip clip = CustomSoundsManager.LoadOGG(key);
            if (clip) clip.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            Clips[name] = clip;
            return clip;
        }
        catch (Exception e)
        {
            Utils.ThrowException(e);
            return null;
        }
    }

    // 破損の瞬間の画面を1コマ撮る。撮れるのはフレームの描画が終わった後なので、コルーチンで待つ。
    private static void RequestCapture(int kind)
    {
        if (_capturing || Main.Instance == null) return;
        _capturing = true;
        try { Main.Instance.StartCoroutine(CaptureCoroutine(kind)); }
        catch (Exception e)
        {
            _capturing = false;
            Utils.ThrowException(e);
        }
    }

    private static IEnumerator CaptureCoroutine(int kind)
    {
        yield return new WaitForEndOfFrame();
        _capturing = false;

        try
        {
            if (!_root || MeetingHud.Instance) yield break;

            int w = Screen.width;
            int h = Screen.height;
            if (w <= 0 || h <= 0) yield break;

            if (!_shot || _shot.width != w || _shot.height != h)
            {
                ReleaseShot();
                _shot = new Texture2D(w, h, TextureFormat.RGB24, false) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
                BuildShotSprites(w, h);
            }

            _shot.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            _shot.Apply(false);
            _shotReady = true;
            BeginBurst(kind);
        }
        catch (Exception e)
        {
            Utils.ThrowException(e);
        }
    }

    // 1コマ全体と、高さのばらばらな横帯を最初に一度だけ作る。中身は ReadPixels で上書きされるので作り直さない。
    private static void BuildShotSprites(int w, int h)
    {
        float ppu = h; // 画面の高さ = 1 ユニット。表示側で画面の高さへ拡大する
        _shotSprite = Sprite.Create(_shot, new Rect(0, 0, w, h), FxMath.V2(0.5f, 0.5f), ppu);
        _shotSprite.hideFlags |= HideFlags.HideAndDontSave;

        var cuts = new float[BandCount + 1];
        cuts[BandCount] = h;
        for (int i = 1; i < BandCount; i++) cuts[i] = FxMath.Range(0f, h);
        Array.Sort(cuts);

        _bandSprites = new Sprite[BandCount];
        _bandCenters = new float[BandCount];
        for (int i = 0; i < BandCount; i++)
        {
            float y0 = (int)cuts[i];
            float bh = FxMath.Max(2f, (int)cuts[i + 1] - y0);
            if (y0 + bh > h) y0 = h - bh;
            _bandSprites[i] = Sprite.Create(_shot, new Rect(0, y0, w, bh), FxMath.V2(0.5f, 0.5f), ppu);
            _bandSprites[i].hideFlags |= HideFlags.HideAndDontSave;
            _bandCenters[i] = (y0 + bh * 0.5f) / h - 0.5f;
        }
    }

    private static void ReleaseShot()
    {
        _shotReady = false;
        if (_bandSprites != null)
        {
            foreach (Sprite sp in _bandSprites)
                if (sp) UnityEngine.Object.Destroy(sp);
        }

        _bandSprites = null;
        _bandCenters = null;
        if (_shotSprite) UnityEngine.Object.Destroy(_shotSprite);
        _shotSprite = null;
        if (_shot) UnityEngine.Object.Destroy(_shot);
        _shot = null;
    }

    private static void BeginBurst(int kind)
    {
        if (!_shotReady || !_freeze || MeetingHud.Instance) return;
        _burstKind = kind;
        _burstAge = 0f;
        _burstStep = 0f;
        _burstHidden = false;
        // 画面をさえぎるのは始まり 0.32 秒・戻り 0.18 秒・紛れ込み 0.1 秒前後まで。キルの瞬間を隠し続けないため。
        _burstLen = kind switch { 0 => 0.32f, 2 => 0.18f, _ => FxMath.Range(0.06f, 0.12f) };

        if (kind == 1 && FxMath.Range(0f, 1f) < 0.5f)
        {
            // 紛れ込みの間だけ、船の色が別の狂った色に飛ぶ。
            _microRecolor = true;
            _microSaved = _wrong;
            _wrong = WrongColors[FxMath.Range(0, WrongColors.Length)];
            ApplyTint(_amount);
        }
    }

    private static void UpdateBurst(float dt, float w, float h)
    {
        if (_burstAge < 0f) return;
        _burstAge += dt;

        if (_burstAge >= _burstLen || !Abrupt || !_shotSprite)
        {
            HideBurst();
            return;
        }

        // 始まりの最初の一瞬だけ、止まった画面がそのまま全面に出る。
        bool frozen = _burstKind == 0 && _burstAge < 0.09f;
        _freeze.enabled = frozen;
        if (frozen)
        {
            _freeze.sprite = _shotSprite;
            _freeze.color = FxMath.Rgba(1f, 1f, 1f);
            _freeze.transform.localScale = FxMath.V3(h, h, 1f);
        }

        if ((_burstStep -= dt) > 0f) return;
        _burstStep = 0.04f;

        // 帯の本数と揺れ幅。紛れ込みは細く少なく、始まりは大きく裂く。
        float bandChance = _burstKind switch { 0 => 0.45f, 2 => 0.35f, _ => 0.15f };
        float shift = _burstKind == 0 ? 0.12f : 0.06f;
        var shown = 0;
        for (int i = 0; i < _bands.Length; i++)
        {
            SpriteRenderer sr = _bands[i];
            // 紛れ込みで1本も出ない回を作らない (出ないと何も起きなかったのと同じ)。
            bool on = FxMath.Range(0f, 1f) < bandChance || (shown == 0 && i == _bands.Length - 1);
            sr.enabled = on;
            if (!on) continue;
            shown++;
            sr.sprite = _bandSprites[i];
            sr.color = FxMath.Rgba(1f, 1f, 1f);
            float dx = FxMath.Range(0.015f, shift) * (FxMath.Range(0, 2) == 0 ? -1f : 1f);
            sr.transform.localScale = FxMath.V3(h, h, 1f);
            sr.transform.localPosition = FxMath.V3(dx * w, _bandCenters[i] * h, CaptureZ - 0.3f - i * 0.01f);
        }

        // 色のにじみ: 赤と青緑に染めた写しを左右へ少しずらして薄く重ねる。
        bool ghost = _burstKind != 1 && FxMath.Range(0f, 1f) < 0.7f;
        for (int i = 0; i < _ghosts.Length; i++)
        {
            SpriteRenderer sr = _ghosts[i];
            sr.enabled = ghost;
            if (!ghost) continue;
            sr.sprite = _shotSprite;
            sr.color = i == 0 ? FxMath.Rgba(1f, 0.1f, 0.15f, 0.3f) : FxMath.Rgba(0.1f, 0.9f, 1f, 0.3f);
            float dx = FxMath.Range(0.004f, 0.014f) * (i == 0 ? -1f : 1f);
            sr.transform.localScale = FxMath.V3(h, h, 1f);
            sr.transform.localPosition = FxMath.V3(dx * w, 0f, CaptureZ - 0.1f - i * 0.05f);
        }
    }

    private static void HideBurst()
    {
        // 既に全部消してあるなら (会議中は毎フレーム呼ばれる) 描画物に触らない。
        if (_burstHidden && !_microRecolor && _burstAge < 0f) return;
        _burstHidden = true;
        if (_microRecolor)
        {
            _microRecolor = false;
            _wrong = _microSaved;
            if (_phase != Phase.None) ApplyTint(_amount * TintKeep);
        }

        _burstAge = -1f;
        if (_freeze) _freeze.enabled = false;
        if (_ghosts != null)
        {
            foreach (SpriteRenderer sr in _ghosts)
                if (sr) sr.enabled = false;
        }

        if (_bands == null) return;
        foreach (SpriteRenderer sr in _bands)
            if (sr) sr.enabled = false;
    }

    // バニラの環境音を鳴らしている部品を止め、船の音を自前のうなりと扉の音だけにする。
    private static void MuteVanillaAmbience()
    {
        MutedAmbience.Clear();
        MutedClips.Clear();

        foreach (AmbientSoundPlayer p in UnityEngine.Object.FindObjectsOfType<AmbientSoundPlayer>())
            Mute(p, p.AmbientSound);
        foreach (RaycastAmbientSoundPlayer p in UnityEngine.Object.FindObjectsOfType<RaycastAmbientSoundPlayer>())
            Mute(p, p.AmbientSound);
        foreach (TagAmbientSoundPlayer p in UnityEngine.Object.FindObjectsOfType<TagAmbientSoundPlayer>())
            Mute(p, p.AmbientSound);

        StopMutedClips();
        Logger.Info($"vanilla ambience muted players={MutedAmbience.Count} clips={MutedClips.Count}", "AirshipLiminal");
        return;

        static void Mute(MonoBehaviour p, AudioClip clip)
        {
            if (!p || !p.enabled) return;
            p.enabled = false;
            MutedAmbience.Add(p);
            if (clip && !MutedClips.Contains(clip)) MutedClips.Add(clip);
        }
    }

    private static void StopMutedClips()
    {
        SoundManager sm = SoundManager.Instance;
        if (!sm) return;
        foreach (AudioClip clip in MutedClips)
            if (clip) sm.StopSound(clip);
    }

    // 試合の途中で演出を切った時だけ効く (試合が終われば部品ごと船が消える)。止めた音は次の試合まで戻らない。
    private static void RestoreVanillaAmbience()
    {
        foreach (MonoBehaviour p in MutedAmbience)
            if (p) p.enabled = true;

        MutedAmbience.Clear();
        MutedClips.Clear();
    }

    private static void Teardown()
    {
        foreach (RoomArt art in Rooms.Values)
            for (int i = 0; i < art.Renderers.Length; i++)
                if (art.Renderers[i]) art.Renderers[i].color = art.Original[i];

        Rooms.Clear();
        _roomIds = [];
        AirshipRuin.Teardown();
        SetCloudsMoving(true);
        _clouds = [];
        EndGarble();
        _garbleRoom = null;
        EndStutter();
        _stutterPending = false;

        RestoreVanillaAmbience();

        _cloudEvent = false;
        _phase = Phase.None;
        _amount = 0f;
        _appliedAmount = -1f;
        _tearAge = -1f;
        _burstAge = -1f;
        _microRecolor = false;
        _humSrc = _sfxSrc = null;
        ReleaseShot();

        if (_bgOverridden && _cam) _cam.backgroundColor = _origBg;
        _bgOverridden = false;

        if (_root) UnityEngine.Object.Destroy(_root);
        _root = null;
        // 船を降りたら朽ち跡の素材 (約46MB) を常駐から外す。次に乗る時はイントロ中に先読みし直す。
        AirshipRuin.ReleaseSprites();
        _wash = _dim = null;
        _dimAlpha = DimAlpha;
        _tears = null;
        _freeze = null;
        _ghosts = _bands = null;
        _ship = null;
    }
}

[HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
internal static class AirshipLiminalHudUpdatePatch
{
    public static void Postfix()
    {
        AirshipLiminal.Tick();
    }
}
