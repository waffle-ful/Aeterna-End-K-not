namespace EndKnot.Roles;

public class Nightmare : RoleBase
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

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(642630, TabGroup.CrewmateRoles, CustomRoles.Nightmare);
    }

    /// <summary>
    ///     停電中だけ
    /// </summary>
    public override int? GetDefensePower(PlayerControl target, AttackKind kind)
    {
        return MurderOnly(kind, Utils.IsActive(SystemTypes.Electrical) ? (int?)AttackDefense.Powerful : null);
    }

    public override bool OnCheckMurderAsTarget(PlayerControl killer, PlayerControl target, bool check = false)
    {
        return !Utils.IsActive(SystemTypes.Electrical);
    }
}