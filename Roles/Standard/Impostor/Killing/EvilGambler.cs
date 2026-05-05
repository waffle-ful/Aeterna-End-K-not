using System.Collections.Generic;

namespace EndKnot.Roles;

public class EvilGambler : RoleBase
{
    private const int Id = 699700;
    public static List<byte> PlayerIdList = [];

    private static OptionItem GambleCollect;
    private static OptionItem CollectkillCooldown;
    private static OptionItem NotcollectkillCooldown;

    private float NowCooldown;

    public override bool IsEnable => PlayerIdList.Count > 0;

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.ImpostorRoles, CustomRoles.EvilGambler);

        GambleCollect = new IntegerOptionItem(Id + 10, "EvilGamblerGambleCollect", new(0, 100, 5), 50, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.EvilGambler])
            .SetValueFormat(OptionFormat.Percent);

        CollectkillCooldown = new FloatOptionItem(Id + 11, "EvilGamblerCollectkillCooldown", new(0f, 180f, 0.5f), 2.5f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.EvilGambler])
            .SetValueFormat(OptionFormat.Seconds);

        NotcollectkillCooldown = new FloatOptionItem(Id + 12, "EvilGamblerNotcollectkillCooldown", new(0f, 180f, 0.5f), 50f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.EvilGambler])
            .SetValueFormat(OptionFormat.Seconds);
    }

    public override void Init()
    {
        PlayerIdList = [];
        NowCooldown = 30f;
    }

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);
        NowCooldown = NotcollectkillCooldown.GetFloat();
    }

    public override void Remove(byte playerId)
    {
        PlayerIdList.Remove(playerId);
    }

    public override void SetKillCooldown(byte id)
    {
        Main.AllPlayerKillCooldown[id] = NowCooldown;
    }

    public override void OnMurder(PlayerControl killer, PlayerControl target)
    {
        NowCooldown = IRandom.Instance.Next(1, 101) <= GambleCollect.GetInt()
            ? CollectkillCooldown.GetFloat()
            : NotcollectkillCooldown.GetFloat();

        Main.AllPlayerKillCooldown[killer.PlayerId] = NowCooldown;
        killer.SyncSettings();
    }

    public override void AfterMeetingTasks()
    {
        NowCooldown = NotcollectkillCooldown.GetFloat();
    }
}
