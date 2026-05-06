using Hazel;

namespace EndKnot.Roles;

internal class SchrodingersCat : RoleBase
{
    public static bool On;

    public static OptionItem WinsWithCrewIfNotAttacked;
    public static OptionItem StealsExactImpostorRole;
    public static OptionItem KillBackKiller;
    public static OptionItem KillBackDelay;
    public override bool IsEnable => On;

    public override void SetupCustomOption()
    {
        const int id = 13840;
        Options.SetupRoleOptions(id, TabGroup.NeutralRoles, CustomRoles.SchrodingersCat);

        WinsWithCrewIfNotAttacked = new BooleanOptionItem(id + 2, "SchrodingersCat.WinsWithCrewIfNotAttacked", true, TabGroup.NeutralRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.SchrodingersCat]);

        StealsExactImpostorRole = new BooleanOptionItem(id + 3, "SchrodingersCat.StealsExactImpostorRole", true, TabGroup.NeutralRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.SchrodingersCat]);

        KillBackKiller = new BooleanOptionItem(id + 4, "SchrodingersCat.KillBackKiller", false, TabGroup.NeutralRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.SchrodingersCat]);

        KillBackDelay = new FloatOptionItem(id + 5, "SchrodingersCat.KillBackDelay", new(0f, 60f, 0.5f), 1f, TabGroup.NeutralRoles)
            .SetParent(KillBackKiller)
            .SetValueFormat(OptionFormat.Seconds);
    }

    public override void Add(byte playerId)
    {
        On = true;
    }

    public override void Init()
    {
        On = false;
    }

    public override bool OnCheckMurderAsTarget(PlayerControl killer, PlayerControl target)
    {
        CustomRoles killerRole = killer.GetCustomRole();

        if (!StealsExactImpostorRole.GetBool() && (killerRole.IsImpostor() || killerRole.IsMadmate())) killerRole = CustomRoles.Renegade;
        if (killerRole == CustomRoles.Jackal) killerRole = CustomRoles.Sidekick;
        if (Options.SingleRoles.Contains(killerRole)) killerRole = CustomRoles.Amnesiac;

        var sender = CustomRpcSender.Create("SchrodingersCat.OnCheckMurderAsTarget", SendOption.Reliable);
        var hasValue = false;

        target.RpcSetCustomRole(killerRole);
        target.RpcChangeRoleBasis(killerRole);

        hasValue |= sender.SetKillCooldown(killer, 5f);

        hasValue |= sender.Notify(killer, string.Format(Translator.GetString("SchrodingersCat.Notify.KillerRecruited"), target.GetRealName(), CustomRoles.SchrodingersCat.ToColoredString()), 10f, setName: false);
        hasValue |= sender.Notify(target, string.Format(Translator.GetString("SchrodingersCat.Notify.RecruitedByKiller"), killer.GetRealName(), killerRole.ToColoredString()), setName: false);

        sender.SendMessage(!hasValue);

        Utils.NotifyRoles(SpecifySeer: killer, ForceLoop: true);
        Utils.NotifyRoles(SpecifySeer: target, ForceLoop: true);

        if (KillBackKiller.GetBool())
        {
            byte killerId = killer.PlayerId;
            LateTask.New(() =>
            {
                var pc = Utils.GetPlayerById(killerId);
                if (pc == null || !pc.IsAlive() || GameStates.IsMeeting) return;
                pc.Suicide(PlayerState.DeathReason.Misfire);
            }, KillBackDelay.GetFloat(), "SchrodingersCat KillBack");
        }

        return false;
    }
}