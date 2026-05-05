using static EndKnot.Options;

namespace EndKnot.Roles;

internal class ImpostorVanillaRoles : IVanillaSettingHolder
{
    public static OptionItem PhantomCooldown;
    public static OptionItem PhantomDuration;
    public static OptionItem ShapeshiftCD;
    public static OptionItem ShapeshiftDur;
    public static OptionItem ViperDissolveTime;
    public TabGroup Tab => TabGroup.ImpostorRoles;

    public void SetupCustomOption()
    {
        SetupRoleOptions(300, Tab, CustomRoles.ImpostorEndKnot);
        SetupRoleOptions(350, Tab, CustomRoles.PhantomEndKnot);

        PhantomCooldown = new FloatOptionItem(352, "PhantomCooldown", new(1f, 180f, 1f), 30f, Tab)
            .SetParent(CustomRoleSpawnChances[CustomRoles.PhantomEndKnot])
            .SetValueFormat(OptionFormat.Seconds);

        PhantomDuration = new FloatOptionItem(353, "PhantomDuration", new(1f, 60f, 1f), 10f, Tab)
            .SetParent(CustomRoleSpawnChances[CustomRoles.PhantomEndKnot])
            .SetValueFormat(OptionFormat.Seconds);

        SetupRoleOptions(400, Tab, CustomRoles.ShapeshifterEndKnot);

        ShapeshiftCD = new FloatOptionItem(402, "ShapeshiftCooldown", new(1f, 180f, 1f), 30f, Tab)
            .SetParent(CustomRoleSpawnChances[CustomRoles.ShapeshifterEndKnot])
            .SetValueFormat(OptionFormat.Seconds);

        ShapeshiftDur = new FloatOptionItem(403, "ShapeshiftDuration", new(1f, 60f, 1f), 10f, Tab)
            .SetParent(CustomRoleSpawnChances[CustomRoles.ShapeshifterEndKnot])
            .SetValueFormat(OptionFormat.Seconds);
        
        SetupRoleOptions(410, Tab, CustomRoles.ViperEndKnot);
        
        ViperDissolveTime = new FloatOptionItem(412, "ViperDissolveTime", new(0f, 60f, 0.5f), 10f, Tab)
            .SetParent(CustomRoleSpawnChances[CustomRoles.ViperEndKnot])
            .SetValueFormat(OptionFormat.Seconds);
    }
}