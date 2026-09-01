using System;
using System.Collections.Generic;
using static EndKnot.Translator;

namespace EndKnot.Roles;

public class MeetingManager : RoleBase
{
    public static List<byte> PlayerIdList = [];

    public override bool IsEnable => PlayerIdList.Count > 0;

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(642640, TabGroup.CrewmateRoles, CustomRoles.MeetingManager);
    }

    public override void Init()
    {
        PlayerIdList = [];
    }

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);
    }

    public override void Remove(byte playerId)
    {
        PlayerIdList.Remove(playerId);
    }

    // 所持者が複数いるとき、1人ずつ独立に送ると同一フレームへ複数の送信が重なる。
    // 通知は1つの遅延タスクにまとめ、全員分を間隔送出キューへ一括で積む。
    // 本文は従来どおり遅延発火の時点で評価する (名前や役職表示の変化を拾うため)。
    private static void SendToHolders(Func<string> textFactory)
    {
        LateTask.New(() =>
        {
            if (PlayerIdList.Count == 0) return;

            string text = textFactory();
            string title = CustomRoles.MeetingManager.ColoredTextByRole(GetString("MeetingManagerMessageTitle"));
            List<Message> messages = [];
            foreach (byte id in PlayerIdList) messages.Add(new Message(text, id, title));
            messages.SendMultipleMessages(MessageImportance.High);
        }, 1f, "Meeting Manager Messages");
    }

    public static void SendCommandUsedMessage(string command)
    {
        SendToHolders(() => string.Format(GetString("MeetingManagerMessageAboutCommand"), command));
    }

    public static void OnGuess(PlayerControl dp, PlayerControl pc)
    {
        SendToHolders(() => dp == pc ? string.Format(GetString("MeetingManagerMessageAboutMisguess"), dp.GetRealName().Replace("\n", " + ")) : string.Format(GetString("MeetingManagerMessageAboutGuessedRole"), dp.GetAllRoleName().Replace("\n", " + ")));
    }

    public static void OnTrial(PlayerControl dp, PlayerControl pc)
    {
        SendToHolders(() => dp == pc ? string.Format(GetString("MeetingManagerMessageAboutJudgeSuicide"), dp.GetRealName().Replace("\n", " + "), CustomRoles.Judge.ToColoredString()) : string.Format(GetString("MeetingManagerMessageAboutGuessedRole"), dp.GetAllRoleName().Replace("\n", " + ")));
    }

    public static void OnSwap(PlayerControl tg1, PlayerControl tg2)
    {
        SendToHolders(() => string.Format(GetString("MeetingManagerMessageAboutSwap"), CustomRoles.Swapper.ToColoredString(), tg1.GetRealName().Replace("\n", " + "), tg2.GetRealName().Replace("\n", " + ")));
    }

    public static void OnCompare(PlayerControl tg1, PlayerControl tg2)
    {
        SendToHolders(() => string.Format(GetString("MeetingManagerMessageAboutCompare"), CustomRoles.Inspector.ToColoredString(), tg1.GetRealName().Replace("\n", " + "), tg2.GetRealName().Replace("\n", " + ")));
    }
}