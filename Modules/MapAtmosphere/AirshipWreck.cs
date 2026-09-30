using System.Collections.Generic;
using UnityEngine;

namespace EndKnot.Modules.MapAtmosphere;

// 朽ちた船で倒れている家具 (AirshipRuin が組み立て・毎フレーム更新・片付けを呼ぶ)。
// 汚れと同じく「見ていない所だけ壊れている」: 影の中 (layer 10) には倒れた姿、視界の中 (layer 0) には元の姿を描く。
// バニラの家具は描画だけ止めて、同じ絵の写しを二組置く。当たり判定は元の物にそのまま残るので、歩ける所は変わらない。
// 破損で朽ちた船が視界の中まで入り込む間は、視界の中の写しも倒れた姿へ入れ替える。
// 上に作業台や別の物が載っている家具 (会議机・台所の調理台・記録室の机・武器庫の銃の台) は、載っている物が宙に浮くので倒さない。
internal static class AirshipWreck
{
    private const int ShadowOnlyLayer = 10;

    // 部屋の名前 / 部屋の直下にある家具の名前。同じ名前が複数あれば全部倒す (ラウンジに積まれた丸椅子は床に散らばる)。
    private static readonly (string Room, string Name, bool Scatter)[] Targets =
    [
        ("Records", "records_chair_left", false),
        ("Records", "records_chair_right", false),
        ("Lounge", "lounge_stool", true),
        ("Lounge", "Lounge_stooltop", true),
        ("Medbay", "medbay_table", false),
        ("Cockpit", "cockpit_chair", false),
        ("Vault", "vault_gun", false),
        ("Vault", "vault_sword", false),
        ("Kitchen", "kitchen_shelf", false),
        ("HallwayMain", "halls_spraybottle", false)
    ];

    private sealed class Piece
    {
        public SpriteRenderer Orig, UpSh, UpLi, DownSh, DownLi;
        public float Threshold;
        public Color Tint;
    }

    private static Transform _root;
    private static Piece[] _pieces = [];
    private static float _shownFade = -1f, _shownDecay = -1f, _shownLit = -1f;

    public static void Build(ShipStatus ship, Transform root)
    {
        _root = root;
        var pieces = new List<Piece>();

        foreach (PlainShipRoom room in ship.AllRooms)
        {
            if (!room) continue;
            Transform rt = room.transform;
            for (int c = 0; c < rt.childCount; c++)
            {
                Transform child = rt.GetChild(c);
                SpriteRenderer sr = child.GetComponent<SpriteRenderer>();
                if (!sr || !sr.enabled || !sr.sprite || !child.gameObject.activeInHierarchy) continue;
                if (!IsTarget(room.name, child.name, out bool scatter)) continue;
                pieces.Add(MakePiece(sr, scatter));
            }
        }

        _pieces = pieces.ToArray();
        _shownFade = _shownDecay = _shownLit = -1f;
        int stuck = 0;
        foreach (Piece p in _pieces)
            if (p.Threshold > 1f) stuck++;
        Logger.Info($"AirshipWreck pieces={_pieces.Length} noRoom={stuck}", "AirshipLiminal");
        foreach (Piece p in _pieces)
            if (p.Threshold > 1f) Logger.Info($"AirshipWreck no room to fall: {p.Orig.transform.parent.name}/{p.Orig.name}", "AirshipLiminal");
    }

    private static bool IsTarget(string room, string name, out bool scatter)
    {
        foreach ((string r, string n, bool s) in Targets)
        {
            if (r != room || n != name) continue;
            scatter = s;
            return true;
        }

        scatter = false;
        return false;
    }

    private static Piece MakePiece(SpriteRenderer orig, bool scatter)
    {
        Transform ot = orig.transform;
        Vector3 pos = ot.position;
        Quaternion rot = ot.rotation;
        Vector3 scale = ot.lossyScale;

        // 足元 (絵の下端の真ん中) を軸に横倒しにする。倒れる向きは左右どちらか。
        // 倒れた姿が壁にめり込まないよう、絵の四隅と真ん中が床 (か元の家具が立っていた所) に収まる姿勢だけを採る。
        // 何度引いても収まらなければ倒さない。四隅は少し内側で見る (床の格子は壁際が削れている)。
        Bounds b = orig.bounds;
        Bounds local = orig.sprite.bounds;
        float hx = local.extents.x * FxMath.Abs(scale.x), hy = local.extents.y * FxMath.Abs(scale.y);
        float ox = (local.center.x * scale.x), oy = (local.center.y * scale.y); // 絵の中心の、transform からのずれ
        Vector3 downPos = pos;
        Quaternion downRot = rot;
        bool placed = false;
        for (int attempt = 0; attempt < 24 && !placed; attempt++)
        {
            float ang = (FxMath.Value < 0.5f ? -1f : 1f) * FxMath.Range(78f, 100f);
            // 床の格子は壁際が少し削れているので、軸も少しずらして探す。
            float px = b.center.x + FxMath.Range(-0.15f, 0.15f), py = b.min.y + FxMath.Range(-0.1f, 0.1f);
            if (scatter)
            {
                // 積まれていた物は、ばらばらの向きで少し離れた所に転がる。
                ang = FxMath.Range(0f, 360f);
                px += FxMath.Range(-0.45f, 0.45f);
                py += FxMath.Range(-0.35f, 0.1f);
            }

            float rad = ang * FxMath.Deg2Rad, cos = FxMath.Cos(rad), sin = FxMath.Sin(rad);
            float dx = pos.x - px, dy = pos.y - py;
            float nx = px + dx * cos - dy * sin, ny = py + dx * sin + dy * cos;
            float cx = nx + ox * cos - oy * sin, cy = ny + ox * sin + oy * cos;
            if (!Fits(b, cx, cy, hx * 0.6f, hy * 0.6f, cos, sin)) continue;
            downPos = FxMath.V3(nx, ny, pos.z);
            downRot = FxMath.RotZ(ang);
            placed = true;
        }

        var piece = new Piece
        {
            Orig = orig,
            Tint = orig.color,
            // 半分は最初から倒れていて、残りは試合が長引くにつれて倒れていく。倒れる場所が無ければ倒れない。
            Threshold = !placed ? 2f : FxMath.Value < 0.5f ? 0f : FxMath.Range(0.15f, 0.85f)
        };
        piece.UpSh = Copy(orig, ShadowOnlyLayer, pos, rot, scale);
        piece.UpLi = Copy(orig, 0, pos, rot, scale);
        piece.DownSh = Copy(orig, ShadowOnlyLayer, downPos, downRot, scale);
        piece.DownLi = Copy(orig, 0, downPos, downRot, scale);
        orig.enabled = false;
        Show(piece, 0f, 0f);
        return piece;
    }

    // 中心 (cx, cy)・半幅 hx・半高 hy・向き (cos, sin) の四角が、床か元の家具の場所 (b) に収まっているか (影に写らない小物の下は床としない)。
    internal static bool Fits(Bounds b, float cx, float cy, float hx, float hy, float cos, float sin)
    {
        for (int i = 0; i < 5; i++)
        {
            float lx = i == 4 ? 0f : (i & 1) == 0 ? -hx : hx;
            float ly = i == 4 ? 0f : (i & 2) == 0 ? -hy : hy;
            float x = cx + lx * cos - ly * sin, y = cy + lx * sin + ly * cos;
            bool onOrig = x >= b.min.x && x <= b.max.x && y >= b.min.y && y <= b.max.y;
            if (!onOrig && (!AirshipRuinLayout.IsFloorAt(x, y) || AirshipRuinLayout.UnderLowProp(x, y))) return false;
        }

        return true;
    }

    private static SpriteRenderer Copy(SpriteRenderer orig, int layer, Vector3 pos, Quaternion rot, Vector3 scale)
    {
        var go = new GameObject(orig.name + (layer == 0 ? "Lit" : "Shadow")) { layer = layer };
        Transform tf = go.transform;
        tf.SetParent(_root, false);
        tf.position = pos;
        tf.rotation = rot;
        tf.localScale = scale;
        SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = orig.sprite;
        sr.sharedMaterial = orig.sharedMaterial;
        sr.flipX = orig.flipX;
        sr.flipY = orig.flipY;
        sr.sortingLayerID = orig.sortingLayerID;
        sr.sortingOrder = orig.sortingOrder;
        sr.color = orig.color;
        return sr;
    }

    // fadeIn: 組み立て直後のフェード (0→1)。invade: 視界の中への侵食 (0→1)。decay: 試合の長さ (0→1)。
    public static void Animate(float fadeIn, float invade, float decay)
    {
        if (!_root || fadeIn == _shownFade && decay == _shownDecay && invade == _shownLit) return;
        _shownFade = fadeIn;
        _shownDecay = decay;
        _shownLit = invade;
        foreach (Piece p in _pieces)
        {
            float grow = p.Threshold <= 0f ? 1f : FxMath.Clamp01((decay - p.Threshold) / 0.1f);
            Show(p, fadeIn * grow, invade);
        }
    }

    // down: 影の中で倒れている度合い (0→1)。lit: 視界の中でも倒れている度合い (0→1)。
    private static void Show(Piece p, float down, float lit)
    {
        Color c = p.Tint;
        p.UpSh.color = FxMath.Rgba(c.r, c.g, c.b, c.a * (1f - down));
        p.DownSh.color = FxMath.Rgba(c.r * 0.85f, c.g * 0.85f, c.b * 0.85f, c.a * down);
        float litDown = down * lit;
        p.UpLi.color = FxMath.Rgba(c.r, c.g, c.b, c.a * (1f - litDown));
        p.DownLi.color = FxMath.Rgba(c.r * 0.85f, c.g * 0.85f, c.b * 0.85f, c.a * litDown);
    }

    public static void Teardown()
    {
        // 写しは AirshipRuin のルートと一緒に壊れる。バニラの家具は描画を戻す (船ごと壊れた時は残っていない)。
        foreach (Piece p in _pieces)
            if (p.Orig) p.Orig.enabled = true;
        _pieces = [];
        _root = null;
    }
}
