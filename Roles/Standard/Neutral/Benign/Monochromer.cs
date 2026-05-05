using AmongUs.GameOptions;
using static EndKnot.Options;
using static EndKnot.Translator;

namespace EndKnot.Roles;

public class Monochromer : RoleBase
{
    private const int Id = 703800;
    public static bool On;

    private static OptionItem HasImpostorVision;
    private static OptionItem CanSeeKillers;
    private static OptionItem ShowKillerRoleColor;

    private byte MonochromerId = byte.MaxValue;

    public override bool IsEnable => On;

    public override void SetupCustomOption()
    {
        StartSetup(Id)
            .AutoSetupOption(ref HasImpostorVision, false)
            .AutoSetupOption(ref CanSeeKillers, true)
            .AutoSetupOption(ref ShowKillerRoleColor, false, overrideParent: CanSeeKillers);
    }

    public override void Init()
    {
        On = false;
        MonochromerId = byte.MaxValue;
    }

    public override void Add(byte playerId)
    {
        On = true;
        MonochromerId = playerId;
    }

    public override void Remove(byte playerId)
    {
        if (MonochromerId == playerId) On = false;
    }

    public override void ApplyGameOptions(IGameOptions opt, byte id)
    {
        opt.SetVision(HasImpostorVision.GetBool());
    }

    public override bool CanUseKillButton(PlayerControl pc) => false;

    public override bool CanUseImpostorVentButton(PlayerControl pc) => false;

    private static bool IsKiller(PlayerControl pc)
    {
        return pc.Is(CustomRoleTypes.Impostor) || pc.IsNeutralKiller();
    }

    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud = false, bool meeting = false)
    {
        if (!CanSeeKillers.GetBool()) return string.Empty;
        if (meeting) return string.Empty;
        if (seer.PlayerId != MonochromerId) return string.Empty;
        if (!seer.IsAlive()) return string.Empty;
        if (seer.PlayerId == target.PlayerId) return string.Empty;
        if (!IsKiller(target)) return string.Empty;

        var color = ShowKillerRoleColor.GetBool()
            ? Utils.GetRoleColor(target.GetCustomRole())
            : UnityEngine.Color.gray;
        return Utils.ColorString(color, "★");
    }

    public override void OnReportDeadBody() { }
}
