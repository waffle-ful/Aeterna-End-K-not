using System.Collections.Generic;

namespace EndKnot.Roles;

public class Sonar : IAddon
{
    private static readonly Dictionary<byte, byte> Target = [];
    public AddonTypes Type => AddonTypes.Helpful;

    public void SetupCustomOption()
    {
        Options.SetupAdtRoleOptions(13642, CustomRoles.Sonar, canSetNum: true, teamSpawnOptions: true);
    }

    public static string GetSuffix(PlayerControl seer, bool meeting)
    {
        if (meeting || !seer.Is(CustomRoles.Sonar) || !Target.TryGetValue(seer.PlayerId, out byte targetId)) return string.Empty;

        return TargetArrow.GetArrows(seer, targetId);
    }

    public static void OnFixedUpdate(PlayerControl seer)
    {
        if (!GameStates.IsInTask || seer.inVent) return;
        
        if (!FastVector2.TryGetClosestPlayerTo(seer, out PlayerControl closest)) return;

        if (Target.TryGetValue(seer.PlayerId, out byte targetId))
        {
            if (targetId != closest.PlayerId)
            {
                if (!ShouldSwitchTarget(seer, targetId, closest)) return;

                Target[seer.PlayerId] = closest.PlayerId;
                TargetArrow.Remove(seer.PlayerId, targetId);
                TargetArrow.Add(seer.PlayerId, closest.PlayerId);
                Utils.NotifyRoles(SpecifySeer: seer, SpecifyTarget: closest);
            }
        }
        else
        {
            Target[seer.PlayerId] = closest.PlayerId;
            TargetArrow.Add(seer.PlayerId, closest.PlayerId);
            Utils.NotifyRoles(SpecifySeer: seer, SpecifyTarget: closest);
        }
    }

    // ほぼ等距離の2人がいると最近傍判定が評価のたびに反転し、矢印がチカチカしつつ
    // TargetArrow.Remove + Add と NotifyRoles が往復する。現ターゲットより明確に近いときだけ乗り換える。
    private const float SwitchMargin = 1f;

    private static bool ShouldSwitchTarget(PlayerControl seer, byte currentId, PlayerControl candidate)
    {
        PlayerControl current = Utils.GetPlayerById(currentId);

        // 現ターゲットが死亡・退出・ベント内・擬似死亡なら最近傍探索の対象外なので即座に乗り換える
        if (current == null || !current.IsAlive() || current.inVent) return true;

        Vector2 origin = seer.Pos();
        return Vector2.Distance(origin, candidate.Pos()) + SwitchMargin <= Vector2.Distance(origin, current.Pos());
    }
}