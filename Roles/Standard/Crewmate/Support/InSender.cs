using AmongUs.GameOptions;
using UnityEngine;
using static EndKnot.Options;

namespace EndKnot.Roles;

public class InSender : RoleBase
{
    private const int Id = 702800;

    public static bool On;
    public override bool IsEnable => On;

    private static OptionItem CanUseActiveCommsOpt;
    private static OptionItem ReportDelayOpt;
    private static OptionItem MaxDelayOpt;
    private static OptionItem TaskAwakeningOpt;
    private static OptionItem AwakeningTaskcountOpt;

    private bool Awakened;

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.CrewmateRoles, CustomRoles.InSender);

        CanUseActiveCommsOpt = new BooleanOptionItem(Id + 9, "InSenderCanUseActiveComms", true, TabGroup.CrewmateRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.InSender]);

        ReportDelayOpt = new FloatOptionItem(Id + 12, "InSenderReportDelay", new(0f, 180f, 0.5f), 3f, TabGroup.CrewmateRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.InSender])
            .SetValueFormat(OptionFormat.Seconds);

        MaxDelayOpt = new FloatOptionItem(Id + 13, "InSenderMaxDelay", new(0f, 180f, 0.5f), 3f, TabGroup.CrewmateRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.InSender])
            .SetValueFormat(OptionFormat.Seconds);

        TaskAwakeningOpt = new BooleanOptionItem(Id + 10, "InSenderTaskAwakening", false, TabGroup.CrewmateRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.InSender]);

        AwakeningTaskcountOpt = new IntegerOptionItem(Id + 14, "InSenderAwakeningTaskcount", new(1, 255, 1), 5, TabGroup.CrewmateRoles)
            .SetParent(TaskAwakeningOpt)
            .SetValueFormat(OptionFormat.Times);
    }

    public override void Init()
    {
        On = false;
    }

    public override void Add(byte playerId)
    {
        On = true;
        Awakened = !TaskAwakeningOpt.GetBool();
    }

    public override bool OnCheckMurderAsTarget(PlayerControl killer, PlayerControl target)
    {
        if (!Awakened) return true;
        if (!CanUseActiveCommsOpt.GetBool() && Utils.IsActive(SystemTypes.Comms)) return true;

        float extra = 0f;
        if (MaxDelayOpt.GetFloat() > 0)
            extra = IRandom.Instance.Next(0, (int)(MaxDelayOpt.GetFloat() * 10)) * 0.1f;

        float delay = Mathf.Max(0.15f, ReportDelayOpt.GetFloat() + extra);
        LateTask.New(() =>
        {
            if (GameStates.IsInTask) target.NoCheckStartMeeting(target.Data);
        }, delay, "InSender Self Report");

        return true;
    }

    public override void OnTaskComplete(PlayerControl pc, int completedTaskCount, int totalTaskCount)
    {
        if (TaskAwakeningOpt.GetBool() && !Awakened && completedTaskCount + 1 >= AwakeningTaskcountOpt.GetInt())
        {
            Awakened = true;
            Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc);
        }
    }
}
