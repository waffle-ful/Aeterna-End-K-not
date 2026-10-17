using System.Collections.Generic;
using UnityEngine;

namespace EndKnot.Modules;

// アーチャー・ハングマン・ワスプ・トレマーの演出。
// アーチャー: 据えた所に弓が浮かび、離れるほど弦が引き絞られ、放つと据えた所から矢が飛ぶ (壁に刺さって震える / 人に当たる)。
// ハングマン: 縄が降りて首に掛かり、吊り上げられて消える (本人は死体を残さず消えるので、その姿を描き直して吊る)。
// ワスプ: 刺す瞬間は刺した本人の画面だけ。毒で倒れる時と群れの間は視界の中の全員に蜂。
// トレマー: 破滅の間、倒される範囲 (半径 2.5) の地面が割れて揺れ、巻き込まれた人の足元から岩が突き上がる。
public static partial class ExplosionFx
{
    // ── アーチャー ────────────────────────────────────────────

    private const float BowPpu = 290f, ArrowPpu = 375f, WaspPpu = 220f, NoosePpu = 300f, RopePpu = 340f;
    private const float ArrowHalf = 0.39f;
    private const float BowTipX = -0.055f, BowTipY = 0.503f;
    private const float ArrowLift = 0.3f;

    private static readonly Color BowString = new(0.93f, 0.9f, 0.82f);
    private static readonly Color ArcherRed = new(1f, 0.35f, 0.25f);
    private static readonly Color ArcherGold = new(1f, 0.8f, 0.35f);

    private static Sprite BowSprite() => Utils.LoadSprite("EndKnot.Resources.Images.Fx.ArcherBow.png", BowPpu);
    private static Sprite ArrowSprite() => Utils.LoadSprite("EndKnot.Resources.Images.Fx.ArcherArrow.png", ArrowPpu);

    // 弓を据えた: Pos = 据えた場所 (本人の位置)、Radius = 本人の PlayerId + 1 + 32 × 放つまでの上限秒数 + 1024 × 本人が矢に乗って飛ぶか
    public static void ArcherSet(PlayerControl pc, Vector2 basePos, float seconds, bool rider)
    {
        if (!pc) return;
        Play(Kind.ArcherSet, basePos, pc.PlayerId + 1 + 32 * FxMath.Clamp((int)System.MathF.Round(seconds), 1, 30) + (rider ? 1024 : 0));
    }

    // 矢を放った: Pos = 矢の出る所、dir = 飛ぶ向き、speed = 秒速 (2 刻み)、life = 飛んでいられる秒数
    public static void ArcherShot(byte id, Vector2 origin, Vector2 dir, float speed, float life)
    {
        float ang = FxMath.Atan2(dir.y, dir.x) * FxMath.Rad2Deg;
        int a = (int)System.MathF.Round(ang) % 360;
        if (a < 0) a += 360;
        int spd = FxMath.Clamp((int)System.MathF.Round(speed / 2f), 1, 63);
        int lf = FxMath.Clamp((int)System.MathF.Round(life / 0.5f), 1, 20);
        Play(Kind.ArcherShot, origin, id + 1 + 32 * a + 11520 * spd + 737280 * lf);
    }

    // 矢が止まった: Pos = 止まった所。killed = 人に当たって倒した / wall = 壁に当たった / cancel = 放てずに弓を畳んだ
    public static void ArcherStop(byte id, Vector2 at, bool killed, bool wall, bool cancel = false)
    {
        Play(Kind.ArcherStop, at, id + 1 + (killed ? 32 : 0) + (wall ? 64 : 0) + (cancel ? 128 : 0));
    }

    private sealed class BowRig
    {
        public byte Id;
        public PlayerControl Pc;
        public bool Vision;
        public Vector2 Base;
        public float Start, Until;
        public float FiredAt = -1f;
        public Vector2 FireDir;
        public float Pull;
        public bool Rider;
        public NovaSprite Bow, StrA, StrB, Nocked, Tether, TipGlow;
        public float DyingFrom = -1f, DyingFade;
    }

    private sealed class ArrowFlight
    {
        public byte Id;
        public bool Vision;
        public Vector2 Pos, Dir;
        public float Ang, Speed, Left, Age;
        public bool Stuck, Falling;
        public float StuckAt;
        // 本人が矢に乗って飛ぶ設定: 矢は本人の足元に付いていく (本人はホストが矢の位置へ少しずつ移す)
        public PlayerControl Rider;
        public NovaSprite Arrow, Trail, Glow;
        public float NextSpark;
    }

    private static readonly List<BowRig> BowRigs = [];
    private static readonly List<ArrowFlight> Arrows = [];

    private static void StopAllRoles7()
    {
        foreach (BowRig b in BowRigs) ReleaseBow(b);
        BowRigs.Clear();
        foreach (ArrowFlight a in Arrows) ReleaseArrow(a);
        Arrows.Clear();
        foreach (Noose n in Nooses) ReleaseNoose(n);
        Nooses.Clear();
        foreach (Swarm s in Swarms) ReleaseSwarm(s);
        Swarms.Clear();
        foreach (Doom d in Dooms) ReleaseDoom(d);
        Dooms.Clear();
    }

    private static bool Roles7Busy => BowRigs.Count > 0 || Arrows.Count > 0 || Nooses.Count > 0 || Swarms.Count > 0 || Dooms.Count > 0;

    private static void ReleaseBow(BowRig b)
    {
        DestroyNova(b.Bow);
        DestroyNova(b.StrA);
        DestroyNova(b.StrB);
        DestroyNova(b.Nocked);
        DestroyNova(b.Tether);
        DestroyNova(b.TipGlow);
    }

    private static void ReleaseArrow(ArrowFlight a)
    {
        DestroyNova(a.Arrow);
        DestroyNova(a.Trail);
        DestroyNova(a.Glow);
    }

    private static void SpawnRoles7(Request r)
    {
        int v = (int)(r.Radius + 0.5f);
        int id = v % 32 - 1;
        if (id is < 0 or > 30) return;

        switch (r.Kind)
        {
            case Kind.ArcherSet:
                StartBow(id, r.Pos, (v / 32) % 32, (v & 1024) != 0);
                break;
            case Kind.ArcherShot:
            {
                int ang = (v / 32) % 360;
                float speed = (v / 11520) % 64 * 2f;
                float life = (v / 737280) * 0.5f;
                LaunchArrow(id, r.Pos, ang, speed, life);
                break;
            }
            case Kind.ArcherStop:
                StopArrow(id, r.Pos, (v & 32) != 0, (v & 64) != 0, (v & 128) != 0);
                break;
            case Kind.HangNoose:
                StartNoose(r.Pos, id);
                break;
            case Kind.WaspSting:
                SpawnWaspSting(r.Pos, id, (v / 32) % 32);
                break;
            case Kind.WaspVenom:
                if (HiddenNow(id)) break;
                SpawnWaspVenom(r.Pos);
                FxSound.At("FxWaspVenom", r.Pos);
                break;
            case Kind.WaspSwarm:
                StartSwarm(id, (v / 32) % 64);
                break;
            case Kind.TremorDoom:
                StartDoom(id, r.Pos, (v / 32) % 256);
                break;
            case Kind.TremorCrush:
                if (HiddenNow(id) || CrushRepeated(r.Pos)) break;
                SpawnTremorCrush(r.Pos);
                FxSound.At("FxTremorCrush", r.Pos);
                break;
        }
    }

    private static void StartBow(int id, Vector2 basePos, int seconds, bool rider)
    {
        PlayerControl pc = Utils.GetPlayerById((byte)id);
        for (int i = BowRigs.Count - 1; i >= 0; i--)
        {
            if (BowRigs[i].Id != id) continue;
            ReleaseBow(BowRigs[i]);
            BowRigs.RemoveAt(i);
        }

        if (!pc || BowRigs.Count >= 8) return;

        float now = Time.time;
        var b = new BowRig
        {
            Id = (byte)id, Pc = pc, Vision = _vision, Base = basePos, Rider = rider, Start = now, Until = now + FxMath.Max(seconds, 1) + 1f,
            Tether = NewNovaSprite(_solid, 7),
            StrA = NewNovaSprite(_solid, 8),
            StrB = NewNovaSprite(_solid, 8),
            Bow = NewNovaSprite(BowSprite(), 9),
            Nocked = NewNovaSprite(ArrowSprite(), 10),
            TipGlow = NewNovaSprite(_glow, 11)
        };
        BowRigs.Add(b);

        if (Unseen(pc, (byte)id)) return;
        Vector2 o = Off(basePos, 0f, ArrowLift);
        Add(Shape.Glow, o, Vector2.zero, 0.4f, 0.3f, 1.2f, ArcherGold, ArcherRed, 0.6f, 0.05f, 0.3f, order: 8);
        Particles(FxParticles.Preset.Sparks, o, 12, ArcherGold, ArcherRed, size: 0.5f, speed: 0.7f, order: 9);
        FxSound.At("FxArcherDraw", o, 0.8f);
    }

    private static void LaunchArrow(int id, Vector2 origin, int angleDeg, float speed, float life)
    {
        float rad = angleDeg * FxMath.Deg2Rad;
        var dir = FxMath.V2(FxMath.Cos(rad), FxMath.Sin(rad));

        BowRig rig = null;
        foreach (BowRig b in BowRigs)
        {
            if (b.Id == id && b.FiredAt < 0f) rig = b;
        }

        if (rig != null)
        {
            rig.FiredAt = Time.time;
            rig.FireDir = dir;
            rig.Until = Time.time + 0.9f;
        }

        // 弦が戻る閃き・放った衝撃
        Add(Shape.Ring, Off(origin, dir.x * 0.2f, dir.y * 0.2f), FxMath.V2(dir.x * 0.8f, dir.y * 0.8f), 0.25f, 0.1f, 0.6f, WindWhite, ArcherGold, 0.5f, 0.01f, 0.2f,
            rot: angleDeg, sy0: 0.3f, sy1: 0.8f, order: 9);
        Particles(FxParticles.Preset.Sparks, origin, 10, ArcherGold, ArcherRed, size: 0.5f, speed: 1.1f, order: 10, angle: angleDeg);
        FxSound.At("FxArcherRelease", origin, 1f, farGain: 0.2f);

        if (Arrows.Count >= 10) return;
        Arrows.Add(new ArrowFlight
        {
            Id = (byte)id, Vision = _vision, Pos = origin, Rider = rig is { Rider: true } ? rig.Pc : null, Dir = dir, Ang = angleDeg, Speed = FxMath.Max(speed, 1f), Left = FxMath.Max(life, 0.5f),
            Trail = NewNovaSprite(_ray, 8, aspect: RayAspect),
            Glow = NewNovaSprite(_glow, 9),
            Arrow = NewNovaSprite(ArrowSprite(), 10)
        });
    }

    private static void StopArrow(int id, Vector2 at, bool killed, bool wall, bool cancel)
    {
        foreach (BowRig b in BowRigs)
        {
            if (b.Id == id && b.DyingFrom < 0f && (cancel || b.FiredAt >= 0f)) BeginBowFade(b, 0.3f);
        }

        if (cancel) return;

        ArrowFlight a = null;
        foreach (ArrowFlight f in Arrows)
        {
            if (f.Id == id && !f.Falling && (!f.Stuck || killed)) a = f;
        }

        if (a == null) return;

        _vision = a.Vision;
        if (killed)
        {
            a.Pos = at;
            a.Stuck = true;
            a.StuckAt = Time.time;
            Add(Shape.Glow, at, Vector2.zero, 0.25f, 0.3f, 1.1f, WindWhite, ArcherRed, 0.8f, 0.01f, 0.2f, order: 10);
            Add(Shape.Star, at, Vector2.zero, 0.15f, 0.45f, 0.15f, WindWhite, ArcherGold, 1f, 0.01f, 0.3f, rot: a.Ang + 45f, order: 12);
            Particles(FxParticles.Preset.Sparks, at, 14, SniperBlood, SniperBloodDeep, size: 0.6f, speed: 0.9f, order: 10, angle: a.Ang);
            FxSound.At("FxArcherHit", at);
        }
        else if (!a.Stuck)
        {
            if (wall) StickArrow(a, at);
            else a.Falling = true;
        }

        _vision = false;
    }

    private static void StickArrow(ArrowFlight a, Vector2 at)
    {
        a.Pos = at;
        a.Stuck = true;
        a.StuckAt = Time.time;
        Particles(FxParticles.Preset.Smoke, at, 4, GunSmoke, GunSmoke, size: 0.3f, speed: 0.4f, order: 8);
        Particles(FxParticles.Preset.Sparks, at, 8, ArcherGold, WindWhite, size: 0.4f, speed: 0.7f, order: 10, angle: a.Ang + 180f);
        SoundSeen("FxArcherThunk", at, 0.9f, a.Vision);
    }

    private static void BeginBowFade(BowRig b, float seconds)
    {
        if (b.DyingFrom >= 0f) return;
        b.DyingFrom = Time.time;
        b.DyingFade = seconds;
    }

    // 毎フレームの経路から鳴らす音と揺れ。受け取った時の見え方の設定は戻っているので、演出ごとに掛け直す
    private static void SoundSeen(string name, Vector2 pos, float volume, bool vision, float farGain = 0f)
    {
        bool keep = FxSound.NeedSight;
        FxSound.NeedSight = vision;
        try { FxSound.At(name, pos, volume, farGain: farGain); }
        finally { FxSound.NeedSight = keep; }
    }

    private static void ImpactSeen(Vector2 c, float r, Color flash, float flashAlpha, float shake, float shakeDuration, bool vision)
    {
        bool keep = _nearFlashOnly;
        _nearFlashOnly = vision;
        try { Impact(c, r, flash, flashAlpha, shake, shakeDuration); }
        finally { _nearFlashOnly = keep; }
    }

    // 透明・ベントの中の人に出す演出は居場所を明かすので出さない (追放で姿を消した人は対象外)
    private static bool HiddenNow(int id)
    {
        PlayerControl pc = id is >= 0 and <= 30 ? Utils.GetPlayerById((byte)id) : null;
        return pc && (pc.inVent || Main.Invisible.Contains((byte)id) || pc.invisibilityAlpha < 0.9f);
    }

    // 2 点を結ぶ細い棒 (_solid は 1 単位四方)
    private static void PlaceBar(NovaSprite s, MeshRenderer shadow, Vector2 a, Vector2 b, float width, Color col, float alpha)
    {
        float dx = b.x - a.x, dy = b.y - a.y;
        float len = FxMath.Sqrt(dx * dx + dy * dy);
        PlaceNova(s, shadow, FxMath.V2((a.x + b.x) * 0.5f, (a.y + b.y) * 0.5f), FxMath.Max(len, 0.001f), width, FxMath.Atan2(dy, dx) * FxMath.Rad2Deg, col, alpha);
    }

    private static Vector2 Rot(Vector2 o, Vector2 f, float lx, float ly) => Off(o, lx * f.x - ly * f.y, lx * f.y + ly * f.x);

    private static bool TickBow(BowRig b, float now, MeshRenderer shadowQuad)
    {
        PlayerControl pc = b.Pc;
        if (!pc) return true;
        if (b.DyingFrom < 0f && (now >= b.Until || (!pc.IsAlive() && b.FiredAt < 0f))) BeginBowFade(b, 0.3f);

        float fade = 1f;
        if (b.DyingFrom >= 0f)
        {
            fade = 1f - (now - b.DyingFrom) / b.DyingFade;
            if (fade <= 0f) return true;
        }

        MeshRenderer shadow = b.Vision ? shadowQuad : null;
        if (Unseen(pc, b.Id))
        {
            HideNova(b.Bow);
            HideNova(b.StrA);
            HideNova(b.StrB);
            HideNova(b.Nocked);
            HideNova(b.Tether);
            HideNova(b.TipGlow);
            return false;
        }

        float t = now - b.Start;
        float appear = FxMath.Clamp01(t / 0.25f);
        float alpha = fade * appear;
        Vector2 o = Off(b.Base, 0f, ArrowLift);

        // 放つ向き = 離れた向きの逆 (ホストの判定と同じ)。まだ離れていなければ顔の向き
        Vector2 p = pc.Pos();
        Vector2 f;
        float pull;
        if (b.FiredAt >= 0f)
        {
            f = b.FireDir;
            pull = 0f;
        }
        else
        {
            float dx = p.x - b.Base.x, dy = p.y - b.Base.y;
            float d = FxMath.Sqrt(dx * dx + dy * dy);
            if (d > 0.05f) f = FxMath.V2(-dx / d, -dy / d);
            else f = FxMath.V2(pc.cosmetics.FlipX ? -1f : 1f, 0f);
            pull = FxMath.Clamp01(d / 1.8f);
        }

        b.Pull += (pull - b.Pull) * FxMath.Clamp01(Time.deltaTime * 12f);
        float ang = FxMath.Atan2(f.y, f.x) * FxMath.Rad2Deg;
        float bend = 1f + 0.35f * b.Pull;
        float squeeze = 1f - 0.06f * b.Pull;
        PlaceNova(b.Bow, shadow, o, bend * (0.8f + 0.2f * appear), squeeze * (0.8f + 0.2f * appear), ang, WindWhite, alpha);

        // 弦: 両端から矢筈へ。放った後は前へ戻り、震えながら静まる
        float nockBack = 0.04f + 0.42f * b.Pull;
        if (b.FiredAt >= 0f)
        {
            float ft = now - b.FiredAt;
            nockBack = 0.04f + 0.08f * FxMath.Sin(ft * 70f) * FxMath.Exp(-ft * 9f);
        }

        Vector2 tipA = Rot(o, f, BowTipX * bend, BowTipY * squeeze);
        Vector2 tipB = Rot(o, f, BowTipX * bend, -BowTipY * squeeze);
        Vector2 nock = Off(o, -f.x * nockBack, -f.y * nockBack);
        PlaceBar(b.StrA, shadow, tipA, nock, 0.016f, BowString, alpha);
        PlaceBar(b.StrB, shadow, tipB, nock, 0.016f, BowString, alpha);

        if (b.FiredAt < 0f)
        {
            PlaceNova(b.Nocked, shadow, Off(nock, f.x * ArrowHalf, f.y * ArrowHalf), 1f, 1f, ang, WindWhite, alpha);
            Vector2 tip = Off(nock, f.x * ArrowHalf * 2f, f.y * ArrowHalf * 2f);
            PlaceNova(b.TipGlow, shadow, tip, 0.25f + 0.35f * b.Pull, 0.25f + 0.35f * b.Pull, 0f, ArcherRed, alpha * 0.75f * b.Pull * (0.8f + 0.2f * FxMath.Sin(t * 14f)));

            // 本人の手と弦を結ぶ細い光 (引いている手応え)
            Vector2 hand = Off(p, 0f, -0.05f);
            float dh = (hand.x - nock.x) * (hand.x - nock.x) + (hand.y - nock.y) * (hand.y - nock.y);
            PlaceBar(b.Tether, shadow, nock, hand, 0.012f, ArcherRed, Unseen(pc, b.Id) || dh < 0.09f ? 0f : 0.35f * alpha);
        }
        else
        {
            HideNova(b.Nocked);
            HideNova(b.TipGlow);
            HideNova(b.Tether);
        }

        return false;
    }

    private static bool TickArrow(ArrowFlight a, float now, float dt, MeshRenderer shadowQuad)
    {
        a.Age += dt;
        MeshRenderer shadow = a.Vision ? shadowQuad : null;

        if (a.Stuck)
        {
            float st = now - a.StuckAt;
            if (st > 1.6f) return true;
            float quiver = 7f * FxMath.Sin(st * 45f) * FxMath.Exp(-st * 5f);
            float al = FxMath.Clamp01((1.6f - st) / 0.4f);
            // 刺さった矢は鏃が壁に埋まった分だけ手前に見える
            Vector2 c = Off(a.Pos, -a.Dir.x * (ArrowHalf - 0.12f), -a.Dir.y * (ArrowHalf - 0.12f));
            PlaceNova(a.Arrow, shadow, c, 1f, 1f, a.Ang + quiver, WindWhite, al);
            HideNova(a.Trail);
            HideNova(a.Glow);
            return false;
        }

        if (a.Falling)
        {
            a.Left = FxMath.Min(a.Left, 0f) - dt;
            float ft = -a.Left;
            if (ft > 0.5f) return true;
            a.Pos = Off(a.Pos, a.Dir.x * a.Speed * 0.3f * dt, a.Dir.y * a.Speed * 0.3f * dt - 2.5f * ft * dt);
            PlaceNova(a.Arrow, shadow, a.Pos, 1f, 1f, a.Ang - 40f * ft * (a.Dir.x < 0f ? -1f : 1f), WindWhite, 1f - ft / 0.5f);
            HideNova(a.Trail);
            HideNova(a.Glow);
            return false;
        }

        float step = a.Speed * dt;
        if (a.Rider) return TickRiddenArrow(a, now, dt, shadow);

        // 壁は手元で判定して刺す (ホストの知らせを待つと壁の向こうへ抜けて見える)
        if (step > 0f && PhysicsHelpers.AnyNonTriggersBetween(a.Pos, a.Dir, step + ArrowHalf * 0.3f, NovaWallMask()))
        {
            _vision = a.Vision;
            StickArrow(a, Off(a.Pos, a.Dir.x * step * 0.5f, a.Dir.y * step * 0.5f));
            _vision = false;
            return false;
        }

        a.Pos = Off(a.Pos, a.Dir.x * step, a.Dir.y * step);
        a.Left -= dt;
        // 射程を飛び切った (ホストの知らせが落ちた時もここで落とす)
        if (a.Left < -0.3f) a.Falling = true;

        float appear = FxMath.Clamp01(a.Age / 0.05f);
        PlaceNova(a.Arrow, shadow, a.Pos, 1f, 1f, a.Ang, WindWhite, appear);
        Vector2 tail = Off(a.Pos, -a.Dir.x * ArrowHalf, -a.Dir.y * ArrowHalf);
        PlaceNova(a.Trail, shadow, tail, 1.6f, 0.1f, a.Ang + 180f, ArcherGold, 0.55f * appear);
        PlaceNova(a.Glow, shadow, Off(a.Pos, a.Dir.x * ArrowHalf, a.Dir.y * ArrowHalf), 0.35f, 0.35f, 0f, ArcherRed, 0.5f * appear);

        if (now >= a.NextSpark)
        {
            a.NextSpark = now + 0.04f;
            _vision = a.Vision;
            Add(Shape.Star, tail, FxMath.V2(-a.Dir.x * 0.6f, -a.Dir.y * 0.6f + 0.2f), 0.3f, 0.07f, 0.02f, ArcherGold, ArcherRed, 0.9f, 0.05f, 0.4f, order: 9);
            _vision = false;
        }

        return false;
    }

    // 乗っている矢: 本人の足元に付けて、後ろへ風の筋を引く。止まるのはホストの知らせ (本人がそこで止まる) か射程切れ
    private static bool TickRiddenArrow(ArrowFlight a, float now, float dt, MeshRenderer shadow)
    {
        PlayerControl pc = a.Rider;
        a.Left -= dt;
        if (!pc || !pc.IsAlive() || a.Left < -0.6f)
        {
            a.Falling = true;
            return false;
        }

        if (Unseen(pc, a.Id))
        {
            HideNova(a.Arrow);
            HideNova(a.Trail);
            HideNova(a.Glow);
            return false;
        }

        // 本人はホストが 0.1 秒ごとに矢の位置へ跳ばすので、足元の矢は滑らかに追わせる
        Vector2 body = pc.Pos();
        Vector2 want = Off(body, a.Dir.x * 0.12f, a.Dir.y * 0.12f - 0.32f);
        float k = 1f - FxMath.Exp(-dt * 20f);
        a.Pos = a.Age < 0.05f ? want : FxMath.V2(a.Pos.x + (want.x - a.Pos.x) * k, a.Pos.y + (want.y - a.Pos.y) * k);

        float appear = FxMath.Clamp01(a.Age / 0.08f);
        PlaceNova(a.Arrow, shadow, a.Pos, 1.15f, 1.15f, a.Ang, WindWhite, appear);
        Vector2 tail = Off(a.Pos, -a.Dir.x * ArrowHalf, -a.Dir.y * ArrowHalf);
        PlaceNova(a.Trail, shadow, tail, 2.4f, 0.16f, a.Ang + 180f, ArcherGold, 0.6f * appear);
        PlaceNova(a.Glow, shadow, Off(a.Pos, a.Dir.x * ArrowHalf, a.Dir.y * ArrowHalf), 0.45f, 0.45f, 0f, ArcherRed, 0.55f * appear);

        if (now >= a.NextSpark)
        {
            a.NextSpark = now + 0.03f;
            _vision = a.Vision;
            // 体の周りを後ろへ流れる風の筋
            float side = FxMath.Range(-0.4f, 0.45f);
            Vector2 p = Off(body, -a.Dir.y * side + a.Dir.x * 0.3f, a.Dir.x * side + a.Dir.y * 0.3f);
            Add(Shape.Ray, p, FxMath.V2(-a.Dir.x * 3f, -a.Dir.y * 3f), 0.22f, 0.7f, 0.4f, WindWhite, ArcherGold, 0.55f, 0.1f, 0.4f, rot: a.Ang + 180f, order: 9);
            Add(Shape.Star, tail, FxMath.V2(-a.Dir.x * 0.6f, -a.Dir.y * 0.6f + 0.2f), 0.3f, 0.08f, 0.02f, ArcherGold, ArcherRed, 0.9f, 0.05f, 0.4f, order: 9);
            _vision = false;
        }

        return false;
    }

    // ── ハングマン ────────────────────────────────────────────

    private static readonly Color RopeTint = new(1f, 1f, 1f);
    private static readonly Color HangDark = new(0.12f, 0.04f, 0.08f);

    private static Sprite NooseSprite() => Utils.LoadSprite("EndKnot.Resources.Images.Fx.NooseLoop.png", NoosePpu);
    private static Sprite RopeSprite() => Utils.LoadSprite("EndKnot.Resources.Images.Fx.RopeSeg.png", RopePpu);

    // 縄で吊って消す: Pos = 吊られた人、Radius = 吊られた人の PlayerId + 1。消える前 (RpcExileV2 の前) に呼ぶ
    public static void HangmanNoose(PlayerControl victim)
    {
        if (!victim) return;
        Play(Kind.HangNoose, victim.Pos(), victim.PlayerId + 1);
    }

    private sealed class Noose
    {
        public bool Vision;
        public Vector2 At;
        public float Start;
        public int Body;
        public float BodySize;
        public bool Left;
        public NovaSprite Rope, Loop;
    }

    private static readonly List<Noose> Nooses = [];

    private static void ReleaseNoose(Noose n)
    {
        FxHands.HoldStop(n.Body, 0f);
        DestroyNova(n.Rope);
        DestroyNova(n.Loop);
    }

    private static void StartNoose(Vector2 at, int id)
    {
        PlayerControl pc = Utils.GetPlayerById((byte)id);
        if (Nooses.Count >= 6 || HiddenNow(id)) return;

        // 消える人の姿 (今の体の絵と色) を写して吊る
        int body = 0;
        float size = 0.5f;
        bool left = false;
        Vector2 c = at;
        try
        {
            if (pc && pc.cosmetics && pc.cosmetics.currentBodySprite != null && pc.cosmetics.currentBodySprite.BodySprite)
            {
                SpriteRenderer src = pc.cosmetics.currentBodySprite.BodySprite;
                size = FxMath.Abs(src.transform.lossyScale.x);
                left = src.flipX;
                Sprite sp = src.sprite;
                // 追放の後で体の絵が外れていたら、自分の体の絵 (同じ色のひな形) をその人の色で塗って代わりに吊る
                if (!sp)
                {
                    PlayerControl lp = PlayerControl.LocalPlayer;
                    SpriteRenderer mine = lp && lp.cosmetics && lp.cosmetics.currentBodySprite != null ? lp.cosmetics.currentBodySprite.BodySprite : null;
                    sp = mine ? mine.sprite : null;
                }

                if (sp) body = FxHands.HoldStart(sp, CurrentColorId(pc), _vision, SortingOrder + 9, -1f - 9 * 0.01f);
            }
        }
        catch (System.Exception e) { Utils.ThrowException(e); }

        Nooses.Add(new Noose
        {
            Vision = _vision, At = c, Start = Time.time, Body = body, BodySize = size, Left = left,
            Rope = NewNovaSprite(RopeSprite(), 10),
            Loop = NewNovaSprite(NooseSprite(), 11)
        });

        Particles(FxParticles.Preset.Smoke, Off(c, 0f, -0.35f), 5, GunSmoke, GunSmoke, size: 0.35f, speed: 0.4f, delay: 0.3f, order: 8);
        FxSound.At("FxHangNoose", c);
    }

    private static bool TickNoose(Noose n, float now, MeshRenderer shadowQuad)
    {
        float t = now - n.Start;
        if (t > 2.4f) return true;

        MeshRenderer shadow = n.Vision ? shadowQuad : null;
        const float top = 5f;

        // 0〜0.25 縄が降りる / 0.25〜0.4 首で締まる / 0.4〜1.0 吊り上げる / 1.0〜 揺れながら闇に消え、縄が上がる
        float lift = t < 0.4f ? 0f : 1f - FxMath.Pow(1f - FxMath.Clamp01((t - 0.4f) / 0.45f), 3f);
        float swing = t < 0.4f ? 0f : 12f * FxMath.Sin((t - 0.4f) * 5.5f) * FxMath.Exp(-(t - 0.4f) * 1.2f);
        // 輪はバイザーの下 (体の中心より少し上) で締まる
        float neckY = n.At.y + 0.06f + 0.95f * lift;
        float drop = FxMath.Clamp01(t / 0.25f);
        float retract = FxMath.Clamp01((t - 1.9f) / 0.5f);
        float loopY = n.At.y + top * (1f - drop) + (neckY - n.At.y) * drop + top * retract * retract;

        float sr = swing * FxMath.Deg2Rad;
        // 揺れは縄の上の端を支点に振れる
        float sway = FxMath.Sin(sr) * 0.6f;
        float bodyAlpha = 1f - FxMath.Clamp01((t - 1.1f) / 0.9f);
        float cinch = t is > 0.25f and < 0.45f ? 1.08f - 0.25f * (t - 0.25f) / 0.2f : 1f;

        Vector2 loop = FxMath.V2(n.At.x + sway, loopY);
        float ropeLen = n.At.y + top + 1f - loopY;
        PlaceNova(n.Rope, shadow, Off(loop, 0f, ropeLen * 0.5f + 0.18f), 1f, ropeLen / (256f / RopePpu), swing * 0.5f, RopeTint, 1f - retract);
        PlaceNova(n.Loop, shadow, loop, cinch, cinch, swing, RopeTint, 1f - retract);

        if (n.Body != 0)
        {
            Vector2 body = FxMath.V2(n.At.x + sway, n.At.y + 0.95f * lift);
            float ang = (n.Left ? 180f : 0f) + swing;
            FxHands.HoldPlace(n.Body, body.x, body.y, ang, n.BodySize, bodyAlpha <= 0f);
            FxHands.HoldAlpha(n.Body, bodyAlpha);

            if (t is > 1.1f and < 1.9f && Time.frameCount % 4 == 0)
            {
                _vision = n.Vision;
                Add(Shape.Cloud, Off(body, FxMath.Range(-0.2f, 0.2f), FxMath.Range(-0.3f, 0.2f)), FxMath.V2(0f, 0.3f), 0.6f, 0.2f, 0.45f, HangDark, HangDark, 0.5f, 0.2f, 0.5f, order: 12);
                _vision = false;
            }
        }

        return false;
    }

    // ── ワスプ ────────────────────────────────────────────────

    private static readonly Color WaspVenomGreen = new(0.55f, 0.95f, 0.25f);
    private static readonly Color WaspVenomPurple = new(0.6f, 0.2f, 0.7f);
    private static readonly Color WaspYellow = new(1f, 0.8f, 0.2f);

    private static Sprite WaspSprite(int frame) => Utils.LoadSprite(frame == 0 ? "EndKnot.Resources.Images.Fx.Wasp_0.png" : "EndKnot.Resources.Images.Fx.Wasp_1.png", WaspPpu);

    // 刺した (刺した本人の画面だけ。刺されたことは本人に知られない)
    public static void WaspSting(PlayerControl wasp, PlayerControl target)
    {
        if (!wasp || !target) return;
        PlayFor(Kind.WaspSting, target.Pos(), wasp.PlayerId + 1 + 32 * target.PlayerId, wasp);
    }

    // 毒が回って倒れる (視界の中の全員)
    public static void WaspVenom(PlayerControl victim)
    {
        if (!victim) return;
        Play(Kind.WaspVenom, victim.Pos(), victim.PlayerId + 1);
    }

    // 群れ: Radius = 本人の PlayerId + 1 + 32 × 秒数
    public static void WaspSwarm(PlayerControl wasp, float seconds)
    {
        if (!wasp) return;
        Play(Kind.WaspSwarm, wasp.Pos(), wasp.PlayerId + 1 + 32 * FxMath.Clamp((int)System.MathF.Round(seconds), 1, 60));
    }

    private sealed class Bug
    {
        public NovaSprite S;
        public float Phase, Radius, Speed, Height;
        public Vector2 Pos;
        public bool Frame;
    }

    private sealed class Swarm
    {
        public byte Id;
        public PlayerControl Pc;
        public bool Vision;
        public float Start, Until;
        public Bug[] Bugs;
        public float NextBuzz, NextFlap;
        public Sprite F0, F1;
        // 刺す時の 1 匹 (Pc = null): 刺した人から刺された人へ飛んで、刺して、飛び去る
        public Vector2 From, To;
        public bool Sting;
        public float DyingFrom = -1f;
    }

    private static readonly List<Swarm> Swarms = [];

    private static void ReleaseSwarm(Swarm s)
    {
        foreach (Bug b in s.Bugs) DestroyNova(b.S);
    }

    private static Bug NewBug(float phase, float radius, float speed, float height) =>
        new() { S = NewNovaSprite(WaspSprite(0), 12), Phase = phase, Radius = radius, Speed = speed, Height = height };

    private static void SpawnWaspSting(Vector2 at, int waspId, int targetId)
    {
        PlayerControl wasp = Utils.GetPlayerById((byte)waspId);
        if (!wasp || Swarms.Count >= 8) return;

        Vector2 from = Off(wasp.Pos(), 0f, 0.3f);
        Swarms.Add(new Swarm
        {
            Id = (byte)waspId, Vision = _vision, Start = Time.time, Until = Time.time + 1.3f, Sting = true, F0 = WaspSprite(0), F1 = WaspSprite(1), From = from, To = Off(at, 0f, 0.15f),
            Bugs = [NewBug(0f, 0f, 0f, 0f)]
        });
        FxSound.At("FxWaspSting", at, 0.9f);
    }

    private static void SpawnWaspVenom(Vector2 c)
    {
        // 紫に膨れる毒の輪・緑の泡・群がる蜂の影
        Add(Shape.Glow, c, Vector2.zero, 0.8f, 0.4f, 1.4f, WaspVenomGreen, WaspVenomPurple, 0.7f, 0.05f, 0.4f, order: 8);
        Add(Shape.Ring, c, Vector2.zero, 0.6f, 0.2f, 1.6f, WaspVenomGreen, WaspVenomPurple, 0.6f, 0.02f, 0.3f, order: 9);
        Particles(FxParticles.Preset.Motes, c, 18, WaspVenomGreen, WaspVenomPurple, size: 0.6f, speed: 0.6f, order: 10);
        for (int i = 0; i < 9; i++)
        {
            float ang = i * 40f * FxMath.Deg2Rad;
            Vector2 st = Off(c, FxMath.Cos(ang) * 0.25f, FxMath.Sin(ang) * 0.25f + 0.1f);
            Add(Shape.Glow, st, FxMath.V2(FxMath.Cos(ang) * 0.3f, 0.9f), 0.9f, 0.1f, 0.03f, WaspVenomGreen, WaspVenomPurple, 0.9f, 0.1f, 0.5f, delay: i * 0.05f, order: 10);
        }

        if (Swarms.Count >= 8) return;
        var bugs = new Bug[7];
        for (int i = 0; i < bugs.Length; i++) bugs[i] = NewBug(i * 0.9f, 0.45f + 0.08f * (i % 3), 5f + i * 0.4f, 0.1f + 0.08f * (i % 2));
        Swarms.Add(new Swarm { Id = 255, F0 = WaspSprite(0), F1 = WaspSprite(1), Vision = _vision, Start = Time.time, Until = Time.time + 1.6f, From = c, To = c, Bugs = bugs });
    }

    private static void StartSwarm(int id, int seconds)
    {
        PlayerControl pc = Utils.GetPlayerById((byte)id);
        for (int i = Swarms.Count - 1; i >= 0; i--)
        {
            if (Swarms[i].Id != id || Swarms[i].Sting) continue;
            ReleaseSwarm(Swarms[i]);
            Swarms.RemoveAt(i);
        }

        if (!pc || Swarms.Count >= 8) return;
        var bugs = new Bug[12];
        for (int i = 0; i < bugs.Length; i++) bugs[i] = NewBug(i * 0.53f, 0.5f + 0.12f * (i % 4), 3.5f + (i % 5) * 0.5f, 0.05f + 0.1f * (i % 3));
        Swarms.Add(new Swarm { Id = (byte)id, Pc = pc, F0 = WaspSprite(0), F1 = WaspSprite(1), Vision = _vision, Start = Time.time, Until = Time.time + FxMath.Max(seconds, 1), Bugs = bugs });
        if (!Unseen(pc, (byte)id)) FxSound.At("FxWaspSwarm", pc.Pos());
    }

    private static bool TickSwarm(Swarm s, float now, MeshRenderer shadowQuad)
    {
        float t = now - s.Start;
        MeshRenderer shadow = s.Vision ? shadowQuad : null;
        bool flap = now >= s.NextFlap;
        if (flap) s.NextFlap = now + 0.045f;

        if (s.Sting)
        {
            if (now >= s.Until) return true;
            // 0〜0.3 飛んでいく / 0.3〜0.55 周りを 1 周して刺す / 0.55〜 上へ飛び去る
            Bug b = s.Bugs[0];
            Vector2 p;
            float ang;
            if (t < 0.3f)
            {
                float u = t / 0.3f;
                p = FxMath.V2(s.From.x + (s.To.x - s.From.x) * u, s.From.y + (s.To.y - s.From.y) * u + 0.35f * FxMath.Sin(u * FxMath.PI));
                ang = s.To.x >= s.From.x ? 0f : 180f;
            }
            else if (t < 0.55f)
            {
                float u = (t - 0.3f) / 0.25f;
                float a = u * 2f * FxMath.PI;
                float jab = u > 0.8f ? (u - 0.8f) * 5f : 0f;
                p = Off(s.To, FxMath.Cos(a) * 0.3f * (1f - jab), FxMath.Sin(a) * 0.18f * (1f - jab));
                ang = FxMath.Sin(a) >= 0f ? 180f : 0f;
                if (u > 0.8f && !b.Frame)
                {
                    b.Frame = true;
                    _vision = s.Vision;
                    Add(Shape.Star, s.To, Vector2.zero, 0.3f, 0.35f, 0.1f, WindWhite, WaspVenomGreen, 1f, 0.01f, 0.3f, order: 13);
                    Add(Shape.Glow, s.To, Vector2.zero, 1.2f, 0.25f, 0.2f, WaspVenomPurple, WaspVenomPurple, 0.6f, 0.05f, 0.4f, order: 11);
                    Particles(FxParticles.Preset.Motes, s.To, 8, WaspVenomGreen, WaspVenomPurple, size: 0.4f, speed: 0.5f, order: 12);
                    _vision = false;
                }
            }
            else
            {
                float u = (t - 0.55f) / 0.75f;
                p = Off(s.To, 0.6f * u, 1.6f * u * u + 0.2f * u);
                ang = 0f;
            }

            if (flap) b.S.Sr.sprite = (Time.frameCount & 1) == 0 ? s.F0 : s.F1;
            float al = t > 1.0f ? 1f - (t - 1.0f) / 0.3f : FxMath.Clamp01(t / 0.08f);
            PlaceNova(b.S, shadow, p, ang > 90f ? -1f : 1f, 1f, 0f, WindWhite, al);
            return false;
        }

        PlayerControl pc = s.Pc;
        if (s.DyingFrom < 0f && (now >= s.Until || (s.Id != 255 && (!pc || !pc.IsAlive())))) s.DyingFrom = now;
        float fade = s.DyingFrom >= 0f ? 1f - (now - s.DyingFrom) / 0.5f : 1f;
        if (fade <= 0f) return true;

        bool hide = pc && Unseen(pc, s.Id);
        Vector2 c = pc ? pc.Pos() : s.From;
        float appear = FxMath.Clamp01(t / 0.4f);

        if (pc && !hide && s.DyingFrom < 0f && now >= s.NextBuzz)
        {
            s.NextBuzz = now + 1.4f;
            if (t > 0.5f) SoundSeen("FxWaspSwarm", c, 0.55f, s.Vision);
        }

        for (int i = 0; i < s.Bugs.Length; i++)
        {
            Bug b = s.Bugs[i];
            if (hide)
            {
                HideNova(b.S);
                continue;
            }

            // 楕円の周回に小さな揺れを重ねて、群れが生き物らしくまとわりつく。散る時は外へ飛び去る
            float a = b.Phase + t * b.Speed;
            float out1 = s.DyingFrom >= 0f ? (now - s.DyingFrom) * 3f : 0f;
            float r = b.Radius * (0.5f + 0.5f * appear) + out1;
            Vector2 p = Off(c, FxMath.Cos(a) * r + 0.06f * FxMath.Sin(t * 13f + i), FxMath.Sin(a) * r * 0.6f + b.Height + 0.05f * FxMath.Sin(t * 17f + i * 2f));
            float vx = -FxMath.Sin(a);
            if (flap) b.S.Sr.sprite = ((Time.frameCount + i) & 1) == 0 ? s.F0 : s.F1;
            PlaceNova(b.S, shadow, p, vx < 0f ? -0.9f : 0.9f, 0.9f, 0f, WindWhite, appear * fade);
        }

        return false;
    }

    // ── トレマー ────────────────────────────────────────────

    private const float DoomRadius = 2.5f;
    private static readonly Color QuakeDust = new(0.62f, 0.52f, 0.42f, 0.8f);
    private static readonly Color QuakeLava = new(1f, 0.45f, 0.12f);
    private static readonly Color QuakeDeep = new(0.25f, 0.1f, 0.05f);

    // 破滅の間 (始まりと毎秒): Radius = 本人の PlayerId + 1 + 32 × 残り秒数。続いている間は残りを延ばすだけ
    public static void TremorDoom(PlayerControl pc, int secondsLeft)
    {
        if (!pc) return;
        Play(Kind.TremorDoom, pc.Pos(), pc.PlayerId + 1 + 32 * FxMath.Clamp(secondsLeft, 1, 255));
    }

    public static void TremorCrush(PlayerControl victim)
    {
        if (!victim) return;
        Play(Kind.TremorCrush, victim.Pos(), victim.PlayerId + 1);
    }

    private sealed class Doom
    {
        public byte Id;
        public PlayerControl Pc;
        public bool Vision;
        public float Start, Until;
        public NovaSprite Zone, ZoneGlow;
        public float NextWave, NextCrack, NextRumble, NextDust;
        public float DyingFrom = -1f;
    }

    private static readonly List<Doom> Dooms = [];

    private static void ReleaseDoom(Doom d)
    {
        DestroyNova(d.Zone);
        DestroyNova(d.ZoneGlow);
    }

    private static void StartDoom(int id, Vector2 at, int seconds)
    {
        float now = Time.time;
        foreach (Doom d in Dooms)
        {
            if (d.Id != id || d.DyingFrom >= 0f) continue;
            d.Until = now + seconds + 1.2f;
            return;
        }

        PlayerControl pc = Utils.GetPlayerById((byte)id);
        if (!pc || Dooms.Count >= 4) return;

        Dooms.Add(new Doom
        {
            Id = (byte)id, Pc = pc, Vision = _vision, Start = now, Until = now + seconds + 1.2f,
            ZoneGlow = NewNovaSprite(_glow, 0, true),
            Zone = NewNovaSprite(_ring, 1, true)
        });

        if (Unseen(pc, (byte)id)) return;
        // 始まり: 足元が割れて溶岩色の光が噴き、地面が大きく揺れる
        SpawnSlam(at, 0.9f);
        Impact(at, DoomRadius, QuakeLava, 0.1f, 0.35f, 0.8f);
        FxSound.At("FxTremorDoom", at);
    }

    private static bool TickDoom(Doom d, float now, MeshRenderer shadowQuad)
    {
        PlayerControl pc = d.Pc;
        if (d.DyingFrom < 0f && (!pc || !pc.IsAlive() || now >= d.Until)) d.DyingFrom = now;
        float fade = d.DyingFrom >= 0f ? 1f - (now - d.DyingFrom) / 0.6f : 1f;
        if (fade <= 0f || !pc) return true;

        MeshRenderer shadow = d.Vision ? shadowQuad : null;
        float t = now - d.Start;
        bool hide = Unseen(pc, d.Id);
        if (hide)
        {
            HideNova(d.Zone);
            HideNova(d.ZoneGlow);
            return false;
        }

        Vector2 c = pc.Pos();
        Vector2 feet = Off(c, 0f, -0.38f);
        float appear = FxMath.Clamp01(t / 0.5f) * fade;
        float beat = 0.5f + 0.5f * FxMath.Sin(t * 6f);

        // 倒される範囲の縁: 床に溶岩色の輪が脈打つ
        PlaceNova(d.Zone, shadow, feet, DoomRadius * 2f, DoomRadius * 2f, t * 8f, QuakeLava, (0.35f + 0.25f * beat) * appear);
        PlaceNova(d.ZoneGlow, shadow, feet, DoomRadius * 2.3f, DoomRadius * 2.3f, 0f, QuakeDeep, 0.35f * appear);
        if (d.DyingFrom >= 0f) return false;

        _vision = d.Vision;

        // 地面を走る揺れの波 (倒す間隔と同じ 0.3 秒おき)
        if (now >= d.NextWave)
        {
            d.NextWave = now + 0.3f;
            Add(Shape.Ring, feet, Vector2.zero, 0.45f, 0.4f, DoomRadius * 2f, QuakeLava, QuakeDust, 0.45f, 0.02f, 0.4f, order: 2, flat: true);
        }

        // 放射状のひび: 足元から範囲の縁まで、溶岩色の芯で光って消える
        if (now >= d.NextCrack)
        {
            d.NextCrack = now + 0.22f;
            float a = FxMath.Range(0f, 2f * FxMath.PI);
            float len = FxMath.Range(1.2f, DoomRadius);
            Vector2 end = Off(feet, FxMath.Cos(a) * len, FxMath.Sin(a) * len * 0.6f);
            int m = FxLines.Fractal(feet, end, 3, len * 0.12f, 0.55f, LineBuf2);
            Lines(LineBuf2, m, new FxLines.Spec
            {
                Width = 0.08f, Taper = FxLines.Taper.Branch, Core = QuakeLava, Under = QuakeDeep, CoreAlpha = 0.95f, UnderAlpha = 0.6f, UnderMul = 3f,
                Grow = 0.12f, Decel = true, Life = 0.9f, FadeOutFrom = 0.5f, CoreFadeOutFrom = 0.3f
            }, 1);
        }

        // 跳ねる小石と土煙
        if (now >= d.NextDust)
        {
            d.NextDust = now + 0.12f;
            float a = FxMath.Range(0f, 2f * FxMath.PI);
            float r = FxMath.Range(0.3f, DoomRadius);
            Vector2 p = Off(feet, FxMath.Cos(a) * r, FxMath.Sin(a) * r * 0.6f);
            Add(Shape.Cloud, p, FxMath.V2(0f, 0.25f), 0.8f, 0.25f, 0.6f, QuakeDust, QuakeDust, 0.45f, 0.1f, 0.4f, order: 3);
            Add(Shape.Chunk, p, FxMath.V2(FxMath.Range(-0.6f, 0.6f), 2.2f), 0.5f, 0.07f, 0.06f, QuakeDeep, QuakeDust, 1f, 0.01f, 0.7f, rise: -6f, spin: 400f, order: 4);
        }

        // 地鳴りと画面の揺れ (近くにいる人ほど強く)
        if (now >= d.NextRumble)
        {
            d.NextRumble = now + 1.8f;
            ImpactSeen(c, DoomRadius, QuakeLava, 0.05f, 0.12f, 1.8f, d.Vision);
            if (t > 0.5f) SoundSeen("FxTremorDoom", c, 0.6f, d.Vision);
        }

        _vision = false;
        return false;
    }

    // 倒せない相手 (盾など) が範囲に居続けると 0.3 秒ごとに知らせが来るので、同じ所では 1.2 秒に 1 回だけ出す
    private static readonly List<(Vector2 At, float Time)> RecentCrush = [];

    private static bool CrushRepeated(Vector2 c)
    {
        float now = Time.time;
        for (int i = RecentCrush.Count - 1; i >= 0; i--)
        {
            (Vector2 at, float t) = RecentCrush[i];
            if (now - t > 1.2f)
            {
                RecentCrush.RemoveAt(i);
                continue;
            }

            if ((at.x - c.x) * (at.x - c.x) + (at.y - c.y) * (at.y - c.y) < 0.36f) return true;
        }

        if (RecentCrush.Count < 32) RecentCrush.Add((c, now));
        return false;
    }

    private static void SpawnTremorCrush(Vector2 c)
    {
        // 足元から岩の柱が突き上がって砕ける
        Vector2 feet = Off(c, 0f, -0.35f);
        SpawnSlam(feet, 0.9f);
        for (int i = 0; i < 7; i++)
        {
            float x = (i - 3) * 0.12f;
            float h = 0.5f + 0.25f * (3 - FxMath.Abs(i - 3)) / 3f;
            Add(Shape.Chunk, Off(feet, x, 0.05f), FxMath.V2(x * 2f, 4f * h), 0.55f, 0.16f, 0.1f, QuakeDeep, QuakeDust, 1f, 0.01f, 0.6f, rise: -9f, spin: 200f, delay: i * 0.015f, order: 9);
        }

        Add(Shape.Glow, feet, Vector2.zero, 0.35f, 0.4f, 1.3f, QuakeLava, QuakeDeep, 0.8f, 0.01f, 0.3f, order: 8);
        Impact(c, 1.5f, QuakeLava, 0.15f, 0.25f, 0.4f);
    }

    // ── 毎フレーム ──

    private static void PulseRoles7()
    {
        float now = Time.time;
        float dt = Time.deltaTime;
        MeshRenderer shadowQuad = HudManager.InstanceExists ? HudManager.Instance.ShadowQuad : null;

        for (int i = BowRigs.Count - 1; i >= 0; i--)
        {
            bool done;
            try { done = TickBow(BowRigs[i], now, shadowQuad); }
            catch (System.Exception e) { Logger.Warn($"Bow: {e.Message}", "ExplosionFx"); done = true; }
            if (!done) continue;
            ReleaseBow(BowRigs[i]);
            BowRigs.RemoveAt(i);
        }

        for (int i = Arrows.Count - 1; i >= 0; i--)
        {
            bool done;
            try { done = TickArrow(Arrows[i], now, dt, shadowQuad); }
            catch (System.Exception e) { Logger.Warn($"Arrow: {e.Message}", "ExplosionFx"); done = true; }
            if (!done) continue;
            ReleaseArrow(Arrows[i]);
            Arrows.RemoveAt(i);
        }

        for (int i = Nooses.Count - 1; i >= 0; i--)
        {
            bool done;
            try { done = TickNoose(Nooses[i], now, shadowQuad); }
            catch (System.Exception e) { Logger.Warn($"Noose: {e.Message}", "ExplosionFx"); done = true; }
            if (!done) continue;
            ReleaseNoose(Nooses[i]);
            Nooses.RemoveAt(i);
        }

        for (int i = Swarms.Count - 1; i >= 0; i--)
        {
            bool done;
            try { done = TickSwarm(Swarms[i], now, shadowQuad); }
            catch (System.Exception e) { Logger.Warn($"Swarm: {e.Message}", "ExplosionFx"); done = true; }
            if (!done) continue;
            ReleaseSwarm(Swarms[i]);
            Swarms.RemoveAt(i);
        }

        for (int i = Dooms.Count - 1; i >= 0; i--)
        {
            bool done;
            try { done = TickDoom(Dooms[i], now, shadowQuad); }
            catch (System.Exception e) { Logger.Warn($"Doom: {e.Message}", "ExplosionFx"); done = true; }
            if (!done) continue;
            ReleaseDoom(Dooms[i]);
            Dooms.RemoveAt(i);
        }

        _vision = false;
    }
}
