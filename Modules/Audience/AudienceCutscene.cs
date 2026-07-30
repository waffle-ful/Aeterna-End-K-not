using System;
using System.Collections;
using UnityEngine;

namespace EndKnot.Modules.Audience;

// 干渉発動時にホスト(配信者)画面へオリジナルのフルスクリーン演出をローカル再生する。
// バニラ会議演出の流用ではなく専用デザイン (ストロボフラッシュ+警告帯+タイトルスラム) で、
// 本物の会議招集と見分けが付くようにしてある。IMGUI 描画のみ・ネットワーク送信ゼロ。
// LobbyCodeBubble / AudienceInfoBubble と同じく Main.cs で AddComponent 登録される。
//
// 共通の骨格 (ストロボ→暗幕→ハザード帯→タイトルスラム→干渉名→視聴者名) は全種別で共有しつつ、
// 干渉の内容に応じた「署名レイヤー」(停電のフリッカー / リアクターの赤警報 / 隕石の落下軌跡 …) を
// 背面・前面に重ねる。パラメータは ApplyKindStyle / ShakeFor / ThemeColor / SoundFor の 4 つの switch に
// 集約してあるので、実機スクショを見ながらの微調整はそこだけ触れば済む。
public class AudienceCutscene : MonoBehaviour
{
    private const float DefaultDuration = 3.4f;

    // 大地震の効果音は地震 (カメラシェイク) の長さに合わせて鳴らし、末尾を絞って揺れの終息とほぼ同時に消す。
    // EarthquakeSoundDuration は AudienceInterventions.QuakeCameraShakeDuration (6s) に対応させている。
    private const float EarthquakeSoundDuration = 6f; // これを過ぎたら確実に停止する上限
    private const float EarthquakeSoundFade = 2.5f;   // 終了 2.5 秒前から線形フェードアウト

    // 天の声の本文表示上限。AudienceInterventions.VoiceMaxLength と同じ値にすること
    // (NG ワード判定は切り詰め後の文字列に対して行われるため、表示側が長いとフィルタ外の文字が映る)。
    private const int VoiceDisplayMaxLength = 60;

    // ---- 再生中の表示状態 (Play で一括構築し OnGUI では描くだけ — 毎フレ文字列生成禁止) ----
    private static bool _active;
    private static float _startTime;
    private static string _title = "";
    private static string _kindLine = "";
    private static string _byLine = "";
    private static string _message = "";
    private static Color _theme = Color.red;

    // ---- 干渉種別ごとの演出パラメータ ----
    // 種別によって使わない値もあるが、前回再生の値が残ると別の演出に混ざるので Play で必ず全て代入する。
    private static string _kindKey = "";
    private static float _duration = DefaultDuration;
    private static float _dimCeiling = 0.68f; // 暗幕の最大濃度
    private static bool _softBars;            // true = 工事帯でなく光の帯 (祝福/天の声)
    private static float _titleShake = 1f;    // タイトルのジッター倍率
    private static int _strobes = 3;          // 冒頭ストロボの回数 (0 でストロボ無し)
    private static bool _darkStrobe;          // true = 白と黒で明滅する (停電の落電表現)
    private static float _seed;               // 再生ごとにパターンを少し変えるための決定的シード

    // ---- スタイル ----
    private GUIStyle _titleStyle, _kindStyle, _byStyle, _msgStyle, _fillStyle;
    private Texture2D _fillTex;
    private int _builtHeight = -1;

    // AudienceManager.TryExecute の成功直後 (InGame && !IsMeeting 保証下)、または
    // CompanionEventEmitter.TickLobbyDemo (IsLobby 保証下) から呼ばれる。どちらもメインスレッド。
    // kindKey は "Blackout" 等の InterventionKind 名で、lang キー "Audience.Cutscene.<kindKey>" に対応する。
    // message は天の声のみ使用 (視聴者が読み上げさせた本文を画面にも出す)。
    public static void Play(string kindKey, string author, byte targetId, string message = null)
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
        _message = BuildMessageLine(message);
        _kindKey = kindKey;
        _theme = ThemeColor(kindKey);
        _startTime = Time.realtimeSinceStartup;
        _seed = Mathf.Repeat(_startTime * 7.13f, 100f);
        _active = true;

        ApplyKindStyle(kindKey);

        // 種別ごとのカメラシェイク。大地震だけは AudienceInterventions.DoEarthquake が
        // 揺れ (6s/0.35 — 効果音のフェードと対応) を既に仕掛けており、Play はその後に呼ばれるため
        // ここで上書きしてはいけない。
        (float shakeDur, float shakeAmp, float shakeDelay) = ShakeFor(kindKey);
        if (shakeDur > 0f)
            ShakeCamera(shakeDur, shakeAmp, shakeDelay);
        else if (kindKey != "Earthquake")
            // 揺らさない演出 (祝福/天の声) は、前の大地震の揺れ (最長 6 秒) が残っていたら消す。
            // 干渉間隔を短く設定していると静かな演出に揺れが被る。Earthquake だけは上記の契約により除外。
            StopCameraShake();

        // 干渉専用のショック効果音をホストローカル再生する (本物の会議招集と音でも区別が付くように)。
        // 既存のカスタム効果音基盤 (Modules/CustomSounds.cs) に相乗り — 埋込リソースを
        // 読んで SoundManager で鳴らす (送信ゼロ)。干渉種別ごとに専用の効果音を割り当てる。
        string sound = SoundFor(kindKey);
        if (kindKey == "Earthquake")
            // 大地震だけは地震演出 (6s) に合わせて末尾をフェードアウトさせたいので AudioSource 制御経路で鳴らす。
            PlayFadingSound(sound, EarthquakeSoundDuration, EarthquakeSoundFade);
        else
            CustomSoundsManager.Play(sound);
    }

    // 干渉種別ごとの演出パラメータ。実機スクショを見ながらの微調整はこの表だけを触れば済む。
    // (長さ, 暗幕の濃さ, 光の帯にするか, タイトル揺れ倍率, ストロボ回数, 白黒ストロボか)
    private static void ApplyKindStyle(string kindKey)
    {
        (_duration, _dimCeiling, _softBars, _titleShake, _strobes, _darkStrobe) = kindKey switch
        {
            "Blackout" => (3.4f, 0.78f, false, 1.0f, 4, true),   // 落電のフリッカー → 暗転
            "Reactor" => (3.6f, 0.60f, false, 1.5f, 3, false),   // 赤の警報パルス
            "Comms" => (3.4f, 0.72f, false, 1.7f, 3, false),     // 砂嵐/走査線
            "Doors" => (3.2f, 0.45f, false, 1.2f, 2, false),     // 左右からシャッター
            "Curse" => (3.6f, 0.74f, false, 0.8f, 3, false),     // 四辺から這う靄 + 呪印
            "Bless" => (3.6f, 0.18f, true, 0.5f, 2, false),      // 天からの光 + きらめき
            "Meteor" => (3.6f, 0.74f, false, 1.4f, 2, false),    // 落下軌跡 → 着弾フラッシュ
            "Earthquake" => (4.2f, 0.66f, false, 2.2f, 3, false),// 落石 + 土煙 + 地割れ
            "Voice" => (4.2f, 0.52f, true, 0.6f, 2, false),      // 天の光柱 + 波紋 + 本文表示
            "FakeBody" => (3.4f, 0.78f, false, 1.0f, 3, false),  // 血の一文字 + 心電図フラットライン
            _ => (DefaultDuration, 0.68f, false, 1.0f, 3, false)
        };
    }

    // 種別ごとのカメラシェイク (長さ, 振幅, 開始遅延)。大地震は DoEarthquake 側が持つので 0。
    private static (float Duration, float Amplitude, float Delay) ShakeFor(string kindKey) => kindKey switch
    {
        "Blackout" => (0.45f, 0.10f, 0f),
        "Reactor" => (2.2f, 0.13f, 0f),
        "Comms" => (0.5f, 0.07f, 0f),
        "Doors" => (0.35f, 0.20f, 0.4f), // シャッターが閉じ切る瞬間に「バン」と一発
        "Curse" => (1.6f, 0.08f, 0f),
        "Meteor" => (1.1f, 0.28f, 0.95f), // 着弾の瞬間から
        "FakeBody" => (0.3f, 0.06f, 0f),
        _ => (0f, 0f, 0f) // Bless / Voice は揺らさない、Earthquake は DoEarthquake が担当
    };

    // 干渉種別ごとの効果音名 (Resources/Sounds/<name>.{ogg,mp3,wav} に対応)。
    // 内容に合う既存音がある種別はそれを使い、無い種別は共通のショック音。
    private static string SoundFor(string kindKey) => kindKey switch
    {
        "Earthquake" => "Earthquake",
        "Curse" => "Curse",
        "Bless" => "Dove",
        "Meteor" => "Boom",
        "Blackout" => "FlashBang",
        _ => "AudienceShock"
    };

    // AudioSource を掴んで鳴らし、末尾 fade 秒をかけて音量を 0 に絞ってから停止する。
    // ワンショット (PlaySoundImmediate) だと地震演出が終わっても土砂崩れ音が鳴り続けるための対策。
    private static void PlayFadingSound(string sound, float duration, float fade)
    {
        AudioSource src = CustomSoundsManager.PlayControllable(sound);
        if (src == null) return;

        if (Main.Instance != null)
            Main.Instance.StartCoroutine(FadeSoundRoutine(src, duration, fade));
    }

    private static IEnumerator FadeSoundRoutine(AudioSource src, float duration, float fade)
    {
        if (src == null) yield break;

        // SoundManager.PlaySound はプール済み AudioSource を再利用するため、フェード中にこの source が
        // 別の効果音 (キル音・他の干渉音など) に採用されることがある。そのまま絞り続けると無関係な音を
        // 巻き添えで無音化してしまう (BGMManager.IsAdoptedAsCurrent と同じ罠) ので、掴んだ時点のクリップから
        // 変わっていたら以降は一切触らない。
        AudioClip startClip = src.clip;
        float startVol = src.volume;
        for (float t = 0f; t < duration; t += Time.deltaTime)
        {
            if (src == null || !IsStillOurSound(src, startClip)) yield break;

            // 会議割込・追放・ゲーム終了で即打ち切り (カメラシェイクの停止条件と揃える — 本編の裏で鳴らし続けない)。
            // ロビー中はロビー自動デモ (CompanionEventEmitter) の再生を許可する。
            if ((!GameStates.InGame && !GameStates.IsLobby) || GameStates.IsMeeting || ExileController.Instance)
            {
                src.Stop();
                yield break;
            }

            // 残り fade 秒に入ったら線形で 0 まで絞る。それより前は原音量を保つ。
            float remaining = duration - t;
            src.volume = remaining >= fade ? startVol : startVol * Mathf.Clamp01(remaining / fade);
            yield return null;
        }

        if (src != null && IsStillOurSound(src, startClip)) { src.volume = 0f; src.Stop(); }
    }

    // フェード対象の AudioSource が、掴んだ時点と同じクリップを鳴らし続けているか (プール再利用で別の音に
    // 差し替わっていないか) を判定する。差し替わっていたら以降は volume/Stop を触ってはいけない。
    private static bool IsStillOurSound(AudioSource src, AudioClip startClip)
        => startClip != null && src.clip != null && src.clip.Pointer == startClip.Pointer;

    // ---- ホストローカルのカメラシェイク ----
    // バニラ FollowerCamera.ShakeScreen は設定の「画面シェイク」OFF だと OverrideScreenShakeEnabled を
    // 立てても揺れない (2026-07-13 実機確認)。設定に依存せず揺らすため、LateUpdate で毎フレーム
    // カメラ位置に減衰ジッターを加算する自前実装。FollowerCamera.Update が毎フレーム位置を
    // ターゲット基準で再設定するので、ここでの加算はフレームを跨いで蓄積しない (送信ゼロ)。
    private static float _shakeStart = -1f;
    private static float _shakeDuration;
    private static float _shakeAmplitude;

    // delay を渡すと「隕石の着弾の瞬間から揺れる」のように演出の途中から開始できる。
    public static void ShakeCamera(float duration, float amplitude, float delay = 0f)
    {
        _shakeStart = Time.time + delay;
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
            if (t < 0f) return; // 開始待ち (delay 付きで予約された揺れ)

            // 会議開始・ゲーム終了で即打ち切り (会議 UI の裏でカメラを揺らし続けない)。ロビーは自動デモ用に許可。
            if (t >= _shakeDuration || (!GameStates.InGame && !GameStates.IsLobby) || GameStates.IsMeeting || ExileController.Instance || !HudManager.InstanceExists)
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

    // 天の声の本文を画面表示用に整える。AudienceInterventions.DoVoice が NG ワード判定に掛けたのと
    // 同じ正規化 (タグ無効化 → 改行除去 → Trim → 60 字で切り詰め) を通すことで、フィルタが見た文字列と
    // 画面に出る文字列を一致させる (切り詰め後の 60 字だけが判定対象なので、ここで長い原文を出すと
    // フィルタをすり抜けた部分が配信に映ってしまう)。
    private static string BuildMessageLine(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "";

        string shown = Sanitize(message).Trim();
        if (shown.Length > VoiceDisplayMaxLength) shown = shown[..VoiceDisplayMaxLength];
        return shown.Length == 0 ? "" : $"「{shown}」";
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

            // 本物の会議が始まった / 追放演出中 / ゲームが終わったら即座に演出を打ち切る (それらの画面の上に被せない)。
            // ロビーは自動デモ用に許可。ExileController は LateUpdate・FadeSoundRoutine の打ち切り条件と揃えたもの
            // (投票終了で IsMeeting が false に戻ってから追放演出が終わるまでの窓に描画が漏れていた)。
            if ((!GameStates.InGame && !GameStates.IsLobby) || GameStates.IsMeeting || ExileController.Instance)
            {
                _active = false;
                return;
            }

            float t = Time.realtimeSinceStartup - _startTime;
            if (t >= _duration)
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
        // 署名レイヤーで 1 回の描画あたりの GUI.Box が増えたので、実描画イベント以外は捨てる
        // (クリック等を扱わないため Layout イベントで描く必要がない)。
        if (Event.current.type != EventType.Repaint) return;

        float w = Screen.width, h = Screen.height;
        // 終盤 0.5 秒で全要素をまとめてフェードアウトさせる係数。
        float globalFade = Mathf.Clamp01((_duration - t) / 0.5f);

        // ① 冒頭ストロボ: 視線を強制的に奪う。停電だけは白と黒の明滅 (落電) にする。
        if (t < 0.45f && _strobes > 0)
        {
            int strobe = (int)(t / (0.45f / _strobes));
            float strobeAlpha = (1f - (t / 0.45f)) * 0.85f;
            Color flash = _darkStrobe
                ? strobe % 2 == 0 ? Color.white : Color.black
                : strobe == 1 ? _theme : Color.white;
            FillRect(new Rect(0, 0, w, h), new Color(flash.r, flash.g, flash.b, strobeAlpha));
        }

        // ② 暗幕: 素早く立ち上がり、終盤にフェードアウト。濃さは干渉種別ごと (停電は真っ暗、祝福は薄く)。
        float dim = Mathf.Clamp01((t - 0.1f) / 0.3f) * _dimCeiling * globalFade;
        FillRect(new Rect(0, 0, w, h), new Color(0f, 0f, 0f, dim));

        // ③ 干渉内容に応じた署名レイヤー (背面)。
        DrawSignatureBack(t, w, h, globalFade);

        // ④ 上下のハザード帯: 左右からスライドイン。テーマ色地に暗いストライプ (祝福/天の声は光の帯)。
        float barH = h * 0.085f;
        float slide = EaseOut(Mathf.Clamp01((t - 0.15f) / 0.35f));
        DrawHazardBar(new Rect(-w + (w * slide), 0, w, barH), globalFade);
        DrawHazardBar(new Rect(w - (w * slide), h - barH, w, barH), globalFade);

        // ⑤ タイトル: 画面上から叩き付けるように落下して静止 + 減衰シェイク。
        // (GUI.matrix / GUIUtility.ScaleAroundPivot によるスケール演出は IL2CPP で unstripping 失敗するため使用禁止 — 実機ログ 2026-07-13 で確認)
        if (t >= 0.2f)
        {
            float ts = Mathf.Clamp01((t - 0.2f) / 0.35f);
            float drop = (1f - EaseOut(ts)) * -h * 0.5f;
            float shakeAmp = Mathf.Max(0f, 1.1f - t) * 14f * _titleShake;
            var jitter = new Vector2(Mathf.Sin(t * 61f) * shakeAmp, Mathf.Cos(t * 47f) * shakeAmp);

            var titleRect = new Rect(jitter.x, (h * 0.38f) - (h * 0.09f) + jitter.y + drop, w, h * 0.18f);
            DrawOutlinedLabel(titleRect, _title, _titleStyle, _theme, Mathf.Clamp01(ts * 2f) * globalFade);
        }

        // ⑥ 干渉名: 右からスライドイン。
        if (t >= 0.5f)
        {
            float ks = EaseOut(Mathf.Clamp01((t - 0.5f) / 0.3f));
            var kindRect = new Rect((1f - ks) * w * 0.4f, h * 0.53f, w, h * 0.11f);
            DrawOutlinedLabel(kindRect, _kindLine, _kindStyle, Color.black, ks * globalFade);
        }

        // ⑦ 視聴者名: 遅れてフェードイン。
        if (t >= 0.85f)
        {
            float bs = Mathf.Clamp01((t - 0.85f) / 0.3f);
            var byRect = new Rect(0, h * 0.66f, w, h * 0.06f);
            DrawOutlinedLabel(byRect, _byLine, _byStyle, Color.black, bs * globalFade);
        }

        // ⑧ 天の声の本文: さらに遅れて出す (読み上げと画面表示を揃える)。
        if (_message.Length > 0 && t >= 1.05f)
        {
            float ms = Mathf.Clamp01((t - 1.05f) / 0.35f);
            var msgRect = new Rect(w * 0.1f, h * 0.73f, w * 0.8f, h * 0.14f);
            DrawOutlinedLabel(msgRect, _message, _msgStyle, Color.black, ms * globalFade);
        }

        // ⑨ 署名レイヤー (前面) — 文字の上に被せるノイズ・閃光の類。
        DrawSignatureFront(t, w, h, globalFade);
    }

    // ---- 干渉内容ごとの署名レイヤー ----
    // 回転・スケール・クリッピングは IL2CPP で使えないため、全て軸並行の矩形 (FillRect) と
    // アルファ変調だけで組み立てている。ランダム性は毎フレ乱数ではなく決定的ハッシュで出す
    // (OnGUI は 1 フレームに複数回呼ばれるので、乱数だと形がちらつく)。

    private void DrawSignatureBack(float t, float w, float h, float fade)
    {
        switch (_kindKey)
        {
            case "Blackout": DrawBlackoutBack(t, w, h, fade); break;
            case "Reactor": DrawReactorBack(t, w, h, fade); break;
            case "Comms": DrawCommsBack(t, w, h, fade); break;
            case "Doors": DrawDoorsBack(t, w, h, fade); break;
            case "Curse": DrawCurseBack(t, w, h, fade); break;
            case "Bless": DrawBlessBack(t, w, h, fade); break;
            case "Meteor": DrawMeteorBack(t, w, h, fade); break;
            case "Earthquake": DrawEarthquakeBack(t, w, h, fade); break;
            case "Voice": DrawVoiceBack(t, w, h, fade); break;
            case "FakeBody": DrawFakeBodyBack(t, w, h, fade); break;
        }
    }

    private void DrawSignatureFront(float t, float w, float h, float fade)
    {
        switch (_kindKey)
        {
            case "Comms": DrawCommsFront(t, w, h, fade); break;
            case "Meteor": DrawMeteorFront(t, w, h, fade); break;
            case "FakeBody": FillRect(new Rect(0, 0, w, h), new Color(0.5f, 0.52f, 0.58f, 0.13f * fade)); break; // 血の気が引いた色被り
        }
    }

    // 停電: 落電のフリッカーと、細く残る非常灯。
    private void DrawBlackoutBack(float t, float w, float h, float fade)
    {
        // 落電のフリッカーは冒頭 1 秒だけ。ホストは演出中もプレイ中なので、画面を長く塞がない。
        if (t < 1f && Hash(Mathf.Floor(t * 16f)) < 0.42f)
            FillRect(new Rect(0, 0, w, h), new Color(0f, 0f, 0f, 0.85f * fade));

        for (int i = 0; i < 4; i++)
        {
            float x = w * (0.12f + (i * 0.25f));
            float pulse = 0.09f + (0.10f * Mathf.Abs(Mathf.Sin((t * 2.2f) + (i * 1.3f))));
            FillRect(new Rect(x, 0, w * 0.055f, h), new Color(_theme.r, _theme.g, _theme.b, pulse * fade));
        }
    }

    // リアクター: 脈打つ赤い警報と、太さの変わる非常フレーム。
    private void DrawReactorBack(float t, float w, float h, float fade)
    {
        float pulse = 0.5f + (0.5f * Mathf.Sin(t * 9f));
        FillRect(new Rect(0, 0, w, h), new Color(1f, 0.05f, 0.05f, (0.07f + (0.14f * pulse)) * fade));
        DrawRectOutline(new Rect(0, 0, w, h), h * (0.016f + (0.014f * pulse)), new Color(1f, 0.1f, 0.1f, 0.85f * fade));
        DrawRectOutline(new Rect(w * 0.03f, h * 0.05f, w * 0.94f, h * 0.9f), h * 0.005f, new Color(1f, 0.5f, 0.2f, 0.45f * fade));
    }

    // 通信妨害: 走査線 (スクロールする横縞)。
    private void DrawCommsBack(float t, float w, float h, float fade)
    {
        float spacing = h / 34f;
        float off = Mathf.Repeat(t * 90f, spacing);
        var line = new Color(_theme.r, _theme.g, _theme.b, 0.15f * fade);
        for (float y = off - spacing; y < h; y += spacing)
            FillRect(new Rect(0, y, w, spacing * 0.34f), line);
    }

    // 通信妨害: 文字の上に走るグリッチバー (横ずれしたノイズ帯)。
    private void DrawCommsFront(float t, float w, float h, float fade)
    {
        float frame = Mathf.Floor(t * 14f);
        for (int i = 0; i < 4; i++)
        {
            float y = Hash(frame + (i * 7.3f)) * h;
            float bh = h * (0.012f + (Hash(frame + i + 0.5f) * 0.04f));
            float dx = (Hash(frame + i + 3.1f) - 0.5f) * w * 0.22f;
            FillRect(new Rect(dx, y, w, bh), new Color(1f, 1f, 1f, 0.09f * fade));
            FillRect(new Rect(dx + (w * 0.012f), y, w, bh * 0.45f), new Color(_theme.r, _theme.g, _theme.b, 0.22f * fade));
        }
    }

    // ドア封鎖: 左右から鉄シャッターが閉じ切る。
    private void DrawDoorsBack(float t, float w, float h, float fade)
    {
        // 閉じ切って 0.3 秒見せたら左右へ引き上げる。閉まる衝撃が主役なので、視界を塞ぎ続けない
        // (ホストは演出中もプレイしている — 全面を覆ったまま数秒放置するとキル/ベントを見落とす)。
        float close = t < 0.75f
            ? EaseOut(Mathf.Clamp01((t - 0.05f) / 0.4f))
            : 1f - EaseOut(Mathf.Clamp01((t - 0.75f) / 0.45f));
        if (close <= 0.002f) return;

        float sw = w * 0.5f * close;
        var metal = new Color(0.1f, 0.1f, 0.12f, 0.95f * fade);
        FillRect(new Rect(0, 0, sw, h), metal);
        FillRect(new Rect(w - sw, 0, sw, h), metal);

        var rib = new Color(0f, 0f, 0f, 0.35f * fade);
        for (float y = 0; y < h; y += h * 0.07f)
        {
            FillRect(new Rect(0, y, sw, h * 0.012f), rib);
            FillRect(new Rect(w - sw, y, sw, h * 0.012f), rib);
        }

        var edge = new Color(_theme.r, _theme.g, _theme.b, 0.9f * fade);
        FillRect(new Rect(sw - (w * 0.004f), 0, w * 0.008f, h), edge);
        FillRect(new Rect(w - sw - (w * 0.004f), 0, w * 0.008f, h), edge);
    }

    // 呪い: 四辺から這い寄る紫の靄と、中心から広がる呪印のリング。
    private void DrawCurseBack(float t, float w, float h, float fade)
    {
        float creep = EaseOut(Mathf.Clamp01((t - 0.1f) / 1.2f));
        float band = h * 0.22f * creep;
        var mist = new Color(_theme.r, _theme.g, _theme.b, 0.42f * fade);
        FillRect(new Rect(0, 0, w, band), mist);
        FillRect(new Rect(0, h - band, w, band), mist);
        FillRect(new Rect(0, 0, band, h), mist);
        FillRect(new Rect(w - band, 0, band, h), mist);

        for (int i = 0; i < 3; i++)
        {
            float p = Mathf.Repeat((t * 0.65f) + (i * 0.33f), 1f);
            float rw = (w * 0.15f) + (w * 0.85f * p), rh = (h * 0.15f) + (h * 0.85f * p);
            DrawRectOutline(new Rect((w - rw) * 0.5f, (h - rh) * 0.5f, rw, rh), h * 0.008f, new Color(_theme.r, _theme.g, _theme.b, (1f - p) * 0.55f * fade));
        }
    }

    // 祝福: 天から差す光の柱ときらめき。
    private void DrawBlessBack(float t, float w, float h, float fade)
    {
        float grow = EaseOut(Mathf.Clamp01(t / 0.8f));
        for (int i = 0; i < 6; i++)
        {
            float x = w * (0.05f + (i * 0.16f) + (Hash(i) * 0.04f));
            float bw = w * (0.03f + (Hash(i + 20f) * 0.05f));
            float a = (0.10f + (0.07f * Mathf.Sin((t * 2.5f) + i))) * fade;
            FillRect(new Rect(x, 0, bw, h * grow), new Color(1f, 0.95f, 0.6f, a));
        }

        for (int i = 0; i < 14; i++)
        {
            float tw = Mathf.Abs(Mathf.Sin((t * 3.4f) + (i * 0.9f)));
            if (tw < 0.6f) continue;
            float s = h * 0.014f * tw;
            FillRect(new Rect(Hash(i + 40f) * w, Hash(i + 60f) * h, s, s), new Color(1f, 1f, 0.85f, 0.8f * fade));
        }
    }

    // 隕石: 右上から左下へ流れる火球の軌跡 (回転できないので小矩形を線上に並べて尾を作る)。
    private void DrawMeteorBack(float t, float w, float h, float fade)
    {
        for (int i = 0; i < 4; i++)
        {
            float delay = 0.05f + (Hash(i) * 0.45f);
            float p = (t - delay) / 0.7f;
            if (p < 0f || p > 1.4f) continue;

            float sx = w * (0.6f + (Hash(i + 11f) * 0.6f)), ex = sx - (w * 0.95f);
            float sy = -h * 0.2f, ey = h * 1.1f;
            float pp = Mathf.Clamp01(p);
            float tail = Mathf.Clamp01(1.4f - p);

            for (int k = 0; k < 10; k++)
            {
                float q = Mathf.Clamp01(pp - (k * 0.04f));
                float x = Mathf.Lerp(sx, ex, q), y = Mathf.Lerp(sy, ey, q);
                float s = h * (0.024f - (k * 0.0016f));
                FillRect(new Rect(x, y, s, s), new Color(1f, 0.6f - (k * 0.03f), 0.1f, (1f - (k / 10f)) * 0.8f * tail * fade));
            }
        }
    }

    // 隕石: 着弾の閃光。ShakeFor の遅延 (0.95s) と同じタイミングで焚く。
    private void DrawMeteorFront(float t, float w, float h, float fade)
    {
        if (t < 0.95f) return;

        float f = Mathf.Clamp01(1f - ((t - 0.95f) / 0.45f));
        FillRect(new Rect(0, 0, w, h), new Color(1f, 0.45f, 0.1f, 0.5f * f * fade));
        FillRect(new Rect(0, h * 0.75f, w, h * 0.25f), new Color(1f, 0.3f, 0.05f, 0.3f * f * fade));
    }

    // 大地震: 落石・立ち上る土煙・地割れ。カメラの揺れは DoEarthquake 側 (6s)。
    private void DrawEarthquakeBack(float t, float w, float h, float fade)
    {
        for (int i = 0; i < 10; i++)
        {
            float p = t - (Hash(i) * 1.8f);
            if (p < 0f) continue;

            float y = p * p * h * 0.85f;
            if (y > h) continue;

            float s = h * (0.018f + (Hash(i + 13f) * 0.03f));
            FillRect(new Rect(Hash(i + 31f) * w, y, s, s * 0.7f), new Color(0.33f, 0.25f, 0.15f, 0.9f * fade));
        }

        float dust = EaseOut(Mathf.Clamp01((t - 0.3f) / 1.4f)) * h * 0.3f;
        FillRect(new Rect(0, h - dust, w, dust), new Color(0.45f, 0.36f, 0.24f, 0.32f * fade));

        if (t <= 0.35f) return;

        float g = Mathf.Clamp01((t - 0.35f) / 0.6f);
        var crack = new Color(0.04f, 0.03f, 0.02f, 0.85f * fade);
        for (int c = 0; c < 3; c++)
        {
            float baseX = w * (0.2f + (c * 0.3f));
            for (int k = 0; k < 9; k++)
            {
                if (k / 9f > g) break;
                FillRect(new Rect(baseX + ((Hash((c * 10f) + k) - 0.5f) * w * 0.05f), h * (0.56f + (k * 0.045f)), w * 0.012f, h * 0.05f), crack);
            }
        }
    }

    // 天の声: 中央に降りる光柱と、広がる波紋。
    private void DrawVoiceBack(float t, float w, float h, float fade)
    {
        float grow = EaseOut(Mathf.Clamp01(t / 0.7f)) * h;
        FillRect(new Rect(w * 0.25f, 0, w * 0.5f, grow), new Color(1f, 0.95f, 0.65f, 0.11f * fade));
        FillRect(new Rect(w * 0.37f, 0, w * 0.26f, grow), new Color(1f, 1f, 0.8f, 0.1f * fade));

        for (int i = 0; i < 3; i++)
        {
            float p = Mathf.Repeat((t * 0.5f) + (i * 0.33f), 1f);
            float rw = (w * 0.2f) + (w * 0.8f * p), rh = (h * 0.2f) + (h * 0.8f * p);
            DrawRectOutline(new Rect((w - rw) * 0.5f, (h - rh) * 0.5f, rw, rh), h * 0.005f, new Color(1f, 0.9f, 0.5f, (1f - p) * 0.4f * fade));
        }
    }

    // 偽死体: 中央から左右に伸びる血の一文字と、途中で平坦になる心電図。
    private void DrawFakeBodyBack(float t, float w, float h, float fade)
    {
        float sp = EaseOut(Mathf.Clamp01((t - 0.25f) / 0.35f));
        float sw = w * 0.9f * sp;
        FillRect(new Rect((w - sw) * 0.5f, (h * 0.5f) - (h * 0.006f), sw, h * 0.012f), new Color(0.75f, 0.05f, 0.05f, 0.9f * fade));

        const int segments = 36;
        float ly = h * 0.84f, seg = w / segments;
        var pulse = new Color(0.55f, 0.95f, 0.65f, 0.8f * fade);
        int drawn = Mathf.RoundToInt(Mathf.Clamp01((t - 0.4f) / 1.5f) * segments);
        float prevY = ly;
        for (int i = 0; i < drawn; i++)
        {
            // 12〜15 の間だけ一度大きく跳ねて、以降は平坦 (心停止) にする。
            float beat = i switch { 12 => -h * 0.05f, 13 => h * 0.035f, 14 => -h * 0.012f, _ => 0f };
            float y = ly + beat;
            FillRect(new Rect(i * seg, y, seg, h * 0.006f), pulse);
            // 段差は縦の矩形で繋いで折れ線に見せる。
            if (i > 0 && !Mathf.Approximately(y, prevY))
                FillRect(new Rect(i * seg, Mathf.Min(y, prevY), seg * 0.35f, Mathf.Abs(y - prevY)), pulse);
            prevY = y;
        }
    }

    // テーマ色の帯に暗色ストライプを重ねて工事現場風のハザード帯にする。
    // 祝福/天の声だけは工事帯だと雰囲気が壊れるので、内側が白く光る柔らかい帯に切り替える。
    private void DrawHazardBar(Rect bar, float alpha)
    {
        FillRect(bar, new Color(_theme.r, _theme.g, _theme.b, 0.9f * alpha));

        if (_softBars)
        {
            FillRect(new Rect(bar.x, bar.y + (bar.height * 0.32f), bar.width, bar.height * 0.36f), new Color(1f, 1f, 0.9f, 0.5f * alpha));
            return;
        }

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

    // 矩形の枠だけを 4 本の帯で描く (呪印のリング・リアクターの非常フレーム用)。
    private void DrawRectOutline(Rect rect, float thickness, Color color)
    {
        FillRect(new Rect(rect.x, rect.y, rect.width, thickness), color);
        FillRect(new Rect(rect.x, rect.yMax - thickness, rect.width, thickness), color);
        FillRect(new Rect(rect.x, rect.y, thickness, rect.height), color);
        FillRect(new Rect(rect.xMax - thickness, rect.y, thickness, rect.height), color);
    }

    private static float EaseOut(float x) => 1f - ((1f - x) * (1f - x) * (1f - x));

    private static float Frac(float x) => x - Mathf.Floor(x);

    // 再生ごとに少しだけ形が変わる決定的ハッシュ。OnGUI は 1 フレームに複数回呼ばれるため、
    // UnityEngine.Random を引くと同一フレーム内で形がちらついてしまう。
    private static float Hash(float n) => Frac(Mathf.Sin((n * 12.9898f) + _seed) * 43758.5453f);

    private void EnsureStyles()
    {
        // 1x1 塗りつぶしテクスチャは解像度に依存しないので一度だけ生成する (再生成するとネイティブ側にリークする)。
        if (!_fillTex)
        {
            _fillTex = new Texture2D(1, 1, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
            _fillTex.SetPixel(0, 0, Color.white);
            _fillTex.Apply(true, true);
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

        _msgStyle = new GUIStyle
        {
            alignment = TextAnchor.UpperCenter,
            fontSize = Mathf.RoundToInt(Screen.height * 0.042f),
            fontStyle = FontStyle.Bold,
            richText = false,
            wordWrap = true
        };
        _msgStyle.normal.textColor = Color.white;
    }
}
