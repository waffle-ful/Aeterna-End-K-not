using System;
using System.Collections.Generic;
using EndKnot.Modules;
using UnityEngine;

namespace EndKnot.SettingsUI;

// 新しい設定画面の End K not の飾り: 板の上のロゴ・夜空色の地と星・開く時の動き。
// 本編の板 (PanelSprite) の子として足し、閉じる時に自分で消す (同じ画面を今の見た目で開き直した時に残さない)。
// 本編の部品の色は変えない (今の見た目の画面と共用なので)。
internal static class SettingsTheme
{
    private const string RootName = "EndKnotTheme";
    private const string LogoPath = "EndKnot.Resources.Images.MainMenu.Dusk.dusk_logo.png";

    // 板の開ききった時の幅 (世界の単位)。板の子の寸法はこれを基準に世界の単位で決める
    private const float PanelWorldW = 10.1f;
    private const float Inset = 0.17f;   // 夜空の地を板の縁から内側へ寄せる幅
    private const float LogoW = 2.0f;
    private const float LogoX = 1.2f;   // 板の中心からロゴの中心まで (検索欄と右上のボタンの間)
    private const int StarCount = 12;

    // ロゴの色から取った色 (K の夜空・字のクリーム・縄の先の火花)
    private static readonly Color NightLeft = new(0.1f, 0.125f, 0.25f, 1f);
    private static readonly Color Cream = new(0.95f, 0.89f, 0.79f, 1f);
    private static readonly Color RimColor = new(0.95f, 0.89f, 0.79f, 0.9f);
    private static readonly Color FrameColor = new(0.62f, 0.54f, 0.43f, 1f);
    private static readonly Color Halo = new(0.22f, 0.28f, 0.55f, 1f);
    private static readonly Color Spark = new(1f, 0.62f, 0.28f, 1f);

    private sealed class Star
    {
        public SpriteRenderer Glow, Core, SpikeH, SpikeV;
        public Color Tint;
        public float Start, Phase, Speed, Lo, Hi, Spike;
    }

    private sealed class Cloud
    {
        public SpriteRenderer Sr;
        public float Alpha, Period, Phase, Z;
    }

    private static readonly List<Cloud> Clouds = new();
    private static float _hw, _hh;
    // 流れ星: 7〜15 秒に 1 本、0.9 秒で斜めに流れる
    private static SpriteRenderer _meteor;
    private static bool _meteorOn;
    private static float _meteorNext, _meteorT0, _meteorX, _meteorY, _meteorDx, _meteorDy;
    private const float MeteorDur = 0.9f, MeteorRun = 2.6f, MeteorLen = 0.9f;

    private static GameObject _root;
    // 毎フレームの生存確認 (Unity の比較) を避けるため、作ったかどうかは自分で持つ。根が生きていれば子も生きている
    private static bool _hasLogo, _hasGlow;
    private static SpriteRenderer _logo, _glow, _halo;
    private static Vector3 _logoPos, _logoScale, _glowScale, _haloScale;
    private static float _rise;
    private static readonly List<Star> Stars = new();
    private static float _t;
    private static Sprite _glowSprite;

    // 画面が開く時 (OnEnable)。同じ画面で 2 回目に開いた時は動きだけやり直す
    public static void Show(GameSettingMenu menu)
    {
        try
        {
            if (_root && _root.transform.parent && menu && _root.transform.IsChildOf(menu.transform))
            {
                _t = 0f;
                _meteorNext = 3.5f;
                _meteorOn = false;
                return;
            }
            Hide();
            Build(menu);
        }
        catch (Exception e)
        {
            Logger.Warn($"theme build failed: {e.Message}", "SettingsTheme");
            Hide();
        }
    }

    public static void Hide()
    {
        if (_root) UnityEngine.Object.Destroy(_root);
        _root = null;
        _logo = null;
        _glow = null;
        _halo = null;
        _hasLogo = _hasGlow = false;
        Stars.Clear();
        Clouds.Clear();
        _meteor = null;
        _meteorOn = false;
    }

    private static void Build(GameSettingMenu menu)
    {
        var panelT = menu ? menu.transform.FindChild("PanelSprite") : null;
        var panel = panelT ? panelT.GetComponent<SpriteRenderer>() : null;
        if (!panel || !panel.sprite) return;
        var leftT = panelT.FindChild("LeftSideTint");
        var left = leftT ? leftT.GetComponent<SpriteRenderer>() : null;

        // 板の絵の大きさ (板の手元の単位) と、手元の単位 1 つが世界の何単位か。縦横の比は開く拡大の途中でも変わらない
        Bounds b = panel.sprite.bounds;
        float sx = PanelWorldW / b.size.x;
        Vector3 lossy = panelT.lossyScale;
        float sy = lossy.x > 0.0001f ? sx * lossy.y / lossy.x : sx;
        float sz = lossy.x > 0.0001f ? sx * Math.Abs(lossy.z) / lossy.x : sx;
        if (sz < 0.0001f) sz = sx;

        _root = new GameObject(RootName) { layer = panelT.gameObject.layer };
        _root.transform.SetParent(panelT, false);
        _root.transform.localPosition = Vector3.zero;

        // 奥行き: 板 < 夜空 (地・星雲・星) < 左の列の地 (LeftSideTint は板より手前にある)
        float leftZ = leftT ? leftT.localPosition.z : -0.08f / sz;
        if (leftZ >= 0f) leftZ = -0.08f / sz;
        float nightZ = leftZ * 0.35f;

        // 本編の灰色の枠の上に縄の色の枠を重ねる (外の黒い縁取りは残す)
        var frame = MenuArt.Panel(_root.transform, "Frame", 0.12f / sx,
            new Vector2(b.size.x - 2f * 0.03f / sx, b.size.y - 2f * 0.03f / sy),
            new Vector3(b.center.x, b.center.y, nightZ * 0.25f), FrameColor, null);
        frame.sortingOrder = panel.sortingOrder;

        // 夜空の地の縁に、ロゴの字と同じクリーム色の細い線を回す
        var rim = MenuArt.Panel(_root.transform, "Rim", 0.16f / sx,
            new Vector2(b.size.x - 2f * (Inset - 0.025f) / sx, b.size.y - 2f * (Inset - 0.025f) / sy),
            new Vector3(b.center.x, b.center.y, nightZ * 0.5f), RimColor, null);
        rim.sortingOrder = panel.sortingOrder;

        // 夜空: 中は世界の単位で組む (板は縦横で倍率が違うので、回す物を歪ませない)。
        // 奥から 地と暗い星の絵 → 星雲 3 層 → 明るい星 → 流れ星。z の差は世界の単位で、左の列の地より奥に収める
        float worldW = b.size.x * sx - 2f * Inset, worldH = b.size.y * sy - 2f * Inset;
        _hw = worldW * 0.5f;
        _hh = worldH * 0.5f;
        int order = panel.sortingOrder;
        var sky = new GameObject("Sky") { layer = _root.layer };
        sky.transform.SetParent(_root.transform, false);
        sky.transform.localPosition = new Vector3(b.center.x, b.center.y, nightZ);
        sky.transform.localScale = new Vector3(1f / sx, 1f / sy, 1f / sz);
        Put(sky.transform, "Night", SpaceArt.Sky(worldW, worldH, 0.14f), Vector3.zero, Vector3.one, Color.white, order);

        float aspect = worldW / worldH;
        AddNebula(sky.transform, 11, aspect, 0.24f, 0.68f, 0.44f, 0.62f, new Color(0.45f, 0.58f, 1f), new Color(0.15f, 0.22f, 0.62f), 0.34f, 11f, -0.004f, worldW, order);
        AddNebula(sky.transform, 12, aspect, 0.76f, 0.3f, 0.46f, 0.62f, new Color(0.82f, 0.45f, 0.92f), new Color(0.3f, 0.16f, 0.56f), 0.26f, 14f, -0.005f, worldW, order);
        AddNebula(sky.transform, 13, aspect, 0.86f, 0.86f, 0.3f, 0.34f, new Color(1f, 0.72f, 0.42f), new Color(0.55f, 0.28f, 0.3f), 0.13f, 9f, -0.006f, worldW, order);

        if (left)
        {
            var leftNight = MenuArt.Panel(leftT, "EndKnotThemeLeft", 0.02f, left.drawMode == SpriteDrawMode.Simple ? (Vector2)left.sprite.bounds.size : left.size,
                new Vector3(0f, 0f, -0.001f), NightLeft, null);
            leftNight.sortingOrder = left.sortingOrder;
            // 左の列の地は本編の部品の子なので、根と一緒に消えるよう根の下に付け替える (位置は保つ)
            leftNight.transform.SetParent(_root.transform, true);
        }

        // 明るい星: にじみと小さな芯 (硬い丸だと中身の縁に半分隠れた時に欠けた点に見える)。
        // 一部には十字の光条。板の中に決まった並びで散らす (開くたびに同じ空)
        var rng = new System.Random(1717);
        var glow = GlowSprite();
        for (int i = 0; i < StarCount; i++)
        {
            float x01 = 0.04f + (float)rng.NextDouble() * 0.92f;
            float y01 = 0.05f + (float)rng.NextDouble() * 0.9f;
            var pos = new Vector3(-_hw + worldW * x01, -_hh + worldH * y01, -0.01f);
            float halo = 0.1f + (float)rng.NextDouble() * 0.07f;
            float core = 0.026f + (float)rng.NextDouble() * 0.014f;
            float tint = (float)rng.NextDouble();
            var c = new Color(Lerp(0.78f, 1f, tint), Lerp(0.86f, 0.93f, tint), Lerp(1f, 0.8f, tint), 0f);
            var st = new Star
            {
                Glow = Put(sky.transform, "StarGlow", glow, pos, new Vector3(halo, halo, 1f), c, order),
                Core = Put(sky.transform, "Star", glow, pos + new Vector3(0f, 0f, -0.001f), new Vector3(core, core, 1f), c, order),
                Tint = c,
                Start = 0.08f + 0.45f * x01,
                Phase = (float)rng.NextDouble() * 6.283f,
                Speed = 6.283f / (2.5f + (float)rng.NextDouble() * 3.5f),
                Lo = 0.25f + (float)rng.NextDouble() * 0.2f,
                Hi = 0.75f + (float)rng.NextDouble() * 0.25f,
            };
            if (i % 3 == 0)
            {
                st.Spike = 0.16f + (float)rng.NextDouble() * 0.1f;
                st.SpikeH = Put(sky.transform, "SpikeH", glow, pos + new Vector3(0f, 0f, -0.0005f), new Vector3(st.Spike, 0.012f, 1f), c, order);
                st.SpikeV = Put(sky.transform, "SpikeV", glow, pos + new Vector3(0f, 0f, -0.0005f), new Vector3(0.012f, st.Spike, 1f), c, order);
            }
            Stars.Add(st);
        }

        _meteor = Put(sky.transform, "Meteor", SpaceArt.Streak(), new Vector3(0f, 0f, -0.012f), Vector3.zero, Clear(Cream), order);
        _meteorOn = false;
        _meteorNext = 3.5f;

        // ロゴ: 板の上の帯、検索欄の右。縄の先の火花に光を重ねる
        var logoSprite = Utils.LoadSprite(LogoPath, 100f);
        if (logoSprite)
        {
            float nativeW = logoSprite.rect.width / logoSprite.pixelsPerUnit;
            float k = LogoW / nativeW;
            float logoH = logoSprite.rect.height / logoSprite.pixelsPerUnit * k;
            var go = new GameObject("Logo") { layer = _root.layer };
            go.transform.SetParent(_root.transform, false);
            _logoScale = new Vector3(k / sx, k / sy, 1f);
            _logoPos = new Vector3(b.center.x + LogoX / sx, b.center.y + (b.size.y * sy * 0.5f + 0.06f + logoH * 0.5f) / sy, -2f / sz);
            go.transform.localScale = _logoScale;
            go.transform.localPosition = _logoPos;
            // 黒い地では字の縁取りが沈むので、裏に淡い紺の光を敷く
            var haloGo = new GameObject("Halo") { layer = _root.layer };
            haloGo.transform.SetParent(_root.transform, false);
            haloGo.transform.localPosition = new Vector3(_logoPos.x, _logoPos.y, _logoPos.z + 0.05f / sz);
            _haloScale = new Vector3(LogoW * 1.35f / sx, logoH * 1.7f / sy, 1f);
            haloGo.transform.localScale = _haloScale;
            _halo = haloGo.AddComponent<SpriteRenderer>();
            _halo.sprite = GlowSprite();
            _halo.color = Clear(Halo);

            _logo = go.AddComponent<SpriteRenderer>();
            _logo.sprite = logoSprite;
            _logo.color = Clear(Color.white);
            _hasLogo = true;
            _rise = 0.2f / sy;

            var glowGo = new GameObject("Spark") { layer = _root.layer };
            glowGo.transform.SetParent(_root.transform, false);
            glowGo.transform.localPosition = new Vector3(_logoPos.x + 0.425f * LogoW / sx, _logoPos.y - 0.26f * logoH / sy, _logoPos.z - 0.05f / sz);
            _glowScale = new Vector3(1f / sx, 1f / sy, 1f);
            glowGo.transform.localScale = Vector3.zero;
            _glow = glowGo.AddComponent<SpriteRenderer>();
            _glow.sprite = GlowSprite();
            _glow.color = Clear(Spark);
            _hasGlow = true;
        }

        _t = 0f;
    }

    // 画面が開いている間、毎フレーム
    public static void Tick()
    {
        if (!_root) return;
        // 画面を作った直後のフレームは重く、そのまま足すと動きを飛ばすので 1 フレーム分に抑える
        _t += FxMath.Min(Time.deltaTime, 1f / 30f);
        float t = _t;

        if (_hasLogo)
        {
            // 0.4 秒で上から降りて現れる
            float p = FxMath.Clamp01(t / 0.4f);
            float e = 1f - (1f - p) * (1f - p) * (1f - p);
            float s = 0.94f + 0.06f * e;
            if (p < 1f || t < 0.45f)
            {
                var tr = _logo.transform;
                tr.localPosition = FxMath.V3(_logoPos.x, _logoPos.y + _rise * (1f - e), _logoPos.z);
                tr.localScale = FxMath.V3(_logoScale.x * s, _logoScale.y * s, 1f);
                _logo.color = FxMath.Rgba(1f, 1f, 1f, e);
                _halo.color = FxMath.Rgba(Halo.r, Halo.g, Halo.b, 0.6f * e);
            }
        }

        if (_hasGlow)
        {
            // 縄の先の火花: ロゴが降りきる頃に灯り、その後はゆっくり息をする
            float a, size;
            if (t < 0.25f) { a = 0f; size = 0.2f; }
            else if (t < 0.55f) { float q = (t - 0.25f) / 0.3f; a = 0.95f * q; size = 0.2f + 0.6f * q; }
            else if (t < 1.1f) { float q = (t - 0.55f) / 0.55f; a = FxMath.Lerp(0.95f, 0.5f, q); size = FxMath.Lerp(0.8f, 0.55f, q); }
            else { float br = FxMath.Sin((t - 1.1f) * 2.6f); a = 0.5f + 0.12f * br; size = 0.55f + 0.04f * br; }
            _glow.transform.localScale = FxMath.V3(_glowScale.x * size, _glowScale.y * size, 1f);
            _glow.color = FxMath.Rgba(Spark.r, Spark.g, Spark.b, a);
        }

        // 星雲: 1 秒かけて浮かび、その後はゆっくり息をしながら少しだけ漂う (動いていると気づくか気づかないかの速さ)
        float fade = FxMath.Clamp01(t / 1f);
        for (int i = 0; i < Clouds.Count; i++)
        {
            var c = Clouds[i];
            float br = 0.8f + 0.2f * FxMath.Sin(c.Phase + t * 6.283f / c.Period);
            c.Sr.color = FxMath.Rgba(1f, 1f, 1f, c.Alpha * br * fade * fade);
            c.Sr.transform.localPosition = FxMath.V3(0.05f * FxMath.Sin(c.Phase + t * 0.134f), 0.03f * FxMath.Sin(c.Phase * 1.7f + t * 0.103f), c.Z);
        }

        // 星: 左から右へ順に瞬いて灯り、その後は 1 つずつ違う速さで瞬く
        for (int i = 0; i < Stars.Count; i++)
        {
            var st = Stars[i];
            float p = FxMath.Clamp01((t - st.Start) / 0.25f);
            float tw = st.Lo + (st.Hi - st.Lo) * (0.5f + 0.5f * FxMath.Sin(st.Phase + t * st.Speed));
            float a = tw * p;
            if (p < 1f) a = FxMath.Max(a, 4f * p * (1f - p) * 0.9f);
            st.Core.color = FxMath.Rgba(st.Tint.r, st.Tint.g, st.Tint.b, a);
            st.Glow.color = FxMath.Rgba(st.Tint.r, st.Tint.g, st.Tint.b, 0.3f * a);
            if (st.Spike > 0f)
            {
                float len = st.Spike * (0.6f + 0.4f * a);
                var sc = FxMath.Rgba(st.Tint.r, st.Tint.g, st.Tint.b, 0.55f * a);
                st.SpikeH.color = sc;
                st.SpikeV.color = sc;
                st.SpikeH.transform.localScale = FxMath.V3(len, 0.012f, 1f);
                st.SpikeV.transform.localScale = FxMath.V3(0.012f, len, 1f);
            }
        }

        TickMeteor(t);
    }

    private static void TickMeteor(float t)
    {
        if (_meteor == null) return;
        if (!_meteorOn)
        {
            if (t < _meteorNext) return;
            _meteorNext = t + FxMath.Range(7f, 15f);
            // 頭の通り道が夜空の内側に収まる向きと始点を選ぶ (収まらなければ今回は流さない)
            for (int k = 0; k < 6; k++)
            {
                float deg = FxMath.Range(198f, 216f);
                if (FxMath.Range(0f, 1f) < 0.35f) deg = 180f - deg + 360f; // 右下へ
                float dx = FxMath.Cos(deg * 0.017453f), dy = FxMath.Sin(deg * 0.017453f);
                float x = FxMath.Range(-_hw + 0.4f, _hw - 0.4f), y = FxMath.Range(_hh * 0.15f, _hh - 0.35f);
                float ex = x + dx * MeteorRun, ey = y + dy * MeteorRun;
                if (ex < -_hw + 0.3f || ex > _hw - 0.3f || ey < -_hh + 0.3f) continue;
                _meteorX = x;
                _meteorY = y;
                _meteorDx = dx;
                _meteorDy = dy;
                _meteorT0 = t;
                _meteorOn = true;
                _meteor.transform.localRotation = FxMath.RotZ(deg);
                break;
            }
            if (!_meteorOn) return;
        }
        float age = t - _meteorT0;
        if (age >= MeteorDur)
        {
            _meteorOn = false;
            _meteor.color = FxMath.Rgba(Cream.r, Cream.g, Cream.b, 0f);
            return;
        }
        // 速く出て少し減速する・尾は出だしで伸びる
        float q = age / MeteorDur;
        float run = MeteorRun * q * (1.4f - 0.4f * q);
        float a = 0.8f * FxMath.Clamp01(age / 0.12f) * FxMath.Clamp01((MeteorDur - age) / 0.35f);
        float len = MeteorLen * FxMath.Clamp01(age / 0.25f);
        _meteor.transform.localPosition = FxMath.V3(_meteorX + _meteorDx * run, _meteorY + _meteorDy * run, -0.012f);
        _meteor.transform.localScale = FxMath.V3(len, 0.16f, 1f);
        _meteor.color = FxMath.Rgba(Cream.r, Cream.g, Cream.b, a);
    }

    private static void AddNebula(Transform sky, int seed, float aspect, float cx, float cy, float rx, float ry, Color core, Color edge, float alpha, float period, float z, float worldW, int order)
    {
        var sr = Put(sky, "Nebula", SpaceArt.Nebula(seed, aspect, cx, cy, rx, ry, core, edge), new Vector3(0f, 0f, z), new Vector3(worldW, worldW, 1f), Clear(Color.white), order);
        Clouds.Add(new Cloud { Sr = sr, Alpha = alpha, Period = period, Phase = seed * 1.37f, Z = z });
    }

    private static SpriteRenderer Put(Transform parent, string name, Sprite sprite, Vector3 pos, Vector3 scale, Color c, int order)
    {
        var go = new GameObject(name) { layer = parent.gameObject.layer };
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        go.transform.localScale = scale;
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.color = c;
        sr.sortingOrder = order;
        return sr;
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static Color Clear(Color c) => new(c.r, c.g, c.b, 0f);

    // 縁へ向かって滑らかに消える丸い光 (幅 1 単位)
    private static Sprite GlowSprite()
    {
        if (_glowSprite) return _glowSprite;
        const int n = 64;
        var px = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
            float r = MathF.Sqrt(dx * dx + dy * dy);
            float a = r >= 1f ? 0f : (1f - r) * (1f - r);
            int i = (y * n + x) * 4;
            px[i] = px[i + 1] = px[i + 2] = 255;
            px[i + 3] = (byte)(a * 255f + 0.5f);
        }
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = "SettingsUiGlow" };
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.LoadRawTextureData(px);
        tex.Apply(false, true);
        tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
        _glowSprite = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), n, 0, SpriteMeshType.FullRect);
        _glowSprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return _glowSprite;
    }
}
