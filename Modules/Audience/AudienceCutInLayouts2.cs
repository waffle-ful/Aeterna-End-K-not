using TMPro;
using UnityEngine;

namespace EndKnot.Modules.Audience;

// 残りの種類の組み立て (リアクター = 警報パネル / 通信 = 放送事故 / 大地震 = 地割れ / 天の声 = お告げ /
// 偽死体 = 事件現場 / 入れ替わり = カード反転 / 全員入れ替わり = シャッフル / 祝福 = 天の光)
internal static partial class AudienceCutIn
{
    private static Sprite _hazard, _noise, _card, _spark, _tri, _chalk;

    private static TextMeshPro ByText(Ctx c, Transform parent, float maxSize = 2.4f)
        => Text(parent, "By", c.ByLine, FxMath.Clamp(52f / FxMath.Max(1, c.ByLine.Length), 1.4f, maxSize), FxMath.Rgba(1f, 1f, 1f, 0.92f), c.HalfW * 2f, false, false);

    private static float Ease2(float p)
    {
        float q = 1f - FxMath.Clamp01(p);
        return 1f - q * q;
    }

    // ---- リアクター: 上下に流れる警告テープ、点滅する赤い警告枠と警告標識 ----
    private sealed class ReactorLayout : Layout
    {
        private const float TileW = 2f, PanelW = 7.4f, PanelH = 2.5f, PanelY = 0.3f;
        private Transform _topTape, _bottomTape, _panel;
        private SpriteRenderer[] _frame;
        private Color[] _frameCol;
        private SpriteRenderer _panelBg, _sign;
        private SpriteRenderer[] _beacons;
        private TextMeshPro _bang, _title, _shadow, _head, _by;

        public override void Build(Ctx c)
        {
            c.DimMax = 0.4f;
            c.DimColor = FxMath.Rgba(0.2f, 0f, 0f);
            float tapeH = 0.5f;
            _topTape = Tape(c, "TopTape", c.HalfH - 0.35f, tapeH);
            _bottomTape = Tape(c, "BottomTape", -c.HalfH + 0.35f, tapeH);

            _beacons = new SpriteRenderer[2];
            for (int i = 0; i < 2; i++)
                _beacons[i] = Spr(c.Rt, "Beacon", _soft, FxMath.Rgba(1f, 0.1f, 0.05f, 0f), (i == 0 ? -1f : 1f) * c.HalfW * 0.9f, c.HalfH * 0.75f, 5.5f, 5.5f, 0.2f);

            var go = new GameObject("Panel") { layer = Layer };
            _panel = go.transform;
            _panel.SetParent(c.Rt, false);
            _panel.localPosition = FxMath.V3(0f, PanelY, 0.1f);

            _panelBg = Quad(_panel, "PanelBg", FxMath.Rgba(0.22f, 0.02f, 0.02f, 0.82f), 0f, 0f, PanelW, PanelH, 0.02f);
            _frame = new SpriteRenderer[8];
            _frameCol = new Color[8];
            for (int i = 0; i < 2; i++)
            {
                float w = i == 0 ? 0.12f : 0.035f;
                Color col = i == 0 ? FxMath.Rgba(1f, 0.12f, 0.08f) : FxMath.Rgba(1f, 0.85f, 0.8f);
                float z = i == 0 ? 0f : -0.01f;
                _frame[i * 4 + 0] = Quad(_panel, "Frame", col, 0f, PanelH * 0.5f, PanelW, w, z);
                _frame[i * 4 + 1] = Quad(_panel, "Frame", col, 0f, -PanelH * 0.5f, PanelW, w, z);
                _frame[i * 4 + 2] = Quad(_panel, "Frame", col, -PanelW * 0.5f, 0f, w, PanelH, z);
                _frame[i * 4 + 3] = Quad(_panel, "Frame", col, PanelW * 0.5f, 0f, w, PanelH, z);
                for (int k = 0; k < 4; k++) _frameCol[i * 4 + k] = col;
            }

            _sign = Spr(_panel, "Sign", _tri, FxMath.Rgba(1f, 0.82f, 0.05f), PanelW * 0.5f - 0.95f, 0.05f, 1.25f, 1.25f, -0.02f);
            _bang = Text(_panel, "Bang", "!", 6f, FxMath.Rgba(0.08f, 0.02f, 0f), 1f, false, false);

            float size = TitleSize(c.KindLine, 4.6f);
            _shadow = Text(_panel, "TitleShadow", c.KindLine, size, FxMath.Rgba(0.15f, 0f, 0f, 0.95f), c.HalfW * 2f, false, false);
            _title = Text(_panel, "Title", c.KindLine, size, FxMath.Rgba(1f, 0.95f, 0.9f), c.HalfW * 2f, false, false);
            Outline(c, _title, FxMath.Rgba(0.45f, 0f, 0f));
            _head = Text(c.Rt, "Header", c.Header, 2.2f, FxMath.Rgba(1f, 0.85f, 0.1f), c.HalfW * 2f, false, false);
            _by = ByText(c, c.Rt);
        }

        // 黄色の帯 + 黒の斜め縞のタイル (横に流す)
        private static Transform Tape(Ctx c, string name, float y, float h)
        {
            var go = new GameObject(name) { layer = Layer };
            Transform t = go.transform;
            t.SetParent(c.Rt, false);
            t.localPosition = FxMath.V3(0f, y, -0.15f);
            int n = (int)(c.HalfW * 2f / TileW) + 3;
            float x0 = -c.HalfW - TileW;
            for (int i = 0; i < n; i++)
            {
                Quad(t, "Yellow", FxMath.Rgba(1f, 0.8f, 0.05f), x0 + (i + 0.5f) * TileW, 0f, TileW + 0.01f, h, 0f);
                Spr(t, "Stripes", _hazard, FxMath.Rgba(0.06f, 0.05f, 0.04f), x0 + (i + 0.5f) * TileW, 0f, TileW, TileW, -0.01f);
            }

            Quad(t, "EdgeTop", FxMath.Rgba(0.1f, 0.08f, 0f), 0f, h * 0.5f, c.HalfW * 3f, 0.04f, -0.02f);
            Quad(t, "EdgeBottom", FxMath.Rgba(0.1f, 0.08f, 0f), 0f, -h * 0.5f, c.HalfW * 3f, 0.04f, -0.02f);
            return t;
        }

        public override void Update(Ctx c, float t, float dt, float fade, float openE)
        {
            // テープは画面の外から差し込まれ、流れ続ける
            float slideIn = Ease2(t / 0.2f);
            float shift = FxMath.Repeat(t * 1.6f, TileW);
            _topTape.localPosition = FxMath.V3(shift, c.HalfH - 0.35f + (1f - slideIn) * 0.8f, -0.15f);
            _bottomTape.localPosition = FxMath.V3(-shift, -c.HalfH + 0.35f - (1f - slideIn) * 0.8f, -0.15f);

            float sy = Ease2((t - 0.06f) / 0.16f);
            _panel.localScale = FxMath.V3(1f, sy, 1f);
            _panel.localPosition = FxMath.V3(0f, PanelY, 0.1f);

            // 警報の点滅 (4 回/秒)
            bool on = FxMath.Sin(t * 25f) > -0.2f;
            float fa = (on ? 1f : 0.25f) * fade;
            for (int i = 0; i < _frame.Length; i++)
            {
                Color fc = _frameCol[i];
                _frame[i].color = FxMath.Rgba(fc.r, fc.g, fc.b, fa);
            }

            _panelBg.color = FxMath.Rgba(on ? 0.3f : 0.18f, 0.02f, 0.02f, 0.82f * fade);
            _sign.color = FxMath.Rgba(1f, 0.82f, 0.05f, (on ? 1f : 0.55f) * fade);

            float beat = 0.5f + 0.5f * FxMath.Sin(t * 12f);
            _beacons[0].color = FxMath.Rgba(1f, 0.1f, 0.05f, 0.55f * beat * openE * fade);
            _beacons[1].color = FxMath.Rgba(1f, 0.1f, 0.05f, 0.55f * (1f - beat) * openE * fade);

            // ブザーの細かい震え
            c.Jx = FxMath.Sin(t * 60f) * 0.025f;

            float slide = EaseOut3((t - 0.08f) / 0.2f);
            PlaceCrew(c, FxMath.Lerp(-c.HalfW * 1.2f, -PanelW * 0.5f + 0.95f, slide), PanelY - 0.3f + FxMath.Abs(FxMath.Sin(t * 12f)) * 0.06f, -0.05f);

            float punch = t >= 0.2f ? 1f + 0.35f * (1f - FxMath.Clamp01((t - 0.2f) / 0.12f)) : 0f;
            SetText(_title, 0f, 0.05f, -0.05f, punch, fade);
            SetText(_shadow, 0.06f, -0.01f, -0.04f, punch, 0.95f * fade);
            SetText(_bang, PanelW * 0.5f - 0.95f, -0.05f, -0.05f, 1f, (on ? 1f : 0.55f) * fade);
            SetText(_head, 0f, PanelY + PanelH * 0.5f + 0.32f, -0.05f, 1f, FxMath.Clamp01((t - 0.12f) / 0.15f) * fade);
            SetText(_by, 0f, PanelY - PanelH * 0.5f - 0.35f, -0.05f, 1f, FxMath.Clamp01((t - 0.4f) / 0.2f) * fade);
        }
    }

    // ---- 通信妨害: 放送事故。カラーバー → 縦に流れる画面 → 走査線と砂嵐の中で色ずれした文字 ----
    private sealed class CommsLayout : Layout
    {
        private static readonly float[] BarR = { 0.75f, 0.75f, 0f, 0f, 0.75f, 0.75f, 0f };
        private static readonly float[] BarG = { 0.75f, 0.75f, 0.75f, 0.75f, 0f, 0f, 0f };
        private static readonly float[] BarB = { 0.75f, 0f, 0.75f, 0f, 0.75f, 0f, 0.75f };
        private Transform _content;
        private SpriteRenderer[] _bars, _glitch;
        private SpriteRenderer _static;
        private Transform _scan;
        private TextMeshPro _title, _red, _cyan, _head, _by;
        private float _seed;

        public override void Build(Ctx c)
        {
            c.DimMax = 0.5f;
            c.DimColor = FxMath.Rgba(0f, 0.04f, 0.05f);
            c.FlashMax = 0f;
            _seed = FxMath.Range(0f, 100f);

            _static = Spr(c.Rt, "Static", _noise, FxMath.Rgba(1f, 1f, 1f, 0f), 0f, 0f, c.HalfW * 2.4f, c.HalfW * 2.4f, 0.2f);

            var sg = new GameObject("Scan") { layer = Layer };
            _scan = sg.transform;
            _scan.SetParent(c.Rt, false);
            int lines = (int)(c.HalfH * 2f / 0.14f) + 2;
            for (int i = 0; i < lines; i++)
                Quad(_scan, "ScanLine", FxMath.Rgba(0f, 0f, 0f, 0.28f), 0f, -c.HalfH - 0.14f + i * 0.14f, c.HalfW * 2.2f, 0.035f, 0f);

            var cg = new GameObject("Content") { layer = Layer };
            _content = cg.transform;
            _content.SetParent(c.Rt, false);

            float size = TitleSize(c.KindLine, 6.5f);
            _red = Text(_content, "TitleRed", c.KindLine, size, FxMath.Rgba(1f, 0.1f, 0.25f, 0.8f), c.HalfW * 2f, false, false);
            _cyan = Text(_content, "TitleCyan", c.KindLine, size, FxMath.Rgba(0f, 0.95f, 1f, 0.8f), c.HalfW * 2f, false, false);
            _title = Text(_content, "Title", c.KindLine, size, FxMath.Rgba(1f, 1f, 1f), c.HalfW * 2f, false, false);
            Outline(c, _title, FxMath.Rgba(0f, 0.12f, 0.14f));
            _head = Text(_content, "Header", c.Header, 2.2f, c.Light, c.HalfW * 2f, false, false);
            _by = ByText(c, _content);
            if (c.HasCrew) c.CrewT.SetParent(_content, false);

            _glitch = new SpriteRenderer[6];
            for (int i = 0; i < _glitch.Length; i++)
                _glitch[i] = Quad(c.Rt, "Glitch", FxMath.Rgba(1f, 1f, 1f, 0f), 0f, 0f, c.HalfW * 2.2f, 0.1f, -0.2f);

            _bars = new SpriteRenderer[7];
            float bw = c.HalfW * 2.2f / 7f;
            for (int i = 0; i < 7; i++)
                _bars[i] = Quad(c.Rt, "Bar", FxMath.Rgba(BarR[i], BarG[i], BarB[i], 0f), -c.HalfW * 1.1f + (i + 0.5f) * bw, 0f, bw + 0.01f, c.HalfH * 2.2f, -0.25f);
        }

        public override void Update(Ctx c, float t, float dt, float fade, float openE)
        {
            float frame = FxMath.Floor(t * 15f);

            // カラーバーは冒頭と途中に一瞬だけ
            bool bars = t < 0.12f || (t > 0.92f && t < 0.97f);
            for (int i = 0; i < 7; i++) _bars[i].color = FxMath.Rgba(BarR[i], BarG[i], BarB[i], bars ? 0.92f : 0f);

            float noiseA = t < 0.4f ? 0.35f : 0.13f;
            _static.color = FxMath.Rgba(0.85f, 1f, 1f, noiseA * fade);
            float flipX = Hash(frame + _seed) < 0.5f ? -1f : 1f, flipY = Hash(frame + _seed + 1.3f) < 0.5f ? -1f : 1f;
            _static.transform.localScale = FxMath.V3(c.HalfW * 2.4f * flipX, c.HalfW * 2.4f * flipY, 1f);
            _static.transform.localPosition = FxMath.V3((Hash(frame + 2.1f) - 0.5f) * 2f, (Hash(frame + 4.7f) - 0.5f) * 2f, 0.2f);

            _scan.localPosition = FxMath.V3(0f, FxMath.Repeat(t * 0.6f, 0.14f), -0.18f);

            // 縦同期が外れて画面が流れ、t = 0.35 でぴたりと止まる
            float roll = t < 0.35f ? FxMath.Repeat(t * 11f, 1f) * c.HalfH * 2f - c.HalfH : 0f;
            float jx = t < 1.2f && Hash(frame + _seed + 7f) < 0.35f ? (Hash(frame + _seed + 3.1f) - 0.5f) * 0.8f : 0f;
            _content.localPosition = FxMath.V3(jx, roll, -0.05f);

            for (int i = 0; i < _glitch.Length; i++)
            {
                SpriteRenderer g = _glitch[i];
                float y = (Hash(frame + i * 7.3f + _seed) - 0.5f) * c.HalfH * 2f;
                float h = 0.04f + Hash(frame + i + 0.5f + _seed) * 0.25f;
                g.transform.localPosition = FxMath.V3((Hash(frame + i + 9.7f) - 0.5f) * 0.8f, y, -0.2f);
                g.transform.localScale = FxMath.V3(c.HalfW * 2.2f, h, 1f);
                g.color = i % 2 == 0 ? FxMath.Rgba(1f, 1f, 1f, 0.12f * fade) : FxMath.Rgba(c.Main.r, c.Main.g, c.Main.b, 0.25f * fade);
            }

            PlaceCrew(c, -c.HalfW * 0.55f, -0.4f, 0f);

            float split = 0.07f + (Hash(frame + _seed + 5f) < 0.3f ? Hash(frame + 8.8f) * 0.35f : 0f);
            float ta = t >= 0.12f ? fade : 0f;
            SetText(_title, 0.9f, 0.2f, -0.1f, 1f, ta);
            SetText(_red, 0.9f - split, 0.2f, -0.08f, 1f, 0.8f * ta);
            SetText(_cyan, 0.9f + split, 0.2f, -0.09f, 1f, 0.8f * ta);
            SetText(_head, 0.9f, 1.05f, -0.1f, 1f, ta);
            SetText(_by, 1.1f, -0.6f, -0.1f, 1f, FxMath.Clamp01((t - 0.45f) / 0.2f) * fade);
        }
    }

    // ---- 大地震: 画面を横切る地割れ。技名は下からせり上がる岩の板に刻まれる ----
    private sealed class EarthquakeLayout : Layout
    {
        private const int CrackPts = 16, DustCount = 8, PebbleCount = 10;
        private const float CrackY = -1.0f, SlabY = 0.75f;
        private Vector2[] _pts;
        private SpriteRenderer[] _segUnder, _segCore;
        private float[] _segDist;
        private Transform _slab;
        private SpriteRenderer[] _dust, _pebbles;
        private float[] _dustX, _pebX, _pebDelay, _pebSpin;
        private TextMeshPro _title, _shadow, _head, _by;

        public override void Build(Ctx c)
        {
            c.DimMax = 0.38f;
            c.DimColor = FxMath.Rgba(0.08f, 0.05f, 0.02f);

            _pts = new Vector2[CrackPts];
            float x0 = -c.HalfW - 0.4f, x1 = c.HalfW + 0.4f;
            float py = CrackY;
            for (int i = 0; i < CrackPts; i++)
            {
                float x = FxMath.Lerp(x0, x1, i / (float)(CrackPts - 1)) + (i > 0 && i < CrackPts - 1 ? FxMath.Range(-0.15f, 0.15f) : 0f);
                // 折れ方を不揃いに (中心線から離れすぎたら戻す)
                py = FxMath.Clamp(py + FxMath.Range(-0.42f, 0.42f) + (CrackY - py) * 0.35f, CrackY - 0.45f, CrackY + 0.45f);
                _pts[i] = FxMath.V2(x, py);
            }

            _segUnder = new SpriteRenderer[CrackPts - 1];
            _segCore = new SpriteRenderer[CrackPts - 1];
            _segDist = new float[CrackPts - 1];
            for (int i = 0; i < CrackPts - 1; i++)
            {
                Vector2 a = _pts[i], b = _pts[i + 1];
                float dx = b.x - a.x, dy = b.y - a.y;
                float len = FxMath.Sqrt(dx * dx + dy * dy) + 0.06f;
                float ang = FxMath.Atan2(dy, dx) * FxMath.Rad2Deg;
                float mx = (a.x + b.x) * 0.5f, my = (a.y + b.y) * 0.5f;
                _segDist[i] = FxMath.Abs(mx);
                _segUnder[i] = Quad(c.Rt, "Crack", FxMath.Rgba(0.04f, 0.02f, 0.01f, 0f), mx, my, len, 0.24f, 0.05f);
                _segCore[i] = Quad(c.Rt, "Magma", FxMath.Rgba(1f, 0.5f, 0.12f, 0f), mx, my, len, 0.06f, 0.04f);
                _segUnder[i].transform.localRotation = FxMath.RotZ(ang);
                _segCore[i].transform.localRotation = FxMath.RotZ(ang);
            }

            var go = new GameObject("Slab") { layer = Layer };
            _slab = go.transform;
            _slab.SetParent(c.Rt, false);
            _slab.localPosition = FxMath.V3(0.6f, -c.HalfH - 1.5f, 0.1f);
            const float sw = 7f, sh = 1.75f;
            Quad(_slab, "Shade", FxMath.Rgba(0.16f, 0.11f, 0.07f, 0.98f), 0.06f, -0.08f, sw, sh, 0.01f);
            Quad(_slab, "Rock", FxMath.Rgba(0.42f, 0.31f, 0.2f, 0.98f), 0f, 0f, sw, sh, 0f);
            Quad(_slab, "Top", FxMath.Rgba(0.62f, 0.5f, 0.36f, 1f), 0f, sh * 0.5f - 0.05f, sw, 0.1f, -0.01f);
            Quad(_slab, "Bottom", FxMath.Rgba(0.22f, 0.15f, 0.09f, 1f), 0f, -sh * 0.5f + 0.06f, sw, 0.12f, -0.01f);
            for (int i = 0; i < 7; i++)
            {
                float s = FxMath.Range(0.6f, 1.3f);
                Spr(_slab, "Blot", _soft, FxMath.Rgba(0.2f, 0.13f, 0.07f, 0.45f), FxMath.Range(-sw * 0.45f, sw * 0.45f), FxMath.Range(-sh * 0.35f, sh * 0.35f), s * 1.6f, s, -0.005f);
            }

            float size = TitleSize(c.KindLine, 6.2f);
            _shadow = Text(_slab, "TitleShadow", c.KindLine, size, FxMath.Rgba(0.12f, 0.07f, 0.03f, 0.95f), sw, false, false);
            _title = Text(_slab, "Title", c.KindLine, size, FxMath.Rgba(1f, 0.9f, 0.7f), sw, false, false);
            Outline(c, _title, FxMath.Rgba(0.2f, 0.1f, 0.02f));
            _head = Text(_slab, "Header", c.Header, 2.1f, FxMath.Rgba(1f, 0.8f, 0.45f), sw, false, false);
            _by = ByText(c, c.Rt);

            _dust = new SpriteRenderer[DustCount];
            _dustX = new float[DustCount];
            for (int i = 0; i < DustCount; i++)
            {
                _dustX[i] = FxMath.Range(-c.HalfW, c.HalfW);
                float s = FxMath.Range(1.4f, 2.4f);
                _dust[i] = Spr(c.Rt, "Dust", _soft, FxMath.Rgba(0.45f, 0.35f, 0.24f, 0f), _dustX[i], CrackY, s * 1.4f, s, 0.03f);
            }

            _pebbles = new SpriteRenderer[PebbleCount];
            _pebX = new float[PebbleCount];
            _pebDelay = new float[PebbleCount];
            _pebSpin = new float[PebbleCount];
            for (int i = 0; i < PebbleCount; i++)
            {
                _pebX[i] = FxMath.Range(-c.HalfW, c.HalfW);
                _pebDelay[i] = FxMath.Range(0.15f, 0.9f);
                _pebSpin[i] = FxMath.Range(-400f, 400f);
                float s = FxMath.Range(0.08f, 0.18f);
                _pebbles[i] = Quad(c.Rt, "Pebble", FxMath.Rgba(0.3f, 0.22f, 0.15f, 0f), _pebX[i], c.HalfH + 0.5f, s, s * 0.8f, -0.02f);
            }
        }

        public override void Update(Ctx c, float t, float dt, float fade, float openE)
        {
            float amp = 0.2f * FxMath.Clamp01(1f - t / 1.3f);
            c.Jx = FxMath.Sin(t * 47f) * amp;
            c.Jy = FxMath.Cos(t * 39f) * amp;

            // ひびは真ん中から両端へ走る
            float reach = Ease2((t - 0.1f) / 0.3f) * (c.HalfW + 0.6f);
            for (int i = 0; i < _segUnder.Length; i++)
            {
                bool shown = _segDist[i] <= reach && t >= 0.1f;
                _segUnder[i].color = FxMath.Rgba(0.04f, 0.02f, 0.01f, shown ? 0.9f * fade : 0f);
                float glow = 0.75f + 0.25f * FxMath.Sin(t * 9f + i);
                _segCore[i].color = FxMath.Rgba(1f, 0.5f, 0.12f, shown ? glow * fade : 0f);
            }

            for (int i = 0; i < _dust.Length; i++)
            {
                float p = FxMath.Clamp01((t - 0.2f - i * 0.03f) / 1f);
                float pe = Ease2(p);
                _dust[i].transform.localPosition = FxMath.V3(_dustX[i], CrackY + pe * 1.4f, 0.03f);
                _dust[i].color = FxMath.Rgba(0.45f, 0.35f, 0.24f, p > 0f ? 0.5f * FxMath.Clamp01(p * 5f) * fade : 0f);
            }

            for (int i = 0; i < _pebbles.Length; i++)
            {
                float k = t - _pebDelay[i];
                if (k < 0f) continue;
                _pebbles[i].transform.localPosition = FxMath.V3(_pebX[i], c.HalfH + 0.3f - 9f * k * k, -0.02f);
                _pebbles[i].transform.localRotation = FxMath.RotZ(_pebSpin[i] * k);
                _pebbles[i].color = FxMath.Rgba(0.3f, 0.22f, 0.15f, fade);
            }

            // 岩の板が下からせり上がり、止まった所でがくんと沈む
            float rise = EaseOut3((t - 0.05f) / 0.3f);
            float settle = t > 0.35f ? -0.08f * FxMath.Sin((t - 0.35f) * 25f) * FxMath.Exp(-(t - 0.35f) * 8f) : 0f;
            _slab.localPosition = FxMath.V3(0.6f, FxMath.Lerp(-c.HalfH - 1.5f, SlabY, rise) + settle, 0.1f);

            // 揺れによろける
            float wob = FxMath.Sin(t * 13f) * 12f * FxMath.Clamp01(1f - t / 1.3f);
            PlaceCrew(c, -c.HalfW * 0.68f, -1.7f + FxMath.Abs(FxMath.Sin(t * 10f)) * 0.08f, -0.05f, wob);

            SetText(_title, 0f, -0.05f, -0.05f, 1f, fade);
            SetText(_shadow, 0.06f, -0.11f, -0.04f, 1f, 0.95f * fade);
            SetText(_head, 0f, 0.62f, -0.05f, 1f, fade);
            SetText(_by, 0.9f, -1.75f, -0.1f, 1f, FxMath.Clamp01((t - 0.45f) / 0.2f) * fade);
        }
    }

    // ---- 天の声: 上の雲にクルーが乗り、本文を真ん中へ 1 文字ずつ打ち出す ----
    private sealed class VoiceLayout : Layout
    {
        private const int CloudCount = 8;
        private const float CloudY = 1.0f, MsgY = -0.9f;
        private SpriteRenderer[] _clouds, _cloudShade, _beams;
        private float[] _cloudX;
        private SpriteRenderer[] _rings;
        private TextMeshPro _title, _head, _msg, _msgShadow, _by;
        private int _msgLen;

        public override void Build(Ctx c)
        {
            c.Duration = 3.5f;
            c.DimMax = 0.3f;
            c.DimColor = FxMath.Rgba(0.08f, 0.06f, 0f);

            _beams = new SpriteRenderer[3];
            for (int i = 0; i < 3; i++)
            {
                _beams[i] = Spr(c.Rt, "Beam", _tail, FxMath.Rgba(1f, 0.9f, 0.55f, 0f), (i - 1) * 1.1f, CloudY, c.HalfH * 2f, 7f, 0.2f);
                _beams[i].transform.localRotation = FxMath.RotZ(-90f + (i - 1) * 14f);
            }

            _clouds = new SpriteRenderer[CloudCount];
            _cloudShade = new SpriteRenderer[CloudCount];
            _cloudX = new float[CloudCount];
            for (int i = 0; i < CloudCount; i++)
            {
                _cloudX[i] = FxMath.Lerp(-2.6f, 2.6f, i / (float)(CloudCount - 1)) + FxMath.Range(-0.2f, 0.2f);
                float s = FxMath.Range(1.5f, 2.3f) * (1f - FxMath.Abs(_cloudX[i]) / 6f);
                // 明るい床でも雲の形が見えるよう、下に影を敷く
                _cloudShade[i] = Spr(c.Rt, "CloudShade", _soft, FxMath.Rgba(0.45f, 0.36f, 0.2f, 0f), _cloudX[i], CloudY - 0.18f, s * 1.6f, s * 0.9f, -0.07f - i * 0.001f);
                _clouds[i] = Spr(c.Rt, "Cloud", _soft, FxMath.Rgba(1f, 0.95f, 0.82f, 0f), _cloudX[i], CloudY, s * 1.5f, s, -0.08f - i * 0.001f);
            }

            _rings = new SpriteRenderer[3];
            for (int i = 0; i < 3; i++)
                _rings[i] = Spr(c.Rt, "Ring", _thinRing, FxMath.Rgba(1f, 0.9f, 0.55f, 0f), 0f, MsgY, 1f, 1f, 0.15f);

            _title = Text(c.Rt, "Title", c.KindLine, 3.6f, FxMath.Rgba(1f, 0.92f, 0.6f), c.HalfW * 2f, false, false);
            Outline(c, _title, FxMath.Rgba(0.35f, 0.22f, 0f));
            _head = Text(c.Rt, "Header", c.Header, 2f, c.Light, c.HalfW * 2f, true, false);
            _msgShadow = Text(c.Rt, "MessageShadow", c.Message, 3.4f, FxMath.Rgba(0f, 0f, 0f, 0.9f), c.HalfW * 1.7f, false, true);
            _msg = Text(c.Rt, "Message", c.Message, 3.4f, FxMath.Rgba(1f, 0.97f, 0.85f), c.HalfW * 1.7f, false, true);
            _by = ByText(c, c.Rt, 2.2f);
            _msgLen = c.Message.Length;
        }

        public override void Update(Ctx c, float t, float dt, float fade, float openE)
        {
            float rise = EaseOut3(t / 0.5f);

            for (int i = 0; i < _clouds.Length; i++)
            {
                float x = _cloudX[i] + FxMath.Sin(t * 0.9f + i * 1.3f) * 0.08f;
                _clouds[i].transform.localPosition = FxMath.V3(x, CloudY + (1f - rise) * 2.2f + FxMath.Sin(t * 1.4f + i) * 0.04f, -0.08f - i * 0.001f);
                _clouds[i].color = FxMath.Rgba(1f, 0.95f, 0.82f, rise * fade);
                _cloudShade[i].transform.localPosition = FxMath.V3(x, CloudY - 0.18f + (1f - rise) * 2.2f, -0.07f - i * 0.001f);
                _cloudShade[i].color = FxMath.Rgba(0.45f, 0.36f, 0.2f, 0.45f * rise * fade);
            }

            for (int i = 0; i < _beams.Length; i++)
            {
                _beams[i].transform.localRotation = FxMath.RotZ(-90f + (i - 1) * 14f + FxMath.Sin(t * 0.8f + i * 1.7f) * 3f);
                _beams[i].color = FxMath.Rgba(1f, 0.9f, 0.55f, (0.2f + 0.05f * FxMath.Sin(t * 2f + i)) * FxMath.Clamp01((t - 0.3f) / 0.4f) * fade);
            }

            for (int i = 0; i < _rings.Length; i++)
            {
                float p = FxMath.Repeat(t * 0.45f + i / 3f, 1f);
                float s = 1.5f + p * 8f;
                _rings[i].transform.localScale = FxMath.V3(s, s * 0.45f, 1f);
                _rings[i].color = FxMath.Rgba(1f, 0.9f, 0.55f, t > 0.5f ? (1f - p) * 0.35f * fade : 0f);
            }

            // 雲に腰掛けて上下にふわふわ
            PlaceCrew(c, 0f, CloudY + 0.12f + (1f - rise) * 2.2f + FxMath.Sin(t * 2.2f) * 0.06f, -0.05f);

            float ta = FxMath.Clamp01((t - 0.25f) / 0.25f) * fade;
            SetText(_head, 0f, 0.3f, -0.1f, 1f, ta);
            SetText(_title, 0f, -0.12f, -0.1f, 1f, ta);

            // 1 文字ずつ打ち出す (14 文字/秒)
            if (_msg is not null)
            {
                int shown = FxMath.Clamp((int)((t - 0.55f) * 14f), 0, _msgLen);
                _msg.maxVisibleCharacters = shown;
                if (_msgShadow is not null) _msgShadow.maxVisibleCharacters = shown;
            }

            float ma = t >= 0.55f ? fade : 0f;
            SetText(_msg, 0f, MsgY, -0.1f, 1f, ma);
            SetText(_msgShadow, 0.05f, MsgY - 0.05f, -0.08f, 1f, 0.9f * ma);
            SetText(_by, 0f, -c.HalfH + 0.55f, -0.1f, 1f, FxMath.Clamp01((t - 0.8f) / 0.3f) * fade);
        }
    }

    // ---- 偽死体: 事件現場。規制線テープが 2 本交差して貼られ、床にチョークの人型。写真のフラッシュ 2 回 ----
    private sealed class FakeBodyLayout : Layout
    {
        private Transform[] _tapes;
        private TextMeshPro[] _tapeText;
        private float[] _tapeRot, _tapeY;
        private SpriteRenderer _chalk, _slash, _photo;
        private TextMeshPro _title, _shadow, _head, _by;

        public override void Build(Ctx c)
        {
            c.DimMax = 0.42f;
            c.DimColor = FxMath.Rgba(0.02f, 0.03f, 0.06f);
            c.FlashMax = 0.3f;

            _chalk = Spr(c.Rt, "Chalk", _chalk_(), FxMath.Rgba(0.95f, 0.95f, 0.92f, 0f), 2.3f, -1.45f, 3.3f, 3.3f, 0.15f);
            _chalk.transform.localRotation = FxMath.RotZ(78f);

            string keep = Translator.GetString("Audience.Cutscene.KeepOut");
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < 9; i++) sb.Append(keep).Append("   ");
            string row = sb.ToString();

            _tapes = new Transform[2];
            _tapeText = new TextMeshPro[2];
            _tapeRot = new[] { 11f, -9f };
            _tapeY = new[] { 1.7f, -2.35f };
            for (int i = 0; i < 2; i++)
            {
                var go = new GameObject("Tape") { layer = Layer };
                Transform tt = go.transform;
                tt.SetParent(c.Rt, false);
                tt.localRotation = FxMath.RotZ(_tapeRot[i]);
                float w = c.HalfW * 3.2f;
                Quad(tt, "Shadow", FxMath.Rgba(0f, 0f, 0f, 0.4f), 0.05f, -0.07f, w, 0.5f, 0.02f);
                Quad(tt, "Yellow", FxMath.Rgba(1f, 0.84f, 0.05f), 0f, 0f, w, 0.46f, 0f);
                Quad(tt, "EdgeTop", FxMath.Rgba(0.12f, 0.1f, 0f), 0f, 0.2f, w, 0.035f, -0.01f);
                Quad(tt, "EdgeBottom", FxMath.Rgba(0.12f, 0.1f, 0f), 0f, -0.2f, w, 0.035f, -0.01f);
                _tapeText[i] = Text(tt, "TapeText", row, 2.2f, FxMath.Rgba(0.06f, 0.05f, 0f), w * 1.2f, false, false);
                _tapes[i] = tt;
            }

            float size = TitleSize(c.KindLine, 6.5f);
            _shadow = Text(c.Rt, "TitleShadow", c.KindLine, size, FxMath.Rgba(0f, 0f, 0f, 0.95f), c.HalfW * 2f, false, false);
            _title = Text(c.Rt, "Title", c.KindLine, size, FxMath.Rgba(1f, 1f, 1f), c.HalfW * 2f, false, false);
            Outline(c, _title, FxMath.Rgba(0.1f, 0.12f, 0.15f));
            _head = Text(c.Rt, "Header", c.Header, 2.2f, c.Light, c.HalfW * 2f, true, false);
            _by = ByText(c, c.Rt);
            _slash = Quad(c.Rt, "Slash", FxMath.Rgba(0.85f, 0.05f, 0.05f, 0f), 0.6f, 0.38f, 0f, 0.09f, -0.12f);
            _slash.transform.localRotation = FxMath.RotZ(-4f);

            _photo = Quad(c.Rt, "Photo", FxMath.Rgba(1f, 1f, 1f, 0f), 0f, 0f, c.HalfW * 2.2f, c.HalfH * 2.2f, -0.28f);
            FlipCrew(c, false);
        }

        private static Sprite _chalk_() => AudienceCutIn._chalk;

        public override void Update(Ctx c, float t, float dt, float fade, float openE)
        {
            for (int i = 0; i < 2; i++)
            {
                float p = EaseOut3((t - 0.04f - i * 0.1f) / 0.18f);
                float dir = i == 0 ? -1f : 1f;
                float x = FxMath.Lerp(dir * c.HalfW * 3.2f, 0f, p);
                _tapes[i].localPosition = FxMath.V3(x, _tapeY[i], i == 0 ? -0.16f : -0.17f);
                // テープの文字はゆっくり流れる
                SetText(_tapeText[i], FxMath.Repeat(t * 0.5f * dir, 1.5f), 0f, -0.02f, 1f, fade);
            }

            float ca = FxMath.Clamp01((t - 0.2f) / 0.35f);
            _chalk.color = FxMath.Rgba(0.95f, 0.95f, 0.92f, 0.85f * ca * fade);

            // 現場写真のフラッシュ
            float ph = 0f;
            if (t >= 0.5f && t < 0.62f) ph = 1f - (t - 0.5f) / 0.12f;
            else if (t >= 0.74f && t < 0.86f) ph = 1f - (t - 0.74f) / 0.12f;
            _photo.color = FxMath.Rgba(1f, 1f, 1f, 0.5f * ph);

            float slide = EaseOut3((t - 0.06f) / 0.2f);
            float hop = t >= 0.5f && t < 0.8f ? FxMath.Abs(FxMath.Sin((t - 0.5f) * 10.5f)) * 0.18f * (1f - (t - 0.5f) / 0.3f) : 0f;
            PlaceCrew(c, FxMath.Lerp(-c.HalfW * 1.3f, -c.HalfW * 0.6f, slide), -0.45f + hop, -0.05f);

            float ta = FxMath.Clamp01((t - 0.12f) / 0.12f) * fade;
            float punch = t >= 0.12f ? 1f + 0.3f * (1f - FxMath.Clamp01((t - 0.12f) / 0.14f)) : 1.3f;
            SetText(_title, 0.6f, 0.35f, -0.1f, punch, ta);
            SetText(_shadow, 0.67f, 0.28f, -0.09f, punch, 0.95f * ta);
            SetText(_head, 0.6f, 1.1f, -0.1f, 1f, ta);
            SetText(_by, 0.8f, -0.45f, -0.1f, 1f, FxMath.Clamp01((t - 0.45f) / 0.2f) * fade);

            float sp = EaseOut3((t - 0.3f) / 0.16f);
            _slash.transform.localScale = FxMath.V3(c.HalfW * 1.25f * sp, 0.09f, 1f);
            _slash.color = FxMath.Rgba(0.85f, 0.05f, 0.05f, t >= 0.3f ? 0.95f * fade : 0f);
        }
    }

    // ---- カード 1 枚 (縁 + 地 + 面) ----
    private static Transform Card(Transform parent, string name, Color edge, Color face, Color inner, float scale, float z)
    {
        var go = new GameObject(name) { layer = Layer };
        Transform t = go.transform;
        t.SetParent(parent, false);
        t.localPosition = FxMath.V3(0f, 0f, z);
        Spr(t, "Edge", _card, edge, 0f, 0f, scale, scale, 0f);
        Spr(t, "Face", _card, face, 0f, 0f, scale * 0.93f, scale * 0.93f, -0.01f);
        Spr(t, "Inner", _card, inner, 0f, 0f, scale * 0.78f, scale * 0.78f, -0.02f);
        return t;
    }

    private static void Sparkles(Transform parent, SpriteRenderer[] arr, float[] x, float[] y, float rangeX, float rangeY, float cy)
    {
        for (int i = 0; i < arr.Length; i++)
        {
            x[i] = FxMath.Range(-rangeX, rangeX);
            y[i] = cy + FxMath.Range(-rangeY, rangeY);
            float s = FxMath.Range(0.4f, 0.9f);
            arr[i] = Spr(parent, "Spark", _spark, FxMath.Rgba(1f, 1f, 1f, 0f), x[i], y[i], s, s, -0.2f);
        }
    }

    private static void UpdateSparkles(SpriteRenderer[] arr, Color l, float t, float fade, float seed)
    {
        for (int i = 0; i < arr.Length; i++)
        {
            float tw = FxMath.Sin(t * 6f + i * 1.9f + seed);
            arr[i].color = FxMath.Rgba(FxMath.Lerp(l.r, 1f, 0.5f), FxMath.Lerp(l.g, 1f, 0.5f), FxMath.Lerp(l.b, 1f, 0.5f), FxMath.Clamp01(tw) * 0.9f * fade);
        }
    }

    // ---- 入れ替わり: クルーのカードと「？」のカードが回りながら場所を入れ替える ----
    private sealed class ShuffleOneLayout : Layout
    {
        private const float CardScale = 1.9f, Cy = 0.15f, Sx = 1.7f;
        private Transform _a, _b;
        private TextMeshPro _q, _title, _shadow, _head, _by;
        private SpriteRenderer[] _sparks;
        private float[] _sx, _sy;
        private float _seed;

        public override void Build(Ctx c)
        {
            c.DimMax = 0.4f;
            c.DimColor = FxMath.Rgba(0f, 0.06f, 0.05f);
            _seed = FxMath.Range(0f, 100f);
            Color m = c.Main, l = c.Light;

            _a = Card(c.Rt, "CardA", l, FxMath.Rgba(m.r * 0.3f, m.g * 0.3f, m.b * 0.3f), FxMath.Rgba(m.r, m.g, m.b, 0.35f), CardScale, 0.1f);
            _b = Card(c.Rt, "CardB", FxMath.Rgba(1f, 1f, 1f), FxMath.Rgba(0.1f, 0.12f, 0.16f), FxMath.Rgba(l.r, l.g, l.b, 0.25f), CardScale, 0.12f);
            if (c.HasCrew)
            {
                c.CrewT.SetParent(_a, false);
                c.CrewT.localScale = FxMath.V3(0.85f, 0.85f, 1f);
            }

            _q = Text(_b, "Question", "?", 12f, FxMath.Rgba(1f, 1f, 1f), 2f, false, false);
            Outline(c, _q);

            _sparks = new SpriteRenderer[10];
            _sx = new float[10];
            _sy = new float[10];
            Sparkles(c.Rt, _sparks, _sx, _sy, c.HalfW * 0.7f, 1.6f, Cy);

            float size = TitleSize(c.KindLine, 5.6f);
            _shadow = Text(c.Rt, "TitleShadow", c.KindLine, size, FxMath.Rgba(0f, 0.1f, 0.08f, 0.95f), c.HalfW * 2f, true, false);
            _title = Text(c.Rt, "Title", c.KindLine, size, FxMath.Rgba(1f, 1f, 1f), c.HalfW * 2f, true, false);
            Outline(c, _title);
            _head = Text(c.Rt, "Header", c.Header, 2.2f, l, c.HalfW * 2f, true, false);
            _by = ByText(c, c.Rt, 2.2f);
        }

        public override void Update(Ctx c, float t, float dt, float fade, float openE)
        {
            float appear = EaseOut3(t / 0.18f);
            // 0.25〜0.85 秒で弧を描いて入れ替わり、その間に 1 回転
            float p = FxMath.Clamp01((t - 0.25f) / 0.6f);
            float e = p * p * (3f - 2f * p);
            float ang = e * FxMath.PI;
            float ax = -Sx * FxMath.Cos(ang), ay = Cy + FxMath.Sin(ang) * 0.9f;
            float bx = Sx * FxMath.Cos(ang), by = Cy - FxMath.Sin(ang) * 0.9f;
            float flip = FxMath.Cos(e * FxMath.PI * 2f);
            float sx = (0.08f + 0.92f * FxMath.Abs(flip)) * appear;
            _a.localPosition = FxMath.V3(ax, ay, 0.1f);
            _b.localPosition = FxMath.V3(bx, by, 0.12f);
            _a.localScale = FxMath.V3(sx, appear, 1f);
            _b.localScale = FxMath.V3(sx, appear, 1f);
            _a.localRotation = FxMath.RotZ(FxMath.Sin(ang) * 8f);
            _b.localRotation = FxMath.RotZ(-FxMath.Sin(ang) * 8f);

            PlaceCrew(c, 0f, -0.15f, -0.05f);
            SetText(_q, 0f, 0f, -0.05f, 1f, fade);

            UpdateSparkles(_sparks, c.Light, t, fade, _seed);

            float ta = FxMath.Clamp01((t - 0.8f) / 0.15f) * fade;
            float punch = t >= 0.8f ? 1f + 0.35f * (1f - FxMath.Clamp01((t - 0.8f) / 0.14f)) : 1.35f;
            SetText(_title, 0f, -1.85f, -0.1f, punch, ta);
            SetText(_shadow, 0.06f, -1.91f, -0.09f, punch, 0.95f * ta);
            SetText(_head, 0f, Cy + 1.75f, -0.1f, 1f, FxMath.Clamp01((t - 0.1f) / 0.2f) * fade);
            SetText(_by, 0f, -2.5f, -0.1f, 1f, FxMath.Clamp01((t - 0.95f) / 0.2f) * fade);
        }
    }

    // ---- 全員入れ替わり: クルーの色のカードを切り混ぜ、最後に扇形に開く ----
    private sealed class ShuffleAllLayout : Layout
    {
        private const int N = 6;
        private const float CardScale = 1.25f, Cy = 0.35f;
        private Transform[] _cards;
        private float[] _phase;
        private TextMeshPro _title, _shadow, _head, _by;
        private SpriteRenderer[] _sparks;
        private float[] _sx, _sy;
        private float _seed;

        public override void Build(Ctx c)
        {
            c.DimMax = 0.4f;
            c.DimColor = FxMath.Rgba(0.06f, 0f, 0.05f);
            _seed = FxMath.Range(0f, 100f);

            _cards = new Transform[N];
            _phase = new float[N];
            for (int i = 0; i < N; i++)
            {
                Color pc = FxMath.Rgba(0.6f, 0.6f, 0.6f);
                try
                {
                    if (Palette.PlayerColors != null && Palette.PlayerColors.Length > 0) pc = Palette.PlayerColors[(i * 3 + 1) % Palette.PlayerColors.Length];
                }
                catch (System.Exception e) { Utils.ThrowException(e); }

                _cards[i] = Card(c.Rt, "Card", FxMath.Rgba(1f, 1f, 1f), FxMath.Rgba(pc.r * 0.55f, pc.g * 0.55f, pc.b * 0.55f), FxMath.Rgba(pc.r, pc.g, pc.b, 0.9f), CardScale, 0.1f + i * 0.01f);
                // 裏面の菱形
                SpriteRenderer d = Quad(_cards[i], "Diamond", FxMath.Rgba(1f, 1f, 1f, 0.55f), 0f, 0f, CardScale * 0.38f, CardScale * 0.38f, -0.03f);
                d.transform.localRotation = FxMath.RotZ(45f);
                _phase[i] = i * FxMath.PI * 2f / N;
            }

            _sparks = new SpriteRenderer[10];
            _sx = new float[10];
            _sy = new float[10];
            Sparkles(c.Rt, _sparks, _sx, _sy, c.HalfW * 0.8f, 1.7f, Cy);

            float size = TitleSize(c.KindLine, 5.6f);
            _shadow = Text(c.Rt, "TitleShadow", c.KindLine, size, FxMath.Rgba(0.12f, 0f, 0.1f, 0.95f), c.HalfW * 2f, true, false);
            _title = Text(c.Rt, "Title", c.KindLine, size, FxMath.Rgba(1f, 1f, 1f), c.HalfW * 2f, true, false);
            Outline(c, _title);
            _head = Text(c.Rt, "Header", c.Header, 2.2f, c.Light, c.HalfW * 2f, true, false);
            _by = ByText(c, c.Rt, 2.2f);
            FlipCrew(c, false);
        }

        public override void Update(Ctx c, float t, float dt, float fade, float openE)
        {
            float gather = EaseOut3(t / 0.2f);
            float fan = EaseOut3((t - 0.85f) / 0.25f);

            for (int i = 0; i < N; i++)
            {
                // 切り混ぜ: 左右に行き交いながら前後が入れ替わる
                float ph = t * 11f + _phase[i];
                float mx = FxMath.Sin(ph) * 1.6f, my = Cy + FxMath.Cos(ph * 0.5f) * 0.15f;
                float mz = 0.1f + (FxMath.Cos(ph) + 1f) * 0.02f + i * 0.001f;
                float mr = FxMath.Sin(ph) * 10f;

                // 扇: 下の一点を中心に -40°〜40°
                float a = FxMath.Lerp(-40f, 40f, i / (float)(N - 1));
                float rad = (90f - a) / FxMath.Rad2Deg;
                float fx = FxMath.Cos(rad) * 1.5f, fy = Cy - 1.2f + FxMath.Sin(rad) * 1.5f;

                // 下から集まって切り混ぜ、最後に扇へ
                float x = FxMath.Lerp(mx * gather, fx, fan);
                float y = FxMath.Lerp(FxMath.Lerp(-c.HalfH - 1.5f, my, gather), fy, fan);
                float z = FxMath.Lerp(mz, 0.1f + (N - i) * 0.01f, fan);
                _cards[i].localPosition = FxMath.V3(x, y, z);
                _cards[i].localRotation = FxMath.RotZ(FxMath.Lerp(mr, -a, fan));
            }

            UpdateSparkles(_sparks, c.Light, t, fade, _seed);

            float slide = EaseOut3((t - 0.05f) / 0.22f);
            float hop = t > 1.1f ? FxMath.Abs(FxMath.Sin((t - 1.1f) * 9f)) * 0.12f : 0f;
            PlaceCrew(c, FxMath.Lerp(-c.HalfW * 1.3f, -c.HalfW * 0.68f, slide), -0.6f + hop, -0.05f);

            float ta = FxMath.Clamp01((t - 0.9f) / 0.15f) * fade;
            float punch = t >= 0.9f ? 1f + 0.35f * (1f - FxMath.Clamp01((t - 0.9f) / 0.14f)) : 1.35f;
            SetText(_title, 0.3f, -1.85f, -0.1f, punch, ta);
            SetText(_shadow, 0.36f, -1.91f, -0.09f, punch, 0.95f * ta);
            SetText(_head, 0f, Cy + 2.05f, -0.1f, 1f, FxMath.Clamp01((t - 0.1f) / 0.2f) * fade);
            SetText(_by, 0.3f, -2.5f, -0.1f, 1f, FxMath.Clamp01((t - 1f) / 0.2f) * fade);
        }
    }

    // ---- 祝福: 天から太い光の柱が降り、その中でクルーが浮かぶ。技名は金の飾り枠に入る ----
    private sealed class BlessLayout : Layout
    {
        private const float PlateY = -1.8f, PlateW = 5.4f, PlateH = 1.15f;
        private SpriteRenderer _pillar, _pillarCore, _aura, _floor;
        private Transform _plate;
        private SpriteRenderer[] _motes;
        private float[] _moteX, _moteSpeed, _moteOff;
        private TextMeshPro _title, _shadow, _head, _by;

        public override void Build(Ctx c)
        {
            c.DimMax = 0.22f;
            c.DimColor = FxMath.Rgba(0.1f, 0.07f, 0f);
            float top = c.HalfH + 0.3f;

            _pillar = Spr(c.Rt, "Pillar", _tail, FxMath.Rgba(1f, 0.85f, 0.35f, 0f), 0f, top, c.HalfH * 2.6f, 13f, 0.2f);
            _pillar.transform.localRotation = FxMath.RotZ(-90f);
            _pillarCore = Spr(c.Rt, "PillarCore", _tail, FxMath.Rgba(1f, 0.98f, 0.85f, 0f), 0f, top, c.HalfH * 2.2f, 5f, 0.19f);
            _pillarCore.transform.localRotation = FxMath.RotZ(-90f);
            _aura = Spr(c.Rt, "Aura", _soft, FxMath.Rgba(1f, 0.85f, 0.4f, 0f), 0f, 0.1f, 3.2f, 3.2f, 0.12f);
            _floor = Spr(c.Rt, "Floor", _thinRing, FxMath.Rgba(1f, 0.85f, 0.4f, 0f), 0f, -0.8f, 2.6f, 0.8f, 0.11f);

            _motes = new SpriteRenderer[12];
            _moteX = new float[12];
            _moteSpeed = new float[12];
            _moteOff = new float[12];
            for (int i = 0; i < _motes.Length; i++)
            {
                _moteX[i] = FxMath.Range(-1.3f, 1.3f);
                _moteSpeed[i] = FxMath.Range(0.6f, 1.3f);
                _moteOff[i] = FxMath.Range(0f, 1f);
                float s = FxMath.Range(0.3f, 0.6f);
                _motes[i] = Spr(c.Rt, "Mote", _spark, FxMath.Rgba(1f, 0.95f, 0.7f, 0f), _moteX[i], 0f, s, s, -0.15f);
            }

            // 金の飾り枠: 暗い金の地 + 上下の細線 + 両端と中央の菱形
            var go = new GameObject("Plate") { layer = Layer };
            _plate = go.transform;
            _plate.SetParent(c.Rt, false);
            _plate.localPosition = FxMath.V3(0f, PlateY, 0.05f);
            Quad(_plate, "Ground", FxMath.Rgba(0.3f, 0.2f, 0.03f, 0.88f), 0f, 0f, PlateW, PlateH, 0f);
            Color gold = FxMath.Rgba(1f, 0.82f, 0.3f);
            for (int s = -1; s <= 1; s += 2)
            {
                Quad(_plate, "Line", gold, 0f, s * (PlateH * 0.5f - 0.06f), PlateW, 0.035f, -0.01f);
                Quad(_plate, "LineOuter", FxMath.Rgba(gold.r, gold.g, gold.b, 0.55f), 0f, s * (PlateH * 0.5f + 0.04f), PlateW * 0.92f, 0.018f, -0.01f);
                for (int k = -1; k <= 1; k++)
                {
                    float sz = k == 0 ? 0.2f : 0.14f;
                    SpriteRenderer d = Quad(_plate, "Diamond", gold, k * PlateW * 0.5f, s * (PlateH * 0.5f - 0.06f), sz, sz, -0.02f);
                    d.transform.localRotation = FxMath.RotZ(45f);
                }
            }

            float size = TitleSize(c.KindLine, 5.4f);
            _shadow = Text(_plate, "TitleShadow", c.KindLine, size, FxMath.Rgba(0.2f, 0.1f, 0f, 0.95f), PlateW, false, false);
            _title = Text(_plate, "Title", c.KindLine, size, FxMath.Rgba(1f, 0.95f, 0.75f), PlateW, false, false);
            Outline(c, _title, FxMath.Rgba(0.4f, 0.22f, 0f));
            _head = Text(c.Rt, "Header", c.Header, 2.2f, FxMath.Rgba(1f, 0.9f, 0.55f), c.HalfW * 2f, true, false);
            _by = ByText(c, c.Rt, 2.2f);
        }

        public override void Update(Ctx c, float t, float dt, float fade, float openE)
        {
            // 光の柱は上から伸びて降りてくる
            float drop = EaseOut3(t / 0.3f);
            float breathe = 1f + 0.06f * FxMath.Sin(t * 4f);
            _pillar.transform.localScale = FxMath.V3(c.HalfH * 2.6f * drop, 13f * breathe, 1f);
            _pillarCore.transform.localScale = FxMath.V3(c.HalfH * 2.2f * drop, 5f * breathe, 1f);
            _pillar.color = FxMath.Rgba(1f, 0.76f, 0.2f, 0.75f * fade);
            _pillarCore.color = FxMath.Rgba(1f, 0.97f, 0.8f, 0.75f * fade);

            float rise = EaseOut3((t - 0.2f) / 0.9f);
            float cy = FxMath.Lerp(-0.45f, -0.05f, rise) + FxMath.Sin(t * 3f) * 0.05f;
            PlaceCrew(c, 0f, cy, -0.05f);
            _aura.transform.localPosition = FxMath.V3(0f, cy + 0.1f, 0.12f);
            _aura.color = FxMath.Rgba(1f, 0.85f, 0.4f, (0.55f + 0.1f * FxMath.Sin(t * 5f)) * drop * fade);
            _floor.color = FxMath.Rgba(1f, 0.85f, 0.4f, 0.7f * drop * fade);

            for (int i = 0; i < _motes.Length; i++)
            {
                float p = FxMath.Repeat(t * _moteSpeed[i] * 0.6f + _moteOff[i], 1f);
                float y = c.HalfH - p * (c.HalfH + 1f);
                _motes[i].transform.localPosition = FxMath.V3(_moteX[i] + FxMath.Sin(t * 2f + i) * 0.15f, y, -0.15f);
                _motes[i].color = FxMath.Rgba(1f, 0.95f, 0.7f, FxMath.Sin(p * FxMath.PI) * 0.9f * drop * fade);
            }

            float pa = Ease2((t - 0.3f) / 0.25f);
            _plate.localScale = FxMath.V3(pa, 1f, 1f);
            SetText(_title, 0f, -0.02f, -0.05f, 1f, pa >= 0.6f ? fade : 0f);
            SetText(_shadow, 0.05f, -0.08f, -0.04f, 1f, pa >= 0.6f ? 0.95f * fade : 0f);
            SetText(_head, 0f, c.HalfH - 0.45f, -0.1f, 1f, FxMath.Clamp01((t - 0.15f) / 0.2f) * fade);
            SetText(_by, 0f, PlateY - PlateH * 0.5f - 0.35f, -0.1f, 1f, FxMath.Clamp01((t - 0.55f) / 0.2f) * fade);
        }
    }

    private static void EnsureMoreSprites()
    {
        // 黒の斜め縞 (横につなげて切れ目が出ない周期)
        _hazard ??= MakeOwn(128, 32, (x, y) =>
        {
            float u = (x + 1f) * 64f + (y + 1f) * 16f;
            float f = FxMath.Repeat(u / 32f, 1f);
            return FxMath.Clamp01((0.5f - FxMath.Abs(f - 0.5f) - 0.25f) * 16f + 0.5f);
        }, 0.5f);
        // 砂嵐 (座標から決まる乱数)
        if (_noise == null)
        {
            _noise = MakeOwn(96, 96, (x, y) =>
            {
                float h = Hash(FxMath.Floor((x + 1f) * 48f) * 13.7f + FxMath.Floor((y + 1f) * 48f) * 91.3f);
                return h * h;
            }, 0.5f);
            _noise.texture.filterMode = FilterMode.Point;
        }
        // 角の丸いカード (幅 1 × 高さ 1.5)
        _card ??= MakeOwn(64, 96, (x, y) =>
        {
            float px = FxMath.Abs(x * 0.5f), py = FxMath.Abs(y * 0.75f);
            const float r = 0.12f;
            float qx = px - (0.49f - r), qy = py - (0.74f - r);
            float ox = FxMath.Max(qx, 0f), oy = FxMath.Max(qy, 0f);
            float d = FxMath.Sqrt(ox * ox + oy * oy) + FxMath.Min(FxMath.Max(qx, qy), 0f) - r;
            return FxMath.Clamp01(-d * 64f + 0.5f);
        }, 0.5f);
        // 4 方向のきらめき
        _spark ??= MakeOwn(64, 64, (x, y) =>
        {
            float ax = FxMath.Abs(x), ay = FxMath.Abs(y);
            float d = FxMath.Sqrt(x * x + y * y);
            float core = FxMath.Pow(1f - FxMath.Clamp01(d / 0.3f), 2f);
            float h = FxMath.Exp(-ay * 22f) * FxMath.Clamp01(1f - ax);
            float v = FxMath.Exp(-ax * 22f) * FxMath.Clamp01(1f - ay);
            return FxMath.Clamp01(FxMath.Max(core, FxMath.Pow(FxMath.Max(h, v), 1.4f)));
        }, 0.5f);
        // 塗りつぶしの三角 (警告標識)
        _tri ??= MakeOwn(128, 128, (x, y) =>
        {
            float d0 = y + 0.78f;
            float d1 = (-1.8f * x - y + 0.9f) / 2.06f;
            float d2 = (1.8f * x - y + 0.9f) / 2.06f;
            return FxMath.Clamp01(FxMath.Min(d0, FxMath.Min(d1, d2)) * 60f);
        }, 0.5f);
        // チョークの人型 (胴 + 背負い袋の輪郭)
        _chalk ??= MakeOwn(128, 128, (x, y) =>
        {
            float body = RoundBox(x - 0.08f, y + 0.02f, 0.4f, 0.62f, 0.36f);
            float pack = RoundBox(x + 0.48f, y + 0.02f, 0.13f, 0.32f, 0.11f);
            float d = FxMath.Min(body, pack);
            float line = FxMath.Exp(-FxMath.Pow(d / 0.028f, 2f));
            return FxMath.Clamp01(line * (0.75f + 0.25f * Hash(FxMath.Floor((x + 1f) * 40f) + FxMath.Floor((y + 1f) * 40f) * 57f)));
        }, 0.5f);
    }

    private static float RoundBox(float x, float y, float hx, float hy, float r)
    {
        float qx = FxMath.Abs(x) - (hx - r), qy = FxMath.Abs(y) - (hy - r);
        float ox = FxMath.Max(qx, 0f), oy = FxMath.Max(qy, 0f);
        return FxMath.Sqrt(ox * ox + oy * oy) + FxMath.Min(FxMath.Max(qx, qy), 0f) - r;
    }
}
