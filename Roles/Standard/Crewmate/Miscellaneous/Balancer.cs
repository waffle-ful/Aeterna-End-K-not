using System.Collections.Generic;
using System.Linq;
using static EndKnot.Translator;

namespace EndKnot.Roles;

public class Balancer : RoleBase
{
    private const int Id = 702200;
    public static List<byte> PlayerIdList = [];

    private static OptionItem OptionMeetingTime;
    private static OptionItem OptionCanUseAllAlive;

    // Global state for the active Balancer meeting (at most one at a time)
    public static bool IsBalancerMeeting;
    public static byte MeetingTarget1 = byte.MaxValue;
    public static byte MeetingTarget2 = byte.MaxValue;

    // Per-player state
    private static Dictionary<byte, bool> IsSelecting = [];
    private static Dictionary<byte, byte> SelectedTarget1 = [];
    private static Dictionary<byte, byte> SelectedTarget2 = [];
    private static Dictionary<byte, bool> HasUsed = [];

    private byte BalancerId;

    public override bool IsEnable => PlayerIdList.Count > 0;

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.CrewmateRoles, CustomRoles.Balancer);

        OptionMeetingTime = new IntegerOptionItem(Id + 10, "BalancerMeetingTime", new(15, 120, 1), 30, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Balancer])
            .SetValueFormat(OptionFormat.Seconds);

        OptionCanUseAllAlive = new BooleanOptionItem(Id + 11, "BalancerCanUseAllAlive", false, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Balancer]);
    }

    public override void Init()
    {
        PlayerIdList = [];
        IsSelecting = [];
        SelectedTarget1 = [];
        SelectedTarget2 = [];
        HasUsed = [];
        IsBalancerMeeting = false;
        MeetingTarget1 = byte.MaxValue;
        MeetingTarget2 = byte.MaxValue;
    }

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);
        BalancerId = playerId;
        IsSelecting[playerId] = false;
        SelectedTarget1[playerId] = byte.MaxValue;
        SelectedTarget2[playerId] = byte.MaxValue;
        HasUsed[playerId] = false;
    }

    public override void Remove(byte playerId)
    {
        PlayerIdList.Remove(playerId);
    }

    private bool CanUseAbility(byte id)
    {
        if (HasUsed.TryGetValue(id, out bool used) && used) return false;
        if (OptionCanUseAllAlive.GetBool() && !GameStates.AlreadyDied) return false;
        return true;
    }

    public override bool OnVote(PlayerControl voter, PlayerControl target)
    {
        if (voter.PlayerId != BalancerId) return false;
        if (!voter.IsAlive()) return false;

        // During a Balancer meeting, the Balancer just votes normally (target1 or target2)
        if (IsBalancerMeeting) return false;

        if (!CanUseAbility(BalancerId)) return false;

        bool selecting = IsSelecting.TryGetValue(BalancerId, out bool s) && s;
        byte t1 = SelectedTarget1.TryGetValue(BalancerId, out byte st1) ? st1 : byte.MaxValue;
        byte t2 = SelectedTarget2.TryGetValue(BalancerId, out byte st2) ? st2 : byte.MaxValue;

        if (!selecting)
        {
            // Self-vote enters selection mode
            if (target == null || target.PlayerId != BalancerId) return false;
            IsSelecting[BalancerId] = true;
            Utils.SendMessage(GetString("BalancerEnterMode"), BalancerId, importance: MessageImportance.High);
            return true;
        }

        // In selection mode
        if (target == null)
        {
            // Skip cancels selection
            IsSelecting[BalancerId] = false;
            SelectedTarget1[BalancerId] = byte.MaxValue;
            SelectedTarget2[BalancerId] = byte.MaxValue;
            Utils.SendMessage(GetString("BalancerCancelMode"), BalancerId, importance: MessageImportance.High);
            return false;
        }

        byte votedId = target.PlayerId;
        if (votedId == BalancerId) return false; // cannot self-select as target

        if (!target.IsAlive())
        {
            Utils.SendMessage(GetString("BalancerTargetDead"), BalancerId, importance: MessageImportance.High);
            return true;
        }

        if (t1 == byte.MaxValue)
        {
            SelectedTarget1[BalancerId] = votedId;
            Utils.SendMessage(string.Format(GetString("BalancerTarget1Selected"), votedId.ColoredPlayerName()), BalancerId, importance: MessageImportance.High);
            return true;
        }

        if (votedId == t1)
        {
            // Voted the same as target1 → deselect
            SelectedTarget1[BalancerId] = byte.MaxValue;
            Utils.SendMessage(GetString("BalancerTarget1Cleared"), BalancerId, importance: MessageImportance.High);
            return true;
        }

        // Second target chosen → finalize
        SelectedTarget2[BalancerId] = votedId;
        IsSelecting[BalancerId] = false;
        HasUsed[BalancerId] = true;
        Utils.SendMessage(string.Format(GetString("BalancerTargetsSelected"), t1.ColoredPlayerName(), votedId.ColoredPlayerName()), BalancerId, importance: MessageImportance.High);
        return true;
    }

    public override void AfterMeetingTasks()
    {
        if (!AmongUsClient.Instance.AmHost) return;

        // After a Balancer meeting ends: clear global state
        if (IsBalancerMeeting)
        {
            IsBalancerMeeting = false;
            MeetingTarget1 = byte.MaxValue;
            MeetingTarget2 = byte.MaxValue;
            Utils.NotifyRoles(ForceLoop: true, NoCache: true);
            return;
        }

        // After the normal meeting: if targets were selected and Balancer is alive, start Balancer meeting
        byte t1 = SelectedTarget1.TryGetValue(BalancerId, out byte st1) ? st1 : byte.MaxValue;
        byte t2 = SelectedTarget2.TryGetValue(BalancerId, out byte st2) ? st2 : byte.MaxValue;

        if (t1 == byte.MaxValue || t2 == byte.MaxValue) return;

        PlayerControl balancerPc = Utils.GetPlayerById(BalancerId);
        if (balancerPc == null || !balancerPc.IsAlive()) return;

        PlayerControl p1 = Utils.GetPlayerById(t1);
        PlayerControl p2 = Utils.GetPlayerById(t2);
        if (p1 == null || !p1.IsAlive() || p2 == null || !p2.IsAlive()) return;

        // Clear targets now so a subsequent normal meeting doesn't re-schedule the Balancer meeting
        SelectedTarget1[BalancerId] = byte.MaxValue;
        SelectedTarget2[BalancerId] = byte.MaxValue;

        LateTask.New(() =>
        {
            // Re-check alive status at fire time
            if (Utils.GetPlayerById(BalancerId)?.IsAlive() != true) return;
            if (Utils.GetPlayerById(t1)?.IsAlive() != true || Utils.GetPlayerById(t2)?.IsAlive() != true) return;

            // Set state just before the meeting opens so it is always consistent
            IsBalancerMeeting = true;
            MeetingTarget1 = t1;
            MeetingTarget2 = t2;

            Utils.SendMessage(string.Format(GetString("BalancerMeetingStart"), t1.ColoredPlayerName(), t2.ColoredPlayerName()), importance: MessageImportance.High);
            balancerPc.NoCheckStartMeeting(null, true);
        }, 3f, "BalancerMeetingStart");
    }

    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud = false, bool meeting = false)
    {
        if (!meeting) return string.Empty;
        if (!IsBalancerMeeting) return string.Empty;
        if (target == null) return string.Empty;
        if (target.PlayerId == MeetingTarget1 || target.PlayerId == MeetingTarget2)
            return "<color=#ff1919>Ω</color>";
        return string.Empty;
    }

    public override string GetProgressText(byte playerId, bool comms)
    {
        if (playerId != BalancerId) return string.Empty;
        bool used = HasUsed.TryGetValue(BalancerId, out bool u) && u;
        return Utils.ColorString(used ? UnityEngine.Color.gray : UnityEngine.Color.cyan, used ? "(✓)" : "(✗)");
    }

    // Called from MeetingHudCastVotePatch to restrict votes during Balancer meeting
    public static bool ShouldCancelVote(PlayerControl target)
    {
        if (!IsBalancerMeeting) return false;
        if (target == null) return true; // cancel skip votes
        return target.PlayerId != MeetingTarget1 && target.PlayerId != MeetingTarget2;
    }

    // Called from CheckForEndVoting to redistribute invalid votes
    public static void ManipulateVotingResult(Dictionary<byte, int> votingData, MeetingHud.VoterState[] states)
    {
        if (!IsBalancerMeeting || MeetingTarget1 == byte.MaxValue || MeetingTarget2 == byte.MaxValue) return;

        var rng = IRandom.Instance;
        for (int i = 0; i < states.Length; i++)
        {
            ref MeetingHud.VoterState state = ref states[i];
            // Redirect votes not for T1 or T2 (including skip = 253, maxbyte = no vote)
            if (state.VotedForId == MeetingTarget1 || state.VotedForId == MeetingTarget2) continue;

            byte redirectTo = rng.Next(2) == 0 ? MeetingTarget1 : MeetingTarget2;
            int oldCount = votingData.GetValueOrDefault(state.VotedForId, 0);
            if (oldCount > 0 && state.VotedForId != 253 && state.VotedForId != byte.MaxValue)
                votingData[state.VotedForId] = oldCount - 1;
            votingData[redirectTo] = votingData.GetValueOrDefault(redirectTo, 0) + 1;
            state.VotedForId = redirectTo;
        }
    }
}
