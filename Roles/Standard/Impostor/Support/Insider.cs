using System.Collections.Generic;

namespace EndKnot.Roles;

public class Insider : RoleBase
{
    private const int Id = 699100;
    public static List<byte> PlayerIdList = [];

    private static OptionItem KillCooldown;
    private static OptionItem CanSeeAllGhostsRoles;
    private static OptionItem CanSeeImpostorAbilities;
    private static OptionItem CanSeeMadmates;
    private static OptionItem KillCountToSeeMadmates;

    public override bool IsEnable => PlayerIdList.Count > 0;

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.ImpostorRoles, CustomRoles.Insider);

        KillCooldown = new FloatOptionItem(Id + 10, "KillCooldown", new(0f, 180f, 0.5f), 25f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Insider])
            .SetValueFormat(OptionFormat.Seconds);

        CanSeeAllGhostsRoles = new BooleanOptionItem(Id + 11, "InsiderCanSeeAllGhostsRoles", false, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Insider]);

        CanSeeImpostorAbilities = new BooleanOptionItem(Id + 12, "InsiderCanSeeImpostorAbilities", true, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Insider]);

        CanSeeMadmates = new BooleanOptionItem(Id + 13, "InsiderCanSeeMadmates", false, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Insider]);

        KillCountToSeeMadmates = new IntegerOptionItem(Id + 14, "InsiderKillCountToSeeMadmates", new(0, 15, 1), 2, TabGroup.ImpostorRoles)
            .SetParent(CanSeeMadmates)
            .SetValueFormat(OptionFormat.Times);
    }

    public override void Init()
    {
        PlayerIdList = [];
    }

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);
    }

    public override void Remove(byte playerId)
    {
        PlayerIdList.Remove(playerId);
    }

    public override void SetKillCooldown(byte id)
    {
        Main.AllPlayerKillCooldown[id] = KillCooldown.GetFloat();
    }

    public static bool ShouldSeeImpostorAbility(PlayerControl seer)
    {
        if (!PlayerIdList.Contains(seer.PlayerId)) return false;
        if (!seer.IsAlive()) return false;
        return CanSeeImpostorAbilities.GetBool();
    }

    public override bool KnowRole(PlayerControl seer, PlayerControl target)
    {
        if (base.KnowRole(seer, target)) return true;
        if (!PlayerIdList.Contains(seer.PlayerId)) return false;
        if (seer.PlayerId == target.PlayerId) return false;
        if (target.Is(CustomRoles.GM)) return false;
        if (!seer.IsAlive() && Options.GhostCanSeeOtherRoles.GetBool()) return false;

        if (!target.IsAlive())
        {
            return CanSeeAllGhostsRoles.GetBool()
                || target.GetRealKiller()?.PlayerId == seer.PlayerId;
        }

        if (target.Is(Team.Impostor) && CanSeeImpostorAbilities.GetBool()) return true;

        if (target.IsMadmate() && CanSeeMadmates.GetBool()
            && Main.PlayerStates[seer.PlayerId].GetKillCount() >= KillCountToSeeMadmates.GetInt()) return true;

        return false;
    }

    public override string GetProgressText(byte playerId, bool comms)
    {
        if (!CanSeeMadmates.GetBool()) return string.Empty;

        int killCount = Main.PlayerStates[playerId].GetKillCount();
        int threshold = KillCountToSeeMadmates.GetInt();

        if (killCount >= threshold) return Utils.ColorString(Palette.ImpostorRed.ShadeColor(0.5f), "★");
        return Utils.ColorString(Palette.ImpostorRed.ShadeColor(0.5f), $"({killCount}/{threshold})");
    }
}
