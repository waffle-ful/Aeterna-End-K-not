using EndKnot.Modules;
using HarmonyLib;
using UnityEngine;

namespace EndKnot;

// ====================================================================
// Epic 6h logout 自動復帰 (in-mod, no launcher)
// ----------------------------------------------------------------------
// EOS の auth token 寿命:
//   - Access token: 2h (SDK が refresh token で自動更新)
//   - Refresh token: 8h (これが切れると再 login 必須 = "user not login")
//
// 戦略:
//   (1) Proactive: 5h 経過時点でロビーにいたら LoginWithCorrectPlatform() を呼び、
//       refresh token 寿命をリセット。Epic Launcher 経由で silent に再 auth される。
//   (2) Reactive: 万一 (1) が間に合わず GoOfflineFromPlatformSignout が呼ばれたら、
//       Postfix で即 StartInitialLoginFlow() を呼んで復帰を試みる。
//   (3) Telemetry: OnAuthExpirationCallback がいつ・どんな状況で呼ばれたかを Finalizer でログる
//       (バニラ本体がこの定期コールで NRE を投げるので、Postfix だと skip される → Finalizer で確実に記録 + NRE 握りつぶし)。
// ====================================================================

[HarmonyPatch(typeof(EOSManager), nameof(EOSManager.Update))]
internal static class EOSReLoginProactivePatch
{
    // 発火間隔。実測ログアウトが約5hなので、それより手前(3h)で先回りして
    // refresh token 寿命をリセットする（実機検証 2026-07-02: 発火・実ログイン成功・ロビー非破壊を確認）。
    private const float ReLoginIntervalSeconds = 3f * 3600f;

    private static float lastReLoginTime = Time.realtimeSinceStartup;

    // ゲーム中に OnAuthExpirationCallback が途中死したとき、即時再ログインは接続を壊すかも
    // しれないので、このフラグを立てておき、ゲームを抜けた最初のフレームで回収して遅延再ログインする。
    internal static bool PendingRecovery;
    internal static float PendingRecoveryArmedTime;

    // ゲームが LastResort 秒以内に終わらなければゲーム中でも再ログインを強行する。
    // 実測 (BUG-20260707-10 相関 n=4) で expircallback の 8〜14 分後にトークン実失効 →
    // ホスト確定切断なので、5 分待ってもゲーム続行中なら「どうせ死ぬ接続」— 賭けて再ログインする方が期待値が高い。
    private const float LastResortDelaySeconds = 300f;

    public static void Postfix(EOSManager __instance)
    {
        if (!__instance.loginFlowFinished) return;
        if (__instance.tryingToLogin) return;

        // メニュー/ロビー両方で発火させる。アクティブな試合中は原則ブロックだが、
        // 据え置いた再ログインが LastResort 期限を超えたらゲーム中でも強行する (放置=確実な認証死のため)。
        if (GameStates.InGame)
        {
            if (PendingRecovery && Time.realtimeSinceStartup - PendingRecoveryArmedTime >= LastResortDelaySeconds)
            {
                Logger.Warn($"Deferred EOS re-login still pending after {LastResortDelaySeconds:F0}s in-game — token death is imminent, attempting last-resort re-login mid-game", "EOSReLogin");
                HealthLog.NoteAnom("ANOM live kind=eos stage=expirrecoverlastresort");
                try
                {
                    PendingRecovery = false;
                    __instance.LoginWithCorrectPlatform();
                    lastReLoginTime = Time.realtimeSinceStartup;
                }
                catch (System.Exception ex)
                {
                    Logger.Error($"Last-resort LoginWithCorrectPlatform threw: {ex}", "EOSReLogin");
                    HealthLog.NoteAnom($"ANOM live kind=eos stage=expirrecoverfail msg=\"{ex.Message}\"");
                    // 失敗時は pending を維持しつつ期限を仕切り直す (ゲーム終了時の回収パスは即発火する)
                    PendingRecovery = true;
                    PendingRecoveryArmedTime = Time.realtimeSinceStartup;
                }
            }

            return;
        }

        // ゲーム中に据え置かれた再ログイン要求をゲーム外へ出た時点で回収する
        if (PendingRecovery)
        {
            PendingRecovery = false;
            Logger.Warn("Executing deferred EOS re-login (expircallback fired mid-game) now that we're out of game", "EOSReLogin");
            HealthLog.NoteAnom("ANOM live kind=eos stage=expirrecoverdeferred");
            try
            {
                __instance.LoginWithCorrectPlatform();
                lastReLoginTime = Time.realtimeSinceStartup;
            }
            catch (System.Exception ex)
            {
                Logger.Error($"Deferred LoginWithCorrectPlatform threw: {ex}", "EOSReLogin");
                HealthLog.NoteAnom($"ANOM live kind=eos stage=expirrecoverfail msg=\"{ex.Message}\"");
            }

            return;
        }

        float now = Time.realtimeSinceStartup;
        float elapsed = now - lastReLoginTime;
        if (elapsed < ReLoginIntervalSeconds) return;

        Logger.Info($"Proactive EOS re-login fired (uptime {now:F0}s, elapsed since last {elapsed:F0}s)", "EOSReLogin");
        lastReLoginTime = now;
        try
        {
            __instance.LoginWithCorrectPlatform();
        }
        catch (System.Exception ex)
        {
            Logger.Error($"LoginWithCorrectPlatform threw: {ex}", "EOSReLogin");
            HealthLog.NoteAnom($"ANOM live kind=eos stage=proactivefail msg=\"{ex.Message}\"");
        }
    }

    public static void ResetTimer() => lastReLoginTime = Time.realtimeSinceStartup;
}

[HarmonyPatch(typeof(EOSManager), nameof(EOSManager.GoOfflineFromPlatformSignout))]
internal static class EOSReLoginReactivePatch
{
    public static void Postfix(EOSManager __instance)
    {
        Logger.Warn("GoOfflineFromPlatformSignout fired — attempting silent re-login", "EOSReLogin");
        // signout 自体が「proactive re-login が間に合わずトークン失効した」証拠なので live に残す
        HealthLog.NoteAnom("ANOM live kind=eos stage=signout");

        try
        {
            // 名前通り「リトライ」用途。GoOffline 後の cleanup 後に呼ぶのに最も自然
            __instance.RetryAuthAndLoginImpl(null, null);
            EOSReLoginProactivePatch.ResetTimer();
        }
        catch (System.Exception ex)
        {
            Logger.Error($"RetryAuthAndLoginImpl threw: {ex}", "EOSReLogin");
            HealthLog.NoteAnom($"ANOM live kind=eos stage=retryfail msg=\"{ex.Message}\"");
        }
    }
}

[HarmonyPatch(typeof(EOSManager), nameof(EOSManager.OnAuthExpirationCallback))]
internal static class EOSAuthExpirationTelemetryPatch
{
    // vanillaThrew 後の能動再ログインの連射防止 (コールバックが短時間に複数回来ても1回だけ)
    private const float RecoveryThrottleSeconds = 600f;

    private static float lastRecoveryTime = float.NegativeInfinity;

    // Finalizer なので original (バニラ) が NRE を投げても必ず走る。
    // Postfix だと original throw 時に skip され、テレメトリが死にコードになっていた。
    // __exception を返さず null を返すことでバニラ由来の NRE を握りつぶし、コンソールの赤を消す。
    // ⚠️ vanillaThrew = バニラのトークン更新ハンドラが途中死 = 放置すると 8〜14 分後に
    // トークン実失効 → サーバがホストを落とす (6pings / Hacking) → "User is not logged in"
    // (2026-07-07/08 実測相関 n=3)。握りつぶすだけでは済まないので能動再ログインで回復する。
    public static System.Exception Finalizer(EOSManager __instance, System.Exception __exception)
    {
        Logger.Info($"OnAuthExpirationCallback fired (lobby={GameStates.IsLobby}, online={GameStates.IsOnlineGame}, tryingToLogin={__instance.tryingToLogin}, vanillaThrew={__exception != null})", "EOSReLogin");

        if (__exception != null)
        {
            HealthLog.NoteAnom($"ANOM live kind=eos stage=expircallback lobby={GameStates.IsLobby} online={GameStates.IsOnlineGame}");

            float now = Time.realtimeSinceStartup;
            if (GameStates.InGame)
            {
                // ゲーム中に即 LoginWithCorrectPlatform() すると接続を壊すおそれがあるので即時実行しない。
                // pending を立てて、ゲーム終了後に proactive Postfix が回収する（トークン失効前にゲームが終われば救済成功）。
                // ゲームが LastResortDelaySeconds 以内に終わらなければ proactive 側がゲーム中でも強行する。
                EOSReLoginProactivePatch.PendingRecovery = true;
                EOSReLoginProactivePatch.PendingRecoveryArmedTime = now;
                Logger.Warn("vanilla OnAuthExpirationCallback threw during a game — deferring re-login until the game ends (or last-resort deadline)", "EOSReLogin");
                HealthLog.NoteAnom("ANOM live kind=eos stage=expirdeferarmed");
            }
            else if (!__instance.tryingToLogin && now - lastRecoveryTime >= RecoveryThrottleSeconds)
            {
                lastRecoveryTime = now;
                try
                {
                    Logger.Warn("vanilla OnAuthExpirationCallback threw — forcing re-login to avert token death", "EOSReLogin");
                    HealthLog.NoteAnom("ANOM live kind=eos stage=expirrecover");
                    __instance.LoginWithCorrectPlatform();
                    EOSReLoginProactivePatch.ResetTimer();
                }
                catch (System.Exception ex)
                {
                    Logger.Error($"LoginWithCorrectPlatform threw: {ex}", "EOSReLogin");
                    HealthLog.NoteAnom($"ANOM live kind=eos stage=expirrecoverfail msg=\"{ex.Message}\"");
                }
            }
        }

        return null;
    }
}
