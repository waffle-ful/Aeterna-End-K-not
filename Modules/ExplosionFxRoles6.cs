using System.Collections.Generic;
using UnityEngine;

namespace EndKnot.Modules;

public static partial class ExplosionFx
{
    // 知らない種類 (新しい版のホストが送ってきたもの) は何もしない
    private static void SpawnRoles6(Request r)
    {
        int id = (int)(r.Radius + 0.5f) - 1;

        switch (r.Kind)
        {
            case Kind.IaiSlash:
                SpawnIaiSlash(r.Pos, id);
                FxSound.At("FxIaiSlash", r.Pos);
                break;
            case Kind.WerewolfMaul:
                SpawnWerewolfMaul(r.Pos, id);
                FxSound.At("FxWerewolfMaul", r.Pos);
                break;
            case Kind.ThanosSnap:
                SpawnThanosSnap(r.Pos, id);
                FxSound.At("FxThanosSnap", r.Pos, 1f, everywhere: true);
                break;
            case Kind.PortalIdle:
                StartPortal(r.Pos);
                break;
            case Kind.PortalClose:
                ClosePortal(r.Pos);
                FxSound.At("FxPortalClose", r.Pos, 0.8f);
                break;
            case Kind.WerewolfRampage:
                SpawnWerewolfRampage(r.Pos);
                FxSound.At("FxWerewolfRampage", r.Pos);
                break;
            case Kind.PestilenceRise:
                SpawnPestilenceRise(r.Pos);
                FxSound.At("FxPestilenceRise", r.Pos);
                break;
            case Kind.SporeCloud:
                SpawnSporeCloud(r.Pos, r.Radius);
                FxSound.At("FxSporeCloud", r.Pos, 0.8f);
                break;
            case Kind.StoneGain:
                SpawnStoneGain(r.Pos, id);
                FxSound.At(id >= 10 ? "FxStoneUse" : "FxStoneGain", r.Pos);
                break;
            case Kind.RiftTear:
                SpawnRiftTear(r.Pos);
                FxSound.At("FxRiftTear", r.Pos);
                break;
            case Kind.PortalPass:
                SpawnPortalPass(r.Pos);
                FxSound.At("FxPortalPass", r.Pos);
                break;
            case Kind.BowlStrike:
                SpawnBowlStrike(r.Pos);
                FxSound.At("FxBowlStrike", r.Pos);
                break;
            case Kind.PenguinGrab:
                SpawnPenguinGrab(r.Pos);
                FxSound.At("FxPenguinGrab", r.Pos);
                break;
            case Kind.AltruistGift:
                SpawnAltruistGift(r.Pos);
                FxSound.At("FxAltruistGift", r.Pos);
                break;
            case Kind.AltruistRevive:
                SpawnAltruistRevive(r.Pos);
                FxSound.At("FxAltruistRevive", r.Pos);
                break;
            case Kind.ForceFieldUp:
                SpawnForceFieldUp(r.Pos, r.Radius);
                FxSound.At("FxForceFieldUp", r.Pos);
                break;
            case Kind.ForceRepel:
                SpawnForceRepel(r.Pos);
                FxSound.At("FxForceRepel", r.Pos, 0.7f);
                break;
            case Kind.GoddessGuard:
                SpawnGoddessGuard(r.Pos);
                FxSound.At("FxGoddessGuard", r.Pos);
                break;
            case Kind.GoddessPetrify:
                SpawnGoddessPetrify(r.Pos);
                FxSound.At("FxGoddessPetrify", r.Pos);
                break;
            case Kind.ChainBind:
                SpawnChainBind(r.Pos, id);
                FxSound.At("FxChainBind", r.Pos);
                break;
            case Kind.GunShot:
            {
                int v = (int)(r.Radius + 0.5f);
                SpawnGunShot(r.Pos, v % 32 - 1, (v / 32) * 22.5f);
                FxSound.At("FxGunShot", r.Pos);
                break;
            }
        }
    }

    private static int ColorIdOf(int playerId)
    {
        if (playerId is < 0 or > 254 || !GameData.Instance) return -1;
        return GameData.Instance.GetPlayerById((byte)playerId)?.DefaultOutfit.ColorId ?? -1;
    }

    // ── サムライ: 居合 ─────────────────────────────────────────────

    private static readonly Color SteelPale = new(0.82f, 0.9f, 1f);
    private static readonly Color SteelBlue = new(0.35f, 0.5f, 0.85f);
    private static readonly Color SteelDeep = new(0.06f, 0.08f, 0.18f);
    private static readonly Color Sakura = new(1f, 0.72f, 0.82f);
    private static readonly Color SakuraDeep = new(0.85f, 0.35f, 0.5f);

    // 一瞬の静寂 (体を横切る細い光が走る) → 三日月の一閃が抜け、斬り口は髪の毛ほどの光の線のまま張り詰める
    // → 刀が鞘に収まる間を置いて斬り口が開く (光が噴き、上下の縁が分かれてずれ、血が扇形に噴く) → 床に赤い筋 → 桜が舞い落ちる
    private static void SpawnIaiSlash(Vector2 c, int victimId)
    {
        BloodColors(ColorIdOf(victimId), out Color bright, out Color main, out Color dark);
        float q = Active.Count > 1200 ? 0.5f : 1f;
        const float cut = 0.16f;
        const float ang = -22f;
        float rad = ang * FxMath.Deg2Rad;
        float ux = FxMath.Cos(rad), uy = FxMath.Sin(rad);
        Vector2 cc = Off(c, 0f, 0.05f);

        // 下地: 明るい床でも一閃が沈まないように奥を暗く
        Add(Shape.Glow, cc, Vector2.zero, 1.1f, 2.4f, 2.6f, SteelDeep, SteelDeep, 0.55f, 0.05f, 0.5f, rot: ang, sy0: 0.9f, sy1: 0.7f, order: 0);

        // 静寂: 刃筋だけが細く光って走る
        Line2(Off(cc, -ux * 1.6f, -uy * 1.6f), Off(cc, ux * 1.6f, uy * 1.6f), 0.006f, cut + 0.05f, SteelBlue, SteelPale, 0.7f, cut * 0.8f, 0.9f);

        // 一閃: 三日月の刃の弧 (暗い縁 → 鋼の青 → 白い刃先) が斜めに抜ける
        Impact(c, 3f, SteelPale, 0.14f, 0.12f, 0.2f, delay: cut);
        Add(Shape.ClawSlash, cc, Vector2.zero, 0.4f, 1.6f, 3.4f, SteelDeep, SteelDeep, 0.8f, 0.01f, 0.35f, delay: cut, rot: ang, sy0: 0.5f, sy1: 0.7f, order: 7);
        Add(Shape.ClawSlash, cc, Vector2.zero, 0.38f, 1.6f, 3.3f, SteelPale, SteelBlue, 1f, 0.01f, 0.4f, delay: cut, rot: ang, sy0: 0.3f, sy1: 0.45f, order: 8);
        Add(Shape.ClawSlash, cc, Vector2.zero, 0.25f, 1.6f, 3.2f, WindWhite, SteelPale, 1f, 0.01f, 0.3f, delay: cut, rot: ang, sy0: 0.1f, sy1: 0.14f, order: 9);
        // 斬り口: 刀が鞘に収まるまでは髪の毛ほどの光の線が張り詰めて明るさを増すだけ (まだ斬れていない)
        const float burst = cut + 0.42f;
        float nx = -uy, ny = ux;
        Add(Shape.Glow, cc, Vector2.zero, burst - cut, 1.15f, 1.3f, ClockVoid, ClockVoid, 0.8f, 0.5f, 1f, delay: cut + 0.04f, rot: ang, sy0: 0.05f, sy1: 0.06f, order: 9);
        Add(Shape.Glow, cc, Vector2.zero, burst - cut, 1.1f, 1.25f, SteelPale, WindWhite, 1f, 0.6f, 1f, delay: cut + 0.04f, rot: ang, sy0: 0.018f, sy1: 0.03f, twinkle: 0.25f, twinkleSpeed: 30f, order: 10);
        Add(Shape.Glow, cc, Vector2.zero, burst - cut, 1.2f, 1.4f, SteelBlue, SteelPale, 0.4f, 0.6f, 1f, delay: cut + 0.04f, rot: ang, sy0: 0.06f, sy1: 0.09f, order: 9);

        // 鞘に収まった瞬間に斬り口が開く: 線に沿って光が噴き、上下の縁が分かれてずれながら消える
        Impact(c, 3f, bright, 0.12f, 0.16f, 0.25f, delay: burst);
        Add(Shape.Glow, cc, Vector2.zero, 0.35f, 1.4f, 3f, WindWhite, bright, 1f, 0.01f, 0.35f, delay: burst, rot: ang, sy0: 0.08f, sy1: 0.45f, order: 10);
        Add(Shape.Glow, cc, Vector2.zero, 0.45f, 1.6f, 3f, bright, main, 0.55f, 0.01f, 0.35f, delay: burst, rot: ang, sy0: 0.3f, sy1: 0.6f, order: 8);
        Add(Shape.Star, cc, Vector2.zero, 0.3f, 1.1f, 0.15f, WindWhite, bright, 1f, 0.01f, 0.3f, delay: burst, rot: ang, spin: 90f, order: 11);

        for (int sgn = -1; sgn <= 1; sgn += 2)
        {
            // 縁 1 本 = 暗い影 + 体の色 + 白い刃先。上の縁と下の縁が刃の向きへ少しずれながら離れていく
            Vector2 drift = FxMath.V2(nx * sgn * 0.7f + ux * sgn * 0.3f, ny * sgn * 0.7f + uy * sgn * 0.3f);
            Vector2 o = Off(cc, nx * sgn * 0.03f, ny * sgn * 0.03f);
            Add(Shape.Glow, o, drift, 0.9f, 1.2f, 1.05f, ClockVoid, ClockVoid, 0.9f, 0.01f, 0.45f, drag: 2.2f, delay: burst, rot: ang, sy0: 0.09f, sy1: 0.06f, order: 9);
            Add(Shape.Glow, o, drift, 0.9f, 1.15f, 1f, bright, main, 1f, 0.01f, 0.45f, drag: 2.2f, delay: burst, rot: ang, sy0: 0.05f, sy1: 0.035f, order: 10);
            Add(Shape.Glow, o, drift, 0.8f, 1.1f, 0.95f, WindWhite, SteelPale, 1f, 0.01f, 0.4f, drag: 2.2f, delay: burst, rot: ang, sy0: 0.018f, sy1: 0.012f, order: 11);
        }

        // 斬り口から扇形に噴く血の帯 (線に沿って順に噴く)
        for (int i = 0; i < 26 * q; i++)
        {
            float t = -0.5f + (float)i / (26 * q);
            float side = i % 2 == 0 ? 1f : -1f;
            float sp = Rnd(1.6f, 3.4f);
            Vector2 v = FxMath.V2(nx * side * sp + ux * Rnd(-0.3f, 0.6f), ny * side * sp + uy * Rnd(-0.3f, 0.6f));
            Add(Shape.Star, Off(cc, ux * t, uy * t), v, Rnd(0.4f, 0.65f), Rnd(0.1f, 0.18f), 0.03f, bright, dark, 1f, 0.01f, 0.5f, drag: 4f, delay: burst + (t + 0.5f) * 0.06f, stretch: 0.18f, rise: -0.6f, order: 7);
        }
        Add(Shape.Glow, cc, Vector2.zero, 0.35f, 2.2f, 3.6f, WindWhite, SteelPale, 1f, 0.01f, 0.3f, delay: cut, rot: ang, sy0: 0.06f, sy1: 0.015f, order: 9);
        Add(Shape.Glow, cc, Vector2.zero, 0.5f, 2.6f, 3.4f, SteelPale, SteelBlue, 0.45f, 0.01f, 0.3f, delay: cut, rot: ang, sy0: 0.22f, sy1: 0.08f, order: 8);
        Add(Shape.Glow, cc, Vector2.zero, 0.45f, 1.4f, 2.2f, bright, main, 0.6f, 0.02f, 0.3f, delay: cut + 0.03f, rot: ang, sy0: 0.08f, sy1: 0.03f, order: 8);
        Add(Shape.Star, Off(cc, ux * 1.3f, uy * 1.3f), Vector2.zero, 0.25f, 0.7f, 0.1f, WindWhite, SteelPale, 1f, 0.01f, 0.3f, delay: cut, spin: 200f, order: 10);

        // 刃の通り道を後から追う細い光の筋 3 本 (少しずつずれて遅れる)
        for (int k = 0; k < 3; k++)
        {
            float o = (k - 1) * 0.06f;
            Vector2 a = Off(cc, -ux * 1.8f - uy * o, -uy * 1.8f + ux * o);
            Vector2 b = Off(cc, ux * 1.8f - uy * o, uy * 1.8f + ux * o);
            Line2(a, b, 0.005f, 0.35f, SteelBlue, SteelPale, 0.8f - k * 0.2f, 0.02f, 0.4f, delay: cut + 0.04f + k * 0.03f, coreFadeFrom: 0.2f);
        }

        // 血: 斬られた線の両側へ、刃の進む向きに流れながら散る
        for (int i = 0; i < 34 * q; i++)
        {
            float t = Rnd(-0.6f, 0.6f);
            float side = i % 2 == 0 ? 1f : -1f;
            float sp = Rnd(0.6f, 2.4f);
            Vector2 v = FxMath.V2(-uy * side * sp + ux * Rnd(0.5f, 1.6f), ux * side * sp + uy * Rnd(0.5f, 1.6f));
            Add(Shape.Star, Off(cc, ux * t, uy * t), v, Rnd(0.35f, 0.6f), Rnd(0.07f, 0.14f), 0.03f, bright, dark, 1f, 0.01f, 0.5f, drag: 3.5f, delay: burst + Rnd(0f, 0.08f), stretch: 0.12f, order: 7);
        }

        for (int i = 0; i < 8 * q; i++)
        {
            float t = Rnd(-0.5f, 0.5f);
            Add(Shape.Cloud, Off(cc, ux * t, uy * t), FxMath.V2(Rnd(-0.3f, 0.3f), Rnd(-0.2f, 0.2f)), Rnd(0.5f, 0.8f), 0.2f, Rnd(0.45f, 0.65f), main, dark, 0.45f, 0.03f, 0.4f, drag: 2f, delay: burst + 0.02f, order: 5);
        }

        // 床の赤い筋 (寝かせた細い光)
        Vector2 f = Off(c, 0f, Feet.y);
        Add(Shape.Glow, f, Vector2.zero, 1.2f, 1.2f, 1.6f, main, dark, 0.6f, 0.08f, 0.6f, delay: burst + 0.05f, rot: ang * 0.4f, sy0: 0.04f, sy1: 0.035f, order: 1);

        // 桜: 斬撃の風で舞い上がってから揺れながら落ちる (放射でない動き)
        for (int i = 0; i < 14 * q; i++)
        {
            Vector2 p = Off(c, Rnd(-1.2f, 1.2f), Rnd(0.2f, 1.1f));
            float s = Rnd(0.07f, 0.11f);
            Add(Shape.Glow, p, FxMath.V2(Rnd(0.2f, 0.7f), Rnd(-0.55f, -0.25f)), Rnd(1.1f, 1.6f), s * 2.2f, s * 2f, Sakura, SakuraDeep, 1f, 0.15f, 0.7f, delay: cut + Rnd(0f, 0.35f),
                rot: Rnd(0f, 360f), spin: Rnd(-220f, 220f), sy0: s * 1.1f, sy1: s, wobble: Rnd(0.06f, 0.12f), wobbleHz: Rnd(1.5f, 2.5f), order: 6);
        }
    }

    // ── 人狼: 暴走中のキル ─────────────────────────────────────────

    private static readonly Color MoonPale = new(1f, 0.96f, 0.82f);
    private static readonly Color MoonHalo = new(0.75f, 0.78f, 0.95f);
    private static readonly Color FurDark = new(0.2f, 0.16f, 0.14f);
    private static readonly Color FurGrey = new(0.45f, 0.4f, 0.38f);
    private static readonly Color WolfRed = new(0.85f, 0.06f, 0.08f);

    // 頭上に満月の光がにじむ → 爪痕が 3 回、角度を変えて走る (黒い縁 + 赤い芯 + 白い刃先) → 血と毛が散る
    // → 床に 3 本の爪痕が残り、月の光が引いていく
    private static void SpawnWerewolfMaul(Vector2 c, int victimId)
    {
        BloodColors(ColorIdOf(victimId), out Color bright, out Color main, out Color dark);
        float q = Active.Count > 1200 ? 0.5f : 1f;

        // 満月の光 (背後から差す冷たい光)
        Vector2 moon = Off(c, 0f, 2.1f);
        Add(Shape.Glow, moon, Vector2.zero, 1.4f, 2.6f, 3f, MoonHalo, MoonHalo, 0.28f, 0.2f, 0.5f, order: 0);
        Add(Shape.Glow, moon, Vector2.zero, 1.4f, 0.9f, 1f, MoonPale, MoonHalo, 0.7f, 0.2f, 0.5f, twinkle: 0.15f, twinkleSpeed: 3f, order: 1);
        Add(Shape.Glow, Off(c, 0f, 0.6f), Vector2.zero, 1.4f, 1.6f, 2.2f, MoonHalo, SteelDeep, 0.2f, 0.25f, 0.5f, rot: 0f, sy0: 3f, sy1: 3.2f, order: 0);

        // 体の奥の暗がり
        Add(Shape.Glow, c, Vector2.zero, 1f, 2f, 2.3f, ClockVoid, ClockVoid, 0.6f, 0.04f, 0.5f, order: 0);

        float[] rots = [-38f, -12f, -58f];
        for (int k = 0; k < 3; k++)
        {
            float d = 0.1f + k * 0.09f;
            Vector2 o = Off(c, (k - 1) * 0.18f, 0.12f - k * 0.1f);
            Add(Shape.ClawSlash, o, Vector2.zero, 0.45f, 0.5f, 2.4f, ClockVoid, ClockVoid, 0.75f, 0.02f, 0.4f, delay: d, rot: rots[k], sy0: 0.45f, sy1: 0.75f, order: 8);
            Add(Shape.ClawSlash, o, Vector2.zero, 0.4f, 0.4f, 2.3f, bright, WolfRed, 1f, 0.02f, 0.45f, delay: d, rot: rots[k], sy0: 0.28f, sy1: 0.5f, order: 9);
            Add(Shape.ClawSlash, o, Vector2.zero, 0.22f, 0.4f, 2.2f, WindWhite, bright, 0.9f, 0.01f, 0.3f, delay: d, rot: rots[k], sy0: 0.08f, sy1: 0.14f, order: 10);
            Impact(c, 2.5f, WolfRed, 0.035f + k * 0.01f, 0.08f + k * 0.04f, 0.18f, delay: d);

            for (int i = 0; i < 12 * q; i++)
            {
                float a = (rots[k] + 90f + Rnd(-35f, 35f)) * FxMath.Deg2Rad * (i % 2 == 0 ? 1f : -1f);
                float sp = Rnd(1f, 2.8f);
                Add(Shape.Star, o, FxMath.V2(FxMath.Cos(a) * sp, FxMath.Sin(a) * sp), Rnd(0.3f, 0.55f), Rnd(0.05f, 0.1f), 0.02f, bright, dark, 1f, 0.01f, 0.5f, drag: 3.5f, delay: d + 0.02f, stretch: 0.1f, order: 7);
            }
        }

        // 血の霧と毛
        for (int i = 0; i < 6 * q; i++)
            Add(Shape.Cloud, Off(c, Rnd(-0.3f, 0.3f), Rnd(-0.2f, 0.3f)), FxMath.V2(Rnd(-0.4f, 0.4f), Rnd(0f, 0.3f)), Rnd(0.6f, 0.9f), 0.25f, Rnd(0.5f, 0.75f), main, dark, 0.4f, 0.04f, 0.4f, drag: 2f, delay: 0.15f, order: 5);

        for (int i = 0; i < 16 * q; i++)
        {
            float len = Rnd(0.14f, 0.24f);
            Add(Shape.Feather, Off(c, Rnd(-0.2f, 0.2f), Rnd(-0.1f, 0.3f)), Dir() * Rnd(0.8f, 2f), Rnd(0.8f, 1.2f), len, len, FurGrey, FurDark, 1f, 0.02f, 0.6f, drag: 3f, delay: Rnd(0.1f, 0.3f),
                rot: Rnd(0f, 360f), spin: Rnd(-260f, 260f), sy0: 0.1f, sy1: 0.1f, rise: -0.4f, order: 6);
        }

        // 床に残る 3 本の爪痕
        Vector2 f = Off(c, 0f, Feet.y);
        for (int k = 0; k < 3; k++)
        {
            Vector2 a = Off(f, -0.45f + k * 0.13f, 0.12f);
            Vector2 b = Off(f, 0.2f + k * 0.13f, -0.14f);
            Line2(a, b, 0.012f, 1.3f, ClockVoid, main, 0.85f, 0.05f, 0.6f, delay: 0.3f + k * 0.04f, coreFadeFrom: 0.5f);
        }
    }

    // ── サノス: 指パッチン ─────────────────────────────────────────

    private static readonly Color[] StoneColors =
    [
        new(0.25f, 0.5f, 1f), // 空間
        new(1f, 0.85f, 0.2f), // 精神
        new(0.95f, 0.1f, 0.15f), // 現実
        new(0.65f, 0.22f, 1f), // 力
        new(0.25f, 0.95f, 0.45f), // 時間
        new(1f, 0.55f, 0.1f) // 魂
    ];

    private static readonly Color GauntletGold = new(1f, 0.78f, 0.3f);
    private static readonly Color AshGrey = new(0.32f, 0.28f, 0.26f);
    private static readonly Color AshDark = new(0.12f, 0.1f, 0.1f);

    public const float SnapTotal = 5f;

    // 撃つ人が target の方へ銃を向けて撃つ (撃つ人の色の手が銃を握る)。キルの直後 (撃つ人が死体の位置へ動く前) に呼ぶ。
    // 撃つ人はキルの瞬間に撃たれた人の位置へ移るので、演出はそこに出し、向きだけ元の位置から測る
    public static void PlayGunShot(PlayerControl shooter, PlayerControl victim)
    {
        // 誤射の自滅 (Suicide → Kill(自分)) も OnMurder を通るので、撃った本人が倒れる時は出さない
        if (!shooter || !victim || shooter.PlayerId == victim.PlayerId) return;

        try
        {
            Vector2 target = victim.Pos();
            Vector2 from = shooter.Pos();
            float ang = FxMath.Atan2(target.y - from.y, target.x - from.x) * FxMath.Rad2Deg;
            int dir = ((int)System.MathF.Round((ang < 0f ? ang + 360f : ang) / 22.5f)) & 15;
            Play(Kind.GunShot, target, shooter.PlayerId + 1 + 32 * dir);
        }
        catch (System.Exception e) { Utils.ThrowException(e); }
    }

    private static readonly Color GunFlash = new(1f, 0.93f, 0.62f);
    private static readonly Color GunFire = new(1f, 0.55f, 0.15f);
    private static readonly Color GunSmoke = new(0.55f, 0.55f, 0.58f, 0.7f);
    private static readonly Color Brass = new(0.95f, 0.75f, 0.3f);

    // 手と銃の絵の中で、握りの中心から見た銃口の位置 (絵の単位・反転なし) と銃身の向き (度)
    private const float MuzzleX = 0.23f, MuzzleY = 1.15f;
    private const float BarrelDeg = 79f;
    private const float GunSize = 0.3f;
    private const float GunFireAt = 0.16f;

    // 銃を抜いて構え → 撃つ (反動で跳ねる) → 少し構えたまま → 消える。銃口の光・弾の筋・硝煙・薬莢が飛ぶ
    private static void SpawnGunShot(Vector2 c, int shooterId, float aimDeg)
    {
        float rad = aimDeg * FxMath.Deg2Rad;
        float ux = FxMath.Cos(rad), uy = FxMath.Sin(rad);
        bool flip = ux < 0f;
        float sx = flip ? -1f : 1f;
        // 手の絵は反転させると銃身の向きも左右反転する。キーの回転は反転側で符号が変わる (FxHands.Tick)
        float barrel = flip ? 180f - BarrelDeg : BarrelDeg;
        float aim = sx * (aimDeg - barrel);
        float kick = 22f;  // 反動で銃口が上へ跳ねる
        float up = sx * (ux >= 0f ? kick : -kick);

        Vector2 hand = Off(c, ux * 0.42f, uy * 0.3f + 0.12f);
        FxHands.Key[] keys =
        [
            new(0f, FxHands.Pose.GunGrip, scale: 0.8f, alpha: 0f, rot: aim + up * 1.6f, dx: -0.06f),
            new(0.06f, FxHands.Pose.GunGrip, rot: aim + up * 0.5f),
            new(0.12f, FxHands.Pose.GunGrip, rot: aim),
            new(GunFireAt, FxHands.Pose.GunGrip, rot: aim, shake: 0.004f),
            new(GunFireAt + 0.05f, FxHands.Pose.GunGrip, scale: 1.08f, rot: aim + up, dx: -0.08f),
            new(GunFireAt + 0.25f, FxHands.Pose.GunGrip, rot: aim + up * 0.15f),
            new(0.95f, FxHands.Pose.GunGrip, rot: aim),
            new(1.2f, FxHands.Pose.GunGrip, scale: 0.9f, alpha: 0f, rot: aim + up * 0.4f)
        ];
        Hand(keys, hand, ColorIdOf(shooterId), GunSize, flip, order: 9, prop: FxHands.Pose.Gun);

        // 撃つ瞬間の銃口 (握りの中心から、構えた角度で回した位置)
        float ar = sx * aim * FxMath.Deg2Rad;
        float mx = sx * MuzzleX * GunSize, my = MuzzleY * GunSize;
        Vector2 muzzle = Off(hand, mx * FxMath.Cos(ar) - my * FxMath.Sin(ar), mx * FxMath.Sin(ar) + my * FxMath.Cos(ar));
        float aimRot = aimDeg;

        // 銃口の閃光: 前へ伸びる炎の舌 + 白い芯 + 周りを照らす光
        Add(Shape.Glow, muzzle, Vector2.zero, 0.18f, 0.5f, 1.4f, GunFlash, GunFire, 0.8f, 0.01f, 0.2f, delay: GunFireAt, order: 10);
        Add(Shape.Flame, Off(muzzle, ux * 0.18f, uy * 0.18f), Vector2.zero, 0.09f, 0.42f, 0.5f, GunFlash, GunFire, 1f, 0.01f, 0.3f, delay: GunFireAt, rot: aimRot - 90f, sy0: 0.16f, sy1: 0.2f, order: 11);
        Add(Shape.Star, muzzle, Vector2.zero, 0.08f, 0.35f, 0.15f, WindWhite, GunFlash, 1f, 0.01f, 0.3f, delay: GunFireAt, rot: aimRot, order: 12);
        Particles(FxParticles.Preset.Sparks, muzzle, 14, GunFlash, GunFire, size: 0.6f, delay: GunFireAt, order: 11, angle: aimDeg);

        // 弾の筋: 銃口から撃った向きへ細く走る
        Line2(muzzle, Off(muzzle, ux * 2.2f, uy * 2.2f), 0.025f, 0.12f, GunFire, GunFlash, 0.9f, 0.01f, 0.2f, delay: GunFireAt + 0.01f);

        // 硝煙: 銃口から少し漂って上へ
        Particles(FxParticles.Preset.Smoke, muzzle, 4, GunSmoke, GunSmoke, size: 0.35f, speed: 0.4f, spread: 0.3f, delay: GunFireAt + 0.03f, order: 8);

        // 薬莢: 銃の横へ弾き出され、弧を描いて床へ落ちる
        float ex = -uy * sx, ey = ux * sx;
        if (ey < 0f) { ex = -ex; ey = -ey; }
        Add(Shape.Solid, Off(muzzle, -ux * 0.12f, -uy * 0.12f), FxMath.V2(ex * 2.2f - ux * 0.4f, 3f), 0.45f, 0.05f, 0.05f, Brass, Brass, 1f, 0.01f, 0.8f,
            drag: 3f, rise: -2.2f, delay: GunFireAt + 0.03f, spin: 900f, sy0: 0.022f, sy1: 0.022f, order: 10);
    }

    // 指パッチンの手: 構えて力を溜め (震え) → 弾く → 弾いた後に跳ねる → 手を開いて消える
    private static readonly FxHands.Key[] SnapHandKeys =
    [
        new(0f, FxHands.Pose.SnapReady, scale: 0.8f, alpha: 0f, dy: -0.1f),
        new(0.12f, FxHands.Pose.SnapReady),
        new(0.5f, FxHands.Pose.SnapReady, scale: 0.96f, shake: 0.014f),
        new(0.56f, FxHands.Pose.SnapFlick, rot: -6f),
        new(0.62f, FxHands.Pose.SnapAfter, scale: 1.15f, rot: 8f),
        new(0.8f, FxHands.Pose.SnapAfter),
        new(1.6f, FxHands.Pose.SnapAfter),
        new(1.75f, FxHands.Pose.Open, scale: 0.9f),
        new(2.1f, FxHands.Pose.Open, scale: 0.95f, alpha: 0f, dy: 0.06f)
    ];

    private static readonly Vector2[] CrackBuf = new Vector2[FxLines.MaxPoints];
    private static readonly Vector2[] Nodes = new Vector2[8];
    private static readonly float[] NodeLen = new float[8];
    private static readonly float[] NodeAng = new float[8];

    // 6 色の石の光がサノスの手元へ集まりパチンと弾ける (白い衝撃波) → 画面の周りの床にひびが走り、破片が虚空へ落ちていく
    // (揺れはだんだん強くなる) → サノス以外の生存者が自分の色の灰になって崩れ、風に流される → 白く飛ぶ
    private static void SpawnThanosSnap(Vector2 c, int thanosId)
    {
        const float snap = 0.6f;
        const float white = 4.1f;
        float q = Active.Count > 1200 ? 0.5f : 1f;
        Vector2 hand = Off(c, 0.32f, 0.15f);

        // 本人の色の手が指を鳴らす
        PlayerControl thanos = Utils.GetPlayerById(thanosId);
        int colorId = thanos && thanos.Data ? thanos.Data.DefaultOutfit.ColorId : 0;
        Hand(SnapHandKeys, Off(hand, 0f, 0.08f), colorId, 0.32f, false, order: 9);

        // 溜め: 6 色の光が手元へ螺旋を描いて吸い込まれる
        Add(Shape.Glow, hand, Vector2.zero, snap + 0.1f, 0.4f, 1.2f, GauntletGold, GauntletGold, 0.8f, 0.3f, 0.8f, twinkle: 0.4f, twinkleSpeed: 16f, order: 6);
        for (int k = 0; k < 6; k++)
        {
            Color col = StoneColors[k];
            for (int i = 0; i < 6 * q; i++)
            {
                float a = (k * 60f + i * 9f) * FxMath.Deg2Rad;
                float rr = 1.6f + i * 0.08f;
                Vector2 st = Off(hand, FxMath.Cos(a) * rr, FxMath.Sin(a) * rr * 0.8f);
                float life = snap - i * 0.04f;
                Add(Shape.Star, st, FxMath.V2((hand.x - st.x) / life, (hand.y - st.y) / life), life, 0.14f, 0.05f, WindWhite, col, 1f, 0.1f, 0.85f, delay: i * 0.04f, stretch: 0.06f, order: 8);
            }

            // 石そのもの: 体の周りを回りながら手元へ寄っていく
            float a0 = k * 60f * FxMath.Deg2Rad;
            Vector2 orb = Off(c, FxMath.Cos(a0) * 0.9f, FxMath.Sin(a0) * 0.7f);
            Add(Shape.Glow, orb, FxMath.V2((hand.x - orb.x) / snap, (hand.y - orb.y) / snap), snap, 0.7f, 0.35f, col, col, 0.55f, 0.1f, 0.9f, order: 8);
            Add(Shape.Star, orb, FxMath.V2((hand.x - orb.x) / snap, (hand.y - orb.y) / snap), snap, 0.32f, 0.14f, WindWhite, col, 1f, 0.1f, 0.9f, spin: 180f, order: 9);
        }

        // パチン: 小さな白い星 → 白と金の衝撃波が画面の外まで
        Add(Shape.Star, hand, Vector2.zero, 0.25f, 1.4f, 0.2f, WindWhite, GauntletGold, 1f, 0.01f, 0.3f, delay: snap, spin: 160f, order: 11);
        Add(Shape.Glow, hand, Vector2.zero, 0.3f, 0.5f, 3f, WindWhite, GauntletGold, 1f, 0.01f, 0.3f, delay: snap, order: 10);
        Add(Shape.Ring, c, Vector2.zero, 1.4f, 0.5f, 26f, WindWhite, GauntletGold, 0.85f, 0.01f, 0.4f, delay: snap, order: 9);
        Particles(FxParticles.Preset.Sparks, hand, (int)(40 * q), WindWhite, GauntletGold, delay: snap, order: 10);
        Impact(c, 30f, WindWhite, 0.3f, 0.15f, 0.3f, delay: snap);

        // ここから先は自分の画面の周りで起きる (どこにいても崩壊が見える)
        Camera cam = Camera.main;
        Vector2 cc = cam ? (Vector2)cam.transform.position : c;

        // 揺れがだんだん強くなる
        for (int i = 0; i < 6; i++)
            Impact(cc, 1f, AshDark, 0.03f + i * 0.015f, 0.06f + i * 0.05f, 0.45f, delay: snap + 0.45f + i * 0.5f);

        // ひび: 画面のあちこちから枝分かれして伸びる。ひびの奥は紫がかった虚空
        for (int k = 0; k < 9; k++)
        {
            float d0 = snap + 0.3f + k * 0.28f;
            Vector2 p = Off(cc, Rnd(-6.5f, 6.5f), Rnd(-3.6f, 3.6f));
            float ang = Rnd(0f, 2f * FxMath.PI);
            float life = white - d0 + 0.4f;

            // 幹: 7 歩の折れ線の骨組みを、歩ごとに中点変位で細かく折る (57 点)。1 歩 0.05 秒で伸びる
            int cn = 0;
            CrackBuf[cn++] = p;
            Vector2 walk = p;

            for (int j = 0; j < 7; j++)
            {
                ang += Rnd(-0.7f, 0.7f);
                float len = Rnd(0.35f, 0.7f);
                Vector2 n = Off(walk, FxMath.Cos(ang) * len, FxMath.Sin(ang) * len);
                int m = FxLines.Fractal(walk, n, 3, len * 0.14f, 0.55f, LineBuf2);
                for (int i = 1; i < m && cn < CrackBuf.Length; i++) CrackBuf[cn++] = LineBuf2[i];
                walk = n;
                Nodes[j] = n;
                NodeLen[j] = len;
                NodeAng[j] = ang;
            }

            // 黒い裂け目 (柔らかい暗い縁つき) と、その中で光る紫の芯
            Lines(CrackBuf, cn, new FxLines.Spec
            {
                Width = 0.09f, Taper = FxLines.Taper.Trunk, Core = VoidBlack, Under = VoidBlack, CoreAlpha = 0.95f, UnderAlpha = 0.4f, UnderMul = 3f,
                Delay = d0, Grow = 0.35f, Life = life, FadeOutFrom = 0.92f
            }, 0);
            Lines(CrackBuf, cn, new FxLines.Spec
            {
                Width = 0.011f, Taper = FxLines.Taper.Even, Core = VoidMagenta, Under = VoidDeep, CoreAlpha = 1f, UnderAlpha = 0.5f, UnderMul = 5f,
                UnderGlow = true, CoreGlow = true, Delay = d0, Grow = 0.35f, Life = life, FadeOutFrom = 0.92f
            }, 1);

            for (int j = 0; j < 7; j++)
            {
                Vector2 n = Nodes[j];
                float len = NodeLen[j];
                float dl = d0 + j * 0.05f;

                if (j % 3 == 1)
                {
                    float ba = NodeAng[j] + (FxMath.Value < 0.5f ? -1f : 1f) * Rnd(0.45f, 0.7f);
                    int m = FxLines.Fractal(n, Off(n, FxMath.Cos(ba) * len * 0.9f, FxMath.Sin(ba) * len * 0.9f), 3, len * 0.1f, 0.6f, LineBuf2);
                    Lines(LineBuf2, m, new FxLines.Spec
                    {
                        Width = 0.06f, Taper = FxLines.Taper.Branch, Core = VoidDeep, Under = VoidBlack, CoreAlpha = 0.9f, UnderAlpha = 0.4f, UnderMul = 3f,
                        Delay = dl + 0.06f, Grow = 0.15f, Decel = true, Life = white - dl + 0.3f, FadeOutFrom = 0.92f
                    }, 0);
                }

                // ひびの節から虚空の穴が広がり、床の破片が回りながら縮んで落ちていく
                if (j % 2 == 0)
                {
                    Add(Shape.Glow, n, Vector2.zero, white - dl + 0.5f, 0.2f, Rnd(1.6f, 2.6f), VoidBlack, VoidBlack, 0.95f, 0.35f, 0.9f, delay: dl + 0.1f, order: 1);
                    Add(Shape.Glow, n, Vector2.zero, white - dl + 0.5f, 0.1f, Rnd(0.5f, 0.9f), VoidDeep, VoidPurple, 0.35f, 0.4f, 0.9f, delay: dl + 0.1f, twinkle: 0.3f, twinkleSpeed: 4f, order: 2);

                    for (int m = 0; m < 3 * q; m++)
                    {
                        float s = Rnd(0.45f, 0.9f);
                        Vector2 at0 = Off(n, Rnd(-0.6f, 0.6f), Rnd(-0.45f, 0.45f));
                        // 欠けた床の縁 (明るい) と板 (暗い) を重ね、回りながら縮んで穴へ落ちる
                        Add(Shape.Solid, at0, FxMath.V2(Rnd(-0.15f, 0.15f), Rnd(-0.35f, -0.1f)), 1.3f, s * 1.08f, 0.03f, Stone, StoneDark, 0.9f, 0.05f, 0.5f,
                            delay: dl + 0.2f + m * 0.15f, rot: 15f * m, spin: 60f, sy0: s * 0.7f, sy1: 0.03f, order: 3);
                        Add(Shape.Solid, at0, FxMath.V2(Rnd(-0.15f, 0.15f), Rnd(-0.35f, -0.1f)), Rnd(0.9f, 1.4f), s, 0.02f, StoneDark, VoidBlack, 1f, 0.05f, 0.5f,
                            delay: dl + 0.2f + m * 0.15f, rot: 15f * m, spin: 60f, sy0: s * 0.62f, sy1: 0.02f, order: 4);
                    }
                }
            }
        }

        // 画面の縁から闇が寄ってくる
        for (int i = 0; i < 4; i++)
        {
            float dl = snap + 1f + i * 0.6f;
            Add(Shape.Solid, Vector2.zero, Vector2.zero, white - dl + 0.6f, 1f, 1f, VoidBlack, VoidBlack, 0.12f, 0.6f, 0.95f, delay: dl, followCamera: true);
        }

        // 灰化: サノス以外の生存者が足元から崩れ、自分の色の灰になって風に流される
        int n0 = 0;
        foreach (PlayerControl pc in Main.AllAlivePlayerControls)
        {
            if (!pc || pc.PlayerId == thanosId) continue;

            BloodColors(pc.Data ? pc.Data.DefaultOutfit.ColorId : -1, out Color bright, out Color main, out Color _);
            Vector2 pp = pc.Pos();
            float d0 = snap + 0.8f + (n0++ % 6) * 0.35f + Rnd(0f, 0.2f);
            const float dur = 1.6f;

            // 体が灰色に変わってから、上から順に崩れて風に流される
            Add(Shape.CrewSil, pp, Vector2.zero, 0.5f, 1f, 1f, main, AshGrey, 0.9f, 0.5f, 0.9f, delay: d0 - 0.3f, rot: 0f, order: 5);
            Add(Shape.CrewSil, pp, FxMath.V2(0.35f, 0.08f), dur, 1f, 1.05f, AshGrey, AshDark, 0.95f, 0.02f, 0.4f, delay: d0 + 0.2f, rot: 0f, order: 5);

            for (int i = 0; i < 80 * q; i++)
            {
                float u = (float)i / (80 * q);
                Vector2 at = Off(pp, Rnd(-0.34f, 0.34f), 0.5f - 0.95f * u + Rnd(-0.05f, 0.05f));
                float s = Rnd(0.08f, 0.16f);
                Color c0 = i % 3 == 0 ? main : i % 3 == 1 ? AshGrey : bright;
                Add(Shape.Cloud, at, FxMath.V2(Rnd(0.8f, 2.2f), Rnd(0.05f, 0.6f)), Rnd(1.1f, 1.8f), s, s * 0.35f, c0, AshDark, 1f, 0.05f, 0.5f, drag: 0.35f, delay: d0 + 0.2f + u * dur * 0.75f,
                    rise: 0.3f, wobble: 0.06f, wobbleHz: Rnd(2f, 4f), order: 6);
            }

            // 細かい灰の欠片: 頭から足元へ 8 段に分けて、風下へ渦を巻きながら舞い上がる
            for (int k = 0; k < 8; k++)
            {
                float u = k / 8f;
                Vector2 at = Off(pp, 0f, 0.5f - 0.95f * u);
                float dl = d0 + 0.2f + u * dur * 0.75f;
                Particles(FxParticles.Preset.Ash, at, (int)(22 * q), main, AshGrey, size: 1.4f, delay: dl, order: 6, angle: 15f);
                Particles(FxParticles.Preset.Ash, at, (int)(10 * q), bright, AshDark, size: 0.6f, speed: 1.5f, delay: dl + 0.05f, order: 6, angle: 25f);
            }
        }

        // 白く飛ぶ (画面の点滅を切っている人は暗転)
        Color end = Main.MapAtmosphereFlash?.Value == true ? WindWhite : VoidBlack;
        Add(Shape.Solid, Vector2.zero, Vector2.zero, SnapTotal - white + 0.4f, 1f, 1f, end, end, 1f, 0.75f, 1f, delay: white, followCamera: true);
    }

    // ── アルトルイスト ─────────────────────────────────────────────

    private static readonly Color LifeWhite = new(0.95f, 1f, 0.95f);
    private static readonly Color LifeGreen = new(0.55f, 1f, 0.7f);
    private static readonly Color LifeTeal = new(0.2f, 0.85f, 0.7f);
    private static readonly Color LifeDeep = new(0.03f, 0.22f, 0.16f);
    private static readonly Color LifeWarm = new(1f, 0.9f, 0.65f);

    // 命を差し出す: 体が内側から白く光って輪郭だけになり、光の粒になってほどけ、粒は床の一点へ沈んで小さな灯りを残す
    private static void SpawnAltruistGift(Vector2 c)
    {
        float q = Active.Count > 1200 ? 0.5f : 1f;
        Vector2 f = Off(c, 0f, Feet.y);

        Add(Shape.Glow, c, Vector2.zero, 1.6f, 1.6f, 2f, LifeDeep, LifeDeep, 0.55f, 0.1f, 0.6f, order: 0);
        Add(Shape.CrewSil, c, Vector2.zero, 0.7f, 1f, 1.02f, LifeWhite, LifeGreen, 0.95f, 0.25f, 0.5f, rot: 0f, order: 5);
        Add(Shape.Glow, c, Vector2.zero, 0.8f, 0.9f, 1.6f, LifeWhite, LifeGreen, 0.7f, 0.3f, 0.5f, twinkle: 0.2f, twinkleSpeed: 6f, order: 4);
        Add(Shape.Glow, Off(c, 0f, 0.9f), Vector2.zero, 1.1f, 0.6f, 0.8f, LifeGreen, LifeTeal, 0.55f, 0.3f, 0.5f, rot: 0f, sy0: 3.5f, sy1: 4f, order: 1);
        Add(Shape.Glow, Off(c, 0f, 0.9f), Vector2.zero, 1.1f, 0.2f, 0.25f, LifeWhite, LifeGreen, 0.9f, 0.3f, 0.5f, rot: 0f, sy0: 3.5f, sy1: 4f, order: 2);

        // ほどける粒: 体の輪郭から舞い上がってから、ゆっくり床の一点へ吸い寄せられる
        for (int i = 0; i < 40 * q; i++)
        {
            Vector2 p = Off(c, Rnd(-0.32f, 0.32f), Rnd(-0.4f, 0.45f));
            float d = 0.35f + Rnd(0f, 0.35f);
            Add(Shape.Star, p, FxMath.V2(Rnd(-0.4f, 0.4f), Rnd(0.3f, 0.8f)), 0.5f, Rnd(0.06f, 0.11f), 0.05f, LifeWhite, LifeGreen, 1f, 0.1f, 0.7f, drag: 2f, delay: d, twinkle: 0.4f, twinkleSpeed: 14f, order: 7);
            float life = Rnd(0.5f, 0.7f);
            Vector2 p2 = Off(p, Rnd(-0.1f, 0.1f), 0.3f);
            Add(Shape.Star, p2, FxMath.V2((f.x - p2.x) / life, (f.y - p2.y) / life), life, 0.08f, 0.03f, LifeGreen, LifeTeal, 1f, 0.05f, 0.8f, delay: d + 0.5f, stretch: 0.05f, order: 7);
        }

        // 床に残る灯り (蘇りを待つ種)
        Add(Shape.Glow, f, Vector2.zero, 0.9f, 0.2f, 0.9f, LifeGreen, LifeTeal, 0.85f, 0.2f, 0.6f, delay: 1.1f, rot: 0f, sy0: 0.35f, sy1: 0.35f, twinkle: 0.3f, twinkleSpeed: 3f, order: 2);
    }

    // 蘇る: 床に光の花が開き (花弁が回りながら開く) → 若葉色の光が柱になって立ち、粒が螺旋に昇る → 体の形に光が満ち、白く弾ける
    private static void SpawnAltruistRevive(Vector2 c)
    {
        float q = Active.Count > 1200 ? 0.5f : 1f;
        Vector2 f = Off(c, 0f, Feet.y);
        const float bloom = 0.45f;
        const float life = 1.9f;

        Add(Shape.Glow, f, Vector2.zero, life, 2.2f, 2.4f, LifeDeep, LifeDeep, 0.6f, 0.1f, 0.7f, flat: true, order: 0);

        // 光の花: 花弁 (細長い光) が中心から開き、内側にもう 1 重
        for (int ring = 0; ring < 2; ring++)
        {
            int petals = ring == 0 ? 8 : 6;
            float len = ring == 0 ? 0.9f : 0.55f;
            for (int k = 0; k < petals; k++)
            {
                float a = (k * 360f / petals + ring * 22f) * FxMath.Deg2Rad;
                Vector2 tip = Off(f, FxMath.Cos(a) * len * 0.5f, FxMath.Sin(a) * len * 0.5f * 0.38f);
                float rot = FxMath.Atan2(FxMath.Sin(a) * 0.38f, FxMath.Cos(a)) * FxMath.Rad2Deg;
                Add(Shape.Glow, tip, Vector2.zero, life - 0.2f, 0.1f, len, LifeGreen, LifeTeal, 0.85f, 0.15f, 0.6f, delay: ring * 0.1f + k * 0.02f, rot: rot, sy0: 0.06f, sy1: 0.14f, order: 2);
                Add(Shape.Glow, tip, Vector2.zero, life - 0.25f, 0.05f, len * 0.8f, LifeWhite, LifeGreen, 0.9f, 0.15f, 0.6f, delay: ring * 0.1f + k * 0.02f, rot: rot, sy0: 0.02f, sy1: 0.035f, order: 3);
            }
        }


        // 光の柱
        Add(Shape.Glow, Off(c, 0f, 1.3f), Vector2.zero, life - bloom, 0.4f, 1.3f, LifeGreen, LifeTeal, 0.35f, 0.15f, 0.6f, delay: bloom, rot: 0f, sy0: 1f, sy1: 3.8f, twinkle: 0.2f, twinkleSpeed: 5f, order: 1);
        Add(Shape.Glow, Off(c, 0f, 1.3f), Vector2.zero, life - bloom, 0.15f, 0.45f, LifeWhite, LifeGreen, 0.9f, 0.15f, 0.6f, delay: bloom, rot: 0f, sy0: 1f, sy1: 3.6f, twinkle: 0.2f, twinkleSpeed: 12f, order: 2);

        // 螺旋に昇る粒と、ひらひら落ちる若葉
        for (int i = 0; i < 34 * q; i++)
        {
            float a = Rnd(0f, 2f * FxMath.PI);
            Add(Shape.Star, Off(f, FxMath.Cos(a) * 0.5f, FxMath.Sin(a) * 0.18f), FxMath.V2(-FxMath.Sin(a) * 1.4f, FxMath.Cos(a) * 0.45f), Rnd(0.7f, 1f), Rnd(0.07f, 0.13f), 0.03f, LifeWhite, LifeGreen, 1f, 0.15f, 0.6f,
                drag: 1.4f, delay: bloom + Rnd(0f, 0.8f), rise: 1.7f, twinkle: 0.6f, twinkleSpeed: Rnd(12f, 20f), order: 6);
        }

        for (int i = 0; i < 10 * q; i++)
        {
            float s = Rnd(0.08f, 0.12f);
            Add(Shape.Glow, Off(c, Rnd(-0.9f, 0.9f), Rnd(0.8f, 1.6f)), FxMath.V2(Rnd(-0.2f, 0.2f), Rnd(-0.5f, -0.3f)), Rnd(1f, 1.4f), s * 2f, s * 1.8f, LifeGreen, LifeTeal, 0.9f, 0.2f, 0.7f,
                delay: bloom + Rnd(0f, 0.6f), rot: Rnd(0f, 360f), spin: Rnd(-160f, 160f), sy0: s, sy1: s, wobble: 0.08f, wobbleHz: 2f, order: 6);
        }

        // 体に光が満ちて弾ける
        Add(Shape.CrewSil, c, Vector2.zero, 0.6f, 0.95f, 1f, LifeWhite, LifeGreen, 0.85f, 0.6f, 0.6f, delay: bloom + 0.6f, rot: 0f, order: 5);
        Add(Shape.Glow, c, Vector2.zero, 0.3f, 1f, 2.4f, LifeWhite, LifeWarm, 0.9f, 0.05f, 0.4f, delay: bloom + 1.2f, order: 8);
        Impact(c, 3f, LifeWhite, 0.12f, 0.05f, 0.15f, delay: bloom + 1.2f);
        for (int i = 0; i < 24 * q; i++)
            Add(Shape.Star, c, Dir() * Rnd(1f, 2.6f), Rnd(0.35f, 0.55f), Rnd(0.05f, 0.08f), 0.02f, i % 3 == 0 ? LifeWarm : LifeWhite, LifeGreen, 1f, 0.02f, 0.5f, drag: 3f, delay: bloom + 1.2f, twinkle: 0.5f, twinkleSpeed: 18f, order: 8);
    }

    // ── フォースフィールダー ───────────────────────────────────────

    private static readonly Color FieldWhite = new(0.85f, 0.97f, 1f);
    private static readonly Color FieldCyan = new(0.3f, 0.85f, 1f);
    private static readonly Color FieldBlue = new(0.2f, 0.4f, 1f);
    private static readonly Color FieldDeep = new(0.02f, 0.06f, 0.2f);

    // 六角形 1 枚の縁を描く
    private static void HexCell(Vector2 c, float r, float w, float life, float delay, Color outer, Color core, float alpha)
    {
        for (int k = 0; k < 6; k++)
        {
            float a0 = (k * 60f + 30f) * FxMath.Deg2Rad, a1 = ((k + 1) * 60f + 30f) * FxMath.Deg2Rad;
            Line2(Off(c, FxMath.Cos(a0) * r, FxMath.Sin(a0) * r), Off(c, FxMath.Cos(a1) * r, FxMath.Sin(a1) * r), w, life, outer, core, alpha, 0.1f, 0.5f, delay: delay);
        }
    }

    // 力場を張る: 縁の輪が少し行き過ぎてから戻って止まり、内側に六角形の格子が中心から外へ波のように点いて消える
    private static void SpawnForceFieldUp(Vector2 c, float radius)
    {
        float r = FxMath.Clamp(radius, 0.8f, 6f);
        const float life = 1.2f;

        Add(Shape.Glow, c, Vector2.zero, life, r * 1.6f, r * 2.2f, FieldDeep, FieldDeep, 0.5f, 0.1f, 0.6f, order: 0);
        Add(Shape.Ring, c, Vector2.zero, 0.3f, 0.3f, r * 2.3f, FieldWhite, FieldCyan, 1f, 0.02f, 0.5f, order: 6);
        Add(Shape.Ring, c, Vector2.zero, life - 0.25f, r * 2.3f, r * 2f, FieldCyan, FieldBlue, 0.9f, 0.05f, 0.6f, delay: 0.25f, twinkle: 0.2f, twinkleSpeed: 20f, order: 6);
        Add(Shape.Glow, c, Vector2.zero, 0.25f, 0.4f, 1.6f, FieldWhite, FieldCyan, 0.9f, 0.02f, 0.4f, order: 7);
        Impact(c, r + 2f, FieldCyan, 0.08f, 0.06f, 0.15f);

        // 六角格子 (蜂の巣) が内から外へ順に点る
        float h = r * 0.24f;
        float dx = h * 1.5f, dy = h * 1.732f;
        for (int i = -5; i <= 5; i++)
        {
            for (int j = -5; j <= 5; j++)
            {
                float x = i * dx, y = j * dy + (i % 2 == 0 ? 0f : dy * 0.5f);
                float dist = FxMath.Sqrt(x * x + y * y);
                if (dist > r - h * 0.6f) continue;
                HexCell(Off(c, x, y), h * 0.92f, 0.008f, 0.5f, 0.15f + dist / r * 0.35f, FieldBlue, FieldWhite, 0.55f + 0.4f * (dist / r));
            }
        }

        // 縁を走る放電の粒
        for (int i = 0; i < 16; i++)
        {
            float a = Rnd(0f, 2f * FxMath.PI);
            Vector2 p = Off(c, FxMath.Cos(a) * r, FxMath.Sin(a) * r);
            Add(Shape.Star, p, FxMath.V2(-FxMath.Sin(a) * 1.5f, FxMath.Cos(a) * 1.5f), Rnd(0.2f, 0.35f), Rnd(0.08f, 0.14f), 0.02f, FieldWhite, FieldCyan, 1f, 0.02f, 0.5f, delay: Rnd(0.2f, 0.8f), stretch: 0.1f, order: 7);
        }
    }

    // 弾き出された: 当たった所の六角形が数枚光って波紋が広がり、火花が外へ散る
    private static void SpawnForceRepel(Vector2 c)
    {
        Add(Shape.Glow, c, Vector2.zero, 0.5f, 0.4f, 1.2f, FieldDeep, FieldDeep, 0.5f, 0.05f, 0.5f, order: 0);
        Add(Shape.Glow, c, Vector2.zero, 0.25f, 0.3f, 1f, FieldWhite, FieldCyan, 0.9f, 0.02f, 0.4f, order: 7);

        const float h = 0.2f;
        HexCell(c, h, 0.01f, 0.45f, 0f, FieldBlue, FieldWhite, 1f);
        for (int k = 0; k < 6; k++)
        {
            float a = k * 60f * FxMath.Deg2Rad;
            HexCell(Off(c, FxMath.Cos(a) * h * 1.732f, FxMath.Sin(a) * h * 1.732f), h, 0.008f, 0.4f, 0.04f, FieldBlue, FieldCyan, 0.7f);
        }

        for (int i = 0; i < 12; i++)
            Add(Shape.Star, c, Dir() * Rnd(1.2f, 2.6f), Rnd(0.2f, 0.35f), Rnd(0.05f, 0.09f), 0.02f, FieldWhite, FieldCyan, 1f, 0.01f, 0.5f, drag: 3f, stretch: 0.1f, order: 8);
    }

    // ── 女神 ───────────────────────────────────────────────────

    private static readonly Color DivineWhite = new(1f, 0.98f, 0.92f);
    private static readonly Color Marble = new(0.86f, 0.84f, 0.8f);
    private static readonly Color DivineDeep = new(0.2f, 0.14f, 0.05f);

    // 反撃の構え (本人の画面だけ): 頭上に金の光輪が降りてきて止まり、天から細い光が差し、金の塵が舞い降りる。足元に二重の輪
    private static void SpawnGoddessGuard(Vector2 c)
    {
        float q = Active.Count > 1200 ? 0.5f : 1f;
        const float life = 1.8f;
        Vector2 halo = Off(c, 0f, 0.62f);
        Vector2 f = Off(c, 0f, Feet.y);

        Add(Shape.Glow, c, Vector2.zero, life, 1.8f, 2.2f, DivineDeep, DivineDeep, 0.55f, 0.15f, 0.7f, order: 0);
        Add(Shape.Glow, Off(c, 0f, 1.5f), Vector2.zero, life, 0.6f, 1.6f, GauntletGold, DivineWhite, 0.5f, 0.2f, 0.6f, rot: 0f, sy0: 2.5f, sy1: 4f, order: 0);
        Add(Shape.Glow, Off(c, 0f, 1.5f), Vector2.zero, life, 0.12f, 0.3f, DivineWhite, GauntletGold, 0.7f, 0.2f, 0.6f, rot: 0f, sy0: 2.5f, sy1: 4f, twinkle: 0.2f, twinkleSpeed: 6f, order: 1);

        // 光輪: 上から降りてきて止まる (暗い縁 + 金 + 白い芯)
        Add(Shape.Ring, Off(halo, 0f, 0.6f), FxMath.V2(0f, -2.4f), 0.25f, 0.5f, 0.55f, GauntletGold, GauntletGold, 0.6f, 0.1f, 1f, rot: 0f, sy0: 0.16f, sy1: 0.18f, order: 6);
        Add(Shape.Ring, halo, Vector2.zero, life - 0.25f, 0.8f, 0.82f, DivineDeep, DivineDeep, 0.8f, 0.05f, 0.7f, delay: 0.25f, rot: 0f, sy0: 0.3f, sy1: 0.3f, order: 5);
        Add(Shape.Ring, halo, Vector2.zero, life - 0.25f, 0.74f, 0.76f, GauntletGold, DivineWhite, 1f, 0.05f, 0.7f, delay: 0.25f, rot: 0f, sy0: 0.26f, sy1: 0.27f, twinkle: 0.2f, twinkleSpeed: 8f, order: 6);
        Add(Shape.Glow, halo, Vector2.zero, life - 0.25f, 0.8f, 0.9f, GauntletGold, GauntletGold, 0.4f, 0.05f, 0.7f, delay: 0.25f, rot: 0f, sy0: 0.3f, sy1: 0.3f, order: 4);
        Add(Shape.Star, halo, Vector2.zero, 0.3f, 0.8f, 0.1f, DivineWhite, GauntletGold, 1f, 0.01f, 0.3f, delay: 0.25f, spin: 120f, order: 7);

        FloorPool(f, 1.8f, GauntletGold, DivineDeep, 0.55f, life - 0.3f, 0.3f);

        for (int i = 0; i < 26 * q; i++)
        {
            Add(Shape.Star, Off(c, Rnd(-0.7f, 0.7f), Rnd(0.8f, 2f)), FxMath.V2(Rnd(-0.1f, 0.1f), Rnd(-0.6f, -0.3f)), Rnd(0.9f, 1.3f), Rnd(0.05f, 0.09f), 0.02f, DivineWhite, GauntletGold, 1f, 0.2f, 0.7f,
                delay: Rnd(0.2f, 0.7f), twinkle: 0.6f, twinkleSpeed: Rnd(10f, 18f), wobble: 0.05f, wobbleHz: 2f, order: 6);
        }
    }

    // 石になる: 女神の視線の 2 つの光が閃く → 足元から石のひびが這い上がり、体が大理石の色に塗り込められる → 砂がこぼれ、金の塵が残る
    private static void SpawnGoddessPetrify(Vector2 c)
    {
        float q = Active.Count > 1200 ? 0.5f : 1f;
        const float gaze = 0.2f;
        const float climb = 0.6f;

        for (int sgn = -1; sgn <= 1; sgn += 2)
        {
            Vector2 e = Off(c, sgn * 0.12f, 0.25f);
            Add(Shape.Star, e, Vector2.zero, 0.3f, 0.5f, 0.1f, DivineWhite, GauntletGold, 1f, 0.02f, 0.4f, spin: 200f * sgn, order: 10);
            Add(Shape.Glow, e, Vector2.zero, 0.3f, 0.6f, 0.2f, DivineWhite, GauntletGold, 0.8f, 0.02f, 0.4f, rot: 0f, sy0: 0.08f, sy1: 0.03f, order: 9);
        }

        Impact(c, 3f, GauntletGold, 0.1f, 0.08f, 0.2f, delay: gaze);
        Add(Shape.Glow, c, Vector2.zero, 1.6f, 1.6f, 1.8f, DivineDeep, DivineDeep, 0.5f, 0.1f, 0.7f, order: 0);

        // ひびが足元から上へ這い上がる
        for (int k = 0; k < 7; k++)
        {
            Skel[0] = Off(c, Rnd(-0.32f, 0.32f), -0.5f);
            for (int j = 1; j <= 5; j++) Skel[j] = Off(Skel[j - 1], Rnd(-0.12f, 0.12f), Rnd(0.16f, 0.24f));

            // 暗い裂け目の中に、石の明るい断面 (芯) が同じ道筋で見える
            int n = Crack(Skel, 6, 0.036f, StoneDark, 1f, gaze + k * 0.02f, climb, 1.6f, 0.7f, false, 20);
            Lines(LineBuf, n, new FxLines.Spec
            {
                Width = 0.012f, Taper = FxLines.Taper.Trunk, Core = StonePale, CoreAlpha = 1f, Delay = gaze + k * 0.02f, Grow = climb, Decel = true, Life = 1.6f, FadeOutFrom = 0.5f
            }, 21);
        }

        // 大理石色が下から塗り込められる (重ねるほど濃くなる)
        for (int j = 0; j < 4; j++)
            Add(Shape.CrewSil, c, Vector2.zero, 1.3f - j * 0.12f, 1f, 1f, Marble, Stone, 0.3f + j * 0.15f, 0.2f, 0.75f, delay: gaze + j * (climb / 4f), rot: 0f, order: 5);

        Add(Shape.Glow, c, Vector2.zero, 0.3f, 0.6f, 1.8f, DivineWhite, Marble, 0.7f, 0.05f, 0.5f, delay: gaze + climb, order: 8);

        for (int i = 0; i < 26 * q; i++)
        {
            Add(Shape.Cloud, Off(c, Rnd(-0.3f, 0.3f), Rnd(-0.45f, 0.2f)), FxMath.V2(Rnd(-0.3f, 0.3f), Rnd(-0.6f, -0.2f)), Rnd(0.6f, 1f), Rnd(0.05f, 0.09f), 0.02f, StonePale, StoneDark, 1f, 0.05f, 0.6f,
                delay: gaze + climb + Rnd(0f, 0.4f), order: 6);
        }

        for (int i = 0; i < 14 * q; i++)
            Add(Shape.Star, Off(c, Rnd(-0.5f, 0.5f), Rnd(-0.3f, 0.6f)), FxMath.V2(0f, Rnd(0.1f, 0.3f)), Rnd(0.8f, 1.2f), Rnd(0.04f, 0.07f), 0.02f, DivineWhite, GauntletGold, 1f, 0.2f, 0.6f,
                delay: gaze + climb + Rnd(0f, 0.3f), twinkle: 0.6f, twinkleSpeed: 16f, order: 7);
    }

    // ── チェインバインダー ─────────────────────────────────────────

    private static readonly Color IronDark = new(0.16f, 0.15f, 0.17f);
    private static readonly Color IronMid = new(0.48f, 0.47f, 0.5f);
    private static readonly Color IronHot = new(1f, 0.45f, 0.15f);

    // 鎖で繋ぐ: 2 人の足元に枷の輪が閉じ、両端から鎖の輪が 1 つずつ伸びて真ん中で噛み合い、火花が散る → 鎖がぴんと張って赤熱し、冷えて消える
    private static void SpawnChainBind(Vector2 a, int otherId)
    {
        PlayerControl other = otherId is >= 0 and <= 254 ? Utils.GetPlayerById((byte)otherId) : null;
        Vector2 b = other ? other.Pos() : Off(a, 1.5f, 0f);
        float dx = b.x - a.x, dy = b.y - a.y;
        float len = FxMath.Sqrt(dx * dx + dy * dy);
        if (len < 0.2f) return;

        float ux = dx / len, uy = dy / len;
        float rot = FxMath.Atan2(dy, dx) * FxMath.Rad2Deg;
        const float link = 0.27f;
        int count = (int)FxMath.Min(60f, len / link);
        const float grow = 0.35f;
        const float hold = 1f;

        for (int e = 0; e < 2; e++)
        {
            Vector2 end = e == 0 ? a : b;
            Vector2 f = Off(end, 0f, Feet.y);
            Add(Shape.Ring, f, Vector2.zero, grow + hold, 0.9f, 0.55f, IronDark, IronDark, 1f, 0.05f, 0.75f, rot: 0f, sy0: 0.35f, sy1: 0.2f, order: 3);
            Add(Shape.Ring, f, Vector2.zero, grow + hold, 0.85f, 0.5f, IronMid, IronHot, 0.9f, 0.05f, 0.75f, rot: 0f, sy0: 0.32f, sy1: 0.18f, order: 4);
            Impact(end, 2f, IronHot, 0.05f, 0.08f, 0.15f, delay: 0.05f);
        }

        // 鎖の下地: 明るい床でも鎖の線が読めるよう、暗い太線を先に敷く
        Line2(a, b, 0.07f, grow + hold, IronDark, IronMid, 0.75f, 0.3f, 0.8f, coreFadeFrom: 0.2f);

        // 鎖の輪: 寝た輪と立った輪を交互に。両端から真ん中へ順に現れる
        for (int i = 0; i <= count; i++)
        {
            float t = (float)i / count;
            Vector2 p = Off(a, ux * len * t, uy * len * t);
            float d = grow * (1f - FxMath.Min(t, 1f - t) * 2f);
            bool lying = i % 2 == 0;
            float sx = lying ? 0.36f : 0.3f, sy = lying ? 0.18f : 0.07f;
            Add(Shape.Ring, p, Vector2.zero, hold + grow - d, sx * 1.15f, sx * 1.15f, IronDark, IronDark, 1f, 0.03f, 0.8f, delay: d, rot: rot, sy0: sy * 1.3f, sy1: sy * 1.3f, order: 5);
            Add(Shape.Ring, p, Vector2.zero, hold + grow - d, sx, sx, IronMid, IronHot, 1f, 0.03f, 0.8f, delay: d, rot: rot, sy0: sy, sy1: sy, colorMid: IronMid, order: 6);
        }

        // 噛み合った瞬間の火花と、赤熱して冷えていく芯
        Vector2 mid = Off(a, dx * 0.5f, dy * 0.5f);
        Add(Shape.Star, mid, Vector2.zero, 0.3f, 0.9f, 0.1f, WindWhite, IronHot, 1f, 0.01f, 0.3f, delay: grow, spin: 200f, order: 8);
        for (int i = 0; i < 16; i++)
            Add(Shape.Star, mid, Dir() * Rnd(1.2f, 2.8f), Rnd(0.25f, 0.45f), Rnd(0.05f, 0.09f), 0.02f, BlazeYellow, IronHot, 1f, 0.01f, 0.5f, drag: 3f, delay: grow, stretch: 0.12f, rise: -1f, order: 8);
        Add(Shape.Glow, mid, Vector2.zero, hold, len * 0.9f, len, IronHot, BlazeRed, 0.35f, 0.05f, 0.6f, delay: grow, rot: rot, sy0: 0.12f, sy1: 0.06f, order: 4);
    }

    // ── 人狼: 暴走の始まり (本人の画面だけ) ──────────────────────────

    // 目が赤く光り (横長の 2 点) → 体の奥から赤黒い気が噴き、足元に爪痕の輪が刻まれる → 赤い火の粉が昇る
    private static void SpawnWerewolfRampage(Vector2 c)
    {
        float q = Active.Count > 1200 ? 0.5f : 1f;
        Vector2 f = Off(c, 0f, Feet.y);
        const float life = 1.6f;

        for (int sgn = -1; sgn <= 1; sgn += 2)
        {
            Vector2 e = Off(c, 0.1f + sgn * 0.09f, 0.18f);
            Add(Shape.Glow, e, Vector2.zero, life * 0.7f, 0.25f, 0.2f, WolfRed, ClockDeep, 1f, 0.05f, 0.6f, rot: 0f, sy0: 0.08f, sy1: 0.06f, twinkle: 0.3f, twinkleSpeed: 12f, order: 10);
            Add(Shape.Glow, e, Vector2.zero, life * 0.7f, 0.12f, 0.1f, WindWhite, WolfRed, 1f, 0.05f, 0.6f, rot: 0f, sy0: 0.035f, sy1: 0.03f, order: 11);
        }

        Add(Shape.Glow, c, Vector2.zero, life, 1.8f, 2.4f, ClockVoid, ClockVoid, 0.6f, 0.1f, 0.6f, order: 0);
        Add(Shape.Glow, c, Vector2.zero, life, 1.1f, 1.6f, WolfRed, ClockDeep, 0.45f, 0.1f, 0.6f, twinkle: 0.3f, twinkleSpeed: 5f, order: 1);
        Impact(c, 3f, WolfRed, 0.08f, 0.12f, 0.3f, delay: 0.1f);

        // 赤黒い気が体の縁から炎の舌のように昇る
        for (int i = 0; i < 26 * q; i++)
        {
            float side = i % 2 == 0 ? -1f : 1f;
            Add(Shape.Glow, Off(c, side * Rnd(0.15f, 0.38f), Rnd(-0.4f, 0.2f)), FxMath.V2(side * Rnd(0f, 0.2f), Rnd(1f, 1.6f)), Rnd(0.45f, 0.7f), 0.35f, 0.1f, WolfRed, ClockVoid, 0.85f, 0.1f, 0.5f,
                drag: 1.4f, delay: Rnd(0.05f, life - 0.6f), stretch: 0.3f, order: 3);
        }

        // 足元に刻まれる爪痕の輪 (3 本ずつ 4 方向)
        for (int k = 0; k < 4; k++)
        {
            float a = (k * 90f + 45f) * FxMath.Deg2Rad;
            for (int j = 0; j < 3; j++)
            {
                float o = (j - 1) * 0.08f;
                Vector2 p0 = Off(f, FxMath.Cos(a) * 0.45f - FxMath.Sin(a) * o, (FxMath.Sin(a) * 0.45f + FxMath.Cos(a) * o) * 0.4f);
                Vector2 p1 = Off(f, FxMath.Cos(a) * 0.85f - FxMath.Sin(a) * o, (FxMath.Sin(a) * 0.85f + FxMath.Cos(a) * o) * 0.4f);
                Line2(p0, p1, 0.012f, life - 0.2f, ClockVoid, WolfRed, 0.9f, 0.05f, 0.6f, delay: 0.15f + k * 0.05f + j * 0.02f, coreFadeFrom: 0.4f);
            }
        }

        for (int i = 0; i < 18 * q; i++)
            Add(Shape.Star, Off(c, Rnd(-0.5f, 0.5f), Rnd(-0.4f, 0.3f)), FxMath.V2(Rnd(-0.2f, 0.2f), Rnd(0.3f, 0.7f)), Rnd(0.6f, 1f), Rnd(0.05f, 0.09f), 0.02f, BlazeYellow, WolfRed, 1f, 0.1f, 0.6f,
                delay: Rnd(0.2f, 0.9f), rise: 0.6f, twinkle: 0.6f, twinkleSpeed: 16f, order: 7);
    }

    // ── ペスティレンス変化 (本人の画面だけ) ─────────────────────────

    private static readonly Color PlagueLime = new(0.7f, 0.95f, 0.25f);
    private static readonly Color PlagueGreen = new(0.3f, 0.5f, 0.1f);
    private static readonly Color PlagueDark = new(0.08f, 0.1f, 0.03f);

    // 体が一度黒く沈む → 腐った緑の瘴気が足元から渦を巻いて噴き上がり、蝿の群れが体の周りを飛び回る → 黒い輪が広がって瘴気が残る
    private static void SpawnPestilenceRise(Vector2 c)
    {
        float q = Active.Count > 1200 ? 0.5f : 1f;
        Vector2 f = Off(c, 0f, Feet.y);
        const float sink = 0.3f;

        Add(Shape.CrewSil, c, Vector2.zero, 0.6f, 1f, 1f, PlagueDark, PlagueGreen, 0.9f, 0.4f, 0.6f, rot: 0f, order: 5);
        Add(Shape.Glow, c, Vector2.zero, 2f, 2f, 2.8f, PlagueDark, PlagueDark, 0.65f, 0.1f, 0.7f, order: 0);
        Impact(c, 3f, PlagueGreen, 0.12f, 0.1f, 0.25f, delay: sink);

        // 渦を巻いて噴き上がる瘴気
        for (int i = 0; i < 30 * q; i++)
        {
            float a = Rnd(0f, 2f * FxMath.PI);
            Vector2 p = Off(f, FxMath.Cos(a) * 0.55f, FxMath.Sin(a) * 0.2f);
            Add(Shape.Cloud, p, FxMath.V2(-FxMath.Sin(a) * 0.9f, Rnd(0.6f, 1.2f)), Rnd(0.8f, 1.2f), 0.2f, Rnd(0.5f, 0.8f), PlagueGreen, PlagueDark, 0.7f, 0.1f, 0.5f,
                drag: 1.2f, delay: sink + Rnd(0f, 0.8f), order: 3);
        }

        FloorKick(f, 1f, PlagueGreen, PlagueDark, 0.6f, sink, 14);

        // 蝿: 体の周りをせわしなく回る黒い点
        for (int i = 0; i < 22 * q; i++)
        {
            float a = Rnd(0f, 2f * FxMath.PI);
            float rr = Rnd(0.45f, 0.8f);
            Add(Shape.Star, Off(c, FxMath.Cos(a) * rr, FxMath.Sin(a) * rr * 0.8f), FxMath.V2(-FxMath.Sin(a) * 2f, FxMath.Cos(a) * 1.6f), Rnd(0.9f, 1.4f), 0.06f, 0.05f, PlagueDark, PlagueDark, 1f, 0.1f, 0.8f,
                delay: sink + Rnd(0f, 0.4f), wobble: 0.25f, wobbleHz: Rnd(6f, 10f), order: 8);
        }

        for (int i = 0; i < 14 * q; i++)
            Add(Shape.Glow, Off(c, Rnd(-0.6f, 0.6f), Rnd(-0.3f, 0.6f)), FxMath.V2(0f, Rnd(0.1f, 0.3f)), Rnd(0.8f, 1.3f), 0.08f, 0.14f, PlagueLime, PlagueGreen, 0.9f, 0.15f, 0.6f,
                delay: sink + Rnd(0.1f, 0.8f), twinkle: 0.5f, twinkleSpeed: 6f, order: 7);
    }

    // ── マイコロジスト (本人の画面だけ) ───────────────────────────

    private static readonly Color SporePink = new(1f, 0.6f, 0.75f);
    private static readonly Color SporeViolet = new(0.62f, 0.35f, 0.75f);
    private static readonly Color Mycelium = new(0.95f, 0.9f, 0.8f);
    private static readonly Color SporeDeep = new(0.18f, 0.08f, 0.16f);

    // 足元から菌糸が枝分かれしながら感染の範囲まで這い広がり → ぽふっと胞子が噴いて、ふわふわ漂いながら範囲いっぱいに広がる
    private static void SpawnSporeCloud(Vector2 c, float radius)
    {
        float q = Active.Count > 1200 ? 0.5f : 1f;
        float r = FxMath.Clamp(radius, 0.5f, 6f);
        Vector2 f = Off(c, 0f, Feet.y);
        const float life = 2.2f;

        Add(Shape.Glow, f, Vector2.zero, life, r * 1.4f, r * 2.2f, SporeDeep, SporeDeep, 0.45f, 0.2f, 0.7f, flat: true, order: 0);

        // 菌糸
        for (int k = 0; k < 10; k++)
        {
            float a = k * 36f * FxMath.Deg2Rad + Rnd(-0.2f, 0.2f);
            Vector2 p = f;
            int steps = 6;
            for (int j = 0; j < steps; j++)
            {
                a += Rnd(-0.4f, 0.4f);
                float seg = r / steps;
                Vector2 n = Off(p, FxMath.Cos(a) * seg, FxMath.Sin(a) * seg * 0.4f);
                Line2(p, n, 0.008f, life - j * 0.1f, SporeViolet, Mycelium, 0.85f, 0.05f, 0.7f, delay: j * 0.06f, coreFadeFrom: 0.5f);
                if (j == 3)
                {
                    float b = a + (k % 2 == 0 ? 0.7f : -0.7f);
                    Line2(n, Off(n, FxMath.Cos(b) * seg, FxMath.Sin(b) * seg * 0.4f), 0.006f, life - 0.4f, SporeViolet, Mycelium, 0.7f, 0.05f, 0.7f, delay: 0.25f);
                }
                p = n;
            }
        }


        // 胞子の噴き出しと漂い
        for (int i = 0; i < 8; i++)
            Add(Shape.Cloud, c, Dir() * Rnd(0.6f, 1.2f), 0.9f, 0.3f, 0.9f, SporePink, SporeViolet, 0.5f, 0.05f, 0.5f, drag: 2f, delay: 0.35f, order: 3);

        for (int i = 0; i < 46 * q; i++)
        {
            Vector2 d = Dir();
            float sp = Rnd(0.3f, 1f) * r;
            Add(Shape.Glow, c, FxMath.V2(d.x * sp, d.y * sp * 0.7f), Rnd(1.4f, 1.8f), Rnd(0.08f, 0.14f), 0.06f, SporePink, SporeViolet, 0.9f, 0.1f, 0.65f, drag: 1.2f, delay: 0.35f + Rnd(0f, 0.3f),
                rise: 0.15f, wobble: 0.08f, wobbleHz: Rnd(1.5f, 3f), twinkle: 0.3f, twinkleSpeed: 4f, order: 4);
        }
    }

    // ── サノス: 石を得る / 使う (本人の画面だけ) ─────────────────────

    // 得る: 死体から石の色の欠片が光りながら浮かび、回って弾ける / 使う: 体の周りを石の色の光が 1 周し、波紋と色の光が広がる
    private static void SpawnStoneGain(Vector2 c, int code)
    {
        bool use = code >= 10;
        int k = (use ? code - 10 : code) % 6;
        if (k < 0) k = 0;
        Color col = StoneColors[k];
        float q = Active.Count > 1200 ? 0.5f : 1f;

        Add(Shape.Glow, c, Vector2.zero, 1.2f, 1.2f, 1.6f, VoidBlack, VoidBlack, 0.45f, 0.1f, 0.6f, order: 0);

        if (!use)
        {
            Vector2 top = Off(c, 0f, 0.9f);
            FloorPool(Off(c, 0f, Feet.y), 1.6f, col, col, 0.55f, 0.6f);
            Add(Shape.Glow, c, FxMath.V2(0f, 1.6f), 0.55f, 0.6f, 0.9f, col, col, 0.75f, 0.05f, 0.8f, drag: 1.5f, order: 6);
            Add(Shape.Star, c, FxMath.V2(0f, 1.6f), 0.55f, 0.45f, 0.7f, WindWhite, col, 1f, 0.05f, 0.8f, drag: 1.5f, spin: 400f, order: 7);
            Add(Shape.Star, top, Vector2.zero, 0.35f, 1.8f, 0.2f, WindWhite, col, 1f, 0.01f, 0.3f, delay: 0.55f, spin: 160f, order: 8);
            Add(Shape.Glow, top, Vector2.zero, 0.4f, 0.5f, 2f, col, col, 0.5f, 0.01f, 0.4f, delay: 0.55f, order: 5);
            for (int i = 0; i < 20 * q; i++)
                Add(Shape.Star, top, Dir() * Rnd(0.8f, 2f), Rnd(0.3f, 0.5f), Rnd(0.05f, 0.08f), 0.02f, WindWhite, col, 1f, 0.01f, 0.5f, drag: 3f, delay: 0.55f, twinkle: 0.5f, twinkleSpeed: 18f, order: 8);
            return;
        }

        for (int i = 0; i < 18; i++)
        {
            float a = i / 18f * 2f * FxMath.PI;
            Vector2 p = Off(c, FxMath.Cos(a) * 0.6f, FxMath.Sin(a) * 0.45f);
            Add(Shape.Star, p, FxMath.V2(-FxMath.Sin(a) * 1.2f, FxMath.Cos(a) * 0.9f), 0.5f, 0.12f, 0.04f, WindWhite, col, 1f, 0.05f, 0.7f, delay: i * 0.015f, stretch: 0.08f, order: 7);
        }

        Add(Shape.Glow, c, Vector2.zero, 0.5f, 0.6f, 2.2f, col, col, 0.55f, 0.03f, 0.4f, delay: 0.3f, order: 2);
        FloorPool(Off(c, 0f, Feet.y), 2.2f, col, col, 0.6f, 0.6f, 0.3f);
        Impact(c, 3f, col, 0.1f, 0.06f, 0.15f, delay: 0.3f);
    }

    // ── リフトメイカー: 裂け目 (本人の画面だけ) ─────────────────────

    // 空間に縦の裂け目が走って開き (暗い芯 + 紫の縁 + 白い刃先)、縁で放電がはぜ、周りの粒が吸い込まれて閉じる
    private static void SpawnRiftTear(Vector2 c)
    {
        float q = Active.Count > 1200 ? 0.5f : 1f;
        const float open = 0.18f;
        const float life = 1.1f;
        Vector2 m = Off(c, 0f, 0.1f);

        Add(Shape.Glow, m, Vector2.zero, life, 1.8f, 2.2f, VoidBlack, VoidBlack, 0.7f, 0.1f, 0.7f, rot: 0f, sy0: 2.4f, sy1: 2.6f, order: 0);
        Line2(Off(m, 0f, -1.1f), Off(m, 0f, 1.1f), 0.008f, open + 0.05f, VoidPurple, WindWhite, 1f, 0.6f, 1f);
        Add(Shape.Glow, m, Vector2.zero, life - open, 0.08f, 0.8f, VoidMagenta, VoidPurple, 0.95f, 0.05f, 0.7f, delay: open, rot: 0f, sy0: 2.3f, sy1: 2.6f, twinkle: 0.2f, twinkleSpeed: 9f, order: 5);
        Add(Shape.Glow, m, Vector2.zero, life - open, 0.05f, 0.5f, VoidBlack, VoidBlack, 1f, 0.05f, 0.7f, delay: open, rot: 0f, sy0: 2f, sy1: 2.3f, order: 6);
        Add(Shape.Glow, m, Vector2.zero, life - open, 0.03f, 0.1f, WindWhite, VoidRim, 0.95f, 0.05f, 0.6f, delay: open, rot: 0f, sy0: 2.3f, sy1: 2.5f, twinkle: 0.4f, twinkleSpeed: 22f, order: 7);
        Impact(c, 2f, VoidPurple, 0.08f, 0.05f, 0.15f, delay: open);

        for (int i = 0; i < 4; i++)
            MicroBolt(Off(m, (i % 2 == 0 ? -1f : 1f) * 0.12f, Rnd(-0.5f, 0.5f)), i % 2 == 0 ? -1f : 1f, VoidDeep, VoidMagenta, open + i * 0.12f);

        for (int i = 0; i < 24 * q; i++)
        {
            Vector2 d = Dir();
            float rr = Rnd(0.7f, 1.3f);
            Vector2 st = Off(m, d.x * rr, d.y * rr);
            float lf = Rnd(0.4f, 0.6f);
            Add(Shape.Star, st, FxMath.V2((m.x - st.x) / lf, (m.y - st.y) / lf), lf, 0.09f, 0.03f, VoidRim, VoidPurple, 1f, 0.1f, 0.8f, delay: open + Rnd(0f, 0.4f), stretch: 0.06f, order: 8);
        }
    }

    // ── ポータルメイカー ───────────────────────────────────────

    private static readonly Color PortalWhite = new(0.9f, 1f, 1f);
    private static readonly Color PortalTeal = new(0.2f, 0.95f, 0.85f);
    private static readonly Color PortalBlue = new(0.15f, 0.45f, 1f);
    private static readonly Color PortalDeep = new(0.02f, 0.08f, 0.18f);

    // 通り抜けた瞬間: 床の渦が速く回って光の腕が巻き込み、中心が白く閃き、粒が渦に沿って飛ぶ
    private static void SpawnPortalPass(Vector2 c)
    {
        float q = Active.Count > 1200 ? 0.5f : 1f;
        const float life = 0.9f;
        Vector2 f = Off(c, 0f, Feet.y * 0.5f);

        Add(Shape.Glow, f, Vector2.zero, life, 1.4f, 1.8f, PortalDeep, PortalDeep, 0.6f, 0.05f, 0.6f, flat: true, order: 0);
        for (int k = 0; k < 6; k++)
        {
            Add(Shape.Ray, f, Vector2.zero, life, 1.2f, 0.8f, PortalTeal, PortalBlue, 0.9f, 0.05f, 0.6f, rot: k * 60f, spin: 720f, sy0: 0.1f, sy1: 0.05f, flat: true, order: 3);
            Add(Shape.Ray, f, Vector2.zero, life, 0.8f, 0.55f, PortalWhite, PortalTeal, 0.95f, 0.05f, 0.6f, rot: k * 60f + 20f, spin: 720f, sy0: 0.04f, sy1: 0.025f, flat: true, order: 4);
        }

        Add(Shape.Glow, c, Vector2.zero, 0.3f, 0.5f, 1.8f, PortalWhite, PortalTeal, 0.95f, 0.02f, 0.4f, order: 6);
        Add(Shape.Glow, Off(c, 0f, 0.8f), Vector2.zero, 0.6f, 0.5f, 0.9f, PortalTeal, PortalBlue, 0.55f, 0.02f, 0.5f, rot: 0f, sy0: 2.4f, sy1: 3.4f, order: 4);
        Add(Shape.Glow, Off(c, 0f, 0.8f), Vector2.zero, 0.5f, 0.15f, 0.3f, PortalWhite, PortalTeal, 0.9f, 0.02f, 0.5f, rot: 0f, sy0: 2.4f, sy1: 3.4f, order: 5);

        for (int i = 0; i < 24 * q; i++)
        {
            float a = Rnd(0f, 2f * FxMath.PI);
            Vector2 p = Off(f, FxMath.Cos(a) * 0.7f, FxMath.Sin(a) * 0.28f);
            Add(Shape.Star, p, FxMath.V2(-FxMath.Sin(a) * 2.2f, FxMath.Cos(a) * 0.9f + 0.8f), Rnd(0.35f, 0.55f), Rnd(0.06f, 0.1f), 0.02f, PortalWhite, PortalTeal, 1f, 0.02f, 0.6f, drag: 2f, stretch: 0.08f, order: 7);
        }
    }

    // ── プロボウラー ───────────────────────────────────────────

    private static readonly Color PinWhite = new(1f, 0.98f, 0.95f);
    private static readonly Color PinRed = new(0.9f, 0.1f, 0.12f);
    private static readonly Color LaneWood = new(0.75f, 0.55f, 0.3f);

    // ストライク: 重い衝撃と床を走る衝撃波 → 白いピン 10 本が回りながら弾け飛び、木屑が散る → 黄色い星の閃き
    private static void SpawnBowlStrike(Vector2 c)
    {
        float q = Active.Count > 1200 ? 0.5f : 1f;
        Vector2 f = Off(c, 0f, Feet.y);

        Impact(c, 3f, PinWhite, 0.15f, 0.2f, 0.3f);
        Add(Shape.Glow, c, Vector2.zero, 0.25f, 0.6f, 2f, WindWhite, BlazeYellow, 1f, 0.01f, 0.4f, order: 8);
        FloorKick(f, 0.9f, LaneWood, DustDark, 0.6f, 0f, 12);
        Add(Shape.Star, c, Vector2.zero, 0.35f, 1.6f, 0.2f, BlazeYellow, BlazeOrange, 1f, 0.01f, 0.4f, spin: 180f, order: 9);

        // ピン: 白い胴 + 赤い帯 (同じ動きの 2 枚を重ねる)
        for (int i = 0; i < 10; i++)
        {
            float a = (-20f + i * 22f + Rnd(-8f, 8f)) * FxMath.Deg2Rad;
            Vector2 v = FxMath.V2(FxMath.Cos(a) * Rnd(2f, 3.4f), FxMath.Sin(a) * Rnd(1.5f, 2.6f) + 1f);
            float spin = Rnd(-720f, 720f), rot = Rnd(0f, 360f);
            float lf = Rnd(0.8f, 1.1f);
            Add(Shape.Solid, c, v, lf, 0.09f, 0.09f, PinWhite, PinWhite, 1f, 0.01f, 0.7f, drag: 1.4f, rot: rot, spin: spin, sy0: 0.26f, sy1: 0.26f, rise: -2.2f, order: 7);
            Add(Shape.Solid, c, v, lf, 0.095f, 0.095f, PinRed, PinRed, 1f, 0.01f, 0.7f, drag: 1.4f, rot: rot, spin: spin, sy0: 0.035f, sy1: 0.035f, rise: -2.2f, order: 8);
        }

        for (int i = 0; i < 20 * q; i++)
            Add(Shape.Cloud, Off(c, 0f, -0.2f), Dir() * Rnd(0.8f, 2.2f), Rnd(0.4f, 0.7f), Rnd(0.06f, 0.1f), 0.03f, LaneWood, DustDark, 1f, 0.01f, 0.5f, drag: 2.5f, rise: -1f, order: 6);

        for (int i = 0; i < 14 * q; i++)
            Add(Shape.Star, c, Dir() * Rnd(1.5f, 3f), Rnd(0.3f, 0.5f), Rnd(0.06f, 0.1f), 0.02f, BlazeYellow, BlazeOrange, 1f, 0.01f, 0.5f, drag: 3f, stretch: 0.1f, order: 8);
    }

    // ── ペンギン ───────────────────────────────────────────────

    // 捕まえる: 足元が一瞬で凍り (氷の輪と放射状の霜)、氷の欠片が跳ね、雪の粒が舞う
    private static void SpawnPenguinGrab(Vector2 c)
    {
        float q = Active.Count > 1200 ? 0.5f : 1f;
        Vector2 f = Off(c, 0f, Feet.y);
        const float life = 1.2f;

        Add(Shape.Glow, f, Vector2.zero, life, 1.6f, 2f, WaterDeep, WaterDeep, 0.45f, 0.05f, 0.6f, flat: true, order: 0);
        Add(Shape.Glow, f, Vector2.zero, life, 0.4f, 1.6f, IceCyan, IceBlue, 0.6f, 0.02f, 0.6f, flat: true, order: 1);
        Impact(c, 2f, IceWhite, 0.1f, 0.08f, 0.15f);

        // 放射状の霜 (枝分かれする細い線)
        for (int k = 0; k < 8; k++)
        {
            float a = (k * 45f + Rnd(-10f, 10f)) * FxMath.Deg2Rad;
            Vector2 p = f;
            for (int j = 0; j < 3; j++)
            {
                Vector2 n = Off(p, FxMath.Cos(a) * 0.22f, FxMath.Sin(a) * 0.22f * 0.4f);
                Line2(p, n, 0.008f, life - 0.1f, IceBlue, IceWhite, 0.9f, 0.03f, 0.6f, delay: j * 0.04f);
                Line2(n, Off(n, FxMath.Cos(a + 0.6f) * 0.1f, FxMath.Sin(a + 0.6f) * 0.04f), 0.006f, life - 0.2f, IceBlue, IceWhite, 0.8f, 0.03f, 0.6f, delay: j * 0.04f + 0.03f);
                p = n;
            }
        }

        for (int i = 0; i < 14 * q; i++)
        {
            float s = Rnd(0.05f, 0.1f);
            Add(Shape.Solid, Off(c, 0f, -0.2f), Dir() * Rnd(1f, 2.2f), Rnd(0.4f, 0.6f), s, s * 0.5f, IceWhite, IceCyan, 1f, 0.01f, 0.5f, drag: 2.5f, rot: Rnd(0f, 360f), spin: Rnd(-400f, 400f), sy0: s * 2f, sy1: s, rise: -1.2f, order: 6);
        }

        for (int i = 0; i < 22 * q; i++)
            Add(Shape.Star, Off(c, Rnd(-0.8f, 0.8f), Rnd(-0.2f, 0.9f)), FxMath.V2(Rnd(-0.15f, 0.15f), Rnd(-0.3f, -0.1f)), Rnd(0.7f, 1.1f), Rnd(0.04f, 0.08f), 0.02f, IceWhite, IceCyan, 1f, 0.1f, 0.6f,
                delay: Rnd(0f, 0.3f), twinkle: 0.6f, twinkleSpeed: 14f, wobble: 0.05f, wobbleHz: 2f, order: 7);
    }

    // ── ポータル (開いている間ずっと) ──────────────────────────────

    private struct PortalEmitter
    {
        public bool Vision;
        public Vector2 Pos;
        public float NextBase;
        public float NextMote;
        public GameObject Machine;
    }

    private static readonly List<PortalEmitter> PortalEmitters = [];

    // ポータルは床に置いた機械 (ペットのボタンの絵と同じ紫の円盤) の上に浮かぶ。
    // 文字で描いた箱 (全員に見える) は機械と楕円の暗い下地で覆い隠す
    private static readonly Vector2 PortalLift = new(0f, 0.5f);
    private static readonly Vector2 MachineAt = new(0f, -0.32f);
    private const float PortalW = 0.48f;
    private const float PortalH = 0.72f;
    private const float PortalBaseLife = 1.1f;
    private const float PortalBaseEvery = 0.9f;

    // 開く: 縦に細い光が走って楕円に開き、粒が弾ける。以後は閉じるまで渦を巻き続ける (同じ場所に二重には開かない)
    private static void StartPortal(Vector2 mark)
    {
        Vector2 c = Off(mark, PortalLift.x, PortalLift.y);

        for (int i = 0; i < PortalEmitters.Count; i++)
        {
            if (FxMath.Abs(PortalEmitters[i].Pos.x - c.x) < 0.3f && FxMath.Abs(PortalEmitters[i].Pos.y - c.y) < 0.3f) return;
        }

        if (PortalEmitters.Count >= 4)
        {
            DestroyMachine(PortalEmitters[0]);
            PortalEmitters.RemoveAt(0);
        }

        FxSound.At("FxPortalOpen", c, 0.8f);
        PortalEmitters.Add(new PortalEmitter { Vision = _vision, Pos = c, NextBase = Time.time + 0.25f, NextMote = Time.time + 0.25f, Machine = BuildMachine(Off(mark, MachineAt.x, MachineAt.y)) });

        // 機械のレンズから光が真上へ伸びて、その先で楕円が開く
        Vector2 lens = Off(mark, MachineAt.x, MachineAt.y + 0.06f);
        Line2(lens, c, 0.02f, 0.35f, PortalBlue, PortalWhite, 1f, 0.2f, 0.6f);

        Line2(Off(c, 0f, -PortalH), Off(c, 0f, PortalH), 0.01f, 0.3f, PortalBlue, PortalWhite, 1f, 0.3f, 0.6f);
        Add(Shape.Glow, c, Vector2.zero, 0.35f, 0.1f, PortalW * 3f, PortalWhite, PortalTeal, 0.9f, 0.05f, 0.4f, delay: 0.15f, rot: 0f, sy0: 2.6f, sy1: 1.1f, order: 7);
        Impact(c, 2f, PortalTeal, 0.08f, 0.05f, 0.15f, delay: 0.15f);
        for (int i = 0; i < 18; i++)
            Add(Shape.Star, c, Dir() * Rnd(1f, 2.4f), Rnd(0.3f, 0.5f), Rnd(0.05f, 0.09f), 0.02f, PortalWhite, PortalTeal, 1f, 0.01f, 0.5f, drag: 3f, delay: 0.15f, order: 8);
    }

    // 閉じる: 楕円が縦の線に潰れ、中心へ光が吸い込まれて消える
    private static void ClosePortal(Vector2 mark)
    {
        Vector2 c = Off(mark, PortalLift.x, PortalLift.y);

        for (int i = PortalEmitters.Count - 1; i >= 0; i--)
        {
            if (FxMath.Abs(PortalEmitters[i].Pos.x - c.x) >= 0.3f || FxMath.Abs(PortalEmitters[i].Pos.y - c.y) >= 0.3f) continue;
            DestroyMachine(PortalEmitters[i]);
            PortalEmitters.RemoveAt(i);
        }

        ReleasePortalWindows();

        Add(Shape.Glow, c, Vector2.zero, 0.35f, PortalW * 3f, 0.05f, PortalTeal, PortalBlue, 0.9f, 0.01f, 0.5f, rot: 0f, sy0: 1.2f, sy1: 2.4f, order: 6);
        Add(Shape.Glow, c, Vector2.zero, 0.3f, 0.6f, 0.1f, PortalWhite, PortalTeal, 1f, 0.01f, 0.5f, order: 7);
        for (int i = 0; i < 16; i++)
        {
            Vector2 d = Dir();
            Vector2 st = Off(c, d.x * 1.2f, d.y * 1.2f);
            Add(Shape.Star, st, FxMath.V2((c.x - st.x) / 0.3f, (c.y - st.y) / 0.3f), 0.3f, 0.09f, 0.03f, PortalWhite, PortalTeal, 1f, 0.05f, 0.8f, stretch: 0.06f, order: 8);
        }
    }

    private static void PulsePortals()
    {
        float now = Time.time;
        Camera cam = Camera.main;
        if (cam)
        {
            Vector3 cp = cam.transform.position;
            UpdatePortalWindows(cp.x, cp.y);
        }

        for (int i = 0; i < PortalEmitters.Count; i++)
        {
            PortalEmitter e = PortalEmitters[i];
            _vision = e.Vision;

            if (now >= e.NextBase)
            {
                e.NextBase = now + PortalBaseEvery;
                if (Active.Count < 2000) SpawnPortalBase(e.Pos, now);
            }

            if (now >= e.NextMote)
            {
                e.NextMote = now + 0.12f;
                if (Active.Count < 1600) SpawnPortalMotes(e.Pos);
            }

            PortalEmitters[i] = e;
        }
    }

    // 1 周期ぶんの本体。寿命を周期より長くして次の周期と重ね、回転の角度は時刻から決めて継ぎ目を出さない
    private static void SpawnPortalBase(Vector2 c, float now)
    {
        const float life = PortalBaseLife;
        const float fi = 0.15f, fo = 0.85f;
        float spinRot = now * 160f % 360f;
        float slowRot = -now * 70f % 360f;

        // 箱を覆い隠す暗い楕円 (中心ほど濃い)
        Add(Shape.Glow, c, Vector2.zero, life, PortalW * 3.4f, PortalW * 3.4f, PortalDeep, VoidBlack, 1f, fi, fo, rot: 0f, sy0: PortalH * 3.4f, sy1: PortalH * 3.4f, order: 1);
        Add(Shape.Glow, c, Vector2.zero, life, PortalW * 2.4f, PortalW * 2.4f, VoidBlack, VoidBlack, 1f, fi, fo, rot: 0f, sy0: PortalH * 2.4f, sy1: PortalH * 2.4f, order: 2);

        // 縁: 外のにじみ + 青緑の輪 + 白い芯。少し脈打つ
        Add(Shape.Ring, c, Vector2.zero, life, PortalW * 2.25f, PortalW * 2.25f, PortalBlue, PortalBlue, 0.6f, fi, fo, rot: 0f, sy0: PortalH * 2.25f, sy1: PortalH * 2.25f, twinkle: 0.15f, twinkleSpeed: 3f, order: 3);
        Add(Shape.Ring, c, Vector2.zero, life, PortalW * 2f, PortalW * 2f, PortalTeal, PortalTeal, 1f, fi, fo, rot: 0f, sy0: PortalH * 2f, sy1: PortalH * 2f, twinkle: 0.2f, twinkleSpeed: 7f, order: 4);
        Add(Shape.Ring, c, Vector2.zero, life, PortalW * 1.93f, PortalW * 1.93f, PortalWhite, PortalWhite, 0.8f, fi, fo, rot: 0f, sy0: PortalH * 1.93f, sy1: PortalH * 1.93f, twinkle: 0.3f, twinkleSpeed: 11f, order: 5);

        // 渦: 中心から伸びる光の腕 4 本 (速い) と、紫の腕 3 本 (逆回り・遅い)
        for (int k = 0; k < 4; k++)
        {
            Add(Shape.Ray, c, Vector2.zero, life, PortalW * 1.05f, PortalW * 1.05f, PortalTeal, PortalBlue, 0.55f, fi, fo, rot: spinRot + k * 90f, spin: 160f, sy0: 0.12f, sy1: 0.12f, order: 3);
            Add(Shape.Ray, c, Vector2.zero, life, PortalW * 0.9f, PortalW * 0.9f, PortalWhite, PortalTeal, 0.5f, fi, fo, rot: spinRot + k * 90f, spin: 160f, sy0: 0.035f, sy1: 0.035f, order: 4);
        }

        for (int k = 0; k < 3; k++)
            Add(Shape.Ray, c, Vector2.zero, life, PortalW * 0.8f, PortalW * 0.8f, VoidPurple, VoidDeep, 0.35f, fi, fo, rot: slowRot + k * 120f, spin: -70f, sy0: 0.05f, sy1: 0.05f, order: 3);

        // 機械のレンズから楕円の下端へ立ちのぼる光
        float beamLen = PortalLift.y - MachineAt.y - PortalH * 0.9f;
        Vector2 beam = Off(c, 0f, -PortalH * 0.9f - beamLen * 0.5f);
        Add(Shape.Glow, beam, Vector2.zero, life, 0.35f, 0.35f, PortalTeal, PortalBlue, 0.55f, fi, fo, rot: 0f, sy0: beamLen * 2.6f, sy1: beamLen * 2.6f, twinkle: 0.25f, twinkleSpeed: 6f, order: 2);
        Add(Shape.Glow, beam, Vector2.zero, life, 0.1f, 0.1f, PortalWhite, PortalTeal, 0.9f, fi, fo, rot: 0f, sy0: beamLen * 2.4f, sy1: beamLen * 2.4f, twinkle: 0.3f, twinkleSpeed: 11f, order: 3);
        Vector2 lensAt = Off(c, 0f, MachineAt.y - PortalLift.y + 0.06f);
        Add(Shape.Glow, lensAt, Vector2.zero, life, 0.7f, 0.7f, PortalTeal, PortalBlue, 0.5f, fi, fo, rot: 0f, sy0: 0.35f, sy1: 0.35f, twinkle: 0.3f, twinkleSpeed: 3f, order: 2);

        // 奥の光
        Add(Shape.Glow, c, Vector2.zero, life, 0.55f, 0.55f, PortalTeal, PortalBlue, 0.25f, fi, fo, twinkle: 0.35f, twinkleSpeed: 2.5f, order: 4);
        Add(Shape.Glow, c, Vector2.zero, life, 0.18f, 0.18f, PortalWhite, PortalTeal, 0.5f, fi, fo, twinkle: 0.3f, twinkleSpeed: 4f, order: 5);
    }

    // 縁から中心へ螺旋を描いて吸い込まれる粒
    private static void SpawnPortalMotes(Vector2 c)
    {
        for (int i = 0; i < 2; i++)
        {
            float a = Rnd(0f, 2f * FxMath.PI);
            float ca = FxMath.Cos(a), sa = FxMath.Sin(a);
            Vector2 st = Off(c, ca * PortalW * 1.15f, sa * PortalH * 1.15f);
            const float life = 0.55f;
            Vector2 v = FxMath.V2((c.x - st.x) / life - sa * 0.8f, (c.y - st.y) / life + ca * 0.8f);
            Add(Shape.Star, st, v, life, Rnd(0.06f, 0.1f), 0.02f, PortalWhite, PortalTeal, 1f, 0.15f, 0.7f, stretch: 0.05f, order: 6);
        }
    }

    // ── ポータルの機械 (ペットのボタンの絵: 太い濃紫の縁取り・薄紫の円盤・ずんぐりした脚 3 本・レンズを囲む濃い帯・水色のレンズに白い星) ──

    private static Sprite _machineBody, _machineTop, _machineBand, _machineLens, _machineStar;
    private static readonly Color MachineOutline = new(0.25f, 0.13f, 0.42f);
    private static readonly Color MachineBody = new(0.68f, 0.52f, 0.9f);
    private static readonly Color MachineTopCol = new(0.82f, 0.7f, 0.98f);
    private static readonly Color MachineBandCol = new(0.42f, 0.27f, 0.68f);
    private static readonly Color MachineLensCol = new(0.7f, 0.9f, 1f);
    private const float MachineW = 1.2f;
    private const float MachineZ = 5f;

    // 斜め上から見た円盤 + 左・右・手前の脚 (外へ向かって細くなる丸い楔)。y は奥行きで縮んでいる
    private static float MachineBodyAlpha(float x, float y)
    {
        const float e = 0.03f;
        float a = Ell(x, y, 0f, 0.05f, 0.64f, 0.5f, e);
        a = FxMath.Max(a, Foot(x, y, -1f, -0.05f));
        a = FxMath.Max(a, Foot(x, y, 1f, -0.05f));
        a = FxMath.Max(a, Foot(x, y, 0f, -1f));
        return a;
    }

    private static float Foot(float x, float y, float dx, float dy)
    {
        float len = FxMath.Sqrt(dx * dx + dy * dy);
        dx /= len; dy /= len;
        float yy = y / 0.75f;
        float along = x * dx + yy * dy, perp = FxMath.Abs(-x * dy + yy * dx);
        if (along < 0.3f || along > 0.88f) return 0f;
        float half = 0.36f - 0.16f * (along - 0.3f) / 0.58f;
        float tip = FxMath.Clamp01((0.88f - along) / 0.06f);
        return FxMath.Clamp01((half - perp) / 0.03f) * tip;
    }

    private static float MachineTopAlpha(float x, float y) => Ell(x, y, 0f, 0.12f, 0.56f, 0.4f, 0.03f);

    private static float MachineBandAlpha(float x, float y) => Ell(x, y, 0f, 0f, 0.98f, 0.98f, 0.03f);

    private static float MachineLensAlpha(float x, float y) => Ell(x, y, 0f, 0f, 0.98f, 0.98f, 0.04f);

    // レンズの白い星: 長短 8 本の筋 + 芯
    private static float MachineStarAlpha(float x, float y)
    {
        float r = FxMath.Sqrt(x * x + y * y);
        if (r > 1f) return 0f;
        float ang = FxMath.Atan2(y, x);
        float a = FxMath.Clamp01((0.22f - r) / 0.06f);
        for (int k = 0; k < 8; k++)
        {
            float t = ang - k * FxMath.PI / 4f;
            if (FxMath.Cos(t) <= 0f) continue;
            float reach = k % 2 == 0 ? 0.92f : 0.6f;
            float w = (k % 2 == 0 ? 0.09f : 0.07f) * (1f - r / reach);
            float d = FxMath.Abs(FxMath.Sin(t)) * r;
            if (r < reach) a = FxMath.Max(a, FxMath.Clamp01((w - d) / 0.025f));
        }

        return a;
    }

    private static GameObject BuildMachine(Vector2 at)
    {
        if (!_machineBody) _machineBody = MakeSprite(256, 192, MachineBodyAlpha);
        if (!_machineTop) _machineTop = MakeSprite(256, 192, MachineTopAlpha);
        if (!_machineBand) _machineBand = MakeSprite(128, 128, MachineBandAlpha);
        if (!_machineLens) _machineLens = MakeSprite(128, 128, MachineLensAlpha);
        if (!_machineStar) _machineStar = MakeSprite(128, 128, MachineStarAlpha);
        if (!_machineBody || !_machineTop || !_machineBand || !_machineLens || !_machineStar) return null;

        // 床に置いた物として人の下に描く: 描画順は人や床と同じ 0 にして、奥行きで床 (z≈8) と人 (z≈0) の間に置く
        var root = new GameObject("EK_PortalMachine") { layer = 0 };
        root.transform.position = FxMath.V3(at.x, at.y, MachineZ);

        float w = MachineW, h = MachineW * 0.75f;
        Part(root, _glow, FxMath.V2(0f, -0.1f), FxMath.V2(w * 1.5f, h * 0.9f), FxMath.Rgba(0f, 0f, 0f, 0.45f), 0);
        Part(root, _machineBody, FxMath.V2(0f, -0.01f), FxMath.V2(w * 1.1f, h * 1.12f), MachineOutline, 1);
        Part(root, _machineBody, Vector2.zero, FxMath.V2(w, h), MachineBody, 2);
        Part(root, _machineTop, FxMath.V2(0f, 0.01f), FxMath.V2(w, h), MachineTopCol, 3);
        Part(root, _machineBand, FxMath.V2(0f, 0.05f), FxMath.V2(w * 0.5f, h * 0.45f), MachineOutline, 4);
        Part(root, _machineBand, FxMath.V2(0f, 0.05f), FxMath.V2(w * 0.46f, h * 0.41f), MachineBandCol, 5);
        Part(root, _machineLens, FxMath.V2(0f, 0.05f), FxMath.V2(w * 0.36f, h * 0.31f), MachineLensCol, 6);
        Part(root, _machineStar, FxMath.V2(0f, 0.05f), FxMath.V2(w * 0.34f, h * 0.3f), FxMath.Rgba(1f, 1f, 1f), 7);
        return root;
    }

    private static void Part(GameObject root, Sprite sprite, Vector2 offset, Vector2 size, Color color, int order)
    {
        var go = new GameObject("part") { layer = 0 };
        go.transform.SetParent(root.transform, false);
        go.transform.localPosition = FxMath.V3(offset.x, offset.y, -order * 0.002f);
        Rect r = sprite.rect;
        float ppu = sprite.pixelsPerUnit;
        go.transform.localScale = FxMath.V3(size.x / (r.width / ppu), size.y / (r.height / ppu), 1f);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.color = color;
        sr.sortingOrder = 0;
    }

    private static void DestroyMachine(PortalEmitter e)
    {
        if (e.Machine) Object.Destroy(e.Machine);
    }

    private static void ReleasePortalMachines()
    {
        foreach (PortalEmitter e in PortalEmitters) DestroyMachine(e);
    }

    // ── 床の反応 (輪の線の代わり。素材に合わせて煙・土・光・焦げで見せる) ──────────

    // 床で舞い上がる土煙・煙・瘴気: ふくらむ雲の粒を、楕円に沿って外へ這わせる
    private static void FloorKick(Vector2 f, float r, Color c0, Color c1, float alpha = 0.7f, float delay = 0f, int count = 14, float life = 0.9f)
    {
        float q = Active.Count > 1200 ? 0.5f : 1f;
        int n = (int)(count * q);

        for (int i = 0; i < n; i++)
        {
            float a = (i + Rnd(-0.3f, 0.3f)) / n * 2f * FxMath.PI;
            float ca = FxMath.Cos(a), sa = FxMath.Sin(a);
            float sp = Rnd(1.6f, 2.6f) * r;
            Add(Shape.Cloud, Off(f, ca * 0.25f * r, sa * 0.1f * r), FxMath.V2(ca * sp, sa * sp * 0.38f + Rnd(0.05f, 0.25f)), life * Rnd(0.8f, 1.2f), 0.25f * r, Rnd(0.6f, 0.95f) * r,
                c0, c1, alpha, 0.05f, 0.4f, drag: 2.4f, delay: delay + Rnd(0f, 0.05f), spin: Rnd(-60f, 60f), rise: 0.15f);
        }
    }

    // 床に落ちる光だまり: 線を描かず、柔らかい楕円の光を置く
    private static void FloorPool(Vector2 f, float size, Color c0, Color c1, float alpha, float life, float delay = 0f)
    {
        Add(Shape.Glow, f, Vector2.zero, life, size * 0.6f, size, c0, c1, alpha, 0.1f, 0.6f, delay: delay, rot: 0f, sy0: size * 0.22f, sy1: size * 0.36f, order: 1);
    }

    // 焦げ跡: 黒い楕円と、縁でくすぶる火の粉
    private static void Scorch(Vector2 f, float size, float life, float delay = 0f)
    {
        Add(Shape.Cloud, f, Vector2.zero, life, size * 0.7f, size, Charred, Charred, 0.85f, 0.05f, 0.7f, delay: delay, rot: 0f, sy0: size * 0.25f, sy1: size * 0.36f, order: 0);

        for (int i = 0; i < 10; i++)
        {
            float a = Rnd(0f, 2f * FxMath.PI);
            Add(Shape.Star, Off(f, FxMath.Cos(a) * size * 0.45f, FxMath.Sin(a) * size * 0.16f), FxMath.V2(0f, Rnd(0.1f, 0.4f)), Rnd(0.5f, 1f), Rnd(0.04f, 0.07f), 0.02f, BlazeYellow, BlazeRed, 1f, 0.1f, 0.6f,
                delay: delay + Rnd(0f, 0.6f), twinkle: 0.6f, twinkleSpeed: 14f, order: 2);
        }
    }
}
