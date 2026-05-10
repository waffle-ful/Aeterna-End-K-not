using System;
using System.Collections.Generic;
using System.Linq;
using AmongUs.GameOptions;
using EndKnot.Modules;
using Hazel;
using UnityEngine;

namespace EndKnot.Roles;

public class Akazukin : RoleBase
{
    private const int Id = 703760;
    public static List<byte> PlayerIdList = [];
    public static Dictionary<byte, AkazukinState> PseudoDead = [];

    private static OptionItem RevivalGracePeriod;
    private static OptionItem DisplayDeathReasonInName;
    private static OptionItem CanReviveAfterMeeting;
    private static OptionItem ReviveAtOriginalPosition;

    private int Count;

    public override bool IsEnable => PlayerIdList.Count > 0;

    public class AkazukinState
    {
        public byte KillerId;
        public PlayerState.DeathReason DeathReason;
        public Vector2 OriginalPos;
        public float OriginalSpeed;
        public DateTime PseudoDeathStart;
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
    }

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);
    }

    public override void Remove(byte playerId)
    {
        PlayerIdList.Remove(playerId);
        PseudoDead.Remove(playerId);
    }

    public static bool IsPseudoDead(byte id) => PseudoDead.ContainsKey(id);

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

        Utils.RpcCreateDeadBody(killPos, (byte)target.Data.DefaultOutfit.ColorId, target);

        PseudoDead[targetId] = new AkazukinState
        {
            KillerId = killer.PlayerId,
            DeathReason = reason,
            OriginalPos = killPos,
            OriginalSpeed = Main.AllPlayerSpeed[targetId],
            PseudoDeathStart = DateTime.Now
        };

        Main.AllPlayerSpeed[targetId] = 0.5f;
        target.TP(Pelican.GetBlackRoomPS());

        target.SetRealKiller(killer);
        Main.PlayerStates[targetId].deathReason = reason;
        target.MarkDirtySettings();

        SendRPC(targetId);
        Utils.NotifyRoles();

        Logger.Info($"{target.GetRealName()} 擬似死亡突入 (killer: {killer.GetRealName()})", "Akazukin");
    }

    public static void Revive(byte targetId)
    {
        if (!PseudoDead.TryGetValue(targetId, out var state)) return;

        var target = Utils.GetPlayerById(targetId);
        PseudoDead.Remove(targetId);
        SendRPC(targetId);

        if (target == null) return;

        Main.AllPlayerSpeed[targetId] = state.OriginalSpeed;

        Vector2 dest = (ReviveAtOriginalPosition?.GetBool() ?? true) ? state.OriginalPos : Pelican.GetBlackRoomPS();
        target.TP(dest);
        target.MarkDirtySettings();

        Utils.NotifyRoles();
        Logger.Info($"{target.GetRealName()} 擬似死亡から復活", "Akazukin");
    }

    public static void RealDeath(byte targetId)
    {
        if (!PseudoDead.TryGetValue(targetId, out var state)) return;

        var target = Utils.GetPlayerById(targetId);
        PseudoDead.Remove(targetId);
        SendRPC(targetId);

        if (target == null) return;

        Main.PlayerStates[targetId].deathReason = state.DeathReason;
        Main.PlayerStates[targetId].SetDead();
        Utils.AfterPlayerDeathTasks(target, false);

        Logger.Info($"{target.GetRealName()} 擬似死亡から本死亡", "Akazukin");
    }

    public static void OnAnyMurder(PlayerControl killer, PlayerControl victim)
    {
        if (victim == null || PseudoDead.Count == 0) return;
        foreach (var kv in PseudoDead.ToArray())
        {
            if (kv.Value.KillerId == victim.PlayerId)
            {
                Revive(kv.Key);
            }
        }
    }

    public static void OnAnyExile(byte exiledId)
    {
        if (PseudoDead.Count == 0) return;
        foreach (var kv in PseudoDead.ToArray())
        {
            if (kv.Value.KillerId != exiledId) continue;
            if (CanReviveAfterMeeting == null || !CanReviveAfterMeeting.GetBool()) continue;

            byte targetId = kv.Key;
            LateTask.New(() => Revive(targetId), 1.5f, "Akazukin Post-Exile Revive");
        }
    }

    public static void OnAnyDisconnect(byte leftId)
    {
        if (PseudoDead.Count == 0) return;
        foreach (var kv in PseudoDead.ToArray())
        {
            if (kv.Value.KillerId == leftId)
            {
                Revive(kv.Key);
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

        float grace = RevivalGracePeriod.GetFloat();
        if (grace > 0 && (DateTime.Now - state.PseudoDeathStart).TotalSeconds >= grace)
        {
            RealDeath(pc.PlayerId);
            return;
        }

        Vector2 blackRoom = Pelican.GetBlackRoomPS();
        if (!FastVector2.DistanceWithinRange(pc.Pos(), blackRoom, 2f))
        {
            pc.TP(blackRoom, log: false);
        }
    }

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
        return voter != null && PseudoDead.ContainsKey(voter.PlayerId);
    }

    public override bool OnVanish(PlayerControl pc)
    {
        return !PseudoDead.ContainsKey(pc.PlayerId);
    }

    public override bool OnShapeshift(PlayerControl shapeshifter, PlayerControl target, bool shapeshifting)
    {
        return !PseudoDead.ContainsKey(shapeshifter.PlayerId);
    }

    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud = false, bool meeting = false)
    {
        if (target == null || !PseudoDead.TryGetValue(target.PlayerId, out var state)) return string.Empty;
        if (DisplayDeathReasonInName == null || !DisplayDeathReasonInName.GetBool()) return string.Empty;
        return Utils.ColorString(Color.gray, $"\n[{Translator.GetString($"DeathReason.{state.DeathReason}")}]");
    }

    public override bool KnowRole(PlayerControl seer, PlayerControl target)
    {
        if (target != null && PseudoDead.ContainsKey(target.PlayerId)) return false;
        return base.KnowRole(seer, target);
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
