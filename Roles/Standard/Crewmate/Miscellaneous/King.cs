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

    public override bool OnCheckMurderAsTarget(PlayerControl killer, PlayerControl target)
    {
        killer.SetKillCooldown();
        return false;
    }

    public override bool KnowRole(PlayerControl seer, PlayerControl target)
    {
        if (base.KnowRole(seer, target)) return true;
        return seer.IsCrewmate() && target.Is(CustomRoles.King);
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
        if (count <= 0) return;

        List<PlayerControl> crews = Main.AllAlivePlayerControls
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

        LateTask.New(() => Utils.NotifyRoles(ForceLoop: true, NoCache: true), 0.4f, "KingAboooonNotify");
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
