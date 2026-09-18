using System.Linq;
using AmongUs.GameOptions;

namespace EndKnot.Roles;

internal class OneWolf : IAddon
{
    public AddonTypes Type => AddonTypes.ImpOnly;

    // 0 = NoChange (通常どおりキルが通る) / 1 = Guard (キルを阻止) / 2 = Remove (キルは通るが属性を剥奪) / 3 = GuardAndRemove (阻止した上で属性を剥奪)
    private static readonly string[] KillMode =
    [
        "OneWolfKillMode.NoChange",
        "OneWolfKillMode.Guard",
        "OneWolfKillMode.Remove",
        "OneWolfKillMode.GuardAndRemove"
    ];

    public static OptionItem ImpostorKillMe;
    public static OptionItem MeCanKillImpostor;

    public void SetupCustomOption()
    {
        Options.SetupAdtRoleOptions(20420, CustomRoles.OneWolf, canSetNum: true, teamSpawnOptions: true);

        // インポスター限定アドオンなので、陣営トグルのうちインポスター以外は常に効かない (CheckAddonConflict の ImpOnly ゲートが先に落とす)
        (_, OptionItem neutral, OptionItem crew, OptionItem coven) = Options.AddonCanBeSettings[CustomRoles.OneWolf];
        neutral.SetHidden(true);
        crew.SetHidden(true);
        coven.SetHidden(true);

        ImpostorKillMe = new StringOptionItem(20430, "OneWolfImpostorKillMe", KillMode, 2, TabGroup.Addons)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.OneWolf]);
        MeCanKillImpostor = new StringOptionItem(20431, "OneWolfMeCanKillImpostor", KillMode, 2, TabGroup.Addons)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.OneWolf]);
    }

    /// <summary>一匹狼が抜けた場合、味方だった全インポスターとの desync を実際の役職へ戻す。</summary>
    public static void Remove(PlayerControl player)
    {
        if (!AmongUsClient.Instance.AmHost || !player.Is(CustomRoles.OneWolf)) return;

        Main.PlayerStates[player.PlayerId].RemoveSubRole(CustomRoles.OneWolf);

        RoleTypes playerAppearsAs = player.IsAlive() ? RoleTypes.Impostor : RoleTypes.ImpostorGhost;

        foreach (PlayerControl imp in Main.AllPlayerControlsToList.Where(p => p.PlayerId != player.PlayerId && p.Is(CustomRoleTypes.Impostor)))
        {
            RoleTypes impAppearsAs = imp.IsAlive() ? RoleTypes.Impostor : RoleTypes.ImpostorGhost;

            player.RpcSetRoleDesync(playerAppearsAs, imp.OwnerId, setRoleMap: true);
            imp.RpcSetRoleDesync(impAppearsAs, player.OwnerId, setRoleMap: true);

            // 互いの役職テキストが見えるようになるのは組ごとの通知でしか届かない
            Utils.NotifyRoles(SpecifySeer: player, SpecifyTarget: imp);
            Utils.NotifyRoles(SpecifySeer: imp, SpecifyTarget: player);
        }

        Logger.Info($"OneWolf add-on removed from {player.GetRealName()}", "OneWolf");
    }

    /// <summary>キル判定の前に呼ぶ。false を返した場合はキルを阻止する (Guard / GuardAndRemove)。</summary>
    public static bool OnCheckMurder(PlayerControl killer, PlayerControl target)
    {
        if (!AmongUsClient.Instance.AmHost) return true;

        if (killer.Is(CustomRoles.OneWolf) && target.Is(CustomRoleTypes.Impostor))
        {
            switch (MeCanKillImpostor.GetValue())
            {
                case 1:
                    return false;
                case 3:
                    Remove(killer);
                    return false;
            }
        }

        if (target.Is(CustomRoles.OneWolf) && killer.Is(CustomRoleTypes.Impostor))
        {
            switch (ImpostorKillMe.GetValue())
            {
                case 1:
                    return false;
                case 3:
                    Remove(target);
                    return false;
            }
        }

        return true;
    }

    /// <summary>キルが実際に成立した直後に呼ぶ。Remove モード (剥奪はするがキルは阻止しない) のみここで発火する。</summary>
    public static void OnMurderPlayer(PlayerControl killer, PlayerControl target)
    {
        if (!AmongUsClient.Instance.AmHost) return;
        if (killer.PlayerId == target.PlayerId) return; // 自滅も同じディスパッチへ来る

        if (killer.Is(CustomRoles.OneWolf) && target.Is(CustomRoleTypes.Impostor) && MeCanKillImpostor.GetValue() == 2)
            Remove(killer);

        if (target.Is(CustomRoles.OneWolf) && killer.Is(CustomRoleTypes.Impostor) && ImpostorKillMe.GetValue() == 2)
            Remove(target);
    }

    public static void ApplyDesync()
    {
        if (!AmongUsClient.Instance.AmHost) return;

        var oneWolves = Main.AllPlayerControlsToList
            .Where(p => p.Is(CustomRoles.OneWolf) && p.Is(CustomRoleTypes.Impostor))
            .ToList();

        if (oneWolves.Count == 0) return;

        var allImps = Main.AllPlayerControlsToList
            .Where(p => p.Is(CustomRoleTypes.Impostor))
            .ToList();

        foreach (PlayerControl ow in oneWolves)
        {
            foreach (PlayerControl imp in allImps)
            {
                if (ow.PlayerId == imp.PlayerId) continue;

                RoleTypes owAppearsAs = ow.IsAlive() ? RoleTypes.Crewmate : RoleTypes.CrewmateGhost;
                RoleTypes impAppearsAs = imp.IsAlive() ? RoleTypes.Crewmate : RoleTypes.CrewmateGhost;

                ow.RpcSetRoleDesync(owAppearsAs, imp.OwnerId, setRoleMap: true);
                imp.RpcSetRoleDesync(impAppearsAs, ow.OwnerId, setRoleMap: true);
            }
        }

        Logger.Info($"OneWolf desync applied for {oneWolves.Count} player(s)", "OneWolf");
    }
}
