using System.Collections;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using TMPro;
using UnityEngine;

namespace EndKnot.Modules.Audience;

// 視聴者の干渉が起きた瞬間のカットイン。斜めの帯が開き、干渉を起こした視聴者のクルー (観客席と同じ見た目) が
// 滑り込んで干渉名を叩き付ける。種別ごとに帯の色と添え物 (呪印・光の柱・明滅・砂嵐・シャッター …) を変える。
// HUD カメラの子に置いた SpriteRenderer と TextMeshPro だけで描き、描くのは手元の画面だけ
internal static partial class AudienceCutIn
{
    private const int Layer = 5;
    private const float OpenTime = 0.15f;
    private const float CloseLen = 0.3f;
    private const int SpeedLineCount = 16;
    private const int CloudCount = 8;
    private const int StarCount = 10;

    private static Sprite _white;
    private static GameObject _root;
    private static PoolablePlayer _crew;

    private sealed class Style
    {
        public float Duration = 1.6f;
        public float Dim = 0.35f;
        public float Tilt = 7f;
        public float BandH = 1.55f;
        public float LineSpeed = 1f;
        public bool Soft;
    }

    private static Style StyleFor(string kindKey) => kindKey switch
    {
        "Blackout" => new Style { Dim = 0.5f },
        "Curse" => new Style { Dim = 0.45f, Tilt = -6f, LineSpeed = 0.45f },
        "Bless" => new Style { Dim = 0.12f, Tilt = 4f, LineSpeed = 0.6f, Soft = true },
        "Voice" => new Style { Duration = 3.5f, Dim = 0.25f, Tilt = 4f, BandH = 1.7f, LineSpeed = 0.5f, Soft = true },
        "Doors" => new Style { Tilt = 0f },
        "Comms" => new Style { Tilt = 5f, LineSpeed = 1.4f },
        "Earthquake" => new Style { Tilt = -8f },
        "ShuffleOne" or "ShuffleAll" => new Style { Tilt = -5f, Soft = true },
        _ => new Style()
    };

    // 文字列は呼び出し側でタグを無効化済みのものを受け取る (TMP はリッチテキストを解釈する)
    public static void Show(string kindKey, string header, string kindLine, string byLine, string message, string author, Color theme)
    {
        if (!HudManager.InstanceExists || GameStates.IsMeeting) return;
        if (ExplosionKillOverlay.Showing || CannonKillOverlay.Showing || HudManager.Instance.KillOverlay && HudManager.Instance.KillOverlay.IsOpen) return;

        IEnumerator co = HasLayout(kindKey)
            ? CoLayout(kindKey, header, kindLine, byLine, message, author, theme)
            : CoShow(kindKey, header, kindLine, byLine, message, author, theme);
        HudManager.Instance.StartCoroutine(CoGuarded(co).WrapToIl2Cpp());
    }

    // 組み立ての途中や動かしている途中で例外が出ても、作りかけを画面に残さない
    private static IEnumerator CoGuarded(IEnumerator inner)
    {
        bool first = true;
        GameObject mine = null;
        PoolablePlayer mineCrew = null;
        while (true)
        {
            bool moved;
            try { moved = inner.MoveNext(); }
            catch (System.Exception e)
            {
                Utils.ThrowException(e);
                moved = false;
                if (first)
                {
                    Close(_root, _crew);
                    _root = null;
                    _crew = null;
                }
                // 後から来た別のカットインは消さない
                else if (mine != null && ReferenceEquals(_root, mine))
                {
                    Close(mine, mineCrew);
                    _root = null;
                    _crew = null;
                }
            }

            if (first)
            {
                mine = _root;
                mineCrew = _crew;
            }

            first = false;
            if (!moved) yield break;
            yield return inner.Current;
        }
    }

    private static bool ShouldStop() => (!GameStates.InGame && !GameStates.IsLobby) || GameStates.IsMeeting || ExileController.Instance;

    private static IEnumerator CoShow(string kindKey, string header, string kindLine, string byLine, string message, string author, Color theme)
    {
        Transform cam = HudManager.Instance.transform.parent;
        if (!cam) yield break;

        Close(_root, _crew);
        _root = null;
        _crew = null;
        EnsureSprite();

        Style st = StyleFor(kindKey);
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

        (Sprite glowSp, Sprite raySp, Sprite starSp, Sprite ringSp, Sprite sigilSp, Sprite cloudSp) = ExplosionFx.BorrowLightSprites();

        // 光の重ね: 暗い下地 → 帯の色 → 明るい縁 → 白い核
        Color main = theme;
        Color light = FxMath.Rgba(FxMath.Lerp(theme.r, 1f, 0.55f), FxMath.Lerp(theme.g, 1f, 0.55f), FxMath.Lerp(theme.b, 1f, 0.55f));
        Color deep = st.Soft
            ? FxMath.Rgba(theme.r * 0.62f, theme.g * 0.62f, theme.b * 0.62f, 0.9f)
            : FxMath.Rgba(theme.r * 0.24f, theme.g * 0.24f, theme.b * 0.24f, 0.94f);

        // 波動砲のカットインと同時に出たら波動砲を手前にする
        GameObject root = _root = new GameObject("AudienceCutIn") { layer = Layer };
        root.transform.SetParent(cam, false);
        root.transform.localPosition = FxMath.V3(0f, 0f, -880f);
        Transform rt = root.transform;

        SpriteRenderer dim = Quad(rt, "Dim", FxMath.Rgba(0f, 0f, 0f, 0f), 0f, 0f, halfW * 2.2f, halfH * 2.2f, 0.3f);
        SpriteRenderer flash = Quad(rt, "Flash", FxMath.Rgba(1f, 1f, 1f, 0f), 0f, 0f, halfW * 2.2f, halfH * 2.2f, -0.3f);

        var band = new GameObject("Band") { layer = Layer };
        Transform bt = band.transform;
        bt.SetParent(rt, false);
        float bandY = 0.35f;
        bt.localPosition = FxMath.V3(0f, bandY, 0f);
        bt.localRotation = FxMath.RotZ(st.Tilt);
        bt.localScale = FxMath.V3(1f, 0f, 1f);

        float bandW = halfW * 2.6f;
        float bh = st.BandH;

        // 帯の外へ漏れるにじみ
        var halos = new SpriteRenderer[3];
        if (glowSp)
        {
            for (int i = 0; i < 3; i++)
                halos[i] = Spr(bt, "Halo", glowSp, FxMath.Rgba(main.r, main.g, main.b, 0.38f), (i - 1) * bandW * 0.33f, 0f, bandW * 0.5f, bh * 3f, 0.22f);
        }

        SpriteRenderer body = Quad(bt, "Body", deep, 0f, 0f, bandW, bh, 0.2f);
        Quad(bt, "Core", FxMath.Rgba(main.r, main.g, main.b, st.Soft ? 0.45f : 0.55f), 0f, 0f, bandW, bh * 0.45f, 0.15f);
        Quad(bt, "EdgeTop", light, 0f, bh * 0.5f, bandW, 0.08f, 0.1f);
        Quad(bt, "EdgeBottom", light, 0f, -bh * 0.5f, bandW, 0.08f, 0.1f);
        Quad(bt, "EdgeTopCore", FxMath.Rgba(1f, 1f, 1f, 0.85f), 0f, bh * 0.5f, bandW, 0.022f, 0.09f);
        Quad(bt, "EdgeBottomCore", FxMath.Rgba(1f, 1f, 1f, 0.85f), 0f, -bh * 0.5f, bandW, 0.022f, 0.09f);

        var lines = new Transform[SpeedLineCount];
        var lineX = new float[SpeedLineCount];
        var lineY = new float[SpeedLineCount];
        var lineSpeed = new float[SpeedLineCount];

        for (int i = 0; i < SpeedLineCount; i++)
        {
            float y = lineY[i] = FxMath.Range(-bh * 0.45f, bh * 0.45f);
            lineX[i] = FxMath.Range(-bandW * 0.5f, bandW * 0.5f);
            lineSpeed[i] = FxMath.Range(28f, 46f) * st.LineSpeed;
            Color col = i % 3 == 0 ? light : FxMath.Rgba(1f, 1f, 1f, 0.75f);
            lines[i] = Quad(bt, "Line", col, lineX[i], y, FxMath.Range(0.8f, 2.6f), FxMath.Range(0.025f, 0.06f), 0.05f).transform;
        }

        float crewRest = -halfW * 0.55f;
        float textRest = halfW * 0.2f;

        // ---- 種別ごとの添え物 ----
        var sig = new Sig();
        BuildSignature(sig, kindKey, rt, bt, halfW, halfH, bh, textRest, main, light, deep, glowSp, raySp, starSp, ringSp, sigilSp, cloudSp);

        // ---- 干渉を起こした視聴者のクルー ----
        PoolablePlayer crew = null;
        SpriteRenderer crewGlow = null;

        try
        {
            if (!string.IsNullOrEmpty(author) && HudManager.Instance.IntroPrefab && HudManager.Instance.IntroPrefab.PlayerPrefab)
            {
                if (glowSp) crewGlow = Spr(bt, "CrewGlow", glowSp, FxMath.Rgba(light.r, light.g, light.b, 0f), -halfW * 1.3f, -0.2f, 2.6f, 2.6f, -0.02f);
                crew = Object.Instantiate(HudManager.Instance.IntroPrefab.PlayerPrefab, bt);
                crew.UpdateFromPlayerOutfit(YouTubeChat.AudienceSeats.OutfitFor(author), PlayerMaterial.MaskType.None, false, false);
                crew.ToggleName(false);
                crew.TogglePet(false);
                crew.transform.localScale = FxMath.V3(1.05f, 1.05f, 1f);
                crew.transform.localPosition = FxMath.V3(-halfW * 1.3f, -0.35f, -0.05f);
            }
        }
        catch (System.Exception e) { Utils.ThrowException(e); }

        _crew = crew;

        // ---- 文字 ----
        int len = FxMath.Max(1, kindLine.Length);
        float titleSize = FxMath.Clamp(73f / len, 3.2f, 6.5f);
        TextMeshPro titleShadow = Text(bt, "TitleShadow", kindLine, titleSize, FxMath.Rgba(deep.r * 0.5f, deep.g * 0.5f, deep.b * 0.5f, 0.95f), halfW * 2f, true, false);
        TextMeshPro title = Text(bt, "Title", kindLine, titleSize, FxMath.Rgba(1f, 1f, 1f), halfW * 2f, true, false);
        TextMeshPro head = Text(bt, "Header", header, 2.2f, light, halfW * 2f, true, false);
        TextMeshPro by = Text(bt, "By", byLine, FxMath.Clamp(52f / FxMath.Max(1, byLine.Length), 1.4f, 2.4f), FxMath.Rgba(1f, 1f, 1f, 0.92f), halfW * 2f, false, false);

        TextMeshPro msgShadow = null, msg = null;
        if (!string.IsNullOrEmpty(message))
        {
            msgShadow = Text(rt, "MessageShadow", message, 3.0f, FxMath.Rgba(0f, 0f, 0f, 0.9f), halfW * 1.6f, false, true);
            msg = Text(rt, "Message", message, 3.0f, FxMath.Rgba(1f, 0.97f, 0.85f), halfW * 1.6f, false, true);
        }

        SetLayer(rt);

        bool hasCrew = crew;
        bool hasCrewGlow = crewGlow;
        float dur = st.Duration;
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
            float bandScale = openE * (1f - close * close);

            float jx = 0f, jy = 0f;
            UpdateSignature(sig, kindKey, t, dt, fade, openE, halfW, halfH, ref jx, ref jy);

            bt.localScale = FxMath.V3(1f, bandScale, 1f);
            bt.localPosition = FxMath.V3(jx, bandY + jy, 0f);

            dim.color = FxMath.Rgba(0f, 0f, 0f, st.Dim * openE * fade);
            flash.color = t < 0.14f ? FxMath.Rgba(light.r, light.g, light.b, 0.55f * (1f - t / 0.14f)) : FxMath.Rgba(1f, 1f, 1f, 0f);

            for (int i = 0; i < 3; i++)
                if (halos[i]) halos[i].color = FxMath.Rgba(main.r, main.g, main.b, (0.34f + 0.06f * FxMath.Sin(t * 7f + i * 2f)) * fade);

            for (int i = 0; i < SpeedLineCount; i++)
            {
                lineX[i] -= lineSpeed[i] * dt;
                if (lineX[i] < -bandW * 0.5f) lineX[i] += bandW;
                lines[i].localPosition = FxMath.V3(lineX[i], lineY[i], 0.05f);
            }

            // 滑り込んで止まり、あとはゆっくり流れる
            float slide = FxMath.Clamp01((t - 0.04f) / 0.18f);
            float slideE = 1f - (1f - slide) * (1f - slide) * (1f - slide);

            if (hasCrew)
            {
                float x = FxMath.Lerp(-halfW * 1.3f, crewRest, slideE) + t * 0.22f;
                // 着いた瞬間に一度跳ねる
                float hop = slide >= 1f ? FxMath.Abs(FxMath.Sin(FxMath.Min(t - 0.22f, 0.5f) * 6.28f)) * 0.18f * (1f - FxMath.Clamp01((t - 0.22f) / 0.5f)) : 0f;
                crew.transform.localPosition = FxMath.V3(x, -0.35f + hop, -0.05f);

                if (hasCrewGlow)
                {
                    crewGlow.transform.localPosition = FxMath.V3(x, -0.2f, -0.02f);
                    crewGlow.color = FxMath.Rgba(light.r, light.g, light.b, 0.55f * slideE * fade);
                }
            }

            {
                float x = FxMath.Lerp(halfW * 1.3f, textRest, slideE) - t * 0.28f;
                float punch = t >= 0.14f ? 1f + 0.6f * (1f - FxMath.Clamp01((t - 0.14f) / 0.14f)) : 1.6f;
                if (title)
                {
                    title.transform.localPosition = FxMath.V3(x, 0.2f, -0.1f);
                    title.transform.localScale = FxMath.V3(punch, punch, 1f);
                    title.alpha = fade;
                }

                if (titleShadow)
                {
                    titleShadow.transform.localPosition = FxMath.V3(x + 0.07f, 0.13f, -0.08f);
                    titleShadow.transform.localScale = FxMath.V3(punch, punch, 1f);
                    titleShadow.alpha = 0.95f * fade;
                }

                if (head)
                {
                    float hx = FxMath.Lerp(-halfW * 1.3f, textRest - 0.4f, slideE);
                    head.transform.localPosition = FxMath.V3(hx, bh * 0.5f + 0.24f, -0.1f);
                    head.alpha = fade;
                }

                if (by)
                {
                    float ba = FxMath.Clamp01((t - 0.3f) / 0.2f);
                    by.transform.localPosition = FxMath.V3(x + 0.3f, -bh * 0.26f, -0.1f);
                    by.alpha = ba * fade;
                }
            }

            if (msg)
            {
                float ma = FxMath.Clamp01((t - 0.45f) / 0.3f) * fade;
                float my = -halfH * 0.55f + (1f - ma) * -0.2f;
                msg.transform.localPosition = FxMath.V3(0f, my, -0.1f);
                msg.alpha = ma;
                if (msgShadow)
                {
                    msgShadow.transform.localPosition = FxMath.V3(0.05f, my - 0.05f, -0.08f);
                    msgShadow.alpha = 0.9f * ma;
                }
            }

            yield return null;
        }

        if (_root == root)
        {
            Close(root, crew);
            _root = null;
            _crew = null;
        }
        else if (root) Close(root, crew);
    }

    // ---- 種別ごとの添え物 ----

    private sealed class Sig
    {
        public Color Main, Light;
        public SpriteRenderer Sigil, Sigil2, Black, Slash, Impact;
        public SpriteRenderer[] Vignette;
        public SpriteRenderer[] Rays;
        public float[] RayBase;
        public SpriteRenderer[] Stars;
        public SpriteRenderer[] Rings;
        public SpriteRenderer[] Clouds;
        public Vector2[] CloudFrom, CloudTo;
        public Color CloudColor;
        public SpriteRenderer[] Glitch;
        public Transform[] Slabs;
        public SpriteRenderer[] Streaks, StreakHeads;
        public Vector2[] StreakFrom;
        public float[] StreakDelay;
        public float Seed;
    }

    private static void BuildSignature(Sig s, string kind, Transform rt, Transform bt, float halfW, float halfH, float bh, float textRest, Color main, Color light, Color deep,
        Sprite glowSp, Sprite raySp, Sprite starSp, Sprite ringSp, Sprite sigilSp, Sprite cloudSp)
    {
        s.Main = main;
        s.Light = light;
        s.Seed = FxMath.Range(0f, 100f);

        switch (kind)
        {
            case "Curse":
                if (sigilSp)
                {
                    s.Sigil = Spr(bt, "Sigil", sigilSp, FxMath.Rgba(main.r, main.g, main.b, 0f), textRest, 0f, 3.2f, 3.2f, 0.12f);
                    s.Sigil2 = Spr(rt, "SigilBig", sigilSp, FxMath.Rgba(main.r * 0.7f, main.g * 0.7f, main.b * 0.7f, 0f), 0f, 0.35f, halfH * 2.4f, halfH * 2.4f, 0.26f);
                }

                Vignette(s, rt, glowSp, halfW, halfH);
                Clouds(s, rt, cloudSp, halfW, halfH, FxMath.Rgba(main.r * 0.35f, main.g * 0.2f, main.b * 0.45f, 0f), false);
                break;

            case "Reactor":
                Vignette(s, rt, glowSp, halfW, halfH);
                break;

            case "Bless":
            case "Voice":
                if (raySp)
                {
                    int n = kind == "Voice" ? 3 : 5;
                    s.Rays = new SpriteRenderer[n];
                    s.RayBase = new float[n];
                    for (int i = 0; i < n; i++)
                    {
                        float x = kind == "Voice" ? (i - 1) * 0.9f : (i - 2) * halfW * 0.42f + FxMath.Range(-0.3f, 0.3f);
                        float len = kind == "Voice" ? halfH * 2.6f : halfH * 2.3f;
                        s.RayBase[i] = -90f + (kind == "Voice" ? (i - 1) * 4f : FxMath.Range(-8f, 8f));
                        s.Rays[i] = Spr(rt, "Ray", raySp, FxMath.Rgba(1f, 0.93f, 0.6f, 0f), x, halfH + 0.3f, len, len * (kind == "Voice" && i == 1 ? 2.4f : 1.6f), 0.27f);
                        s.Rays[i].transform.localRotation = FxMath.RotZ(s.RayBase[i]);
                    }
                }

                Stars(s, bt, starSp, halfW, bh, light);
                if (kind == "Voice") Rings(s, rt, ringSp, light);
                break;

            case "ShuffleOne":
            case "ShuffleAll":
                Stars(s, bt, starSp, halfW, bh, light);
                Rings(s, rt, ringSp, light);
                break;

            case "Blackout":
                s.Black = Quad(rt, "Black", FxMath.Rgba(0f, 0f, 0f, 0f), 0f, 0f, halfW * 2.2f, halfH * 2.2f, -0.25f);
                Vignette(s, rt, glowSp, halfW, halfH);
                break;

            case "Comms":
                s.Glitch = new SpriteRenderer[5];
                for (int i = 0; i < s.Glitch.Length; i++)
                    s.Glitch[i] = Quad(rt, "Glitch", FxMath.Rgba(1f, 1f, 1f, 0f), 0f, 0f, halfW * 2.2f, 0.1f, -0.2f);
                break;

            case "Doors":
                s.Slabs = new Transform[2];
                for (int side = 0; side < 2; side++)
                {
                    var slab = new GameObject("Slab") { layer = Layer };
                    slab.transform.SetParent(rt, false);
                    float dir = side == 0 ? -1f : 1f;
                    slab.transform.localPosition = FxMath.V3(dir * halfW * 2f, 0f, 0.24f);
                    Transform st = slab.transform;
                    Quad(st, "Metal", FxMath.Rgba(0.11f, 0.11f, 0.13f, 0.96f), 0f, 0f, halfW, halfH * 2.2f, 0f);
                    for (int k = 0; k < 8; k++)
                        Quad(st, "Rib", FxMath.Rgba(0f, 0f, 0f, 0.4f), 0f, -halfH + k * halfH * 0.28f, halfW, 0.06f, -0.01f);
                    // 合わせ目の側だけ光る
                    Quad(st, "Edge", light, -dir * halfW * 0.5f, 0f, 0.07f, halfH * 2.2f, -0.02f);
                    Quad(st, "EdgeCore", FxMath.Rgba(1f, 1f, 1f, 0.9f), -dir * halfW * 0.5f, 0f, 0.02f, halfH * 2.2f, -0.03f);
                    s.Slabs[side] = st;
                }

                break;

            case "Meteor":
                if (raySp && glowSp)
                {
                    const int n = 3;
                    s.Streaks = new SpriteRenderer[n];
                    s.StreakHeads = new SpriteRenderer[n];
                    s.StreakFrom = new Vector2[n];
                    s.StreakDelay = new float[n];
                    for (int i = 0; i < n; i++)
                    {
                        s.StreakFrom[i] = FxMath.V2(halfW * (0.4f + i * 0.35f), halfH + 1f);
                        s.StreakDelay[i] = 0.05f + i * 0.14f;
                        // 尾は進む向きの逆 (右上) へ伸びる
                        s.Streaks[i] = Spr(rt, "Streak", raySp, FxMath.Rgba(1f, 0.55f, 0.12f, 0f), 99f, 99f, 4.5f, 4.5f, 0.25f);
                        s.Streaks[i].transform.localRotation = FxMath.RotZ(50f);
                        s.StreakHeads[i] = Spr(rt, "Head", glowSp, FxMath.Rgba(1f, 0.85f, 0.5f, 0f), 99f, 99f, 1.1f, 1.1f, 0.24f);
                    }
                }

                s.Impact = Quad(rt, "Impact", FxMath.Rgba(1f, 0.45f, 0.1f, 0f), 0f, 0f, halfW * 2.2f, halfH * 2.2f, -0.22f);
                break;

            case "Earthquake":
                Clouds(s, rt, cloudSp, halfW, halfH, FxMath.Rgba(0.42f, 0.33f, 0.22f, 0f), true);
                break;

            case "FakeBody":
                s.Slash = Quad(bt, "Slash", FxMath.Rgba(0.8f, 0.05f, 0.05f, 0.95f), 0f, -0.02f, 0f, 0.07f, -0.12f);
                break;
        }
    }

    private static void UpdateSignature(Sig s, string kind, float t, float dt, float fade, float openE, float halfW, float halfH, ref float jx, ref float jy)
    {
        Color m = s.Main, l = s.Light;

        switch (kind)
        {
            case "Curse":
            {
                float grow = 1f - (1f - FxMath.Clamp01(t / 0.5f)) * (1f - FxMath.Clamp01(t / 0.5f));
                if (s.Sigil)
                {
                    s.Sigil.transform.localRotation = FxMath.RotZ(-t * 55f);
                    float k = 3.2f * (0.7f + 0.3f * grow);
                    s.Sigil.transform.localScale = FxMath.V3(k, k, 1f);
                    s.Sigil.color = FxMath.Rgba(l.r, l.g, l.b, 0.55f * grow * fade);
                }

                if (s.Sigil2)
                {
                    s.Sigil2.transform.localRotation = FxMath.RotZ(t * 18f);
                    s.Sigil2.color = FxMath.Rgba(m.r, m.g, m.b, 0.22f * grow * fade);
                }

                UpdateVignette(s, m, 0.5f + 0.08f * FxMath.Sin(t * 5f), openE * fade);
                UpdateClouds(s, t, fade, 0.42f);
                break;
            }

            case "Reactor":
                UpdateVignette(s, FxMath.Rgba(1f, 0.08f, 0.05f), 0.32f + 0.3f * (0.5f + 0.5f * FxMath.Sin(t * 9f)), openE * fade);
                break;

            case "Blackout":
            {
                // 落電の明滅は冒頭だけ
                bool off = t < 0.42f && Hash(FxMath.Floor(t * 18f) + s.Seed) < 0.45f;
                if (s.Black) s.Black.color = FxMath.Rgba(0f, 0f, 0f, off ? 0.85f : 0f);
                UpdateVignette(s, m, 0.3f, openE * fade);
                break;
            }

            case "Bless":
            case "Voice":
                if (s.Rays != null)
                {
                    float grow = FxMath.Clamp01(t / 0.45f);
                    for (int i = 0; i < s.Rays.Length; i++)
                    {
                        s.Rays[i].transform.localRotation = FxMath.RotZ(s.RayBase[i] + FxMath.Sin(t * 1.3f + i * 1.7f) * 3f);
                        float a = (kind == "Voice" ? 0.5f : 0.55f) + 0.1f * FxMath.Sin(t * 3f + i);
                        s.Rays[i].color = FxMath.Rgba(1f, 0.93f, 0.6f, a * grow * fade);
                    }
                }

                UpdateStars(s, t, fade);
                if (kind == "Voice") UpdateRings(s, t, fade, 0.9f);
                break;

            case "ShuffleOne":
            case "ShuffleAll":
                UpdateStars(s, t, fade);
                UpdateRings(s, t, fade, 1.4f);
                break;

            case "Comms":
            {
                float frame = FxMath.Floor(t * 14f);
                if (t < 1.1f && Hash(frame + s.Seed) < 0.4f) jx = (Hash(frame + s.Seed + 3.1f) - 0.5f) * 0.7f;

                for (int i = 0; i < s.Glitch.Length; i++)
                {
                    SpriteRenderer g = s.Glitch[i];
                    float y = (Hash(frame + i * 7.3f + s.Seed) - 0.5f) * halfH * 2f;
                    float h = 0.04f + Hash(frame + i + 0.5f + s.Seed) * 0.22f;
                    g.transform.localPosition = FxMath.V3((Hash(frame + i + 9.7f) - 0.5f) * 0.8f, y, -0.2f);
                    g.transform.localScale = FxMath.V3(halfW * 2.2f, h, 1f);
                    g.color = i % 2 == 0 ? FxMath.Rgba(1f, 1f, 1f, 0.1f * fade) : FxMath.Rgba(m.r, m.g, m.b, 0.22f * fade);
                }

                break;
            }

            case "Doors":
            {
                // 叩き付けて閉まり、少し見せて開く
                float closeP = t < 0.5f ? FxMath.Clamp01((t - 0.03f) / 0.2f) : 1f - FxMath.Clamp01((t - 0.5f) / 0.32f);
                float e = t < 0.5f ? closeP * closeP : 1f - (1f - closeP) * (1f - closeP);
                for (int side = 0; side < 2; side++)
                {
                    float dir = side == 0 ? -1f : 1f;
                    float x = FxMath.Lerp(dir * halfW * 1.6f, dir * halfW * 0.5f, e);
                    s.Slabs[side].localPosition = FxMath.V3(x, 0f, 0.24f);
                }

                // 閉まり切った瞬間の衝撃
                if (t >= 0.23f && t < 0.4f) jy = FxMath.Sin(t * 90f) * 0.08f * (1f - (t - 0.23f) / 0.17f);
                break;
            }

            case "Meteor":
                if (s.Streaks != null)
                {
                    for (int i = 0; i < s.Streaks.Length; i++)
                    {
                        float p = (t - s.StreakDelay[i]) / 0.55f;
                        bool on = p is >= 0f and <= 1.15f;
                        float pp = FxMath.Clamp01(p);
                        Vector2 from = s.StreakFrom[i];
                        float x = from.x - pp * halfW * 1.9f, y = from.y - pp * halfH * 2.6f;
                        float a = on ? FxMath.Clamp01(1.15f - p) * fade : 0f;
                        s.Streaks[i].transform.localPosition = FxMath.V3(x, y, 0.25f);
                        s.Streaks[i].color = FxMath.Rgba(1f, 0.55f, 0.12f, 0.85f * a);
                        s.StreakHeads[i].transform.localPosition = FxMath.V3(x, y, 0.24f);
                        s.StreakHeads[i].color = FxMath.Rgba(1f, 0.85f, 0.5f, a);
                    }
                }

                if (s.Impact)
                {
                    float f = t >= 0.95f ? FxMath.Clamp01(1f - (t - 0.95f) / 0.4f) : 0f;
                    s.Impact.color = FxMath.Rgba(1f, 0.45f, 0.1f, 0.5f * f * fade);
                }

                break;

            case "Earthquake":
            {
                float amp = 0.16f * FxMath.Clamp01(1f - t / 1.3f);
                jx = FxMath.Sin(t * 47f) * amp;
                jy = FxMath.Cos(t * 39f) * amp;
                UpdateClouds(s, t, fade, 0.5f);
                break;
            }

            case "FakeBody":
                if (s.Slash)
                {
                    float sp = FxMath.Clamp01((t - 0.18f) / 0.2f);
                    sp = 1f - (1f - sp) * (1f - sp) * (1f - sp);
                    s.Slash.transform.localScale = FxMath.V3(halfW * 2.4f * sp, 0.07f, 1f);
                    s.Slash.color = FxMath.Rgba(0.8f, 0.05f, 0.05f, 0.95f * fade);
                }

                break;
        }
    }

    // 画面の四隅から染みる色
    private static void Vignette(Sig s, Transform rt, Sprite glowSp, float halfW, float halfH)
    {
        if (!glowSp) return;
        s.Vignette = new SpriteRenderer[4];
        for (int i = 0; i < 4; i++)
        {
            float x = (i % 2 == 0 ? -1f : 1f) * halfW, y = (i < 2 ? 1f : -1f) * halfH;
            s.Vignette[i] = Spr(rt, "Vignette", glowSp, FxMath.Rgba(0f, 0f, 0f, 0f), x, y, halfH * 3.2f, halfH * 3.2f, 0.28f);
        }
    }

    private static void UpdateVignette(Sig s, Color c, float alpha, float k)
    {
        if (s.Vignette == null) return;
        for (int i = 0; i < s.Vignette.Length; i++) s.Vignette[i].color = FxMath.Rgba(c.r, c.g, c.b, alpha * k);
    }

    // rise = 床から立ち上る土煙 / それ以外は帯から外へ這い出す靄
    private static void Clouds(Sig s, Transform rt, Sprite cloudSp, float halfW, float halfH, Color c, bool rise)
    {
        if (!cloudSp) return;
        s.CloudColor = c;
        s.Clouds = new SpriteRenderer[CloudCount];
        s.CloudFrom = new Vector2[CloudCount];
        s.CloudTo = new Vector2[CloudCount];
        for (int i = 0; i < CloudCount; i++)
        {
            float x = FxMath.Range(-halfW, halfW);
            if (rise)
            {
                s.CloudFrom[i] = FxMath.V2(x, -halfH - 0.8f);
                s.CloudTo[i] = FxMath.V2(x + FxMath.Range(-0.6f, 0.6f), -halfH + FxMath.Range(0.8f, 1.8f));
            }
            else
            {
                float up = i % 2 == 0 ? 1f : -1f;
                s.CloudFrom[i] = FxMath.V2(x, 0.35f + up * 0.4f);
                s.CloudTo[i] = FxMath.V2(x + FxMath.Range(-0.8f, 0.8f), 0.35f + up * FxMath.Range(1.6f, 2.6f));
            }

            float size = FxMath.Range(2f, 3.2f);
            s.Clouds[i] = Spr(rt, "Cloud", cloudSp, c, s.CloudFrom[i].x, s.CloudFrom[i].y, size, size * 0.8f, 0.26f);
            s.Clouds[i].transform.localRotation = FxMath.RotZ(FxMath.Range(0f, 360f));
        }
    }

    private static void UpdateClouds(Sig s, float t, float fade, float alpha)
    {
        if (s.Clouds == null) return;
        for (int i = 0; i < s.Clouds.Length; i++)
        {
            float p = FxMath.Clamp01((t - i * 0.04f) / 1.1f);
            float e = 1f - (1f - p) * (1f - p);
            Vector2 a = s.CloudFrom[i], b = s.CloudTo[i];
            SpriteRenderer sr = s.Clouds[i];
            sr.transform.localPosition = FxMath.V3(FxMath.Lerp(a.x, b.x, e), FxMath.Lerp(a.y, b.y, e), 0.26f);
            Color c = s.CloudColor;
            sr.color = FxMath.Rgba(c.r, c.g, c.b, alpha * FxMath.Clamp01(p * 4f) * fade);
        }
    }

    private static void Stars(Sig s, Transform bt, Sprite starSp, float halfW, float bh, Color light)
    {
        if (!starSp) return;
        s.Stars = new SpriteRenderer[StarCount];
        for (int i = 0; i < StarCount; i++)
        {
            float size = FxMath.Range(0.6f, 1.2f);
            // 帯の中より縁の上下の方が明るい床でも光って見える
            float y = (i % 2 == 0 ? 1f : -1f) * bh * FxMath.Range(0.45f, 0.75f);
            s.Stars[i] = Spr(bt, "Star", starSp, FxMath.Rgba(light.r, light.g, light.b, 0f), FxMath.Range(-halfW * 0.95f, halfW * 0.95f), y, size, size, -0.06f);
        }
    }

    private static void UpdateStars(Sig s, float t, float fade)
    {
        if (s.Stars == null) return;
        Color l = s.Light;
        for (int i = 0; i < s.Stars.Length; i++)
        {
            float tw = FxMath.Sin(t * 5.5f + i * 1.9f + s.Seed);
            s.Stars[i].color = FxMath.Rgba(FxMath.Lerp(l.r, 1f, 0.5f), FxMath.Lerp(l.g, 1f, 0.5f), FxMath.Lerp(l.b, 1f, 0.5f), FxMath.Clamp01(tw) * 0.9f * fade);
        }
    }

    private static void Rings(Sig s, Transform rt, Sprite ringSp, Color light)
    {
        if (!ringSp) return;
        s.Rings = new SpriteRenderer[3];
        for (int i = 0; i < 3; i++)
            s.Rings[i] = Spr(rt, "Ring", ringSp, FxMath.Rgba(light.r, light.g, light.b, 0f), 0f, 0.35f, 1f, 1f, 0.23f);
    }

    private static void UpdateRings(Sig s, float t, float fade, float speed)
    {
        if (s.Rings == null) return;
        Color l = s.Light;
        for (int i = 0; i < s.Rings.Length; i++)
        {
            float p = FxMath.Repeat(t * speed * 0.5f + i / 3f, 1f);
            float size = 1f + p * 9f;
            s.Rings[i].transform.localScale = FxMath.V3(size, size * 0.62f, 1f);
            s.Rings[i].color = FxMath.Rgba(l.r, l.g, l.b, (1f - p) * 0.5f * fade);
        }
    }

    // 毎フレーム乱数を引かず、時刻から形を決める
    private static float Hash(float n)
    {
        float v = FxMath.Sin(n * 12.9898f) * 43758.5453f;
        return v - FxMath.Floor(v);
    }

    // 次のカットインに割り込まれた時もここを通す (複製されたマテリアルを残さない)
    private static void Close(GameObject root, PoolablePlayer crew)
    {
        // プレイヤー色の設定で複製されたマテリアルは Renderer を壊しても残るので、先に消す。
        // 文字はキルボタンの文字の複製で、元と同じマテリアルを指しうるので触らない
        if (crew)
        {
            foreach (Renderer r in crew.GetComponentsInChildren<Renderer>(true))
            {
                if (!r) continue;

                foreach (Material m in r.sharedMaterials)
                    if (m && m.name.EndsWith("(Instance)")) Object.Destroy(m);
            }
        }

        if (root) Object.Destroy(root);
    }

    private static TextMeshPro Text(Transform parent, string name, string value, float size, Color color, float width, bool italic, bool wrap)
    {
        try
        {
            TextMeshPro src = HudManager.Instance.KillButton ? HudManager.Instance.KillButton.buttonLabelText : null;
            if (!src) return null;

            GameObject go = Object.Instantiate(src.gameObject, parent);
            go.DestroyTranslator();
            go.name = name;
            go.transform.localPosition = FxMath.V3(99f, 99f, -0.1f);
            go.transform.localRotation = FxMath.RotZ(0f);
            go.transform.localScale = FxMath.V3(1f, 1f, 1f);
            var text = go.GetComponent<TextMeshPro>();
            text.enableAutoSizing = false;
            text.fontSize = size;
            text.enableWordWrapping = wrap;
            text.alignment = TextAlignmentOptions.Center;
            text.color = color;
            text.text = italic ? $"<i>{value}</i>" : value;
            go.GetComponent<RectTransform>().sizeDelta = FxMath.V2(width, wrap ? 2.5f : 2f);
            text.alpha = 0f;
            return text;
        }
        catch (System.Exception e)
        {
            Utils.ThrowException(e);
            return null;
        }
    }

    private static SpriteRenderer Quad(Transform parent, string name, Color color, float x, float y, float w, float h, float z)
        => Spr(parent, name, _white, color, x, y, w, h, z);

    private static SpriteRenderer Spr(Transform parent, string name, Sprite sprite, Color color, float x, float y, float w, float h, float z)
    {
        var go = new GameObject(name) { layer = Layer };
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

    // 1 × 1 ユニットの白い四角 (色と大きさは SpriteRenderer と scale で決める)
    private static void EnsureSprite()
    {
        if (_white) return;

        var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        var px = new Color32[16];
        for (int i = 0; i < px.Length; i++) px[i] = FxMath.Rgba32(255, 255, 255, 255);
        tex.SetPixels32(px);
        tex.Apply();
        tex.hideFlags = HideFlags.HideAndDontSave;
        _white = Sprite.Create(tex, new Rect(0f, 0f, 4f, 4f), FxMath.V2(0.5f, 0.5f), 4f);
        _white.hideFlags = HideFlags.HideAndDontSave;
    }
}
