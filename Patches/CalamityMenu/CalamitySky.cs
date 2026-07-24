using System.Collections.Generic;
using EndKnot.Modules.CalamityMenu;
using UnityEngine;

namespace EndKnot.Patches.CalamityMenu;

// Calamity メニュー背景の「空」演出 (ホストローカル描画のみ・送信ゼロ)。
//   ① 隕石雨   — 銀河から燃える街へ光条が斜めに降り注ぎ、地平線付近で小フラッシュ。
//   ② 稲妻     — 時々空が白く一瞬フラッシュ (ピーク α 控えめでテキスト可読性を保つ)。
// 加算シェーダは AU ビルドに皆無 (2026-07-24 census 確定) なので、全て Sprites/Default の
// アルファ合成で成立させる。粒子と同じく MakeCircleSprite / 手続きテクスチャで自給する。
public static class CalamitySky
{
    // ── Meteors ──────────────────────────────────────────────────────────
    private struct Meteor
    {
        public GameObject go;
        public SpriteRenderer streak;
        public SpriteRenderer head;
        public Vector3 pos;
        public Vector3 vel;
        public bool active;
    }

    // 隕石雨は一旦停止 (稲妻は残す)。false の間は生成・更新・爆発を丸ごとスキップする。
    // 復活させたいときは true に戻すだけ (他は無改変)。
    private const bool EnableMeteors = false;

    private const int MaxMeteors = 8;
    private static readonly List<Meteor> Meteors = [];
    private static float _spawnTimer;
    private static float _nextSpawnDelay = 2f;

    private static Sprite _streakSprite;
    private static Sprite _headSprite;
    private static Sprite _flashSprite;
    private static Sprite _ringSprite;

    // 爆発を構成する汎用パーツ「Puff」: 膨らみながら色が変わり (colorStart→colorEnd)、
    // 立ち上がり→減衰の包絡でフェードする塊。発火閃光・火球・煙・衝撃波リングを全てこれで表現。
    private struct Puff
    {
        public GameObject go;
        public SpriteRenderer sr;
        public Vector3 pos;
        public Vector3 vel;
        public float life;
        public float maxLife;
        public float startScale;
        public float endScale;
        public Color colorStart;
        public Color colorEnd;
        public float peakAlpha;
        public float fadeInFrac;
    }

    private static readonly List<Puff> Puffs = [];

    // 着弾で飛び散る火花 (重力で放物線を描き、フェードアウト)。
    private struct Ember
    {
        public GameObject go;
        public SpriteRenderer sr;
        public Vector3 pos;
        public Vector3 vel;
        public float life;
        public float maxLife;
    }

    private static readonly List<Ember> Embers = [];

    // ── Lightning ────────────────────────────────────────────────────────
    private static GameObject _lightningGo;
    private static SpriteRenderer _lightningSr;
    private static float _lightningTimer;
    private static float _nextLightningDelay = 12f;
    private static float _lightningLife; // >0 while flashing
    private static float _lightningMaxLife;

    private static Transform _layer;
    private static Camera _cam;
    private static float _left, _right, _bottom, _top;

    // 隕石が「街に落ちた」とみなす高さ (地平線=街の稜線あたり)。ここでフラッシュしてリサイクル。
    private const float ImpactY = -0.8f;

    public static void Init(Transform particleLayer)
    {
        _layer = particleLayer;
        _cam = Camera.main;
        RefreshBounds();

        _streakSprite = MakeStreakSprite();
        _headSprite   = MakeCircleSprite(8, new Color(1f, 0.92f, 0.75f, 1f));
        _flashSprite  = MakeSoftBlobSprite(48);
        _ringSprite   = MakeRingSprite(64);

        Meteors.Clear();
        if (EnableMeteors)
            for (int i = 0; i < MaxMeteors; i++) Meteors.Add(CreateMeteor());

        Puffs.Clear();
        Embers.Clear();

        _spawnTimer = 0f;
        _nextSpawnDelay = Random.Range(0.3f, 1.2f);
        _lightningTimer = 0f;
        _nextLightningDelay = Random.Range(6f, 14f);

        BuildLightningQuad();
    }

    public static void UpdateAll(float dt)
    {
        if (!CalamityMenuState.Active || _layer == null) return;
        if (_cam == null) { _cam = Camera.main; RefreshBounds(); }

        if (EnableMeteors)
        {
            UpdateMeteors(dt);
            UpdatePuffs(dt);
            UpdateEmbers(dt);
        }
        UpdateLightning(dt);
    }

    // ── Meteors ──────────────────────────────────────────────────────────

    private static void UpdateMeteors(float dt)
    {
        // spawn scheduling
        _spawnTimer += dt;
        if (_spawnTimer >= _nextSpawnDelay)
        {
            _spawnTimer = 0f;
            _nextSpawnDelay = Random.Range(0.5f, 1.8f);
            SpawnMeteor();
        }

        for (int i = 0; i < Meteors.Count; i++)
        {
            Meteor m = Meteors[i];
            if (!m.active || m.go == null) continue;

            m.pos += m.vel * dt;

            // 街に着弾 → 爆発 (大閃光 + 火花バースト) を地平線付近に落として消灯
            if (m.pos.y <= ImpactY)
            {
                SpawnExplosion(new Vector3(m.pos.x, ImpactY, 0f));
                m.active = false;
                m.go.SetActive(false);
                Meteors[i] = m;
                continue;
            }

            // 画面横に大きく外れたら消灯 (真下に落ちない斜め弾の保険)
            if (m.pos.x < _left - 2f || m.pos.x > _right + 2f)
            {
                m.active = false;
                m.go.SetActive(false);
                Meteors[i] = m;
                continue;
            }

            m.go.transform.localPosition = m.pos;
            Meteors[i] = m;
        }
    }

    private static void SpawnMeteor()
    {
        // 空きスロットを探す
        int idx = -1;
        for (int i = 0; i < Meteors.Count; i++)
            if (!Meteors[i].active) { idx = i; break; }
        if (idx < 0) return;

        Meteor m = Meteors[idx];

        // 画面上端の外・横幅は広めに散らす。銀河→街 (下・やや中央寄せ) へ落とす。
        float x = Random.Range(_left - 1f, _right + 1f);
        float y = _top + Random.Range(0.5f, 2.0f);
        m.pos = new Vector3(x, y, 0f);

        // 主に下方向、横にわずかに流す (中央側へ寄せると「街へ降る」印象)
        float vy = Random.Range(-9f, -6f);
        float vx = Random.Range(-2.2f, 1.2f);
        m.vel = new Vector3(vx, vy, 0f);

        // 向き: streak の +x 軸を進行方向へ (pivot は先端 = 右端)
        float angle = Mathf.Atan2(m.vel.y, m.vel.x) * Mathf.Rad2Deg;
        m.go.transform.localPosition = m.pos;
        m.go.transform.localEulerAngles = new Vector3(0f, 0f, angle);

        // 長さ・太さをランダムに
        float len = Random.Range(1.6f, 3.0f);
        float thick = Random.Range(0.5f, 0.9f);
        m.streak.transform.localScale = new Vector3(len, thick, 1f);

        m.active = true;
        m.go.SetActive(true);
        Meteors[idx] = m;
    }

    private static Meteor CreateMeteor()
    {
        var go = new GameObject("Meteor");
        go.transform.SetParent(_layer);
        go.transform.localScale = Vector3.one;
        go.SetActive(false);

        // streak (尾): pivot=先端。GO を回転させ、streak 自身は基準スケール1。
        var streakGo = new GameObject("Streak");
        streakGo.transform.SetParent(go.transform);
        streakGo.transform.localPosition = Vector3.zero;
        var streak = streakGo.AddComponent<SpriteRenderer>();
        streak.sprite = _streakSprite;
        streak.sortingOrder = 46;
        streak.color = new Color(1f, 0.82f, 0.55f, 0.95f);

        // head (先端グロー): 回転の影響を受けても位置は原点なので不変
        var headGo = new GameObject("Head");
        headGo.transform.SetParent(go.transform);
        headGo.transform.localPosition = Vector3.zero;
        headGo.transform.localScale = Vector3.one * 0.08f;
        var head = headGo.AddComponent<SpriteRenderer>();
        head.sprite = _headSprite;
        head.sortingOrder = 47;
        head.color = new Color(1f, 0.95f, 0.8f, 1f);

        return new Meteor { go = go, streak = streak, head = head, active = false };
    }

    // ── Impact explosion (layered: flash → fireball → shockwave → sparks → smoke) ──

    private static void SpawnExplosion(Vector3 pos)
    {
        float mag = Random.Range(0.85f, 1.25f); // 個体差 (大きめの爆発も混ぜる)

        // ① 発火の白閃光 — 一瞬で大きく開いて即消える (爆発の「ピカッ」)
        SpawnPuff(pos, Vector3.zero, _flashSprite, sortingOrder: 51,
                  startScale: 0.25f * mag, endScale: 1.1f * mag, life: 0.16f,
                  colorStart: new Color(1f, 1f, 0.95f), colorEnd: new Color(1f, 0.95f, 0.7f),
                  peakAlpha: 1f, fadeInFrac: 0.08f);

        // ② 火球コア — 白熱→黄→橙→赤黒へ変色しながら膨らむ本体 (複数塊で billow)
        int cores = Random.Range(3, 5);
        for (int i = 0; i < cores; i++)
        {
            var off = new Vector3(Random.Range(-0.18f, 0.18f), Random.Range(-0.05f, 0.22f), 0f) * mag;
            var drift = new Vector3(Random.Range(-0.3f, 0.3f), Random.Range(0.3f, 0.9f), 0f);
            SpawnPuff(pos + off, drift, _flashSprite, sortingOrder: 50,
                      startScale: Random.Range(0.14f, 0.22f) * mag, endScale: Random.Range(0.5f, 0.8f) * mag,
                      life: Random.Range(0.45f, 0.7f),
                      colorStart: new Color(1f, 0.95f, 0.75f), colorEnd: new Color(0.45f, 0.09f, 0.02f),
                      peakAlpha: 1f, fadeInFrac: 0.06f);
        }

        // ③ 衝撃波リング — 外へ走って薄れる輪
        SpawnPuff(pos, Vector3.zero, _ringSprite, sortingOrder: 49,
                  startScale: 0.12f * mag, endScale: 1.9f * mag, life: 0.5f,
                  colorStart: new Color(1f, 0.85f, 0.55f), colorEnd: new Color(1f, 0.5f, 0.2f),
                  peakAlpha: 0.85f, fadeInFrac: 0.03f);

        // ④ 火花バースト — 上半球へ放射状に飛ばし重力で放物線
        int sparks = Random.Range(14, 22);
        for (int i = 0; i < sparks; i++)
        {
            float ang = Random.Range(15f, 165f) * Mathf.Deg2Rad;
            float speed = Random.Range(2.2f, 5.5f) * mag;
            var vel = new Vector3(Mathf.Cos(ang) * speed, Mathf.Sin(ang) * speed, 0f);
            SpawnEmber(pos, vel);
        }

        // ⑤ 立ち昇る煙 — 火球の裏で遅れて現れ、大きく膨らみながら上昇して薄れる
        int smoke = Random.Range(3, 6);
        for (int i = 0; i < smoke; i++)
        {
            var off = new Vector3(Random.Range(-0.2f, 0.2f), Random.Range(0f, 0.25f), 0f) * mag;
            var rise = new Vector3(Random.Range(-0.25f, 0.25f), Random.Range(0.5f, 1.1f), 0f);
            float g = Random.Range(0.18f, 0.28f);
            SpawnPuff(pos + off, rise, _flashSprite, sortingOrder: 47,
                      startScale: 0.2f * mag, endScale: Random.Range(0.9f, 1.4f) * mag,
                      life: Random.Range(1.1f, 2.0f),
                      colorStart: new Color(g, g * 0.92f, g * 0.85f), colorEnd: new Color(0.1f, 0.09f, 0.09f),
                      peakAlpha: 0.4f, fadeInFrac: 0.35f);
        }
    }

    private static void SpawnPuff(Vector3 pos, Vector3 vel, Sprite sprite, int sortingOrder,
                                  float startScale, float endScale, float life,
                                  Color colorStart, Color colorEnd, float peakAlpha, float fadeInFrac)
    {
        var go = new GameObject("Puff");
        go.transform.SetParent(_layer);
        go.transform.localPosition = pos;
        go.transform.localScale = Vector3.one * startScale;

        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.sortingOrder = sortingOrder;
        sr.color = new Color(colorStart.r, colorStart.g, colorStart.b, 0f);

        Puffs.Add(new Puff
        {
            go = go, sr = sr, pos = pos, vel = vel,
            life = life, maxLife = life,
            startScale = startScale, endScale = endScale,
            colorStart = colorStart, colorEnd = colorEnd,
            peakAlpha = peakAlpha, fadeInFrac = fadeInFrac
        });
    }

    private static void UpdatePuffs(float dt)
    {
        for (int i = Puffs.Count - 1; i >= 0; i--)
        {
            Puff p = Puffs[i];
            if (p.go == null) { Puffs.RemoveAt(i); continue; }

            p.life -= dt;
            if (p.life <= 0f)
            {
                Object.Destroy(p.go);
                Puffs.RemoveAt(i);
                continue;
            }

            float age = 1f - p.life / p.maxLife; // 0 → 1

            // スケール: 立ち上がりを速く見せる ease-out
            float ease = 1f - (1f - age) * (1f - age);
            float scale = Mathf.Lerp(p.startScale, p.endScale, ease);
            p.pos += p.vel * dt;
            p.go.transform.localPosition = p.pos;
            p.go.transform.localScale = Vector3.one * scale;

            // 色: colorStart → colorEnd を age で補間
            Color rgb = Color.Lerp(p.colorStart, p.colorEnd, age);

            // アルファ包絡: fadeInFrac まで立ち上がり、その後減衰
            float env = age < p.fadeInFrac
                ? age / Mathf.Max(0.0001f, p.fadeInFrac)
                : 1f - (age - p.fadeInFrac) / Mathf.Max(0.0001f, 1f - p.fadeInFrac);
            float alpha = Mathf.Clamp01(env) * p.peakAlpha;

            p.sr.color = new Color(rgb.r, rgb.g, rgb.b, alpha);
            Puffs[i] = p;
        }
    }

    // ── Embers (爆発の火花) ───────────────────────────────────────────────

    private static void SpawnEmber(Vector3 pos, Vector3 vel)
    {
        var go = new GameObject("Ember");
        go.transform.SetParent(_layer);
        go.transform.localPosition = pos;
        go.transform.localScale = Vector3.one * Random.Range(0.03f, 0.06f);

        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = _headSprite;
        sr.sortingOrder = 48;
        sr.color = new Color(1f, Random.Range(0.55f, 0.8f), 0.25f, 1f);

        float life = Random.Range(0.5f, 1.0f);
        Embers.Add(new Ember { go = go, sr = sr, pos = pos, vel = vel, life = life, maxLife = life });
    }

    private static void UpdateEmbers(float dt)
    {
        const float gravity = -6.5f;
        for (int i = Embers.Count - 1; i >= 0; i--)
        {
            Ember e = Embers[i];
            if (e.go == null) { Embers.RemoveAt(i); continue; }

            e.life -= dt;
            if (e.life <= 0f)
            {
                Object.Destroy(e.go);
                Embers.RemoveAt(i);
                continue;
            }

            e.vel += new Vector3(0f, gravity * dt, 0f);
            e.pos += e.vel * dt;
            e.go.transform.localPosition = e.pos;

            float t = e.life / e.maxLife;            // 1 → 0
            var c = e.sr.color;
            e.sr.color = new Color(c.r, c.g, c.b, t); // 消えながら暗転
            Embers[i] = e;
        }
    }

    // ── Lightning ────────────────────────────────────────────────────────

    private static void BuildLightningQuad()
    {
        _lightningGo = new GameObject("LightningFlash");
        _lightningGo.transform.SetParent(_layer);
        _lightningGo.transform.localPosition = new Vector3(0f, 0f, 0f);

        float camH = _cam != null ? _cam.orthographicSize * 2f : 6f;
        float camW = _cam != null ? camH * _cam.aspect : camH * (16f / 9f);
        _lightningGo.transform.localScale = new Vector3(camW * 1.2f, camH * 1.2f, 1f);

        _lightningSr = _lightningGo.AddComponent<SpriteRenderer>();
        _lightningSr.sprite = MakeSolidSprite(new Color(0.92f, 0.95f, 1f, 1f));
        _lightningSr.sortingOrder = 45;
        _lightningSr.color = new Color(0.92f, 0.95f, 1f, 0f);
    }

    private static void UpdateLightning(float dt)
    {
        if (_lightningLife > 0f)
        {
            _lightningLife -= dt;
            float t = Mathf.Clamp01(_lightningLife / _lightningMaxLife); // 1 → 0
            // 二段フラッシュ: 立ち上がり鋭く・落ちながら小さな2発目
            float env = t * t + 0.35f * Mathf.Max(0f, Mathf.Sin(t * Mathf.PI * 3f));
            float alpha = Mathf.Clamp01(env) * 0.38f;
            if (_lightningSr != null) _lightningSr.color = new Color(0.92f, 0.95f, 1f, alpha);
            return;
        }

        _lightningTimer += dt;
        if (_lightningTimer >= _nextLightningDelay)
        {
            _lightningTimer = 0f;
            _nextLightningDelay = Random.Range(8f, 22f);
            _lightningMaxLife = 0.35f;
            _lightningLife = _lightningMaxLife;
        }
    }

    // ── Bounds / sprites ─────────────────────────────────────────────────

    private static void RefreshBounds()
    {
        if (_cam == null) { _left = -5.33f; _right = 5.33f; _bottom = -3f; _top = 3f; return; }
        float h = _cam.orthographicSize;
        float w = h * _cam.aspect;
        _left = -w; _right = w; _bottom = -h; _top = h;
    }

    // 尾スプライト: 右端(head)ほど明るく、左端(tail)へ透明に減衰。上下は中央から減衰。
    // pivot = (1, 0.5) にして GO の原点が先端になるようにする。
    private static Sprite MakeStreakSprite()
    {
        const int w = 64, h = 16;
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        var pixels = new Color[w * h];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            float tx = x / (float)(w - 1);                 // 0(tail)→1(head)
            float ty = 1f - Mathf.Abs((y / (float)(h - 1)) - 0.5f) * 2f; // 中央=1・端=0
            float lengthGrad = Mathf.Pow(tx, 1.6f);        // 先端寄りに集中
            float alpha = lengthGrad * Mathf.Pow(ty, 1.4f);
            pixels[y * w + x] = new Color(1f, 0.85f, 0.6f, alpha);
        }
        tex.SetPixels(pixels);
        tex.Apply();
        // ppu=w → 自然幅=1unit・高さ=0.25unit。pivot 右端中央。
        return Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(1f, 0.5f), w);
    }

    private static Sprite MakeCircleSprite(int radius, Color color)
    {
        int size = radius * 2;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        float cx = radius - 0.5f, cy = radius - 0.5f, r2 = radius * radius;
        var pixels = new Color[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float dx = x - cx, dy = y - cy;
            float dist2 = dx * dx + dy * dy;
            if (dist2 <= r2)
            {
                float edge = 1f - Mathf.Clamp01((Mathf.Sqrt(dist2) - (radius - 1.5f)) / 1.5f);
                pixels[y * size + x] = new Color(color.r, color.g, color.b, edge);
            }
        }
        tex.SetPixels(pixels);
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
    }

    private static Sprite MakeSolidSprite(Color tint)
    {
        var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false);
        var pixels = new Color[16];
        for (int i = 0; i < 16; i++) pixels[i] = tint;
        tex.SetPixels(pixels);
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 4);
    }

    // 火球/煙用のふわっとした塊: 中心が濃く縁へ滑らかに消える (white 基調・色は SpriteRenderer で着色)。
    private static Sprite MakeSoftBlobSprite(int radius)
    {
        int size = radius * 2;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        float c = (size - 1) * 0.5f, maxDist = radius;
        var pixels = new Color[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float dx = x - c, dy = y - c;
            float d = Mathf.Sqrt(dx * dx + dy * dy) / maxDist; // 0(中央)→1(縁)
            float a = Mathf.Clamp01(1f - d);
            a = a * a * (3f - 2f * a); // smoothstep で柔らかい塊感
            pixels[y * size + x] = new Color(1f, 1f, 1f, a);
        }
        tex.SetPixels(pixels);
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
    }

    // 衝撃波リング: 中間半径にピークを持つ環。内外へ滑らかに 0 へ落ちる。
    private static Sprite MakeRingSprite(int radius)
    {
        int size = radius * 2;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        float c = (size - 1) * 0.5f;
        float ringR = radius * 0.72f;   // 環のピーク半径
        float thickness = radius * 0.22f; // 環の太さ (フェード幅)
        var pixels = new Color[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float dx = x - c, dy = y - c;
            float d = Mathf.Sqrt(dx * dx + dy * dy);
            float a = Mathf.Clamp01(1f - Mathf.Abs(d - ringR) / thickness);
            a = a * a; // 縁を締める
            pixels[y * size + x] = new Color(1f, 1f, 1f, a);
        }
        tex.SetPixels(pixels);
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
    }
}
