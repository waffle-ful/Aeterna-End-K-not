using System;
using System.Collections.Generic;
using System.Linq;
using EndKnot.Modules;
using UnityEngine;
using static EndKnot.Translator;

namespace EndKnot.Roles;

// ============================================================
// Newscaster (ニュースキャスター) — Crewmate/Investigate
//
// コンセプト:
//   死体が通報されるとチャットで大々的に報道する。会議中に /int 1 または /int 2 で:
//     - 自分以外が通報した場合: 通報者にインタビュー
//         1 = 通報者が(前の会議以降に)出会ったクルーの一覧
//         2 = 通報者の経路(通った部屋の順)
//     - 自分が通報した場合: 死体を調べる
//         1 = 死因
//         2 = 死亡時刻(前の会議終了、あるいはゲーム開始からの経過秒数)
//   結果は常にチャットで全員に公開される。
//
// 実装ノート:
//   - 遭遇/経路の追跡はホストローカル・送信ゼロ。RoleBase.OnFixedUpdate(pc) は保持者本人にしか
//     飛んでこないため (PlayerControlPatch.cs の s.Role.OnFixedUpdate(player) が単一 dispatch)、
//     保持者の tick の中で全生存プレイヤーを自前で走査する (Satellite.cs の部屋変化検知と同じ形)。
//     0.5 秒間隔に間引く。
//   - 通報時の「誰が/どこで/誰の死体を」は Enigma.OnReportDeadBody 等と同じ位置
//     (PlayerControlPatch.cs の AfterReportTasks) に静的フックを追加して捕捉する。この時点で
//     Encounters/Routes のライブ辞書から通報者ぶんをスナップショットへコピーしておく — 直後に
//     RoleBase.OnReportDeadBody() のインスタンス側オーバーライドがライブ辞書を「前の会議以降」の
//     意味でクリアするため、先にコピーしておかないと /int が空を返してしまう。
//   - 「クルー」は仕様上 Crewmate 陣営限定ではなく全プレイヤー扱い (インポスターも死体を通報する
//     ため陣営で絞ると壊れる。専用オプションも用意されていない)。
// ============================================================
public class Newscaster : RoleBase
{
    private const int Id = 706800;
    public static List<byte> PlayerIdList = [];

    private const float ScanIntervalSeconds = 0.5f;

    private static OptionItem AbilityUseLimit;
    private static OptionItem AbilityUseGainWithEachTaskCompleted;
    private static OptionItem ReporterKnowsInterviewer;
    private static OptionItem MaxEncountersShown;
    private static OptionItem MaxRouteRoomsShown;
    private static OptionItem EncounterRange;

    // 全生存プレイヤー間の「出会い」「経路」。キーは観測対象のプレイヤー (Newscaster 保持者ではない)。
    private static Dictionary<byte, HashSet<byte>> Encounters = [];
    private static Dictionary<byte, List<SystemTypes>> Routes = [];
    private static Dictionary<byte, SystemTypes> LastRoomPerPlayer = [];
    private static float ScanTimer;

    // 直近の通報1件ぶん (会議ごとに上書き)。
    private static bool LastReportWasBodyReport;
    private static byte LastReporterId = byte.MaxValue;
    private static byte LastVictimId = byte.MaxValue;
    private static SystemTypes? LastReportRoomId;
    private static List<byte> LastReporterEncountersSnapshot = [];
    private static List<SystemTypes> LastReporterRouteSnapshot = [];

    // ラウンド開始時刻 (前回会議終了、あるいはゲーム開始)。死亡時刻の相対表示に使う。
    private static DateTime RoundStartTimeStamp = DateTime.MinValue;

    // RoundStartTimeStamp を更新した時点の MeetingNum。RoleBase.OnRevived(pc) は既定で
    // AfterMeetingTasks() を呼ぶため、ラウンド途中の蘇生 (Altruist/TimeMaster等) でも
    // AfterMeetingTasks が飛んでくる。MeetingNum が進んでいないうちの再呼び出しは
    // 「本当の会議明け」ではないとみなしてタイムスタンプの上書きをスキップする
    // (ゲーム開始時の MeetingNum=0 と揃えて 0 で初期化)。
    private static int RoundStartMeetingNum;

    public override bool IsEnable => PlayerIdList.Count > 0;

    public override void SetupCustomOption()
    {
        StartSetup(Id)
            .AutoSetupOption(ref AbilityUseLimit, 3f, new FloatValueRule(0f, 20f, 0.05f), OptionFormat.Times)
            .AutoSetupOption(ref AbilityUseGainWithEachTaskCompleted, 0.3f, new FloatValueRule(0f, 5f, 0.05f), OptionFormat.Times)
            .AutoSetupOption(ref ReporterKnowsInterviewer, true)
            .AutoSetupOption(ref MaxEncountersShown, 5, new IntegerValueRule(1, 15, 1), OptionFormat.Pieces)
            .AutoSetupOption(ref MaxRouteRoomsShown, 6, new IntegerValueRule(1, 15, 1), OptionFormat.Pieces)
            .AutoSetupOption(ref EncounterRange, 2f, new FloatValueRule(0.5f, 5f, 0.1f));
    }

    public override void Init()
    {
        PlayerIdList = [];
        Encounters = [];
        Routes = [];
        LastRoomPerPlayer = [];
        ScanTimer = 0f;

        LastReportWasBodyReport = false;
        LastReporterId = byte.MaxValue;
        LastVictimId = byte.MaxValue;
        LastReportRoomId = null;
        LastReporterEncountersSnapshot = [];
        LastReporterRouteSnapshot = [];

        RoundStartTimeStamp = DateTime.MinValue;
        RoundStartMeetingNum = 0;
    }

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);
        playerId.SetAbilityUseLimit(AbilityUseLimit.GetFloat());
    }

    public override void Remove(byte playerId)
    {
        PlayerIdList.Remove(playerId);
    }

    public override void OnFixedUpdate(PlayerControl player)
    {
        if (!AmongUsClient.Instance.AmHost) return;
        if (!GameStates.IsInTask) return;

        // 最初の tick = 実質「ゲーム開始」。以後は AfterMeetingTasks が会議明けに更新する。
        if (RoundStartTimeStamp == DateTime.MinValue) RoundStartTimeStamp = DateTime.Now;

        ScanTimer -= Time.fixedDeltaTime;
        if (ScanTimer > 0f) return;
        ScanTimer = ScanIntervalSeconds;

        List<PlayerControl> alive = Main.AllAlivePlayerControlsToList;

        // 経路: 部屋が変わった瞬間だけ追記 (同じ部屋の連続は重複させない)。
        foreach (PlayerControl pc in alive)
        {
            PlainShipRoom room = pc.GetPlainShipRoom();
            if (!room) continue;

            if (LastRoomPerPlayer.TryGetValue(pc.PlayerId, out SystemTypes last) && last == room.RoomId) continue;
            LastRoomPerPlayer[pc.PlayerId] = room.RoomId;

            if (!Routes.TryGetValue(pc.PlayerId, out List<SystemTypes> route))
                Routes[pc.PlayerId] = route = [];

            route.Add(room.RoomId);
        }

        // 遭遇: 生存プレイヤーの全ペアを距離判定。
        float range = EncounterRange.GetFloat();

        for (var i = 0; i < alive.Count; i++)
        {
            for (int j = i + 1; j < alive.Count; j++)
            {
                PlayerControl a = alive[i];
                PlayerControl b = alive[j];
                // sqrt を避ける二乗距離比較 (0.5 秒ごとの全ペア走査なので repo の既定形に揃える)
                if (!FastVector2.DistanceWithinRange(a.Pos(), b.Pos(), range)) continue;

                if (!Encounters.TryGetValue(a.PlayerId, out HashSet<byte> setA)) Encounters[a.PlayerId] = setA = [];
                if (!Encounters.TryGetValue(b.PlayerId, out HashSet<byte> setB)) Encounters[b.PlayerId] = setB = [];
                setA.Add(b.PlayerId);
                setB.Add(a.PlayerId);
            }
        }
    }

    // 通報1件ぶんのスナップショットを捕捉する (PlayerControlPatch.AfterReportTasks から、
    // Enigma/Mortician/Medium/Spiritualist と同じ位置で呼ばれる)。このあと
    // RoleBase.OnReportDeadBody() のインスタンス側オーバーライドが Encounters/Routes を
    // クリアするので、必ずそれより前に走る (呼び出し順は PlayerControlPatch.cs 側で保証済み)。
    // synthetic: 他役職・コマンドが起こした合成通報 (InSender / Anonymous / Paranoid / /mt <id> 等)。
    // 死体を指してはいるが「誰かが実際に見つけた」わけではないので、緊急ボタンと同じく調査対象なし扱いに
    // する (Wave 3 契約 §2 — それまでは target 付き合成通報が本物の通報として号外に載っていた)。
    public static void OnAnyoneReportDeadBody(PlayerControl player, NetworkedPlayerInfo target, bool synthetic = false)
    {
        if (target == null || synthetic)
        {
            // 通常ボタンでの緊急会議 (死体の通報ではない)。今回の会議は調査対象なし扱いにする。
            LastReportWasBodyReport = false;
            return;
        }

        LastReportWasBodyReport = true;
        LastReporterId = player.PlayerId;
        LastVictimId = target.PlayerId;
        LastReportRoomId = Main.PlayerStates[player.PlayerId].LastRoom?.RoomId;

        LastReporterEncountersSnapshot = Encounters.TryGetValue(player.PlayerId, out HashSet<byte> encountered) ? encountered.ToList() : [];
        LastReporterRouteSnapshot = Routes.TryGetValue(player.PlayerId, out List<SystemTypes> route) ? route.ToList() : [];
    }

    public override void OnReportDeadBody()
    {
        // 「前の会議以降」の意味にするため、次のラウンド用にライブ追跡をクリアする。
        // (このロールの静的 OnAnyoneReportDeadBody フックがこれより先に走り、今回の通報ぶんは
        // 既にスナップショットへコピー済みなので、ここで消えても /int の結果には影響しない)
        Encounters.Clear();
        Routes.Clear();
        LastRoomPerPlayer.Clear();
    }

    public override void AfterMeetingTasks()
    {
        // RoleBase.OnRevived(pc) の既定実装がこの AfterMeetingTasks() を呼ぶため、ラウンド途中の
        // 蘇生でも飛んでくる。MeetingNum がまだ進んでいなければ「本当の会議明け」ではないので
        // タイムスタンプは更新しない (更新すると次の /int 2 の死亡時刻が蘇生時刻基準にずれる)。
        if (MeetingStates.MeetingNum != RoundStartMeetingNum)
        {
            RoundStartMeetingNum = MeetingStates.MeetingNum;
            RoundStartTimeStamp = DateTime.Now;
        }

        // NoCheckStartMeeting 経由 (PortalButton / Shuffler 等) の会議が OnAnyoneReportDeadBody を
        // 経由しなかった場合に、前回の通報が次の無関係な会議まで持ち越されるのを防ぐ
        // (WordKiller の Words/PendingKill 二重クリアと同じ理由)。
        LastReportWasBodyReport = false;
    }

    public static string BuildBroadcast()
    {
        // CustomRoles.Newscaster.IsEnable() (呼び出し側のガード) は「ホスト設定で出現枠が
        // ある」ことしか見ない。実際にこのゲームで誰にも Newscaster が割り当てられなかった
        // 場合でも OnAnyoneReportDeadBody 自体は毎通報で無条件に呼ばれて LastReportWasBodyReport
        // が立つため、ここで実アサイン (PlayerIdList) も確認しないと役職が存在しないゲームでも
        // 通報者/被害者/場所の情報が全員に漏れてしまう。
        if (PlayerIdList.Count == 0) return string.Empty;
        if (!LastReportWasBodyReport) return string.Empty;

        string reporterName = LastReporterId.ColoredPlayerName();
        string victimName = LastVictimId.ColoredPlayerName();
        string roomName = LastReportRoomId.HasValue ? GetString(LastReportRoomId.Value.ToString()) : GetString("FailToTrack");

        return string.Format(GetString("NewscasterBroadcastBody"), reporterName, victimName, roomName);
    }

    // /int コマンド本体 (ChatCommandPatch から呼ばれる)。会議中のみ有効。
    public static bool InterviewMsg(PlayerControl pc, string msg, bool isUI = false)
    {
        if (!AmongUsClient.Instance.AmHost || !GameStates.IsMeeting || (MeetingHud.Instance && MeetingHud.Instance.state is MeetingHud.VoteStates.Results or MeetingHud.VoteStates.Proceeding) || !pc || !pc.Is(CustomRoles.Newscaster)) return false;

        msg = msg.ToLower().TrimStart().TrimEnd();

        // ⚠️ 別名は「長い方を先」に置くこと。GuessManager.CheckCommand は `|` 区切りを先頭から
        // StartsWith で走査し、最初に当たったものだけを Replace で剥がす。"int|interview" の順だと
        // `/interview 1` が "int" に先に当たり "erview 1" が残って、別名が丸ごと死ぬ。
        if (!GuessManager.CheckCommand(ref msg, "interview|int", false, out bool spamRequired)) return false;

        if (!pc.IsAlive())
        {
            Utils.SendMessage(GetString("NewscasterDead"), pc.PlayerId, importance: MessageImportance.Low);
            return true;
        }

        if (!isUI && spamRequired)
            Utils.SendMessage("\n", pc.PlayerId, GetString("NoSpamAnymoreUseCmd"));

        msg = msg.Trim();

        if (msg != "1" && msg != "2")
        {
            Utils.SendMessage(GetString("NewscasterInterviewHelp"), pc.PlayerId);
            return true;
        }

        if (!LastReportWasBodyReport)
        {
            Utils.SendMessage(GetString("NewscasterNoIncident"), pc.PlayerId);
            return true;
        }

        if (pc.GetAbilityUseLimit() < 1f)
        {
            Utils.SendMessage(GetString("OutOfAbilityUsesDoMoreTasks"), pc.PlayerId);
            return true;
        }

        bool mode1 = msg == "1";
        bool selfReported = LastReporterId == pc.PlayerId;
        string body = selfReported ? BuildCorpseExamination(mode1) : BuildReporterInterview(mode1);

        pc.RpcRemoveAbilityUse(notify: false);

        string title = Utils.ColorString(Utils.GetRoleColor(CustomRoles.Newscaster), GetString("NewscasterInterviewTitle"));
        Utils.SendMessage(body, 255, title, importance: MessageImportance.High);

        if (!selfReported && ReporterKnowsInterviewer.GetBool())
            Utils.SendMessage(GetString("NewscasterReporterNotified"), LastReporterId, title, importance: MessageImportance.High);

        return true;
    }

    private static string BuildReporterInterview(bool mode1)
    {
        string reporterName = LastReporterId.ColoredPlayerName();

        if (mode1)
        {
            List<byte> shown = LastReporterEncountersSnapshot.Take(MaxEncountersShown.GetInt()).ToList();
            if (shown.Count == 0) return string.Format(GetString("NewscasterEncounterListEmpty"), reporterName);

            string names = string.Join(", ", shown.Select(id => id.ColoredPlayerName()));
            return string.Format(GetString("NewscasterEncounterList"), reporterName, names);
        }

        List<SystemTypes> route = LastReporterRouteSnapshot;
        List<SystemTypes> shownRooms = route.Count > MaxRouteRoomsShown.GetInt() ? route.Skip(route.Count - MaxRouteRoomsShown.GetInt()).ToList() : route;
        if (shownRooms.Count == 0) return string.Format(GetString("NewscasterRouteListEmpty"), reporterName);

        string rooms = string.Join(" → ", shownRooms.Select(r => GetString(r.ToString())));
        return string.Format(GetString("NewscasterRouteList"), reporterName, rooms);
    }

    private static string BuildCorpseExamination(bool mode1)
    {
        string victimName = LastVictimId.ColoredPlayerName();
        PlayerState victimState = Main.PlayerStates[LastVictimId];

        // ⚠️ 通報された死体の親が「生きているプレイヤー」であることがある。
        // Trapster の囮死体 (Trapster.OnVanish) はランダムな生存者を親にして偽の死体を作り、
        // deathReason も RealKiller も設定しない。ガードなしで読むと deathReason の既定値
        // (etc = "その他") を「死因」として全員にチャットへ流してしまう。
        // 判定は Utils.GetVitalText と同型 (本死のみ。赤ずきんも捕食された時点で本当に死んでいる)。
        if (!victimState.IsDead)
            return string.Format(GetString("NewscasterExamInconclusive"), victimName);

        if (mode1)
        {
            string reason = GetString($"DeathReason.{victimState.deathReason}");
            return string.Format(GetString("NewscasterCauseOfDeath"), victimName, reason);
        }

        if (victimState.RealKiller.TimeStamp == DateTime.MinValue)
            return string.Format(GetString("NewscasterTimeOfDeathUnknown"), victimName);

        int seconds = (int)Math.Max(0, (victimState.RealKiller.TimeStamp - RoundStartTimeStamp).TotalSeconds);
        string key = MeetingStates.MeetingNum > 1 ? "NewscasterTimeOfDeathSinceMeeting" : "NewscasterTimeOfDeathSinceStart";
        return string.Format(GetString(key), victimName, seconds);
    }
}
