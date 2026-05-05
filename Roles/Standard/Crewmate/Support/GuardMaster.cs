using AmongUs.GameOptions;
using EndKnot.Modules;
using UnityEngine;
using static EndKnot.Options;

namespace EndKnot.Roles;

public class GuardMaster : RoleBase
{
    private const int Id = 702600;

    public static bool On;
    public override bool IsEnable => On;

    private static OptionItem CanSeeProtectOpt;
    private static OptionItem AddGuardCountOpt;
    private static OptionItem TaskAwakeningOpt;
    private static OptionItem AwakeningTaskcountOpt;

    private byte GmId;
    private int Guard;
    private bool Awakened;

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.CrewmateRoles, CustomRoles.GuardMaster);

        CanSeeProtectOpt = new BooleanOptionItem(Id + 10, "MadGuardianCanSeeWhoTriedToKill", true, TabGroup.CrewmateRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.GuardMaster]);

        AddGuardCountOpt = new IntegerOptionItem(Id + 11, "GuardMasterAddGuardCount", new(1, 99, 1), 1, TabGroup.CrewmateRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.GuardMaster])
            .SetValueFormat(OptionFormat.Times);

        TaskAwakeningOpt = new BooleanOptionItem(Id + 12, "GuardMasterTaskAwakening", false, TabGroup.CrewmateRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.GuardMaster]);

        AwakeningTaskcountOpt = new IntegerOptionItem(Id + 13, "GuardMasterAwakeningTaskcount", new(1, 255, 1), 5, TabGroup.CrewmateRoles)
            .SetParent(TaskAwakeningOpt)
            .SetValueFormat(OptionFormat.Times);

        OverrideTasksData.Create(Id + 20, TabGroup.CrewmateRoles, CustomRoles.GuardMaster);
    }

    public override void Init()
    {
        On = false;
    }

    public override void Add(byte playerId)
    {
        On = true;
        GmId = playerId;
        Guard = 0;
        Awakened = !TaskAwakeningOpt.GetBool() || AwakeningTaskcountOpt.GetInt() < 1;
    }

    public override bool OnCheckMurderAsTarget(PlayerControl killer, PlayerControl target)
    {
        if (Guard <= 0) return true;

        Guard--;
        NameColorManager.Add(killer.PlayerId, target.PlayerId, "8FBC8B");
        if (CanSeeProtectOpt.GetBool() && Awakened)
            NameColorManager.Add(target.PlayerId, killer.PlayerId, "8FBC8B");

        Utils.NotifyRoles(SpecifySeer: killer);
        Utils.NotifyRoles(SpecifySeer: target);
        Logger.Info($"{target.GetNameWithRole().RemoveHtmlTags()}: Guard remaining: {Guard}", "GuardMaster");
        return false;
    }

    public override void OnTaskComplete(PlayerControl pc, int completedTaskCount, int totalTaskCount)
    {
        if (TaskAwakeningOpt.GetBool() && !Awakened && completedTaskCount + 1 >= AwakeningTaskcountOpt.GetInt())
        {
            Awakened = true;
            Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc);
        }

        if (pc.IsAlive())
            Guard += AddGuardCountOpt.GetInt();
    }

    public override string GetProgressText(byte playerId, bool comms)
    {
        if (playerId != GmId || !CanSeeProtectOpt.GetBool()) return string.Empty;
        return Utils.ColorString(Guard == 0 ? Color.gray : Utils.GetRoleColor(CustomRoles.GuardMaster), $"({Guard})");
    }
}
