using System.Collections.Generic;
using AmongUs.GameOptions;
using EndKnot.Patches;
using UnityEngine;

namespace EndKnot.Roles;

public class CharismaStar : RoleBase
{
    private const int Id = 700700;
    private static List<byte> PlayerIdList = [];

    private static OptionItem KillCooldown;
    public static OptionItem GatherCooldown;
    private static OptionItem GatherMaxCount;
    private static OptionItem NotGatherPlayerKill;
    private static OptionItem CanAllPlayerGather;

    private static readonly Vector2 LiftPosition = new(7.76f, 8.56f);

    private byte CharismaStarId;
    private HashSet<byte> GatherChoosePlayers;
    private int GatherLimitCount;

    public override bool IsEnable => PlayerIdList.Count > 0;

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.ImpostorRoles, CustomRoles.CharismaStar);

        KillCooldown = new FloatOptionItem(Id + 10, "KillCooldown", new(2.5f, 180f, 0.5f), 30f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.CharismaStar])
            .SetValueFormat(OptionFormat.Seconds);

        GatherCooldown = new FloatOptionItem(Id + 11, "CharismaStarGatherCooldown", new(2.5f, 180f, 2.5f), 30f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.CharismaStar])
            .SetValueFormat(OptionFormat.Seconds);

        GatherMaxCount = new IntegerOptionItem(Id + 12, "CharismaStarGatherMaxCount", new(1, 10, 1), 3, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.CharismaStar])
            .SetValueFormat(OptionFormat.Times);

        NotGatherPlayerKill = new BooleanOptionItem(Id + 13, "CharismaStarNotGatherPlayerKill", true, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.CharismaStar]);

        CanAllPlayerGather = new BooleanOptionItem(Id + 14, "CharismaStarCanAllPlayerGather", true, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.CharismaStar]);
    }

    public override void Init()
    {
        PlayerIdList = [];
    }

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);
        CharismaStarId = playerId;
        GatherLimitCount = GatherMaxCount.GetInt();
        GatherChoosePlayers = [];
    }

    public override void Remove(byte playerId)
    {
        PlayerIdList.Remove(playerId);
    }

    public override void ApplyGameOptions(IGameOptions opt, byte playerId)
    {
        float cd = GatherLimitCount > 0 ? GatherCooldown.GetFloat() : 200f;
        if (IntroCutsceneDestroyPatch.PreventKill) cd = Mathf.Max(cd, 12f);

        if (Options.UsePhantomBasis.GetBool())
            AURoleOptions.PhantomCooldown = cd;
        else if (!Options.UsePets.GetBool())
        {
            AURoleOptions.ShapeshifterCooldown = cd;
            AURoleOptions.ShapeshifterDuration = 1f;
        }

        AURoleOptions.KillCooldown = KillCooldown.GetFloat();
    }

    public override void SetButtonTexts(HudManager hud, byte id)
    {
        string text = Translator.GetString("CharismaStarGatherButtonText");
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
        if (GatherLimitCount <= 0) return;

        if (GatherChoosePlayers.Count == 0)
        {
            if (!CanAllPlayerGather.GetBool()) return;
            foreach (PlayerControl p in Main.AllAlivePlayerControls)
                GatherChoosePlayers.Add(p.PlayerId);
        }
        else
        {
            GatherChoosePlayers.Add(pc.PlayerId);
        }

        Vent nearestVent = pc.GetClosestVent();
        if (nearestVent == null) return;

        foreach (byte targetId in GatherChoosePlayers)
        {
            PlayerControl t = Utils.GetPlayerById(targetId);
            if (t == null || !t.IsAlive()) continue;

            bool onLadder = t.MyPhysics.Animations.IsPlayingAnyLadderAnimation();
            bool onLift = Main.CurrentMap == MapNames.Airship && Vector2.Distance(t.Pos(), LiftPosition) <= 1.9f;

            if ((onLadder || onLift) && !t.Is(Team.Impostor))
            {
                if (NotGatherPlayerKill.GetBool())
                {
                    t.SetRealKiller(pc);
                    t.Suicide(PlayerState.DeathReason.Fall, pc);
                    pc.KillFlash();
                }
                continue;
            }

            t.MyPhysics.RpcExitVent(nearestVent.Id);
        }

        GatherChoosePlayers.Clear();
        GatherLimitCount--;
        pc.SyncSettings();
        pc.RpcResetAbilityCooldown();
        LateTask.New(() => { Main.AllPlayerKillCooldown[pc.PlayerId] = 0.1f; pc.SetKillCooldown(); }, 0.2f, "CharismaStar.GatherKCD");
        Utils.NotifyRoles(SpecifySeer: pc);
    }

    public override bool OnCheckMurder(PlayerControl killer, PlayerControl target)
    {
        if (GatherLimitCount <= 0) return true;
        if (GatherChoosePlayers.Contains(target.PlayerId)) return true;
        GatherChoosePlayers.Add(target.PlayerId);
        LateTask.New(() => Utils.NotifyRoles(SpecifySeer: killer), 0.2f, "CharismaStar.MarkNotify");
        killer.SetKillCooldown();
        return false;
    }

    public override void OnMurder(PlayerControl killer, PlayerControl target)
    {
        killer.RpcResetAbilityCooldown();
        LateTask.New(() => { Main.AllPlayerKillCooldown[killer.PlayerId] = KillCooldown.GetFloat(); killer.SyncSettings(); }, 0.2f, "CharismaStar.KillKCD");
    }

    public override void OnReportDeadBody()
    {
        GatherChoosePlayers?.Clear();
    }

    public override string GetProgressText(byte playerId, bool comms)
    {
        if (playerId != CharismaStarId) return string.Empty;
        Color color = GatherLimitCount > 0 ? Palette.ImpostorRed : Color.gray;
        return Utils.ColorString(color, $"[{GatherLimitCount}]");
    }

    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud, bool meeting)
    {
        if (meeting) return string.Empty;
        if (seer.PlayerId != CharismaStarId) return string.Empty;
        if (!GatherChoosePlayers.Contains(target.PlayerId)) return string.Empty;
        return Utils.ColorString(Palette.ImpostorRed, "◎");
    }
}
