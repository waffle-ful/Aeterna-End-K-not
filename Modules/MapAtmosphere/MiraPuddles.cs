using System.IO;
using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using Random = UnityEngine.Random;

namespace EndKnot.Modules.MapAtmosphere;

// Mira HQ 屋外 (発射台・バルコニー) の水溜り。MiraStorm が組み立て・毎フレーム更新・片付けを呼ぶ。
// 床は壁と一緒に一枚絵 (Walls/launchPadWalls z=7・Walls/CafeteriaWalls z=4) に焼き込まれているので、
// 床のすぐ手前の z に敷けば、人や小物 (z≒0〜3) の下に入る。木箱なども同じ絵なので置き場所は手で避けてある。
// 空・雨と違ってワールドに固定するため、カメラに追従する嵐のルートとは別のルートを船の下に持つ。
// 水面の演出は全てゲームの状態と無関係に作る (他人の位置や死亡を映すと、見えないはずの情報が漏れる)。
//   足元の波紋 — 自分の足元だけ。他人に出すと透明化中のプレイヤーの居場所が分かってしまう。
//   歩く波紋   — 雨が止んだ静けさの中で、誰も居ない水溜りを足跡の波紋が横切る。
//   にじみ     — 死体が水溜りの上に倒れている時だけ、死体の飛沫と同じボディ色が死体から水の中へ広がる。
//                死体そのものが見えている場所に限るので新しい情報は増えない。死体が消えたらすぐ引く。
internal static class MiraPuddles
{
    private const string Res = "EndKnot.Resources.Images.MapAtmosphere.";

    private const float LaunchPadZ = 6.5f;
    private const float BalconyZ = 3.6f;
    private const float Squash = 0.58f; // 見下ろし斜めの視点に合わせて縦を潰す
    private const float WaterRadius = 0.28f; // 画像の幅に対する水の部分の半径 (おおよそ)

    private enum Floor { Concrete, Wood }

    // x, y, 画像の幅 (ワールド単位・水の部分はその半分ほど), 形の番号, 床の種類
    private static readonly (float X, float Y, float W, int Shape, Floor Floor)[] Spots =
    [
        (-4.2f, 2.8f, 2.4f, 0, Floor.Concrete),
        (-3.5f, 0.45f, 2.0f, 1, Floor.Concrete),
        (-5.3f, 0.4f, 1.4f, 2, Floor.Concrete),
        (-10.4f, 3.7f, 2.4f, 3, Floor.Concrete),
        (-10.9f, 2.3f, 1.4f, 2, Floor.Concrete),
        (21.6f, -2.1f, 2.2f, 2, Floor.Wood),
        (26.2f, -1.95f, 2.6f, 0, Floor.Wood),
        (28.0f, -2.3f, 1.5f, 1, Floor.Wood),
        (20.3f, -1.8f, 1.2f, 3, Floor.Concrete) // 兵器室前の六角タイル (明るい床)
    ];

    // どちらの床も濡れると黒ずむ。明るい床は青灰の水色で、暗い木目は色を沈めてツヤ (映り込み) で水だと見せる。
    // 暗い床に明るい色を重ねると白いもやに見えてしまう。
    private static readonly Color ConcreteTint = new(0.2f, 0.29f, 0.43f, 0.58f);
    private static readonly Color WoodTint = new(0.06f, 0.08f, 0.13f, 0.5f);
    private const float ConcreteSheen = 0.5f;
    private const float WoodSheen = 0.55f;

    private const int RipplesPerPuddle = 10;

    // 波紋の種類: 寿命 (秒), 最大の大きさ, 濃さ
    private static readonly (float Life, float Size, float Alpha) RainRipple = (0.7f, 0.42f, 0.85f);
    private static readonly (float Life, float Size, float Alpha) StepRipple = (1f, 0.6f, 1f);
    private static readonly (float Life, float Size, float Alpha) StandRipple = (1.1f, 0.35f, 0.5f);
    private static readonly (float Life, float Size, float Alpha) PhantomRipple = (1.6f, 0.6f, 1f);

    private const float PhantomChance = 0.5f;
    private const float NearbyRange = 6f; // 見える範囲の水溜りだけを選ぶ

    private sealed class Puddle
    {
        public float W, Z, BaseSheen, Timer;
        public Vector2 Center;
        public Color Tint;
        public int Shape;

        // にじみ: 水の形に切り抜いた小さなテクスチャを、死体の位置から描き広げる。
        public SpriteRenderer StainSr;
        public Texture2D StainTex;
        public Il2CppStructArray<Color32> StainPixels;
        public Color StainColor;
        public Vector2 StainOrigin; // テクスチャ上の起点 (ピクセル)
        public float StainAge = -1f, StainFade, StainRedraw;
        public float Phase1, Phase2, Phase3;
        public bool Stained;
        public SpriteRenderer Body, Sheen;
        public SpriteRenderer[] Ripples;
        public Transform[] RippleTf;
        public float[] Age, Life, Size, Alpha;
    }

    private static GameObject _root;
    private static Puddle[] _puddles;

    private static Vector2 _lastFeet;
    private static float _stepTimer;

    // 歩く波紋
    private static Puddle _walkPuddle;
    private static Vector2 _walkFrom, _walkDir;
    private static int _walkStep;
    private static float _walkTimer;
    private const int WalkSteps = 6;

    // にじみ
    private static float _bodyScan;
    private const float BodyScanInterval = 0.5f;
    private const float StainSpreadSeconds = 10f;
    private const float StainClearSeconds = 0.3f;
    private const float StainAlpha = 0.85f;
    private const int StainRes = 80; // 水溜りの画像 (320px) を 1/4 に落とした解像度
    private static byte[][] _waterAlpha; // 形ごとの水の濃さ (StainRes 四方)
    private const float WalkInterval = 0.6f;

    public static void Build(Transform ship)
    {
        _root = new GameObject("EK_MiraPuddles") { layer = 0 };
        _root.transform.SetParent(ship, false);

        var body = new Sprite[4];
        var sheen = new Sprite[4];

        for (int i = 0; i < 4; i++)
        {
            body[i] = Utils.LoadSprite(Res + $"puddle{i}.png", 320f);
            sheen[i] = Utils.LoadSprite(Res + $"puddle{i}_sheen.png", 320f);
        }

        Sprite ripple = Utils.LoadSprite(Res + "ripple.png", 128f);

        _puddles = new Puddle[Spots.Length];

        for (int i = 0; i < Spots.Length; i++)
        {
            (float x, float y, float w, int shape, Floor floor) = Spots[i];
            bool wood = floor == Floor.Wood;
            float z = x > 15f ? BalconyZ : LaunchPadZ;

            var p = new Puddle
            {
                W = w,
                Z = z,
                Shape = shape,
                Center = new Vector2(x, y),
                Tint = wood ? WoodTint : ConcreteTint,
                BaseSheen = wood ? WoodSheen : ConcreteSheen,
                Timer = Random.Range(0f, 0.3f),
                Ripples = new SpriteRenderer[RipplesPerPuddle],
                RippleTf = new Transform[RipplesPerPuddle],
                Age = new float[RipplesPerPuddle],
                Life = new float[RipplesPerPuddle],
                Size = new float[RipplesPerPuddle],
                Alpha = new float[RipplesPerPuddle]
            };

            // 同じ形が並んでも見分けが付かないよう、左右反転と僅かな回転で崩す。
            bool flip = Random.value < 0.5f;
            float rot = Random.Range(-12f, 12f);
            var scale = new Vector3(w * (flip ? -1f : 1f), w * Squash, 1f);

            p.Body = Make($"Puddle{i}", body[shape], new Vector3(x, y, z), scale, rot);
            p.Sheen = Make($"Puddle{i}Sheen", sheen[shape], new Vector3(x, y, z - 0.01f), scale, rot);

            for (int r = 0; r < RipplesPerPuddle; r++)
            {
                p.Ripples[r] = Make($"Puddle{i}Ripple{r}", ripple, new Vector3(x, y, z - 0.02f), Vector3.zero, 0f);
                p.RippleTf[r] = p.Ripples[r].transform;
                p.Age[r] = -1f;
            }

            _puddles[i] = p;
        }

        _walkPuddle = null;
        _bodyScan = 0f;
        _stepTimer = 0f;
        _lastFeet = LocalFeet() ?? Vector2.zero;
    }

    private static SpriteRenderer Make(string name, Sprite sprite, Vector3 pos, Vector3 scale, float rot)
    {
        var go = new GameObject(name) { layer = 0 };
        Transform tf = go.transform;
        tf.SetParent(_root.transform, false);
        tf.localPosition = pos;
        tf.localRotation = Quaternion.Euler(0f, 0f, rot);
        tf.localScale = scale;
        SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.color = Color.clear;
        return sr;
    }

    // 雨が止みきった時に MiraStorm が呼ぶ。近くの誰も居ない水溜りを、見えない何かに歩かせる。
    public static void OnLull()
    {
        if (!_root || _puddles == null || Random.value >= PhantomChance) return;
        if (LocalFeet() is not { } feet) return;

        Puddle p = Nearest(feet, 1.8f);
        if (p == null) return;

        _walkPuddle = p;
        _walkDir = Random.insideUnitCircle.normalized;
        if (_walkDir == Vector2.zero) _walkDir = Vector2.right;
        _walkFrom = -_walkDir * WaterRadius;
        _walkStep = 0;
        _walkTimer = Random.Range(0.5f, 1.5f);
        Logger.Info($"MiraPuddles phantom walk puddle={System.Array.IndexOf(_puddles, p)}", "MiraStorm");
    }

    // fade: 全体の立ち上がり (0→1)。glow: 稲妻の光り具合 (0〜1)。水面が空を映して一瞬明るくなる。
    // rain: 雨の強さ (0〜1)。雨が止むと雨粒の波紋も止み、他の波紋だけが残る。
    // 水溜りだけの不具合で空や雨まで止めないよう、例外はここで受けて水溜りだけを片付ける。
    public static void Animate(float dt, float fade, float glow, float rain)
    {
        if (!_root || _puddles == null) return;

        try
        {
            AnimateCore(dt, fade, glow, rain);
        }
        catch (System.Exception e)
        {
            Utils.ThrowException(e);
            Teardown();
        }
    }

    private static void AnimateCore(float dt, float fade, float glow, float rain)
    {
        ScanBodies(dt);

        foreach (Puddle p in _puddles)
        {
            UpdateStain(p, dt, fade);
            Color t = p.Tint;
            // 光った瞬間は映り込みの筋だけを強く光らせる。濡れ色まで明るくすると周りの床と見分けが付かなくなる。
            p.Body.color = new Color(t.r, t.g, t.b, t.a * fade);
            p.Sheen.color = new Color(0.9f, 0.94f, 1f, Mathf.Min(1f, p.BaseSheen + glow * 1.6f) * fade);

            // 雨粒の波紋: 小さな楕円の輪を水溜りの内側に落とし、広げながら消す。
            p.Timer -= dt;

            if (p.Timer <= 0f)
            {
                p.Timer = Random.Range(0.1f, 0.3f) / p.W / Mathf.Max(rain, 0.05f);
                Vector2 off = Random.insideUnitCircle * (WaterRadius * 0.8f);
                Spawn(p, p.Center + new Vector2(off.x * p.W, off.y * p.W * Squash), RainRipple);
            }

            for (int r = 0; r < p.Ripples.Length; r++)
            {
                if (p.Age[r] < 0f) continue;
                p.Age[r] += dt;
                float k = p.Age[r] / p.Life[r];

                if (k >= 1f)
                {
                    p.Age[r] = -1f;
                    p.Ripples[r].color = Color.clear;
                    continue;
                }

                float size = Mathf.Lerp(0.08f, p.Size[r], 1f - (1f - k) * (1f - k));
                p.RippleTf[r].localScale = new Vector3(size, size * Squash, 1f);
                float a = (1f - k) * p.Alpha[r] * fade;
                p.Ripples[r].color = new Color(0.85f, 0.9f, 1f, Mathf.Min(1f, a + glow * 0.3f * (1f - k)));
            }
        }

        UpdateFootsteps(dt);
        UpdateWalk(dt);
    }

    private static void UpdateFootsteps(float dt)
    {
        if (LocalFeet() is not { } feet) return;

        bool moving = (feet - _lastFeet).magnitude > dt * 0.5f;
        _lastFeet = feet;

        if ((_stepTimer -= dt) > 0f) return;

        Puddle p = PuddleAt(feet);

        if (p == null)
        {
            _stepTimer = 0.1f;
            return;
        }

        Spawn(p, feet + Random.insideUnitCircle * 0.03f, moving ? StepRipple : StandRipple);
        _stepTimer = moving ? 0.26f : Random.Range(1.2f, 1.8f);
    }

    private static void UpdateWalk(float dt)
    {
        if (_walkPuddle == null || (_walkTimer -= dt) > 0f) return;

        Puddle p = _walkPuddle;
        // 左右の足を交互に、進行方向と直角へ少しずらす。
        var side = new Vector2(-_walkDir.y, _walkDir.x) * (0.05f * (_walkStep % 2 == 0 ? 1f : -1f));
        Vector2 local = _walkFrom + _walkDir * (WaterRadius * 2f * _walkStep / (WalkSteps - 1)) + side;
        Spawn(p, p.Center + new Vector2(local.x * p.W, local.y * p.W * Squash), PhantomRipple);

        _walkTimer = WalkInterval * Random.Range(0.9f, 1.15f);
        if (++_walkStep >= WalkSteps) _walkPuddle = null;
    }

    // 死体の出入りは頻繁でないので間隔を空けて探す。非表示にされた死体 (消す系の能力) は無いものとして扱う。
    private static void ScanBodies(float dt)
    {
        if ((_bodyScan -= dt) > 0f) return;
        _bodyScan = BodyScanInterval;

        foreach (Puddle p in _puddles) p.Stained = false;

        foreach (DeadBody body in Object.FindObjectsOfType<DeadBody>())
        {
            if (!body || !IsVisible(body)) continue;
            Vector3 world = body.TruePosition;
            Puddle p = PuddleAt(_root.transform.InverseTransformPoint(world));
            if (p == null || p.Stained) continue;
            if (p.StainAge < 0f && !StartStain(p, body, world)) continue;
            p.Stained = true;
        }
    }

    private static bool StartStain(Puddle p, DeadBody body, Vector3 world)
    {
        if (BodyColor(body) is not { } c || WaterAlpha(p.Shape) == null) return false;

        if (!p.StainTex)
        {
            p.StainTex = new Texture2D(StainRes, StainRes, TextureFormat.ARGB32, false) { wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            p.StainPixels = new Il2CppStructArray<Color32>((long)(StainRes * StainRes));
            Sprite sprite = Sprite.Create(p.StainTex, new Rect(0f, 0f, StainRes, StainRes), new Vector2(0.5f, 0.5f), StainRes, 0, SpriteMeshType.FullRect);
            sprite.hideFlags |= HideFlags.HideAndDontSave;
            // 水溜りの子にして、反転・回転・縦潰しを水の形とそろえる。濡れ色より手前・ツヤの筋より奥。
            p.StainSr = Make($"Puddle{System.Array.IndexOf(_puddles, p)}Stain", sprite, Vector3.zero, Vector3.one, 0f);
            p.StainSr.transform.SetParent(p.Body.transform, false);
            p.StainSr.transform.localPosition = new Vector3(0f, 0f, -0.005f);
        }

        // 飛沫の色そのままだと濡れた床の上で浮くので、少し沈めて水の色に寄せる。
        p.StainColor = new Color(c.r * 0.7f, c.g * 0.7f, c.b * 0.7f, 1f);
        Vector3 local = p.Body.transform.InverseTransformPoint(world);
        p.StainOrigin = new Vector2((local.x + 0.5f) * StainRes, (local.y + 0.5f) * StainRes);
        p.Phase1 = Random.Range(0f, 6.3f);
        p.Phase2 = Random.Range(0f, 6.3f);
        p.Phase3 = Random.Range(0f, 6.3f);
        p.StainAge = 0f;
        p.StainFade = 1f;
        p.StainRedraw = 0f;
        Logger.Info($"MiraPuddles stain puddle={System.Array.IndexOf(_puddles, p)}", "MiraStorm");
        return true;
    }

    private static void UpdateStain(Puddle p, float dt, float fade)
    {
        if (p.StainAge < 0f) return;

        p.StainFade = p.Stained ? 1f : Mathf.MoveTowards(p.StainFade, 0f, dt / StainClearSeconds);

        if (p.StainFade <= 0f)
        {
            p.StainAge = -1f;
            p.StainSr.color = Color.clear;
            return;
        }

        Color c = p.StainColor;
        // 広がるにつれて色が深まる。
        float deepen = 0.6f + 0.4f * Mathf.Clamp01(p.StainAge / StainSpreadSeconds);
        p.StainSr.color = new Color(c.r, c.g, c.b, StainAlpha * deepen * p.StainFade * fade);

        if (p.StainAge >= StainSpreadSeconds) return; // 広がりきったら描き直さない
        p.StainAge += dt;
        if ((p.StainRedraw -= dt) > 0f && p.StainAge < StainSpreadSeconds) return;
        p.StainRedraw = 1f / 20f;
        DrawStain(p, Mathf.Clamp01(p.StainAge / StainSpreadSeconds));
    }

    // 死体の下の小さな溜まりから、縁が波打つ輪でじわじわ押し出す。縁の出っ張りは方向ごとに伸びる速さが違い、
    // 時間とともに形を変えながら這うように進む。縁は表面張力で少し濃く盛り上がって見せる。
    // 画像の縦はゲーム内で潰れるので、距離は潰した後の見た目で測って丸く広がって見えるようにする。
    private static void DrawStain(Puddle p, float k)
    {
        byte[] water = WaterAlpha(p.Shape);
        float radius = StainRes * 0.95f * (0.08f + 0.92f * Mathf.Pow(k, 0.8f));
        float wobble = 0.12f + 0.18f * k; // 進むほど縁の凸凹が育つ
        float drift = p.StainAge * 0.15f; // 出っ張りの向きがゆっくり移ろう
        Il2CppStructArray<Color32> px = p.StainPixels;

        for (int y = 0, i = 0; y < StainRes; y++)
        {
            float dy = (y + 0.5f - p.StainOrigin.y) * Squash;

            for (int x = 0; x < StainRes; x++, i++)
            {
                byte a = water[i];

                if (a == 0)
                {
                    px[i] = new Color32(255, 255, 255, 0);
                    continue;
                }

                float dx = x + 0.5f - p.StainOrigin.x;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                float th = Mathf.Atan2(dy, dx);
                float shape = Mathf.Sin(3f * th + p.Phase1 + drift) + 0.55f * Mathf.Sin(5f * th + p.Phase2 - drift * 1.3f) + 0.3f * Mathf.Sin(9f * th + p.Phase3 + drift * 0.7f);
                float edge = radius * (1f + wobble * shape);
                float m = Mathf.Clamp01((edge - d) / 5f);
                float core = 0.65f + 0.35f * Mathf.Clamp01(1f - d / Mathf.Max(edge, 1f)); // 起点ほど濃い
                float rim = 0.3f * Mathf.Clamp01(1f - Mathf.Abs(edge - d - 3f) / 3f); // 縁の盛り上がり
                px[i] = new Color32(255, 255, 255, (byte)Mathf.Min(255f, a * m * (core + rim)));
            }
        }

        p.StainTex.SetPixels32(px);
        p.StainTex.Apply(false);
    }

    // 水の形は水溜りの画像のアルファから取る。ゲーム内の画像は読み出し不可で読み込まれているので、埋め込みから読み直して縮める。
    private static byte[] WaterAlpha(int shape)
    {
        _waterAlpha ??= new byte[4][];
        if (_waterAlpha[shape] != null) return _waterAlpha[shape];

        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(Res + $"puddle{shape}.png");
        if (stream == null) return null;
        var ms = new MemoryStream();
        stream.CopyTo(ms);

        var tex = new Texture2D(2, 2, TextureFormat.ARGB32, false);

        try
        {
            if (!tex.LoadImage(ms.ToArray(), false)) return null;
            Il2CppStructArray<Color32> src = tex.GetPixels32();
            int w = tex.width, h = tex.height;
            var alpha = new byte[StainRes * StainRes];

            for (int y = 0; y < StainRes; y++)
            for (int x = 0; x < StainRes; x++)
                alpha[y * StainRes + x] = src[(y * h / StainRes + h / StainRes / 2) * w + x * w / StainRes + w / StainRes / 2].a;

            return _waterAlpha[shape] = alpha;
        }
        finally
        {
            Object.Destroy(tex);
        }
    }

    private static bool IsVisible(DeadBody body)
    {
        if (!body.gameObject.activeInHierarchy || body.bodyRenderers == null) return false;

        foreach (SpriteRenderer r in body.bodyRenderers)
            if (r && r.enabled && r.gameObject.activeInHierarchy) return true;

        return false;
    }

    // 死体の飛沫はボディ色のマテリアルで塗られている。見た目の色をそのまま拾うので、変装中に倒れた死体なども画面どおりになる。
    private static Color? BodyColor(DeadBody body)
    {
        SpriteRenderer sr = body.bloodSplatter ? body.bloodSplatter : body.bodyRenderers.Length > 0 ? body.bodyRenderers[0] : null;
        if (!sr) return null;
        Material m = sr.material;
        return m && m.HasProperty("_BodyColor") ? m.GetColor("_BodyColor") : null;
    }

    // 大きな水溜りでは雨粒の波紋だけで枠がほぼ埋まる (実測 10 枠中 8)。雨粒以外は空きが無ければ一番消えかけの輪を譲らせる。
    private static void Spawn(Puddle p, Vector2 pos, (float Life, float Size, float Alpha) kind)
    {
        int slot = -1;
        float oldest = -1f;

        for (int r = 0; r < p.Ripples.Length; r++)
        {
            if (p.Age[r] < 0f)
            {
                slot = r;
                break;
            }

            float k = p.Age[r] / p.Life[r];
            if (kind == RainRipple || k <= oldest) continue;
            oldest = k;
            slot = r;
        }

        if (slot < 0) return;
        p.Age[slot] = 0f;
        // 同じ大きさの輪が並ぶと機械的に見えるので、1つずつ大きさと寿命を散らす。
        p.Life[slot] = kind.Life * Random.Range(0.85f, 1.15f);
        p.Size[slot] = kind.Size * Random.Range(0.75f, 1.25f);
        p.Alpha[slot] = kind.Alpha;
        p.RippleTf[slot].localPosition = new Vector3(pos.x, pos.y, p.Z - 0.02f);
        p.RippleTf[slot].localScale = Vector3.zero;
    }

    // 生きている自分の足元 (船のローカル座標)。幽霊は浮いているので水を踏まない。
    private static Vector2? LocalFeet()
    {
        PlayerControl lp = PlayerControl.LocalPlayer;
        if (!lp || lp.Data == null || lp.Data.IsDead || lp.inVent) return null;
        return _root.transform.InverseTransformPoint(lp.GetTruePosition());
    }

    private static bool Contains(Puddle p, Vector2 pos)
    {
        float r = WaterRadius * p.W;
        float dx = (pos.x - p.Center.x) / r;
        float dy = (pos.y - p.Center.y) / (r * Squash);
        return dx * dx + dy * dy <= 1f;
    }

    private static Puddle PuddleAt(Vector2 pos)
    {
        foreach (Puddle p in _puddles)
            if (Contains(p, pos)) return p;

        return null;
    }

    // 自分が立っていない、近くのある程度大きな水溜り。
    private static Puddle Nearest(Vector2 from, float minW)
    {
        Puddle best = null;
        float bestDist = NearbyRange;

        foreach (Puddle p in _puddles)
        {
            if (p.W < minW || Contains(p, from)) continue;
            float d = Vector2.Distance(from, p.Center);
            if (d >= bestDist) continue;
            best = p;
            bestDist = d;
        }

        return best;
    }

    public static void Teardown()
    {
        // にじみのテクスチャとスプライトは HideAndDontSave なので、ルートを壊しても残る。
        if (_puddles != null)
        {
            foreach (Puddle p in _puddles)
            {
                if (p.StainSr && p.StainSr.sprite) Object.Destroy(p.StainSr.sprite);
                if (p.StainTex) Object.Destroy(p.StainTex);
            }
        }

        if (_root) Object.Destroy(_root);
        _root = null;
        _puddles = null;
        _walkPuddle = null;
    }
}
