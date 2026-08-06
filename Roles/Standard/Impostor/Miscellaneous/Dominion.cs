using System.Collections.Generic;
using AmongUs.GameOptions;
using static EndKnot.Translator;

namespace EndKnot.Roles;

// ============================================================
// Dominion (ドミニオン) — Impostor/Miscellaneous
//
// コンセプト:
//   通常のインポスターの上に「投票の乗っ取り」を足した役職。対象選択は Teller と同じ
//   「自己投票で選択モードに入る」方式、投票の上書きは Balancer.ManipulateVotingResult の
//   redirect パターンをそのまま踏襲する。
//
// 操作フロー (すべて OnVote 経由。CancelsVote() へ常時登録することで投票時に呼ばれる):
//   1. 自分に投票 → 選択モードに入る
//   2. 続けて TargetsPerUse 人ぶん投票 → 「操られる側」を確定
//   3. さらに1人投票 → 操られる側全員の投票先をその人へ上書き。能力を1回消費して選択モード終了
//   選択モード中の投票は常に true を返して消費する (票にしない)。ただし対象が死亡している場合は
//   Balancer.OnVote (BalancerTargetDead) に倣い、状態を進めずメッセージだけ出して再投票を促す
//   (死亡者を states に絡めると、後段の ManipulateVotingResult が死者の投票枠から票を捏造しうるため)。
//
// 状態は全て per-player Dictionary<byte,T>。RoleBase はロールごとの singleton インスタンスであり、
// 複数人が同時に Dominion を持てる (Max > 1 設定時) ため、単一フィールドで持つと共有されて壊れる。
// PendingRedirects は「操られる側」の PlayerId をキーにする (Dominion 本人の PlayerId ではない) ことで、
// 同じ会議中に複数の Dominion が並行して選択していても、対象単位で正しく合成される。
// ============================================================
public class Dominion : RoleBase
{
    private const int Id = 706900;
    public static List<byte> PlayerIdList = [];

    private static OptionItem KillCooldown;
    private static OptionItem CanKill;
    private static OptionItem CanVent;
    private static OptionItem ImpostorVision;
    private static OptionItem MaxOverwrites;
    private static OptionItem TargetsPerUse;

    // 選択モード中かどうか、および現在までに選んだ「操られる側」の一覧 (Dominion の PlayerId がキー)。
    private static Dictionary<byte, bool> Selecting = [];
    private static Dictionary<byte, List<byte>> PendingTargets = [];

    // 確定済みの上書き (操られる側の PlayerId → 上書き先の PlayerId)。
    // 同じ会議の集計時に ManipulateVotingResult が消費してクリアする (次の会議へは持ち越さない)。
    private static Dictionary<byte, byte> PendingRedirects = [];

    public override bool IsEnable => PlayerIdList.Count > 0;

    public override void SetupCustomOption()
    {
        StartSetup(Id)
            .AutoSetupOption(ref KillCooldown, 30f, new FloatValueRule(0f, 180f, 0.5f), OptionFormat.Seconds)
            .AutoSetupOption(ref CanKill, true)
            .AutoSetupOption(ref CanVent, true)
            .AutoSetupOption(ref ImpostorVision, true)
            .AutoSetupOption(ref MaxOverwrites, 1, new IntegerValueRule(1, 15, 1), OptionFormat.Times)
            .AutoSetupOption(ref TargetsPerUse, 1, new IntegerValueRule(1, 5, 1), OptionFormat.Players);
    }

    public override void Init()
    {
        PlayerIdList = [];
        Selecting = [];
        PendingTargets = [];
        PendingRedirects = [];
    }

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);
        playerId.SetAbilityUseLimit(MaxOverwrites.GetInt());
        Selecting[playerId] = false;
        PendingTargets[playerId] = [];
    }

    public override void Remove(byte playerId)
    {
        PlayerIdList.Remove(playerId);
    }

    public override void SetKillCooldown(byte id)
    {
        Main.AllPlayerKillCooldown[id] = KillCooldown.GetFloat();
    }

    public override bool CanUseKillButton(PlayerControl pc)
    {
        return CanKill.GetBool();
    }

    public override bool CanUseImpostorVentButton(PlayerControl pc)
    {
        return CanVent.GetBool();
    }

    public override void ApplyGameOptions(IGameOptions opt, byte playerId)
    {
        opt.SetVision(ImpostorVision.GetBool());
    }

    public override bool OnVote(PlayerControl voter, PlayerControl target)
    {
        if (voter == null) return false;
        if (!voter.IsAlive()) return false;

        byte id = voter.PlayerId;

        // target == null (スキップ) 用の防御的分岐。現状の呼び出し元3箇所
        // (ChatCommandPatch の /vote / MeetingHudPatch の集計時観測 / 同 投票時) は
        // いずれも target が null のときは OnVote を呼ばないため**現状は到達しない**
        // (Balancer.OnVote の同じ分岐も同理由で到達しない)。将来呼び出し側が変わったときに
        // 選択モードが宙に浮いたまま残らないよう、解除の意図だけ明示しておく。
        if (target == null)
        {
            if (!Selecting.GetValueOrDefault(id)) return false;

            Selecting[id] = false;
            PendingTargets.Remove(id);
            Utils.SendMessage(GetString("DominionCancelMode"), id, importance: MessageImportance.High);
            return false;
        }

        if (!Selecting.GetValueOrDefault(id))
        {
            float remain = id.GetAbilityUseLimit();
            if (float.IsNaN(remain) || remain < 1f) return false;
            if (target.PlayerId != id) return false; // 自己投票でのみ選択モードへ入る (Teller と同型)

            Selecting[id] = true;
            PendingTargets[id] = [];
            Utils.SendMessage(string.Format(GetString("DominionEnterMode"), TargetsPerUse.GetInt()), id, importance: MessageImportance.High);
            return true;
        }

        // 死亡者を対象にすると、後段の集計 (states) に実体のない票が乗って ManipulateVotingResult が
        // 票を捏造しうる (Balancer.OnVote の BalancerTargetDead と同じ理由)。状態は進めず再投票を促す。
        if (!target.IsAlive())
        {
            Utils.SendMessage(GetString("DominionTargetDead"), id, importance: MessageImportance.High);
            return true;
        }

        if (!PendingTargets.TryGetValue(id, out List<byte> pending))
        {
            pending = [];
            PendingTargets[id] = pending;
        }

        int need = TargetsPerUse.GetInt();

        if (pending.Count < need)
        {
            pending.Add(target.PlayerId);
            bool ready = pending.Count >= need;

            Utils.SendMessage(
                ready
                    ? string.Format(GetString("DominionTargetsReady"), target.PlayerId.ColoredPlayerName())
                    : string.Format(GetString("DominionTargetPicked"), target.PlayerId.ColoredPlayerName(), need - pending.Count),
                id, importance: MessageImportance.High);

            return true;
        }

        // 3段階目: 上書き先を確定する。
        byte redirectTo = target.PlayerId;
        Selecting[id] = false;

        foreach (byte manipulated in pending)
            PendingRedirects[manipulated] = redirectTo;

        string names = string.Join(", ", pending.ConvertAll(p => p.ColoredPlayerName()));
        PendingTargets[id] = [];

        voter.RpcRemoveAbilityUse();

        Utils.SendMessage(string.Format(GetString("DominionRedirectConfirmed"), names, redirectTo.ColoredPlayerName()), id, importance: MessageImportance.High);
        return true;
    }

    public override void OnReportDeadBody()
    {
        Selecting = [];
        PendingTargets = [];
        PendingRedirects = [];
    }

    public override void AfterMeetingTasks()
    {
        // Dictator の即決処理 (MeetingHudPatch.cs の CheckForEndVoting 冒頭) はタリー本体より前に
        // return するため ManipulateVotingResult が呼ばれず、PendingRedirects の自己クリアが発火しない。
        // NoCheckStartMeeting 経由の会議 (PortalButton/Shuffler 等) は OnReportDeadBody 自体を経由しない。
        // どちらも次の変則会議へ未消費の状態が持ち越されないよう、ここでも二重にクリアする
        // (WordKiller/Newscaster と同型の保険)。
        Selecting = [];
        PendingTargets = [];
        PendingRedirects = [];
    }

    /// <summary>
    /// 会議集計時に、確定済みの上書きを votingData / states へ反映する。
    /// skip=253 / 未投票=byte.MaxValue の扱いを含め、Balancer.ManipulateVotingResult をそのまま踏襲する。
    /// </summary>
    public static void ManipulateVotingResult(Dictionary<byte, int> votingData, MeetingHud.VoterState[] states)
    {
        if (PendingRedirects.Count == 0) return;

        for (int i = 0; i < states.Length; i++)
        {
            ref MeetingHud.VoterState state = ref states[i];
            if (!PendingRedirects.TryGetValue(state.VoterId, out byte redirectTo)) continue;
            if (state.VotedForId == redirectTo) continue;

            // ⚠️ 253 (スキップ) を減算から除外してはいけない。CustomCalculateVotes
            // (MeetingHudPatch.cs:629) の算入条件は `is not 252 and not byte.MaxValue and not 254` で、
            // **253 は正規の集計対象**。除外すると「スキップの票は減らないのに移動先だけ増える」形になり、
            // 集計の総票数が実投票者数を超えて増殖する (僅差の追放判定が壊れる)。
            // Balancer.ManipulateVotingResult は同じ形をしているが、あちらは投票時に
            // ShouldCancelVote が T1/T2 以外 (スキップ含む) を弾くので実際には 253 が載らない。
            // Dominion には投票内容の制限が無いので、こちらは素通りで踏む。
            // (255 = 未投票 は元から dic にキーが立たないので除外は残しても無害。)
            int oldCount = votingData.GetValueOrDefault(state.VotedForId, 0);
            if (oldCount > 0 && state.VotedForId != byte.MaxValue)
                votingData[state.VotedForId] = oldCount - 1;

            votingData[redirectTo] = votingData.GetValueOrDefault(redirectTo, 0) + 1;
            state.VotedForId = redirectTo;
        }

        PendingRedirects.Clear();
    }
}
