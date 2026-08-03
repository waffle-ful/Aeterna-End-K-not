using System;
using System.Collections.Generic;
using EndKnot.Modules;
using EndKnot.Modules.Extensions;
using Hazel;
using UnityEngine;

namespace EndKnot.Roles;

public class Bouncer : RoleBase
{
    public static bool On;

    public static OptionItem AbilityCooldown;
    private static OptionItem AbilityDuration;
    private static OptionItem AbortBouncingIfLeftRoom;
    private static OptionItem WhoGetsBounced;
    private static OptionItem AbilityUseLimit;
    private static OptionItem AbilityUseGainWithEachTaskCompleted;
    private static OptionItem AbilityChargesWhenFinishedTasks;

    private static readonly string[] WhoGetsBouncedOptions =
    [
        "Bouncer.WhoGetsBouncedOptions.Everyone",
        "Bouncer.WhoGetsBouncedOptions.Impostors",
        "Bouncer.WhoGetsBouncedOptions.ImpNKNPNE"
    ];

    public override bool IsEnable => On;

    private Dictionary<byte, Vector2> LastPosition = [];
    private CountdownTimer Timer;
    private PlainShipRoom MarkedRoom;
    private byte BouncerId;

    public override void SetupCustomOption()
    {
        StartSetup(706000)
            .AutoSetupOption(ref AbilityCooldown, 15, new IntegerValueRule(0, 120, 1), OptionFormat.Seconds)
            .AutoSetupOption(ref AbilityDuration, 15, new IntegerValueRule(1, 60, 1), OptionFormat.Seconds)
            .AutoSetupOption(ref AbortBouncingIfLeftRoom, true)
            .AutoSetupOption(ref WhoGetsBounced, 0, WhoGetsBouncedOptions)
            .AutoSetupOption(ref AbilityUseLimit, 1f, new FloatValueRule(0, 20, 0.05f), OptionFormat.Times)
            .AutoSetupOption(ref AbilityUseGainWithEachTaskCompleted, 0.5f, new FloatValueRule(0f, 5f, 0.05f), OptionFormat.Times)
            .AutoSetupOption(ref AbilityChargesWhenFinishedTasks, 0.2f, new FloatValueRule(0f, 5f, 0.05f), OptionFormat.Times);
    }

    public override void Init()
    {
        On = false;
    }

    public override void Add(byte playerId)
    {
        On = true;
        BouncerId = playerId;
        LastPosition = [];
        Timer = null;
        MarkedRoom = null;
        playerId.SetAbilityUseLimit(AbilityUseLimit.GetFloat());
    }

    public override void OnPet(PlayerControl pc)
    {
        var room = pc.GetPlainShipRoom();
        if (!room || pc.GetAbilityUseLimit() < 1f) return;

        MarkedRoom = room;
        Timer = new CountdownTimer(AbilityDuration.GetFloat(), () =>
        {
            Timer = null;
            LastPosition = [];
            if (!pc || !pc.IsAlive()) return;
            Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc);
        }, onTick: () =>
        {
            if (Timer.Remaining.TotalSeconds >= 6 || !pc || !pc.IsAlive()) return;
            Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc);
        }, onCanceled: () =>
        {
            Timer = null;
            LastPosition = [];
        });
        pc.RpcRemoveAbilityUse(notify: false);
        Utils.SendRPC(CustomRPC.SyncRoleData, BouncerId, false);
        Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc);
    }

    // per-player の minInterval は1人あたりの頻度制御であって合計消費を制御しない —
    // 複数人が同時にマーク部屋へ押し寄せると N人×5回/秒 で SnapTo cap (80/100) を食い尽くす。
    // 全員共有の秒間トークンで合計を制限する (Blockade.cs と同じ3点セット)。
    private const int BouncesPerSecond = 10;
    private float BounceWindowStart;
    private int BounceCount;

    public override void OnCheckPlayerPosition(PlayerControl pc)
    {
        if (Timer == null) return;

        if (pc.PlayerId == BouncerId)
        {
            if (AbortBouncingIfLeftRoom.GetBool() && !pc.IsInRoom(MarkedRoom))
            {
                Timer.Dispose();
                Timer = null;
                LastPosition = [];
                Utils.SendRPC(CustomRPC.SyncRoleData, BouncerId, true);
            }

            return;
        }

        if (!LastPosition.TryGetValue(pc.PlayerId, out Vector2 lastPosition))
        {
            if (pc.IsInRoom(MarkedRoom)) return;
            LastPosition[pc.PlayerId] = pc.transform.position;
        }
        else if (pc.IsInRoom(MarkedRoom))
        {
            if (WhoGetsBounced.GetValue() switch
            {
                0 => true,
                1 => pc.Is(Team.Impostor),
                2 => pc.Is(Team.Impostor) || pc.IsNeutralKiller() || pc.IsNeutralPariah() || pc.IsNeutralEvil(),
                _ => false
            })
            {
                if (Time.time - BounceWindowStart >= 1f)
                {
                    BounceWindowStart = Time.time;
                    BounceCount = 0;
                }

                if (BounceCount >= BouncesPerSecond) return;
                if (pc.TP(lastPosition, minInterval: 0.2f)) BounceCount++;
            }
        }
        else
            LastPosition[pc.PlayerId] = pc.transform.position;
    }

    public void ReceiveRPC(MessageReader reader)
    {
        Timer = reader.ReadBoolean() ? null : new CountdownTimer(AbilityDuration.GetFloat(), () => Timer = null, onCanceled: () => Timer = null);
    }

    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud = false, bool meeting = false)
    {
        if (seer.PlayerId != BouncerId || seer.PlayerId != target.PlayerId || (seer.IsModdedClient() && !hud) || meeting || Timer == null) return string.Empty;
        int remainingSeconds = (int)Math.Ceiling(Timer.Remaining.TotalSeconds);
        string timerText = seer.IsModdedClient() || remainingSeconds <= 5 ? string.Format(Translator.GetString("Bouncer.Suffix.RemainingSeconds"), remainingSeconds) : string.Empty;
        return string.Format(Translator.GetString("Bouncer.Suffix"), Translator.GetString(MarkedRoom.RoomId)) + timerText;
    }
}
