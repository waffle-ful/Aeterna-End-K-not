using System.Collections;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using TMPro;
using UnityEngine;
using static EndKnot.Translator;

namespace EndKnot.Modules;

// 波動砲を撃った瞬間に画面を横切る必殺技のカットイン。帯と技名は全員の画面に出し、撃ち手のキャラは撃った本人の画面にだけ出す (正体が分からないように)
internal static class CannonCutIn
{
    private const float Duration = 1.15f;
    private const float OpenTime = 0.12f;
    private const float CloseFrom = 0.9f;
    private const float Tilt = 7f;
    private const float BandHeight = 1.55f;
    private const int SpeedLineCount = 16;
    private const int Layer = 5;

    private static Sprite _white;
    private static GameObject _root;
    private static PoolablePlayer _crew;

    public static void Show(byte shooterId, ExplosionFx.CannonTitle title, Color light, Color main, Color deep)
    {
        if (!HudManager.InstanceExists || GameStates.IsMeeting) return;
        if (ExplosionKillOverlay.Showing || CannonKillOverlay.Showing || HudManager.Instance.KillOverlay && HudManager.Instance.KillOverlay.IsOpen) return;

        HudManager.Instance.StartCoroutine(CoShow(shooterId, title, light, main, deep).WrapToIl2Cpp());
    }

    private static string TitleText(ExplosionFx.CannonTitle title)
    {
        return title switch
        {
            ExplosionFx.CannonTitle.SuperCannon => GetString("CannonCutIn.Super"),
            ExplosionFx.CannonTitle.BlackHole => GetString("SuperCannonTypeBlackHole"),
            ExplosionFx.CannonTitle.Twin => GetString("SuperCannonTypeTwin"),
            ExplosionFx.CannonTitle.Dynamic => GetString("SuperCannonTypeDynamic"),
            ExplosionFx.CannonTitle.CertainKill => GetString("SuperCannonTypeCertainKill"),
            _ => GetString("WaveCannon")
        };
    }

    private static IEnumerator CoShow(byte shooterId, ExplosionFx.CannonTitle title, Color light, Color main, Color deep)
    {
        Transform cam = HudManager.Instance.transform.parent;
        if (!cam) yield break;

        Close(_root, _crew);
        EnsureSprite();

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

        GameObject root = _root = new GameObject("CannonCutIn") { layer = Layer };
        root.transform.SetParent(cam, false);
        root.transform.localPosition = FxMath.V3(0f, 0f, -900f);

        SpriteRenderer dim = Quad(root.transform, "Dim", FxMath.Rgba(0f, 0f, 0f, 0f), 0f, 0f, halfW * 2.2f, halfH * 2.2f, 0.3f);
        SpriteRenderer flash = Quad(root.transform, "Flash", FxMath.Rgba(1f, 1f, 1f, 0f), 0f, 0f, halfW * 2.2f, halfH * 2.2f, -0.3f);

        var band = new GameObject("Band") { layer = Layer };
        band.transform.SetParent(root.transform, false);
        band.transform.localPosition = FxMath.V3(0f, 0.35f, 0f);
        band.transform.localRotation = FxMath.RotZ(Tilt);
        band.transform.localScale = FxMath.V3(1f, 0f, 1f);

        float bandW = halfW * 2.6f;
        Color deepA = deep;
        deepA.a = 0.93f;
        Color mainA = main;
        mainA.a = 0.55f;
        Quad(band.transform, "Body", deepA, 0f, 0f, bandW, BandHeight, 0.2f);
        Quad(band.transform, "Core", mainA, 0f, 0f, bandW, BandHeight * 0.45f, 0.15f);
        Quad(band.transform, "EdgeTop", light, 0f, BandHeight * 0.5f, bandW, 0.07f, 0.1f);
        Quad(band.transform, "EdgeBottom", light, 0f, -BandHeight * 0.5f, bandW, 0.07f, 0.1f);

        // 帯の中を流れる光の筋
        var lines = new SpriteRenderer[SpeedLineCount];
        var lineX = new float[SpeedLineCount];
        var lineY = new float[SpeedLineCount];
        var lineSpeed = new float[SpeedLineCount];

        for (int i = 0; i < SpeedLineCount; i++)
        {
            float y = lineY[i] = FxMath.Range(-BandHeight * 0.45f, BandHeight * 0.45f);
            lineX[i] = FxMath.Range(-bandW * 0.5f, bandW * 0.5f);
            lineSpeed[i] = FxMath.Range(28f, 46f);
            Color col = i % 3 == 0 ? light : FxMath.Rgba(1f, 1f, 1f, 0.8f);
            lines[i] = Quad(band.transform, "Line", col, lineX[i], y, FxMath.Range(0.8f, 2.6f), FxMath.Range(0.025f, 0.06f), 0.05f);
        }

        // 撃ち手のキャラは本人の画面だけ
        PoolablePlayer crew = null;
        PlayerControl lp = PlayerControl.LocalPlayer;

        try
        {
            if (lp && lp.PlayerId == shooterId && lp.Data && HudManager.Instance.IntroPrefab && HudManager.Instance.IntroPrefab.PlayerPrefab)
            {
                crew = Object.Instantiate(HudManager.Instance.IntroPrefab.PlayerPrefab, band.transform);
                crew.UpdateFromPlayerOutfit(lp.Data.DefaultOutfit, PlayerMaterial.MaskType.None, false, false);
                crew.ToggleName(false);
                crew.TogglePet(false);
                crew.transform.localScale = FxMath.V3(1.05f, 1.05f, 1f);
            }
        }
        catch (System.Exception e) { Utils.ThrowException(e); }

        TextMeshPro text = null;

        try
        {
            TextMeshPro src = HudManager.Instance.KillButton ? HudManager.Instance.KillButton.buttonLabelText : null;

            if (src)
            {
                GameObject go = Object.Instantiate(src.gameObject, band.transform);
                go.DestroyTranslator();
                go.name = "Title";
                text = go.GetComponent<TextMeshPro>();
                text.enableAutoSizing = false;
                text.fontSize = 6.5f;
                text.enableWordWrapping = false;
                text.alignment = TextAlignmentOptions.Center;
                text.color = FxMath.Rgba(1f, 1f, 1f);
                text.text = $"<i>{TitleText(title)}</i>";
                go.GetComponent<RectTransform>().sizeDelta = FxMath.V2(halfW * 2f, 2f);
            }
        }
        catch (System.Exception e) { Utils.ThrowException(e); }

        SetLayer(root.transform);
        _crew = crew;
        bool hasCrew = crew;
        bool hasText = text;
        KillOverlay ko = HudManager.Instance.KillOverlay;
        bool hasKo = ko;

        float crewRest = -halfW * 0.42f;
        float textRest = hasCrew ? halfW * 0.12f : 0f;
        float t = 0f;

        while (t < Duration)
        {
            if (!root || GameStates.IsMeeting || !GameStates.InGame) break;
            if (hasKo && ko.IsOpen) break;

            float dt = Time.deltaTime;
            t += dt;

            float open = FxMath.Clamp01(t / OpenTime);
            float close = t > CloseFrom ? FxMath.Clamp01((t - CloseFrom) / (Duration - CloseFrom)) : 0f;
            float openE = 1f - (1f - open) * (1f - open);
            float bandY = openE * (1f - close * close);
            band.transform.localScale = FxMath.V3(1f, bandY, 1f);

            // root が生きている間は子も生きている
            dim.color = FxMath.Rgba(0f, 0f, 0f, 0.35f * openE * (1f - close));
            flash.color = FxMath.Rgba(1f, 1f, 1f, t < 0.14f ? 0.55f * (1f - t / 0.14f) : 0f);

            for (int i = 0; i < SpeedLineCount; i++)
            {
                lineX[i] -= lineSpeed[i] * dt;
                if (lineX[i] < -bandW * 0.5f) lineX[i] += bandW;
                lines[i].transform.localPosition = FxMath.V3(lineX[i], lineY[i], 0.05f);
            }

            // 滑り込んで止まり、あとはゆっくり流れる
            float slide = FxMath.Clamp01((t - 0.04f) / 0.16f);
            float slideE = 1f - (1f - slide) * (1f - slide) * (1f - slide);

            if (hasCrew)
            {
                float x = FxMath.Lerp(-halfW * 1.3f, crewRest, slideE) + t * 0.25f;
                crew.transform.localPosition = FxMath.V3(x, -0.35f, -0.05f);
            }

            if (hasText)
            {
                float x = FxMath.Lerp(halfW * 1.3f, textRest, slideE) - t * 0.3f;
                float punch = 1f + 0.5f * (1f - FxMath.Clamp01((t - 0.12f) / 0.12f)) * (t >= 0.12f ? 1f : 0f);
                text.transform.localPosition = FxMath.V3(x, 0.02f, -0.1f);
                text.transform.localScale = FxMath.V3(punch, punch, 1f);
                text.alpha = 1f - close;
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

    // 次のカットインに割り込まれた時もここを通す (複製されたマテリアルを残さない)
    private static void Close(GameObject root, PoolablePlayer crew)
    {
        // プレイヤー色の設定で複製されたマテリアルは Renderer を壊しても残るので、先に消す。
        // 技名の文字はキルボタンの文字の複製で、元と同じマテリアルを指しうるので触らない
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

    private static SpriteRenderer Quad(Transform parent, string name, Color color, float x, float y, float w, float h, float z)
    {
        var go = new GameObject(name) { layer = Layer };
        go.transform.SetParent(parent, false);
        go.transform.localPosition = FxMath.V3(x, y, z);
        go.transform.localScale = FxMath.V3(w, h, 1f);
        SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = _white;
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
