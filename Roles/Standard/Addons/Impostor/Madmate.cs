using System.Collections.Generic;
using System.Linq;
using EndKnot.Patches;
using static EndKnot.Options;

namespace EndKnot.Roles;

internal class Madmate : IAddon
{
    public AddonTypes Type => AddonTypes.Mixed;

    public void SetupCustomOption()
    {
        SetupAdtRoleOptions(15800, CustomRoles.Madmate, canSetNum: true, canSetChance: false, allowZeroCount: true);

        MadmateSpawnMode = new StringOptionItem(15810, "MadmateSpawnMode", MadmateSpawnModeStrings, 0, TabGroup.Addons)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Madmate]);

        MadmateCountMode = new StringOptionItem(15811, "MadmateCountMode", MadmateCountModeStrings, 0, TabGroup.Addons)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Madmate]);

        SheriffCanBeMadmate = new BooleanOptionItem(15812, "SheriffCanBeMadmate", false, TabGroup.Addons)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Madmate]);

        MayorCanBeMadmate = new BooleanOptionItem(15813, "MayorCanBeMadmate", false, TabGroup.Addons)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Madmate]);

        NGuesserCanBeMadmate = new BooleanOptionItem(15814, "NGuesserCanBeMadmate", false, TabGroup.Addons)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Madmate]);

        MarshallCanBeMadmate = new BooleanOptionItem(15815, "MarshallCanBeMadmate", false, TabGroup.Addons)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Madmate]);

        InvestigatorCanBeMadmate = new BooleanOptionItem(15816, "InvestigatorCanBeMadmate", false, TabGroup.Addons)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Madmate]);

        PresidentCanBeMadmate = new BooleanOptionItem(15817, "PresidentCanBeMadmate", false, TabGroup.Addons)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Madmate]);

        SnitchCanBeMadmate = new BooleanOptionItem(15818, "SnitchCanBeMadmate", false, TabGroup.Addons)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Madmate]);

        MadSnitchTasks = new IntegerOptionItem(15819, "MadSnitchTasks", new(0, 90, 1), 3, TabGroup.Addons)
            .SetParent(SnitchCanBeMadmate)
            .SetValueFormat(OptionFormat.Pieces);

        JudgeCanBeMadmate = new BooleanOptionItem(15820, "JudgeCanBeMadmate", false, TabGroup.Addons)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Madmate]);
    }

    /// <summary>
    ///     追放されたマッドメイトが道連れを 1 人連れていく。対象は陣営ごとの子オプションで絞る。
    ///     呼び出しは追放確定後 (ExileControllerWrapUpPatch) の 1 箇所だけ。
    /// </summary>
    public static void CheckRevenge(byte exiledId)
    {
        if (!AmongUsClient.Instance.AmHost) return;
        if (!MadmateRevengePlayer.GetBool()) return;

        PlayerControl exiled = Utils.GetPlayerById(exiledId);
        if (exiled == null || !exiled.Is(CustomRoles.Madmate)) return;

        List<PlayerControl> candidates = Main.AllAlivePlayerControlsToList
            .Where(pc => pc.PlayerId != exiledId && IsRevengeTarget(pc))
            .ToList();

        if (candidates.Count == 0) return;

        PlayerControl target = candidates.RandomElement();
        Logger.Info($"{target.GetRealName()} was dragged along by the exiled Madmate", "MadmateRevenge");

        // 追放処理の途中なので即死させず、会議明けの死亡キューへ積む (Twins の後追い死と同じ経路)。
        // killer を立てておかないと死因表示や追跡系から「誰にやられたか不明」になる (Avenger の道連れと同型)。
        CheckForEndVotingPatch.TryAddAfterMeetingDeathPlayers(PlayerState.DeathReason.Revenge, target.PlayerId);
        target.SetRealKiller(exiled);
    }

    // マッドメイトはクルー役職 + 属性なので、陣営判定より先に属性を見ないとクルー扱いへ流れる。
    private static bool IsRevengeTarget(PlayerControl pc)
    {
        if (pc.Is(CustomRoles.Madmate)) return MadmateRevengeMadmate.GetBool();

        // インポスターだけ CustomRoleTypes で見る — Team.Impostor は Framer に嵌められたクルーまで含むので、
        // 「道連れにしてよい相手か」の判定には広すぎる。
        if (pc.Is(CustomRoleTypes.Impostor)) return MadmateRevengeImpostor.GetBool();

        // 残りは Team で見る。改宗アドオン (Charmed/Contagious/Undead/Entranced) は MainRole が
        // 元のクルー役職のままなので、CustomRoleTypes で見るとクルー扱いへ落ちる。
        if (pc.Is(Team.Coven)) return MadmateRevengeCoven.GetBool();
        if (pc.Is(Team.Neutral)) return MadmateRevengeNeutral.GetBool();

        return MadmateRevengeCrewmate.GetBool();
    }
}