using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace EndKnot.Modules.YouTubeChat;

// YouTube ライブチャットの「ビュー」層 (IMGUI)。
// 画面上をドラッグで自由に動かせる小さな YouTube 風バブル（赤い ▶ ボタン）。
// クリック、または Ctrl+Y でコメント欄をトグル表示する。ClientControlGUI と同じ
// IMGUI + HandleDrag 方式。位置・開閉状態は永続 GameObject 上のフィールドなので
// 会議跨ぎ・ラウンド跨ぎで維持される（毎回開き直し不要）。
// 100% host 画面ローカル（RPC / NetObject 不使用）。
public class YouTubeChatBubble : MonoBehaviour
{
    public static YouTubeChatBubble Instance;

    // ---- 永続する状態 (シーンを跨いで保持) ----
    private bool _isOpen;
    private Vector2 _bubblePos;      // バブル左上 (screen px)
    private bool _posInitialized;

    // ---- ドラッグ ----
    private bool _dragging;
    private bool _didDrag;
    private bool _dragFromBubble;    // ドラッグの起点。バブル起点のみ MouseUp でトグル判定
    private Vector2 _dragOffset;

    // ---- パネル geometry (ComputePanelGeometry が毎フレーム確定させ、HandleBubbleInput / DrawPanel で共有) ----
    private Rect _panelRect;
    private Rect _panelDragZone;     // _panelRect からスクロールバー列を除いた、ドラッグ対象領域

    // ---- スクロール ----
    private Vector2 _scroll;
    private float _contentH;
    private bool _stickToBottom = true; // 最下部にいる間は新着に自動追従

    // ---- スタイル ----
    private GUIStyle _bubbleStyle, _panelBg, _textStyle, _hintStyle;
    private Texture2D _redTex, _redHoverTex, _panelTex;
    private float _builtScale = -1f;

    private readonly List<string> _lines = new();
    private readonly StringBuilder _sb = new();

    private static float Scale => Screen.width / 1080f * 0.5f;
    private static float ScrollbarColumnWidth => (OperatingSystem.IsAndroid() ? 42f : 22f) * Scale;

    private void Awake()
    {
        Instance = this;
    }

    private static bool ShouldShow()
    {
        if (!HudManager.InstanceExists) return false;
        if (!AmongUsClient.Instance || !AmongUsClient.Instance.AmHost) return false;
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
            if (!ShouldShow()) return;

            EnsureStyles();
            EnsureDefaultPos();
            ComputePanelGeometry();

            HandleBubbleInput();
            DrawBubble();
            if (_isOpen) DrawPanel();
        }
        catch (Exception e) { Utils.ThrowException(e); }
    }

    private float BubbleSize => 46f * Scale;

    private Rect BubbleRect => new(_bubblePos.x, _bubblePos.y, BubbleSize, BubbleSize);

    private void EnsureDefaultPos()
    {
        if (_posInitialized) return;
        _posInitialized = true;
        // 左端の中ほど。ClientControlGUI トグル (画面下・左 30%) や左上設定表示と被らない位置。
        _bubblePos = new Vector2(14f * Scale, Screen.height * 0.34f);
    }

    // バブルをドラッグで移動 / クリックでトグル。パネルが開いていればパネル本体
    // (スクロールバー列を除く) を掴んでも同じくバブル位置を動かせる。
    // gameplay へイベントが漏れないよう e.Use()。
    private void HandleBubbleInput()
    {
        Event e = Event.current;
        switch (e.type)
        {
            case EventType.MouseDown when BubbleRect.Contains(e.mousePosition):
                _dragging = true;
                _didDrag = false;
                _dragFromBubble = true;
                _dragOffset = e.mousePosition - _bubblePos;
                e.Use();
                break;
            case EventType.MouseDown when _isOpen && _panelDragZone.Contains(e.mousePosition):
                _dragging = true;
                _didDrag = false;
                _dragFromBubble = false;
                _dragOffset = e.mousePosition - _bubblePos;
                e.Use();
                break;
            case EventType.MouseDrag when _dragging:
                Vector2 np = e.mousePosition - _dragOffset;
                if ((np - _bubblePos).sqrMagnitude > 36f) _didDrag = true; // 6px 以上動いたらドラッグ扱い
                _bubblePos = new Vector2(
                    Mathf.Clamp(np.x, 0f, Screen.width - BubbleSize),
                    Mathf.Clamp(np.y, 0f, Screen.height - BubbleSize));
                e.Use();
                break;
            case EventType.MouseUp when _dragging:
                _dragging = false;
                if (_dragFromBubble && !_didDrag) _isOpen = !_isOpen; // バブルを動かさず離した = クリック
                e.Use();
                break;
        }
    }

    private void DrawBubble()
    {
        // 赤い丸 ▶ ボタン。押下トグルは HandleBubbleInput が担うのでここは見た目だけ。
        _bubbleStyle.normal.background = BubbleRect.Contains(Event.current.mousePosition) ? _redHoverTex : _redTex;
        _bubbleStyle.hover.background = _redHoverTex;
        GUI.Box(BubbleRect, _isOpen ? "▲" : "▶", _bubbleStyle);
    }

    // パネルの数フレーム前から一定の rect と、本体ドラッグ用ゾーン (スクロールバー列を除く)
    // を確定させる。HandleBubbleInput と DrawPanel の両方がこの結果を参照する。
    private const int BaseVisibleLines = 5; // PanelHeight=100% のときの可視行数
    private const float BasePanelWidth = 240f;

    private void ComputePanelGeometry()
    {
        if (!_isOpen)
        {
            _panelRect = default;
            _panelDragZone = default;
            return;
        }

        float pad = 8f * Scale;
        float lineH = 20f * Scale;
        float widthPct = (YouTubeChatOptions.PanelWidth?.GetInt() ?? 100) / 100f;
        float heightPct = (YouTubeChatOptions.PanelHeight?.GetInt() ?? 100) / 100f;

        float pw = BasePanelWidth * Scale * widthPct;
        int visibleLines = Mathf.Max(1, Mathf.RoundToInt(BaseVisibleLines * heightPct));
        float ph = visibleLines * lineH + pad * 2f;

        // バブルの右側に出す。画面外へはみ出すなら左へ回す。下端も画面内へクランプ。
        float px = _bubblePos.x + BubbleSize + 6f * Scale;
        if (px + pw > Screen.width) px = _bubblePos.x - pw - 6f * Scale;
        if (px < 0f) px = 0f;
        float py = Mathf.Clamp(_bubblePos.y, 0f, Screen.height - ph);

        _panelRect = new Rect(px, py, pw, ph);
        _panelDragZone = new Rect(px, py, Mathf.Max(0f, pw - ScrollbarColumnWidth), ph);
    }

    private void DrawPanel()
    {
        if (_panelRect.width <= 0f) return;

        float pad = 8f * Scale;
        GUI.Box(_panelRect, GUIContent.none, _panelBg);

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
        if (!YouTubeChatManager.IsActive)
            return "<color=#AAAAAA>▶ /yt ＜配信URL＞ で開始</color>";

        YouTubeChatOverlay.CopyLinesInto(_lines);
        if (_lines.Count == 0)
            return $"<color=#AAAAAA>● 取得中... ({YouTubeChatManager.CurrentVideoId})</color>";

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
        if (_bubbleStyle != null && Math.Abs(_builtScale - Scale) < 0.01f) return;
        _builtScale = Scale;

        _redTex = SolidTex(new Color(0.79f, 0.10f, 0.10f, 0.96f));       // YouTube 赤
        _redHoverTex = SolidTex(new Color(1f, 0.27f, 0.27f, 0.98f));
        _panelTex = SolidTex(new Color(0f, 0f, 0f, 0.66f));               // 半透明黒

        int bubbleFont = Mathf.Max(14, Mathf.RoundToInt(26f * Scale));
        int textFont = Mathf.Max(11, Mathf.RoundToInt(20f * Scale));

        _bubbleStyle = new GUIStyle
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = bubbleFont,
            fontStyle = FontStyle.Bold,
            richText = false
        };
        _bubbleStyle.normal.textColor = Color.white;
        _bubbleStyle.hover.textColor = Color.white;

        _panelBg = new GUIStyle();
        _panelBg.normal.background = _panelTex;

        _textStyle = new GUIStyle
        {
            alignment = TextAnchor.UpperLeft,
            fontSize = textFont,
            richText = true,
            wordWrap = true,
            clipping = TextClipping.Clip
        };
        _textStyle.normal.textColor = Color.white;

        _hintStyle = _textStyle; // 予備
    }

    private static Texture2D SolidTex(Color c)
    {
        var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
        tex.SetPixel(0, 0, c);
        tex.Apply();
        tex.hideFlags |= HideFlags.HideAndDontSave;
        return tex;
    }
}
