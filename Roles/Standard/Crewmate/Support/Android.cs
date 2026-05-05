using System.Collections.Generic;
using AmongUs.GameOptions;
using UnityEngine;

namespace EndKnot.Roles;

public class Android : RoleBase
{
    private const int Id = 701600;
    private static List<byte> PlayerIdList = [];

    public static OptionItem CoolTime;
    public static OptionItem InVentTime;
    public static OptionItem TaskAddBattery;
    public static OptionItem RemoveBattery;
    public static OptionItem DrainAmount;
    public static OptionItem RemoveTime;
    public static OptionItem VentDrainMultiplier;
    public static OptionItem AutoRecharge;
    public static OptionItem AutoRechargeAmount;
    public static OptionItem AutoRechargeInterval;
    public static OptionItem AutoRechargeStationary;

    private byte AndroidId;
    private float Battery;
    private float removetimer;
    private float rechargetimer;
    private int NowVent;
    private bool WasSabotageActive;
    private bool allTasksDone;
    private float addbattery;
    private float maxcooltime;
    private bool optRemoveBattery;
    private float optRemove;
    private float optRemoveTime;
    private float optVentMultiplier;
    private bool optAutoRecharge;
    private float optRechargeAmount;
    private float optRechargeInterval;
    private bool optRechargeStationary;
    private Vector2 lastPosition;

    public override bool IsEnable => PlayerIdList.Count > 0;

    public override void SetupCustomOption()
    {
        Options.SetupRoleOptions(Id, TabGroup.CrewmateRoles, CustomRoles.Android);
        CoolTime = new FloatOptionItem(Id + 10, "Cooldown", new(0f, 180f, 0.5f), 20f, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Android])
            .SetValueFormat(OptionFormat.Seconds);
        InVentTime = new FloatOptionItem(Id + 11, "AndroidInVentTime", new(1f, 60f, 0.5f), 7.5f, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Android])
            .SetValueFormat(OptionFormat.Seconds);
        RemoveBattery = new BooleanOptionItem(Id + 13, "AndroidRemoveBattery", true, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Android]);
        DrainAmount = new FloatOptionItem(Id + 14, "AndroidRemove", new(1f, 100f, 0.1f), 7.5f, TabGroup.CrewmateRoles)
            .SetParent(RemoveBattery)
            .SetValueFormat(OptionFormat.Percent);
        RemoveTime = new FloatOptionItem(Id + 15, "AndroidRemoveTime", new(1f, 180f, 0.5f), 4.0f, TabGroup.CrewmateRoles)
            .SetParent(RemoveBattery)
            .SetValueFormat(OptionFormat.Seconds);
        VentDrainMultiplier = new FloatOptionItem(Id + 16, "AndroidVentDrainMultiplier", new(1f, 10f, 0.5f), 3f, TabGroup.CrewmateRoles)
            .SetParent(RemoveBattery)
            .SetValueFormat(OptionFormat.Multiplier);
        TaskAddBattery = new FloatOptionItem(Id + 12, "AndroidAddTaskBattery", new(1f, 100f, 1f), 10f, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Android])
            .SetValueFormat(OptionFormat.Percent);
        AutoRecharge = new BooleanOptionItem(Id + 17, "AndroidAutoRecharge", true, TabGroup.CrewmateRoles)
            .SetParent(Options.CustomRoleSpawnChances[CustomRoles.Android]);
        AutoRechargeAmount = new FloatOptionItem(Id + 18, "AndroidAutoRechargeAmount", new(1f, 100f, 1f), 5f, TabGroup.CrewmateRoles)
            .SetParent(AutoRecharge)
            .SetValueFormat(OptionFormat.Percent);
        AutoRechargeInterval = new FloatOptionItem(Id + 19, "AndroidAutoRechargeInterval", new(1f, 180f, 0.5f), 10f, TabGroup.CrewmateRoles)
            .SetParent(AutoRecharge)
            .SetValueFormat(OptionFormat.Seconds);
        AutoRechargeStationary = new BooleanOptionItem(Id + 20, "AndroidAutoRechargeStationary", false, TabGroup.CrewmateRoles)
            .SetParent(AutoRecharge);
    }

    public override void Init() => PlayerIdList = [];

    public override void Add(byte playerId)
    {
        PlayerIdList.Add(playerId);
        AndroidId = playerId;
        Battery = 0f;
        removetimer = 0f;
        rechargetimer = 0f;
        NowVent = -1;
        WasSabotageActive = false;
        allTasksDone = false;
        addbattery = TaskAddBattery.GetFloat() * 0.01f;
        maxcooltime = CoolTime.GetFloat();
        optRemoveBattery = RemoveBattery.GetBool();
        optRemove = DrainAmount.GetFloat();
        optRemoveTime = RemoveTime.GetFloat();
        optVentMultiplier = VentDrainMultiplier.GetFloat();
        optAutoRecharge = AutoRecharge.GetBool();
        optRechargeAmount = AutoRechargeAmount.GetFloat() * 0.01f;
        optRechargeInterval = AutoRechargeInterval.GetFloat();
        optRechargeStationary = AutoRechargeStationary.GetBool();
        lastPosition = Vector2.zero;
    }

    public override void Remove(byte playerId) => PlayerIdList.Remove(playerId);

    public override void ApplyGameOptions(IGameOptions opt, byte playerId)
    {
        float inMax = Battery * InVentTime.GetFloat();
        if (inMax <= 1f) inMax = 1f;

        AURoleOptions.EngineerCooldown = Battery == 0 ? 200f : (maxcooltime * 3) - (Battery * maxcooltime * 2);
        AURoleOptions.EngineerInVentMaxTime = Battery == 0 ? 1f : inMax;
    }

    public override bool CanUseVent(PlayerControl pc, int ventId)
    {
        if (pc == null) return true;
        if (pc.PlayerId != AndroidId) return true;
        return !Utils.IsAnySabotageActive();
    }

    public override void OnEnterVent(PlayerControl pc, Vent vent)
    {
        NowVent = vent.Id;
    }

    public override void OnExitVent(PlayerControl pc, Vent vent)
    {
        NowVent = -1;
        pc.SyncSettings();
        pc.RpcResetAbilityCooldown();
    }

    public override void OnTaskComplete(PlayerControl pc, int completedTaskCount, int totalTaskCount)
    {
        float lastBatt = Battery;
        Battery += addbattery;
        if (Battery > 1f) Battery = 1f;

        if (completedTaskCount + 1 >= totalTaskCount)
            allTasksDone = true;

        pc.SyncSettings();

        if (lastBatt <= 0f)
            pc.RpcResetAbilityCooldown();

        Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc);
    }

    public override void OnFixedUpdate(PlayerControl pc)
    {
        if (!AmongUsClient.Instance.AmHost || !pc.IsAlive()) return;
        if (GameStates.IsLobby) return;

        bool isSabotage = Utils.IsAnySabotageActive();

        if (isSabotage && !WasSabotageActive && pc.inVent && NowVent >= 0)
        {
            if (!Amnesia.IsAbilityBlocked(pc))
            {
                pc.MyPhysics.RpcBootFromVent(NowVent);
                NowVent = -1;
            }
        }

        if (!isSabotage && WasSabotageActive)
            pc.RpcResetAbilityCooldown();

        WasSabotageActive = isSabotage;

        if (Battery > 1f) Battery = 1f;

        Vector2 currentPos = pc.GetTruePosition();

        if (!GameStates.IsMeeting)
        {
            if (Battery > 0f && optRemoveBattery)
            {
                removetimer += Time.fixedDeltaTime;
                if (removetimer >= optRemoveTime)
                {
                    float drain = (pc.inVent && NowVent >= 0) ? optRemove * optVentMultiplier : optRemove;
                    Battery -= drain * 0.01f;
                    removetimer = 0f;
                    if (Battery < 0f) Battery = 0f;

                    if (Battery <= 0f && pc.inVent && NowVent >= 0)
                    {
                        pc.MyPhysics.RpcBootFromVent(NowVent);
                        NowVent = -1;
                    }

                    Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc);
                    pc.SyncSettings();
                }
            }

            if (optAutoRecharge && allTasksDone && Battery < 1f)
            {
                bool canRecharge = !optRechargeStationary || (currentPos - lastPosition).sqrMagnitude < 0.001f;
                if (canRecharge)
                {
                    rechargetimer += Time.fixedDeltaTime;
                    if (rechargetimer >= optRechargeInterval)
                    {
                        Battery += optRechargeAmount;
                        if (Battery > 1f) Battery = 1f;
                        rechargetimer = 0f;
                        Utils.NotifyRoles(SpecifySeer: pc, SpecifyTarget: pc);
                        pc.SyncSettings();
                    }
                }
                else
                {
                    rechargetimer = 0f;
                }
            }
        }

        lastPosition = currentPos;
    }

    public override string GetSuffix(PlayerControl seer, PlayerControl target, bool hud = false, bool meeting = false)
    {
        if (seer.PlayerId != target.PlayerId || meeting) return string.Empty;
        if (seer.PlayerId != AndroidId) return string.Empty;
        return "<u>" + GetNowBattery() + "</u>";
    }

    private string GetNowBattery()
    {
        float battery = Battery * 100;
        if (battery <= 0) return Utils.ColorString(Color.gray, "|");
        if (battery <= 5) return Utils.ColorString(new Color32(0xd9, 0x53, 0x27, 0xff), "||");
        if (battery <= 10) return Utils.ColorString(new Color32(0xd9, 0x6e, 0x27, 0xff), "|||");
        if (battery <= 20) return Utils.ColorString(new Color32(0xd9, 0xb8, 0x27, 0xff), "||||");
        if (battery <= 30) return Utils.ColorString(new Color32(0xd6, 0xd9, 0x27, 0xff), "|||||");
        if (battery <= 40) return Utils.ColorString(new Color32(0xb8, 0xd1, 0x3b, 0xff), "||||||");
        if (battery <= 50) return Utils.ColorString(new Color32(0xa7, 0xba, 0x47, 0xff), "|||||||");
        if (battery <= 60) return Utils.ColorString(new Color32(0x96, 0xba, 0x47, 0xff), "||||||||");
        if (battery <= 70) return Utils.ColorString(new Color32(0x84, 0xba, 0x47, 0xff), "|||||||||");
        if (battery <= 80) return Utils.ColorString(new Color32(0x75, 0xba, 0x47, 0xff), "||||||||||");
        if (battery <= 90) return Utils.ColorString(new Color32(0x3f, 0xb8, 0x1d, 0xff), "|||||||||||");
        return Utils.ColorString(new Color32(0x03, 0xff, 0x4a, 0xff), "||||||||||||");
    }
}
