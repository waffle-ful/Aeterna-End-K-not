using System.Collections.Generic;
using System.Linq;
using AmongUs.GameOptions;
using EndKnot.Modules;
using EndKnot.Patches;
using UnityEngine;

namespace EndKnot.Roles;

public class EvilBomber : RoleBase
{
    private const int Id = 700000;
    private static List<byte> PlayerIdList = [];

    private static OptionItem BombMaxCount;
    private static OptionItem AbilityCooldown;
    private static OptionItem BombKillDelay;
    private static OptionItem BlastRange;

    private byte EvilBomberId;
    private Dictionary<byte, float> PendingExplosions = new();
    private int BombCount;

    public override bool IsEnable => PlayerIdList.Count > 0;

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.ImpostorRoles, CustomRoles.EvilBomber);

        BombMaxCount = new IntegerOptionItem(Id + 10, "EvilBomberBombCount", new(1, 99, 1), 2, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.EvilBomber])
            .SetValueFormat(OptionFormat.Times);

        AbilityCooldown = new FloatOptionItem(Id + 11, "AbilityCooldown", new(0f, 999f, 0.5f), 15f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.EvilBomber])
            .SetValueFormat(OptionFormat.Seconds);

        BombKillDelay = new FloatOptionItem(Id + 12, "EvilBomberKillDelay", new(1f, 1000f, 1f), 10f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.EvilBomber])
            .SetValueFormat(OptionFormat.Seconds);

        BlastRange = new FloatOptionItem(Id + 13, "EvilBomberBlastRange", new(0.5f, 30f, 0.5f), 1f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.EvilBomber])
            .SetValueFormat(OptionFormat.Multiplier);
    }

    public override void Init()
    {
        PlayerIdList = [];
    }

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);
        EvilBomberId = playerId;
        PendingExplosions = new();
        BombCount = BombMaxCount.GetInt();
    }

    public override void Remove(byte playerId)
    {
        PlayerIdList.Remove(playerId);
    }

    public override void ApplyGameOptions(IGameOptions opt, byte playerId)
    {
        float cd = BombCount <= 0 ? 200f : AbilityCooldown.GetFloat();

        if (IntroCutsceneDestroyPatch.PreventKill)
            cd = Mathf.Max(cd, 12f);

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
        string text = Translator.GetString("EvilBomberAbility");
        if (Options.UsePets.GetBool() && !Options.UsePhantomBasis.GetBool())
            hud.PetButton?.OverrideText(text);
        else
            hud.AbilityButton?.OverrideText(text);
    }

    public override void OnPet(PlayerControl pc) => OnAbility(pc);

    public override bool OnShapeshift(PlayerControl shapeshifter, PlayerControl target, bool shapeshifting)
    {
        if (!shapeshifting) return true;
        shapeshifter.RpcRejectShapeshift();
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
        if (BombCount <= 0) return;

        PlayerControl nearestTarget = null;
        float minDist = float.MaxValue;

        foreach (PlayerControl p in Main.AllAlivePlayerControls)
        {
            if (p.PlayerId == pc.PlayerId) continue;
            if (PendingExplosions.ContainsKey(p.PlayerId)) continue;
            float d = Vector2.Distance(pc.Pos(), p.Pos());
            if (d >= minDist) continue;
            minDist = d;
            nearestTarget = p;
        }

        if (nearestTarget == null) return;

        BombCount--;
        PendingExplosions[nearestTarget.PlayerId] = 0f;
        pc.SyncSettings();
        pc.RpcResetAbilityCooldown();
        Utils.NotifyRoles(SpecifySeer: pc);
    }

    public override void OnFixedUpdate(PlayerControl pc)
    {
        if (!AmongUsClient.Instance.AmHost || !GameStates.IsInTask || !pc.IsAlive()) return;
        if (PendingExplosions.Count == 0) return;

        foreach ((byte targetId, float timer) in PendingExplosions.ToArray())
        {
            if (timer >= BombKillDelay.GetFloat())
            {
                PendingExplosions.Remove(targetId);
                PlayerControl target = Utils.GetPlayerById(targetId);
                if (target == null || !target.IsAlive()) continue;

                Explode(pc, target.Pos());
            }
            else
            {
                PendingExplosions[targetId] = timer + Time.fixedDeltaTime;
            }
        }
    }

    private void Explode(PlayerControl pc, Vector2 pos)
    {
        CustomSoundsManager.RPCPlayCustomSoundAll("Boom");
        float range = BlastRange.GetFloat();

        foreach (PlayerControl tg in Main.AllAlivePlayerControls)
        {
            if (tg.PlayerId == pc.PlayerId) continue;
            if (Pelican.IsEaten(tg.PlayerId) || Medic.ProtectList.Contains(tg.PlayerId) || tg.Is(CustomRoles.Pestilence)) continue;
            if (!FastVector2.DistanceWithinRange(pos, tg.Pos(), range)) continue;

            if (!tg.IsModdedClient()) tg.KillFlash();
            tg.SetRealKiller(pc);
            tg.Suicide(PlayerState.DeathReason.Bombed, pc);
        }
    }

    public override void OnReportDeadBody()
    {
        PendingExplosions.Clear();
    }

    public override string GetProgressText(byte playerId, bool comms)
    {
        if (playerId != EvilBomberId) return string.Empty;
        Color color = BombCount > 0 ? Palette.ImpostorRed : Color.gray;
        return Utils.ColorString(color, $"({BombCount})");
    }
}
