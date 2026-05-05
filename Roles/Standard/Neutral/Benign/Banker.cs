using AmongUs.GameOptions;
using EndKnot.Modules;
using static EndKnot.Options;
using static EndKnot.Translator;

namespace EndKnot.Roles;

public class Banker : RoleBase
{
    private const int Id = 703700;
    public static bool On;

    public static OptionItem InitialCoins;
    public static OptionItem TaskAddCoin;
    public static OptionItem KillAddCoin;
    public static OptionItem SwitchCoinCost;
    public static OptionItem TurnRemoveCoin;
    public static OptionItem AddWinCoin;
    public static OptionItem DieCanWin;
    public static OptionItem DieRemoveCoin;
    public static OptionItem DieRemoveTurnCoin;
    public static OptionItem KillCooldown;

    private byte BankerId = byte.MaxValue;
    private bool TaskMode;
    public int HaveCoin;
    private bool IsDead;

    public override bool IsEnable => On;

    public override void SetupCustomOption()
    {
        StartSetup(Id)
            .AutoSetupOption(ref InitialCoins, 5, new IntegerValueRule(0, 50, 1))
            .AutoSetupOption(ref TaskAddCoin, 5, new IntegerValueRule(1, 50, 1))
            .AutoSetupOption(ref KillAddCoin, 15, new IntegerValueRule(1, 50, 1))
            .AutoSetupOption(ref SwitchCoinCost, 5, new IntegerValueRule(0, 50, 1))
            .AutoSetupOption(ref TurnRemoveCoin, 3, new IntegerValueRule(0, 50, 1))
            .AutoSetupOption(ref AddWinCoin, 60, new IntegerValueRule(1, 200, 1))
            .AutoSetupOption(ref DieCanWin, false)
            .AutoSetupOption(ref DieRemoveCoin, 30, new IntegerValueRule(1, 100, 1), overrideParent: DieCanWin)
            .AutoSetupOption(ref DieRemoveTurnCoin, 10, new IntegerValueRule(1, 100, 1), overrideParent: DieCanWin)
            .AutoSetupOption(ref KillCooldown, 30, new IntegerValueRule(5, 180, 5), OptionFormat.Seconds)
            .CreateOverrideTasksData();
    }

    public override void Init()
    {
        On = false;
        BankerId = byte.MaxValue;
    }

    public override void Add(byte playerId)
    {
        On = true;
        BankerId = playerId;
        TaskMode = true;
        HaveCoin = InitialCoins.GetInt();
        IsDead = false;
    }

    public override void Remove(byte playerId)
    {
        if (BankerId == playerId) On = false;
    }

    public override void SetKillCooldown(byte id)
    {
        Main.AllPlayerKillCooldown[id] = KillCooldown.GetFloat();
    }

    public override bool CanUseKillButton(PlayerControl pc)
    {
        return !TaskMode && pc.IsAlive();
    }

    public override bool CanUseImpostorVentButton(PlayerControl pc)
    {
        return pc.IsAlive();
    }

    public override bool CanUseSabotage(PlayerControl pc) => false;

    public override void ApplyGameOptions(IGameOptions opt, byte id)
    {
        opt.SetVision(false);
    }

    public override void OnTaskComplete(PlayerControl pc, int completedTaskCount, int totalTaskCount)
    {
        if (pc.PlayerId != BankerId || !TaskMode) return;
        HaveCoin += TaskAddCoin.GetInt();
        Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc);
    }

    public override bool OnCheckMurder(PlayerControl killer, PlayerControl target)
    {
        if (!TaskMode)
        {
            HaveCoin += KillAddCoin.GetInt();
            Utils.NotifyRoles(SpecifySeer: killer, SpecifyTarget: killer);
        }
        return true;
    }

    public override void OnEnterVent(PlayerControl pc, Vent vent)
    {
        if (!pc.IsAlive()) return;
        if (HaveCoin < SwitchCoinCost.GetInt()) return;
        HaveCoin -= SwitchCoinCost.GetInt();
        TaskMode = !TaskMode;
        pc.SetKillCooldown();
        pc.Notify(GetString(TaskMode ? "Banker.SwitchedToTask" : "Banker.SwitchedToKill"));
        Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc);
    }

    public override void OnFixedUpdate(PlayerControl pc)
    {
        if (!pc.IsAlive() && !IsDead)
        {
            IsDead = true;
            if (DieCanWin.GetBool())
            {
                HaveCoin -= DieRemoveCoin.GetInt();
                Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc);
            }
        }
    }

    public override void AfterMeetingTasks()
    {
        if (CustomWinnerHolder.WinnerTeam != CustomWinner.Default) return;
        PlayerControl pc = Utils.GetPlayerById(BankerId);
        if (pc == null) return;
        HaveCoin -= pc.IsAlive() ? TurnRemoveCoin.GetInt() : DieRemoveTurnCoin.GetInt();
        Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc);
    }

    public override string GetProgressText(byte playerId, bool comms)
    {
        if (playerId != BankerId) return string.Empty;
        PlayerControl pc = Utils.GetPlayerById(playerId);
        if (pc == null) return string.Empty;
        bool canWin = pc.IsAlive() || DieCanWin.GetBool();
        if (!canWin && IsDead) return string.Empty;
        string modeStr = TaskMode ? GetString("Banker.ModeTask") : GetString("Banker.ModeKill");
        return modeStr + Utils.ColorString(Utils.GetRoleColor(CustomRoles.Banker), $"({HaveCoin})");
    }

    public bool IsWon => HaveCoin >= AddWinCoin.GetInt();

    public override void SetButtonTexts(HudManager hud, byte id)
    {
        if (TaskMode)
            hud.ImpostorVentButton?.OverrideText(GetString("Banker.VentButtonText"));
        else
            hud.KillButton?.OverrideText(GetString("Banker.KillButtonText"));
    }
}
