using System;
using System.Collections.Generic;
using System.Linq;
using static EndKnot.Options;

namespace EndKnot.Roles;

internal class Twins : IAddon
{
    public static OptionItem AddWin;
    public static readonly Dictionary<byte, byte> Pairs = [];
    public AddonTypes Type => AddonTypes.Mixed;

    public void SetupCustomOption()
    {
        SetupAdtRoleOptions(20440, CustomRoles.Twins, canSetNum: true, teamSpawnOptions: true);

        AddWin = new BooleanOptionItem(20450, "TwinsAddWin", true, TabGroup.Addons)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Twins]);
    }

    public static void Init()
    {
        Pairs.Clear();

        if (!AmongUsClient.Instance.AmHost) return;

        List<PlayerControl> players = Main.AllPlayerControlsToList
            .Where(p => p.Is(CustomRoles.Twins))
            .OrderBy(_ => Guid.NewGuid())
            .ToList();

        for (int i = 0; i + 1 < players.Count; i += 2)
        {
            byte a = players[i].PlayerId;
            byte b = players[i + 1].PlayerId;
            Pairs[a] = b;
            Pairs[b] = a;
            Logger.Info($"Twins paired: {players[i].GetRealName()} <-> {players[i + 1].GetRealName()}", "Twins");
        }

        if (players.Count % 2 == 1)
        {
            PlayerControl unpaired = players[^1];
            Main.PlayerStates[unpaired.PlayerId].RemoveSubRole(CustomRoles.Twins);
            Logger.Info($"Twins unpaired (removed): {unpaired.GetRealName()}", "Twins");
        }
    }

    public static bool ArePartners(byte a, byte b)
    {
        return Pairs.TryGetValue(a, out byte partner) && partner == b;
    }
}
