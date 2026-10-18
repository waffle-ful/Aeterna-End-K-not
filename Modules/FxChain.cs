using System.Collections.Generic;
using UnityEngine;

namespace EndKnot.Modules;

// 鎖の部品: 繋がれた 2 人がそれぞれ手で端を握り、その間に金属の輪 (正面向きと横向きを交互) を並べる。
// 鎖の長さは決まっていて、近いと垂れ下がり (放物線)、離れて張ると細かく震える。照りが 1.5 秒おきに端から端へ走る。
// 出す時は両端から真ん中へ伸び、切れる時は真ん中から両側へ弾けて輪が落ちる。手は本人の色 (自作の握った手)。
// 描くのは手元の画面だけ。メインスレッド専用
internal static class FxChain
{
    internal const int MaxLinks = 64;
    private const int MaxChains = 8;
    private const float Pitch = 0.125f;
    private const float HandOut = 0.36f;    // 体の中心から手までの距離
    private const float HandDown = 0.08f;
    private const float HandSize = 0.24f;    // 握った手の絵 (1.4u 幅) の倍率
    private const float FeetDown = 0.45f;   // 体の中心から床までの高さ (影を落とす所)
    private const float GrowTime = 0.35f;
    private const float SnapTime = 0.45f;
    private const float CuffSwivel = 31f / 320f;   // 手錠の輪の中心から鎖の付け根 (回り金) まで (輪の倍率 1 の時)

    private sealed class Chain
    {
        public GameObject Root;
        public SpriteRenderer[] Links, Shadows;
        public SpriteRenderer HandA, HandB, CuffA, CuffB, RingBackA, RingBackB;
        public Transform[] LinkTf, ShadowTf;
        public Transform HandATf, HandBTf, CuffATf, CuffBTf, RingBackATf, RingBackBTf;
        public int Id;
        public float Hand;
        public float Z;
        public bool Cuffs;
        public float RingSize, LinkSize, HandDrop, HandTilt;
        public Vector2 RingA, RingB;
        public float Length;
        public float Start;
        public bool Vision;
        public bool Snapping;
        public float SnapStart;
        public int Shown;
        public Vector2[] Pos, Vel;
        public float[] Rot, Spin;
    }

    private static readonly List<Chain> Live = [];
    private static readonly Stack<Chain> Pool = new();
    private static int _nextId;
    private static Sprite _face, _edge, _cuff, _shadow, _grip, _ringBack, _ringFront;
    private static bool _prepared, _prepareFailed;

    internal static bool Ready => Prepare();

    // 鎖を出す。length = 鎖そのものの長さ (手と手の間・u)。戻り値は番号 (0 = 出せなかった)
    internal static int Start(float length, int colorA, int colorB, bool vision, int order, float z)
    {
        if (!Prepare() || Live.Count >= MaxChains) return 0;

        Chain c = null;
        try
        {
            c = Pool.Count > 0 ? Pool.Pop() : Create();
            if (c == null) return 0;

            c.Id = ++_nextId;
            c.Hand = HandSize;
            c.Cuffs = false;
            c.LinkSize = 1f;
            c.Length = FxMath.Max(length, 0.3f);
            c.Start = Time.time;
            c.Vision = vision;
            c.Snapping = false;
            c.Shown = 0;

            for (int i = 0; i < MaxLinks; i++) c.Links[i].sprite = (i & 1) == 0 ? _face : _edge;
            for (int i = 0; i < c.Shadows.Length; i++) c.Shadows[i].sprite = _shadow;
            c.CuffA.sprite = c.CuffB.sprite = _cuff;
            c.HandA.sprite = c.HandB.sprite = _grip;

            Material mat = FxHands.PlayerMat();
            if (mat)
            {
                c.HandA.sharedMaterial = mat;
                c.HandB.sharedMaterial = mat;
            }

            PlayerMaterial.SetColors(ClampColor(colorA), c.HandA);
            PlayerMaterial.SetColors(ClampColor(colorB), c.HandB);

            Sort(c, vision, order, z);

            for (int i = 0; i < MaxLinks; i++)
            {
                c.Links[i].color = FxMath.Rgba(1f, 1f, 1f, 1f);
                c.Links[i].enabled = false;
                c.LinkTf[i].localScale = FxMath.V3(1f, 1f, 1f);
            }

            c.RingBackA.enabled = c.RingBackB.enabled = false;
            c.CuffATf.localScale = c.CuffBTf.localScale = FxMath.V3(1f, 1f, 1f);

            for (int i = 0; i < c.Shadows.Length; i++) c.Shadows[i].enabled = false;

            c.HandA.color = c.HandB.color = c.CuffA.color = c.CuffB.color = FxMath.Rgba(1f, 1f, 1f, 1f);
            c.Root.SetActive(true);
            Live.Add(c);
            return c.Id;
        }
        catch (System.Exception e)
        {
            Utils.ThrowException(e);
            if (c != null && c.Root)
            {
                c.Root.SetActive(false);
                Pool.Push(c);
            }

            return 0;
        }
    }

    // 2 人の体の位置から、手と鎖を置き直す (毎フレーム)。hidden なら鎖ごと見えなくする
    internal static void Place(int id, Vector2 a, Vector2 b, bool hidden)
    {
        Chain c = Find(id);
        if (c == null || c.Snapping) return;

        if (hidden)
        {
            if (c.Root.activeSelf) c.Root.SetActive(false);
            return;
        }

        if (!c.Root.activeSelf) c.Root.SetActive(true);

        float now = Time.time;
        float dx = b.x - a.x, dy = b.y - a.y;
        float dist = FxMath.Sqrt(dx * dx + dy * dy);
        float ux = dist > 0.001f ? dx / dist : 1f, uy = dist > 0.001f ? dy / dist : 0f;
        float ang = FxMath.Atan2(uy, ux) * FxMath.Rad2Deg;

        float ax, ay, bx, by;
        if (c.Cuffs)
        {
            // 手錠: a・b は輪の中心。鎖は輪の内側の回り金から出る
            PlaceCuffs(c, a, b, ang, now);
            float sw = CuffSwivel * c.RingSize;
            ax = a.x + ux * sw;
            ay = a.y + uy * sw;
            bx = b.x - ux * sw;
            by = b.y - uy * sw;
        }
        else
        {
            // 手: 体から相手の方へ伸ばした所。拳の向きは鎖の出る向き、左向きの時は上下を返して親指を上に保つ
            float hax = a.x + ux * HandOut, hay = a.y + uy * HandOut - HandDown;
            float hbx = b.x - ux * HandOut, hby = b.y - uy * HandOut - HandDown;
            PlaceHand(c.HandATf, c.HandA, c.CuffATf, hax, hay, ang, ux, c.Hand);
            PlaceHand(c.HandBTf, c.HandB, c.CuffBTf, hbx, hby, ang + 180f, -ux, c.Hand);

            // 鎖は拳の中から出る
            ax = hax + ux * 0.05f;
            ay = hay + uy * 0.05f;
            bx = hbx - ux * 0.05f;
            by = hby - uy * 0.05f;
        }

        float ex = bx - ax, ey = by - ay;
        float d = FxMath.Sqrt(ex * ex + ey * ey);

        // 垂れ: 長さ L の鎖を距離 d に張った時の放物線の深さ (L ≈ d + 8h²/3d)。張り切ったら震える
        float L = c.Length;
        float sag = d < L ? FxMath.Min(FxMath.Sqrt(0.375f * d * (L - d)), 1.2f) : 0f;
        float shake = d >= L * 0.98f ? 0.01f : 0f;
        float arc = FxMath.Max(d, FxMath.Min(L, d + 2.7f * sag * sag / FxMath.Max(d, 0.1f)));
        int n = FxMath.Clamp((int)(arc / (Pitch * c.LinkSize) + 0.5f), 2, MaxLinks);

        // 出始め: 両端から真ん中へ伸びる
        float grow = FxMath.Clamp01((now - c.Start) / GrowTime);

        // 照り: 1.5 秒おきに 0.5 秒かけて端から端へ
        float gt = FxMath.Repeat(now - c.Start, 1.5f);
        float glint = gt < 0.5f ? gt / 0.5f : -10f;

        float px = -ey / FxMath.Max(d, 0.001f), py = ex / FxMath.Max(d, 0.001f);
        float feetA = a.y - FeetDown, feetB = b.y - FeetDown;

        for (int i = 0; i < MaxLinks; i++)
        {
            SpriteRenderer sr = c.Links[i];
            if (i >= n)
            {
                if (sr.enabled) sr.enabled = false;
                continue;
            }

            float t = (i + 0.5f) / n;
            bool show = FxMath.Min(t, 1f - t) * 2f <= grow;
            if (sr.enabled != show) sr.enabled = show;
            if (!show) continue;

            float s4 = 4f * sag * t * (1f - t);
            float wob = shake > 0f ? FxMath.Sin(now * 113f + i * 1.7f) * shake : 0f;
            float x = ax + ex * t + px * wob;
            float y = ay + ey * t - s4 + py * wob;

            // 向き = 曲線の接線 (dP/dt = e + (0,-4h(1-2t)))
            float tx = ex, ty = ey - 4f * sag * (1f - 2f * t);
            float rot = FxMath.Atan2(ty, tx) * FxMath.Rad2Deg;

            c.LinkTf[i].localPosition = FxMath.V3(x, y, LinkZ(c, i));
            c.LinkTf[i].localRotation = FxMath.RotZ(rot);

            float g = glint > -1f ? FxMath.Exp(-((t - glint) * (t - glint)) / 0.006f) : 0f;
            float k = 0.8f + 0.2f * g;
            sr.color = FxMath.Rgba(k, k, k * 1.02f, 1f);

            // 影は輪 2 つに 1 つ・床 (両者の足元を結んだ線) に落とす。手錠は体の前なので落とさない
            if (!c.Cuffs && (i & 1) == 0)
            {
                int si = i >> 1;
                if (si < c.Shadows.Length)
                {
                    SpriteRenderer sh = c.Shadows[si];
                    if (!sh.enabled) sh.enabled = true;
                    c.ShadowTf[si].localPosition = FxMath.V3(x, feetA + (feetB - feetA) * t, ShadowZ(c));
                }
            }
        }

        // 手錠は影を一度も出さない (Start で全部消してある) ので見に行かない
        if (!c.Cuffs)
            for (int si = (n + 1) >> 1; si < c.Shadows.Length; si++)
                if (c.Shadows[si].enabled) c.Shadows[si].enabled = false;

        c.Shown = n;
    }

    // 鎖を外す。snap = 真ん中から切れて輪が落ちる / false = すぐ消す
    internal static void Stop(int id, bool snap)
    {
        Chain c = Find(id);
        if (c == null) return;

        if (!snap || !c.Root.activeSelf || c.Shown <= 0)
        {
            Release(c);
            return;
        }

        c.Snapping = true;
        c.SnapStart = Time.time;
        int n = c.Shown;

        for (int i = 0; i < n; i++)
        {
            Vector3 p = c.LinkTf[i].localPosition;
            float t = (i + 0.5f) / n;
            float side = t < 0.5f ? -1f : 1f;
            float near = 1f - FxMath.Abs(t - 0.5f) * 2f;   // 真ん中ほど強く弾ける
            c.Pos[i] = FxMath.V2(p.x, p.y);
            c.Vel[i] = FxMath.V2(side * FxMath.Range(0.4f, 1.6f) * (0.4f + near), FxMath.Range(0.5f, 1.8f) * near);
            c.Rot[i] = c.LinkTf[i].localEulerAngles.z;
            c.Spin[i] = FxMath.Range(-540f, 540f);
        }

        for (int si = 0; si < c.Shadows.Length; si++) c.Shadows[si].enabled = false;
    }

    internal static void Tick()
    {
        if (Live.Count == 0) return;

        float now = Time.time, dt = Time.deltaTime;

        for (int k = Live.Count - 1; k >= 0; k--)
        {
            Chain c = Live[k];
            if (!c.Root)
            {
                Live.RemoveAt(k);
                continue;
            }

            if (!c.Snapping) continue;

            float u = (now - c.SnapStart) / SnapTime;
            if (u >= 1f)
            {
                Release(c);
                continue;
            }

            float a = 1f - u * u;
            for (int i = 0; i < c.Shown; i++)
            {
                Vector2 v = c.Vel[i];
                v = FxMath.V2(v.x, v.y - 7f * dt);
                c.Vel[i] = v;
                c.Pos[i] = FxMath.V2(c.Pos[i].x + v.x * dt, c.Pos[i].y + v.y * dt);
                c.Rot[i] += c.Spin[i] * dt;
                c.LinkTf[i].localPosition = FxMath.V3(c.Pos[i].x, c.Pos[i].y, LinkZ(c, i));
                c.LinkTf[i].localRotation = FxMath.RotZ(c.Rot[i]);
                c.Links[i].color = FxMath.Rgba(0.8f, 0.8f, 0.82f, a);
            }

            Color ha = FxMath.Rgba(1f, 1f, 1f, a);
            c.HandA.color = c.HandB.color = c.CuffA.color = c.CuffB.color = ha;

            // 手錠は外れた輪が外へ傾きながら落ちる
            if (c.Cuffs)
            {
                c.RingBackA.color = c.RingBackB.color = ha;
                float el = now - c.SnapStart;
                float fall = 3.5f * el * el, spread = 0.1f * u;
                Vector2 ra = c.RingA, rb = c.RingB;
                float fz = CuffFrontZ(c), bz = CuffBackZ(c);
                c.CuffATf.localPosition = FxMath.V3(ra.x - spread, ra.y - fall, fz);
                c.RingBackATf.localPosition = FxMath.V3(ra.x - spread, ra.y - fall, bz);
                c.CuffBTf.localPosition = FxMath.V3(rb.x + spread, rb.y - fall, fz);
                c.RingBackBTf.localPosition = FxMath.V3(rb.x + spread, rb.y - fall, bz);
                c.CuffATf.localRotation = c.RingBackATf.localRotation = FxMath.RotZ(70f * u);
                c.CuffBTf.localRotation = c.RingBackBTf.localRotation = FxMath.RotZ(-70f * u);
            }
        }
    }

    internal static void ClearAll()
    {
        for (int i = Live.Count - 1; i >= 0; i--)
        {
            Chain c = Live[i];
            if (c.Root)
            {
                c.Root.SetActive(false);
                Pool.Push(c);
            }
        }

        Live.Clear();
    }

    // ── 中身 ───────────────────────────────────────────

    private static void PlaceHand(Transform hand, SpriteRenderer sr, Transform cuff, float x, float y, float ang, float facingX, float size)
    {
        bool left = facingX < 0f;
        hand.localPosition = FxMath.V3(x, y, hand.localPosition.z);
        hand.localRotation = FxMath.RotZ(ang);
        hand.localScale = FxMath.V3(size, left ? -size : size, 1f);

        // 手首の鉄の輪は拳の後ろ (鎖と反対側)。拳を小さくしたら輪も手首へ寄せる
        float r = ang * FxMath.Deg2Rad;
        float back = 0.17f * size / HandSize;
        cuff.localPosition = FxMath.V3(x - FxMath.Cos(r) * back, y - FxMath.Sin(r) * back, cuff.localPosition.z);
        cuff.localRotation = FxMath.RotZ(ang);
    }

    // 拳の大きさを変える (既定 HandSize)。1 人の手首に掛ける手錠のように、体に対して手を小さく見せたい時
    internal static void SetHandSize(int id, float size)
    {
        Chain c = Find(id);
        if (c != null) c.Hand = size;
    }

    // 手錠にする: 拳で鎖を握らず、両端に輪 (正面向き・錠前箱付き) を置いて小さな鎖で輪と輪を繋ぎ、拳は輪の下に垂らす。
    // 以後 Place の a・b は輪の中心。ring / link = 輪と鎖の輪の倍率、drop = 輪から拳の中心まで、tilt = 拳を外へ傾ける角度
    internal static void SetCuffs(int id, float ring, float link, float drop, float tilt)
    {
        Chain c = Find(id);
        if (c == null) return;

        c.Cuffs = true;
        c.RingSize = ring;
        c.LinkSize = link;
        c.HandDrop = drop;
        c.HandTilt = tilt;
        c.CuffA.sprite = c.CuffB.sprite = _ringFront;
        c.RingBackA.enabled = c.RingBackB.enabled = true;
        c.RingBackA.color = c.RingBackB.color = FxMath.Rgba(1f, 1f, 1f, 1f);
        for (int i = 0; i < MaxLinks; i++) c.LinkTf[i].localScale = FxMath.V3(link, link, 1f);

        // 輪の奥半分 → 拳 → 輪の手前半分 の順に手前へ (手首が輪を通って見える)。視界の外で隠す時は z だけで決まる
        if (c.Vision)
        {
            c.RingBackA.sortingLayerID = c.RingBackB.sortingLayerID = c.CuffA.sortingLayerID;
            c.RingBackA.sortingOrder = c.RingBackB.sortingOrder = c.CuffA.sortingOrder;
        }
        else
        {
            int order = c.HandA.sortingOrder - 2;
            c.RingBackA.sortingLayerID = c.RingBackB.sortingLayerID = 0;
            c.RingBackA.sortingOrder = c.RingBackB.sortingOrder = order + 1;
            c.CuffA.sortingOrder = c.CuffB.sortingOrder = order + 3;
        }
    }

    private static void PlaceCuffs(Chain c, Vector2 a, Vector2 b, float ang, float now)
    {
        c.RingA = a;
        c.RingB = b;

        // かけた瞬間に輪が締まる
        float pop = 1f - FxMath.Clamp01((now - c.Start) / 0.15f);
        float s = c.RingSize * (1f + 0.3f * pop * pop);
        Quaternion rot = FxMath.RotZ(ang);
        float fz = CuffFrontZ(c), bz = CuffBackZ(c);

        c.CuffATf.localPosition = FxMath.V3(a.x, a.y, fz);
        c.RingBackATf.localPosition = FxMath.V3(a.x, a.y, bz);
        c.CuffBTf.localPosition = FxMath.V3(b.x, b.y, fz);
        c.RingBackBTf.localPosition = FxMath.V3(b.x, b.y, bz);
        c.CuffATf.localRotation = c.RingBackATf.localRotation = rot;
        c.CuffBTf.localRotation = c.RingBackBTf.localRotation = rot;
        c.CuffATf.localScale = c.RingBackATf.localScale = FxMath.V3(s, s, 1f);
        c.CuffBTf.localScale = c.RingBackBTf.localScale = FxMath.V3(-s, s, 1f);

        // 拳は手首を上にして輪から垂らし、少し外へ傾ける (左右は鏡写し)
        float hz = c.Vision ? c.Z - 0.002f : 0f;
        float ha = (-90f - c.HandTilt) * FxMath.Deg2Rad, hb = (-90f + c.HandTilt) * FxMath.Deg2Rad;
        c.HandATf.localPosition = FxMath.V3(a.x + FxMath.Cos(ha) * c.HandDrop, a.y + FxMath.Sin(ha) * c.HandDrop, hz);
        c.HandBTf.localPosition = FxMath.V3(b.x + FxMath.Cos(hb) * c.HandDrop, b.y + FxMath.Sin(hb) * c.HandDrop, hz);
        c.HandATf.localRotation = FxMath.RotZ(-90f - c.HandTilt);
        c.HandBTf.localRotation = FxMath.RotZ(-90f + c.HandTilt);
        c.HandATf.localScale = FxMath.V3(c.Hand, c.Hand, 1f);
        c.HandBTf.localScale = FxMath.V3(c.Hand, -c.Hand, 1f);
    }

    private static float LinkZ(Chain c, int i) => c.Vision ? c.Z + (i & 1) * 0.0002f : 0f;
    private static float ShadowZ(Chain c) => c.Vision ? c.Z + 0.002f : 0f;
    private static float CuffFrontZ(Chain c) => c.Vision ? c.Z - 0.003f : 0f;
    private static float CuffBackZ(Chain c) => c.Vision ? c.Z - 0.001f : 0f;

    // 視界の外で隠す時は全部が影の板と同じ描画順になり、前後は z だけで決まる。影 → 輪 → 手首の輪 → 手 の順に手前へ
    private static void Sort(Chain c, bool vision, int order, float z)
    {
        MeshRenderer shadow = vision && HudManager.InstanceExists ? HudManager.Instance.ShadowQuad : null;
        c.Z = z;

        if (shadow)
        {
            int layer = shadow.sortingLayerID, so = shadow.sortingOrder;
            foreach (SpriteRenderer sr in c.Links) { sr.sortingLayerID = layer; sr.sortingOrder = so; }
            foreach (SpriteRenderer sr in c.Shadows) { sr.sortingLayerID = layer; sr.sortingOrder = so; }
            c.HandA.sortingLayerID = c.HandB.sortingLayerID = c.CuffA.sortingLayerID = c.CuffB.sortingLayerID = layer;
            c.HandA.sortingOrder = c.HandB.sortingOrder = c.CuffA.sortingOrder = c.CuffB.sortingOrder = so;
            c.HandATf.localPosition = FxMath.V3(0f, 0f, z - 0.002f);
            c.HandBTf.localPosition = FxMath.V3(0f, 0f, z - 0.002f);
            c.CuffATf.localPosition = FxMath.V3(0f, 0f, z - 0.001f);
            c.CuffBTf.localPosition = FxMath.V3(0f, 0f, z - 0.001f);
        }
        else
        {
            foreach (SpriteRenderer sr in c.Links) { sr.sortingLayerID = 0; sr.sortingOrder = order; }
            foreach (SpriteRenderer sr in c.Shadows) { sr.sortingLayerID = 0; sr.sortingOrder = order - 1; }
            c.HandA.sortingLayerID = c.HandB.sortingLayerID = c.CuffA.sortingLayerID = c.CuffB.sortingLayerID = 0;
            c.CuffA.sortingOrder = c.CuffB.sortingOrder = order + 1;
            c.HandA.sortingOrder = c.HandB.sortingOrder = order + 2;
            c.HandATf.localPosition = c.HandBTf.localPosition = c.CuffATf.localPosition = c.CuffBTf.localPosition = FxMath.V3(0f, 0f, 0f);
        }
    }

    private static Chain Find(int id)
    {
        if (id <= 0) return null;
        for (int i = 0; i < Live.Count; i++)
            if (Live[i].Id == id) return Live[i];
        return null;
    }

    private static void Release(Chain c)
    {
        Live.Remove(c);
        c.Snapping = false;
        c.Shown = 0;
        if (!c.Root) return;

        c.Root.SetActive(false);
        Pool.Push(c);
    }

    private static int ClampColor(int id) => id < 0 || id >= Palette.PlayerColors.Length ? 0 : id;

    private static Chain Create()
    {
        var root = new GameObject("FxChain") { layer = 0 };
        Object.DontDestroyOnLoad(root);
        root.SetActive(false);

        var c = new Chain
        {
            Root = root, Links = new SpriteRenderer[MaxLinks], LinkTf = new Transform[MaxLinks], Shadows = new SpriteRenderer[MaxLinks / 2], ShadowTf = new Transform[MaxLinks / 2],
            Pos = new Vector2[MaxLinks], Vel = new Vector2[MaxLinks], Rot = new float[MaxLinks], Spin = new float[MaxLinks]
        };

        for (int i = 0; i < MaxLinks; i++)
        {
            SpriteRenderer sr = Child(root, "Link", (i & 1) == 0 ? _face : _edge);
            c.Links[i] = sr;
            c.LinkTf[i] = sr.transform;
        }

        for (int i = 0; i < c.Shadows.Length; i++)
        {
            SpriteRenderer sr = Child(root, "Shadow", _shadow);
            sr.color = FxMath.Rgba(1f, 1f, 1f, 0.25f);
            c.Shadows[i] = sr;
            c.ShadowTf[i] = sr.transform;
        }

        c.RingBackA = Child(root, "RingBackA", _ringBack);
        c.RingBackB = Child(root, "RingBackB", _ringBack);
        c.RingBackATf = c.RingBackA.transform;
        c.RingBackBTf = c.RingBackB.transform;
        c.CuffA = Child(root, "CuffA", _cuff);
        c.CuffB = Child(root, "CuffB", _cuff);
        c.HandA = Child(root, "HandA", _grip);
        c.HandB = Child(root, "HandB", _grip);
        c.HandATf = c.HandA.transform;
        c.HandBTf = c.HandB.transform;
        c.CuffATf = c.CuffA.transform;
        c.CuffBTf = c.CuffB.transform;
        return c;
    }

    private static SpriteRenderer Child(GameObject root, string name, Sprite sprite)
    {
        var go = new GameObject(name) { layer = 0 };
        go.transform.SetParent(root.transform, false);
        SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        return sr;
    }

    private static bool Prepare()
    {
        // シーンの切り替えで絵が破棄されていたら読み直す
        if (_prepared && _face && _edge && _cuff && _shadow && _grip && _ringBack && _ringFront) return true;
        if (_prepareFailed) return false;

        try
        {
            // 輪と手錠は 320 画素で 1u (正面の輪 = 0.2u)・影は 200 画素で 1u・手は自作の手と同じ 100 画素で 1u
            _face = Utils.LoadSprite("EndKnot.Resources.Images.Fx.ChainLinkFace.png", 320f);
            _edge = Utils.LoadSprite("EndKnot.Resources.Images.Fx.ChainLinkEdge.png", 320f);
            _cuff = Utils.LoadSprite("EndKnot.Resources.Images.Fx.ChainCuff.png", 320f);
            _shadow = Utils.LoadSprite("EndKnot.Resources.Images.Fx.ChainShadow.png", 200f);
            _grip = FxHands.GripSprite();
            _ringBack = Utils.LoadSprite("EndKnot.Resources.Images.Fx.CuffRingBack.png", 320f);
            _ringFront = Utils.LoadSprite("EndKnot.Resources.Images.Fx.CuffRingFront.png", 320f);

            if (!_face || !_edge || !_cuff || !_shadow || !_grip || !_ringBack || !_ringFront || !FxHands.PlayerMat())
            {
                _prepareFailed = true;
                return false;
            }
        }
        catch (System.Exception e)
        {
            _prepareFailed = true;
            Utils.ThrowException(e);
            return false;
        }

        _prepared = true;
        return true;
    }
}
