using System;
using System.Collections.Generic;
using AmongUs.GameOptions;
using EndKnot.Gamemodes;
using EndKnot.Modules.Audience;
using UnityEngine;

namespace EndKnot.Modules.StreamOverlay;

// 視聴者に「!コマンドで参加できる」ことを配信画面へ周知するコンパクトな回転バナー。
// LobbyCodeBubble を踏襲した IMGUI + ドラッグ移動パターン。100% host 画面ローカル（RPC / NetObject 不使用）。
// ホスト専用 MOD なので描画されるのはホスト = 配信画面。Translator.GetString がホストの言語設定を見るため、
// 表示言語は自動でゲーム内言語に一致する（英語クライアントなら英語、日本語なら日本語）。
// 有効な干渉コマンドと価格を数秒ごとに1件ずつ切り替えて表示する。
public class AudienceInfoBubble : MonoBehaviour
{
    public static AudienceInfoBubble Instance;

    private const float RotateSeconds = 4f;

    // ---- 永続する状態 (シーンを跨いで保持) ----
    private Vector2 _bubblePos;
    private bool _posInitialized;

    // ---- ドラッグ ----
    private bool _dragging;
    private Vector2 _dragOffset;

    // ---- 回転スライドのキャッシュ (毎 OnGUI での List/文字列生成を避け GC churn を抑える) ----
    // OnGUI は 1 フレームに複数回 (Layout/Repaint/入力イベント) 呼ばれるため、スライド一覧の再構築は
    // 回転 tick が変わったときだけに限定する。
    private List<string> _cachedSlides;
    private int _lastTick = -1;
    private int _lastSlideIndex = -1;
    private string _cachedLine = "";

    // ---- スタイル ----
    private GUIStyle _headerStyle, _lineStyle, _bgStyle, _borderStyle;
    private Texture2D _bgTex, _borderTex;
    private float _builtScale = -1f;

    private static float Scale => Screen.width / 1080f * 0.5f * UserScale;
    private static float UserScale => (AudienceOptions.InfoOverlayScale?.GetInt() ?? 150) / 100f;

    private void Awake()
    {
        Instance = this;
    }

    private static bool ShouldShow()
    {
        if (!HudManager.InstanceExists) return false;
        if (!AmongUsClient.Instance || !AmongUsClient.Instance.AmHost) return false;
        if (AudienceOptions.Enabled == null || !AudienceOptions.Enabled.GetBool()) return false;
        if (AudienceOptions.ShowInfoOverlay == null || !AudienceOptions.ShowInfoOverlay.GetBool()) return false;
        // ロビー(予告)とゲーム中(干渉が効く)の両方で表示。会議中も表示したままにする。
        if (!GameStates.IsLobby && !GameStates.InGame) return false;
        return true;
    }

    private void OnGUI()
    {
        try
        {
            if (!ShouldShow()) return;

            EnsureStyles();
            EnsureDefaultPos();
            UpdateSlide();

            HandleDrag();
            DrawBubble();
        }
        catch (Exception e) { Utils.ThrowException(e); }
    }

    private float BubbleWidth => 280f * Scale;
    private float HeaderHeight => 24f * Scale;
    private float LineHeight => 34f * Scale;
    private float BubbleHeight => HeaderHeight + LineHeight;

    private Rect BubbleRect => new(_bubblePos.x, _bubblePos.y, BubbleWidth, BubbleHeight);

    private void EnsureDefaultPos()
    {
        if (_posInitialized) return;
        _posInitialized = true;
        // 右上寄り。LobbyCodeBubble(右端 34%) や YouTubeChatBubble(左端 34%) と被らない位置。
        _bubblePos = new Vector2(Screen.width - BubbleWidth - 14f * Scale, Screen.height * 0.12f);
    }

    // 有効な干渉コマンドを列挙し、経過時間に応じて表示スライドを1件選ぶ。
    // スライド一覧の再構築は回転 tick が変わったときだけ(既定4秒に1回)。表示行の差し替えも
    // インデックスが変わったときだけ。これで毎 OnGUI での List/文字列生成をゼロにする。
    private void UpdateSlide()
    {
        int tick = (int)(Time.realtimeSinceStartup / RotateSeconds);
        if (tick != _lastTick || _cachedSlides == null)
        {
            _lastTick = tick;
            _cachedSlides = BuildSlides();
        }

        if (_cachedSlides.Count == 0)
        {
            _cachedLine = "";
            _lastSlideIndex = -1;
            return;
        }

        int index = tick % _cachedSlides.Count;
        if (index == _lastSlideIndex && _cachedLine.Length > 0) return;

        _lastSlideIndex = index;
        _cachedLine = _cachedSlides[index];
    }

    private static List<string> BuildSlides()
    {
        var slides = new List<string>();

        // 先頭スライド: ポイントの稼ぎ方を案内する。
        slides.Add(Translator.GetString("AudienceInfoOverlayEarn"));

        AddCommand(slides, AudienceOptions.BlackoutEnabled, "AudienceCmdBlackout", AudienceOptions.BlackoutPrice);
        AddCommand(slides, AudienceOptions.ReactorEnabled, "AudienceCmdReactor", AudienceOptions.ReactorPrice);
        AddCommand(slides, AudienceOptions.CommsEnabled, "AudienceCmdComms", AudienceOptions.CommsPrice);
        AddCommand(slides, AudienceOptions.DoorsEnabled, "AudienceCmdDoors", AudienceOptions.DoorsPrice);
        AddCommand(slides, AudienceOptions.CurseEnabled, "AudienceCmdCurse", AudienceOptions.CursePrice);
        AddCommand(slides, AudienceOptions.BlessEnabled, "AudienceCmdBless", AudienceOptions.BlessPrice);
        AddCommand(slides, AudienceOptions.EarthquakeEnabled, "AudienceCmdEarthquake", AudienceOptions.EarthquakePrice);
        AddCommand(slides, AudienceOptions.VoiceEnabled, "AudienceCmdVoice", AudienceOptions.VoicePrice);
        AddCommand(slides, AudienceOptions.FakeBodyEnabled, "AudienceCmdFakeBody", AudienceOptions.FakeBodyPrice);

        // 隕石は災害系モードでしか実行できない(それ以外では実行失敗しポイント返却されるだけ)ので、
        // 実際に効くときだけ案内に含める。
        bool meteorPlayable = Options.CurrentGameMode == CustomGameMode.NaturalDisasters || Options.IntegrateNaturalDisasters.GetBool();
        if (meteorPlayable)
            AddCommand(slides, AudienceOptions.MeteorEnabled, "AudienceCmdMeteor", AudienceOptions.MeteorPrice);

        return slides;
    }

    private static void AddCommand(List<string> slides, OptionItem enabled, string triggerKey, OptionItem price)
    {
        if (enabled == null || !enabled.GetBool()) return;
        slides.Add($"{Translator.GetString(triggerKey)}  →  {price.GetInt()}pt");
    }

    // バブルをドラッグで移動するだけ。gameplay へイベントが漏れないよう e.Use()。
    private void HandleDrag()
    {
        Event e = Event.current;
        switch (e.type)
        {
            case EventType.MouseDown when BubbleRect.Contains(e.mousePosition):
                _dragging = true;
                _dragOffset = e.mousePosition - _bubblePos;
                e.Use();
                break;
            case EventType.MouseDrag when _dragging:
                Vector2 np = e.mousePosition - _dragOffset;
                _bubblePos = new Vector2(
                    Mathf.Clamp(np.x, 0f, Screen.width - BubbleWidth),
                    Mathf.Clamp(np.y, 0f, Screen.height - BubbleHeight));
                e.Use();
                break;
            case EventType.MouseUp when _dragging:
                _dragging = false;
                e.Use();
                break;
        }
    }

    private void DrawBubble()
    {
        if (_cachedLine.Length == 0) return;

        // 縁取り: マップ背景色に関わらず輪郭が潰れないよう、白い縁を一回り大きく敷いてから本体を重ねる。
        float border = 3f * Scale;
        Rect outer = new(BubbleRect.x - border, BubbleRect.y - border, BubbleRect.width + border * 2f, BubbleRect.height + border * 2f);
        GUI.Box(outer, GUIContent.none, _borderStyle);
        GUI.Box(BubbleRect, GUIContent.none, _bgStyle);

        // 上段に固定ヘッダ、下段に回転する1コマンド行。
        Rect headerRect = new(BubbleRect.x, BubbleRect.y, BubbleWidth, HeaderHeight);
        Rect lineRect = new(BubbleRect.x, BubbleRect.y + HeaderHeight, BubbleWidth, LineHeight);
        GUI.Label(headerRect, Translator.GetString("AudienceInfoOverlayHeader"), _headerStyle);
        GUI.Label(lineRect, _cachedLine, _lineStyle);
    }

    private void EnsureStyles()
    {
        if (_headerStyle != null && Math.Abs(_builtScale - Scale) < 0.01f) return;
        _builtScale = Scale;

        // 鮮やかな赤紫。LobbyCodeBubble(青紫)と色で区別しつつ、黄土色/茶系マップ背景でも埋もれない高彩度・高不透明度で。
        _bgTex = SolidTex(new Color(0.72f, 0.12f, 0.42f, 0.97f));
        _borderTex = SolidTex(Color.white);

        int lineFontSize = Mathf.Max(14, Mathf.RoundToInt(22f * Scale));
        int headerFontSize = Mathf.Max(11, Mathf.RoundToInt(16f * Scale));

        _bgStyle = new GUIStyle();
        _bgStyle.normal.background = _bgTex;

        _headerStyle = new GUIStyle
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = headerFontSize,
            fontStyle = FontStyle.Bold,
            richText = false
        };
        _headerStyle.normal.textColor = Color.white;

        _lineStyle = new GUIStyle
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = lineFontSize,
            fontStyle = FontStyle.Bold,
            richText = false
        };
        _lineStyle.normal.textColor = Color.white;

        _borderStyle = new GUIStyle();
        _borderStyle.normal.background = _borderTex;
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
