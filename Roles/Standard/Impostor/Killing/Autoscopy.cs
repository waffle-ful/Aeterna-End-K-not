using System;
using System.Collections.Generic;
using System.Linq;
using AmongUs.GameOptions;
using EndKnot.Modules;
using Hazel;
using UnityEngine;
using static EndKnot.Options;

namespace EndKnot.Roles;

// ============================================================
// Autoscopy (オートスコピー) — Impostor/Killing
//
// コンセプト:
//   Doppelganger (Neutral/Killing) の Impostor 版。キル成立時に killer と victim の
//   「スキン・表示ID・レベル・ペット・名刺・色」を相互スワップする。
//
// per-instance 状態は Skinwalker に倣い完全に自前 (Doppelganger の static dict は共有しない) —
// AllRoleClasses.Sort() 順に依存する共有 dict は Doppelganger の初アサイン契機で消える
// (GameState.cs の `if (!role.RoleExist(true)) Role.Init();`) ため。
//
// スワップ回数は Doppelganger のような自前 dict + 専用 RPC ではなく、既存の汎用
// AbilityUseLimit 機構 (SetAbilityUseLimit / RpcRemoveAbilityUse) に乗せる
// (WordKiller と同じ形。新規 CustomRPC を増やさない制約のため)。
//
// OnReportDeadBody (会議明け即時リセット) の「誰と入れ替わっているか」の解決は、
// Doppelganger の GetRealName() 文字列一致検索を踏襲しない — RpcChangeOutfitByData
// (公式鯖分岐) は pc.name を更新しないため、公式鯖では常に自分自身と一致してしまう。
// 代わりに SwappedIDs (killer, target) の実データから直接引く。
// ============================================================
public class Autoscopy : RoleBase
{
    private const int Id = 706500;
    public static List<byte> PlayerIdList = [];

    private static OptionItem KillCooldown;
    private static OptionItem CanVent;
    private static OptionItem ImpostorVision;
    private static OptionItem AutoscopyMaxSwaps;
    private static OptionItem AutoscopySwapLevel;
    private static OptionItem AutoscopyResetMode;
    private static OptionItem AutoscopyResetTimer;

    // Doppelganger の DGRM.* をそのまま再利用 (新規 lang キーは作らない)
    private static readonly string[] ResetModes =
    [
        "DGRM.None",
        "DGRM.OnMeeting",
        "DGRM.AfterTime"
    ];

    // Doppelganger.DoppelVictim 相当。playerId (本人 or 過去にスワップされたターゲット) →
    // その時点の本名。「今誰が自分の顔を被っているか」を辿るための逆引き元。
    public static Dictionary<byte, string> OriginalNames = [];

    // 現在纏っている外見。Camouflage.RpcSetSkin が最優先で参照する。
    public static Dictionary<byte, NetworkedPlayerInfo.PlayerOutfit> PresentSkin = [];

    // Add() 時点の素の外見 / レベル (リセットで自分が戻る先)。
    private static Dictionary<byte, NetworkedPlayerInfo.PlayerOutfit> DefaultSkin = [];
    private static Dictionary<byte, uint> DefaultLevel = [];

    // スワップ相手がスワップ直前に着ていた外見 / レベル (リセットで相手が戻る先)。
    // ⚠️ PresentSkin[相手] は「相手が今着ている外見」= こちらの顔なので復元元には使えない
    // (Doppelganger のリセットはこれを渡しているため相手側が no-op で、変装したまま残る)。
    private static Dictionary<byte, NetworkedPlayerInfo.PlayerOutfit> PartnerOriginalSkin = [];
    private static Dictionary<byte, uint> PartnerOriginalLevel = [];

    public static HashSet<(byte, byte)> SwappedIDs = [];

    private byte AutoscopyId;

    public override bool IsEnable => PlayerIdList.Count > 0;

    public override void SetupCustomOption()
    {
        StartSetup(Id)
            .AutoSetupOption(ref KillCooldown, 22.5f, new FloatValueRule(0f, 180f, 0.5f), OptionFormat.Seconds)
            .AutoSetupOption(ref CanVent, true)
            .AutoSetupOption(ref ImpostorVision, true)
            .AutoSetupOption(ref AutoscopyMaxSwaps, 9, new IntegerValueRule(1, 14, 1))
            .AutoSetupOption(ref AutoscopySwapLevel, true)
            .AutoSetupOption(ref AutoscopyResetMode, 0, ResetModes)
            .AutoSetupOption(ref AutoscopyResetTimer, 30f, new FloatValueRule(0f, 60f, 1f), OptionFormat.Seconds);
    }

    public override void Init()
    {
        PlayerIdList = [];
        OriginalNames = [];
        PresentSkin = [];
        DefaultSkin = [];
        DefaultLevel = [];
        PartnerOriginalSkin = [];
        PartnerOriginalLevel = [];
        SwappedIDs = [];
        AutoscopyId = byte.MaxValue;
    }

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);
        AutoscopyId = playerId;
        playerId.SetAbilityUseLimit(AutoscopyMaxSwaps.GetInt());

        PlayerControl pc = Utils.GetPlayerById(playerId);

        OriginalNames[playerId] = playerId == PlayerControl.LocalPlayer.PlayerId && Main.NickName.Length != 0 ? Main.NickName : pc.Data.PlayerName;
        DefaultSkin[playerId] = pc.CurrentOutfit;
        DefaultLevel[playerId] = pc.Data.PlayerLevel;
    }

    public override void Remove(byte playerId)
    {
        // Doppelganger と同じく sub-dict は掃除しない (死後もスワップ済み外見/名前解決が
        // Camouflage / MeetingHud / OutroPatch から参照され続けるため)。
        PlayerIdList.Remove(playerId);
    }

    public override void SetKillCooldown(byte id)
    {
        Main.AllPlayerKillCooldown[id] = KillCooldown.GetFloat();
    }

    public override bool CanUseImpostorVentButton(PlayerControl pc)
    {
        return CanVent.GetBool();
    }

    public override void ApplyGameOptions(IGameOptions opt, byte playerId)
    {
        opt.SetVision(ImpostorVision.GetBool());
    }

    // Skinwalker.RpcWearOutfit のコピー (null ガード付き)。Doppelganger.RpcChangeSkin には
    // このガードが無い。
    private static void RpcWearOutfit(PlayerControl pc, NetworkedPlayerInfo.PlayerOutfit newOutfit)
    {
        if (!AmongUsClient.Instance.AmHost) return;

        NetworkedPlayerInfo.PlayerOutfit current = pc.Data.DefaultOutfit;
        if (newOutfit.PlayerName == null || newOutfit.HatId == null || newOutfit.SkinId == null || newOutfit.VisorId == null || newOutfit.PetId == null || newOutfit.NamePlateId == null)
            Logger.Warn($"Null outfit field for {pc.Data?.PlayerName}: Name={newOutfit.PlayerName == null}, Hat={newOutfit.HatId == null}, Skin={newOutfit.SkinId == null}, Visor={newOutfit.VisorId == null}, Pet={newOutfit.PetId == null}, NamePlate={newOutfit.NamePlateId == null}", "Autoscopy.RpcWearOutfit");
        newOutfit.PlayerName ??= current.PlayerName ?? pc.Data.PlayerName ?? string.Empty;
        newOutfit.HatId ??= current.HatId ?? string.Empty;
        newOutfit.SkinId ??= current.SkinId ?? string.Empty;
        newOutfit.VisorId ??= current.VisorId ?? string.Empty;
        newOutfit.PetId ??= current.PetId ?? string.Empty;
        newOutfit.NamePlateId ??= current.NamePlateId ?? string.Empty;

        // 公式鯖では spoof RPC ではなく正規 serialize で見た目を同期 (anti-cheat 修正後)
        if (GameStates.CurrentServerType == GameStates.ServerType.Vanilla)
        {
            Main.AllPlayerNames[pc.PlayerId] = newOutfit.PlayerName;
            pc.RpcChangeOutfitByData(newOutfit);
            PresentSkin[pc.PlayerId] = newOutfit;
            return;
        }

        var sender = CustomRpcSender.Create($"Autoscopy.RpcWearOutfit({pc.Data.PlayerName})", SendOption.Reliable);

        pc.SetName(newOutfit.PlayerName);
        sender.AutoStartRpc(pc.NetId, RpcCalls.SetName)
            .Write(pc.Data.NetId)
            .Write(newOutfit.PlayerName)
            .EndRpc();

        Main.AllPlayerNames[pc.PlayerId] = newOutfit.PlayerName;

        pc.SetColor(newOutfit.ColorId);
        sender.AutoStartRpc(pc.NetId, RpcCalls.SetColor)
            .Write(pc.Data.NetId)
            .Write((byte)newOutfit.ColorId)
            .EndRpc();

        pc.SetHat(newOutfit.HatId, newOutfit.ColorId);
        pc.Data.DefaultOutfit.HatSequenceId += 10;
        sender.AutoStartRpc(pc.NetId, RpcCalls.SetHatStr)
            .Write(newOutfit.HatId)
            .Write(pc.GetNextRpcSequenceId(RpcCalls.SetHatStr))
            .EndRpc();

        pc.SetSkin(newOutfit.SkinId, newOutfit.ColorId);
        pc.Data.DefaultOutfit.SkinSequenceId += 10;
        sender.AutoStartRpc(pc.NetId, RpcCalls.SetSkinStr)
            .Write(newOutfit.SkinId)
            .Write(pc.GetNextRpcSequenceId(RpcCalls.SetSkinStr))
            .EndRpc();

        pc.SetVisor(newOutfit.VisorId, newOutfit.ColorId);
        pc.Data.DefaultOutfit.VisorSequenceId += 10;
        sender.AutoStartRpc(pc.NetId, RpcCalls.SetVisorStr)
            .Write(newOutfit.VisorId)
            .Write(pc.GetNextRpcSequenceId(RpcCalls.SetVisorStr))
            .EndRpc();

        pc.SetPet(newOutfit.PetId);
        pc.Data.DefaultOutfit.PetSequenceId += 10;
        sender.AutoStartRpc(pc.NetId, RpcCalls.SetPetStr)
            .Write(newOutfit.PetId)
            .Write(pc.GetNextRpcSequenceId(RpcCalls.SetPetStr))
            .EndRpc();

        pc.SetNamePlate(newOutfit.NamePlateId);
        pc.Data.DefaultOutfit.NamePlateSequenceId += 10;
        sender.AutoStartRpc(pc.NetId, RpcCalls.SetNamePlateStr)
            .Write(newOutfit.NamePlateId)
            .Write(pc.GetNextRpcSequenceId(RpcCalls.SetNamePlateStr))
            .EndRpc();

        sender.SendMessage();
        PresentSkin[pc.PlayerId] = newOutfit;
    }

    /// <summary>
    /// ゲーム終了時に Level を元へ戻すための引き当て (OutroPatch から呼ばれる)。
    /// ⚠️ Data.PlayerLevel はロビーへ戻っても残るため、戻さないと
    /// ホストが KickLowLevelPlayer を有効にしている場合に、キルに関与していない側まで
    /// 次のロビーで理由不明のキック + 再入場ブロックを受ける。
    /// リセットモード (None/OnMeeting/AfterTime) の選択に依存しない保険としてここに置く
    /// — 既定の None では RestoreSwap が一度も呼ばれず、他モードでも決着が先に来れば発火しない。
    /// </summary>
    public static bool TryGetOriginalLevel(byte playerId, out uint level)
    {
        level = 0;
        if (AutoscopySwapLevel == null || !AutoscopySwapLevel.GetBool()) return false;
        return DefaultLevel.TryGetValue(playerId, out level) || PartnerOriginalLevel.TryGetValue(playerId, out level);
    }

    /// <summary>スワップを解除し、双方を元の外見・レベル・表示IDへ戻す。</summary>
    // ゲーム終了時にホストから呼ぶ (Doppelganger と同型 — リセット設定「なし」が既定なので
    // RestoreSwap が一度も呼ばれず、見た目だけ入れ替わったままロビーへ持ち越される)。
    public static void RestoreOutfitOnGameEnd(byte id)
    {
        PlayerControl pc = Utils.GetPlayerById(id);
        if (pc == null) return;

        // ⚠️ Doppelganger と同じ理由で一致判定が要る (退避は Add() 時点で全員分入る)
        if (DefaultSkin.TryGetValue(id, out NetworkedPlayerInfo.PlayerOutfit ownSkin))
        {
            if (!ownSkin.Compare(pc.Data.DefaultOutfit)) RpcWearOutfit(pc, ownSkin);
        }
        else if (PartnerOriginalSkin.TryGetValue(id, out NetworkedPlayerInfo.PlayerOutfit partnerSkin))
        {
            if (!partnerSkin.Compare(pc.Data.DefaultOutfit)) RpcWearOutfit(pc, partnerSkin);
        }
    }

    private static void RestoreSwap(PlayerControl autoscopy, byte partnerId)
    {
        PlayerControl partner = Utils.GetPlayerById(partnerId);

        if (partner && PartnerOriginalSkin.TryGetValue(partnerId, out NetworkedPlayerInfo.PlayerOutfit partnerSkin))
        {
            RpcWearOutfit(partner, partnerSkin);

            if (AutoscopySwapLevel.GetBool() && PartnerOriginalLevel.TryGetValue(partnerId, out uint partnerLevel))
                partner.RpcSetLevel(partnerLevel);
        }

        if (DefaultSkin.TryGetValue(autoscopy.PlayerId, out NetworkedPlayerInfo.PlayerOutfit ownSkin))
            RpcWearOutfit(autoscopy, ownSkin);

        if (AutoscopySwapLevel.GetBool() && DefaultLevel.TryGetValue(autoscopy.PlayerId, out uint ownLevel))
            autoscopy.RpcSetLevel(ownLevel);

        // 表示ID のスワップも解除する。残すと外見だけ戻って番号が入れ替わったままになる。
        SwappedIDs.RemoveWhere(x => x.Item1 == autoscopy.PlayerId || x.Item2 == autoscopy.PlayerId);

        // ⚠️ OriginalNames は「その playerId の本名」台帳なので空文字で潰さない。
        // OutroPatch / Camouflage の gameEnd 復元がこの値をそのまま RpcSetName に渡すため、
        // 空にすると試合終了時に名前が消える (Doppelganger が踏んでいる罠)。
    }

    public static void OnCheckMurderEnd(PlayerControl killer, PlayerControl target)
    {
        if (Camouflage.IsCamouflage || Camouflager.IsActive || Main.PlayerStates[killer.PlayerId].Role is not Autoscopy { IsEnable: true }) return;

        if (target.IsShifted())
        {
            Logger.Info("Target was shapeshifting", "Autoscopy");
            return;
        }

        if (killer.GetAbilityUseLimit() < 1f) return;

        // notify:false — 直後に outfit swap 前の状態で NotifyRoles が走るのを避ける
        // (末尾の Utils.NotifyRoles() が swap 後の状態を確実にカバーする)
        killer.RpcRemoveAbilityUse(notify: false);

        byte killerId = killer.PlayerId;
        byte targetId = target.PlayerId;

        LateTask.New(TryTimedRestore, AutoscopyResetTimer.GetInt());

        string kname = killer.AmOwner && Main.NickName.Length != 0 ? Main.NickName : killer.Data.PlayerName;
        string tname = target.AmOwner && Main.NickName.Length != 0 ? Main.NickName : target.Data.PlayerName;

        NetworkedPlayerInfo.PlayerOutfit killerSkin = new NetworkedPlayerInfo.PlayerOutfit().Set(kname, killer.CurrentOutfit.ColorId, killer.CurrentOutfit.HatId, killer.CurrentOutfit.SkinId, killer.CurrentOutfit.VisorId, killer.CurrentOutfit.PetId, killer.CurrentOutfit.NamePlateId);
        NetworkedPlayerInfo.PlayerOutfit targetSkin = new NetworkedPlayerInfo.PlayerOutfit().Set(tname, target.CurrentOutfit.ColorId, target.CurrentOutfit.HatId, target.CurrentOutfit.SkinId, target.CurrentOutfit.VisorId, target.CurrentOutfit.PetId, target.CurrentOutfit.NamePlateId);

        // ⚠️ 上書きしない。相手が既に他人の顔を被っている (別の Autoscopy 保持者だった等) 場合、
        // ここでの tname は借り物の名前なので、先に記録済みの本名を潰すと試合終了時の復元
        // (OutroPatch / Camouflage の gameEnd 分岐) が借り物の名前で戻してしまう。
        OriginalNames.TryAdd(targetId, tname);

        uint killerLevel = killer.Data.PlayerLevel;
        uint targetLevel = target.Data.PlayerLevel;

        // リセットで相手を戻す先。RpcWearOutfit に渡すインスタンスとは別物にする
        // (渡した側は PresentSkin に格納されて後から null 埋めされるため、参照を共有しない)。
        PartnerOriginalSkin[targetId] = new NetworkedPlayerInfo.PlayerOutfit().Set(tname, target.CurrentOutfit.ColorId, target.CurrentOutfit.HatId, target.CurrentOutfit.SkinId, target.CurrentOutfit.VisorId, target.CurrentOutfit.PetId, target.CurrentOutfit.NamePlateId);
        PartnerOriginalLevel[targetId] = targetLevel;

        SwappedIDs.RemoveWhere(x => x.Item1 == killerId || x.Item2 == killerId);
        SwappedIDs.Add((killerId, targetId));

        RpcWearOutfit(target, killerSkin);
        Logger.Info("Changed target skin", "Autoscopy");
        RpcWearOutfit(killer, targetSkin);
        Logger.Info("Changed killer skin", "Autoscopy");

        if (AutoscopySwapLevel.GetBool())
        {
            target.RpcSetLevel(killerLevel);
            killer.RpcSetLevel(targetLevel);
        }

        target.Notify(Translator.GetString("AutoscopyWarning"));

        Utils.NotifyRoles();
        killer.ResetKillCooldown();
        killer.SetKillCooldown();

        target.SetRealKiller(killer);
        return;

        void TryTimedRestore()
        {
            if (GameStates.IsEnded) return;

            // 会議リセット等で既に解除済みなら二重に戻さない
            if (AutoscopyResetMode.GetValue() != 2 || !SwappedIDs.Contains((killerId, targetId))) return;

            PlayerControl ac = Utils.GetPlayerById(killerId);
            if (!ac) return;

            // ⚠️ 会議中 / 追放ワープアップ中は撃たない。RestoreSwap は 2 人分の外見書換
            // (+ レベル 2 本) を Reliable で送るため、投票終了〜会議クローズの Reliable バーストと
            // 重なると公式鯖のキック帯に入る (WordKiller の同型ガードと同じ理由)。
            // 窓が開くまで再武装する — 単純 return だと AfterTime リセットが無音で消える。
            if (GameStates.IsMeeting || ExileController.Instance || AntiBlackout.SkipTasks)
            {
                LateTask.New(TryTimedRestore, 3f, "Autoscopy.ResetRetry", log: false);
                return;
            }

            RestoreSwap(ac, targetId);

            if (GameStates.IsInTask) Utils.NotifyRoles();
        }
    }

    public override void OnReportDeadBody()
    {
        try
        {
            if (AutoscopyResetMode.GetValue() != 1 || AutoscopyId.GetAbilityUseLimit() >= AutoscopyMaxSwaps.GetInt()) return;

            PlayerControl pc = Utils.GetPlayerById(AutoscopyId);
            if (!pc) return;

            // GetRealName() 文字列一致検索 (Doppelganger 方式) は使わない — 公式鯖の
            // RpcChangeOutfitByData 分岐は pc.name を更新しないため常に自分自身に一致してしまう。
            // SwappedIDs の実データから直接パートナーを引く。
            if (!SwappedIDs.FindFirst(x => x.Item1 == AutoscopyId, out var pair)) return;

            RestoreSwap(pc, pair.Item2);
        }
        catch (Exception e) { Utils.ThrowException(e); }
    }
}
