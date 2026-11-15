// Based on https://github.com/haru03741-glitch/TownOfHost_L Modules/SettingsMenu/OptEditor.cs (GPL-3.0),
// which is based on https://github.com/haru03741-glitch/More-Roles-Plus src/Options/OptEditor.cs (GPL-3.0),
// which is based on https://github.com/Dolly1016/Nebula-Public NebulaPluginNova/Configuration/ConfigurationVariations.cs (GPL-3.0)
using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace EndKnot.SettingsUI;

// 設定項目 1 つの編集の行 (「名前 : 値」と -/+、ON/OFF は押すと切り替わるスイッチ)。
// 値は MenuOpt の 1 か所だけ。値を変えた時は字と縁の色だけ書き換え、行を作り直さない。
// 出る行が変わった時だけ onShownChanged を呼ぶ。
internal sealed class OptEditor
{
    public static readonly Color OnColor = new(0.35f, 1f, 0.45f, 1f);
    public static readonly Color OffColor = new(1f, 0.38f, 0.38f, 1f);
    public static readonly Color Dim = new(0.45f, 0.45f, 0.48f, 1f);
    // 既定から変えた値の字
    public static readonly Color ChangedColor = new(1f, 0.72f, 0.3f, 1f);
    public const float NameW = 2.72f, ValueW = 1.25f, FontSize = 1.7f, StepW = 0.42f, RowH = 0.4f, ResetW = 0.6f;
    // ± を押し続けた時に 1 つ進める間隔 (秒)
    private const float RepeatSeconds = 0.12f;
    // Shift を押しながら ± を押した時の幅
    private const int ShiftStep = 5;

    private readonly List<(MenuOpt opt, Label name, Label value, Button toggle, Button minus, Button plus, Button reset)> _rows = new();

    // 項目の名前の右の小さい点 (初期値から変えた項目・値の字は白のまま)
    internal static readonly string ChangedMark = $"<size=60%> <color=#{ColorUtility.ToHtmlStringRGB(ChangeDot.Color)}>●</color></size>";

    private static string NameText(MenuOpt opt, string label = null) => (label ?? opt.Label) + (opt.Changed ? ChangedMark : "");
    // 名前の字を差し替えた行 (検索で当たった所に色を付けた名前)。名前の部品 → 字
    private readonly Dictionary<Label, string> _labels = new();
    private readonly List<MenuOpt> _watched = new();
    private bool[] _shown;
    private readonly Action _onShownChanged;
    private readonly Action<MenuOpt> _onChanged;

    // canEdit = 押した時に、まだ値を変えてよいか (開いた後に試合が始まった時など)
    private readonly Func<bool> _canEdit;

    // 値を書き換えている間は 0 でない。その間に外から来る「値が変わった」の通知では、頁を作り直さない
    // (押したボタンごと頁を作り直すと、この編集の後始末が消えた行を触るため。作り直しは編集の後始末が 1 回だけ行う)
    internal static int Busy;

    public OptEditor(Action onShownChanged, Action<MenuOpt> onChanged = null, Func<bool> canEdit = null)
    {
        _onShownChanged = onShownChanged;
        _onChanged = onChanged;
        _canEdit = canEdit;
    }

    // 出る行が変わったかを見張る項目 (並べる候補を全部)。Rows を組む前に渡す
    public void Watch(IEnumerable<MenuOpt> opts)
    {
        _watched.Clear();
        _watched.AddRange(opts);
        _shown = new bool[_watched.Count];
        for (int i = 0; i < _watched.Count; i++) _shown[i] = _watched[i].Shown;
    }

    public void Clear()
    {
        _rows.Clear();
        _labels.Clear();
    }

    // 値を 1 つ動かす。Shift を押している時は 5 倍幅 (端で止まる)
    public static void Step(MenuOpt opt, int delta)
    {
        bool shift = !opt.IsToggle && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift));
        Busy++;
        try
        {
            if (opt.Step(shift ? delta * ShiftStep : delta, shift)) AfterStep(opt);
        }
        finally { Busy--; }
    }

    // 押した以外の道 (既定へ戻す・取り消し) で値を変えた後も同じ後始末
    public static void AfterStep(MenuOpt opt)
    {
        SettingsPages.AfterEdited(opt);
        SettingsPages.RefreshMarks();
    }

    // 狭い欄 (設定画面の右の欄) の行: 字を小さく・「初期値」のボタンは無し (変えた項目は名前の右の点で分かり、戻すのは欄の下のボタン)。
    // 名前が 1 行に入らない時は、名前を上の段 (幅いっぱい)・値を下の段の右に置く
    public const float NarrowFont = 1.35f, NarrowStepW = 0.3f, NarrowValueW = 0.74f, NarrowRowH = 0.34f;

    // ON/OFF はスイッチ (編集できない時は字だけ)
    public Widget NarrowRow(MenuOpt opt, bool editable, float width, float indent = 0f, Color? nameColor = null)
    {
        opt.Sync();
        editable &= !opt.Fixed;
        var info = InfoButton(opt);
        const float gap = 0.06f;
        float controls = opt.IsToggle ? (editable ? Switch.W : NarrowValueW) : NarrowStepW * 2f + NarrowValueW + gap * 2f;
        float infoW = info != null ? InfoW + gap : 0f;
        float nameW = width - indent - controls - gap - infoW;
        bool twoLines = Label.Measure(opt.Label, NarrowFont, true) > nameW;
        if (twoLines) nameW = width - indent - infoW;
        var name = new Label(NameText(opt), NarrowFont) { Width = nameW, NoWrap = !twoLines, Bold = true, Color = nameColor ?? Color.white };
        var head = new Stack(true) { Gap = gap, Align = Align.Start };
        if (indent > 0f) head.Add(new Spacer(indent - gap, 0.1f));
        head.Add(name);
        if (info != null) head.Add(info);
        // 値の部品を足す先 (1 行なら名前の右・2 段なら下の段の右寄せ)
        Widget row = head;
        var ctl = head;
        if (twoLines)
        {
            ctl = new Stack(true) { Gap = gap, Align = Align.Start };
            ctl.Add(new Spacer(width - controls - gap, 0.1f));
            row = new Stack(false, head, ctl) { Gap = 0.03f, Width = width, Align = Align.Start };
        }

        if (opt.IsToggle)
        {
            if (editable)
            {
                var sw = new Switch(opt.Index != 0, () => Changed(opt, 1));
                ctl.Add(sw);
                _rows.Add((opt, name, null, sw, null, null, null));
                return row;
            }
            var value = new Label(opt.Display(), NarrowFont) { Width = controls, TextAlign = TextAlignmentOptions.Center, Bold = true, Color = ToggleColor(opt), NoWrap = true, MinFontSize = 0.7f };
            ctl.Add(value);
            _rows.Add((opt, name, value, null, null, null, null));
            return row;
        }

        var val = new Label(opt.Display(), NarrowFont) { Width = editable ? NarrowValueW : controls, TextAlign = TextAlignmentOptions.Center, Bold = true, Color = Color.white, NoWrap = true, MinFontSize = 0.7f };
        if (editable) Typable(opt, val, NarrowRowH);
        if (!editable)
        {
            ctl.Add(val);
            _rows.Add((opt, name, val, null, null, null, null));
            return row;
        }
        var minus = new Button("-", () => Changed(opt, -1), NarrowFont) { Size = new Vector2(NarrowStepW, NarrowRowH), Repeat = RepeatSeconds };
        var plus = new Button("+", () => Changed(opt, 1), NarrowFont) { Size = new Vector2(NarrowStepW, NarrowRowH), Repeat = RepeatSeconds };
        ctl.Add(minus).Add(val).Add(plus);
        _rows.Add((opt, name, val, null, minus, plus, null));
        return row;
    }

    // 行の幅 (字下げ・? のボタン無し)。名前・「:」・- 値 +・「初期値」と、その間の 5 つの隙間
    private const float RowW = NameW + 0.2f + StepW * 2f + ValueW + ResetW + 0.08f * 5f;

    // width = 行に使える幅 (0 = 名前の欄を NameW のまま)。渡した時は名前の欄を縮めて、字下げしても幅に収める
    // label = 名前の字の差し替え (null = opt.Label)
    public Widget Row(MenuOpt opt, bool editable, float indent = 0f, Color? nameColor = null, float width = 0f, string label = null)
    {
        opt.Sync();
        editable &= !opt.Fixed;
        var info = InfoButton(opt);
        float nameW = NameW - indent - (info != null ? InfoW + 0.08f : 0f);
        if (width > 0f) nameW -= RowW - width + (indent > 0f ? 0.08f : 0f);
        var name = new Label(NameText(opt, label), FontSize) { Width = nameW, Bold = true, Color = nameColor ?? Color.white };
        if (label != null) _labels[name] = label;
        var colon = new Label(":", FontSize) { Width = 0.2f, TextAlign = TextAlignmentOptions.Center };
        var row = new Stack(true) { Gap = 0.08f, Align = Align.Start };
        if (indent > 0f) row.Add(new Spacer(indent, 0.1f));
        row.Add(name);
        if (info != null) row.Add(info);
        row.Add(colon);

        if (opt.IsToggle)
        {
            var value = new Label(opt.Display(), FontSize) { Width = ValueW - 0.2f, TextAlign = TextAlignmentOptions.Center, Bold = true, Color = ToggleColor(opt), NoWrap = true, MinFontSize = 0.7f };
            Button toggle = null, treset = null;
            if (editable)
            {
                toggle = new Button(value, () => Changed(opt, 1)) { Size = new Vector2(ValueW, RowH), Color = ToggleColor(opt) };
                row.Add(toggle);
                treset = ResetButton(opt);
                // 「初期値」を数値の行 (- 値 +) と同じ列に置く
                if (treset != null) row.Add(new Spacer(StepW * 2f + 0.08f, 0.1f)).Add(treset);
            }
            else row.Add(value);
            _rows.Add((opt, name, value, toggle, null, null, treset));
            return row;
        }

        var val = new Label(opt.Display(), FontSize) { Width = ValueW, TextAlign = TextAlignmentOptions.Center, Bold = true, Color = Color.white, NoWrap = true, MinFontSize = 0.7f };
        if (editable) Typable(opt, val, RowH);
        if (!editable)
        {
            row.Add(val);
            _rows.Add((opt, name, val, null, null, null, null));
            return row;
        }
        var minus = new Button("-", () => Changed(opt, -1), FontSize) { Size = new Vector2(StepW, RowH - 0.04f), Repeat = RepeatSeconds };
        var plus = new Button("+", () => Changed(opt, 1), FontSize) { Size = new Vector2(StepW, RowH - 0.04f), Repeat = RepeatSeconds };
        row.Add(minus).Add(val).Add(plus);
        var reset = ResetButton(opt);
        if (reset != null) row.Add(reset);
        _rows.Add((opt, name, val, null, minus, plus, reset));
        return row;
    }

    private const float InfoW = 0.3f;

    // 行の地: 角の丸い板で包み、1 行おきに少し明るくする (k = 節の中で何行目か)
    public const float PlatePad = 0.08f;
    // 地と縞の明るさ: 板の黒 (15) と地、地と縞の差をそれぞれコントラスト比 1.3 ほどに (淡いと暗い画面で縞が消える)
    private static readonly Color PlateColor = new(0.16f, 0.17f, 0.2f, 1f), StripeColor = new(0.23f, 0.24f, 0.28f, 1f);

    public static Widget Plated(Widget row, float width, int k)
    {
        row.Align = Align.Start;
        return new Plate(row) { Width = width, Padding = new Vector2(PlatePad, 0.025f), Align = Align.Start, Color = k % 2 == 1 ? StripeColor : PlateColor };
    }

    // 数値の値の欄: すばやく 2 回押すと字を打ち込む欄になる (Enter か欄の外を押すと決める・Esc でやめる)。
    // 打った数は範囲の中のいちばん近い、± で選べる値に寄せる (全角・単位付きでもよい)
    private void Typable(MenuOpt opt, Label val, float h)
    {
        if (!opt.Numeric) return;
        val.HitHeight = h;
        val.OnDoubleClick = l => BeginInput(opt, l, h);
    }

    private sealed class Typing
    {
        public OptEditor Editor;
        public MenuOpt Opt;
        public Label Label;
        public TextField Field;
        public GameObject Go;
        public readonly List<int> Slots = new();
    }

    private static Typing _typing;

    private void BeginInput(MenuOpt opt, Label label, float h)
    {
        if (_canEdit != null && !_canEdit()) return;
        var t = label.Built;
        if (!t) return;
        EndInput(false);
        var e = new Typing { Editor = this, Opt = opt, Label = label };
        var ctx = new BuildCtx(e.Slots, t.gameObject.layer) { ClickMask = label.ClickMask };
        float w = Math.Max(t.rectTransform.sizeDelta.x, 0.6f);
        var field = new TextField(new Vector2(w, h - 0.02f), opt.InputText()) { FontSize = label.FontSize, MaxLength = 16, Selected = true };
        var go = field.Build(t.transform.parent, ctx, w, out _);
        go.transform.localPosition = t.transform.localPosition + new Vector3(0f, 0f, -0.05f);
        UiScreen.Order(go.transform, t.sortingOrder + 1);
        // スクロール欄の中なら、欄の外へはみ出さないよう行と同じく切り抜く
        if (label.ClickMask) ScrollView.Masked(go.transform);
        e.Field = field;
        e.Go = go;
        _typing = e;
        t.gameObject.SetActive(false);
        field.OnSubmit = () => EndInput(true);
        field.OnBlur = () => EndInput(true);
        field.OnCancel = () => EndInput(false);
        field.Focus();
    }

    // 打ち込む欄を閉じる。commit なら打った数を値にする (欄が見えなくなった時はやめる)
    internal static void EndInput(bool commit)
    {
        var e = _typing;
        if (e == null) return;
        _typing = null;
        e.Field.OnBlur = null;
        if (TextField.Focused == e.Field) TextField.Blur();
        bool alive = e.Go && e.Go.activeInHierarchy;
        UiActions.Release(e.Slots);
        if (e.Go)
        {
            if (PassiveButtonManager.InstanceExists && PassiveButtonManager.Instance.currentOver
                && PassiveButtonManager.Instance.currentOver.transform.IsChildOf(e.Go.transform))
                PassiveButtonManager.Instance.currentOver = null;
            UnityEngine.Object.Destroy(e.Go);
        }
        if (e.Label.Built) e.Label.Built.gameObject.SetActive(true);
        if (commit && alive) e.Editor.Typed(e.Opt, e.Field.Value);
        else e.Editor.Refresh();
    }

    // 打ち込み中か (設定画面を閉じる Esc は、欄をやめるだけにする)
    internal static bool IsTyping => _typing != null;

    private void Typed(MenuOpt opt, string input)
    {
        if (_canEdit != null && !_canEdit()) { Refresh(); return; }
        int index = opt.IndexForInput(input);
        int before = opt.Index;
        if (index < 0) { Refresh(); return; }
        Busy++;
        try
        {
            if (!opt.Restore(index)) { Refresh(); return; }
            AfterStep(opt);
        }
        finally { Busy--; }
        OptHistory.Push(opt, before);
        Edited(opt);
    }

    private static readonly bool Touch = OperatingSystem.IsAndroid();

    // 項目に長い説明がある時だけ、名前の後ろに「?」の箱。PC は当てると・スマホは押すと札が出る
    private static Button InfoButton(MenuOpt opt)
    {
        if (!opt.HasDetail) return null;
        Button b = null;
        b = new Button("?", () =>
        {
            if (!Touch) return;
            var t = b.BackRenderer ? b.BackRenderer.transform : null;
            if (Tooltip.ShownFor(t)) Tooltip.Hide();
            else Tooltip.Show(t, opt.Detail());
        }, 1.2f)
        {
            Size = new Vector2(InfoW, InfoW),
            Padding = Vector2.zero,
            Color = InfoColor,
            OnHover = () => { if (!Touch && b.BackRenderer) Tooltip.Show(b.BackRenderer.transform, opt.Detail()); },
            OnUnhover = () => { if (!Touch) Tooltip.Hide(); },
        };
        return b;
    }

    private static readonly Color InfoColor = new(0.55f, 0.8f, 1f, 1f);

    // 既定へ戻すボタン (既定の時は暗く、押しても何もしない)。既定を持たない項目には付けない
    private Button ResetButton(MenuOpt opt) =>
        opt.HasDefault ? new Button(Translator.GetString("SettingsUI.Reset"), () => Reset(opt), 1.15f) { Size = new Vector2(ResetW, RowH - 0.04f), Color = opt.Changed ? ChangedColor : Dim } : null;

    private void Reset(MenuOpt opt)
    {
        if (_canEdit != null && !_canEdit()) return;
        int before = opt.Index;
        Busy++;
        try
        {
            if (!opt.ResetToDefault()) return;
            AfterStep(opt);
        }
        finally { Busy--; }
        OptHistory.Push(opt, before);
        Edited(opt);
    }

    private static Color ToggleColor(MenuOpt opt) => opt.Index != 0 ? OnColor : OffColor;

    private void Changed(MenuOpt opt, int delta)
    {
        if (_canEdit != null && !_canEdit()) return;
        opt.Sync();
        int before = opt.Index;
        Step(opt, delta);
        if (opt.Index != before) OptHistory.Push(opt, before);
        Edited(opt);
    }

    private void Edited(MenuOpt opt)
    {
        _onChanged?.Invoke(opt);
        if (ShownChanged()) { _onShownChanged?.Invoke(); return; }
        Refresh();
    }

    // 出る行が変わったか (外から値が変わった後の比べ直しにも使う)
    internal bool ShownChanged()
    {
        if (_shown == null) return false;
        bool changed = false;
        for (int i = 0; i < _watched.Count; i++)
        {
            bool s = _watched[i].Shown;
            if (s != _shown[i]) changed = true;
            _shown[i] = s;
        }
        return changed;
    }

    // 並んでいる行の字を今の値に (外から値を変えた後も)
    public void Refresh()
    {
        foreach (var (opt, name, value, toggle, minus, plus, reset) in _rows)
        {
            opt.Sync();
            bool changed = opt.Changed;
            reset?.SetColor(changed ? ChangedColor : Dim);
            var nt = name.Built;
            string ns = NameText(opt, _labels.TryGetValue(name, out var lb) ? lb : null);
            if (nt && ns != name.Content) { name.Content = ns; nt.text = ns; }
            if (toggle is Switch sw)
            {
                sw.SetOn(opt.Index != 0);
                continue;
            }
            var t = value.Built;
            value.SetText(opt.Display());
            if (opt.IsToggle)
            {
                if (t) t.color = ToggleColor(opt);
                toggle?.SetColor(ToggleColor(opt));
                continue;
            }
            // 数値は端で止まるので、それ以上動かない側を暗くする
            minus?.SetColor(opt.WrapsAround || opt.Index > 0 ? Color.white : Dim);
            plus?.SetColor(opt.WrapsAround || opt.Index < opt.Count - 1 ? Color.white : Dim);
        }
    }
}

// 設定の値の取り消しの履歴。複数の頁が同じ項目を編集できるので 1 か所に持つ。
// 1 回の操作 (行の 1 押し・まとめて変える) が 1 段
internal static class OptHistory
{
    private const int Max = 50;
    private static readonly List<List<(MenuOpt opt, int index)>> Steps = new();
    private static List<(MenuOpt opt, int index)> _group;

    public static bool Any => Steps.Count > 0;

    public static void Clear()
    {
        Steps.Clear();
        _group = null;
        _lastAt = -100f;
    }

    // 変える前の番号を積む。BeginGroup と EndGroup の間は 1 段にまとめる
    public static void Push(MenuOpt opt, int before)
    {
        if (_group != null) { _group.Add((opt, before)); return; }
        // 同じ項目を続けて動かした分 (± の長押し・連打) は 1 段にまとめ、最初の値へ戻す
        float now = Time.unscaledTime;
        bool same = Steps.Count > 0 && Steps[^1].Count == 1 && Steps[^1][0].opt == opt && now - _lastAt < MergeSeconds;
        _lastAt = now;
        if (same) return;
        Add(new List<(MenuOpt, int)> { (opt, before) });
    }

    private const float MergeSeconds = 0.6f;
    private static float _lastAt = -100f;

    public static void BeginGroup() => _group = new List<(MenuOpt, int)>();

    public static void EndGroup()
    {
        var g = _group;
        _group = null;
        if (g != null && g.Count > 0) Add(g);
    }

    private static void Add(List<(MenuOpt, int)> step)
    {
        if (Steps.Count >= Max) Steps.RemoveAt(0);
        Steps.Add(step);
    }

    // 最後の 1 段を戻す。戻した項目を返す (無ければ空)
    public static List<MenuOpt> Undo()
    {
        var done = new List<MenuOpt>();
        if (Steps.Count == 0) return done;
        var step = Steps[^1];
        Steps.RemoveAt(Steps.Count - 1);
        _lastAt = -100f;
        for (int i = step.Count - 1; i >= 0; i--)
        {
            var (opt, index) = step[i];
            if (opt.Restore(index)) OptEditor.AfterStep(opt);
            done.Add(opt);
        }
        return done;
    }
}
