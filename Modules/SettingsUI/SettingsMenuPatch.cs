// Based on https://github.com/haru03741-glitch/TownOfHost_L Patches/GameSettingMenuPatch.cs and Patches/GameOptionsMenuPatch.cs (GPL-3.0),
// which are based on https://github.com/haru03741-glitch/More-Roles-Plus src/Options/SettingsMenu.cs (GPL-3.0),
// which is based on https://github.com/waffle-ful/Aeterna-End-K-not Patches/GameOptionsMenuPatch.cs (GPL-3.0)
// 画面の枠は本編の大きさのまま。左の列 1 本 (プリセット / ゲーム / 全般 / 区分ごと) と板の上の検索欄で、全部の設定へ行く
using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace EndKnot.SettingsUI;

// タブの番号: 本編は 0〜2、区分は 3 から TabGroup の順、その次が検索の結果とプリセット
internal static class SettingsTabs
{
    public const int FirstModTab = 3;
    public static readonly int SearchTab = FirstModTab + Enum.GetValues(typeof(TabGroup)).Length;
    public static readonly int PresetTab = SearchTab + 1;
    public static int TabIndex;
    public static bool IsModTab(int tabNum) => tabNum >= FirstModTab;
}

[HarmonyPatch(typeof(GameSettingMenu))]
internal static class SettingsMenuPatch
{
    private static GameOptionsMenu templateGameOptionsMenu;
    private static PassiveButton templateGameSettingsButton;

    public static bool PresetChanged;

    private static string PresetChangedWarning
        => $"\n\n<color=#ffff00><b>{Translator.GetString("PresetChangedWarning")}</b></color>";

    public static void NotifyPresetChanged()
    {
        PresetChanged = true;

        var menu = GameSettingMenu.Instance;
        if (!menu || !menu.MenuDescriptionText) return;

        // 説明を書き直す道 (置き場に収める・入らなければ案内を落とす) を通して警告を付ける
        if (lastDescText != null)
        {
            SetDescription(menu, lastDescText, lastDescBelow);
            return;
        }

        menu.MenuDescriptionText.DestroyTranslator();
        if (!menu.MenuDescriptionText.text.Contains(PresetChangedWarning))
            menu.MenuDescriptionText.text += PresetChangedWarning;
    }

    // 頁の置き場にする画面 (本編のゲーム設定の画面の複製)。全般の頁と、役職の区分・検索の結果の頁 (RoleBrowser 1 つ) で 1 枚ずつ
    private static GameOptionsMenu ModTab;
    private static GameOptionsMenu RoleHost;
    private static GameOptionsMenu PresetHost;
    private static SettingsSearchBox SearchBox;
    // 検索欄の今の語 (検索の結果の頁を出している間だけ頁に渡す)
    private static string searchQuery = "";
    private static GameSettingMenu OpenMenu;
    // 検索を空にした時に戻るタブ
    private static int ReturnTab = 1;

    // 左の列 (設定画面の座標)。X は本編のタブのボタンと同じ所。
    // ボタンが CatFitCount 個を超えたら、同じ高さに収まるよう間隔とボタンの高さを詰める
    private const float CatX = -2.96f, CatTopY = 1.6f, CatStepY = 0.46f;
    private const float CatScaleX = 1f, CatScaleY = 0.78f;
    private const int CatFitCount = 7;
    // ボタンの数は区分の列の全部 (ゲームモードで隠れる分も含む) で決め、列の高さが変わらないようにする
    private static float CatFit => ColumnTabs.Length + 2 > CatFitCount ? (CatFitCount - 1f) / (ColumnTabs.Length + 2 - 1f) : 1f;
    // 区分の絵: ボタンの高さに対する絵の高さ・選んだ時の大きさ
    private const float CatArtHeight = 1.05f, CatArtPicked = 1.15f;
    // 検索欄は板の上の縁より上・右上の本編のボタンの列に掛からない所
    private static readonly Vector2 SearchBoxPosition = new(-1.65f, 2.6f);
    private const float SearchBoxWidth = 3.4f;
    private const float SearchBoxHeight = 0.42f;

    // 左の列に出す区分 (全般 + 役職の列)。ゲームの設定・タスクの設定は「ゲーム」の頁に、古い役職は各列の末尾に、プリセット エクスプローラーはプリセットの頁にまとめる
    private static readonly TabGroup[] ColumnTabs =
    {
        TabGroup.SystemSettings, TabGroup.ImpostorRoles, TabGroup.CrewmateRoles, TabGroup.NeutralRoles, TabGroup.CovenRoles,
        TabGroup.JackalRoles, TabGroup.Combinations, TabGroup.Addons, TabGroup.OtherRoles,
    };

    // 左の列のボタン (押すと出すタブの番号)
    // 左の列のボタンに結んだ UiActions の番号 (閉じる時に返す)
    private static readonly List<int> CatSlots = new();

    private sealed class Cat
    {
        public PassiveButton Button;
        public int TabNum;
        public TabGroup? Tab; // null = 本編のゲーム設定 (Presets の時はプリセット)
        public bool Presets;
        public TextMeshPro Label;
        public string Shown; // 字に書いた今の文
        public Transform Art;
        public Vector3 ArtPos, ArtScale;
        public float ArtLift;
    }

    private static readonly List<Cat> Cats = new();

    private static readonly Dictionary<TabGroup, string> CategoryColors = new()
    {
        [TabGroup.SystemSettings] = "#ffef3a",
        [TabGroup.ImpostorRoles] = "#ff3b3b",
        [TabGroup.CrewmateRoles] = "#8cffff",
        [TabGroup.NeutralRoles] = "#ffab1b",
        [TabGroup.CovenRoles] = "#b07cf0",
        [TabGroup.JackalRoles] = "#2cc6f0",
        [TabGroup.Combinations] = "#e8c24a",
        [TabGroup.Addons] = "#ff9ae8",
        [TabGroup.OtherRoles] = "#c4c8d0",
    };

    private const string GameCategoryColor = "#8aa2ff";

    // 区分の絵 (専用の絵が無い列は、似た陣営の絵を使う。絵の無い列は文字だけ)
    private static string ArtOf(TabGroup? tab) => tab switch
    {
        null => "Game",
        TabGroup.SystemSettings => "ModMainSettings",
        TabGroup.ImpostorRoles => "ImpostorRoles",
        TabGroup.CrewmateRoles => "CrewmateRoles",
        TabGroup.NeutralRoles => "NeutralRoles",
        TabGroup.CovenRoles => "MadmateRoles",
        TabGroup.JackalRoles => "NeutralRoles",
        TabGroup.Addons => "Addons",
        _ => null,
    };

    internal static Color CatColor(TabGroup? tab)
    {
        var hex = tab is { } t && CategoryColors.TryGetValue(t, out var c) ? c : GameCategoryColor;
        // ColorUtility.TryParseHtmlString は Android の libunity に無いので、自分で読む
        if (hex.Length == 7 && hex[0] == '#' && int.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out int rgb))
            return new Color((rgb >> 16 & 0xFF) / 255f, (rgb >> 8 & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);
        return Color.white;
    }

    private static string CategoryLabel(TabGroup? tab)
    {
        if (tab is not { } group) return Translator.GetString("MenuCat.Game");
        var name = Translator.GetString($"MenuCat.{group}");
        if (group == TabGroup.SystemSettings) return name;

        int total = 0, lit = 0;
        foreach (var e in RoleCatalog.Of(group))
        {
            if (!e.IsRole || !e.Listed) continue;
            total++;
            if (e.On) lit++;
        }

        return total == 0 ? name : $"{name}<size=65%>  {lit}/{total}</size>";
    }

    // 出る役職の数が変わった時・ゲームモードが変わった時
    public static void RefreshCategoryLabels()
    {
        if (!OpenMenu) return;
        foreach (var cat in Cats)
        {
            if (!cat.Label || cat.Presets) continue;
            // 書き込みは字の組み直しを伴うので、変わった時だけ
            var text = CategoryLabel(cat.Tab);
            if (text == cat.Shown) continue;
            cat.Shown = text;
            cat.Label.text = text;
        }

        RelayoutCats();
    }

    // 今のゲームモードに無い区分のボタンは隠し、残りを詰めて並べ直す。開いている区分が無くなったらゲームの頁へ
    private static void RelayoutCats()
    {
        if (Cats.Count == 0) return;
        float step = CatStepY * CatFit;
        int row = 0;
        foreach (var c in Cats)
        {
            bool on = IsRelevant(c);
            if (c.Button) c.Button.gameObject.SetActive(on);
            if (!on) continue;
            if (c.Button)
            {
                var t = c.Button.transform;
                t.localPosition = new Vector3(CatX, CatTopY - row * step, t.localPosition.z);
            }

            row++;
        }

        int tabIndex = SettingsTabs.TabIndex;
        if (OpenMenu && SettingsTabs.IsModTab(tabIndex) && tabIndex != SettingsTabs.SearchTab && tabIndex != SettingsTabs.PresetTab)
        {
            var tab = (TabGroup)(tabIndex - SettingsTabs.FirstModTab);
            if (!tab.IsRelevant()) OpenMenu.ChangeTab(1, false);
        }
    }

    private static bool IsRelevant(Cat c) => c.Tab is not { } t || t.IsRelevant();

    // 頁の置き場の画面か (CreateSettings がどの頁を作るかを決める)
    internal static bool IsRoleHost(GameOptionsMenu menu) => menu && RoleHost && menu.Pointer == RoleHost.Pointer;
    internal static bool IsModTabMenu(GameOptionsMenu menu) => menu && ModTab && menu.Pointer == ModTab.Pointer;
    internal static bool IsPresetHost(GameOptionsMenu menu) => menu && PresetHost && menu.Pointer == PresetHost.Pointer;

    // 左の列の一番下のボタンのすぐ下 (世界座標の左上と幅)。役職の区分ではここに絞り込みの札を置く
    internal static bool CatFoot(out Vector3 topLeft, out float width)
    {
        topLeft = default;
        width = 0f;
        Cat last = null;
        foreach (var c in Cats) if (IsRelevant(c)) last = c;
        if (last == null) return false;
        var btn = last.Button;
        if (!ButtonBounds(btn, out var b)) return false;
        topLeft = new Vector3(b.min.x, b.min.y - b.size.y * 0.25f, b.center.z);
        width = b.size.x;
        return true;
    }

    // ボタンの見た目の範囲 (世界の座標)。Collider2D.bounds は動かした直後の同じフレームでは前の位置のままなので、絵の範囲で測る
    private static bool ButtonBounds(PassiveButton btn, out Bounds bounds)
    {
        bounds = default;
        if (!btn) return false;
        foreach (var sr in btn.GetComponentsInChildren<SpriteRenderer>())
        {
            if (!sr.enabled || !sr.gameObject.activeInHierarchy) continue;
            bounds = sr.bounds;
            return bounds.size.y > 0f;
        }

        return false;
    }

    // 左の列のボタン 1 つ (本編のゲーム設定のボタンの複製・押す音と当てた時の見た目は本編のまま)
    private static void AddCat(GameSettingMenu menu, TabGroup? tab, int tabNum, bool presets = false)
    {
        var proto = templateGameSettingsButton;
        var btn = UnityEngine.Object.Instantiate(proto, menu.GameSettingsButton.transform.parent);
        string key = presets ? "Presets" : tab?.ToString() ?? "Game";
        btn.name = "Cat_" + key;
        btn.gameObject.SetActive(true);
        var z = proto.transform.localPosition.z;
        btn.transform.localPosition = new Vector3(CatX, CatTopY - Cats.Count * CatStepY * CatFit, z);
        var ps = proto.transform.localScale;
        float scaleY = CatScaleY * CatFit;
        btn.transform.localScale = new Vector3(ps.x * CatScaleX, ps.y * scaleY, ps.z);

        // 字: 縦横の縮みを揃え (横だけ潰れないよう)、はみ出す分は自動で小さくする
        var label = btn.GetComponentInChildren<TextMeshPro>();
        label.DestroyTranslator();
        float fix = scaleY / CatScaleX;
        var ls = label.transform.localScale;
        label.transform.localScale = new Vector3(ls.x * fix, ls.y, ls.z);
        var rt = label.rectTransform;
        rt.sizeDelta = new Vector2(rt.sizeDelta.x / fix, rt.sizeDelta.y);
        label.enableWordWrapping = false;
        label.enableAutoSizing = true;
        label.fontSizeMin = 1f;
        label.fontSizeMax = label.fontSize;
        label.alignment = TextAlignmentOptions.Center;
        label.text = presets ? Translator.GetString("MenuCat.Presets") : CategoryLabel(tab);

        var c = presets ? PresetPage.TabColor : CatColor(tab);
        btn.inactiveSprites.GetComponent<SpriteRenderer>().color = new Color(c.r * 0.6f, c.g * 0.6f, c.b * 0.6f, 1f);
        btn.activeSprites.GetComponent<SpriteRenderer>().color = c;
        btn.selectedSprites.GetComponent<SpriteRenderer>().color = c;

        var cat = new Cat { Button = btn, TabNum = tabNum, Tab = tab, Presets = presets, Label = label, Shown = label.text };
        string art = presets ? "Presets" : ArtOf(tab);
        if (art != null) AddCatArt(cat, $"EndKnot.Resources.Images.MenuCat_{art}.png");

        btn.OnMouseOver = new UnityEngine.Events.UnityEvent();
        btn.OnMouseOut = new UnityEngine.Events.UnityEvent();
        btn.OnClick = new();
        btn.OnClick.AddListener(UiActions.Take(() => menu.ChangeTab(tabNum, false), CatSlots));
        menu.ControllerSelectable.Add(btn);
        Cats.Add(cat);
    }

    // ボタンの左端に区分の絵 (クルーのポーズ)。字は絵の右の残りの幅へ寄せる
    private static void AddCatArt(Cat cat, string resource)
    {
        var btn = cat.Button;
        var bg = btn.inactiveSprites.GetComponent<SpriteRenderer>();
        var sprite = Utils.LoadSprite(resource, 100f);
        if (!bg || !sprite) return;
        var t = btn.transform;
        var b = bg.bounds;
        Vector3 min = t.InverseTransformPoint(b.min), max = t.InverseTransformPoint(b.max);
        float h = max.y - min.y;
        var ls = t.lossyScale;
        float aspect = ls.y / ls.x; // ボタンは縦横で縮みが違うので、絵の横を戻す
        float artH = h * CatArtHeight;

        var go = new GameObject("CatArt") { layer = btn.gameObject.layer };
        go.transform.SetParent(t, false);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.sortingOrder = bg.sortingOrder + 1;
        float s = artH / sprite.bounds.size.y;
        go.transform.localScale = new Vector3(s * aspect, s, 1f);
        go.transform.localPosition = new Vector3(min.x, min.y + h * 0.04f + artH / 2f, -0.01f);
        // 絵の左端をボタンの左の縁の少し内側へ (ポーズで横幅が違う)
        go.transform.position += new Vector3(b.min.x + b.size.y * 0.08f - sr.bounds.min.x, 0f, 0f);
        cat.Art = go.transform;
        cat.ArtPos = go.transform.localPosition;
        cat.ArtScale = go.transform.localScale;
        cat.ArtLift = h * 0.07f;

        var label = cat.Label;
        if (label.sortingOrder <= sr.sortingOrder) label.sortingOrder = sr.sortingOrder + 1;
        // 字の左の余白 = 絵の右端まで
        float edge = label.transform.InverseTransformPoint(new Vector3(sr.bounds.max.x, b.center.y, b.center.z)).x;
        float m = edge - label.rectTransform.rect.xMin;
        if (m > 0f) label.margin = new Vector4(m, 0f, 0f, 0f);
    }

    // 説明の字 (区分の説明と、行に当てた時の長い説明) を左の列の下から板の下の縁までに置く。長い時は字を小さくする
    private static void PlaceDescription(GameSettingMenu menu)
    {
        var desc = menu.MenuDescriptionText;
        Cat lastCat = null;
        foreach (var c in Cats) if (IsRelevant(c)) lastCat = c;
        var last = lastCat?.Button;
        var panel = menu.transform.FindChild("PanelSprite");
        var panelSr = panel ? panel.GetComponent<SpriteRenderer>() : null;
        if (!desc || !panelSr || !ButtonBounds(last, out var b)) return;
        float top = b.min.y - b.size.y * 0.3f;
        float bottom = panelSr.bounds.min.y + b.size.y * 0.5f;
        if (top <= bottom) return;
        descTop = top;
        descBottom = bottom;

        desc.DestroyTranslator();
        var t = desc.transform;
        t.SetParent(last.transform.parent, true);
        var rt = desc.rectTransform;
        rt.pivot = new Vector2(0f, 1f);
        t.position = new Vector3(b.min.x, top, t.position.z);
        var ls = t.lossyScale;
        if (ls.x > 0f && ls.y > 0f) rt.sizeDelta = new Vector2(b.size.x / ls.x, (top - bottom) / ls.y);
        desc.alignment = TextAlignmentOptions.TopLeft;
        desc.enableWordWrapping = true;
        // TMP の自動縮小はこの字では効かなかったので、字を書くたびに高さを測って縮める (FitDescription)
        desc.enableAutoSizing = false;
        // 画面は開き直しても使い回されるので、元の大きさは最初の 1 回だけ控える (縮めた後の大きさを控えない)
        if (DescBaseSize <= 0f) DescBaseSize = desc.fontSize;
        desc.gameObject.SetActive(true);
    }

    // 説明の置き場の上端と下端 (世界の座標・置けなかった時は上端 <= 下端)
    private static float descTop, descBottom;

    // below = 説明の上に空ける高さ (役職の区分では左の列の下に絞り込みの札が入る)
    private static void SetDescription(GameSettingMenu menu, string text, float below = 0f)
    {
        var desc = menu.MenuDescriptionText;
        if (!desc) return;
        desc.DestroyTranslator();
        lastDescText = text;
        lastDescBelow = below;
        // 開く途中は画面が拡大の動きの最中で測れないので、書くたびに置き場を測り直す
        PlaceDescription(menu);
        if (descTop > descBottom)
        {
            // 札の下に字が入る高さが残らない時は説明を出さない (板の外へはみ出さないように)
            float top = descTop - below;
            desc.gameObject.SetActive(top - descBottom >= 0.35f);
            var t = desc.transform;
            t.position = new Vector3(t.position.x, top, t.position.z);
            var ls = t.lossyScale;
            if (ls.y > 0f) desc.rectTransform.sizeDelta = new Vector2(desc.rectTransform.sizeDelta.x, (top - descBottom) / ls.y);
        }

        string warning = PresetChanged ? PresetChangedWarning : "";
        desc.text = text + warning;
        if (FitDescription(desc)) return;

        // 左の列が長いと置き場が低く、最小の字でも入らない。空行より後ろ (操作の案内) を落とす
        int cut = text.IndexOf("\n\n", StringComparison.Ordinal);
        if (cut > 0)
        {
            desc.text = text[..cut] + warning;
            if (FitDescription(desc)) return;
        }

        // それでも入らない時は、プリセットが替わった警告を本文より優先する
        if (warning.Length == 0) return;
        desc.text = warning.TrimStart('\n');
        FitDescription(desc);
    }

    private static float DescBaseSize;
    private static string lastDescText;
    private static float lastDescBelow;

    // 説明が置き場の高さに入るまで字を小さくする
    private static bool FitDescription(TextMeshPro desc)
    {
        if (DescBaseSize <= 0f) return true;
        var size = desc.rectTransform.sizeDelta;
        desc.fontSize = DescBaseSize;
        for (int i = 0; i < 12 && desc.fontSize > 0.8f; i++)
        {
            if (desc.GetPreferredValues(desc.text, size.x, 0f).y <= size.y) return true;
            desc.fontSize *= 0.9f;
        }

        return desc.GetPreferredValues(desc.text, size.x, 0f).y <= size.y;
    }

    // 今のタブに当たるボタンだけを選んだ見た目に (検索の間はどれも選ばない)。選んだ区分のクルーは少し大きく持ち上げる
    private static void HighlightCat(int tabNum)
    {
        foreach (var cat in Cats)
        {
            if (!cat.Button) continue;
            bool on = cat.TabNum == tabNum;
            cat.Button.SelectButton(on);
            if (!cat.Art) continue;
            cat.Art.localScale = on ? cat.ArtScale * CatArtPicked : cat.ArtScale;
            cat.Art.localPosition = on ? cat.ArtPos + new Vector3(0f, cat.ArtLift, 0f) : cat.ArtPos;
        }
    }

    // 打つたびに全部の項目を照らして行を作り直すと重いので、打つ手が止まってから探す
    private const float SearchDelay = 0.25f;
    private static int searchSeq;

    private static void OnSearchChanged(string query)
    {
        int seq = ++searchSeq;
        LateTask.New(() =>
        {
            if (seq == searchSeq) ApplySearch(query);
        }, SearchDelay, log: false);
    }

    private static void ApplySearch(string query)
    {
        if (!OpenMenu) return;
        SettingsSearchBox.UpdateSuggestion(query);
        var q = query?.Trim() ?? "";
        // 英数字 1 文字ではほぼ全部の項目に当たるので探さない (日本語などは 1 文字でも役職の名前になる)
        if (q.Length == 1 && q[0] < 0x80) q = "";
        bool onSearch = SettingsTabs.TabIndex == SettingsTabs.SearchTab;
        if (SettingsSearch.Tokens(q).Length == 0)
        {
            searchQuery = "";
            if (onSearch) OpenMenu.ChangeTab(ReturnTab, false);
            return;
        }

        searchQuery = q;
        // 検索の結果を出している間は、頁の中身を替えて説明を書き替えるだけ (タブの出し直しはしない)
        if (onSearch && RolePage.Built)
        {
            RolePage.SetQuery(q);
            SetDescription(OpenMenu, SearchDescription());
            return;
        }

        if (!onSearch) ReturnTab = SettingsTabs.TabIndex;
        OpenMenu.ChangeTab(SettingsTabs.SearchTab, false);
    }

    private static string SearchDescription()
    {
        int count = RolePage.HitCount;
        return count == 0
            ? string.Format(Translator.GetString("MenuSearch.NoHit"), searchQuery)
            : string.Format(Translator.GetString("MenuSearch.Result"), searchQuery, count);
    }

    // ゲーム設定メニュー 初期関数 (本編の Start より前に左の列と検索欄を作る)
    [HarmonyPatch(nameof(GameSettingMenu.Start)), HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static void StartPrefix(GameSettingMenu __instance)
    {
        if (!SettingsUiMode.Active) return;

        // 前の画面が Close を通らずに消えていたら (退出・試合の開始など)、閉じた時と同じ後始末をここで済ませる
        if (OpenMenu) CloseMenu();
        SettingsTabs.TabIndex = 1;
        PresetChanged = false;
        OpenMenu = __instance;
        ReturnTab = 1;
        searchQuery = "";

        // 本編のタブのボタン 3 つと左上の題は出さない (左の列が同じ役目)
        foreach (var vanillaButton in new[] { __instance.GameSettingsButton, __instance.RoleSettingsButton, __instance.GamePresetsButton })
        {
            vanillaButton.gameObject.SetActive(false);
            __instance.ControllerSelectable.Remove(vanillaButton);
        }

        var gameSettingsLabel = __instance.transform.FindChild("GameSettingsLabel");
        if (gameSettingsLabel) gameSettingsLabel.gameObject.SetActive(false);

        // 本編の説明の箱は出さない。説明の字だけを左の列の下へ移す (列を作った後)
        var whatIsThis = __instance.transform.FindChild("What Is This?");
        if (whatIsThis) whatIsThis.gameObject.SetActive(false);

        // 検索欄: 板の上 (どの区分からでも全部を探す)
        var buttonParent = __instance.GameSettingsButton.transform.parent;
        var searchPos = new Vector3(SearchBoxPosition.x, SearchBoxPosition.y, __instance.GameSettingsButton.transform.localPosition.z);
        SearchBox = SettingsSearchBox.Create(buttonParent, searchPos, SearchBoxWidth, SearchBoxHeight,
            Translator.GetString("MenuSearch.Hint"), OnSearchChanged);

        // 左の列: プリセット・ゲーム・全般・役職の区分。今のモードに無い区分は RelayoutCats が隠す
        Cats.Clear();
        AddCat(__instance, null, SettingsTabs.PresetTab, presets: true);
        AddCat(__instance, null, 1);
        foreach (var tab in ColumnTabs)
            AddCat(__instance, tab, (int)tab + SettingsTabs.FirstModTab);
        __instance.DefaultButtonSelected = Cats[1].Button;
        RelayoutCats();
        PlaceDescription(__instance);
        // 開く時の拡大の動きが終わってから、説明を測り直して書き直す
        var opened = __instance;
        LateTask.New(() =>
        {
            if (OpenMenu && opened && OpenMenu.Pointer == opened.Pointer && lastDescText != null)
                SetDescription(opened, lastDescText, lastDescBelow);
        }, 0.3f, log: false);
        HighlightCat(SettingsTabs.TabIndex == SettingsTabs.SearchTab ? -1 : SettingsTabs.TabIndex);

        // 頁の置き場にする画面を作る (ゲーム設定の画面の複製)。全般の頁用と、役職の区分・検索の結果用・プリセット用
        ModTab = UnityEngine.Object.Instantiate(templateGameOptionsMenu, __instance.GameSettingsTab.transform.parent);
        ModTab.name = "SettingsPage";
        ModTab.gameObject.SetActive(false);
        RoleHost = UnityEngine.Object.Instantiate(templateGameOptionsMenu, __instance.GameSettingsTab.transform.parent);
        RoleHost.name = "RoleBrowser";
        RoleHost.gameObject.SetActive(false);
        PresetHost = UnityEngine.Object.Instantiate(templateGameOptionsMenu, __instance.GameSettingsTab.transform.parent);
        PresetHost.name = "PresetPage";
        PresetHost.gameObject.SetActive(false);
    }

    // ゲームの頁: 本編の ChangeTab が説明の字を本編の文に書き戻すので、その後に頁の操作の案内を付けて書き直す
    [HarmonyPatch(nameof(GameSettingMenu.ChangeTab)), HarmonyPostfix]
    private static void ChangeTabPostfix(GameSettingMenu __instance, int tabNum, [HarmonyArgument(1)] bool previewOnly)
    {
        if (!SettingsUiMode.Active) return;
        if (previewOnly || SettingsTabs.IsModTab(tabNum) || !TranslationController.Instance) return;
        SetDescription(__instance, TranslationController.Instance.GetString(StringNames.GameSettingsDescription)
            + "\n\n" + Translator.GetString("GameSettingHelpText.Page"));
    }

    [HarmonyPatch(nameof(GameSettingMenu.ChangeTab)), HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static bool ChangeTabPrefix(GameSettingMenu __instance, ref int tabNum, [HarmonyArgument(1)] bool previewOnly)
    {
        if (!SettingsUiMode.Active) return true;

        SettingsTabs.TabIndex = tabNum;
        bool isSearch = tabNum == SettingsTabs.SearchTab;

        // 検索の途中で区分を選んだら、検索の語を消してその区分を出す
        // (TabIndex を先に替えてあるので、語を消した時の「元のタブへ戻る」は走らない)
        if (!previewOnly && !isSearch && SearchBox != null && SearchBox.Query.Length > 0)
        {
            ReturnTab = tabNum;
            SearchBox.Clear();
            searchSeq++;
            searchQuery = "";
        }

        if ((previewOnly && Controller.currentTouchType == Controller.TouchType.Joystick) || !previewOnly)
        {
            if (ModTab) ModTab.gameObject.SetActive(false);
            if (RoleHost) RoleHost.gameObject.SetActive(false);
            if (PresetHost) PresetHost.gameObject.SetActive(false);
            HighlightCat(isSearch ? -1 : tabNum);
        }

        if (!previewOnly)
        {
            var whatIsThis = __instance.transform.FindChild("What Is This?");
            if (whatIsThis) whatIsThis.gameObject.SetActive(false);
            // 検索の結果の行など、頁の外で変えた値を頁へ写す
            SettingsPages.RefreshFromOutside();
        }

        if (!SettingsTabs.IsModTab(tabNum)) return true;

        if ((previewOnly && Controller.currentTouchType == Controller.TouchType.Joystick) || !previewOnly)
        {
            __instance.PresetsTab.gameObject.SetActive(false);
            __instance.GameSettingsTab.gameObject.SetActive(false);
            __instance.RoleSettingsTab.gameObject.SetActive(false);
            __instance.GamePresetsButton.SelectButton(false);
            __instance.GameSettingsButton.SelectButton(false);
            __instance.RoleSettingsButton.SelectButton(false);

            TabGroup? group = isSearch || tabNum == SettingsTabs.PresetTab ? null : (TabGroup)(tabNum - SettingsTabs.FirstModTab);
            bool modPage = group == TabGroup.SystemSettings;
            var shown = modPage ? ModTab : RoleHost;
            if (tabNum == SettingsTabs.PresetTab)
            {
                if (PresetHost)
                {
                    PresetHost.gameObject.SetActive(true);
                    PresetPage.Show();
                    SetDescription(__instance, Translator.GetString("MenuDescriptionText.Presets"));
                }
            }
            else if (shown)
            {
                // 役職の頁は区分と検索の語を先に渡す (初めて出す時は、出す時に頁が作られる)
                if (!modPage) RolePage.Prepare(group, isSearch ? searchQuery : "");
                shown.gameObject.SetActive(true);
                if (!modPage) RolePage.Apply();
                // 検索の結果の行など、頁の外で変えた値を頁へ写す
                SettingsPages.RefreshFromOutside();
                // 名前の「?」と打ち込みで操作するので、説明の操作の案内も頁用
                if (isSearch) SetDescription(__instance, SearchDescription());
                else
                    SetDescription(__instance,
                        Translator.GetString($"MenuDescriptionText.{group}") + (modPage ? "\n\n" + Translator.GetString("GameSettingHelpText.Page") : ""),
                        modPage ? 0f : RolePage.ChipsWorldHeight);
            }
        }

        if (previewOnly)
        {
            __instance.ToggleLeftSideDarkener(false);
            __instance.ToggleRightSideDarkener(true);
            return false;
        }

        __instance.ToggleLeftSideDarkener(true);
        __instance.ToggleRightSideDarkener(false);
        return false;
    }

    // 本編の OnEnable を差し替える。EK 版の OnEnable は、新しい画面の時は共通の後始末だけして素通りする
    [HarmonyPatch(nameof(GameSettingMenu.OnEnable)), HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static bool OnEnablePrefix(GameSettingMenu __instance)
    {
        SettingsUiMode.Latch();
        if (!SettingsUiMode.Active) return true;
        SettingsTheme.Show(__instance);

        if (!templateGameOptionsMenu)
        {
            templateGameOptionsMenu = UnityEngine.Object.Instantiate(__instance.GameSettingsTab, __instance.GameSettingsTab.transform.parent);
            templateGameOptionsMenu.gameObject.SetActive(false);
        }

        if (!templateGameSettingsButton)
        {
            templateGameSettingsButton = UnityEngine.Object.Instantiate(__instance.GameSettingsButton, __instance.GameSettingsButton.transform.parent);
            templateGameSettingsButton.gameObject.SetActive(false);
        }

        // 左の列は Start で作る。コントローラーで選べるのはその列だけにする
        __instance.ControllerSelectable = new();

        ControllerManager.Instance.OpenOverlayMenu(__instance.name, __instance.BackButton, __instance.DefaultButtonSelected, __instance.ControllerSelectable);
        if (HudManager.InstanceExists) HudManager.Instance.menuNavigationPrompts.SetActive(false);
        if (Controller.currentTouchType != Controller.TouchType.Joystick) __instance.ChangeTab(1, false);

        __instance.StartCoroutine(__instance.CoSelectDefault());

        return false;
    }

    // 数を打ち込んでいる間の Esc は、打ち込みをやめるだけ (設定画面は閉じない)
    [HarmonyPatch(nameof(GameSettingMenu.Close)), HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static bool ClosePrefix()
    {
        if (!SettingsUiMode.Active) return true;

        if (OptEditor.IsTyping)
        {
            OptEditor.EndInput(false);
            return false;
        }

        // 名前の欄などに字を打っている間の Esc は、欄を抜けるだけ
        if (TextField.Typing || TextField.CancelledFrame == Time.frameCount)
        {
            TextField.Blur();
            return false;
        }

        return true;
    }

    [HarmonyPatch(nameof(GameSettingMenu.Close)), HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void ClosePostfix(bool __runOriginal)
    {
        if (!SettingsUiMode.Active) return;

        // 打ち込みをやめるだけで閉じなかった時は、画面はそのまま (EK 版の Close の前置きが外した Backrooms の覆いを戻す)
        if (!__runOriginal)
        {
            EndKnot.Modules.BackroomsLobby.SetOverlaySuppressed(true);
            return;
        }

        CloseMenu();
        SettingsUiMode.Release();
    }

    private static void CloseMenu()
    {
        // 頁 (自前の部品) を全部捨てる。ホストは本編のゲーム設定を変えていれば保存して全員へ送る
        SettingsPages.Forget();
        SettingsPages.FlushSave(true);
        // ロビーを抜ける時は本編がホストを外してから画面を閉じる。書き換えはホストの間にしか起きないので、外れた後でも保存だけはする
        VanillaGame.OnMenuClosed(AmongUsClient.Instance && AmongUsClient.Instance.AmHost && SettingsPages.CanEdit());

        // 左の列のボタンに入れたデリゲートは画面を握り、GameObject を消しても画面全体が回収されなくなる。消す前に外す
        foreach (var cat in Cats)
        {
            try { cat.Button?.OnClick?.RemoveAllListeners(); }
            catch { /* 破棄済みの部品 */ }
        }

        Cats.Clear();
        UiActions.Release(CatSlots);
        if (ModTab) UnityEngine.Object.Destroy(ModTab);
        if (RoleHost) UnityEngine.Object.Destroy(RoleHost);
        if (PresetHost) UnityEngine.Object.Destroy(PresetHost);
        ModTab = null;
        RoleHost = null;
        PresetHost = null;
        SearchBox = null;
        searchSeq++;
        SettingsSearchBox.Forget();
        searchQuery = "";
        SettingsTheme.Hide();
        OpenMenu = null;
    }
}

// 設定画面の頁 (全般・役職・ゲーム) の置き場になる本編の画面 (GameOptionsMenu) の初期化
[HarmonyPatch(typeof(GameOptionsMenu))]
internal static class SettingsOptionsMenuPatch
{
    [HarmonyPatch(nameof(GameOptionsMenu.Initialize)), HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static bool InitializePrefix(GameOptionsMenu __instance)
    {
        if (!SettingsUiMode.Active) return true;
        if (!SettingsTabs.IsModTab(SettingsTabs.TabIndex)) return true;

        if (__instance.Children == null || __instance.Children.Count == 0)
        {
            __instance.MapPicker.gameObject.SetActive(false);
            __instance.Children = new Il2CppSystem.Collections.Generic.List<OptionBehaviour>();
            __instance.CreateSettings();
            __instance.cachedData = GameOptionsManager.Instance.CurrentGameOptions;
            for (int i = 0; i < __instance.Children.Count; i++)
            {
                OptionBehaviour optionBehaviour = __instance.Children[i];
                optionBehaviour.OnValueChanged = new Action<OptionBehaviour>(__instance.ValueChanged);
            }

            __instance.InitializeControllerNavigation();
        }

        return false;
    }

    [HarmonyPatch(nameof(GameOptionsMenu.CreateSettings)), HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static bool CreateSettingsPrefix(GameOptionsMenu __instance)
    {
        if (!SettingsUiMode.Active) return true;

        var gradient = __instance.transform.FindChild("Gradient");
        if (gradient)
        {
            gradient.localPosition = new Vector3(3.88f, -4.44f, -20f);
            gradient.localScale = new Vector3(0.449f, 0.3605f, 7.2125f);
        }

        var maskBg = __instance.MaskBg.transform;
        maskBg.localScale = new Vector3(6.6f, 5f, 0.56f);
        var maskArea = __instance.MaskArea.transform;
        maskArea.localScale = new Vector3(6.6f, 5f, 0.56f);

        if (!SettingsTabs.IsModTab(SettingsTabs.TabIndex)) return true;

        // 全般と役職・検索の結果は行を並べず、自前の頁にする (この画面は頁の置き場)
        try
        {
            if (SettingsMenuPatch.IsPresetHost(__instance)) PresetPage.Build(__instance);
            else if (SettingsMenuPatch.IsRoleHost(__instance)) RolePage.Build(__instance);
            else if (SettingsMenuPatch.IsModTabMenu(__instance)) SettingsPages.General.Build(__instance);
            else return true;
        }
        catch (Exception e) { Logger.Exception(e, "SettingsOptionsMenuPatch.Page"); }

        return false;
    }

    // 本編のゲーム設定のタブ: 本編の行は隠し、本編の設定を自前の頁で並べる
    [HarmonyPatch(nameof(GameOptionsMenu.CreateSettings)), HarmonyPostfix]
    private static void CreateSettingsPostfix(GameOptionsMenu __instance)
    {
        if (!SettingsUiMode.Active) return;
        if (SettingsTabs.IsModTab(SettingsTabs.TabIndex)) return;
        // 本編の OnEnable を差し替えているので、この時点では GameSettingMenu.Instance がまだ無いことがある
        var menu = GameSettingMenu.Instance ? GameSettingMenu.Instance : __instance.GetComponentInParent<GameSettingMenu>();
        if (!menu || !menu.GameSettingsTab || menu.GameSettingsTab.Pointer != __instance.Pointer) return;
        SettingsPages.GameTab = __instance;
        try { SettingsPages.Game.Build(__instance); }
        catch (Exception e) { Logger.Exception(e, "SettingsOptionsMenuPatch.GamePage"); }
    }
}
