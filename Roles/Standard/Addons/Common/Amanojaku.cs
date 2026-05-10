using EndKnot.Modules;
using static EndKnot.Options;

namespace EndKnot.Roles;

internal class Amanojaku : IAddon
{
    public static OptionItem ActivationDay;
    public static OptionItem MustSurvive;
    public static OptionItem WinIfImp;
    public static OptionItem WinIfNeutral;
    public static OptionItem WinIfCoven;
    public AddonTypes Type => AddonTypes.Mixed;

    public void SetupCustomOption()
    {
        SetupAdtRoleOptions(20000, CustomRoles.Amanojaku, canSetNum: true, teamSpawnOptions: true);

        ActivationDay = new IntegerOptionItem(20010, "AmanojakuActivationDay", new(0, 30, 1), 4, TabGroup.Addons)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Amanojaku]);

        MustSurvive = new BooleanOptionItem(20011, "AmanojakuMustSurvive", true, TabGroup.Addons)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Amanojaku]);

        WinIfImp = new BooleanOptionItem(20012, "AmanojakuWinIfImpostor", true, TabGroup.Addons)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Amanojaku]);

        WinIfNeutral = new BooleanOptionItem(20013, "AmanojakuWinIfNeutral", true, TabGroup.Addons)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Amanojaku]);

        WinIfCoven = new BooleanOptionItem(20014, "AmanojakuWinIfCoven", true, TabGroup.Addons)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Amanojaku]);
    }

    public static bool ShouldWin(PlayerControl pc, GameOverReason reason)
    {
        if (!pc.Is(CustomRoles.Amanojaku)) return false;
        if (MeetingStates.MeetingNum < ActivationDay.GetInt()) return false;
        if (MustSurvive.GetBool() && !pc.IsAlive()) return false;
        if (reason is GameOverReason.CrewmatesByTask or GameOverReason.CrewmatesByVote) return false;

        var winner = CustomWinnerHolder.WinnerTeam;

        return winner switch
        {
            CustomWinner.Impostor => WinIfImp.GetBool(),
            CustomWinner.Coven => WinIfCoven.GetBool(),
            _ when winner is not CustomWinner.Crewmate
                and not CustomWinner.None
                and not CustomWinner.Draw
                and not CustomWinner.Error
                and not CustomWinner.Default => WinIfNeutral.GetBool(),
            _ => false
        };
    }
}
