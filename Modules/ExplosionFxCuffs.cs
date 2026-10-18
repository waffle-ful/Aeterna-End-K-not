using System.Collections.Generic;
using UnityEngine;

namespace EndKnot.Modules;

// 手錠: かけられた人の体の前に鉄の輪を 2 つ並べて小さな鎖で繋ぎ、輪から本人の色の拳を垂らす (部品は FxChain の手錠モード)。
// 輪は締まる動きで現れ、鎖は両端から伸びる。時間切れ・死亡で鎖が真ん中から切れ、輪は外れて落ちる。会議で消える。
// ベント・透明・梯子などで本人が見えない間は隠す (手錠が居場所を明かさないように)。
// 見せるのはかけられた本人とかけた人だけ (周りに見えると、手錠をかけた役職と相手がキラーかどうかが割れる)。
public static partial class ExplosionFx
{
    private const int MaxCuffs = 4;
    private const float CuffSpread = 0.18f;  // 輪の中心は体の中心から左右 ±0.18 (錠前箱が体の幅に収まる)
    private const float CuffDown = 0.12f;    // 輪はお腹の前
    private const float CuffRing = 1.1f;     // 輪の絵 (帯の外径 0.16u) の倍率
    private const float CuffLink = 0.5f;     // 鎖の輪は 2 人を繋ぐ鎖の半分の大きさ
    private const float CuffHand = 0.14f;
    private const float CuffHandDrop = 0.075f;
    private const float CuffHandTilt = 12f;
    private const float CuffChainLength = 0.3f;

    private sealed class Cuff
    {
        public byte Id;
        public PlayerControl Pc;
        public int Chain;
        public float Until;
    }

    private static readonly List<Cuff> Cuffs = [];

    // ホストが呼ぶ。target と cuffer の画面にだけ出す
    public static void Handcuffed(PlayerControl target, float seconds, PlayerControl cuffer)
    {
        if (!target) return;

        float packed = target.PlayerId + 1 + 32 * FxMath.Clamp((int)System.MathF.Round(seconds), 1, 180);
        Vector2 pos = target.Pos();
        PlayFor(Kind.Handcuffed, pos, packed, target);
        if (cuffer && cuffer.PlayerId != target.PlayerId) PlayFor(Kind.Handcuffed, pos, packed, cuffer);
    }

    private static void StartCuffs(int id, int seconds)
    {
        PlayerControl pc = Utils.GetPlayerById((byte)id);
        EndCuffs(id, false);
        if (!pc || !pc.IsAlive()) return;

        if (Cuffs.Count >= MaxCuffs)
        {
            FxChain.Stop(Cuffs[0].Chain, false);
            Cuffs.RemoveAt(0);
        }

        int color = pc.Data ? pc.Data.DefaultOutfit.ColorId : 0;
        bool vision = _vision && HudManager.InstanceExists && HudManager.Instance.ShadowQuad;
        int chain = FxChain.Start(CuffChainLength, color, color, vision, SortingOrder + 6, -1f - 6 * 0.01f);
        if (chain == 0)
        {
            Logger.Warn("handcuffs could not be drawn (FxChain not ready or full)", "ExplosionFx");
            return;
        }

        FxChain.SetHandSize(chain, CuffHand);
        FxChain.SetCuffs(chain, CuffRing, CuffLink, CuffHandDrop, CuffHandTilt);
        Cuffs.Add(new Cuff { Id = (byte)id, Pc = pc, Chain = chain, Until = Time.time + FxMath.Max(seconds, 1) });
        if (!Unseen(pc, (byte)id)) FxSound.At("FxChainBind", pc.Pos(), 0.6f);
    }

    private static void EndCuffs(int id, bool snap)
    {
        for (int i = Cuffs.Count - 1; i >= 0; i--)
        {
            if (Cuffs[i].Id != id) continue;
            FxChain.Stop(Cuffs[i].Chain, snap);
            Cuffs.RemoveAt(i);
        }
    }

    private static void StopAllCuffs()
    {
        for (int i = Cuffs.Count - 1; i >= 0; i--) FxChain.Stop(Cuffs[i].Chain, false);
        Cuffs.Clear();
    }

    private static void PulseCuffs(float now)
    {
        for (int i = Cuffs.Count - 1; i >= 0; i--)
        {
            Cuff c = Cuffs[i];
            PlayerControl pc = c.Pc;

            if (!pc || !pc.IsAlive() || now >= c.Until)
            {
                FxChain.Stop(c.Chain, pc);
                Cuffs.RemoveAt(i);
                continue;
            }

            try
            {
                Vector2 p = pc.Pos();
                float y = p.y - CuffDown;
                FxChain.Place(c.Chain, FxMath.V2(p.x - CuffSpread, y), FxMath.V2(p.x + CuffSpread, y), Unseen(pc, c.Id));
            }
            catch (System.Exception e)
            {
                Logger.Warn($"Cuffs: {e.Message}", "ExplosionFx");
                FxChain.Stop(c.Chain, false);
                Cuffs.RemoveAt(i);
            }
        }
    }
}
