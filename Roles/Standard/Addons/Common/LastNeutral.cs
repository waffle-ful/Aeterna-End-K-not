using System.Linq;
using Hazel;
using static EndKnot.Options;

namespace EndKnot.Roles;

internal class LastNeutral : IAddon
{
    public static byte CurrentId = byte.MaxValue;
    public static OptionItem KillCooldown;
    public static OptionItem GiveOpportunist;
    private static OptionItem AssignIfAloneFromStart;
    private static bool SawMultipleNeutrals;
    public AddonTypes Type => AddonTypes.Mixed;

    public void SetupCustomOption()
    {
        // 最後の 1 人へ自動で付く属性で、開始時のランダム抽選は通らない。
        // 人数も出現率も選出に使われないので隠し、陣営トグルはそもそも作らない
        // (作ると CheckAddonConflict がその値を見るため、古いプリセットの「ニュートラル: OFF」が
        //  Reroll 時の剥奪として残り、隠した UI からは直せなくなる)。
        SetupAdtRoleOptions(20480, CustomRoles.LastNeutral, canSetChance: false);

        KillCooldown = new FloatOptionItem(20490, "LastNeutralKillCooldown", new(0f, 180f, 1f), 15f, TabGroup.Addons)
            .SetParent(CustomRoleSpawnChances[CustomRoles.LastNeutral])
            .SetValueFormat(OptionFormat.Seconds);

        GiveOpportunist = new BooleanOptionItem(20491, "LastNeutralGiveOpportunist", false, TabGroup.Addons)
            .SetParent(CustomRoleSpawnChances[CustomRoles.LastNeutral]);

        AssignIfAloneFromStart = new BooleanOptionItem(20492, "LastNeutralAssignIfAloneFromStart", true, TabGroup.Addons)
            .SetParent(CustomRoleSpawnChances[CustomRoles.LastNeutral]);
    }

    public static void Init()
    {
        CurrentId = byte.MaxValue;
        SawMultipleNeutrals = false;
    }

    public static void SetKillCooldown()
    {
        if (CurrentId == byte.MaxValue) return;
        if (!Main.AllPlayerKillCooldown.ContainsKey(CurrentId)) return;

        Main.AllPlayerKillCooldown[CurrentId] = KillCooldown.GetFloat();
    }

    private static bool CanBeLastNeutral(PlayerControl pc)
    {
        // 浄化された相手への付与は RpcSetCustomRole (ExtendedPlayerControl.cs:100) が無音で落とす。
        // 撃つ前に弾かないと、属性が付いていないのに CurrentId だけ埋まってキルクールが変わり、
        // しかも以後この試合で誰にも付与されなくなる (逆に CurrentId を立てないと毎 tick 撃ち続ける)。
        return pc.IsAlive() && !pc.Is(CustomRoles.LastNeutral) && pc.GetCustomRole().IsNeutral() &&
               (Cleanser.CleansedCanGetAddon.GetBool() || !pc.Is(CustomRoles.Cleansed));
    }

    public static void SetSubRole()
    {
        if (CurrentId != byte.MaxValue || !AmongUsClient.Instance.AmHost) return;
        if (Options.CurrentGameMode != CustomGameMode.Standard) return;
        if (!CustomRoles.LastNeutral.IsEnable()) return;

        var aliveNeutrals = Main.EnumerateAlivePlayerControls()
            .Where(pc => pc.GetCustomRole().IsNeutral())
            .ToList();

        if (aliveNeutrals.Count >= 2) SawMultipleNeutrals = true;

        if (aliveNeutrals.Count != 1) return;

        // 開始時からニュートラルが 1 人しかいない試合で付けるかは設定次第
        if (!SawMultipleNeutrals && !AssignIfAloneFromStart.GetBool()) return;

        PlayerControl pc = aliveNeutrals[0];
        if (!CanBeLastNeutral(pc)) return;

        pc.RpcSetCustomRole(CustomRoles.LastNeutral);
        CurrentId = pc.PlayerId;
        SetKillCooldown();

        var sender = CustomRpcSender.Create("LastNeutral", SendOption.Reliable);
        var hasValue = false;
        hasValue |= sender.SyncSettings(pc);
        hasValue |= sender.NotifyRolesSpecific(pc, pc, out sender, out bool cleared);
        if (cleared) hasValue = false;

        // 仲間を殺してラストになる流れが典型なので、走行中のキルタイマーも新しいクールへ切り詰める
        if (Main.KillTimers.TryGetValue(pc.PlayerId, out float timer) &&
            Main.AllPlayerKillCooldown.TryGetValue(pc.PlayerId, out float cd) &&
            timer > cd)
            hasValue |= sender.SetKillCooldown(pc);

        sender.SendMessage(!hasValue);

        Logger.Info($"LastNeutral assigned to {pc.GetRealName()}", "LastNeutral");
    }
}
