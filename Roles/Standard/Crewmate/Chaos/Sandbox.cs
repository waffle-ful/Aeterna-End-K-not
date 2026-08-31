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

    // Sandbox.PlayerId -> 現在生存している block リスト (順序保持で FIFO Despawn)
    internal static Dictionary<byte, List<SandboxBlock>> ActiveBlocks = [];
    // 会議跨ぎの位置データ (owner -> position list)
    public static Dictionary<byte, List<Vector2>> SavedBlockPositions = [];
    // Pet スパム防止 (owner -> last place time)
    private static Dictionary<byte, float> LastPlaceTime = [];

    // プレイヤー本体の半径 (Among Us の標準プレイヤー collider 相当)。
    // GetPlayersInRange はプレイヤー中心点で距離を測るため、見た目の端で止めるにはこの分を足す必要がある。
    // CNO 視覚もこれを足した radius で描画して「視覚 = プレイヤーが止まる端」を一致させる。
    internal const float PlayerColliderRadius = 0.25f;
    // ブロック中心 → エッジ位置の追加バッファ (再侵入防止の極小オフセット)
    private const float EdgeBuffer = 0.05f;

    // 12 方向のプッシュ候補角 (壁がある場合の迂回先)
    private static readonly float[] PushAngleOffsets =
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

        BlockRadius = new FloatOptionItem(Id + 4, "SandboxBlockRadius", new(0.2f, 1.5f, 0.05f), 0.5f, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Sandbox])
            .SetValueFormat(OptionFormat.Multiplier);
    }

    public override void Init()
    {
        On = false;
        ActiveBlocks = [];
        SavedBlockPositions = [];
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

        var block = new SandboxBlock(pc.Pos(), pc.PlayerId, BlockRadius.GetFloat() + PlayerColliderRadius);
        list.Add(block);
        LastPlaceTime[pc.PlayerId] = now;

        pc.Notify(Translator.GetString("SandboxBlockPlaced"));
    }

    public override void OnFixedUpdate(PlayerControl pc)
    {
        if (!pc.IsAlive() || !GameStates.IsInTask || ExileController.Instance) return;
        if (!ActiveBlocks.TryGetValue(pc.PlayerId, out var blocks) || blocks.Count == 0) return;

        // BlockRadius はブロックの「視覚半径」。プレイヤー中心がここに来た時点では
        // プレイヤーの体は既にブロックに食い込んでいるので、検出/押し戻し共にプレイヤー半径を加算する。
        float triggerRadius = BlockRadius.GetFloat() + PlayerColliderRadius;

        foreach (var block in blocks)
        {
            Vector2 center = block.Position;
            foreach (PlayerControl target in FastVector2.GetPlayersInRange(center, triggerRadius, _ => true))
            {
                if (!target.IsAlive()) continue;
                PushToEdge(target, center, triggerRadius);
            }
        }
    }

    public override void OnReportDeadBody()
    {
        // 全 block の位置を保存し、CNO を Despawn (SandboxBlock.OnMeeting で自動 Despawn されるが、
        // データの保存はここで実施)
        foreach ((byte owner, var blocks) in ActiveBlocks)
        {
            // 再生成 (RespawnBlocks, 会議明け +10 秒) の前に次の会議が来ると ActiveBlocks は空のまま。
            // ここで無条件に上書きすると保存済み位置が空で潰れ、ブロックが永久消失する — 空ならスキップ
            // して前会議の保存を持ち越す (未配置プレイヤーは saved も空なのでスキップで無害)。
            if (blocks.Count == 0) continue;

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

            // ブロック1個の張り直しごとに CreateNetObject (多段 Reliable) が飛ぶので、
            // 保持者数 × MaxBlocks (最大15) を同一フレームで作らないよう順送りにずらす。
            var index = 0;

            foreach ((byte owner, var positions) in SavedBlockPositions)
            {
                if (positions.Count == 0) continue;

                if (!ActiveBlocks.TryGetValue(owner, out var list))
                {
                    list = ActiveBlocks[owner] = [];
                }

                float visualRadius = BlockRadius.GetFloat() + PlayerColliderRadius;
                byte blockOwner = owner;

                foreach (Vector2 pos in positions)
                {
                    Vector2 blockPos = pos;

                    LateTask.New(() =>
                    {
                        if (MeetingStates.MeetingNum != meetingNum) return;
                        if (GameStates.IsMeeting || ExileController.Instance || GameStates.IsEnded || !GameStates.InGame) return;

                        list.Add(new SandboxBlock(blockPos, blockOwner, visualRadius));
                    }, index++ * 0.05f, log: false);
                }
            }

            foreach (var list in SavedBlockPositions.Values) list.Clear();
            // ⚠️ 外側 delay は 10 秒から縮めないこと (基底 CustomNetObject.OnMeeting() と同じ規約)。
            // 1 秒開始だと追放スイープ+ドレイン窓 (task phase 開始後 ~10 秒) にブロック再生成の
            // CreateNetObject 連鎖が丸ごと重なり、合算 nests がキック域に達する。
        }, 10f, "Sandbox.RespawnBlocks");
    }

    // ブロック中心からプレイヤーへの向きへ「半径+極小バッファ」までスナップする。
    // 弾き出しではなく毎フレームのエッジスナップなので、プレイヤーは壁に張り付いた挙動になる。
    // 壁が邪魔して TP できない場合のみ 12 方向の代替角を試す。
    private static void PushToEdge(PlayerControl target, Vector2 center, float radius)
    {
        Vector2 outward = target.Pos() - center;
        if (outward.sqrMagnitude < 0.0001f) outward = Vector2.up;
        Vector2 baseDir = outward.normalized;
        Collider2D collider = target.Collider;
        float pushDist = radius + EdgeBuffer;

        // raycast 始点は collider.bounds.center を使う (Car.cs:111 と同パターン)。
        // target.Pos() を始点にすると collider 範囲外の点になり exclusion が効かず、
        // ray が自分のコライダーに即ヒット → 全 12 方向で AnythingBetween が true 返す
        Vector2 rayStart = collider != null ? (Vector2)collider.bounds.center : target.Pos();

        foreach (float offset in PushAngleOffsets)
        {
            Vector2 candidateDir = Rotate(baseDir, offset);
            Vector2 candidate = center + candidateDir * pushDist;

            if (PhysicsHelpers.AnythingBetween(collider, rayStart, candidate, Constants.ShipOnlyMask, false))
                continue;

            target.TP(candidate, log: false);
            return;
        }
    }

    private static Vector2 Rotate(Vector2 v, float radians)
    {
        float cos = Mathf.Cos(radians);
        float sin = Mathf.Sin(radians);
        return new Vector2(v.x * cos - v.y * sin, v.x * sin + v.y * cos);
    }
}
