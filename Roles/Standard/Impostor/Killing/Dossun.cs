using System;
using System.Collections.Generic;
using EndKnot.Modules;
using UnityEngine;

namespace EndKnot.Roles;

public class Dossun : RoleBase
{
    private const int Id = 704500;
    public static bool On;

    public static OptionItem AbilityCooldown;
    private static OptionItem KnockbackDistance;
    private static OptionItem BlockDuration;

    // ブロック/プレイヤーの当たり判定に使う半径。CNO は 2x2 の <size=300%> スプライトなので
    // おおよそプレイヤー本体 1 体分の見た目サイズになる → 半径 0.6、プレイヤー本体側は 0.4 で猶予を持たせる
    private const float BlockHalfWidth = 0.6f;
    private const float PlayerHalfWidth = 0.4f;
    private const float HitRange = BlockHalfWidth + PlayerHalfWidth;

    private const float MinSpeedToHit = 0.5f; // units/s
    private const float MinMoveToUpdate = 0.05f; // units/frame
    private const float CrushCheckDistance = 1.0f;
    private const long PerVictimHitCooldownSeconds = 1;
    private static readonly float[] KnockbackFallbackMultipliers = [1f, 0.66f, 0.33f];

    public enum Phase { None, Placed, Active }

    // Utils.ShouldNotApplyAbilityCooldown が参照するため public (設置直後は CD 免除で即起動できるようにする)
    public Phase CurrentPhase;
    private byte DossunId;
    private string AnchorRoomName = string.Empty;
    private byte ArrowTargetId = byte.MaxValue;
    private Vector2 Anchor;
    private Vector2 ControlOrigin;
    private Vector2 LastBlockPos;
    private Vector2 BlockVelocity;
    private long ActivatedTimeStamp;
    private DossunBlock BlockCNO;
    private readonly Dictionary<byte, long> LastHitTimestamp = [];

    public override bool IsEnable => On;

    public override void SetupCustomOption()
    {
        StartSetup(Id)
            .AutoSetupOption(ref AbilityCooldown, 15, new IntegerValueRule(1, 60, 1), OptionFormat.Seconds)
            .AutoSetupOption(ref KnockbackDistance, 3, new IntegerValueRule(1, 10, 1), OptionFormat.Multiplier)
            .AutoSetupOption(ref BlockDuration, 30, new IntegerValueRule(5, 120, 1), OptionFormat.Seconds);
    }

    public override void Init()
    {
        On = false;
    }

    public override void Add(byte playerId)
    {
        On = true;
        DossunId = playerId;
        ResetState();
    }

    public override void Remove(byte playerId)
    {
        DespawnBlock();
    }

    private void ResetState()
    {
        CurrentPhase = Phase.None;
        Anchor = Vector2.zero;
        ControlOrigin = Vector2.zero;
        LastBlockPos = Vector2.zero;
        BlockVelocity = Vector2.zero;
        ActivatedTimeStamp = 0;
        LastHitTimestamp.Clear();
        DespawnBlock();
    }

    private void DespawnBlock()
    {
        BlockCNO?.Despawn();
        BlockCNO = null;

        if (ArrowTargetId != byte.MaxValue)
        {
            TargetArrow.RemoveAllTarget(DossunId);
            ArrowTargetId = byte.MaxValue;
        }
    }

    public override void OnPet(PlayerControl pc)
    {
        if (!pc.IsAlive() || !GameStates.IsInTask) return;

        switch (CurrentPhase)
        {
            case Phase.None:
                // ブロックを設置する部屋の位置だけ記憶する。CNO はまだ出さない (完全不可視)
                Anchor = pc.Pos();
                CurrentPhase = Phase.Placed;
                PlainShipRoom room = pc.GetPlainShipRoom();
                AnchorRoomName = room == null ? Translator.GetString("Outside") : Translator.GetString($"{room.RoomId}");
                pc.Notify(string.Format(Translator.GetString("Dossun.Placed"), AnchorRoomName));
                break;

            case Phase.Placed:
                // ここで初めて全員可視の CNO を出し、以降の自分の移動をブロックの間接操作に変換する
                ControlOrigin = pc.Pos();
                LastBlockPos = Anchor;
                BlockVelocity = Vector2.zero;
                ActivatedTimeStamp = Utils.TimeStamp;
                Utils.CombineSendTimeLowering(() =>
                {
                    BlockCNO = new DossunBlock(Anchor);
                });
                CurrentPhase = Phase.Active;

                // 起動地点とアンカーが離れているとブロックは永久にカメラ外 (ブロックとの距離は起動時の
                // アンカー距離で固定される) なので、本人にだけ方向矢印を出して間接操作を補助する。
                // CNO は実 PlayerId を持つので TargetArrow が追加 RPC なしで移動へ自動追従する
                ArrowTargetId = BlockCNO.playerControl.PlayerId;
                TargetArrow.Add(pc.PlayerId, ArrowTargetId);
                pc.Notify(string.Format(Translator.GetString("Dossun.Activated"), AnchorRoomName));
                break;

            case Phase.Active:
                DespawnBlock();
                CurrentPhase = Phase.None;
                pc.Notify(Translator.GetString("Dossun.Recalled"));
                break;
        }
    }

    public override void OnFixedUpdate(PlayerControl pc)
    {
        if (!AmongUsClient.Instance.AmHost) return;

        if (!pc.IsAlive())
        {
            if (CurrentPhase != Phase.None)
            {
                DespawnBlock();
                CurrentPhase = Phase.None;
            }
            return;
        }

        if (!GameStates.IsInTask) return;
        if (CurrentPhase != Phase.Active || BlockCNO == null) return;

        if (Utils.TimeStamp - ActivatedTimeStamp >= BlockDuration.GetInt())
        {
            DespawnBlock();
            CurrentPhase = Phase.None;
            pc.Notify(Translator.GetString("Dossun.Expired"));
            return;
        }

        Vector2 newBlockPos = Anchor + (pc.Pos() - ControlOrigin);
        Vector2 delta = newBlockPos - LastBlockPos;

        if (delta.magnitude > MinMoveToUpdate)
        {
            BlockVelocity = delta / Time.fixedDeltaTime;
            BlockCNO.TP(newBlockPos);
            LastBlockPos = newBlockPos;
        }
        else
        {
            // 止まっているブロックは轢く力を持たない
            BlockVelocity = Vector2.zero;
        }

        if (BlockVelocity.magnitude > MinSpeedToHit)
            CheckBlockHits(pc, LastBlockPos);
    }

    private void CheckBlockHits(PlayerControl dossun, Vector2 blockPos)
    {
        long now = Utils.TimeStamp;
        Vector2 dir = BlockVelocity.normalized;

        // 轢殺の SetDead → ForceRebuildCachesPlayerControls が生きキャッシュを Clear するため、スナップショットで列挙する
        foreach (PlayerControl target in Main.AllAlivePlayerControlsToArray)
        {
            if (target.PlayerId == dossun.PlayerId) continue;
            if (target.PlayerId >= 200) continue; // CNO 除外
            if (LastHitTimestamp.TryGetValue(target.PlayerId, out long lastHit) && lastHit + PerVictimHitCooldownSeconds > now) continue;

            Vector2 pos = target.GetTruePosition();
            if (Mathf.Abs(pos.x - blockPos.x) > HitRange || Mathf.Abs(pos.y - blockPos.y) > HitRange) continue;

            LastHitTimestamp[target.PlayerId] = now;

            Vector2 victimPos = target.GetTruePosition();
            bool crushed = PhysicsHelpers.AnyNonTriggersBetween(victimPos, dir, CrushCheckDistance, Constants.ShipAndObjectsMask);

            if (crushed)
            {
                // 壁との間に挟まれた → CheckMurder バイパスで確定キル (WaveCannon/Abyssbringer と同じパターン)
                target.RpcExileV2();
                RPC.PlaySoundRPC(dossun.PlayerId, Sounds.KillSound);

                PlayerState state = Main.PlayerStates[target.PlayerId];
                state.deathReason = PlayerState.DeathReason.Crushed;
                state.RealKiller = (DateTime.Now, dossun.PlayerId);
                state.SetDead();

                Utils.AfterPlayerDeathTasks(target);
                continue;
            }

            // 壁が無ければノックバック。指定距離の先が壁ならより近い距離へ段階的にフォールバック
            float knockDist = KnockbackDistance.GetFloat();
            foreach (float mult in KnockbackFallbackMultipliers)
            {
                float dist = knockDist * mult;
                if (!PhysicsHelpers.AnyNonTriggersBetween(victimPos, dir, dist, Constants.ShipAndObjectsMask))
                {
                    target.TP(victimPos + dir * dist);
                    break;
                }
            }
        }
    }

    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud = false, bool meeting = false)
    {
        if (seer.PlayerId != DossunId || seer.PlayerId != target.PlayerId || (seer.IsModdedClient() && !hud) || meeting || !seer.IsAlive()) return string.Empty;
        if (CurrentPhase != Phase.Active) return string.Empty;

        return Utils.ColorString(Utils.GetRoleColor(CustomRoles.Dossun), TargetArrow.GetArrows(seer, ArrowTargetId));
    }

    public override void OnReportDeadBody()
    {
        if (CurrentPhase == Phase.None) return;
        DespawnBlock();
        CurrentPhase = Phase.None;
    }
}
