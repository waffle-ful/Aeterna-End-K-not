using System;
using EndKnot.Modules.YouTubeChat;
using HarmonyLib;
using TMPro;
using UnityEngine;
using static EndKnot.Translator;

namespace EndKnot.Patches;

// YouTube 自動投稿の各チェックボックス行 (参加募集 / 人数 / ローテーション / 宣伝) に ✎ ボタン (フォントに
// 字が無ければ #) を足し、押すと行のすぐ下に文言の入力欄を開く。入力欄は NumericOptionInputPatch と同じ
// freeChatField クローンの手。Enter で保存 (空なら既定の文言へ戻す)、Esc か欄の外のクリックで閉じる。
//
// 文言はホストの BepInEx cfg (YouTubePostTexts) にだけ書く — オプション値ではないので同期も RPC も無い。
[HarmonyPatch]
public static class YouTubePostTextEditPatch
{
    private const string ButtonName = "YouTubePostTextEditButton";
    private const string PencilGlyph = "✎";
    private const string FallbackGlyph = "#";

    // YouTube のライブチャットは 1 投稿 200 文字まで。ローテーションは複数行を 1 欄に並べるので広めに取る。
    private const int SingleTextLimit = 200;
    private const int RotationTextLimit = 1000;

    private static YouTubePostTextKind? KindOf(OptionItem item)
    {
        if (item == null) return null;
        if (item == YouTubePostOptions.WelcomeEnabled) return YouTubePostTextKind.Welcome;
        if (item == YouTubePostOptions.CountdownEnabled) return YouTubePostTextKind.Countdown;
        if (item == YouTubePostOptions.RotationEnabled) return YouTubePostTextKind.Rotation;
        if (item == YouTubePostOptions.PromoEnabled) return YouTubePostTextKind.Promo;
        return null;
    }

    // ==== 行への ✎ ボタン追加 ====

    // ToggleOptionPatch.InitializePrefix が本体を置き換えるが、Postfix はそのまま走る。
    // Initialize は新規の行で1回だけ通る想定だが、念のため子の名前で冪等にしてある。
    [HarmonyPatch(typeof(ToggleOption), nameof(ToggleOption.Initialize))]
    private static class AddEditButtonPatch
    {
        public static void Postfix(ToggleOption __instance)
        {
            try { EnsureEditButton(__instance); }
            catch (Exception e) { Logger.Warn($"edit button setup failed: {e.Message}", "YouTubePostTextEdit"); }
        }
    }

    private static void EnsureEditButton(ToggleOption row)
    {
        if (!row || !row.CheckMark) return;
        if (!ModGameOptionsMenu.OptionList.TryGetValue(row, out int index)) return;

        YouTubePostTextKind? kind = KindOf(OptionItem.AllOptions[index]);
        if (kind == null) return;

        Transform rowT = row.transform;
        for (int i = 0; i < rowT.childCount; i++)
            if (rowT.GetChild(i).name == ButtonName) return;

        // チェックボックス行には ± ボタンが無いので、数値行のプレハブの + ボタンを型紙にする
        var menu = row.GetComponentInParent<GameOptionsMenu>();
        NumberOption numberTemplate = menu ? menu.numberOptionOrigin : null;
        if (!numberTemplate || !numberTemplate.PlusBtn) return;

        Transform plusTemplate = numberTemplate.PlusBtn.transform;
        Transform clone = UnityEngine.Object.Instantiate(plusTemplate, rowT, false);
        clone.name = ButtonName;
        clone.localScale = plusTemplate.localScale;

        // チェックボックスの左隣 (右隣は配信用のロビーコード表示と重なりやすい)
        Vector3 check = row.CheckMark.transform.position;
        clone.position = new Vector3(check.x - 0.45f, check.y, check.z);

        // 型紙はプレハブなので、スクロール領域のマスク用の素材が行と食い違ってボタンの地が描かれない。
        // 同じ行のチェックボックスの素材とマスク設定に揃え、スクロールでの見え隠れも行と一致させる。
        foreach (SpriteRenderer r in clone.GetComponentsInChildren<SpriteRenderer>(true))
        {
            r.sharedMaterial = row.CheckMark.sharedMaterial;
            r.maskInteraction = row.CheckMark.maskInteraction;
        }

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
            catch (Exception e) { Logger.Warn($"edit button glyph check failed: {e.Message}", "YouTubePostTextEdit"); }

            text.SetText(hasGlyph ? PencilGlyph : FallbackGlyph);
            text.color = Color.white;
        }

        // GameOptionButton は hover のたびに interactableColor で塗り直すので、色はフィールド側に持たせる
        button.interactableColor = new Color32(90, 140, 220, byte.MaxValue);
        button.interactableHoveredColor = new Color32(130, 180, 255, byte.MaxValue);

        Transform sprite = clone.FindChild("ButtonSprite");
        if (sprite)
        {
            var sr = sprite.GetComponent<SpriteRenderer>();
            if (sr) sr.color = button.interactableColor;
        }

        YouTubePostTextKind k = kind.Value;
        button.OnClick = new();
        button.OnClick.AddListener((Action)(() => OpenFor(row, k)));

        // チェックボックスの右隣はタブの ClickMask の外なので、マスク付きだと押下が捨てられる。
        // マスクは外し、行が表示範囲内かは OpenFor 側でチェックボックスのマスクと照合する。
        button.ClickMask = null;
    }

    // 行のマスク張り替え (タブ切替・検索結果一覧) で ✎ にマスクが付かないよう追随させる
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
            catch (Exception e) { Logger.Warn($"edit button click mask follow failed: {e.Message}", "YouTubePostTextEdit"); }
        }
    }

    // ==== 入力欄の開閉 ====

    // 検索欄クローンの見た目 (root 0.3×0.59・文字 3.5×2) と同じ文字の縦横比のまま、横幅だけ広げる
    private static readonly Vector3 FieldWorldScale = new(0.8f, 0.59f, 1f);
    private static readonly Vector3 FieldTextScale = new(1.3125f, 2f, 1f);

    private static FreeChatInputField InputFieldInstance;

    // FixInputChatField / TextBoxPatch / ControlPatch が検索欄・数値入力欄と同じ扱いにするために参照する
    public static FreeChatInputField ActiveField => InputFieldInstance;
    public static bool IsInputOpen => IsOpen;

    private static YouTubePostTextKind TargetKind;
    private static ToggleOption TargetRow;
    private static bool IsOpen;
    private static Collider2D SuppressedScrollHitbox;
    private static int OpenedFrame;
    private static string PendingPrefill;
    private static bool FirstFrame;

    private static bool EnsureInputField()
    {
        if (InputFieldInstance) return true;

        FreeChatInputField freeChatField = HudManager.InstanceExists ? HudManager.Instance.Chat?.freeChatField : null;
        if (!freeChatField)
        {
            Logger.Warn("freeChatField unavailable — text editor skipped this time", "YouTubePostTextEdit");
            return false;
        }

        InputFieldInstance = UnityEngine.Object.Instantiate(freeChatField);
        InputFieldInstance.name = "YouTubePostTextEditField";
        UnityEngine.Object.DontDestroyOnLoad(InputFieldInstance.gameObject);

        // 送信ボタンと文字数カウンタはチャットの名残。確定は Enter、取り消しは Esc。
        Transform sendButton = InputFieldInstance.transform.FindChild("ChatSendButton");
        if (sendButton) sendButton.gameObject.SetActive(false);
        if (InputFieldInstance.charCountText) InputFieldInstance.charCountText.gameObject.SetActive(false);

        if (InputFieldInstance.textArea) InputFieldInstance.textArea.ClearOnFocus = false;

        // クローン元の AspectPosition が OnEnable/Update で位置を画面端基準へ書き戻すので外す。
        // enabled=false を先に立てないと、Destroy 反映前の SetActive(true) で OnEnable が1回走る。
        foreach (AspectPosition ap in InputFieldInstance.GetComponentsInChildren<AspectPosition>(true))
        {
            ap.enabled = false;
            UnityEngine.Object.Destroy(ap);
        }

        InputFieldInstance.gameObject.SetActive(false);
        return true;
    }

    private static void OpenFor(ToggleOption row, YouTubePostTextKind kind)
    {
        if (!row) return;

        // マスク無しのボタンはスクロールで隠れた行でも押せてしまうので、行がチェックボックスのマスク内にある時だけ開く
        var toggleButton = row.GetComponentInChildren<PassiveButton>();
        Collider2D rowMask = toggleButton ? toggleButton.ClickMask : null;
        if (rowMask)
        {
            float y = row.transform.position.y;
            Bounds b = rowMask.bounds;
            if (y < b.min.y || y > b.max.y) return;
        }

        if (!EnsureInputField()) return;

        TargetKind = kind;
        TargetRow = row;

        // 行の子にすると行の見出しや背景の後ろに描かれて文字が見えない (実測)。検索欄と同じ
        // 設定メニュー直下に置き、押した行のすぐ下に横長で開く。
        Transform menuT = GameSettingMenu.Instance ? GameSettingMenu.Instance.transform : row.transform;
        Transform t = InputFieldInstance.transform;
        t.SetParent(menuT, true);

        // 欄の上でのクリックが後ろのスクローラーに届いてドラッグ扱いになるのを止める
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
        catch (Exception e) { Logger.Warn($"scroll hitbox suppress failed: {e.Message}", "YouTubePostTextEdit"); }

        Vector3 parentLossy = menuT.lossyScale;
        t.localScale = new Vector3(
            parentLossy.x != 0f ? FieldWorldScale.x / parentLossy.x : FieldWorldScale.x,
            parentLossy.y != 0f ? FieldWorldScale.y / parentLossy.y : FieldWorldScale.y,
            parentLossy.z != 0f ? FieldWorldScale.z / parentLossy.z : FieldWorldScale.z);

        if (InputFieldInstance.textArea && InputFieldInstance.textArea.outputText)
            InputFieldInstance.textArea.outputText.transform.localScale = FieldTextScale;

        // 開いた状態を先に立てる: 以降で例外が出ても Pump が回って Esc / 欄の外クリックで閉じられる
        string prefill = YouTubePostTexts.ToEditorText(kind);
        PendingPrefill = prefill;
        IsOpen = true;
        OpenedFrame = Time.frameCount;
        FirstFrame = true;

        ComputeAnchor(row);

        InputFieldInstance.gameObject.SetActive(true);
        ShowHint(GetString($"YouTubePostTextEdit.Hint.{kind}"));

        PinToAnchor();
        InputFieldInstance.textArea.characterLimit = kind == YouTubePostTextKind.Rotation ? RotationTextLimit : SingleTextLimit;
        InputFieldInstance.Focus();
        // 文は次のフレーム (Pump の初回) で入れる: 有効化した同じフレームでは TMP の描画が追いつかず、
        // 文字列は入っても画面に出ない (実測)

        Logger.Info($"text editor opened: kind={kind} rowWorld={row.transform.position} fieldWorld={t.position} fieldLossy={t.lossyScale}", "YouTubePostTextEdit");
    }

    // 文字を入れた同じフレームでは TMP の文字情報がまだ古く、SetPipePosition がキャレット位置を引けずに
    // IndexOutOfRange を投げる (数値より長い文を入れた時に実測)。文字自体は入っているので、メッシュを作り直して
    // キャレットだけ置き直す。
    private static void SetFieldText(TextBoxTMP area, string text, int caret = -1)
    {
        try { TextBoxPatch.SetChatFieldText(area, text, caret); }
        catch (Exception e)
        {
            Logger.Info($"set text: caret placement retried ({e.GetType().Name})", "YouTubePostTextEdit");
            try
            {
                if (area.outputText) area.outputText.ForceMeshUpdate();
                area.SetPipePosition();
            }
            catch { /* キャレットの表示位置だけの問題。次の入力で置き直される */ }
        }
    }

    // 欄の左上を合わせる位置 (世界座標)。左端は検索欄にそろえ、上端は押した行の下端。
    // 文が折り返すと背景を縦に伸ばすので、上端を固定して下へ伸ばす (下端固定だと画面外へはみ出す)。
    private static Vector3 Anchor;

    private static void ComputeAnchor(ToggleOption row)
    {
        Vector3 p = row.transform.position;
        float left = p.x - 1f;
        FreeChatInputField search = GameSettingMenuPatch.InputField;
        if (search && search.Background) left = search.Background.bounds.min.x;

        float top = p.y - 0.25f;
        Transform button = row.transform.FindChild(ButtonName);
        var buttonSprite = button ? button.GetComponentInChildren<SpriteRenderer>() : null;
        if (buttonSprite) top = buttonSprite.bounds.min.y - 0.05f;

        // 一覧の行より手前 (行は z が小さいほど手前)
        Anchor = new Vector3(left, top, p.z - 5f);
    }

    // 欄の基準点 (pivot) の位置はクローン元の作り次第なので、背景の実寸から左上を合わせる。
    private static void PinToAnchor()
    {
        Transform t = InputFieldInstance.transform;
        SpriteRenderer bg = InputFieldInstance.Background;
        if (!bg)
        {
            t.position = Anchor;
            return;
        }

        // 折り返した行数に背景の高さを合わせる。この欄では TextHeight が伸びない (実測) ので、
        // 文字のメッシュの寸法 (TMP の bounds は自分の座標系) を背景の座標系へ直して使う。
        TextMeshPro outText = InputFieldInstance.textArea ? InputFieldInstance.textArea.outputText : null;
        float bgScaleY = bg.transform.lossyScale.y;
        if (outText && bgScaleY > 0f)
        {
            Vector2 size = bg.size;
            float h = Math.Max(0.62f, outText.bounds.size.y * outText.transform.lossyScale.y / bgScaleY + 0.2f);
            if (Math.Abs(size.y - h) > 1e-3f) bg.size = new Vector2(size.x, h);
        }

        Bounds b = bg.bounds;
        Vector3 want = new(t.position.x + (Anchor.x - b.min.x), t.position.y + (Anchor.y - b.max.y), Anchor.z);
        if ((t.position - want).sqrMagnitude > 1e-6f) t.position = want;
    }

    // 案内文 (使える差し込み記号と操作) は設定画面左の説明欄に出す。下端の通知は設定画面の裏に隠れて見えず、
    // 欄の外に自前で置いた文字は一覧の行のマスクや描画順に負けて見えない (実測)。
    private static string HintShown;
    private static string HintSavedText;

    private static TextMeshPro DescriptionText => GameSettingMenu.Instance ? GameSettingMenu.Instance.MenuDescriptionText : null;

    private static void ShowHint(string hint)
    {
        TextMeshPro desc = DescriptionText;
        if (!desc) return;

        if (HintShown == null) HintSavedText = desc.text;
        HintShown = hint;
        desc.SetText(hint);
    }

    // 行にカーソルを乗せると説明欄が書き換わるので、開いている間はときどき書き戻す
    private static void KeepHint()
    {
        if (HintShown == null || Time.frameCount % 15 != 0) return;
        TextMeshPro desc = DescriptionText;
        if (desc) desc.SetText(HintShown);
    }

    private static void HideHint()
    {
        if (HintShown == null) return;
        TextMeshPro desc = DescriptionText;
        if (desc && HintSavedText != null) desc.SetText(HintSavedText);
        HintShown = null;
        HintSavedText = null;
    }

    private static void RestoreScrollHitbox()
    {
        if (SuppressedScrollHitbox) SuppressedScrollHitbox.enabled = true;
        SuppressedScrollHitbox = null;
    }

    private static void Close()
    {
        IsOpen = false;
        FirstFrame = false;
        PendingPrefill = null;
        RestoreScrollHitbox();
        TargetRow = null;

        HideHint();

        if (!InputFieldInstance) return;

        try { InputFieldInstance.Unfocus(); }
        catch { /* ignore */ }

        try { InputFieldInstance.ForceKeyboardClose(); }
        catch { /* ignore */ }

        InputFieldInstance.gameObject.SetActive(false);
        InputFieldInstance.transform.SetParent(null, true); // 行の破棄 (再ホスト等) に巻き込まれないよう外す
        UnityEngine.Object.DontDestroyOnLoad(InputFieldInstance.gameObject); // 行の配下に入れた時点で DDOL が外れているので掛け直す
    }

    private static void Confirm()
    {
        string text = TextBoxPatch.SafeChatText(InputFieldInstance.textArea);
        YouTubePostTextKind kind = TargetKind;
        Close();

        bool reset = YouTubePostTexts.SaveFromEditor(kind, text);

        string result = reset
            ? GetString(kind == YouTubePostTextKind.Rotation ? "YouTubePostTextEdit.RotationCleared" : "YouTubePostTextEdit.Reset")
            : string.Format(GetString("YouTubePostTextEdit.Saved"), YouTubePostTexts.Preview(kind));

        // 結果も説明欄に出す (次に行へカーソルを乗せるまで残る)
        TextMeshPro desc = DescriptionText;
        if (desc) desc.SetText(result);
        Logger.Info($"saved: kind={kind} reset={reset}", "YouTubePostTextEdit");
    }

#if !ANDROID
    // 入力欄の Update は TextBoxPatch が置き換えており、Ctrl+V はチャット欄専用の処理しか無いので自前で受ける
    private static void HandlePaste()
    {
        if (!(Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) || !Input.GetKeyDown(KeyCode.V)) return;

        TextBoxTMP area = InputFieldInstance.textArea;
        if (!area) return;

        string clip = (GUIUtility.systemCopyBuffer ?? "").Replace("\r", "").Trim();
        if (clip.Length == 0) return;

        clip = clip.Replace("\n", TargetKind == YouTubePostTextKind.Rotation ? $" {YouTubePostTexts.RotationSeparator} " : " ");

        string current = TextBoxPatch.SafeChatText(area);
        int at = Math.Clamp(area.caretPos, 0, current.Length);
        int room = Math.Max(0, area.characterLimit - current.Length);
        if (clip.Length > room) clip = clip[..room];
        if (clip.Length == 0) return;

        SetFieldText(area, current.Insert(at, clip), at + clip.Length);
    }
#endif

    // バニラの TextBoxTMP.SetText は「{」「}」などを捨てるので、差し込み記号が打てない。チャット欄は
    // TextBoxPatch.AllowAllCharacters が置き換えているが、あちらはチャット欄の文字列の控えと一体なので使えない。
    // この入力欄だけ、改行を落として文字数上限で切るだけの SetText に置き換える。
    [HarmonyPatch(typeof(TextBoxTMP), nameof(TextBoxTMP.SetText))]
    private static class AllowAllCharactersPatch
    {
        public static bool Prefix(TextBoxTMP __instance, [HarmonyArgument(0)] string input, [HarmonyArgument(1)] string inputCompo)
        {
            if (!InputFieldInstance || __instance != InputFieldInstance.textArea) return true;

            input ??= "";
            inputCompo ??= "";

            string before;
            try { before = __instance.text ?? ""; }
            catch { before = ""; }

            var sb = new System.Text.StringBuilder(input.Length);
            foreach (char c in input)
            {
                if (c is '\r' or '\n') continue;
                if (c == '\b')
                {
                    if (sb.Length > 0) sb.Length--;
                    continue;
                }

                sb.Append(c);
            }

            if (__instance.characterLimit > 0 && sb.Length > __instance.characterLimit) sb.Length = __instance.characterLimit;

            string text = sb.ToString();
            __instance.AdjustCaretPosition(text.Length - before.Length);
            __instance.text = text;
            __instance.compoText = inputCompo;
            __instance.outputText.text = text + inputCompo;
            __instance.outputText.ForceMeshUpdate(true, true);
            __instance.OnChange.Invoke();

            try { __instance.SetPipePosition(); }
            catch { /* キャレットの表示位置だけの問題。次の入力で置き直される */ }

            return false;
        }
    }

    // 作ったばかりの欄は、有効化した次のフレームにゲーム側の初期化が TextBoxTMP.Clear を呼んで中身を消す
    // (実測: 開いた時に入れた文が1フレーム後に空になる)。開いている間の消去はこの欄では使わないので止める。
    [HarmonyPatch(typeof(TextBoxTMP), nameof(TextBoxTMP.Clear))]
    private static class KeepTextWhileOpenPatch
    {
        public static bool Prefix(TextBoxTMP __instance) => !(IsOpen && InputFieldInstance && __instance == InputFieldInstance.textArea);
    }

    // ==== Enter で保存・Esc で取り消し ====

    [HarmonyPatch(typeof(ChatController), nameof(ChatController.Update))]
    private static class PumpPatch
    {
        public static void Postfix()
        {
            if (!IsOpen) return;

            try { Pump(); }
            catch (Exception e)
            {
                Logger.Warn($"text editor pump failed: {e.Message}", "YouTubePostTextEdit");
                Close();
            }
        }
    }

    private static void Pump()
    {
        if (!InputFieldInstance || !InputFieldInstance.gameObject.activeSelf || !TargetRow || !GameSettingMenu.Instance || !GameSettingMenu.Instance.isActiveAndEnabled)
        {
            Close();
            return;
        }

        PinToAnchor();
        KeepHint();

        // 検索欄と同じ配色 (暗い背景に白文字)。クローン元のチャット欄は白背景に黒文字なので毎フレーム塗り直す
        if (InputFieldInstance.background) InputFieldInstance.background.color = new Color32(40, 40, 40, byte.MaxValue);
        if (InputFieldInstance.textArea && InputFieldInstance.textArea.outputText) InputFieldInstance.textArea.outputText.color = Color.white;

        if (FirstFrame)
        {
            FirstFrame = false;

            if (PendingPrefill != null) SetFieldText(InputFieldInstance.textArea, PendingPrefill);

            PendingPrefill = null;

        }

        // 欄の外をクリックしたら取り消し (欄の上のクリックはフォーカス取り直しに使う)
        if (Input.GetMouseButtonDown(0) && Camera.main && Time.frameCount > OpenedFrame + 1)
        {
            Vector2 world = Camera.main.ScreenToWorldPoint(Input.mousePosition);
            bool onField = false;
            // OverlapPointAll はエンジンコードを削ったプレイヤーにネイティブ実装が無いので、ごく小さい円で点判定する
            foreach (var h in Physics2D.OverlapCircleAll(world, 0.001f))
                if (h && h.transform.IsChildOf(InputFieldInstance.transform)) { onField = true; break; }

            if (!onField)
            {
                Close();
                return;
            }
        }

        if (InputFieldInstance.textArea && !InputFieldInstance.textArea.hasFocus)
            InputFieldInstance.textArea.GiveFocus();

        if (Input.GetKeyDown(KeyCode.Escape))
        {
            Close();
            return;
        }

#if !ANDROID
        HandlePaste();
#endif

        if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
            Confirm();
    }
}
