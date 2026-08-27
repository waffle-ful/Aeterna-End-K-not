using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace EndKnot.Modules;

// ロビー / ゲーム中に、プレイヤーの外見をまるごと入れ替えるお遊び機能。
//
// 「帽子は A さん・スキンは B さん」という部品単位のミックスではなく、Doppelganger と同じ
// 「名前も色も装備もまるごと別人になる」入れ替えを、一対一のシャッフル (巡回置換) で全員に掛ける。
// 各外見がちょうど 1 人ずつ存在する状態が保たれるので、「同じ顔が 3 人いる」事故が起きない。
//
// ⚠️ 外見と名前はロビーへ持ち越される永続データなので、復元は Patches/OutroPatch.cs の
// 無条件復元とセットで成立する ([[project-persistent-player-data-restore-on-game-end]])。
// この台帳を死亡クリーンアップで消してはいけない (消すと試合終了時に戻す先が無くなる)。
public static class OutfitShuffle
{
    public const int OptionIdBase = 44750;

    // 公式鯖の fan-out キック安全実績域は「宛先数 × 体数/秒 ≤ 20」
    // ([[project_fanout_burst_kick_brackets]])。全員シャッフルは 1 人 1 回の Data ブロードキャストなので
    // 送出間隔 = 宛先数 / 20 秒 に開けるとちょうどこの実績域に収まる。
    private const float SafeNestsPerSecond = 20f;
    private const float MinInterval = 0.25f;

    // 自動シャッフルをイントロ破棄から何秒待って撃つか。
    private const float AutoShuffleDelay = 14f;

    // 会議明けに外見の一斉入れ替えを避ける秒数 (CustomNetObject.DeferredSpawnBaseDelay と同じ 10 秒規約)。
    private const float PostMeetingSweepWindow = 10f;

    // 分散送出中に会議が始まったときの再試行間隔。
    private const float MeetingRetryInterval = 3f;

    private static float LastMeetingEndTime = -1f;

    public static OptionItem Enabled;
    public static OptionItem AutoOnGameStart;
    public static OptionItem SwapLevel;
    public static OptionItem AudienceShuffleOneEnabled;
    public static OptionItem AudienceShuffleOnePrice;
    public static OptionItem AudienceShuffleAllEnabled;
    public static OptionItem AudienceShuffleAllPrice;

    // 本物の外見台帳。ClearLedger() でのみクリアする。
    private static readonly Dictionary<byte, NetworkedPlayerInfo.PlayerOutfit> OriginalOutfits = [];
    private static readonly Dictionary<byte, uint> OriginalLevels = [];

    // OutroPatch の per-player 復元ループが積む分。ループ終了後に ClearLedger がまとめて分散送出する。
    private static readonly List<Snapshot> PendingRestore = [];

    // 分散送出の LateTask がゲームを跨いで誤爆しないようにする世代トークン。
    private static int Generation;

    public static void SetupCustomOption()
    {
        Enabled = new BooleanOptionItem(OptionIdBase + 0, "OutfitShuffleEnabled", true, TabGroup.SystemSettings)
            .SetHeader(true);

        AutoOnGameStart = new BooleanOptionItem(OptionIdBase + 1, "OutfitShuffleAutoOnGameStart", false, TabGroup.SystemSettings)
            .SetParent(Enabled);

        SwapLevel = new BooleanOptionItem(OptionIdBase + 2, "OutfitShuffleSwapLevel", true, TabGroup.SystemSettings)
            .SetParent(Enabled);

        AudienceShuffleOneEnabled = new BooleanOptionItem(OptionIdBase + 3, "AudienceShuffleOneEnabled", true, TabGroup.SystemSettings)
            .SetParent(Audience.AudienceOptions.Enabled);

        AudienceShuffleOnePrice = new IntegerOptionItem(OptionIdBase + 4, "AudienceShuffleOnePrice", new(0, 2000, 10), 120, TabGroup.SystemSettings)
            .SetParent(AudienceShuffleOneEnabled);

        AudienceShuffleAllEnabled = new BooleanOptionItem(OptionIdBase + 5, "AudienceShuffleAllEnabled", true, TabGroup.SystemSettings)
            .SetParent(Audience.AudienceOptions.Enabled);

        AudienceShuffleAllPrice = new IntegerOptionItem(OptionIdBase + 6, "AudienceShuffleAllPrice", new(0, 5000, 10), 400, TabGroup.SystemSettings)
            .SetParent(AudienceShuffleAllEnabled);
    }

    // Patches/OnGameStartedPatch.cs の一括リセットブロックから呼ばれる。
    // ⚠️ ここでは台帳を**消さない**。ロビーで掛けたシャッフルはそのまま試合へ持ち込む仕様なので、
    // 消すと「入れ替わったまま戻す先が無い」状態で試合が始まってしまう。
    // 台帳を空にするのは「戻し終わった時」だけ (ClearLedger) — 試合が異常終了して台帳が残った場合も、
    // 実際の見た目が入れ替わったままなので、次の試合終了時に戻すのが正しい動作になる。
    public static void OnGameStart()
    {
        LastMeetingEndTime = -1f;

        if (OriginalOutfits.Count > 0) Logger.Info($"Carrying {OriginalOutfits.Count} shuffled outfit(s) into the game", "OutfitShuffle");
    }

    // Patches/IntroPatch.cs の IntroCutsceneDestroyPatch から呼ばれる。
    // ⚠️ 起点はゲーム開始ではなくイントロ破棄。ゲーム開始から数えると、待ち時間がまるごと
    // イントロの尺に食われて結局は開始直後のバーストへ着弾する。
    public static void OnIntroFinished()
    {
        if (!Enabled.GetBool() || !AutoOnGameStart.GetBool()) return;

        // 公式鯖のキックに対して効いているのは「待つこと」ではなく DispatchStaggered の間引き
        // (同時本数を上げない) の方。固定待ちは対策として無効というのが実測の結論
        // ([[project_p6_game_start_spawn_window]])。この遅延は役職一斉割り当てのバーストと
        // 単純に時間帯をずらすためだけのもの。
        int generation = Generation;
        LateTask.New(() =>
        {
            if (generation != Generation || !GameStates.InGame || GameStates.IsMeeting) return;

            ShuffleAll(out _);
        }, AutoShuffleDelay, "OutfitShuffle.AutoShuffle", false);
    }

    // Patches/ExilePatch.cs の AfterMeetingTasks から呼ばれる。会議明けスイープ窓の起点。
    public static void OnAfterMeeting()
    {
        LastMeetingEndTime = Time.realtimeSinceStartup;
    }

    // 会議明けは追放スイープ (SetRole 全員分 + Desync + ReactorFlash + NotifyRoles) とレートゲートの
    // ドレインが task phase 開始後 ~10 秒続く。この窓に外見の一斉入れ替えを重ねると合算 nests が
    // キック域に達する (2026-08-03 実キック・BUG-20260803-07。CNO の DeferredSpawnBaseDelay と同じ規約)。
    public static bool InPostMeetingSweep => LastMeetingEndTime >= 0f && Time.realtimeSinceStartup - LastMeetingEndTime < PostMeetingSweepWindow;

    // Patches/PlayerJoinAndLeftPatch.cs の OnGameJoinedPatch から呼ばれる。
    // ⚠️ ClearLedger と違って**送信せずに捨てる**。新しいロビーでは PlayerId が別人に振り直されて
    // いるので、積んである復元を撃つと無関係な人を前の部屋の誰かに変えてしまう。
    public static void ResetForNewLobby()
    {
        OriginalOutfits.Clear();
        OriginalLevels.Clear();
        PendingRestore.Clear();
        LastMeetingEndTime = -1f;
        Generation++;
    }

    // 切断した本人の分は持っていても戻す相手が居ないので落とす。
    public static void OnPlayerLeft(byte id)
    {
        OriginalOutfits.Remove(id);
        OriginalLevels.Remove(id);
    }

    public static bool IsActive => OriginalOutfits.Count > 0;

    // ---- 入れ替えの実行 ----

    // 全員を一対一でシャッフルする (巡回置換なので固定点なし・重複なし)。
    public static bool ShuffleAll(out string error)
    {
        error = null;

        if (!AmongUsClient.Instance.AmHost)
        {
            error = "not host";
            return false;
        }

        if (!Enabled.GetBool())
        {
            error = Translator.GetString("OutfitShuffle.Disabled");
            return false;
        }

        // 会議中は外す。MeetingHud のプレイヤー行は会議開始時に作られたクローンなので、
        // 途中で見た目を差し替えても追随せず、投票 UI の顔と実体がずれる。
        if (GameStates.IsMeeting)
        {
            error = Translator.GetString("OutfitShuffle.NotInMeeting");
            return false;
        }

        if (InPostMeetingSweep)
        {
            error = Translator.GetString("OutfitShuffle.PostMeetingCooldown");
            return false;
        }

        List<PlayerControl> pool = GetPool();

        if (pool.Count < 2)
        {
            error = Translator.GetString("OutfitShuffle.NotEnoughPlayers");
            return false;
        }

        // ⚠️ 適用前に全員分を退避する。1 人ずつ書き換えながら読むと、後半の人が
        // 「すでに入れ替わった後の外見」を配られる。
        List<Snapshot> snapshots = pool.ConvertAll(Capture);
        snapshots.ForEach(RememberOriginal);

        // Fisher-Yates で並べ替えてから 1 つずらす = 単一巡回の置換。
        // 自分自身が当たることも、同じ外見が 2 人に配られることも構造的に起きない。
        for (int i = snapshots.Count - 1; i > 0; i--)
        {
            int j = IRandom.Instance.Next(i + 1);
            (snapshots[i], snapshots[j]) = (snapshots[j], snapshots[i]);
        }

        var plan = new List<Snapshot>(snapshots.Count);

        for (int i = 0; i < snapshots.Count; i++)
        {
            Snapshot donor = snapshots[(i + 1) % snapshots.Count];
            plan.Add(donor with { Id = snapshots[i].Id });
        }

        DispatchStaggered(plan);
        Logger.Info($"Shuffled {plan.Count} player(s)", "OutfitShuffle");
        return true;
    }

    // 2 人の外見を交換する。一対一を崩さないので単体指定でもこの形を使う。
    public static bool SwapPair(byte firstId, byte secondId, out string error)
    {
        error = null;

        if (!AmongUsClient.Instance.AmHost)
        {
            error = "not host";
            return false;
        }

        if (!Enabled.GetBool())
        {
            error = Translator.GetString("OutfitShuffle.Disabled");
            return false;
        }

        // 会議中は外す。MeetingHud のプレイヤー行は会議開始時に作られたクローンなので、
        // 途中で見た目を差し替えても追随せず、投票 UI の顔と実体がずれる。
        if (GameStates.IsMeeting)
        {
            error = Translator.GetString("OutfitShuffle.NotInMeeting");
            return false;
        }

        if (InPostMeetingSweep)
        {
            error = Translator.GetString("OutfitShuffle.PostMeetingCooldown");
            return false;
        }

        if (firstId == secondId)
        {
            error = Translator.GetString("OutfitShuffle.SameTarget");
            return false;
        }

        PlayerControl first = Utils.GetPlayerById(firstId);
        PlayerControl second = Utils.GetPlayerById(secondId);

        if (first == null || second == null || !IsEligible(first) || !IsEligible(second))
        {
            error = Translator.GetString("OutfitShuffle.NotEligible");
            return false;
        }

        Snapshot firstSnapshot = Capture(first);
        Snapshot secondSnapshot = Capture(second);
        RememberOriginal(firstSnapshot);
        RememberOriginal(secondSnapshot);

        DispatchStaggered([secondSnapshot with { Id = firstId }, firstSnapshot with { Id = secondId }]);
        Logger.Info($"Swapped {firstId} <-> {secondId}", "OutfitShuffle");
        return true;
    }

    // 指定 1 人を、無作為に選んだもう 1 人と交換する。
    public static bool ShuffleOne(byte targetId, out string error)
    {
        error = null;

        List<PlayerControl> pool = GetPool();
        pool.RemoveAll(x => x.PlayerId == targetId);

        if (pool.Count == 0)
        {
            error = Translator.GetString("OutfitShuffle.NotEnoughPlayers");
            return false;
        }

        return SwapPair(targetId, pool[IRandom.Instance.Next(pool.Count)].PlayerId, out error);
    }

    // ---- 復元 ----

    // Patches/OutroPatch.cs から全員分ぶんまわされる無条件復元。
    // ⚠️ ガードに Count > 0 / IsEnable を混ぜないこと。切断で台帳が空になると全員分の復元が飛ぶ。
    //
    // ここでは送らずに積むだけ。全員分を試合終了の 1 フレームに撃つと、入れ替え本体で分散させた
    // 意味が無くなる (復元も同じ量の outfit ブロードキャストなので同じ fan-out 予算に乗る)。
    // 実際の送出はループが終わった後の ClearLedger が分散して行う。
    public static void RestoreOnGameEnd(byte id)
    {
        if (!OriginalOutfits.TryGetValue(id, out NetworkedPlayerInfo.PlayerOutfit original)) return;
        if (PendingRestore.Exists(x => x.Id == id)) return;

        PendingRestore.Add(new Snapshot(id, original, OriginalLevels.GetValueOrDefault(id)));
    }

    // Patches/OutroPatch.cs の復元ループが終わった後に呼ばれる。積まれた復元を分散送出してから台帳を空にする。
    public static void ClearLedger()
    {
        OriginalOutfits.Clear();
        OriginalLevels.Clear();

        // ⚠️ 世代を上げるのは予約より先。逆順にすると自分で予約した復元を即座に無効化してしまう。
        Generation++;

        if (PendingRestore.Count > 0)
        {
            DispatchStaggered(PendingRestore, true);
            PendingRestore.Clear();
        }
    }

    // /mix reset 用。ロビーでの巻き戻しもここを通る。
    public static void RestoreAll()
    {
        foreach (byte id in OriginalOutfits.Keys.ToArray()) RestoreOnGameEnd(id);

        ClearLedger();
    }

    // ---- 内部 ----

    private readonly record struct Snapshot(byte Id, NetworkedPlayerInfo.PlayerOutfit Outfit, uint Level);

    private static List<PlayerControl> GetPool()
    {
        IReadOnlyList<PlayerControl> source = GameStates.IsLobby ? Main.AllPlayerControls : Main.AllAlivePlayerControls;
        var pool = new List<PlayerControl>(source.Count);

        for (int i = 0; i < source.Count; i++)
        {
            PlayerControl pc = source[i];
            if (IsEligible(pc)) pool.Add(pc);
        }

        return pool;
    }

    private static bool IsEligible(PlayerControl pc)
    {
        if (pc == null || pc.Data == null || pc.Data.Disconnected) return false;

        // BananaMan は RpcChangeSkin 側で専用 outfit を強制されるので、配っても配られても破綻する。
        if (pc.Is(CustomRoles.BananaMan)) return false;

        // 死者は Camouflage が "Dead" 表示へ書き換える対象なので混ぜない。
        return GameStates.IsLobby || pc.IsAlive();
    }

    private static Snapshot Capture(PlayerControl pc)
    {
        NetworkedPlayerInfo.PlayerOutfit current = pc.Data.DefaultOutfit;

        // 退避と送信で同じインスタンスを共有すると、後の書き換えで台帳ごと汚染される。
        var copy = new NetworkedPlayerInfo.PlayerOutfit().Set(
            Main.AllPlayerNames.GetValueOrDefault(pc.PlayerId, current.PlayerName ?? string.Empty),
            current.ColorId,
            current.HatId ?? string.Empty,
            current.SkinId ?? string.Empty,
            current.VisorId ?? string.Empty,
            current.PetId ?? string.Empty,
            current.NamePlateId ?? string.Empty);

        return new Snapshot(pc.PlayerId, copy, pc.Data.PlayerLevel);
    }

    private static void RememberOriginal(Snapshot snapshot)
    {
        // 2 回目以降のシャッフルで「1 回目の入れ替え後の姿」を本物として覚えないよう TryAdd。
        if (OriginalOutfits.TryAdd(snapshot.Id, snapshot.Outfit)) OriginalLevels[snapshot.Id] = snapshot.Level;
    }

    private static void DispatchStaggered(List<Snapshot> plan, bool restoring = false)
    {
        int generation = Generation;
        int targets = Math.Max(1, Main.AllPlayerControls.Count);
        float interval = Math.Max(MinInterval, targets / SafeNestsPerSecond);

        for (int i = 0; i < plan.Count; i++)
        {
            Snapshot item = plan[i];

            if (i == 0)
            {
                Apply(generation, item, restoring);
                continue;
            }

            LateTask.New(() => Apply(generation, item, restoring), interval * i, "OutfitShuffle.Apply", false);
        }
    }

    private static void Apply(int generation, Snapshot item, bool restoring)
    {
        if (generation != Generation) return;

        PlayerControl pc = Utils.GetPlayerById(item.Id);
        if (pc == null || pc.Data == null || pc.Data.Disconnected) return;

        // ⚠️ ここで生死や BananaMan を**再判定しない**。対象を絞るのは計画を組む GetPool の時点だけ。
        // 分散送出の途中で誰かが死ぬのは普通に起きるが、そこで 1 人ぶん落とすと「その人はドナーとして
        // 自分の顔を配ったのに、自分は元の顔のまま」= 同じ顔が 2 人という状態になり、一対一という
        // 本機能の唯一の約束が壊れる。死者に当ててもロビーへの持ち越しは試合終了時の復元が拾う。
        // 復元 (restoring) も同じ理由で降りてはいけない — 死んだまま終わった人こそ入れ替わったまま残る。

        // 分散送出の途中で会議が始まったら、残りは会議明けまで持ち越す。
        // ⚠️ ここで捨ててはいけない — 一部だけ配られた状態は「同じ顔が 2 人」になり、
        // 一対一という本機能の唯一の約束が壊れる。
        if (!restoring && GameStates.IsMeeting)
        {
            LateTask.New(() => Apply(generation, item, false), MeetingRetryInterval, "OutfitShuffle.ApplyRetry", false);
            return;
        }

        // 公式鯖では RpcChangeOutfitByData 側が Main.AllPlayerNames を書かないので、先に自分で入れる。
        Main.AllPlayerNames[item.Id] = item.Outfit.PlayerName;

        // コムズ変装の復元先台帳も入れ替え後の姿へ寄せる。
        // ⚠️ Camouflage.BlockCamouflage は立てない — 立てっぱなしにするとコムズ変装が試合の残り
        // 全部で死ぬ (Devourer の 3 点セットは時限能力用であって、ラウンド持続の用途には合わない)。
        if (Camouflage.PlayerSkins.ContainsKey(item.Id)) Camouflage.PlayerSkins[item.Id] = item.Outfit;

        // カモフラ中は当ててもすぐ塗り潰されるので帯域を使わない。ただし復元だけは必ず当てる
        // (試合終了時にコムズ変装が張られたままだと、戻す機会がここしか無い)。
        if (restoring || !Camouflage.IsCamouflage) Utils.RpcChangeSkin(pc, item.Outfit);

        // Level はロビーでは絶対に触らない。ロビー中は毎フレーム低レベル自動キック判定が走っており
        // (Patches/PlayerControlPatch.cs)、低レベルの人に化けた瞬間に無実のプレイヤーが
        // 誤キック + 再入場ブロックされる。
        if (SwapLevel.GetBool() && !GameStates.IsLobby) pc.RpcSetLevel(item.Level);
    }
}
