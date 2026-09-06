using System;
using EndKnot.Modules.Companion;
using EndKnot.Modules.YouTubeChat;
using UnityEngine;
using UnityEngine.SceneManagement;
using static EndKnot.Translator;
// ReSharper disable InconsistentNaming

namespace EndKnot.Modules.Setup;

// 「配信セットアップ」ウィンドウ。ホスト画面ローカルの IMGUI (RPC/NetObject 不使用)。
// 状態の検出・保存は Modules/Setup/StreamSetupState.cs が持ち、このクラスは読んで描くだけ
// (重い処理を OnGUI 内で行わない — OnGUI は1フレームに複数回呼ばれるため)。
// Modules/ClientControlGUI.cs の構成 (RebuildStyles・MakeBtn/RoundedTexture・ドラッグ移動・
// BeginScrollView) をテンプレとして踏襲する。
public class StreamSetupGUI : MonoBehaviour
{
    public static StreamSetupGUI Instance;
    public bool IsOpen;

    private Vector2 _scroll;
    private float _contentH;
    private Rect _windowRect;
    private bool _dragging;
    private Vector2 _dragOffset;
    private bool _windowInitialized;

    private static float PlatformScale => OperatingSystem.IsAndroid() ? 0.6f : 0.5f;
    private static float Scale => Screen.width / 1080f * PlatformScale;

    // 起動直後 (コンテンツ計測前) の初期高さ、および計測後もこれ以上には広げない上限。
    private static float MaxWindowHeightFraction => OperatingSystem.IsAndroid() ? 0.85f : 0.80f;

    // これより低くはしない (計測前のちらつき防止・行が減った瞬間に潰れすぎないため)。
    private static float MinWindowHeightFraction => OperatingSystem.IsAndroid() ? 0.35f : 0.30f;

    // 直前にウィンドウ背景を作り直した高さ。これと計測結果の差が小さい間は作り直さない
    // (状態が変わるたびに角丸テクスチャを再生成するとテクスチャが積み上がるため)。
    private float _lastBuiltHeight = -1f;

    private static int FontSize => Mathf.Max(12, Mathf.RoundToInt(19f * Scale));
    private static int SmallFontSize => Mathf.Max(10, FontSize - 5);
    private static float RowLineHeight => 30f * Scale;
    private static float ButtonHeight => 38f * Scale;
    private static float ButtonWidth => 210f * Scale;
    private static float Padding => 10f * Scale;
    private static float GlyphColumnWidth => 56f * Scale;
    private static float ContentWidth => 540f * Scale;
    private static float ScrollbarColumnWidth => (OperatingSystem.IsAndroid() ? 42f : 22f) * Scale;

    private float _lastScale = -1f;

    private GUIStyle _sWindow, _sTitleBar, _sDragHint, _sSection, _sRowName, _sStatus, _sHint, _sBtn, _sClose;

    // StreamSetupState の enum/バージョン文字列から Update() (main thread) で組み立てるキャッシュ。
    // GetString は IL2CPP の TranslationController に触るためメインスレッド専用 — ワーカーからは
    // 呼べない。OnGUI はこのキャッシュを読むだけで、文言解決も string.Format も行わない。
    private string _pythonStatusText = "";
    private string _geminiStatusText = "";
    private string _geminiErrorText = "";
    private string _geminiVerifyText = "";
    private string _voiceVoxStatusText = "";

    private string _consoleStatusText = "";
    private string _saveErrorText = "";
    private string _connectStatusText = "";
    private string _connectErrorText = "";
    private string _detectStatusText = "";
    private string _testPostStatusText = "";
    private string _posterStatusText = "";

    // 判定結果を表す色。役職の Palette とは無関係な、UI 状態専用の配色。
    private static readonly Color ColorOk = new(0.45f, 0.85f, 0.45f);
    private static readonly Color ColorMissing = new(0.95f, 0.40f, 0.40f);
    private static readonly Color ColorWarn = new(0.95f, 0.75f, 0.35f);
    private static readonly Color ColorNeutral = new(0.65f, 0.72f, 0.82f);

    // 既定 GUI スキンのフォントに Unicode 記号 (✓/✗) が入っているか未確認のため、
    // ASCII のみで状態を表す。実機で問題なければ記号への差し替えは1箇所で済む。
    private const string OkGlyph = "[OK]";
    private const string MissingGlyph = "[X]";
    private const string WarnGlyph = "[!]";
    private const string NeutralGlyph = "[-]";

    private void Awake()
    {
        Instance = this;
        SceneManager.add_sceneLoaded((Action<Scene, LoadSceneMode>)OnSceneLoaded);
        Logger.Info("StreamSetupGUI initialised", "StreamSetupGUI");
    }

    private void OnDestroy()
    {
        SceneManager.remove_sceneLoaded((Action<Scene, LoadSceneMode>)OnSceneLoaded);
        DestroyStyleTextures();
    }

    // 解像度はシーンをまたぐと変わりうるので、開いたままシーン遷移した場合は次フレームで
    // ウィンドウ位置を再計算させる。
    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        _windowInitialized = false;
    }

    private void Update()
    {
        // OnGUI 側の KeyDown/Escape 判定に加えて、こちらは repo で実績のある Input.GetKeyDown
        // 経路 (Patches/NumericOptionInputPatch.cs) での確実な閉じ手段。どちらか一方が拾えば良い。
        if (IsOpen && Input.GetKeyDown(KeyCode.Escape)) IsOpen = false;

        // クリップボードのクリアはバックグラウンドの保存ワーカーからは行えない (Unity API はメイン
        // スレッド専用) ため、フラグ経由でここで消費する。
        if (StreamSetupState.ClipboardClearPending)
        {
            StreamSetupState.ClipboardClearPending = false;
            try { GUIUtility.systemCopyBuffer = ""; }
            catch { /* クリップボードが他アプリに握られている等は無視してよい */ }
        }

        // device flow が受け取った refresh token の ConfigEntry 書込みも同じ理由でメインスレッド
        // 専用 (ConfigFile.Save を伴う)。バックグラウンドから直接書かず、ここで消費する。
        if (StreamSetupYouTubeState.PendingRefreshToken != null)
            StreamSetupYouTubeState.ConsumePendingRefreshToken();

        // ワーカーが状態を更新したら、文言解決 (GetString/string.Format) をここでまとめて1回だけ行う。
        // OnGUI は毎フレーム呼ばれるが、このキャッシュを読むだけなので文字列組み立ては発生しない。
        // 起動直後は Translator.Init() より前にこの Update が回りうるので、それまでは Dirty を
        // 立てたまま待つ (GetString の NRE を避ける)。
        if (StreamSetupState.Dirty && Translator.IsInitialized)
        {
            StreamSetupState.Dirty = false;
            ResolveDisplayStrings();
        }
    }

    private void ResolveDisplayStrings()
    {
        _pythonStatusText = StreamSetupState.PythonStatus switch
        {
            StreamSetupState.PythonState.Bundled => GetString("Setup.Python.Bundled"),
            StreamSetupState.PythonState.Ok => string.Format(GetString("Setup.Python.Found"), StreamSetupState.PythonVersion),
            StreamSetupState.PythonState.TooOld => string.Format(GetString("Setup.Python.TooOld"), StreamSetupState.PythonVersion),
            _ => GetString("Setup.Python.NotFound")
        };

        _geminiStatusText = StreamSetupState.KeyStatus == StreamSetupState.KeyState.Set
            ? string.Format(GetString("Setup.Gemini.Set"), StreamSetupState.KeyMasked)
            : GetString("Setup.Gemini.NotSet");

        _geminiErrorText = StreamSetupState.KeyError switch
        {
            StreamSetupState.KeyErrorKind.ClipboardInvalid => GetString("Setup.Gemini.Error.ClipboardInvalid"),
            StreamSetupState.KeyErrorKind.SaveFailed => GetString("Setup.Gemini.Error.SaveFailed"),
            _ => ""
        };

        _geminiVerifyText = StreamSetupState.KeyVerifyResult switch
        {
            StreamSetupState.KeyVerifyKind.Ok => GetString("Setup.Gemini.Verify.Ok"),
            StreamSetupState.KeyVerifyKind.Invalid => GetString("Setup.Gemini.Verify.Invalid"),
            StreamSetupState.KeyVerifyKind.Unknown => GetString("Setup.Gemini.Verify.Unknown"),
            StreamSetupState.KeyVerifyKind.NoKey => GetString("Setup.Gemini.Verify.NoKey"),
            _ => ""
        };

        _voiceVoxStatusText = StreamSetupState.VoiceVoxStatus == StreamSetupState.VoiceVoxState.Ok
            ? string.Format(GetString("Setup.VoiceVox.Ok"), StreamSetupState.VoiceVoxVersion)
            : GetString("Setup.VoiceVox.NotRunning");

        bool idSet = StreamSetupYouTubeState.ClientIdSet;
        bool secretSet = StreamSetupYouTubeState.ClientSecretSet;
        _consoleStatusText = idSet && secretSet
            ? GetString("Setup.YouTube.Console.Done")
            : string.Format(GetString("Setup.YouTube.Console.StepOf4"), idSet || secretSet ? 4 : 1);

        _saveErrorText = StreamSetupYouTubeState.SaveError switch
        {
            StreamSetupYouTubeState.SaveErrorKind.IdInvalid => GetString("Setup.YouTube.Console.Error.IdInvalid"),
            StreamSetupYouTubeState.SaveErrorKind.SecretInvalid => GetString("Setup.YouTube.Console.Error.SecretInvalid"),
            _ => ""
        };

        _connectStatusText = StreamSetupYouTubeState.Connect switch
        {
            StreamSetupYouTubeState.ConnectState.Connected => GetString("Setup.YouTube.Connect.Connected"),
            StreamSetupYouTubeState.ConnectState.Requesting => GetString("Setup.YouTube.Connect.Requesting"),
            StreamSetupYouTubeState.ConnectState.WaitingForUser => GetString("Setup.YouTube.Connect.WaitingForUser"),
            StreamSetupYouTubeState.ConnectState.Failed => GetString("Setup.YouTube.Connect.Failed"),
            _ => GetString("Setup.YouTube.Connect.NotConnected")
        };

        _connectErrorText = StreamSetupYouTubeState.Connect != StreamSetupYouTubeState.ConnectState.Failed
            ? ""
            : StreamSetupYouTubeState.ConnectErrorCode switch
            {
                "cancelled" => GetString("Setup.YouTube.Connect.Cancelled"),
                "expired" => GetString("Setup.YouTube.Connect.Expired"),
                _ => string.Format(GetString("Setup.YouTube.Connect.ErrorDetail"), StreamSetupYouTubeState.ConnectErrorCode)
            };

        _detectStatusText = StreamSetupYouTubeState.Detect switch
        {
            StreamSetupYouTubeState.DetectState.Checking => GetString("Setup.YouTube.Detect.Checking"),
            StreamSetupYouTubeState.DetectState.Found => string.Format(GetString("Setup.YouTube.Detect.Found"), StreamSetupYouTubeState.DetectedTitle),
            StreamSetupYouTubeState.DetectState.NotFound => GetString("Setup.YouTube.Detect.NotFound"),
            StreamSetupYouTubeState.DetectState.TokenInvalid => GetString("Setup.YouTube.Detect.TokenInvalid"),
            _ => GetString("Setup.YouTube.Detect.Unknown")
        };

        _testPostStatusText = StreamSetupYouTubeState.TestPost switch
        {
            StreamSetupYouTubeState.TestPostState.Posting => GetString("Setup.YouTube.TestPost.Posting"),
            StreamSetupYouTubeState.TestPostState.Success => GetString("Setup.YouTube.TestPost.Success"),
            StreamSetupYouTubeState.TestPostState.Failed => GetString($"Setup.YouTube.TestPost.Error.{StreamSetupYouTubeState.TestPostErrorKind}"),
            _ => ""
        };

        bool posterOn = YouTubePostOptions.Enabled?.GetBool() == true;
        _posterStatusText = posterOn ? GetString("Setup.YouTube.Poster.On") : GetString("Setup.YouTube.Poster.Off");
    }

    public static void Open()
    {
        if (Instance == null) return;

        Instance.IsOpen = true;
        StreamSetupState.KeyError = StreamSetupState.KeyErrorKind.None;
        StreamSetupState.KeyVerifyResult = StreamSetupState.KeyVerifyKind.None;
        StreamSetupYouTubeState.Refresh();
        if (Translator.IsInitialized) Instance.ResolveDisplayStrings();
        StreamSetupState.Refresh();
    }

    public static void Toggle()
    {
        if (Instance == null) return;

        if (Instance.IsOpen) Instance.IsOpen = false;
        else Open();
    }

    private void OnGUI()
    {
        if (!IsOpen) return;
        // 起動直後は Translator.Init() より前に OnGUI が回りうる。GetString の NRE を避ける。
        if (!Translator.IsInitialized) return;

        Event current = Event.current;
        if (current != null && current.type == EventType.KeyDown && current.keyCode == KeyCode.Escape)
        {
            IsOpen = false;
            current.Use();
            return;
        }

        try
        {
            if (!_windowInitialized) InitWindowRect();
            UpdateWindowHeightForContent();
            if (Math.Abs(_lastScale - Scale) > 0.01f) RebuildStyles();

            HandleDrag();
            DrawWindow();
        }
        catch (Exception e)
        {
            // 握って続行すると毎フレーム再発火してログが洪水になるので、まず閉じてから投げる。
            IsOpen = false;
            Utils.ThrowException(e);
        }
    }

    private void InitWindowRect()
    {
        float w = ContentWidth + Padding * 4f + ScrollbarColumnWidth;
        float h = Screen.height * MaxWindowHeightFraction;
        _windowRect = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
        _windowInitialized = true;

        // 前回セッションの高さが残っていると、初期化直後の実測コンテンツ高さとの差が
        // 3px 未満に見えて UpdateWindowHeightForContent が縮小をスキップしてしまう。
        _lastBuiltHeight = -1f;
    }

    // セクションが増減するたびに空欄/スクロールが不自然に余るのを防ぐため、直前フレームで
    // 実測したコンテンツ高さ (_contentH) にウィンドウを合わせる (下限/上限は画面比率でクランプ)。
    // 初回フレームはまだ _contentH が無いので何もしない (InitWindowRect の初期値のまま)。
    private void UpdateWindowHeightForContent()
    {
        if (_contentH <= 0f) return;

        float titleH = ButtonHeight * 0.9f + Padding;
        float desired = Mathf.Clamp(
            _contentH + titleH + Padding * 2.5f,
            Screen.height * MinWindowHeightFraction,
            Screen.height * MaxWindowHeightFraction);

        if (Mathf.Abs(desired - _lastBuiltHeight) < 3f) return;

        _windowRect.height = desired;
        if (_windowRect.y + _windowRect.height > Screen.height)
            _windowRect.y = Mathf.Max(0f, Screen.height - _windowRect.height);

        // 次の RebuildStyles で背景テクスチャをこの高さに合わせて作り直させる。頻度は状態変化
        // (Refresh 完了・保存成功等) の時だけなので、解像度変更と同程度にしか起きない。
        _lastScale = -1f;
    }

    private void RebuildStyles()
    {
        _lastScale = Scale;
        _lastBuiltHeight = _windowRect.height;

        // 直前の角丸背景テクスチャはプールされないので、作り直す前に明示的に破棄する
        // (高さがコンテンツ追従で変わるたびに呼ばれるため、捨てないと積み上がる)。
        DestroyStyleTextures();

        int winW = Mathf.Max(1, Mathf.RoundToInt(ContentWidth + Padding * 4f + ScrollbarColumnWidth));
        int winH = Mathf.Max(1, Mathf.RoundToInt(_windowRect.height));

        _sWindow = new GUIStyle
        {
            normal = { background = RoundedTexture(winW, winH, 22, new Color(0.06f, 0.07f, 0.15f, 1f), new Color(0.10f, 0.16f, 0.30f, 1f)) }
        };

        _sTitleBar = new GUIStyle
        {
            fontSize = FontSize + 3,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
            richText = false,
            normal = { textColor = new Color(0.72f, 0.90f, 1.00f, 1f) }
        };

        _sDragHint = new GUIStyle
        {
            fontSize = Mathf.Max(10, FontSize - 5),
            fontStyle = FontStyle.Italic,
            alignment = TextAnchor.MiddleCenter,
            richText = false,
            normal = { textColor = new Color(0.45f, 0.58f, 0.72f, 1f) }
        };

        _sSection = new GUIStyle
        {
            fontSize = FontSize,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft,
            richText = false,
            normal = { textColor = new Color(0.58f, 0.82f, 1.00f, 1f) }
        };

        _sRowName = new GUIStyle
        {
            fontSize = FontSize,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft,
            wordWrap = false,
            richText = false,
            normal = { textColor = Color.white }
        };

        _sStatus = new GUIStyle
        {
            fontSize = FontSize,
            alignment = TextAnchor.MiddleLeft,
            wordWrap = false,
            richText = false,
            normal = { textColor = Color.white }
        };

        _sHint = new GUIStyle
        {
            fontSize = SmallFontSize,
            fontStyle = FontStyle.Italic,
            alignment = TextAnchor.MiddleLeft,
            wordWrap = true,
            richText = false,
            normal = { textColor = new Color(0.68f, 0.76f, 0.86f, 1f) }
        };

        int btnW = Mathf.Max(1, Mathf.RoundToInt(ButtonWidth));
        int btnH = Mathf.Max(1, Mathf.RoundToInt(ButtonHeight));
        int btnRadius = Mathf.Max(1, Mathf.RoundToInt(ButtonHeight * 0.3f));

        _sBtn = new GUIStyle
        {
            fontSize = SmallFontSize,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
            wordWrap = true,
            richText = false,
            normal = { background = RoundedTexture(btnW, btnH, btnRadius, new Color(0.07f, 0.22f, 0.40f, 1f), new Color(0.12f, 0.36f, 0.62f, 1f)), textColor = Color.white },
            hover = { background = RoundedTexture(btnW, btnH, btnRadius, new Color(0.12f, 0.32f, 0.54f, 1f), new Color(0.20f, 0.46f, 0.74f, 1f)), textColor = Color.white },
            active = { background = RoundedTexture(btnW, btnH, btnRadius, new Color(0.04f, 0.14f, 0.28f, 1f), new Color(0.08f, 0.22f, 0.40f, 1f)), textColor = Color.white }
        };

        int closeSize = Mathf.Max(1, Mathf.RoundToInt(30f * Scale));
        int closeRadius = closeSize / 4;

        _sClose = new GUIStyle
        {
            fontSize = FontSize,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
            richText = false,
            normal = { background = RoundedTexture(closeSize, closeSize, closeRadius, new Color(0.35f, 0.10f, 0.10f, 1f), new Color(0.55f, 0.16f, 0.16f, 1f)), textColor = Color.white },
            hover = { background = RoundedTexture(closeSize, closeSize, closeRadius, new Color(0.55f, 0.16f, 0.16f, 1f), new Color(0.75f, 0.22f, 0.22f, 1f)), textColor = Color.white },
            active = { background = RoundedTexture(closeSize, closeSize, closeRadius, new Color(0.22f, 0.06f, 0.06f, 1f), new Color(0.35f, 0.10f, 0.10f, 1f)), textColor = Color.white }
        };
    }

    private void DestroyStyleTextures()
    {
        DestroyTex(_sWindow?.normal.background);
        DestroyTex(_sBtn?.normal.background);
        DestroyTex(_sBtn?.hover.background);
        DestroyTex(_sBtn?.active.background);
        DestroyTex(_sClose?.normal.background);
        DestroyTex(_sClose?.hover.background);
        DestroyTex(_sClose?.active.background);
    }

    private static void DestroyTex(Texture2D tex)
    {
        if (tex != null) Destroy(tex);
    }

    // Modules/ClientControlGUI.cs の RoundedTexture / CornerAlpha と同じアルゴリズム。
    // 白 1x1 相当の矩形塗りつぶしではなく角丸のため、テクスチャを一度だけ生成して使い回す。
    private static Texture2D RoundedTexture(int width, int height, int radius, Color fill, Color edge)
    {
        width = Mathf.Max(1, width);
        height = Mathf.Max(1, height);
        radius = Mathf.Clamp(radius, 0, Mathf.Min(width, height) / 2);
        var tex = new Texture2D(width, height, TextureFormat.ARGB32, false) { filterMode = FilterMode.Bilinear };

        for (int py = 0; py < height; py++)
        {
            for (int px = 0; px < width; px++)
            {
                float a = CornerAlpha(px, py, width, height, radius);
                Color c = a <= 0f ? Color.clear
                    : a >= 1f ? fill
                    : Color.Lerp(edge, fill, a);
                tex.SetPixel(px, py, c);
            }
        }

        tex.Apply(true, true);
        tex.hideFlags = HideFlags.HideAndDontSave;
        return tex;
    }

    private static float CornerAlpha(int px, int py, int w, int h, int r)
    {
        int cx, cy;
        if (px < r && py < r) { cx = r; cy = r; }
        else if (px >= w - r && py < r) { cx = w - r; cy = r; }
        else if (px < r && py >= h - r) { cx = r; cy = h - r; }
        else if (px >= w - r && py >= h - r) { cx = w - r; cy = h - r; }
        else return 1f;

        float d = Mathf.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
        if (d >= r + 1f) return 0f;
        if (d <= r - 1f) return 1f;
        return r + 0.5f - d;
    }

    // タイトルバーの右端に重なる X ボタンの矩形。ドラッグ判定 (HandleDrag) と描画 (DrawWindow) の
    // 両方で同じ式を使い、ずれで X が押せなくなる/ドラッグに食われることが無いようにする。
    private Rect GetCloseRect()
    {
        float closeSize = 30f * Scale;
        return new Rect(_windowRect.x + _windowRect.width - closeSize - Padding * 0.6f, _windowRect.y + Padding * 0.6f, closeSize, closeSize);
    }

    private void HandleDrag()
    {
        Event e = Event.current;
        float titleH = ButtonHeight * 0.9f + Padding;
        var titleRect = new Rect(_windowRect.x, _windowRect.y, _windowRect.width, titleH);
        Rect closeRect = GetCloseRect();

        switch (e.type)
        {
            // X ボタンの上は常にドラッグ対象から除外する。ここで e.Use() すると GUI.Button 側の
            // MouseDown が届かずクリックが成立しなくなる (タイトルバー全体を掴む判定が X を覆っていた)。
            case EventType.MouseDown when titleRect.Contains(e.mousePosition) && !closeRect.Contains(e.mousePosition):
                _dragging = true;
                _dragOffset = e.mousePosition - new Vector2(_windowRect.x, _windowRect.y);
                e.Use();
                break;

            case EventType.MouseDrag when _dragging:
                float nx = Mathf.Clamp(e.mousePosition.x - _dragOffset.x, 0, Screen.width - _windowRect.width);
                float ny = Mathf.Clamp(e.mousePosition.y - _dragOffset.y, 0, Screen.height - _windowRect.height);
                _windowRect.x = nx;
                _windowRect.y = ny;
                e.Use();
                break;

            case EventType.MouseUp:
                _dragging = false;
                break;
        }
    }

    private void DrawWindow()
    {
        GUI.Box(_windowRect, "", _sWindow);

        float titleH = ButtonHeight * 0.9f + Padding;

        GUI.Label(
            new Rect(_windowRect.x, _windowRect.y + Padding * 0.6f, _windowRect.width, ButtonHeight * 0.6f),
            GetString("Setup.Window.Title"),
            _sTitleBar
        );

        if (GUI.Button(GetCloseRect(), "X", _sClose))
            IsOpen = false;

        GUI.Label(
            new Rect(_windowRect.x, _windowRect.y + ButtonHeight * 0.62f + Padding * 0.4f, _windowRect.width, ButtonHeight * 0.4f),
            GetString("Setup.DragHint"),
            _sDragHint
        );

        float scrollY = _windowRect.y + titleH + Padding * 0.4f;
        float scrollH = _windowRect.height - titleH - Padding;
        float visibleW = _windowRect.width - Padding * 2f;
        float contentW = visibleW - ScrollbarColumnWidth - 1f;

        var outerRect = new Rect(_windowRect.x + Padding, scrollY, visibleW, scrollH);
        var innerRect = new Rect(0, 0, contentW, _contentH);

        GUI.skin.verticalScrollbar.fixedWidth = ScrollbarColumnWidth;
        GUI.skin.verticalScrollbarThumb.fixedWidth = ScrollbarColumnWidth;

        _scroll = GUI.BeginScrollView(outerRect, _scroll, innerRect, false, false);
        float y = Padding * 0.5f;
        DrawContent(ref y, contentW);
        _contentH = y + Padding;
        GUI.EndScrollView();
    }

    private void DrawContent(ref float y, float w)
    {
        // AI実況は Companion ランチャーが Windows 専用のため、非対応環境ではこの
        // セクションだけ案内文に差し替える。YouTube自動投稿は HTTP のみで完結し
        // プラットフォーム非依存なので、この分岐の外に置いて常に描く。
        if (CompanionLauncher.IsSupported)
        {
            Section(ref y, w, GetString("Setup.Section.AICommentary"));
            DrawPythonRow(ref y, w);
            DrawGeminiRow(ref y, w);
            DrawVoiceVoxRow(ref y, w);
            DrawBottomRow(ref y, w);
        }
        else
        {
            GUI.Label(new Rect(0, y, w, RowLineHeight * 2f), GetString("Setup.WindowsOnly"), _sHint);
            y += RowLineHeight * 2f + Padding;
        }

        Section(ref y, w, GetString("Setup.Section.YouTubePost"));
        DrawGoogleConsoleRow(ref y, w);
        DrawYouTubeConnectRow(ref y, w);
        DrawYouTubeDetectRow(ref y, w);
        DrawYouTubeTestPostRow(ref y, w);
        DrawYouTubePosterStatusRow(ref y, w);
    }

    private void Section(ref float y, float w, string title)
    {
        GUI.Label(new Rect(0, y, w, RowLineHeight), title, _sSection);
        y += RowLineHeight + Padding * 0.3f;
    }

    private void RowHeader(ref float y, float w, string glyph, Color glyphColor, string name, string statusText, Color statusColor)
    {
        Color prev = GUI.color;

        GUI.color = glyphColor;
        GUI.Label(new Rect(0, y, GlyphColumnWidth, RowLineHeight), glyph, _sRowName);

        GUI.color = Color.white;
        GUI.Label(new Rect(GlyphColumnWidth, y, w * 0.34f, RowLineHeight), name, _sRowName);

        GUI.color = statusColor;
        GUI.Label(new Rect(GlyphColumnWidth + w * 0.34f, y, w - GlyphColumnWidth - w * 0.34f, RowLineHeight), statusText, _sStatus);

        GUI.color = prev;
        y += RowLineHeight;
    }

    private void RowButtons(ref float y, float w, (string label, Action onClick) btn1, (string label, Action onClick)? btn2)
    {
        float x = GlyphColumnWidth;

        if (GUI.Button(new Rect(x, y, ButtonWidth, ButtonHeight), btn1.label, _sBtn))
            SafeInvoke(btn1.onClick);
        x += ButtonWidth + Padding * 0.6f;

        if (btn2.HasValue && GUI.Button(new Rect(x, y, ButtonWidth, ButtonHeight), btn2.Value.label, _sBtn))
            SafeInvoke(btn2.Value.onClick);

        y += ButtonHeight + Padding * 0.5f;
    }

    private void RowHint(ref float y, float w, string hint)
    {
        if (string.IsNullOrEmpty(hint)) return;

        GUI.Label(new Rect(GlyphColumnWidth, y, w - GlyphColumnWidth, RowLineHeight * 0.8f), hint, _sHint);
        y += RowLineHeight * 0.8f;
    }

    private static void SafeInvoke(Action action)
    {
        try { action?.Invoke(); }
        catch (Exception e) { Logger.Error(e.ToString(), "StreamSetupGUI"); }
    }

    private void DrawPythonRow(ref float y, float w)
    {
        bool ok = StreamSetupState.PythonStatus == StreamSetupState.PythonState.Ok ||
                  StreamSetupState.PythonStatus == StreamSetupState.PythonState.Bundled;
        bool found = ok || StreamSetupState.PythonStatus == StreamSetupState.PythonState.TooOld;
        string glyph = ok ? OkGlyph : found ? WarnGlyph : MissingGlyph;
        Color color = ok ? ColorOk : found ? ColorWarn : ColorMissing;

        RowHeader(ref y, w, glyph, color, GetString("Setup.Python.Name"), _pythonStatusText, color);

        if (!ok)
        {
            RowButtons(ref y, w, (GetString("Setup.Python.GetButton"), () => Application.OpenURL("https://www.python.org/downloads/")), null);
            RowHint(ref y, w, GetString("Setup.Python.Hint"));
        }

        y += Padding * 1.2f;
    }

    private void DrawGeminiRow(ref float y, float w)
    {
        bool found = StreamSetupState.KeyStatus == StreamSetupState.KeyState.Set;
        Color color = found ? ColorOk : ColorMissing;
        string glyph = found ? OkGlyph : MissingGlyph;
        bool saving = StreamSetupState.KeySaving;

        RowHeader(ref y, w, glyph, color, GetString("Setup.Gemini.Name"), _geminiStatusText, color);

        // 保存中は保存/差替ボタンを描かない (連打での多重ワーカー起動を防ぐ)。
        if (saving)
        {
            RowHint(ref y, w, GetString("Setup.Gemini.Saving"));
        }
        else if (!found)
        {
            RowButtons(ref y, w,
                (GetString("Setup.Gemini.GetButton"), () => Application.OpenURL("https://aistudio.google.com/apikey")),
                (GetString("Setup.Gemini.SaveButton"), OnSaveKeyClicked));
        }
        else
        {
            RowButtons(ref y, w,
                (GetString("Setup.Gemini.ReplaceButton"), OnSaveKeyClicked),
                (GetString("Setup.Gemini.VerifyButton"), OnVerifyKeyClicked));
        }

        if (!saving)
        {
            if (!string.IsNullOrEmpty(_geminiErrorText))
            {
                Color prev = GUI.color;
                GUI.color = ColorMissing;
                RowHint(ref y, w, _geminiErrorText);
                GUI.color = prev;
            }
            else if (StreamSetupState.KeyElsewhereHint)
                RowHint(ref y, w, GetString("Setup.Gemini.ElsewhereHint"));
        }

        if (StreamSetupState.KeyVerifying)
            RowHint(ref y, w, GetString("Setup.Gemini.Verifying"));
        else if (!string.IsNullOrEmpty(_geminiVerifyText))
            RowHint(ref y, w, _geminiVerifyText);

        y += Padding * 1.2f;
    }

    private void DrawVoiceVoxRow(ref float y, float w)
    {
        bool ok = StreamSetupState.VoiceVoxStatus == StreamSetupState.VoiceVoxState.Ok;
        Color color = ok ? ColorOk : ColorNeutral;

        RowHeader(ref y, w, NeutralGlyph, ColorNeutral, GetString("Setup.VoiceVox.Name"), _voiceVoxStatusText, color);
        RowButtons(ref y, w, (GetString("Setup.VoiceVox.GetButton"), () => Application.OpenURL("https://voicevox.hiroshiba.jp/")), null);
        RowHint(ref y, w, GetString("Setup.VoiceVox.Hint"));

        y += Padding * 1.2f;
    }

    private void DrawBottomRow(ref float y, float w)
    {
        bool on = Main.EnableAICommentary?.Value == true;

        if (on)
        {
            GUI.Label(new Rect(0, y, w, RowLineHeight), GetString("Setup.AICommentary.OnLabel"), _sRowName);
            y += RowLineHeight;
        }
        else
        {
            if (GUI.Button(new Rect(0, y, ButtonWidth, ButtonHeight), GetString("Setup.AICommentary.OnButton"), _sBtn))
                SafeInvoke(() => Main.EnableAICommentary.Value = true);
            y += ButtonHeight;
        }

        y += Padding;
    }

    // クリップボードの読取はメインスレッド (OnGUI) で行い、重い保存処理だけを
    // StreamSetupState 側のバックグラウンドスレッドへ渡す。
    private static void OnSaveKeyClicked()
    {
        string clip = GUIUtility.systemCopyBuffer;
        StreamSetupState.SaveKeyFromClipboard(clip);
    }

    private static void OnVerifyKeyClicked() => StreamSetupState.VerifyKey();

    private void DrawGoogleConsoleRow(ref float y, float w)
    {
        bool idSet = StreamSetupYouTubeState.ClientIdSet;
        bool secretSet = StreamSetupYouTubeState.ClientSecretSet;
        bool done = idSet && secretSet;
        Color headerColor = done ? ColorOk : ColorMissing;
        string headerGlyph = done ? OkGlyph : MissingGlyph;

        RowHeader(ref y, w, headerGlyph, headerColor, GetString("Setup.YouTube.Console.Name"), _consoleStatusText, headerColor);

        RowHint(ref y, w, GetString("Setup.YouTube.Console.Step1"));
        RowButtons(ref y, w, (GetString("Setup.YouTube.OpenButton"), () => Application.OpenURL(GoogleConsoleLinks.ProjectCreate)), null);

        RowHint(ref y, w, GetString("Setup.YouTube.Console.Step2"));
        RowButtons(ref y, w, (GetString("Setup.YouTube.OpenButton"), () => Application.OpenURL(GoogleConsoleLinks.EnableApi)), null);

        RowHint(ref y, w, GetString("Setup.YouTube.Console.Step3"));
        Color prevWarn = GUI.color;
        GUI.color = ColorWarn;
        RowHint(ref y, w, GetString("Setup.YouTube.Console.Step3Warning"));
        GUI.color = prevWarn;
        RowButtons(ref y, w,
            (GetString("Setup.YouTube.OpenButton"), () => Application.OpenURL(GoogleConsoleLinks.AuthOverview)),
            (GetString("Setup.YouTube.Console.PublishButton"), () => Application.OpenURL(GoogleConsoleLinks.AuthAudience)));

        RowHint(ref y, w, GetString("Setup.YouTube.Console.Step4"));
        RowButtons(ref y, w, (GetString("Setup.YouTube.OpenButton"), () => Application.OpenURL(GoogleConsoleLinks.ClientCreate)), null);

        y += Padding * 0.4f;

        Color idColor = idSet ? ColorOk : ColorMissing;
        RowHeader(ref y, w, idSet ? OkGlyph : MissingGlyph, idColor, GetString("Setup.YouTube.Console.IdName"),
            idSet ? GetString("Setup.YouTube.Console.Saved") : GetString("Setup.YouTube.Console.NotSaved"), idColor);
        if (!idSet)
            RowButtons(ref y, w, (GetString("Setup.YouTube.Console.SaveIdButton"), OnSaveClientIdClicked), null);

        Color secretColor = secretSet ? ColorOk : ColorMissing;
        RowHeader(ref y, w, secretSet ? OkGlyph : MissingGlyph, secretColor, GetString("Setup.YouTube.Console.SecretName"),
            secretSet ? GetString("Setup.YouTube.Console.Saved") : GetString("Setup.YouTube.Console.NotSaved"), secretColor);
        if (!secretSet)
            RowButtons(ref y, w, (GetString("Setup.YouTube.Console.SaveSecretButton"), OnSaveClientSecretClicked), null);

        if (!string.IsNullOrEmpty(_saveErrorText))
        {
            Color prev = GUI.color;
            GUI.color = ColorMissing;
            RowHint(ref y, w, _saveErrorText);
            GUI.color = prev;
        }

        y += Padding * 1.2f;
    }

    private void DrawYouTubeConnectRow(ref float y, float w)
    {
        bool canConnect = StreamSetupYouTubeState.ClientIdSet && StreamSetupYouTubeState.ClientSecretSet;
        bool connected = StreamSetupYouTubeState.Connect == StreamSetupYouTubeState.ConnectState.Connected;
        Color color = connected ? ColorOk : canConnect ? ColorMissing : ColorNeutral;
        string glyph = connected ? OkGlyph : canConnect ? MissingGlyph : NeutralGlyph;

        RowHeader(ref y, w, glyph, color, GetString("Setup.YouTube.Connect.Name"), _connectStatusText, color);

        if (!canConnect)
        {
            RowHint(ref y, w, GetString("Setup.YouTube.Connect.NeedConsoleHint"));
        }
        else if (StreamSetupYouTubeState.ConnectBusy)
        {
            if (StreamSetupYouTubeState.Connect == StreamSetupYouTubeState.ConnectState.WaitingForUser &&
                !string.IsNullOrEmpty(StreamSetupYouTubeState.DeviceUserCode))
            {
                Color prev = GUI.color;
                GUI.color = new Color(0.72f, 0.90f, 1.00f, 1f);
                GUI.Label(new Rect(GlyphColumnWidth, y, w - GlyphColumnWidth, RowLineHeight * 1.2f), StreamSetupYouTubeState.DeviceUserCode, _sTitleBar);
                GUI.color = prev;
                y += RowLineHeight * 1.2f;

                RowButtons(ref y, w,
                    (GetString("Setup.YouTube.Connect.OpenBrowserButton"), () => Application.OpenURL(StreamSetupYouTubeState.DeviceVerificationUrl)),
                    (GetString("Setup.YouTube.Connect.CopyCodeButton"), OnCopyDeviceCodeClicked));

                RowHint(ref y, w, GetString("Setup.YouTube.Connect.CodeHiddenOnStreamHint"));
            }
            else
            {
                RowHint(ref y, w, GetString("Setup.YouTube.Connect.Requesting"));
            }

            RowButtons(ref y, w, (GetString("Setup.YouTube.Connect.CancelButton"), OnCancelConnectClicked), null);
        }
        else if (connected)
        {
            RowButtons(ref y, w, (GetString("Setup.YouTube.Connect.ReconnectButton"), OnStartConnectClicked), null);
        }
        else
        {
            RowButtons(ref y, w, (GetString("Setup.YouTube.Connect.StartButton"), OnStartConnectClicked), null);

            if (!string.IsNullOrEmpty(_connectErrorText))
            {
                Color prev = GUI.color;
                GUI.color = ColorMissing;
                RowHint(ref y, w, _connectErrorText);
                GUI.color = prev;
            }
        }

        y += Padding * 1.2f;
    }

    private void DrawYouTubeDetectRow(ref float y, float w)
    {
        bool canDetect = StreamSetupYouTubeState.RefreshTokenSet;
        bool found = StreamSetupYouTubeState.Detect == StreamSetupYouTubeState.DetectState.Found;
        Color color = found ? ColorOk : canDetect ? ColorMissing : ColorNeutral;
        string glyph = found ? OkGlyph : canDetect ? MissingGlyph : NeutralGlyph;

        RowHeader(ref y, w, glyph, color, GetString("Setup.YouTube.Detect.Name"), _detectStatusText, color);

        if (!canDetect)
            RowHint(ref y, w, GetString("Setup.YouTube.NeedConnectHint"));
        else if (!StreamSetupYouTubeState.DetectBusy)
            RowButtons(ref y, w, (GetString("Setup.YouTube.Detect.Button"), OnDetectClicked), null);
        else
            RowHint(ref y, w, GetString("Setup.YouTube.Detect.Checking"));

        y += Padding * 1.2f;
    }

    private void DrawYouTubeTestPostRow(ref float y, float w)
    {
        bool canPost = StreamSetupYouTubeState.RefreshTokenSet;

        RowHeader(ref y, w, NeutralGlyph, ColorNeutral, GetString("Setup.YouTube.TestPost.Name"), "", ColorNeutral);

        if (!canPost)
        {
            RowHint(ref y, w, GetString("Setup.YouTube.NeedConnectHint"));
        }
        else if (StreamSetupYouTubeState.TestPostBusy)
        {
            RowHint(ref y, w, GetString("Setup.YouTube.TestPost.Posting"));
        }
        else if (StreamSetupYouTubeState.TestPostOnCooldown)
        {
            RowHint(ref y, w, string.Format(GetString("Setup.YouTube.TestPost.CooldownHint"), StreamSetupYouTubeState.TestPostCooldownRemaining()));
        }
        else
        {
            RowButtons(ref y, w, (GetString("Setup.YouTube.TestPost.Button"), OnTestPostClicked), null);
        }

        if (!string.IsNullOrEmpty(_testPostStatusText))
        {
            Color prev = GUI.color;
            GUI.color = StreamSetupYouTubeState.TestPost == StreamSetupYouTubeState.TestPostState.Success ? ColorOk : ColorMissing;
            RowHint(ref y, w, _testPostStatusText);
            GUI.color = prev;
        }

        y += Padding * 1.2f;
    }

    private void DrawYouTubePosterStatusRow(ref float y, float w)
    {
        bool on = YouTubePostOptions.Enabled?.GetBool() == true;
        Color prev = GUI.color;
        GUI.color = on ? ColorOk : ColorNeutral;
        GUI.Label(new Rect(0, y, w, RowLineHeight), _posterStatusText, _sRowName);
        GUI.color = prev;
        y += RowLineHeight;

        if (!on)
        {
            if (GUI.Button(new Rect(0, y, ButtonWidth, ButtonHeight), GetString("Setup.YouTube.Poster.OnButton"), _sBtn))
                SafeInvoke(OnEnablePosterClicked);
            y += ButtonHeight;
        }

        y += Padding;
    }

    private static void OnSaveClientIdClicked()
    {
        string clip = GUIUtility.systemCopyBuffer;
        StreamSetupYouTubeState.SaveClientIdFromClipboard(clip);
    }

    private static void OnSaveClientSecretClicked()
    {
        string clip = GUIUtility.systemCopyBuffer;
        StreamSetupYouTubeState.SaveClientSecretFromClipboard(clip);
    }

    private static void OnStartConnectClicked() => StreamSetupYouTubeState.StartConnect();

    private static void OnCancelConnectClicked() => StreamSetupYouTubeState.CancelConnect();

    // 表示専用の user_code をクリップボードへ書き出すだけ (秘密ではないので読取側の検証は不要)。
    private static void OnCopyDeviceCodeClicked()
    {
        string code = StreamSetupYouTubeState.DeviceUserCode;
        if (!string.IsNullOrEmpty(code)) GUIUtility.systemCopyBuffer = code;
    }

    private static void OnDetectClicked() => StreamSetupYouTubeState.DetectStream();

    private static void OnTestPostClicked()
    {
        // Translator はメインスレッド専用。クリックハンドラは OnGUI (メインスレッド) から呼ばれるので
        // ここで文言解決してから StreamSetupYouTubeState へ渡す (バックグラウンド側は資材化済み文字列だけ扱う)。
        string text = GetString("Setup.YouTube.TestPostText");
        StreamSetupYouTubeState.RunTestPost(text);
    }

    private static void OnEnablePosterClicked()
    {
        // メインメニューにはロビー同期先が無いため StreamerMode.SetOn と同じく doSync:false で書く。
        YouTubePostOptions.Enabled?.SetValue(1, doSync: false);
        StreamSetupState.Dirty = true;
    }
}
