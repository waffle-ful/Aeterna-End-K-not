using static EndKnot.Options;

namespace EndKnot.Roles;

internal class NonReport : IAddon
{
    public static OptionItem Mode;
    public AddonTypes Type => AddonTypes.Harmful;

    private static readonly string[] Modes =
    [
        "NonReportMode.NotButton",
        "NonReportMode.NotReport",
        "NonReportMode.Both"
    ];

    public void SetupCustomOption()
    {
        SetupAdtRoleOptions(20180, CustomRoles.NonReport, canSetNum: true, teamSpawnOptions: true);

        Mode = new StringOptionItem(20190, "NonReportMode", Modes, 2, TabGroup.Addons)
            .SetParent(CustomRoleSpawnChances[CustomRoles.NonReport]);
    }

    public static bool BlocksButton => Mode.GetValue() is 0 or 2;
    public static bool BlocksReport => Mode.GetValue() is 1 or 2;
}
