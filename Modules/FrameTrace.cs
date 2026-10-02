using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEngine;

namespace EndKnot.Modules;

// 描画フレームの中を区間に割って刻む計器 (TestBridge `frametrace [frames]`)。
//   upd  = 前フレームの描画後 → このフレームの Update 後 (Present・フレーム上限の待ち・FixedUpdate・Update)
//   late = Update 後 → 計器の LateUpdate (LateUpdate の前半)
//   cam  = 計器の LateUpdate → 計器の OnGUI Repaint (LateUpdate の後半・カメラ描画・TMP のメッシュ作り直し・他の IMGUI)
//   gui  = 計器の OnGUI Repaint → 描画後 (計器の OnGUI は最後の方に呼ばれるので、ほぼ 0 になる)
// fixed = このフレームで回った FixedUpdate の回数 (Time.fixedTime の進みから)。
// 周期的なフレーム揺れが Update 側か描画側か、FixedUpdate の有無と揃うかを 1 本の CSV で答える。
public static class FrameTrace
{
    private static bool _running;
    private static long[] _late, _gui;
    private static int _cur = -1;
    private static FrameTraceProbe _probe;

    internal static void OnLate()
    {
        int i = _cur;
        if (i >= 0 && _late != null && _late[i] == 0) _late[i] = Stopwatch.GetTimestamp();
    }

    internal static void OnGui()
    {
        int i = _cur;
        if (i >= 0 && _gui != null && _gui[i] == 0 && Event.current.type == EventType.Repaint) _gui[i] = Stopwatch.GetTimestamp();
    }

    public static string Start(int frames, Action<string> done)
    {
        if (_running) return "ERR frametrace busy";
        if (Main.Instance == null) return "ERR frametrace no host";
        frames = Math.Clamp(frames, 60, 7200);
        _running = true;
        try
        {
            if (!_probe) _probe = Main.Instance.AddComponent<FrameTraceProbe>();
        }
        catch (Exception e) { Logger.Warn($"frametrace probe: {e.Message}", "FrameTrace"); }

        try { Main.Instance.StartCoroutine(Run(frames, done)); }
        catch (Exception e)
        {
            _running = false;
            return $"ERR frametrace {e.Message}";
        }

        return $"OK frametrace started {frames} frames";
    }

    private static IEnumerator Run(int frames, Action<string> done)
    {
        var upd = new long[frames];
        var eof = new long[frames];
        var fixedSteps = new int[frames];
        var frameNo = new int[frames];
        _late = new long[frames];
        _gui = new long[frames];
        double toMs = 1000.0 / Stopwatch.Frequency;
        float fixedDt = Time.fixedDeltaTime;

        yield return new WaitForEndOfFrame();
        long prevEof = Stopwatch.GetTimestamp();
        float prevFixed = Time.fixedTime;

        for (int i = 0; i < frames; i++)
        {
            yield return null;
            upd[i] = Stopwatch.GetTimestamp();
            _cur = i;
            float ft = Time.fixedTime;
            fixedSteps[i] = fixedDt > 0f ? (int)Math.Round((ft - prevFixed) / fixedDt) : -1;
            prevFixed = ft;
            frameNo[i] = Time.frameCount;
            yield return new WaitForEndOfFrame();
            eof[i] = Stopwatch.GetTimestamp();
        }

        _cur = -1;

        string result;
        try
        {
            var sb = new StringBuilder(frames * 32);
            sb.AppendLine("frame,upd_ms,rnd_ms,total_ms,fixed,late_ms,cam_ms,gui_ms");
            for (int i = 0; i < frames; i++)
            {
                long start = i == 0 ? prevEof : eof[i - 1];
                double u = (upd[i] - start) * toMs;
                double r = (eof[i] - upd[i]) * toMs;
                sb.Append(frameNo[i]).Append(',').Append(u.ToString("F3")).Append(',').Append(r.ToString("F3")).Append(',')
                    .Append((u + r).ToString("F3")).Append(',').Append(fixedSteps[i]).Append(',');
                if (_late[i] != 0 && _gui[i] != 0 && _gui[i] >= _late[i])
                    sb.Append(((_late[i] - upd[i]) * toMs).ToString("F3")).Append(',').Append(((_gui[i] - _late[i]) * toMs).ToString("F3")).Append(',')
                        .Append(((eof[i] - _gui[i]) * toMs).ToString("F3"));
                else sb.Append("-1,-1,-1");
                sb.AppendLine();
            }

            string dir = Path.GetDirectoryName(HealthLog.FilePath) ?? Main.DataPath;
            string path = Path.Combine(dir, $"frametrace_{DateTime.Now:yyyyMMdd-HHmmss}.csv");
            File.WriteAllText(path, sb.ToString());
            result = $"OK frametrace done {frames} frames fixedDt={fixedDt:F4} -> {path}";
        }
        catch (Exception e) { result = $"ERR frametrace write {e.Message}"; }

        if (_probe) UnityEngine.Object.Destroy(_probe);
        _probe = null;
        _running = false;
        done(result);
    }
}

// FrameTrace 用の時刻刻み。記録のあいだだけ付けて、終わったら外す。
public class FrameTraceProbe : MonoBehaviour
{
    private void Awake() => ImguiNoLayout.Apply(this);
    private void LateUpdate() => FrameTrace.OnLate();
    private void OnGUI() => FrameTrace.OnGui();
}
