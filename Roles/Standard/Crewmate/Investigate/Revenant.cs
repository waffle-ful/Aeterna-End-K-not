using EndKnot.Modules;
using Hazel;

namespace EndKnot.Roles;

using static Options;
using static Utils;

internal class Revenant : RoleBase
{
    public static bool On;
    public static OptionItem KnowInfo;
    private static OptionItem RemainingTasksToBeFound;

    private static readonly string[] KnowInfoMode =
    [
        "Alignments",
        "Roles"
    ];

    public bool TaskDone;
    public bool StillAlive;
    private bool IsExposed;
    private byte RevenantId;

    public override bool IsEnable => On;

    public override void SetupCustomOption()
    {
        StartSetup(659500)
            .AutoSetupOption(ref KnowInfo, 1, KnowInfoMode)
            .AutoSetupOption(ref RemainingTasksToBeFound, 1, new IntegerValueRule(0, 10, 1), overrideName: "SnitchRemainingTaskFound")
            .CreateOverrideTasksData();
    }

    public override void Init()
    {
        On = false;
    }

    public override void Add(byte playerId)
    {
        On = true;
        TaskDone = false;
        StillAlive = false;
        IsExposed = false;
        RevenantId = playerId;
    }

    public override void OnReportDeadBody()
    {
        PlayerControl pc = RevenantId.GetPlayer();

        if (pc && pc.IsAlive() && !pc.AllTasksCompleted())
        {
            StillAlive = true;
            pc.RpcExiled();
            PlayerState state = Main.PlayerStates[RevenantId];
            state.deathReason = PlayerState.DeathReason.Suicide;
            state.SetDead();
            SendRPC(CustomRPC.SyncRoleData, RevenantId, TaskDone, StillAlive, IsExposed);
        }
    }

    public override void AfterMeetingTasks()
    {
        if (!StillAlive) return;

        LateTask.New(() =>
        {
            if (GameStates.IsEnded || !GameStates.IsInTask || ExileController.Instance || AntiBlackout.SkipTasks) return;

            PlayerControl pc = RevenantId.GetPlayer();

            if (pc)
            {
                pc.RpcRevive();
                pc.TPToRandomVent();
                StillAlive = false;
                SendRPC(CustomRPC.SyncRoleData, RevenantId, TaskDone, StillAlive, IsExposed);
            }
        }, 2f, "Revenant Revive Delay");
    }

    public override void OnTaskComplete(PlayerControl pc, int completedTaskCount, int totalTaskCount)
    {
        if (!pc.IsAlive() && !GameStates.IsMeeting) return;

        if (totalTaskCount - (completedTaskCount + 1) <= RemainingTasksToBeFound.GetInt() && !IsExposed)
        {
            foreach (PlayerControl target in Main.CachedAlivePlayerControls())
            {
                TargetArrow.Add(target.PlayerId, pc.PlayerId);
                NotifyRoles(SpecifySeer: target, SpecifyTarget: target);
            }
            IsExposed = true;
        }

        if (completedTaskCount + 1 >= totalTaskCount)
        {
            TaskDone = true;
            pc.Notify(Translator.GetString("RevenantDoneTasks"));
        }
        SendRPC(CustomRPC.SyncRoleData, RevenantId, TaskDone, StillAlive, IsExposed);
    }

    public void ReceiveRPC(MessageReader reader)
    {
        TaskDone = reader.ReadBoolean();
        StillAlive = reader.ReadBoolean();
        IsExposed = reader.ReadBoolean();
    }
}
