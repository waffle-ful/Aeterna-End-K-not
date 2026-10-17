using System.Collections.Generic;
using UnityEngine;

namespace EndKnot.Modules;

// スナイパーの演出。構えると本人の手に狙撃銃が現れ、撃つ向き (構えた場所 → 今の位置) へ銃口を向け続ける。
// 撃つと反動で跳ね、銃口の閃光・弾の筋 (判定と同じく壁を抜ける)・硝煙・薬莢、当たった所に着弾の火花。
// 向きは構えた場所だけ 1 回受け取り、後は手元の位置から毎フレーム測る (ホストの当たり判定と同じ式)。
public static partial class ExplosionFx
{
    private static readonly Color SniperLaser = new(1f, 0.16f, 0.12f);
    private static readonly Color SniperGlint = new(0.75f, 0.95f, 1f);
    private static readonly Color SniperTracer = new(1f, 0.86f, 0.5f);

    // 狙撃銃の絵 (560×170 px・右向き) の単位。画像の中心から見た各部の位置 (px・上が +)
    private const float RiflePpu = 480f;
    private const float RifleMuzzleX = 268f, RifleMuzzleY = -11f;
    private const float RifleLensX = 74f, RifleLensY = 26f;
    private const float RifleGripX = -84f, RifleGripY = -35f;
    private const float RifleForeX = 92f, RifleForeY = -19f;
    private const float RiflePortX = -10f, RiflePortY = -5f;

    // 手の絵 (140 px・ppu 100) を狙撃銃の握りに合う大きさへ
    private const float RifleHandSize = 0.13f;

    // 銃の中心は体の中心から撃つ向きへこれだけ前・少し下
    private const float RifleAhead = 0.16f, RifleDrop = -0.07f;

    private const float SniperBulletSpeed = 90f;
    private const float SniperMissRange = 30f;

    // ── ホストが呼ぶ ──────────────────────────────────────────

    // 構えた: Pos = 構えた場所 (撃つ向きの起点)。Radius = 本人の PlayerId + 1 + 32 × 構えていられる秒数 (1〜60)
    public static void SniperAim(PlayerControl pc, Vector2 basePos, float seconds)
    {
        if (!pc) return;
        int sec = FxMath.Clamp((int)System.MathF.Round(seconds), 1, 60);
        Play(Kind.SniperAim, basePos, pc.PlayerId + 1 + 32 * sec);
    }

    // 撃った: Pos = 撃った人の位置。向きはホストの当たり判定と同じ (構えた場所 → 今の位置)。hit = 倒した人 (null = 外れ)
    public static void SniperFire(PlayerControl pc, Vector2 basePos, PlayerControl hit)
    {
        if (!pc) return;

        try
        {
            Vector2 from = pc.Pos();
            float dx = from.x - basePos.x, dy = from.y - basePos.y;
            float ang = dx * dx + dy * dy > 0.0001f ? FxMath.Atan2(dy, dx) * FxMath.Rad2Deg : pc.cosmetics.FlipX ? 180f : 0f;
            float len = SniperMissRange;
            if (hit)
            {
                Vector2 h = hit.Pos();
                len = FxMath.Sqrt((h.x - from.x) * (h.x - from.x) + (h.y - from.y) * (h.y - from.y));
            }

            // 撃った本人には役職の発射音が別に届くので、本人の画面では銃声を鳴らさない
            Play(Kind.SniperFire, from, PackSniperFire(pc.PlayerId, ang, len, hit, true));
        }
        catch (System.Exception e) { Utils.ThrowException(e); }
    }

    // 撃たずに構えを解いた (会議・弾切れ・死亡)
    public static void SniperDown(byte id)
    {
        Play(Kind.SniperDown, Vector2.zero, id + 1);
    }

    // ベテランの警戒: 本人の画面だけに、狙撃銃を斜めに構えて待つ姿を出す
    public static void VeteranGuard(PlayerControl pc, float seconds)
    {
        if (!pc) return;
        int sec = FxMath.Clamp((int)System.MathF.Round(seconds), 1, 60);
        PlayFor(Kind.VeteranGuard, pc.Pos(), pc.PlayerId + 1 + 32 * sec, pc);
    }

    // ベテランの返り討ち: 襲ってきた人へ向けて撃つ (視界の中の全員)。キルの直前 (撃った人が死体の位置へ動く前) に呼ぶ
    public static void VeteranCounter(PlayerControl veteran, PlayerControl killer)
    {
        if (!veteran || !killer || veteran.PlayerId == killer.PlayerId) return;

        try
        {
            Vector2 from = veteran.Pos(), to = killer.Pos();
            float dx = to.x - from.x, dy = to.y - from.y;
            float ang = dx * dx + dy * dy > 0.0001f ? FxMath.Atan2(dy, dx) * FxMath.Rad2Deg : veteran.cosmetics.FlipX ? 180f : 0f;
            Play(Kind.SniperFire, from, PackSniperFire(veteran.PlayerId, ang, FxMath.Sqrt(dx * dx + dy * dy), true, false));
        }
        catch (System.Exception e) { Utils.ThrowException(e); }
    }

    // 撃った人の PlayerId + 1 + 32 × 向き (0〜359°) + 11520 × 弾の飛ぶ距離 (0.25 刻み・0〜64) + 2960640 × 当たったか
    // + 5921280 × 撃った本人の画面では銃声を鳴らさないか
    private const int SniperHitBit = 11520 * 257;
    private const int SniperQuietBit = SniperHitBit * 2;

    private static float PackSniperFire(byte id, float angleDeg, float length, bool hit, bool quietForShooter)
    {
        int ang = (int)System.MathF.Round(angleDeg) % 360;
        if (ang < 0) ang += 360;
        int len = FxMath.Clamp((int)System.MathF.Round(length / 0.25f), 0, 256);
        return id + 1 + 32 * ang + 11520 * len + (hit ? SniperHitBit : 0) + (quietForShooter ? SniperQuietBit : 0);
    }

    // ── 受け取って描く ────────────────────────────────────────

    private static void SpawnSniper(Request r)
    {
        int v = (int)(r.Radius + 0.5f);
        int id = v % 32 - 1;
        if (id is < 0 or > 30) return;

        switch (r.Kind)
        {
            case Kind.SniperAim:
                StartSniperRig(id, r.Pos, (v / 32) % 64, false, false);
                break;
            case Kind.VeteranGuard:
                StartSniperRig(id, r.Pos, (v / 32) % 64, true, true);
                break;
            case Kind.SniperFire:
            {
                int ang = (v / 32) % 360;
                float len = (v / 11520 % 257) * 0.25f;
                bool hit = (v / SniperHitBit) % 2 == 1;
                bool quiet = v >= SniperQuietBit;
                FireSniperRig(id, r.Pos, ang, len, hit, quiet);
                break;
            }
            case Kind.SniperDown:
                for (int i = 0; i < SniperRigs.Count; i++)
                {
                    if (SniperRigs[i].Id == id) BeginRigFade(SniperRigs[i], 0.3f);
                }

                break;
        }
    }

    private sealed class SniperRig
    {
        public byte Id;
        public PlayerControl Pc;
        public bool Vision;
        public bool Guard;
        public Vector2 Base;
        public float Start, Until;
        public float Ang;
        public bool AngKnown;
        public float FiredAt = -1f;
        public float FireAng;
        public Vector2 FirePos;
        public bool Aimed;
        public int ColorId = -1;
        public float NextColor, NextGlint;
        public int GripHand, ForeHand;
        public NovaSprite Rifle, Laser, LaserCore;
        public float DyingFrom = -1f, DyingFade;
    }

    private static readonly List<SniperRig> SniperRigs = [];

    private static bool SniperBusy => SniperRigs.Count > 0;

    private static Sprite RifleSprite() => Utils.LoadSprite("EndKnot.Resources.Images.Fx.SniperRifle.png", RiflePpu);
    private static Sprite RifleGripSprite() => Utils.LoadSprite("EndKnot.Resources.Images.Fx.SniperHandGrip.png", 100f);
    private static Sprite RifleForeSprite() => Utils.LoadSprite("EndKnot.Resources.Images.Fx.SniperHandFore.png", 100f);

    private static void StopAllSnipers()
    {
        foreach (SniperRig g in SniperRigs) ReleaseRig(g);
        SniperRigs.Clear();
    }

    private static void ReleaseRig(SniperRig g)
    {
        FxHands.HoldStop(g.GripHand, 0f);
        FxHands.HoldStop(g.ForeHand, 0f);
        DestroyNova(g.Rifle);
        DestroyNova(g.Laser);
        DestroyNova(g.LaserCore);
    }

    private static void BeginRigFade(SniperRig g, float seconds)
    {
        if (g.DyingFrom >= 0f) return;
        g.DyingFrom = Time.time;
        g.DyingFade = seconds;
    }

    // 変身後の姿の色で握る (本当の色で握ると変身が見破られる)
    private static int CurrentColorId(PlayerControl pc)
    {
        int c = pc.CurrentOutfit?.ColorId ?? -1;
        return c >= 0 && c < Palette.PlayerColors.Length ? c : 0;
    }

    private static SniperRig StartSniperRig(int id, Vector2 basePos, int seconds, bool guard, bool silent)
    {
        PlayerControl pc = Utils.GetPlayerById((byte)id);

        // 同じ人の前の構えが残っていたら畳む (構えの終わりの合図は落ちることがある)
        for (int i = SniperRigs.Count - 1; i >= 0; i--)
        {
            if (SniperRigs[i].Id != id) continue;
            ReleaseRig(SniperRigs[i]);
            SniperRigs.RemoveAt(i);
        }

        if (!pc || SniperRigs.Count >= 8) return null;

        float now = Time.time;
        int color = CurrentColorId(pc);
        bool vision = _vision;
        var g = new SniperRig
        {
            Id = (byte)id, Pc = pc, Vision = vision, Guard = guard, Base = basePos, Start = now, Until = now + FxMath.Max(seconds, 1) + (guard ? 0.2f : 1.5f),
            ColorId = color, NextColor = now + 0.5f, NextGlint = now + 0.9f,
            Rifle = NewNovaSprite(RifleSprite(), 9),
            Laser = NewNovaSprite(_ray, 7, aspect: RayAspect),
            LaserCore = NewNovaSprite(_ray, 8, aspect: RayAspect),
            GripHand = FxHands.HoldStart(RifleGripSprite(), color, vision, SortingOrder + 11, -1f - 11 * 0.01f),
            ForeHand = FxHands.HoldStart(RifleForeSprite(), color, vision, SortingOrder + 11, -1f - 11 * 0.01f)
        };
        SniperRigs.Add(g);

        // 警戒の時は役職の装填音が本人に届く。撃つ合図から作り直した時は銃声がすぐ続くので鳴らさない
        if (!silent && !Unseen(pc, (byte)id)) FxSound.At("FxSniperDraw", pc.Pos(), 0.8f);

        return g;
    }

    private static void FireSniperRig(int id, Vector2 origin, int angleDeg, float length, bool hit, bool quietForShooter)
    {
        SniperRig g = null;
        for (int i = 0; i < SniperRigs.Count; i++)
        {
            if (SniperRigs[i].Id == id && SniperRigs[i].DyingFrom < 0f) g = SniperRigs[i];
        }

        // 構えの合図が落ちていても、撃った瞬間の銃は出す
        if (g == null)
        {
            g = StartSniperRig(id, origin, 2, false, true);
            if (g == null) return;
            g.Start = Time.time - 0.35f;
        }

        float now = Time.time;
        g.FiredAt = now;
        g.FireAng = angleDeg;
        g.FirePos = origin;
        g.Ang = angleDeg;
        g.AngKnown = true;
        g.Until = now + 1.1f;

        PlayerControl pc = g.Pc;
        if (!pc || Unseen(pc, g.Id)) return;

        // 銃口は撃った時の位置から (ベテランは返り討ちのキルで死体の位置へ移った後にこれが届く)
        Vector2 body = origin;
        float rad = angleDeg * FxMath.Deg2Rad;
        float ux = FxMath.Cos(rad), uy = FxMath.Sin(rad);
        Vector2 center = Off(body, ux * RifleAhead, uy * RifleAhead + RifleDrop);
        Vector2 muzzle = RiflePoint(center, ux, uy, RifleMuzzleX, RifleMuzzleY);
        Vector2 port = RiflePoint(center, ux, uy, RiflePortX, RiflePortY);

        _vision = g.Vision;
        bool keepNear = _nearFlashOnly;
        _nearFlashOnly = g.Vision;

        // 銃口の閃光: 前へ長く伸びる炎の舌 + 横へ割れる十字 + 白い芯 + 周りを照らす光
        Add(Shape.Glow, muzzle, Vector2.zero, 0.2f, 0.7f, 1.8f, GunFlash, GunFire, 0.85f, 0.01f, 0.2f, order: 10);
        Add(Shape.Flame, Off(muzzle, ux * 0.3f, uy * 0.3f), Vector2.zero, 0.1f, 0.7f, 0.9f, GunFlash, GunFire, 1f, 0.01f, 0.3f, rot: angleDeg - 90f, sy0: 0.2f, sy1: 0.26f, order: 11);
        Add(Shape.Flame, Off(muzzle, -uy * 0.12f, ux * 0.12f), Vector2.zero, 0.07f, 0.28f, 0.34f, GunFlash, GunFire, 0.9f, 0.01f, 0.3f, rot: angleDeg, sy0: 0.12f, sy1: 0.14f, order: 11);
        Add(Shape.Flame, Off(muzzle, uy * 0.12f, -ux * 0.12f), Vector2.zero, 0.07f, 0.28f, 0.34f, GunFlash, GunFire, 0.9f, 0.01f, 0.3f, rot: angleDeg + 180f, sy0: 0.12f, sy1: 0.14f, order: 11);
        Add(Shape.Star, muzzle, Vector2.zero, 0.1f, 0.55f, 0.2f, WindWhite, GunFlash, 1f, 0.01f, 0.3f, rot: angleDeg, order: 12);
        Particles(FxParticles.Preset.Sparks, muzzle, 18, GunFlash, GunFire, size: 0.7f, speed: 1.3f, delay: 0f, order: 11, angle: angleDeg);

        // 銃口から前へ吹き出す衝撃の輪 (細長い楕円が撃つ向きへ広がる)
        Add(Shape.Ring, Off(muzzle, ux * 0.25f, uy * 0.25f), FxMath.V2(ux * 1.2f, uy * 1.2f), 0.25f, 0.1f, 0.55f, WindWhite, GunFlash, 0.55f, 0.01f, 0.2f,
            rot: angleDeg, sy0: 0.25f, sy1: 0.7f, order: 10);

        // 弾の筋: 弾の速さで伸び、少し残って消える。判定と同じく壁を抜ける (視界の外は影が隠す)
        float travel = length / SniperBulletSpeed;
        Vector2 end = Off(muzzle, ux * length, uy * length);
        Line2(muzzle, end, 0.035f, travel + 0.45f, GunFire, SniperTracer, 0.9f, 0.01f, travel + 0.05f, taper: FxLines.Taper.Even, grow: travel, order: 9);
        Line2(muzzle, Off(muzzle, ux * FxMath.Min(length, 6f), uy * FxMath.Min(length, 6f)), 0.09f, 0.9f, GunSmoke, GunSmoke, 0.35f, 0.02f, 0.3f,
            delay: 0.02f, taper: FxLines.Taper.Trunk, grow: FxMath.Min(travel, 0.07f), order: 7);

        // 硝煙: 銃口から前へ吹き出して漂う
        Particles(FxParticles.Preset.Smoke, Off(muzzle, ux * 0.2f, uy * 0.2f), 7, GunSmoke, GunSmoke, size: 0.45f, speed: 0.6f, spread: 0.4f, delay: 0.03f, order: 8, angle: angleDeg);

        // 薬莢: 機関部の横から上へ弾き出され、回りながら床へ落ちる (ボルトを引く 0.35 秒後)
        float sx = ux < 0f ? -1f : 1f;
        Add(Shape.Solid, port, FxMath.V2(-ux * 0.8f - uy * sx * 0.4f, 3.2f), 0.5f, 0.06f, 0.06f, Brass, Brass, 1f, 0.01f, 0.85f,
            drag: 2.5f, rise: -2.4f, delay: 0.35f, spin: 1100f, sy0: 0.026f, sy1: 0.026f, order: 10);

        // 着弾: 当たった所に火花と赤い飛沫、外れた弾は遠くで小さく光る
        if (hit)
        {
            Add(Shape.Glow, end, Vector2.zero, 0.18f, 0.3f, 0.9f, GunFlash, GunFire, 0.8f, 0.01f, 0.2f, delay: travel, order: 10);
            Add(Shape.Star, end, Vector2.zero, 0.12f, 0.4f, 0.15f, WindWhite, GunFlash, 1f, 0.01f, 0.3f, delay: travel, rot: angleDeg + 45f, order: 12);
            Particles(FxParticles.Preset.Sparks, end, 16, GunFlash, GunFire, size: 0.6f, delay: travel, order: 11, angle: angleDeg);
            Particles(FxParticles.Preset.Sparks, end, 12, SniperBlood, SniperBloodDeep, size: 0.55f, speed: 0.8f, delay: travel, order: 10, angle: angleDeg);
        }

        Impact(muzzle, 2.2f, GunFlash, 0.12f, 0.05f, 0.18f);
        _nearFlashOnly = keepNear;
        _vision = false;

        if (!(quietForShooter && pc.AmOwner)) FxSound.At("FxSniperShot", muzzle, 1f, farGain: 0.3f);
        else FxSound.At("FxSniperBolt", muzzle, 0.7f);
    }

    private static readonly Color SniperBlood = new(0.85f, 0.08f, 0.08f);
    private static readonly Color SniperBloodDeep = new(0.4f, 0.02f, 0.04f);

    // 狙撃銃の絵の中の点 (px・右向き基準) を、撃つ向き (ux, uy) に回した世界の位置へ。左向きの時は絵を上下に返す
    private static Vector2 RiflePoint(Vector2 center, float ux, float uy, float px, float py)
    {
        float lx = px / RiflePpu, ly = (ux < 0f ? -py : py) / RiflePpu;
        return Off(center, lx * ux - ly * uy, lx * uy + ly * ux);
    }

    private static void PulseSnipers()
    {
        float now = Time.time;
        float dt = Time.deltaTime;
        MeshRenderer shadowQuad = HudManager.InstanceExists ? HudManager.Instance.ShadowQuad : null;

        for (int i = SniperRigs.Count - 1; i >= 0; i--)
        {
            bool done;
            try { done = TickRig(SniperRigs[i], now, dt, shadowQuad); }
            catch (System.Exception e)
            {
                Logger.Warn($"SniperRig: {e.Message}", "ExplosionFx");
                done = true;
            }

            if (!done) continue;
            ReleaseRig(SniperRigs[i]);
            SniperRigs.RemoveAt(i);
        }
    }

    private static bool TickRig(SniperRig g, float now, float dt, MeshRenderer shadowQuad)
    {
        PlayerControl pc = g.Pc;
        if (!pc) return true;
        if (g.DyingFrom < 0f && (!pc.IsAlive() || now >= g.Until)) BeginRigFade(g, g.FiredAt >= 0f ? 0.35f : 0.25f);

        float fade = 1f;
        if (g.DyingFrom >= 0f)
        {
            fade = 1f - (now - g.DyingFrom) / g.DyingFade;
            if (fade <= 0f) return true;
        }

        MeshRenderer shadow = g.Vision ? shadowQuad : null;
        bool hide = Unseen(pc, g.Id);
        if (hide)
        {
            HideNova(g.Rifle);
            HideNova(g.Laser);
            HideNova(g.LaserCore);
            FxHands.HoldPlace(g.GripHand, 0f, 0f, 0f, 0f, true);
            FxHands.HoldPlace(g.ForeHand, 0f, 0f, 0f, 0f, true);
            return false;
        }

        // 変身すると姿の色が変わるので、握る手の色を時々合わせ直す
        if (now >= g.NextColor)
        {
            g.NextColor = now + 0.5f;
            int col = CurrentColorId(pc);
            if (col != g.ColorId)
            {
                g.ColorId = col;
                FxHands.HoldColor(g.GripHand, col);
                FxHands.HoldColor(g.ForeHand, col);
            }
        }

        // 警戒からの返り討ちは撃った場所に残す (本人はキルで死体の位置へ移る)。スナイパーは撃った後も歩けるので付いていく
        Vector2 body = g.Guard && g.FiredAt >= 0f ? g.FirePos : pc.Pos();
        float t = now - g.Start;

        // 撃つ向き: ホストの当たり判定と同じく、構えた場所から今の位置へ。まだ動いていなければ顔の向き
        float target;
        if (g.FiredAt >= 0f) target = g.FireAng;
        // 警戒: 銃口を斜め上に向けて体の前に抱える (顔の向きに合わせる)
        else if (g.Guard) target = pc.cosmetics.FlipX ? 145f : 35f;
        else
        {
            float dx = body.x - g.Base.x, dy = body.y - g.Base.y;
            // 構えた場所から動いていない間は、ホストの判定も座標の微小な揺れで向きが決まる (狙いが定まっていない)
            g.Aimed = dx * dx + dy * dy > 0.0025f;
            target = g.Aimed ? FxMath.Atan2(dy, dx) * FxMath.Rad2Deg : pc.cosmetics.FlipX ? 180f : 0f;
        }

        if (!g.AngKnown)
        {
            g.Ang = target;
            g.AngKnown = true;
        }
        else
        {
            float diff = FxMath.Repeat(target - g.Ang + 180f, 360f) - 180f;
            // 追従は速めに (構えた場所の近くでは少し動くだけで向きが大きく変わり、遅いと実際の向きから目に見えて遅れる)
            g.Ang += diff * (1f - FxMath.Exp(-dt * 40f));
        }

        float rad = g.Ang * FxMath.Deg2Rad;
        float ux = FxMath.Cos(rad), uy = FxMath.Sin(rad);
        bool left = ux < 0f;

        // 現れる: 0.35 秒で銃口を下げた所から持ち上げて構える
        float rise = FxMath.Clamp01(t / 0.35f);
        float ease = 1f - (1f - rise) * (1f - rise) * (1f - rise);
        float lower = (1f - ease) * -55f * (left ? -1f : 1f);

        // 反動: 撃った瞬間に後ろへ下がり銃口が跳ね上がって、戻りながらボルトを引く
        float back = 0f, kick = 0f;
        if (g.FiredAt >= 0f)
        {
            float f = now - g.FiredAt;
            float k = FxMath.Exp(-f * 9f);
            back = 0.13f * k;
            kick = 16f * k * (left ? -1f : 1f);
            if (f is > 0.3f and < 0.6f) back += 0.025f * FxMath.Sin((f - 0.3f) / 0.3f * FxMath.PI);
        }

        float drawAng = g.Ang + lower + kick;
        float dr = drawAng * FxMath.Deg2Rad;
        float dux = FxMath.Cos(dr), duy = FxMath.Sin(dr);
        Vector2 center = Off(body, ux * (RifleAhead - back), uy * (RifleAhead - back) + RifleDrop);
        float alpha = fade * ease;
        float scale = 0.85f + 0.15f * ease;

        PlaceNova(g.Rifle, shadow, center, scale, left ? -scale : scale, drawAng, WindWhite, alpha);

        Vector2 grip = RiflePoint(center, dux, duy, RifleGripX * scale, RifleGripY * scale);
        Vector2 fore = RiflePoint(center, dux, duy, RifleForeX * scale, RifleForeY * scale);
        bool handsHidden = alpha < 0.05f;
        FxHands.HoldPlace(g.GripHand, grip.x, grip.y, drawAng, RifleHandSize * scale, handsHidden);
        FxHands.HoldPlace(g.ForeHand, fore.x, fore.y, drawAng, RifleHandSize * scale, handsHidden);
        FxHands.HoldAlpha(g.GripHand, alpha);
        FxHands.HoldAlpha(g.ForeHand, alpha);

        // 照準の赤い光: 構えている間だけ銃口から撃つ向きへ。撃ったら消す
        Vector2 muzzle = RiflePoint(center, dux, duy, RifleMuzzleX * scale, RifleMuzzleY * scale);
        float laser = g.FiredAt >= 0f || g.Guard || !g.Aimed ? 0f : alpha * FxMath.Clamp01((t - 0.3f) / 0.25f) * (0.8f + 0.2f * FxMath.Sin(t * 6f));
        PlaceNova(g.Laser, shadow, muzzle, 5.5f, 0.09f, drawAng, SniperLaser, 0.45f * laser);
        PlaceNova(g.LaserCore, shadow, muzzle, 5f, 0.025f, drawAng, WindWhite, 0.6f * laser);

        // スコープのレンズがときどき光る (遠くの人には「何かが光った」で分かる)
        if (g.FiredAt < 0f && !g.Guard && g.DyingFrom < 0f && now >= g.NextGlint && t > 0.4f)
        {
            g.NextGlint = now + 1.4f + (g.Id % 4) * 0.15f;
            Vector2 lens = RiflePoint(center, dux, duy, RifleLensX * scale, RifleLensY * scale);
            _vision = g.Vision;
            Add(Shape.Star, lens, Vector2.zero, 0.35f, 0.05f, 0.32f, WindWhite, SniperGlint, 1f, 0.15f, 0.5f, rot: 0f, order: 12);
            Add(Shape.Glow, lens, Vector2.zero, 0.35f, 0.08f, 0.22f, WindWhite, SniperGlint, 0.7f, 0.15f, 0.5f, order: 11);
            _vision = false;
        }

        return false;
    }
}
