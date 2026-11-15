// Based on https://github.com/haru03741-glitch/TownOfHost_L Modules/SettingsMenu/UI/TextField.cs (GPL-3.0),
// which is based on https://github.com/haru03741-glitch/More-Roles-Plus src/UI/TextField.cs (GPL-3.0)
using System;
using System.Text;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.Events;

namespace EndKnot.SettingsUI;

// 字を打ち込む欄 (本編のチャットの欄が無いメインメニューでも使える)。
// 押すと入力を受け、Input.inputString で字を足す。日本語の変換中の字 (IME) は下線で出す。
// Ctrl+V で貼り付け・Backspace で消す・Enter は 1 行の欄なら OnSubmit、複数行の欄なら改行 (Ctrl+Enter で OnSubmit)。
// 欄の外を押すと入力をやめる。入力中は Typing が true (本編の移動やキーの割り当てを止める)
internal sealed class TextField : Widget
{
    public string Value = "";
    public string Hint = "";
    public int MaxLength = 200;
    public bool Multiline;
    public Vector2 Size;
    public float FontSize = 1.4f;
    public Action<string> OnChange;
    public Action OnSubmit;
    public Action OnBlur;      // 入力をやめた時 (欄の外を押した・別の欄へ移った・欄が消えた)
    public Action OnCancel;    // Esc (null なら Esc は何もしない)
    public bool Selected;      // 字が全部選ばれている (最初に打った字・貼った字・Backspace で前の字を消す)

    private static readonly Color Field = new(0.07f, 0.075f, 0.095f, 1f), Edge = new(0.24f, 0.26f, 0.32f, 1f), EdgeFocus = new(0f, 0.9f, 0.46f, 1f);
    private const float Pad = 0.12f;

    private static TextField _focused;
    private static TouchScreenKeyboard _keyboard;
    private static bool _clickedField;
    private TextMeshPro _text;
    private SpriteRenderer _edge;
    private GameObject _root;

    public static bool Typing => _focused != null && _focused._root;
    public static TextField Focused => _focused;

    public TextField(Vector2 size, string value = "", string hint = "")
    {
        Size = size;
        Value = value ?? "";
        Hint = hint ?? "";
    }

    public override GameObject Build(Transform parent, BuildCtx ctx, float maxWidth, out Vector2 size)
    {
        var go = Node("TextField", parent, ctx.Layer);
        _root = go;
        size = Size;
        float r = Math.Min(0.12f, Size.y / 2f);
        // 縁 (Back) と地 (Fill)
        var edge = Node(UiScreen.BackName, go.transform, ctx.Layer).AddComponent<SpriteRenderer>();
        edge.sprite = MenuArt.Round(r);
        edge.drawMode = SpriteDrawMode.Sliced;
        edge.size = Size;
        edge.color = Edge;
        _edge = edge;
        var fill = Node(UiScreen.FillName, edge.transform, ctx.Layer).AddComponent<SpriteRenderer>();
        fill.transform.localPosition = new Vector3(0f, 0f, -0.01f);
        fill.sprite = MenuArt.Round(Math.Max(r - 0.03f, 0.01f));
        fill.drawMode = SpriteDrawMode.Sliced;
        fill.size = new Vector2(Size.x - 0.06f, Size.y - 0.06f);
        fill.color = Field;

        var proto = UiAssets.TextProto;
        if (proto)
        {
            var t = UnityEngine.Object.Instantiate(proto, go.transform);
            t.name = "Text";
            t.gameObject.layer = ctx.Layer;
            t.gameObject.SetActive(true);
            t.transform.localPosition = new Vector3(0f, 0f, -0.02f);
            t.transform.localScale = Vector3.one;
            var rt = t.rectTransform;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(Size.x - Pad * 2f, Size.y - Pad * 1.2f);
            t.enableAutoSizing = false;
            t.margin = Vector4.zero;
            t.fontSize = FontSize;
            t.fontStyle = FontStyles.Normal;
            t.richText = true;
            t.enableWordWrapping = Multiline;
            t.alignment = Multiline ? TextAlignmentOptions.TopLeft : TextAlignmentOptions.Left;
            _text = t;
        }

        var col = go.AddComponent<BoxCollider2D>();
        col.size = Size;
        var pb = go.AddComponent<PassiveButton>();
        pb.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
        pb.OnMouseOver = new UnityEvent();
        pb.OnMouseOut = new UnityEvent();
        if (ctx.ClickMask) pb.ClickMask = ctx.ClickMask;
        pb.OnClick.AddListener(UiActions.Take(Focus, ctx.Slots));
        Refresh();
        return go;
    }

    public void Focus()
    {
        _clickedField = true;
        if (_focused == this) return;
        var old = _focused;
        _focused = this;
        old?.Refresh();
        old?.OnBlur?.Invoke();
        if (OperatingSystem.IsAndroid())
        {
            // Android の libunity には Input.inputString が無いので、画面の上のキーボードで打つ
            Selected = false;
            _keyboard = TouchScreenKeyboard.Open(Value, TouchScreenKeyboardType.Default, false, Multiline, false, false, "", MaxLength);
        }
        else Input.imeCompositionMode = IMECompositionMode.On;

        Refresh();
    }

    // Esc で入力をやめたフレーム (同じ Esc で画面まで閉じないよう、閉じる側が見る)
    public static int CancelledFrame { get; private set; } = -1;

    public static void Blur()
    {
        var old = _focused;
        _focused = null;
        if (OperatingSystem.IsAndroid())
        {
            if (_keyboard != null) _keyboard.active = false;
            _keyboard = null;
        }
        else Input.imeCompositionMode = IMECompositionMode.Auto;

        old?.Refresh();
        old?.OnBlur?.Invoke();
    }

    public void Set(string v)
    {
        Value = Clip(v ?? "");
        Refresh();
        OnChange?.Invoke(Value);
    }

    private string Clip(string v)
    {
        if (!Multiline) v = v.Replace("\r", "").Replace("\n", " ");
        else v = v.Replace("\r\n", "\n").Replace('\r', '\n');
        return v.Length > MaxLength ? v.Substring(0, MaxLength) : v;
    }

    private static string Escape(string s) => s.Replace("<", "<​");

    private void Refresh()
    {
        if (!_text) return;
        bool focused = _focused == this;
        if (_edge) _edge.color = focused ? EdgeFocus : Edge;
        string comp = focused && !OperatingSystem.IsAndroid() ? Input.compositionString ?? "" : "";
        if (Value.Length == 0 && comp.Length == 0 && !focused)
        {
            _text.text = $"<color=#7E8592>{Escape(Hint)}</color>";
            return;
        }
        bool caret = focused && (int)(Time.realtimeSinceStartup * 2f) % 2 == 0;
        string tail = (comp.Length > 0 ? $"<u><color=#FFE082>{Escape(comp)}</color></u>" : "") + (caret ? "<color=#00E676>|</color>" : "<alpha=#00>|");
        // 入り切らない時は前を落として、打っている所 (最後) を見せる
        string body = Value;
        float w = Size.x - Pad * 2f, h = Size.y - Pad * 1.2f;
        if (Selected && focused && comp.Length == 0)
        {
            _text.text = $"<mark=#00E67666>{Escape(body)}</mark>" + tail;
            return;
        }
        string shown = Escape(body) + tail;
        _text.text = shown;
        int cut = 0;
        while (cut < body.Length && Overflow(shown, w, h))
        {
            cut += Math.Max(1, (body.Length - cut) / 8);
            shown = "…" + Escape(body.Substring(Math.Min(cut, body.Length))) + tail;
        }
        _text.text = shown;
    }

    private bool Overflow(string s, float w, float h)
    {
        if (!Multiline) return _text.GetPreferredValues(s).x > w;
        return _text.GetPreferredValues(s, w, 0f).y > h;
    }

    // 毎フレーム (入力を受ける間だけ。呼び出し元の Update から)
    public static void Tick()
    {
        var f = _focused;
        if (f == null) { _clickedField = false; return; }
        if (!f._root || !f._root.activeInHierarchy) { Blur(); return; }
        // 欄の外を押したらやめる (欄を押した時はボタンの処理が同じフレームか次で Focus を呼ぶ)
        if (Input.GetMouseButtonUp(0))
        {
            if (!_clickedField) { Blur(); return; }
        }
        if (Input.GetMouseButtonDown(0)) _clickedField = false;

        if (f.OnCancel != null && Input.GetKeyDown(KeyCode.Escape)) { CancelledFrame = Time.frameCount; f.OnCancel(); return; }
        if (OperatingSystem.IsAndroid()) { TickAndroid(f); return; }
        bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
        string v = f.Value;
        bool changed = false, submit = false;
        if (f.Selected)
        {
            // Enter だけなら選んだまま (そのまま決める)
            string typed = (Input.inputString ?? "").Replace("\r", "").Replace("\n", "");
            if ((ctrl && Input.GetKeyDown(KeyCode.V)) || typed.Length > 0 || !string.IsNullOrEmpty(Input.compositionString))
            {
                f.Selected = false;
                // 選んだ字は消える (Backspace はそれで終わり: 空の字からは消す物が無い)
                v = "";
                changed = true;
            }
        }
        if (ctrl && Input.GetKeyDown(KeyCode.V))
        {
            v += GUIUtility.systemCopyBuffer ?? "";
            changed = true;
        }
        else
        {
            var sb = new StringBuilder(v);
            foreach (char c in Input.inputString ?? "")
            {
                if (c == '\b') { if (sb.Length > 0) sb.Length--; changed = true; }
                else if (c == '\n' || c == '\r')
                {
                    if (f.Multiline && !ctrl) { sb.Append('\n'); changed = true; }
                    else submit = true;
                }
                else if (!char.IsControl(c)) { sb.Append(c); changed = true; }
            }
            v = sb.ToString();
        }
        if (changed)
        {
            f.Value = f.Clip(v);
            f.OnChange?.Invoke(f.Value);
        }
        f.Refresh();
        if (submit) f.OnSubmit?.Invoke();
    }

    // Android: 画面の上のキーボードの字を欄へ写す。決定で OnSubmit・取り消しで OnCancel (無ければ欄を抜ける)
    private static void TickAndroid(TextField f)
    {
        var kb = _keyboard;
        if (kb == null) { Blur(); return; }
        var status = kb.status;
        if (status == TouchScreenKeyboard.Status.Visible || status == TouchScreenKeyboard.Status.Done)
        {
            string t = kb.text ?? "";
            if (t != f.Value)
            {
                f.Value = f.Clip(t);
                f.OnChange?.Invoke(f.Value);
            }

            f.Refresh();
        }

        if (status == TouchScreenKeyboard.Status.Done) f.OnSubmit?.Invoke();
        else if (status == TouchScreenKeyboard.Status.Canceled || status == TouchScreenKeyboard.Status.LostFocus)
        {
            if (f.OnCancel != null) { CancelledFrame = Time.frameCount; f.OnCancel(); }
            else Blur();
        }
    }

    // 打っている間は本編の移動 (WASD・矢印) を止める
    [HarmonyPatch(typeof(KeyboardJoystick), nameof(KeyboardJoystick.Update))]
    private static class MovePatch
    {
        public static bool Prefix() => !Typing;
    }
}
