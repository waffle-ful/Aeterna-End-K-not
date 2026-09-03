using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEngine;

namespace EndKnot.Modules;

// 起動〜メニュー到達までの区間タイミングを1行の BOOT ログにまとめる計器。ホストローカルの
// 観測のみで送信は一切行わない。プロセス起動からの経過を各 Mark 呼び出し元が刻み、
// 最初のメニューが操作可能になってから10秒間のフレーム間隔を見てヒッチを拾う。
public static class BootTimeline
{
    private const int MaxMarks = 64;
    private const int MaxGaps = 40;
    private const float MenuFrameHitchMs = 50f;
    private const float MenuWindowSeconds = 10f;

    private static readonly DateTime T0;
    private static readonly List<(string Name, long Ms)> Marks = new();
    private static readonly HashSet<string> MarkNames = new();
    private static readonly List<string> Gaps = new();

    private static bool _menuStarted;
    private static bool _emitted;
    private static bool _firstTickNoted;
    private static long _menuInteractiveMs;
    private static float _menuInteractiveRealtime;
    private static int _menuInteractiveFrame;
    private static float _lastMenuFrameRealtime;

    static BootTimeline()
    {
        try { T0 = Process.GetCurrentProcess().StartTime; }
        catch { T0 = DateTime.Now; }
    }

    private static long NowMs => (long)(DateTime.Now - T0).TotalMilliseconds;

    public static void Mark(string name)
    {
        try
        {
            if (MarkNames.Contains(name) || Marks.Count >= MaxMarks) return;
            MarkNames.Add(name);
            Marks.Add((name, NowMs));
        }
        catch { }
    }

    // 最初の FixedUpdate 到達を1回だけ記録する。ガードは呼び出し側の bool チェックで
    // 済ませ、2回目以降は Mark() の集合検索すら発生させない。
    public static void NoteFirstTick()
    {
        if (_firstTickNoted) return;
        _firstTickNoted = true;
        Mark("firstTick");
    }

    // MainMenuManager.LateUpdate から毎フレーム呼ばれる。プロセス最初のメニュー到達でだけ
    // 10秒間のヒッチ計測窓を回し、Emit() を1回発火する (以降のメニュー再構築は何もしない)。
    public static void OnMenuFrame()
    {
        try
        {
            if (_emitted) return;

            if (!_menuStarted)
            {
                if (!MarkNames.Contains("menu.start.end") && !MarkNames.Contains("menu.vanilla.start")) return;

                _menuStarted = true;
                Mark("menu.interactive");
                _menuInteractiveMs = NowMs;
                _menuInteractiveRealtime = Time.realtimeSinceStartup;
                _menuInteractiveFrame = Time.frameCount;
                _lastMenuFrameRealtime = _menuInteractiveRealtime;
                return;
            }

            float now = Time.realtimeSinceStartup;
            float gapMs = (now - _lastMenuFrameRealtime) * 1000f;
            _lastMenuFrameRealtime = now;

            if (gapMs >= MenuFrameHitchMs && Gaps.Count < MaxGaps)
            {
                long sinceInteractiveMs = (long)((now - _menuInteractiveRealtime) * 1000f);
                Gaps.Add($"+{sinceInteractiveMs}:{(long)gapMs}");
            }

            if (now - _menuInteractiveRealtime >= MenuWindowSeconds)
            {
                _emitted = true;
                Emit();
            }
        }
        catch { }
    }

    private static void Emit()
    {
        try
        {
            var marksSb = new StringBuilder();
            var deltasSb = new StringBuilder();
            long prevMs = 0;
            bool first = true;

            foreach ((string name, long ms) in Marks)
            {
                if (!first) { marksSb.Append(','); deltasSb.Append(','); }
                marksSb.Append(name).Append(':').Append(ms);
                deltasSb.Append(name).Append(":+").Append(ms - prevMs);
                prevMs = ms;
                first = false;
            }

            int frames10s = Time.frameCount - _menuInteractiveFrame;
            double fps10s = frames10s / (double)MenuWindowSeconds;

            string line = $"BOOT total={_menuInteractiveMs} marks={marksSb} deltas={deltasSb} frames10s={frames10s} fps10s={fps10s:0.0} gaps=[{string.Join(",", Gaps)}] patch2={PatchPhases.DeferredCount}/{PatchPhases.Phase2Ms}ms/{PatchPhases.Phase2Frames}f t={Utils.TimeStamp}";

            HealthLog.Note(line);
            Logger.Info(line, "BootTimeline");
        }
        catch { }
    }
}
