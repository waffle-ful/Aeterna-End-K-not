using AmongUs.GameOptions;

namespace EndKnot.Roles;

// コンビネーション役職の相方。desync Impostor 基底の中立キラーで、Vega との逢い引きで強化される。
// 出現率オプションは持たない (Vega.cs にぶら下がる。Modules/CombinationRoles.cs の Pairs 登録により
// 抽選プールには入らない)。
public class Altair : RoleBase
{
    public static bool On;
    public override bool IsEnable => On;

    public override void SetupCustomOption() { }

    public override void Init()
    {
        On = false;
    }

    public override void Add(byte playerId)
    {
        On = true;
        Vega.AltairId = playerId;
    }

    public override void Remove(byte playerId)
    {
        On = false;
    }

    public override void ApplyGameOptions(IGameOptions opt, byte playerId)
    {
        opt.SetVision(Vega.ImpostorVision.GetBool());
    }

    public override void SetKillCooldown(byte id)
    {
        Main.AllPlayerKillCooldown[id] = Vega.GetCurrentAltairKillCooldown();
    }

    public override bool CanUseSabotage(PlayerControl pc)
    {
        return false;
    }

    public override bool CanUseImpostorVentButton(PlayerControl pc)
    {
        return pc.IsAlive() && Vega.AltairCanUseVent.GetBool();
    }

    public override bool OnCheckMurder(PlayerControl killer, PlayerControl target)
    {
        if (target.PlayerId == Vega.VegaId) return false;

        return base.OnCheckMurder(killer, target);
    }
}
