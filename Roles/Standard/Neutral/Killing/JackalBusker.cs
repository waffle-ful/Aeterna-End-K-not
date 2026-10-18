using System;
using AmongUs.GameOptions;
using EndKnot.Modules;
using EndKnot.Modules.Extensions;
using EndKnot.Patches;
using Hazel;
using UnityEngine;

namespace EndKnot.Roles;

// ジャッカルバスカー — ジャッカル陣営のキラー。EvilBusker と同じく一時的に幽霊
// (RoleTypes.GuardianAngel) になり、AbilityDuration 秒後に自動で元の姿へ戻る。
// 発動はペット / ファントム / シェイプシフトの三系統 (キルボタンはキルに使う)。
// 幽霊化と復帰の手順は EvilBusker と同じ (戻し先は現在の役職を読む・キルクールダウンを立て直す)。
public class JackalBusker : RoleBase
{
    public static bool On;

    private static OptionItem KillCooldown;
    private static OptionItem CanVent;
    private static OptionItem CanSabotage;
    private static OptionItem ImpostorVision;
    public static OptionItem AbilityCooldown;
    public static OptionItem AbilityDuration;
    private static OptionItem AbilityUseLimit;
    // キルでの回数増は Utils.IncreaseAbilityUseLimitOnKill がこのオプション名で拾う (役職側で足すと二重になる)
    private static OptionItem AbilityUseGainWithEachKill;

    private byte JackalBuskerId;
    public CountdownTimer Timer;

    public override bool IsEnable => On;

    // GetDYRole と同じ条件 (第三陣営がファントム基底になるのは UsePhantomBasisForNKs も ON の時だけ)
    private static bool PhantomBasis => Options.UsePhantomBasis.GetBool() && Options.UsePhantomBasisForNKs.GetBool();

    public override void SetupCustomOption()
    {
        StartSetup(709800)
            .AutoSetupOption(ref KillCooldown, 22.5f, new FloatValueRule(0f, 180f, 0.5f), OptionFormat.Seconds)
            .AutoSetupOption(ref CanVent, true)
            .AutoSetupOption(ref CanSabotage, true)
            .AutoSetupOption(ref ImpostorVision, true)
            .AutoSetupOption(ref AbilityCooldown, 15, new IntegerValueRule(0, 120, 1), OptionFormat.Seconds)
            .AutoSetupOption(ref AbilityDuration, 10, new IntegerValueRule(1, 30, 1), OptionFormat.Seconds)
            .AutoSetupOption(ref AbilityUseLimit, 2f, new FloatValueRule(0, 20, 0.05f), OptionFormat.Times)
            .AutoSetupOption(ref AbilityUseGainWithEachKill, 0.3f, new FloatValueRule(0f, 5f, 0.05f), OptionFormat.Times);
    }

    public override void Init()
    {
        On = false;
        JackalBuskerId = byte.MaxValue;
        Timer = null;
    }

    public override void Add(byte playerId)
    {
        On = true;
        Timer = null;
        JackalBuskerId = playerId;
        playerId.SetAbilityUseLimit(AbilityUseLimit.GetFloat());
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

    public override void ApplyGameOptions(IGameOptions opt, byte playerId)
    {
        opt.SetVision(ImpostorVision.GetBool());

        float cd = AbilityCooldown.GetFloat();
        if (IntroCutsceneDestroyPatch.PreventKill) cd = Mathf.Max(cd, (Options.StartingKillCooldown?.GetFloat() ?? 10f) + 2f);

        if (PhantomBasis)
            AURoleOptions.PhantomCooldown = cd;
        else if (!Options.UsePets.GetBool())
        {
            AURoleOptions.ShapeshifterCooldown = cd;
            AURoleOptions.ShapeshifterDuration = 1f;
        }

        // 幽霊化中の実体は RoleTypes.GuardianAngel。Protect ボタンが生きたままだと誤操作の元になるので固定で潰す。
        try { AURoleOptions.GuardianAngelCooldown = 900f; }
        catch { }
    }

    public override void SetButtonTexts(HudManager hud, byte id)
    {
        hud.SabotageButton?.ToggleVisible(CanSabotage.GetBool());

        if (Options.UsePets.GetBool() && !PhantomBasis)
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
        BecomeGhostTemporarily(shapeshifter);
        return false;
    }

    void BecomeGhostTemporarily(PlayerControl pc)
    {
        if (pc.GetAbilityUseLimit() < 1f || ReportDeadBodyPatch.MeetingStarted || GameStates.IsMeeting) return;
        pc.RpcRemoveAbilityUse(notify: false);

        Timer = new CountdownTimer(AbilityDuration.GetInt(), () => BecomeAliveAgain(pc), onTick: () => Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc), onCanceled: () => Timer = null);
        Utils.SendRPC(CustomRPC.SyncRoleData, JackalBuskerId);

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

        // 幽霊化中に外部から役職変換を受けてもタイマーは後から発火するので、戻し先は現在の役職を読む
        pc.RpcChangeRoleBasis(pc.GetCustomRole());
        Camouflage.RpcSetSkin(pc);

        // 会議開始からの強制復帰はここまで。以降の再同期は会議明けの一括処理に任せる
        if (onMeeting) return;

        pc.SyncSettings();

        // RpcChangeRoleBasis は Main.AllPlayerKillCooldown に触れないので、キルクールダウンを明示的に立て直す
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
        PlayerControl buskerPc = JackalBuskerId.GetPlayer();
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

    public void ReceiveRPC(MessageReader reader)
    {
        Timer = new CountdownTimer(AbilityDuration.GetInt(), () => Timer = null, onCanceled: () => Timer = null);
    }

    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud = false, bool meeting = false)
    {
        if (seer.PlayerId != JackalBuskerId || seer.PlayerId != target.PlayerId || hud || meeting || Timer == null) return string.Empty;
        return string.Format(Translator.GetString("EvilBuskerSuffix"), (int)Math.Ceiling(Timer.Remaining.TotalSeconds));
    }
}
