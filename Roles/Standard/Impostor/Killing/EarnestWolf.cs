using System.Collections.Generic;
using AmongUs.GameOptions;

namespace EndKnot.Roles;

public class EarnestWolf : RoleBase
{
    private const int Id = 699400;
    private static List<byte> PlayerIdList = [];

    private static OptionItem KillCooldown;
    public static OptionItem OverKillCount;
    private static OptionItem OverKillBairitu;
    private static OptionItem NormalKillDistance;
    private static OptionItem OverKillDistance;
    private static OptionItem CantReportOpt;

    private bool OverKillMode;
    private float CurrentKillCooldown;
    private int KillsDoneInOverKill;
    private List<byte> OverKillVictims;
    private byte EarnestWolfId;

    public override bool IsEnable => PlayerIdList.Count > 0;

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.ImpostorRoles, CustomRoles.EarnestWolf);

        KillCooldown = new FloatOptionItem(Id + 10, "KillCooldown", new(0f, 180f, 0.5f), 25f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.EarnestWolf])
            .SetValueFormat(OptionFormat.Seconds);

        OverKillCount = new IntegerOptionItem(Id + 11, "EarnestWolfOverKillCount", new(0, 15, 1), 2, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.EarnestWolf])
            .SetValueFormat(OptionFormat.Times);

        OverKillBairitu = new FloatOptionItem(Id + 12, "EarnestWolfOverBairitu", new(0.25f, 10f, 0.01f), 1.05f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.EarnestWolf])
            .SetValueFormat(OptionFormat.Multiplier);

        NormalKillDistance = new IntegerOptionItem(Id + 13, "EarnestWolfNormalKillDistance", new(0, 2, 1), 0, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.EarnestWolf]);

        OverKillDistance = new IntegerOptionItem(Id + 14, "EarnestWolfOverKillDistance", new(0, 2, 1), 2, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.EarnestWolf]);

        CantReportOpt = new BooleanOptionItem(Id + 15, "EarnestWolfCantReport", false, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.EarnestWolf]);
    }

    public override void Init()
    {
        PlayerIdList = [];
    }

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);
        // count == 0 means infinite OverKill → start in OverKill mode
        OverKillMode = OverKillCount.GetInt() == 0;
        CurrentKillCooldown = KillCooldown.GetFloat();
        KillsDoneInOverKill = 0;
        OverKillVictims = [];
        EarnestWolfId = playerId;
    }

    public override void Remove(byte playerId)
    {
        PlayerIdList.Remove(playerId);
    }

    public override void SetKillCooldown(byte id)
    {
        Main.AllPlayerKillCooldown[id] = CurrentKillCooldown;
    }

    public override void ApplyGameOptions(IGameOptions opt, byte playerId)
    {
        opt.SetInt(Int32OptionNames.KillDistance,
            OverKillMode ? OverKillDistance.GetInt() : NormalKillDistance.GetInt());

        if (OverKillCount.GetInt() > 0)
        {
            AURoleOptions.ShapeshifterCooldown = 1f;
            AURoleOptions.ShapeshifterDuration = 1f;
        }
    }

    public override bool OnShapeshift(PlayerControl shapeshifter, PlayerControl target, bool shapeshifting)
    {
        if (!shapeshifting) return false;

        int maxCount = OverKillCount.GetInt();
        // If uses are depleted or infinite mode, toggle is meaningless
        if (maxCount > 0 && KillsDoneInOverKill >= maxCount)
        {
            OverKillMode = false;
        }
        else if (maxCount > 0)
        {
            OverKillMode = !OverKillMode;
        }

        LateTask.New(() =>
        {
            Utils.NotifyRoles(SpecifySeer: shapeshifter, SpecifyTarget: shapeshifter);
            shapeshifter.SyncSettings();
        }, 0.2f, "EarnestWolf.Toggle");

        return false;
    }

    public override bool OnCheckMurder(PlayerControl killer, PlayerControl target)
    {
        if (!OverKillMode) return true;

        KillsDoneInOverKill++;
        CurrentKillCooldown *= OverKillBairitu.GetFloat();
        OverKillVictims.Add(target.PlayerId);

        int maxCount = OverKillCount.GetInt();
        if (maxCount > 0 && KillsDoneInOverKill >= maxCount)
            OverKillMode = false;

        LateTask.New(() =>
        {
            Main.AllPlayerKillCooldown[killer.PlayerId] = CurrentKillCooldown;
            killer.SetKillCooldown();
            Utils.NotifyRoles(SpecifySeer: killer, SpecifyTarget: killer);
            killer.SyncSettings();
        }, 0.2f, "EarnestWolf.PostKill");

        return true;
    }

    public override bool CheckReportDeadBody(PlayerControl reporter, NetworkedPlayerInfo target, PlayerControl killer)
    {
        if (!CantReportOpt.GetBool()) return true;
        if (target == null) return true;
        return !OverKillVictims.Contains(target.PlayerId);
    }

    public override void OnReportDeadBody()
    {
        // reset OverKill CD multiplier each meeting but keep mode/count
        CurrentKillCooldown = KillCooldown.GetFloat();
    }

    public override string GetProgressText(byte playerId, bool comms)
    {
        if (playerId != EarnestWolfId) return string.Empty;

        int max = OverKillCount.GetInt();
        if (max == 0)
            return Utils.ColorString(Palette.ImpostorRed, "(∞)");

        int remaining = max - KillsDoneInOverKill;
        return Utils.ColorString(
            remaining > 0 ? Palette.ImpostorRed : Palette.DisabledGrey,
            $"({remaining})");
    }

    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud = false, bool meeting = false)
    {
        if (seer.PlayerId != EarnestWolfId || seer.PlayerId != target.PlayerId || meeting) return string.Empty;
        return OverKillMode ? "<color=#ff1919>◎</color>" : string.Empty;
    }

    public override void SetButtonTexts(HudManager hud, byte id)
    {
        hud.KillButton?.OverrideText(Translator.GetString(
            OverKillMode ? "EarnestWolfKillButtonOverKill" : "EarnestWolfKillButtonNormal"));
    }
}
