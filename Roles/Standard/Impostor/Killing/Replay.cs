using System;
using AmongUs.GameOptions;
using EndKnot.Modules;
using EndKnot.Patches;
using UnityEngine;
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
    private static OptionItem ReplayRadiusGrowthPerBlast;
    private static OptionItem ReplayCooldownReductionPerBlast;

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
            .AutoSetupOption(ref ReplayRequiredKills4, 2, new IntegerValueRule(1, 15, 1), overrideParent: ReplayMaxBlastCount)
            .AutoSetupOption(ref ReplayRequiredKills5, 2, new IntegerValueRule(1, 15, 1), overrideParent: ReplayMaxBlastCount)
            .AutoSetupOption(ref ReplayRequiredKills6, 2, new IntegerValueRule(1, 15, 1), overrideParent: ReplayMaxBlastCount)
            .AutoSetupOption(ref ReplayRequiredKills7, 2, new IntegerValueRule(1, 15, 1), overrideParent: ReplayMaxBlastCount)
            .AutoSetupOption(ref ReplayRequiredKills8, 2, new IntegerValueRule(1, 15, 1), overrideParent: ReplayMaxBlastCount)
            .AutoSetupOption(ref ReplayRequiredKills9, 2, new IntegerValueRule(1, 15, 1), overrideParent: ReplayMaxBlastCount)
            .AutoSetupOption(ref ReplayRequiredKills10, 2, new IntegerValueRule(1, 15, 1), overrideParent: ReplayMaxBlastCount)
            .AutoSetupOption(ref ReplayRequiredKills11, 2, new IntegerValueRule(1, 15, 1), overrideParent: ReplayMaxBlastCount)
            .AutoSetupOption(ref ReplayRequiredKills12, 2, new IntegerValueRule(1, 15, 1), overrideParent: ReplayMaxBlastCount)
            .AutoSetupOption(ref ReplayRequiredKills13, 2, new IntegerValueRule(1, 15, 1), overrideParent: ReplayMaxBlastCount)
            .AutoSetupOption(ref ReplayRequiredKills14, 2, new IntegerValueRule(1, 15, 1), overrideParent: ReplayMaxBlastCount)
            .AutoSetupOption(ref ReplayRequiredKills15, 2, new IntegerValueRule(1, 15, 1), overrideParent: ReplayMaxBlastCount)
            .AutoSetupOption(ref ReplaySelfExplodesAtMax, true)
            .AutoSetupOption(ref ReplaySelfDiesOnFail, true)
            .AutoSetupOption(ref ReplayIncludeImpostors, false)
            .AutoSetupOption(ref ReplayRadiusGrowthPerBlast, 0.5f, new FloatValueRule(0f, 2f, 0.1f), OptionFormat.Multiplier)
            .AutoSetupOption(ref ReplayCooldownReductionPerBlast, 5f, new FloatValueRule(0f, 30f, 0.5f), OptionFormat.Seconds);

        RequiredKillsList =
        [
            ReplayRequiredKills1, ReplayRequiredKills2, ReplayRequiredKills3, ReplayRequiredKills4, ReplayRequiredKills5,
            ReplayRequiredKills6, ReplayRequiredKills7, ReplayRequiredKills8, ReplayRequiredKills9, ReplayRequiredKills10,
            ReplayRequiredKills11, ReplayRequiredKills12, ReplayRequiredKills13, ReplayRequiredKills14, ReplayRequiredKills15
        ];

        // 親が Integer (最小値1) の子は GetBool() が常に true になり出し分けが効かないため、
        // 最大爆破回数を超える行は SetHidden で明示的に畳む (Disguiser / Lovers と同型)。
        SyncRequiredKillsVisibility();
        ReplayMaxBlastCount.RegisterUpdateValueEvent((_, _, _) => SyncRequiredKillsVisibility()).SetRunEventOnLoad(true);
    }

    private static void SyncRequiredKillsVisibility()
    {
        if (RequiredKillsList == null) return;

        int max = ReplayMaxBlastCount.GetInt();
        for (var i = 0; i < RequiredKillsList.Length; i++) RequiredKillsList[i].SetHidden(i >= max);
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
            // アンコールを重ねるほどクールダウンが短くなる (Blast 後の SyncSettings で即時反映される)。
            float cd = Math.Max(ReplayBombCooldown.GetFloat() - BlastsDone * ReplayCooldownReductionPerBlast.GetFloat(), 1f);

            // イントロ直後の PreventKill 窓では OnVanish が呼ばれず CD だけリセットされる (初回押下が無音で不発)。
            // 窓の長さは固定 10 秒ではなく Options.StartingKillCooldown なので、それに合わせてクランプする。
            if (IntroCutsceneDestroyPatch.PreventKill)
                cd = Mathf.Max(cd, (Options.StartingKillCooldown?.GetFloat() ?? 10f) + 2f);

            AURoleOptions.PhantomCooldown = cd;
        }
        catch { }
    }

    public override void SetButtonTexts(HudManager hud, byte id)
    {
        hud.AbilityButton?.OverrideText(Translator.GetString("ReplayAbilityButtonText"));
    }

    public override string GetProgressText(byte playerId, bool comms)
    {
        if (Locked) return Utils.ColorString(Color.gray, GetString("ReplayProgressSealed"));

        int max = ReplayMaxBlastCount.GetInt();
        int required = RequiredKillsList[Math.Min(BlastsDone, RequiredKillsList.Length - 1)].GetInt();
        return Utils.ColorString(Utils.GetRoleColor(CustomRoles.Replay), string.Format(GetString("ReplayProgress"), BlastsDone, max, required));
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

        // アンコールを重ねるほど爆破範囲が広がる (BlastsDone は加算前なので初回は素の半径)。
        float radius = ReplayBlastRadius.GetFloat() + BlastsDone * ReplayRadiusGrowthPerBlast.GetFloat();
        var killCount = 0;

        foreach (PlayerControl tg in Main.EnumeratePlayerControls())
        {
            try
            {
                if (!tg.IsAliveWithConditions() || Medic.ProtectList.Contains(tg.PlayerId) || tg.inVent || tg.Is(CustomRoles.Pestilence)) continue;
                if (tg.PlayerId == pc.PlayerId) continue;
                if (!ReplayIncludeImpostors.GetBool() && tg.Is(CustomRoleTypes.Impostor)) continue;
                if (!FastVector2.DistanceWithinRange(pc.Pos(), tg.Pos(), radius)) continue;

                // KillFlash は ReactorFlash 経由で対象1人ずつ desync RPC を 2〜3 発送るので、爆破半径に
                // 入った相手だけに絞る (EvilBomber / EvilJumper と同型。全員へ撃つと CD 下限 1 秒の
                // 連射と組み合わさって公式鯖の fan-out キック帯に入る)。
                if (!tg.IsModdedClient()) tg.KillFlash();

                bool wasAlive = tg.IsAlive();
                tg.Suicide(PlayerState.DeathReason.Bombed, pc);

                // Suicide は保護役職 (ベテランの反撃態勢・シュレディンガーの猫・あかずきん等) では
                // 相手を殺さずに返るため、「呼んだ回数」ではなく「実際に死んだ数」を必要キル数と突き合わせる。
                if (!wasAlive || tg.IsAlive()) continue;

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

        // 短縮後のクールダウンを、PhantomRolePatch の RpcResetAbilityCooldown が走る前に届ける
        // (MarkDirtySettings だと 0.2 秒バッチ送信に載って間に合わないので即時送信を使う)。
        if (!Locked) pc.SyncSettings();

        // 進捗表示は本人向けなので seer 限定で更新する (target 指定は全員宛の fan-out になる)。
        Utils.NotifyRoles(SpecifySeer: pc);

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
