using System.Linq;
using EndKnot.Modules;
using Il2CppSystem;

namespace EndKnot.Roles;

public class Helper : RoleBase
{
    public static bool On;

    private static OptionItem AbilityWorksAfterDeath;

    public override bool IsEnable => On;

    public override void SetupCustomOption()
    {
        StartSetup(651300)
            .CreateOverrideTasksData();

        // 651302〜651305 はタスク上書きが使うので、後から足した設定は既存 Id を動かさないようその後ろに置く
        AbilityWorksAfterDeath = new BooleanOptionItem(651306, "Helper.AbilityWorksAfterDeath", true, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Helper]);
    }

    public override void Init()
    {
        On = false;
    }

    public override void Add(byte playerId)
    {
        On = true;
    }

    public override void OnTaskComplete(PlayerControl pc, int completedTaskCount, int totalTaskCount)
    {
        if (!AbilityWorksAfterDeath.GetBool() && !pc.IsAlive()) return;
        var randomPlayer = Main.EnumeratePlayerControls().Without(pc).Where(x => x.Is(Team.Crewmate)).Select(x => (pc: x, ts: x.GetTaskState())).Where(x => !x.ts.IsTaskFinished && x.ts.HasTasks).Select(x => x.pc).RandomElement();
        var incompleteTasks = randomPlayer.myTasks.FindAll((Predicate<PlayerTask>)(x => !x.IsComplete));
        // 非モッド客の myTasks には Id 0 の説明タスクが並ぶことがあり、CompleteTask の Id 検索が
        // そちらに当たって本命のタスクが完了表示にならない。選べる限り Id 0 以外から選ぶ。
        var pickable = incompleteTasks.FindAll((Predicate<PlayerTask>)(x => x.Id != 0));
        if (pickable.Count > 0) incompleteTasks = pickable;
        RPC.PlaySoundRPC(randomPlayer.PlayerId, Sounds.TaskUpdateSound);
        randomPlayer.RpcCompleteTask(incompleteTasks[IRandom.Instance.Next(0, incompleteTasks.Count)].Id);
        randomPlayer.Notify(string.Format(Translator.GetString("HelperCompletedTaskForYou"), CustomRoles.Helper.ToColoredString()));
    }
}
