using System;
using System.Collections;
using System.Collections.Generic;
using EndKnot.Modules;
using UnityEngine;

namespace EndKnot.Roles;

public class ForceFielder : RoleBase
{
    public static bool On;

    private static OptionItem FieldRadius;
    private static OptionItem SpeedMultiplier;
    private static OptionItem PropelForce;
    private static OptionItem ActivationCooldown;

    private byte ForceFielderId;
    private bool FieldActive;
    private ForceFieldCNO FieldCNO;
    private float LastToggleTime;
    private float OriginalSpeed;
    private HashSet<byte> CurrentlyPropelling;

    public override bool IsEnable => On;

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(703750, TabGroup.CrewmateRoles, CustomRoles.ForceFielder);
        FieldRadius = new FloatOptionItem(703752, "ForceFielder.FieldRadius", new(1f, 8f, 0.5f), 3f, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.ForceFielder]);
        SpeedMultiplier = new FloatOptionItem(703753, "ForceFielder.SpeedMultiplier", new(0.1f, 1f, 0.1f), 0.5f, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.ForceFielder]);
        PropelForce = new FloatOptionItem(703754, "ForceFielder.PropelForce", new(0.5f, 5f, 0.5f), 2f, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.ForceFielder]);
        ActivationCooldown = new FloatOptionItem(703755, "ForceFielder.ActivationCooldown", new(0f, 30f, 2.5f), 10f, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.ForceFielder]);
    }

    public override void Init()
    {
        On = false;
    }

    public override void Add(byte playerId)
    {
        On = true;
        ForceFielderId = playerId;
        FieldActive = false;
        FieldCNO = null;
        LastToggleTime = -999f;
        OriginalSpeed = 0f;
        CurrentlyPropelling = [];
    }

    public override void OnPet(PlayerControl pc)
    {
        if (!GameStates.IsInTask || ExileController.Instance) return;
        if (Time.time - LastToggleTime < ActivationCooldown.GetFloat())
        {
            pc.Notify(Translator.GetString("ForceFielder.Cooldown"));
            return;
        }

        LastToggleTime = Time.time;
        FieldActive = !FieldActive;

        if (FieldActive)
        {
            OriginalSpeed = Main.AllPlayerSpeed[pc.PlayerId];
            Main.AllPlayerSpeed[pc.PlayerId] = Math.Max(Main.MinSpeed, OriginalSpeed * SpeedMultiplier.GetFloat());
            pc.MarkDirtySettings();
            FieldCNO = new ForceFieldCNO(pc.Pos(), FieldRadius.GetFloat());
            pc.Notify(Translator.GetString("ForceFielder.FieldOn"));
        }
        else
        {
            DeactivateField(pc);
            pc.Notify(Translator.GetString("ForceFielder.FieldOff"));
        }
    }

    public override void OnFixedUpdate(PlayerControl pc)
    {
        if (!pc.IsAlive() || !GameStates.IsInTask || ExileController.Instance) return;
        if (!FieldActive) return;

        if (FieldCNO != null) FieldCNO.Position = pc.Pos();

        Vector2 center = pc.Pos();
        foreach (PlayerControl target in FastVector2.GetPlayersInRange(center, FieldRadius.GetFloat(), p => p.PlayerId != pc.PlayerId))
        {
            if (CurrentlyPropelling.Add(target.PlayerId))
                Main.Instance.StartCoroutine(Propel(pc, target));
        }
    }

    private IEnumerator Propel(PlayerControl forceFielder, PlayerControl target)
    {
        Vector2 dir = target.Pos() - forceFielder.Pos();
        if (dir == Vector2.zero) dir = Vector2.up;
        dir = dir.normalized;

        Vector2 addVector = dir * 0.15f;
        Vector2 pushStart = target.Pos();
        float pushLimit = PropelForce.GetFloat();
        Collider2D collider = target.Collider;

        while (GameStates.IsInTask && FieldActive && target.IsAlive()
               && FastVector2.DistanceWithinRange(pushStart, target.Pos(), pushLimit))
        {
            Vector2 newPos = target.Pos() + addVector;

            if (PhysicsHelpers.AnythingBetween(collider, collider.bounds.center, newPos + addVector * 2, Constants.ShipOnlyMask, false))
                break;

            target.TP(newPos, log: false);

            if (!target.IsInsideMap())
            {
                Vector2 playerPosition = target.Pos();
                Vector2 closestSpawn = FastVector2.TryGetClosest(playerPosition, RandomSpawn.SpawnMap.GetSpawnMap().Positions.Values, out Vector2 sp) ? sp : new(50f, 50f);
                Vector3 closestVent = target.GetClosestVent()?.transform.position ?? closestSpawn;
                target.TP(Vector2.Distance(playerPosition, closestVent) < Vector2.Distance(playerPosition, closestSpawn) ? closestVent : closestSpawn);
                break;
            }

            yield return new WaitForSecondsRealtime(0.05f);
        }

        CurrentlyPropelling.Remove(target.PlayerId);
    }

    public override void OnReportDeadBody()
    {
        PlayerControl pc = ForceFielderId.GetPlayer();
        if (pc == null || !FieldActive) return;
        DeactivateField(pc);
    }

    private void DeactivateField(PlayerControl pc)
    {
        FieldActive = false;
        Main.AllPlayerSpeed[pc.PlayerId] = OriginalSpeed;
        pc.MarkDirtySettings();
        FieldCNO?.Despawn();
        FieldCNO = null;
    }
}
