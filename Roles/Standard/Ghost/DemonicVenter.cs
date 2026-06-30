using AmongUs.GameOptions;
using EndKnot.Modules;
using UnityEngine;

namespace EndKnot.Roles;

internal class DemonicVenter : IGhostRole
{
    private static OptionItem CD;

    public Team Team => Team.Impostor;
    public RoleTypes RoleTypes => RoleTypes.GuardianAngel;
    public int Cooldown => CD.GetInt();

    public void OnProtect(PlayerControl pc, PlayerControl target)
    {
        if (ShipStatus.Instance == null) return;

        Vector2 targetPos = target.Pos();
        Vector2 originalPos = pc.Pos();

        // Find the vent nearest to the protected target
        Vent nearestVent = null;
        var minDist = float.MaxValue;
        foreach (Vent vent in ShipStatus.Instance.AllVents)
        {
            float dist = Vector2.Distance(targetPos, vent.transform.position);
            if (dist < minDist)
            {
                minDist = dist;
                nearestVent = vent;
            }
        }

        if (nearestVent == null) return;

        // Hop the ghost onto it and pop it open, then return — a harmless prank, no buff/debuff
        Utils.TP(pc.NetTransform, (Vector2)nearestVent.transform.position, noCheckState: true, log: false);
        LateTask.New(() => pc.MyPhysics?.RpcExitVent(nearestVent.Id), 0.2f, "DemonicVenter ExitVent");
        LateTask.New(() => Utils.TP(pc.NetTransform, originalPos, noCheckState: true, log: false), 0.6f, "DemonicVenter Return");

        pc.AddAbilityCD(Cooldown);
    }

    public void OnAssign(PlayerControl pc) { }

    public void SetupCustomOption()
    {
        Options.SetupRoleOptions(650200, TabGroup.OtherRoles, CustomRoles.DemonicVenter);

        CD = new IntegerOptionItem(650202, "AbilityCooldown", new(0, 180, 1), 25, TabGroup.OtherRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.DemonicVenter])
            .SetValueFormat(OptionFormat.Seconds);
    }
}
