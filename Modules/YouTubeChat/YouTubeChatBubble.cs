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
    // 板・ボタンはロビー右の板と同じ作り (黒い輪郭・灰の縁・内側の溝・暗い灰の中身) を自前のテクスチャに描き、9 分割で伸ばす
    private GUIStyle _panelBg, _textStyle, _closeStyle, _openStyle, _titleStyle, _lineStyle;
    private Texture2D _openTex, _openHoverTex;
    private GUIContent _openContent, _titleContent;
    private readonly GUIContent _closeContent = new("×");
    private Texture2D _panelTex, _closeTex, _closeHoverTex, _lineTex, _thumbTex, _clearTex;
    private float _builtScale = -1f;
    private Rect _titleRect, _lineRect;
    private GUIStyleState _closeState, _barState, _thumbState;
    private GUIStyle _barStyle, _thumbStyle;
    private Texture2D _barBg, _thumbBg;

    private readonly List<string> _lines = new();
    private readonly StringBuilder _sb = new();

    private static float Scale => Il2Direct.ScreenWidth / 1080f * 0.5f * SeatsUserScale;

    // 観客席を出している時は観客席の大きさ設定に合わせる
    private static float SeatsUserScale => YouTubeChatOptions.SeatsEnabled?.GetBool() == true ? (YouTubeChatOptions.SeatsScale?.GetInt() ?? 100) / 100f : 1f;
    private static float ScrollbarColumnWidth => (OperatingSystem.IsAndroid() ? 30f : 10f) * Scale;
    private static float HeaderHeight => 26f * Scale;

    private void Awake()
    {
        Instance = this;
        ImguiHost.Add(this, DrawGui, WantsGui);
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
            if ((Time.frameCount % 30) == 0) MeasureStartButton();

            bool typing = HudManager.Instance.Chat != null && HudManager.Instance.Chat.IsOpenOrOpening;
            if (typing) return;

            if ((Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) && Input.GetKeyDown(KeyCode.Y))
                _isOpen = !_isOpen;
        }
        catch (Exception e) { Utils.ThrowException(e); }
    }

    // ImguiHost が描くかを聞く (偽の間は IMGUI ごと止まる)
    private bool WantsGui()
    {
        if (CleanShot.On) return false;
        if (ShouldShow()) return true;

        _panelRect = default;
        return false;
    }

    private void DrawGui()
    {
        try
        {
            if (CleanShot.On) return;
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
        float pad = PanelPad;
        float lineH = _textStyle.lineHeight > 0f ? _textStyle.lineHeight : 20f * Scale;
        float widthPct = (YouTubeChatOptions.PanelWidth?.GetInt() ?? 100) / 100f;
        float heightPct = (YouTubeChatOptions.PanelHeight?.GetInt() ?? 100) / 100f;

        int visibleLines = Mathf.Max(1, Mathf.RoundToInt(BaseVisibleLines * heightPct));
        float ph = visibleLines * lineH + pad * 2f + HeaderHeight;

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

        // ロビーでは下の中央の「開始」ボタンに掛からないよう、右端をその手前で止める
        float startLeft = _startLeft;
        if (startLeft > 0f && px < startLeft && px + pw > startLeft - 8f * Scale)
            pw = Mathf.Max(120f * Scale, startLeft - 8f * Scale - px);

        px = Mathf.Clamp(px, 0f, Mathf.Max(0f, Screen.width - pw));
        py = Mathf.Clamp(py, 0f, Mathf.Max(0f, Screen.height - ph));

        _panelRect = new Rect(px, py, pw, ph);
        _panelHitZone = new Rect(px, py, Mathf.Max(0f, pw - ScrollbarColumnWidth - pad), ph);

        // 見出しの段: 左に題、右端に ×、その下に区切り線
        float cs = 20f * Scale;
        float headerMid = py + pad + HeaderHeight / 2f - 2f * Scale;
        _closeRect = new Rect(px + pw - pad - cs, headerMid - cs / 2f, cs, cs);
        _titleRect = new Rect(px + pad + 2f * Scale, headerMid - cs / 2f, pw - pad * 2f - cs, cs);
        _lineRect = new Rect(px + pad, py + pad + HeaderHeight - 3f * Scale, pw - pad * 2f, Mathf.Max(1f, Mathf.Round(1f * Scale)));
    }

    // 「開始」ボタンの左端 (IMGUI 座標・無ければ 0)。OnGUI は 1 フレームに何度も呼ばれるので、Update でときどき測っておく
    private float _startLeft;

    private void MeasureStartButton()
    {
        _startLeft = 0f;
        if (!GameStates.IsLobby || !GameStartManager.InstanceExists) return;
        PassiveButton start = GameStartManager.Instance.StartButton;
        Camera cam = HudManager.Instance.UICamera;
        if (!start || !start.gameObject.activeInHierarchy || !cam) return;
        SpriteRenderer sr = start.GetComponentInChildren<SpriteRenderer>();
        if (!sr) return;
        float left = cam.WorldToScreenPoint(sr.bounds.min).x;
        if (left > 0f) _startLeft = left;
    }

    private void DrawPanel()
    {
        if (_panelRect.width <= 0f) return;

        float pad = PanelPad;
        GUI.Box(_panelRect, GUIContent.none, _panelBg);
        GUI.Label(_titleRect, _titleContent, _titleStyle);
        if (Event.current.type == EventType.Repaint) _closeState.background = _closeRect.Contains(Event.current.mousePosition) ? _closeHoverTex : _closeTex;
        GUI.Box(_closeRect, _closeContent, _closeStyle);
        GUI.Box(_lineRect, GUIContent.none, _lineStyle);

        float top = _panelRect.y + pad + HeaderHeight;
        var outerRect = new Rect(_panelRect.x + pad, top, _panelRect.width - pad * 2f, _panelRect.yMax - pad - top);
        float contentW = Mathf.Max(1f, outerRect.width - ScrollbarColumnWidth - 4f * Scale);

        string body = BuildBody();
        float measuredH = _textStyle.CalcHeight(new GUIContent(body), contentW);
        _contentH = measuredH;

        var innerRect = new Rect(0, 0, contentW, _contentH);
        float maxScroll = Mathf.Max(0f, _contentH - outerRect.height);

        // スクロールバーは本編の板の縁と同じ灰の細いつまみだけにする (溝は透明)。skin は他の画面と共有なので描き終えたら戻す
        // 参照と元の絵は EnsureStyles で 1 回だけ取る (取るたびに包みができ、OnGUI は 1 フレームに何度も呼ばれる)
        GUIStyle bar = _barStyle, thumb = _thumbStyle;
        float barW = bar.fixedWidth, thumbW = thumb.fixedWidth;
        bar.fixedWidth = thumb.fixedWidth = ScrollbarColumnWidth;
        _barState.background = _clearTex;
        _thumbState.background = _thumbTex;

        if (_stickToBottom) _scroll.y = maxScroll;

        // 横スクロールは無効。縦は内容が溢れたときだけ自動的に出る (alwaysShow=false)
        try
        {
            _scroll = GUI.BeginScrollView(outerRect, _scroll, innerRect, false, false);
            GUI.Label(innerRect, body, _textStyle);
            GUI.EndScrollView();
        }
        finally
        {
            _barState.background = _barBg;
            _thumbState.background = _thumbBg;
            bar.fixedWidth = barW;
            thumb.fixedWidth = thumbW;
        }

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

        // 縁の太さは画面の大きさに合わせて描き直す (9 分割の縁はテクスチャの画素のまま画面に出るため)。前の物は捨てる
        foreach (Texture2D old in new[] { _panelTex, _closeTex, _closeHoverTex, _openTex, _openHoverTex })
            if (old) Destroy(old);

        float k = Mathf.Max(0.6f, Scale / 0.89f); // 1080p で 1
        // 外から: 黒い輪郭 → 明るい縁 → 灰の縁 → 暗い縁 → 黒い溝 → 中身 (ロビー右の板の実測を細くしたもの)
        _panelTex = FrameTex(k, 9f, PanelBorder(k),
            (0f, new Color32(16, 12, 16, 255)),
            (2f, new Color32(140, 150, 155, 255)),
            (3f, new Color32(108, 112, 116, 255)),
            (7f, new Color32(70, 73, 78, 255)),
            (8f, new Color32(8, 8, 8, 255)),
            (9.5f, new Color32(41, 41, 41, 240)));
        // 本編のメニューの閉じるボタン: 白い枠の黒い角丸に白い ×。乗ると枠が本編の緑になる
        _closeTex = FrameTex(k, 5f, Mathf.RoundToInt(6f * k), (0f, new Color32(236, 236, 236, 255)), (2f, new Color32(18, 18, 18, 255)));
        _closeHoverTex = FrameTex(k, 5f, Mathf.RoundToInt(6f * k), (0f, new Color32(44, 232, 200, 255)), (2f, new Color32(18, 18, 18, 255)));
        // 閉じている間の「開く」ボタン: 見落とされないよう YouTube の赤。本編のボタンと同じく濃い縁取り
        _openTex = FrameTex(k, 6f, Mathf.RoundToInt(8f * k), (0f, new Color32(40, 8, 8, 255)), (2f, new Color32(201, 26, 26, 255)), (3f, new Color32(220, 40, 40, 255)));
        _openHoverTex = FrameTex(k, 6f, Mathf.RoundToInt(8f * k), (0f, new Color32(40, 8, 8, 255)), (2f, new Color32(240, 70, 70, 255)), (3f, new Color32(255, 85, 85, 255)));

        if (!_lineTex) _lineTex = SolidTex(new Color32(70, 73, 78, 255));
        if (!_thumbTex) _thumbTex = SolidTex(new Color32(108, 112, 116, 230));
        if (!_clearTex) _clearTex = SolidTex(new Color(0f, 0f, 0f, 0f));

        int textFont = Mathf.Max(11, Mathf.RoundToInt(20f * Scale));

        _panelBg = SlicedStyle(_panelTex, PanelBorder(k));

        _closeStyle = SlicedStyle(_closeTex, Mathf.RoundToInt(6f * k));
        _closeStyle.alignment = TextAnchor.MiddleCenter;
        _closeStyle.fontSize = Mathf.Max(10, Mathf.RoundToInt(17f * Scale));
        _closeStyle.fontStyle = FontStyle.Bold;
        _closeStyle.normal.textColor = Color.white;

        _openStyle = SlicedStyle(_openTex, Mathf.RoundToInt(8f * k));
        _openStyle.alignment = TextAnchor.MiddleCenter;
        _openStyle.fontSize = Mathf.Max(11, Mathf.RoundToInt(20f * Scale));
        _openStyle.fontStyle = FontStyle.Bold;
        _openStyle.normal.textColor = Color.white;
        _openContent = new GUIContent(Translator.GetString("YouTubeChatHistoryOpen"));

        _titleStyle = new GUIStyle
        {
            alignment = TextAnchor.MiddleLeft,
            fontSize = Mathf.Max(10, Mathf.RoundToInt(16f * Scale)),
            fontStyle = FontStyle.Bold,
            clipping = TextClipping.Clip
        };
        _titleStyle.normal.textColor = new Color32(178, 178, 178, 255);
        _titleContent = new GUIContent(Translator.GetString("YouTubeChatHistoryTitle"));

        _lineStyle = new GUIStyle();
        _lineStyle.normal.background = _lineTex;

        _closeState = _closeStyle.normal;
        if (_barStyle == null)
        {
            _barStyle = GUI.skin.verticalScrollbar;
            _thumbStyle = GUI.skin.verticalScrollbarThumb;
            _barState = _barStyle.normal;
            _thumbState = _thumbStyle.normal;
            _barBg = _barState.background;
            _thumbBg = _thumbState.background;
        }

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

    private static float PanelPad => 15f * Scale;
    private static int PanelBorder(float k) => Mathf.RoundToInt(13f * k);

    // 背景を 9 分割で伸ばす GUIStyle。縁を設定できなければ (古い環境) 全体を引き伸ばして描く
    private static bool _borderUnsupported;

    private static GUIStyle SlicedStyle(Texture2D tex, int border)
    {
        var style = new GUIStyle();
        style.normal.background = tex;
        if (_borderUnsupported) return style;

        try
        {
            // RectOffset の 4 引数コンストラクタは interop に無いので、style が持つ物の各辺を書く
            RectOffset b = style.border;
            b.left = b.right = b.top = b.bottom = border;
        }
        catch (Exception e)
        {
            _borderUnsupported = true;
            Logger.Warn($"GUIStyle.border unavailable: {e.Message}", "YouTubeChatBubble");
        }

        return style;
    }

    // 角の丸い四角を外側から順に塗り重ねたテクスチャ。layers は (外側からの距離 px @1080p, 色)。縁は 1px ぼかす
    private static Texture2D FrameTex(float k, float radius, int border, params (float inset, Color32 color)[] layers)
    {
        int size = border * 2 + 2;
        var px = new Color32[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            Color c = new(0f, 0f, 0f, 0f);
            foreach ((float inset, Color32 color) in layers)
            {
                float d = RoundedRectDistance(x, y, size, Math.Max(1.5f, (radius - inset) * k), inset * k);
                float cov = Math.Clamp(0.5f - d, 0f, 1f);
                if (cov > 0f) c = Color.Lerp(c, color, cov);
            }

            px[y * size + x] = c;
        }

        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        tex.SetPixels32(px);
        tex.Apply(false, true);
        tex.hideFlags |= HideFlags.HideAndDontSave;
        return tex;
    }

    // 四角の内側で負・外側で正の距離 (px)。inset だけ縁から内側へ寄せた角丸の四角に対して測る
    private static float RoundedRectDistance(int x, int y, int size, float radius, float inset)
    {
        float half = size / 2f - inset;
        float qx = Math.Abs(x + 0.5f - size / 2f) - (half - radius);
        float qy = Math.Abs(y + 0.5f - size / 2f) - (half - radius);
        float ox = Math.Max(qx, 0f), oy = Math.Max(qy, 0f);
        return (float)Math.Sqrt(ox * ox + oy * oy) + Math.Min(Math.Max(qx, qy), 0f) - radius;
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
