using System;
using System.Collections.Generic;
using AmongUs.GameOptions;
using EndKnot.Modules;
using Hazel;
using UnityEngine;
using static EndKnot.Options;

namespace EndKnot.Roles;

public class Doppelganger : RoleBase
{
    private const int Id = 648100;
    public static List<byte> PlayerIdList = [];

    private static OptionItem KillCooldown;
    private static OptionItem CanVent;
    private static OptionItem ImpostorVision;
    private static OptionItem MaxSteals;
    private static OptionItem ResetMode;
    private static OptionItem ResetTimer;

    private static int LocalPlayerChangeSkinTimes;

    public static HashSet<(byte, byte)> SwappedIDs = [];
    public static Dictionary<byte, string> DoppelVictim = [];
    public static Dictionary<byte, NetworkedPlayerInfo.PlayerOutfit> DoppelPresentSkin = [];
    private static Dictionary<byte, int> TotalSteals = [];
    private static Dictionary<byte, NetworkedPlayerInfo.PlayerOutfit> DoppelDefaultSkin = [];

    // スワップ相手がスワップ直前に着ていた外見 (リセットで相手が戻る先)。
    // ⚠️ DoppelPresentSkin[相手] は「相手が今着ている外見」= こちらの顔なので復元元には使えない。
    private static Dictionary<byte, NetworkedPlayerInfo.PlayerOutfit> DoppelPartnerOriginalSkin = [];

    // 本人 + スワップ被害者の「本物の Level」台帳。DoppelVictim と同じ規律で上書きしない
    // (借り物の Level を記録すると復元が壊れる)。Level はロビーへ持ち越されるため、
    // 戻さないと KickLowLevelPlayer が無実のプレイヤーを蹴る。
    public static Dictionary<byte, uint> DoppelOriginalLevel = [];

    private static readonly string[] ResetModes =
    [
        "DGRM.None",
        "DGRM.OnMeeting",
        "DGRM.AfterTime"
    ];

    private byte DGId;
    private static Color32 ShadeColor;

    public override bool IsEnable => PlayerIdList.Count > 0;

    public override void SetupCustomOption()
    {
        SetupRoleOptions(Id, TabGroup.NeutralRoles, CustomRoles.Doppelganger);

        MaxSteals = new IntegerOptionItem(Id + 10, "DoppelMaxSteals", new(1, 14, 1), 9, TabGroup.NeutralRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Doppelganger]);

        KillCooldown = new FloatOptionItem(Id + 11, "KillCooldown", new(0f, 180f, 0.5f), 22.5f, TabGroup.NeutralRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Doppelganger])
            .SetValueFormat(OptionFormat.Seconds);

        CanVent = new BooleanOptionItem(Id + 14, "CanVent", false, TabGroup.NeutralRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Doppelganger]);

        ImpostorVision = new BooleanOptionItem(Id + 15, "ImpostorVision", false, TabGroup.NeutralRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Doppelganger]);

        ResetMode = new StringOptionItem(Id + 12, "DGResetMode", ResetModes, 0, TabGroup.NeutralRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Doppelganger]);

        ResetTimer = new FloatOptionItem(Id + 13, "DGResetTimer", new(0f, 60f, 1f), 30f, TabGroup.NeutralRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Doppelganger])
            .SetValueFormat(OptionFormat.Seconds);
    }

    public override void Init()
    {
        PlayerIdList = [];
        SwappedIDs = [];
        DoppelVictim = [];
        TotalSteals = [];
        DoppelPresentSkin = [];
        DoppelDefaultSkin = [];
        DoppelPartnerOriginalSkin = [];
        DoppelOriginalLevel = [];
        DGId = byte.MaxValue;

        LocalPlayerChangeSkinTimes = 0;

        ShadeColor = Utils.GetRoleColor(CustomRoles.Doppelganger).ShadeColor(0.25f);
    }

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);
        DGId = playerId;
        TotalSteals[playerId] = 0;
        PlayerControl pc = Utils.GetPlayerById(playerId);

        if (playerId == PlayerControl.LocalPlayer.PlayerId && Main.NickName.Length != 0)
            DoppelVictim[playerId] = Main.NickName;
        else
            DoppelVictim[playerId] = pc.Data.PlayerName;

        DoppelDefaultSkin[playerId] = pc.CurrentOutfit;
        DoppelOriginalLevel[playerId] = pc.Data.PlayerLevel;
    }

    public override void Remove(byte playerId)
    {
        PlayerIdList.Remove(playerId);
    }

    private void SendRPC(byte playerId)
    {
        if (!IsEnable || !Utils.DoRPC) return;

        MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(PlayerControl.LocalPlayer.NetId, (byte)CustomRPC.SetDoppelgangerStealLimit, SendOption.Reliable);
        writer.Write(playerId);
        writer.Write(TotalSteals[playerId]);
        AmongUsClient.Instance.FinishRpcImmediately(writer);
    }

    public static void ReceiveRPC(MessageReader reader)
    {
        byte PlayerId = reader.ReadByte();
        int Limit = reader.ReadInt32();
        if (!TotalSteals.TryAdd(PlayerId, 0)) TotalSteals[PlayerId] = Limit;
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

    private static NetworkedPlayerInfo.PlayerOutfit Set(NetworkedPlayerInfo.PlayerOutfit instance, string playerName, int colorId, string hatId, string skinId, string visorId, string petId, string nameplateId)
    {
        instance.PlayerName = playerName;
        instance.ColorId = colorId;
        instance.HatId = hatId;
        instance.SkinId = skinId;
        instance.VisorId = visorId;
        instance.PetId = petId;
        instance.NamePlateId = nameplateId;
        return instance;
    }

    private static void RpcChangeSkin(PlayerControl pc, NetworkedPlayerInfo.PlayerOutfit newOutfit)
    {
        if (!AmongUsClient.Instance.AmHost) return;

        // 公式鯖では spoof RPC ではなく正規 serialize で見た目を同期 (anti-cheat 修正後)
        if (GameStates.CurrentServerType == GameStates.ServerType.Vanilla)
        {
            Main.AllPlayerNames[pc.PlayerId] = newOutfit.PlayerName;
            pc.RpcChangeOutfitByData(newOutfit);
            return;
        }

        var sender = CustomRpcSender.Create($"Doppelganger.RpcChangeSkin({pc.Data.PlayerName})", SendOption.Reliable);

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
        DoppelPresentSkin[pc.PlayerId] = newOutfit;

        if (pc.AmOwner)
        {
            LocalPlayerChangeSkinTimes++;
            if (LocalPlayerChangeSkinTimes >= 2) Achievements.Type.Mimicry.Complete();
        }
    }

    /// <summary>スワップを解除し、双方を元の外見・表示IDへ戻す。</summary>
    private static void RestoreSwap(PlayerControl doppel, byte partnerId)
    {
        PlayerControl partner = Utils.GetPlayerById(partnerId);

        if (partner && DoppelPartnerOriginalSkin.TryGetValue(partnerId, out NetworkedPlayerInfo.PlayerOutfit partnerSkin))
            RpcChangeSkin(partner, partnerSkin);

        if (DoppelDefaultSkin.TryGetValue(doppel.PlayerId, out NetworkedPlayerInfo.PlayerOutfit ownSkin))
            RpcChangeSkin(doppel, ownSkin);

        // Level も外見と一緒に戻す (残すと番号だけ入れ替わったままになる)
        if (partner && DoppelOriginalLevel.TryGetValue(partnerId, out uint partnerLevel))
            partner.RpcSetLevel(partnerLevel);

        if (DoppelOriginalLevel.TryGetValue(doppel.PlayerId, out uint ownLevel))
            doppel.RpcSetLevel(ownLevel);

        // 表示ID のスワップも解除する。残すと外見だけ戻って番号が入れ替わったままになる。
        SwappedIDs.RemoveWhere(x => x.Item1 == doppel.PlayerId || x.Item2 == doppel.PlayerId);

        // ⚠️ DoppelVictim は「その playerId の本名」台帳なので空文字で潰さない。
        // OutroPatch / Camouflage の gameEnd 復元がこの値をそのまま RpcSetName に渡すため、
        // 空にすると試合終了時に名前が消える。
    }

    public static void OnCheckMurderEnd(PlayerControl killer, PlayerControl target)
    {
        if (Camouflage.IsCamouflage || Camouflager.IsActive || Main.PlayerStates[killer.PlayerId].Role is not Doppelganger { IsEnable: true } dg) return;

        if (target.IsShifted())
        {
            Logger.Info("Target was shapeshifting", "Doppelganger");
            return;
        }

        if (TotalSteals[killer.PlayerId] >= MaxSteals.GetInt())
        {
            TotalSteals[killer.PlayerId] = MaxSteals.GetInt();
            return;
        }

        TotalSteals[killer.PlayerId]++;

        byte killerId = killer.PlayerId;
        byte targetId = target.PlayerId;
        uint killerLevel = killer.Data.PlayerLevel;
        uint targetLevel = target.Data.PlayerLevel;

        LateTask.New(() =>
        {
            if (ResetMode.GetValue() != 2 || TotalSteals[killerId] <= 0 || !killer) return;
            if (!SwappedIDs.Contains((killerId, targetId))) return; // 会議リセット等で既に解除済み

            RestoreSwap(killer, targetId);

            if (GameStates.IsInTask && !ExileController.Instance && !AntiBlackout.SkipTasks)
                Utils.NotifyRoles();
        }, ResetTimer.GetInt());

        string kname;

        if (killer.AmOwner && Main.NickName.Length != 0)
            kname = Main.NickName;
        else
            kname = killer.Data.PlayerName;

        string tname;

        if (target.AmOwner && Main.NickName.Length != 0)
            tname = Main.NickName;
        else
            tname = target.Data.PlayerName;

        NetworkedPlayerInfo.PlayerOutfit killerSkin = Set(new(), kname, killer.CurrentOutfit.ColorId, killer.CurrentOutfit.HatId, killer.CurrentOutfit.SkinId, killer.CurrentOutfit.VisorId, killer.CurrentOutfit.PetId, killer.CurrentOutfit.NamePlateId);
        NetworkedPlayerInfo.PlayerOutfit targetSkin = Set(new(), tname, target.CurrentOutfit.ColorId, target.CurrentOutfit.HatId, target.CurrentOutfit.SkinId, target.CurrentOutfit.VisorId, target.CurrentOutfit.PetId, target.CurrentOutfit.NamePlateId);

        // ⚠️ 上書きしない。相手が既に他人の顔を被っていると tname は借り物の名前になり、
        // 先に記録済みの本名を潰すと試合終了時の復元が借り物の名前で戻してしまう。
        DoppelVictim.TryAdd(target.PlayerId, tname);

        // リセットで相手を戻す先。RpcChangeSkin に渡すインスタンスとは別物にする
        // (渡した側は DoppelPresentSkin に格納されるため参照を共有しない)。
        DoppelPartnerOriginalSkin[targetId] = Set(new(), tname, target.CurrentOutfit.ColorId, target.CurrentOutfit.HatId, target.CurrentOutfit.SkinId, target.CurrentOutfit.VisorId, target.CurrentOutfit.PetId, target.CurrentOutfit.NamePlateId);

        SwappedIDs.RemoveWhere(x => x.Item1 == killer.PlayerId || x.Item2 == killer.PlayerId);
        SwappedIDs.Add((killer.PlayerId, target.PlayerId));


        RpcChangeSkin(target, killerSkin);
        Logger.Info("Changed target skin", "Doppelganger");
        RpcChangeSkin(killer, targetSkin);
        Logger.Info("Changed killer skin", "Doppelganger");

        // Level も交換する。外見と表示IDだけ入れ替えても Level が残っていると即座に見破られる。
        // 交換するのは現在値 (外見と同じ挙動)、復元元は本物の台帳から引く。
        DoppelOriginalLevel.TryAdd(targetId, targetLevel);
        killer.RpcSetLevel(targetLevel);
        target.RpcSetLevel(killerLevel);

        target.Notify(Translator.GetString("DoppelgangerWarning"));

        dg.SendRPC(killer.PlayerId);
        Utils.NotifyRoles();
        killer.ResetKillCooldown();
        killer.SetKillCooldown();

        target.SetRealKiller(killer);
    }

    public override void OnReportDeadBody()
    {
        try
        {
            if (ResetMode.GetValue() == 1 && TotalSteals[DGId] > 0)
            {
                PlayerControl pc = Utils.GetPlayerById(DGId);
                if (!pc) return;

                // 「今の表示名が自分の本名になっている人」を探す文字列一致は使わない —
                // 公式鯖分岐の RpcChangeOutfitByData は pc.name を更新しないため成立しない。
                // SwappedIDs の実データから直接パートナーを引く。
                if (!SwappedIDs.FindFirst(x => x.Item1 == DGId, out var pair)) return;

                RestoreSwap(pc, pair.Item2);
            }
        }
        catch (Exception e) { Utils.ThrowException(e); }
    }

    public override string GetProgressText(byte playerId, bool comms)
    {
        return Utils.ColorString(TotalSteals[playerId] < MaxSteals.GetInt() ? Utils.GetRoleColor(CustomRoles.Doppelganger).ShadeColor(0.25f) : Color.gray, TotalSteals.TryGetValue(playerId, out int stealLimit) ? $"({MaxSteals.GetInt() - stealLimit})" : "Invalid");
    }
}