using AmongUs.GameOptions;

namespace EndKnot.Roles;

public class Camouflager : RoleBase
{
    private const int Id = 2500;

    public static OptionItem CamouflageCooldown;
    private static OptionItem CamouflageDuration;
    private static OptionItem CamoLimitOpt;
    public static OptionItem AbilityUseGainWithEachKill;
    public static OptionItem DoesntSpawnOnFungle;

    public static bool IsActive;
    public static bool On;

    public override bool IsEnable => On;

    public override void SetupCustomOption()
    {
        Options.SetupSingleRoleOptions(Id, TabGroup.ImpostorRoles, CustomRoles.Camouflager);

        CamouflageCooldown = new FloatOptionItem(Id + 2, "CamouflageCooldown", new(1f, 60f, 1f), 25f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Camouflager])
            .SetValueFormat(OptionFormat.Seconds);

        CamouflageDuration = new FloatOptionItem(Id + 3, "CamouflageDuration", new(1f, 30f, 1f), 12f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Camouflager])
            .SetValueFormat(OptionFormat.Seconds);

        CamoLimitOpt = new IntegerOptionItem(Id + 4, "AbilityUseLimit", new(0, 20, 1), 1, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Camouflager])
            .SetValueFormat(OptionFormat.Times);

        AbilityUseGainWithEachKill = new FloatOptionItem(Id + 5, "AbilityUseGainWithEachKill", new(0f, 5f, 0.1f), 0.5f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Camouflager])
            .SetValueFormat(OptionFormat.Times);

        DoesntSpawnOnFungle = new BooleanOptionItem(Id + 6, "DoesntSpawnOnFungle", true, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Camouflager]);
    }

    public override void ApplyGameOptions(IGameOptions opt, byte id)
    {
        if (Options.UsePhantomBasis.GetBool())
            AURoleOptions.PhantomCooldown = CamouflageCooldown.GetFloat();
        else
        {
            AURoleOptions.ShapeshifterCooldown = CamouflageCooldown.GetFloat();
            AURoleOptions.ShapeshifterDuration = CamouflageDuration.GetFloat();
        }
    }

    public override void Init()
    {
        IsActive = false;
        On = false;
    }

    public override void Add(byte playerId)
    {
        playerId.SetAbilityUseLimit(CamoLimitOpt.GetFloat());
        On = true;
    }

    public override void Remove(byte playerId)
    {
        if (IsActive) IsDead();
    }

    private static void PlayMist()
    {
        foreach (PlayerControl p in Main.EnumerateAlivePlayerControls())
            EndKnot.Modules.ExplosionFx.Play(EndKnot.Modules.ExplosionFx.Kind.CamoMist, p.Pos(), 1f);
    }

    public override bool OnShapeshift(PlayerControl pc, PlayerControl target, bool shapeshifting)
    {
        if (!shapeshifting)
        {
            bool wasActive = IsActive;
            IsActive = false;
            Camouflage.CheckCamouflage();
            if (wasActive) PlayMist();
            return true;
        }

        if (pc.GetAbilityUseLimit() < 1 && !Options.DisableShapeshiftAnimations.GetBool())
        {
            pc.SetKillCooldown(CamouflageDuration.GetFloat() + 1f);
            return true;
        }

        pc.RpcRemoveAbilityUse(notify: false);
        IsActive = true;
        Camouflage.CheckCamouflage();
        PlayMist();

        return true;
    }

    public override bool OnVanish(PlayerControl pc)
    {
        if (pc.GetAbilityUseLimit() < 1) return false;
        pc.RpcRemoveAbilityUse(notify: false);

        IsActive = true;
        Camouflage.CheckCamouflage();
        PlayMist();

        LateTask.New(() =>
        {
            if (GameStates.IsInTask && !ExileController.Instance)
            {
                bool wasActive = IsActive;
                IsActive = false;
                Camouflage.CheckCamouflage();
                if (wasActive) PlayMist();
            }
        }, CamouflageDuration.GetFloat(), "Revert Camouflage");

        return false;
    }

    public override void OnPet(PlayerControl pc)
    {
        OnVanish(pc);
    }

    public override void OnReportDeadBody()
    {
        IsActive = false;
        Camouflage.CheckCamouflage();
    }

    public static void IsDead()
    {
        IsActive = false;
        Camouflage.CheckCamouflage();
    }

    public override void SetButtonTexts(HudManager hud, byte id)
    {
        hud.AbilityButton?.OverrideText(Translator.GetString("CamouflagerShapeshiftText"));
    }
}
