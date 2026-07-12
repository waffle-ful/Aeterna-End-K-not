using System;
using InnerNet;
using UnityEngine;

namespace EndKnot.Modules.StreamOverlay;

// 配信画面に常時ロビーコードを表示するドラッグ可能なバブル。
// YouTubeChatBubble (Modules/YouTubeChat/YouTubeChatBubble.cs) の
// IMGUI + ドラッグ移動パターンを踏襲した最小構成（パネル展開・クリックトグルは無し）。
// 100% host 画面ローカル（RPC / NetObject 不使用）。ロビー中のみ表示。
public class LobbyCodeBubble : MonoBehaviour
{
    public static LobbyCodeBubble Instance;

    // ---- 永続する状態 (シーンを跨いで保持) ----
    private Vector2 _bubblePos;
    private bool _posInitialized;

    // ---- ドラッグ ----
    private bool _dragging;
    private Vector2 _dragOffset;

    // ---- スタイル ----
    private GUIStyle _bubbleStyle, _labelStyle, _bgStyle, _borderStyle;
    private Texture2D _bgTex, _borderTex;
    private float _builtScale = -1f;

    private static float Scale => Screen.width / 1080f * 0.5f * UserScale;
    private static float UserScale => (LobbyCodeBubbleOptions.Scale?.GetInt() ?? 150) / 100f;

    private void Awake()
    {
        Instance = this;
    }

    private static bool ShouldShow()
    {
        if (!HudManager.InstanceExists) return false;
        if (!AmongUsClient.Instance || !AmongUsClient.Instance.AmHost) return false;
        if (LobbyCodeBubbleOptions.Enabled == null || !LobbyCodeBubbleOptions.Enabled.GetBool()) return false;
        if (!GameStates.IsLobby) return false;
        return true;
    }

    private void OnGUI()
    {
        try
        {
            if (!ShouldShow()) return;

            string code = GameCode.IntToGameName(AmongUsClient.Instance.GameId);
            if (string.IsNullOrEmpty(code)) return;

            EnsureStyles();
            EnsureDefaultPos();

            HandleDrag();
            DrawBubble(code);
        }
        catch (Exception e) { Utils.ThrowException(e); }
    }

    private float BubbleWidth => 130f * Scale;
    private float LabelHeight => 18f * Scale;
    private float CodeHeight => 46f * Scale;
    private float BubbleHeight => LabelHeight + CodeHeight;

    private Rect BubbleRect => new(_bubblePos.x, _bubblePos.y, BubbleWidth, BubbleHeight);

    private void EnsureDefaultPos()
    {
        if (_posInitialized) return;
        _posInitialized = true;
        // 右端の中ほど。YouTubeChatBubble (左端 34%) やClientControlGUIトグル(下部30%)と被らない位置。
        _bubblePos = new Vector2(Screen.width - BubbleWidth - 14f * Scale, Screen.height * 0.34f);
    }

    // バブルをドラッグで移動するだけ (クリックトグルは無し)。gameplay へイベントが漏れないよう e.Use()。
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

    private void DrawBubble(string code)
    {
        // 縁取り: マップ背景色に関わらず輪郭が潰れないよう、白い縁を一回り大きく敷いてから本体を重ねる
        float border = 3f * Scale;
        Rect outer = new(BubbleRect.x - border, BubbleRect.y - border, BubbleRect.width + border * 2f, BubbleRect.height + border * 2f);
        GUI.Box(outer, GUIContent.none, _borderStyle);
        GUI.Box(BubbleRect, GUIContent.none, _bgStyle);

        // 横に伸ばさず縦積み: 上段に小さく「ルームコード」ラベル、下段に大きくコード本体。
        Rect labelRect = new(BubbleRect.x, BubbleRect.y, BubbleWidth, LabelHeight);
        Rect codeRect = new(BubbleRect.x, BubbleRect.y + LabelHeight, BubbleWidth, CodeHeight);
        GUI.Label(labelRect, Translator.GetString("LobbyCodeBubbleLabel"), _labelStyle);
        GUI.Label(codeRect, code, _bubbleStyle);
    }

    private void EnsureStyles()
    {
        if (_bubbleStyle != null && Math.Abs(_builtScale - Scale) < 0.01f) return;
        _builtScale = Scale;

        // 鮮やかな青紫。The Skeld 等の黄土色/茶系マップ背景でも埋もれないよう高彩度・高不透明度で。
        _bgTex = SolidTex(new Color(0.20f, 0.15f, 0.85f, 0.97f));
        _borderTex = SolidTex(Color.white);

        int fontSize = Mathf.Max(16, Mathf.RoundToInt(28f * Scale));
        int labelFontSize = Mathf.Max(9, Mathf.RoundToInt(13f * Scale));

        _bgStyle = new GUIStyle();
        _bgStyle.normal.background = _bgTex;

        _bubbleStyle = new GUIStyle
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = fontSize,
            fontStyle = FontStyle.Bold,
            richText = false
        };
        _bubbleStyle.normal.textColor = Color.white;

        // ラベル行: 「ルームコード」等の小さな見出し。本体より薄い白でコード側を食わない。
        _labelStyle = new GUIStyle
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = labelFontSize,
            fontStyle = FontStyle.Normal,
            richText = false
        };
        _labelStyle.normal.textColor = new Color(1f, 1f, 1f, 0.75f);

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
