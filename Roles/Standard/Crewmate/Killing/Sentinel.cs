using System.Collections.Generic;
using System.Linq;
using AmongUs.GameOptions;
using EndKnot.Modules.Extensions;
using static EndKnot.Options;
using static EndKnot.Translator;
using static EndKnot.Utils;

namespace EndKnot.Roles;

public class PatrollingState(byte sentinelId, int patrolDuration, float patrolRadius, PlayerControl sentinel = null, bool isPatrolling = false, Vector2? startingPosition = null)
{
    private List<byte> LastNearbyKillers = [];

    public byte SentinelId => sentinelId;

    public PlayerControl Sentinel { get; private set; } = sentinel;

    public bool IsPatrolling { get; private set; } = isPatrolling;

    private Vector2 StartingPosition { get; set; } = startingPosition ?? Vector2.zero;

    private int PatrolDuration => patrolDuration;

    private float PatrolRadius => patrolRadius;

    private CountdownTimer Timer;

    public IEnumerable<PlayerControl> NearbyKillers => Sentinel == null ? [] : FastVector2.GetPlayersInRange(StartingPosition, PatrolRadius).Where(x => !x.Is(Team.Crewmate) && (!Sentinel.IsMadmate() || !x.Is(Team.Impostor)) && SentinelId != x.PlayerId);

    // 巡回は本人が生きている間だけの仕掛け。CountdownTimer はホルダーの FixedUpdate に依存しないので、
    // 死亡を見ないと死体の座標で PatrolDuration 秒ぶん効果が残り続ける。
    public bool HolderIsAlive => Sentinel != null && Sentinel.IsAliveWithConditions();
    
    public void SetPlayer()
    {
        Sentinel = GetPlayerById(SentinelId);
    }

    public void StartPatrolling()
    {
        if (IsPatrolling) return;
        IsPatrolling = true;
        StartingPosition = Sentinel.Pos();
        NearbyKillers.Do(x => x.Notify(string.Format(GetString("KillerNotifyPatrol"), PatrolDuration)));
        Sentinel.MarkDirtySettings();
        Timer = new CountdownTimer(PatrolDuration, FinishPatrolling, onTick: CheckPlayerPositions, onCanceled: () =>
        {
            Timer = null;
            IsPatrolling = false;
        });
    }

    private void CheckPlayerPositions()
    {
        if (!IsPatrolling) return;

        // ホルダーが死んだら巡回ごと畳む。ブロックも反撃も道連れも起こさない。
        if (!HolderIsAlive)
        {
            StopPatrolling();
            return;
        }

        List<byte> killers = NearbyKillers.Select(x => x.PlayerId).ToList();
        int timeLeft = (int)Timer.Remaining.TotalSeconds;

        foreach (PlayerControl pc in Main.EnumerateAlivePlayerControls())
        {
            bool nowInRange = killers.Contains(pc.PlayerId);
            bool wasInRange = LastNearbyKillers.Contains(pc.PlayerId);

            if (wasInRange && !nowInRange) pc.Notify(GetString("KillerEscapedFromSentinel"));
            if (nowInRange && timeLeft >= 0) pc.Notify(string.Format(GetString("KillerNotifyPatrol"), timeLeft), 3f, true);
        }

        LastNearbyKillers = killers;
    }

    // Dispose はコルーチンを止めるだけで onCanceled を呼ばないので、後始末はここで揃える。
    private void StopPatrolling()
    {
        IsPatrolling = false;
        Timer?.Dispose();
        Timer = null;
        LastNearbyKillers = [];

        // 巡回中は本人の視界を落としているので、畳んだら戻してやる。
        Sentinel?.MarkDirtySettings();
    }

    private void FinishPatrolling()
    {
        Timer = null;
        IsPatrolling = false;

        // 最後の1秒でホルダーが死んだ場合は onTick の番が来ない。道連れの手前でもう一度見る。
        if (HolderIsAlive) NearbyKillers.Do(x => x.Suicide(PlayerState.DeathReason.Patrolled, Sentinel));

        // 視界は生死に関わらず戻す。
        Sentinel?.MarkDirtySettings();
    }
}

internal class Sentinel : RoleBase
{
    public static OptionItem PatrolCooldown;
    private static OptionItem PatrolDuration;
    public static OptionItem LoweredVision;
    private static OptionItem PatrolRadius;
    private static int Id => 64430;

    public static List<PatrollingState> PatrolStates { get; } = [];

    public override bool IsEnable => PatrolStates.Count > 0 || Randomizer.Exists;

    public override void SetupCustomOption()
    {
        SetupRoleOptions(Id, TabGroup.CrewmateRoles, CustomRoles.Sentinel);

        PatrolCooldown = CreateCDSetting(Id + 2, TabGroup.CrewmateRoles, CustomRoles.Sentinel);

        PatrolDuration = new IntegerOptionItem(Id + 3, "SentinelPatrolDuration", new(1, 90, 1), 5, TabGroup.CrewmateRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Sentinel])
            .SetValueFormat(OptionFormat.Seconds);

        LoweredVision = new FloatOptionItem(Id + 4, "FFA_LowerVision", new(0.05f, 3f, 0.05f), 0.2f, TabGroup.CrewmateRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Sentinel])
            .SetValueFormat(OptionFormat.Multiplier);

        PatrolRadius = new FloatOptionItem(Id + 5, "SentinelPatrolRadius", new(0.1f, 25f, 0.1f), 5f, TabGroup.CrewmateRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Sentinel])
            .SetValueFormat(OptionFormat.Multiplier);
    }

    public override void Init()
    {
        PatrolStates.Clear();
    }

    public override void Add(byte playerId)
    {
        var newPatrolState = new PatrollingState(playerId, PatrolDuration.GetInt(), PatrolRadius.GetFloat());
        PatrolStates.Add(newPatrolState);
        LateTask.New(newPatrolState.SetPlayer, 8f, log: false);
    }

    public override void Remove(byte playerId)
    {
        PatrolStates.RemoveAll(x => x.SentinelId == playerId);
    }

    private static PatrollingState GetPatrollingState(byte playerId)
    {
        return PatrolStates.FirstOrDefault(x => x.SentinelId == playerId) ?? new(playerId, PatrolDuration.GetInt(), PatrolRadius.GetInt());
    }

    public static bool IsPatrolling(byte playerId)
    {
        return GetPatrollingState(playerId)?.IsPatrolling == true;
    }

    public override void OnEnterVent(PlayerControl pc, Vent vent)
    {
        if (UsePets.GetBool()) return;
        GetPatrollingState(pc.PlayerId)?.StartPatrolling();
    }

    public override void OnPet(PlayerControl pc)
    {
        GetPatrollingState(pc.PlayerId)?.StartPatrolling();
    }

    public static bool OnAnyoneCheckMurder(PlayerControl killer, bool check = false)
    {
        if (killer == null || !PatrolStates.Any(x => x.IsPatrolling)) return true;

        foreach (PatrollingState state in PatrolStates)
        {
            if (!state.IsPatrolling) continue;

            // PatrolStates は役職変更でしか掃除されない。死亡と次の onTick の間に隙間があるので
            // 関所側でも生死を見る。ループの外で return すると生きている2人目の出番が消える。
            if (!state.HolderIsAlive) continue;

            if (state.NearbyKillers.Any(x => x.PlayerId == killer.PlayerId))
            {
                // 打診で撃ち返すと、まだ誰も殴っていない相手を照準だけで殺してしまう。
                if (check) return false;

                AttackDefense.Retaliate(state.Sentinel, killer, performKill: true);
                return false;
            }
        }

        return true;
    }

    public override void ApplyGameOptions(IGameOptions opt, byte playerId)
    {
        AURoleOptions.EngineerCooldown = PatrolCooldown.GetFloat();
        AURoleOptions.EngineerInVentMaxTime = 1f;
    }

    public override bool CanUseVent(PlayerControl pc, int ventId)
    {
        return !IsThisRole(pc) || pc.Is(CustomRoles.Nimble) || pc.GetClosestVent()?.Id == ventId;
    }

    public override void ManipulateGameEndCheckCrew(PlayerState playerState, out bool keepGameGoing, out int countsAs)
    {
        if (playerState.IsDead)
        {
            base.ManipulateGameEndCheckCrew(playerState, out keepGameGoing, out countsAs);
            return;
        }

        keepGameGoing = true;
        countsAs = 1;
    }
}