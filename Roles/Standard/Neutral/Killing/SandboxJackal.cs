using System.Collections.Generic;
using System.Linq;
using AmongUs.GameOptions;
using EndKnot.Modules;
using EndKnot.Patches;
using UnityEngine;

namespace EndKnot.Roles;

// サンドボックスジャッカル — ジャッカル陣営のキラー。Sandbox と同じブロック (SandboxBlock) を足元に置いて壁を作る。
// キルボタンはキルに使うので、設置はペット / ファントム / シェイプシフトの三系統。
// ブロックの押し戻し・会議跨ぎの保存と張り直しは Sandbox と同じ手順で、状態はこの役職だけで持つ。
public class SandboxJackal : RoleBase
{
    public static bool On;
    public override bool IsEnable => On;

    private static OptionItem KillCooldown;
    private static OptionItem CanVent;
    private static OptionItem CanSabotage;
    private static OptionItem ImpostorVision;
    private static OptionItem MaxBlocks;
    public static OptionItem PlaceCooldown;
    private static OptionItem BlockRadius;

    private static Dictionary<byte, List<SandboxBlock>> ActiveBlocks = [];
    private static Dictionary<byte, List<Vector2>> SavedBlockPositions = [];
    private static Dictionary<byte, float> LastPlaceTime = [];

    private static bool PhantomBasis => Options.UsePhantomBasis.GetBool() && Options.UsePhantomBasisForNKs.GetBool();

    public override void SetupCustomOption()
    {
        StartSetup(709900)
            .AutoSetupOption(ref KillCooldown, 22.5f, new FloatValueRule(0f, 180f, 0.5f), OptionFormat.Seconds)
            .AutoSetupOption(ref CanVent, true)
            .AutoSetupOption(ref CanSabotage, true)
            .AutoSetupOption(ref ImpostorVision, true)
            .AutoSetupOption(ref MaxBlocks, 5, new IntegerValueRule(1, 15, 1), OptionFormat.Pieces, overrideName: "SandboxMaxBlocks")
            .AutoSetupOption(ref PlaceCooldown, 3f, new FloatValueRule(0f, 30f, 0.5f), OptionFormat.Seconds, overrideName: "SandboxPlaceCooldown")
            .AutoSetupOption(ref BlockRadius, 0.5f, new FloatValueRule(0.2f, 1.5f, 0.05f), OptionFormat.Multiplier, overrideName: "SandboxBlockRadius");
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

    public override void SetKillCooldown(byte id)
    {
        Main.AllPlayerKillCooldown[id] = KillCooldown.GetFloat();
    }

    public override bool CanUseImpostorVentButton(PlayerControl pc)
    {
        return CanVent.GetBool();
    }

    public override bool CanUseSabotage(PlayerControl pc)
    {
        return base.CanUseSabotage(pc) || (CanSabotage.GetBool() && pc.IsAlive());
    }

    public override void ApplyGameOptions(IGameOptions opt, byte id)
    {
        opt.SetVision(ImpostorVision.GetBool());

        float cd = PlaceCooldown.GetFloat();
        if (IntroCutsceneDestroyPatch.PreventKill) cd = Mathf.Max(cd, (Options.StartingKillCooldown?.GetFloat() ?? 10f) + 2f);

        if (PhantomBasis)
        {
            AURoleOptions.PhantomCooldown = cd;
            AURoleOptions.PhantomDuration = 1f;
        }
        else if (!Options.UsePets.GetBool())
        {
            AURoleOptions.ShapeshifterCooldown = cd;
            AURoleOptions.ShapeshifterDuration = 1f;
        }
    }

    public override void SetButtonTexts(HudManager hud, byte id)
    {
        hud.SabotageButton?.ToggleVisible(CanSabotage.GetBool());

        if (Options.UsePets.GetBool() && !PhantomBasis)
            hud.PetButton?.OverrideText(Translator.GetString("SandboxJackalButtonText"));
        else
            hud.AbilityButton?.OverrideText(Translator.GetString("SandboxJackalButtonText"));
    }

    public override void OnPet(PlayerControl pc)
    {
        PlaceBlock(pc);
    }

    public override bool OnVanish(PlayerControl pc)
    {
        PlaceBlock(pc);
        return false;
    }

    public override bool OnShapeshift(PlayerControl shapeshifter, PlayerControl target, bool shapeshifting)
    {
        if (!shapeshifting) return true;
        PlaceBlock(shapeshifter);
        return false;
    }

    private static void PlaceBlock(PlayerControl pc)
    {
        if (!GameStates.IsInTask || ExileController.Instance || !pc.IsAlive()) return;

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

        var block = new SandboxBlock(pc.Pos(), pc.PlayerId, BlockRadius.GetFloat() + Sandbox.PlayerColliderRadius);
        list.Add(block);
        LastPlaceTime[pc.PlayerId] = now;

        pc.Notify(Translator.GetString("SandboxBlockPlaced"));
    }

    public override void OnFixedUpdate(PlayerControl pc)
    {
        if (!pc.IsAlive() || !GameStates.IsInTask || ExileController.Instance) return;
        if (!ActiveBlocks.TryGetValue(pc.PlayerId, out var blocks) || blocks.Count == 0) return;

        // 当たり判定はブロックの視覚半径 + プレイヤー半径 (Sandbox と同じ)
        float triggerRadius = BlockRadius.GetFloat() + Sandbox.PlayerColliderRadius;

        foreach (var block in blocks)
        {
            Vector2 center = block.Position;
            foreach (PlayerControl target in FastVector2.GetPlayersInRange(center, triggerRadius, _ => true))
            {
                if (!target.IsAlive()) continue;
                Sandbox.PushToEdge(target, center, triggerRadius, 0.1f);
            }
        }
    }

    public override void OnReportDeadBody()
    {
        // ブロックの CNO は SandboxBlock.OnMeeting で消えるので、ここでは位置だけ保存する
        foreach ((byte owner, var blocks) in ActiveBlocks)
        {
            // 張り直し前に次の会議が来ると ActiveBlocks は空のまま。空なら前会議の保存を持ち越す
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
            if (MeetingStates.MeetingNum != meetingNum) return;
            if (GameStates.IsMeeting || ExileController.Instance) return;

            // ブロック1個ごとに CreateNetObject が飛ぶので、同一フレームに積まないよう順送りにずらす
            var index = 0;

            foreach ((byte owner, var positions) in SavedBlockPositions)
            {
                if (positions.Count == 0) continue;

                if (!ActiveBlocks.TryGetValue(owner, out var list))
                {
                    list = ActiveBlocks[owner] = [];
                }

                float visualRadius = BlockRadius.GetFloat() + Sandbox.PlayerColliderRadius;
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
            // ⚠️ 外側 delay は 10 秒から縮めないこと (追放後の送信が集中する窓に CreateNetObject の連鎖を重ねない)
        }, 10f, "SandboxJackal.RespawnBlocks");
    }
}
