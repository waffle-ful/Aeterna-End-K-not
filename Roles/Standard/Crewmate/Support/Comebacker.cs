using System.Collections.Generic;
using AmongUs.GameOptions;
using UnityEngine;
using static EndKnot.Translator;

namespace EndKnot.Roles;

public class Comebacker : RoleBase
{
    private const int Id = 701800;
    private static List<byte> PlayerIdList = [];

    private static OptionItem OptionCooldown;

    private byte ComebackerId;
    private Vector2? OldPosition;
    private int OldVentId;
    private string ComebackPosString;

    public override bool IsEnable => PlayerIdList.Count > 0;

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.CrewmateRoles, CustomRoles.Comebacker);

        OptionCooldown = new FloatOptionItem(Id + 10, "Cooldown", new(0f, 180f, 0.5f), 30f, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Comebacker])
            .SetValueFormat(OptionFormat.Seconds);
    }

    public override void Init()
    {
        PlayerIdList = [];
    }

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);
        ComebackerId = playerId;
        OldPosition = null;
        OldVentId = -1;
        ComebackPosString = string.Empty;
    }

    public override void Remove(byte playerId)
    {
        PlayerIdList.Remove(playerId);
    }

    public override void ApplyGameOptions(IGameOptions opt, byte playerId)
    {
        AURoleOptions.EngineerCooldown = OptionCooldown.GetFloat();
        AURoleOptions.EngineerInVentMaxTime = 1.5f;
    }

    public override void OnEnterVent(PlayerControl pc, Vent vent)
    {
        if (OldPosition.HasValue)
        {
            Vector2 tp = OldPosition.Value;
            int storedVentId = OldVentId;
            LateTask.New(() =>
            {
                pc.TP(tp + new Vector2(0f, 0.1f), log: false);
                if (pc.inVent) pc.MyPhysics?.RpcExitVent(storedVentId);
            }, 0.5f, "Comebacker.TP");
        }

        OldPosition = vent.transform.position;
        OldVentId = vent.Id;

        PlainShipRoom room = pc.GetPlainShipRoom();
        ComebackPosString = room != null ? GetString(room.RoomId.ToString()) : string.Empty;

        Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc);
    }

    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud = false, bool meeting = false)
    {
        if (seer.PlayerId != ComebackerId || seer.PlayerId != target.PlayerId) return string.Empty;
        if (meeting || !seer.IsAlive() || ComebackPosString == string.Empty) return string.Empty;
        return Utils.ColorString(Utils.GetRoleColor(CustomRoles.Comebacker), string.Format(GetString("ComebackerLowerText"), ComebackPosString));
    }

    public override void SetButtonTexts(HudManager hud, byte id)
    {
        hud.AbilityButton?.OverrideText(GetString("ComebackerAbility"));
    }
}
