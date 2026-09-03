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
    private const string PencilGlyph = "✎";
    private const string FallbackGlyph = "#";

    private static OptionItem EnableNumericOptionInput;
    private static bool GlyphLogged;

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
            bool hasGlyph = false;
            try { hasGlyph = text.font && text.font.HasCharacter(PencilGlyph[0], true, true); }
            catch (Exception e) { Logger.Warn($"numeric input glyph check failed: {e.Message}", "NumericOptionInput"); }

            if (!GlyphLogged)
            {
                GlyphLogged = true;
                Logger.Info($"numeric input glyph: {(hasGlyph ? PencilGlyph : $"{FallbackGlyph} (fallback)")} font={(text.font ? text.font.name : "<null>")}", "NumericOptionInput");
            }

            text.SetText(hasGlyph ? PencilGlyph : FallbackGlyph); // atlas に無い環境向けに ASCII へ倒す
            text.color = Color.white;
        }

        // GameOptionButton は hover の出入りや再有効化のたびに interactableColor で塗り直すので、
        // スプライトの色だけ変えても白に戻る (白地に白文字で「空の四角」になる)。色はフィールド側に持たせる。
        button.interactableColor = new Color32(90, 140, 220, byte.MaxValue);
        button.interactableHoveredColor = new Color32(130, 180, 255, byte.MaxValue);

        Transform sprite = clone.FindChild("ButtonSprite");
        if (sprite)
        {
            var sr = sprite.GetComponent<SpriteRenderer>();
            if (sr) sr.color = button.interactableColor;
        }

        // clone は +ボタンの OnClick (インクリメント処理) をそのまま引き継いでいるので、
        // 新しい UnityEvent に丸ごと差し替えて自前の処理だけを積む (SetupHelpIcon と同じ手)。
        button.OnClick = new();
        button.OnClick.AddListener((Action)(() => OpenFor(row, data)));

        // ボタンは + の右隣 = タブの ClickMask (スクロール領域の当たり判定) の外に置いているので、
        // マスク付きだと PassiveButtonManager が押下を全部捨てる (実機で + は効くのに # だけ無反応)。
        // マスクは外し、行が表示範囲内かは OpenFor 側で + のマスクの縦範囲と照合する。
        button.ClickMask = null;
    }

    // 行のマスク張り替え (タブ切替・検索での行の借用) で # にマスクが付かないよう追随させる
    [HarmonyPatch(typeof(OptionBehaviour), nameof(OptionBehaviour.SetClickMask))]
    private static class FollowClickMaskPatch
    {
        public static void Postfix(OptionBehaviour __instance)
        {
            try
            {
                Transform t = __instance.transform.FindChild(ButtonName);
                if (!t) return;
                var pb = t.GetComponent<PassiveButton>();
                if (pb && pb.ClickMask) pb.ClickMask = null;
            }
            catch (Exception e) { Logger.Warn($"numeric input click mask follow failed: {e.Message}", "NumericOptionInput"); }
        }
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

    // GameOptionsMenuPatch のオプション検索欄 (同じ freeChatField クローン) が実機で正しい大きさに
    // 見えているときの localScale を流用する。数値入力欄は ValueText の数値ボックス幅に収める
    // ため、検索欄よりやや小さめの x スケールにしてある (実機で見ながら微調整する前提の定数)。
    private static readonly Vector3 KnownGoodWorldScale = new(0.22f, 0.59f, 1f);

    private static FreeChatInputField InputFieldInstance;

    // GameOptionsMenuPatch.FixInputChatField が検索欄と同じくバニラの UpdateCharCount を止めるために参照する
    public static FreeChatInputField ActiveField => InputFieldInstance;

    // 開いている間は Enter を数値の確定に使う (設定メニューの検索アクションと取り合わないよう ControlPatch が見る)
    public static bool IsInputOpen => IsOpen;
    private static OptionItem TargetOption;
    private static NumberOption TargetRow;
    private static bool IsOpen;
    private static Collider2D SuppressedScrollHitbox; // 開いている間だけ止めるスクローラーのドラッグ判定
    private static int OpenedFrame; // ボタン押下と同じフレームの押下を「欄の外クリック」と誤認しないため
    private static string PendingPrefill; // 開いた次のフレームで欄が空なら1回だけ入れ直す (フォーカス処理が同フレーム内で欄を消すことがある)
    private static bool LogAfterFirstFrame;

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

        // 文字数カウンタ (チャットの名残) は数値入力には無用で、縮小した欄の右上に残骸として映る
        if (InputFieldInstance.charCountText) InputFieldInstance.charCountText.gameObject.SetActive(false);

        // フォーカス取得時の自動クリアは不要 (これだけでは開いた同フレーム内の消去は防げず、初回 Pump の再適用が要る)
        if (InputFieldInstance.textArea) InputFieldInstance.textArea.ClearOnFocus = false;

        // freeChatField クローンは AspectPosition を継承しており、これが OnEnable/Update で
        // localPosition を画面端基準に書き戻す — SetActive(true) 後に行の位置から画面端へ
        // 飛ばされる原因になる。SimpleButton/BGMInfoDisplay/LobbyDecor と同じ手で除去する。
        AspectPosition[] aspectPositions = InputFieldInstance.GetComponentsInChildren<AspectPosition>(true);
        if (aspectPositions.Length == 0)
        {
            Logger.Info("numeric input clone: no AspectPosition found", "NumericOptionInput");
        }
        else
        {
            var hitNames = new System.Text.StringBuilder();
            foreach (var ap in aspectPositions)
            {
                if (hitNames.Length > 0) hitNames.Append(", ");
                hitNames.Append(ap.gameObject.name);

                // enabled=false を先に立てないと、Destroy が反映される前の SetActive(true) で
                // OnEnable が一度だけ走ってしまい、最初に開いた回だけ画面端へ飛ぶ。
                ap.enabled = false;
                UnityEngine.Object.Destroy(ap);
            }

            Logger.Info($"numeric input clone: destroyed {aspectPositions.Length} AspectPosition(s): {hitNames}", "NumericOptionInput");
        }

        // AspectPosition 以外にも位置を動かすコンポーネントが無いか、ルートの構成を1回だけ記録する。
        // C# の is 判定は il2cpp ラッパーで常に false になるため、型名は GetIl2CppType() で取る。
        Component[] rootComponents = InputFieldInstance.GetComponents<Component>();
        var names = new System.Text.StringBuilder();
        foreach (Component c in rootComponents)
        {
            if (names.Length > 0) names.Append(", ");
            names.Append(c ? c.GetIl2CppType().Name : "<null>");
        }

        Logger.Info($"numeric input clone root components: {names}", "NumericOptionInput");

        InputFieldInstance.gameObject.SetActive(false);
        return true;
    }

    private static void OpenFor(NumberOption row, BaseGameSetting data)
    {
        if (!Enabled || !row) return;

        // マスク無しのボタンはスクロールで隠れた行でも押せてしまうので、+ のマスクの縦範囲に行があるときだけ開く
        Collider2D rowMask = row.PlusBtn ? row.PlusBtn.ClickMask : null;
        if (rowMask)
        {
            float y = row.ValueText ? row.ValueText.transform.position.y : row.transform.position.y;
            Bounds b = rowMask.bounds;
            if (y < b.min.y || y > b.max.y) return;
        }

        OptionItem match = FindOptionItem(data);
        if (match is not (IntegerOptionItem or FloatOptionItem) || !EnsureInputField()) return;

        TargetOption = match;
        TargetRow = row;

        // 世界座標で置き、row の階層スケールに依存しない形にする (localPosition 決め打ちは
        // 親のスケール次第で行から大きく外れる)。scale は検索欄クローンの localScale を流用し、
        // row の lossyScale で割り戻すことで見た目の大きさを揃える。
        // 行の右端は画面外まではみ出すので、代わりに ValueText (数値表示ボックス) の真上に重ねる。
        Transform t = InputFieldInstance.transform;
        Vector3 valueWorld = row.ValueText.transform.position;
        Vector3 fieldWorld = new(valueWorld.x, valueWorld.y, valueWorld.z - 1f);

        t.SetParent(row.transform, true);
        t.position = fieldWorld;

        // 欄の上でのクリックが後ろのスクローラー (Hitbox) に届いてドラッグ扱いになるのを止める
        RestoreScrollHitbox();
        try
        {
            Scroller scroller = row.GetComponentInParent<Scroller>();
            if (scroller && scroller.Hitbox && scroller.Hitbox.enabled)
            {
                SuppressedScrollHitbox = scroller.Hitbox;
                SuppressedScrollHitbox.enabled = false;
            }
        }
        catch (Exception e) { Logger.Warn($"scroll hitbox suppress failed: {e.Message}", "NumericOptionInput"); }

        Vector3 rowLossy = row.transform.lossyScale;
        t.localScale = new Vector3(
            rowLossy.x != 0f ? KnownGoodWorldScale.x / rowLossy.x : KnownGoodWorldScale.x,
            rowLossy.y != 0f ? KnownGoodWorldScale.y / rowLossy.y : KnownGoodWorldScale.y,
            rowLossy.z != 0f ? KnownGoodWorldScale.z / rowLossy.z : KnownGoodWorldScale.z);

        // 検索欄クローンはルートの非等方スケールで文字が縦横に潰れる分を outputText 側の
        // 逆スケールで戻している — 同じクローンを使う以上ここも合わせないと打ち込んだ文字
        // だけ細長く歪む (KnownGoodWorldScale を変えたらここも実機で見て合わせ直す)。
        if (InputFieldInstance.textArea && InputFieldInstance.textArea.outputText)
            InputFieldInstance.textArea.outputText.transform.localScale = new Vector3(3.5f, 2f, 1f);

        Logger.Info($"numeric input opened: rowWorld={row.transform.position} valueWorld={row.ValueText.transform.position} fieldWorld={t.position} fieldLossy={t.lossyScale} textAreaWorld={InputFieldInstance.textArea?.transform.position}", "NumericOptionInput");

        InputFieldInstance.gameObject.SetActive(true);
        InputFieldInstance.textArea.Clear();

        InputFieldInstance.Focus();

        string prefill = match is IntegerOptionItem ? match.GetInt().ToString(CultureInfo.InvariantCulture) : match.GetFloat().ToString("0.##", CultureInfo.InvariantCulture);
        TextBoxPatch.SetChatFieldText(InputFieldInstance.textArea, prefill);
        PendingPrefill = prefill;
        IsOpen = true;
        OpenedFrame = Time.frameCount;
        LogAfterFirstFrame = true; // SetActive/Focus 後に位置が動いていないか、Pump の初回で1回だけ確認する
    }

    private static void RestoreScrollHitbox()
    {
        if (SuppressedScrollHitbox) SuppressedScrollHitbox.enabled = true;
        SuppressedScrollHitbox = null;
    }

    private static void Close()
    {
        IsOpen = false;
        LogAfterFirstFrame = false;
        PendingPrefill = null;
        RestoreScrollHitbox();
        TargetOption = null;
        TargetRow = null;

        if (!InputFieldInstance) return;

        try { InputFieldInstance.Unfocus(); }
        catch { /* ignore */ }

        try { InputFieldInstance.ForceKeyboardClose(); }
        catch { /* ignore */ }

        InputFieldInstance.gameObject.SetActive(false);
        InputFieldInstance.transform.SetParent(null, true); // row の破棄 (再ホスト等) に巻き込まれないよう外しておく
        UnityEngine.Object.DontDestroyOnLoad(InputFieldInstance.gameObject); // row 配下へ入れた時点で DDOL 資格が外れているので掛け直す
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

        // 行と同じ高さに置いても有効化直後の更新で縦へ約1.2u ずれる (実測) ので、開いている間は
        // 毎フレーム ValueText の真上へ張り直す。スクロールで行が動いた場合の追従も兼ねる。
        if (TargetRow && TargetRow.ValueText)
        {
            Vector3 valueWorld = TargetRow.ValueText.transform.position;
            Vector3 want = new(valueWorld.x, valueWorld.y, valueWorld.z - 1f);
            Transform ft = InputFieldInstance.transform;
            if ((ft.position - want).sqrMagnitude > 1e-6f) ft.position = want;
        }

        // 検索欄と同じ配色 (暗い背景に白文字)。クローン元のチャット欄は白背景に黒文字なので毎フレーム塗り直す
        if (InputFieldInstance.background) InputFieldInstance.background.color = new Color32(40, 40, 40, byte.MaxValue);
        if (InputFieldInstance.textArea && InputFieldInstance.textArea.outputText) InputFieldInstance.textArea.outputText.color = Color.white;

        if (LogAfterFirstFrame)
        {
            LogAfterFirstFrame = false;

            if (PendingPrefill != null && TextBoxPatch.SafeChatText(InputFieldInstance.textArea).Length == 0)
            {
                Logger.Info("numeric input prefill was cleared after open — reapplying once", "NumericOptionInput");
                TextBoxPatch.SetChatFieldText(InputFieldInstance.textArea, PendingPrefill);
            }

            PendingPrefill = null;
            Transform t = InputFieldInstance.transform;
            TextMeshPro outText = InputFieldInstance.textArea ? InputFieldInstance.textArea.outputText : null;
            Logger.Info($"numeric input after-1f: fieldWorld={t.position} fieldLossy={t.lossyScale} textAreaWorld={InputFieldInstance.textArea?.transform.position} text='{TextBoxPatch.SafeChatText(InputFieldInstance.textArea)}' out='{(outText ? outText.text : "<null>")}' outColor={(outText ? outText.color : default)} outEnabled={(outText && outText.enabled)} outWorld={(outText ? outText.transform.position : default)} outLossy={(outText ? outText.transform.lossyScale : default)} fontSize={(outText ? outText.fontSize : 0f)}", "NumericOptionInput");
        }

        // 欄の外をクリックしたら取り消し (欄の上のクリックはフォーカス取り直しに使う)
        if (Input.GetMouseButtonDown(0) && Camera.main && Time.frameCount > OpenedFrame + 1)
        {
            Vector2 world = Camera.main.ScreenToWorldPoint(Input.mousePosition);
            bool onField = false;
            foreach (var h in Physics2D.OverlapPointAll(world))
                if (h && h.transform.IsChildOf(InputFieldInstance.transform)) { onField = true; break; }

            if (!onField)
            {
                Close();
                return;
            }
        }

        // フォーカスは開いた直後に外れることがある (押下時 hasFocus=false を実測)。開いている間は持ち続ける。
        if (InputFieldInstance.textArea && !InputFieldInstance.textArea.hasFocus)
            InputFieldInstance.textArea.GiveFocus();

        if (Input.GetKeyDown(KeyCode.Escape))
        {
            Close();
            return;
        }

        if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
            Confirm();
    }
}
