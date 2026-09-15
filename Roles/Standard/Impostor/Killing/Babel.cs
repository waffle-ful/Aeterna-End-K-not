using System;
using System.Collections.Generic;
using System.Linq;
using EndKnot.Modules;
using static EndKnot.Translator;

namespace EndKnot.Roles;

// ============================================================
// Babel (バベル) — Impostor/Killing
//
// コンセプト:
//   会議中に /babel <n> で「発言の縛り (文字種ルール)」を仕掛ける。WordKiller の双子で、
//   述語が「リテラル語を含む」から「文字種クラス」に変わっただけ。会議が終わるまで縛りは
//   有効で、途中で何度でも切り替えられる (時間制限ではなくモード制)。
//   縛りは会議全体で1つ — 複数の Babel 保持者が居ても「最後に切り替えた人の縛り」が有効で、
//   違反者を処刑するのもその切替者 (同時に複数の縛りが走ると誰にも理由が分からなくなるため)。
//
//   ひらがな/カタカナ/漢字の禁止・必須は日本語ロビーでしか意味を持たない
//   (他言語では文字体系縛りが「誰も踏まない」か「全員死ぬ」かの二択に自壊する)。
//   ラテン文字の禁止・必須も非ラテン圏だけに絞る。モードは Translator.GetEffectiveLang() で自動的に
//   出し分ける (ホストオプションにはしない — 選択式にすると自壊モードを選べてしまう)。
//
// 会議中キルの機構・ガード列は WordKiller からそのまま移す。
// ============================================================
public class Babel : RoleBase
{
    private const int Id = 707400;

    public static bool On;

    private static OptionItem KillCooldown;
    private static OptionItem AbilityUseLimit;
    private static OptionItem AbilityUseGainWithEachKill;
    private static OptionItem MinLength;
    private static OptionItem GraceCount;
    private static OptionItem NotifyInterval;
    private static OptionItem SwitchGraceSeconds;
    private static OptionItem LengthLimitValue;
    private static OptionItem CanKillImpostors;

    // 会議全体で1つの縛り。0 = 非適用。
    private static int CurrentMode;
    private static byte CurrentSetter = byte.MaxValue;

    // 切替直後の恩赦窓 (この時刻までの違反は無告知・回数不消費で見逃す)。
    private static long GraceUntilTs;

    // 直近のアナウンス時刻。定期通知の間隔計測に使う。
    private static long LastNotifyTs;

    // 送信下限に阻まれて出せなかった切替アナウンスを、この時刻に必ず出す (0 = 保留なし)。
    // 落としたままにすると「告知されていない縛り」で死ぬ窓ができる。
    private static long PendingAnnounceTs;

    // 話者ごとの見逃し回数。モード切替のたびにリセットする (前の縛りの違反を新しい縛りに持ち越さない)。
    private static readonly Dictionary<byte, int> ViolationCounts = [];

    // キル予約済みのターゲット。WordKiller と同じブレーキ (Censor の LateTask 完了まで多重発火を防ぐ)。
    private static readonly HashSet<byte> PendingKill = [];

    // 縛りモードの一覧。Available はそのロビー言語 (SupportedLangs の生 int 値) で選べるかどうか。
    private static readonly HashSet<int> NonLatinScriptLangIds = [11, 13, 14, 4, 5, 109, 107, 104, 103]; // ja, zh_CN, zh_TW, ko, ru, be, sr, ar, fa

    private static bool IsJapaneseLang(int langId) => langId == 11;
    private static bool CanToggleLatin(int langId) => NonLatinScriptLangIds.Contains(langId);
    private static bool AlwaysAvailable(int langId) => true;

    private static readonly (int Id, string Key, Func<int, bool> Available)[] Modes =
    [
        (1, "Babel.Mode.NoHiragana", IsJapaneseLang),
        (2, "Babel.Mode.RequireHiragana", IsJapaneseLang),
        (3, "Babel.Mode.NoKatakana", IsJapaneseLang),
        (4, "Babel.Mode.RequireKatakana", IsJapaneseLang),
        (5, "Babel.Mode.NoHan", IsJapaneseLang),
        (6, "Babel.Mode.RequireHan", IsJapaneseLang),
        (7, "Babel.Mode.NoLatin", CanToggleLatin),
        (8, "Babel.Mode.RequireLatin", CanToggleLatin),
        (9, "Babel.Mode.NoDigit", AlwaysAvailable),
        (10, "Babel.Mode.RequireDigit", AlwaysAvailable),
        (11, "Babel.Mode.NoPunct", AlwaysAvailable),
        (12, "Babel.Mode.MaxLength", AlwaysAvailable),
        (13, "Babel.Mode.MinLength", AlwaysAvailable)
    ];

    public override bool IsEnable => On;

    public override void SetupCustomOption()
    {
        StartSetup(Id)
            .AutoSetupOption(ref KillCooldown, 30f, new FloatValueRule(0f, 180f, 0.5f), OptionFormat.Seconds)
            .AutoSetupOption(ref AbilityUseLimit, 4f, new FloatValueRule(0f, 30f, 1f), OptionFormat.Times)
            .AutoSetupOption(ref AbilityUseGainWithEachKill, 0f, new FloatValueRule(0f, 5f, 0.25f), OptionFormat.Times)
            .AutoSetupOption(ref MinLength, 3, new IntegerValueRule(1, 20, 1), OptionFormat.None)
            .AutoSetupOption(ref GraceCount, 1, new IntegerValueRule(0, 5, 1), OptionFormat.Times)
            .AutoSetupOption(ref NotifyInterval, 15, new IntegerValueRule(5, 60, 5), OptionFormat.Seconds)
            .AutoSetupOption(ref SwitchGraceSeconds, 3, new IntegerValueRule(0, 10, 1), OptionFormat.Seconds)
            .AutoSetupOption(ref LengthLimitValue, 10, new IntegerValueRule(3, 30, 1), OptionFormat.None)
            .AutoSetupOption(ref CanKillImpostors, false);
    }

    public override void Init()
    {
        On = false;
        CurrentMode = 0;
        CurrentSetter = byte.MaxValue;
        GraceUntilTs = 0;
        LastNotifyTs = 0;
        PendingAnnounceTs = 0;
        ViolationCounts.Clear();
        PendingKill.Clear();
    }

    public override void Add(byte playerId)
    {
        On = true;
        playerId.SetAbilityUseLimit(AbilityUseLimit.GetFloat());
    }

    public override void SetKillCooldown(byte id)
    {
        Main.AllPlayerKillCooldown[id] = KillCooldown.GetFloat();
    }

    // 会議ごとに縛りをリセットする契約 (WordKiller と同じ二重リセット)。
    public override void OnReportDeadBody()
    {
        ResetMeetingState();
    }

    public override void AfterMeetingTasks()
    {
        ResetMeetingState();
    }

    private static void ResetMeetingState()
    {
        CurrentMode = 0;
        CurrentSetter = byte.MaxValue;
        GraceUntilTs = 0;
        LastNotifyTs = 0;
        PendingAnnounceTs = 0;
        ViolationCounts.Clear();
        PendingKill.Clear();
    }

    // /babel コマンドの本体 (ChatCommandPatch から呼ばれる)。
    public static void SetMode(PlayerControl player, string[] args)
    {
        if (!player.Is(CustomRoles.Babel)) return;

        if (!player.IsAlive())
        {
            Utils.SendMessage(GetString("Babel.Dead"), player.PlayerId, importance: MessageImportance.Low);
            return;
        }

        // 判定は「プレイヤーが実際に読んでいる言語」で行う。GetUserTrueLang (OS カルチャ) だと
        // ModLanguage を設定したホストで「判定は日本語ロビー・告知は英語」の食い違いが起き、
        // 英語話者に『ひらがな必須』が飛んで全滅する。
        int langId = (int)GetEffectiveLang();

        if (args.Length < 2 || !int.TryParse(args[1], out int n))
        {
            SendModeList(player, langId);
            return;
        }

        if (n == 0)
        {
            if (CurrentMode == 0) return;

            CurrentMode = 0;
            CurrentSetter = byte.MaxValue;
            ViolationCounts.Clear();
            AnnounceOff();
            return;
        }

        (int Id, string Key, Func<int, bool> Available) target = Modes.FirstOrDefault(m => m.Id == n);

        if (target.Key == null || !target.Available(langId))
        {
            Utils.SendMessage(GetString("Babel.InvalidMode"), player.PlayerId, importance: MessageImportance.Low);
            return;
        }

        if (player.GetAbilityUseLimit() < 1f)
        {
            Utils.SendMessage(GetString("Babel.NoUsesLeft"), player.PlayerId, importance: MessageImportance.Low);
            return;
        }

        CurrentMode = n;
        CurrentSetter = player.PlayerId;
        ViolationCounts.Clear();

        // 恩赦は「告知が実際に出た時刻」から数える。下限に阻まれて先送りになった分も含めるので、
        // 告知より先に縛りが効いて理不尽に死ぬことがない。
        long announceAt = Math.Max(Utils.TimeStamp, LastNotifyTs + MinAnnounceInterval);
        GraceUntilTs = announceAt + SwitchGraceSeconds.GetInt();

        if (!Announce(true)) PendingAnnounceTs = announceAt;
        Logger.Info($"{player.GetNameWithRole().RemoveHtmlTags()} が縛りモード {n} を設定", "Babel");
    }

    private static void SendModeList(PlayerControl player, int langId)
    {
        var sb = new StringBuilder();
        sb.Append(GetString("Babel.ModeList.Header")).Append('\n');
        sb.Append("0: ").Append(GetString("Babel.ModeList.Off"));

        foreach ((int Id, string Key, Func<int, bool> Available) mode in Modes)
        {
            if (!mode.Available(langId)) continue;
            sb.Append('\n').Append(mode.Id).Append(": ").Append(ModeName(mode.Key));
        }

        Utils.SendMessage(sb.ToString(), player.PlayerId, GetString("Babel.Title"), importance: MessageImportance.Low);
    }

    // 定期通知 (MeetingHudUpdatePatch.Postfix から呼ばれる)。会議中は OnFixedUpdate が止まるのでここが窓。
    public static void PeriodicNotify()
    {
        if (!On || CurrentMode == 0 || !GameStates.IsMeeting) return;

        // 執行できない縛りを告知し続けない。仕掛けた本人が会議中に死ぬ / 使用回数が尽きると
        // OnAnyoneChat 側は何もしなくなるので、「効かないルール」を全員に見せ続けることになる。
        PlayerControl setter = Utils.GetPlayerById(CurrentSetter);

        if (!setter || !setter.IsAlive() || setter.GetAbilityUseLimit() < 1f)
        {
            CurrentMode = 0;
            CurrentSetter = byte.MaxValue;
            ViolationCounts.Clear();
            AnnounceOff();
            return;
        }

        // 先送りになった切替アナウンスは、間隔が明けた時点で定期通知より先に出す。
        if (PendingAnnounceTs > 0 && Utils.TimeStamp >= PendingAnnounceTs)
        {
            LastNotifyTs = 0;
            Announce();
            return;
        }

        Announce();
    }

    // 手動切替・定期通知の唯一の送信口。LastNotifyTs は書くだけでなく読んでから送る —
    // 切替は回数を消費しないので、下限を掛けないと連打で全員宛てブロードキャストが無制限に出る。
    private const int MinAnnounceInterval = 2;

    private static bool Announce(bool manual = false)
    {
        string key = Modes.FirstOrDefault(m => m.Id == CurrentMode).Key;
        if (key == null) return false;

        long now = Utils.TimeStamp;
        if (now - LastNotifyTs < (manual ? MinAnnounceInterval : NotifyInterval.GetInt())) return false;

        Utils.SendMessage(string.Format(GetString("Babel.ModeAnnounce"), ModeName(key)), 255, GetString("Babel.Title"), importance: MessageImportance.Low);
        LastNotifyTs = now;
        PendingAnnounceTs = 0;
        return true;
    }

    // モード 12/13 (N文字以内/以上) は表示にホスト設定の N を差し込む。他のモードは {0} を含まないので無視される。
    private static string ModeName(string key) => string.Format(GetString(key), LengthLimitValue.GetInt());

    private static void AnnounceOff()
    {
        long now = Utils.TimeStamp;
        if (now - LastNotifyTs < MinAnnounceInterval) return;

        Utils.SendMessage(GetString("Babel.ModeAnnounceOff"), 255, GetString("Babel.Title"), importance: MessageImportance.Low);
        LastNotifyTs = now;
        PendingAnnounceTs = 0;
    }

    // 会議中の発言を全部ここに通す (ChatCommandPatch の送信側・受信側の両方から)。
    public static void OnAnyoneChat(PlayerControl speaker, string text)
    {
        if (!On || !AmongUsClient.Instance.AmHost) return;
        if (!GameStates.IsMeeting || CurrentMode == 0) return;
        if (!speaker || speaker.PlayerId >= 200 || !speaker.IsAlive()) return;

        // 投票締め〜会議クローズの窓でキルすると、追放ワープアップの Reliable バーストと重なって
        // 公式鯖のキック帯に入る (WordKiller と同じガード)。
        if (MeetingHud.Instance && MeetingHud.Instance.state is MeetingHud.MeetingStates.Results or MeetingHud.MeetingStates.Proceeding) return;

        // 既にキル予約が入っている相手には二重に撃たない。
        if (PendingKill.Contains(speaker.PlayerId)) return;

        if (string.IsNullOrWhiteSpace(text) || text.TrimStart().StartsWith('/')) return;

        string trimmed = text.Trim();

        // 短い発言の免除。ただしモード13 (N文字以上必須) にこれを掛けると「一番短い違反だけ助かる」
        // 逆転が起きる (2文字は生存・5文字は死) ので、そのモードだけ免除しない。
        if (CurrentMode != 13 && trimmed.Length < MinLength.GetInt()) return;

        if (!CharClass.IsViolation(CurrentMode, trimmed, LengthLimitValue.GetInt())) return;

        // 切替直後の恩赦: まだ誰も新しい縛りを見ていない可能性があるので、回数を消費せず・無告知で見逃す。
        if (Utils.TimeStamp < GraceUntilTs) return;

        PlayerControl setter = Utils.GetPlayerById(CurrentSetter);
        if (!setter || setter.PlayerId == speaker.PlayerId || !setter.IsAlive()) return;
        if (setter.GetAbilityUseLimit() < 1f) return;
        if (!CanKillImpostors.GetBool() && speaker.Is(CustomRoleTypes.Impostor)) return;

        // 会議中キルを弾く既存の保護。弾かれたときは回数を消費せず、踏んだ側にも何も知らせない。
        if (Jailor.PlayerIdList.Exists(x => Main.PlayerStates[x].Role is Jailor { IsEnable: true } jl && jl.JailorTarget == speaker.PlayerId)) return;
        if (Medic.InProtect(speaker.PlayerId)) return;
        if (speaker.Is(CustomRoles.Pestilence)) return;

        int count = ViolationCounts.TryGetValue(speaker.PlayerId, out int c) ? c + 1 : 1;
        ViolationCounts[speaker.PlayerId] = count;

        int grace = GraceCount.GetInt();
        if (count <= grace)
        {
            Utils.SendMessage(string.Format(GetString("Babel.Forgiven"), grace - count), speaker.PlayerId, importance: MessageImportance.Low);
            return;
        }

        // 予約と回数消費は LateTask の外・同フレームで確定させる (WordKiller と同じブレーキの掛け方)。
        PendingKill.Add(speaker.PlayerId);
        setter.RpcRemoveAbilityUse();
        Censor(setter, speaker);
    }

    private static void Censor(PlayerControl killer, PlayerControl target)
    {
        string name = target.GetRealName();
        Logger.Info($"{killer.GetNameWithRole().RemoveHtmlTags()} が縛りで {target.GetNameWithRole().RemoveHtmlTags()} を処刑", "Babel");

        byte killerId = killer.PlayerId;
        byte targetId = target.PlayerId;

        LateTask.New(() =>
        {
            PlayerControl kp = Utils.GetPlayerById(killerId);
            PlayerControl tp = Utils.GetPlayerById(targetId);
            if (!tp || !tp.IsAlive()) return;

            Main.PlayerStates[targetId].deathReason = PlayerState.DeathReason.Babel;
            if (kp) tp.SetRealKiller(kp);
            tp.RpcGuesserMurderPlayer();

            Utils.AfterPlayerDeathTasks(tp, true);

            LateTask.New(() => Utils.SendMessage(string.Format(GetString("Babel.Violated"), name), 255, CustomRoles.Babel.ColoredTextByRole(GetString("Babel.Title")), importance: MessageImportance.High), 0.6f, "Babel Msg");
        }, 0.2f, "Babel Censor");
    }

    public override void OnMurder(PlayerControl killer, PlayerControl target)
    {
        if (killer.PlayerId == target.PlayerId) return;
        killer.SetAbilityUseLimit(killer.GetAbilityUseLimit() + AbilityUseGainWithEachKill.GetFloat());
    }

    // GetSuffix で今の縛りを表示することは意図的にしていない。縛りは全員へ定期通知済みなので、
    // 表示を追加すると誰が Babel かの手がかりを増やすだけになる (WordKiller と同じ判断)。
}
