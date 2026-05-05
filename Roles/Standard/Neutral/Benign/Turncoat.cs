using System.Collections.Generic;
using System.Linq;
using EndKnot.Modules;
using static EndKnot.Options;
using static EndKnot.Translator;

namespace EndKnot.Roles;

public class Turncoat : RoleBase
{
    private const int Id = 703900;
    public static bool On;

    private static OptionItem CanTargetImpostor;
    private static OptionItem CanTargetNeutral;
    private static OptionItem CanTargetMadmate;
    private static OptionItem KnowTargetRole;

    private byte TurncoatId = byte.MaxValue;
    public byte TargetId = byte.MaxValue;
    public bool IsTargetDied;

    public override bool IsEnable => On;

    public override void SetupCustomOption()
    {
        StartSetup(Id)
            .AutoSetupOption(ref CanTargetImpostor, false)
            .AutoSetupOption(ref CanTargetNeutral, false)
            .AutoSetupOption(ref CanTargetMadmate, false)
            .AutoSetupOption(ref KnowTargetRole, true);
    }

    public override void Init()
    {
        On = false;
        TurncoatId = byte.MaxValue;
        TargetId = byte.MaxValue;
        IsTargetDied = false;
    }

    public override void Add(byte playerId)
    {
        On = true;
        TurncoatId = playerId;
        TargetId = byte.MaxValue;
        IsTargetDied = false;

        LateTask.New(() => AssignTarget(playerId), 3f, "Turncoat.AssignTarget");
    }

    public override void Remove(byte playerId)
    {
        if (TurncoatId == playerId) On = false;
    }

    private void AssignTarget(byte playerId)
    {
        if (!AmongUsClient.Instance.AmHost) return;

        List<PlayerControl> candidates = Main.EnumeratePlayerControls()
            .Where(pc =>
            {
                if (pc.PlayerId == playerId) return false;
                if (pc.Is(CustomRoles.GM)) return false;
                if (pc.Is(CustomRoles.Turncoat)) return false;
                CustomRoleTypes roleType = pc.GetCustomRole().GetCustomRoleTypes();
                return roleType switch
                {
                    CustomRoleTypes.Crewmate => true,
                    CustomRoleTypes.Impostor => CanTargetImpostor.GetBool(),
                    CustomRoleTypes.Neutral => CanTargetNeutral.GetBool(),
                    _ => CanTargetMadmate.GetBool()
                };
            })
            .ToList();

        if (candidates.Count == 0)
            candidates = Main.EnumeratePlayerControls().Where(pc => pc.PlayerId != playerId && !pc.Is(CustomRoles.GM)).ToList();

        if (candidates.Count == 0) return;

        PlayerControl chosen = candidates[IRandom.Instance.Next(candidates.Count)];
        TargetId = chosen.PlayerId;

        Logger.Info($"Turncoat {playerId} target: {chosen.GetNameWithRole().RemoveHtmlTags()}", "Turncoat");

        PlayerControl turncoat = Utils.GetPlayerById(playerId);
        if (turncoat != null) Utils.NotifyRoles(SpecifySeer: turncoat, SpecifyTarget: turncoat);
    }

    public override bool CanUseKillButton(PlayerControl pc) => false;

    public override bool CanUseImpostorVentButton(PlayerControl pc) => false;

    public override void OnFixedUpdate(PlayerControl pc)
    {
        if (!AmongUsClient.Instance.AmHost) return;
        if (IsTargetDied || TargetId == byte.MaxValue) return;
        if (!pc.IsAlive()) return;

        PlayerControl target = Utils.GetPlayerById(TargetId);
        if (target == null || target.Data.Disconnected)
        {
            // Target disconnected — change to Opportunist
            pc.RpcSetCustomRole(CustomRoles.Opportunist);
            return;
        }

        if (!target.IsAlive())
        {
            IsTargetDied = true;
            Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc);
        }
    }

    public override bool KnowRole(PlayerControl seer, PlayerControl target)
    {
        if (base.KnowRole(seer, target)) return true;
        if (!KnowTargetRole.GetBool()) return false;
        return seer.PlayerId == TurncoatId && target.PlayerId == TargetId;
    }

    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud = false, bool meeting = false)
    {
        if (seer.PlayerId != TurncoatId) return string.Empty;
        if (seer.PlayerId == target.PlayerId) return string.Empty;
        if (target.PlayerId != TargetId) return string.Empty;
        if (meeting) return string.Empty;
        return Utils.ColorString(Utils.GetRoleColor(CustomRoles.Turncoat), "★");
    }

    public override string GetProgressText(byte playerId, bool comms)
    {
        if (playerId != TurncoatId) return string.Empty;
        if (TargetId == byte.MaxValue) return string.Empty;
        PlayerControl target = Utils.GetPlayerById(TargetId);
        if (target == null) return string.Empty;
        string targetName = target.GetRealName();
        if (IsTargetDied && Main.PlayerStates.TryGetValue(TargetId, out PlayerState ps))
        {
            CustomRoles role = ps.MainRole;
            targetName += Utils.ColorString(Utils.GetRoleColor(role), $"({GetString($"{role}")})");
        }
        return Utils.ColorString(Utils.GetRoleColor(CustomRoles.Turncoat), $"[{targetName}]");
    }
}
