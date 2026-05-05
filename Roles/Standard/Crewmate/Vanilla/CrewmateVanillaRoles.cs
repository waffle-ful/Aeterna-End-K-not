using static EndKnot.Options;

namespace EndKnot.Roles;

internal class CrewmateVanillaRoles : IVanillaSettingHolder
{
    public static OptionItem VanillaCrewmateCannotBeGuessed;
    public static OptionItem EngineerCD;
    public static OptionItem EngineerDur;
    public static OptionItem NoiseMakerImpostorAlert;
    public static OptionItem NoisemakerAlertDuration;
    public static OptionItem ScientistDur;
    public static OptionItem ScientistCD;
    public static OptionItem TrackerCooldown;
    public static OptionItem TrackerDuration;
    public static OptionItem TrackerDelay;
    public static OptionItem DetectiveSuspectLimit;
    public TabGroup Tab => TabGroup.CrewmateRoles;

    public void SetupCustomOption()
    {
        SetupRoleOptions(5020, Tab, CustomRoles.CrewmateEndKnot);

        VanillaCrewmateCannotBeGuessed = new BooleanOptionItem(5022, "VanillaCrewmateCannotBeGuessed", false, Tab)
            .SetParent(CustomRoleSpawnChances[CustomRoles.CrewmateEndKnot]);

        SetupRoleOptions(5000, Tab, CustomRoles.EngineerEndKnot);

        EngineerCD = new FloatOptionItem(5002, "VentCooldown", new(0f, 250f, 1f), 30f, Tab)
            .SetParent(CustomRoleSpawnChances[CustomRoles.EngineerEndKnot])
            .SetValueFormat(OptionFormat.Seconds);

        EngineerDur = new FloatOptionItem(5003, "MaxInVentTime", new(0f, 250f, 1f), 15f, Tab)
            .SetParent(CustomRoleSpawnChances[CustomRoles.EngineerEndKnot])
            .SetValueFormat(OptionFormat.Seconds);

        SetupRoleOptions(5040, Tab, CustomRoles.NoisemakerEndKnot);

        NoiseMakerImpostorAlert = new BooleanOptionItem(5042, "NoisemakerImpostorAlert", false, Tab)
            .SetParent(CustomRoleSpawnChances[CustomRoles.NoisemakerEndKnot]);

        NoisemakerAlertDuration = new FloatOptionItem(5043, "NoisemakerAlertDuration", new(0f, 250f, 1f), 5f, Tab)
            .SetParent(CustomRoleSpawnChances[CustomRoles.NoisemakerEndKnot])
            .SetValueFormat(OptionFormat.Seconds);

        SetupRoleOptions(5100, Tab, CustomRoles.ScientistEndKnot);

        ScientistCD = new FloatOptionItem(5102, "VitalsCooldown", new(0f, 250f, 1f), 3f, Tab)
            .SetParent(CustomRoleSpawnChances[CustomRoles.ScientistEndKnot])
            .SetValueFormat(OptionFormat.Seconds);

        ScientistDur = new FloatOptionItem(5103, "VitalsDuration", new(0f, 250f, 1f), 15f, Tab)
            .SetParent(CustomRoleSpawnChances[CustomRoles.ScientistEndKnot])
            .SetValueFormat(OptionFormat.Seconds);

        SetupRoleOptions(5060, Tab, CustomRoles.TrackerEndKnot);

        TrackerCooldown = new FloatOptionItem(5062, "TrackerCooldown", new(0f, 250f, 1f), 25f, Tab)
            .SetParent(CustomRoleSpawnChances[CustomRoles.TrackerEndKnot])
            .SetValueFormat(OptionFormat.Seconds);

        TrackerDuration = new FloatOptionItem(5063, "TrackerDuration", new(0f, 250f, 1f), 20f, Tab)
            .SetParent(CustomRoleSpawnChances[CustomRoles.TrackerEndKnot])
            .SetValueFormat(OptionFormat.Seconds);

        TrackerDelay = new FloatOptionItem(5064, "TrackerDelay", new(0f, 250f, 1f), 5f, Tab)
            .SetParent(CustomRoleSpawnChances[CustomRoles.TrackerEndKnot])
            .SetValueFormat(OptionFormat.Seconds);
        
        SetupRoleOptions(5080, Tab, CustomRoles.DetectiveEndKnot);
        
        DetectiveSuspectLimit = new FloatOptionItem(5082, "DetectiveSuspectLimit", new(0f, 30f, 1f), 4f, Tab)
            .SetParent(CustomRoleSpawnChances[CustomRoles.DetectiveEndKnot])
            .SetValueFormat(OptionFormat.Players);
    }
}