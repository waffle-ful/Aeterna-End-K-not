using System;
using System.Collections.Generic;
using System.Linq;
using AmongUs.GameOptions;
using EndKnot.Modules;
using EndKnot.Modules.Extensions;
using UnityEngine;
using static EndKnot.Translator;

namespace EndKnot.Roles;

public class Vampire : RoleBase
{
    private const int Id = 4500;
    private static readonly List<byte> PlayerIdList = [];

    private static OptionItem Cooldown;
    private static OptionItem OptionKillDelay;
    public static OptionItem OptionCanKillNormally;
    private static OptionItem OptionSpeedDown;
    private static OptionItem OptionSpeedDownStartTime;

    private readonly Dictionary<byte, float> BittenPlayers = [];
    private readonly Dictionary<byte, float> OriginalSpeeds = [];

    // 演出を出して倒れるのを待っている人。待つ間に会議が始まったら OnReportDeadBody がその場で死なせる
    private readonly HashSet<byte> PendingKills = [];

    private bool CanKillNormally;
    private bool CanVent;
    private bool IsPoisoner;
    private float KillCooldown;
    private float KillDelay;
    private bool SpeedDown;
    private float SpeedDownStartTime;

    private byte VampireId;

    public override bool IsEnable => PlayerIdList.Count > 0;

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.ImpostorRoles, CustomRoles.Vampire);

        Cooldown = new FloatOptionItem(Id + 9, "VampireKillCooldown", new(1f, 120f, 1f), 30f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Vampire])
            .SetValueFormat(OptionFormat.Seconds);

        OptionKillDelay = new FloatOptionItem(Id + 10, "VampireKillDelay", new(1f, 120f, 1f), 3f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Vampire])
            .SetValueFormat(OptionFormat.Seconds);

        OptionCanKillNormally = new BooleanOptionItem(Id + 11, "CanKillNormally", true, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Vampire]);

        OptionSpeedDown = new BooleanOptionItem(Id + 12, "VampireSpeedDown", true, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Vampire]);

        OptionSpeedDownStartTime = new FloatOptionItem(Id + 13, "VampireSpeedDownStartTime", new(0f, 120f, 0.5f), 1f, TabGroup.ImpostorRoles)
            .SetParent(OptionSpeedDown)
            .SetValueFormat(OptionFormat.Seconds);
    }

    public override void Init()
    {
        PlayerIdList.Clear();
    }

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);

        VampireId = playerId;
        IsPoisoner = Main.PlayerStates[playerId].MainRole == CustomRoles.Poisoner;

        if (!IsPoisoner)
        {
            KillCooldown = Cooldown.GetFloat();
            KillDelay = OptionKillDelay.GetFloat();
            CanVent = true;
            CanKillNormally = OptionCanKillNormally.GetBool();
            SpeedDown = OptionSpeedDown.GetBool();
            SpeedDownStartTime = OptionSpeedDownStartTime.GetFloat();
        }
        else
        {
            KillCooldown = Poisoner.KillCooldown.GetFloat();
            KillDelay = Poisoner.OptionKillDelay.GetFloat();
            CanVent = Poisoner.CanVent.GetBool();
            CanKillNormally = Poisoner.CanKillNormally.GetBool();
            SpeedDown = false;
            SpeedDownStartTime = 0f;
        }

        BittenPlayers.Clear();
        OriginalSpeeds.Clear();
    }

    public override void Remove(byte playerId)
    {
        PlayerIdList.Remove(playerId);
    }

    public override void ApplyGameOptions(IGameOptions opt, byte playerId)
    {
        if (IsPoisoner) opt.SetVision(Poisoner.ImpostorVision.GetBool());
    }

    public override void SetKillCooldown(byte id)
    {
        Main.AllPlayerKillCooldown[id] = KillCooldown;
    }

    public override bool CanUseImpostorVentButton(PlayerControl pc)
    {
        return CanVent;
    }

    public override bool OnCheckMurder(PlayerControl killer, PlayerControl target)
    {
        if (target.Is(CustomRoles.Bait)) return true;
        if (target.Is(CustomRoles.Pestilence)) return true;
        if (target.Is(CustomRoles.Guardian) && target.AllTasksCompleted()) return true;
        if (target.Is(CustomRoles.Opportunist) && target.AllTasksCompleted() && Opportunist.OppoImmuneToAttacksWhenTasksDone.GetBool()) return true;
        if (target.Is(CustomRoles.Veteran) && Veteran.VeteranInProtect.Contains(target.PlayerId)) return true;
        if (Medic.ProtectList.Contains(target.PlayerId)) return true;

        if (CanKillNormally) return killer.CheckDoubleTrigger(target, Bite);

        Bite();
        return false;

        void Bite()
        {
            killer.SetKillCooldown(KillCooldown + KillDelay);
            killer.RPCPlayCustomSound("Bite");

            if (BittenPlayers.ContainsKey(target.PlayerId)) return;

            BittenPlayers[target.PlayerId] = 0f;
            if (Main.AllPlayerSpeed.TryGetValue(target.PlayerId, out float origSpeed))
                OriginalSpeeds[target.PlayerId] = origSpeed;
        }
    }

    public override void OnFixedUpdate(PlayerControl pc)
    {
        if (!AmongUsClient.Instance.AmHost || !GameStates.IsInTask) return;
        if (BittenPlayers.Count == 0) return;

        foreach (var (targetId, elapsed) in BittenPlayers.ToArray())
        {
            PlayerControl target = Utils.GetPlayerById(targetId);
            if (target == null || target.Data.Disconnected || !target.IsAlive())
            {
                RestoreSpeed(targetId);
                BittenPlayers.Remove(targetId);
                continue;
            }

            float newElapsed = elapsed + Time.fixedDeltaTime;

            if (newElapsed >= KillDelay)
            {
                RestoreSpeed(targetId);
                KillBitten(pc, target);
                BittenPlayers.Remove(targetId);
                continue;
            }

            BittenPlayers[targetId] = newElapsed;

            if (!SpeedDown || IsPoisoner) continue;
            if (newElapsed < SpeedDownStartTime) continue;
            if (!OriginalSpeeds.TryGetValue(targetId, out float origSpeed)) continue;

            float rampWindow = KillDelay - SpeedDownStartTime;
            if (rampWindow <= 0f) continue;

            float sp = origSpeed * ((KillDelay - newElapsed) / rampWindow);
            if (KillDelay - newElapsed <= 0.5f) sp = Main.MinSpeed;

            if (sp >= Main.MinSpeed && sp < origSpeed)
            {
                Main.AllPlayerSpeed[targetId] = sp;
                target.MarkDirtySettings();
            }
        }
    }

    private void RestoreSpeed(byte targetId)
    {
        if (!OriginalSpeeds.TryGetValue(targetId, out float origSpeed))
        {
            OriginalSpeeds.Remove(targetId);
            return;
        }

        Main.AllPlayerSpeed[targetId] = origSpeed;
        Utils.GetPlayerById(targetId)?.MarkDirtySettings();
        OriginalSpeeds.Remove(targetId);
    }

    private void KillBitten(PlayerControl vampire, PlayerControl target, bool meeting = false, bool fxShown = false)
    {
        if (vampire == null || target == null || target.Data.Disconnected) return;

        // 演出が始まってから倒れる (同時だと倒れた後に演出が出て見える)。
        // 待つ間に会議が始まると Suicide は会議中なので効かない → PendingKills に残しておき、OnReportDeadBody がその場で死なせる
        if (!meeting && !fxShown && target.IsAlive())
        {
            if (IsPoisoner) ExplosionFx.Play(ExplosionFx.Kind.Poison, target.Pos(), 1f);
            else ExplosionFx.Play(ExplosionFx.Kind.Drain, target.Pos(), target.PlayerId + 1f);
            PendingKills.Add(target.PlayerId);

            LateTask.New(() =>
            {
                if (!PendingKills.Remove(target.PlayerId)) return;
                if (GameStates.IsEnded || target == null || target.Data.Disconnected || !target.IsAlive()) return;
                KillBitten(vampire, target, meeting: GameStates.IsMeeting, fxShown: true);
            }, 0.45f, "Vampire Delayed Bite Kill");
            return;
        }

        if (target.IsAlive())
        {
            target.Suicide(IsPoisoner ? PlayerState.DeathReason.Poison : PlayerState.DeathReason.Bite, vampire);
            RPC.PlaySoundRPC(vampire.PlayerId, Sounds.KillSound);

            if (!meeting && vampire.IsAlive())
            {
                if (target.Is(CustomRoles.Beartrap)) vampire.BeartrapKilled(target);
                vampire.Notify(GetString("VampireTargetDead"));
            }
        }
    }

    public override void OnReportDeadBody()
    {
        try
        {
            foreach (byte targetId in PendingKills.ToArray())
            {
                try { KillBitten(Utils.GetPlayerById(VampireId), Utils.GetPlayerById(targetId), meeting: true, fxShown: true); }
                catch (Exception e) { Utils.ThrowException(e); }
            }

            PendingKills.Clear();

            foreach (byte targetId in BittenPlayers.Keys.ToArray())
            {
                try
                {
                    PlayerControl target = Utils.GetPlayerById(targetId);
                    PlayerControl vampire = Utils.GetPlayerById(VampireId);
                    RestoreSpeed(targetId);
                    KillBitten(vampire, target, meeting: true);
                }
                catch (Exception e) { Utils.ThrowException(e); }
            }
        }
        catch (Exception e) { Utils.ThrowException(e); }

        BittenPlayers.Clear();
        OriginalSpeeds.Clear();
    }

    public override void SetButtonTexts(HudManager hud, byte id)
    {
        hud.KillButton.OverrideText(GetString(IsPoisoner ? "PoisonerKillButtonText" : "VampireBiteButtonText"));
    }
}
