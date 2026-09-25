using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using EndKnot.Modules;
using EndKnot.Modules.CalamityMenu;
using UnityEngine;

namespace EndKnot.Patches.CalamityMenu;

// メインメニュー背景「滅びゆく街の夕暮れ」。紙芝居のように切り絵の層を重ね、少しだけ動かす。
// 素材は層ごとの PNG と、層の置き場所・窓の位置を並べた dusk_layers.txt (座標はキャンバス px)。
// 描画は全て Sprites/Default のアルファ合成 (AU ビルドに加算シェーダは無い)。送信ゼロ・ホストローカル。
public static class CalamityDusk
{
    // false にすると旧背景 (写真 + 炎動画 + 流星) に戻る。
    public static bool Enabled = true;
    public static bool Built { get; private set; }

    // 配色違いの素材一式をテーマごとのフォルダに持ち、起動ごとに 1 つ選ぶ (同じ起動中はメニューに戻っても同じ空)。
    private static readonly string[] Themes = ["plum", "ember", "crimson"];
    private static string _theme;
    private static string Prefix => "EndKnot.Resources.Images.MainMenu.Dusk." + (_theme ??= Themes[Rng.Next(Themes.Length)]) + ".";
    private const float StepFps = 12f;          // 花火と窓のコマ送り
    private const float SwayPeriod = 48f;
    private const float SwayMaxPx = 12f;

    private sealed class Layer
    {
        public string Name;
        public Transform T;
        public float Cx, Cy, W;                  // キャンバス px (中心)
        public float Parallax;
        public float Drift;                      // 雲の流れ (px/s)
        public float OffX;
    }

    private struct Spark
    {
        public SpriteRenderer Sr;
        public Vector2 Pos, Vel;
        public float Life, MaxLife, Size;
        public Color Color;
        public bool Flash;
    }

    private struct Rocket
    {
        public SpriteRenderer Sr;
        public Vector2 From, To;
        public float T, Dur, Hue;
        public bool Double;
    }

    private struct Puff
    {
        public SpriteRenderer Sr;
        public Vector2 Pos;
        public float Age, Life, Seed;
    }

    private static readonly List<Layer> Layers = [];
    private static readonly System.Random Rng = new();
    private static Transform _root;
    private static float _canvasW, _canvasH, _upp, _visHalfW, _swayAmp;
    private static float _stepAcc;

    // 窓明かり: 点灯状態はメニューを離れても保持する (戻るたびに街が少しずつ暗くなっていく)。
    private static bool[] _lit;
    private static Vector2[] _winPos;
    private static Texture2D _winTex;
    private static Transform _winT;
    private static float _winLayerX, _winLayerY;
    private static int _midOrder;
    private static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Color> _warmBlock, _offBlock;
    private static int _flickerIdx = -1, _flickerLeft;
    private static float _flickerT, _nextOut, _nextRelight;
    private static bool _winDirty;

    private static Sprite _dotSprite, _blobSprite;
    private static readonly List<Spark> Sparks = [];
    private static readonly List<Rocket> Rockets = [];
    private static readonly List<Puff> Puffs = [];
    private static readonly Stack<SpriteRenderer> SparkPool = new();
    private static Transform _fxRoot;
    private static float _nextFirework, _nextPuff;
    private static Layer _farLayer, _sunLayer;
    private static readonly Vector2[] SmokeSources = [new(812f, 716f), new(2046f, 770f)];

    public static bool Build(Transform backgroundLayer)
    {
        Built = false;
        if (!Enabled || backgroundLayer == null) return false;
        try
        {
            if (!BuildInner(backgroundLayer)) { Teardown(); return false; }
            Built = true;
            BootTimeline.Mark("dusk.build");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Exception(ex, "CalamityDusk.Build");
            Teardown();
            return false;
        }
    }

    private static void Teardown()
    {
        if (_root != null) UnityEngine.Object.Destroy(_root.gameObject);
        _root = null;
        Layers.Clear();
        Sparks.Clear();
        Rockets.Clear();
        Puffs.Clear();
        SparkPool.Clear();
        _winT = null;
        if (_winTex != null) UnityEngine.Object.Destroy(_winTex);
        _winTex = null;
    }

    private static bool BuildInner(Transform parent)
    {
        Teardown();
        string manifest = ReadText(Prefix + "dusk_layers.txt");
        if (manifest == null) return false;

        var layerLines = new List<string[]>();
        var wins = new List<Vector2>();
        foreach (string raw in manifest.Split('\n'))
        {
            string[] f = raw.Trim().Split(' ');
            if (f.Length < 3) continue;
            if (f[0] == "canvas") { _canvasW = F(f[1]); _canvasH = F(f[2]); }
            else if (f[0] == "layer" && f.Length >= 6) layerLines.Add(f);
            else if (f[0] == "win" && f.Length >= 3) wins.Add(new Vector2(F(f[1]), F(f[2])));
        }
        if (_canvasW <= 0f || layerLines.Count == 0) return false;

        // cover フィット + 視差ぶんの余白。縦長にも横長 (21:9) にも、端が見切れないよう ortho×aspect から出す。
        Camera cam = Camera.main;
        float camH = cam != null ? cam.orthographicSize * 2f : 6f;
        float camW = cam != null ? camH * cam.aspect : camH * (16f / 9f);
        _upp = Mathf.Max(camH / _canvasH, camW / (_canvasW - 4f));
        _visHalfW = camW * 0.5f / _upp;
        _swayAmp = Mathf.Clamp(_canvasW * 0.5f - _visHalfW - 1f, 0f, SwayMaxPx);

        _root = new GameObject("DuskBackdrop").transform;
        _root.SetParent(parent, false);
        _root.localPosition = Vector3.zero;

        int order = -110;
        foreach (string[] f in layerLines)
        {
            string name = f[1];
            float x = F(f[2]), y = F(f[3]), w = F(f[4]), h = F(f[5]);
            Sprite sp = Utils.LoadSprite(Prefix + "dusk_" + name + ".png", 100f);
            if (sp == null)
            {
                if (name == "sky") return false;
                continue;
            }

            var go = new GameObject("Dusk_" + name);
            go.transform.SetParent(_root, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sp;
            // 花火と煙は far と mid の間、窓明かりは mid の直上に差し込む
            if (name == "mid") order += 2;
            if (name == "fg") order += 1;
            sr.sortingOrder = order++;
            float texW = sp.texture.width;
            go.transform.localScale = Vector3.one * (w * _upp * 100f / texW);

            var layer = new Layer { Name = name, T = go.transform, Cx = x + w * 0.5f, Cy = y + h * 0.5f, W = w, Parallax = ParallaxOf(name) };
            if (name.StartsWith("clouds_hi")) layer.Drift = 4.5f * (0.7f + 0.6f * (float)Rng.NextDouble());
            else if (name.StartsWith("clouds_lo")) layer.Drift = 2.2f * (0.7f + 0.6f * (float)Rng.NextDouble());
            Layers.Add(layer);
            if (name == "far") _farLayer = layer;
            if (name == "sun") _sunLayer = layer;

            if (name == "mid") _midOrder = sr.sortingOrder;
            if (name == "mid") BuildWindows(go.transform.parent, x, y, w, h, wins, sr.sortingOrder + 1);
        }

        if (!_dotSprite) _dotSprite = MakeDotSprite(16);
        if (!_blobSprite) _blobSprite = MakeDotSprite(48);
        _fxRoot = new GameObject("DuskFx").transform;
        _fxRoot.SetParent(_root, false);
        BuildMenuShade();
        _nextFirework = 3f;
        _nextPuff = 0f;
        // 煙は最初から立っているように見せるため、寿命ぶん先に進めておく
        for (int i = 0; i < 90; i++) UpdateSmoke(0.1f);

        ApplyLayout(0f);
        Logger.Info($"Dusk backdrop built: theme={_theme} layers={Layers.Count} windows={wins.Count} upp={_upp:0.0000} sway={_swayAmp:0.0}", "CalamityDusk");
        return true;
    }

    // メニュー文字の列の後ろだけ、ぼかした暗いかげを薄く敷く (明るいもやの上でも生成りの文字が読めるように)。
    private static void BuildMenuShade()
    {
        var go = new GameObject("Dusk_menuShade");
        go.transform.SetParent(_root, false);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = _blobSprite;
        sr.color = new Color(0.10f, 0.05f, 0.09f, 0.42f);
        sr.sortingOrder = -80;
        go.transform.localPosition = new Vector3(0f, -0.35f, 0f);
        // _blobSprite は 0.96 units 四方 (96px / ppu 100)
        go.transform.localScale = new Vector3(4.6f / 0.96f, 5.2f / 0.96f, 1f);
    }

    private static float ParallaxOf(string name)
    {
        if (name == "sky") return 0f;
        if (name == "sun") return 0.08f;
        if (name.StartsWith("clouds_hi")) return 0.15f;
        if (name.StartsWith("clouds_lo")) return 0.25f;
        if (name == "far") return 0.35f;
        if (name == "mid") return 0.6f;
        return 1f;
    }

    public static void Tick(float dt)
    {
        if (!Built || !CalamityMenuState.Active || _root == null) return;
        if (dt > 0.25f) dt = 0.25f;

        float t = Time.realtimeSinceStartup;
        foreach (Layer l in Layers)
        {
            if (l.Drift == 0f) continue;
            l.OffX += l.Drift * dt;
            float left = l.Cx + l.OffX - l.W * 0.5f;
            if (left > _canvasW * 0.5f + _visHalfW + 30f)
                l.OffX -= _visHalfW * 2f + l.W + 60f;
        }
        ApplyLayout(t);
        UpdateSmoke(dt);

        _stepAcc += dt;
        if (_stepAcc < 1f / StepFps) return;
        float step = _stepAcc;
        _stepAcc = 0f;
        UpdateWindows(step);
        UpdateFireworks(step);
    }

    private static void ApplyLayout(float t)
    {
        float sway = _swayAmp * Mathf.Sin(t * Mathf.PI * 2f / SwayPeriod);
        foreach (Layer l in Layers)
        {
            float cy = l.Cy;
            // 夕日はとてもゆっくり沈んでは戻る (10 分周期・気づかれない速さ)
            if (l == _sunLayer) cy += 26f * (0.5f - 0.5f * Mathf.Cos(t * Mathf.PI * 2f / 600f));
            l.T.localPosition = ToLocal(l.Cx + l.OffX + sway * l.Parallax, cy, 0f);
        }
        if (_winT != null)
            _winT.localPosition = ToLocal(_winLayerX + sway * ParallaxOf("mid"), _winLayerY, 0f);
        if (_fxRoot != null)
            _fxRoot.localPosition = new Vector3(sway * ParallaxOf("far") * _upp, 0f, 0f);
    }

    private static Vector3 ToLocal(float cx, float cy, float z)
        => new((cx - _canvasW * 0.5f) * _upp, (_canvasH * 0.5f - cy) * _upp, z);

    // ── 窓明かり ─────────────────────────────────────────────────────────
    // 窓 1 つ = 半解像度テクスチャの 4×5 px。消灯・点灯のたびに該当ブロックだけ書き換え、Apply はコマごとに最大 1 回。
    private static void BuildWindows(Transform parent, float lx, float ly, float lw, float lh, List<Vector2> wins, int order)
    {
        if (wins.Count == 0) return;
        int tw = Mathf.CeilToInt(lw / 2f), th = Mathf.CeilToInt(lh / 2f);
        _winTex = new Texture2D(tw, th, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        var clear = new Color32[tw * th];
        _winTex.SetPixels32(clear);

        var warm = new Color[20];
        var off = new Color[20];
        for (int i = 0; i < 20; i++)
        {
            warm[i] = new Color(1f, 0.80f, 0.50f, 0.95f);
            off[i] = new Color(0f, 0f, 0f, 0f);
        }
        _warmBlock = warm;
        _offBlock = off;

        _winPos = new Vector2[wins.Count];
        for (int i = 0; i < wins.Count; i++)
            _winPos[i] = new Vector2(Mathf.Clamp(Mathf.Round((wins[i].x - lx) / 2f), 0, tw - 4),
                                     Mathf.Clamp(Mathf.Round((lh - (wins[i].y - ly) - 10f) / 2f), 0, th - 5));

        if (_lit == null || _lit.Length != wins.Count)
        {
            _lit = new bool[wins.Count];
            for (int i = 0; i < _lit.Length; i++) _lit[i] = Rng.NextDouble() < 0.55;
        }
        for (int i = 0; i < _lit.Length; i++)
            if (_lit[i]) PaintWindow(i, true);
        _winTex.Apply(false, false);

        var go = new GameObject("Dusk_windows");
        go.transform.SetParent(parent, false);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = Sprite.Create(_winTex, new Rect(0, 0, tw, th), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
        sr.sortingOrder = order;
        go.transform.localScale = Vector3.one * (_upp * 100f * 2f);
        _winT = go.transform;
        _winLayerX = lx + tw;          // テクスチャ中心 (キャンバス px)
        _winLayerY = ly + lh - th;

        _flickerIdx = -1;
        _nextOut = 1.5f + (float)Rng.NextDouble() * 2f;
        _nextRelight = 9f;
    }

    private static void PaintWindow(int i, bool on)
    {
        Vector2 p = _winPos[i];
        var block = !on ? _offBlock : _warmBlock;
        _winTex.SetPixels((int)p.x, (int)p.y, 4, 5, block);
        _winDirty = true;
    }

    private static void UpdateWindows(float step)
    {
        if (_winTex == null || _lit == null) return;

        if (_flickerIdx >= 0)
        {
            _flickerT -= step;
            if (_flickerT <= 0f)
            {
                _flickerLeft--;
                bool on = _flickerLeft > 0 && _flickerLeft % 2 == 0;
                PaintWindow(_flickerIdx, on);
                if (_flickerLeft <= 0)
                {
                    _lit[_flickerIdx] = false;
                    _flickerIdx = -1;
                }
                else _flickerT = 0.06f + (float)Rng.NextDouble() * 0.16f;
            }
        }
        else
        {
            _nextOut -= step;
            if (_nextOut <= 0f)
            {
                _nextOut = 1.8f + (float)Rng.NextDouble() * 3f;
                if (LitFraction() > 0.12f)
                {
                    int idx = PickWindow(true);
                    if (idx >= 0)
                    {
                        _flickerIdx = idx;
                        _flickerLeft = 1 + 2 * Rng.Next(0, 3);   // 奇数回の明滅のあと消える
                        _flickerT = 0.1f;
                    }
                }
            }
        }

        _nextRelight -= step;
        if (_nextRelight <= 0f)
        {
            _nextRelight = 8f + (float)Rng.NextDouble() * 7f;
            if (LitFraction() < 0.5f)
            {
                int idx = PickWindow(false);
                if (idx >= 0 && idx != _flickerIdx) { _lit[idx] = true; PaintWindow(idx, true); }
            }
        }

        if (_winDirty)
        {
            _winTex.Apply(false, false);
            _winDirty = false;
        }
    }

    private static float LitFraction()
    {
        int n = 0;
        foreach (bool b in _lit) if (b) n++;
        return (float)n / _lit.Length;
    }

    private static int PickWindow(bool lit)
    {
        for (int tries = 0; tries < 40; tries++)
        {
            int i = Rng.Next(_lit.Length);
            if (_lit[i] == lit) return i;
        }
        return -1;
    }

    // ── 遠くの花火 (最後のお祭り) ───────────────────────────────────────
    // 滅びた街の向こうで、ときどき音もなく虹色の花火が上がる。コマ送りで動かして切り絵の手触りにする。
    private static void UpdateFireworks(float step)
    {
        _nextFirework -= step;
        if (_nextFirework <= 0f)
        {
            _nextFirework = 7f + (float)Rng.NextDouble() * 9f;
            LaunchRocket(false);
        }

        for (int i = Rockets.Count - 1; i >= 0; i--)
        {
            Rocket r = Rockets[i];
            r.T += step;
            float k = Mathf.Clamp01(r.T / r.Dur);
            float e = 1f - (1f - k) * (1f - k);
            Vector2 p = Vector2.Lerp(r.From, r.To, e);
            r.Sr.transform.localPosition = ToLocal(p.x, p.y, 0f);
            r.Sr.color = new Color(1f, 0.9f, 0.75f, 0.8f * (1f - k * 0.5f));
            if (k >= 1f)
            {
                Burst(r.To, r.Hue, r.Double ? 0.7f : 1f);
                if (r.Double)
                    Burst(r.To + new Vector2(RandRange(-60f, 60f), RandRange(-20f, 30f)), r.Hue + 0.5f, 0.55f);
                Release(r.Sr);
                Rockets.RemoveAt(i);
                continue;
            }
            Rockets[i] = r;
        }

        for (int i = Sparks.Count - 1; i >= 0; i--)
        {
            Spark s = Sparks[i];
            s.Life -= step;
            if (s.Life <= 0f)
            {
                Release(s.Sr);
                Sparks.RemoveAt(i);
                continue;
            }
            float drag = Mathf.Exp(-1.1f * step);
            s.Vel *= drag;
            if (!s.Flash) s.Vel.y += 26f * step;
            s.Pos += s.Vel * step;
            float lifeK = s.Life / s.MaxLife;
            Color c = s.Color;
            c.a *= s.Flash ? lifeK * lifeK : Mathf.Pow(lifeK, 1.3f);
            s.Sr.color = c;
            s.Sr.transform.localPosition = ToLocal(s.Pos.x, s.Pos.y, 0f);
            float size = s.Flash ? s.Size * (1.4f - 0.4f * lifeK) : s.Size * (0.5f + 0.5f * lifeK);
            s.Sr.transform.localScale = Vector3.one * (size * _upp * 100f / (s.Flash ? 96f : 32f));
            Sparks[i] = s;
        }
    }

    private static void LaunchRocket(bool isDouble)
    {
        // 画面中央 (メニュー文字の後ろ) は避けて、左右の街の上に上げる
        float x = Rng.NextDouble() < 0.5 ? RandRange(560f, 1060f) : RandRange(1560f, 2240f);
        var to = new Vector2(x + RandRange(-30f, 30f), RandRange(300f, 520f));
        SpriteRenderer sr = Acquire(_dotSprite, _midOrder - 2);
        sr.transform.localScale = Vector3.one * (3f * _upp * 100f / 32f);
        Rockets.Add(new Rocket
        {
            Sr = sr, From = new Vector2(x, 780f), To = to, T = 0f, Dur = RandRange(1.0f, 1.4f),
            Hue = (float)Rng.NextDouble(), Double = isDouble || Rng.NextDouble() < 0.3,
        });
    }

    private static void Burst(Vector2 at, float hue, float scale)
    {
        SpriteRenderer flash = Acquire(_blobSprite, _midOrder - 2);
        Sparks.Add(new Spark
        {
            Sr = flash, Pos = at, Vel = Vector2.zero, Life = 0.6f, MaxLife = 0.6f, Size = 230f * scale,
            Color = Color.HSVToRGB(Mathf.Repeat(hue, 1f), 0.35f, 1f) * new Color(1f, 1f, 1f, 0.3f), Flash = true,
        });

        int n = (int)(36 * scale) + Rng.Next(0, 10);
        for (int i = 0; i < n; i++)
        {
            float a = (i + (float)Rng.NextDouble() * 0.4f) / n * Mathf.PI * 2f;
            float sp = RandRange(95f, 135f) * scale;
            Color c = Color.HSVToRGB(Mathf.Repeat(hue + (float)i / n, 1f), 0.75f, 1f);
            float life = RandRange(1.5f, 2.2f);
            Sparks.Add(new Spark
            {
                Sr = Acquire(_dotSprite, _midOrder - 2), Pos = at, Vel = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * sp,
                Life = life, MaxLife = life, Size = RandRange(7f, 9.5f), Color = c,
            });
        }
    }

    private static SpriteRenderer Acquire(Sprite sprite, int order)
    {
        SpriteRenderer sr = null;
        while (SparkPool.Count > 0 && sr == null) sr = SparkPool.Pop();
        if (sr == null)
        {
            var go = new GameObject("DuskSpark");
            go.transform.SetParent(_fxRoot, false);
            sr = go.AddComponent<SpriteRenderer>();
        }
        sr.sprite = sprite;
        sr.sortingOrder = order;
        sr.gameObject.SetActive(true);
        return sr;
    }

    private static void Release(SpriteRenderer sr)
    {
        if (sr == null) return;
        sr.gameObject.SetActive(false);
        SparkPool.Push(sr);
    }

    // ── 廃墟から立ち昇る細い煙 ─────────────────────────────────────────
    private static void UpdateSmoke(float dt)
    {
        if (_fxRoot == null) return;
        _nextPuff -= dt;
        if (_nextPuff <= 0f)
        {
            _nextPuff = 0.55f;
            foreach (Vector2 src in SmokeSources)
            {
                SpriteRenderer sr = Acquire(_blobSprite, _midOrder - 1);
                Puffs.Add(new Puff { Sr = sr, Pos = src + new Vector2(RandRange(-3f, 3f), 0f), Age = 0f, Life = RandRange(9f, 12f), Seed = (float)Rng.NextDouble() * 10f });
            }
        }

        for (int i = Puffs.Count - 1; i >= 0; i--)
        {
            Puff p = Puffs[i];
            p.Age += dt;
            if (p.Age >= p.Life)
            {
                Release(p.Sr);
                Puffs.RemoveAt(i);
                continue;
            }
            float k = p.Age / p.Life;
            p.Pos += new Vector2(5f + 4f * k + Mathf.Sin(p.Age * 0.7f + p.Seed) * 3f, -13f) * dt;
            float a = Mathf.Min(k * 6f, 1f) * (1f - k) * 0.16f;
            p.Sr.color = new Color(0.30f, 0.20f, 0.26f, a);
            p.Sr.transform.localPosition = ToLocal(p.Pos.x, p.Pos.y, 0f);
            p.Sr.transform.localScale = Vector3.one * ((10f + 70f * k) * _upp * 100f / 96f);
            Puffs[i] = p;
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────
    private static float RandRange(float a, float b) => a + (float)Rng.NextDouble() * (b - a);

    private static float F(string s) => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

    private static string ReadText(string resource)
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource);
        if (stream == null)
        {
            Logger.Info($"Resource not found (skipped): {resource}", "CalamityDusk");
            return null;
        }
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // 中心が濃く縁へ滑らかに消える白い点 (色は SpriteRenderer.color で付ける)。直径 = radius*2 px。
    private static Sprite MakeDotSprite(int radius)
    {
        int size = radius * 2;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        var px = new Color[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float dx = (x + 0.5f - radius) / radius, dy = (y + 0.5f - radius) / radius;
            float d = Mathf.Sqrt(dx * dx + dy * dy);
            float a = Mathf.Clamp01(1f - d);
            px[y * size + x] = new Color(1f, 1f, 1f, a * a * (3f - 2f * a));
        }
        tex.SetPixels(px);
        tex.Apply(false, true);
        tex.hideFlags |= HideFlags.HideAndDontSave;
        Sprite sp = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
        sp.hideFlags |= HideFlags.HideAndDontSave;
        return sp;
    }
}
