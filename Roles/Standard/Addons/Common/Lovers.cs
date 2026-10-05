using System.Collections.Generic;
using System.Linq;
using EndKnot.Gamemodes;
using static EndKnot.Options;

namespace EndKnot.Roles;

internal class Lovers : IAddon
{
    public static OptionItem LoverSpawnChances;
    public static OptionItem LoverKnowRoles;
    public static OptionItem LoverDieConsequence;
    public static OptionItem LoverSuicideTime;
    public static OptionItem ImpCanBeInLove;
    public static OptionItem CrewCanBeInLove;
    public static OptionItem NeutralCanBeInLove;
    public static OptionItem CovenCanBeInLove;
    public static OptionItem CrewLoversWinWithCrew;
    public static OptionItem LegacyLovers;
    public static OptionItem LovingImpostorSpawnChance;
    public static OptionItem LovingImpostorRoleForOtherImps;
    public static OptionItem PrivateChat;
    public static OptionItem GuessAbility;

    private static readonly string[] GuessModes =
    [
        "RoleOff", // 0
        "Untouched", // 1
        "RoleOn" // 2
    ];

    private static readonly string[] LIRole =
    [
        "Impostor",
        "RandomONImpRole",
        "LovingImpostor"
    ];

    private static readonly string[] Consequences =
    [
        "Nothing",
        "Suicide",
        "HalvedVision"
    ];

    private static readonly string[] SuicideTimes =
    [
        "Immediately",
        "WhenNextMeetingStarts",
        "WhenNextMeetingEnds"
    ];

    public static CustomRoles LovingImpostorRole;

    // 何組目のラバーズか (Main.LoversPlayers の並びとは独立 — 抜けや入れ替わりで相手が組み替わらないように)
    public static readonly Dictionary<byte, int> PairOf = [];

    // 後追い/視界半減が既に発動した組
    public static readonly HashSet<int> DeadPairs = [];

    private static bool SharedChatNoticeSent;

    public AddonTypes Type => AddonTypes.Mixed;

    public void SetupCustomOption()
    {
        const CustomRoles role = CustomRoles.Lovers;
        const int id = 16200;
        const CustomGameMode customGameMode = CustomGameMode.Standard;

        var spawnOption = new StringOptionItem(id, role.ToString(), RatesZeroOne, 0, TabGroup.Addons)
            .SetColor(Utils.GetRoleColor(role))
            .SetHeader(true)
            .SetGameMode(customGameMode) as StringOptionItem;

        var rateOption = new IntegerOptionItem(id + 2, "LoverSpawnChances", new(0, 100, 5), 50, TabGroup.Addons)
            .SetParent(spawnOption)
            .SetValueFormat(OptionFormat.Percent)
            .SetGameMode(customGameMode) as IntegerOptionItem;

        LoverSpawnChances = rateOption;

        LoverDieConsequence = new StringOptionItem(id + 3, "LoverDieConsequence", Consequences, 0, TabGroup.Addons)
            .SetParent(spawnOption)
            .SetGameMode(customGameMode);

        LoverSuicideTime = new StringOptionItem(id + 4, "LoverSuicideTime", SuicideTimes, 0, TabGroup.Addons)
            .SetParent(LoverDieConsequence)
            .SetValueFormat(OptionFormat.Seconds)
            .SetGameMode(customGameMode);

        LoverKnowRoles = new BooleanOptionItem(id + 5, "LoverKnowRoles", true, TabGroup.Addons)
            .SetParent(spawnOption)
            .SetGameMode(customGameMode);

        PrivateChat = new BooleanOptionItem(id + 6, "PrivateChat", false, TabGroup.Addons)
            .SetParent(spawnOption)
            .SetGameMode(customGameMode);

        CrewLoversWinWithCrew = new BooleanOptionItem(id + 8, "CrewLoversWinWithCrew", true, TabGroup.Addons)
            .SetParent(spawnOption)
            .SetGameMode(customGameMode);

        GuessAbility = new StringOptionItem(id + 9, "GuessAbility", GuessModes, 1, TabGroup.Addons)
            .SetParent(spawnOption)
            .SetGameMode(customGameMode);

        LegacyLovers = new BooleanOptionItem(id + 10, "LegacyLovers", false, TabGroup.Addons)
            .SetParent(spawnOption)
            .SetGameMode(customGameMode)
            .RegisterUpdateValueEvent((_, _, _) => new[] { ImpCanBeInLove, CrewCanBeInLove, NeutralCanBeInLove, CovenCanBeInLove }.Do(x => x.SetHidden(LegacyLovers.GetBool())))
            .SetRunEventOnLoad(true);

        LovingImpostorSpawnChance = new FloatOptionItem(id + 11, "LovingImpostorSpawnChance", new(0, 100, 5), 25, TabGroup.Addons)
            .SetParent(LegacyLovers)
            .SetValueFormat(OptionFormat.Percent)
            .SetGameMode(customGameMode);

        LovingImpostorRoleForOtherImps = new StringOptionItem(id + 12, "LIRoleForOtherImps", LIRole, 2, TabGroup.Addons)
            .SetParent(LovingImpostorSpawnChance)
            .SetGameMode(customGameMode);

        ImpCanBeInLove = new BooleanOptionItem(id + 13, "ImpCanBeInLove", true, TabGroup.Addons)
            .SetParent(spawnOption)
            .SetHidden(LegacyLovers.GetBool())
            .SetGameMode(customGameMode);

        CrewCanBeInLove = new BooleanOptionItem(id + 14, "CrewCanBeInLove", true, TabGroup.Addons)
            .SetParent(spawnOption)
            .SetHidden(LegacyLovers.GetBool())
            .SetGameMode(customGameMode);

        NeutralCanBeInLove = new BooleanOptionItem(id + 15, "NeutralCanBeInLove", true, TabGroup.Addons)
            .SetParent(spawnOption)
            .SetHidden(LegacyLovers.GetBool())
            .SetGameMode(customGameMode);

        CovenCanBeInLove = new BooleanOptionItem(id + 16, "CovenCanBeInLove", true, TabGroup.Addons)
            .SetParent(spawnOption)
            .SetHidden(LegacyLovers.GetBool())
            .SetGameMode(customGameMode);


        OptionItem countOption = new IntegerOptionItem(id + 1, "NumberOfLovers", new(1, 7, 1), 1, TabGroup.Addons)
            .SetParent(spawnOption)
            .SetGameMode(customGameMode);

        CustomRoleSpawnChances.Add(role, spawnOption);
        CustomRoleCounts.Add(role, countOption);

        CustomAdtRoleSpawnRate.Add(role, rateOption);
    }

    public static void Init()
    {
        try { LovingImpostorRole = Main.CustomRoleValues.Where(x => x.IsEnable() && x.IsImpostor() && x != CustomRoles.LovingImpostor && !x.RoleExist(true) && !CustomHnS.AllHnSRoles.Contains(x)).RandomElement(); }
        catch { LovingImpostorRole = CustomRoles.LovingImpostor; }
    }

    public static void ResetPairs()
    {
        PairOf.Clear();
        DeadPairs.Clear();
        SharedChatNoticeSent = false;
    }

    // 試合中のチャットは開いている全員に届くので組ごとには分けられない — 複数組の時は最初の解禁で一度だけ知らせる
    public static void SendSharedChatNotice()
    {
        if (SharedChatNoticeSent || !AmongUsClient.Instance.AmHost || !PrivateChat.GetBool() || ChatDuringGame.GetBool()) return;

        EnsurePairs();
        if (PairOf.Values.Distinct().Count() < 2) return;

        SharedChatNoticeSent = true;
        string title = Utils.ColorString(Utils.GetRoleColor(CustomRoles.Lovers), Translator.GetString("Lovers"));

        string text = Translator.GetString("LoversSharedChatNotice");
        if (EnableLoversChat.GetBool()) text += "\n" + Translator.GetString("LoversSharedChatNotice.Lc");

        foreach (PlayerControl lover in Main.LoversPlayers)
        {
            if (lover && lover.IsAlive())
                Utils.SendMessage(text, lover.PlayerId, title);
        }
    }

    // 組番号が付いていない者 (旧式ラバーズ・ホスト指定・後からの再構築) を並び順で2人ずつ組にする
    public static void EnsurePairs()
    {
        List<PlayerControl> lovers = Main.LoversPlayers;
        if (lovers.Count == 0) return;

        PlayerControl waiting = null;

        for (int i = 0; i < lovers.Count; i++)
        {
            PlayerControl pc = lovers[i];
            if (!pc || PairOf.ContainsKey(pc.PlayerId)) continue;

            if (!waiting)
            {
                waiting = pc;
                continue;
            }

            // 外れた組の番号 (DeadPairs に残る) を使い回さない
            int pair = 0;
            foreach (int used in PairOf.Values) pair = System.Math.Max(pair, used + 1);
            foreach (int used in DeadPairs) pair = System.Math.Max(pair, used + 1);
            PairOf[waiting.PlayerId] = pair;
            PairOf[pc.PlayerId] = pair;
            waiting = null;
        }
    }

    public static int PairIndexOf(byte id)
    {
        EnsurePairs();
        if (!PairOf.TryGetValue(id, out int pair)) return -1;

        List<PlayerControl> lovers = Main.LoversPlayers;

        for (int i = 0; i < lovers.Count; i++)
        {
            PlayerControl pc = lovers[i];
            if (pc && pc.PlayerId == id) return pair;
        }

        return -1;
    }

    public static List<PlayerControl> GetPair(int pair)
    {
        List<PlayerControl> result = [];
        if (pair < 0) return result;

        List<PlayerControl> lovers = Main.LoversPlayers;

        for (int i = 0; i < lovers.Count; i++)
        {
            PlayerControl pc = lovers[i];
            if (pc && PairOf.TryGetValue(pc.PlayerId, out int p) && p == pair) result.Add(pc);
        }

        return result;
    }

    public static int CountAlive(int pair)
    {
        var count = 0;
        List<PlayerControl> lovers = Main.LoversPlayers;

        for (int i = 0; i < lovers.Count; i++)
        {
            PlayerControl pc = lovers[i];
            if (pc && pc.IsAlive() && PairOf.TryGetValue(pc.PlayerId, out int p) && p == pair) count++;
        }

        return count;
    }

    public static PlayerControl GetPartner(byte id)
    {
        int pair = PairIndexOf(id);
        if (pair < 0) return null;

        List<PlayerControl> lovers = Main.LoversPlayers;

        for (int i = 0; i < lovers.Count; i++)
        {
            PlayerControl pc = lovers[i];
            if (pc && pc.PlayerId != id && PairOf.TryGetValue(pc.PlayerId, out int p) && p == pair) return pc;
        }

        return null;
    }

    public static bool ArePartners(byte a, byte b)
    {
        if (a == b) return false;
        int pair = PairIndexOf(a);
        return pair >= 0 && pair == PairIndexOf(b);
    }

    public static bool IsSamePairOrSelf(byte a, byte b)
    {
        int pair = PairIndexOf(a);
        return pair >= 0 && pair == PairIndexOf(b);
    }

    public static bool IsPairDead(byte id)
    {
        return DeadPairs.Contains(PairIndexOf(id));
    }

    // Amnesiac などで恋人の座が別人へ移る時に、組番号ごと引き継ぐ
    public static void ReplaceMember(byte oldId, PlayerControl newPc)
    {
        int pair = PairIndexOf(oldId);
        Main.LoversPlayers.RemoveAll(x => x.PlayerId == oldId);
        PairOf.Remove(oldId);
        Main.LoversPlayers.Add(newPc);
        if (pair >= 0) PairOf[newPc.PlayerId] = pair;
        Modules.RPC.SyncLoversPlayers();
    }

    public static void RemoveMember(byte id)
    {
        Main.LoversPlayers.RemoveAll(x => x.PlayerId == id);
        PairOf.Remove(id);
        Modules.RPC.SyncLoversPlayers();
    }
}
