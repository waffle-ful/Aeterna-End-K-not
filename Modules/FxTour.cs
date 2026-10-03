using UnityEngine;

namespace EndKnot.Modules;

// 音の付いた役職演出を、ホストの足元で一定の間隔を空けて順に流す (判定・キルは起きない)
public static class FxTour
{
    private static readonly ExplosionFx.Kind[] Kinds =
    [
        ExplosionFx.Kind.Fire, ExplosionFx.Kind.Supernova, ExplosionFx.Kind.GustRight, ExplosionFx.Kind.WindTrailRight,
        ExplosionFx.Kind.GeminiSplit, ExplosionFx.Kind.GeminiShatter, ExplosionFx.Kind.WarpOut, ExplosionFx.Kind.WarpIn,
        ExplosionFx.Kind.Freeze, ExplosionFx.Kind.TimeStop, ExplosionFx.Kind.LightningStrike, ExplosionFx.Kind.Slam,
        ExplosionFx.Kind.Splash, ExplosionFx.Kind.Ignite, ExplosionFx.Kind.Swallow, ExplosionFx.Kind.VoidBurst,
        ExplosionFx.Kind.Smoke, ExplosionFx.Kind.BurrowIn, ExplosionFx.Kind.BurrowOut, ExplosionFx.Kind.Drain,
        ExplosionFx.Kind.Poison, ExplosionFx.Kind.Petrify, ExplosionFx.Kind.PuppetStrings, ExplosionFx.Kind.Tornado,
        ExplosionFx.Kind.TornadoLift,
        ExplosionFx.Kind.TimeRewind, ExplosionFx.Kind.RewindLand, ExplosionFx.Kind.RewindRevive, ExplosionFx.Kind.TimeSteal,
        ExplosionFx.Kind.ChronoRampage, ExplosionFx.Kind.CamoMist, ExplosionFx.Kind.OilDrip, ExplosionFx.Kind.DemoFuse,
        ExplosionFx.Kind.VultureFeast, ExplosionFx.Kind.HexMark, ExplosionFx.Kind.WebSpin, ExplosionFx.Kind.WebSnare,
        ExplosionFx.Kind.WebDevour, ExplosionFx.Kind.RevengeAwaken,
        ExplosionFx.Kind.IaiSlash, ExplosionFx.Kind.WerewolfMaul, ExplosionFx.Kind.WerewolfRampage,
        ExplosionFx.Kind.AltruistRevive, ExplosionFx.Kind.ForceFieldUp, ExplosionFx.Kind.ForceRepel, ExplosionFx.Kind.GoddessGuard,
        ExplosionFx.Kind.GoddessPetrify, ExplosionFx.Kind.ChainBind, ExplosionFx.Kind.PestilenceRise, ExplosionFx.Kind.SporeCloud,
        ExplosionFx.Kind.StoneGain, ExplosionFx.Kind.RiftTear, ExplosionFx.Kind.PortalPass, ExplosionFx.Kind.BowlStrike,
        ExplosionFx.Kind.PenguinGrab
    ];

    // 流している途中で止めたり流し直したりした時に、前の回の残りを捨てるための通し番号
    private static int _seq;

    public static int Count => Kinds.Length;

    public static void Stop()
    {
        _seq++;
        ExplosionFx.StopAuras();
    }

    public static void Start(float gap, Vector2 shift, System.Action<string> report = null)
    {
        int seq = ++_seq;

        for (int i = 0; i < Kinds.Length; i++)
        {
            ExplosionFx.Kind kind = Kinds[i];

            LateTask.New(() =>
            {
                if (seq != _seq) return;
                report?.Invoke($"FXTOUR {kind}");
                PlayOne(kind, shift);
            }, i * gap, "FxTour", false);
        }

        // 最後の覚醒で付いた気配を片付ける
        LateTask.New(() =>
        {
            if (seq != _seq) return;
            ExplosionFx.StopAuras();
            report?.Invoke("OK fxtour done");
        }, Kinds.Length * gap + 3f, "FxTourEnd", false);
    }

    private static void PlayOne(ExplosionFx.Kind kind, Vector2 shift)
    {
        PlayerControl lp = PlayerControl.LocalPlayer;
        if (!lp || !GameStates.InGame || GameStates.IsMeeting) return;

        Vector2 at = lp.Pos() + shift;
        float self = lp.PlayerId + 1;

        switch (kind)
        {
            // 本人の画面にだけ出る演出
            case ExplosionFx.Kind.OilDrip:
            case ExplosionFx.Kind.HexMark:
                ExplosionFx.PlayFor(kind, at, 1f, lp);
                break;
            case ExplosionFx.Kind.DemoFuse:
                ExplosionFx.PlayFor(kind, at, 3f, lp);
                break;
            case ExplosionFx.Kind.WebSpin:
                ExplosionFx.PlayFor(kind, at, 2f, lp);
                break;
            case ExplosionFx.Kind.RevengeAwaken:
                ExplosionFx.PlayFor(kind, at, self, lp);
                break;
            case ExplosionFx.Kind.GoddessGuard:
            case ExplosionFx.Kind.WerewolfRampage:
                ExplosionFx.PlayFor(kind, at, 3f, lp);
                break;
            case ExplosionFx.Kind.PestilenceRise:
            case ExplosionFx.Kind.RiftTear:
                ExplosionFx.PlayFor(kind, at, 1f, lp);
                break;
            case ExplosionFx.Kind.SporeCloud:
                ExplosionFx.PlayFor(kind, at, 2f, lp);
                break;
            // 石を使った時 (石の番号 + 11)
            case ExplosionFx.Kind.StoneGain:
                ExplosionFx.PlayFor(kind, at, 14f, lp);
                break;
            // 鎖のもう一端は誰でもない番号にして、足元の横へ張る
            case ExplosionFx.Kind.ChainBind:
                ExplosionFx.Play(kind, at, 256f);
                break;
            case ExplosionFx.Kind.ForceFieldUp:
                ExplosionFx.Play(kind, at, 2f);
                break;
            // 秒数を載せる演出
            case ExplosionFx.Kind.TimeRewind:
            case ExplosionFx.Kind.WebSnare:
            case ExplosionFx.Kind.Freeze:
            case ExplosionFx.Kind.TimeStop:
            case ExplosionFx.Kind.Tornado:
                ExplosionFx.Play(kind, at, 3f);
                break;
            // 大きさや距離を載せる演出
            case ExplosionFx.Kind.Fire:
            case ExplosionFx.Kind.Supernova:
            case ExplosionFx.Kind.GustRight:
            case ExplosionFx.Kind.WindTrailRight:
                ExplosionFx.Play(kind, at, 2.5f);
                break;
            // 誰の番号かを載せる演出 (砂の行き先・血の色)
            case ExplosionFx.Kind.TimeSteal:
            case ExplosionFx.Kind.VultureFeast:
            case ExplosionFx.Kind.Drain:
            case ExplosionFx.Kind.PuppetStrings:
            case ExplosionFx.Kind.IaiSlash:
            case ExplosionFx.Kind.WerewolfMaul:
                ExplosionFx.Play(kind, at, self);
                break;
            default:
                ExplosionFx.Play(kind, at, 1f);
                break;
        }
    }
}
