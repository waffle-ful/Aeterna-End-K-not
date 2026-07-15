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
    // A/B ゲート (毎時 EOS 認証死ループの犯人切り分け用)。false を返すと Harmony がこのクラスの
    // パッチ適用自体をスキップする = EOSManager に DMD detour が一切入らない素の状態になる。
    // Finalizer 内 early-return では detour (by-ref 引数の marshaling 往復) が残るため不十分。
    internal static bool Prepare() => Main.EnableEosReloginPatch.Value;

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

    // ── 再ログインスタック検出 ──
    // 実測 (2026-07-10 sid=1783672562): 壊れた認証状態で LoginWithCorrectPlatform() を呼ぶと完了コールバックが
    // 二度と来ず tryingToLogin=true のまま 59 分以上スタックし、下の tryingToLogin ガードが全回復パス
    // (据え置き回収 / lastresort / proactive) を封鎖したままトークン実失効 → ゲーム中の Hacking キック → 認証死に至った。
    // 正常ログインは数秒で完了するため、オンライン接続中に 180 秒続く tryingToLogin は「不発弾」と断定して
    // フラグを強制クリアし、pending を立て直して再試行させる。2 回連続で不発ならプロセス内回復は不可能と判定し、
    // トークン実失効を待たず安全窓 (ゲーム外、期限超過で強行) でプロセス再起動へエスカレーションする。
    private const float LoginStuckSeconds = 180f;
    private const float EscalateForceDelaySeconds = 300f;

    private static float tryingSince = float.NaN;
    private static int stuckStrikes; // 連続不発回数 (自然完了で 0 に戻る)
    private static bool escalatePending;
    private static float escalateArmedTime;

    public static void Postfix(EOSManager __instance)
    {
        // 早期ブート (メインシーン前) は AmongUsClient がまだ無く GameStates が NRE るので触らない
        if (AmongUsClient.Instance == null) return;

        // ⚠️ スタック検出〜リカバリの状態機械を loginFlowFinished ガードの外に置くこと。
        // LoginWithCorrectPlatform() での再ログインが vanilla のフローを再スタートさせ
        // loginFlowFinished=false に戻す — 壊れた認証ではフローが完了せず false のまま
        // スタックするため、ガードの内側に検出器を置くと自分自身を封鎖して全回復パスが
        // 死ぬ (2026-07-11 実測: expirrecover 後 tryingToLogin=true が 40 分沈黙 → token
        // 実失効 → InTask 中に Hacking キック。BUG-20260711-03 の真因)。
        // loginFlowFinished は初回起動ログイン完了前の proactive 再ログイン抑止 (メソッド
        // 末尾) にのみ使う。

        if (__instance.tryingToLogin)
        {
            // メニューでの手動ログイン等は対象外 — 救う対象はオンライン接続中のセッションのみ
            if (!GameStates.IsOnlineGame)
            {
                tryingSince = float.NaN;
                return;
            }

            float t = Time.realtimeSinceStartup;
            if (float.IsNaN(tryingSince)) { tryingSince = t; }
            else if (t - tryingSince >= LoginStuckSeconds)
            {
                stuckStrikes++;
                tryingSince = float.NaN;
                Logger.Warn($"EOS login flow stuck for {LoginStuckSeconds:F0}s (strike {stuckStrikes}) — force-clearing tryingToLogin to unblock recovery paths", "EOSReLogin");
                HealthLog.NoteAnom($"ANOM live kind=eos stage=loginstuck n={stuckStrikes}");
                __instance.tryingToLogin = false; // 不発弾の除去 (これで回復パスの封鎖が解ける)

                if (stuckStrikes >= 2)
                {
                    // 2 連続不発 = プロセス内で EOS 認証を回復できない壊れ方。放置してもトークン実失効で
                    // ゲーム中に突然死する (実測 19:56 InTask 中の Hacking キック) ので、先回り再起動をアームする。
                    escalatePending = true;
                    escalateArmedTime = t;
                    HealthLog.NoteAnom("ANOM live kind=eos stage=stuckescalatearmed");
                }
                else
                {
                    // 即再試行をアーム: ゲーム外なら次フレームの回収パス、ゲーム中なら lastresort 期限で発火する
                    PendingRecovery = true;
                    PendingRecoveryArmedTime = t;
                }
            }

            return;
        }

        if (!float.IsNaN(tryingSince))
        {
            // 180 秒以内にフラグが自然に降りた = ログインフローが正常完了した。不発カウントをリセット。
            tryingSince = float.NaN;
            stuckStrikes = 0;
        }

        if (escalatePending)
        {
            // 安全窓 (ゲーム外) で先回り再起動。ゲームが長引く場合は期限超過で強行する
            // (どのみちトークン実失効の突然死が来るため、待ち続ける方が期待値が低い)。
            if (!GameStates.InGame || Time.realtimeSinceStartup - escalateArmedTime >= EscalateForceDelaySeconds)
            {
                escalatePending = false;
                AutoRestart.OnEosLoginStuck();
            }

            // エスカレーション待機中は回復パスを走らせない (不発と確定した弾を撃ち続けない)
            return;
        }

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

        // 初回起動のログインフロー完了前は proactive タイマー再ログインを撃たない
        // (スタック検出/pending 回収は上で処理済み — このガードはここより下にしか効かない)
        if (!__instance.loginFlowFinished) return;

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
    internal static bool Prepare() => Main.EnableEosReloginPatch.Value;

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
    internal static bool Prepare() => Main.EnableEosReloginPatch.Value;

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
            // 握りつぶす例外の正体を必ず残す (毎時1回なのでスパムしない)。NRE の発生箇所が
            // バニラ側トークン更新処理か、パッチ由来の引数破壊かを切り分ける一次証拠になる。
            Logger.Warn($"vanilla OnAuthExpirationCallback exception detail: {__exception}", "EOSReLogin");
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
