using System;
using System.Globalization;
using HarmonyLib;
using TMPro;
using UnityEngine;
using static EndKnot.Translator;

namespace EndKnot.Patches;

// 設定メニューの数値オプション (Float/Int) 行に小さな「入力」ボタンを1つ足し、±連打の代わりに
// テキストで直接値を打てるようにする。入力欄は検索欄 (OptionSearchSuggestPatch) と同じ
// freeChatField クローンの手を使う — 新しい IMGUI は書かない。
//
// ボタンは NumberOption.SetUpFromData の Postfix だけで足す。この native メソッドは EHR 側でも
// GameOptionsMenuPatch (通常タブ) と NewRoleMenuView (役職メニュー) の両方が同じ形で呼んでおり、
// かつ「新規に組み立てた行だけが通る」(pool 再利用行は二度と通らない) ため、一度きりの追加が
// そのまま冪等性の担保になる — 行自体が pool される限りボタンも row の子として一緒に生き残る。
//
// 純ホスト画面 UI・RPC ゼロ: OptionItem.SetValue はローカルのプリセットを直接書き換えるだけで、
// 変更は既存の SyncAllOptions 経路 (RPC.SyncCustomSettingsRPC) を通る — 新しい RPC は増やさない。
[HarmonyPatch]
public static class NumericOptionInputPatch
{
    private const string ButtonName = "NumericInputButton";

    private static OptionItem EnableNumericOptionInput;

    public static void SetupCustomOption()
    {
        new TextOptionItem(110100, "MenuTitle.NumericOptionInput", TabGroup.GameSettings)
            .SetColor(new Color32(150, 200, 255, byte.MaxValue))
            .SetHeader(true);

        EnableNumericOptionInput = new BooleanOptionItem(960110, "EnableNumericOptionInput", true, TabGroup.GameSettings)
            .SetColor(new Color32(150, 200, 255, byte.MaxValue));
    }

    private static bool Enabled => EnableNumericOptionInput != null && EnableNumericOptionInput.GetBool();

    // ==== 行への「入力」ボタン追加 ====

    [HarmonyPatch(typeof(NumberOption), nameof(NumberOption.SetUpFromData))]
    private static class AddInputButtonPatch
    {
        // OnlinePresetsManager は NumberOption をただのクリック可能な行として使い回しており、
        // その呼び出しは data=null で来る — 本物のオプション行ではないので触らない。
        public static void Postfix(NumberOption __instance, BaseGameSetting data)
        {
            try { EnsureInputButton(__instance, data); }
            catch (Exception e) { Logger.Warn($"numeric input button setup failed: {e.Message}", "NumericOptionInput"); }
        }
    }

    private static void EnsureInputButton(NumberOption row, BaseGameSetting data)
    {
        if (!data || !row || !row.PlusBtn || !row.ValueText) return;

        // プリセット行 (PresetOptionItem) も NumberOption だが、値を打ち直せる対象は
        // Integer/FloatOptionItem だけ (ApplyValue の switch もその2種類しか扱わない)。
        // プリセット切り替えは番号を直接書き換えると ReloadUI 相当の後処理が要り、割に合わない
        // ので、ボタンごと出さない。
        OptionItem owner = FindOptionItem(data);
        if (owner is not (IntegerOptionItem or FloatOptionItem)) return;

        Transform plusTemplate = row.PlusBtn.transform;
        Transform parent = plusTemplate.parent;
        if (!parent) return;

        for (int i = 0; i < parent.childCount; i++)
            if (parent.GetChild(i).name == ButtonName) return; // 冪等性: SetUpFromData は新規行でしか呼ばれないので通常は通らないが、念のため

        Transform clone = UnityEngine.Object.Instantiate(plusTemplate, parent, true);
        clone.name = ButtonName;
        clone.localPosition = plusTemplate.localPosition + new Vector3(0.55f, 0f, 0f);

        var button = clone.GetComponent<GameOptionButton>();
        if (!button)
        {
            UnityEngine.Object.Destroy(clone.gameObject);
            return;
        }

        TextMeshPro text = clone.GetComponentInChildren<TextMeshPro>();
        if (text)
        {
            text.SetText("✎"); // ✎ (鉛筆)。+/- と紛れない言語非依存アイコン
            text.color = Color.white;
        }

        Transform sprite = clone.FindChild("ButtonSprite");
        if (sprite)
        {
            var sr = sprite.GetComponent<SpriteRenderer>();
            if (sr) sr.color = new Color32(90, 140, 220, byte.MaxValue);
        }

        // clone は +ボタンの OnClick (インクリメント処理) をそのまま引き継いでいるので、
        // 新しい UnityEvent に丸ごと差し替えて自前の処理だけを積む (SetupHelpIcon と同じ手)。
        button.OnClick = new();
        button.OnClick.AddListener((Action)(() => OpenFor(row, data)));
    }

    private static OptionItem FindOptionItem(BaseGameSetting data)
    {
        foreach (var kv in ModGameOptionsMenu.BaseGameSettingCache)
        {
            if ((object)kv.Value != (object)data) continue;
            return kv.Key;
        }

        return null;
    }

    // ==== 入力欄の開閉 ====

    private static FreeChatInputField InputFieldInstance;
    private static OptionItem TargetOption;
    private static NumberOption TargetRow;
    private static bool IsOpen;

    private static bool EnsureInputField()
    {
        if (InputFieldInstance) return true;

        FreeChatInputField freeChatField = HudManager.InstanceExists ? HudManager.Instance.Chat?.freeChatField : null;
        if (!freeChatField)
        {
            Logger.Warn("freeChatField unavailable — numeric input skipped this time", "NumericOptionInput");
            return false;
        }

        InputFieldInstance = UnityEngine.Object.Instantiate(freeChatField);
        InputFieldInstance.name = "NumericOptionInputField";
        UnityEngine.Object.DontDestroyOnLoad(InputFieldInstance.gameObject);

        // 送信ボタン (チャットの名残) は使わない。確定は Enter、取り消しは Esc。
        Transform sendButton = InputFieldInstance.transform.FindChild("ChatSendButton");
        if (sendButton) sendButton.gameObject.SetActive(false);

        InputFieldInstance.gameObject.SetActive(false);
        return true;
    }

    private static void OpenFor(NumberOption row, BaseGameSetting data)
    {
        if (!Enabled || !row) return;

        OptionItem match = FindOptionItem(data);
        if (match is not (IntegerOptionItem or FloatOptionItem) || !EnsureInputField()) return;

        TargetOption = match;
        TargetRow = row;

        Transform t = InputFieldInstance.transform;
        t.SetParent(row.transform, false);
        t.localPosition = row.ValueText.transform.localPosition + new Vector3(0f, 0.55f, -1f);
        t.localScale = new Vector3(0.16f, 0.3f, 1f);

        InputFieldInstance.gameObject.SetActive(true);
        InputFieldInstance.textArea.Clear();

        string prefill = match is IntegerOptionItem ? match.GetInt().ToString(CultureInfo.InvariantCulture) : match.GetFloat().ToString("0.##", CultureInfo.InvariantCulture);
        TextBoxPatch.SetChatFieldText(InputFieldInstance.textArea, prefill);

        InputFieldInstance.Focus();
        IsOpen = true;
    }

    private static void Close()
    {
        IsOpen = false;
        TargetOption = null;
        TargetRow = null;

        if (!InputFieldInstance) return;

        try { InputFieldInstance.Unfocus(); }
        catch { /* ignore */ }

        try { InputFieldInstance.ForceKeyboardClose(); }
        catch { /* ignore */ }

        InputFieldInstance.gameObject.SetActive(false);
        InputFieldInstance.transform.SetParent(null, true); // row の破棄 (再ホスト等) に巻き込まれないよう外しておく
    }

    private static void Confirm()
    {
        string text = TextBoxPatch.SafeChatText(InputFieldInstance.textArea).Trim();
        OptionItem option = TargetOption;
        NumberOption row = TargetRow;
        Close();

        if (option == null || !row || text.Length == 0) return; // 空入力はキャンセル扱い

        if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float raw))
        {
            Logger.SendInGame(GetString("NumericOptionInput.ParseError"), Palette.Orange);
            return;
        }

        ApplyValue(row, option, raw);
    }

    private static void ApplyValue(NumberOption row, OptionItem option, float raw)
    {
        int index;

        switch (option)
        {
            case FloatOptionItem fo:
                float fClamped = Mathf.Clamp(raw, fo.Rule.MinValue, fo.Rule.MaxValue);
                index = fo.Rule.GetNearestIndex(fClamped);
                break;
            case IntegerOptionItem io:
                int iClamped = Mathf.Clamp(Mathf.RoundToInt(raw), io.Rule.MinValue, io.Rule.MaxValue);
                index = io.Rule.GetNearestIndex(iClamped);
                break;
            default:
                return;
        }

        option.SetValue(index);

        // OptionItem.SetValue の Refresh() は StringOption 行しか塗り直さない (NumberOption は対象外) ので、
        // 行の見た目はここで自前で合わせる。次の +/- クリックが正しい値から動くよう Value/oldValue も揃える。
        if (row && row.ValueText)
        {
            row.ValueText.text = option.GetString();
            row.oldValue = row.Value = option.GetFloat();
        }

        // 子オプションを持つ親の値を変えたときは、行の並び自体を組み直さないと子行の出し入れが
        // 追随しない。+/- ボタンが通っている OnValueChanged (= GameOptionsMenu.ValueChanged) を
        // そのまま呼んで、通常タブと役職メニューの違いは既存経路に吸収させる。
        // 行が作り直される可能性があるので、row を触り終えてから最後に呼ぶ。
        if (row && row.OnValueChanged != null) row.OnValueChanged.Invoke(row);
    }

    // ==== Enter で確定・Esc で取り消し ====

    [HarmonyPatch(typeof(ChatController), nameof(ChatController.Update))]
    private static class PumpPatch
    {
        public static void Postfix()
        {
            if (!IsOpen) return;

            try { Pump(); }
            catch (Exception e)
            {
                Logger.Warn($"numeric input pump failed: {e.Message}", "NumericOptionInput");
                Close();
            }
        }
    }

    private static void Pump()
    {
        if (!InputFieldInstance || !InputFieldInstance.gameObject.activeSelf || !GameSettingMenu.Instance || !GameSettingMenu.Instance.isActiveAndEnabled)
        {
            Close();
            return;
        }

        if (Input.GetKeyDown(KeyCode.Escape))
        {
            Close();
            return;
        }

        if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
            Confirm();
    }
}
