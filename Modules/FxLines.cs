using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace EndKnot.Modules;

// 線の部品: 稲妻とひびを、四角の継ぎ合わせでなく 1 本の折れ線 (LineRenderer) で描く。
// 1 本の線 = 下の線 (太い・にじみか暗い縁) + 芯 (細い) の 2 枚重ね。太さは根元から先へ細る曲線で、角は丸める。
// 形は中点変位 (まっすぐな線の真ん中を横にずらしては分ける) で作る。伸びる様子は見せる点の数を進めるだけ。
// 稲妻は同じ道筋に何度か光り直し (再撃)、最後は残光の色へ移って消える。
// 描くのは手元の画面だけ。メインスレッド専用。
internal static class FxLines
{
    internal enum Taper : byte
    {
        Trunk,   // 根元 1.0 → 中 0.7 → 先 0.25 (これより細いと画素より細くなって点線に見える)
        Branch,  // 根元 0.5 → 先 0.15 (枝。幅の指定は幹と同じ基準のまま渡す)
        Even,
        Spindle  // 両端 0.25 → 中 1.0 (爪痕・刃筋。点が 2 つだと中が太らないので途中の点を渡す)
    }

    // 線 1 本の見た目と時間。Under は芯の下に敷く太い線 (幅 = Width × UnderMul)
    internal struct Spec
    {
        public float Width;
        public Taper Taper;
        public Color Core, Under, After;
        public float CoreAlpha, UnderAlpha, UnderMul;
        public bool UnderGlow;  // true = にじみ (柔らかい縁・光る重ね方) / false = 暗いにじみ (柔らかい縁・普通の重ね方)
        public bool CoreGlow;   // true = 芯も光る重ね方
        public float Delay, Grow, Life, FadeOutFrom;
        public float FadeIn;          // 現れる時間 (秒。0 = 0.02 秒)
        public float CoreFadeOutFrom; // 芯だけ先に消え始める (0 = FadeOutFrom と同じ)。消えた後は下の線が残像になる
        public bool Loop;             // 終点を始点へつなぐ (六角形など)。伸び切ってからつなぐ
        public bool Decel;      // 伸びが減速して止まる (ひび)
        public int Strikes;     // 再撃の回数 (0 = 光り直さない)
        public float StrikeGap; // 再撃の間隔 (秒)
        public float Jitter;    // 再撃の時に道筋を揺らす幅 (u)
        public bool Vision, Floor;
        public int Order;       // 視界の外で隠さない時の描画順
        public float Z;         // 視界の外で隠す時の z (床の物は点ごとに y から決める)
    }

    private sealed class Lr
    {
        public GameObject Go;
        public LineRenderer R;
        public Il2CppStructArray<Vector3> Buf;
    }

    private sealed class Line
    {
        public Lr Under, Core;
        public Spec S;
        public Vector3[] Pts;
        public int N;
        public float Start;
        public int Shown;
        public int StrikeDone;
        public float LastStrike;
        public float LastAlpha = -1f, LastUnder = -1f, LastFlash = -1f, LastAfter = -1f;
        public int Id;          // 出し続ける線の番号 (0 = 寿命で消える普通の線)
        public float Mul = 1f;  // 後から変える濃さの倍率
        public Vector2 Shift;   // 線ごと動かした量
    }

    internal const int MaxPoints = 64;
    private const int PoolSize = 256;
    private const float FlashDecay = 0.07f;
    private const float UnderBehind = 0.0005f;

    private static readonly Stack<Lr> Pool = new();
    private static readonly List<Line> Live = [];
    private static readonly Stack<Vector3[]> PtsPool = new();
    private static int _created;
    private static Material _solid, _soft, _glow, _coreGlow;
    private static bool _prepared, _prepareFailed;
    private static Texture2D _softTex, _hardTex;
    private static AnimationCurve _trunk, _branch, _even, _spindle;

    internal static int LiveCount => Live.Count;
    private static int _nextId;

    // 描ける状態か (素材を作れない環境では出し続ける演出の代わりに元の見た目を残す判断に使う)
    internal static bool Ready => Prepare();

    // 作り置きを 1 本だけ足す。足りていれば false (試合開始時の下ごしらえで少しずつ呼ぶ)
    internal static bool WarmOne()
    {
        if (_created >= PoolSize) return false;

        Lr lr = Create();
        if (lr == null) return false;

        Pool.Push(lr);
        return true;
    }

    // 線を 1 本出す。pts は世界座標の折れ線 (n 点・入れ物は原点に置くので局所座標 = 世界座標)。点の配列は呼び出し後に使い回してよい
    internal static void Play(Vector2[] pts, int n, Spec s) => Start(pts, n, s, 0);

    // 消えるまで出し続ける線。戻り値の番号で Move / SetMul / Stop する (0 = 出せなかった)。Life と FadeOutFrom は無視する
    internal static int PlayHeld(Vector2[] pts, int n, Spec s)
    {
        s.Life = 1e6f;
        s.FadeOutFrom = 1f;
        s.CoreFadeOutFrom = 0f;
        int id = ++_nextId;
        if (_nextId >= int.MaxValue - 1) _nextId = 0;
        return Start(pts, n, s, id) ? id : 0;
    }

    // 出し続ける線を、出した時の位置から offset だけずらした所へ動かす
    internal static void Move(int id, Vector2 offset)
    {
        Line l = Find(id);
        if (l == null || (l.Shift.x == offset.x && l.Shift.y == offset.y)) return;

        l.Shift = offset;
        Place(l.Core, offset, false);
        if (l.Under != null) Place(l.Under, offset, true);
    }

    // 出し続ける線の濃さの倍率 (0 で見えなくなる)
    internal static void SetMul(int id, float mul)
    {
        Line l = Find(id);
        if (l != null) l.Mul = mul;
    }

    // 出し続ける線を fade 秒で消す
    internal static void Stop(int id, float fade)
    {
        Line l = Find(id);
        if (l == null) return;

        float t = FxMath.Max(Time.time - l.Start, 0f);
        float life = t + FxMath.Max(fade, 0.01f);
        l.S.Life = life;
        l.S.FadeOutFrom = t / life;
        l.Id = 0;
    }

    private static Line Find(int id)
    {
        if (id <= 0) return null;
        for (int i = 0; i < Live.Count; i++)
            if (Live[i].Id == id) return Live[i];
        return null;
    }

    private static void Place(Lr lr, Vector2 offset, bool under) => lr.Go.transform.position = FxMath.V3(offset.x, offset.y, under ? UnderBehind : 0f);

    private static bool Start(Vector2[] pts, int n, Spec s, int id)
    {
        if (n < 2) return false;
        if (n > MaxPoints) n = MaxPoints;

        Lr core = Take();
        if (core == null) return false;

        Lr under = null;
        if (s.UnderMul > 0f && s.UnderAlpha > 0f)
        {
            under = Take();
            if (under == null)
            {
                Give(core);
                return false;
            }
        }

        Vector3[] p = PtsPool.Count > 0 ? PtsPool.Pop() : new Vector3[MaxPoints];
        for (int i = 0; i < n; i++)
            p[i] = new Vector3(pts[i].x, pts[i].y, s.Vision && s.Floor ? pts[i].y / 1000f + 0.01f : s.Vision ? s.Z : 0f);

        var line = new Line { Core = core, Under = under, S = s, Pts = p, N = n, Start = Time.time + s.Delay, Shown = -1, LastStrike = -1f, Id = id };

        try
        {
            Setup(line.Core, s, false);
            if (under != null) Setup(line.Under, s, true);
        }
        catch (System.Exception e)
        {
            Release(line);
            Logger.Warn($"FxLines: {e.Message}", "FxLines");
            return false;
        }

        Live.Add(line);
        return true;
    }

    internal static void Tick()
    {
        if (Live.Count == 0) return;

        float now = Time.time;

        for (int i = Live.Count - 1; i >= 0; i--)
        {
            Line l = Live[i];
            float t = now - l.Start;
            if (t < 0f) continue;

            if (t >= l.S.Life)
            {
                Release(l);
                Live.RemoveAt(i);
                continue;
            }

            try { Step(l, t, now); }
            catch (System.Exception e)
            {
                Release(l);
                Live.RemoveAt(i);
                Logger.Warn($"FxLines: {e.Message}", "FxLines");
            }
        }
    }

    internal static void ClearAll()
    {
        for (int i = Live.Count - 1; i >= 0; i--) Release(Live[i]);
        Live.Clear();
    }

    private static void Step(Line l, float t, float now)
    {
        Spec s = l.S;

        // 伸びる: 見せる点の数を進め、最後の 1 点は途中まで
        float g = s.Grow <= 0f ? 1f : FxMath.Clamp01(t / s.Grow);
        if (s.Decel) g = 1f - (1f - g) * (1f - g);
        float head = g * (l.N - 1);
        int full = (int)head;
        int shown = full + (full < l.N - 1 ? 2 : 1);

        if (shown != l.Shown || full < l.N - 1)
        {
            if (shown != l.Shown)
            {
                l.Shown = shown;
                Show(l.Core, l, shown);
                if (l.Under != null) Show(l.Under, l, shown);
            }

            if (full < l.N - 1)
            {
                float k = head - full;
                Vector3 a = l.Pts[full], b = l.Pts[full + 1];
                var tip = new Vector3(a.x + (b.x - a.x) * k, a.y + (b.y - a.y) * k, a.z + (b.z - a.z) * k);
                l.Core.R.SetPosition(shown - 1, tip);
                if (l.Under != null) l.Under.R.SetPosition(shown - 1, tip);
            }
        }

        // 明るさ: 現れて (0.02 秒) → 再撃のたびに跳ね上がって落ちる → 終わりに向けて消える
        float a0 = FxMath.Clamp01(t / FxMath.Max(s.FadeIn, 0.02f));
        float flash = 0f;

        if (s.Strikes > 0)
        {
            int due = FxMath.Min(s.Strikes, (int)(t / FxMath.Max(s.StrikeGap, 0.01f)));
            if (due > l.StrikeDone)
            {
                l.StrikeDone = due;
                l.LastStrike = t;
                if (s.Jitter > 0f) Rejitter(l);
            }

            float since = l.LastStrike >= 0f ? t - l.LastStrike : t;
            flash = FxMath.Exp(-since / FlashDecay);
        }

        float u = t / s.Life;
        float fade = Fade(u, s.FadeOutFrom);
        float coreFade = s.CoreFadeOutFrom > 0f ? Fade(u, s.CoreFadeOutFrom) : fade;

        // 再撃が終わったら残光の色へ移る
        float afterK = 0f;
        if (s.Strikes > 0)
        {
            float endStrike = s.Strikes * s.StrikeGap + FlashDecay * 2f;
            if (t > endStrike) afterK = FxMath.Clamp01((t - endStrike) / 0.15f);
        }

        float bright = s.Strikes > 0 ? 0.35f + 0.65f * flash : 1f;
        float alpha = a0 * coreFade * bright * l.Mul;
        float ua = a0 * fade * (s.Strikes > 0 ? 0.5f + 0.5f * flash : 1f) * s.UnderAlpha * l.Mul;

        // 伸び切って消え始めるまでの間は色も太さも変わらないので書き直さない
        if (alpha == l.LastAlpha && ua == l.LastUnder && flash == l.LastFlash && afterK == l.LastAfter) return;
        l.LastAlpha = alpha;
        l.LastUnder = ua;
        l.LastFlash = flash;
        l.LastAfter = afterK;

        Color c = Mix(s.Core, s.After, afterK);
        l.Core.R.startColor = FxMath.Rgba(c.r, c.g, c.b, alpha * s.CoreAlpha);
        l.Core.R.endColor = FxMath.Rgba(c.r, c.g, c.b, alpha * s.CoreAlpha * 0.6f);
        l.Core.R.widthMultiplier = s.Width * (1f + 0.25f * flash) * (1f - afterK * 0.4f);

        if (l.Under != null)
        {
            Color uc = Mix(s.Under, s.After, afterK);
            l.Under.R.startColor = FxMath.Rgba(uc.r, uc.g, uc.b, ua);
            l.Under.R.endColor = FxMath.Rgba(uc.r, uc.g, uc.b, ua * 0.6f);
            l.Under.R.widthMultiplier = s.Width * s.UnderMul * (1f + 0.3f * flash);
        }
    }

    private static float Fade(float u, float from) => u > from ? 1f - (u - from) / FxMath.Max(1f - from, 0.001f) : 1f;

    private static Color Mix(Color a, Color b, float k) => k <= 0f ? a : FxMath.Rgba(a.r + (b.r - a.r) * k, a.g + (b.g - a.g) * k, a.b + (b.b - a.b) * k);

    // 再撃: 端の 2 点は動かさず、途中の点を少しだけ横へ揺らし直す
    private static void Rejitter(Line l)
    {
        float j = l.S.Jitter;
        for (int i = 1; i < l.N - 1; i++)
        {
            Vector3 q = l.Pts[i];
            l.Pts[i] = new Vector3(q.x + FxMath.Range(-j, j), q.y + FxMath.Range(-j, j) * 0.5f, q.z);
        }

        l.Shown = -1;
    }

    private static void Show(Lr lr, Line l, int count)
    {
        for (int i = 0; i < count; i++) lr.Buf[i] = l.Pts[i];
        lr.R.positionCount = count;
        lr.R.SetPositions(lr.Buf);
        lr.R.loop = l.S.Loop && count == l.N;
    }

    private static void Setup(Lr lr, Spec s, bool under)
    {
        LineRenderer r = lr.R;
        r.positionCount = 0;
        r.widthCurve = s.Taper switch { Taper.Trunk => _trunk, Taper.Branch => _branch, Taper.Spindle => _spindle, _ => _even };
        r.loop = false;
        r.widthMultiplier = s.Width * (under ? s.UnderMul : 1f);
        r.sharedMaterial = under ? s.UnderGlow ? _glow : _soft : s.CoreGlow ? _coreGlow : _solid;
        r.startColor = FxMath.Rgba(1f, 1f, 1f, 0f);
        r.endColor = FxMath.Rgba(1f, 1f, 1f, 0f);

        MeshRenderer shadow = s.Vision && HudManager.InstanceExists ? HudManager.Instance.ShadowQuad : null;
        if (shadow)
        {
            r.sortingLayerID = shadow.sortingLayerID;
            r.sortingOrder = shadow.sortingOrder;
        }
        else
        {
            r.sortingLayerID = 0;
            r.sortingOrder = s.Order + (under ? 0 : 1);
        }

        // 視界の外で隠す時は全部が影の板と同じ描画順になり、前後は z だけで決まる。下の線を芯よりわずかに奥へ置く
        lr.Go.transform.position = new Vector3(0f, 0f, under ? UnderBehind : 0f);
        lr.Go.SetActive(true);
    }

    private static Lr Take()
    {
        while (Pool.Count > 0)
        {
            Lr lr = Pool.Pop();
            if (lr.Go && lr.R) return lr;
            _created--;
        }

        return _created < PoolSize ? Create() : null;
    }

    private static void Give(Lr lr)
    {
        if (lr == null) return;
        if (!lr.Go)
        {
            _created--;
            return;
        }

        lr.Go.SetActive(false);
        Pool.Push(lr);
    }

    private static void Release(Line l)
    {
        Give(l.Core);
        Give(l.Under);
        if (l.Pts != null) PtsPool.Push(l.Pts);
        l.Pts = null;
    }

    private static Lr Create()
    {
        if (!Prepare()) return null;

        var go = new GameObject("FxLine") { layer = 0 };
        Object.DontDestroyOnLoad(go);
        LineRenderer r = go.AddComponent<LineRenderer>();
        r.useWorldSpace = false;
        r.numCornerVertices = 2;
        r.numCapVertices = 2;
        r.alignment = LineAlignment.View;
        r.textureMode = LineTextureMode.Stretch;
        r.receiveShadows = false;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        go.SetActive(false);
        _created++;
        return new Lr { Go = go, R = r, Buf = new Il2CppStructArray<Vector3>(MaxPoints) };
    }

    private static bool Prepare()
    {
        if (_prepared) return true;
        if (_prepareFailed) return false;

        try
        {
            _trunk = new AnimationCurve(new Keyframe[] { new(0f, 1f), new(0.5f, 0.7f), new(1f, 0.25f) });
            _branch = new AnimationCurve(new Keyframe[] { new(0f, 0.5f), new(1f, 0.15f) });
            _even = new AnimationCurve(new Keyframe[] { new(0f, 1f), new(1f, 1f) });
            _spindle = new AnimationCurve(new Keyframe[] { new(0f, 0.25f), new(0.5f, 1f), new(1f, 0.25f) });

            // 幅方向の明るさ: 柔らかい縁 (にじみ用のガウス) と、芯用のほぼ平らな帯 (縁 1 画素だけ落とす)
            _softTex = Keep(ProfileTexture(v => FxMath.Exp(-v * v * 5f)));
            _hardTex = Keep(ProfileTexture(v => v < 0.75f ? 1f : FxMath.Clamp01((1f - v) / 0.25f)));

            // 普通の重ね方は SpriteRenderer の標準と同じ物を借りる
            var probe = new GameObject("FxLineProbe");
            Material sprite = probe.AddComponent<SpriteRenderer>().sharedMaterial;
            _solid = sprite ? Keep(new Material(sprite) { mainTexture = _hardTex }) : null;
            Object.Destroy(probe);

            if (!_solid)
            {
                _prepareFailed = true;
                return false;
            }

            _soft = Keep(new Material(_solid) { mainTexture = _softTex });
            _glow = _soft;
            _coreGlow = _solid;
        }
        catch (System.Exception e)
        {
            _prepareFailed = true;
            Utils.ThrowException(e);
            return false;
        }

        // 光る重ね方は粒のシェーダ (事前乗算 + 覆い) を借りる。バンドルが無い・作れない時は普通の重ね方のまま描く
        try
        {
            GameObject sparks = FxShaderBundle.ParticlePreset((int)FxParticles.Preset.Sparks);
            ParticleSystemRenderer pr = sparks ? sparks.GetComponent<ParticleSystemRenderer>() : null;
            Material pm = pr ? pr.sharedMaterial : null;

            if (pm)
            {
                Material glow = Keep(new Material(pm) { mainTexture = _softTex });
                glow.SetFloat("_Cover", 0.3f);
                Material coreGlow = Keep(new Material(pm) { mainTexture = _hardTex });
                coreGlow.SetFloat("_Cover", 0.7f);
                _glow = glow;
                _coreGlow = coreGlow;
            }
        }
        catch (System.Exception e) { Logger.Warn($"FxLines glow material: {e.Message}", "FxLines"); }

        _prepared = true;
        return true;
    }

    // 試合をまたいで持つ素材: シーン切り替えの破棄と、使われていない資産の片付けの両方から外す
    private static T Keep<T>(T asset) where T : Object
    {
        if (!asset) return asset;

        Object.DontDestroyOnLoad(asset);
        asset.hideFlags |= HideFlags.DontUnloadUnusedAsset;
        return asset;
    }

    // 縦 32 画素の帯 (線の幅方向)。v = 中心からの距離 0〜1
    private static Texture2D ProfileTexture(System.Func<float, float> alpha)
    {
        const int h = 32, w = 4;
        var bytes = new byte[w * h * 4];

        for (int y = 0; y < h; y++)
        {
            float v = FxMath.Abs((y + 0.5f) / h * 2f - 1f);
            byte a = (byte)(FxMath.Clamp01(alpha(v)) * 255f);

            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                bytes[o] = 255;
                bytes[o + 1] = 255;
                bytes[o + 2] = 255;
                bytes[o + 3] = a;
            }
        }

        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        tex.LoadRawTextureData(bytes);
        tex.Apply(false, true);
        return tex;
    }

    // ── 形 ────────────────────────────────────────────

    // a→b を中点変位で折る。levels 段 (点の数 = 2^levels + 1)・初期のずれ幅 rough (u)・段ごとに decay 倍。
    // squashY < 1 なら a を中心に縦を潰す (床を見下ろした形)。戻り値は点の数
    internal static int Fractal(Vector2 a, Vector2 b, int levels, float rough, float decay, Vector2[] buf, float squashY = 1f)
    {
        int n = (1 << levels) + 1;
        if (n > buf.Length) n = buf.Length;

        int last = n - 1;
        buf[0] = a;
        buf[last] = b;

        float dx = b.x - a.x, dy = b.y - a.y;
        float len = FxMath.Sqrt(dx * dx + dy * dy);
        float nx = len > 0f ? -dy / len : 0f, ny = len > 0f ? dx / len : 1f;
        float d = rough;

        for (int step = last; step > 1; step /= 2)
        {
            int half = step / 2;
            for (int i = half; i < last; i += step)
            {
                Vector2 p = buf[i - half], q = buf[i + half];
                float off = FxMath.Range(-d, d);
                buf[i] = FxMath.V2((p.x + q.x) * 0.5f + nx * off, (p.y + q.y) * 0.5f + ny * off);
            }

            d *= decay;
        }

        if (squashY < 1f)
            for (int i = 1; i < n; i++) buf[i] = FxMath.V2(buf[i].x, a.y + (buf[i].y - a.y) * squashY);

        return n;
    }
}
