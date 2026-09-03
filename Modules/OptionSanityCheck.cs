using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using static EndKnot.Translator;

namespace EndKnot.Modules;

// ゲーム開始ボタンが押された瞬間に設定同士の矛盾を洗い、ホストへ警告する。
// 開始そのものはブロックしない (配信中の進行を止めないため)。
public static class OptionSanityCheck
{
    private static OptionItem EnableOptionSanityCheck;
    private static OptionItem AutoFix;
    private static OptionItem AlsoWarnInLobbyChat;

    private const int MaxReportedViolations = 10;

    private sealed record SanityRule(string Key, Func<bool> IsViolated, Action Fix, Func<string> Describe);

    private static readonly List<SanityRule> Rules =
    [
        new("ZeroMaxWithChance",
            () => RolesWithChanceButZeroMax().Count > 0,
            FixZeroMaxWithChance,
            () => string.Format(GetString("OptionSanity.ZeroMaxWithChance"),
                string.Join(", ", RolesWithChanceButZeroMax().Select(x => x.ToColoredString())))),

        new("FactionMinAboveMax",
            () => TeamsWithMinAboveMax().Count > 0,
            FixFactionMinAboveMax,
            () => string.Format(GetString("OptionSanity.FactionMinAboveMax"),
                string.Join(", ", TeamsWithMinAboveMax().Select(x => Utils.ColorString(x.GetColor(), GetString($"ShortTeamName.{x}")))))),

        new("NkEnabledButMaxZero",
            () => AnyNeutralKillingRoleEnabled() && Options.MaxNNKs.GetInt() == 0,
            () => SetIntWithoutSave((IntegerOptionItem)Options.MaxNNKs, 1),
            () => GetString("OptionSanity.NkEnabledButMaxZero")),

        // Main.RealOptionsData はロビーでは未設定のことがある (BeginGame の Prefix 時点では null)。
        // 読む前に必ず確認する — ここで落ちると後続のルールが1つも評価されない。
        new("NoTaskCountButTaskWin",
            () => Main.RealOptionsData != null && !Options.DisableTaskWin.GetBool() && Utils.TotalTaskCount == 0,
            null,
            () => GetString("OptionSanity.NoTaskCountButTaskWin")),

        // 母数は EnumeratePlayerControls (PlayerId >= 200 の CNO を除外) で数える。
        // AllPlayerControls を生で使うとロビーの装飾 CNO が人数に混ざって警告が出なくなる。
        // 5 人未満のロビー (開発・テスト用) では必ず成立してしまい、毎回警告が出て邪魔になるので判定しない。
        new("TooManyImpostors",
            () => Main.EnumeratePlayerControls().Count() >= 5 && Main.NormalOptions.NumImpostors * 2 >= Main.EnumeratePlayerControls().Count(),
            null,
            () => string.Format(GetString("OptionSanity.TooManyImpostors"), Main.NormalOptions.NumImpostors, Main.EnumeratePlayerControls().Count()))
    ];

    public static void SetupCustomOption()
    {
        new TextOptionItem(110080, "MenuTitle.OptionSanityCheck", TabGroup.GameSettings)
            .SetColor(new Color32(255, 200, 60, byte.MaxValue))
            .SetHeader(true);

        EnableOptionSanityCheck = new BooleanOptionItem(960070, "EnableOptionSanityCheck", true, TabGroup.GameSettings)
            .SetColor(new Color32(255, 200, 60, byte.MaxValue));

        AutoFix = new BooleanOptionItem(960071, "OptionSanityAutoFix", false, TabGroup.GameSettings)
            .SetParent(EnableOptionSanityCheck)
            .SetColor(new Color32(255, 200, 60, byte.MaxValue));

        AlsoWarnInLobbyChat = new BooleanOptionItem(960072, "OptionSanityAlsoWarnInLobbyChat", true, TabGroup.GameSettings)
            .SetParent(EnableOptionSanityCheck)
            .SetColor(new Color32(255, 200, 60, byte.MaxValue));
    }

    // 役職の出現率 > 0% なのに最大数だけ 0 になっている役職 (加算役職の allowZeroCount 枠で起こりうる)
    private static List<CustomRoles> RolesWithChanceButZeroMax()
    {
        return Options.CustomRoleCounts.Keys
            .Where(role => Options.GetRoleSpawnMode(role) > 0 && Options.GetRoleCount(role) == 0)
            .ToList();
    }

    private static void FixZeroMaxWithChance()
    {
        foreach (CustomRoles role in RolesWithChanceButZeroMax())
        {
            if (Options.CustomRoleCounts[role] is IntegerOptionItem countOption)
                SetIntWithoutSave(countOption, 1);
        }
    }

    private static List<Team> TeamsWithMinAboveMax()
    {
        return Options.FactionMinMaxSettings
            .Where(x => x.Value.MinSetting.GetInt() > x.Value.MaxSetting.GetInt())
            .Select(x => x.Key)
            .ToList();
    }

    private static void FixFactionMinAboveMax()
    {
        foreach (Team team in TeamsWithMinAboveMax())
        {
            (OptionItem minSetting, OptionItem maxSetting) = Options.FactionMinMaxSettings[team];
            if (minSetting is IntegerOptionItem intMin)
                SetIntWithoutSave(intMin, maxSetting.GetInt());
        }
    }

    private static bool AnyNeutralKillingRoleEnabled()
    {
        return Options.CustomRoleCounts.Keys.Any(role => role.IsNK() && Options.GetRoleSpawnMode(role) > 0);
    }

    // OptionItem.SetValue の 3 引数オーバーロード (doSave 明示) はプリセットへ保存しない。
    // サブクラスの仮想 SetValue はここを doSave:true 固定で呼ぶため、AutoFix はこちらを直接使う。
    // doSync も切る: この直後に GameStartRandomMap.Prefix が OptionItem.SyncAllOptions() を無条件で走らせるので、
    // 補正1件ごとに SyncCustomSettingsRPC を撃つと Play 押下時のヒッチに乗るだけで何も得しない。
    private static void SetIntWithoutSave(IntegerOptionItem option, int rawValue)
    {
        option.SetValue(option.Rule.GetNearestIndex(rawValue), doSave: false, doSync: false);
    }

    public static void RunAndReport()
    {
        try
        {
            if (!AmongUsClient.Instance.AmHost || EnableOptionSanityCheck?.GetBool() != true) return;

            bool autoFix = AutoFix.GetBool();
            List<string> messages = [];

            // ルールは1本ずつ隔離して評価する。1つが例外を投げても残りは動かす
            // (ルールは今後1本ずつ足していくもので、1本の事故で機能全体を黙らせない)。
            foreach (SanityRule rule in Rules)
            {
                try
                {
                    if (!rule.IsViolated()) continue;

                    string description = rule.Describe();
                    Logger.Warn(description, "OptionSanity");

                    if (autoFix && rule.Fix != null)
                    {
                        rule.Fix();
                        Logger.Info($"Auto-fixed: {rule.Key}", "OptionSanity");
                    }

                    if (messages.Count < MaxReportedViolations) messages.Add(description);
                }
                catch (Exception e)
                {
                    Logger.Warn($"Rule {rule.Key} threw and was skipped: {e.Message}", "OptionSanity");
                }
            }

            if (messages.Count == 0 || !AlsoWarnInLobbyChat.GetBool()) return;

            string body = string.Join("\n", messages.Select(x => $"⚠ {x}"));
            Utils.SendMessage(body, PlayerControl.LocalPlayer.PlayerId, GetString("Message.OptionSanityTitle"));
        }
        catch (Exception e) { Utils.ThrowException(e); }
    }
}
