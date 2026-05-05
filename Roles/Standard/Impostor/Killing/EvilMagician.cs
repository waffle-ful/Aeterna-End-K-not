using System.Collections.Generic;
using System.Linq;
using AmongUs.GameOptions;
using EndKnot.Patches;
using UnityEngine;

namespace EndKnot.Roles;

public class EvilMagician : RoleBase
{
    private const int Id = 700100;
    private static List<byte> PlayerIdList = [];

    private static OptionItem MagicCooldown;
    private static OptionItem Maximum;
    private static OptionItem Radius;
    private static OptionItem ShowDeadbody;
    private static OptionItem MagicUseKillCount;
    private static OptionItem ResetKillCount;
    private static OptionItem ResetMagicTarget;

    private byte EvilMagicianId;
    private float CurrentCooldown;
    private int MagicCount;
    private int HaveKillCount;
    private List<byte> MagicTargets;

    public override bool IsEnable => PlayerIdList.Count > 0;

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.ImpostorRoles, CustomRoles.EvilMagician);

        MagicCooldown = new FloatOptionItem(Id + 10, "EvilMagicianCooldown", new(0f, 120f, 1f), 15f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.EvilMagician])
            .SetValueFormat(OptionFormat.Seconds);

        Maximum = new IntegerOptionItem(Id + 11, "EvilMagicianMaximum", new(0, 99, 1), 3, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.EvilMagician])
            .SetValueFormat(OptionFormat.Times);

        Radius = new FloatOptionItem(Id + 12, "EvilMagicianRadius", new(0.5f, 3f, 0.5f), 1.5f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.EvilMagician])
            .SetValueFormat(OptionFormat.Multiplier);

        MagicUseKillCount = new IntegerOptionItem(Id + 13, "EvilMagicianMagicUseKillCount", new(0, 99, 1), 1, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.EvilMagician])
            .SetValueFormat(OptionFormat.Players);

        ShowDeadbody = new BooleanOptionItem(Id + 14, "EvilMagicianShowDeadbody", false, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.EvilMagician]);

        ResetKillCount = new BooleanOptionItem(Id + 15, "EvilMagicianResetKillCount", true, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.EvilMagician]);

        ResetMagicTarget = new BooleanOptionItem(Id + 16, "EvilMagicianResetMagicTarget", false, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.EvilMagician]);
    }

    public override void Init()
    {
        PlayerIdList = [];
    }

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);
        EvilMagicianId = playerId;
        CurrentCooldown = MagicCooldown.GetFloat();
        MagicCount = 0;
        HaveKillCount = 0;
        MagicTargets = [];
    }

    public override void Remove(byte playerId)
    {
        PlayerIdList.Remove(playerId);
    }

    public override void ApplyGameOptions(IGameOptions opt, byte playerId)
    {
        int max = Maximum.GetInt();
        float cd = max > 0 && MagicCount >= max ? 200f : CurrentCooldown;

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
        string text = Translator.GetString("EvilMagicianAbility");
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
        int max = Maximum.GetInt();
        if (max > 0 && MagicCount >= max) return;

        float radius = Radius.GetFloat();
        PlayerControl nearestTarget = null;
        float minDist = float.MaxValue;

        foreach (PlayerControl p in Main.AllAlivePlayerControls)
        {
            if (p.PlayerId == pc.PlayerId) continue;
            if (p.GetCustomRole().IsImpostor()) continue;
            if (MagicTargets.Contains(p.PlayerId)) continue;
            float d = Vector2.Distance(pc.Pos(), p.Pos());
            if (d >= radius || d >= minDist) continue;
            minDist = d;
            nearestTarget = p;
        }

        if (nearestTarget == null)
        {
            CurrentCooldown = 0f;
            pc.SyncSettings();
            pc.RpcResetAbilityCooldown();
            return;
        }

        MagicCount++;
        MagicTargets.Add(nearestTarget.PlayerId);

        int maxNow = Maximum.GetInt();
        CurrentCooldown = maxNow > 0 && MagicCount >= maxNow ? 200f : MagicCooldown.GetFloat();

        pc.SyncSettings();
        pc.RpcResetAbilityCooldown();
        LateTask.New(() => Utils.NotifyRoles(), 0.1f, "EvilMagician.Notify");
    }

    public override void OnMurder(PlayerControl killer, PlayerControl target)
    {
        HaveKillCount++;
    }

    public override void OnFixedUpdate(PlayerControl pc)
    {
        if (!AmongUsClient.Instance.AmHost || !GameStates.IsInTask) return;
        if (MagicTargets.Count == 0 || HaveKillCount < MagicUseKillCount.GetInt()) return;

        foreach (byte id in MagicTargets.ToArray())
        {
            PlayerControl target = Utils.GetPlayerById(id);
            if (target == null || !target.IsAlive()) continue;

            if (ShowDeadbody.GetBool())
            {
                target.Suicide(PlayerState.DeathReason.Spell, pc);
            }
            else
            {
                target.SetRealKiller(pc);
                Main.PlayerStates[target.PlayerId].deathReason = PlayerState.DeathReason.Spell;
                target.RpcExileV2();
                target.Data.IsDead = true;
                Main.PlayerStates[target.PlayerId].SetDead();
                Utils.AfterPlayerDeathTasks(target);
            }
        }

        MagicTargets.Clear();
        HaveKillCount -= MagicUseKillCount.GetInt();

        int max = Maximum.GetInt();
        CurrentCooldown = max > 0 && MagicCount >= max ? 200f : MagicCooldown.GetFloat();

        pc.SyncSettings();
        LateTask.New(() => Utils.NotifyRoles(), 0.1f, "EvilMagician.NotifyAfterKill");
    }

    public override void AfterMeetingTasks()
    {
        if (ResetKillCount.GetBool()) HaveKillCount = 0;
        if (ResetMagicTarget.GetBool()) MagicTargets.Clear();

        int max = Maximum.GetInt();
        CurrentCooldown = max > 0 && MagicCount >= max ? 200f : MagicCooldown.GetFloat();
    }

    public override string GetProgressText(byte playerId, bool comms)
    {
        if (playerId != EvilMagicianId) return string.Empty;

        int max = Maximum.GetInt();
        int killNeeded = MagicUseKillCount.GetInt();

        if (max == 0 && killNeeded == 0) return string.Empty;

        string text = "(";
        if (max != 0) text += max - MagicCount;
        if (killNeeded > 0) text += (max > 0 ? " | " : "") + $"{HaveKillCount}/{killNeeded}";

        Color color = HaveKillCount < killNeeded ? Color.yellow
            : max > 0 && MagicCount < max ? Palette.ImpostorRed
            : Color.gray;

        return Utils.ColorString(color, text + ")");
    }
}
