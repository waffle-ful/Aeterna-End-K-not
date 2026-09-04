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
    private static readonly List<(string Name, long Ms, long JitMs, long JitCount)> Marks = new();
    private static readonly HashSet<string> MarkNames = new();
    private static readonly List<string> Gaps = new();

    private static bool _menuStarted;
    private static bool _emitted;
    private static bool _firstTickNoted;
    private static long _menuInteractiveMs;
    private static float _menuInteractiveRealtime;
    private static int _menuInteractiveFrame;
    private static float _lastMenuFrameRealtime;
    private static long _lastMenuFrameJitMs;

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
            // 区間ごとの JIT 費用 (CoreCLR 全体の累積・スレッド問わず) を併記して、
            // 「パース/初期化が重いのか JIT が重いのか」を BOOT 行だけで読めるようにする。
            long jitMs = 0, jitCount = 0;
            try
            {
                jitMs = (long)System.Runtime.JitInfo.GetCompilationTime().TotalMilliseconds;
                jitCount = System.Runtime.JitInfo.GetCompiledMethodCount();
            }
            catch { }
            Marks.Add((name, NowMs, jitMs, jitCount));
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
                try { _lastMenuFrameJitMs = (long)System.Runtime.JitInfo.GetCompilationTime().TotalMilliseconds; } catch { }
                return;
            }

            float now = Time.realtimeSinceStartup;
            float gapMs = (now - _lastMenuFrameRealtime) * 1000f;
            _lastMenuFrameRealtime = now;

            // メニュー到達後の間隙が JIT (初回実行) 由来かを見分けるため、間隙 1 件ごとに
            // 直前フレームからの JIT 累積差分 (ms) を "/j" で併記する。
            long jitNow = 0;
            try { jitNow = (long)System.Runtime.JitInfo.GetCompilationTime().TotalMilliseconds; } catch { }
            long jitDelta = jitNow - _lastMenuFrameJitMs;
            _lastMenuFrameJitMs = jitNow;

            if (gapMs >= MenuFrameHitchMs && Gaps.Count < MaxGaps)
            {
                long sinceInteractiveMs = (long)((now - _menuInteractiveRealtime) * 1000f);
                Gaps.Add($"+{sinceInteractiveMs}:{(long)gapMs}/j{jitDelta}");
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
            var jitSb = new StringBuilder();
            long prevMs = 0, prevJitMs = 0, prevJitCount = 0;
            bool first = true;

            foreach ((string name, long ms, long jitMs, long jitCount) in Marks)
            {
                if (!first) { marksSb.Append(','); deltasSb.Append(','); jitSb.Append(','); }
                marksSb.Append(name).Append(':').Append(ms);
                deltasSb.Append(name).Append(":+").Append(ms - prevMs);
                jitSb.Append(name).Append(":+").Append(jitMs - prevJitMs).Append('/').Append(jitCount - prevJitCount);
                prevMs = ms;
                prevJitMs = jitMs;
                prevJitCount = jitCount;
                first = false;
            }

            int frames10s = Time.frameCount - _menuInteractiveFrame;
            double fps10s = frames10s / (double)MenuWindowSeconds;

            string line = $"BOOT total={_menuInteractiveMs} marks={marksSb} deltas={deltasSb} frames10s={frames10s} fps10s={fps10s:0.0} gaps=[{string.Join(",", Gaps)}] jit={jitSb} patch2={PatchPhases.DeferredCount}/{PatchPhases.Phase2Ms}ms/{PatchPhases.Phase2Frames}f t={Utils.TimeStamp}";

            HealthLog.Note(line);
            Logger.Info(line, "BootTimeline");
        }
        catch { }
    }
}
