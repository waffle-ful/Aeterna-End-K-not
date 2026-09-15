using System.Collections.Generic;
using System.Linq;
using EndKnot.Modules;
using EndKnot.Patches;
using static EndKnot.Options;
using static EndKnot.Translator;

namespace EndKnot.Roles;

// ============================================================
// Shibboleth (合言葉) — ImpOnly アドオン
//
// コンセプト:
//   会議が始まると、保持者だけに私信で「合言葉」が1つ渡る。その会議の中で合言葉を含む発言を
//   一度でもすれば解除。していなければ会議終了後に死ぬ。
//
//   合言葉はロビー言語のプールからランダムに選ぶ。プールが無い言語 (en/ja 以外の多くの言語) では
//   「言えない言葉」を強制することになるので、そのロビーにはこのアドオンを配らない
//   (CustomRolesHelper.CheckAddonConflict の HasTranslation ガードで止める)。
// ============================================================
public class Shibboleth : IAddon
{
    // 14800 台は Seer が SetupAdtRoleOptions で先取りしている。Option Id は単一のグローバル辞書を
    // 共有するので、衝突した側は例外もログも無しにメニューから消える。
    private const int Id = 14120;

    private static OptionItem ShibbolethFirstMeetingExempt;

    // 保持者 -> この会議の合言葉 (小文字化済み・一致判定用)。会議ごとに配り直す。
    private static readonly Dictionary<byte, string> AssignedWord = [];

    // 合言葉を言い終えた保持者。会議ごとにクリアされる。
    private static readonly HashSet<byte> Fulfilled = [];

    public AddonTypes Type => AddonTypes.ImpOnly;

    public void SetupCustomOption()
    {
        SetupAdtRoleOptions(Id, CustomRoles.Shibboleth, canSetNum: true);

        ShibbolethFirstMeetingExempt = new BooleanOptionItem(Id + 10, "ShibbolethFirstMeetingExempt", true, TabGroup.Addons)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Shibboleth]);
    }

    public static void Init()
    {
        AssignedWord.Clear();
        Fulfilled.Clear();
    }

    // 会議開始のたびに合言葉を配り直す (PlayerControlPatch.cs の AfterReportTasks から呼ばれる)。
    public static void OnMeetingStart()
    {
        AssignedWord.Clear();
        Fulfilled.Clear();

        if (MeetingStates.FirstMeeting && ShibbolethFirstMeetingExempt.GetBool()) return;

        string pool = GetString("Shibboleth.WordPool", GetEffectiveLang());
        string[] words = pool.Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries);
        if (words.Length == 0) return;

        foreach (PlayerControl pc in Main.EnumerateAlivePlayerControls())
        {
            if (!pc.Is(CustomRoles.Shibboleth)) continue;

            string word = words.RandomElement();
            AssignedWord[pc.PlayerId] = word.ToLower();

            // OnMeetingStart は AfterReportTasks (会議 UI がまだ存在しない時点) から呼ばれるので、
            // 私信そのものは AfterReportTasks が黒幕を解く 3 秒後 (RemoveBlackout と同じ間合い) まで遅らせる。
            byte holderId = pc.PlayerId;
            LateTask.New(() => Utils.SendMessage(string.Format(GetString("Shibboleth.WordAssigned"), word), holderId, GetString("Shibboleth.Title"), importance: MessageImportance.High), 3f, "Shibboleth Word Msg");
        }
    }

    // 会議中の発言を全部ここに通す (ChatCommandPatch の送信側・受信側の両方から)。
    public static void OnAnyoneChat(PlayerControl speaker, string text)
    {
        if (!AmongUsClient.Instance.AmHost) return;
        if (!GameStates.IsMeeting) return;
        if (!speaker || speaker.PlayerId >= 200 || !speaker.IsAlive()) return;
        if (!AssignedWord.TryGetValue(speaker.PlayerId, out string word)) return;
        if (Fulfilled.Contains(speaker.PlayerId)) return;
        if (string.IsNullOrWhiteSpace(text) || text.TrimStart().StartsWith('/')) return;

        if (!text.ToLower().Contains(word)) return;

        Fulfilled.Add(speaker.PlayerId);
        Utils.SendMessage(GetString("Shibboleth.Fulfilled"), speaker.PlayerId, GetString("Shibboleth.Title"), importance: MessageImportance.Low);
    }

    // 会議が閉じる直前 (CheckForDeathOnExile の Vote 分岐) から呼ばれる。ここより後ろでは
    // Main.AfterMeetingDeathPlayers は既に確定・消化済みで手遅れになる。
    // exileIds = この投票で追放される人。通常追放は AfterMeetingDeathPlayers を経由せず
    // まだ生存扱いのままここへ来るので、除外しないと同じ人が二重に死亡処理される
    // (死因が Shibboleth に化け、AfterPlayerDeathTasks の副作用が 2 度走る)。
    public static void OnExile(byte[] exileIds)
    {
        if (AssignedWord.Count == 0) return;

        byte[] deathList = [.. AssignedWord.Keys
            .Where(id => !Fulfilled.Contains(id) && !Main.AfterMeetingDeathPlayers.ContainsKey(id) && !exileIds.Contains(id))
            .Where(id =>
            {
                PlayerControl pc = id.GetPlayer();
                return pc && pc.IsAlive();
            })];

        CheckForEndVotingPatch.TryAddAfterMeetingDeathPlayers(PlayerState.DeathReason.Shibboleth, deathList);
    }
}
