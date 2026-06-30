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
        if (pc == null || target == null || ShipStatus.Instance == null || pc.MyPhysics == null) return;

        byte ghostId = pc.PlayerId;
        Vector2 targetPos = target.Pos();
        Vector2 originalPos = pc.Pos();

        // Find the vent nearest to the protected target
        Vent nearestVent = null;
        var minDist = float.MaxValue;
        foreach (Vent vent in ShipStatus.Instance.AllVents)
        {
            if (vent == null || vent.transform == null) continue;
            float dist = Vector2.Distance(targetPos, (Vector2)vent.transform.position);
            if (dist < minDist)
            {
                minDist = dist;
                nearestVent = vent;
            }
        }

        if (nearestVent == null) return;

        // Capture only value types in the delayed tasks (never a stale Il2Cpp object), and re-fetch the
        // ghost by id each time.
        //
        // CRITICAL: do NOT call RpcEnterVent on a dead ghost — putting a dead player into a real vent
        // state corrupts the native/IL2CPP heap (intermittent "Internal CLR error (0x80131506)" crash).
        // The TOHK original never enters the vent: it only snaps the ghost onto the vent and fires
        // RpcExitVent, which plays the vent "pop" animation on all clients WITHOUT a real vent transition.
        int ventId = nearestVent.Id;
        Vector2 ventPos = nearestVent.transform.position;

        pc.AddAbilityCD(Cooldown);

        Utils.TP(pc.NetTransform, ventPos, noCheckState: true, log: false);

        LateTask.New(() =>
        {
            PlayerControl p = Utils.GetPlayerById(ghostId);
            if (p == null || p.MyPhysics == null) return;
            p.MyPhysics.RpcExitVent(ventId);
        }, 0.2f, "DemonicVenter ExitVent");

        LateTask.New(() =>
        {
            PlayerControl p = Utils.GetPlayerById(ghostId);
            if (p == null || p.NetTransform == null) return;
            Utils.TP(p.NetTransform, originalPos, noCheckState: true, log: false);
        }, 0.6f, "DemonicVenter Return");
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
