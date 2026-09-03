using UnityEngine;
using static EndKnot.Translator;

namespace EndKnot.Modules;

// 死体通報/緊急ボタンのどちらで会議が始まったかを、少し間を置いてから全員へ知らせる。
// 会議初手のテンプレ送信と時刻が重ならないよう、必ずその送信より後に遅延送信する。
public static class ReportReasonNotice
{
    private static OptionItem EnableReportReasonNotice;
    private static OptionItem ReportReasonDelay;
    private static OptionItem ShowReporter;
    private static OptionItem ShowVictim;

    private static bool HasPendingReport;
    private static byte PendingReporterId = byte.MaxValue;
    private static byte PendingVictimId = byte.MaxValue; // MaxValue = 緊急ボタン (死体無し)

    public static void SetupCustomOption()
    {
        new TextOptionItem(110090, "MenuTitle.ReportReasonNotice", TabGroup.GameSettings)
            .SetColor(new Color32(120, 200, 255, byte.MaxValue))
            .SetHeader(true);

        EnableReportReasonNotice = new BooleanOptionItem(960090, "EnableReportReasonNotice", false, TabGroup.GameSettings)
            .SetColor(new Color32(120, 200, 255, byte.MaxValue));

        ReportReasonDelay = new FloatOptionItem(960091, "ReportReasonDelay", new(1f, 30f, 1f), 8f, TabGroup.GameSettings)
            .SetParent(EnableReportReasonNotice)
            .SetValueFormat(OptionFormat.Seconds)
            .SetColor(new Color32(120, 200, 255, byte.MaxValue));

        ShowReporter = new BooleanOptionItem(960092, "ReportReasonShowReporter", true, TabGroup.GameSettings)
            .SetParent(EnableReportReasonNotice)
            .SetColor(new Color32(120, 200, 255, byte.MaxValue));

        ShowVictim = new BooleanOptionItem(960093, "ReportReasonShowVictim", true, TabGroup.GameSettings)
            .SetParent(EnableReportReasonNotice)
            .SetColor(new Color32(120, 200, 255, byte.MaxValue));
    }

    // ReportDeadBodyPatch.AfterReportTasks から呼ぶ。この時点で会議は確定して始まる。
    public static void OnReportConfirmed(PlayerControl reporter, NetworkedPlayerInfo target)
    {
        if (!AmongUsClient.Instance.AmHost || EnableReportReasonNotice?.GetBool() != true) return;

        HasPendingReport = true;
        PendingReporterId = reporter ? reporter.PlayerId : byte.MaxValue;
        PendingVictimId = target?.PlayerId ?? byte.MaxValue;
    }

    // MeetingHudPatch の OnMeeting テンプレ送信ブロックの直後から呼ぶ。
    // 呼び出し自体が既に (少なくとも) そのテンプレ送信と同時刻以降であることが前提。
    public static void ScheduleSend()
    {
        if (!AmongUsClient.Instance.AmHost || EnableReportReasonNotice?.GetBool() != true || !HasPendingReport) return;

        byte reporterId = PendingReporterId;
        byte victimId = PendingVictimId;
        HasPendingReport = false;

        // 会議番号を捕捉しておく。総遅延は (テンプレ送信の 8 秒) + (このオプション秒) で最大 38 秒あり、
        // その間に今の会議が終わって次の会議が始まると IsMeeting は再び true になる。
        // 番号を照合しないと「前の会議の通報理由」が次の会議に出る (Missioneer / Sandbox と同じ定型)。
        int meetingNum = MeetingStates.MeetingNum;

        float delay = Mathf.Max(0.1f, ReportReasonDelay.GetFloat());
        LateTask.New(() => Send(reporterId, victimId, meetingNum), delay, "ReportReasonNotice");
    }

    private static void Send(byte reporterId, byte victimId, int meetingNum)
    {
        if (!AmongUsClient.Instance.AmHost || !GameStates.InGame || GameStates.IsEnded || !GameStates.IsMeeting) return;
        if (MeetingStates.MeetingNum != meetingNum) return;

        // 遅延中に切断等で対象が解決できなくなっていたら無言でスキップする。
        if (!Utils.GetPlayerById(reporterId)) return;

        bool isEmergency = victimId == byte.MaxValue;
        if (!isEmergency && !Utils.GetPlayerById(victimId)) return;

        string reporterName = ShowReporter.GetBool() ? reporterId.ColoredPlayerName() : GetString("ReportReason.AnonymousReporter");

        string body = isEmergency
            ? string.Format(GetString("ReportReason.Emergency"), reporterName)
            : string.Format(GetString("ReportReason.Body"), reporterName, ShowVictim.GetBool() ? victimId.ColoredPlayerName() : GetString("ReportReason.AnonymousVictim"));

        Utils.SendMessage(body, byte.MaxValue, importance: MessageImportance.Low);
    }
}
