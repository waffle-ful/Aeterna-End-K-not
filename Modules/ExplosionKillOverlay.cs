using System.Collections;
using System.Collections.Generic;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using HarmonyLib;
using UnityEngine;

namespace EndKnot.Modules;

// 爆発で死んだ本人の画面に出すキル演出。バニラの「赤い炎の帯」の上で、突っ立ったクルーが震えて膨らみ、爆発して半身の死体になる。
// 死因 (SetDeathReason はキルより先に Reliable で届く) で判定するので送信は増えない。バニラ客には従来のキル演出が出る。
internal static class ExplosionKillOverlay
{
    private const float Duration = 2.7f;
    private const float BoomAt = 1.15f;
    private const int Layer = 5;

    private static readonly Color Outline = new(0.09f, 0.07f, 0.11f);

    private static Sprite _burst;
    private static Sprite _puff;
    private static Sprite _chunk;
    private static Sprite _disc;
    private static GameObject _root;

    public static bool Showing => _root;

    public static bool IsExplosionDeath(NetworkedPlayerInfo victim)
    {
        if (!victim || !Main.PlayerStates.TryGetValue(victim.PlayerId, out PlayerState state)) return false;

        return state.deathReason switch
        {
            PlayerState.DeathReason.Bombed => true,
            PlayerState.DeathReason.Stopped => victim.Object && victim.Object.Is(CustomRoles.Supernova),
            _ => false
        };
    }

    public static void Show(NetworkedPlayerInfo victim)
    {
        if (!HudManager.InstanceExists || !victim) return;
        HudManager.Instance.StartCoroutine(CoShow(victim).WrapToIl2Cpp());
    }

    private static IEnumerator CoShow(NetworkedPlayerInfo victim)
    {
        KillOverlay ko = HudManager.Instance.KillOverlay;
        if (!ko) yield break;

        if (_root) Object.Destroy(_root);
        EnsureSprites();

        var parts = new List<Part>();
        GameObject root = _root = new GameObject("ExplosionKillOverlay") { layer = Layer };
        root.transform.SetParent(ko.transform, false);
        root.transform.localPosition = FxMath.V3(0f, 0f, -1f);

        // ── バニラの素材: 赤い全画面の閃光と炎の帯 ──
        SpriteRenderer flash = null;
        Transform fullScreen = ko.transform.Find("FullScreen");

        if (fullScreen)
        {
            GameObject f = Object.Instantiate(fullScreen.gameObject, root.transform);
            f.transform.localPosition = FxMath.V3(0f, 0f, 1.5f);
            flash = f.GetComponent<SpriteRenderer>();
            flash.color = FxMath.Rgba(1f, 0f, 0f, 1f);
            flash.enabled = true;
        }

        Transform flameQuad = null;
        Transform quadParent = ko.transform.Find("QuadParent");

        if (quadParent)
        {
            GameObject q = Object.Instantiate(quadParent.gameObject, root.transform);
            q.transform.localPosition = FxMath.V3(0f, 0f, 1f);
            q.SetActive(true);
            flameQuad = q.transform;
            flameQuad.localScale = FxMath.V3(1f, 0f, 1f);
        }

        // ── 突っ立ったクルー ──
        PoolablePlayer crew = null;
        var crewTint = new List<SpriteRenderer>();

        try
        {
            if (HudManager.Instance.IntroPrefab && HudManager.Instance.IntroPrefab.PlayerPrefab)
            {
                crew = Object.Instantiate(HudManager.Instance.IntroPrefab.PlayerPrefab, root.transform);
                crew.transform.localPosition = FxMath.V3(0f, -0.3f, 0f);
                crew.transform.localScale = FxMath.V3(0.7f, 0.7f, 1f);
                crew.UpdateFromPlayerOutfit(victim.DefaultOutfit, PlayerMaterial.MaskType.None, false, false);
                crew.ToggleName(false);
                crew.TogglePet(false);
                foreach (SpriteRenderer sr in crew.GetComponentsInChildren<SpriteRenderer>(true)) crewTint.Add(sr);
            }
        }
        catch (System.Exception e) { Utils.ThrowException(e); }

        SetLayer(root.transform);

        int colorId = victim.DefaultOutfit.ColorId;
        Color bodyColor = colorId >= 0 && colorId < Palette.PlayerColors.Length ? Palette.PlayerColors[colorId] : FxMath.Rgba(0.8f, 0.1f, 0.1f);

        // バニラと同じ導入曲 (刺されるときの効果音は爆発音に差し替える)
        try
        {
            OverlayKillAnimation stab = ko.KillAnims != null && ko.KillAnims.Length > 0 ? ko.KillAnims[0] : null;
            if (stab && stab.Stinger && Constants.ShouldPlaySfx()) SoundManager.Instance.PlaySound(stab.Stinger, false, stab.StingerVolume);
        }
        catch (System.Exception e) { Utils.ThrowException(e); }

        bool boomed = false;
        SpriteRenderer deadBody = null;
        float t = 0f;
        float shake = 0f;

        while (t < Duration)
        {
            if (!root || GameStates.IsMeeting || !GameStates.InGame && !GameStates.IsLobby) break;

            float dt = Time.deltaTime;
            t += dt;

            // 閃光 0.1 秒 → 炎の帯が斜めから差し込む (0.2 秒) → 最後に抜ける
            if (flash) flash.enabled = t < 0.1f;
            if (crew && !boomed) crew.gameObject.SetActive(t >= 0.1f);

            if (flameQuad)
            {
                float open = t < 0.1f ? 0f : t < 0.3f ? FxMath.Clamp01((t - 0.1f) / 0.2f) : t > Duration - 0.2f ? FxMath.Clamp01((Duration - t) / 0.2f) : 1f;
                float ease = 1f - (1f - open) * (1f - open);
                flameQuad.localScale = FxMath.V3(1f, ease, 1f);
                flameQuad.localRotation = FxMath.RotZ((1f - ease) * 28f);
            }

            if (!boomed)
            {
                if (crew && t >= 0.45f)
                {
                    // 震えながら膨らみ、赤く点滅する (点滅と震えは起爆に向けて速く・強く)
                    float k = FxMath.Clamp01((t - 0.45f) / (BoomAt - 0.45f));
                    float amp = 0.015f + 0.08f * k * k;
                    float swell = 1f + 0.65f * k * k * k;
                    float wob = FxMath.Sin(t * (30f + 40f * k)) * 0.08f * k;
                    crew.transform.localPosition = FxMath.V3(FxMath.Range(-amp, amp), -0.3f + FxMath.Range(-amp, amp) * 0.6f + (swell - 1f) * 0.25f, 0f);
                    crew.transform.localScale = FxMath.V3(0.7f * (swell + wob), 0.7f * (swell - wob), 1f);

                    float blink = FxMath.Sin(t * (10f + 38f * k * k)) > 0.2f ? k : 0f;
                    Color tint = FxMath.Rgba(1f, 1f - 0.7f * blink, 1f - 0.7f * blink);
                    foreach (SpriteRenderer sr in crewTint)
                        if (sr) sr.color = tint;
                }

                if (t >= BoomAt)
                {
                    boomed = true;
                    shake = 0.22f;
                    if (crew) crew.gameObject.SetActive(false);
                    CustomSoundsManager.Play("Boom", 1f, 0.9f);
                    SpawnBoom(root.transform, parts, bodyColor);
                    deadBody = SpawnDeadBody(root.transform, colorId);
                    SetLayer(root.transform);
                }
            }
            else
            {
                if (deadBody)
                {
                    // 爆心へ上から落ちてきて弾む
                    float u = FxMath.Clamp01((t - BoomAt - 0.25f) / 0.35f);
                    float y = u < 1f ? FxMath.Lerp(1.6f, 0f, u * u) : 0f;
                    if (u >= 1f && t - BoomAt - 0.6f < 0.15f) y = FxMath.Sin((t - BoomAt - 0.6f) / 0.15f * FxMath.PI) * 0.08f;
                    deadBody.transform.localPosition = FxMath.V3(0f, -0.55f + y, -0.15f);
                    deadBody.enabled = t >= BoomAt + 0.25f;
                }
            }

            // 画面揺れ (帯ごと揺らす)
            if (shake > 0f)
            {
                shake = FxMath.Max(0f, shake - dt * 0.6f);
                float s = shake * shake * 2.2f;
                root.transform.localPosition = FxMath.V3(FxMath.Range(-s, s), FxMath.Range(-s, s), -1f);
            }

            for (int i = parts.Count - 1; i >= 0; i--)
            {
                Part p = parts[i];
                if (!p.Sr) { parts.RemoveAt(i); continue; }

                p.Age += dt;
                float a = p.Age / p.Life;

                if (a >= 1f)
                {
                    p.Sr.enabled = false;
                    parts.RemoveAt(i);
                    continue;
                }

                p.Vel.y -= p.Gravity * dt;
                p.Vel.x *= 1f - FxMath.Min(1f, p.Drag * dt);
                p.Vel.y *= 1f - FxMath.Min(1f, p.Drag * dt);
                p.Pos.x += p.Vel.x * dt;
                p.Pos.y += p.Vel.y * dt;
                p.Rot += p.Spin * dt;

                float sc = p.Pop > 0f ? FxMath.Lerp(p.S0, p.S1, 1f - FxMath.Pow(1f - FxMath.Clamp01(p.Age / p.Pop), 3f)) : FxMath.Lerp(p.S0, p.S1, a);
                p.Tf.localPosition = FxMath.V3(p.Pos.x, p.Pos.y, p.Z);
                p.Tf.localScale = FxMath.V3(sc, sc, 1f);
                p.Tf.localRotation = FxMath.RotZ(p.Rot);

                Color c = p.Color;
                c.a = a < p.FadeFrom ? 1f : 1f - (a - p.FadeFrom) / (1f - p.FadeFrom);
                p.Sr.color = c;
                parts[i] = p;
            }

            yield return null;
        }

        if (root)
        {
            // プレイヤー色の設定で複製されたマテリアルは Renderer を壊しても残るので、先に消す
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!r) continue;

                foreach (Material m in r.sharedMaterials)
                    if (m && m.name.EndsWith("(Instance)")) Object.Destroy(m);
            }

            Object.Destroy(root);
        }

        if (_root == root) _root = null;
    }

    private struct Part
    {
        public SpriteRenderer Sr;
        public Transform Tf;
        public Vector2 Pos, Vel;
        public float Gravity, Drag, Rot, Spin, S0, S1, Pop, Age, Life, FadeFrom, Z;
        public Color Color;
    }

    private static void Add(List<Part> parts, Transform root, Sprite sprite, Color color, Vector2 pos, Vector2 vel, float s0, float s1, float life,
        float z, float gravity = 0f, float drag = 0f, float spin = 0f, float pop = 0f, float fadeFrom = 0.6f, float rot = 0f)
    {
        var go = new GameObject("fx") { layer = Layer };
        go.transform.SetParent(root, false);
        SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.color = color;
        go.transform.localPosition = FxMath.V3(pos.x, pos.y, z);
        go.transform.localScale = FxMath.V3(s0, s0, 1f);
        go.transform.localRotation = FxMath.RotZ(rot);

        parts.Add(new Part
        {
            Sr = sr, Tf = go.transform, Pos = pos, Vel = vel, Gravity = gravity, Drag = drag, Rot = rot, Spin = spin,
            S0 = s0, S1 = s1, Pop = pop, Life = life, FadeFrom = fadeFrom, Z = z, Color = color
        });
    }

    // 漫画調の爆発: 白い閃光の円 → 黄・橙・赤の三重のギザギザ → 灰色の煙の玉 → 体の色の破片とバイザーが飛び散る
    private static void SpawnBoom(Transform root, List<Part> parts, Color bodyColor)
    {
        Vector2 c = FxMath.V2(0f, -0.1f);
        Color white = FxMath.Rgba(1f, 1f, 1f);
        Color yellow = FxMath.Rgba(1f, 0.93f, 0.3f);
        Color orange = FxMath.Rgba(1f, 0.55f, 0.12f);
        Color red = FxMath.Rgba(0.93f, 0.16f, 0.1f);
        Color smoke = FxMath.Rgba(0.62f, 0.6f, 0.64f);
        Color visor = FxMath.Rgba(0.58f, 0.8f, 0.88f);

        Add(parts, root, _disc, white, c, Vector2.zero, 0.3f, 4.2f, 0.28f, -0.9f, pop: 0.12f, fadeFrom: 0.2f);

        Add(parts, root, _burst, red, c, Vector2.zero, 0.2f, 3.3f, 0.75f, -0.2f, spin: 25f, pop: 0.14f, fadeFrom: 0.55f, rot: 10f);
        Add(parts, root, _burst, orange, c, Vector2.zero, 0.15f, 2.5f, 0.68f, -0.3f, spin: -35f, pop: 0.12f, fadeFrom: 0.5f, rot: -8f);
        Add(parts, root, _burst, yellow, c, Vector2.zero, 0.1f, 1.6f, 0.6f, -0.4f, spin: 50f, pop: 0.1f, fadeFrom: 0.45f, rot: 20f);

        for (int i = 0; i < 9; i++)
        {
            Vector2 d = Dir(i / 9f + FxMath.Range(-0.03f, 0.03f));
            Add(parts, root, _puff, smoke, c + d * FxMath.Range(0.5f, 0.9f), d * FxMath.Range(0.6f, 1.3f) + FxMath.V2(0f, 0.35f), FxMath.Range(0.4f, 0.6f),
                FxMath.Range(1.1f, 1.5f), FxMath.Range(0.95f, 1.15f), -0.1f, drag: 1.4f, spin: FxMath.Range(-40f, 40f), fadeFrom: 0.75f);
        }

        // 体の破片は画面の外まで飛ぶ (漫画の「バラバラ」)
        for (int i = 0; i < 10; i++)
        {
            Vector2 d = Dir(FxMath.Value);
            Color col = i % 3 == 0 ? bodyColor * 0.72f : bodyColor;
            col.a = 1f;
            Add(parts, root, _chunk, col, c, d * FxMath.Range(3.2f, 6f) + FxMath.V2(0f, 1.4f), FxMath.Range(0.32f, 0.55f), FxMath.Range(0.28f, 0.48f), 1.1f, -0.6f,
                gravity: 7f, drag: 0.4f, spin: FxMath.Range(-720f, 720f), fadeFrom: 0.85f, rot: FxMath.Range(0f, 360f));
        }

        Add(parts, root, _chunk, visor, c, FxMath.V2(FxMath.Range(-1.5f, 1.5f), 5.2f), 0.45f, 0.45f, 1.1f, -0.65f, gravity: 7f, spin: FxMath.Range(-540f, 540f), fadeFrom: 0.85f);

        for (int i = 0; i < 8; i++)
        {
            Vector2 d = Dir(FxMath.Value);
            Add(parts, root, _disc, i % 2 == 0 ? yellow : orange, c + d * 0.3f, d * FxMath.Range(4f, 7f), 0.09f, 0.03f, FxMath.Range(0.35f, 0.5f), -0.5f, drag: 3f, fadeFrom: 0.4f);
        }
    }

    // バニラの死体 (骨の出た半身) を体の色で描く。DeadBody 本体は複製しない (通報・掃除の対象に数えられてしまう)。
    private static SpriteRenderer SpawnDeadBody(Transform root, int colorId)
    {
        try
        {
            if (!GameManager.Instance || GameManager.Instance.deadBodyPrefab == null || GameManager.Instance.deadBodyPrefab.Length == 0) return null;

            DeadBody prefab = GameManager.Instance.deadBodyPrefab[0];
            SpriteRenderer src = prefab.bodyRenderers != null && prefab.bodyRenderers.Length > 0 ? prefab.bodyRenderers[0] : null;
            if (!src) return null;

            var go = new GameObject("DeadBody") { layer = Layer };
            go.transform.SetParent(root, false);
            go.transform.localScale = FxMath.V3(0.9f, 0.9f, 1f);
            SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = src.sprite;
            sr.sharedMaterial = src.sharedMaterial;
            sr.enabled = false;
            PlayerMaterial.SetColors(colorId, sr);
            return sr;
        }
        catch (System.Exception e)
        {
            Utils.ThrowException(e);
            return null;
        }
    }

    private static Vector2 Dir(float turn)
    {
        float ang = turn * 2f * FxMath.PI;
        return FxMath.V2(FxMath.Cos(ang), FxMath.Sin(ang));
    }

    private static void SetLayer(Transform t)
    {
        t.gameObject.layer = Layer;
        for (int i = 0; i < t.childCount; i++) SetLayer(t.GetChild(i));
    }

    // ── 素材 (白い塗り + 濃い縁取り。色は SpriteRenderer.color で塗る = 縁は黒のまま残る) ──

    private static void EnsureSprites()
    {
        if (!_burst)
        {
            _burst = MakeOutlined(160, (x, y) =>
            {
                float ang = FxMath.Atan2(y, x);
                float spikes = FxMath.Pow(FxMath.Abs(FxMath.Sin(ang * 5.5f + 0.4f)), 0.6f);
                float jitter = 0.05f * FxMath.Sin(ang * 13f + 1.1f);
                return 0.5f + 0.42f * spikes + jitter;
            }, 0.06f);
        }

        if (!_puff)
        {
            _puff = MakeOutlined(96, (x, y) =>
            {
                float ang = FxMath.Atan2(y, x);
                return 0.82f + 0.1f * FxMath.Abs(FxMath.Sin(ang * 2.5f + 0.3f));
            }, 0.09f);
        }

        if (!_chunk)
        {
            _chunk = MakeOutlined(48, (x, y) =>
            {
                float ang = FxMath.Atan2(y, x);
                return 0.72f + 0.18f * FxMath.Sin(ang * 3f + 1.3f) + 0.08f * FxMath.Sin(ang * 7f);
            }, 0.14f);
        }

        if (!_disc) _disc = MakeOutlined(64, (_, _) => 0.95f, 0f);
    }

    // radius(x, y) は中心からの向きごとの輪郭の半径 (縁 = 1)。outline はその内側に引く濃い縁の太さ。
    private static Sprite MakeOutlined(int size, System.Func<float, float, float> radius, float outline)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        var pixels = new Color[size * size];
        float h = (size - 1) * 0.5f;
        float aa = 1.5f / h;

        for (int py = 0; py < size; py++)
        {
            for (int px = 0; px < size; px++)
            {
                float x = (px - h) / h, y = (py - h) / h;
                float d = FxMath.Sqrt(x * x + y * y);
                float r = radius(x, y);
                float alpha = FxMath.Clamp01((r - d) / aa + 0.5f);
                float fill = outline <= 0f ? 1f : FxMath.Clamp01((r - outline - d) / aa + 0.5f);
                pixels[py * size + px] = FxMath.Rgba(FxMath.Lerp(Outline.r, 1f, fill), FxMath.Lerp(Outline.g, 1f, fill), FxMath.Lerp(Outline.b, 1f, fill), alpha);
            }
        }

        tex.SetPixels(pixels);
        tex.Apply(false, true);
        tex.hideFlags |= HideFlags.HideAndDontSave;

        Sprite sprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size, 0, SpriteMeshType.FullRect);
        sprite.hideFlags |= HideFlags.HideAndDontSave;
        return sprite;
    }
}

[HarmonyPatch(typeof(KillOverlay), nameof(KillOverlay.ShowKillAnimation), typeof(NetworkedPlayerInfo), typeof(NetworkedPlayerInfo))]
internal static class ExplosionKillOverlayPatch
{
    public static bool Prefix(NetworkedPlayerInfo victim)
    {
        try
        {
            if (!victim || !victim.Object || !victim.Object.AmOwner || !ExplosionKillOverlay.IsExplosionDeath(victim)) return true;

            Logger.Info($"explosion kill overlay for {victim.PlayerId}", "ExplosionKillOverlay");
            ExplosionKillOverlay.Show(victim);
            return false;
        }
        catch (System.Exception e)
        {
            Utils.ThrowException(e);
            return true;
        }
    }
}
