namespace EndKnot.Roles;

internal class Luckey : RoleBase
{
    public static bool On;
    public override bool IsEnable => On;

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(5800, TabGroup.CrewmateRoles, CustomRoles.Luckey);

        Options.LuckeyProbability = new IntegerOptionItem(5900, "LuckeyProbability", new(0, 100, 5), 50, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Luckey])
            .SetValueFormat(OptionFormat.Percent);
    }

    public override void Add(byte playerId)
    {
        On = true;
    }

    public override void Init()
    {
        On = false;
    }

    public override bool OnCheckMurderAsTarget(PlayerControl killer, PlayerControl target, bool check = false)
    {
        // 打診では乱数を振らない。照準表示から毎フレーム振り直すと、確率で防ぐ能力が実質0%に溶ける。
        if (check) return true;

        var rd = IRandom.Instance;

        if (rd.Next(0, 100) < Options.LuckeyProbability.GetInt())
        {
            killer.SetKillCooldown(15f);
            return false;
        }

        return true;
    }
}