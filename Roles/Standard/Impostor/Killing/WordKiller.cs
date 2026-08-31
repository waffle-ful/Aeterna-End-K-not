using System.Collections.Generic;
using EndKnot.Modules;
using static EndKnot.Translator;

namespace EndKnot.Roles;

// ============================================================
// WordKiller (ワードキラー) — Impostor/Killing
//
// コンセプト:
//   会議中に /word <ワード> で禁止ワードを1つ仕掛ける。
//   その会議の中でそのワードを含む発言をした生存者は即座に死ぬ。
//   ワードは会議ごとにリセットされるので、毎会議仕掛け直す。
//
// 会議中キルの機構は Judge (裁判官) と同型:
//   RpcGuesserMurderPlayer + SetRealKiller + AfterPlayerDeathTasks + 遅延アナウンス。
//
// ⚠️ ワード指定コマンドの秘匿は `/cmd` 前提。`/cmd` を付けずに打つと送信側のバニラが先に
//   全員へ生ブロードキャストするため、EHR は flood-clear (巨大空白で画面外へ押し出す) で
//   隠すことしかできない (Guess / Whisper と同じ既存の限界)。
//   コマンドは alwaysHidden で登録してあるので flood-clear 経路には自動で乗る。
// ============================================================
public class WordKiller : RoleBase
{
    private const int Id = 705800;

    public static bool On;

    private static OptionItem KillCooldown;
    private static OptionItem AbilityUseLimit;
    private static OptionItem AbilityUseGainWithEachKill;
    private static OptionItem CanKillImpostors;
    private static OptionItem MinWordLength;

    // 保持者ごとの禁止ワード (小文字化済み)。会議ごとにクリアされる。
    // チャットフックは static から叩かれるので、instance ではなく辞書で持つ (Judge の各種 Limit と同じ形)。
    private static readonly Dictionary<byte, string> Words = [];

    // キル予約済みのターゲット。実際の死亡は Censor() の LateTask (0.2秒後) まで遅れるので、
    // その窓の間に同じ人が禁止ワードを連投すると speaker.IsAlive() がまだ true のまま何度も通り、
    // 1発言1キルであるべきところが使用回数の尽きるまで多重発火する。
    // Judge が TrialLimitPerMeeting を LateTask の外で同期デクリメントしているのと同じ役割のブレーキ。
    private static readonly HashSet<byte> PendingKill = [];

    public override bool IsEnable => On;

    public override void SetupCustomOption()
    {
        StartSetup(Id)
            .AutoSetupOption(ref KillCooldown, 30f, new FloatValueRule(0f, 180f, 0.5f), OptionFormat.Seconds)
            .AutoSetupOption(ref AbilityUseLimit, 5f, new FloatValueRule(0f, 30f, 1f), OptionFormat.Times)
            .AutoSetupOption(ref AbilityUseGainWithEachKill, 0f, new FloatValueRule(0f, 5f, 0.25f), OptionFormat.Times)
            .AutoSetupOption(ref MinWordLength, 2, new IntegerValueRule(1, 10, 1), OptionFormat.None)
            .AutoSetupOption(ref CanKillImpostors, false);
    }

    public override void Init()
    {
        On = false;
        Words.Clear();
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

    // 会議ごとに仕掛け直す契約。OnReportDeadBody は会議が始まる前に走るので、
    // 前の会議で仕掛けたワードがそのまま次の会議に持ち越されることはない。
    public override void OnReportDeadBody()
    {
        Words.Clear();
        PendingKill.Clear();
    }

    // OnReportDeadBody は AfterReportTasks 内から呼ばれる (PlayerControlPatch.cs:1616) ので、
    // NoCheckStartMeeting 経由の会議 (PortalButton のポータル / Shuffler の赤丸 / Ghostbuttoner 等) でも
    // リセットは走る。それでも「会議ごとに必ず消える」を経路に依存せず成立させるため、閉じる側でも消す。
    public override void AfterMeetingTasks()
    {
        Words.Clear();
        PendingKill.Clear();
    }

    // /word コマンドの本体 (ChatCommandPatch から呼ばれる)。
    public static void SetWord(PlayerControl player, string[] args)
    {
        if (!player.Is(CustomRoles.WordKiller)) return;

        if (!player.IsAlive())
        {
            Utils.SendMessage(GetString("WordKiller.Dead"), player.PlayerId, importance: MessageImportance.Low);
            return;
        }

        if (player.GetAbilityUseLimit() < 1f)
        {
            Utils.SendMessage(GetString("WordKiller.NoUsesLeft"), player.PlayerId, importance: MessageImportance.Low);
            return;
        }

        // ワード自体に空白を含められるよう、コマンド名より後ろを全部つなぐ。
        string word = args.Length < 2 ? string.Empty : string.Join(' ', args[1..]).Trim();

        if (word.Length < MinWordLength.GetInt())
        {
            Utils.SendMessage(string.Format(GetString("WordKiller.WordTooShort"), MinWordLength.GetInt()), player.PlayerId, importance: MessageImportance.Low);
            return;
        }

        Words[player.PlayerId] = word.ToLower();
        Utils.SendMessage(string.Format(GetString("WordKiller.WordSet"), word), player.PlayerId, GetString("WordKiller.Title"));
        Logger.Info($"{player.GetNameWithRole().RemoveHtmlTags()} が禁止ワードを設定", "WordKiller");
    }

    // 会議中の発言を全部ここに通す (ChatCommandPatch の送信側・受信側の両方から)。
    public static void OnAnyoneChat(PlayerControl speaker, string text)
    {
        if (!On || !AmongUsClient.Instance.AmHost) return;
        if (!GameStates.IsMeeting || Words.Count == 0) return;
        if (!speaker || speaker.PlayerId >= 200 || !speaker.IsAlive()) return;

        // 投票終了〜会議クローズの窓でキルすると、追放ワープアップの Reliable バーストと重なって
        // 公式鯖のキック帯に入る。
        // `/` コマンド分岐 (ChatCommandPatch.cs) が持つのと同じ vote-state ガードを張る。
        if (MeetingHud.Instance && MeetingHud.Instance.state is MeetingHud.MeetingStates.Results or MeetingHud.MeetingStates.Proceeding) return;

        // 既にキル予約が入っている相手には二重に撃たない (使用回数の多重消費とアナウンス連投の防止)。
        if (PendingKill.Contains(speaker.PlayerId)) return;

        // コマンドは判定対象外。ワードキラー同士が互いのワードを踏んで事故るのを防ぐのが主目的
        // (プレイヤーが `/` を付けて回避できる穴は残るが、そもそもこの機構を知らない側は使えない)。
        if (string.IsNullOrWhiteSpace(text) || text.TrimStart().StartsWith('/')) return;

        string lowered = text.ToLower();

        foreach ((byte killerId, string word) in Words)
        {
            if (killerId == speaker.PlayerId) continue;
            if (word.Length == 0 || !lowered.Contains(word)) continue;

            PlayerControl killer = Utils.GetPlayerById(killerId);
            if (!killer || !killer.IsAlive()) continue;
            if (killer.GetAbilityUseLimit() < 1f) continue;
            if (!CanKillImpostors.GetBool() && speaker.Is(CustomRoleTypes.Impostor)) continue;

            // 会議中キルを弾く既存の保護。Judge と同じ判定を使い、弾かれたときは
            // 使用回数を消費しない (踏んだ側にも何も知らせない — 何が起きたか悟らせないため)。
            if (Jailor.PlayerIdList.Exists(x => Main.PlayerStates[x].Role is Jailor { IsEnable: true } jl && jl.JailorTarget == speaker.PlayerId)) continue;
            if (Medic.InProtect(speaker.PlayerId)) continue;
            if (speaker.Is(CustomRoles.Pestilence)) continue;

            // 予約と回数消費は LateTask の外・同フレームで確定させる (Judge と同じブレーキの掛け方)。
            PendingKill.Add(speaker.PlayerId);
            killer.RpcRemoveAbilityUse();
            Censor(killer, speaker);
            return;
        }
    }

    private static void Censor(PlayerControl killer, PlayerControl target)
    {
        string name = target.GetRealName();
        Logger.Info($"{killer.GetNameWithRole().RemoveHtmlTags()} が禁止ワードで {target.GetNameWithRole().RemoveHtmlTags()} を処刑", "WordKiller");

        // vanilla のチャット処理を終わらせてからキルする (Judge / Exorcist と同じ遅延)。
        byte killerId = killer.PlayerId;
        byte targetId = target.PlayerId;

        LateTask.New(() =>
        {
            PlayerControl kp = Utils.GetPlayerById(killerId);
            PlayerControl tp = Utils.GetPlayerById(targetId);
            if (!tp || !tp.IsAlive()) return;

            Main.PlayerStates[targetId].deathReason = PlayerState.DeathReason.Censored;
            if (kp) tp.SetRealKiller(kp);
            tp.RpcGuesserMurderPlayer();

            Utils.AfterPlayerDeathTasks(tp, true);

            // 誰が死んだかだけ伝え、ワードそのものは明かさない (次の会議も同じ手が使えるように)。
            LateTask.New(() => Utils.SendMessage(string.Format(GetString("WordKiller.Censored"), name), 255, CustomRoles.WordKiller.ColoredTextByRole(GetString("WordKiller.Title")), importance: MessageImportance.High), 0.6f, "WordKiller Msg");
        }, 0.2f, "WordKiller Censor");
    }

    public override void OnMurder(PlayerControl killer, PlayerControl target)
    {
        if (killer.PlayerId == target.PlayerId) return;
        killer.SetAbilityUseLimit(killer.GetAbilityUseLimit() + AbilityUseGainWithEachKill.GetFloat());
    }

    // GetSuffix で仕掛けたワードを表示することは意図的にしていない。
    // ワードが存在するのは会議の最中だけであり、会議中の名前表示が非モッド客に対して
    // 本当に seer 単位で配られているかを検証できていない。もし共有名で出れば
    // ワードと役職の両方が一発で漏れて能力が死ぬ。
    // 仕掛けた内容は SetWord の確認メッセージで本人に伝えてあるので、表示は不要。
}
