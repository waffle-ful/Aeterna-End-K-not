using System.Diagnostics;

namespace EndKnot.Modules.Extensions;

public static class StopwatchHelper
{
    public static int GetRemainingTime(this Stopwatch stopwatch, int totalTime) => totalTime - stopwatch.Elapsed.Seconds;
}