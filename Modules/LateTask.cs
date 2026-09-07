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
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                try { action(); }
                finally { Modules.AllocProbe.Mark("latetask", alloc); }

                // HITCH (≥50ms) に届かない中量級の latetask も所要時間を残す — 名前付きのみ・10ms 以上。
                // 配信 7 人卓で 46〜109ms を記録した Reset SkipTasks / Aftermeeting Blackout Buster / FixKillCooldownTask が
                // 送信前計装の 64KB 複製税 (2026-09-07 第35弾で修正) だったかを、修正後の値で判定するための計器。
                double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                if (ms >= 10 && name is not "" and not "No Name Task") Logger.Info($"\"{name}\" ms={ms:F1}", "LT");

                if (name is not "" and not "No Name Task" && log)
                    Logger.Info($"\"{name}\" is finished", "LateTask");
            }
            catch (Exception ex) { Logger.Error($"{ex.GetType()}: {ex.Message}\n  in \"{name}\"\n  (created at {path.Split('\\')[^1].Split('/')[^1]}, by member {member}, at line {line})\n  {ex.StackTrace}".Replace("\r\n", "\n"), "LateTask.Error", false, multiLine: true); }
        }
    }
}