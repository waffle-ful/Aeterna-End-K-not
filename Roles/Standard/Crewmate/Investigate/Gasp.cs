using static EndKnot.Options;

namespace EndKnot.Roles;

public class Gasp : RoleBase
{
    private const int Id = 702300;

    public static bool On;
    public override bool IsEnable => On;

    private static OptionItem TaskTriggerOpt;

    private bool CanSeeMark;
    private bool AfterAbility;
    private byte KillerPlayerId;

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.CrewmateRoles, CustomRoles.Gasp);

        TaskTriggerOpt = new IntegerOptionItem(Id + 10, "GaspTaskTrigger", new(0, 99, 1), 7, TabGroup.CrewmateRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Gasp])
            .SetValueFormat(OptionFormat.Pieces);

        OverrideTasksData.Create(Id + 20, TabGroup.CrewmateRoles, CustomRoles.Gasp);
    }

    public override void Init()
    {
        On = false;
    }

    public override void Add(byte playerId)
    {
        On = true;
        CanSeeMark = false;
        AfterAbility = false;
        KillerPlayerId = byte.MaxValue;
    }

    public override bool OnCheckMurderAsTarget(PlayerControl killer, PlayerControl target)
    {
        if (!AfterAbility)
        {
            if (target.GetTaskState().CompletedTasksCount >= TaskTriggerOpt.GetInt())
            {
                KillerPlayerId = killer.PlayerId;
                LateTask.New(() =>
                {
                    if (!GameStates.IsMeeting)
                    {
                        CanSeeMark = true;
                        Utils.NotifyRoles(ForceLoop: true);
                    }
                    else
                        AfterAbility = true;
                }, 0.1f, "GaspMark");
            }
            else
                AfterAbility = true;
        }
        return true;
    }

    public override void OnReportDeadBody()
    {
        if (CanSeeMark)
        {
            CanSeeMark = false;
            AfterAbility = true;
        }
    }

    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud = false, bool meeting = false)
    {
        if (!CanSeeMark || meeting || target.PlayerId != KillerPlayerId) return string.Empty;
        return "<color=#ab9d44>★</color>";
    }
}
