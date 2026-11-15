using System;
using System.Collections.Generic;

namespace EndKnot.SettingsUI;

// 設定画面が OptionItem を読む時の共通の窓口 (今のモードで出す項目か・説明の字・役職の出現率の読み替え)。
internal static class SettingsCompat
{
    // 見出しの開閉は見ず、今のゲームモードで出す項目か
    public static bool IsShown(this OptionItem o) => !o.IsCurrentlyHidden(checkCollapsedSection: false);

    public static bool Has(string key)
    {
        try
        {
            return Translator.HasTranslation(key, SupportedLangs.English) || Translator.HasTranslation(key, Translator.GetEffectiveLang());
        }
        catch { return false; }
    }

    // 出現率の項目 (StringOptionItem の 5% 刻みの番号) が、番号 index のとき何 % か
    public static int PercentAt(StringOptionItem item, int index)
    {
        int count = item.Selections.Count;
        index = Math.Clamp(index, 0, count - 1);
        switch (count)
        {
            case 2: return index * 100;
            case 3: return index;
            default: return (index + Options.Rates.Length - count) * 5;
        }
    }

    public static string RoleDetail(CustomRoles role)
    {
        string roleName = role.IsVanilla() ? role + "EndKnot" : role.ToString();
        string str = Translator.GetString($"{roleName}InfoLong").FixRoleName(role);
        return $"{role.ToColoredString()}{str}";
    }
}

// ゲームモードの選び。ゲームモードの項目は隠し項目 (行が出ない) なので、ゲームの頁にだけ行として出す
internal sealed class ModeOpt : MenuOpt
{
    public static readonly ModeOpt Instance = new();

    private ModeOpt() : base(Options.GameMode) { }

    public override bool Shown => true;
    public override string Label => Translator.GetString("GameMode");
    public override string PlainLabel => Label;
}
