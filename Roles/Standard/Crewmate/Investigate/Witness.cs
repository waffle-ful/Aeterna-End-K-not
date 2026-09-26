using System.Collections.Generic;
using AmongUs.GameOptions;
using static EndKnot.Options;

namespace EndKnot.Roles;

internal class Witness : RoleBase
{
    public static HashSet<byte> AllKillers = [];

    // 偽証で付けた⚠は本物のキルとは別に持つ (同じ相手でも片方の期限切れがもう片方を消さないように)。
    private static HashSet<byte> FalseKillers = [];

    public static bool On;
    public override bool IsEnable => On;

    public override void SetupCustomOption()
    {
        SetupRoleOptions(8550, TabGroup.CrewmateRoles, CustomRoles.Witness);

        WitnessCD = new FloatOptionItem(8552, "AbilityCD", new(0f, 60f, 0.5f), 10f, TabGroup.CrewmateRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Witness])
            .SetValueFormat(OptionFormat.Seconds);

        WitnessTime = new IntegerOptionItem(8553, "WitnessTime", new(0, 90, 1), 10, TabGroup.CrewmateRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Witness])
            .SetValueFormat(OptionFormat.Seconds);

        WitnessUsePet = CreatePetUseSetting(8554, CustomRoles.Witness);
    }

    public override void Add(byte playerId)
    {
        On = true;
    }

    public override void Init()
    {
        On = false;
        AllKillers = [];
        FalseKillers = [];
    }

    public override void SetKillCooldown(byte id)
    {
        Main.AllPlayerKillCooldown[id] = WitnessCD.GetFloat();
    }

    public override void ApplyGameOptions(IGameOptions opt, byte playerId)
    {
        opt.SetVision(false);
    }

    public override void SetButtonTexts(HudManager hud, byte id)
    {
        hud.KillButton?.OverrideText(Translator.GetString("WitnessButtonText"));
    }

    public override bool CanUseKillButton(PlayerControl pc)
    {
        return pc.IsAlive();
    }

    public override bool OnCheckMurder(PlayerControl killer, PlayerControl target)
    {
        killer.SetKillCooldown();
        killer.Notify($"<size=3><#{(AllKillers.Contains(target.PlayerId) || FalseKillers.Contains(target.PlayerId) ? "ffff00>⚠" : "00ff00>✓")}</color></size>");

        // マッドメイトの証人は偽証を行う: 押した相手を実際の殺害の有無に関わらず
        // WitnessTime 秒だけ⚠が付くようにし、クルー側の証人を欺く。
        if (killer.Is(CustomRoles.Madmate))
        {
            FalseKillers.Add(target.PlayerId);
            byte falseTargetId = target.PlayerId;
            LateTask.New(() => FalseKillers.Remove(falseTargetId), WitnessTime.GetInt(), log: false);
        }

        return false;
    }

    public override void OnReportDeadBody()
    {
        AllKillers.Clear();
        FalseKillers.Clear();
    }
}