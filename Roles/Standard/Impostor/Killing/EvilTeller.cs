using System.Collections.Generic;
using AmongUs.GameOptions;
using EndKnot.Patches;
using UnityEngine;

namespace EndKnot.Roles;

public class EvilTeller : RoleBase
{
    private const int Id = 699900;
    private static List<byte> PlayerIdList = [];

    private static OptionItem KillCooldown;
    private static OptionItem AbilityCooldown;
    private static OptionItem MaxTellCount;
    private static OptionItem TellTime;
    private static OptionItem TellDistance;
    private static OptionItem UseKillCooldownAfterTell;

    private byte EvilTellerId;
    private Dictionary<byte, CustomRoles> SeenTargets = new();
    private TimerState? CurrentTarget;
    private bool IsObserving;
    private bool ObservationFailed;

    private record struct TimerState(byte TargetId, float Timer);

    public override bool IsEnable => PlayerIdList.Count > 0;

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.ImpostorRoles, CustomRoles.EvilTeller);

        KillCooldown = new FloatOptionItem(Id + 10, "KillCooldown", new(0f, 180f, 0.5f), 30f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.EvilTeller])
            .SetValueFormat(OptionFormat.Seconds);

        MaxTellCount = new IntegerOptionItem(Id + 11, "EvilTellerMaxTellCount", new(1, 99, 1), 3, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.EvilTeller])
            .SetValueFormat(OptionFormat.Times);

        AbilityCooldown = new FloatOptionItem(Id + 12, "EvilTellerAbilityCooldown", new(0f, 180f, 0.5f), 30f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.EvilTeller])
            .SetValueFormat(OptionFormat.Seconds);

        TellTime = new FloatOptionItem(Id + 13, "EvilTellerTellTime", new(0f, 100f, 0.5f), 5f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.EvilTeller])
            .SetValueFormat(OptionFormat.Seconds);

        TellDistance = new FloatOptionItem(Id + 14, "EvilTellerDistance", new(0.5f, 30f, 0.25f), 1.75f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.EvilTeller]);

        UseKillCooldownAfterTell = new BooleanOptionItem(Id + 15, "EvilTellerUseKillCooldownAfterTell", false, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.EvilTeller]);
    }

    public override void Init()
    {
        PlayerIdList = [];
    }

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);
        EvilTellerId = playerId;
        SeenTargets = new();
        CurrentTarget = null;
        IsObserving = false;
        ObservationFailed = false;
    }

    public override void Remove(byte playerId)
    {
        PlayerIdList.Remove(playerId);
    }

    public override void SetKillCooldown(byte id)
    {
        Main.AllPlayerKillCooldown[id] = KillCooldown.GetFloat();
    }

    public override void ApplyGameOptions(IGameOptions opt, byte playerId)
    {
        float cd;
        if (SeenTargets.Count >= MaxTellCount.GetInt())
            cd = 200f;
        else if (IsObserving)
            cd = TellTime.GetFloat();
        else
            cd = ObservationFailed ? 1f : AbilityCooldown.GetFloat();

        if (IntroCutsceneDestroyPatch.PreventKill)
            cd = Mathf.Max(cd, 12f);

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
        string text = Translator.GetString("EvilTellerAbility");
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
        if (SeenTargets.Count >= MaxTellCount.GetInt()) return;
        if (CurrentTarget != null) return;

        float dist = TellDistance.GetFloat();
        PlayerControl nearestTarget = null;
        float minDist = float.MaxValue;

        foreach (PlayerControl p in Main.AllAlivePlayerControlsToList)
        {
            if (p.PlayerId == pc.PlayerId) continue;
            if (p.Is(CustomRoleTypes.Impostor)) continue;
            float d = Vector2.Distance(pc.Pos(), p.Pos());
            if (d > dist || d >= minDist) continue;
            minDist = d;
            nearestTarget = p;
        }

        if (nearestTarget == null) return;

        CurrentTarget = new(nearestTarget.PlayerId, 0f);
        IsObserving = true;
        ObservationFailed = false;
        pc.SyncSettings();
        pc.RpcResetAbilityCooldown();
        Utils.NotifyRoles(SpecifySeer: pc);
    }

    public override void OnFixedUpdate(PlayerControl pc)
    {
        if (!AmongUsClient.Instance.AmHost) return;
        if (!GameStates.IsInTask || CurrentTarget == null || !pc.IsAlive()) return;
        if (SeenTargets.Count >= MaxTellCount.GetInt()) return;

        byte targetId = CurrentTarget.Value.TargetId;
        float timer = CurrentTarget.Value.Timer;
        PlayerControl target = Utils.GetPlayerById(targetId);

        if (target == null || !target.IsAlive())
        {
            IsObserving = false;
            ObservationFailed = true;
            CurrentTarget = null;
            pc.SyncSettings();
            Utils.NotifyRoles(SpecifySeer: pc);
            return;
        }

        if (Vector2.Distance(pc.Pos(), target.Pos()) > TellDistance.GetFloat())
        {
            IsObserving = false;
            ObservationFailed = true;
            CurrentTarget = null;
            pc.SyncSettings();
            Utils.NotifyRoles(SpecifySeer: pc);
            return;
        }

        timer += Time.fixedDeltaTime;

        if (timer >= TellTime.GetFloat())
        {
            IsObserving = false;
            ObservationFailed = false;
            SeenTargets.TryAdd(targetId, target.GetCustomRole());
            CurrentTarget = null;
            pc.SyncSettings();

            if (UseKillCooldownAfterTell.GetBool())
            {
                Main.AllPlayerKillCooldown[pc.PlayerId] = KillCooldown.GetFloat();
                pc.SetKillCooldown();
            }
            else
            {
                pc.RpcResetAbilityCooldown();
            }

            Utils.NotifyRoles(SpecifySeer: pc, ForceLoop: true);
        }
        else
        {
            CurrentTarget = new(targetId, timer);
        }
    }

    public override void OnReportDeadBody()
    {
        IsObserving = false;
        ObservationFailed = false;
        CurrentTarget = null;
    }

    public override bool KnowRole(PlayerControl seer, PlayerControl target)
    {
        if (base.KnowRole(seer, target)) return true;
        if (seer.PlayerId != EvilTellerId) return false;
        return SeenTargets.ContainsKey(target.PlayerId);
    }

    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud = false, bool meeting = false)
    {
        if (meeting) return string.Empty;
        if (seer.PlayerId != EvilTellerId || !seer.IsAlive()) return string.Empty;

        if (!hud && CurrentTarget.HasValue && target.PlayerId == CurrentTarget.Value.TargetId)
            return "<color=#ff1919>◆</color>";

        if (hud && seer.PlayerId == target.PlayerId && IsObserving)
            return Translator.GetString("EvilTellerObservingHint");

        return string.Empty;
    }

    public override string GetProgressText(byte playerId, bool comms)
    {
        if (playerId != EvilTellerId) return string.Empty;
        int remaining = MaxTellCount.GetInt() - SeenTargets.Count;
        Color color = remaining > 0 ? Palette.ImpostorRed : Color.gray;
        return Utils.ColorString(color, $"({remaining})");
    }
}
