using System;
using AmongUs.GameOptions;
using EndKnot.Modules;
using EndKnot.Modules.Extensions;
using EndKnot.Patches;
using Hazel;
using UnityEngine;

namespace EndKnot.Roles;

// ============================================================
// EvilBusker (イビルバスカー) — Impostor/Concealing
//
// コンセプト:
//   Astral (Crewmate/Power, Id 651400) の Impostor 版。一時的に幽霊
//   (RoleTypes.GuardianAngel) になり、AbilityDuration 秒後に自動で元の姿へ戻る。
//   CountdownTimer / CustomRPC.SyncRoleData 同期 / GetSuffix 残り秒表示 /
//   OnReportDeadBody での強制復帰といった構造は Astral をそのまま踏襲する。
//
// Astral との差分 (Impostor 化):
//   - 発動トリガはベント(Astral のエンジニア化)ではなく Shapeshift/Vanish/Pet の
//     三系統 (通常のインポスターベントと衝突させないため)。EvilBomber/Twister と
//     同じ「既存 Impostor 役職の標準形」(ApplyGameOptions の Phantom/Shapeshifter
//     分岐 + SetButtonTexts)。
//   - 復帰は RpcSetRoleGlobal ではなく RpcChangeRoleBasis(EvilBusker) を使う
//     (RoleMap も同時に整合する EHR 標準 API)。Astral 未経験の「キル可能な役職への
//     復帰」なので、RpcRevive (ExtendedPlayerControl.cs) に倣いキルクールダウンも
//     明示的に再アサートする。
//   - タスク完了で回数増 (クルー専用) は削除し、キル成立で回数増に置換
//     (Skinwalker.OnMurder と同型)。
// ============================================================
public class EvilBusker : RoleBase
{
    public static bool On;

    private static OptionItem KillCooldown;
    public static OptionItem AbilityCooldown;
    public static OptionItem AbilityDuration;
    private static OptionItem AbilityUseLimit;
    private static OptionItem AbilityUseGainWithEachKill;

    private byte EvilBuskerId;
    public CountdownTimer Timer;

    public override bool IsEnable => On;

    public override void SetupCustomOption()
    {
        StartSetup(706600)
            .AutoSetupOption(ref KillCooldown, 25f, new FloatValueRule(0f, 180f, 0.5f), OptionFormat.Seconds)
            .AutoSetupOption(ref AbilityCooldown, 15, new IntegerValueRule(0, 120, 1), OptionFormat.Seconds)
            .AutoSetupOption(ref AbilityDuration, 10, new IntegerValueRule(1, 30, 1), OptionFormat.Seconds)
            .AutoSetupOption(ref AbilityUseLimit, 2f, new FloatValueRule(0, 20, 0.05f), OptionFormat.Times)
            .AutoSetupOption(ref AbilityUseGainWithEachKill, 0.3f, new FloatValueRule(0f, 5f, 0.05f), OptionFormat.Times);
    }

    public override void Init()
    {
        On = false;
        EvilBuskerId = byte.MaxValue;
        Timer = null;
    }

    public override void Add(byte playerId)
    {
        On = true;
        Timer = null;
        EvilBuskerId = playerId;
        playerId.SetAbilityUseLimit(AbilityUseLimit.GetFloat());
    }

    public override void SetKillCooldown(byte id)
    {
        Main.AllPlayerKillCooldown[id] = KillCooldown.GetFloat();
    }

    public override void ApplyGameOptions(IGameOptions opt, byte playerId)
    {
        float cd = AbilityCooldown.GetFloat();
        if (IntroCutsceneDestroyPatch.PreventKill) cd = Mathf.Max(cd, 12f);

        if (Options.UsePhantomBasis.GetBool())
            AURoleOptions.PhantomCooldown = cd;
        else if (!Options.UsePets.GetBool())
        {
            AURoleOptions.ShapeshifterCooldown = cd;
            AURoleOptions.ShapeshifterDuration = 1f;
        }

        // 幽霊化中の実体は RoleTypes.GuardianAngel (Pet/Shapeshifter/Phantom いずれの
        // トリガでも共通)。Protect ボタンが生きたままだと誤操作の元になるので固定で潰す。
        try { AURoleOptions.GuardianAngelCooldown = 900f; }
        catch { }
    }

    public override void SetButtonTexts(HudManager hud, byte id)
    {
        if (Options.UsePets.GetBool() && !Options.UsePhantomBasis.GetBool())
            hud.PetButton?.OverrideText(Translator.GetString("EvilBuskerAbilityButtonText"));
        else
            hud.AbilityButton?.OverrideText(Translator.GetString("EvilBuskerAbilityButtonText"));
    }

    public override void OnPet(PlayerControl pc)
    {
        BecomeGhostTemporarily(pc);
    }

    public override bool OnVanish(PlayerControl pc)
    {
        BecomeGhostTemporarily(pc);
        return false;
    }

    public override bool OnShapeshift(PlayerControl shapeshifter, PlayerControl target, bool shapeshifting)
    {
        if (!shapeshifting) return true;
        shapeshifter.RpcRejectShapeshift();
        BecomeGhostTemporarily(shapeshifter);
        return false;
    }

    void BecomeGhostTemporarily(PlayerControl pc)
    {
        if (pc.GetAbilityUseLimit() < 1f || ReportDeadBodyPatch.MeetingStarted || GameStates.IsMeeting) return;
        pc.RpcRemoveAbilityUse(notify: false);

        Timer = new CountdownTimer(AbilityDuration.GetInt(), () => BecomeAliveAgain(pc), onTick: () => Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc), onCanceled: () => Timer = null);
        Utils.SendRPC(CustomRPC.SyncRoleData, EvilBuskerId);

        pc.RpcSetRoleGlobal(RoleTypes.GuardianAngel);
        pc.MarkDirtySettings();
        LateTask.New(pc.RpcResetAbilityCooldown, 0.2f, log: false);
    }

    void BecomeAliveAgain(PlayerControl pc, bool onMeeting = false)
    {
        Timer = null;
        if (!pc || !pc.IsAlive()) return;

        GhostRolesManager.RemoveGhostRole(pc.PlayerId);
        ReportDeadBodyPatch.AlreadyReportedBodies.Remove(pc.PlayerId);

        // ⚠️ 戻し先は CustomRoles.EvilBusker 決め打ちにしない。幽霊化中に外部から役職変換
        // (RpcSetCustomRole 経由の Camouflager/Necromancer 等) を受けると、このインスタンスは
        // 差し替えられるがタイマーのコルーチンは生き残って後から発火する。決め打ちだと
        // RpcChangeRoleBasis がロビー全員の RoleMap を「このプレイヤー = EvilBusker」で
        // 塗り潰してしまう。現在の役職を読んで戻せば、通常時は同値・変換後は正しい basis になる。
        pc.RpcChangeRoleBasis(pc.GetCustomRole());
        Camouflage.RpcSetSkin(pc);

        // 会議開始からの強制復帰はここまで。以降の再同期は会議明けの一括処理に任せる
        // (会議開始と同じフレームに送信を積み増さないため — 復帰の SetRole だけでも人数分ある)。
        if (onMeeting) return;

        pc.SyncSettings();

        // キル可能な役職へ戻るので、幽霊状態で止まっていたキルクールダウンの表示/実値を
        // 明示的に立て直す (RpcRevive の ResetKillCooldown + 遅延 SetKillCooldown と同型。
        // RpcChangeRoleBasis 自体は RoleTypes/RoleMap の同期のみで Main.AllPlayerKillCooldown
        // には触れない)。
        pc.ResetKillCooldown();
        LateTask.New(() => pc.SetKillCooldown(), 0.2f, log: false);

        if (!Options.UsePets.GetBool())
            pc.RpcResetAbilityCooldown();

        Utils.NotifyRoles(SpecifySeer: pc);
        Utils.NotifyRoles(SpecifyTarget: pc);

        if (!pc.IsInsideMap())
        {
            Vector2 playerPosition = pc.Pos();
            Vector2 closestSpawnPosition = FastVector2.TryGetClosest(playerPosition, RandomSpawn.SpawnMap.GetSpawnMap().Positions.Values, out Vector2 closest1) ? closest1 : new Vector2(50f, 50f);
            Vector3 closestVentPosition = pc.GetClosestVent()?.transform.position ?? closestSpawnPosition;
            pc.TP(Vector2.Distance(playerPosition, closestVentPosition) < Vector2.Distance(playerPosition, closestSpawnPosition) ? closestVentPosition : closestSpawnPosition);
        }
    }

    public override void OnReportDeadBody()
    {
        PlayerControl buskerPc = EvilBuskerId.GetPlayer();
        if (buskerPc == null) return;

        if (Timer != null)
        {
            Timer.Dispose();
            Timer = null;
            BecomeAliveAgain(buskerPc, true);
        }

        if (!buskerPc.IsAlive()) return;
        ChatManager.ClearChat(buskerPc);
    }

    public override void OnMurder(PlayerControl killer, PlayerControl target)
    {
        if (killer.Is(CustomRoles.EvilBusker) && AbilityUseGainWithEachKill.GetFloat() > 0)
            killer.RpcIncreaseAbilityUseLimitBy(AbilityUseGainWithEachKill.GetFloat());
    }

    public void ReceiveRPC(MessageReader reader)
    {
        Timer = new CountdownTimer(AbilityDuration.GetInt(), () => Timer = null, onCanceled: () => Timer = null);
    }

    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud = false, bool meeting = false)
    {
        if (seer.PlayerId != EvilBuskerId || seer.PlayerId != target.PlayerId || hud || meeting || Timer == null) return string.Empty;
        return string.Format(Translator.GetString("EvilBuskerSuffix"), (int)Math.Ceiling(Timer.Remaining.TotalSeconds));
    }
}
