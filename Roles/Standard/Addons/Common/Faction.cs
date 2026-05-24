using System.Linq;

namespace EndKnot.Roles;

internal class Faction : IAddon
{
    public AddonTypes Type => AddonTypes.Mixed;

    public void SetupCustomOption()
    {
        Options.SetupAdtRoleOptions(20460, CustomRoles.Faction, canSetNum: true, teamSpawnOptions: true);
    }

    public static bool AreAllies(PlayerControl seer, PlayerControl target)
    {
        return seer.Is(CustomRoles.Faction) && target.Is(CustomRoles.Faction);
    }

    public static void OnGameEnd()
    {
        if (!AmongUsClient.Instance.AmHost) return;
        if (CustomWinnerHolder.WinnerTeam is CustomWinner.Default or CustomWinner.None or CustomWinner.Draw or CustomWinner.Crewmate) return;

        var winnerIds = CustomWinnerHolder.WinnerIds;
        bool anyFactionWon = Main.AllPlayerControlsToList.Any(p => p.Is(CustomRoles.Faction) && winnerIds.Contains(p.PlayerId));

        if (!anyFactionWon) return;

        foreach (PlayerControl player in Main.AllPlayerControlsToList.Where(p => p.Is(CustomRoles.Faction)))
        {
            if (Main.LoversPlayers.Exists(x => x.PlayerId == player.PlayerId)) continue;
            CustomWinnerHolder.WinnerIds.Add(player.PlayerId);
        }

        CustomWinnerHolder.AdditionalWinnerTeams.Add(AdditionalWinners.Faction);
    }
}
