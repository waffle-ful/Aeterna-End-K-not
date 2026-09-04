using System;
using EndKnot.Modules.YouTubeChat;
using UnityEngine;

namespace EndKnot.Modules;

// 配信者向けワンスイッチ。
// ・SystemSettings の StreamerMode トグルを OFF→ON にすると、24 時間無人配信に必要な 4 設定
//   (AutoRehostAfterKick / CrashWatchdog / AutoPlayAgain / YouTubeChat Enabled) を一括 ON にする。
//   片方向: OFF に戻しても 4 設定はそのまま (ホストが個別に微調整した設定を壊さない)。
// ・コメント取得 (YouTubeChat) は配信 URL を /yt <url> で入れるまで動かないので、一括 ON 後に
//   ホストがロビーに入ったら 1 度だけ URL 設定を促す案内メッセージを出す。
public static class StreamerMode
{
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
}
