using System.Collections.Generic;
using AmongUs.GameOptions;
using EndKnot.Modules;
using EndKnot.Modules.Extensions;
using Hazel;

namespace EndKnot.Roles;

public class Entombed : IAddon
{
    public AddonTypes Type => AddonTypes.Harmful;

    public static Dictionary<byte, SystemTypes> BlockedRoom;
    private static long MeetingEndTS;
    private static float GracePeriodLength;

    public void SetupCustomOption()
    {
        Options.SetupAdtRoleOptions(658080, CustomRoles.Entombed, canSetNum: true, teamSpawnOptions: true);
    }

    public static void AfterMeeting()
    {
        foreach (PlayerControl pc in Main.CachedAllPlayerControls())
        {
            if (pc.Is(CustomRoles.Entombed))
            {
                BlockedRoom ??= [];
                BlockedRoom[pc.PlayerId] = ShipStatus.Instance.AllRooms.RandomElement().RoomId;
            }
        }
        
        if (BlockedRoom == null) return;
        
        MeetingEndTS = Utils.TimeStamp;
        GracePeriodLength = 5 + 5 / Main.RealOptionsData.GetFloat(FloatOptionNames.PlayerSpeedMod);

        // 計器 (BUG-20260715-07)
        foreach ((byte id, SystemTypes room) in BlockedRoom)
            Logger.Info($"Assigned {room} to {Utils.GetPlayerById(id)?.GetRealName() ?? id.ToString()} (grace {GracePeriodLength:F1}s from TS {MeetingEndTS})", "Entombed");

        if (Utils.DoRPC)
        {
            MessageWriter writer = Utils.CreateRPC(CustomRPC.Entombed);
            writer.Write(MeetingEndTS.ToString());
            writer.Write(GracePeriodLength);
            writer.Write(BlockedRoom.Count);

            foreach ((byte key, SystemTypes value) in BlockedRoom)
            {
                writer.Write(key);
                writer.Write((byte)value);
            }
            
            Utils.EndRPC(writer);
        }
    }

    public static void ReceiveRPC(MessageReader reader)
    {
        MeetingEndTS = long.Parse(reader.ReadString());
        GracePeriodLength = reader.ReadSingle();
        BlockedRoom = [];
        Loop.Times(reader.ReadInt32(), _ => BlockedRoom[reader.ReadByte()] = (SystemTypes)reader.ReadByte());
    }

    public static void OnFixedUpdate(PlayerControl pc)
    {
        if (BlockedRoom == null || !BlockedRoom.TryGetValue(pc.PlayerId, out SystemTypes blockedRoom)) return;

        long now = Utils.TimeStamp;
        
        if (now - MeetingEndTS <= GracePeriodLength)
        {
            Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc);
            return;
        }

        if (pc.IsInRoom(blockedRoom))
        {
            Logger.Info($"{pc.GetRealName()} entered blocked room {blockedRoom} - suicide", "Entombed");
            pc.Suicide();
            return;
        }

        LogNearMiss(pc, blockedRoom, now);
    }

    // 計器 (BUG-20260715-07): 「指定部屋に入ったのに自殺しない」の切り分け用。
    // 部屋の bounds には入っているのに IsInRoom() が false を返した = 「死ぬはずが死ななかった」瞬間だけを
    // 1 秒に 1 回記録する (OnFixedUpdate は毎 tick 呼ばれるため無throttleだとログが溢れる)。
    // IsTouching=false なら Check() の非矩形部屋パス (ExtendedPlayerControl.cs:2730) が犯人、
    // colliderOffset.y が 127 付近なら no-clip ([[project_noclip_collider_offset_shadow_loss]]) が犯人。
    private static readonly Dictionary<byte, long> LastNearMissLog = [];

    private static void LogNearMiss(PlayerControl pc, SystemTypes blockedRoom, long now)
    {
        PlainShipRoom room = blockedRoom.GetRoomClass();
        if (!room || !room.roomArea) return;

        Vector2 pos = pc.Pos();
        if (!room.roomArea.bounds.Contains2D(pos)) return;

        if (LastNearMissLog.TryGetValue(pc.PlayerId, out long last) && last == now) return;
        LastNearMissLog[pc.PlayerId] = now;

        Vector2 colliderOffset = pc.Collider ? pc.Collider.offset : Vector2.zero;
        bool touching = pc.Collider && pc.Collider.IsTouching(room.roomArea);
        Logger.Warn($"NEAR-MISS: {pc.GetRealName()} is inside {blockedRoom} bounds at ({pos.x:F2}, {pos.y:F2}) but IsInRoom()=false - colliderOffset=({colliderOffset.x:F2}, {colliderOffset.y:F2}) IsTouching={touching}", "Entombed");
    }

    public static string GetSelfSuffix(PlayerControl seer)
    {
        if (BlockedRoom == null || !BlockedRoom.TryGetValue(seer.PlayerId, out SystemTypes blockedRoom) || !seer.IsAlive()) return string.Empty;
        long elapsed = Utils.TimeStamp - MeetingEndTS;
        return string.Format(Translator.GetString(elapsed < GracePeriodLength ? "Entombed.SuffixGracePeriod" : "Entombed.SuffixActive"), Translator.GetString(blockedRoom.ToString()), GracePeriodLength - elapsed);
    }
}