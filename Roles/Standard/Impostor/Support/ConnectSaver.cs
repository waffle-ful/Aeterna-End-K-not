using System.Collections.Generic;
using System.Linq;
using AmongUs.GameOptions;
using UnityEngine;
using static EndKnot.Translator;

namespace EndKnot.Roles;

public class ConnectSaver : RoleBase
{
    private const int Id = 700400;
    public static List<byte> PlayerIdList = [];

    private static OptionItem OptionKillCoolDown;
    private static OptionItem OptionTageKillCoolDown;
    private static OptionItem OptionMax;
    private static OptionItem OptionMinimumPlayerCount;
    private static OptionItem OptionDeathReason;

    private static readonly PlayerState.DeathReason[] DeathReasonOptions =
    [
        PlayerState.DeathReason.Kill,
        PlayerState.DeathReason.Suicide,
        PlayerState.DeathReason.Revenge,
        PlayerState.DeathReason.FollowingSuicide
    ];

    private byte ConnectSaverId;
    private byte target1;
    private byte target2;
    private int usedcount;
    private bool IsUsing;
    private bool IsSelectingMode;

    public override bool IsEnable => PlayerIdList.Count > 0;

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.ImpostorRoles, CustomRoles.ConnectSaver);

        OptionKillCoolDown = new FloatOptionItem(Id + 10, "KillCooldown", new(0f, 180f, 0.5f), 30f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.ConnectSaver])
            .SetValueFormat(OptionFormat.Seconds);

        OptionTageKillCoolDown = new FloatOptionItem(Id + 11, "ConnectSaverTageKillCooldown", new(0f, 180f, 0.5f), 40f, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.ConnectSaver])
            .SetValueFormat(OptionFormat.Seconds);

        OptionMax = new IntegerOptionItem(Id + 12, "Maximum", new(1, 99, 1), 1, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.ConnectSaver])
            .SetValueFormat(OptionFormat.Times);

        OptionMinimumPlayerCount = new IntegerOptionItem(Id + 13, "ConnectSaverMinPlayerCount", new(0, 15, 1), 4, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.ConnectSaver])
            .SetValueFormat(OptionFormat.Players);

        OptionDeathReason = new StringOptionItem(Id + 14, "ConnectSaverDeathReason",
            DeathReasonOptions.Select(x => x.ToString()).ToArray(), 3, TabGroup.ImpostorRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.ConnectSaver]);
    }

    public override void Init()
    {
        PlayerIdList = [];
    }

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);
        ConnectSaverId = playerId;
        target1 = byte.MaxValue;
        target2 = byte.MaxValue;
        usedcount = 0;
        IsUsing = false;
        IsSelectingMode = false;
    }

    public override void Remove(byte playerId)
    {
        PlayerIdList.Remove(playerId);
    }

    public override void SetKillCooldown(byte id)
    {
        Main.AllPlayerKillCooldown[id] = IsUsing ? OptionTageKillCoolDown.GetFloat() : OptionKillCoolDown.GetFloat();
    }

    public override void AfterMeetingTasks()
    {
        IsSelectingMode = false;

        PlayerControl pc = Utils.GetPlayerById(ConnectSaverId);
        if (pc == null || !pc.IsAlive()) return;

        Main.AllPlayerKillCooldown[ConnectSaverId] = IsUsing ? OptionTageKillCoolDown.GetFloat() : OptionKillCoolDown.GetFloat();
        pc.SyncSettings();
    }

    public override bool OnVote(PlayerControl voter, PlayerControl target)
    {
        if (voter.PlayerId != ConnectSaverId) return false;
        if (!voter.IsAlive()) return false;
        if (usedcount >= OptionMax.GetInt()) return false;
        if (target == null) return false;

        // 最低人数チェックは self-vote 時点で行う。以前は target-vote の段階で
        // silent return（消費するが何も起きない）していたため、プレイヤー側からは
        // 「self-vote で activate できたのにその後何も起きない＝壊れている」と見えていた
        int minCount = OptionMinimumPlayerCount.GetInt();
        if (Main.AllAlivePlayerControls.Count < minCount)
        {
            if (target.PlayerId == ConnectSaverId)
                Utils.SendMessage(string.Format(GetString("ConnectSaverInsufficientPlayers"), minCount), ConnectSaverId);
            return false; // 通常投票として扱う（silent eat ではなく）
        }

        if (!IsSelectingMode)
        {
            if (target.PlayerId != ConnectSaverId) return false;

            IsSelectingMode = true;
            Utils.SendMessage(GetString("ConnectSaverActivate"), ConnectSaverId);
            return true;
        }

        // In selection mode: eat self-votes
        if (target.PlayerId == ConnectSaverId) return true;

        SetTarget(target.PlayerId);
        return true;
    }

    private void SetTarget(byte votedId)
    {
        if (target1 == byte.MaxValue)
            target1 = votedId;
        else if (target2 == byte.MaxValue)
            target2 = votedId;

        if (target1 == target2)
            target2 = byte.MaxValue;

        ValidateTarget(ref target1);
        ValidateTarget(ref target2);

        if (target1 != byte.MaxValue || target2 != byte.MaxValue)
        {
            string targetName = votedId.ColoredPlayerName();
            string countKey = (target1 != byte.MaxValue && target2 != byte.MaxValue) ? "ConnectSaverTwoTargets" : "ConnectSaverOneTarget";
            Utils.SendMessage(string.Format(GetString("ConnectSaverTargetSet"), GetString(countKey), targetName), ConnectSaverId);
        }

        if (target1 != byte.MaxValue && target2 != byte.MaxValue)
        {
            Utils.SendMessage(GetString("ConnectSaverConnected"), ConnectSaverId);
            usedcount++;
            IsUsing = true;
            IsSelectingMode = false;
        }
    }

    private static void ValidateTarget(ref byte targetId)
    {
        if (targetId == byte.MaxValue) return;
        PlayerControl pc = Utils.GetPlayerById(targetId);
        if (pc == null || !pc.IsAlive()) targetId = byte.MaxValue;
    }

    public override bool OnCheckMurder(PlayerControl killer, PlayerControl target)
    {
        if (!IsUsing) return true;

        bool isTarget1 = target.PlayerId == target1 && target1 != byte.MaxValue;
        bool isTarget2 = target.PlayerId == target2 && target2 != byte.MaxValue;

        if (isTarget1 || isTarget2)
        {
            byte partnerId = isTarget1 ? target2 : target1;
            PlayerControl partner = Utils.GetPlayerById(partnerId);

            if (partner != null && partner.IsAlive())
            {
                partner.SetRealKiller(killer);
                partner.Suicide(DeathReasonOptions[OptionDeathReason.GetValue()], killer);
            }

            target1 = byte.MaxValue;
            target2 = byte.MaxValue;
            IsUsing = false;

            Main.AllPlayerKillCooldown[ConnectSaverId] = OptionKillCoolDown.GetFloat();
            LateTask.New(() => killer.SyncSettings(), 0.1f, "ConnectSaver.CDReset");

            return true;
        }

        // Connected but target is not one of the two → block kill
        return false;
    }

    public override void OnReportDeadBody()
    {
        target1 = byte.MaxValue;
        target2 = byte.MaxValue;
        IsUsing = false;
        IsSelectingMode = false;
    }

    public override string GetProgressText(byte playerId, bool comms)
    {
        if (playerId != ConnectSaverId) return string.Empty;
        int remaining = OptionMax.GetInt() - usedcount;
        Color color = remaining > 0 ? Palette.ImpostorRed : Color.gray;
        return Utils.ColorString(color, $"({remaining})");
    }

    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud = false, bool meeting = false)
    {
        if (seer.PlayerId != ConnectSaverId) return string.Empty;

        if (meeting && seer.PlayerId == target.PlayerId && seer.IsAlive())
        {
            if (IsSelectingMode)
            {
                string hint = target1 == byte.MaxValue
                    ? GetString("ConnectSaverSelectFirst")
                    : GetString("ConnectSaverSelectSecond");
                return $"<size=40%><color=#ff1919>{hint}</color></size>";
            }

            if (!IsUsing && usedcount < OptionMax.GetInt() && Main.AllAlivePlayerControls.Count >= OptionMinimumPlayerCount.GetInt())
                return $"<size=40%><color=#ff1919>{GetString("ConnectSaverSelfVoteHint")}</color></size>";

            return string.Empty;
        }

        if (!meeting && !hud && IsUsing)
        {
            if (target.PlayerId == target1 || target.PlayerId == target2)
                return Utils.ColorString(Palette.Purple, "◎");
        }

        return string.Empty;
    }
}
