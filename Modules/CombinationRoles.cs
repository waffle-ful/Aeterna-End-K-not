using System.Collections.Generic;
using System.Linq;

namespace EndKnot;

// 2人1組で配られる役職 (コンビネーション役職)。
// ペアは「主役職」と「相方」で構成され、出現率・人数・役職オプションは主役職側に1つだけ持つ。
// 相方は自分の出現率オプションを持たないので抽選プールには入らず、主役職が選ばれた時だけ
// EnsurePartners で枠を確保して配られる。
public static class CombinationRoles
{
    public static readonly UnityEngine.Color32 TabColor32 = new(247, 193, 20, 255);
    public static UnityEngine.Color TabColor => TabColor32;

    // 主役職 → 相方
    public static readonly Dictionary<CustomRoles, CustomRoles> Pairs = new()
    {
        [CustomRoles.Driver] = CustomRoles.Braid,
        [CustomRoles.Vega] = CustomRoles.Altair
    };

    private static Dictionary<CustomRoles, CustomRoles> partnerToPrimary;

    private static Dictionary<CustomRoles, CustomRoles> PartnerToPrimary => partnerToPrimary ??= Pairs.ToDictionary(x => x.Value, x => x.Key);

    public static bool IsCombinationRole(this CustomRoles role) => Pairs.ContainsKey(role) || PartnerToPrimary.ContainsKey(role);

    public static bool IsCombinationPrimary(this CustomRoles role) => Pairs.ContainsKey(role);

    public static bool IsCombinationPartner(this CustomRoles role) => PartnerToPrimary.ContainsKey(role);

    public static CustomRoles GetCombinationPartner(this CustomRoles role)
    {
        if (Pairs.TryGetValue(role, out CustomRoles partner)) return partner;
        return PartnerToPrimary.TryGetValue(role, out CustomRoles primary) ? primary : CustomRoles.NotAssigned;
    }

    public static CustomRoles GetCombinationPrimary(this CustomRoles role)
    {
        if (Pairs.ContainsKey(role)) return role;
        return PartnerToPrimary.TryGetValue(role, out CustomRoles primary) ? primary : CustomRoles.NotAssigned;
    }

    // 「ドライバーとブレイド」形式のペア名
    public static string GetCombinationName(this CustomRoles role, bool colored = true)
    {
        CustomRoles primary = role.GetCombinationPrimary();
        if (primary == CustomRoles.NotAssigned) return colored ? role.ToColoredString() : Translator.GetString($"{role}");

        CustomRoles partner = primary.GetCombinationPartner();
        string a = colored ? primary.ToColoredString() : Translator.GetString($"{primary}");
        string b = colored ? partner.ToColoredString() : Translator.GetString($"{partner}");
        return string.Format(Translator.GetString("CombinationName"), a, b);
    }

    // 配役リストの最終調整。主役職が入っていれば相方を1枠入れ、入れる枠が無ければ主役職を外す。
    // preSetRoles はホストの事前指定分 (finalRoles とは別枠で既に席が決まっている役職)。
    public static void EnsurePartners(List<CustomRoles> finalRoles, ICollection<CustomRoles> preSetRoles, int slots)
    {
        if (Pairs.Count == 0) return;

        foreach ((CustomRoles primary, CustomRoles partner) in Pairs)
        {
            bool primaryPresent = finalRoles.Contains(primary) || preSetRoles.Contains(primary);
            bool partnerPresent = finalRoles.Contains(partner) || preSetRoles.Contains(partner);

            if (!primaryPresent)
            {
                if (partnerPresent) Logger.Warn($"{partner} is assigned without {primary}", "CombinationRoles");
                continue;
            }

            if (partnerPresent) continue;

            if (finalRoles.Count < slots)
            {
                finalRoles.Add(partner);
                Logger.Info($"{primary} selected: added {partner}", "CombinationRoles");
                continue;
            }

            // 満席ならクルー陣営の通常役職1つを相方に差し替える (恋人など2人1組で入っている役職は崩さない)。
            // 出現率 100% の役職は「必ず出す」設定なので、それ以外から無作為に選ぶ
            List<int> candidates = Enumerable.Range(0, finalRoles.Count)
                .Where(i => finalRoles[i] is var x && x.IsCrewmate() && !x.IsMadmate() && !x.IsCombinationRole() && x is not (CustomRoles.LovingCrewmate or CustomRoles.LovingImpostor))
                .ToList();
            List<int> preferred = candidates.FindAll(i => finalRoles[i].GetMode() < 100);
            if (preferred.Count > 0) candidates = preferred;
            int replaceIndex = candidates.Count > 0 ? candidates[IRandom.Instance.Next(candidates.Count)] : -1;

            if (replaceIndex >= 0)
            {
                Logger.Info($"{primary} selected: replaced {finalRoles[replaceIndex]} with {partner}", "CombinationRoles");
                finalRoles[replaceIndex] = partner;
                continue;
            }

            // 相方を置けないなら主役職を同陣営の素役職に戻す (席数は変えない)
            int primaryIndex = finalRoles.IndexOf(primary);

            if (primaryIndex >= 0)
            {
                finalRoles[primaryIndex] = primary.IsImpostor() ? CustomRoles.ImpostorEndKnot : CustomRoles.CrewmateEndKnot;
                Logger.Warn($"No slot for {partner}: replaced {primary} with {finalRoles[primaryIndex]}", "CombinationRoles");
            }
            else
                Logger.Warn($"No slot for {partner}: pre-set {primary} is assigned alone", "CombinationRoles");
        }
    }
}
