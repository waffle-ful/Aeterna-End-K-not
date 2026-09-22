using System.Collections.Generic;
using AmongUs.GameOptions;
using static EndKnot.Options;
using static EndKnot.Translator;

namespace EndKnot.Roles;

public class Monochromer : RoleBase
{
    private const int Id = 703800;
    public static bool On;
    public static List<Monochromer> Instances = [];

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
        Instances = [];
        MonochromerId = byte.MaxValue;
    }

    public override void Add(byte playerId)
    {
        On = true;
        Instances.Add(this);
        MonochromerId = playerId;
    }

    public override void Remove(byte playerId)
    {
        Instances.RemoveAll(x => x.MonochromerId == playerId);
        if (Instances.Count == 0) On = false;
    }

    public override void ApplyGameOptions(IGameOptions opt, byte id)
    {
        opt.SetVision(HasImpostorVision.GetBool());
    }

    public override bool CanUseKillButton(PlayerControl pc) => false;

    public override bool CanUseImpostorVentButton(PlayerControl pc) => false;

    private static bool IsKiller(PlayerControl pc)
    {
        return pc.Is(CustomRoleTypes.Impostor) || pc.IsNeutralKiller() || pc.Is(CustomRoles.Sheriff) || pc.Is(CustomRoles.WolfBoy);
    }

    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud = false, bool meeting = false)
    {
        if (!CanSeeKillers.GetBool()) return string.Empty;
        if (meeting) return string.Empty;
        // 初回会議を強制する設定のときは、その会議が終わるまで★を見せない (原典と同じ扱い)
        if (Options.FirstTurnMeeting.GetBool() && MeetingStates.FirstMeeting) return string.Empty;
        if (seer.PlayerId != MonochromerId) return string.Empty;
        if (!seer.IsAlive()) return string.Empty;
        if (seer.PlayerId == target.PlayerId) return string.Empty;
        if (!IsKiller(target)) return string.Empty;

        // WolfBoy は正体を偽装する役職なので、★色でも Impostor 色に化けさせる。
        var color = ShowKillerRoleColor.GetBool()
            ? Utils.GetRoleColor(target.Is(CustomRoles.WolfBoy) ? CustomRoles.Impostor : target.GetCustomRole())
            : UnityEngine.Color.gray;
        return Utils.ColorString(color, "★");
    }

    public override void OnReportDeadBody() { }
}
