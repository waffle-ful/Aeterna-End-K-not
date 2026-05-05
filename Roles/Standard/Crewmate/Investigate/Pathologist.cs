using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using static EndKnot.Translator;

namespace EndKnot.Roles;

// Migrated from TOHK Inspector. Renamed to avoid conflict with EHR's existing Inspector role.
public class Pathologist : RoleBase
{
    private const int Id = 702700;
    public static List<byte> PlayerIdList = [];

    private static OptionItem OptionMaxUses;
    private static OptionItem OptionVoteMode;
    private static OptionItem OptionInfoCount;
    private static OptionItem OptionSetRect;
    private static OptionItem OptionDeathReason;
    private static OptionItem OptionKillerColor;
    private static OptionItem OptionTargetTeam;
    private static OptionItem OptionKillerRole;
    private static OptionItem OptionDeathTimer;
    private static OptionItem OptionTargetRoom;
    private static OptionItem OptionTaskAwakening;
    private static OptionItem OptionAwakeningTaskCount;

    private static bool UseSetRect;
    private static float RectDeathReason;
    private static float RectKillerColor;
    private static float RectTargetTeam;
    private static float RectKillerRole;
    private static float RectDeathTimer;
    private static float RectTargetRoom;

    private static Dictionary<byte, int> UseCount = [];
    private static Dictionary<byte, byte> TrackedTarget = [];
    private static Dictionary<byte, float> DeadTimer = [];
    private static Dictionary<byte, bool> IsTargetDead = [];
    private static Dictionary<byte, bool> Awakened = [];
    private static Dictionary<byte, bool> IsSelecting = [];

    private byte PathologistId;

    private enum VoteMode { Normal, SelfVote }
    private enum InfoType { DeathReason, Color, TargetTeam, KillerRole, DeathTimer, TargetRoom }

    public override bool IsEnable => PlayerIdList.Count > 0;

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.CrewmateRoles, CustomRoles.Pathologist);

        OptionMaxUses = new IntegerOptionItem(Id + 10, "PathologistMaxUses", new(1, 99, 1), 1, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Pathologist])
            .SetValueFormat(OptionFormat.Times);

        OptionVoteMode = new StringOptionItem(Id + 11, "PathologistVoteMode", ["Normal", "Self Vote"], 0, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Pathologist]);

        OptionInfoCount = new IntegerOptionItem(Id + 21, "PathologistInfoCount", new(1, 6, 1), 1, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Pathologist])
            .SetValueFormat(OptionFormat.Times);

        OptionTaskAwakening = new BooleanOptionItem(Id + 12, "PathologistTaskAwakening", false, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Pathologist]);

        OptionAwakeningTaskCount = new IntegerOptionItem(Id + 13, "PathologistAwakeningTaskCount", new(1, 99, 1), 5, TabGroup.CrewmateRoles)
            .SetParent(OptionTaskAwakening);

        OptionSetRect = new BooleanOptionItem(Id + 14, "PathologistSetRect", false, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Pathologist]);

        OptionDeathReason = new FloatOptionItem(Id + 15, "PathologistDeathReason", new(0, 100, 5), 100, TabGroup.CrewmateRoles)
            .SetParent(OptionSetRect).SetValueFormat(OptionFormat.Percent);

        OptionKillerColor = new FloatOptionItem(Id + 16, "PathologistKillerColor", new(0, 100, 5), 100, TabGroup.CrewmateRoles)
            .SetParent(OptionSetRect).SetValueFormat(OptionFormat.Percent);

        OptionTargetTeam = new FloatOptionItem(Id + 17, "PathologistTargetTeam", new(0, 100, 5), 100, TabGroup.CrewmateRoles)
            .SetParent(OptionSetRect).SetValueFormat(OptionFormat.Percent);

        OptionKillerRole = new FloatOptionItem(Id + 18, "PathologistKillerRole", new(0, 100, 5), 100, TabGroup.CrewmateRoles)
            .SetParent(OptionSetRect).SetValueFormat(OptionFormat.Percent);

        OptionDeathTimer = new FloatOptionItem(Id + 19, "PathologistDeathTimer", new(0, 100, 5), 100, TabGroup.CrewmateRoles)
            .SetParent(OptionSetRect).SetValueFormat(OptionFormat.Percent);

        OptionTargetRoom = new FloatOptionItem(Id + 20, "PathologistTargetRoom", new(0, 100, 5), 100, TabGroup.CrewmateRoles)
            .SetParent(OptionSetRect).SetValueFormat(OptionFormat.Percent);
    }

    public override void Init()
    {
        PlayerIdList = [];
        UseCount = [];
        TrackedTarget = [];
        DeadTimer = [];
        IsTargetDead = [];
        Awakened = [];
        IsSelecting = [];

        UseSetRect = OptionSetRect.GetBool();
        RectDeathReason = OptionDeathReason.GetFloat();
        RectKillerColor = OptionKillerColor.GetFloat();
        RectTargetTeam = OptionTargetTeam.GetFloat();
        RectKillerRole = OptionKillerRole.GetFloat();
        RectDeathTimer = OptionDeathTimer.GetFloat();
        RectTargetRoom = OptionTargetRoom.GetFloat();
    }

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);
        PathologistId = playerId;
        UseCount[playerId] = OptionMaxUses.GetInt();
        TrackedTarget[playerId] = byte.MaxValue;
        DeadTimer[playerId] = 0f;
        IsTargetDead[playerId] = false;
        IsSelecting[playerId] = false;

        bool requiresAwakening = OptionTaskAwakening.GetBool();
        Awakened[playerId] = !requiresAwakening;
    }

    public override void Remove(byte playerId)
    {
        PlayerIdList.Remove(playerId);
    }

    private bool CanUseAbility(byte id)
    {
        if (!Awakened.TryGetValue(id, out bool aw) || !aw) return false;
        if (!UseCount.TryGetValue(id, out int cnt) || cnt <= 0) return false;
        if (TrackedTarget.TryGetValue(id, out byte t) && t != byte.MaxValue) return false;
        return true;
    }

    public override void OnTaskComplete(PlayerControl pc, int completedTaskCount, int totalTaskCount)
    {
        if (!Awakened.TryGetValue(pc.PlayerId, out bool aw) || aw) return;
        if (completedTaskCount + 1 >= OptionAwakeningTaskCount.GetInt())
        {
            Awakened[pc.PlayerId] = true;
            Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc);
        }
    }

    public override void OnFixedUpdate(PlayerControl pc)
    {
        if (!AmongUsClient.Instance.AmHost) return;
        if (!pc.IsAlive()) return;
        if (!IsTargetDead.TryGetValue(pc.PlayerId, out bool dead) || !dead)
        {
            if (!TrackedTarget.TryGetValue(pc.PlayerId, out byte tid) || tid == byte.MaxValue) return;
            PlayerControl target = Utils.GetPlayerById(tid);
            if (target == null || target.IsAlive()) return;
            if (Main.PlayerStates.TryGetValue(tid, out PlayerState st) && st.deathReason == PlayerState.DeathReason.Disconnected)
            {
                TrackedTarget[pc.PlayerId] = byte.MaxValue;
                return;
            }
            IsTargetDead[pc.PlayerId] = true;
        }
        else
        {
            DeadTimer[pc.PlayerId] += Time.fixedDeltaTime;
        }
    }

    public override bool OnVote(PlayerControl voter, PlayerControl target)
    {
        if (voter.PlayerId != PathologistId) return false;
        if (!voter.IsAlive()) return false;
        if (!CanUseAbility(PathologistId)) return false;

        VoteMode mode = (VoteMode)OptionVoteMode.GetValue();

        if (mode == VoteMode.SelfVote)
        {
            bool selecting = IsSelecting.TryGetValue(PathologistId, out bool s) && s;
            if (!selecting)
            {
                if (target == null || target.PlayerId != PathologistId) return false;
                IsSelecting[PathologistId] = true;
                Utils.SendMessage(GetString("PathologistEnterMode"), PathologistId, importance: MessageImportance.High);
                return true;
            }

            IsSelecting[PathologistId] = false;
            if (target == null || !target.IsAlive()) return false;
            SetTarget(voter.PlayerId, target.PlayerId);
            return true;
        }
        else
        {
            if (target == null || target.PlayerId == PathologistId || !target.IsAlive()) return false;
            SetTarget(voter.PlayerId, target.PlayerId);
            return true;
        }
    }

    private void SetTarget(byte id, byte targetId)
    {
        UseCount[id]--;
        TrackedTarget[id] = targetId;
        Utils.SendMessage(string.Format(GetString("PathologistTargetSet"), targetId.ColoredPlayerName()), id, importance: MessageImportance.High);
    }

    public override void OnReportDeadBody()
    {
        if (!AmongUsClient.Instance.AmHost) return;
        if (!IsTargetDead.TryGetValue(PathologistId, out bool dead) || !dead) return;

        byte tid = TrackedTarget.TryGetValue(PathologistId, out byte t) ? t : byte.MaxValue;
        if (tid == byte.MaxValue) return;

        if (!Main.PlayerStates.TryGetValue(tid, out PlayerState targetState)) return;

        float timer = DeadTimer.TryGetValue(PathologistId, out float dt) ? dt : 0f;
        byte killerId = targetState.RealKiller.ID;

        int enumCount = System.Enum.GetValues(typeof(InfoType)).Length;
        int infoCount = Math.Min(OptionInfoCount.GetInt(), enumCount);

        List<(InfoType type, float weight)> pool = [];
        if (UseSetRect)
        {
            if (RectDeathReason > 0) pool.Add((InfoType.DeathReason, RectDeathReason));
            if (RectKillerColor > 0) pool.Add((InfoType.Color, RectKillerColor));
            if (RectTargetTeam > 0) pool.Add((InfoType.TargetTeam, RectTargetTeam));
            if (RectKillerRole > 0) pool.Add((InfoType.KillerRole, RectKillerRole));
            if (RectDeathTimer > 0) pool.Add((InfoType.DeathTimer, RectDeathTimer));
            if (RectTargetRoom > 0) pool.Add((InfoType.TargetRoom, RectTargetRoom));
        }
        else
        {
            foreach (InfoType it in System.Enum.GetValues(typeof(InfoType)))
                pool.Add((it, 1f));
        }

        var sb = new StringBuilder();

        for (int i = 0; i < infoCount && pool.Count > 0; i++)
        {
            InfoType infoType;
            if (UseSetRect)
            {
                float total = 0f;
                foreach (var p in pool) total += p.weight;
                float chance = IRandom.Instance.Next(Math.Max(1, (int)total));
                float acc = 0f;
                infoType = pool[^1].type;
                for (int j = 0; j < pool.Count; j++)
                {
                    acc += pool[j].weight;
                    if (chance < acc) { infoType = pool[j].type; break; }
                }
            }
            else
            {
                infoType = pool[IRandom.Instance.Next(pool.Count)].type;
            }

            for (int j = pool.Count - 1; j >= 0; j--)
                if (pool[j].type == infoType) { pool.RemoveAt(j); break; }

            if (sb.Length > 0) sb.AppendLine();

            switch (infoType)
            {
                case InfoType.DeathReason:
                    sb.AppendFormat(GetString("PathologistInfoDeathReason"), tid.ColoredPlayerName(), GetString($"DeathReason.{targetState.deathReason}"));
                    break;

                case InfoType.Color:
                    if (killerId != byte.MaxValue && Camouflage.PlayerSkins.TryGetValue(killerId, out var outfit))
                    {
                        int colorId = outfit.ColorId;
                        string colorName = Palette.GetColorName(colorId);
                        sb.AppendFormat(GetString("PathologistInfoColor"), tid.ColoredPlayerName(), colorName);
                    }
                    else
                    {
                        sb.AppendFormat(GetString("PathologistInfoDeathReason"), tid.ColoredPlayerName(), GetString($"DeathReason.{targetState.deathReason}"));
                    }
                    break;

                case InfoType.TargetTeam:
                    CustomRoleTypes teamType = targetState.MainRole.GetCustomRoleTypes();
                    string teamStr = teamType switch
                    {
                        CustomRoleTypes.Impostor => Utils.ColorString(Palette.ImpostorRed, GetString("TeamImpostor")),
                        CustomRoleTypes.Neutral => Utils.ColorString(UnityEngine.Color.gray, GetString("TeamNeutral")),
                        _ => Utils.ColorString(UnityEngine.Color.white, GetString("TeamCrewmate"))
                    };
                    sb.AppendFormat(GetString("PathologistInfoTeam"), tid.ColoredPlayerName(), teamStr);
                    break;

                case InfoType.KillerRole:
                    string killerRoleName = killerId != byte.MaxValue
                        ? Utils.GetRoleName(Main.PlayerStates.TryGetValue(killerId, out PlayerState ks) ? ks.MainRole : CustomRoles.Crewmate)
                        : "?";
                    sb.AppendFormat(GetString("PathologistInfoRole"), tid.ColoredPlayerName(), killerRoleName);
                    break;

                case InfoType.DeathTimer:
                    sb.AppendFormat(GetString("PathologistInfoTimer"), tid.ColoredPlayerName(), (int)timer);
                    break;

                case InfoType.TargetRoom:
                    PlainShipRoom lastRoom = targetState.LastRoom;
                    if (lastRoom != null)
                        sb.AppendFormat(GetString("PathologistInfoRoom"), tid.ColoredPlayerName(), GetString(lastRoom.RoomId.ToString()));
                    else
                        sb.AppendFormat(GetString("PathologistInfoDeathReason"), tid.ColoredPlayerName(), GetString($"DeathReason.{targetState.deathReason}"));
                    break;

                default:
                    sb.Append("???");
                    break;
            }
        }

        // 状態 reset は AfterMeetingTasks ではなくここで行う。
        // AfterMeetingTasks 内で reset すると「投票で target を仕込む → 会議終了 reset
        // → 次ラウンドで target 死亡しても TrackedTarget=byte.MaxValue で OnFixedUpdate が
        // 早期 return → IsTargetDead が立たず情報が出ない」という致命バグになる
        // (TOHK Inspector の OnStartMeeting 末尾 reset パターンに合わせる)
        TrackedTarget[PathologistId] = byte.MaxValue;
        DeadTimer[PathologistId] = 0f;
        IsTargetDead[PathologistId] = false;
        IsSelecting[PathologistId] = false;

        if (sb.Length == 0) return;
        string message = sb.ToString();
        LateTask.New(() => Utils.SendMessage(message, PathologistId, GetString("PathologistTitle"), importance: MessageImportance.High), 3f, "PathologistSend");
    }

    public override void AfterMeetingTasks()
    {
        // TrackedTarget / IsTargetDead / DeadTimer は OnReportDeadBody 末尾で reset する。
        // ここで reset すると「投票で仕込んだ target がラウンド中に死亡しても次会議で
        // 検出されない」というバグになる。
        // IsSelecting だけは「会議中に self-vote モード入りしてキャンセルした場合に持ち越す」
        // のを防ぐためここで確実に false に戻す
        IsSelecting[PathologistId] = false;
    }

    public override string GetProgressText(byte playerId, bool comms)
    {
        if (playerId != PathologistId) return string.Empty;
        int cnt = UseCount.TryGetValue(PathologistId, out int c) ? c : 0;
        return Utils.ColorString(cnt > 0 ? Color.cyan : Color.gray, $"({cnt})");
    }

    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud = false, bool meeting = false)
    {
        if (meeting || seer.PlayerId != PathologistId || target.PlayerId != PathologistId) return string.Empty;
        byte tid = TrackedTarget.TryGetValue(PathologistId, out byte t) ? t : byte.MaxValue;
        if (tid == byte.MaxValue) return string.Empty;
        return Utils.ColorString(Color.cyan, $"◎{tid.ColoredPlayerName()}");
    }
}
