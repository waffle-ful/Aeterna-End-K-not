using System.Collections.Generic;
using InnerNet;
using UnityEngine;

namespace EndKnot.Modules;

// 参加直後の自動再入室 (客側)。
// 公式サーバーでは、送信内容と関係なく「接続から十数秒」で Hacking 切断されることがまれにある。
// 客がロビー参加直後・試合開始前にこれを受けたら、同じ部屋コードへ 1 回だけ自動で入り直す。
// ホストの切断は AutoRehost が扱う (こちらはホストでは発火しない)。
// 同じ部屋への再入室は 1 プロセスにつき 1 回まで。本物の検知で入り直しを繰り返さないため。
public static class AutoRejoin
{
    private const float MaxLobbyAgeSeconds = 25f;
    private const float FirstTryDelaySeconds = 1f;
    private const float SettleSeconds = 3f;
    private const float PollSeconds = 0.5f;
    private const float GiveUpSeconds = 25f;
    private const float SuccessPopupSeconds = 6f;

    private static readonly HashSet<int> TriedGameIds = [];

    private static float _joinedAt = -1f;
    private static bool _guestOnlineLatch;
    private static int _latchedGameId;
    private static int _pendingGameId = -1;
    private static float _pendingSince;
    private static bool _joinRequested;
    private static float _cleanSince;
    private static int _seq;

    // OnGameJoinedPatch.Postfix から。AmHost / GameId が確定している瞬間に記録しておく。
    public static void OnGameJoined()
    {
        try
        {
            AmongUsClient c = AmongUsClient.Instance;
            if (c == null) return;

            _joinedAt = Time.realtimeSinceStartup;
            _guestOnlineLatch = !c.AmHost && GameStates.IsOnlineGame;
            _latchedGameId = c.GameId;

            if (_pendingGameId != -1 && c.GameId == _pendingGameId) Success();
        }
        catch { }
    }

    // ExitGamePatch.Prefix から (切断処理の前なので GameId / NetworkMode がまだ正しい)。
    // 引き受けたら true。
    public static bool OnDisconnect(DisconnectReasons reason)
    {
        try
        {
            // 切断のたびに参加記録を消費する。残すと、次の部屋の OnGameJoined より前に切られた時に前の部屋へ入り直してしまう。
            bool wasGuestOnline = _guestOnlineLatch;
            _guestOnlineLatch = false;

            if (reason != DisconnectReasons.Hacking) return false;
            if (!wasGuestOnline || _pendingGameId != -1) return false;

            AmongUsClient c = AmongUsClient.Instance;
            if (c == null || c.AmHost) return false;
            if (GameStates.InGame) return false;
            if (!Utils.IsOfficialServer()) return false;

            float age = _joinedAt < 0f ? float.MaxValue : Time.realtimeSinceStartup - _joinedAt;
            if (age > MaxLobbyAgeSeconds) return false;

            int gameId = _latchedGameId;
            if (!TriedGameIds.Add(gameId)) return false;

            _pendingGameId = gameId;
            _pendingSince = Time.realtimeSinceStartup;
            _joinRequested = false;
            _cleanSince = 0f;
            int seq = ++_seq;
            OfficialServerNotice.SuppressWhileRehosting = true;

            Logger.Info($"Guest kicked (Hacking) {age:0.0}s after joining; rejoining {GameCode.IntToGameName(gameId)} once", "AutoRejoin");
            HealthLog.NoteAnom($"ANOM live kind=rejoin stage=start lobbyAge={age:0} isLobby={GameStates.IsLobby}");
            LateTask.New(() => Tick(seq), FirstTryDelaySeconds, "AutoRejoin.Tick", log: false);
            return true;
        }
        catch { return false; }
    }

    private static void Tick(int seq)
    {
        if (seq != _seq || _pendingGameId == -1) return;

        try
        {
            if (Time.realtimeSinceStartup - _pendingSince > GiveUpSeconds)
            {
                Logger.Warn($"Rejoin to {GameCode.IntToGameName(_pendingGameId)} did not complete; giving up", "AutoRejoin");
                HealthLog.NoteAnom("ANOM live kind=rejoin stage=giveup");
                Clear();
                return;
            }

            if (!_joinRequested && IsSettledAtMainMenu())
            {
                _joinRequested = true;
                AmongUsClient.Instance.StartCoroutine(AmongUsClient.Instance.CoFindGameInfoFromCodeAndJoin(_pendingGameId));
                Logger.Info($"Rejoin requested: {GameCode.IntToGameName(_pendingGameId)}", "AutoRejoin");
            }
        }
        catch (System.Exception e)
        {
            Utils.ThrowException(e);
            Clear();
            return;
        }

        LateTask.New(() => Tick(seq), PollSeconds, "AutoRejoin.Tick", log: false);
    }

    // 前セッションの後片付け中に接続を始めると公式鯖に Hacking で切られるので、
    // 完全にクリーンなメインメニューが SettleSeconds 続いてから入る (AutoRehost と同じ条件)。
    private static bool IsSettledAtMainMenu()
    {
        bool clean = GameStates.IsNotJoined
                     && LobbyBehaviour.Instance == null
                     && UnityEngine.Object.FindObjectOfType<MainMenuManager>() != null
                     && UnityEngine.Object.FindObjectOfType<MMOnlineManager>() == null;

        float now = Time.realtimeSinceStartup;
        if (!clean)
        {
            _cleanSince = 0f;
            return false;
        }

        if (_cleanSince == 0f) _cleanSince = now;
        return now - _cleanSince >= SettleSeconds;
    }

    private static void Success()
    {
        Logger.Info($"Rejoined {GameCode.IntToGameName(_pendingGameId)}", "AutoRejoin");
        HealthLog.NoteAnom("ANOM live kind=rejoin stage=success");
        Clear();

        if (!HudManager.InstanceExists) return;
        try { HudManager.Instance.ShowPopUp(Translator.GetString("AutoRejoin.Success")); }
        catch { return; }

        LateTask.New(() =>
        {
            try
            {
                DialogueBox dlg = HudManager.Instance != null ? HudManager.Instance.Dialogue : null;
                if (dlg != null) dlg.gameObject.SetActive(false);
            }
            catch { }
        }, SuccessPopupSeconds, "AutoRejoin.DismissPopup", log: false);
    }

    private static void Clear()
    {
        _pendingGameId = -1;
        _joinRequested = false;
        _seq++;
        OfficialServerNotice.SuppressWhileRehosting = false;
    }
}
