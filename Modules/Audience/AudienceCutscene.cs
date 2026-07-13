using System;
using UnityEngine;

namespace EndKnot.Modules.Audience;

// 干渉発動時にホスト(配信者)画面へオリジナルのフルスクリーン演出をローカル再生する。
// バニラ会議演出の流用ではなく専用デザイン (ストロボフラッシュ+警告帯+タイトルスラム) で、
// 本物の会議招集と見分けが付くようにしてある。IMGUI 描画のみ・ネットワーク送信ゼロ。
// LobbyCodeBubble / AudienceInfoBubble と同じく Main.cs で AddComponent 登録される。
public class AudienceCutscene : MonoBehaviour
{
    private const float Duration = 3.4f;

    // ---- 再生中の表示状態 (Play で一括構築し OnGUI では描くだけ — 毎フレ文字列生成禁止) ----
    private static bool _active;
    private static float _startTime;
    private static string _title = "";
    private static string _kindLine = "";
    private static string _byLine = "";
    private static Color _theme = Color.red;

    // ---- スタイル ----
    private GUIStyle _titleStyle, _kindStyle, _byStyle, _fillStyle;
    private Texture2D _fillTex;
    private int _builtHeight = -1;

    // AudienceManager.TryExecute の成功直後 (InGame && !IsMeeting 保証下、メインスレッド) からのみ呼ばれる。
    // kindKey は "Blackout" 等の InterventionKind 名で、lang キー "Audience.Cutscene.<kindKey>" に対応する。
    public static void Play(string kindKey, string author, byte targetId)
    {
        if (AudienceOptions.InterventionCutscene == null || !AudienceOptions.InterventionCutscene.GetBool()) return;

        string kindName = Translator.GetString($"Audience.Cutscene.{kindKey}");

        // 呪い/祝福は対象プレイヤー名を干渉名に添える。
        if (targetId != byte.MaxValue && Main.AllPlayerNames.TryGetValue(targetId, out string targetName))
            kindName = $"{kindName} → {Sanitize(targetName)}";

        _title = Translator.GetString("Audience.Cutscene.Title");
        _kindLine = kindName;
        // 視聴者名は任意文字列なのでタグ/改行を無効化してから埋め込む (YouTubeChatOverlay.Sanitize と同一手法)。
        _byLine = string.Format(Translator.GetString("Audience.Cutscene.By"), Sanitize(author));
        _theme = ThemeColor(kindKey);
        _startTime = Time.realtimeSinceStartup;
        _active = true;

        // 干渉専用のショック効果音をホストローカル再生する (本物の会議招集と音でも区別が付くように)。
        // 既存のカスタム効果音基盤 (Modules/CustomSounds.cs) に相乗り — 埋込リソース
        // EndKnot.Resources.Sounds.AudienceShock.ogg を読んで SoundManager で鳴らす (送信ゼロ)。
        CustomSoundsManager.Play("AudienceShock");
    }

    // ---- ホストローカルのカメラシェイク ----
    // バニラ FollowerCamera.ShakeScreen は設定の「画面シェイク」OFF だと OverrideScreenShakeEnabled を
    // 立てても揺れない (2026-07-13 実機確認)。設定に依存せず揺らすため、LateUpdate で毎フレーム
    // カメラ位置に減衰ジッターを加算する自前実装。FollowerCamera.Update が毎フレーム位置を
    // ターゲット基準で再設定するので、ここでの加算はフレームを跨いで蓄積しない (送信ゼロ)。
    private static float _shakeStart = -1f;
    private static float _shakeDuration;
    private static float _shakeAmplitude;

    public static void ShakeCamera(float duration, float amplitude)
    {
        _shakeStart = Time.time;
        _shakeDuration = duration;
        _shakeAmplitude = amplitude;
    }

    public static void StopCameraShake() => _shakeStart = -1f;

    private void LateUpdate()
    {
        if (_shakeStart < 0f) return;

        try
        {
            float t = Time.time - _shakeStart;

            // 会議開始・ゲーム終了で即打ち切り (会議 UI の裏でカメラを揺らし続けない)。
            if (t >= _shakeDuration || !GameStates.InGame || GameStates.IsMeeting || ExileController.Instance || !HudManager.InstanceExists)
            {
                _shakeStart = -1f;
                return;
            }

            FollowerCamera cam = HudManager.Instance.PlayerCam;
            if (!cam) return;

            // 序盤は全力、残り時間に応じて振幅を減衰させる (最後は自然に収束)。
            float decay = Mathf.Clamp01((_shakeDuration - t) / _shakeDuration);
            float amp = _shakeAmplitude * ((0.35f + (0.65f * decay)) * Mathf.Clamp01(t / 0.1f));

            // 非整合な2周波の sin/cos で疑似ランダムな揺れにする (タイトルスラムのジッターと同手法)。
            Vector3 pos = cam.transform.position;
            pos.x += Mathf.Sin(t * 39f) * amp;
            pos.y += Mathf.Cos(t * 31f) * amp;
            cam.transform.position = pos;
        }
        catch (Exception e)
        {
            _shakeStart = -1f;
            Utils.ThrowException(e);
        }
    }

    private static string Sanitize(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        return s.Replace("<", "＜").Replace("\n", " ").Replace("\r", " ");
    }

    private static Color ThemeColor(string kindKey) => kindKey switch
    {
        "Blackout" => new Color(0.25f, 0.55f, 1f),
        "Reactor" => new Color(1f, 0.15f, 0.15f),
        "Comms" => new Color(0f, 0.8f, 0.8f),
        "Doors" => new Color(1f, 0.55f, 0f),
        "Curse" => new Color(0.65f, 0.2f, 0.95f),
        "Bless" => new Color(1f, 0.8f, 0.1f),
        "Meteor" => new Color(1f, 0.3f, 0.05f),
        "Earthquake" => new Color(0.75f, 0.5f, 0.15f),
        "Voice" => new Color(1f, 0.85f, 0.35f),
        "FakeBody" => new Color(0.55f, 0.6f, 0.68f),
        _ => new Color(1f, 0.2f, 0.2f)
    };

    private void OnGUI()
    {
        try
        {
            if (!_active) return;

            // 本物の会議が始まった / ゲームが終わったら即座に演出を打ち切る (会議画面の上に被せない)。
            if (!GameStates.InGame || GameStates.IsMeeting)
            {
                _active = false;
                return;
            }

            float t = Time.realtimeSinceStartup - _startTime;
            if (t >= Duration)
            {
                _active = false;
                return;
            }

            EnsureStyles();
            Draw(t);
        }
        catch (Exception e)
        {
            // OnGUI は毎フレーム複数回呼ばれるため、失敗したら演出を打ち切って例外スパムを防ぐ (実測: 放置すると1回の再生で1700行超)。
            _active = false;
            Utils.ThrowException(e);
        }
    }

    private void Draw(float t)
    {
        float w = Screen.width, h = Screen.height;
        // 終盤 0.5 秒で全要素をまとめてフェードアウトさせる係数。
        float globalFade = Mathf.Clamp01((Duration - t) / 0.5f);

        // ① 冒頭ストロボ: 白→テーマ色→白 と 3 回瞬いて視線を強制的に奪う。
        if (t < 0.45f)
        {
            int strobe = (int)(t / 0.15f);
            float strobeAlpha = (1f - t / 0.45f) * 0.85f;
            Color flash = strobe == 1 ? _theme : Color.white;
            FillRect(new Rect(0, 0, w, h), new Color(flash.r, flash.g, flash.b, strobeAlpha));
        }

        // ② 暗幕: 素早く立ち上がり、終盤にフェードアウト。
        float dim = Mathf.Clamp01((t - 0.1f) / 0.3f) * 0.68f * globalFade;
        FillRect(new Rect(0, 0, w, h), new Color(0f, 0f, 0f, dim));

        // ③ 上下のハザード帯: 左右からスライドイン。テーマ色地に暗いストライプ。
        float barH = h * 0.085f;
        float slide = EaseOut(Mathf.Clamp01((t - 0.15f) / 0.35f));
        DrawHazardBar(new Rect(-w + (w * slide), 0, w, barH), globalFade);
        DrawHazardBar(new Rect(w - (w * slide), h - barH, w, barH), globalFade);

        // ④ タイトル: 画面上から叩き付けるように落下して静止 + 減衰シェイク。
        // (GUI.matrix / GUIUtility.ScaleAroundPivot によるスケール演出は IL2CPP で unstripping 失敗するため使用禁止 — 実機ログ 2026-07-13 で確認)
        if (t >= 0.2f)
        {
            float ts = Mathf.Clamp01((t - 0.2f) / 0.35f);
            float drop = (1f - EaseOut(ts)) * -h * 0.5f;
            float shakeAmp = Mathf.Max(0f, 1.1f - t) * 14f;
            var jitter = new Vector2(Mathf.Sin(t * 61f) * shakeAmp, Mathf.Cos(t * 47f) * shakeAmp);

            var titleRect = new Rect(jitter.x, h * 0.38f - (h * 0.09f) + jitter.y + drop, w, h * 0.18f);
            DrawOutlinedLabel(titleRect, _title, _titleStyle, _theme, Mathf.Clamp01(ts * 2f) * globalFade);
        }

        // ⑤ 干渉名: 右からスライドイン。
        if (t >= 0.5f)
        {
            float ks = EaseOut(Mathf.Clamp01((t - 0.5f) / 0.3f));
            var kindRect = new Rect((1f - ks) * w * 0.4f, h * 0.53f, w, h * 0.11f);
            DrawOutlinedLabel(kindRect, _kindLine, _kindStyle, Color.black, ks * globalFade);
        }

        // ⑥ 視聴者名: 遅れてフェードイン。
        if (t >= 0.85f)
        {
            float bs = Mathf.Clamp01((t - 0.85f) / 0.3f);
            var byRect = new Rect(0, h * 0.66f, w, h * 0.06f);
            DrawOutlinedLabel(byRect, _byLine, _byStyle, Color.black, bs * globalFade);
        }
    }

    // テーマ色の帯に暗色ストライプを重ねて工事現場風のハザード帯にする。
    private void DrawHazardBar(Rect bar, float alpha)
    {
        FillRect(bar, new Color(_theme.r, _theme.g, _theme.b, 0.9f * alpha));

        float stripeW = bar.height * 1.2f;
        var dark = new Color(0f, 0f, 0f, 0.55f * alpha);
        for (float x = 0; x < bar.width; x += stripeW * 2f)
            FillRect(new Rect(bar.x + x, bar.y, stripeW, bar.height), dark);
    }

    // 白文字の周囲 4 方向に縁取り色を敷いた見出しを描く。GUI.color で色・アルファを変調する。
    private static void DrawOutlinedLabel(Rect rect, string text, GUIStyle style, Color outline, float alpha)
    {
        float off = Mathf.Max(2f, rect.height * 0.045f);
        Color old = GUI.color;

        GUI.color = new Color(outline.r, outline.g, outline.b, alpha);
        GUI.Label(new Rect(rect.x - off, rect.y, rect.width, rect.height), text, style);
        GUI.Label(new Rect(rect.x + off, rect.y, rect.width, rect.height), text, style);
        GUI.Label(new Rect(rect.x, rect.y - off, rect.width, rect.height), text, style);
        GUI.Label(new Rect(rect.x, rect.y + off, rect.width, rect.height), text, style);

        GUI.color = new Color(1f, 1f, 1f, alpha);
        GUI.Label(rect, text, style);

        GUI.color = old;
    }

    // GUI.DrawTexture は IL2CPP で unstripping 失敗して例外になるため、
    // 既存バブル UI と同じ「background texture 付き GUIStyle + GUI.Box」で塗る (GUI.color で色・アルファ変調)。
    private void FillRect(Rect rect, Color color)
    {
        Color old = GUI.color;
        GUI.color = color;
        GUI.Box(rect, GUIContent.none, _fillStyle);
        GUI.color = old;
    }

    private static float EaseOut(float x) => 1f - ((1f - x) * (1f - x) * (1f - x));

    private void EnsureStyles()
    {
        // 1x1 塗りつぶしテクスチャは解像度に依存しないので一度だけ生成する (再生成するとネイティブ側にリークする)。
        if (!_fillTex)
        {
            _fillTex = new Texture2D(1, 1, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
            _fillTex.SetPixel(0, 0, Color.white);
            _fillTex.Apply();
            _fillTex.hideFlags |= HideFlags.HideAndDontSave;
        }

        if (_titleStyle != null && _builtHeight == Screen.height) return;
        _builtHeight = Screen.height;

        _fillStyle = new GUIStyle();
        _fillStyle.normal.background = _fillTex;

        _titleStyle = new GUIStyle
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = Mathf.RoundToInt(Screen.height * 0.105f),
            fontStyle = FontStyle.Bold,
            richText = false
        };
        _titleStyle.normal.textColor = Color.white;

        _kindStyle = new GUIStyle
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = Mathf.RoundToInt(Screen.height * 0.07f),
            fontStyle = FontStyle.Bold,
            richText = false
        };
        _kindStyle.normal.textColor = Color.white;

        _byStyle = new GUIStyle
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = Mathf.RoundToInt(Screen.height * 0.035f),
            fontStyle = FontStyle.Bold,
            richText = false
        };
        _byStyle.normal.textColor = Color.white;
    }
}
