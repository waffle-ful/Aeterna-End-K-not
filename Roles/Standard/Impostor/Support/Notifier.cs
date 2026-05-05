using System.Collections.Generic;
using AmongUs.GameOptions;

namespace EndKnot.Roles;

public class Notifier : RoleBase
{
    private const int Id = 699000;
    public static List<byte> PlayerIdList = [];

    private static OptionItem KillCooldown;
    private static OptionItem NotifierProbability;

    public override bool IsEnable => PlayerIdList.Count > 0;

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.ImpostorRoles, CustomRoles.Notifier);

        KillCooldown = new FloatOptionItem(Id + 10, "KillCooldown", new(0f, 180f, 0.5f), 25f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Notifier])
            .SetValueFormat(OptionFormat.Seconds);

        NotifierProbability = new IntegerOptionItem(Id + 11, "NotifierProbability", new(0, 100, 5), 50, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Notifier])
            .SetValueFormat(OptionFormat.Percent);
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

    public override void SetKillCooldown(byte id)
    {
        Main.AllPlayerKillCooldown[id] = KillCooldown.GetFloat();
    }

    public override void OnMurder(PlayerControl killer, PlayerControl target)
    {
        if (IRandom.Instance.Next(1, 101) <= NotifierProbability.GetInt())
        {
            foreach (PlayerControl seer in Main.EnumeratePlayerControls())
                seer.KillFlash();
        }
    }
}
