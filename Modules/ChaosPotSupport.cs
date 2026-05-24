using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using static EndKnot.Translator;

namespace EndKnot;

public static class ChaosPotSupport
{
    public static OptionItem Enable;
    public static OptionItem RevealImpostor;
    public static OptionItem RevealNeutral;
    public static OptionItem RevealCoven;
    public static OptionItem RevealCrewmate;
    public static OptionItem ShowOnMeetingScreen;

    private static List<(CustomRoles Role, int Count)> ImpostorRoles = [];
    private static List<(CustomRoles Role, int Count)> NeutralRoles = [];
    private static List<(CustomRoles Role, int Count)> CovenRoles = [];
    private static List<(CustomRoles Role, int Count)> CrewmateRoles = [];

    public static void SetupOptions(int id)
    {
        Enable = new BooleanOptionItem(id, "ChaosPotSupport.Enable", false, TabGroup.GameSettings)
            .SetGameMode(CustomGameMode.Standard)
            .SetColor(new Color32(255, 178, 102, byte.MaxValue));

        RevealImpostor = new BooleanOptionItem(id + 1, "ChaosPotSupport.RevealImpostor", true, TabGroup.GameSettings)
            .SetParent(Enable)
            .SetGameMode(CustomGameMode.Standard)
            .SetColor(new Color32(255, 178, 102, byte.MaxValue));

        RevealNeutral = new BooleanOptionItem(id + 2, "ChaosPotSupport.RevealNeutral", true, TabGroup.GameSettings)
            .SetParent(Enable)
            .SetGameMode(CustomGameMode.Standard)
            .SetColor(new Color32(255, 178, 102, byte.MaxValue));

        RevealCoven = new BooleanOptionItem(id + 3, "ChaosPotSupport.RevealCoven", true, TabGroup.GameSettings)
            .SetParent(Enable)
            .SetGameMode(CustomGameMode.Standard)
            .SetColor(new Color32(255, 178, 102, byte.MaxValue));

        RevealCrewmate = new BooleanOptionItem(id + 4, "ChaosPotSupport.RevealCrewmate", false, TabGroup.GameSettings)
            .SetParent(Enable)
            .SetGameMode(CustomGameMode.Standard)
            .SetColor(new Color32(255, 178, 102, byte.MaxValue));

        ShowOnMeetingScreen = new BooleanOptionItem(id + 5, "ChaosPotSupport.ShowOnMeetingScreen", true, TabGroup.GameSettings)
            .SetParent(Enable)
            .SetGameMode(CustomGameMode.Standard)
            .SetColor(new Color32(255, 178, 102, byte.MaxValue));
    }

    public static void Init()
    {
        ImpostorRoles.Clear();
        NeutralRoles.Clear();
        CovenRoles.Clear();
        CrewmateRoles.Clear();
    }

    public static void BuildRoleListFromCurrentGame()
    {
        Init();
        if (!Enable.GetBool()) return;
        if (Options.CurrentGameMode != CustomGameMode.Standard) return;

        Dictionary<CustomRoles, int> impCount = [];
        Dictionary<CustomRoles, int> neutCount = [];
        Dictionary<CustomRoles, int> covCount = [];
        Dictionary<CustomRoles, int> crewCount = [];

        foreach (PlayerControl pc in Main.AllPlayerControlsToList)
        {
            if (!Main.PlayerStates.TryGetValue(pc.PlayerId, out PlayerState state)) continue;
            CustomRoles role = state.MainRole;
            if (role == CustomRoles.NotAssigned || role == CustomRoles.GM) continue;

            if (role.IsImpostor()) Bump(impCount, role);
            else if (role.IsCoven()) Bump(covCount, role);
            else if (role.IsNeutral()) Bump(neutCount, role);
            else Bump(crewCount, role);
        }

        ImpostorRoles = impCount.Select(kv => (kv.Key, kv.Value)).OrderByDescending(x => x.Value).ThenBy(x => x.Key.ToString()).ToList();
        NeutralRoles = neutCount.Select(kv => (kv.Key, kv.Value)).OrderByDescending(x => x.Value).ThenBy(x => x.Key.ToString()).ToList();
        CovenRoles = covCount.Select(kv => (kv.Key, kv.Value)).OrderByDescending(x => x.Value).ThenBy(x => x.Key.ToString()).ToList();
        CrewmateRoles = crewCount.Select(kv => (kv.Key, kv.Value)).OrderByDescending(x => x.Value).ThenBy(x => x.Key.ToString()).ToList();

        return;

        static void Bump(Dictionary<CustomRoles, int> dict, CustomRoles r)
        {
            dict[r] = dict.GetValueOrDefault(r) + 1;
        }
    }

    private static string FormatRoleList(List<(CustomRoles Role, int Count)> roles)
    {
        if (roles.Count == 0) return GetString("ChaosPotSupport.None");

        var sb = new StringBuilder();
        for (int i = 0; i < roles.Count; i++)
        {
            (CustomRoles role, int count) = roles[i];
            string name = Utils.GetRoleName(role);
            string colored = Utils.ColorString(Utils.GetRoleColor(role), name);
            sb.Append(colored);
            if (count > 1) sb.Append($" x{count}");
            if (i < roles.Count - 1) sb.Append(", ");
        }

        return sb.ToString();
    }

    public static string GetMeetingScreenText()
    {
        if (!Enable.GetBool() || !ShowOnMeetingScreen.GetBool()) return string.Empty;

        var sb = new StringBuilder("<line-height=95%>");
        bool any = false;

        if (RevealImpostor.GetBool())
        {
            sb.Append($"<#ff1919>{GetString("ChaosPotSupport.HeaderImpostor")}</color>\n");
            sb.Append(FormatRoleList(ImpostorRoles));
            any = true;
        }

        if (RevealNeutral.GetBool())
        {
            if (any) sb.Append('\n');
            sb.Append($"<#7f7f7f>{GetString("ChaosPotSupport.HeaderNeutral")}</color>\n");
            sb.Append(FormatRoleList(NeutralRoles));
            any = true;
        }

        if (RevealCoven.GetBool())
        {
            if (any) sb.Append('\n');
            sb.Append($"<#7b599e>{GetString("ChaosPotSupport.HeaderCoven")}</color>\n");
            sb.Append(FormatRoleList(CovenRoles));
        }

        return sb.ToString();
    }

    public static List<(string Title, string Body)> BuildChatSections(bool includeCrewmate)
    {
        List<(string, string)> sections = [];
        if (!Enable.GetBool()) return sections;

        if (RevealImpostor.GetBool())
            sections.Add((Utils.ColorString(Utils.GetRoleColor(CustomRoles.Impostor), GetString("ChaosPotSupport.HeaderImpostor")), FormatRoleList(ImpostorRoles)));

        if (RevealNeutral.GetBool())
            sections.Add((Utils.ColorString(new Color32(127, 127, 127, 255), GetString("ChaosPotSupport.HeaderNeutral")), FormatRoleList(NeutralRoles)));

        if (RevealCoven.GetBool())
            sections.Add((Utils.ColorString(Utils.GetRoleColor(CustomRoles.CovenLeader), GetString("ChaosPotSupport.HeaderCoven")), FormatRoleList(CovenRoles)));

        if (includeCrewmate && RevealCrewmate.GetBool())
            sections.Add((Utils.ColorString(Utils.GetRoleColor(CustomRoles.Crewmate), GetString("ChaosPotSupport.HeaderCrewmate")), FormatRoleList(CrewmateRoles)));

        return sections;
    }

    public static void SendChatBroadcastOnMeetingStart()
    {
        if (!Enable.GetBool()) return;
        if (Options.CurrentGameMode != CustomGameMode.Standard) return;

        List<(string Title, string Body)> sections = BuildChatSections(includeCrewmate: true);
        if (sections.Count == 0) return;

        foreach ((string title, string body) in sections)
            Utils.SendMessage("\n" + body, byte.MaxValue, title);
    }

    public static void SendChatToPlayer(PlayerControl player)
    {
        if (!player) return;
        if (!Enable.GetBool())
        {
            Utils.SendMessage(GetString("ChaosPotSupport.Disabled"), player.PlayerId);
            return;
        }

        List<(string Title, string Body)> sections = BuildChatSections(includeCrewmate: true);
        if (sections.Count == 0)
        {
            Utils.SendMessage(GetString("ChaosPotSupport.NothingRevealed"), player.PlayerId);
            return;
        }

        foreach ((string title, string body) in sections)
            Utils.SendMessage("\n" + body, player.PlayerId, title);
    }
}
