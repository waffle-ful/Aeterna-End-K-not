using System.Collections.Generic;
using AmongUs.GameOptions;
using UnityEngine;

namespace EndKnot.Roles;

public class Decrescendo : RoleBase
{
    private const int Id = 700900;
    private static List<byte> PlayerIdList = [];

    private static OptionItem DecKillCount;
    private static OptionItem NormalKillCooldown;
    private static OptionItem KillCooldownMultiplier;
    private static OptionItem MaxKillCooldown;
    private static OptionItem DecreaseVision;
    private static OptionItem VisionMultiplier;
    private static OptionItem MinVision;
    private static OptionItem CantUseVentWhenWeakened;

    private byte DecrescendoId;
    private bool Decrescending;
    private int KillCount;
    private float NowKillCool;
    private float NowVision;

    public override bool IsEnable => PlayerIdList.Count > 0;

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.ImpostorRoles, CustomRoles.Decrescendo);

        DecKillCount = new IntegerOptionItem(Id + 10, "DecrescendoDecKillCount", new(0, 15, 1), 3, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Decrescendo])
            .SetValueFormat(OptionFormat.Times);

        NormalKillCooldown = new FloatOptionItem(Id + 11, "KillCooldown", new(0f, 180f, 0.5f), 25f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Decrescendo])
            .SetValueFormat(OptionFormat.Seconds);

        KillCooldownMultiplier = new FloatOptionItem(Id + 12, "DecrescendoKillCoolMultiplier", new(1f, 2f, 0.05f), 1.25f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Decrescendo])
            .SetValueFormat(OptionFormat.Multiplier);

        MaxKillCooldown = new FloatOptionItem(Id + 13, "DecrescendoMaxKillCooldown", new(0f, 180f, 0.5f), 60f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Decrescendo])
            .SetValueFormat(OptionFormat.Seconds);

        DecreaseVision = new BooleanOptionItem(Id + 14, "DecrescendoDecreaseVision", true, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Decrescendo]);

        VisionMultiplier = new FloatOptionItem(Id + 15, "DecrescendoVisionMultiplier", new(0f, 1f, 0.05f), 0.75f, TabGroup.ImpostorRoles)
            .SetParent(DecreaseVision)
            .SetValueFormat(OptionFormat.Multiplier);

        MinVision = new FloatOptionItem(Id + 16, "DecrescendoMinVision", new(0f, 1f, 0.05f), 0.1f, TabGroup.ImpostorRoles)
            .SetParent(DecreaseVision)
            .SetValueFormat(OptionFormat.Multiplier);

        CantUseVentWhenWeakened = new BooleanOptionItem(Id + 17, "DecrescendoCantUseVent", true, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Decrescendo]);
    }

    public override void Init()
    {
        PlayerIdList = [];
    }

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);
        DecrescendoId = playerId;
        Decrescending = false;
        KillCount = 0;
        NowKillCool = NormalKillCooldown.GetFloat();
        NowVision = Main.DefaultImpostorVision;
    }

    public override void Remove(byte playerId)
    {
        PlayerIdList.Remove(playerId);
    }

    public override void SetKillCooldown(byte id)
    {
        Main.AllPlayerKillCooldown[id] = Decrescending ? NowKillCool : NormalKillCooldown.GetFloat();
    }

    public override void OnMurder(PlayerControl killer, PlayerControl target)
    {
        if (killer.PlayerId != DecrescendoId) return;

        KillCount++;
        if (DecKillCount.GetInt() > KillCount) return;

        Decrescending = true;
        NowKillCool = Mathf.Min(NowKillCool * KillCooldownMultiplier.GetFloat(), MaxKillCooldown.GetFloat());
        if (DecreaseVision.GetBool())
            NowVision = Mathf.Max(NowVision * VisionMultiplier.GetFloat(), MinVision.GetFloat());

        Main.AllPlayerKillCooldown[killer.PlayerId] = NowKillCool;
        LateTask.New(() =>
        {
            killer.SyncSettings();
        }, 0.3f, "Decrescendo SyncSettings");
    }

    public override void ApplyGameOptions(IGameOptions opt, byte playerId)
    {
        if (!Decrescending || !DecreaseVision.GetBool()) return;
        opt.SetFloat(FloatOptionNames.ImpostorLightMod, NowVision);
    }

    public override bool CanUseVent(PlayerControl pc, int ventId)
    {
        if (Decrescending && CantUseVentWhenWeakened.GetBool()) return false;
        return true;
    }

    public override string GetProgressText(byte playerId, bool comms)
    {
        if (playerId != DecrescendoId) return string.Empty;
        if (Decrescending) return Utils.ColorString(Color.gray, "(弱化中)");
        return Utils.ColorString(Palette.ImpostorRed, $"({KillCount}/{DecKillCount.GetInt()})");
    }
}
