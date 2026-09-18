using AmongUs.GameOptions;
using UnityEngine;
using static EndKnot.Options;

namespace EndKnot.Roles;

public class InSender : RoleBase
{
    private const int Id = 702800;

    public static bool On;
    public override bool IsEnable => On;

    private static OptionItem CanUseActiveCommsOpt;
    private static OptionItem ReportDelayOpt;
    private static OptionItem MaxDelayOpt;
    private static OptionItem TaskAwakeningOpt;
    private static OptionItem AwakeningTaskcountOpt;

    private bool Awakened;

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.CrewmateRoles, CustomRoles.InSender);

        CanUseActiveCommsOpt = new BooleanOptionItem(Id + 9, "InSenderCanUseActiveComms", true, TabGroup.CrewmateRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.InSender]);

        ReportDelayOpt = new FloatOptionItem(Id + 12, "InSenderReportDelay", new(0f, 180f, 0.5f), 3f, TabGroup.CrewmateRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.InSender])
            .SetValueFormat(OptionFormat.Seconds);

        MaxDelayOpt = new FloatOptionItem(Id + 13, "InSenderMaxDelay", new(0f, 180f, 0.5f), 3f, TabGroup.CrewmateRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.InSender])
            .SetValueFormat(OptionFormat.Seconds);

        TaskAwakeningOpt = new BooleanOptionItem(Id + 10, "InSenderTaskAwakening", false, TabGroup.CrewmateRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.InSender]);

        AwakeningTaskcountOpt = new IntegerOptionItem(Id + 14, "InSenderAwakeningTaskcount", new(1, 255, 1), 5, TabGroup.CrewmateRoles)
            .SetParent(TaskAwakeningOpt)
            .SetValueFormat(OptionFormat.Times);
    }

    public override void Init()
    {
        On = false;
    }

    public override void Add(byte playerId)
    {
        On = true;
        Awakened = !TaskAwakeningOpt.GetBool();
    }

    // OnCheckMurderAsTarget はキル打診 (check: true の下見) でも呼ばれるため、実際に死んだ後の
    // post-murder ディスパッチ (Patches/PlayerControlPatch.cs の MurderPlayerPatch.Postfix) からのみ発火させる。
    public static void OnAnyoneMurder(PlayerControl killer, PlayerControl target)
    {
        if (!On || killer == null || target == null || killer.PlayerId == target.PlayerId) return;
        if (Main.PlayerStates.TryGetValue(target.PlayerId, out PlayerState state) && state.Role is InSender inSender)
            inSender.SelfReport(killer, target);
    }

    private void SelfReport(PlayerControl killer, PlayerControl target)
    {
        if (!Awakened) return;
        if (!CanUseActiveCommsOpt.GetBool() && Utils.IsActive(SystemTypes.Comms)) return;

        float extra = 0f;
        if (MaxDelayOpt.GetFloat() > 0)
            extra = IRandom.Instance.Next(0, (int)(MaxDelayOpt.GetFloat() * 10)) * 0.1f;

        float delay = Mathf.Max(0.15f, ReportDelayOpt.GetFloat() + extra);
        LateTask.New(() =>
        {
            if (!GameStates.IsInTask) return;
            PlayerControl reporter = target.Is(CustomRoles.Madmate) ? FindFarthestCrew(killer, target) ?? target : target;
            reporter.NoCheckStartMeeting(target.Data);
        }, delay, "InSender Self Report");
    }

    // マッドメイトのインセンダーは、キラーから一番遠い生存クルー (インポスター陣営・マッドメイト以外) を通報者に仕立てる。
    private static PlayerControl FindFarthestCrew(PlayerControl killer, PlayerControl target)
    {
        if (killer == null) return null;

        Vector2 killerPos = killer.Pos();
        PlayerControl farthest = null;
        float maxDist = -1f;
        foreach (PlayerControl pc in Main.EnumerateAlivePlayerControls())
        {
            if (pc.PlayerId == killer.PlayerId || pc.PlayerId == target.PlayerId) continue;
            if (!pc.IsCrewmate() || pc.IsMadmate()) continue;

            float dist = Vector2.Distance(killerPos, pc.Pos());
            if (dist <= maxDist) continue;
            maxDist = dist;
            farthest = pc;
        }

        return farthest;
    }

    public override void OnTaskComplete(PlayerControl pc, int completedTaskCount, int totalTaskCount)
    {
        if (TaskAwakeningOpt.GetBool() && !Awakened && completedTaskCount + 1 >= AwakeningTaskcountOpt.GetInt())
        {
            Awakened = true;
            Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc);
        }
    }
}
