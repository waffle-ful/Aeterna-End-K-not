using System.Collections.Generic;
using TMPro;
using UnityEngine;
using static EndKnot.Translator;

namespace EndKnot.Modules;

// 会議画面の左上に「Day.N」と「どうして会議が始まったか」の 2 行を出す。
//
// 非モッドクライアントにも見えることが要件なので、ホスト画面のローカル UI ではなく
// バニラが描く名前テキストに相乗りする。line-height=0 で行送りを殺し、voffset で
// 上方向へ浮かせるため、行を足してもカードのレイアウトは押し下がらない。
//
// 相乗り先は「会議カードの先頭に並ぶ人」1 人だけ。並び順はバニラ側の都合なので、
// 予想が外れていないかを MeetingHud 生成時に 1 ゲーム 1 回だけ突き合わせて記録する。
public static class MeetingHeader
{
    // 会議が始まった理由。
    public enum MeetingCause
    {
        None,
        EmergencyButton,
        BodyReport,
        Forced
    }

    // voffset は非 orthographic 換算で値 x 0.1 ワールド単位。
    // 🔴 名前本体にも下駄 (NameOffset) を履かせるのが要。先頭の改行でテキスト塊が 1 行ぶん下がるので、
    //    名前を同じだけ持ち上げないとカードの外へはみ出して落ちる (2026-09-14 非モッド客で実測)。
    private const string DayOffset = "17.5";
    private const string CauseOffset = "15";
    private const string NameOffset = "10";

    // 会議が始まった理由の行を一番大きく見せる。日数はその脇に添える大きさ。
    // 名前へ相乗りする側だけ小さいのは、会議カードの名前欄が狭く、等倍だと右端で切れるため (実測)。
    private const string NameDaySize = "70%";
    private const string NameCauseSize = "85%";
    private const string OverlayDaySize = "85%";
    private const string OverlayCauseSize = "115%";

    // 日数の色。モッドの色 (cyan) を、白地でも黒地でも読める明るさまで落とした値。
    // 相対輝度 0.288 = 黒に対し 6.8:1 / 白に対し 3.1:1 で、両方とも大きめの字の基準 (3:1) を満たす。
    // これより明るい cyan は白地側が 3:1 を割るので、ブランド色に寄せられる上限がここ。
    private const string DayColor = "#00a3a3";

    private static OptionItem EnableMeetingHeader;
    private static OptionItem ShowDay;
    private static OptionItem ShowCause;

    private static MeetingCause Cause;
    private static byte ReporterId = byte.MaxValue;
    private static byte VictimId = byte.MaxValue;
    private static int Day;

    // 名前に相乗りさせる相手 (会議カードの先頭に並ぶと予想した人)。
    private static byte SlotZeroId = byte.MaxValue;

    // 予算不足でヘッダーを諦めたことを記録した会議の日数 (同じ会議で何度も書かないため)。
    private static int LastBudgetDropDay;

    public static bool Enabled => EnableMeetingHeader?.GetBool() == true && Options.CurrentGameMode == CustomGameMode.Standard;

    public static void SetupCustomOption()
    {
        new TextOptionItem(110150, "MenuTitle.MeetingHeader", TabGroup.GameSettings)
            .SetColor(new Color32(252, 144, 3, byte.MaxValue))
            .SetHeader(true);

        EnableMeetingHeader = new BooleanOptionItem(960190, "EnableMeetingHeader", true, TabGroup.GameSettings)
            .SetColor(new Color32(252, 144, 3, byte.MaxValue));

        ShowDay = new BooleanOptionItem(960191, "MeetingHeaderShowDay", true, TabGroup.GameSettings)
            .SetParent(EnableMeetingHeader)
            .SetColor(new Color32(252, 144, 3, byte.MaxValue));

        ShowCause = new BooleanOptionItem(960192, "MeetingHeaderShowCause", true, TabGroup.GameSettings)
            .SetParent(EnableMeetingHeader)
            .SetColor(new Color32(252, 144, 3, byte.MaxValue));
    }

    // 会議が終わったら相乗り先を手放す。ここを外すと、会議明けに ForMeeting 付きの名前更新が
    // 走った場合にヘッダーが試合中の名前へ残る。
    public static void OnMeetingEnd()
    {
        SlotZeroId = byte.MaxValue;
        Cause = MeetingCause.None;
    }

    public static void Reset()
    {
        Cause = MeetingCause.None;
        ReporterId = byte.MaxValue;
        VictimId = byte.MaxValue;
        Day = 0;
        SlotZeroId = byte.MaxValue;
        LastBudgetDropDay = 0;
    }

    // ReportDeadBodyPatch.AfterReportTasks から呼ぶ。この時点で会議は確定して始まるが、
    // MeetingStates.MeetingNum はまだ加算されていない (加算は MeetingHud.Start の Prefix) ので、
    // ここで日数を確定させて会議のあいだ使い回す。
    public static void OnReportConfirmed(PlayerControl reporter, NetworkedPlayerInfo target, bool synthetic)
    {
        if (!AmongUsClient.Instance.AmHost) return;

        ReporterId = reporter ? reporter.PlayerId : byte.MaxValue;
        VictimId = target?.PlayerId ?? byte.MaxValue;
        Day = MeetingStates.MeetingNum + 1;

        Cause = target != null
            ? MeetingCause.BodyReport
            : synthetic
                ? MeetingCause.Forced
                : MeetingCause.EmergencyButton;
    }

    // 相乗り先を決め直す。NotifyRoles の会議ぶんの組み立て 1 回につき 1 度だけ呼ぶ。
    // バニラの会議カードは「生存者を PlayerId 昇順 → 死亡者を PlayerId 昇順」で並ぶ、という前提。
    //
    // 🔴 カードの並びは MeetingHud が生成された 1 度きりで固定される。会議が開いた後に計算し直すと、
    //    会議中の切断で相乗り先が別人へ移り、元の人の名前はヘッダー無しで上書きされて消える。
    //    会議が始まっていたら触らない。
    public static void Recompute()
    {
        if (GameStates.IsMeeting) return;

        SlotZeroId = byte.MaxValue;
        if (!Enabled) return;

        List<byte> order = BuildPredictedOrder();
        SlotZeroId = order.Count > 0 ? order[0] : byte.MaxValue;
    }

    // 予想する会議カードの並び。生存者を PlayerId 昇順、その後ろに死亡者を PlayerId 昇順。
    //
    // 見るのは GameData で、バニラ側の生死 (NetworkedPlayerInfo.IsDead) だけを使う。理由は 2 つ:
    // 並べるのがバニラなのでモッド側の生死 (IsAlive) とはずれうること、そして切断した客のカードは
    // 会議画面に残り死亡者と同じ群の末尾に並ぶのに、PlayerControl の一覧からは消えてしまうこと
    // (どちらも 2026-09-14 に非モッド客の画面で実測)。
    private static List<byte> BuildPredictedOrder()
    {
        var alive = new List<byte>();
        var dead = new List<byte>();

        if (GameData.Instance == null) return alive;

        Il2CppSystem.Collections.Generic.List<NetworkedPlayerInfo> all = GameData.Instance.AllPlayers;

        for (int index = 0; index < all.Count; index++)
        {
            NetworkedPlayerInfo info = all[index];

            // 200 以上は CustomNetObject が使う循環 ID で、会議カードにはならない。
            if (info == null || info.PlayerId >= 200) continue;

            if (info.IsDead || info.Disconnected) dead.Add(info.PlayerId);
            else alive.Add(info.PlayerId);
        }

        alive.Sort();
        dead.Sort();
        alive.AddRange(dead);

        return alive;
    }

    // 名前へヘッダーを足す。相乗り先でなければ、また予算に収まらなければ元の名前をそのまま返す。
    // 🔴 公式鯖の名前クランプは末尾を切るので、ヘッダーは必ず先頭に置く。ただし足した結果
    //    予算を超えると、今度は役職マークや接尾辞の側が黙って消える。役職の情報の方が大事なので、
    //    収まらないときはヘッダーを出さない。
    public static string Apply(string name, byte targetPlayerId)
    {
        string prefix = GetNamePrefix(targetPlayerId);
        if (prefix.Length == 0) return name;

        if (GameStates.CurrentServerType == GameStates.ServerType.Vanilla
            && System.Text.Encoding.UTF8.GetByteCount(prefix) + System.Text.Encoding.UTF8.GetByteCount(name) > CustomRpcSenderExtensions.EffectiveNameBudget)
        {
            // 黙って諦めると「一番飾りの多い人だけヘッダーが出ない」状態が原因不明のまま残る。
            // 会議 1 回につき 1 度だけ記録する。
            if (LastBudgetDropDay != GetDay())
            {
                LastBudgetDropDay = GetDay();
                Logger.Warn($"Meeting header dropped for player {targetPlayerId}: name is already {System.Text.Encoding.UTF8.GetByteCount(name)}B of the {CustomRpcSenderExtensions.EffectiveNameBudget}B budget", "MeetingHeader");
            }

            return name;
        }

        return prefix + name;
    }

    // 名前の先頭に差し込む文字列。相乗り先でなければ空。
    private static string GetNamePrefix(byte targetPlayerId)
    {
        if (!Enabled || targetPlayerId != SlotZeroId) return string.Empty;

        string dayLine = ShowDay.GetBool() ? BuildDayText(NameDaySize) : string.Empty;
        string causeLine = ShowCause.GetBool() ? BuildCauseText(NameCauseSize) : string.Empty;

        if (dayLine.Length == 0 && causeLine.Length == 0) return string.Empty;

        // 先頭の改行だけは通常の行送りで入れる (ここで塊が 1 行ぶん下がり、名前の下駄でそれを戻す)。
        var prefix = "\n<line-height=0>";

        if (dayLine.Length > 0) prefix += $"<voffset={DayOffset}>{dayLine}\n";
        if (causeLine.Length > 0) prefix += $"<voffset={CauseOffset}>{causeLine}\n";

        // 行送りを戻してから名前本体へ。戻さないと名前の複数行が 1 行に重なる。
        return prefix + $"<line-height=100%><voffset={NameOffset}>";
    }

    // モッドクライアント向けの自前 TMP に出す文字列 (相乗りと違って行送りは普通でよい)。
    public static string GetOverlayText()
    {
        if (!Enabled) return string.Empty;

        string dayLine = ShowDay.GetBool() ? BuildDayText(OverlayDaySize) : string.Empty;
        string causeLine = ShowCause.GetBool() ? BuildCauseText(OverlayCauseSize) : string.Empty;

        if (dayLine.Length == 0) return causeLine;
        if (causeLine.Length == 0) return dayLine;

        return $"{dayLine}\n{causeLine}";
    }

    private static int GetDay()
    {
        // ホストは通報を受けた時点で確定させた値を使う。ホスト以外のモッド客はその通知を受け取らないので、
        // MeetingHud.Start の Prefix で既に加算済みの会議番号をそのまま使う (同じ数になる)。
        return Day > 0 ? Day : MeetingStates.MeetingNum;
    }

    private static string BuildDayText(string size)
    {
        return $"<size={size}><{DayColor}>{string.Format(GetString("MeetingHeader.Day"), GetDay())}</color></size>";
    }

    private static string BuildCauseText(string size)
    {
        MeetingCause cause = Cause;

        if (cause == MeetingCause.None)
        {
            // ホスト以外のモッド客は通報の中身を知らない。死体があれば死体通報だと分かるが、
            // 死体が無いときは緊急ボタンと強制会議の区別が付かない。役職が起こした会議を
            // 「誰かがボタンを押した」と出すと濡れ衣になるので、分からないときは理由を出さない。
            if (MeetingStates.ReportTarget == null) return string.Empty;

            cause = MeetingCause.BodyReport;
        }

        string body = cause switch
        {
            MeetingCause.BodyReport => string.Format(GetString("MeetingHeader.BodyReport"), GetVictimName()),
            MeetingCause.Forced => GetString("MeetingHeader.Forced"),
            _ => GetString("MeetingHeader.EmergencyButton")
        };

        Color32 markColor = ReporterId != byte.MaxValue && Main.PlayerColors.TryGetValue(ReporterId, out Color32 reporterColor)
            ? reporterColor
            : new Color32(byte.MaxValue, byte.MaxValue, byte.MaxValue, byte.MaxValue);

        return $"<size={size}><u>{Utils.ColorString(markColor, "★")}<#ffffff>{body}</color></u></size>";
    }

    // 名前欄に収まる長さに丸めた死亡者の名前。長い名前をそのまま入れると行ごと右端で切れて読めなくなる。
    private const int MaxVictimNameLength = 10;

    private static string GetVictimName()
    {
        string name = null;

        if (VictimId != byte.MaxValue) Main.AllPlayerNames.TryGetValue(VictimId, out name);

        if (string.IsNullOrEmpty(name))
        {
            NetworkedPlayerInfo target = MeetingStates.ReportTarget;
            if (target != null) name = target.PlayerName;
        }

        if (string.IsNullOrEmpty(name)) return GetString("ReportReason.AnonymousVictim");

        return name.Length > MaxVictimNameLength ? name[..MaxVictimNameLength] + "…" : name;
    }

    // モッドクライアントは NotifyRoles の名前書き換えを受け取らない (Standard では早期 return される) ので、
    // 同じ文言を自前の TMP で会議画面へ出す。ホスト自身もここを通る。
    //
    // 相乗り側と違って、ここは予想した相手ではなく実際に先頭に並んだカード (playerStates[0]) に直接付ける。
    // ローカルには本物の並びがあるのだから、予想を挟む理由が無い。結果として、予想が外れている間だけ
    // モッド客と非モッド客でヘッダーの付くカードが食い違う (そのズレは VerifySlotOrder が記録する)。
    public static void CreateOverlay(MeetingHud meetingHud)
    {
        if (!Enabled || !meetingHud) return;
        if (meetingHud.playerStates == null || meetingHud.playerStates.Length == 0) return;

        string text = GetOverlayText();
        if (text.Length == 0) return;

        PlayerVoteArea anchor = meetingHud.playerStates[0];
        if (!anchor || !anchor.NameText) return;

        TextMeshPro header = Object.Instantiate(anchor.NameText, anchor.transform, true);
        header.gameObject.name = "MeetingHeader";

        // 名前欄には役職テキストが子として生えているので、複製するとその写しまで付いてくる。
        // 見出しの下に役職名の幽霊が出るため、複製の子は捨てる。
        for (int index = header.transform.childCount - 1; index >= 0; index--)
            Object.Destroy(header.transform.GetChild(index).gameObject);
        // 先頭カードの真上に中央揃えで置く。横位置はカードの名前欄から借りるので、
        // カードの内部レイアウトが変わっても中央からずれない。
        header.transform.localPosition = new(anchor.NameText.transform.localPosition.x, 0.64f, -1f);
        header.transform.localScale = Vector3.one;
        header.fontSize = 1.9f;
        header.alignment = TextAlignmentOptions.Center;
        header.enableWordWrapping = false;
        header.color = Color.white;
        header.text = text;
        header.ForceMeshUpdate();
    }

    // 相乗り先の予想が当たっているかを、実際に並んだカードと突き合わせる。会議 1 回につき 1 度通り、
    // 外れたときだけ記録する。並びの前提が違っていることに気付ける唯一の手掛かりなので消さないこと。
    // 死体が複数あり切断者も居る会議でこそ差が出る (第 1 会議で一致しても述語の証拠にはならない)。
    public static void VerifySlotOrder(MeetingHud meetingHud)
    {
        if (!AmongUsClient.Instance.AmHost || !meetingHud) return;
        if (meetingHud.playerStates == null || meetingHud.playerStates.Length == 0) return;

        // 🔴 playerStates の配列順はカードの生成順であって、画面の並びではない (2026-09-14 実測:
        //    配列は [0,1,2,3] のまま、画面は [0,3,1,2])。位置で並べ直さないと別物を照合してしまう。
        var cards = new List<PlayerVoteArea>(meetingHud.playerStates.Length);

        for (int index = 0; index < meetingHud.playerStates.Length; index++)
        {
            PlayerVoteArea pva = meetingHud.playerStates[index];
            if (pva) cards.Add(pva);
        }

        if (cards.Count == 0) return;

        // 上の行が先、同じ行なら左が先。
        cards.Sort((a, b) =>
        {
            Vector3 pa = a.transform.localPosition;
            Vector3 pb = b.transform.localPosition;

            if (Mathf.Abs(pa.y - pb.y) > 0.05f) return pb.y.CompareTo(pa.y);

            return pa.x.CompareTo(pb.x);
        });

        var actual = new List<byte>(cards.Count);
        for (int index = 0; index < cards.Count; index++) actual.Add(cards[index].PlayerId);

        // 先頭が当たっているかが本題だが、並び順の前提そのもの (生存を PlayerId 昇順 → 死亡を PlayerId 昇順)
        // が正しいかは列全体を見ないと分からない。先頭だけ見ていると、死者が先頭に来ない限りずっと一致し続ける。
        List<byte> predicted = BuildPredictedOrder();

        bool headMatches = actual[0] == SlotZeroId;
        bool orderMatches = predicted.Count == actual.Count;

        for (int index = 0; orderMatches && index < predicted.Count; index++)
            if (predicted[index] != actual[index])
                orderMatches = false;

        if (headMatches && orderMatches) return;

        Logger.Warn($"Meeting header slot mismatch: head predicted {SlotZeroId} actual {actual[0]}, order predicted [{string.Join(",", predicted)}] actual [{string.Join(",", actual)}]", "MeetingHeader");
    }
}
