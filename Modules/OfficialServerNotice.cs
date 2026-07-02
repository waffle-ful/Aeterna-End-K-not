using static EndKnot.Translator;

namespace EndKnot.Modules;

// 公式 Among Us サーバーでのお知らせ。位置操作/見た目変更/ペットの残っていた制限も ebdde4b9 で
// 全撤廃済みで、現在は公式鯖でも End K not はフル機能で動く。ロビー入室時の「一部制限あり」案内
// (旧 WarnInLobby) はその撤廃と同時に呼び出し側から外され、以降は不要になったため削除済み。
// 万一 (大人数ロビー等の未検証ケースで) Hacking 切断された場合のフォールバック通知だけ残す。
//
// 表示は全て LOCAL の ShowPopUp のみ。公式鯖で networked SendMessage を足すと、それ自体が
// anti-cheat を誘発しかねない ([[project_au2026_sendmessage_burst_kick]]) ので絶対にネットワーク送信しない。
public static class OfficialServerNotice
{
    // 自動部屋立て直し中は警告ポップアップを出さない (公式で kick されると毎周期出てしまうため)。
    // AutoRehost が立て直し開始〜完了の間 true にする。
    public static bool SuppressWhileRehosting;

    // 無人ホスト運用でモーダルが残り続けないよう、ポップアップは一定秒で自動的に閉じる。
    private const float AutoDismissSeconds = 10f;

    // ExitGamePatch から「Hacking 切断」のときに呼ぶ。kick 不具合は修正済みなので通常は発火しないが、
    // 大人数など未検証ケースで万一切断された場合のフォールバック通知として残す。
    // AmHost は切断処理中に倒れることがあるので見ない (公式鯖 + Hacking だけで十分な signal)。
    public static void WarnAfterHackingKick()
    {
        if (!Utils.IsOfficialServer()) return;
        LateTask.New(() => ShowPopUp(GetString("OfficialServerWarning.Kicked")), 1.9f, log: false);
    }

    private static void ShowPopUp(string text)
    {
        if (SuppressWhileRehosting) return;
        if (!HudManager.InstanceExists) return;
        try
        {
            HudManager.Instance.ShowPopUp(Decorate(text));

            // ShowPopUp は HudManager.Instance.Dialogue を使う共有ポップアップなので、N 秒後に閉じる。
            LateTask.New(() =>
            {
                try
                {
                    DialogueBox dlg = HudManager.Instance != null ? HudManager.Instance.Dialogue : null;
                    if (dlg != null) dlg.gameObject.SetActive(false);
                }
                catch { }
            }, AutoDismissSeconds, log: false);
        }
        catch { }
    }

    // 地震速報風の赤い演出でラップする (モッドニュースの警告と同系色)。読み飛ばされないための着色。
    private static string Decorate(string text)
    {
        return $"<color=#FF1A1A><b>⚠⚠⚠</b></color>\n<color=#FF2A2A>{text}</color>";
    }
}
