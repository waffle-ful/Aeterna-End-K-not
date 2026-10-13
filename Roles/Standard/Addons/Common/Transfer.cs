using System.Collections.Generic;
using EndKnot.Modules;

namespace EndKnot.Roles;

public class Transfer : IAddon
{
    public AddonTypes Type => AddonTypes.Mixed;

    private static Dictionary<byte, long> LastTP;
    private static HashSet<byte> WaitingToLeave;
    private static Vector2[] VentLocations;

    public void SetupCustomOption()
    {
        Options.SetupAdtRoleOptions(708600, CustomRoles.Transfer, canSetNum: true, teamSpawnOptions: true);
    }

    public static void Init()
    {
        LastTP = null;
        WaitingToLeave = null;
        VentLocations = null;
        LateTask.New(CacheVentLocations, 25f, "Transfer Cache Vents");
    }

    private static void CacheVentLocations()
    {
        if (!ShipStatus.Instance || !CustomRoles.Transfer.RoleExist()) return;

        var vents = ShipStatus.Instance.AllVents;
        var locations = new Vector2[vents.Length];

        for (var i = 0; i < vents.Length; i++)
        {
            var pos = vents[i].transform.position;
            locations[i] = new Vector2(pos.x, pos.y + 0.3636f);
        }

        VentLocations = locations;
    }

    public static void OnCheckPlayerPosition(PlayerControl pc)
    {
        if (VentLocations == null || VentLocations.Length < 2 || !pc.Is(CustomRoles.Transfer) || !PerSecondUpdateScheduler.ShouldRunUpdate(pc.PlayerId)) return;

        LastTP ??= [];
        WaitingToLeave ??= [];

        bool onVent = FastVector2.TryGetClosestInRange(pc.Pos(), VentLocations, 0.8f, out Vector2 closest);

        // The landing spot is itself a vent, so the player must step off it before the next transfer;
        // otherwise standing still would bounce them between vents forever.
        if (!onVent)
        {
            WaitingToLeave.Remove(pc.PlayerId);
            return;
        }

        if (WaitingToLeave.Contains(pc.PlayerId)) return;

        long now = Utils.TimeStamp;
        if (LastTP.TryGetValue(pc.PlayerId, out long ts) && ts + 3 >= now) return;

        // A real-position SnapTo would reveal an invisible player to everyone while Main.Invisible still lists them.
        if (Main.Invisible.Contains(pc.PlayerId)) return;

        int index = IRandom.Instance.Next(VentLocations.Length - 1);
        Vector2 destination = VentLocations[index];
        if (destination == closest) destination = VentLocations[^1];

        // Utils.TP with state checks on: no TP while in a vent, on a ladder or moving platform, or with AntiTP.
        if (!Utils.TP(pc.NetTransform, destination)) return;

        LastTP[pc.PlayerId] = now;
        WaitingToLeave.Add(pc.PlayerId);
    }
}
