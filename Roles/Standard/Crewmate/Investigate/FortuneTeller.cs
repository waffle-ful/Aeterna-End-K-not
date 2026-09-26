using System;
using System.Collections.Generic;
using System.Linq;
using EndKnot.Modules;
using Hazel;
using static EndKnot.Options;
using static EndKnot.Translator;

namespace EndKnot.Roles;

public class FortuneTeller : RoleBase
{
    private const int Id = 6700;
    private static int RolesPerCategory;
    private static List<byte> PlayerIdList = [];

    public static OptionItem CheckLimitOpt;
    public static OptionItem AccurateCheckMode;
    public static OptionItem HideVote;
    public static OptionItem ShowSpecificRole;
    public static OptionItem NumRolesListedForEachPlayer;
    public static OptionItem AbilityUseGainWithEachTaskCompleted;
    public static OptionItem AbilityChargesWhenFinishedTasks;
    public static OptionItem CancelVote;

    public static readonly List<byte> DidVote = [];

    private static Dictionary<byte, List<CustomRoles>> AllPlayerRoleList = [];

    public override bool IsEnable => PlayerIdList.Count > 0;

    public override void SetupCustomOption()
    {
        SetupRoleOptions(Id, TabGroup.CrewmateRoles, CustomRoles.FortuneTeller);

        CheckLimitOpt = new IntegerOptionItem(Id + 10, "FortuneTellerSkillLimit", new(0, 20, 1), 1, TabGroup.CrewmateRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.FortuneTeller])
            .SetValueFormat(OptionFormat.Times);

        AccurateCheckMode = new BooleanOptionItem(Id + 12, "AccurateCheckMode", false, TabGroup.CrewmateRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.FortuneTeller]);

        ShowSpecificRole = new BooleanOptionItem(Id + 13, "ShowSpecificRole", false, TabGroup.CrewmateRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.FortuneTeller]);

        HideVote = new BooleanOptionItem(Id + 14, "FortuneTellerHideVote", false, TabGroup.CrewmateRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.FortuneTeller]);

        AbilityUseGainWithEachTaskCompleted = new FloatOptionItem(Id + 15, "AbilityUseGainWithEachTaskCompleted", new(0f, 5f, 0.05f), 1f, TabGroup.CrewmateRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.FortuneTeller])
            .SetValueFormat(OptionFormat.Times);

        AbilityChargesWhenFinishedTasks = new FloatOptionItem(Id + 16, "AbilityChargesWhenFinishedTasks", new(0f, 5f, 0.05f), 0.2f, TabGroup.CrewmateRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.FortuneTeller])
            .SetValueFormat(OptionFormat.Times);

        NumRolesListedForEachPlayer = new IntegerOptionItem(Id + 17, "NumRolesListedForEachPlayer", new(1, 10, 1), 5, TabGroup.CrewmateRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.FortuneTeller]);

        CancelVote = CreateVoteCancellingUseSetting(Id + 11, CustomRoles.FortuneTeller, TabGroup.CrewmateRoles);
        OverrideTasksData.Create(Id + 21, TabGroup.CrewmateRoles, CustomRoles.FortuneTeller);
    }

    public override void Init()
    {
        PlayerIdList = [];
        AllPlayerRoleList = [];
        RolesPerCategory = NumRolesListedForEachPlayer.GetInt();
    }

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);
        playerId.SetAbilityUseLimit(CheckLimitOpt.GetFloat());

        LateTask.New(() =>
        {
            var players = Main.CachedAllPlayerControls();
            int rolesNeeded = players.Count * (RolesPerCategory - 1);

            (List<CustomRoles> RoleList, PlayerControl Player)[] roleList = Main.CustomRoleValues
                .Where(x => !x.IsVanilla() && !x.IsAdditionRole() && x is not CustomRoles.GM and not CustomRoles.Convict and not CustomRoles.NotAssigned && !x.IsForOtherGameMode() && !CustomRoleSelector.RoleResult.ContainsValue(x))
                .OrderBy(x => x.IsEnable() ? IRandom.Instance.Next(10) : IRandom.Instance.Next(10, 100))
                .Take(rolesNeeded)
                .Chunk(RolesPerCategory - 1)
                .Zip(players, (array, player) => (RoleList: array.ToList(), Player: player))
                .ToArray();

            roleList.Do(x => x.RoleList.Insert(IRandom.Instance.Next(x.RoleList.Count), Modules.Ekm.EkrManager.GetApparentRole(x.Player)));
            AllPlayerRoleList = roleList.ToDictionary(x => x.Player.PlayerId, x => x.RoleList);

            Logger.Info(string.Join(" ---- ", AllPlayerRoleList.Select(x => $"ID {x.Key} ({x.Key.GetPlayer().GetNameWithRole()}): {string.Join(", ", x.Value)}")), "FortuneTeller Roles");
        }, 8f, log: false);
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
            Utils.SendMessage(GetString("FortuneTellerCheckReachLimit"), player.PlayerId, CustomRoles.FortuneTeller.ColoredTextByRole(GetString("FortuneTellerCheckMsgTitle")));
            return false;
        }

        player.RpcRemoveAbilityUse();

        if (player.PlayerId == target.PlayerId)
        {
            Utils.SendMessage(GetString("FortuneTellerCheckSelfMsg") + "\n\n" + string.Format(GetString("FortuneTellerCheckLimit"), player.GetAbilityUseLimit()), player.PlayerId, CustomRoles.FortuneTeller.ColoredTextByRole(GetString("FortuneTellerCheckMsgTitle")), importance: MessageImportance.Low);
            return false;
        }

        string msg;

        if ((player.AllTasksCompleted() || AccurateCheckMode.GetBool()) && ShowSpecificRole.GetBool())
            msg = string.Format(GetString("FortuneTellerCheck.TaskDone"), target.GetRealName(), Modules.Ekm.EkrManager.GetApparentRole(target).ToColoredString());
        else
        {
            string roles = string.Join(", ", AllPlayerRoleList[target.PlayerId].Select(x => x.ToColoredString()));
            msg = string.Format(GetString("FortuneTellerCheckResult"), target.GetRealName(), roles);
        }

        Utils.SendMessage(GetString("FortuneTellerCheck") + "\n" + msg + "\n\n" + string.Format(GetString("FortuneTellerCheckLimit"), player.GetAbilityUseLimit()), player.PlayerId, CustomRoles.FortuneTeller.ColoredTextByRole(GetString("FortuneTellerCheckMsgTitle")), importance: MessageImportance.High);

        // マッドメイトの占い師は、占った相手の候補一覧に実在のインポスターの役職を混入させる。
        // 一覧は全占い師で共有なので、後で本物の占い師が同じ相手を占うと黒い候補が紛れて見える。
        if (player.Is(CustomRoles.Madmate)) PoisonCandidateList(target);

        Main.DontCancelVoteList.Add(player.PlayerId);
        return true;
    }

    private static void PoisonCandidateList(PlayerControl target)
    {
        if (!AllPlayerRoleList.TryGetValue(target.PlayerId, out List<CustomRoles> list) || list.Count == 0) return;

        List<PlayerControl> impostors = [.. Main.EnumerateAlivePlayerControls().Where(x => x.Is(CustomRoleTypes.Impostor) && x.PlayerId != target.PlayerId)];
        if (impostors.Count == 0) return;

        CustomRoles impostorRole = Modules.Ekm.EkrManager.GetApparentRole(impostors.RandomElement());
        // 本当の役職の枠は残す (真実を消すのではなく、黒い候補を紛れ込ませる)。
        CustomRoles trueRole = Modules.Ekm.EkrManager.GetApparentRole(target);
        List<int> slots = [.. Enumerable.Range(0, list.Count).Where(i => list[i] != trueRole)];
        if (slots.Count == 0) return;

        list[slots.RandomElement()] = impostorRole;
    }

    public override void OnMeetingShapeshift(PlayerControl shapeshifter, PlayerControl target)
    {
        OnVote(shapeshifter, target);
    }

    public static void OnRoleChange(byte id, CustomRoles previousRole, CustomRoles newRole)
    {
        try
        {
            if (!AllPlayerRoleList.TryGetValue(id, out List<CustomRoles> list)) return;

            int index = list.IndexOf(previousRole);
            list.Remove(previousRole);
            list.Insert(index, newRole);
        }
        catch (Exception e) { Utils.ThrowException(e); }
    }
}