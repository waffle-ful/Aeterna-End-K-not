using System;
using InnerNet;
using UnityEngine;

namespace EndKnot.Modules.StreamOverlay;

// 配信画面の上部に、参加コードの看板を一定間隔で出す。
// 看板は右から滑り込んで画面の中央で止まり、コードのタイルが 1 枚ずつ発着表示板のようにめくれて確定する。
// しばらく止まったあと左へ加速して抜ける。出ている看板は常に 1 枚で、中身はモッドが組み立てた案内だけ。
// 100% host 画面ローカル (RPC / NetObject 不使用)。
public class LobbyCodeTicker : MonoBehaviour
{
    // 起動直後は少し待ってから最初の 1 枚を出す (ロビーに入った直後の画面の切り替わりと重ねない)
    private const float FirstDelay = 6f;

    // 滑り込み / 抜けの秒数。間の時間だけ止まる
    private const float SlideSeconds = 0.7f;

    // タイルがめくれ始める時刻 (1 枚目) と、1 枚ごとの遅れ・めくれている時間・文字の切り替わり間隔
    private const float FlipStart = 0.45f;
    private const float FlipStagger = 0.11f;
    private const float FlipSeconds = 0.4f;
    private const float FlipStep = 0.05f;

    private const string FlipChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    private GUIStyle _tileStyle, _labelStyle, _hintStyle, _fillStyle, _frameStyle, _tileBgStyle, _hintBgStyle;
    private Texture2D _fillTex, _frameTex, _tileTex, _hintTex;
    private float _builtScale = -1f;

    private int _codeGameId;
    private GUIContent[] _codeChars;
    private GUIContent[] _flipChars;
    private string _label, _hint;
    private GUIContent _labelContent, _hintContent;
    private bool _audienceOn;
    private float _hintWidth;

    // 今の 1 枚が出た時刻。負なら待機中
    private float _runStart = -1f;
    private float _nextStart = -1f;

    private static float Scale => Il2Direct.ScreenWidth / 1080f * 0.5f * UserScale;
    private static float UserScale => (LobbyCodeBubbleOptions.TickerScale?.GetInt() ?? 100) / 100f;

    private void Awake()
    {
        ImguiNoLayout.Apply(this);
    }

    private static bool ShouldShow()
    {
        if (!HudManager.InstanceExists) return false;
        AmongUsClient client = AmongUsClient.Instance;
        if (!Il2Direct.Alive(client) || !client.AmHost) return false;
        if (LobbyCodeBubbleOptions.TickerEnabled == null || !LobbyCodeBubbleOptions.TickerEnabled.GetBool()) return false;
        return GameStates.IsLobby;
    }

    private void OnGUI()
    {
        try
        {
            if (Event.current.type != EventType.Repaint) return;

            if (!ShouldShow())
            {
                // 隠れている間に出ていた 1 枚は捨て、戻ったら少し待ってから出し直す
                _runStart = -1f;
                _nextStart = -1f;
                return;
            }

            float now = Time.realtimeSinceStartup;
            if (_nextStart < 0f) _nextStart = now + FirstDelay;

            if (_runStart < 0f)
            {
                if (now < _nextStart) return;
                if (!EnsureContent()) return;
                _runStart = now;
            }

            float shown = LobbyCodeBubbleOptions.TickerRunSeconds?.GetFloat() ?? 8f;
            if (shown < SlideSeconds * 2f + 1f) shown = SlideSeconds * 2f + 1f;

            float t = now - _runStart;
            if (t >= shown)
            {
                _runStart = -1f;
                _nextStart = now + (LobbyCodeBubbleOptions.TickerInterval?.GetFloat() ?? 45f);
                return;
            }

            EnsureStyles();
            Draw(t, shown);
        }
        catch (Exception e) { Utils.ThrowException(e); }
    }

    private void Draw(float t, float shown)
    {
        float s = Scale;
        int n = _codeChars.Length;
        float tileW = 62f * s, tileH = 86f * s, gap = 10f * s, pad = 16f * s, labelH = 34f * s, border = 4f * s;
        float plateW = pad * 2f + n * tileW + (n - 1) * gap;
        float plateH = pad + labelH + tileH + pad;

        // 滑り込みは減速 (3 乗の ease-out)、抜けは加速 (3 乗の ease-in)
        float sw = Il2Direct.ScreenWidth;
        float rest = (sw - plateW) * 0.5f;
        float x;
        if (t < SlideSeconds)
        {
            float u = 1f - t / SlideSeconds;
            x = rest + (sw - rest) * u * u * u;
        }
        else if (t > shown - SlideSeconds)
        {
            float u = (t - (shown - SlideSeconds)) / SlideSeconds;
            x = rest - (rest + plateW) * u * u * u;
        }
        else x = rest;

        float y = Il2Direct.ScreenHeight * 0.08f;

        GUI.Box(new Rect(x - border, y - border, plateW + border * 2f, plateH + border * 2f), GUIContent.none, _frameStyle);
        GUI.Box(new Rect(x, y, plateW, plateH), GUIContent.none, _fillStyle);
        GUI.Label(new Rect(x, y + pad * 0.5f, plateW, labelH), _labelContent, _labelStyle);

        float ty = y + pad * 0.5f + labelH + pad * 0.5f;

        for (int i = 0; i < n; i++)
        {
            float tx = x + pad + i * (tileW + gap);
            float since = t - FlipStart - i * FlipStagger;

            if (since < 0f)
            {
                // まだめくれていないタイルは無地
                GUI.Box(new Rect(tx, ty, tileW, tileH), GUIContent.none, _tileBgStyle);
                continue;
            }

            GUIContent face = _codeChars[i];
            float h = tileH;

            if (since < FlipSeconds)
            {
                // めくれている間は文字が次々入れ替わり、板が縦に潰れては戻る (時刻から決めるので同じフレームの再描画でもちらつかない)
                int step = (int)(since / FlipStep);
                face = _flipChars[((step * 7 + i * 5 + _codeGameId) & int.MaxValue) % _flipChars.Length];
                float phase = since / FlipStep - step;
                h = tileH * (0.35f + 0.65f * Math.Abs(1f - 2f * phase));
            }

            float yOff = (tileH - h) * 0.5f;
            Rect r = new(tx, ty + yOff, tileW, h);
            GUI.Box(r, GUIContent.none, _tileBgStyle);
            GUI.Label(r, face, _tileStyle);
        }

        // 視聴者干渉の案内は看板の下の帯に
        if (_audienceOn && _hintContent != null)
        {
            float hw = FxMath.Max(_hintWidth + pad * 2f, plateW);
            float hx = x + (plateW - hw) * 0.5f;
            float hh = 38f * s;
            GUI.Box(new Rect(hx, y + plateH + border, hw, hh), GUIContent.none, _hintBgStyle);
            GUI.Label(new Rect(hx, y + plateH + border, hw, hh), _hintContent, _hintStyle);
        }
    }

    // 1 枚出し始める時だけ呼ぶ。コード・翻訳・干渉の有無のどれかが変わった時だけ包み直す
    private bool EnsureContent()
    {
        int gameId = AmongUsClient.Instance.GameId;
        if (gameId != _codeGameId || _codeChars == null)
        {
            string code = GameCode.IntToGameName(gameId);
            if (string.IsNullOrEmpty(code)) return false;
            _codeGameId = gameId;
            _codeChars = new GUIContent[code.Length];
            for (int i = 0; i < code.Length; i++) _codeChars[i] = new GUIContent(code[i].ToString());
        }

        if (_flipChars == null)
        {
            _flipChars = new GUIContent[FlipChars.Length];
            for (int i = 0; i < FlipChars.Length; i++) _flipChars[i] = new GUIContent(FlipChars[i].ToString());
        }

        string label = Translator.GetString("LobbyCodeTickerLabel");
        if (_labelContent == null || !string.Equals(label, _label, StringComparison.Ordinal))
        {
            _label = label;
            _labelContent = new GUIContent(label);
        }

        _audienceOn = Audience.AudienceOptions.Enabled != null && Audience.AudienceOptions.Enabled.GetBool();
        string hint = Translator.GetString("LobbyCodeTickerAudience");
        if (_hintContent == null || !string.Equals(hint, _hint, StringComparison.Ordinal))
        {
            _hint = hint;
            _hintContent = new GUIContent(hint);
            _builtScale = -1f;
        }

        return true;
    }

    private void EnsureStyles()
    {
        if (_tileStyle != null && Math.Abs(_builtScale - Scale) < 0.01f) return;
        _builtScale = Scale;
        float s = Scale;

        // 濃紺の板に黄色の縁。黄土色や白い床の上でも輪郭が残る
        _fillTex ??= SolidTex(new Color(0.07f, 0.09f, 0.2f, 0.95f));
        _frameTex ??= SolidTex(new Color(1f, 0.8f, 0.18f, 1f));
        _tileTex ??= SolidTex(new Color(0.01f, 0.02f, 0.06f, 1f));
        _hintTex ??= SolidTex(new Color(0f, 0f, 0f, 0.75f));

        _fillStyle = new GUIStyle();
        _fillStyle.normal.background = _fillTex;
        _frameStyle = new GUIStyle();
        _frameStyle.normal.background = _frameTex;
        _tileBgStyle = new GUIStyle();
        _tileBgStyle.normal.background = _tileTex;
        _hintBgStyle = new GUIStyle();
        _hintBgStyle.normal.background = _hintTex;

        _tileStyle = new GUIStyle
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = Math.Max(20, (int)(64f * s + 0.5f)),
            fontStyle = FontStyle.Bold,
            richText = false,
            clipping = TextClipping.Clip
        };
        _tileStyle.normal.textColor = Color.white;

        _labelStyle = new GUIStyle
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = Math.Max(12, (int)(25f * s + 0.5f)),
            fontStyle = FontStyle.Bold,
            richText = false
        };
        _labelStyle.normal.textColor = new Color(1f, 0.8f, 0.18f, 1f);

        _hintStyle = new GUIStyle
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = Math.Max(12, (int)(25f * s + 0.5f)),
            fontStyle = FontStyle.Bold,
            richText = false,
            wordWrap = false,
            clipping = TextClipping.Overflow
        };
        _hintStyle.normal.textColor = Color.white;

        _hintWidth = _hintContent != null ? _hintStyle.CalcSize(_hintContent).x : 0f;
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
