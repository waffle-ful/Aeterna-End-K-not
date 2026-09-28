using System.Collections;
using System.Collections.Generic;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using UnityEngine;

namespace EndKnot.Modules;

// 吸血・毒・操り糸 / 呪いで殺された本人の画面に出すキル演出。爆発死のキル演出と同じく、バニラの赤い閃光と炎の帯の上で、
// 突っ立ったクルーが死んでいく様子を見せる。山場は集中線・衝撃波・画面揺れで強調する。バニラ客には従来のキル演出が出る。
//   吸血: 首筋に牙が刺さり、血を吸われて色が抜け、しなびる → 弾けて血しぶきとコウモリの群れが飛び散る
//   毒: 震えながら緑に染まって泡を噴く → よろめいて横倒しになると毒気が爆ぜ、毒の水たまりが広がる
//   操り糸 / 呪い: 糸で吊られた犯人がカクカク降りてきて斬りかかる → 相手が倒れ、犯人は糸で引き上げられる
internal static class SpecialKillOverlay
{
    public enum Style : byte
    {
        Bite,
        Poison,
        Puppet,
        Curse
    }

    private const float Duration = 2.8f;
    private const int Layer = 5;

    private static Sprite _disc;
    private static Sprite _puff;
    private static Sprite _burst;
    private static Sprite _bat;
    private static Sprite _ring;
    private static Sprite _solid;
    private static Sprite _shock;
    private static GameObject _root;

    public static bool Showing => _root;

    public static bool TryGetStyle(NetworkedPlayerInfo victim, out Style style)
    {
        style = Style.Bite;
        if (!victim || !Main.PlayerStates.TryGetValue(victim.PlayerId, out PlayerState state)) return false;

        switch (state.deathReason)
        {
            case PlayerState.DeathReason.Bite:
                style = Style.Bite;
                return true;
            case PlayerState.DeathReason.Poison:
                style = Style.Poison;
                return true;
        }

        if (ExplosionFx.WasStringsKill(victim.PlayerId, out bool curse))
        {
            style = curse ? Style.Curse : Style.Puppet;
            return true;
        }

        return false;
    }

    public static void Show(NetworkedPlayerInfo victim, NetworkedPlayerInfo killer, Style style)
    {
        if (!HudManager.InstanceExists || !victim) return;
        HudManager.Instance.StartCoroutine(CoShow(victim, killer, style).WrapToIl2Cpp());
    }

    private struct Part
    {
        public SpriteRenderer Sr;
        public Transform Tf;
        public Vector2 Pos, Vel;
        public float Gravity, Drag, Rot, Spin, S0, S1, Pop, Age, Delay, Life, FadeFrom, Z, Flap, Squash;
        public Color Color;
    }

    private struct Line
    {
        public SpriteRenderer Sr;
        public Transform Tf;
    }

    private static IEnumerator CoShow(NetworkedPlayerInfo victim, NetworkedPlayerInfo killer, Style style)
    {
        KillOverlay ko = HudManager.Instance.KillOverlay;
        if (!ko) yield break;

        if (_root) Object.Destroy(_root);
        EnsureSprites();

        bool puppet = style is Style.Puppet or Style.Curse;
        var parts = new List<Part>();
        GameObject root = _root = new GameObject("SpecialKillOverlay") { layer = Layer };
        root.transform.SetParent(ko.transform, false);
        root.transform.localPosition = FxMath.V3(0f, 0f, -1f);

        // ── 閃光と帯: バニラのキル演出の赤い閃光と、炎のギザギザの帯を複製して使う (斜めから差し込んで開く) ──
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

        Transform bandTf;
        Transform quadParent = ko.transform.Find("QuadParent");

        if (quadParent)
        {
            GameObject q = Object.Instantiate(quadParent.gameObject, root.transform);
            q.transform.localPosition = FxMath.V3(0f, 0f, 1f);
            q.SetActive(true);
            bandTf = q.transform;
        }
        else
        {
            var band = new GameObject("Band") { layer = Layer };
            band.transform.SetParent(root.transform, false);
            band.transform.localPosition = FxMath.V3(0f, 0f, 1f);
            bandTf = band.transform;
            MakeSolid(bandTf, FxMath.Rgba(0.75f, 0.05f, 0.05f), 0f, 0f, 40f, BandHeight, 0.4f);
        }

        bandTf.localScale = FxMath.V3(1f, 0f, 1f);

        // ── クルー ──
        PoolablePlayer crew = SpawnCrew(root.transform, victim, puppet ? FxMath.V2(0.95f, -0.3f) : FxMath.V2(0f, -0.3f), puppet);
        var crewTint = new List<SpriteRenderer>();
        if (crew) crewTint.AddRange(crew.GetComponentsInChildren<SpriteRenderer>(true));

        PoolablePlayer doll = null;
        var dollTint = new List<(SpriteRenderer Sr, Color Base)>();
        var strings = new List<Line>();
        Color stringCol = style == Style.Curse ? FxMath.Rgba(1f, 0.35f, 0.4f) : FxMath.Rgba(0.9f, 0.75f, 1f);

        if (puppet)
        {
            NetworkedPlayerInfo who = killer && killer.PlayerId != victim.PlayerId ? killer : null;
            doll = SpawnCrew(root.transform, who ? who : victim, FxMath.V2(-0.95f, 3f), false);

            if (doll)
            {
                foreach (SpriteRenderer sr in doll.GetComponentsInChildren<SpriteRenderer>(true))
                {
                    if (!sr) continue;
                    if (!who) sr.color = FxMath.Rgba(0.1f, 0.08f, 0.12f);
                    dollTint.Add((sr, sr.color));
                }
            }

            for (int i = 0; i < 3; i++)
            {
                var go = new GameObject("String") { layer = Layer };
                go.transform.SetParent(root.transform, false);
                SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = _solid;
                sr.color = stringCol;
                strings.Add(new Line { Sr = sr, Tf = go.transform });
            }
        }

        SetLayer(root.transform);

        int colorId = victim.DefaultOutfit.ColorId;

        try
        {
            OverlayKillAnimation stab = ko.KillAnims != null && ko.KillAnims.Length > 0 ? ko.KillAnims[0] : null;
            if (stab && stab.Stinger && Constants.ShouldPlaySfx()) SoundManager.Instance.PlaySound(stab.Stinger, false, stab.StingerVolume);
        }
        catch (System.Exception e) { Utils.ThrowException(e); }

        // 倒れる瞬間 (クルーを消して死体を出す)
        float deathAt = style switch
        {
            Style.Bite => 1.6f,
            Style.Poison => 1.75f,
            _ => 1.3f
        };

        bool dead = false;
        SpriteRenderer deadBody = null;
        float deadX = puppet ? 0.95f : 0f;
        float t = 0f, shake = 0f, emit = 0f;

        while (t < Duration)
        {
            if (!root || GameStates.IsMeeting || !GameStates.InGame && !GameStates.IsLobby) break;

            float dt = Time.deltaTime;
            t += dt;

            if (flash) flash.enabled = t < 0.1f;
            if (crew && !dead) crew.gameObject.SetActive(t >= 0.1f);

            float open = t < 0.1f ? 0f : t < 0.3f ? FxMath.Clamp01((t - 0.1f) / 0.2f) : t > Duration - 0.2f ? FxMath.Clamp01((Duration - t) / 0.2f) : 1f;
            float ease = 1f - (1f - open) * (1f - open);
            bandTf.localScale = FxMath.V3(1f, ease, 1f);
            bandTf.localRotation = FxMath.RotZ((1f - ease) * 28f);

            switch (style)
            {
                case Style.Bite:
                    StepBite(t, dt, ref emit, ref shake, crew, crewTint, parts, root.transform, dead, deathAt);
                    break;
                case Style.Poison:
                    StepPoison(t, dt, ref emit, ref shake, crew, crewTint, parts, root.transform, dead, deathAt);
                    break;
                default:
                    StepPuppet(t, ref shake, doll, dollTint, strings, parts, root.transform, style == Style.Curse);
                    break;
            }

            if (!dead && t >= deathAt)
            {
                dead = true;
                if (crew) crew.gameObject.SetActive(false);
                deadBody = ExplosionKillOverlay.SpawnDeadBody(root.transform, colorId);
                SetLayer(root.transform);

                if (deadBody)
                {
                    deadBody.enabled = true;
                    deadBody.transform.localPosition = FxMath.V3(deadX, -0.55f, -0.15f);

                    // 血を吸われた死体は色が抜けて、毒の死体は緑がかる
                    if (style == Style.Bite) deadBody.color = FxMath.Rgba(0.62f, 0.55f, 0.6f);
                    else if (style == Style.Poison) deadBody.color = FxMath.Rgba(0.7f, 1f, 0.65f);
                }

                OnDeath(style, parts, root.transform, deadX);
                shake = puppet ? 0.16f : 0.3f;
            }

            if (shake > 0f)
            {
                shake = FxMath.Max(0f, shake - dt * 0.6f);
                float s = shake * shake * 2.2f;
                root.transform.localPosition = FxMath.V3(FxMath.Range(-s, s), FxMath.Range(-s, s), -1f);
            }

            StepParts(parts, dt);
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

    private const float BandHeight = 3.6f;

    // ── 吸血 ──
    private static readonly Color Fang = new(1f, 0.95f, 0.95f);
    private static readonly Color BloodRed = new(0.9f, 0.05f, 0.12f);
    private static readonly Color BloodDeep = new(0.45f, 0.01f, 0.06f);

    private static void StepBite(float t, float dt, ref float emit, ref float shake, PoolablePlayer crew, List<SpriteRenderer> tint, List<Part> parts, Transform root,
        bool dead, float deathAt)
    {
        if (dead || !crew) return;

        // 首筋 (バイザーより下の、体の右肩あたり)
        Vector2 neck = FxMath.V2(0.24f, -0.5f);

        // 牙が刺さる
        if (t >= 0.45f && t - dt < 0.45f)
        {
            shake = 0.22f;
            AddSpeedLines(parts, root, neck, FxMath.Rgba(1f, 0.85f, 0.85f), 28, 0.6f);
            AddPart(parts, root, _shock, BloodRed, neck, Vector2.zero, 0.2f, 3.2f, 0.4f, -0.65f, pop: 0.3f, fadeFrom: 0.3f);
            for (int s = -1; s <= 1; s += 2)
            {
                Vector2 p = neck + FxMath.V2(0.07f * s, 0f);
                AddPart(parts, root, _disc, Fang, p, Vector2.zero, 0.02f, 0.12f, 0.3f, -0.7f, pop: 0.06f, fadeFrom: 0.3f);
                AddPart(parts, root, _disc, BloodDeep, p, Vector2.zero, 0.02f, 0.08f, 0.9f, -0.69f, pop: 0.1f, fadeFrom: 0.6f, delay: 0.12f);
                AddPart(parts, root, _burst, BloodRed, p, Vector2.zero, 0.05f, 0.45f, 0.35f, -0.6f, pop: 0.1f, fadeFrom: 0.3f, spin: 90f);
            }
        }

        // 吸われる: 首筋から血の粒が画面の外 (吸っている相手) へ流れ出し、体は色が抜けてしなびる
        float k = FxMath.Clamp01((t - 0.5f) / (deathAt - 0.5f));

        if (t >= 0.5f)
        {
            emit += dt;

            while (emit >= 0.022f)
            {
                emit -= 0.022f;
                Vector2 v = FxMath.V2(FxMath.Range(-4.8f, -3.2f), FxMath.Range(1.8f, 3.2f));
                AddPart(parts, root, _disc, FxMath.Value < 0.3f ? FxMath.Rgba(1f, 0.35f, 0.4f) : BloodRed, neck + FxMath.V2(FxMath.Range(-0.05f, 0.05f), FxMath.Range(-0.05f, 0.05f)), v,
                    FxMath.Range(0.1f, 0.16f), 0.05f, 0.7f, -0.5f, drag: 0.4f, fadeFrom: 0.6f);
            }
        }

        float amp = 0.01f + 0.03f * k;
        float shrink = 1f - 0.16f * k * k;
        crew.transform.localPosition = FxMath.V3(FxMath.Range(-amp, amp), -0.3f - 0.08f * k * k, 0f);
        crew.transform.localScale = FxMath.V3(0.7f * (1f - 0.08f * k), 0.7f * shrink, 1f);

        float g = 1f - 0.45f * k;
        Color c = FxMath.Rgba(g + 0.05f * k, g * 0.92f, g * 0.95f);
        foreach (SpriteRenderer sr in tint)
            if (sr) sr.color = c;
    }

    // ── 毒 ──
    private static readonly Color Tox = new(0.5f, 1f, 0.3f);
    private static readonly Color ToxPale = new(0.85f, 1f, 0.55f);
    private static readonly Color ToxViolet = new(0.62f, 0.25f, 0.95f);

    private static void StepPoison(float t, float dt, ref float emit, ref float shake, PoolablePlayer crew, List<SpriteRenderer> tint, List<Part> parts, Transform root,
        bool dead, float deathAt)
    {
        if (dead || !crew) return;

        const float fallFrom = 1.35f;
        float k = FxMath.Clamp01((t - 0.3f) / (fallFrom - 0.3f));

        if (t >= 0.35f)
        {
            emit += dt;

            // 泡と紫の毒気が体から湧く
            while (emit >= 0.05f)
            {
                emit -= 0.05f;
                float s = FxMath.Range(0.2f, 0.36f);
                Vector2 p = FxMath.V2(FxMath.Range(-0.3f, 0.3f), FxMath.Range(-0.6f, 0.1f));
                float life = FxMath.Range(0.5f, 0.8f);
                Vector2 v = FxMath.V2(FxMath.Range(-0.3f, 0.3f), FxMath.Range(1f, 1.8f));
                AddPart(parts, root, _ring, FxMath.Value < 0.4f ? ToxPale : Tox, p, v, s * 0.4f, s, life, -0.5f, fadeFrom: 0.85f);
                AddPart(parts, root, _burst, ToxPale, p + v * life, Vector2.zero, s * 0.6f, s * 1.6f, 0.16f, -0.55f, delay: life, fadeFrom: 0.2f, spin: 120f);

                if (FxMath.Value < 0.45f)
                {
                    AddPart(parts, root, _puff, FxMath.Rgba(ToxViolet.r, ToxViolet.g, ToxViolet.b, 0.55f), FxMath.V2(FxMath.Range(-0.3f, 0.3f), FxMath.Range(-0.2f, 0.3f)),
                        FxMath.V2(FxMath.Range(-0.3f, 0.3f), FxMath.Range(0.5f, 0.9f)), 0.3f, FxMath.Range(0.8f, 1.1f), FxMath.Range(0.8f, 1.1f), -0.1f, spin: FxMath.Range(-40f, 40f),
                        fadeFrom: 0.5f);
                }
            }
        }

        // 足元から緑に染まる (点滅しながら強まる)
        float blink = 0.75f + 0.25f * FxMath.Sin(t * 22f);
        float g = k * blink;
        Color c = FxMath.Rgba(1f - 0.45f * g, 1f, 1f - 0.6f * g);
        foreach (SpriteRenderer sr in tint)
            if (sr) sr.color = c;

        if (t < fallFrom)
        {
            // 苦しんで震える (次第に強く)
            float amp = 0.01f + 0.05f * k * k;
            crew.transform.localPosition = FxMath.V3(FxMath.Range(-amp, amp), -0.3f + FxMath.Range(-amp, amp) * 0.4f, 0f);
            float wob = FxMath.Sin(t * 30f) * 0.04f * k;
            crew.transform.localScale = FxMath.V3(0.7f * (1f + wob), 0.7f * (1f - wob), 1f);
            crew.transform.localRotation = FxMath.RotZ(FxMath.Sin(t * 9f) * 6f * k);
            if (t >= 1f && t - dt < 1f)
            {
                shake = 0.12f;
                AddPart(parts, root, _shock, Tox, FxMath.V2(0f, -0.3f), Vector2.zero, 0.3f, 2.6f, 0.4f, -0.6f, pop: 0.3f, fadeFrom: 0.3f);
            }
        }
        else
        {
            // よろめいて横倒しに
            float u = FxMath.Clamp01((t - fallFrom) / (deathAt - fallFrom));
            float e = u * u;
            crew.transform.localRotation = FxMath.RotZ(-86f * e);
            crew.transform.localPosition = FxMath.V3(0.35f * e, -0.3f - 0.3f * e, 0f);
            crew.transform.localScale = FxMath.V3(0.7f, 0.7f, 1f);
        }
    }

    // ── 操り糸 / 呪い ──
    private static void StepPuppet(float t, ref float shake, PoolablePlayer doll, List<(SpriteRenderer Sr, Color Base)> dollTint, List<Line> strings, List<Part> parts,
        Transform root, bool curse)
    {
        const float strike = 1.12f;

        if (!doll) return;

        // 糸で吊られた犯人: カクカクと 4 段で降りる → 斬りかかる → 糸に引き上げられて消える
        float x, y, rot;

        if (t < 0.95f)
        {
            float step = FxMath.Clamp01(t / 0.9f) * 4f;
            int n = (int)step;
            float within = step - n;
            float settle = within < 0.25f ? 1f - within / 0.25f : 0f;
            float yy = 3f - (n + FxMath.Min(1f, within / 0.25f)) * (3.3f / 4f);
            y = yy + settle * 0.05f * FxMath.Sin(within * 60f);
            x = -0.95f;
            rot = (n % 2 == 0 ? 10f : -10f) * (1f - FxMath.Min(1f, within * 3f)) + (n % 2 == 0 ? -4f : 4f);
        }
        else if (t < strike)
        {
            float u = FxMath.Clamp01((t - 0.95f) / (strike - 0.95f));
            x = -0.95f + 1.3f * u * u;
            y = -0.3f + 0.15f * FxMath.Sin(u * FxMath.PI);
            rot = -18f * u;
        }
        else if (t < 1.75f)
        {
            float u = FxMath.Clamp01((t - strike) / 0.6f);
            x = 0.35f - 0.3f * u;
            y = -0.3f + 0.05f * FxMath.Sin(u * 20f) * (1f - u);
            rot = -18f + 22f * u;
        }
        else
        {
            float u = FxMath.Clamp01((t - 1.75f) / 0.55f);
            x = 0.05f;
            y = -0.3f + 4f * u * u;
            rot = 4f + 25f * FxMath.Sin(u * 12f) * u;
        }

        doll.transform.localPosition = FxMath.V3(x, y, -0.05f);
        doll.transform.localRotation = FxMath.RotZ(rot);

        // 帯の外 (上端より上) にいる間と、帯が開ききるまでは見せない。上端をくぐる所はにじませる
        float vis = t < 0.3f ? 0f : FxMath.Clamp01((BandHeight * 0.5f - 0.1f - y) / 0.6f);
        doll.gameObject.SetActive(vis > 0f);

        foreach ((SpriteRenderer sr, Color c) in dollTint)
            if (sr) sr.color = FxMath.Rgba(c.r, c.g, c.b, c.a * vis);

        if (t >= strike && t - Time.deltaTime < strike)
        {
            shake = 0.34f;
            Color main = curse ? FxMath.Rgba(0.95f, 0.15f, 0.22f) : FxMath.Rgba(0.75f, 0.4f, 1f);
            Vector2 hit = FxMath.V2(0.95f, -0.2f);
            AddSpeedLines(parts, root, hit, FxMath.Rgba(1f, 1f, 1f), 24, 0.8f);
            AddSpeedLines(parts, root, hit, main, 16, 1.1f);
            AddPart(parts, root, _shock, FxMath.Rgba(1f, 1f, 1f), hit, Vector2.zero, 0.2f, 3.6f, 0.35f, -0.85f, pop: 0.25f, fadeFrom: 0.3f);
            AddPart(parts, root, _shock, main, hit, Vector2.zero, 0.2f, 5f, 0.5f, -0.84f, pop: 0.4f, fadeFrom: 0.3f, delay: 0.05f);
            AddPart(parts, root, _disc, FxMath.Rgba(1f, 1f, 1f), hit, Vector2.zero, 0.2f, 1.1f, 0.14f, -0.9f, pop: 0.06f, fadeFrom: 0.2f);
            AddPart(parts, root, _burst, main, hit, Vector2.zero, 0.2f, 1.5f, 0.45f, -0.3f, pop: 0.1f, fadeFrom: 0.4f, spin: 40f);
            AddSlash(parts, root, hit, 38f, main);
            AddSlash(parts, root, hit, -38f, main, 0.05f);

            for (int i = 0; i < 12; i++)
            {
                float a = FxMath.Range(0f, 2f * FxMath.PI);
                AddPart(parts, root, _disc, i % 2 == 0 ? main : FxMath.Rgba(1f, 1f, 1f), hit, FxMath.V2(FxMath.Cos(a), FxMath.Sin(a)) * FxMath.Range(3f, 6f), 0.12f, 0.04f, 0.45f, -0.5f,
                    drag: 3f, fadeFrom: 0.4f);
            }
        }

        // 糸: 画面の上から、頭と両手へ (ぴんと張って細かく震える。斬る瞬間だけ白く光る)
        float cos = FxMath.Cos(rot / FxMath.Rad2Deg), sin = FxMath.Sin(rot / FxMath.Rad2Deg);
        float glow = t >= strike - 0.08f && t < strike + 0.12f ? 1f : 0f;

        for (int i = 0; i < strings.Count; i++)
        {
            Line l = strings[i];
            if (!l.Sr) continue;

            float ax = i switch { 0 => -0.3f, 1 => 0f, _ => 0.3f };
            float ay = i == 1 ? 0.42f : 0.02f;
            float bx = x + ax * cos - ay * sin, by = y + ax * sin + ay * cos;
            float tx = bx + (i - 1) * 0.35f + FxMath.Sin(t * 40f + i) * 0.01f, ty = BandHeight * 0.5f;
            l.Sr.enabled = vis > 0f && by < ty;
            float dx = tx - bx, dy = ty - by;
            float len = FxMath.Sqrt(dx * dx + dy * dy);
            l.Tf.localPosition = FxMath.V3((bx + tx) * 0.5f, (by + ty) * 0.5f, -0.1f);
            l.Tf.localRotation = FxMath.RotZ(FxMath.Atan2(dy, dx) * FxMath.Rad2Deg);
            l.Tf.localScale = FxMath.V3(len, glow > 0f ? 0.06f : 0.03f, 1f);
            l.Sr.color = glow > 0f ? FxMath.Rgba(1f, 1f, 1f, vis) : curse ? FxMath.Rgba(1f, 0.35f, 0.4f, vis) : FxMath.Rgba(0.9f, 0.75f, 1f, vis);
        }

        // 呪いは足元から赤い火の粉が立ちのぼる
        if (curse && FxMath.Value < 0.35f)
        {
            AddPart(parts, root, _disc, FxMath.Rgba(1f, 0.35f, 0.3f), FxMath.V2(FxMath.Range(-2.5f, 2.5f), -1.6f), FxMath.V2(FxMath.Range(-0.2f, 0.2f), FxMath.Range(0.8f, 1.6f)),
                FxMath.Range(0.05f, 0.09f), 0.02f, FxMath.Range(0.9f, 1.4f), -0.2f, fadeFrom: 0.5f);
        }
    }

    private static void OnDeath(Style style, List<Part> parts, Transform root, float x)
    {
        Vector2 c = FxMath.V2(x, -0.35f);

        switch (style)
        {
            case Style.Bite:
            {
                // 吸い尽くされた体が弾けて血しぶきが舞う
                AddPart(parts, root, _disc, FxMath.Rgba(1f, 0.9f, 0.9f), c, Vector2.zero, 0.3f, 2.4f, 0.16f, -0.9f, pop: 0.08f, fadeFrom: 0.2f);
                AddPart(parts, root, _burst, BloodDeep, c, Vector2.zero, 0.2f, 3.6f, 0.7f, -0.25f, pop: 0.14f, fadeFrom: 0.5f, spin: -30f, rot: 15f);
                AddPart(parts, root, _burst, BloodRed, c, Vector2.zero, 0.15f, 2.6f, 0.6f, -0.3f, pop: 0.12f, fadeFrom: 0.45f, spin: 40f);
                AddPart(parts, root, _shock, BloodRed, c, Vector2.zero, 0.3f, 5.5f, 0.5f, -0.65f, pop: 0.4f, fadeFrom: 0.3f);
                AddSpeedLines(parts, root, c, FxMath.Rgba(1f, 0.8f, 0.8f), 30, 1f);

                for (int i = 0; i < 20; i++)
                {
                    float a = FxMath.Range(0f, 2f * FxMath.PI);
                    AddPart(parts, root, _disc, i % 3 == 0 ? FxMath.Rgba(1f, 0.35f, 0.4f) : BloodRed, c, FxMath.V2(FxMath.Cos(a), FxMath.Sin(a)) * FxMath.Range(3f, 6.5f) + FxMath.V2(0f, 1.5f),
                        FxMath.Range(0.12f, 0.24f), 0.08f, FxMath.Range(0.7f, 1f), -0.55f, gravity: 7f, drag: 0.3f, fadeFrom: 0.7f);
                }

                // しなびた体が崩れて、コウモリの群れが飛び立つ
                for (int i = 0; i < 30; i++)
                {
                    float a = FxMath.Range(0.1f, 0.9f) * FxMath.PI;
                    Vector2 v = FxMath.V2(FxMath.Cos(a), FxMath.Sin(a)) * FxMath.Range(3f, 5.5f);
                    AddPart(parts, root, _bat, FxMath.Rgba(0.1f, 0.03f, 0.08f), c + FxMath.V2(FxMath.Range(-0.2f, 0.2f), FxMath.Range(0f, 0.3f)), v, FxMath.Range(0.5f, 0.7f),
                        FxMath.Range(0.7f, 0.95f), 1.1f, -0.7f, drag: 0.5f, fadeFrom: 0.8f, delay: FxMath.Range(0f, 0.15f), flap: FxMath.Range(18f, 26f));
                }

                for (int i = 0; i < 8; i++)
                {
                    float a = FxMath.Range(0f, 2f * FxMath.PI);
                    AddPart(parts, root, _puff, FxMath.Rgba(0.35f, 0.08f, 0.12f), c, FxMath.V2(FxMath.Cos(a) * 1.2f, FxMath.Sin(a) * 0.5f + 0.4f), 0.3f, FxMath.Range(0.9f, 1.3f),
                        FxMath.Range(0.7f, 1f), -0.1f, drag: 1.5f, spin: FxMath.Range(-40f, 40f), fadeFrom: 0.5f);
                }

                break;
            }
            case Style.Poison:
            {
                // 倒れた瞬間に毒気が爆ぜる
                AddPart(parts, root, _burst, Tox, c, Vector2.zero, 0.2f, 3.2f, 0.6f, -0.3f, pop: 0.14f, fadeFrom: 0.45f, spin: 35f);
                AddPart(parts, root, _burst, ToxViolet, c, Vector2.zero, 0.2f, 4f, 0.7f, -0.25f, pop: 0.16f, fadeFrom: 0.5f, spin: -25f, rot: 20f);
                AddPart(parts, root, _shock, ToxPale, c, Vector2.zero, 0.3f, 5.5f, 0.5f, -0.65f, pop: 0.4f, fadeFrom: 0.3f);
                AddSpeedLines(parts, root, c, ToxPale, 26, 1f);

                for (int i = 0; i < 14; i++)
                {
                    float a = FxMath.Range(0f, 2f * FxMath.PI);
                    AddPart(parts, root, _puff, i % 2 == 0 ? FxMath.Rgba(0.35f, 0.85f, 0.2f, 0.85f) : FxMath.Rgba(0.55f, 0.18f, 0.85f, 0.85f), c,
                        FxMath.V2(FxMath.Cos(a) * 3.5f, FxMath.Sin(a) * 1.6f + 0.6f), 0.3f, FxMath.Range(1f, 1.5f), FxMath.Range(0.8f, 1.1f), -0.15f, drag: 2.2f,
                        spin: FxMath.Range(-60f, 60f), fadeFrom: 0.5f);
                }

                for (int i = 0; i < 16; i++)
                {
                    float a = FxMath.Range(0.05f, 0.95f) * FxMath.PI;
                    float sz = FxMath.Range(0.16f, 0.3f);
                    AddPart(parts, root, _ring, ToxPale, c, FxMath.V2(FxMath.Cos(a), FxMath.Sin(a)) * FxMath.Range(2.5f, 5f), sz, sz, FxMath.Range(0.6f, 0.9f), -0.5f,
                        gravity: 4f, drag: 0.8f, fadeFrom: 0.7f);
                }

                // 毒の水たまりが広がり、泡が弾け続ける
                AddPart(parts, root, _disc, FxMath.Rgba(0.35f, 0.85f, 0.2f, 0.85f), FxMath.V2(x, -0.72f), Vector2.zero, 0.3f, 2.2f, 1.05f, 0.05f, pop: 0.5f, fadeFrom: 0.85f,
                    squash: 0.28f);
                AddPart(parts, root, _ring, ToxPale, FxMath.V2(x, -0.72f), Vector2.zero, 0.3f, 2.6f, 0.6f, 0.04f, pop: 0.3f, fadeFrom: 0.4f, squash: 0.28f);

                for (int i = 0; i < 10; i++)
                {
                    Vector2 p = FxMath.V2(x + FxMath.Range(-0.8f, 0.8f), -0.72f + FxMath.Range(-0.15f, 0.15f));
                    float d = FxMath.Range(0.1f, 0.85f);
                    AddPart(parts, root, _ring, ToxPale, p, FxMath.V2(0f, 0.2f), 0.04f, 0.16f, 0.3f, -0.2f, delay: d, fadeFrom: 0.8f);
                    AddPart(parts, root, _burst, ToxPale, p + FxMath.V2(0f, 0.06f), Vector2.zero, 0.1f, 0.3f, 0.14f, -0.25f, delay: d + 0.3f, fadeFrom: 0.2f);
                }

                break;
            }
            default:
            {
                AddPart(parts, root, _burst, FxMath.Rgba(0.2f, 0.15f, 0.25f), c, Vector2.zero, 0.2f, 2.2f, 0.5f, -0.2f, pop: 0.12f, fadeFrom: 0.4f, spin: 30f);

                for (int i = 0; i < 6; i++)
                {
                    float a = FxMath.Range(0f, 2f * FxMath.PI);
                    AddPart(parts, root, _puff, FxMath.Rgba(0.25f, 0.2f, 0.3f, 0.8f), c, FxMath.V2(FxMath.Cos(a) * 1.4f, FxMath.Sin(a) * 0.5f + 0.3f), 0.3f, FxMath.Range(0.8f, 1.1f),
                        FxMath.Range(0.6f, 0.9f), -0.1f, drag: 1.8f, spin: FxMath.Range(-40f, 40f), fadeFrom: 0.5f);
                }

                break;
            }
        }
    }

    // 漫画の集中線: c の外側から放射状に、細い線が外へ走って消える
    private static void AddSpeedLines(List<Part> parts, Transform root, Vector2 c, Color color, int count, float r0)
    {
        for (int i = 0; i < count; i++)
        {
            float ang = FxMath.Range(0f, 360f);
            float rad = ang / FxMath.Rad2Deg;
            Vector2 d = FxMath.V2(FxMath.Cos(rad), FxMath.Sin(rad));
            float len = FxMath.Range(1.4f, 3f);
            float th = FxMath.Range(0.03f, 0.07f);
            AddPart(parts, root, _solid, color, c + d * (r0 + len * 0.5f + FxMath.Range(0f, 1.2f)), d * FxMath.Range(6f, 11f), len, len * 1.3f, FxMath.Range(0.18f, 0.3f), -0.85f,
                fadeFrom: 0.35f, rot: ang, squash: th / len);
        }
    }

    private static void AddSlash(List<Part> parts, Transform root, Vector2 at, float angle, Color color, float delay = 0f)
    {
        var go = new GameObject("Slash") { layer = Layer };
        go.transform.SetParent(root, false);
        SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = _solid;
        sr.color = color;
        go.transform.localPosition = FxMath.V3(at.x, at.y, -0.8f);
        go.transform.localRotation = FxMath.RotZ(angle);
        go.transform.localScale = FxMath.V3(0f, 0.12f, 1f);

        parts.Add(new Part
        {
            Sr = sr, Tf = go.transform, Pos = at, Rot = angle, S0 = 0.2f, S1 = 3.2f, Pop = 0.08f, Delay = delay, Life = 0.35f, FadeFrom = 0.3f, Z = -0.8f,
            Color = color, Squash = 0.04f
        });
    }

    private static void AddPart(List<Part> parts, Transform root, Sprite sprite, Color color, Vector2 pos, Vector2 vel, float s0, float s1, float life, float z,
        float gravity = 0f, float drag = 0f, float spin = 0f, float pop = 0f, float fadeFrom = 0.6f, float rot = 0f, float delay = 0f, float flap = 0f, float squash = 1f)
    {
        var go = new GameObject("fx") { layer = Layer };
        go.transform.SetParent(root, false);
        SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.color = FxMath.Rgba(color.r, color.g, color.b, 0f);
        go.transform.localPosition = FxMath.V3(pos.x, pos.y, z);
        go.transform.localScale = FxMath.V3(s0, s0 * squash, 1f);
        go.transform.localRotation = FxMath.RotZ(rot);

        parts.Add(new Part
        {
            Sr = sr, Tf = go.transform, Pos = pos, Vel = vel, Gravity = gravity, Drag = drag, Rot = rot, Spin = spin,
            S0 = s0, S1 = s1, Pop = pop, Delay = delay, Life = life, FadeFrom = fadeFrom, Z = z, Color = color, Flap = flap, Squash = squash
        });
    }

    private static void StepParts(List<Part> parts, float dt)
    {
        for (int i = parts.Count - 1; i >= 0; i--)
        {
            Part p = parts[i];
            if (!p.Sr) { parts.RemoveAt(i); continue; }

            if (p.Delay > 0f)
            {
                p.Delay -= dt;
                parts[i] = p;
                continue;
            }

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
            float sy = sc * p.Squash;
            float rot = p.Rot;

            // 羽ばたき: 縦を潰したり戻したりし、進む向きへ少し傾ける
            if (p.Flap > 0f)
            {
                sy = sc * (0.25f + 0.75f * FxMath.Abs(FxMath.Sin(p.Age * p.Flap)));
                rot = FxMath.Clamp(p.Vel.x * -6f, -25f, 25f);
            }

            p.Tf.localPosition = FxMath.V3(p.Pos.x, p.Pos.y, p.Z);
            p.Tf.localScale = FxMath.V3(sc, sy, 1f);
            p.Tf.localRotation = FxMath.RotZ(rot);

            Color c = p.Color;
            float fade = a < p.FadeFrom ? 1f : 1f - (a - p.FadeFrom) / (1f - p.FadeFrom);
            p.Sr.color = FxMath.Rgba(c.r, c.g, c.b, c.a * fade);
            parts[i] = p;
        }
    }

    private static PoolablePlayer SpawnCrew(Transform root, NetworkedPlayerInfo who, Vector2 pos, bool faceLeft)
    {
        try
        {
            if (!HudManager.Instance.IntroPrefab || !HudManager.Instance.IntroPrefab.PlayerPrefab) return null;

            PoolablePlayer crew = Object.Instantiate(HudManager.Instance.IntroPrefab.PlayerPrefab, root);
            crew.transform.localPosition = FxMath.V3(pos.x, pos.y, 0f);
            crew.transform.localScale = FxMath.V3(faceLeft ? -0.7f : 0.7f, 0.7f, 1f);
            crew.UpdateFromPlayerOutfit(who.DefaultOutfit, PlayerMaterial.MaskType.None, false, false);
            crew.ToggleName(false);
            crew.TogglePet(false);
            return crew;
        }
        catch (System.Exception e)
        {
            Utils.ThrowException(e);
            return null;
        }
    }

    private static void MakeSolid(Transform parent, Color color, float x, float y, float w, float h, float z)
    {
        var go = new GameObject("Solid") { layer = Layer };
        go.transform.SetParent(parent, false);
        SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = _solid;
        sr.color = color;
        go.transform.localPosition = FxMath.V3(x, y, z);
        go.transform.localScale = FxMath.V3(w, h, 1f);
    }

    private static void SetLayer(Transform t)
    {
        t.gameObject.layer = Layer;
        for (int i = 0; i < t.childCount; i++) SetLayer(t.GetChild(i));
    }

    // ── 素材 (白い塗り + 濃い縁取り。色は SpriteRenderer.color で塗る) ──

    private static void EnsureSprites()
    {
        if (!_disc) _disc = ExplosionKillOverlay.MakeOutlined(64, (_, _) => 0.95f, 0f);
        if (!_puff) _puff = ExplosionKillOverlay.MakeOutlined(96, (x, y) => 0.82f + 0.1f * FxMath.Abs(FxMath.Sin(FxMath.Atan2(y, x) * 2.5f + 0.3f)), 0.09f);

        if (!_burst)
        {
            _burst = ExplosionKillOverlay.MakeOutlined(96, (x, y) =>
            {
                float ang = FxMath.Atan2(y, x);
                return 0.45f + 0.48f * FxMath.Pow(FxMath.Abs(FxMath.Sin(ang * 4f)), 0.7f);
            }, 0f);
        }

        if (!_bat) _bat = MakeTex(128, ExplosionFx.BatAlpha);

        // 泡: 縁だけ白い輪と、左上の小さなつや
        if (!_ring)
        {
            _ring = MakeTex(64, (x, y) =>
            {
                float d = FxMath.Sqrt(x * x + y * y);
                float rim = FxMath.Clamp01(1f - FxMath.Abs(d - 0.82f) / 0.12f);
                float hx = x + 0.35f, hy = y - 0.35f;
                float gloss = FxMath.Clamp01(1f - FxMath.Sqrt(hx * hx + hy * hy) / 0.16f);
                float inner = d < 0.82f ? 0.18f : 0f;
                return FxMath.Max(FxMath.Max(rim, gloss), inner);
            });
        }

        if (!_solid) _solid = MakeTex(4, (_, _) => 1f);

        // 衝撃波: 細い輪
        if (!_shock) _shock = MakeTex(128, (x, y) => FxMath.Clamp01(1f - FxMath.Abs(FxMath.Sqrt(x * x + y * y) - 0.9f) / 0.06f));
    }

    // alpha(x, y) は中心を 0・縁を ±1 とした座標での不透明度
    private static Sprite MakeTex(int size, System.Func<float, float, float> alpha)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        var pixels = new Color[size * size];
        float h = (size - 1) * 0.5f;

        for (int py = 0; py < size; py++)
        {
            for (int px = 0; px < size; px++)
                pixels[py * size + px] = FxMath.Rgba(1f, 1f, 1f, alpha((px - h) / h, (py - h) / h));
        }

        tex.SetPixels(pixels);
        tex.Apply(false, true);
        tex.hideFlags |= HideFlags.HideAndDontSave;

        Sprite sprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size, 0, SpriteMeshType.FullRect);
        sprite.hideFlags |= HideFlags.HideAndDontSave;
        return sprite;
    }
}
