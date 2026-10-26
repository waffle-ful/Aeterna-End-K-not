using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BepInEx;
using EndKnot.Patches;
using HarmonyLib;
using TMPro;
using UnityEngine;
using static EndKnot.Translator;

namespace EndKnot.Modules;

// 右上のボタンから開く、ゲーム内のバグ報告。送り先は /report と同じ relay の報告窓口 (LobbyShare.SubmitReport)。
// 自分のログ (LogOutput.log の末尾・log.html・Health.log) を zip にして添えられる。フレンドコードと
// Windows のユーザーフォルダ名は zip にする前に伏せる。画面は自分の手元だけで、RPC も他人への送信も無い。
public static class BugReport
{
    private const int TextMax = 400; // LobbyShare.ReportTextMax と同じ

    private const long LogOutputTailBytes = 6L * 1024 * 1024;
    private const long HtmlTailBytes = 6L * 1024 * 1024;
    private const long HealthTailBytes = 2L * 1024 * 1024;
    private const int ZipMaxBytes = 7 * 1024 * 1024; // relay の上限 (8MB) より少し下

    // 本編の右上は板が重なって 1 本につながる組と、少し隙間を空けた別の組がある。虫は別の組として置く
    private const float ButtonSpacingFallback = 0.92f;
    private const int PanelSortingOrder = 30000;

    // 入力の箱。フィードバックの窓らしく最初から数行ぶんの高さを取り、文が溢れたら下へ伸ばす (窓も一緒に伸びる)
    private const float BoxMinHeight = 1.45f;
    private const float BoxBorder = 0.01f;

    private const float Pad = 0.4f;
    private const float BoxTop = 0.825f;
    private const float FooterHeight = 1.3f;
    private const float FooterTop = -1.975f + FooterHeight; // 窓の下端から下の段の高さだけ上
    private const float FooterRow = -FooterHeight / 2f;  // FooterTop から見た下の段の中心 (枠の内側に 0.2 ほど空く)

    // 絵が見つからなかった時の代わりの角丸
    private const float CardRadius = 0.12f;
    private const float PartRadius = 0.05f;
    private const float CheckSize = 0.3f;

    private static readonly Vector2 CardSize = new(6.0f, 3.95f);

    // 窓・入力欄・ボタンは、ゲームが読み込んでいる本編の絵を借りて 9 分割で伸ばす (右のロビーの板と同じ枠になる)。
    // ppu は画面上の縁の太さを本編の板とそろえる値 (HUD は 1 単位 ≒ 176px @1080p)
    private const string PaneSpriteName = "IMAGE-LobbyInfoPane";      // 438×648・縁 40px
    private const string FieldSpriteName = "IMAGE-FieldCube";         // 116×116・縁 45px
    private const string ButtonSpriteName = "buttonbase_kickBan";     // 59×26・白地なので色を掛けて使う
    private const string CheckSpriteName = "BUTTON-CheckboxInactive"; // 118×118
    private const string CheckHoverSpriteName = "BUTTON-CheckboxHighlight";
    private const string CheckMarkSpriteName = "IMAGE-CheckMark";     // 60×48
    private const string ExitSpriteName = "BUTTON-ExitStandard";      // 98×98
    private const string ExitHoverSpriteName = "BUTTON-ExitHover";
    private static readonly string[] VanillaNames = [PaneSpriteName, FieldSpriteName, ButtonSpriteName, CheckSpriteName, CheckHoverSpriteName, CheckMarkSpriteName, ExitSpriteName, ExitHoverSpriteName];
    private const float PanePpu = 176f;
    private const float FieldPpu = 352f;
    private const float ButtonPpu = 100f;
    private const float PaneBorderPx = 40f;
    private static readonly Vector3 FieldWorldScale = new(1.05f, 0.59f, 1f);
    // 欄の倍率は横 1.05・縦 0.59 なので、字の縦横を揃えるには縦を 1.05/0.59 倍する。0.8 で本文と同じくらいの字の大きさ
    private const float FieldTextSize = 0.8f;
    private static readonly Vector3 FieldTextScale = new(FieldTextSize, FieldTextSize * 1.05f / 0.59f, 1f);

    // 色はロビーの板と「編集」ボタンの実測から。地は青みの無い灰、字は白、押す物は本編の青
    private static readonly Color32 BackdropColor = new(10, 10, 10, 165);
    private static readonly Color32 CardColor = new(41, 41, 41, 255);         // 板の中身
    private static readonly Color32 CardEdgeColor = new(82, 85, 90, 255);     // 板の縁 (絵が無い時の代わり)
    private static readonly Color32 BoxColor = new(18, 18, 18, 255);          // 入力欄の中身
    private static readonly Color32 BoxEdgeColor = new(76, 76, 76, 255);
    private static readonly Color32 AccentColor = new(0, 150, 200, 255);      // 「編集」の青
    private static readonly Color32 AccentHover = new(40, 185, 235, 255);
    private static readonly Color32 CheckMarkColor = new(44, 232, 200, 255);  // 本編のチェックの色
    private static readonly Color32 TextColor = new(236, 236, 236, 255);
    private static readonly Color32 MutedColor = new(178, 178, 178, 255);
    private static readonly Color32 FaintColor = new(118, 118, 118, 255);
    private static readonly Color32 GhostColor = new(96, 100, 106, 255);
    private static readonly Color32 GhostHover = new(128, 133, 140, 255);

    private static GameObject _hudButton;
    private static float _spacing = ButtonSpacingFallback;

    private static GameObject _panel;
    private static SpriteRenderer _card;
    private static SpriteRenderer _cardBorder;
    private static Transform _footer;
    private static SpriteRenderer _box;
    private static SpriteRenderer _boxInner;
    private static TextMeshPro _placeholder;
    private static TextMeshPro _counter;
    private static Transform _fieldAnchor;
    private static TextMeshPro _status;
    private static SpriteRenderer _checkBorder;
    private static SpriteRenderer _checkFill;
    private static GameObject _checkMark;
    private static FreeChatInputField _field;
    // 本編の絵から作った 9 分割のスプライト (名前@ppu ごと)。元の絵のテクスチャを参照するので、元が捨てられても残る
    private static readonly System.Collections.Generic.Dictionary<string, Sprite> VanillaSprites = [];
    private static Sprite _solid;
    private static Texture2D _roundTex;
    private static readonly System.Collections.Generic.Dictionary<int, Sprite> RoundSprites = [];
    private static TextMeshPro _textTemplate;
    private static string _counterText;
    private static Sprite _icon;
    private static Sprite _iconActive;
    private static Sprite _iconSelected;

    private static bool _attachLog = true;
    private static bool _busy;
    private static int _openedFrame;
    private static float _busySince;

    // 欄の中身の控え。SetText の前置きで受け取った値だけを使い、毎フレームゲーム側の文字列 (特に変換中の文字) を読まない
    internal static string MirrorText = string.Empty;
    internal static string MirrorCompo = string.Empty;
    internal static bool LayoutDirty = true;

    // 裏スレッドから戻る結果。メインスレッドの Tick で受け取る
    private static volatile bool _zipReady;
    private static byte[] _zip;
    private static volatile bool _resultReady;
    private static int _resultCode;
    private static string _pendingText;

    // 窓は HudManager の子なので、開いたまま部屋を抜けると窓ごと消える。印だけ残らないよう窓の生存も見る
    public static bool IsOpen
    {
        get => _open && Il2Direct.Alive(_panel);
        private set => _open = value;
    }

    private static bool _open;

    // TextBoxPatch / FixInputChatField / ControlPatch が他の入力欄と同じ扱いにするために参照する
    public static FreeChatInputField ActiveField => _field;

    // HudManager.Update の後置から毎フレーム
    internal static void Tick(HudManager hud)
    {
        try
        {
            DrainResults();

            // 送信の返事が来ないまま (HTTP の上限 60 秒を過ぎても) 止まった時の保険
            if (_busy && Time.realtimeSinceStartup - _busySince > 75f)
            {
                _busy = false;
                SetStatus(string.Format(GetString("BugReport.Failed"), -1));
            }

            PlayerControl me = PlayerControl.LocalPlayer;
            bool show = Il2Direct.Alive(me) && Il2Direct.Alive(hud.SettingsButton) && hud.SettingsButton.activeInHierarchy && (IsOpen || !CleanShot.On) && (GameStates.IsLobby || !GameStates.IsEnded); // 終了の印は次の試合開始まで残る

            EnsureHudButton(hud);
            if (Il2Direct.Alive(_hudButton))
            {
                if (Il2Direct.ActiveSelf(_hudButton) != show) _hudButton.SetActive(show);
                if (show && (Il2Direct.FrameCount & 7) == 0) PlaceHudButton(hud);
            }

            if (IsOpen) Pump();
        }
        catch (Exception e)
        {
            Logger.Warn($"tick failed: {e.Message}", "BugReport");
            if (IsOpen) Close();
        }
    }

    // ==== 右上のボタン ====

    private static void EnsureHudButton(HudManager hud)
    {
        if (Il2Direct.Alive(_hudButton) || !Il2Direct.Alive(hud.SettingsButton)) return;

        // 設定のボタンを型紙にして、右上の他のボタンと同じ見た目・同じ押し心地にする
        _hudButton = Object.Instantiate(hud.SettingsButton, hud.SettingsButton.transform.parent);
        _hudButton.name = "EndKnotBugReportButton";

        foreach (AspectPosition ap in _hudButton.GetComponentsInChildren<AspectPosition>(true))
        {
            ap.enabled = false;
            Object.Destroy(ap);
        }

        // 歯車の絵は Inactive / Active / Selected の 3 枚 (102×92px・100ppu)。後ろの板 (Background) とコントローラーの記号はそのまま使う。
        // 虫の絵は本編の 3 枚の地 (影・下の段差・縁) をそのまま使い、歯車だけ描き替えた 2 倍解像度 (204×184px) なので 200ppu で同じ大きさになる
        _icon ??= Utils.LoadSprite("EndKnot.Resources.Images.BugReportIcon.png", 200f);
        _iconActive ??= Utils.LoadSprite("EndKnot.Resources.Images.BugReportIconActive.png", 200f);
        _iconSelected ??= Utils.LoadSprite("EndKnot.Resources.Images.BugReportIconSelected.png", 200f);
        foreach (SpriteRenderer sr in _hudButton.GetComponentsInChildren<SpriteRenderer>(true))
        {
            Sprite replace = sr.name switch
            {
                "Inactive" => _icon,
                "Active" => _iconActive,
                "Selected" => _iconSelected,
                _ => null
            };
            if (!replace || !sr.sprite) continue;

            // 元の絵の単位寸法に合わせて倍率を直す (本編の絵は ppu も部品の倍率もまちまち)
            Vector2 before = sr.sprite.bounds.size;
            sr.sprite = replace;
            Vector2 after = replace.bounds.size;
            if (after.x > 0f && after.y > 0f)
            {
                Vector3 s = sr.transform.localScale;
                sr.transform.localScale = new Vector3(s.x * before.x / after.x, s.y * before.y / after.y, s.z);
            }
        }

        var button = _hudButton.GetComponent<PassiveButton>();
        if (button)
        {
            button.OnClick = new();
            button.OnClick.AddListener((Action)Toggle);
        }

        PlaceHudButton(hud);
    }

    // 右上の並びで今いちばん左にある本編のボタンの、さらに左隣へ置く。
    // チャットや「?」の出し入れで並びが変わるので、ときどき置き直す
    private static void PlaceHudButton(HudManager hud)
    {
        Transform settings = hud.SettingsButton.transform;
        Vector3 s = settings.position;
        float left = s.x;

        if (Il2Direct.Alive(hud.MapButton) && hud.MapButton.gameObject.activeInHierarchy)
        {
            float mapX = hud.MapButton.transform.position.x;
            float gap = Math.Abs(mapX - s.x);
            if (gap is > 0.4f and < 1.5f) _spacing = gap;
            left = Math.Min(left, mapX);
        }

        if (Il2Direct.Alive(hud.MatchInfoButton) && hud.MatchInfoButton.gameObject.activeInHierarchy)
            left = Math.Min(left, hud.MatchInfoButton.transform.position.x);

        ChatController chat = hud.Chat;
        if (Il2Direct.Alive(chat) && Il2Direct.Alive(chat.chatButton) && chat.chatButton.gameObject.activeInHierarchy)
            left = Math.Min(left, chat.chatButton.transform.position.x);

        Vector3 want = new(left - _spacing, s.y, s.z);
        if ((_hudButton.transform.position - want).sqrMagnitude > 1e-6f) _hudButton.transform.position = want;
    }

    // ==== 報告の窓 ====

    private static void Toggle()
    {
        if (IsOpen) Close();
        else Open();
    }

    private static void Open()
    {
        if (!HudManager.InstanceExists) return;
        HudManager hud = HudManager.Instance;

        if (!EnsurePanel(hud)) return;

        IsOpen = true;
        LayoutDirty = true;
        _openedFrame = Time.frameCount;
        _panel.SetActive(true);
        _field.gameObject.SetActive(true);

        PlaceField();
        _field.textArea.characterLimit = TextMax;
        _field.Focus();
        if (!_busy) SetStatus(string.Empty);
        RefreshLogToggle();

        Logger.Info($"opened: fieldWorld={_field.transform.position} bg={(_field.Background ? _field.Background.bounds.size.ToString() : "-")}", "BugReport");
    }

    private static void Close()
    {
        IsOpen = false;

        if (_field)
        {
            try { _field.Unfocus(); }
            catch { /* ignore */ }

            try { _field.ForceKeyboardClose(); }
            catch { /* ignore */ }
        }

        if (_panel) _panel.SetActive(false);
    }

    private static bool EnsurePanel(HudManager hud)
    {
        if (_panel && _field) return true;
        if (_panel) Object.Destroy(_panel);

        TextMeshPro template = hud.KillButton ? hud.KillButton.cooldownTimerText : null;
        _textTemplate = template;
        FreeChatInputField chatField = hud.Chat ? hud.Chat.freeChatField : null;
        if (!template || !chatField)
        {
            Logger.Warn("no TMP template or chat field — panel skipped", "BugReport");
            return false;
        }

        _panel = new GameObject("EndKnotBugReportPanel") { layer = 5 };
        _panel.transform.SetParent(hud.transform, false);
        // 幕は右のロビーの板も含めて全部覆うので、窓は画面の真ん中に置く
        _panel.transform.localPosition = new Vector3(0f, -0.05f, -30f); // これよりカメラ側へ寄せると、手前の部品がカメラの手前側の面で切れる

        LoadVanillaSprites();

        // 画面全体を暗くする幕。後ろの本編のボタンや床へのクリックをここで受け止め、押されたら閉じる
        SpriteRenderer backdrop = MakeSolid("Backdrop", BackdropColor, new Vector3(0f, 0f, 0.9f), new Vector2(40f, 25f));
        MakeClickable(backdrop.gameObject, new Vector2(40f, 25f), Close, null, null);

        // 窓の枠はロビーの板の絵 (縁・影込み)。読み込まれていなければ同じ色の角丸で代わりに描く
        _card = MakeVanilla("Card", PaneSpriteName, new Vector3(0f, 0f, 0.6f), CardSize);
        _cardBorder = null;
        if (!_card)
        {
            _cardBorder = MakeRound("CardBorder", CardEdgeColor, new Vector3(0f, 0f, 0.7f), CardSize, CardRadius);
            _card = MakeRound("Card", CardColor, new Vector3(0f, 0f, 0.6f), CardSize - Vector2.one * 0.2f, CardRadius - 0.05f);
        }

        // 窓の上のクリックは幕まで抜けないよう、窓の地でも受け止める
        MakeClickable(_card.gameObject, Vector2.one, null, null, null);

        float left = -CardSize.x / 2f + Pad;
        float right = CardSize.x / 2f - Pad;
        float top = CardSize.y / 2f - 0.34f;
        float width = CardSize.x - Pad * 2f;

        TextMeshPro title = MakeText(template, _panel.transform, "Title", new Vector3(left, top - 0.02f, 0f), 2.0f, GetString("BugReport.Title"), FontStyles.Bold, TextColor, width - 0.6f);
        title.characterSpacing = 2f;
        MakeText(template, _panel.transform, "Hint", new Vector3(left, top - 0.5f, 0f), 1.1f, GetString("BugReport.Hint"), FontStyles.Normal, MutedColor, width);

        MakeCloseButton(new Vector3(right - 0.06f, top - 0.2f, -0.2f));

        // 入力の箱は本編の入力欄の絵 (灰の縁の黒い箱)
        _box = MakeVanilla("Box", FieldSpriteName, new Vector3(0f, 0f, 0.3f), Vector2.one);
        _boxInner = null;
        if (!_box)
        {
            _box = MakeRound("Box", BoxEdgeColor, new Vector3(0f, 0f, 0.3f), Vector2.one, PartRadius);
            _boxInner = MakeRound("BoxInner", BoxColor, new Vector3(0f, 0f, 0.2f), Vector2.one, PartRadius - BoxBorder);
        }

        _fieldAnchor = new GameObject("FieldAnchor").transform;
        _fieldAnchor.SetParent(_panel.transform, false);
        _fieldAnchor.localPosition = new Vector3(left + 0.08f, BoxTop - 0.06f, -1f);

        _placeholder = MakeText(template, _panel.transform, "Placeholder", new Vector3(left + 0.2f, BoxTop - 0.17f, -0.5f), 1.15f, GetString("BugReport.Placeholder"), FontStyles.Normal, FaintColor, width - 0.4f);

        _counterText = null;
        _counter = MakeText(template, _panel.transform, "Counter", Vector3.zero, 0.85f, string.Empty, FontStyles.Normal, FaintColor, 2f);
        _counter.alignment = TextAlignmentOptions.BottomRight;
        RectTransform crt = _counter.GetComponent<RectTransform>();
        if (crt) crt.pivot = new Vector2(1f, 0f);

        _field = Object.Instantiate(chatField);
        _field.name = "EndKnotBugReportField";
        Transform sendButton = _field.transform.FindChild("ChatSendButton");
        if (sendButton) sendButton.gameObject.SetActive(false);
        if (_field.charCountText) _field.charCountText.gameObject.SetActive(false);

        foreach (AspectPosition ap in _field.GetComponentsInChildren<AspectPosition>(true))
        {
            ap.enabled = false;
            Object.Destroy(ap);
        }

        _field.transform.SetParent(_panel.transform, true);
        Vector3 lossy = _panel.transform.lossyScale;
        _field.transform.localScale = new Vector3(
            lossy.x != 0f ? FieldWorldScale.x / lossy.x : FieldWorldScale.x,
            lossy.y != 0f ? FieldWorldScale.y / lossy.y : FieldWorldScale.y,
            1f);

        TextBoxTMP area = _field.textArea;
        if (area)
        {
            area.ClearOnFocus = false;
            area.AllowPaste = true;
            area.AllowSymbols = true;
            area.allowAllCharacters = true;
            area.characterLimit = TextMax;
            if (area.outputText)
            {
                area.outputText.transform.localScale = FieldTextScale;
                area.outputText.enableWordWrapping = true;
            }
        }

        // 下の段は窓の下端に敷いた一段暗い帯。箱が伸びた分だけまとめて下げる
        _footer = new GameObject("Footer") { layer = 5 }.transform;
        _footer.SetParent(_panel.transform, false);
        _footer.localPosition = new Vector3(0f, FooterTop, 0f);

        _status = MakeText(template, _footer, "Status", new Vector3(left, -0.03f, 0f), 1.0f, string.Empty, FontStyles.Normal, MutedColor, width);

        // ログを添付するかのチェック。本編の設定画面と同じ白い枠と緑のチェック。左端は見出し・入力の箱と同じ線にそろえ、文字の部分を押しても切り替わる
        var check = new GameObject("LogCheck") { layer = 5 }.transform;
        check.SetParent(_footer, false);
        check.localPosition = new Vector3(left + CheckSize / 2f, FooterRow + 0.1f, -0.2f);
        _checkBorder = MakeVanilla("Border", CheckSpriteName, Vector3.zero, Vector2.one * CheckSize, check);
        _checkFill = null;
        SpriteRenderer mark = MakeVanilla("Mark", CheckMarkSpriteName, new Vector3(0f, 0.01f, -0.04f), new Vector2(0.22f, 0.176f), check);
        if (_checkBorder && mark)
        {
            _checkMark = mark.gameObject;
        }
        else
        {
            if (_checkBorder) Object.Destroy(_checkBorder.gameObject);
            if (mark) Object.Destroy(mark.gameObject);
            _checkBorder = MakeRound("Border", TextColor, Vector3.zero, Vector2.one * CheckSize, 0.05f, check);
            _checkFill = MakeRound("Fill", BoxColor, new Vector3(0f, 0f, -0.02f), Vector2.one * (CheckSize - 0.06f), 0.03f, check);
            _checkMark = MakeCheckMark(check);
        }

        Sprite checkIdle = _checkBorder.sprite;
        Sprite checkHover = _checkFill ? null : VanillaSprites.TryGetValue(CheckHoverSpriteName, out Sprite ch) ? ch : null;
        SpriteRenderer checkBox = _checkBorder;
        var hit = new GameObject("Hit") { layer = 5 };
        hit.transform.SetParent(check, false);
        hit.transform.localPosition = new Vector3(1.1f, -0.1f, 0f);
        MakeClickable(hit, new Vector2(2.6f, 0.6f), () =>
            {
                _attachLog = !_attachLog;
                RefreshLogToggle();
            },
            () => { if (checkHover) checkBox.sprite = checkHover; },
            () => checkBox.sprite = checkIdle);

        // 文字の段: 見出し (チェックと同じ高さ) とその下の注記
        float textLeft = left + CheckSize + 0.16f;
        MakeText(template, _footer, "LogLabel", new Vector3(textLeft, FooterRow + 0.22f, 0f), 1.1f, GetString("BugReport.AttachLog"), FontStyles.Normal, TextColor, 2.5f);
        MakeText(template, _footer, "LogNote", new Vector3(textLeft, FooterRow - 0.02f, 0f), 0.82f, GetString("BugReport.LogNote"), FontStyles.Normal, FaintColor, 2.5f);

        var sendSize = new Vector2(1.4f, 0.5f);
        var cancelSize = new Vector2(1.0f, 0.5f);
        MakeButton("Send", new Vector3(right - sendSize.x / 2f, FooterRow, -0.2f), sendSize, AccentColor, AccentHover, GetString("BugReport.Send"), Send);
        MakeButton("Cancel", new Vector3(right - sendSize.x - 0.12f - cancelSize.x / 2f, FooterRow, -0.2f), cancelSize, GhostColor, GhostHover, GetString("BugReport.Close"), Close);

        // ロビーの表示物や本編のボタンは描画順が高いものがあり、z だけでは窓の上に出てくる。
        // 地の部品は決まった順に、それ以外 (文字・入力欄・ボタン) はその上の段へまとめて持ち上げる
        // il2cpp の物は包みが毎回別なので、同じ物かは InstanceID で見る
        SpriteRenderer[] layered = [backdrop, _cardBorder, _card, _box, _boxInner];
        var layerOf = new System.Collections.Generic.Dictionary<int, int>();
        for (var i = 0; i < layered.Length; i++)
            if (layered[i]) layerOf[layered[i].GetInstanceID()] = i;
        foreach (Renderer r in _panel.GetComponentsInChildren<Renderer>(true))
            r.sortingOrder = layerOf.TryGetValue(r.GetInstanceID(), out int at) ? PanelSortingOrder + at : PanelSortingOrder + 20 + Math.Clamp(r.sortingOrder, 0, 50);

        foreach (SpriteRenderer sr in _panel.GetComponentsInChildren<SpriteRenderer>(true))
            sr.maskInteraction = SpriteMaskInteraction.None;

        LiftToOverlayQueue(backdrop.sharedMaterial);

        // HUD のカメラは UI レイヤー (5) しか映さない
        foreach (Transform child in _panel.GetComponentsInChildren<Transform>(true))
            child.gameObject.layer = 5;

        RefreshLogToggle();
        _panel.SetActive(false);
        return true;
    }

    // ロビーの表示物の一部は描画キュー 4000 (Overlay) の材質で描かれていて、sortingOrder をいくら上げても
    // その手前に出られない。窓の部品の材質も 4000 へ移す。複製は元の材質ごとに 1 つだけ作って使い回す
    // (renderer.material の複製は Destroy で回収されないので自分で持つ)
    private static readonly System.Collections.Generic.Dictionary<int, Material> OverlayMaterials = [];

    private static Material OverlayCopy(Material src, bool flatText = false)
    {
        if (!src) return src;
        int key = src.GetInstanceID() * 2 + (flatText ? 1 : 0);
        if (OverlayMaterials.TryGetValue(key, out Material m) && m) return m;
        m = new Material(src) { renderQueue = 4000, hideFlags = HideFlags.HideAndDontSave };

        // 型紙の文字 (キルの残り秒) は太い縁取りと影付き。窓の文字は縁取り無しの平らな字にする (ボタンの字だけは本編のボタンと同じく縁取りを残す)
        if (flatText)
        {
            m.DisableKeyword("OUTLINE_ON");
            m.DisableKeyword("UNDERLAY_ON");
            if (m.HasProperty("_OutlineWidth")) m.SetFloat("_OutlineWidth", 0f);
            if (m.HasProperty("_FaceDilate")) m.SetFloat("_FaceDilate", 0f);
            if (m.HasProperty("_UnderlayColor")) m.SetColor("_UnderlayColor", Color.clear);
        }

        OverlayMaterials[key] = m;
        return m;
    }

    // スプライトは全部、素の材質にそろえる
    private static void LiftToOverlayQueue(Material plainSprite)
    {
        foreach (TextMeshPro t in _panel.GetComponentsInChildren<TextMeshPro>(true))
            t.fontSharedMaterial = OverlayCopy(t.fontSharedMaterial, t.name != ButtonLabelName);

        foreach (SpriteRenderer sr in _panel.GetComponentsInChildren<SpriteRenderer>(true))
            sr.sharedMaterial = OverlayCopy(plainSprite);
    }

    private static SpriteRenderer MakeSolid(string name, Color32 color, Vector3 pos, Vector2 size)
    {
        var go = new GameObject(name) { layer = 5 };
        go.transform.SetParent(_panel.transform, false);
        go.transform.localPosition = pos;
        go.transform.localScale = new Vector3(size.x, size.y, 1f);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = Solid();
        sr.color = color;
        return sr;
    }

    // 角の丸い四角。9 分割の絵なので大きさを変えても角の丸みは radius のまま
    private static SpriteRenderer MakeRound(string name, Color32 color, Vector3 pos, Vector2 size, float radius, Transform parent = null)
    {
        var go = new GameObject(name) { layer = 5 };
        go.transform.SetParent(parent ? parent : _panel.transform, false);
        go.transform.localPosition = pos;
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = RoundSprite(radius);
        sr.drawMode = SpriteDrawMode.Sliced;
        sr.size = size;
        sr.color = color;
        return sr;
    }

    // 本編の絵の 9 分割で板を作る。絵が読み込まれていなければ null (呼び出し側が角丸で代わりに描く)
    private static SpriteRenderer MakeVanilla(string name, string spriteName, Vector3 pos, Vector2 size, Transform parent = null)
    {
        if (!VanillaSprites.TryGetValue(spriteName, out Sprite s) || !s) return null;
        var go = new GameObject(name) { layer = 5 };
        go.transform.SetParent(parent ? parent : _panel.transform, false);
        go.transform.localPosition = pos;
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = s;
        sr.drawMode = SpriteDrawMode.Sliced;
        sr.size = size;
        return sr;
    }

    // 使う本編の絵を名前で探し、縁を指定した 9 分割のスプライトに作り直して持っておく。
    // 全部の絵をなめる走査は重い (15〜70ms) ので、欠けがある時に窓を作る時だけ行う。元の絵そのものは持たない
    private static void LoadVanillaSprites()
    {
        if (Array.TrueForAll(VanillaNames, n => VanillaSprites.TryGetValue(n, out Sprite have) && have)) return;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (Object o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<Sprite>()))
        {
            Sprite src = o != null ? o.TryCast<Sprite>() : null;
            if (!src) continue;

            string nm = src.name;
            if (Array.IndexOf(VanillaNames, nm) < 0 || (VanillaSprites.TryGetValue(nm, out Sprite have) && have)) continue;

            try
            {
                // ロビーの板の絵は 9 分割の縁が設定されていないので、縁と角の丸みが収まる幅を自分で与える
                Vector4 border = nm == PaneSpriteName ? Vector4.one * PaneBorderPx : src.border;
                float ppu = nm switch
                {
                    PaneSpriteName => PanePpu,
                    FieldSpriteName => FieldPpu,
                    ButtonSpriteName => ButtonPpu,
                    _ => 100f
                };
                Sprite s = Sprite.Create(src.texture, src.textureRect, new Vector2(0.5f, 0.5f), ppu, 0, SpriteMeshType.FullRect, border);
                s.name = "EndKnot_" + nm;
                s.hideFlags |= HideFlags.HideAndDontSave;
                VanillaSprites[nm] = s;
            }
            catch (Exception e)
            {
                Logger.Warn($"vanilla sprite {nm} unusable: {e.Message}", "BugReport");
            }
        }

        Logger.Info($"vanilla sprites: {VanillaSprites.Count}/{VanillaNames.Length} in {sw.ElapsedMilliseconds}ms", "BugReport");
    }

    // 押せる物にする。PassiveButton の 3 つのイベントは new しないと実マウスで NRE になる
    private static PassiveButton MakeClickable(GameObject go, Vector2 size, Action onClick, Action onOver, Action onOut)
    {
        var col = go.AddComponent<BoxCollider2D>();
        col.size = size;
        var btn = go.AddComponent<PassiveButton>();
        btn.Colliders = new Collider2D[] { col };
        btn.OnClick = new();
        btn.OnMouseOver = new();
        btn.OnMouseOut = new();
        btn.ClickSound = null;
        if (onClick != null) btn.OnClick.AddListener(onClick);
        if (onOver != null) btn.OnMouseOver.AddListener(onOver);
        if (onOut != null) btn.OnMouseOut.AddListener(onOut);
        return btn;
    }

    private const string ButtonLabelName = "Label";

    // 本編のボタンの絵 (濃い縁・上の照り・下の影) に色を掛け、縁取りの太字を載せる
    private static void MakeButton(string name, Vector3 pos, Vector2 size, Color32 fill, Color32 hover, string label, Action onClick)
    {
        var root = new GameObject(name) { layer = 5 }.transform;
        root.SetParent(_footer, false);
        root.localPosition = pos;

        SpriteRenderer bg = MakeVanilla("Fill", ButtonSpriteName, Vector3.zero, size, root);
        if (bg) bg.color = fill;
        else bg = MakeRound("Fill", fill, Vector3.zero, size, PartRadius, root);

        TextMeshPro text = MakeText(_textTemplate, root, ButtonLabelName, new Vector3(0f, 0.02f, -0.05f), 1.4f, label, FontStyles.Bold, TextColor, size.x);
        text.alignment = TextAlignmentOptions.Center;
        RectTransform rt = text.GetComponent<RectTransform>();
        if (rt)
        {
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(size.x, size.y);
        }

        MakeClickable(root.gameObject, size, onClick, () => bg.color = hover, () => bg.color = fill);
    }

    // 右上の「×」。本編のメニューの閉じるボタンの絵 (乗ると緑)。無ければ細い棒 2 本で描く
    private static void MakeCloseButton(Vector3 pos)
    {
        var root = new GameObject("Close") { layer = 5 }.transform;
        root.SetParent(_panel.transform, false);
        root.localPosition = pos;

        var size = new Vector2(0.36f, 0.36f);
        SpriteRenderer exit = MakeVanilla("Exit", ExitSpriteName, Vector3.zero, size, root);
        Sprite exitHover = VanillaSprites.TryGetValue(ExitHoverSpriteName, out Sprite eh) ? eh : null;
        if (exit && exitHover)
        {
            Sprite exitIdle = exit.sprite;
            MakeClickable(root.gameObject, size, Close, () => exit.sprite = exitHover, () => exit.sprite = exitIdle);
            return;
        }

        if (exit) Object.Destroy(exit.gameObject);
        SpriteRenderer bg = MakeRound("Hover", new Color32(0, 0, 0, 0), new Vector3(0f, 0f, 0.05f), size, PartRadius, root);
        var bars = new SpriteRenderer[2];
        for (var i = 0; i < 2; i++)
        {
            var bar = new GameObject("Bar") { layer = 5 };
            bar.transform.SetParent(root, false);
            bar.transform.localScale = new Vector3(0.22f, 0.025f, 1f);
            bar.transform.localRotation = Quaternion.Euler(0f, 0f, i == 0 ? 45f : -45f);
            bars[i] = bar.AddComponent<SpriteRenderer>();
            bars[i].sprite = Solid();
            bars[i].color = MutedColor;
        }

        MakeClickable(root.gameObject, size, Close,
            () => { bg.color = GhostHover; bars[0].color = bars[1].color = TextColor; },
            () => { bg.color = new Color32(0, 0, 0, 0); bars[0].color = bars[1].color = MutedColor; });
    }

    // 箱の高さを文の量に合わせる (最低 BoxMinHeight)。上端は固定して下へ伸ばし、窓と下の段も同じだけ伸ばす
    private static void LayoutBox(float textHeight)
    {
        if (!_box) return;
        float h = Math.Max(BoxMinHeight, textHeight + 0.3f);
        float w = CardSize.x - Pad * 2f;
        float cy = BoxTop - h / 2f;

        _box.transform.localPosition = new Vector3(0f, cy, 0.3f);
        _box.size = new Vector2(w, h);
        if (_boxInner)
        {
            _boxInner.transform.localPosition = new Vector3(0f, cy, 0.2f);
            _boxInner.size = new Vector2(w - BoxBorder * 2f, h - BoxBorder * 2f);
        }

        if (_counter) _counter.transform.localPosition = new Vector3(w / 2f - 0.14f, BoxTop - h + 0.08f, -0.5f);

        float extra = h - BoxMinHeight;
        if (_footer) _footer.localPosition = new Vector3(0f, FooterTop - extra, 0f);

        var cardSize = new Vector2(CardSize.x, CardSize.y + extra);
        _card.transform.localPosition = new Vector3(0f, -extra / 2f, 0.6f);
        if (_cardBorder)
        {
            _card.size = cardSize - Vector2.one * 0.2f;
            _cardBorder.transform.localPosition = new Vector3(0f, -extra / 2f, 0.7f);
            _cardBorder.size = cardSize;
        }
        else _card.size = cardSize;

        BoxCollider2D cardCol = _card.GetComponent<BoxCollider2D>();
        if (cardCol) cardCol.size = cardSize;
    }

    private static TextMeshPro MakeText(TextMeshPro template, Transform parent, string name, Vector3 pos, float size, string text, FontStyles style, Color32 color, float width)
    {
        TextMeshPro t = Object.Instantiate(template, parent);
        t.gameObject.SetActive(false);
        t.name = name;
        t.gameObject.layer = 5;
        t.DestroyTranslator();
        AspectPosition ap = t.GetComponent<AspectPosition>();
        if (ap) Object.Destroy(ap);

        t.alignment = TextAlignmentOptions.TopLeft;
        t.fontStyle = style;
        t.fontSize = t.fontSizeMax = t.fontSizeMin = size;
        t.enableAutoSizing = false;
        t.color = color;
        t.enableWordWrapping = true;
        t.overflowMode = TextOverflowModes.Overflow;
        t.transform.localScale = Vector3.one;
        t.transform.localPosition = pos;

        RectTransform rt = t.GetComponent<RectTransform>();
        if (rt)
        {
            rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = new Vector2(width, 1f);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        }

        t.text = text;
        t.gameObject.SetActive(true);
        return t;
    }

    private static Sprite Solid()
    {
        if (_solid) return _solid;
        var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
        tex.SetPixel(0, 0, Color.white);
        tex.Apply(true, true);
        _solid = Sprite.Create(tex, new Rect(0f, 0f, 1f, 1f), new Vector2(0.5f, 0.5f), 1f, 0, SpriteMeshType.FullRect);
        _solid.hideFlags |= HideFlags.HideAndDontSave;
        return _solid;
    }

    private const int RoundTexSize = 64;
    private const int RoundTexRadius = 30;

    // 白い角丸 1 枚 (縁は 1px ぼかし) を、半径ごとに ppu だけ変えた 9 分割スプライトとして使い回す
    private static Sprite RoundSprite(float radius)
    {
        int key = Mathf.RoundToInt(radius * 1000f);
        if (RoundSprites.TryGetValue(key, out Sprite s) && s) return s;

        if (!_roundTex)
        {
            _roundTex = BuildTexture(RoundTexSize, (x, y) =>
            {
                float d = RoundedRectDistance(x, y, RoundTexSize, RoundTexRadius);
                return Math.Clamp(0.5f - d, 0f, 1f);
            });
        }

        float border = RoundTexRadius + 1;
        s = Sprite.Create(_roundTex, new Rect(0f, 0f, RoundTexSize, RoundTexSize), new Vector2(0.5f, 0.5f), RoundTexRadius / radius, 0, SpriteMeshType.FullRect, new Vector4(border, border, border, border));
        s.hideFlags |= HideFlags.HideAndDontSave;
        RoundSprites[key] = s;
        return s;
    }

    // 四角の内側で負・外側で正の距離 (px)。inset だけ縁から内側へ寄せた角丸の四角に対して測る
    private static float RoundedRectDistance(int x, int y, int size, float radius, float inset = 0f)
    {
        float half = size / 2f - inset;
        float px = Math.Abs(x + 0.5f - size / 2f) - (half - radius);
        float py = Math.Abs(y + 0.5f - size / 2f) - (half - radius);
        float ox = Math.Max(px, 0f), oy = Math.Max(py, 0f);
        return (float)Math.Sqrt(ox * ox + oy * oy) + Math.Min(Math.Max(px, py), 0f) - radius;
    }

    private static Texture2D BuildTexture(int size, Func<int, int, float> alpha)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        var px = new Color32[size * size];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
            px[y * size + x] = new Color32(255, 255, 255, (byte)Math.Round(alpha(x, y) * 255f));
        tex.SetPixels32(px);
        tex.Apply(false, true);
        tex.hideFlags |= HideFlags.HideAndDontSave;
        return tex;
    }

    private static void RefreshLogToggle()
    {
        if (_checkMark && _checkMark.activeSelf != _attachLog) _checkMark.SetActive(_attachLog);
    }

    // ✓ を細い棒 2 本で描く (短い棒が左下へ、長い棒が右上へ)
    private static GameObject MakeCheckMark(Transform parent)
    {
        var root = new GameObject("Mark") { layer = 5 };
        root.transform.SetParent(parent, false);
        root.transform.localPosition = new Vector3(0f, 0f, -0.04f);
        (Vector2 a, Vector2 b)[] strokes = [(new(-0.068f, 0.004f), new(-0.022f, -0.045f)), (new(-0.022f, -0.045f), new(0.07f, 0.055f))];
        foreach ((Vector2 a, Vector2 b) in strokes)
        {
            var bar = new GameObject("Stroke") { layer = 5 };
            bar.transform.SetParent(root.transform, false);
            Vector2 d = b - a;
            const float thick = 0.026f;
            bar.transform.localPosition = new Vector3((a.x + b.x) / 2f, (a.y + b.y) / 2f, 0f);
            bar.transform.localRotation = Quaternion.Euler(0f, 0f, (float)(Math.Atan2(d.y, d.x) * 180.0 / Math.PI));
            bar.transform.localScale = new Vector3(d.magnitude + thick, thick, 1f);
            var sr = bar.AddComponent<SpriteRenderer>();
            sr.sprite = Solid();
            sr.color = CheckMarkColor;
        }

        return root;
    }

    private static void SetStatus(string text)
    {
        if (_status) _status.text = text;
    }

    // 欄の左上を窓の中の基準点へ合わせる。文が折り返すと背景を下へ伸ばす
    private static void PlaceField()
    {
        if (!_field || !_fieldAnchor) return;
        Transform t = _field.transform;
        SpriteRenderer bg = _field.Background;
        Vector3 anchor = _fieldAnchor.position;
        if (!bg)
        {
            t.position = anchor;
            return;
        }

        TextMeshPro outText = _field.textArea ? _field.textArea.outputText : null;

        // 字を縮めた分だけ折り返しの幅を広げ、箱の内側 (左右 0.2 ずつ空ける) で折り返させる
        if (outText)
        {
            RectTransform ort = outText.rectTransform;
            float sx = outText.transform.lossyScale.x;
            float wrap = (CardSize.x - Pad * 2f - 0.4f) * _panel.transform.lossyScale.x;
            if (ort && sx > 0f && Math.Abs(ort.sizeDelta.x * sx - wrap) > 1e-3f) ort.sizeDelta = new Vector2(wrap / sx, ort.sizeDelta.y);
        }

        float bgScaleY = bg.transform.lossyScale.y;
        if (outText && bgScaleY > 0f)
        {
            Vector2 size = bg.size;
            float h = Math.Max(0.62f, outText.bounds.size.y * outText.transform.lossyScale.y / bgScaleY + 0.2f);
            if (Math.Abs(size.y - h) > 1e-3f) bg.size = new Vector2(size.x, h);
        }

        Bounds b = bg.bounds;
        Vector3 want = new(t.position.x + (anchor.x - b.min.x), t.position.y + (anchor.y - b.max.y), anchor.z);
        if ((t.position - want).sqrMagnitude > 1e-6f) t.position = want;

        float panelScaleY = _panel.transform.lossyScale.y;
        LayoutBox(panelScaleY > 0f ? b.size.y / panelScaleY : BoxMinHeight);

        if (_placeholder)
        {
            bool empty = MirrorText.Length == 0 && MirrorCompo.Length == 0;
            if (_placeholder.gameObject.activeSelf != empty) _placeholder.gameObject.SetActive(empty);
        }

        if (_counter)
        {
            int n = MirrorText.Length + MirrorCompo.Length;
            string c = n >= TextMax ? $"<color=#E08578>{n} / {TextMax}</color>" : $"{n} / {TextMax}";
            if (c != _counterText)
            {
                _counterText = c;
                _counter.text = c;
            }
        }
    }

    private static void Pump()
    {
        if (!_panel || !_field || !_field.gameObject.activeInHierarchy)
        {
            Close();
            return;
        }

        // 置き直しは中身が変わった時と、ときどきだけ
        if (LayoutDirty || Time.frameCount % 15 == 0)
        {
            LayoutDirty = false;
            PlaceField();
        }

        // クローン元のチャット欄は白地に黒文字なので、窓に合わせて暗い地に白文字へ塗り直す
        if (_field.background) _field.background.color = new Color32(24, 24, 28, 0);
        if (_field.textArea && _field.textArea.outputText) _field.textArea.outputText.color = TextColor;

        if (_field.textArea && !_field.textArea.hasFocus && Time.frameCount > _openedFrame + 1)
            _field.textArea.GiveFocus();

        if (Input.GetKeyDown(KeyCode.Escape))
        {
            Close();
            return;
        }

#if !ANDROID
        HandlePaste();
#endif
    }

#if !ANDROID
    // 欄の Update は TextBoxPatch が置き換えており、Ctrl+V はチャット欄専用の処理しか無いので自前で受ける
    private static void HandlePaste()
    {
        if (!(Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) || !Input.GetKeyDown(KeyCode.V)) return;

        TextBoxTMP area = _field.textArea;
        if (!area) return;

        string clip = (GUIUtility.systemCopyBuffer ?? "").Replace("\r", "").Replace("\n", " ").Trim();
        if (clip.Length == 0) return;

        string current = TextBoxPatch.SafeChatText(area);
        int at = Math.Clamp(area.caretPos, 0, current.Length);
        int room = Math.Max(0, area.characterLimit - current.Length);
        if (clip.Length > room) clip = clip[..room];
        if (clip.Length == 0) return;

        try { TextBoxPatch.SetChatFieldText(area, current.Insert(at, clip), at + clip.Length); }
        catch (Exception e) { Logger.Info($"paste: caret placement retried ({e.GetType().Name})", "BugReport"); }
    }
#endif

    // ==== 送信 ====

    private static void Send()
    {
        if (_busy || !_field || !_field.textArea) return;

        string text = TextBoxPatch.SafeChatText(_field.textArea).Trim();
        if (text.Length == 0)
        {
            SetStatus(GetString("BugReport.Empty"));
            return;
        }

        PlayerControl me = PlayerControl.LocalPlayer;
        if (me && LobbyShare.IsReportOnCooldown(me.PlayerId))
        {
            SetStatus(GetString("BugReport.Cooldown"));
            return;
        }

        _busy = true;
        _busySince = Time.realtimeSinceStartup;
        _pendingText = text;

        if (!_attachLog)
        {
            Submit(null);
            return;
        }

        SetStatus(GetString("BugReport.Packing"));

        // 直前の数分 (= 報告したい瞬間) がまだ log.html の書き込み待ちに居るので先に吐き出す
        try { Logger.FlushNow(); }
        catch { /* 取れた分だけ送る */ }

        try { HealthLog.FlushPending(); }
        catch { /* 取れた分だけ送る */ }

        string context = BuildContext();
        string logOutput = Path.Combine(Paths.BepInExRootPath, "LogOutput.log");
        string html = CustomLogger.LOGFilePath;
        string health = HealthLog.FilePath;

        Task.Run(() =>
        {
            byte[] zip = null;
            try { zip = BuildZip(context, logOutput, html, health); }
            catch { /* ログ無しで送る */ }

            _zip = zip;
            _zipReady = true;
        });
    }

    private static void Submit(byte[] zip)
    {
        PlayerControl me = PlayerControl.LocalPlayer;
        if (!me)
        {
            _busy = false;
            SetStatus(string.Format(GetString("BugReport.Failed"), -1));
            return;
        }

        SetStatus(GetString("BugReport.Sending"));

        LobbyShare.ReportResult r = LobbyShare.SubmitReport(me, _pendingText, zip, (code, _) =>
        {
            _resultCode = code;
            _resultReady = true;
        }, bugReport: true);

        Logger.Info($"submit: result={r} log={(zip == null ? 0 : zip.Length)}B", "BugReport");

        if (r == LobbyShare.ReportResult.OnCooldown)
        {
            _busy = false;
            SetStatus(GetString("BugReport.Cooldown"));
        }
        else if (r == LobbyShare.ReportResult.Empty)
        {
            _busy = false;
            SetStatus(GetString("BugReport.Empty"));
        }
    }

    private static void DrainResults()
    {
        if (_zipReady)
        {
            _zipReady = false;
            byte[] zip = _zip;
            _zip = null;
            if (zip is { Length: > ZipMaxBytes }) zip = null;
            Submit(zip);
        }

        if (!_resultReady) return;
        _resultReady = false;
        _busy = false;

        int code = _resultCode;
        Logger.Info($"relay answered {code}", "BugReport");

        if (code is >= 200 and < 300)
        {
            SetStatus(GetString("BugReport.Sent"));
            if (_field && _field.textArea)
            {
                try { TextBoxPatch.SetChatFieldText(_field.textArea, string.Empty); }
                catch { /* 文はそのまま残る */ }
            }
        }
        else if (code == 0) SetStatus(GetString("BugReport.SavedLocally"));
        else if (code == 429) SetStatus(GetString("BugReport.Cooldown"));
        else SetStatus(string.Format(GetString("BugReport.Failed"), code));
    }

    // ==== ログの zip ====

    private static string BuildContext()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"time={DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"mod={Main.PluginVersion} game={Application.version} os={SystemInfo.operatingSystem}");

        try
        {
            sb.AppendLine($"host={(AmongUsClient.Instance && AmongUsClient.Instance.AmHost ? 1 : 0)} phase={(GameStates.IsLobby ? "lobby" : GameStates.IsMeeting ? "meeting" : GameStates.IsInGame ? "in-game" : "other")} mode={Options.CurrentGameMode}");
            sb.AppendLine($"map={Main.NormalOptions?.MapId} players={Main.AllPlayerControlsToList.Count}");
            if (GameStates.IsInGame && PlayerControl.LocalPlayer) sb.AppendLine($"role={PlayerControl.LocalPlayer.GetCustomRole()}");
        }
        catch (Exception e) { sb.AppendLine($"context error: {e.Message}"); }

        return sb.ToString();
    }

    private static byte[] BuildZip(string context, string logOutput, string html, string health)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            AddEntry(zip, "context.txt", context);
            AddTail(zip, "LogOutput.log", logOutput, LogOutputTailBytes);
            AddTail(zip, "log.html", html, HtmlTailBytes);
            AddTail(zip, "Health.log", health, HealthTailBytes);
        }

        return ms.ToArray();
    }

    private static void AddTail(ZipArchive zip, string name, string path, long maxBytes)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

        try
        {
            // ログは書き込み中なので共有読みで開き、末尾だけ取る
            using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long start = Math.Max(0, s.Length - maxBytes);
            s.Seek(start, SeekOrigin.Begin);
            var buf = new byte[s.Length - start];
            int read = 0;
            while (read < buf.Length)
            {
                int n = s.Read(buf, read, buf.Length - read);
                if (n <= 0) break;
                read += n;
            }

            AddEntry(zip, name, Encoding.UTF8.GetString(buf, 0, read));
        }
        catch { /* 読めなかったファイルは入れない */ }
    }

    private static void AddEntry(ZipArchive zip, string name, string text)
    {
        ZipArchiveEntry entry = zip.CreateEntry(name, System.IO.Compression.CompressionLevel.Optimal);
        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        w.Write(Redact(text));
    }

    // フレンドコード (英字#数字4桁) と、パスに出る Windows のユーザーフォルダ名を伏せる
    private static readonly Regex FriendCodeRe = new(@"(?<![A-Za-z0-9])[A-Za-z]{2,}#\d{4}(?!\d)", RegexOptions.Compiled);
    private static readonly Regex UserDirRe = new(@"(?i)([A-Z]:[\\/]+Users[\\/]+)[^\\/\r\n""<>|:*?]+", RegexOptions.Compiled);

    internal static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        text = FriendCodeRe.Replace(text, "xxxx#0000");
        return UserDirRe.Replace(text, "$1<user>");
    }
}

[HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
internal static class BugReportTickPatch
{
    public static void Postfix(HudManager __instance) => BugReport.Tick(__instance);
}

// 入力中は WASD やキル (Q) などが本編の操作に流れないよう、キーボードの読み取りごと止める
[HarmonyPatch(typeof(KeyboardJoystick), nameof(KeyboardJoystick.Update))]
internal static class BugReportBlockKeyboardPatch
{
    public static bool Prefix(KeyboardJoystick __instance)
    {
        if (!BugReport.IsOpen) return true;
        __instance.del = Vector2.zero;
        return false;
    }
}

// バニラの TextBoxTMP.SetText は記号や一部の文字を捨てるので、この欄だけ改行を落として文字数で切るだけにする
[HarmonyPatch(typeof(TextBoxTMP), nameof(TextBoxTMP.SetText))]
internal static class BugReportFieldSetTextPatch
{
    public static bool Prefix(TextBoxTMP __instance, [HarmonyArgument(0)] string input, [HarmonyArgument(1)] string inputCompo)
    {
        FreeChatInputField field = BugReport.ActiveField;
        if (!field || __instance != field.textArea) return true;

        input ??= "";
        inputCompo ??= "";

        string before;
        try { before = __instance.text ?? ""; }
        catch { before = ""; }

        var sb = new StringBuilder(input.Length);
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
        __instance.keyboard?.text = text;
        __instance.OnChange.Invoke();

        BugReport.MirrorText = text;
        BugReport.MirrorCompo = inputCompo;
        BugReport.LayoutDirty = true;

        try { __instance.SetPipePosition(); }
        catch { /* キャレットの表示位置だけの問題。次の入力で置き直される */ }

        return false;
    }
}

// 有効化した次のフレームにゲーム側の初期化が TextBoxTMP.Clear を呼んで中身を消すので、開いている間は止める
[HarmonyPatch(typeof(TextBoxTMP), nameof(TextBoxTMP.Clear))]
internal static class BugReportFieldKeepTextPatch
{
    public static bool Prefix(TextBoxTMP __instance)
    {
        FreeChatInputField field = BugReport.ActiveField;
        return !(BugReport.IsOpen && field && __instance == field.textArea);
    }
}
