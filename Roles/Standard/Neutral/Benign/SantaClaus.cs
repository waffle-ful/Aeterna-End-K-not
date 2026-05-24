using System.Collections.Generic;
using System.Linq;
using AmongUs.GameOptions;
using EndKnot.Modules;
using UnityEngine;
using static EndKnot.Options;
using static EndKnot.Translator;

namespace EndKnot.Roles;

public class SantaClaus : RoleBase
{
    private const int Id = 704600;
    public static bool On;
    public static List<SantaClaus> Instances = [];

    private static OptionItem WinGivePresentCountOpt;
    private static OptionItem MaxHavePresentOpt;
    private static OptionItem AddWinModeOpt;
    private static OptionItem GiftAddonOpt;

    private static readonly CustomRoles[] GiftAddons =
    [
        CustomRoles.Clumsy,
        CustomRoles.Tired,
        CustomRoles.Lazy,
        CustomRoles.Asthmatic,
        CustomRoles.Disco,
        CustomRoles.Mischievous,
        CustomRoles.Oblivious,
        CustomRoles.Sleep,
        CustomRoles.Sunglasses,
        CustomRoles.Tiebreaker,
        CustomRoles.Unlucky,
        CustomRoles.Youtuber
    ];

    private byte SantaId = byte.MaxValue;
    private int HavePresent;
    private int GiftPresent;
    private int MeetingGift;
    private int? EntotuVentId;
    private Vector3? EntotuVentPos;
    private string RoomName = string.Empty;
    private string MeetingMemo = string.Empty;
    private readonly List<byte> GiftedPlayers = [];
    public bool IsWon;

    public override bool IsEnable => On;

    public override void SetupCustomOption()
    {
        StartSetup(Id)
            .AutoSetupOption(ref WinGivePresentCountOpt, 4, new IntegerValueRule(1, 30, 1))
            .AutoSetupOption(ref MaxHavePresentOpt, 2, new IntegerValueRule(1, 15, 1))
            .AutoSetupOption(ref AddWinModeOpt, false)
            .AutoSetupOption(ref GiftAddonOpt, true)
            .CreateOverrideTasksData();
    }

    public override void Init()
    {
        On = false;
        Instances = [];
    }

    public override void Add(byte playerId)
    {
        On = true;
        Instances.Add(this);
        SantaId = playerId;
        HavePresent = 0;
        GiftPresent = 0;
        MeetingGift = 0;
        EntotuVentId = null;
        EntotuVentPos = null;
        RoomName = string.Empty;
        MeetingMemo = string.Empty;
        GiftedPlayers.Clear();
        IsWon = false;
        SetPresentVent();
    }

    public override void Remove(byte playerId)
    {
        Instances.RemoveAll(x => x.SantaId == playerId);
        if (Instances.Count == 0) On = false;
        if (EntotuVentPos.HasValue)
            LocateArrow.Remove(SantaId, EntotuVentPos.Value);
    }

    public override bool CanUseKillButton(PlayerControl pc) => false;
    public override bool CanUseSabotage(PlayerControl pc) => false;

    public override void ApplyGameOptions(IGameOptions opt, byte id)
    {
        AURoleOptions.EngineerCooldown = 1.1f;
        AURoleOptions.EngineerInVentMaxTime = 1f;
    }

    public override void OnTaskComplete(PlayerControl pc, int completedTaskCount, int totalTaskCount)
    {
        if (pc.PlayerId != SantaId) return;
        if (!pc.IsAlive()) return;
        if (HavePresent >= MaxHavePresentOpt.GetInt()) return;

        HavePresent++;
        Logger.Info($"SantaClaus task complete: HavePresent={HavePresent}/{MaxHavePresentOpt.GetInt()}", "SantaClaus");
        Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc);
    }

    public override void OnEnterVent(PlayerControl pc, Vent vent)
    {
        if (pc.PlayerId != SantaId) return;
        if (!pc.IsAlive()) return;
        if (EntotuVentId == null || vent.Id != EntotuVentId.Value) return;
        if (HavePresent <= 0 || EntotuVentPos == null) return;

        HavePresent--;
        pc.RpcGuardAndKill(pc); // 配達演出: 一瞬の無敵フラッシュ
        GiftPresent++;
        MeetingGift++;
        Logger.Info($"SantaClaus delivered to vent {vent.Id} ({RoomName}): HavePresent={HavePresent}, GiftPresent={GiftPresent}/{WinGivePresentCountOpt.GetInt()}, MeetingGift={MeetingGift}", "SantaClaus");
        EntotuVentId = null;

        // 部屋名を確定（記録用）
        var roomNameAtDelivery = RoomName;

        if (EntotuVentPos.HasValue)
            LocateArrow.Remove(SantaId, EntotuVentPos.Value);

        // 勝利チェック
        if (GiftPresent >= WinGivePresentCountOpt.GetInt())
        {
            if (!AddWinModeOpt.GetBool())
            {
                CustomWinnerHolder.ResetAndSetWinner(CustomWinner.SantaClaus);
                CustomWinnerHolder.WinnerIds.Add(SantaId);
                return;
            }

            IsWon = true;
        }

        SetPresentVent();
        Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc);
        // 配達した部屋の記録は会議メッセージ用に保持（MeetingNotifyRoom 相当）
        DeliveryRooms.Add(roomNameAtDelivery);
    }

    private readonly List<string> DeliveryRooms = [];

    public override void OnReportDeadBody()
    {
        MeetingMemo = string.Empty;

        PlayerControl pc = SantaId.GetPlayer();
        if (pc == null || !pc.IsAlive()) return;
        if (MeetingGift <= 0 || DeliveryRooms.Count == 0) return;

        var sb = new System.Text.StringBuilder();
        int idx = 0;
        while (MeetingGift > 0 && idx < DeliveryRooms.Count)
        {
            string room = DeliveryRooms[idx++];
            int chance = IRandom.Instance.Next(0, 20);
            int msgNum = 0;
            if (chance > 18) msgNum = 2;
            else if (chance > 15) msgNum = 1;

            string msg = string.Format(GetString($"SantaClausMeetingMsg{msgNum}"), room);
            if (sb.Length > 0) sb.Append('\n');
            sb.Append($"<size=60%><color=#e05050>{msg}</color></size>");

            if (GiftAddonOpt.GetBool())
                GiftPresentToRandom();

            MeetingGift--;
        }

        MeetingGift = 0;
        DeliveryRooms.Clear();
        MeetingMemo = sb.ToString();

        if (!string.IsNullOrEmpty(MeetingMemo))
            Utils.SendMessage(MeetingMemo, SantaId);
    }

    private void GiftPresentToRandom()
    {
        var candidates = Main.AllAlivePlayerControlsToList
            .Where(p => p.PlayerId != SantaId && !GiftedPlayers.Contains(p.PlayerId))
            .ToList();
        if (candidates.Count == 0) return;

        var target = candidates[IRandom.Instance.Next(candidates.Count)];
        if (target == null) return;

        var availableAddons = GiftAddons.Where(addon => !target.Is(addon)).ToList();
        if (availableAddons.Count == 0)
        {
            GiftedPlayers.Add(target.PlayerId);
            return;
        }

        var giftRole = availableAddons[IRandom.Instance.Next(availableAddons.Count)];
        target.RpcSetCustomRole(giftRole);
        GiftedPlayers.Add(target.PlayerId);
        Logger.Info($"SantaClaus gift: {target.GetRealName()} <= {giftRole}", "SantaClaus");

        LateTask.New(() =>
        {
            Utils.SendMessage(string.Format(GetString("SantaGiftAddonMessage"), GetString(giftRole.ToString())), target.PlayerId);
        }, 5f, "SantaGiftMessage");
    }

    private void SetPresentVent()
    {
        if (!AmongUsClient.Instance.AmHost) return;
        if (ShipStatus.Instance == null) return;

        var allVents = ShipStatus.Instance.AllVents.ToArray();
        if (allVents.Length == 0) return;

        var vent = allVents[IRandom.Instance.Next(allVents.Length)];
        EntotuVentId = vent.Id;
        EntotuVentPos = new Vector3(vent.transform.position.x, vent.transform.position.y);

        LocateArrow.Add(SantaId, EntotuVentPos.Value);
        var room = ((Vector2)EntotuVentPos.Value).GetPlainShipRoom();
        RoomName = room != null ? GetString(room.RoomId.ToString()) : "?";
    }

    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud = false, bool meeting = false)
    {
        if (meeting) return string.Empty;
        if (seer.PlayerId != SantaId) return string.Empty;
        if (target != null && target.PlayerId != seer.PlayerId) return string.Empty;
        if (!seer.IsAlive()) return string.Empty;
        if (EntotuVentPos == null) return string.Empty;

        string size = hud ? string.Empty : "<size=60%>";
        string arrow = LocateArrow.GetArrow(seer, EntotuVentPos.Value);

        if (HavePresent >= MaxHavePresentOpt.GetInt())
            return $"{size}<color=#e05050>{GetString("SantaClausDeliver")} {arrow}({RoomName})</color>";

        return $"{size}<color=#e05050>{GetString("SantaClausCollect")} <size=60%>{arrow}({RoomName})</size></color>";
    }

    public override string GetProgressText(byte playerId, bool comms)
    {
        if (playerId != SantaId) return string.Empty;
        return $"({HavePresent}) <color=#e05050>({GiftPresent}/{WinGivePresentCountOpt.GetInt()})</color>";
    }

    public override void SetButtonTexts(HudManager hud, byte id)
    {
        hud.AbilityButton?.OverrideText(GetString("SantaClausAbilityText"));
    }
}
