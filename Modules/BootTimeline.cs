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
    private const float BootFrameHitchMs = 40f;
    private const int MaxBootGaps = 48;

    private static readonly DateTime T0;
    private static readonly List<(string Name, long Ms, long JitMs, long JitCount)> Marks = new();
    private static readonly HashSet<string> MarkNames = new();
    private static readonly List<string> Gaps = new();
    private static readonly List<string> BootGaps = new();

    private static bool _menuStarted;
    private static bool _preludeEnded;
    private static bool _emitted;
    private static bool _firstTickNoted;
    private static bool _eosTokenMarked;
    private static bool _eosFlowMarked;
    private static int _eosTokenProbeFailures;
    private static long _menuInteractiveMs;
    private static float _menuInteractiveRealtime;
    private static int _menuInteractiveFrame;
    private static float _lastMenuFrameRealtime;
    private static long _lastMenuFrameJitMs;
    private static string _lastMarkName;

    // splash 最初のフレーム〜メニュー到達までの区間を覆う計器。
    private static bool _bootStarted;
    private static bool _bootDone;
    private static long _bootFirstFrameMs;
    private static float _bootFirstFrameRealtime;
    private static float _lastBootFrameRealtime;
    private static long _lastBootFrameJitMs;
    private static int _lastBootGcCount;
    private static int _bootFrames;
    private static double _bootExcessMs;

    static BootTimeline()
    {
        try { T0 = Process.GetCurrentProcess().StartTime; }
        catch { T0 = DateTime.Now; }
    }

    private static long NowMs => (long)(DateTime.Now - T0).TotalMilliseconds;

    /// <summary>プロセス最初のメインメニュー到達 (menu.interactive) 済みか。起動中の作業配分 (スプラッシュ中は控えめ) の判定用。</summary>
    public static bool MenuReached => _menuStarted;

    /// <summary>オプションプレリュード (opts.prelude.end) の Mark が済んだか。メニュー到達までの空き時間の起点判定用。</summary>
    public static bool PreludeEnded => _preludeEnded;

    public static void Mark(string name)
    {
        try
        {
            if (name == "opts.prelude.end") _preludeEnded = true;
            if (MarkNames.Contains(name) || Marks.Count >= MaxMarks) return;
            MarkNames.Add(name);
            _lastMarkName = name;
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

            // EOS ログイン鎖の 2 つの節目 (プラットフォームログイン完了 = トークン取得 / ログインフロー完了 =
            // ロビー作成が許される時刻) を、パッチ無しでフレームごとの読み取りだけで刻む。
            if (!_eosFlowMarked)
            {
                EOSManager eos = null;
                try { eos = EOSManager.Instance; } catch { }

                if (eos != null)
                {
                    // UserIDToken の getter はログイン完了前に NullReference を投げる (バニラ挙動) ので、
                    // 例外を 1 度見たら以降は 15 フレームに 1 回だけ読み直す。
                    if (!_eosTokenMarked && (_eosTokenProbeFailures == 0 || Time.frameCount % 15 == 0))
                    {
                        try
                        {
                            if (!string.IsNullOrEmpty(eos.UserIDToken)) { _eosTokenMarked = true; Mark("eos.token"); }
                        }
                        catch { _eosTokenProbeFailures++; }
                    }

                    try
                    {
                        if (eos.loginFlowFinished) { _eosFlowMarked = true; Mark("eos.flowdone"); }
                    }
                    catch { _eosFlowMarked = true; }
                }
            }

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

    // splash の最初のフレームからメニュー到達までの毎フレーム間隔を見て、40ms 以上のヒッチを
    // その時点の pump 状況ごと記録する。opts.prelude.end 以降 (遅延 prewarm/pump の実行区間) も
    // 引き続き記録し、その間隙を隠さない。
    public static void OnBootFrame()
    {
        if (_bootDone) return;

        try
        {
            if (_menuStarted)
            {
                _bootDone = true;
                return;
            }

            float now = Time.realtimeSinceStartup;

            if (!_bootStarted)
            {
                _bootStarted = true;
                _bootFirstFrameMs = NowMs;
                _bootFirstFrameRealtime = now;
                _lastBootFrameRealtime = now;
                try { _lastBootFrameJitMs = (long)System.Runtime.JitInfo.GetCompilationTime().TotalMilliseconds; } catch { }
                try { _lastBootGcCount = GC.CollectionCount(0) + GC.CollectionCount(1) + GC.CollectionCount(2); } catch { }
                return;
            }

            float gapMs = (now - _lastBootFrameRealtime) * 1000f;
            _lastBootFrameRealtime = now;

            _bootFrames++;
            _bootExcessMs += Math.Max(0, gapMs - (1000.0 / 60.0));

            long jitNow = 0;
            try { jitNow = (long)System.Runtime.JitInfo.GetCompilationTime().TotalMilliseconds; } catch { }
            long jitDelta = jitNow - _lastBootFrameJitMs;
            _lastBootFrameJitMs = jitNow;

            int gcNow = 0;
            try { gcNow = GC.CollectionCount(0) + GC.CollectionCount(1) + GC.CollectionCount(2); } catch { }
            int gcDelta = gcNow - _lastBootGcCount;
            _lastBootGcCount = gcNow;

            if (gapMs >= BootFrameHitchMs && BootGaps.Count < MaxBootGaps)
            {
                long sinceFirstMs = (long)((now - _bootFirstFrameRealtime) * 1000f);
                string lastItem = PatchPhases.LastItem ?? "-";
                string lastMark = _lastMarkName ?? "-";
                BootGaps.Add($"+{sinceFirstMs}:{(long)gapMs}/j{jitDelta}/g{gcDelta}/p{PatchPhases.LastPumpMs}/{lastItem}/{lastMark}");
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

            string line = $"BOOT total={_menuInteractiveMs} marks={marksSb} deltas={deltasSb} frames10s={frames10s} fps10s={fps10s:0.0} gaps=[{string.Join(",", Gaps)}] jit={jitSb} patch2={PatchPhases.DeferredCount}/{PatchPhases.Phase2Ms}ms/{PatchPhases.Phase2Frames}f t={Utils.TimeStamp} sframes={_bootFrames} sexcess={_bootExcessMs:0} sgaps=[{string.Join(",", BootGaps)}]";

            HealthLog.Note(line);
            Logger.Info(line, "BootTimeline");
        }
        catch { }
    }
}
