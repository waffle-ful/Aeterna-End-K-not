using System;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace EndKnot.Modules;

// 状態遷移窓 (ゲーム終了→ロビー帰還 / カウントダウン→試合開始) の時間分解計器。
//
// HITCH 行は「窓のどこかで 1 フレーム 593ms 止まった」までしか言えず、TRANSIT 行は mod 側の仕事量
// (合計 60ms 程度) しか測っていないので、残りの数百 ms が「シーン読み込み / 暗黙の UnloadUnusedAssets /
// Boehm GC の何回目 / CLR gen2」のどれに落ちているかが見えない。ここでは窓を Arm してから Flush するまで、
//  - 50ms 以上の Tick 間隙 (= ストールしたフレーム) を窓開始からの相対時刻・その時点のシーン名・
//    GC カウンタ (窓開始からの増分) ・直近 op と共に列挙し、
//  - シーンの load/unload、GCPRE、TRANSIT、STATE 遷移、UIPURGE などの節目を同じ時間軸に Mark する。
// 1 窓 = Health.log の TLINE 1 行。ストール行の前後にどの節目があるかで「シーン読み込み前 (旧シーンの
// 破棄+Unload) / 読み込み後 (新シーン初期化)」が切り分けられ、bgc の増分 × GCPRE の実測単価で GC の取り分を
// 見積もれる。窓外は一切コストを掛けない (Tick 内の分岐 1 つだけ)。
public static class TransitionTimeline
{
    private const long HitchMs = 50;
    private const int MaxItems = 48;

    private static string _window;
    private static long _startMs, _deadlineMs, _disarmAtMs;
    private static string _disarmState;
    private static int _disarmDelayMs;
    private static int _bgc0, _gc00, _gc20, _frame0;
    private static long _boehm0, _hitchSum;
    private static int _items, _dropped;
    private static readonly StringBuilder Sb = new();
    private static bool _sceneHooked, _sceneHookDead;

    public static bool Armed => _window != null;

    /// <summary>窓を開く。既に別窓が開いていれば先に flush する。disarmState に入ってから disarmDelayMs 後、または timeoutMs で閉じる。</summary>
    public static void Arm(string window, int timeoutMs, string disarmState, int disarmDelayMs)
    {
        try
        {
            if (_window != null) Flush("rearm");

            EnsureSceneHooks();

            _window = window;
            _startMs = HealthLog.HitchClock.ElapsedMilliseconds;
            _deadlineMs = _startMs + timeoutMs;
            _disarmState = disarmState;
            _disarmDelayMs = disarmDelayMs;
            _disarmAtMs = 0;
            _bgc0 = GcPrepass.BoehmCollectionCount();
            _gc00 = GC.CollectionCount(0);
            _gc20 = GC.CollectionCount(2);
            _frame0 = Time.frameCount;
            _boehm0 = GcPrepass.BoehmUsedBytes();
            _hitchSum = 0;
            _items = 0;
            _dropped = 0;
            Sb.Clear();
            Append($"+0:arm scene={SafeSceneName()}");
        }
        catch (Exception e) { Utils.ThrowException(e); }
    }

    /// <summary>節目を刻む。窓外なら何もしない (呼び出し側は Armed を気にせず呼んでよい)。</summary>
    public static void Mark(string label)
    {
        if (_window == null) return;

        try { Append($"+{HealthLog.HitchClock.ElapsedMilliseconds - _startMs}:{label}"); }
        catch { }
    }

    /// <summary>HealthLog.Tick から毎 Tick 呼ばれる。窓外は即 return。</summary>
    internal static void OnTick(long nowMs, long gapMs, string state, long boehmNow, string lastOp)
    {
        if (_window == null) return;

        try
        {
            if (gapMs >= HitchMs)
            {
                _hitchSum += gapMs;
                int bgc = GcPrepass.BoehmCollectionCount();
                string op = lastOp != null ? $" op={lastOp}" : "";
                Append($"+{nowMs - _startMs}:gap{gapMs}[st={state} scene={SafeSceneName()} f={Time.frameCount - _frame0} bgc={bgc - _bgc0} gc0={GC.CollectionCount(0) - _gc00} gc2={GC.CollectionCount(2) - _gc20} boehmMB={(boehmNow > 0 ? boehmNow / 1048576 : -1)}{op}]");
            }

            if (_disarmAtMs == 0 && state == _disarmState) _disarmAtMs = nowMs + _disarmDelayMs;

            if (nowMs >= _deadlineMs) Flush("timeout");
            else if (_disarmAtMs != 0 && nowMs >= _disarmAtMs) Flush("settled");
        }
        catch (Exception e) { Utils.ThrowException(e); }
    }

    internal static void OnStateChange(string from, string to)
    {
        if (_window == null) return;
        Mark($"STATE:{from}->{to}");
    }

    private static void Flush(string reason)
    {
        string window = _window;
        if (window == null) return;

        _window = null;

        try
        {
            long nowMs = HealthLog.HitchClock.ElapsedMilliseconds;
            long boehmNow = GcPrepass.BoehmUsedBytes();
            string dropped = _dropped > 0 ? $" dropped={_dropped}" : "";
            HealthLog.Note($"TLINE window={window} end={reason} totalMs={nowMs - _startMs} frames={Time.frameCount - _frame0} bgc={_bgc0}->{GcPrepass.BoehmCollectionCount()} gc0={_gc00}->{GC.CollectionCount(0)} gc2={_gc20}->{GC.CollectionCount(2)} boehmMB={(_boehm0 > 0 ? _boehm0 / 1048576 : -1)}->{(boehmNow > 0 ? boehmNow / 1048576 : -1)} hitchSumMs={_hitchSum} n={_items}{dropped} items={Sb} t={Utils.TimeStamp}");
        }
        catch (Exception e) { Utils.ThrowException(e); }
        finally
        {
            Sb.Clear();
        }
    }

    private static void Append(string item)
    {
        if (_items >= MaxItems)
        {
            _dropped++;
            return;
        }

        _items++;
        if (Sb.Length > 0) Sb.Append(' ');
        Sb.Append(item);
    }

    private static string SafeSceneName()
    {
        try { return SceneManager.GetActiveScene().name; }
        catch { return "?"; }
    }

    // シーンの load/unload を同じ時間軸に刻む。イベント登録は 1 回だけ (デリゲート変換は登録時の 1 回で済む)。
    // sceneUnloaded 側が使えない環境でも sceneLoaded だけで動くよう個別に守る。
    private static void EnsureSceneHooks()
    {
        if (_sceneHooked || _sceneHookDead) return;

        _sceneHooked = true;

        try { SceneManager.add_sceneLoaded((Action<Scene, LoadSceneMode>)OnSceneLoaded); }
        catch (Exception e)
        {
            _sceneHookDead = true;
            Utils.ThrowException(e);
        }

        try { SceneManager.add_sceneUnloaded((Action<Scene>)OnSceneUnloaded); }
        catch (Exception e) { Utils.ThrowException(e); }
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        try { Mark($"sceneLoaded:{scene.name}"); }
        catch { }
    }

    private static void OnSceneUnloaded(Scene scene)
    {
        try { Mark($"sceneUnloaded:{scene.name}"); }
        catch { }
    }
}
