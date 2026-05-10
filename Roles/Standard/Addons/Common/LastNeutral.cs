using System.Linq;
using static EndKnot.Options;

namespace EndKnot.Roles;

internal class LastNeutral : IAddon
{
    public static byte CurrentId = byte.MaxValue;
    public static OptionItem KillCooldown;
    public static OptionItem GiveOpportunist;
    public AddonTypes Type => AddonTypes.Mixed;

    public void SetupCustomOption()
    {
        SetupAdtRoleOptions(20480, CustomRoles.LastNeutral, canSetNum: true, teamSpawnOptions: true);

        KillCooldown = new FloatOptionItem(20490, "LastNeutralKillCooldown", new(0f, 180f, 1f), 15f, TabGroup.Addons)
            .SetParent(CustomRoleSpawnChances[CustomRoles.LastNeutral])
            .SetValueFormat(OptionFormat.Seconds);

        GiveOpportunist = new BooleanOptionItem(20491, "LastNeutralGiveOpportunist", true, TabGroup.Addons)
            .SetParent(CustomRoleSpawnChances[CustomRoles.LastNeutral]);
    }

    public static void Init()
    {
        CurrentId = byte.MaxValue;
    }

    public static void SetKillCooldown()
    {
        if (CurrentId == byte.MaxValue) return;
        if (!Main.AllPlayerKillCooldown.ContainsKey(CurrentId)) return;

        Main.AllPlayerKillCooldown[CurrentId] = KillCooldown.GetFloat();
    }

    private static bool CanBeLastNeutral(PlayerControl pc)
    {
        return pc.IsAlive() && !pc.Is(CustomRoles.LastNeutral) && pc.GetCustomRole().IsNeutral();
    }

    public static void SetSubRole()
    {
        if (CurrentId != byte.MaxValue || !AmongUsClient.Instance.AmHost) return;
        if (Options.CurrentGameMode != CustomGameMode.Standard) return;
        if (!CustomRoles.LastNeutral.IsEnable()) return;

        var aliveNeutrals = Main.EnumerateAlivePlayerControls()
            .Where(pc => pc.GetCustomRole().IsNeutral())
            .ToList();

        if (aliveNeutrals.Count != 1) return;

        PlayerControl pc = aliveNeutrals[0];
        if (!CanBeLastNeutral(pc)) return;

        Main.PlayerStates[pc.PlayerId].SetSubRole(CustomRoles.LastNeutral);
        CurrentId = pc.PlayerId;
        SetKillCooldown();
        pc.SyncSettings();
        Utils.NotifyRoles(SpecifySeer: pc);

        Logger.Info($"LastNeutral assigned to {pc.GetRealName()}", "LastNeutral");
    }
}
