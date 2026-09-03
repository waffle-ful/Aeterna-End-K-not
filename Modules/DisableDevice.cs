using System;
using System.Collections.Generic;
using System.Linq;
using EndKnot.Roles;
using HarmonyLib;
using Hazel;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using static EndKnot.Translator;

namespace EndKnot;

// Reference: https://github.com/ykundesu/SuperNewRoles/blob/master/SuperNewRoles/Mode/SuperHostRoles/BlockTool.cs
internal static class DisableDevice
{
    public static readonly List<byte> DesyncComms = [];
    private static int frame;

    // デバイス使用時間制限。カテゴリ index: 0=Admin, 1=Camera, 2=DoorLog, 3=Vital
    private static readonly string[] TimeLimitModes = ["DeviceTimeLimitMode.PerGame", "DeviceTimeLimitMode.PerRound"];
    private static OptionItem EnableDeviceTimeLimit;
    private static OptionItem DeviceTimeLimitMode;
    private static OptionItem AdminTimeLimit;
    private static OptionItem CameraTimeLimit;
    private static OptionItem DoorLogTimeLimit;
    private static OptionItem VitalTimeLimit;
    private static OptionItem DeviceTimeLimitNotifyOnExhausted;

    private static readonly Dictionary<byte, float[]> DeviceTimeUsed = [];
    private static readonly HashSet<(byte PlayerId, int Category)> NotifiedExhausted = [];

    public static bool TimeLimitEnabled => EnableDeviceTimeLimit != null && EnableDeviceTimeLimit.GetBool();

    public static void SetupTimeLimitOptions()
    {
        new TextOptionItem(110110, "MenuTitle.DeviceTimeLimit", TabGroup.GameSettings)
            .SetColor(new Color32(255, 153, 153, byte.MaxValue))
            .SetHeader(true);

        EnableDeviceTimeLimit = new BooleanOptionItem(960130, "EnableDeviceTimeLimit", false, TabGroup.GameSettings)
            .SetColor(new Color32(255, 153, 153, byte.MaxValue));

        DeviceTimeLimitMode = new StringOptionItem(960131, "DeviceTimeLimitMode", TimeLimitModes, 0, TabGroup.GameSettings)
            .SetParent(EnableDeviceTimeLimit)
            .SetColor(new Color32(255, 153, 153, byte.MaxValue));

        AdminTimeLimit = new FloatOptionItem(960132, "AdminTimeLimit", new(0f, 300f, 5f), 60f, TabGroup.GameSettings)
            .SetParent(EnableDeviceTimeLimit)
            .SetValueFormat(OptionFormat.Seconds)
            .SetColor(new Color32(255, 153, 153, byte.MaxValue));

        CameraTimeLimit = new FloatOptionItem(960133, "CameraTimeLimit", new(0f, 300f, 5f), 60f, TabGroup.GameSettings)
            .SetParent(EnableDeviceTimeLimit)
            .SetValueFormat(OptionFormat.Seconds)
            .SetColor(new Color32(255, 153, 153, byte.MaxValue));

        DoorLogTimeLimit = new FloatOptionItem(960134, "DoorLogTimeLimit", new(0f, 300f, 5f), 60f, TabGroup.GameSettings)
            .SetParent(EnableDeviceTimeLimit)
            .SetValueFormat(OptionFormat.Seconds)
            .SetColor(new Color32(255, 153, 153, byte.MaxValue));

        VitalTimeLimit = new FloatOptionItem(960135, "VitalTimeLimit", new(0f, 300f, 5f), 60f, TabGroup.GameSettings)
            .SetParent(EnableDeviceTimeLimit)
            .SetValueFormat(OptionFormat.Seconds)
            .SetColor(new Color32(255, 153, 153, byte.MaxValue));

        DeviceTimeLimitNotifyOnExhausted = new BooleanOptionItem(960136, "DeviceTimeLimitNotifyOnExhausted", true, TabGroup.GameSettings)
            .SetParent(EnableDeviceTimeLimit)
            .SetColor(new Color32(255, 153, 153, byte.MaxValue));
    }

    // 試合開始時 (ShipStatus.Start) に呼ぶ。全クライアントで自分のぶんだけ持っているローカル集計なので、常に無条件で消してよい。
    public static void ResetTimeLimitForNewGame()
    {
        DeviceTimeUsed.Clear();
        NotifiedExhausted.Clear();
    }

    // 会議明け (ExileControllerWrapUpPatch.WrapUpFinalizer) に呼ぶ。PerRound モードの時だけ消す。
    public static void ResetTimeLimitForRound()
    {
        if (DeviceTimeLimitMode == null || DeviceTimeLimitMode.GetInt() != 1) return;
        DeviceTimeUsed.Clear();
        NotifiedExhausted.Clear();
    }

    // 範囲内に居た時間を加算し、上限を超えていれば true (=comms を強制的に偽装させる)
    private static bool ApplyTimeLimit(PlayerControl pc, int category, float elapsed)
    {
        float limit = category switch
        {
            0 => AdminTimeLimit.GetFloat(),
            1 => CameraTimeLimit.GetFloat(),
            2 => DoorLogTimeLimit.GetFloat(),
            _ => VitalTimeLimit.GetFloat()
        };
        if (limit <= 0f) return false; // 0 = 無制限

        if (!DeviceTimeUsed.TryGetValue(pc.PlayerId, out float[] used))
        {
            used = new float[4];
            DeviceTimeUsed[pc.PlayerId] = used;
        }

        if (used[category] < limit) used[category] += elapsed;

        if (used[category] < limit) return false;

        NotifyExhausted(pc, category);
        return true;
    }

    private static void NotifyExhausted(PlayerControl pc, int category)
    {
        if (DeviceTimeLimitNotifyOnExhausted == null || !DeviceTimeLimitNotifyOnExhausted.GetBool()) return;
        if (!NotifiedExhausted.Add((pc.PlayerId, category))) return;

        string deviceName = GetString(category switch
        {
            0 => "DeviceTimeLimit.Admin",
            1 => "DeviceTimeLimit.Camera",
            2 => "DeviceTimeLimit.DoorLog",
            _ => "DeviceTimeLimit.Vital"
        });

        Utils.SendMessage(string.Format(GetString("DeviceTimeLimit.Exhausted"), deviceName), pc.PlayerId, importance: MessageImportance.Low);
    }

    public static readonly Dictionary<string, Vector2> DevicePos = new()
    {
        ["SkeldAdmin"] = new(3.48f, -8.62f),
        ["SkeldCamera"] = new(-13.06f, -2.45f),
        ["MiraHQAdmin"] = new(21.02f, 19.09f),
        ["MiraHQDoorLog"] = new(16.22f, 5.82f),
        ["PolusLeftAdmin"] = new(22.80f, -21.52f),
        ["PolusRightAdmin"] = new(24.66f, -21.52f),
        ["PolusCamera"] = new(2.96f, -12.74f),
        ["PolusVital"] = new(26.70f, -15.94f),
        ["DleksAdmin"] = new(-3.48f, -8.62f),
        ["DleksCamera"] = new(13.06f, -2.45f),
        ["AirshipCockpitAdmin"] = new(-22.32f, 0.91f),
        ["AirshipRecordsAdmin"] = new(19.89f, 12.60f),
        ["AirshipCamera"] = new(8.10f, -9.63f),
        ["AirshipVital"] = new(25.24f, -7.94f),
        ["FungleCamera"] = new(6.20f, 0.10f),
        ["FungleVital"] = new(-2.50f, -9.80f),
        ["SubmergedVital"] = new(5f, 32.54f),
        ["SubmergedLeftAdmin"] = new(-9.45f, 10.16f),
        ["SubmergedRightAdmin"] = new(-7.07f, 10.16f),
        ["SubmergedCamera"] = new(-3.41f, -34.56f)
    };

    public static bool DoDisable => Options.DisableDevices.GetBool();

    public static float UsableDistance => Main.CurrentMap switch
    {
        MapNames.Skeld => 1.8f,
        MapNames.MiraHQ => 2.4f,
        MapNames.Polus => 1.8f,
        MapNames.Dleks => 1.5f,
        MapNames.Airship => 1.8f,
        MapNames.Fungle => 1.8f,
        _ => 2f
    };

    public static void FixedUpdate()
    {
        frame = frame == 3 ? 0 : ++frame;
        if (frame != 0) return;

        bool rogueForce = false;
        if (Rogue.On)
            foreach (var ps in Main.PlayerStates.Values)
            {
                var role = ps.Role;
                if (role is Rogue { DisableDevices: true })
                {
                    rogueForce = true;
                    break;
                }
            }

        if (!DoDisable && !rogueForce && !TimeLimitEnabled) return;

        foreach (PlayerControl pc in Main.EnumeratePlayerControls())
        {
            if (pc.IsModdedClient()) continue;

            try
            {
                var doComms = false;
                var mapId = Main.NormalOptions.MapId;
                Vector2 PlayerPos = pc.Pos();

                bool ignore = (Options.DisableDevicesIgnoreImpostors.GetBool() && pc.Is(CustomRoleTypes.Impostor)) ||
                              (Options.DisableDevicesIgnoreNeutrals.GetBool() && pc.Is(CustomRoleTypes.Neutral)) ||
                              (Options.DisableDevicesIgnoreCrewmates.GetBool() && pc.Is(CustomRoleTypes.Crewmate)) ||
                              (Options.DisableDevicesIgnoreAfterAnyoneDied.GetBool() && GameStates.AlreadyDied);

                ignore &= !rogueForce;

                if (pc.IsAlive() && !Utils.IsActive(SystemTypes.Comms))
                {
                    switch (mapId)
                    {
                        case 0:
                            if (Options.DisableSkeldAdmin.GetBool() || rogueForce) doComms |= FastVector2.DistanceWithinRange(PlayerPos, DevicePos["SkeldAdmin"], UsableDistance);
                            if (Options.DisableSkeldCamera.GetBool() || rogueForce) doComms |= FastVector2.DistanceWithinRange(PlayerPos, DevicePos["SkeldCamera"], UsableDistance);
                            break;
                        case 1:
                            if (Options.DisableMiraHQAdmin.GetBool() || rogueForce) doComms |= FastVector2.DistanceWithinRange(PlayerPos, DevicePos["MiraHQAdmin"], UsableDistance);
                            if (Options.DisableMiraHQDoorLog.GetBool() || rogueForce) doComms |= FastVector2.DistanceWithinRange(PlayerPos, DevicePos["MiraHQDoorLog"], UsableDistance);
                            break;
                        case 2:
                            if (Options.DisablePolusAdmin.GetBool() || rogueForce)
                            {
                                doComms |= FastVector2.DistanceWithinRange(PlayerPos, DevicePos["PolusLeftAdmin"], UsableDistance);
                                doComms |= FastVector2.DistanceWithinRange(PlayerPos, DevicePos["PolusRightAdmin"], UsableDistance);
                            }

                            if (Options.DisablePolusCamera.GetBool() || rogueForce) doComms |= FastVector2.DistanceWithinRange(PlayerPos, DevicePos["PolusCamera"], UsableDistance);
                            if (Options.DisablePolusVital.GetBool() || rogueForce) doComms |= FastVector2.DistanceWithinRange(PlayerPos, DevicePos["PolusVital"], UsableDistance);
                            break;
                        case 3:
                            if (Options.DisableSkeldAdmin.GetBool() || rogueForce) doComms |= FastVector2.DistanceWithinRange(PlayerPos, DevicePos["DleksAdmin"], UsableDistance);
                            if (Options.DisableSkeldCamera.GetBool() || rogueForce) doComms |= FastVector2.DistanceWithinRange(PlayerPos, DevicePos["DleksCamera"], UsableDistance);
                            break;
                        case 4:
                            if (Options.DisableAirshipCockpitAdmin.GetBool() || rogueForce) doComms |= FastVector2.DistanceWithinRange(PlayerPos, DevicePos["AirshipCockpitAdmin"], UsableDistance);
                            if (Options.DisableAirshipRecordsAdmin.GetBool() || rogueForce) doComms |= FastVector2.DistanceWithinRange(PlayerPos, DevicePos["AirshipRecordsAdmin"], UsableDistance);
                            if (Options.DisableAirshipCamera.GetBool() || rogueForce) doComms |= FastVector2.DistanceWithinRange(PlayerPos, DevicePos["AirshipCamera"], UsableDistance);
                            if (Options.DisableAirshipVital.GetBool() || rogueForce) doComms |= FastVector2.DistanceWithinRange(PlayerPos, DevicePos["AirshipVital"], UsableDistance);
                            break;
                        case 5:
                            if (Options.DisableFungleCamera.GetBool() || rogueForce) doComms |= FastVector2.DistanceWithinRange(PlayerPos, DevicePos["FungleCamera"], UsableDistance);
                            if (Options.DisableFungleVital.GetBool() || rogueForce) doComms |= FastVector2.DistanceWithinRange(PlayerPos, DevicePos["FungleVital"], UsableDistance);
                            break;
                    }

                    // 完全禁止 (DisableXXX) が OFF の装置でも時間制限だけは独立して効く必要があるので、
                    // 上のスイッチとは別に距離判定をやり直す。frame スロットルで 4 tick に 1 回しか
                    // 通らないため、経過時間は間引き分をまとめて加算する。除外対象 (ignore) は
                    // 時間を消費させない — 消費/通知の判定は ignore の確定より後に置く。
                    if (TimeLimitEnabled && !ignore)
                    {
                        int category = mapId switch
                        {
                            0 => FastVector2.DistanceWithinRange(PlayerPos, DevicePos["SkeldAdmin"], UsableDistance) ? 0
                                : FastVector2.DistanceWithinRange(PlayerPos, DevicePos["SkeldCamera"], UsableDistance) ? 1
                                : -1,
                            1 => FastVector2.DistanceWithinRange(PlayerPos, DevicePos["MiraHQAdmin"], UsableDistance) ? 0
                                : FastVector2.DistanceWithinRange(PlayerPos, DevicePos["MiraHQDoorLog"], UsableDistance) ? 2
                                : -1,
                            2 => FastVector2.DistanceWithinRange(PlayerPos, DevicePos["PolusLeftAdmin"], UsableDistance) || FastVector2.DistanceWithinRange(PlayerPos, DevicePos["PolusRightAdmin"], UsableDistance) ? 0
                                : FastVector2.DistanceWithinRange(PlayerPos, DevicePos["PolusCamera"], UsableDistance) ? 1
                                : FastVector2.DistanceWithinRange(PlayerPos, DevicePos["PolusVital"], UsableDistance) ? 3
                                : -1,
                            3 => FastVector2.DistanceWithinRange(PlayerPos, DevicePos["DleksAdmin"], UsableDistance) ? 0
                                : FastVector2.DistanceWithinRange(PlayerPos, DevicePos["DleksCamera"], UsableDistance) ? 1
                                : -1,
                            4 => FastVector2.DistanceWithinRange(PlayerPos, DevicePos["AirshipCockpitAdmin"], UsableDistance) || FastVector2.DistanceWithinRange(PlayerPos, DevicePos["AirshipRecordsAdmin"], UsableDistance) ? 0
                                : FastVector2.DistanceWithinRange(PlayerPos, DevicePos["AirshipCamera"], UsableDistance) ? 1
                                : FastVector2.DistanceWithinRange(PlayerPos, DevicePos["AirshipVital"], UsableDistance) ? 3
                                : -1,
                            5 => FastVector2.DistanceWithinRange(PlayerPos, DevicePos["FungleCamera"], UsableDistance) ? 1
                                : FastVector2.DistanceWithinRange(PlayerPos, DevicePos["FungleVital"], UsableDistance) ? 3
                                : -1,
                            _ => -1
                        };

                        if (category >= 0 && ApplyTimeLimit(pc, category, Time.fixedDeltaTime * 4f)) doComms = true;
                    }
                }

                doComms &= !ignore;

                var hasValue = false;
                var activateSabComms = false;

                if (doComms && !pc.inVent)
                {
                    if (!DesyncComms.Contains(pc.PlayerId)) DesyncComms.Add(pc.PlayerId);
                    hasValue = true;
                    activateSabComms = true;
                }
                else if (!Utils.IsActive(SystemTypes.Comms) && DesyncComms.Contains(pc.PlayerId))
                {
                    DesyncComms.Remove(pc.PlayerId);
                    hasValue = true;
                }

                if (!hasValue) continue;

                var sender = CustomRpcSender.Create("DisableDevice.FixedUpdate", SendOption.Reliable, log: false);

                if (activateSabComms)
                    sender.RpcDesyncUpdateSystem(pc, SystemTypes.Comms, 128);
                else
                {
                    sender.RpcDesyncUpdateSystem(pc, SystemTypes.Comms, 16);

                    if (mapId is 1 or 5) // Mira HQ or The Fungle
                        sender.RpcDesyncUpdateSystem(pc, SystemTypes.Comms, 17);
                }

                sender.SendMessage();
            }
            catch (Exception ex) { Logger.Exception(ex, "DisableDevice"); }
        }
    }
}

[HarmonyPatch(typeof(ShipStatus), nameof(ShipStatus.Start))]
public class RemoveDisableDevicesPatch
{
    public static void Postfix()
    {
        DisableDevice.ResetTimeLimitForNewGame();

        bool rogueForce = Rogue.On && Main.PlayerStates.Values.Any(x => x.Role is Rogue { DisableDevices: true });
        if (!Options.DisableDevices.GetBool() && !rogueForce) return;

        UpdateDisableDevices();
    }

    public static void UpdateDisableDevices()
    {
        PlayerControl player = PlayerControl.LocalPlayer;
        bool rogueForce = Rogue.On && Main.PlayerStates.Values.Any(x => x.Role is Rogue { DisableDevices: true });

        bool ignore = player.Is(CustomRoles.GM) ||
                      !player.IsAlive() ||
                      (Options.DisableDevicesIgnoreImpostors.GetBool() && player.Is(CustomRoleTypes.Impostor)) ||
                      (Options.DisableDevicesIgnoreNeutrals.GetBool() && player.Is(CustomRoleTypes.Neutral)) ||
                      (Options.DisableDevicesIgnoreCrewmates.GetBool() && player.Is(CustomRoleTypes.Crewmate)) ||
                      (Options.DisableDevicesIgnoreAfterAnyoneDied.GetBool() && GameStates.AlreadyDied);

        ignore &= !rogueForce;
        Il2CppArrayBase<MapConsole> admins = Object.FindObjectsOfType<MapConsole>(true);
        Il2CppArrayBase<SystemConsole> consoles = Object.FindObjectsOfType<SystemConsole>(true);
        if (admins == null || consoles == null) return;

        switch (Main.NormalOptions.MapId)
        {
            case 3:
            case 0:
                if (Options.DisableSkeldAdmin.GetBool() || rogueForce) admins[0].gameObject.GetComponent<CircleCollider2D>().enabled = ignore;
                if (Options.DisableSkeldCamera.GetBool() || rogueForce) consoles.DoIf(x => x.name == "SurvConsole", x => x.gameObject.GetComponent<PolygonCollider2D>().enabled = ignore);
                break;
            case 1:
                if (Options.DisableMiraHQAdmin.GetBool() || rogueForce) admins[0].gameObject.GetComponent<CircleCollider2D>().enabled = ignore;
                if (Options.DisableMiraHQDoorLog.GetBool() || rogueForce) consoles.DoIf(x => x.name == "SurvLogConsole", x => x.gameObject.GetComponent<BoxCollider2D>().enabled = ignore);

                break;
            case 2:
                if (Options.DisablePolusAdmin.GetBool() || rogueForce) admins.Do(x => x.gameObject.GetComponent<BoxCollider2D>().enabled = ignore);
                if (Options.DisablePolusCamera.GetBool() || rogueForce) consoles.DoIf(x => x.name == "Surv_Panel", x => x.gameObject.GetComponent<BoxCollider2D>().enabled = ignore);
                if (Options.DisablePolusVital.GetBool() || rogueForce) consoles.DoIf(x => x.name == "panel_vitals", x => x.gameObject.GetComponent<BoxCollider2D>().enabled = ignore);
                break;
            case 4:
                admins.Do(x =>
                {
                    if ((Options.DisableAirshipCockpitAdmin.GetBool() && x.name == "panel_cockpit_map") ||
                        (Options.DisableAirshipRecordsAdmin.GetBool() && x.name == "records_admin_map") || rogueForce)
                        x.gameObject.GetComponent<BoxCollider2D>().enabled = ignore;
                });

                if (Options.DisableAirshipCamera.GetBool() || rogueForce) consoles.DoIf(x => x.name == "task_cams", x => x.gameObject.GetComponent<BoxCollider2D>().enabled = ignore);
                if (Options.DisableAirshipVital.GetBool() || rogueForce) consoles.DoIf(x => x.name == "panel_vitals", x => x.gameObject.GetComponent<CircleCollider2D>().enabled = ignore);
                break;
            case 5:
                if (Options.DisableFungleCamera.GetBool() || rogueForce) consoles.DoIf(x => x.name == "BinocularsSecurityConsole", x => x.gameObject.GetComponent<PolygonCollider2D>().enabled = ignore);
                if (Options.DisableFungleVital.GetBool() || rogueForce) consoles.DoIf(x => x.name == "VitalsConsole", x => x.gameObject.GetComponent<BoxCollider2D>().enabled = ignore);
                break;
        }
    }
}
