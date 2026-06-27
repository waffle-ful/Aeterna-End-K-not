using System.Collections.Generic;
using static EndKnot.Options;

namespace EndKnot.Roles;

internal class Disco : IAddon
{
    private static readonly Dictionary<byte, long> LastChange = [];
    public AddonTypes Type => AddonTypes.Mixed;

    public void SetupCustomOption()
    {
        SetupAdtRoleOptions(652000, CustomRoles.Disco, canSetNum: true, teamSpawnOptions: true);

        DiscoChangeInterval = new IntegerOptionItem(652010, "DiscoChangeInterval", new(1, 90, 1), 5, TabGroup.Addons)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Disco])
            .SetValueFormat(OptionFormat.Seconds);
    }

    public static void ChangeColor(PlayerControl pc)
    {
        int colorId = IRandom.Instance.Next(0, 18);

        // 公式鯖では spoof RPC ではなく正規 serialize で色を同期 (anti-cheat 修正後)
        pc.RpcChangeColor((byte)colorId);
    }

    public static void OnFixedUpdate(PlayerControl pc)
    {
        if (!GameStates.IsInTask || ExileController.Instance || AntiBlackout.SkipTasks || pc.IsShifted() || Camouflage.IsCamouflage || pc.inVent || pc.MyPhysics.Animations.IsPlayingEnterVentAnimation() || pc.walkingToVent || pc.onLadder || pc.MyPhysics.Animations.IsPlayingAnyLadderAnimation() || pc.inMovingPlat) return;

        long now = Utils.TimeStamp;
        if (LastChange.TryGetValue(pc.PlayerId, out long change) && change + DiscoChangeInterval.GetInt() > now) return;

        ChangeColor(pc);
        LastChange[pc.PlayerId] = now;
    }
}
