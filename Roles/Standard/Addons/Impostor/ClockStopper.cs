using System;
using System.Linq;

namespace EndKnot.Roles;

// キルするたびに、会議のあとの緊急ボタンの待ち時間が伸びる
internal class ClockStopper : IAddon
{
    private static OptionItem EmergencyCooldownIncreasePerKill;
    private static OptionItem MaxEmergencyCooldown;
    private static OptionItem CountAddedTimeAfterDeath;

    public AddonTypes Type => AddonTypes.ImpOnly;

    public void SetupCustomOption()
    {
        Options.SetupAdtRoleOptions(710500, CustomRoles.ClockStopper, canSetNum: true, tab: TabGroup.Addons);

        EmergencyCooldownIncreasePerKill = new IntegerOptionItem(710510, "ClockStopperIncreasePerKill", new(1, 30, 1), 5, TabGroup.Addons)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.ClockStopper])
            .SetValueFormat(OptionFormat.Seconds);

        MaxEmergencyCooldown = new IntegerOptionItem(710511, "ClockStopperMaxEmergencyCooldown", new(5, 300, 5), 90, TabGroup.Addons)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.ClockStopper])
            .SetValueFormat(OptionFormat.Seconds);

        CountAddedTimeAfterDeath = new BooleanOptionItem(710512, "ClockStopperCountAfterDeath", false, TabGroup.Addons)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.ClockStopper]);
    }

    public static bool IsActive => CustomRoles.ClockStopper.RoleExist(CountAddedTimeAfterDeath.GetBool());

    public static int GetTotalTime(int originalTime)
    {
        int maxTime = MaxEmergencyCooldown.GetInt();
        if (originalTime >= maxTime) return originalTime;

        bool countDead = CountAddedTimeAfterDeath.GetBool();
        int added = Main.PlayerStates.Values
            .Where(s => s.SubRoles.Contains(CustomRoles.ClockStopper) && (!s.IsDead || countDead))
            .Sum(s => EmergencyCooldownIncreasePerKill.GetInt() * s.GetKillCount());

        return Math.Min(maxTime, originalTime + added);
    }
}
