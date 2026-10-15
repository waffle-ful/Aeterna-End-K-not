using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace EndKnot.Modules.YouTubeChat;

// YouTube ライブチャットの「ビュー」層 (IMGUI)。過去のコメントを並べるログの板。
// 最初から開いていて、右上の × で閉じる。閉じている間は同じ場所に「開く」ボタンを出す (観客席の札のクリックと Ctrl+Y でも開閉できる)。観客席の右隣に下端をそろえて貼り付き
// (観客席を動かすと一緒に動き、大きさも観客席に合わせる)、観客席を出していない時は画面の左端に出す。
// 開閉状態は永続 GameObject 上のフィールドなので会議跨ぎ・ラウンド跨ぎで維持される。
// 100% host 画面ローカル（RPC / NetObject 不使用）。
public class YouTubeChatBubble : MonoBehaviour
{
    public static YouTubeChatBubble Instance;

    // ---- 永続する状態 (シーンを跨いで保持) ----
    private bool _isOpen = true;

    public static bool IsOpen => Instance && Instance._isOpen;

    public static void Toggle()
    {
        if (Instance) Instance._isOpen = !Instance._isOpen;
    }

    // 今描いている板の範囲 (IMGUI 座標)。出ていなければ false
    public static bool TryGetPanelRect(out Rect rect)
    {
        rect = Instance ? Instance._panelRect : default;
        return rect.width > 0f;
    }

    // マウスが板の上にある (ホイールを板のスクロールに使い、カメラのズームへ渡さない)
    public static bool IsPointerOver()
    {
        if (!TryGetPanelRect(out Rect rect)) return false;
        Vector3 mp = Input.mousePosition;
        return rect.Contains(new Vector2(mp.x, Screen.height - mp.y));
    }

    // ---- パネル geometry (ComputePanelGeometry が毎フレーム確定させ、HandlePanelInput / DrawPanel で共有) ----
    private Rect _panelRect;
    private Rect _panelHitZone;      // _panelRect からスクロールバー列を除いた、クリックを吸う領域
    private Rect _closeRect;         // 板の右上の角に載せる × のつまみ

    // ---- スクロール ----
    private Vector2 _scroll;
    private float _contentH;
    private bool _stickToBottom = true; // 最下部にいる間は新着に自動追従

    // ---- スタイル ----
    private GUIStyle _panelBg, _textStyle, _closeStyle, _openStyle;
    private Texture2D _openTex, _openHoverTex;
    private GUIContent _openContent;
    private readonly GUIContent _closeContent = new("×");
    private Texture2D _panelTex;
    private float _builtScale = -1f;

    private readonly List<string> _lines = new();
    private readonly StringBuilder _sb = new();

    private static float Scale => Il2Direct.ScreenWidth / 1080f * 0.5f * SeatsUserScale;

    // 観客席を出している時は観客席の大きさ設定に合わせる
    private static float SeatsUserScale => YouTubeChatOptions.SeatsEnabled?.GetBool() == true ? (YouTubeChatOptions.SeatsScale?.GetInt() ?? 100) / 100f : 1f;
    private static float ScrollbarColumnWidth => (OperatingSystem.IsAndroid() ? 42f : 22f) * Scale;

    private void Awake()
    {
        Instance = this;
        ImguiNoLayout.Apply(this);
    }

    private static bool ShouldShow()
    {
        if (!HudManager.InstanceExists) return false;
        if (!Il2Direct.Alive(AmongUsClient.Instance) || !AmongUsClient.Instance.AmHost) return false;
        if (YouTubeChatOptions.Enabled == null || !YouTubeChatOptions.Enabled.GetBool()) return false;
        if (YouTubeChatOptions.HideDuringMeetings != null && YouTubeChatOptions.HideDuringMeetings.GetBool() && GameStates.IsMeeting) return false;
        return true;
    }

    // ショートカット (Ctrl+Y)。チャット入力中は誤爆しないよう弾く。
    private void Update()
    {
        try
        {
            if (!ShouldShow()) return;
            YouTubeChatOverlay.EnsureSubscribed();

            bool typing = HudManager.Instance.Chat != null && HudManager.Instance.Chat.IsOpenOrOpening;
            if (typing) return;

            if ((Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) && Input.GetKeyDown(KeyCode.Y))
                _isOpen = !_isOpen;
        }
        catch (Exception e) { Utils.ThrowException(e); }
    }

    private void OnGUI()
    {
        try
        {
            if (!ShouldShow())
            {
                _panelRect = default;
                return;
            }

            EnsureStyles();
            ComputePanelGeometry();

            if (!_isOpen)
            {
                Rect panel = _panelRect;
                _panelRect = default;
                OpenButton(panel);
                return;
            }

            HandlePanelInput();
            DrawPanel();
        }
        catch (Exception e) { Utils.ThrowException(e); }
    }

    // × で閉じる。板の上でのクリックは gameplay へ漏れないよう吸う。スクロールバー列は ScrollView に任せる。
    private void HandlePanelInput()
    {
        Event e = Event.current;
        if (e.type == EventType.MouseDown && _closeRect.Contains(e.mousePosition))
        {
            _isOpen = false;
            _panelRect = default;
            e.Use();
            return;
        }

        if (e.type is EventType.MouseDown or EventType.MouseUp && (_panelHitZone.Contains(e.mousePosition) || _closeRect.Contains(e.mousePosition))) e.Use();
    }

    // 閉じている間、板の左下の角に「開く」ボタンを出す
    private void OpenButton(Rect panel)
    {
        Vector2 size = _openStyle.CalcSize(_openContent) + new Vector2(20f * Scale, 10f * Scale);
        var rect = new Rect(panel.x, panel.yMax - size.y, size.x, size.y);

        Event e = Event.current;
        if (e.type == EventType.MouseDown && rect.Contains(e.mousePosition))
        {
            _isOpen = true;
            _stickToBottom = true;
            e.Use();
            return;
        }

        if (e.type == EventType.MouseUp && rect.Contains(e.mousePosition)) e.Use();
        if (e.type == EventType.Repaint)
        {
            _openStyle.normal.background = rect.Contains(e.mousePosition) ? _openHoverTex : _openTex;
            GUI.Box(rect, _openContent, _openStyle);
        }
    }

    private const int BaseVisibleLines = 5; // PanelHeight=100% のときの可視行数
    private const float BasePanelWidth = 240f;

    private void ComputePanelGeometry()
    {
        float pad = 8f * Scale;
        float lineH = 20f * Scale;
        float widthPct = (YouTubeChatOptions.PanelWidth?.GetInt() ?? 100) / 100f;
        float heightPct = (YouTubeChatOptions.PanelHeight?.GetInt() ?? 100) / 100f;

        int visibleLines = Mathf.Max(1, Mathf.RoundToInt(BaseVisibleLines * heightPct));
        float ph = visibleLines * lineH + pad * 2f;

        float px, py, pw;
        if (AudienceSeats.TryGetHistoryAnchor(out float benchLeft, out float benchRight, out float bottom, out float width))
        {
            // 観客席の右隣で下端をそろえる。右に入らなければ左隣。幅は観客席と同じ (幅の設定は観客席を出していない時だけ効く)
            float gap = 10f * Scale;
            pw = width;
            px = benchRight + gap + pw <= Screen.width ? benchRight + gap : benchLeft - gap - pw;
            py = bottom - ph;
        }
        else
        {
            // 観客席が出ていない時は左端の中ほど
            pw = BasePanelWidth * Scale * widthPct;
            px = 14f * Scale;
            py = Screen.height * 0.34f;
        }

        px = Mathf.Clamp(px, 0f, Mathf.Max(0f, Screen.width - pw));
        py = Mathf.Clamp(py, 0f, Mathf.Max(0f, Screen.height - ph));

        _panelRect = new Rect(px, py, pw, ph);
        _panelHitZone = new Rect(px, py, Mathf.Max(0f, pw - ScrollbarColumnWidth), ph);

        // スクロールバーと重ならないよう、板の上辺の外に載せる
        float cs = 24f * Scale;
        _closeRect = new Rect(px + pw - cs, py - cs, cs, cs);
    }

    private void DrawPanel()
    {
        if (_panelRect.width <= 0f) return;

        float pad = 8f * Scale;
        GUI.Box(_panelRect, GUIContent.none, _panelBg);
        GUI.Box(_closeRect, _closeContent, _closeStyle);

        var outerRect = new Rect(_panelRect.x + pad, _panelRect.y + pad, _panelRect.width - pad * 2f, _panelRect.height - pad * 2f);
        float contentW = Mathf.Max(1f, outerRect.width - ScrollbarColumnWidth - 1f);

        string body = BuildBody();
        float measuredH = _textStyle.CalcHeight(new GUIContent(body), contentW);
        _contentH = measuredH;

        var innerRect = new Rect(0, 0, contentW, _contentH);
        float maxScroll = Mathf.Max(0f, _contentH - outerRect.height);

        GUI.skin.verticalScrollbar.fixedWidth = ScrollbarColumnWidth;
        GUI.skin.verticalScrollbarThumb.fixedWidth = ScrollbarColumnWidth;

        if (_stickToBottom) _scroll.y = maxScroll;

        // 横スクロールは無効。縦は内容が溢れたときだけ自動的に出る (alwaysShow=false)
        _scroll = GUI.BeginScrollView(outerRect, _scroll, innerRect, false, false);
        GUI.Label(innerRect, body, _textStyle);
        GUI.EndScrollView();

        // 最下部付近にいれば追従継続、上へスクロール中なら追従停止
        _stickToBottom = _scroll.y >= maxScroll - 2f;
    }

    private string BuildBody()
    {
        // 取得が切れていても、それまでに届いたコメントは読み返せるように出す
        YouTubeChatOverlay.CopyLinesInto(_lines);
        if (_lines.Count == 0)
        {
            return YouTubeChatManager.IsActive
                ? $"<color=#AAAAAA>● 取得中... ({YouTubeChatManager.CurrentVideoId})</color>"
                : "<color=#AAAAAA>▶ /yt ＜配信URL＞ で開始</color>";
        }

        _sb.Clear();
        for (int i = 0; i < _lines.Count; i++)
        {
            if (i > 0) _sb.Append('\n');
            _sb.Append(_lines[i]);
        }

        return _sb.ToString();
    }

    private void EnsureStyles()
    {
        if (_panelBg != null && Math.Abs(_builtScale - Scale) < 0.01f) return;
        _builtScale = Scale;

        _panelTex = SolidTex(new Color(0f, 0f, 0f, 0.66f));               // 半透明黒

        int textFont = Mathf.Max(11, Mathf.RoundToInt(20f * Scale));

        _panelBg = new GUIStyle();
        _panelBg.normal.background = _panelTex;

        _closeStyle = new GUIStyle
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = Mathf.Max(11, Mathf.RoundToInt(20f * Scale)),
            fontStyle = FontStyle.Bold
        };
        _closeStyle.normal.background = _panelTex;
        _closeStyle.normal.textColor = Color.white;

        // 見落とされないよう YouTube の赤で目立たせる
        if (!_openTex) _openTex = SolidTex(new Color(0.79f, 0.10f, 0.10f, 0.96f));
        if (!_openHoverTex) _openHoverTex = SolidTex(new Color(1f, 0.27f, 0.27f, 0.98f));
        _openStyle = new GUIStyle
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = Mathf.Max(11, Mathf.RoundToInt(20f * Scale)),
            fontStyle = FontStyle.Bold
        };
        _openStyle.normal.background = _openTex;
        _openStyle.normal.textColor = Color.white;
        _openContent = new GUIContent(Translator.GetString("YouTubeChatHistoryOpen"));

        _textStyle = new GUIStyle
        {
            alignment = TextAnchor.UpperLeft,
            fontSize = textFont,
            richText = true,
            wordWrap = true,
            clipping = TextClipping.Clip
        };
        _textStyle.normal.textColor = Color.white;
    }

    private static Texture2D SolidTex(Color c)
    {
        var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
        tex.SetPixel(0, 0, c);
        tex.Apply(true, true);
        tex.hideFlags |= HideFlags.HideAndDontSave;
        return tex;
    }
}
