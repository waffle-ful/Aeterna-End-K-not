using System;
using System.Collections.Generic;
using Hazel;
using InnerNet;
using UnityEngine;

namespace EndKnot.Modules;

// 会議取り残しの検知 + rescue (BUG-20260721-09)。
// 会議開始RPCを取りこぼしたクライアントは MeetingHud を開けずゲームプレイ状態のまま移動を続けるため、
// 「会議中 (Animating 以降) に位置更新が連続して届くプレイヤー」= 会議に入れていない、と判定する。
// 検知したら当該クライアント1人宛てに StartMeeting RPC を再送する (1会議1人1回)。位置更新が流れて
// いる = 受信路は生きているので、再送で MeetingHud を開ける見込みが高い。rescue 後も歩き続けたら
// 失敗ログを1回だけ出す (次配信の効果測定用)。
public static class MeetingStuckProbe
{
    private const float SampleInterval = 1f;
    private const float MinStep = 0.03f; // これ未満は静止揺らぎ扱い
    private const float MaxStep = 3f; // これ超は host 側 TP (SnapTo) 扱いで移動連続にカウントしない
    private const int StreakToFlag = 3; // 連続3サンプル (約3秒) 歩き続けたら取り残しと判定

    private static MeetingHud _current;
    private static float _nextSampleTime;
    private static byte _reporterPlayerId = byte.MaxValue;
    private static byte _targetPlayerId = byte.MaxValue;
    private static readonly Dictionary<byte, Vector2> LastPos = [];
    private static readonly Dictionary<byte, int> MoveStreak = [];
    private static readonly HashSet<byte> Flagged = [];
    private static readonly HashSet<byte> Rescued = [];
    private static readonly HashSet<byte> RescueFailed = [];

    // AfterReportTasks (全会議の関所) から毎会議呼ばれる。rescue 再送時に本物の通報者/対象を使うための記録
    public static void OnMeetingStart(PlayerControl reporter, NetworkedPlayerInfo target)
    {
        _reporterPlayerId = reporter != null ? reporter.PlayerId : byte.MaxValue;
        _targetPlayerId = target != null ? target.PlayerId : byte.MaxValue;
    }

    public static void Update(MeetingHud meetingHud)
    {
        if (!AmongUsClient.Instance.AmHost || meetingHud == null) return;

        if (_current != meetingHud)
        {
            _current = meetingHud;
            _nextSampleTime = Time.time + SampleInterval;
            LastPos.Clear();
            MoveStreak.Clear();
            Flagged.Clear();
            Rescued.Clear();
            RescueFailed.Clear();
        }

        if (meetingHud.state is MeetingHud.VoteStates.Animating or MeetingHud.VoteStates.Results or MeetingHud.VoteStates.Proceeding) return;
        if (Time.time < _nextSampleTime) return;

        _nextSampleTime = Time.time + SampleInterval;

        foreach (PlayerControl pc in Main.AllAlivePlayerControls)
        {
            if (pc.IsHost() || pc.PlayerId >= 200) continue;

            Vector2 pos = pc.Pos();

            if (LastPos.TryGetValue(pc.PlayerId, out Vector2 last))
            {
                float step = (pos - last).magnitude;

                if (step is > MinStep and < MaxStep)
                {
                    int streak = MoveStreak.GetValueOrDefault(pc.PlayerId) + 1;
                    MoveStreak[pc.PlayerId] = streak;

                    if (streak >= StreakToFlag)
                    {
                        if (Flagged.Add(pc.PlayerId))
                        {
                            Logger.Warn($"{pc.GetRealName()} (id {pc.PlayerId}, modded={pc.IsModdedClient()}, platform={pc.GetClient()?.PlatformData?.Platform}) keeps moving during meeting (state={meetingHud.state}) — likely never entered MeetingHud", "MeetingStuckProbe");
                            TryRescue(pc);
                            MoveStreak[pc.PlayerId] = 0; // rescue 後の再観測 (止まらなければ失敗と判定)
                        }
                        else if (Rescued.Contains(pc.PlayerId) && RescueFailed.Add(pc.PlayerId))
                            Logger.Warn($"{pc.GetRealName()} (id {pc.PlayerId}) still moving after StartMeeting resend — rescue failed", "MeetingStuckProbe");
                    }
                }
                else
                    MoveStreak[pc.PlayerId] = 0;
            }

            LastPos[pc.PlayerId] = pos;
        }
    }

    // 取り残されクライアント1人宛てに StartMeeting RPC を再送する。通報者が既に切断済みの場合は
    // クライアント側で RPC の乗り物 (NetId) が despawn 済みで届かないため、ホストの緊急会議 (255) に落とす
    private static void TryRescue(PlayerControl pc)
    {
        try
        {
            ClientData client = pc.GetClient();
            if (client == null) return;

            PlayerControl carrier = Utils.GetPlayerById(_reporterPlayerId);
            byte targetByte = _targetPlayerId;

            if (carrier == null || carrier.Data == null || carrier.Data.Disconnected)
            {
                carrier = PlayerControl.LocalPlayer;
                targetByte = byte.MaxValue;
            }

            MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(carrier.NetId, (byte)RpcCalls.StartMeeting, SendOption.Reliable, client.Id);
            writer.Write(targetByte);
            AmongUsClient.Instance.FinishRpcImmediately(writer);

            Rescued.Add(pc.PlayerId);
            Logger.Warn($"resent StartMeeting (reporter={carrier.GetRealName()}, target={targetByte}) to {pc.GetRealName()} (client {client.Id})", "MeetingStuckProbe");
        }
        catch (Exception e) { Utils.ThrowException(e); }
    }
}
