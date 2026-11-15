// Based on https://github.com/haru03741-glitch/TownOfHost_L Modules/SettingsMenu/SettingsSearchBox.cs (GPL-3.0),
// which is based on https://github.com/haru03741-glitch/More-Roles-Plus src/Options/SearchBox.cs (GPL-3.0),
// which is based on https://github.com/Gurge44/EndlessHostRoles Patches/GameOptionsMenuPatch.cs (GPL-3.0),
// whose search bar is credited to Drakos (https://github.com/EnhancedNetwork/TownofHost-Enhanced pull request #1115, GPL-3.0)
using System;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.Events;

namespace EndKnot.SettingsUI;

// 設定画面の検索欄。本編のチャット入力欄を複製し、文字入力の部品 (TextBoxTMP) だけを生かして使う。
// - 複製したら、チャットへ送る部品 (FreeChatInputField・送信ボタン) は動き出す前に外す。
//   残すと Enter や送信ボタンで検索語がチャットに流れたり、文字数の表示を書き換えに来たりする。
// - 文字が変わったことは TextBoxTMP.SetText の後置で受け取る (欄の OnChange に管理側のデリゲートを入れない)。
//   受け取った文字は控えに持ち、本編の欄の text を読み返さない (破棄後の読み出しで落ちることがあるため)。
// - 設定画面を開くたびに作り、画面を閉じたら画面ごと破棄される。同時に 1 つだけ。
public sealed class SettingsSearchBox
{
    private static SettingsSearchBox live;
    private static UnityAction focusAction;

    private readonly TextBoxTMP text;
    private readonly TextMeshPro hint;
    private TextMeshPro suggest;
    private string suggestTop = "";
    private readonly Action<string> changed;
    public string Query { get; private set; } = "";

    // 検索欄に字を打っている間 (ロビーのキー操作を止めるため)
    public static bool IsTyping => live != null && live.text != null && live.text.hasFocus;

    private SettingsSearchBox(TextBoxTMP text, TextMeshPro hint, Action<string> changed)
    {
        this.text = text;
        this.hint = hint;
        this.changed = changed;
    }

    // 幅 width・高さ height (親の座標の単位) の欄を parent の pos に作る。作れなければ null
    public static SettingsSearchBox Create(Transform parent, Vector3 pos, float width, float height, string hintText, Action<string> changed)
    {
        var chat = DestroyableSingleton<HudManager>.InstanceExists ? DestroyableSingleton<HudManager>.Instance.Chat : null;
        if (chat == null || chat.freeChatField == null) return null;

        // 元が非表示のことがある (チャットの窓が閉じている)。複製も非表示のまま作り、部品を外してから表示する
        var root = Object.Instantiate(chat.freeChatField.gameObject, parent);
        root.name = "SettingsSearchBox";
        root.SetActive(false);
        try
        {
            var field = root.GetComponent<FreeChatInputField>();
            var textArea = field != null ? field.textArea : root.GetComponentInChildren<TextBoxTMP>(true);
            if (field != null) Object.DestroyImmediate(field);
            var send = root.transform.Find("ChatSendButton");
            if (send != null) Object.DestroyImmediate(send.gameObject);
            if (textArea == null) { Object.Destroy(root); return null; }

            // 欄のイベントは空のものに差し替える (複製元のチャットへの配線が残っているため)
            textArea.OnEnter = new UnityEngine.UI.Button.ButtonClickedEvent();
            textArea.OnChange = new UnityEngine.UI.Button.ButtonClickedEvent();
            textArea.OnFocusLost = new UnityEngine.UI.Button.ButtonClickedEvent();
            textArea.OnFocus = new UnityEngine.UI.Button.ButtonClickedEvent();
            textArea.allowAllCharacters = true;
            textArea.AllowSymbols = true;
            textArea.AllowPaste = true;
            textArea.characterLimit = 24;
            textArea.ClearOnFocus = false;

            // 大きさ: 縦は全体の縮尺で、横は背景の絵の幅で合わせる (文字が横に潰れないように)
            var bg = root.transform.Find("Background");
            var bgSprite = bg != null ? bg.GetComponent<SpriteRenderer>() : null;
            float baseH = bgSprite != null ? bgSprite.size.y : 0.62f;
            float s = height / baseH;
            root.transform.localScale = new Vector3(s, s, 1f);
            root.transform.localPosition = pos;
            float w = width / s;
            if (bgSprite != null)
            {
                bgSprite.size = new Vector2(w, baseH);
                var col = bg.GetComponent<BoxCollider2D>();
                if (col != null) col.size = new Vector2(w, col.size.y);
                var button = bg.GetComponent<PassiveButton>();
                // 複製元の欄は、マウスを当てると緑になる。検索欄では白のまま薄く暗くする
                var roll = bg.GetComponent<ButtonRolloverHandler>();
                if (roll != null)
                {
                    roll.Target = bgSprite;
                    roll.OutColor = Color.white;
                    roll.OverColor = new Color(0.86f, 0.88f, 0.9f, 1f);
                    bgSprite.color = Color.white;
                }
                if (button != null)
                {
                    button.OnClick = new UnityEngine.UI.Button.ButtonClickedEvent();
                    focusAction ??= (UnityAction)(() =>
                    {
                        if (live != null && live.text != null) live.text.GiveFocus();
                    });
                    button.OnClick.AddListener(focusAction);
                }
            }
            float textX = -w / 2f + 0.2f / s;
            textArea.transform.localPosition = new Vector3(textX, 0f, 0f);
            if (textArea.outputText != null)
            {
                // 複製元のチャット欄はダークテーマだと白い字のまま写る。背景を白にしているので字は黒に揃える
                textArea.outputText.color = Color.black;
                var rt = textArea.outputText.rectTransform;
                rt.sizeDelta = new Vector2(w - 0.4f / s, rt.sizeDelta.y);
            }

            // 文字数の表示の欄を、空の時の案内に使う
            TextMeshPro hintTmp = null;
            var counter = root.transform.Find("CharCounter (TMP)");
            if (counter != null)
            {
                var translator = counter.GetComponent<TextTranslatorTMP>();
                if (translator != null) Object.DestroyImmediate(translator);
                // 入力の印 (|) の右から出す (重なると 1 文字目が隠れる)
                counter.localPosition = new Vector3(textX + 0.12f, 0f, -0.01f);
                hintTmp = counter.GetComponent<TextMeshPro>();
                if (hintTmp != null)
                {
                    hintTmp.text = hintText;
                    hintTmp.fontSize = textArea.outputText != null ? textArea.outputText.fontSize * 0.85f : 1.8f;
                    hintTmp.alignment = TextAlignmentOptions.Left;
                    hintTmp.color = new Color(0.45f, 0.45f, 0.45f, 1f);
                    hintTmp.rectTransform.pivot = new Vector2(0f, 0.5f);
                    hintTmp.rectTransform.sizeDelta = new Vector2(w - 0.6f / s, baseH);
                    hintTmp.enableWordWrapping = false;
                }
            }

            TextMeshPro suggestTmp = null;
            if (hintTmp != null)
            {
                suggestTmp = Object.Instantiate(hintTmp, hintTmp.transform.parent);
                suggestTmp.name = "SettingsSearchSuggest";
                suggestTmp.transform.localPosition = new Vector3(textX + 0.12f, -baseH * 0.95f, -0.01f);
                suggestTmp.color = Color.white;
                suggestTmp.richText = true;
                suggestTmp.text = "";
                suggestTmp.enabled = false;
            }

            root.SetActive(true);
            live = new SettingsSearchBox(textArea, hintTmp, changed);
            live.suggest = suggestTmp;
            textArea.SetText("", "");
            return live;
        }
        catch (Exception ex)
        {
            Logger.Error($"search box: {ex}", "SettingsSearchBox");
            live = null;
            Object.Destroy(root);
            return null;
        }
    }

    // 打っている語に当たる設定の数と、いちばん当たりの高い名前を入力欄の下に 1 行で出す (Tab でその名前を欄へ入れる)
    public static void UpdateSuggestion(string query)
    {
        var cur = live;
        if (cur == null || cur.suggest == null) return;
        try
        {
            string q = (query ?? "").Trim();
            if (q.Length == 0 || (q.Length == 1 && q[0] < 0x80))
            {
                cur.suggestTop = "";
                cur.suggest.enabled = false;
                return;
            }

            var preview = OptionSearch.PreviewQuery(q);
            cur.suggestTop = preview.Hits > 0 ? preview.Top : "";
            cur.suggest.text = preview.Hits == 0
                ? $"<#ff8f8f>{Translator.GetString("SearchPreviewNoHit")}</color>"
                : $"<#7fd6ff>{string.Format(Translator.GetString("SearchPreviewHits"), preview.Hits)}</color>  <#ffd966>▶ {preview.Top}</color>  <#999999>{Translator.GetString("SearchSuggestTabHint")}</color>";
            cur.suggest.enabled = true;
        }
        catch (Exception ex) { Logger.Warn($"search suggestion: {ex.Message}", "SettingsSearchBox"); }
    }

    // Tab: 当たりの高い名前を入力欄へ入れる
    public static void AcceptTop()
    {
        var cur = live;
        if (cur == null || cur.text == null || cur.suggestTop.Length == 0) return;
        cur.text.SetText(cur.suggestTop, "");
    }

    public void Clear()
    {
        if (text != null) text.SetText("", "");
    }

    // 画面を閉じた時に呼ぶ (欄そのものは画面と一緒に破棄される)
    public static void Forget() => live = null;

    internal static void OnSetText(TextBoxTMP box, string input, string compo)
    {
        var cur = live;
        if (cur == null || cur.text == null || cur.text.Pointer != box.Pointer) return;
        string query = Clean((input ?? "") + (compo ?? ""));
        if (cur.hint != null) cur.hint.enabled = query.Length == 0;
        if (query == cur.Query) return;
        cur.Query = query;
        cur.changed(query);
    }

    // 欄の字には BackSpace (\b) が字として残ることがある (「1」→ BackSpace →「a」で "1\ba")。
    // BackSpace は前の字を消す扱いにし、ほかの制御文字は捨てる
    static string Clean(string s)
    {
        bool plain = true;
        foreach (var c in s) if (c < 0x20) { plain = false; break; }
        if (plain) return s;
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c == '\b') { if (sb.Length > 0) sb.Length--; }
            else if (c >= 0x20) sb.Append(c);
        }
        return sb.ToString();
    }
}

[HarmonyPatch(typeof(TextBoxTMP), nameof(TextBoxTMP.SetText))]
public static class SettingsSearchBoxSetTextPatch
{
    public static void Postfix(TextBoxTMP __instance, [HarmonyArgument(0)] string input, [HarmonyArgument(1)] string inputCompo)
    {
        try { SettingsSearchBox.OnSetText(__instance, input, inputCompo); }
        catch (Exception ex) { Logger.Warn($"search box text: {ex.Message}", "SettingsSearchBox"); }
    }
}
