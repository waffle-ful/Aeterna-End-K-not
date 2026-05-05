using System.Collections.Generic;
using AmongUs.GameOptions;
using EndKnot.Patches;
using UnityEngine;
using static EndKnot.Translator;

namespace EndKnot.Roles;

public class EvilMaker : RoleBase
{
    private const int Id = 701100;
    private static List<byte> PlayerIdList = [];

    private static OptionItem KillCooldown;
    public static OptionItem AbilityCooldown;

    private byte EvilMakerId;
    private bool Used;

    public override bool IsEnable => PlayerIdList.Count > 0;

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.ImpostorRoles, CustomRoles.EvilMaker);

        KillCooldown = new FloatOptionItem(Id + 10, "KillCooldown", new(0f, 180f, 0.5f), 20f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.EvilMaker])
            .SetValueFormat(OptionFormat.Seconds);

        AbilityCooldown = new FloatOptionItem(Id + 11, "AbilityCooldown", new(0f, 180f, 0.5f), 20f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.EvilMaker])
            .SetValueFormat(OptionFormat.Seconds);
    }

    public override void Init()
    {
        PlayerIdList = [];
    }

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);
        EvilMakerId = playerId;
        Used = false;
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
        float cd = Used ? 200f : AbilityCooldown.GetFloat();
        if (!Used && IntroCutsceneDestroyPatch.PreventKill) cd = Mathf.Max(cd, 12f);

        if (Options.UsePhantomBasis.GetBool())
            AURoleOptions.PhantomCooldown = cd;
        else if (!Options.UsePets.GetBool())
        {
            AURoleOptions.ShapeshifterCooldown = cd;
            AURoleOptions.ShapeshifterDuration = 1f;
        }
    }

    public override void SetButtonTexts(HudManager hud, byte id)
    {
        string text = GetString("EvilMakerAbilityButton");
        if (Options.UsePets.GetBool() && !Options.UsePhantomBasis.GetBool())
            hud.PetButton?.OverrideText(text);
        else
            hud.AbilityButton?.OverrideText(text);
    }

    public override void OnPet(PlayerControl pc) => OnAbility(pc);

    public override bool OnShapeshift(PlayerControl shapeshifter, PlayerControl target, bool shapeshifting)
    {
        if (!shapeshifting) return true;
        OnAbility(shapeshifter);
        return false;
    }

    public override bool OnVanish(PlayerControl pc)
    {
        OnAbility(pc);
        return false;
    }

    private void OnAbility(PlayerControl pc)
    {
        if (Used || !pc.IsAlive()) return;

        List<PlayerControl> targets = pc.GetPlayersInAbilityRangeSorted(p =>
            p.IsCrewmate() && !p.Is(CustomRoles.Madmate) && !p.GetCustomRole().IsImpostor());

        if (targets.Count == 0)
        {
            pc.Notify(Utils.ColorString(Utils.GetRoleColor(CustomRoles.EvilMaker), GetString("EvilMakerNoTarget")));
            return;
        }

        PlayerControl convTarget = targets[0];
        Used = true;

        convTarget.RpcSetCustomRole(CustomRoles.Madmate);

        var sender = CustomRpcSender.Create("EvilMaker.OnAbility", Hazel.SendOption.Reliable);
        bool hasValue = false;

        pc.ResetKillCooldown();
        hasValue |= sender.Notify(pc, Utils.ColorString(Utils.GetRoleColor(CustomRoles.Madmate), GetString("EvilMakerConverted")), setName: false);
        hasValue |= sender.SyncSettings(pc);
        hasValue |= sender.NotifyRolesSpecific(pc, convTarget, out sender, out bool cleared);
        if (cleared) hasValue = false;

        hasValue |= sender.Notify(convTarget, Utils.ColorString(Utils.GetRoleColor(CustomRoles.Madmate), GetString("EvilMakerBeConverted")), setName: false);
        hasValue |= sender.RpcGuardAndKill(convTarget, pc);
        hasValue |= sender.RpcGuardAndKill(convTarget, convTarget);
        hasValue |= sender.NotifyRolesSpecific(convTarget, pc, out sender, out cleared);
        if (cleared) hasValue = false;

        sender.SendMessage(!hasValue);

        Logger.Info($"EvilMaker {pc.Data?.PlayerName} converted {convTarget.Data?.PlayerName} to Madmate", "EvilMaker");
    }

    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud = false, bool meeting = false)
    {
        if (seer.PlayerId != EvilMakerId || seer.PlayerId != target.PlayerId) return string.Empty;
        if (!seer.IsAlive() || meeting || !Used) return string.Empty;
        return Utils.ColorString(Color.gray, GetString("EvilMakerUsed"));
    }
}
