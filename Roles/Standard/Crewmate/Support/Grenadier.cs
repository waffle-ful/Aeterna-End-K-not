using System.Collections.Generic;
using System.Linq;
using AmongUs.GameOptions;
using EndKnot.Modules;
using EndKnot.Modules.Extensions;
using Hazel;
using static EndKnot.Options;

namespace EndKnot.Roles;

internal class Grenadier : RoleBase
{
    public static HashSet<byte> GrenadierBlinding = [];
    public static HashSet<byte> MadGrenadierBlinding = [];

    public static bool On;
    private static readonly HashSet<byte> SyncedActive = [];
    // 発動中のタイマー。満了まで会議を見ないので、会議開始と次の発動で止めないと次の発動分を消してしまう
    private static readonly Dictionary<byte, CountdownTimer> ActiveTimers = [];
    private byte GrenadierId;
    public override bool IsEnable => On;

    public override void SetupCustomOption()
    {
        SetupRoleOptions(6800, TabGroup.CrewmateRoles, CustomRoles.Grenadier);

        GrenadierSkillCooldown = new FloatOptionItem(6810, "GrenadierSkillCooldown", new(0f, 180f, 1f), 25f, TabGroup.CrewmateRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Grenadier])
            .SetValueFormat(OptionFormat.Seconds);

        GrenadierSkillDuration = new FloatOptionItem(6811, "GrenadierSkillDuration", new(0f, 180f, 1f), 10f, TabGroup.CrewmateRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Grenadier])
            .SetValueFormat(OptionFormat.Seconds);

        GrenadierCauseVision = new FloatOptionItem(6812, "GrenadierCauseVision", new(0f, 5f, 0.05f), 0.3f, TabGroup.CrewmateRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Grenadier])
            .SetValueFormat(OptionFormat.Multiplier);

        GrenadierCanAffectNeutral = new BooleanOptionItem(6813, "GrenadierCanAffectNeutral", false, TabGroup.CrewmateRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Grenadier]);

        GrenadierSkillMaxOfUsage = new IntegerOptionItem(6814, "GrenadierSkillMaxOfUsage", new(0, 30, 1), 2, TabGroup.CrewmateRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Grenadier])
            .SetValueFormat(OptionFormat.Times);

        GrenadierAbilityUseGainWithEachTaskCompleted = new FloatOptionItem(6815, "AbilityUseGainWithEachTaskCompleted", new(0f, 5f, 0.05f), 0.5f, TabGroup.CrewmateRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Grenadier])
            .SetValueFormat(OptionFormat.Times);

        GrenadierAbilityChargesWhenFinishedTasks = new FloatOptionItem(6816, "AbilityChargesWhenFinishedTasks", new(0f, 5f, 0.05f), 0.2f, TabGroup.CrewmateRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Grenadier])
            .SetValueFormat(OptionFormat.Times);
    }

    public override void Add(byte playerId)
    {
        On = true;
        GrenadierId = playerId;
        playerId.SetAbilityUseLimit(GrenadierSkillMaxOfUsage.GetFloat());
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

        AURoleOptions.EngineerCooldown = GrenadierSkillCooldown.GetFloat();
        AURoleOptions.EngineerInVentMaxTime = 1f;
    }

    // 使用中の色分けは各クライアントが自分で判定するので、使用中かどうかをモッド客へ送る
    private static void SendBlindingSync(byte id, bool active)
    {
        if (active) SyncedActive.Add(id);
        else SyncedActive.Remove(id);

        Utils.SendRPC(CustomRPC.SyncRoleData, id, active);
    }

    // 会議開始でホストは使用中の集合を空にするので、客側の保持も同時に解除させる
    public override void OnReportDeadBody()
    {
        if (ActiveTimers.Remove(GrenadierId, out CountdownTimer timer)) timer.Dispose();
        if (SyncedActive.Contains(GrenadierId)) SendBlindingSync(GrenadierId, false);
    }

    public void ReceiveRPC(MessageReader reader)
    {
        if (reader.ReadBoolean()) GrenadierBlinding.Add(GrenadierId);
        else GrenadierBlinding.Remove(GrenadierId);
    }

    public override string GetProgressText(byte playerId, bool comms)
    {
        var progressText = new StringBuilder();

        progressText.Append(Utils.GetAbilityUseLimitDisplay(playerId, GrenadierBlinding.Contains(playerId)));
        progressText.Append(Utils.GetTaskCount(playerId, comms));

        return progressText.ToString();
    }

    public override void SetButtonTexts(HudManager hud, byte id)
    {
        if (UsePets.GetBool())
            hud.PetButton.buttonLabelText.text = Translator.GetString("GrenadierVentButtonText");
        else
            hud.AbilityButton.buttonLabelText.text = Translator.GetString("GrenadierVentButtonText");
    }

    public override void OnPet(PlayerControl pc)
    {
        BlindPlayers(pc);
    }

    public override void OnEnterVent(PlayerControl pc, Vent vent)
    {
        if (UsePets.GetBool()) return;
        BlindPlayers(pc);
    }

    private static void BlindPlayers(PlayerControl pc)
    {
        if (GrenadierBlinding.Contains(pc.PlayerId) || MadGrenadierBlinding.Contains(pc.PlayerId)) return;

        if (pc.GetAbilityUseLimit() >= 1)
        {
            if (pc.Is(CustomRoles.Madmate))
            {
                MadGrenadierBlinding.Add(pc.PlayerId);
                if (ActiveTimers.Remove(pc.PlayerId, out CountdownTimer old)) old.Dispose();
                ActiveTimers[pc.PlayerId] = new CountdownTimer(GrenadierSkillDuration.GetInt(), () =>
                {
                    MadGrenadierBlinding.Remove(pc.PlayerId);
                    pc.RpcResetAbilityCooldown();
                    pc.Notify(string.Format(Translator.GetString("GrenadierSkillStop"), (int)pc.GetAbilityUseLimit()));
                    Utils.MarkEveryoneDirtySettingsV3();
                }, onCanceled: () => MadGrenadierBlinding.Remove(pc.PlayerId));
                Main.EnumeratePlayerControls().Where(x => x.IsModdedClient()).Where(x => !x.GetCustomRole().IsImpostorTeam() && !x.Is(CustomRoles.Madmate)).Do(x => x.RPCPlayCustomSound("FlashBang"));
            }
            else
            {
                GrenadierBlinding.Add(pc.PlayerId);
                SendBlindingSync(pc.PlayerId, true);
                if (ActiveTimers.Remove(pc.PlayerId, out CountdownTimer old)) old.Dispose();
                ActiveTimers[pc.PlayerId] = new CountdownTimer(GrenadierSkillDuration.GetInt(), () =>
                {
                    GrenadierBlinding.Remove(pc.PlayerId);
                    SendBlindingSync(pc.PlayerId, false);
                    pc.RpcResetAbilityCooldown();
                    pc.Notify(string.Format(Translator.GetString("GrenadierSkillStop"), (int)pc.GetAbilityUseLimit()));
                    Utils.MarkEveryoneDirtySettingsV3();
                }, onCanceled: () =>
                {
                    GrenadierBlinding.Remove(pc.PlayerId);
                    if (GameStates.InGame) SendBlindingSync(pc.PlayerId, false);
                });
                Main.EnumeratePlayerControls().Where(x => x.IsModdedClient()).Where(x => x.IsImpostor() || (x.GetCustomRole().IsNeutral() && GrenadierCanAffectNeutral.GetBool())).Do(x => x.RPCPlayCustomSound("FlashBang"));
            }

            pc.RPCPlayCustomSound("FlashBang");
            pc.Notify(Translator.GetString("GrenadierSkillInUse"), GrenadierSkillDuration.GetFloat());
            pc.RpcRemoveAbilityUse();
            Utils.MarkEveryoneDirtySettingsV3();
        }
        else
            pc.Notify(Translator.GetString("OutOfAbilityUsesDoMoreTasks"));
    }

    public override bool CanUseVent(PlayerControl pc, int ventId)
    {
        return !IsThisRole(pc) || pc.Is(CustomRoles.Nimble) || pc.GetClosestVent()?.Id == ventId;
    }
}