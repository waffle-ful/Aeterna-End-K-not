using System.Collections.Generic;
using System.Linq;
using AmongUs.GameOptions;
using EndKnot.Patches;
using UnityEngine;

namespace EndKnot.Roles;

public class Archer : RoleBase
{
    private const int Id = 701000;
    private static List<byte> PlayerIdList = [];

    public static OptionItem AbilityCooldown;
    private static OptionItem ArrowCount;
    private static OptionItem LostArrowTimer;
    private static OptionItem ArrowSpeed;
    private static OptionItem MyArrow;
    private static OptionItem CanNormalKill;
    private static OptionItem FriendlyFire;

    private byte ArcherId;
    private float PlayerSpeed;

    private Vector2 ArrowPosition;
    private Vector2 ArrowLastPos;
    private Vector2 PlayerPosition;
    private bool IsUsing;
    public bool IsSetting;
    private float Timer;
    private float TeleportTimer;
    private int? ArrowsLeft;
    private long FireStartTS;

    public override bool IsEnable => PlayerIdList.Count > 0;

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.ImpostorRoles, CustomRoles.Archer);

        AbilityCooldown = new FloatOptionItem(Id + 10, "AbilityCooldown", new(0f, 180f, 0.5f), 35f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Archer])
            .SetValueFormat(OptionFormat.Seconds);

        LostArrowTimer = new FloatOptionItem(Id + 11, "ArcherLostArrowTimer", new(0.5f, 10f, 0.1f), 3f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Archer])
            .SetValueFormat(OptionFormat.Seconds);

        ArrowCount = new IntegerOptionItem(Id + 12, "ArcherArrowCount", new(0, 99, 1), 3, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Archer])
            .SetValueFormat(OptionFormat.Times);

        ArrowSpeed = new FloatOptionItem(Id + 13, "ArcherArrowSpeed", new(0.5f, 10f, 0.25f), 1f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Archer]);

        MyArrow = new BooleanOptionItem(Id + 14, "ArcherMyArrow", false, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Archer]);

        CanNormalKill = new BooleanOptionItem(Id + 15, "ArcherCanNormalKill", false, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Archer]);

        FriendlyFire = new BooleanOptionItem(Id + 16, "ArcherFriendlyFire", true, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Archer]);
    }

    public override void Init()
    {
        PlayerIdList = [];
    }

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);
        ArcherId = playerId;
        PlayerSpeed = Main.AllPlayerSpeed.TryGetValue(playerId, out float spd) ? spd : Main.MinSpeed;
        int count = ArrowCount.GetInt();
        ArrowsLeft = count == 0 ? null : count;
        Reset();
    }

    public override void Remove(byte playerId)
    {
        PlayerIdList.Remove(playerId);
    }

    public override bool CanUseKillButton(PlayerControl pc)
    {
        return base.CanUseKillButton(pc) && (CanNormalKill.GetBool() || ArrowsLeft is 0);
    }

    public override void ApplyGameOptions(IGameOptions opt, byte playerId)
    {
        float cd = ArrowsLeft is 0 ? 200f : AbilityCooldown.GetFloat();
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
        string text = Translator.GetString("ArcherAbilityButton");
        if (Options.UsePets.GetBool() && !Options.UsePhantomBasis.GetBool())
            hud.PetButton?.OverrideText(text);
        else
            hud.AbilityButton?.OverrideText(text);
    }

    public override void OnPet(PlayerControl pc) => OnAbility(pc);

    public override bool OnShapeshift(PlayerControl shapeshifter, PlayerControl target, bool shapeshifting)
    {
        if (!shapeshifting) return true;
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
        if (!pc.IsAlive()) return;
        if (ArrowsLeft is 0) return;

        if (IsSetting)
        {
            IsSetting = false;
            FireArrow(pc);
            return;
        }

        if (IsUsing) return;

        IsSetting = true;
        Timer = 0;
        PlayerPosition = pc.Pos();
        if (ArrowsLeft.HasValue)
        {
            ArrowsLeft--;
        }

        Utils.NotifyRoles(SpecifySeer: pc);
    }

    private void FireArrow(PlayerControl pc)
    {
        var delta = pc.Pos() - PlayerPosition;
        var dir = delta.normalized;

        if (dir.sqrMagnitude < 0.5f)
        {
            IsSetting = false;
            if (ArrowsLeft.HasValue) ArrowsLeft++;
            if (MyArrow.GetBool())
            {
                Main.AllPlayerSpeed[pc.PlayerId] = PlayerSpeed;
                pc.SyncSettings();
            }
            pc.Notify(Utils.ColorString(Palette.ImpostorRed, Translator.GetString("ArcherNeedsMovement")));
            Utils.NotifyRoles(SpecifySeer: pc);
            return;
        }

        ArrowPosition = dir;

        while (ArrowPosition.x + ArrowPosition.y > 0.4f || ArrowPosition.x + ArrowPosition.y < -0.4f
            || ArrowPosition.x > 0.15f || ArrowPosition.x < -0.15f
            || ArrowPosition.y > 0.15f || ArrowPosition.y < -0.15f)
        {
            ArrowPosition *= 0.9f;
        }

        ArrowPosition *= -1;
        ArrowLastPos = PlayerPosition + new Vector2(0, 0.3f);
        IsUsing = true;
        Timer = 0;
        TeleportTimer = 0;
        FireStartTS = Utils.TimeStamp;

        if (MyArrow.GetBool())
        {
            Main.AllPlayerSpeed[pc.PlayerId] = Main.MinSpeed;
            pc.MarkDirtySettings();
        }

        Logger.Info($"Fire: IsUsing=true, FireStartTS={FireStartTS}, LostArrowTimer={LostArrowTimer.GetFloat()}", "Archer");
        Utils.NotifyRoles(SpecifySeer: pc);
        pc.RpcResetAbilityCooldown();

        long firedAt = FireStartTS;
        byte targetId = pc.PlayerId;
        float safetyDelay = LostArrowTimer.GetFloat() + 1.5f;
        LateTask.New(() =>
        {
            if (IsUsing && FireStartTS == firedAt)
            {
                Logger.Warn($"Archer flight safety LateTask fired at {Utils.TimeStamp} (fired at {firedAt}, elapsed {Utils.TimeStamp - firedAt}s). Force reset.", "Archer");
                var target = Utils.GetPlayerById(targetId);
                if (target != null) ResetAfterFlight(target);
            }
        }, safetyDelay, "Archer flight safety");
    }

    public override void OnFixedUpdate(PlayerControl pc)
    {
        if (!AmongUsClient.Instance.AmHost || !pc.IsAlive()) return;

        if (IsUsing && (Utils.TimeStamp - FireStartTS) > (long)Mathf.Ceil(LostArrowTimer.GetFloat()) + 1)
        {
            Logger.Warn($"OnFixedUpdate wall-clock safety triggered (elapsed {Utils.TimeStamp - FireStartTS}s)", "Archer");
            ResetAfterFlight(pc);
            Utils.NotifyRoles(SpecifySeer: pc);
            return;
        }

        if (IsSetting)
        {
            Timer += Time.fixedDeltaTime;
            if (Timer > 5f)
            {
                IsSetting = false;
                Timer = 0;
                FireArrow(pc);
            }
            return;
        }

        if (!IsUsing) return;

        Timer += Time.fixedDeltaTime;
        TeleportTimer += Time.fixedDeltaTime;

        int speedSteps = (int)(ArrowSpeed.GetFloat() / 0.25f) + 1;

        if (Timer <= LostArrowTimer.GetFloat())
        {
            for (int i = 0; i <= speedSteps; i++)
            {
                if (!AdvanceArrow(pc, i)) break;
            }
        }
        else
        {
            if (MyArrow.GetBool() && IsPathClear(true))
            {
                pc.TP(ArrowLastPos + new Vector2(0, 0.1f), log: false);
            }
            ResetAfterFlight(pc);
            Utils.NotifyRoles(SpecifySeer: pc);
        }
    }

    private bool IsPathClear(bool forTeleport = false)
    {
        var nextpos = ArrowLastPos + ArrowPosition;
        var last = PlayerPosition;
        var vector = nextpos - last;
        float dis = vector.magnitude;
        if (forTeleport) dis = Mathf.Clamp(dis + 2f, 0.01f, 99f);
        return !PhysicsHelpers.AnyNonTriggersBetween(last, vector.normalized, dis, Constants.ShipAndObjectsMask);
    }

    private bool AdvanceArrow(PlayerControl pc, int step)
    {
        Vector2 prevPos = ArrowLastPos;
        ArrowLastPos += ArrowPosition * 0.25f;

        if (!IsPathClear())
        {
            if (MyArrow.GetBool() && Timer > 0.15f)
            {
                pc.TP(prevPos + new Vector2(0, 0.1f), log: false);
            }
            ResetAfterFlight(pc);
            Utils.NotifyRoles(SpecifySeer: pc);
            return false;
        }

        if (MyArrow.GetBool() && TeleportTimer > 0.1f && IsPathClear(true))
        {
            pc.TP(ArrowLastPos + new Vector2(0, 0.1f), log: false);
            TeleportTimer = 0;
        }

        var distances = new Dictionary<byte, float>();
        foreach (PlayerControl target in Main.AllAlivePlayerControlsToList)
        {
            if (target.PlayerId == pc.PlayerId) continue;
            if (!FriendlyFire.GetBool() && target.GetCustomRole().IsImpostor()) continue;

            float dist = Vector2.Distance(ArrowLastPos, target.transform.position);
            if (dist <= 0.6f)
                distances[target.PlayerId] = dist;
        }

        if (distances.Count == 0) return true;

        byte nearestId = distances.OrderBy(x => x.Value).First().Key;
        PlayerControl nearest = Utils.GetPlayerById(nearestId);

        if (nearest != null && nearest.IsAlive())
        {
            nearest.SetRealKiller(pc);
            nearest.Suicide(PlayerState.DeathReason.Sniped, pc);
            pc.RpcResetAbilityCooldown();
        }

        if (MyArrow.GetBool() && IsPathClear(true))
            pc.TP(ArrowLastPos + new Vector2(0, 0.1f), log: false);

        ResetAfterFlight(pc);
        Utils.NotifyRoles(SpecifySeer: pc);
        return false;
    }

    private void ResetAfterFlight(PlayerControl pc)
    {
        Logger.Info($"ResetAfterFlight (Timer={Timer:F2}s, wall-clock elapsed={(IsUsing ? Utils.TimeStamp - FireStartTS : 0)}s)", "Archer");
        ArrowPosition = Vector2.zero;
        PlayerPosition = Vector2.zero;
        IsUsing = false;
        IsSetting = false;
        Timer = 0;
        TeleportTimer = 0;

        Main.AllPlayerSpeed[pc.PlayerId] = PlayerSpeed;
        pc.SyncSettings();
    }

    private void Reset()
    {
        ArrowPosition = Vector2.zero;
        ArrowLastPos = Vector2.zero;
        PlayerPosition = Vector2.zero;
        IsUsing = false;
        IsSetting = false;
        Timer = 0;
        TeleportTimer = 0;
    }

    public override void OnReportDeadBody()
    {
        var pc = Utils.GetPlayerById(ArcherId);
        if (pc != null)
        {
            Main.AllPlayerSpeed[ArcherId] = PlayerSpeed;
            pc.SyncSettings();
        }

        Reset();
    }

    public override string GetProgressText(byte playerId, bool comms)
    {
        if (playerId != ArcherId) return string.Empty;
        if (ArrowsLeft is null) return string.Empty;
        Color color = ArrowsLeft > 0 ? Color.white : Color.red;
        return Utils.ColorString(color, $"({ArrowsLeft.Value})");
    }

    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud = false, bool meeting = false)
    {
        if (seer.PlayerId != ArcherId || seer.PlayerId != target.PlayerId) return string.Empty;
        if (!seer.IsAlive() || meeting || ArrowsLeft is 0) return string.Empty;

        if (IsUsing) return Utils.ColorString(Palette.ImpostorRed, Translator.GetString("ArcherLower_ArrowActive"));
        if (IsSetting) return Utils.ColorString(Palette.ImpostorRed, Translator.GetString("ArcherLower_SetBow"));
        return Utils.ColorString(Palette.ImpostorRed, Translator.GetString("ArcherLower_Ready"));
    }
}
