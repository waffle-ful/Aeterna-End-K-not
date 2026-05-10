using System.Collections.Generic;
using System.Linq;
using static EndKnot.Options;

namespace EndKnot.Roles;

internal class Stack : IAddon
{
    private static readonly (string Name, CustomRoles Role)[] EligibleAddons =
    [
        ("Water", CustomRoles.Water),
        ("Notvoter", CustomRoles.Notvoter),
        ("Elector", CustomRoles.Elector),
        ("Transparent", CustomRoles.Transparent),
        ("Moon", CustomRoles.Moon),
        ("MagicHand", CustomRoles.MagicHand),
        ("Serial", CustomRoles.Serial),
        ("Opener", CustomRoles.Opener),
        ("NonReport", CustomRoles.NonReport),
        ("InfoPoor", CustomRoles.InfoPoor),
        ("News", CustomRoles.News),
        ("SlowStarter", CustomRoles.SlowStarter),
        ("Connecting", CustomRoles.Connecting)
    ];

    private static readonly Dictionary<CustomRoles, OptionItem> AssignOptions = [];

    public AddonTypes Type => AddonTypes.Mixed;

    public void SetupCustomOption()
    {
        SetupAdtRoleOptions(20500, CustomRoles.Stack, canSetNum: true, teamSpawnOptions: true);

        AssignOptions.Clear();
        int id = 20510;
        foreach ((string name, CustomRoles role) in EligibleAddons)
        {
            OptionItem opt = new BooleanOptionItem(id++, $"StackAssign.{name}", false, TabGroup.Addons)
                .SetParent(CustomRoleSpawnChances[CustomRoles.Stack]);
            AssignOptions[role] = opt;
        }
    }

    public static void Apply()
    {
        if (!AmongUsClient.Instance.AmHost) return;

        foreach (PlayerControl player in Main.AllPlayerControls.Where(p => p.Is(CustomRoles.Stack)).ToArray())
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
