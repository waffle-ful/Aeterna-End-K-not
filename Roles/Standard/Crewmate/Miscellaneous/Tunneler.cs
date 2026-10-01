using System.Collections.Generic;
using EndKnot.Modules;
using Hazel;

namespace EndKnot.Roles;

internal class Tunneler : RoleBase
{
    public static Dictionary<byte, Vector2> TunnelerPositions = [];
    public static bool On;
    public override bool IsEnable => On;

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(5592, TabGroup.CrewmateRoles, CustomRoles.Tunneler);
    }

    public override void Add(byte playerId)
    {
        On = true;
    }

    public override void Init()
    {
        On = false;
        TunnelerPositions = [];
    }

    public override string GetProgressText(byte playerId, bool comms)
    {
        var ProgressText = new StringBuilder();

        ProgressText.Append(base.GetProgressText(playerId, comms));
        if (TunnelerPositions.ContainsKey(playerId)) ProgressText.Append('●');

        return ProgressText.ToString();
    }

    public override void OnPet(PlayerControl pc)
    {
        if (TunnelerPositions.TryGetValue(pc.PlayerId, out Vector2 ps))
        {
            Vector2 from = pc.Pos();

            if (pc.TP(ps))
            {
                EndKnot.Modules.ExplosionFx.Play(EndKnot.Modules.ExplosionFx.Kind.BurrowIn, from, 1f);
                EndKnot.Modules.ExplosionFx.Play(EndKnot.Modules.ExplosionFx.Kind.BurrowOut, ps, 1f);
            }

            TunnelerPositions.Remove(pc.PlayerId);
            Utils.SendRPC(CustomRPC.SyncRoleData, pc.PlayerId, pc.PlayerId, false);
        }
        else
        {
            TunnelerPositions[pc.PlayerId] = pc.Pos();
            Utils.SendRPC(CustomRPC.SyncRoleData, pc.PlayerId, pc.PlayerId, true);
        }
    }

    // 進行表示の ● は各クライアントが自分の TunnelerPositions で出すので、位置を刻んだかどうかをモッド客へ送る (座標は表示に使わない)
    public void ReceiveRPC(MessageReader reader)
    {
        byte id = reader.ReadByte();

        if (reader.ReadBoolean()) TunnelerPositions[id] = Vector2.zero;
        else TunnelerPositions.Remove(id);
    }
}