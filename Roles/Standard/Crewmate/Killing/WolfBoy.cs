using System.Collections.Generic;
using AmongUs.GameOptions;
using UnityEngine;
using static EndKnot.Translator;

namespace EndKnot.Roles;

public class WolfBoy : RoleBase
{
    private const int Id = 703300;
    public static List<byte> PlayerIdList = [];

    private static OptionItem OptionKillCooldown;
    private static OptionItem OptionShotLimit;
    private static OptionItem OptionCanKillAllAlive;
    private static OptionItem OptionImpostorVision;
    public static OptionItem OptionWinKillCount;
    private static OptionItem OptionCountCrew;
    private static OptionItem OptionCountImpostor;
    private static OptionItem OptionCountMadmate;
    private static OptionItem OptionCountNeutral;

    private byte WolfBoyId;
    private int shotLimit;
    private int killCount;

    public override bool IsEnable => PlayerIdList.Count > 0;

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.CrewmateRoles, CustomRoles.WolfBoy);

        OptionKillCooldown = new FloatOptionItem(Id + 10, "KillCooldown", new(0f, 180f, 0.5f), 30f, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.WolfBoy])
            .SetValueFormat(OptionFormat.Seconds);

        OptionShotLimit = new IntegerOptionItem(Id + 11, "WolfBoyShotLimit", new(1, 15, 1), 15, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.WolfBoy])
            .SetValueFormat(OptionFormat.Times);

        OptionCanKillAllAlive = new BooleanOptionItem(Id + 12, "WolfBoyCanKillAllAlive", true, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.WolfBoy]);

        OptionImpostorVision = new BooleanOptionItem(Id + 13, "ImpostorVision", true, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.WolfBoy]);

        OptionWinKillCount = new IntegerOptionItem(Id + 14, "WolfBoyWinKillCount", new(0, 14, 1), 2, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.WolfBoy])
            .SetValueFormat(OptionFormat.Players);

        OptionCountCrew = new BooleanOptionItem(Id + 15, "WolfBoyCountCrew", true, TabGroup.CrewmateRoles)
            .SetParent(OptionWinKillCount);

        OptionCountImpostor = new BooleanOptionItem(Id + 16, "WolfBoyCountImpostor", false, TabGroup.CrewmateRoles)
            .SetParent(OptionWinKillCount);

        OptionCountMadmate = new BooleanOptionItem(Id + 17, "WolfBoyCountMadmate", false, TabGroup.CrewmateRoles)
            .SetParent(OptionWinKillCount);

        OptionCountNeutral = new BooleanOptionItem(Id + 18, "WolfBoyCountNeutral", false, TabGroup.CrewmateRoles)
            .SetParent(OptionWinKillCount);
    }

    public override void Init()
    {
        PlayerIdList = [];
    }

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);
        WolfBoyId = playerId;
        shotLimit = OptionShotLimit.GetInt();
        killCount = 0;
    }

    public override void Remove(byte playerId)
    {
        PlayerIdList.Remove(playerId);
    }

    public override void SetKillCooldown(byte id)
    {
        Main.AllPlayerKillCooldown[id] = OptionKillCooldown.GetFloat();
    }

    public override bool CanUseKillButton(PlayerControl pc)
    {
        return !Main.PlayerStates[pc.PlayerId].IsDead
            && (OptionCanKillAllAlive.GetBool() || GameStates.AlreadyDied)
            && shotLimit > 0;
    }

    public override bool CanUseImpostorVentButton(PlayerControl pc)
    {
        return false;
    }

    public override void ApplyGameOptions(IGameOptions opt, byte playerId)
    {
        opt.SetVision(OptionImpostorVision.GetBool());
    }

    public override bool OnCheckMurder(PlayerControl killer, PlayerControl target)
    {
        if (shotLimit <= 0) return false;

        shotLimit--;
        SetKillCooldown(killer.PlayerId);

        // Count kill toward win condition
        if (OptionWinKillCount.GetInt() > 0)
        {
            bool count = target.GetCustomRoleTypes() switch
            {
                CustomRoleTypes.Crewmate => OptionCountCrew.GetBool(),
                CustomRoleTypes.Impostor => OptionCountImpostor.GetBool(),
                CustomRoleTypes.Neutral => OptionCountNeutral.GetBool(),
                _ => false
            };

            if (!count && target.IsMadmate()) count = OptionCountMadmate.GetBool();
            if (count) killCount++;
        }

        return true;
    }

    public int GetKillCount() => killCount;

    public override string GetProgressText(byte playerId, bool comms)
    {
        if (playerId != WolfBoyId) return string.Empty;
        Color color = shotLimit > 0 ? Color.yellow : Color.gray;
        return Utils.ColorString(color, $"({shotLimit})");
    }

    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud = false, bool meeting = false)
    {
        if (meeting || seer == null || target == null) return string.Empty;
        if (seer.PlayerId != WolfBoyId || seer.PlayerId != target.PlayerId) return string.Empty;
        int required = OptionWinKillCount.GetInt();
        if (required == 0) return string.Empty;
        return Utils.ColorString(killCount >= required ? Color.green : Color.yellow, $"({killCount}/{required})");
    }
}
