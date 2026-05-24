using System.Collections.Generic;
using AmongUs.GameOptions;
using EndKnot.Modules;
using UnityEngine;

namespace EndKnot.Roles;

public class Ballooner : RoleBase
{
    private const int Id = 699500;
    private static List<byte> PlayerIdList = [];

    private static OptionItem KillCooldown;
    private static OptionItem AbilityCooldown;
    private static OptionItem MinBoomDis;
    private static OptionItem MaxBoomDis;
    private static OptionItem ChargeWalk;
    private static OptionItem ChargeStep;
    private static OptionItem AfterMeetingRemoveCharge;
    private static OptionItem Suicide;
    private static OptionItem TargetImpostor;

    private float NowBoomDis;
    private float NowWalkCount;
    private Vector2 OldPosition;
    private byte BalloonerId;

    public override bool IsEnable => PlayerIdList.Count > 0;

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.ImpostorRoles, CustomRoles.Ballooner);

        KillCooldown = new FloatOptionItem(Id + 10, "KillCooldown", new(0f, 180f, 0.5f), 30f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Ballooner])
            .SetValueFormat(OptionFormat.Seconds);

        AbilityCooldown = new FloatOptionItem(Id + 11, "BalloonerAbilityCooldown", new(1f, 180f, 0.5f), 30f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Ballooner])
            .SetValueFormat(OptionFormat.Seconds);

        MinBoomDis = new FloatOptionItem(Id + 12, "BalloonerMinBoomDis", new(-5f, 10f, 0.25f), 0f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Ballooner])
            .SetValueFormat(OptionFormat.Multiplier);

        MaxBoomDis = new FloatOptionItem(Id + 13, "BalloonerMaxBoomDis", new(0.25f, 15f, 0.25f), 3f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Ballooner])
            .SetValueFormat(OptionFormat.Multiplier);

        ChargeWalk = new FloatOptionItem(Id + 14, "BalloonerChargeWalk", new(1f, 300f, 1f), 60f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Ballooner]);

        ChargeStep = new FloatOptionItem(Id + 15, "BalloonerChargeStep", new(0.1f, 5f, 0.05f), 0.5f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Ballooner])
            .SetValueFormat(OptionFormat.Multiplier);

        AfterMeetingRemoveCharge = new FloatOptionItem(Id + 16, "BalloonerAfterMeetingRemoveCharge", new(0f, 30f, 0.5f), 0.5f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Ballooner])
            .SetValueFormat(OptionFormat.Multiplier);

        Suicide = new BooleanOptionItem(Id + 17, "BalloonerSuicide", false, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Ballooner]);

        TargetImpostor = new BooleanOptionItem(Id + 18, "BalloonerTargetImpostor", true, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Ballooner]);
    }

    public override void Init()
    {
        PlayerIdList = [];
    }

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);
        BalloonerId = playerId;
        OldPosition = new Vector2(50f, 50f);
        ResetBalloon();
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
        if (Options.UsePhantomBasis.GetBool())
            AURoleOptions.PhantomCooldown = AbilityCooldown.GetFloat();
        else if (!Options.UsePets.GetBool())
        {
            AURoleOptions.ShapeshifterCooldown = AbilityCooldown.GetFloat();
            AURoleOptions.ShapeshifterDuration = 1f;
        }
    }

    public override void OnFixedUpdate(PlayerControl pc)
    {
        if (!pc.IsAlive()) return;

        Vector2 currentPos = pc.Pos();

        if (OldPosition == new Vector2(50f, 50f) || NowBoomDis >= MaxBoomDis.GetFloat()
            || pc.inVent || pc.MyPhysics.Animations.IsPlayingEnterVentAnimation()
            || pc.MyPhysics.Animations.IsPlayingAnyLadderAnimation() || pc.inMovingPlat
            || !pc.CanMove)
        {
            OldPosition = currentPos;
            return;
        }

        float distance = Vector2.Distance(OldPosition, currentPos);
        OldPosition = currentPos;

        NowWalkCount += distance;
        if (NowWalkCount < ChargeWalk.GetFloat()) return;

        NowWalkCount = 0f;
        NowBoomDis = Mathf.Clamp(NowBoomDis + ChargeStep.GetFloat(), MinBoomDis.GetFloat(), MaxBoomDis.GetFloat());
        NowBoomDis = Mathf.Round(NowBoomDis * 100f) / 100f;
        Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc);
    }

    public override void SetButtonTexts(HudManager hud, byte id)
    {
        if (Options.UsePets.GetBool() && !Options.UsePhantomBasis.GetBool())
            hud.PetButton?.OverrideText(Translator.GetString("BalloonerAbility"));
        else
            hud.AbilityButton?.OverrideText(Translator.GetString("BalloonerAbility"));
    }

    public override void OnPet(PlayerControl pc)
    {
        Explode(pc);
    }

    public override bool OnShapeshift(PlayerControl shapeshifter, PlayerControl target, bool shapeshifting)
    {
        if (!shapeshifting) return true;
        Explode(shapeshifter);
        return false;
    }

    public override bool OnVanish(PlayerControl pc)
    {
        Explode(pc);
        return false;
    }

    private void Explode(PlayerControl pc)
    {
        if (NowBoomDis <= 0f) return;
        if (Pelican.IsEaten(pc.PlayerId)) return;

        bool suicideEnabled = Suicide.GetBool();
        bool targetImpostor = TargetImpostor.GetBool();
        float radius = NowBoomDis;

        foreach (PlayerControl tg in Main.AllAlivePlayerControlsToList)
        {
            if (tg.PlayerId == pc.PlayerId && !suicideEnabled) continue;
            if (tg.PlayerId != pc.PlayerId && tg.Is(CustomRoleTypes.Impostor) && !targetImpostor) continue;
            if (Pelican.IsEaten(tg.PlayerId) || Medic.ProtectList.Contains(tg.PlayerId) || tg.Is(CustomRoles.Pestilence)) continue;
            if (!FastVector2.DistanceWithinRange(pc.Pos(), tg.Pos(), radius)) continue;

            if (!tg.IsModdedClient()) tg.KillFlash();
            tg.SetRealKiller(pc);
            tg.Suicide(PlayerState.DeathReason.Bombed, pc);
        }

        if (!suicideEnabled)
        {
            ResetBalloon();
            Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc);
        }

        LateTask.New(() =>
        {
            Main.AllPlayerKillCooldown[pc.PlayerId] = KillCooldown.GetFloat();
            pc.SetKillCooldown();
        }, 0.3f, "Ballooner.PostExplode");
    }

    public override void AfterMeetingTasks()
    {
        NowBoomDis = Mathf.Clamp(NowBoomDis - AfterMeetingRemoveCharge.GetFloat(), MinBoomDis.GetFloat(), MaxBoomDis.GetFloat());
        NowBoomDis = Mathf.Round(NowBoomDis * 100f) / 100f;
        NowWalkCount = 0f;
        OldPosition = new Vector2(50f, 50f);

        PlayerControl pc = Utils.GetPlayerById(BalloonerId);
        if (pc != null) Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc);
    }

    private void ResetBalloon()
    {
        NowBoomDis = MinBoomDis.GetFloat();
        NowWalkCount = 0f;
    }

    public override string GetProgressText(byte playerId, bool comms)
    {
        if (playerId != BalloonerId) return string.Empty;

        float max = MaxBoomDis.GetFloat();
        Color color = NowBoomDis <= 0f
            ? Palette.DisabledGrey
            : NowBoomDis >= max
                ? Palette.ImpostorRed
                : (Color)new Color32(255, 165, 0, 255);

        return Utils.ColorString(color, $" ({NowBoomDis})");
    }

    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud = false, bool meeting = false)
    {
        if (meeting) return string.Empty;
        if (seer.PlayerId != BalloonerId || seer.PlayerId != target.PlayerId) return string.Empty;

        var sb = new StringBuilder();
        if (NowBoomDis < MaxBoomDis.GetFloat())
            sb.Append(Translator.GetString("BalloonerLowerText1"));
        if (NowBoomDis > 0f)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(Translator.GetString("BalloonerLowerText2"));
        }

        if (sb.Length == 0) return string.Empty;

        string text = hud ? sb.ToString() : $"<size=60%>{sb}</size>";
        return Utils.ColorString(new Color32(255, 165, 0, 255), text);
    }
}
