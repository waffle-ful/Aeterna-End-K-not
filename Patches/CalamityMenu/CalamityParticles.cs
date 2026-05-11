using System.Collections.Generic;
using EndKnot.Modules.CalamityMenu;
using UnityEngine;

namespace EndKnot.Patches.CalamityMenu;

public static class CalamityParticles
{
    private struct Particle
    {
        public GameObject go;
        public SpriteRenderer sr;
        public Vector3 pos;
        public Vector3 vel;
        public float life;
        public float maxLife;
        public float breathSpeed;
        public float phase;
        public Color baseColor;
    }

    private static readonly List<Particle> Active = [];
    private static Transform _particleLayer;
    private static Sprite _fireflySprite;
    private static Sprite _ashSprite;
    private static Sprite _glowSprite;
    private static Camera _cam;

    // bounds derived from camera; refreshed on Init
    private static float _left, _right, _bottom, _top;

    public static void Init(Transform particleLayer)
    {
        _particleLayer = particleLayer;
        _cam = Camera.main;
        RefreshBounds();

        _fireflySprite = MakeCircleSprite(8,  new Color(1.0f, 0.95f, 0.5f, 1f));
        _ashSprite     = MakeCircleSprite(4,  new Color(0.85f, 0.85f, 0.85f, 1f));
        _glowSprite    = MakeCircleSprite(16, new Color(0.7f, 0.65f, 1.0f, 1f));

        Active.Clear();

        // Pre-spawn all particles at random positions so the screen isn't empty at start
        for (int i = 0; i < 70; i++) SpawnFirefly(randomY: true);
        for (int i = 0; i < 35; i++) SpawnAsh(randomY: true);
        for (int i = 0; i < 12; i++) SpawnGlow(randomY: true);
    }

    public static void UpdateAll(float dt)
    {
        if (!CalamityMenuState.Active || _particleLayer == null) return;

        for (int i = Active.Count - 1; i >= 0; i--)
        {
            Particle p = Active[i];
            if (p.go == null) { Active.RemoveAt(i); continue; }

            p.life -= dt;

            if (p.life <= 0f)
            {
                // respawn instead of destroy so particles persist indefinitely
                if (p.vel.y >= 0f) RespawnAtBottom(i);
                else RespawnAtTop(i);
                continue;
            }

            // position
            p.pos += p.vel * dt;

            // wrap horizontally
            if (p.pos.x > _right + 0.5f) p.pos.x = _left  - 0.5f;
            if (p.pos.x < _left  - 0.5f) p.pos.x = _right + 0.5f;

            // alpha breathing
            float t = (Mathf.Sin(Time.time * p.breathSpeed + p.phase) + 1f) * 0.5f;
            float lifeRatio = p.life / p.maxLife;
            float fade = lifeRatio < 0.15f ? lifeRatio / 0.15f : 1f; // fade out near end
            float alpha = Mathf.Lerp(0.35f, 1.0f, t) * fade;
            p.sr.color = new Color(p.baseColor.r, p.baseColor.g, p.baseColor.b, alpha);

            p.go.transform.localPosition = p.pos;
            Active[i] = p;

            // respawn when out-of-bounds vertically (based on velocity direction)
            if (p.vel.y > 0 && p.pos.y > _top  + 1f) RespawnAtBottom(i);
            if (p.vel.y < 0 && p.pos.y < _bottom - 1f) RespawnAtTop(i);
        }
    }

    // ── Spawn helpers ────────────────────────────────────────────────────

    private static void SpawnFirefly(bool randomY = false)
    {
        float x = Random.Range(_left, _right);
        float y = randomY ? Random.Range(_bottom, _top) : _bottom - Random.Range(0.2f, 1f);
        float life = Random.Range(12f, 22f);
        Spawn(_fireflySprite, new Vector3(x, y, 0f),
              new Vector3(Random.Range(-0.15f, 0.15f), Random.Range(0.08f, 0.22f), 0f),
              life, Random.Range(0.8f, 1.6f), Random.Range(0f, 6.28f),
              new Color(1.0f, 0.95f, 0.5f, 1f), scale: 0.04f);
    }

    private static void SpawnAsh(bool randomY = false)
    {
        float x = Random.Range(_left, _right);
        float y = randomY ? Random.Range(_bottom, _top) : _top + Random.Range(0.2f, 1f);
        float life = Random.Range(15f, 28f);
        Spawn(_ashSprite, new Vector3(x, y, 0f),
              new Vector3(Random.Range(-0.06f, 0.06f), Random.Range(-0.12f, -0.05f), 0f),
              life, Random.Range(0.4f, 0.9f), Random.Range(0f, 6.28f),
              new Color(0.85f, 0.85f, 0.85f, 1f), scale: 0.025f);
    }

    private static void SpawnGlow(bool randomY = false)
    {
        float x = Random.Range(_left, _right);
        float y = randomY ? Random.Range(_bottom, _top) : (Random.value > 0.5f ? _bottom - 1f : _top + 1f);
        float life = Random.Range(20f, 40f);
        float vy = Random.value > 0.5f ? Random.Range(0.03f, 0.07f) : Random.Range(-0.07f, -0.03f);
        Spawn(_glowSprite, new Vector3(x, y, 0f),
              new Vector3(Random.Range(-0.02f, 0.02f), vy, 0f),
              life, Random.Range(0.3f, 0.6f), Random.Range(0f, 6.28f),
              new Color(0.7f, 0.65f, 1.0f, 1f), scale: 0.12f);
    }

    private static void Spawn(Sprite sprite, Vector3 pos, Vector3 vel, float life,
                              float breathSpeed, float phase, Color color, float scale)
    {
        var go = new GameObject("Particle");
        go.transform.SetParent(_particleLayer);
        go.transform.localPosition = pos;
        go.transform.localScale    = Vector3.one * scale;

        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite       = sprite;
        sr.sortingOrder = 50;
        sr.color        = new Color(color.r, color.g, color.b, 0f);

        Active.Add(new Particle
        {
            go = go, sr = sr, pos = pos, vel = vel,
            life = life, maxLife = life,
            breathSpeed = breathSpeed, phase = phase, baseColor = color
        });
    }

    private static void RespawnAtBottom(int idx)
    {
        Particle p = Active[idx];
        p.pos = new Vector3(Random.Range(_left, _right), _bottom - Random.Range(0.2f, 1f), p.pos.z);
        p.life = p.maxLife;
        Active[idx] = p;
    }

    private static void RespawnAtTop(int idx)
    {
        Particle p = Active[idx];
        p.pos = new Vector3(Random.Range(_left, _right), _top + Random.Range(0.2f, 1f), p.pos.z);
        p.life = p.maxLife;
        Active[idx] = p;
    }

    private static void RefreshBounds()
    {
        if (_cam == null) return;
        float h = _cam.orthographicSize;
        float w = h * _cam.aspect;
        _left = -w; _right = w; _bottom = -h; _top = h;
    }

    // ── Procedural sprite ────────────────────────────────────────────────

    private static Sprite MakeCircleSprite(int radius, Color color)
    {
        int size = radius * 2;
        var tex  = new Texture2D(size, size, TextureFormat.RGBA32, false);
        float cx = radius - 0.5f, cy = radius - 0.5f, r2 = radius * radius;
        var pixels = new Color[size * size];

        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float dx = x - cx, dy = y - cy;
            float dist2 = dx * dx + dy * dy;
            if (dist2 <= r2)
            {
                // soft edge
                float edge = 1f - Mathf.Clamp01((Mathf.Sqrt(dist2) - (radius - 1.5f)) / 1.5f);
                pixels[y * size + x] = new Color(color.r, color.g, color.b, edge);
            }
        }

        tex.SetPixels(pixels);
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
    }
}
