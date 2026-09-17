using EndKnot.Modules;
using static EndKnot.Options;

namespace EndKnot.Roles;

// コンビネーション役職の相方。クルー(またはベント可ならエンジニア)基底でタスクを行い、
// インポスター陣営で勝利する。インポスターとは互いに正体を認識しない — 唯一の可視化手段は
// Driver.BraidCanSeeDriver / Driver.DriverCanSeeBraid の ☆ マークのみ。
public class Braid : RoleBase
{
    public static bool On;
    public override bool IsEnable => On;

    public static OverrideTasksData Tasks;

    // Driver 側へ付与する能力のフラグ。Driver.OnTaskComplete 相当の判定はここで行い、
    // Driver クラス側から読む (Driver は 1 回だけ消費するガードの残数を自分で管理する)。
    public static bool DriverSeesKillFlash;
    public static bool DriverSeesDeathReason;
    public static bool DriverSeesVotes;
    public static bool DriverGuardUnlocked;
    public static bool BraidTasksFinished;

    private byte BraidId;

    public override void SetupCustomOption() { }

    public override void Init()
    {
        On = false;
        DriverSeesKillFlash = false;
        DriverSeesDeathReason = false;
        DriverSeesVotes = false;
        DriverGuardUnlocked = false;
        BraidTasksFinished = false;
    }

    public override void Add(byte playerId)
    {
        On = true;
        BraidId = playerId;
    }

    public override bool CanUseKillButton(PlayerControl pc)
    {
        return false;
    }

    public override bool CanUseSabotage(PlayerControl pc)
    {
        return false;
    }

    public override bool CanUseImpostorVentButton(PlayerControl pc)
    {
        return false;
    }

    public override void OnTaskComplete(PlayerControl pc, int completedTaskCount, int totalTaskCount)
    {
        if (!pc.IsAlive()) return;

        if (Driver.GiveKillFlash.GetBool() && !DriverSeesKillFlash && completedTaskCount + 1 >= Driver.KillFlashTaskTrigger.GetInt())
        {
            DriverSeesKillFlash = true;
            Logger.Info("Driver gained kill flash visibility", "Braid");
        }

        if (Driver.GiveDeathReason.GetBool() && !DriverSeesDeathReason && completedTaskCount + 1 >= Driver.DeathReasonTaskTrigger.GetInt())
        {
            DriverSeesDeathReason = true;
            Logger.Info("Driver gained death reason visibility", "Braid");
        }

        if (Driver.GiveWatchVotes.GetBool() && !DriverSeesVotes && completedTaskCount + 1 >= Driver.WatchVotesTaskTrigger.GetInt())
        {
            DriverSeesVotes = true;
            Logger.Info("Driver gained vote visibility", "Braid");
            // 匿名投票は GameOptions 経由でクライアントに届くので、次の会議に間に合うよう再送させる
            Utils.MarkEveryoneDirtySettings();
        }

        if (Driver.GiveGuard.GetBool() && !DriverGuardUnlocked && completedTaskCount + 1 >= Driver.GuardTaskTrigger.GetInt())
        {
            DriverGuardUnlocked = true;
            Logger.Info("Driver gained a guard charge", "Braid");
        }

        if (completedTaskCount + 1 >= totalTaskCount)
        {
            BraidTasksFinished = true;
            Logger.Info("Braid finished all tasks: Driver kill cooldown reduced", "Braid");
        }
    }

    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud = false, bool meeting = false)
    {
        if (seer.PlayerId == target.PlayerId) return string.Empty;

        if (Driver.BraidCanSeeDriver.GetBool() && seer.PlayerId == BraidId && target.Is(CustomRoles.Driver))
            return Utils.ColorString(Utils.GetRoleColor(CustomRoles.Braid), "☆");

        if (Driver.DriverCanSeeBraid.GetBool() && seer.Is(CustomRoles.Driver) && target.PlayerId == BraidId)
            return Utils.ColorString(Utils.GetRoleColor(CustomRoles.Braid), "☆");

        return string.Empty;
    }
}
