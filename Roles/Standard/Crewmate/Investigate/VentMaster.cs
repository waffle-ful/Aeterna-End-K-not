using System.Collections.Generic;
using AmongUs.GameOptions;

namespace EndKnot.Roles;

public class VentMaster : RoleBase
{
    private const int Id = 701300;
    private static List<byte> PlayerIdList = [];

    public static OptionItem CanUseVentOption;

    public override bool IsEnable => PlayerIdList.Count > 0;

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.CrewmateRoles, CustomRoles.VentMaster);

        CanUseVentOption = new BooleanOptionItem(Id + 10, "CanVent", true, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.VentMaster]);
    }

    public override void Init()
    {
        PlayerIdList = [];
    }

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);
    }

    public override void Remove(byte playerId)
    {
        PlayerIdList.Remove(playerId);
    }

    public override void ApplyGameOptions(IGameOptions opt, byte playerId)
    {
        if (!CanUseVentOption.GetBool()) return;
        AURoleOptions.EngineerCooldown = 0f;
        AURoleOptions.EngineerInVentMaxTime = 0f;
    }

    public static void OnAnyoneEnterVent(PlayerControl pc)
    {
        if (!AmongUsClient.Instance.AmHost) return;
        if (!GameStates.IsInTask) return;

        foreach (PlayerControl vm in Main.AllAlivePlayerControls)
        {
            if (vm.PlayerId == pc.PlayerId) continue;
            if (!vm.Is(CustomRoles.VentMaster)) continue;
            vm.KillFlash();
        }
    }
}
