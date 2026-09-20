namespace EndKnot.Roles;

internal class Guardian : RoleBase
{
    public static bool On;
    public override bool IsEnable => On;

    public override void Add(byte playerId)
    {
        On = true;
    }

    public override void Init()
    {
        On = false;
    }

    /// <summary>
    ///     全タスク完了後は恒久
    /// </summary>
    public override int? GetDefensePower(PlayerControl target, AttackKind kind)
    {
        return MurderOnly(kind, target.AllTasksCompleted() ? (int?)AttackDefense.Unstoppable : null);
    }

    public override bool OnCheckMurderAsTarget(PlayerControl killer, PlayerControl target, bool check = false)
    {
        return !target.AllTasksCompleted();
    }

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(9200, TabGroup.CrewmateRoles, CustomRoles.Guardian);
        Options.OverrideTasksData.Create(9210, TabGroup.CrewmateRoles, CustomRoles.Guardian);
    }
}