using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using static EndKnot.Translator;

namespace EndKnot.Roles;

public class King : RoleBase
{
    private const int Id = 703400;
    public static List<byte> PlayerIdList = [];

    private static OptionItem OptionExileVoteCount;
    private static OptionItem OptionInvolvementCount;
    private static OptionItem OptionDeathReason;
    private static OptionItem OptionMadExtraInvolvement;
    private static OptionItem OptionRemoveAddonCount;
    private static OptionItem OptionRemoveRoleCount;

    private byte KingId;
    private bool aboooonTriggered;

    public override bool IsEnable => PlayerIdList.Count > 0;

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.CrewmateRoles, CustomRoles.King);

        OptionExileVoteCount = new IntegerOptionItem(Id + 10, "KingExileVoteCount", new(1, 15, 1), 3, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.King])
            .SetValueFormat(OptionFormat.Votes);

        OptionInvolvementCount = new IntegerOptionItem(Id + 11, "KingInvolvementCount", new(0, 15, 1), 5, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.King])
            .SetValueFormat(OptionFormat.Players);

        OptionDeathReason = new StringOptionItem(Id + 12, "KingDeathReason",
            [PlayerState.DeathReason.Kill.ToString(), PlayerState.DeathReason.Suicide.ToString(),
             PlayerState.DeathReason.FollowingSuicide.ToString()],
            0, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.King]);

        OptionMadExtraInvolvement = new IntegerOptionItem(Id + 13, "KingMadExtraInvolvement", new(0, 15, 1), 1, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.King])
            .SetValueFormat(OptionFormat.Players);

        OptionRemoveAddonCount = new IntegerOptionItem(Id + 14, "KingAddon", new(0, 15, 1), 5, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.King])
            .SetValueFormat(OptionFormat.Players);

        OptionRemoveRoleCount = new IntegerOptionItem(Id + 15, "KingRole", new(0, 15, 1), 5, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.King])
            .SetValueFormat(OptionFormat.Players);
    }

    public override void Init()
    {
        PlayerIdList = [];
    }

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);
        KingId = playerId;
        aboooonTriggered = false;
    }

    public override void Remove(byte playerId)
    {
        PlayerIdList.Remove(playerId);
    }

    /// <summary>
    ///     無条件
    /// </summary>
    public override int? GetDefensePower(PlayerControl target, AttackKind kind)
    {
        return MurderOnly(kind, AttackDefense.Unstoppable);
    }

    public override bool OnCheckMurderAsTarget(PlayerControl killer, PlayerControl target, bool check = false)
    {
        if (!check) killer.SetKillCooldown();

        return false;
    }

    public override bool KnowRole(PlayerControl seer, PlayerControl target)
    {
        if (base.KnowRole(seer, target)) return true;
        if (seer.IsCrewmate() && target.Is(CustomRoles.King)) return true;
        // マッドメイトの王は、インポスターからも王だと分かる。
        return seer.Is(CustomRoleTypes.Impostor) && target.Is(CustomRoles.King) && target.Is(CustomRoles.Madmate);
    }

    public override void AfterMeetingTasks()
    {
        if (!AmongUsClient.Instance.AmHost) return;
        if (aboooonTriggered) return;

        PlayerControl king = Utils.GetPlayerById(KingId);
        if (king == null || king.IsAlive()) return;

        aboooonTriggered = true;
        LateTask.New(() => CrewMateAboooon(), 0.5f, "KingAboooon");
    }

    private static PlayerState.DeathReason GetDeathReason()
    {
        return OptionDeathReason.GetValue() switch
        {
            0 => PlayerState.DeathReason.Kill,
            1 => PlayerState.DeathReason.Suicide,
            2 => PlayerState.DeathReason.FollowingSuicide,
            _ => PlayerState.DeathReason.Kill
        };
    }

    private void CrewMateAboooon()
    {
        if (!AmongUsClient.Instance.AmHost) return;

        PlayerControl king = Utils.GetPlayerById(KingId);
        int count = OptionInvolvementCount.GetInt();
        // マッドメイトの王が追放されると、道連れが増える。
        if (king != null && king.Is(CustomRoles.Madmate)) count += OptionMadExtraInvolvement.GetInt();
        if (count <= 0) return;

        List<PlayerControl> crews = Main.AllAlivePlayerControlsToList
            .Where(pc => pc.PlayerId != KingId && pc.IsCrewmate())
            .ToList();

        PlayerState.DeathReason reason = GetDeathReason();

        for (int i = 0; i < count && crews.Count > 0; i++)
        {
            int idx = IRandom.Instance.Next(0, crews.Count);
            PlayerControl crew = crews[idx];
            crews.RemoveAt(idx);

            if (!crew.IsAlive()) { i--; continue; }

            Main.PlayerStates[crew.PlayerId].deathReason = reason;
            if (king != null)
                king.Kill(crew);
            else
                crew.Suicide(reason);

            Logger.Info($"{crew.name} was involved by King", "KingAboooon");
        }

        // 巻き込みを免れたクルーから、属性剥奪と役職剥奪を別々に抽選する。属性剥奪は「有利属性」に限る
        // (マッドメイト/カップル等の陣営を動かす属性は Mixed 判定で対象外のまま)。
        List<CustomRoles> helpfulAddons = Options.GroupedAddons.TryGetValue(AddonTypes.Helpful, out List<CustomRoles> list) ? list : [];
        int addonCount = OptionRemoveAddonCount.GetInt();
        List<PlayerControl> addonPool = [..crews];
        for (int i = 0; i < addonCount && addonPool.Count > 0; i++)
        {
            int idx = IRandom.Instance.Next(0, addonPool.Count);
            PlayerControl crew = addonPool[idx];
            addonPool.RemoveAt(idx);
            if (!crew.IsAlive()) { i--; continue; }

            PlayerState state = Main.PlayerStates[crew.PlayerId];
            state.SubRoles.ToArray().DoIf(helpfulAddons.Contains, state.RemoveSubRole);
            Logger.Info($"{crew.name}'s helpful addons were removed by King", "KingAddon");
        }

        // 役職リセットは SetMainRole が1人ごとに全員宛ての NotifyRoles を2回撃つため、会議明けに
        // まとめて撃たず 0.15 秒ずつ順送りにする。最後の再描画はその後ろへ回す。
        int roleCount = OptionRemoveRoleCount.GetInt();
        List<PlayerControl> rolePool = [..crews];
        var resetTargets = new List<PlayerControl>();
        for (int i = 0; i < roleCount && rolePool.Count > 0; i++)
        {
            int idx = IRandom.Instance.Next(0, rolePool.Count);
            PlayerControl crew = rolePool[idx];
            rolePool.RemoveAt(idx);
            if (!crew.IsAlive()) { i--; continue; }

            resetTargets.Add(crew);
        }

        float delay = 0.1f;
        foreach (PlayerControl crew in resetTargets)
        {
            PlayerControl target = crew;
            LateTask.New(() =>
            {
                if (!GameStates.IsInGame || !target.IsAlive()) return;

                target.RpcSetCustomRole(CustomRoles.Crewmate);
                Logger.Info($"{target.name}'s role was reset to Crewmate by King", "KingRole");
            }, delay, "KingRoleReset");

            delay += 0.15f;
        }

        LateTask.New(() => Utils.NotifyRoles(ForceLoop: true, NoCache: true), Mathf.Max(0.4f, delay + 0.2f), "KingAboooonNotify");
    }

    public static void ManipulateVotingResult(Dictionary<byte, int> votingData, MeetingHud.VoterState[] states)
    {
        if (PlayerIdList.Count == 0) return;

        int threshold = OptionExileVoteCount.GetInt();
        foreach (byte kingId in PlayerIdList)
        {
            if (!votingData.TryGetValue(kingId, out int count)) continue;
            if (count >= threshold)
            {
                votingData[kingId] = 999;
                Logger.Info($"King {kingId} got {count} >= {threshold} votes, forcing exile", "King");
            }
        }
    }
}
