using System;
using System.Collections.Generic;
using UnityEngine;
using static EndKnot.Translator;

namespace EndKnot.Roles;

public class Observer : RoleBase
{
    private const int Id = 7500;
    public static List<byte> PlayerIdList = [];

    private static OptionItem OptionMaxMonitoring;
    private static OptionItem OptionTaskAwakening;
    private static OptionItem OptionAwakeningTaskCount;

    private byte ObserverId;
    private int remaining;
    private byte observerTarget;
    private bool awakened;

    public override bool IsEnable => PlayerIdList.Count > 0;

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.CrewmateRoles, CustomRoles.Observer);

        OptionMaxMonitoring = new IntegerOptionItem(Id + 10, "ObserverMaxMonitoring", new(1, 99, 1), 10, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Observer])
            .SetValueFormat(OptionFormat.Times);

        OptionTaskAwakening = new BooleanOptionItem(Id + 11, "ObserverTaskAwakening", false, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Observer]);

        OptionAwakeningTaskCount = new IntegerOptionItem(Id + 12, "ObserverAwakeningTaskCount", new(1, 99, 1), 5, TabGroup.CrewmateRoles)
            .SetParent(OptionTaskAwakening);
    }

    public override void Init()
    {
        PlayerIdList = [];
    }

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);
        ObserverId = playerId;
        remaining = OptionMaxMonitoring.GetInt();
        observerTarget = byte.MaxValue;
        awakened = !OptionTaskAwakening.GetBool();
    }

    public override void Remove(byte playerId)
    {
        PlayerIdList.Remove(playerId);
    }

    public override bool OnVote(PlayerControl voter, PlayerControl target)
    {
        if (!voter.IsAlive()) return false;
        if (target == null) return false;
        if (!awakened || remaining <= 0) return false;

        observerTarget = target.PlayerId;
        return false;
    }

    public override void OnFixedUpdate(PlayerControl pc)
    {
        if (!AmongUsClient.Instance.AmHost) return;
        if (!GameStates.IsInTask) return;
        if (remaining <= 0 || observerTarget == byte.MaxValue) return;

        PlayerControl target = Utils.GetPlayerById(observerTarget);
        if (target == null || target.IsAlive()) return;

        Utils.GetPlayerById(ObserverId)?.KillFlash();
        Utils.SendMessage(string.Format(GetString("ObserverTargetDied"), observerTarget.ColoredPlayerName()), ObserverId, importance: MessageImportance.High);

        observerTarget = byte.MaxValue;
        remaining = Math.Max(0, remaining - 1);
    }

    public override void OnReportDeadBody()
    {
        observerTarget = byte.MaxValue;
    }

    public override void OnTaskComplete(PlayerControl pc, int completedTaskCount, int totalTaskCount)
    {
        if (awakened) return;
        if (completedTaskCount + 1 >= OptionAwakeningTaskCount.GetInt())
        {
            awakened = true;
            Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc);
        }
    }

    public override string GetProgressText(byte playerId, bool comms)
    {
        return Utils.ColorString(awakened && remaining > 0 ? Utils.GetRoleColor(CustomRoles.Observer) : Color.gray, $"({remaining})");
    }
}
