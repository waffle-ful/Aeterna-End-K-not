using System.Linq;
using EndKnot.Roles;
using HarmonyLib;
using Hazel;

namespace EndKnot;

[HarmonyPatch(typeof(DoorsSystemType), nameof(DoorsSystemType.UpdateSystem))]
public static class DoorsSystemTypeUpdateSystemPatch
{
    private static bool DoorsProgressing;

    private static readonly int[][] PolusDoorGroups =
    [
        [71, 72],
        [67, 68],
        [64, 65, 66],
        [73, 74]
    ];

    private static readonly int[][] AirshipDoorGroups =
    [
        [64, 65, 66, 67],
        [71, 72, 73],
        [74, 75],
        [76, 77, 78],
        [68, 69, 70],
        [83, 84],
        [79, 80, 81, 82]
    ];

    public static void Prefix(DoorsSystemType __instance, [HarmonyArgument(0)] PlayerControl player, [HarmonyArgument(1)] MessageReader msgReader)
    {
        if (!AmongUsClient.Instance.AmHost) return;
        if (DoorsProgressing) return;
        if (!player.Is(CustomRoles.Opener)) return;

        byte amount;
        {
            MessageReader newReader = MessageReader.Get(msgReader);
            amount = newReader.ReadByte();
            newReader.Recycle();
        }

        ShipStatus shipStatus = ShipStatus.Instance;
        if (!shipStatus) return;

        DoorsProgressing = true;

        try
        {
            switch (Main.CurrentMap)
            {
                case MapNames.Polus:
                    OpenGroupContaining(shipStatus, amount, PolusDoorGroups);
                    break;
                case MapNames.Airship:
                    OpenGroupContaining(shipStatus, amount, AirshipDoorGroups);
                    break;
                case MapNames.Fungle:
                    OpenSameRoomDoors(shipStatus, amount);
                    break;
            }
        }
        finally
        {
            DoorsProgressing = false;
        }
    }

    private static void OpenGroupContaining(ShipStatus shipStatus, int amount, int[][] groups)
    {
        foreach (int[] group in groups)
        {
            if (!group.Contains(amount)) continue;

            foreach (int id in group)
                shipStatus.RpcUpdateSystem(SystemTypes.Doors, (byte)id);

            return;
        }
    }

    private static void OpenSameRoomDoors(ShipStatus shipStatus, int amount)
    {
        int openedDoorId = amount & DoorsSystemType.IdMask;
        var openedDoor = shipStatus.AllDoors.FirstOrDefault(door => door.Id == openedDoorId);
        if (openedDoor == null) return;

        SystemTypes room = openedDoor.Room;

        foreach (PlainDoor door in shipStatus.AllDoors)
        {
            if (door.Id == openedDoorId) continue;
            if (door.Room != room) continue;

            door.SetDoorway(true);
        }
    }
}
