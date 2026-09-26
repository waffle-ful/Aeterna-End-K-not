using System.Collections.Generic;
using System.Linq;

namespace EndKnot.Roles;

public class Inquisitor : RoleBase
{
    public static bool On;

    public override bool IsEnable => On;

    private static OptionItem KnowExactRolesAfterTasksFinished;
    private static OptionItem ExcludeDeadPlayers;
    private static OptionItem AbilityUseLimit;
    private static OptionItem AbilityUseGainWithEachTaskCompleted;
    private static OptionItem AbilityChargesWhenFinishedTasks;

    public override void SetupCustomOption()
    {
        StartSetup(654900)
            .AutoSetupOption(ref KnowExactRolesAfterTasksFinished, true)
            .AutoSetupOption(ref ExcludeDeadPlayers, false)
            .AutoSetupOption(ref AbilityUseLimit, 1f, new FloatValueRule(0, 20, 0.05f), OptionFormat.Times)
            .AutoSetupOption(ref AbilityUseGainWithEachTaskCompleted, 0.5f, new FloatValueRule(0f, 5f, 0.05f), OptionFormat.Times)
            .AutoSetupOption(ref AbilityChargesWhenFinishedTasks, 0.2f, new FloatValueRule(0f, 5f, 0.05f), OptionFormat.Times);
    }

    public override void Init()
    {
        On = false;
    }

    public override void Add(byte playerId)
    {
        On = true;
        playerId.SetAbilityUseLimit(AbilityUseLimit.GetFloat());
    }

    public override bool OnVote(PlayerControl voter, PlayerControl target)
    {
        if (Starspawn.IsDayBreak) return false;
        if (!voter || !target || voter.PlayerId == target.PlayerId || Main.DontCancelVoteList.Contains(voter.PlayerId)) return false;

        var players = ExcludeDeadPlayers.GetBool() ? Main.EnumerateAlivePlayerControls() : Main.EnumeratePlayerControls();

        // マッドメイトの尋問官は逆尋問: 「対象が誰の役職を知っているか」ではなく「対象の役職を誰が知っているか」を調べる。
        bool madReversed = voter.Is(CustomRoles.Madmate);
        List<(byte Id, CustomRoles Role)> knownRoles = [.. from pc in players where madReversed ? Utils.KnowsTargetRole(pc, target) : Utils.KnowsTargetRole(target, pc) select (pc.PlayerId, Modules.Ekm.EkrManager.GetApparentRole(pc))];

        string result;

        if (knownRoles.Count <= 1)
            result = Translator.GetString("InquisitorNoInfo");
        else if (KnowExactRolesAfterTasksFinished.GetBool() && voter.GetTaskState().IsTaskFinished)
            result = string.Join('\n', knownRoles.Select(x => $"{x.Id.ColoredPlayerName()}: {x.Role.ToColoredString()}"));
        else
            result = string.Join(", ", knownRoles.Select(x => x.Id.ColoredPlayerName()));

        string resultTemplateKey = madReversed ? "Inquisitor.MadVoteResult" : "InquisitorVoteResult";
        Utils.SendMessage("\n", voter.PlayerId, string.Format(Translator.GetString(resultTemplateKey), target.PlayerId.ColoredPlayerName(), result), importance: MessageImportance.High);

        voter.RpcRemoveAbilityUse();
        Main.DontCancelVoteList.Add(voter.PlayerId);
        return true;
    }

    public override void OnMeetingShapeshift(PlayerControl shapeshifter, PlayerControl target)
    {
        OnVote(shapeshifter, target);
    }
}