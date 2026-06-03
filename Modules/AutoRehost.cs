using System;
using System.Reflection;
using EndKnot.Modules.CalamityMenu;
using EndKnot.Patches.CalamityMenu;
using InnerNet;
using UnityEngine;

namespace EndKnot.Modules;

// 配信者向け「自動部屋立て直し」。
// 公式 kick (Hacking) / 通信エラー / タイムアウトでホストがオンライン部屋から切断されたら、
// 一定秒数 (既定 10 秒) 待ってから「同じリージョン・同じ設定」で新しいオンライン部屋を自動で立て直す。
// 立て直し後は EHR 既存の自律ループ (AutoPlayAgain / AutoStart) と LobbyShare の自動再投稿が引き継ぐ。
// 対象は公式・Modded 両方 (公式除外ガードは入れない。ループ保護は AutoRehostMaxAttempts のみ)。認証(EOS)には触れない。
//
// 【設計 — 2026-06-03 v3 / 大規模調査後の作り直し】
// 明示 4 フェーズのステートマシン: WaitClean → OpenDialog → WaitDialog → WaitJoin。
//   ・成功はポーリングではなく **OnGameJoined イベント** (OnGameJoinedPatch → NotifyJoinedNewLobby) で検知。
//     旧 GameId と比較して「別の新しい部屋に入った」ことを確認する。これで「古い部屋の残骸を成功と誤検知」も
//     「部屋は立ったのに成功扱いされずループ継続 → working な部屋を watchdog が破壊」も両方なくなる。
//   ・**アンチチートの真因は Confirm 連打ではなく「前セッションの teardown 中 / scene-connect 中にホスト作成すること」**
//     (実機ログ: 19 Confirm に対し kick は 2 回だけ、いずれも前部屋 OnLobbyDestroyed の 1〜2 秒後の最初の Confirm)。
//     よって AC 対策の本丸は **SETTLE GATE**: 完全にクリーンな MainMenu 状態が SettleSeconds 継続するまでホストしない。
//     1 回だけ Confirm するのは念のためのベルト (本丸ではない)。
//   ・作成は (1) mm.OpenCreateGame() を reflection で試行 (in-scene、scene-connect を避け MultiplayerMapIdFix も通らない)、
//     ダメなら (2) 実機実績のある CalamityButtons.GoToMultiplayer フォールバック。
//   ・リージョン/EHR 設定/ゲームモードは disconnect+ChangeScene を跨いで自動保持される (再適用不要)。MapId(Dleks=3)だけ
//     フォールバック経路で MultiplayerMapIdFix にリセットされうるので、念のため捕捉して再適用する。
public static class AutoRehost
{
    private enum Phase
    {
        WaitClean,  // クリーンな MainMenu を待ち、SettleSeconds 安定するまで待機 (AC 対策の本丸)
        OpenDialog, // 作成ダイアログを開く
        WaitDialog, // ダイアログが active になるのを待ち、1 回だけ Confirm
        WaitJoin    // 新部屋への join を待つ (成功は NotifyJoinedNewLobby イベントで来る)
    }

    private static bool _pending;
    private static int _attempts;
    private static int _seq;        // 世代トークン。古い Tick / stabilize を無効化する
    private static Phase _phase;
    private static float _deadline; // この試行のタイムアウト時刻
    private static float _cleanSince; // クリーン状態が始まった時刻 (0=未クリーン)
    private static float _openedAt; // ダイアログを開いた時刻 (DialogTimeout 用)
    private static float _nextWaitLogAt; // WaitClean 診断ログのスロットル
    private static int _oldGameId;  // 切断前の GameId (新部屋判定用)
    private static byte _oldMapId;  // 切断前の MapId (Dleks 保持用)

    // ===== tunable =====
    private const float PollInterval = 0.5f;
    private const float WatchdogSeconds = 45f;  // 新部屋に到達できなければリトライ
    private const float SettleSeconds = 3f;     // クリーン状態がこの秒数続いてからホスト (AC 対策の本丸)
    private const float DialogTimeout = 8f;     // OpenCreateGame が効かない時にフォールバックへ
    private const float StabilizeSeconds = 60f; // 新部屋がこの秒数もてば成功確定 → attempts リセット
    private const float SuccessPopupSeconds = 6f;
    // ===================

    private static int MaxAttempts => Mathf.Max(1, Options.AutoRehostMaxAttempts?.GetInt() ?? 3);

    // ── 切断検知 (ExitGamePatch.Prefix から)。Prefix 時点なら AmHost / NetworkMode / GameId がまだ正しい ──
    public static void OnDisconnect(DisconnectReasons reason)
    {
        if (!(Options.AutoRehostAfterKick?.GetBool() ?? false)) return;

        if (reason is DisconnectReasons.ExitGame or DisconnectReasons.Banned or DisconnectReasons.IncorrectVersion)
        {
            if (_pending) Cancel("intentional/unrecoverable disconnect");
            return;
        }

        bool wasHostingOnline = AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost && GameStates.IsOnlineGame;
        if (!wasHostingOnline) return;

        if (_pending) return;

        _pending = true;
        OfficialServerNotice.SuppressWhileRehosting = true;

        _oldGameId = AmongUsClient.Instance.GameId;
        try { _oldMapId = GameOptionsManager.Instance.normalGameHostOptions.MapId; }
        catch { _oldMapId = 0; }

        float delay = Mathf.Max(1, Options.AutoRehostDelay?.GetInt() ?? 10);
        Logger.Info($"Host disconnected (reason: {reason}). Auto-rehost in {delay:N0}s (attempt {_attempts + 1}/{MaxAttempts}); oldGameId={_oldGameId} oldMapId={_oldMapId}", "AutoRehost");
        LateTask.New(StartAttempt, delay, "AutoRehost.StartAttempt", log: false);
    }

    // ── 待機満了 → この試行を開始。リトライの起点でもある ──
    private static void StartAttempt()
    {
        if (!_pending) return;

        _attempts++;
        if (_attempts > MaxAttempts)
        {
            GiveUp();
            return;
        }

        _seq++;
        int mySeq = _seq;
        _phase = Phase.WaitClean;
        _cleanSince = 0f;
        _openedAt = 0f;
        _nextWaitLogAt = 0f;
        _deadline = Time.realtimeSinceStartup + WatchdogSeconds;

        Logger.Info($"Auto-rehost attempt {_attempts}/{MaxAttempts}: leaving to MainMenu", "AutoRehost");
        try { SceneChanger.ChangeScene("MainMenu"); }
        catch (Exception ex) { Logger.Warn($"ChangeScene(MainMenu) failed: {ex.Message}", "AutoRehost"); }

        LateTask.New(() => Tick(mySeq), 1f, "AutoRehost.Tick", log: false);
    }

    private static void Tick(int mySeq)
    {
        if (!_pending || _seq != mySeq) return; // 完了 or 世代切替

        if (Time.realtimeSinceStartup > _deadline)
        {
            Logger.Warn($"Auto-rehost: attempt {_attempts} timed out (phase {_phase}); retrying", "AutoRehost");
            StartAttempt();
            return;
        }

        try { DriveOneStep(); }
        catch (Exception ex) { Logger.Warn($"Auto-rehost tick error: {ex.Message}", "AutoRehost"); }

        if (!_pending || _seq != mySeq) return; // 成功 / リトライで世代が変わった
        LateTask.New(() => Tick(mySeq), PollInterval, "AutoRehost.Tick", log: false);
    }

    private static void DriveOneStep()
    {
        float now = Time.realtimeSinceStartup;

        switch (_phase)
        {
            case Phase.WaitClean:
            {
                // 完全にクリーンな MainMenu か? (どれも active-only / singleton 判定で zombie を拾わない)
                bool notJoined = GameStates.IsNotJoined;
                bool lobbyNull = LobbyBehaviour.Instance == null;
                bool atMenu = UnityEngine.Object.FindObjectOfType<MainMenuManager>() != null;
                bool noMatchmaking = UnityEngine.Object.FindObjectOfType<MMOnlineManager>() == null;
                bool clean = notJoined && lobbyNull && atMenu && noMatchmaking;

                if (!clean)
                {
                    _cleanSince = 0f;
                    // 自己診断: WaitClean で詰まると watchdog timeout と区別がつかないので、どの条件が未達か出す
                    // (特に MMOnlineManager が MainMenu に居座ると永久に clean にならない可能性 — 実機で要確認)
                    if (now >= _nextWaitLogAt)
                    {
                        _nextWaitLogAt = now + 2f;
                        Logger.Info($"Auto-rehost WaitClean: not clean yet (IsNotJoined={notJoined} LobbyNull={lobbyNull} MainMenu={atMenu} NoMatchmaking={noMatchmaking})", "AutoRehost");
                    }
                    return;
                }

                if (_cleanSince == 0f)
                {
                    _cleanSince = now;
                    Logger.Info($"Auto-rehost: clean MainMenu reached; settling {SettleSeconds:N0}s before host", "AutoRehost");
                    return;
                }
                if (now - _cleanSince < SettleSeconds) return; // SETTLE GATE (AC 対策の本丸)

                Logger.Info("Auto-rehost: settled -> proceeding to create", "AutoRehost");
                ReapplyMapId();
                _phase = Phase.OpenDialog;
                return;
            }

            case Phase.OpenDialog:
            {
                MainMenuManager mm = UnityEngine.Object.FindObjectOfType<MainMenuManager>();
                if (mm == null) { _phase = Phase.WaitClean; _cleanSince = 0f; return; }

                if (TryOpenCreateGame(mm))
                    Logger.Info("Auto-rehost: opening create dialog via OpenCreateGame()", "AutoRehost");
                else
                {
                    Logger.Info("Auto-rehost: OpenCreateGame unavailable; using GoToMultiplayer", "AutoRehost");
                    GoToMultiplayer(mm);
                }

                _openedAt = now;
                _phase = Phase.WaitDialog;
                return;
            }

            case Phase.WaitDialog:
            {
                CreateGameOptions cgo = UnityEngine.Object.FindObjectOfType<CreateGameOptions>(); // active-only
                if (cgo != null && cgo.isActiveAndEnabled)
                {
                    Logger.Info($"Auto-rehost: CreateGameOptions active -> Confirm() once (region={Utils.GetRegionName()} mapId={SafeMapId()})", "AutoRehost");
                    try { cgo.Confirm(); }
                    catch (Exception ex) { Logger.Warn($"CreateGameOptions.Confirm() failed: {ex.Message}", "AutoRehost"); }
                    _phase = Phase.WaitJoin; // 二度と Confirm しない。成功は OnGameJoined で来る
                    return;
                }

                // OpenCreateGame が握り潰された場合 (Calamity の VanillaSuppressor 等) は実績ある経路へ
                if (now - _openedAt > DialogTimeout)
                {
                    Logger.Warn("Auto-rehost: create dialog never activated; falling back to GoToMultiplayer", "AutoRehost");
                    MainMenuManager mm = UnityEngine.Object.FindObjectOfType<MainMenuManager>();
                    if (mm != null) GoToMultiplayer(mm);
                    _openedAt = now; // ダイアログ待ちを再武装
                }
                return;
            }

            case Phase.WaitJoin:
                // 何もしない。成功は NotifyJoinedNewLobby (OnGameJoined) で来る。来なければ watchdog がリトライ。
                return;
        }
    }

    // mm.OpenCreateGame() を reflection で呼ぶ (リポジトリ未参照のため直書きすると compile risk)。
    // 存在すれば in-scene でダイアログを開き、matchmaking scene-connect (kick と相関) と MultiplayerMapIdFix を回避できる。
    private static bool TryOpenCreateGame(MainMenuManager mm)
    {
        try
        {
            MethodInfo m = typeof(MainMenuManager).GetMethod("OpenCreateGame", BindingFlags.Public | BindingFlags.Instance);
            if (m == null) return false;
            m.Invoke(mm, null);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"OpenCreateGame reflection failed: {ex.Message}", "AutoRehost");
            return false;
        }
    }

    private static void GoToMultiplayer(MainMenuManager mm)
    {
        try
        {
            if (CalamityMenuState.Active)
                CalamityButtons.GoToMultiplayer(mm);
            else
                mm.OpenGameModeMenu();
        }
        catch (Exception ex) { Logger.Warn($"GoToMultiplayer failed: {ex.Message}", "AutoRehost"); }
    }

    // 切断前の MapId を再適用 (リージョン/設定/ゲームモードは自動保持だが、Dleks=3 だけはフォールバック経路で
    // MultiplayerMapIdFix にリセットされうるので保険)。
    private static void ReapplyMapId()
    {
        try
        {
            var opts = GameOptionsManager.Instance.normalGameHostOptions;
            if (opts != null && opts.MapId != _oldMapId)
            {
                opts.MapId = _oldMapId;
                GameOptionsManager.Instance.SaveNormalHostOptions();
            }
        }
        catch (Exception ex) { Logger.Warn($"ReapplyMapId failed: {ex.Message}", "AutoRehost"); }
    }

    private static byte SafeMapId()
    {
        try { return GameOptionsManager.Instance.normalGameHostOptions.MapId; }
        catch { return 0; }
    }

    // ── 成功検知 (OnGameJoinedPatch.Postfix から)。OnGameJoined は本物の join で 1 回だけ発火し、
    //    破棄済みの旧部屋では再発火しないので誤検知しない。GameId が旧と違う = 別の新部屋に入った ──
    public static void NotifyJoinedNewLobby()
    {
        if (!_pending) return;
        AmongUsClient c = AmongUsClient.Instance;
        if (c == null) return;
        if (!(c.AmHost && GameStates.IsOnlineGame)) return;
        if (c.GameId == _oldGameId) return; // 新しい部屋ではない (stale ガード)
        Success();
    }

    private static void Success()
    {
        Logger.Info($"Auto-rehost: new lobby reached (attempt {_attempts}, gameId={AmongUsClient.Instance?.GameId}). Handing off to existing auto-start loop", "AutoRehost");
        _pending = false;
        _seq++; // 飛んでいる Tick を無効化
        OfficialServerNotice.SuppressWhileRehosting = false;
        ShowAutoDismissPopup(GetSafeString("AutoRehost.Success", "Lobby was automatically re-hosted."), SuccessPopupSeconds);

        // すぐ attempts を 0 にすると「成功 → 即再 kick」のループを検知できないので、一定時間もったらリセット
        int genAtSuccess = _seq;
        LateTask.New(() =>
        {
            if (_seq == genAtSuccess && !_pending) _attempts = 0;
        }, StabilizeSeconds, "AutoRehost.Stabilize", log: false);
    }

    private static void GiveUp()
    {
        Logger.Warn($"Auto-rehost: reached max attempts ({MaxAttempts}); stopping", "AutoRehost");
        ShowAutoDismissPopup(GetSafeString("AutoRehost.GaveUp", "Auto-rehost stopped after repeated failures."), 10f);
        ResetState();
    }

    private static void Cancel(string why)
    {
        Logger.Info($"Auto-rehost: cancelled ({why})", "AutoRehost");
        ResetState();
    }

    private static void ResetState()
    {
        _pending = false;
        _cleanSince = 0f;
        _openedAt = 0f;
        _attempts = 0;
        _seq++; // 飛んでいる Tick / stabilize を無効化
        OfficialServerNotice.SuppressWhileRehosting = false;
    }

    // 自動で消えるポップアップ (無人ホスト運用でモーダルが残り続けないように)。
    // ShowPopUp は HudManager.Instance.Dialogue (型は DialogueBox) を使う共有ポップアップなので、N 秒後に閉じる。
    private static void ShowAutoDismissPopup(string text, float seconds)
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
        }, seconds, "AutoRehost.DismissPopup", log: false);
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
