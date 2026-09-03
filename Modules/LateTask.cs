using System;
using System.Collections;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace EndKnot;

internal static class LateTask
{
    /// <summary>
    ///     Creates a task that will be automatically completed after the specified amount of time
    /// </summary>
    /// <param name="action">The delayed task</param>
    /// <param name="time">The time to wait until the task is run</param>
    /// <param name="name">The name of the task</param>
    /// <param name="log">Whether to send log of the creation and completion of the Late Task</param>
    public static void New(Action action, float time, string name = "No Name Task", bool log = true, [CallerFilePath] string path = "", [CallerMemberName] string member = "", [CallerLineNumber] int line = 0)
    {
        if (log && name is not "" and not "No Name Task") Logger.Info($"\"{name}\" is created (completes in {time:N2})", "LateTask");
        Main.Instance.StartCoroutine(CoLateTask());
        return;

        IEnumerator CoLateTask()
        {
            yield return new WaitForSecondsRealtime(time);

            try
            {
                // 無名タスクは最頻出なので帰属に載せない — lastOp が "No Name Task" で埋まると
                // 名前付きの重い区間 (帰属計器の本命) を上書きして計器が無意味化する。
                if (name is not "" and not "No Name Task") Modules.HealthLog.NoteOp(name);

                var alloc = Modules.AllocProbe.Now();
                try { action(); }
                finally { Modules.AllocProbe.Mark("latetask", alloc); }
            
                if (name is not "" and not "No Name Task" && log)
                    Logger.Info($"\"{name}\" is finished", "LateTask");
            }
            catch (Exception ex) { Logger.Error($"{ex.GetType()}: {ex.Message}\n  in \"{name}\"\n  (created at {path.Split('\\')[^1].Split('/')[^1]}, by member {member}, at line {line})\n  {ex.StackTrace}".Replace("\r\n", "\n"), "LateTask.Error", false, multiLine: true); }
        }
    }
}