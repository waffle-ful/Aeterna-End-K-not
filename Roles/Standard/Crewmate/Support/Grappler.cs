using System.Collections.Generic;
using EndKnot.Modules;
using Hazel;

namespace EndKnot.Roles;

public class Grappler : RoleBase
{
    public static bool On;
    private static List<Grappler> Instances = [];

    private static OptionItem AbilityUseLimit;
    public static OptionItem AbilityUseGainWithEachTaskCompleted;
    public static OptionItem AbilityChargesWhenFinishedTasks;
    
    private byte GrapplerId;
    private bool InUse;

    public override bool IsEnable => On;

    public override void SetupCustomOption()
    {
        StartSetup(647250)
            .AutoSetupOption(ref AbilityUseLimit, 0f, new FloatValueRule(0, 20, 0.05f), OptionFormat.Times)
            .AutoSetupOption(ref AbilityUseGainWithEachTaskCompleted, 0.3f, new FloatValueRule(0f, 5f, 0.05f), OptionFormat.Times)
            .AutoSetupOption(ref AbilityChargesWhenFinishedTasks, 0.2f, new FloatValueRule(0f, 5f, 0.05f), OptionFormat.Times);
    }

    public override void Init()
    {
        On = false;
        Instances = [];
    }

    public override void Add(byte playerId)
    {
        On = true;
        Instances.Add(this);
        GrapplerId = playerId;
        InUse = false;
        playerId.SetAbilityUseLimit(AbilityUseLimit.GetFloat());
    }

    public override void Remove(byte playerId)
    {
        Instances.Remove(this);
    }

    public override void AfterMeetingTasks()
    {
        if (Main.PlayerStates[GrapplerId].IsDead) return;

        if (GrapplerId.GetAbilityUseLimit() >= 1f)
        {
            InUse = true;
            GrapplerId.GetPlayer()?.RpcRemoveAbilityUse(notify: false);
        }
        else
            InUse = false;

        Utils.SendRPC(CustomRPC.SyncRoleData, GrapplerId, InUse);
    }

    public override string GetProgressText(byte playerId, bool comms)
    {
        return base.GetProgressText(playerId, comms) + (InUse ? "<#00ff00>\u271a</color>" : string.Empty);
    }

    public void ReceiveRPC(MessageReader reader)
    {
        InUse = reader.ReadBoolean();
    }

    public static bool OnAnyoneCheckMurder(PlayerControl target)
    {
        if (Utils.IsAnySabotageActive()) return true;

        foreach (Grappler instance in Instances)
        {
            PlayerControl grappler = instance.GrapplerId.GetPlayer();

            // Instances は役職変更でしか掃除されず、InUse は会議明けの再計算が死者を素通りするので
            // 死んだ本人の分が残る。生死を見ないと死体の座標へ的を引き寄せてキルだけ消える。
            if (instance.InUse && grappler != null && grappler.IsAliveWithConditions())
            {
                target.TP(grappler);
                instance.InUse = false;
                Utils.SendRPC(CustomRPC.SyncRoleData, instance.GrapplerId, instance.InUse);
                return false;
            }
        }

        return true;
    }
}