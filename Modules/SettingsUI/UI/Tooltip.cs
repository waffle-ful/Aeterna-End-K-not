// Based on https://github.com/haru03741-glitch/TownOfHost_L Modules/SettingsMenu/UI/Tooltip.cs (GPL-3.0),
// which is based on https://github.com/haru03741-glitch/More-Roles-Plus src/UI/Tooltip.cs (GPL-3.0)
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

namespace EndKnot.SettingsUI;

// 部品の横に出す説明の札 (1 つだけ)。札はスクロール欄の外 (いちばん外の SortingGroup の直下) に置くので、欄の縁で切れない。
// 当てた/外れた・押した時にだけ動き、毎フレームの処理は持たない。作り直しで元の部品が消える時は UiScreen.Clear が消す
internal static class Tooltip
{
    private const float Width = 4.6f;
    private const int Order = 5000;

    private static GameObject _go;
    private static UiScreen _screen;
    private static Transform _anchor;

    public static bool ShownFor(Transform anchor) => _go && _anchor == anchor;

    public static void Show(Transform anchor, string text)
    {
        Hide();
        if (!anchor || string.IsNullOrEmpty(text)) return;
        var root = Root(anchor);
        _anchor = anchor;
        _go = new GameObject("Tooltip") { layer = anchor.gameObject.layer };
        _go.transform.SetParent(root, false);
        _screen = new UiScreen(_go.transform) { Anchor = Align.Start, MaxWidth = Width, BaseOrder = Order };
        var body = new Label(text, 1.25f) { Width = Width - 0.3f, TextAlign = TextAlignmentOptions.TopLeft };
        _screen.Set(new Box(body) { Padding = new Vector2(0.15f, 0.1f), Fill = new Color(0.06f, 0.06f, 0.08f, 0.96f) });

        // 部品の真下に、左の端を部品に合わせて出す。画面の右の端からはみ出す時は左へずらす (部品の行は隠さない)
        var p = root.InverseTransformPoint(anchor.position);
        // 下の端からはみ出す時は部品の真上へ
        float x = p.x - 0.2f, y = p.y - 0.22f;
        var cam = Camera.main;
        if (cam)
        {
            var lb = root.InverseTransformPoint(cam.ViewportToWorldPoint(new Vector3(0f, 0f, 0f)));
            var rt = root.InverseTransformPoint(cam.ViewportToWorldPoint(new Vector3(1f, 1f, 0f)));
            x = System.Math.Max(System.Math.Min(x, rt.x - 0.15f - _screen.LastSize.x), lb.x + 0.15f);
            if (y - _screen.LastSize.y < lb.y + 0.1f) y = p.y + 0.22f + _screen.LastSize.y;
        }
        _go.transform.localPosition = new Vector3(x, y, p.z - 1f);
    }

    // 先に控えを空にしてから片付ける (札の Clear が HideUnder 経由でここへ戻っても何もしないように)
    public static void Hide()
    {
        var screen = _screen;
        var go = _go;
        _screen = null;
        _go = null;
        _anchor = null;
        screen?.Clear();
        if (go) Object.Destroy(go);
    }

    // 作り直す画面の中に元の部品があれば消す (消えた部品の札を残さない)
    public static void HideUnder(Transform root)
    {
        if (_go == null && _anchor == null) return;
        if (!_anchor || !root || _anchor.IsChildOf(root)) Hide();
    }

    // いちばん外の SortingGroup (窓・設定画面の頁)。無ければ部品の親の根
    private static Transform Root(Transform t)
    {
        Transform found = null;
        for (var p = t; p; p = p.parent)
            if (p.GetComponent<SortingGroup>()) found = p;
        return found ? found : t.root;
    }
}
