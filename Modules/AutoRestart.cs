using System.Diagnostics;
using System.Threading;
using InnerNet;
using UnityEngine;

namespace EndKnot.Modules;

// 配信者向け「自動再起動」。AutoRehost (その場での部屋立て直し) が全リトライを使い果たした =
// ネット回線 / EOS 認証が死んでおり、プロセスを生かしたままでは二度とオンライン部屋を立てられない状態。
// このとき番犬 (WatchdogLauncher) が動いていれば、ゲームを一度終了してプロセスごと立て直す。
// 新プロセスは EOS ログインと接続をやり直すので、その場立て直しでは戻れない「認証不能」から復帰できる。
//
// 【設計】
//   ・トリガーは AutoRehost.GiveUp() 一点 (回復ラダーの最終段 = その場回復が定義上失敗した瞬間)。
//   ・復帰は既存の番犬蘇生パスを流用: Application.Quit() → 番犬が「プロセス消失」を検知 → 再起動 →
//     autohost_request.flag を置く → OnMainMenuStart が前回設定で自動ホスト。番犬スクリプト側の変更は不要。
//   ・番犬が居ないときは終了しない (ユーザーを無人放置で締め出さないため)。代わりに手動再起動を促す通知のみ。
//   ・暴走再起動の抑止: プロセス起動直後 (MinUptime 未満) は終了しない + 番犬側の per-hour 上限/クールダウン。
public static class AutoRestart
{
    // 起動直後にこの秒数を過ぎるまでは再起動しない (ブート直後の一時的失敗で無限に落ちるのを防ぐ)。
    // 実際の GiveUp はデフォルト設定で ~145s+ かかる (3 リトライ×45s + 待機) ため、この値が正規の
    // エスカレーションを妨げることはない。純粋にブートループ抑止のベルト (番犬側の per-hour 上限と二重)。
    private const float MinUptimeSeconds = 60f;
    // 終了までの猶予 (ポップアップ表示 + マーカー書き込みを確実に着地させる)。
    private const float QuitDelaySeconds = 3f;

    // ハード終了ベルト: Application.Quit を投げてからこの秒数を過ぎてもプロセスが生きていたら
    // (メインスレッドが固まって Quit コルーチンが着地しないケース) バックグラウンドスレッドから
    // 強制終了する。番犬がプロセス消失を検知して立て直す。
    private const int HardKillAfterSeconds = 10;

    // 再起動のための終了進行中フラグ。WatchdogLauncher.OnGameQuit がこれを見て「意図的終了」の
    // stop-flag 書き込みをスキップする (でないと再起動のはずが番犬を止めて蘇生されなくなる)。
    public static bool RestartInProgress { get; private set; }

    // ── 認証/サーバー死の判定 ──
    // これらの理由での切断は「その場では二度とオンライン部屋を立てられない」種別。EOS 認証トークンや
    // マッチメイカー経路が死んでおり、AutoRehost (同プロセスでの立て直し) では回復できない。
    // プロセスを丸ごと立て直して EOS ログインからやり直すのが唯一の回復パス。
    // ※ 一時的な kick / ping タイムアウト (Custom/Error/Kicked 等) は含めない — そちらは AutoRehost が先に試す。
    public static bool IsFatalConnectionDeath(DisconnectReasons reason) => reason is
        DisconnectReasons.NotAuthorized
        or DisconnectReasons.NoServersAvailable
        or DisconnectReasons.ServerNotFound
        or DisconnectReasons.MatchmakerInactivity
        or DisconnectReasons.InternalConnectionToken
        or DisconnectReasons.InternalNonceFailure;

    // ── EOS 認証トークン死の判定 ──
    // IsFatalConnectionDeath の部分集合。これらは「EOS ログイントークンが失効した」種別で、EGL 本体の
    // 再起動でトークンがリフレッシュされて復帰しうる。サーバー到達不能 (NoServersAvailable/ServerNotFound/
    // MatchmakerInactivity) は EGL 再起動では直らないので含めない (BUG-17: EGL 先行再起動は authfail 限定)。
    private static bool IsAuthTokenDeath(DisconnectReasons reason) => reason is
        DisconnectReasons.NotAuthorized
        or DisconnectReasons.InternalConnectionToken
        or DisconnectReasons.InternalNonceFailure;

    // 切断フック (HealthLogDisconnectPatch から reason 付きで呼ばれる)。認証/サーバー死なら
    // AutoRehost の 3×リトライ (~145s の無駄) を飛ばして即プロセス再起動へエスカレーションする (穴1+穴5)。
    public static void OnDisconnect(DisconnectReasons reason)
    {
        if (!IsFatalConnectionDeath(reason)) return;
        if (!(Options.AutoRehostAfterKick?.GetBool() ?? false)) return;

        Logger.Warn($"Auto-restart: fatal connection death (reason: {reason}); escalating straight to process restart (skipping in-place rehost)", "AutoRestart");
        Escalate($"fatal disconnect: {reason}", authDeath: IsAuthTokenDeath(reason));
    }

    // 認証失敗ポップアップ検知 (MessageCapture から)。DisconnectReasons を伴わず GenericPopup だけで
    // 「Among Us サーバーで認証できませんでした…」が出るケース (メニューでのホスト不能) を拾う唯一の経路。
    // その場では二度とオンラインに行けないので、番犬経由でプロセスを立て直して EOS ログインからやり直す (穴5)。
    public static void OnAuthFailureMessage(string source, string text)
    {
        if (!(Options.AutoRehostAfterKick?.GetBool() ?? false)) return;

        Logger.Warn($"Auto-restart: authentication-failure message detected [{source}]: {text}", "AutoRestart");
        HealthLog.NoteAnom($"ANOM live kind=authfail src={source}");
        Escalate($"auth-failure popup: {source}", authDeath: true);
    }

    // AutoRehost が全リトライ失敗 (GiveUp) したときに呼ぶ。番犬が居ればプロセス再起動へエスカレーションする。
    // rehost 枯渇は ping/kick タイムアウト側の死なので認証死ではない (EGL 先行再起動はしない)。
    public static void EscalateFromRehostGiveUp()
    {
        Escalate("rehost exhausted", authDeath: false);
    }

    // 再起動エスカレーションの共通コア。呼び出し元 (rehost giveup / 認証死切断 / 認証死ポップアップ) を why に受ける。
    // authDeath=true (EOS トークン死) のときだけ egl-refresh 旗を置き、番犬に 1回目の再起動前の EGL 再起動を促す。
    private static void Escalate(string why, bool authDeath)
    {
        // Windows + 番犬前提。Android や番犬未起動では終了させない (無人締め出し回避)。
        if (!WatchdogLauncher.IsSupported || !WatchdogLauncher.IsRunning)
        {
            Logger.Warn($"Auto-restart: watchdog not running; cannot escalate to full restart (manual restart needed) [{why}]", "AutoRestart");
            HealthLog.NoteAnom("ANOM live kind=restart stage=nowatchdog");
            ShowPopup(GetSafeString("AutoRehost.ManualRestartNeeded", "Cannot recover the connection automatically. Please restart the game."), 12f);
            return;
        }

        // ブート直後の連続終了 (quit-storm) を防ぐ。番犬側の BootGrace/クールダウンと二重の保険。
        float uptime = Time.realtimeSinceStartup;
        if (uptime < MinUptimeSeconds)
        {
            Logger.Warn($"Auto-restart: uptime {uptime:F0}s < {MinUptimeSeconds:F0}s; skipping restart to avoid boot-loop [{why}]", "AutoRestart");
            HealthLog.NoteAnom($"ANOM live kind=restart stage=tooyoung up={uptime:F0}");
            return;
        }

        if (RestartInProgress) return;
        RestartInProgress = true;

        Logger.Warn($"Auto-restart: restarting the game process via watchdog to recover from auth/network death [{why}]", "AutoRestart");
        HealthLog.NoteAnom($"ANOM live kind=restart stage=escalate why=\"{why}\"");

        // 番犬は再起動時に自前でマーカーを置くが、番犬設定に依存しないよう念のためこちらでも置いておく
        // (OnMainMenuStart が一度で消費するので二重でも無害)。
        AutoRehost.EnsureStartupHostMarker();

        // 意図的終了なので番犬に「grace/cooldown 窓を無視して即立て直せ」と伝える (穴2)。番犬が直前に
        // 起動した直後だと grace(150s)/cooldown(120s) に落ちて再launchされず一時的に無人死する空振りを塞ぐ。
        WatchdogLauncher.RequestRestart();

        // 認証死 (EOS トークン失効) 起因のときだけ egl-refresh を要求する (BUG-17)。プレーン再起動は
        // トークン未リフレッシュで必ずブート死し、番犬が 150s の起動猶予を空費してから egl-restart に至る。
        // この旗があると番犬は 1回目の再起動前に EGL をリフレッシュして即復帰する。ping/kick 死では呼ばない。
        if (authDeath) WatchdogLauncher.RequestEglRefresh();

        ShowPopup(GetSafeString("AutoRehost.Restarting", "Cannot recover the connection. Restarting the game to re-host automatically."), QuitDelaySeconds + 2f);

        // ポップアップ表示とマーカー書き込みを着地させてから終了。番犬が「プロセス消失」を検知して立て直す。
        LateTask.New(() =>
        {
            try { Application.Quit(); }
            catch (System.Exception ex) { Logger.Error($"Application.Quit() threw: {ex}", "AutoRestart"); }
        }, QuitDelaySeconds, "AutoRestart.Quit", log: false);

        // ハード終了ベルト (穴3): メインスレッドが固まって Application.Quit コルーチンが着地しない場合でも、
        // バックグラウンドスレッドから強制終了する。番犬がプロセス消失を検知して立て直す。
        // (真の完全ハングは番犬の心拍 stale 検知が正典だが、Escalate まで到達したケースの取りこぼしを防ぐ二重保険)
        ArmHardKillBelt();
    }

    // Quit が着地しなかった時のバックグラウンド強制終了。メインスレッドの停止と独立に動く。
    private static void ArmHardKillBelt()
    {
        var t = new Thread(() =>
        {
            try
            {
                Thread.Sleep((QuitDelaySeconds > 0 ? (int)(QuitDelaySeconds * 1000) : 0) + HardKillAfterSeconds * 1000);
                Process self = Process.GetCurrentProcess();
                if (!self.HasExited)
                {
                    Logger.Warn("Auto-restart: graceful quit did not land in time; hard-killing process (watchdog will relaunch)", "AutoRestart");
                    HealthLog.NoteAnom("ANOM live kind=restart stage=hardkill");
                    self.Kill();
                }
            }
            catch { }
        }) { IsBackground = true, Name = "EndKnotAutoRestartHardKill" };
        t.Start();
    }

    private static void ShowPopup(string text, float seconds)
    {
        if (!HudManager.InstanceExists) return;
        try { HudManager.Instance.ShowPopUp(text); }
        catch { return; }

        LateTask.New(() =>
        {
            try
            {
                DialogueBox dlg = HudManager.Instance != null ? HudManager.Instance.Dialogue : null;
                if (dlg != null) dlg.gameObject.SetActive(false);
            }
            catch { }
        }, seconds, "AutoRestart.DismissPopup", log: false);
    }

    private static string GetSafeString(string key, string fallback)
    {
        try
        {
            string s = Translator.GetString(key);
            return string.IsNullOrEmpty(s) || s.StartsWith("*") ? fallback : s;
        }
        catch { return fallback; }
    }
}
