using System;
using System.Collections.Generic;
using AmongUs.GameOptions;
using EndKnot.Modules;
using Hazel;

namespace EndKnot.Roles;

// ============================================================
// ProbabilityKing (確率王) — Neutral/Killing
//
// コンセプト:
//   普通にキルできる Neutral Killing。キル成立ごとに「現在の成功率」で抽選し、当たれば
//   ①追加投票 +1 ②成功率が SuccessRateIncrease ポイント上昇 (上限100%) して二度と下がらない。
//   外れても成功率は据え置きで、次のキルでまた同じ確率から挑戦できる。
//
// 追加投票の実装は Amogus.ExtraVotes と完全に同型: 役職インスタンス自身に public int ExtraVotes を
// 持たせ、Patches/MeetingHudPatch.cs の2箇所 (票アイコン描画側 / 集計側) の
// switch (Main.PlayerStates[ps.PlayerId].Role) に case ProbabilityKing { IsEnable: true,
// ExtraVotes: > 0 } を追加して読む。RoleBase はロールごとの singleton ではなく、SetMainRole が
// role.GetRoleClass() (Activator.CreateInstance) で毎回新規インスタンスを払い出すため、各保持者は
// 自分専用のインスタンスを持つ (Main.PlayerStates[id].Role で常にその人自身のオブジェクトが引ける)。
// よって SuccessRate / ExtraVotes を per-instance フィールドで持っても複数人同時所持で共有事故は
// 起きない (Amogus の CurrentLevel / ExtraVotes と同じ根拠)。
// ============================================================
public class ProbabilityKing : RoleBase
{
    private const int Id = 707000;
    public static List<byte> PlayerIdList = [];

    private static OptionItem KillCooldown;
    private static OptionItem CanVent;
    private static OptionItem ImpostorVision;
    private static OptionItem BaseSuccessRate;
    private static OptionItem SuccessRateIncrease;
    private static OptionItem MaxExtraVotes;

    private byte ProbabilityKingId;
    private int SuccessRate;
    public int ExtraVotes;

    public override bool IsEnable => PlayerIdList.Count > 0;

    public override void SetupCustomOption()
    {
        StartSetup(Id)
            .AutoSetupOption(ref KillCooldown, 25f, new FloatValueRule(0f, 180f, 0.5f), OptionFormat.Seconds)
            .AutoSetupOption(ref CanVent, true)
            .AutoSetupOption(ref ImpostorVision, true)
            .AutoSetupOption(ref BaseSuccessRate, 2, new IntegerValueRule(2, 20, 1), OptionFormat.Percent)
            .AutoSetupOption(ref SuccessRateIncrease, 10, new IntegerValueRule(5, 20, 1), OptionFormat.Percent)
            .AutoSetupOption(ref MaxExtraVotes, 10, new IntegerValueRule(1, 15, 1), OptionFormat.Votes);
    }

    public override void Init()
    {
        PlayerIdList = [];
    }

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);
        ProbabilityKingId = playerId;
        SuccessRate = BaseSuccessRate.GetInt();
        ExtraVotes = 0;
    }

    public override void Remove(byte playerId)
    {
        PlayerIdList.Remove(playerId);
    }

    public override void SetKillCooldown(byte id)
    {
        Main.AllPlayerKillCooldown[id] = KillCooldown.GetFloat();
    }

    public override bool CanUseImpostorVentButton(PlayerControl pc)
    {
        return CanVent.GetBool();
    }

    public override void ApplyGameOptions(IGameOptions opt, byte playerId)
    {
        opt.SetVision(ImpostorVision.GetBool());
    }

    public override void OnMurder(PlayerControl killer, PlayerControl target)
    {
        bool hit = IRandom.Instance.Next(100) < SuccessRate;

        if (!hit)
        {
            killer.Notify(string.Format(Translator.GetString("ProbabilityKingMiss"), SuccessRate));
            return;
        }

        bool votesGained = ExtraVotes < MaxExtraVotes.GetInt();
        if (votesGained) ExtraVotes++;

        SuccessRate = Math.Min(SuccessRate + SuccessRateIncrease.GetInt(), 100);

        Utils.SendRPC(CustomRPC.SyncRoleData, ProbabilityKingId, 1, SuccessRate, ExtraVotes);

        killer.Notify(string.Format(Translator.GetString(votesGained ? "ProbabilityKingHit" : "ProbabilityKingHitCapped"), SuccessRate));
    }

    public void ReceiveRPC(MessageReader reader)
    {
        switch (reader.ReadPackedInt32())
        {
            case 1:
                SuccessRate = reader.ReadPackedInt32();
                ExtraVotes = reader.ReadPackedInt32();
                break;
        }
    }

    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud = false, bool meeting = false)
    {
        if (seer.PlayerId != ProbabilityKingId || seer.PlayerId != target.PlayerId || meeting || (seer.IsModdedClient() && !hud)) return string.Empty;

        return string.Format(Translator.GetString("ProbabilityKing.Suffix"), SuccessRate);
    }

    public override string GetProgressText(byte playerId, bool comms)
    {
        return $"{base.GetProgressText(playerId, comms)} {Utils.ColorString(Utils.GetRoleColor(CustomRoles.ProbabilityKing), $"({SuccessRate}%)")} {string.Format(Translator.GetString("ExtraVotesPT"), ExtraVotes)}";
    }
}
