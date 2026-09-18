using System.Collections.Generic;
using AmongUs.GameOptions;
using UnityEngine;

namespace EndKnot.Roles;

public class ProgressKiller : RoleBase
{
    private const int Id = 700500;
    private static List<byte> PlayerIdList = [];

    private static OptionItem KillCooldown;
    private static OptionItem ProgressKillerMadseen;
    private static OptionItem ProgressWorkhorseseen;

    private byte ProgressKillerId;
    private HashSet<byte> NotifiedFinished = [];

    public override bool IsEnable => PlayerIdList.Count > 0;

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.ImpostorRoles, CustomRoles.ProgressKiller);

        KillCooldown = new FloatOptionItem(Id + 10, "KillCooldown", new(0f, 180f, 0.5f), 30f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.ProgressKiller])
            .SetValueFormat(OptionFormat.Seconds);

        ProgressKillerMadseen = new BooleanOptionItem(Id + 11, "ProgressKillerMadseen", true, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.ProgressKiller]);

        ProgressWorkhorseseen = new BooleanOptionItem(Id + 12, "ProgressWorkhorseseen", true, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.ProgressKiller]);
    }

    public override void Init()
    {
        PlayerIdList = [];
    }

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);
        ProgressKillerId = playerId;
        NotifiedFinished = [];
    }

    public override void Remove(byte playerId)
    {
        PlayerIdList.Remove(playerId);
    }

    public override void SetKillCooldown(byte id)
    {
        Main.AllPlayerKillCooldown[id] = KillCooldown.GetFloat();
    }

    // ☆/〇 は seer-target ペア指定でしか客に届かないので、対象がタスクを終えた瞬間に
    // 個別 NotifyRoles を送る (次の全体 NotifyRoles = 会議まで表示が更新されないのを防ぐ)。
    public override void OnFixedUpdate(PlayerControl pc)
    {
        if (!AmongUsClient.Instance.AmHost || !GameStates.IsInTask) return;
        if (pc.PlayerId != ProgressKillerId || !pc.IsAlive()) return;

        foreach (PlayerControl target in Main.AllAlivePlayerControlsToList)
        {
            if (target.PlayerId == ProgressKillerId) continue;
            if (NotifiedFinished.Contains(target.PlayerId)) continue;
            if (!target.GetTaskState().IsTaskFinished) continue;

            NotifiedFinished.Add(target.PlayerId);
            Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: target);
        }
    }

    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud = false, bool meeting = false)
    {
        if (seer.PlayerId != ProgressKillerId || seer.PlayerId == target.PlayerId) return string.Empty;
        if (!seer.IsAlive()) return string.Empty;

        TaskState taskState = target.GetTaskState();
        if (!taskState.IsTaskFinished) return string.Empty;

        bool isMadmate = target.IsMadmate();
        Color roleColor = Utils.GetRoleColor(CustomRoles.ProgressKiller);

        if (ProgressKillerMadseen.GetBool() && isMadmate)
            return Utils.ColorString(roleColor, "☆");
        if (ProgressWorkhorseseen.GetBool() && !isMadmate)
            return Utils.ColorString(roleColor, "〇");

        return string.Empty;
    }

    // Insider の味方能力マーク集約 (Utils.cs) から呼ばれる。マドメイト向け☆マークのみ (原典の Insider 内通経路と同じ)。
    public string GetInsiderMark(PlayerControl target)
    {
        if (target.PlayerId == ProgressKillerId) return string.Empty;
        if (!ProgressKillerMadseen.GetBool() || !target.IsMadmate()) return string.Empty;
        if (!target.GetTaskState().IsTaskFinished) return string.Empty;
        return Utils.ColorString(Utils.GetRoleColor(CustomRoles.ProgressKiller), "☆");
    }
}
