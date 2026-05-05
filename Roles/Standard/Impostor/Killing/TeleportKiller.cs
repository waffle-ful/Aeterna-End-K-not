using System.Collections.Generic;
using AmongUs.GameOptions;
using EndKnot.Patches;
using UnityEngine;

namespace EndKnot.Roles;

public class TeleportKiller : RoleBase
{
    private const int Id = 700300;
    private static List<byte> PlayerIdList = [];

    private static OptionItem KillCooldown;
    private static OptionItem AbilityCooldown;
    private static OptionItem Maximum;
    private static OptionItem Duration;
    private static OptionItem TeleportKillerFall;
    private static OptionItem TeleportKillerVentgaaa;
    private static OptionItem TeleportKillerPlatformFall;
    private static OptionItem TeleportKillerLadderFall;
    private static OptionItem TeleportKillerDokkaaaan;
    private static OptionItem TeleportKillerKillCooldownReset;
    private static OptionItem TeleportKillerChangeDeathReason;

    private byte TeleportKillerId;
    private int usecount;
    private List<byte> PendingKillTargets;
    private bool IsAnimation;
    private Vector2 AnimStart;
    private Vector2 AnimGoal;
    private float AnimT;

    public override bool IsEnable => PlayerIdList.Count > 0;

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.ImpostorRoles, CustomRoles.TeleportKiller);

        KillCooldown = new FloatOptionItem(Id + 10, "KillCooldown", new(0f, 180f, 0.5f), 30f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.TeleportKiller])
            .SetValueFormat(OptionFormat.Seconds);

        AbilityCooldown = new FloatOptionItem(Id + 11, "AbilityCooldown", new(0f, 180f, 0.5f), 30f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.TeleportKiller])
            .SetValueFormat(OptionFormat.Seconds);

        Maximum = new IntegerOptionItem(Id + 12, "TeleportKillerMaximum", new(0, 999, 1), 2, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.TeleportKiller])
            .SetValueFormat(OptionFormat.Times);

        Duration = new FloatOptionItem(Id + 13, "TeleportKillerDuration", new(0f, 15f, 1f), 5f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.TeleportKiller])
            .SetValueFormat(OptionFormat.Seconds);

        TeleportKillerFall = new BooleanOptionItem(Id + 14, "TeleportKillerFall", false, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.TeleportKiller]);

        TeleportKillerVentgaaa = new BooleanOptionItem(Id + 15, "TeleportKillerVentgaaa", false, TabGroup.ImpostorRoles)
            .SetParent(TeleportKillerFall);

        TeleportKillerPlatformFall = new BooleanOptionItem(Id + 16, "TeleportKillerPlatformFall", false, TabGroup.ImpostorRoles)
            .SetParent(TeleportKillerFall);

        TeleportKillerLadderFall = new BooleanOptionItem(Id + 17, "TeleportKillerLadderFall", false, TabGroup.ImpostorRoles)
            .SetParent(TeleportKillerFall);

        TeleportKillerDokkaaaan = new BooleanOptionItem(Id + 18, "TeleportKillerDokkaaaan", false, TabGroup.ImpostorRoles)
            .SetParent(TeleportKillerFall);

        TeleportKillerKillCooldownReset = new BooleanOptionItem(Id + 19, "TeleportKillerKillCooldownReset", false, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.TeleportKiller]);

        TeleportKillerChangeDeathReason = new BooleanOptionItem(Id + 20, "TeleportKillerChangeDeathReason", false, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.TeleportKiller]);
    }

    public override void Init()
    {
        PlayerIdList = [];
    }

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);
        TeleportKillerId = playerId;
        usecount = 0;
        PendingKillTargets = [];
        IsAnimation = false;
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
        float cd = AbilityCooldown.GetFloat();
        if (IntroCutsceneDestroyPatch.PreventKill) cd = Mathf.Max(cd, 12f);

        AURoleOptions.ShapeshifterCooldown = Maximum.GetInt() != 0 && usecount >= Maximum.GetInt() ? 200f : cd;
        AURoleOptions.ShapeshifterDuration = Duration.GetFloat();
        AURoleOptions.KillCooldown = KillCooldown.GetFloat();
    }

    public override void SetButtonTexts(HudManager hud, byte id)
    {
        hud.AbilityButton?.OverrideText(Translator.GetString("TeleportKillerAbility"));
    }

    public override bool OnShapeshift(PlayerControl shapeshifter, PlayerControl target, bool shapeshifting)
    {
        if (!shapeshifting) return false;
        if (shapeshifter.PlayerId == target.PlayerId) return false;

        int max = Maximum.GetInt();
        if (max != 0 && usecount >= max) return false;

        usecount++;

        LateTask.New(() =>
        {
            if (!AmongUsClient.Instance.AmHost) return;

            if (!target.IsAlive() && TeleportKillerDokkaaaan.GetBool())
            {
                shapeshifter.Suicide(PlayerState.DeathReason.Bombed);
                return;
            }

            if (!TPCheck(target, killerTP: true))
            {
                if ((target.inVent || target.MyPhysics.Animations.IsPlayingEnterVentAnimation()) && TeleportKillerVentgaaa.GetBool())
                {
                    shapeshifter.TP(target.Pos(), log: false);
                    shapeshifter.Suicide(PlayerState.DeathReason.Bombed);
                    return;
                }

                if (!target.IsAlive()) return;
                PendingKillTargets.Add(target.PlayerId);
            }
            else
            {
                TeleportKill(shapeshifter, target);
            }
        }, 1.5f, "TeleportKiller.Shapeshift");

        return false;
    }

    private void TeleportKill(PlayerControl killer, PlayerControl target)
    {
        Vector2 killerOriginalPos = killer.Pos();
        bool check = TPCheck(target);
        bool fallDeath = false;

        if ((target.inVent || target.MyPhysics.Animations.IsPlayingEnterVentAnimation()) && !TeleportKillerVentgaaa.GetBool())
        {
            int ventId = target.GetClosestVent()?.Id ?? -1;
            if (ventId >= 0) target.MyPhysics.RpcBootFromVent(ventId);
            LateTask.New(() => PendingKillTargets.Add(target.PlayerId), 1.5f, "TeleportKiller.VentBoot");
            check = false;
        }
        else
        {
            killer.TP(target.Pos(), log: false);
        }

        if (check)
        {
            // Start fall animation if target is on ladder or platform
            if (target.onLadder || target.inMovingPlat)
            {
                fallDeath = true;
                AnimStart = killer.Pos();
                AnimGoal = AnimStart - new Vector2(0f, 4f);
                AnimT = 0f;
                IsAnimation = true;
            }

            target.TP(killerOriginalPos, log: false);

            LateTask.New(() =>
            {
                if (!target.IsAlive()) return;
                if (target.inVent || target.MyPhysics.Animations.IsPlayingEnterVentAnimation()) return;

                PlayerState.DeathReason reason = TeleportKillerChangeDeathReason.GetBool()
                    ? PlayerState.DeathReason.Spell
                    : PlayerState.DeathReason.Kill;

                target.SetRealKiller(killer);
                target.Suicide(reason, killer);

                if (!fallDeath && TeleportKillerKillCooldownReset.GetBool())
                {
                    Main.AllPlayerKillCooldown[killer.PlayerId] = KillCooldown.GetFloat();
                    killer.SyncSettings();
                }
            }, 0.5f, "TeleportKiller.Kill");
        }
    }

    private static bool TPCheck(PlayerControl target, bool killerTP = false)
    {
        if (target.onLadder || target.MyPhysics.Animations.IsPlayingAnyLadderAnimation())
            return killerTP && TeleportKillerLadderFall.GetBool();

        if (target.inMovingPlat)
            return killerTP && TeleportKillerPlatformFall.GetBool();

        if (target.MyPhysics.Animations.IsPlayingEnterVentAnimation() || target.inVent)
            return killerTP && !TeleportKillerVentgaaa.GetBool();

        if (!target.IsAlive()) return false;

        return true;
    }

    public override void OnFixedUpdate(PlayerControl pc)
    {
        if (!AmongUsClient.Instance.AmHost) return;

        if (PendingKillTargets.Count > 0)
        {
            foreach (byte targetId in PendingKillTargets.ToArray())
            {
                PlayerControl target = Utils.GetPlayerById(targetId);
                if (target == null || !target.IsAlive())
                {
                    PendingKillTargets.Remove(targetId);
                    continue;
                }

                if (!TPCheck(target)) continue;

                TeleportKill(pc, target);
                PendingKillTargets.Remove(targetId);
            }
        }

        if (IsAnimation)
        {
            AnimT += Time.fixedDeltaTime / 2f;
            if (AnimT > 1f) AnimT = 1f;

            pc.TP(Vector2.Lerp(AnimStart, AnimGoal, AnimT), log: false);

            if (AnimT >= 1f)
            {
                IsAnimation = false;
                pc.Suicide(PlayerState.DeathReason.Fall);
            }
        }
    }

    public override void OnReportDeadBody()
    {
        PendingKillTargets?.Clear();
        IsAnimation = false;
    }

    public override string GetProgressText(byte playerId, bool comms)
    {
        if (playerId != TeleportKillerId) return string.Empty;
        int max = Maximum.GetInt();
        if (max == 0) return string.Empty;
        int remaining = max - usecount;
        Color color = remaining > 0 ? Palette.ImpostorRed : Color.gray;
        return Utils.ColorString(color, $"({remaining})");
    }
}
