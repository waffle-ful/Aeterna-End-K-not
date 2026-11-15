// Based on https://github.com/haru03741-glitch/TownOfHost_L Modules/SettingsMenu/RoleBrowser.cs (GPL-3.0),
// which is based on https://github.com/haru03741-glitch/More-Roles-Plus src/Options/RoleBrowser.cs (GPL-3.0),
// which is based on https://github.com/Dolly1016/Nebula-Public NebulaPluginNova/Configuration/NebulaSettingMenu.cs (GPL-3.0)
using System;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;

namespace EndKnot.SettingsUI;

// 役職の欄 1 つ分。役職 (出現率の項目と、その下に連なる項目) か、役職でない設定のまとまり (見出しから次の見出しまで)。
internal sealed class RoleEntry
{
    public TabGroup Tab;
    public TextOptionItem Section;   // 見出しの項目 (開閉の状態を持つ)・見出しの無い所は null
    public string SectionTitle;
    public CustomRoles? Role;
    public OptionItem Root;          // 役職の出現率の項目 (役職でなければ null)
    public string Title = "";
    public string Blurb = "";        // 1 行の説明
    public Color Color = Color.white;
    public readonly List<MenuOpt> Opts = new();   // 先頭から順に行になる項目 (役職の欄では先頭が出現率)
    public MenuOpt Chance, Count;

    public bool IsRole => Chance != null;

    // 一覧に出すか。役職は今のモードで出す物、まとまりは出る行が 1 つでもある物
    public bool Listed => IsRole ? MenuOpt.IsEnabled(Root) : Opts.Exists(o => !o.IsText && o.Shown);

    // 出る役職か (出現率が 0 でなく、人数も 0 でない)
    public bool On => Chance != null && ChanceValue > 0 && (Count == null || Count.Item.GetInt() > 0);

    public int ChanceValue => Chance == null ? 0 : (int)(Chance.ValueAt(Chance.Index) ?? 0);

    public string Values => Count != null ? $"×{Count.Item.GetInt()}  {ChanceValue}%" : $"{ChanceValue}%";
}

// 役職の出現率の行。名前は役職名ではなく「出現率」にする
internal class ChanceOpt : MenuOpt
{
    public ChanceOpt(OptionItem item) : base(item) { }
    public override string Label => Translator.GetString("RoleBrowser.Chance");
    public override string PlainLabel => Label;

    public override double? ValueAt(int index) => Item is StringOptionItem s ? SettingsCompat.PercentAt(s, index) : base.ValueAt(index);
}

// 属性の出現率: 出る/出ないの項目と、出る時の確率 (AdditionRolesSpawnRate) を 1 つの「出現率」にまとめる。0 = 出ない
internal sealed class AddonChanceOpt : ChanceOpt
{
    private readonly IntegerOptionItem _rate;

    public AddonChanceOpt(OptionItem root, IntegerOptionItem rate) : base(root) { _rate = rate; }

    public override int Index => Item.GetValue() == 0 ? 0 : _rate.CurrentValue;
    public override int Count => _rate.Rule.GetNearestIndex(_rate.Rule.MaxValue) + 1;
    public override bool WrapsAround => false;
    public override double? ValueAt(int index) => _rate.Rule.GetValueByIndex(index);
    public override string Display() => $"{ValueAt(Index)}%";

    protected override void Write(int index)
    {
        if (index == 0)
        {
            Item.SetValue(0, doSave: false, doSync: false);
            SettingsPages.RequestSave();
            return;
        }

        _rate.SetValue(index, doSave: false, doSync: false);
        Item.SetValue(1, doSave: false, doSync: false);
        SettingsPages.RequestSave();
    }
}

// 区分 (TabGroup) ごとの役職の欄の一覧。構造は変わらないので 1 度だけ組む (画面を閉じる時に捨てる)。
// 「古い役職」タブの役職は、元の区分の欄の末尾に並べる
internal static class RoleCatalog
{
    public static readonly TabGroup[] Tabs =
    {
        TabGroup.ImpostorRoles, TabGroup.CrewmateRoles, TabGroup.NeutralRoles, TabGroup.CovenRoles,
        TabGroup.JackalRoles, TabGroup.Combinations, TabGroup.Addons, TabGroup.OtherRoles,
    };

    private static readonly Dictionary<TabGroup, List<RoleEntry>> Cache = new();

    public static void Clear() => Cache.Clear();

    public static bool IsRoleTab(TabGroup tab) => Array.IndexOf(Tabs, tab) >= 0;

    public static List<RoleEntry> Of(TabGroup tab)
    {
        if (!Cache.TryGetValue(tab, out var list)) Cache[tab] = list = Build(tab);
        return list;
    }

    private static List<RoleEntry> Build(TabGroup tab)
    {
        var roleOf = new Dictionary<OptionItem, CustomRoles>();
        foreach (var kv in Options.CustomRoleSpawnChances) roleOf[kv.Value] = kv.Key;

        var list = new List<RoleEntry>();
        RoleEntry group = null;
        TextOptionItem section = null;
        string sectionTitle = null;

        void Add(OptionItem o)
        {
            if (roleOf.TryGetValue(o, out var role))
            {
                group = null;
                var re = RoleOf(o, role, tab);
                re.Section = section;
                re.SectionTitle = sectionTitle;
                list.Add(re);
                return;
            }
            if (o is TextOptionItem toi)
            {
                section = toi;
                sectionTitle = toi.GetName(disableColor: true).RemoveHtmlTags().Trim('★', ' ');
                group = null;
                return;
            }
            // 役職でない親の無い項目: 見出し (IsHeader の項目) で新しいまとまりを始める
            if (group == null || o.IsHeader)
            {
                var color = o.NameColor;
                group = new RoleEntry
                {
                    Tab = tab,
                    Section = section,
                    SectionTitle = sectionTitle,
                    Title = (o.IsHeader ? o.GetName(disableColor: true) : Translator.GetString("MenuCat." + tab)).RemoveHtmlTags(),
                    Blurb = "",
                    Color = color == Color.white ? SettingsMenuPatch.CatColor(tab) : color,
                };
                list.Add(group);
            }
            AddTree(group, o);
        }

        foreach (var o in OptionItem.AllOptions)
        {
            if (o.Tab != tab || o.Parent != null) continue;
            Add(o);
        }

        list.RemoveAll(e => !e.IsRole && e.Opts.Count == 0);
        return list;
    }

    private static RoleEntry RoleOf(OptionItem root, CustomRoles role, TabGroup tab)
    {
        var e = new RoleEntry
        {
            Tab = tab,
            Role = role,
            Root = root,
            Title = root.GetName(disableColor: true).RemoveHtmlTags(),
            Blurb = Blurb(role),
            Color = root.NameColor,
        };
        IntegerOptionItem rate = null;
        if (Options.CustomAdtRoleSpawnRate.TryGetValue(role, out var adtRate) && adtRate.IsShown()) rate = adtRate;
        e.Chance = rate != null ? new AddonChanceOpt(root, rate) : new ChanceOpt(root);
        e.Opts.Add(e.Chance);
        foreach (var child in root.Children)
        {
            if (child == rate) continue;
            AddTree(e, child);
        }
        if (Options.CustomRoleCounts.TryGetValue(role, out var count)) e.Count = MenuOpt.Of(count);
        return e;
    }

    // 役職の説明の 1 行目 (キャッチ文)
    private static string Blurb(CustomRoles role)
    {
        string roleName = role.IsVanilla() ? role + "EndKnot" : role.ToString();
        string key = roleName + "InfoLong";
        if (!SettingsCompat.Has(key)) return "";
        string[] lines = Translator.GetString(key).FixRoleName(role).Split('\n');
        return lines.Length == 0 ? "" : lines[0].RemoveHtmlTags().Trim();
    }

    private static void AddTree(RoleEntry e, OptionItem o)
    {
        e.Opts.Add(MenuOpt.Of(o));
        foreach (var child in o.Children) AddTree(e, child);
    }
}

// 役職の一覧と細かい欄。設定画面の役職の区分 (インポスター・クルー・第三陣営・カヴン・ジャッカル・コンビネーション・属性・幽霊) と検索の結果が 1 つずつ持つ。
// 一覧 = 左に役職のカード (縁の色 = 役職の色・出ない役職は暗く)・右にカードの要約か編集の欄。カードを押すと右の欄で編集する。
// 検索の間 = 語に当たる設定を、まとまりごとの行と役職のカードで並べる (カードを押すと細かい頁)。
// 値を変えられるのは CanEdit が真の間だけ (それ以外は値の文だけ出す)。
internal sealed class RoleBrowser
{
    private static readonly Color Gray = new(0.42f, 0.42f, 0.45f, 1f);
    private static readonly Color SubText = new(0.78f, 0.8f, 0.86f, 1f);
    private static readonly Color Highlight = new(1f, 0.88f, 0.4f, 1f);
    private const float ChipH = 0.36f;
    private const float CardGap = 0.07f;
    private const int SortCount = 3;   // 0 = 標準・1 = 出現率の高い順・2 = 名前
    private const int SummaryLines = 10;

    private readonly float W, H, ListW, SideW, CardW;
    private readonly string _tag;
    private readonly OptEditor _editor;

    // 絞り込みと並べ替えの札を一覧の外 (設定画面の左の列の下) に出す置き場 (null = 一覧の上)
    public UiScreen ChipScreen;

    // 開き直しても残す
    private TabGroup _team = TabGroup.ImpostorRoles;
    private bool _enabledOnly;
    private int _sort;
    private int _bulkChance = 50, _bulkCount = 1;

    private UiScreen _screen;
    // 開いている物 (null = 無し)。一覧では右の欄で編集している物 (一覧はそのまま)、検索の結果から開いた時は細かい頁
    private RoleEntry _open;
    private RoleEntry _hover;  // 右に要約を出している物
    private Holder _side;
    private float _sideH;
    private ScrollView _list, _sideList;
    private readonly List<(RoleEntry e, Button card, Label values, Label name)> _cards = new();
    private readonly List<RoleEntry> _listed = new();   // 一覧に並べている役職の欄 (外で値が変わった時に並びが変わったかを比べる)
    private Button _resetBtn, _undoBtn;
    private Slider _slider;
    private MenuOpt _slideOpt;
    private float _slideAt;
    private bool _pendingSide;   // つまみを引いている間に出る行が変わった (離したら右の欄を作り直す)
    private bool _sideDirty;     // 当てた物が変わった (次の Tick で右の欄を書き直す)
    private float _sideAt;       // 右の欄を書き直してよい時刻 (カードの上を滑らせている間は書き直さない)
    private RoleEntry _sideShown; // 右の欄に今出ている要約の役職
    private bool _sideByHover;   // 書き直しの予約が当てた/外れたから来た (同じ物に戻っただけなら書き直さない)
    private string _query = "";
    private string[] _tokens = Array.Empty<string>();

    // 検索の結果の件数
    public int HitCount { get; private set; }

    public RoleBrowser(float width, float height, string tag)
    {
        W = width;
        H = height;
        ListW = (float)Math.Round(width * 0.56f, 2);
        SideW = width - ListW - 0.15f;
        CardW = ListW - 0.2f;
        _tag = tag;
        _editor = new OptEditor(OnShownChanged, AfterEdit, CanEdit);
    }

    private static bool CanEdit() => SettingsPages.CanEdit();

    public bool Editable => CanEdit();
    public bool Searching => _tokens.Length > 0;

    // 画面に付ける (前の画面の物は捨てる)。新しく開いた時は 1 枚目から
    public void Attach(UiScreen screen, TabGroup team, string query)
    {
        _screen = screen;
        _team = team;
        SetQueryText(query);
        _open = null;
        _hover = null;
        Rebuild();
    }

    public void Detach()
    {
        _screen?.Clear();
        _screen = null;
        ChipScreen?.Clear();
        if (ChipScreen != null && ChipScreen.Root) UnityEngine.Object.Destroy(ChipScreen.Root.gameObject);
        ChipScreen = null;
        _side = null;
        _list = null;
        _sideList = null;
        _slider = null;
        _slideOpt = null;
        _resetBtn = _undoBtn = null;
        _cards.Clear();
        _listed.Clear();
        _editor.Clear();
        _open = _hover = null;
        _tokens = Array.Empty<string>();
        _query = "";
        _pendingSide = false;
    }

    private void SetQueryText(string q)
    {
        _query = q ?? "";
        _tokens = SettingsSearch.Tokens(_query);
    }

    // 区分と検索の語を決めて組み直す (検索の語を打ち進めた時は開いている細かい頁を閉じない)
    public void Show(TabGroup team, string query)
    {
        bool was = Searching;
        bool teamChanged = team != _team;
        _team = team;
        SetQueryText(query);
        if (teamChanged || Searching != was)
        {
            _open = null;
            _hover = null;
        }
        Rebuild();
    }

    // ---- 組み立て ----

    public void Rebuild()
    {
        if (_screen == null || !_screen.Alive) return;
        _list?.Remember();
        _sideList?.Remember();
        _sideList = null;
        _cards.Clear();
        _listed.Clear();
        _editor.Clear();
        _side = null;
        _list = null;
        _slider = null;
        _resetBtn = _undoBtn = null;
        _pendingSide = false;
        ChipScreen?.Clear();
        _screen.Set(_open != null && !SideEdit ? DetailPage(_open) : Searching ? SearchPage() : ListPage());
    }

    // 一覧 = 役職を押すと右の欄で編集する (頁を切り替えない)。検索の結果から開いた時は細かい頁
    private bool SideEdit => !Searching;

    // 項目の出入り (ほかの項目の値で出る行) が変わった: 右の欄で編集中なら右の欄だけ作り直す
    private void OnShownChanged()
    {
        if (SideEdit && _open != null && _side != null) RefreshSide();
        else Rebuild();
    }

    private static string Tr(string key) => Translator.GetString(key);

    private Widget ListPage()
    {
        var cards = new Stack(false) { Gap = CardGap, Width = CardW };
        float half = (CardW - CardGap) / 2f;
        Stack line = null;
        int n = 0;
        bool headers = _sort == 0 && !_enabledOnly;
        TextOptionItem lastSection = null;
        foreach (var e in Visible())
        {
            if (headers && e.Section != null && e.Section != lastSection)
            {
                lastSection = e.Section;
                cards.Add(SectionHeader(e, CardW));
                line = null;
                n = 0;
            }
            _listed.Add(e);
            if (headers && e.Section is { CollapsesSection: true }) continue;
            if (n % 2 == 0)
            {
                line = new Stack(true) { Gap = CardGap, Align = Align.Start };
                cards.Add(line);
            }
            line.Add(Card(e, half, true));
            n++;
        }
        if (_listed.Count == 0) cards.Add(new Label(Tr("RoleBrowser.NoMatch"), 1.6f) { Color = SubText, Width = CardW, TextAlign = TextAlignmentOptions.Center });

        bool outside = ChipScreen != null && ChipScreen.Alive;
        if (outside) ChipScreen.Set(Chips(ChipScreen.MaxWidth));
        float listH = outside ? H : H - ChipH * 2f - 0.17f;
        _list = new ScrollView(new Vector2(ListW, listH), cards) { Tag = _tag + ":" + _team + (_enabledOnly ? "+" : "") + _sort };
        _sideH = listH;
        _side = new Holder(new Vector2(SideW, listH), SideContent());
        var body = new Stack(true, _list, _side) { Gap = 0.15f, Align = Align.Start };
        if (outside) return body;
        return new Stack(false, Chips(W), body) { Gap = 0.12f, Width = W };
    }

    // 絞り込みと並べ替えの札 (幅いっぱいの札を縦に並べる)
    private Widget Chips(float width)
    {
        var col = new Stack(false) { Gap = 0.05f, Width = width, Align = Align.Start };
        col.Add(Chip(Tr("RoleBrowser.EnabledOnly"), new Color(0.6f, 1f, 0.6f), _enabledOnly, width, () => { _enabledOnly = !_enabledOnly; _open = null; Rebuild(); }));
        col.Add(Chip(Tr("RoleBrowser.Sort" + _sort), new Color(0.78f, 0.78f, 0.84f), false, width, () => { _sort = (_sort + 1) % SortCount; Rebuild(); }));
        return col;
    }

    private static Button Chip(string text, Color c, bool on, float width, Action click)
    {
        var label = new Label(text, 1.15f) { TextAlign = TextAlignmentOptions.Center, Bold = true, Color = on ? new Color(0.08f, 0.08f, 0.1f) : c, Width = width - 0.1f, MinFontSize = 0.75f };
        var b = new Button(label, click) { Size = new Vector2(width, ChipH * 0.9f), MinSize = Vector2.zero, Padding = new Vector2(0.04f, 0.02f), Color = c };
        if (on) b.Fill = c;
        return b;
    }

    // 見出しの帯 (押すと、その見出しの下の役職を畳む・開く。畳んだ状態は設定画面の本編版と共有)
    private Widget SectionHeader(RoleEntry e, float width)
    {
        var section = e.Section;
        var c = SettingsMenuPatch.CatColor(e.Tab);
        string mark = section.CollapsesSection ? "▶ " : "▼ ";
        var label = new Label(mark + e.SectionTitle, 1.35f) { Bold = true, Color = MenuOpt.Readable(c), Width = width - 0.3f, MinFontSize = 0.9f, Align = Align.Start };
        return new Button(label, () =>
        {
            section.CollapsesSection = !section.CollapsesSection;
            _open = null;
            Rebuild();
        })
        { Size = new Vector2(width, 0.36f), MinSize = Vector2.zero, Padding = new Vector2(0.1f, 0.02f), Color = c, Hollow = true, Align = Align.Start };
    }

    private List<RoleEntry> Visible()
    {
        var list = new List<RoleEntry>();
        foreach (var e in RoleCatalog.Of(_team))
        {
            if (!e.Listed) continue;
            if (_enabledOnly && !(e.IsRole && e.On)) continue;
            list.Add(e);
        }
        if (_sort == 0 || list.Count < 2) return list;
        var order = new Dictionary<RoleEntry, int>();
        for (int i = 0; i < list.Count; i++) order[list[i]] = i;
        list.Sort((x, y) =>
        {
            int c = 0;
            if (_sort == 1) c = (y.IsRole ? y.ChanceValue : -1).CompareTo(x.IsRole ? x.ChanceValue : -1);
            else if (_sort == 2) c = string.CompareOrdinal(SettingsSearch.Normalize(x.Title), SettingsSearch.Normalize(y.Title));
            return c != 0 ? c : order[x].CompareTo(order[y]);
        });
        return list;
    }

    private static Color CardColor(RoleEntry e) => e.IsRole && !e.On ? Color.Lerp(e.Color, Gray, 0.55f) : e.Color;
    private static Color NameColor(RoleEntry e) => e.IsRole && !e.On ? Color.Lerp(e.Color, Gray, 0.55f) : Color.Lerp(e.Color, Color.white, 0.2f);

    private static string CardValues(RoleEntry e) =>
        e.IsRole ? e.Values : string.Format(Tr("SettingsUI.Count"), CountShown(e));

    private static int CountShown(RoleEntry e)
    {
        int n = 0;
        foreach (var o in e.Opts)
            if (!o.IsText && o.Shown) n++;
        return n;
    }

    // カードの右上の点: 初期値から変えた項目があるか (出現率は初期値が 0 = 出ないので縁の色で分かる → 数えない)
    private static bool DetailChanged(RoleEntry e)
    {
        foreach (var o in e.Opts)
            if (o != e.Chance && o.Changed) return true;
        return false;
    }

    // narrow = 2 列の一覧のカード (名前+人数と出現率の 1 行・説明は右の欄だけ)。検索の結果は幅いっぱいで説明つき
    private Widget Card(RoleEntry e, float width, bool narrow)
    {
        bool on = !e.IsRole || e.On;
        float pad = narrow ? 0.09f : 0.14f;
        Label name, values;
        Widget body;
        if (narrow)
        {
            float textW = width - pad * 2f;
            name = new Label(e.Title, 1.6f) { Width = textW, MinFontSize = 1.05f, Bold = true, Color = NameColor(e), Align = Align.Start };
            values = new Label(CardValues(e), 1.15f) { Width = textW, Bold = true, Color = on ? Color.white : Gray, Align = Align.Start };
            body = new Stack(false, name, values) { Gap = 0f, Width = textW, Align = Align.Start };
        }
        else
        {
            name = new Label(e.Title, 1.9f) { Width = width - 1.0f, Bold = true, Color = NameColor(e), Align = Align.Start };
            var desc = new Label(e.Blurb, 1.15f) { Width = width - 1.0f, Color = on ? SubText : Gray, Align = Align.Start };
            values = new Label(CardValues(e), 1.35f) { Width = 0.9f, TextAlign = TextAlignmentOptions.Right, Bold = true, Color = on ? Color.white : Gray };
            var text = new Stack(false, name) { Gap = 0.01f, Width = width - 1.25f };
            if (desc.Content.Length > 0) text.Add(desc);
            body = new Stack(true, text, values) { Gap = 0.1f };
        }
        var card = new Button(body, () => OnCard(e))
        {
            Size = new Vector2(width, 0f),
            MinSize = new Vector2(width, 0.55f),
            Padding = new Vector2(pad, 0.07f),
            Color = CardColor(e),
            Badge = true,
            BadgeOn = DetailChanged(e),
            // 選んだカードは中を薄く塗る (選び直しは塗りの色だけ書き換える)
            Fill = narrow && SideEdit ? SelectFill(e) : null,
            OnHover = () => { if (narrow) ShowSide(e, true); },
            OnUnhover = () => { if (narrow && _hover == e) ShowSide(null, true); },
        };
        _cards.Add((e, card, values, name));
        return card;
    }

    private Color SelectFill(RoleEntry e) => e == _open ? Color.Lerp(Color.black, e.Color, 0.28f) : Color.black;

    private void OnCard(RoleEntry e)
    {
        if (SideEdit)
        {
            // もう一度押すと閉じる
            Select(_open == e ? null : e);
            return;
        }
        OpenDetail(e);
    }

    private void OpenDetail(RoleEntry e)
    {
        _list?.Remember();
        _open = e;
        Rebuild();
    }

    // ---- 右の欄 ----

    // 当てた物の要約を右に出す (右の欄で編集している間は編集のまま)
    // 当てた/外れた時の本編の通知の中で画面を作り直さない: 右の欄は次の Tick で書き直す
    // hover = カードに当てた/外れた時。次々に当てる間に毎回作り直さないよう、止まってから少し待って 1 回だけ書き直す
    private const float HoverSettle = 0.08f;

    private void ShowSide(RoleEntry e, bool hover = false)
    {
        _hover = e;
        if (SideEdit && _open != null) return;
        _sideByHover = hover && (!_sideDirty || _sideByHover);
        _sideAt = hover ? Time.realtimeSinceStartup + HoverSettle : 0f;
        _sideDirty = true;
    }

    private void RefreshSide()
    {
        if (_side?.Screen == null || !_side.Screen.Alive) return;
        _sideList?.Remember();
        _sideList = null;
        _slider = null;
        _editor.Clear();
        _resetBtn = _undoBtn = null;
        _pendingSide = false;
        _sideDirty = false;
        _sideByHover = false;
        _sideShown = SideEdit && _open != null ? null : _hover;
        _side.Screen.Set(SideContent());
    }

    // 右の欄で編集する物を選ぶ (null = 選びを外して、まとめて変える札へ)
    private void Select(RoleEntry e)
    {
        _open = e;
        foreach (var (s, card, _, _) in _cards)
            if (card.FillRenderer) card.FillRenderer.color = SelectFill(s);
        RefreshSide();
    }

    private Widget SideContent()
    {
        if (SideEdit && _open != null) return SideEditor(_open);
        if (_hover != null) return Summary(_hover);
        if (Editable && HasRoles()) return Bulk();
        return new Stack(false,
            new Label(Tr("RoleBrowser.Hint"), 1.3f) { Color = SubText, Width = SideW, Align = Align.Start }) { Gap = 0.12f, Width = SideW };
    }

    private bool HasRoles()
    {
        foreach (var e in RoleCatalog.Of(_team))
            if (e.IsRole && e.Listed) return true;
        return false;
    }

    private static string Category(RoleEntry e) => Tr("MenuCat." + e.Tab);

    private static Widget Hero(float width, RoleEntry e, Widget tools = null) => SectionPage.Hero(width, e.Color, e.Title, Category(e), tools);

    private Widget Summary(RoleEntry e)
    {
        var col = new Stack(false) { Gap = 0.03f, Width = SideW };
        col.Add(Hero(SideW, e));
        col.Add(new Spacer(0.1f, 0.03f));
        if (e.Blurb.Length > 0) col.Add(new Label(e.Blurb, 1.2f) { Color = SubText, Width = SideW, Align = Align.Start });
        col.Add(new Spacer(0.1f, 0.06f));
        int n = 0;
        foreach (var opt in e.Opts)
        {
            if (opt.IsText || !opt.Shown) continue;
            if (n++ >= SummaryLines)
            {
                col.Add(new Label("…", 1.25f) { Width = SideW, Align = Align.Start, Color = SubText });
                break;
            }
            col.Add(new Label($"{opt.Label}{(opt.Changed ? OptEditor.ChangedMark : "")}: {ValueText(opt)}", 1.25f) { Width = SideW, Align = Align.Start, MinFontSize = 0.9f, NoWrap = true });
        }
        return col;
    }

    private static string ValueText(MenuOpt opt)
    {
        opt.Sync();
        if (opt.IsToggle)
            return $"<color=#{ColorUtility.ToHtmlStringRGB(opt.Index != 0 ? OptEditor.OnColor : OptEditor.OffColor)}>{opt.Display()}</color>";
        return opt.Display();
    }

    // ---- 区分ぜんぶを一度に変える (何も選んでいない時の右の欄) ----

    private Widget Bulk()
    {
        string team = Tr("MenuCat." + _team);
        var col = new Stack(false) { Gap = 0.08f, Width = SideW };
        string head = string.Format(Tr("RoleBrowser.BulkTeam"), team);
        col.Add(SectionPage.Hero(SideW, SettingsMenuPatch.CatColor(_team), team, head));
        col.Add(new Label(string.Format(Tr("RoleBrowser.EnabledBig"), EnabledIn(), CountIn()), 2.4f) { Bold = true, Width = SideW, Align = Align.Start });
        col.Add(new Label(Tr("RoleBrowser.Hint"), 1.1f) { Color = SubText, Width = SideW, Align = Align.Start });
        float third = (SideW - 0.16f) / 3f;
        col.Add(new Stack(true,
            new Button("0%", () => ApplyBulk(true, 0), 1.4f) { Size = new Vector2(third, 0.4f) },
            new Button("100%", () => ApplyBulk(true, 100), 1.4f) { Size = new Vector2(third, 0.4f) },
            new Button(Tr("SettingsUI.Undo"), Undo, 1.2f) { Size = new Vector2(third, 0.4f), Color = UndoColor }) { Gap = 0.08f });
        col.Add(BulkStepper(string.Format(Tr("RoleBrowser.ChanceTo"), _bulkChance),
            () => { _bulkChance = Math.Max(0, _bulkChance - 10); ShowSide(null); },
            () => { _bulkChance = Math.Min(100, _bulkChance + 10); ShowSide(null); },
            () => ApplyBulk(true, _bulkChance)));
        col.Add(BulkStepper(string.Format(Tr("RoleBrowser.CountTo"), _bulkCount),
            () => { _bulkCount = Math.Max(1, _bulkCount - 1); ShowSide(null); },
            () => { _bulkCount = Math.Min(MaxBulkCount(), _bulkCount + 1); ShowSide(null); },
            () => ApplyBulk(false, _bulkCount)));
        return col;
    }

    private Widget BulkStepper(string label, Action minus, Action plus, Action apply)
    {
        const float step = 0.36f;
        return new Stack(true,
            new Button("-", minus, 1.5f) { Size = new Vector2(step, 0.4f) },
            new Button(new Label(label, 1.2f) { TextAlign = TextAlignmentOptions.Center, Width = SideW - step * 2f - 0.4f, MinFontSize = 0.8f }, apply) { Size = new Vector2(SideW - step * 2f - 0.16f, 0.4f) },
            new Button("+", plus, 1.5f) { Size = new Vector2(step, 0.4f) }) { Gap = 0.08f };
    }

    private IEnumerable<RoleEntry> TeamRoles()
    {
        foreach (var e in Visible()) if (e.IsRole) yield return e;
    }

    private int EnabledIn()
    {
        int n = 0;
        foreach (var e in TeamRoles()) if (e.On) n++;
        return n;
    }

    private int CountIn()
    {
        int n = 0;
        foreach (var _ in TeamRoles()) n++;
        return n;
    }

    private int MaxBulkCount()
    {
        int max = 1;
        foreach (var e in TeamRoles())
            if (e.Count != null && !e.Count.Fixed) max = Math.Max(max, (int)(e.Count.ValueAt(e.Count.Count - 1) ?? 1));
        return max;
    }

    // まとめて変える: 今の区分の札で見えている役職を全部 (変える前を 1 回分覚える)
    private void ApplyBulk(bool chance, int value)
    {
        if (!Editable) return;
        int n = 0;
        OptHistory.BeginGroup();
        OptEditor.Busy++;
        try
        {
            foreach (var e in TeamRoles())
            {
                var opt = chance ? e.Chance : e.Count;
                if (opt == null || opt.Fixed) continue;
                int index = opt.IndexForInput(value.ToString());
                int before = opt.Index;
                if (index < 0 || !opt.Restore(index)) continue;
                OptHistory.Push(opt, before);
                OptEditor.AfterStep(opt);
                n++;
            }
        }
        finally
        {
            OptEditor.Busy--;
            OptHistory.EndGroup();
        }
        if (n == 0) return;
        AfterEdit(null);
        RefreshView();
    }

    // 最後の 1 回の操作を戻す (行の 1 押し・まとめて変える・既定に戻す。ほかの頁と共通)
    private void Undo()
    {
        if (!OptHistory.Any || !Editable) return;
        OptEditor.Busy++;
        try { OptHistory.Undo(); }
        finally { OptEditor.Busy--; }
        AfterEdit(null);
        RefreshView();
    }

    // 開いている欄の項目を全部既定へ (1 回の取り消しで戻る)
    private void ResetEntry(RoleEntry e)
    {
        if (!Editable) return;
        bool any = false;
        OptHistory.BeginGroup();
        OptEditor.Busy++;
        try
        {
            foreach (var opt in e.Opts)
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
        if (!any) return;
        AfterEdit(null);
        RefreshView();
    }

    // 右の欄の 0% / 100% (1 回の取り消しで戻る)
    private void SetChance(RoleEntry e, int value)
    {
        var opt = e.Chance;
        if (opt == null || !Editable) return;
        int index = opt.IndexForInput(value.ToString());
        if (index < 0 || !SetIndex(opt, index)) return;
        AfterEdit(opt);
        RefreshView();
    }

    // 番号を書いて取り消しの履歴に積む。変わったら true
    private static bool SetIndex(MenuOpt opt, int index)
    {
        int before = opt.Index;
        OptEditor.Busy++;
        try
        {
            if (!opt.Restore(index)) return false;
            OptEditor.AfterStep(opt);
        }
        finally { OptEditor.Busy--; }
        OptHistory.Push(opt, before);
        return true;
    }

    private static float Frac(MenuOpt opt) => opt.Count > 1 ? opt.Index / (float)(opt.Count - 1) : 0f;

    // 出現率のつまみ (右の欄で役職を編集している間だけ)。引いて変えた値は ± と同じ道で後始末する
    private float SlideChance(RoleEntry e, float v)
    {
        var opt = e.Chance;
        if (opt == null) return v;
        if (!Editable) return Frac(opt);
        // 引いている間は毎フレーム来る。1 回引く間の変更は取り消し 1 段にまとめる (離して間が空いたら次の段)
        float now = Time.unscaledTime;
        bool sameDrag = _slideOpt == opt && now - _slideAt < 0.5f;
        _slideOpt = opt;
        _slideAt = now;
        int before = opt.Index;
        OptEditor.Busy++;
        try
        {
            if (!opt.Restore((int)Math.Round(v * (opt.Count - 1)))) return Frac(opt);
            OptEditor.AfterStep(opt);
        }
        finally { OptEditor.Busy--; }
        if (!sameDrag) OptHistory.Push(opt, before);
        AfterEdit(opt);
        _editor.Refresh();
        // 出る行が変わった (出現率が 0 をまたいだ): つまみを引いている間は作り直さず、離してから
        if (_editor.ShownChanged()) _pendingSide = true;
        return Frac(opt);
    }

    private static Color UndoColor => OptHistory.Any ? Color.white : OptEditor.Dim;

    private static Color ResetColor(RoleEntry e)
    {
        foreach (var opt in e.Opts)
            if (opt.Changed) return OptEditor.ChangedColor;
        return OptEditor.Dim;
    }

    private void AfterEdit(MenuOpt opt)
    {
        if (_open != null)
        {
            _resetBtn?.SetColor(ResetColor(_open));
            _undoBtn?.SetColor(UndoColor);
            if (_slider != null && _open.Chance != null) _slider.Show(Frac(_open.Chance));
        }
        RefreshCards();
        SettingsPages.RefreshOtherPages();
        SettingsMenuPatch.RefreshCategoryLabels();
        // プリセットとゲームモードは出る設定がまるごと変わる
        if (opt?.Item is PresetOptionItem || opt?.Item == Options.GameMode) Rebuild();
    }

    // カードの値と縁の色だけを今の値に (作り直さない)
    public void RefreshCards()
    {
        foreach (var (e, card, values, name) in _cards)
        {
            if (!card.BackRenderer) continue;
            bool on = !e.IsRole || e.On;
            card.SetColor(CardColor(e));
            card.SetBadge(DetailChanged(e));
            // 字はゲーム側へ写すたびに費用が掛かるので、変わったカードだけ書く
            if (values.Built)
            {
                string v = CardValues(e);
                if (v != values.Content) values.SetText(v);
                values.Built.color = on ? Color.white : Gray;
            }
            if (name.Built) name.Built.color = NameColor(e);
        }
    }

    // 外 (ほかの頁・プリセット・コマンド・取り消し) で値を変えた後。並びが変わっていれば組み直し、そうでなければ値だけ書き直す
    public void RefreshFromOutside()
    {
        if (_screen == null || !_screen.Alive) return;
        RefreshView();
    }

    private void RefreshView()
    {
        if (_screen == null || !_screen.Alive) return;
        if (Searching || (_open != null && !SideEdit)) { Rebuild(); return; }
        var now = Visible();
        bool same = now.Count == _listed.Count;
        for (int i = 0; same && i < now.Count; i++)
            if (now[i] != _listed[i]) same = false;
        if (!same) { Rebuild(); return; }
        RefreshCards();
        if (_open != null || _hover != null || _side != null)
        {
            // 右の欄は出る行が変わっていれば作り直し、そうでなければ行の字だけ
            if (_open != null && _side != null && !_editor.ShownChanged()) _editor.Refresh();
            else RefreshSide();
        }
    }

    // ---- 右の欄で編集 ----

    private Widget SideEditor(RoleEntry e)
    {
        _slider = null;
        bool edit = Editable;
        const float btn = 0.36f, gap = 0.05f;
        // 見出しの左上: 前後の欄・閉じる
        var tools = new Stack(true) { Gap = gap };
        var order = Visible();
        int at = order.IndexOf(e);
        if (at >= 0 && order.Count > 1)
        {
            var prev = order[(at + order.Count - 1) % order.Count];
            var next = order[(at + 1) % order.Count];
            tools.Add(new Button("◀", () => Select(prev), 1.3f) { Size = new Vector2(btn, btn), Padding = Vector2.zero });
            tools.Add(new Button("▶", () => Select(next), 1.3f) { Size = new Vector2(btn, btn), Padding = Vector2.zero });
        }
        tools.Add(new Button("×", () => Select(null), 1.5f) { Size = new Vector2(btn, btn), Padding = Vector2.zero });

        var col = new Stack(false) { Gap = 0.05f, Width = SideW };
        col.Add(Hero(SideW, e, tools));

        // 項目の行 (多い時はこの中だけスクロール)。出現率と人数は役職の色の枠にまとめ、0% / 100% のボタンを添える
        float rowW = SideW - 0.18f;
        var rows = new Stack(false) { Gap = 0.06f, Width = rowW };
        string detail = e.IsRole ? e.Chance.Detail() : null;
        if (!string.IsNullOrEmpty(detail)) rows.Add(new Label(detail, 1.05f) { Color = SubText, Width = rowW, Align = Align.Start });
        _editor.Watch(e.Opts);
        Stack core = null;
        float coreW = rowW - 0.1f;
        foreach (var opt in e.Opts)
        {
            if (!opt.Shown) continue;
            int indent = Math.Max(0, opt.Depth - (e.IsRole ? 1 : 0));
            if (opt.IsText)
            {
                rows.Add(new Label(opt.Label, OptEditor.NarrowFont) { Bold = true, Width = rowW - indent * 0.2f, Color = opt.NameColor == Color.white ? SubText : MenuOpt.Readable(opt.NameColor), Align = Align.Start });
                continue;
            }
            bool inCore = opt == e.Chance || opt == e.Count;
            var r = _editor.NarrowRow(opt, edit, inCore ? coreW : rowW, inCore ? 0f : indent * 0.2f);
            r.Align = Align.Start;
            if (!inCore) { rows.Add(r); continue; }
            if (core == null)
            {
                core = new Stack(false) { Gap = 0.06f, Width = coreW };
                rows.Add(new Box(core) { Size = new Vector2(rowW, 0f), Padding = new Vector2(0.05f, 0.07f), Color = Color.Lerp(e.Color, Color.black, 0.25f), Hollow = true });
            }
            core.Add(r);
            if (opt == e.Chance && edit)
            {
                _slider = new Slider(coreW, Frac(opt), v => SlideChance(e, v)) { Color = e.Color };
                core.Add(_slider);
            }
        }
        if (core != null && edit && e.Chance != null)
        {
            float qw = (coreW - 0.08f) / 2f;
            core.Add(new Stack(true,
                new Button("0%", () => SetChance(e, 0), 1.3f) { Size = new Vector2(qw, 0.32f) },
                new Button("100%", () => SetChance(e, 100), 1.3f) { Size = new Vector2(qw, 0.32f) }) { Gap = 0.08f });
        }
        _editor.Refresh();

        // 下の段: 初期値に戻す・元に戻す
        const float barH = 0.38f;
        var bar = new Stack(true) { Gap = gap, Align = Align.Start };
        float bw = (SideW - gap) / 2f;
        if (edit)
        {
            _resetBtn = new Button(new Label(Tr("SettingsUI.Reset"), 1.2f) { TextAlign = TextAlignmentOptions.Center, Width = bw - 0.2f, MinFontSize = 0.8f, NoWrap = true }, () => ResetEntry(e)) { Size = new Vector2(bw, barH), Color = ResetColor(e) };
            _undoBtn = new Button(Tr("SettingsUI.Undo"), Undo, 1.2f) { Size = new Vector2(bw, barH), Color = UndoColor };
            bar.Add(_resetBtn).Add(_undoBtn);
        }

        float used = SectionPage.HeroH + 0.05f + (edit ? barH + 0.1f : 0f);
        // 値を変えて作り直しても同じ所を見せる (欄ごとの位置)
        _sideList = new ScrollView(new Vector2(SideW, Math.Max(_sideH - used, 1f)), rows) { Tag = _tag + ":side:" + (e.Root?.Name ?? e.Title) };
        col.Add(_sideList);
        if (edit) col.Add(bar);
        return col;
    }

    // ---- 細かい頁 (検索の結果から開く) ----

    private Widget DetailPage(RoleEntry e)
    {
        bool edit = Editable;
        var back = new Button("<<", () => { _open = null; Rebuild(); }, 1.9f) { Size = new Vector2(0.7f, 0.5f) };
        var title = new Label(e.Title, 2.6f) { Bold = true, Color = e.Color };
        var head = new Stack(true, back, title) { Gap = 0.2f, Align = Align.Start };

        var rows = new Stack(false) { Gap = 0.1f, Width = W - 0.3f };
        string detail = e.IsRole ? e.Chance.Detail() : null;
        if (!string.IsNullOrEmpty(detail)) rows.Add(new Label(detail, 1.3f) { Color = SubText, Width = W - 0.4f, Align = Align.Start });
        else if (e.Blurb.Length > 0) rows.Add(new Label(e.Blurb, 1.3f) { Color = SubText, Width = W - 0.4f, Align = Align.Start });
        if (edit)
        {
            _resetBtn = new Button(Tr("SettingsUI.Reset"), () => ResetEntry(e), 1.35f) { Size = new Vector2(0f, 0.4f), MinSize = new Vector2(1.5f, 0.4f), Color = ResetColor(e) };
            _undoBtn = new Button(Tr("SettingsUI.Undo"), Undo, 1.35f) { Size = new Vector2(0f, 0.4f), MinSize = new Vector2(1.2f, 0.4f), Color = UndoColor };
            rows.Add(new Stack(true, _resetBtn, _undoBtn) { Gap = 0.1f, Align = Align.Start });
        }
        _editor.Watch(e.Opts);
        float rowW = W - 0.3f;
        int k = 0;
        foreach (var opt in e.Opts)
        {
            if (!opt.Shown) continue;
            int indent = Math.Max(0, opt.Depth - (e.IsRole ? 1 : 0));
            if (opt.IsText)
            {
                rows.Add(new Label(opt.Label, OptEditor.FontSize) { Bold = true, Width = W - 0.4f, Color = opt.NameColor == Color.white ? SubText : MenuOpt.Readable(opt.NameColor), Align = Align.Start });
                k = 0;
                continue;
            }
            var r = _editor.Row(opt, edit, indent * 0.3f, null, rowW - OptEditor.PlatePad * 2f);
            rows.Add(OptEditor.Plated(r, rowW, k++));
        }
        _editor.Refresh();

        _list = new ScrollView(new Vector2(W, H - 0.62f), rows) { Tag = _tag + ":detail:" + (e.Root?.Name ?? e.Title) };
        return new Stack(false, head, _list) { Gap = 0.1f, Width = W };
    }

    // ---- 検索 ----

    private static string Join(string a, string b) => $"{a} › {b}";

    // 役職でない設定のまとまり (見出し・色・項目): ゲームの頁・役職の区分の共通の設定
    private static IEnumerable<(string title, Color color, IEnumerable<MenuOpt> opts)> PlainGroups()
    {
        if (VanillaGame.All.Count == 0) VanillaGame.Rebuild();
        string game = Tr("MenuCat.Game");
        var gameColor = SettingsMenuPatch.CatColor(null);
        foreach (var g in SettingsPages.GameGroups())
            yield return (Join(game, g.Title), g.Color == Color.white ? gameColor : g.Color, g.Opts);


        foreach (var tab in RoleCatalog.Tabs)
        {
            string cat = Tr("MenuCat." + tab);
            foreach (var e in RoleCatalog.Of(tab))
                if (!e.IsRole) yield return (Join(cat, e.Title), e.Color, e.Opts);
        }
    }

    // 検索の結果の行の名前: 語に当たった所だけ色を付ける (名前全体を塗ると、どの行も同じ色になって目印にならない)。
    // 名前に当たらず、見出しや内部の名前で当たった行は、ふだんの名前のまま
    private static readonly string HighlightHex = "#" + ColorUtility.ToHtmlStringRGB(Highlight);

    private string MarkedLabel(MenuOpt opt)
    {
        string marked = SettingsSearch.Mark(opt.PlainLabel, _tokens, HighlightHex);
        return marked == null ? null : Utils.ColorString(MenuOpt.Readable(opt.NameColor), marked);
    }

    // 結果の頁に並べる行の上限。当たりが数百あると頁が高くなりすぎて、スクロール欄が中身を描き切れなくなる
    private const int MaxSearchRows = 80;

    private Widget SearchPage()
    {
        HitCount = 0;
        int shown = 0;
        bool edit = Editable;
        float colW = W - 0.3f;
        var col = new Stack(false) { Gap = 0.08f, Width = colW };
        var watch = new List<MenuOpt>();

        foreach (var (title, color, opts) in PlainGroups())
        {
            string context = SettingsSearch.Haystack(title);
            Stack rows = null;
            foreach (var opt in opts)
            {
                if (opt.IsText || !opt.Shown) continue;
                string hay = SettingsSearch.Haystack(opt);
                if (!SettingsSearch.AllIn(_tokens, hay + "\n" + context)) continue;
                HitCount++;
                if (shown >= MaxSearchRows) continue;
                shown++;
                if (rows == null)
                {
                    rows = new Stack(false) { Gap = 0.06f, Width = colW, Align = Align.Start };
                    rows.Add(new Label(title, 1.7f) { Color = MenuOpt.Readable(color), Bold = true, Width = colW - 0.1f, Align = Align.Start });
                }
                watch.Add(opt);
                var r = _editor.Row(opt, edit, 0.25f + opt.Depth * 0.2f, null, colW - OptEditor.PlatePad * 2f, MarkedLabel(opt));
                rows.Add(OptEditor.Plated(r, colW, rows.Children.Count - 1));
            }
            if (rows != null) col.Add(rows);
        }

        foreach (var tab in RoleCatalog.Tabs)
        {
            foreach (var e in RoleCatalog.Of(tab))
            {
                if (!e.IsRole || !e.Listed) continue;
                // 名前・説明・設定の名前のどれかに当たれば出す
                string nameHay = SettingsSearch.Haystack(e.Title) + "\n" + SettingsSearch.Haystack(e.Root.Name);
                var hay = new StringBuilder(nameHay).Append('\n').Append(SettingsSearch.Haystack(e.Blurb));
                foreach (var opt in e.Opts) hay.Append('\n').Append(SettingsSearch.Haystack(opt));
                if (!SettingsSearch.AllIn(_tokens, hay.ToString())) continue;
                HitCount++;
                if (shown >= MaxSearchRows) continue;
                shown++;
                var card = Card(e, colW, false);
                card.Align = Align.Start;
                col.Add(card);
                watch.AddRange(e.Opts);
                // 役職の名前に当たった時はその設定を全部、そうでなければ当たった設定だけ
                bool nameHit = SettingsSearch.AnyIn(_tokens, nameHay);
                // 役職の設定の行は、カードの下に一段下げた地で並べる
                const float under = 0.2f;
                float subW = colW - under;
                int k = 0;
                foreach (var opt in e.Opts)
                {
                    if (opt.IsText || !opt.Shown) continue;
                    bool hit = SettingsSearch.AnyIn(_tokens, SettingsSearch.Haystack(opt));
                    if (!nameHit && !hit) continue;
                    if (shown >= MaxSearchRows) break;
                    shown++;
                    var r = _editor.Row(opt, edit, 0.2f + Math.Max(0, opt.Depth - 1) * 0.2f, null, subW - OptEditor.PlatePad * 2f, hit ? MarkedLabel(opt) : null);
                    col.Add(new Stack(true, new Spacer(under, 0.1f), OptEditor.Plated(r, subW, k++)) { Gap = 0f, Align = Align.Start });
                }
            }
        }

        if (HitCount == 0)
            col.Add(new Label(string.Format(Tr("MenuSearch.NoHit"), _query), 1.6f) { Color = SubText, Width = W - 0.4f, TextAlign = TextAlignmentOptions.Center });
        else
        {
            col.Children.Insert(0, new Label(string.Format(Tr("MenuSearch.Result"), _query, HitCount), 1.3f) { Color = SubText, Width = W - 0.4f, Align = Align.Start });
            if (shown >= MaxSearchRows)
                col.Add(new Label(string.Format(Tr("MenuSearch.TooMany"), MaxSearchRows), 1.4f) { Color = SubText, Width = W - 0.4f, TextAlign = TextAlignmentOptions.Center });
        }
        _editor.Watch(watch);
        _editor.Refresh();
        _list = new ScrollView(new Vector2(W, H), col);
        return _list;
    }

    // ---- 毎フレーム (開いている間だけ) ----

    public void Tick()
    {
        if (_sideDirty && Time.realtimeSinceStartup >= _sideAt)
        {
            // 外れてすぐ同じカードへ戻っただけなら、出ている要約のまま
            if (_sideByHover && _hover == _sideShown) { _sideDirty = false; _sideByHover = false; }
            else RefreshSide();
        }
        // つまみを離したら、出る行が変わっていた右の欄を作り直す
        if (_pendingSide && !Input.GetMouseButton(0) && Input.touchCount == 0)
        {
            _pendingSide = false;
            RefreshSide();
        }
        // カードに当てている間の右クリック = 出現率を 0% と最大で切り替える (値を変えられる時だけ)
        if (!SideEdit || _hover?.Chance == null || !Input.GetMouseButtonDown(1) || !Editable) return;
        var c = _hover.Chance;
        if (!SetIndex(c, c.Index > 0 ? 0 : c.Count - 1)) return;
        AfterEdit(c);
        RefreshView();
    }
}
