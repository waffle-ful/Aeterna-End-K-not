using System;
using EndKnot.Modules;
using Hazel;

namespace EndKnot.Roles;

public class Leery : RoleBase
{
    public static bool On;

    private static OptionItem Radius;
    private static OptionItem Duration;
    private static OptionItem ShowNearestPlayerName;
    private static OptionItem ShowProgress;

    private int Count;

    private byte CurrentTarget;
    private long InvestigationEndTS;
    private byte LeeryId;

    public override bool IsEnable => On;

    public override void SetupCustomOption()
    {
        StartSetup(645750)
            .AutoSetupOption(ref Radius, 1f, new FloatValueRule(0.1f, 10f, 0.1f), OptionFormat.Multiplier)
            .AutoSetupOption(ref Duration, 15, new IntegerValueRule(1, 60, 1), OptionFormat.Seconds)
            .AutoSetupOption(ref ShowNearestPlayerName, true)
            .AutoSetupOption(ref ShowProgress, true, overrideParent: ShowNearestPlayerName);
    }

    public override void Init()
    {
        On = false;
    }

    public override void Add(byte playerId)
    {
        On = true;
        LeeryId = playerId;
        CurrentTarget = byte.MaxValue;
        InvestigationEndTS = 0;
    }

    public override void OnFixedUpdate(PlayerControl pc)
    {
        if (!GameStates.IsInTask || !Main.IntroDestroyed || ExileController.Instance || !pc.IsAlive() || Count++ < 5) return;

        Count = 0;

        if (!FastVector2.TryGetClosestPlayerInRangeTo(pc, Radius.GetFloat(), out PlayerControl nearestPlayer))
        {
            CurrentTarget = byte.MaxValue;
            InvestigationEndTS = 0;
            SendRPC();
            return;
        }

        if (nearestPlayer.PlayerId == CurrentTarget && InvestigationEndTS == 0) return;

        if (nearestPlayer.PlayerId != CurrentTarget)
        {
            if (!ShouldSwitchTarget(pc, nearestPlayer)) return;

            CurrentTarget = nearestPlayer.PlayerId;
            InvestigationEndTS = Utils.TimeStamp + Duration.GetInt();
            SendRPC();
            return;
        }

        if (Utils.TimeStamp < InvestigationEndTS) return;

        InvestigationEndTS = 0;
        SendRPC();
        if (!nearestPlayer.IsCrewmate()) pc.Notify(Translator.GetString("LeeryNotify"));
    }

    // ほぼ等距離の2人が圏内にいると最近傍判定が評価のたびに反転し、そのたびに InvestigationEndTS が
    // リセットされて調査が永久に完了しない。現ターゲットより明確に近いときだけ乗り換える。
    // マージンは Radius 設定に比例させる (固定値だと半径 0.1 と 10 のどちらかで破綻するため)。
    private bool ShouldSwitchTarget(PlayerControl pc, PlayerControl candidate)
    {
        if (CurrentTarget == byte.MaxValue) return true;

        PlayerControl current = Utils.GetPlayerById(CurrentTarget);

        // 現ターゲットが死亡・退出・ベント内・擬似死亡なら最近傍探索の対象外なので即座に乗り換える
        if (current == null || !current.IsAlive() || current.inVent || Akazukin.IsPseudoDead(CurrentTarget)) return true;

        float radius = Radius.GetFloat();
        Vector2 origin = pc.Pos();
        float distToCurrent = Vector2.Distance(origin, current.Pos());

        if (distToCurrent > radius) return true; // 現ターゲットが圏外に出たなら維持しない

        return Vector2.Distance(origin, candidate.Pos()) + (radius * 0.3f) <= distToCurrent;
    }

    private void SendRPC()
    {
        Utils.SendRPC(CustomRPC.SyncRoleData, LeeryId, CurrentTarget, InvestigationEndTS);
    }

    public void ReceiveRPC(MessageReader reader)
    {
        CurrentTarget = reader.ReadByte();
        InvestigationEndTS = long.Parse(reader.ReadString());
    }

    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud = false, bool meeting = false)
    {
        if (seer.PlayerId != LeeryId || seer.PlayerId != target.PlayerId || meeting || hud || !ShowNearestPlayerName.GetBool() || InvestigationEndTS == 0 || !seer.IsAlive()) return string.Empty;

        string text = string.Format(Translator.GetString("LeerySuffix"), CurrentTarget.ColoredPlayerName());

        if (ShowProgress.GetBool())
        {
            long now = Utils.TimeStamp;
            float percentage = (float)(InvestigationEndTS - now) / Duration.GetInt();
            text += $" {100 - (int)Math.Round(percentage * 100f)}%";
        }

        return text;
    }
}