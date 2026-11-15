// Based on https://github.com/haru03741-glitch/TownOfHost_L Modules/SettingsMenu/SettingsPages.cs (GPL-3.0),
// which is based on https://github.com/haru03741-glitch/More-Roles-Plus src/Options/SettingsPages.cs (GPL-3.0),
// which is based on https://github.com/Dolly1016/Nebula-Public NebulaPluginNova/Configuration/ConfigurationVariations.cs (GPL-3.0)
using System;
using System.Collections.Generic;
using AmongUs.GameOptions;
using HarmonyLib;
using UnityEngine;

namespace EndKnot.SettingsUI;

// 設定画面の「ゲーム」(本編の設定と EK のゲームのルール) と「全般」(参加の制限・自動の操作など) の頁。
// 本編の設定か EK の設定かでは分けず、話題で分ける
internal static class SettingsPages
{
    public static readonly SectionPage General = new("generalpage", () => Translator.GetString("MenuCat.SystemSettings"),
        SettingsMenuPatch.CatColor(TabGroup.SystemSettings), GeneralGroups);

    public static readonly SectionPage Game = new("gamepage", () => Translator.GetString("MenuCat.Game"),
        SettingsMenuPatch.CatColor(null), GameGroups, MapTop, VanillaGame.Rebuild, TopKey) { HeroSub = MapName };

    public static bool AnyBuilt => General.Built || Game.Built || RolePage.Built;

    // ゲームの頁を作った本編の画面 (マップの絵を借りる)
    public static GameOptionsMenu GameTab;

    // 設定画面を閉じる・開き直す時: 頁と、頁が持つ物 (札・打ち込み欄・取り消しの履歴) を全部忘れる
    public static void Forget()
    {
        OptEditor.EndInput(false);
        GameTab = null;
        General.Forget();
        Game.Forget();
        RolePage.Forget();
        PresetPage.Forget();
        Tooltip.Hide();
        OptHistory.Clear();
        SettingsCover.Reset();
    }

    public static void RefreshMarks()
    {
        General.RefreshMarks();
        Game.RefreshMarks();
    }

    // 外 (プリセット・コマンド・取り消し) で値を変えた後
    public static void RefreshFromOutside()
    {
        RefreshOtherPages();
        RolePage.RefreshFromOutside();
        PresetPage.RefreshFromOutside();
    }

    // 役職の頁 (RoleBrowser) の行で値を変えた後: 役職の頁以外の頁
    public static void RefreshOtherPages()
    {
        General.RefreshFromOutside();
        Game.RefreshFromOutside();
    }

    // 値をまとめて書き換える間 (設定の文字列の読み込みなど) は値ごとに頁を書き直さず、終わった後に 1 回だけ書き直す
    private static int _batch;
    public static bool Batching => _batch > 0;

    public static void Batch(System.Action write)
    {
        _batch++;
        try { write(); }
        finally { _batch--; }
        if (_batch == 0) RefreshAllSafe();
    }

    // OptionItem の値が書き換わった (プリセットの切り替えのように何千回も続く時があるので、描くのは 1 フレームに 1 回)
    private static bool _dirty;

    public static void MarkDirty() => _dirty = true;

    // 値を書いた後の保存の予約。書く手が止まって 1 秒たったら 1 回だけ保存し、画面を閉じる時は待たずに保存する
    private const float SaveDelay = 1f;
    private static float _saveAt = -1f;
    public static void RequestSave() => _saveAt = Time.realtimeSinceStartup + SaveDelay;

    public static void FlushSave(bool now)
    {
        if (_saveAt < 0f || (!now && Time.realtimeSinceStartup < _saveAt)) return;
        _saveAt = -1f;
        try { EndKnot.Modules.OptionSaver.Save(); }
        catch (Exception e) { Utils.ThrowException(e); }
    }

    internal static void ClearDirty() => _dirty = false;

    // ロビーを抜ける・アプリを閉じる時: 予約中の保存と、本編の設定の保存を待たずに済ませる (Close を通らずに画面が消えた時の分)
    public static void FlushOnLeave()
    {
        FlushSave(true);
        if (VanillaGame.Dirty) VanillaGame.OnMenuClosed(false);
    }

    internal static void FlushDirty()
    {
        if (!_dirty || OptEditor.Busy > 0 || Batching) return;
        _dirty = false;
        RefreshAllSafe();
    }

    public static void RefreshAll()
    {
        RefreshFromOutside();
        SettingsMenuPatch.RefreshCategoryLabels();
    }

    // 画面の外 (OptionItem の値の変更) から呼ぶ形。画面の側の例外を、値を変えた側へ投げない
    public static void RefreshAllSafe()
    {
        if (!AnyBuilt) return;
        try { RefreshAll(); }
        catch (Exception ex) { Logger.Exception(ex, "SettingsPages"); }
    }

    // 頁の行で値を変えた後の、頁の外の後始末。プリセットとゲームモードは出る設定がまるごと変わるので、他の頁も書き直す
    // (押した行を持つ頁は、押した後の後始末が組み直す)
    public static void AfterEdited(MenuOpt opt)
    {
        var item = opt.Item;
        if (item == null) return;
        if (item is PresetOptionItem) SettingsMenuPatch.NotifyPresetChanged();
    }

    // ---- EK の設定の節: 見出し (親の無い TextOptionItem) から次の見出しまで ----

    // 区分 (複数なら続けて) の設定を節に分ける。見出しの前の設定の節は見出し無し
    internal static List<SectionPage.Group> SectionsOf(params TabGroup[] tabs)
    {
        var list = new List<SectionPage.Group>();
        foreach (var tab in tabs)
        {
            SectionPage.Group cur = null;
            foreach (var option in OptionItem.AllOptions)
            {
                if (option.Tab != tab || option is PresetOptionItem || option == Options.GameMode) continue;
                if (option is TextOptionItem toi && option.Parent == null)
                {
                    cur = new SectionPage.Group { Title = SectionTitle(option), Color = option.NameColor, Head = toi };
                    list.Add(cur);
                    continue;
                }
                if (cur == null)
                {
                    cur = new SectionPage.Group { Title = Translator.GetString("MenuCat." + tab) };
                    list.Add(cur);
                }
                cur.Opts.Add(MenuOpt.Of(option));
            }
        }
        return list;
    }

    internal static List<SectionPage.Group> GeneralGroups() => SectionsOf(TabGroup.SystemSettings);

    private static string SectionTitle(OptionItem option) => option.GetName(disableColor: true).RemoveHtmlTags().Trim('★', ' ');

    // ---- ゲーム: ゲームモードの節 → 本編の設定の節 (本編の並びのまま) → EK のゲームのルールの節 ----

    internal static List<SectionPage.Group> GameGroups()
    {
        var list = new List<SectionPage.Group>();
        var mode = new SectionPage.Group { Title = Translator.GetString("GameMode") };
        mode.Opts.Add(ModeOpt.Instance);
        list.Add(mode);
        foreach (var sec in VanillaGame.All)
        {
            var g = new SectionPage.Group { Title = sec.Title };
            g.Opts.AddRange(sec.Opts);
            if (g.Opts.Count > 0) list.Add(g);
        }
        list.AddRange(SectionsOf(TabGroup.GameSettings, TabGroup.TaskSettings));
        return list;
    }

    // ---- ゲームの頁の上の段: マップ ----

    private static readonly Color Accent = new(0.5f, 0.85f, 0.75f, 1f);
    private const float MapBtn = 0.62f;

    private static IGameOptions GameOpts => GameOptionsManager.Instance?.CurrentGameOptions;

    // 上の段の見た目が変わる値 (外で変えた時に組み直すかを決める)
    private static int TopKey() => GameOpts != null ? GameOpts.MapId : -1;

    private static Widget MapTop(bool edit)
    {
        float rowW = SectionPage.RowW;
        // 本編の OnEnable を差し替えているので GameSettingMenu.Instance は当てにせず、頁を作った画面のマップの絵を使う
        var picker = GameTab ? GameTab.MapPicker : null;
        var icons = picker ? picker.AllMapIcons : null;
        if (icons == null || icons.Count == 0) return null;
        int cur = GameOpts != null ? GameOpts.MapId : -1;
        // マップは名前の札を付けず、行の幅いっぱいに並べる (選んだマップの名前は右の見出しに出す)
        var line = new Stack(true) { Gap = 0.1f };
        foreach (var icon in icons)
        {
            if (icon == null || !icon.MapIcon) continue;
            int id = (int)icon.Name;
            bool on = id == cur;
            line.Add(new Button(new Picture(icon.MapIcon, MapBtn - 0.1f, MapBtn - 0.1f) { Fit = true }, () => SelectMap(id))
            {
                Size = new Vector2(MapBtn, MapBtn),
                Padding = Vector2.zero,
                Color = on ? Accent : OptEditor.Dim,
                Fill = on ? Color.Lerp(Color.black, Accent, 0.35f) : null,
            });
        }
        return new Stack(true, line) { Width = rowW };
    }

    // 選んでいるマップの名前
    private static string MapName()
    {
        var o = GameOpts;
        if (o == null || !TranslationController.Instance) return "";
        StringNames? n = o.MapId switch
        {
            0 => StringNames.MapNameSkeld,
            1 => StringNames.MapNameMira,
            2 => StringNames.MapNamePolus,
            4 => StringNames.MapNameAirship,
            5 => StringNames.MapNameFungle,
            _ => null,
        };
        return n.HasValue ? TranslationController.Instance.GetString(n.Value) : "";
    }

    // 値を変えられるのは、ホストがロビーにいる間だけ (画面を開いたまま試合が始まった時に書かせない)
    public static bool CanEdit() => AmongUsClient.Instance && AmongUsClient.Instance.AmHost && GameStates.IsLobby && !GameStates.IsInGame;

    // 本編のマップ選びと同じ設定の欄へ書く (同期と保存は画面を閉じた時に本編の仕組みで)
    private static void SelectMap(int id)
    {
        var o = GameOpts;
        if (o == null || !CanEdit() || o.MapId == id) return;
        o.SetByte(ByteOptionNames.MapId, (byte)id);
        VanillaGame.Dirty = true;
        RefreshAll();
    }
}

// 頁の毎フレームの処理: 打ち込み欄の入力と、本編の窓が開いている間の頁の隠し。頁が無い間は何もしない
[HarmonyPatch(typeof(HudManager))]
internal static class SettingsPagesTick
{
    [HarmonyPatch(nameof(HudManager.Update)), HarmonyPostfix]
    public static void Postfix()
    {
        SettingsPages.FlushSave(false);
        if (!SettingsPages.AnyBuilt)
        {
            SettingsPages.ClearDirty();
            // 画面が Close を通らずに消えた (頁の部品ごと壊れた): 本編の設定の保存と同期をここで回す
            if (VanillaGame.Dirty && SettingsPages.CanEdit()) VanillaGame.OnMenuClosed();
            return;
        }
        SettingsPages.FlushDirty();
        TextField.Tick();
        RolePage.Tick();
        SettingsCover.Tick();
    }
}
