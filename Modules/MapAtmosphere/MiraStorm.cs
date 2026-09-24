using System;
using System.Collections.Generic;
using AmongUs.Data;
using EndKnot.Patches.CalamityMenu;
using HarmonyLib;
using UnityEngine;
using Random = UnityEngine.Random;

namespace EndKnot.Modules.MapAtmosphere;

// Mira HQ の嵐演出 (各クライアントのローカル描画のみ・送信ゼロ)。
//   空   — カメラの単色背景の手前 (床より奥) に雷雲タイルを敷き、視差でゆっくり流す。
//   雨   — 奥の層 (床より奥 = 屋外の隙間にだけ見える) と手前の薄い層の2枚。
//          タイル描画の SpriteRenderer をタイル1枚分の周期で動かして巻き戻すので、UV スクロール用シェーダは要らない。
//   稲妻 — 空の層に稲妻を走らせ、画面全体を一瞬白く光らせる。遅れて雷鳴 (近い=短い遅延・遠い=長い遅延)。
//   室内 — 画面下の部屋名と同じ RoomTracker の判定で屋外度を出し、室内では手前の雨と雨音を絞る。
//   凪   — ときどき雨と雨音がほぼ消える静かな時間を挟み、近い雷で破る。一定の調子が続くと背景に慣れてしまうため。
//   突風 — 雨を大きく傾けて濃くし、うなる風音を重ねる。強さは毎回変える。
//   揺らぎ — 雨脚の強弱と風向きをゆっくり漂わせ、同じ降り方が続かないようにする。
//   明滅 — 近い雷鳴の直後に画面が暗く瞬き、照明が落ちかけたように見せる (描画だけで視界の広さは変えない)。
// Mira の空は画像ではなくカメラの backgroundColor (SolidColor) なので、背景色は退出時に必ず元へ戻す。
// 加算シェーダは AU ビルドに無いため、発光は全て Sprites/Default のアルファ合成で作る。
public static class MiraStorm
{
    private const string Res = "EndKnot.Resources.Images.MapAtmosphere.";

    // カメラからの相対 z。Mira の床・壁は z=-14〜8、カメラの描画範囲は ±1000。
    // 透明物は z の奥から順に描かれるので、+側は「床の奥 (屋外の隙間だけに見える)」、-側は「全ての手前」になる。
    private const float SkyZ = 100f;
    private const float BoltZ = 95f;
    private const float RainFarZ = 90f;
    private const float RainNearZ = -30f;
    private const float TintZ = -35f;
    private const float FlashZ = -40f;

    private const float SkyTile = 4f;     // storm_sky.png 512px / ppu128
    private const float RainFarTile = 2f; // rain_far.png 256px / ppu128
    private const float RainNearTile = 2.667f; // rain_near.png 256px / ppu96
    private const float RainAngle = 12f;  // 風で斜めに降らせる角度 (この前後を漂う)

    private static readonly Color StormBg = new(0.03f, 0.035f, 0.05f, 1f);
    private static readonly Color TintColor = new(0.04f, 0.06f, 0.11f, 0.2f);
    private static readonly Color FlashColor = new(0.9f, 0.93f, 1f, 1f);

    private static GameObject _root;
    private static ShipStatus _ship;
    private static ShipStatus _failedShip;
    private static Camera _cam;
    private static Color _origBg;
    private static bool _bgOverridden;

    private static Transform _skyTf, _rainFarTf, _rainNearTf, _boltTf;
    private static SpriteRenderer _sky, _rainFar, _rainNear, _bolt, _tint, _flash;
    private static Sprite[] _boltSprites;
    private static Sprite _solidSprite;

    private static float _rainFarOffset, _rainNearOffset, _skyDrift;
    private static float _strikeTimer, _nextStrike;
    private static float _strikeAge = -1f;
    private static float _strikeLen;
    private static bool _doubleStrike;
    private static float _glow; // 稲妻の光り具合 (水溜りの映り込みに渡す)
    private static bool _flickerOnThunder;
    private static float _flickerAge = -1f;

    private enum Phase { None, Rising, Holding, Falling }

    // 凪: _rain は雨の強さ (1=通常)。Falling で絞り、Holding で静けさを保ち、近い雷と共に Rising で一気に戻す。
    private static Phase _lullPhase;
    private static float _rain = 1f, _lullTimer, _lullHold, _lullFloor;
    private const float DeepLull = 0.12f; // これより静かな凪だけ、水溜りを見えない何かに歩かせる

    // 突風: _gust は風の強さ (0〜1)。
    private static Phase _gustPhase;
    private static float _gust, _gustTimer, _gustHold, _gustPeak;
    private const float GustAngle = 14f;
    private const float WindVolume = 0.5f;

    // 揺らぎ: _swell は雨脚の強弱 (凪の _rain に掛ける)。_windAngle は雨の傾き。どちらも目標を時々引き直して追従する。
    private static float _swell = 1f, _swellTarget = 1f, _swellTimer;
    private static float _windAngle = RainAngle, _windTarget = RainAngle, _windTimer;

    // 屋外度: 1=屋外 (バルコニー/発射台)・0.5=廊下・0=部屋の中。急に切り替わらないよう滑らかに追従させる。
    private static float _outdoor = 1f;
    private static float _fadeIn; // 0→1 (FadeInSeconds かけて全体を立ち上げる)
    private const float FadeInSeconds = 2.5f;
    private static float _rainRetry;
    private const float NearRainAlpha = 0.55f;

    private const float RainVolume = 0.35f;
    private static AudioSource _rainSrc, _thunderSrc, _windSrc;
    private static float _thunderDelay = -1f;
    private static bool _thunderNear;
    private static readonly Dictionary<string, AudioClip> Clips = [];

    public static void Tick()
    {
        try
        {
            ShipStatus ship = ShipStatus.Instance;
            // イントロ (役職発表) 中は組み立てない。明けてからフェードインさせる。
            bool want = Main.MapAtmosphere?.Value == true && ship && GameStates.IsInGame && ship.Type == ShipStatus.MapType.Hq
                        && HudManager.InstanceExists && !HudManager.Instance.IsIntroDisplayed;

            if (!_root)
            {
                if (_root is not null) Teardown(); // 船ごと破棄された (ロビー復帰)。背景色を戻す
                if (!want || ship == _failedShip) return;
                Build(ship);
                return;
            }

            if (!want || ship != _ship)
            {
                Teardown();
                return;
            }

            Animate(Time.deltaTime);
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

        _root = new GameObject("EK_MiraStorm");
        _root.layer = 0;
        _root.transform.SetParent(ship.transform, false);

        _sky = MakeLayer("Sky", Utils.LoadSprite(Res + "storm_sky.png", 128f), SkyZ, true, out _skyTf);
        _boltSprites = new Sprite[4];
        for (int i = 0; i < _boltSprites.Length; i++) _boltSprites[i] = Utils.LoadSprite(Res + $"bolt{i}.png", 256f);
        _bolt = MakeLayer("Bolt", _boltSprites[0], BoltZ, false, out _boltTf);
        _bolt.color = new Color(1f, 1f, 1f, 0f);
        _rainFar = MakeLayer("RainFar", Utils.LoadSprite(Res + "rain_far.png", 128f), RainFarZ, true, out _rainFarTf);
        _rainNear = MakeLayer("RainNear", Utils.LoadSprite(Res + "rain_near.png", 96f), RainNearZ, true, out _rainNearTf);
        _rainFarTf.localRotation = _rainNearTf.localRotation = Quaternion.Euler(0f, 0f, RainAngle);

        // 入場のたびに作り直すと Texture2D が孤立して残るので、1枚を使い回す (破棄済みなら作り直す)。
        if (!_solidSprite)
        {
            _solidSprite = CalamitySky.MakeSolidSprite(Color.white);
            _solidSprite.hideFlags |= HideFlags.HideAndDontSave;
            _solidSprite.texture.hideFlags |= HideFlags.HideAndDontSave;
        }

        Sprite solid = _solidSprite;
        _tint = MakeLayer("Tint", solid, TintZ, false, out _);
        _tint.color = TintColor;
        _flash = MakeLayer("Flash", solid, FlashZ, false, out _);
        _flash.color = new Color(FlashColor.r, FlashColor.g, FlashColor.b, 0f);

        MiraPuddles.Build(ship.transform);

        _outdoor = OutdoorTarget();
        _fadeIn = 0f;
        _rainRetry = 0f;
        _thunderDelay = -1f;
        SetupAudio();

        _origBg = _cam.backgroundColor;
        _cam.backgroundColor = StormBg;
        _bgOverridden = true;

        _rainFarOffset = _rainNearOffset = _skyDrift = 0f;
        _strikeTimer = 0f;
        _nextStrike = Random.Range(3f, 6f); // 入場直後に1回鳴らして嵐だと分からせる
        _strikeAge = -1f;
        _flickerAge = -1f;
        _flickerOnThunder = false;

        _lullPhase = Phase.None;
        _rain = 1f;
        _lullTimer = Random.Range(40f, 75f);
        _gustPhase = Phase.None;
        _gust = 0f;
        _gustTimer = Random.Range(15f, 30f);
        _swell = _swellTarget = Random.Range(0.7f, 1.1f);
        _swellTimer = Random.Range(8f, 20f);
        _windAngle = _windTarget = RainAngle + Random.Range(-4f, 4f);
        _windTimer = Random.Range(10f, 25f);

        Animate(0f);
        Logger.Info("MiraStorm built", "MiraStorm");
    }

    private static SpriteRenderer MakeLayer(string name, Sprite sprite, float z, bool tiled, out Transform tf)
    {
        var go = new GameObject(name) { layer = 0 }; // Main Camera の cullingMask に Default(0) は含まれる
        tf = go.transform;
        tf.SetParent(_root.transform, false);
        tf.localPosition = new Vector3(0f, 0f, z);

        SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        if (tiled) sr.drawMode = SpriteDrawMode.Tiled;
        return sr;
    }

    private static void Animate(float dt)
    {
        if (!_cam) _cam = Camera.main;
        if (!_cam) return;

        Vector3 camPos = _cam.transform.position;
        _root.transform.position = new Vector3(camPos.x, camPos.y, camPos.z);

        float h = _cam.orthographicSize * 2f;
        float w = h * _cam.aspect;
        float cover = Mathf.Sqrt(w * w + h * h) * 1.15f; // 回転した雨層でも画面の四隅を覆う

        UpdateLull(dt);
        UpdateGust(dt);
        UpdateSwell(dt);
        float rain = _rain * _swell;

        // 空: カメラ位置の 15% だけ動く視差 + ゆっくりした横流れ (突風の間は速く流れる)。タイル1枚分で巻き戻す。
        _skyDrift += dt * (0.12f + 0.5f * _gust);
        _sky.size = new Vector2(w + SkyTile * 2f, h + SkyTile * 2f);
        float sx = -Mathf.Repeat(camPos.x * 0.15f + _skyDrift, SkyTile);
        float sy = -Mathf.Repeat(camPos.y * 0.15f, SkyTile);
        _skyTf.localPosition = new Vector3(sx + SkyTile * 0.5f, sy + SkyTile * 0.5f, SkyZ);

        // 雨: 層ごとに落下速度を変えて奥行きを出す。風が強いほど傾いて速くなり、雨脚が強いほど少し速く落ちる。
        float speed = (0.8f + 0.2f * _swell) * (1f + 0.35f * _gust);
        _rainFarTf.localRotation = _rainNearTf.localRotation = Quaternion.Euler(0f, 0f, _windAngle + GustAngle * _gust);
        _rainFarOffset = Mathf.Repeat(_rainFarOffset + dt * 7f * speed, RainFarTile);
        _rainNearOffset = Mathf.Repeat(_rainNearOffset + dt * 12f * speed, RainNearTile);
        _rainFar.size = new Vector2(cover + RainFarTile * 2f, cover + RainFarTile * 2f);
        _rainNear.size = new Vector2(cover + RainNearTile * 2f, cover + RainNearTile * 2f);
        _rainFarTf.localPosition = (Vector3)(_rainFarTf.localRotation * new Vector3(0f, -_rainFarOffset, 0f)) + new Vector3(0f, 0f, RainFarZ);
        _rainNearTf.localPosition = (Vector3)(_rainNearTf.localRotation * new Vector3(0f, -_rainNearOffset, 0f)) + new Vector3(0f, 0f, RainNearZ);

        _tint.transform.localScale = _flash.transform.localScale = new Vector3(w * 1.3f, h * 1.3f, 1f);

        _outdoor = Mathf.MoveTowards(_outdoor, OutdoorTarget(), dt * 1.2f);
        _fadeIn = Mathf.MoveTowards(_fadeIn, 1f, dt / FadeInSeconds);
        float nearAlpha = NearRainAlpha * Mathf.Lerp(0.12f, 1f, _outdoor) * rain * (1f + 0.6f * _gust);
        _rainNear.color = new Color(1f, 1f, 1f, Mathf.Min(1f, nearAlpha) * _fadeIn);
        _rainFar.color = new Color(1f, 1f, 1f, Mathf.Clamp(rain, 0.12f, 1f) * _fadeIn); // 奥の層は霧雨程度に残す
        _tint.color = new Color(TintColor.r, TintColor.g, TintColor.b, (TintColor.a + Flicker(dt)) * _fadeIn);
        UpdateAudio(dt, rain);

        UpdateLightning(dt, w, h);
        MiraPuddles.Animate(dt, _fadeIn, _glow, rain);
    }

    private static void UpdateLightning(float dt, float w, float h)
    {
        if (_strikeAge >= 0f)
        {
            _strikeAge += dt;
            float t = _strikeAge;

            // 稲妻本体: 点く→一瞬消える→再点灯して減衰 (二段の明滅)。
            float bolt = t < 0.07f ? 1f : t < 0.12f ? 0.15f : Mathf.Clamp01(1f - (t - 0.12f) / (_strikeLen - 0.12f));
            if (_doubleStrike && t is > 0.3f and < 0.38f) bolt = Mathf.Max(bolt, 0.9f);

            // 画面フラッシュは本体より短く鋭く。空も同じ包絡で明るくする。
            float flash = t < 0.05f ? 1f : t < 0.1f ? 0.25f : Mathf.Clamp01(1f - (t - 0.1f) / 0.25f) * 0.6f;
            if (_doubleStrike && t is > 0.3f and < 0.36f) flash = Mathf.Max(flash, 0.7f);
            if (Main.MapAtmosphereFlash?.Value != true) flash = 0f;
            flash *= Mathf.Lerp(0.6f, 1f, _outdoor); // 室内では窓越しの光くらいに抑える

            _glow = Mathf.Max(flash, bolt * 0.35f);
            _bolt.color = new Color(1f, 1f, 1f, bolt);
            _flash.color = new Color(FlashColor.r, FlashColor.g, FlashColor.b, flash * 0.32f);
            float lit = 1f + Mathf.Max(bolt * 0.6f, flash * 1.4f);
            _sky.color = new Color(Mathf.Min(1f, lit * 0.55f), Mathf.Min(1f, lit * 0.55f), Mathf.Min(1f, lit * 0.62f), _fadeIn);

            if (_strikeAge >= _strikeLen + (_doubleStrike ? 0.3f : 0f))
            {
                _strikeAge = -1f;
                _bolt.color = new Color(1f, 1f, 1f, 0f);
                _flash.color = new Color(FlashColor.r, FlashColor.g, FlashColor.b, 0f);
                _glow = 0f;
            }
            return;
        }

        _sky.color = new Color(0.55f, 0.55f, 0.62f, _fadeIn);

        if (_lullPhase is Phase.Falling or Phase.Holding) return; // 凪の間は凪を破る1発まで落とさない

        _strikeTimer += dt;
        if (_strikeTimer < _nextStrike) return;

        StartStrike(false, w, h);
    }

    // lullBreak: 凪を破る1発。必ず近く、光ってから鳴るまでが短い。
    // それ以外は「光→雷鳴」の型に慣れさせないよう、鳴るだけ・光るだけを混ぜる。
    private static void StartStrike(bool lullBreak, float w, float h)
    {
        _strikeTimer = 0f;
        _nextStrike = Random.Range(7f, 20f);
        float roll = lullBreak ? 1f : Random.value;

        if (roll < 0.15f)
        {
            // 見えないところで落ちた: 遠雷だけ
            _thunderNear = false;
            _thunderDelay = Random.Range(0.1f, 0.6f);
            _flickerOnThunder = false;
            Logger.Info("MiraStorm rumble only", "MiraStorm");
            return;
        }

        _strikeAge = 0f;
        _strikeLen = Random.Range(0.45f, 0.7f);
        _doubleStrike = Random.value < (lullBreak ? 0.6f : 0.3f);

        _bolt.sprite = _boltSprites[Random.Range(0, _boltSprites.Length)];
        _bolt.flipX = Random.value < 0.5f;
        float scale = h * 1.15f / 3f; // bolt*.png は 768px / ppu256 = 3 unit の高さ
        _boltTf.localScale = new Vector3(scale, scale, 1f);
        _boltTf.localPosition = new Vector3(Random.Range(-w * 0.4f, w * 0.4f), h * 0.05f, BoltZ);

        // 近い雷ほど光ってから鳴るまでが短い。
        _thunderNear = lullBreak || Random.value < 0.45f;
        _thunderDelay = lullBreak ? Random.Range(0.08f, 0.2f) : _thunderNear ? Random.Range(0.15f, 0.5f) : Random.Range(1.2f, 2.8f);
        _flickerOnThunder = _thunderNear && (lullBreak || Random.value < 0.5f);

        if (roll < 0.27f) // 光るだけで鳴らない
        {
            _thunderDelay = -1f;
            _flickerOnThunder = false;
        }

        Logger.Info($"MiraStorm strike lullBreak={lullBreak} silent={_thunderDelay < 0f} double={_doubleStrike} near={_thunderNear} outdoor={_outdoor:0.00} rain={(_rainSrc ? $"{_rainSrc.volume:0.00}/{_rainSrc.isPlaying}" : "none")}", "MiraStorm");
    }

    private static void UpdateLull(float dt)
    {
        switch (_lullPhase)
        {
            case Phase.None:
                if (_strikeAge >= 0f || _thunderDelay >= 0f || (_lullTimer -= dt) > 0f) break;
                _lullPhase = Phase.Falling;
                // 毎回無音まで落とさず、小降りで止まる浅い凪も混ぜる。
                _lullFloor = Random.value < 0.6f ? Random.Range(0.03f, DeepLull) : Random.Range(0.18f, 0.35f);
                Logger.Info($"MiraStorm lull begins floor={_lullFloor:0.00}", "MiraStorm");
                break;
            case Phase.Falling:
                _rain = Mathf.MoveTowards(_rain, _lullFloor, dt / 3.5f);
                if (_rain > _lullFloor) break;
                _lullPhase = Phase.Holding;
                _lullHold = Random.Range(5f, 9f);
                if (_lullFloor < DeepLull) MiraPuddles.OnLull();
                break;
            case Phase.Holding:
                if ((_lullHold -= dt) > 0f) break;
                _lullPhase = Phase.Rising;
                float h = _cam.orthographicSize * 2f;
                StartStrike(true, h * _cam.aspect, h);
                break;
            case Phase.Rising:
                _rain = Mathf.MoveTowards(_rain, 1f, dt / 0.8f);
                if (_rain < 1f) break;
                _lullPhase = Phase.None;
                _lullTimer = Random.Range(45f, 90f);
                Logger.Info($"MiraStorm lull ends next={_lullTimer:0}s", "MiraStorm");
                break;
        }
    }

    private static void UpdateGust(float dt)
    {
        bool calm = _lullPhase is Phase.Falling or Phase.Holding;

        switch (_gustPhase)
        {
            case Phase.None:
                if (calm || (_gustTimer -= dt) > 0f) break;
                _gustPhase = Phase.Rising;
                _gustHold = Random.Range(2f, 4f);
                _gustPeak = Random.Range(0.45f, 1f);
                PlayWind();
                Logger.Info($"MiraStorm gust peak={_gustPeak:0.00} wind={(_windSrc ? _windSrc.isPlaying : false)}", "MiraStorm");
                break;
            case Phase.Rising:
                _gust = Mathf.MoveTowards(_gust, _gustPeak, dt / 1.8f);
                if (calm) _gustPhase = Phase.Falling;
                else if (_gust >= _gustPeak) _gustPhase = Phase.Holding;
                break;
            case Phase.Holding:
                if (calm || (_gustHold -= dt) <= 0f) _gustPhase = Phase.Falling;
                break;
            case Phase.Falling:
                _gust = Mathf.MoveTowards(_gust, 0f, dt / 3f);
                if (_gust > 0f) break;
                _gustPhase = Phase.None;
                _gustTimer = Random.Range(18f, 40f);
                break;
        }
    }

    // 雨脚は小降り〜本降りの間を、風向きは基準の角度の前後を、それぞれ時々目標を引き直してゆっくり追う。
    // 凪の最中は凪だけに任せるため、雨脚の目標は引き直さない。
    private static void UpdateSwell(float dt)
    {
        if (_lullPhase == Phase.None && (_swellTimer -= dt) <= 0f)
        {
            _swellTimer = Random.Range(8f, 20f);
            _swellTarget = Random.value < 0.2f ? Random.Range(1.1f, 1.3f) : Random.Range(0.5f, 1f); // ときどき一段強い本降り
        }

        _swell = Mathf.MoveTowards(_swell, _swellTarget, dt / 6f);

        if ((_windTimer -= dt) <= 0f)
        {
            _windTimer = Random.Range(10f, 25f);
            _windTarget = RainAngle + Random.Range(-5f, 5f);
        }

        _windAngle = Mathf.MoveTowards(_windAngle, _windTarget, dt * 0.8f);
    }

    // 照明が落ちかけたような暗い瞬き。点滅なので画面フラッシュを切っている人には出さない。
    private static float Flicker(float dt)
    {
        if (_flickerAge < 0f) return 0f;
        _flickerAge += dt;
        float t = _flickerAge;

        if (t >= 0.4f || Main.MapAtmosphereFlash?.Value != true)
        {
            _flickerAge = -1f;
            return 0f;
        }

        float dim = t < 0.07f ? 0.55f : t < 0.13f ? 0.1f : t < 0.2f ? 0.45f : t < 0.34f ? 0.05f : 0.3f;
        return dim * Mathf.Lerp(1f, 0.6f, _outdoor);
    }

    // RoomTracker は画面下の部屋名表示と同じ判定 (コライダー判定なので矩形近似より正確)。部屋外=廊下は null。
    private static float OutdoorTarget()
    {
        if (MeetingHud.Instance) return 0.3f; // 会議中は雨を遠くに
        HudManager hud = HudManager.Instance;
        PlainShipRoom room = hud && hud.roomTracker ? hud.roomTracker.LastRoom : null;
        if (!room) return 0.5f;
        return room.RoomId is SystemTypes.Balcony or SystemTypes.Launchpad ? 1f : 0f;
    }

    private static void SetupAudio()
    {
        if (!CustomSoundsManager.AudioPlatformSupported) return;

        AudioClip rain = GetClip("MiraRainLoop");

        // SoundManager 経由だと BGM 側の消音処理が soundPlayers を止めに来るので、独自の AudioSource を船の下に持つ。
        _rainSrc = _root.AddComponent<AudioSource>();
        _rainSrc.playOnAwake = false;
        _rainSrc.spatialBlend = 0f;
        _rainSrc.loop = true;
        _rainSrc.clip = rain;
        _rainSrc.volume = 0f;
        if (rain) _rainSrc.Play();

        _thunderSrc = _root.AddComponent<AudioSource>();
        _thunderSrc.playOnAwake = false;
        _thunderSrc.spatialBlend = 0f;

        // 風音は凪で途中から絞れるよう、ワンショットでなく音量を毎フレーム追従させる。
        _windSrc = _root.AddComponent<AudioSource>();
        _windSrc.playOnAwake = false;
        _windSrc.spatialBlend = 0f;
        _windSrc.volume = 0f;
    }

    private static void PlayWind()
    {
        if (!_windSrc) return;
        AudioClip clip = GetClip("MiraWindGust");
        if (!clip) return;
        _windSrc.clip = clip;
        _windSrc.pitch = Random.Range(0.92f, 1.06f);
        _windSrc.Play();
    }

    private static void UpdateAudio(float dt, float rainLevel)
    {
        float sfx = DataManager.Settings?.Audio != null ? DataManager.Settings.Audio.SfxVolume : 1f;

        if (_rainSrc)
        {
            // 初回の読み込みに失敗しても試合中ずっと無音にならないよう、間隔を空けて取り直す。
            if (!_rainSrc.clip && (_rainRetry -= dt) <= 0f)
            {
                _rainRetry = 2f;
                AudioClip rain = GetClip("MiraRainLoop");
                if (rain)
                {
                    _rainSrc.clip = rain;
                    _rainSrc.Play();
                }
            }

            _rainSrc.volume = sfx * RainVolume * Mathf.Lerp(0.22f, 1f, _outdoor) * Mathf.Min(rainLevel, 1.15f) * (1f + 0.25f * _gust) * _fadeIn;
        }

        if (_windSrc) _windSrc.volume = sfx * WindVolume * Mathf.Lerp(0.35f, 1f, _outdoor) * Mathf.Max(_rain, 0.1f) * _fadeIn;

        if (_thunderDelay < 0f) return;
        _thunderDelay -= dt;
        if (_thunderDelay > 0f) return;
        _thunderDelay = -1f;

        if (_flickerOnThunder) _flickerAge = 0f;
        _flickerOnThunder = false;

        if (!_thunderSrc) return;
        AudioClip clip = GetClip(_thunderNear ? "MiraThunderNear" : "MiraThunderFar");
        if (!clip) return;

        float vol = (_thunderNear ? 0.9f : 0.6f) * Mathf.Lerp(0.55f, 1f, _outdoor);
        _thunderSrc.pitch = Random.Range(0.9f, 1.08f);
        _thunderSrc.PlayOneShot(clip, sfx * vol);
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

    private static void Teardown()
    {
        _thunderDelay = -1f;
        _rainSrc = _thunderSrc = _windSrc = null;
        _flickerAge = -1f;
        if (_bgOverridden && _cam) _cam.backgroundColor = _origBg;
        _bgOverridden = false;

        MiraPuddles.Teardown();
        if (_root) Object.Destroy(_root);
        _root = null;
        _glow = 0f;
        _ship = null;
        _strikeAge = -1f;
    }
}

[HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
internal static class MiraStormHudUpdatePatch
{
    public static void Postfix()
    {
        MiraStorm.Tick();
    }
}
