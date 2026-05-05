using System.Collections.Generic;
using System.Linq;
using AmongUs.GameOptions;
using EndKnot.Patches;
using UnityEngine;

namespace EndKnot.Roles;

public class AntiReporter : RoleBase
{
    private const int Id = 700600;
    private static List<byte> PlayerIdList = [];
    private static Dictionary<byte, float> ReportCrashTimers = [];

    public static OptionItem AbilityCooldown;
    private static OptionItem MaxUseCount;
    private static OptionItem ResetMeeting;
    private static OptionItem ResetSeconds;

    private byte AntiReporterId;
    private int UseCount;

    public override bool IsEnable => PlayerIdList.Count > 0;

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.ImpostorRoles, CustomRoles.AntiReporter);

        AbilityCooldown = new FloatOptionItem(Id + 10, "AbilityCooldown", new(1f, 120f, 1f), 20f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.AntiReporter])
            .SetValueFormat(OptionFormat.Seconds);

        MaxUseCount = new IntegerOptionItem(Id + 11, "AntiReporterMaximum", new(1, 99, 1), 3, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.AntiReporter])
            .SetValueFormat(OptionFormat.Times);

        ResetMeeting = new BooleanOptionItem(Id + 12, "AntiReporterResetMeeting", true, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.AntiReporter]);

        ResetSeconds = new IntegerOptionItem(Id + 13, "AntiReporterResetse", new(0, 999, 1), 20, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.AntiReporter])
            .SetValueFormat(OptionFormat.Seconds);
    }

    public override void Init()
    {
        PlayerIdList = [];
        ReportCrashTimers = [];
    }

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);
        AntiReporterId = playerId;
        UseCount = MaxUseCount.GetInt();
    }

    public override void Remove(byte playerId)
    {
        PlayerIdList.Remove(playerId);
    }

    public override void ApplyGameOptions(IGameOptions opt, byte playerId)
    {
        float cd = UseCount > 0 ? AbilityCooldown.GetFloat() : 200f;
        if (IntroCutsceneDestroyPatch.PreventKill) cd = Mathf.Max(cd, 12f);

        if (Options.UsePhantomBasis.GetBool())
            AURoleOptions.PhantomCooldown = cd;
        else if (!Options.UsePets.GetBool())
        {
            AURoleOptions.ShapeshifterCooldown = cd;
            AURoleOptions.ShapeshifterDuration = 1f;
        }
    }

    public override void SetButtonTexts(HudManager hud, byte id)
    {
        string text = Translator.GetString("AntiReporter_Ability");
        if (Options.UsePets.GetBool() && !Options.UsePhantomBasis.GetBool())
            hud.PetButton?.OverrideText(text);
        else
            hud.AbilityButton?.OverrideText(text);
    }

    public override void OnPet(PlayerControl pc) => OnAbility(pc);

    public override bool OnShapeshift(PlayerControl shapeshifter, PlayerControl target, bool shapeshifting)
    {
        if (!shapeshifting) return true;
        shapeshifter.RpcRejectShapeshift();
        OnAbility(shapeshifter);
        return false;
    }

    public override bool OnVanish(PlayerControl pc)
    {
        OnAbility(pc);
        return false;
    }

    private void OnAbility(PlayerControl pc)
    {
        if (UseCount <= 0) return;

        PlayerControl nearestTarget = null;
        float minDist = float.MaxValue;

        foreach (PlayerControl p in Main.AllAlivePlayerControls)
        {
            if (p.PlayerId == pc.PlayerId) continue;
            if (ReportCrashTimers.ContainsKey(p.PlayerId)) continue;
            float d = Vector2.Distance(pc.Pos(), p.Pos());
            if (d >= minDist) continue;
            minDist = d;
            nearestTarget = p;
        }

        if (nearestTarget == null) return;

        ReportCrashTimers[nearestTarget.PlayerId] = 0f;
        ReportDeadBodyPatch.CanReport[nearestTarget.PlayerId] = false;
        UseCount--;
        pc.SyncSettings();
        pc.RpcResetAbilityCooldown();
        Utils.NotifyRoles(SpecifySeer: pc);
    }

    public override void OnFixedUpdate(PlayerControl pc)
    {
        if (!AmongUsClient.Instance.AmHost || GameStates.IsLobby) return;
        if (ReportCrashTimers.Count == 0) return;

        int resetSeconds = ResetSeconds.GetInt();
        if (resetSeconds == 0) return;

        foreach ((byte targetId, float timer) in ReportCrashTimers.ToArray())
        {
            if (timer >= resetSeconds)
            {
                ReportCrashTimers.Remove(targetId);
                ReportDeadBodyPatch.CanReport[targetId] = true;
            }
            else
            {
                ReportCrashTimers[targetId] = timer + Time.fixedDeltaTime;
            }
        }
    }

    public override void OnReportDeadBody()
    {
        if (ResetMeeting.GetBool())
        {
            foreach (byte id in ReportCrashTimers.Keys)
                ReportDeadBodyPatch.CanReport[id] = true;
            ReportCrashTimers.Clear();
        }
    }

    public override string GetProgressText(byte playerId, bool comms)
    {
        if (playerId != AntiReporterId) return string.Empty;
        Color color = UseCount > 0 ? Palette.ImpostorRed : Color.gray;
        return Utils.ColorString(color, $"({UseCount})");
    }

    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud, bool meeting)
    {
        if (meeting) return string.Empty;
        if (seer.PlayerId != AntiReporterId) return string.Empty;
        if (seer.PlayerId != target.PlayerId) return string.Empty;
        if (UseCount <= 0) return string.Empty;
        return Utils.ColorString(Palette.ImpostorRed, Translator.GetString("AntiReporterHudHint"));
    }
}
