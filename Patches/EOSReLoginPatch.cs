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
//   (3) Telemetry: OnAuthExpirationCallback がいつ・どんな状況で呼ばれたかログる。
// ====================================================================

[HarmonyPatch(typeof(EOSManager), nameof(EOSManager.Update))]
internal static class EOSReLoginProactivePatch
{
    // 安全マージン: refresh token 寿命 8h より十分手前
    private const float ReLoginIntervalSeconds = 5f * 3600f;

    private static float lastReLoginTime = Time.realtimeSinceStartup;

    public static void Postfix(EOSManager __instance)
    {
        if (!__instance.loginFlowFinished) return;
        if (__instance.tryingToLogin) return;
        // メニュー/ロビー両方で発火させる。アクティブな試合中だけブロック
        if (GameStates.InGame) return;

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
        try
        {
            // 名前通り「リトライ」用途。GoOffline 後の cleanup 後に呼ぶのに最も自然
            __instance.RetryAuthAndLoginImpl(null, null);
            EOSReLoginProactivePatch.ResetTimer();
        }
        catch (System.Exception ex)
        {
            Logger.Error($"RetryAuthAndLoginImpl threw: {ex}", "EOSReLogin");
        }
    }
}

[HarmonyPatch(typeof(EOSManager), nameof(EOSManager.OnAuthExpirationCallback))]
internal static class EOSAuthExpirationTelemetryPatch
{
    public static void Postfix(EOSManager __instance)
    {
        Logger.Info($"OnAuthExpirationCallback fired (lobby={GameStates.IsLobby}, online={GameStates.IsOnlineGame}, tryingToLogin={__instance.tryingToLogin})", "EOSReLogin");
    }
}
