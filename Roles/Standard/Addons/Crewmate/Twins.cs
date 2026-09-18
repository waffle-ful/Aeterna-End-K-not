using System;
using System.Collections.Generic;
using System.Linq;
using EndKnot.Patches;
using static EndKnot.Options;

namespace EndKnot.Roles;

internal class Twins : IAddon
{
    public static OptionItem AddWin;
    public static OptionItem CanAssingMadmate;
    public static OptionItem CanAssingCantKillNeutral;
    public static OptionItem DieFollow;
    public static readonly Dictionary<byte, byte> Pairs = [];
    public AddonTypes Type => AddonTypes.Mixed;

    public void SetupCustomOption()
    {
        SetupAdtRoleOptions(20440, CustomRoles.Twins, canSetNum: true, teamSpawnOptions: true);

        AddWin = new BooleanOptionItem(20450, "TwinsAddWin", true, TabGroup.Addons)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Twins]);

        CanAssingMadmate = new BooleanOptionItem(20451, "TwinsCanAssingMadmate", false, TabGroup.Addons)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Twins]);

        CanAssingCantKillNeutral = new BooleanOptionItem(20452, "TwinsCanAssingCantKillNeutral", false, TabGroup.Addons)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Twins]);

        DieFollow = new BooleanOptionItem(20453, "TwinsDiefollow", false, TabGroup.Addons)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Twins]);
    }

    public static void Init()
    {
        Pairs.Clear();

        if (!AmongUsClient.Instance.AmHost) return;

        List<PlayerControl> players = Main.AllPlayerControlsToList
            .Where(p => p.Is(CustomRoles.Twins))
            .OrderBy(_ => Guid.NewGuid())
            .ToList();

        for (int i = 0; i + 1 < players.Count; i += 2)
        {
            byte a = players[i].PlayerId;
            byte b = players[i + 1].PlayerId;
            Pairs[a] = b;
            Pairs[b] = a;
            Logger.Info($"Twins paired: {players[i].GetRealName()} <-> {players[i + 1].GetRealName()}", "Twins");
        }

        if (players.Count % 2 == 1)
        {
            PlayerControl unpaired = players[^1];
            Main.PlayerStates[unpaired.PlayerId].RemoveSubRole(CustomRoles.Twins);
            Logger.Info($"Twins unpaired (removed): {unpaired.GetRealName()}", "Twins");
        }
    }

    public static bool ArePartners(byte a, byte b)
    {
        return Pairs.TryGetValue(a, out byte partner) && partner == b;
    }

    // 片割れの死亡が確定した直後に呼ぶ。生存側の片割れだけを後追いさせる。
    // isExiled=true (追放中) は Lovers と同じ経路 (CheckForEndVotingPatch.TryAddAfterMeetingDeathPlayers) で
    // 会議明けへ持ち越す。false はその場で Suicide() (会議中/イントロ中は自身が窓明けまで自動で遅延する)。
    public static void CheckFollowingSuicide(byte deadPlayerId, bool isExiled = false)
    {
        if (!DieFollow.GetBool()) return;
        if (!Pairs.TryGetValue(deadPlayerId, out byte partnerId)) return;

        PlayerControl deadTwin = Utils.GetPlayerById(deadPlayerId);
        if (deadTwin != null && deadTwin.IsAlive()) return;

        PlayerControl survivingTwin = Utils.GetPlayerById(partnerId);
        if (survivingTwin == null || !survivingTwin.IsAlive()) return;

        Logger.Info($"{survivingTwin.GetRealName()} follows twin (id {deadPlayerId}) in death", "Twins");

        if (isExiled)
            CheckForEndVotingPatch.TryAddAfterMeetingDeathPlayers(PlayerState.DeathReason.FollowingSuicide, survivingTwin.PlayerId);
        else
            survivingTwin.Suicide(PlayerState.DeathReason.FollowingSuicide);
    }
}
