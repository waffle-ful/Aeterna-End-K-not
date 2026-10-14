using UnityEngine;

namespace EndKnot.Modules;

// 配信の視聴者がゲームに干渉した時の現場の演出
public static partial class ExplosionFx
{
    private static readonly Color AudSpark = new(1f, 0.86f, 0.4f);
    private static readonly Color AudSparkHot = new(1f, 0.32f, 0.04f);
    private static readonly Color AudElecDeep = new(0.05f, 0.12f, 0.45f);
    private static readonly Color AudHolyAmber = new(0.9f, 0.5f, 0.08f);
    private static readonly Color AudElecBlue = new(0.4f, 0.7f, 1f);
    private static readonly Color AudSoot = new(0.1f, 0.1f, 0.13f);
    private static readonly Color AudAlarmRed = new(1f, 0.16f, 0.12f);
    private static readonly Color AudAlarmDeep = new(0.45f, 0.02f, 0.02f);
    private static readonly Color AudSteam = new(0.62f, 0.64f, 0.7f);
    private static readonly Color AudSteamDark = new(0.32f, 0.33f, 0.38f);
    private static readonly Color AudGlitchCyan = new(0.2f, 1f, 0.95f);
    private static readonly Color AudGlitchMagenta = new(1f, 0.2f, 0.85f);
    private static readonly Color AudGlitchDeep = new(0.05f, 0.08f, 0.2f);
    private static readonly Color AudCurseViolet = new(0.62f, 0.2f, 0.95f);
    private static readonly Color AudCurseDeep = new(0.16f, 0.02f, 0.26f);
    private static readonly Color AudCurseBlack = new(0.06f, 0.02f, 0.09f);
    private static readonly Color AudHolyGold = new(1f, 0.86f, 0.42f);
    private static readonly Color AudHolyPale = new(1f, 0.97f, 0.85f);
    private static readonly Color AudRock = new(0.42f, 0.37f, 0.33f);
    private static readonly Color AudRockDark = new(0.22f, 0.19f, 0.17f);
    private static readonly Color AudCrack = new(0.08f, 0.06f, 0.05f);

    // 知らない種類 (新しい版のホストが送ってきたもの) は何もしない
    private static void SpawnAudience(Request r)
    {
        switch (r.Kind)
        {
            case Kind.AudSurge:
                SpawnAudSurge(r.Pos);
                FxSound.At("FxAudSurge", r.Pos, 0.9f, farGain: 0.35f);
                break;
            case Kind.AudAlarm:
                SpawnAudAlarm(r.Pos);
                FxSound.At("FxAudAlarm", r.Pos, 0.9f, farGain: 0.35f);
                break;
            case Kind.AudStatic:
                SpawnAudStatic(r.Pos);
                FxSound.At("FxAudStatic", r.Pos, 0.9f, farGain: 0.3f);
                break;
            case Kind.AudDoorSlam:
                SpawnAudDoorSlam(r.Pos);
                FxSound.At("FxAudDoorSlam", r.Pos, 0.9f);
                break;
            case Kind.AudCurse:
                SpawnAudCurse(r.Pos);
                FxSound.At("FxAudCurse", r.Pos);
                break;
            case Kind.AudBless:
                SpawnAudBless(r.Pos);
                FxSound.At("FxAudBless", r.Pos);
                break;
            case Kind.AudVoice:
            {
                SpawnAudVoice();
                // 送られてくる位置は使わないので、こもり・響きは聞き手自身のいる場所で決める
                PlayerControl lp = PlayerControl.LocalPlayer;
                FxSound.At("FxAudVoice", lp ? lp.GetTruePosition() : r.Pos, 0.85f, everywhere: true);
                break;
            }
            // 地鳴りはカットシーンが鳴らすので、ここでは絵だけ
            case Kind.AudQuake:
                SpawnAudQuake();
                break;
        }
    }

    // 停電: 配電盤が青白く火を噴く (閃光・光の球・衝撃の輪) → 火花が弧を描いて床へ落ちる → 放電がしばらくはぜる → 黒い煤煙が昇る
    private static void SpawnAudSurge(Vector2 c)
    {
        float q = Active.Count > 1200 ? 0.5f : 1f;

        Impact(c, 4f, AudElecBlue, 0.35f, 0.1f, 0.3f);
        Add(Shape.Glow, c, Vector2.zero, 0.9f, 1f, 5f, AudElecDeep, AudElecDeep, 0.55f, 0.01f, 0.4f, order: 7);
        Add(Shape.Glow, c, Vector2.zero, 0.6f, 0.6f, 4.5f, BoltWhite, AudElecBlue, 1f, 0.01f, 0.25f, order: 8);
        Add(Shape.Star, c, Vector2.zero, 0.45f, 0.8f, 4f, BoltWhite, AudElecBlue, 1f, 0.01f, 0.3f, spin: 90f, order: 9);
        Add(Shape.Ring, c, Vector2.zero, 0.7f, 0.5f, 7f, BoltWhite, AudElecBlue, 0.9f, 0.01f, 0.3f);
        Add(Shape.Ring, c, Vector2.zero, 0.9f, 0.3f, 5f, AudElecBlue, BoltViolet, 0.6f, 0.01f, 0.3f, delay: 0.12f);

        for (int i = 0; i < 70 * q; i++)
        {
            float a = Rnd(-30f, 210f) * FxMath.Deg2Rad;
            Vector2 v = FxMath.V2(FxMath.Cos(a) * Rnd(1.5f, 5f), FxMath.Sin(a) * Rnd(1.5f, 5f) + 1.5f);
            Add(Shape.Star, c, v, Rnd(0.6f, 1.2f), Rnd(0.18f, 0.26f), 0.05f, i % 3 == 0 ? AudSpark : BlazeOrange, AudSparkHot, 1f, 0.01f, 0.6f,
                drag: 1.2f, rise: -4.5f, stretch: 0.14f, delay: Rnd(0f, 0.25f), order: 9);
        }

        // 配電盤のまわりで遅れてはぜる短い放電
        for (int i = 0; i < 22 * q; i++)
        {
            Vector2 a = Off(c, Rnd(-1.6f, 1.6f), Rnd(-1f, 1.2f));
            float ang = Rnd(0f, 2f * FxMath.PI);
            float l = Rnd(0.4f, 0.9f);
            int m = FxLines.Fractal(a, Off(a, FxMath.Cos(ang) * l, FxMath.Sin(ang) * l), 3, l * 0.25f, 0.5f, LineBuf2);
            FxLines.Spec sp = BoltSpec(0.05f, Rnd(0.05f, 1.6f), FxLines.Taper.Branch, 0, 0.1f, 0.12f);
            sp.UnderMul = 5f;
            Lines(LineBuf2, m, sp, 8);
        }

        for (int i = 0; i < 12 * q; i++)
        {
            Add(Shape.Cloud, Off(c, Rnd(-0.8f, 0.8f), Rnd(-0.3f, 0.5f)), FxMath.V2(Rnd(-0.3f, 0.3f), 0f), Rnd(1.8f, 2.6f), 0.4f, Rnd(1.4f, 2.2f), AudSoot, AudSoot, 0.6f, 0.08f, 0.45f,
                delay: Rnd(0.15f, 0.6f), spin: Rnd(-40f, 40f), rise: 0.7f, order: 6);
        }
    }

    // 原子炉: 赤い警報の光が 3 回脈打って床に広がる → 炉のまわりから蒸気が噴き出す → 赤い火花
    private static void SpawnAudAlarm(Vector2 c)
    {
        float q = Active.Count > 1200 ? 0.5f : 1f;

        Impact(c, 5f, AudAlarmRed, 0.25f, 0.12f, 0.4f);

        for (int k = 0; k < 3; k++)
        {
            float d = k * 0.7f;
            Add(Shape.Glow, c, Vector2.zero, 0.75f, 1.5f, 8f, AudAlarmRed, AudAlarmDeep, 0.55f, 0.08f, 0.35f, delay: d, sy0: 0.9f, sy1: 4f, rot: 0f, order: 1);
            Add(Shape.Ring, c, Vector2.zero, 0.8f, 0.8f, 9f, AudAlarmRed, AudAlarmDeep, 0.9f, 0.04f, 0.3f, delay: d, sy0: 0.4f, sy1: 4.5f, rot: 0f, order: 2);
            Add(Shape.Glow, c, Vector2.zero, 0.5f, 2.2f, 2.6f, WindWhite, AudAlarmRed, 0.7f, 0.05f, 0.3f, delay: d, order: 8);
        }

        // 蒸気: 炉のまわり 4 か所から斜め上へ噴く
        for (int j = 0; j < 4; j++)
        {
            float side = j % 2 == 0 ? -1f : 1f;
            Vector2 vent = Off(c, side * Rnd(1f, 2f), Rnd(-0.8f, 0.6f));
            float at = 0.2f + j * 0.35f;

            for (int i = 0; i < 9 * q; i++)
            {
                Add(Shape.Cloud, vent, FxMath.V2(side * Rnd(0.4f, 1.4f), Rnd(1.8f, 3.2f)), Rnd(0.9f, 1.4f), 0.25f, Rnd(1f, 1.6f), AudSteam, AudSteamDark, 0.75f, 0.04f, 0.4f,
                    drag: 1.6f, delay: at + Rnd(0f, 0.35f), spin: Rnd(-60f, 60f), order: 6);
            }
        }

        for (int i = 0; i < 40 * q; i++)
        {
            Add(Shape.Star, Off(c, Rnd(-1.5f, 1.5f), Rnd(-0.8f, 0.8f)), Dir() * Rnd(1f, 3.5f), Rnd(0.4f, 0.8f), Rnd(0.16f, 0.24f), 0.04f, BlazeOrange, AudAlarmRed, 1f, 0.01f, 0.5f,
                drag: 2f, rise: -2f, stretch: 0.1f, delay: Rnd(0f, 1.6f), order: 9);
        }
    }

    // 通信: 電波の輪が広がりながら途切れる → 青と桃色の四角いノイズが部屋に散って瞬く → 走査線のような横筋
    private static void SpawnAudStatic(Vector2 c)
    {
        float q = Active.Count > 1200 ? 0.5f : 1f;

        Add(Shape.Glow, c, Vector2.zero, 0.5f, 0.5f, 3.5f, WindWhite, AudGlitchCyan, 0.9f, 0.01f, 0.3f, order: 8);

        for (int k = 0; k < 4; k++)
        {
            Add(Shape.Ring, c, Vector2.zero, 0.7f, 0.4f, 5f + k, k % 2 == 0 ? AudGlitchCyan : AudGlitchMagenta, AudGlitchDeep, 0.85f, 0.02f, 0.4f, delay: k * 0.22f,
                twinkle: 0.8f, twinkleSpeed: 22f);
        }

        // ノイズの四角: 短く瞬くだけ (動かさない)。色は 2 色をずらして重ねる
        for (int i = 0; i < 90 * q; i++)
        {
            Vector2 p = Off(c, Rnd(-2.6f, 2.6f), Rnd(-1.6f, 1.8f));
            float w = Rnd(0.15f, 0.9f), h = Rnd(0.05f, 0.16f);
            float d = Rnd(0f, 1.5f), lf = Rnd(0.06f, 0.18f);
            Add(Shape.Solid, p, Vector2.zero, lf, w, w, AudGlitchCyan, AudGlitchCyan, 0.85f, 0.01f, 0.5f, delay: d, rot: 0f, sy0: h, sy1: h, order: 9);
            Add(Shape.Solid, Off(p, 0.06f, -0.03f), Vector2.zero, lf, w, w, AudGlitchMagenta, AudGlitchMagenta, 0.6f, 0.01f, 0.5f, delay: d, rot: 0f, sy0: h, sy1: h, order: 8);
            Add(Shape.Solid, Off(p, -0.04f, 0.04f), Vector2.zero, lf, w * 1.1f, w * 1.1f, AudGlitchDeep, AudGlitchDeep, 0.55f, 0.01f, 0.5f, delay: d, rot: 0f, sy0: h * 1.6f, sy1: h * 1.6f, order: 7);
        }

        for (int i = 0; i < 10 * q; i++)
        {
            float y = Rnd(-1.5f, 1.8f), w = Rnd(2f, 5f);
            Add(Shape.Solid, Off(c, Rnd(-0.8f, 0.8f), y), Vector2.zero, 0.12f, w, w, WindWhite, AudGlitchCyan, 0.5f, 0.01f, 0.4f, delay: Rnd(0f, 1.5f), rot: 0f, sy0: 0.025f, sy1: 0.025f,
                order: 9);
        }
    }

    // ドア: 扉が叩きつけられた所から土埃が横へ噴き、火花が散る
    private static void SpawnAudDoorSlam(Vector2 c)
    {
        Impact(c, 2f, WindWhite, 0.12f, 0.08f, 0.2f);
        FloorKick(c, 1f, AudRock, AudRockDark, 0.8f, 0f, 18);
        Add(Shape.Ring, c, Vector2.zero, 0.45f, 0.4f, 2.6f, AudRock, AudRockDark, 0.8f, 0.02f, 0.3f, rot: 0f, flat: true, order: 1);
        Add(Shape.Glow, c, Vector2.zero, 0.3f, 0.4f, 1.8f, WindWhite, BlazeYellow, 0.8f, 0.01f, 0.3f, order: 8);

        for (int i = 0; i < 18 * (Active.Count > 1200 ? 0.5f : 1f); i++)
        {
            Add(Shape.Star, c, Dir() * Rnd(1.5f, 3.5f), Rnd(0.3f, 0.6f), Rnd(0.07f, 0.11f), 0.02f, AudSpark, BlazeOrange, 1f, 0.01f, 0.5f, drag: 2.5f, rise: -2f, stretch: 0.1f,
                order: 9);
        }
    }

    // 呪い: 足元に紫の魔法陣が逆回りに開く → 黒紫の靄が体にまとわりついて昇る → 蝙蝠が数羽飛び立つ → 光が体へ吸い込まれる
    private static void SpawnAudCurse(Vector2 c)
    {
        float q = Active.Count > 1200 ? 0.5f : 1f;
        Vector2 f = Off(c, 0f, Feet.y);

        Impact(c, 2f, AudCurseViolet, 0.18f, 0.08f, 0.2f, 0.5f);
        Add(Shape.Glow, f, Vector2.zero, 2f, 1.2f, 2.6f, AudCurseDeep, AudCurseBlack, 0.7f, 0.1f, 0.7f, rot: 0f, flat: true, order: 0);
        Add(Shape.Sigil, f, Vector2.zero, 2f, 0.6f, 2f, AudCurseViolet, AudCurseDeep, 0.95f, 0.1f, 0.75f, spin: -80f, rot: 0f, flat: true, order: 1);
        Add(Shape.Ring, c, Vector2.zero, 0.6f, 3.2f, 0.3f, AudCurseViolet, AudCurseBlack, 0.9f, 0.05f, 0.5f, delay: 0.45f, order: 7);

        for (int i = 0; i < 26 * q; i++)
        {
            Add(Shape.Cloud, Off(c, Rnd(-0.45f, 0.45f), Rnd(-0.45f, 0.1f)), FxMath.V2(Rnd(-0.25f, 0.25f), 0f), Rnd(1f, 1.5f), 0.25f, Rnd(0.6f, 1f),
                i % 3 == 0 ? AudCurseViolet : AudCurseDeep, AudCurseBlack, 0.7f, 0.1f, 0.5f, delay: Rnd(0.3f, 1.2f), spin: Rnd(-80f, 80f), rise: Rnd(0.8f, 1.4f), order: 6);
        }

        for (int i = 0; i < 5 * q; i++)
        {
            float a = Rnd(30f, 150f) * FxMath.Deg2Rad;
            float s = Rnd(0.35f, 0.5f);
            Add(Shape.Bat, Off(c, 0f, 0.3f), FxMath.V2(FxMath.Cos(a) * Rnd(2f, 3f), FxMath.Sin(a) * Rnd(1.5f, 2.5f)), Rnd(1f, 1.4f), s * 0.7f, s, AudCurseBlack, AudCurseDeep, 1f, 0.03f, 0.7f,
                delay: 0.55f + Rnd(0f, 0.2f), flap: 14f, order: 9);
        }

        for (int i = 0; i < 12 * q; i++)
        {
            Add(Shape.Star, Off(c, Rnd(-0.6f, 0.6f), Rnd(-0.5f, 0.4f)), Vector2.zero, Rnd(0.5f, 0.8f), 0.12f, 0.3f, WindWhite, AudCurseViolet, 1f, 0.1f, 0.6f,
                delay: Rnd(0f, 0.6f), rise: Rnd(0.6f, 1.2f), twinkle: 0.5f, twinkleSpeed: 18f, order: 9);
        }
    }

    // 祝福: 空から金色の光の柱が降りる → 足元に光だまり → 白と金の羽根がゆっくり舞い落ちる → きらめきが昇る
    private static void SpawnAudBless(Vector2 c)
    {
        float q = Active.Count > 1200 ? 0.5f : 1f;
        Vector2 f = Off(c, 0f, Feet.y);

        Impact(c, 2f, AudHolyPale, 0.2f, 0f, 0f, 0.2f);

        // 柱: 琥珀の下地 → 金のにじみ → 白い芯。光条は根元が明るいので、足元に根元を置いて空へ向ける (明るい床でも輪郭が残る)
        Add(Shape.Ray, f, Vector2.zero, 1.8f, 8f, 8f, AudHolyAmber, AudHolyAmber, 0.4f, 0.08f, 0.6f, rot: 90f, sy0: 1.2f, sy1: 2f, order: 5);
        Add(Shape.Ray, f, Vector2.zero, 1.7f, 8f, 8f, AudHolyGold, AudHolyGold, 0.7f, 0.08f, 0.6f, rot: 90f, sy0: 0.6f, sy1: 1.1f, delay: 0.03f, order: 6);
        Add(Shape.Ray, f, Vector2.zero, 1.6f, 8f, 8f, WindWhite, AudHolyPale, 0.95f, 0.08f, 0.6f, rot: 90f, sy0: 0.16f, sy1: 0.32f, delay: 0.06f, order: 7);
        Add(Shape.Glow, c, Vector2.zero, 1.6f, 1.2f, 2.6f, AudHolyPale, AudHolyGold, 0.7f, 0.1f, 0.6f, order: 8);
        Add(Shape.Glow, f, Vector2.zero, 2f, 1f, 2.8f, AudHolyGold, AudHolyAmber, 0.6f, 0.1f, 0.6f, rot: 0f, flat: true, order: 0);
        Add(Shape.Ring, f, Vector2.zero, 0.9f, 0.4f, 3.2f, AudHolyPale, AudHolyGold, 0.8f, 0.03f, 0.4f, delay: 0.2f, sy0: 0.15f, sy1: 1.1f, rot: 0f, order: 1);

        for (int i = 0; i < 10; i++)
        {
            Vector2 p = Off(c, Rnd(-1.2f, 1.2f), Rnd(1.2f, 2.6f));
            float len = Rnd(0.22f, 0.32f);
            Add(Shape.Feather, p, FxMath.V2(Rnd(-0.2f, 0.2f), Rnd(-0.6f, -0.4f)), Rnd(2f, 2.6f), len, len, i % 2 == 0 ? AudHolyPale : AudHolyGold, AudHolyAmber, 0.95f, 0.1f, 0.7f,
                delay: Rnd(0.2f, 0.7f), rot: Rnd(0f, 360f), spin: Rnd(-40f, 40f), sy0: 0.08f, sy1: 0.08f, wobble: 0.25f, wobbleHz: 1.2f, order: 9);
        }

        for (int i = 0; i < 30 * q; i++)
        {
            Add(Shape.Star, Off(c, Rnd(-0.7f, 0.7f), Rnd(-0.6f, 0.3f)), Vector2.zero, Rnd(0.6f, 1.1f), 0.1f, 0.24f, AudHolyGold, AudHolyAmber, 1f, 0.1f, 0.6f,
                delay: Rnd(0.2f, 1.3f), rise: Rnd(0.8f, 1.6f), twinkle: 0.6f, twinkleSpeed: 14f, order: 9);
        }
    }

    // 天の声: 画面の上から金色の光が何本も差し込み、画面全体が温かく色づき、光の粒が漂う。カメラの位置は受け取った各自の画面で引く
    private static void SpawnAudVoice()
    {
        Camera cam = Camera.main;
        if (!cam) return;

        float q = Active.Count > 1200 ? 0.5f : 1f;
        Vector3 cp3 = cam.transform.position;
        float halfH = cam.orthographicSize;
        float halfW = halfH * cam.aspect;
        // 光条は根元が明るいので、根元を画面の上端の内側に置く
        Vector2 top = FxMath.V2(cp3.x, cp3.y + halfH - 0.2f);

        Add(Shape.Solid, top, Vector2.zero, 2.4f, 1f, 1f, AudHolyGold, AudHolyPale, 0.16f, 0.12f, 0.6f, followCamera: true);

        for (int i = 0; i < 7; i++)
        {
            float t = (i + 0.5f) / 7f;
            Vector2 from = FxMath.V2(cp3.x - halfW * 0.8f + t * halfW * 1.6f + Rnd(-0.4f, 0.4f), top.y);
            float ang = 270f + (t - 0.5f) * 30f + Rnd(-4f, 4f);
            float len = halfH * 2f + 1f;
            float w = Rnd(0.6f, 1.2f), d = Rnd(0f, 0.25f), sp = Rnd(-2f, 2f);
            Add(Shape.Ray, from, Vector2.zero, 2.4f, len, len, AudHolyAmber, AudHolyAmber, 0.3f, 0.15f, 0.55f, delay: d, rot: ang, spin: sp, sy0: w * 1.6f, sy1: w * 2.2f, order: 5);
            Add(Shape.Ray, from, Vector2.zero, 2.4f, len, len, AudHolyGold, AudHolyGold, 0.5f, 0.15f, 0.55f, delay: d, rot: ang, spin: sp, sy0: w, sy1: w * 1.4f,
                twinkle: 0.2f, twinkleSpeed: Rnd(1.5f, 3f), order: 6);
            Add(Shape.Ray, from, Vector2.zero, 2.4f, len * 0.8f, len * 0.8f, WindWhite, AudHolyPale, 0.75f, 0.15f, 0.55f, delay: d, rot: ang, spin: sp, sy0: w * 0.2f, sy1: w * 0.3f, order: 7);
        }

        for (int i = 0; i < 40 * q; i++)
        {
            Vector2 p = FxMath.V2(cp3.x + Rnd(-halfW, halfW), cp3.y + Rnd(-halfH, halfH));
            float size = Rnd(0.1f, 0.2f);
            Add(Shape.Star, p, FxMath.V2(0f, -0.2f), Rnd(1.5f, 2.4f), size, size, AudHolyGold, AudHolyAmber, 0.9f, 0.15f, 0.6f, delay: Rnd(0f, 0.6f), twinkle: 0.7f,
                twinkleSpeed: Rnd(2f, 4f), order: 9);
        }
    }

    // 大地震: 画面の上から岩と土くれが降り続け、あちこちで土煙が立ち、自分の足元の床に放射状のひびが走る。カメラの位置は受け取った各自の画面で引く
    private static void SpawnAudQuake()
    {
        Camera cam = Camera.main;
        if (!cam) return;

        float q = Active.Count > 1200 ? 0.5f : 1f;
        Vector3 cp3 = cam.transform.position;
        float halfH = cam.orthographicSize;
        float halfW = halfH * cam.aspect;

        // 岩は画面の上から落ちて床のどこかに当たり、当たった所で土煙と小さな欠片が跳ねる
        for (int i = 0; i < 18 * q; i++)
        {
            Vector2 land = FxMath.V2(cp3.x + Rnd(-halfW * 0.9f, halfW * 0.9f), cp3.y + Rnd(-halfH * 0.8f, halfH * 0.6f));
            float drop = Rnd(4f, 6f), fall = 0.45f, d = Rnd(0f, 3.5f);
            float s = Rnd(0.3f, 0.5f);
            Add(Shape.Chunk, Off(land, Rnd(-0.6f, 0.6f), drop), FxMath.V2(0f, -drop / fall), fall, s, s, i % 3 == 0 ? AudRock : AudRockDark, AudRockDark, 1f, 0.02f, 1f,
                delay: d, spin: Rnd(-300f, 300f), order: 8);
            FloorKick(land, Rnd(0.6f, 0.9f), AudRock, AudRockDark, 0.6f, d + fall, 8, 1f);

            for (int j = 0; j < 3; j++)
            {
                Add(Shape.Chunk, land, FxMath.V2(Rnd(-1.6f, 1.6f), Rnd(1f, 2.2f)), Rnd(0.4f, 0.6f), s * 0.35f, s * 0.35f, AudRockDark, AudRockDark, 1f, 0.01f, 0.7f,
                    delay: d + fall, spin: Rnd(-400f, 400f), rise: -6f, order: 8);
            }
        }

        PlayerControl lp = PlayerControl.LocalPlayer;
        if (!lp) return;

        Vector2 f = Off(lp.GetTruePosition(), 0f, 0.05f);

        for (int k = 0; k < 7; k++)
        {
            float a = (k * (360f / 7f) + Rnd(-18f, 18f)) * FxMath.Deg2Rad;
            float l = Rnd(1.2f, 2.4f);
            int m = FxLines.Fractal(f, Off(f, FxMath.Cos(a) * l, FxMath.Sin(a) * l * 0.45f), 4, l * 0.12f, 0.5f, LineBuf2);
            Strand(LineBuf2, m, 0.05f, 4.5f, AudRockDark, AudCrack, 0.9f, 0.02f, 0.75f, delay: 0.1f + k * 0.05f, taper: FxLines.Taper.Trunk, grow: 0.25f, floor: true, order: 2);
        }
    }
}
