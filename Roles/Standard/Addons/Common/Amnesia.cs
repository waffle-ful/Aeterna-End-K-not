using System.Collections.Generic;
using System.Linq;
using static EndKnot.Options;

namespace EndKnot.Roles;

internal class Amnesia : IAddon
{
    private const int Id = 701700;

    public static OptionItem OptionDontCanUseAbility;
    public static OptionItem OptionCanRealizeDay;
    public static OptionItem OptionRealizeDayCount;
    public static OptionItem OptionCanRealizeTask;
    public static OptionItem OptionRealizeTaskCount;

    private static List<byte> PlayerIdList = [];
    private static bool DontCanUseAbility;

    private static bool IsEnable => PlayerIdList.Count > 0;

    public AddonTypes Type => AddonTypes.Harmful;

    public void SetupCustomOption()
    {
        SetupAdtRoleOptions(Id, CustomRoles.Amnesia, canSetNum: true, teamSpawnOptions: true);
        OptionDontCanUseAbility = new BooleanOptionItem(Id + 10, "AmnesiaDontCanUseAbility", true, TabGroup.Addons)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Amnesia]);
        OptionCanRealizeDay = new BooleanOptionItem(Id + 20, "AmnesiaCanRealizeDay", false, TabGroup.Addons)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Amnesia]);
        OptionRealizeDayCount = new IntegerOptionItem(Id + 21, "AmnesiaRealizeDayCount", new(1, 99, 1), 4, TabGroup.Addons)
            .SetParent(OptionCanRealizeDay)
            .SetValueFormat(OptionFormat.Times);
        OptionCanRealizeTask = new BooleanOptionItem(Id + 30, "AmnesiaCanRealizeTask", false, TabGroup.Addons)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Amnesia]);
        OptionRealizeTaskCount = new IntegerOptionItem(Id + 31, "AmnesiaRealizeTaskCount", new(1, 255, 1), 4, TabGroup.Addons)
            .SetParent(OptionCanRealizeTask)
            .SetValueFormat(OptionFormat.Times);
    }

    public static void Init()
    {
        PlayerIdList = [];
        DontCanUseAbility = OptionDontCanUseAbility?.GetBool() ?? true;

        LateTask.New(() =>
        {
            foreach (PlayerControl pc in Main.AllAlivePlayerControlsToList)
            {
                if (pc.Is(CustomRoles.Amnesia))
                    PlayerIdList.Add(pc.PlayerId);
            }
        }, 20f, "Amnesia.Init");
    }

    public static bool IsAbilityBlocked(PlayerControl pc)
        => pc != null && DontCanUseAbility && PlayerIdList.Contains(pc.PlayerId);

    private static void RemoveAmnesia(byte playerId)
    {
        if (!PlayerIdList.Remove(playerId)) return;
        if (!Main.PlayerStates.TryGetValue(playerId, out PlayerState state)) return;
        state.RemoveSubRole(CustomRoles.Amnesia);
        PlayerControl pc = Utils.GetPlayerById(playerId);
        if (pc != null) Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc);
    }

    public static void OnTaskComplete(PlayerControl pc)
    {
        if (!IsEnable) return;
        if (!OptionCanRealizeTask.GetBool()) return;
        if (!PlayerIdList.Contains(pc.PlayerId)) return;
        if (pc.GetTaskState().CompletedTasksCount < OptionRealizeTaskCount.GetInt()) return;
        RemoveAmnesia(pc.PlayerId);
    }

    public static void AfterMeetingTasks()
    {
        if (!IsEnable) return;
        if (!OptionCanRealizeDay.GetBool()) return;
        if (MeetingStates.MeetingNum < OptionRealizeDayCount.GetInt()) return;
        foreach (byte id in PlayerIdList.ToList())
            RemoveAmnesia(id);
    }
}
