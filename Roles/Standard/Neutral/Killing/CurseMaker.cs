using AmongUs.GameOptions;
using EndKnot.Modules;
using Hazel;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using static EndKnot.Options;
using static EndKnot.Translator;

namespace EndKnot.Roles;

public class CurseMaker : RoleBase
{
    private const int Id = 704200;
    public static List<byte> PlayerIdList = [];

    private static OptionItem PetCooldown;
    private static OptionItem CurseDistance;
    private static OptionItem NoroiTime;
    private static OptionItem DelTurn;
    public static OptionItem CanSoloWin;

    private byte _curseMakerId;
    private byte ChargingTargetId;
    private float ChargeTimer;
    private Dictionary<byte, int> CursedPlayers = [];

    public override bool IsEnable => PlayerIdList.Count > 0;

    public override void SetupCustomOption()
    {
        SetupRoleOptions(Id, TabGroup.NeutralRoles, CustomRoles.CurseMaker);

        PetCooldown = new FloatOptionItem(Id + 10, "KillCooldown", new(0f, 180f, 0.5f), 20f, TabGroup.NeutralRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.CurseMaker])
            .SetValueFormat(OptionFormat.Seconds);

        CurseDistance = new FloatOptionItem(Id + 11, "CurseMakerCurseDistance", new(0.5f, 5f, 0.25f), 1.75f, TabGroup.NeutralRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.CurseMaker])
            .SetValueFormat(OptionFormat.Multiplier);

        NoroiTime = new FloatOptionItem(Id + 12, "CurseMakerNoroiTime", new(0.5f, 30f, 0.5f), 3f, TabGroup.NeutralRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.CurseMaker])
            .SetValueFormat(OptionFormat.Seconds);

        DelTurn = new IntegerOptionItem(Id + 13, "CurseMakerDelTurn", new(1, 30, 1), 4, TabGroup.NeutralRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.CurseMaker]);

        CanSoloWin = new BooleanOptionItem(Id + 14, "CurseMakerCanSoloWin", true, TabGroup.NeutralRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.CurseMaker]);
    }

    public override void Init()
    {
        PlayerIdList = [];
    }

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);
        _curseMakerId = playerId;
        ChargingTargetId = byte.MaxValue;
        ChargeTimer = 0f;
        CursedPlayers = [];
    }

    public override void Remove(byte playerId)
    {
        PlayerIdList.Remove(playerId);
    }

    public override void SetKillCooldown(byte id)
    {
        Main.AllPlayerKillCooldown[id] = PetCooldown.GetFloat();
    }

    public override bool CanUseKillButton(PlayerControl pc) => false;

    public override void ApplyGameOptions(IGameOptions opt, byte id)
    {
        opt.SetVision(false);
    }

    // Pet: context-sensitive — start charge, cancel charge, or detonate
    public override void OnPet(PlayerControl pc)
    {
        if (!pc.IsAlive()) return;

        // Cancel active charge
        if (ChargingTargetId != byte.MaxValue)
        {
            ChargingTargetId = byte.MaxValue;
            ChargeTimer = 0f;
            SendRPCCharging();
            Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc);
            pc.Notify(GetString("CurseMakerChargeCanceled"));
            return;
        }

        // Start charge on nearby player
        if (FastVector2.TryGetClosestPlayerInRangeTo(pc, CurseDistance.GetFloat(), out PlayerControl target))
        {
            if (CursedPlayers.ContainsKey(target.PlayerId))
            {
                pc.Notify(string.Format(GetString("CurseMakerAlreadyCursed"), target.GetRealName()));
                return;
            }

            ChargingTargetId = target.PlayerId;
            ChargeTimer = 0f;
            SendRPCCharging();
            Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc);
            pc.Notify(string.Format(GetString("CurseMakerCharging"), target.GetRealName()));
            return;
        }

        // Detonate if cursed players exist
        if (CursedPlayers.Count > 0)
        {
            Detonate(pc);
            return;
        }

        pc.Notify(GetString("CurseMakerNoTarget"));
    }

    private void Detonate(PlayerControl pc)
    {
        bool soloWin = CanSoloWin.GetBool();
        byte[] cursedIds = [.. CursedPlayers.Keys];
        Logger.Info($"CurseMaker {pc.GetNameWithRole()} detonates {cursedIds.Length} cursed player(s)", "CurseMaker");

        LateTask.New(() =>
        {
            if (!GameStates.IsInTask) return;

            foreach (byte id in cursedIds)
            {
                PlayerControl cursed = Utils.GetPlayerById(id);
                if (cursed != null && cursed.IsAlive())
                    cursed.Suicide(PlayerState.DeathReason.Spell, pc);
            }

            if (soloWin && GameStates.IsInTask)
            {
                CustomWinnerHolder.ResetAndSetWinner(CustomWinner.CurseMaker);
                CustomWinnerHolder.WinnerIds.Add(pc.PlayerId);
            }

            pc.Suicide(PlayerState.DeathReason.Bombed);
        }, 0.1f, "CurseMaker Detonate", true);
    }

    public override void OnFixedUpdate(PlayerControl pc)
    {
        if (!GameStates.IsInTask || !pc.IsAlive() || ChargingTargetId == byte.MaxValue) return;

        PlayerControl target = Utils.GetPlayerById(ChargingTargetId);
        if (target == null || !target.IsAlive())
        {
            ChargingTargetId = byte.MaxValue;
            ChargeTimer = 0f;
            SendRPCCharging();
            Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc);
            return;
        }

        float dist = Vector2.Distance(pc.Pos(), target.Pos());
        if (dist <= CurseDistance.GetFloat())
        {
            ChargeTimer += Time.fixedDeltaTime;
            if (ChargeTimer >= NoroiTime.GetFloat())
            {
                CursedPlayers.TryAdd(target.PlayerId, 0);
                ChargingTargetId = byte.MaxValue;
                ChargeTimer = 0f;
                SendRPCCursed(target.PlayerId);
                SendRPCCharging();
                Utils.NotifyRoles();
                Logger.Info($"CurseMaker cursed {target.GetNameWithRole()}", "CurseMaker");
            }
        }
        else
        {
            ChargingTargetId = byte.MaxValue;
            ChargeTimer = 0f;
            SendRPCCharging();
            Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc);
        }
    }

    public override void OnReportDeadBody()
    {
        ChargingTargetId = byte.MaxValue;
        ChargeTimer = 0f;

        List<byte> toRemove = [];
        foreach ((byte id, int turns) in CursedPlayers)
        {
            PlayerControl p = Utils.GetPlayerById(id);
            if (p == null || !p.IsAlive() || turns + 1 >= DelTurn.GetInt())
                toRemove.Add(id);
            else
                CursedPlayers[id] = turns + 1;
        }

        toRemove.ForEach(id => CursedPlayers.Remove(id));
        SendRPCFullSync();
    }

    private void SendRPCCharging()
    {
        if (!Utils.DoRPC) return;
        MessageWriter w = Utils.CreateRPC(CustomRPC.SyncRoleData);
        w.Write(_curseMakerId);
        w.WritePacked(1);
        w.Write(ChargingTargetId);
        Utils.EndRPC(w);
    }

    private void SendRPCCursed(byte targetId)
    {
        if (!Utils.DoRPC) return;
        MessageWriter w = Utils.CreateRPC(CustomRPC.SyncRoleData);
        w.Write(_curseMakerId);
        w.WritePacked(2);
        w.Write(targetId);
        Utils.EndRPC(w);
    }

    private void SendRPCFullSync()
    {
        if (!Utils.DoRPC) return;
        MessageWriter w = Utils.CreateRPC(CustomRPC.SyncRoleData);
        w.Write(_curseMakerId);
        w.WritePacked(3);
        w.Write(ChargingTargetId);
        w.WritePacked(CursedPlayers.Count);
        foreach ((byte id, int turns) in CursedPlayers)
        {
            w.Write(id);
            w.WritePacked(turns);
        }
        Utils.EndRPC(w);
    }

    public void ReceiveRPC(MessageReader reader)
    {
        switch (reader.ReadPackedInt32())
        {
            case 1:
                ChargingTargetId = reader.ReadByte();
                ChargeTimer = 0f;
                break;
            case 2:
                CursedPlayers.TryAdd(reader.ReadByte(), 0);
                break;
            case 3:
                ChargingTargetId = reader.ReadByte();
                ChargeTimer = 0f;
                CursedPlayers.Clear();
                int count = reader.ReadPackedInt32();
                for (int i = 0; i < count; i++)
                {
                    byte id = reader.ReadByte();
                    int turns = reader.ReadPackedInt32();
                    CursedPlayers[id] = turns;
                }
                break;
        }
    }

    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud = false, bool meeting = false)
    {
        // Show curse markers on other players visible to CurseMaker
        if (seer.PlayerId == _curseMakerId && seer.PlayerId != target.PlayerId)
        {
            if (CursedPlayers.ContainsKey(target.PlayerId)) return "<color=#554d59>†</color>";
            if (target.PlayerId == ChargingTargetId) return "<color=#554d59>◇</color>";
            return string.Empty;
        }

        if (seer.PlayerId != _curseMakerId || seer.PlayerId != target.PlayerId) return string.Empty;
        if (!hud && !seer.IsModdedClient()) return string.Empty;

        if (ChargingTargetId != byte.MaxValue)
        {
            PlayerControl t = Utils.GetPlayerById(ChargingTargetId);
            string tName = t != null ? t.GetRealName() : "?";
            float pct = NoroiTime.GetFloat() > 0f ? ChargeTimer / NoroiTime.GetFloat() * 100f : 100f;
            return string.Format(GetString("CurseMakerChargingHUD"), tName, (int)pct);
        }

        if (CursedPlayers.Count > 0)
            return string.Format(GetString("CurseMakerCursedHUD"), CursedPlayers.Count);

        return GetString("CurseMakerPetToMarkHUD");
    }
}
