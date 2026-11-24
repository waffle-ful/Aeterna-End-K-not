using static EndKnot.Options;

namespace EndKnot.Roles;

internal class Torch : IAddon
{
    public AddonTypes Type => AddonTypes.Helpful;

    public void SetupCustomOption()
    {
        SetupAdtRoleOptions(14200, CustomRoles.Torch, canSetNum: true, teamSpawnOptions: true);

        TorchVision = new FloatOptionItem(14210, "TorchVision", new(0.25f, 5f, 0.05f), 1.25f, TabGroup.Addons)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Torch])
            .SetValueFormat(OptionFormat.Multiplier);

        TorchAffectedByLights = new BooleanOptionItem(14220, "TorchAffectedByLights", false, TabGroup.Addons)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Torch]);

        // トーチの人の視界を本編のかくれんぼの懐中電灯にする (Modules/Flashlight)
        TorchFlashlight = new BooleanOptionItem(14230, "TorchFlashlight", false, TabGroup.Addons)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Torch]);

        TorchFlashlightSize = new FloatOptionItem(14231, "TorchFlashlightSize", new(0.1f, 1.5f, 0.05f), 0.5f, TabGroup.Addons)
            .SetParent(TorchFlashlight)
            .SetValueFormat(OptionFormat.Multiplier);
    }
}
