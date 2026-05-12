using System.Collections.Generic;
using System.Linq;
using EndKnot.Modules;
using UnityEngine;

namespace EndKnot.Roles;

public class Sandbox : RoleBase
{
    private const int Id = 704900;
    public static bool On;
    public override bool IsEnable => On;

    private static OptionItem MaxBlocks;
    public static OptionItem PlaceCooldown;
    private static OptionItem BlockRadius;
    private static OptionItem EjectMargin;

    // Sandbox.PlayerId -> 現在生存している block リスト (順序保持で FIFO Despawn)
    internal static Dictionary<byte, List<SandboxBlock>> ActiveBlocks = [];
    // 会議跨ぎの位置データ (owner -> position list)
    public static Dictionary<byte, List<Vector2>> SavedBlockPositions = [];
    // Eject 連続発動防止 (target -> last eject time)
    private static Dictionary<byte, float> LastEjectTime = [];
    // Pet スパム防止 (owner -> last place time)
    private static Dictionary<byte, float> LastPlaceTime = [];

    private const float EjectCooldownSec = 0.5f;

    // ForceFielder.cs:22-30 と同じ 12 方向の eject 候補角 (ラジアン)
    private static readonly float[] EjectAngleOffsets =
    {
        0f,
        0.5236f, -0.5236f,
        1.0472f, -1.0472f,
        1.5708f, -1.5708f,
        2.0944f, -2.0944f,
        2.6180f, -2.6180f,
        3.1416f
    };

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.CrewmateRoles, CustomRoles.Sandbox);

        MaxBlocks = new IntegerOptionItem(Id + 2, "SandboxMaxBlocks", new(1, 15, 1), 5, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Sandbox])
            .SetValueFormat(OptionFormat.Pieces);

        PlaceCooldown = new FloatOptionItem(Id + 3, "SandboxPlaceCooldown", new(0f, 30f, 0.5f), 3f, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Sandbox])
            .SetValueFormat(OptionFormat.Seconds);

        BlockRadius = new FloatOptionItem(Id + 4, "SandboxBlockRadius", new(0.5f, 2f, 0.1f), 1f, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Sandbox])
            .SetValueFormat(OptionFormat.Multiplier);

        EjectMargin = new FloatOptionItem(Id + 5, "SandboxEjectMargin", new(0.1f, 1.5f, 0.1f), 0.5f, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Sandbox])
            .SetValueFormat(OptionFormat.Multiplier);
    }

    public override void Init()
    {
        On = false;
        ActiveBlocks = [];
        SavedBlockPositions = [];
        LastEjectTime = [];
        LastPlaceTime = [];
    }

    public override void Add(byte playerId)
    {
        On = true;
        ActiveBlocks[playerId] = [];
        SavedBlockPositions[playerId] = [];
    }

    public override void Remove(byte playerId)
    {
        if (ActiveBlocks.TryGetValue(playerId, out var blocks))
        {
            foreach (var block in blocks) block.Despawn();
            blocks.Clear();
        }

        ActiveBlocks.Remove(playerId);
        SavedBlockPositions.Remove(playerId);
        LastPlaceTime.Remove(playerId);
    }

    public override void OnPet(PlayerControl pc)
    {
        if (!GameStates.IsInTask || ExileController.Instance) return;

        float now = Time.time;
        if (LastPlaceTime.TryGetValue(pc.PlayerId, out float lastPlace) && now - lastPlace < PlaceCooldown.GetFloat())
        {
            pc.Notify(Translator.GetString("SandboxCooldownActive"));
            return;
        }

        if (!ActiveBlocks.TryGetValue(pc.PlayerId, out var list))
        {
            list = ActiveBlocks[pc.PlayerId] = [];
        }

        // 上限到達: 最古の block を Despawn
        if (list.Count >= MaxBlocks.GetInt())
        {
            var oldest = list[0];
            oldest.Despawn();
            list.RemoveAt(0);
        }

        var block = new SandboxBlock(pc.Pos(), pc.PlayerId);
        list.Add(block);
        LastPlaceTime[pc.PlayerId] = now;

        pc.Notify(Translator.GetString("SandboxBlockPlaced"));
    }

    public override void OnFixedUpdate(PlayerControl pc)
    {
        if (!pc.IsAlive() || !GameStates.IsInTask || ExileController.Instance) return;
        if (!ActiveBlocks.TryGetValue(pc.PlayerId, out var blocks) || blocks.Count == 0) return;

        float radius = BlockRadius.GetFloat();
        float ejectDist = radius + EjectMargin.GetFloat();
        float now = Time.time;

        foreach (var block in blocks)
        {
            Vector2 center = block.Position;
            foreach (PlayerControl target in FastVector2.GetPlayersInRange(center, radius, _ => true))
            {
                byte tid = target.PlayerId;
                if (!target.IsAlive()) continue;
                if (LastEjectTime.TryGetValue(tid, out float lastT) && now - lastT < EjectCooldownSec) continue;

                EjectFromBlock(target, center, ejectDist);
                LastEjectTime[tid] = now;
            }
        }
    }

    public override void OnReportDeadBody()
    {
        // 全 block の位置を保存し、CNO を Despawn (SandboxBlock.OnMeeting で自動 Despawn されるが、
        // データの保存はここで実施)
        foreach ((byte owner, var blocks) in ActiveBlocks)
        {
            if (!SavedBlockPositions.TryGetValue(owner, out var saved))
            {
                saved = SavedBlockPositions[owner] = [];
            }
            else
            {
                saved.Clear();
            }
            saved.AddRange(blocks.Select(b => b.Position));
            blocks.Clear();
        }

        LastEjectTime.Clear();
        LastPlaceTime.Clear();
    }

    public static void OnAfterMeetingTasks()
    {
        if (!On) return;

        int meetingNum = MeetingStates.MeetingNum;
        LateTask.New(() =>
        {
            // 別会議が始まったらキャンセル
            if (MeetingStates.MeetingNum != meetingNum) return;
            if (GameStates.IsMeeting || ExileController.Instance) return;

            foreach ((byte owner, var positions) in SavedBlockPositions)
            {
                if (positions.Count == 0) continue;

                if (!ActiveBlocks.TryGetValue(owner, out var list))
                {
                    list = ActiveBlocks[owner] = [];
                }

                foreach (Vector2 pos in positions)
                {
                    list.Add(new SandboxBlock(pos, owner));
                }
            }

            foreach (var list in SavedBlockPositions.Values) list.Clear();
        }, 1f, "Sandbox.RespawnBlocks");
    }

    // ForceFielder.cs:172-198 の EjectFromField を Sandbox 用にコピー。
    // block 中心から外向きに 12 方向の空きを探して TP で弾き出す。
    private static void EjectFromBlock(PlayerControl target, Vector2 center, float ejectDist)
    {
        Vector2 outward = target.Pos() - center;
        if (outward == Vector2.zero) outward = Vector2.up;
        Vector2 baseDir = outward.normalized;
        Collider2D collider = target.Collider;

        foreach (float offset in EjectAngleOffsets)
        {
            Vector2 candidateDir = Rotate(baseDir, offset);
            Vector2 candidate = center + candidateDir * ejectDist;

            if (PhysicsHelpers.AnythingBetween(collider, target.Pos(), candidate, Constants.ShipOnlyMask, false))
                continue;

            target.TP(candidate, noCheckState: true, log: false);
            if (!target.IsInsideMap()) TPToFallback(target);
            return;
        }

        TPToFallback(target);
    }

    private static void TPToFallback(PlayerControl target)
    {
        Vector2 playerPos = target.Pos();
        Vector2 closestSpawn = FastVector2.TryGetClosest(playerPos, RandomSpawn.SpawnMap.GetSpawnMap().Positions.Values, out Vector2 sp) ? sp : new(50f, 50f);
        Vector3 closestVent = target.GetClosestVent()?.transform.position ?? closestSpawn;
        target.TP(Vector2.Distance(playerPos, closestVent) < Vector2.Distance(playerPos, closestSpawn) ? closestVent : closestSpawn, noCheckState: true);
    }

    private static Vector2 Rotate(Vector2 v, float radians)
    {
        float cos = Mathf.Cos(radians);
        float sin = Mathf.Sin(radians);
        return new Vector2(v.x * cos - v.y * sin, v.x * sin + v.y * cos);
    }
}
