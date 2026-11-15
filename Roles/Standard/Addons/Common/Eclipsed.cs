using System.Collections.Generic;
using AmongUs.GameOptions;

namespace EndKnot.Roles;

// 最初は視界が狭く、キルするたびに広がっていく (インポスターとニュートラルキラー向け)
internal class Eclipsed : IAddon
{
    private static OptionItem StartVision;
    private static OptionItem VisionIncrease;
    private static OptionItem MaxVision;

    private static readonly Dictionary<byte, float> Vision = [];

    public AddonTypes Type => AddonTypes.Mixed;

    public void SetupCustomOption()
    {
        Options.SetupAdtRoleOptions(710600, CustomRoles.Eclipsed, canSetNum: true);

        StartVision = new FloatOptionItem(710610, "EclipsedStartVision", new(0.1f, 5f, 0.1f), 0.5f, TabGroup.Addons)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Eclipsed])
            .SetValueFormat(OptionFormat.Multiplier);

        VisionIncrease = new FloatOptionItem(710611, "EclipsedVisionIncrease", new(0.05f, 5f, 0.05f), 0.1f, TabGroup.Addons)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Eclipsed])
            .SetValueFormat(OptionFormat.Multiplier);

        MaxVision = new FloatOptionItem(710612, "EclipsedMaxVision", new(0.25f, 5f, 0.25f), 1.5f, TabGroup.Addons)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Eclipsed])
            .SetValueFormat(OptionFormat.Multiplier);
    }

    public static void Init()
    {
        Vision.Clear();
    }

    private static float Get(byte id) => Vision.TryGetValue(id, out float v) ? v : StartVision.GetFloat();

    public static void ApplyGameOptions(IGameOptions opt, byte id)
    {
        float v = Get(id);
        opt.SetVision(true);
        opt.SetFloat(FloatOptionNames.CrewLightMod, v);
        opt.SetFloat(FloatOptionNames.ImpostorLightMod, v);
    }

    public static void OnMurder(PlayerControl killer, PlayerControl target)
    {
        if (killer == target || !killer.Is(CustomRoles.Eclipsed)) return;

        float current = Get(killer.PlayerId);
        float next = current + VisionIncrease.GetFloat();
        if (next > MaxVision.GetFloat()) next = MaxVision.GetFloat();
        Vision[killer.PlayerId] = next;

        if (next != current) killer.MarkDirtySettings();
    }
}
