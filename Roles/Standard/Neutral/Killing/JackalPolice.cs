using System.Linq;
using AmongUs.GameOptions;
using EndKnot.Modules;
using EndKnot.Patches;
using Hazel;
using UnityEngine;
using static EndKnot.Translator;

namespace EndKnot.Roles;

// ジャッカルポリス — ジャッカル陣営のキラー。Deputy と同じ手錠 (遅れて相手のキルクールダウンを設定し直す) を
// ペット / ファントム / シェイプシフトのボタンで一番近い人にかける。キルボタンは普通のキル。
public class JackalPolice : RoleBase
{
    public static bool On;
    public override bool IsEnable => On;

    private static OptionItem KillCooldown;
    private static OptionItem CanVent;
    private static OptionItem CanSabotage;
    private static OptionItem ImpostorVision;
    public static OptionItem HandcuffCooldown;
    private static OptionItem HandcuffMax;
    private static OptionItem HandcuffCDForTarget;
    private static OptionItem HandcuffDelay;

    private static bool PhantomBasis => Options.UsePhantomBasis.GetBool() && Options.UsePhantomBasisForNKs.GetBool();

    public override void SetupCustomOption()
    {
        StartSetup(710000)
            .AutoSetupOption(ref KillCooldown, 22.5f, new FloatValueRule(0f, 180f, 0.5f), OptionFormat.Seconds)
            .AutoSetupOption(ref CanVent, true)
            .AutoSetupOption(ref CanSabotage, true)
            .AutoSetupOption(ref ImpostorVision, true)
            .AutoSetupOption(ref HandcuffCooldown, 17.5f, new FloatValueRule(0f, 60f, 0.5f), OptionFormat.Seconds, overrideName: "DeputyHandcuffCooldown")
            .AutoSetupOption(ref HandcuffMax, 3, new IntegerValueRule(1, 20, 1), OptionFormat.Times, overrideName: "DeputyHandcuffMax")
            .AutoSetupOption(ref HandcuffCDForTarget, 15f, new FloatValueRule(0f, 180f, 0.5f), OptionFormat.Seconds, overrideName: "DeputyHandcuffCDForTarget")
            .AutoSetupOption(ref HandcuffDelay, 5, new IntegerValueRule(0, 20, 1), OptionFormat.Seconds, overrideName: "DeputyHandcuffDelay");
    }

    public override void Init()
    {
        On = false;
    }

    public override void Add(byte playerId)
    {
        On = true;
        playerId.SetAbilityUseLimit(HandcuffMax.GetFloat());
    }

    public override void SetKillCooldown(byte id)
    {
        Main.AllPlayerKillCooldown[id] = KillCooldown.GetFloat();
    }

    public override bool CanUseImpostorVentButton(PlayerControl pc)
    {
        return CanVent.GetBool();
    }

    public override bool CanUseSabotage(PlayerControl pc)
    {
        return base.CanUseSabotage(pc) || (CanSabotage.GetBool() && pc.IsAlive());
    }

    public override void ApplyGameOptions(IGameOptions opt, byte id)
    {
        opt.SetVision(ImpostorVision.GetBool());

        float cd = HandcuffCooldown.GetFloat();
        if (IntroCutsceneDestroyPatch.PreventKill) cd = Mathf.Max(cd, (Options.StartingKillCooldown?.GetFloat() ?? 10f) + 2f);

        if (PhantomBasis)
        {
            AURoleOptions.PhantomCooldown = cd;
            AURoleOptions.PhantomDuration = 1f;
        }
        else if (!Options.UsePets.GetBool())
        {
            AURoleOptions.ShapeshifterCooldown = cd;
            AURoleOptions.ShapeshifterDuration = 1f;
        }
    }

    public override void SetButtonTexts(HudManager hud, byte id)
    {
        hud.SabotageButton?.ToggleVisible(CanSabotage.GetBool());

        if (Options.UsePets.GetBool() && !PhantomBasis)
            hud.PetButton?.OverrideText(GetString("DeputyHandcuffText"));
        else
            hud.AbilityButton?.OverrideText(GetString("DeputyHandcuffText"));
    }

    public override void OnPet(PlayerControl pc)
    {
        Handcuff(pc);
    }

    public override bool OnVanish(PlayerControl pc)
    {
        Handcuff(pc);
        return false;
    }

    public override bool OnShapeshift(PlayerControl shapeshifter, PlayerControl target, bool shapeshifting)
    {
        if (!shapeshifting) return true;
        Handcuff(shapeshifter);
        return false;
    }

    private static void Handcuff(PlayerControl police)
    {
        if (!police.IsAlive() || !GameStates.IsInTask || ExileController.Instance) return;
        if (police.GetAbilityUseLimit() < 1) return;

        Vector2 pos = police.Pos();
        PlayerControl target = FastVector2.GetPlayersInRange(pos, police.GetKillDistance(), p => p.PlayerId != police.PlayerId && p.IsAlive() && !p.GetCustomRole().IsJackalTeamMember())
            .OrderBy(p => Vector2.Distance(pos, p.Pos()))
            .FirstOrDefault();

        if (!target)
        {
            police.Notify(CustomRoles.JackalPolice.ColoredTextByRole(GetString("JackalPoliceNoTarget")));
            return;
        }

        police.RpcRemoveAbilityUse(notify: false);
        police.Notify(CustomRoles.JackalPolice.ColoredTextByRole(GetString("DeputyHandcuffedPlayer")));

        float cuffSeconds = HandcuffCDForTarget.GetFloat();
        byte policeId = police.PlayerId;

        LateTask.New(() =>
        {
            if (!GameStates.IsInTask || !target || !target.IsAlive()) return;

            var sender = CustomRpcSender.Create("JackalPolice.Handcuff", SendOption.Reliable);
            var hasValue = false;
            hasValue |= sender.SetKillCooldown(target, cuffSeconds);
            hasValue |= sender.Notify(target, CustomRoles.JackalPolice.ColoredTextByRole(GetString("HandcuffedByDeputy")), out sender);

            if (target.IsModdedClient()) hasValue |= sender.RpcResetAbilityCooldown(target);
            else hasValue |= sender.RpcGuardAndKill(target, target);

            sender.SendMessage(!hasValue);

            ExplosionFx.Handcuffed(target, cuffSeconds, policeId.GetPlayer());
        }, HandcuffDelay.GetInt(), "JackalPoliceHandcuffDelay");
    }
}
