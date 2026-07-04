using System.Collections.Generic;
using AmongUs.GameOptions;
using EndKnot.Modules;

namespace EndKnot.Roles;

internal class Minion : IGhostRole
{
    public static HashSet<byte> BlindPlayers = [];

    private static OptionItem BlindDuration;
    private static OptionItem CD;

    public Team Team => Team.Impostor;
    public RoleTypes RoleTypes => RoleTypes.GuardianAngel;
    public int Cooldown => CD.GetInt();

    public bool OnProtect(PlayerControl pc, PlayerControl target)
    {
        if (!BlindPlayers.Add(target.PlayerId)) return false;

        target.RPCPlayCustomSound("FlashBang");
        target.MarkDirtySettings();
        int duration = BlindDuration.GetInt();

        // Capture ONLY the value-type id (never an Il2Cpp PlayerControl) and re-fetch it inside the task.
        byte targetId = target.PlayerId;
        LateTask.New(() =>
        {
            if (BlindPlayers.Remove(targetId))
            {
                PlayerControl p = Utils.GetPlayerById(targetId);
                if (p != null) p.MarkDirtySettings();
            }
            RPC.PlaySoundRPC(targetId, Sounds.TaskComplete);
        }, duration, "Remove Minion Blindness");
        pc.AddAbilityCD(Cooldown + duration);
        return true;
    }

    public void OnAssign(PlayerControl pc) { }

    public void SetupCustomOption()
    {
        Options.SetupRoleOptions(649000, TabGroup.OtherRoles, CustomRoles.Minion);

        BlindDuration = new IntegerOptionItem(649002, "MinionBlindDuration", new(1, 90, 1), 5, TabGroup.OtherRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Minion])
            .SetValueFormat(OptionFormat.Seconds);

        CD = new IntegerOptionItem(649003, "AbilityCooldown", new(0, 120, 1), 30, TabGroup.OtherRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Minion])
            .SetValueFormat(OptionFormat.Seconds);
    }
}
