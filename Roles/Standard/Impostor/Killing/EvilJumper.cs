using System.Collections.Generic;
using AmongUs.GameOptions;
using EndKnot.Patches;
using UnityEngine;

namespace EndKnot.Roles;

public class EvilJumper : RoleBase
{
    private const int Id = 699600;
    private static List<byte> PlayerIdList = [];

    // junpdis table from TOHK: JumpDistance option key → kill radius per hop
    private static readonly Dictionary<int, float> JumpRangeTable = new()
    {
        { 1, 1.22f },
        { 2, 1.82f },
        { 3, 2.1f },
        { 4, 2.7f },
        { 5, 2.9f }
    };

    private static OptionItem KillCooldown;
    private static OptionItem JumpCount;
    private static OptionItem JumpRange;
    private static OptionItem OneCoolTime;
    private static OptionItem JumpCoolTime;
    private static OptionItem JumpInterval;

    private byte EvilJumperId;
    private Vector2? JumpToPosition;
    private Vector2? UsePosition;
    private bool Jumping;
    private float Timer;
    private int NowJumpCount;
    private float JumpX;
    private float JumpY;
    private float SavedSpeed;
    private EvilJumperMark Mark;

    public override bool IsEnable => PlayerIdList.Count > 0;

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.ImpostorRoles, CustomRoles.EvilJumper);

        KillCooldown = new FloatOptionItem(Id + 10, "KillCooldown", new(0f, 180f, 0.5f), 30f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.EvilJumper])
            .SetValueFormat(OptionFormat.Seconds);

        JumpCount = new IntegerOptionItem(Id + 11, "EvilJumperJumpCount", new(1, 30, 1), 4, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.EvilJumper])
            .SetValueFormat(OptionFormat.Times);

        JumpRange = new IntegerOptionItem(Id + 12, "EvilJumperJumpRange", new(1, 5, 1), 1, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.EvilJumper]);

        OneCoolTime = new FloatOptionItem(Id + 13, "EvilJumperOneCoolTime", new(0f, 180f, 0.5f), 15f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.EvilJumper])
            .SetValueFormat(OptionFormat.Seconds);

        JumpCoolTime = new FloatOptionItem(Id + 14, "EvilJumperJumpCoolTime", new(0f, 180f, 0.5f), 25f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.EvilJumper])
            .SetValueFormat(OptionFormat.Seconds);

        JumpInterval = new FloatOptionItem(Id + 15, "EvilJumperJumpInterval", new(0.2f, 3f, 0.1f), 1.5f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.EvilJumper])
            .SetValueFormat(OptionFormat.Seconds);
    }

    public override void Init()
    {
        PlayerIdList = [];
    }

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);
        EvilJumperId = playerId;
        ResetJump();
        SavedSpeed = Main.AllPlayerSpeed[playerId];
    }

    public override void Remove(byte playerId)
    {
        PlayerIdList.Remove(playerId);
        Mark?.Despawn();
        Mark = null;
    }

    public override void SetKillCooldown(byte id)
    {
        Main.AllPlayerKillCooldown[id] = KillCooldown.GetFloat();
    }

    public override bool CanUseKillButton(PlayerControl pc)
    {
        return base.CanUseKillButton(pc) && !Jumping;
    }

    public override bool CanUseVent(PlayerControl pc, int ventId)
    {
        return !Jumping;
    }

    public override void ApplyGameOptions(IGameOptions opt, byte playerId)
    {
        float cd = JumpToPosition.HasValue ? JumpCoolTime.GetFloat() : OneCoolTime.GetFloat();

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
        string key = JumpToPosition.HasValue ? "EvilJumperJump" : "EvilJumperSetJumpPos";
        string text = Translator.GetString(key);

        if (Options.UsePets.GetBool() && !Options.UsePhantomBasis.GetBool())
            hud.PetButton?.OverrideText(text);
        else
            hud.AbilityButton?.OverrideText(text);
    }

    public override void OnPet(PlayerControl pc)
    {
        OnAbility(pc);
    }

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
        Logger.Info($"OnAbility entry: Jumping={Jumping}, JumpToPosition.HasValue={JumpToPosition.HasValue}, pos={pc.Pos()}", "EvilJumper");

        if (Jumping) return;

        if (!JumpToPosition.HasValue)
        {
            JumpToPosition = pc.Pos();
            Logger.Info($"Set branch: saved JumpToPosition={JumpToPosition.Value}", "EvilJumper");
            pc.SyncSettings();
            pc.RpcResetAbilityCooldown();
            Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc);
            return;
        }

        UsePosition = pc.Pos();
        Timer = 0f;
        NowJumpCount = 1;
        Jumping = true;
        Logger.Info($"Jump branch: UsePosition={UsePosition.Value}, JumpToPosition={JumpToPosition.Value}", "EvilJumper");

        int count = JumpCount.GetInt();
        JumpX = (JumpToPosition.Value.x - UsePosition.Value.x) / count;
        JumpY = (JumpToPosition.Value.y - UsePosition.Value.y) / count;

        SavedSpeed = Main.AllPlayerSpeed[pc.PlayerId];
        Main.AllPlayerSpeed[pc.PlayerId] = Main.MinSpeed;
        pc.MarkDirtySettings();
        Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc);
    }

    public override void OnFixedUpdate(PlayerControl pc)
    {
        if (!AmongUsClient.Instance.AmHost) return;
        if (!Jumping || !pc.IsAlive() || GameStates.IsMeeting) return;

        Timer += Time.fixedDeltaTime;
        if (Timer <= JumpInterval.GetFloat()) return;

        int totalHops = JumpCount.GetInt();
        Vector2 nextPos = NowJumpCount == totalHops
            ? JumpToPosition.Value
            : new Vector2(UsePosition.Value.x + JumpX * NowJumpCount, UsePosition.Value.y + JumpY * NowJumpCount);

        pc.TP(nextPos, log: false);

        if (Mark == null)
            Mark = new EvilJumperMark(nextPos, JumpRange.GetInt());
        else
            Mark.TP(nextPos);

        LateTask.New(() => RangeKill(pc), 0.01f, "EvilJumper.RangeKill");

        if (NowJumpCount >= totalHops)
            EndJump(pc);

        NowJumpCount++;
        Timer = 0f;
    }

    private void RangeKill(PlayerControl pc)
    {
        if (GameStates.IsMeeting || !pc.IsAlive()) return;

        float range = JumpRangeTable.TryGetValue(JumpRange.GetInt(), out float r) ? r : 0f;
        if (range <= 0f) return;

        foreach (PlayerControl tg in Main.AllAlivePlayerControls)
        {
            if (tg.PlayerId == pc.PlayerId) continue;
            if (Pelican.IsEaten(tg.PlayerId) || Medic.ProtectList.Contains(tg.PlayerId) || tg.Is(CustomRoles.Pestilence)) continue;
            if (!FastVector2.DistanceWithinRange(pc.Pos(), tg.Pos(), range)) continue;

            if (!tg.IsModdedClient()) tg.KillFlash();
            tg.SetRealKiller(pc);
            tg.Suicide(PlayerState.DeathReason.Bombed, pc);
        }
    }

    private void EndJump(PlayerControl pc)
    {
        Jumping = false;
        JumpToPosition = null;
        UsePosition = null;
        Main.AllPlayerSpeed[pc.PlayerId] = SavedSpeed;
        pc.SyncSettings();

        Mark?.Despawn();
        Mark = null;

        LateTask.New(() =>
        {
            pc.RpcResetAbilityCooldown();
            Main.AllPlayerKillCooldown[pc.PlayerId] = KillCooldown.GetFloat();
            pc.SetKillCooldown();
            Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc);
        }, 0.2f, "EvilJumper.EndJump");
    }

    public override void OnReportDeadBody()
    {
        PlayerControl pc = Utils.GetPlayerById(EvilJumperId);
        if (pc != null && Jumping)
        {
            Main.AllPlayerSpeed[pc.PlayerId] = SavedSpeed;
            pc.MarkDirtySettings();
        }

        ResetJump();
    }

    private void ResetJump()
    {
        JumpToPosition = null;
        UsePosition = null;
        Jumping = false;
        Timer = 0f;
        NowJumpCount = 0;
        Mark?.Despawn();
        Mark = null;
    }

    public override string GetProgressText(byte playerId, bool comms)
    {
        if (playerId != EvilJumperId) return string.Empty;

        string key = JumpToPosition.HasValue ? "EvilJumperJump" : "EvilJumperSetJumpPos";
        return Utils.ColorString(Palette.ImpostorRed, $" {Translator.GetString(key)}");
    }
}
