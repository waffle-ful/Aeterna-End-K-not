using System.Collections.Generic;
using AmongUs.GameOptions;
using UnityEngine;
using static EndKnot.Translator;

namespace EndKnot.Roles;

public class Comebacker : RoleBase
{
    private const int Id = 701800;
    private static List<byte> PlayerIdList = [];

    private static OptionItem OptionCooldown;

    private byte ComebackerId;
    private Vector2? OldPosition;
    private int OldVentId;
    private string ComebackPosString;

    // マッドメイト時に記録地点への矢印を配ったインポスターと、その地点。地点が変わるたびに張り替える。
    private List<byte> MadArrowImps = [];
    private Vector3? MadArrowPos;

    public override bool IsEnable => PlayerIdList.Count > 0;

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.CrewmateRoles, CustomRoles.Comebacker);

        OptionCooldown = new FloatOptionItem(Id + 10, "Cooldown", new(0f, 180f, 0.5f), 30f, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Comebacker])
            .SetValueFormat(OptionFormat.Seconds);
    }

    public override void Init()
    {
        PlayerIdList = [];
    }

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);
        ComebackerId = playerId;
        OldPosition = null;
        OldVentId = -1;
        ComebackPosString = string.Empty;
        MadArrowImps = [];
        MadArrowPos = null;
    }

    public override void Remove(byte playerId)
    {
        PlayerIdList.Remove(playerId);
        ClearMadArrows();
    }

    public override void OnReportDeadBody()
    {
        if (Utils.GetPlayerById(ComebackerId)?.IsAlive() != true) ClearMadArrows();
    }

    private void ClearMadArrows()
    {
        if (MadArrowPos.HasValue)
        {
            foreach (byte impId in MadArrowImps)
                LocateArrow.Remove(impId, MadArrowPos.Value);
        }

        MadArrowImps.Clear();
        MadArrowPos = null;
    }

    public override void ApplyGameOptions(IGameOptions opt, byte playerId)
    {
        AURoleOptions.EngineerCooldown = OptionCooldown.GetFloat();
        AURoleOptions.EngineerInVentMaxTime = 1.5f;
    }

    public override void OnEnterVent(PlayerControl pc, Vent vent)
    {
        bool mad = pc.Is(CustomRoles.Madmate);

        if (OldPosition.HasValue)
        {
            if (mad) NotifyImpostorsOfReturn(pc, ComebackPosString);

            Vector2 tp = OldPosition.Value;
            int storedVentId = OldVentId;
            LateTask.New(() =>
            {
                // 遅延中の切断で stale player を TP すると SnapTo が NRE。
                if (!pc || pc.Data == null || pc.Data.Disconnected) return;
                pc.TP(tp + new Vector2(0f, 0.1f), log: false);
                if (pc.inVent) pc.MyPhysics?.RpcExitVent(storedVentId);
            }, 0.5f, "Comebacker.TP");
        }

        OldPosition = vent.transform.position;
        OldVentId = vent.Id;

        PlainShipRoom room = pc.GetPlainShipRoom();
        ComebackPosString = room != null ? GetString(room.RoomId.ToString()) : string.Empty;

        if (mad) ShareWaypointWithImpostors(vent.transform.position);

        Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc);
    }

    // マッドメイトのカムバッカーが記録地点へ戻ると、生存インポスター全員に知らせる。
    private void NotifyImpostorsOfReturn(PlayerControl pc, string roomName)
    {
        string msg = string.Format(GetString("ComebackerMadReturn"), ComebackerId.ColoredPlayerName(), roomName);
        foreach (PlayerControl imp in Main.EnumerateAlivePlayerControls())
        {
            if (imp.PlayerId == pc.PlayerId || !imp.Is(CustomRoleTypes.Impostor)) continue;
            imp.Notify(msg, 4f);
        }
    }

    // 記録地点を生存インポスター全員へ矢印で共有する (集合場所)。
    private void ShareWaypointWithImpostors(Vector3 pos)
    {
        ClearMadArrows();
        MadArrowPos = pos;

        foreach (PlayerControl imp in Main.EnumerateAlivePlayerControls())
        {
            if (imp.PlayerId == ComebackerId || !imp.Is(CustomRoleTypes.Impostor)) continue;
            LocateArrow.Add(imp.PlayerId, pos);
            MadArrowImps.Add(imp.PlayerId);
            Utils.NotifyRoles(SpecifySeer: imp, SpecifyTarget: imp);
        }
    }

    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud = false, bool meeting = false)
    {
        // インポスターから見た、マッドメイトのカムバッカーの記録地点
        if (MadArrowPos.HasValue && !meeting && seer.PlayerId == target.PlayerId && MadArrowImps.Contains(seer.PlayerId))
        {
            if (Utils.GetPlayerById(ComebackerId)?.IsAlive() != true) return string.Empty;
            return Utils.ColorString(Palette.ImpostorRed, LocateArrow.GetArrow(seer, MadArrowPos.Value));
        }

        if (seer.PlayerId != ComebackerId || seer.PlayerId != target.PlayerId) return string.Empty;
        if (meeting || !seer.IsAlive() || ComebackPosString == string.Empty) return string.Empty;
        return Utils.ColorString(Utils.GetRoleColor(CustomRoles.Comebacker), string.Format(GetString("ComebackerLowerText"), ComebackPosString));
    }

    public override void SetButtonTexts(HudManager hud, byte id)
    {
        hud.AbilityButton?.OverrideText(GetString("ComebackerAbility"));
    }
}
