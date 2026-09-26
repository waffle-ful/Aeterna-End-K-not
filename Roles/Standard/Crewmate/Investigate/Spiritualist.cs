using System.Collections.Generic;
using System.Linq;
using Hazel;
using static EndKnot.Options;
using static EndKnot.Translator;

namespace EndKnot.Roles;

internal class Spiritualist : RoleBase
{
    private const int Id = 8100;

    public static List<byte> PlayerIdList = [];

    private static OptionItem ShowGhostArrowEverySeconds;
    private static OptionItem ShowGhostArrowForSeconds;

    public static byte SpiritualistTarget;

    // マッドメイトの悪霊使いは、通常の直近死者でなく直近に死んだインポスターの幽霊に接続する。
    // 投票追放は OnReportDeadBody を経由しないため、判定は会議明けの死亡インポスター総覧との差分で行う。
    public static byte LastDeadImpostorId;
    private static readonly HashSet<byte> KnownDeadImpostors = [];
    private long LastGhostArrowShowTime;
    private long ShowGhostArrowUntil;
    private byte SpiritualistId;

    public override bool IsEnable => PlayerIdList.Count > 0;

    private bool ShowArrow
    {
        get
        {
            long timestamp = Utils.TimeStamp;

            if (LastGhostArrowShowTime == 0 || LastGhostArrowShowTime + (long)ShowGhostArrowEverySeconds.GetFloat() <= timestamp)
            {
                LastGhostArrowShowTime = timestamp;
                ShowGhostArrowUntil = timestamp + (long)ShowGhostArrowForSeconds.GetFloat();
                return true;
            }

            return ShowGhostArrowUntil >= timestamp;
        }
    }

    public override void SetupCustomOption()
    {
        SetupRoleOptions(Id, TabGroup.CrewmateRoles, CustomRoles.Spiritualist);

        ShowGhostArrowEverySeconds = new FloatOptionItem(Id + 10, "SpiritualistShowGhostArrowEverySeconds", new(1f, 60f, 1f), 15f, TabGroup.CrewmateRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Spiritualist])
            .SetValueFormat(OptionFormat.Seconds);

        ShowGhostArrowForSeconds = new FloatOptionItem(Id + 11, "SpiritualistShowGhostArrowForSeconds", new(1f, 60f, 1f), 2f, TabGroup.CrewmateRoles)
            .SetParent(CustomRoleSpawnChances[CustomRoles.Spiritualist])
            .SetValueFormat(OptionFormat.Seconds);
    }

    public override void Init()
    {
        PlayerIdList = [];
        SpiritualistTarget = 0;
        LastDeadImpostorId = byte.MaxValue;
        KnownDeadImpostors.Clear();
        LastGhostArrowShowTime = 0;
        ShowGhostArrowUntil = 0;
    }

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);
        SpiritualistTarget = byte.MaxValue;
        LastGhostArrowShowTime = 0;
        ShowGhostArrowUntil = 0;
        SpiritualistId = playerId;
    }

    public override void Remove(byte playerId)
    {
        PlayerIdList.Remove(playerId);
    }

    public static void OnReportDeadBody(NetworkedPlayerInfo target)
    {
        if (target == null) return;

        if (SpiritualistTarget != byte.MaxValue) RemoveTarget();

        SpiritualistTarget = target.PlayerId;
    }

    // マッドメイト向けの「直近に死んだインポスター」は投票追放でも切り替わる必要があるため、
    // OnReportDeadBody (通報時にしか発火しない) には乗せず、会議明けに死亡インポスターの
    // 総覧と既知集合の差分を取って検出する。複数人が同時に新規死亡した場合は列挙順で最後の1人を採用する。
    private static void UpdateLastDeadImpostor()
    {
        byte newest = byte.MaxValue;

        foreach (PlayerControl imp in Main.EnumeratePlayerControls())
        {
            if (imp.IsAlive() || !imp.Is(CustomRoleTypes.Impostor)) continue;
            if (!KnownDeadImpostors.Add(imp.PlayerId)) continue;

            newest = imp.PlayerId;
        }

        if (newest == byte.MaxValue) return;

        if (LastDeadImpostorId != byte.MaxValue) RemoveMadTarget();
        LastDeadImpostorId = newest;
    }

    public override void AfterMeetingTasks()
    {
        UpdateLastDeadImpostor();

        foreach (byte spiritualist in PlayerIdList)
        {
            PlayerControl player = spiritualist.GetPlayer();
            if (!player.IsAlive()) continue;

            LastGhostArrowShowTime = 0;
            ShowGhostArrowUntil = 0;

            if (!AmongUsClient.Instance.AmHost) continue;

            byte connectedId = player.Is(CustomRoles.Madmate) ? LastDeadImpostorId : SpiritualistTarget;
            if (connectedId == byte.MaxValue) continue;

            PlayerControl target = Main.EnumeratePlayerControls().FirstOrDefault(a => a.PlayerId == connectedId);
            if (target == null) continue;

            target.Notify(GetString("SpiritualistTargetMessage"));

            TargetArrow.Add(spiritualist, target.PlayerId);

            // 宛先がホスト自身だと自分宛 tag6 エンベロープを撃つことになるため、
            // Utils.SendMessage と同じくローカル表示へ分岐する (Utils.cs の receiver.AmOwner 分岐と同型)。
            if (target.AmOwner)
                Utils.SendMessage(GetString("SpiritualistNoticeMessage"), target.PlayerId, GetString("SpiritualistNoticeTitle"));
            else
            {
                var writer = CustomRpcSender.Create("SpiritualistSendMessage", SendOption.Reliable);
                writer.StartMessage(target.OwnerId);

                writer.StartRpc(PlayerControl.LocalPlayer.NetId, RpcCalls.SetName)
                    .Write(PlayerControl.LocalPlayer.Data.NetId)
                    .Write(GetString("SpiritualistNoticeTitle"))
                    .EndRpc();

                writer.StartRpc(PlayerControl.LocalPlayer.NetId, RpcCalls.SendChat)
                    .Write(GetString("SpiritualistNoticeMessage"))
                    .EndRpc();

                writer.StartRpc(PlayerControl.LocalPlayer.NetId, RpcCalls.SetName)
                    .Write(PlayerControl.LocalPlayer.Data.NetId)
                    .Write(PlayerControl.LocalPlayer.Data.PlayerName)
                    .EndRpc();

                writer.EndMessage();
                writer.SendMessage();

                // 素名 reset で消えた装飾名 (ホストタグ等) を flush 後に復元 (Utils.SendMessage と同型の穴)。
                Utils.ScheduleDecoratedNameRestore(PlayerControl.LocalPlayer);
            }
        }
    }

    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud = false, bool meeting = false)
    {
        if (!seer.IsAlive() || seer.PlayerId != SpiritualistId || target != null && seer.PlayerId != target.PlayerId || meeting || hud) return string.Empty;

        byte connectedId = seer.Is(CustomRoles.Madmate) ? LastDeadImpostorId : SpiritualistTarget;
        return connectedId != byte.MaxValue && ShowArrow ? Utils.ColorString(seer.GetRoleColor(), TargetArrow.GetArrows(seer, connectedId)) : string.Empty;
    }

    public static void RemoveTarget()
    {
        foreach (byte spiritualist in PlayerIdList) TargetArrow.Remove(spiritualist, SpiritualistTarget);

        SpiritualistTarget = byte.MaxValue;
    }

    private static void RemoveMadTarget()
    {
        foreach (byte spiritualist in PlayerIdList)
        {
            PlayerControl player = spiritualist.GetPlayer();
            if (player != null && player.Is(CustomRoles.Madmate))
                TargetArrow.Remove(spiritualist, LastDeadImpostorId);
        }
    }
}