using System.Linq;
using AmongUs.GameOptions;

namespace EndKnot.Roles;

internal class OneWolf : IAddon
{
    public AddonTypes Type => AddonTypes.ImpOnly;

    public void SetupCustomOption()
    {
        Options.SetupAdtRoleOptions(20420, CustomRoles.OneWolf, canSetNum: true, teamSpawnOptions: true);
    }

    public static void ApplyDesync()
    {
        if (!AmongUsClient.Instance.AmHost) return;

        var oneWolves = Main.AllPlayerControls
            .Where(p => p.Is(CustomRoles.OneWolf) && p.Is(CustomRoleTypes.Impostor))
            .ToList();

        if (oneWolves.Count == 0) return;

        var allImps = Main.AllPlayerControls
            .Where(p => p.Is(CustomRoleTypes.Impostor))
            .ToList();

        foreach (PlayerControl ow in oneWolves)
        {
            foreach (PlayerControl imp in allImps)
            {
                if (ow.PlayerId == imp.PlayerId) continue;

                RoleTypes owAppearsAs = ow.IsAlive() ? RoleTypes.Crewmate : RoleTypes.CrewmateGhost;
                RoleTypes impAppearsAs = imp.IsAlive() ? RoleTypes.Crewmate : RoleTypes.CrewmateGhost;

                ow.RpcSetRoleDesync(owAppearsAs, imp.OwnerId);
                imp.RpcSetRoleDesync(impAppearsAs, ow.OwnerId);
            }
        }

        Logger.Info($"OneWolf desync applied for {oneWolves.Count} player(s)", "OneWolf");
    }
}
