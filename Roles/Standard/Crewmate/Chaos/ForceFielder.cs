using System;
using System.Collections.Generic;
using EndKnot.Modules;
using UnityEngine;

namespace EndKnot.Roles;

public class ForceFielder : RoleBase
{
    public static bool On;

    private static OptionItem FieldRadius;
    private static OptionItem SpeedMultiplier;
    private static OptionItem EjectMargin;
    private static OptionItem ActivationCooldown;
    private static OptionItem EjectOnActivation;

    // 試行する射出方向のリスト（外向きベクトルからの相対角、ラジアン）。
    // 自然な外側から始めて、壁ブロックなら徐々に角度をズラして空きを探す。
    // 0°, ±30°, ±60°, ±90°, ±120°, ±150°, 180° の 12 方向。
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

    private byte ForceFielderId;
    private bool FieldActive;
    private ForceFieldCNO FieldCNO;
    private float LastToggleTime;
    private float OriginalSpeed;

    // 展開時に既にフィールド内に居たプレイヤー集合（Mode OFF = TRAP）。これに入っているプレイヤーは
    // フィールド外に出るまで一切 eject されない（お気に入りのトラップ挙動を保持）。
    // フィールド外に出た時点で集合から外れ、以後の再侵入は通常 eject 対象になる。
    private HashSet<byte> ActivationTrapped;

    // 各プレイヤーの最終 eject 時刻。連続スパム防止用のクールダウン。0.5 秒未満なら eject 抑制。
    // → 「フィールド内に居続けるなら 2 回/秒で押し戻され続ける」「ネットワークラグ中の重複 SnapTo
    // を抑制」の両立。
    private Dictionary<byte, float> LastEjectTime;

    private const float EjectCooldownSec = 0.5f;

    public override bool IsEnable => On;

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(703750, TabGroup.CrewmateRoles, CustomRoles.ForceFielder);
        FieldRadius = new FloatOptionItem(703752, "ForceFielder.FieldRadius", new(1f, 8f, 0.5f), 3f, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.ForceFielder]);
        SpeedMultiplier = new FloatOptionItem(703753, "ForceFielder.SpeedMultiplier", new(0.1f, 1f, 0.1f), 0.5f, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.ForceFielder]);
        // 旧 PropelForce option を「射出時のフィールド外周からのオフセット距離」として再解釈。
        // 既存の保存値を尊重しつつ、視覚的な「フィールド端からどれだけ外側に弾く」かを調整可能に。
        EjectMargin = new FloatOptionItem(703754, "ForceFielder.PropelForce", new(0.5f, 5f, 0.5f), 1f, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.ForceFielder]);
        ActivationCooldown = new FloatOptionItem(703755, "ForceFielder.ActivationCooldown", new(0f, 30f, 2.5f), 10f, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.ForceFielder]);
        // OFF (default, 「お気に入りの挙動」): 展開時に既に中に居たプレイヤーはそのままトラップ
        // ON: 展開時に中に居たプレイヤーも 1 回だけ eject
        EjectOnActivation = new BooleanOptionItem(703756, "ForceFielder.EjectOnActivation", false, TabGroup.CrewmateRoles)
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
        ActivationTrapped = [];
        LastEjectTime = [];
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

            // EjectOnActivation=OFF (default): 展開時に既にフィールド内に居たプレイヤーを
            //   ActivationTrapped に登録 → そのプレイヤーは脱出するまで一切 eject されない（トラップ）。
            // EjectOnActivation=ON: ActivationTrapped 空 → 全員通常 eject 対象になる。
            ActivationTrapped.Clear();
            LastEjectTime.Clear();
            if (!EjectOnActivation.GetBool())
            {
                Vector2 ctr = pc.Pos();
                float rad = FieldRadius.GetFloat();
                foreach (PlayerControl t in FastVector2.GetPlayersInRange(ctr, rad, p => p.PlayerId != pc.PlayerId))
                    ActivationTrapped.Add(t.PlayerId);
            }

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
        float radius = FieldRadius.GetFloat();
        float ejectDist = radius + EjectMargin.GetFloat() * 0.5f;
        float now = Time.time;

        HashSet<byte> currentlyInside = [];
        foreach (PlayerControl target in FastVector2.GetPlayersInRange(center, radius, p => p.PlayerId != pc.PlayerId))
        {
            byte tid = target.PlayerId;
            currentlyInside.Add(tid);

            // ActivationTrapped に居るプレイヤーは脱出するまで eject しない（お気に入りトラップ挙動）
            if (ActivationTrapped.Contains(tid)) continue;

            // クールダウン: 同じプレイヤーを EjectCooldownSec 未満で再 eject しない（スパム防止 +
            // ネットワークラグ中の Pos() 古い値による重複 SnapTo 抑制）
            if (LastEjectTime.TryGetValue(tid, out float lastT) && now - lastT < EjectCooldownSec) continue;

            EjectFromField(target, center, ejectDist);
            LastEjectTime[tid] = now;
        }

        // フィールド外に脱出したトラップ済みプレイヤーは ActivationTrapped から外す
        // → 再侵入時には通常 eject 対象になる
        ActivationTrapped.IntersectWith(currentlyInside);
    }

    // フィールド内に居るプレイヤーを即座に外周外側へ TP する。
    // 旧 propel + slow + 壁スタック方式は「動けないのに脱出もできない」スタック状態を
    // 生みやすかったため、シンプルに「シールドに触れたら外に弾かれる」設計に切り替え。
    //
    // TP は noCheckState=true で実行：Utils.TP は標準で inMovingPlat / onLadder /
    // ladder animation / vent 入場 animation / AntiTP role で silent fail するため、
    // 「動く床に立っている棒立ちプレイヤーに FF が近づいた瞬間 → eject が黙って失敗して
    // フィールド内に取り残される」という稀現象が発生する。FF の eject は正当な強制動作
    // なので状態チェックをバイパスして必ず排出する。
    private static void EjectFromField(PlayerControl target, Vector2 center, float ejectDist)
    {
        // 自然な外向き方向（FF 中心 → target）から始め、壁ブロックなら徐々に角度をズラして
        // 空きを探す。すべての方向で塞がっていたら最寄りスポーン or vent に飛ばす。
        Vector2 outward = target.Pos() - center;
        if (outward == Vector2.zero) outward = Vector2.up;
        Vector2 baseDir = outward.normalized;
        Collider2D collider = target.Collider;

        // raycast 始点は collider.bounds.center を使う (Car.cs:111 と同パターン)。
        // target.Pos() (=GetTruePosition、足元) を始点にすると collider 範囲外の点になる
        // ため exclusion が効かず、ray が自分のコライダーに即ヒット → 全 12 方向で
        // AnythingBetween が true を返し、毎回 TPToFallback (spawn) に落ちていた。
        Vector2 rayStart = collider != null ? (Vector2)collider.bounds.center : target.Pos();

        foreach (float offset in EjectAngleOffsets)
        {
            Vector2 candidateDir = Rotate(baseDir, offset);
            Vector2 candidate = center + candidateDir * ejectDist;

            if (PhysicsHelpers.AnythingBetween(collider, rayStart, candidate, Constants.ShipOnlyMask, false))
                continue;

            target.TP(candidate, noCheckState: true, log: false);

            // TP 先がマップ外なら fallback 適用
            if (!target.IsInsideMap()) TPToFallback(target);
            return;
        }

        // 全角度で壁ブロック（小部屋に閉じ込められた等）→ fallback
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
        ActivationTrapped.Clear();
        LastEjectTime.Clear();
    }
}
