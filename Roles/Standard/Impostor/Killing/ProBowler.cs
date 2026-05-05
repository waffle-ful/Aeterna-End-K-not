using System.Collections.Generic;
using AmongUs.GameOptions;
using UnityEngine;

namespace EndKnot.Roles;

public class ProBowler : RoleBase
{
    private const int Id = 700200;
    private static List<byte> PlayerIdList = [];

    private static OptionItem KillCooldownOpt;
    private static OptionItem AbilityCooldown;
    private static OptionItem MaxUseCount;
    private static OptionItem BowlingOpt;
    private static OptionItem DeathReasonIsFall;

    private byte ProBowlerId;
    private int NowUseCount;
    private bool NowKilling;
    private Vector2? Bowl;
    private PlayerControl Bowltarget;
    private Vector2 BowlTp;
    private Vector2 TargetPos;
    private int Rollscount;
    private float RollTimer;

    public override bool IsEnable => PlayerIdList.Count > 0;

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.ImpostorRoles, CustomRoles.ProBowler);

        KillCooldownOpt = new FloatOptionItem(Id + 10, "KillCooldown", new(0f, 180f, 0.5f), 20f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.ProBowler])
            .SetValueFormat(OptionFormat.Seconds);

        AbilityCooldown = new FloatOptionItem(Id + 11, "AbilityCooldown", new(0f, 180f, 0.5f), 25f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.ProBowler])
            .SetValueFormat(OptionFormat.Seconds);

        MaxUseCount = new IntegerOptionItem(Id + 12, "ProBowlerMaxUseCount", new(1, 99, 1), 4, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.ProBowler])
            .SetValueFormat(OptionFormat.Times);

        BowlingOpt = new BooleanOptionItem(Id + 13, "ProBowlerBowling", true, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.ProBowler]);

        DeathReasonIsFall = new BooleanOptionItem(Id + 14, "ProBowlerDeathReasonIsFall", false, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.ProBowler]);
    }

    public override void Init()
    {
        PlayerIdList = [];
    }

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);
        ProBowlerId = playerId;
        NowUseCount = 0;
        NowKilling = false;
        Bowl = null;
        Bowltarget = null;
        Rollscount = 0;
        RollTimer = 0f;
    }

    public override void Remove(byte playerId)
    {
        PlayerIdList.Remove(playerId);
    }

    public override void SetKillCooldown(byte id)
    {
        Main.AllPlayerKillCooldown[id] = KillCooldownOpt.GetFloat();
    }

    public override void ApplyGameOptions(IGameOptions opt, byte playerId)
    {
        AURoleOptions.ShapeshifterCooldown = NowUseCount >= MaxUseCount.GetInt() ? 200f : AbilityCooldown.GetFloat();
        AURoleOptions.ShapeshifterDuration = 1f;
        AURoleOptions.ShapeshifterLeaveSkin = false;
    }

    public override void SetButtonTexts(HudManager hud, byte id)
    {
        hud.AbilityButton?.OverrideText(Translator.GetString("ProBowlerAbility"));
    }

    public override bool OnShapeshift(PlayerControl shapeshifter, PlayerControl target, bool shapeshifting)
    {
        if (!shapeshifting) return false;
        if (Bowl != null || NowUseCount >= MaxUseCount.GetInt()) return false;

        NowUseCount++;
        Bowl = shapeshifter.Pos();
        LateTask.New(() =>
        {
            shapeshifter.SyncSettings();
            Utils.NotifyRoles(SpecifySeer: shapeshifter, SpecifyTarget: shapeshifter);
        }, 0.2f, "ProBowler.BowlSet");
        return false;
    }

    public override bool OnCheckMurder(PlayerControl killer, PlayerControl target)
    {
        if (NowKilling) return false;
        if (Bowl == null) return true;

        NowKilling = true;
        killer.SetKillCooldown(KillCooldownOpt.GetFloat());
        Bowltarget = target;
        Rollscount = 0;
        RollTimer = 0f;

        if (BowlingOpt.GetBool())
        {
            Vector2 targetPos = target.Pos();
            BowlTp = new Vector2((Bowl.Value.x - targetPos.x) * 0.1f, (Bowl.Value.y - targetPos.y) * 0.1f);
            TargetPos = targetPos;
            Bowl = null;
        }
        else
        {
            Vector2 bowlPos = Bowl.Value;
            Bowl = null;
            target.TP(bowlPos, log: false);

            PlayerState.DeathReason reason = DeathReasonIsFall.GetBool()
                ? PlayerState.DeathReason.Fall
                : PlayerState.DeathReason.Kill;

            LateTask.New(() =>
            {
                NowKilling = false;
                if (!target.IsAlive()) return;
                target.SetRealKiller(killer);
                target.Suicide(reason, killer);
            }, 0.3f, "ProBowler.Kill");
        }

        return false;
    }

    public override void OnFixedUpdate(PlayerControl pc)
    {
        if (!AmongUsClient.Instance.AmHost) return;
        if (!BowlingOpt.GetBool() || !NowKilling || Bowltarget == null) return;
        if (!GameStates.IsInTask || ExileController.Instance) return;

        RollTimer += Time.fixedDeltaTime;
        if (RollTimer < 0.1f) return;

        RollTimer = 0f;
        Rollscount++;
        Bowltarget.TP(new Vector2(TargetPos.x + BowlTp.x * Rollscount, TargetPos.y + BowlTp.y * Rollscount), log: false);

        if (Rollscount < 10) return;

        NowKilling = false;
        if (!Bowltarget.IsAlive()) return;

        PlayerState.DeathReason reason = DeathReasonIsFall.GetBool()
            ? PlayerState.DeathReason.Fall
            : PlayerState.DeathReason.Kill;

        Bowltarget.SetRealKiller(pc);
        Bowltarget.Suicide(reason, pc);
        Bowltarget = null;
    }

    public override void OnReportDeadBody()
    {
        RollTimer = 0f;
        Rollscount = 0;

        if (NowKilling && Bowltarget != null && Bowltarget.IsAlive())
        {
            PlayerState.DeathReason reason = DeathReasonIsFall.GetBool()
                ? PlayerState.DeathReason.Fall
                : PlayerState.DeathReason.Kill;

            Bowltarget.SetRealKiller(Utils.GetPlayerById(ProBowlerId));
            Bowltarget.Suicide(reason, Utils.GetPlayerById(ProBowlerId));
        }

        NowKilling = false;
        Bowl = null;
        Bowltarget = null;
    }

    public override string GetProgressText(byte playerId, bool comms)
    {
        if (playerId != ProBowlerId) return string.Empty;
        int remaining = MaxUseCount.GetInt() - NowUseCount;
        Color color = remaining > 0 ? Palette.ImpostorRed : Color.gray;
        return Utils.ColorString(color, $"({remaining})");
    }

    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud, bool meeting)
    {
        if (meeting || hud || seer.PlayerId != target.PlayerId || seer.PlayerId != ProBowlerId) return string.Empty;
        if (!seer.IsAlive() || NowUseCount >= MaxUseCount.GetInt()) return string.Empty;
        return Bowl == null
            ? Translator.GetString("ProBowlerInfoTextSet")
            : Translator.GetString("ProBowlerInfoTextKill");
    }
}
