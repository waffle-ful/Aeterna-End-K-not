// Based on https://github.com/haru03741-glitch/TownOfHost_L Modules/SettingsMenu/RolePage.cs (GPL-3.0),
// which is based on https://github.com/haru03741-glitch/More-Roles-Plus src/Options/RolePage.cs (GPL-3.0),
// which is based on https://github.com/Dolly1016/Nebula-Public NebulaPluginNova/Configuration/NebulaSettingMenu.cs (GPL-3.0)
using UnityEngine;

namespace EndKnot.SettingsUI;

// 設定画面の役職の区分 (インポスター・クルー・第三陣営・カヴン・ジャッカル・コンビネーション・属性・幽霊) と検索の結果の頁。
// 中身は RoleBrowser 1 つ。区分を切り替えても作り直さず、区分と検索の語だけを替える。
// 頁の置き場は本編の画面 (GameOptionsMenu) 1 枚。左の列で区分を選ぶと、同じ置き場に別の区分を出す。
internal static class RolePage
{
    // 頁の置き場と大きさ (全般・ゲームの頁と同じ)
    private static readonly Vector3 Center = new(4.41f, -2.2f, -6f);
    private const float W = 6.3f, H = 4.15f;
    // 絞り込みの札 (2 枚) を左の列の下に置くのに掛かる高さ (画面の座標)
    private const float ChipsHeight = 0.8f;

    private static readonly RoleBrowser Browser = new(W, H, "rolepage");

    private static GameObject _root;
    private static System.IntPtr _menu;
    private static bool _built;
    private static TabGroup _team = TabGroup.ImpostorRoles;
    private static string _query = "";
    private static bool _dirty;

    public static bool Built => _built && _root;

    public static int HitCount => Browser.HitCount;
    public static bool Searching => _query.Length > 0;

    // 絞り込みの札が左の列の下を取る高さ (世界の座標・検索の間は無し)
    public static float ChipsWorldHeight => Built && !Searching ? ChipsHeight * _root.transform.lossyScale.y : 0f;

    public static void Build(GameOptionsMenu m)
    {
        // 同じ画面を出し直しただけなら、そのまま使う (本編が画面を出すたびに組み立てを呼ぶ)
        if (Built && m && _menu == m.Pointer) return;
        Teardown();
        if (!m || !UiAssets.Box) return;
        // 画面の元の中身 (本編の行の一覧・スクロール) は使わない。コントローラーで閉じるボタンだけ残す
        for (int i = 0; i < m.transform.childCount; i++)
        {
            var c = m.transform.GetChild(i);
            if (c.name != "CloseButton") c.gameObject.SetActive(false);
        }
        _root = Widget.Node("SettingsUi_rolepage", m.transform, m.gameObject.layer);
        _root.transform.localPosition = Center;
        // 頁ごと本編の設定画面の上に描く (中のスクロール欄は自分の切り抜きを持つ)
        _root.AddComponent<UnityEngine.Rendering.SortingGroup>().sortingOrder = 5;
        SettingsCover.Add(_root);
        _menu = m.Pointer;
        _built = true;
        _dirty = false;
        Browser.ChipScreen = ChipScreen(m);
        Browser.Attach(new UiScreen(_root.transform) { MaxWidth = W, BaseOrder = 1 }, _team, _query);
    }

    // 絞り込みの札の置き場 = 左の列の下 (左の列の位置が分からなければ一覧の上に出す)
    private static UiScreen ChipScreen(GameOptionsMenu m)
    {
        if (!SettingsMenuPatch.CatFoot(out var topLeft, out float width)) return null;
        var t = m.transform;
        var go = Widget.Node("SettingsUi_rolechips", t, m.gameObject.layer);
        var local = t.InverseTransformPoint(topLeft);
        go.transform.localPosition = new Vector3(local.x, local.y, Center.z);
        go.AddComponent<UnityEngine.Rendering.SortingGroup>().sortingOrder = 5;
        SettingsCover.Add(go);
        float w = t.InverseTransformVector(new Vector3(width, 0f, 0f)).x;
        return new UiScreen(go.transform) { Anchor = Align.Start, MaxWidth = w, BaseOrder = 1 };
    }

    private static void Teardown()
    {
        Browser.Detach();
        if (_root) Object.Destroy(_root);
        _root = null;
        _menu = System.IntPtr.Zero;
        _built = false;
    }

    // 設定画面を閉じる時: 頁と、頁が持つ物を全部忘れる
    public static void Forget()
    {
        Teardown();
        _team = TabGroup.ImpostorRoles;
        _query = "";
        _dirty = false;
        RoleCatalog.Clear();
        SettingsSearch.ClearCache();
    }

    // 出す区分と検索の語を決める (画面を出す前に呼ぶ。team = null は今の区分のまま)。組み直しは Apply で
    public static void Prepare(TabGroup? team, string query)
    {
        query ??= "";
        if (team.HasValue && team.Value != _team)
        {
            _team = team.Value;
            _dirty = true;
        }
        if (query != _query)
        {
            _query = query;
            _dirty = true;
        }
    }

    // 画面を出した後: 区分か語が変わっていて、頁が既に作ってあれば組み直す (作りたてなら作る時に使っている)
    public static void Apply()
    {
        if (!_dirty) return;
        _dirty = false;
        if (Built) Browser.Show(_team, _query);
    }

    // 検索欄の語が変わった (空 = 検索をやめた)
    public static void SetQuery(string q)
    {
        Prepare(null, q);
        Apply();
    }

    // 外 (プリセット・コマンド・ほかの頁・取り消し) で値を変えた後
    public static void RefreshFromOutside()
    {
        if (!Built) return;
        // 自分の行を押して値を書き換えている最中は、その後始末が行う
        if (OptEditor.Busy > 0) return;
        Browser.RefreshFromOutside();
    }

    public static void Tick()
    {
        if (!Built) return;
        Browser.Tick();
    }
}
