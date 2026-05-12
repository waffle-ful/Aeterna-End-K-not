using System.Collections.Generic;
using System.Linq;
using AmongUs.GameOptions;
using EndKnot.Modules;
using EndKnot.Patches;
using Hazel;
using UnityEngine;
using static EndKnot.Options;

namespace EndKnot.Roles;

public class Skinwalker : RoleBase
{
    private const int Id = 704800;
    public static List<byte> PlayerIdList = [];

    private static OptionItem KillCooldown;
    private static OptionItem CanVent;
    private static OptionItem ImpostorVision;
    private static OptionItem MaxUsage;
    private static OptionItem CopyLevel;
    private static OptionItem AbilityUseGainWithEachKill;

    // 偽装中の Skinwalker.PlayerId → 情報
    public static Dictionary<byte, Vector2> WornCorpsePos = [];
    public static Dictionary<byte, byte> WornCorpseTargetId = [];
    public static Dictionary<byte, NetworkedPlayerInfo.PlayerOutfit> OriginalOutfit = [];
    public static Dictionary<byte, uint> OriginalLevel = [];
    // Camouflage.RpcSetSkin が会議遷移で参照する偽装中 outfit dict
    public static Dictionary<byte, NetworkedPlayerInfo.PlayerOutfit> SkinwalkerPresentSkin = [];

    public override bool IsEnable => PlayerIdList.Count > 0;

    public override void SetupCustomOption()
    {
        SetupRoleOptions(Id, TabGroup.ImpostorRoles, CustomRoles.Skinwalker);

        KillCooldown = new FloatOptionItem(Id + 2, "KillCooldown", new(0f, 180f, 0.5f), 30f, TabGroup.ImpostorRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Skinwalker])
            .SetValueFormat(OptionFormat.Seconds);

        CanVent = new BooleanOptionItem(Id + 3, "CanVent", true, TabGroup.ImpostorRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Skinwalker]);

        ImpostorVision = new BooleanOptionItem(Id + 4, "ImpostorVision", true, TabGroup.ImpostorRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Skinwalker]);

        MaxUsage = new IntegerOptionItem(Id + 5, "SkinwalkerMaxUsage", new(1, 15, 1), 3, TabGroup.ImpostorRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Skinwalker])
            .SetValueFormat(OptionFormat.Times);

        CopyLevel = new BooleanOptionItem(Id + 6, "SkinwalkerCopyLevel", true, TabGroup.ImpostorRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Skinwalker]);

        AbilityUseGainWithEachKill = new FloatOptionItem(Id + 7, "AbilityUseGainWithEachKill", new(0f, 5f, 0.1f), 0f, TabGroup.ImpostorRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Skinwalker])
            .SetValueFormat(OptionFormat.Times);
    }

    public override void Init()
    {
        PlayerIdList = [];
        WornCorpsePos = [];
        WornCorpseTargetId = [];
        OriginalOutfit = [];
        OriginalLevel = [];
        SkinwalkerPresentSkin = [];
    }

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);
        playerId.SetAbilityUseLimit(MaxUsage.GetInt());
    }

    public override void Remove(byte playerId)
    {
        PlayerIdList.Remove(playerId);
        WornCorpsePos.Remove(playerId);
        WornCorpseTargetId.Remove(playerId);
        OriginalOutfit.Remove(playerId);
        OriginalLevel.Remove(playerId);
        SkinwalkerPresentSkin.Remove(playerId);
    }

    public override void SetKillCooldown(byte id)
    {
        Main.AllPlayerKillCooldown[id] = KillCooldown.GetFloat();
    }

    public override bool CanUseImpostorVentButton(PlayerControl pc) => CanVent.GetBool();

    public override void ApplyGameOptions(IGameOptions opt, byte playerId)
    {
        opt.SetVision(ImpostorVision.GetBool());
    }

    public override void OnMurder(PlayerControl killer, PlayerControl target)
    {
        if (killer.Is(CustomRoles.Skinwalker) && AbilityUseGainWithEachKill.GetFloat() > 0)
            killer.RpcIncreaseAbilityUseLimitBy(AbilityUseGainWithEachKill.GetFloat());
    }

    public override bool CheckReportDeadBody(PlayerControl reporter, NetworkedPlayerInfo target, PlayerControl killer)
    {
        if (!reporter.Is(CustomRoles.Skinwalker)) return true;

        if (reporter.GetAbilityUseLimit() < 1)
        {
            reporter.Notify(Translator.GetString("SkinwalkerNoUsageLeft"));
            return true;
        }

        if (WornCorpseTargetId.ContainsKey(reporter.PlayerId))
        {
            reporter.Notify(Translator.GetString("SkinwalkerAlreadyWorn"));
            return true;
        }

        if (target == null || target.Disconnected) return true;
        if (Cleaner.CleanerBodies.Contains(target.PlayerId)) return true;

        PlayerControl tpc = target.Object;
        if (tpc == null) return true;

        DeadBody body = Object.FindObjectsOfType<DeadBody>().FirstOrDefault(x => x.ParentId == target.PlayerId && x.enabled);
        if (body == null) return true;

        Vector2 pos = body.TruePosition;

        WornCorpsePos[reporter.PlayerId] = pos;
        WornCorpseTargetId[reporter.PlayerId] = target.PlayerId;
        OriginalOutfit[reporter.PlayerId] = Camouflage.PlayerSkins.GetValueOrDefault(reporter.PlayerId, reporter.CurrentOutfit);
        OriginalLevel[reporter.PlayerId] = reporter.Data.PlayerLevel;

        body.enabled = false;
        ReportDeadBodyPatch.AlreadyReportedBodies.Add(target.PlayerId);

        NetworkedPlayerInfo.PlayerOutfit targetOutfit = Camouflage.PlayerSkins.GetValueOrDefault(target.PlayerId, tpc.CurrentOutfit);
        var newOutfit = new NetworkedPlayerInfo.PlayerOutfit().Set(
            targetOutfit.PlayerName,
            targetOutfit.ColorId,
            targetOutfit.HatId,
            targetOutfit.SkinId,
            targetOutfit.VisorId,
            targetOutfit.PetId,
            targetOutfit.NamePlateId);

        RpcWearOutfit(reporter, newOutfit);

        if (CopyLevel.GetBool())
            reporter.RpcSetLevel(tpc.Data.PlayerLevel);

        reporter.RpcRemoveAbilityUse();
        reporter.Notify(string.Format(Translator.GetString("SkinwalkerWorn"), targetOutfit.PlayerName));

        return false;
    }

    public override void OnPet(PlayerControl pc)
    {
        if (!WornCorpseTargetId.ContainsKey(pc.PlayerId)) return;

        byte targetId = WornCorpseTargetId[pc.PlayerId];
        Vector2 pos = WornCorpsePos[pc.PlayerId];
        PlayerControl tpc = targetId.GetPlayer();

        if (OriginalOutfit.TryGetValue(pc.PlayerId, out NetworkedPlayerInfo.PlayerOutfit origOutfit))
            RpcWearOutfit(pc, origOutfit);

        SkinwalkerPresentSkin.Remove(pc.PlayerId);

        if (CopyLevel.GetBool() && OriginalLevel.TryGetValue(pc.PlayerId, out uint origLevel))
            pc.RpcSetLevel(origLevel);

        if (tpc != null)
            Utils.RpcCreateDeadBody(pos, (byte)tpc.Data.DefaultOutfit.ColorId, tpc);

        ReportDeadBodyPatch.AlreadyReportedBodies.Remove(targetId);

        WornCorpsePos.Remove(pc.PlayerId);
        WornCorpseTargetId.Remove(pc.PlayerId);
        OriginalOutfit.Remove(pc.PlayerId);
        OriginalLevel.Remove(pc.PlayerId);

        pc.Notify(Translator.GetString("SkinwalkerUnworn"));
    }

    public override void OnFixedUpdate(PlayerControl pc)
    {
        if (GameStates.IsMeeting) return;
        if (!WornCorpseTargetId.ContainsKey(pc.PlayerId)) return;
        if (pc.IsAlive()) return;

        byte targetId = WornCorpseTargetId[pc.PlayerId];
        Vector2 pos = WornCorpsePos[pc.PlayerId];
        PlayerControl tpc = targetId.GetPlayer();

        if (tpc != null && Main.IntroDestroyed)
            Utils.RpcCreateDeadBody(pos, (byte)tpc.Data.DefaultOutfit.ColorId, tpc);

        ReportDeadBodyPatch.AlreadyReportedBodies.Remove(targetId);

        WornCorpsePos.Remove(pc.PlayerId);
        WornCorpseTargetId.Remove(pc.PlayerId);
        OriginalOutfit.Remove(pc.PlayerId);
        OriginalLevel.Remove(pc.PlayerId);
        SkinwalkerPresentSkin.Remove(pc.PlayerId);
    }

    private static void RpcWearOutfit(PlayerControl pc, NetworkedPlayerInfo.PlayerOutfit newOutfit)
    {
        if (!AmongUsClient.Instance.AmHost) return;

        var sender = CustomRpcSender.Create($"Skinwalker.RpcWearOutfit({pc.Data.PlayerName})", SendOption.Reliable);

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
        SkinwalkerPresentSkin[pc.PlayerId] = newOutfit;
    }
}
