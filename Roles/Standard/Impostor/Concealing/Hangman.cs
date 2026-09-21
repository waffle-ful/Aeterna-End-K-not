using System.Collections.Generic;
using AmongUs.GameOptions;
using EndKnot.Modules;
using static EndKnot.Options;

namespace EndKnot.Roles;

public class Hangman : RoleBase
{
    private const int Id = 1400;
    private static List<byte> PlayerIdList = [];

    private static OptionItem ShapeshiftCooldown;
    public static OptionItem ShapeshiftDuration;
    private static OptionItem KCD;
    private static OptionItem HangmanLimitOpt;
    public static OptionItem HangmanAbilityUseGainWithEachKill;

    public override bool IsEnable => PlayerIdList.Count > 0;

    public override void SetupCustomOption()
    {
        SetupRoleOptions(Id, TabGroup.ImpostorRoles, CustomRoles.Hangman);

        ShapeshiftCooldown = new FloatOptionItem(Id + 2, "ShapeshiftCooldown", new(1f, 60f, 1f), 30f, TabGroup.ImpostorRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Hangman])
            .SetValueFormat(OptionFormat.Seconds);

        ShapeshiftDuration = new FloatOptionItem(Id + 3, "ShapeshiftDuration", new(1f, 30f, 1f), 10f, TabGroup.ImpostorRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Hangman])
            .SetValueFormat(OptionFormat.Seconds);

        KCD = new FloatOptionItem(Id + 4, "KillCooldownOnStrangle", new(1f, 90f, 1f), 40f, TabGroup.ImpostorRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Hangman])
            .SetValueFormat(OptionFormat.Seconds);

        HangmanLimitOpt = new IntegerOptionItem(Id + 5, "AbilityUseLimit", new(0, 20, 1), 0, TabGroup.ImpostorRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Hangman])
            .SetValueFormat(OptionFormat.Times);

        HangmanAbilityUseGainWithEachKill = new FloatOptionItem(Id + 6, "AbilityUseGainWithEachKill", new(0f, 5f, 0.1f), 0.5f, TabGroup.ImpostorRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Hangman])
            .SetValueFormat(OptionFormat.Times);
    }

    public override void Init()
    {
        PlayerIdList = [];
    }

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);
        playerId.SetAbilityUseLimit(HangmanLimitOpt.GetFloat());
    }

    public override void Remove(byte playerId)
    {
        PlayerIdList.Remove(playerId);
    }

    public override void ApplyGameOptions(IGameOptions opt, byte id)
    {
        AURoleOptions.ShapeshifterCooldown = ShapeshiftCooldown.GetFloat();
        AURoleOptions.ShapeshifterDuration = ShapeshiftDuration.GetFloat();
    }

    /// <summary>
    ///     変身中の処刑だけ強力 (Lv2)。メディック・警戒中ベテラン・Pestilence (いずれも Lv2 以上) で止まり、
    ///     消費型・確率型 (Lv1) は貫く — 手書き3チェックだった頃と同じ力関係 (spec §8-3)。
    /// </summary>
    public override int GetAttackPower(PlayerControl killer, AttackKind kind)
    {
        if (kind == AttackKind.Execution) return killer.IsShifted() ? AttackDefense.Powerful : AttackDefense.Basic;

        return base.GetAttackPower(killer, kind);
    }

    public override bool OnCheckMurder(PlayerControl killer, PlayerControl target)
    {
        if (Medic.ProtectList.Contains(target.PlayerId)) return false;

        if (target.Is(CustomRoles.Madmate) && !ImpCanKillMadmate.GetBool()) return false;

        if (killer.GetAbilityUseLimit() < 1 && killer.IsShifted()) return false;

        if (killer.IsShifted())
        {
            // Pestilence は関所へ入れると撃った側を殺し返すので、従来どおり手前で無言の空振りにする。
            if (target.Is(CustomRoles.Pestilence)) return false;

            // 残りの手書き (警戒中ベテラン / メディック) は、
            // 攻撃レベル (変身中 = 強力 Lv2) に置き換わって関所がまとめて判定する。
            if (!CheckMurderPatch.PassesGate(killer, target, kind: AttackKind.Execution)) return false;

            RPC.PlaySoundRPC(killer.PlayerId, Sounds.KillSound);
            killer.RpcRemoveAbilityUse();
            target.SetRealKiller(killer);
            Main.PlayerStates[target.PlayerId].deathReason = PlayerState.DeathReason.LossOfHead;
            target.RpcExileV2();
            target.Data.IsDead = true;
            Main.PlayerStates[target.PlayerId].SetDead();
            Utils.AfterPlayerDeathTasks(target);
            target.SetRealKiller(killer);
            killer.SetKillCooldown(KCD.GetFloat());
            return false;
        }

        return true;
    }

    public override bool OnShapeshift(PlayerControl shapeshifter, PlayerControl target, bool shapeshifting)
    {
        if (shapeshifter.GetAbilityUseLimit() < 1 && shapeshifting)
        {
            shapeshifter.SetKillCooldown(ShapeshiftDuration.GetFloat() + 1f);
            return false;
        }
        
        return true;
    }

    public override void SetButtonTexts(HudManager hud, byte id)
    {
        if (id.IsPlayerShifted()) hud.KillButton?.OverrideText(Translator.GetString("HangmanKillButtonTextDuringSS"));
    }
}
