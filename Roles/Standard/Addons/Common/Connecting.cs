using System.Collections.Generic;
using System.Linq;

namespace EndKnot.Roles;

internal class Connecting : IAddon
{
    public AddonTypes Type => AddonTypes.Helpful;

    public void SetupCustomOption()
    {
        Options.SetupAdtRoleOptions(20020, CustomRoles.Connecting, canSetNum: true, teamSpawnOptions: true);
    }

    public static void Init()
    {
        if (!AmongUsClient.Instance.AmHost) return;

        List<PlayerControl> holders = Main.AllPlayerControlsToList
            .Where(p => p.Is(CustomRoles.Connecting))
            .ToList();

        // Connecting is a mutual-reveal group; a lone holder has no effect at all (Twins-style cleanup)
        if (holders.Count == 1)
        {
            PlayerControl lone = holders[0];
            Main.PlayerStates[lone.PlayerId].RemoveSubRole(CustomRoles.Connecting);
            Logger.Info($"Connecting removed (lone holder): {lone.GetRealName()}", "Connecting");
        }
    }
}
