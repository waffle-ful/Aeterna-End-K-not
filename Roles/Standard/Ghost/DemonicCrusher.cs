using System.Linq;
using AmongUs.GameOptions;
using EndKnot.Modules;
using Hazel;

namespace EndKnot.Roles;

// Mad Ghost (Impostor team). Ported from TownOfHost-K's DemonicCrusher.
// OnProtect ignores the protected target: for AbilityTime seconds it jams EVERY information
// device (Admin / Vitals / Cameras / Door Logs) for ALL living players by desyncing a
// per-player "communications down" state to each of them — the same vanilla comms-down
// primitive End K not's DisableDevice already uses (RpcDesyncUpdateSystem Comms 128 / 16),
// so it reaches non-modded clients in a host-only lobby. A LateTask restores comms after the window.
internal class DemonicCrusher : IGhostRole
{
    private static OptionItem CD;
    private static OptionItem AbilityTime;

    // Single global flag (only one DemonicCrusher exists per game). Read by GetSuffix; reset on assign
    // and at the end of each jam so a stale 'true' can never leak into a new game.
    public static bool Active;

    public Team Team => Team.Impostor;
    public RoleTypes RoleTypes => RoleTypes.GuardianAngel;
    public int Cooldown => CD.GetInt();

    public void OnProtect(PlayerControl pc, PlayerControl target)
    {
        // The protected target is intentionally ignored — this is a map-wide effect. One jam at a time.
        if (pc == null || Active || ShipStatus.Instance == null) return;

        Active = true;
        pc.AddAbilityCD(Cooldown);
        SetCommsJam(true);
        Utils.NotifyRoles(SpecifySeer: pc);

        byte id = pc.PlayerId;
        LateTask.New(() =>
        {
            Active = false;
            SetCommsJam(false);

            PlayerControl p = Utils.GetPlayerById(id);
            if (p != null) Utils.NotifyRoles(SpecifySeer: p);
            else Utils.NotifyRoles();
        }, AbilityTime.GetInt(), "DemonicCrusher");
    }

    // Desync a "comms down" state to every alive non-modded player (jam=true) or restore it (jam=false),
    // all in one batched CustomRpcSender. Mirrors DisableDevice's proven RpcDesyncUpdateSystem usage.
    private static void SetCommsJam(bool jam)
    {
        if (ShipStatus.Instance == null) return;

        PlayerControl[] targets = Main.AllAlivePlayerControls.Where(x => x != null && !x.IsModdedClient()).ToArray();
        if (targets.Length == 0) return;

        int mapId = Main.NormalOptions.MapId;
        var sender = CustomRpcSender.Create("DemonicCrusher.SetCommsJam", SendOption.Reliable, log: false);

        foreach (PlayerControl pc in targets)
        {
            if (jam)
                sender.RpcDesyncUpdateSystem(pc, SystemTypes.Comms, 128);
            else
            {
                sender.RpcDesyncUpdateSystem(pc, SystemTypes.Comms, 16);
                if (mapId is 1 or 5) sender.RpcDesyncUpdateSystem(pc, SystemTypes.Comms, 17); // Mira HQ / The Fungle
            }
        }

        sender.SendMessage();
    }

    public void OnAssign(PlayerControl pc)
    {
        Active = false;
    }

    public void SetupCustomOption()
    {
        Options.SetupRoleOptions(650500, TabGroup.OtherRoles, CustomRoles.DemonicCrusher);

        CD = new IntegerOptionItem(650502, "AbilityCooldown", new(0, 180, 1), 25, TabGroup.OtherRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.DemonicCrusher])
            .SetValueFormat(OptionFormat.Seconds);

        AbilityTime = new IntegerOptionItem(650503, "DemonicCrusherAbilityTime", new(1, 60, 1), 10, TabGroup.OtherRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.DemonicCrusher])
            .SetValueFormat(OptionFormat.Seconds);
    }

    // Self-only "?" shown above the crusher's head while the jam is active.
    public static string GetSuffix(PlayerControl seer)
    {
        if (seer == null || !Active || GameStates.IsMeeting) return string.Empty;
        if (!GhostRolesManager.AssignedGhostRoles.TryGetValue(seer.PlayerId, out (CustomRoles Role, IGhostRole Instance) gr) || gr.Role != CustomRoles.DemonicCrusher) return string.Empty;

        return Utils.ColorString(Utils.GetRoleColor(CustomRoles.DemonicCrusher), " ？");
    }
}
