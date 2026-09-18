using System.Linq;
using EndKnot.Modules;
using static EndKnot.Options;

namespace EndKnot.Roles;

internal class SlowStarter : IAddon
{
    public static OptionItem AliveImpThreshold;
    public static OptionItem ActivationDay;
    public static bool DayReached;
    public AddonTypes Type => AddonTypes.ImpOnly;

    public void SetupCustomOption()
    {
        SetupAdtRoleOptions(20280, CustomRoles.SlowStarter, canSetNum: true, teamSpawnOptions: true, maxCount: 3);

        // インポスター限定アドオンなので、陣営トグルのうちインポスター以外は常に効かない (CheckAddonConflict の ImpOnly ゲートが先に落とす)
        (_, OptionItem neutral, OptionItem crew, OptionItem coven) = AddonCanBeSettings[CustomRoles.SlowStarter];
        neutral.SetHidden(true);
        crew.SetHidden(true);
        coven.SetHidden(true);

        AliveImpThreshold = new IntegerOptionItem(20290, "SlowStarterAliveImpThreshold", new(1, 3, 1), 2, TabGroup.Addons)
            .SetParent(CustomRoleSpawnChances[CustomRoles.SlowStarter]);

        ActivationDay = new IntegerOptionItem(20291, "SlowStarterActivationDay", new(0, 30, 1), 0, TabGroup.Addons)
            .SetParent(CustomRoleSpawnChances[CustomRoles.SlowStarter]);
    }

    public static void Init()
    {
        DayReached = false;
    }

    public static bool CanKill()
    {
        if (DayReached) return true;

        int aliveImps = Main.AllAlivePlayerControlsToList.Count(p => p.Is(CustomRoleTypes.Impostor));
        return aliveImps <= AliveImpThreshold.GetInt();
    }

    public static void OnMeetingStart()
    {
        int activationDay = ActivationDay.GetInt();
        if (activationDay > 0 && MeetingStates.MeetingNum >= activationDay)
            DayReached = true;
    }
}
