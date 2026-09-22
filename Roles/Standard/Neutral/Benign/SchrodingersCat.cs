using System.Collections.Generic;
using System.Linq;
using Hazel;

namespace EndKnot.Roles;

internal class SchrodingersCat : RoleBase
{
    public static bool On;

    // キルバックの予約台帳 (キー = キラー)。転向した猫の役職インスタンスは RpcSetCustomRole の
    // 時点で差し替わって消えるため、遅延キルの予約をインスタンスの外に置く必要がある。
    private static readonly HashSet<byte> PendingKillBacks = [];

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
        PendingKillBacks.Clear();
    }

    /// <summary>
    ///     死なずにキラーの役職を簒奪する
    /// </summary>
    public override int? GetDefensePower(PlayerControl target, AttackKind kind)
    {
        return MurderOnly(kind, AttackDefense.Unstoppable);
    }

    public override bool OnCheckMurderAsTarget(PlayerControl killer, PlayerControl target, bool check = false)
    {
        if (check) return false;

        CustomRoles killerRole = killer.GetCustomRole();

        if (!StealsExactImpostorRole.GetBool() && (killerRole.IsImpostor() || killerRole.IsMadmate())) killerRole = CustomRoles.Renegade;
        if (killerRole == CustomRoles.Jackal) killerRole = CustomRoles.Sidekick;
        if (Options.SingleRoles.Contains(killerRole)) killerRole = CustomRoles.Amnesiac;

        var sender = CustomRpcSender.Create("SchrodingersCat.OnCheckMurderAsTarget", SendOption.Reliable);
        var hasValue = false;

        target.RpcSetCustomRole(killerRole);
        target.RpcChangeRoleBasis(killerRole);

        hasValue |= sender.SetKillCooldown(killer, 5f);

        hasValue |= sender.Notify(killer, string.Format(Translator.GetString("SchrodingersCat.Notify.KillerRecruited"), target.GetRealName(), CustomRoles.SchrodingersCat.ToColoredString()), out sender, 10f, setName: false);
        hasValue |= sender.Notify(target, string.Format(Translator.GetString("SchrodingersCat.Notify.RecruitedByKiller"), killer.GetRealName(), killerRole.ToColoredString()), out sender, setName: false);

        sender.SendMessage(!hasValue);

        Utils.NotifyRoles(SpecifySeer: killer, ForceLoop: true);
        Utils.NotifyRoles(SpecifySeer: target, ForceLoop: true);

        if (KillBackKiller.GetBool())
        {
            byte killerId = killer.PlayerId;
            PendingKillBacks.Add(killerId);

            LateTask.New(() =>
            {
                // 会議に入っていたら予約を残したまま降りる。会議開始の保険 (OnAnyoneReportDeadBody) が
                // 撃つので、ここで消費すると二重に撃つか、逆に取りこぼす。
                if (GameStates.IsMeeting || ReportDeadBodyPatch.MeetingStarted) return;
                if (!PendingKillBacks.Remove(killerId)) return;

                KillBack(killerId);
            }, KillBackDelay.GetFloat(), "SchrodingersCat KillBack");
        }

        return false;
    }

    /// <summary>
    ///     会議開始時の保険。遅延キルの前に会議が始まるとタイマーは撃てないので、
    ///     予約の残っているキラーをここで始末する。
    /// </summary>
    public static void OnAnyoneReportDeadBody()
    {
        if (PendingKillBacks.Count == 0) return;
        if (!AmongUsClient.Instance.AmHost) return;

        byte[] killerIds = PendingKillBacks.ToArray();
        PendingKillBacks.Clear();

        foreach (byte killerId in killerIds) KillBack(killerId);
    }

    private static void KillBack(byte killerId)
    {
        PlayerControl pc = Utils.GetPlayerById(killerId);
        if (pc == null || !pc.IsAlive() || pc.Data == null || pc.Data.Disconnected) return;

        // 処刑系 (RpcExileV2 直呼び) の攻撃は守りを貫くので、名指しで除くのは Pestilence だけ。
        if (pc.Is(CustomRoles.Pestilence)) return;

        PlayerState state = Main.PlayerStates[killerId];
        pc.SetRealKiller(pc);
        state.deathReason = PlayerState.DeathReason.Misfire;
        pc.RpcExileV2();
        pc.Data.IsDead = true;
        state.SetDead();
        Utils.AfterPlayerDeathTasks(pc);

        Logger.Info($"Killed back {pc.GetNameWithRole().RemoveHtmlTags()}", "SchrodingersCat");
    }
}