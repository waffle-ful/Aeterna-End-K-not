// Based on https://github.com/haru03741-glitch/TownOfHost_L Modules/SettingsMenu/SectionPage.cs (GPL-3.0),
// which is based on https://github.com/haru03741-glitch/More-Roles-Plus src/Options/SectionPage.cs (GPL-3.0),
// which is based on https://github.com/Dolly1016/Nebula-Public NebulaPluginNova/Configuration/ConfigurationVariations.cs (GPL-3.0)
using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace EndKnot.SettingsUI;

// 設定画面の「ゲーム」「全般」の頁。真ん中 = (上の段) + 節の見出しと行のスクロール欄・右 = 頁の見出しと節の目次・戻すボタン。
// 行は OptEditor の行 (名前の右の点で初期値から変えた項目が分かる)。
internal sealed class SectionPage
{
    internal sealed class Group
    {
        public string Title;
        public Color Color = Color.white;
        public TextOptionItem Head;   // 見出しの項目 (開閉の状態を持つ)・無ければ null
        public readonly List<MenuOpt> Opts = new();
    }

    // 頁の置き場と大きさ (左の列からの並びを揃える)。右の欄は目次だけなので行の欄を広く取る
    private static readonly Vector3 Center = new(4.41f, -2.2f, -6f);
    private const float W = 6.3f, H = 4.15f;
    private static readonly float ListW = (float)Math.Round(W * 0.65f, 2), SideW = W - ListW - 0.15f;
    internal static float RowW => ListW - 0.24f;
    private const float HeadH = 0.4f, BarH = 0.4f, RowPad = OptEditor.PlatePad;
    // 色の決まっていない節の見出しの色 (順に回す)
    private static readonly Color[] HeadPalette =
    {
        new(0.35f, 0.75f, 1f, 1f), new(1f, 0.72f, 0.3f, 1f), new(0.55f, 0.9f, 0.55f, 1f),
        new(0.85f, 0.55f, 1f, 1f), new(1f, 0.5f, 0.55f, 1f), new(0.4f, 0.9f, 0.85f, 1f),
    };
    private static readonly Color SubText = new(0.72f, 0.75f, 0.8f, 1f);

    private readonly string _tag;
    private readonly Func<string> _title;
    private readonly Color _color;
    private readonly Func<List<Group>> _groups;
    private readonly Func<bool, Widget> _top;   // 節の前に置く物 (編集できるか)。null = 無し
    private readonly Action _onBuild;           // 組む前に呼ぶ (項目の作り直しなど)
    private readonly Func<int> _topKey;         // 上の段の見た目が変わる値 (外で変わったら組み直す)
    private int _builtTopKey;
    public Func<string> HeroSub;                // 頁の見出しの下の字 (null = 無し)
    private readonly OptEditor _editor;

    private sealed class Entry
    {
        public Group Group;
        public Box Head;
        public Label TocLabel;
        public bool Changed;
    }

    private readonly List<Entry> _entries = new();
    private readonly List<MenuOpt> _shownOpts = new(); // 今並べている行 (外で値を変えた後に、出る行が変わったかを比べる)
    private List<Group> _all = new();
    private UiScreen _screen;
    private ScrollView _list;
    private Button _resetBtn, _undoBtn;
    private bool _built;
    private IntPtr _menu;

    public SectionPage(string tag, Func<string> title, Color color, Func<List<Group>> groups, Func<bool, Widget> top = null, Action onBuild = null, Func<int> topKey = null)
    {
        _tag = tag;
        _title = title;
        _color = color;
        _groups = groups;
        _top = top;
        _onBuild = onBuild;
        _topKey = topKey;
        _editor = new OptEditor(OnShownChanged, AfterEdit, CanEdit);
    }

    private static bool CanEdit() => SettingsPages.CanEdit();

    public bool Built => _built && _screen != null && _screen.Alive;

    public void Build(GameOptionsMenu m)
    {
        // 同じ画面を出し直しただけなら、そのまま使う (本編が画面を出すたびに組み立てを呼ぶ)
        if (Built && m && _menu == m.Pointer) return;
        Forget();
        if (!m || !UiAssets.Box) return;
        // タブの元の中身 (本編の行の一覧・スクロール) は使わない。コントローラーで閉じるボタンだけ残す
        for (int i = 0; i < m.transform.childCount; i++)
        {
            var c = m.transform.GetChild(i);
            if (c.name != "CloseButton") c.gameObject.SetActive(false);
        }
        var root = Widget.Node("SettingsUi_" + _tag, m.transform, m.gameObject.layer);
        root.transform.localPosition = Center;
        root.AddComponent<UnityEngine.Rendering.SortingGroup>().sortingOrder = 5;
        SettingsCover.Add(root);
        _screen = new UiScreen(root.transform) { MaxWidth = W, BaseOrder = 1 };
        _menu = m.Pointer;
        _built = true;
        Rebuild();
    }

    public void Forget()
    {
        _list?.Remember();
        _screen?.Clear();
        if (_screen != null && _screen.Root) UnityEngine.Object.Destroy(_screen.Root.gameObject);
        _screen = null;
        _list = null;
        _resetBtn = _undoBtn = null;
        _entries.Clear();
        _shownOpts.Clear();
        _all = new List<Group>();
        _editor.Clear();
        _built = false;
        _menu = IntPtr.Zero;
    }

    public void Rebuild()
    {
        if (_screen == null || !_screen.Alive) return;
        _list?.Remember();
        _entries.Clear();
        _shownOpts.Clear();
        _editor.Clear();
        _onBuild?.Invoke();
        _all = _groups();
        _builtTopKey = _topKey?.Invoke() ?? 0;
        bool edit = CanEdit();

        float rowW = RowW;
        var rows = new Stack(false) { Gap = 0.06f, Width = rowW };
        var watch = new List<MenuOpt>();
        foreach (var g in _all) watch.AddRange(g.Opts);
        _editor.Watch(watch);
        var top = _top?.Invoke(edit);
        if (top != null)
        {
            top.Align = Align.Start;
            rows.Add(top);
        }
        foreach (var g in _all)
        {
            var shown = new List<MenuOpt>();
            int values = 0;
            foreach (var opt in g.Opts)
            {
                if (!opt.Shown) continue;
                shown.Add(opt);
                if (!opt.IsText) values++;
            }
            _shownOpts.AddRange(shown);
            if (values == 0) continue;
            var e = new Entry { Group = g, Changed = AnyChanged(g) };
            // 見出し: 横いっぱいの色の帯 (節ごとに色を変えて、どこからが次の節か一目で分かるように)。前は広めに間を空ける
            if (_entries.Count > 0 || top != null) rows.Add(new Spacer(rowW, 0.16f));
            var accent = g.Color == Color.white ? HeadPalette[_entries.Count % HeadPalette.Length] : g.Color;
            string title = $"{g.Title}  <size=70%><color=#B8C0CC>{string.Format(Translator.GetString("SettingsUI.Count"), values)}</color></size>";
            bool collapsed = g.Head is { CollapsesSection: true };
            if (g.Head != null) title = (collapsed ? "▶ " : "▼ ") + title;
            var headLabel = new Label(title, 1.65f) { Bold = true, Color = Color.Lerp(accent, Color.white, 0.35f), Width = rowW - 0.3f, MinFontSize = 1.1f };
            var headFill = Color.Lerp(accent, new Color(0.08f, 0.09f, 0.11f, 1f), 0.72f);
            if (g.Head != null)
            {
                var toggled = g.Head;
                e.Head = new Button(headLabel, () => { toggled.CollapsesSection = !toggled.CollapsesSection; Rebuild(); })
                {
                    Size = new Vector2(rowW, HeadH + 0.06f),
                    Padding = new Vector2(0.15f, 0.04f),
                    Color = accent,
                    Fill = headFill,
                    Align = Align.Start,
                };
            }
            else
                e.Head = new Box(headLabel)
                {
                    Size = new Vector2(rowW, HeadH + 0.06f),
                    Padding = new Vector2(0.15f, 0.04f),
                    Color = accent,
                    Fill = headFill,
                    Align = Align.Start,
                };
            rows.Add(e.Head);
            if (collapsed) { _entries.Add(e); continue; }
            // 行ごとに地を交互に濃淡 (横に長い行でも目で追いやすく)
            int k = 0;
            foreach (var opt in shown)
            {
                Widget r;
                if (opt.IsText)
                {
                    // 親のある見出し (設定の途中の小見出し): 字だけ
                    var tc = opt.NameColor;
                    r = new Label(opt.Label, OptEditor.NarrowFont) { Bold = true, Width = rowW - RowPad * 2f - opt.Depth * 0.2f, Color = tc == Color.white ? SubText : MenuOpt.Readable(tc) };
                }
                else r = _editor.NarrowRow(opt, edit, rowW - RowPad * 2f, opt.Depth * 0.2f);
                rows.Add(OptEditor.Plated(r, rowW, k++));
            }
            _entries.Add(e);
        }
        _editor.Refresh();
        _list = new ScrollView(new Vector2(ListW, H), rows) { Tag = _tag };
        _screen.Set(new Stack(true, _list, Side(edit)) { Gap = 0.15f, Align = Align.Start });
    }

    private Widget Side(bool edit)
    {
        var col = new Stack(false) { Gap = 0.06f, Width = SideW };
        col.Add(Hero(SideW, _color, _title(), HeroSub?.Invoke() ?? ""));
        // 目次: 押すとその節の見出しへ (節の中に初期値から変えた項目があれば点)
        var toc = new Stack(false) { Gap = 0.05f, Width = SideW - 0.2f };
        for (int i = 0; i < _entries.Count; i++)
        {
            var e = _entries[i];
            int n = i;
            e.TocLabel = new Label(TocText(e), 1.3f) { Width = SideW - 0.44f, NoWrap = true, Bold = true, MinFontSize = 0.9f };
            toc.Add(new Button(e.TocLabel, () => Jump(n)) { Size = new Vector2(SideW - 0.2f, 0.34f), Padding = new Vector2(0.1f, 0.02f), Color = new Color(0.42f, 0.42f, 0.45f, 1f), Hollow = true });
        }
        float tocH = H - HeroH - 0.06f - (edit ? BarH + 0.06f : 0f);
        col.Add(new ScrollView(new Vector2(SideW, tocH), toc) { Tag = _tag + ":toc" });
        if (edit)
        {
            float bw = (SideW - 0.05f) / 2f;
            _resetBtn = new Button(new Label(Translator.GetString("SettingsUI.ResetAll"), 1.2f) { Width = bw - 0.3f, MinFontSize = 0.4f, NoWrap = true, TextAlign = TextAlignmentOptions.Center }, ResetAll) { Size = new Vector2(bw, BarH), Color = ResetColor() };
            _undoBtn = new Button(Translator.GetString("SettingsUI.Undo"), Undo, 1.2f) { Size = new Vector2(bw, BarH), Color = UndoColor };
            col.Add(new Stack(true, _resetBtn, _undoBtn) { Gap = 0.05f });
        }
        return col;
    }

    // ---- 右の欄の見出し: 区分の色の地に大きな名前 ----

    internal const float HeroH = 0.98f;

    // tools = 見出しの左上に置く操作のボタン (役職の欄の前後・閉じる)
    internal static Widget Hero(float width, Color c, string title, string sub, Widget tools = null)
    {
        var hero = new Layers(width, HeroH);
        var fill = Color.Lerp(Color.black, c, 0.2f);
        fill.a = 0.55f;
        hero.Add(new Box(null) { Size = new Vector2(width, HeroH), Color = Color.Lerp(c, Color.black, 0.5f), Fill = fill, Hollow = true }, 0f, 0f);
        float textW = width - 0.3f;
        var text = new Stack(false, new Label(title, 1.9f) { Bold = true, Color = c, Width = textW, MinFontSize = 1.2f, Align = Align.Start }) { Gap = 0f, Width = textW };
        if (!string.IsNullOrEmpty(sub)) text.Add(new Label(sub, 1.05f) { Color = SubText, Width = textW, Align = Align.Start });
        hero.Add(text, 0.14f, 0.06f, false, true);
        if (tools != null) hero.Add(tools, 0.1f, 0.08f);
        return hero;
    }

    private static string TocText(Entry e) => e.Changed ? e.Group.Title + OptEditor.ChangedMark : e.Group.Title;

    private static bool AnyChanged(Group g)
    {
        foreach (var opt in g.Opts)
            if (opt.Changed) return true;
        return false;
    }

    private Color ResetColor()
    {
        foreach (var g in _all)
            if (AnyChanged(g)) return OptEditor.ChangedColor;
        return OptEditor.Dim;
    }

    private static Color UndoColor => OptHistory.Any ? Color.white : OptEditor.Dim;

    // 見出しを欄の上端へ
    private void Jump(int n)
    {
        if (n < 0 || n >= _entries.Count || _list == null) return;
        var head = _entries[n].Head?.BackRenderer;
        var s = _list.Scroller;
        if (!head || !s) return;
        float hy = s.Inner.InverseTransformPoint(head.transform.position).y;
        _list.ScrollTo(_list.Size.y / 2f - hy - HeadH / 2f - 0.04f);
    }

    private void ResetAll()
    {
        if (!CanEdit()) return;
        bool any = false;
        OptHistory.BeginGroup();
        OptEditor.Busy++;
        try
        {
            foreach (var g in _all)
                foreach (var opt in g.Opts)
                {
                    int before = opt.Index;
                    if (!opt.ResetToDefault()) continue;
                    OptHistory.Push(opt, before);
                    OptEditor.AfterStep(opt);
                    any = true;
                }
        }
        finally
        {
            OptEditor.Busy--;
            OptHistory.EndGroup();
        }
        if (any) SettingsPages.RefreshAll();
    }

    private static void Undo()
    {
        if (!OptHistory.Any || !CanEdit()) return;
        OptEditor.Busy++;
        try { OptHistory.Undo(); }
        finally { OptEditor.Busy--; }
        SettingsPages.RefreshAll();
    }

    private void OnShownChanged()
    {
        Rebuild();
        SettingsMenuPatch.RefreshCategoryLabels();
    }

    private void AfterEdit(MenuOpt _)
    {
        RefreshMarks();
        SettingsMenuPatch.RefreshCategoryLabels();
    }

    // 値を変えた後: 目次の点と、戻すボタンの色
    public void RefreshMarks()
    {
        if (!Built) return;
        foreach (var e in _entries)
        {
            e.Changed = AnyChanged(e.Group);
            var t = e.TocLabel?.Built;
            if (!t) continue;
            string s = TocText(e);
            if (s != e.TocLabel.Content) { e.TocLabel.Content = s; t.text = s; }
        }
        _resetBtn?.SetColor(ResetColor());
        _undoBtn?.SetColor(UndoColor);
    }

    // 外 (プリセット・コマンド・検索の結果の行・取り消し) で値を変えた後。出る行が変わっていれば組み直す
    public void RefreshFromOutside()
    {
        if (!Built) return;
        // 自分の行を押して値を書き換えている最中は、その後始末が行う
        if (OptEditor.Busy > 0) return;
        if (_topKey != null && _topKey() != _builtTopKey) { Rebuild(); return; }
        int k = 0;
        bool same = true;
        foreach (var g in _all)
        {
            foreach (var opt in g.Opts)
            {
                if (!opt.Shown) continue;
                if (k >= _shownOpts.Count || _shownOpts[k] != opt) { same = false; break; }
                k++;
            }
            if (!same) break;
        }
        if (!same || k != _shownOpts.Count) { Rebuild(); return; }
        _editor.Refresh();
        RefreshMarks();
    }
}
