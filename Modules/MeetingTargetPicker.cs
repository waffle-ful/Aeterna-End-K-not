using System;
using System.Collections.Generic;
using System.Linq;
using AmongUs.GameOptions;
using EndKnot.Roles;

namespace EndKnot.Modules;

// 会議中に「相手の行をタップして 1 人選ぶ」能力を、非モッド客にもボタンで渡すための共通層。
//
// バニラ Judge (RoleTypes.Judge) の basis を会議のあいだだけ配ると、他人の行に木槌ボタンが生え、
// 押下が RpcCalls.QueueOverruleVotes (66) でホストへ届く。これを役職側のコマンド処理へ流す。
// 実測で確認済みの制約は次のとおり:
//   ・押下したクライアントの票は didVote=True / votedFor=255 の無効票になる (集計は 255 を既に除外)
//   ・木槌はバニラ側で 1 会議 1 回のハードキャップ (使用回数の多い役職はチャットコマンドが必要)
//   ・死者の行には出ない / 通信妨害中は死ぬ
//   ・basis はラウンド通して占有しなくてよい。MeetingHud 生成後に配るのが安全な順序
public static class MeetingTargetPicker
{
    // basis を配ってあるプレイヤーと、配る直前の本来の RoleTypes。
    // 復帰は「その場で再計算」ではなくこのスナップショットを使う (会議中に basis が変わる役職を
    // 将来 WantsButton に足したとき、復帰値が他処理との実行順まかせになるのを防ぐ)。
    private static readonly Dictionary<byte, RoleTypes> Snapshot = [];

    // PlayerGameOptionsSender がタスクゲートを 0 にする対象。
    public static bool IsHolder(byte playerId)
    {
        return Snapshot.ContainsKey(playerId);
    }

    private static bool Enabled => Options.UseJudgeMeetingButton.GetBool();

    // 木槌チャンネルを使う役職。ここに足すだけで対応役職が増える。
    // 条件: 単一ターゲット / 素の Crewmate basis / その会議の 1 票を捨ててよい / 対象が生存者。
    //
    // モッド客は対象外 — 既に mod 自前の会議内ボタン (Judge.StartMeetingPatch) を持っているので、
    // 配ると二重 UI になる (ExtendedPlayerControl.UsesMeetingShapeshift と同じ除外規約)。
    // 付与済みかどうかに依らない純粋な述語。付与のタイミングより先に走る表示系
    // (「シェイプシフト メニューを使ってください」の案内等) から使う。
    public static bool WouldGetButton(PlayerControl pc)
    {
        return Enabled && pc && WantsButton(pc);
    }

    private static bool WantsButton(PlayerControl pc)
    {
        if (pc.IsModdedClient()) return false;

        return pc.Is(CustomRoles.Judge);
    }

    // 押下をその役職の既存経路へ流す。チャットコマンドと同じ関所を通すのが肝
    // (ホスト側で役職・生存・使用回数を再検証するため、クライアント申告を信用しない)。
    private static void Dispatch(PlayerControl judge, byte targetId)
    {
        if (judge.Is(CustomRoles.Judge))
            Judge.TrialMsg(judge, $"/tl {targetId}", true, false);
    }

    // ゲーム開始時のリセット。会議中/直後にゲームが終わると AfterMeeting() ごとスキップされるため、
    // ここで消さないと次ゲームで同じ PlayerId を引いた別人にタスクゲート解除が漏れる。
    public static void Reset()
    {
        Snapshot.Clear();
    }

    public static void OnMeetingStart()
    {
        Snapshot.Clear();

        if (!Enabled || !AmongUsClient.Instance.AmHost || GameStates.IsEnded) return;

        foreach (PlayerControl pc in Main.EnumeratePlayerControls())
        {
            try
            {
                if (!pc || !pc.IsAlive() || !WantsButton(pc)) continue;

                Snapshot[pc.PlayerId] = pc.GetRoleTypes();

                // JudgeRole.IsBlockedByTasks() を無効化するため、タスク要求率 0 を先に届ける。
                pc.SyncSettings();
                pc.RpcSetRoleDesync(RoleTypes.Judge, pc.OwnerId);

                Logger.Info($"Granted meeting picker basis to {pc.GetNameWithRole()}", "MeetingTargetPicker");
            }
            catch (Exception e) { Utils.ThrowException(e); }
        }
    }

    // 会議明けに元の basis へ戻す。AntiBlackout の役職 revert は CachedRoleMap が空だと走らないので、
    // ここで必ず自前で戻す (両方走っても同じ値の再送になるだけ)。
    public static void AfterMeeting()
    {
        if (Snapshot.Count == 0) return;

        (byte Id, RoleTypes Role)[] entries = [.. Snapshot.Select(x => (x.Key, x.Value))];
        Snapshot.Clear();

        foreach ((byte id, RoleTypes role) in entries)
        {
            try
            {
                PlayerControl pc = Utils.GetPlayerById(id);
                if (!pc) continue;

                // 死者/切断者は触らない — ここは AntiBlackout がゴースト役職を配り終えた後なので、
                // 生前の basis を書き戻すとゴースト化を自分で剥がしてしまう
                // (木槌で自決した Judge / 追放された Judge が該当する)。
                if (!pc.IsAlive() || pc.Data == null || pc.Data.Disconnected) continue;

                pc.SyncSettings();
                pc.RpcSetRoleDesync(role, pc.OwnerId);

                Logger.Info($"Reverted meeting picker basis for {pc.GetNameWithRole()}", "MeetingTargetPicker");
            }
            catch (Exception e) { Utils.ThrowException(e); }
        }
    }

    // RpcCalls.QueueOverruleVotes (66) の受信。読み取り位置は呼び出し側が戻すので、ここでは読むだけ。
    public static void OnOverrulePressed(byte judgeId, byte targetId)
    {
        // Enabled は見ない — 配ってあるボタンは設定を切っても消えないので、会議中に OFF へ切り替えると
        // 押下が無反応になって「投票権だけ失う」無言の事故になる。保持者かどうかだけを関所にする。
        if (!AmongUsClient.Instance.AmHost || GameStates.IsEnded) return;
        if (!IsHolder(judgeId)) return;

        PlayerControl judge = Utils.GetPlayerById(judgeId);
        PlayerControl target = Utils.GetPlayerById(targetId);
        if (!judge || !target || !judge.IsAlive()) return;
        if (Starspawn.IsDayBreak) return;

        Logger.Info($"{judge.GetNameWithRole()} picked {target.GetNameWithRole()} via the meeting button", "MeetingTargetPicker");

        try { Dispatch(judge, targetId); }
        catch (Exception e) { Utils.ThrowException(e); }
    }
}
