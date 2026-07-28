using System;
using System.Collections.Generic;
using System.Linq;
using AmongUs.GameOptions;
using EndKnot.Modules;
using EndKnot.Patches;
using UnityEngine;

namespace EndKnot.Roles;

// ============================================================
// Shuffler (シャッフラー) — Impostor/Support
//
// コンセプト: 「そのマップは本物か？」
//   ファントムボタン (またはペット) で部屋どうしの繋がりを架け替える。
//   発動中は、部屋を移動した瞬間に「置換表 π が指す別の部屋」へ飛ばされる
//   (カフェのドアを抜けたのに電気室に出る)。π は発動から会議までは固定なので
//   プレイヤーは架け替えを学習できる (毎回ランダムだと理不尽なだけになる)。
//   同時に赤い丸をマップ上へ撒き、踏んだ生存者が会議を起こす。
//
// SnapTo 予算 (NumSnapToCallsThisRound 80/100・会議境界リセット+時間回復 1.5s/token) の扱い:
//   部屋の移動ごとに 1 TP なので、無制限にすると数十秒でラウンド共有の予算を
//   使い切り、他役職 (Engineer の vent TP 等) の TP まで無音で失敗する。
//   MapExtender と同じ3点セットで閉じる:
//     ① per-player 間隔 (Utils.TP の minInterval — 手書き辞書と違い同役職が
//        複数枚配られても間引きが効く)
//     ② ラウンド総予算 (MaxShuffleTpsPerRound) のバックストップ
//     ③ pc.TP() の bool 戻り値で「成功した時だけ」予算を消費
//   さらに ShuffleDuration の時限式にして、予算切れで無言で効かなくなるのではなく
//   「効果時間が終わった」と分かる形にしている。
//
// void 送り:
//   1回の発動で VoidThreshold 回目の転送だけは、行き先が部屋ではなくマップ外になる。
//   死亡ではなく着地点を変えるだけなのは、飛ばされる側 (タスク中のクルー) に
//   一切の選択権がないため — 死亡罰にすると被害者がシャッフラーの能力で罰される形になる。
//   void に落ちた人はそのタスクターン戻れず、次の会議では投票不可 + 発言が流し戻される。
//   一定人数が落ちたらラウンドが成立しなくなるので救済の会議を起こす (通常の会議)。
//
// マップ境界算出に失敗した場合 (未知マップの例外 / Positions 空 / LIMap) は
// 機能全体を無効化する (MapExtender / Riptide と同じ fail-safe 方針)。
// ============================================================
public class Shuffler : RoleBase
{
    private const int Id = 705600;

    private const float TpInterval = 3f;             // 同一プレイヤーの連続転送間隔
    private const int MaxShuffleTpsPerRound = 40;    // ラウンド SnapTo 予算のバックストップ
    private const float MarkerTriggerRange = 1f;     // 赤丸が反応する半径 (CNO の footprint 前提)

    public static bool On;

    private static OptionItem KillCooldown;
    public static OptionItem AbilityCooldown;
    private static OptionItem ShuffleDuration;
    private static OptionItem MarkerCount;
    private static OptionItem VoidThreshold;
    private static OptionItem VoidPlayersForMeeting;
    private static OptionItem AbilityUseLimit;
    private static OptionItem AbilityUseGainWithEachKill;

    // ---- マップ情報 (Init() で算出、ゲーム全体で共有) ----
    private static bool MapBoundsValid;
    private static Vector2 VoidPosition;

    // ---- ラウンド状態は保持者間で共有する (static) ----
    // インスタンス単位で持つと、2人以上が Shuffler を持ったときに置換表と予算が
    // 二重管理になり、同じフレームで両方から転送されて予算を二重消費する。
    // 追跡対象はプレイヤーとマップであって保持者ではないので共有が正しい。
    private static readonly Dictionary<SystemTypes, SystemTypes> Rewire = [];
    private static readonly Dictionary<byte, SystemTypes> LastRoom = [];
    private static readonly Dictionary<byte, int> TpCountThisActivation = [];
    private static readonly HashSet<byte> VoidPlayers = [];
    // 座標は CNO から読まない。CustomNetObject.Position は生成コルーチン内で非同期に代入される
    // ため、設置直後に読むと (0,0) を返し、マップ原点付近に立っているだけの人が誤爆する。
    private static readonly List<(ShuffleMarker Marker, Vector2 Position)> Markers = [];
    private static long ShuffleEndTS;
    private static int TpsThisRound;
    private static bool CapLoggedThisRound;
    private static bool MeetingTriggered;

    public override bool IsEnable => On;

    /// <summary>void に落とされ、次の会議で投票・発言を封じられているプレイヤーか</summary>
    public static bool IsInVoid(byte playerId)
    {
        return On && VoidPlayers.Contains(playerId);
    }

    public override void SetupCustomOption()
    {
        StartSetup(Id)
            .AutoSetupOption(ref KillCooldown, 30, new IntegerValueRule(1, 120, 1), OptionFormat.Seconds)
            .AutoSetupOption(ref AbilityCooldown, 25, new IntegerValueRule(10, 120, 1), OptionFormat.Seconds)
            .AutoSetupOption(ref ShuffleDuration, 20, new IntegerValueRule(5, 90, 1), OptionFormat.Seconds)
            .AutoSetupOption(ref MarkerCount, 2, new IntegerValueRule(0, 8, 1))
            .AutoSetupOption(ref VoidThreshold, 5, new IntegerValueRule(1, 20, 1), OptionFormat.Times)
            .AutoSetupOption(ref VoidPlayersForMeeting, 3, new IntegerValueRule(1, 14, 1), OptionFormat.Players)
            .AutoSetupOption(ref AbilityUseLimit, 1f, new FloatValueRule(0f, 20f, 0.05f), OptionFormat.Times)
            .AutoSetupOption(ref AbilityUseGainWithEachKill, 0.5f, new FloatValueRule(0f, 5f, 0.25f), OptionFormat.Times);
    }

    public override void Init()
    {
        On = false;
        MapBoundsValid = false;
        ResetRoundState();
        VoidPlayers.Clear();

        try
        {
            if (Main.LIMap) return;

            RandomSpawn.SpawnMap spawnMap = RandomSpawn.SpawnMap.GetSpawnMap();
            List<Vector2> positions = spawnMap.Positions.Values.ToList();
            if (positions.Count < 2) return;

            float minX = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;

            foreach (Vector2 pos in positions)
            {
                if (pos.x < minX) minX = pos.x;
                if (pos.x > maxX) maxX = pos.x;
                if (pos.y > maxY) maxY = pos.y;
            }

            // MapExtender の出口ポータル (ExitBasePosition = 中央X, maxY + 25) と同じ公式・同じ入力なので、
            // 素直に置くと座標がビット単位で一致して落下地点がポータルの反応半径に入る。
            // さらに上へ逃がしたうえで、MapExtender 側にも void 除外を入れてある (OnCheckPlayerPosition)。
            VoidPosition = new Vector2((minX + maxX) / 2f, maxY + 45f);
            MapBoundsValid = true;
        }
        catch (Exception e)
        {
            Logger.Error($"Shuffler: マップ境界計算失敗 → 機能無効化: {e.Message}", "Shuffler.Init");
            MapBoundsValid = false;
        }
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

    public override void ApplyGameOptions(IGameOptions opt, byte playerId)
    {
        // Phantom basis 化に伴い、AU の vanish/phantom cooldown を発動クールタイムに使う (Crosswind/WaveCannon と同型)。
        // AbilityCooldown の最小値 (10) は PreventKill の 10 秒ガードを僅かに下回りうるため、
        // イントロ直後は 12 秒未満にならないようクランプする。
        float cd = AbilityCooldown.GetFloat();
        if (IntroCutsceneDestroyPatch.PreventKill) cd = Mathf.Max(cd, 12f);

        AURoleOptions.PhantomDuration = 0.1f;
        AURoleOptions.PhantomCooldown = cd;
    }

    public override bool OnVanish(PlayerControl pc)
    {
        Activate(pc);
        return false;
    }

    public override void OnPet(PlayerControl pc)
    {
        Activate(pc);
    }

    private void Activate(PlayerControl pc)
    {
        if (!pc.IsAlive() || !GameStates.IsInTask) return;

        if (!MapBoundsValid)
        {
            pc.Notify(Translator.GetString("Shuffler.MapUnsupported"));
            return;
        }

        if (pc.GetAbilityUseLimit() < 1f)
        {
            pc.Notify(Translator.GetString("Shuffler.NoUsesLeft"));
            return;
        }

        pc.RpcRemoveAbilityUse();

        // 発動経路が2本 (ファントムボタン = vanilla エンジンの PhantomCooldown / ペット = Main.AbilityCD 辞書) あり、
        // 互いのクロックを見ないため片方を押しても他方は「使える」ままになる。使用回数を増やす設定にすると
        // 交互押しで規定のクールタイムより速く連発できてしまうので、発動時に両方のクロックを進める。
        pc.RpcResetAbilityCooldown();
        pc.AddAbilityCD(AbilityCooldown.GetInt());

        BuildRewire();
        SpawnMarkers();

        // 発動時点の部屋を記録しておかないと、既に部屋に立っている全員が
        // 次のフレームで「部屋が変わった」と判定されて一斉に飛ぶ。
        LastRoom.Clear();
        TpCountThisActivation.Clear();

        foreach (PlayerControl target in Main.AllAlivePlayerControlsToArray)
        {
            if (target.PlayerId >= 200) continue; // CNO 除外
            PlainShipRoom room = target.GetPlainShipRoom();
            if (room) LastRoom[target.PlayerId] = room.RoomId;
        }

        ShuffleEndTS = Utils.TimeStamp + ShuffleDuration.GetInt();

        pc.Notify(string.Format(Translator.GetString("Shuffler.Activated"), ShuffleDuration.GetInt(), Math.Round(pc.GetAbilityUseLimit(), 2)));
    }

    // 部屋 → 部屋 の置換表を作る。自分自身へ写る部屋があると「ドアを抜けても何も起きない」
    // 部屋ができてしまうので、1つずらした巡回にして全ての部屋が必ず別の部屋を指すようにする。
    //
    // 一度組んだ表は会議まで組み替えない (AfterMeetingTasks で破棄)。架け替えを読めることが
    // この能力の肝なので、2枚目の Shuffler の発動や同一ラウンドの再発動で抽選し直すと、
    // 覚えた繋がりが理由なく変わってプレイヤー側からは事故に見える。
    private static void BuildRewire()
    {
        if (Rewire.Count > 0) return;

        List<SystemTypes> rooms = RandomSpawn.SpawnMap.GetSpawnMap().Positions.Keys.ToList().Shuffle();
        if (rooms.Count < 2) return;

        for (var i = 0; i < rooms.Count; i++)
            Rewire[rooms[i]] = rooms[(i + 1) % rooms.Count];
    }

    private static void SpawnMarkers()
    {
        DespawnMarkers();

        int count = MarkerCount.GetInt();
        if (count <= 0) return;

        List<Vector2> spots = RandomSpawn.SpawnMap.GetSpawnMap().Positions.Values.ToList().Shuffle();

        for (var i = 0; i < count && i < spots.Count; i++)
            Markers.Add((new ShuffleMarker(spots[i]), spots[i]));
    }

    public override void OnCheckPlayerPosition(PlayerControl pc)
    {
        if (!MapBoundsValid) return;

        // OnCheckPlayerPosition の呼び出し側 (PlayerControlPatch) が inTask / 生存 /
        // ExileController 無し / IntroDestroyed を既にゲートしている。会議中だけ自前で弾く。
        if (GameStates.IsMeeting) return;
        if (pc.PlayerId >= 200) return; // CNO 除外

        CheckMarkers(pc);
        CheckShuffle(pc);
    }

    private void CheckMarkers(PlayerControl pc)
    {
        if (MeetingTriggered || Markers.Count == 0) return;

        Vector2 pos = pc.Pos();

        for (var i = 0; i < Markers.Count; i++)
        {
            (ShuffleMarker marker, Vector2 markerPos) = Markers[i];
            if (!FastVector2.DistanceWithinRange(markerPos, pos, MarkerTriggerRange)) continue;

            marker?.Despawn();
            Markers.RemoveAt(i);

            MeetingTriggered = true;
            Logger.Info($"{pc.GetNameWithRole().RemoveHtmlTags()} が赤丸を踏んだため会議を開始", "Shuffler");
            pc.NoCheckStartMeeting(null, true);
            return;
        }
    }

    private void CheckShuffle(PlayerControl pc)
    {
        // 同じフレームで赤丸を踏んで会議が呼ばれた直後にここへ落ちると、
        // 会議を起こした本人がその会議の投票権をその場で失うことがある。
        if (MeetingTriggered) return;
        if (ShuffleEndTS == 0) return;

        if (Utils.TimeStamp > ShuffleEndTS)
        {
            EndShuffle();
            return;
        }

        byte id = pc.PlayerId;

        if (pc.Is(CustomRoles.Shuffler)) return;    // 本人は免疫 (本物の地図を知っている側)
        if (VoidPlayers.Contains(id)) return;       // 既に落ちている
        if (Pelican.IsEaten(id)) return;            // 黒部屋に固定されており二重 TP になるだけ

        PlainShipRoom room = pc.GetPlainShipRoom();
        if (!room) return;                          // 廊下・マップ外は架け替えの対象外

        SystemTypes now = room.RoomId;

        if (!LastRoom.TryGetValue(id, out SystemTypes last))
        {
            LastRoom[id] = now;
            return;
        }

        if (last == now) return;

        if (!Rewire.TryGetValue(now, out SystemTypes destRoom)) { LastRoom[id] = now; return; }

        Dictionary<SystemTypes, Vector2> positions = RandomSpawn.SpawnMap.GetSpawnMap().Positions;
        if (!positions.TryGetValue(destRoom, out Vector2 destPos)) { LastRoom[id] = now; return; }

        if (TpsThisRound >= MaxShuffleTpsPerRound)
        {
            // OnCheckPlayerPosition は lowLoad ゲート無しで毎 fixed update 走るので、
            // 上限到達後は歩くたびにここへ落ちる。ログはラウンドに1回だけ出す。
            if (!CapLoggedThisRound)
            {
                CapLoggedThisRound = true;
                Logger.Info($"ラウンドの転送上限 ({MaxShuffleTpsPerRound}) に到達 — 会議まで架け替えを停止", "Shuffler");
            }

            LastRoom[id] = now;
            return;
        }

        int next = TpCountThisActivation.GetValueOrDefault(id) + 1;
        bool toVoid = next >= VoidThreshold.GetInt();

        // TP が間引き・不可状態で失敗したときに回数と予算を進めないよう、成否を見てから確定する。
        if (!pc.TP(toVoid ? VoidPosition : destPos, minInterval: TpInterval)) return;

        TpCountThisActivation[id] = next;
        TpsThisRound++;

        if (toVoid)
        {
            LastRoom.Remove(id);
            SendToVoid(pc);
            return;
        }

        // TP 先も「部屋が変わった」状態なので、着地点を最後の部屋として記録しておかないと
        // 次のフレームで連鎖して飛び続ける (MapExtender のラッチと同じ役割)。
        LastRoom[id] = destRoom;
    }

    private static void SendToVoid(PlayerControl pc)
    {
        VoidPlayers.Add(pc.PlayerId);
        pc.Notify(Translator.GetString("Shuffler.FellIntoVoid"));
        Logger.Info($"{pc.GetNameWithRole().RemoveHtmlTags()} が void に落ちた", "Shuffler");

        // 次の会議で発言を封じる。ホストから本当に黙らせる手段は無いので、
        // /mute と同じ flood-clear (発言した瞬間に全員のチャット履歴を流し戻す) を使う。
        // 解除は AfterMeetingTasks — duration はその会議を跨げるだけの長さを取る。
        ChatCommands.MutedPlayers[pc.PlayerId] = (Utils.TimeStamp, 600, 0);

        if (VoidPlayers.Count < VoidPlayersForMeeting.GetInt()) return;

        // これ以上落ちるとラウンドが成立しないので救済の会議を起こす。
        // 通常の会議 (強制スキップしない) — 強制終了はラウンド状態を全役職ぶん壊すので使わない。
        if (MeetingTriggered) return;

        PlayerControl caller = Main.AllAlivePlayerControlsToArray.FirstOrDefault(x => x.PlayerId < 200 && !VoidPlayers.Contains(x.PlayerId));
        if (!caller) return;

        MeetingTriggered = true;
        Logger.Info($"void 送りが {VoidPlayers.Count} 人に達したため救済の会議を開始", "Shuffler");
        caller.NoCheckStartMeeting(null, true);
    }

    // 効果時間の終了。Rewire (置換表) はここでは消さない — 会議まで同じ繋がりを保つ契約。
    private static void EndShuffle()
    {
        ShuffleEndTS = 0;
        LastRoom.Clear();
        TpCountThisActivation.Clear();
    }

    public override void AfterMeetingTasks()
    {
        ResetRoundState();

        // void 組は会議後の通常配置でマップ内へ戻る (ExilePatch)。封じも会議1回ぶんで解ける。
        foreach (byte id in VoidPlayers) ChatCommands.MutedPlayers.Remove(id);
        VoidPlayers.Clear();
    }

    private static void ResetRoundState()
    {
        EndShuffle();
        Rewire.Clear();
        DespawnMarkers();
        TpsThisRound = 0;
        CapLoggedThisRound = false;
        MeetingTriggered = false;
    }

    private static void DespawnMarkers()
    {
        foreach ((ShuffleMarker marker, Vector2 _) in Markers) marker?.Despawn();
        Markers.Clear();
    }

    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud = false, bool meeting = false)
    {
        if (!On || meeting || seer.PlayerId != target.PlayerId) return string.Empty;
        if (ShuffleEndTS == 0 || !seer.Is(CustomRoles.Shuffler)) return string.Empty;

        long remain = ShuffleEndTS - Utils.TimeStamp;
        if (remain < 0) remain = 0;
        return Utils.ColorString(Utils.GetRoleColor(CustomRoles.Shuffler), $" {remain}");
    }
}
