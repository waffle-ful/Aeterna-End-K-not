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
    private const float BoxTop = 0.77f;
    private const float FooterHeight = 0.9f;
    private const float FooterTop = -2.075f + FooterHeight; // 窓の下端から帯の高さだけ上
    private const float FooterRow = -FooterHeight / 2f;    // FooterTop から見た帯の中心

    // 角の丸みは控えめに。窓だけ少し大きく、中の部品はそろえて小さく
    private const float CardRadius = 0.08f;
    private const float PartRadius = 0.05f;
    private const float CheckSize = 0.24f;

    private static readonly Vector2 CardSize = new(6.0f, 4.15f);
    private static readonly Vector3 FieldWorldScale = new(1.05f, 0.59f, 1f);
    // 欄の倍率は横 1.05・縦 0.59 なので、字の縦横を揃えるには縦を 1.05/0.59 倍する。0.8 で本文と同じくらいの字の大きさ
    private const float FieldTextSize = 0.8f;
    private static readonly Vector3 FieldTextScale = new(FieldTextSize, FieldTextSize * 1.05f / 0.59f, 1f);

    // 色は End K not の意匠 (夜の湯宿) から。地は青みの墨、差し色は行灯の橙、罫は檜
    private static readonly Color32 BackdropColor = new(15, 20, 23, 165);     // 墨
    private static readonly Color32 CardColor = new(21, 28, 33, 255);         // 宵
    private static readonly Color32 CardBorderColor = new(201, 168, 124, 46); // 檜
    private static readonly Color32 DividerColor = new(201, 168, 124, 30);    // 檜
    private static readonly Color32 BoxColor = new(15, 20, 23, 255);          // 墨
    private static readonly Color32 AccentColor = new(233, 166, 72, 255);     // 行灯
    private static readonly Color32 AccentHover = new(242, 186, 104, 255);
    private static readonly Color32 BoxBorderColor = new(52, 66, 73, 255);
    private static readonly Color32 FooterColor = new(17, 23, 27, 255);
    private static readonly Color32 CheckOffBorder = new(74, 90, 96, 255);
    private static readonly Color32 AccentGlow = new(233, 166, 72, 22);
    private static readonly Color32 OnAccentColor = new(15, 20, 23, 255);     // 行灯の上の字は墨
    private static readonly Color32 TextColor = new(232, 239, 240, 255);      // 湯気
    private static readonly Color32 MutedColor = new(143, 163, 168, 255);     // 霞
    private static readonly Color32 FaintColor = new(104, 121, 127, 255);
    private static readonly Color32 HinokiColor = new(201, 168, 124, 255);    // 檜
    private static readonly Color32 GhostColor = new(30, 39, 44, 255);        // 岩
    private static readonly Color32 GhostHover = new(42, 53, 59, 255);
    private static readonly Color32 ToggleOff = new(42, 53, 59, 255);

    private static GameObject _hudButton;
    private static float _spacing = ButtonSpacingFallback;

    private static GameObject _panel;
    private static SpriteRenderer _card;
    private static SpriteRenderer _cardBorder;
    private static SpriteRenderer _shadow;
    private static Transform _footer;
    private static SpriteRenderer _box;
    private static SpriteRenderer _boxInner;
    private static TextMeshPro _placeholder;
    private static TextMeshPro _counter;
    private static TextMeshPro _eyebrow;
    private static Transform _fieldAnchor;
    private static TextMeshPro _status;
    private static SpriteRenderer _checkBorder;
    private static SpriteRenderer _checkFill;
    private static GameObject _checkMark;
    private static SpriteRenderer _glow;
    private static FreeChatInputField _field;
    private static Sprite _solid;
    private static Texture2D _roundTex;
    private static Texture2D _shadowTex;
    private static readonly System.Collections.Generic.Dictionary<int, Sprite> RoundSprites = [];
    private static Sprite _shadowSprite;
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
        get => _open && _panel;
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
            bool show = me && hud.SettingsButton && hud.SettingsButton.activeInHierarchy && (IsOpen || !CleanShot.On) && (GameStates.IsLobby || !GameStates.IsEnded); // 終了の印は次の試合開始まで残る

            EnsureHudButton(hud);
            if (_hudButton)
            {
                if (_hudButton.activeSelf != show) _hudButton.SetActive(show);
                if (show && (Time.frameCount & 7) == 0) PlaceHudButton(hud);
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
        if (_hudButton || !hud.SettingsButton) return;

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

        if (hud.MapButton && hud.MapButton.gameObject.activeInHierarchy)
        {
            float gap = Math.Abs(hud.MapButton.transform.position.x - s.x);
            if (gap is > 0.4f and < 1.5f) _spacing = gap;
            left = Math.Min(left, hud.MapButton.transform.position.x);
        }

        if (hud.MatchInfoButton && hud.MatchInfoButton.gameObject.activeInHierarchy)
            left = Math.Min(left, hud.MatchInfoButton.transform.position.x);

        ChatController chat = hud.Chat;
        if (chat && chat.chatButton && chat.chatButton.gameObject.activeInHierarchy)
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

        string where = GetString(GameStates.IsLobby ? "BugReport.WhereLobby" : GameStates.IsMeeting ? "BugReport.WhereMeeting" : "BugReport.WhereGame");
        if (_eyebrow) _eyebrow.text = $"END K NOT {Main.PluginVersion}  ·  {where}";

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
        // 右のロビー設定の板は幕より手前に描かれるので、窓はそれに掛からないよう少し左へ寄せる
        _panel.transform.localPosition = new Vector3(-0.12f, -0.05f, -30f); // これよりカメラ側へ寄せると、手前の部品がカメラの手前側の面で切れる

        // 画面全体を暗くする幕。後ろの本編のボタンや床へのクリックをここで受け止め、押されたら閉じる
        SpriteRenderer backdrop = MakeSolid("Backdrop", BackdropColor, new Vector3(0f, 0f, 0.9f), new Vector2(40f, 25f));
        MakeClickable(backdrop.gameObject, new Vector2(40f, 25f), Close, null, null);

        _shadow = MakeShadow(new Vector3(0f, -0.08f, 0.8f));
        _cardBorder = MakeRound("CardBorder", CardBorderColor, new Vector3(0f, 0f, 0.7f), CardSize + Vector2.one * 0.025f, CardRadius + 0.01f);
        _card = MakeRound("Card", CardColor, new Vector3(0f, 0f, 0.6f), CardSize, CardRadius);
        // 窓の上のクリックは幕まで抜けないよう、窓の地でも受け止める
        MakeClickable(_card.gameObject, Vector2.one, null, null, null);

        float left = -CardSize.x / 2f + Pad;
        float right = CardSize.x / 2f - Pad;
        float top = CardSize.y / 2f - 0.34f;
        float width = CardSize.x - Pad * 2f;

        // 窓の左上から滲む行灯の灯。窓の縁より内側で消えるので、角からはみ出さない
        _glow = MakeShadowLike("Glow", AccentGlow, new Vector3(left + 1.3f, top - 0.55f, 0.55f), new Vector2(3.0f, 1.3f));

        // 報告に一緒に付く「版・どこで」を見出しの上に出す
        _eyebrow = MakeText(template, _panel.transform, "Eyebrow", new Vector3(left, top, 0f), 0.85f, string.Empty, FontStyles.Normal, HinokiColor, width - 0.6f);
        _eyebrow.characterSpacing = 6f;
        TextMeshPro title = MakeText(template, _panel.transform, "Title", new Vector3(left, top - 0.2f, 0f), 2.0f, GetString("BugReport.Title"), FontStyles.Bold, TextColor, width - 0.6f);
        title.characterSpacing = 2f;
        MakeText(template, _panel.transform, "Hint", new Vector3(left, top - 0.64f, 0f), 1.1f, GetString("BugReport.Hint"), FontStyles.Normal, MutedColor, width);

        MakeCloseButton(new Vector3(right - 0.08f, top - 0.34f, -0.2f));

        // 入力の箱は細い中間色の線だけ。差し色は送るボタンとチェックにだけ使う
        _box = MakeRound("Box", BoxBorderColor, new Vector3(0f, 0f, 0.3f), Vector2.one, PartRadius);
        _boxInner = MakeRound("BoxInner", BoxColor, new Vector3(0f, 0f, 0.2f), Vector2.one, PartRadius - BoxBorder);

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

        // 帯は下の角だけ丸い。角丸の板の上半分を四角い板で覆って上の角を消す
        SpriteRenderer band = MakeRound("Band", FooterColor, new Vector3(0f, FooterRow, 0.5f), new Vector2(CardSize.x, FooterHeight), CardRadius, _footer);
        SpriteRenderer bandTop = MakeSolid("BandTop", FooterColor, new Vector3(0f, -CardRadius, 0.5f), new Vector2(CardSize.x, CardRadius * 2f));
        bandTop.transform.SetParent(_footer, false);
        SpriteRenderer divider = MakeSolid("Divider", DividerColor, new Vector3(0f, 0f, 0.45f), new Vector2(CardSize.x, 0.01f));
        divider.transform.SetParent(_footer, false);

        _status = MakeText(template, _footer, "Status", new Vector3(left, 0.4f, 0f), 1.0f, string.Empty, FontStyles.Normal, MutedColor, width);

        // ログを添付するかのチェック。左端は見出し・入力の箱と同じ線にそろえ、文字の部分を押しても切り替わる
        var check = new GameObject("LogCheck") { layer = 5 }.transform;
        check.SetParent(_footer, false);
        check.localPosition = new Vector3(left + CheckSize / 2f, FooterRow + 0.1f, -0.2f);
        _checkBorder = MakeRound("Border", CheckOffBorder, Vector3.zero, Vector2.one * CheckSize, 0.04f, check);
        _checkFill = MakeRound("Fill", BoxColor, new Vector3(0f, 0f, -0.02f), Vector2.one * (CheckSize - 0.03f), 0.03f, check);
        _checkMark = MakeCheckMark(check);
        var hit = new GameObject("Hit") { layer = 5 };
        hit.transform.SetParent(check, false);
        hit.transform.localPosition = new Vector3(1.1f, -0.1f, 0f);
        MakeClickable(hit, new Vector2(2.6f, 0.6f), () =>
        {
            _attachLog = !_attachLog;
            RefreshLogToggle();
        }, null, null);

        // 文字の段: 見出し (チェックと同じ高さ) とその下の注記
        float textLeft = left + CheckSize + 0.16f;
        MakeText(template, _footer, "LogLabel", new Vector3(textLeft, FooterRow + 0.22f, 0f), 1.1f, GetString("BugReport.AttachLog"), FontStyles.Normal, TextColor, 2.5f);
        MakeText(template, _footer, "LogNote", new Vector3(textLeft, FooterRow - 0.02f, 0f), 0.82f, GetString("BugReport.LogNote"), FontStyles.Normal, FaintColor, 2.5f);

        var sendSize = new Vector2(1.3f, 0.4f);
        var cancelSize = new Vector2(0.9f, 0.4f);
        MakeButton("Send", new Vector3(right - sendSize.x / 2f, FooterRow, -0.2f), sendSize, AccentColor, AccentHover, null, GetString("BugReport.Send"), OnAccentColor, FontStyles.Bold, Send);
        MakeButton("Cancel", new Vector3(right - sendSize.x - 0.12f - cancelSize.x / 2f, FooterRow, -0.2f), cancelSize, GhostColor, GhostHover, null, GetString("BugReport.Close"), TextColor, FontStyles.Normal, Close);

        // ロビーの表示物や本編のボタンは描画順が高いものがあり、z だけでは窓の上に出てくる。
        // 地の部品は決まった順に、それ以外 (文字・入力欄・ボタン) はその上の段へまとめて持ち上げる
        // il2cpp の物は包みが毎回別なので、同じ物かは InstanceID で見る
        SpriteRenderer[] layered = [backdrop, _shadow, _cardBorder, _card, _glow, band, bandTop, divider, _box, _boxInner];
        var layerOf = new System.Collections.Generic.Dictionary<int, int>();
        for (var i = 0; i < layered.Length; i++) layerOf[layered[i].GetInstanceID()] = i;
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
        int key = src.GetInstanceID();
        if (OverlayMaterials.TryGetValue(key, out Material m) && m) return m;
        m = new Material(src) { renderQueue = 4000, hideFlags = HideFlags.HideAndDontSave };

        // 型紙の文字 (キルの残り秒) は太い縁取りと影付き。窓の文字は縁取り無しの平らな字にする
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
            t.fontSharedMaterial = OverlayCopy(t.fontSharedMaterial, true);

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

    private static SpriteRenderer MakeShadow(Vector3 pos)
    {
        var go = new GameObject("Shadow") { layer = 5 };
        go.transform.SetParent(_panel.transform, false);
        go.transform.localPosition = pos;
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = ShadowSprite();
        sr.drawMode = SpriteDrawMode.Sliced;
        sr.size = CardSize + Vector2.one * 0.7f;
        sr.color = new Color(0f, 0f, 0f, 0.55f);
        return sr;
    }

    private static SpriteRenderer MakeShadowLike(string name, Color32 color, Vector3 pos, Vector2 size)
    {
        SpriteRenderer sr = MakeShadow(pos);
        sr.name = name;
        sr.size = size;
        sr.color = color;
        return sr;
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

    private static void MakeButton(string name, Vector3 pos, Vector2 size, Color32 fill, Color32 hover, Color32? border, string label, Color32 labelColor, FontStyles style, Action onClick)
    {
        var root = new GameObject(name) { layer = 5 }.transform;
        root.SetParent(_footer, false);
        root.localPosition = pos;

        if (border is { } b) MakeRound("Border", b, new Vector3(0f, 0f, 0.02f), size, PartRadius, root);
        SpriteRenderer bg = MakeRound("Fill", fill, Vector3.zero, border.HasValue ? size - Vector2.one * 0.02f : size, PartRadius, root);

        TextMeshPro text = MakeText(_textTemplate, root, "Label", new Vector3(0f, 0f, -0.05f), 1.05f, label, style, labelColor, size.x);
        text.alignment = TextAlignmentOptions.Center;
        RectTransform rt = text.GetComponent<RectTransform>();
        if (rt)
        {
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(size.x, size.y);
        }

        MakeClickable(root.gameObject, size, onClick, () => bg.color = hover, () => bg.color = fill);
    }

    // 右上の「×」。字形はフォントに頼らず細い棒 2 本で描く
    private static void MakeCloseButton(Vector3 pos)
    {
        var root = new GameObject("Close") { layer = 5 }.transform;
        root.SetParent(_panel.transform, false);
        root.localPosition = pos;

        SpriteRenderer bg = MakeRound("Hover", new Color32(0, 0, 0, 0), new Vector3(0f, 0f, 0.05f), new Vector2(0.42f, 0.42f), PartRadius, root);
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

        MakeClickable(root.gameObject, new Vector2(0.42f, 0.42f), Close,
            () => { bg.color = GhostHover; bars[0].color = bars[1].color = TextColor; },
            () => { bg.color = new Color32(0, 0, 0, 0); bars[0].color = bars[1].color = MutedColor; });
    }

    // 箱の高さを文の量に合わせる (最低 BoxMinHeight)。上端は固定して下へ伸ばし、窓と下の段も同じだけ伸ばす
    private static void LayoutBox(float textHeight)
    {
        if (!_box || !_boxInner) return;
        float h = Math.Max(BoxMinHeight, textHeight + 0.3f);
        float w = CardSize.x - Pad * 2f;
        float cy = BoxTop - h / 2f;

        _box.transform.localPosition = new Vector3(0f, cy, 0.3f);
        _box.size = new Vector2(w, h);
        _boxInner.transform.localPosition = new Vector3(0f, cy, 0.2f);
        _boxInner.size = new Vector2(w - BoxBorder * 2f, h - BoxBorder * 2f);

        if (_counter) _counter.transform.localPosition = new Vector3(w / 2f - 0.14f, BoxTop - h + 0.08f, -0.5f);

        float extra = h - BoxMinHeight;
        if (_footer) _footer.localPosition = new Vector3(0f, FooterTop - extra, 0f);

        var cardSize = new Vector2(CardSize.x, CardSize.y + extra);
        _card.transform.localPosition = new Vector3(0f, -extra / 2f, 0.6f);
        _card.size = cardSize;
        _cardBorder.transform.localPosition = new Vector3(0f, -extra / 2f, 0.7f);
        _cardBorder.size = cardSize + Vector2.one * 0.03f;
        _shadow.transform.localPosition = new Vector3(0f, -extra / 2f - 0.08f, 0.8f);
        _shadow.size = cardSize + Vector2.one * 0.7f;
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

    // 窓の落ち影。角丸の外側へなだらかに薄れる
    private static Sprite ShadowSprite()
    {
        if (_shadowSprite) return _shadowSprite;

        const int size = 96;
        const float blur = 30f;
        _shadowTex = BuildTexture(size, (x, y) =>
        {
            float d = RoundedRectDistance(x, y, size, 18f, blur + 2f);
            float t = Math.Clamp(1f - (d + blur) / (blur * 2f), 0f, 1f);
            return t * t * (3f - 2f * t);
        });

        const float b = 47f;
        _shadowSprite = Sprite.Create(_shadowTex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), (blur * 2f) / 0.7f, 0, SpriteMeshType.FullRect, new Vector4(b, b, b, b));
        _shadowSprite.hideFlags |= HideFlags.HideAndDontSave;
        return _shadowSprite;
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
        if (_checkBorder) _checkBorder.color = _attachLog ? AccentColor : CheckOffBorder;
        if (_checkFill) _checkFill.color = _attachLog ? AccentColor : BoxColor;
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
            sr.color = OnAccentColor;
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
