using System.Collections;
using System.Collections.Generic;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using UnityEngine;

namespace EndKnot.Modules;

// 波動砲に撃たれた本人の画面に出すキル演出。横から来た光線に呑まれたクルーが震えながら焼かれ、光の粒になって吹き散り、焦げた死体が残る。
// 波動砲のキルは追放扱いでバニラのキル演出が出ないので、ホストが撃たれた本人にだけ合図を送って出す。
internal static class CannonKillOverlay
{
    private const float Duration = 2.9f;
    private const float BeamAt = 0.3f;
    private const float VanishAt = 1.45f;
    private const float BeamEnd = 1.95f;
    private const int Layer = 5;

    private static Sprite _white;
    private static Sprite _soft;
    private static GameObject _root;
    private static PoolablePlayer _crew;

    public static bool Showing => _root;

    // fromLeft = 光線が画面の左から来る
    public static void Show(bool fromLeft, Color light, Color main, Color deep)
    {
        if (!HudManager.InstanceExists || GameStates.IsMeeting) return;

        HudManager.Instance.StartCoroutine(CoShow(fromLeft, light, main, deep).WrapToIl2Cpp());
    }

    private static IEnumerator CoShow(bool fromLeft, Color light, Color main, Color deep)
    {
        Transform cam = HudManager.Instance.transform.parent;
        PlayerControl lp = PlayerControl.LocalPlayer;
        if (!cam || !lp || !lp.Data) yield break;

        Close(_root, _crew);
        EnsureSprites();

        float halfH = 3f, halfW = 5.4f;

        try
        {
            Camera c = cam.GetComponent<Camera>();
            if (c)
            {
                halfH = c.orthographicSize;
                halfW = halfH * c.aspect;
            }
        }
        catch (System.Exception e) { Utils.ThrowException(e); }

        float s = fromLeft ? 1f : -1f;
        GameObject root = _root = new GameObject("CannonKillOverlay") { layer = Layer };
        root.transform.SetParent(cam, false);
        root.transform.localPosition = FxMath.V3(0f, 0f, -905f);

        // 背景: 画面を暗く落とし、光線の色の帯を敷く
        SpriteRenderer dim = Quad(root.transform, _white, FxMath.Rgba(0f, 0f, 0f, 0f), 0f, 0f, halfW * 2.2f, halfH * 2.2f, 0.5f);
        Color deepA = deep;
        deepA.a = 0.95f;
        SpriteRenderer band = Quad(root.transform, _white, deepA, 0f, -0.2f, halfW * 2.2f, 0f, 0.4f);
        SpriteRenderer bandEdgeTop = Quad(root.transform, _white, light, 0f, 1.1f, halfW * 2.2f, 0.06f, 0.35f);
        SpriteRenderer bandEdgeBottom = Quad(root.transform, _white, light, 0f, -1.5f, halfW * 2.2f, 0.06f, 0.35f);
        SpriteRenderer flash = Quad(root.transform, _white, FxMath.Rgba(1f, 1f, 1f, 0f), 0f, 0f, halfW * 2.2f, halfH * 2.2f, -0.6f);

        // 光線: 外側の光・本体・白い芯
        Color mainA = main;
        mainA.a = 0.9f;
        SpriteRenderer beamGlow = Quad(root.transform, _soft, FxMath.Rgba(light.r, light.g, light.b, 0f), 0f, -0.25f, halfW * 2.4f, 0f, 0.1f);
        SpriteRenderer beamBody = Quad(root.transform, _white, mainA, 0f, -0.25f, 0f, 1.25f, 0f);
        SpriteRenderer beamCore = Quad(root.transform, _white, FxMath.Rgba(1f, 1f, 1f), 0f, -0.25f, 0f, 0.42f, -0.05f);
        // クルーの手前にかぶせる薄い光 (光線に呑まれて見える)
        SpriteRenderer haze = Quad(root.transform, _soft, FxMath.Rgba(1f, 1f, 1f, 0f), 0f, -0.25f, halfW * 2.4f, 1.8f, -0.2f);
        beamBody.enabled = beamCore.enabled = false;

        PoolablePlayer crew = null;
        var crewTint = new List<SpriteRenderer>();

        try
        {
            if (HudManager.Instance.IntroPrefab && HudManager.Instance.IntroPrefab.PlayerPrefab)
            {
                crew = Object.Instantiate(HudManager.Instance.IntroPrefab.PlayerPrefab, root.transform);
                crew.transform.localPosition = FxMath.V3(0f, -0.3f, -0.1f);
                crew.transform.localScale = FxMath.V3(0.8f, 0.8f, 1f);
                crew.UpdateFromPlayerOutfit(lp.Data.DefaultOutfit, PlayerMaterial.MaskType.None, false, false);
                crew.ToggleName(false);
                crew.TogglePet(false);
                // 光線の来る方を向く
                crew.SetFlipX(fromLeft);
                foreach (SpriteRenderer sr in crew.GetComponentsInChildren<SpriteRenderer>(true)) crewTint.Add(sr);
            }
        }
        catch (System.Exception e) { Utils.ThrowException(e); }

        _crew = crew;
        SetLayer(root.transform);

        int colorId = lp.Data.DefaultOutfit.ColorId;
        Color bodyColor = colorId >= 0 && colorId < Palette.PlayerColors.Length ? Palette.PlayerColors[colorId] : FxMath.Rgba(0.8f, 0.1f, 0.1f);

        try
        {
            KillOverlay ko = HudManager.Instance.KillOverlay;
            OverlayKillAnimation stab = ko && ko.KillAnims != null && ko.KillAnims.Length > 0 ? ko.KillAnims[0] : null;
            if (stab && stab.Stinger && Constants.ShouldPlaySfx()) SoundManager.Instance.PlaySound(stab.Stinger, false, stab.StingerVolume);
        }
        catch (System.Exception e) { Utils.ThrowException(e); }

        bool hasCrew = crew;
        var parts = new List<Part>();
        bool vanished = false;
        SpriteRenderer deadBody = null;
        float ashClock = 0f;
        float t = 0f;
        float shake = 0f;

        while (t < Duration)
        {
            if (!root || GameStates.IsMeeting || !GameStates.InGame) break;

            float dt = Time.deltaTime;
            t += dt;

            // 閃光 → 帯が開く → 最後に帯が閉じる
            flash.color = FxMath.Rgba(1f, 1f, 1f, t < 0.12f ? 0.85f * (1f - t / 0.12f) : t >= VanishAt && t < VanishAt + 0.12f ? 0.6f * (1f - (t - VanishAt) / 0.12f) : 0f);
            float open = FxMath.Clamp01((t - 0.05f) / 0.18f);
            float closing = t > Duration - 0.25f ? FxMath.Clamp01((Duration - t) / 0.25f) : 1f;
            float bandH = 2.6f * (1f - (1f - open) * (1f - open)) * closing;
            band.transform.localScale = FxMath.V3(halfW * 2.2f, bandH, 1f);
            bandEdgeTop.transform.localPosition = FxMath.V3(0f, -0.2f + bandH * 0.5f, 0.35f);
            bandEdgeBottom.transform.localPosition = FxMath.V3(0f, -0.2f - bandH * 0.5f, 0.35f);
            dim.color = FxMath.Rgba(0f, 0f, 0f, 0.45f * open * closing);

            // 光線: 来る側から一瞬で画面を貫き、クルーが消えたあと細くなって消える
            if (t >= BeamAt && t < BeamEnd)
            {
                beamBody.enabled = beamCore.enabled = true;
                float reach = FxMath.Clamp01((t - BeamAt) / 0.1f);
                float fade = t > VanishAt ? 1f - FxMath.Clamp01((t - VanishAt) / (BeamEnd - VanishAt)) : 1f;
                float wob = 1f + 0.08f * FxMath.Sin(t * 70f) + 0.05f * FxMath.Sin(t * 43f);
                float w = halfW * 2.4f * reach;
                float x = -s * halfW * 1.2f + s * w * 0.5f;
                beamBody.transform.localPosition = FxMath.V3(x, -0.25f, 0f);
                beamBody.transform.localScale = FxMath.V3(w, 1.25f * wob * fade, 1f);
                beamCore.transform.localPosition = FxMath.V3(x, -0.25f, -0.05f);
                beamCore.transform.localScale = FxMath.V3(w, 0.42f * wob * fade, 1f);
                beamGlow.transform.localScale = FxMath.V3(halfW * 2.4f, 3.2f * wob * fade * reach, 1f);
                beamGlow.color = FxMath.Rgba(light.r, light.g, light.b, 0.55f * fade);
                haze.transform.localScale = FxMath.V3(halfW * 2.4f, 1.8f * wob * fade * reach, 1f);
                haze.color = FxMath.Rgba(1f, 1f, 1f, 0.3f * fade * reach);
                if (reach >= 1f && shake < 0.05f && t < VanishAt) shake = 0.12f;
            }
            else if (t >= BeamEnd)
            {
                beamBody.enabled = beamCore.enabled = false;
                beamGlow.color = FxMath.Rgba(light.r, light.g, light.b, 0f);
                haze.color = FxMath.Rgba(1f, 1f, 1f, 0f);
            }

            if (hasCrew && !vanished)
            {
                crew.gameObject.SetActive(t >= 0.08f);

                if (t >= BeamAt)
                {
                    // 焼かれるほど光の色に染まり、震えが強くなる
                    float k = FxMath.Clamp01((t - BeamAt) / (VanishAt - BeamAt));
                    float amp = 0.02f + 0.07f * k;
                    crew.transform.localPosition = FxMath.V3(FxMath.Range(-amp, amp) + s * 0.25f * k * k, -0.3f + FxMath.Range(-amp, amp) * 0.6f, -0.1f);
                    float blink = FxMath.Sin(t * (24f + 30f * k)) > 0f ? 1f : 0.6f;
                    float mix = (0.35f + 0.6f * k) * blink;
                    Color tint = FxMath.Rgba(1f - (1f - light.r) * mix * 0.4f, 1f - (1f - light.g) * mix * 0.4f, 1f - (1f - light.b) * mix * 0.4f, 1f - 0.35f * k * k);
                    foreach (SpriteRenderer sr in crewTint)
                        if (sr) sr.color = tint;

                    // 体の縁から光の粒が剥がれて光線の向きへ流れる
                    ashClock += dt * (40f + 90f * k);
                    while (ashClock >= 1f)
                    {
                        ashClock -= 1f;
                        var p = FxMath.V2(FxMath.Range(-0.35f, 0.35f), -0.3f + FxMath.Range(-0.45f, 0.5f));
                        Color col = FxMath.Value < 0.35f ? bodyColor : FxMath.Value < 0.5f ? FxMath.Rgba(0.2f, 0.2f, 0.22f) : light;
                        float size = FxMath.Range(0.05f, 0.12f);
                        Add(parts, root.transform, _white, col, p, FxMath.V2(s * FxMath.Range(2.5f, 6f), FxMath.Range(-0.4f, 1.2f)), size, size * 0.3f, FxMath.Range(0.5f, 0.9f), -0.1f,
                            drag: 0.6f, spin: FxMath.Range(-400f, 400f), fadeFrom: 0.5f);
                    }
                }

                if (t >= VanishAt)
                {
                    // 最後に光の粒となって吹き散る
                    vanished = true;
                    crew.gameObject.SetActive(false);
                    shake = 0.2f;

                    for (int i = 0; i < 70; i++)
                    {
                        var p = FxMath.V2(FxMath.Range(-0.4f, 0.4f), -0.3f + FxMath.Range(-0.5f, 0.55f));
                        Color col = i % 3 == 0 ? bodyColor : i % 3 == 1 ? light : FxMath.Rgba(1f, 1f, 1f);
                        float size = FxMath.Range(0.06f, 0.16f);
                        Add(parts, root.transform, _white, col, p, FxMath.V2(s * FxMath.Range(3f, 9f), FxMath.Range(-1.5f, 2f)), size, size * 0.2f, FxMath.Range(0.6f, 1.1f), -0.1f,
                            drag: 0.8f, spin: FxMath.Range(-600f, 600f), fadeFrom: 0.45f);
                    }

                    Add(parts, root.transform, _soft, light, FxMath.V2(0f, -0.3f), Vector2.zero, 0.4f, 3.2f, 0.35f, -0.2f, fadeFrom: 0.2f);

                    deadBody = ExplosionKillOverlay.SpawnDeadBody(root.transform, colorId);
                    if (deadBody)
                    {
                        deadBody.gameObject.layer = Layer;
                        deadBody.transform.localPosition = FxMath.V3(0f, -0.55f, 0f);
                    }
                }
            }

            // 焦げた死体: 光線が消える頃に姿を見せ、煙がくすぶる
            if (deadBody)
            {
                float u = FxMath.Clamp01((t - VanishAt - 0.25f) / 0.35f);
                deadBody.enabled = u > 0f;
                deadBody.color = FxMath.Rgba(0.35f, 0.33f, 0.33f, u);

                if (u > 0f && FxMath.Value < dt * 14f)
                    Add(parts, root.transform, _soft, FxMath.Rgba(0.45f, 0.43f, 0.45f), FxMath.V2(FxMath.Range(-0.35f, 0.35f), -0.45f), FxMath.V2(FxMath.Range(-0.1f, 0.1f), FxMath.Range(0.5f, 0.9f)),
                        0.25f, 0.7f, FxMath.Range(0.8f, 1.2f), -0.15f, fadeFrom: 0.3f);
            }

            if (shake > 0f)
            {
                shake = FxMath.Max(0f, shake - dt * 0.35f);
                float a = shake * shake * 2.5f;
                root.transform.localPosition = FxMath.V3(FxMath.Range(-a, a), FxMath.Range(-a, a), -905f);
            }

            for (int i = parts.Count - 1; i >= 0; i--)
            {
                Part p = parts[i];
                p.Age += dt;
                float a = p.Age / p.Life;

                if (a >= 1f)
                {
                    p.Sr.enabled = false;
                    parts.RemoveAt(i);
                    continue;
                }

                p.Vel.x *= 1f - FxMath.Min(1f, p.Drag * dt);
                p.Vel.y *= 1f - FxMath.Min(1f, p.Drag * dt);
                p.Pos.x += p.Vel.x * dt;
                p.Pos.y += p.Vel.y * dt;
                p.Rot += p.Spin * dt;
                float sc = FxMath.Lerp(p.S0, p.S1, a);
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

        if (_root == root)
        {
            Close(root, crew);
            _root = null;
            _crew = null;
        }
    }

    // 次の演出に割り込まれた時もここを通す (プレイヤー色の設定で複製されたマテリアルを残さない)
    private static void Close(GameObject root, PoolablePlayer crew)
    {
        if (crew)
        {
            foreach (Renderer r in crew.GetComponentsInChildren<Renderer>(true))
            {
                if (!r) continue;

                foreach (Material m in r.sharedMaterials)
                    if (m && m.name.EndsWith("(Instance)")) Object.Destroy(m);
            }
        }

        if (!root) return;

        // 死体は体の色を付けたマテリアルを持つ
        foreach (SpriteRenderer sr in root.GetComponentsInChildren<SpriteRenderer>(true))
        {
            if (!sr || sr.gameObject.name != "DeadBody") continue;

            Material m = sr.sharedMaterial;
            if (m && m.name.EndsWith("(Instance)")) Object.Destroy(m);
        }

        Object.Destroy(root);
    }

    private struct Part
    {
        public SpriteRenderer Sr;
        public Transform Tf;
        public Vector2 Pos, Vel;
        public float Drag, Rot, Spin, S0, S1, Age, Life, FadeFrom, Z;
        public Color Color;
    }

    private static void Add(List<Part> parts, Transform root, Sprite sprite, Color color, Vector2 pos, Vector2 vel, float s0, float s1, float life,
        float z, float drag = 0f, float spin = 0f, float fadeFrom = 0.6f)
    {
        var go = new GameObject("fx") { layer = Layer };
        go.transform.SetParent(root, false);
        SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.color = color;
        go.transform.localPosition = FxMath.V3(pos.x, pos.y, z);
        go.transform.localScale = FxMath.V3(s0, s0, 1f);

        parts.Add(new Part
        {
            Sr = sr, Tf = go.transform, Pos = pos, Vel = vel, Drag = drag, Spin = spin, S0 = s0, S1 = s1, Life = life, FadeFrom = fadeFrom, Z = z, Color = color
        });
    }

    private static SpriteRenderer Quad(Transform parent, Sprite sprite, Color color, float x, float y, float w, float h, float z)
    {
        var go = new GameObject("q") { layer = Layer };
        go.transform.SetParent(parent, false);
        go.transform.localPosition = FxMath.V3(x, y, z);
        go.transform.localScale = FxMath.V3(w, h, 1f);
        SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.color = color;
        return sr;
    }

    private static void SetLayer(Transform t)
    {
        t.gameObject.layer = Layer;
        for (int i = 0; i < t.childCount; i++) SetLayer(t.GetChild(i));
    }

    // 1 × 1 ユニットの白い四角と、縁がぼける白い円 (上下にぼけるので光線の外側の光にも使う)
    private static void EnsureSprites()
    {
        if (!_white) _white = Make(4, (_, _) => 1f);
        if (!_soft) _soft = Make(64, (x, y) => FxMath.Pow(1f - FxMath.Clamp01(FxMath.Sqrt(x * x + y * y)), 1.8f));
    }

    private static Sprite Make(int size, System.Func<float, float, float> alpha)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        var px = new Color32[size * size];

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float u = (x + 0.5f) / size * 2f - 1f;
                float v = (y + 0.5f) / size * 2f - 1f;
                px[y * size + x] = FxMath.Rgba32(255, 255, 255, (byte)(FxMath.Clamp01(alpha(u, v)) * 255f));
            }
        }

        tex.SetPixels32(px);
        tex.Apply(false, true);
        tex.hideFlags |= HideFlags.HideAndDontSave;

        Sprite sprite = Sprite.Create(tex, new Rect(0f, 0f, size, size), FxMath.V2(0.5f, 0.5f), size, 0, SpriteMeshType.FullRect);
        sprite.hideFlags |= HideFlags.HideAndDontSave;
        return sprite;
    }
}
