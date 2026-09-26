using System.Collections.Generic;

namespace EndKnot.Roles;

internal class TaskManager : RoleBase
{
    public static bool On;
    private byte TaskManagerId;
    public override bool IsEnable => On;

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(5575, TabGroup.CrewmateRoles, CustomRoles.TaskManager);
    }

    public override void Add(byte playerId)
    {
        On = true;
        TaskManagerId = playerId;
    }

    public override void Init()
    {
        On = false;
    }

    // マッドメイトのタスク管理者は、会議のたびに生存クルーで最もタスクが遅れている相手を
    // 未提出者リストとして生存インポスター全員へ知らせる。会議開始時の一斉送信と重ならないよう、
    // 他の会議通知役職 (Pathologist 等) と同じく少し遅延させ、複数宛先はまとめて送出する。
    public override void OnReportDeadBody()
    {
        byte taskManagerId = TaskManagerId;
        PlayerControl pc = taskManagerId.GetPlayer();
        if (pc == null || !pc.Is(CustomRoles.Madmate)) return;

        PlayerControl straggler = null;
        int minCompleted = int.MaxValue;
        int stragglerTotal = 0;

        foreach (PlayerControl alive in Main.CachedAlivePlayerControls())
        {
            if (alive.PlayerId == taskManagerId || alive.Is(CustomRoleTypes.Impostor)) continue;

            TaskState taskState = Main.PlayerStates[alive.PlayerId].TaskState;
            if (!taskState.HasTasks) continue;

            int completed = taskState.CompletedTasksCount;
            if (completed >= minCompleted) continue;

            minCompleted = completed;
            stragglerTotal = taskState.AllTasksCount;
            straggler = alive;
        }

        if (straggler == null) return;

        string leakTitle = Utils.ColorString(Palette.ImpostorRed, Translator.GetString("TaskManagerMadLeakTitle"));
        string text = string.Format(Translator.GetString("TaskManagerMadLeak"), straggler.GetRealName(), minCompleted, stragglerTotal);

        LateTask.New(() =>
        {
            List<Message> leaks = [];
            foreach (PlayerControl imp in Main.EnumerateAlivePlayerControls())
            {
                if (imp.PlayerId == taskManagerId || !imp.Is(CustomRoleTypes.Impostor)) continue;
                leaks.Add(new Message(text, imp.PlayerId, leakTitle));
            }

            leaks.SendMultipleMessages(MessageImportance.High);
        }, 3f, "TaskManagerMadLeak");
    }

    public override string GetProgressText(byte playerId, bool comms)
    {
        var ProgressText = new StringBuilder();

        ProgressText.Append(Utils.GetTaskCount(playerId, comms));

        string totalCompleted = comms ? "?" : $"{GameData.Instance.CompletedTasks}";
        ProgressText.Append($" <color=#777777>-</color> <color=#00ffa5>{totalCompleted}</color><color=#ffffff>/{GameData.Instance.TotalTasks}</color>");

        return ProgressText.ToString();
    }
}