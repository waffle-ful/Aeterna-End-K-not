using AmongUs.GameOptions;
using EndKnot.Modules;
using EndKnot.Patches;
using Hazel;
using System.Collections.Generic;
using UnityEngine;
using static EndKnot.Options;
using static EndKnot.Translator;

namespace EndKnot.Roles;

public class MassMedia : RoleBase
{
    private const int Id = 704700;
    public static List<byte> PlayerIdList = [];

    private static OptionItem KillCooldown;
    private static OptionItem BlackVisionAmount;
    private static OptionItem MeetingTargetReset;
    private static OptionItem CanSeeKillFlash;
    private static OptionItem CriminalProfile;

    private byte MassMediaId;
    private byte TargetId;
    private byte GuessId;
    private bool GuessMode;
    private bool Win;
    private bool IsBlackOut;
    private bool WasTargetAlive;
    private Vector3 TargetPosition;
    private List<byte> Suspects;

    public override bool IsEnable => PlayerIdList.Count > 0;

    public override void SetupCustomOption()
    {
        SetupRoleOptions(Id, TabGroup.NeutralRoles, CustomRoles.MassMedia);

        KillCooldown = new FloatOptionItem(Id + 10, "KillCooldown", new(0f, 180f, 0.5f), 20f, TabGroup.NeutralRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.MassMedia])
            .SetValueFormat(OptionFormat.Seconds);

        BlackVisionAmount = new FloatOptionItem(Id + 11, "MassMediaBlackVisionAmount", new(0f, 0.2f, 0.02f), 0.04f, TabGroup.NeutralRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.MassMedia])
            .SetValueFormat(OptionFormat.Multiplier);

        MeetingTargetReset = new BooleanOptionItem(Id + 12, "MassMediaMeetingTargetReset", false, TabGroup.NeutralRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.MassMedia]);

        CanSeeKillFlash = new BooleanOptionItem(Id + 13, "MassMediaCanSeeKillFlash", false, TabGroup.NeutralRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.MassMedia]);

        CriminalProfile = new BooleanOptionItem(Id + 14, "MassMediaCriminalProfile", false, TabGroup.NeutralRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.MassMedia]);
    }

    public override void Init()
    {
        PlayerIdList = [];
    }

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);
        MassMediaId = playerId;
        TargetId = byte.MaxValue;
        GuessId = byte.MaxValue;
        GuessMode = false;
        Win = false;
        IsBlackOut = false;
        WasTargetAlive = false;
        TargetPosition = new Vector3(999f, 999f, 0f);
        Suspects = [];
    }

    public override void Remove(byte playerId)
    {
        PlayerIdList.Remove(playerId);
    }

    public override void SetKillCooldown(byte id)
    {
        Main.AllPlayerKillCooldown[id] = KillCooldown.GetFloat();
    }

    public override bool CanUseKillButton(PlayerControl pc) => true;

    public override bool OnCheckMurder(PlayerControl killer, PlayerControl target)
    {
        if (TargetId != byte.MaxValue) return false;

        TargetId = target.PlayerId;
        WasTargetAlive = true;
        killer.SetKillCooldown(999f);
        TargetArrow.Add(MassMediaId, TargetId);
        SendRPC();
        Utils.NotifyRoles(SpecifySeer: killer);
        return false;
    }

    public override void OnFixedUpdate(PlayerControl player)
    {
        if (!GameStates.IsInTask || !player.IsAlive() || TargetId == byte.MaxValue) return;

        var target = Utils.GetPlayerById(TargetId);
        if (target == null) return;

        bool targetAlive = target.IsAlive();

        if (WasTargetAlive && !targetAlive)
        {
            WasTargetAlive = false;
            GuessId = target.GetRealKiller()?.PlayerId ?? byte.MaxValue;
            TargetPosition = target.transform.position;
            TargetArrow.Remove(MassMediaId, TargetId);
            LocateArrow.Add(MassMediaId, TargetPosition);
            if (CanSeeKillFlash.GetBool()) player.KillFlash();
            SendRPC();
        }

        if (targetAlive)
        {
            float range = Main.DefaultCrewmateVision * 6.5f;
            float dist = Vector2.Distance(player.Pos(), target.Pos());
            bool nowBlackOut = dist <= range;

            if (nowBlackOut != IsBlackOut)
            {
                IsBlackOut = nowBlackOut;
                player.MarkDirtySettings();
            }

            if (CriminalProfile.GetBool())
            {
                foreach (var other in Main.AllAlivePlayerControlsToList)
                {
                    if (other.PlayerId == TargetId || other.PlayerId == MassMediaId) continue;
                    if (Suspects.Contains(other.PlayerId)) continue;
                    if (Vector2.Distance(target.Pos(), other.Pos()) <= 4.5f)
                        Suspects.Add(other.PlayerId);
                }
            }
        }
        else if (IsBlackOut)
        {
            IsBlackOut = false;
            player.MarkDirtySettings();
        }
    }

    public override void ApplyGameOptions(IGameOptions opt, byte playerId)
    {
        if (IsBlackOut)
        {
            float v = BlackVisionAmount.GetFloat();
            opt.SetFloat(FloatOptionNames.CrewLightMod, v);
            opt.SetFloat(FloatOptionNames.ImpostorLightMod, v);
        }
        else
        {
            opt.SetVision(false);
        }
    }

    // Called from PlayerControlPatch when MassMedia self-reports a dead body
    public void OnSelfReport(byte reportedBodyPlayerId)
    {
        if (reportedBodyPlayerId != TargetId || GuessMode) return;
        GuessMode = true;
        SendRPC();
    }

    public override void OnReportDeadBody()
    {
        bool targetDead = !(Utils.GetPlayerById(TargetId)?.IsAlive() ?? false);
        if (MeetingTargetReset.GetBool() || targetDead)
        {
            if (TargetId != byte.MaxValue)
            {
                TargetArrow.Remove(MassMediaId, TargetId);
                LocateArrow.RemoveAllTarget(MassMediaId);
                TargetId = byte.MaxValue;
                TargetPosition = new Vector3(999f, 999f, 0f);
            }
        }

        IsBlackOut = false;
        Suspects = [];
        WasTargetAlive = TargetId != byte.MaxValue && (Utils.GetPlayerById(TargetId)?.IsAlive() ?? false);
        SendRPC();
    }

    public override bool OnVote(PlayerControl voter, PlayerControl voted)
    {
        if (voter.PlayerId != MassMediaId || !GuessMode) return false;
        if (!AmongUsClient.Instance.AmHost) return false;

        if (voted == null || voted.PlayerId == voter.PlayerId)
        {
            GuessMode = false;
            SendRPC();
            Utils.SendMessage(GetString("MassMediaGuessCanceled"), voter.PlayerId, Utils.ColorString(Utils.GetRoleColor(CustomRoles.MassMedia), GetString("MassMedia")));
            return true;
        }

        if (voted.PlayerId == GuessId)
        {
            Win = true;
            SendRPC();
            Utils.SendMessage(GetString("MassMediaGuessCorrect"), voter.PlayerId, Utils.ColorString(Utils.GetRoleColor(CustomRoles.MassMedia), GetString("MassMedia")));
        }
        else
        {
            CheckForEndVotingPatch.TryAddAfterMeetingDeathPlayers(PlayerState.DeathReason.Misfire, voter.PlayerId);
            Utils.SendMessage(GetString("MassMediaGuessWrong"), voter.PlayerId, Utils.ColorString(Utils.GetRoleColor(CustomRoles.MassMedia), GetString("MassMedia")));
        }

        return true;
    }

    public override void AfterMeetingTasks()
    {
        if (Win && AmongUsClient.Instance.AmHost)
        {
            CustomWinnerHolder.ResetAndSetWinner(CustomWinner.MassMedia);
            CustomWinnerHolder.WinnerIds.Add(MassMediaId);
        }

        Win = false;
        GuessMode = false;
        IsBlackOut = false;
        Utils.GetPlayerById(MassMediaId)?.MarkDirtySettings();
        SendRPC();
    }

    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud = false, bool meeting = false)
    {
        if (seer.PlayerId != MassMediaId) return string.Empty;

        // Self-view: HUD status + arrows + guess hint
        if (target.PlayerId == MassMediaId)
        {
            if (!hud && !seer.IsModdedClient()) return string.Empty;

            if (GuessMode && meeting)
                return Utils.ColorString(Utils.GetRoleColor(CustomRoles.MassMedia), GetString("MassMediaGuessMode") + "\n" + GetString("MassMediaChance"));

            if (TargetId != byte.MaxValue)
            {
                string name = Utils.GetPlayerById(TargetId)?.GetRealName() ?? "?";
                string arrowStr = string.Empty;
                var t = Utils.GetPlayerById(TargetId);
                if (t != null)
                    arrowStr = t.IsAlive()
                        ? Utils.ColorString(Utils.GetRoleColor(CustomRoles.MassMedia), TargetArrow.GetArrows(Utils.GetPlayerById(MassMediaId), TargetId))
                        : Utils.ColorString(Utils.GetRoleColor(CustomRoles.MassMedia), LocateArrow.GetArrows(Utils.GetPlayerById(MassMediaId)));

                return string.Format(GetString("MassMediaTargetHUD"), name) + arrowStr;
            }

            return GetString("MassMediaNoTarget");
        }

        // ★ on marked target
        if (TargetId != byte.MaxValue && target.PlayerId == TargetId)
            return Utils.ColorString(Utils.GetRoleColor(CustomRoles.MassMedia), "★");

        // 〇 on suspects in meeting
        if (meeting && CriminalProfile.GetBool() && Suspects.Contains(target.PlayerId))
            return Utils.ColorString(Utils.GetRoleColor(CustomRoles.MassMedia), "〇");

        return string.Empty;
    }

    private void SendRPC()
    {
        bool hasPos = TargetPosition != new Vector3(999f, 999f, 0f);
        Utils.SendRPC(CustomRPC.SyncRoleData, MassMediaId,
            TargetId,
            GuessId,
            GuessMode ? 1 : 0,
            Win ? 1 : 0,
            hasPos ? 1 : 0,
            TargetPosition.x,
            TargetPosition.y);
    }

    public void ReceiveRPC(MessageReader reader)
    {
        byte newTargetId = reader.ReadByte();
        GuessId = reader.ReadByte();
        GuessMode = reader.ReadPackedInt32() == 1;
        Win = reader.ReadPackedInt32() == 1;
        bool hasPos = reader.ReadPackedInt32() == 1;
        float x = reader.ReadSingle();
        float y = reader.ReadSingle();

        TargetId = newTargetId;
        TargetPosition = hasPos ? new Vector3(x, y, 0f) : new Vector3(999f, 999f, 0f);
        WasTargetAlive = TargetId != byte.MaxValue && (Utils.GetPlayerById(TargetId)?.IsAlive() ?? false);
    }
}
