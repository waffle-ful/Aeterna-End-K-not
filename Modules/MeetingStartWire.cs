using System.Collections;
using Hazel;
using UnityEngine;

namespace EndKnot.Modules;

// 会議開始 RPC (StartMeeting) の TOHK パリティ配送 (2026-07-22)。
// TOHK はホストを先に会議へ入れ、0.2 秒後に StartMeeting RPC をワイヤ直送する
// (../TOHK/Patches/PlayerContorols/ReportDeadBodyPatch.cs:296-311)。うちは vanilla の
// RpcStartMeeting がグローバル 25/s レートゲート FIFO の最後尾に載るため、会議直前の
// バースト (NotifyRoles/名前再送) の後ろで数秒遅れ、遅いクライアントは会議 UI が出ない
// まま取り残される (BUG-20260721-09 系)。ここでは v4 (OnGameStartedPatch) と同じ
// 「ドレイン待ち→直送窓」でワイヤ直行を保証する。ドレインがタイムアウトした場合は
// 追い越し (順序逆転 = 既知の暗転原因型) を作らないよう従来どおりゲート経由で送る。
// Rollback bit: create EndKnot_DATA/disable_meeting_direct_window.txt (会議毎に素読み・再ビルド不要)
public static class MeetingStartWire
{
    // 2026-07-23: ワイヤ方式の初実戦2ゲームで、mod起点会議のクローズ瞬間に公式鯖 Hacking キックが2連発
    // (BUG-20260723-01)。機序は未確定だが唯一の一貫相関のため、既定を従来の vanilla RpcStartMeeting 経路へ
    // 戻し、本ワイヤ方式は EndKnot_DATA/enable_meeting_wire.txt を置いたときだけ有効 (A/B 用・再起動不要)。
    // 有効化すると会議取り残し (BUG-20260721-09) の修復が戻る代わりにキック疑いを再導入する。
    private static bool _wireEnabled;
    private static float _wireCheckedAt = float.MinValue;

    public static bool WireEnabled
    {
        get
        {
            if (Time.realtimeSinceStartup - _wireCheckedAt > 30f)
            {
                _wireCheckedAt = Time.realtimeSinceStartup;
                bool exists;
                try { exists = System.IO.File.Exists($"{Main.DataPath}/EndKnot_DATA/enable_meeting_wire.txt"); }
                catch { exists = false; }
                if (exists != _wireEnabled) Logger.Warn($"Meeting start wire {(exists ? "ENABLED (enable_meeting_wire.txt present) — host enters meeting first, RPC wired after drain" : "disabled — vanilla RpcStartMeeting flow")}", "MeetingStartWire");
                _wireEnabled = exists;
            }

            return _wireEnabled;
        }
    }

    /// <summary>ホスト側の会議入り (AssignSelf/OpenMeetingRoom/StartMeeting) を済ませた直後に呼ぶ。</summary>
    public static void SendStartMeeting(PlayerControl reporter, NetworkedPlayerInfo target)
    {
        if (!AmongUsClient.Instance.AmHost || !reporter) return;
        Main.Instance.StartCoroutine(CoSendStartMeeting(reporter, target?.PlayerId ?? byte.MaxValue));
    }

    private static IEnumerator CoSendStartMeeting(PlayerControl reporter, byte targetId)
    {
        // TOHK パリティ: ホストの会議入りから 0.2 秒後に送出
        yield return new WaitForSecondsRealtime(0.2f);

        if (AmongUsClient.Instance.IsGameOver || GameStates.IsLobby || !reporter) yield break;

        bool direct = !DisableMeetingDirectWindow();
        float drainStart = Time.realtimeSinceStartup;

        if (!direct)
            Logger.Warn("disable_meeting_direct_window.txt present: sending StartMeeting gated", "BlackoutProbe");
        else
        {
            while ((PacketRateGate.PendingCount > 0 || DataFlagRateLimiter.PendingCount > 0) && Time.realtimeSinceStartup - drainStart < 4f)
            {
                // ドレイン中に廃村/切断/reporter 破棄が起きたら送らない (破棄済み NetId への RPC は
                // anti-cheat に Hacking として弾かれ得る)
                if (!reporter || AmongUsClient.Instance.IsGameOver || GameStates.IsLobby) yield break;

                yield return null;
            }

            if (PacketRateGate.PendingCount > 0 || DataFlagRateLimiter.PendingCount > 0)
            {
                direct = false;
                Logger.Error($"BlackoutProbe: meeting-start drain timed out after 4s (gateQueue={PacketRateGate.PendingCount}, dataQueue={DataFlagRateLimiter.PendingCount}) — sending StartMeeting gated", "BlackoutProbe");
            }
        }

        if (!reporter || AmongUsClient.Instance.IsGameOver || GameStates.IsLobby) yield break;

        PacketRateGate.StartWindowBypass = direct;
        DataFlagRateLimiter.StartWindowBypass = direct;

        try
        {
            CustomRpcSender.Create("MeetingStartWire", SendOption.Reliable)
                .AutoStartRpc(reporter.NetId, RpcCalls.StartMeeting)
                .Write(targetId)
                .EndRpc()
                .SendMessage();

            Logger.Info($"BlackoutProbe: StartMeeting wired {(direct ? "direct" : "gated")} +{Time.realtimeSinceStartup - drainStart:F2}s after drain start (gateQueue={PacketRateGate.PendingCount})", "BlackoutProbe");
        }
        finally
        {
            PacketRateGate.StartWindowBypass = false;
            DataFlagRateLimiter.StartWindowBypass = false;
        }
    }

    private static bool DisableMeetingDirectWindow()
    {
        try { return System.IO.File.Exists($"{Main.DataPath}/EndKnot_DATA/disable_meeting_direct_window.txt"); }
        catch { return false; }
    }
}
