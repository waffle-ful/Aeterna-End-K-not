using System.Linq;
using AmongUs.GameOptions;
using EndKnot.Modules;
using UnityEngine;
using static EndKnot.Options;

namespace EndKnot.Roles;

// コンビネーション役職の主役職。desync Impostor 基底の中立キラーで、キルボタンは実際には殺さず、
// 相方 Altair と「逢い引き」して両者を強化する。オプションは相方の分も含めてここに全部ぶら下げる
// (Modules/CombinationRoles.cs の Pairs 登録により相方は自分の出現率オプションを持たない)。
public class Vega : RoleBase
{
    public static bool On;
    public override bool IsEnable => On;

    public static byte VegaId = byte.MaxValue;
    public static byte AltairId = byte.MaxValue;

    private static bool Rendezvoused;
    private static int RendezvousCount;
    private static float CurrentAltairKillCooldown;
    public static bool CanSeeKiller;

    public const string TeamColorHex = "#f0e7a8";
    public static readonly Color32 TeamColorValue = new(0xf0, 0xe7, 0xa8, 0xff);
    public static Color TeamColor => TeamColorValue;

    public static OptionItem KillCooldown;
    public static OptionItem RendezvousCooldown;
    public static OptionItem ImpostorVision;
    public static OptionItem VegaCanUseVent;
    public static OptionItem AltairCanUseVent;
    public static OptionItem BuffThreshold;
    public static OptionItem KillCooldownThreshold;
    public static OptionItem KillCooldownAmount;
    public static OptionItem MinimumKillCooldown;
    public static OptionItem RevealKillableFactions;
    public static OptionItem RKFThreshold;
    public static OptionItem FactionBasedStarColor;
    public static OptionItem AddWin;

    public override void SetupCustomOption()
    {
        StartSetup(707600)
            .AutoSetupOption(ref KillCooldown, 30f, new FloatValueRule(0f, 180f, 0.5f), OptionFormat.Seconds)
            .AutoSetupOption(ref RendezvousCooldown, 15f, new FloatValueRule(0f, 180f, 0.5f), OptionFormat.Seconds)
            .AutoSetupOption(ref ImpostorVision, true)
            .AutoSetupOption(ref VegaCanUseVent, true)
            .AutoSetupOption(ref AltairCanUseVent, true)
            .AutoSetupOption(ref BuffThreshold, 3, new IntegerValueRule(1, 99, 1), OptionFormat.Times)
            .AutoSetupOption(ref KillCooldownThreshold, 6, new IntegerValueRule(1, 99, 1), OptionFormat.Times)
            .AutoSetupOption(ref KillCooldownAmount, 5f, new FloatValueRule(1f, 180f, 0.5f), OptionFormat.Seconds)
            .AutoSetupOption(ref MinimumKillCooldown, 5f, new FloatValueRule(0.5f, 180f, 0.5f), OptionFormat.Seconds)
            .AutoSetupOption(ref RevealKillableFactions, false)
            .AutoSetupOption(ref RKFThreshold, 8, new IntegerValueRule(1, 99, 1), OptionFormat.Times, overrideParent: RevealKillableFactions)
            .AutoSetupOption(ref FactionBasedStarColor, true, overrideParent: RevealKillableFactions)
            .AutoSetupOption(ref AddWin, true);

        SoloWinOption.Create(707615, TabGroup.Combinations, CustomRoles.Vega, defo: 0);
    }

    public override void Init()
    {
        On = false;
        VegaId = byte.MaxValue;
        AltairId = byte.MaxValue;
        Rendezvoused = false;
        RendezvousCount = 0;
        CanSeeKiller = false;
        CurrentAltairKillCooldown = KillCooldown.GetFloat();
    }

    public override void Add(byte playerId)
    {
        On = true;
        VegaId = playerId;
        CurrentAltairKillCooldown = KillCooldown.GetFloat();
    }

    public override void Remove(byte playerId)
    {
        On = false;
    }

    public override void ApplyGameOptions(IGameOptions opt, byte playerId)
    {
        opt.SetVision(ImpostorVision.GetBool());
    }

    public override void SetKillCooldown(byte id)
    {
        Main.AllPlayerKillCooldown[id] = RendezvousCooldown.GetFloat();
    }

    public override bool CanUseKillButton(PlayerControl pc)
    {
        return base.CanUseKillButton(pc) && !Rendezvoused;
    }

    public override bool CanUseSabotage(PlayerControl pc)
    {
        return false;
    }

    public override bool CanUseImpostorVentButton(PlayerControl pc)
    {
        return pc.IsAlive() && VegaCanUseVent.GetBool();
    }

    public override bool OnCheckMurder(PlayerControl killer, PlayerControl target)
    {
        // Already rendezvoused this life-segment: CanUseKillButton() is already false, so the
        // button is locked and there is no cooldown left to reset (calling SetKillCooldown here
        // would silently no-op anyway, since it early-returns when CanUseKillButton() is false).
        if (Rendezvoused) return false;

        if (target.PlayerId != AltairId)
        {
            // A miss just goes on the normal (short) rendezvous cooldown so Vega can try again.
            killer.SetKillCooldown(RendezvousCooldown.GetFloat());
            return false;
        }

        // Lock the kill button for the rest of this life-segment *before* flipping Rendezvoused,
        // since CanUseKillButton() (and therefore SetKillCooldown()) would otherwise already see
        // it as locked and skip applying the cooldown.
        killer.SetKillCooldown(999f, target);
        Rendezvous(killer, target);
        return false;
    }

    private static void Rendezvous(PlayerControl vega, PlayerControl altair)
    {
        RendezvousCount++;
        Rendezvoused = true;

        Logger.Info($"{vega.GetNameWithRole().RemoveHtmlTags()} rendezvoused with {altair.GetNameWithRole().RemoveHtmlTags()} ({RendezvousCount})", "Vega");

        if (RendezvousCount >= BuffThreshold.GetInt()) GiveBuff(altair);

        if (RendezvousCount >= KillCooldownThreshold.GetInt())
            CurrentAltairKillCooldown = Mathf.Max(CurrentAltairKillCooldown - KillCooldownAmount.GetFloat(), MinimumKillCooldown.GetFloat());

        if (RendezvousCount >= RKFThreshold.GetInt() && RevealKillableFactions.GetBool() && !CanSeeKiller)
        {
            CanSeeKiller = true;
            Utils.NotifyRoles(SpecifySeer: altair);
        }
    }

    private static void GiveBuff(PlayerControl altair)
    {
        CustomRoles addon = GroupedAddons[AddonTypes.Helpful]
            .Where(x => !altair.Is(x) && !x.IsNotAssignableMidGame() && !x.IsConverted() && CustomRolesHelper.CheckAddonConflict(x, altair) &&
                        x is not (CustomRoles.Nimble or CustomRoles.Bloodlust or CustomRoles.Physicist or CustomRoles.Finder or CustomRoles.Noisy or CustomRoles.Examiner or CustomRoles.Venom))
            .RandomElement();

        if (addon == default) return;

        altair.RpcSetCustomRole(addon);
        Logger.Info($"Rendezvous buff: {altair.GetNameWithRole().RemoveHtmlTags()} => {addon}", "Vega");
        LateTask.New(() => Utils.NotifyRoles(SpecifySeer: VegaId.GetPlayer()), 0.15f, log: false);
    }

    public override void AfterMeetingTasks()
    {
        Rendezvoused = false;

        if (AltairId == byte.MaxValue) return;

        PlayerControl vega = VegaId.GetPlayer();
        if (!vega || !vega.IsAlive()) return;

        PlayerControl altair = AltairId.GetPlayer();
        bool altairGone = !altair || altair.Data == null || altair.Data.Disconnected || !altair.IsAlive() || altair.GetCustomRole() != CustomRoles.Altair;

        if (!altairGone) return;

        // AfterMeetingTasks() runs from ExileControllerWrapUpPatch, while ExileController.Instance
        // is still set — pc.Suicide() hard-guards on that and would silently no-op here. Apply the
        // death directly (silent-exile pattern): RpcExileV2 first so every client actually shows
        // the ghost transition, then the same host-side bookkeeping ExilePatch's
        // AfterMeetingDeathPlayers drain does.
        PlayerState state = Main.PlayerStates[vega.PlayerId];
        vega.SetRealKiller(vega);
        state.deathReason = PlayerState.DeathReason.FollowingSuicide;
        vega.RpcExileV2();
        vega.Data.IsDead = true;
        state.SetDead();
        Utils.AfterPlayerDeathTasks(vega);
    }

    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud = false, bool meeting = false)
    {
        if (VegaId == byte.MaxValue) return string.Empty;

        // Mutual ☆ between Vega and Altair — they always know each other.
        if ((seer.PlayerId == VegaId && target.PlayerId == AltairId) || (seer.PlayerId == AltairId && target.PlayerId == VegaId))
            return Utils.ColorString(TeamColor, "☆");

        // ★ marks killable factions for Altair, once unlocked by enough rendezvous.
        if (seer.PlayerId != AltairId || target.PlayerId == AltairId || target.PlayerId == VegaId || meeting || !CanSeeKiller || !RevealKillableFactions.GetBool()) return string.Empty;
        if (FirstTurnMeeting.GetBool() && MeetingStates.FirstMeeting) return string.Empty;

        bool killable = target.IsImpostor() || target.GetCustomRole().IsNK() || (target.IsCrewmate() && target.CanUseKillButton());
        if (!killable) return string.Empty;

        Color color = FactionBasedStarColor.GetBool() ? (target.IsImpostor() ? Utils.GetRoleColor(CustomRoles.Impostor) : Utils.GetRoleColor(target.GetCustomRole())) : Palette.DisabledGrey;
        return Utils.ColorString(color, "★");
    }

    public override bool KnowRole(PlayerControl seer, PlayerControl target)
    {
        if ((seer.PlayerId == VegaId && target.PlayerId == AltairId) || (seer.PlayerId == AltairId && target.PlayerId == VegaId)) return true;

        return base.KnowRole(seer, target);
    }

    public static float GetCurrentAltairKillCooldown()
    {
        return CurrentAltairKillCooldown;
    }
}
