using System.Collections.Generic;
using UnityEngine;

namespace EndKnot.Modules.MapAtmosphere;

// 朽ちた船で荒らされた棚と机の周り (AirshipRuin が組み立て・毎フレーム更新・片付けを呼ぶ)。
// 倒れた家具 (AirshipWreck) と同じく「見ていない所だけ荒れている」: 影の中 (layer 10) に荒れた姿、視界の中 (layer 0) は破損の間だけ荒れる。
// 記録室の棚はバインダーが抜けた姿を上に重ね、抜けたバインダーや紙を床に散らす。机の周りには書類・割れた皿・倒れたカップを散らす。
// タスクになっている物 (棚・机の書類・テープ) は元の絵を消さない (黄色い縁取りは元の絵に付く)。同じ絵の写しを床に散らすだけ。
// 金庫室のマネキンの帽子や上着は、元の絵を隠して床に落とす。
// 散らばった物は床の上だけに置き、壁にはめり込ませない。当たり判定は何も足さないので、歩ける所は変わらない。
internal static class AirshipRansack
{
    private const string Res = "EndKnot.Resources.Images.MapAtmosphere.";
    private const int ShadowOnlyLayer = 10;

    // 床に散らす物の横幅 (ワールド単位)
    private const float BinderW = 0.22f, BinderOpenW = 0.42f, PageW = 0.26f, ShardW = 0.8f, CupW = 0.45f;
    private const float SewageW = 1.1f, PaperRollW = 0.7f, PorcelainW = 0.45f;

    // 汚れを上に重ねる家具 (部屋の名前 / 絵の名前)。汚れの絵は元の絵と同じ大きさで、元の絵の形の内側だけに描いてある。
    private static readonly (string Room, string Sprite)[] Grimy =
    [
        ("Lounge", "lounge_pooltable1"),
        ("Lounge", "lounge_pooltable2"),
        ("MeetingRoom", "meeting_table"),
        ("Kitchen", "kitchen_table1"),
        ("Kitchen", "kitchen_table2"),
        ("Records", "records_table"),
        ("Medbay", "medbay_table"),
        ("Showers", "shower_bench1"),
        ("Showers", "shower_bench2"),
        ("Showers", "showers_bench3"),
        ("Storage", "storage_cargo1"),
        ("Storage", "storage_cargo1top"),
        ("Vault", "vault_goldtop"),
        ("Armory", "armory_table")
    ];

    private sealed class Bit
    {
        public SpriteRenderer Sh, Li;
        public Color Tint;
        public bool Up; // 荒れる前の姿 (荒れると消える)
    }

    private sealed class Group
    {
        public SpriteRenderer Orig; // 隠したバニラの絵 (無ければ足すだけ)
        public readonly List<Bit> Bits = [];
        public float Threshold;
    }

    private static Transform _root;
    private static Group[] _groups = [];
    private static int _order;
    private static float _shownFade = -1f, _shownDecay = -1f, _shownLit = -1f;

    public static void Build(ShipStatus ship, Transform root)
    {
        _root = root;
        _order = 0;
        _groups = [];
        var groups = new List<Group>();
        int shelves = 0, scattered = 0, grimed = 0;

        // 途中で失敗しても、それまでに隠したバニラの絵は Teardown で戻せるように必ず残す。
        try
        {
            foreach (PlainShipRoom room in ship.AllRooms)
            {
                if (!room) continue;
                foreach (SpriteRenderer sr in room.GetComponentsInChildren<SpriteRenderer>(false))
                {
                    if (!sr || !sr.enabled || !sr.sprite) continue;
                    string sprite = sr.sprite.name;
                    Group g = null;
                    switch (room.name, sprite)
                    {
                        case ("Records", "task_records3"):
                        case ("Records", "task_records4"):
                            g = NewGroup(null);
                            if (Overlay(g, sr, sprite == "task_records3" ? "ruin_shelf3_" : "ruin_shelf4_")) shelves++;
                            for (int i = FxMath.Range(5, 9); i > 0; i--) scattered += Scatter(g, sr.bounds, RandomPaper(), spread: 0.5f);
                            break;
                        case ("Records", "records_table"):
                            g = NewGroup(null);
                            for (int i = FxMath.Range(3, 5); i > 0; i--) scattered += Scatter(g, sr.bounds, (Load($"ruin_page{FxMath.Range(0, 3)}.png"), PageW, White));
                            break;
                        case ("Records", "task_recordsmain"):
                            g = NewGroup(null);
                            for (int i = FxMath.Range(1, 3); i > 0; i--) scattered += Scatter(g, sr.bounds, CopyOf(sr), sr);
                            break;
                        case ("Security", "security_tapes3"):
                        case ("Security", "security_tapes4"):
                            g = NewGroup(null);
                            for (int i = FxMath.Range(1, 3); i > 0; i--) scattered += Scatter(g, sr.bounds, CopyOf(sr), sr);
                            break;
                        case ("MeetingRoom", "meeting_table"):
                            g = NewGroup(null);
                            for (int i = FxMath.Range(2, 4); i > 0; i--) scattered += Scatter(g, sr.bounds, (Load("ruin_cup.png"), CupW, White));
                            scattered += Scatter(g, sr.bounds, (Load($"ruin_shard{FxMath.Range(0, 2)}.png"), ShardW, White));
                            for (int i = FxMath.Range(1, 3); i > 0; i--) scattered += Scatter(g, sr.bounds, (Load($"ruin_page{FxMath.Range(0, 3)}.png"), PageW, White));
                            break;
                        case ("Kitchen", "kitchen_table1"):
                        case ("Kitchen", "kitchen_table2"):
                            g = NewGroup(null);
                            for (int i = FxMath.Range(2, 4); i > 0; i--) scattered += Scatter(g, sr.bounds, (Load($"ruin_shard{FxMath.Range(0, 2)}.png"), ShardW, White));
                            scattered += Scatter(g, sr.bounds, (Load("ruin_cup.png"), CupW, White));
                            break;
                        case ("Lounge", "task_toilet"):
                            // 便器は汚れで覆い、足元に汚水をあふれさせ、ほどけたトイレットペーパーと割れた陶器を散らす。
                            g = NewGroup(null);
                            if (Overlay(g, sr, "ruin_toilet_")) grimed++;
                            for (int i = FxMath.Range(1, 3); i > 0; i--) scattered += Scatter(g, sr.bounds, (Load($"ruin_sewage{FxMath.Range(0, 2)}.png"), SewageW, White), spread: 0.35f);
                            for (int i = FxMath.Range(1, 3); i > 0; i--) scattered += Scatter(g, sr.bounds, (Load($"ruin_tp{FxMath.Range(0, 2)}.png"), PaperRollW, White), spread: 0.6f);
                            for (int i = FxMath.Range(0, 3); i > 0; i--) scattered += Scatter(g, sr.bounds, (Load($"ruin_porcelain{FxMath.Range(0, 2)}.png"), PorcelainW, White), spread: 0.5f);
                            break;
                        case ("Vault", "vault_shako"):
                        case ("Vault", "vault_goldhat"):
                        case ("Vault", "vault_purplejacket"):
                            // 帽子や上着はマネキンから落ちて、足元に転がる。
                            Transform stand = sr.transform.parent;
                            SpriteRenderer body = stand ? stand.GetComponent<SpriteRenderer>() : null;
                            g = NewGroup(sr);
                            groups.Add(g);
                            g.Bits.Add(MakeBit(sr.sprite, sr, sr.transform.position, sr.transform.rotation, sr.transform.lossyScale, sr.color, true));
                            if (Scatter(g, body ? body.bounds : sr.bounds, CopyOf(sr), sr) == 0) Restore(g);
                            else scattered++;
                            break;
                    }

                    if (IsGrimy(room.name, sprite))
                    {
                        g ??= NewGroup(null);
                        if (Overlay(g, sr, $"ruin_grime_{sprite}_")) grimed++;
                    }

                    if (g != null && g.Bits.Count > 0 && !groups.Contains(g)) groups.Add(g);
                }
            }
        }
        finally
        {
            groups.RemoveAll(g => g.Bits.Count == 0 && !g.Orig);
            _groups = groups.ToArray();
        }

        _shownFade = _shownDecay = _shownLit = -1f;
        foreach (Group g in _groups) Show(g, 0f, 0f);
        Logger.Info($"AirshipRansack groups={_groups.Length} shelves={shelves} grimed={grimed} scattered={scattered}", "AirshipLiminal");
    }

    private static bool IsGrimy(string room, string sprite)
    {
        foreach ((string r, string s) in Grimy)
            if (r == room && s == sprite) return true;
        return false;
    }

    private static readonly Color White = FxMath.Rgba(1f, 1f, 1f);

    private static Group NewGroup(SpriteRenderer orig) => new()
    {
        Orig = orig,
        // 半分は最初から荒れていて、残りは試合が長引くにつれて荒れていく。
        Threshold = FxMath.Value < 0.5f ? 0f : FxMath.Range(0.15f, 0.85f)
    };

    private static void Restore(Group g)
    {
        foreach (Bit b in g.Bits)
        {
            if (b.Sh) Object.Destroy(b.Sh.gameObject);
            if (b.Li) Object.Destroy(b.Li.gameObject);
        }

        g.Bits.Clear();
        if (g.Orig) g.Orig.enabled = true;
        g.Orig = null;
    }

    private static Sprite Load(string file) => Utils.LoadSprite(Res + file, 100f);

    private static (Sprite, float, Color) RandomPaper()
    {
        int c = FxMath.Range(0, 3);
        return FxMath.Range(0, 3) switch
        {
            0 => (Load($"ruin_binder{c}.png"), BinderW, White),
            1 => (Load($"ruin_binderopen{c}.png"), BinderOpenW, White),
            _ => (Load($"ruin_page{FxMath.Range(0, 3)}.png"), PageW, White)
        };
    }

    // バニラの絵の写しを、元と同じ大きさで散らす (少し暗くして、落ちて汚れた感じにする)。
    private static (Sprite, float, Color) CopyOf(SpriteRenderer sr)
    {
        Color c = sr.color;
        return (sr.sprite, -1f, FxMath.Rgba(c.r * 0.85f, c.g * 0.85f, c.b * 0.85f, c.a));
    }

    // バインダーが抜けた棚・汚れた家具の絵を、元の絵にぴったり重ねる。絵は元の絵と同じ大きさ・同じ画素で作ってある。
    private static bool Overlay(Group g, SpriteRenderer orig, string prefix)
    {
        Sprite over = Load($"{prefix}{FxMath.Range(0, 2)}.png");
        Sprite src = orig.sprite;
        if (!over || (int)over.rect.width != (int)src.rect.width || (int)over.rect.height != (int)src.rect.height)
        {
            Logger.Info($"AirshipRansack overlay size mismatch: {orig.name}", "AirshipLiminal");
            return false;
        }

        // 元の絵の中心は pivot からずれている。読み込んだ絵は真ん中が pivot なので、そのずれの分だけ動かす。
        float ppu = src.pixelsPerUnit;
        float lx = (src.rect.width * 0.5f - src.pivot.x) / ppu, ly = (src.rect.height * 0.5f - src.pivot.y) / ppu;
        if (orig.flipX) lx = -lx;
        if (orig.flipY) ly = -ly;
        Transform ot = orig.transform;
        Vector3 pos = ot.TransformPoint(FxMath.V3(lx, ly, 0f));
        pos.z = ot.position.z - 0.0005f;
        g.Bits.Add(MakeBit(over, orig, pos, ot.rotation, ot.lossyScale, White, false));
        return true;
    }

    // anchor の周り (spread 以内) の床に 1 つ散らす。置ける所が無ければ置かない (0 を返す)。width < 0 は元の絵の大きさのまま。
    private static int Scatter(Group g, Bounds anchor, (Sprite Sprite, float Width, Color Tint) item, SpriteRenderer like = null, float spread = 0.7f)
    {
        Sprite sprite = item.Sprite;
        if (!sprite) return 0;

        Vector3 scale;
        if (item.Width > 0f)
        {
            float s = item.Width / (sprite.rect.width / sprite.pixelsPerUnit);
            scale = FxMath.V3(s, s, 1f);
        }
        else if (like)
            scale = like.transform.lossyScale;
        else
            return 0;

        float hx = sprite.rect.width / sprite.pixelsPerUnit * 0.5f * FxMath.Abs(scale.x);
        float hy = sprite.rect.height / sprite.pixelsPerUnit * 0.5f * FxMath.Abs(scale.y);
        // 床の上だけ。元の物の上 (机や棚の上) には乗せない。
        var nowhere = new Bounds(FxMath.V3(-9999f, -9999f, 0f), FxMath.V3(0f, 0f, 0f));
        for (int attempt = 0; attempt < 20; attempt++)
        {
            float x = FxMath.Range(anchor.min.x - spread, anchor.max.x + spread);
            float y = FxMath.Range(anchor.min.y - spread, anchor.max.y + spread);
            if (x > anchor.min.x && x < anchor.max.x && y > anchor.min.y && y < anchor.max.y) continue;
            float ang = FxMath.Range(0f, 360f), rad = ang * FxMath.Deg2Rad;
            if (!AirshipWreck.Fits(nowhere, x, y, hx * 0.7f, hy * 0.7f, FxMath.Cos(rad), FxMath.Sin(rad))) continue;
            Vector3 pos = FxMath.V3(x, y, FloorZ(x, y));
            g.Bits.Add(MakeBit(sprite, like, pos, FxMath.RotZ(ang), scale, item.Tint, false));
            return 1;
        }

        return 0;
    }

    // 床の汚れより手前・人や家具より奥。
    private static float FloorZ(float x, float y)
    {
        float z = 4.75f;
        foreach (AirshipRuinLayout.RoomGrid g in AirshipRuinLayout.Grids)
        {
            if (!g.Inside(x, y)) continue;
            z = g.Z - 0.045f;
            break;
        }

        return z - (_order++ % 50) * 0.0001f;
    }

    private static Bit MakeBit(Sprite sprite, SpriteRenderer like, Vector3 pos, Quaternion rot, Vector3 scale, Color tint, bool up)
    {
        if (up && like) like.enabled = false;
        return new Bit
        {
            Sh = Make(sprite, like, ShadowOnlyLayer, pos, rot, scale, up),
            Li = Make(sprite, like, 0, pos, rot, scale, up),
            Tint = tint,
            Up = up
        };
    }

    private static SpriteRenderer Make(Sprite sprite, SpriteRenderer like, int layer, Vector3 pos, Quaternion rot, Vector3 scale, bool up)
    {
        var go = new GameObject("Ransack" + (layer == 0 ? "Lit" : "Shadow")) { layer = layer };
        Transform tf = go.transform;
        tf.SetParent(_root, false);
        tf.position = pos;
        tf.rotation = rot;
        tf.localScale = scale;
        SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        if (like)
        {
            // 荒れる前の姿だけ元と同じマテリアルにする。散らした写しは既定のままにして、タスクの黄色い縁取りを写さない。
            if (up) sr.sharedMaterial = like.sharedMaterial;
            sr.flipX = like.flipX;
            sr.flipY = like.flipY;
            sr.sortingLayerID = like.sortingLayerID;
            sr.sortingOrder = like.sortingOrder;
        }

        sr.color = default;
        return sr;
    }

    // fadeIn: 組み立て直後のフェード (0→1)。invade: 視界の中への侵食 (0→1)。decay: 試合の長さ (0→1)。
    public static void Animate(float fadeIn, float invade, float decay)
    {
        if (!_root || fadeIn == _shownFade && decay == _shownDecay && invade == _shownLit) return;
        _shownFade = fadeIn;
        _shownDecay = decay;
        _shownLit = invade;
        foreach (Group g in _groups)
        {
            float grow = g.Threshold <= 0f ? 1f : FxMath.Clamp01((decay - g.Threshold) / 0.1f);
            Show(g, fadeIn * grow, invade);
        }
    }

    // down: 影の中で荒れている度合い (0→1)。lit: 視界の中でも荒れている度合い (0→1)。
    private static void Show(Group g, float down, float lit)
    {
        float litDown = down * lit;
        foreach (Bit b in g.Bits)
        {
            Color c = b.Tint;
            float sh = b.Up ? 1f - down : down, li = b.Up ? 1f - litDown : litDown;
            // 写しはルートの子で、ルートごと壊れるまで生きている。存在の有無は managed の null 比較で足りる。
            if (b.Sh is not null) b.Sh.color = FxMath.Rgba(c.r, c.g, c.b, c.a * sh);
            if (b.Li is not null) b.Li.color = FxMath.Rgba(c.r, c.g, c.b, c.a * li);
        }
    }

    public static void Teardown()
    {
        // 写しは AirshipRuin のルートと一緒に壊れる。隠したバニラの絵は描画を戻す (船ごと壊れた時は残っていない)。
        foreach (Group g in _groups)
            if (g.Orig) g.Orig.enabled = true;
        _groups = [];
        _root = null;
    }
}
