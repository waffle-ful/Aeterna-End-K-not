using System;
using AmongUs.GameOptions;
using EndKnot.Modules;
using static EndKnot.Options;
using static EndKnot.Translator;

namespace EndKnot.Roles;

public class Remotekiller : RoleBase
{
    private const int Id = 704000;
    public static bool On;

    private static OptionItem KillCooldown;
    private static OptionItem HasImpostorVision;
    private static OptionItem CanVent;

    private byte RemotekillerID = byte.MaxValue;
    public byte MarkedTargetId = byte.MaxValue;

    public override bool IsEnable => On;

    public override void SetupCustomOption()
    {
        StartSetup(Id)
            .AutoSetupOption(ref KillCooldown, 30, new IntegerValueRule(5, 180, 5), OptionFormat.Seconds)
            .AutoSetupOption(ref HasImpostorVision, true)
            .AutoSetupOption(ref CanVent, true);
    }

    public override void Init()
    {
        On = false;
        RemotekillerID = byte.MaxValue;
        MarkedTargetId = byte.MaxValue;
    }

    public override void Add(byte playerId)
    {
        On = true;
        RemotekillerID = playerId;
        MarkedTargetId = byte.MaxValue;
    }

    public override void Remove(byte playerId)
    {
        if (RemotekillerID == playerId) On = false;
    }

    public override void SetKillCooldown(byte id)
    {
        Main.AllPlayerKillCooldown[id] = KillCooldown.GetFloat();
    }

    public override bool CanUseImpostorVentButton(PlayerControl pc)
    {
        return CanVent.GetBool() && pc.IsAlive();
    }

    public override bool CanUseSabotage(PlayerControl pc)
    {
        return base.CanUseSabotage(pc) || (pc.IsAlive() && !(UsePhantomBasis.GetBool() && UsePhantomBasisForNKs.GetBool()));
    }

    public override void ApplyGameOptions(IGameOptions opt, byte id)
    {
        opt.SetVision(HasImpostorVision.GetBool());
        if (UsePhantomBasis.GetBool() && UsePhantomBasisForNKs.GetBool())
            AURoleOptions.PhantomCooldown = 1f;
    }

    public override bool OnCheckMurder(PlayerControl killer, PlayerControl target)
    {
        // Mark the target instead of killing
        MarkedTargetId = target.PlayerId;
        killer.SetKillCooldown(KillCooldown.GetFloat(), target: target);
        killer.Notify(string.Format(GetString("Remotekiller.Marked"), target.GetRealName()));
        Utils.NotifyRoles(SpecifySeer: killer, SpecifyTarget: killer);
        return false;
    }

    public override void OnEnterVent(PlayerControl pc, Vent vent)
    {
        if (MarkedTargetId == byte.MaxValue) return;
        if (pc.PlayerId != RemotekillerID) return;

        PlayerControl target = Utils.GetPlayerById(MarkedTargetId);
        if (target == null || !target.IsAlive())
        {
            MarkedTargetId = byte.MaxValue;
            return;
        }

        // Execute the remote kill
        MarkedTargetId = byte.MaxValue;
        RPC.PlaySoundRPC(pc.PlayerId, Sounds.KillSound);

        target.RpcExileV2();
        PlayerState state = Main.PlayerStates[target.PlayerId];
        state.deathReason = PlayerState.DeathReason.Kill;
        state.RealKiller = (DateTime.Now, pc.PlayerId);
        state.SetDead();
        Utils.AfterPlayerDeathTasks(target);

        pc.SetKillCooldown();
        Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc);
        Logger.Info($"Remotekiller {pc.GetNameWithRole().RemoveHtmlTags()} killed {target.GetNameWithRole().RemoveHtmlTags()} remotely", "Remotekiller");
    }

    public override void OnReportDeadBody()
    {
        MarkedTargetId = byte.MaxValue;
    }

    public override string GetProgressText(byte playerId, bool comms)
    {
        if (playerId != RemotekillerID) return string.Empty;
        if (MarkedTargetId == byte.MaxValue) return string.Empty;
        PlayerControl target = Utils.GetPlayerById(MarkedTargetId);
        if (target == null) return string.Empty;
        return Utils.ColorString(Utils.GetRoleColor(CustomRoles.Remotekiller), $"[{target.GetRealName()}]");
    }

    public override void SetButtonTexts(HudManager hud, byte id)
    {
        hud.KillButton?.OverrideText(GetString("Remotekiller.MarkButtonText"));
        if (MarkedTargetId != byte.MaxValue)
            hud.ImpostorVentButton?.OverrideText(GetString("Remotekiller.VentButtonText"));
    }
}
