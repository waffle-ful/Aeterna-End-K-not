// Based on https://github.com/haru03741-glitch/TownOfHost_L Modules/SettingsMenu/PresetPage.cs (GPL-3.0),
// which is based on https://github.com/haru03741-glitch/More-Roles-Plus src/Options/PresetPage.cs (GPL-3.0)
using System;
using EndKnot.Modules;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using TMPro;
using UnityEngine;

namespace EndKnot.SettingsUI;

// 設定画面の「プリセット」の頁。
//   上: 20 枠のプリセット (押すとその枠に切り替える・今の枠は金の縁)
//   下: プリセット エクスプローラー (今の設定をファイルへ書き出す・書き出したファイルを読み込む)
// 読み込みは今の枠の設定を上書きするので、2 回押した時だけ読み込む。押すたびに頁を作り直す (スクロールの位置は残す)
internal static class PresetPage
{
    public static readonly Color TabColor = new(0.45f, 0.85f, 0.6f);

    // 頁の置き場 (ほかの頁と同じ)
    private static readonly Vector3 Center = new(4.41f, -2.2f, -6f);
    private const float W = 6.3f, H = 4.15f;
    private const float Gap = 0.1f, SlotH = 0.42f, FileH = 0.9f, BarH = 0.4f;
    private const int SlotCols = 4, FileCols = 2;

    private static readonly Color SubText = new(0.78f, 0.8f, 0.86f, 1f);
    private static readonly Color Gold = new(1f, 0.82f, 0.2f, 1f);
    private static readonly Color Dim = new(0.42f, 0.42f, 0.45f, 1f);
    private static readonly Color Blue = new(0.4f, 0.75f, 1f), Green = new(0.45f, 0.9f, 0.55f);

    private static GameObject _root;
    private static UiScreen _screen;
    private static IntPtr _menu;
    private static string _status = "";
    private static int _armed = -1;

    public static bool Built => _root && _screen != null && _screen.Alive;

    public static void Forget()
    {
        _screen?.Clear();
        _screen = null;
        if (_root) UnityEngine.Object.Destroy(_root);
        _root = null;
        _menu = IntPtr.Zero;
        _armed = -1;
        _status = "";
    }

    public static void Build(GameOptionsMenu m)
    {
        // 同じ画面を出し直しただけなら、中身だけ新しくする (外でプリセットのファイルが増えているかもしれない)
        if (Built && m && _menu == m.Pointer) { Refresh(true); return; }
        Forget();
        if (!m || !UiAssets.Box) return;
        // 画面の元の中身 (本編の行の一覧・スクロール) は使わない。コントローラーで閉じるボタンだけ残す
        for (int i = 0; i < m.transform.childCount; i++)
        {
            var c = m.transform.GetChild(i);
            if (c.name != "CloseButton") c.gameObject.SetActive(false);
        }

        _root = Widget.Node("Settings_presetpage", m.transform, m.gameObject.layer);
        _root.transform.localPosition = Center;
        _root.AddComponent<UnityEngine.Rendering.SortingGroup>().sortingOrder = 5;
        SettingsCover.Add(_root);
        _menu = m.Pointer;
        _screen = new UiScreen(_root.transform) { MaxWidth = W, BaseOrder = 1 };
        Refresh(true);
    }

    // 外 (ほかの頁・コマンド・取り消し) で値を変えた後: 今の枠の縁を付け直す
    // (見えていない間は何もしない。出す時に Show が付け直す)
    public static void RefreshFromOutside()
    {
        if (Built && _root.activeInHierarchy) Refresh(false);
    }

    // 頁を出した時 (別の頁で値を変えていたかもしれない)
    public static void Show()
    {
        if (Built) Refresh(true);
    }

    private static void Refresh(bool rescan)
    {
        if (!Built) return;
        if (rescan || !OnlinePresetsManager.PresetsLoaded) OnlinePresetsManager.LoadLocalPresets();
        _screen.Set(Page());
    }

    private static ConfigEntry<string>[] SlotNames => new[]
    {
        Main.Preset1, Main.Preset2, Main.Preset3, Main.Preset4, Main.Preset5, Main.Preset6, Main.Preset7, Main.Preset8, Main.Preset9, Main.Preset10,
        Main.Preset11, Main.Preset12, Main.Preset13, Main.Preset14, Main.Preset15, Main.Preset16, Main.Preset17, Main.Preset18, Main.Preset19, Main.Preset20,
    };

    private static string SlotName(int n)
    {
        var entry = SlotNames[n];
        return entry.Value == (string)entry.DefaultValue ? Translator.GetString($"Preset_{n + 1}") : entry.Value;
    }

    private static Widget Page()
    {
        bool edit = SettingsPages.CanEdit();
        float inner = W - 0.2f;
        var col = new Stack(false) { Gap = 0.1f, Width = inner, Align = Align.Start };

        col.Add(new Label(Tr("PresetPage.Slots"), 1.6f) { Bold = true, Color = TabColor, Width = inner, Align = Align.Start });
        float slotW = (inner - Gap * (SlotCols - 1)) / SlotCols;
        var slots = new Stack(false) { Gap = Gap, Width = inner, Align = Align.Start };
        Stack row = null;
        for (int i = 0; i < OptionItem.NumPresets; i++)
        {
            if (i % SlotCols == 0) { row = new Stack(true) { Gap = Gap, Align = Align.Start }; slots.Add(row); }
            row.Add(Slot(i, slotW, edit));
        }

        col.Add(slots);

        col.Add(new Spacer(inner, 0.1f));
        col.Add(new Label(Tr("PresetPage.Explorer"), 1.6f) { Bold = true, Color = TabColor, Width = inner, Align = Align.Start });
        string status = _status.Length > 0 ? _status : OnlinePresetsManager.CachedPresets.Count == 0 ? Tr("PresetPage.NoSaved") : "";
        if (status.Length > 0) col.Add(new Label(status, 1.15f) { Color = SubText, Width = inner, Align = Align.Start });
        col.Add(new Button(Tr("PresetPage.Export"), Export, 1.3f) { Size = new Vector2(inner * 0.5f, BarH), Color = Blue });

        float fileW = (inner - Gap * (FileCols - 1)) / FileCols;
        var files = new Stack(false) { Gap = Gap, Width = inner, Align = Align.Start };
        Stack fileRow = null;
        var list = OnlinePresetsManager.CachedPresets;
        for (int i = 0; i < list.Count; i++)
        {
            if (i % FileCols == 0) { fileRow = new Stack(true) { Gap = Gap, Align = Align.Start }; files.Add(fileRow); }
            fileRow.Add(FileSlot(i, fileW, edit));
        }

        col.Add(files);
        return new ScrollView(new Vector2(W, H), col) { Tag = "presets" };
    }

    // 枠 1 つ (押すとその枠へ切り替える)
    private static Widget Slot(int n, float w, bool edit)
    {
        bool current = n == OptionItem.CurrentPreset;
        var label = new Label(SlotName(n), 1.2f) { Width = w - 0.2f, MinFontSize = 0.7f, Bold = current, Color = current ? Gold : Color.white, TextAlign = TextAlignmentOptions.Center, NoWrap = true };
        return new Button(label, () => SwitchTo(n))
        {
            Size = new Vector2(w, SlotH),
            Padding = new Vector2(0.06f, 0.02f),
            Color = current ? Gold : Dim,
            Fill = current ? new Color(0.16f, 0.13f, 0.04f) : null,
        };
    }

    // 書き出したファイル 1 つ (2 回押すと今の枠へ読み込む)
    private static Widget FileSlot(int n, float w, bool edit)
    {
        var meta = OnlinePresetsManager.CachedPresets[n];
        bool armed = _armed == n;
        string when = DateTimeOffset.FromUnixTimeSeconds((long)meta.created_at).ToLocalTime().ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture);
        float textW = w - 0.2f;
        var text = new Stack(false,
            new Label(meta.name, 1.3f) { Bold = true, Width = textW, MinFontSize = 0.8f, Align = Align.Start },
            new Label(armed ? Tr("PresetPage.LoadAgain") : string.Format(Tr("PresetPage.Info"), when, meta.downloads), 1.0f) { Color = armed ? Gold : SubText, Width = textW, Align = Align.Start }) { Gap = 0f, Width = textW };
        return new Button(text, () => Load(n))
        {
            Size = new Vector2(w, FileH),
            Padding = new Vector2(0.1f, 0.06f),
            Color = armed ? Gold : edit ? Green : Dim,
            Align = Align.Start,
        };
    }

    private static void SwitchTo(int n)
    {
        _armed = -1;
        if (!SettingsPages.CanEdit() || n == OptionItem.CurrentPreset) return;
        GameSettingMenuPatch.LastPresetChange = Utils.TimeStamp;
        Options.Preset.SetValue(n);
        SettingsPages.AfterEdited(MenuOpt.Of(Options.Preset));
        SettingsPages.RefreshAll();
        Refresh(false);
    }

    private static void Export()
    {
        _armed = -1;
        try
        {
            OnlinePresetsManager.ExportToFile();
            _status = Tr("PresetPage.Exported");
        }
        catch (Exception e) { Logger.Exception(e, "PresetPage"); }

        Refresh(true);
    }

    private static void Load(int n)
    {
        if (!SettingsPages.CanEdit()) { _status = Tr("PresetPage.LobbyOnly"); Refresh(false); return; }
        if (_armed != n)
        {
            _armed = n;
            _status = Tr("PresetPage.LoadWarning");
            Refresh(false);
            return;
        }

        _armed = -1;
        try
        {
            if (OnlinePresetsManager.ApplyFromFile(OnlinePresetsManager.CachedPresets[n].id))
            {
                _status = Tr("PresetPage.Loaded");
                SettingsPages.AfterEdited(MenuOpt.Of(Options.Preset));
                SettingsPages.RefreshAll();
            }
        }
        catch (Exception e) { Logger.Exception(e, "PresetPage"); }

        Refresh(false);
    }

    private static string Tr(string key) => Translator.GetString(key);
}
