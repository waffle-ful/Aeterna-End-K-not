using System.Collections.Generic;
using System.Linq;
using static EndKnot.Options;

namespace EndKnot.Roles;

internal class Stack : IAddon
{
    // オプション ID は保存値の鍵なので固定で持つ (欠番は廃止した属性の分)
    private static readonly (int Id, string Name, CustomRoles Role)[] EligibleAddons =
    [
        (20510, "Water", CustomRoles.Water),
        (20511, "Notvoter", CustomRoles.Notvoter),
        (20512, "Elector", CustomRoles.Elector),
        (20514, "Moon", CustomRoles.Moon),
        (20515, "MagicHand", CustomRoles.MagicHand),
        (20517, "Opener", CustomRoles.Opener),
        (20518, "NonReport", CustomRoles.NonReport),
        (20519, "InfoPoor", CustomRoles.InfoPoor)
    ];

    private static readonly Dictionary<CustomRoles, OptionItem> AssignOptions = [];

    public AddonTypes Type => AddonTypes.Mixed;

    public void SetupCustomOption()
    {
        SetupAdtRoleOptions(20500, CustomRoles.Stack, canSetNum: true, teamSpawnOptions: true);

        AssignOptions.Clear();
        foreach ((int id, string name, CustomRoles role) in EligibleAddons)
        {
            OptionItem opt = new BooleanOptionItem(id, $"StackAssign.{name}", false, TabGroup.Addons)
                .SetParent(CustomRoleSpawnChances[CustomRoles.Stack]);
            AssignOptions[role] = opt;
        }
    }

    public static void Apply()
    {
        if (!AmongUsClient.Instance.AmHost) return;

        foreach (PlayerControl player in Main.AllPlayerControlsToList.Where(p => p.Is(CustomRoles.Stack)).ToArray())
        {
            foreach ((CustomRoles role, OptionItem opt) in AssignOptions)
            {
                if (!opt.GetBool()) continue;
                if (player.Is(role)) continue;

                Main.PlayerStates[player.PlayerId].SetSubRole(role);
                Logger.Info($"Stack added {role} to {player.GetRealName()}", "Stack");
            }
        }
    }
}
