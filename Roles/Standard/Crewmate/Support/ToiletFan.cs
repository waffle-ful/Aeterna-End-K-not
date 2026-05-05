using System.Collections.Generic;

namespace EndKnot.Roles;

public class ToiletFan : RoleBase
{
    private const int Id = 701500;
    private static List<byte> PlayerIdList = [];

    public static OptionItem Cooldown;

    public override bool IsEnable => PlayerIdList.Count > 0;

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.CrewmateRoles, CustomRoles.ToiletFan);
        Cooldown = new FloatOptionItem(Id + 10, "Cooldown", new(1f, 30f, 1f), 5f, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.ToiletFan])
            .SetValueFormat(OptionFormat.Seconds);
    }

    public override void Init() => PlayerIdList = [];

    public override void Add(byte playerId) => PlayerIdList.Add(playerId);

    public override void Remove(byte playerId) => PlayerIdList.Remove(playerId);

    public override void OnPet(PlayerControl pc)
    {
        if (ShipStatus.Instance == null) return;
        if (!ShipStatus.Instance.Systems.ContainsKey(SystemTypes.Doors)) return;
        ShipStatus.Instance.RpcUpdateSystem(SystemTypes.Doors, 79);
        ShipStatus.Instance.RpcUpdateSystem(SystemTypes.Doors, 80);
        ShipStatus.Instance.RpcUpdateSystem(SystemTypes.Doors, 81);
        ShipStatus.Instance.RpcUpdateSystem(SystemTypes.Doors, 82);
    }

    public override void SetButtonTexts(HudManager hud, byte id)
    {
        hud.PetButton?.OverrideText(Translator.GetString("ToiletFanAbility"));
    }
}
