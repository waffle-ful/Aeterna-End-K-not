using System;
using System.Collections;
using Hazel;
using UnityEngine;

namespace EndKnot.Modules.Audience;

// 干渉発動時にホスト(配信者)画面へ専用のカットインを出す (絵は AudienceCutIn)。効果音とカメラシェイクもここで鳴らす。
// 本物の会議招集と見分けが付くよう、バニラの会議演出は流用しない。全員に見えている干渉だけは
// モッド客にも同じ演出を送る (RpcShareWithModdedClients)。
// カメラシェイクのために Main.cs で AddComponent 登録される。
public class AudienceCutscene : MonoBehaviour
{
    // 大地震の効果音は地震 (カメラシェイク) の長さに合わせて鳴らし、末尾を絞って揺れの終息とほぼ同時に消す。
    // EarthquakeSoundDuration は AudienceInterventions.QuakeCameraShakeDuration (6s) に対応させている。
    private const float EarthquakeSoundDuration = 6f; // これを過ぎたら確実に停止する上限
    private const float EarthquakeSoundFade = 2.5f;   // 終了 2.5 秒前から線形フェードアウト

    // 天の声の本文表示上限。AudienceInterventions.VoiceMaxLength と同じ値にすること
    // (NG ワード判定は切り詰め後の文字列に対して行われるため、表示側が長いとフィルタ外の文字が映る)。
    private const int VoiceDisplayMaxLength = 60;

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

        string header = Translator.GetString("Audience.Cutscene.Title");
        // 視聴者名は任意文字列なのでタグ/改行を無効化してから埋め込む (YouTubeChatOverlay.Sanitize と同一手法)。
        // 長い名前は帯からはみ出すので詰める。
        string shownAuthor = Sanitize(author) ?? "";
        if (shownAuthor.Length > 14) shownAuthor = shownAuthor[..(char.IsHighSurrogate(shownAuthor[12]) ? 12 : 13)] + "…";
        string byLine = string.Format(Translator.GetString("Audience.Cutscene.By"), shownAuthor);
        AudienceCutIn.Show(kindKey, header, kindName, byLine, BuildMessageLine(message), author, ThemeColor(kindKey));

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

    // ---- モッド客への共有 ----
    // 全員の画面で起きていることが分かる干渉だけをモッド客にも見せる。呪い/祝福 (対象が割れる)・
    // 偽死体 (偽物だと分かったら成立しない)・1人の変身 (誰かが化けたと分かる) はホスト画面だけ。
    // RPC 番号は使い切っているので、モッド客向け効果音同期 (ControllableSound) の sub-op 2 に載せる。
    private const int SharedAuthorMaxLength = 40;

    private static bool IsSharedWithModdedClients(string kindKey) => kindKey is
        "Blackout" or "Reactor" or "Comms" or "Doors" or "Meteor" or "Earthquake" or "Voice" or "ShuffleAll";

    public static void RpcShareWithModdedClients(string kindKey, string author, string message)
    {
        try
        {
            if (!AmongUsClient.Instance.AmHost || !IsSharedWithModdedClients(kindKey)) return;
            if (AudienceOptions.InterventionCutscene == null || !AudienceOptions.InterventionCutscene.GetBool()) return;
            if (!AnyOtherModdedClient()) return;

            author ??= string.Empty;
            if (author.Length > SharedAuthorMaxLength)
                author = author[..(char.IsHighSurrogate(author[SharedAuthorMaxLength - 1]) ? SharedAuthorMaxLength - 1 : SharedAuthorMaxLength)];

            MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(PlayerControl.LocalPlayer.NetId, (byte)CustomRPC.ControllableSound, SendOption.Reliable);
            writer.Write((byte)2);
            writer.Write(kindKey);
            writer.Write(author);
            writer.Write(message ?? string.Empty);
            AmongUsClient.Instance.FinishRpcImmediately(writer);
        }
        catch (Exception e) { Utils.ThrowException(e); }
    }

    public static void ReceiveFromHost(string kindKey, MessageReader reader)
    {
        string author = reader.ReadString();
        string message = reader.ReadString();
        if (AmongUsClient.Instance.AmHost || !IsSharedWithModdedClients(kindKey)) return;

        Play(kindKey, author, byte.MaxValue, message.Length == 0 ? null : message);

        // ホストでは大地震の揺れを DoEarthquake が仕掛けるが、客ではそこを通らないのでここで揺らす。
        if (kindKey == "Earthquake")
            ShakeCamera(AudienceInterventions.QuakeCameraShakeDuration, AudienceInterventions.QuakeCameraShakeSeverity);
    }

    private static bool AnyOtherModdedClient()
    {
        foreach (PlayerControl pc in Main.EnumeratePlayerControls())
            if (pc && !pc.AmOwner && pc.IsModdedClient()) return true;

        return false;
    }

    // 種別ごとのカメラシェイク (長さ, 振幅, 開始遅延)。大地震は DoEarthquake 側が持つので 0。
    private static (float Duration, float Amplitude, float Delay) ShakeFor(string kindKey) => kindKey switch
    {
        "Blackout" => (0.45f, 0.10f, 0f),
        "Reactor" => (2.2f, 0.13f, 0f),
        "Comms" => (0.5f, 0.07f, 0f),
        "Doors" => (0.35f, 0.20f, 0.24f), // シャッターが閉じ切る瞬間に「バン」と一発
        "Curse" => (1.6f, 0.08f, 0f),
        "Meteor" => (1.1f, 0.28f, 0.48f), // 着弾の瞬間から
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

    internal static Color ThemeColor(string kindKey) => kindKey switch
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
        "ShuffleOne" => new Color(0.3f, 0.9f, 0.7f),
        "ShuffleAll" => new Color(0.95f, 0.4f, 0.85f),
        _ => new Color(1f, 0.2f, 0.2f)
    };
}
