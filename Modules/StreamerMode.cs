using System;
using EndKnot.Modules.YouTubeChat;
using HarmonyLib;
using TMPro;
using Twitch;
using UnityEngine;
using UnityEngine.Events;

namespace EndKnot.Modules;

// 配信者向けワンスイッチ + 初回オンボーディング。
// ・SystemSettings の StreamerMode トグルを OFF→ON にすると、24 時間無人配信に必要な 4 設定
//   (AutoRehostAfterKick / CrashWatchdog / AutoPlayAgain / YouTubeChat Enabled) を一括 ON にする。
//   片方向: OFF に戻しても 4 設定はそのまま (ホストが個別に微調整した設定を壊さない)。
// ・初回メニュー表示時に「配信者向け機能を体験しませんか?」の 2 択ポップアップを 1 度だけ出す。
//   はい → 一括 ON、いいえ → 何もしない。どちらでも二度と訊かない (Config フラグで永続化)。
// ・コメント取得 (YouTubeChat) は配信 URL を /yt <url> で入れるまで動かないので、一括 ON 後に
//   ホストがロビーに入ったら 1 度だけ URL 設定を促す案内メッセージを出す。
public static class StreamerMode
{
    private static GenericPopup _popup;

    // Apply 後、ホストがロビーに入ったら URL 設定案内を 1 度出すための保留フラグ。
    public static bool PendingUrlHint;

    // 4 つの配信者向け設定を ON にする。カスケード配線は OptionHolder (オプション生成時) で登録済み。
    // 個別 SetValue はそれぞれ SyncAllOptions を発火するため、公式サーバーへのバーストを避けるべく
    // 各設定は同期なし (doSync:false) で書き換え、最後に 1 度だけ全体同期する。
    public static void Apply()
    {
        try
        {
            bool changed = false;
            changed |= SetOn(Options.AutoRehostAfterKick);
            changed |= SetOn(Options.CrashWatchdog);
            changed |= SetOn(Options.AutoPlayAgain);
            changed |= SetOn(YouTubeChatOptions.Enabled);
            changed |= SetOn(Options.SpectatorAutoCam);
            changed |= SetOn(Options.MeetingAutoOpenChat);
            if (changed) OptionItem.SyncAllOptions();

            // 子オプション行の表示/位置は ReloadUI で並べ直す (閉じていれば即 return)。
            // ただし ReCreateSettings/RefreshSettingValues は CheckMark.enabled を GetBool() から
            // 塗り直さない (SetActive と位置だけ) ため、兄弟トグルのチェックはここで明示的に更新する。
            GameOptionsMenuPatch.ReloadUI();
            RepaintToggle(Options.AutoRehostAfterKick);
            RepaintToggle(Options.CrashWatchdog);
            RepaintToggle(Options.AutoPlayAgain);
            RepaintToggle(YouTubeChatOptions.Enabled);
            RepaintToggle(Options.SpectatorAutoCam);
            RepaintToggle(Options.MeetingAutoOpenChat);

            PendingUrlHint = true;
            Logger.Info("Streamer mode applied (auto-rehost / crash-watchdog / auto-play-again / youtube-chat ON)", "StreamerMode");
        }
        catch (Exception e) { Utils.ThrowException(e); }
    }

    // ON に変更したら true。同期は呼び出し側でまとめて 1 回行う。
    private static bool SetOn(OptionItem opt)
    {
        if (opt == null || opt.GetInt() == 1) return false;
        opt.SetValue(1, doSync: false);
        return true;
    }

    // 設定メニューが開いていれば、そのオプション行のチェックマークを現在値で塗り直す。
    private static void RepaintToggle(OptionItem opt)
    {
        if (opt?.OptionBehaviour == null || !opt.OptionBehaviour) return;
        ToggleOption toggle = opt.OptionBehaviour.TryCast<ToggleOption>();
        if (toggle != null && toggle.CheckMark) toggle.CheckMark.enabled = opt.GetBool();
    }

    // ロビー入室時にホストへ URL 設定案内を 1 度だけ出す (LobbyBehaviourStartPatch から呼ぶ)。
    public static void ConsumeUrlHintIfHost()
    {
        if (!PendingUrlHint) return;
        try
        {
            if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost || PlayerControl.LocalPlayer == null) return;

            PendingUrlHint = false;
            // URL 未設定 & YouTubeChat 有効のときだけ案内 (既に /yt 済みなら不要)
            bool ytOn = YouTubeChatOptions.Enabled?.GetBool() ?? false;
            if (ytOn && string.IsNullOrEmpty(Main.YouTubeStreamUrl?.Value))
                Utils.SendMessage(Translator.GetString("StreamerMode.SetUrlHint"), PlayerControl.LocalPlayer.PlayerId);
        }
        catch (Exception e) { Utils.ThrowException(e); }
    }

    // ── 初回オンボーディング (メニュー表示時に 1 度だけ) ──
    [HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.Start))]
    [HarmonyPostfix]
    public static void MainMenuStart_Postfix()
    {
        try
        {
            // watchdog が効かない環境なので初回勧誘は PC のみ (Android では出さない)
            if (OperatingSystem.IsAndroid()) return;
            if (Main.StreamerModeAsked == null || Main.StreamerModeAsked.Value) return;

            LateTask.New(() => TryShowFirstRunPopup(0), 1.5f, "StreamerMode.FirstRun", log: false);
        }
        catch (Exception e) { Utils.ThrowException(e); }
    }

    private static void TryShowFirstRunPopup(int tries)
    {
        if (Main.StreamerModeAsked == null || Main.StreamerModeAsked.Value) return;

        // 更新ポップアップと重ならないよう、保留があれば少し待ってから出す
        if (ModUpdater.UpdatePopupPending && tries < 20)
        {
            LateTask.New(() => TryShowFirstRunPopup(tries + 1), 1f, "StreamerMode.FirstRunWait", log: false);
            return;
        }

        ShowFirstRunPopup();
    }

    private static void ShowFirstRunPopup()
    {
        try
        {
            if (TwitchManager.Instance == null) return;
            Main.StreamerModeAsked.Value = true; // 出した時点で消費 (再表示しない)

            if (_popup == null)
            {
                _popup = Object.Instantiate(TwitchManager.Instance.TwitchPopup);
                _popup.name = "StreamerModePopup";
            }

            ShowTwoButtons(
                _popup,
                Translator.GetString("StreamerMode.PromptBody"),
                Translator.GetString("StreamerMode.PromptYes"),
                Translator.GetString("StreamerMode.PromptNo"),
                onYes: EnableFromPopup,
                onNo: null);
        }
        catch (Exception e) { Utils.ThrowException(e); }
    }

    // 「はい」でマスタートグルを ON にする → cascade 経由で 4 設定が揃い、メニュー表示も ON で一致する。
    // doSync:false: cascade 先の Apply() が末尾で 1 度だけ同期するため、ここで同期すると二重発火になる。
    private static void EnableFromPopup()
    {
        if (Options.StreamerMode != null) Options.StreamerMode.SetValue(1, doSync: false); // → RegisterCascade の handler が Apply
        else Apply();
    }

    // ModUpdater.ShowPopupWithTwoButtons と同じ GenericPopup パターンの自前版 (既存を触らないため複製)。
    private static void ShowTwoButtons(GenericPopup popup, string message, string yesText, string noText, Action onYes, Action onNo)
    {
        if (!popup) return;
        Transform template = popup.transform.FindChild("ExitGame");
        if (!template) return;

        Transform background = popup.transform.FindChild("Background");
        if (background) background.localScale *= 2f;

        popup.Show(message);
        template.gameObject.SetActive(false);

        Transform yes = Object.Instantiate(template, popup.transform);
        Transform no = Object.Instantiate(template, popup.transform);

        ConfigButton(yes, new(-1f, -0.7f), yesText, popup, onYes);
        ConfigButton(no, new(1f, -0.7f), noText, popup, onNo);
    }

    private static void ConfigButton(Transform button, Vector2 offset, string label, GenericPopup popup, Action onClick)
    {
        if (!button) return;
        button.gameObject.SetActive(true);
        Vector3 lp = button.localPosition;
        button.localPosition = new(lp.x + offset.x, lp.y + offset.y, lp.z);
        button.localScale *= 1.2f;

        Transform child = button.GetChild(0);
        var tt = child.GetComponent<TextTranslatorTMP>();
        if (tt) { tt.TargetText = StringNames.Cancel; tt.ResetText(); tt.DestroyTranslator(); }
        var tmp = child.GetComponent<TextMeshPro>();
        if (tmp) tmp.text = label;
        var tmpText = child.GetComponent<TMP_Text>();
        if (tmpText) tmpText.text = label;

        var pb = button.GetComponent<PassiveButton>();
        pb.OnClick = new();
        pb.OnClick.AddListener((UnityAction)(() =>
        {
            onClick?.Invoke();
            popup.Close();
        }));
    }
}
