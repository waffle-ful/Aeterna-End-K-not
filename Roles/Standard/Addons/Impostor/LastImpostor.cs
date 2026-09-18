using System.Linq;
using Hazel;

namespace EndKnot.Roles;

public class LastImpostor : IAddon
{
    private const int Id = 15900;
    public static byte CurrentId = byte.MaxValue;

    private static OptionItem Reduction;
    public AddonTypes Type => AddonTypes.ImpOnly;

    public void SetupCustomOption()
    {
        Options.SetupSingleRoleOptions(Id, TabGroup.Addons, CustomRoles.LastImpostor, zeroOne: true);

        Reduction = new FloatOptionItem(Id + 15, "ArroganceReduceKillCooldown", new(5f, 95f, 5f), 20f, TabGroup.Addons)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.LastImpostor])
            .SetValueFormat(OptionFormat.Percent);
    }

    public static void Init()
    {
        CurrentId = byte.MaxValue;
    }

    private static void Add(byte id)
    {
        CurrentId = id;
    }

    public static void SetKillCooldown()
    {
        if (CurrentId == byte.MaxValue) return;

        if (!Main.AllPlayerKillCooldown.TryGetValue(CurrentId, out float cd)) return;

        float minus = cd * (Reduction.GetFloat() / 100f);
        Main.AllPlayerKillCooldown[CurrentId] -= minus;
        Logger.Info($"{CurrentId.ColoredPlayerName().RemoveHtmlTags()}'s cooldown is {Main.AllPlayerKillCooldown[CurrentId]}s", "LastImpostor");
    }

    private static bool CanBeLastImpostor(PlayerControl pc)
    {
        // 浄化された相手への付与は RpcSetCustomRole (ExtendedPlayerControl.cs:100) が無音で落とす。
        // 撃つ前に弾かないと、属性が付いていないのに CurrentId だけ埋まってキルクールが変わり、
        // しかも以後この試合で誰にも付与されなくなる (逆に CurrentId を立てないと毎 tick 撃ち続ける)。
        return pc.IsAlive() && !pc.Is(CustomRoles.LastImpostor) && pc.Is(CustomRoleTypes.Impostor) &&
               (Cleanser.CleansedCanGetAddon.GetBool() || !pc.Is(CustomRoles.Cleansed));
    }

    public static void SetSubRole()
    {
        if (CurrentId != byte.MaxValue || !AmongUsClient.Instance.AmHost) return;

        if (Options.CurrentGameMode != CustomGameMode.Standard || !CustomRoles.LastImpostor.IsEnable() || Main.EnumerateAlivePlayerControls().Count(pc => pc.Is(CustomRoleTypes.Impostor)) != 1) return;

        var players = Main.CachedAlivePlayerControls();
        for (byte playerId = 0; playerId < players.Count; playerId++)
        {
            PlayerControl pc = players[playerId];
            if (CanBeLastImpostor(pc))
            {
                pc.RpcSetCustomRole(CustomRoles.LastImpostor);
                Add(pc.PlayerId);
                SetKillCooldown();

                var sender = CustomRpcSender.Create("LastImpostor", SendOption.Reliable);
                var hasValue = false;
                hasValue |= sender.SyncSettings(pc);
                hasValue |= sender.NotifyRolesSpecific(pc, pc, out sender, out bool cleared);
                if (cleared) hasValue = false;

                if (Main.KillTimers.TryGetValue(pc.PlayerId, out float timer) &&
                    Main.AllPlayerKillCooldown.TryGetValue(pc.PlayerId, out float cd) &&
                    timer > cd)
                    hasValue |= sender.SetKillCooldown(pc);

                sender.SendMessage(!hasValue);

                break;
            }
        }
    }
}