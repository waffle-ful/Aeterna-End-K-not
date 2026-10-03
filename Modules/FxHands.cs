using System.Collections.Generic;
using UnityEngine;

namespace EndKnot.Modules;

// 手の部品: 本編の手 (プレイヤー色のひな形: 赤=本体色・青=影色) を本人の色で塗って出し、コマ送りで動かす。
// 本編に無いポーズ (指パッチン) は同じ様式で自作した絵 (Resources/Images/Fx/SnapHand_*.png)。
// 本編の絵はゲームが読み込んでいる物を名前で探して借りる (配布物には入れない)。見つからない絵のコマは飛ばす。
// 描くのは手元の画面だけ。メインスレッド専用。
internal static class FxHands
{
    internal enum Pose : byte
    {
        None,
        SnapReady,
        SnapFlick,
        SnapAfter,
        Open,     // 本編: 開いた手 (タスク画面)
        Grab,     // 本編: 握った手 (タスク画面)
        GunGrip,  // 本編: 銃を握った手 (銃キル)
        Gun       // 本編: 手に握られた銃 (銃キル・プレイヤー色ではない)
    }

    // t 秒の時点の姿。間は直線で補間し、絵 (Pose) はその時点までで最後のキーの物
    internal struct Key
    {
        public float T;
        public Pose Pose;
        public float Dx, Dy, Rot, Scale, Alpha, Shake;

        public Key(float t, Pose pose, float scale = 1f, float alpha = 1f, float dx = 0f, float dy = 0f, float rot = 0f, float shake = 0f)
        {
            T = t;
            Pose = pose;
            Scale = scale;
            Alpha = alpha;
            Dx = dx;
            Dy = dy;
            Rot = rot;
            Shake = shake;
        }
    }

    private sealed class Hand
    {
        public GameObject Go;
        public Transform Tf;
        public SpriteRenderer Sr;
        public SpriteRenderer Prop;  // 手に持たせる物 (銃)。同じ原点に重ねる
        public Key[] Keys;
        public float Start;
        public Vector2 Pos;
        public float Size;
        public bool Flip;
        public Pose Shown;
        public Pose PropPose;
    }

    private const int MaxLive = 16;
    private const float SelfPpu = 100f;

    private static readonly List<Hand> Live = [];
    private static readonly Stack<Hand> Pool = [];
    private static readonly Dictionary<string, Sprite> Vanilla = [];
    private static Material _playerMaterial;
    private static int _scans;
    private const int MaxScans = 3;
    private const float RescanGap = 20f;
    private static float _lastScan;

    // keys の通りに手を動かす。pos はワールド座標、size は絵の倍率 (自作の絵は 1 = 1.4u 幅)、flip で左右反転。
    // prop を渡すと同じ原点に重ねて持たせる (プレイヤー色に塗らない)。vision / order / z は ExplosionFx の置き方に合わせる
    internal static void Play(Key[] keys, Vector2 pos, int colorId, float size, bool flip, float delay, bool vision, int order, float z, Pose prop = Pose.None)
    {
        if (keys == null || keys.Length == 0 || Live.Count >= MaxLive) return;
        if (colorId < 0 || colorId >= Palette.PlayerColors.Length) colorId = 0;

        Hand h = null;
        try
        {
            Material mat = PlayerMat();
            if (!mat) return;

            h = Pool.Count > 0 ? Pool.Pop() : null;
            if (h == null || !h.Go)
            {
                var go = new GameObject("FxHand") { layer = 0 };
                Object.DontDestroyOnLoad(go);
                h = new Hand { Go = go, Tf = go.transform, Sr = go.AddComponent<SpriteRenderer>() };
                h.Sr.sharedMaterial = mat;
                var pgo = new GameObject("FxHandProp") { layer = 0 };
                pgo.transform.SetParent(go.transform, false);
                h.Prop = pgo.AddComponent<SpriteRenderer>();
            }

            PlayerMaterial.SetColors(colorId, h.Sr);
            h.Keys = keys;
            h.Start = Time.time + delay;
            h.Pos = pos;
            h.Size = size;
            h.Flip = flip;
            h.Shown = Pose.None;
            h.PropPose = prop;
            h.Sr.sprite = null;
            h.Sr.flipX = flip;
            h.Prop.sprite = prop != Pose.None ? SpriteOf(prop) : null;
            h.Prop.flipX = flip;

            MeshRenderer shadow = vision && HudManager.InstanceExists ? HudManager.Instance.ShadowQuad : null;
            if (shadow)
            {
                h.Sr.sortingLayerID = h.Prop.sortingLayerID = shadow.sortingLayerID;
                h.Sr.sortingOrder = shadow.sortingOrder;
                h.Prop.sortingOrder = shadow.sortingOrder;
            }
            else
            {
                h.Sr.sortingLayerID = h.Prop.sortingLayerID = 0;
                h.Sr.sortingOrder = order;
                h.Prop.sortingOrder = order - 1;  // 銃は握る手の奥
            }

            h.Tf.position = new Vector3(pos.x, pos.y, z);
            // 影の板の層では順序が同じなので z で前後を付ける (銃を手の奥へ)
            h.Prop.transform.localPosition = new Vector3(0f, 0f, shadow ? 0.001f : 0f);
            h.Tf.localScale = Vector3.zero;
            h.Go.SetActive(true);
            Live.Add(h);
            h = null;
        }
        catch (System.Exception e)
        {
            Utils.ThrowException(e);
            // 準備の途中で落ちた手は Live にも Pool にも居ない → 隠して Pool へ戻す (残すと画面に居座る)
            if (h != null && h.Go)
            {
                h.Go.SetActive(false);
                Pool.Push(h);
            }
        }
    }

    internal static void Tick()
    {
        if (Live.Count == 0) return;

        float now = Time.time;
        for (int i = Live.Count - 1; i >= 0; i--)
        {
            Hand h = Live[i];
            if (!h.Go)
            {
                Live.RemoveAt(i);
                continue;
            }

            float t = now - h.Start;
            if (t < 0f) continue;

            Key[] k = h.Keys;
            if (t >= k[^1].T)
            {
                Release(h);
                Live.RemoveAt(i);
                continue;
            }

            int j = 0;
            while (j + 1 < k.Length && k[j + 1].T <= t) j++;
            Key a = k[j];
            Key b = j + 1 < k.Length ? k[j + 1] : a;
            float u = b.T > a.T ? (t - a.T) / (b.T - a.T) : 1f;

            if (a.Pose != h.Shown)
            {
                h.Shown = a.Pose;
                Sprite s = SpriteOf(a.Pose);
                if (s) h.Sr.sprite = s;  // 見つからない絵のコマは前の絵のまま
            }

            float sx = h.Flip ? -1f : 1f;
            float shake = FxMath.Lerp(a.Shake, b.Shake, u);
            float jx = shake > 0f ? FxMath.Sin(t * 173f) * shake : 0f;
            float jy = shake > 0f ? FxMath.Sin(t * 131f + 1.3f) * shake : 0f;
            float sc = FxMath.Lerp(a.Scale, b.Scale, u) * h.Size;
            float rot = sx * FxMath.Lerp(a.Rot, b.Rot, u);
            // 絵の原点が手から離れている物 (銃キルの絵) は、手の中心が Pos に来るようにずらす
            Vector2 an = Anchor(h.Shown);
            float ax = sx * an.x * sc, ay = an.y * sc;
            float rr = rot * FxMath.Deg2Rad, cs = FxMath.Cos(rr), sn = FxMath.Sin(rr);
            Vector3 p = h.Tf.position;
            h.Tf.position = new Vector3(h.Pos.x + sx * FxMath.Lerp(a.Dx, b.Dx, u) + jx - (ax * cs - ay * sn), h.Pos.y + FxMath.Lerp(a.Dy, b.Dy, u) + jy - (ax * sn + ay * cs), p.z);
            h.Tf.rotation = FxMath.RotZ(rot);
            h.Tf.localScale = new Vector3(sc, sc, 1f);
            float al = FxMath.Clamp01(FxMath.Lerp(a.Alpha, b.Alpha, u));
            h.Sr.color = new Color(1f, 1f, 1f, al);
            h.Prop.color = new Color(1f, 1f, 1f, al);
        }
    }

    internal static void ClearAll()
    {
        foreach (Hand h in Live) Release(h);
        Live.Clear();
    }

    private static void Release(Hand h)
    {
        if (!h.Go) return;

        h.Go.SetActive(false);
        if (Pool.Count < MaxLive) Pool.Push(h);
        else Object.Destroy(h.Go);
    }

    private static Sprite SpriteOf(Pose pose) => pose switch
    {
        Pose.SnapReady => Utils.LoadSprite("EndKnot.Resources.Images.Fx.SnapHand_0.png", SelfPpu),
        Pose.SnapFlick => Utils.LoadSprite("EndKnot.Resources.Images.Fx.SnapHand_1.png", SelfPpu),
        Pose.SnapAfter => Utils.LoadSprite("EndKnot.Resources.Images.Fx.SnapHand_2.png", SelfPpu),
        Pose.Open => VanillaSprite("hand_open"),
        Pose.Grab => VanillaSprite("hand_grab"),
        Pose.GunGrip => VanillaSprite("killGun_hand0017"),
        Pose.Gun => VanillaSprite("killGun_gun0017"),
        _ => null
    };

    // 絵の原点から見た手の中心 (絵の単位)。銃キルの手と銃は同じ原点を共有していて、手は原点の左上に描かれている
    private static Vector2 Anchor(Pose pose) => pose is Pose.GunGrip ? new Vector2(-1.48f, 0.47f) : Vector2.zero;

    private static readonly string[] Wanted = ["hand_open", "hand_grab", "killGun_hand0017", "killGun_gun0017"];

    // 本編の絵を名前で探す。全部の絵をなめるのは重いので、試合ごとに 1 回だけ (マップの絵は試合ごとに読み直される)
    private static Sprite VanillaSprite(string name)
    {
        if (Vanilla.TryGetValue(name, out Sprite s) && s) return s;
        // 欠けていたら試合ごとに MaxScans 回まで、RescanGap 秒あけて探し直す (後から読み込まれる絵がある・走査は 1 回 15〜70ms)
        if (_scans >= MaxScans || (_scans > 0 && Time.time - _lastScan < RescanGap)) return null;

        _scans++;
        _lastScan = Time.time;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int n = 0;
        foreach (Object o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<Sprite>()))
        {
            n++;
            Sprite sp = o != null ? o.TryCast<Sprite>() : null;
            if (!sp) continue;

            string nm = sp.name;
            if (System.Array.IndexOf(Wanted, nm) >= 0) Vanilla[nm] = sp;
        }

        Logger.Info($"vanilla hand sprites: found={Vanilla.Count}/{Wanted.Length} scanned={n} in {sw.ElapsedMilliseconds}ms", "FxHands");
        return Vanilla.TryGetValue(name, out s) && s ? s : null;
    }

    // 試合が始まる度に呼ぶ (マップの絵は読み直されるので探し直しを許す)
    internal static void ResetScan()
    {
        _scans = 0;
        Vanilla.Clear();
    }

    private static Material PlayerMat()
    {
        if (_playerMaterial) return _playerMaterial;
        if (!GameManager.Instance || GameManager.Instance.deadBodyPrefab == null || GameManager.Instance.deadBodyPrefab.Length == 0) return null;

        DeadBody prefab = GameManager.Instance.deadBodyPrefab[0];
        SpriteRenderer src = prefab.bodyRenderers != null && prefab.bodyRenderers.Length > 0 ? prefab.bodyRenderers[0] : null;
        return _playerMaterial = src ? src.sharedMaterial : null;
    }

    // 今の状態 (確認用)
    internal static string Describe()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"live={Live.Count} pool={Pool.Count} mat={(_playerMaterial ? _playerMaterial.shader.name : "null")} vanilla=");
        foreach (string w in Wanted) sb.Append(w).Append(Vanilla.TryGetValue(w, out Sprite s) && s ? "+ " : "- ");
        return sb.ToString();
    }
}
