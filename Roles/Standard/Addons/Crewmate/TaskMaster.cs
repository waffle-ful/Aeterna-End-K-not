using EndKnot.Modules;
using Il2CppSystem;
using Exception = System.Exception;

namespace EndKnot.Roles;

public class TaskMaster : IAddon
{
    public AddonTypes Type => AddonTypes.Helpful;

    public void SetupCustomOption()
    {
        Options.SetupAdtRoleOptions(655500, CustomRoles.TaskMaster, canSetNum: true);
    }

    public static void AfterMeetingTasks(PlayerControl pc)
    {
        try
        {
            TaskState ts = pc.GetTaskState();
            if (!ts.HasTasks || ts.IsTaskFinished || !Utils.HasTasks(pc.Data, forRecompute: false)) return;
            var incompleteTasks = pc.myTasks.FindAll((Predicate<PlayerTask>)(x => !x.IsComplete));
            // 非モッド客の myTasks には Id 0 の説明タスクが並ぶことがあり、CompleteTask の Id 検索が
            // そちらに当たって本命のタスクが完了表示にならない。選べる限り Id 0 以外から選ぶ。
            var pickable = incompleteTasks.FindAll((Predicate<PlayerTask>)(x => x.Id != 0));
            if (pickable.Count > 0) incompleteTasks = pickable;
            LateTask.New(() =>
            {
                if (GameStates.IsEnded) return;
                RPC.PlaySoundRPC(pc.PlayerId, Sounds.TaskUpdateSound);
                pc.RpcCompleteTask(incompleteTasks[IRandom.Instance.Next(0, incompleteTasks.Count)].Id);
            }, 0.3f, log: false);
        }
        catch (Exception e) { Utils.ThrowException(e); }
    }
}