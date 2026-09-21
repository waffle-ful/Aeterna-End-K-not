using AmongUs.GameOptions;
using EndKnot.Modules;
using static EndKnot.Options;

namespace EndKnot.Roles;

// コンビネーション役職の主役職。相方 Braid のタスク進捗に応じて能力が解放される。
// オプションは相方の分も含めてここに全部ぶら下げる (Modules/CombinationRoles.cs の Pairs 登録により
// 相方は自分の出現率オプションを持たない)。
public class Driver : RoleBase
{
    public static bool On;
    public override bool IsEnable => On;

    public static OptionItem DriverCanSeeBraid;
    public static OptionItem KillCooldown;
    public static OptionItem KillCooldownAfterBraidTasks;
    public static OptionItem GiveKillFlash;
    public static OptionItem KillFlashTaskTrigger;
    public static OptionItem GiveDeathReason;
    public static OptionItem DeathReasonTaskTrigger;
    public static OptionItem GiveWatchVotes;
    public static OptionItem WatchVotesTaskTrigger;
    public static OptionItem GiveGuard;
    public static OptionItem GuardTaskTrigger;
    public static OptionItem BraidCanSeeDriver;
    public static OptionItem BraidCanVent;

    private bool GuardChargeAvailable;

    public override void SetupCustomOption()
    {
        StartSetup(707500)
            .AutoSetupOption(ref DriverCanSeeBraid, false)
            .AutoSetupOption(ref KillCooldown, 30f, new FloatValueRule(0f, 180f, 0.5f), OptionFormat.Seconds)
            .AutoSetupOption(ref KillCooldownAfterBraidTasks, 15f, new FloatValueRule(0f, 180f, 0.5f), OptionFormat.Seconds)
            .AutoSetupOption(ref GiveKillFlash, false)
            .AutoSetupOption(ref KillFlashTaskTrigger, 5, new IntegerValueRule(1, 297, 1), overrideParent: GiveKillFlash)
            .AutoSetupOption(ref GiveDeathReason, false)
            .AutoSetupOption(ref DeathReasonTaskTrigger, 5, new IntegerValueRule(1, 297, 1), overrideParent: GiveDeathReason)
            .AutoSetupOption(ref GiveWatchVotes, false)
            .AutoSetupOption(ref WatchVotesTaskTrigger, 5, new IntegerValueRule(1, 297, 1), overrideParent: GiveWatchVotes)
            .AutoSetupOption(ref GiveGuard, false)
            .AutoSetupOption(ref GuardTaskTrigger, 5, new IntegerValueRule(1, 297, 1), overrideParent: GiveGuard)
            .AutoSetupOption(ref BraidCanSeeDriver, false)
            .AutoSetupOption(ref BraidCanVent, true);

        // Braid は自分の出現率オプションを持たないため、通常の CreateOverrideTasksData() (StartSetup を
        // 呼び出した役職 = Driver のキーで作られる) は使えない。Braid キーで AllData に登録しつつ、
        // 表示上の親は Driver の出現率オプションにする。
        Braid.Tasks = OverrideTasksData.Create(707515, TabGroup.Combinations, CustomRoles.Braid, CustomRoleSpawnChances[CustomRoles.Driver]);
    }

    public override void Init()
    {
        On = false;
    }

    public override void Add(byte playerId)
    {
        On = true;
        GuardChargeAvailable = true;
    }

    public override void SetKillCooldown(byte id)
    {
        Main.AllPlayerKillCooldown[id] = Braid.BraidTasksFinished ? KillCooldownAfterBraidTasks.GetFloat() : KillCooldown.GetFloat();
    }

    public override void ApplyGameOptions(IGameOptions opt, byte playerId)
    {
        if (Braid.DriverSeesVotes) opt.SetBool(BoolOptionNames.AnonymousVotes, false);
    }

    /// <summary>
    ///     Braid から貰ったガード
    /// </summary>
    public override int? GetDefensePower(PlayerControl target, AttackKind kind)
    {
        return MurderOnly(kind, Braid.DriverGuardUnlocked && GuardChargeAvailable ? (int?)AttackDefense.Powerful : null);
    }

    public override bool OnCheckMurderAsTarget(PlayerControl killer, PlayerControl target, bool check = false)
    {
        if (!Braid.DriverGuardUnlocked || !GuardChargeAvailable) return true;

        if (check) return false;

        GuardChargeAvailable = false;
        Utils.NotifyRoles(SpecifySeer: killer);
        Utils.NotifyRoles(SpecifySeer: target);
        Logger.Info($"{target.GetNameWithRole().RemoveHtmlTags()}: Braid-granted guard consumed", "Driver");
        return false;
    }
}
