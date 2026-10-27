using System.Linq;

namespace EndKnot.Roles;

// コンビ役職の片割れが役目を失った時の変化先。光/影 (追放後) とファーリー (ケモノの死後) が同じ候補を共有する。
public static class PairRoleChange
{
    public static readonly CustomRoles[] Candidates =
    [
        CustomRoles.Opportunist,
        CustomRoles.CrewmateEndKnot,
        CustomRoles.Madmate,
        CustomRoles.Jester,
        CustomRoles.Traitor,
        CustomRoles.Turncoat,
        CustomRoles.Emptiness
    ];

    public static string[] OptionNames() => Candidates.Select(x => x.ToColoredString()).ToArray();

    // マッドメイトはクルーメイトにアドオンを付けて表す。それ以外は主役職の差し替えとベースの切り替え。
    public static void Apply(PlayerControl pc, int index)
    {
        if (!AmongUsClient.Instance.AmHost || !pc || !pc.IsAlive()) return;

        CustomRoles chosen = Candidates[System.Math.Clamp(index, 0, Candidates.Length - 1)];
        CustomRoles main = chosen == CustomRoles.Madmate ? CustomRoles.CrewmateEndKnot : chosen;

        pc.RpcSetCustomRole(main);
        pc.RpcChangeRoleBasis(main);
        if (chosen == CustomRoles.Madmate) pc.RpcSetCustomRole(CustomRoles.Madmate);

        Logger.Info($"{pc.GetNameWithRole().RemoveHtmlTags()} changed to {chosen}", "PairRoleChange");
        Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc);
    }
}
