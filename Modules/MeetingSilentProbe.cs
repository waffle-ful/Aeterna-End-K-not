using System.Collections.Generic;

namespace EndKnot.Modules;

// 会議UI破綻検知の計器 (ホストローカル・送信ゼロ)。
// 会議には入れた (チャットは送れる) のに投票グリッドが構築されず投票できないクラスは、
// 移動ベースの MeetingStuckProbe では原理的に検知できない (会議に入れた時点で移動はロックされるため)。
// 判別器 = 「生存していて、会議中にチャットは送れているのに、CastVote が一度もホストに届かない」。
// チャットが送れている = 画面の前に居て操作意思がある、なので「単に投票しない人」の交絡をかなり削れる。
// 検知のみで介入はしない (rescue は別途設計判断)。
//
// 【2026-07-28 判別器修正】07-27 の実出力 77 会議の実読で、素の chat-no-vote は
// 「会議が投票時間を使い切った」とほぼ同値のトートロジーだと判明 (全員投票時のみ会議が早期終了する
// ため構造的に交絡する)。3点を追加:
//   (a) Notvoter (投票不能アドオン) 保持者を検知母集団から除外
//   (b) 会議が時間切れ終了したか (timedOut) と経過秒を summary に記録
//   (c) 「前の会議では投票が届いていたのに今回チャットのみ」= regressed を最強シグナルとして別掲
//       (単に投票しない人は前会議でも投票していないので、回帰だけが UI 破綻を素直に指す)
public static class MeetingSilentProbe
{
    private static MeetingHud _current;
    private static readonly HashSet<byte> Voted = [];
    private static readonly Dictionary<byte, int> ChatCount = [];

    // 前会議の観測 (回帰判定用)。ゲームを跨いだ持ち越しは Update() の FirstMeeting 検知でクリアする。
    private static readonly HashSet<byte> PrevAlive = [];
    private static readonly HashSet<byte> PrevVoted = [];

    private static float _meetingStart;

    // Results フェーズ遷移瞬間の残り投票秒 (-1 = 未到達)。OnDestroy 時点の VotingTimeLeft は
    // Results/Proceeding の 5 秒カウントダウンに常時上書きされるため使えない (2026-07-28 確認)。
    private static int _votingTimeLeftAtResults = -1;

    public static void Update(MeetingHud meetingHud)
    {
        if (!AmongUsClient.Instance.AmHost || meetingHud == null || _current == meetingHud) return;

        _current = meetingHud;
        Voted.Clear();
        ChatCount.Clear();
        _meetingStart = UnityEngine.Time.realtimeSinceStartup;
        _votingTimeLeftAtResults = -1;

        // 新しいゲームの1回目の会議 → 前会議の記録は前ゲームのもの。回帰判定に使ってはいけない。
        if (MeetingStates.FirstMeeting)
        {
            PrevAlive.Clear();
            PrevVoted.Clear();
        }
    }

    // MeetingHudUpdatePatch.Prefix の Results 分岐冒頭から毎フレーム呼ばれる — 初回のみラッチ
    public static void OnResultsPhase()
    {
        if (_current != null && _votingTimeLeftAtResults < 0)
            _votingTimeLeftAtResults = MeetingTimeManager.VotingTimeLeft;
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

        float elapsed = UnityEngine.Time.realtimeSinceStartup - _meetingStart;

        // 時間切れ判定: Results 遷移瞬間にラッチした残り投票秒 (OnResultsPhase 参照)。
        // 全員投票の早期終了なら大きい値、タイマー満了なら 0〜1 でラッチされている。
        bool timedOut = _votingTimeLeftAtResults is >= 0 and <= 1;

        int alive = 0, voted = 0, notVoterExcluded = 0, regressed = 0;
        List<string> suspects = [];
        List<string> silent = [];
        List<byte> aliveIds = [];

        // 端末別の分母/分子 (2026-08-07 追加)。発症率は 07-31 の 6.9% から 08-03 以降 20〜25% へ上昇したが、
        // これまで被害者側の platform しか記録していなかったため「コンソール勢が増えただけ」の交絡を潰せなかった。
        // 母集団 (alive) 側も端末別に数えることで、端末ごとの発症率を直接出せるようにする。
        Dictionary<string, int> platAlive = [];
        Dictionary<string, int> platNoVote = [];
        var moddedAlive = 0;

        foreach (PlayerControl pc in Main.AllAlivePlayerControls)
        {
            if (pc.IsHost() || pc.PlayerId >= 200) continue;

            // 投票不能アドオン持ちは「投票が届かない」のが仕様なので検知母集団から外す
            // (回帰スナップショットにも入れない — 次会議で外れた場合の誤検知回避)
            if (Main.PlayerStates.TryGetValue(pc.PlayerId, out PlayerState ps) && ps.SubRoles.Contains(CustomRoles.Notvoter))
            {
                notVoterExcluded++;
                continue;
            }

            alive++;
            aliveIds.Add(pc.PlayerId);

            string platform = pc.GetClient()?.PlatformData?.Platform.ToString() ?? "Unknown";
            platAlive[platform] = platAlive.GetValueOrDefault(platform) + 1;
            if (pc.IsModdedClient()) moddedAlive++;

            if (Voted.Contains(pc.PlayerId))
            {
                voted++;
                continue;
            }

            int chats = ChatCount.GetValueOrDefault(pc.PlayerId);

            // 回帰 = 前会議には生存参加して投票が届いていたのに、今回は届かなかった
            bool didRegress = PrevAlive.Contains(pc.PlayerId) && PrevVoted.Contains(pc.PlayerId);
            if (didRegress) regressed++;

            string desc = $"{pc.GetRealName()} (id {pc.PlayerId}, modded={pc.IsModdedClient()}, platform={platform}, chats={chats}, regressed={didRegress})";

            if (chats > 0)
            {
                suspects.Add(desc);
                platNoVote[platform] = platNoVote.GetValueOrDefault(platform) + 1;
            }
            else silent.Add(desc);
        }

        Logger.Info($"meeting summary: alive={alive} voted={voted} chat-no-vote={suspects.Count} silent-no-vote={silent.Count} notvoter-excluded={notVoterExcluded} regressed-no-vote={regressed} timedOut={timedOut} voteLeftAtResults={_votingTimeLeftAtResults} elapsed={elapsed:F0}s", "MeetingSilentProbe");

        // 端末別の内訳。分母 (alive) 側も端末で割ることで、「発症率の上昇」が本当の悪化なのか
        // 単に来場者の端末構成が変わっただけなのかを日別集計で分離できる。
        List<string> platParts = [];
        foreach (KeyValuePair<string, int> kv in platAlive)
            platParts.Add($"{kv.Key}={platNoVote.GetValueOrDefault(kv.Key)}/{kv.Value}");

        Logger.Info($"platform breakdown (chat-no-vote/alive): {string.Join(" ", platParts)} | modded-alive={moddedAlive}/{alive}", "MeetingSilentProbe");

        foreach (string s in suspects)
            Logger.Warn($"{s} sent chat during meeting but never cast a vote — likely broken vote UI", "MeetingSilentProbe");

        if (silent.Count > 0)
            Logger.Info($"no-vote & no-chat (indeterminate, AFK?): {string.Join(", ", silent)}", "MeetingSilentProbe");

        // 回帰スナップショット更新 (今会議の観測が次会議の「前会議」になる)
        PrevAlive.Clear();
        PrevVoted.Clear();

        foreach (byte id in aliveIds)
        {
            PrevAlive.Add(id);
            if (Voted.Contains(id)) PrevVoted.Add(id);
        }
    }
}
