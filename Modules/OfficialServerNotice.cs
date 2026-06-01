using static EndKnot.Translator;

namespace EndKnot.Modules;

// 転ばぬ先の杖: 公式 Among Us サーバーでは 2026 anti-cheat が「desync 役職セットアップ」を
// Hacking と判定してホストを切断するため、End K not の役職機能は現状まったく動作しない。
// ホストに「公式鯖では動かない / Modded 鯖へ切り替えて」と知らせる。
//
// 表示は全て LOCAL の ShowPopUp のみ。公式鯖で networked SendMessage を足すと、それ自体が
// anti-cheat を誘発しかねない ([[project_au2026_sendmessage_burst_kick]]) ので絶対にネットワーク送信しない。
public static class OfficialServerNotice
{
    // ロビー警告はアプリ起動中 1 回だけ (毎ロビー再入室で出すとしつこいため)。ゲーム開始警告は毎回出す。
    private static bool _lobbyWarnedThisAppSession;

    // ロビー入室時に呼ぶ。公式鯖 + ホストのときだけ 1 回警告する。
    public static void WarnInLobby()
    {
        if (_lobbyWarnedThisAppSession) return;
        if (!ShouldWarn()) return;
        _lobbyWarnedThisAppSession = true;
        ShowPopUp(GetString("OfficialServerWarning.Lobby"));
    }

    // ホストがゲームを開始したときに呼ぶ。公式鯖 + ホストのときは毎回警告する
    // (ここで開始すると確実に切断されるため、最後の砦として毎回出す)。
    public static void WarnOnGameStart()
    {
        if (!ShouldWarn()) return;
        ShowPopUp(GetString("OfficialServerWarning.GameStart"));
    }

    // ExitGamePatch から「Hacking 切断」のときに呼ぶ。公式鯖なら、着地画面で理由と対処を説明する。
    // ロビー警告 (起動中1回) とゲーム開始警告 (loading bar に隠れて見えない可能性) の取りこぼしを
    // 確実に拾う本命の通知。RejoinRequired と同じく LateTask で着地後の安定した画面に出す。
    // AmHost は切断処理中に倒れることがあるので見ない (公式鯖 + Hacking だけで十分な signal)。
    public static void WarnAfterHackingKick()
    {
        if (!Utils.IsOfficialServer()) return;
        LateTask.New(() => ShowPopUp(GetString("OfficialServerWarning.Kicked")), 1.9f, log: false);
    }

    private static bool ShouldWarn()
    {
        return AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost && Utils.IsOfficialServer();
    }

    private static void ShowPopUp(string text)
    {
        if (!HudManager.InstanceExists) return;
        try { HudManager.Instance.ShowPopUp(text); }
        catch { }
    }
}
