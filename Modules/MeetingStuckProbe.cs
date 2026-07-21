using System.Collections.Generic;
using UnityEngine;

namespace EndKnot.Modules;

// 会議取り残し検知の計器 (ホストローカル・送信ゼロ)。
// 会議開始RPCを取りこぼしたクライアントは MeetingHud を開けずゲームプレイ状態のまま移動を続けるため、
// 「会議中 (Animating 以降) に位置更新が連続して届くプレイヤー」= 会議に入れていない、をログに残す。
// 背景: BUG-20260721-09 (初手会議で取り残され→EAC誤BAN)。初手以外でも発生報告があり、発生率と対象の
// 端末/タイミングを取るのが目的。検知しても介入はしない (rescue は別途設計判断)。
public static class MeetingStuckProbe
{
    private const float SampleInterval = 1f;
    private const float MinStep = 0.03f; // これ未満は静止揺らぎ扱い
    private const float MaxStep = 3f; // これ超は host 側 TP (SnapTo) 扱いで移動連続にカウントしない
    private const int StreakToFlag = 3; // 連続3サンプル (約3秒) 歩き続けたら取り残しと判定

    private static MeetingHud _current;
    private static float _nextSampleTime;
    private static readonly Dictionary<byte, Vector2> LastPos = [];
    private static readonly Dictionary<byte, int> MoveStreak = [];
    private static readonly HashSet<byte> Flagged = [];

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

                    if (streak >= StreakToFlag && Flagged.Add(pc.PlayerId))
                        Logger.Warn($"{pc.GetRealName()} (id {pc.PlayerId}, modded={pc.IsModdedClient()}, platform={pc.GetClient()?.PlatformData?.Platform}) keeps moving during meeting (state={meetingHud.state}) — likely never entered MeetingHud", "MeetingStuckProbe");
                }
                else
                    MoveStreak[pc.PlayerId] = 0;
            }

            LastPos[pc.PlayerId] = pos;
        }
    }
}
