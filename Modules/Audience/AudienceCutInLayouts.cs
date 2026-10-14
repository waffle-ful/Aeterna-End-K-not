using System.Collections;
using TMPro;
using UnityEngine;

namespace EndKnot.Modules.Audience;

// 帯を使わない種類のカットイン。種類ごとに画面の組み立てそのものを変える
// (停電 = 闇を走る懐中電灯 / ドア封鎖 = 叩き降りるシャッター / 呪い = 描かれていく魔法陣 / 隕石 = 着弾)。
// 光の部品は自前で作る (ExplosionFx の下ごしらえ前のロビーでも同じ見た目にするため)
internal static partial class AudienceCutIn
{
    private static Sprite _soft, _hole, _thinRing, _tail, _cone;

    private static bool HasLayout(string kindKey) => kindKey is "Blackout" or "Doors" or "Curse" or "Meteor" or "Reactor" or "Comms" or "Earthquake"
        or "Voice" or "FakeBody" or "ShuffleOne" or "ShuffleAll" or "Bless";

    private static Layout NewLayout(string kindKey) => kindKey switch
    {
        "Blackout" => new BlackoutLayout(),
        "Doors" => new DoorsLayout(),
        "Curse" => new CurseLayout(),
        "Meteor" => new MeteorLayout(),
        "Reactor" => new ReactorLayout(),
        "Comms" => new CommsLayout(),
        "Earthquake" => new EarthquakeLayout(),
        "Voice" => new VoiceLayout(),
        "FakeBody" => new FakeBodyLayout(),
        "ShuffleOne" => new ShuffleOneLayout(),
        "ShuffleAll" => new ShuffleAllLayout(),
        "Bless" => new BlessLayout(),
        _ => null
    };

    private sealed class Ctx
    {
        public Transform Rt;
        public float HalfW, HalfH;
        public Color Main, Light, Deep;
        public Sprite Sigil;
        public PoolablePlayer Crew;
        public bool HasCrew;
        public Transform CrewT;
        public string Kind, Header, KindLine, ByLine, Message;
        public float Duration = 1.6f;
        public float DimMax = 0.35f;
        public Color DimColor = FxMath.Rgba(0f, 0f, 0f);
        public float FlashMax = 0.55f;
        public float Jx, Jy;
    }

    private abstract class Layout
    {
        public abstract void Build(Ctx c);
        public abstract void Update(Ctx c, float t, float dt, float fade, float openE);
    }

    private static IEnumerator CoLayout(string kindKey, string header, string kindLine, string byLine, string message, string author, Color theme)
    {
        Transform cam = HudManager.Instance.transform.parent;
        if (!cam) yield break;

        Close(_root, _crew);
        _root = null;
        _crew = null;
        EnsureSprite();
        EnsureLayoutSprites();

        var c = new Ctx { Kind = kindKey, Header = header, KindLine = kindLine, ByLine = byLine, Message = message ?? "", Main = theme, HalfH = 3f, HalfW = 5.4f };

        try
        {
            Camera cc = cam.GetComponent<Camera>();
            if (cc)
            {
                c.HalfH = cc.orthographicSize;
                c.HalfW = c.HalfH * cc.aspect;
            }
        }
        catch (System.Exception e) { Utils.ThrowException(e); }

        c.Light = FxMath.Rgba(FxMath.Lerp(theme.r, 1f, 0.55f), FxMath.Lerp(theme.g, 1f, 0.55f), FxMath.Lerp(theme.b, 1f, 0.55f));
        c.Deep = FxMath.Rgba(theme.r * 0.24f, theme.g * 0.24f, theme.b * 0.24f, 0.94f);
        c.Sigil = ExplosionFx.BorrowLightSprites().Sigil;

        // 波動砲のカットインと同時に出たら波動砲を手前にする
        GameObject root = _root = new GameObject("AudienceCutIn") { layer = Layer };
        root.transform.SetParent(cam, false);
        root.transform.localPosition = FxMath.V3(0f, 0f, -880f);
        c.Rt = root.transform;

        SpriteRenderer dim = Quad(c.Rt, "Dim", FxMath.Rgba(0f, 0f, 0f, 0f), 0f, 0f, c.HalfW * 2.2f, c.HalfH * 2.2f, 0.3f);
        SpriteRenderer flash = Quad(c.Rt, "Flash", FxMath.Rgba(1f, 1f, 1f, 0f), 0f, 0f, c.HalfW * 2.2f, c.HalfH * 2.2f, -0.3f);

        try
        {
            if (!string.IsNullOrEmpty(author) && HudManager.Instance.IntroPrefab && HudManager.Instance.IntroPrefab.PlayerPrefab)
            {
                PoolablePlayer crew = Object.Instantiate(HudManager.Instance.IntroPrefab.PlayerPrefab, c.Rt);
                _crew = c.Crew = crew;
                // 着せ替えで例外が出ても、位置は毎フレーム動かせるように先に持つ
                c.CrewT = crew.transform;
                c.HasCrew = true;
                c.CrewT.localScale = FxMath.V3(1.05f, 1.05f, 1f);
                c.CrewT.localPosition = FxMath.V3(99f, 99f, 0f);
                crew.UpdateFromPlayerOutfit(YouTubeChat.AudienceSeats.OutfitFor(author), PlayerMaterial.MaskType.None, false, false);
                crew.ToggleName(false);
                crew.TogglePet(false);
            }
        }
        catch (System.Exception e) { Utils.ThrowException(e); }

        Layout layout = NewLayout(kindKey);
        layout.Build(c);
        SetLayer(c.Rt);

        float dur = c.Duration;
        float closeFrom = dur - CloseLen;
        float t = 0f;

        while (t < dur)
        {
            if (!root || _root != root || ShouldStop()) break;

            float dt = Time.deltaTime;
            t += dt;

            float open = FxMath.Clamp01(t / OpenTime);
            float close = t > closeFrom ? FxMath.Clamp01((t - closeFrom) / CloseLen) : 0f;
            float openE = 1f - (1f - open) * (1f - open);
            float fade = 1f - close;

            c.Jx = 0f;
            c.Jy = 0f;
            layout.Update(c, t, dt, fade, openE);
            c.Rt.localPosition = FxMath.V3(c.Jx, c.Jy, -880f);

            Color dc = c.DimColor;
            dim.color = FxMath.Rgba(dc.r, dc.g, dc.b, c.DimMax * openE * fade);
            Color l = c.Light;
            flash.color = t < 0.14f ? FxMath.Rgba(l.r, l.g, l.b, c.FlashMax * (1f - t / 0.14f)) : FxMath.Rgba(1f, 1f, 1f, 0f);

            yield return null;
        }

        if (_root == root)
        {
            Close(root, c.Crew);
            _root = null;
            _crew = null;
        }
        else if (root) Close(root, c.Crew);
    }

    private static void PlaceCrew(Ctx c, float x, float y, float z, float rot = 0f)
    {
        if (!c.HasCrew) return;
        c.CrewT.localPosition = FxMath.V3(x, y, z);
        c.CrewT.localRotation = FxMath.RotZ(rot);
    }

    private static void FlipCrew(Ctx c, bool faceLeft)
    {
        if (!c.HasCrew) return;
        try { c.Crew.SetFlipX(faceLeft); }
        catch (System.Exception e) { Utils.ThrowException(e); }
    }

    private static void SetText(TextMeshPro tx, float x, float y, float z, float scale, float alpha)
    {
        if (tx is null) return;
        tx.transform.localPosition = FxMath.V3(x, y, z);
        tx.transform.localScale = FxMath.V3(scale, scale, 1f);
        tx.alpha = alpha;
    }

    // 技名の縁取りを干渉の色にする (元の文字は他と共有のマテリアルなので、種類ごとの複製に差し替える)
    private static readonly System.Collections.Generic.Dictionary<string, Material> OutlineMats = new();

    private static void Outline(Ctx c, TextMeshPro tx, Color? color = null)
    {
        if (tx is null) return;

        try
        {
            // 複製元の文字の字形 (フォント) が変わったら作り直す
            string key = c.Kind + ":" + (tx.font ? tx.font.GetInstanceID() : 0);
            if (!OutlineMats.TryGetValue(key, out Material m) || !m)
            {
                Color oc = color ?? FxMath.Rgba(c.Main.r * 0.35f, c.Main.g * 0.35f, c.Main.b * 0.35f);
                m = new Material(tx.fontSharedMaterial) { hideFlags = HideFlags.HideAndDontSave };
                m.SetColor("_OutlineColor", oc);
                OutlineMats[key] = m;
            }

            tx.fontSharedMaterial = m;
        }
        catch (System.Exception e) { Utils.ThrowException(e); }
    }

    private static float TitleSize(string s, float max) => FxMath.Clamp(73f / FxMath.Max(1, s.Length), 3.2f, max);

    private static float EaseOut3(float p)
    {
        float q = 1f - FxMath.Clamp01(p);
        return 1f - q * q * q;
    }

    // ---- 停電: ブレーカーが落ちて真っ暗 → 懐中電灯の光が走り、照らされた所だけ見える ----
    private sealed class BlackoutLayout : Layout
    {
        private const float HoleW = 7.4f, HoleH = 3.7f;
        private Transform _dark;
        private SpriteRenderer[] _darkParts;
        private SpriteRenderer _warm, _beam;
        private bool _hasBeam;
        private TextMeshPro _title, _shadow, _head, _by;
        private float _seed;
        private bool _flipped;

        public override void Build(Ctx c)
        {
            c.DimMax = 0f;
            c.FlashMax = 0.7f;
            _seed = FxMath.Range(0f, 100f);

            var go = new GameObject("Dark") { layer = Layer };
            _dark = go.transform;
            _dark.SetParent(c.Rt, false);
            _dark.localPosition = FxMath.V3(c.HalfW * 3f, 0.15f, -0.2f);

            // 穴の開いた闇 = 縁がぼけた穴の絵 1 枚 + 上下左右を埋める黒い板 4 枚
            float big = c.HalfW * 5f, o = 0.02f;
            _darkParts = new SpriteRenderer[5];
            _darkParts[0] = Spr(_dark, "Hole", _hole, FxMath.Rgba(0f, 0f, 0f, 0f), 0f, 0f, HoleW, HoleH, 0f);
            _darkParts[1] = Quad(_dark, "L", FxMath.Rgba(0f, 0f, 0f, 0f), -HoleW * 0.5f - big * 0.5f + o, 0f, big, big, 0f);
            _darkParts[2] = Quad(_dark, "R", FxMath.Rgba(0f, 0f, 0f, 0f), HoleW * 0.5f + big * 0.5f - o, 0f, big, big, 0f);
            _darkParts[3] = Quad(_dark, "T", FxMath.Rgba(0f, 0f, 0f, 0f), 0f, HoleH * 0.5f + big * 0.5f - o, HoleW, big, 0f);
            _darkParts[4] = Quad(_dark, "B", FxMath.Rgba(0f, 0f, 0f, 0f), 0f, -HoleH * 0.5f - big * 0.5f + o, HoleW, big, 0f);
            _warm = Spr(_dark, "Warm", _soft, FxMath.Rgba(1f, 0.93f, 0.7f, 0f), 0f, 0f, HoleW * 0.95f, HoleH * 1.05f, 0.05f);

            // 右上から差す懐中電灯の光の筋 (闇の手前)
            _beam = Spr(c.Rt, "Cone", _cone, FxMath.Rgba(1f, 0.95f, 0.75f, 0f), c.HalfW + 0.6f, c.HalfH + 0.6f, 1f, 1f, -0.25f);
            _hasBeam = _beam;

            float size = TitleSize(c.KindLine, 6.5f);
            _shadow = Text(c.Rt, "TitleShadow", c.KindLine, size, FxMath.Rgba(0.02f, 0.08f, 0.22f, 0.95f), c.HalfW * 2f, true, false);
            _title = Text(c.Rt, "Title", c.KindLine, size, FxMath.Rgba(1f, 1f, 1f), c.HalfW * 2f, true, false);
            _head = Text(c.Rt, "Header", c.Header, 2.2f, c.Light, c.HalfW * 2f, true, false);
            _by = Text(c.Rt, "By", c.ByLine, FxMath.Clamp(52f / FxMath.Max(1, c.ByLine.Length), 1.4f, 2.4f), FxMath.Rgba(1f, 1f, 1f, 0.92f), c.HalfW * 2f, false, false);
            Outline(c, _title);

            FlipCrew(c, true);
        }

        public override void Update(Ctx c, float t, float dt, float fade, float openE)
        {
            // 落ちた直後の明滅 → 真っ暗 → 光が右から走って止まり、手持ちで少し揺れる
            float darkA;
            if (t < 0.08f) darkA = 0f;
            else if (t < 0.34f) darkA = Hash(FxMath.Floor(t * 22f) + _seed) < 0.5f ? 0.96f : 0.3f;
            else darkA = 0.93f;
            darkA *= fade;

            float sweep = EaseOut3((t - 0.34f) / 0.3f);
            float hx = t < 0.34f ? c.HalfW * 3f : FxMath.Lerp(c.HalfW * 1.7f, 0f, sweep);
            float hy = 0.15f;
            if (t >= 0.64f)
            {
                hx += FxMath.Sin(t * 2.3f + _seed) * 0.12f;
                hy += FxMath.Sin(t * 3.1f + _seed) * 0.06f;
            }

            _dark.localPosition = FxMath.V3(hx, hy, -0.2f);
            for (int i = 0; i < _darkParts.Length; i++) _darkParts[i].color = FxMath.Rgba(0f, 0f, 0f, darkA);

            bool lit = t >= 0.34f;
            _warm.color = FxMath.Rgba(1f, 0.93f, 0.7f, lit ? 0.12f * fade : 0f);

            if (_hasBeam)
            {
                float fx = c.HalfW + 0.6f, fy = c.HalfH + 0.6f;
                float dx = hx - fx, dy = hy - fy;
                float len = FxMath.Sqrt(dx * dx + dy * dy);
                _beam.transform.localRotation = FxMath.RotZ(FxMath.Atan2(dy, dx) * FxMath.Rad2Deg);
                _beam.transform.localScale = FxMath.V3(len, HoleH * 1.7f, 1f);
                _beam.color = FxMath.Rgba(1f, 0.95f, 0.75f, lit ? 0.16f * fade : 0f);
            }

            // 光が当たった瞬間に驚いて跳ね、光の来る方へ向き直る
            float hop = 0f;
            if (t >= 0.62f)
            {
                if (!_flipped)
                {
                    _flipped = true;
                    FlipCrew(c, false);
                }

                float k = t - 0.62f;
                if (k < 0.36f) hop = FxMath.Abs(FxMath.Sin(k * 8.7f)) * 0.24f * (1f - k / 0.36f);
            }

            PlaceCrew(c, -2.0f, -0.45f + hop, 0f);

            float punch = t >= 0.64f ? 1f + 0.25f * (1f - FxMath.Clamp01((t - 0.64f) / 0.14f)) : 1.25f;
            SetText(_title, 0.95f, 0.25f, -0.05f, punch, fade);
            SetText(_shadow, 1.02f, 0.18f, -0.04f, punch, 0.95f * fade);
            SetText(_head, 0.95f, 1.15f, -0.05f, 1f, fade);
            SetText(_by, 1.1f, -0.55f, -0.05f, 1f, FxMath.Clamp01((t - 0.7f) / 0.2f) * fade);
        }
    }

    // ---- ドア封鎖: 縦のシャッターが叩き降り、鉄板に技名が塗られている。クルーは閉まる寸前に下へ滑り込む ----
    private sealed class DoorsLayout : Layout
    {
        private const float BottomY = -1.15f;
        private Transform _shutter;
        private float _restY, _h;
        private SpriteRenderer[] _lamps;
        private SpriteRenderer[] _dust;
        private float[] _dustX, _dustVx;
        private TextMeshPro _title, _shadow, _head, _by;
        private float _titleY;

        public override void Build(Ctx c)
        {
            c.DimMax = 0.3f;
            c.FlashMax = 0.35f;

            float top = c.HalfH + 0.3f;
            _h = top - BottomY;
            _restY = (top + BottomY) * 0.5f;
            float w = c.HalfW * 2.3f;

            var go = new GameObject("Shutter") { layer = Layer };
            _shutter = go.transform;
            _shutter.SetParent(c.Rt, false);
            _shutter.localPosition = FxMath.V3(0f, _restY + _h + 0.5f, 0.1f);

            Quad(_shutter, "Plate", FxMath.Rgba(0.22f, 0.23f, 0.26f, 0.98f), 0f, 0f, w, _h, 0f);

            // 板の段 (明るい上縁 + 暗い下の溝)
            const float slat = 0.34f;
            int n = (int)(_h / slat);
            for (int i = 0; i < n; i++)
            {
                float y = _h * 0.5f - (i + 0.5f) * slat;
                Quad(_shutter, "Slat", i % 2 == 0 ? FxMath.Rgba(0.30f, 0.31f, 0.35f, 1f) : FxMath.Rgba(0.27f, 0.28f, 0.32f, 1f), 0f, y, w, slat * 0.86f, -0.01f);
                Quad(_shutter, "Hi", FxMath.Rgba(0.62f, 0.64f, 0.7f, 0.55f), 0f, y + slat * 0.41f, w, 0.025f, -0.02f);
                Quad(_shutter, "Groove", FxMath.Rgba(0f, 0f, 0f, 0.55f), 0f, y - slat * 0.47f, w, 0.05f, -0.02f);
            }

            // 下端の框: 暗い桟 + 干渉の色の光の線 + 取っ手
            float by = -_h * 0.5f;
            Quad(_shutter, "Rail", FxMath.Rgba(0.12f, 0.12f, 0.14f, 1f), 0f, by + 0.14f, w, 0.3f, -0.03f);
            Quad(_shutter, "RailLight", c.Main, 0f, by + 0.3f, w, 0.06f, -0.04f);
            Quad(_shutter, "RailCore", FxMath.Rgba(1f, 0.92f, 0.75f, 0.9f), 0f, by + 0.3f, w, 0.018f, -0.05f);
            for (int s = -1; s <= 1; s += 2)
                Quad(_shutter, "Handle", FxMath.Rgba(0.45f, 0.46f, 0.5f, 1f), s * c.HalfW * 0.45f, by + 0.12f, 0.6f, 0.09f, -0.05f);

            // 回転灯 (框の両端)
            _lamps = new SpriteRenderer[2];
            for (int s = 0; s < 2; s++)
                _lamps[s] = Spr(_shutter, "Lamp", _soft, FxMath.Rgba(1f, 0.5f, 0f, 0f), (s == 0 ? -1f : 1f) * c.HalfW * 0.82f, by + 0.3f, 1.6f, 1.6f, -0.06f);

            // 塗装された技名 (鉄板の子なので一緒に降りる)
            _titleY = 0.55f - _restY;
            float size = TitleSize(c.KindLine, 7f);
            _shadow = Text(_shutter, "TitleShadow", c.KindLine, size, FxMath.Rgba(0.06f, 0.05f, 0.04f, 0.9f), c.HalfW * 2f, false, false);
            _title = Text(_shutter, "Title", c.KindLine, size, FxMath.Rgba(1f, 0.66f, 0.16f), c.HalfW * 2f, false, false);
            Outline(c, _title, FxMath.Rgba(0.12f, 0.06f, 0f));
            _head = Text(_shutter, "Header", c.Header, 2.2f, FxMath.Rgba(1f, 0.85f, 0.55f), c.HalfW * 2f, false, false);
            _by = Text(c.Rt, "By", c.ByLine, FxMath.Clamp(52f / FxMath.Max(1, c.ByLine.Length), 1.4f, 2.2f), FxMath.Rgba(1f, 1f, 1f, 0.95f), c.HalfW * 1.6f, false, false);

            // 着地の土ぼこり
            const int dn = 10;
            _dust = new SpriteRenderer[dn];
            _dustX = new float[dn];
            _dustVx = new float[dn];
            for (int i = 0; i < dn; i++)
            {
                _dustX[i] = FxMath.Range(-c.HalfW, c.HalfW);
                _dustVx[i] = (_dustX[i] < 0f ? -1f : 1f) * FxMath.Range(0.6f, 1.8f);
                float s = FxMath.Range(0.9f, 1.6f);
                _dust[i] = Spr(c.Rt, "Dust", _soft, FxMath.Rgba(0.6f, 0.57f, 0.52f, 0f), _dustX[i], BottomY, s * 1.4f, s, -0.08f);
            }

            FlipCrew(c, false);
        }

        public override void Update(Ctx c, float t, float dt, float fade, float openE)
        {
            const float land = 0.24f;
            float startY = _restY + _h + 0.5f;
            float y;
            if (t < land)
            {
                float p = FxMath.Clamp01((t - 0.04f) / (land - 0.04f));
                y = FxMath.Lerp(startY, _restY, p * p);
            }
            else
            {
                float k = t - land;
                y = _restY - 0.1f * FxMath.Sin(k * 30f) * FxMath.Exp(-k * 10f);
            }

            // 最後に巻き上がって消える
            float riseFrom = c.Duration - 0.34f;
            if (t > riseFrom)
            {
                float q = FxMath.Clamp01((t - riseFrom) / 0.34f);
                y += q * q * (_h + 0.8f);
            }

            _shutter.localPosition = FxMath.V3(0f, y, 0.1f);

            if (t >= land && t < land + 0.22f) c.Jy = FxMath.Sin(t * 90f) * 0.1f * (1f - (t - land) / 0.22f);

            float blink = 0.5f + 0.5f * FxMath.Sin(t * 16f);
            _lamps[0].color = FxMath.Rgba(1f, 0.5f, 0f, 0.75f * blink * openE);
            _lamps[1].color = FxMath.Rgba(1f, 0.5f, 0f, 0.75f * (1f - blink) * openE);

            for (int i = 0; i < _dust.Length; i++)
            {
                float k = t - land;
                if (k < 0f) continue;
                float a = FxMath.Clamp01(k / 0.05f) * FxMath.Clamp01(1f - k / 0.7f);
                float dx = _dustVx[i] * (1f - FxMath.Exp(-k * 3f));
                _dust[i].transform.localPosition = FxMath.V3(_dustX[i] + dx, BottomY - 0.1f + k * 0.5f, -0.08f);
                _dust[i].color = FxMath.Rgba(0.6f, 0.57f, 0.52f, 0.5f * a * fade);
            }

            // 閉まる寸前に左から滑り込み (寝そべり → 起き上がる)
            float slide = EaseOut3(t / 0.22f);
            float cx = FxMath.Lerp(-c.HalfW * 1.3f, -c.HalfW * 0.62f, slide);
            float rot = FxMath.Lerp(-70f, 0f, FxMath.Clamp01((t - 0.18f) / 0.16f));
            float hop = t >= 0.34f && t < 0.7f ? FxMath.Abs(FxMath.Sin((t - 0.34f) * 8.7f)) * 0.15f * (1f - (t - 0.34f) / 0.36f) : 0f;
            PlaceCrew(c, cx, -1.95f + hop, -0.1f, rot);

            float punch = t >= land ? 1f + 0.3f * (1f - FxMath.Clamp01((t - land) / 0.12f)) : 1f;
            SetText(_title, 0f, _titleY, -0.1f, punch, fade);
            SetText(_shadow, 0.06f, _titleY - 0.06f, -0.09f, punch, 0.9f * fade);
            SetText(_head, 0f, _titleY + 0.95f, -0.1f, 1f, fade);
            SetText(_by, 1.3f, -1.75f, -0.1f, 1f, FxMath.Clamp01((t - 0.36f) / 0.2f) * fade);
        }
    }

    // ---- 呪い: 画面の真ん中に魔法陣が描かれていき、中心でクルーが浮かぶ ----
    private sealed class CurseLayout : Layout
    {
        private const float R = 2.0f, Cy = 0.35f;
        private const int RuneCount = 24;
        private Transform _circle;
        private SpriteRenderer _outer, _inner, _sigil, _aura, _pulse;
        private SpriteRenderer[] _runes;
        private SpriteRenderer[] _lineUnder, _lineCore;
        private Transform[] _lineUnderT, _lineCoreT;
        private Vector2[] _from, _to;
        private SpriteRenderer[] _vig, _mist;
        private float[] _mistX, _mistSize, _mistRise;
        private TextMeshPro _title, _shadow, _ghost, _head, _by;

        public override void Build(Ctx c)
        {
            c.DimMax = 0.5f;
            c.DimColor = FxMath.Rgba(0.05f, 0f, 0.08f);
            Color m = c.Main, l = c.Light;

            _vig = new SpriteRenderer[4];
            for (int i = 0; i < 4; i++)
            {
                float x = (i % 2 == 0 ? -1f : 1f) * c.HalfW, y = (i < 2 ? 1f : -1f) * c.HalfH;
                _vig[i] = Spr(c.Rt, "Vignette", _soft, FxMath.Rgba(m.r, m.g, m.b, 0f), x, y, c.HalfH * 3.2f, c.HalfH * 3.2f, 0.28f);
            }

            _mist = new SpriteRenderer[8];
            _mistX = new float[8];
            _mistSize = new float[8];
            _mistRise = new float[8];
            for (int i = 0; i < _mist.Length; i++)
            {
                _mistX[i] = FxMath.Range(-c.HalfW, c.HalfW);
                _mistSize[i] = FxMath.Range(2.2f, 3.4f);
                _mistRise[i] = FxMath.Range(1.2f, 2.6f);
                _mist[i] = Spr(c.Rt, "Mist", _soft, FxMath.Rgba(0.22f, 0.08f, 0.3f, 0f), _mistX[i], -c.HalfH, _mistSize[i] * 1.3f, _mistSize[i], 0.25f);
            }

            var go = new GameObject("Circle") { layer = Layer };
            _circle = go.transform;
            _circle.SetParent(c.Rt, false);
            _circle.localPosition = FxMath.V3(0f, Cy, 0.1f);

            _aura = Spr(c.Rt, "Aura", _soft, FxMath.Rgba(m.r, m.g, m.b, 0f), 0f, Cy - 0.1f, 3.4f, 3.4f, 0.12f);
            _pulse = Quad(c.Rt, "Pulse", FxMath.Rgba(m.r, m.g, m.b, 0f), 0f, 0f, c.HalfW * 2.2f, c.HalfH * 2.2f, -0.28f);

            if (c.Sigil) _sigil = Spr(_circle, "Sigil", c.Sigil, FxMath.Rgba(m.r, m.g, m.b, 0f), 0f, 0f, R * 1.45f, R * 1.45f, 0.02f);
            _outer = Spr(_circle, "Outer", _thinRing, FxMath.Rgba(l.r, l.g, l.b, 0f), 0f, 0f, R * 2f, R * 2f, 0f);
            _inner = Spr(_circle, "Inner", _thinRing, FxMath.Rgba(l.r, l.g, l.b, 0f), 0f, 0f, R * 1.56f, R * 1.56f, 0f);

            // 二重の輪の間を一周するルーン
            _runes = new SpriteRenderer[RuneCount];
            for (int i = 0; i < RuneCount; i++)
            {
                float a = 90f - i * 360f / RuneCount;
                float rad = a / FxMath.Rad2Deg;
                float rr = R * 0.89f;
                bool tick = i % 3 == 0;
                _runes[i] = Quad(_circle, "Rune", FxMath.Rgba(l.r, l.g, l.b, 0f), FxMath.Cos(rad) * rr, FxMath.Sin(rad) * rr, tick ? 0.06f : 0.2f, tick ? 0.2f : 0.05f, -0.01f);
                _runes[i].transform.localRotation = FxMath.RotZ(a + 90f);
            }

            // 六芒星: 頂点から次の頂点へ線が伸びていく
            _from = new Vector2[6];
            _to = new Vector2[6];
            _lineUnder = new SpriteRenderer[6];
            _lineCore = new SpriteRenderer[6];
            _lineUnderT = new Transform[6];
            _lineCoreT = new Transform[6];
            float vr = R * 0.78f;
            for (int i = 0; i < 6; i++)
            {
                int tri = i / 3, k = i % 3;
                float a0 = (90f + tri * 60f + k * 120f) / FxMath.Rad2Deg;
                float a1 = (90f + tri * 60f + (k + 1) * 120f) / FxMath.Rad2Deg;
                _from[i] = FxMath.V2(FxMath.Cos(a0) * vr, FxMath.Sin(a0) * vr);
                _to[i] = FxMath.V2(FxMath.Cos(a1) * vr, FxMath.Sin(a1) * vr);
                float ang = FxMath.Atan2(_to[i].y - _from[i].y, _to[i].x - _from[i].x) * FxMath.Rad2Deg;
                _lineUnder[i] = Quad(_circle, "LineUnder", FxMath.Rgba(m.r, m.g, m.b, 0f), 0f, 0f, 0f, 0.15f, 0.005f);
                _lineCore[i] = Quad(_circle, "LineCore", FxMath.Rgba(l.r, l.g, l.b, 0f), 0f, 0f, 0f, 0.04f, -0.005f);
                _lineUnderT[i] = _lineUnder[i].transform;
                _lineCoreT[i] = _lineCore[i].transform;
                _lineUnderT[i].localRotation = FxMath.RotZ(ang);
                _lineCoreT[i].localRotation = FxMath.RotZ(ang);
            }

            float size = TitleSize(c.KindLine, 6.2f);
            _ghost = Text(c.Rt, "TitleGhost", c.KindLine, size, FxMath.Rgba(m.r, m.g, m.b, 0.5f), c.HalfW * 2f, false, false);
            _shadow = Text(c.Rt, "TitleShadow", c.KindLine, size, FxMath.Rgba(0.1f, 0f, 0.15f, 0.95f), c.HalfW * 2f, false, false);
            _title = Text(c.Rt, "Title", c.KindLine, size, FxMath.Rgba(0.97f, 0.9f, 1f), c.HalfW * 2f, false, false);
            Outline(c, _title);
            _head = Text(c.Rt, "Header", c.Header, 2.2f, l, c.HalfW * 2f, true, false);
            _by = Text(c.Rt, "By", c.ByLine, FxMath.Clamp(52f / FxMath.Max(1, c.ByLine.Length), 1.4f, 2.2f), FxMath.Rgba(1f, 1f, 1f, 0.92f), c.HalfW * 2f, false, false);
        }

        public override void Update(Ctx c, float t, float dt, float fade, float openE)
        {
            Color m = c.Main, l = c.Light;

            float grow = EaseOut3(t / 0.5f);
            float sc = 0.9f + 0.1f * grow;
            _circle.localRotation = FxMath.RotZ(-t * 24f);
            _circle.localScale = FxMath.V3(sc, sc, 1f);

            // 描き終わった瞬間に一度強く光る
            float burst = t >= 0.56f ? FxMath.Clamp01(1f - (t - 0.56f) / 0.3f) : 0f;
            float glowK = (0.85f + 0.5f * burst) * fade;

            float ringA = FxMath.Clamp01((t - 0.05f) / 0.25f);
            _outer.color = FxMath.Rgba(l.r, l.g, l.b, 0.9f * ringA * glowK);
            _inner.color = FxMath.Rgba(l.r, l.g, l.b, 0.65f * FxMath.Clamp01((t - 0.15f) / 0.25f) * glowK);
            _inner.transform.localRotation = FxMath.RotZ(t * 60f);

            for (int i = 0; i < _runes.Length; i++)
            {
                float a = FxMath.Clamp01((t - 0.08f - i * 0.016f) / 0.06f);
                _runes[i].color = FxMath.Rgba(l.r, l.g, l.b, 0.85f * a * glowK);
            }

            for (int i = 0; i < 6; i++)
            {
                float p = EaseOut3((t - 0.14f - i * 0.06f) / 0.14f);
                Vector2 a = _from[i], b = _to[i];
                float dx = b.x - a.x, dy = b.y - a.y;
                float len = FxMath.Sqrt(dx * dx + dy * dy) * p;
                float mx = a.x + dx * p * 0.5f, my = a.y + dy * p * 0.5f;
                _lineUnderT[i].localPosition = FxMath.V3(mx, my, 0.005f);
                _lineUnderT[i].localScale = FxMath.V3(len, 0.15f, 1f);
                _lineCoreT[i].localPosition = FxMath.V3(mx, my, -0.005f);
                _lineCoreT[i].localScale = FxMath.V3(len, 0.04f, 1f);
                float la = p > 0f ? 1f : 0f;
                _lineUnder[i].color = FxMath.Rgba(m.r, m.g, m.b, 0.5f * la * glowK);
                _lineCore[i].color = FxMath.Rgba(l.r, l.g, l.b, 0.95f * la * glowK);
            }

            if (_sigil)
            {
                _sigil.transform.localRotation = FxMath.RotZ(t * 70f);
                _sigil.color = FxMath.Rgba(m.r, m.g, m.b, 0.4f * FxMath.Clamp01((t - 0.4f) / 0.3f) * glowK);
            }

            _pulse.color = FxMath.Rgba(m.r, m.g, m.b, 0.22f * burst * fade);
            _aura.color = FxMath.Rgba(m.r, m.g, m.b, (0.5f + 0.12f * FxMath.Sin(t * 6f)) * grow * fade);

            for (int i = 0; i < 4; i++) _vig[i].color = FxMath.Rgba(m.r, m.g, m.b, (0.5f + 0.08f * FxMath.Sin(t * 5f)) * openE * fade);

            for (int i = 0; i < _mist.Length; i++)
            {
                float p = FxMath.Clamp01((t - i * 0.05f) / 1.3f);
                float y = -c.HalfH - 0.6f + (1f - (1f - p) * (1f - p)) * _mistRise[i];
                _mist[i].transform.localPosition = FxMath.V3(_mistX[i] + FxMath.Sin(t * 1.4f + i) * 0.3f, y, 0.25f);
                _mist[i].color = FxMath.Rgba(0.22f, 0.08f, 0.3f, 0.55f * FxMath.Clamp01(p * 4f) * fade);
            }

            // 陣の中心へ下から浮かび上がり、ゆらゆら漂う
            float rise = EaseOut3((t - 0.08f) / 0.42f);
            PlaceCrew(c, 0f, FxMath.Lerp(-1.6f, Cy - 0.25f, rise) + FxMath.Sin(t * 3.2f) * 0.07f, -0.05f);

            float ta = FxMath.Clamp01((t - 0.36f) / 0.3f) * fade;
            float ty = -1.95f + FxMath.Sin(t * 2.6f) * 0.04f;
            float ts = 0.86f + 0.14f * EaseOut3((t - 0.36f) / 0.3f);
            SetText(_title, 0f, ty, -0.1f, ts, ta);
            SetText(_shadow, 0.06f, ty - 0.06f, -0.09f, ts, 0.95f * ta);
            // 文字から抜け出して広がる残像
            float gp = FxMath.Repeat(t * 1.4f, 1f);
            SetText(_ghost, 0f, ty, -0.08f, ts * (1f + gp * 0.25f), 0.45f * (1f - gp) * ta);
            SetText(_head, 0f, Cy + R + 0.32f, -0.1f, 1f, FxMath.Clamp01((t - 0.1f) / 0.2f) * fade);
            SetText(_by, 0f, -2.62f, -0.1f, 1f, FxMath.Clamp01((t - 0.5f) / 0.2f) * fade);
        }
    }

    // ---- 隕石: 右上から火の玉が飛び込んで着弾。爆心から技名がはじけ出て、クルーは慌てて逃げる ----
    private sealed class MeteorLayout : Layout
    {
        private const float Impact = 0.48f;
        private const int DebrisCount = 14, EmberCount = 10, SmokeCount = 6;
        private Vector2 _p, _s;
        private Transform _ball;
        private SpriteRenderer _outer, _mid, _core, _tailWide, _tailCore, _crater, _flashFull;
        private SpriteRenderer[] _waves;
        private SpriteRenderer[] _debris, _embers, _smoke;
        private Vector2[] _dv;
        private float[] _dSpin, _eOff, _sDx;
        private TextMeshPro _title, _shadow, _head, _by;
        private bool _ran, _stopped;

        public override void Build(Ctx c)
        {
            c.DimMax = 0.38f;
            c.DimColor = FxMath.Rgba(0.18f, 0.04f, 0f);
            c.FlashMax = 0.25f;
            _p = FxMath.V2(0.7f, 0.05f);
            _s = FxMath.V2(c.HalfW + 1.5f, c.HalfH + 1.8f);

            float back = FxMath.Atan2(_s.y - _p.y, _s.x - _p.x) * FxMath.Rad2Deg;

            var go = new GameObject("Ball") { layer = Layer };
            _ball = go.transform;
            _ball.SetParent(c.Rt, false);
            _ball.localPosition = FxMath.V3(_s.x, _s.y, 0f);

            // 尾は進む向きの逆 (右上) へ。光の重ね: 広い橙 → 細い黄 → 外のにじみ → 中 → 白い核
            _tailWide = Spr(_ball, "TailWide", _tail, FxMath.Rgba(1f, 0.4f, 0.06f, 0.75f), 0f, 0f, 6f, 6f, 0.04f);
            _tailCore = Spr(_ball, "TailCore", _tail, FxMath.Rgba(1f, 0.85f, 0.45f, 0.9f), 0f, 0f, 5f, 2.4f, 0.03f);
            _tailWide.transform.localRotation = FxMath.RotZ(back);
            _tailCore.transform.localRotation = FxMath.RotZ(back);
            _outer = Spr(_ball, "Outer", _soft, FxMath.Rgba(1f, 0.35f, 0.05f, 0.6f), 0f, 0f, 3.2f, 3.2f, 0.02f);
            _mid = Spr(_ball, "Mid", _soft, FxMath.Rgba(1f, 0.68f, 0.2f, 0.9f), 0f, 0f, 1.6f, 1.6f, 0.01f);
            _core = Spr(_ball, "Core", _soft, FxMath.Rgba(1f, 0.98f, 0.88f, 1f), 0f, 0f, 0.8f, 0.8f, 0f);

            _embers = new SpriteRenderer[EmberCount];
            _eOff = new float[EmberCount];
            for (int i = 0; i < EmberCount; i++)
            {
                _eOff[i] = FxMath.Range(0f, 1f);
                _embers[i] = Quad(c.Rt, "Ember", FxMath.Rgba(1f, 0.6f, 0.15f, 0f), 99f, 99f, 0.09f, 0.09f, 0.05f);
            }

            _crater = Spr(c.Rt, "Crater", _soft, FxMath.Rgba(1f, 0.45f, 0.08f, 0f), _p.x, _p.y, 3.4f, 2.4f, 0.1f);

            _waves = new SpriteRenderer[2];
            for (int i = 0; i < 2; i++)
                _waves[i] = Spr(c.Rt, "Wave", _thinRing, FxMath.Rgba(1f, 0.75f, 0.4f, 0f), _p.x, _p.y, 1f, 1f, -0.02f);

            _smoke = new SpriteRenderer[SmokeCount];
            _sDx = new float[SmokeCount];
            for (int i = 0; i < SmokeCount; i++)
            {
                _sDx[i] = FxMath.Range(-1.2f, 1.2f);
                float s = FxMath.Range(1.6f, 2.6f);
                _smoke[i] = Spr(c.Rt, "Smoke", _soft, FxMath.Rgba(0.16f, 0.1f, 0.08f, 0f), _p.x, _p.y, s, s, 0.08f);
            }

            _debris = new SpriteRenderer[DebrisCount];
            _dv = new Vector2[DebrisCount];
            _dSpin = new float[DebrisCount];
            for (int i = 0; i < DebrisCount; i++)
            {
                float a = FxMath.Range(15f, 165f) / FxMath.Rad2Deg;
                float sp = FxMath.Range(4f, 9f);
                _dv[i] = FxMath.V2(FxMath.Cos(a) * sp, FxMath.Sin(a) * sp);
                _dSpin[i] = FxMath.Range(-720f, 720f);
                float s = FxMath.Range(0.12f, 0.26f);
                _debris[i] = Quad(c.Rt, "Debris", FxMath.Rgba(1f, 0.55f, 0.15f, 0f), _p.x, _p.y, s, s * FxMath.Range(0.6f, 1f), -0.04f);
            }

            _flashFull = Quad(c.Rt, "ImpactFlash", FxMath.Rgba(1f, 0.9f, 0.7f, 0f), 0f, 0f, c.HalfW * 2.2f, c.HalfH * 2.2f, -0.29f);

            float size = TitleSize(c.KindLine, 7f);
            _shadow = Text(c.Rt, "TitleShadow", c.KindLine, size, FxMath.Rgba(0.35f, 0.05f, 0f, 0.95f), c.HalfW * 2f, true, false);
            _title = Text(c.Rt, "Title", c.KindLine, size, FxMath.Rgba(1f, 0.95f, 0.75f), c.HalfW * 2f, true, false);
            Outline(c, _title, FxMath.Rgba(0.45f, 0.08f, 0f));
            _head = Text(c.Rt, "Header", c.Header, 2.2f, c.Light, c.HalfW * 2f, true, false);
            _by = Text(c.Rt, "By", c.ByLine, FxMath.Clamp(52f / FxMath.Max(1, c.ByLine.Length), 1.4f, 2.4f), FxMath.Rgba(1f, 1f, 1f, 0.92f), c.HalfW * 2f, false, false);

            FlipCrew(c, false);
        }

        public override void Update(Ctx c, float t, float dt, float fade, float openE)
        {
            // 落下は加速しながら
            float fp = FxMath.Clamp01((t - 0.05f) / (Impact - 0.05f));
            float e = fp * fp;
            float bx = FxMath.Lerp(_s.x, _p.x, e), by = FxMath.Lerp(_s.y, _p.y, e);
            bool before = t < Impact;
            _ball.localPosition = before ? FxMath.V3(bx, by, 0f) : FxMath.V3(99f, 99f, 0f);

            float flick = 0.9f + 0.1f * FxMath.Sin(t * 40f);
            _outer.transform.localScale = FxMath.V3(3.2f * flick, 3.2f * flick, 1f);

            float dx = _s.x - _p.x, dy = _s.y - _p.y;
            float dl = FxMath.Sqrt(dx * dx + dy * dy);
            float ux = dx / dl, uy = dy / dl;
            for (int i = 0; i < _embers.Length; i++)
            {
                float k = FxMath.Repeat(t * 3f + _eOff[i], 1f);
                float back = 0.4f + k * 3.2f;
                float side = FxMath.Sin(i * 2.7f + t * 9f) * 0.25f * k;
                _embers[i].transform.localPosition = FxMath.V3(bx + ux * back - uy * side, by + uy * back + ux * side, 0.05f);
                _embers[i].color = FxMath.Rgba(1f, FxMath.Lerp(0.85f, 0.35f, k), 0.15f, before && t > 0.05f ? (1f - k) * 0.9f : 0f);
            }

            float k0 = t - Impact;
            bool after = k0 >= 0f;

            _flashFull.color = FxMath.Rgba(1f, 0.9f, 0.7f, after ? 0.75f * FxMath.Clamp01(1f - k0 / 0.3f) : 0f);
            if (after && k0 < 0.5f)
            {
                float amp = 0.22f * (1f - k0 / 0.5f);
                c.Jx = FxMath.Sin(t * 53f) * amp;
                c.Jy = FxMath.Cos(t * 41f) * amp;
            }

            _crater.color = FxMath.Rgba(1f, 0.45f, 0.08f, after ? 0.6f * FxMath.Clamp01(1f - k0 / 1f) * fade : 0f);

            for (int i = 0; i < _waves.Length; i++)
            {
                float p = after ? FxMath.Clamp01((k0 - i * 0.07f) / 0.55f) : 0f;
                float s = 0.6f + EaseOut3(p) * (i == 0 ? 10f : 7f);
                _waves[i].transform.localScale = FxMath.V3(s, s * 0.82f, 1f);
                _waves[i].color = FxMath.Rgba(1f, 0.75f, 0.4f, p > 0f ? (1f - p) * 0.85f : 0f);
            }

            for (int i = 0; i < _debris.Length; i++)
            {
                if (!after) continue;
                float k = FxMath.Min(k0, 1.1f);
                Vector2 v = _dv[i];
                float x = _p.x + v.x * k * 0.85f, y = _p.y + v.y * k * 0.85f - 7f * k * k;
                _debris[i].transform.localPosition = FxMath.V3(x, y, -0.04f);
                _debris[i].transform.localRotation = FxMath.RotZ(_dSpin[i] * k);
                float heat = FxMath.Clamp01(1f - k / 0.6f);
                _debris[i].color = FxMath.Rgba(FxMath.Lerp(0.3f, 1f, heat), FxMath.Lerp(0.12f, 0.6f, heat), FxMath.Lerp(0.05f, 0.2f, heat), FxMath.Clamp01(1.2f - k) * fade);
            }

            for (int i = 0; i < _smoke.Length; i++)
            {
                float p = after ? FxMath.Clamp01((k0 - 0.05f - i * 0.04f) / 0.9f) : 0f;
                float pe = 1f - (1f - p) * (1f - p);
                _smoke[i].transform.localPosition = FxMath.V3(_p.x + _sDx[i] * pe, _p.y + 0.2f + pe * (1.2f + i * 0.15f), 0.08f);
                _smoke[i].color = FxMath.Rgba(0.16f, 0.1f, 0.08f, p > 0f ? 0.55f * FxMath.Clamp01(p * 5f) * fade : 0f);
            }

            // 空を見上げて待ち、着弾で左へ逃げる
            float cx = -c.HalfW * 0.55f, hop = 0f;
            if (after)
            {
                if (!_ran)
                {
                    _ran = true;
                    FlipCrew(c, true);
                }

                float r = FxMath.Clamp01((k0 - 0.04f) / 0.4f);
                cx -= 1.2f * r;
                if (r < 1f) hop = FxMath.Abs(FxMath.Sin(k0 * 16f)) * 0.15f;
                else if (!_stopped)
                {
                    _stopped = true;
                    FlipCrew(c, false);
                }
            }

            PlaceCrew(c, cx, -0.75f + hop, -0.05f);

            // 爆心からはじけ出る
            float pop = after ? FxMath.Clamp01(k0 / 0.12f) : 0f;
            float ts = !after ? 0f : pop < 1f ? 0.2f + 1.3f * pop : 1f + 0.5f * FxMath.Clamp01(1f - (k0 - 0.12f) / 0.16f);
            SetText(_title, _p.x, _p.y + 0.2f, -0.1f, ts, after ? fade : 0f);
            SetText(_shadow, _p.x + 0.07f, _p.y + 0.13f, -0.09f, ts, after ? 0.95f * fade : 0f);
            SetText(_head, _p.x, 1.35f, -0.1f, 1f, FxMath.Clamp01((t - 0.08f) / 0.2f) * fade);
            SetText(_by, _p.x + 0.2f, -0.65f, -0.1f, 1f, FxMath.Clamp01((t - 0.75f) / 0.2f) * fade);
        }
    }

    // ---- 自前の光の部品 (1 回だけ作る。幅 = 1 単位) ----
    private static void EnsureLayoutSprites()
    {
        // 柔らかい光の玉
        _soft ??= MakeOwn(64, 64, (x, y) => FxMath.Pow(1f - FxMath.Clamp01(FxMath.Sqrt(x * x + y * y)), 2.2f), 0.5f);
        // 真ん中だけ抜けた闇 (縁はぼかす・四隅は塗り切って周りの板とつなぐ)
        _hole ??= MakeOwn(128, 128, (x, y) =>
        {
            float d = FxMath.Sqrt(x * x + y * y);
            float k = FxMath.Clamp01((d - 0.5f) / 0.5f);
            return k * k * (3f - 2f * k);
        }, 0.5f);
        // 細い光の輪 (芯 + にじみ)
        _thinRing ??= MakeOwn(256, 256, (x, y) =>
        {
            float d = FxMath.Sqrt(x * x + y * y);
            float core = FxMath.Exp(-FxMath.Pow((d - 0.94f) / 0.012f, 2f));
            float halo = FxMath.Exp(-FxMath.Pow((d - 0.94f) / 0.05f, 2f)) * 0.45f;
            return FxMath.Clamp01(core + halo);
        }, 0.5f);
        // 根元が太く明るく先へ細る尾 (pivot は根元)
        _tail ??= MakeOwn(128, 32, (x, y) =>
        {
            float u = (x + 1f) * 0.5f;
            float w = FxMath.Lerp(0.95f, 0.08f, u);
            return FxMath.Clamp01(FxMath.Exp(-FxMath.Pow(y / w, 2f) * 3f) * FxMath.Pow(1f - u, 1.2f));
        }, 0f);
        // 先へ広がる懐中電灯の光 (pivot は根元)
        _cone ??= MakeOwn(128, 64, (x, y) =>
        {
            float u = (x + 1f) * 0.5f;
            float w = 0.1f + 0.9f * u;
            float across = FxMath.Exp(-FxMath.Pow(y / w, 2f) * 2.5f);
            return FxMath.Clamp01(across * (0.45f + 0.55f * u) * FxMath.Clamp01(u / 0.05f) * FxMath.Clamp01((1f - u) / 0.12f));
        }, 0f);
        EnsureMoreSprites();
    }

    private static Sprite MakeOwn(int w, int h, System.Func<float, float, float> alpha, float pivotX)
    {
        var px = new Color32[w * h];
        float hx = (w - 1) * 0.5f, hy = (h - 1) * 0.5f;
        for (int py = 0; py < h; py++)
        for (int pxi = 0; pxi < w; pxi++)
        {
            float a = FxMath.Clamp01(alpha((pxi - hx) / hx, (py - hy) / hy));
            px[py * w + pxi] = FxMath.Rgba32(255, 255, 255, (byte)(a * 255f + 0.5f));
        }

        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        tex.SetPixels32(px);
        tex.Apply(false, true);
        tex.hideFlags = HideFlags.HideAndDontSave;
        Sprite sp = Sprite.Create(tex, new Rect(0f, 0f, w, h), FxMath.V2(pivotX, 0.5f), w, 0, SpriteMeshType.FullRect);
        sp.hideFlags = HideFlags.HideAndDontSave;
        return sp;
    }
}
