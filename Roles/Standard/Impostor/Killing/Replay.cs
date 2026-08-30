using System;
using AmongUs.GameOptions;
using EndKnot.Modules;
using EndKnot.Patches;
using static EndKnot.Translator;

namespace EndKnot.Roles;

internal class Replay : RoleBase
{
    public static bool On;

    private static OptionItem KillCooldown;
    private static OptionItem ReplayBombCooldown;
    private static OptionItem ReplayBlastRadius;
    private static OptionItem ReplayMaxBlastCount;
    private static OptionItem ReplayRequiredKills1;
    private static OptionItem ReplayRequiredKills2;
    private static OptionItem ReplayRequiredKills3;
    private static OptionItem ReplayRequiredKills4;
    private static OptionItem ReplayRequiredKills5;
    private static OptionItem ReplayRequiredKills6;
    private static OptionItem ReplayRequiredKills7;
    private static OptionItem ReplayRequiredKills8;
    private static OptionItem ReplayRequiredKills9;
    private static OptionItem ReplayRequiredKills10;
    private static OptionItem ReplayRequiredKills11;
    private static OptionItem ReplayRequiredKills12;
    private static OptionItem ReplayRequiredKills13;
    private static OptionItem ReplayRequiredKills14;
    private static OptionItem ReplayRequiredKills15;
    private static OptionItem ReplaySelfExplodesAtMax;
    private static OptionItem ReplaySelfDiesOnFail;
    private static OptionItem ReplayIncludeImpostors;

    private static OptionItem[] RequiredKillsList;

    private int BlastsDone;
    private bool Locked;

    public override bool IsEnable => On;

    public override void SetupCustomOption()
    {
        StartSetup(709400)
            .AutoSetupOption(ref KillCooldown, 30f, new FloatValueRule(0f, 180f, 0.5f), OptionFormat.Seconds)
            .AutoSetupOption(ref ReplayBombCooldown, 30f, new FloatValueRule(0f, 180f, 0.5f), OptionFormat.Seconds)
            .AutoSetupOption(ref ReplayBlastRadius, 3f, new FloatValueRule(0.5f, 5f, 0.5f), OptionFormat.Multiplier)
            .AutoSetupOption(ref ReplayMaxBlastCount, 3, new IntegerValueRule(1, 15, 1))
            .AutoSetupOption(ref ReplayRequiredKills1, 3, new IntegerValueRule(1, 15, 1), overrideParent: ReplayMaxBlastCount)
            .AutoSetupOption(ref ReplayRequiredKills2, 2, new IntegerValueRule(1, 15, 1), overrideParent: ReplayMaxBlastCount)
            .AutoSetupOption(ref ReplayRequiredKills3, 2, new IntegerValueRule(1, 15, 1), overrideParent: ReplayMaxBlastCount)
            .AutoSetupOption(ref ReplayRequiredKills4, 3, new IntegerValueRule(1, 15, 1), overrideParent: ReplayMaxBlastCount)
            .AutoSetupOption(ref ReplayRequiredKills5, 3, new IntegerValueRule(1, 15, 1), overrideParent: ReplayMaxBlastCount)
            .AutoSetupOption(ref ReplayRequiredKills6, 3, new IntegerValueRule(1, 15, 1), overrideParent: ReplayMaxBlastCount)
            .AutoSetupOption(ref ReplayRequiredKills7, 3, new IntegerValueRule(1, 15, 1), overrideParent: ReplayMaxBlastCount)
            .AutoSetupOption(ref ReplayRequiredKills8, 3, new IntegerValueRule(1, 15, 1), overrideParent: ReplayMaxBlastCount)
            .AutoSetupOption(ref ReplayRequiredKills9, 3, new IntegerValueRule(1, 15, 1), overrideParent: ReplayMaxBlastCount)
            .AutoSetupOption(ref ReplayRequiredKills10, 3, new IntegerValueRule(1, 15, 1), overrideParent: ReplayMaxBlastCount)
            .AutoSetupOption(ref ReplayRequiredKills11, 3, new IntegerValueRule(1, 15, 1), overrideParent: ReplayMaxBlastCount)
            .AutoSetupOption(ref ReplayRequiredKills12, 3, new IntegerValueRule(1, 15, 1), overrideParent: ReplayMaxBlastCount)
            .AutoSetupOption(ref ReplayRequiredKills13, 3, new IntegerValueRule(1, 15, 1), overrideParent: ReplayMaxBlastCount)
            .AutoSetupOption(ref ReplayRequiredKills14, 3, new IntegerValueRule(1, 15, 1), overrideParent: ReplayMaxBlastCount)
            .AutoSetupOption(ref ReplayRequiredKills15, 3, new IntegerValueRule(1, 15, 1), overrideParent: ReplayMaxBlastCount)
            .AutoSetupOption(ref ReplaySelfExplodesAtMax, true)
            .AutoSetupOption(ref ReplaySelfDiesOnFail, true)
            .AutoSetupOption(ref ReplayIncludeImpostors, true);

        RequiredKillsList =
        [
            ReplayRequiredKills1, ReplayRequiredKills2, ReplayRequiredKills3, ReplayRequiredKills4, ReplayRequiredKills5,
            ReplayRequiredKills6, ReplayRequiredKills7, ReplayRequiredKills8, ReplayRequiredKills9, ReplayRequiredKills10,
            ReplayRequiredKills11, ReplayRequiredKills12, ReplayRequiredKills13, ReplayRequiredKills14, ReplayRequiredKills15
        ];
    }

    public override void Init()
    {
        On = false;
    }

    public override void Add(byte playerId)
    {
        On = true;
        BlastsDone = 0;
        Locked = false;
    }

    public override void SetKillCooldown(byte id)
    {
        Main.AllPlayerKillCooldown[id] = KillCooldown.GetFloat();
    }

    public override void ApplyGameOptions(IGameOptions opt, byte playerId)
    {
        try
        {
            float cd = ReplayBombCooldown.GetFloat();

            // PreventKill 窓 (intro 後 StartingKillCooldown 秒) 内の押下は PhantomRolePatch.CheckTrigger の
            // キャンセルパスに入り OnVanish へ届かず無音で消えるため、初回クールダウンを窓より長くクランプする
            // (EvilBomber.ApplyGameOptions と同型)。
            if (IntroCutsceneDestroyPatch.PreventKill) cd = Math.Max(cd, 12f);

            AURoleOptions.PhantomCooldown = cd;
        }
        catch { }
    }

    public override void SetButtonTexts(HudManager hud, byte id)
    {
        hud.AbilityButton?.OverrideText(Translator.GetString("ReplayAbilityButtonText"));
    }

    public override bool OnVanish(PlayerControl pc)
    {
        if (Locked)
        {
            pc.Notify(GetString("ReplayLocked"));
            return false;
        }

        Blast(pc);
        return false;
    }

    private void Blast(PlayerControl pc)
    {
        if (Pelican.IsEaten(pc.PlayerId)) return;

        CustomSoundsManager.RPCPlayCustomSoundAll("Boom");

        float radius = ReplayBlastRadius.GetFloat();
        var killCount = 0;

        foreach (PlayerControl tg in Main.EnumeratePlayerControls())
        {
            try
            {
                if (!tg.IsModdedClient()) tg.KillFlash();

                if (!tg.IsAliveWithConditions() || Medic.ProtectList.Contains(tg.PlayerId) || tg.inVent || tg.Is(CustomRoles.Pestilence)) continue;
                if (tg.PlayerId == pc.PlayerId) continue;
                if (!ReplayIncludeImpostors.GetBool() && tg.Is(CustomRoleTypes.Impostor)) continue;
                if (!FastVector2.DistanceWithinRange(pc.Pos(), tg.Pos(), radius)) continue;

                tg.Suicide(PlayerState.DeathReason.Bombed, pc);
                killCount++;

                if (pc.AmOwner && tg.IsImpostor())
                    Achievements.Type.FriendlyFire.Complete();
            }
            catch (Exception e) { Utils.ThrowException(e); }
        }

        int required = RequiredKillsList[BlastsDone].GetInt();
        BlastsDone++;

        bool selfDestruct = false;

        if (killCount < required)
        {
            Locked = true;
            if (ReplaySelfDiesOnFail.GetBool()) selfDestruct = true;
        }
        else if (BlastsDone >= ReplayMaxBlastCount.GetInt())
        {
            Locked = true;
            if (ReplaySelfExplodesAtMax.GetBool()) selfDestruct = true;
        }

        if (selfDestruct)
        {
            LateTask.New(() =>
            {
                if (Main.AllAlivePlayerControlsCount > 1 && !GameStates.IsEnded)
                    pc.Suicide(PlayerState.DeathReason.Bombed);
            }, 0.2f, "Replay Suicide");
        }
    }
}
