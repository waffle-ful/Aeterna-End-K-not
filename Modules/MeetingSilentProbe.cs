using System.Collections.Generic;

namespace EndKnot.Modules;

// 会議UI破綻検知の計器 (ホストローカル・送信ゼロ)。
// BUG-20260725-05: 会議には入れた (チャットは送れる) のに投票グリッドが構築されず投票できないクラスは、
// 移動ベースの MeetingStuckProbe では原理的に検知できない (会議に入れた時点で移動はロックされるため)。
// 判別器 = 「生存していて、会議中にチャットは送れているのに、CastVote が一度もホストに届かない」。
// チャットが送れている = 画面の前に居て操作意思がある、なので「単に投票しない人」の交絡をかなり削れる。
// 検知のみで介入はしない (rescue は別途設計判断)。
public static class MeetingSilentProbe
{
    private static MeetingHud _current;
    private static readonly HashSet<byte> Voted = [];
    private static readonly Dictionary<byte, int> ChatCount = [];

    public static void Update(MeetingHud meetingHud)
    {
        if (!AmongUsClient.Instance.AmHost || meetingHud == null || _current == meetingHud) return;

        _current = meetingHud;
        Voted.Clear();
        ChatCount.Clear();
    }

    // CastVote RPC がホストに届いた事実 = そのクライアントの投票UIは生きている (取り消し/無効票でも可)
    public static void OnCastVote(byte srcPlayerId)
    {
        if (_current != null) Voted.Add(srcPlayerId);
    }

    public static void OnChat(PlayerControl player)
    {
        if (_current == null || player == null) return;

        ChatCount[player.PlayerId] = ChatCount.GetValueOrDefault(player.PlayerId) + 1;
    }

    public static void OnMeetingEnd()
    {
        if (_current == null) return;

        _current = null;

        if (!AmongUsClient.Instance.AmHost) return;

        int alive = 0, voted = 0;
        List<string> suspects = [];
        List<string> silent = [];

        foreach (PlayerControl pc in Main.AllAlivePlayerControls)
        {
            if (pc.IsHost() || pc.PlayerId >= 200) continue;

            alive++;

            if (Voted.Contains(pc.PlayerId))
            {
                voted++;
                continue;
            }

            int chats = ChatCount.GetValueOrDefault(pc.PlayerId);
            string desc = $"{pc.GetRealName()} (id {pc.PlayerId}, modded={pc.IsModdedClient()}, platform={pc.GetClient()?.PlatformData?.Platform}, chats={chats})";

            if (chats > 0) suspects.Add(desc);
            else silent.Add(desc);
        }

        Logger.Info($"meeting summary: alive={alive} voted={voted} chat-no-vote={suspects.Count} silent-no-vote={silent.Count}", "MeetingSilentProbe");

        foreach (string s in suspects)
            Logger.Warn($"{s} sent chat during meeting but never cast a vote — likely broken vote UI (BUG-20260725-05 class)", "MeetingSilentProbe");

        if (silent.Count > 0)
            Logger.Info($"no-vote & no-chat (indeterminate, AFK?): {string.Join(", ", silent)}", "MeetingSilentProbe");
    }
}
