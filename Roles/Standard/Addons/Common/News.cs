using System.Collections.Generic;
using System.Linq;
using EndKnot.Modules;
using static EndKnot.Options;

namespace EndKnot.Roles;

internal class News : IAddon
{
    public static OptionItem TriggerDay;
    public static OptionItem TriggerEveryDayAfter;
    public static OptionItem TriggerAliveCount;
    public static OptionItem TriggerEveryAliveCountBelow;
    public AddonTypes Type => AddonTypes.Harmful;

    public void SetupCustomOption()
    {
        SetupAdtRoleOptions(20160, CustomRoles.News, canSetNum: true, teamSpawnOptions: true);

        TriggerDay = new IntegerOptionItem(20170, "NewsTriggerDay", new(0, 30, 1), 4, TabGroup.Addons)
            .SetParent(CustomRoleSpawnChances[CustomRoles.News]);

        TriggerEveryDayAfter = new BooleanOptionItem(20171, "NewsTriggerEveryDayAfter", false, TabGroup.Addons)
            .SetParent(TriggerDay);

        TriggerAliveCount = new IntegerOptionItem(20172, "NewsTriggerAliveCount", new(0, 15, 1), 6, TabGroup.Addons)
            .SetParent(CustomRoleSpawnChances[CustomRoles.News]);

        TriggerEveryAliveCountBelow = new BooleanOptionItem(20173, "NewsTriggerEveryAliveCountBelow", false, TabGroup.Addons)
            .SetParent(TriggerAliveCount);
    }

    public static string BuildBroadcast()
    {
        int day = MeetingStates.MeetingNum;
        int aliveCount = Main.AllAlivePlayerControlsToList.Count;
        int triggerDay = TriggerDay.GetInt();
        int triggerAlive = TriggerAliveCount.GetInt();

        bool dayTriggered = triggerDay > 0 && (TriggerEveryDayAfter.GetBool() ? day >= triggerDay : day == triggerDay);
        bool aliveTriggered = triggerAlive > 0 && (TriggerEveryAliveCountBelow.GetBool() ? aliveCount <= triggerAlive : aliveCount == triggerAlive);

        if (!dayTriggered && !aliveTriggered) return string.Empty;

        List<string> roleStrings = Main.EnumerateAlivePlayerControls()
            .Where(p => p.Is(CustomRoles.News))
            .Select(p => p.GetCustomRole().ToColoredString())
            .ToList();

        if (roleStrings.Count == 0) return string.Empty;

        int variant = IRandom.Instance.Next(3);
        return string.Format(Translator.GetString($"News.Broadcast.{variant}"), string.Join("、", roleStrings));
    }
}
