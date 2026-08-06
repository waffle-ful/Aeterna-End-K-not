using System;
using System.Collections.Generic;
using System.Linq;
using AmongUs.GameOptions;
using EndKnot.Modules;
using Hazel;
using UnityEngine;

namespace EndKnot.Roles;

// ============================================================
// Akazukin (あかづきん) — Crewmate/Power
//
// ⚠️ 名前に反して「擬似死亡」ではない。捕食された赤ずきんは PlayerState.SetDead() +
//    RpcExiled() で **本当に死んでいる** (2026-08-07 に擬似死亡から切り替えた)。
//    PseudoDead / IsPseudoDead / EnterPseudoDeath は互換のため名前を据え置いてあるだけで、
//    意味は「捕食されて隔離房に居る = まだ生き返る余地がある死者」。
//
//    本死亡にした理由と、それで壊れないカラクリ:
//    ・殺害者がバレない ← バニラのキル演出を通さない (OnCheckMurderAsTarget が false を返す)
//      ので被害者にキルシーンが再生されない。役職・名前色・死因通知・ゴースト情報は
//      Utils.IsRevivingRoleAlive() × Main.DiedThisRound の目隠し機構が封じる
//      (だから EnterPseudoDeath は PseudoDead 登録を AfterPlayerDeathTasks より先に行う)。
//      復活時は RpcRevive() が RealKiller を消すので、会議開始 13 秒後の
//      「あなたを殺したのは X」遅延メッセージ (PlayerControlPatch) も本人には届かない。
//    ・ゲームが先に終わらない ← ManipulateGameEndCheckCrew で猶予中は勝敗判定を止める。
//    ・マップを覗かれない ← ゴーストの視界はホストから縮められない
//      (バニラの CalculateLightRadius は死者に対して CrewLightMod/ImpostorLightMod を無視する)
//      ので、距離で封じる。隔離房 (HoldingCell) はマップ下端の外に置く。
//      ⚠️ 隔離房への TP は **必ず noCheckState: true** で撃つこと。Utils.TP は既定で
//      「死んでいる相手は転送不能」として無音キャンセルするため、外すと封じ込めが丸ごと
//      no-op になる (2026-08-07 実機で発覚。生存前提の Pelican から機構をコピーしたのが原因)。
// ============================================================
public class Akazukin : RoleBase
{
    private const int Id = 703760;

    /// <summary>隔離房から出たと判定する距離。速度 0.5 (≒1.2u/s) × 0.4 秒 tick では届かないので
    /// 実際にはまず張り付いたままになり、SnapTo cap (ラウンド共有 80/100) をほぼ消費しない。</summary>
    private const float HoldingCellRadius = 5f;

    public static List<byte> PlayerIdList = [];
    public static Dictionary<byte, AkazukinState> PseudoDead = [];

    private static OptionItem RevivalGracePeriod;
    private static OptionItem DisplayDeathReasonInName;
    private static OptionItem CanReviveAfterMeeting;
    private static OptionItem ReviveAtOriginalPosition;

    // ---- 隔離房 (Init() でマップ境界から算出、全保持者で共有) ----
    private static bool MapBoundsValid;
    private static Vector2 HoldingCell;

    private int Count;
    private byte AkazukinId;

    public override bool IsEnable => PlayerIdList.Count > 0;

    public class AkazukinState
    {
        public byte KillerId;
        public PlayerState.DeathReason DeathReason;
        public Vector2 OriginalPos;
        public float OriginalSpeed;
        public DateTime PseudoDeathStart;

        /// <summary>復活要求。RpcRevive は会議・追放演出中だと無音で失敗するため、
        /// 実行は必ずタスク中に走る OnFixedUpdate 側へ寄せる (安全になるまで自動で再試行される)。</summary>
        public bool ReviveRequested;
    }

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.CrewmateRoles, CustomRoles.Akazukin);

        RevivalGracePeriod = new FloatOptionItem(Id + 10, "Akazukin_RevivalGracePeriod", new(0f, 300f, 5f), 60f, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Akazukin])
            .SetValueFormat(OptionFormat.Seconds);

        DisplayDeathReasonInName = new BooleanOptionItem(Id + 11, "Akazukin_DisplayDeathReasonInName", true, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Akazukin]);

        CanReviveAfterMeeting = new BooleanOptionItem(Id + 12, "Akazukin_CanReviveAfterMeeting", true, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Akazukin]);

        ReviveAtOriginalPosition = new BooleanOptionItem(Id + 13, "Akazukin_ReviveAtOriginalPosition", true, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Akazukin]);
    }

    public override void Init()
    {
        PlayerIdList = [];
        PseudoDead = [];
        MapBoundsValid = false;

        try
        {
            if (Main.LIMap) return;

            RandomSpawn.SpawnMap spawnMap = RandomSpawn.SpawnMap.GetSpawnMap();
            List<Vector2> positions = spawnMap.Positions.Values.ToList();
            if (positions.Count < 2) return;

            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue;

            foreach (Vector2 pos in positions)
            {
                if (pos.x < minX) minX = pos.x;
                if (pos.x > maxX) maxX = pos.x;
                if (pos.y < minY) minY = pos.y;
            }

            // マップ外座標は同じ公式・同じ入力だとビット単位で他役職と一致する。上端側はもう埋まっている
            // (MapExtender の OutsideLine = maxY+15 と出口ポータル = maxY+25、Shuffler の void = maxY+45)ので、
            // 誰も使っていない下端側へ置く。Riptide の波の生成境界 minY-15 からも 25u 離してある。
            HoldingCell = new Vector2((minX + maxX) / 2f, minY - 40f);
            MapBoundsValid = true;
        }
        catch (Exception e)
        {
            Logger.Error($"Akazukin: マップ境界計算失敗 → 従来の黒い部屋へフォールバック: {e.Message}", "Akazukin.Init");
            MapBoundsValid = false;
        }
    }

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);
        AkazukinId = playerId;
    }

    /// <summary>捕食中の隔離先。マップ境界が取れなかった時だけ従来の黒い部屋に落とす。</summary>
    private static Vector2 GetHoldingCell()
    {
        return MapBoundsValid ? HoldingCell : Pelican.GetBlackRoomPS();
    }

    public override void Remove(byte playerId)
    {
        PlayerIdList.Remove(playerId);
        PseudoDead.Remove(playerId);
    }

    public static bool IsPseudoDead(byte id) => PseudoDead.ContainsKey(id);

    public static bool ShouldDisplayDeathReason(byte id)
        => PseudoDead.ContainsKey(id) && DisplayDeathReasonInName != null && DisplayDeathReasonInName.GetBool();

    public override bool OnCheckMurderAsTarget(PlayerControl killer, PlayerControl target)
    {
        if (killer == null || target == null) return true;

        if (PseudoDead.ContainsKey(target.PlayerId))
        {
            killer.SetKillCooldown();
            return false;
        }

        EnterPseudoDeath(target, killer, PlayerState.DeathReason.Kill);
        killer.SetKillCooldown();
        return false;
    }

    public static void EnterPseudoDeath(PlayerControl target, PlayerControl killer, PlayerState.DeathReason reason)
    {
        if (target == null || killer == null) return;
        if (PseudoDead.ContainsKey(target.PlayerId)) return;

        Vector2 killPos = target.Pos();
        byte targetId = target.PlayerId;

        // ⚠️ 死亡処理より **先に** 登録する。下の Utils.AfterPlayerDeathTasks は
        //    「DiedThisRound かつ IsRevivingRoleAlive()」のときだけ「あなたを殺したのは X」通知を
        //    握り潰すが、その IsRevivingRoleAlive() がこの辞書を見ている。後から入れると殺害者がバレる。
        PseudoDead[targetId] = new AkazukinState
        {
            KillerId = killer.PlayerId,
            DeathReason = reason,
            OriginalPos = killPos,
            OriginalSpeed = Main.AllPlayerSpeed[targetId],
            PseudoDeathStart = DateTime.Now
        };

        target.SetRealKiller(killer);

        PlayerState state = Main.PlayerStates[targetId];
        state.deathReason = reason;
        state.SetDead();

        // ---- 死亡の配信は赤ずきん専用配線 (2026-08-07 全面改修) ----
        // 旧実装 (RpcExiled ブロードキャスト + Utils.RpcCreateDeadBody) は、偽死体機構が
        // 「本人と同じ PlayerId を名乗る偽 PlayerControl」を客にスポーンするため、本死亡化した途端に
        // クライアント側の PlayerId→PlayerControl 対応表が壊れ、死体2つ/通報不可/カメラ非追従/
        // 無関係プレイヤーの連動という複合症状を生んだ (実機 2026-08-07_01.34.48)。
        // 新配線は偽オブジェクトを一切スポーンせず、宛先で経路を分ける:
        //   本人以外の全クライアント → 本人 NetId の self-MurderPlayer (targeted)
        //       = 各クライアントのバニラ機構が「本物の死体」を現場に落とす。通報可・ID 衝突なし。
        //         自分同士のキルなので殺害者情報は流れず、傍観者にキル演出も出ない。
        //   本人 → Exiled (targeted) = 死体もキル演出も見ずに死ぬ (targeted self-murder の前例は
        //         ExtendedPlayerControl.FixBlackScreen — 公式鯖で実運用済み)。
        //   ホスト → ローカルで Exiled() 実走 + CreateDeadBody() 直呼び (RPC なし)。
        // ⚠️ 送信は target.Exiled() より先に行う。Exiled() が立てる Data.IsDead=true の dirty 同期が
        //    MurderPlayer より先に客へ届くと「既に死んでいる相手への殺害」になり死体が落ちない恐れがある。
        // ⚠️ RpcExileV2() を使わないのは従来どおり (LoversSuicide を呼ぶ = 復活しても相方が死んだまま)。
        var sender = CustomRpcSender.Create("Akazukin.EnterPseudoDeath", SendOption.Reliable);
        var sentClientIds = new HashSet<int> { AmongUsClient.Instance.ClientId };

        if (!target.AmOwner)
        {
            sentClientIds.Add(target.OwnerId);
            sender.AutoStartRpc(target.NetId, RpcCalls.Exiled, target.OwnerId);
            sender.EndRpc();
        }

        // EnumeratePlayerControls = 接続中の実プレイヤーのみ (fan-out の母集合はこれが慣習。
        // AllPlayerControls だと偽 PlayerControl の残留混入時に OwnerId 未設定 → tag5 ブロードキャスト化の穴がある)
        foreach (PlayerControl pc in Main.EnumeratePlayerControls())
        {
            if (!sentClientIds.Add(pc.OwnerId)) continue;
            sender.AutoStartRpc(target.NetId, RpcCalls.MurderPlayer, pc.OwnerId);
            sender.WriteNetObject(target);
            sender.Write((int)MurderResultFlags.Succeeded);
            sender.EndRpc();
        }

        sender.SendMessage();

        target.Exiled();
        Utils.AfterPlayerDeathTasks(target); // Main.DiedThisRound への登録 = 目隠し機構の入口

        // ホストに見せる死体。⚠️ 必ず Exiled() の **後** に作ること。先に作るとホストの
        // ローカル死体だけが Exiled() の実走に巻き込まれて消え、「客には見えるのにホストからだけ
        // 死体が無い/通報できない」という非対称になる (2026-08-07 実機で観測)。
        Utils.CreateDeadBody(killPos, (byte)target.Data.DefaultOutfit.ColorId, target);

        Main.AllPlayerSpeed[targetId] = 0.5f;
        target.MarkDirtySettings();

        // ⚠️ noCheckState: true が必須。Utils.TP は既定で「死んでいる相手は転送不能」として
        //    無音キャンセルする (Utils.cs の !pc.IsAlive() ガード) ので、外すと隔離房送りが
        //    まるごと no-op になり、ゴーストがマップを自由に飛び回れてしまう。
        target.TP(GetHoldingCell(), noCheckState: true);

        SendRPC(targetId);
        Utils.NotifyRoles();

        Logger.Info($"{target.GetRealName()} 捕食 — 隔離房へ (killer: {killer.GetRealName()})", "Akazukin");
    }

    /// <summary>復活を予約する。実行は OnFixedUpdate (= 必ずタスク中) が安全な窓で行う。</summary>
    private static void RequestRevive(byte targetId)
    {
        if (PseudoDead.TryGetValue(targetId, out AkazukinState state)) state.ReviveRequested = true;
    }

    /// <summary>実際の復活。⚠️ タスク中の安全な窓からのみ呼ぶこと (呼び出し元は OnFixedUpdate)。</summary>
    private static void Revive(byte targetId)
    {
        if (!PseudoDead.TryGetValue(targetId, out AkazukinState state)) return;

        PlayerControl target = Utils.GetPlayerById(targetId);

        if (target == null)
        {
            PseudoDead.Remove(targetId);
            SendRPC(targetId);
            return;
        }

        // RpcRevive() は Data.IsDead でない相手には無音 return する。取りこぼすと
        // 「PseudoDead からは外れたのに生き返っていない」= 永久に死んだままになるので、
        // 辞書から外す前に確認する。
        if (!target.Data.IsDead)
        {
            PseudoDead.Remove(targetId);
            SendRPC(targetId);
            Logger.Warn($"{target.GetRealName()} は既に生存状態だったため復活処理を省略", "Akazukin");
            return;
        }

        PseudoDead.Remove(targetId);
        SendRPC(targetId);

        // 速度は RpcRevive() 内の SyncSettings より先に戻す (でないと 0.5 のまま同期される)。
        Main.AllPlayerSpeed[targetId] = state.OriginalSpeed;

        target.RpcRevive();

        Vector2 dest = (ReviveAtOriginalPosition?.GetBool() ?? true) ? state.OriginalPos : Pelican.GetBlackRoomPS();
        target.TP(dest);
        target.MarkDirtySettings();

        // 捕食中はゴーストとして会議に座るので、他の死者の発言 (「◯◯に殺された」) が画面に残っている。
        // チャットはクライアント間ブロードキャストでホストからは受信自体を止められないため、
        // せめて生き返った時点で流し、死者席の会話を持ち帰らせない。
        ChatManager.ClearChat(target);

        target.Notify(Translator.GetString("Akazukin_Revived"), 10f);

        Utils.NotifyRoles();
        Logger.Info($"{target.GetRealName()} 隔離房から復活", "Akazukin");
    }

    /// <summary>猶予切れ。捕食時点で既に死んでいるので、ここでやるのは「もう戻らない」確定処理だけ。</summary>
    public static void RealDeath(byte targetId)
    {
        if (!PseudoDead.TryGetValue(targetId, out AkazukinState state)) return;

        PseudoDead.Remove(targetId);
        SendRPC(targetId);

        PlayerControl target = Utils.GetPlayerById(targetId);
        if (target == null) return;

        // 隔離房に置き去りにすると、ゴーストが減速したままマップ外に取り残される。
        // 速度を戻して自分の死体の場所へ解放する。
        Main.AllPlayerSpeed[targetId] = state.OriginalSpeed;
        target.MarkDirtySettings();
        target.TP(state.OriginalPos, noCheckState: true); // 確定死亡後もゴーストなので noCheckState が要る

        Utils.NotifyRoles();
        Logger.Info($"{target.GetRealName()} 猶予切れ — 確定死亡", "Akazukin");
    }

    public static void OnAnyMurder(PlayerControl killer, PlayerControl victim)
    {
        if (victim == null || PseudoDead.Count == 0) return;
        foreach (var kv in PseudoDead.ToArray())
        {
            if (kv.Value.KillerId == victim.PlayerId)
            {
                RequestRevive(kv.Key);
            }
        }
    }

    public static void OnAnyExile(byte exiledId)
    {
        if (PseudoDead.Count == 0) return;
        if (CanReviveAfterMeeting == null || !CanReviveAfterMeeting.GetBool()) return;

        foreach (var kv in PseudoDead.ToArray())
        {
            if (kv.Value.KillerId != exiledId) continue;

            // 予約だけしておく。ここは追放演出中で RpcRevive が無音失敗する窓なので、
            // 実際の復活はタスクが再開してから OnFixedUpdate が行う。
            RequestRevive(kv.Key);
        }
    }

    public static void OnAnyDisconnect(byte leftId)
    {
        if (PseudoDead.Count == 0) return;
        foreach (var kv in PseudoDead.ToArray())
        {
            if (kv.Value.KillerId == leftId)
            {
                RequestRevive(kv.Key);
            }
        }
    }

    public override void OnFixedUpdate(PlayerControl pc)
    {
        if (!GameStates.IsInTask) return;
        if (pc == null || !PseudoDead.TryGetValue(pc.PlayerId, out var state)) return;

        Count--;
        if (Count > 0) return;
        Count = 20;

        // 復活予約の消化。RpcRevive とその内部の RpcChangeRoleBasis は追放演出中・AntiBlackout 中だと
        // 無音で失敗するので、通り抜けられる窓になるまでここで待つ (次の tick で自動的に再試行される)。
        if (state.ReviveRequested)
        {
            if (!GameStates.IsEnded && !ExileController.Instance && !AntiBlackout.SkipTasks)
                Revive(pc.PlayerId);

            return;
        }

        float grace = RevivalGracePeriod.GetFloat();
        if (grace > 0 && (DateTime.Now - state.PseudoDeathStart).TotalSeconds >= grace)
        {
            RealDeath(pc.PlayerId);
            return;
        }

        Vector2 cell = GetHoldingCell();
        if (!FastVector2.DistanceWithinRange(pc.Pos(), cell, HoldingCellRadius))
        {
            // noCheckState: 死者の TP は既定で弾かれる (上の捕食時と同じ理由)。
            // minInterval: ラウンド共有の SnapTo 予算 (80 降格 / 100 キャンセル) は 1.0 秒で 1 個回復するので、
            //   1 秒に 1 回までに抑えれば単独では絶対に枯渇させない。半径 5u を速度 0.5 で抜けるのに
            //   4 秒近くかかるため、この間引きで封じ込めの強さは実質落ちない。
            pc.TP(cell, noCheckState: true, log: false, minInterval: 1f);
        }
    }

    // 以下の能力封じ 6 点は、擬似死亡 (= 生きたまま封じる) 時代の名残。
    // 本死亡になった今は呼び出し元がどれも先に IsAlive() で弾くため、ほぼ到達しない。
    // 「ここで守られている」と思って依存コードを積まないこと (保険として残しているだけ)。
    public override bool CanUseKillButton(PlayerControl pc)
    {
        if (PseudoDead.ContainsKey(pc.PlayerId)) return false;
        return base.CanUseKillButton(pc);
    }

    public override bool CanUseImpostorVentButton(PlayerControl pc)
    {
        if (PseudoDead.ContainsKey(pc.PlayerId)) return false;
        return base.CanUseImpostorVentButton(pc);
    }

    public override bool CanUseVent(PlayerControl pc, int ventId)
    {
        if (PseudoDead.ContainsKey(pc.PlayerId)) return false;
        return base.CanUseVent(pc, ventId);
    }

    public override bool CanUseSabotage(PlayerControl pc)
    {
        if (PseudoDead.ContainsKey(pc.PlayerId)) return false;
        return base.CanUseSabotage(pc);
    }

    public override bool OnVote(PlayerControl voter, PlayerControl target)
    {
        if (voter == null || !PseudoDead.ContainsKey(voter.PlayerId)) return false;
        Utils.SendMessage(Translator.GetString("Akazukin_CannotVotePseudoDead"), voter.PlayerId);
        return true;
    }

    public override bool OnVanish(PlayerControl pc)
    {
        return !PseudoDead.ContainsKey(pc.PlayerId);
    }

    public override bool OnShapeshift(PlayerControl shapeshifter, PlayerControl target, bool shapeshifting)
    {
        return !PseudoDead.ContainsKey(shapeshifter.PlayerId);
    }

    public override bool KnowRole(PlayerControl seer, PlayerControl target)
    {
        if (target != null && PseudoDead.ContainsKey(target.PlayerId)) return false;
        return base.KnowRole(seer, target);
    }

    public override void ManipulateGameEndCheckCrew(PlayerState playerState, out bool keepGameGoing, out int countsAs)
    {
        countsAs = 1;

        if (!PseudoDead.TryGetValue(AkazukinId, out AkazukinState state))
        {
            keepGameGoing = false;
            return;
        }

        // 捕食中の赤ずきんは本当に死んでいるので、そのままだと復活を待たずに
        // インポスター勝利で試合が終わってしまう。猶予が切れるまで勝敗判定を止める。
        //
        // ⚠️ ただし「戻ってくる見込み」が消えたら保持もやめる。復活予約も無く捕食者も死んでいる状態で
        //    保持を続けると、生存者ゼロのまま試合が終わらず非モッド客が復帰不能の暗転に落ちる
        //    (2026-08-07 実機: キラー追放後に All: 0/2 のまま 36 秒以上停止)。
        PlayerControl killer = state.KillerId.GetPlayer();
        keepGameGoing = state.ReviveRequested || (killer && killer.IsAlive());
    }

    public static void SendRPC(byte playerId)
    {
        if (!Utils.DoRPC) return;

        MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(PlayerControl.LocalPlayer.NetId, (byte)CustomRPC.SyncAkazukinPseudoDeath, SendOption.Reliable);
        writer.Write(playerId);
        writer.Write(PseudoDead.ContainsKey(playerId));
        AmongUsClient.Instance.FinishRpcImmediately(writer);
    }

    public static void ReceiveRPC(MessageReader reader)
    {
        byte playerId = reader.ReadByte();
        bool isPseudoDead = reader.ReadBoolean();

        if (isPseudoDead)
        {
            PseudoDead.TryAdd(playerId, new AkazukinState());
        }
        else
        {
            PseudoDead.Remove(playerId);
        }
    }
}
