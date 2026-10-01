using System.Collections.Generic;
using AmongUs.GameOptions;
using EndKnot.Modules;
using EndKnot.Modules.Extensions;
using Hazel;
using static EndKnot.Options;

namespace EndKnot.Roles;

internal class SecurityGuard : RoleBase
{
    public static HashSet<byte> BlockSabo = [];

    public static bool On;
    private static readonly HashSet<byte> SyncedActive = [];
    // 発動中のタイマー。満了まで会議を見ないので、会議開始と次の発動で止めないと次の発動分を消してしまう
    private static readonly Dictionary<byte, CountdownTimer> ActiveTimers = [];
    private byte SecurityGuardId;
    public override bool IsEnable => On;

    public override void SetupCustomOption()
    {
        SetupRoleOptions(6860, TabGroup.CrewmateRoles, CustomRoles.SecurityGuard);

        SecurityGuardSkillCooldown = new FloatOptionItem(6862, "SecurityGuardSkillCooldown", new(0f, 180f, 1f), 15f, TabGroup.CrewmateRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.SecurityGuard])
            .SetValueFormat(OptionFormat.Seconds);

        SecurityGuardSkillDuration = new FloatOptionItem(6863, "SecurityGuardSkillDuration", new(0f, 180f, 1f), 10f, TabGroup.CrewmateRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.SecurityGuard])
            .SetValueFormat(OptionFormat.Seconds);

        SecurityGuardSkillMaxOfUsage = new IntegerOptionItem(6866, "AbilityUseLimit", new(0, 30, 1), 1, TabGroup.CrewmateRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.SecurityGuard])
            .SetValueFormat(OptionFormat.Times);

        SecurityGuardAbilityUseGainWithEachTaskCompleted = new FloatOptionItem(6867, "AbilityUseGainWithEachTaskCompleted", new(0f, 5f, 0.05f), 0.4f, TabGroup.CrewmateRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.SecurityGuard])
            .SetValueFormat(OptionFormat.Times);

        SecurityGuardAbilityChargesWhenFinishedTasks = new FloatOptionItem(6868, "AbilityChargesWhenFinishedTasks", new(0f, 5f, 0.05f), 0.2f, TabGroup.CrewmateRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.SecurityGuard])
            .SetValueFormat(OptionFormat.Times);
    }

    public override void Add(byte playerId)
    {
        On = true;
        SecurityGuardId = playerId;
        playerId.SetAbilityUseLimit(SecurityGuardSkillMaxOfUsage.GetFloat());
    }

    public override void Init()
    {
        On = false;
        SyncedActive.Clear();
        ActiveTimers.Clear();
    }

    public override void ApplyGameOptions(IGameOptions opt, byte playerId)
    {
        if (UsePets.GetBool()) return;

        AURoleOptions.EngineerInVentMaxTime = 1f;
        AURoleOptions.EngineerCooldown = SecurityGuardSkillCooldown.GetFloat();
    }

    // 使用中の色分けは各クライアントが自分で判定するので、使用中かどうかをモッド客へ送る
    private static void SendBlockSync(byte id, bool active)
    {
        if (active) SyncedActive.Add(id);
        else SyncedActive.Remove(id);

        Utils.SendRPC(CustomRPC.SyncRoleData, id, active);
    }

    // 会議開始でホストは使用中の集合を空にするので、客側の保持も同時に解除させる
    public override void OnReportDeadBody()
    {
        if (ActiveTimers.Remove(SecurityGuardId, out CountdownTimer timer)) timer.Dispose();
        if (SyncedActive.Contains(SecurityGuardId)) SendBlockSync(SecurityGuardId, false);
    }

    public void ReceiveRPC(MessageReader reader)
    {
        if (reader.ReadBoolean()) BlockSabo.Add(SecurityGuardId);
        else BlockSabo.Remove(SecurityGuardId);
    }

    public override string GetProgressText(byte playerId, bool comms)
    {
        var progressText = new StringBuilder();

        progressText.Append(Utils.GetAbilityUseLimitDisplay(playerId, BlockSabo.Contains(playerId)));
        progressText.Append(Utils.GetTaskCount(playerId, comms));

        return progressText.ToString();
    }

    // Revert this if needed, recommended to actually add a text called "SaboBlock" or something
    //public override void SetButtonTexts(HudManager hud, byte id)
    //{
    //    if (UsePets.GetBool())
    //        hud.PetButton.buttonLabelText.text = Translator.GetString("SecurityGuardVentButtonText");
    //    else
    //        hud.AbilityButton.buttonLabelText.text = Translator.GetString("SecurityGuardVentButtonText");
    //}

    public override void OnPet(PlayerControl pc)
    {
        Guard(pc);
    }

    public override void OnEnterVent(PlayerControl pc, Vent vent)
    {
        if (UsePets.GetBool()) return;
        Guard(pc);
    }

    private static void Guard(PlayerControl pc)
    {
        if (BlockSabo.Contains(pc.PlayerId)) return;

        if (pc.GetAbilityUseLimit() >= 1)
        {
            BlockSabo.Add(pc.PlayerId);
            SendBlockSync(pc.PlayerId, true);
            if (ActiveTimers.Remove(pc.PlayerId, out CountdownTimer old)) old.Dispose();
            ActiveTimers[pc.PlayerId] = new CountdownTimer(SecurityGuardSkillDuration.GetInt(), () =>
            {
                BlockSabo.Remove(pc.PlayerId);
                SendBlockSync(pc.PlayerId, false);
                pc.RpcResetAbilityCooldown();
                pc.Notify(Translator.GetString("SecurityGuardSkillStop"));
            }, onCanceled: () =>
            {
                BlockSabo.Remove(pc.PlayerId);
                if (GameStates.InGame) SendBlockSync(pc.PlayerId, false);
            });
            pc.Notify(Translator.GetString("SecurityGuardSkillInUse"), SecurityGuardSkillDuration.GetFloat());
            pc.RpcRemoveAbilityUse();
        }
        else
            pc.Notify(Translator.GetString("OutOfAbilityUsesDoMoreTasks"));
    }

    public override bool CanUseVent(PlayerControl pc, int ventId)
    {
        return !IsThisRole(pc) || pc.Is(CustomRoles.Nimble) || pc.GetClosestVent()?.Id == ventId;
    }
}
