using System.Collections.Generic;
using AmongUs.GameOptions;
using UnityEngine;

namespace EndKnot.Roles;

public class ProgressKiller : RoleBase
{
    private const int Id = 700500;
    private static List<byte> PlayerIdList = [];

    private static OptionItem KillCooldown;
    private static OptionItem ProgressKillerMadseen;
    private static OptionItem ProgressWorkhorseseen;

    private byte ProgressKillerId;

    public override bool IsEnable => PlayerIdList.Count > 0;

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.ImpostorRoles, CustomRoles.ProgressKiller);

        KillCooldown = new FloatOptionItem(Id + 10, "KillCooldown", new(0f, 180f, 0.5f), 30f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.ProgressKiller])
            .SetValueFormat(OptionFormat.Seconds);

        ProgressKillerMadseen = new BooleanOptionItem(Id + 11, "ProgressKillerMadseen", true, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.ProgressKiller]);

        ProgressWorkhorseseen = new BooleanOptionItem(Id + 12, "ProgressWorkhorseseen", true, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.ProgressKiller]);
    }

    public override void Init()
    {
        PlayerIdList = [];
    }

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);
        ProgressKillerId = playerId;
    }

    public override void Remove(byte playerId)
    {
        PlayerIdList.Remove(playerId);
    }

    public override void SetKillCooldown(byte id)
    {
        Main.AllPlayerKillCooldown[id] = KillCooldown.GetFloat();
    }

    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud = false, bool meeting = false)
    {
        if (seer.PlayerId != ProgressKillerId || seer.PlayerId == target.PlayerId) return string.Empty;
        if (!seer.IsAlive()) return string.Empty;

        TaskState taskState = target.GetTaskState();
        if (!taskState.IsTaskFinished) return string.Empty;

        bool isMadmate = target.GetCustomRole().IsMadmate();
        Color roleColor = Utils.GetRoleColor(CustomRoles.ProgressKiller);

        if (ProgressKillerMadseen.GetBool() && isMadmate)
            return Utils.ColorString(roleColor, "☆");
        if (ProgressWorkhorseseen.GetBool() && !isMadmate)
            return Utils.ColorString(roleColor, "〇");

        return string.Empty;
    }
}
