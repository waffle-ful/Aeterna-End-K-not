using System;
using System.Collections.Generic;
using Hazel;
using static EndKnot.Options;
using static EndKnot.Translator;

namespace EndKnot.Roles;

public class Oracle : RoleBase
{
    private const int Id = 7600;
    private static List<byte> PlayerIdList = [];

    public static OptionItem CheckLimitOpt;
    public static OptionItem HideVote;
    public static OptionItem FailChance;
    public static OptionItem OracleAbilityUseGainWithEachTaskCompleted;
    public static OptionItem AbilityChargesWhenFinishedTasks;
    public static OptionItem CancelVote;

    public static readonly List<byte> DidVote = [];

    // マッドメイトの託宣者が最初に使った1回だけ、結果を全員へ公開する (以降は通常の非公開判定に戻る)。
    private bool MadPublicRevealUsed;

    public override bool IsEnable => PlayerIdList.Count > 0;

    public override void SetupCustomOption()
    {
        SetupRoleOptions(Id, TabGroup.CrewmateRoles, CustomRoles.Oracle);

        CheckLimitOpt = new IntegerOptionItem(Id + 10, "OracleSkillLimit", new(0, 10, 1), 0, TabGroup.CrewmateRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Oracle])
            .SetValueFormat(OptionFormat.Times);

        HideVote = new BooleanOptionItem(Id + 12, "OracleHideVote", false, TabGroup.CrewmateRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Oracle]);

        FailChance = new IntegerOptionItem(Id + 13, "FailChance", new(0, 100, 5), 20, TabGroup.CrewmateRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Oracle])
            .SetValueFormat(OptionFormat.Percent);

        OracleAbilityUseGainWithEachTaskCompleted = new FloatOptionItem(Id + 14, "AbilityUseGainWithEachTaskCompleted", new(0f, 5f, 0.05f), 0.2f, TabGroup.CrewmateRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Oracle])
            .SetValueFormat(OptionFormat.Times);

        AbilityChargesWhenFinishedTasks = new FloatOptionItem(Id + 15, "AbilityChargesWhenFinishedTasks", new(0f, 5f, 0.05f), 0.2f, TabGroup.CrewmateRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Oracle])
            .SetValueFormat(OptionFormat.Times);

        CancelVote = CreateVoteCancellingUseSetting(Id + 11, CustomRoles.Oracle, TabGroup.CrewmateRoles);
    }

    public override void Init()
    {
        PlayerIdList = [];
    }

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);
        playerId.SetAbilityUseLimit(CheckLimitOpt.GetFloat());
        MadPublicRevealUsed = false;
    }

    public override void Remove(byte playerId)
    {
        PlayerIdList.Remove(playerId);
    }

    public override bool OnVote(PlayerControl player, PlayerControl target)
    {
        if (Starspawn.IsDayBreak) return false;
        if (player == null || target == null) return false;

        if (DidVote.Contains(player.PlayerId) || Main.DontCancelVoteList.Contains(player.PlayerId)) return false;

        DidVote.Add(player.PlayerId);

        if (player.GetAbilityUseLimit() < 1)
        {
            Utils.SendMessage(GetString("OracleCheckReachLimit"), player.PlayerId, CustomRoles.Oracle.ColoredTextByRole(GetString("OracleCheckMsgTitle")));
            return false;
        }

        player.RpcRemoveAbilityUse();

        if (player.PlayerId == target.PlayerId)
        {
            Utils.SendMessage(GetString("OracleCheckSelfMsg") + "\n\n" + string.Format(GetString("OracleCheckLimit"), player.GetAbilityUseLimit()), player.PlayerId, CustomRoles.Oracle.ColoredTextByRole(GetString("OracleCheckMsgTitle")), importance: MessageImportance.Low);
            return false;
        }

        Team team = Modules.Ekm.EkrManager.GetApparentTeam(target);

        // マッドメイトの託宣者は、初回の判定だけ結果を全員公開にする。インポ相手は必ず「クルー」と偽り、
        // それ以外は FailChance を適用せず正直に答える (2回目以降は下の通常分岐に戻る)。
        if (player.Is(CustomRoles.Madmate) && !MadPublicRevealUsed)
        {
            MadPublicRevealUsed = true;

            Team publicTeam = team.HasFlag(Team.Impostor) ? Team.Crewmate : team;
            string publicMsg = string.Format(GetString($"OracleCheck.{GetString($"ShortTeamName.{publicTeam}", SupportedLangs.English)}"), target.GetRealName());

            Utils.SendMessage($"{GetString("OracleCheck")}\n{publicMsg}", title: CustomRoles.Oracle.ColoredTextByRole(GetString("OracleCheckMsgTitle")), importance: MessageImportance.High);

            Main.DontCancelVoteList.Add(player.PlayerId);
            return true;
        }

        if (IRandom.Instance.Next(100) < FailChance.GetInt())
            team = Main.TeamValues[1..].Without(team).RandomElement();

        string msg = string.Format(GetString($"OracleCheck.{GetString($"ShortTeamName.{team}", SupportedLangs.English)}"), target.GetRealName());

        Utils.SendMessage($"{GetString("OracleCheck")}\n{msg}\n\n{string.Format(GetString("OracleCheckLimit"), player.GetAbilityUseLimit())}", player.PlayerId, CustomRoles.Oracle.ColoredTextByRole(GetString("OracleCheckMsgTitle")), importance: MessageImportance.High);

        Main.DontCancelVoteList.Add(player.PlayerId);
        return true;
    }

    public override void OnMeetingShapeshift(PlayerControl shapeshifter, PlayerControl target)
    {
        OnVote(shapeshifter, target);
    }
}