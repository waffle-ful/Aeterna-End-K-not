using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using static EndKnot.Translator;

namespace EndKnot.Modules;

// 自己申告制のカミングアウト (CO)。本家 (TOH-hamo) は役職ごとに可否を設定できるが、
// 役職数が数百あるためカテゴリ単位 (Impostor/Neutral/Coven/Addon) のトグルに丸めている。
// 個別役職の禁止は将来コマンドで足せるよう、判定は全て TryDeclare の1箇所に集約してある。
public static class ComingOut
{
    private static OptionItem EnableComingOut;
    private static OptionItem AllowFalseClaim;
    private static OptionItem OnlyInMeeting;
    private static OptionItem MaxPerGame;
    private static OptionItem BlockImpostorRoles;
    private static OptionItem BlockNeutralRoles;
    private static OptionItem BlockCovenRoles;
    private static OptionItem BlockAddons;
    private static OptionItem EnableList;

    // プレイヤーID -> これまでにCOした役職一覧。試合開始でクリアする。
    private static readonly Dictionary<byte, List<CustomRoles>> Records = [];

    public static void SetupCustomOption()
    {
        new TextOptionItem(110120, "MenuTitle.ComingOut", TabGroup.GameSettings)
            .SetColor(new Color32(180, 255, 180, byte.MaxValue))
            .SetHeader(true);

        EnableComingOut = new BooleanOptionItem(960180, "EnableComingOut", false, TabGroup.GameSettings)
            .SetColor(new Color32(180, 255, 180, byte.MaxValue));

        AllowFalseClaim = new BooleanOptionItem(960181, "CoAllowFalseClaim", true, TabGroup.GameSettings)
            .SetParent(EnableComingOut)
            .SetColor(new Color32(180, 255, 180, byte.MaxValue));

        OnlyInMeeting = new BooleanOptionItem(960182, "CoOnlyInMeeting", true, TabGroup.GameSettings)
            .SetParent(EnableComingOut)
            .SetColor(new Color32(180, 255, 180, byte.MaxValue));

        MaxPerGame = new IntegerOptionItem(960183, "CoMaxPerGame", new(0, 10, 1), 0, TabGroup.GameSettings)
            .SetParent(EnableComingOut)
            .SetValueFormat(OptionFormat.Times)
            .SetColor(new Color32(180, 255, 180, byte.MaxValue));

        BlockImpostorRoles = new BooleanOptionItem(960184, "CoBlockImpostorRoles", false, TabGroup.GameSettings)
            .SetParent(EnableComingOut)
            .SetColor(new Color32(180, 255, 180, byte.MaxValue));

        BlockNeutralRoles = new BooleanOptionItem(960185, "CoBlockNeutralRoles", false, TabGroup.GameSettings)
            .SetParent(EnableComingOut)
            .SetColor(new Color32(180, 255, 180, byte.MaxValue));

        BlockCovenRoles = new BooleanOptionItem(960186, "CoBlockCovenRoles", false, TabGroup.GameSettings)
            .SetParent(EnableComingOut)
            .SetColor(new Color32(180, 255, 180, byte.MaxValue));

        BlockAddons = new BooleanOptionItem(960187, "CoBlockAddons", false, TabGroup.GameSettings)
            .SetParent(EnableComingOut)
            .SetColor(new Color32(180, 255, 180, byte.MaxValue));

        EnableList = new BooleanOptionItem(960188, "CoEnableList", true, TabGroup.GameSettings)
            .SetParent(EnableComingOut)
            .SetColor(new Color32(180, 255, 180, byte.MaxValue));
    }

    public static bool IsEnabled => EnableComingOut != null && EnableComingOut.GetBool();

    public static void Reset() => Records.Clear();

    private static void Reject(PlayerControl player, string reasonKey) => Utils.SendMessage(GetString(reasonKey), player.PlayerId);

    // isAddon=false: /co (自分の主役職を宣言) / isAddon=true: /aco (アドオンを宣言)
    public static void TryDeclare(PlayerControl player, CustomRoles role, bool isAddon)
    {
        if (!IsEnabled) { Reject(player, "ComingOut.Disabled"); return; }
        if (player == null || !player.IsAlive()) { Reject(player, "ComingOut.Dead"); return; }
        if (OnlyInMeeting.GetBool() && !GameStates.IsMeeting) { Reject(player, "ComingOut.OnlyInMeeting"); return; }

        if (isAddon && !role.IsAdditionRole()) { Reject(player, "ComingOut.NotAnAddon"); return; }
        if (!isAddon && role.IsAdditionRole()) { Reject(player, "ComingOut.NotAMainRole"); return; }

        if (!AllowFalseClaim.GetBool())
        {
            bool actuallyHas = isAddon ? player.GetCustomSubRoles().Contains(role) : player.GetCustomRole() == role;
            if (!actuallyHas) { Reject(player, "ComingOut.FalseClaimBlocked"); return; }
        }

        if (isAddon)
        {
            if (BlockAddons.GetBool()) { Reject(player, "ComingOut.AddonBlocked"); return; }
        }
        else
        {
            Team team = role.GetTeam();
            if (team == Team.Impostor && BlockImpostorRoles.GetBool()) { Reject(player, "ComingOut.ImpostorBlocked"); return; }
            if (team == Team.Neutral && BlockNeutralRoles.GetBool()) { Reject(player, "ComingOut.NeutralBlocked"); return; }
            if (team == Team.Coven && BlockCovenRoles.GetBool()) { Reject(player, "ComingOut.CovenBlocked"); return; }
        }

        int max = MaxPerGame.GetInt();
        if (max > 0 && Records.TryGetValue(player.PlayerId, out List<CustomRoles> existing) && existing.Count >= max)
        {
            Reject(player, "ComingOut.MaxReached");
            return;
        }

        if (!Records.TryGetValue(player.PlayerId, out List<CustomRoles> list))
        {
            list = [];
            Records[player.PlayerId] = list;
        }

        list.Add(role);

        Utils.SendMessage(string.Format(GetString("ComingOut.Announce"), player.PlayerId.ColoredPlayerName(), role.ToColoredString()), byte.MaxValue);
    }

    public static void SendList(PlayerControl requester)
    {
        if (!IsEnabled) { Reject(requester, "ComingOut.Disabled"); return; }
        if (!EnableList.GetBool()) { Reject(requester, "ComingOut.ListDisabled"); return; }

        if (Records.Count == 0)
        {
            Utils.SendMessage(GetString("ComingOut.ListEmpty"), requester.PlayerId);
            return;
        }

        string body = string.Join('\n', Records.Select(kvp => $"{kvp.Key.ColoredPlayerName()}: {string.Join(", ", kvp.Value.Select(r => r.ToColoredString()))}"));
        Utils.SendMessage(body, requester.PlayerId, GetString("ComingOut.ListTitle"));
    }
}
