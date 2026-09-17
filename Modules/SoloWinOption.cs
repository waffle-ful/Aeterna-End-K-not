using System.Collections.Generic;

namespace EndKnot;

// A role's win priority setting. Higher values take over the win from whatever is currently
// set; equal values ride along as an additional winner (see CustomWinnerHolder.ResetAndSetAndChWinner).
public sealed class SoloWinOption
{
    public static readonly Dictionary<CustomRoles, SoloWinOption> AllData = [];

    public CustomRoles Role { get; }
    public int IdStart { get; }
    public OptionItem OptionWin { get; }

    private SoloWinOption(int idStart, TabGroup tab, CustomRoles role, int defo)
    {
        IdStart = idStart;
        Role = role;

        OptionWin = new IntegerOptionItem(idStart, "SoloWinOption", new IntegerValueRule(0, 50, 1), defo, tab);

        if (tab == TabGroup.GameSettings)
            OptionWin.SetGameMode(CustomGameMode.Standard);
        else
            OptionWin.SetParent(Options.CustomRoleSpawnChances[role]);

        OptionWin.AddReplacement(("%role%", role.GetCombinationName()));

        if (!AllData.TryAdd(role, this))
            Logger.Warn($"Duplicate SoloWinOption for {role}", "SoloWinOption");
    }

    public static SoloWinOption Create(int idStart, TabGroup tab, CustomRoles role, int defo = 0)
    {
        return new(idStart, tab, role, defo);
    }
}
