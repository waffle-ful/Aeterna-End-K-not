using System.Collections.Generic;
using AmongUs.GameOptions;
using EndKnot.Modules;
using Hazel;
using static EndKnot.Options;
using static EndKnot.Translator;

namespace EndKnot.Roles;

public class Remotekiller : RoleBase
{
    private const int Id = 704000;
    public static bool On;
    public static List<Remotekiller> Instances = [];

    private static OptionItem KillCooldown;
    private static OptionItem HasImpostorVision;
    private static OptionItem CanVent;
    private static OptionItem KillAnimation;

    private byte RemotekillerID = byte.MaxValue;
    public byte MarkedTargetId = byte.MaxValue;

    public override bool IsEnable => On;

    public override void SetupCustomOption()
    {
        StartSetup(Id)
            .AutoSetupOption(ref KillCooldown, 30, new IntegerValueRule(5, 180, 5), OptionFormat.Seconds)
            .AutoSetupOption(ref HasImpostorVision, true)
            .AutoSetupOption(ref CanVent, true)
            .AutoSetupOption(ref KillAnimation, true);
    }

    public override void Init()
    {
        On = false;
        Instances = [];
        RemotekillerID = byte.MaxValue;
        MarkedTargetId = byte.MaxValue;
    }

    public override void Add(byte playerId)
    {
        On = true;
        Instances.Add(this);
        RemotekillerID = playerId;
        MarkedTargetId = byte.MaxValue;
    }

    public override void Remove(byte playerId)
    {
        Instances.RemoveAll(x => x.RemotekillerID == playerId);
        if (Instances.Count == 0) On = false;
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
        // ターゲットがすでに死んでいるならマークは保持したまま何もしない (TOHK 原典準拠 — 再マークの手間を省く)
        if (!target || !target.IsAlive()) return;

        // Execute the remote kill
        MarkedTargetId = byte.MaxValue;

        // 抗えない (Lv3) の処刑。Pestilence だけは従来どおり素通し (反撃もさせない)。
        if (target.Is(CustomRoles.Pestilence) || !CheckMurderPatch.PassesGate(pc, target, kind: AttackKind.Execution)) return;

        if (KillAnimation.GetBool())
        {
            // TOHK 原典はベント入場自体を止めてテレポートキルへ差し替える。EHR の OnEnterVent は
            // void で入場を拒めないので、入った直後に外へ戻して見た目を揃える。
            pc.MyPhysics?.RpcExitVent(vent.Id);

            // テレポート演出込みのキルは 1.2 秒遅らせて、キラーがターゲットの位置へ実際に移動してから普通に殺す (死体が残り通報できる)
            LateTask.New(() =>
            {
                if (GameStates.IsMeeting || GameStates.IsEnded || ReportDeadBodyPatch.MeetingStarted) return;
                if (!pc || !pc.IsAlive() || !target || !target.IsAlive()) return;

                // 1.2 秒の間に相手が守りを得た / ベントに入った / 食べられた場合があるので、
                // 撃つ直前にもう一度見る (下の Kill は関所を通らない生のキル)。
                if (target.inVent || Pelican.IsEaten(target.PlayerId)) return;
                if (target.Is(CustomRoles.Pestilence) || !CheckMurderPatch.PassesGate(pc, target, kind: AttackKind.Execution)) return;

                // 公式鯖の SnapTo 予算を食うので 1 キルにつき 1 回の単発ホップに留める (Ninja のワープキルと同じ組み方)
                Vector2 targetPosition = target.Pos();

                // この 30+ マスの瞬間移動を不正移動検知に「歩いた」と誤判定させないための除外
                // (Ninja の暗殺テレポートと同じ処方: 直前位置を上書きし、判定と AFK 検知を一時的に外す)
                CheckInvalidMovementPatch.LastPosition[pc.PlayerId] = targetPosition;
                CheckInvalidMovementPatch.ExemptedPlayers.Add(pc.PlayerId);
                AFKDetector.TempIgnoredPlayers.Add(pc.PlayerId);
                LateTask.New(() => AFKDetector.TempIgnoredPlayers.Remove(pc.PlayerId), 0.2f + Utils.CalculatePingDelay(), log: false);

                pc.NetTransform.SnapTo(targetPosition, (ushort)(pc.NetTransform.lastSequenceId + 328));

                MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(pc.NetTransform.NetId, (byte)RpcCalls.SnapTo, SendOption.Reliable, pc.OwnerId);
                NetHelpers.WriteVector2(targetPosition, writer);
                writer.Write((ushort)(pc.NetTransform.lastSequenceId + 8));
                AmongUsClient.Instance.FinishRpcImmediately(writer);

                pc.Kill(target);

                RPC.PlaySoundRPC(pc.PlayerId, Sounds.KillSound);
                RPC.PlaySoundRPC(pc.PlayerId, Sounds.TaskComplete);

                pc.SetKillCooldown();
                Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc);
                Logger.Info($"Remotekiller {pc.GetNameWithRole().RemoveHtmlTags()} teleported to and killed {target.GetNameWithRole().RemoveHtmlTags()} remotely", "Remotekiller");
            }, 1.2f, log: false);

            return;
        }

        RPC.PlaySoundRPC(pc.PlayerId, Sounds.KillSound);
        RPC.PlaySoundRPC(pc.PlayerId, Sounds.TaskComplete);

        PlayerState state = Main.PlayerStates[target.PlayerId];
        target.SetRealKiller(pc);
        state.deathReason = PlayerState.DeathReason.Kill;
        target.RpcExileV2();
        target.Data.IsDead = true;
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
