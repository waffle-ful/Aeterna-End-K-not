using System.Collections.Generic;
using AmongUs.GameOptions;
using UnityEngine;

namespace EndKnot.Roles;

public class QuickKiller : RoleBase
{
    private const int Id = 699200;
    private static List<byte> PlayerIdList = [];

    private static OptionItem KillCooldown;
    private static OptionItem QuickKillTimer;
    private static OptionItem AbilityCanUsePlayerCount;

    // null = not in quick kill window
    private float? timer;

    public override bool IsEnable => PlayerIdList.Count > 0;

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.ImpostorRoles, CustomRoles.QuickKiller);

        KillCooldown = new FloatOptionItem(Id + 10, "KillCooldown", new(0f, 180f, 0.5f), 20f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.QuickKiller])
            .SetValueFormat(OptionFormat.Seconds);

        QuickKillTimer = new FloatOptionItem(Id + 11, "QuickKillerTimer", new(0.1f, 10f, 0.1f), 3f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.QuickKiller])
            .SetValueFormat(OptionFormat.Seconds);

        AbilityCanUsePlayerCount = new IntegerOptionItem(Id + 12, "QuickKillerCanuseplayercount", new(0, 15, 1), 6, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.QuickKiller])
            .SetValueFormat(OptionFormat.Players);
    }

    public override void Init()
    {
        PlayerIdList = [];
    }

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);
        timer = null;
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
        AURoleOptions.ShapeshifterCooldown = timer.HasValue ? timer.Value : 200f;
        AURoleOptions.ShapeshifterDuration = 1f;
    }

    public override bool OnShapeshift(PlayerControl shapeshifter, PlayerControl target, bool shapeshifting)
    {
        return false;
    }

    public override void OnMurder(PlayerControl killer, PlayerControl target)
    {
        if (AbilityCanUsePlayerCount.GetInt() > Main.AllAlivePlayerControls.Count) return;

        Main.AllPlayerKillCooldown[killer.PlayerId] = 0.0001f;

        if (timer.HasValue)
        {
            killer.SyncSettings();
            return;
        }

        timer = QuickKillTimer.GetFloat();
        killer.SyncSettings();
        killer.RpcResetAbilityCooldown();
    }

    public override void OnFixedUpdate(PlayerControl pc)
    {
        if (!pc.IsAlive() || timer == null || GameStates.IsMeeting) return;

        timer -= Time.fixedDeltaTime;

        if (timer < 0)
        {
            timer = null;
            Main.AllPlayerKillCooldown[pc.PlayerId] = KillCooldown.GetFloat();
            pc.SetKillCooldown();
            pc.RpcResetAbilityCooldown();
        }
    }

    public override void OnReportDeadBody()
    {
        timer = null;
    }

    public override void SetButtonTexts(HudManager hud, byte id)
    {
        if (!timer.HasValue) return;
        string text = Translator.GetString("QuickKillerAbility");
        if (Options.UsePets.GetBool() && !Options.UsePhantomBasis.GetBool())
            hud.PetButton?.OverrideText(text);
        else
            hud.AbilityButton?.OverrideText(text);
    }

    public override string GetProgressText(byte playerId, bool comms)
    {
        if (!timer.HasValue) return string.Empty;
        return Utils.ColorString(Palette.ImpostorRed.ShadeColor(0.5f), $"⚡{(int)timer.Value + 1}s");
    }
}
